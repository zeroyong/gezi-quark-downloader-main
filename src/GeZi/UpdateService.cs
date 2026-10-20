// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
// This program is free software; you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation; either version 3 of the License, or
// (at your option) any later version.
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Reflection;
using System.Threading.Tasks;
using GeZi.Core.Update;

namespace GeZi
{
    /// <summary>
    /// 「检查更新」的 UI 层编排：把 Core 的 <see cref="GitHubReleaseChecker"/> 接进界面。
    ///
    /// 启动时只做后台检查：有新版本就点亮红点 + 弹一次气泡，不打断用户；
    /// 真正的更新详情窗只在用户主动点「检查更新」之后才出现。
    /// 启动检查吞掉所有异常并静默返回（断网/接口改版不该弹窗）；
    /// 手动检查失败则如实告诉用户，并给出 Releases 手动地址。
    /// </summary>
    internal static class UpdateService
    {
        /// <summary>两次检查之间的最小间隔（分钟），只为挡住反复重启调试。</summary>
        public const double MinCheckIntervalMinutes = 10.0;

        public const string ReleasesPageUrl = GitHubReleaseChecker.ReleasesPageUrl;

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

        /// <summary>最近一次实际查询得到的发布信息（重启后为 null）。</summary>
        public static GitHubRelease Latest { get; private set; }

        /// <summary>当前是否有（未被忽略的）新版本。红点与气泡都看它。</summary>
        public static bool HasUpdate { get; private set; }

        /// <summary>由主窗口在启动时注入的配置对象。</summary>
        public static AppSettings Settings { get; private set; }

        /// <summary>主窗口启动时调用：注入配置并立即恢复红点状态。</summary>
        public static void Attach(AppSettings s)
        {
            Settings = s;
            Load(s);
        }

        /// <summary>距上次检查是否已超过最小间隔。lastTicks = 0 表示从没查过。</summary>
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

        /// <summary>算不算有更新：解析得出版本 &gt; 当前版本，且没被忽略。</summary>
        public static bool ComputeHasUpdate(string latestText, string ignoredText, UpdateVersion current)
        {
            UpdateVersion v;
            if (!UpdateVersion.TryParse(latestText, out v) || v == null) return false;
            if (current == null) return false;
            if (v.CompareTo(current) <= 0) return false;

            UpdateVersion ignored;
            if (UpdateVersion.TryParse(ignoredText, out ignored) && ignored != null && ignored.Equals(v))
                return false;

            return true;
        }

        /// <summary>启动时用上次落盘的版本号恢复红点状态（本批节流没查也不熄灭）。</summary>
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

        public static bool IsIgnored(UpdateVersion v)
        {
            if (v == null || Settings == null) return false;
            UpdateVersion ignored;
            if (!UpdateVersion.TryParse(Settings.UpdateIgnoredVersion, out ignored) || ignored == null)
                return false;
            return ignored.Equals(v);
        }

        /// <summary>取消忽略后必须重新算 HasUpdate，否则红点不会自己亮回来。</summary>
        public static void ClearIgnore(AppSettings s)
        {
            if (s == null) return;
            s.UpdateIgnoredVersion = "";
            HasUpdate = ComputeHasUpdate(s.UpdateLatestVersion, s.UpdateIgnoredVersion, CurrentVersion);
        }

        /// <summary>启动检查。失败一律静默。</summary>
        /// <param name="onNewUpdate">确认有新版本时回调（UI 线程），用来弹气泡。</param>
        public static async Task RunStartupCheckAsync(AppSettings s, Action<GitHubRelease> onNewUpdate)
        {
            if (s == null) return;
            if (!ShouldCheck(s.UpdateLastCheckTicks, DateTime.Now)) return;

            GitHubRelease found = null;
            try
            {
                // ⚠️ 不要 ConfigureAwait(false)：启动检查从 UI 线程发起，
                //    回来还得在 UI 线程写配置/弹气泡，否则 NotifyToast 会跨线程抛异常。
                var checker = new GitHubReleaseChecker();
                found = await checker.FindLatestAsync();
            }
            catch
            {
                return;   // 断网 / 接口改版 —— 启动检查必须静默
            }

            Apply(s, found, onNewUpdate);
        }

        /// <summary>用户手动点「检查更新」：不节流，取不到就如实抛错。</summary>
        public static async Task<GitHubRelease> CheckNowAsync()
        {
            var checker = new GitHubReleaseChecker();
            var found = await checker.FindLatestAsync();
            if (found == null)
                throw new InvalidOperationException(
                    "无法读取 GitHub Releases（网络不通或仓库地址有变）。");
            return found;
        }

        /// <summary>把一次检查结果落到设置与内存状态里。</summary>
        public static void Apply(AppSettings s, GitHubRelease found, Action<GitHubRelease> onNewUpdate)
        {
            if (s == null) return;

            s.UpdateLastCheckTicks = DateTime.Now.Ticks;
            Latest = (found != null && found.IsValid) ? found : null;
            s.UpdateLatestVersion = Latest != null ? Latest.Version.ToString() : "";
            HasUpdate = ComputeHasUpdate(s.UpdateLatestVersion, s.UpdateIgnoredVersion, CurrentVersion);

            if (HasUpdate && onNewUpdate != null)
                onNewUpdate(Latest);
        }
    }
}
