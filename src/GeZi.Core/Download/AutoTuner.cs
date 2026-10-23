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
        /// <summary>总连接预算。与 <see cref="ThreadAllocator.DefaultBudget"/> 同源。</summary>
        public const int Budget = ThreadAllocator.DefaultBudget;   // 512

        /// <summary>单文件并发下限：低于这个值，单个大文件很难跑满。</summary>
        public const int MinThreads = 16;

        /// <summary>单文件并发上限（受核心层实现上限约束）。</summary>
        public const int MaxThreads = SegmentedDownloader.MaxRealConcurrency;

        /// <summary>
        /// 自动调度推荐的单文件并发上限。
        ///
        /// <para>为什么不直接给到 <see cref="MaxThreads"/>(512)：
        /// 原版注释里记着实测结论 —— 「512 不一定比 256 快」，
        /// 因为连接数过高时每条流都在慢启动、互相争抢，拥塞窗口涨不起来。
        /// 用户实测也印证：256 路时速度已经很接近上限。
        /// 所以自动调度把 256 当作"够用的天花板"，超出部分交给多任务并行。</para>
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

            // 文件数未知：给通用最优解。
            // 为什么是 64 路 × 4 个 = 256？这是"保守但够用"的中间值：
            //   · 64 路是原版默认值，实测能跑满多数家宽；
            //   · 4 个任务并行既能让总量上去，又不会让磁盘随机写成为瓶颈；
            //   · 256 只用了 512 预算的一半，给"用户临时点多几个文件"留了余量。
            if (fileCount <= 0)
            {
                int defThreads = Math.Min(64, budget / 4);
                defThreads = Clamp(defThreads, MinThreads, MaxThreads);
                int defConc = Math.Max(MinConcurrent,
                    Math.Min(MaxConcurrent, Math.Min(4, budget / Math.Max(1, defThreads))));
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
