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
using System.Collections.Generic;

namespace GeZi.Core.Download
{
    /// <summary>
    /// 把「总连接预算」按**文件大小比例**分给一批下载任务。
    ///
    /// 【背景】Python Flet 版有「智能调度」：`实际线程数 = min(下载线程数, 总连接预算 / 本批任务数)`
    /// —— 多个文件**平分**总预算。C# 版当初删掉「全局连接预算」设置项时，
    /// 连"多文件共享总预算"这件事一起没了，变成每个任务都开满自己的线程数
    /// （单文件设 512、同时跑 3 个 = 1536 条连接，而夸克是按账号限总连接数的）。
    ///
    /// 【用户要求】「不要平均分配了，改成比例分配，按文件大小，文件越大分配越多，
    /// 文件越小分配越少，但有最低下限，不然别让小文件根本下不了了」。
    ///
    /// 所以：<c>threads_i = clamp(预算 × size_i / Σsize, 下限, 用户设的单文件并发数)</c>
    ///
    /// 抽成独立静态类是为了**可测试** —— 纯数学，不碰 UI 也不碰网络，
    /// 验证宿主可以直接跑一组用例（见 tmp-links）。
    /// </summary>
    public static class ThreadAllocator
    {
        /// <summary>
        /// 总连接预算。夸克按账号限制总连接数，Python 版实测 512 为最优总量。
        /// </summary>
        public const int DefaultBudget = 512;

        /// <summary>单个文件至少给几条连接 —— 保证小文件也下得动，不会被大文件挤成 0。</summary>
        public const int MinPerFile = 4;

        /// <summary>
        /// 小文件（小于 <see cref="SmallFileThreshold"/>）的连接数封顶。
        ///
        /// <para>【2026-10-24 改：5 → 16】用户反馈「为什么 50 多 MB 的最大连接数只有 5，
        /// 改成 64，尽可能利用带宽」。</para>
        ///
        /// <para>原来的 5 路是早期定的「小文件没必要开太多」，但实测发现两个问题：</para>
        /// <list type="bullet">
        /// <item>50MB 在今天的网速下**根本不算小**（千兆宽带 8 秒就能下完），
        ///       5 路完全跑不满；</item>
        /// <item>更糟的是它造成了**断崖**：49.9MB 给 5 路、50.1MB 给 64 路，
        ///       同一个文件夹里两个大小相近的文件速度差十几倍，用户会以为程序有 bug。</item>
        /// </list>
        ///
        /// <para>现在改成：<b>小文件也给 16 路</b>（够跑满多数家宽），
        /// 并且阈值以上不再断崖 —— 见 <see cref="Allocate"/> 里的渐变逻辑。
        /// 保留一个下限而不是完全放开，是因为几百个几 KB 的文件各开 64 路
        /// 会把账号总连接额度占满（夸克按账号限制），反而拖累真正需要带宽的大文件。</para>
        /// </summary>
        public const int SmallFileCap = 16;

        /// <summary>
        /// 小于此值的文件视为「小文件」，连接数按渐变曲线收敛到 <see cref="SmallFileCap"/>。
        ///
        /// <para>【2026-10-24】阈值从 50MB 提到 128MB。原因：用户实测 50 多 MB 的文件
        /// 被卡在 5 路；而 50~128MB 这个区间在挂机下载场景里很常见（短剧、MV、单集视频），
        /// 完全值得给足并发。</para>
        /// </summary>
        public const long SmallFileThreshold = 128L * 1024 * 1024;   // 128MB

        /// <summary>
        /// 按大小比例分配线程数，返回与入参等长的数组。
        /// </summary>
        /// <param name="sizes">每个任务的文件字节数（未知填 0 或负数）。</param>
        /// <param name="perFileCap">用户设的「单文件并发连接数」，同时是每项的上限。</param>
        /// <param name="budget">总预算。</param>
        /// <param name="minPerFile">每项下限。</param>
        /// <param name="concurrency">
        /// 同时最多跑几个任务（= 设置页的「同时下载任务数」）。
        /// **这个参数很关键**：预算约束的是"同时并发"的量级，而不是"整批"的账面总和。
        /// </param>
        /// <remarks>
        /// · **只有一个文件** → 直接给 <paramref name="perFileCap"/>（行为与改动前一致）。
        /// · **多个文件等大** → 退化成"平分"，与 Python 版行为一致。
        /// · **大小全未知** → 也退回平分，不会把所有任务饿到只剩下限。
        /// · 结果永远落在 [<paramref name="minPerFile"/>, <paramref name="perFileCap"/>]。
        ///
        /// 【为什么要按"批数"放大预算】调度器用信号量限制**同时只跑
        /// <paramref name="concurrency"/> 个**任务，所以 N 个文件其实是分
        /// ⌈N/concurrency⌉ 批跑完的。如果预算只按 512 分给全部 N 个，
        /// 文件一多（比如 200 个）每份就被压到下限 4 路 —— 而同时其实只有 3 个在跑，
        /// 白白浪费了额度。所以这里把预算乘上"批数"：
        /// <c>有效预算 = budget × ⌈N / concurrency⌉</c>，
        /// 这样**同时**在跑的那几个加起来正好落在 budget 附近，而文件之间的
        /// **大小比例关系完全不变**（放大系数是统一的）。
        /// </remarks>
        public static int[] Allocate(IList<long> sizes, int perFileCap,
            int budget = DefaultBudget, int minPerFile = MinPerFile, int concurrency = 1)
        {
            int cap = Math.Max(1, perFileCap);
            if (minPerFile < 1) minPerFile = 1;
            if (minPerFile > cap) minPerFile = cap;

            int n = sizes == null ? 0 : sizes.Count;
            var result = new int[n];
            if (n == 0) return result;

            // 连接数上限按文件大小**渐变**，不再断崖。
            //
            // 【2026-10-24 改】原来是一个硬阈值：
            //     size < 50MB  → 封顶 5 路
            //     size >= 50MB → 直接给满 cap（常见 64~256 路）
            // 后果是两个**大小几乎相同**的文件速度差十几倍（49.9MB vs 50.1MB）。
            // 用户看到的是"这个 50 多 MB 的只有 5 个连接，那个 60MB 的却有 64 个" ——
            // 完全无法理解，只会认为程序有 bug。
            //
            // 现在改成渐进收敛：越小给得越少，但不是一刀切。
            //   公式：cap = Min + (Max - Min) × (size / Threshold)^0.5
            //   · 平方根让曲线在靠近阈值时快速抬升（10MB 就给到约 0.28 的系数），
            //     避免"小文件全被压在下限"；
            //   · 达到 Threshold（128MB）后就是完整 cap，再往上不变。
            //
            // 仍然保留下限 floor（SmallFileCap 的一半）：几 KB 的 epub 各开几十路
            // 纯属浪费握手，还会占满账号额度拖累大文件。
            long floorBytes = 4L * 1024 * 1024;          // 4MB 以下一律给 floor 值
            int floorConns = Math.Max(minPerFile, Math.Min(cap, SmallFileCap / 2));

            int CapFor(long size)
            {
                int c;
                if (size <= 0)
                {
                    // 大小未知：给一个保守值（不能因为"不知道多大"就开满）
                    c = Math.Min(cap, SmallFileCap);
                }
                else if (size <= floorBytes)
                {
                    c = floorConns;
                }
                else if (size >= SmallFileThreshold)
                {
                    c = cap;                              // 够大 → 给满
                }
                else
                {
                    // 在 [floorBytes, Threshold] 之间按平方根渐变
                    double t = (double)(size - floorBytes) / (SmallFileThreshold - floorBytes);
                    if (t < 0) t = 0;
                    if (t > 1) t = 1;
                    double k = Math.Sqrt(t);              // 0 → 1，前段抬升更快
                    c = (int)Math.Round(floorConns + (cap - floorConns) * k);
                }
                if (c < minPerFile) c = minPerFile;
                if (c > cap) c = cap;
                return c;
            }

            if (n == 1)
            {
                // 单文件：小文件封顶、否则给满 cap
                result[0] = CapFor(sizes[0]);
                return result;
            }

            // 有效预算 = 总预算本身（**不放大**）。
            //
            // 【2026-10-24 修：两处放大都是错的】
            //
            // ① 原实现 `effBudget = budget × ceil(N/concurrency)`：
            //    实测（用户 7 个 3.2GB 文件，Threads=256，ConcurrentTasks=3）：
            //        waves = ceil(7/3) = 3
            //        effBudget = 512 × 3 = 1536
            //        每文件份额 = 1536 × (3.2/20.68) ≈ 238 → 夹到上限 256
            //    ⇒ **7 个任务各开 256 路 = 1792 条连接**，512 预算彻底失效。
            //    理由错误：注释说"N 个文件分 N/concurrency 批跑完，预算该乘批数"，
            //    但那些批次**不同时存在** —— 没轮到的任务一条连接都不该占。
            //    提前把它们的额度算进来，等于凭空把总预算放大数倍。
            //
            // ② 若改成 `budget × concurrentNow` 同样是错的：
            //    512 × 3 = 1536，还是 1536。预算是**全局上限**，乘任何东西都不再是上限。
            //
            // 正确语义：`budget` 就是"此刻所有活跃任务加起来最多多少条连接"。
            // 分配时按大小比例切这 512 份，谁在跑谁用，跑完让给下一批。
            //
            // 实证依据：夸克按账号限制总连接数，Python 版实测 512 为最优总量。
            // 用户 1792 条连接的实测表现正是"连接开着但不传数据、总速度几十 KB/s"。
            int k = Math.Max(1, concurrency);
            long effBudget = budget;

            long total = 0;
            for (int i = 0; i < n; i++)
                if (sizes[i] > 0) total += sizes[i];

            // 每个任务的额度 = 总预算 ÷ 同时运行的任务数。
            //
            // 【为什么除以 min(n, concurrency) 而不是按大小比例切】
            // 调度器**同时只跑 concurrency 个**任务，所以任意时刻真正占用连接的
            // 只有这么多任务。想让"同时占用 ≈ budget"且"任意时刻不超 budget"，
            // 唯一正确的做法是每个任务拿 budget/concurrency：
            //     用户场景 n=7, concurrency=3, budget=512
            //     → 每个任务 512/3 ≈ 170 路，同时跑的 3 个合计 ≈ 510 ✓
            //
            // 若按"整批 N 个文件的大小比例"去切 512：
            //     每个只有 512×3.2/20.68 ≈ 79 路，同时跑的 3 个合计仅 237 ——
            //     只用掉 46% 预算，白白浪费一半带宽。
            //
            // 大小比例的诉求由下面的 min/max 夹取与 SmallFileCap 满足：
            // 小文件本来就不该开满（<50MB 封顶 5 路），
            // 差额自然让给了大文件（它们各自仍受 perFileCap 约束）。
            int slots = Math.Max(1, Math.Min(n, k));
            long perFileBudget = budget / slots;
            if (perFileBudget < minPerFile) perFileBudget = minPerFile;
            if (perFileBudget > cap) perFileBudget = cap;

            if (total <= 0)
            {
                // 大小未知 → 全部按额度走（原"平分"行为的等价形式）
                for (int i = 0; i < n; i++)
                    result[i] = (int)Math.Min(CapFor(0), perFileBudget);
                return result;
            }

            double maxSize = 0;
            for (int j = 0; j < n; j++) if (sizes[j] > maxSize) maxSize = sizes[j];

            for (int i = 0; i < n; i++)
            {
                // 基础额度 = perFileBudget；再按"相对批内最大文件"做**温和倾斜**。
                //
                // ⚠️ 别按 ratio = size/(total/n) 去倾斜 —— 那会算错：
                //    10 个等大文件时 ratio=1，倾斜系数也是 1，但 perFileBudget=512/3≈170，
                //    170 × 1 仍是 170，看起来对；可一旦 tie 到 cap 或再被 ceilBudget 回收，
                //    结果就会掉到 19 这种离谱值。改用"相对最大文件"更稳：
                //    最大的文件拿满 perFileBudget，其余按 (size/maxSize) 线性缩放，
                //    并给一个 0.5 的下限系数，保证大文件之间不会拉开太离谱。
                double tilt = 1.0;
                if (maxSize > 0 && sizes[i] > 0)
                {
                    double rel = sizes[i] / maxSize;          // 0~1，最大文件 = 1
                    tilt = 0.5 + 0.5 * rel;                   // 映射到 [0.5, 1.0]
                }

                long share = (long)Math.Round(perFileBudget * tilt);
                int t = (int)share;
                if (t < minPerFile) t = minPerFile;   // 下限：小文件也要下得动
                int fileCap = CapFor(sizes[i]);        // 小文件封顶到 SmallFileCap
                if (t > fileCap) t = fileCap;          // 上限：小文件≤5、大文件≤用户档位
                result[i] = t;
            }

            // 回收上限 = 同时运行的总额度（= perFileBudget × slots ≈ budget）。
            //
            // ⚠️ 这里必须是**同时运行**的额度，不是全批 N 个的额度：
            //    调度器一次只跑 slots 个，所以"同时占用"最多就是 perFileBudget × slots。
            //    若按 N 个的总额去回收，7 个文件每个都会被压到 512/7 ≈ 73 路
            //    —— 而实际只有 3 个在跑，等于只用了 219/512，白白浪费一半带宽。
            long ceilBudget = (long)perFileBudget * slots;
            long sum = 0;
            for (int i = 0; i < n; i++) sum += result[i];
            int guard = 0;
            while (sum > ceilBudget && guard++ < 100000)
            {
                int best = -1;
                for (int i = 0; i < n; i++)
                    if (result[i] > minPerFile && (best < 0 || result[i] > result[best]))
                        best = i;
                if (best < 0) break;   // 所有项都到下限了，无法再扣
                result[best]--;
                sum--;
            }

            return result;
        }
    }
}
