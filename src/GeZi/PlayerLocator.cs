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
using System.IO;
using Microsoft.Win32;

namespace GeZi
{
    /// <summary>
    /// 本机播放器探测（VLC / PotPlayer）。
    ///
    /// <para>【2026-10-24 改：支持 PotPlayer】用户需求：</para>
    /// <list type="bullet">
    /// <item>「视频文件下载完成后使用 PotPlayer 播放（先检测对应位置）——
    ///       点文件右侧的播放按钮，用 PotPlayer 打开」</item>
    /// <item>「如果是正在下载的，才使用 VLC」</item>
    /// <item>「用户也可以在设置自定义」</item>
    /// </list>
    ///
    /// <para><b>为什么"下完了用 PotPlayer、下载中用 VLC"</b>：</para>
    /// <list type="bullet">
    /// <item><b>下载中</b>要靠"文件共享模式"——VLC 用
    ///       <c>FileShare.ReadWrite</c> 打开，与我们写盘互不干扰；
    ///       部分播放器只允许只读共享，会让我们后续写盘失败。所以下载中必须 VLC。</item>
    /// <item><b>已完成</b>的文件不再有并发写入，PotPlayer 的兼容性和画质更好，
    ///       用它更合适。</item>
    /// </list>
    ///
    /// <para>探测顺序：注册表 App Paths → 常见安装目录 → PATH。
    /// 两种播放器各查一遍，任一缺失不影响另一个。</para>
    /// </summary>
    internal static class PlayerLocator
    {
        internal sealed class Player
        {
            public string Name;      // 显示名（VLC / PotPlayer）
            public string ExePath;   // 可执行文件完整路径
            public bool Recommended; // 保留字段

            /// <summary>是不是 VLC（决定是否要加 --started-from-file 等专属参数）。</summary>
            public bool IsVlc
            {
                get
                {
                    return !string.IsNullOrEmpty(ExePath)
                        && Path.GetFileName(ExePath)
                            .IndexOf("vlc", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
        }

        /// <summary>
        /// 用户手动指定的 VLC 路径（来自设置页）。由 MainWindow 在加载设置时写入。
        /// 为空/文件不存在时自动回退到常规探测 —— 便携版装在任意目录也能用。
        /// </summary>
        public static string PreferredPath { get; set; }

        /// <summary>
        /// 用户手动指定的 PotPlayer 路径（设置页"完成后播放器"那一栏）。
        /// 留空则自动探测。见 <see cref="FindPotPlayer"/>。
        /// </summary>
        public static string PreferredPotPlayerPath { get; set; }

        // ---------------- 两种播放器 ----------------

        /// <summary>探测 VLC（**下载中**用它，因为它的文件共享模式最宽容）。没找到返回 null。</summary>
        public static Player Find()
        {
            string preferred = PreferredPath;
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                try
                {
                    if (File.Exists(preferred))
                        return new Player
                        {
                            Name = "VLC",
                            ExePath = Path.GetFullPath(preferred),
                            Recommended = true,
                        };
                }
                catch { }
            }
            return FindVlc();
        }

        /// <summary>VLC —— 格式全，且以 FileShare.ReadWrite 打开文件（下载中必须用它）。</summary>
        public static Player FindVlc()
        {
            string p = Probe("vlc.exe", "VideoLAN\\VLC\\vlc.exe",
                             "VideoLAN\\VLC\\vlc.exe");
            return p == null ? null : new Player { Name = "VLC", ExePath = p, Recommended = true };
        }

        /// <summary>
        /// 探测 PotPlayer（**下载完成后**优先用它）。
        ///
        /// <para>安装位置很杂，这几个都见过：</para>
        /// <list type="bullet">
        /// <item><c>%ProgramFiles%\DAUM\PotPlayer\PotPlayerMini64.exe</c>（官方 64 位安装）</item>
        /// <item><c>%ProgramFiles%\DAUM\PotPlayer\PotPlayerMini.exe</c>（32 位）</item>
        /// <item><c>%ProgramFiles(x86)%\DAUM\PotPlayer\PotPlayerMini.exe</c></item>
        /// <item>绿色版解压到任意目录（只能靠用户手填或 PATH）</item>
        /// </list>
        /// <para>注意可执行文件名是 <c>PotPlayerMini64.exe</c> / <c>PotPlayerMini.exe</c>，
        /// 不是 PotPlayer.exe —— 主程序 <c>PotPlayer.exe</c> 只是个启动器。</para>
        /// </summary>
        public static Player FindPotPlayer()
        {
            // ① 用户手填优先（绿色版/自定义目录只能靠这个）
            string preferred = PreferredPotPlayerPath;
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                try
                {
                    if (File.Exists(preferred))
                        return new Player
                        {
                            Name = "PotPlayer",
                            ExePath = Path.GetFullPath(preferred),
                            Recommended = true,
                        };
                }
                catch { }
            }

            // ② 依次试三个 exe 名（64 位优先）
            string[] exeNames = { "PotPlayerMini64.exe", "PotPlayerMini.exe", "PotPlayer.exe" };
            string[] relPaths =
            {
                "DAUM\\PotPlayer\\PotPlayerMini64.exe",
                "DAUM\\PotPlayer\\PotPlayerMini.exe",
                "PotPlayer\\PotPlayerMini64.exe",
                "PotPlayer\\PotPlayerMini.exe",
            };

            foreach (var exe in exeNames)
            {
                string p = exe == "PotPlayerMini64.exe" ? Probe(exe, relPaths[0], relPaths[2])
                         : exe == "PotPlayerMini.exe" ? Probe(exe, relPaths[1], relPaths[3])
                         : Probe(exe, "DAUM\\PotPlayer\\PotPlayer.exe", "DAUM\\PotPlayer\\PotPlayer.exe");
                if (p != null)
                    return new Player { Name = "PotPlayer", ExePath = p, Recommended = true };
            }
            return null;
        }

        /// <summary>
        /// 三路探测：注册表 App Paths → 常见安装目录（含 32 位重定向）→ PATH。
        /// </summary>
        private static string Probe(string exeName, string relX64, string relX86)
        {
            // ① 注册表 App Paths —— 安装程序正规登记的位置，最可靠
            foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName,
                                         @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\" + exeName })
            {
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(root))
                    {
                        if (k == null) continue;
                        var v = k.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(v) && File.Exists(v)) return v;
                    }
                }
                catch { }
            }

            // ② 常见安装目录（含免安装版解压到用户目录的情况）
            foreach (var baseDir in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Path.Combine(Environment.GetFolderPath(
                             Environment.SpecialFolder.LocalApplicationData), "Programs"),
                     })
            {
                if (string.IsNullOrEmpty(baseDir)) continue;
                foreach (var rel in new[] { relX64, relX86 })
                {
                    if (string.IsNullOrEmpty(rel)) continue;
                    try
                    {
                        string p = Path.Combine(baseDir, rel);
                        if (File.Exists(p)) return p;
                    }
                    catch { }
                }
            }

            // ③ PATH —— 便携版 / 解压版常这么装
            try
            {
                string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathVar.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try
                    {
                        string p = Path.Combine(dir.Trim(), exeName);
                        if (File.Exists(p)) return p;
                    }
                    catch { }
                }
            }
            catch { }

            return null;
        }
    }
}
