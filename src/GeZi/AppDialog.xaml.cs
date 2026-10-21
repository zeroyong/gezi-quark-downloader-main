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
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GeZi
{
    /// <summary>
    /// 统一的自绘对话框，取代系统 <see cref="MessageBox"/>。
    ///
    /// 【为什么要做这个】
    /// 系统 MessageBox 是 Win32 经典外观：方角、左上角大块位图图标、
    /// 方形灰按钮、固定字号。本项目其余部分都是圆角卡片 + 设计令牌自绘，
    /// 两者放一起像是两个软件拼起来的（用户截图反馈"各式各样弹窗都好丑"）。
    ///
    /// 【用法】保持与原 MessageBox.Show 高度接近的静态签名，替换调用点时
    /// 只需把 `MessageBox.Show(...)` 改成 `AppDialog.Show(...)`，
    /// 返回类型仍是 <see cref="MessageBoxResult"/>，调用方的判断逻辑不用动。
    /// </summary>
    public partial class AppDialog : Window
    {
        private MessageBoxResult _result = MessageBoxResult.None;
        private MessageBoxResult _defaultResult = MessageBoxResult.OK;

        private AppDialog()
        {
            InitializeComponent();
        }

        // ============================================================
        // 静态入口：与 MessageBox.Show 的常用重载一一对应
        // ============================================================

        public static MessageBoxResult Show(Window owner, string text)
        {
            return Show(owner, text, "提示", MessageBoxButton.OK,
                        MessageBoxImage.Information, MessageBoxResult.OK);
        }

        public static MessageBoxResult Show(Window owner, string text, string title)
        {
            return Show(owner, text, title, MessageBoxButton.OK,
                        MessageBoxImage.Information, MessageBoxResult.OK);
        }

        public static MessageBoxResult Show(Window owner, string text, string title,
                                            MessageBoxButton button)
        {
            return Show(owner, text, title, button,
                        MessageBoxImage.Information, DefaultFor(button));
        }

        public static MessageBoxResult Show(Window owner, string text, string title,
                                            MessageBoxButton button, MessageBoxImage icon)
        {
            return Show(owner, text, title, button, icon, DefaultFor(button));
        }

        public static MessageBoxResult Show(Window owner, string text, string title,
                                            MessageBoxButton button, MessageBoxImage icon,
                                            MessageBoxResult defaultResult)
        {
            var dlg = new AppDialog();
            if (owner != null && owner.IsVisible)
            {
                dlg.Owner = owner;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            dlg.Configure(text, title, button, icon, defaultResult);
            dlg.ShowDialog();
            return dlg._result;
        }

        // ============================================================
        // 带「不再提示」勾选框的入口（2026-10-03 新增）
        // ============================================================

        /// <summary>
        /// 显示一个对话框，并在右下角带一个「此后不再提示」勾选框。
        ///
        /// 【为什么要它】「关闭按钮该最小化到托盘还是直接退出」这类问题只该问一次，
        /// 之后由用户的选择记住 —— 硬弹一个每次都要问的框很烦，静默替用户决定
        /// 又容易让人以为"程序关了其实还在跑"。
        ///
        /// 按钮两个，按本对话框既有的「主操作在左」习惯排列：
        /// 左边 <paramref name="primaryText"/>（主按钮、回车默认，返回 <see cref="MessageBoxResult.OK"/>）、
        /// 右边 <paramref name="secondaryText"/>（次按钮，返回 <see cref="MessageBoxResult.Cancel"/>）。
        /// </summary>
        /// <param name="dontAskAgain">输出：用户是否勾选了「此后不再提示」</param>
        /// <returns>主按钮 = OK，次按钮 = Cancel，右上角 X / Esc = Cancel</returns>
        public static MessageBoxResult ShowWithDontAsk(Window owner, string text, string title,
                                                       string primaryText, string secondaryText,
                                                       MessageBoxImage icon, out bool dontAskAgain)
        {
            var dlg = new AppDialog();
            if (owner != null && owner.IsVisible)
            {
                dlg.Owner = owner;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            dlg.Configure(text, title, MessageBoxButton.OKCancel, icon, MessageBoxResult.OK);

            // ⚠️ 必须认准 AppDialog 内部 OKCancel 的槽位映射（见 SetupButtons）：
            //      Btn2 = 确定(OK)，显示在**左**；Btn1 = 取消(Cancel)，显示在**右**；
            //      Btn3 隐藏。之前想当然写成 Btn1=主操作，结果**返回值与文案对调**：
            //      点「最小化到托盘」拿到的是 Cancel → 被当成"直接退出"，一点就退进程。
            dlg.Btn2.Content = primaryText;
            dlg.Btn1.Content = secondaryText;
            dlg.Btn2.MinWidth = 108;
            dlg.Btn1.MinWidth = 108;
            dlg.DontAskCheck.Visibility = Visibility.Visible;

            dlg.ShowDialog();
            dontAskAgain = dlg.DontAskCheck.IsChecked == true;
            return dlg._result;
        }

        private static MessageBoxResult DefaultFor(MessageBoxButton button)
        {
            switch (button)
            {
                case MessageBoxButton.YesNo: return MessageBoxResult.Yes;
                case MessageBoxButton.OKCancel: return MessageBoxResult.OK;
                case MessageBoxButton.YesNoCancel: return MessageBoxResult.Yes;
                default: return MessageBoxResult.OK;
            }
        }

        // ============================================================
        // 倒计时自动确认（2026-10-22 新增）
        // ============================================================

        /// <summary>倒计时用的计时器（走 UI 线程的 DispatcherTimer）。</summary>
        private System.Windows.Threading.DispatcherTimer _countdownTimer;

        /// <summary>倒计时剩余秒数。</summary>
        private int _countdownLeft;

        /// <summary>倒计时走完后要自动按下的按钮。</summary>
        private System.Windows.Controls.Button _countdownTarget;

        /// <summary>倒计时提示行的模板文案（{0} 会被替换成剩余秒数）。</summary>
        private string _countdownFormat;

        /// <summary>
        /// 显示一个**带倒计时**的确认框：倒计时走完后自动按默认按钮。
        ///
        /// <para>【场景】2026-10-22 用户要求：「任务页开启了完成任务后自动关机，
        /// 但是会弹出一个确认框，应该是倒计时 15s，如果用户没有选取消关机，
        /// 那么结束后自动在 1 分钟内唤起关机」。</para>
        ///
        /// <para>【为什么不能直接用普通确认框】普通确认框是**阻塞式**的：
        /// 用户不点它就一直停在那儿；程序也没法"到点自己继续"。
        /// 倒计时版本到点后自动等同点了默认按钮，主流程继续往下走
        /// （关机那边接着就是 <c>shutdown /s /t 60</c>，即 1 分钟内关机）。</para>
        ///
        /// <param name="countdownSeconds">倒计时秒数；≤0 表示不启用倒计时。</param>
        /// <param name="countdownFormat">提示行文案，{0} 为剩余秒数。</param>
        /// </summary>
        public static MessageBoxResult ShowWithCountdown(Window owner, string text, string title,
            string primaryText, string secondaryText, MessageBoxImage icon,
            int countdownSeconds, string countdownFormat,
            MessageBoxResult defaultResult = MessageBoxResult.Yes)
        {
            var dlg = new AppDialog();
            if (owner != null && owner.IsVisible)
            {
                dlg.Owner = owner;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            dlg.Configure(text, title, MessageBoxButton.OKCancel, icon, defaultResult);

            // OKCancel 的槽位：Btn2 = 确定(OK) 在左，Btn1 = 取消(Cancel) 在右
            dlg.Btn2.Content = primaryText;
            dlg.Btn1.Content = secondaryText;
            dlg.Btn2.MinWidth = 108;
            dlg.Btn1.MinWidth = 108;

            // ⚠️ SetupButtons 在 OKCancel 模式下把 Btn2 的 Tag 设成 **OK**，
            //    而这里调用方要的默认结果是 **Yes**（"确认关机"）。
            //    不改 Tag 的话：① 倒计时找不到目标按钮（不会自动继续）；
            //    ② 即使用户手点"确认关机"，返回的也是 OK 而非 Yes，
            //    调用方的 `ans != Yes` 判断会把"确认"当成"取消" → 永远不关机。
            //    这里把两个按钮的 Tag 显式改成调用方要的语义。
            dlg.Btn2.Tag = defaultResult;
            dlg.Btn1.Tag = MessageBoxResult.No;

            // Tag 改了 → 默认按钮的高亮也要重算（ApplyDefaultHighlight 在 Configure 里
            // 按当时的 Tag 算过一次，那次 Btn2 还是 OK，认不出现在的 Yes）。
            dlg.ApplyDefaultHighlight();

            dlg.StartCountdown(countdownSeconds, countdownFormat, defaultResult);

            dlg.ShowDialog();
            return dlg._result;
        }

        /// <summary>启动倒计时：到点自动按默认按钮。</summary>
        private void StartCountdown(int seconds, string format, MessageBoxResult defaultResult)
        {
            if (seconds <= 0) return;

            _countdownLeft = seconds;
            _countdownFormat = string.IsNullOrEmpty(format)
                ? "将在 {0} 秒后自动继续…"
                : format;

            // 找到对应默认结果的按钮作为倒计时结束时的自动目标
            _countdownTarget = null;
            foreach (var b in new[] { Btn1, Btn2, Btn3 })
            {
                if (b.Visibility == Visibility.Visible && b.Tag is MessageBoxResult r
                    && r == defaultResult)
                {
                    _countdownTarget = b;
                    break;
                }
            }
            if (_countdownTarget == null) return;

            try { CountdownText.Visibility = Visibility.Visible; } catch { }
            UpdateCountdownText();

            _countdownTimer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromSeconds(1), System.Windows.Threading.DispatcherPriority.Normal,
                OnCountdownTick, Dispatcher);
            _countdownTimer.Start();
        }

        private void OnCountdownTick(object sender, EventArgs e)
        {
            _countdownLeft--;
            if (_countdownLeft > 0)
            {
                UpdateCountdownText();
                return;
            }

            // 到点：停表 + 自动按默认按钮
            try { _countdownTimer?.Stop(); } catch { }
            try { CountdownText.Visibility = Visibility.Collapsed; } catch { }

            var target = _countdownTarget;
            _countdownTarget = null;
            if (target != null) Finish(target);
        }

        private void UpdateCountdownText()
        {
            try
            {
                CountdownText.Text = string.Format(_countdownFormat, _countdownLeft);
            }
            catch { }
        }

        // ============================================================
        // 组装
        // ============================================================

        /// <summary>把文案、按钮组合、图标语义铺进界面。</summary>
        private void Configure(string text, string title, MessageBoxButton button,
                               MessageBoxImage icon, MessageBoxResult defaultResult)
        {
            // 窗口标题也要跟着内容变（任务栏 / Alt-Tab / UIA 都读这个），
            // 只改 TitleText 的话，所有对话框在系统层面都叫「提示」。
            this.Title = string.IsNullOrEmpty(title) ? "提示" : title;
            TitleText.Text = this.Title;
            BodyText.Text = text ?? "";
            _defaultResult = defaultResult;

            SetupIcon(icon);
            SetupButtons(button, defaultResult);
        }

        /// <summary>
        /// 图标按语义分三色：信息=主色、警告=琥珀、错误=红。
        /// 圆底 + 线性图标，跟主界面空状态同一种语言。
        /// </summary>
        private void SetupIcon(MessageBoxImage icon)
        {
            string geoKey;
            string bgKey;
            string strokeKey;

            switch (icon)
            {
                case MessageBoxImage.Warning:
                    geoKey = "IconWarning";
                    bgKey = "WarningLightBrush";
                    strokeKey = "WarningBrush";
                    break;
                case MessageBoxImage.Error:
                    geoKey = "IconXCircle";
                    bgKey = "DangerLightBrush";
                    strokeKey = "DangerBrush";
                    break;
                case MessageBoxImage.Question:
                    geoKey = "IconInfo";
                    bgKey = "PrimaryLightBrush";
                    strokeKey = "PrimaryBrush";
                    break;
                default: // Information
                    geoKey = "IconInfo";
                    bgKey = "PrimaryLightBrush";
                    strokeKey = "PrimaryBrush";
                    break;
            }

            try { IconPath.Data = (Geometry)FindResource(geoKey); } catch { }
            try { IconBox.Background = (Brush)FindResource(bgKey); } catch { }
            try { IconPath.Stroke = (Brush)FindResource(strokeKey); } catch { }
        }

        /// <summary>
        /// 按钮按 Windows 习惯排列：主操作在最右（回车默认）、次操作在左。
        /// 中文按钮带助记符（是(Y) / 否(N)），与原 MessageBox 保持一致，
        /// 老用户不用重新学。
        /// </summary>
        private void SetupButtons(MessageBoxButton button, MessageBoxResult defaultResult)
        {
            switch (button)
            {
                case MessageBoxButton.OK:
                    Btn1.Content = "确定";
                    Btn1.Tag = MessageBoxResult.OK;
                    Btn2.Visibility = Visibility.Collapsed;
                    Btn3.Visibility = Visibility.Collapsed;
                    break;

                case MessageBoxButton.OKCancel:
                    Btn2.Content = "确定";
                    Btn2.Tag = MessageBoxResult.OK;
                    Btn1.Content = "取消";
                    Btn1.Tag = MessageBoxResult.Cancel;
                    Btn3.Visibility = Visibility.Collapsed;
                    break;

                case MessageBoxButton.YesNo:
                    Btn2.Content = "是(Y)";
                    Btn2.Tag = MessageBoxResult.Yes;
                    Btn1.Content = "否(N)";
                    Btn1.Tag = MessageBoxResult.No;
                    Btn3.Visibility = Visibility.Collapsed;
                    break;

                case MessageBoxButton.YesNoCancel:
                    Btn3.Content = "是(Y)";
                    Btn3.Tag = MessageBoxResult.Yes;
                    Btn2.Content = "否(N)";
                    Btn2.Tag = MessageBoxResult.No;
                    Btn1.Content = "取消";
                    Btn1.Tag = MessageBoxResult.Cancel;
                    Btn3.Visibility = Visibility.Visible;
                    break;
            }

            // 默认按钮用主色实心，其余用次要样式 —— 一眼看出回车会触发哪个
            ApplyDefaultHighlight();
        }

        private void ApplyDefaultHighlight()
        {
            var primary = TryFindResource("PrimaryButton") as Style;
            var secondary = TryFindResource("SecondaryButton") as Style;

            foreach (var b in new[] { Btn1, Btn2, Btn3 })
            {
                if (b.Visibility != Visibility.Visible) continue;
                bool isDefault = b.Tag is MessageBoxResult r && r == _defaultResult;
                if (primary != null && secondary != null)
                    b.Style = isDefault ? primary : secondary;
            }
        }

        // ============================================================
        // 交互
        // ============================================================

        private void OnTitleBarDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
        }

        private void OnButton1(object sender, RoutedEventArgs e) { Finish(Btn1); }
        private void OnButton2(object sender, RoutedEventArgs e) { Finish(Btn2); }
        private void OnButton3(object sender, RoutedEventArgs e) { Finish(Btn3); }

        private void Finish(System.Windows.Controls.Button b)
        {
            StopCountdown();
            _result = b.Tag is MessageBoxResult r ? r : MessageBoxResult.None;
            Close();
        }

        /// <summary>停掉倒计时计时器（对话框关闭时必须调用，否则会泄漏）。</summary>
        private void StopCountdown()
        {
            try { _countdownTimer?.Stop(); } catch { }
            _countdownTimer = null;
            _countdownTarget = null;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            StopCountdown();
            // 点右上角 X：行为跟"取消/否"一致
            switch (_result)
            {
                default:
                    _result = MessageBoxResult.Cancel;
                    break;
            }
            // 如果是 OK-only 的提示窗，关闭即视为已读
            if (Btn2.Visibility == Visibility.Collapsed && Btn3.Visibility == Visibility.Collapsed)
                _result = MessageBoxResult.OK;
            Close();
        }

        /// <summary>
        /// Esc = 取消（或 OK-only 时关闭）；回车 = 默认按钮。
        /// 用 PreviewKeyDown 以抢在按钮之前处理。
        /// </summary>
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                OnCancel(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter)
            {
                foreach (var b in new[] { Btn1, Btn2, Btn3 })
                {
                    if (b.Visibility == Visibility.Visible && b.Tag is MessageBoxResult r
                        && r == _defaultResult)
                    {
                        Finish(b);
                        e.Handled = true;
                        return;
                    }
                }
            }
            base.OnPreviewKeyDown(e);
        }

        /// <summary>入场淡入 + 轻微上移，避免弹窗"啪"地砸出来。</summary>
        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            try
            {
                RootCard.Opacity = 0;
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130));
                RootCard.BeginAnimation(OpacityProperty, fade);
            }
            catch { }
        }

        /// <summary>窗口关闭时兜底停表（按 X / Alt+F4 关闭时也会走到这里）。</summary>
        protected override void OnClosed(EventArgs e)
        {
            StopCountdown();
            base.OnClosed(e);
        }
    }
}
