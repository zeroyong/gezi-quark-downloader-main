// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Update;

namespace GeZi
{
    /// <summary>
    /// 「检查更新」的 UI 层编排。
    /// <para>
    /// 【2026-10-22 双源】原来是"只查 GitHub Releases"。GitHub 在国内网络下并不总通，
    /// 启动那次检查一失败就等于永远看不到更新提示。现在把用户自己的夸克分享
    /// （pan.quark.cn/s/065af2172c40，提取码 VD8m，游客可读）作为<b>第二个源</b>接进来：
    /// 两个源各查一次，取<b>版本号更高</b>的那个当"最新版本"。
    /// </para>
    /// <para>
    /// 需求明确要求<b>不打断用户</b>：启动时只做后台检查，有新版本就点亮红点 + 弹一次气泡，
    /// 用户不点就当没发生过。真正的窗口只在用户<b>主动点</b>「检查更新」之后才出现。
    /// </para>
    /// ⚠️ <b>线程约定</b>：除 <see cref="CheckNowAsync"/> 与 <see cref="InstallAsync"/> 外，
    /// 会改写 <c>AppSettings</c> 的方法<b>只能在 UI 线程调用</b>
    /// （与设置页的 Save 同一条线程，避免并发写盘）。
    /// </summary>
    internal static class UpdateService
    {
        /// <summary>
        /// 两次检查之间的最小间隔（分钟）。
        /// 需求要求"每次启动都检查"，所以间隔取得很小 —— 只为挡住"反复重启调试"
        /// 那种几分钟内连开十几次的情况，正常一天开几次都会真的去查。
        /// </summary>
        public const double MinCheckIntervalMinutes = 10.0;

        /// <summary>GitHub Releases 列表页（查询失败时给用户的手动入口）。</summary>
        public const string ReleasesPageUrl = GitHubReleaseChecker.ReleasesPageUrl;

        /// <summary>夸克分享的更新包地址（用户 2026-10-22 指定的固定分享）。</summary>
        public const string QuarkShareUrl = QuarkReleaseChecker.SharePageUrl;

        static UpdateService()
        {
            try
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                CurrentVersion = UpdateVersion.FromVersion(v);
            }
            catch
            {
                CurrentVersion = null;
            }
        }

        /// <summary>当前程序版本（从程序集读，不再手写一份）。</summary>
        public static UpdateVersion CurrentVersion { get; private set; }

        /// <summary>来源标签数组，UI 用来显示"这次结果是哪个源给的"。</summary>
        private static readonly string[] SourceLabels =
        {
            ReleaseSourceNames.GitHub, ReleaseSourceNames.Quark,
        };

        /// <summary>
        /// 最近一次**实际查询**得到的发布信息（下载页地址、更新说明都要用）。
        /// ⚠️ 重启后为 null —— 落盘的只有版本号（字符串），要拿详情必须重新查一次。
        ///    所以「检查更新」按钮总是先查再动作，靠这里不会出现"有红点但打不开"的情况。
        /// </summary>
        public static IReleaseInfo Latest { get; private set; }

        /// <summary>当前是否有（未被忽略的）新版本。红点与气泡都看它。</summary>
        public static bool HasUpdate { get; private set; }

        /// <summary>最近一次检查的结果是哪个源（可能就是 "" = 没查到）。</summary>
        public static string LatestSource { get; private set; }

        /// <summary>「一键更新」的编排器（用当前进程所在的目录当覆盖目标）。</summary>
        public static UpdateInstaller Installer
        {
            get
            {
                if (_installer == null)
                    _installer = new UpdateInstaller(AppContext.BaseDirectory);
                return _installer;
            }
        }
        private static UpdateInstaller _installer;

        /// <summary>
        /// 由主窗口在启动时注入的配置对象。
        /// 「关于」窗口是临时 <c>new</c> 出来的，
        /// 让它反向去拿主窗口的字段很别扭；这里由主窗口挂一次，各处（关于页/更新流程）
        /// 直接读同一个引用即可，落盘仍统一走 <c>SettingsStore.Save</c>。
        /// </summary>
        public static AppSettings Settings { get; private set; }

        /// <summary>主窗口启动时调用：注入配置并立即恢复红点状态。</summary>
        public static void Attach(AppSettings s)
        {
            Settings = s;
            Load(s);
        }

        // ---------------- 纯逻辑（不碰网络，可单测） ----------------

        /// <summary>距上次检查是否已超过最小间隔。<paramref name="lastTicks"/> 为 0 表示从没查过。</summary>
        public static bool ShouldCheck(long lastTicks, DateTime now)
        {
            if (lastTicks <= 0) return true;
            DateTime last;
            try { last = new DateTime(lastTicks); }
            catch { return true; }

            double minutes = (now - last).TotalMinutes;
            if (minutes < 0) return true;      // 时钟被改过，别卡住
            return minutes >= MinCheckIntervalMinutes;
        }

        /// <summary>
        /// 判定"算不算有更新"。三条同时成立才算：解析得出版本 &gt; 当前版本，且没被忽略。
        /// </summary>
        public static bool ComputeHasUpdate(string latestText, string ignoredText, UpdateVersion current)
        {
            UpdateVersion v;
            if (!UpdateVersion.TryParse(latestText, out v) || v == null) return false;
            if (current == null) return false;
            if (v.CompareTo(current) <= 0) return false;

            UpdateVersion ignored;
            if (UpdateVersion.TryParse(ignoredText, out ignored) && ignored != null && ignored.Equals(v))
                return false;                  // 用户明确忽略过这个版本

            return true;
        }

        /// <summary>
        /// 从两个源的结果里挑一个当"最新版本"：版本号高的赢。
        /// 都一样高时平手 → 返回 GitHub 那个（说明更完整，UI 更值钱）。
        /// </summary>
        public static IReleaseInfo PickNewer(IReleaseInfo a, IReleaseInfo b)
        {
            if (a == null || !a.IsValid) return (b != null && b.IsValid) ? b : null;
            if (b == null || !b.IsValid) return a;
            return b.Version.CompareTo(a.Version) > 0 ? b : a;
        }

        /// <summary>
        /// 启动时调用：用**上次落盘的版本号**恢复红点状态。
        /// 这样即使本次因节流没去查，红点也能保持（否则用户重启一次红点就没了）。
        /// </summary>
        public static void Load(AppSettings s)
        {
            if (s == null) return;
            HasUpdate = ComputeHasUpdate(s.UpdateLatestVersion, s.UpdateIgnoredVersion, CurrentVersion);
        }

        /// <summary>用户点了「忽略此版本」。红点立即熄灭，且不再弹气泡。</summary>
        public static void IgnoreCurrent(AppSettings s)
        {
            if (s == null) return;
            s.UpdateIgnoredVersion = s.UpdateLatestVersion ?? "";
            HasUpdate = false;
        }

        /// <summary>这个版本是不是被用户忽略过。</summary>
        public static bool IsIgnored(UpdateVersion v)
        {
            if (v == null || Settings == null) return false;
            UpdateVersion ignored;
            if (!UpdateVersion.TryParse(Settings.UpdateIgnoredVersion, out ignored) || ignored == null)
                return false;
            return ignored.Equals(v);
        }

        /// <summary>
        /// 取消忽略（用户又想看到提醒了）。
        /// ⚠️ 必须**重新算** HasUpdate —— 只把字符串清空的话红点不会自己亮回来。
        /// </summary>
        public static void ClearIgnore(AppSettings s)
        {
            if (s == null) return;
            s.UpdateIgnoredVersion = "";
            HasUpdate = ComputeHasUpdate(s.UpdateLatestVersion, s.UpdateIgnoredVersion, CurrentVersion);
        }

        // ---------------- 检查 ----------------

        /// <summary>
        /// 启动检查。失败一律静默。
        /// </summary>
        /// <param name="onNewUpdate">确认有新版本时回调（UI 线程），用来弹气泡。</param>
        public static async Task RunStartupCheckAsync(AppSettings s, Action<IReleaseInfo> onNewUpdate)
        {
            if (s == null) return;
            if (!ShouldCheck(s.UpdateLastCheckTicks, DateTime.Now)) return;

            IReleaseInfo found = null;
            try
            {
                found = await QueryBothAsync().ConfigureAwait(true);
            }
            catch
            {
                // 断网 / 接口改版 —— 启动检查失败必须静默，不打扰用户
                return;
            }

            Apply(s, found, onNewUpdate);
        }

        /// <summary>
        /// 用户手动点「检查更新」：不做节流，失败也如实抛出（他是真的想知道结果）。
        /// <para>
        /// 这里是<b>双源</b>的：GitHub 与夸克分享各查一次，取版本号更高的那个。
        /// GitHub 查不通时自动退到夸克；两个都查不通才抛异常（那时用户确实需要知道）。
        /// </para>
        /// </summary>
        public static async Task<IReleaseInfo> CheckNowAsync()
        {
            IReleaseInfo winner = await QueryBothAsync().ConfigureAwait(false);
            if (winner == null || !winner.IsValid)
                throw new InvalidOperationException(
                    "两个更新源都没连上（GitHub " + ReleasesPageUrl + " / 夸克分享 " + QuarkShareUrl + "）。");
            return winner;
        }

        /// <summary>
        /// 两个源各查一次，取版本号更高的那个。任一个源抛异常只按"该源没有"处理
        /// —— 一个源不通不该让另一次查询白跑。
        /// </summary>
        public static async Task<IReleaseInfo> QueryBothAsync()
        {
            var ghTask = QueryGitHubAsync();
            var qkTask = QueryQuarkAsync();
            await Task.WhenAny(Task.WhenAll(ghTask, qkTask)).ConfigureAwait(false);

            IReleaseInfo gh = null;
            try { gh = ghTask.Result; } catch { gh = null; }
            IReleaseInfo qk = null;
            try { qk = qkTask.Result; } catch { qk = null; }

            return PickNewer(gh, qk);
        }

        private static async Task<IReleaseInfo> QueryGitHubAsync()
        {
            try
            {
                var checker = new GitHubReleaseChecker();
                return await checker.FindLatestAsync().ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<IReleaseInfo> QueryQuarkAsync()
        {
            try
            {
                var checker = new QuarkReleaseChecker();
                return await checker.FindLatestAsync().ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 「一键更新」：下载安装包 → 解压 → 外部脚本接力覆盖 → 重启。
        /// 返回 true 表示脚本已启动，调用方应立刻 <c>Environment.Exit(0)</c>。
        /// </summary>
        public static async Task<bool> InstallAsync(IProgress<UpdateProgress> progress = null,
            CancellationToken ct = default(CancellationToken))
        {
            var rel = Latest as QuarkRelease;
            string url;
            if (rel != null && rel.IsValid)
            {
                // 夸克源：直链有时效，必须在用户确认的那一刻现场取
                url = await new QuarkReleaseChecker().ResolveDownloadUrlAsync(rel, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                url = Latest != null ? Latest.PackageUrl : null;
            }

            if (string.IsNullOrEmpty(url))
                return false;

            return await Installer.InstallAsync(url,
                rel != null ? rel.FileName : "",
                Latest.Version, progress, ct).ConfigureAwait(false);
        }

        /// <summary>把一次检查结果落到设置与内存状态里。</summary>
        public static void Apply(AppSettings s, IReleaseInfo found, Action<IReleaseInfo> onNewUpdate)
        {
            if (s == null) return;

            s.UpdateLastCheckTicks = DateTime.Now.Ticks;
            Latest = (found != null && found.IsValid) ? found : null;
            LatestSource = Latest != null ? Latest.SourceName : "";
            s.UpdateLatestVersion = Latest != null ? Latest.Version.ToString() : "";
            HasUpdate = ComputeHasUpdate(s.UpdateLatestVersion, s.UpdateIgnoredVersion, CurrentVersion);

            if (HasUpdate && onNewUpdate != null)
                onNewUpdate(Latest);
        }

        /// <summary>把 "GitHub Releases" 这样的内部源名换成界面上说的话。</summary>
        public static string SourceLabel(string source)
        {
            switch (source ?? "")
            {
                case ReleaseSourceNames.GitHub: return "GitHub Releases";
                case ReleaseSourceNames.Quark: return "夸克网盘分享";
                default: return "";
            }
        }
    }
}
