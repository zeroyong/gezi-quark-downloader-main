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
using System.Xml.Serialization;
using GeZi.Core.Support;

namespace GeZi
{
    public class AppSettings
    {
        /// <summary>
        /// 单任务下载线程数（= 单文件最大并发连接）。默认 64，与 Python Flet 版一致
        /// （core 的 DOWNLOAD_THREADS 也是 64）。上限见 SegmentedDownloader.MaxRealConcurrency。
        /// </summary>
        public int Threads { get; set; } = 64;

        public int ConcurrentTasks { get; set; } = 3;
        public string OutDir { get; set; } = "";
        public bool AutoClean { get; set; } = true;

        /// <summary>
        /// 分片落盘方式。SharedFile = 直写同一文件（默认，峰值 1×）；
        /// SegmentedFiles = 独立分片 + 边合并（可边下边播，峰值 1×+单片）。
        /// 老配置文件里没有这个字段，反序列化后保持默认值 SharedFile，行为与旧版一致。
        /// </summary>
        public GeZi.Core.Download.WriteMode WriteMode { get; set; } = GeZi.Core.Download.WriteMode.SharedFile;

        /// <summary>
        /// 分片/续传元数据的根目录。留空 = 与目标文件同级（`{目标}.qparts`），即原有行为。
        /// 移植自 Python Flet 版的 part_dir：可把分片集中到另一块盘（如 SSD）。
        /// </summary>
        public string PartsRoot { get; set; } = "";

        /// <summary>
        /// 是否把分片目录设为「隐藏」。移植自 Python Flet 版的 hide_parts。
        /// 只影响新建的分片目录在资源管理器里是否显示（不影响功能）。
        /// </summary>
        public bool HideParts { get; set; } = true;

        /// <summary>
        /// 曾经用过的分片根目录清单（最多 <see cref="PartsDirsMax"/> 个，最近使用的在末尾）。
        ///
        /// 移植自 Python Flet 版的 `part_dirs_known`。存在的理由很实际：
        /// 用户改过「默认分片位置」之后，**旧目录里的残留分片既扫不到、也没入口去清**，
        /// 只能一直烂在磁盘上（"分片根本没删掉"就是这么来的）。
        /// 记住历史目录后，清理/统计时可以把这些位置的残留一并处理。
        /// </summary>
        public List<string> PartsDirsKnown { get; set; } = new List<string>();

        /// <summary>历史分片目录清单的长度上限（与 Python 版一致）。</summary>
        public const int PartsDirsMax = 20;

        /// <summary>
        /// 登录 Cookie。内存里始终是明文（供 QuarkClient 使用），
        /// 序列化到磁盘时由 Save/Load 做 DPAPI 加解密，字段本身不感知。
        /// </summary>
        public string Cookie { get; set; } = "";

        public string Who { get; set; } = "";

        /// <summary>
        /// 账号头像 URL（登录时从 account/info 探测到的）。
        /// 留空 = 拿不到 → 界面回退到"昵称首字"头像。
        /// 头像链接可能带时效，每次登录/启动校验都会重新探测覆盖。
        /// </summary>
        public string AvatarUrl { get; set; } = "";

        /// <summary>
        /// **当前登录账号的档案 Key**（对应 accounts/{key}.xml 的文件名）。
        /// </summary>
        /// <remarks>
        /// 【2026-10-03 新增】用来判断"账号列表里哪一行是当前账号"。
        /// 原来是用**整串 Cookie 做字符串比较**（`acc.Cookie == _client.CookieStr`），
        /// 但运行时 Cookie 会被重建/刷新（顺序、条目都可能变），所以永远比不中 ——
        /// 表现为「正在登录的那个账号，它的『切换』按钮居然还是可点的」（用户反馈）。
        /// 存 Key 就没有这个问题：Key 是夸克返回的 uid，稳定不变。
        /// </remarks>
        public string CurrentAccountKey { get; set; } = "";

        /// <summary>
        /// 点主窗口「关闭」按钮时的行为。见 <see cref="CloseAction"/>。
        /// </summary>
        /// <remarks>
        /// 【2026-10-03 新增】用户要求：「第一次时会询问用户，然后可以选择最小化托盘
        /// 或者是直接关闭程序，并且在这个弹窗的右下角可以勾选此后不再提示，在设置中可以修改」。
        /// ⚠️ 默认值必须是 0 = <see cref="CloseAction.Ask"/>：
        ///    XmlSerializer 反序列化**不会执行字段初始化器**，老配置文件里没有这个节点时
        ///    属性会保持 default(T) = 0 —— 正好就是"第一次要问"。
        /// </remarks>
        public CloseAction OnCloseButton { get; set; } = CloseAction.Ask;

        /// <summary>
        /// 小窗左上角坐标。⚠️ 必须配 <see cref="MiniPosSet"/> 用：
        /// XmlSerializer 反序列化不跑字段初始化器，老配置里缺这个节点时值是 0
        /// （而不是 NaN），光看 0 分不清"没摆过"还是"就摆在 (0,0)"。
        /// </summary>
        public double MiniLeft { get; set; }
        public double MiniTop { get; set; }

        /// <summary>小窗是否已经摆过位置（false = 首次打开，贴屏幕右下角）。</summary>
        public bool MiniPosSet { get; set; }

        /// <summary>
        /// 外观主题（2026-10-04 新增）。Light / Dark / System。
        ///
        /// ⚠️ 默认值必须是 0 = <see cref="AppTheme.System"/>：
        ///    XmlSerializer 反序列化**不执行字段初始化器**，老配置里没有这个节点时
        ///    属性保持 default(T) = 0。这里刻意让 0 对应 System（而不是 Light），
        ///    于是**老用户升级后自动跟随系统** —— 如果系统是浅色，观感与升级前完全一致；
        ///    系统是深色则界面跟着变深，正是"跟随系统"该有的行为。
        /// </summary>
        public AppTheme Theme { get; set; } = AppTheme.System;
    }

    /// <summary>主窗口「关闭」按钮的行为。</summary>
    public enum CloseAction
    {
        /// <summary>弹一次询问框（默认）。用户勾了「此后不再提示」后会被改成下面两个之一。</summary>
        Ask = 0,
        /// <summary>隐藏到托盘，下载继续跑。</summary>
        MinimizeToTray = 1,
        /// <summary>直接退出程序。</summary>
        Exit = 2,
    }

    public static class SettingsStore
    {
        private static readonly XmlSerializer Ser = new XmlSerializer(typeof(AppSettings));

        private static string _filePath;
        private static bool _loaded;

        /// <summary>
        /// 配置文件路径：优先 exe 同目录（绿色便携，数据随 U 盘走）；
        /// 同目录不可写（装在 Program Files、"受控文件夹访问" 拦截等）时
        /// 回退到 %LOCALAPPDATA%\鸽子下载\，保证任何情况下都能存住设置。
        /// </summary>
        public static string FilePath
        {
            get
            {
                if (_loaded && !string.IsNullOrEmpty(_filePath))
                    return _filePath;

                _filePath = ResolveFilePath();
                _loaded = true;
                return _filePath;
            }
        }

        private static string ResolveFilePath()
        {
            string portable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GeZi.config.xml");

            // exe 同目录已经有一份配置文件 → 无条件沿用（避免老用户设置"搬家"到别处）
            try
            {
                if (File.Exists(portable))
                    return portable;
            }
            catch { }

            // 试着真的在 exe 同目录落一个文件，确认可写
            if (Util.IsWritable(AppDomain.CurrentDomain.BaseDirectory))
                return portable;

            string fallbackDir = Util.GetLocalAppDataDir();
            try
            {
                Directory.CreateDirectory(fallbackDir);
            }
            catch { }
            return Path.Combine(fallbackDir, "GeZi.config.xml");
        }

        /// <summary>上一次 Load/Save 是否踩到了异常（供 UI 提示用户设置未能持久化）。</summary>
        public static string LastError { get; private set; }

        public static AppSettings Load()
        {
            LastError = null;
            var s = new AppSettings();
            try
            {
                if (File.Exists(FilePath))
                {
                    using (var fs = File.OpenRead(FilePath))
                    {
                        var loaded = Ser.Deserialize(fs) as AppSettings;
                        if (loaded != null)
                            s = loaded;
                    }
                }
                else
                {
                    // 便携路径没文件时，兜底再看一眼 %LOCALAPPDATA%，实现两种位置互认
                    string alt = Path.Combine(Util.GetLocalAppDataDir(), "GeZi.config.xml");
                    if (File.Exists(alt))
                    {
                        using (var fs = File.OpenRead(alt))
                        {
                            var loaded = Ser.Deserialize(fs) as AppSettings;
                            if (loaded != null)
                                s = loaded;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }

            // 兼容旧版明文 Cookie：解出来是明文就原样用，下次 Save 时会被自动加密
            s.Cookie = SecretProtector.Unprotect(s.Cookie);
            // 并发上限与核心层的 MaxRealConcurrency 保持一致。
            // 旧配置里可能存着 512/8192 这类超出范围的值，读进来时夹到有效范围，
            // 免得 UI 上落不到任何一个合法档位。上限取自核心层常量，避免两处失配。
            if (s.Threads <= 0) s.Threads = 64;
            if (s.Threads > GeZi.Core.Download.SegmentedDownloader.MaxRealConcurrency)
                s.Threads = GeZi.Core.Download.SegmentedDownloader.MaxRealConcurrency;
            if (s.ConcurrentTasks <= 0) s.ConcurrentTasks = 3;
            return s;
        }

        public static void Save(AppSettings s)
        {
            LastError = null;
            if (s == null)
                return;

            string rawCookie = s.Cookie;   // 内存中始终明文，落盘前临时加密
            var tmpPath = FilePath + ".tmp";
            try
            {
                s.Cookie = SecretProtector.Protect(rawCookie);

                // 先写临时文件再原子替换：避免写一半断电/异常留下半截 XML，
                // 下次启动 Load 直接抛异常、用户设置全丢。
                using (var fs = File.Create(tmpPath))
                    Ser.Serialize(fs, s);

                if (File.Exists(FilePath))
                    File.Delete(FilePath);
                File.Move(tmpPath, FilePath);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            }
            finally
            {
                s.Cookie = rawCookie;   // 还原内存明文，调用方拿到的对象不被污染
            }
        }
    }
}
