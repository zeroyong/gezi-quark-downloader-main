using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GeZi
{
    /// <summary>
    /// 视频文件判定 + MP4 索引预检。
    ///
    /// 为什么 MP4 要单独看：moov（索引表）可能写在文件末尾。边下边播时
    /// 目标文件只有"已下载的前缀"，moov 还没出现，播放器会直接判损坏。
    /// 所以在开播前沿 top-level box 走一遍：只要在当前前缀里没看到 moov，
    /// 就明确告诉用户"索引还没下到"，而不是让 PotPlayer 甩一句无法播放。
    /// MKV / TS 等没有这个问题（关键信息在头部或可顺序解析）。
    /// </summary>
    internal static class MediaProbe
    {
        private static readonly HashSet<string> VideoExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".mkv", ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".flv",
                ".ts", ".m2ts", ".mts", ".webm", ".rmvb", ".rm", ".3gp",
                ".mpg", ".mpeg", ".vob", ".ogv", ".divx", ".asf",
            };

        private static readonly HashSet<string> Mp4Extensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".mp4", ".m4v", ".mov",
            };

        public static bool IsVideo(string path)
        {
            return !string.IsNullOrEmpty(path) && VideoExtensions.Contains(Path.GetExtension(path) ?? "");
        }

        public static bool IsMp4Family(string path)
        {
            return !string.IsNullOrEmpty(path) && Mp4Extensions.Contains(Path.GetExtension(path) ?? "");
        }

        /// <summary>
        /// mp4/mov 的 moov 是否已经在当前已下载前缀里。
        /// 非 MP4 或无法解析时返回 true —— 不阻止播放（交给播放器自己判断）。
        /// </summary>
        public static bool IsMp4IndexAvailable(string path)
        {
            if (!IsMp4Family(path)) return true;

            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
                {
                    long len = fs.Length;
                    long off = 0;
                    var head = new byte[16];

                    for (int guard = 0; guard < 128 && off + 8 <= len; guard++)
                    {
                        fs.Position = off;
                        if (!ReadExactly(fs, head, 8)) return true;

                        long size = ReadBE32(head, 0);
                        string type = Encoding.ASCII.GetString(head, 4, 4);

                        if (size == 1)
                        {
                            // 64 位 largesize
                            if (off + 16 > len) return false;
                            fs.Position = off;
                            if (!ReadExactly(fs, head, 16)) return false;
                            size = ReadBE64(head, 8);
                            if (size < 16) return false;
                        }
                        else if (size == 0)
                        {
                            // 一直延伸到文件末尾
                            size = len - off;
                        }
                        else if (size < 8)
                        {
                            return true;   // 结构异常：不拦，交给播放器
                        }

                        if (string.Equals(type, "moov", StringComparison.Ordinal))
                            return true;

                        if (off + size > len)
                            return false;  // 下一个 box 还没下到，moov 也还没出现

                        off += size;
                    }

                    // 走完已有前缀都没看到 moov
                    return false;
                }
            }
            catch
            {
                return true;   // 读不到就别拦着播放
            }
        }

        private static bool ReadExactly(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        private static uint ReadBE32(byte[] b, int o)
        {
            return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16)
                 | ((uint)b[o + 2] << 8) | b[o + 3];
        }

        private static long ReadBE64(byte[] b, int o)
        {
            return ((long)ReadBE32(b, o) << 32) | ReadBE32(b, o + 4);
        }
    }
}
