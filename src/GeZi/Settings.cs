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
        /// 分片落盘方式。**已唯一化为 SegmentedFiles（独立分片 + 边合并）**。
        ///
        /// <para>【2026-10-20】原来还有 SharedFile（多 worker 各自 seek 写同一目标文件）：
        /// 那是随机 I/O，机械硬盘上磁头疯狂寻道 → 磁盘占用 100%、下载速度被拖住
        /// （用户实测反馈「2.0 后版本都会导致磁盘占用 100%」）。
        /// 而独立分片全程**顺序追加**，是唯一同时满足机械盘与固态盘的方案
        /// （夸克 CDN 上限 ~16MB/s，远低于 HDD 顺序写 ~150MB/s）。
        /// 因此共写模式已物理移除，此字段**仅为兼容老配置文件**而保留，
        /// 读入后一律强制按 SegmentedFiles 执行（见 DownloadJob 构造处）。</para>
        /// </summary>
        public GeZi.Core.Download.WriteMode WriteMode { get; set; } = GeZi.Core.Download.WriteMode.SegmentedFiles;

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
        /// 用户手动指定的 VLC 路径（vlc.exe）。
        /// 留空 = 自动探测；便携版/VLC 装在非常规目录时由设置页「选择 VLC 位置…」写入。
        /// 路径失效（文件被移走）时自动回退到自动探测，不会拦住下载/播放。
        /// </summary>
        public string VlcPath { get; set; } = "";

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
        /// 全部任务下载完成后是否自动关机（2026-10-20 新增）。
        ///
        /// <para>【为什么需要】用户常把大文件挂着下一整夜，希望下完自动关机省电。
        /// 原来只能自己盯进度、或者用第三方关机工具。</para>
        ///
        /// <para>⚠️ 默认 false：这是有副作用的破坏性动作，绝不能默认开启。
        /// XmlSerializer 反序列化不执行字段初始化器，老配置里没这个节点时
        /// 属性保持 default(bool) = false —— 正好符合预期。</para>
        ///
        /// <para>⚠️ 只在**本次运行**有效，不跨重启：重启后任务列表可能已变，
        /// 贸然沿用上次的"关机"意图很危险（用户可能忘了）。</para>
        /// </summary>
        public bool ShutdownAfterAll { get; set; }

        /// <summary>
        /// 是否跟随 Windows 系统代理（WinINET 设置，含 PAC）。
        /// 默认 false = 直连，与旧版本行为完全一致：本机代理软件常把夸克接口劫持到
        /// 127.0.0.1，且代理会改变 TLS 指纹，可能直接触发 WAF。
        /// 只有公司网络/只能经代理出口的环境才需要打开。
        /// ⚠️ 只影响**此后新建**的连接：改完要重新登录/新建任务才生效。
        /// </summary>
        public bool UseSystemProxy { get; set; }

        // ---------------- 检查更新（GitHub Releases） ----------------

        /// <summary>上次检查更新的时刻（DateTime.Ticks，本地时间）。0 = 从没查过。</summary>
        public long UpdateLastCheckTicks { get; set; }

        /// <summary>上次查到的最新版本号（"2.7.3"）；空 = 没有待提示的更新。</summary>
        public string UpdateLatestVersion { get; set; }

        /// <summary>用户点过「忽略此版本」的版本号。</summary>
        public string UpdateIgnoredVersion { get; set; }

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
        /// 复制到剪贴板的夸克分享链接是否**自动解析**（2026-10-08 新增）。默认开启。
        /// 关掉后程序根本不注册剪贴板监听（见 <see cref="MainWindow"/> 的 ApplyClipboardListener），
        /// 不是"监听但忽略"—— 关闭状态下零开销，也不存在"读用户剪贴板"这件事。
        /// </summary>
        public bool ClipboardAutoParse { get; set; } = true;

        /// <summary>
        /// <see cref="ClipboardAutoParse"/> 是否已经落过盘。
        ///
        /// 🚨 【为什么光有一个布尔不够】XmlSerializer 反序列化**不执行字段初始化器**：
        ///   老配置文件里没有 ClipboardAutoParse 节点时，属性会保持 default(bool) = false ——
        ///   也就是"老用户升级后，自动解析默认变成关闭"，与要求（默认打开）正好相反，
        ///   而且用户会觉得"你说默认开，我什么都没动怎么是关的"。
        ///   Theme 那边靠"0 = System"绕过了同样的坑，布尔类型没有等价的取巧办法，
        ///   只能额外记一个"写过没有"的标记：没写过 → 按默认值「开」处理（见 SettingsStore.Load）。
        /// </summary>
        public bool ClipboardAutoParseSet { get; set; }

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

        /// <summary>
        /// 任务列表排序：新加入的任务是否排在**列表最前面**（2026-10-13 新增）。
        /// false（默认）= 排在已有任务**后面**，即加入顺序，与旧版行为一致；
        /// true = 按时间倒序，最新的任务在最上面（见 <see cref="TaskItem.Seq"/>）。
        ///
        /// ⚠️ 默认值刻意留 false：XmlSerializer 反序列化**不执行字段初始化器**，
        ///    老配置文件里没有这个节点时属性就是 default(bool) = false，
        ///    正好等于"保持旧行为"，所以这里**不需要**像 ClipboardAutoParse 那样
        ///    额外记一个"写过没有"的标记。
        /// </summary>
        public bool TaskNewestFirst { get; set; }

        /// <summary>
        /// 任务列表的展示方式（2026-10-13 新增）：文件夹分组 / 平铺（一个个文件）。
        /// 用户要求「选择要记忆」，所以落盘。
        ///
        /// ⚠️ 枚举的 0 值刻意是 <see cref="TaskViewMode.Grouped"/>：XmlSerializer
        ///    反序列化**不执行字段初始化器**，老配置里没有这个节点时就是 default = 0，
        ///    正好等于"默认按文件夹分组、展开"。
        /// </summary>
        public TaskViewMode TaskView { get; set; } = TaskViewMode.Grouped;

        /// <summary>
        /// 任务页「运行日志」输出框的高度（像素，2026-10-21 新增）。
        ///
        /// <para>用户反馈日志框占用太多空间，要求「改为可以调整高度，并且记忆」。
        /// 现在任务页底部有一条可拖拽的分隔条，拖动即改高度，松手落盘。</para>
        ///
        /// <para>⚠️ 默认值写 0 而不是具体像素：XmlSerializer 反序列化**不执行字段初始化器**，
        /// 老配置文件里没有这个节点时就是 0 = "没设过"，
        /// 由 <c>MainWindow.NormalizeLogPanelHeight()</c> 用它回退到内置默认值。
        /// 这样既兼容老配置，也不用在这里写死一个会随界面设计变动而失效的数字。</para>
        /// </summary>
        public double LogPanelHeight { get; set; }

        /// <summary>
        /// 任务页：同一文件夹分组内，是否把**未完成的文件排在前面**（2026-10-21 新增）。
        ///
        /// <para>【为什么需要】用户反馈「假如有多个文件在文件夹下，应该优先展示未完成的文件，
        /// 因为有时候需要滚动到底部才发现」—— 一个文件夹下几十个文件时，
        /// 已完成的和正在下的混在一起，想找"还在下的那几个"得往下翻。</para>
        ///
        /// <para>【排序规则】未完成（下载中 / 排队 / 暂停 / 取消中）在前，
        /// 已完成 / 失败 / 已取消在后；两组内部再按原有的"新任务在前 / 在后"偏好排。
        /// 这样既满足"未完成的优先看到"，又不破坏用户已经习惯的时间序。</para>
        ///
        /// <para>⚠️ 默认 false：XmlSerializer 不执行字段初始化器，老配置里没这个节点
        /// 就是 false = 保持原有排序行为，不会让老用户觉得列表突然乱序。</para>
        /// </summary>
        public bool TaskUnfinishedFirst { get; set; }

        /// <summary>
        /// 任务页里**被收起**的文件夹分组名（逗号分隔，2026-10-22 新增）。
        ///
        /// <para>用户要求「记忆文件夹的展开和收起」—— 收起某个文件夹后重启程序，
        /// 它应该还是收起的；下次再打开那个文件夹下载，也不该被自动展开。</para>
        ///
        /// <para><b>为什么存"收起的"而不是"展开的"</b>：新出现的文件夹默认应该是
        /// **展开**的（用户刚下载完一个文件夹，理应马上看到内容）；
        /// 存"收起的"意味着只有用户明确收过的那几个才保持收起。</para>
        ///
        /// <para>🚨 <b>分隔符只能用 XML 合法字符</b>。曾经用 <c>'\u0001'</c>「更安全」，
        /// 结果 XmlSerializer 把它写成 <c>&amp;#x1;</c>，而 XML 1.0 连字符引用都禁止
        /// C0 控制字符（U+0000–U+001F，除 \t \n \r）—— 整份 config.xml 变成非法 XML，
        /// 下次启动读取失败，用户表现为「Cookie 没了、主题变回亮色」。
        /// 现在改用换行符 <c>\n</c>：XML 合法，且 Windows 文件夹名里不可能出现。</para>
        /// </summary>
        public string TaskCollapsedGroups { get; set; }

        // ---------------- 下载页「上次浏览位置」（2026-10-21 新增） ----------------
        //
        // 【需求】用户反馈「每次重启启动应用，下载页都会重置，能否保留上次的浏览文件位置，
        //        方便继续浏览（包括打开文件的层级）」。选型为方案 A：
        //        只记住"目录层级"（模式 + 分享标识 + 路径链 + fid 链），
        //        重启后自动重新加载该层，**不缓存文件列表本体**。
        //
        // 【为什么只存层级、不存列表】
        //   · 列表可能几千项，序列化进 config.xml 会让设置文件膨胀到几 MB，读写都变慢；
        //   · 存下来的列表一定是"旧"的，用户看到过期内容反而困惑；
        //   · 重新请求一次就能拿到最新数据，代价只是一次网络往返（本来启动也要拉目录）。
        //
        // 【失效处理】分享过期 / 网盘里被删 / 未登录 → 静默回退到根目录，只在日志里记一行。

        /// <summary>
        /// 上次是否在「分享链接」模式（false = 「我的网盘」）。
        /// <para>启动时据此决定要不要恢复分享面板与 ParseBtn 的默认模式。</para>
        /// </summary>
        public bool LastBrowseWasShare { get; set; }

        /// <summary>上次浏览的分享 ID（仅分享模式有意义，对应 URL 里的 /s/&lt;pwd_id&gt;）。</summary>
        public string LastSharePwdId { get; set; }

        /// <summary>上次浏览的分享提取码（可能为空）。</summary>
        public string LastSharePasscode { get; set; }

        /// <summary>上次浏览的分享起点 fid（分享链接自带的子目录入口，通常是 "0"）。</summary>
        public string LastShareStartFid { get; set; }

        /// <summary>
        /// 上次浏览的目录 fid（当前层）。
        /// <para>网盘模式下这是 DriveFid；分享模式下这是分享内的 fid。</para>
        /// </summary>
        public string LastBrowseFid { get; set; }

        /// <summary>
        /// 从根到当前层的**祖先 fid 链**，顺序为 [根, …, 上一层]（不含当前层）。
        /// <para>用它可以在恢复时重建 <c>_fidStack</c> 与面包屑；
        /// 只存当前 fid 是不够的 —— "上一级"和面包屑都要靠这条链。</para>
        /// </summary>
        public List<string> LastBrowseFidChain { get; set; } = new List<string>();

        /// <summary>
        /// 从根到当前层的**目录名链**，顺序为 [第一层, …, 当前层]（不含根），
        /// 与 <see cref="LastBrowseFidChain"/> 逐项对齐（链长关系见代码注释）。
        /// </summary>
        public List<string> LastBrowseNameChain { get; set; } = new List<string>();

        /// <summary>
        /// 启动时是否自动恢复上次的浏览位置（2026-10-21 新增，默认开启）。
        ///
        /// <para>⚠️ 默认 true 需要特殊处理：XmlSerializer 反序列化**不执行字段初始化器**，
        /// 老配置里没有这个节点时是 default(bool) = false，会把功能误关掉。
        /// 所以照抄 <see cref="ClipboardAutoParse"/> 的做法 —— 配一个
        /// <see cref="RestoreBrowseSet"/> 标记，在 Load 阶段判定"从没设过 → 按默认开"。</para>
        /// </summary>
        public bool RestoreBrowseOnStartup { get; set; } = true;

        /// <summary>
        /// <see cref="RestoreBrowseOnStartup"/> 是否已经落过盘。
        /// <para>用于区分"用户主动关掉了"与"老配置里没这个节点"——两者反序列化后都是 false。</para>
        /// </summary>
        public bool RestoreBrowseSet { get; set; }

        /// <summary>
        /// 上次浏览目录的**文件列表快照**（2026-10-21 新增，用户要求"把正在浏览的层级、滑动位置
        /// 存在本地，不然每次打开都重复请求，只有点击刷新才检查更新"）。
        ///
        /// <para>【与旧方案的区别】旧方案只存层级，启动时**重新请求**目录；
        /// 用户觉得"每次打开都在发请求"不可接受，要求**本地缓存优先**：
        /// 启动时直接用这份快照铺列表，一个请求都不发；
        /// 只有用户主动点「刷新」才去服务端核对最新内容。</para>
        ///
        /// <para>【只存当前这一层、只存已铺开的前若干条】不存整个目录树 ——
        /// 否则几千项的分享会把配置文件撑到几 MB。够用于"接着上次继续看"即可。</para>
        /// </summary>
        public BrowseSnapshotCache LastBrowseCache { get; set; }
    }

    /// <summary>
    /// 下载页「上次浏览位置」的文件列表快照（与 <see cref="AppSettings.LastBrowseCache"/> 配套）。
    /// 字段都做成最简单的可序列化类型，XmlSerializer 直接能处理。
    /// </summary>
    public class BrowseSnapshotCache
    {
        /// <summary>这份快照属于哪个目录（与 AppSettings.LastBrowseFid 对照，防错配）。</summary>
        public string Fid { get; set; }

        /// <summary>是否分享模式（true）/ 网盘模式（false）。</summary>
        public bool FromShare { get; set; }

        /// <summary>快照保存时刻（Ticks），用于在界面上说明"这是什么时候的数据"。</summary>
        public long SavedTicks { get; set; }

        /// <summary>
        /// 列表滚动位置（像素）。用户要求"滑动位置存在本地" ——
        /// 恢复后直接回到上次看的那一行附近，而不是停在顶部。
        /// </summary>
        public double ScrollOffset { get; set; }

        /// <summary>当时已在界面上露出的条数（分页用的游标）。</summary>
        public int RevealCount { get; set; }

        /// <summary>服务端还有没有下一页（决定底部"下面还有"提示是否出现）。</summary>
        public bool HasMore { get; set; }

        /// <summary>条目列表。</summary>
        public List<BrowseCacheItem> Items { get; set; } = new List<BrowseCacheItem>();
    }

    /// <summary>快照里的一个条目（对应 <c>ShareFileItem</c> 的持久化形态）。</summary>
    public class BrowseCacheItem
    {
        public string Fid { get; set; }
        public string Token { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }
        public bool IsDir { get; set; }
    }

    /// <summary>任务列表展示方式。</summary>
    public enum TaskViewMode
    {
        /// <summary>一个下载文件夹 = 一个可展开/收起的分组（默认）。</summary>
        Grouped = 0,
        /// <summary>全部任务平铺，一个文件一行。</summary>
        Flat = 1,
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
        /// 配置文件路径：<b>固定在 %LOCALAPPDATA%\鸽子下载\</b>。
        ///
        /// <para>【2026-10-22 变更】原先是"exe 同目录优先"（绿色便携）。改固定的根因：</para>
        /// <list type="bullet">
        /// <item>同一程序的每一份副本各写各的 state.json → <b>换目录启动就"任务不见了"</b>
        ///       （用户实测：下载是发行包发起的，记录却只在 Debug 那份里有）；</item>
        /// <item>两个副本同时下载同一文件 → 写同一份分片目录 → <b>互相踩坏</b>。</item>
        /// </list>
        /// <para>固定后所有副本共享同一份数据，任务/进度/账号/设置互通。
        /// 老的便携数据由 <see cref="Util.MigrateLegacyProfile"/> 在首次启动时搬过来。</para>
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
            string dir = Util.GetDataDir();
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
                // 建不出目录（权限/受控文件夹访问）→ 退回 exe 同目录，
                // 至少保证程序还能起来。
                dir = AppDomain.CurrentDomain.BaseDirectory;
            }
            return Path.Combine(dir, "GeZi.config.xml");
        }

        /// <summary>上一次 Load/Save 是否踩到了异常（供 UI 提示用户设置未能持久化）。</summary>
        public static string LastError { get; private set; }

        /// <summary>
        /// 本次 Load 是否**没能读到已有配置**（文件不存在 / 读失败）。
        ///
        /// <para><b>【2026-10-22 修复的配置丢失】</b>用户实测：Cookie 没了、主题变回亮色、
        /// 下载目录被改回默认值 —— 整个 config.xml 变成一份 1461 字节的**空模板**。
        /// 根因与 <c>HistoryStore</c> 是同一个 bug 模式：
        /// <c>Load()</c> 读不到时返回**新建的空 AppSettings</c>，
        /// 随后任何一次 <c>Save(_settings)</c> 就把这个空对象**写回去**，
        /// 于是登录 Cookie、主题、下载目录、线程数全被默认值覆盖。</para>
        ///
        /// <para>为什么这次触发：<c>package.ps1</c> 重新打包时删掉了程序目录
        /// （连带删掉迁移过来的 config），程序退出时 <c>OnClosed</c> 走
        /// <c>SettingsStore.Save(_settings)</c> —— 内存里那份是空对象，一写就毁。</para>
        ///
        /// <para>现在：读不到时<b>仍然返回可用的对象</b>（否则程序没法启动），
        /// 但用 <see cref="LoadedExistingConfig"/> 标记出来，
        /// 由调用方决定要不要落盘 —— <b>读不到就不写</b>，保住磁盘上已有的那份。</para>
        /// </summary>
        public static bool LoadedExistingConfig { get; private set; }

        public static AppSettings Load()
        {
            LastError = null;
            LoadedExistingConfig = false;
            var s = new AppSettings();
            try
            {
                if (File.Exists(FilePath))
                {
                    using (var fs = File.OpenRead(FilePath))
                    {
                        var loaded = Ser.Deserialize(fs) as AppSettings;
                        if (loaded != null)
                        {
                            s = loaded;
                            LoadedExistingConfig = true;
                        }
                    }
                }
                else
                {
                    // 统一目录里没文件时，兜底再看一眼**旧位置**（exe 同目录）。
                    // 【2026-10-22】以前这里是"便携优先 + LOCALAPPDATA 兜底"，
                    // 现在主路径固定在 LOCALAPPDATA，这个兜底改成救"旧位置的老用户" ——
                    // 迁移没跑成（权限/竞态）时，至少还能把老配置读出来，不至于设置全丢。
                    string alt = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GeZi.config.xml");
                    if (!string.Equals(Path.GetFullPath(alt),
                                       Path.GetFullPath(FilePath),
                                       StringComparison.OrdinalIgnoreCase)
                        && File.Exists(alt))
                    {
                        using (var fs = File.OpenRead(alt))
                        {
                            var loaded = Ser.Deserialize(fs) as AppSettings;
                            if (loaded != null)
                            {
                                s = loaded;
                                LoadedExistingConfig = true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                // 读失败：LoadedExistingConfig 保持 false → 调用方不会把它写回去
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
            // 老配置（没有 ClipboardAutoParseSet 节点）→ 按默认值「开」，
            // 而不是让 default(bool) 的 false 把功能悄悄关掉。见该属性的注释。
            if (!s.ClipboardAutoParseSet) s.ClipboardAutoParse = true;
            // 同理：「启动时恢复上次浏览位置」默认也是开
            if (!s.RestoreBrowseSet) s.RestoreBrowseOnStartup = true;
            return s;
        }

        /// <summary>
        /// 磁盘上的配置文件现在**能不能正常读出来**。
        ///
        /// <para>【2026-10-22】Save() 用它做保护：文件存在却读不出内容时，
        /// 说明它正在被外部破坏（被杀软隔离、被截断）或者损坏 ——
        /// 此时写回空配置会把仅存的好数据也毁掉，所以宁可这次不写。</para>
        /// </summary>
        private static bool CanReadConfig()
        {
            return CanReadFile(FilePath);
        }

        /// <summary>
        /// 指定文件现在能不能被 <see cref="Ser"/> 正常反序列化出来。
        ///
        /// <para>用于两处：① 写盘前判断磁盘上那份是否已损坏；
        /// ② 写盘后回读验证新文件是否合法 XML（见 Save 里的自检）。</para>
        /// </summary>
        private static bool CanReadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return true;   // 本来就没有 → 视为可用
                using (var fs = File.OpenRead(path))
                {
                    if (fs.Length == 0) return false;  // 空文件 = 损坏
                    var loaded = Ser.Deserialize(fs) as AppSettings;
                    return loaded != null;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void Save(AppSettings s)
        {
            LastError = null;
            if (s == null)
                return;

            // ⚠️ 【2026-10-22】**磁盘上已有配置、但这次没读出来**时，不写。
            //   Load() 读失败（文件被外部删掉 / 半截 / 被占用）会返回一个空对象，
            //   此时若无脑写回，就等于用空模板覆盖掉磁盘上那份好配置 ——
            //   用户实测就是这样丢了登录 Cookie、主题和下载目录。
            //
            //   判据不能只看 LoadedExistingConfig（那是 Load() 时刻的状态，
            //   Save 可能发生在别的时刻、文件状态可能已变）——
            //   所以在这里**再看一眼磁盘**：文件存在却读不出内容 → 拒绝写。
            //   文件本来就不存在（首次运行）→ 正常写。
            if (File.Exists(FilePath) && !CanReadConfig())
            {
                LastError = "配置文件读取失败，为避免覆盖已有设置，本次未写入。";
                return;
            }

            string rawCookie = s.Cookie;   // 内存中始终明文，落盘前临时加密
            var tmpPath = FilePath + ".tmp";
            try
            {
                s.Cookie = SecretProtector.Protect(rawCookie);
                // 落过盘就置位：下次 Load 才能区分"用户主动关掉了"与"老配置里没这个节点"
                // （见 ClipboardAutoParseSet 的注释）。放在这里而不是各个调用点，
                // 是为了覆盖所有写盘路径，避免漏一处就退回"默认关"。
                s.ClipboardAutoParseSet = true;

                // 同理：「启动时恢复上次浏览位置」也在这里置位，
                // 覆盖所有写盘路径（见 RestoreBrowseSet 的注释）。
                s.RestoreBrowseSet = true;

                // 先写临时文件再原子替换：避免写一半断电/异常留下半截 XML，
                // 下次启动 Load 直接抛异常、用户设置全丢。
                using (var fs = File.Create(tmpPath))
                    Ser.Serialize(fs, s);

                // 🚨 【2026-10-22 加了这道自检】序列化**成功**不等于写出来的 XML **合法**：
                //   XmlSerializer 会把非法控制字符写成 &#x1; 这种字符引用，
                //   而 XML 1.0 连字符引用都禁止 C0 控制字符 —— 文件当场变成非法 XML，
                //   下次 Load 直接 ParseError，整份配置（Cookie / 主题 / 下载目录）全丢。
                //   教训：曾经因为「已收起分组名」用 '\u0001' 做分隔符而整份配置报废。
                //
                //   所以：**写完立刻回读验证**，读不回来就中止，绝不覆盖磁盘上那份好配置。
                if (!CanReadFile(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { }
                    LastError = "序列化结果无法回读（含 XML 非法字符），本次未写入，已保留原配置。";
                    try
                    {
                        if (File.Exists(FilePath))
                        {
                            var bak = FilePath + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
                            File.Copy(FilePath, bak, true);
                        }
                    }
                    catch { }
                    return;
                }

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
