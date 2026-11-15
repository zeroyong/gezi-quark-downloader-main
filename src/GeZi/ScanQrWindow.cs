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
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using GeZi.Core.Support;
using WpfImage = System.Windows.Controls.Image;   // 避免与 System.Drawing.Image 撞名

namespace GeZi
{
    /// <summary>
    /// 「识别二维码」对话框（2026-10-04 新增，2026-11-13 改为自动识别）。
    ///
    /// 【为什么需要】夸克分享有时只给一张二维码图片、不给文本链接（用户原话：
    /// 「有些时候夸克的分享链接不是一点点形式而是二维码」）。原来只能手动
    /// 把图存下来、再找第三方工具扫，很断。这里做进软件里。
    ///
    /// 【四种取图方式】按"顺手程度"排：
    ///   1. Ctrl+V 直接粘贴剪贴板里的截图 ← 默认，最快（截图后不用存盘）
    ///   2. Ctrl+V 粘贴 <c>data:image/png;base64,…</c> 这类**文本形式的图片**
    ///      （2026-11-13 新增；用户原话：「就是类似 这种 data:image/png;base64,iVBORw0KGg
    ///      只是图片链接 需要转成网址」）
    ///   3. 「选择图片…」从磁盘挑
    ///   4. 直接把图片文件（或装着 base64 的 txt）拖到窗口上
    /// 四条路统一走 <see cref="QrDecoder"/> 解码，成功后把文本回传给主窗。
    ///
    /// 【★ 读入即识别】2026-11-13 用户要求「添加二维码 自动识别功能」：
    /// 图一进来（粘贴/选文件/拖入）就立刻在后台解码，不再需要点「识别」按钮；
    /// 按钮保留为"自动识别失败后手动重试"的兜底。
    /// </summary>
    internal sealed class ScanQrWindow : Window
    {
        private readonly Action<string> _onDecoded;   // 解出的文本（保证非空）
        private readonly Action<string> _log;

        private byte[] _pendingBytes;                  // 待解码的原始字节
        private string _pendingPath;                   // 或文件路径
        private bool _decoding;                        // 正在解码（防重复触发）

        // ⚠️ 这里的 Image 是 WPF 的 System.Windows.Controls.Image（下方 using 了别名 WpfImage），
        //    不能直接写 Image —— 会跟 System.Drawing.Image 撞名（CS0104）。
        private WpfImage _imgPreviewBox;
        private TextBlock _statusText;
        private Button _decodeBtn;

        /// <summary>
        /// 打开扫码窗口。<paramref name="onDecoded"/> 在解码成功后被调用（文本非空）。
        /// </summary>
        public ScanQrWindow(Action<string> onDecoded, Action<string> log)
        {
            _onDecoded = onDecoded;
            _log = log;

            Title = "识别二维码";
            ResizeMode = ResizeMode.NoResize;
            DialogChrome.StyleWindow(this, 520, 560);
            DialogChrome.ApplyRoundedCorners(this);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            DialogChrome.ApplyRoundedCorners(this);
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            // 开窗即尝试读取剪贴板：绝大多数场景就是"刚截完图过来"，直接命中
            TryPasteFromClipboard(silentIfEmpty: true);
        }

        // ---------------- 界面 ----------------

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            BuildUi();
        }

        private void BuildUi()
        {
            var root = new Grid { Margin = new Thickness(22, 20, 22, 18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 说明
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 预览
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 状态
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按钮

            var title = DialogChrome.PageTitle("识别二维码");
            Grid.SetRow(title, 0);
            root.Children.Add(title);

            var hint = DialogChrome.Hint(
                "把夸克分享二维码识别成链接。图一进来就自动识别，不用再点按钮：\n"
                + "· 截图后按 Ctrl+V 直接粘贴（最快）\n"
                + "· 粘贴 data:image/png;base64,… 这类图片文本（也能解）\n"
                + "· 点「选择图片…」从电脑里挑一张，或把图片文件拖到本窗口",
                0, 12);
            Grid.SetRow(hint, 1);
            root.Children.Add(hint);

            // 预览区：一个虚线框感的卡片，里面放拖放提示或已选图片
            var dropHint = new TextBlock
            {
                Text = "把图片拖到这里\n（或 Ctrl+V 粘贴 / 点下方「选择图片…」）",
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                FontSize = 12.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            {
                var b = TryFindResource("TextTertiaryBrush") as System.Windows.Media.Brush;
                if (b != null) dropHint.Foreground = b;
            }

            var preview = new WpfImage
            {
                Stretch = System.Windows.Media.Stretch.Uniform,
                Margin = new Thickness(6),
            };
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(
                preview, System.Windows.Media.BitmapScalingMode.HighQuality);

            var previewHost = new Grid();
            previewHost.Children.Add(dropHint);
            previewHost.Children.Add(preview);
            _imgPreviewBox = preview;

            var previewCard = DialogChrome.Card(previewHost);
            previewCard.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetRow(previewCard, 2);
            root.Children.Add(previewCard);

            _statusText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(2, 10, 2, 0),
                Visibility = Visibility.Collapsed,
            };
            Grid.SetRow(_statusText, 3);
            root.Children.Add(_statusText);

            var pickBtn = DialogChrome.SecondaryButton("选择图片…", (s, a) => PickFile());
            _decodeBtn = DialogChrome.PrimaryButton("识别", (s, a) => DecodeNow());
            _decodeBtn.IsEnabled = false;
            var cancelBtn = DialogChrome.SecondaryButton("取消", (s, a) => Close());

            var btns = DialogChrome.ButtonRow(pickBtn, _decodeBtn, cancelBtn);
            btns.Margin = new Thickness(0, 14, 0, 0);
            Grid.SetRow(btns, 4);
            root.Children.Add(btns);

            Content = root;

            // ---- 交互：Ctrl+V 粘贴、拖放 ----
            AllowDrop = true;
            PreviewKeyDown += (s, a) =>
            {
                if (a.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    TryPasteFromClipboard(silentIfEmpty: false);
                    a.Handled = true;
                }
                else if (a.Key == Key.Escape)
                {
                    Close();
                }
                else if (a.Key == Key.Enter && _decodeBtn.IsEnabled)
                {
                    DecodeNow();
                    a.Handled = true;
                }
            };

            Drop += OnDrop;
            DragOver += (s, a) =>
            {
                a.Effects = a.Data.GetDataPresent(DataFormats.FileDrop)
                    ? DragDropEffects.Copy : DragDropEffects.None;
                a.Handled = true;
            };
        }

        // ---------------- 取图 ----------------

        private void PickFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择二维码图片",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(this) == true)
                LoadFromFile(dlg.FileName);
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;

            // 只认第一张能读出来的图片（拖多张时按顺序试）
            foreach (var f in files)
            {
                if (LoadFromFile(f)) return;
            }

            // 【2026-11-13】不是图片 → 再试一次"装着 data:image/...;base64 的文本文件"。
            // 用户拿到的内联图片常常是被存成 .txt / .html 的一段文本。
            foreach (var f in files)
            {
                string txt;
                if (TryReadInlineTextFile(f, out txt))
                {
                    LoadInlineImageText(txt, f);
                    return;
                }
            }

            SetStatus("拖进来的文件读不出图片（支持 PNG / JPG / BMP / GIF，"
                      + "或内含 data:image/...;base64 的文本文件）", error: true);
        }

        /// <summary>把文件当文本读，看里面是不是内联图片数据（.txt / .html 都能蒙对）。</summary>
        private static bool TryReadInlineTextFile(string path, out string text)
        {
            text = null;
            try
            {
                if (!File.Exists(path)) return false;
                var fi = new FileInfo(path);
                if (fi.Length <= 0 || fi.Length > 8L * 1024 * 1024) return false;   // 8MB 上限
                string t = File.ReadAllText(path);
                if (!QrDecoder.LooksLikeInlineImageText(t)) return false;
                text = t;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void TryPasteFromClipboard(bool silentIfEmpty)
        {
            try
            {
                if (Clipboard.ContainsImage())
                {
                    var src = Clipboard.GetImage();
                    if (src == null)
                    {
                        if (!silentIfEmpty) SetStatus("剪贴板里的图片读不出来。", error: true);
                        return;
                    }

                    // 转成 PNG 字节交给解码器（走 DecodeBytes 这条统一入口）
                    byte[] png;
                    using (var ms = new MemoryStream())
                    {
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(src));
                        enc.Save(ms);
                        png = ms.ToArray();
                    }

                    SetPending(png, null);
                    SetStatus("已读取剪贴板图片（" + src.PixelWidth + "×" + src.PixelHeight + "），正在自动识别…", error: false);
                    AutoDecode();
                    return;
                }

                // 【2026-11-13】剪贴板里没有图片 —— 看文本是不是「图片数据本身」：
                // data:image/png;base64,… 或纯 base64 图片。这是用户明确要补的路径。
                string txt = null;
                try
                {
                    if (Clipboard.ContainsText()) txt = Clipboard.GetText();
                }
                catch { /* 剪贴板被别的进程占着，按"没有文本"处理 */ }

                if (!string.IsNullOrEmpty(txt) && QrDecoder.LooksLikeInlineImageText(txt))
                {
                    LoadInlineImageText(txt, null);
                    return;
                }

                if (!silentIfEmpty)
                {
                    SetStatus("剪贴板里没有图片，也不是 data:image/...;base64 图片数据。"
                              + "请先截图/复制二维码图片，或复制那段 base64 文本。", error: true);
                }
            }
            catch (Exception ex)
            {
                if (!silentIfEmpty) SetStatus("读剪贴板失败：" + ex.Message, error: true);
            }
        }

        /// <summary>
        /// 把文本形式的内联图片（data URI / 纯 base64）变成待识别图片。
        /// <paramref name="sourceHint"/> 只用于状态文案（来自哪个文件）。
        /// </summary>
        private void LoadInlineImageText(string text, string sourceHint)
        {
            byte[] bytes;
            if (!QrDecoder.TryGetInlineImageBytes(text, out bytes))
            {
                SetStatus("这段文本看着像图片 base64，但解不出图片 —— 可能复制时被截断了，"
                          + "或不是 PNG / JPG / BMP / GIF。", error: true);
                return;
            }

            SetPending(bytes, null);
            SetStatus("已读取内联图片数据"
                      + (string.IsNullOrEmpty(sourceHint) ? "" : "（" + System.IO.Path.GetFileName(sourceHint) + "）")
                      + "（" + bytes.Length + " 字节），正在自动识别…", error: false);
            AutoDecode();
        }

        private bool LoadFromFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                using (var ms = new MemoryStream(bytes))
                {
                    // ⚠️ 完整限定 System.Drawing.Bitmap：本文件已用别名绑走 WPF 的 Image，
                    //    不 using System.Drawing（否则 Image 又会撞名）。
                    var bmp = new System.Drawing.Bitmap(ms);   // 先探一下能不能解码
                    int w = bmp.Width, h = bmp.Height;
                    bmp.Dispose();
                    SetPending(bytes, path);
                    SetStatus("已选择：" + Path.GetFileName(path) + "（" + w + "×" + h + "），正在自动识别…", error: false);
                    AutoDecode();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private void SetPending(byte[] bytes, string path)
        {
            _pendingBytes = bytes;
            _pendingPath = path;
            _decodeBtn.IsEnabled = !_decoding;
            ShowPreview(bytes, path);
        }

        /// <summary>
        /// 【★ 2026-11-13 用户要求「添加二维码 自动识别功能」】读入图片后立刻在后台识别，
        /// 不再需要手动点「识别」/按回车。按钮退化成"自动识别失败后手动重试"的兜底。
        /// </summary>
        private void AutoDecode()
        {
            if (_decoding || _pendingBytes == null) return;
            DecodeNow();
        }

        private void ShowPreview(byte[] bytes, string path)
        {
            try
            {
                BitmapImage bi;
                if (bytes != null)
                {
                    bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.StreamSource = new MemoryStream(bytes);
                    bi.EndInit();
                    bi.Freeze();
                }
                else
                {
                    bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.UriSource = new Uri(path);
                    bi.EndInit();
                    bi.Freeze();
                }
                _imgPreviewBox.Source = bi;
            }
            catch
            {
                // 预览失败不影响解码
            }
        }

        // ---------------- 解码 ----------------

        private void DecodeNow()
        {
            if (_pendingBytes == null)
            {
                SetStatus("还没有选图片。", error: true);
                return;
            }

            if (_decoding) return;   // 自动识别与手动点击可能同时发生，只跑一份
            _decoding = true;

            _decodeBtn.IsEnabled = false;
            SetStatus("正在识别…", error: false);

            byte[] bytes = _pendingBytes;
            string path = _pendingPath;

            // 解码要跑多轮图像变换（可能上百毫秒），放到线程池，别卡住窗口
            Task.Run(() =>
            {
                QrDecoder.Result r = path != null
                    ? QrDecoder.DecodeFile(path)
                    : QrDecoder.DecodeBytes(bytes);
                return r;
            }).ContinueWith(t =>
            {
                _decoding = false;

                if (t.IsFaulted)
                {
                    SetStatus("识别出错：" + (t.Exception?.GetBaseException().Message ?? "未知错误"), error: true);
                    _decodeBtn.Content = "重新识别";
                    _decodeBtn.IsEnabled = true;
                    return;
                }

                var r = t.Result;
                if (r == null)
                {
                    SetStatus("识别失败。", error: true);
                    _decodeBtn.Content = "重新识别";
                    _decodeBtn.IsEnabled = true;
                    return;
                }

                if (!r.Ok)
                {
                    SetStatus(r.Error ?? "没识别到二维码。", error: true);
                    _decodeBtn.Content = "重新识别";
                    _decodeBtn.IsEnabled = true;
                    return;
                }

                // 成功
                _log?.Invoke("二维码识别成功（" + r.Format + "，" + r.Attempt + "）：" + r.Text);
                var cb = _onDecoded;
                Close();
                // 先关窗再回调，避免回调里弹的对话框被本窗口挡在后面
                cb?.Invoke(r.Text);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void SetStatus(string text, bool error)
        {
            _statusText.Text = text;
            _statusText.Visibility = Visibility.Visible;
            // SetResourceReference：跟着主题走（深色下 DangerBrush 是亮红 #EF4444，
            // 硬编码 Firebrick 在深底上几乎看不见）。
            _statusText.SetResourceReference(
                System.Windows.Controls.TextBlock.ForegroundProperty,
                error ? "DangerBrush" : "TextSecondaryBrush");
        }
    }
}
