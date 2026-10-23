// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;

namespace GeZi.Core.Download
{
    /// <summary>
    /// 自动调度：按照「总连接预算」把 <c>单文件并发</c> 与 <c>同时任务数</c> 配对，
    /// 让用户不用自己猜那两个数。
    ///
    /// <para>【2026-10-24 新增｜用户需求】「一般设置几个同时下载数，下载速度更能跑满呢，
    /// 你可以添加一个自动调度的选项，然后自动计算」。</para>
    ///
    /// <para>【为什么这两个数难调】速度 ≈ <c>单文件并发 × 同时任务数 × 单连接速度</c>，
    /// 而第二三项都有天花板：</para>
    /// <list type="bullet">
    /// <item><b>总连接数</b>受账号配额限制（夸克实测 512 为最优总量）。
    ///       用户 7 个文件各开 256 路 = 1792 条，超出后服务端降速/挂起，
    ///       实测表现就是「244/256 连接却只跑 1~2 MB/s」。</item>
    /// <item><b>单连接速度</b>由 CDN 决定（常见 200 KB/s ~ 1 MB/s）。
    ///       开再多连接也不会让单条变快，只会让它们互相抢带宽。</item>
    /// </list>
    ///
    /// <para>【算法思路】在 <c>files × threads × concurrent ≤ budget</c> 的约束下，
    /// 选一个"能跑满带宽且开销最小"的组合。经验规则（按优先级）：</para>
    /// <list type="number">
    /// <item>任务数不超过 <see cref="MaxConcurrent"/>（太多任务 = 磁盘随机写、进度条乱跳）。</item>
    /// <item>任务数不超过文件数（没有那么多活可派）。</item>
    /// <item>单文件并发不低于 <see cref="MinThreads"/>（太低则单文件跑不满）。</item>
    /// <item>在满足上面三条的前提下，**尽量把任务数开大、单文件并发开小** ——
    ///       因为"多任务并行"比"单任务超多连接"更容易跑满总带宽
    ///       （前者是多条独立 TCP 流，后者在同一条流上抢）。</item>
    /// </list>
    ///
    /// <para>纯数学，不碰 UI 也不碰网络 —— 便于单独验证。</para>
    /// </summary>
    public static class AutoTuner
    {
        /// <summary>
        /// 总连接预算。与 <see cref="ThreadAllocator.DefaultBudget"/> 同源。
        ///
        /// <para><b>【512 是怎么来的｜原版实测记录，2026-10-02，真实直链，4 轮】</b></para>
        /// <code>
        /// 1024 档：23.20 MB/s，单连接 23.2 KB/s，出现 AggregateException / 任务未结束
        ///  512 档：22.33 MB/s，单连接 44.7 KB/s，稳定
        /// </code>
        /// <para>结论：1024 只快约 <b>5%</b>，但单连接速度<b>减半</b>（配额被摊薄，已近天花板），
        /// 且异常率更高 —— 收益不显著、连接数翻倍（路由器 NAT 压力 + 风控风险），
        /// 故原版<b>维持 512</b>。</para>
        ///
        /// <para><b>⚠️ 512 不是"速度最优"，而是"还能正常工作的经验上限"</b>：
        /// 原版注释明确写了两条 ——</para>
        /// <list type="bullet">
        /// <item>「这是"还能正常工作"的经验上限；再往上（1024+）收益趋近于零，
        ///       而风险（连接超时风暴、整网卡死）急剧上升」</item>
        /// <item>「多数家宽在 <b>64~128 路</b>就已跑满，继续加只是让首字节更慢、
        ///       更容易触发服务端限流」</item>
        /// </list>
        ///
        /// <para><b>所以"更多不一定更快"</b>：真正的瓶颈是「账号总连接配额」与
        /// 「单连接被 CDN 限速」。用户实测印证了两端 ——
        /// 1792 条连接（远超配额）只有几百 KB/s；而 510 条时能跑到 4~6 MB/s。</para>
        /// </summary>
        public const int Budget = ThreadAllocator.DefaultBudget;   // 512

        /// <summary>单文件并发下限：低于这个值，单个大文件很难跑满。</summary>
        public const int MinThreads = 16;

        /// <summary>单文件并发上限（受核心层实现上限约束）。</summary>
        public const int MaxThreads = SegmentedDownloader.MaxRealConcurrency;

        /// <summary>
        /// 自动调度推荐的单文件并发上限 = 256。
        ///
        /// <para>依据（原版注释 + 实测）：</para>
        /// <list type="bullet">
        /// <item>原版记着「<b>512 比 256 慢</b>」这个现象 —— 连接太多时每条流都在慢启动、
        ///       互相争抢带宽，拥塞窗口涨不起来（现存版本用 <c>SlowStartGate</c> 缓解，
        ///       但"分批放行"只是让情况不变坏，并不会让 512 反超 256）。</item>
        /// <item>1024 档实测仅比 512 快 5%，而单连接速度减半 —— 说明配额已近天花板，
        ///       继续加只是把同一份带宽切得更碎。</item>
        /// <item>单文件开到 256 路时，单连接配额已被摊到 ~44 KB/s 量级；
        ///       再翻倍只会更碎，不会更快。</item>
        /// </list>
        /// <para>所以自动调度把 256 当作"够用的天花板"，超出预算的部分交给
        /// <b>多任务并行</b>（多条独立 TCP 流比单文件超多连接更容易跑满总带宽）。</para>
        /// </summary>
        public const int RecommendedMaxThreads = 256;

        /// <summary>
        /// 同时任务数上限。
        /// 为什么是 8：超过 8 个任务时，<c>预算/任务数</c> 会让每个任务分到 &lt;64 路，
        /// 而单个大文件想跑满通常需要 32~64 路；再往上加只会让每个任务都跑不快。
        /// 另外任务多 = 磁盘随机写更重、进度条更跳、失败重试更乱。
        /// </summary>
        public const int MaxConcurrent = 8;

        /// <summary>同时任务数下限。</summary>
        public const int MinConcurrent = 1;

        /// <summary>自动调度的结果。</summary>
        public sealed class Plan
        {
            /// <summary>单文件并发连接数。</summary>
            public int Threads { get; set; }

            /// <summary>同时下载任务数。</summary>
            public int Concurrent { get; set; }

            /// <summary>计划占用的总连接数（Threads × Concurrent）。</summary>
            public int TotalConnections { get { return Threads * Concurrent; } }

            /// <summary>给用户看的一句话解释（为什么是这个组合）。</summary>
            public string Reason { get; set; }
        }

        /// <summary>
        /// 算一组推荐值。
        /// </summary>
        /// <param name="fileCount">
        /// 这一批要下几个文件。<b>传 0 或负数表示未知</b>（例如用户只是打开设置页，
        /// 还没选文件）—— 此时给一个"通用最优"组合。
        /// </param>
        /// <param name="budget">总连接预算，默认 <see cref="Budget"/>。</param>
        public static Plan Compute(int fileCount, int budget = Budget)
        {
            if (budget < 1) budget = 1;

            // 文件数未知：给一个"不含偏见"的通用解。
            //
            // 【2026-10-24 修】原来这里返回 64×4=256，用户实测看到
            // 「当前：单文件 64 路 × 同时 5 个 ≈ 320 条连接（预算 512）」就疑惑
            // 「不是 512 左右吗？为什么没有多跑几个任务」。两个问题：
            //   ① 256 只用了预算一半，看起来"没用满"；
            //   ② 与 Settings 里的旧值（5 个）不一致，显示自相矛盾。
            // 现在改成**用满预算**的通用解：64 路 × 8 个 = 512。
            // 选 64 而不是更大：64 是原版默认值，实测能跑满多数家宽；
            // 剩下的额度交给"同时任务数"——多流并行比单文件超多连接更容易跑满。
            // 真正开下载时会按实际文件数再算一次（见 MainWindow 的 AutoTuneForBatch）。
            if (fileCount <= 0)
            {
                int defThreads = 64;
                if (defThreads > budget / MaxConcurrent) defThreads = Math.Max(MinThreads, budget / MaxConcurrent);
                defThreads = Clamp(defThreads, MinThreads, RecommendedMaxThreads);
                int defConc = Clamp(budget / Math.Max(1, defThreads), MinConcurrent, MaxConcurrent);
                return new Plan
                {
                    Threads = defThreads,
                    Concurrent = defConc,
                    Reason = "按通用最优值：单文件 " + defThreads + " 路 × 同时 " + defConc
                        + " 个 ≈ " + (defThreads * defConc) + " 条连接（预算 " + budget + "）。",
                };
            }

            // ---- 有明确文件数：在约束下搜索最优组合 ----
            //
            // 目标函数：优先让"总连接数接近预算"（跑满带宽），
            // 其次让"任务数尽量大"（多流并行优于单流超多连接），
            // 再其次让"单文件并发不浪费"（超过 128 路收益递减明显）。
            int maxConc = Math.Min(MaxConcurrent, fileCount);

            int bestThreads = MinThreads;
            int bestConc = MinConcurrent;
            double bestScore = double.MinValue;

            for (int conc = MinConcurrent; conc <= maxConc; conc++)
            {
                // 该任务数下，每个任务能分到的连接数
                int perTask = budget / conc;
                if (perTask < MinThreads) continue;          // 分不到下限，这个 conc 太激进
                // 夹到 [MinThreads, RecommendedMaxThreads]：
                // 上限用 256 而不是 512 —— 原版实测「512 不一定比 256 快」，
                // 单文件超过 256 路后收益递减明显，不如把额度让给多任务并行。
                int threads = Math.Min(perTask, RecommendedMaxThreads);

                // 单文件并发超过 128 后收益递减（原版注释：512 不一定比 256 快）。
                // 超出部分算"浪费"——它占着预算却不带来等比例的速度。
                int usefulThreads = Math.Min(threads, 128);

                // 用满预算的程度（0~1）
                double usage = (double)(threads * conc) / budget;
                if (usage > 1.0) usage = 1.0;

                // 打分：
                //   · 预算利用率 权重最大（这是"跑满带宽"的直接指标）
                //   · 任务数越多越好（多流并行），但边际递减，用 sqrt 抑制
                //   · 单文件并发浪费（threads > 128 的部分）扣分
                double waste = threads > usefulThreads
                    ? (double)(threads - usefulThreads) / Math.Max(1, threads)
                    : 0.0;

                double score = usage * 100.0
                             + Math.Sqrt(conc) * 8.0
                             - waste * 25.0;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestThreads = threads;
                    bestConc = conc;
                }
            }

            bestThreads = Clamp(bestThreads, MinThreads, RecommendedMaxThreads);
            bestConc = Clamp(bestConc, MinConcurrent, maxConc);

            string reason;
            if (fileCount < bestConc)
                reason = "这批只有 " + fileCount + " 个文件，按 " + bestConc + " 个并行没必要 —— 已按文件数收敛。";
            else if (bestThreads >= RecommendedMaxThreads)
                reason = "单文件并发按推荐上限 " + bestThreads + " 路（再高收益递减），"
                    + "同时 " + bestConc + " 个任务，合计 " + (bestThreads * bestConc) + " 条连接。";
            else
                reason = "预算 " + budget + " ÷ " + bestConc + " 个任务 = 每个 " + bestThreads
                    + " 路，合计 " + (bestThreads * bestConc) + " 条连接。";

            return new Plan
            {
                Threads = bestThreads,
                Concurrent = bestConc,
                Reason = reason,
            };
        }

        private static int Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}
