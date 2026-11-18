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
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.Core.Download
{
    /// <summary>
    /// 下载**完整性校验**（2026-11-17 新增，方案见仓库根 GeZi-下载完整性校验方案.md）。
    ///
    /// <para>【要解决什么】真实案例：`gameplayscene_greenhell` 217MB，文件大小、
    /// 文件头全部正常，**尾部 17.9MB 全是 0x00**（占 8.24%），游戏一加载就崩。
    /// 原有收尾校验只比对长度（`finalLen != total`），而补零损坏的特征恰恰是
    /// 「长度完全正确、内容错了」—— 现有防线对它完全无感。</para>
    ///
    /// <para>【三层防线】
    /// <list type="number">
    /// <item><b>防线一 · 分片校验</b>（合并前）：分片写满、置 partDone 之前扫一遍零游程，
    ///   可疑就**重下该片对比**——两次内容一致 ⇒ 源数据本来如此，放行（零误报）。
    ///   这是性价比核心：坏片在进入目标文件之前就被拦下，重下代价只是一个分片。</item>
    /// <item><b>防线二 · 尾零扫描</b>（合并后）：从文件尾向前找最后一个非零字节，
    ///   尾零 ≥1MB 就报警。&lt;0.5s，只读尾部 ≤64MB。</item>
    /// <item><b>防线三 · zip 逐条目 CRC32</b>（.zip 自动启用）：net48 的
    ///   <see cref="ZipArchiveEntry"/> 流读到底时会自行核对 CRC 并抛
    ///   <see cref="InvalidDataException"/> —— 「读到底」就是校验本身。**零误报**。</item>
    /// </list></para>
    ///
    /// <para>【设计原则】本类是**纯检测器**：无网络、无状态、可单测。
    /// 「重下对比」那种需要 HTTP 基建的动作留在 <see cref="SegmentedDownloader"/> 里。</para>
    /// </summary>
    public static class IntegrityChecker
    {
        // ==================== 校验等级 ====================

        /// <summary>校验等级。</summary>
        public enum VerifyLevel
        {
            /// <summary>关闭（不推荐）。</summary>
            Off = 0,
            /// <summary>防线一 + 防线二（默认）。</summary>
            Fast = 1,
            /// <summary>fast + 全文件零洞扫描（读全文件，防"中间被注入零段"）。</summary>
            Strict = 2,
        }

        // ==================== 可调阈值（与 Settings 对接） ====================

        /// <summary>当前校验等级（由 MainWindow 从 Settings 同步）。</summary>
        public static VerifyLevel Level = VerifyLevel.Fast;

        /// <summary>.zip 是否做逐条目 CRC 校验（fast 及以上生效）。</summary>
        public static bool VerifyZip = true;

        /// <summary>连续零段达到这个长度即可疑（1MB）。</summary>
        public static long ZeroRunThreshold = 1024 * 1024;

        /// <summary>零占比判据（防"分散碎零"绕过游程判据）。</summary>
        public static double ZeroRatioThreshold = 0.95;

        /// <summary>占比判据只看这么大的片（4MB 以下片太小，比例失真）。</summary>
        public static long ZeroRatioMinBytes = 4 * 1024 * 1024;

        /// <summary>合并后尾零告警阈值（1MB）。</summary>
        public static long TailZeroThreshold = 1024 * 1024;

        /// <summary>尾零扫描最多向前读这么多（64MB，再长的合法尾零极罕见）。</summary>
        public static long TailScanMaxBytes = 64L * 1024 * 1024;

        /// <summary>strict 模式下的零洞阈值（4MB）。</summary>
        public static long HoleThreshold = 4L * 1024 * 1024;

        /// <summary>每片自动重下对比的次数上限。</summary>
        public static int ReDownloadMaxPerPart = 2;

        // ==================== 一次零段扫描的结果 ====================

        /// <summary>一次零段扫描的结果。</summary>
        public sealed class ZeroScan
        {
            /// <summary>实际扫描的字节数。</summary>
            public long ScannedBytes;
            /// <summary>最长连续 0x00 游程。</summary>
            public long MaxZeroRun;
            /// <summary>0x00 总数。</summary>
            public long ZeroBytes;
            /// <summary>零字节占比。</summary>
            public double ZeroRatio;
            /// <summary>是否判为可疑。</summary>
            public bool Suspicious;
            /// <summary>零洞区间（strict 全文件模式用；每项 = [起, 止]）。</summary>
            public List<long[]> Holes = new List<long[]>();
        }

        // ==================== 防线一：分片内容校验 ====================

        /// <summary>
        /// 防线一：分片是否可疑（重读分片文件——刚由同一 worker 写满，
        /// 15~30MB 全在系统页缓存里，开销≈0）。
        /// </summary>
        public static bool IsPartSuspicious(string partFile, long expectedBytes, out ZeroScan scan)
        {
            scan = ScanFile(partFile, expectedBytes);
            return scan.Suspicious;
        }

        /// <summary>扫描文件的前 <paramref name="expectedBytes"/> 字节。</summary>
        public static ZeroScan ScanFile(string path, long expectedBytes)
        {
            var r = new ZeroScan();
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete, 1024 * 1024,
                           FileOptions.SequentialScan))
                {
                    ScanStreamCore(fs, Math.Min(expectedBytes, fs.Length), r, trackHoles: false);
                }
            }
            catch
            {
                // 读不了按"不可疑"处理 —— 绝不能因为校验器自己出错而弄坏下载。
            }
            r.Suspicious = r.MaxZeroRun >= ZeroRunThreshold
                        || (r.ScannedBytes >= ZeroRatioMinBytes && r.ZeroRatio >= ZeroRatioThreshold);
            return r;
        }

        /// <summary>零段扫描核心（分片与全文件共用）。</summary>
        private static void ScanStreamCore(Stream s, long bytes, ZeroScan r, bool trackHoles)
        {
            byte[] buf = new byte[1024 * 1024];
            long run = 0, maxRun = 0, zeros = 0, pos = 0, holeStart = -1;
            int n;
            while (bytes > 0 && (n = s.Read(buf, 0, (int)Math.Min(buf.Length, bytes))) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    if (buf[i] == 0)
                    {
                        run++; zeros++;
                        if (trackHoles && holeStart < 0) holeStart = pos + i;
                    }
                    else
                    {
                        if (run > maxRun) maxRun = run;
                        if (trackHoles && holeStart >= 0 && (pos + i) - holeStart >= HoleThreshold)
                            r.Holes.Add(new[] { holeStart, pos + i });
                        if (trackHoles) holeStart = -1;
                        run = 0;
                    }
                }
                pos += n;
                bytes -= n;
            }
            if (run > maxRun) maxRun = run;
            if (trackHoles && holeStart >= 0 && pos - holeStart >= HoleThreshold)
                r.Holes.Add(new[] { holeStart, pos });

            r.MaxZeroRun = maxRun;
            r.ZeroBytes = zeros;
            r.ScannedBytes = pos;
            r.ZeroRatio = pos > 0 ? (double)zeros / pos : 0;
        }

        // ==================== 防线二：合并后尾零扫描 ====================

        /// <summary>
        /// 防线二：从文件尾向前找**最后一个非零字节**，返回尾零长度。
        /// 只读尾部最多 <see cref="TailScanMaxBytes"/>（64MB），<0.5s。
        /// </summary>
        public static long TailZeroBytes(string path)
        {
            const int chunk = 4 * 1024 * 1024;
            var fi = new FileInfo(path);
            long fileLen = fi.Length;
            long pos = fileLen;
            byte[] buf = new byte[chunk];
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete, chunk, FileOptions.SequentialScan))
                {
                    while (pos > 0)
                    {
                        int want = (int)Math.Min(chunk, pos);
                        fs.Seek(pos - want, SeekOrigin.Begin);
                        int got = ReadFull(fs, buf, want);
                        for (int i = got - 1; i >= 0; i--)
                            if (buf[i] != 0)
                                return fileLen - (pos - want + i) - 1;
                        pos -= want;
                        if (fileLen - pos >= TailScanMaxBytes) break;
                    }
                }
            }
            catch { }
            return fileLen;   // 整个（扫过的部分）都是零
        }

        private static int ReadFull(FileStream fs, byte[] buf, int want)
        {
            int total = 0;
            while (total < want)
            {
                int n = fs.Read(buf, total, want - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        // ==================== 防线三：zip 逐条目 CRC32 ====================

        /// <summary>zip 校验结果。</summary>
        public sealed class ZipVerifyResult
        {
            /// <summary>是否全部通过。</summary>
            public bool Ok = true;
            /// <summary>坏条目清单（最多 50 条 + 省略提示）。</summary>
            public List<string> BadEntries = new List<string>();
            /// <summary>EOCD / 中央目录损坏（整个 zip 不可用）。</summary>
            public bool StructuralBroken;
            /// <summary>无法校验但不算失败的条目（加密等）。</summary>
            public List<string> Warnings = new List<string>();
        }

        /// <summary>看文件头是不是 zip（PK\x03\x04）。</summary>
        public static bool LooksLikeZip(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4))
                {
                    if (fs.Length < 4) return false;
                    return fs.ReadByte() == 'P' && fs.ReadByte() == 'K'
                        && fs.ReadByte() == 3 && fs.ReadByte() == 4;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// 防线三：zip 逐条目 CRC 校验。
        /// <para>net48 的 <see cref="ZipArchiveEntry"/> 流在读完/Dispose 时会自行核对
        /// CRC32 并抛 <see cref="InvalidDataException"/> —— 所以「读到底」就是校验本身，
        /// 不用自己算 CRC。</para>
        /// </summary>
        public static ZipVerifyResult VerifyZipCrc(string zipPath, Action<string> log, CancellationToken ct)
        {
            var r = new ZipVerifyResult();
            try
            {
                using (var za = ZipFile.OpenRead(zipPath))
                {
                    foreach (var e in za.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (e.Length == 0) continue;   // 目录 / 零字节条目跳过

                        try
                        {
                            using (var s = e.Open())
                            {
                                byte[] buf = new byte[1024 * 1024];
                                while (s.Read(buf, 0, buf.Length) > 0) { }
                            }
                        }
                        catch (InvalidDataException)
                        {
                            r.Ok = false;
                            r.BadEntries.Add(e.FullName + "  [CRC 不符]");
                        }
                        catch (NotSupportedException)
                        {
                            // 加密条目等：跳过并警告（罕见，不阻塞整个校验）。
                            r.Warnings.Add(e.FullName + "  [格式不支持，已跳过]");
                        }
                        catch (Exception ex)
                        {
                            r.Ok = false;
                            r.BadEntries.Add(e.FullName + "  [" + ex.Message + "]");
                        }

                        if (r.BadEntries.Count >= 50)
                        {
                            r.BadEntries.Add("……（其余略）");
                            break;
                        }
                    }
                }
            }
            catch (InvalidDataException)
            {
                r.Ok = false;
                r.StructuralBroken = true;
                r.BadEntries.Add("zip 结构损坏（EOCD/中央目录无法读取）");
            }
            return r;
        }

        // ==================== 收尾总入口（防线二 + 三编排） ====================

        /// <summary>最终判定结果。</summary>
        public sealed class FinalVerdict
        {
            /// <summary>是否通过。</summary>
            public bool Ok;
            /// <summary>被校验的文件路径。</summary>
            public string Path;
            /// <summary>给用户看的失败文案。</summary>
            public string UserMessage;
            /// <summary>尾部零字节数。</summary>
            public long TailZeroBytes;
            /// <summary>zip 校验结果（非 zip 为 null）。</summary>
            public ZipVerifyResult Zip;
            /// <summary>strict 模式发现的零洞。</summary>
            public List<long[]> Holes;
            /// <summary>是否放行（尾零但 zip 全通过 → 源数据本身如此）。</summary>
            public bool PassedAsSource;
        }

        /// <summary>
        /// 收尾总入口：防线二（尾零）+ 防线三（zip CRC）+ strict 全文件零洞。
        /// <para>⚠️ 走到这里分片**已经合并完并删光**，校验失败不能再写"可重试续传"。</para>
        /// </summary>
        public static async Task<FinalVerdict> VerifyFinalFileAsync(
            string dest, long total, VerifyLevel level, Action<string> log, CancellationToken ct)
        {
            var v = new FinalVerdict { Path = dest };
            if (level == VerifyLevel.Off)
            {
                v.Ok = true;
                return v;
            }

            // ---- 防线三先跑：zip 的 CRC 是精确的，能一锤定音 ----
            bool isZip = VerifyZip && LooksLikeZip(dest);
            ZipVerifyResult zr = null;
            if (isZip)
            {
                log?.Invoke("[完整性] 正在校验 zip 完整性（逐条目 CRC32），大文件可能需要几十秒…");
                await Task.Run(() => zr = VerifyZipCrc(dest, log, ct), ct).ConfigureAwait(false);
                v.Zip = zr;

                if (zr != null && !zr.Ok)
                {
                    v.Ok = false;
                    v.UserMessage = string.Format(
                        "zip 校验失败：{0} 个条目 CRC 不符（详见 {1}.integrity-report.txt）。"
                        + "分片已合并、无法定位重下，请整文件重新下载；若反复出现请更换下载源。",
                        zr.BadEntries.Count, dest);
                    return v;
                }
                if (zr != null)
                    log?.Invoke("[完整性] zip 校验通过（全部条目 CRC32 正确）。");
            }

            // ---- 防线二：尾零扫描 ----
            long tail = TailZeroBytes(dest);
            v.TailZeroBytes = tail;
            if (tail >= TailZeroThreshold)
            {
                if (zr != null && zr.Ok)
                {
                    // zip 全部条目 CRC 通过 ⇒ 尾部零数据是源文件本身如此，放行。
                    log?.Invoke(string.Format(
                        "[完整性] 尾部 {0} 是零数据，但 zip 全部条目 CRC 校验通过 → 判定为源数据本身如此，放行。",
                        FmtBytes(tail)));
                    v.Ok = true;
                    v.PassedAsSource = true;
                    return v;
                }
                v.Ok = false;
                v.UserMessage = string.Format(
                    "下载内容校验未通过：文件尾部 {0} 为空数据（零填充），疑似静默损坏。"
                    + "报告已保存到 {1}.integrity-report.txt，建议重新下载。",
                    FmtBytes(tail), dest);
                return v;
            }

            // ---- strict：全文件零洞扫描 ----
            if (level == VerifyLevel.Strict)
            {
                log?.Invoke("[完整性] 严格模式：正在扫描全文件零洞…");
                var scan = new ZeroScan();
                bool scanned = await Task.Run(() =>
                {
                    try
                    {
                        using (var fs = new FileStream(dest, FileMode.Open, FileAccess.Read,
                                   FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                            ScanStreamCore(fs, fs.Length, scan, trackHoles: true);
                        return true;
                    }
                    catch { return false; }
                }, ct).ConfigureAwait(false);

                if (scanned && scan.Holes.Count > 0 && !(zr != null && zr.Ok))
                {
                    v.Ok = false;
                    v.Holes = scan.Holes;
                    v.UserMessage = string.Format(
                        "下载内容校验未通过：文件内发现 {0} 处 ≥{1} 的零洞，疑似静默损坏。"
                        + "报告已保存到 {2}.integrity-report.txt，建议重新下载。",
                        scan.Holes.Count, FmtBytes(HoleThreshold), dest);
                    return v;
                }
            }

            v.Ok = true;
            return v;
        }

        // ==================== 证据报告 ====================

        /// <summary>失败时落盘证据报告（成功不落盘）。</summary>
        public static void WriteReport(string dest, FinalVerdict v)
        {
            try
            {
                var lines = new List<string>
                {
                    "GeZi 下载完整性校验报告",
                    "文件: " + v.Path,
                    "时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    "文件大小: " + new FileInfo(v.Path).Length,
                    "尾部零字节: " + v.TailZeroBytes,
                };
                if (v.Zip != null)
                {
                    lines.Add("zip 校验: " + (v.Zip.Ok ? "通过" : "失败")
                        + (v.Zip.StructuralBroken ? "（结构损坏）" : ""));
                    foreach (var b in v.Zip.BadEntries) lines.Add("  坏条目: " + b);
                    foreach (var w in v.Zip.Warnings) lines.Add("  警告: " + w);
                }
                if (v.Holes != null)
                    foreach (var h in v.Holes)
                        lines.Add(string.Format("  零洞: {0} - {1}（{2}）", h[0], h[1], FmtBytes(h[1] - h[0])));
                lines.Add("");
                lines.Add("结论: " + v.UserMessage);
                File.WriteAllLines(dest + ".integrity-report.txt", lines);
            }
            catch { }
        }

        /// <summary>格式化字节数（1GB/1MB/1KB 三档）。</summary>
        public static string FmtBytes(long b)
        {
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("F2") + " GB";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("F1") + " MB";
            if (b >= 1L << 10) return (b / (double)(1L << 10)).ToString("F1") + " KB";
            return b + " B";
        }
    }
}
