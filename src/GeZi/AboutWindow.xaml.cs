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
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GeZi.Core.Update;

namespace GeZi
{
    /// <summary>
    /// 「关于」窗口：软件介绍 + 赞赏码（可点击放大）。
    ///
    /// 【来由】用户要求「和 Python Flet 版那个差不多」——Flet 版有个 `_show_about()`
    /// 带品牌行、B 站链接、应用简介、功能特性、赞赏支持。这里按当前 C# 版的实际情况
    /// **重写了介绍内容**（Flet 版那套功能描述已经对不上了）。
    ///
    /// ⚠️ 赞赏码是用户 2026-10-09 换成的新图（「躬行小舟」的赞赏码），
    /// 资源名 `Resources/reward-xiaozhou.png`，放大弹窗用的是同一张。
    /// </summary>
    public partial class AboutWindow : Window
    {
        /// <summary>作者 B 站主页（用户 2026-10-09 给的新短链）。</summary>
        private const string BiliUrl = "https://b23.tv/txZtz98";

        /// <summary>
        /// 项目开源地址（GitHub）。
        /// 【为什么放显眼处】这类工具的用户最怕"来路不明的 exe"（见过太多捆绑木马的），
        /// 源码公开是他们判断可信度的第一依据 —— 比任何"本软件无毒"的声明都有用。
        /// 【2026-10-21 修正】原来指向上游原作者仓库，但本项目的后续发布在
        /// <c>zeroyong/gezi-quark-downloader-main</c>；「检查更新」也查这个仓库，
        /// 两处必须一致，否则用户点开源地址看到的版本和检查更新说的对不上。
        /// </summary>
        private const string RepoUrl = "https://github.com/zeroyong/gezi-quark-downloader-main";

        /// <summary>功能特性（面向普通用户，一条一句话）。</summary>
        /// <remarks>
        /// 【2026-10-03】用户提醒「怎么没在简介里写免转存功能，这不是很大的一个特点吗」——
        /// 确实漏了。补上后顺带把「多账号」也加了（也是别人常问的）。
        /// 写法要求：**说清"对你有什么好处"**，别只写功能名。
        /// </remarks>
        private static readonly string[] Features =
        {
            "解析分享链接：链接、提取码、整段分享文案都能直接粘",
            "免转存下载：不往自己网盘写中转文件，省网盘空间、不留垃圾",
            "扫码登录：浏览「我的网盘」，支持转存与删除",
            "高速下载：单文件最多 512 个连接，支持断点续传",
            "两种写入方式：共写同一文件（省空间）/ 独立分片（可边下边看）",
            "导出直链：纯直链、curl、aria2c 三种格式",
            "多账号：可保存多个账号，随时一键切换",
            "托盘常驻 + 悬浮小窗：窗口关掉也在后台继续下",
        };

        public AboutWindow()
        {
            InitializeComponent();

            // 版本号从程序集读，别再手写一份（写死了迟早对不上）
            try
            {
                var ver = Assembly.GetExecutingAssembly().GetName().Version;
                VersionText.Text = "夸克网盘 · 直链助手 · v" + (ver != null ? ver.ToString(3) : "2.2.0");
            }
            catch { }

            BuildFeatureList();

            Loaded += (s, e) =>
            {
                DialogChrome.ApplyRoundedCorners(this);
                // 启动检查是异步的，可能刚好在构造之后才完成；打开时再刷一次红点
                RefreshUpdateDot();
            };
        }

        // ---------------- 检查更新（GitHub Releases） ----------------

        private bool _checkingUpdate;

        /// <summary>按 UpdateService.HasUpdate 决定「检查更新」旁边的红点。</summary>
        private void RefreshUpdateDot()
        {
            try
            {
                UpdateDot.Visibility = UpdateService.HasUpdate
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        /// <summary>
        /// 用户主动点「检查更新」：不节流，结果明确告诉用户（失败也给原因和手动地址）。
        /// 与启动时的静默检查是两回事。
        /// </summary>
        private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
        {
            if (_checkingUpdate) return;
            _checkingUpdate = true;
            CheckUpdateBtn.IsEnabled = false;
            try
            {
                var m = await UpdateService.CheckNowAsync();

                if (UpdateService.Settings != null)
                    UpdateService.Apply(UpdateService.Settings, m, null);
                RefreshUpdateDot();

                // 有新版本、但之前被忽略过 → 先问要不要恢复提醒
                if (m != null && UpdateService.CurrentVersion != null
                    && m.Version.CompareTo(UpdateService.CurrentVersion) > 0
                    && UpdateService.IsIgnored(m.Version))
                {
                    var resume = AppDialog.Show(this,
                        "你之前忽略了 v" + m.Version + " 的更新提醒。\n\n是否恢复提醒并查看更新？",
                        "检查更新", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (resume != MessageBoxResult.Yes) return;

                    if (UpdateService.Settings != null)
                    {
                        UpdateService.ClearIgnore(UpdateService.Settings);
                        SettingsStore.Save(UpdateService.Settings);
                    }
                    RefreshUpdateDot();
                }

                if (UpdateService.HasUpdate && m != null)
                {
                    OpenUpdatePrompt(m);
                    RefreshUpdateDot();
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
                AppDialog.Show(this,
                    "检查更新失败：" + ex.Message
                    + "\n\n你也可以手动打开 Releases 页查看：\n" + UpdateService.ReleasesPageUrl,
                    "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _checkingUpdate = false;
                CheckUpdateBtn.IsEnabled = true;
            }
        }

        /// <summary>打开「更新详情」窗；用户在窗内明确点「忽略此版本」才落盘。</summary>
        private void OpenUpdatePrompt(GitHubRelease m)
        {
            if (m == null) return;

            var prompt = new UpdatePromptWindow(m) { Owner = this };
            prompt.ShowDialog();

            if (prompt.IgnoredByUser && UpdateService.Settings != null)
            {
                UpdateService.IgnoreCurrent(UpdateService.Settings);
                SettingsStore.Save(UpdateService.Settings);
            }
        }

        private void BuildFeatureList()
        {
            var bullet = (Brush)FindResource("PrimaryBrush");
            var text = (Brush)FindResource("TextPrimaryBrush");
            foreach (var f in Features)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var dot = new TextBlock
                {
                    Text = "·",
                    FontSize = 13,
                    Width = 14,
                    Foreground = bullet,
                    VerticalAlignment = VerticalAlignment.Top,
                };
                Grid.SetColumn(dot, 0);
                row.Children.Add(dot);

                var t = new TextBlock
                {
                    Text = f,
                    FontSize = 11.5,
                    LineHeight = 18,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = text,
                };
                Grid.SetColumn(t, 1);
                row.Children.Add(t);

                FeatureList.Children.Add(row);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            try { Close(); } catch { }
        }

        private void OnBiliClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!ShellLaunch.OpenUrl(BiliUrl))
                    AppDialog.Show(this, "打开浏览器失败，请手动访问：\n\n" + BiliUrl, "打开链接");
            }
            catch { }
        }

        /// <summary>点「开源地址」→ 用默认浏览器打开 GitHub 仓库。</summary>
        private void OnRepoClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!ShellLaunch.OpenUrl(RepoUrl))
                    AppDialog.Show(this, "打开浏览器失败，请手动访问：\n\n" + RepoUrl, "打开链接");
            }
            catch { }
        }

        /// <summary>点赞赏码 → 弹一个放大的窗口（和 Flet 版的「赞赏支持」弹窗同一个意思）。</summary>
        private void OnQrClick(object sender, RoutedEventArgs e)
        {
            try { ShowReward(); } catch { }
        }

        private void ShowReward()
        {
            var win = new Window
            {
                Owner = this,
                Title = "赞赏支持",
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (Brush)FindResource("SurfaceBrush"),
                FontFamily = (FontFamily)FindResource("AppFontFamily"),
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
            };
            TextOptions.SetTextFormattingMode(win, TextFormattingMode.Display);

            // 跟小窗/关于窗一样：WindowChrome（非分层窗口，文字才清晰）+ DWM 圆角
            System.Windows.Shell.WindowChrome.SetWindowChrome(win,
                new System.Windows.Shell.WindowChrome
                {
                    CaptionHeight = 40,
                    ResizeBorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(0),
                    GlassFrameThickness = new Thickness(0),
                    UseAeroCaptionButtons = false,
                });

            var img = new Image
            {
                Source = new BitmapImage(new Uri("pack://application:,,,/Resources/reward-xiaozhou.png")),
                Width = 340,
                Height = 340,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var close = new Button
            {
                Content = "关闭",
                MinWidth = 96,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 14, 0, 0),
                Style = FindResource("PrimaryButton") as Style,
            };
            close.Click += (s2, e2) => { try { win.Close(); } catch { } };

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                Text = "躬行小舟的赞赏码",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
            });
            panel.Children.Add(new TextBlock
            {
                Text = "用微信 / 支付宝扫码即可赞赏支持",
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)FindResource("TextTertiaryBrush"),
            });
            panel.Children.Add(img);
            panel.Children.Add(close);

            win.Content = new Border
            {
                BorderBrush = (Brush)FindResource("BorderBrushSoft"),
                BorderThickness = new Thickness(1),
                Child = panel,
            };

            win.Loaded += (s2, e2) => DialogChrome.ApplyRoundedCorners(win);
            win.ShowDialog();
        }
    }
}
