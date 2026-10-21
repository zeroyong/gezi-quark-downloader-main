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
using System.Runtime.InteropServices;

namespace GeZi
{
    public static class Util
    {
        public static string FormatSize(long n)
        {
            // 🚨 【2026-10-06 修】原来除以 1024 却标 "KB/MB/GB"（那是 KiB/MiB/GiB）。
            // 后果：同一个文件「夸克分享页写 9.54 GB、本程序写 8.89 GB」，用户以为少下了。
            // 改成 **1000 进制** —— 单位名副其实，也顺带让速度显示符合网络速率的惯例（1 MB/s = 10^6 B/s）。
            double v = n;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (v >= 1000 && i < units.Length - 1)
            {
                v /= 1000.0;
                i++;
            }
            return i == 0 ? n + " B" : v.ToString("F2") + " " + units[i];
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out string ppszPath);

        /// <summary>取 Windows「下载」文件夹的真实路径（可能被重定向到别的盘/OneDrive），与原版 _downloads_dir 一致。</summary>
        public static string GetDownloadsFolder()
        {
            try
            {
                // FOLDERID_Downloads
                var fid = new Guid("374DE290-123F-4565-9164-39C4925E467B");
                int hres = SHGetKnownFolderPath(ref fid, 0, IntPtr.Zero, out string path);
                if (hres == 0 && !string.IsNullOrEmpty(path) && Directory.Exists(path))
                    return path;
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        /// <summary>尝试创建目录并写入一个测试文件，判断该目录是否真的可写。</summary>
        public static bool IsWritable(string dir)
        {
            if (string.IsNullOrEmpty(dir))
                return false;
            try
            {
                Directory.CreateDirectory(dir);
                string test = Path.Combine(dir, ".w_" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(test, "1");
                File.Delete(test);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>应用本地数据目录（%LOCALAPPDATA%，系统保证可写、不受「受控文件夹访问」保护）。</summary>
        /// <remarks>
        /// ⚠️ 【2026-10-22】目录名**保持「鸽子下载」不变**（不要跟着品牌改名走）：
        /// 这里是配置、账号档案、任务快照的存放位置，改了的话老用户升级后
        /// 会"设置全丢、账号不见了、任务历史没了" —— 属于破坏性变更。
        /// 品牌名只影响**界面与提示文案**。
        /// </remarks>
        public static string GetLocalAppDataDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "鸽子下载");
        }

        /// <summary>
        /// 统一数据目录：<b>固定在 %LOCALAPPDATA%\鸽子下载\</b>，不再跟 exe 目录走。
        ///
        /// <para><b>为什么 2026-10-22 改成固定</b>：原先的规则是"exe 同目录优先"
        /// （便携模式），于是<b>同一个程序的每一份副本各存各的</b>：</para>
        /// <list type="bullet">
        /// <item>Debug 版（src\GeZi\bin\Debug\net48\）写自己那份 state.json；</item>
        /// <item>发行包（dist\GeZi-v2.8.1-win64\）写另一份；</item>
        /// <item>原版 v2.3.1 又是另一份。</item>
        /// </list>
        /// <para>后果实测（用户报告）：<b>下载是发行包发起的，任务记录却只在 Debug 那份里有</b> ——
        /// 切到哪个目录启动，就只能看到那个目录上次写下的记录，"重启后任务不见了"。
        /// 更危险的是两个副本<b>同时下载同一个文件</b>会写同一份分片目录，互相踩坏。</para>
        ///
        /// <para>改成固定目录后，Debug / 发行包 / 任何副本<b>共享同一份</b>数据：
        /// 任务、进度、账号、设置全都互通，也从根本上杜绝了"两份程序互相踩分片"。</para>
        ///
        /// <para><b>迁移</b>：老用户的配置与历史可能躺在 exe 同目录（便携模式留下的）。
        /// 首次启动时若统一目录里还没有、而 exe 目录里有，就把它搬过去（见
        /// <see cref="MigrateLegacyProfile"/>）。搬完把旧文件改名成 .migrated，
        /// 不删除 —— 万一搬错了还能手动找回。</para>
        /// </remarks>
        public static string GetDataDir()
        {
            return GetLocalAppDataDir();
        }

        /// <summary>
        /// 一次性迁移：把 exe 同目录里的旧配置 / 任务快照 / 账号档案搬到统一数据目录。
        ///
        /// <para><b>触发条件</b>：统一目录里<b>还没有</b> GeZi.config.xml，
        /// 而 exe 同目录里<b>有</b> —— 说明用户是"便携模式"的老用户，配置还在 exe 旁边。</para>
        ///
        /// <para><b>为什么不无条件搬</b>：统一目录里已经有配置时，说明迁移做过了
        /// （或用户就是用固定目录用的），再搬会把统一目录的现有数据覆盖掉。</para>
        ///
        /// <para><b>失败不致命</b>：任何一步出错都只是放弃迁移，程序照常启动
        /// （配置会新建一份，用户重新登录即可）。</para>
        /// </summary>
        public static void MigrateLegacyProfile()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string destDir = GetDataDir();

                // 已经在统一目录里了（而不是 exe 同目录）→ 没有可迁移的东西
                if (string.Equals(Path.GetFullPath(baseDir).TrimEnd('\\'),
                                 Path.GetFullPath(destDir).TrimEnd('\\'),
                                 StringComparison.OrdinalIgnoreCase))
                    return;

                Directory.CreateDirectory(destDir);

                string destCfg = Path.Combine(destDir, "GeZi.config.xml");
                if (File.Exists(destCfg))
                    return;   // 已经迁过了

                // ---- 配置 ----
                string legacyCfg = Path.Combine(baseDir, "GeZi.config.xml");
                if (File.Exists(legacyCfg))
                {
                    File.Copy(legacyCfg, destCfg, true);
                    TryMarkMigrated(legacyCfg);
                }

                // ---- 任务快照 ----
                foreach (var name in new[] { "GeZi.state.json", "GeZi.state.json.bak" })
                {
                    string src = Path.Combine(baseDir, name);
                    if (!File.Exists(src)) continue;
                    string dst = Path.Combine(destDir, name);
                    if (File.Exists(dst)) continue;
                    File.Copy(src, dst, true);
                    TryMarkMigrated(src);
                }

                // ---- 账号档案 ----
                string legacyAcc = Path.Combine(baseDir, "accounts");
                if (Directory.Exists(legacyAcc))
                {
                    string destAcc = Path.Combine(destDir, "accounts");
                    if (!Directory.Exists(destAcc))
                    {
                        Directory.CreateDirectory(destAcc);
                        foreach (var f in Directory.GetFiles(legacyAcc, "*.xml"))
                        {
                            string dst = Path.Combine(destAcc, Path.GetFileName(f));
                            if (!File.Exists(dst))
                                File.Copy(f, dst, true);
                        }
                    }
                    TryMarkMigrated(legacyAcc);
                }
            }
            catch
            {
                // 迁移失败不拦启动：配置会新建一份，用户重新登录即可
            }
        }

        /// <summary>给已迁移的旧文件/目录改名（加 .migrated 后缀），便于用户确认与回退。</summary>
        private static void TryMarkMigrated(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    string d = path + ".migrated";
                    if (!Directory.Exists(d)) Directory.Move(path, d);
                }
                else if (File.Exists(path))
                {
                    string f = path + ".migrated";
                    if (!File.Exists(f)) File.Move(path, f);
                }
            }
            catch { }
        }
    }
}
