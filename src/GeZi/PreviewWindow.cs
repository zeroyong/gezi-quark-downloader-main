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
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GeZi
{
    /// <summary>
    /// 单文件**点击预览窗**（2026-11-15 P0，同日按用户反馈重构）。
    ///
    /// <para>【用户反馈驱动的改版】「展示不完整 滑动滚轮缩放图片 a/d 切换前后的图片
    /// 上方展示标题 把蓝色外框去掉 还有没有对应的日期 现在只展示了大小」+「图片展示
    /// 记得有一定的圆角 美化」。逐条落实：
    /// ① 完整显示 —— 初版 Image 放 ScrollViewer 里：ScrollViewer 给子元素的 Measure
    ///    空间无限，Stretch=Uniform 在无限约束下不缩放 → 大图按解码尺寸渲染只能滚动
    ///    看局部。现在按**视口-内边距**显式算出适配尺寸（fit），初始整图可见；
    /// ② 滚轮缩放 —— 挂 <b>ScrollViewer.PreviewMouseWheel</b>（挂在 Image 上，鼠标
    ///    不在图上时会漏给滚动条变滚动）；缩放直接改 Image 布局宽高（RenderTransform
    ///    不改布局尺寸，ScrollViewer 不会出滚动条 —— 初版也踩了这个）；
    /// ③ A/D 切换 —— MainWindow 传 NavigateDelta 回调；窗口键盘 A/D（图片态另有
    ///    ←/→）切换前/后一个可预览文件，两侧半透明圆形箭头与键盘同源，到头自动隐藏；
    /// ④ 顶部标题 —— 居中完整文件名（超长省略 + ToolTip 看全名）；
    /// ⑤ 蓝色外框 —— 就是 ScrollViewer 的**默认 1px 系统边框**，BorderThickness=0
    ///    干掉；内容区统一套圆角深底卡片；
    /// ⑥ 日期 —— meta 行补 yyyy-MM-dd HH:mm（MainWindow 传 UpdatedAtText）；
    /// ⑦ 圆角美化 —— 图片按其实际显示尺寸裁 RectangleGeometry 圆角（贴图边不悬空），
    ///    文本框透明底嵌在圆角卡片里。</para>
    ///
    /// <para>【数据流】MainWindow 构造窗 + SetItem（含 loader：取直链+拉前 N 字节）；
    /// 窗体只管 loading → 内容 / 错误。取链失败也算预览失败，错误在**窗内**展示 ——
    /// 窗已经弹出来了，提示条够不着。</para>
    /// </summary>
    internal sealed class PreviewWindow : Window
    {
        private string _name;
        private long _size;
        private ShareFileItem.PreviewKind _kind;
        private Func<CancellationToken, Task<byte[]>> _loader;
        private string _dateText = "";
        private int _index;
        private int _count;

        /// <summary>当前请求的取消源。A/D 切换文件 = Cancel 旧的 + 换新的（旧请求立即作废）。</summary>
        private CancellationTokenSource _cts = new CancellationTokenSource();

        /// <summary>切到前/后一个可预览文件（±1）。MainWindow 闭包提供；null = 无导航。</summary>
        public Action<int> NavigateDelta;

        private Grid _contentHost;      // 内容区外层（含导航按钮/缩放指示）
        private TextBlock _title;
        private TextBlock _meta;
        private Button _saveBtn;
        private UIElement _navPrev, _navNext;
        private TextBlock _zoomLabel;

        private byte[] _bytes;          // 拉到的原始字节（另存为时直接写盘）
        private bool _truncated;        // 文本超上限被截断（给尾注用）
        private bool _loaded;
        private bool _pendingRun;       // SetItem 早于 Loaded 时挂起，Loaded 后执行

        // ---------- 图片缩放状态 ----------
        private Image _img;
        private double _srcW, _srcH;    // 解码后源图尺寸
        private double _fitW, _fitH;    // 视口内"完整可见"的基础尺寸（zoom=1）
        private double _zoom = 1.0;     // 0.4 ~ 4.0

        /// <summary>图片预览的解码上限边（DecodePixelWidth）—— 大图降采样防内存爆炸。</summary>
        private const int ImageDecodeMax = 2048;
        private const double ZoomMin = 0.4, ZoomMax = 4.0, ZoomStep = 0.12;
        /// <summary>图片圆角半径（用户定稿：「有一定的圆角 美化」）。</summary>
        private const double ImageCornerRadius = 12;

        public PreviewWindow()
        {
            DialogChrome.StyleWindow(this, 740, 600);
            Background = (TryFindResource("AppBgBrush") as Brush) ?? Background;

            BuildUi();
            Loaded += (_, __) =>
            {
                _loaded = true;
                if (_pendingRun) { _pendingRun = false; RunLoadAsync(); }
            };
            Closed += (_, __) => _cts.Cancel();
            PreviewKeyDown += OnPreviewKeyDown;

            // 【2026-11-17】窗口级左键抬起 = 拖动兜底复位。
            // 只在这里挂一次（AttachDragToPan 会随 A/D 切换重复调用，挂里面会累积订阅）。
            // 覆盖"鼠标拖出窗口外才松手"的情况：此时 ScrollViewer 的 Up 收不到。
            PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (!_imgDragging || _dragScroll == null) return;
                EndImageDrag();
            };
        }

        // ---------- 图片拖动状态（窗口级，供上面的兜底回调读） ----------
        private bool _imgDragging;
        private ScrollViewer _dragScroll;

        /// <summary>结束拖动并复位光标/捕获（可被多个出口重复调用，内部幂等）。</summary>
        private void EndImageDrag()
        {
            _imgDragging = false;
            var sc = _dragScroll;
            if (sc != null)
            {
                try { sc.ReleaseMouseCapture(); } catch { }
                sc.Cursor = Cursors.SizeAll;
            }
        }

        // ====================================================================
        //  SetItem：装填一个文件（首开与 A/D 切换共用入口）
        // ====================================================================

        public void SetItem(string name, long size, ShareFileItem.PreviewKind kind,
            Func<CancellationToken, Task<byte[]>> loader, string dateText, int index, int count)
        {
            // 上一张还在拉？立刻取消 —— 切换文件的响应必须干脆，旧请求不能串台。
            _cts.Cancel();
            _cts = new CancellationTokenSource();

            _name = name;
            _size = size;
            _kind = kind;
            _loader = loader;
            _dateText = dateText ?? "";
            _index = index;
            _count = count;
            _bytes = null;
            _truncated = false;
            _zoom = 1.0;
            _fitW = _fitH = 0;
            _img = null;
            // 【2026-11-17】换文件时内容区会被整个重建（ScrollViewer 是新的），
            // 旧的拖动状态必须清掉，否则下次进来鼠标一动就"飘"。
            EndImageDrag();
            _dragScroll = null;

            Title = "预览 — " + name;
            UpdateHead();
            ShowLoading();
            UpdateNavButtons();
            _saveBtn.IsEnabled = false;

            if (_loaded) RunLoadAsync();
            else _pendingRun = true;
        }

        // ====================================================================
        //  UI 构造
        // ====================================================================

        private void BuildUi()
        {
            var root = new Grid { Margin = new Thickness(16, 10, 16, 12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 标题
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // meta
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 内容
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 按钮

            // ===== 顶部标题：居中、完整文件名（超长省略，ToolTip 看全名） =====
            _title = new TextBlock
            {
                FontSize = 16.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 660,
                Foreground = (TryFindResource("TextPrimaryBrush") as Brush) ?? Brushes.White,
            };
            Grid.SetRow(_title, 0);
            root.Children.Add(_title);

            // ===== meta：大小 · 日期 · 预览方式 =====
            _meta = new TextBlock
            {
                Margin = new Thickness(0, 4, 0, 10),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (TryFindResource("TextTertiaryBrush") as Brush) ?? Brushes.Gray,
            };
            Grid.SetRow(_meta, 1);
            root.Children.Add(_meta);

            // ===== 内容区：圆角深底卡片（图片/文本/错误都装在这里面） =====
            var card = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = (TryFindResource("SurfaceSunkenBrush") as Brush)
                    ?? new SolidColorBrush(Color.FromRgb(0x15, 0x18, 0x1D)),
                ClipToBounds = true,
            };
            _contentHost = new Grid();
            card.Child = _contentHost;
            Grid.SetRow(card, 2);
            root.Children.Add(card);

            // 视口尺寸变化（首帧渲染 / 用户拉窗口）→ 重算图片 fit。
            // ★ 只挂这一次：挂进 ShowImage 会在 A/D 切换时累积 handler。
            card.SizeChanged += OnImageViewportChanged;

            // ===== 底部按钮 =====
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            _saveBtn = DialogChrome.PrimaryButton("另存为…", OnSaveAs);
            _saveBtn.IsEnabled = false;
            btnRow.Children.Add(_saveBtn);
            btnRow.Children.Add(DialogChrome.SecondaryButton("关闭", (_, __) => Close()));
            Grid.SetRow(btnRow, 3);
            root.Children.Add(btnRow);

            Content = root;
            UpdateHead();
        }

        /// <summary>标题与 meta 刷新（SetItem 与加载完成共用；extra 是加载后的补充信息）。</summary>
        private void UpdateHead(string extra = null)
        {
            if (_title != null)
            {
                _title.Text = _name;
                _title.ToolTip = _name;
            }
            if (_meta != null)
            {
                string desc = _kind == ShareFileItem.PreviewKind.Image
                    ? "图片预览 · 滚轮缩放 · A/D 切换"
                    : "文本预览 · A/D 切换";
                string date = string.IsNullOrEmpty(_dateText) ? "" : " · " + _dateText;
                _meta.Text = Util.FormatSize(_size) + date + " · " + desc
                    + (string.IsNullOrEmpty(extra) ? "" : " · " + extra);
            }
        }

        // ====================================================================
        //  内容三态：加载中 / 内容 / 错误
        // ====================================================================

        /// <summary>加载中态：转圈 + 文案（取直链可能要几秒，必须让用户看到动静）。</summary>
        private void ShowLoading()
        {
            _contentHost.Children.Clear();
            _navPrev = _navNext = _zoomLabel = null;
            var sp = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            sp.Children.Add(MakeSpinner());
            sp.Children.Add(new TextBlock
            {
                Text = "正在加载预览…（取直链可能要几秒）",
                Margin = new Thickness(0, 12, 0, 0),
                Foreground = (TryFindResource("TextSecondaryBrush") as Brush) ?? Brushes.DimGray,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            _contentHost.Children.Add(sp);
        }

        /// <summary>自绘转圈。Forever 动画会抓住变换对象不放 —— 窗关了显式停掉，否则泄漏。</summary>
        private FrameworkElement MakeSpinner()
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M12,3 A9,9 0 1 0 21,12"),
                Stroke = (TryFindResource("PrimaryBrush") as Brush) ?? Brushes.RoyalBlue,
                StrokeThickness = 2.2,
                StrokeStartLineCap = PenLineCap.Round,
                Width = 30,
                Height = 30,
                Stretch = Stretch.Uniform,
            };
            var rotate = new RotateTransform(0);
            path.RenderTransform = rotate;
            path.RenderTransformOrigin = new Point(0.5, 0.5);
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromSeconds(0.9),
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            };
            rotate.BeginAnimation(RotateTransform.AngleProperty, anim);
            Closed += (_, __) => rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            return path;
        }

        /// <summary>错误态：说明 + 出口。失败必须**看得见**（窗已开，提示条够不着）。</summary>
        private void ShowError(string message)
        {
            _contentHost.Children.Clear();
            _navPrev = _navNext = _zoomLabel = null;
            var sp = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 520,
            };
            var icon = new System.Windows.Shapes.Path
            {
                Data = (TryFindResource("IconCloudOff") as Geometry)
                    ?? Geometry.Parse("M6,3.5 H14 L18.5,8 V20.5 H6 Z"),
                Stroke = (TryFindResource("DangerBrush") as Brush) ?? Brushes.IndianRed,
                StrokeThickness = 1.6,
                Width = 42, Height = 42,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            sp.Children.Add(icon);
            sp.Children.Add(new TextBlock
            {
                Text = "暂时无法预览",
                FontWeight = FontWeights.SemiBold,
                FontSize = 15,
                Margin = new Thickness(0, 14, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            sp.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                Foreground = (TryFindResource("TextSecondaryBrush") as Brush) ?? Brushes.DimGray,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            sp.Children.Add(new TextBlock
            {
                Text = "可以「另存为」保存原始文件，或用 A/D 切换其他文件。",
                FontSize = 12,
                Margin = new Thickness(0, 10, 0, 0),
                Foreground = (TryFindResource("TextTertiaryBrush") as Brush) ?? Brushes.Gray,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            _contentHost.Children.Add(sp);

            _saveBtn.IsEnabled = _bytes != null && _bytes.Length > 0;
        }

        // ====================================================================
        //  加载
        // ====================================================================

        private void RunLoadAsync()
        {
            if (_loader == null) return;
            var cts = _cts;
            _ = RunLoadCoreAsync(cts);
        }

        private async Task RunLoadCoreAsync(CancellationTokenSource cts)
        {
            try
            {
                var bytes = await _loader(cts.Token);
                if (cts.IsCancellationRequested) return;
                _bytes = bytes;

                if (_kind == ShareFileItem.PreviewKind.Image)
                    ShowImage(bytes);
                else
                    ShowText(bytes, out _truncated);

                _saveBtn.IsEnabled = true;
                UpdateHead("实取 " + Util.FormatSize(bytes.Length) + (_truncated ? "（已截断）" : ""));
            }
            catch (OperationCanceledException)
            {
                // 切走 / 关窗，什么都不做
            }
            catch (Exception ex)
            {
                if (cts.IsCancellationRequested || !_loaded) return;
                ShowError(ex.Message);
            }
        }

        // ---------------- 图片 ----------------

        /// <summary>
        /// 图片渲染：视口内显式算 fit 尺寸（初始整图可见）→ 圆角裁切 → 滚轮缩放
        /// 改布局宽高（ScrollViewer 滚动区正确）→ 左下角缩放百分比 → 导航按钮。
        /// </summary>
        private void ShowImage(byte[] bytes)
        {
            BitmapImage bmp;
            try
            {
                bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = ImageDecodeMax;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
            }
            catch
            {
                throw new Exception("不是可识别的图片格式（WPF 解码器不支持该格式，如 webp/heic）。");
            }

            _contentHost.Children.Clear();
            _srcW = bmp.PixelWidth;
            _srcH = bmp.PixelHeight;

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(0),   // ★ "蓝色外框"的来源：ScrollViewer 默认 1px 系统边框
                Background = Brushes.Transparent,
                Padding = new Thickness(16),
            };

            _img = new Image
            {
                Source = bmp,
                Cursor = Cursors.SizeAll,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // 圆角贴实际显示尺寸（宽高每次变化都重裁）
            _img.SizeChanged += (s, e) => UpdateImageClip();
            scroll.Content = _img;
            _contentHost.Children.Add(scroll);

            // 缩放指示（左下角）
            _zoomLabel = new TextBlock
            {
                Text = "100%",
                FontSize = 11,
                Margin = new Thickness(12, 0, 0, 10),
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Left,
                Foreground = (TryFindResource("TextTertiaryBrush") as Brush) ?? Brushes.Gray,
            };
            _contentHost.Children.Add(_zoomLabel);

            AddNavButtons();

            // 滚轮缩放：拦在 ScrollViewer 上（挂 Image 上会漏掉鼠标在空白区的情况）
            scroll.PreviewMouseWheel += (s, e) =>
            {
                ApplyZoom(_zoom + (e.Delta > 0 ? ZoomStep : -ZoomStep));
                e.Handled = true;
            };

            // ==================== 【2026-11-17 新增·鼠标拖动平移】====================
            // 用户需求：「添加图片预览时 鼠标按住可以拖动，现在只能滚轮放大缩小」。
            //
            // 【为什么不用 ScrollViewer 自带的 PanningMode】PanningMode 是**触摸**惯性与
            // 拖拽滚动（为触屏设计的），鼠标左键按住并不会滚动内容 —— 桌面用户看到
            // 光标一直是 SizeAll 却拖不动，正是用户报的这个现象。
            //
            // 【实现】自己接管左键拖动：记住起点与当时的滚动偏移，移动时反向设置
            // ScrollViewer 的 ScrollToHorizontalOffset/VerticalOffset。
            // 只在"图片比视口大"时才真正有位移（否则滚动范围是 0，拖了也不动，符合直觉）。
            AttachDragToPan(scroll);

            // 双击复位（左键双击 = 回到适配尺寸）
            _img.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2) ApplyZoom(1.0);
            };

            // 容器此刻可能还没走完布局 —— 有尺寸先算，没有等 SizeChanged 回来算
            TryFitImage();
        }

        /// <summary>
        /// 给图片预览挂**鼠标按住拖动平移**（2026-11-17）。
        ///
        /// <para>【要点】① 用 <see cref="Mouse.Capture"/> 抓住鼠标，保证拖出图片边界
        /// 也继续跟随（否则一移出 Image 就丢事件，手感是"拖两下就断"）；
        /// ② 位移 = 起点偏移 − 鼠标移动量，符号方向必须与实际滚动方向一致；
        /// ③ 拖动期间把光标换成 <see cref="Cursors.ScrollAll"/>（四向箭头），
        /// 松手恢复 <see cref="Cursors.SizeAll"/>，让用户知道"现在能拖"。</para>
        /// </summary>
        private void AttachDragToPan(ScrollViewer scroll)
        {
            if (scroll == null) return;
            _dragScroll = scroll;

            Point dragStart = default(Point);
            double startH = 0, startV = 0;

            scroll.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount >= 2) return;          // 双击留给"复位缩放"
                dragStart = e.GetPosition(scroll);
                startH = scroll.HorizontalOffset;
                startV = scroll.VerticalOffset;
                _imgDragging = true;
                try { scroll.CaptureMouse(); } catch { }
                scroll.Cursor = Cursors.ScrollAll;
                e.Handled = true;
            };

            scroll.PreviewMouseMove += (s, e) =>
            {
                if (!_imgDragging) return;
                var p = e.GetPosition(scroll);
                // 反向：鼠标往右拖 → 内容跟着往右走 → 视口偏移减小
                scroll.ScrollToHorizontalOffset(startH - (p.X - dragStart.X));
                scroll.ScrollToVerticalOffset(startV - (p.Y - dragStart.Y));
                e.Handled = true;
            };

            // 松手 / 丢失捕获都要复位（否则拖动状态会卡住，之后鼠标一动图就飘）
            scroll.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (!_imgDragging) return;
                EndImageDrag();
                e.Handled = true;
            };
            scroll.LostMouseCapture += (s, e) =>
            {
                if (!_imgDragging) return;
                _imgDragging = false;
                scroll.Cursor = Cursors.SizeAll;
            };
        }

        /// <summary>视口尺寸变化（首帧渲染 / 用户拉窗口）→ 重算 fit。</summary>
        private void OnImageViewportChanged(object sender, SizeChangedEventArgs e)
        {
            TryFitImage();
        }

        /// <summary>按视口与源图比例算 zoom=1 的适配尺寸（小图不放大），并应用当前 zoom。</summary>
        private void TryFitImage()
        {
            if (_img == null || _srcW <= 0 || _contentHost.ActualWidth < 20) return;

            double vw = _contentHost.ActualWidth - 44;   // ScrollViewer Padding 16×2 + 呼吸余量
            double vh = _contentHost.ActualHeight - 44;
            if (vw < 20 || vh < 20) return;

            double scale = Math.Min(vw / _srcW, vh / _srcH);
            if (scale > 1) scale = 1;                    // 小图不硬放大（DownOnly 语义）
            _fitW = Math.Max(1, _srcW * scale);
            _fitH = Math.Max(1, _srcH * scale);
            ApplyZoom(_zoom);
        }

        /// <summary>应用缩放：直接改 Image 布局宽高（ScrollViewer 滚动区随之变化）。</summary>
        private void ApplyZoom(double z)
        {
            _zoom = Math.Max(ZoomMin, Math.Min(ZoomMax, z));
            if (_img != null && _fitW > 0)
            {
                _img.Width = Math.Max(1, Math.Round(_fitW * _zoom));
                _img.Height = Math.Max(1, Math.Round(_fitH * _zoom));
            }
            if (_zoomLabel != null)
                _zoomLabel.Text = Math.Round(_zoom * 100) + "%";
        }

        /// <summary>给图片裁圆角（按实际显示尺寸，贴图边不悬空）。</summary>
        private void UpdateImageClip()
        {
            if (_img == null) return;
            double w = _img.ActualWidth > 0 ? _img.ActualWidth : (_img.Width > 0 ? _img.Width : 1);
            double h = _img.ActualHeight > 0 ? _img.ActualHeight : (_img.Height > 0 ? _img.Height : 1);
            _img.Clip = new RectangleGeometry(
                new Rect(0, 0, w, h), ImageCornerRadius, ImageCornerRadius);
        }

        // ---------------- 文本 ----------------

        /// <summary>文本渲染：UTF-8 严格 → GBK 回落；等宽只读可选中复制。</summary>
        private void ShowText(byte[] bytes, out bool truncated)
        {
            truncated = false;
            string text;
            try
            {
                var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
                text = strict.GetString(bytes);
            }
            catch
            {
                text = Encoding.GetEncoding(936).GetString(bytes);
            }

            _contentHost.Children.Clear();

            var box = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                FontFamily = new FontFamily("Consolas, 'Microsoft YaHei UI'"),
                FontSize = 12.5,
                Background = Brushes.Transparent,
                Foreground = (TryFindResource("TextSecondaryBrush") as Brush) ?? Brushes.WhiteSmoke,
                BorderThickness = new Thickness(0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(16),
            };
            _contentHost.Children.Add(box);

            if (bytes.Length >= 256 * 1024)
            {
                truncated = true;
                box.Text = text + "\n\n—— 已截断：仅显示前 256 KB，完整内容请「另存为」后查看。";
            }

            AddNavButtons();
        }

        // ---------------- 导航 ----------------

        /// <summary>挂左右导航按钮（图片/文本两态都有；到头自动隐藏由 UpdateNavButtons 管）。</summary>
        private void AddNavButtons()
        {
            _navPrev = MakeNavButton("‹", -1, () => NavigateDelta?.Invoke(-1));
            _navNext = MakeNavButton("›", +1, () => NavigateDelta?.Invoke(+1));
            _contentHost.Children.Add(_navPrev);
            _contentHost.Children.Add(_navNext);
            UpdateNavButtons();
        }

        /// <summary>
        /// 半透明圆形导航按钮（夸克网页式左右箭头）。
        /// 用 Border+TextBlock 组合而非 Button 模板 —— 预览窗里按钮不参与焦点，
        /// 自拼视觉层最直接，也免掉 ControlTemplate 的 ContentPresenter 接线。
        /// </summary>
        private UIElement MakeNavButton(string glyph, double side, Action onClick)
        {
            var txt = new TextBlock
            {
                Text = glyph,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = (TryFindResource("TextPrimaryBrush") as Brush) ?? Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -3, 0, 0), // 字形在圆里的光学居中
            };
            var idle = new SolidColorBrush(Color.FromArgb(0xA8, 0x2C, 0x31, 0x3B));
            var hover = new SolidColorBrush(Color.FromArgb(0xD0, 0x3A, 0x40, 0x4C));
            idle.Freeze();
            hover.Freeze();

            var bd = new Border
            {
                Width = 38,
                Height = 38,
                CornerRadius = new CornerRadius(19),
                Background = idle,
                BorderBrush = (TryFindResource("BorderBrushStrong") as Brush) ?? Brushes.DimGray,
                BorderThickness = new Thickness(1),
                Child = txt,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = side < 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                Margin = new Thickness(side < 0 ? 12 : 0, 0, side < 0 ? 0 : 12, 0),
            };
            bd.MouseEnter += (_, __) => bd.Background = hover;
            bd.MouseLeave += (_, __) => bd.Background = idle;
            bd.MouseLeftButtonDown += (s, e) => { onClick(); e.Handled = true; };
            return bd;
        }

        /// <summary>导航按钮显隐：到头一侧隐藏（首张无左箭头、末张无右箭头），单文件都隐藏。</summary>
        private void UpdateNavButtons()
        {
            if (_navPrev != null)
                _navPrev.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_navNext != null)
                _navNext.Visibility = _index < _count - 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 键盘导航：A/D 始终有效（文本框只读，A/D 不会被输入消费）；图片态另有 ←/→；
        /// Esc 关窗。挂 PreviewKeyDown —— 隧道事件先于 TextBox 的光标移动。
        /// </summary>
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            Key k = e.Key == Key.System ? e.SystemKey : e.Key;
            int delta = 0;
            if (k == Key.A) delta = -1;
            else if (k == Key.D) delta = +1;
            else if (_kind == ShareFileItem.PreviewKind.Image && k == Key.Left) delta = -1;
            else if (_kind == ShareFileItem.PreviewKind.Image && k == Key.Right) delta = +1;
            else if (k == Key.Escape) { Close(); e.Handled = true; return; }

            if (delta != 0)
            {
                NavigateDelta?.Invoke(delta);
                e.Handled = true;
            }
        }

        // ====================================================================
        //  另存为（预览窗自己的下载出口）
        // ====================================================================

        private void OnSaveAs(object sender, RoutedEventArgs e)
        {
            if (_bytes == null || _bytes.Length == 0) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = _name,
                Filter = "所有文件 (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                File.WriteAllBytes(dlg.FileName, _bytes);
                MessageBox.Show(this, "已保存到：\n" + dlg.FileName, "保存成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败：" + ex.Message, "保存失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
