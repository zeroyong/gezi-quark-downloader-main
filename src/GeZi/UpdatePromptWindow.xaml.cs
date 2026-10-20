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
using System.Windows;
using GeZi.Core.Update;

namespace GeZi
{
    /// <summary>
    /// 「更新详情」窗口：展示版本号 / 发布时间 / GitHub 更新说明。
    /// 「前往下载」把用户送到 Release 页（浏览器下载安装包）。
    /// 本窗口不碰网络，也不自行下载/覆盖安装。
    /// </summary>
    public partial class UpdatePromptWindow : Window
    {
        private readonly GitHubRelease _release;

        /// <summary>用户在详情窗里点了「忽略此版本」，由调用方负责落盘。</summary>
        public bool IgnoredByUser { get; private set; }

        public UpdatePromptWindow(GitHubRelease release)
        {
            if (release == null) throw new ArgumentNullException("release");
            _release = release;

            InitializeComponent();

            NewVersionText.Text = "发现新版本 v" + _release.Version;
            CurrentVersionText.Text = "当前版本 v" + (UpdateService.CurrentVersion != null
                ? UpdateService.CurrentVersion.ToString() : "?");

            if (_release.PublishedAt.HasValue)
                SizeText.Text = _release.PublishedAt.Value.ToLocalTime().ToString("yyyy-MM-dd") + " 发布";
            else if (_release.AssetSize > 0)
                SizeText.Text = (_release.AssetSize / 1048576.0).ToString("0.0") + " MB";
            else
                SizeText.Text = "v" + _release.TagName;

            BodyText.Text = string.IsNullOrWhiteSpace(_release.Body)
                ? "（这个版本没有写更新说明，可打开 Release 页查看。）"
                : _release.Body.Trim();

            Loaded += (s, e) => DialogChrome.ApplyRoundedCorners(this);
        }

        private void OnDownloadClick(object sender, RoutedEventArgs e)
        {
            var url = _release.HtmlUrl;
            if (string.IsNullOrEmpty(url))
                url = UpdateService.ReleasesPageUrl;
            try
            {
                if (!ShellLaunch.OpenUrl(url))
                    AppDialog.Show(this, "打开浏览器失败，请手动访问：\n\n" + url, "更新");
            }
            catch (Exception ex)
            {
                AppDialog.Show(this, "打开浏览器失败：" + ex.Message, "更新");
            }
            try { Close(); } catch { }
        }

        private void OnIgnoreClick(object sender, RoutedEventArgs e)
        {
            IgnoredByUser = true;
            try { Close(); } catch { }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            try { Close(); } catch { }
        }
    }
}
