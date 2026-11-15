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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GeZi
{
    /// <summary>
    /// 「资源搜索」页的界面逻辑（2026-11-11 新增）。
    ///
    /// <para>【本页当前做到哪一步】界面 + 交互 + 与下载流程的衔接已全部打通，
    /// 但**数据是本地演示数据**（见 <see cref="ResourcesMock"/> 顶部说明）。
    /// 真搜源是独立议题，需要后端或逐个适配公开源，不在这一版范围内。</para>
    ///
    /// <para>【接真源时要改哪里】只有一处：把 <see cref="RunSearch"/> 里的
    /// <c>ResourcesMock.Search</c> 换成真实搜索调用（异步）。其余代码不用动 ——
    /// 界面只认 <see cref="ResourceItem"/>。</para>
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>资源结果的绑定源。</summary>
        private readonly ObservableCollection<ResourceItem> _resItems =
            new ObservableCollection<ResourceItem>();

        /// <summary>「解析」按钮旁边那行是否已挂过事件（只挂一次）。</summary>
        private bool _resHooked;

        /// <summary>
        /// 首次进入该页时做一次初始化（绑定数据源 + 给个默认演示结果）。
        /// 由 <see cref="OnTabsSelectionChanged"/> 在切到本页时调用。
        /// </summary>
        private void EnsureResourcesPageReady()
        {
            if (_resHooked) return;
            _resHooked = true;

            try
            {
                if (ResList != null)
                    ResList.ItemsSource = _resItems;
            }
            catch { }

            // 默认就把演示结果铺出来：让用户一进页面就知道这块能干什么，
            // 而不是对着一张空白页猜"要输入什么"。
            try
            {
                ResEmptyHint.Text = "当前为演示数据，仅展示界面效果";
                RunSearch();
            }
            catch (Exception ex)
            {
                Log("[资源] 初始化失败: " + ex.Message);
            }
        }

        // ==================== 搜索 ====================

        private void OnResSearchClick(object sender, RoutedEventArgs e)
        {
            RunSearch();
        }

        private void OnResSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                RunSearch();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                OnResSearchClear(null, null);
            }
        }

        private void OnResSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                if (ResSearchClearBtn == null || ResSearchBox == null) return;
                bool has = !string.IsNullOrEmpty(ResSearchBox.Text);
                ResSearchClearBtn.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        private void OnResSearchClear(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ResSearchBox != null) ResSearchBox.Text = "";
                if (ResSearchClearBtn != null) ResSearchClearBtn.Visibility = Visibility.Collapsed;
                RunSearch();
                if (ResSearchBox != null) ResSearchBox.Focus();
            }
            catch { }
        }

        private void OnResPlatformChanged(object sender, RoutedEventArgs e)
        {
            // Tag 里存平台 id（all / quark / uc）
            RunSearch();
        }

        /// <summary>当前选中的平台 id。</summary>
        private string SelectedResPlatform()
        {
            try
            {
                if (ResPlatUc != null && ResPlatUc.IsChecked == true) return "uc";
                if (ResPlatQuark != null && ResPlatQuark.IsChecked == true) return "quark";
            }
            catch { }
            return "all";
        }

        /// <summary>
        /// 执行一次搜索并把结果铺到列表。
        ///
        /// <para>【接真源】把 <c>ResourcesMock.Search</c> 换成真实调用即可。
        /// 真实搜索是异步的，届时改成 async 方法 + 转圈态，其余不变。</para>
        /// </summary>
        private void RunSearch()
        {
            try
            {
                string kw = ResSearchBox != null ? (ResSearchBox.Text ?? "").Trim() : "";
                string plat = SelectedResPlatform();

                var list = ResourcesMock.Search(kw, plat);

                _resItems.Clear();
                foreach (var it in list) _resItems.Add(it);

                // 结果条
                if (ResResultInfo != null)
                {
                    if (list.Count == 0)
                        ResResultInfo.Text = "没有找到相关资源";
                    else
                    {
                        string platName = plat == "all" ? "全部平台" : (plat == "uc" ? "UC" : "夸克");
                        ResResultInfo.Text = "共找到 " + list.Count + " 个结果 · " + platName
                            + (kw.Length > 0 ? " · 关键词「" + kw + "」" : "");
                    }
                }

                // 空态与结果列表互斥
                bool empty = list.Count == 0;
                if (ResEmpty != null)
                    ResEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
                if (ResScroll != null)
                    ResScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
                if (ResDetectBtn != null)
                    ResDetectBtn.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

                Log("[资源] 搜索「" + kw + "」平台=" + plat + " → " + list.Count + " 条");
            }
            catch (Exception ex)
            {
                Log("[资源] 搜索失败: " + ex.Message);
            }
        }

        /// <summary>「重新检测」：本期只是把状态重新随机一遍，用来演示 UI 反馈。</summary>
        private void OnResDetectLinks(object sender, RoutedEventArgs e)
        {
            try
            {
                // 【接真源】这里应逐条探测 shareUrl 是否可解析（取 stoken / 直链试探）。
                var rnd = new Random();
                foreach (var it in _resItems)
                {
                    int r = rnd.Next(100);
                    it.Health = r < 70 ? LinkHealth.Ok : (r < 90 ? LinkHealth.Unknown : LinkHealth.Bad);
                }
                // 强制刷新绑定（Health 相关是计算属性，改完要重建项）
                var snapshot = new List<ResourceItem>(_resItems);
                _resItems.Clear();
                foreach (var it in snapshot) _resItems.Add(it);

                // 按"有效 → 无法检测 → 失效"排序，与文析助手一致
                var sorted = new List<ResourceItem>(_resItems);
                sorted.Sort((a, b) =>
                {
                    int ra = a.Health == LinkHealth.Ok ? 0 : (a.Health == LinkHealth.Unknown ? 1 : 2);
                    int rb = b.Health == LinkHealth.Ok ? 0 : (b.Health == LinkHealth.Unknown ? 1 : 2);
                    return ra.CompareTo(rb);
                });
                _resItems.Clear();
                foreach (var it in sorted) _resItems.Add(it);

                Log("[资源] 已重新检测 " + sorted.Count + " 条链接");
            }
            catch (Exception ex)
            {
                Log("[资源] 检测失败: " + ex.Message);
            }
        }

        // ==================== 行交互 ====================

        /// <summary>悬停该行时淡入右侧操作按钮（与解析历史的星标同一做法）。</summary>
        private void OnResRowEnter(object sender, MouseEventArgs e)
        {
            FadeResRowActions(sender as DependencyObject, 1.0);
        }

        private void OnResRowLeave(object sender, MouseEventArgs e)
        {
            FadeResRowActions(sender as DependencyObject, 0.0);
        }

        private static void FadeResRowActions(DependencyObject row, double to)
        {
            try
            {
                var grid = row as Grid;
                if (grid == null) return;
                // 操作区是该行 Grid 的第 3 列（index 2）里的 StackPanel，名字固定
                StackPanel actions = null;
                foreach (var child in grid.Children)
                {
                    var sp = child as StackPanel;
                    if (sp != null && ReferenceEquals(sp.Name, "ResRowActions"))
                    {
                        actions = sp;
                        break;
                    }
                    // Name 在同一模板里会重名，改用 Tag 不行 —— 这里退化为"最后一个 StackPanel"
                    if (sp != null) actions = sp;
                }
                if (actions == null) return;
                var anim = new DoubleAnimation
                {
                    To = to,
                    Duration = TimeSpan.FromMilliseconds(120),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                };
                actions.BeginAnimation(UIElement.OpacityProperty, anim);
            }
            catch { }
        }

        /// <summary>点整行 = 解析该资源（等同右侧「解析」按钮）。</summary>
        private void OnResRowClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                // 点在按钮上时不重复触发（按钮自己会处理）
                var src = e.OriginalSource as DependencyObject;
                if (FindAncestor<Button>(src) != null) return;

                var fe = sender as FrameworkElement;
                var item = fe != null ? fe.DataContext as ResourceItem : null;
                if (item != null) OpenResource(item);
            }
            catch { }
        }

        /// <summary>右侧「解析」按钮。</summary>
        private void OnResOpenShare(object sender, RoutedEventArgs e)
        {
            try
            {
                var fe = sender as FrameworkElement;
                var item = fe != null ? fe.DataContext as ResourceItem : null;
                if (item != null) OpenResource(item);
            }
            catch { }
        }

        /// <summary>右侧「复制链接」按钮。</summary>
        private void OnResCopyLink(object sender, RoutedEventArgs e)
        {
            try
            {
                var fe = sender as FrameworkElement;
                var item = fe != null ? fe.DataContext as ResourceItem : null;
                if (item == null || string.IsNullOrEmpty(item.ShareUrl)) return;

                Clipboard.SetText(item.ShareUrl);
                Log("[资源] 已复制链接：" + item.ShareUrl);
            }
            catch (Exception ex)
            {
                Log("[资源] 复制失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 把资源送到「下载」页并解析 —— 这是本页与主流程的衔接点。
        ///
        /// <para>【为什么不是直接下载】资源是"分享链接"，要先解析出目录、
        /// 让用户勾选文件、选目录，才能下。直接开始下载等于替用户做了没授权过的决定。
        /// 所以这里只做"填好链接 + 切页 + 触发解析"，剩下的交给已有的下载流程。</para>
        /// </summary>
        private void OpenResource(ResourceItem item)
        {
            try
            {
                if (item == null || string.IsNullOrEmpty(item.ShareUrl))
                {
                    Log("[资源] 这条没有可用链接");
                    return;
                }
                if (item.Health == LinkHealth.Bad)
                {
                    AppDialog.Show(this, "链接已失效",
                        "这条资源的分享链接检测为**已失效**（可能被取消或文件已删）。\n"
                        + "建议换一条结果试试。",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 保证下载页处于"分享链接"模式，否则填进去也看不见
                try
                {
                    if (ShareModeRadio != null) ShareModeRadio.IsChecked = true;
                }
                catch { }

                // 切到下载页（索引 0）
                try { Tabs.SelectedIndex = TabDownload; } catch { }

                // 填链接并触发解析（复用已有入口，不重复实现平台判断）
                try
                {
                    if (LinkBox != null) LinkBox.Text = item.ShareUrl;
                    if (!string.IsNullOrEmpty(item.Passcode) && PassBox != null)
                        PassBox.Text = item.Passcode;
                }
                catch { }

                Log("[资源] 打开资源：" + item.Title + " → " + item.ShareUrl);
                try { OnParseClick(this, null); } catch { }
            }
            catch (Exception ex)
            {
                Log("[资源] 打开失败: " + ex.Message);
            }
        }
    }
}
