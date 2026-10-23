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
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;   // ToggleButton（续传列表的真复选框）
using System.Windows.Data;                  // Binding（同上）
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GeZi.Core.Api;
using GeZi.Core.Download;
using GeZi.Core.Models;
using GeZi.Core.Support;
using GeZi.Core.Update;
using System.Text.RegularExpressions;

// 说明：MainWindow 里既有 XAML 生成的字段，也有代码动态构建的窗口（如续传询问、
// 诊断面板）。动态构建需要 System.Windows.Controls 下的 Grid/Button/ListBox 等，
// 所以这里必须 using —— WPF 项目不会自动带入。
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using SelectionMode = System.Windows.Controls.SelectionMode;
using MessageBox = System.Windows.MessageBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Run = System.Windows.Documents.Run;
using TextTrimming = System.Windows.TextTrimming;
// 加载态用到的类型与已有名称冲突，统一取别名
using DiagClock = System.Diagnostics.Stopwatch;
using ShapePath = System.Windows.Shapes.Path;   // 避免与 System.IO.Path 撞名

namespace GeZi
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<ShareFileItem> _files = new ObservableCollection<ShareFileItem>();
        private readonly ObservableCollection<TaskItem> _tasks = new ObservableCollection<TaskItem>();

        /// <summary>任务列表的默认视图 —— 开关「文件夹分组」时直接改它的 GroupDescriptions。</summary>
        private ListCollectionView _taskView;
        private readonly List<string> _pathList = new List<string>();
        private readonly Stack<string> _fidStack = new Stack<string>();

        /// <summary>
        /// 分享链接带子目录（<c>#/list/share/&lt;fid&gt;</c>）时，**那个目录本身的名字**。
        /// 解析时从分享凭证的 title 推断（分享标题默认就是被分享文件夹的名字），
        /// 只用于任务页的「文件夹分组」显示，不参与目标路径拼接。
        /// </summary>
        private string _shareRootName = "";

        // ---------------- 托盘 / 小窗 / 退出（2026-10-03） ----------------
        /// <summary>托盘图标（常驻通知区）。</summary>
        private TrayIcon _tray;
        /// <summary>悬浮小窗（按需创建，关掉后置空）。</summary>
        private MiniWindow _mini;
        /// <summary>小窗拖动中的坐标缓存（只在关闭时落盘一次，避免拖动时疯狂写配置）。</summary>
        private double _miniLeft, _miniTop;
        /// <summary>是否正在走"真退出"流程。关闭事件里靠它放行，避免被再次拦下。</summary>
        private bool _exiting;
        /// <summary>托盘气泡只提示一次（首次隐藏时），之后不再打扰。</summary>
        private bool _trayHintShown;

        /// <summary>
        /// 一个模式的导航快照。
        /// </summary>
        /// <remarks>
        /// 【2026-10-03】用户反馈：「解析完链接之后点击我的网盘再返回这里，发现还需要重新解析，
        /// 没有显示解析后的文件」。根因是 <see cref="OnModeChanged"/> **无条件**调 ResetNav()，
        /// 把 _files/_fidStack/_pathList 全清了，切回来时分享状态已经没了。
        /// 现在两种模式各自留一份快照，来回切换时**原样恢复**（连列表内容都不重新请求）。
        /// </remarks>
        private sealed class NavSnapshot
        {
            public List<ShareFileItem> Files = new List<ShareFileItem>();
            public string CurFid = "0";
            public List<string> FidStack = new List<string>();
            public List<string> PathList = new List<string>();
            public string PwdId;
            public string Stoken;
        }

        /// <summary>「分享」模式与「我的网盘」模式各自的导航快照。</summary>
        private readonly NavSnapshot _shareNav = new NavSnapshot();
        private readonly NavSnapshot _driveNav = new NavSnapshot();

        /// <summary>文件列表的筛选视图 + 当前筛选词（空 = 不筛选）。</summary>
        private System.ComponentModel.ICollectionView _filesView;
        private string _fileFilter = "";

        /// <summary>
        /// 是否正在加载目录。仅供皮肤层区分「加载中」与「真的空」两种状态 ——
        /// 不影响任何下载/请求逻辑。见 UpdateEmptyStates()。
        /// </summary>
        private volatile bool _loadingDir;

        /// <summary>
        /// 正在用代码给设置控件赋值（LoadSettingsToUi）时为 true。
        /// 挡住控件赋值引发的事件回调，避免启动时把默认值写回、覆盖用户配置。
        /// </summary>
        private bool _applyingSettings;

        /// <summary>待清理的网盘文件：fid + 是否来自「转存」路径。</summary>
        /// <remarks>
        /// 只有「转存→取直链」路径才会在用户网盘里写入一份中转文件，下载完应当清理；
        /// 「免转存」与「我的网盘」路径的 fid 指向的是分享/原文件，绝不能跟着一起删
        /// （否则会把别人分享里的源文件、或用户自己的原文件误删）。故清理时按
        /// <see cref="IsTransferred"/> 过滤，只删真正是我们写进去的中转件。
        /// </remarks>
        private sealed class CleanupEntry
        {
            public string Fid;
            public bool IsTransferred;
        }

        private readonly List<CleanupEntry> _pendingCleanup = new List<CleanupEntry>();
        private readonly object _cleanupLock = new object();
        private QuarkClient _client;

        /// <summary>
        /// 启动时那次自动登录的任务（可能是 null：没有保存的 Cookie 时不会启动）。
        /// 续传要用它来「等登录态就绪」——见 <see cref="EnsureClientForResumeAsync"/>。
        /// </summary>
        private System.Threading.Tasks.Task _startupLoginTask;
        private string _pwdId;
        private string _stoken;
        private string _curFid = "0";

        // ==================== stoken 过期自愈用的稳定快照（2026-10-20） ====================
        // _curFid 会随用户导航变化（进子目录就变），不能当"分享进入点"用；
        // 提取码框里的内容用户也可能改。所以要单独记一份"解析这个分享时的起点"，
        // 供 TryReResolveShareTokenAsync 从正确位置逐级下钻。

        /// <summary>解析分享时用的提取码（无码为空串）。重解析分享时需要。</summary>
        private string _sharePasscode = "";

        /// <summary>分享进入目录的 fid（"0" = 根）。重解析时的下钻起点。</summary>
        private string _shareStartFid = "0";
        private AppSettings _settings;
        private DownloadScheduler _scheduler;

        /// <summary>直链短时缓存（TTL 600s），避免重复取链触发夸克限流。见 LinkCache。</summary>
        private readonly LinkCache _linkCache = new LinkCache();

        // ---------------- 剪贴板自动解析（2026-10-08） ----------------

        /// <summary>
        /// 剪贴板监听注册在哪个窗口句柄上（IntPtr.Zero = 没注册）。
        ///
        /// 【为什么记句柄而不是记一个 bool】注册是**绑在原生句柄上**的，
        /// 而窗口隐藏→恢复之间句柄有可能被重建（见 <see cref="AttachWindowHooks"/>）。
        /// 只记"已注册/未注册"，句柄一换就会误判成"已注册"而不再重挂。
        /// </summary>
        private IntPtr _clipboardHookHwnd;

        /// <summary>
        /// 「跨进程激活消息钩子 + 剪贴板监听」已经挂在哪个窗口句柄上
        /// （IntPtr.Zero = 还没挂）。见 <see cref="AttachWindowHooks"/>。
        /// </summary>
        private IntPtr _hooksOnHwnd;

        /// <summary>
        /// 上一次因剪贴板自动解析处理过的分享 ID。
        ///
        /// 【为什么需要去重】一次"复制"并不保证只来一条 <c>WM_CLIPBOARDUPDATE</c> ——
        /// 有些程序（浏览器扩展、剪贴板管理器）会把同一份内容分多次写剪贴板，
        /// 于是通知会连发好几条。不去重的话每次都要重发换 stoken + 拉目录两个请求，
        /// 白等一次，还有被夸克风控盯上的风险。
        /// </summary>
        private string _clipboardLastPwdId;

        /// <summary>
        /// 从剪贴板文本里圈出候选 URL 的正则。
        ///
        /// 只负责"把看起来像 URL 的片段切出来"，**不判断是不是夸克** ——
        /// 域名那一步交给 <see cref="IsQuarkHost"/>。排除集与「粘贴进链接框」时用的
        /// 保持同一份（<see cref="OnLinkBoxPasting"/>），免得两处对"URL 到哪结束"的
        /// 理解不一致。
        /// </summary>
        private static readonly Regex ClipboardUrlRx =
            new Regex(@"https?://[^\s""'<>）】\]]+", RegexOptions.Compiled);

        /// <summary>
        /// 剪贴板文本超过这么多字符就**不看**（约 2 万字符）。
        /// 「随手复制的分享链接」撑死几百字符；一份几十万字的剪贴板内容不可能是它，
        /// 而在 UI 线程上为它跑一遍正则纯属自找卡顿。
        /// </summary>
        private const int MaxClipboardScanChars = 20000;

        private bool ShareMode => ShareModeRadio.IsChecked == true;

        // ---------------- 单次处理文件数保护 ----------------
        // 沿用早期原型定下的 MAX_FILES / CONFIRM_THRESHOLD。
        //
        // 为什么要有这道阀：一次误操作（比如选中一个几万文件的分享目录、或"全选"）
        // 会连带触发几万次取链请求 + 几万个下载任务的目录创建，轻则把自己账号
        // 送进服务端风控，重则把目标盘写爆。Python 版对此是"硬拒 + 超量确认"两道，
        // 这里保持同样语义，不做自动截断 —— 静默丢掉用户选的文件比直接报错更糟。
        //
        // 【单一事实来源】上限与确认阈值都只在这里定义一处，
        // 不要再在别处写死 1000，否则改一处漏一处就会出不一致的行为。
        private const int MaxFiles = 1000;          // 单次处理文件数上限（硬拒）
        private const int ConfirmThreshold = 100;   // 超过该数量时先弹确认（软提醒）

        public MainWindow()
        {
            InitializeComponent();
            FileList.ItemsSource = _files;
            // 当前目录的筛选视图（名称包含关键字才显示）。空关键字 = 全部显示。
            _filesView = System.Windows.Data.CollectionViewSource.GetDefaultView(_files);
            _filesView.Filter = o =>
            {
                var f = o as ShareFileItem;
                if (f == null) return false;
                var w = _fileFilter ?? "";
                return w.Length == 0 ||
                       (f.Name ?? "").IndexOf(w, StringComparison.CurrentCultureIgnoreCase) >= 0;
            };
            TaskList.ItemsSource = _tasks;
            // 拿默认视图的引用（与 ItemsSource 用的是同一个对象），供「文件夹分组」开关用。
            _taskView = CollectionViewSource.GetDefaultView(_tasks) as ListCollectionView;
            ShareModeRadio.IsChecked = true;

            // 侧栏导航（ListBox）与内容区（TabControl）的初始对齐。
            // 顺序很重要：先建好账号卡初始态，再让选中项落到第 0 页 —— 这样
            // 后续任何 Tabs.SelectedIndex 赋值都会经由 OnTabsSelectionChanged
            // 反过来把 NavList 同步到位，两边永远不会不同步。
            UpdateAccountCard(null, 0, 0);
            NavList.SelectedIndex = 0;
            Tabs.SelectedIndex = 0;
            Tabs.SelectionChanged += OnTabsSelectionChanged;

            // 窗口句柄一（重）建好，就把「跨进程激活消息钩子 + 剪贴板监听」挂上去。
            //
            // ⚠️ 用 SourceInitialized 而不是 Loaded：它正是"HWND 刚刚存在"的时刻
            //    （项目里 ScanQrWindow 给窗口加圆角也是用这个事件），而且**只要新建了
            //    句柄就会再触发一次** —— 窗口隐藏到托盘、再恢复时如果句柄被重建，
            //    这里会跟着重挂，注册不会静默失效。见 AttachWindowHooks。
            SourceInitialized += (s, e) => AttachWindowHooks();

            // 空状态提示：集合有变化就同步一次可见性。
            // 不改动任何业务逻辑，只是"列表为空时显示插画"这一层皮肤。
            // ⚠️ 走**合并**版（RequestUpdateEmptyStates）：分页铺列表时会连着变很多次，
            //    每次都在里面整表数一遍是 O(n²)，几千项的目录会直接卡死。
            _files.CollectionChanged += (s, e) => RequestUpdateEmptyStates();

            // 文件列表快滑到底部时继续加载下一页（分页逻辑见 LoadDirAsync / LoadMoreItems）。
            // 用 AddHandler 挂 ScrollChanged：不必去可视树里翻 ListBox 模板生成的那个
            // ScrollViewer —— 事件里的 OriginalSource 就是它。
            FileList.AddHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent,
                new System.Windows.Controls.ScrollChangedEventHandler(OnFileListScrollChanged));
            _tasks.CollectionChanged += (s, e) =>
            {
                UpdateEmptyStates();
                // 【2026-10-03】任务被移出列表时必须同步「未完成任务」记录，
                // 否则删掉的任务下次启动还会被当成可续传任务弹出来。
                // 放在这里而不是各个删除点，是为了覆盖所有移除路径。
                // SyncPending 内部有签名节流，集合频繁变化也不会反复写盘。
                SyncPending();
                // 任务列表快照（含已完成记录）：增删也要落盘
                RequestSaveTaskList();
            };
            // 筛选视图变化也要刷新（筛选后可能一条都不剩）
            _filesView.CollectionChanged += (s, e) => UpdateEmptyStates();

            // 【2026-10-22】分组展开状态的恢复。
            // 分组容器（GroupItem）是**虚拟化**生成的 —— 启动时列表还没布局完，
            // 那一刻拿不到任何 GroupItem，更别说里面的 Expander。
            // 所以用一个小定时器反复试：拿到容器就按记忆恢复，恢复了就停。
            // 用定时器而不是 LayoutUpdated：后者每次布局都触发，太频繁。
            _groupStateTimer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(400),
                System.Windows.Threading.DispatcherPriority.Background,
                (s3, e3) => TryRestoreGroupExpandedStates(),
                Dispatcher);
            _groupStateTimer.Start();

            // 日志攒批刷新器：把高频日志合并成一次 UI 写入（见 Log 的注释）。
            // 用 Background 优先级 —— 让进度条等更要紧的 UI 更新先跑。
            _logFlushTimer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(LogFlushMs),
                System.Windows.Threading.DispatcherPriority.Background,
                (s, e) => FlushLogs(),
                Dispatcher);
            _logFlushTimer.Start();

            // 任务列表快照的节流写盘（2026-10-13）：进度每 500ms 报一次，不能跟着写盘。
            // ⚠️ DispatcherTimer 的 4 参构造会**立刻启动**，这里创建后马上 Stop ——
            //    只在真的有变化时由 RequestSaveTaskList 启动，避免空转。
            _taskSaveTimer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromSeconds(3),
                System.Windows.Threading.DispatcherPriority.Background,
                (s, e) =>
                {
                    _taskSaveTimer.Stop();
                    SaveTaskListNow();
                },
                Dispatcher);
            _taskSaveTimer.Stop();

            // ---- 保活：下载期间阻止系统空闲睡眠 ----
            //
            // 【为什么用独立定时器，而不是挂在 OnJobUpdate 上】
            // 任务**卡死**时（worker 卡在掐不断的调用里 → `Task.WhenAll` 永不返回）
            // 状态会永远停在 Downloading，而进度回调也可能就此停摆 ——
            // 挂在回调上等于**永不复查**，电脑会整夜不睡。
            // 独立定时器每 30 秒复查一次，卡死也能在"无进展超时"之后释放保活。
            //
            // 【为什么不做成开关】用户明确要求「不需要单独按钮」→ 自动跟随任务状态。
            _sleepGuardTimer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromSeconds(SleepGuardTickSec),
                System.Windows.Threading.DispatcherPriority.Background,
                (s, e) => RefreshSleepGuard(),
                Dispatcher);
            _sleepGuardTimer.Start();
            // 把保活的状态变化写进日志 —— 用户能看到"为什么电脑没睡"（不做开关，但保留知情权）。
            SleepGuard.Log = Log;

            // 「总」进度日志：把所有任务**汇总成一行**，每 10 秒最多一条。
            //
            // 【为什么要它】原来核心层是**逐任务**每 5 秒打一行
            // 「运行中: 0 B/s，活跃连接 49/49，分片进度 0/49，命中 IP …」——
            // 11 个任务就是每分钟 130+ 行，把"开始/完成/失败"这些真正有用的行全冲掉了
            // （用户：「日志里全是这玩意根本没有用，普通用户也不会看」）。
            // 现在核心那条默认关掉（`SegmentedDownloader.PerTaskDiagLog`），
            // 这里改成**一行总量**；而且内容没变就不重复打（空闲时不会刷屏）。
            _progressLogTimer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromSeconds(ProgressLogSec),
                System.Windows.Threading.DispatcherPriority.Background,
                (s2, e2) => LogAggregateProgress(),
                Dispatcher);
            _progressLogTimer.Start();

            // 定时刷容量（用户要求）。与保活心跳同频 5 分钟 —— 下载会改变已用空间，
            // 光靠启动那一次拉取，卡片上的容量会一直是旧的。
            _capacityTimer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromSeconds(CapacityRefreshSec),
                System.Windows.Threading.DispatcherPriority.Background,
                (s3, e3) => OnCapacityTick(),
                Dispatcher);
            _capacityTimer.Start();

            _settings = SettingsStore.Load();
            // 代理策略必须在创建 QuarkClient / 下载器之前写入核心层，
            // 否则启动时第一次登录仍会按旧值直连。
            NetworkOptions.UseSystemProxy = _settings.UseSystemProxy;

            // 【2026-10-13】把上次的任务列表铺回来（含已完成记录与未完成的进度）。
            // 必须在 LoadSettingsToUi 之前：恢复时要按「新任务在前/在后」重排。
            // 出错绝不能拦住启动，所以内部全包了 try/catch。
            RestoreTaskList();

            // 确定一个「真正可写」的下载目录：已保存的 → 真实下载目录 → 本地应用数据目录 → 程序目录
            string resolved = null;
            string[] candidates =
            {
                _settings.OutDir,
                Path.Combine(Util.GetDownloadsFolder(), "鸽子下载自用版"),
                Util.GetLocalAppDataDir(),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads"),
            };
            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c) || !Util.IsWritable(c))
                    continue;
                resolved = c;
                break;
            }
            if (resolved == null)
                resolved = Util.GetLocalAppDataDir();

            if (!string.Equals(resolved, _settings.OutDir, StringComparison.OrdinalIgnoreCase))
            {
                _settings.OutDir = resolved;
                SettingsStore.Save(_settings);
                Log("下载目录不可写（可能是 Windows「受控文件夹访问」拦截），已改用: " + resolved);
            }

            _scheduler = new DownloadScheduler(_settings.ConcurrentTasks);
            _scheduler.Log = LogFromWorker;
            _scheduler.Diagnostics = OnDiagnostics;
            LoadSettingsToUi();
            // 【2026-10-23】把更新模块的诊断接到「运行日志」。
            //
            // ⚠️ 一律走 LogDetail（详细级）：这些是**排查用**的内部状态
            // （扫目录层数、token 刷新、直链参数……），
            // 普通用户点「检查更新」只需要看到「发现新版本」或错误，不需要看过程。
            // 需要排查时点日志框右上角「详细」按钮即可全部显示。
            GeZi.Core.Update.QuarkReleaseChecker.Log = LogDetail;
            GeZi.Core.Update.UpdateInstaller.Log = LogDetail;
            UpdateService.Log = LogDetail;
            // 🚨 【2026-10-23】把"运行期有效 Cookie"的取法交给更新服务。
            //
            // 为什么必须这样：`_settings.Cookie` 只在**登录那一刻**写入，
            // 冷启动时它是几天前的快照；而夸克的 __puus 会随请求滚动更新，
            // 真正有效的那份在 `_client.CookieStr`。
            // 实测：用手动下载能成功（1986 字节），但一键更新 403
            // （服务端原文 `require login [auth expired]`），因为它拿到的是
            // 1865 字节的旧值。
            //
            // 顺序与主窗口其它地方一致（见 ShowAccountsDialog / 导出 Cookie）：
            //   ① _client.CookieStr（运行期最新）
            //   ② _settings.Cookie（登录时保存的，可能过期）
            UpdateService.CookieProvider = () =>
            {
                try
                {
                    if (_client != null && !string.IsNullOrEmpty(_client.CookieStr))
                        return _client.CookieStr;
                }
                catch { }
                return _settings != null ? _settings.Cookie : "";
            };
            // 🚨 【2026-10-23】把主窗口那个**长期存活**的 client 交给更新流程复用。
            //
            // 【为什么必须复用，而不是新建一个】
            // 实测（同一份配置、同一个包、Cookie 长度都是 1986）：
            //   · 更新流程自己 new QuarkClient → 取链成功，但下载阶段**稳定 412**
            //     （Tengine Precondition Failed），连点三次全失败；
            //   · 手动在分享页下载（用本窗口的 _client）→ 立刻 206 成功；
            //   · 手动下载成功之后再点「一键更新」→ **也成功了**。
            //
            // 差别不在 Cookie 字符串（长度完全相同），而在于 `_client` 是
            // **一直在用**的会话：它内部的 jar 收过服务端滚动下发的
            // __puus/__kp/__kps 等字段，而新建的 client 只有配置里那份静态快照。
            // 夸克 CDN 的 OAuth 回调据此判断"是不是活跃会话"，
            // 于是对新建会话的直链在真正下载时返回 412。
            //
            // 复用之后，"一键更新"与"手动下载"走的是完全同一条链路。
            UpdateService.SharedClientProvider = () => _client;
            // 检查更新：注入配置，恢复上次的红点状态（不在这里发网络请求）
            UpdateService.Attach(_settings);
            // Attach 之后 HasUpdate 才有值（LoadSettingsToUi 跑在 Attach 之前，
            // 那次刷新只能填上版本号）。这里再刷一次，把红点/状态行补对。
            RefreshSettingsUpdateUi();

            // 加载完成后再提示未完成任务（此时窗口已就绪，弹窗才有 Owner）
            Loaded += (s, e) =>
            {
                // 确保主窗真的显示在最前。此前的现象是"托盘有图标但主窗不弹出"，
                // 根因在单实例激活逻辑（见 App.ActivateExistingInstance），这里再兜一道：
                // 即便窗口因任何原因处于隐藏/最小化，也强制恢复。
                try
                {
                    if (!IsVisible) Show();
                    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                    Activate();
                }
                catch { }

                // 先启动分片清扫（后台线程）。
                // 清扫只回收"超过阈值没被碰过"的孤儿分片，不会动正在续传的，
                // 但顺序上仍让清扫先跑，避免把刚被判定为待续的分片误伤（mtime 很新，实际不会）。
                StartPartsSweep();

                // 【2026-10-13 移除】原来这里会用 Idle 优先级弹「未完成的任务」询问框。
                // 现在任务页本身就会把上次的任务（含未完成的进度）恢复成列表行，
                // 点行上的 ▶ 即可续传 —— 再弹一个询问框只是在重复问同一件事。

                // 窗口圆角（用户反馈「整个软件窗口四个角太尖了，微微加一些圆角」）。
                // ⚠️ **必须放在 Loaded 里**：它要拿窗口句柄（HWND），
                // 而 HWND 到 Loaded 才存在。曾经写在构造函数里 →
                // `WindowInteropHelper.Handle` 是 IntPtr.Zero → 函数静默返回，
                // 表现为"加了代码但窗口依然没有圆角"（连日志都没打）。
                ApplyWindowRoundedCorners();

                // 【2026-10-21】日志高度再应用一次：
                // LoadSettingsToUi 跑在构造函数阶段，那时 ActualHeight 还是 0，
                // ClampLogPanelHeight 只能用 700 兜底估上限 —— 大屏上恢复出来的高度
                // 会被这个估值错误地夹小。到 Loaded 时窗口尺寸已经确定，这里重算一次。
                ApplyLogPanelHeight();

                // 【2026-10-21】恢复下载页上次的浏览位置（模式 + 目录层级）。
                // 放在 Loaded 里而不是构造函数：需要窗口已就绪（会有日志与 UI 反馈），
                // 而且要在自动登录**之后**才有 _client —— 恢复流程内部会等登录任务。
                _ = RestoreBrowsePositionOnStartupAsync();

                // 托盘图标（用户要求加后台功能）。
                // ⚠️ 也必须在 Loaded 里：NotifyIcon 需要一个窗口句柄来收任务栏消息，
                //    而 WPF 窗口句柄到 Loaded 才存在。
                InitTray();

                // 启动检查更新：静默执行，有新版本只点亮关于页红点 + 弹一次气泡，
                // 不打断用户；失败（断网/接口不通）什么也不做。
                _ = UpdateService.RunStartupCheckAsync(_settings, OnUpdateFound);

                // 【2026-10-08】「再点一次 exe 把窗口拉起来」的消息钩子、以及
                // 「剪贴板自动解析」的监听，都不在这里挂了 —— 它俩都绑在窗口句柄上，
                // 统一交给 AttachWindowHooks（订阅在构造函数里，走 SourceInitialized）。
            };

            if (!string.IsNullOrEmpty(_settings.Cookie))
            {
                Log("正在自动登录…");
                // 记下这个任务：续传前要等它（见 EnsureClientForResumeAsync）。
                _startupLoginTask = ApplyLoginAsync(_settings.Cookie);
            }
            else
            {
                Log("就绪。未登录：可用「分享链接」模式下载公开分享，或到「登录」页扫码。");
            }

            // 【2026-10-02 修复】首屏必须主动刷一次空状态。
            // 两个空状态（文件列表、任务列表）在 XAML 里初始 Visibility=Collapsed，
            // 只靠 LoadDirAsync/集合变化去点亮。而启动时若未登录、又没粘贴链接，
            // LoadDirAsync 根本不会跑 → 列表区里两个状态都不显示，只剩一片灰底
            // （用户截图反馈的"这里空这么一大块"）。
            UpdateEmptyStates();

            // 【2026-10-03 修复】合并后的「暂停全部/恢复全部」按钮，Content 是代码里
            // 生成的（XAML 只留了一个空 Button）。不在这里刷一次的话，启动后它就是个
            // **空白方块**（用户截图反馈"这里怎么回事儿"）。
            UpdatePauseAllButton(force: true);
        }

        // ---------------- 窗口圆角 ----------------
        //
        // WPF 自己没法给顶层窗口加圆角（除非开 AllowsTransparency，那会关掉硬件加速、
        // 影响最大化行为，对一个持续刷新的下载界面不划算）。
        // Windows 11 的 DWM 提供了原生圆角，一次调用即可，且由系统裁剪内容、不留锯齿。

        /// <summary>
        /// 给窗口加系统级圆角（实现在 <see cref="DialogChrome.ApplyRoundedCorners"/>，
        /// 小窗共用同一份）。Win11 生效；Win10 上静默忽略 —— 圆角只是观感，
        /// 不该因为它让程序出问题，也**不写日志**（成功是常态，写进日志只是噪音）。
        /// </summary>
        private void ApplyWindowRoundedCorners()
        {
            DialogChrome.ApplyRoundedCorners(this);
        }

        /// <summary>
        /// 主窗消息钩子，处理两条消息：
        ///   · <see cref="App.WmActivateMain"/>：另一个实例请我把窗口拉起来；
        ///   · <see cref="NativeMethods.WM_CLIPBOARDUPDATE"/>：剪贴板内容变了
        ///     （只在设置里开着「剪贴板自动解析」时才注册，见 <see cref="ApplyClipboardListener"/>）。
        ///
        /// 为什么让**收到消息的旧进程**自己激活，而不是让新进程去 SetForegroundWindow：
        /// Windows 的前台锁定不允许后台进程抢焦点，新进程调用往往只让任务栏闪一下。
        /// 旧进程是被"用户操作"触发的，激活才有效。
        /// </summary>
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == App.WmActivateMain)
            {
                RestoreFromTray();
                handled = true;
            }
            else if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
            {
                // ⚠️ 不置 handled：这是系统的广播性质通知，本程序只是订阅者之一，
                //    没有理由把它吃掉（也避免将来其它组件想接同一条消息时被拦）。
                OnClipboardChanged();
            }
            return IntPtr.Zero;
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _logFlushTimer?.Stop(); } catch { }
            try { _sleepGuardTimer?.Stop(); } catch { }
            // 【2026-10-22】分组展开状态恢复用的节流定时器
            try { _groupStateTimer?.Stop(); } catch { }
            // 关窗前把任务页的最终状态落盘（节流定时器可能还没到点）。
            try { _taskSaveTimer?.Stop(); } catch { }
            SaveTaskListNow();
            // 释放保活。其实进程退出时线程级状态会自动释放，这里显式写更清楚，
            // 也避免"关窗后进程还活着"的路径（如托盘常驻）把保活留着。
            try { SleepGuard.Release(); } catch { }
            try { _client?.StopKeepAlive(); } catch { }
            // 关掉边下边播的本地 HTTP 服务（仍在播放的播放器会随之断开）
            try { LocalStreamServer.Instance.Dispose(); } catch { }
            // ⚠️ 托盘图标必须释放：不释放的话进程退出后图标会留在通知区，
            //    鼠标划过去才消失（最经典的托盘 bug）。
            try { _tray?.Dispose(); _tray = null; } catch { }
            try { _mini?.Shutdown(); } catch { }
            // 摘掉剪贴板监听：进程退出时系统也会回收这条注册，但显式摘掉更干净，
            // 且与 SourceInitialized 里的注册成对出现（窗口在托盘常驻期间一直持有它）。
            try
            {
                if (_clipboardHookHwnd != IntPtr.Zero)
                {
                    NativeMethods.RemoveClipboardFormatListener(_clipboardHookHwnd);
                    _clipboardHookHwnd = IntPtr.Zero;
                }
            }
            catch { }
            base.OnClosed(e);
        }

        // ---------------- 基础 ----------------

        /// <summary>
        /// 一条日志：文本 + 是否属于「详细」级（2026-10-20 新增两级日志）。
        ///
        /// <para>【为什么分两级】目录加载、连接分配、缓存命中等过程细节每秒钟几十行，
        /// 全打出来会把真正有用的（开始/完成/报错）淹掉。默认只显示 <c>Detail=false</c> 的
        /// 日常信息，用户点「详细」才把 <c>Detail=true</c> 的过程细节一并显示。</para>
        /// </summary>
        private sealed class LogLine
        {
            public string Text;
            public bool Detail;
        }

        /// <summary>待刷入 LogBox 的日志行（跨线程入队，UI 定时器出队）。</summary>
        private readonly System.Collections.Concurrent.ConcurrentQueue<LogLine> _logQueue =
            new System.Collections.Concurrent.ConcurrentQueue<LogLine>();

        /// <summary>
        /// 全量日志历史（含详细级）。切「详细」时要能把之前被过滤掉的行补显出来，
        /// 所以不能只留 LogBox 里那份文本 —— 必须另存一份完整列表。
        /// </summary>
        private readonly List<LogLine> _logAll = new List<LogLine>();

        /// <summary>是否显示详细日志（「详细」按钮切换）。默认关 = 只看日常信息。</summary>
        private bool _logVerbose;

        /// <summary>日志攒批刷新器。见 <see cref="Log"/> 的注释。</summary>
        private System.Windows.Threading.DispatcherTimer _logFlushTimer;

        /// <summary>保活复查定时器：有任务活跃下载时阻止系统空闲睡眠。见 <see cref="RefreshSleepGuard"/>。</summary>
        private System.Windows.Threading.DispatcherTimer _sleepGuardTimer;

        /// <summary>保活复查间隔（秒）。30 秒足够——系统空闲睡眠的阈值通常是分钟级。</summary>
        private const int SleepGuardTickSec = 30;

        /// <summary>
        /// 「无进展超时」（分钟）：任务处于 Downloading/Queued 但这么久没有前进，
        /// 就认定它**卡死**了，不再为它保活（让电脑照睡，下次启动会由
        /// 「未完成的任务」对话框提醒用户）。
        ///
        /// 为什么是 5 分钟：核心层已有 45 秒的停滞看门狗，但那条走的是"任务失败"路径；
        /// 这里兜的是**看门狗也掐不断**的卡死（worker 卡在不响应令牌的调用里）。
        /// 合并阶段虽然 `Done` 不增长，但有 `Phase`（"正在合并分片"）兜底，不会被误判。
        /// </summary>
        private const int SleepGuardStaleMinutes = 5;

        /// <summary>日志刷入 UI 的间隔（毫秒）。200ms 对肉眼已近乎实时。</summary>
        private const int LogFlushMs = 200;

        /// <summary>窗口隐藏到托盘时的日志刷新间隔（没人在看，降频省点 CPU）。</summary>
        private const int LogFlushMsHidden = 2000;

        /// <summary>
        /// 日志**入队**，不直接写控件 —— 由 <see cref="_logFlushTimer"/> 攒批刷入 LogBox。
        ///
        /// 为什么要攒批：原实现是每条日志一次
        /// `LogBox.AppendText(...) + ScrollToEnd()`，而 ScrollToEnd 会触发布局重算；
        /// 且每条都要切一次 UI 线程。512 并发时日志量不小，等于持续往**单线程的 UI**
        /// 上灌小任务 —— 界面就会卡。
        /// 参考 Python Flet 版：它用 `_process_q` 消息泵攒批刷 UI。
        ///
        /// 入队（ConcurrentQueue）本身线程安全，所以下载线程可直接调用。
        /// </summary>
        private void Log(string msg)
        {
            EnqueueLog(msg, detail: false);
        }

        /// <summary>
        /// 详细级日志：只在用户点开「详细」时显示（过程细节，如目录加载、连接分配、缓存命中）。
        /// </summary>
        private void LogDetail(string msg)
        {
            EnqueueLog(msg, detail: true);
        }

        /// <summary>把一条日志入队（线程安全，下载线程可直接调用）。</summary>
        private void EnqueueLog(string msg, bool detail)
        {
            try
            {
                _logQueue.Enqueue(new LogLine
                {
                    Text = DateTime.Now.ToString("HH:mm:ss") + "  " + msg,
                    Detail = detail,
                });
            }
            catch { }
        }

        /// <summary>来自下载工作线程的日志。入队线程安全，无需再切 UI 线程。</summary>
        private void LogFromWorker(string msg)
        {
            // 下载内核的日志整体算「专业级」：分片规划、连接分配、直链刷新等
            // 每秒可能几十行，默认藏起来，点「详细」才看。
            //
            // 【2026-10-22】例外：以 "[诊断·" 开头的行是排查用的关键结论
            // （探测失败码、请求头构成等），数量少且只在异常时出现 ——
            // 若也藏进「详细」，用户按普通模式根本看不到，等于白加。
            bool detail = !(msg != null && msg.StartsWith("[诊断·", StringComparison.Ordinal));
            EnqueueLog(msg, detail);
        }

        /// <summary>LogBox 保留的最大行数（超出后从头砍）。</summary>
        /// <remarks>
        /// 【2026-10-03 新增】加了"后台驻留"之后这条变得必要：以前关闭=退出，
        /// 日志最多活一次会话；现在窗口一收就能挂好几天，日志会**无限增长**，
        /// 既吃内存又让 TextBox 越来越卡（每次 AppendText 都要重排）。
        /// 2000 行足够回溯一次下载的完整过程，再多也没人翻。
        /// </remarks>
        private const int LogMaxLines = 2000;

        /// <summary>
        /// 网盘剩余容量低于这个值就直接把容量条判红（1 GB）。
        /// 与"占用率 ≥ 90%"是**或**的关系 —— 见 <see cref="UpdateAccountCard"/> 里的说明。
        /// </summary>
        private const long LowFreeBytes = 1024L * 1024L * 1024L;

        /// <summary>定时刷容量的间隔（秒）。5 分钟，与保活心跳同频。</summary>
        private const int CapacityRefreshSec = 300;

        /// <summary>定时刷容量的定时器。</summary>
        private System.Windows.Threading.DispatcherTimer _capacityTimer;

        /// <summary>登录是否已被判定为失效（失效后停止刷容量/心跳，等用户重新登录）。</summary>
        private bool _loginExpired;

        /// <summary>「总」进度日志的间隔（秒）。</summary>
        private const int ProgressLogSec = 10;

        /// <summary>进度汇总日志的定时器。</summary>
        private System.Windows.Threading.DispatcherTimer _progressLogTimer;

        /// <summary>上一行汇总日志的内容，用于去重（没变化就不重复打）。</summary>
        private string _lastProgressLog;

        /// <summary>
        /// 把所有在跑的任务**汇总成一行**写进日志。
        ///
        /// 取代原来核心层那种「每个任务每 5 秒一行」的刷屏日志 ——
        /// 普通用户只关心"一共多快、还剩多少"，逐任务的连接数/分片数/命中 IP
        /// 是排查用的，需要时把 <see cref="GeZi.Core.Download.SegmentedDownloader.PerTaskDiagLog"/>
        /// 打开即可，诊断面板本来也一直有。
        ///
        /// 去重：内容与上一条完全相同就不打（空闲/卡住时不会一直刷）。
        /// </summary>
        private void LogAggregateProgress()
        {
            try
            {
                // 【2026-10-20】顺手刷新关机提示条里的「预计 X 后」——
                // 它随总速度变化，跟进度日志同一个节拍（每 10 秒）正好。
                if (_shutdownAfterAll) RefreshShutdownPill();

                // 【2026-10-21】底部状态栏同节拍刷新（方案 C）。
                // 放在 active.Count==0 的提前 return 之前 —— 全部完成/全部暂停时
                // 状态栏也得更新到"没有在跑"的正确形态，不能停在最后一帧。
                RefreshTotalStatusBar();

                var active = _tasks.Where(t => t.State == JobState.Downloading
                                            || t.State == JobState.Queued
                                            || t.State == JobState.Paused
                                            || t.State == JobState.Cancelling).ToList();
                if (active.Count == 0)
                {
                    _lastProgressLog = null;   // 这一批结束了，下次重新开始
                    return;
                }

                double speed = active.Sum(t => t.Speed);
                long done = active.Sum(t => t.Done);
                long total = active.Sum(t => t.Total);
                int running = active.Count(t => t.State == JobState.Downloading);

                string pct = total > 0 ? (done * 100.0 / total).ToString("F1") + "%" : "—";
                string line = string.Format("下载中：{0} 个任务（{1} 个在跑），总速度 {2}/s，总进度 {3}",
                    active.Count, running,
                    Util.FormatSize((long)speed), pct);

                if (line == _lastProgressLog) return;   // 没变化就不重复打
                _lastProgressLog = line;
                Log(line);
            }
            catch { }
        }

        /// <summary>把攒下的日志一次性刷进 LogBox（由定时器在 UI 线程上调用）。</summary>
        private void FlushLogs()
        {
            if (_logQueue.IsEmpty)
                return;
            var sb = new StringBuilder();
            LogLine item;
            int n = 0;
            // 单次上限 500 条：万一积压太多，也不让一次刷新占住 UI 太久
            while (n < 500 && _logQueue.TryDequeue(out item))
            {
                // 全量历史始终收着 —— 切到「详细」时才能把之前过滤掉的行补显出来
                _logAll.Add(item);
                // 详细级只在「详细」模式下进文本框
                if (_logVerbose || !item.Detail)
                    sb.Append(item.Text).Append(Environment.NewLine);
                n++;
            }
            if (sb.Length > 0)
            {
                LogBox.AppendText(sb.ToString());
            }
            TrimLogLines();
            LogBox.ScrollToEnd();
        }

        /// <summary>
        /// 日志超过 <see cref="LogMaxLines"/> 时，从头部砍掉多余的整行。
        ///
        /// <para>【为什么要按 <see cref="_logAll"/> 而不是 LogBox.Text 截】两级日志下，
        /// LogBox 里只是"当前可见的子集"，按它算行数会算少；而且切「详细」需要从
        /// 全量列表重建，如果只截了可见文本，被砍掉的详细行就永久丢了。</para>
        /// </summary>
        private void TrimLogLines()
        {
            try
            {
                if (_logAll.Count <= LogMaxLines) return;
                int cut = _logAll.Count - LogMaxLines;
                _logAll.RemoveRange(0, cut);
                RebuildLogBox();
            }
            catch { }
        }

        /// <summary>
        /// 右键「清空日志」（2026-10-22 新增）。
        ///
        /// <para>【用户需求】「日志要是可以右键全部清除就好了，这样方便比对」——
        /// 排查问题时经常要"清屏 → 复现一次 → 只看新日志"，
        /// 否则新旧混在一起，很难分辨哪几行是这次产生的。</para>
        ///
        /// <para>【必须同时清两处】<see cref="LogBox"/> 只是**可见子集**，
        /// 全量历史在 <see cref="_logAll"/> 里（切「详细」时会从它重建）。
        /// 只清 TextBox 的话，一点「详细」旧日志又全回来了 —— 等于没清。</para>
        ///
        /// <para>【队列也要清】<see cref="_logQueue"/> 里可能还压着未刷新的行，
        /// 不清的话它们会在下一次定时器触发时冒出来。</para>
        /// </summary>
        private void OnClearLog(object sender, RoutedEventArgs e)
        {
            try
            {
                // ① 清待刷队列（还没进 LogBox 的行）
                LogLine _;
                while (_logQueue.TryDequeue(out _)) { }

                // ② 清全量历史（切「详细」时的重建源）
                _logAll.Clear();

                // ③ 清可见文本
                if (LogBox != null) LogBox.Clear();

                // ④ 汇总日志的去重基准也重置 ——
                //    否则清屏后第一条汇总可能因为"和上一条相同"被跳过，
                //    用户会以为日志坏了。
                _lastProgressLog = null;

                // 清屏本身不写日志（写了就白清了）。
            }
            catch { }
        }

        /// <summary>
        /// 右键「复制全部」（2026-10-22 新增）：把当前**可见**日志整段复制到剪贴板。
        ///
        /// <para>【为什么按可见内容而不是 _logAll】用户复制日志是为了贴给别人看，
        /// 那些被「详细」模式过滤掉的行贴出去只是噪音。
        /// 想连细节一起复制，先点「详细 · 开」再复制即可。</para>
        /// </summary>
        private void OnCopyAllLog(object sender, RoutedEventArgs e)
        {
            try
            {
                if (LogBox == null || string.IsNullOrEmpty(LogBox.Text)) return;
                System.Windows.Clipboard.SetText(LogBox.Text);
            }
            catch
            {
                // 剪贴板被别的进程占用时会抛 —— 这里静默即可（复制不是关键路径）。
            }
        }

        /// <summary>按当前 <see cref="_logVerbose"/> 从全量历史重建 LogBox 文本。</summary>
        private void RebuildLogBox()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("…（更早的日志已省略，仅保留最近 ").Append(LogMaxLines).Append(" 行）")
                  .Append(Environment.NewLine);
                foreach (var item in _logAll)
                {
                    if (_logVerbose || !item.Detail)
                        sb.Append(item.Text).Append(Environment.NewLine);
                }
                LogBox.Text = sb.ToString();
                LogBox.ScrollToEnd();
            }
            catch { }
        }

        /// <summary>
        /// 「详细」按钮：切换是否显示详细级日志（目录加载、连接分配、缓存命中等过程细节）。
        /// 切换时从全量历史重建，所以打开后能立刻看到之前被过滤掉的行。
        /// </summary>
        private void OnToggleLogVerbose(object sender, RoutedEventArgs e)
        {
            _logVerbose = !_logVerbose;
            if (LogDetailBtn != null)
                LogDetailBtn.Content = _logVerbose ? "详细 · 开" : "详细";
            RebuildLogBox();
        }

        /// <summary>
        /// 把「会等网络 / 扫磁盘」的按钮统一置灰，避免请求期间被重复触发。
        /// </summary>
        /// <remarks>
        /// ⚠️ 会**跳过正在显示加载态的按钮** —— 那些按钮由 <see cref="BeginBusy"/> 自己管，
        /// 它们要保持原色（加载态要看得清），不能被这里按成 0.45 透明度的"禁用"样子。
        /// </remarks>
        private void SetBusy(bool busy)
        {
            foreach (var b in new[] { ParseBtn, DownloadSelBtn, DownloadAllBtn,
                                      DriveRefreshBtn, DriveUpBtn, FetchLinksBtn })
            {
                if (b == null) continue;
                if (_busyButtons.Contains(b)) continue;   // 加载中 → 保持原色
                b.IsEnabled = !busy;
            }
        }

        // ---------------- 按钮「加载中」态 ----------------
        //
        // 【为什么需要】用户反馈：「他们需要加载时间或者是会切换到另一个东西，
        // 每次点击它们的时候都要有一个加载过程，主要是为了让用户看到他在加载」。
        // 本质是消除"点击 → 界面响应"之间那段静默期 —— 没有反馈时，用户无法区分
        // 「程序在忙」和「程序没反应」，于是会重复点击（取直链连点 = 重复发网络请求、
        // 导出连点 = 桌面多出几个 txt）。有反馈时同样的耗时就变成"它在干活，我等一下"。

        /// <summary>正在显示加载态的按钮。用于**拒绝重入**（防重复提交）。</summary>
        private readonly HashSet<Button> _busyButtons = new HashSet<Button>();

        /// <summary>进入加载态前的原始 Content，退出时原样还原（含图标等复杂内容）。</summary>
        private readonly Dictionary<Button, object> _busySavedContent = new Dictionary<Button, object>();

        /// <summary>
        /// 加载态的最短显示时长（毫秒）。实现见 <see cref="UiBusy.MinBusyMs"/>。
        /// </summary>
        private const int MinBusyMs = UiBusy.MinBusyMs;

        /// <summary>取一个计时基准，配合 <see cref="EndBusyAsync"/> 用。</summary>
        private static long BusyClock()
        {
            return DiagClock.GetTimestamp();
        }

        /// <summary>
        /// 把按钮切成「加载中」：内容换成 小转圈 + 短文案，并阻止重复点击。
        /// </summary>
        /// <returns>false = 该按钮已在加载中，本次点击应**直接丢弃**。</returns>
        /// <remarks>
        /// ⚠️ 这里**故意不动 IsEnabled**：按钮样式在 IsEnabled=False 时会把底色压到
        /// 0.45 透明度，那样加载态看起来是"被禁用"而不是"在忙"，转圈也会一起变淡。
        /// 所以改用 `IsHitTestVisible=false` 挡鼠标 + `_busyButtons` 挡键盘（焦点在按钮上
        /// 按空格仍会触发 Click，靠集合兜住）。同时 <see cref="SetBusy"/> 会跳过它。
        /// </remarks>
        private bool BeginBusy(Button btn, string busyText)
        {
            return BeginBusy(btn, busyText, cancelable: false);
        }

        /// <summary>
        /// 把按钮切成「加载中」，可选**保持可点击**（用于「入队中（点此终止）」）。
        ///
        /// <para>【cancelable 的用途】入队要递归展开目录 + 逐个取直链，大目录可能要几十秒。
        /// 用户反馈「点了下载后一直卡在『入队中』」，希望能中止。此时按钮保持可点，
        /// 再点一次 = 触发取消（见 <see cref="RequestCancelEnqueue"/>）。</para>
        /// </summary>
        private bool BeginBusy(Button btn, string busyText, bool cancelable)
        {
            if (btn == null) return true;                 // 没有按钮就只当普通执行
            if (_busyButtons.Contains(btn)) return false; // 已在加载 → 拒绝重入

            _busyButtons.Add(btn);
            _busySavedContent[btn] = btn.Content;
            // 冻结最小宽度：忙时文案通常比原文案短，不冻结的话按钮会突然缩一下
            if (btn.ActualWidth > 0)
                btn.MinWidth = Math.Max(btn.MinWidth, btn.ActualWidth);
            btn.Content = UiBusy.BuildContent(busyText, btn);
            // 可终止的加载态：按钮仍可点击（点击 = 终止）；普通加载态则挡住重入。
            btn.IsHitTestVisible = cancelable;
            return true;
        }

        /// <summary>退出加载态、还原按钮内容（并保证加载态至少显示了 <see cref="MinBusyMs"/>）。</summary>
        private async Task EndBusyAsync(Button btn, long startedTicks)
        {
            if (btn == null) return;

            long elapsedMs = (DiagClock.GetTimestamp() - startedTicks) * 1000L / DiagClock.Frequency;
            int remain = MinBusyMs - (int)elapsedMs;
            if (remain > 0)
                await Task.Delay(remain);

            if (_busySavedContent.TryGetValue(btn, out var saved))
                btn.Content = saved;
            _busySavedContent.Remove(btn);
            _busyButtons.Remove(btn);
            btn.IsHitTestVisible = true;
        }

        // ---------------- 终止入队（2026-10-20 移植） ----------------
        //
        // 【为什么需要】用户反馈「点击下载后一直卡在『入队中』」。
        // 入队要递归展开目录（大目录几十页）+ 逐个取直链，中途还可能撞上限流。
        // 原来这段时间按钮是死的，用户只能干等或强杀进程。
        // 现在：入队期间把「下载」按钮就地变成**可点**的「入队中（点此终止）」，
        // 再点一次即触发下面的取消源，立即停下（已加入的任务不受影响）。

        /// <summary>入队按钮的忙时文案。</summary>
        private const string EnqueueBusyText = "入队中";

        /// <summary>非 null = 正在入队；取消它会立刻中止本次入队。</summary>
        private CancellationTokenSource _enqueueCts;

        /// <summary>正在入队的那个按钮（终止时要把它的文案改成「正在终止…」）。</summary>
        private Button _enqueueBusyBtn;

        /// <summary>
        /// 开始一次入队。返回 false 表示"本次点击不该继续执行"。
        /// 若已有入队在跑，本次点击视为**终止**，返回 false。
        /// </summary>
        private bool BeginEnqueue(Button btn)
        {
            if (_enqueueCts != null)
            {
                // 已在入队 → 再点一次 = 终止
                RequestCancelEnqueue();
                return false;
            }
            if (btn != null && _busyButtons.Contains(btn)) return false;   // 拒绝重入

            _enqueueCts = new CancellationTokenSource();
            _enqueueBusyBtn = btn;
            if (btn != null) BeginBusy(btn, EnqueueBusyText, cancelable: true);
            return true;
        }

        /// <summary>请求终止入队（不阻塞，供 UI 点击直接调用）。</summary>
        private void RequestCancelEnqueue()
        {
            try
            {
                Log("正在终止入队…");
                if (_enqueueCts != null) _enqueueCts.Cancel();
                // 收回可点，防连点反复 Cancel；文案改成「正在终止…」
                if (_enqueueBusyBtn != null && _busyButtons.Contains(_enqueueBusyBtn))
                {
                    _enqueueBusyBtn.Content = UiBusy.BuildContent("正在终止…", _enqueueBusyBtn);
                    _enqueueBusyBtn.IsHitTestVisible = false;
                }
            }
            catch { }
        }

        /// <summary>结束入队：释放取消源并还原按钮。</summary>
        private async Task EndEnqueueAsync(Button btn, long startedTicks)
        {
            try { if (_enqueueCts != null) _enqueueCts.Dispose(); } catch { }
            _enqueueCts = null;
            _enqueueBusyBtn = null;
            await EndBusyAsync(btn, startedTicks);
        }

        /// <summary>造一个"小转圈"（缺口圆弧 + 无限旋转）。Stroke 由调用方设置。</summary>
        private static ShapePath MakeSpinner()
        {
            return UiBusy.MakeSpinner();
        }

        // ---------------- 模式切换 ----------------

        /// <summary>
        /// 正在执行"恢复上次浏览位置"（2026-10-21）。
        /// <para>恢复流程要用代码改 ShareModeRadio/DriveModeRadio 的选中态，
        /// 而那会触发 <see cref="OnModeChanged"/> —— 它会 ResetNav 并重新加载，
        /// 把恢复要铺的状态冲掉。这个标记让 OnModeChanged 在恢复期间直接返回。</para>
        /// </summary>
        private bool _restoringBrowse;

        private async void OnModeChanged(object sender, RoutedEventArgs e)
        {
            if (SharePanel == null)
                return;

            // 恢复浏览位置期间不要响应：恢复流程自己会负责加载正确的目录
            if (_restoringBrowse) return;
            bool nowShare = ShareMode;
            SharePanel.Visibility = nowShare ? Visibility.Visible : Visibility.Collapsed;

            // ① 先把**离开的那个模式**的导航状态存下来
            CaptureNav(nowShare ? _driveNav : _shareNav);

            // ② 再恢复**进入的那个模式**上次的状态。
            //    有快照就直接还原（连列表都不重新请求）—— 用户反馈的
            //    「切到我的网盘再回来要重新解析、解析结果没了」就是这里缺了恢复。
            var snap = nowShare ? _shareNav : _driveNav;
            if (snap.Files.Count > 0)
            {
                ApplyNav(snap);
                Log("已切回上次的目录" + (snap.PathList.Count > 0
                        ? "：" + string.Join(" / ", snap.PathList)
                        : "（根目录）"));
                return;
            }

            // ③ 没有快照（首次进入该模式）才走原来的流程
            ResetNav();
            if (!nowShare)
            {
                if (_client == null)
                {
                    Log("网盘模式需要先登录");
                    return;
                }
                await LoadDirAsync("0");
            }
        }

        /// <summary>把当前导航状态存进指定快照。</summary>
        private void CaptureNav(NavSnapshot into)
        {
            if (into == null) return;
            into.Files = _files.ToList();
            into.CurFid = _curFid;
            into.FidStack = _fidStack.ToList();   // Stack.ToList() = 栈顶在前（pop 顺序）
            into.PathList = _pathList.ToList();
            into.PwdId = _pwdId;
            into.Stoken = _stoken;
        }

        /// <summary>把快照还原成当前导航状态（含分享的 pwd_id / stoken）。</summary>
        private void ApplyNav(NavSnapshot s)
        {
            if (s == null) return;
            _files.Clear();
            foreach (var f in s.Files) _files.Add(f);

            _curFid = s.CurFid ?? "0";

            _fidStack.Clear();
            // Stack.ToList() 是"栈顶在前"，还原时要倒着 push 回去才等价
            for (int i = s.FidStack.Count - 1; i >= 0; i--)
                _fidStack.Push(s.FidStack[i]);

            _pathList.Clear();
            _pathList.AddRange(s.PathList);

            _pwdId = s.PwdId;
            _stoken = s.Stoken;

            UpdatePathText();
            UpdateEmptyStates();
        }

        private void ResetNav()
        {
            _files.Clear();
            _fidStack.Clear();
            _pathList.Clear();
            _shareRootName = "";
            _curFid = "0";
            UpdatePathText();
        }

        // ---------------- 下载页「上次浏览位置」持久化（2026-10-21） ----------------
        //
        // 【需求】用户反馈「每次重启启动应用，下载页都会重置，能否保留上次的浏览文件位置，
        //        方便继续浏览（包括打开文件的层级）」。选型方案 A：只记目录层级，重启后重新加载。
        //
        // 【为什么不复用现有的 NavSnapshot】那个是**内存里**的（切模式时用），
        //   字段是完整的 ShareFileItem 列表，需要 XmlSerializer 能序列化 ShareFileItem；
        //   而我们明确不想把列表落盘（几千项、且必然是旧的）。
        //   所以这里单独存"层级骨架"：模式 + 分享标识 + 当前 fid + 两条链。

        /// <summary>
        /// 把当前浏览位置写进设置（由 <see cref="SaveBrowsePosition"/> 调用，节流后落盘）。
        /// <para>导航变化时立刻调，但真正写盘由 SettingsStore.Save 自己合并 ——
        /// 用户点几下上级目录不该产生几次磁盘写。</para>
        /// </summary>
        private void CaptureBrowsePosition()
        {
            try
            {
                if (_settings == null) return;

                _settings.LastBrowseWasShare = ShareMode;
                _settings.LastBrowseFid = _curFid ?? "0";

                if (ShareMode)
                {
                    _settings.LastSharePwdId = _pwdId ?? "";
                    _settings.LastSharePasscode = _sharePasscode ?? "";
                    _settings.LastShareStartFid = string.IsNullOrEmpty(_shareStartFid) ? "0" : _shareStartFid;
                }
                else
                {
                    // 网盘模式与分享无关，清掉分享标识，避免下次启动误以为要恢复分享
                    _settings.LastSharePwdId = "";
                    _settings.LastSharePasscode = "";
                    _settings.LastShareStartFid = "0";
                }

                // 祖先链：_fidStack.ToList() 是"栈顶在前" = [上一层, …, 根]，反转成 [根, …, 上一层]
                var chain = new List<string>(_fidStack);
                chain.Reverse();
                _settings.LastBrowseFidChain = chain;

                // 名字链：_pathList 就是 [第一层, …, 当前层]
                _settings.LastBrowseNameChain = new List<string>(_pathList);
            }
            catch { }
        }

        /// <summary>
        /// 记录浏览位置并落盘。
        /// <para>⚠️ SettingsStore.Save 是**同步全量序列化 + 原子替换**（见它的实现），
        /// 没有合并/去抖。而 LoadDirAsync 每次导航都会调到这里，
        /// 逐个目录点「上级」就变成一次一次写盘 —— 所以这里自己做一个 1.5 秒的去抖：
        /// 只记住"待写"，等用户停下来再真正落盘。窗口关闭时由 FlushBrowsePosition 强制补写。</para>
        /// </summary>
        private void SaveBrowsePosition()
        {
            try
            {
                CaptureBrowsePosition();   // 先把意图记到 _settings 上（纯内存，便宜）
                CaptureBrowseCache();      // 列表快照同理（含滚动位置）

                if (_browseSaveTimer == null)
                {
                    _browseSaveTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(1.5),
                    };
                    _browseSaveTimer.Tick += (s, e) =>
                    {
                        try
                        {
                            _browseSaveTimer.Stop();
                            if (_settings != null) SettingsStore.Save(_settings);
                        }
                        catch { }
                    };
                }
                _browseSaveTimer.Stop();
                _browseSaveTimer.Start();
            }
            catch { }
        }

        /// <summary>
        /// 立刻把浏览位置写盘（窗口关闭时调用）——
        /// 否则用户在去抖窗口内关掉程序，最后一次导航就丢了。
        /// </summary>
        private void FlushBrowsePosition()
        {
            try
            {
                _browseSaveTimer?.Stop();
                CaptureBrowsePosition();
                // 【2026-10-21】顺手把列表快照也存下来（含滚动位置）——
                // 这是"下次打开不用重新请求"的关键。放在这里而不是只在 LoadDirAsync 里存，
                // 是因为用户可能只是滚动浏览了一下就关掉程序，那时并没有触发目录加载。
                CaptureBrowseCache();
                if (_settings != null) SettingsStore.Save(_settings);
            }
            catch { }
        }

        /// <summary>浏览位置落盘的去抖定时器（见 <see cref="SaveBrowsePosition"/>）。</summary>
        private DispatcherTimer _browseSaveTimer;

        /// <summary>
        /// 启动时恢复浏览位置的**包装**：先等自动登录有结果，再决定能不能恢复。
        ///
        /// <para>为什么一定要等登录：网盘模式没有 Cookie 就拿不到目录；
        /// 分享模式虽然游客也能看，但先等登录结束能让两条路径的行为一致
        /// （否则可能出现"这次能恢复、下次不能"的随机感）。</para>
        ///
        /// <para>登录失败也不算错 —— 分享模式照样可用，所以这里吞掉异常往下走。</para>
        /// </summary>
        private async Task RestoreBrowsePositionOnStartupAsync()
        {
            try
            {
                if (_startupLoginTask != null)
                {
                    try { await _startupLoginTask; } catch { }
                }
                await RestoreBrowsePositionAsync();
            }
            catch (Exception ex)
            {
                Log("恢复浏览位置时出错：" + ex.Message);
            }
        }

        /// <summary>
        /// 启动时恢复上次浏览位置（方案 A：重新请求，不缓存列表）。
        ///
        /// <para>【调用时机】必须在"登录就绪"之后：网盘模式要 Cookie、分享模式虽然
        /// 游客也能看但拿 stoken 更稳。见构造函数里 <c>_startupLoginTask</c> 的用法。</para>
        ///
        /// <para>【失败策略】静默回退到根目录，日志记一行（用户选的方案）。
        /// 任何一步失败都不弹窗 —— 启动流程被一个"上次的目录打不开"的对话框打断很难受。</para>
        /// </summary>
        private async Task RestoreBrowsePositionAsync()
        {
            try
            {
                if (_settings == null) return;

                // 用户在设置里关掉了 → 直接不恢复（保持原有的"从根目录开始"行为）
                if (!_settings.RestoreBrowseOnStartup) return;

                bool wantShare = _settings.LastBrowseWasShare;
                string fid = _settings.LastBrowseFid;

                // ① 没有任何记录（首次启动 / 老配置）→ 什么都不做，保持原有首屏
                if (!wantShare && string.IsNullOrEmpty(fid))
                {
                    return;
                }
                // 网盘模式停在根目录、且没有分享记录 → 等价于"没浏览过"，不用恢复
                if (!wantShare && (fid == "0" || string.IsNullOrEmpty(fid))
                    && (_settings.LastBrowseFidChain == null || _settings.LastBrowseFidChain.Count == 0))
                {
                    return;
                }

                if (wantShare)
                {
                    string pwdId = _settings.LastSharePwdId;
                    if (string.IsNullOrEmpty(pwdId))
                    {
                        Log("上次没有可恢复的分享浏览位置。");
                        return;
                    }
                    await RestoreSharePositionAsync(pwdId, fid);
                }
                else
                {
                    await RestoreDrivePositionAsync(fid);
                }
            }
            catch (Exception ex)
            {
                // 静默回退：只记日志，不打扰用户
                Log("恢复上次浏览位置失败：" + ex.Message + "（已回到根目录）");
                try { ResetNav(); } catch { }
            }
        }

        /// <summary>恢复「我的网盘」模式的上次目录。</summary>
        private async Task RestoreDrivePositionAsync(string fid)
        {
            // 【2026-10-22】恢复浏览位置改成**直接请求最新数据**（不再用本地快照铺列表），
            // 所以必须真的有客户端（要发请求）—— 快照里没有列表内容可用了。
            if (_client == null)
            {
                Log("未登录，跳过「我的网盘」浏览位置恢复。");
                return;
            }

            // 先把模式切到「我的网盘」。
            // ⚠️ 用 DriveModeRadio.IsChecked = true 会触发 OnModeChanged，
            //    那里面会 ResetNav + 重新 LoadDirAsync —— 于是我们后续的
            //    RestoreChainThenLoadAsync 就白做了。所以先挡掉它（_syncingNav
            //    不适用，这里用一个"正在恢复"标记，见 _restoringBrowse）。
            _restoringBrowse = true;
            try
            {
                if (DriveModeRadio != null) DriveModeRadio.IsChecked = true;
            }
            finally { _restoringBrowse = false; }

            await RestoreChainThenLoadAsync(fid, "网盘");
        }

        /// <summary>恢复分享模式的上次目录：先重新解析分享拿 stoken，再沿链导航。</summary>
        private async Task RestoreSharePositionAsync(string pwdId, string fid)
        {
            try
            {
                // 同 RestoreDrivePositionAsync：用标记挡掉 OnModeChanged 的自动加载，
                // 否则它会先 ResetNav + LoadDirAsync（把我们要恢复的状态冲掉）。
                _restoringBrowse = true;
                try
                {
                    if (ShareModeRadio != null) ShareModeRadio.IsChecked = true;
                    if (SharePanel != null) SharePanel.Visibility = Visibility.Visible;
                }
                finally { _restoringBrowse = false; }

                // 链接框回填，让用户看得见"恢复的是哪条分享"
                try
                {
                    if (LinkBox != null && string.IsNullOrEmpty(LinkBox.Text))
                        LinkBox.Text = "https://pan.quark.cn/s/" + pwdId;
                    if (PassBox != null && string.IsNullOrEmpty(PassBox.Text.Trim())
                        && !string.IsNullOrEmpty(_settings.LastSharePasscode))
                        PassBox.Text = _settings.LastSharePasscode;
                }
                catch { }

                // 【2026-10-22】恢复浏览位置改成**直接请求最新数据**（不再用本地快照铺列表），
                // 所以 stoken 一定要重新申请 —— 快照里的旧 stoken 早就过期了。
                _pwdId = pwdId;
                _sharePasscode = _settings.LastSharePasscode ?? "";
                _shareStartFid = string.IsNullOrEmpty(_settings.LastShareStartFid) ? "0" : _settings.LastShareStartFid;

                // 链接文本框回填（用户要能看见"恢复的是哪条分享"）
                try
                {
                    if (LinkBox != null && string.IsNullOrEmpty(LinkBox.Text))
                        LinkBox.Text = "https://pan.quark.cn/s/" + pwdId;
                    if (PassBox != null && string.IsNullOrEmpty(PassBox.Text.Trim())
                        && !string.IsNullOrEmpty(_sharePasscode))
                        PassBox.Text = _sharePasscode;
                }
                catch { }

                // 拿 stoken 后才能真正拉目录（分享过期 / 提取码变了会在这里失败）
                _client?.Dispose();
                _client = new QuarkClient(_settings.Cookie);

                var shareInfo = await _client.GetShareInfoAsync(pwdId, _sharePasscode);
                if (shareInfo == null || string.IsNullOrEmpty(shareInfo.Stoken))
                {
                    Log("上次的分享已失效（可能过期或需要提取码），已回到根目录。");
                    ResetNav();
                    return;
                }
                _stoken = shareInfo.Stoken;
                _linkCache.Clear();
                _dirCache.Clear();
                _dirSizeCache.Clear();

                await RestoreChainThenLoadAsync(fid, "分享");
            }
            catch (Exception ex)
            {
                Log("恢复分享浏览位置失败：" + ex.Message + "（已回到根目录）");
                try { ResetNav(); } catch { }
            }
        }

        /// <summary>
        /// 把当前目录的**浏览位置**（滚动位置）存成快照（2026-10-21 新增，2026-10-22 精简）。
        ///
        /// <para>【2026-10-22 变化】原来这里还存**列表内容**（前
        /// <see cref="BrowseCacheMaxItems"/> 条），启动时直接铺出来、一个请求都不发。
        /// 用户实测发现两个问题，于是改成"**直接请求最新数据 + 滚到上次位置**"：</para>
        /// <list type="number">
        /// <item>快照只存了前若干条 → 列表只显示 10 条，后面全是空的；</item>
        /// <item>快照铺出来的行**不能选中** → 没法勾文件、没法「免转存发送」。</item>
        /// </list>
        ///
        /// <para>所以现在快照**只用于记"我看到哪儿了"**（目录 fid + 滚动位置），
        /// 不再当数据源 —— 列表一律来自服务端最新数据，完整且可选中。</para>
        /// </summary>
        private void CaptureBrowseCache()
        {
            try
            {
                if (_settings == null) return;
                // 目录是空的就不覆盖旧快照 —— 多半是加载失败/切页清空，
                // 用空值覆盖会让用户的"上次位置"白丢。
                if (_files == null || _files.Count == 0) return;

                var snap = new BrowseSnapshotCache
                {
                    Fid = _curFid ?? "0",
                    FromShare = ShareMode,
                    SavedTicks = DateTime.Now.Ticks,
                    // 滚动位置：用户明确要求存这个，恢复后能回到上次看的地方
                    ScrollOffset = _fileScroller != null ? _fileScroller.VerticalOffset : 0,
                    RevealCount = _files.Count,
                    HasMore = _curDir != null && _curDir.HasMore,
                    // 【2026-10-22】不再存列表内容（恢复时一律请求最新数据）。
                    Items = new List<BrowseCacheItem>(),
                };

                _settings.LastBrowseCache = snap;
            }
            catch { }
        }

        /// <summary>快照最多存多少条（保留字段兼容老配置；2026-10-22 起不再存列表内容）。</summary>
        private const int BrowseCacheMaxItems = 200;

        /// <summary>
        /// 用设置里存的 fid 链重建 <c>_fidStack</c> / <c>_pathList</c>，然后加载目标目录。
        /// <para>链长关系：fidChain 是 [根, …, 上一层]，nameChain 是 [第一层, …, 当前层]。
        /// 正常情况 <c>nameChain.Count == fidChain.Count</c>（每一层都有一个名字）。
        /// 不一致时以较短的为准，宁可少还原一层也不要把面包屑搞乱。</para>
        /// </summary>
        private async Task RestoreChainThenLoadAsync(string fid, string modeName)
        {
            try
            {
                var fids = _settings.LastBrowseFidChain ?? new List<string>();
                var names = _settings.LastBrowseNameChain ?? new List<string>();

                _files.Clear();
                _fidStack.Clear();
                _pathList.Clear();
                _curFid = string.IsNullOrEmpty(fid) ? "0" : fid;

                // 重建栈：栈底是根、栈顶是上一层 → 按 fidChain 顺序压入即可
                for (int i = 0; i < fids.Count; i++)
                    _fidStack.Push(fids[i]);

                // 重建名字链：只取与 fidChain 等长的部分，避免多出来的名字对不上层级
                int n = Math.Min(names.Count, fids.Count);
                for (int i = 0; i < n; i++)
                    _pathList.Add(names[i]);

                UpdatePathText();

                // 【2026-10-22 用户要求】恢复浏览位置时**直接请求最新数据**，
                // 然后滚动到上次记住的位置。
                //
                // 【为什么不再用本地快照铺列表】原来优先走本地缓存（一个请求都不发），
                // 但用户实测发现两个问题：
                //   ① 缓存里只存了前若干条 → 列表只显示 10 条，后面全是空的；
                //   ② 缓存铺出来的行**不能选中** → 没法勾文件、没法"免转存发送"。
                // 快照只适合"记得我看到哪儿了"，不适合当数据源。
                //
                // 【现在的做法】请求最新数据（列表完整、可正常选中），
                // 再把滚动位置还原到上次离开的地方。滚动位置由
                // <see cref="CaptureBrowseCache"/> 单独存，不依赖列表内容。

                // 上次在这个目录的滚动位置（可能为 0 = 顶部）
                double wantOffset = 0;
                try
                {
                    var snap = _settings?.LastBrowseCache;
                    if (snap != null && !string.IsNullOrEmpty(snap.Fid)
                        && string.Equals(snap.Fid, _curFid, StringComparison.Ordinal))
                    {
                        wantOffset = snap.ScrollOffset;
                    }
                }
                catch { }

                bool loadFailed = false;
                await LoadDirAsync(_curFid, () => loadFailed = true);

                // 【关键】加载失败 = 这个 fid 打不开了（分享过期 / 网盘里被删 / 被移走），
                // 按用户选定的策略：静默回退到根目录，只在日志里记一行。
                // 不能把"加载失败"当成"空目录"——那样用户会看到一个空列表，
                // 却以为自己上次的目录里真的没东西（实测踩到过）。
                if (loadFailed)
                {
                    Log("上次的" + modeName + "目录已打不开，已回到根目录。");
                    ResetNav();
                    await LoadDirAsync("0");
                    return;
                }

                // 【补充规则】非根目录却拿到了空列表 → 判定为"目录已失效"并回退。
                // 为什么这么判：夸克 API 对**不存在的 pdir_fid** 并不报错，
                // 而是返回 code=0 + 空 list（实测：伪造一个无效 fid，客户端毫无异常，
                // 界面上就是一片空）。所以只在异常路径上回退是不够的。
                // 代价：用户上次若真的停在一个空文件夹，会被带回根目录 ——
                // 但空目录本身没什么可浏览的，且日志会说明原因，可接受。
                if (_files.Count == 0 && !string.IsNullOrEmpty(_curFid) && _curFid != "0")
                {
                    Log("上次的" + modeName + "目录已为空或不存在，已回到根目录。");
                    ResetNav();
                    await LoadDirAsync("0");
                    return;
                }

                // 【2026-10-22】滚回上次的位置（在最新数据铺好之后）。
                if (wantOffset > 0) RestoreScrollOffset(wantOffset);

                if (_files.Count == 0)
                {
                    // 目录能打开但空的（或是被删了只剩一个可进的壳）—— 也算恢复成功
                    Log("已恢复上次的" + modeName + "浏览位置：" +
                        (_pathList.Count > 0 ? string.Join(" / ", _pathList) : "根目录") + "（空目录）");
                }
                else
                {
                    Log("已恢复上次的" + modeName + "浏览位置：" +
                        (_pathList.Count > 0 ? string.Join(" / ", _pathList) : "根目录") +
                        "（" + _files.Count + " 项）");
                }
            }
            catch (Exception ex)
            {
                Log("恢复目录失败：" + ex.Message + "（已回到根目录）");
                try
                {
                    ResetNav();
                    await LoadDirAsync("0");
                }
                catch { }
            }
        }

        /// <summary>
        /// 当前所在目录名 —— 任务分组用（2026-10-20）。
        /// 优先取面包屑最后一段（用户自己点进来的子目录），否则用分享标题兜底
        /// （链接直接指向某个子目录时，列表里只有它的子项、拿不到它自己的名字）。
        /// 根目录 / 非分享模式返回空串 = 按「单独选择的文件」归类。
        /// </summary>
        private string CurrentFolderGroupName()
        {
            if (_pathList.Count > 0)
                return _pathList[_pathList.Count - 1] ?? "";
            return ShareMode ? (_shareRootName ?? "") : "";
        }

        // ---------------- 分享解析 ----------------

        /// <summary>「分享链接」输入框右侧的一键清空（×）。</summary>
        private void OnClearLinkBox(object sender, RoutedEventArgs e)
        {
            try
            {
                LinkBox.Clear();
                LinkBox.Focus();   // 清空后把焦点还回去，方便直接粘贴下一条
            }
            catch { }
        }

        /// <summary>
        /// 【2026-10-04 新增】「识别二维码」——夸克分享有时只给二维码图片、不给链接。
        ///
        /// 打开扫码窗口，用户用「Ctrl+V 粘贴截图 / 选文件 / 拖拽」任一方式给图，
        /// 解出的文本会走 <see cref="ApplyScannedText"/> 自动填进链接框。
        /// </summary>
        private void OnScanQrClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var win = new ScanQrWindow(ApplyScannedText, Log)
                {
                    Owner = this,
                };
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                Log("打开二维码识别窗口失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 把识别出来的文本落到界面上。
        ///
        /// 【为什么不直接调 OnParseClick】识别结果不保证是链接（可能是提取码文案、口令、
        /// 甚至一张无关的码）。所以这里先判断：像链接就填进去并自动解析，不像就原样填进
        /// 链接框、让用户自己看一眼再决定 —— 悄悄丢弃或强行解析都不合适。
        /// </summary>
        private void ApplyScannedText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim();

            try
            {
                LinkBox.Text = text;
                LinkBox.CaretIndex = text.Length;

                // 用现有解析器判断这段文本里有没有分享链接；有就顺手提取码
                var info = ShareUrlParser.Parse(text);
                bool looksLikeLink = !string.IsNullOrEmpty(info.PwdId);

                if (!looksLikeLink)
                {
                    // 不是可识别的分享链接：只填不解析，给出明确提示
                    Log("二维码识别为：" + text);
                    if (info.IsKouling)
                    {
                        AppDialog.Show(this,
                            "识别出的内容是【夸克口令】，不是分享链接：\n\n" + text + "\n\n" +
                            "夸克没有公开的「口令转链接」接口，本工具无法直接解析。\n" +
                            "请在夸克 App 里打开这条口令，进入分享页后点「分享」→「复制链接」，\n" +
                            "再把拿到的 https://pan.quark.cn/s/… 链接粘进来。",
                            "这是夸克口令", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        AppDialog.Show(this,
                            "二维码识别成功，但内容看起来不是分享链接：\n\n" + text + "\n\n" +
                            "已填进链接框，你可以自己确认一下。",
                            "识别结果", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return;
                }

                // 是分享链接 → 把提取码一并回填（识别出的码通常比用户手输的准）
                if (!string.IsNullOrEmpty(info.Passcode))
                    PassBox.Text = info.Passcode;

                Log("二维码识别出分享链接，开始解析…");
                OnParseClick(null, null);
            }
            catch (Exception ex)
            {
                Log("处理识别结果失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 【粘贴时自动精简】用户常把夸克分享的**整段文案**粘进来，形如：
        /// 「我用夸克网盘给你分享了「xxx.pdf」，点击链接或复制整段内容，打开「夸克APP」即可获取。
        ///   链接：https://pan.quark.cn/s/20ef19d26112  提取码：abcd」
        ///
        /// 🚨 为什么必须拦一下：`LinkBox` 是**单行、42px、横向滚动条 Disabled** 的输入框，
        /// 整段文案（近百字符）塞进去后**只能看到开头那截**，链接被挤出可视区域 ——
        /// 用户会以为"链接不见了"（真实反馈）。
        /// 其实解析本身没问题（`ShareUrlParser` 能正确提取），是**看不见**造成的误会。
        ///
        /// 所以粘贴时直接把内容替换成「裸链接」，并把文案里带的提取码顺手填进 `PassBox`。
        /// 找不到分享链接时**原样粘贴**，绝不吞掉用户的内容。
        /// </summary>
        private void OnLinkBoxPasting(object sender, DataObjectPastingEventArgs e)
        {
            try
            {
                if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText))
                    return;
                string text = e.SourceDataObject.GetData(DataFormats.UnicodeText) as string;
                if (string.IsNullOrEmpty(text))
                    return;

                // 先尝试从文案里**原样抠出 URL**（保留 fid / 查询串等），比按 PwdId 重建更保真。
                var m = Regex.Match(text, @"https?://[^\s""'<>）】\]]+");
                string url = null;
                if (m.Success && m.Value.IndexOf("/s/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    url = m.Value;
                }
                else
                {
                    // 退路：按解析出的 PwdId 重建（应对"只有纯文本、URL 被截断"等情况）
                    var info = ShareUrlParser.Parse(text);
                    if (!string.IsNullOrEmpty(info.PwdId))
                        url = "https://pan.quark.cn/s/" + info.PwdId;
                }

                if (string.IsNullOrEmpty(url))
                    return;   // 不是分享链接 → 原样粘贴，别动用户的东西

                // 用替换后的数据对象覆盖本次粘贴内容
                e.DataObject = new DataObject(DataFormats.UnicodeText, url);

                // 文案里带提取码时顺手填上（用户看得见"用了什么码"，解析失败时好排查）
                var info2 = ShareUrlParser.Parse(text);
                if (!string.IsNullOrEmpty(info2.Passcode) && string.IsNullOrEmpty(PassBox.Text.Trim()))
                    PassBox.Text = info2.Passcode;
            }
            catch { /* 粘贴绝不因为这里出错而失败 */ }
        }

        private async void OnParseClick(object sender, RoutedEventArgs e)
        {
            string url = LinkBox.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                Log("请先粘贴分享链接");
                return;
            }
            var info = ShareUrlParser.Parse(url);
            if (info.IsKouling)
            {
                // 夸克口令（形如 /~469d3M9zTu~:/）**不是**分享链接，也没有公开的
                // 「口令 → 链接」接口（查过官方开放平台与非官方 API 文档，分享相关
                // 接口只接受 pwd_id）。所以这里不装作能解析，直接给出可操作的指引。
                Log("这是夸克口令（/~" + info.Kouling + "~:/），不是分享链接，无法直接解析。");
                AppDialog.Show(this,
                    "这是一条【夸克口令】，不是分享链接。\n\n" +
                    "夸克没有公开「口令转链接」的接口，本工具无法直接解析它。\n\n" +
                    "请这样拿到链接：\n" +
                    "1. 在夸克 App / 夸克网盘客户端里打开这条口令；\n" +
                    "2. 进入分享页后点「分享」→「复制链接」；\n" +
                    "3. 把复制到的 https://pan.quark.cn/s/… 链接粘回这里即可。\n\n" +
                    "（如果对方同时给了链接和提取码，直接整段粘贴进来也行 —— " +
                    "本工具会自动把链接和提取码都认出来。）",
                    "这是夸克口令", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.IsNullOrEmpty(info.PwdId))
            {
                Log("无法从链接解析出分享 ID");
                return;
            }
            string passcode = string.IsNullOrEmpty(PassBox.Text.Trim()) ? info.Passcode : PassBox.Text.Trim();
            // 整段分享文案里自带提取码时，把它回填到输入框 —— 让用户看得见"用了什么码"，
            // 否则解析失败时完全不知道是码错了还是别的原因。
            if (!string.IsNullOrEmpty(info.Passcode) && PassBox.Text.Trim() != info.Passcode)
                PassBox.Text = info.Passcode;
            _pwdId = info.PwdId;
            _linkCache.Clear();   // 换分享后旧 fid 的直链不再适用
            // 目录缓存与大小缓存同理：fid 是分享内的编号，换了分享再留着会张冠李戴。
            _dirCache.Clear();
            _dirSizeCache.Clear();

            _client?.Dispose();
            _client = new QuarkClient(_settings.Cookie);

            // 解析要发两次网络请求（换 stoken + 拉目录），期间必须让用户看到"在忙"
            long t0 = BusyClock();
            if (!BeginBusy(ParseBtn, "解析中")) return;
            SetBusy(true);
            try
            {
                var shareInfo = await _client.GetShareInfoAsync(info.PwdId, passcode);
                _stoken = shareInfo.Stoken;
                if (string.IsNullOrEmpty(_stoken))
                {
                    Log("获取分享凭证失败（可能需登录或提取码错误）");
                    NotifyIfHidden("自动解析失败",
                        "获取分享凭证失败，可能需要登录，或分享提取码不正确。");
                    return;
                }
                ResetNav();
                _curFid = info.StartFid;
                // stoken 过期自愈用（2026-10-20）：记下"这次解析的起点"。
                // _curFid 之后会随用户导航变化，不能当分享起点用。
                _sharePasscode = passcode ?? "";
                _shareStartFid = string.IsNullOrEmpty(info.StartFid) ? "0" : info.StartFid;
                // 链接直接指向某个子目录（#/list/share/<fid>）时，列表里只有它的子项，
                // 拿不到它自己的名字 —— 用分享标题兜底，作为任务分组的名字。
                _shareRootName = info.StartFid != "0" ? (shareInfo.Title ?? "") : "";
                await LoadDirAsync(_curFid);
                Log("解析成功。单击文件夹进入子目录；选中文件后点「下载选中」即可下载"
                    + "（文件夹要先选中：Shift / Ctrl+点击，或右键该行）。");
                RecordShareHistory(info.PwdId, passcode, info.StartFid);
                NotifyIfHidden("已解析剪贴板里的分享链接",
                    _files.Count > 0
                        ? "共 " + _files.Count + " 个文件。双击托盘图标打开窗口即可下载。"
                        : "该分享的当前目录是空的（文件可能在子目录里）。双击托盘图标打开窗口查看。");
            }
            catch (Exception ex)
            {
                Log("解析失败: " + ex.Message);
                NotifyIfHidden("自动解析失败", ex.Message);
            }
            finally
            {
                SetBusy(false);
                await EndBusyAsync(ParseBtn, t0);
            }
        }

        // ---------------- 剪贴板自动解析（2026-10-08） ----------------
        //
        // 需求：在**任何程序**里复制到夸克分享链接，本程序直接解析好，不用再切回窗口粘贴。
        //   默认开；设置页「窗口与后台 → 剪贴板」可关，关掉时**根本不注册**监听（零开销）。
        //
        // 实现路线见 NativeMethods：AddClipboardFormatListener + WM_CLIPBOARDUPDATE，
        // 既不用轮询，也不插队进 `SetClipboardViewer` 那条"链"。
        // 通知由系统投递到本窗口的消息队列 → 下面这些回调**都在 UI 线程**上跑。

        /// <summary>
        /// 把两件**绑在窗口句柄上**的事挂到当前句柄上：
        ///   ① 跨进程激活消息钩子（再点一次 exe 时由本进程自己把窗口拉起来）；
        ///   ② 剪贴板监听（按设置，见 <see cref="ApplyClipboardListener"/>）。
        ///
        /// 由 <c>SourceInitialized</c> 触发（订阅在构造函数里），**幂等**：同一个句柄只挂一次。
        ///
        /// 【为什么不干脆在 Loaded 里挂一次了事】`HwndSource.AddHook` 和
        /// `AddClipboardFormatListener` 都是**绑在原生窗口句柄**上的。窗口在
        /// "隐藏到托盘 → 恢复" 的过程中句柄会不会被 WPF 重建，各版本/资料说法不一，
        /// 赌不起：句柄一旦被换掉，两个注册就都**静默失效** —— 表现为"恢复窗口后
        /// 复制分享链接不再自动解析"、"再点 exe 唤不醒托盘里的窗口"，
        /// 而这两种现象都不会报错，只会让人以为功能没做对。
        /// 所以这里按句柄判断，并在每次句柄（重）建时重挂一遍 —— 两种情况都对。
        /// </summary>
        private void AttachWindowHooks()
        {
            try
            {
                var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (h == IntPtr.Zero) return;      // 句柄还没建好，等下一次事件
                if (h == _hooksOnHwnd) return;     // 这个句柄已经挂过了

                try
                {
                    var src = System.Windows.Interop.HwndSource.FromHwnd(h);
                    src?.AddHook(WndProc);
                }
                catch { }

                _hooksOnHwnd = h;
                ApplyClipboardListener();
            }
            catch (Exception ex)
            {
                Log("窗口消息钩子安装失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 按设置挂上 / 摘掉剪贴板监听（幂等）。启动、窗口句柄重建、设置页开关都调它。
        /// </summary>
        private void ApplyClipboardListener()
        {
            try
            {
                // ⚠️ 注册必须**有窗口句柄之后**（HWND 存在前是 IntPtr.Zero）——
                //    这也是它不能写在构造函数里的原因。
                var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (h == IntPtr.Zero) return;

                bool want = _settings.ClipboardAutoParse;

                // 句柄与设置都没变 → 已经是对的，什么都不用做。
                if (want && _clipboardHookHwnd == h) return;
                if (!want && _clipboardHookHwnd == IntPtr.Zero) return;

                // 先摘掉旧注册。旧句柄若已销毁，这次摘除会失败 —— 无所谓，
                // 注册本来就是随句柄一起消失的。
                if (_clipboardHookHwnd != IntPtr.Zero)
                {
                    NativeMethods.RemoveClipboardFormatListener(_clipboardHookHwnd);
                    _clipboardHookHwnd = IntPtr.Zero;
                }
                if (!want) return;

                if (NativeMethods.AddClipboardFormatListener(h))
                {
                    _clipboardHookHwnd = h;
                }
                else
                {
                    // 基本只会在句柄无效时发生；但既然失败了就说清楚 ——
                    // 让用户知道"复制链接没反应"不是链接的问题。
                    Log("剪贴板监听开启失败（Win32 错误码 " + Marshal.GetLastWin32Error()
                        + "），复制分享链接不会自动解析。");
                }
            }
            catch (Exception ex)
            {
                Log("剪贴板监听设置失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 设置页「剪贴板」开关。
        ///
        /// 走独立处理器而不是 <see cref="OnSettingChanged"/>（同 <see cref="OnThemeChanged"/>
        /// 的理由）：这个开关只影响"要不要注册一条系统通知"，与并发数/下载目录无关，
        /// 走 ApplySettings() 会顺带 RecreateScheduler() 去重建下载调度器 ——
        /// 为了一个开关打断在途下载，代价完全不成比例。
        /// </summary>
        private void OnClipboardAutoParseChanged(object sender, RoutedEventArgs e)
        {
            // 回填控件时也会触发 Click（走 _applyingSettings 保护，同其它设置项）
            if (_applyingSettings) return;
            try
            {
                bool on = ClipboardAutoParseCheck.IsChecked == true;
                _settings.ClipboardAutoParse = on;
                SettingsStore.Save(_settings);
                ApplyClipboardListener();
                Log(on ? "已开启剪贴板监听：复制夸克分享链接会自动解析。"
                       : "已关闭剪贴板监听。");

                // 关掉时清掉去重标记：重新打开后，用户再复制同一条链接应该能正常解析，
                // 不该被"上次已经处理过"的记忆挡住。
                if (!on) _clipboardLastPwdId = null;
            }
            catch (Exception ex)
            {
                Log("剪贴板设置保存失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 设置页「优先展示未完成的文件」勾选变化（2026-10-21 新增）。
        ///
        /// <para>用户需求：「假如有多个文件在文件夹下，应该优先展示未完成的文件，
        /// 因为有时候需要滚动到底部才发现」。</para>
        /// <para>开关一改就立刻重排并落盘 —— 用户想看到的是"勾上马上生效"，
        /// 而不是下次启动才变。</para>
        /// </summary>
        private void OnTaskUnfinishedFirstChanged(object sender, RoutedEventArgs e)
        {
            // 回填控件时也会触发 Click（走 _applyingSettings 保护，同其它设置项）
            if (_applyingSettings) return;
            try
            {
                bool on = TaskUnfinishedFirstCheck.IsChecked == true;
                _settings.TaskUnfinishedFirst = on;
                SettingsStore.Save(_settings);

                ApplyTaskOrder();
                Log(on
                    ? "任务列表已切换：需要处理的文件排在前面（失败/取消 → 未完成 → 已完成，同组内仍按时间偏好排序）"
                    : "任务列表已切换：不再把需要处理的文件排前面（恢复纯时间序）");
            }
            catch (Exception ex)
            {
                Log("任务列表排序设置保存失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 设置页「自动调度」勾选变化（2026-10-24 新增）。
        ///
        /// <para>【用户需求】「一般设置几个同时下载数，下载速度更能跑满呢，
        /// 你可以添加一个自动调度的选项，然后自动计算」。</para>
        ///
        /// <para>开启后：按 <see cref="GeZi.Core.Download.AutoTuner"/> 算出的组合
        /// 覆盖「单文件并发」与「同时下载数」两个控件，并把它们**置灰**
        /// （只读）—— 避免用户看到两个数被改动却以为是程序出错了。
        /// 关闭后恢复可编辑，值保持当前（不还原旧值，避免"关掉开关数值又跳回去"的困惑）。</para>
        /// </summary>
        private void OnAutoTuneChanged(object sender, RoutedEventArgs e)
        {
            if (_applyingSettings) return;
            try
            {
                bool on = AutoTuneCheck.IsChecked == true;
                _settings.AutoTune = on;

                if (on)
                {
                    // 文件数未知时用通用最优值；真正开下时会按实际文件数再算一次。
                    var plan = GeZi.Core.Download.AutoTuner.Compute(0);
                    ApplyAutoPlan(plan);
                    Log("已开启自动调度：" + plan.Reason);
                    Log("  「单文件并发」与「同时下载数」已锁定为自动计算值，手动修改需先关掉这个开关。");
                }
                else
                {
                    Log("已关闭自动调度：并发与同时任务数改回手动设置。");
                }

                UpdateAutoTuneUi();
                SettingsStore.Save(_settings);
            }
            catch (Exception ex)
            {
                Log("自动调度设置保存失败: " + ex.Message);
            }
        }

        /// <summary>把 AutoTuner 的结果写进两个输入控件（不触发保存，由调用方负责）。</summary>
        private void ApplyAutoPlan(GeZi.Core.Download.AutoTuner.Plan plan)
        {
            if (plan == null) return;
            _settings.Threads = plan.Threads;
            _settings.ConcurrentTasks = plan.Concurrent;

            // ThreadsBox 是档位下拉（16/32/64/128/256/512），自动算出的 170 这种
            // 中间值不在档位里 —— 选最接近的档位显示，但 _settings.Threads 保留精确值。
            // （下拉只是"选择器"，真正下发的是 _settings.Threads。）
            ThreadsBox.SelectedIndex = PresetToIndex(plan.Threads);
            ConcurrentBox.Text = plan.Concurrent.ToString();
        }

        /// <summary>按自动调度开关的状态，刷新控件可用性与提示文案。</summary>
        private void UpdateAutoTuneUi()
        {
            bool on = _settings.AutoTune;
            if (AutoTuneCheck != null) AutoTuneCheck.IsChecked = on;

            // 单文件并发下拉：自动模式下禁用（值仍由 PresetToIndex 显示最接近档）
            if (ThreadsBox != null) ThreadsBox.IsEnabled = !on;
            if (ConcurrentBox != null) ConcurrentBox.IsEnabled = !on;

            if (AutoTuneHint != null)
            {
                if (on)
                {
                    var plan = GeZi.Core.Download.AutoTuner.Compute(0);
                    AutoTuneHint.Text = string.Format(
                        "当前：单文件 {0} 路 × 同时 {1} 个 ≈ {2} 条连接（预算 {3}）",
                        _settings.Threads, _settings.ConcurrentTasks,
                        _settings.Threads * _settings.ConcurrentTasks,
                        GeZi.Core.Download.AutoTuner.Budget);
                }
                else
                {
                    AutoTuneHint.Text = "手动模式：两个数都由你自己定";
                }
            }
        }

        /// <summary>
        /// 设置页「启动时恢复上次浏览的目录」勾选变化（2026-10-21 新增）。
        /// </summary>
        private void OnRestoreBrowseChanged(object sender, RoutedEventArgs e)
        {
            if (_applyingSettings) return;
            try
            {
                bool on = RestoreBrowseCheck.IsChecked == true;
                _settings.RestoreBrowseOnStartup = on;
                SettingsStore.Save(_settings);
                Log(on
                    ? "已开启：启动时自动恢复上次浏览的目录。"
                    : "已关闭：启动时不再恢复浏览位置，总是从根目录开始。");
            }
            catch (Exception ex)
            {
                Log("浏览位置设置保存失败: " + ex.Message);
            }
        }

        /// <summary>收到「剪贴板内容变了」的系统通知。</summary>
        private void OnClipboardChanged()
        {
            try
            {
                // 退出流程里不再处理：这时窗口/托盘可能已经拆掉了。
                if (_exiting) return;

                // 关了开关就不再处理。一般走不到这里（关了就直接摘注册），
                // 但"关掉的那一刻"可能已经有通知排进消息队列了 —— 补一道，免得
                // 用户刚勾掉开关、随手复制一下，程序又自作主张解析了一次。
                if (!_settings.ClipboardAutoParse) return;

                string text = TryReadClipboardText();
                if (string.IsNullOrEmpty(text)) return;

                string url = FindQuarkShareUrl(text);
                if (string.IsNullOrEmpty(url))
                {
                    // 不是夸克分享链接（含"夸克口令"）。这里**刻意什么都不做、也不弹窗**：
                    // 剪贴板通知不是用户在本程序里发起的动作，为一句"这不能解析"打断他很粗暴
                    // —— 何况口令在聊天记录里到处都是，复制顺手就会触发。
                    return;
                }

                var info = ShareUrlParser.Parse(url);
                if (string.IsNullOrEmpty(info.PwdId)) return;

                if (string.Equals(info.PwdId, _clipboardLastPwdId, StringComparison.Ordinal))
                {
                    // 同一条分享的重复通知 → 不再重复发请求。见 _clipboardLastPwdId 的注释。
                    // （要重新解析，直接点「解析」按钮即可 —— 手动路径不受这个标记影响。）
                    return;
                }
                _clipboardLastPwdId = info.PwdId;

                // 提取码从**整段文本**里取：链接自带 pwd= 的能认，文案里的
                // 「提取码：abcd」也能认（ShareUrlParser 两种都支持）。
                FillAndParse(url, ShareUrlParser.Parse(text).Passcode);
            }
            catch (Exception ex)
            {
                try { Log("剪贴板自动解析失败: " + ex.Message); } catch { }
            }
        }

        /// <summary>
        /// 从一段文本里找出**夸克网盘**的分享链接；找不到返回 null。
        ///
        /// 🚨 这里必须比「粘贴进链接框」（<see cref="OnLinkBoxPasting"/>）严得多：
        ///    · 粘贴是用户在**本程序里**的明确动作，识别宽松些最多是顺手填个框；
        ///    · 剪贴板监听是**后台自己发起**的，认错就会把用户正在复制的别的东西
        ///      （比如别的网盘恰好也叫 /s/ 的链接）当夸克链接去请求、还把他的界面切走。
        /// 所以域名必须真的是夸克的，`/s/` 只是必要条件。
        ///
        /// 【顺带解决了"自己写剪贴板"的死循环】本程序有唯一一处 Clipboard.SetText
        /// （诊断面板「复制诊断文本」），写的是制表符分隔的文件名 —— 不含夸克域名，
        /// 因此不会触发自己，不需要额外的"自写抑制"标记。
        /// </summary>
        private static string FindQuarkShareUrl(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            if (text.Length > MaxClipboardScanChars) return null;

            foreach (Match m in ClipboardUrlRx.Matches(text))
            {
                string u = m.Value;
                if (u.IndexOf("/s/", StringComparison.OrdinalIgnoreCase) < 0) continue;

                // URL 后面常紧跟从句标点或中文标点（"…/s/abc。以下略"、"（…/s/abc）"），
                // 正则可列的排除集不可能全，统一在**第一个非 URL 字符**处截断，
                // 再削掉尾部可能残留的句读（`.` `,` 在 URL 里合法，只能靠这一步收尾）。
                int cut = 0;
                while (cut < u.Length && IsUrlChar(u[cut])) cut++;
                u = u.Substring(0, cut).TrimEnd('.', ',', ';', '!', '?');

                if (!IsQuarkHost(u)) continue;
                return u;
            }
            return null;
        }

        /// <summary>URL 里合法的字符（RFC 3986 的 unreserved + 保留字 + `%`）。</summary>
        private static bool IsUrlChar(char c)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                return true;
            return "-._~:/?#[]@!$&'()*+,;=%".IndexOf(c) >= 0;
        }

        /// <summary>URL 的主机是不是夸克自己（<c>quark.cn</c> / <c>*.quark.cn</c>）。</summary>
        private static bool IsQuarkHost(string url)
        {
            try
            {
                Uri u;
                if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
                if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
                string h = u.Host;
                return h.Equals("quark.cn", StringComparison.OrdinalIgnoreCase)
                    || h.EndsWith(".quark.cn", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// 尽力读一次剪贴板文本：不是文本、或实在抢不到剪贴板时返回 null。
        ///
        /// 【为什么要重试】剪贴板是**全局独占**的：写入方还没释放时
        /// <see cref="Clipboard.GetText"/> 会抛 COMException(CLIPBRD_E_CANT_OPEN)。
        /// `WM_CLIPBOARDUPDATE` 是"写完之后"才投递的，正常第一次就能读到；
        /// 但 Office 之流会自己多持有几毫秒，所以留一个很短的退避重试。
        /// 总等待上限 ~120ms，且只在真的被占用时才会发生（正常路径一次就返回）。
        /// </summary>
        private static string TryReadClipboardText()
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    if (!Clipboard.ContainsText()) return null;
                    return Clipboard.GetText();
                }
                catch (Exception)
                {
                    Thread.Sleep(20);
                }
            }
            return null;
        }

        /// <summary>
        /// 把一条分享链接**填进界面并解析**（复用「解析」按钮那套实现，包括按钮加载态与失败提示）。
        /// 两个入口：剪贴板监听到链接、以及历史面板里点某一条。
        /// </summary>
        private void FillAndParse(string url, string passcode)
        {
            try
            {
                // ① 必须切回「分享链接」模式 —— 这不只是"让结果看得见"：
                //    LoadDirAsync 是**按 ShareMode 分流**的（分享走 ShareDetailAsync、
                //    我的网盘走 ListDirAsync），停在「我的网盘」模式下解析会把
                //    自己网盘的根目录列出来。切换本身是同步完成的
                //    （OnModeChanged 到第一个 await 之前就做完了），并且会先把网盘那边的
                //    导航存进快照，用户原来的浏览位置不会丢。
                if (ShareModeRadio.IsChecked != true)
                    ShareModeRadio.IsChecked = true;

                // ② 结果只出现在「下载」页的分享面板里（NavList 顺序：0=下载 1=任务
                //    2=设置 3=登录），所以把页面也指过去，否则用户在设置页复制的链接，
                //    程序在背后解析完他什么也看不见。
                //    ⚠️ 只切页签，不 Show / 不 Activate —— 窗口藏在托盘时静默解析，不抢焦点。
                Tabs.SelectedIndex = 0;
            }
            catch { }

            LinkBox.Text = url;
            LinkBox.CaretIndex = url.Length;

            // ③ 提取码以**本次复制的内容**为准（没有就清空）：PassBox 里可能还留着
            //    上一条分享的码，而 OnParseClick 会优先采用 PassBox 的值 ——
            //    留着它会"用错码"，解析失败时还完全看不出原因。
            PassBox.Text = passcode ?? "";

            Log("剪贴板里发现夸克分享链接，自动解析…");
            OnParseClick(null, null);
        }

        /// <summary>
        /// 窗口不可见（藏在托盘）时用托盘气泡报一句；窗口可见时不弹 ——
        /// 界面上本来就能看到结果，再弹气泡是重复打扰。
        /// </summary>
        private void NotifyIfHidden(string title, string text)
        {
            try
            {
                if (IsVisible) return;
                _tray?.ShowBalloon(title, text);
            }
            catch { }
        }

        // ---------------- 解析历史面板（2026-10-09，按 h 唤起） ----------------
        //
        // 数据在 HistoryStore（GeZi.state.json 里的 Shares），这里只管画与开关。
        // 布局照选定稿：整窗压暗 + **居中**卡片，列表按 今天 / 昨天 / 更早 分组，
        // 一行 = 文件夹图标 + 名称 + 「时间 · 分享 ID · 提取码」，悬停整行高亮。

        /// <summary>历史面板是不是开着（快捷键分支与开关都看它）。</summary>
        private bool HistoryOpen
        {
            get { return HistoryOverlay != null && HistoryOverlay.Visibility == Visibility.Visible; }
        }

        private void ShowHistoryPanel()
        {
            try
            {
                RebuildHistoryPanel();   // 每次打开都重建：这中间可能又解析过新分享
                if (HistoryOverlay == null) return;
                HistoryOverlay.Visibility = Visibility.Visible;
                // 把焦点收到浮层自己身上：否则之后随手敲的字母会钻进底下那个**看不见的**
                // 链接框（把链接改脏），或者变成文件列表的首字母跳转。
                HistoryOverlay.Focus();
            }
            catch (Exception ex)
            {
                Log("打开解析历史失败: " + ex.Message);
            }
        }

        private void HideHistoryPanel()
        {
            try { if (HistoryOverlay != null) HistoryOverlay.Visibility = Visibility.Collapsed; } catch { }
        }

        /// <summary>点遮罩（面板外的暗色区域）= 关闭。面板本身是它的兄弟节点，不会误触。</summary>
        private void OnHistoryOverlayClick(object sender, MouseButtonEventArgs e)
        {
            HideHistoryPanel();
        }

        private void OnHistoryClearClick(object sender, RoutedEventArgs e)
        {
            try
            {
                HistoryStore.ClearShareHistory();
                RebuildHistoryPanel();
                Log("已清空解析历史。");
            }
            catch (Exception ex)
            {
                Log("清空解析历史失败: " + ex.Message);
            }
        }

        /// <summary>重建列表：分组标题 + 可点行（都动态生成，见 MakeHistoryRow）。</summary>
        private void RebuildHistoryPanel()
        {
            if (HistoryListPanel == null) return;
            HistoryListPanel.Children.Clear();

            var items = HistoryStore.GetShareHistory();
            if (HistoryCountText != null)
                HistoryCountText.Text = items.Count > 0 ? "共 " + items.Count + " 条" : "";

            if (items.Count == 0)
            {
                HistoryListPanel.Children.Add(new TextBlock
                {
                    Text = "还没有解析记录。\n解析过的分享会留在这里，下次点一下就能重新打开。",
                    Style = (Style)FindResource("HistoryEmptyText"),
                });
                return;
            }

            string lastGroup = null;
            foreach (var it in items)
            {
                // 置顶的单独成组排在最前（GetShareHistory 已经把置顶的排到前面了）
                string g = it.Pinned ? "置顶" : HistoryGroupOf(it.Time);
                if (!string.Equals(g, lastGroup, StringComparison.Ordinal))
                {
                    HistoryListPanel.Children.Add(new TextBlock
                    {
                        Text = g,
                        Style = (Style)FindResource("HistoryGroupLabel"),
                    });
                    lastGroup = g;
                }
                HistoryListPanel.Children.Add(MakeHistoryRow(it));
            }
        }

        /// <summary>分组标签：按"距今天多久"给粗粒度分组（今天 / 昨天 / 更早）。</summary>
        private static string HistoryGroupOf(string raw)
        {
            DateTime t;
            if (!DateTime.TryParse(raw, out t)) return "更早";
            DateTime today = DateTime.Today;
            if (t.Date == today) return "今天";
            if (t.Date == today.AddDays(-1)) return "昨天";
            return "更早";
        }

        /// <summary>行里的时间：「今天 11:58」/「昨天 21:03」/「10-06 17:20」。</summary>
        private static string FormatHistoryTime(string raw)
        {
            DateTime t;
            if (!DateTime.TryParse(raw, out t)) return raw ?? "";
            DateTime today = DateTime.Today;
            if (t.Date == today) return "今天 " + t.ToString("HH:mm");
            if (t.Date == today.AddDays(-1)) return "昨天 " + t.ToString("HH:mm");
            return t.ToString("MM-dd HH:mm");
        }

        /// <summary>行里的副行：「今天 11:58 · /s/1f842a86 · 提取码 4k3p」。</summary>
        private static string HistoryMetaOf(ShareHistoryEntry it)
        {
            var sb = new StringBuilder(FormatHistoryTime(it.Time));
            if (!string.IsNullOrEmpty(it.PwdId))
            {
                sb.Append(" · /s/");
                sb.Append(it.PwdId.Length > 8 ? it.PwdId.Substring(0, 8) : it.PwdId);
            }
            if (!string.IsNullOrEmpty(it.Passcode))
                sb.Append(" · 提取码 ").Append(it.Passcode);
            return sb.ToString();
        }

        /// <summary>造一行历史。整行是个 Button，点 = 重新解析那条分享。</summary>
        private UIElement MakeHistoryRow(ShareHistoryEntry it)
        {
            // 图标：文件夹用"警示色的文件夹"（与文件列表里的文件夹图标同一套语言）；
            // 没有文件夹的（根目录只有散文件）用文件图标 + 次要色。
            var icon = new Border
            {
                Width = 34, Height = 34,
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            icon.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty,
                "SurfaceSunkenBrush");
            icon.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty,
                "BorderBrushSoft");

            var glyph = new ShapePath
            {
                Data = (Geometry)FindResource(it.IsFolder ? "IconFolder" : "IconFile"),
                Stretch = Stretch.Uniform,
                Width = 17, Height = 17,
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            glyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
                it.IsFolder ? "WarningBrush" : "TextTertiaryBrush");
            icon.Child = glyph;

            var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            meta.Children.Add(new TextBlock
            {
                Text = it.Name ?? "",
                Style = (Style)FindResource("HistoryNameText"),
            });
            meta.Children.Add(new TextBlock
            {
                Text = HistoryMetaOf(it),
                Style = (Style)FindResource("HistoryMetaText"),
            });

            // ⚠️ 外层用 Grid 而不是横向 StackPanel：名称那个省略号只在**宽度被约束**时才生效，
            //    StackPanel 会用无限宽去量孩子，长名字就直接撑出去、不截断。
            var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
            meta.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(icon, 0);
            Grid.SetColumn(meta, 1);
            row.Children.Add(icon);
            row.Children.Add(meta);

            var btn = new Button
            {
                Content = row,
                Style = (Style)FindResource("HistoryRowButton"),
                Tag = it,     // 点击时取回这一条（见 OnHistoryItemClick）
                ToolTip = "重新解析：" + (it.Name ?? ""),
            };
            btn.Click += OnHistoryItemClick;
            btn.ContextMenu = MakeHistoryMenu(it);
            return btn;
        }

        /// <summary>行上右键：置顶 / 取消置顶、删除这条记录。</summary>
        private ContextMenu MakeHistoryMenu(ShareHistoryEntry it)
        {
            // ⚠️ 菜单项是**每次重建行时新建**的，所以 Header 直接按当前状态定；
            //    操作完都会 RebuildHistoryPanel()，标签跟着刷新（不用做双向绑定）。
            var menu = new ContextMenu();

            var pin = new MenuItem { Header = it.Pinned ? "取消置顶" : "置顶" };
            pin.Click += (s, e) =>
            {
                try
                {
                    HistoryStore.SetSharePinned(it.PwdId, !it.Pinned);
                    RebuildHistoryPanel();
                    Log(it.Pinned ? "已取消置顶：" + it.Name : "已置顶：" + it.Name);
                }
                catch (Exception ex) { Log("置顶操作失败: " + ex.Message); }
            };

            var del = new MenuItem { Header = "删除这条记录" };
            del.Click += (s, e) =>
            {
                try
                {
                    HistoryStore.RemoveShareHistory(it.PwdId);
                    RebuildHistoryPanel();
                    Log("已从解析历史里删除：" + it.Name);
                }
                catch (Exception ex) { Log("删除记录失败: " + ex.Message); }
            };

            menu.Items.Add(pin);
            menu.Items.Add(del);
            return menu;
        }

        /// <summary>
        /// 文件区工具栏上那个「解析历史」按钮（h 键之外看得见的入口）。
        /// 记的是**解析过的分享**（pwd_id / 提取码），与任务页无关 ——
        /// 任务页原来的「下载历史」按钮已经移除（任务列表本身就会跨重启保留）。
        /// </summary>
        private void OnShareHistoryButtonClick(object sender, RoutedEventArgs e)
        {
            ShowHistoryPanel();
        }

        /// <summary>点历史里的一条 = 重新解析那条分享。</summary>
        private void OnHistoryItemClick(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var it = btn != null ? btn.Tag as ShareHistoryEntry : null;
            if (it == null || string.IsNullOrEmpty(it.PwdId)) return;

            HideHistoryPanel();

            // 原来那条链接指向某个子目录时，把 fid 也拼回去：OnParseClick 会把它当成
            // start_fid，于是回到上次看的那个目录，而不是分享根目录。
            string url = "https://pan.quark.cn/s/" + it.PwdId;
            if (!string.IsNullOrEmpty(it.StartFid) && it.StartFid != "0")
                url += "/" + it.StartFid;

            Log("从解析历史打开：「" + (it.Name ?? it.PwdId) + "」");
            FillAndParse(url, it.Passcode);
        }

        /// <summary>解析成功后记一条历史（供 h 面板）。</summary>
        private void RecordShareHistory(string pwdId, string passcode, string startFid)
        {
            try
            {
                // 名字取"解析后看到的第一个文件夹" —— 这正是用户认这条记录的依据；
                // 分享根目录只有散文件时退化成「（根目录）· N 个文件」。
                var firstDir = _files.FirstOrDefault(f => f.IsDir);
                string name = firstDir != null
                    ? firstDir.Name
                    : "（根目录）· " + _files.Count + " 个文件";
                HistoryStore.AddShareHistory(pwdId, passcode, startFid, name, firstDir != null);
            }
            catch { }
        }

        // ---------------- 目录浏览（分享/网盘统一） ----------------

        /// <summary>上一次「单击进入文件夹」的时刻（<see cref="DiagClock"/> 的单调计时）。</summary>
        private long _lastEnterFolderTick;

        /// <summary>
        /// 双击间隔，换算成 <see cref="DiagClock"/> 的刻度。
        /// 取**系统设置**（<see cref="NativeMethods.GetDoubleClickTime"/>）而不是自己写死毫秒数
        /// —— 用户把双击速度调慢时，判据要跟着变。
        /// </summary>
        private static readonly long DoubleClickTicks =
            (long)NativeMethods.GetDoubleClickTime() * DiagClock.Frequency / 1000L;

        /// <summary>
        /// 进入文件夹（单击文件夹行触发）。
        ///
        /// 【为什么是 async void】它由点击事件直接触发，没有调用方要等结果 ——
        /// 与 <see cref="OnParseClick"/> 等既有处理器同一形态。目录请求失败已在
        /// <see cref="LoadDirAsync"/> 内部消化（打日志），不会漏到这里。
        /// </summary>
        private async void EnterFolder(ShareFileItem dir)
        {
            // ① 目录正在加载时不响应：列表要等网络返回才刷新，这段窗口里**旧行还能点**。
            //    不挡的话会连着推两次 _fidStack / _pathList（面包屑多一层、"上一级"错位），
            //    两个 LoadDirAsync 还会并发赛跑，谁后回来谁覆盖。
            if (_loadingDir) return;

            // ② 吞掉"双击的第二下"。用户习惯性双击时第一下已经把目录换掉了，
            //    第二下的落点要么还在旧行上、要么落在刷新后的新行上 ——
            //    后者会莫名其妙再进一层（甚至进到完全无关的目录）。
            //    判据是系统双击间隔：间隔之内算同一次双击，不算两次点击。
            long now = DiagClock.GetTimestamp();
            if (now - _lastEnterFolderTick < DoubleClickTicks) return;
            _lastEnterFolderTick = now;

            _fidStack.Push(_curFid);
            _curFid = dir.Fid;
            _pathList.Add(dir.Name);
            UpdatePathText();
            await LoadDirAsync(dir.Fid);
        }

        /// <summary>
        /// 文件列表的**粘滞多选模式**（2026-10-21 新增，按 s 进入 / 再按 s 或 Esc 退出）。
        ///
        /// <para>【为什么要做】用户反馈「一直需要按 shift 才行 太麻烦」。
        /// 原来只能 Shift+点击离散多选 —— 每点一个都得压着 Shift，
        /// 选十几个文件时手一直绷着，很难受。</para>
        ///
        /// <para>【与 Shift 的分工】两者都能多选，但性质不同：
        /// · Shift+点击 = **一次性**修饰：按着才有效，松手即恢复"单击进入文件夹"；
        /// · s 多选模式 = **粘滞**开关：按一下进入，之后点选全程不用再按键，再按 s 或 Esc 退出。
        /// 进入后文件夹也不再"单击进入"（否则选文件夹时会被弹进目录），
        /// 这正是多选场景想要的。</para>
        /// </summary>
        private bool _fileSelectMode;

        /// <summary>
        /// 当前文件列表里是否有可选的条目（考虑筛选词）。
        /// <para>用于决定多选模式能否进入 —— 空列表里进多选模式毫无意义，
        /// 只会看到一条提示条挂在那里，用户反馈过"没有文件也会展示多选提示"。</para>
        /// </summary>
        private bool HasSelectableFiles()
        {
            try
            {
                // 有筛选词时以"视图里的可见条数"为准：列表里显示 0 条就没得选
                if (!string.IsNullOrEmpty(_fileFilter))
                    return _filesView != null && _filesView.Cast<object>().Any();
                return _files != null && _files.Count > 0;
            }
            catch { return _files != null && _files.Count > 0; }
        }

        /// <summary>
        /// 进入多选模式。<paramref name="silent"/> = true 时不写日志（用于自动重进等场景）。
        /// </summary>
        /// <returns>是否真的进入了（列表空时返回 false）。</returns>
        private bool EnterFileSelectMode(bool silent = false)
        {
            if (_fileSelectMode) return true;

            // ⚠️ 空列表不进入：没有任何可选项时提示条只会是噪音
            if (!HasSelectableFiles())
            {
                if (!silent) Log("当前目录没有可选择的文件，多选模式不需要开启。");
                return false;
            }

            _fileSelectMode = true;
            try
            {
                if (FileSelectBar != null) FileSelectBar.Visibility = Visibility.Visible;
            }
            catch { }
            UpdateFileSelectCount();
            if (!silent)
                Log("已进入多选模式：点文件即可加选 / 取消（文件夹不再进入目录）；再按 s 或 Esc 退出。");
            return true;
        }

        /// <param name="clearSelection">退出时是否清空已选（Esc 默认清空，与任务页 z 模式一致）。</param>
        /// <param name="silent">true = 不写日志。用于"列表变空自动退出"这类用户没主动操作的场景。</param>
        private void ExitFileSelectMode(bool clearSelection = true, bool silent = false)
        {
            if (!_fileSelectMode) return;
            _fileSelectMode = false;
            try
            {
                if (FileSelectBar != null) FileSelectBar.Visibility = Visibility.Collapsed;
            }
            catch { }
            if (clearSelection) ClearFileSelection();
            UpdateFileSelectCount();
            if (!silent) Log("已退出多选模式。");
        }

        /// <summary>
        /// 多选提示条上的「已选 N 项（共 X MB）」。
        ///
        /// <para>【2026-10-22 用户要求】「在提示：已选多少项后面加上共：多少MB/G」。
        /// 只说"选了几项"用户没有体积概念，加了总大小才能判断"这一批大概多大"、
        /// 值不值得开下载。</para>
        /// </summary>
        private void UpdateFileSelectCount()
        {
            try
            {
                if (FileSelectCountText == null) return;
                int n = 0;
                long bytes = 0;
                if (FileList != null)
                {
                    foreach (var it in FileList.SelectedItems)
                    {
                        var f = it as ShareFileItem;
                        if (f == null) continue;
                        n++;
                        if (f.Size > 0) bytes += f.Size;
                    }
                }
                FileSelectCountText.Text = bytes > 0
                    ? "已选 " + n + " 项（共 " + Util.FormatSize(bytes) + "）"
                    : "已选 " + n + " 项";
            }
            catch { }
        }

        private void OnFileListSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 只在多选模式下维护计数，平时这个提示条是收起的
            if (_fileSelectMode) UpdateFileSelectCount();
        }

        /// <summary>
        /// 文件列表的左键点击策略（在 PreviewMouseLeftButtonDown / 隧道阶段统一裁决）：
        ///   · **s 多选模式**（粘滞）：点任意行 = 切换该行选中，文件夹不进入；
        ///   · **单击文件夹 = 直接进入**（2026-10-08 用户要求「单击就直接打开文件夹」）；
        ///   · **Shift+点击 = 离散多选**（只切换点中的这一项），**不做连选**；
        ///   · **Ctrl+点击 = 只选中这一个**，不进目录；
        ///   · 单击**文件** = WPF 默认行为（选中）。
        ///
        /// 【Shift 为什么必须自己写】WPF 的 `SelectionMode="Extended"` 里：
        ///   · Ctrl+点击  = 切换单项（离散多选）
        ///   · **Shift+点击 = 连选**：把"锚点项"到当前项**整段**都选上
        /// 用户要的是「可以使用 shift 多选，但**不是连选**」——
        /// 也就是 Shift 只作用于**点中的那一个**，不拉区间。
        /// 所以在隧道阶段把它拦下来自己处理，并 `e.Handled = true` 阻止 WPF 的连选。
        /// ⚠️ 不拦的话会怎样：选中第 1 个、Shift+点第 5 个 → 1~5 全被选上（连选）。
        ///
        /// 【单击变成"进入"之后，怎么**选中文件夹**】「下载选中 / 删除网盘文件」都要求先选中，
        /// 而单纯单击文件夹已经变成"进入"了，所以选中文件夹改走这三条路：
        /// Shift+点击、Ctrl+点击，或**右键**（右键会把光标下的那一行选中，
        /// 见 <see cref="OnFileListRightClick"/>），随后菜单里就有「下载选中」。
        /// </summary>
        private void OnListPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            var item = FindListBoxItem(e.OriginalSource as DependencyObject);

            // ⓪ 任务页的「删除选择模式」：点击语义整体换一套 ——
            //    点行 = 切换该行、点分组头 = 整组切换、点空白 = 开始拖拽框选。
            //    必须放在"item == null 直接 return"之前，否则空白的框选起点就被吞了。
            if (_taskSelectMode && ReferenceEquals(sender, TaskList))
            {
                e.Handled = true;

                // ⚠️ 顺序不能反：分组内的每一行都在 GroupItem 里，
                //    先查 GroupItem 会把「点某一行」也当成「点文件夹头」，
                //    结果只能整组选中、无法单独选一个文件。
                if (item != null)
                {
                    item.IsSelected = !item.IsSelected;
                    try { item.Focus(); } catch { }
                    UpdateTaskSelectCount();
                    return;
                }
                // 只有点在文件夹卡片的标题栏上才算「选整组」
                var hdr = FindAncestor<ToggleButton>(e.OriginalSource as DependencyObject);
                var gi = hdr == null ? null : FindAncestorGroupItem(hdr);
                if (gi != null)
                {
                    ToggleTaskGroupSelection(gi);
                    return;
                }
                BeginTaskBandDrag(e);
                return;
            }

            if (item == null)
                return;   // 点在空白处：交给默认处理（清空选择）

            // ⓪.5 多选模式（粘滞，2026-10-21）：点任意行 = 切换该行选中。
            //     必须排在 Shift / Ctrl / "单击文件夹进入" 之前 ——
            //     这个模式存在的意义就是"点一下就切换，不用按修饰键、也不会被弹进目录"。
            //     放在 item == null 之后：点空白处仍然走默认（清空选择），符合直觉。
            if (_fileSelectMode && ReferenceEquals(sender, FileList))
            {
                e.Handled = true;
                item.IsSelected = !item.IsSelected;
                try { item.Focus(); } catch { }
                UpdateFileSelectCount();
                return;
            }

            // ① Shift+点击 = 离散多选（只切换点中的这一项）
            if ((mods & ModifierKeys.Shift) != 0)
            {
                e.Handled = true;                    // 关键：阻止 WPF 的"连选"
                item.IsSelected = !item.IsSelected;
                try { item.Focus(); } catch { }
                return;
            }

            // ② Ctrl+点击 = **按用户要求关掉**（原来它是"切换单项"）。
            //    现在让它表现为"只选中这一个"，且**不进目录** ——
            //    这是单击变"进入"之后，选中文件夹的主要入口之一。
            if ((mods & ModifierKeys.Control) != 0)
            {
                e.Handled = true;
                var lb = sender as ListBox;
                if (lb != null) lb.SelectedItems.Clear();
                item.IsSelected = true;
                try { item.Focus(); } catch { }
                return;
            }

            // ③ 不按修饰键、点是文件夹 = 直接进入。
            //    `e.Handled = true` 是刻意的：不给 WPF 默认的"选中"处理 ——
            //    否则这一行会先高亮一下、紧接着被新目录刷掉，白闪一帧。
            var dir = FileList.ItemContainerGenerator.ItemFromContainer(item) as ShareFileItem;
            if (dir != null && dir.IsDir)
            {
                e.Handled = true;
                EnterFolder(dir);
                return;
            }
            // ④ 单击文件 / 其它情况：完全走 WPF 默认行为
        }

        /// <summary>从鼠标命中的可视元素往上找 ListBoxItem。</summary>
        private static ListBoxItem FindListBoxItem(DependencyObject d)
        {
            while (d != null)
            {
                if (d is ListBoxItem it) return it;
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        /// <summary>
        /// 入队成功后清掉文件列表的选中。
        ///
        /// 【2026-10-13】用户要求：「用 Shift 选中文件夹之后，再按 d 免转存，还是会选中
        /// —— 记得发送后取消选中」。留着高亮的坏处：看不出东西"已经交出去了"，
        /// 而且顺手再按一次 d/x 会把同一批文件**重复入队**。
        /// </summary>
        private void ClearFileSelection()
        {
            try { FileList.SelectedItems.Clear(); } catch { }
            // 【2026-10-21】多选模式下按 d / x 发送后只清选择、**保留模式**（用户要求），
            // 但提示条上的「已选 N 项」必须跟着归零 —— 否则它还显示着刚才那批数量，
            // 用户会以为没发送成功。SelectedItems.Clear 会触发 SelectionChanged，
            // 那里已经刷过一次；这里再刷一次是为了覆盖"本来就没选中"等不触发事件的情况。
            UpdateFileSelectCount();
        }

        /// <summary>
        /// 搜索框里按 Esc：退出焦点、把键盘交回文件列表。
        ///
        /// <para>【2026-10-21】全局的「Esc 退出任何输入框」已经统一在
        /// <see cref="OnShortcutKeyDown"/> 里做了（见 ReleaseTypingFocus），
        /// 这里只保留一份最短路径 —— 因为搜索框的 KeyDown 比窗口的 PreviewKeyDown
        /// 更早触发，不在这里挡一下会走两遍（日志会打两行）。
        /// 行为与统一逻辑一致：焦点回列表、**筛选内容保留**。</para>
        /// </summary>
        private void OnFileFilterKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            try { FileList.Focus(); } catch { }
            Log("已退出搜索框（筛选内容保留）");
        }

        /// <summary>文件列表筛选框变化：只按名称包含关键字过滤，不改动底层集合。</summary>
        private void OnFileFilterChanged(object sender, TextChangedEventArgs e)        {
            _fileFilter = (FileFilterBox.Text ?? "").Trim();
            try { _filesView?.Refresh(); } catch { }
        }

        // ---------------- 目录分页 / 缓存（2026-10-09） ----------------
        //
        // 【要解决的问题】一个分享目录可能有几千项，原来的做法有三处叠加的卡：
        //   ① LoadDirAsync 走 ShareDetailAsync / ListDirAsync —— 那是"从第 1 页循环到
        //      第 200 页、把整个目录攒完才返回"，点进去要等十几秒才有东西；
        //   ② 攒完再一次性 Add 几千条进 _files，而集合每变一次都会走 UpdateEmptyStates
        //      （里面 _filesView.Cast<object>().Count() 是**整表枚举**）→ O(n²)；
        //   ③ 退出再进（按 a 返回）把这套重跑一遍。
        // 现在：
        //   · 只取**一页**就先显示，界面里一次只"露" <see cref="DirRevealSize"/> 条；
        //   · 快滑到底部再取下一页（用户要求「每次只加载 10 个，滑到底部再继续加载」）；
        //   · 取到的按 fid 缓存，返回上一层再进来**一个请求都不发**（点「刷新」才重取）；
        //   · UpdateEmptyStates 合并调用。

        /// <summary>界面里一次"露"多少条（用户要求 10）。</summary>
        private const int DirRevealSize = 10;

        /// <summary>一个目录已取到的内容 + 分页游标（缓存里存的就是它）。</summary>
        private sealed class DirCacheEntry
        {
            /// <summary>已取到的条目（已按"目录在前、文件在后"排好）。</summary>
            public List<ShareFileItem> Items = new List<ShareFileItem>();
            /// <summary>下一批要取接口的第几页。</summary>
            public int NextPage = 2;
            /// <summary>接口那边还有没有下一页。</summary>
            public bool HasMore;
            /// <summary>已经放进 <c>_files</c> 的条数。</summary>
            public int RevealCount;
            /// <summary>
            /// 离开这个目录时列表的滚动位置（像素）。
            /// 按 a 返回时把滚动条放回原处 —— 否则"内容回来了、位置却在最上面"。
            /// </summary>
            public double ScrollOffset;
        }

        /// <summary>目录缓存：缓存键 → 已取内容。返回上一层/重进时直接铺开，不再请求。</summary>
        private readonly Dictionary<string, DirCacheEntry> _dirCache =
            new Dictionary<string, DirCacheEntry>();

        /// <summary>当前目录对应的那一份缓存（没加载过时为 null）。</summary>
        private DirCacheEntry _curDir;

        /// <summary>
        /// 当前列表是**从本地快照铺出来的**（2026-10-21）。
        /// <para>这种状态下没有可用的会话凭证（缓存恢复刻意零请求），
        /// 所以不自动续页，也不该悄悄失败 —— 由用户点「刷新」触发真正的加载。
        /// 任何一次真实加载（LoadDirAsync / OnRefresh）都会把它清掉。</para>
        /// </summary>
        private bool _curDirFromCache;

        /// <summary>文件列表模板里的那个 ScrollViewer（滚动时顺手记下来，恢复位置要用）。</summary>
        private ScrollViewer _fileScroller;

        /// <summary>目录加载的"代"：切目录后，旧的异步结果要作废（否则会覆盖新目录）。</summary>
        private int _dirLoadToken;

        /// <summary>正在取下一批（防重入：滚动事件会连发好几条）。</summary>
        private bool _dirLoadingMore;

        /// <summary>
        /// 缓存键。**必须带上模式**：分享的根 fid 和自己网盘的根 fid 都是 "0"，
        /// 不带前缀两个模式会串台（切过去会看到另一个模式的目录）。
        /// </summary>
        private string DirCacheKey(string fid)
        {
            return (ShareMode ? "s:" : "d:") + (fid ?? "");
        }

        /// <summary>取一页目录（分享模式 / 网盘模式走各自接口）。</summary>
        private Task<QuarkClient.DirPage> FetchDirPageAsync(string fid, int page)
        {
            return ShareMode
                ? _client.ShareDetailPageAsync(_pwdId, _stoken, fid, page)
                : _client.ListDirPageAsync(fid, page);
        }

        /// <summary>目录在前、文件在后，同类按名称排序 —— 与 Python Flet 版一致。</summary>
        private static List<ShareFileItem> SortDirEntries(IList<QuarkFileEntry> items)
        {
            return items
                .OrderBy(it => it.IsDir ? 0 : 1)
                .ThenBy(it => it.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(it => new ShareFileItem
                {
                    Fid = it.Fid, Name = it.Name, Size = it.Size,
                    IsDir = it.IsDir, Token = it.Token,
                })
                .ToList();
        }

        /// <summary>
        /// 把一份目录内容铺到界面上：清列表 → 露第一批 → 重起大小求和。
        /// </summary>
        /// <param name="restore">
        /// true = 这是从缓存**返回**到之前看过的目录：把上次已铺开的条数和滚动位置
        /// 一起还原（用户要求「用 a 回退时记住上次滚动的位置和加载的内容，
        /// 不要重复加载」）。false = 首次进入，从头铺。
        /// </param>
        private void ApplyDirView(DirCacheEntry entry, bool restore = false)
        {
            // ⚠️ 必须先取值再动列表：下面 _files.Clear() 会触发 ScrollChanged，
            //    而那个处理器会把 entry.ScrollOffset 覆写成 0（因为列表清空就在顶部）。
            int wantCount = restore ? entry.RevealCount : 0;
            double wantOffset = restore ? entry.ScrollOffset : 0;

            _curDir = entry;

            // 换目录时清掉上一次的筛选词，避免用户以为"新目录是空的"
            if (!string.IsNullOrEmpty(_fileFilter))
            {
                _fileFilter = "";
                try { FileFilterBox.Text = ""; } catch { }
            }

            _files.Clear();
            ResetDirSizeScan();     // 换目录了：上一轮没算完的不再算

            if (restore)
            {
                // 已算过的文件夹大小在 _dirSizeCache 里，恢复时不会重新发请求。
                RevealUpTo(wantCount);
                RestoreScrollOffset(wantOffset);
            }
            else
            {
                entry.RevealCount = 0;
                entry.ScrollOffset = 0;
                RevealNextBatch();
            }
            UpdateDirMoreHint();
        }

        /// <summary>
        /// 一次性把 <paramref name="count"/> 条以内已看过的内容铺回列表。
        /// 用于"从缓存恢复视图"：不走 <see cref="RevealNextBatch"/> 的 10 条一批，
        /// 免得几千项时一路批处理刷几十次 UI。
        /// </summary>
        private void RevealUpTo(int count)
        {
            if (_curDir == null) return;
            int to = Math.Min(count, _curDir.Items.Count);
            if (to <= _files.Count) return;

            var freshDirs = new List<ShareFileItem>();
            for (int i = _files.Count; i < to; i++)
            {
                var it = _curDir.Items[i];
                _files.Add(it);
                if (it.IsDir) freshDirs.Add(it);
            }
            _curDir.RevealCount = to;
            QueueDirSizeScan(freshDirs);
        }

        /// <summary>把滚动条放回上次离开这个目录时的位置。</summary>
        private void RestoreScrollOffset(double offset)
        {
            if (offset <= 0) return;
            // 必须等布局跑完（列表内容变了、可滚动高度才算得出来），
            // 否则 ScrollToVerticalOffset 会被夹到 0。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var sv = _fileScroller ?? FindDescendantScrollViewer(FileList);
                    if (sv == null) return;
                    _fileScroller = sv;
                    sv.ScrollToVerticalOffset(offset);
                }
                catch { }
            }), DispatcherPriority.Loaded);
        }

        private static ScrollViewer FindDescendantScrollViewer(DependencyObject root)
        {
            if (root == null) return null;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                if (c is ScrollViewer sv) return sv;
                var r = FindDescendantScrollViewer(c);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>把下一批（<see cref="DirRevealSize"/> 条）放进列表。</summary>
        private void RevealNextBatch()
        {
            if (_curDir == null) return;
            int from = _curDir.RevealCount;
            int to = Math.Min(from + DirRevealSize, _curDir.Items.Count);
            if (to <= from) return;

            var freshDirs = new List<ShareFileItem>();
            for (int i = from; i < to; i++)
            {
                var it = _curDir.Items[i];
                _files.Add(it);
                if (it.IsDir) freshDirs.Add(it);
            }
            _curDir.RevealCount = to;

            // 只给"刚露出来这几个文件夹"算大小：几千个目录不可能都算，
            // 而且用户还没滚到的那些他根本看不见。
            QueueDirSizeScan(freshDirs);
        }

        /// <summary>
        /// 滑到底部时继续加载：缓冲里还有没露的直接露，取完了才去要下一页。
        /// </summary>
        private async void LoadMoreItems()
        {
            if (_dirLoadingMore || _curDir == null) return;

            if (_files.Count < _curDir.Items.Count)
            {
                RevealNextBatch();
                UpdateDirMoreHint();
                return;
            }
            if (!_curDir.HasMore) return;      // 真到底了

            // 【2026-10-21】从本地缓存恢复出来的目录，**不自动续页**。
            // 原因：缓存铺出来时并没有取过 stoken / 会话（这正是"零请求"的代价），
            // 一滚到底就去请求必然失败（实测日志：guest missing pwd_id or stoken）。
            // 用户要的语义也是"只有点击刷新才检查更新"，所以这里直接提示去点刷新，
            // 把网络动作留给用户显式触发。
            if (_curDirFromCache)
            {
                UpdateDirMoreHint();
                return;
            }

            _dirLoadingMore = true;
            int token = _dirLoadToken;
            string fid = _curFid;
            try
            {
                var page = await FetchDirPageAsync(fid, _curDir.NextPage);
                if (token != _dirLoadToken) return;   // 期间又切了目录 → 作废
                _curDir.Items.AddRange(SortDirEntries(page.Items));
                _curDir.NextPage++;
                _curDir.HasMore = page.HasMore;
                RevealNextBatch();
                Log("继续加载：本页 " + page.Items.Count + " 项（已取 "
                    + _curDir.Items.Count + " 项）");
            }
            catch (Exception ex)
            {
                Log("加载更多失败: " + ex.Message);
            }
            finally
            {
                _dirLoadingMore = false;
                UpdateDirMoreHint();
            }
        }

        /// <summary>列表底部那行提示：还有没加载完的就说清楚。</summary>
        private void UpdateDirMoreHint()
        {
            try
            {
                if (FileMoreHint == null) return;
                bool more = _curDir != null
                    && (_curDir.HasMore || _files.Count < _curDir.Items.Count);
                if (!more)
                {
                    FileMoreHint.Visibility = Visibility.Collapsed;
                    return;
                }
                // 从本地快照铺出来的列表：下面还有内容，但需要用户主动刷新才能取
                // （此刻没有会话凭证，自动请求必然失败 —— 见 _curDirFromCache 注释）
                FileMoreHint.Text = _curDirFromCache
                    ? "已显示 " + _files.Count + " 项 · 这是本地缓存，点「刷新」加载最新内容"
                    : "已显示 " + _files.Count + " 项 · 下面还有，下滑继续加载";
                FileMoreHint.Visibility = Visibility.Visible;
            }
            catch { }
        }

        /// <summary>
        /// 文件列表滚动：快到底部就继续加载。
        /// ⚠️ 只在**往下滚**（或内容变高）时判，否则往上滚回顶部也会触发一遍。
        /// 另外顺手把当前滚动位置记进当前目录的缓存里，供按 a 返回时还原。
        /// </summary>
        private void OnFileListScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            try
            {
                var sv = e.OriginalSource as ScrollViewer;
                if (sv == null) return;
                if (_fileScroller == null) _fileScroller = sv;
                if (_curDir != null && e.VerticalChange != 0)
                    _curDir.ScrollOffset = sv.VerticalOffset;

                if (e.VerticalChange < 0) return;
                // 距底部还剩不到一屏就不算"到底"：一屏的高度 ≈ 视口高度，够宽松了
                if (sv.ScrollableHeight - sv.VerticalOffset > sv.ViewportHeight) return;
                LoadMoreItems();
            }
            catch { }
        }

        /// <param name="failed">
        /// 出参：本次加载是否**抛异常失败**（区别于"目录真的是空的"）。
        /// 恢复浏览位置时用它在失效 fid 上回退到根目录，见 RestoreChainThenLoadAsync。
        /// </param>
        private async Task LoadDirAsync(string fid, Action onFailed = null)
        {
            bool failed = false;
            // 真实加载开始了 → 这份列表不再是"快照铺出来的"，允许后续自动续页
            _curDirFromCache = false;
            SetBusy(true);
            // 【纯皮肤】置位加载态并立刻刷新一次 —— 让"正在加载…"在请求发出前
            // 就出现，而不是等 SetBusy 的按钮禁用生效后才补上（会闪一下空状态）。
            _loadingDir = true;
            UpdateEmptyStates();
            int token = ++_dirLoadToken;
            try
            {
                // ① 缓存命中 → 直接铺开，**一个请求都不发**（用户反馈"进文件夹再按 a
                //    返回又要重新加载，很卡"）。
                //    并且按 restore 还原上次的滚动位置与已铺开条数。
                DirCacheEntry hit;
                if (_dirCache.TryGetValue(DirCacheKey(fid), out hit))
                {
                    ApplyDirView(hit, restore: true);
                    LogDetail("目录来自缓存: 共 " + hit.Items.Count + " 项，已恢复 "
                        + hit.RevealCount + " 项（未重新请求）");
                    return;                }

                // ② 只取第一页就先显示，剩下的等滚到底部再取
                var page = await FetchDirPageAsync(fid, 1);
                if (token != _dirLoadToken) return;   // 期间又切了目录 → 这份结果作废

                var entry = new DirCacheEntry
                {
                    Items = SortDirEntries(page.Items),
                    NextPage = 2,
                    HasMore = page.HasMore,
                };
                _dirCache[DirCacheKey(fid)] = entry;
                ApplyDirView(entry);
                LogDetail("目录加载完成: 本页 " + entry.Items.Count + " 项"
                    + (entry.HasMore ? "，下面还有（往下滑继续加载）" : ""));
            }
            catch (Exception ex)
            {
                Log("加载目录失败: " + ex.Message);
                // 【2026-10-21】把失败标出来交给调用方。
                // 恢复浏览位置时需要区分"目录真的是空的"和"这个 fid 根本打不开"——
                // 前者算恢复成功（用户上次停在一个空目录），后者必须回退到根目录。
                // 不加这个标记的话，失效 fid 会被当成"空目录"静默通过（实测踩到）。
                failed = true;
            }
            finally
            {
                _loadingDir = false;
                SetBusy(false);
                // _files.CollectionChanged 在清空/填充时已经触发过 UpdateEmptyStates，
                // 但"加载失败且一条都没有"这条路径不会动集合 —— 补一次保证加载态收掉。
                UpdateEmptyStates();

                // 【2026-10-21】每次目录加载完成（含缓存命中）都记一次浏览位置。
                // 为什么放 finally 而不是每个导航入口各写一遍：
                // 进子目录 / 上级 / 面包屑跳转 / 恢复位置 全都汇流到 LoadDirAsync，
                // 在这里记最不容易漏；而且缓存命中那条路径是提前 return 的，
                // 只有 finally 能覆盖到它。
                // 写盘由 SettingsStore.Save 内部合并，连续点几下上级目录不会产生多次落盘。
                //
                // ⚠️ 失败时**不记**：否则失效 fid 会被写回设置，
                // 下次启动又拿着同一个坏 fid 去恢复（等于把错误固化下来）。
                if (!failed)
                {
                    SaveBrowsePosition();
                    // 【2026-10-21】同时把列表内容存成快照 —— 用户要求"存在本地，
                    // 不然每次打开都重复请求"。只存层级的话，下次启动还是得发请求。
                    CaptureBrowseCache();
                }
                else { try { onFailed?.Invoke(); } catch { } }
            }
        }

        // ---------------- 文件夹大小（后台递归求和） ----------------

        /// <summary>
        /// 已算出的目录大小缓存（缓存键 → 字节数）。换目录不清，回来时能秒显示。
        /// ⚠️ 键要带模式前缀（见 <see cref="DirSizeKey"/>）—— 分享根与网盘根的 fid 都是 "0"。
        /// </summary>
        private readonly Dictionary<string, long> _dirSizeCache = new Dictionary<string, long>();

        /// <summary>待算大小的文件夹**队列**（随"露出来"的条目增长）。</summary>
        private readonly List<ShareFileItem> _dirSizeQueue = new List<ShareFileItem>();

        /// <summary>队列是否正在被消费（空了就置回 false）。</summary>
        private bool _dirSizeRunning;

        /// <summary>本轮（同一次目录加载）还能发多少次请求。</summary>
        private int _dirSizeBudget;

        /// <summary>当前这轮目录大小扫描的取消源。换目录/重进时取消上一轮。</summary>
        private CancellationTokenSource _dirSizeCts;

        /// <summary>
        /// 单次目录扫描的**请求预算**上限。
        /// </summary>
        /// <remarks>
        /// 递归求和是"每个子目录一次请求"。一个几千文件的分享可能展开成几百次请求 ——
        /// 既慢又容易踩到风控。所以给个预算，超了就停手（剩下的标成「—」而不是 0），
        /// 宁可少算也不要因为一个后台功能把接口打爆。
        /// </remarks>
        private const int DirSizeRequestBudget = 300;

        /// <summary>大小缓存的键：与目录缓存同理，带模式前缀，免得两个模式串台。</summary>
        private string DirSizeKey(string fid)
        {
            return (ShareMode ? "s:" : "d:") + (fid ?? "");
        }

        /// <summary>换目录时重置：停掉上一轮、清空待办、重置预算。</summary>
        private void ResetDirSizeScan()
        {
            try { _dirSizeCts?.Cancel(); } catch { }
            _dirSizeCts = new CancellationTokenSource();
            _dirSizeQueue.Clear();
            _dirSizeBudget = DirSizeRequestBudget;
        }

        /// <summary>
        /// 把要算大小的目录**追加**进队列（已在缓存里的直接填上）。
        ///
        /// 【为什么改成队列】原来是"每调一次就取消上一轮、只算这次给的目录"，
        /// 而分页之后目录是**一批批露出来**的 —— 那样每露一批就会把上一批没算完的丢掉。
        /// </summary>
        private void QueueDirSizeScan(IList<ShareFileItem> dirs)
        {
            if (dirs == null || dirs.Count == 0) return;
            foreach (var d in dirs)
            {
                if (d == null || string.IsNullOrEmpty(d.Fid)) continue;
                long cached;
                if (_dirSizeCache.TryGetValue(DirSizeKey(d.Fid), out cached))
                {
                    d.DirSize = cached;
                    continue;
                }
                if (!_dirSizeQueue.Contains(d)) _dirSizeQueue.Add(d);
            }
            if (_dirSizeRunning) return;
            var cts = _dirSizeCts;
            if (cts == null) return;
            _dirSizeRunning = true;
            // 不 await：让它在后台自己跑，界面立刻可用
            var ignored = DrainDirSizeQueueAsync(cts.Token);
        }

        private async Task DrainDirSizeQueueAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _dirSizeQueue.Count > 0)
                {
                    var d = _dirSizeQueue[0];
                    _dirSizeQueue.RemoveAt(0);
                    if (d == null || string.IsNullOrEmpty(d.Fid)) continue;

                    // 预算用完 → 标成"算不了"（显示「—」）。⚠️ 不能落 0：
                    // 几千个目录时预算必然用完，落 0 会让一大片文件夹看起来是空的。
                    if (_dirSizeBudget <= 0)
                    {
                        d.DirSizeFailed = true;
                        continue;
                    }
                    try
                    {
                        long total = await SumDirAsync(d.Fid, ct, () => _dirSizeBudget-- > 0);
                        if (ct.IsCancellationRequested) return;
                        _dirSizeCache[DirSizeKey(d.Fid)] = total;
                        d.DirSize = total;
                    }
                    catch (Exception ex)
                    {
                        if (ct.IsCancellationRequested) return;
                        d.DirSizeFailed = true;
                        Log("计算文件夹大小失败（" + d.Name + "）：" + ex.Message);
                    }
                }
            }
            finally { _dirSizeRunning = false; }
        }

        /// <summary>递归求和：文件夹 = 所有子孙文件字节数之和。</summary>
        /// <param name="takeBudget">每发一次请求调一次；返回 false 表示预算耗尽，停止下钻。</param>
        private async Task<long> SumDirAsync(string fid, CancellationToken ct, Func<bool> takeBudget)
        {
            if (string.IsNullOrEmpty(fid) || ct.IsCancellationRequested) return 0;

            long cached;
            if (_dirSizeCache.TryGetValue(DirSizeKey(fid), out cached)) return cached;
            if (!takeBudget()) return 0;

            var items = ShareMode
                ? await _client.ShareDetailAsync(_pwdId, _stoken, fid)
                : await _client.ListDirAsync(fid);

            long sum = 0;
            foreach (var it in items)
            {
                if (ct.IsCancellationRequested) return sum;
                if (it.IsDir)
                    sum += await SumDirAsync(it.Fid, ct, takeBudget);
                else
                    sum += it.Size;
            }
            _dirSizeCache[DirSizeKey(fid)] = sum;
            return sum;
        }

        /// <summary>
        /// 工具栏「刷新」按钮。
        /// <para>【2026-10-21】用户要求「r 相当于点击刷新功能」，所以把实际逻辑抽到
        /// <see cref="RefreshCurrentDirAsync"/>，按钮与快捷键共用同一份，
        /// 不会出现"按钮刷了、r 刷的是别的"这种分叉。</para>
        /// </summary>
        private async void OnRefresh(object sender, RoutedEventArgs e)
        {
            await RefreshCurrentDirAsync();
        }

        /// <summary>
        /// 刷新当前目录（绕过缓存重新请求）。按钮点击与 <c>r</c> 快捷键共用。
        /// </summary>
        private async Task RefreshCurrentDirAsync()
        {
            // 刷新要重新拉一次目录（网络）→ 按钮就地变成"刷新中…"
            long t0 = BusyClock();
            if (!BeginBusy(DriveRefreshBtn, "刷新中")) return;
            try
            {
                // 【2026-10-21】分享模式下，从缓存恢复后是没有 stoken 的
                // （那正是"零请求"的代价）。刷新是用户显式要求联网的动作，
                // 所以在这里补取一次，否则刷新必定失败（guest missing pwd_id or stoken）。
                if (ShareMode && string.IsNullOrEmpty(_stoken)
                    && !string.IsNullOrEmpty(_pwdId) && _client != null)
                {
                    try
                    {
                        var info = await _client.GetShareInfoAsync(_pwdId, _sharePasscode);
                        if (info != null && !string.IsNullOrEmpty(info.Stoken))
                            _stoken = info.Stoken;
                        else
                            Log("刷新前获取分享凭证失败（分享可能已过期或需要提取码）。");
                    }
                    catch (Exception ex)
                    {
                        Log("刷新前获取分享凭证失败：" + ex.Message);
                    }
                }

                // 「刷新」的语义就是要**绕过缓存**重新取一次
                _dirCache.Remove(DirCacheKey(_curFid));
                await LoadDirAsync(_curFid);
            }
            finally
            {
                await EndBusyAsync(DriveRefreshBtn, t0);
            }
        }

        private async void OnUp(object sender, RoutedEventArgs e)
        {
            if (_fidStack.Count == 0)
            {
                Log("已在根目录");
                return;
            }
            // ⚠️ 重入检查必须放在**改导航状态之前**：否则连点时会把栈 pop 两次却不加载
            long t0 = BusyClock();
            if (!BeginBusy(DriveUpBtn, "返回中")) return;
            try
            {
                _curFid = _fidStack.Pop();
                if (_pathList.Count > 0)
                    _pathList.RemoveAt(_pathList.Count - 1);
                UpdatePathText();
                await LoadDirAsync(_curFid);
            }
            finally
            {
                await EndBusyAsync(DriveUpBtn, t0);
            }
        }

        // 【2026-10-08 删除】原「双击文件夹进入子目录」的处理器（OnFileListDoubleClick）。
        // 单击进入之后它就成了重复入口，而且**会有害**：双击的第二下会把目录再进一层
        // （第一下已经换过目录，第二下落在刷新后的新行上）。现在只剩 EnterFolder 一条路。

        /// <summary>
        /// 右键按下时把光标下的行选中（若该行还没被选中），
        /// 这样「下载选中/导出直链」对准的是用户真正点的文件，
        /// 且保留已有的多选（右键点在已选中的行上时不破坏多选）。
        /// </summary>
        private void OnFileListRightClick(object sender, MouseButtonEventArgs e)
        {
            var el = e.OriginalSource as System.Windows.DependencyObject;
            while (el != null && !(el is System.Windows.Controls.ListBoxItem))
                el = System.Windows.Media.VisualTreeHelper.GetParent(el);
            var lbi = el as System.Windows.Controls.ListBoxItem;
            if (lbi != null && !lbi.IsSelected)
            {
                FileList.SelectedItems.Clear();
                lbi.IsSelected = true;
                // 多选模式下右键换选也要同步「已选 N 项」
                UpdateFileSelectCount();
            }

            // 「免转存下载」只在**分享**模式下有意义：网盘模式的文件本来就在自己网盘里，
            // 不存在"转存"这一步。菜单每次弹出时按当前模式决定它是否出现。
            if (NoSaveDownloadMenuItem != null)
                NoSaveDownloadMenuItem.Visibility = ShareMode
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 「免转存下载」：**不往自己网盘写中转文件**，直接从分享取链下载。
        /// </summary>
        /// <remarks>
        /// 【2026-10-03 新增】用户要求：「右键解析后的文件应该会有免转存下载选项」。
        /// 原来这个能力是**隐式**的 —— 登录状态下默认走「转存→取直链」（更稳定，但会
        /// 往你网盘写一份、占用空间、还会留下中转垃圾），只有未登录或网盘容量不足时
        /// 才自动降级为免转存。现在给它一个显式入口。
        /// </remarks>
        private async void OnDownloadSelectedNoSave(object sender, RoutedEventArgs e)
        {
            if (!ShareMode)
            {
                Log("「免转存下载」只在分享模式下可用（网盘文件无需转存）。");
                return;
            }
            long t0 = BusyClock();
            if (!BeginEnqueue(DownloadSelBtn)) return;
            try
            {
                await StartDownloadAsync(
                    FileList.SelectedItems.Cast<ShareFileItem>().ToList(), forceNoSave: true,
                    ct: _enqueueCts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("已终止入队（未加入任何下载任务）。");
            }
            finally
            {
                await EndEnqueueAsync(DownloadSelBtn, t0);
            }
        }

        private void OnFileListSelectAll(object sender, RoutedEventArgs e)
        {
            FileList.SelectAll();
        }

        // 【2026-10-03 删除】原「创建分享链接」入口（右键菜单项 + OnCreateShareSelected）
        // 已按用户要求移除（用户反馈「创建分享链接按钮没有用」）。
        // 同时 `QuarkClient.CreateShareAsync` 也一并删掉了 —— 它当时被"留着当 API 封装"，
        // 但 app 里**没有任何调用点**，属于纯死代码（用户要求"把不需要的死的代码找到并删除"）。
        // 若日后要做「创建分享链接」，照 `/agent/v1/share/create`（入参 fid_list/title/
        // url_type/expired_type）重新实现即可。

        /// <summary>
        /// 「删除网盘文件」：把选中的文件/文件夹从**夸克网盘**删除（进回收站后再清空）。
        ///
        /// 移植自 Python Flet 版 `_delete_cloud_selected`。
        /// 限制为「我的网盘」模式：分享模式里的 fid 属于分享者，删不得；
        /// 若用户是在分享模式下转存后又想删中转文件，请用设置里的自动清理或网盘端操作。
        /// </summary>
        private async void OnDeleteCloudSelected(object sender, RoutedEventArgs e)
        {
            if (_client == null) { Log("请先登录"); return; }
            if (ShareMode) { Log("「删除网盘文件」只在「我的网盘」模式下可用（分享里的文件属于分享者）"); return; }

            var selected = FileList.SelectedItems.Cast<ShareFileItem>().ToList();
            if (selected.Count == 0) { Log("没有选中任何文件"); return; }

            var ans = AppDialog.Show(this,
                string.Format("确定从网盘删除选中的 {0} 项吗？\n\n" +
                    "删除后会一并清空回收站，**无法恢复**。", selected.Count),
                "删除网盘文件", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (ans != MessageBoxResult.Yes) return;

            var fids = selected.Select(f => f.Fid).Where(f => !string.IsNullOrEmpty(f)).ToList();
            if (fids.Count == 0) return;

            SetBusy(true);
            try
            {
                Log("正在删除网盘文件 " + fids.Count + " 项…");
                await _client.DeleteAsync(fids);
                try { await _client.PurgeRecycleAsync(fids); } catch { }
                Log("已从网盘删除 " + fids.Count + " 项。");
                await LoadDirAsync(_curFid);   // 刷新列表
                // 删文件会改变已用空间 —— 顺手把账号卡的容量也刷一下（用户要求）
                await RefreshCapacityAsync();
            }
            catch (Exception ex)
            {
                Log("删除网盘文件失败: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ---------------- 目录路径条（面包屑，2026-10-08 改为可点击） ----------------
        //
        // 【为什么不直接用 _pathList 画】显示要的是"根 → 当前"这条链上**每一层叫什么、
        // 对应哪个 fid**，而导航状态是分开存的：名字在 _pathList、祖先的 fid 在
        // _fidStack（栈顶 = 上一层）。所以先换算成「层 → {名字, fid}」，再画成
        // 可点的小节 —— 见 BuildCrumbs。

        /// <summary>面包屑的一节。</summary>
        private sealed class Crumb
        {
            /// <summary>显示名（第一段固定是「根目录」）。</summary>
            public string Name;
            /// <summary>这一层对应的 fid；点它就是要跳到这个目录。</summary>
            public string Fid;
            /// <summary>是不是当前所在层（当前层不做成链接）。</summary>
            public bool IsCurrent;
        }

        /// <summary>
        /// 把当前导航状态换算成面包屑（根 → 当前）。
        ///
        /// 【fid 是怎么推出来的】进子目录时 `_fidStack.Push(上一层 fid)` +
        /// `_pathList.Add(目录名)`，于是：
        ///   · `_fidStack.ToList()` 是"栈顶在前" = [上一层, 上上层, …, 根]；
        ///   · **反转它** = [根, …, 上一层]，正好与 `_pathList` **逐项对齐**
        ///     （两者永远等长：进一层各加一项）。
        /// 所以第 0 段「根目录」用 chain[0]（没进过子目录时就是 "0"），
        /// `_pathList[i]` 这一层用 chain[i+1]，最后一段（当前层）用 `_curFid`。
        ///
        /// 【分享链接带 StartFid 的情况】解析时 ResetNav 清掉了栈，只有 _curFid 指向
        /// 子目录 → 面包屑就只剩「根目录」一节，而它的 fid 是进入点；
        /// 点不了（本来就是当前层），要出去用「上一级」。
        /// </summary>
        private List<Crumb> BuildCrumbs()
        {
            var chain = _fidStack.ToList();   // 栈顶在前
            chain.Reverse();                  // → 根在最前

            var list = new List<Crumb>();
            list.Add(new Crumb
            {
                Name = "根目录",
                Fid = chain.Count > 0 ? chain[0] : "0",
                IsCurrent = _pathList.Count == 0,
            });
            for (int i = 0; i < _pathList.Count; i++)
            {
                list.Add(new Crumb
                {
                    Name = _pathList[i],
                    Fid = (i + 1 < chain.Count) ? chain[i + 1] : _curFid,
                    IsCurrent = i == _pathList.Count - 1,
                });
            }
            return list;
        }

        /// <summary>
        /// 重建目录路径条。祖先层画成可点的小按钮（悬停高亮，见 BreadcrumbButton 样式），
        /// 当前层画成不可点的普通文字（主色 + 半粗）。
        /// </summary>
        private void UpdatePathText()
        {
            try
            {
                if (DrivePathBar == null) return;
                DrivePathBar.Children.Clear();

                var crumbs = BuildCrumbs();
                var sepStyle = (Style)FindResource("TertiaryText");
                for (int i = 0; i < crumbs.Count; i++)
                {
                    if (i > 0)
                    {
                        DrivePathBar.Children.Add(new TextBlock
                        {
                            Text = "/",
                            Style = sepStyle,
                            VerticalAlignment = VerticalAlignment.Center,
                        });
                    }
                    DrivePathBar.Children.Add(MakeCrumbElement(crumbs[i], i));
                }

                // 路径比可用宽度长时滚到最右 —— 「当前在哪一层」比左边那几节更该看得见。
                // ⚠️ 先 UpdateLayout：刚加完孩子时 ScrollViewer 的 Extent 还是旧的，
                //    直接滚会滚不动（等于什么都没做）。
                if (DrivePathScroll != null)
                {
                    DrivePathScroll.UpdateLayout();
                    DrivePathScroll.ScrollToRightEnd();
                }
            }
            catch { }
        }

        /// <summary>画面包屑的一节：祖先 = 可点按钮；当前层 = 普通文字。</summary>
        private UIElement MakeCrumbElement(Crumb crumb, int level)
        {
            if (crumb.IsCurrent)
            {
                return new TextBlock
                {
                    Text = crumb.Name,
                    Style = (Style)FindResource("BreadcrumbCurrent"),
                };
            }

            var btn = new Button
            {
                Content = crumb.Name,
                Style = (Style)FindResource("BreadcrumbButton"),
                // 存**层号**而不是 fid：跳转要重建 _fidStack / _pathList，那需要知道
                // "跳到第几层"，而层号在 OnBreadcrumbClick 里还能重新核对一遍
                // （重新 BuildCrumbs 比拿着可能过期的 fid 更稳）。
                Tag = level,
            };
            btn.Click += OnBreadcrumbClick;
            return btn;
        }

        /// <summary>
        /// 点面包屑的某一节 = 直接跳到那一层。
        /// </summary>
        private async void OnBreadcrumbClick(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null || !(btn.Tag is int))
                return;

            // 加载中不跳 —— 与 EnterFolder 同一个理由：这段窗口里旧行/旧面包屑还在，
            // 跳一次会和正在回来的那次结果打架（栈和面包屑会被推错）。
            if (_loadingDir) return;

            int level = (int)btn.Tag;
            if (level < 0) return;

            var crumbs = BuildCrumbs();
            if (level >= crumbs.Count) return;
            if (crumbs[level].IsCurrent) return;   // 点的就是当前层，不用动

            // 把导航状态**重建**成"停在目标层"：
            // 祖先链 = crumbs[0..level-1]，逐层 Push 进去（最后压入的正好是最直接的上一层）；
            // _pathList 则是去掉「根目录」那一段后的 crumbs[1..level]。
            _fidStack.Clear();
            for (int i = 0; i < level; i++)
                _fidStack.Push(crumbs[i].Fid);
            _pathList.Clear();
            for (int i = 1; i <= level; i++)
                _pathList.Add(crumbs[i].Name);
            _curFid = crumbs[level].Fid;

            UpdatePathText();
            Log("已跳到目录：" + string.Join(" / ", _pathList));
            await LoadDirAsync(_curFid);
        }

        // ---------------- 键盘快捷键（2026-10-08） ----------------
        //
        // 单键操作，不带 Ctrl 组合：
        //   · 切页：1 下载 / 2 任务 / 3 登录 / q 设置 / 6 关于本程序；
        //   · 文件列表：a 后退 / d 免转存下载 / x 下载选中 / g 获取直链。
        // 文案在设置页最下面的「快捷键」一节，两边要一起改。
        //
        // ⚠️ 单键方案唯一的坑是**抢输入**：这些字符在链接框、提取码、筛选框里就是普通字符，
        //    必须照常能敲进去。所以第一步先看焦点在不在输入控件上（IsTypingTarget）。
        // 🚨 只认**无修饰键**的按键：带 Ctrl/Alt/Win 的一律放过 —— Ctrl+C、Alt+Tab 这些
        //    是系统和编辑器的，截胡了会很难查。

        /// <summary>窗口级快捷键。挂在 Window 的 PreviewKeyDown 上（XAML 里）。</summary>
        private void OnShortcutKeyDown(object sender, KeyEventArgs e)
        {
            // ⓪-1 Esc 退出任何输入框（2026-10-21 用户要求：
            //      「添加 esc 退出所有输入框功能，不管是链接输入还是搜索」）。
            //
            // ⚠️ 必须排在 IsTypingTarget 那个"在打字就放行"的判断**之前** ——
            //    否则焦点在输入框里时 Esc 会被直接放过，这个功能就等于没做。
            //    这也正是原来只有搜索框能 Esc 退出的原因：它在 OnFileFilterKeyDown
            //    里单独处理了一份；现在统一收到这里，所有输入框行为一致。
            //
            // 行为：把焦点交还给当前页的主容器（而不是某个具体控件），
            // 这样后续单键快捷键能正常触发；输入框里的内容**不清空**
            // （用户只是"退出输入"，不是"放弃已输入的内容"）。
            if (e.Key == Key.Escape && IsTypingTarget(Keyboard.FocusedElement))
            {
                e.Handled = true;
                ReleaseTypingFocus();
                return;
            }

            // 光标在输入框里 → 这些字符是文字，不是快捷键
            if (IsTypingTarget(Keyboard.FocusedElement)) return;

            // ⓪ 多选模式开关（2026-10-21 新增，2026-10-21 晚改键）：
            //    **s 键切换进入 / 退出**，Esc 也能退出（任何页面都能退，
            //    避免切页后卡在模式里不自知）。
            //
            // 【为什么不用 Alt】用户反馈「现在 alt 键会导致误触发，比如 Alt+Tab 切换窗口，
            // 然后切换回去会自动进入多选」。
            // 根因：Alt 是**系统级修饰键**，Alt+Tab / Alt+F4 / Alt+空格 都会先产生一次
            // "Alt 被按下"的 KeyDown —— 而我们当时只要看到 KeyDown 就切换模式，
            // 于是每次切窗口都会把多选模式翻一次（切走翻成开、切回来又翻一次，看着像"自动进入"）。
            // 而且用"抬起时判定"也治不好：切换窗口期间键盘焦点不在我们窗口上，
            // KeyUp 可能收不到，状态就更容易错乱。
            // 结论：修饰键不适合当这种"粘滞开关"，换成一个普通字母键最干净。
            // 字母键走的是下面的普通分支（不带修饰键），与所有系统快捷键零冲突。
            if (e.Key == Key.S && Tabs.SelectedIndex == 0)
            {
                e.Handled = true;
                if (_fileSelectMode) ExitFileSelectMode();
                else EnterFileSelectMode();
                return;
            }

            // ⓪.1 Esc 隐藏「已在下载 / 已下载过」提示条（2026-10-21 用户要求）。
            //      顺序讲究：先收提示，再轮到下面的"退出多选模式" ——
            //      提示条是刚出现的临时信息，用户按 Esc 的第一意图几乎总是"关掉它"。
            if (e.Key == Key.Escape && DuplicateHintBar != null
                && DuplicateHintBar.Visibility == Visibility.Visible)
            {
                e.Handled = true;
                HideDuplicateHint();
                return;
            }

            // ⓪.2 r = 刷新当前目录（等价于点工具栏那颗「刷新」，2026-10-21 用户要求）。
            //      只在下载页有意义：刷新的是文件列表。
            //      复用 OnRefresh 意味着同样会**绕过缓存**、走真实请求 ——
            //      这样从本地缓存恢复后，r 就是"去看有没有更新"的快捷方式。
            if (e.Key == Key.R && Tabs.SelectedIndex == 0)
            {
                e.Handled = true;
                RefreshCurrentDirAsync();
                return;
            }

            // Esc 退出多选模式
            if (_fileSelectMode && e.Key == Key.Escape)
            {
                e.Handled = true;
                ExitFileSelectMode();
                return;
            }

            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
                return;

            // ① h：开关「解析历史」面板（任何页面都可用）。
            //    面板开着时它当"关闭"用，Esc 同样关；并且**其它快捷键一律不响应** ——
            //    面板是模态观感，这期间把界面切走会让人莫名其妙。
            if (HistoryOpen)
            {
                if (e.Key == Key.H || e.Key == Key.Escape)
                {
                    e.Handled = true;
                    HideHistoryPanel();
                }
                return;
            }
            if (e.Key == Key.H)
            {
                e.Handled = true;
                ExitTaskSelectMode();   // 面板是模态观感，别让底下的选择模式还挂着
                ShowHistoryPanel();
                return;
            }

            // ①.4 F：全屏 / 还原（= 最大化切换）。任何页面都可用。
            if (e.Key == Key.F)
            {
                e.Handled = true;
                ToggleMaximizeRestore();
                return;
            }

            // ①.5 任务页的「删除选择模式」：z 进入 / Enter 确认 / Esc 退出。
            //     用户要求「选好后 再按 Enter，弹出是仅删除记录还是一起删除本地文件」。
            if (_taskSelectMode)
            {
                if (e.Key == Key.Z || e.Key == Key.Escape)
                {
                    e.Handled = true;
                    ExitTaskSelectMode();
                    return;
                }
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    ConfirmTaskDeleteSelection();
                    return;
                }
            }
            if (e.Key == Key.Z && Tabs.SelectedIndex == 1)
            {
                e.Handled = true;
                EnterTaskSelectMode();
                return;
            }

            // ② 切换页面：任何页面都能用。
            //    1/2/3 = 下载 / 任务 / 登录（设置那页给了 q，不让数字键去占位），
            //    6 = 关于本程序。小键盘上的同名键（NumPad）一并认，免得用户按了没反应。
            switch (e.Key)
            {
                case Key.D1:
                case Key.NumPad1:
                    e.Handled = true;
                    Tabs.SelectedIndex = 0;   // 侧栏顺序：0 下载 / 1 任务 / 2 设置 / 3 登录
                    return;
                case Key.D2:
                case Key.NumPad2:
                    e.Handled = true;
                    Tabs.SelectedIndex = 1;
                    return;
                case Key.D3:
                case Key.NumPad3:
                    e.Handled = true;
                    Tabs.SelectedIndex = 3;
                    return;
                case Key.Q:
                    e.Handled = true;
                    Tabs.SelectedIndex = 2;
                    return;
                case Key.D6:
                case Key.NumPad6:
                    e.Handled = true;
                    OnAboutClick(null, null);   // 与侧栏品牌区那个「?」同一条路径
                    return;
            }

            // ② 以下都作用于「下载」页的文件列表。在任务页/设置页按 x 却把文件列表里
            //    的选中项下掉，既看不见也想不到，所以先判页面。
            if (Tabs.SelectedIndex != 0) return;

            switch (e.Key)
            {
                case Key.A:   // 后退（上一级）—— 分享链接 / 我的网盘 是同一套实现
                    e.Handled = true;
                    OnUp(null, null);
                    break;
                case Key.D:   // 免转存下载
                    e.Handled = true;
                    OnDownloadSelectedNoSave(null, null);
                    break;
                case Key.X:   // 下载选中
                    e.Handled = true;
                    OnDownloadSelected(null, null);
                    break;
                case Key.G:   // 获取直链…
                    e.Handled = true;
                    OnFetchLinks(null, null);
                    break;
                case Key.I:   // 聚焦文件搜索框（Esc 退出焦点）
                    e.Handled = true;
                    FocusFileFilter();
                    break;
            }
        }

        /// <summary>把光标送进当前目录的搜索框，并全选已有内容（按 i 时用）。</summary>
        private void FocusFileFilter()
        {
            try
            {
                FileFilterBox.Focus();
                FileFilterBox.SelectAll();
            }
            catch { }
        }

        /// <summary>F：全屏 / 还原（= 最大化切换）。与标题栏那颗「最大化或还原」一致。</summary>
        private void ToggleMaximizeRestore()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            Log(WindowState == WindowState.Maximized ? "已进入全屏（最大化）" : "已还原窗口");
        }

        /// <summary>
        /// 把键盘焦点从当前输入框交还给所在页，用于「Esc 退出输入」（2026-10-21）。
        ///
        /// <para>【为什么不 Focus 到某个具体控件】各页结构不同，硬指定容易拿到不该有焦点的元素
        /// （比如只读的按钮反而被高亮）。这里按"当前在哪一页"选一个合理的归宿：
        /// 下载页 → 文件列表（这样 a/d/s/x/g 等单键立刻可用）；
        /// 其余页 → 让页容器自己接管。</para>
        ///
        /// <para>【不影响内容】只移焦点，不改文本。用户按 Esc 通常是想"先去点别的东西"，
        /// 把已输入的内容清掉会很恼人。</para>
        /// </summary>
        private void ReleaseTypingFocus()
        {
            try
            {
                // 记下是哪种输入框，日志里能说清楚（排查"按了没反应"时有用）
                string kind = "输入框";
                var focused = Keyboard.FocusedElement;
                var fe = focused as FrameworkElement;
                if (focused is PasswordBox) kind = "密码框";
                else if (focused is TextBox) kind = "文本框";
                else if (focused is ComboBox) kind = "下拉框";
                string name = fe != null && !string.IsNullOrEmpty(fe.Name) ? "（" + fe.Name + "）" : "";

                // 【关键】焦点必须从"正在打字的那个控件"**主动移走**，
                // 光调别的控件 Focus() 不一定有用 —— 实测点进链接框后
                // 调 FileList.Focus() 无效：焦点仍留在 LinkBox，于是接着按 s/a/d
                // 还是被当成打字，单键快捷键全部失效。
                //
                // 做法（顺序有讲究）：
                // ① 先把当前输入框设为不可聚焦（Keyboard.ClearFocus 在 WPF 里没有直接对应，
                //    最可靠的替代是把焦点明确交给一个"能接收焦点"的容器）；
                // ② 下载页优先给文件列表（这样 a/d/s/x/g 立刻可用），
                //    其它页给页容器；
                // ③ 兜底：把焦点交给窗口本身（Window 是 IInputElement，一定能接住）。
                bool moved = false;

                if (Tabs.SelectedIndex == 0 && FileList != null)
                {
                    try
                    {
                        FileList.Focus();
                        moved = ReferenceEquals(Keyboard.FocusedElement, FileList)
                             || !IsTypingTarget(Keyboard.FocusedElement);
                    }
                    catch { }
                }

                if (!moved)
                {
                    // 用 MoveFocus 把焦点从当前元素顺着逻辑树挪走，
                    // 比"指名某个控件"更通用（设置页/登录页各有各的控件树）。
                    try
                    {
                        var from = focused as UIElement;
                        if (from != null)
                        {
                            from.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                            moved = !IsTypingTarget(Keyboard.FocusedElement);
                        }
                    }
                    catch { }
                }

                if (!moved)
                {
                    // 最后兜底：交给窗口自己。窗口持有焦点时 IsTypingTarget 为 false，
                    // 单键快捷键一定会重新生效。
                    try
                    {
                        // Window 默认 Focusable=false，直接 Focus() 会被忽略 ——
                        // 临时打开它、抢到焦点后再关掉，这样键盘输入不会落到窗口本身。
                        bool old = Focusable;
                        Focusable = true;
                        Focus();
                        moved = !IsTypingTarget(Keyboard.FocusedElement);
                        Focusable = old;
                    }
                    catch { }
                }

                Log("已退出" + kind + name + "的输入状态（内容保留）" + (moved ? "" : "（提示：焦点可能未完全移开）"));
            }
            catch
            {
                // 拿不到焦点目标也不能让程序出错：静默即可，功能降级为"这次没退出"
            }
        }

        /// <summary>
        /// 焦点是不是落在"要打字"的控件上（文本框 / 密码框 / 可编辑下拉框）。
        /// 是的话快捷键必须让路 —— 否则链接框里连字母都敲不进去。
        /// </summary>
        private static bool IsTypingTarget(IInputElement focused)
        {
            if (focused == null) return false;
            if (focused is TextBoxBase) return true;     // TextBox / RichTextBox
            if (focused is PasswordBox) return true;
            var cb = focused as ComboBox;
            return cb != null && cb.IsEditable;          // 只读下拉框不算（在那儿按字母本来没反应）
        }

        // ---------------- 下载 ----------------

        private async void OnDownloadSelected(object sender, RoutedEventArgs e)
        {
            // 下载前要先递归展开目录 + 取直链（网络）→ 按钮就地变成"入队中（点此终止）"
            long t0 = BusyClock();
            if (!BeginEnqueue(DownloadSelBtn)) return;
            try
            {
                await StartDownloadAsync(FileList.SelectedItems.Cast<ShareFileItem>().ToList(),
                    ct: _enqueueCts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("已终止入队（未加入任何下载任务）。");
            }
            finally
            {
                await EndEnqueueAsync(DownloadSelBtn, t0);
            }
        }

        private async void OnDownloadAll(object sender, RoutedEventArgs e)
        {
            long t0 = BusyClock();
            if (!BeginEnqueue(DownloadAllBtn)) return;
            try
            {
                await StartDownloadAsync(_files.ToList(), ct: _enqueueCts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("已终止入队（未加入任何下载任务）。");
            }
            finally
            {
                await EndEnqueueAsync(DownloadAllBtn, t0);
            }
        }

        // ---------------- 获取直链（导出对话框） ----------------

        /// <summary>
        /// 「获取直链」—— 取到链接后弹出对话框，**上方**给出三种导出格式按钮：
        /// aria2c 命令 / curl 命令 / 纯直链；点任一个即按该格式导出（写文件 + 剪贴板）。
        ///
        /// <para>【2026-10-02 改版】</para>
        /// <list type="bullet">
        /// <item>原来这个窗口把每条「文件名 Tab 链接」平铺在一个大列表里，用户反馈
        ///   「右边显示了好多直链太乱了不要显示」——现在窗口**不再显示链接文本**，
        ///   只列出将导出的文件名（一行一个），链接本身通过导出格式按钮落到文件/剪贴板。</item>
        /// <item>原来下载页另有一个「导出直链」按钮，功能与这里重复；用户要求删掉它，
        ///   把这个能力合并进本窗口的「上方三个按钮」。</item>
        /// <item>列表右键菜单里的「导出直链」项也一并删除，避免两条入口语义打架。</item>
        /// </list>
        ///
        /// <para>注意：取链只做一次，三个格式按钮共用同一份结果 —— 点哪个都能立刻导出，
        /// 不需要重新取链（直链带时效签名，重复取链反而变慢）。</para>
        /// </summary>
        private async void OnFetchLinks(object sender, RoutedEventArgs e)
        {
            if (_client == null)
            {
                Log("请先登录或解析分享");
                return;
            }
            var selected = FileList.SelectedItems.Cast<ShareFileItem>().ToList();
            if (selected.Count == 0)
            {
                Log("没有选中任何文件");
                return;
            }

            // 取直链是"每个文件一次网络请求"，可能要好几秒；而且取完还要弹窗 ——
            // 期间界面若毫无变化，用户会以为没点上，然后连点导致重复取链。
            // 所以按钮就地变成「获取中」+ 转圈，并在弹窗出现前一直保持。
            long t0 = BusyClock();
            if (!BeginBusy(FetchLinksBtn, "获取中")) return;
            SetBusy(true);
            try
            {
                var files = await CollectFilesAsync(selected, "", 0);
                if (files.Count == 0) { Log("没有可获取链接的文件"); return; }
                if (files.Count > MaxFiles)
                {
                    Log(string.Format("文件数量过多（本次 {0} 个，单次上限 {1} 个），已中止。",
                        files.Count, MaxFiles));
                    return;
                }

                Log("共展开 " + files.Count + " 个文件，正在取直链…");
                var items = await GetDownloadItemsAsync(files);
                if (items == null || items.Count == 0) { Log("未取到任何直链"); return; }

                var withUrl = items.Where(it => !string.IsNullOrEmpty(it.Url)).ToList();
                int noUrl = items.Count - withUrl.Count;
                if (withUrl.Count == 0)
                {
                    Log("全部条目都没有直链（分享可能已失效或限制游客下载）。");
                    return;
                }

                Log(string.Format("取到 {0} 条直链（{1} 条无链接）。", withUrl.Count, noUrl));

                // 先收掉加载态再弹窗，否则转圈会和对话框同时存在
                await EndBusyAsync(FetchLinksBtn, t0);
                t0 = 0;
                ShowLinksDialog(withUrl, noUrl);
            }
            catch (Exception ex)
            {
                Log("获取直链失败：" + ex.Message);
            }
            finally
            {
                SetBusy(false);
                if (t0 != 0) await EndBusyAsync(FetchLinksBtn, t0);
            }
        }

        /// <summary>
        /// 直链导出对话框：**上方**文件管理器式页签（三种直链种类，只负责"选"），
        /// **中间**待导出文件清单（只有文件名，不显示链接文本），
        /// **下方**「导出直链 / 下载文件 / 关闭」。
        ///
        /// <para>布局要点（历轮用户反馈累积）：
        /// ① 种类选择器在**最上面**（用户原话「这三个按钮放在上方」）；
        /// ② 不列出 URL（用户原话「显示了好多直链太乱了不要显示」）；
        /// ③ 点击页签**只是切换种类，不导出任何文件** —— 用户原话
        ///    「这三个按钮点击之后不会导出任何文件只有选择作用，真正导出指链是靠下方的按钮」，
        ///    所以这里把「导出」动作**只**挂在下方「导出直链」按钮上（历轮曾经是点页签即导出，已纠正）；
        /// ④ 形态是「顶部文件夹标签」—— 选中页签白底、上沿圆角、底边与列表区连成一体。</para>
        /// </summary>
        private void ShowLinksDialog(List<DownloadItem> items, int noUrlCount)
        {
            var win = new Window
            {
                Owner = this,
                Title = "获取直链",
            };
            // 统一弹窗外观（背景 / 字体 / 尺寸习惯），不再裸用系统默认样式
            DialogChrome.StyleWindow(win, 700, 520);

            var root = new Grid { Margin = new Thickness(22, 18, 22, 18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 说明
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 页签 + 文件清单
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 底部按钮

            var title = DialogChrome.PageTitle(string.Format("获取直链（{0} 个文件）", items.Count));
            Grid.SetRow(title, 0);
            root.Children.Add(title);

            // ===== 说明：压成一行，说清"页签只选种类、导出靠下方按钮" =====
            // 这一行同时兼作**切换页签时的加载指示区**：切换期间整行原地换成
            // 「转圈 + 正在切换到 xxx…」，切完再换回说明文字（两者尺寸接近，不会跳）。
            string hint = "先在上方选一种直链种类，再点下方「导出直链」——结果会复制到剪贴板，并另存为桌面上的 txt 文件。";
            if (noUrlCount > 0)
                hint += string.Format("（另有 {0} 条未取到链接，已略过）", noUrlCount);
            var head = DialogChrome.Hint(hint, 0, 12);

            var hintBusyText = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var hintBusy = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 0, 12),
            };
            var hintSpin = MakeSpinner();
            hintSpin.Stroke = (TryFindResource("PrimaryBrush") as Brush) ?? Brushes.SteelBlue;
            hintBusy.Children.Add(hintSpin);
            hintBusy.Children.Add(hintBusyText);

            var hintHost = new Grid();
            hintHost.Children.Add(head);
            hintHost.Children.Add(hintBusy);
            Grid.SetRow(hintHost, 1);
            root.Children.Add(hintHost);

            // ===== 页签 + 文件清单：套在同一个连体外框里 =====
            // 【2026-10-02 用户反馈】「将顶上的这三个按钮变成下面文件夹的三种标签页」
            //   并选定形态为「顶部文件夹标签」。
            //   命名同时纠正：上一轮误把首位按钮叫「标注版」，
            //   用户要求「改回原本的名字纯直链」→ 显示名统一为 纯直链 / curl 命令 / aria2c 命令。
            var host = new Grid();
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 页签条
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 列表

            // 页签条：Grid 里最后加的一层在最上面 —— 先放分隔线，页签压在上面，
            // 选中页签再用 Margin 下移 1px 吃掉分隔线，形成"与下方连体"的文件夹页签效果。
            var strip = new Grid { Margin = new Thickness(14, 0, 14, 0) };
            strip.Children.Add(DialogChrome.TabStrip());

            var tabBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            strip.Children.Add(tabBar);
            Grid.SetRow(strip, 0);
            host.Children.Add(strip);

            var tabPlain = DialogChrome.FileTab("纯直链", LinkExporter.Format.Labeled);
            var tabCurl = DialogChrome.FileTab("curl 命令", LinkExporter.Format.Curl);
            var tabAria = DialogChrome.FileTab("aria2c 命令", LinkExporter.Format.Aria2);
            var tabMotrix = DialogChrome.FileTab("Motrix 界面", LinkExporter.Format.MotrixGui);
            // 【用户要求】「后两者自带 UA 与 Cookie，把它的颜色改红」。
            // curl / aria2c / Motrix 界面 三种格式会把**登录凭证（UA + Cookie）**一起写进导出文件，
            // 属于敏感内容 → 用警示红标出来，和下载页那行提示保持同一套语义色。
            // ⚠️ 这里用的是**本地值**：WPF 里本地值优先级高于样式触发器，
            //    所以 FileTabItem 那个「选中变主色」的触发器不会把红色覆盖掉 ——
            //    正是我们要的（这几个页签任何状态下都是红的）。
            var warnBrush = TryFindResource("DangerBrush") as Brush;
            if (warnBrush != null)
            {
                tabCurl.Foreground = warnBrush;
                tabAria.Foreground = warnBrush;
                tabMotrix.Foreground = warnBrush;
            }
            tabBar.Children.Add(tabPlain);
            tabBar.Children.Add(tabCurl);
            tabBar.Children.Add(tabAria);
            tabBar.Children.Add(tabMotrix);

            // 互斥 + 默认选中「纯直链」（原首位按钮的位置）
            LinkExporter.Format chosen = LinkExporter.Format.Labeled;
            Action syncTabs = () =>
            {
                tabPlain.IsChecked = chosen == LinkExporter.Format.Labeled;
                tabCurl.IsChecked = chosen == LinkExporter.Format.Curl;
                tabAria.IsChecked = chosen == LinkExporter.Format.Aria2;
                tabMotrix.IsChecked = chosen == LinkExporter.Format.MotrixGui;
            };

            // ===== 切换种类时的加载指示 =====
            // 【用户要求】「切换导出直链种类的那一个要加」加载过程。
            // ⚠️ 说实话：切种类本身**是瞬时的**（只改 chosen 这一个变量，零耗时），
            //    所以这里的加载态不是"真在加载"，而是**给用户一个"点到了、切过去了"的确认** ——
            //    否则点完页签除了高亮变一下，界面没有任何别的动静，容易让人怀疑没生效。
            // 因此刻意停留 MinBusyMs 再收掉，避免一闪而过。
            // 连续快速点不同页签时不阻塞（每次立即生效），只让指示器延长到最后一次点击之后。
            int switchSeq = 0;
            Func<LinkExporter.Format, string, Task> switchTo = async (fmt, name) =>
            {
                chosen = fmt;
                syncTabs();

                int my = ++switchSeq;
                hintBusyText.Text = "正在切换到 " + name + "…";
                head.Visibility = Visibility.Collapsed;
                hintBusy.Visibility = Visibility.Visible;

                await Task.Delay(MinBusyMs);

                if (my != switchSeq) return;   // 期间又切过 → 让最后一次来收尾
                hintBusy.Visibility = Visibility.Collapsed;
                head.Visibility = Visibility.Visible;
            };
            tabPlain.Click += (s, e) => { _ = switchTo(LinkExporter.Format.Labeled, "纯直链"); };
            tabCurl.Click += (s, e) => { _ = switchTo(LinkExporter.Format.Curl, "curl 命令"); };
            tabAria.Click += (s, e) => { _ = switchTo(LinkExporter.Format.Aria2, "aria2c 命令"); };
            tabMotrix.Click += (s, e) => { _ = switchTo(LinkExporter.Format.MotrixGui, "Motrix 界面"); };
            syncTabs();

            var list = DialogChrome.StyledList();
            list.FontSize = 12.5;
            list.SelectionMode = SelectionMode.Extended;
            list.Padding = new Thickness(4, 5, 4, 5);
            var itemStyle = TryFindResource("DialogListItem") as Style;
            if (itemStyle != null) list.ItemContainerStyle = itemStyle;
            // 列表项分两级显示（符合整体 UI 的层次语言）：
            //   路径（含末尾的 \）= 三级文字色，弱化
            //   文件名           = 一级文字色，主体
            // 一整行同色同字重的「路径 \ 文件名」太朴素，是用户说"不符合 UI"的那处。
            var pathBrush = TryFindResource("TextTertiaryBrush") as Brush;
            var nameBrush = TryFindResource("TextPrimaryBrush") as Brush;
            foreach (var it in items)
            {
                var row = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                if (!string.IsNullOrEmpty(it.RelDir) && pathBrush != null)
                {
                    row.Inlines.Add(new Run(it.RelDir + " \\ ") { Foreground = pathBrush });
                }
                var nm = new Run(it.Name);
                if (nameBrush != null) nm.Foreground = nameBrush;
                row.Inlines.Add(nm);
                list.Items.Add(row);
            }
            Grid.SetRow(list, 1);
            host.Children.Add(list);

            var hostCard = DialogChrome.TabHost(host);
            Grid.SetRow(hostCard, 2);
            root.Children.Add(hostCard);

            // ===== 底部按钮：等宽 =====
            // 【2026-10-02】「复制全部连接按钮改成导出直链」（按页签选中的种类导出）
            //              「把下载这些文件改成下载文件」
            var copyAll = DialogChrome.PrimaryButton("导出直链");
            var dlBtn = DialogChrome.SecondaryButton("下载文件");
            var close = DialogChrome.SecondaryButton("关闭");
            copyAll.MinWidth = 126;
            dlBtn.MinWidth = 126;
            close.MinWidth = 90;
            var btns = DialogChrome.ButtonRow(copyAll, dlBtn, close);
            // 与列表卡留出明显的间距，避免"按钮压在列表上"（用户反馈「别和这些按钮重叠」）
            btns.Margin = new Thickness(0, 16, 0, 0);
            Grid.SetRow(btns, 3);
            root.Children.Add(btns);

            win.Content = root;

            // 导出动作只在这里发生：读页签当前选中的种类。
            copyAll.Click += (s, e) => ExportLinksFromDialog(win, items, chosen);
            dlBtn.Click += (s, e) =>
            {
                win.Close();
                // 直接复用已取到的链接入队（避免二次取链）
                StartDownloadFromItems(items);
            };
            close.Click += (s, e) => win.Close();

            win.ShowDialog();
        }

        /// <summary>
        /// 在「获取直链」对话框里按选定格式导出（**只写桌面文件**）。
        /// 与旧的「导出直链」按钮共用同一套 LinkExporter 与落盘逻辑。
        /// ⚠️ 用户明确要求：**不要再复制到剪贴板**（原话「无论是导出直链还是导出
        /// cookie都不要复制到粘贴板上」）—— 剪贴板是用户的私有区域，工具不该悄悄占用。
        /// </summary>
        private void ExportLinksFromDialog(Window owner, List<DownloadItem> items,
            LinkExporter.Format format)
        {
            if (_client == null) { Log("请先登录或解析分享"); return; }

            var list = new List<LinkExporter.Item>();
 int skipped = 0;
 var pathSkippedNames = new List<string>();   // 路径级去重跳过的文件名（供 UI 提示）
            foreach (var it in items)
            {
                if (string.IsNullOrEmpty(it.Url)) { skipped++; continue; }
                string rel = string.IsNullOrEmpty(it.RelDir)
                    ? it.Name
                    : Path.Combine(it.RelDir, it.Name);
                list.Add(new LinkExporter.Item { Url = it.Url, OutputPath = rel });
            }

            string text = LinkExporter.Build(list, QuarkConstants.DlUa,
                _client.CookieStr, QuarkConstants.Referer, format,
                // 【仅 Motrix 格式用】必须是绝对路径：Motrix 是独立进程，
                // 它把 "." 解析成【它自己的工作目录】（实测落到了 D:\motrix\ 安装目录），
                // 不是用户执行命令时所在的目录。curl/aria2c 没这问题（用户自己在 shell 里跑）。
                absoluteSaveDir: _settings.OutDir);
            if (string.IsNullOrEmpty(text))
            {
                Log("没有可导出的直链");
                return;
            }

            string outFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "GeZi-直链导出-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            try
            {
                File.WriteAllText(outFile, text, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log("写导出文件失败：" + ex.Message);
                outFile = null;
            }

            Log(string.Format("已导出 {0} 条直链（格式：{1}），跳过 {2} 条无链接项。",
                list.Count, format, skipped));
            if (outFile != null)
                Log("导出文件：" + outFile);
            Log("注意：直链有时效，请尽快使用。");

            AppDialog.Show(owner,
                string.Format("已导出 {0} 条直链（{1}）。\n\n文件：\n{2}\n\n" +
                    "注意：直链带签名有时效，请尽快交给下载器使用。",
                    list.Count, FormatName(format),
                    outFile ?? "（写入失败）"),
                "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>导出种类的显示名（日志与提示里用）。</summary>
        private static string FormatName(LinkExporter.Format format)
        {
            switch (format)
            {
                case LinkExporter.Format.Aria2: return "aria2c 命令";
                case LinkExporter.Format.Curl: return "curl 命令";
                case LinkExporter.Format.MotrixGui: return "Motrix 界面";
                case LinkExporter.Format.Labeled: return "纯直链";
                default: return "纯直链";
            }
        }

        // ---------------- 连接数按文件大小比例分配 ----------------
        //
        // 【背景】Python Flet 版有「智能调度」：实际线程数 = min(下载线程数, 总连接预算 / 本批任务数)
        // —— 也就是**多个文件平分**总预算。C# 版当初删掉「全局连接预算」设置项时，
        // 连"多文件共享总预算"这件事一起没了，变成每个任务都开满自己的线程数
        // （用户把单文件并发拉到 512、同时跑 3 个 = 1536 条连接，而夸克是按账号限总连接数的）。
        //
        // 【用户要求】「不要平均分配了，改成比例分配，按文件大小，文件越大分配越多，
        // 文件越小分配越少，但有最低下限，不然别让小文件根本下不了了」。
        //
        // 具体算法在 GeZi.Core.Download.ThreadAllocator（纯函数，可单独测试）。

        /// <summary>总连接预算。见 <see cref="GeZi.Core.Download.ThreadAllocator.DefaultBudget"/>。</summary>
        private const int DownloadConnBudget = GeZi.Core.Download.ThreadAllocator.DefaultBudget;

        /// <summary>单个文件的最低连接数。见 <see cref="GeZi.Core.Download.ThreadAllocator.MinPerFile"/>。</summary>
        private const int MinThreadsPerFile = GeZi.Core.Download.ThreadAllocator.MinPerFile;

        /// <summary>
        /// 按**文件大小比例**把总连接预算分给这一批任务，返回与入参等长的线程数数组。
        /// </summary>
        private int[] AllocateThreads(IList<long> sizes)
        {
            return GeZi.Core.Download.ThreadAllocator.Allocate(
                sizes,
                Math.Min(_settings.Threads, GeZi.Core.Download.SegmentedDownloader.MaxRealConcurrency),
                DownloadConnBudget,
                MinThreadsPerFile,
                // 同时最多跑几个 —— 预算约束的是"同时并发"的量级，不是整批的账面总和
                Math.Max(1, _settings.ConcurrentTasks));
        }

        /// <summary>把分配结果写进日志（只在"不是所有任务都一样"时打，避免刷屏）。</summary>
        private void LogAllocation(int[] threads)
        {
            if (threads == null || threads.Length <= 1) return;
            bool uniform = true;
            for (int i = 1; i < threads.Length; i++)
                if (threads[i] != threads[0]) { uniform = false; break; }
            if (uniform)
            {
                Log(string.Format("本批 {0} 个文件，每个 {1} 路并发。",
                    threads.Length, threads[0]));
                return;
            }
            var sb = new StringBuilder();
            for (int i = 0; i < threads.Length && i < 12; i++)
            {
                if (i > 0) sb.Append('、');
                sb.Append(threads[i]);
            }
            if (threads.Length > 12) sb.Append("…");
            Log(string.Format(
                "已按文件大小分配连接数（总预算 {0}）：{1} —— 大文件多分、小文件少分，每文件不低于 {2} 路。",
                DownloadConnBudget, sb, MinThreadsPerFile));
        }

        // ---------------- 重复下载检测（2026-10-21 新增） ----------------
        //
        // 【问题】用户问「不小心误选了同样文件，是否会重复下载」。
        // 当时有两层保护，但都不够：
        //   ① 核心层 DownloadScheduler._activeDests 同路径互斥 —— 但它是在**开始下载那一刻**
        //      才判定，冲突时把第二个任务标成 JobState.Failed（"已跳过：同一文件正在下载中"）。
        //      结果：任务页多出一行红色失败任务，看着像出错了，还挺碍眼。
        //   ② PathUtil.UniquePath 只查**磁盘**上是否已存在同名文件。
        //      "文件还没落盘（或只落了部分）"时它查不到，于是同一个文件可以被排进队列两次。
        // 【做法】在**入队前**按目标路径查一遍任务列表，命中就跳过并记日志，
        //        从源头上不产生第二条任务。核心层那道互斥继续留着当最后一道保险。

        // ---------------- 重复下载提示（2026-10-21 新增） ----------------
        //
        // 【需求】用户要求：「当检查重复下载时，应该要添加提示信息，提示用户已经下载过，
        //        现在只是不入队」。
        // 【为什么不能只写日志】去重原来只 Log 一行，而日志在**任务页**、用户点的是
        //        **下载页**的按钮 —— 点了没反应、任务数也没变，很容易以为按钮坏了。
        //        所以在按钮下方给一条看得见的说明，把"跳过"变成明确结果。

        /// <summary>
        /// 「已在下载 / 已下载过」提示条的自动隐藏定时器（2026-10-21 用户要求
        /// 「最好过段时间自动隐藏，不要一直展示」）。
        /// </summary>
        private DispatcherTimer _dupHintTimer;

        /// <summary>提示条自动隐藏延时（毫秒）。</summary>
        /// <remarks>
        /// 取 8 秒的理由：这条提示是要被**读完**的（含文件名），比纯状态提示需要更久；
        /// 但它只是个通知，不该长期占着按钮上方的空间。
        /// 鼠标悬停在提示条上时会暂停计时（见 OnDuplicateHintMouseEnter/Leave），
        /// 用户正在读或准备点「打开下载目录」时不会被抢走。
        /// </remarks>
        private const int DupHintAutoHideMs = 8000;

        /// <summary>启动（或重启）提示条的自动隐藏计时。</summary>
        private void RestartDuplicateHintTimer()
        {
            try
            {
                if (_dupHintTimer == null)
                {
                    _dupHintTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(DupHintAutoHideMs),
                    };
                    _dupHintTimer.Tick += (s, e) =>
                    {
                        try
                        {
                            _dupHintTimer.Stop();
                            HideDuplicateHint();
                        }
                        catch { }
                    };
                }
                _dupHintTimer.Stop();
                _dupHintTimer.Start();
            }
            catch { }
        }

        /// <summary>鼠标移到提示条上 → 暂停自动隐藏（用户正在看）。</summary>
        private void OnDuplicateHintMouseEnter(object sender, MouseEventArgs e)
        {
            try { _dupHintTimer?.Stop(); } catch { }
        }

        /// <summary>鼠标离开提示条 → 重新开始计时（给足一整轮时间再收）。</summary>
        private void OnDuplicateHintMouseLeave(object sender, MouseEventArgs e)
        {
            if (DuplicateHintBar != null && DuplicateHintBar.Visibility == Visibility.Visible)
                RestartDuplicateHintTimer();
        }

        /// <summary>
        /// 显示「已在下载 / 已下载过」提示条。
        /// </summary>
        /// <param name="skippedNames">被跳过的文件名（用于文案；只列举前几个）。</param>
        /// <param name="allSkipped">true = 本次选择**全部**都已存在，一个都没入队。</param>
        private void ShowDuplicateHint(IList<string> skippedNames, bool allSkipped)
        {
            try
            {
                if (DuplicateHintBar == null || DuplicateHintText == null) return;
                int n = skippedNames != null ? skippedNames.Count : 0;
                if (n == 0) return;

                // 文案：全跳过 vs 部分跳过，说清"没有入队"这件事
                string head = allSkipped
                    ? "这些文件已经下载过或正在下载，本次没有加入队列。"
                    : "已跳过 " + n + " 个已在任务列表里的文件（其余文件正常加入队列）。";

                // 最多列 3 个名字，避免提示条被撑得过长
                var sb = new StringBuilder(head);
                int show = Math.Min(n, 3);
                for (int i = 0; i < show; i++)
                {
                    sb.Append(i == 0 ? "　" : "、");
                    sb.Append(skippedNames[i]);
                }
                if (n > show) sb.Append(" 等 " + n + " 个");

                DuplicateHintText.Text = sb.ToString();

                // 全部跳过 → 警示色（用户什么都没得到，需要明确解释）
                // 部分跳过 → 中性色（任务照常进行了，只是少几个）
                DuplicateHintBar.Background = allSkipped
                    ? (Brush)(TryFindResource("WarningLightBrush") ?? Brushes.Transparent)
                    : (Brush)(TryFindResource("SurfaceAltBrush") ?? Brushes.Transparent);
                DuplicateHintBar.BorderBrush = (Brush)(TryFindResource("WarningBrush") ?? Brushes.Gray);

                // 「打开下载目录」只在全都跳过时才有意义（部分跳过时用户关注的是新任务）
                if (DuplicateOpenDirBtn != null)
                    DuplicateOpenDirBtn.Visibility = allSkipped ? Visibility.Visible : Visibility.Collapsed;

                DuplicateHintBar.Visibility = Visibility.Visible;

                // 自动隐藏（用户要求）：显示后开始计时，到点自己收起
                RestartDuplicateHintTimer();
            }
            catch { }
        }

        /// <summary>收起提示条（用户点「知道了」，或自动隐藏到点）。</summary>
        private void OnDismissDuplicateHint(object sender, RoutedEventArgs e)
        {
            HideDuplicateHint();
        }

        /// <summary>提示条上的「打开下载目录」：直接去看已经下好的文件。</summary>
        private void OnOpenDownloadDirFromHint(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = _settings != null ? _settings.OutDir : null;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    Log("下载目录还不存在：" + (dir ?? "(未设置)"));
                    return;
                }
                if (!ShellLaunch.OpenFolder(dir))
                    Log("打开下载目录失败：" + dir);
            }
            catch (Exception ex)
            {
                Log("打开下载目录失败：" + ex.Message);
            }
        }

        /// <summary>用户做了新的下载动作、或提示到时/被关闭时隐藏提示条。</summary>
        private void HideDuplicateHint()
        {
            try
            {
                _dupHintTimer?.Stop();     // 收起了就别再跑计时器
                if (DuplicateHintBar != null) DuplicateHintBar.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        /// <summary>
        /// 从待下载列表里剔除**已经下过或正在下**的文件（2026-10-21 新增，用户要求"在发请求之前判断"）。
        ///
        /// <para>【为什么按 fid 而不是按路径】fid 是文件在网盘/分享里的唯一编号，
        /// 同一个 fid 必然是同一个文件。而路径会变：<c>UniquePath</c> 发现磁盘上已有同名文件时
        /// 会把它改成 "xxx (1)"，于是"按路径判断"就会认为这是个新文件 —— 这正是上一版
        /// 仍然重复下载的根因（用户实测反馈"还是会重复下载"）。</para>
        ///
        /// <para>【什么算"已经下过"】任务列表里存在同 fid 的行，且该行**不是失败/取消**：
        /// · 排队 / 下载中 / 暂停 / 取消中 → 正在处理，跳过；
        /// · 已完成 → 文件已经在了，默认跳过（用户明确要"避免重复下载"）；
        /// · 失败 / 已取消 → 允许重新下载（否则失败一次就再也下不了了）。</para>
        ///
        /// <para>【原地修改】直接把重复项从 <paramref name="files"/> 里移除，
        /// 返回被剔除的数量，调用方据此判断"是不是全都被跳过了"。</para>
        /// </summary>
        private int FilterAlreadyQueued(List<ShareFileItem> files, List<string> skippedNames = null)
        {
            if (files == null || files.Count == 0) return 0;
            int removed = 0;
            try
            {
                for (int i = files.Count - 1; i >= 0; i--)
                {
                    var f = files[i];
                    if (f == null || string.IsNullOrEmpty(f.Fid)) continue;

                    var hit = _tasks.FirstOrDefault(t =>
                        t != null && t.Job != null && t.Job.Pending != null
                        && string.Equals(t.Job.Pending.Fid, f.Fid, StringComparison.Ordinal)
                        && ShouldBlockRedownload(t.State));

                    if (hit == null) continue;

                    files.RemoveAt(i);
                    removed++;
                    skippedNames?.Add(f.Name);   // 供 UI 提示条列举（2026-10-21）
                    string why = hit.State == JobState.Completed ? "已下载过" : "已在任务列表里";
                    Log(why + "，跳过：" + f.Name);
                }
            }
            catch { }
            return removed;
        }

        /// <summary>
        /// 处于这些状态的任务，会阻止同一文件被再次加入下载。
        /// <para>失败 / 已取消**不阻止** —— 否则一次失败就再也下不了同一个文件了。</para>
        /// </summary>
        private static bool ShouldBlockRedownload(JobState st)
        {
            return st == JobState.Queued
                || st == JobState.Downloading
                || st == JobState.Paused
                || st == JobState.Cancelling
                || st == JobState.Completed;
        }

        /// <summary>
        /// 这个目标路径是否已经有一条"还在进行"的任务（排队/下载中/暂停/取消中）。
        ///
        /// <para>为什么不拦"已完成"的：已完成的同名文件由 <see cref="PathUtil.UniquePath"/>
        /// 改名为 "xxx (1)" 另存 —— 用户主动重下同一文件是合理需求（比如上次下坏了）。</para>
        /// </summary>
        private bool HasActiveTaskForDest(string dest)
        {
            if (string.IsNullOrEmpty(dest)) return false;
            try
            {
                foreach (var t in _tasks)
                {
                    if (t.Job == null || string.IsNullOrEmpty(t.Job.Dest)) continue;
                    if (!string.Equals(t.Job.Dest, dest, StringComparison.OrdinalIgnoreCase)) continue;
                    // 只有"还在进行"的才算重复；已完成/失败/已取消的不拦
                    if (t.State == JobState.Queued
                        || t.State == JobState.Downloading
                        || t.State == JobState.Paused
                        || t.State == JobState.Cancelling)
                        return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 给一个文件算出最终目标路径，并在**本批内部**与**已有任务**之间去重。
        /// </summary>
        /// <param name="destDir">目标目录。</param>
        /// <param name="name">文件名（未清洗）。</param>
        /// <param name="takenInBatch">本批已占用的路径（大小写不敏感），防止同批两个同名文件互撞。</param>
        /// <returns>可用的目标路径；<c>null</c> 表示这个文件已经在下、应当跳过。</returns>
        private string ResolveDestOrSkip(string destDir, string name, HashSet<string> takenInBatch)
        {
            string basePath = Path.Combine(destDir, PathUtil.SanitizeFileName(name));

            // ① 本批内部撞名（同一个文件夹里真有两个同名文件，或用户重复勾选）：
            //    这时**不该跳过**，而应像磁盘冲突那样改名 —— 它们是两个不同的文件。
            //    但如果路径完全相同且我们已在本批登记过，说明是同一次误选，跳过。
            if (takenInBatch != null && !takenInBatch.Add(basePath))
            {
                // 本批里已经处理过同一个路径 → 是同一次选择被重复提交，直接跳过
                return null;
            }

            // ② 已有任务正在下这个路径 → 跳过（这才是"误选同一文件"的典型情况）
            if (HasActiveTaskForDest(basePath))
                return null;

            // ③ 磁盘上已存在 → 改名为 xxx (1)。这里必须再过一次
            //    "改名后是否与已有任务撞"，否则改名结果可能正好是另一条在下的任务。
            string dest = PathUtil.UniquePath(basePath);
            int guard = 0;
            while (HasActiveTaskForDest(dest) && guard++ < 50)
                dest = PathUtil.UniquePath(dest);

            if (takenInBatch != null) takenInBatch.Add(dest);
            return dest;
        }

        /// <summary>
        /// 用**已取到的链接**直接建任务入队（不再重新取链）。
        /// 给「获取直链」对话框的「下载这些文件」按钮用。
        /// </summary>
        private void StartDownloadFromItems(List<DownloadItem> items)
        {
            var alloc = AllocateThreads(items.Select(i => i.Size).ToList());
            var jobs = new List<DownloadJob>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // 本批已占用路径
 int skipped = 0;
 var pathSkippedNames = new List<string>();   // 路径级去重跳过的文件名（供 UI 提示）
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (string.IsNullOrEmpty(it.Url)) continue;
                string destDir = Path.Combine(_settings.OutDir, it.RelDir ?? "");
                try { Directory.CreateDirectory(destDir); }
                catch (Exception ex) { Log("创建下载目录失败: " + destDir + " — " + ex.Message); continue; }

                string dest = ResolveDestOrSkip(destDir, it.Name, taken);
                if (dest == null) { skipped++; pathSkippedNames.Add(it.Name); Log("已在下载，跳过：" + it.Name); continue; }
                var item = it;
                jobs.Add(new DownloadJob
                {
                    Name = item.Name, Url = item.Url, Dest = dest,
                    Cookie = _client.CookieStr,
                    Threads = i < alloc.Length ? alloc[i] : _settings.Threads,
                    // 写入方式统一取设置页的全局值（原来靠下载区的「边下边播」勾选框，
                    // 与设置页重复且容易误导，已移除该勾选框）。
                    WriteMode = WriteMode.SegmentedFiles,   // 落盘方式唯一化
                    PartsRoot = string.IsNullOrWhiteSpace(_settings.PartsRoot) ? null : _settings.PartsRoot,
                    LinkRefresher = ct => RefreshLinkAsync(item, ct),
                    // 免转存（从分享直接取链）→ 续传取不到新链，失败文案别承诺"可续传"
                    ResumeCannotRefresh = item.FromShare,
                    // 转存路径的中转文件 fid：下载完成后立即清理
                    TransferredFid = item.TransferredFid,
                    Pending = new PendingInfo
                    {
                        PwdId = _pwdId, Stoken = _stoken, Passcode = PassBox.Text,
                        Fid = item.Fid, Token = item.Token, FromShare = item.FromShare,
                        RelDir = item.RelDir ?? "", Dest = dest, Size = item.Size,
                        // 文件就在当前打开的目录里（RelDir 为空）→ 用该目录名做分组
                        GroupName = item.RelDir == null || item.RelDir.Length == 0
                            ? CurrentFolderGroupName() : null,
                        // 【2026-10-22】源文件定位信息（转存副本没了时可回退免转存）
                        SourceFid = item.SourceFid,
                        SourceName = item.SourceName,
                    },
                });
            }
            if (jobs.Count == 0)
            {
                Log(skipped > 0
                    ? "这些文件都已在下载队列里，没有新增任务。"
                    : "没有可下载的条目。");
                // 用户要求：检查重复时要给看得见的提示（不能只写日志）
                if (skipped > 0) ShowDuplicateHint(pathSkippedNames, allSkipped: true);
                return;
            }
            if (skipped > 0) ShowDuplicateHint(pathSkippedNames, allSkipped: false);
            else HideDuplicateHint();

            LogAllocation(alloc);
            foreach (var job in jobs)
                AddTaskItem(new TaskItem(job, job.Name));
            ClearFileSelection();   // 同上：入队即撤掉列表里的高亮
            Log("已加入下载队列: " + jobs.Count + " 个文件（复用已取链接）"
                + (skipped > 0 ? "（另有 " + skipped + " 个已在下载，已跳过）" : ""));
            Tabs.SelectedIndex = 1;
            RunJobs(jobs);
        }

        private async Task StartDownloadAsync(List<ShareFileItem> selected, bool forceNoSave = false,
            CancellationToken ct = default(CancellationToken))
        {
            if (_client == null)
            {
                Log("请先登录或解析分享");
                return;
            }
            if (selected.Count == 0)
            {
                Log("没有选中任何文件");
                return;
            }

            SetBusy(true);
            try
            {
                // 把选中的文件夹递归展开成文件列表（保留相对目录）
                //
                // 【2026-10-21 修复】起点从 "" 改为**当前所在的目录名**。
                //
                // 原来的问题（用户反馈「任务页的文件夹分类展示是正常的，但是下载的文件
                // 没有放到对应文件夹位置，反而直接下载，和任务页看到的不符合」）：
                //   · 用户在「《亚马逊原版电子书》…」这个目录里选中若干**文件**下载；
                //   · CollectFilesAsync 的起点是 ""，而这些文件不在任何子文件夹里，
                //     于是它们的 RelDir 就是 ""；
                //   · 落盘路径 = OutDir + "" + 文件名 → 文件全铺在下载根目录；
                //   · 但任务页分组名用的是 CurrentFolderGroupName()（= 当前浏览目录名），
                //     所以界面上归好了组、磁盘上却没建目录 —— 两边不一致。
                //
                // 修法：起点用**当前目录名**（但不是 _shareRootName）。
                //
                // ⚠️ 为什么不能用 CurrentFolderGroupName()：那个函数在"分享根目录"时会
                //    回退到 _shareRootName（分享标题），而 _shareRootName 的注释明确写着
                //    「只用于任务页的文件夹分组显示，不参与目标路径拼接」。
                //    拿它拼磁盘路径会凭空造出一个以分享标题命名的大文件夹 —— 不是用户要的。
                //    所以这里只取 _pathList 的最后一层：那是**用户真正点进去的目录**，
                //    有它才说明"文件应该归到这个目录里"。
                string baseRel = _pathList.Count > 0 ? (_pathList[_pathList.Count - 1] ?? "") : "";
                var files = await CollectFilesAsync(selected, baseRel, 0, ct);
                ct.ThrowIfCancellationRequested();
                if (files.Count == 0)
                {
                    Log("没有要下载的文件");
                    return;
                }
                Log("共展开 " + files.Count + " 个文件，正在取直链…");

                // ---- 文件数保护（对齐 Python 版 check_limit）----
                // 必须在"取直链"之前拦下：一旦进入 GetDownloadItemsAsync，
                // 就会按批调用服务端接口，此时再中止已经打出去一部分请求了，
                // 既浪费额度又可能踩进风控。所以这道检查要尽量靠前。
                if (files.Count > MaxFiles)
                {
                    // 硬上限：直接拒绝，不提供"继续"选项。
                    // 理由与 Python 一致 —— 这个量级已经超出单次任务的合理范围，
                    // 分批做才是对的（用户可以少选一些重复操作）。
                    Log(string.Format(
                        "文件数量过多（本次 {0} 个，单次上限 {1} 个），已中止。请减少选择后分批下载。",
                        files.Count, MaxFiles));
                    AppDialog.Show(this,
                        string.Format(
                            "本次共展开 {0} 个文件，超过单次处理上限 {1} 个。\n\n" +
                            "请减少选择后分批下载（例如先选中一部分再点「下载选中」）。\n\n" +
                            "这是为了避免一次性发出过多取链请求触发服务端风控。",
                            files.Count, MaxFiles),
                        "文件数量过多", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (files.Count > ConfirmThreshold)
                {
                    // 软提醒：数量偏多但仍在上限内，让用户自己确认。
                    // 默认按钮设为"否"，避免连点两次回车就把大批任务放出去。
                    var ans = AppDialog.Show(this,
                        string.Format(
                            "本次共展开 {0} 个文件，数量较多。\n\n" +
                            "确认要一次性全部加入下载队列吗？",
                            files.Count),
                        "确认下载", MessageBoxButton.YesNo, MessageBoxImage.Question,
                        MessageBoxResult.No);
                    if (ans != MessageBoxResult.Yes)
                    {
                        Log(string.Format("已取消（本次 {0} 个文件，用户未确认）", files.Count));
                        return;
                    }
                }

                // 【2026-10-21】去重要在**取链之前**做（用户反馈"应该在发请求之前就判断"）。
                // 上一版把判断放在取链之后，白白发了一轮网络请求才发现重复，
                // 用户看到日志里"正在取直链…"已经跑过了，体验上等于"还是重复下载了"。
                //
                // 这里按**文件身份**判断，而不是按目标路径：
                //   · 同一个 fid 就是同一个文件，无论目标路径是否被 UniquePath 改过名；
                //   · 上一版按路径判断有个致命漏洞 —— 已完成的任务会让 UniquePath 把新任务
                //     改名成 "xxx (1)"，路径不同了于是判定"不重复"，结果又下了一份。
                var skippedNames = new List<string>();
                int preSkipped = FilterAlreadyQueued(files, skippedNames);
                if (preSkipped > 0 && files.Count == 0)
                {
                    Log("这些文件都已经在任务列表里了，没有新增下载。");
                    // 用户要求「添加提示信息，提示用户已经下载过，现在只是不入队」——
                    // 全部重复时一个任务都不会新增，必须在界面上说清楚。
                    ShowDuplicateHint(skippedNames, allSkipped: true);
                    return;
                }
                // 部分重复：提示"跳过 N 个"，但继续处理其余文件
                if (preSkipped > 0) ShowDuplicateHint(skippedNames, allSkipped: false);
                else HideDuplicateHint();   // 没有重复 → 收掉上一条旧提示

                var items = await GetDownloadItemsAsync(files, forceNoSave);

                // 连接数按文件大小比例分配（大文件多分、小文件少分，各不低于下限）
                var alloc = AllocateThreads(items.Select(x => x.Size).ToList());

                var jobs = new List<DownloadJob>();
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
 int skipped = 0;
 var pathSkippedNames = new List<string>();   // 路径级去重跳过的文件名（供 UI 提示）
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    string destDir = Path.Combine(_settings.OutDir, it.RelDir ?? "");
                    try { Directory.CreateDirectory(destDir); }
                    catch (Exception ex) { Log("创建下载目录失败: " + destDir + " — " + ex.Message); continue; }
                    string dest = ResolveDestOrSkip(destDir, it.Name, taken);
                    if (dest == null) { skipped++; pathSkippedNames.Add(it.Name); Log("已在下载，跳过：" + it.Name); continue; }
                    var item = it;   // 闭包捕获：每次循环一个新变量，避免所有回调指向同一个
                    jobs.Add(new DownloadJob
                    {
                        Name = item.Name, Url = item.Url, Dest = dest,
                        Cookie = _client.CookieStr,
                        Threads = i < alloc.Length ? alloc[i] : _settings.Threads,
                        // 写入方式统一取设置页的全局值（原下载区「边下边播」勾选框已移除）。
                        WriteMode = WriteMode.SegmentedFiles,   // 落盘方式唯一化
                        // 分片根目录：留空 = 与目标同级（默认）。非空则分片集中存放，
                        // 下载目录不被 .qparts 污染，也支持分片跨盘（如放 SSD）。
                        PartsRoot = string.IsNullOrWhiteSpace(_settings.PartsRoot) ? null : _settings.PartsRoot,
                        // 直链带时效签名，长任务下到一半会过期。
                        // 给下载器一个"重新取链"的口子，它就能自动换链续传，而不是整个任务失败。
                        LinkRefresher = ct => RefreshLinkAsync(item, ct),
                        // 免转存（从分享直接取链）→ 续传取不到新链，失败文案别承诺"可续传"
                        ResumeCannotRefresh = item.FromShare,
                        // 续传上下文：写进「未完成任务」记录后，程序重启也能重取链接着下。
                        Pending = new PendingInfo
                        {
                            PwdId = _pwdId, Stoken = _stoken,
                            Passcode = PassBox.Text,   // 分享提取码（无码时为空）
                            Fid = item.Fid, Token = item.Token,
                            FromShare = item.FromShare, RelDir = item.RelDir ?? "",
                            Dest = dest, Size = item.Size,
                            GroupName = item.RelDir == null || item.RelDir.Length == 0
                                ? CurrentFolderGroupName() : null,
                            // 【2026-10-22】源文件定位信息（转存副本没了时可回退免转存）
                            SourceFid = item.SourceFid,
                            SourceName = item.SourceName,
                        },
                    });
                }
                if (jobs.Count == 0)
                {
                    Log(skipped > 0
                        ? "这些文件都已在下载队列里，没有新增任务。"
                        : "没有取得直链（分享者可能关闭直链或游客额度限制）");
                    // 用户要求：检查重复时要给看得见的提示（不能只写日志）
                    if (skipped > 0) ShowDuplicateHint(pathSkippedNames, allSkipped: true);
                    return;
                }
                if (skipped > 0) ShowDuplicateHint(pathSkippedNames, allSkipped: false);

                LogAllocation(alloc);
                foreach (var job in jobs)
                    AddTaskItem(new TaskItem(job, job.Name));
                ClearFileSelection();   // 已提交 → 撤掉高亮，避免误触重复入队
                Log("已加入下载队列: " + jobs.Count + " 个文件"
                    + (skipped > 0 ? "（另有 " + skipped + " 个已在下载，已跳过）" : ""));
                Tabs.SelectedIndex = 1;
                RunJobs(jobs);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task<List<ShareFileItem>> CollectFilesAsync(IEnumerable<ShareFileItem> items, string relPrefix, int depth,
            CancellationToken ct = default(CancellationToken))
        {
            var result = new List<ShareFileItem>();
            foreach (var it in items)
            {
                ct.ThrowIfCancellationRequested();   // 「终止入队」能立刻停下（大目录展开时尤其重要）
                if (it.IsDir)
                {
                    if (depth >= 40)
                        continue;
                    List<QuarkFileEntry> children;
                    try
                    {
                        children = ShareMode
                            ? await _client.ShareDetailAsync(_pwdId, _stoken, it.Fid)
                            : await _client.ListDirAsync(it.Fid);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;   // 取消要原样上抛，不能被下面的 catch 吞掉
                    }
                    catch
                    {
                        continue;
                    }
                    var kids = children.Select(c => new ShareFileItem
                    {
                        Fid = c.Fid, Name = c.Name, Size = c.Size, IsDir = c.IsDir, Token = c.Token,
                    }).ToList();
                    var sub = await CollectFilesAsync(kids, Path.Combine(relPrefix, PathUtil.SanitizeFileName(it.Name)), depth + 1, ct);
                    result.AddRange(sub);
                }
                else
                {
                    result.Add(new ShareFileItem
                    {
                        Fid = it.Fid, Name = it.Name, Size = it.Size, IsDir = false,
                        Token = it.Token, RelDir = relPrefix,
                    });
                }
            }
            return result;
        }

        private async Task<List<DownloadItem>> GetDownloadItemsAsync(List<ShareFileItem> files,
            bool forceNoSave = false)
        {
            if (ShareMode)
            {
                // 【2026-10-03】用户要求给「免转存下载」一个显式入口。
                // 默认（登录状态下）走「转存→取直链」，会往用户网盘写一份；
                // 免转存则不碰网盘，直接从分享取链 —— 省空间、也不留中转垃圾。
                if (forceNoSave)
                {
                    Log("按你的选择走「免转存」路径：不写入网盘，直接从分享取链。");
                    return await NoSaveGetItemsAsync(files);
                }

                bool loggedIn = !string.IsNullOrEmpty(_settings.Cookie);
                if (loggedIn)
                {
                    Log("已登录，走「转存→取直链」路径（更稳定）");
                    try
                    {
                        return await SaveAndGetItemsAsync(files);
                    }
                    catch (CapacityDowngradeException)
                    {
                        // 移植自 Python 版：转存遇「网盘容量不足」时**自动降级**为免转存
                        // （不写入网盘、直接从分享取链），而不是让整批任务失败。
                        // 这是很常见的场景：网盘满了但仍想下载公开分享。
                        Log("网盘容量不足，已自动改用「免转存」路径取直链（不写入网盘）。");
                        return await NoSaveGetItemsAsync(files);
                    }
                }
                Log("未登录，走「免转存」路径（游客可能受额度限制）");
                return await NoSaveGetItemsAsync(files);
            }
            return await DriveGetItemsAsync(files);
        }

        /// <summary>
        /// 内部信号：转存因网盘容量不足失败，请求上层降级到免转存。
        /// 只用于本类的控制流，不向外暴露。
        /// </summary>
        private sealed class CapacityDowngradeException : Exception
        {
            public CapacityDowngradeException() : base("网盘容量不足") { }
        }

        private async Task<List<DownloadItem>> NoSaveGetItemsAsync(List<ShareFileItem> files)
        {
            var targets = files.Select(f => new LinkTarget { Fid = f.Fid, Token = f.Token, Name = f.Name }).ToList();
            var urlOf = await FetchUrlsWithRetryAsync(targets, fromShare: true);
            var items = new List<DownloadItem>();
            foreach (var f in files)
            {
                if (urlOf.TryGetValue(f.Fid, out string url))
                    items.Add(new DownloadItem
                    {
                        Name = f.Name, Size = f.Size, Url = url, RelDir = f.RelDir,
                        Fid = f.Fid, Token = f.Token, FromShare = true,
                        // stoken 过期自愈用（2026-10-20）：重解析时按这三个字段逐级找回文件
                        Passcode = _sharePasscode,
                        StartFid = _shareStartFid,
                        ShareRelDir = f.RelDir,
                        // 【2026-10-22】免转存路径下源文件就是自己
                        SourceFid = f.Fid,
                        SourceName = f.Name,
                    });
                else
                    Log("跳过（无直链）: " + f.Name);
            }
            return items;
        }

        private async Task<List<DownloadItem>> DriveGetItemsAsync(List<ShareFileItem> files)
        {
            var targets = files.Select(f => new LinkTarget { Fid = f.Fid, Name = f.Name }).ToList();
            var urlOf = await FetchUrlsWithRetryAsync(targets, fromShare: false);
            var items = new List<DownloadItem>();
            foreach (var f in files)
            {
                if (urlOf.TryGetValue(f.Fid, out string url))
                    items.Add(new DownloadItem
                    {
                        Name = f.Name, Size = f.Size, Url = url, RelDir = f.RelDir,
                        Fid = f.Fid, FromShare = false,
                    });
                else
                    Log("无直链: " + f.Name);
            }
            return items;
        }

        private async Task<List<DownloadItem>> SaveAndGetItemsAsync(List<ShareFileItem> files)
        {
            var items = new List<DownloadItem>();
            string toFid = await EnsureFolderAsync(QuarkConstants.DefaultFolder);
            if (string.IsNullOrEmpty(toFid))
            {
                Log("无法创建中转文件夹，转存失败");
                return items;
            }

            for (int i = 0; i < files.Count; i += 50)
            {
                var batch = files.Skip(i).Take(50).ToList();
                var fids = batch.Select(f => f.Fid).ToList();
                var tokens = batch.Select(f => f.Token).ToList();
                List<string> newFids;
                try
                {
                    newFids = await _client.SaveShareAndGetFidsAsync(fids, tokens, toFid, _pwdId, _stoken);
                }
                catch (QuarkApiException ex) when (QuarkClient.IsCapacityMessage(ex.Message))
                {
                    // 容量不足：不用"continue 跳过这批"，而是整批降级 ——
                    // 容量满时后续每批都会失败，继续试只是浪费时间与请求额度。
                    throw new CapacityDowngradeException();
                }
                catch (Exception ex)
                {
                    Log("转存失败: " + ex.Message);
                    continue;
                }

                if (newFids.Count > 0)
                {
                    // 转存后校验：等文件落盘，过滤无效 fid（对应原版 _resolve_saved_fids）
                    newFids = await ResolveSavedFidsAsync(toFid, newFids);

                    // 用转存后的新 fid 取链，名字沿用分享里的原名（只为日志好读）
                    var targets = new List<LinkTarget>();
                    for (int j = 0; j < newFids.Count; j++)
                    {
                        targets.Add(new LinkTarget
                        {
                            Fid = newFids[j],
                            Name = j < batch.Count ? batch[j].Name : newFids[j],
                        });
                    }
                    var urlOf = await FetchUrlsWithRetryAsync(targets, fromShare: false);

                    for (int j = 0; j < newFids.Count && j < batch.Count; j++)
                    {
                        if (urlOf.TryGetValue(newFids[j], out string url))
                            items.Add(new DownloadItem
                            {
                                Name = batch[j].Name, Size = batch[j].Size, Url = url, RelDir = batch[j].RelDir,
                                // 转存后要按"新 fid"取链，所以这里记的是转存后的 fid，不是分享里的
                                Fid = newFids[j], FromShare = false,
                                // 这份新 fid 就是用户在网盘里的中转文件，下载完应立即清理
                                TransferredFid = newFids[j],
                                // 【2026-10-22】同时记下**分享里的源文件**定位信息：
                                // 中转副本被用户删掉后，续传可据此回退到免转存（源文件还在）。
                                SourceFid = batch[j].Fid,
                                SourceName = batch[j].Name,
                            });
                        else
                            Log("无直链: " + batch[j].Name);
                    }
                }
                // 只有「转存」路径才在用户网盘里写了一份中转文件，下载完应当清理。
                // 这里先记进兜底队列（批结束再清一次），作为"单文件完成即清"的备份 ——
                // 万一某个任务的完成回调因异常没跑到，也不至于让中转文件永久残留。
                // newFids 来自 _client.SaveShareAndGetFidsAsync（转存），天然是中转件。
                lock (_cleanupLock)
                {
                    foreach (var f in newFids)
                        _pendingCleanup.Add(new CleanupEntry { Fid = f, IsTransferred = true });
                }
            }
            return items;
        }

        /// <summary>取链目标：一个待取直链的文件。</summary>
        private sealed class LinkTarget
        {
            public string Fid;
            public string Token;   // 仅「分享免转存」路径需要
            public string Name;    // 仅用于日志
        }

        /// <summary>
        /// 批量取直链 + 多轮重试。对应原版 fetch_links / fetch_share_links。
        ///
        /// 相比早先的实现，补了原版有、C# 之前缺的两点：
        ///  1. **失败原因不再被吞掉**。以前是 `catch { links = new List&lt;DownloadLink&gt;(); }`，
        ///     用户只看到「跳过（无直链）」，无从判断是会员额度、风控还是分享被关。
        ///  2. **多一轮"逐个单请求"兜底**。批量接口更易被限流，剩少量文件时逐个问成功率更高。
        /// </summary>
        private async Task<Dictionary<string, string>> FetchUrlsWithRetryAsync(
            List<LinkTarget> targets, bool fromShare)
        {
            var urls = new Dictionary<string, string>();
            var pending = new List<LinkTarget>(targets);
            var reason = new Dictionary<string, string>();
            int[] delays = { 0, 800, 1600, 3000, 5000 };

            // ---- 先查直链缓存（对应 Python 的 LINK_TTL 复用）----
            // 命中的直接从 pending 里摘掉，不再发请求。
            int cached = 0;
            var miss = new List<LinkTarget>();
            foreach (var t in pending)
            {
                string hit = _linkCache.Get(t.Fid);
                if (hit != null) { urls[t.Fid] = hit; cached++; }
                else miss.Add(t);
            }
            if (cached > 0)
                Log(string.Format("{0} 个文件命中直链缓存（10 分钟内有效），跳过取链。", cached));
            pending = miss;

            for (int round = 0; round < delays.Length && pending.Count > 0; round++)
            {
                if (round > 0)
                {
                    Log(string.Format("{0} 个文件未返回直链，重试第 {1} 轮…", pending.Count, round));
                    await Task.Delay(delays[round]);
                }
                var next = new List<LinkTarget>();
                for (int i = 0; i < pending.Count; i += 20)
                {
                    var batch = pending.Skip(i).Take(20).ToList();
                    List<DownloadLink> links = null;
                    string err = null;
                    try { links = await FetchBatchAsync(batch, fromShare); }
                    catch (Exception ex) { err = ex.Message; }
                    if (err != null)
                    {
                        foreach (var t in batch)
                            reason[t.Fid] = "接口报错: " + err;
                    }

                    var got = new Dictionary<string, string>();
                    if (links != null)
                    {
                        foreach (var l in links)
                        {
                            if (!string.IsNullOrEmpty(l.Fid) && !string.IsNullOrEmpty(l.DownloadUrl))
                                got[l.Fid] = l.DownloadUrl;
                        }
                    }

                    foreach (var t in batch)
                    {
                        string url;
                        if (got.TryGetValue(t.Fid, out url))
                        {
                            urls[t.Fid] = url;
                        }
                        else
                        {
                            if (err == null)
                                reason[t.Fid] = "接口未返回该文件的 download_url";
                            next.Add(t);
                        }
                    }
                }
                pending = next;
            }

            // 逐个单请求兜底：批量接口更容易被限流，剩少量文件时单独问成功率明显更高。
            if (pending.Count > 0 && pending.Count <= 30)
            {
                Log(string.Format("对剩余 {0} 个文件逐个单独重试…", pending.Count));
                var still = new List<LinkTarget>();
                foreach (var t in pending)
                {
                    await Task.Delay(150);
                    try
                    {
                        var one = await FetchBatchAsync(new List<LinkTarget> { t }, fromShare);
                        var hit = one == null ? null : one.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl));
                        if (hit != null)
                        {
                            urls[t.Fid] = hit.DownloadUrl;
                        }
                        else
                        {
                            reason[t.Fid] = "单个请求仍未返回 download_url";
                            still.Add(t);
                        }
                    }
                    catch (Exception ex)
                    {
                        reason[t.Fid] = "单个请求报错: " + ex.Message;
                        still.Add(t);
                    }
                }
                pending = still;
            }

            if (pending.Count > 0)
            {
                Log(string.Format("仍有 {0} 个文件未取得直链，逐条原因:", pending.Count));
                foreach (var t in pending)
                {
                    string r;
                    Log(string.Format("  - {0}  ->  {1}", t.Name, reason.TryGetValue(t.Fid, out r) ? r : "未知"));
                }
                Log("判断: 接口报错或 code 非 0 多为账号/权限/限流；"
                    + "「无 download_url」多为会员额度或文件被风控；"
                    + "分享直链还可能是分享者已关闭直链，或超出游客可获取的大小限制。");
            }

            // ---- 写回缓存：本次取到的链在 TTL 内可被后续操作复用 ----
            foreach (var kv in urls)
                _linkCache.Put(kv.Key, kv.Value);

            return urls;
        }

        private Task<List<DownloadLink>> FetchBatchAsync(List<LinkTarget> batch, bool fromShare)
        {
            if (fromShare)
            {
                // 【2026-10-22 诊断】正常下载（用户勾选文件按 D 发送）走的是这条路径，
                // 不是续传的 TryShareDownloadAsync。用户实测"新文件能下、老的续传不行"，
                // 需要看清这里的入参差异 —— 尤其是 stoken 是不是当前会话那一份。
                Log("[诊断·批量取链] 分享模式 " + batch.Count + " 个文件"
                    + " pwdId=" + Short(_pwdId)
                    + " stoken=" + Short(_stoken)
                    + " 首个fid=" + Short(batch[0].Fid)
                    + " 首个token=" + Short(batch[0].Token));
                // 【2026-10-23 诊断】打清"这次取链用的是不是登录态" ——
                // 夸克更新源用**空 cookie（游客）** 取链，拿到的是 dl-guest-* 域名，
                // 实测下载被拒（412）。手动免转存走这里，是**带登录态**的，
                // 拿到的应该是 dl-pc-*。两条日志对照即可确认差别。
                Log("[诊断·批量取链] 登录态: cookie 长度="
                    + (_client == null || _client.CookieStr == null ? 0 : _client.CookieStr.Length)
                    + "（0 = 游客，非 0 = 登录）");
                return _client.GetShareDownloadAsync(
                    batch.Select(t => t.Fid).ToList(),
                    batch.Select(t => t.Token ?? "").ToList(),
                    _pwdId, _stoken);
            }
            Log("[诊断·批量取链] 网盘模式 " + batch.Count + " 个文件");
            return _client.GetDownloadAsync(batch.Select(t => t.Fid).ToList());
        }

        /// <summary>
        /// 重新获取某个文件的直链。下载中直链签名过期时由下载器回调触发。
        /// 按这个文件当初走的是哪条路径取链（分享免转存 / 我的网盘），保证刷新方式一致。
        /// </summary>
        private async Task<string> RefreshLinkAsync(DownloadItem item, CancellationToken ct)
        {
            if (item == null || string.IsNullOrEmpty(item.Fid))
                return null;

            // 缓存里的链可能仍然有效（例如只是瞬时网络抖动触发了重取），
            // 先看一眼能省一次接口调用。但注意：下载器**是因为旧链失败才来刷新**的，
            // 所以这里必须**绕过命中缓存**，否则会把同一条坏链再喂回去 → 死循环。
            // 因此不用 _linkCache.Get，而是直接取新链后刷新缓存。
            _linkCache.Invalidate(item.Fid);

            string url;
            if (item.FromShare)
            {
                // 免转存：走"先试旧凭证 → 失败自动重解析分享"的自愈路径（2026-10-20）。
                // stoken 一般几小时就过期，原来一过期任务就卡死，用户只能手动重新解析。
                url = await GetShareLinkWithReResolveAsync(
                    _client, item.Fid, item.Token, _pwdId, _stoken, item.Passcode,
                    item.StartFid, item.ShareRelDir,
                    // 【2026-10-22】老任务没有 ShareRelDir → 用 RelDir 兜底下钻
                    item.RelDir, item.Name,
                    (freshStoken, freshToken) =>
                    {
                        _stoken = freshStoken;      // 回填会话级
                        item.Token = freshToken;    // 回填任务级
                    }, ct).ConfigureAwait(false);
            }
            else
            {
                var links = await _client.GetDownloadAsync(new List<string> { item.Fid }).ConfigureAwait(false);
                if (links == null) return null;
                var hit = links.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl));
                url = hit == null ? null : hit.DownloadUrl;
            }

            if (url != null)
                _linkCache.Put(item.Fid, url);   // 新链写回缓存，供后续同类操作复用
            return url;
        }

        // ---------------- 免转存续传：stoken 过期自愈（2026-10-20 移植） ----------------

        /// <summary>
        /// 免转存取链：先用手头的（可能已过期的）stoken / share_fid_token 试一次；
        /// 失败就**自动重解析分享** —— 重新申请 stoken、沿 ShareRelDir 逐级展开定位
        /// 目标文件拿到新的 share_fid_token，再取一次链。
        ///
        /// <para>免转存续传原先只认"保存下来的旧 stoken"，它一般几小时~几天就过期，
        /// 过期后 <see cref="QuarkClient.GetShareDownloadAsync"/> 一律失败：
        /// 点续传 → 该任务被跳过；下载中换链 → 任务中止。用户只能手动重新解析分享。
        /// 现在改成点一次续传/换链即自愈（分享被取消/改密码仍救不回，如实返回 null
        /// 让上层按原逻辑提示）。</para>
        /// </summary>
        /// <param name="client">取链用的客户端（续传路径可能是局部 client，不能假设是 _client）。</param>
        /// <param name="fid">分享内文件 fid（分享内稳定不变，重解析后仍用它定位）。</param>
        /// <param name="token">share_fid_token。</param>
        /// <param name="pwdId">分享 ID。</param>
        /// <param name="stoken">分享凭证。</param>
        /// <param name="passcode">提取码；无码分享为空串/null。</param>
        /// <param name="startFid">分享进入目录 fid（普通分享为 "0"）。</param>
        /// <param name="shareRelDir">文件在分享内的完整相对目录（相对 startFid；"" = 进入目录本身）。</param>
        /// <param name="fallbackRelDir">
        /// 【2026-10-22】备用下钻路径：<paramref name="shareRelDir"/> 为空时（老任务
        /// 快照里没有这个字段，实测全是 null）用 <c>GroupName</c> / <c>RelDir</c> 推断。
        /// </param>
        /// <param name="fileName">源文件名；路径与 fid 都定位不到时按名字兜底。</param>
        /// <param name="onReResolved">重解析成功时回调，把 (新 stoken, 新 token) 写回调用方记录。</param>
        /// <returns>直链；彻底失败返回 null（调用方按原逻辑提示"需重新解析分享"）。</returns>
        private async Task<string> GetShareLinkWithReResolveAsync(
            QuarkClient client, string fid, string token, string pwdId, string stoken,
            string passcode, string startFid, string shareRelDir,
            string fallbackRelDir, string fileName,
            Action<string, string> onReResolved, CancellationToken ct)
        {
            if (client == null || string.IsNullOrEmpty(fid) || string.IsNullOrEmpty(pwdId))
                return null;

            // ① 先用现有凭据试一次 —— 绝大多数情况这一步就成，不值得惊动重解析
            string url = await TryShareDownloadAsync(client, fid, token, pwdId, stoken).ConfigureAwait(false);
            if (url != null)
                return url;

            ct.ThrowIfCancellationRequested();

            // ② 自动重解析：换新 stoken + 新 share_fid_token
            //    （async 方法不能用 out 参数，用 Tuple 回传：Item1 = 新 stoken，Item2 = 新 token）
            var fresh = await TryReResolveShareTokenAsync(
                client, pwdId, passcode, startFid, shareRelDir, fid,
                fallbackRelDir, fileName, ct).ConfigureAwait(false);
            if (fresh == null)
            {
                Log("分享直链获取失败，自动重新解析分享也没有成功（分享可能已被取消、删除或改了提取码）。");
                return null;
            }

            if (onReResolved != null)
                onReResolved(fresh.Item1, fresh.Item2);

            // ③ 用新凭据再取一次
            url = await TryShareDownloadAsync(client, fid, fresh.Item2, pwdId, fresh.Item1).ConfigureAwait(false);
            if (url != null)
                Log("分享凭证已过期，已自动重新解析分享并拿到新直链。");
            return url;
        }

        /// <summary>
        /// 取第一个非空字符串（2026-10-22 新增）。
        /// <para>用于「老任务没有 <c>ShareRelDir</c>，拿 <c>GroupName</c> / <c>RelDir</c> 兜底下钻路径」——
        /// 这两个字段在 2026-10-20 之前就有，所以老任务里通常是有值的。</para>
        /// </summary>
        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null) return null;
            foreach (string v in values)
                if (!string.IsNullOrWhiteSpace(v))
                    return v;
            return null;
        }

        /// <summary>取分享直链；吞掉一切接口异常，失败只返回 null（由上层决定提示文案）。</summary>
        private async Task<string> TryShareDownloadAsync(
            QuarkClient client, string fid, string token, string pwdId, string stoken)
        {
            try
            {
                // 【2026-10-22 诊断】把入参形态打出来 —— 用户实测"取链成功但 0 B/s"，
                // 需要区分是「token 本身不对」还是「token 对但 CDN 拒下载」。
                // 只打长度和前缀，不打完整值（stoken 是敏感凭证）。
                Log("[诊断·取链] fid=" + Short(fid) + " token=" + Short(token)
                    + " pwdId=" + Short(pwdId) + " stoken=" + Short(stoken));

                var links = await client.GetShareDownloadAsync(
                    new List<string> { fid },
                    new List<string> { token ?? "" },
                    pwdId, stoken).ConfigureAwait(false);
                if (links == null)
                {
                    Log("[诊断·取链] 接口返回 null");
                    return null;
                }
                var hit = links.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl));
                if (hit == null)
                {
                    Log("[诊断·取链] 返回 " + links.Count + " 条，但都没有 DownloadUrl");
                    return null;
                }
                Log("[诊断·取链] 成功，直链 host=" + SafeHostOf(hit.DownloadUrl)
                    + " 长度=" + hit.DownloadUrl.Length);
                return hit.DownloadUrl;
            }
            catch (Exception ex)
            {
                Log("获取分享直链失败: " + ex.Message);
                // 异常原文里通常带业务码（如 41020 转存文件token校验异常），
                // 这是判断"该走哪条修复路径"的关键，必须打全。
                Log("[诊断·取链] 异常类型=" + ex.GetType().Name);
                return null;
            }
        }

        /// <summary>日志用：只留前 8 个字符，避免把凭证整条写进日志。</summary>
        private static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(空)";
            return s.Length <= 8 ? s : s.Substring(0, 8) + "…(" + s.Length + ")";
        }

        /// <summary>从直链里取 host（直链很长，日志里只打域名）。</summary>
        private static string SafeHostOf(string url)
        {
            try { return new Uri(url).Host; }
            catch { return "?"; }
        }

        /// <summary>
        /// 重新解析分享：申请新 stoken → 从 startFid 起沿 shareRelDir 逐级进目录 →
        /// 在最终目录里**按 fid** 找到目标文件的**新** share_fid_token。
        /// 按 fid 匹配而不是按名字：fid 在分享内不变，比文件名可靠。
        ///
        /// <para>【2026-10-22 补强】用户实测：重启后 9 个未完成任务里 8 个救不回来，
        /// 日志只有一句「自动重新解析分享也没有成功」，且**距离上一次失败只隔 1 秒** ——
        /// 逐级下钻 + 逐页找文件根本不可能这么快，说明它压根没往下走。</para>
        ///
        /// <para>【根因】<c>StartFid</c> / <c>ShareRelDir</c> 是 2026-10-20 才加的字段，
        /// 老任务的快照里**是 null**。两个都空 → 只能在分享根目录找，
        /// 而文件在子目录里（实测在 <c>小书屋01</c> / <c>Z 周星驰电影系列合集 98集</c> 下）
        /// → 永远找不到，每次重启都重复同一个失败。</para>
        ///
        /// <para>【现在的三层兜底】</para>
        /// <list type="number">
        /// <item>按记录下来的 <c>ShareRelDir</c> 逐级下钻（正常路径）；</item>
        /// <item><c>ShareRelDir</c> 为空时，用 <c>GroupName</c> / <c>RelDir</c> 兜底下钻
        ///       （老任务里这两个字段是有值的，实测就是现成的路径线索）；</item>
        /// <item>都失败 → <b>在分享里按 fid 全量广度优先搜索</b>。
        ///       fid 在同一分享内不变，所以不需要知道路径就能定位。</item>
        /// </list>
        /// </summary>
        /// <returns>null = 失败；否则 Item1 = 新 stoken，Item2 = 新 share_fid_token。
        /// 用 Tuple 而不是 out 参数：async 方法不允许 ref/out。</returns>
        private async Task<Tuple<string, string>> TryReResolveShareTokenAsync(
            QuarkClient client, string pwdId, string passcode, string startFid,
            string shareRelDir, string fid, string fallbackRelDir, string fileName,
            CancellationToken ct)
        {
            try
            {
                // ① 重新申请分享凭证（分享取消/改密码都会在这一步失败）
                Log("[诊断·重解析] 开始 pwdId=" + Short(pwdId) + " passcode="
                    + (string.IsNullOrEmpty(passcode) ? "(无)" : "(有)")
                    + " startFid=" + Short(startFid) + " fid=" + Short(fid)
                    + " ShareRelDir=" + (string.IsNullOrEmpty(shareRelDir) ? "(空)" : shareRelDir)
                    + " 兜底路径=" + (string.IsNullOrEmpty(fallbackRelDir) ? "(空)" : fallbackRelDir));

                string stoken = await client.GetStokenAsync(pwdId, passcode ?? "").ConfigureAwait(false);
                if (string.IsNullOrEmpty(stoken))
                {
                    Log("重新解析分享失败：分享凭证申请不到（分享可能已被取消或改了提取码）。");
                    return null;
                }
                Log("[诊断·重解析] 新 stoken 已拿到 " + Short(stoken));

                string pdirFid = string.IsNullOrEmpty(startFid) ? "0" : startFid;

                // ② 逐级下钻：先按记录下来的 ShareRelDir，再退回 GroupName/RelDir 推断。
                //    ⚠️ 两次尝试都要走，不能因为第一次下钻失败就直接放弃 ——
                //    老任务的 ShareRelDir 是 null，正好走第二次。
                string[] candidates = { shareRelDir, fallbackRelDir };
                foreach (string cand in candidates)
                {
                    ct.ThrowIfCancellationRequested();

                    string rel = (cand ?? "").Replace('/', '\\').Trim('\\');
                    string cur = pdirFid;
                    bool ok = true;

                    Log("[诊断·重解析] 尝试路径「" + (rel.Length == 0 ? "(根目录)" : rel) + "」");

                    if (rel.Length > 0)
                    {
                        foreach (string seg in rel.Split('\\'))
                        {
                            if (seg.Length == 0) continue;
                            ct.ThrowIfCancellationRequested();

                            // 名字匹配做两手准备：目录列表里存的是目录原名，而 RelDir 系列
                            // 字段是 SanitizeFileName 规范化过的，原名带非法字符时两者不同。
                            var dir = await FindShareEntryAsync(client, pwdId, stoken, cur,
                                e => e.IsDir
                                     && (e.Name == seg || PathUtil.SanitizeFileName(e.Name) == seg),
                                ct).ConfigureAwait(false);
                            if (dir == null)
                            {
                                Log("[诊断·重解析] 目录段「" + seg + "」没找到（在 pdirFid="
                                    + Short(cur) + " 下）");
                                ok = false;
                                break;
                            }
                            cur = dir.Fid;
                            Log("[诊断·重解析] 目录段「" + seg + "」→ pdirFid=" + Short(cur));
                        }
                    }

                    if (!ok) continue;   // 这条路径走不通，试下一条

                    // ③ 到达目录后按 fid 定位文件，拿它的新 share_fid_token
                    var file = await FindShareEntryAsync(client, pwdId, stoken, cur,
                        e => !e.IsDir && e.Fid == fid, ct).ConfigureAwait(false);
                    if (file != null && !string.IsNullOrEmpty(file.Token))
                    {
                        Log("[诊断·重解析] 命中！路径「" + (rel.Length == 0 ? "(根目录)" : rel)
                            + "」新 token=" + Short(file.Token));
                        return Tuple.Create(stoken, file.Token);
                    }
                    Log("[诊断·重解析] 目录里没有 fid=" + Short(fid) + " 的文件");
                }

                // ④ 最后兜底：路径信息全都不可用 → 在**整个分享里按 fid 广度优先搜索**。
                //    fid 在分享内不变，所以不需要知道它在哪一层。
                //    这比按名字可靠（名字可能被改），但请求数多，只在前面都失败时才走。
                Log("[诊断·重解析] 路径都失败 → 开始全分享按 fid 搜索（可能要几十秒）…");
                var byFid = await client.FindShareFileByFidAsync(pwdId, stoken, fid)
                    .ConfigureAwait(false);
                if (byFid != null && !string.IsNullOrEmpty(byFid.Token))
                {
                    Log("已通过全分享搜索重新定位到源文件（路径信息缺失，按 fid 找到）。");
                    Log("[诊断·重解析] 全分享搜索命中，新 token=" + Short(byFid.Token));
                    return Tuple.Create(stoken, byFid.Token);
                }

                // ⑤ 再按文件名试一次（fid 也可能变了 —— 例如分享者删了重传）
                if (!string.IsNullOrEmpty(fileName))
                {
                    Log("[诊断·重解析] 按 fid 没找到 → 按文件名「" + fileName + "」再试一次…");
                    var byName = await client.FindShareFileByNameAsync(pwdId, stoken, fileName)
                        .ConfigureAwait(false);
                    if (byName != null && !string.IsNullOrEmpty(byName.Token))
                    {
                        Log("已通过文件名在分享中重新定位到源文件。");
                        Log("[诊断·重解析] 按名字命中，新 fid=" + Short(byName.Fid)
                            + " 新 token=" + Short(byName.Token));
                        return Tuple.Create(stoken, byName.Token);
                    }
                }

                // 走到这里 = 分享还在（stoken 拿到了），但文件真的找不到了。
                // 说清楚是哪一种失败，别再让用户猜。
                Log("重新解析分享失败：已拿到新凭证，但在分享里找不到该文件"
                    + "（可能已被分享者删除、移动，或分享内容有变动）。");
                return null;
            }
            catch (Exception ex)
            {
                Log("自动重新解析分享失败: " + ex.Message);
                Log("[诊断·重解析] 异常类型=" + ex.GetType().Name);
                return null;
            }
        }

        /// <summary>
        /// 在分享的一个目录里**逐页**找第一个满足条件的条目，找到即停。
        /// 用一页一页的 <see cref="QuarkClient.ShareDetailPageAsync"/> 而不是整目录
        /// 拉完的 <see cref="QuarkClient.ShareDetailAsync"/>：目标文件常在靠前的页，
        /// 逐页找能少拉几十页（几千项的分享整目录拉完要十几秒）。
        /// </summary>
        private async Task<QuarkFileEntry> FindShareEntryAsync(
            QuarkClient client, string pwdId, string stoken, string pdirFid,
            Func<QuarkFileEntry, bool> pred, CancellationToken ct)
        {
            for (int page = 1; page <= 200; page++)   // 200 页兜底，与 ShareDetailAsync 一致
            {
                ct.ThrowIfCancellationRequested();
                var dir = await client.ShareDetailPageAsync(pwdId, stoken, pdirFid, page).ConfigureAwait(false);
                if (dir == null || dir.Items == null)
                    return null;
                foreach (var e in dir.Items)
                    if (pred(e))
                        return e;
                if (!dir.HasMore)
                    break;
            }
            return null;
        }

        /// <summary>转存后校验：等文件异步落盘，过滤不在目标目录里的 fid（对应原版 _resolve_saved_fids）。</summary>
        private async Task<List<string>> ResolveSavedFidsAsync(string toFid, List<string> fids)
        {
            int[] delays = { 0, 1000, 1500, 2000, 2500, 3000 };
            for (int round = 0; round < delays.Length; round++)
            {
                if (round > 0)
                    await Task.Delay(delays[round]);
                try
                {
                    var items = await _client.ListDirAsync(toFid, 1);
                    var existing = new HashSet<string>(items.Select(it => it.Fid));
                    if (fids.All(f => existing.Contains(f)))
                        return fids;
                }
                catch { }
            }
            return fids;
        }

        private async Task<string> EnsureFolderAsync(string name)
        {
            string path = "/" + name;
            try
            {
                string fid = await _client.PathFidAsync(path);
                if (!string.IsNullOrEmpty(fid))
                    return fid;
                return await _client.MkdirAsync(path);
            }
            catch (Exception ex)
            {
                Log("中转文件夹处理失败: " + ex.Message);
                return null;
            }
        }

        private async void RunJobs(List<DownloadJob> jobs)
        {
            // 🚨 【2026-10-22】async void 的异常**不会**被调用方观察到，
            // 会直接抛到 SynchronizationContext 上（多数情况下等于静默消失）。
            // 原来 try 从 `await scheduler.RunAllAsync` 才开始 ——
            // 它之前的 `new Progress<JobUpdate>(OnJobUpdate)`、`_tasks.Sum(...)`
            // 等语句一旦抛异常，用户看到的就是"点了没反应，日志什么都没有"。
            // 现在整段包起来，并且**先打一行**确认进来了。
            try
            {
                Log("[诊断·RunJobs] 进入，共 " + (jobs == null ? 0 : jobs.Count) + " 个任务");

                // 🚨 【2026-10-22 关键修复】必须**强制回到 UI 线程**再往下走。
                //
                // 【用户实测】续传入队成功后，紧接着：
                //   InvalidOperationException: 调用线程无法访问此对象，
                //   因为另一个线程拥有该对象。
                //   在 UpdatePauseAllButton(Boolean force) 位置 MainWindow.xaml.cs:7046
                //   在 RunJobsCoreAsync 位置 MainWindow.xaml.cs:5729
                // → 任务行停在「排队中」，永远不进「下载中」，速度 0。
                //
                // 【根因】调用方 ResumePendingBatchAsync 里取链用了
                // `.ConfigureAwait(false)`，await 之后**已经不在 UI 线程**。
                // 虽然那里用 `await Dispatcher.InvokeAsync(...)` 切回 UI 线程做了入队，
                // 但 **await 一个 DispatcherOperation 完成后，并不保证后续代码仍在 UI 线程**
                // （当调用方本身没有 SynchronizationContext 时，续体回到线程池）。
                // 于是紧跟着的 `RunJobs(jobs)` 又跑在了线程池线程上，
                // 而它第一件事就是 `UpdatePauseAllButton` → 直接 SetValue 一个
                // DependencyProperty → VerifyAccess 抛异常。
                //
                // 【修法】在入口处显式检查并切回 UI 线程，而不是依赖调用方的上下文。
                // 这样无论谁、在哪个线程调用 RunJobs，行为都一致。
                if (!Dispatcher.CheckAccess())
                {
                    Log("[诊断·RunJobs] 当前不在 UI 线程，已切回 UI 线程后继续。");
                    await Dispatcher.InvokeAsync(() => RunJobs(jobs));
                    return;
                }

                await RunJobsCoreAsync(jobs).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log("下载调度出错: " + ex.GetType().Name + ": " + ex.Message);
                Log("[诊断·RunJobs] 堆栈: " + (ex.StackTrace ?? "").Replace("\r", " ").Replace("\n", " | "));
            }
        }

        /// <summary><see cref="RunJobs"/> 的实际实现（异常统一由外层记日志）。</summary>
        private async Task RunJobsCoreAsync(List<DownloadJob> jobs)
        {
            var progress = new Progress<JobUpdate>(OnJobUpdate);
            // 【关键】把 scheduler 抓到局部变量再 await。
            // 直接写 await _scheduler.RunAllAsync(...) 的话，await 期间若 _scheduler
            // 被替换（见 RecreateScheduler），语义就变成"用可能已变化的字段"，
            // 且和已经开始的这批不是同一个对象 —— 限流账目会错乱。
            // 抓成局部引用后，本批任务从头到尾只用同一个 scheduler，清晰且安全。
            var scheduler = _scheduler;
            _batchRunning = true;

            // 【2026-10-22】本批任务的起点：状态栏要显示"已耗时"。
            // 口径 = **这一批从推送到全部完成**的用时（用户明确要求，不是累加）。
            // 每推一批重新计时；批次结束停表并把结果留在状态栏上。
            _batchStartTime = DateTime.UtcNow;
            _lastBatchElapsed = null;

            // 记下"本批开始时"的累计字节 —— 之后算平均速度时用**增量**，
            // 免得把之前几批已下完的体积也算进去（曾因此显示 200MB/s 的假速度）。
            _batchDoneAtStart = _tasks.Sum(t => t.Done);

            // 新任务已入队 → 合并按钮立刻切成「暂停全部」（不用等第一次进度上报）
            UpdatePauseAllButton(force: true);
            // 立刻开启保活，别等 30 秒的定时器 —— 用户可能点完就去睡觉了
            RefreshSleepGuard();
            try
            {
                await scheduler.RunAllAsync(jobs, progress, CancellationToken.None);
            }
            catch (ObjectDisposedException ex)
            {
                // 兜底：即便将来又有别的路径 Dispose 了 scheduler，也不该让用户
                // 看到"已释放该信号量"这种无信息量的英文异常名。
                Log("下载被中断：下载器被重新初始化（请重新开始该任务，已下载部分会续传）。" + ex.Message);
            }
            catch (Exception ex)
            {
                Log("下载异常: " + ex.Message);
            }
            finally
            {
                _batchRunning = false;

                // 【2026-10-22】本批结束 → 停表，把用时留在状态栏上。
                // 用户要的是"这一批从推送到完成用了多久"，所以到点就定格，
                // 不再继续累加（下一批推送时会重新计时）。
                if (_batchStartTime != null)
                {
                    _lastBatchElapsed = DateTime.UtcNow - _batchStartTime.Value;
                    _batchStartTime = null;
                }
                RefreshTotalStatusBar();
            }
            Log("本批下载结束");

            // 整批结束时在日志里汇总一次（原托盘气泡通知已随"后台功能"一起移除）。
            int ok = 0, fail = 0;
            try
            {
                ok = jobs.Count(j =>
                {
                    var t = _tasks.FirstOrDefault(x => x.Job == j);
                    return t != null && t.State == JobState.Completed;
                });
                fail = jobs.Count - ok;
                if (ok > 0 && fail == 0)
                    Log(string.Format("全部完成：{0} 个文件已下载完成。", ok));
                else if (ok > 0)
                    Log(string.Format("下载结束：{0} 个完成，{1} 个未完成。", ok, fail));
                else if (fail > 0)
                    Log(string.Format("下载失败：{0} 个文件未能完成，请查看任务列表。", fail));
            }
            catch { }

            // 【2026-10-20 新增】全部完成后关机（方案 A）。
            // 必须放在"本批下载结束"之后、清理转存文件之前判断 ——
            // 但真正的关机要**延后到清理完成**，否则可能把清理到一半的文件留在盘上。
            bool shouldShutdown = false;
            if (_settings != null && _settings.ShutdownAfterAll)
            {
                // 只有"全部成功"才关机：有失败时用户多半想留下来看错误，
                // 直接关机会让人完全不知道发生了什么。
                shouldShutdown = fail == 0 && ok > 0 && !_exiting;
                if (!shouldShutdown && fail > 0)
                {
                    Log("有任务未完成，「完成后关机」已跳过（避免关机后看不到失败原因）。");
                }
            }

            await CleanupTransferredAsync();

            if (shouldShutdown)
            {
                PerformShutdown();
            }
        }

        // ---------------- 完成后关机（2026-10-20 新增 · 方案 A） ----------------

        /// <summary>
        /// 关机开关的状态镜像（真正的意图存在 <see cref="AppSettings.ShutdownAfterAll"/>）。
        /// 单独留一个字段是为了让 UI 刷新不必反复读设置对象。
        /// </summary>
        private bool _shutdownAfterAll;

        /// <summary>
        /// 「完成后关机」开关：切换状态、落盘、刷新胶囊外观。
        /// </summary>
        private void OnToggleShutdownAfterAll(object sender, RoutedEventArgs e)
        {
            SetShutdownAfterAll(!_shutdownAfterAll, announce: true);
        }

        /// <summary>
        /// 设置「完成后关机」状态（供开关与恢复设置时共用）。
        /// </summary>
        /// <param name="announce">是否在日志里说明（恢复设置时不需要）。</param>
        private void SetShutdownAfterAll(bool on, bool announce)
        {
            _shutdownAfterAll = on;
            if (_settings != null)
            {
                _settings.ShutdownAfterAll = on;
                SettingsStore.Save(_settings);
            }
            RefreshShutdownPill();

            if (announce)
            {
                Log(on
                    ? "已开启「完成后关机」：全部任务下载完成后将自动关机。"
                    : "已关闭「完成后关机」。");
            }
        }

        /// <summary>
        /// 按当前状态刷新关机胶囊与提示条（对齐方案 A 的视觉）。
        ///
        /// <para>开启时三处联动：
        /// ① 胶囊整块变警告色底、文字转警告色、迷你开关滑到右侧并变警告色；
        /// ② 工具栏下方出现黄色提示条「已开启：全部任务完成后将自动关机（预计 X 后）」；
        /// ③ 提示条右侧「取消」可一键关掉。</para>
        /// </summary>
        private void RefreshShutdownPill()
        {
            try
            {
                if (ShutdownPill == null) return;

                // 用 Tag 驱动 Styles.xaml 里的 DataTrigger（"on" = 开启态）
                ShutdownPill.Tag = _shutdownAfterAll ? "on" : null;

                var warning = (System.Windows.Media.Brush)FindResource("WarningBrush");
                var secondary = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                var tertiary = (System.Windows.Media.Brush)FindResource("TextTertiaryBrush");
                // 描边色统一走 BorderBrushSoft（令牌里只有 Soft/Strong 两档，
                // 此前误写的 "BorderBrush" 并不存在，会在切换到开启态时抛
                // ResourceReferenceKeyNotFoundException → 弹出「未处理错误」）。
                var swOffBorder = (TryFindResource("BorderBrushSoft") as System.Windows.Media.Brush)
                    ?? System.Windows.Media.Brushes.Gray;

                if (ShutdownLabel != null)
                {
                    // 文案与方案 A 一致：关闭「完成后关机」/ 开启「已开启关机」
                    ShutdownLabel.Text = _shutdownAfterAll ? "已开启关机" : "完成后关机";
                    ShutdownLabel.Foreground = _shutdownAfterAll ? warning : secondary;
                }

                // 迷你开关本体：开启 → 警告色底 + 白点靠右；关闭 → 凹槽底 + 灰点靠左
                if (ShutdownSw != null)
                {
                    ShutdownSw.Background = _shutdownAfterAll
                        ? warning
                        : (System.Windows.Media.Brush)FindResource("SurfaceSunkenBrush");
                    ShutdownSw.BorderBrush = _shutdownAfterAll
                        ? warning
                        : swOffBorder;
                }
                if (ShutdownSwKnob != null)
                {
                    ShutdownSwKnob.HorizontalAlignment = _shutdownAfterAll
                        ? System.Windows.HorizontalAlignment.Right
                        : System.Windows.HorizontalAlignment.Left;
                    ShutdownSwKnob.Margin = _shutdownAfterAll
                        ? new Thickness(0, 0, 2, 0)
                        : new Thickness(2, 0, 0, 0);
                    ShutdownSwKnob.Background = _shutdownAfterAll
                        ? System.Windows.Media.Brushes.White
                        : tertiary;
                }

                ShutdownPill.ToolTip = _shutdownAfterAll
                    ? "已开启：全部任务下载完成后自动关机。再点一次取消。"
                    : "全部任务下载完成后自动关机";

                // ---- 提示条 ----
                if (ShutdownNote != null)
                {
                    ShutdownNote.Visibility = _shutdownAfterAll
                        ? System.Windows.Visibility.Visible
                        : System.Windows.Visibility.Collapsed;
                }
                if (_shutdownAfterAll && ShutdownNoteText != null)
                {
                    string eta = FormatRemainingTotalEta();
                    ShutdownNoteText.Text = string.IsNullOrEmpty(eta)
                        ? "已开启：全部任务完成后将自动关机。"
                        : "已开启：全部任务完成后将自动关机（预计 " + eta + " 后）。";
                }
            }
            catch { }
        }

        /// <summary>
        /// 汇总当前所有"下载中"任务的合计剩余时间（用于关机提示条的「预计 X 后」与状态栏的「剩余」）。
        ///
        /// <para>【为什么是"最大剩余"而不是"求和"】多个任务并发下载时它们同时结束，
        /// 总耗时取决于**最慢的那个**，不是各自相加。求和会给出一个虚高的数字。</para>
        /// <para>没有在跑的任务时返回空串（调用方会退化成不带预计时间的文案）。</para>
        ///
        /// <para>【2026-10-22】速度很低时（≤1KB/s）原来直接跳过这个任务，
        /// 于是"只剩 8 秒但此刻速度很慢"会显示成"计算中"—— 秒级的信息恰恰最该给出来。
        /// 现在改成：只要**还有剩余字节**就给出估算，速度低时按最低速算一个上界；
        /// 真的算不出来（速度为 0）才跳过。</para>
        /// </summary>
        private string FormatRemainingTotalEta()
        {
            try
            {
                double maxSeconds = 0;
                bool any = false;
                foreach (var t in _tasks)
                {
                    if (t.State != JobState.Downloading) continue;
                    if (t.Total <= 0 || t.Done >= t.Total) continue;

                    double remain = t.Total - t.Done;
                    if (remain <= 0) continue;

                    double s;
                    if (t.Speed > 1024)
                    {
                        s = remain / t.Speed;
                    }
                    else
                    {
                        // 速度太低：按 1KB/s 算一个保守上界，至少给个数字
                        s = remain / 1024.0;
                    }
                    if (s <= 0) continue;
                    if (s > maxSeconds) maxSeconds = s;
                    any = true;
                }
                return any ? TaskItem.FormatEta(maxSeconds) : "";
            }
            catch { return ""; }
        }

        // ---------------- 运行日志高度（可拖拽 + 记忆 · 2026-10-21） ----------------
        //
        // 【需求】用户反馈日志输出框占用太多空间，要求「改为可以调整高度，并且记忆」，
        //        并且明确指出「拖动内容不应该包括运行日志标题，需要拖动的是下方的输出区」。
        // 【结构】任务页外层 Grid 的 Row4 是具名行 LogRow（**像素高度**），
        //        它内部再分「标题 Auto + LogBox *」——
        //        所以拖大拖小改的是整块日志区，而增量全部落在 LogBox 上，标题高度恒定。
        // 【为什么 LogRow 必须是像素】GridSplitter 的 PreviousAndNext 只对能写死高度的行生效；
        //        Auto 行由内容决定高度，拖动改不动（上一版就是 Auto，实测拖了没反应）。
        // 【落盘】拖完把像素值存进 AppSettings.LogPanelHeight。这个数值是**整块日志区**的
        //        高度（含标题），与用户的直观感受一致，也便于换机器时按比例夹取。

        /// <summary>
        /// 日志区默认总高（含标题行与间距）。用户没拖过时用它。
        /// <para>从旧的 132 降到 120 —— 同时标题行约 24px，日志框实际约 96px，
        /// 与"用户嫌原来太大"的诉求一致，又不至于只剩两行看不清。</para>
        /// </summary>
        private const double DefaultLogPanelHeight = 120;

        /// <summary>日志区最小总高：再小连标题行都放不下，拖到就夹住。</summary>
        private const double MinLogPanelHeight = 70;

        /// <summary>
        /// 日志区最大高度比例（占窗口高度的百分比）。
        /// <para>为什么不给固定像素：窗口在不同屏幕上大小差别很大，
        /// 固定 600px 在 768 高的笔记本上会把任务列表挤没。</para>
        /// </summary>
        private const double MaxLogPanelHeightRatio = 0.62;

        /// <summary>
        /// 把设置里的日志高度恢复到界面上（启动时调用一次）。
        /// 同时做范围校正 —— 上次可能是在大屏幕上拖的，换到小屏就得夹回来。
        /// </summary>
        private void ApplyLogPanelHeight()
        {
            try
            {
                if (LogRow == null) return;

                double h = _settings != null ? _settings.LogPanelHeight : 0;
                if (h <= 0) h = DefaultLogPanelHeight;   // 0 = 老配置/没设过 → 用默认值

                LogRow.Height = new GridLength(ClampLogPanelHeight(h));
            }
            catch { }
        }

        /// <summary>把日志高度夹进 [MinLogPanelHeight, 可用高度 × MaxLogPanelHeightRatio]。</summary>
        private double ClampLogPanelHeight(double h)
        {
            try
            {
                // 任务页整体可用高度大致 = 窗口内容高 - 标题栏/页头等固定开销。
                // 这里不追求精确：用 ActualHeight 估个上限，只为防止"拖到把列表挤没"。
                double avail = ActualHeight > 0 ? ActualHeight : 700;
                double max = Math.Max(MinLogPanelHeight, avail * MaxLogPanelHeightRatio);
                if (h < MinLogPanelHeight) h = MinLogPanelHeight;
                if (h > max) h = max;
            }
            catch { }
            return h;
        }

        /// <summary>
        /// 用户拖完分隔条：把最终高度落盘。
        /// <para>为什么绑 DragCompleted 而不是 ValueChanged：拖动过程中每一帧都落盘
        /// 会把 settings 文件写崩（几百次 IO）；拖完只写一次即可。</para>
        /// </summary>
        private void OnLogSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            try
            {
                if (LogRow == null || _settings == null) return;
                if (LogRow.Height.IsAbsolute)
                {
                    double h = ClampLogPanelHeight(LogRow.Height.Value);
                    // 夹过范围就回写，避免界面显示 900 而设置里存 435 这种不一致
                    LogRow.Height = new GridLength(h);
                    _settings.LogPanelHeight = h;
                    SettingsStore.Save(_settings);
                }
            }
            catch { }
        }

        /// <summary>提示条上的「取消」：关掉完成后关机。</summary>
        private void OnCancelShutdownAfterAll(object sender, RoutedEventArgs e)
        {
            SetShutdownAfterAll(false, announce: true);
        }

        // ---------------- 底部状态栏：总进度 / 总速度 / 剩余（方案 C · 2026-10-21） ----------------
        //
        // 【为什么要有】任务页原来只能逐行看每个文件，没有一个"整体还剩多少"的答案。
        // 【为什么放底部】总进度这类是"背景信息"：想瞟一眼就有，但不该占住视线主路径、
        //   更不该挤压任务列表（顶部仪表条 / 环形卡都验证过，代价是 40~132px 的列表高度）。
        //   底部 32px + 顶边一条 2px 光带，几乎零代价。
        //
        // 【口径】与 LogAggregateProgress / FormatRemainingTotalEta 完全一致，避免两处数字打架：
        //   · 分母 = **未完成**任务的字节总和（已完成的不再算进度，否则永远到不了 100%）；
        //   · 总速度 = 各下载中任务速度求和；
        //   · 剩余时间 = 各下载中任务的**最大**剩余（并发任务同时结束，取最慢那个，不求和）。

        /// <summary>
        /// 状态栏顶边光带的「待算百分比」。
        /// <para>-1 = 已经算好，不用再补；≥0 = 因为容器宽度还没测量出来，等 SizeChanged 补算。</para>
        /// </summary>
        private double _totalPctForLine = -1;

        /// <summary>
        /// 状态栏尺寸变化时补算光带宽度。
        /// <para>首次 RefreshTotalStatusBar 往往发生在布局之前（ActualWidth=0），
        /// 那时算出来的光带宽度是 0 —— 表现为"顶边那条进度光带根本不出现"。
        /// 这里等真实宽度出来后再补一次。</para>
        /// </summary>
        private void OnTotalStatusBarSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_totalPctForLine < 0) return;
            try
            {
                if (TotalProgressLine == null) return;
                var parent = TotalProgressLine.Parent as FrameworkElement;
                double full = (parent != null && parent.ActualWidth > 0)
                    ? parent.ActualWidth
                    : TotalStatusBar.ActualWidth;
                if (full <= 0) return;

                TotalProgressLine.Width = Math.Round(full * _totalPctForLine / 100.0, 1);
            }
            catch { }
        }

        /// <summary>
        /// 刷新底部状态栏。可在任意时刻重复调用（内部分支都已判空）。
        /// 由 <see cref="LogAggregateProgress"/> 按 10 秒节拍驱动，任务增删时也会主动调一次。
        /// </summary>
        private void RefreshTotalStatusBar()
        {
            try
            {
                if (TotalStatusBar == null) return;

                // 参与统计的任务：未完成且不是"取消中"（取消中的不算进度，避免进度回退）
                var pending = _tasks.Where(t => t.State == JobState.Queued
                                             || t.State == JobState.Downloading
                                             || t.State == JobState.Paused).ToList();

                long totalBytes = pending.Sum(t => t.Total);
                long doneBytes = pending.Sum(t => t.Done);
                double speed = _tasks.Where(t => t.State == JobState.Downloading).Sum(t => t.Speed);

                // 【2026-10-22 用户要求】「总速度之类的可以一直保留，
                // 没必要过几秒就消失了」——
                // 没有未完成任务时（全部完成/取消），改用**全部任务**的累计统计，
                // 这样总进度/体积不会退化成"—"。
                if (totalBytes == 0 && _tasks.Count > 0)
                {
                    totalBytes = _tasks.Sum(t => t.Total);
                    doneBytes = _tasks.Sum(t => t.Done);
                }

                // ---- 总进度 ----
                double pct = totalBytes > 0
                    ? Math.Max(0, Math.Min(100, doneBytes * 100.0 / totalBytes))
                    : 0;

                if (TotalPctText != null)
                    TotalPctText.Text = totalBytes > 0 ? pct.ToString("F1") + "%" : "—";

                // 【2026-10-22 用户要求】总进度后面跟一个"已耗时"。
                if (TotalElapsedText != null)
                    TotalElapsedText.Text = FormatElapsedTotal();

                // 光带与进度条：按百分比设宽度。
                // ⚠️ 光带的父级宽度在首次刷新时可能还没测量出来（ActualWidth=0），
                //    直接算会得到 0 宽 —— 表现为"光带根本不出现"。
                //    所以拿不到宽度时**记个待办**，等 SizeChanged 再补一次（见 OnTotalStatusBarSizeChanged）。
                double barTotal = 150;      // TotalBarWrap 的固定宽（XAML 里 Width="150"）
                if (TotalBarFill != null)
                    TotalBarFill.Width = Math.Round(barTotal * pct / 100.0, 1);

                if (TotalProgressLine != null)
                {
                    double full = 0;
                    var parent = TotalProgressLine.Parent as FrameworkElement;
                    if (parent != null && parent.ActualWidth > 0) full = parent.ActualWidth;
                    else if (TotalStatusBar.ActualWidth > 0) full = TotalStatusBar.ActualWidth;

                    if (full > 0)
                    {
                        TotalProgressLine.Width = Math.Round(full * pct / 100.0, 1);
                        _totalPctForLine = -1;      // 已算好，清掉待办
                    }
                    else
                    {
                        // 宽度未知：把百分比存下来，交给 SizeChanged 补算
                        _totalPctForLine = pct;
                    }
                }

                // ---- 总速度 ----
                if (TotalSpeedText != null)
                {
                    if (speed > 1024)
                    {
                        TotalSpeedText.Text = Util.FormatSize((long)speed) + "/s";
                    }
                    else if (pending.Count > 0)
                    {
                        TotalSpeedText.Text = "0 B/s";
                    }
                    else
                    {
                        // 【2026-10-22 用户要求】「总速度之类的可以一直保留，
                        // 没必要过几秒就消失了」——
                        // 全部完成时不再显示"—"，而是把**本批的平均速度**定格留着
                        // （总字节 ÷ 本批耗时），这样还能看出"这批平均有多快"。
                        TotalSpeedText.Text = FormatAverageSpeed();
                    }
                }

                // ---- 剩余时间 ----
                if (TotalEtaText != null)
                {
                    // 直接复用关机提示条那套"取最慢任务"的算法，口径一致。
                    // 三种形态说清当前状态，避免一直挂着"计算中"这种假忙碌：
                    //   · 有下载中任务且速度够 → 「约 9 分」
                    //   · 有下载中任务但速度太低 → 「计算中」（速度回来就会给出数字）
                    //   · 没有在下的任务 → 「已完成」/「已暂停」（保留信息，不显示"—"）
                    string eta = FormatRemainingTotalEta();
                    if (!string.IsNullOrEmpty(eta))
                    {
                        TotalEtaText.Text = "约 " + eta;
                    }
                    else
                    {
                        bool anyRunning = _tasks.Any(t => t.State == JobState.Downloading);
                        if (anyRunning) TotalEtaText.Text = "计算中";
                        else if (pending.Count > 0) TotalEtaText.Text = "已暂停";
                        else TotalEtaText.Text = "已完成";
                    }
                }

                // ---- 已下 / 总量 ----
                if (TotalSizeText != null)
                {
                    // totalBytes/doneBytes 上面已经统一口径
                    // （有未完成任务时算未完成的，没有时算全部任务），
                    // 所以这里直接用，不再分叉。
                    TotalSizeText.Text = totalBytes > 0
                        ? Util.FormatSize(doneBytes) + " / " + Util.FormatSize(totalBytes)
                        : "—";
                }

                // ---- 任务状态点阵 ----
                UpdateTotalDots();
            }
            catch { }
        }

        /// <summary>
        /// 重建状态栏右端的任务点阵：每个任务一个 5px 圆点，颜色区分状态。
        ///
        /// <para>为什么用点阵而不是"3/12 下载中"这种文字：任务数量变化时文字长度会跳，
        /// 而点阵是固定节奏的视觉块，扫一眼就知道"几个在跑、几个完了、几个停了"。</para>
        /// <para>最多画 40 个点：再多就失去"一眼可数"的意义，改成右侧补一个总数。</para>
        /// </summary>
        private void UpdateTotalDots()
        {
            if (TotalDotsPanel == null) return;
            TotalDotsPanel.Children.Clear();

            const int MaxDots = 40;
            var all = _tasks.ToList();
            if (all.Count == 0) return;

            int draw = Math.Min(all.Count, MaxDots);

            // 排序：下载中优先、然后排队、暂停、完成、失败/取消 ——
            // 让"正在动"的点集中在左边，扫视时更容易先看到
            var ordered = all
                .OrderBy(t => t.State == JobState.Downloading ? 0
                            : t.State == JobState.Queued ? 1
                            : t.State == JobState.Paused ? 2
                            : t.State == JobState.Completed ? 3 : 4)
                .Take(draw);

            foreach (var t in ordered)
            {
                Brush brush;
                switch (t.State)
                {
                    case JobState.Downloading: brush = Res("PrimaryBrush"); break;
                    case JobState.Completed: brush = Res("SuccessBrush"); break;
                    case JobState.Paused: brush = Res("WarningBrush"); break;
                    case JobState.Failed: brush = Res("DangerBrush"); break;
                    case JobState.Queued: brush = Res("TextTertiaryBrush"); break;
                    default: brush = Res("BorderBrushStrong"); break;
                }

                TotalDotsPanel.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 5,
                    Height = 5,
                    Margin = new Thickness(0, 0, 3, 0),
                    Fill = brush,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = t.Name + " · " + t.State.ToString(),
                });
            }

            // 超出 MaxDots 时补一个「+N」，避免用户以为点数就是全部
            if (all.Count > draw)
            {
                TotalDotsPanel.Children.Add(new TextBlock
                {
                    Text = "+" + (all.Count - draw),
                    FontSize = 10,
                    Margin = new Thickness(0, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Res("TextTertiaryBrush"),
                });
            }

            Brush Res(string key)
            {
                return (TryFindResource(key) as Brush) ?? System.Windows.Media.Brushes.Gray;
            }
        }

        /// <summary>
        /// 执行关机：先给用户一个**带 15 秒倒计时**的确认框，再调用系统 shutdown。
        ///
        /// <para>【为什么要确认】关机会打断用户正在做的其它事情，而且是不可逆的。
        /// 即便他刚才主动开了开关，真到执行那一刻也应该有一次"临门一脚"的机会 ——
        /// 何况他可能早就忘了自己开过。</para>
        ///
        /// <para>【2026-10-22 用户要求】「应该是倒计时 15s，如果用户没有选取消关机，
        /// 那么结束后自动在 1 分钟内唤起关机」——
        /// 所以这里用<b>倒计时确认框</b>而不是普通 Yes/No：
        /// 用户不操作，15 秒后自动等同点了"确认关机"，接着走
        /// <c>shutdown.exe /s /t 60</c>（系统自己的 60 秒倒计时），
        /// 于是"下载完成 → 15s 倒计时 → 1 分钟内关机"全程无需人工干预。</para>
        ///
        /// <para>【为什么用 shutdown.exe 而不是 WMI】shutdown.exe /s /t 60 让系统自己
        /// 提供倒计时与"取消"入口（shutdown /a），比程序自己 Sleep 60 秒更可靠
        /// —— 程序可能在这 60 秒里被用户关掉。</para>
        /// </summary>
        private void PerformShutdown()
        {
            try
            {
                var ans = AppDialog.ShowWithCountdown(this,
                    "全部任务已下载完成。\n\n"
                    + "你之前开启了「完成后关机」，现在要关机吗？\n"
                    + "（不操作则自动继续；点「取消关机」则本次不关机，开关保持开启）",
                    "完成后关机",
                    "确认关机", "取消关机",
                    System.Windows.MessageBoxImage.Question,
                    ShutdownConfirmSeconds,
                    "将在 {0} 秒后自动关机（想取消请点「取消关机」）…",
                    System.Windows.MessageBoxResult.Yes);

                if (ans != System.Windows.MessageBoxResult.Yes)
                {
                    Log("已取消本次关机（开关保持开启，下次全部完成后仍会再问一次）。");
                    return;
                }

                Log("正在关机：系统将在 60 秒后关闭。要取消请运行 shutdown /a 。");

                // /s = 关机；/t 60 = 60 秒倒计时（给用户反悔的机会）；/c = 注释
                var psi = new System.Diagnostics.ProcessStartInfo("shutdown.exe",
                    "/s /t 60 /c \"鸽子下载自用版：全部任务已完成，即将关机。\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log("关机失败: " + ex.Message);
                AppDialog.Show(this,
                    "调用系统关机失败，请手动关机。\n\n" + ex.Message,
                    "关机失败");
            }
        }

        /// <summary>
        /// 关机确认框的倒计时秒数（2026-10-22 用户要求 15 秒）。
        /// </summary>
        private const int ShutdownConfirmSeconds = 15;

        /// <summary>
        /// 清理「一个」转存来源的中转文件：删文件 + 清回收站。
        /// 供「下载完成即清理单个文件」与「批结束兜底清理」共用。
        /// </summary>
        private async Task CleanupOneAsync(string fid)
        {
            if (string.IsNullOrEmpty(fid) || _client == null || !_settings.AutoClean)
                return;
            var fids = new List<string> { fid };
            await _client.DeleteAsync(fids);
            await _client.PurgeRecycleAsync(fids);
            Log("已清理网盘转存文件 1 个");
        }

        private async Task CleanupTransferredAsync()
        {
            List<CleanupEntry> all;
            lock (_cleanupLock)
            {
                if (_pendingCleanup.Count == 0)
                    return;
                all = new List<CleanupEntry>(_pendingCleanup);
                _pendingCleanup.Clear();
            }

            // 只清「转存」路径写进网盘的中转文件；分享源文件/用户原文件绝不碰。
            var fids = all.Where(e => e.IsTransferred && !string.IsNullOrEmpty(e.Fid))
                         .Select(e => e.Fid).Distinct().ToList();
            if (fids.Count == 0)
                return;

            if (!_settings.AutoClean)
            {
                Log("已保留网盘转存文件 " + fids.Count + " 个（可在设置里关闭自动清理）");
                return;
            }
            if (_client == null)
            {
                Log("未登录（_client 为空），跳过清理 " + fids.Count + " 个转存文件；稍后重新登录会自动清理。");
                return;
            }
            try
            {
                await _client.DeleteAsync(fids);
                await _client.PurgeRecycleAsync(fids);
                Log("已清理网盘转存文件 " + fids.Count + " 个");
            }
            catch (Exception ex)
            {
                // 清理失败不能影响下载，但必须如实告诉用户，否则会以为已删其实没删。
                Log("⚠️ 清理转存文件失败（不影响下载，请稍后到网盘手动删除中转文件夹）："
                    + ex.Message);
            }
        }

        private void OnJobUpdate(JobUpdate u)
        {
            var item = _tasks.FirstOrDefault(t => t.Job == u.Job);
            if (item == null)
                return;

            // ⚠️ 用户已按暂停、但下载器还在收尾时，**忽略核心报来的 Downloading**。
            // 核心的 JobProgressAdapter 永远报 Downloading（它只是转发进度，不关心暂停），
            // 不拦的话刚设好的 Paused 会被下一帧覆盖 → 图标先变三角又变回竖杠。
            // 进度数据本身照常更新（这样能看到速度渐降到 0，文案随之从「暂停中…」变「已暂停」）。
            bool keepPaused = item.PauseRequested && u.State == JobState.Downloading;
            // 【2026-10-21】开启「优先展示未完成」时，任务从"未完成"变成"已完成/失败/取消"
            // （或反过来，比如点 ▶ 续传一个暂停的任务）就需要重排一次 ——
            // 否则它不会自动挪到后面/前面，用户还得手动切一下排序按钮才生效。
            //
            // 【2026-10-22】改成按**关注度分层**（见 TaskAttentionRank）判定是否要重排：
            // 原来的 IsFinished 只有"结束/未结束"两档，而现在是三档
            // （失败/取消 → 未完成 → 已完成）。用两档判定会漏掉
            // 「失败 → 取消」「未完成 → 失败」这类跨层变化 ——
            // 那样任务就会停在一个已经不符合新排序的位置上。
            //
            // 只在分层真正翻转时才重排：进度更新每 500ms 一次，
            // 每次都全表 Move 会让列表抖动且白耗性能。
            int wasRank = TaskAttentionRank(item);
            if (!keepPaused)
            {
                // 【2026-10-22 用户建议】「其实也可以把失败，当做暂停，
                //  这样就可以用全部恢复来恢复失败的文件了」。
                //
                // 【为什么采纳】原来失败任务是终态（JobState.Failed）：
                //   · 只能**逐个**点「重试」，没有批量入口；
                //   · 「全部恢复」只管已暂停的，失败的不在其中 ——
                //     一堆文件失败后要一个个点重试，非常低效。
                // 把它并入「已暂停」这一档后：
                //   · 「全部恢复」能一次把失败的任务全部捞回来重试；
                //   · 失败原因仍保留在 Error 里（界面照常显示「失败: xxx」）；
                //   · 单独点行内 ▶ 也能重试（走 ResumeTask 的重试分支）。
                //
                // ⚠️ 状态仍记为 Failed（不是 Paused）—— 因为"失败"要进历史记录、
                //    也要在任务页显示错误原因；只是把它**同时**纳入「可恢复」这一档。
                //    真正的"可恢复"判定由 CanResumeAfterFailure 负责。
                item.State = u.State;
                // 走到终态/被恢复时清掉暂停标志，避免它一直挂着影响后续状态显示
                if (u.State != JobState.Paused)
                    item.PauseRequested = false;
            }
            if (_settings.TaskUnfinishedFirst && wasRank != TaskAttentionRank(item))
                ApplyTaskOrder();
            item.Error = u.Error ?? "";
            if ((u.State == JobState.Failed || u.State == JobState.Cancelled) && !string.IsNullOrEmpty(u.Error))
            {
                Log("[" + item.Name + "] " + (u.State == JobState.Failed ? "失败" : "取消") + ": " + u.Error);
            }
            if (u.Progress != null)
            {
                item.Percent = u.Progress.Percent;
                // 【2026-10-20 移植】暂停期间**冻结速度**，不接收核心上报的渐降值。
                // 闸门式暂停的语义是"worker 跑完手上那一次读才阻塞"，所以在途请求
                // 还会回来一点数据、核心层会继续上报 EWMA 渐降的速度。
                // UI 若照单全收，用户看到的就是「按了暂停、速度从 20MB/s 慢慢滑到 0」
                // （实测反馈：点了暂停 5 秒后日志还在打「总速度 81.45 KB/s」）→ 以为没生效。
                // 暂停那一刻已把 Speed 置 0（见 OnPauseAll / OnPauseTask），这里再守住，
                // 界面就**一按即**「已暂停 / 0 B/s」。
                // 进度（Percent/Done）照常更新：那是真实已下载字节，且暂停后本来就不再增长。
                if (!keepPaused) item.Speed = u.Progress.Speed;
                item.Done = u.Progress.Done;
                item.Total = u.Progress.Total;
                // 阶段说明（如「正在合并分片」）。非下载态一律清空，
                // 免得完成/失败后还挂着「正在合并分片」这种残留文案。
                item.Phase = u.State == JobState.Downloading ? u.Progress.Phase : null;

                // 【2026-10-22 用户要求】「任务页的总进度和总速度更新太慢，
                // 要确保和任务在下载同步」。
                //
                // 【原来】底部状态栏只由 10 秒一次的定时器刷新 → 总速度/总进度
                // 比单个任务的进度条慢 20 倍，看着像"卡住了不动"。
                // 【现在】进度每次上报（核心层 500ms 一报）就顺手刷一次状态栏 ——
                // 数据源本来就是最新的，只是刷新时机被定时器拖慢了。
                RefreshTotalStatusBar();
            }

            // ---- 历史记录：终态各记一条（只记一次）----
            if (!item.HistoryRecorded
                && (u.State == JobState.Completed || u.State == JobState.Failed || u.State == JobState.Cancelled))
            {
                item.HistoryRecorded = true;
                string status = u.State == JobState.Completed ? "完成"
                    : u.State == JobState.Failed ? "失败" : "已取消";
                long sizeBytes = item.Total > 0 ? item.Total : item.Done;
                HistoryStore.AddHistory(item.Name, Util.FormatSize(sizeBytes), item.Job.Dest, status);
            }

            // ---- 下载完成即清理对应的转存文件 ----
            // 【用户要求】下载完一个文件就立刻删掉网盘里那份中转文件，而不是等整批结束。
            // 这样即使中途关程序/崩溃，也已下载完的文件不会残留中转件。
            // 只清本任务自己的 TransferredFid（转存路径才有；免转存/我的网盘为 null）。
            if (u.State == JobState.Completed && !string.IsNullOrEmpty(item.Job.TransferredFid))
            {
                string fid = item.Job.TransferredFid;
                item.Job.TransferredFid = null;   // 防重复清理
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await CleanupOneAsync(fid);
                        // 从兜底队列里摘掉，避免批结束又删一次（其实再删一次也无害，只是省一次请求）
                        lock (_cleanupLock)
                            _pendingCleanup.RemoveAll(e => e.Fid == fid);
                    }
                    catch { /* 单文件清理失败不阻塞 UI，批结束还有兜底 */ }
                });
            }

            // ---- 未完成任务记录：仅在状态集合发生变化时同步（避免每帧进度都写盘）----
            SyncPending();
            // ---- 任务列表快照：进度/状态变化也记下来（内部 3 秒节流）----
            RequestSaveTaskList();

            // 合并按钮的文案/图标要跟着任务状态走（有在跑的 → 暂停全部；否则 → 恢复全部）
            UpdatePauseAllButton();
        }

        /// <summary>上次写入 pending 时的状态签名，用于避免高频重复写盘。</summary>
        private string _pendingSig;

        // ---- 任务列表快照（2026-10-13）：跨重启保留任务页的记录与进度 ----

        /// <summary>节流写盘用：最多 3 秒落一次（进度每 500ms 就报一次，不能跟着写）。</summary>
        private DispatcherTimer _taskSaveTimer;

        /// <summary>最多保留多少条任务快照（防止一次几千文件的目录把 state 撑爆）。</summary>
        private const int MaxTaskSnapshots = 1000;

        /// <summary>请求把任务页写进 state（节流）。集合变化、状态/进度变化都走这里。</summary>
        private void RequestSaveTaskList()
        {
            if (_taskSaveTimer == null) return;
            if (!_taskSaveTimer.IsEnabled) _taskSaveTimer.Start();
        }

        /// <summary>立刻把任务页整表写进 state.json（OnClosed / 关键操作时用）。</summary>
        private void SaveTaskListNow()
        {
            try
            {
                var list = new List<TaskSnapshot>();
                // 按"加入次序"存（不是界面顺序）：恢复后由 ApplyTaskOrder 按用户偏好重排，
                // 这样"新任务在前/在后"的设置在重启后依然成立。
                foreach (var t in _tasks.OrderBy(x => x.Seq))
                {
                    var job = t.Job;
                    var pi = job == null ? null : job.Pending;
                    list.Add(new TaskSnapshot
                    {
                        Name = t.Name,
                        Dest = job == null ? null : job.Dest,
                        Size = t.Total,
                        Done = t.Done,
                        State = t.State.ToString(),
                        Error = t.Error,
                        Url = job == null ? null : job.Url,
                        Fid = pi == null ? null : pi.Fid,
                        Token = pi == null ? null : pi.Token,
                        FromShare = pi != null && pi.FromShare,
                        PwdId = pi == null ? null : pi.PwdId,
                        Stoken = pi == null ? null : pi.Stoken,
                        Passcode = pi == null ? null : pi.Passcode,
                        RelDir = pi == null ? null : pi.RelDir,
                        GroupName = pi == null ? null : pi.GroupName,
                        // 【2026-10-22】源文件定位信息（转存副本没了时可回退免转存）
                        SourceFid = pi == null ? null : pi.SourceFid,
                        SourceName = pi == null ? null : pi.SourceName,
                        Segmented = true,   // 落盘方式唯一化：恒为独立分片
                        Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    });
                }
                if (list.Count > MaxTaskSnapshots)
                    list.RemoveRange(0, list.Count - MaxTaskSnapshots);
                HistoryStore.SetTasks(list);
            }
            catch { }
        }

        /// <summary>
        /// 启动时把上次的任务列表铺回任务页（含进度）。
        ///
        /// 未完成的一律先显示成「已暂停」并**标记为 Dormant**：它们没有挂到调度器上，
        /// 点 ▶ 才走"重新取链 → 续传"（见 ResumeTask 的 Dormant 分支）。
        /// </summary>
        /// <summary>
        /// 用旧版的「下载历史」（<see cref="HistoryRecord"/>）反推任务快照。
        ///
        /// <para>【为什么需要】任务快照 <c>Tasks</c> 是 2.6 才引入的字段。
        /// v2.3.1 之类的老版本 <c>state.json</c> 里只有
        /// <c>Records</c> / <c>Pending</c> / <c>Shares</c>，没有 <c>Tasks</c> ——
        /// 直接升级会看到「暂无下载任务」，用户以为历史丢了。</para>
        ///
        /// <para>【能补出什么】<c>Records</c> 字段有限（名称 / 路径 / 大小 /
        /// 状态 / 时间），没有 fid 与直链，所以补出来的行：
        /// <list type="bullet">
        /// <item>一律标 <c>Completed</c>：这些文件本来就已经下完躺在磁盘上，
        ///       用户要的是「看得见、能打开、能删除」；</item>
        /// <item>不带 Url / Fid / Token —— 直链早就过期，带着反而会误导
        ///       （点了恢复会拿一条死链去取）；</item>
        /// <item>跳过路径已经不存在的条目：历史里可能有用户早已删掉的文件，
        ///       补成任务行只会让人以为"下了一半"。</item>
        /// </list></para>
        /// </summary>
        private List<TaskSnapshot> BuildTaskSnapshotsFromRecords()
        {
            var list = new List<TaskSnapshot>();
            List<HistoryRecord> records;
            try { records = HistoryStore.GetHistory(); }
            catch { return list; }
            if (records == null || records.Count == 0) return list;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in records)
            {
                if (r == null) continue;
                // 没有本地路径就无法对应到任务行（任务行必须有 Dest）
                if (string.IsNullOrWhiteSpace(r.Path)) continue;
                if (!seen.Add(r.Path)) continue;          // 同一路径只补一条

                long size = ParseSizeText(r.Size);
                // 文件已经不在磁盘上的历史，补成任务行没有意义（点了也打不开），
                // 但它仍留在「下载历史」里，不丢信息。
                bool exists = false;
                try { exists = File.Exists(r.Path); } catch { }
                if (!exists) continue;

                list.Add(new TaskSnapshot
                {
                    Name = r.Name ?? Path.GetFileName(r.Path),
                    Dest = r.Path,
                    Size = size,
                    Done = size,                  // 已下完，进度 100%
                    State = "Completed",
                    Error = "",
                    FromShare = false,
                    Segmented = true,
                    Time = r.Time,
                    GroupName = "",               // 让界面按"未知文件夹"归组
                    RelDir = "",
                    ShareRelDir = "",
                });
            }
            return list;
        }

        /// <summary>
        /// 把历史记录里的显示文本（"586.60 KB" / "1.23 GB"）解析成字节数。
        ///
        /// <para>⚠️ 必须按 <b>1000 进制</b> 解析 —— 与 <see cref="Util.FormatSize"/>
        /// 的输出保持一致（那里用的是 1000 而非 1024，见其注释：
        /// 夸克分享页与本地显示必须同口径，否则用户以为少下了）。</para>
        /// </summary>
        private static long ParseSizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var m = System.Text.RegularExpressions.Regex.Match(
                text.Trim(), @"^\s*([0-9]+(?:\.[0-9]+)?)\s*([KMGTP]?)B?\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return 0;

            double v;
            if (!double.TryParse(m.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out v))
                return 0;

            switch (m.Groups[2].Value.ToUpperInvariant())
            {
                case "K": v *= 1000L; break;
                case "M": v *= 1000L * 1000L; break;
                case "G": v *= 1000L * 1000L * 1000L; break;
                case "T": v *= 1000L * 1000L * 1000L * 1000L; break;
                case "P": v *= 1000L * 1000L * 1000L * 1000L * 1000L; break;
            }
            if (v < 0 || v > long.MaxValue) return 0;
            return (long)v;
        }

        private void RestoreTaskList()
        {
            List<TaskSnapshot> saved;
            try { saved = HistoryStore.GetTasks(); } catch { return; }

            // 🚨 【2026-10-22 向下兼容】任务快照（Tasks）是 2.6 才有的字段。
            //    老版本（如 v2.3.1）的 state.json 里**只有 Records / Pending / Shares**，
            //    没有 Tasks —— 于是升级后任务页直接显示「暂无下载任务」，
            //    用户以为历史全丢了（实测：16 条记录明明在 state.json 里）。
            //
            //    这里做一次一次性回填：Tasks 为空时，用 Records 反推快照。
            //    Records 是「下载历史」，字段比 TaskSnapshot 少（只有名称/路径/大小/
            //    状态/时间），所以补出来的是**只有完成态的行**：
            //      · 已完成 → 直接标 Completed，界面上能看到、能打开、能删除；
            //      · 其它状态 → 没有 fid/直链信息，无法续传，按 Completed 处理更诚实
            //        （它们的文件本来就在磁盘上，用户要的是"看得见"）。
            //    回填后**立刻落盘**，下次启动就走正常路径，不会重复回填。
            if (saved == null || saved.Count == 0)
            {
                saved = BuildTaskSnapshotsFromRecords();
                if (saved != null && saved.Count > 0)
                {
                    try { HistoryStore.SetTasks(saved); } catch { }
                    Log(string.Format(
                        "已从旧版下载历史（{0} 条）恢复任务列表 —— 这些是 v2.8.4 之前版本记录的文件。",
                        saved.Count));
                }
            }

            if (saved == null || saved.Count == 0) return;

            int restored = 0, unfinished = 0;
            foreach (var s in saved)
            {
                if (s == null || string.IsNullOrEmpty(s.Dest)) continue;
                try
                {
                    var job = new DownloadJob
                    {
                        Name = s.Name,
                        Url = s.Url,
                        Dest = s.Dest,
                        Threads = _settings.Threads,
                        WriteMode = WriteMode.SegmentedFiles,   // 落盘方式唯一化
                        PartsRoot = string.IsNullOrWhiteSpace(_settings.PartsRoot) ? null : _settings.PartsRoot,
                        ResumeCannotRefresh = s.FromShare,
                        Pending = new PendingInfo
                        {
                            PwdId = s.PwdId, Stoken = s.Stoken, Passcode = s.Passcode,
                            Fid = s.Fid, Token = s.Token, FromShare = s.FromShare,
                            RelDir = s.RelDir ?? "", Dest = s.Dest, Size = s.Size,
                            GroupName = s.GroupName ?? "",
                            // 【2026-10-22】源文件定位信息（转存副本没了时可回退免转存）
                            SourceFid = s.SourceFid,
                            SourceName = s.SourceName,
                        },
                    };
                    job.LinkRefresher = ct => RefreshLinkAsyncForPending(job, ct);

                    // ⚠️ 【2026-10-22】同一 Dest 只保留**一行**。
                    //    快照里可能出现重复行（早先的"重试"会新建一行、
                    //    老行没被清掉 —— 用户实测：任务列表里同一个文件出现两次，
                    //    删掉一行另一行还在，看着像"删不掉"）。
                    //    这里按 Dest 去重：已存在同 Dest 的行就跳过这条快照。
                    if (_tasks.Any(x => x.Job != null && !string.IsNullOrEmpty(s.Dest)
                        && string.Equals(x.Job.Dest, s.Dest, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var t = new TaskItem(job,
                        string.IsNullOrEmpty(s.Name) ? Path.GetFileName(s.Dest) : s.Name)
                    {
                        Dormant = true,
                    };
                    t.Total = s.Size;
                    t.Done = s.Done;
                    t.Error = s.Error ?? "";

                    JobState st;
                    if (!Enum.TryParse(s.State, out st))
                        st = JobState.Paused;

                    // 【2026-10-22】进度以**磁盘上的 .qmeta** 为准。
                    // 快照里记的 Done 可能不准（例如"失败当暂停"重试时新建的行
                    // 没继承进度 → 写回 0），用户会看到"明明下了 10MB 却显示 0.0%"，
                    // 还会以为分片丢了。续传的真实进度只存在 .qmeta 里。
                    if (!string.IsNullOrEmpty(s.Dest) && st != JobState.Completed)
                    {
                        long onDisk = SegmentedDownloader.ReadDownloadedBytes(
                            s.Dest, string.IsNullOrWhiteSpace(_settings.PartsRoot) ? null : _settings.PartsRoot);
                        if (onDisk > 0)
                        {
                            t.Done = onDisk;
                            if (s.Size > 0)
                                t.Percent = Math.Min(99.9, onDisk * 100.0 / s.Size);
                        }
                    }

                    if (st == JobState.Completed)
                    {
                        t.Percent = 100;
                    }
                    else if (st == JobState.Failed || st == JobState.Cancelled)
                    {
                        t.Percent = s.Size > 0 ? Math.Min(100.0, t.Done * 100.0 / s.Size) : 0;
                    }
                    else
                    {
                        // 上次是 排队/下载中/暂停/取消中 → 统一回到「已暂停」
                        st = JobState.Paused;
                        t.Percent = s.Size > 0 ? Math.Min(99.9, t.Done * 100.0 / s.Size) : 0;
                        unfinished++;
                    }
                    t.State = st;
                    _tasks.Add(t);
                    restored++;
                }
                catch { }
            }

            if (restored == 0) return;
            ApplyTaskOrder();   // 按「新任务在前 / 在后」的偏好重排
            Log(string.Format("已恢复上次的任务记录: {0} 项{1}", restored,
                unfinished > 0 ? "（其中 " + unfinished + " 个未完成，点 ▶ 可续传）" : ""));
        }

        /// <summary>
        /// 把当前"未完成"的任务（排队/下载/暂停）写进持久化记录，
        /// 已完成的从记录里移除。供下次启动提示"有 N 个未完成任务"。
        ///
        /// 节流：只有"未完成任务的名字 + 状态"组合变了才真正写盘 ——
        /// OnJobUpdate 每 500ms 就来一次，无条件写盘等于持续小额 IO。
        /// </summary>
        private void SyncPending()
        {
            try
            {
                var sb = new StringBuilder();
                var list = new List<PendingTask>();
                foreach (var t in _tasks)
                {
                    // 快照恢复出来的行（Dormant）**不再写回 Pending**：
                    // 它们本来就直接来自 Pending/上次快照，写回去只会把"未完成任务"弹窗
                    // 再撑一遍（用户会看到列表里已经有了、弹窗又问一次）。
                    if (t.Dormant)
                        continue;
                    if (t.State == JobState.Queued || t.State == JobState.Downloading
                        || t.State == JobState.Paused)
                    {
                        sb.Append(t.Name).Append('|').Append(t.State).Append(';');
                        var pi = t.Job.Pending;
                        list.Add(new PendingTask
                        {
                            Name = t.Name,
                            Dest = t.Job.Dest,
                            Url = t.Job.Url,
                            Fid = pi?.Fid,
                            Token = pi?.Token,
                            FromShare = pi?.FromShare ?? false,
                            Size = t.Total,
                            StreamPlay = true,   // 落盘方式唯一化：恒可边下边播
                            StreamPlayMode = true,   // 落盘方式唯一化：恒可边下边播
                            Status = t.StatusText,
                            // ---- 续传所需（重启后重取链）----
                            PwdId = pi?.PwdId,
                            Stoken = pi?.Stoken,
                            Passcode = pi?.Passcode,
                            RelDir = pi?.RelDir,
                            GroupName = pi?.GroupName,
                            // 【2026-10-22】源文件定位信息（转存副本没了时可回退免转存）
                            SourceFid = pi?.SourceFid,
                            SourceName = pi?.SourceName,
                        });
                    }
                }
                string sig = sb.ToString();
                if (sig == _pendingSig)
                    return;
                _pendingSig = sig;
                HistoryStore.SetPending(list);
            }
            catch { }
        }

        // ---------------- 任务管理 ----------------

        /// <summary>
        /// 托盘菜单里的「暂停全部 / 恢复全部」：按当前状态自动选一个方向。
        /// </summary>
        /// <remarks>
        /// 【2026-10-20 改】任务页工具栏已经拆成**两个独立按钮**（方案 B 分段胶囊），
        /// 各自只管一个方向。这个方法保留给托盘右键菜单用（那里只有一行，没法放两个）。
        /// </remarks>
        private void OnPauseAllOrResume(object sender, RoutedEventArgs e)
        {
            bool anyRunning = _tasks.Any(t => t.State == JobState.Downloading || t.State == JobState.Queued);
            if (anyRunning) OnPauseAll(sender, e);
            else OnResumeAll(sender, e);
        }

        /// <summary>「全部暂停」：只暂停，不碰已暂停的（分段胶囊左半）。</summary>
        private void OnPauseAll(object sender, RoutedEventArgs e)
        {
            int n = 0;
            foreach (var t in _tasks)
            {
                if (t.State == JobState.Downloading || t.State == JobState.Queued)
                {
                    t.Job.PauseJob();
                    // 与单个暂停同理：先置标志再改状态，否则进度上报会把 Paused 覆盖掉
                    t.PauseRequested = true;
                    t.State = JobState.Paused;
                    // 【2026-10-20 移植】立刻把速度归零。
                    // 闸门式暂停的语义是"worker 跑完手上那一次读才阻塞"，在途请求还会
                    // 回来一点数据、核心层会继续上报 EWMA 渐降的速度。UI 若照单全收，
                    // 用户看到的就是「按了暂停、速度慢慢滑到 0」，以为没生效。
                    // 置 0 后配合 OnJobUpdate 里的 keepPaused 冻结，界面**一按就**是 0 B/s。
                    t.Speed = 0;
                    n++;
                }
            }
            if (n == 0)
            {
                Log("没有正在下载 / 排队的任务。");
                return;
            }
            Log("已暂停 " + n + " 个任务。");
            SyncPending();
            UpdatePauseAllButton(force: true);
        }

        /// <summary>
        /// 「全部恢复」：恢复已暂停的**以及失败/已取消的**（分段胶囊右半）。
        /// Dormant（上次运行恢复出来的占位行）单独走**一次批量续传**，
        /// 避免 N 个任务各自并发取链、把请求量放大 N 倍。
        ///
        /// <para>【2026-10-22 用户建议】「其实也可以把失败，当做暂停，
        /// 这样就可以用全部恢复来恢复失败的文件了」——
        /// 所以这里把 Failed / Cancelled 也纳入恢复范围：</para>
        /// <list type="bullet">
        /// <item>本次会话内的失败/取消 → <b>重置令牌后重新入队</b>（分片还在，续传）。</item>
        /// <item>Dormant 的失败/取消 → 走**批量重新取链续传**（与暂停行同一路径）。</item>
        /// </list>
        /// </summary>
        private void OnResumeAll(object sender, RoutedEventArgs e)
        {
            int n = 0;
            int retryFailed = 0;

            foreach (var t in _tasks)
            {
                if (t.Dormant) continue;   // Dormant 单独处理（见下）

                if (t.State == JobState.Paused)
                {
                    t.PauseRequested = false;
                    t.Job.ResumeJob();
                    t.State = JobState.Downloading;
                    n++;
                }
                else if (t.State == JobState.Failed || t.State == JobState.Cancelled)
                {
                    // 【2026-10-22】失败/取消 → 当作暂停来恢复：重置令牌后重新入队。
                    // 旧的 Cancel 令牌已触发且不可复用，必须换新的才能再跑。
                    t.Job.ResetForRetry();
                    t.Error = "";
                    t.HistoryRecorded = false;   // 允许重试成功后再记一条历史
                    t.PauseRequested = false;
                    t.Speed = 0;
                    t.State = JobState.Queued;   // 先挂牌「排队中」，RunJobs 会改为「下载中」
                    retryFailed++;
                }
            }

            // 【2026-10-22】失败/取消的任务统一走「重新取链 → 续传」路径，
            // 而不是直接重跑旧 job。
            //
            // 【为什么不直接重跑旧 job】旧 job 的直链**带时效签名**，早就过期了；
            // 而重新取链时能顺带触发「中转副本没了 → 回退到分享源文件」的自愈
            // （见 TryFallbackToShareSourceAsync）。分片在磁盘上，新 job 用同一个
            // Dest，自然续传，不用重下。
            var retryPending = new List<PendingTask>();
            foreach (var t in _tasks)
            {
                if (t.Dormant) continue;
                if (t.State != JobState.Queued) continue;   // 只处理刚被置为 Queued 的
                var pend = BuildPendingTask(t);
                if (pend != null) retryPending.Add(pend);
            }

            var dormantPending = new List<PendingTask>();
            foreach (var t in _tasks)
            {
                bool dormantHit = t.Dormant
                    && (t.State == JobState.Paused || t.State == JobState.Failed || t.State == JobState.Cancelled);
                if (!dormantHit) continue;
                var pend = BuildPendingTask(t);
                if (pend != null) dormantPending.Add(pend);
            }

            if (n == 0 && retryFailed == 0 && dormantPending.Count == 0)
            {
                Log("没有可恢复的任务。");
                return;
            }
            if (n > 0) Log("已恢复 " + n + " 个任务。");
            if (retryFailed > 0) Log("正在重试 " + retryFailed + " 个失败/取消的任务（已下载部分会续传）…");
            if (dormantPending.Count > 0)
            {
                Log("正在为 " + dormantPending.Count + " 个未完成任务重新取链…");
                _ = ResumePendingBatchAsync(dormantPending);
            }
            if (retryPending.Count > 0)
            {
                _ = ResumePendingBatchAsync(retryPending);
            }
            SyncPending();
            UpdatePauseAllButton(force: true);
        }

        /// <summary>上次刷新「暂停全部/恢复全部」按钮时用的状态签名，避免每帧重建 Content。</summary>
        private string _pauseAllSig;

        /// <summary>
        /// 刷新合并按钮的文案与图标：有任务在跑 → 「⏸ 暂停全部」；否则有暂停的 → 「▶ 恢复全部」；
        /// 都没有 → 置灰（点了也没意义）。
        /// </summary>
        /// <param name="force">true = 忽略签名缓存强制重建（按钮刚被点击时用）。</param>
        /// <summary>
        /// 刷新分段胶囊上的两个数量与可用状态（2026-10-20 改为方案 B）。
        /// 左半「全部暂停」只统计 下载中/排队；右半「全部恢复」统计 已暂停 + **失败/已取消**。
        ///
        /// <para>【2026-10-22】右半把 Failed / Cancelled 也算进去 ——
        /// 按用户建议「把失败当做暂停」，这样「全部恢复」能一次重试所有失败任务。</para>
        /// </summary>
        /// <param name="force">true = 忽略签名缓存强制刷新（按钮刚被点击时用）。</param>
        private void UpdatePauseAllButton(bool force = false)
        {
            if (PauseAllBtn == null) return;

            int running = _tasks.Count(t => t.State == JobState.Downloading || t.State == JobState.Queued);
            int paused = _tasks.Count(t => t.State == JobState.Paused);
            int failed = _tasks.Count(t => t.State == JobState.Failed || t.State == JobState.Cancelled);
            int resumable = paused + failed;   // 「全部恢复」能救回的数量
            string sig = running + "/" + resumable + "/" + failed;
            if (!force && sig == _pauseAllSig) return;
            _pauseAllSig = sig;

            if (PauseAllCount != null)
            {
                PauseAllCount.Text = running.ToString();
                PauseAllCount.Opacity = running > 0 ? 1.0 : 0.55;
            }
            if (ResumeAllCount != null)
            {
                ResumeAllCount.Text = resumable.ToString();
                ResumeAllCount.Opacity = resumable > 0 ? 1.0 : 0.55;
            }

            PauseAllBtn.IsEnabled = running > 0;
            if (ResumeAllBtn != null) ResumeAllBtn.IsEnabled = resumable > 0;

            PauseAllBtn.ToolTip = running > 0
                ? "暂停所有正在下载 / 排队的任务（" + running + " 个）"
                : "当前没有正在下载 / 排队的任务";
            if (ResumeAllBtn != null)
            {
                ResumeAllBtn.ToolTip = resumable > 0
                    ? "恢复所有已暂停的任务（" + paused + " 个）"
                      + (failed > 0 ? "，并重试失败/取消的任务（" + failed + " 个）" : "")
                    : "当前没有已暂停或失败的任务";
            }
        }

        /// <summary>
        /// 复查「保活」：有任务在**活跃地**下载就阻止系统空闲睡眠，否则释放。
        ///
        /// 【为什么需要】用户反馈「中午开着任务后关显示器离开了，下午再看任务全部停了」——
        /// 系统按空闲计划睡眠会掐断所有连接，任务自然全灭。
        ///
        /// 【活跃的判据】状态是 Downloading/Queued，**且**满足下面任一条：
        ///   · 最近 <see cref="SleepGuardStaleMinutes"/> 分钟内进度**真的前进过**；
        ///   · 正在合并分片（<see cref="TaskItem.Phase"/> 非空）——
        ///     合并期间 `Done` 不再增长，但它确实在干活，**不能当卡死**。
        ///
        /// 【为什么要"无进展超时"这一条】任务**卡死**时（worker 卡在掐不断的调用里、
        /// `Task.WhenAll` 永不返回）状态会永远停在 Downloading。只看状态就会一直保活、
        /// 电脑整夜不睡。加了超时后卡死也能正常睡 —— 未完成的任务下次启动会由
        /// 「未完成的任务」对话框提醒用户（`SyncPending` 收 Downloading 状态）。
        ///
        /// ⚠️ 必须在 UI 线程调用（<see cref="SleepGuard"/> 是线程级的）。
        /// </summary>
        private void RefreshSleepGuard()
        {
            try
            {
                var now = DateTime.UtcNow;
                var staleAfter = TimeSpan.FromMinutes(SleepGuardStaleMinutes);

                bool active = _tasks.Any(t =>
                    (t.State == JobState.Downloading || t.State == JobState.Queued)
                    && (!string.IsNullOrEmpty(t.Phase) || (now - t.LastProgressUtc) < staleAfter));

                SleepGuard.Set(active);
            }
            catch { /* 保活失败绝不能影响下载本身 */ }
        }

        private void OnClearDone(object sender, RoutedEventArgs e)
        {
            var done = _tasks.Where(t => t.State == JobState.Completed || t.State == JobState.Failed
                                      || t.State == JobState.Cancelled || t.State == JobState.Cancelling)
                             .ToList();
            if (done.Count == 0)
            {
                Log("没有已完成的记录。");
                return;
            }

            // 【2026-10-13】用户要求「清空已完成也和 z 删除类似，需要选是仅删除记录还是包括文件」。
            var choice = AskTaskDeleteChoice(done);
            if (choice == TaskDeleteChoice.Cancel) return;

            if (choice == TaskDeleteChoice.WithFiles) DeleteTaskLocals(done);
            else RemoveTaskRecords(done, askRunning: false);

            SyncPending();
            UpdatePauseAllButton(force: true);
        }

        // ---------------- 任务列表排序：新任务在前 / 在后（2026-10-13 新增） ----------------
        //
        // 【原行为】新任务一律 `_tasks.Add(...)` 追加到列表**末尾** —— 于是最新加入的
        //   任务被一堆早已下完的旧任务压在下面，要一直往下滚才能看到（用户反馈）。
        // 【现行为】工具栏右上角一颗切换按钮：默认仍是"新任务在后"（保持旧行为），
        //   点一下就变成"新任务在前"（时间倒序），选择会落盘，下次启动依然生效。

        /// <summary>
        /// 把新任务按当前排序偏好放进列表。
        /// ⚠️ 所有"加入任务"的地方都必须走这里，不要再直接 <c>_tasks.Add</c>，
        ///    否则排序偏好会被悄悄绕过去（列表里新旧任务混在一起）。
        /// </summary>
        private void AddTaskItem(TaskItem t)
        {
            if (_settings.TaskNewestFirst)
                _tasks.Insert(0, t);
            else
                _tasks.Add(t);

            // 【2026-10-21】入队后立刻刷新底部状态栏，否则新任务要等
            // 下一个 10 秒节拍才出现在点阵里，看着像"没加进去"。
            RefreshTotalStatusBar();
        }

        /// <summary>
        /// 按当前排序偏好重排**已有**任务（点击切换排序时调用）。
        /// 用 <see cref="ObservableCollection{T}.Move"/> 而不是 Clear+重填：
        /// Move 只挪位置、不销毁容器，选中项和滚动位置都不会乱跳。
        ///
        /// <para>【2026-10-21 新增「优先展示未完成」】用户反馈「假如有多个文件在文件夹下，
        /// 应该优先展示未完成的文件，因为有时候需要滚动到底部才发现」。</para>
        /// <para>实现要点：这里排的是 <c>_tasks</c> 这个**唯一真源**的顺序，
        /// 分组视图的分组内顺序就是跟着它来的，所以只要在这里把"未完成"提到前面，
        /// 每个文件夹卡片内部的顺序也就跟着变了 —— 不需要另写一套分组内排序。</para>
        /// <para>为什么用稳定排序（OrderBy 本身就是稳定的）：先按"关注度分层"分组，
        /// 再在组内按时间偏好排，等于给原有顺序加了一个主键；
        /// 用户原来的"新任务在前 / 在后"偏好依然在组内生效，不会被打乱。</para>
        /// </summary>
        private void ApplyTaskOrder()
        {
            IEnumerable<TaskItem> seq = _settings.TaskNewestFirst
                ? _tasks.OrderByDescending(t => t.Seq)
                : _tasks.OrderBy(t => t.Seq);

            // 关注度优先：等于是加在时间序**之上**的第一排序键
            if (_settings.TaskUnfinishedFirst)
                seq = seq.OrderBy(t => TaskAttentionRank(t));

            var ordered = seq.ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                int cur = _tasks.IndexOf(ordered[i]);
                if (cur != i) _tasks.Move(cur, i);
            }
        }

        /// <summary>
        /// 任务的"关注度分层"——「优先展示未完成、失败的任务」用的第一排序键。
        ///
        /// <para>【为什么分三层而不是两层】用户反馈（2026-10-22）：
        /// 「任务页同样应该优先展示未完成、**失败**的任务，方便重试
        /// （不然每次要滚动半天）」。原来的两层分法把"失败"和"已完成"归为一类
        /// （见 <see cref="IsFinished"/>），于是**失败任务会沉到最底下** ——
        /// 恰恰是最需要用户点重试的那些。</para>
        ///
        /// <para>三层：</para>
        /// <list type="bullet">
        /// <item><b>0 = 失败 / 已取消</b>：需要用户处理（重试 / 排查），最该被看到。
        ///       已取消也算 —— 它同样处于"没下完、可以重来"的状态。</item>
        /// <item><b>1 = 未完成</b>：下载中 / 排队 / 暂停 / 取消中 —— 正在进行或可继续。</item>
        /// <item><b>2 = 已完成</b>：只剩记录价值，垫底。</item>
        /// </list>
        ///
        /// <para>组内仍按用户原有的"新任务在前 / 在后"偏好排（OrderBy 稳定）。</para>
        /// </summary>
        private static int TaskAttentionRank(TaskItem t)
        {
            if (t == null) return 2;
            switch (t.State)
            {
                // 失败 / 已取消：需要用户重试或排查 —— 排最前
                case JobState.Failed:
                case JobState.Cancelled:
                    return 0;
                // 未完成：下载中 / 排队 / 暂停 / 取消中
                case JobState.Queued:
                case JobState.Downloading:
                case JobState.Paused:
                case JobState.Cancelling:
                    return 1;
                // 已完成
                default:
                    return 2;
            }
        }

        /// <summary>
        /// 「这个任务算不算已经结束」—— 供「这个任务还需要用户关注吗」这类判定用。
        /// <para>已结束：完成 / 失败 / 已取消。其余（下载中、排队、暂停、取消中）都算未完成。</para>
        ///
        /// <para>⚠️ 【2026-10-22】**排序位置**不再由这个二分判定决定 ——
        /// 位置改由 <see cref="TaskAttentionRank"/> 的三层负责（失败要单独排最前）。
        /// 本方法保留给"是否还需要用户关注"这类语义使用。</para>
        /// </summary>
        private static bool IsFinished(TaskItem t)
        {
            if (t == null) return true;
            return t.State == JobState.Completed
                || t.State == JobState.Failed
                || t.State == JobState.Cancelled;
        }

        private void OnToggleTaskSort(object sender, RoutedEventArgs e)
        {
            _settings.TaskNewestFirst = !_settings.TaskNewestFirst;
            SettingsStore.Save(_settings);
            ApplyTaskOrder();
            UpdateTaskSortButton();
            Log(_settings.TaskNewestFirst
                ? "任务列表已切换：新任务排在最前面（按最新时间）"
                : "任务列表已切换：新任务排在已有任务后面（加入顺序）");
        }

        /// <summary>同步排序按钮的文案 / 图标（图标在文字右边）/ 悬停提示。</summary>
        private void UpdateTaskSortButton()
        {
            bool first = _settings.TaskNewestFirst;
            TaskSortText.Text = first ? "新任务在前" : "新任务在后";
            // TryFindResource：拿不到就保持 XAML 里 StaticResource 给的默认图标，
            // 不要为了换个图标把整个启动流程炸掉。
            var geo = TryFindResource(first ? "IconSortDesc" : "IconSortAsc") as Geometry;
            if (geo != null) TaskSortIcon.Data = geo;
            TaskSortBtn.ToolTip = first
                ? "当前：新任务排在最前面（点击改为排在已有任务后面）"
                : "当前：新任务排在已有任务后面（点击改为排在最前面）";
        }

        // ---------------- 任务列表：文件夹分组 / 平铺（2026-10-13 新增） ----------------
        //
        // 用户要求：「选择文件夹下载时，任务展示可以选择『用文件夹名称展示目录下的文件』
        // 还是『展示一个个文件』，并且记忆选择；默认展开，可以点击收起」。
        //
        // 实现走 WPF 自带的 CollectionView 分组（GroupDescriptions）而不是自己拍平列表：
        //   · TaskList.SelectedItems 里**仍然是 TaskItem**，右键菜单 / 暂停 / 删除等
        //     全部现有逻辑一行都不用改；
        //   · 分组头是独立的 GroupItem，天然不可被选中，不会污染多选；
        //   · 收起/展开交给分组头里的 Expander，默认 IsExpanded=True。

        /// <summary>按当前设置增删分组（Grouped = 按 <see cref="TaskItem.GroupName"/> 分组）。</summary>
        private void ApplyTaskGrouping()
        {
            if (_taskView == null) return;
            using (_taskView.DeferRefresh())
            {
                _taskView.GroupDescriptions.Clear();
                if (_settings.TaskView == TaskViewMode.Grouped)
                    _taskView.GroupDescriptions.Add(
                        new PropertyGroupDescription(nameof(TaskItem.GroupName)));
            }

            // 分组 = 文件夹卡片里的平铺行；平铺 = 一行一张小卡片。
            var styleKey = _settings.TaskView == TaskViewMode.Grouped ? "TaskRowItem" : "TaskListItem";
            if (TryFindResource(styleKey) is Style rowStyle)
                TaskList.ItemContainerStyle = rowStyle;

            // 【2026-10-22】分组结构变了（切视图 / 重新分组）→ 容器会重新生成。
            // 展开状态**不需要**在这里补救：每个新容器生成时都会在
            // OnTaskGroupItemLoaded 里自己读配置摆正（见该方法的注释）。
            // 这里只同步一次"已经存在的"容器，作为快速路径。
            RestoreGroupExpandedStates();
        }

        /// <summary>同步「展示方式」按钮的文案 / 图标（图标在文字右边）。</summary>
        private void UpdateTaskViewButton()
        {
            bool grouped = _settings.TaskView == TaskViewMode.Grouped;
            TaskViewText.Text = grouped ? "文件夹分组" : "平铺";
            var geo = TryFindResource(grouped ? "IconFolder" : "IconList") as Geometry;
            if (geo != null) TaskViewIcon.Data = geo;
            TaskViewBtn.ToolTip = grouped
                ? "当前：按文件夹分组（点击改为平铺一个个文件）"
                : "当前：平铺一个个文件（点击改为按文件夹分组）";
        }

        private void OnToggleTaskView(object sender, RoutedEventArgs e)
        {
            _settings.TaskView = _settings.TaskView == TaskViewMode.Grouped
                ? TaskViewMode.Flat : TaskViewMode.Grouped;
            SettingsStore.Save(_settings);
            ApplyTaskGrouping();
            UpdateTaskViewButton();
            Log(_settings.TaskView == TaskViewMode.Grouped
                ? "任务列表已切换：按文件夹分组（点分组头可收起）"
                : "任务列表已切换：平铺显示一个个文件");
        }

        // ---------------- 任务列表：全部收起 / 全部展开 + 展开状态记忆（2026-10-22） ----------------
        //
        // 用户要求：
        //   ① 任务页加一个「全部收起 / 全部展开」按钮；
        //   ② 记忆每个文件夹分组的展开 / 收起状态（重启后保持）。
        //
        // 实现要点：
        //   · 分组头里的 Expander 名字是 GroupExpander（在 GroupItem 模板里），
        //     通过 GroupItem.Template.FindName 拿到；
        //   · Expander.IsExpanded 变化时把分组名记进「已收起」列表并落盘；
        //   · 新出现的分组默认展开 —— 所以存的是「收起的」而不是「展开的」。

        /// <summary>
        /// 分组名之间的分隔符：**换行符**（书名里可能有逗号/竖线，用它们会冲突）。
        ///
        /// <para>🚨 <b>【2026-10-22 严重事故】原来的分隔符是 <c>'\u0001'</c>，导致整份配置报废。</b>
        /// XmlSerializer 把控制字符写成字符引用 <c>&amp;#x1;</c>，而
        /// <b>XML 1.0 明确规定 C0 控制字符（U+0000–U+001F，除 \t \n \r）不得以任何形式出现</b>
        /// —— 连字符引用也被禁止。于是写出来的 config.xml 直接变成非法 XML：</para>
        /// <code>
        /// &lt;TaskCollapsedGroups&gt;读书小站…（2000册）&amp;#x1;科学休息…&lt;/TaskCollapsedGroups&gt;
        /// </code>
        /// <para>后果：下次启动 <c>SettingsStore.Load()</c> 反序列化抛 ParseError →
        /// <c>LoadedExistingConfig = false</c> → 程序拿到一份空 AppSettings →
        /// 表现为「登录 Cookie 没了（未登录）、主题变回亮色（Theme 退回 System）、
        /// 下载目录回到默认值」。用户看到的正是这个现象。</para>
        ///
        /// <para>所以分隔符只能用 <b>XML 合法</b>的字符。换行符（U+000A）合法，
        /// 且不可能出现在 Windows 文件夹名里 —— 既有天然唯一性，又不会损坏配置。</para>
        /// </summary>
        private const char GroupSep = '\n';

        /// <summary>「全部收起 / 全部展开」按钮的当前模式：true = 全部收起。</summary>
        private bool _collapseAllMode = true;

        /// <summary>正在恢复展开状态（恢复期间不记录，避免把默认值写进配置）。</summary>
        private bool _restoringGroupState;

        /// <summary>
        /// 【已废弃】分组容器生成后用于恢复展开状态的节流定时器。
        /// 现在改由 GroupItem 的 Loaded 事件驱动（见 OnTaskGroupItemLoaded），
        /// 不再需要"等容器出现"的轮询。字段保留只为不惊动其余引用点。
        /// </summary>
        private System.Windows.Threading.DispatcherTimer _groupStateTimer;

        /// <summary>
        /// 拿到某个 GroupItem 里的 Expander（分组头那个）。
        /// 模板里给它起了名字 GroupExpander，FindName 走的是模板的 namescope。
        /// </summary>
        private Expander FindGroupExpander(GroupItem gi)
        {
            try
            {
                if (gi == null) return null;
                var tpl = gi.Template;
                if (tpl == null) return null;
                var exp = tpl.FindName("GroupExpander", gi) as Expander;
                if (exp != null && !_expanderWatched.Contains(exp))
                {
                    // ⚠️ Expander 没有 IsExpandedChanged 事件（那是 Selector 的），
                    //    但 IsExpanded 是依赖属性 —— 用 DependencyPropertyDescriptor
                    //    监听它的值变化。
                    try
                    {
                        var dpd = System.ComponentModel.DependencyPropertyDescriptor
                            .FromProperty(Expander.IsExpandedProperty, typeof(Expander));
                        dpd.AddValueChanged(exp, OnGroupExpanderIsExpandedChanged);
                        _expanderWatched.Add(exp);
                    }
                    catch { }
                }
                return exp;
            }
            catch { return null; }
        }

        /// <summary>已经挂上 IsExpanded 监听的 Expander（避免重复挂）。</summary>
        private readonly HashSet<Expander> _expanderWatched = new HashSet<Expander>();

        /// <summary>
        /// 当前任务列表里**已经生成出来**的 GroupItem 容器。
        ///
        /// <para>🚨 【2026-10-22 血泪史，四次才修对】</para>
        /// <list type="number">
        /// <item>遍历可视树 → 只找到 3 个；</item>
        /// <item><c>ContainerFromIndex</c> → 返回 0 个（日志"共有 11 个分组"）；</item>
        /// <item>关闭虚拟化（<c>IsVirtualizing="False"</c>）→ 容器确实全生成了，
        ///       但**布局崩了**：202 个任务只渲染 67 个、展开的分组后半段空白、
        ///       63 个文件的文件夹只显示前几个（用户实测）。原因是
        ///       <c>PlainListBox</c> 的 <c>CanContentScroll="True"</c>（逻辑滚动）
        ///       跟非虚拟化面板组合会测量异常；</item>
        /// <item>【定稿】虚拟化照旧，本方法**只返回当前已生成的容器**（可能不全），
        ///       而"全部分组"的正确性**不依赖它** ——
        ///       由 <see cref="OnTaskGroupItemLoaded"/> 保证（每个容器生成时自己读配置）。
        ///       本方法只用于"批量操作后立刻同步屏幕上那几个"，拿不全也没关系。</item>
        /// </list>
        /// </summary>
        private List<GroupItem> EnumerateGroupItems()
        {
            var list = new List<GroupItem>();
            try
            {
                if (TaskList == null) return list;
                CollectVisualChildren(TaskList, list);
            }
            catch { }
            return list;
        }

        /// <summary>深度遍历可视树，收集所有 <see cref="GroupItem"/>。</summary>
        private static void CollectVisualChildren(DependencyObject root, List<GroupItem> found)
        {
            if (root == null) return;
            int n;
            try { n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); }
            catch { return; }

            for (int i = 0; i < n; i++)
            {
                DependencyObject child;
                try { child = System.Windows.Media.VisualTreeHelper.GetChild(root, i); }
                catch { continue; }
                if (child == null) continue;

                var gi = child as GroupItem;
                if (gi != null && !found.Contains(gi)) found.Add(gi);
                CollectVisualChildren(child, found);
            }
        }

        /// <summary>分组头是否处于收起状态（Expander.IsExpanded == false）。</summary>
        private bool IsGroupCollapsed(GroupItem gi)
        {
            var exp = FindGroupExpander(gi);
            return exp != null && !exp.IsExpanded;
        }

        /// <summary>分组名（CollectionViewGroup.Name 是 object，转成字符串）。</summary>
        private static string GroupNameOf(GroupItem gi)
        {
            var grp = gi?.DataContext as CollectionViewGroup;
            if (grp == null) return null;
            var n = grp.Name;
            return n == null ? null : n.ToString();
        }

        /// <summary>
        /// 展开状态变化时的回调（Expander.IsExpanded 依赖属性的值变化）。
        /// 把该分组的"收起/展开"状态记进 <see cref="AppSettings.TaskCollapsedGroups"/> 并落盘。
        /// </summary>
        private void OnGroupExpanderIsExpandedChanged(object sender, EventArgs e)
        {
            if (_restoringGroupState) return;
            var exp = sender as Expander;
            if (exp == null) return;
            var gi = FindAncestor<GroupItem>(exp);
            if (gi == null) return;
            var name = GroupNameOf(gi);
            if (string.IsNullOrEmpty(name)) return;

            var collapsed = GetCollapsedGroups();
            bool wasCollapsed = collapsed.Contains(name);
            if (!exp.IsExpanded)
            {
                if (!wasCollapsed) { collapsed.Add(name); }
            }
            else
            {
                if (wasCollapsed) collapsed.Remove(name);
            }
            SaveCollapsedGroups(collapsed);
        }

        /// <summary>
        /// 每个文件夹分组容器**生成时**调用（GroupItem 的 Loaded 事件）。
        ///
        /// <para>🚨 <b>这是「记忆展开状态」的唯一正确入口。</b>
        /// 之前写的是"启动后遍历一遍所有 GroupItem 容器、逐个设置 IsExpanded"，
        /// 在虚拟化下必错：屏幕外的容器是**滚动时才生成**的，
        /// 启动那一刻压根不存在（用户实测：11 个分组只找到 3 个，
        /// 而且点「全部收起」直接提示"暂时拿不到分组容器"）。</para>
        ///
        /// <para>改成事件驱动后：<b>任何一个</b>分组无论何时被生成（启动 / 滚动 / 切换视图），
        /// 都会立刻按配置里的「已收起列表」摆正状态，不依赖"当时屏幕上有什么"。</para>
        /// </summary>
        private void OnTaskGroupItemLoaded(object sender, RoutedEventArgs e)
        {
            var gi = sender as GroupItem;
            if (gi == null) return;
            WatchGroupItemRecycling(gi);
            ApplyExpandedStateToGroup(gi);
        }

        /// <summary>
        /// 盯住分组容器的 DataContext 变化，处理**回收复用**场景。
        ///
        /// <para>【为什么必须有】<c>VirtualizationMode="Recycling"</c> 下，滚动会把同一个
        /// GroupItem 容器复用到另一个分组上 —— 此时 <c>Loaded</c> <b>不会再次触发</b>，
        /// 新分组会白白继承上一个分组的展开状态（实测：滚动后分组状态错乱）。</para>
        ///
        /// <para>⚠️ <c>DataContextChanged</c> 是普通 .NET 事件、**不是** RoutedEvent，
        /// 所以不能写在 XAML 的 EventSetter 里（会报 MC6006），
        /// 必须用 <see cref="System.ComponentModel.DependencyPropertyDescriptor"/> 挂。</para>
        /// </summary>
        private void WatchGroupItemRecycling(GroupItem gi)
        {
            if (gi == null || !_groupItemWatched.Add(gi)) return;
            try
            {
                var dpd = System.ComponentModel.DependencyPropertyDescriptor
                    .FromProperty(FrameworkElement.DataContextProperty, typeof(GroupItem));
                dpd.AddValueChanged(gi, OnGroupItemDataContextChanged);
            }
            catch { }
        }

        /// <summary>已经挂上 DataContext 监听的分组容器（避免重复挂）。</summary>
        private readonly HashSet<GroupItem> _groupItemWatched = new HashSet<GroupItem>();

        /// <summary>分组容器换了分组（回收复用）→ 重新按配置摆正展开状态。</summary>
        private void OnGroupItemDataContextChanged(object sender, EventArgs e)
        {
            var gi = sender as GroupItem;
            if (gi == null) return;
            // 推到布局之后：此刻模板里的 Expander 才真正对上新的分组
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { ApplyExpandedStateToGroup(gi); } catch { }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// 按「已收起列表」摆正某个分组的展开状态，并挂上变化监听。
        /// 供容器生成（<see cref="OnTaskGroupItemLoaded"/>）与批量操作后统一调用。
        /// </summary>
        private void ApplyExpandedStateToGroup(GroupItem gi)
        {
            if (_settings.TaskView != TaskViewMode.Grouped) return;
            var name = GroupNameOf(gi);
            if (string.IsNullOrEmpty(name)) return;

            var exp = FindGroupExpander(gi);   // 内部会顺手挂好 IsExpanded 监听
            if (exp == null) return;

            bool wantCollapsed = GetCollapsedGroups().Contains(name);
            if (exp.IsExpanded == !wantCollapsed) return;   // 已经是目标状态

            _restoringGroupState = true;       // 恢复期间不记录，避免回写自己
            try { exp.IsExpanded = !wantCollapsed; }
            finally { _restoringGroupState = false; }
        }

        /// <summary>读「已收起的分组名」列表。</summary>
        private List<string> GetCollapsedGroups()
        {
            var raw = _settings.TaskCollapsedGroups;
            var list = new List<string>();
            if (string.IsNullOrEmpty(raw)) return list;
            // 兼容旧数据：早期版本用过 '\u0001' 做分隔符（那是个会写坏 XML 的字符，
            // 见 GroupSep 的注释）。磁盘上真存着的话也一并切开，别当成一个怪名字。
            foreach (var s in raw.Split(new[] { GroupSep, '\u0001' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = s.Trim('\r', ' ', '\t');
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        /// <summary>写回「已收起的分组名」并落盘。</summary>
        private void SaveCollapsedGroups(List<string> collapsed)
        {
            // 兜底：任何 XML 非法字符都不许进配置（见 GroupSep 的注释，这里曾经毁过一次配置）。
            var safe = new List<string>(collapsed.Count);
            foreach (var s in collapsed)
            {
                if (string.IsNullOrEmpty(s)) continue;
                safe.Add(Util.StripXmlInvalid(s));
            }
            string raw = string.Join(GroupSep.ToString(), safe);
            if (raw == _settings.TaskCollapsedGroups) return;
            _settings.TaskCollapsedGroups = raw;
            try { SettingsStore.Save(_settings); } catch { }
        }

        /// <summary>把某个分组的展开状态记下来（供「全部收起/展开」批量操作时统一保存）。</summary>
        private void RememberGroupState(GroupItem gi, bool collapsed)
        {
            var name = GroupNameOf(gi);
            if (string.IsNullOrEmpty(name)) return;
            var list = GetCollapsedGroups();
            bool has = list.Contains(name);
            if (collapsed && !has) { list.Add(name); SaveCollapsedGroups(list); }
            else if (!collapsed && has) { list.Remove(name); SaveCollapsedGroups(list); }
        }

        /// <summary>
        /// 恢复各分组的展开状态（按记忆）。
        ///
        /// <para>⚠️ 【2026-10-22】只负责**已经生成**的容器，作为
        /// <see cref="OnTaskGroupItemLoaded"/> 的补充（切换视图后容器已存在时立即生效）。
        /// 真正保证"屏幕外的分组也正确"的是那个 Loaded 事件 ——
        /// 所以这里**不再**要求"必须遍历到全部容器"，拿不到就跳过。</para>
        /// </summary>
        private void RestoreGroupExpandedStates()
        {
            if (_settings.TaskView != TaskViewMode.Grouped) return;
            try
            {
                foreach (var gi in EnumerateGroupItems())
                    ApplyExpandedStateToGroup(gi);
            }
            catch { }
        }

        /// <summary>
        /// 【2026-10-22 已废弃，保留为空实现】原先是"等容器生成后恢复展开状态"的轮询。
        ///
        /// <para>现在恢复完全由 <see cref="OnTaskGroupItemLoaded"/> 驱动（每个容器
        /// 生成时自己读配置），**不再需要**"等所有容器都出现"这种前提 ——
        /// 而那个前提在虚拟化下永远不成立，正是"点全部收起没反应"的病根。
        /// 保留空实现只为不用去翻改各个调用点。</para>
        /// </summary>
        private void TryRestoreGroupExpandedStates()
        {
            try { _groupStateTimer?.Stop(); } catch { }
        }

        /// <summary>
        /// 「全部收起 / 全部展开」按钮。
        ///
        /// <para>🚨 <b>实现方式：改配置，而不是改容器。</b>
        /// 旧实现在这里遍历 GroupItem 容器、逐个点 <c>IsExpanded</c>，
        /// 但虚拟化下拿不到屏幕外的容器 —— 用户实测 11 个分组只处理了 3 个，
        /// 后来又变成"暂时拿不到分组容器（共有 11 个分组）"，点了完全没反应。</para>
        ///
        /// <para>正确做法：把"全部收起/展开"这个<b>事实</b>写进配置里的已收起列表，
        /// 再让已经生成的容器同步过去；将来滚动出来的新容器会在
        /// <see cref="OnTaskGroupItemLoaded"/> 里自动对上状态。
        /// 这样"屏幕上有几个容器"再也影响不到结果。</para>
        /// </summary>
        private void OnToggleCollapseAllGroups(object sender, RoutedEventArgs e)
        {
            if (_settings.TaskView != TaskViewMode.Grouped)
            {
                Log("当前是平铺视图，没有文件夹分组可收起。");
                return;
            }

            bool collapse = _collapseAllMode;

            // ① 全部当前分组名 —— 从**视图数据**取，与容器是否生成无关
            var names = GetAllGroupNames();
            if (names.Count == 0)
            {
                Log("当前没有可收起的文件夹分组。");
                return;
            }

            // ⚠️ 【2026-10-22】点之前先核对一下"实际是不是已经全收起了"。
            //   _collapseAllMode 是**自维护**的布尔，启动时按"上次收起的数量"推得，
            //   一旦和真实 UI 不同步（实测：重启后全是收起的，但标志是 true，
            //   于是第一次点击执行了"展开"而不是"收起"，用户看到"点了没反应"），
            //   后面每次点击都会反着来。
            //   修法：以**实际状态**为准 —— 已经全收起就必然转为展开，反之亦然。
            bool allCollapsedNow = names.Count > 0;
            foreach (var n in names)
            {
                if (!GetCollapsedGroups().Contains(n)) { allCollapsedNow = false; break; }
            }
            collapse = !allCollapsedNow;

            // ② 把事实写进配置（这才是唯一事实来源）
            if (collapse) SaveCollapsedGroups(new List<string>(names));
            else SaveCollapsedGroups(new List<string>());

            // ③ 已经生成出来的容器立刻同步（没生成的交给 Loaded 事件）
            int applied = 0;
            foreach (var gi in EnumerateGroupItems())
            {
                try
                {
                    ApplyExpandedStateToGroup(gi);
                    applied++;
                }
                catch { }
            }

            _collapseAllMode = !collapse;
            UpdateCollapseAllButton();
            Log(collapse
                ? string.Format("已收起全部 {0} 个文件夹分组（屏幕上即时同步 {1} 个）。", names.Count, applied)
                : string.Format("已展开全部 {0} 个文件夹分组（屏幕上即时同步 {1} 个）。", names.Count, applied));
        }

        /// <summary>
        /// 当前视图里**所有**分组名 —— 直接从 <see cref="ListCollectionView.Groups"/> 读。
        ///
        /// <para>这个属性是分组结果的**数据模型**（<c>CollectionViewGroup</c> 集合），
        /// 与虚拟化、与容器是否生成**完全无关** —— 所以永远拿得到全量。
        /// 之前绕道去翻容器，才是"只有 3 个"和"一个都拿不到"的根源。</para>
        /// </summary>
        private List<string> GetAllGroupNames()
        {
            var names = new List<string>();
            try
            {
                if (_taskView == null) return names;
                var groups = _taskView.Groups;
                if (groups == null) return names;
                foreach (var g in groups)
                {
                    var cvg = g as System.Windows.Data.CollectionViewGroup;
                    if (cvg == null) continue;
                    // ListCollectionView 未分组时，Groups 里会有一个 Name == null 的整包组，
                    // 那不是文件夹分组，跳过。
                    var n = cvg.Name == null ? null : cvg.Name.ToString();
                    if (string.IsNullOrEmpty(n)) continue;
                    if (!names.Contains(n)) names.Add(n);
                }
            }
            catch { }
            return names;
        }

        /// <summary>
        /// 同步「全部收起 / 全部展开」按钮的文案与图标。
        ///
        /// <para>🚨 【2026-10-22】文案**必须从实际状态推导**，不能用自维护的
        /// <c>_collapseAllMode</c>。实测事故：重启后所有分组都是收起的，
        /// 但标志位默认 <c>true</c>，于是按钮显示"全部收起"（其实已经收起了），
        /// 点一下执行的是"展开" —— 用户看到的就是"点了没反应 / 行为反着来"。</para>
        /// </summary>
        private void UpdateCollapseAllButton()
        {
            if (TaskCollapseAllText == null) return;

            // 实际是否"已经全收起"：分组全在已收起列表里 → 按钮该显示"全部展开"
            bool allCollapsed = false;
            var names = GetAllGroupNames();
            if (names.Count > 0)
            {
                allCollapsed = true;
                var collapsed = GetCollapsedGroups();
                foreach (var n in names)
                {
                    if (!collapsed.Contains(n)) { allCollapsed = false; break; }
                }
            }

            // 把标志位拉回和现实一致，后续点击才能算对方向
            _collapseAllMode = !allCollapsed;

            TaskCollapseAllText.Text = _collapseAllMode ? "全部收起" : "全部展开";
            var geo = TryFindResource(_collapseAllMode ? "IconCollapseAll" : "IconExpandAll") as Geometry;
            if (geo != null) TaskCollapseAllIcon.Data = geo;
            TaskCollapseAllBtn.ToolTip = _collapseAllMode
                ? "当前：展开状态（点击把所有文件夹收起）"
                : "当前：收起状态（点击把所有文件夹展开）";
        }

        // ---------------- 任务列表：删除选择模式（z / Enter） ----------------
        //
        // 用户要求：「按 z 进入删除选择模式，可以框选也可以点选（选中文件夹就删整个文件夹）；
        // 选好后按 Enter 确认，弹出『仅删除记录』还是『一起删除本地文件』」。
        // 另：「清空已完成」也用同一套二选一。

        private bool _taskSelectMode;
        private Point _bandStart;
        private bool _bandDragging;

        /// <summary>删除方式（批量删除 / 清空已完成共用）。</summary>
        private enum TaskDeleteChoice
        {
            Cancel,
            RecordOnly,
            WithFiles,
        }

        private void EnterTaskSelectMode()
        {
            if (_taskSelectMode) return;
            _taskSelectMode = true;
            // 行模板里的勾选框靠这个 Tag 显示（不做隐藏控件常驻）
            TaskList.Tag = "z";
            TaskSelectBar.Visibility = Visibility.Visible;
            TaskSelectFrame.Visibility = Visibility.Visible;
            TaskBandLayer.Visibility = Visibility.Visible;
            UpdateTaskSelectCount();
            Log("已进入删除选择模式：拖拽框选，或点文件行单独选择、点文件夹头选中整组；按 Enter 确认，Esc 退出。");
        }

        private void ExitTaskSelectMode(bool clearSelection = true)
        {
            if (!_taskSelectMode) return;
            _taskSelectMode = false;
            TaskList.Tag = null;
            TaskSelectBar.Visibility = Visibility.Collapsed;
            TaskSelectFrame.Visibility = Visibility.Collapsed;
            TaskBandLayer.Visibility = Visibility.Collapsed;
            TaskBandRect.Visibility = Visibility.Collapsed;
            _bandDragging = false;
            try { TaskList.ReleaseMouseCapture(); } catch { }
            if (clearSelection) TaskList.SelectedItems.Clear();
            UpdateTaskSelectCount();
        }

        private void UpdateTaskSelectCount()
        {
            if (TaskSelectCountText == null) return;
            TaskSelectCountText.Text = "已选 " + TaskList.SelectedItems.Count + " 项";
        }

        private void OnTaskSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_taskSelectMode) UpdateTaskSelectCount();
        }

        /// <summary>点分组头：整组选中 / 取消（只在删除选择模式下拦截，平时交给 Expander 收起）。</summary>
        private void ToggleTaskGroupSelection(GroupItem gi)
        {
            var grp = gi == null ? null : gi.DataContext as CollectionViewGroup;
            if (grp == null) return;
            var items = grp.Items.OfType<TaskItem>().ToList();
            bool all = items.All(t => TaskList.SelectedItems.Contains(t));
            foreach (var t in items)
            {
                if (all) TaskList.SelectedItems.Remove(t);
                else if (!TaskList.SelectedItems.Contains(t)) TaskList.SelectedItems.Add(t);
            }
            UpdateTaskSelectCount();
        }

        private static GroupItem FindAncestorGroupItem(DependencyObject d)
        {
            return FindAncestor<GroupItem>(d);
        }

        /// <summary>从可视元素往上找第一个指定类型（用于判定点的是标题栏还是行）。</summary>
        private static T FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        private void BeginTaskBandDrag(MouseButtonEventArgs e)
        {
            _bandDragging = true;
            _bandStart = e.GetPosition(TaskBandLayer);
            TaskBandRect.Visibility = Visibility.Visible;
            Canvas.SetLeft(TaskBandRect, _bandStart.X);
            Canvas.SetTop(TaskBandRect, _bandStart.Y);
            TaskBandRect.Width = 0;
            TaskBandRect.Height = 0;
            try { TaskList.CaptureMouse(); } catch { }
        }

        private void OnTaskListMouseMove(object sender, MouseEventArgs e)
        {
            if (!_bandDragging) return;
            var p = e.GetPosition(TaskBandLayer);
            double x = Math.Min(p.X, _bandStart.X);
            double y = Math.Min(p.Y, _bandStart.Y);
            double w = Math.Abs(p.X - _bandStart.X);
            double h = Math.Abs(p.Y - _bandStart.Y);
            Canvas.SetLeft(TaskBandRect, x);
            Canvas.SetTop(TaskBandRect, y);
            TaskBandRect.Width = w;
            TaskBandRect.Height = h;
            SelectTasksInBand(new Rect(x, y, w, h));
        }

        private void OnTaskListMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_bandDragging) return;
            _bandDragging = false;
            try { TaskList.ReleaseMouseCapture(); } catch { }
            bool tiny = TaskBandRect.Width < 4 && TaskBandRect.Height < 4;
            TaskBandRect.Visibility = Visibility.Collapsed;
            // 点在空白处（没拖动）= 清空选择，和列表的常规手感一致
            if (tiny) TaskList.SelectedItems.Clear();
            UpdateTaskSelectCount();
        }

        /// <summary>把矩形碰到（且已生成容器）的任务行选中，其余取消。</summary>
        private void SelectTasksInBand(Rect band)
        {
            for (int i = 0; i < _tasks.Count; i++)
            {
                var t = _tasks[i];
                var lbi = TaskList.ItemContainerGenerator.ContainerFromItem(t) as ListBoxItem;
                if (lbi == null) continue;   // 没生成的（滚出屏幕/被收起）不参与框选
                Rect b;
                try
                {
                    b = lbi.TransformToAncestor(TaskBandLayer)
                            .TransformBounds(new Rect(lbi.RenderSize));
                }
                catch { continue; }

                bool hit = b.IntersectsWith(band);
                bool sel = TaskList.SelectedItems.Contains(t);
                if (hit && !sel) TaskList.SelectedItems.Add(t);
                else if (!hit && sel) TaskList.SelectedItems.Remove(t);
            }
        }

        /// <summary>Enter：把当前选中项交给用户做「仅删除记录 / 连本地文件一起删」的选择。</summary>
        private void ConfirmTaskDeleteSelection()
        {
            var sel = TaskList.SelectedItems.Cast<TaskItem>().ToList();
            if (sel.Count == 0)
            {
                Log("没有选中任何任务，已退出删除选择模式。");
                ExitTaskSelectMode(clearSelection: false);
                return;
            }

            var choice = AskTaskDeleteChoice(sel);
            if (choice == TaskDeleteChoice.Cancel) return;   // 留在选择模式里，方便继续调整

            ExitTaskSelectMode();
            if (choice == TaskDeleteChoice.WithFiles) DeleteTaskLocals(sel);
            else RemoveTaskRecords(sel, askRunning: false);
        }

        /// <summary>
        /// 删除方式的二选一对话框。
        /// 不用 AppDialog（MessageBox 的按钮固定是"是/否"，没法写成两种删除方式）。
        /// </summary>
        private TaskDeleteChoice AskTaskDeleteChoice(IList<TaskItem> tasks)
        {
            int total = tasks.Count;
            var selectedSet = new HashSet<TaskItem>(tasks);
            // 【2026-10-20】只有整组都被选中时才会连文件夹一起删；
            // 只选了文件夹里的一个文件时它就是普通文件，不能再写成"1 个文件夹"。
            var wholeFolders = tasks
                .Where(t => t.GroupName != TaskItem.SingleFileGroupName)
                .Select(t => t.GroupName)
                .Distinct()
                .Where(name => _tasks.Where(x => x.GroupName == name).All(selectedSet.Contains))
                .ToList();
            int inWholeFolders = tasks.Count(t => wholeFolders.Contains(t.GroupName));
            int loneFiles = total - inWholeFolders;

            string summary;
            if (wholeFolders.Count > 0 && loneFiles > 0)
                summary = string.Format("共 {0} 项：{1} 个文件夹（{2} 个文件），另有 {3} 个单独选择的文件。",
                    total, wholeFolders.Count, inWholeFolders, loneFiles);
            else if (wholeFolders.Count > 0)
                summary = string.Format("共 {0} 个文件，来自 {1} 个文件夹（整个文件夹会被删除）。",
                    total, wholeFolders.Count);
            else
                summary = string.Format("共 {0} 个文件。", total);

            if (wholeFolders.Count > 0)
            {
                var names = wholeFolders.Select(n => "· " + n).Take(3).ToList();
                if (wholeFolders.Count > 3) names.Add("…");
                summary += "\n" + string.Join("\n", names);
            }

            var win = new Window { Title = "删除任务", Owner = this };
            DialogChrome.StyleWindow(win, 480, 320);
            win.SizeToContent = SizeToContent.Height;   // 内容多时往下长，不出现截断

            var root = new System.Windows.Controls.Grid
            {
                Margin = new Thickness(20, 18, 20, 16),
            };
            for (int i = 0; i < 4; i++)
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = DialogChrome.PageTitle("删除 " + total + " 项");
            System.Windows.Controls.Grid.SetRow(title, 0);
            root.Children.Add(title);

            var msg = DialogChrome.Hint(summary, 2, 10);
            System.Windows.Controls.Grid.SetRow(msg, 1);
            root.Children.Add(msg);

            var warn = DialogChrome.Hint(
                "「仅删除记录」只移除列表条目，磁盘上的文件保留。\n" +
                "「删除记录和本地文件」会先取消下载，再删除目标文件与分片缓存；\n" +
                "整组选中时，删空的文件夹也会一并清理 —— 不可恢复。", 0, 14);
            warn.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            System.Windows.Controls.Grid.SetRow(warn, 2);
            root.Children.Add(warn);

            var choice = TaskDeleteChoice.Cancel;
            var cancel = DialogChrome.SecondaryButton("取消", (s, e) => win.Close());
            // 直觉：Esc 就是取消（IsCancel 让 Esc 直接关窗）
            cancel.IsCancel = true;
            var record = DialogChrome.SecondaryButton("仅删除记录", (s, e) =>
            {
                choice = TaskDeleteChoice.RecordOnly;
                win.Close();
            });
            var withFiles = DialogChrome.DangerButton("删除记录和本地文件", (s, e) =>
            {
                choice = TaskDeleteChoice.WithFiles;
                win.Close();
            });
            var row = DialogChrome.ButtonRow(cancel, record, withFiles);
            System.Windows.Controls.Grid.SetRow(row, 3);
            root.Children.Add(row);

            win.Loaded += (s, e) => { try { cancel.Focus(); } catch { } };
            win.Content = root;
            win.ShowDialog();
            return choice;
        }

        /// <summary>批量「仅移除记录」（支持正在下载的任务：会先取消再移除）。</summary>
        private void RemoveTaskRecords(IList<TaskItem> items, bool askRunning)
        {
            if (items == null || items.Count == 0) return;

            if (askRunning)
            {
                var running = items.Where(x => x.State == JobState.Downloading
                                            || x.State == JobState.Queued
                                            || x.State == JobState.Paused
                                            || x.State == JobState.Cancelling).ToList();
                if (running.Count > 0)
                {
                    var ans = AppDialog.Show(this,
                        (running.Count == 1
                            ? "任务「" + running[0].Name + "」还在进行中。\n\n"
                            : "选中的 " + running.Count + " 个任务还在进行中。\n\n")
                        + "仅移除记录不会停止后台下载，文件仍会继续写入。\n"
                        + "确定要先取消并移除记录吗？",
                        "仅移除记录", MessageBoxButton.YesNo, MessageBoxImage.Question,
                        MessageBoxResult.No);
                    if (ans != MessageBoxResult.Yes)
                        return;
                }
            }

            foreach (var t in items) RemoveTaskRecord(t);
            // 移除后立刻同步「未完成任务」记录 —— 否则它下次启动还会被当成可续传任务
            SyncPending();
            UpdatePauseAllButton(force: true);
        }

        /// <summary>批量「删除本地文件」：取消 + 移除记录 + 后台删文件（分片元数据一并清）。</summary>
        private void DeleteTaskLocals(IList<TaskItem> items)
        {
            if (items == null) return;
            var list = items.Where(x => x != null && x.Job != null
                                     && !string.IsNullOrEmpty(x.Job.Dest)).ToList();
            if (list.Count == 0)
            {
                Log("没有可删除的本地文件（任务没有记录目标路径）。");
                return;
            }

            foreach (var t in list)
            {
                // 只要还在"活着"的状态就先取消（含暂停——见 RemoveTaskRecord 的注释）
                if (t.State == JobState.Downloading || t.State == JobState.Queued
                    || t.State == JobState.Paused || t.State == JobState.Cancelling)
                {
                    try { t.Job.CancelJob(); } catch { }
                    t.State = JobState.Cancelling;
                }

                string dest = t.Job.Dest;
                string name = t.Name;
                _tasks.Remove(t);
                // 后台删除：文件可能较大，且被占用时要等待句柄释放，不能卡 UI 线程。
                _ = Task.Run(() => DeleteLocalFiles(dest, name));
            }

            SyncPending();
            UpdatePauseAllButton(force: true);
            Log("已删除 " + list.Count + " 项任务的本地文件。");
        }

        // ---------------- 任务列表右键菜单（单任务操作） ----------------
        //
        // 移植自 Python Flet 版的 _pause_single / _resume_single / _cancel_single /
        // _task_del_local / _file_menu_popup（右键菜单）。
        //
        // 为什么必须有：此前 C# 只有「全部暂停/恢复/取消」，一个跑得慢的任务
        // 会拖住整批，用户没法单独处理它 —— 这是下载工具的基本操作。

        /// <summary>
        /// 右键按下时把光标下的任务选中，这样菜单动作能对准用户真正点的那一行。
        /// 若该处没有任务（点到空白），保持原选中不变。
        ///
        /// <para>⚠️ 【2026-10-22 修】原来挂在 <c>MouseRightButtonUp</c> 上，
        /// 那个事件在右键菜单场景下**不可靠**：右键按下时 <c>ContextMenu</c> 就会弹出，
        /// ListBox 往往收不到 RightButtonUp → "右键点某一行，删除却没反应"。
        /// 改挂 <c>PreviewMouseRightButtonDown</c>（隧道阶段，先于菜单弹出）。</para>
        /// </summary>
        private void OnTaskListRightClickDown(object sender, MouseButtonEventArgs e)
        {
            var lbi = FindListBoxItem(e.OriginalSource as DependencyObject);
            if (lbi == null) return;

            // 记住这次右键命中的行：菜单动作优先用它（见 TasksFromMenu），
            // 这样即便选中态没跟上，删除/重试也一定作用在用户点的这一行。
            _taskRightClickTarget = lbi.DataContext as TaskItem;

            // ⚠️ 【2026-10-03 修】原来无条件 `TaskList.SelectedItem = ...` ——
            //    这句会**把整个选择替换成这一个**，于是"Shift 离散多选之后再右键"
            //    多选立刻被清掉，菜单里的暂停/删除只作用到一个文件
            //    （用户反馈：「根本无法完成多选的情况下右键使用功能」）。
            //    现在：点到**已选中**的行 → 保持整份多选不动；点到**没选中**的行 →
            //    才把它单独选上（否则右键一个没选中的行却对别的行生效，很反直觉）。
            if (lbi.IsSelected) return;
            TaskList.SelectedItems.Clear();
            lbi.IsSelected = true;
        }

        /// <summary>
        /// 任务列表的右键（保留旧入口，供键盘/其它调用路径使用）。
        /// </summary>
        private void OnTaskListRightClick(object sender, MouseButtonEventArgs e)
        {
            OnTaskListRightClickDown(sender, e);
        }

        /// <summary>双击任务行 = 打开文件（与「打开」按钮同义）。</summary>
        private void OnTaskListDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var item = TaskList.SelectedItem as TaskItem;
            if (item != null)
                OpenTaskFileCore(item);
        }

        /// <summary>
        /// 从右键菜单的动作里取出**要操作的全部任务**。
        ///
        /// 【2026-10-03】原来只取 `SelectedItem`（一个）—— 于是"Shift 离散多选之后
        /// 右键暂停/删除"只会作用于其中一个（用户反馈）。现在把选中的全取出来；
        /// 没选中任何行时回退到菜单项自己的 Tag（行内按钮那条路径）。
        ///
        /// <para>【2026-10-22】新增一层兜底：**右键命中的那一行**
        /// （<see cref="_taskRightClickTarget"/>）。</para>
        /// <para>为什么需要：右键时"选中态"可能没跟上（分组视图 / 已删本地文件的任务），
        /// 此时若只认 SelectedItems，菜单动作会拿到**空集合** → 点了"删除"毫无反应
        /// （用户实测：「右键删除和选择都没用」）。用右键命中的行兜底，
        /// 保证"点哪一行就作用于哪一行"。</para>
        /// </summary>
        private List<TaskItem> TasksFromMenu(object sender)
        {
            var list = TaskList.SelectedItems.OfType<TaskItem>().ToList();
            if (list.Count > 0) return list;

            // 选中态没跟上 → 用右键命中的那一行兜底
            if (_taskRightClickTarget != null) return new List<TaskItem> { _taskRightClickTarget };

            var one = (sender as System.Windows.Controls.MenuItem)?.Tag as TaskItem;
            return one != null ? new List<TaskItem> { one } : new List<TaskItem>();
        }

        /// <summary>
        /// 最近一次**右键命中的任务行**（见 <see cref="OnTaskListRightClickDown"/>）。
        /// 用作菜单动作的兜底目标。
        /// </summary>
        private TaskItem _taskRightClickTarget;

        /// <summary>从右键菜单的动作里取出目标任务：优先用选中的，回退按钮 Tag。</summary>
        private TaskItem TaskFromMenu(object sender)
        {
            return (TaskList.SelectedItem as TaskItem)
                   ?? ((sender as System.Windows.Controls.MenuItem)?.Tag as TaskItem);
        }

        /// <summary>
        /// 任务行右键菜单弹出前，按目标文件格式改写「播放文件 / 打开文件」文案。
        ///
        /// 【2026-10-20 用户要求】视频（mkv/mp4…）显示「播放文件」，用 VLC 打开；
        /// 其它格式（zip/pdf/jpg…）显示「打开文件」，用系统默认程序打开。
        /// 菜单项在 XAML 里只写一份，靠这里动态改 Header，避免维护两套菜单。
        /// </summary>
        private void OnTaskMenuOpened(object sender, RoutedEventArgs e)
        {
            try
            {
                if (TaskOpenMenuItem == null) return;

                // 右键点在某一行上时，选中项已由 WPF 切换为该行；没有选中项就用首项兜底。
                var item = TaskList.SelectedItem as TaskItem
                           ?? TaskList.Items.Cast<TaskItem>().FirstOrDefault();
                string dest = item?.Job?.Dest;

                bool isVideo = MediaProbe.IsVideo(dest);
                TaskOpenMenuItem.Header = isVideo ? "播放文件" : "打开文件";

                // 「重试」只在任务终态（失败/已取消，含网络停滞中止）出现：
                // 这类任务分片还在，重新入队即可续传，不需要重新解析分享。
                if (TaskRetryMenuItem != null)
                    TaskRetryMenuItem.Visibility =
                        (item != null && (item.State == JobState.Failed || item.State == JobState.Cancelled))
                            ? System.Windows.Visibility.Visible
                            : System.Windows.Visibility.Collapsed;
            }
            catch { }
        }

        private void OnTaskPause(object sender, RoutedEventArgs e)
        {
            foreach (var t in TasksFromMenu(sender)) PauseTask(t);
        }

        private void OnTaskResume(object sender, RoutedEventArgs e)
        {
            foreach (var t in TasksFromMenu(sender)) ResumeTask(t);
        }

        /// <summary>
        /// 任务右键「重试」：针对**失败 / 已取消（含网络停滞中止）**的任务，
        /// 分片还在本地，重新入队即可续传，不需要重新解析分享。
        /// 【用户原话】「网络停滞……而且为什么无法重试下载」—— 此前菜单里根本没「重试」入口。
        /// </summary>
        private void OnTaskRetry(object sender, RoutedEventArgs e)
        {
            foreach (var t in TasksFromMenu(sender))
            {
                if (t.State == JobState.Failed || t.State == JobState.Cancelled)
                    RetryTask(t);
            }
        }

        /// <summary>
        /// 任务行上那个**图标式播放/暂停切换按钮**（图标会随状态互换）。
        /// 它控制的是**这个下载任务**的暂停/恢复 —— 与右键菜单的「暂停/恢复」同一套逻辑。
        /// ⚠️ 不是"控制播放器"：GeZi 是把文件交给 VLC 等**外部播放器**打开的，控制不了它们。
        /// </summary>
        private void OnTaskTogglePause(object sender, RoutedEventArgs e)
        {
            var t = (sender as System.Windows.Controls.Button)?.Tag as TaskItem;
            if (t == null) return;
            if (t.State == JobState.Paused) ResumeTask(t);
            else PauseTask(t);
        }

        /// <summary>暂停单个任务（右键菜单与行内按钮共用）。</summary>
        private void PauseTask(TaskItem t)
        {
            if (t == null) return;
            if (t.State == JobState.Downloading || t.State == JobState.Queued)
            {
                t.Job.PauseJob();
                // 与 Python 版一致：排队中的任务也允许「暂停」，语义是先挂牌，
                // 轮到它时不会真的开跑，用户恢复后才进队列。
                // ⚠️ 必须先置 PauseRequested 再改 State：进度上报永远报 Downloading，
                //    没有这个标志的话下一帧就会把 Paused 覆盖掉（图标会来回跳）。
                t.PauseRequested = true;
                t.State = JobState.Paused;
                // 【2026-10-20 移植】立刻把速度归零（配合 OnJobUpdate 的 keepPaused 冻结），
                // 否则在途请求还会让核心上报渐降速度，界面看着像"慢慢停"。
                t.Speed = 0;
                Log("已暂停: " + t.Name);
            }
            else
            {
                Log("该任务当前状态不可暂停（" + t.StatusText + "）");
            }
        }

        /// <summary>
        /// 重试单个失败/取消任务：本地分片还在，重置状态后重新入队续传。
        /// · Dormant（程序重启后从快照恢复的行）：走与「恢复」相同的重新取链续传流程；
        /// · 非 Dormant（本次会话内失败/取消）：重置令牌直接重跑同一 job，分片自然续传。
        /// </summary>
        private void RetryTask(TaskItem t)
        {
            if (t == null) return;
            if (t.State != JobState.Failed && t.State != JobState.Cancelled)
            {
                Log("该任务当前状态不可重试（" + t.StatusText + "）");
                return;
            }

            // 重启恢复的占位行：重新取链续传（与 ResumeTask 的 Dormant 分支一致）
            if (t.Dormant)
            {
                // ⚠️ 注意别再回到 ResumeTask：ResumeTask 的失败分支现在会调用本方法，
                //   两边互相调用会无限递归。这里直接走 Dormant 的续传逻辑。
                var pend = BuildPendingTask(t);
                if (pend == null)
                {
                    Log("这个任务缺少续传信息，无法继续；请重新解析分享后再下载（分片在，会自然续传）。");
                    return;
                }
                Log("正在为「" + t.Name + "」重新取链……");
                _ = ResumePendingBatchAsync(new List<PendingTask> { pend });
                return;
            }

            // 本次会话内失败/取消：旧 CTS 已触发不可复用，重置后才能再跑
            t.Job.ResetForRetry();
            t.Error = "";
            t.HistoryRecorded = false;   // 允许重试成功后再记一条历史
            t.PauseRequested = false;
            t.Speed = 0;
            t.State = JobState.Queued;   // 先挂牌「排队中」，RunJobs 会改为「下载中」
            Log("正在重试: " + t.Name + "（已下载部分会续传）");
            RunJobs(new List<DownloadJob> { t.Job });
        }

        /// <summary>恢复单个任务（右键菜单与行内按钮共用）。</summary>
        private void ResumeTask(TaskItem t)
        {
            if (t == null) return;

            // 【2026-10-13】上次运行恢复出来的行：没有挂在调度器上，点 ▶ = 重新取链续传。
            if (t.Dormant)
            {
                if (t.State == JobState.Completed)
                {
                    Log("任务已完成。");
                    return;
                }
                var pend = BuildPendingTask(t);
                if (pend == null)
                {
                    Log("这个任务缺少续传信息，无法继续；请重新解析分享后再下载（分片在，会自然续传）。");
                    return;
                }

                Log("正在为「" + t.Name + "」重新取链……");
                // 续传流程会自己新建一行并替换掉这条占位（见 ResumePendingBatchAsync 里的去重）
                _ = ResumePendingBatchAsync(new List<PendingTask> { pend });
                return;
            }

            if (t.State == JobState.Paused)
            {
                // 🚨 【2026-10-22 修】重启恢复出来的 Paused 任务**不能直接 ResumeJob**。
                //
                // 【用户实测】关了程序 1 分钟再打开，7 个任务停在「已暂停」，
                // 点 ▶ 之后状态变成「下载中」，但日志一直是
                // 「下载中：7 个任务（0 个在跑），总速度 0 B/s」——永远下不动。
                //
                // 【根因】`ResumeJob()` 只做一件事：取消暂停信号。它**不重新取链**。
                // 而这些任务的 `Url` 是**上次会话**取到的直链，签名早已过期
                // （实测 state.json 里 7 条 Url 长度 520~658，全是死链）。
                // 恢复后 worker 拿死链去请求 → CDN 返回 401/403/412 → 立即失败 →
                // 表现就是"在跑但速度 0"。
                //
                // 【为什么以前没暴露】同一个会话内暂停/恢复是没问题的 ——
                // 那时直链还活着（TTL 5~10 分钟）。只有**跨重启**才会踩到。
                //
                // 【修法】Dormant（= 上次运行恢复出来的行）的 Paused 一律走
                // 「重新取链 → 续传」，与失败/取消任务同一条路径；
                // 非 Dormant（本次会话内暂停的）保持原来的 ResumeJob —— 那是对的。
                if (t.Dormant)
                {
                    var pend = BuildPendingTask(t);
                    if (pend == null)
                    {
                        Log("这个任务缺少续传信息，无法继续；请重新解析分享后再下载（分片在，会自然续传）。");
                        return;
                    }
                    Log("正在为「" + t.Name + "」重新取链……（上次会话的直链已过期）");
                    _ = ResumePendingBatchAsync(new List<PendingTask> { pend });
                    return;
                }

                t.PauseRequested = false;   // 先清标志，否则会继续忽略核心的 Downloading
                t.Job.ResumeJob();
                t.State = JobState.Downloading;
                Log("已恢复: " + t.Name);
            }
            else if (t.State == JobState.Failed || t.State == JobState.Cancelled)
            {
                // 🚨 【2026-10-22 修】失败/已取消也要能用 ▶ 恢复。
                //
                // 【缺陷】原先这里只有上面 Paused 一个分支，失败任务落进下面的 else，
                //   只打一句「该任务当前状态不可恢复（失败）」就结束 ——
                //   用户看到行内明明有个 ▶ 按钮，点了却什么都没发生。
                //   实测截图里四个「网络停滞已中止」的任务就卡在这里：
                //   它们的 Message 明确写着「已保留断点，降低线程数后重试可续传」，
                //   也就是**本来就能续传**，只是这个入口没实现。
                //
                // 【为什么之前没被发现】「全部恢复」走的是 OnResumeAll，那边
                //   已经正确处理了 Failed/Cancelled（转 Queued 再重新取链），
                //   所以只有**单个任务的 ▶** 是坏的 —— 不点全部恢复就永远撞不上。
                //
                // 【做法】与 OnResumeAll 完全一致，走「重新取链 → 续传」：
                //   旧直链带时效签名早已过期，直接重跑旧 job 必然立刻再失败；
                //   重新取链还能顺带触发「中转副本没了 → 回退到分享源文件」的自愈。
                //   分片还在磁盘上，新 job 用同一个 Dest，自然续传不重下。
                RetryTask(t);
                Log("重新开始: " + t.Name);
            }
            else
            {
                Log("该任务当前状态不可恢复（" + t.StatusText + "）");
            }
        }

        /// <summary>
        /// 把一个（Dormant 的）任务还原成续传流程认识的 <see cref="PendingTask"/>。
        /// 缺少重取链信息（免转存 / fid 为空）时返回 null，由调用方给提示。
        /// </summary>
        private PendingTask BuildPendingTask(TaskItem t)
        {
            if (t == null || t.Job == null) return null;
            var p = t.Job.Pending;
            if (p == null || string.IsNullOrEmpty(t.Job.Dest) || string.IsNullOrEmpty(p.Fid))
                return null;
            if (p.FromShare
                && (string.IsNullOrEmpty(p.Token) || string.IsNullOrEmpty(p.PwdId)
                    || string.IsNullOrEmpty(p.Stoken)))
                return null;

            return new PendingTask
            {
                Name = t.Name,
                Dest = t.Job.Dest,
                Url = t.Job.Url,
                Fid = p.Fid,
                Token = p.Token,
                FromShare = p.FromShare,
                Size = t.Total,
                StreamPlay = true,   // 落盘方式唯一化：恒可边下边播
                StreamPlayMode = true,   // 落盘方式唯一化：恒可边下边播
                Status = t.StatusText,
                PwdId = p.PwdId,
                Stoken = p.Stoken,
                Passcode = p.Passcode,
                RelDir = p.RelDir,
                GroupName = p.GroupName,
                // 【2026-10-22】源文件定位信息（转存副本没了时可回退免转存）
                SourceFid = p.SourceFid,
                SourceName = p.SourceName,
            };
        }

        private void OnTaskCancel(object sender, RoutedEventArgs e)
        {
            foreach (var t in TasksFromMenu(sender)) CancelTask(t);
        }

        /// <summary>取消单个任务（右键菜单可能一次取消多个）。</summary>
        private void CancelTask(TaskItem t)
        {
            if (t == null) return;
            if (t.State == JobState.Downloading || t.State == JobState.Queued
                || t.State == JobState.Paused || t.State == JobState.Cancelling)
            {
                t.Job.CancelJob();
                // 与 CancelAll 同理：立即置「取消中」，否则窗口期内它看着还是
                // 「下载中」，用户点「清空已完成」清不掉会以为按钮坏了。
                t.State = JobState.Cancelling;
                // 【2026-10-03 用户反馈】「丢弃这些任务选择不进行续传的时候不会删除
                //  取消续传的文件的分片和对应的其他文件」。
                //  实测根因**不在"丢弃"那一步**（那条路径本来就删）：而是**被取消的任务
                //  根本进不了「未完成的任务」列表** —— SyncPending() 只收
                //  Queued/Downloading/Paused，Cancelled 被排除在外，
                //  于是它的分片成了**没有任何入口能清掉的孤儿**，而 PartsSweeper
                //  又只扫用户自定义的分片根目录（默认无自定义根 → 一个目录都不扫），
                //  磁盘上就一直躺着（用户机器上实测残留 59 MB）。
                //  → 按用户选定的方案：**取消 = 彻底放弃，立刻清理本地残留**。
                //    想保留进度以后再续传，请用「暂停」。
                Log("已请求取消: " + t.Name + "（将清理本地分片与未下完的部分）");
                CleanupCanceledTaskAsync(t.Job.Dest, t.Name);
            }
            else
            {
                Log("该任务当前状态不可取消（" + t.StatusText + "）");
            }
        }

        /// <summary>
        /// 取消任务后清理它的本地残留：分片目录 / 续传元数据 / 未下完的目标文件。
        ///
        /// <para>【为什么要后台 + 重试】</para>
        /// <para>
        /// 取消是**异步**的：<c>CancelJob()</c> 只是发个信号，下载器可能还要几百毫秒
        /// 才放手 —— 正在写的分片文件此刻还占着句柄，立刻删会 IOException。
        /// 所以放到后台线程，每轮"先删再查"，直到目标消失或重试用尽。
        /// </para>
        ///
        /// <para>【为什么不用 TryDeletePath 的静默吞异常】</para>
        /// <para>
        /// 用户报的正是"删没删不知道"。这里把结果写进日志：成功报释放了多少，
        /// 失败也明说（并提示下次启动的自动清扫会兜底），不再静默。
        /// </para>
        /// </summary>
        private void CleanupCanceledTaskAsync(string dest, string taskName)
        {
            if (string.IsNullOrEmpty(dest)) return;

            // ⚠️ AllPartsRoots() 要读 _settings，必须在 UI 线程先取好再进后台线程。
            var roots = AllPartsRoots();
            string name = taskName ?? "任务";

            var th = new Thread(() =>
            {
                long freed = 0;
                bool ok = false;

                for (int attempt = 0; attempt < 12; attempt++)
                {
                    if (attempt > 0) Thread.Sleep(250);

                    ok = true;
                    try
                    {
                        foreach (string path in SegmentedDownloader.EnumeratePartialPaths(dest, roots))
                        {
                            freed += PathSizeSafe(path);
                            TryDeletePath(path);
                            if (Directory.Exists(path) || File.Exists(path)) ok = false;
                        }

                        // 目标文件本身（未下完的残件；共写模式下它可能是预分配的满尺寸空洞文件）
                        if (File.Exists(dest))
                        {
                            freed += PathSizeSafe(dest);
                            try { File.Delete(dest); } catch { }
                            if (File.Exists(dest)) ok = false;
                        }
                    }
                    catch { ok = false; }

                    if (ok) break;
                }

                try
                {
                    LogFromWorker(ok
                        ? string.Format("已清理「{0}」的本地残留（释放 {1}）。",
                            name, Util.FormatSize(freed))
                        : string.Format("「{0}」的本地残留有文件正被占用，未能全部删除；"
                            + "下次启动的自动清扫会兜底回收。", name));
                }
                catch { }
            })
            {
                IsBackground = true,          // 后台线程：不阻止程序退出
                Name = "GeZi-CancelCleanup",
            };
            th.Start();
        }

        /// <summary>取路径占用的字节数（目录递归求和）；拿不到就返回 0（只用于日志）。</summary>
        private static long PathSizeSafe(string path)
        {
            try
            {
                if (File.Exists(path)) return new FileInfo(path).Length;
                if (Directory.Exists(path))
                {
                    long sum = 0;
                    foreach (string f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    {
                        try { sum += new FileInfo(f).Length; } catch { }
                    }
                    return sum;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>「仅移除记录」：只把任务从列表拿掉，不动磁盘上的文件。</summary>
        private void OnTaskRemoveRecord(object sender, RoutedEventArgs e)
        {
            // 支持 Shift 离散多选：一次移除多条记录。
            var all = TasksFromMenu(sender);
            if (all.Count == 0) return;
            RemoveTaskRecords(all, askRunning: true);
        }

        /// <summary>移除单条任务记录（不做确认，确认由调用方统一做一次）。</summary>
        private void RemoveTaskRecord(TaskItem t)
        {
            if (t == null) return;
            // ⚠️ 【2026-10-03 修】原来这里**漏了 Paused**：暂停中的任务被移除记录后，
            // 它的 Job 仍然挂在调度器里（worker 阻塞在 PauseToken 的闸门上），
            // 于是 RunAllAsync 永远不返回 → `_batchRunning` 一直是 true
            // → 用户退出时永远弹「还有下载任务正在运行」，而且它还会留在
            // 「未完成任务」记录里，下次启动又被当成可续传任务。
            // 用户原话：「我中途暂停并且删除了，退出后为什么会显示还有任务正在运行，
            //           这意味着续传也会在我重建软件的时候出现呗」—— 确实如此。
            if (t.State == JobState.Downloading || t.State == JobState.Queued
                || t.State == JobState.Paused || t.State == JobState.Cancelling)
            {
                // 暂停中的任务必须先恢复再取消：worker 正卡在闸门上等 Resume，
                // 只发 Cancel 的话它要等闸门放开才会看到取消标志。
                if (t.State == JobState.Paused)
                {
                    try { t.Job.ResumeJob(); } catch { }
                }
                t.Job.CancelJob();
                t.State = JobState.Cancelling;
            }
            _tasks.Remove(t);
            Log("已移除记录（本地文件保留）: " + t.Name);
        }

        /// <summary>
        /// 「删除本地文件」：清掉目标文件 + 分片目录 + .qmeta。
        ///
        /// 与 Python 版 `_task_del_local` 对齐。关键细节：
        ///  - 先取消后台任务，否则 worker 还在写，删了又会重新创建；
        ///  - 文件被外部程序（播放器、资源管理器预览）占用时，用 MoveFileEx
        ///    登记为**重启后自动删除**，而不是直接报一个用户看不懂的错误。
        /// </summary>
        /// <summary>
        /// 「删除本地文件」= **不要这个任务了**：先取消下载，再删掉本地文件与分片。
        ///
        /// 【2026-10-03 用户明确】「在任务中一个任务正在下载中当我选择删除本地文件，
        /// 就相当于我不要这个任务了，所以会进行取消下载并完成删除本地文件。
        /// 日志上只会显示已成功删除本地文件XXX」。
        ///
        /// ⚠️ 取消条件里**必须包含 Paused**：暂停中的任务 Job 仍挂在调度器上
        /// （worker 阻塞在 PauseToken 闸门），只把任务从列表移走而不取消，
        /// `RunAllAsync` 就永不返回 → `_batchRunning` 一直 true
        /// → 退出时永远提示"还有任务在运行"、下次启动又被当成可续传任务。
        /// （同样的坑在「仅移除记录」上已经踩过一次，见那里的注释。）
        /// ⚠️ 支持 Shift 离散多选后一次删多个。
        /// </summary>
        private void OnTaskDeleteLocal(object sender, RoutedEventArgs e)
        {
            var list = TasksFromMenu(sender)
                .Where(x => x?.Job != null && !string.IsNullOrEmpty(x.Job.Dest))
                .ToList();
            if (list.Count == 0) return;

            string body = list.Count == 1
                ? list[0].Job.Dest
                : string.Join("\n", list.Take(8).Select(x => "· " + x.Name))
                  + (list.Count > 8 ? "\n…" : "");
            var ans = AppDialog.Show(this,
                string.Format("确定删除{0}本地文件吗？\n\n{1}\n\n" +
                              "（正在下载的会先取消；分片与续传元数据一并清理，不可撤销）",
                    list.Count == 1 ? "以下" : list.Count + " 个任务的", body),
                "删除本地文件", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (ans != MessageBoxResult.Yes)
                return;

            DeleteTaskLocals(list);
        }

        private void DeleteLocalFiles(string dest, string name)
        {
            int deleted = 0, delayed = 0;
            var failed = new List<string>();

            // 分片目录 + 续传元数据的清理。
            //
            // 【曾经的 Bug】这里原来写死 `dest + ".qparts"` / `dest + ".qmeta"`，
            // **完全没有考虑「默认分片位置」设置** —— 一旦用户在设置页把分片根目录
            // 指到别处（或事后改过），分片实际落在 `{root}\{slug}_{fnv8}\parts`，
            // 而删除逻辑还在删目标旁边的 `.qparts`，于是"分片根本没删掉"。
            //
            // 现在改为复用下载器**同一份**路径推导（SegmentedDownloader.ResolvePaths），
            // 并把「当前 + 历史用过的」全部根目录都覆盖到 —— 用户反复改过设置时，
            // 散落在多个旧根目录下的残片也能一次清干净。
            foreach (string p in SegmentedDownloader.EnumeratePartialPaths(dest, AllPartsRoots()))
            {
                try
                {
                    if (Directory.Exists(p))
                    {
                        Directory.Delete(p, true);
                        deleted++;
                    }
                    else if (File.Exists(p))
                    {
                        File.Delete(p);
                        deleted++;
                    }
                }
                catch (Exception ex) { failed.Add(p + " — " + ex.Message); }
            }

            // 目标文件本身
            try
            {
                if (File.Exists(dest))
                {
                    File.Delete(dest);
                    deleted++;
                }
            }
            catch (IOException)
            {
                // 被占用（播放器/预览）：登记重启后删除，比直接失败体验好得多。
                if (NativeMethods.MoveFileEx(dest, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT))
                    delayed++;
                else
                    failed.Add(dest + " — 被其他程序占用，且登记重启删除失败（可能需要管理员权限）");
            }
            catch (Exception ex) { failed.Add(dest + " — " + ex.Message); }

            // 【2026-10-20】顺手清理因此变空的目录（只到下载根目录为止）。
            // 用户反馈：「删除本地文件后，对应的文件夹还留着空壳」。
            // ⚠️ 只删**空**目录：里面还有别的文件就立刻停手，绝不误删用户放进去的东西；
            //    同时用 StartsWith 守住下载根目录，老任务（Dest 在别的盘/别的目录）不会越界。
            try
            {
                string root = Path.GetFullPath(_settings.OutDir)
                                  .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string dir = Path.GetDirectoryName(dest);
                while (!string.IsNullOrEmpty(dir))
                {
                    string full = Path.GetFullPath(dir)
                                      .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) break;   // 根目录本身不删
                    if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        break;
                    if (!Directory.Exists(full))
                    {
                        dir = Path.GetDirectoryName(full);
                        continue;
                    }
                    if (Directory.EnumerateFileSystemEntries(full).Any()) break;   // 还有别的东西 → 停
                    Directory.Delete(full);
                    LogFromWorker("已删除空文件夹「" + Path.GetFileName(full) + "」");
                    dir = Path.GetDirectoryName(full);
                }
            }
            catch { }

            // 日志尽量只有一行（用户要求：「日志上只会显示已成功删除本地文件XXX」）。
            // 出问题时才追加说明 —— 那种情况必须让用户看到。
            if (failed.Count == 0 && delayed == 0)
            {
                LogFromWorker("已成功删除本地文件「" + name + "」");
            }
            else
            {
                LogFromWorker(string.Format("已删除本地文件「{0}」{1}{2}",
                    name,
                    delayed > 0 ? "（" + delayed + " 项正被占用，已登记为重启后删除）" : "",
                    failed.Count > 0 ? "，" + failed.Count + " 项失败：" + string.Join("；", failed) : ""));
            }
        }

        /// <summary>
        /// 把用户手输的目录规范化：去空白/去首尾引号，并统一分隔符。
        ///
        /// <para>【为什么必须做】用户可以在设置页**手输**下载目录，很容易输成
        /// `C:/Users/<用户名>/Downloads`（正斜杠）。而 `Path.Combine` 拼文件名时用的是 `\`，
        /// 于是得到 `C:/Users/<用户名>/Downloads\xxx.mp4` 这种**混合斜杠**路径。
        /// Windows 自己认，但**外部程序不一定认** —— VLC 就会把 `C:/…` 当成 URL
        /// 协议头（`c:` scheme）而打不开文件（2026-10-04 用户实测）。</para>
        /// </summary>
        private static string NormalizeDir(string dir)
        {
            dir = (dir ?? "").Trim().Trim('"').Trim();
            if (dir.Length == 0) return "";
            try { return Path.GetFullPath(dir); } catch { return dir; }
        }

        /// <summary>
        /// 「打开」：交给**系统默认程序**处理。
        ///
        /// ⚠️ 这不是"在 GeZi 里播放" —— 内嵌播放会受 WPF MediaElement 限制
        /// （底层是 Windows Media Player，不支持 mkv/flv）。
        /// 交给系统则**没有格式限制**：装了 VLC 就用 VLC，只装系统自带的就用
        /// 「电影和电视」，都没装则弹「选择打开方式」。
        ///
        /// 边下边播时，只要目标文件已按序增长到可播位置，这里就能直接看。
        /// </summary>
        private void OnOpenTaskFile(object sender, RoutedEventArgs e)
        {
            var item = (sender as System.Windows.Controls.Button)?.Tag as TaskItem
                       ?? TaskFromMenu(sender);
            if (item != null)
                OpenTaskFileCore(item);
        }

        private void OpenTaskFileCore(TaskItem item)
        {
            string dest = item?.Job?.Dest;
            if (string.IsNullOrEmpty(dest)) return;

            if (!System.IO.File.Exists(dest))
            {
                Log("文件还没写盘，暂时打不开：" + dest);
                return;
            }

            bool isVideo = MediaProbe.IsVideo(dest);

            long curSize = 0;
            try { curSize = new System.IO.FileInfo(dest).Length; } catch { }

            // ---- 非视频（zip / pdf / jpg / epub / mp3 …）：交给系统默认程序 ----
            // 【2026-10-20 用户要求】这类文件交给 VLC 毫无意义（只会弹"无法播放"）。
            // ⚠️ 必须用 OpenWithDefaultApp 而**不是** OpenFile —— 后者只要探测到 VLC
            //    就会一律用 VLC 打开（那是为视频设计的），epub 会被错误地丢给 VLC
            //    （用户实测反馈：「epub 点击打开还是用 VLC 打开，正常应该用 Readest」）。
            if (!isVideo)
            {
                if (curSize <= 0)
                {
                    Log("文件还是 0 字节，暂时打不开：" + dest);
                    return;
                }
                try
                {
                    if (!ShellLaunch.OpenWithDefaultApp(dest))
                    {
                        Log("打开失败（文件当前 " + Util.FormatSize(curSize) + "），请手动到下载目录查看：" + dest);
                        AppDialog.Show(this,
                            "无法打开文件，请手动到下载目录查看。\n\n" + dest,
                            "需要手动打开",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Information);
                    }
                }
                catch (Exception ex) { Log("打开失败: " + ex.Message); }
                return;
            }

            // ---- 以下都是视频 ----

            // 【2026-10-04 用户反馈】「在软件内点播放无法播放这个文件，但从下载目录里
            //  右键用播放器打开又能看」。
            //  实测：文件本身是**完好的**（987.72MB 全盘扫描无空洞），VLC 也能正常打开。
            //  真正的原因是**点的时机太早** —— 「独立分片（边下边播）」模式的目标文件
            //  是从头**按序拼**出来的：前面的分片还没下到时，文件可能是 0 字节或只有一小段，
            //  播放器只会甩一句「无法播放此文件」，用户很容易误以为文件坏了、白删重下。
            //  → 先看一眼实际长度：还是空的就直接说清楚，别把播放器的报错丢给用户。
            if (curSize <= 0)
            {
                Log("这个文件目前是 0 字节，还没到能播放的程度：" + dest);
                AppDialog.Show(this,
                    "这个文件现在还是 0 字节，播放器打不开它。\n\n"
                    + "「独立分片（边下边播）」模式下，目标文件是从头按顺序拼出来的：\n"
                    + "前面的分片还没下到时，文件就是空的。\n"
                    + "等进度涨起来再点「播放」就可以看了。",
                    "暂时还播不了",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            // 还没下完就点播放：优先走「本地 HTTP 串流 + PotPlayer/VLC」。
            // 旧做法是直接把半截文件交给播放器：它读到当前 EOF 就停，MP4 索引在
            // 尾部时更是直接判损坏 —— 用户看到的往往是一句"无法播放此文件"。
            if (item.State != JobState.Completed)
            {
                if (TryStartStreamingPlayback(item, dest))
                    return;

                Log(string.Format(
                    "提示: 该文件还没下完（当前 {0} / {1}）。独立分片模式下目标文件是按顺序"
                    + "拼出来的前缀，若视频索引在文件末尾，播放器会提示「无法播放此文件」—— "
                    + "这属于正常现象，等进度更多（或下完）再试即可。",
                    Util.FormatSize(curSize), Util.FormatSize(item.Total)));
            }

            try
            {
                // 走统一入口（内部全用 CreateProcess + UseShellExecute=false），
                // 绕开系统文件关联——这条经验来自今天实测：用户在机器上重启后
                // Python Flet 能用 / GeZi 不能用，差异就在 Process.Start 重载默认走
                // ShellExecuteEx 依赖关联，而关联层在某些机器上坏掉。
                if (!ShellLaunch.OpenFile(dest))
                {
                    Log("打开失败（文件当前 " + Util.FormatSize(curSize) + "），请手动到下载目录查看：" + dest);
                    AppDialog.Show(this,
                        "无法打开文件，请手动到下载目录查看。\n\n" + dest,
                        "需要手动打开",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                }
            }
            catch (Exception ex) { Log("打开失败: " + ex.Message); }
        }

        /// <summary>
        /// 未下完的视频：走本地 HTTP 串流 + PotPlayer/VLC，而不是把半截文件
        /// 直接交给播放器。返回 true 表示"已经处理（或已明确告知）"，外层不要再回退。
        /// </summary>
        private bool TryStartStreamingPlayback(TaskItem item, string dest)
        {
            if (item == null || item.Job == null) return false;
            if (!MediaProbe.IsVideo(dest)) return false;

            // 【2026-10-20】落盘方式已唯一化为「独立分片 + 边合并」，
            // 目标文件从 0 连续增长，恒可边下边播，不再需要判 WriteMode。

            // 上次运行留下的占位行没有活动下载器，流视图取不到 —— 先恢复，
            // 恢复后是新的一行，再点播放即可。
            if (item.Dormant)
            {
                var ans = AppDialog.Show(this,
                    "这个任务是上次运行留下的，需要先恢复下载才能边下边播。\n\n现在恢复吗？",
                    "边下边播", System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question,
                    System.Windows.MessageBoxResult.Yes);
                if (ans != System.Windows.MessageBoxResult.Yes) return true;
                ResumeTask(item);
                AppDialog.Show(this,
                    "已开始恢复下载。等任务开始写入后，再点一次「播放」即可边下边播。",
                    "边下边播");
                return true;
            }

            var map = item.Job.GetStreamMap == null ? null : item.Job.GetStreamMap();
            if (map == null)
            {
                Log("下载器还没开始写盘，暂时无法边下边播。");
                return false;
            }

            if (item.State == JobState.Paused)
            {
                var ans = AppDialog.Show(this,
                    "任务当前已暂停。边下边播需要继续下载，是否恢复并播放？",
                    "边下边播", System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question,
                    System.Windows.MessageBoxResult.Yes);
                if (ans != System.Windows.MessageBoxResult.Yes) return true;
                ResumeTask(item);
            }
            else if (item.State == JobState.Failed || item.State == JobState.Cancelled)
            {
                Log("任务已停止，边下边播只能播放已下载的部分：" + item.Name);
            }

            // MP4 的 moov 常在文件尾部：先主动把最后 16MB 提成优先分片，
            // 播放器一读索引就能马上拿到，不用干等顺序下载。
            if (MediaProbe.IsMp4Family(dest) && !MediaProbe.IsMp4IndexAvailable(dest)
                && item.Job.BoostRange != null && item.Total > 0)
            {
                long tailStart = Math.Max(0, item.Total - 16L * 1024 * 1024);
                item.Job.BoostRange(tailStart, item.Total - 1);
                Log("MP4 索引在文件尾部，已优先下载最后 16MB。");
            }

            string url = LocalStreamServer.Instance.Publish(
                map.Total, map.Dest, map.ReadAt,
                () => item.State == JobState.Completed,
                () => item.State == JobState.Downloading || item.State == JobState.Queued,
                (s, e) =>
                {
                    try
                    {
                        var boost = item.Job == null ? null : item.Job.BoostRange;
                        return boost != null && boost(s, e);
                    }
                    catch { return false; }
                });
            if (string.IsNullOrEmpty(url))
            {
                Log("边下边播服务启动失败。");
                return false;
            }

            var player = PlayerLocator.Find();
            if (player == null)
            {
                LocalStreamServer.Instance.Unpublish(url);
                Log("未检测到 VLC，无法边下边播。");
                AppDialog.Show(this,
                    "边下边播需要 VLC（播放器要能直接打开 HTTP 地址）。\n\n"
                    + "装好 VLC 后，在设置页点「重新检测」即可。",
                    "未检测到 VLC",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return true;
            }

            if (!ShellLaunch.OpenPlayerUrl(player, url))
            {
                LocalStreamServer.Instance.Unpublish(url);
                Log("启动播放器失败：" + player.ExePath);
                return false;
            }

            Log("边下边播已启动（" + player.Name + "）：" + url);
            return true;
        }

        /// <summary>「定位」：在资源管理器中**选中**该文件；还没写盘就打开所在目录。</summary>
        private void OnLocateTaskFile(object sender, RoutedEventArgs e)
        {
            var item = (sender as System.Windows.Controls.Button)?.Tag as TaskItem
                       ?? TaskFromMenu(sender);
            string dest = item?.Job?.Dest;
            if (string.IsNullOrEmpty(dest)) return;

            // 走 Shell API 而不是 Process.Start("explorer.exe", "/select,...")：
            // 后者会另起 explorer 进程，用户实测报过 0xc0000142（进程启动即崩）。
            // 详见 ShellReveal 的注释。
            if (!ShellReveal.Reveal(dest))
                Log("定位失败：找不到该文件或所在目录（" + dest + "）");
        }

        /// <summary>
        /// 按路径打开文件（供历史窗口复用「打开」逻辑）。
        /// 同样按格式分流：视频用播放器，其它用系统默认程序（见 OpenTaskFileCore 的说明）。
        /// </summary>
        private void OpenTaskFileByPath(string path)
        {
            bool ok = MediaProbe.IsVideo(path)
                ? ShellLaunch.OpenFile(path)              // 视频：优先 VLC
                : ShellLaunch.OpenWithDefaultApp(path);   // 其它：系统默认关联程序
            if (!ok)
                Log("打开失败，请手动到下载目录查看：" + path);
        }

        // ---------------- 启动时的未完成任务提示 ----------------

        // ---------------- 诊断面板 ----------------

        /// <summary>
        /// 最近一次诊断快照（按任务名分组，最新的覆盖）。
        /// 下载线程高频回调（每 5 秒 × N 个任务），**只存数据不碰 UI**，
        /// 由面板定时器去读 —— 避免高频跨线程 UI 更新。
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DiagnosticsSnapshot>
            _diagLatest = new System.Collections.Concurrent.ConcurrentDictionary<string, DiagnosticsSnapshot>(
                StringComparer.OrdinalIgnoreCase);
        private DiagnosticsSnapshot _diagLast;
        private Window _diagWindow;

        /// <summary>
        /// 下载器诊断回调（在下载线程上执行）。
        ///
        /// <para>【2026-10-20 修】原来这里只写字典，**不碰 UI**；而"连接车道"必须把
        /// 活跃连接数喂给对应的 <see cref="TaskItem"/>。但这里是**下载线程**，
        /// 直接改 TaskItem 会抛跨线程异常 —— 而外层有 <c>catch {}</c> 兜底，
        /// 异常会被**静默吞掉**，表现为"车道永远不亮"（原仓库踩过这个坑，注释有记录）。
        /// 所以：先封送到 UI 线程再写属性。</para>
        /// </summary>
        private void OnDiagnostics(DiagnosticsSnapshot snap)
        {
            if (snap == null) return;
            try
            {
                _diagLast = snap;
                string key = string.IsNullOrEmpty(snap.Name) ? "(未命名)" : snap.Name;
                _diagLatest[key] = snap;

                // 喂连接车道：必须回到 UI 线程（TaskItem 的属性变更通知绑定在界面上）
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action<DiagnosticsSnapshot>(OnDiagnostics), snap);
                    return;
                }
                ApplyDiagnosticsToTask(snap);
            }
            catch { }
        }

        /// <summary>
        /// 把一条诊断快照喂给对应的任务行（连接车道）。
        ///
        /// <para>匹配键必须用**目标文件完整路径** <c>Job.Dest</c>，不能用文件名 ——
        /// SanitizeFileName / UniquePath 会改名，按名字匹配会串到别的任务上。
        /// 找不到再退一步按名字兜底（仅当名字唯一时）。</para>
        /// </summary>
        private void ApplyDiagnosticsToTask(DiagnosticsSnapshot snap)
        {
            if (snap == null) return;

            TaskItem hit = null;
            if (!string.IsNullOrEmpty(snap.Dest))
            {
                foreach (var t in _tasks)
                {
                    if (t.Job != null && !string.IsNullOrEmpty(t.Job.Dest)
                        && string.Equals(t.Job.Dest, snap.Dest, StringComparison.OrdinalIgnoreCase))
                    {
                        hit = t;
                        break;
                    }
                }
            }
            if (hit == null && !string.IsNullOrEmpty(snap.Name))
            {
                foreach (var t in _tasks)
                {
                    if (t.Job != null && string.Equals(t.Job.Name, snap.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (hit != null) { hit = null; break; }   // 名字不唯一 → 放弃兜底
                        hit = t;
                    }
                }
            }

            if (hit != null)
            {
                hit.ActiveConnections = snap.ActiveConnections;
                hit.WorkerCount = snap.WorkerCount;
            }
        }

        /// <summary>
        /// 打开独立诊断面板（移植并强化 Python Flet 版的诊断输出）。
        ///
        /// <para>以前只有日志文本框，指标混在几百行日志里，要"看出来"得靠肉眼逐行扫。
        /// 面板把这些指标整理成表格并每 2 秒刷新 —— 一眼就能判断
        /// "是服务端限速还是客户端没发满连接"。</para>
        /// </summary>
        private void OnOpenDiagnostics(object sender, RoutedEventArgs e)
        {
            if (_diagWindow != null)
            {
                try { _diagWindow.Activate(); return; } catch { _diagWindow = null; }
            }

            var win = new Window
            {
                Owner = this,
                Title = "下载诊断",
            };
            DialogChrome.StyleWindow(win, 1020, 560);

            var root = new Grid { Margin = new Thickness(20, 18, 20, 18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = DialogChrome.PageTitle("下载诊断");
            Grid.SetRow(title, 0);
            root.Children.Add(title);

            var head = DialogChrome.Hint(
                "指标由下载器每 5 秒上报一次。判断口径：\n"
                + "  · 活跃连接接近「并发上限」而速度低 → 服务端限速（客户端无解）\n"
                + "  · 活跃连接明显偏低 → 客户端没把连接发满（可调高线程数）\n"
                + "  · 对端 IP 只有 1 个 → DNS 没分散，慢连接抢占重连仍是同节点\n"
                + "  · 已抢占次数持续增长 → 存在长尾慢连接，抢占正在起作用",
                0, 12);
            Grid.SetRow(head, 1);
            root.Children.Add(head);

            var grid = new DataGrid
            {
                IsReadOnly = true,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                BorderThickness = new Thickness(0),
                FontSize = 12,
                RowHeight = 30,
                Background = Brushes.Transparent,
                HorizontalGridLinesBrush = (Brush)(TryFindResource("BorderBrushSoft") ?? Brushes.Gainsboro),
                Foreground = (Brush)(TryFindResource("TextPrimaryBrush") ?? Brushes.Black),
            };
            // 主题化：WPF 默认的 DataGrid 是"系统表格"观感（灰渐变表头 + 蓝底选中格），
            // 放在这套自绘界面里非常突兀（用户反馈「这个地方的 ui 也太丑了」）。
            var hdrStyle = TryFindResource("DiagColumnHeader") as Style;
            if (hdrStyle != null) grid.ColumnHeaderStyle = hdrStyle;
            var rowStyle = TryFindResource("DiagRow") as Style;
            if (rowStyle != null) grid.RowStyle = rowStyle;
            var cellStyle = TryFindResource("DiagCell") as Style;
            if (cellStyle != null) grid.CellStyle = cellStyle;
            grid.SelectionMode = DataGridSelectionMode.Single;
            grid.SelectionUnit = DataGridSelectionUnit.FullRow;
            grid.CanUserReorderColumns = false;
            grid.CanUserResizeRows = false;
            // 「对端 IP」那一列内容最长（可能好几个 IP），原来是 2* 只有 ~190px，
            // 用户反馈「由于内容太长无法全部显示」。两处一起改：
            //   ① 列权重提到 3*，并从「进度」「分片」里挤一点宽度出来；
            //   ② 单元格允许**换行**（行高会自动长高），再挂 ToolTip 兜底。
            // ⚠️ DataGridTextColumn 的换行/提示要靠 ElementStyle 设，直接给 Column 设没用。
            grid.Columns.Add(new DataGridTextColumn { Header = "任务", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "速度", Binding = new System.Windows.Data.Binding("Speed"), Width = new DataGridLength(84) });
            grid.Columns.Add(new DataGridTextColumn { Header = "活跃/上限", Binding = new System.Windows.Data.Binding("Conns"), Width = new DataGridLength(84) });
            grid.Columns.Add(new DataGridTextColumn { Header = "分片", Binding = new System.Windows.Data.Binding("Parts"), Width = new DataGridLength(84) });
            grid.Columns.Add(new DataGridTextColumn { Header = "进度", Binding = new System.Windows.Data.Binding("Progress"), Width = new DataGridLength(120) });
            grid.Columns.Add(new DataGridTextColumn { Header = "抢占", Binding = new System.Windows.Data.Binding("Preempt"), Width = new DataGridLength(56) });
            var ipCol = new DataGridTextColumn
            {
                Header = "对端 IP",
                Binding = new System.Windows.Data.Binding("PeerIps"),
                Width = new DataGridLength(3, DataGridLengthUnitType.Star),
            };
            var ipCell = new Style(typeof(System.Windows.Controls.TextBlock));
            ipCell.Setters.Add(new Setter(System.Windows.Controls.TextBlock.TextWrappingProperty,
                TextWrapping.Wrap));
            ipCell.Setters.Add(new Setter(System.Windows.Controls.TextBlock.VerticalAlignmentProperty,
                VerticalAlignment.Center));
            ipCell.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,
                new System.Windows.Data.Binding("PeerIps")));
            ipCol.ElementStyle = ipCell;
            grid.Columns.Add(ipCol);
            var gridCard = DialogChrome.Card(grid);
            gridCard.Padding = new Thickness(0);
            Grid.SetRow(gridCard, 2);
            root.Children.Add(gridCard);

            var copyBtn = DialogChrome.SecondaryButton("复制诊断文本");
            var clearBtn = DialogChrome.SecondaryButton("清空");
            var closeBtn = DialogChrome.SecondaryButton("关闭");
            var btns = DialogChrome.ButtonRow(copyBtn, clearBtn, closeBtn);
            btns.Margin = new Thickness(0, 14, 0, 0);
            Grid.SetRow(btns, 3);
            root.Children.Add(btns);

            win.Content = root;

            var rows = new ObservableCollection<DiagRow>();
            grid.ItemsSource = rows;

            var timer = new DispatcherTimer(TimeSpan.FromSeconds(2),
                DispatcherPriority.Background, (s, ev) =>
                {
                    try { RefreshDiagRows(rows); } catch { }
                }, Dispatcher);
            timer.Start();

            copyBtn.Click += (s, ev) =>
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("文件\t速度\t活跃/上限\t分片\t进度\t抢占\t对端IP");
                    foreach (var r in rows)
                        sb.AppendLine(string.Join("\t", r.Name, r.Speed, r.Conns, r.Parts, r.Progress, r.Preempt, r.PeerIps));
                    Clipboard.SetText(sb.ToString());
                    Log("已复制诊断文本到剪贴板。");
                }
                catch { }
            };
            clearBtn.Click += (s, ev) => { _diagLatest.Clear(); rows.Clear(); };

            win.Closed += (s, ev) => { try { timer.Stop(); } catch { } _diagWindow = null; };
            _diagWindow = win;
            win.Show();
        }

        /// <summary>把最新快照刷进面板行（面板定时器调用，已在 UI 线程）。</summary>
        private void RefreshDiagRows(ObservableCollection<DiagRow> rows)
        {
            var snaps = _diagLatest.Values
                .OrderByDescending(s => s.Speed)
                .ToList();

            // 按需增删，避免每次重建导致选中态/滚动位置丢失
            while (rows.Count > snaps.Count) rows.RemoveAt(rows.Count - 1);
            for (int i = 0; i < snaps.Count; i++)
            {
                if (i >= rows.Count) rows.Add(new DiagRow());
                rows[i].Update(snaps[i]);
            }
        }

        /// <summary>诊断面板的一行（把快照格式化成可读文本）。</summary>
        private sealed class DiagRow : INotifyPropertyChanged
        {
            public string Name { get; private set; } = "";
            public string Speed { get; private set; } = "";
            public string Conns { get; private set; } = "";
            public string Parts { get; private set; } = "";
            public string Progress { get; private set; } = "";
            public string Preempt { get; private set; } = "";
            public string PeerIps { get; private set; } = "";

            public void Update(DiagnosticsSnapshot s)
            {
                Name = s.Name ?? "";
                Speed = Util.FormatSize((long)s.Speed) + "/s";
                Conns = s.ActiveConnections + "/" + s.WorkerCount;
                Parts = s.PartsDone + "/" + s.PartsTotal;
                string pct = s.TotalBytes > 0
                    ? (s.DoneBytes * 100.0 / s.TotalBytes).ToString("F1") + "%"
                    : "-";
                Progress = pct + "  " + Util.FormatSize(s.DoneBytes);
                Preempt = s.PreemptedCount.ToString();
                PeerIps = s.PeerIps ?? "";

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        /// <summary>
        /// 启动时在**后台线程**清扫过期分片残留（移植自 Python Flet 版的 `_sweep_stale_parts`）。
        ///
        /// <para>【为什么要做】</para>
        /// <para>
        /// 用户取消下载、改分片目录、或程序被强杀时，分片文件会留在磁盘上。
        /// 此前 C# 版**没有任何自动回收机制** —— 残留只增不减，用户会明显感到
        /// "磁盘越用越少"却找不到元凶（分片目录默认还是隐藏的）。
        /// </para>
        ///
        /// <para>【判定与安全】</para>
        /// <list type="bullet">
        /// <item>只看**文件 mtime**：正在下载的分片 mtime 持续刷新，永远不会被误删；</item>
        /// <item>扫的是 <see cref="AllPartsRoots"/> —— **当前生效 + 历史上用过的**全部根目录，
        ///   只扫当前那个会漏掉旧目录里的残留（这正是过去"分片删不掉"的成因）；</item>
        /// <item>删不掉（被占用/权限不足）就跳过，绝不因此影响下载；</item>
        /// <item>全程 try/catch，任何异常都只记日志。</item>
        /// </list>
        ///
        /// <para>【已知副作用（有意为之）】</para>
        /// <para>
        /// 中断/暂停超过 <see cref="PartsSweeper.DefaultMaxAgeDays"/> 天的下载，
        /// 其分片会在下次启动被清掉，该任务无法续传（需从 0 重下）。
        /// 这是"自动回收磁盘"与"无限期保留断点"之间必须做的取舍。
        /// </para>
        /// </summary>
        private void StartPartsSweep()
        {
            var roots = AllPartsRoots();
            string rootSnapshot = roots.Count == 0 ? "（无自定义根目录）" : string.Join("；", roots);

            var t = new Thread(() =>
            {
                try
                {
                    var r = PartsSweeper.Sweep(roots, PartsSweeper.DefaultMaxAgeDays);
                    if (r.Scanned == 0)
                        return;   // 没有任何分片文件，不必打扰用户

                    if (r.AnythingDone)
                    {
                        LogFromWorker(string.Format(
                            "分片清扫：回收了 {0} 个过期分片（{1}），跳过 {2} 个被占用的文件。",
                            r.Removed, Util.FormatSize(r.FreedBytes), r.Skipped));
                    }
                    else if (r.Skipped > 0)
                    {
                        LogFromWorker(string.Format(
                            "分片清扫：{0} 个过期分片因被占用而跳过（下次启动会重试）。", r.Skipped));
                    }
                }
                catch (Exception ex)
                {
                    try { LogFromWorker("分片清扫失败（已忽略）: " + ex.Message); } catch { }
                }
            })
            {
                IsBackground = true,             // 后台线程：不阻止程序退出
                Name = "GeZi-PartsSweep",
            };
            t.Start();

            Log("已启动分片自动清扫（阈值 " + PartsSweeper.DefaultMaxAgeDays + " 天）。扫描范围: " + rootSnapshot);
        }

        /// <summary>删除文件或目录，失败静默（供丢弃续传用）。</summary>
        private static void TryDeletePath(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        /// <summary>
        /// 批量续传：为每个任务重新取链并入队下载。
        ///
        /// <para>取链按 <see cref="PendingTask.FromShare"/> 分流，复用与首次下载相同的接口。
        /// 任一任务取链失败不影响其他任务（逐条 try/catch 并记日志）。</para>
        /// </summary>
        /// <summary>
        /// 续传前确保拿到一个可用的客户端。
        ///
        /// <para>【为什么要它】启动时的自动登录是异步的，而续传框弹得很早
        /// （Idle 优先级），用户可能先点了「继续下载」。此时全局客户端还没赋值，
        /// 旧代码会直接放弃续传 —— 用户只觉得"点了没反应"。</para>
        ///
        /// <para>顺序：① 已有客户端直接用；② 等启动时那次自动登录（最多 15 秒）；
        /// ③ 还不行就用保存的 Cookie 现建一个（取直链只需要 Cookie，
        /// 昵称/头像的校验可以后补）；④ 都没有才返回 null。</para>
        /// </summary>
        private async Task<QuarkClient> EnsureClientForResumeAsync()
        {
            if (_client != null) return _client;

            Log("续传需要登录态，正在等待自动登录完成…");
            var t = _startupLoginTask;
            if (t != null)
            {
                try { await Task.WhenAny(t, Task.Delay(15000)).ConfigureAwait(true); }
                catch { }
            }
            if (_client != null)
            {
                Log("自动登录已完成，开始续传。");
                return _client;
            }

            // 兜底：保存的 Cookie 足够取直链了（账号校验只影响昵称/头像的显示）
            if (!string.IsNullOrEmpty(_settings.Cookie))
            {
                try
                {
                    Log("自动登录尚未完成，已直接用保存的登录态开始续传。");
                    return new QuarkClient(_settings.Cookie);
                }
                catch (Exception ex)
                {
                    Log("用保存的登录态续传失败: " + ex.Message);
                }
            }
            return null;
        }

        private async Task ResumePendingBatchAsync(List<PendingTask> tasks)
        {
            // 【2026-10-04 用户反馈】「弹出续传窗口时我点继续下载，但左下角的账号卡
            //  还没显示登录，这时会怎么样？」
            // 查证：续传框是在 Idle 优先级弹的，而自动登录（ValidateAsync + 走 Python 桥
            // 取昵称/头像）是**异步**的，很可能还没跑完 —— 此时客户端字段仍是 null，
            // 原来的代码会**直接 return**，只在任务页留一行日志，
            // 用户看到的就是"点了没反应"，而且完全不知道为什么。
            // → 现在：① 先等一会儿自动登录；② 还不行就用保存的 Cookie 现建一个
            //   **局部**客户端（取直链只需要 Cookie，账号昵称/头像的校验可以后补）；
            //   ③ 确实没有登录态才明确弹框告知（而不是静默放弃）。
            // 注意用的是**局部** client：自动登录完成时会换掉全局客户端，
            // 若直接用字段，换的那一刻可能在途请求会被 Dispose 掉。
            var client = await EnsureClientForResumeAsync();
            if (client == null)
            {
                Log("续传需要先登录。请登录后重新下载（分片在，会自然续传）。");
                AppDialog.Show(this,
                    "续传需要重新获取直链，而这必须处于登录状态。\n\n"
                    + "现在还没有可用的登录态，所以这次没有开始续传。\n"
                    + "分片都还在，登录之后再点一次「继续下载」即可（不会重下）。",
                    "需要先登录",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            SetBusy(true);
            var jobs = new List<DownloadJob>();
            try
            {
                // 【2026-10-24 新增】续传/恢复同样要走连接预算分配器。
                // 原来这条路径直接用 _settings.Threads —— 用户实测「7 个任务
                // 每个都开 256 路 = 1792 条连接」，超出夸克账号配额后
                // 表现就是「244/256 连接却只跑 1~2 MB/s」。
                var allocThreads = AllocateThreads(tasks.Select(t => (long)t.Size).ToList());
                int jIdx = 0;

                foreach (var p in tasks)
                {
                    try
                    {
                        string url = null;

                        if (p.FromShare)
                        {
                            // 分享模式：走"先试旧凭证 → 失败自动重解析分享"的自愈路径（2026-10-20）。
                            // stoken 一般几小时就过期，原来一过期续传就整批跳过，用户只能手动重解析。
                            url = await GetShareLinkWithReResolveAsync(
                                client, p.Fid, p.Token, p.PwdId, p.Stoken, p.Passcode,
                                p.StartFid, p.ShareRelDir,
                                // 【2026-10-22】老任务没有 ShareRelDir → 用 GroupName/RelDir 兜底下钻
                                FirstNonEmpty(p.GroupName, p.RelDir), p.Name,
                                (freshStoken, freshToken) =>
                                {
                                    p.Stoken = freshStoken;   // 回填到续传记录，后续 job.Pending 用的就是新值
                                    p.Token = freshToken;
                                }, CancellationToken.None).ConfigureAwait(true);
                                // 🚨 【2026-10-22】必须是 true（回到 UI 线程）。
                                // 这里原来写的是 false —— 于是 await 之后的代码
                                // （_tasks.Remove / AddTaskItem / RunJobs）
                                // 全跑在线程池线程上，改 ObservableCollection 直接抛
                                // NotSupportedException（CollectionView 跨线程），
                                // RunJobs 里改 DependencyProperty 再抛 InvalidOperationException。
                                // 表现为"取链成功但任务停在排队中、速度 0"。
                                // 本方法是 UI 层方法，本来就该全程在 UI 线程 ——
                                // 真正耗时的网络请求在 QuarkClient 内部已用 ConfigureAwait(false)。

                            // 【2026-10-22】还是拿不到 → 走「转存副本没了就回免转存」的兜底。
                            // 场景：转存路径下 p.Fid 是**中转副本**的 fid，用户把副本删了，
                            // 而分享里的源文件还好好的 —— 原来这里直接跳过，任务永远续不上。
                            if (string.IsNullOrEmpty(url))
                            {
                                url = await TryFallbackToShareSourceAsync(client, p, CancellationToken.None)
                                    .ConfigureAwait(true);
                            }
                        }
                        else
                        {
                            // 我的网盘：fid 失效（文件被删）时，同样回退到分享源文件。
                            string myUrl = null;
                            try
                            {
                                var links = await client.GetDownloadAsync(new List<string> { p.Fid });
                                myUrl = links?.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl))?.DownloadUrl;
                            }
                            catch (Exception ex)
                            {
                                Log("[" + p.Name + "] 按网盘 fid 取链失败: " + ex.Message);
                            }
                            url = myUrl;

                            if (string.IsNullOrEmpty(url))
                            {
                                url = await TryFallbackToShareSourceAsync(client, p, CancellationToken.None)
                                    .ConfigureAwait(true);
                            }
                        }

                        if (string.IsNullOrEmpty(url))
                        {
                            Log("[" + p.Name + "] 续传取链失败（分享可能已被取消、删除或改了提取码）。");
                            continue;
                        }

                        // 【2026-10-22 诊断】取到链了，但用户实测"速度一直是 0"。
                        // 打清 URL 的域名 / 长度 / Cookie 长度 —— 若 CDN 域名是
                        // dl-pc-zb 而 Cookie 又没带上，就能立刻看出是请求头问题。
                        Log("[诊断·续传] " + p.Name + " 取链成功"
                            + " host=" + SafeHostOf(url)
                            + " urlLen=" + url.Length
                            + " cookieLen=" + (client.CookieStr == null ? 0 : client.CookieStr.Length)
                            + " fromShare=" + p.FromShare);

                        string dest = p.Dest;
                        try { Directory.CreateDirectory(Path.GetDirectoryName(dest)); } catch { }

                        var job = new DownloadJob
                        {
                            Name = p.Name ?? Path.GetFileName(dest),
                            Url = url,
                            Dest = dest,
                            Cookie = client.CookieStr,
                            // 【2026-10-24 修】原来这里是 `_settings.Threads`（直接 256），
                            // **绕过了连接预算分配器** —— 用户实测「7 个任务每个都开 256 路
                            // = 1792 条连接，远超夸克账号配额，结果 244/256 连接却只跑 1~2 MB/s」。
                            // 恢复/续传路径必须和新任务路径走同一套分配逻辑。
                            Threads = allocThreads != null && jIdx < allocThreads.Length
                                ? allocThreads[jIdx] : _settings.Threads,
                            WriteMode = WriteMode.SegmentedFiles,   // 落盘方式唯一化
                            PartsRoot = string.IsNullOrWhiteSpace(_settings.PartsRoot) ? null : _settings.PartsRoot,
                            Pending = new PendingInfo
                            {
                                PwdId = p.PwdId, Stoken = p.Stoken, Passcode = p.Passcode,
                                Fid = p.Fid, Token = p.Token, FromShare = p.FromShare,
                                RelDir = p.RelDir ?? "", Dest = dest, Size = p.Size,
                                GroupName = p.GroupName ?? "",
                                StartFid = p.StartFid, ShareRelDir = p.ShareRelDir,
                                // 【2026-10-22】源文件定位信息（转存副本没了时可回退免转存）
                                SourceFid = p.SourceFid,
                                SourceName = p.SourceName,
                            },
                        };
                        var captured = job;
                        job.LinkRefresher = ct => RefreshLinkAsyncForPending(captured, ct);
                        // 免转存任务续传取不到新链（stoken 过期）→ 失败文案要说"需重新解析分享"
                        job.ResumeCannotRefresh = p.FromShare;
                        jobs.Add(job);
                        jIdx++;
                    }
                    catch (Exception ex)
                    {
                        Log("[" + (p.Name ?? "?") + "] 续传失败: " + ex.Message);
                    }
                }

                if (jobs.Count == 0)
                {
                    Log("没有任务成功取到直链。");
                    return;
                }

                // 把分配结果打出来 —— 用户要能一眼看到"这次每个任务到底开了几路"，
                // 而不是只能从界面上的 244/256 去猜。
                LogAllocation(allocThreads);

                // 【2026-10-22 诊断】用户实测：取链全部成功（日志有 3 条 [诊断·续传]），
                // 但**完全没有**「已续传 N 个任务」，也没有任何 [诊断·下载] ——
                // 说明流程在这之后、RunJobs 之前就断了。把中间状态打出来。
                Log("[诊断·续传] 取链完成，jobs=" + jobs.Count + " 个，准备入队");

                // 🚨 【2026-10-22 关键修复】入队必须回到 **UI 线程**。
                //
                // 【用户实测】点「▶」续传，日志显示取链成功、进入入队，然后：
                //   NotSupportedException: 该类型的 CollectionView 不支持从调度程序
                //   线程以外的线程对其 SourceCollection 进行的更改。
                //   ……在 ResumePendingBatchAsync 位置 MainWindow.xaml.cs:行号 9600
                // 于是**整个续传流程静默中断** —— 任务不入队、不下载，
                // 界面只剩「下载中：N 个任务（0 个在跑），总速度 0 B/s」。
                //
                // 【根因】本方法里取链那几行用了 `.ConfigureAwait(false)`
                // （见上面 GetShareLinkWithReResolveAsync / GetDownloadAsync 的调用），
                // 它让**后续代码不再回到 UI 线程**，而是继续跑在线程池线程上。
                // 而 `_tasks` 是绑定到 `TaskList.ItemsSource` 的 ObservableCollection，
                // WPF 的 CollectionView 禁止跨线程改它 —— 一改就抛 NotSupportedException。
                //
                // 【为什么以前没暴露】正常下载走的是 `AddTaskItem`（在 UI 事件处理器里
                // 同步执行，天然在 UI 线程）。只有**续传**这条 async 路径会跨线程。
                //
                // 【修法】把"改集合 + 加行 + 启下载"整体切回 UI 线程。
                // 用 InvokeAsync 而不是 BeginInvoke：要让异常能被外层 catch 到。
                await Dispatcher.InvokeAsync(() =>
                {
                    foreach (var job in jobs)
                    {
                        // 去重：任务页里可能已经有一条同路径的行 ——
                        //   · "上次恢复出来的占位行"（Dormant）：续传成功后要把它换掉；
                        //   · "失败/取消的老行"：也会被新的续传行取代，不能并存两行。
                        // 判据：同一 Dest，且那一行**没有在真正下载**。
                        var stale = _tasks.FirstOrDefault(x => x.Job != null
                            && string.Equals(x.Job.Dest, job.Dest, StringComparison.OrdinalIgnoreCase)
                            && (x.Dormant
                                || x.State == JobState.Failed
                                || x.State == JobState.Cancelled
                                || x.State == JobState.Paused
                                || x.State == JobState.Queued));
                        if (stale != null)
                        {
                            Log("[诊断·续传] 移除旧行: " + (stale.Name ?? "?")
                                + "（状态=" + stale.State + " Dormant=" + stale.Dormant + "）");
                            _tasks.Remove(stale);
                        }
                        AddTaskItem(new TaskItem(job, job.Name)
                        {
                            // 【2026-10-22 修】续传入队时**预填磁盘上的真实进度**。
                            //
                            // 【用户实测】续传本身是成功的（分片确实在续），但
                            // 「进度条会一下从 0 跳到继续下载的位置」—— 看着像从头重下。
                            //
                            // 【原因】`new TaskItem(job, name)` 的 Done/Total/Percent
                            // 初始都是 0，要等**第一次进度上报**（OnJobUpdate）才有值。
                            // 而续传任务第一次上报前要经历"建连 → 探测 → 规划分片"，
                            // 中间有几百毫秒到几秒的窗口，这段时间进度条就是 0% ——
                            // 用户正好看到它，于是以为重新开始了。
                            //
                            // 【修法】入队时直接用 `ReadDownloadedBytes` 读 .qmeta，
                            // 把 Done/Percent 先填成"上次下到哪"。
                            // 与 RestoreTaskList（6810 行）用的是同一个数据源，口径一致。
                            Total = job.Pending != null && job.Pending.Size > 0 ? job.Pending.Size : 0,
                            Done = PrefillDone(job.Dest),
                            Percent = PrefillPercent(job.Dest,
                                job.Pending != null ? job.Pending.Size : 0),
                        });
                        Log("[诊断·续传] 已入队: " + job.Name
                            + "（预填进度 " + PrefillPercent(job.Dest,
                                job.Pending != null ? job.Pending.Size : 0).ToString("F1") + "%）");
                    }
                    Log("已续传 " + jobs.Count + " 个任务。");
                });

                // RunJobs 内部会 new Progress<>（捕获当前 SynchronizationContext），
                // 必须在 UI 线程调用，否则进度回调不会回到 UI。
                Log("[诊断·续传] 即将调用 RunJobs(" + jobs.Count + " 个)…");
                RunJobs(jobs);
            }
            catch (Exception ex)
            {
                // 🚨 【2026-10-22 修】这个方法是被 `_ = ResumePendingBatchAsync(...)`
                // fire-and-forget 调用的 —— **没有外层 try** 的话，任何未处理异常
                // 都会随 Task 一起被丢弃（无人 await → 静默），
                // 用户看到的就是"点了续传，日志显示取链成功，然后什么都没有"。
                // 这正是本次实测的现象：3 条 [诊断·续传] 取链成功，
                // 却**完全没有**「已续传 N 个任务」，也没有任何下载日志。
                Log("续传流程出错: " + ex.GetType().Name + ": " + ex.Message);
                Log("[诊断·续传] 堆栈: " + (ex.StackTrace ?? "").Replace("\r", " ").Replace("\n", " | "));
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>
        /// 读磁盘上已下载的字节数（.qmeta 里的真实进度），用于**续传入队时预填进度条**。
        ///
        /// <para>【为什么要预填】续传成功后新建的 TaskItem 初始 Done=0，
        /// 要等第一次进度上报才有值。而首次上报前要"建连 → 探测 → 规划分片"，
        /// 中间几百毫秒~几秒进度条一直是 0% —— 用户会以为在从头重下
        /// （实测反馈："进度条会一下从 0 跳到继续下载的位置"）。</para>
        ///
        /// <para>失败一律返回 0：这只是显示优化，绝不能因为它让续传本身失败。</para>
        /// </summary>
        private long PrefillDone(string dest)
        {
            if (string.IsNullOrEmpty(dest)) return 0;
            try
            {
                return SegmentedDownloader.ReadDownloadedBytes(
                    dest, string.IsNullOrWhiteSpace(_settings.PartsRoot) ? null : _settings.PartsRoot);
            }
            catch { return 0; }
        }

        /// <summary>
        /// 与 <see cref="PrefillDone"/> 配套：把已下字节换算成百分比（0~99.9）。
        /// 总大小未知（≤0）时返回 0，避免除零。
        /// </summary>
        private double PrefillPercent(string dest, long size)
        {
            if (size <= 0) return 0;
            long done = PrefillDone(dest);
            if (done <= 0) return 0;
            return Math.Min(99.9, done * 100.0 / size);
        }

        /// <summary>给"续传任务"用的重新取链回调（基于 Job 上的 Pending 上下文）。</summary>
        private async Task<string> RefreshLinkAsyncForPending(DownloadJob job, CancellationToken ct)
        {
            var p = job.Pending;
            if (p == null || string.IsNullOrEmpty(p.Fid))
                return null;
            try
            {
                List<DownloadLink> links;
                if (p.FromShare)
                {
                    links = await _client.GetShareDownloadAsync(
                        new List<string> { p.Fid },
                        new List<string> { p.Token ?? "" },
                        p.PwdId, p.Stoken);
                }
                else
                {
                    links = await _client.GetDownloadAsync(new List<string> { p.Fid });
                }
                var url = links?.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl))?.DownloadUrl;
                if (!string.IsNullOrEmpty(url)) return url;

                // 【2026-10-22】原路径取不到链 → 回退到分享源文件（免转存）
                return await TryFallbackToShareSourceAsync(_client, p, ct).ConfigureAwait(false);
            }
            catch
            {
                // 原路径失败 → 也回退一次（转存副本可能已被删）
                try { return await TryFallbackToShareSourceAsync(_client, p, ct).ConfigureAwait(false); }
                catch { return null; }
            }
        }

        /// <summary>
        /// 兜底取链：**转存副本没了，就回分享里找源文件，走免转存下载**。
        ///
        /// <para>【场景】2026-10-22 用户实测：转存路径下下载的文件，用户把网盘里的
        /// **中转副本**删了 → 续传时按副本 fid 取链报 "file not found"，
        /// 任务再也续不上。但**分享里的源文件明明还在** —— 白白下不去。</para>
        ///
        /// <para>【做法】依次尝试：
        /// ① 用现有的 stoken 按**源文件 fid**（<see cref="PendingInfo.SourceFid"/>）走免转存取链；
        /// ② stoken 可能已过期 → 重新申请一份；
        /// ③ 再按**文件名**（<see cref="PendingInfo.SourceName"/>）在分享里定位源文件，
        ///    拿到新的 share_fid_token 取链。</para>
        ///
        /// <para>【为什么必须有分享上下文】免转存取链需要 pwd_id + stoken，
        /// 没有这些就无从回退（返回 null）。</para>
        /// </summary>
        /// <returns>取到的直链；失败返回 null。</returns>
        private async Task<string> TryFallbackToShareSourceAsync(
            QuarkClient client, PendingInfo p, CancellationToken ct)
        {
            if (client == null || p == null) return null;
            if (string.IsNullOrEmpty(p.PwdId)) return null;   // 没有分享上下文 → 无从回退

            string stoken = p.Stoken;
            string sourceFid = p.SourceFid;
            string sourceName = p.SourceName;

            // 旧任务的快照里没有 SourceFid/SourceName（2026-10-22 之前创建的），
            // 但**转存路径**的 Fid 一定是中转副本、分享上下文又还在 ——
            // 这时至少还能按文件名去找源文件。
            if (string.IsNullOrEmpty(sourceName)) sourceName = p.Dest;

            try
            {
                // ① 用现有 stoken 按源 fid 走免转存取链
                if (!string.IsNullOrEmpty(sourceFid) && !string.IsNullOrEmpty(stoken))
                {
                    string u = await TryShareDownloadAsync(
                        client, sourceFid, "", p.PwdId, stoken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(u))
                    {
                        Log("中转副本已不可用，已自动改用分享源文件继续下载。");
                        p.Fid = sourceFid;
                        p.FromShare = true;
                        return u;
                    }
                }

                // ② stoken 可能已过期 → 重新申请一份（分享还在的话）
                try
                {
                    var fresh = await client.GetStokenAsync(p.PwdId, p.Passcode ?? "").ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(fresh))
                    {
                        stoken = fresh;
                        p.Stoken = fresh;
                    }
                }
                catch { }
                if (string.IsNullOrEmpty(stoken)) return null;

                // ③ 按**文件名**在分享里定位源文件，拿它的 share_fid_token 取链
                if (!string.IsNullOrEmpty(sourceName))
                {
                    var fileName = Path.GetFileName(sourceName);
                    var entry = await client.FindShareFileByNameAsync(
                        p.PwdId, stoken, fileName).ConfigureAwait(false);
                    if (entry != null && !string.IsNullOrEmpty(entry.Fid))
                    {
                        string u = await TryShareDownloadAsync(
                            client, entry.Fid, entry.Token, p.PwdId, stoken).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(u))
                        {
                            Log("中转副本已不可用，已在分享中重新定位源文件并继续下载。");
                            // 回填上下文，后续重试/续传都用新的
                            p.Fid = entry.Fid;
                            p.Token = entry.Token;
                            p.FromShare = true;
                            return u;
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Log("回退到分享源文件失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// <see cref="TryFallbackToShareSourceAsync(QuarkClient, PendingInfo, CancellationToken)"/>
        /// 的 <see cref="PendingTask"/> 入口（续传批处理用的是 PendingTask）。
        /// </summary>
        private async Task<string> TryFallbackToShareSourceAsync(
            QuarkClient client, PendingTask p, CancellationToken ct)
        {
            if (p == null) return null;
            var info = new PendingInfo
            {
                PwdId = p.PwdId, Stoken = p.Stoken, Passcode = p.Passcode,
                Fid = p.Fid, Token = p.Token, FromShare = p.FromShare,
                Dest = p.Dest, Size = p.Size,
                SourceFid = p.SourceFid, SourceName = p.SourceName,
                StartFid = p.StartFid, ShareRelDir = p.ShareRelDir,
            };
            string url = await TryFallbackToShareSourceAsync(client, info, ct).ConfigureAwait(false);
            // 把回填结果同步回 PendingTask，调用方后续用得到
            p.Fid = info.Fid; p.Token = info.Token; p.Stoken = info.Stoken;
            p.FromShare = info.FromShare;
            return url;
        }


        // ---------------- 检查更新（GitHub Releases） ----------------

        /// <summary>
        /// 启动检查确认有新版本后的回调（UI 线程）。
        /// 只做两件"不抢焦点"的事：应用内气泡 + 日志行；
        /// 真正的更新详情窗等用户去「关于 → 检查更新」才出现。
        /// </summary>
        private void OnUpdateFound(IReleaseInfo m)
        {
            try
            {
                if (m == null) return;
                string size = m.PackageSize > 0
                    ? "，" + (m.PackageSize / 1048576.0).ToString("0.0") + " MB"
                    : "";
                string srcLabel = UpdateService.SourceLabel(m.SourceName);
                NotifyToast.Show("发现新版本 v" + m.Version,
                    "打开「关于」点「检查更新」查看更新说明并下载（来源：" + srcLabel + "）" + size, null);
                Log("发现新版本 v" + m.Version + "（" + m.TagName + "，来源：" + srcLabel
                    + "）— 可在「关于」页点击「检查更新」。");

                // 【2026-10-21】设置页现在也有正式的更新入口，
                // 气泡文案跟着改，并且把设置页的状态行/红点一起刷新。
                RefreshSettingsUpdateUi();
            }
            catch { }
        }

        // ---------------- 设置页「关于与更新」（2026-10-21 新增） ----------------
        //
        // 【为什么要有】用户反馈："不是说接入了 GitHub 自动更新吗，在设置页没看到相关检查更新 UI"。
        // 原因：更新功能确实接好了（UpdateService + AboutWindow），但入口只有一个 ——
        // 侧栏品牌区那个「?」按钮打开「关于」窗口后，版本号旁边一个 10.5px 的小按钮。
        // 对用户来说等于不存在。设置页是"找功能"的默认去处，这里给一个正式入口。

        private bool _settingsCheckingUpdate;

        /// <summary>
        /// 状态行文案是否已被 <see cref="OnSettingsCheckUpdate"/> 的 catch 分支"钉住"。
        /// <para>为什么需要：<see cref="RefreshSettingsUpdateUi"/> 会把状态行重置成默认/有新版本的文案，
        /// 而它会在 finally 里再跑一次 —— 那正好把刚写好的「检查失败：…」抹掉，
        /// 用户看到的就是"点了没反应"。</para>
        /// </summary>
        private bool _settingsUpdateStatusPinned;

        /// <summary>
        /// 刷新设置页的版本号 / 红点 / 状态文案（不联网，纯读内存状态）。
        /// 可在任意时刻重复调用：启动完成、手动检查后、打开设置页时。
        /// </summary>
        private void RefreshSettingsUpdateUi()
        {
            try
            {
                if (SettingsVersionText != null)
                {
                    var v = UpdateService.CurrentVersion;
                    SettingsVersionText.Text = v != null ? "v" + v.ToString() : "v—";
                }

                if (SettingsUpdateDot != null)
                {
                    SettingsUpdateDot.Visibility = UpdateService.HasUpdate
                        ? System.Windows.Visibility.Visible
                        : System.Windows.Visibility.Collapsed;
                }

                if (SettingsUpdateStatusText != null && !_settingsCheckingUpdate)
                {
                    // 注意：Latest 在重启后是 null（落盘的只有版本号字符串），
                    // 所以这里只凭 HasUpdate + 落盘的版本号说话，不假设能拿到更新说明。
                    if (UpdateService.HasUpdate)
                    {
                        string latest = _settings != null ? _settings.UpdateLatestVersion : "";
                        SettingsUpdateStatusText.Text = string.IsNullOrEmpty(latest)
                            ? "发现新版本，点「检查更新」查看更新说明。"
                            : "发现新版本 v" + latest + "，点「检查更新」查看更新说明。";
                    }
                    else
                    {
                        SettingsUpdateStatusText.Text = "从 GitHub Releases 检查新版本";
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 设置页「检查更新」：语义与「关于」窗口里的那个按钮完全一致 ——
        /// 不节流、失败如实告知、有新版本弹详情窗。
        /// </summary>
        private async void OnSettingsCheckUpdate(object sender, RoutedEventArgs e)
        {
            if (_settingsCheckingUpdate) return;
            _settingsCheckingUpdate = true;
            if (SettingsCheckUpdateBtn != null) SettingsCheckUpdateBtn.IsEnabled = false;

            try
            {
                if (SettingsUpdateStatusText != null)
                    SettingsUpdateStatusText.Text = "正在检查…";

                var m = await UpdateService.CheckNowAsync();

                if (UpdateService.Settings != null)
                    UpdateService.Apply(UpdateService.Settings, m, null);
                _settingsCheckingUpdate = false;
                RefreshSettingsUpdateUi();

                // 有新版本、但之前被忽略过 → 先问要不要恢复提醒（与关于页同款交互）
                if (m != null && UpdateService.CurrentVersion != null
                    && m.Version.CompareTo(UpdateService.CurrentVersion) > 0
                    && UpdateService.IsIgnored(m.Version))
                {
                    var resume = AppDialog.Show(this,
                        "你之前忽略了 v" + m.Version + " 的更新提醒。\n\n是否恢复提醒并查看更新？",
                        "检查更新", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (resume != MessageBoxResult.Yes)
                    {
                        RefreshSettingsUpdateUi();
                        return;
                    }

                    if (UpdateService.Settings != null)
                    {
                        UpdateService.ClearIgnore(UpdateService.Settings);
                        SettingsStore.Save(UpdateService.Settings);
                    }
                    RefreshSettingsUpdateUi();
                }

                if (UpdateService.HasUpdate && m != null)
                {
                    var prompt = new UpdatePromptWindow(m) { Owner = this };
                    prompt.ShowDialog();

                    if (prompt.IgnoredByUser && UpdateService.Settings != null)
                    {
                        UpdateService.IgnoreCurrent(UpdateService.Settings);
                        SettingsStore.Save(UpdateService.Settings);
                        Log("已忽略 v" + m.Version + " 的更新提醒。");
                    }
                }
                else if (UpdateService.Latest != null
                         && UpdateService.CurrentVersion != null
                         && UpdateService.Latest.Version.CompareTo(UpdateService.CurrentVersion) > 0)
                {
                    AppDialog.Show(this,
                        "你已忽略 v" + UpdateService.Latest.Version + " 的更新提醒。\n"
                        + "当前版本仍是 v" + UpdateService.CurrentVersion + "。\n\n"
                        + "想恢复提醒：再点一次「检查更新」即可。",
                        "检查更新");
                }
                else
                {
                    AppDialog.Show(this,
                        "当前已是最新版本（v"
                        + (UpdateService.CurrentVersion != null ? UpdateService.CurrentVersion.ToString() : "?")
                        + "）。",
                        "检查更新");
                }
            }
            catch (Exception ex)
            {
                _settingsCheckingUpdate = false;
                RefreshSettingsUpdateUi();
                if (SettingsUpdateStatusText != null)
                    SettingsUpdateStatusText.Text = "检查失败：" + ex.Message;
                // ⚠️ 这里给状态行留个标记：finally 里的 RefreshSettingsUpdateUi
                // 会把文案重置成默认值，那会把"检查失败"这个用户最需要的信息抹掉。
                _settingsUpdateStatusPinned = true;

                AppDialog.Show(this,
                    "检查更新失败：" + ex.Message
                    + "\n\n你也可以手动打开 Releases 页查看：\n" + UpdateService.ReleasesPageUrl,
                    "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _settingsCheckingUpdate = false;
                if (SettingsCheckUpdateBtn != null) SettingsCheckUpdateBtn.IsEnabled = true;
                // 失败时状态行已由 catch 写好，不要再覆盖（见上面的 _settingsUpdateStatusPinned）
                if (_settingsUpdateStatusPinned) _settingsUpdateStatusPinned = false;
                else RefreshSettingsUpdateUi();
            }
        }

        /// <summary>设置页「打开发布页」：浏览器打开 GitHub Releases。</summary>
        private void OnOpenReleasesPage(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = UpdateService.ReleasesPageUrl,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Log("打开发布页失败：" + ex.Message);
            }
        }

        // ---------------- 播放器探测（边下边播用） ----------------

        /// <summary>刷新设置页的播放器状态文案（同步版，供启动阶段调用）。</summary>
        private void RefreshPlayerStatus()
        {
            ApplyPlayerStatus(PlayerLocator.Find());
        }

        /// <summary>把探测结果落到 UI —— 纯 UI 操作，必须在 UI 线程调用。</summary>
        private void ApplyPlayerStatus(PlayerLocator.Player p)
        {
            try
            {
                if (p == null)
                {
                    PlayerStatusText.Text = "未检测到 VLC。"
                        + "如果 VLC 装在非常规目录，点「选择 VLC 位置…」手动指定 vlc.exe（会记住）。"
                        + "「打开」将回退到系统默认程序，但通常无法边下边播。";
                    GetVlcBtn.Visibility = System.Windows.Visibility.Visible;
                }
                else
                {
                    PlayerStatusText.Text = "已检测到 VLC（" + p.ExePath + "）"
                        + " —— 支持边下边播。";
                    GetVlcBtn.Visibility = System.Windows.Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                PlayerStatusText.Text = "检测播放器失败: " + ex.Message;
            }
        }

        /// <summary>
        /// 「重新检测」—— 探测播放器。
        /// </summary>
        /// <remarks>
        /// ⚠️ 必须异步：<see cref="PlayerLocator.Find"/> 除了读注册表、查几个固定路径，
        /// 还会**遍历整个 PATH** 逐个 <c>File.Exists</c>。PATH 里只要有一个网络路径，
        /// 这一步就会卡好几秒 —— 原来是同步调用，等于把整个窗口冻住（点哪都没反应）。
        /// 现在把探测丢到后台线程，按钮就地显示「检测中」+ 转圈。
        /// </remarks>
        private async void OnRecheckPlayer(object sender, RoutedEventArgs e)
        {
            long t0 = BusyClock();
            if (!BeginBusy(RecheckPlayerBtn, "检测中")) return;
            try
            {
                // 「重新检测」= 回到自动探测：清掉手动路径，避免旧路径挡住新的安装位置
                if (!string.IsNullOrEmpty(_settings.VlcPath))
                {
                    _settings.VlcPath = "";
                    SettingsStore.Save(_settings);
                    Log("已清除手动指定的 VLC 路径，改为自动检测。");
                }
                PlayerLocator.PreferredPath = null;

                PlayerLocator.Player p;
                try
                {
                    p = await Task.Run(() => PlayerLocator.Find());
                }
                catch (Exception ex)
                {
                    PlayerStatusText.Text = "检测播放器失败: " + ex.Message;
                    return;
                }
                ApplyPlayerStatus(p);
            }
            finally
            {
                await EndBusyAsync(RecheckPlayerBtn, t0);
            }
        }

        /// <summary>
        /// 「选择 VLC 位置…」—— 便携版/非常规安装目录的用户手动指定 vlc.exe。
        /// 路径写入设置并立即生效，之后播放与边下边播都用它。
        /// </summary>
        private void OnPickVlc(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 VLC 播放器（vlc.exe）",
                Filter = "VLC (vlc.exe)|vlc.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
            };
            if (dlg.ShowDialog(this) != true) return;

            string path = dlg.FileName;
            _settings.VlcPath = path;
            SettingsStore.Save(_settings);
            PlayerLocator.PreferredPath = path;
            RefreshPlayerStatus();
            Log("已指定 VLC 路径: " + path);
        }

        private void OnGetVlc(object sender, RoutedEventArgs e)
        {
            const string url = "https://www.videolan.org/vlc/";
            // 这个按钮只负责打开官方页面，不会偷偷下载或安装播放器。
            // ShellLaunch 与 Python webbrowser.open 对齐，激活已有默认浏览器实例。
            // 都失败就**弹窗**给用户，并把网址原文显示出来供复制 —— Log 里
            // 可能被大量日志淹没，弹窗不会。
            if (!ShellLaunch.OpenUrl(url))
            {
                Log("打不开浏览器，请手动访问：" + url);
                AppDialog.Show(this,
                    "无法启动浏览器。\n\n请手动复制下方地址到浏览器打开：\n\n" + url,
                    "需要手动打开",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }
        }

        // ---------------- 设置 ----------------

        /// <summary>
        /// 可选的并发档位。**最后一项必须等于 SegmentedDownloader.MaxRealConcurrency**，
        /// 否则会出现"界面能选、代码却夹不到"的假承诺。
        /// 注意这个数组的顺序必须与 XAML 里 ComboBoxItem 的顺序严格一致。
        /// 一致性由下面的静态构造函数在启动时自检，改漏了会直接抛异常暴露。
        /// </summary>
        private static readonly int[] ThreadPresets = { 16, 32, 64, 128, 256, 512 };

        /// <summary>并发上限的单一事实来源，取自核心层常量。</summary>
        private static int ConcurrencyCap => GeZi.Core.Download.SegmentedDownloader.MaxRealConcurrency;

        static MainWindow()
        {
            // 防止"改了核心常量却忘了改 UI 档位"这类不一致再次发生。
            // 这种 bug 很隐蔽：界面看起来正常，实际选的值被悄悄夹掉，
            // 用户会觉得程序在骗他。宁可启动就炸，也不要静默失配。
            int last = ThreadPresets[ThreadPresets.Length - 1];
            if (last != ConcurrencyCap)
            {
                throw new InvalidOperationException(
                    "并发档位与核心层上限不一致：档位最高 " + last +
                    "，核心上限 " + ConcurrencyCap +
                    "。请同步修改 ThreadPresets 与 XAML 的 ComboBoxItem。");
            }
        }

        private static int PresetToIndex(int threads)
        {
            int best = 1, bestDiff = int.MaxValue;
            for (int i = 0; i < ThreadPresets.Length; i++)
            {
                int diff = Math.Abs(ThreadPresets[i] - threads);
                if (diff < bestDiff) { bestDiff = diff; best = i; }
            }
            return best;
        }

        private int SelectedThreads()
        {
            int i = ThreadsBox.SelectedIndex;
            if (i < 0 || i >= ThreadPresets.Length) i = 1;   // 默认 32
            int v = ThreadPresets[i];
            // 兜底：即使档位表被改错，也不会把超过核心上限的值交给下载器。
            if (v > ConcurrencyCap) v = ConcurrencyCap;
            return v;
        }

        private void LoadSettingsToUi()
        {
            // 置位期间控件赋值会触发 SelectionChanged/Click，必须挡住，
            // 否则会用"界面上的默认值"反向覆盖用户已保存的配置。
            _applyingSettings = true;
            try
            {
                ThreadsBox.SelectedIndex = PresetToIndex(_settings.Threads);
                ConcurrentBox.Text = _settings.ConcurrentTasks.ToString();
                OutDirBox.Text = _settings.OutDir;
                AutoCleanCheck.IsChecked = _settings.AutoClean;
                SystemProxyCheck.IsChecked = _settings.UseSystemProxy;
                // 【2026-10-20】恢复「完成后关机」开关（不写日志，避免启动时刷屏）
                SetShutdownAfterAll(_settings.ShutdownAfterAll, announce: false);
                // 写入方式改成两张可点击的卡片（下拉框已删）
                WriteModeSegmentedRadio.IsChecked = true;   // 落盘方式唯一化
                WriteModeSharedRadio.IsChecked = false;   // 落盘方式唯一化（共写模式已移除）
                PartsRootBox.Text = _settings.PartsRoot ?? "";
                HidePartsCheck.IsChecked = _settings.HideParts;
                CloseActionBox.SelectedIndex = (int)_settings.OnCloseButton;
                // 剪贴板自动解析（默认开）。注意这里**只回填控件**：真正的注册在窗口句柄
                // 建好之后由 AttachWindowHooks→ApplyClipboardListener 做。
                ClipboardAutoParseCheck.IsChecked = _settings.ClipboardAutoParse;
                // 【2026-10-21】「优先展示未完成的文件」（默认关）
                TaskUnfinishedFirstCheck.IsChecked = _settings.TaskUnfinishedFirst;
                // 【2026-10-21】「启动时恢复上次浏览的目录」（默认开）
                RestoreBrowseCheck.IsChecked = _settings.RestoreBrowseOnStartup;
                // 【2026-10-24】自动调度：回填勾选 + 若是开启状态，按自动值刷新
                // 两个输入框并置灰（否则用户会看到"勾开着但还能手改"的矛盾界面）。
                AutoTuneCheck.IsChecked = _settings.AutoTune;
                // 外观主题：把配置里的选择回填到三个单选（_applyingSettings 已挡掉 Checked）
                SyncThemeRadios();
                // 任务列表排序偏好 → 同步工具栏那颗切换按钮的文案与图标
                UpdateTaskSortButton();
                // 任务列表展示方式（文件夹分组 / 平铺）→ 应用分组 + 同步按钮
                ApplyTaskGrouping();
                UpdateTaskViewButton();
                // 【2026-10-22】「全部收起 / 全部展开」按钮的初始文案与图标
                UpdateCollapseAllButton();
                // 【2026-10-21】恢复「运行日志」的可拖拽高度（用户要求记忆）
                ApplyLogPanelHeight();
                // 【2026-10-21】设置页「关于与更新」：填版本号 + 恢复红点状态。
                // 此时 UpdateService.Attach 已执行过（见构造函数里的顺序），
                // HasUpdate 是从上次落盘的版本号算出来的，不会发网络请求。
                RefreshSettingsUpdateUi();
            }
            finally
            {
                _applyingSettings = false;
            }
            // 自动调度：初始化控件的可用性/提示文案。
            // 放在 _applyingSettings 复位之后 —— 它内部会改 IsChecked/Text，
            // 若在保护区间内反而不会被识别为"用户操作"（这里本来也不需要触发保存，
            // 但保持语义清晰：先回填完，再统一刷新展示状态）。
            try { UpdateAutoTuneUi(); } catch { }
            // 把「分片目录隐藏」告知核心层（静态开关，只影响外观）
            SegmentedDownloader.HidePartsDir = _settings.HideParts;
            // 把诊断日志接进界面日志框：外部启动是否成功、ShellExecute 返回码多少，
            // 都要能看到 —— 否则每次失败都只能猜。
            ShellLaunch.Log = Log;
            LocalStreamServer.Log = Log;
            // 手动指定的 VLC 路径优先于自动探测（便携版可能在任何目录）
            PlayerLocator.PreferredPath = _settings.VlcPath;
            // 探测本机播放器，供设置页展示 + 「打开」按钮选用
            RefreshPlayerStatus();
        }

        /// <summary>
        /// 【2026-10-02 改造】设置改为**即时生效**，不再有「保存设置」按钮。
        ///
        /// 各控件的触发时机（见 MainWindow.xaml 的事件绑定）：
        ///   · ComboBox  → SelectionChanged（选中即生效）
        ///   · CheckBox  → Click（勾选即生效）
        ///   · TextBox   → LostFocus（移开焦点才生效）+ KeyDown 回车立刻生效
        ///
        /// 为什么文本框不用 TextChanged：每敲一个字符都会 RecreateScheduler()，
        /// 正在下载时反复重建调度器代价很大，而且中间态（如 "1" → "12"）会被
        /// 当成有效值落盘。用 LostFocus 是最稳的折中。
        ///
        /// _applyingSettings 是重入保护：LoadSettingsToUi() 里给控件赋值同样会
        /// 触发 SelectionChanged/Click，若不挡住会在启动时把默认值写回覆盖用户配置。
        /// </summary>
        private void OnSettingChanged(object sender, RoutedEventArgs e)
        {
            if (_applyingSettings) return;
            ApplySettings();
        }

        // ================= 外观主题（2026-10-04 新增） =================

        /// <summary>
        /// 设置页「个性化」三选一切换。走独立处理器而不是 <see cref="OnSettingChanged"/>，
        /// 因为换主题**不需要**重建调度器 / 重读所有设置项 —— 那会顺带触发一串
        /// 无关的副作用（比如重建下载调度器会打断在途任务）。
        /// </summary>
        private void OnThemeChanged(object sender, RoutedEventArgs e)
        {
            // 加载配置回填控件时也会触发 Checked，必须挡掉（否则会把 System 又写回成具体值）
            if (_applyingSettings) return;

            AppTheme want;
            if (ThemeDarkRadio.IsChecked == true) want = AppTheme.Dark;
            else if (ThemeSystemRadio.IsChecked == true) want = AppTheme.System;
            else want = AppTheme.Light;

            try
            {
                ThemeManager.SetTheme(want);
                _settings.Theme = want;
                SettingsStore.Save(_settings);
                UpdateThemeHint();
                Log(ThemeLogText(want));
            }
            catch (Exception ex)
            {
                Log("切换主题失败：" + ex.Message);
            }
        }

        /// <summary>把配置里的主题回填到单选按钮（加载设置时调用）。</summary>
        private void SyncThemeRadios()
        {
            try
            {
                switch (_settings.Theme)
                {
                    case AppTheme.Dark: ThemeDarkRadio.IsChecked = true; break;
                    case AppTheme.Light: ThemeLightRadio.IsChecked = true; break;
                    default: ThemeSystemRadio.IsChecked = true; break;
                }
                UpdateThemeHint();
            }
            catch { }
        }

        /// <summary>
        /// 说明行的文案随选择变化：
        /// 选「跟随系统」时要讲清"系统是深色/浅色"以及"改系统会自动跟"，
        /// 否则用户会疑惑"我明明选了跟随系统，现在到底是哪个"。
        /// </summary>
        private void UpdateThemeHint()
        {
            try
            {
                if (ThemeHintText == null) return;

                if (ThemeSystemRadio.IsChecked == true)
                {
                    bool sysDark = ThemeManager.IsSystemDark();
                    ThemeHintText.Text = "跟随系统时，Windows 切换深色/浅色，本程序会自动跟着变。"
                        + "（当前系统为" + (sysDark ? "深色" : "浅色") + "）";
                }
                else if (ThemeDarkRadio.IsChecked == true)
                {
                    ThemeHintText.Text = "始终使用深色界面，不受系统设置影响。";
                }
                else
                {
                    ThemeHintText.Text = "始终使用浅色界面，不受系统设置影响。";
                }
            }
            catch { }
        }

        private static string ThemeLogText(AppTheme t)
        {
            if (t == AppTheme.Dark) return "外观已切换为深色";
            if (t == AppTheme.Light) return "外观已切换为浅色";
            return "外观已设为跟随系统（当前" + (ThemeManager.IsSystemDark() ? "深色" : "浅色") + "）";
        }

        /// <summary>文本框里按回车 = 立刻生效（不必先点别处）。</summary>
        private void OnSettingKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (_applyingSettings) return;
            ApplySettings();
            e.Handled = true;
        }

        /// <summary>
        /// 「跟随系统代理」勾选变更（2026-10-20 新增独立处理器）。
        ///
        /// <para>【为什么不用 OnSettingChanged】那条路最后会 <c>RecreateScheduler()</c>
        /// 重建下载调度器 —— 只为切换代理就把**在途任务全打断**，代价太大。
        /// 而代理开关只影响"此后新建的 HttpClientHandler"（见 NetworkOptions.ApplyTo），
        /// 根本不需要重建调度器。</para>
        /// </summary>
        private void OnSystemProxyChanged(object sender, RoutedEventArgs e)
        {
            if (_applyingSettings) return;
            try
            {
                bool on = SystemProxyCheck.IsChecked == true;
                _settings.UseSystemProxy = on;
                NetworkOptions.UseSystemProxy = on;
                SettingsStore.Save(_settings);
                Log(on
                    ? "已开启「跟随系统代理」：之后新建的连接会走 Windows 系统代理（重新开始下载 / 重新登录后生效）。"
                    : "已关闭「跟随系统代理」：之后的连接一律直连。");
            }
            catch (Exception ex)
            {
                Log("代理设置保存失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 从界面读取全部设置项 → 落盘 → 重建调度器。
        /// 由 OnSettingChanged / OnSettingKeyDown 调用；没有"保存"按钮了。
        /// </summary>
        private void ApplySettings()
        {
            try
            {
                int conc;
                int threads = SelectedThreads();
                int.TryParse((ConcurrentBox.Text ?? "").Trim(), out conc);

                _settings.Threads = threads;

                if (conc < 1) conc = 1;
                if (conc > 64) conc = 64;
                _settings.ConcurrentTasks = conc;

                // ⚠️ 必须规范化：用户手输的可能是 `C:/a/b`（正斜杠），
                // 而 Path.Combine 拼文件名时用 `\` → 会得到混合斜杠路径，
                // 外部播放器（VLC）会把它当 URL 协议头而打不开（2026-10-04 实测）。
                _settings.OutDir = NormalizeDir(OutDirBox.Text);
                _settings.AutoClean = AutoCleanCheck.IsChecked == true;
                _settings.UseSystemProxy = SystemProxyCheck.IsChecked == true;
                NetworkOptions.UseSystemProxy = _settings.UseSystemProxy;
                _settings.WriteMode = WriteMode.SegmentedFiles;   // 落盘方式唯一化（共写模式已移除）
                // 分片根目录变了 → 把**旧值**记入历史清单，否则旧目录里的残留分片
                // 之后既扫不到、也没入口清理（用户只能自己去翻磁盘删）。
                string newPartsRoot = (PartsRootBox.Text ?? "").Trim();
                RegisterPartsDirHistory(_settings.PartsRoot);
                _settings.PartsRoot = newPartsRoot;
                _settings.HideParts = HidePartsCheck.IsChecked == true;
                SegmentedDownloader.HidePartsDir = _settings.HideParts;
                _settings.OnCloseButton = (CloseAction)Math.Max(0,
                    Math.Min(2, CloseActionBox.SelectedIndex));

                SettingsStore.Save(_settings);
                RecreateScheduler();

                Log(string.Format("设置已生效（单文件并发 {0} 路，同时下载 {1} 个）", threads, conc));
            }
            catch (Exception ex)
            {
                Log("设置保存失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 把某个分片根目录记入历史清单（去重、最近使用在末尾、超长时丢弃最旧的）。
        /// 移植自 Python Flet 版的 `_register_part_dir`。空白与「默认位置」不记。
        /// </summary>
        private void RegisterPartsDirHistory(string dir)
        {
            dir = (dir ?? "").Trim();
            if (dir.Length == 0) return;
            try
            {
                string norm = Path.GetFullPath(dir);
                if (_settings.PartsDirsKnown == null)
                    _settings.PartsDirsKnown = new List<string>();
                for (int i = _settings.PartsDirsKnown.Count - 1; i >= 0; i--)
                {
                    string e;
                    try { e = Path.GetFullPath(_settings.PartsDirsKnown[i]); }
                    catch { e = _settings.PartsDirsKnown[i]; }
                    if (string.Equals(e, norm, StringComparison.OrdinalIgnoreCase))
                        _settings.PartsDirsKnown.RemoveAt(i);
                }
                _settings.PartsDirsKnown.Add(dir);
                while (_settings.PartsDirsKnown.Count > AppSettings.PartsDirsMax)
                    _settings.PartsDirsKnown.RemoveAt(0);
            }
            catch { }
        }

        /// <summary>
        /// 当前生效 + 历史用过的**全部分片根目录**，用于清理/统计残留分片。
        /// 移植自 Python Flet 版的 `_part_dirs_all`：程序曾经用过、后来被切走的目录
        /// 也一并返回 —— 否则那些残留分片既扫不到、也清不掉。
        /// </summary>
        private List<string> AllPartsRoots()
        {
            var outList = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string d)
            {
                d = (d ?? "").Trim();
                if (d.Length == 0) return;
                string k;
                try { k = Path.GetFullPath(d); }
                catch { k = d; }
                if (seen.Add(k)) outList.Add(d);
            }

            Add(_settings.PartsRoot);
            if (_settings.PartsDirsKnown != null)
                foreach (var d in _settings.PartsDirsKnown) Add(d);
            return outList;
        }

        private void OnBrowsePartsRoot(object sender, RoutedEventArgs e)
        {
            string start = (PartsRootBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(start)) start = _settings.OutDir;
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = start })
            {
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    PartsRootBox.Text = dlg.SelectedPath;
                    ApplySettings();   // 即时生效（没有"保存"按钮了）
                }
            }
        }

        /// <summary>
        /// 在资源管理器中打开「分片/缓存根目录」。
        ///
        /// 【2026-10-04 用户要求】原来这一行只有「浏览…」，用户希望像「下载目录」那样
        /// 也能一键打开所在目录。
        ///
        /// 与 <see cref="OnOpenOutDir"/> 的差异（刻意如此）：
        ///   - 留空时的语义是"与目标文件同目录"，没有单一目录可开 ——
        ///     此时**回退到下载目录**（目标文件默认就下在那里），而不是报错。
        ///   - 同样**不自动创建**目录：用户只是想在资源管理器里看看；
        ///     若目录不存在就明确提示，别悄悄建一堆空目录。
        /// </summary>
        private void OnOpenPartsRoot(object sender, RoutedEventArgs e)
        {
            string dir = (PartsRootBox.Text ?? "").Trim();
            // 留空 = 分片与目标文件同目录 → 打开下载目录即可。
            if (string.IsNullOrEmpty(dir)) dir = _settings.OutDir;
            if (string.IsNullOrEmpty(dir))
            {
                AppDialog.Show(this, "还没设置分片根目录，也没有下载目录可打开。",
                    "无可打开的目录", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }
            try
            {
                if (!Directory.Exists(dir))
                {
                    // 不自动创建：仅提示，避免误建目录。
                    Log("分片根目录不存在：" + dir);
                    AppDialog.Show(this,
                        "分片/缓存根目录还不存在。\n\n请先点「浏览…」选择一个目录，" +
                        "或留空使用默认（与目标文件同目录）。\n\n路径：\n" + dir,
                        "目录不存在", System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                    return;
                }
                // 走统一入口：先 shell，失败再显式启动 explorer（与 OnOpenOutDir 一致）。
                if (!ShellLaunch.OpenFolder(dir))
                {
                    Log("打不开分片根目录，请手动打开：" + dir);
                    AppDialog.Show(this,
                        "无法打开分片/缓存根目录。\n\n请手动复制下方路径到资源管理器：\n\n" + dir,
                        "需要手动打开", System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Log("打开分片根目录失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 应用设置变更。
        ///
        /// 【不要在有下载在跑时 Dispose 重建 scheduler】
        /// 曾经的实现是无条件 Dispose + new。但保存设置（改并发/连接预算）时
        /// 下载往往正在跑：Dispose 会把内部 SemaphoreSlim 一起释放，
        /// 而此刻 RunOneAsync 正持有它 —— 接下来的 sem.Release() 就抛
        /// ObjectDisposedException("已释放该信号量")，任务被中断且提示莫名其妙。
        ///
        /// 更隐蔽的后果：RunJobs 读的是字段 _scheduler，旧对象被 Dispose 后
        /// 新对象上位，于是"正在跑的那批"用旧限流、"新点的"用新限流 ——
        /// 两个 scheduler 并存，ConcurrentTasks 限流直接失效。
        ///
        /// 现在的策略（2026-10-24 起）：
        ///   - 空闲时：正常重建（新并发数立即生效）
        ///   - **下载中：也立即新建一个 scheduler 给"新任务"用**，
        ///     但**不 Dispose 旧的那个** —— 让正在跑的那批自然跑完。
        ///
        /// 【为什么这样改】用户实测「设置里改成同时 5 个，保存后日志还是『3 个在跑』」。
        /// 原来的做法是"下载中只保存、不生效"，从用户角度看就是设置坏了。
        /// 关键洞察：原来担心的两个事故（Dispose 正在用的 sem / 新旧并存导致限流失效）
        /// 都源于"Dispose 旧对象"。**只要不 Dispose，它们都不会发生**：
        ///   · 旧 scheduler 继续用它自己的 sem 跑完手上的任务 → 不会 ObjectDisposed；
        ///   · 新 scheduler 只管新加进来的任务 → 不存在"两个都在调度同一批"。
        /// 代价是"过渡期总数可能略微超出 ConcurrentTasks"（旧批次逐渐收尾），
        /// 但这是**用户明确要求的行为**，而且只持续到旧批跑完。
        /// </summary>
        private void RecreateScheduler()
        {
            // 连接上限现在由 SegmentedDownloader / QuarkSession 各自显式设定，
            // 不再暴露"全局连接预算"给用户（原来的设置项实际不起作用）。
            // NetworkConfig.Apply 仍在调度器构造里以固定值调用一次，兜底全局 DefaultConnectionLimit。
            var old = _scheduler;
            _scheduler = new DownloadScheduler(_settings.ConcurrentTasks);
            _scheduler.Log = LogFromWorker;
            _scheduler.Diagnostics = OnDiagnostics;

            if (_batchRunning)
            {
                // 正在下载：旧的不动（让它跑完手上的），新的接手后续任务。
                // 旧 scheduler 会在本批自然结束后由 GC 回收 —— 它的 sem 从未被 Dispose，
                // 因此不会出现"Release 抛 ObjectDisposedException"那类事故。
                Log(string.Format(
                    "并发设置已生效：新任务按【同时 {0} 个】排队。"
                    + "当前批次（最多 {1} 个）会先跑完，不打断。",
                    _settings.ConcurrentTasks, _settings.ConcurrentTasks));
                return;
            }

            // 空闲：旧对象手上没任务，可以安全释放。
            try { old?.Dispose(); } catch { }
        }

        /// <summary>当前是否有下载批次在跑。用于阻止"运行中重建 scheduler"这类危险操作。</summary>
        private volatile bool _batchRunning;

        /// <summary>
        /// 本次下载批次的起点（UTC）。用于状态栏显示"已耗时"。
        ///
        /// <para>【2026-10-22 用户要求】「耗时是每次推送任务到下载完成用时多少，
        /// 不是累加的」——
        /// 所以口径是**一批任务从推送到全部完成**的用时：
        /// 每推一批就重新计时，批次结束（全部完成/取消）后停表并把结果留住。</para>
        ///
        /// <para>【为什么不是"会话累加"】累加的话，续传/重试/追加新文件都会把时间越滚越长，
        /// 完全看不出"这批到底下了多久"。</para>
        /// </summary>
        private DateTime? _batchStartTime;

        /// <summary>上一批完成时的用时（保留显示，直到下一批开始）。</summary>
        private TimeSpan? _lastBatchElapsed;

        /// <summary>
        /// 本批任务在**开始时**已经下载的字节数（用来算本批实际新增了多少）。
        ///
        /// <para>⚠️ 【2026-10-22 修】平均速度算错（显示 200MB/s，实际只有 1MB/s）：
        /// 分子原来用 <c>_tasks.Sum(t =&gt; t.Done)</c> —— 那是**所有任务的历史累计**
        /// （含之前几批早已下完的文件），分母却只有本批耗时 → 分子远大于分母对应的量。
        /// 现在改成"本批的**增量**"：本批结束时的累计值减去本批开始时的累计值。</para>
        /// </summary>
        private long _batchDoneAtStart;

        /// <summary>已耗时文本（如「耗时 12 分 30 秒」）。</summary>
        private string FormatElapsedTotal()
        {
            try
            {
                if (_batchStartTime != null)
                {
                    var span = DateTime.UtcNow - _batchStartTime.Value;
                    return "耗时 " + TaskItem.FormatEta(span.TotalSeconds);
                }
                if (_lastBatchElapsed != null)
                {
                    return "耗时 " + TaskItem.FormatEta(_lastBatchElapsed.Value.TotalSeconds);
                }
                return "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// 本批的**平均速度**（本批新增字节 ÷ 本批耗时）。
        ///
        /// <para>【2026-10-22 用户要求】「总速度之类的可以一直保留，
        /// 没必要过几秒就消失了」—— 全部下载完、实时速度归零之后，
        /// 用这个平均值顶上，状态栏就一直有信息可看。</para>
        ///
        /// <para>⚠️ 分子必须是**本批新增**的字节数，不能用全部任务的累计 Done ——
        /// 否则会把之前几批早已下完的体积也算进来，算出几百 MB/s 的假速度。</para>
        /// </summary>
        private string FormatAverageSpeed()
        {
            try
            {
                TimeSpan? span = _batchStartTime != null
                    ? (TimeSpan?)(DateTime.UtcNow - _batchStartTime.Value)
                    : _lastBatchElapsed;
                if (span == null || span.Value.TotalSeconds < 0.5) return "—";

                // ⚠️ 分子必须是**本批新增**的字节数。
                // 用全部任务的累计 Done 会把之前几批早已下完的体积算进来
                // （用户实测：显示 200MB/s，实际平均只有 1MB/s）。
                long nowDone = _tasks.Sum(t => t.Done);
                long bytes = nowDone - _batchDoneAtStart;
                if (bytes <= 0) return "—";

                double avg = (double)bytes / span.Value.TotalSeconds;
                if (avg < 1) return "—";
                return Util.FormatSize((long)avg) + "/s";
            }
            catch { return "—"; }
        }

        private void OnBrowseOutDir(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = _settings.OutDir })
            {
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    OutDirBox.Text = dlg.SelectedPath;
                    ApplySettings();   // 即时生效（没有"保存"按钮了）
                }
            }
        }

        /// <summary>在文件资源管理器中打开当前下载目录；目录不存在时先创建。</summary>
        private void OnOpenOutDir(object sender, RoutedEventArgs e)
        {
            string dir = (OutDirBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(dir)) dir = _settings.OutDir;
            try
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    Log("下载目录不存在，已创建: " + dir);
                }
                // 走统一入口：先 shell，失败再显式启动 explorer。
                // 两级都失败就**弹窗**给用户 —— 用户实测 explorer.exe 启动即崩
                // (0xc0000142)，纯 Log 容易被埋没，弹窗强制可见，且完整路径可直接复制。
                if (!ShellLaunch.OpenFolder(dir))
                {
                    Log("打不开下载目录，请手动打开：" + dir);
                    AppDialog.Show(this,
                        "无法打开下载目录。\n\n请手动复制下方路径到资源管理器：\n\n" + dir,
                        "需要手动打开",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Log("打开下载目录失败: " + ex.Message);
            }
        }

        // ---------------- 登录 ----------------

        private void OnLoginClick(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 3;

        /// <summary>
        /// 打开账号管理弹窗：列出本机保存的账号，可一键切换 / 删除，或去扫码登录新账号。
        /// 移植自 Flet 版的 _open_account_dialog。
        /// </summary>
        private void OnOpenAccounts(object sender, RoutedEventArgs e) => ShowAccountsDialog();

        private void ShowAccountsDialog()
        {
            var list = AccountStore.LoadAll();
            string curCookie = _client?.CookieStr ?? _settings.Cookie ?? "";
            // 当前账号的 Key：优先用它判断"哪一行是当前账号"，Cookie 只作兜底
            string curKey = _settings.CurrentAccountKey ?? "";
            if (string.IsNullOrEmpty(curKey))
                curKey = _client?.AccountSig ?? "";

            var win = new Window
            {
                Owner = this,
                Title = "切换账号",
                ResizeMode = ResizeMode.CanResize,
            };
            DialogChrome.StyleWindow(win, 500, 460);

            var root = new Grid { Margin = new Thickness(20, 18, 20, 18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 说明
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 列表
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按钮

            var title = DialogChrome.PageTitle("切换账号");
            Grid.SetRow(title, 0);
            root.Children.Add(title);

            var hint = DialogChrome.Hint(
                "选择一个账号即可切换，不用重新扫码。删除会同时清掉本机保存的登录信息（含 Cookie）；"
                + "删除当前账号即等于退出登录。", 0, 12);
            Grid.SetRow(hint, 1);
            root.Children.Add(hint);

            var listBox = DialogChrome.StyledList();
            listBox.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            var listCard = DialogChrome.Card(listBox);
            listCard.Padding = new Thickness(0);
            Grid.SetRow(listCard, 2);
            root.Children.Add(listCard);

            Action rebuild = null;
            rebuild = () =>
            {
                listBox.Items.Clear();
                var accounts = AccountStore.LoadAll();
                if (accounts.Count == 0)
                {
                    listBox.Items.Add(new TextBlock
                    {
                        Text = "暂无已保存的账号，扫码登录后会自动保存一份。",
                        Foreground = (Brush)(TryFindResource("TextTertiaryBrush")
                                             ?? new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))),
                        FontSize = 12,
                        Margin = new Thickness(14),
                    });
                    return;
                }
                foreach (var a in accounts)
                {
                    listBox.Items.Add(BuildAccountRow(a, curKey, curCookie, win, rebuild));
                }
            };
            rebuild();

            var scanBtn = DialogChrome.PrimaryButton("扫码登录新账号");
            // 【2026-10-03】「退出登录」按钮已按用户要求删除 ——
            // 用户原话：「这里退出登录有啥用啊，也不能一键切回。直接删掉这个功能，
            //           想退出的人直接删账号就行了根本不用点这一个」。
            // 所以退出登录只剩**一条**路径：在账号列表里删掉当前账号
            // （删除会连带清掉配置里的 Cookie，见 delBtn 的处理）。
            var closeBtn = DialogChrome.SecondaryButton("关闭");
            var btns = DialogChrome.ButtonRow(scanBtn, closeBtn);
            btns.Margin = new Thickness(0, 14, 0, 0);
            Grid.SetRow(btns, 3);
            root.Children.Add(btns);

            scanBtn.Click += (s, ev) =>
            {
                // 扫码新账号：切到登录页并收起本弹窗（保留现有账号档案不动）。
                win.Close();
                Tabs.SelectedIndex = 3;
            };
            closeBtn.Click += (s, ev) => win.Close();

            win.Content = root;
            win.ShowDialog();
        }

        /// <summary>账号列表里的一行：昵称 + 副标题 + 「切换」「删除」两个操作。</summary>
        /// <param name="curKey">当前登录账号的 Key（来自 <see cref="AppSettings.CurrentAccountKey"/>）。</param>
        /// <param name="curCookie">
        /// 当前 Cookie。仅在 <paramref name="curKey"/> 为空时作为**兜底**判据
        /// （老配置文件里没有 CurrentAccountKey 字段）。
        /// </param>
        private Border BuildAccountRow(AccountProfile acc, string curKey, string curCookie,
            Window owner, Action rebuild)
        {
            // 【2026-10-03 修】原来只拿整串 Cookie 比字符串，而运行时 Cookie 会被
            // 重建/刷新（顺序、条目都会变）→ 永远比不中 → 当前账号的「切换」按钮
            // 居然还是可点的（用户反馈：「当我正在登录对应的账号的时候它的切换应该是不可使用的」）。
            // 现在优先比 Key（夸克返回的 uid，稳定），Cookie 只作兜底。
            bool isCurrent = !string.IsNullOrEmpty(curKey)
                ? string.Equals(acc.Key, curKey, StringComparison.Ordinal)
                : (!string.IsNullOrEmpty(curCookie) &&
                   string.Equals(acc.Cookie, curCookie, StringComparison.Ordinal));

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock
            {
                Text = acc.DisplayName,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            string sub;
            if (isCurrent) sub = "当前账号";
            else if (acc.LastUsedUtc > DateTime.MinValue)
                sub = "最近使用 " + acc.LastUsedUtc.ToLocalTime().ToString("MM-dd HH:mm");
            else sub = "本机已保存";
            texts.Children.Add(new TextBlock
            {
                Text = sub,
                FontSize = 11,
                // 跟随主题：暗色下 #2E7D32 那种暗绿在深底上几乎看不清，
                // 统一走 Success/TextTertiary 令牌，两套主题都达标。
                Foreground = (Brush)(TryFindResource(isCurrent
                    ? "SuccessBrush" : "TextTertiaryBrush")
                    ?? new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))),
            });
            Grid.SetColumn(texts, 0);
            grid.Children.Add(texts);

            var switchBtn = new Button
            {
                Content = "切换",
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(6, 0, 6, 0),
                IsEnabled = !isCurrent,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var secStyle = TryFindResource("SecondaryButton") as Style;
            if (secStyle != null) switchBtn.Style = secStyle;
            switchBtn.Click += async (s, ev) =>
            {
                switchBtn.IsEnabled = false;
                switchBtn.Content = "切换中…";
                await ApplyLoginAsync(acc.Cookie);
                owner.Close();
            };
            Grid.SetColumn(switchBtn, 1);
            grid.Children.Add(switchBtn);

            var delBtn = new Button
            {
                Content = "删除",
                Padding = new Thickness(12, 4, 12, 4),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var ghostStyle = TryFindResource("GhostButton") as Style;
            if (ghostStyle != null) delBtn.Style = ghostStyle;
            var dangerBrush = TryFindResource("DangerBrush") as Brush;
            if (dangerBrush != null) delBtn.Foreground = dangerBrush;
            delBtn.Click += (s, ev) =>
            {
                string tip = "将删除本机保存的账号「" + acc.DisplayName + "」的登录信息，" +
                             "之后要切回它需要重新扫码登录。";
                // 【2026-10-03】用户反馈「删除时是否真会把对应的 cookie 完全删除，
                // 旧版 Python Flet 版就删了但是账号还存在」—— 确实是那个问题：
                // 删档案只删了 accounts/{key}.xml，但**当前账号的 Cookie 还留在
                // GeZi.config.xml 里**，所以程序看起来还是登录状态。
                // 现在删当前账号会**同时退出登录**，才算真的删干净。
                if (isCurrent)
                    tip += "\n（这是当前账号：删除后会同时退出登录，需要重新登录才能再用。）";
                var r = AppDialog.Show(owner, tip + "\n\n确定删除吗？", "删除账号",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (r != MessageBoxResult.OK)
                    return;
                if (AccountStore.Remove(acc.Key))
                {
                    Log("已删除本机保存的账号「" + acc.DisplayName + "」");
                    // 删的是当前账号 → 把配置里的 Cookie 一起清掉，否则"账号还在"
                    if (isCurrent)
                    {
                        DoLogout("已删除当前账号，同时退出登录。");
                        owner.Close();
                        return;
                    }
                    rebuild();
                }
                else
                {
                    AppDialog.Show(owner, "删除失败（文件可能被占用）。", "删除账号",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            Grid.SetColumn(delBtn, 2);
            grid.Children.Add(delBtn);

            return new Border
            {
                Child = grid,
                Padding = new Thickness(12, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
                CornerRadius = new CornerRadius(10),
                // 用主题令牌而不是硬编码灰，深色主题才能跟着变
                Background = (Brush)(TryFindResource("SurfaceAltBrush")
                                     ?? new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6))),
                BorderBrush = (Brush)(TryFindResource("BorderBrushSoft")
                                     ?? new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6))),
                BorderThickness = new Thickness(1),
            };
        }

        private async void OnQrLogin(object sender, RoutedEventArgs e)
        {
            // 【2026-10-03】原来的「刷新二维码」按钮已删（用户反馈"多余"）——
            // 它和「获取二维码」本来就是同一个处理函数，点哪个都是重新取一张。
            // 现在只留「获取二维码」：过期时提示用户再点一次即可。
            var src = sender as Button;
            QrLoginBtn.IsEnabled = false;
            SetQrStatus("正在获取二维码…", "#333");

            // ⚠️ 只给「取二维码」这一段加按钮加载态。
            // 后面的轮询是**等用户拿手机扫码**，可能持续几分钟 —— 那不是"加载"，
            // 一直转圈会让人以为程序卡住了。等二维码画出来就立刻收掉加载态。
            long t0 = BusyClock();
            if (!BeginBusy(src, "获取中"))
            {
                QrLoginBtn.IsEnabled = true;
                return;
            }
            try
            {
                using (var svc = new LoginQrService())
                {
                    var (ok, token, qrUrl, err) = await svc.GetQrCodeAsync();
                    await EndBusyAsync(src, t0);
                    t0 = 0;
                    if (!ok)
                    {
                        SetQrStatus(err, "#C0392B");
                        return;
                    }

                    // 在界面里直接画出二维码，用户用夸克 App 扫屏幕即可登录，
                    // 不必再跳到浏览器（浏览器只是备用路径）。
                    ShowQrImage(qrUrl);

                    // 状态随轮询实时上报：等待扫码 → 已扫码待确认 → 成功/过期。
                    // 用 Progress<T> 自动切回 UI 线程（本方法运行在 UI 线程上）。
                    var status = new Progress<QrPollResult>(r =>
                    {
                        switch (r.Stage)
                        {
                            case QrLoginStage.WaitingScan:
                                SetQrStatus("请用夸克 App 扫描二维码（等待扫码…）", "#333");
                                break;
                            case QrLoginStage.ScannedWaitConfirm:
                                SetQrStatus("已扫码，请在手机上点击「确认登录」", "#1B6FB8");
                                break;
                            case QrLoginStage.Expired:
                                SetQrStatus(DescribeExpired(r), "#C0392B");
                                QrLoginBtn.Focus();   // 按钮只剩这一个，把焦点给它方便回车重试
                                break;
                            case QrLoginStage.GotTicket:
                                SetQrStatus("已确认，正在换取登录态…", "#1B6FB8");
                                break;
                        }
                    });

                    var poll = await svc.PollForTicketWithStatusAsync(token, 300, status);
                    if (poll.Stage == QrLoginStage.Expired)
                    {
                        // 过期/无效：收起图片，提示用户再点一次「获取二维码」重新取一张。
                        HideQrImage();
                        SetQrStatus(DescribeExpired(poll) + " 请再点一次「获取二维码」重试。", "#C0392B");
                        return;
                    }

                    var (cok, cookie, cerr) = await svc.ExchangeTicketAsync(poll.Ticket);
                    if (!cok)
                    {
                        SetQrStatus(cerr, "#C0392B");
                        return;
                    }
                    await ApplyLoginAsync(cookie);
                    SetQrStatus("登录成功", "#2E7D32");
                    HideQrImage();
                }
            }
            catch (Exception ex)
            {
                SetQrStatus("登录失败: " + ex.Message, "#C0392B");
            }
            finally
            {
                QrLoginBtn.IsEnabled = true;
                if (t0 != 0) await EndBusyAsync(src, t0);
            }
        }

        /// <summary>把轮询结果翻译成给用户看的过期/失败说明（未识别的码原样展示，不吞信息）。</summary>
        private static string DescribeExpired(QrPollResult r)
        {
            if (r.Status == -1)
                return "扫码超时，二维码可能已失效。";
            if (!string.IsNullOrEmpty(r.Message))
                return "二维码已失效（" + r.Message + "）。";
            return "二维码已失效。";
        }

        /// <summary>
        /// 更新状态提示文本（含颜色与图标），并确保提示框可见。
        ///
        /// colorHex 仍是调用方给的十六进制色（保持原有调用点不动），但**图标**按语义挑：
        /// 不再无脑用黄三角 —— 「正在等待扫码」是正常流程，配警告图标会让人以为出错了。
        /// </summary>
        private void SetQrStatus(string text, string colorHex)
        {
            QrStatusBox.Visibility = Visibility.Visible;
            QrStatusText.Text = text;

            var brush = ResolveStatusBrush(colorHex);
            var icon = PickStatusIcon(text, colorHex, out _);
            QrStatusIcon.Data = icon;
            QrStatusIcon.Stroke = brush;
            QrStatusText.Foreground = brush;
        }

        /// <summary>
        /// 把 <see cref="SetQrStatus"/> 传入的色值字符串解析成**跟随主题**的画刷。
        ///
        /// 【为什么不能直接用那个色值】调用方写的是硬编码十六进制（`#333` / `#C0392B`…），
        /// 其中 `#333`（近黑）在**深色主题下会完全看不见** —— 而这是登录页的状态提示，
        /// 用户看不到就不知道"到底在等什么"。所以这里把"色值"当作**语义标签**，
        /// 原样映射到主题令牌：
        ///   · `#333`             → TextSecondaryBrush（中性进行中）
        ///   · `#1B6FB8`（蓝）     → PrimaryBrush（需要用户操作的提示）
        ///   · `#C0392B`（红）     → DangerBrush
        ///   · `#2E7D32`（绿）     → SuccessBrush
        /// 未知色值走 <paramref name="colorHex"/> 原样解析（保持向后兼容），
        /// 解析失败再退回正文色，绝不出现"看不见的字"。
        /// </summary>
        private System.Windows.Media.Brush ResolveStatusBrush(string colorHex)
        {
            string token = null;
            if (!string.IsNullOrEmpty(colorHex))
            {
                string h = colorHex.TrimStart('#').ToUpperInvariant();
                if (h == "333" || h == "333333" || h == "1F2430") token = "TextSecondaryBrush";
                else if (h == "1B6FB8" || h == "4F6BED") token = "PrimaryBrush";
                else if (h == "C0392B" || h == "EF4444") token = "DangerBrush";
                else if (h == "2E7D32" || h == "10B981") token = "SuccessBrush";
            }

            if (token != null)
            {
                var b = TryFindResource(token) as System.Windows.Media.Brush;
                if (b != null) return b;
            }

            try
            {
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                    .ConvertFromString(colorHex);
                return new System.Windows.Media.SolidColorBrush(c);
            }
            catch { }

            return (TryFindResource("TextPrimaryBrush") as System.Windows.Media.Brush)
                   ?? System.Windows.Media.Brushes.Black;
        }

        /// <summary>
        /// 按文案与颜色推断该用哪个状态图标。语义优先于颜色：
        /// 「正在/等待/…」→ 时钟（中性进行中）；「成功/已确认」→ 绿对勾；
        /// 其余带错误色的 → 红叉；都不匹配才退回中性信息圈。
        /// </summary>
        private static System.Windows.Media.Geometry PickStatusIcon(
            string text, string colorHex, out System.Windows.Media.Brush brush)
        {
            text = text ?? "";
            bool bad = colorHex != null &&
                       (colorHex.IndexOf("C0392B", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        colorHex.IndexOf("EF4444", StringComparison.OrdinalIgnoreCase) >= 0);
            bool good = colorHex != null &&
                        (colorHex.IndexOf("2E7D32", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         colorHex.IndexOf("10B981", StringComparison.OrdinalIgnoreCase) >= 0);

            bool pending = text.Contains("正在") || text.Contains("等待") ||
                           text.Contains("请用") || text.Contains("获取二维码");

            string key;
            if (good || text.Contains("成功")) { key = "IconCheckCircle"; }
            else if (bad || text.Contains("失败") || text.Contains("失效") || text.Contains("超时"))
            { key = "IconXCircle"; }
            else if (pending) { key = "IconClock"; }
            else if (text.Contains("已扫码") || text.Contains("已确认")) { key = "IconCheck"; }
            else { key = "IconInfo"; }

            brush = (System.Windows.Media.Brush)System.Windows.Application.Current
                .FindResource(good ? "SuccessBrush" : (bad ? "DangerBrush" : "TextSecondaryBrush"));
            return (System.Windows.Media.Geometry)System.Windows.Application.Current.FindResource(key);
        }

        /// <summary>把二维码 URL 渲染成图像并显示在登录页。</summary>
        private void ShowQrImage(string content)
        {
            try
            {
                int side = 264;                 // 目标显示边长（与 XAML 里 QrImageBox/QrPlaceholderBox 的 264 对齐）
                var probe = QrEncoder.Encode(content);
                int modules = probe.GetLength(0) + 8;   // 含静默区
                int scale = Math.Max(1, side / modules);

                int w, h, stride;
                byte[] bgr = QrEncoder.EncodeToBgr(content, scale, 4, out w, out h, out stride);
                var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                var data = bmp.LockBits(
                    new System.Drawing.Rectangle(0, 0, w, h),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                try
                {
                    for (int y = 0; y < h; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(
                            bgr, y * stride, data.Scan0 + y * data.Stride, w * 3);
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }

                // Bitmap -> BitmapSource
                var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;
                var decoder = System.Windows.Media.Imaging.BitmapFrame.Create(
                    ms, System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                QrImage.Source = decoder;
                QrImageBox.Visibility = Visibility.Visible;
                if (QrPlaceholderBox != null)
                    QrPlaceholderBox.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                QrStatusText.Text = "二维码绘制失败: " + ex.Message;
                QrImageBox.Visibility = Visibility.Collapsed;
                if (QrPlaceholderBox != null)
                    QrPlaceholderBox.Visibility = Visibility.Visible;
            }
        }

        private void HideQrImage()
        {
            QrImage.Source = null;
            QrImageBox.Visibility = Visibility.Collapsed;
            // 收起二维码后把占位框放回来，保证左卡高度不跳。
            if (QrPlaceholderBox != null)
                QrPlaceholderBox.Visibility = Visibility.Visible;
        }

        private async void OnImportCookie(object sender, RoutedEventArgs e)
        {
            var cookie = CookieImportBox.Text.Trim();
            if (string.IsNullOrEmpty(cookie))
            {
                Log("请先粘贴 Cookie");
                return;
            }
            // 导入要联网校验（拉账号信息）→ 按钮就地变成"登录中…"
            long t0 = BusyClock();
            if (!BeginBusy(ImportCookieBtn, "登录中")) return;
            try
            {
                await ApplyLoginAsync(cookie);
            }
            finally
            {
                await EndBusyAsync(ImportCookieBtn, t0);
            }
        }

        /// <summary>
        /// 从**文件**导入 Cookie 并登录。
        /// </summary>
        /// <remarks>
        /// 【2026-10-03 新增】用户反馈「为什么没有导入 cookie 文件进行登录的功能」——
        /// 原来只能把 Cookie 文本粘进输入框，而浏览器扩展导出的是**文件**。
        /// 支持 .json（Cookie-Editor / EditThisCookie 的导出格式）与 .txt（原始 Cookie 串），
        /// 解析交给 <see cref="GeZi.Core.Support.CookieText"/>。
        /// </remarks>
        private async void OnImportCookieFile(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 Cookie 文件",
                Filter = "Cookie 文件|*.txt;*.json|文本文件 (*.txt)|*.txt|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(this) != true) return;

            string raw;
            try
            {
                raw = File.ReadAllText(dlg.FileName);
            }
            catch (Exception ex)
            {
                Log("读取 Cookie 文件失败：" + ex.Message);
                return;
            }

            string cookie = CookieText.Parse(raw);
            if (string.IsNullOrEmpty(cookie))
            {
                Log("未能从文件中解析出 Cookie：" + dlg.FileName);
                AppDialog.Show(this,
                    "没能从这个文件里解析出 Cookie。\n\n"
                    + "支持两种内容：\n"
                    + "  · 浏览器扩展（如 Cookie-Editor）导出的 .json\n"
                    + "  · 直接复制的 Cookie 文本（.txt，形如 a=1; b=2）",
                    "解析失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 回填到输入框，让用户看得见到底导入了什么
            CookieImportBox.Text = cookie;
            int n = cookie.Split(';').Length;
            Log(string.Format("已从文件导入 Cookie（{0}），共 {1} 项。", dlg.FileName, n));

            long t0 = BusyClock();
            if (!BeginBusy(ImportCookieFileBtn, "登录中")) return;
            try
            {
                await ApplyLoginAsync(cookie);
            }
            finally
            {
                await EndBusyAsync(ImportCookieFileBtn, t0);
            }
        }

        /// <summary>
        /// 导出当前登录 Cookie 到桌面文件。
        /// ⚠️ 用户明确要求：**不要复制到剪贴板**（原话「无论是导出直链还是导出
        /// cookie都不要复制到粘贴板上」）—— 所以这里只写文件。
        /// 写的是**原始 Cookie 串**（`k=v; k=v`），可直接粘回「导入 Cookie」框。
        /// </summary>
        private void OnExportCookie(object sender, RoutedEventArgs e)
        {
            if (_client == null)
            {
                Log("尚未登录");
                return;
            }
            try
            {
                string outFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "GeZi-Cookie-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.WriteAllText(outFile, _client.CookieStr, new UTF8Encoding(false));

                Log("已导出 Cookie：" + outFile);
                AppDialog.Show(this,
                    "已把当前登录 Cookie 导出到：\n\n" + outFile +
                    "\n\n注意：Cookie 等同于账号登录凭证，请勿分享给他人。",
                    "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log("导出 Cookie 失败：" + ex.Message);
                AppDialog.Show(this, "导出 Cookie 失败：\n\n" + ex.Message,
                    "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 退出登录：清掉内存里的客户端 + 配置里保存的 Cookie/昵称/头像。
        /// </summary>
        /// <remarks>
        /// 【2026-10-03】登录页那个「登出」按钮已按用户要求删掉，但这个能力没丢 ——
        /// 它被挪进了「切换账号」窗口（那里才是账号管理的地方），
        /// 并且**删除当前账号时也会走这里**（否则只删档案、Cookie 还在配置里，
        /// 表现为"删了但账号还存在"，正是用户反馈的旧版问题）。
        /// </remarks>
        private void DoLogout(string reason)
        {
            try { _client?.StopKeepAlive(); } catch { }
            _client?.Dispose();
            _client = null;
            _stoken = null;
            _linkCache.Clear();   // 换账号后旧账号的直链必须作废
            _settings.Cookie = "";
            _settings.Who = "";
            _settings.AvatarUrl = "";
            _settings.CurrentAccountKey = "";
            SettingsStore.Save(_settings);
            UpdateAccountCard(null, 0, 0);
            LoginBtn.Visibility = Visibility.Visible;
            AccountsBtn.Visibility = Visibility.Collapsed;
            Log(string.IsNullOrEmpty(reason) ? "已登出" : reason);
        }

        private async Task ApplyLoginAsync(string cookie)
        {
            try
            {
                var client = new QuarkClient(cookie);
                var (valid, nick) = await client.ValidateAsync();

                // ⚠️ account/info 挂在 pan.quark.cn，**.NET 根本连不上**（WAF 按 TLS
                // ClientHello 的曲线组扩展大小做指纹过滤，SChannel 必然被 RST），
                // 所以 ValidateAsync 每次都走兜底 → 昵称是"已登录用户"、头像永远为空
                // —— 这正是用户反馈「都没有名字和头像」的根因。
                // 借 Python 桥（OpenSSL 3.5.7 的 ClientHello 能过）补一次真实账号信息。
                // 昵称/头像走**与手动刷新同一个方法**（这样两处的行为永远一致）
                string realAvatar = null;
                if (valid)
                {
                    var resolved = await ResolveAccountInfoAsync(client, cookie);
                    valid = resolved.valid;
                    if (!string.IsNullOrEmpty(resolved.nick)) nick = resolved.nick;
                    realAvatar = resolved.avatar;
                }

                if (valid)
                {
                    try { _client?.StopKeepAlive(); } catch { }
                    _client?.Dispose();
                    _client = client;
                    // 保活心跳连续失败 → 登录很可能已失效。事件在后台线程上触发，
                    // 必须切回 UI 线程再动界面。
                    _client.LoginExpired += () =>
                    {
                        try
                        {
                            Dispatcher.BeginInvoke(new Action(
                                () => ShowLoginExpired("保活心跳连续失败")));
                        }
                        catch { }
                    };
                    _client.StartKeepAlive();
                    _loginExpired = false;   // 新登录成功 → 清掉"已过期"标记
                    _linkCache.Clear();   // 换账号后旧账号的直链必须作废
                    _settings.Cookie = client.CookieStr;
                    _settings.Who = nick;
                    // 头像：探测到的就存下来（拿不到存空串 → 界面回退"昵称首字"）
                    _settings.AvatarUrl = realAvatar ?? client.AvatarUrl ?? "";
                    SettingsStore.Save(_settings);

                    // 登录成功后存一份账号档案，之后可在「切换账号」里一键切回，不用再扫码。
                    // 标识优先用夸克返回的稳定 uid，取不到时回退昵称（同名不同账号才会冲突）。
                    string acctKey = !string.IsNullOrEmpty(client.AccountSig) ? client.AccountSig : nick;
                    if (!string.IsNullOrEmpty(acctKey))
                        AccountStore.Save(acctKey, nick, client.CookieStr);
                    // 记住"当前是哪个账号" —— 账号列表用它判断哪一行的「切换」该置灰
                    _settings.CurrentAccountKey = acctKey ?? "";

                    UpdateAccountCard(nick, 0, 0);
                    LoginBtn.Visibility = Visibility.Collapsed;
                    AccountsBtn.Visibility = Visibility.Visible;
                    Log("登录成功: " + nick);
                    await RefreshCapacityAsync();
                }
                else
                {
                    client.Dispose();
                    UpdateAccountCard(null, 0, 0);
                    AccountHintText.Text = "Cookie 已失效，请重新登录";
                    Log("登录校验失败: " + nick);
                }
            }
            catch (Exception ex)
            {
                Log("登录出错: " + ex.Message);
            }
        }

        /// <summary>
        /// 账号卡上那个小刷新按钮：**重新校验登录态 + 拉最新昵称/头像/容量**。
        ///
        /// 与定时刷容量的区别：定时那条只刷容量（省请求），这个按钮是用户主动点的，
        /// 所以连登录态一起重新校验 —— 万一登录其实已经失效，点一下就能看出来。
        /// </summary>
        /// <summary>
        /// `QuarkClient.ValidateAsync` 在拿不到真名时返回的**兜底昵称**。
        /// ⚠️ 这个值**绝不能写进配置** —— 否则真实昵称就被它覆盖掉了。
        /// </summary>
        private const string FallbackNick = "已登录用户";

        /// <summary>
        /// 取**真实**的账号昵称与头像（登录时、手动刷新时共用这一份）。
        ///
        /// 【为什么必须共用】`ValidateAsync` 里的昵称**永远**是兜底的"已登录用户" ——
        /// account/info 挂在 `pan.quark.cn`，.NET 的 SChannel 必然被 WAF RST（见上面长注释）。
        /// 真名和头像只能借 Python 桥（OpenSSL 3.5.7 能过）。
        ///
        /// ⚠️ **踩过的坑**：手动刷新那一版只调了 `ValidateAsync`、没走 Python 桥，
        /// 于是把好名字覆盖成了"已登录用户"（用户实测：「点了右上角刷新之后我的名字没了」）。
        /// 所以刷新与登录**必须走同一个方法**。
        /// </summary>
        private async Task<(bool valid, string nick, string avatar)> ResolveAccountInfoAsync(
            QuarkClient client, string cookie)
        {
            var (valid, nick) = await client.ValidateAsync();
            if (!valid) return (false, null, null);

            // 昵称是兜底值、或还没有头像 → 借 OpenSSL 桥补真实信息
            if (nick == FallbackNick || string.IsNullOrEmpty(client.AvatarUrl))
            {
                try
                {
                    var info = await Task.Run(() => PanAccountBridge.FetchAccountInfo(cookie));
                    if (info.HasValue && info.Value.Ok)
                    {
                        if (!string.IsNullOrEmpty(info.Value.Nickname))
                            nick = info.Value.Nickname;
                        if (!string.IsNullOrEmpty(info.Value.Avatar))
                            client.SetAvatarUrl(info.Value.Avatar);
                    }
                    else if (info.HasValue && !string.IsNullOrEmpty(info.Value.Error))
                    {
                        Log("取账号昵称/头像失败：" + info.Value.Error);
                    }
                }
                catch (Exception ex)
                {
                    // 拿不到就用兜底值，绝不让它影响登录
                    Log("取账号昵称/头像出错：" + ex.Message);
                }
            }
            return (true, nick, client.AvatarUrl);
        }

        private async void OnRefreshAccountClick(object sender, RoutedEventArgs e)
        {
            if (_client == null)
            {
                Log("尚未登录，先扫码登录吧");
                try { Tabs.SelectedIndex = 3; } catch { }
                return;
            }
            long t0 = BusyClock();
            if (!BeginBusy(AccountRefreshBtn, "")) return;   // 忙时按钮显示转圈
            try
            {
                var (valid, nick, avatar) = await ResolveAccountInfoAsync(_client, _settings.Cookie);
                if (!valid)
                {
                    Log("账号状态刷新：登录已失效");
                    ShowLoginExpired("刷新时发现登录已失效");
                    return;
                }

                // 昵称可能变了（改过夸克昵称）→ 存起来并刷新头像。
                // ⚠️ **兜底昵称"已登录用户"绝不写进去** —— 写了就把真名覆盖没了
                //    （这正是上一版刷新把名字刷丢的原因）。
                bool nickOk = !string.IsNullOrWhiteSpace(nick) && nick != FallbackNick;
                if (nickOk && nick != _settings.Who)
                    _settings.Who = nick;
                if (!string.IsNullOrWhiteSpace(avatar) && avatar != _settings.AvatarUrl)
                    _settings.AvatarUrl = avatar;
                try { SettingsStore.Save(_settings); } catch { }

                await RefreshCapacityAsync();
                Log("已刷新账号状态" + (string.IsNullOrWhiteSpace(_settings.Who)
                    ? "" : "：" + _settings.Who));
            }
            catch (Exception ex)
            {
                Log("刷新账号状态失败：" + ex.Message);
            }
            finally
            {
                await EndBusyAsync(AccountRefreshBtn, t0);
            }
        }

        /// <summary>
        /// 登录已失效的统一提示（心跳发现 / 手动刷新发现 都走这里）。
        ///
        /// 三件事一起做，缺一不可：
        ///   ① 账号卡变成"登录已过期"的红色状态（否则界面还显示着已登录，用户不知道）；
        ///   ② 弹一条托盘通知（用户可能正把窗口收在托盘里）；
        ///   ③ 写一行日志。
        /// 重新扫码登录成功后，`ApplyLoginAsync` 会把卡片恢复正常。
        /// </summary>
        private void ShowLoginExpired(string reason)
        {
            _loginExpired = true;
            try
            {
                AccountText.Text = "登录已过期";
                AccountText.ToolTip = "登录已过期，请到「登录」页重新扫码";
                AccountHintText.Text = "点这里重新扫码登录";
                AvatarText.Text = "!";
                AvatarText.Visibility = Visibility.Visible;
                AvatarImage.Visibility = Visibility.Collapsed;
                AvatarImage.Source = null;
                AvatarBox.Background = (Brush)FindResource("DangerLightBrush");
                CapacityBar.Value = 0;
                CapacityBar.Foreground = (Brush)FindResource("DangerBrush");
                CapacityBar.ToolTip = null;
                CapacityText.Text = "登录已过期";
                SyncLoginPageState(null, 0, 0);
            }
            catch { }

            Log("登录已过期：" + (reason ?? "") + " 请到「登录」页重新扫码。");
            try
            {
                _tray?.ShowBalloon("登录已过期",
                    "夸克登录状态已失效，请打开主窗口到「登录」页重新扫码。");
            }
            catch { }
        }

        /// <summary>
        /// 定时刷容量（默认 5 分钟，与保活心跳同频）。
        ///
        /// 原来容量**只在启动那一次拉取**，所以下载完一批文件后卡片上的
        /// "已用 / 总量"还是启动时的数字（用户问「账号状态多久刷新一次」时发现的）。
        /// 这里补上定时刷新；隐藏到托盘时也照刷（一个很轻的接口）。
        /// </summary>
        private async void OnCapacityTick()
        {
            if (_client == null || _loginExpired) return;
            try { await RefreshCapacityAsync(); } catch { }
        }

        private async Task RefreshCapacityAsync()
        {
            if (_client == null)
                return;
            try
            {
                var (total, used) = await _client.GetCapacityAsync();
                if (total > 0)
                {
                    // 容量条已经显示了用量，这里只更新卡片（昵称保持当前值）。
                    UpdateAccountCard(_settings.Who, used, total);
                }
                else
                {
                    UpdateAccountCard(_settings.Who, 0, 0);
                }
            }
            catch { }
        }

        // ---------------- 窗口关闭 / 托盘驻留 ----------------
        //
        // 【历史】2026-10-02 曾整套删掉"关闭到托盘"：那时点 × 就隐藏，但**没有可发现的
        // 退出入口**，用户以为关了其实还在跑，再点 exe 又因为找不到可见窗口而"什么都不弹"。
        //
        // 【2026-10-03 恢复】用户要求加回后台功能 + 小窗，并明确了行为：
        //   「第一次时会询问用户，然后可以选择最小化托盘或者是直接关闭程序，
        //     并且在这个弹窗的右下角可以勾选此后不再提示，在设置中可以修改」。
        // 所以现在的语义是：`AppSettings.OnCloseButton` 决定 ——
        //   Ask（默认，只问一次）→ 弹询问框，勾了「不再提示」就把选择写进设置；
        //   MinimizeToTray → 隐藏到托盘（下载继续）；
        //   Exit → 真退出。
        // ⚠️ 无论哪种模式，**托盘右键菜单里必须有「退出」**，这是唯一不会被误解的出口。

        private void OnWindowClosing(object sender, CancelEventArgs e)
        {
            if (_exiting)
                return;   // 已经在走退出流程，放行

            var action = _settings.OnCloseButton;
            if (action == CloseAction.Ask)
            {
                bool dontAsk;
                var r = AppDialog.ShowWithDontAsk(this,
                    "要让鸽子下载自用版继续在后台下载吗？\n\n" +
                    "· 最小化到托盘：窗口收进通知区，下载继续跑；\n" +
                    "   想彻底退出，右键托盘图标选「退出」。\n" +
                    "· 直接退出程序：立即结束进程（已下部分会保留，下次可续传）。",
                    "关闭窗口",
                    "最小化到托盘", "直接退出程序",
                    MessageBoxImage.Question, out dontAsk);

                action = (r == MessageBoxResult.OK)
                    ? CloseAction.MinimizeToTray : CloseAction.Exit;

                if (dontAsk)
                {
                    // 勾了「此后不再提示」→ 记住这次的选择（设置页里还能改回来）。
                    // ⚠️ 只改这一个字段 + 落盘，**不要**走 ApplySettings()：
                    //    那会重读所有控件、重建调度器、还打一行"设置已生效"，纯属副作用。
                    _settings.OnCloseButton = action;
                    try { SettingsStore.Save(_settings); } catch { }
                    try { LoadSettingsToUi(); } catch { }
                    Log("已记住「关闭按钮」行为：" +
                        (action == CloseAction.MinimizeToTray ? "最小化到托盘" : "直接退出程序") +
                        "（可在设置页修改）");
                }
            }

            if (action == CloseAction.MinimizeToTray)
            {
                // 【2026-10-21】隐藏到托盘时也立刻落盘：
                // 用户可能挂在托盘里很久甚至几天，期间如果断电/强杀，
                // 去抖还没落地的那次导航就丢了。
                FlushBrowsePosition();
                e.Cancel = true;      // 不真关，只隐藏
                HideToTray();
                return;
            }

            // 直接退出：若还有任务在跑，先问一句（避免误关）
            if (_batchRunning)
            {
                var r = AppDialog.Show(this,
                    "还有下载任务正在运行，确定要退出吗？\n（退出会中断本次下载，已下部分会保留，下次可续传。）",
                    "退出确认", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (r != MessageBoxResult.OK)
                {
                    e.Cancel = true;
                    return;
                }
            }
            _exiting = true;
            // 【2026-10-21】退出前把浏览位置**立刻**写盘：
            // SaveBrowsePosition 是 1.5 秒去抖的，用户在去抖窗口内关程序会丢掉最后一次导航。
            FlushBrowsePosition();
            // 不放行也不行：这里只做清理，真正退出统一走 ExitApp()，
            // 否则"最后关掉的那个窗口"会决定进程去留（App 已设 OnExplicitShutdown）。
            e.Cancel = true;
            ExitApp();
        }

        /// <summary>隐藏到托盘（窗口 Hide，进程与下载继续）。</summary>
        private void HideToTray()
        {
            try
            {
                Hide();
                // 隐藏时把日志刷新降频：没人在看，200ms 一次的布局重排纯属浪费。
                // 队列仍在攒（不丢日志），恢复显示时会一次性刷出来。
                if (_logFlushTimer != null)
                    _logFlushTimer.Interval = TimeSpan.FromMilliseconds(LogFlushMsHidden);
                if (_tray != null && !_trayHintShown)
                {
                    _trayHintShown = true;
                    _tray.ShowBalloon("鸽子下载自用版仍在后台运行",
                        "下载会继续。双击托盘图标可重新打开窗口，右键可选「退出」。");
                }
                Log("已最小化到托盘，下载继续。右键托盘图标可选「退出」。");
            }
            catch { }
        }

        /// <summary>把主窗从托盘/最小化状态拉起来。也被"再点一次 exe"的跨进程消息调用。</summary>
        private void RestoreFromTray()
        {
            try
            {
                if (_logFlushTimer != null)
                    _logFlushTimer.Interval = TimeSpan.FromMilliseconds(LogFlushMs);
                if (!IsVisible) Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                // 有些情况下 Activate() 仍抢不到前台（前台锁定），再用 Win32 兜一刀
                var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (h != IntPtr.Zero)
                {
                    NativeMethods.ShowWindow(h, NativeMethods.SW_RESTORE);
                    NativeMethods.SetForegroundWindow(h);
                }
            }
            catch { }
        }

        /// <summary>真正退出程序：清托盘 → 关小窗 → 关主窗 → Shutdown。</summary>
        private void ExitApp()
        {
            _exiting = true;
            try { _tray?.Dispose(); _tray = null; } catch { }
            try { CloseMiniWindow(); } catch { }
            try { Close(); } catch { }
            try { Application.Current.Shutdown(); } catch { }
        }

        /// <summary>创建托盘图标（在 Loaded 里调，此时窗口句柄已存在）。</summary>
        private void InitTray()
        {
            try
            {
                // 提示文案要把"单击/双击"讲清楚 —— 这两种行为不写出来没人猜得到。
                _tray = new TrayIcon("鸽子下载自用版 · 单击开关小窗，双击打开主窗口，右键更多");
                // 用户指定：**单击 = 小窗，双击 = 主窗口**。
                // （单击动作在 TrayIcon 里延迟一个双击间隔执行，否则双击会先触发单击。）
                _tray.SingleClickRequested += ToggleMiniWindow;
                _tray.DoubleClickRequested += RestoreFromTray;
                _tray.ExitRequested += ExitApp;
                _tray.ToggleMiniRequested += ToggleMiniWindow;
                _tray.PauseAllRequested += () => OnPauseAllOrResume(null, null);
                _tray.SetMiniVisible(false);
                _tray.SetAllPaused(false);
                // 主题切换后托盘图标要重画（图标是一次性渲染的位图，不会自动跟资源字典变）。
                // ⚠️ ThemeChanged 在 ThemeManager 里是**同步**触发的（用户在设置页点单选钮 = UI 线程），
                //    但**跟随系统**模式下是 SystemEvents 线程触发的 → ThemeManager 已经切回 UI 线程，
                //    这里再保一道 Dispatcher，别赌调用方。
                ThemeManager.ThemeChanged += OnThemeChangedRedrawTray;
            }
            catch (Exception ex)
            {
                Log("托盘图标创建失败（不影响下载）：" + ex.Message);
            }
        }

        /// <summary>显示 / 隐藏小窗（托盘菜单用）。</summary>
        private void ToggleMiniWindow()
        {
            if (_mini != null && _mini.IsVisible) CloseMiniWindow();
            else ShowMiniWindow();
        }

        /// <summary>
        /// 主题切换后重画托盘图标（2026-10-04 新增）。
        ///
        /// 订阅在 <see cref="InitTray"/> 里（此时 _tray 才存在），退出时在
        /// <see cref="ExitApp"/> 前面的 Dispose 路径里已经 Dispatcher 掉了 —— 但为了
        /// 不留悬挂引用，OnExit 之前若窗口已关，事件仍持有 this；这里用弱式保护：
        /// 回调里先看 `_exiting`，退出中直接忽略。
        /// </summary>
        private void OnThemeChangedRedrawTray(bool isDark)
        {
            try
            {
                if (_exiting) return;
                // 保险：跟随系统模式下 ThemeManager 已切回 UI 线程，这里再兜一层。
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(() => OnThemeChangedRedrawTray(isDark)));
                    return;
                }
                _tray?.RefreshIcon();
            }
            catch { }
        }

        private void ShowMiniWindow()
        {
            try
            {
                if (_mini == null)
                {
                    _mini = new MiniWindow(_tasks);
                    _mini.OpenMainRequested += RestoreFromTray;
                    _mini.HideRequested += CloseMiniWindow;
                    _mini.PositionChanged += (l, t) =>
                    {
                        // 拖动过程中会疯狂触发，这里只记值；真正落盘在关闭时做一次。
                        _miniLeft = l; _miniTop = t;
                    };
                    _mini.Closed += (s, e) =>
                    {
                        _settings.MiniLeft = _miniLeft;
                        _settings.MiniTop = _miniTop;
                        _settings.MiniPosSet = true;
                        ApplySettings();
                        _mini = null;
                        _tray?.SetMiniVisible(false);
                    };
                }

                if (_settings.MiniPosSet)
                {
                    _mini.WindowStartupLocation = WindowStartupLocation.Manual;
                    _mini.Left = _settings.MiniLeft;
                    _mini.Top = _settings.MiniTop;
                }
                else
                {
                    // 首次打开：贴到工作区右下角，不挡中间
                    var wa = SystemParameters.WorkArea;
                    _mini.WindowStartupLocation = WindowStartupLocation.Manual;
                    _mini.Left = wa.Right - 360;
                    _mini.Top = wa.Bottom - 260;
                }
                _miniLeft = _mini.Left;
                _miniTop = _mini.Top;

                _mini.Show();
                _mini.Activate();
                _tray?.SetMiniVisible(true);
            }
            catch (Exception ex)
            {
                Log("小窗打开失败：" + ex.Message);
            }
        }

        private void CloseMiniWindow()
        {
            try
            {
                if (_mini != null)
                {
                    _mini.Shutdown();
                    _mini.Close();
                }
            }
            catch { }
            _mini = null;
            _tray?.SetMiniVisible(false);
        }

        // ---------------- 自绘标题栏 ----------------

        // ============================================================
        // 侧栏导航 ↔ 内容区 双向同步
        // ============================================================
        //
        // 【为什么是双向】上一轮把侧栏放在 TabControl 的 ControlTemplate 里，
        // 结果模板内的 x:Name 不是类字段，账号卡根本访问不到（CS0103）。
        // 这一轮把侧栏搬成 MainWindow 里的独立 ListBox，于是需要两处同步：
        //   · 用户点侧栏 ListBox  → 要把 Tabs.SelectedIndex 跟过去
        //   · 代码里写 Tabs.SelectedIndex（全项目共 6 处：登录跳转、下载后跳任务页…）
        //                         → 要把 NavList.SelectedIndex 跟过来
        // 判等是防死循环的关键：不加判等，两边会互相触发 SelectionChanged
        // 形成无限递归（WPF 里表现为栈溢出崩掉，或界面卡死）。
        //
        // 用 SelectionChanged 事件而不是 SelectedIndex 依赖属性回调：
        // 事件在属性值真的变化时才触发，语义更贴合，也不引入额外的绑定开销。

        private bool _syncingNav;

        private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingNav)
                return;
            try
            {
                int i = NavList.SelectedIndex;
                if (i < 0 || i == Tabs.SelectedIndex)
                    return;
                _syncingNav = true;
                Tabs.SelectedIndex = i;
            }
            catch { }
            finally { _syncingNav = false; }
        }

        private void OnTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 切走「任务」页时顺手退出删除选择模式：否则回来会看到一个还亮着的提示条，
            // 而用户此时并没有在选东西。
            if (_taskSelectMode && Tabs.SelectedIndex != 1)
                ExitTaskSelectMode();

            // 【2026-10-21】同理：离开下载页就退出多选模式。
            // 否则切到别的页面时提示条不见了、模式却还开着，回来点文件会对不上预期。
            if (_fileSelectMode && Tabs.SelectedIndex != 0)
                ExitFileSelectMode(clearSelection: false);

            // 【2026-10-21】进入设置页时刷一次「关于与更新」卡片：
            // 启动检查是异步的，可能在用户切过来之后才拿到结果。
            // Tab 顺序：0 下载 / 1 任务 / 2 设置 / 3 登录
            if (Tabs.SelectedIndex == 2)
                RefreshSettingsUpdateUi();

            if (_syncingNav)
                return;
            try
            {
                int i = Tabs.SelectedIndex;
                if (i < 0 || i == NavList.SelectedIndex)
                    return;
                _syncingNav = true;
                NavList.SelectedIndex = i;
            }
            catch { }
            finally { _syncingNav = false; }
        }

        /// <summary>点账号卡直接去「登录」页 —— 侧栏底部那块地方小，放不了按钮。</summary>
        /// <summary>侧栏品牌区那个「?」按钮：打开「关于」窗口（软件介绍 + 赞赏码）。</summary>
        private void OnAboutClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var w = new AboutWindow { Owner = this };
                w.ShowDialog();
            }
            catch (Exception ex)
            {
                Log("打开「关于」窗口失败：" + ex.Message);
            }
        }

        private void OnAccountCardClick(object sender, MouseButtonEventArgs e)
        {
            // ⚠️ 卡片上还嵌了个「刷新」小按钮。Button 一般会自己吃掉鼠标事件，
            //    但这里再挡一道：点到按钮时不要顺带跳到登录页。
            var d = e.OriginalSource as DependencyObject;
            while (d != null)
            {
                if (d is Button) return;
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
            try { Tabs.SelectedIndex = 3; } catch { }
        }

        // ============================================================
        // 账号卡刷新（侧栏左下角）
        // ============================================================

        /// <summary>
        /// 异步把账号头像图加载到 <paramref name="img"/>；拿不到就保持"昵称首字"回退显示。
        /// </summary>
        /// <remarks>
        /// 【2026-10-03】用户要求「我登录账号后希望能够显示账号的名称还有账号图片」。
        /// 名称本来就在显示（侧栏账号卡 + 登录页都会显示昵称）；头像原来只有
        /// "昵称首字 + 散列底色"的占位，这里补上真实头像。
        ///
        /// ⚠️ 头像 URL 是**探测**出来的（夸克没公开字段名，Python 版也从没用过），
        ///    可能为空、也可能过期 —— 所以**任何失败都必须静默回退**，
        ///    绝不能让"拿不到头像"这件事冒出错误提示或影响登录。
        /// </remarks>
        private async Task LoadAvatarAsync(System.Windows.Controls.Image img,
            System.Windows.Controls.TextBlock fallback, string url)
        {
            if (img == null) return;

            // 同一个 URL 已经加载过就不重复下载。
            // ⚠️ 启动时 UpdateAccountCard 会被调用好几次（自动登录 → 刷容量 → 同步登录页），
            // 不拦的话同一张头像会被下载 4 次（实测日志里出现 4 行「账号头像已加载」）。
            if (img.Source != null &&
                string.Equals(img.Tag as string, url, StringComparison.Ordinal))
                return;

            if (string.IsNullOrEmpty(url))
            {
                img.Source = null;
                img.Tag = null;
                img.Visibility = Visibility.Collapsed;
                if (fallback != null) fallback.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                byte[] bytes = await Task.Run(() =>
                {
                    using (var wc = new System.Net.WebClient())
                    {
                        wc.Headers["User-Agent"] = QuarkConstants.DlUa;
                        return wc.DownloadData(url);
                    }
                });

                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                // OnLoad：立刻把流读进内存，否则流被回收后图会变成空白
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();   // 冻结后可以跨线程/被多处共享

                img.Source = bmp;
                img.Tag = url;      // 记住已加载的 URL，避免重复下载
                img.Visibility = Visibility.Visible;
                if (fallback != null) fallback.Visibility = Visibility.Collapsed;
                // 【2026-10-03】成功**不写日志** —— 用户要求「这两个就不要在日志中显示了，
                // 他们俩失败时才显示失败」。下面 catch 里的失败日志保留。
            }
            catch (Exception ex)
            {
                // 拿不到就回退首字头像 —— 不打扰用户，但记一行日志便于排查
                Log("账号头像加载失败，已回退为昵称首字：" + ex.GetType().Name + ": " + ex.Message);
                img.Source = null;
                img.Tag = null;
                img.Visibility = Visibility.Collapsed;
                if (fallback != null) fallback.Visibility = Visibility.Visible;
            }
        }

        //
        // 【为什么要收拢成一个方法】原来"账号/容量"散在 5 处直接给
        // AccountText.Text / CapacityText.Text 赋值，措辞还各不相同
        // （"已登录: 已登录用户" / "未登录（Cookie 无效）"）。字面量拼在
        // 调用点，想统一改个措辞就要翻五处，极易漏改。现在全部走这一个入口。
        //
        // nick 为 null / 空 = 未登录态。

        /// <summary>
        /// 刷新左下角账号卡。
        ///
        /// 【为什么容量区不再 Collapsed】卡片高度必须**两种状态一致**，
        /// 否则登录/登出时整张卡会明显跳高跳矮（用户截图反馈过）。
        /// 现在未登录也保留容量条的位置，只是画成 0 值的灰色空槽。
        /// </summary>
        /// <param name="nick">昵称；null 或空表示未登录</param>
        /// <param name="used">已用容量（字节）</param>
        /// <param name="total">总容量（字节）；≤0 表示未知，显示空槽</param>
        private void UpdateAccountCard(string nick, long used, long total)
        {
            try
            {
                bool logged = !string.IsNullOrWhiteSpace(nick);

                if (!logged)
                {
                    AvatarText.Text = "?";
                    AvatarBox.Background = (Brush)FindResource("BorderBrushStrong");
                    AvatarImage.Source = null;
                    AvatarImage.Visibility = Visibility.Collapsed;
                    AvatarText.Visibility = Visibility.Visible;
                    AccountText.Text = "未登录";
                    AccountText.ToolTip = null;
                    AccountHintText.Text = "登录后可浏览网盘";
                    // 空槽：值为 0、用最浅的状态色画一条，保持占位不消失
                    CapacityBar.Value = 0;
                    CapacityBar.Foreground = (Brush)FindResource("BorderBrushStrong");
                    CapacityBar.ToolTip = null;
                    CapacityText.Text = "容量未知";

                    SyncLoginPageState(null, 0, 0);
                    return;
                }

                // 头像：取昵称首字符。比画一个通用人形图标更有"这是我的账号"的归属感，
                // 而且不需要额外请求头像图片（夸克接口给的昵称足够）。
                string first = nick.Trim().Substring(0, 1);
                AvatarText.Text = first.ToUpperInvariant();
                AvatarBox.Background = AvatarTintFor(nick);
                // 有真头像就异步换上（拿不到保持首字回退，不打扰用户）
                var ignoredAvatar = LoadAvatarAsync(AvatarImage, AvatarText, _settings.AvatarUrl);

                AccountText.Text = nick;
                AccountText.ToolTip = nick;   // 昵称过长被截断时仍可悬停看全
                AccountHintText.Text = "夸克网盘";

                if (total > 0)
                {
                    double pct = Math.Max(0, Math.Min(100, used * 100.0 / total));
                    CapacityBar.Value = pct;

                    // 状态色编码（绿 → 琥珀 → 红）。
                    //
                    // 【2026-10-03 用户问「剩余容量小于 1G 会变红吗」——原来**不会**，
                    //  只看占用率。但纯百分比对两种极端都不合理：
                    //   · 10TB 账号用掉 99.99%（剩 1GB）→ 其实已经快满了，红是对的；
                    //   · 5GB 账号用掉 82%（剩 0.9GB）→ 只剩不到 1G 了却是琥珀，不合理。
                    //  所以补一条**绝对阈值**：剩余不足 1GB 也直接判红。】
                    //
                    // 【2026-10-03 再补】用户问「夸克容量能超过默认值，比如 20G/10.4G，
                    //  这时进度条什么颜色？我希望保持红色」。
                    //  pct 已被上面 Clamp 到 100（所以进度条是**满格**而不是溢出/归零），
                    //  但 free = total - used 会变成负数 → freeTooLow 自然为真 → 红。
                    //  为了不依赖这个"顺带成立"的巧合，这里显式加一条 overQuota 判据：
                    //  ① 语义直白；② 万一以后有人改了上面的 Clamp 也不会翻车；
                    //  ③ 提示语里能把"超额"说清楚，而不是像原来那样显示"剩余 0"（会让人困惑）。
                    long free = total - used;
                    bool overQuota = used > total;
                    bool freeTooLow = free < LowFreeBytes;
                    CapacityBar.Foreground = (Brush)FindResource(
                        (overQuota || pct >= 90 || freeTooLow) ? "CapacityHighBrush"
                        : pct >= 70 ? "CapacityMidBrush"
                        : "CapacityLowBrush");

                    CapacityText.Text = Util.FormatSize(used) + " / " + Util.FormatSize(total);
                    CapacityBar.ToolTip = "已用 " + Util.FormatSize(used)
                                        + " / 共 " + Util.FormatSize(total)
                                        + "（" + pct.ToString("0.#") + "%）"
                                        + (overQuota
                                            ? "\n已超出默认容量 " + Util.FormatSize(used - total)
                                            : "\n剩余 " + Util.FormatSize(free < 0 ? 0 : free)
                                              + (freeTooLow ? "　⚠ 剩余不足 1 GB" : ""));
                }
                else
                {
                    // 已登录但容量接口没返回：同样留空槽，保证高度不变
                    CapacityBar.Value = 0;
                    CapacityBar.Foreground = (Brush)FindResource("BorderBrushStrong");
                    CapacityBar.ToolTip = null;
                    CapacityText.Text = "容量获取中…";
                }

                SyncLoginPageState(nick, used, total);
            }
            catch { }
        }

        /// <summary>
        /// 同步「登录」页里的当前状态卡（与侧栏账号卡同源，避免两处状态不一致）。
        /// 侧栏那块地方小，只放昵称+容量；这里空间大，补上容量明细与操作按钮的可见性。
        /// </summary>
        private void SyncLoginPageState(string nick, long used, long total)
        {
            if (LoginWhoText == null)
                return;
            try
            {
                bool logged = !string.IsNullOrWhiteSpace(nick);
                if (!logged)
                {
                    LoginAvatarText.Text = "?";
                    LoginAvatarBox.Background = (Brush)FindResource("BorderBrushStrong");
                    LoginAvatarImage.Source = null;
                    LoginAvatarImage.Visibility = Visibility.Collapsed;
                    LoginAvatarText.Visibility = Visibility.Visible;
                    LoginWhoText.Text = "未登录";
                    LoginSubText.Text = "扫码或导入 Cookie 即可登录";
                    return;
                }

                LoginAvatarText.Text = nick.Trim().Substring(0, 1).ToUpperInvariant();
                LoginAvatarBox.Background = AvatarTintFor(nick);
                var ignoredLoginAvatar = LoadAvatarAsync(
                    LoginAvatarImage, LoginAvatarText, _settings.AvatarUrl);
                LoginWhoText.Text = nick;
                LoginSubText.Text = total > 0
                    ? "已用 " + Util.FormatSize(used) + " / 共 " + Util.FormatSize(total)
                    : "已登录（容量获取中…）";
            }
            catch { }
        }

        /// <summary>按昵称散列挑一个头像底色，同名每次结果一致（不会闪来闪去）。</summary>
        private Brush AvatarTintFor(string nick)        {
            string[] keys =
            {
                "AvatarTint1Brush", "AvatarTint2Brush", "AvatarTint3Brush",
                "AvatarTint4Brush", "AvatarTint5Brush",
            };
            int hash = 0;
            foreach (char c in nick)
                hash = (hash * 31 + c) & 0x7FFFFFFF;
            return (Brush)FindResource(keys[hash % keys.Length]);
        }

        /// <summary>
        /// 同步两个列表的「空状态插画」可见性。
        /// 纯皮肤逻辑：集合没内容（或筛选后全被过滤掉）时显示提示，否则隐藏。
        ///
        /// 【2026-10-02 改造】现在还会参考"是否正在加载"：
        /// 加载中显示"正在加载…"，加载完且为空才显示"还没有内容"。
        /// 不加这个判断的话，加载的几百毫秒里会闪出"还没有内容"，误导用户。
        /// </summary>
        private void UpdateEmptyStates()
        {
            try
            {
                if (FileEmptyState != null)
                {
                    // 用筛选视图的计数：筛选后一条不剩时也应该显示空状态；
                    // 但筛选是本地过滤，跟"正在加载"无关 —— 筛选出 0 条时
                    // 仍应显示空状态，不该显示"正在加载"。
                    // 没有筛选词时 _files.Count 就是可见条数，**不必整表枚举**视图 ——
                    // 几千项的目录里，这个枚举是卡顿的主因之一。
                    int n = string.IsNullOrEmpty(_fileFilter)
                        ? _files.Count
                        : (_filesView != null ? _filesView.Cast<object>().Count() : _files.Count);
                    bool loading = _loadingDir && _files.Count == 0 && string.IsNullOrEmpty(_fileFilter);

                    if (FileLoadingState != null)
                        FileLoadingState.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;

                    FileEmptyState.Visibility = (!loading && n == 0)
                        ? Visibility.Visible : Visibility.Collapsed;

                    // 【2026-10-21】列表变空（进空目录 / 筛选无结果）时自动退出多选模式：
                    // 否则提示条会挂在"没有内容"的界面上方，用户反馈过这种情况。
                    // 用 silent 语义的退出（不清选择，反正也没得选），避免刷日志。
                    if (n == 0 && _fileSelectMode)
                        ExitFileSelectMode(clearSelection: false, silent: true);

                    // 空状态的文案按模式区分：分享模式该说"粘贴链接"，
                    // 网盘模式该说"这个目录是空的"。一句通用文案两边都不贴切。
                    if (FileEmptyText != null)
                        FileEmptyText.Text = ShareMode ? "还没有内容" : "这个目录是空的";
                }
                if (TaskEmptyState != null)
                    TaskEmptyState.Visibility = _tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        /// <summary>已排队的空状态刷新（把连续多次集合变化合并成一次计算）。</summary>
        private bool _emptyStatePending;

        /// <summary>
        /// 合并版 <see cref="UpdateEmptyStates"/>：置个标记，攒到本轮 UI 空闲时才算一次。
        ///
        /// 【为什么要合并】往列表里铺目录时集合会连着变很多次，而 UpdateEmptyStates
        /// 要数一遍可见条数（有筛选词时是整表枚举）—— 一次铺几千条就是 O(n²)，
        /// 界面直接卡死（几千项目录的用户实测反馈）。合并之后一批变化只算一次。
        /// </summary>
        private void RequestUpdateEmptyStates()
        {
            if (_emptyStatePending) return;
            _emptyStatePending = true;
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _emptyStatePending = false;
                    UpdateEmptyStates();
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { _emptyStatePending = false; }
        }

        // 用 WindowChrome 去掉了系统边框，所以拖拽/双击最大化要自己接。
        // 注意：窗口按钮本身标了 WindowChrome.IsHitTestVisibleInChrome=True，
        // 它们的 Click 不会被下面的 MouseLeftButtonDown 吞掉。

        private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                // 双击标题栏 = 最大化 / 还原（与系统行为一致）
                ToggleMaximize();
                return;
            }
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            // 最大化后窗口会贴上屏幕边缘并盖住我们画的 1px 描边，
            // 用 Padding 让内容不被屏幕边缘裁掉。
            RootPadding();
        }

        /// <summary>最大化时给根 Grid 留出边距，否则自定义描边会被屏幕边缘吃掉。</summary>
        private void RootPadding()
        {
            try
            {
                if (Content is Border bd && bd.Child is Grid g)
                    g.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            }
            catch { }
        }

        private void OnMinimizeClick(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void OnMaximizeClick(object sender, RoutedEventArgs e)
        {
            ToggleMaximize();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            try
            {
                // 同步最大化/还原图标
                if (MaxIcon != null)
                    MaxIcon.Data = WindowState == WindowState.Maximized
                        ? (Geometry)FindResource("IconRestore")
                        : (Geometry)FindResource("IconMaximize");
            }
            catch { }
            RootPadding();
        }
    }
}






