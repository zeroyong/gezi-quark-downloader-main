// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
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
using System.Windows;
using System.Windows.Controls;
using GeZi.Core.Update;

namespace GeZi
{
    /// <summary>
    /// 「更新详情」窗口（2026-10-22 双源版）。
    /// 展示发现的新版本（版本号 / 来源 / 发布信息），并提供两种动作：
    /// <list type="bullet">
    ///   <item><b>一键更新</b>（主按钮）：程序自动下载安装包 → 解压 → 外部脚本覆盖 → 重启。
    ///     仅当来源支持自动安装（GitHub 直链 / 夸克分享直链）时可用。</item>
    ///   <item><b>查看详情</b>：在浏览器打开来源页（GitHub Release / 夸克分享），
    ///     手动下载安装包。任何来源都可用，作为兜底。</item>
    /// </list>
    /// 本窗口只负责 UI 与"调起"安装；真正的网络下载/覆盖在
    /// <see cref="UpdateService.InstallAsync"/> 里完成。
    /// </summary>
    public partial class UpdatePromptWindow : Window
    {
        private readonly IReleaseInfo _release;

        /// <summary>用户在详情窗里点了「忽略此版本」。由调用方负责落盘。</summary>
        public bool IgnoredByUser { get; private set; }

        public UpdatePromptWindow(IReleaseInfo release)
        {
            if (release == null) throw new ArgumentNullException("release");
            _release = release;

            InitializeComponent();

            NewVersionText.Text = "发现新版本 v" + _release.Version;
            CurrentVersionText.Text = "当前版本 v" + (UpdateService.CurrentVersion != null
                ? UpdateService.CurrentVersion.ToString() : "?");

            // 来源徽章：GitHub Releases / 夸克网盘分享
            SourceText.Text = UpdateService.SourceLabel(_release.SourceName);

            // 发布信息：有发布时间显示日期，否则显示包大小
            if (_release.PublishedAt.HasValue)
                SizeText.Text = _release.PublishedAt.Value.ToLocalTime().ToString("yyyy-MM-dd") + " 发布";
            else if (_release.PackageSize > 0)
                SizeText.Text = (_release.PackageSize / 1048576.0).ToString("0.0") + " MB";
            else
                SizeText.Text = "v" + _release.TagName;

            // 更新说明：优先用来源给的 Notes，没有就提示去来源页看
            BodyText.Text = string.IsNullOrWhiteSpace(_release.Notes)
                ? "（这个版本没有写更新说明，可点「查看详情」打开来源页查看。）"
                : _release.Notes.Trim();

            // 「一键更新」按钮：仅当来源能自动安装时才可用
            bool canAuto = _release.CanAutoInstall;
            UpdateBtn.IsEnabled = canAuto;
            UpdateBtn.ToolTip = canAuto
                ? "自动下载安装包并覆盖重启（无需手动操作）"
                : "此来源暂不支持自动安装，请改用「查看详情」手动下载";

            Loaded += (s, e) =>
            {
                DialogChrome.ApplyRoundedCorners(this);
            };
        }

        /// <summary>「一键更新」：后台跑安装流程，进度条实时刷新；成功则拉起外部脚本并关闭本窗。</summary>
        private async void OnUpdateClick(object sender, RoutedEventArgs e)
        {
            if (!_release.CanAutoInstall) return;

            UpdateBtn.IsEnabled = false;
            ViewBtn.IsEnabled = false;
            CloseBtn.IsEnabled = false;
            ProgressBar.Visibility = Visibility.Visible;
            StatusText.Visibility = Visibility.Visible;
            StatusText.Text = "正在准备更新…";

            try
            {
                var progress = new Progress<UpdateProgress>(p =>
                {
                    if (!Dispatcher.CheckAccess())
                    {
                        Dispatcher.BeginInvoke(new Action<UpdateProgress>(OnProgress), p);
                        return;
                    }
                    OnProgress(p);
                });

                bool started = await UpdateService.InstallAsync(progress);
                if (started)
                {
                    StatusText.Text = "更新已接管，程序即将重启…";
                    // 给 UI 一点时间把"即将重启"刷出来，再由主进程退出
                    await System.Threading.Tasks.Task.Delay(400);
                    // ⚠️ 【2026-10-22】用 Shutdown() 而不是 Environment.Exit(0)。
                    //   Environment.Exit 会**跳过 MainWindow.OnClosed** ——
                    //   托盘图标不释放、文件流不 flushing，映像文件的句柄
                    //   也可能因此晚几百毫秒才放开，外部脚本覆盖 GeZi.exe
                    //   就会撞 "Sharing violation"（用户实测：下载成功但版本没变）。
                    //   Shutdown() 走正常关闭流程（OnClosed 会跑），资源释放干净，
                    //   脚本那边覆盖成功率也高得多。
                    try { Application.Current.Shutdown(); }
                    catch { Environment.Exit(0); }
                }
                else
                {
                    ResetButtons("无法获取安装包地址，请改用「查看详情」手动下载。");
                }
            }
            catch (Exception ex)
            {
                ResetButtons("更新失败：" + ex.Message);
            }
        }

        private void OnProgress(UpdateProgress p)
        {
            switch (p.Stage)
            {
                case UpdateStage.Downloading:
                    if (p.Total > 0)
                    {
                        ProgressBar.Value = (double)p.Downloaded / p.Total * 100;
                        StatusText.Text = "正在下载安装包… "
                            + (p.Downloaded / 1048576.0).ToString("0.0") + " / "
                            + (p.Total / 1048576.0).ToString("0.0") + " MB";
                    }
                    else
                    {
                        ProgressBar.IsIndeterminate = true;
                        StatusText.Text = "正在下载安装包… "
                            + (p.Downloaded / 1048576.0).ToString("0.0") + " MB";
                    }
                    break;
                case UpdateStage.Extracting:
                    ProgressBar.IsIndeterminate = true;
                    StatusText.Text = "正在解压安装包…";
                    break;
                case UpdateStage.Preparing:
                    ProgressBar.IsIndeterminate = true;
                    StatusText.Text = "正在准备重启…";
                    break;
                case UpdateStage.Restarting:
                    StatusText.Text = "即将重启完成更新…";
                    break;
                case UpdateStage.Failed:
                    StatusText.Text = p.Message ?? "更新失败";
                    break;
            }
        }

        private void ResetButtons(string message)
        {
            ProgressBar.Visibility = Visibility.Collapsed;
            StatusText.Text = message ?? "更新失败";
            UpdateBtn.IsEnabled = _release.CanAutoInstall;
            ViewBtn.IsEnabled = true;
            CloseBtn.IsEnabled = true;
        }

        /// <summary>「查看详情」：打开来源页（GitHub Release / 夸克分享），手动下载安装包。</summary>
        private void OnViewClick(object sender, RoutedEventArgs e)
        {
            var url = _release.OpenUrl;
            if (string.IsNullOrEmpty(url))
                url = _release.SourceName == ReleaseSourceNames.Quark
                    ? UpdateService.QuarkShareUrl
                    : UpdateService.ReleasesPageUrl;
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

        /// <summary>「忽略此版本」：记下标记，由调用方落盘（之后不再提示这个版本）。</summary>
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
