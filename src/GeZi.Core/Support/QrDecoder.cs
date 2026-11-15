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
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using ZXing;
using ZXing.Common;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 二维码/条码解码器（基于 ZXing.Net，Apache-2.0，本地固化引用见 lib/ZXing/）。
    ///
    /// 【为什么用库而不是自己写】<see cref="QrEncoder"/> 那种编码器只要把数据排进矩阵，
    /// 而解码反着来：要先在杂乱图像里找出定位图形、做透视校正、按模块采样，
    /// 最后跑 Reed-Solomon 纠错 —— 现实中的截图/照片还带缩放、JPEG 噪点、背景干扰。
    /// 这部分用成熟库远比自写可靠。
    ///
    /// 【设计要点】现实图片千奇百怪，单次尝试失败率偏高，所以这里做**多轮渐进尝试**：
    ///   1) 原图直解（清晰截图命中率最高，也最快）
    ///   2) 放大（小图/被缩小的图，模块太小会丢）
    ///   3) 灰度 + 提高对比度（低对比、带色调的截图）
    ///   4) 灰度 + 二值化（噪点、渐变背景）
    ///   5) 反转色（深浅色主题下的"反色二维码"）
    ///   6) 中央裁剪重试（二维码只占图片一小块时，裁掉无关区域减少干扰）
    /// 任一命中即返回，避免无谓开销。
    /// </summary>
    public static class QrDecoder
    {
        /// <summary>解码结果。</summary>
        public sealed class Result
        {
            /// <summary>是否成功解出内容。</summary>
            public bool Ok;

            /// <summary>解出的文本（含非链接内容，交给上层判断）。</summary>
            public string Text;

            /// <summary>识别到的码制（如 QR_CODE、CODE_128）。</summary>
            public string Format;

            /// <summary>失败原因（成功时为空）。</summary>
            public string Error;

            /// <summary>命中的尝试轮次名（排查用）。</summary>
            public string Attempt;
        }

        /// <summary>允许的码制。默认收窄到场景相关的两类，避免把菜谱、产品条码等误识成结果。</summary>
        private static readonly List<BarcodeFormat> Formats = new List<BarcodeFormat>
        {
            BarcodeFormat.QR_CODE,
            BarcodeFormat.CODE_128,   // 少数分享会以条码图给出
        };

        /// <summary>尝试的放大倍数（原图之外的额外轮次）。</summary>
        private const int UpscaleFactor = 2;

        /// <summary>图片像素总量上限（超过则先等比缩小，避免大图爆内存/超时）。</summary>
        private const long MaxPixels = 32L * 1024 * 1024;   // 约 3200 万像素

        /// <summary>按最大边限制的尺寸（超大图缩到这个量级再解）。</summary>
        private const int MaxSide = 4000;

        /// <summary>
        /// 从图片文件解码。支持 png / jpg / bmp / gif 等 GDI+ 能读的格式。
        /// </summary>
        public static Result DecodeFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return Fail("路径为空");
            if (!File.Exists(path))
                return Fail("文件不存在：" + path);

            Bitmap bmp = null;
            try
            {
                // 不用 Image.FromFile —— 那样会一直占着文件句柄，
                // 从流加载可以让本方法返回后立刻释放（用户可能马上删/移这张图）。
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bmp = new Bitmap(fs);
                }
            }
            catch (Exception ex)
            {
                return Fail("图片读取失败：" + ex.Message);
            }

            using (bmp)
            {
                return Decode(bmp);
            }
        }

        /// <summary>
        /// 从内存字节解码（贴剪贴板图片用；剪贴板拿到的是 DIB 字节，没有路径）。
        /// </summary>
        public static Result DecodeBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return Fail("图片数据为空");

            try
            {
                using (var ms = new MemoryStream(bytes, writable: false))
                using (var bmp = new Bitmap(ms))
                {
                    return Decode(bmp);
                }
            }
            catch (Exception ex)
            {
                return Fail("图片解析失败：" + ex.Message);
            }
        }

        // ============================================================
        // 文本形式的图片（2026-11-13 新增）
        // ============================================================

        /// <summary>
        /// 【用户需求 2026-11-13】原话：「就是类似 这种 data:image/png;base64,iVBORw0KGg
        /// 只是图片链接 需要转成网址」。
        ///
        /// <para>【为什么要它】网页、聊天工具、剪贴板里拿到的二维码，经常不是图片文件、
        /// 也不是图片 URL，而是**整张图编成 base64 的文本**（<c>data:image/png;base64,…</c>）。
        /// 原来的三条取图路径（剪贴板图片 / 选文件 / 拖文件）全都进不来 ——
        /// 用户粘贴这串东西只会看到一坨乱码塞进输入框。这里补上"文本 → 图片字节"这一环，
        /// 之后照旧走 <see cref="DecodeBytes"/> 识别，拿到里面的网址。</para>
        /// </summary>
        public static Result DecodeInlineImage(string text)
        {
            byte[] bytes;
            if (!TryGetInlineImageBytes(text, out bytes))
            {
                return Fail("这段内容不是图片数据（既不是 data:image/...;base64，"
                            + "也不是 base64 编码的图片）。如果它是普通网页链接，直接粘贴链接即可。");
            }

            Result r = DecodeBytes(bytes);
            if (r != null && r.Ok && string.IsNullOrEmpty(r.Attempt))
                r.Attempt = "内联图片";
            return r;
        }

        /// <summary>
        /// 廉价判断「这段文本是否长得像内联图片」——**不做** base64/图像解码，UI 线程可直接调。
        ///
        /// <para>只用来决定"要不要把这次粘贴/拖入拦下来交给后台处理"。
        /// 真正能不能解出图片以 <see cref="TryGetInlineImageBytes"/> 为准。</para>
        ///
        /// <para>【为什么纯 base64 要认魔数】不带 <c>data:</c> 前缀时，一长串普通英文/代码
        /// 也可能全落在 base64 字符集里。所以这里要求它**以图片的 base64 魔数开头**
        /// （PNG 的 iVBORw0KGgo、JPEG 的 /9j/ 等），误判率几乎为零。</para>
        /// </summary>
        public static bool LooksLikeInlineImageText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            string s = StripWhitespace(text);

            if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                // data:image/png;base64,xxxx —— 声明里必须写明 base64
                if (s.IndexOf("base64", StringComparison.OrdinalIgnoreCase) < 0) return false;
                int comma = s.IndexOf(',');
                return comma > 0 && s.Length - comma - 1 >= MinInlinePayloadChars;
            }

            string run = LongestBase64Run(s);
            if (run == null) return false;
            return StartsWithImageMagic(run);
        }

        /// <summary>
        /// 从一段文本里抠出图片字节（data URI 或纯 base64 都支持）。
        /// 失败返回 false，不抛异常 —— 调用方（粘贴/拖拽入口）绝不能因此崩。
        /// </summary>
        public static bool TryGetInlineImageBytes(string text, out byte[] bytes)
        {
            bytes = null;
            string payload = ExtractBase64Payload(text);
            if (payload == null) return false;

            try
            {
                bytes = Convert.FromBase64String(payload);
            }
            catch (System.FormatException)   // ⚠️ 必须写全名：ZXing 也有一个 FormatException
            {
                bytes = null;
                return false;
            }

            if (bytes.Length < 64)
            {
                bytes = null;
                return false;
            }

            // 最后一道确认：GDI+ 真能把它读成图才算数。
            // 不做这一步的话，一坨"恰好是合法 base64"的乱码会被当成图片，
            // 后面 DecodeBytes 报的错就叫"图片解析失败"，用户会一头雾水。
            try
            {
                using (var ms = new MemoryStream(bytes, writable: false))
                using (var probe = new Bitmap(ms))
                {
                    if (probe.Width <= 0 || probe.Height <= 0)
                    {
                        bytes = null;
                        return false;
                    }
                }
            }
            catch
            {
                bytes = null;
                return false;
            }

            return true;
        }

        /// <summary>内联图片至少要这么长的 base64 段 —— 再短不可能是一张能扫出来的二维码。</summary>
        private const int MinInlinePayloadChars = 200;

        /// <summary>
        /// 图片 base64 的开头特征（用来确认"这是图不是按钮一长串文本"）：
        /// PNG = iVBORw0KGgo，JPEG = /9j/，GIF = R0lGOD，WebP = UklGR，ICO = AAABAA。
        /// </summary>
        private static readonly string[] ImageBase64Heads =
        {
            "iVBORw0KGgo",
            "/9j/",
            "R0lGOD",
            "UklGR",
            "AAABAA",
        };

        private static bool StartsWithImageMagic(string run)
        {
            foreach (var head in ImageBase64Heads)
            {
                if (run.StartsWith(head, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>去掉所有空白字符（base64 常被折行，复制时也常带空格/换行）。</summary>
        private static string StripWhitespace(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static bool IsBase64Char(char c)
        {
            return (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c == '+' || c == '/' || c == '=' || c == '-' || c == '_';
        }

        /// <summary>
        /// 取文本中最长的 base64 连续段。
        /// 【为什么不直接整段当 base64】粘贴内容常带前后缀（引号、"data:image/png;base64,"、
        /// 末尾的说明文字/"提取码:abcd"），整段解必然失败；按段取才稳。
        /// </summary>
        private static string LongestBase64Run(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;

            string best = null;
            int start = -1;
            for (int i = 0; i <= s.Length; i++)
            {
                bool ok = i < s.Length && IsBase64Char(s[i]);
                if (ok)
                {
                    if (start < 0) start = i;
                }
                else if (start >= 0)
                {
                    int len = i - start;
                    if (len >= MinInlinePayloadChars && (best == null || len > best.Length))
                        best = s.Substring(start, len);
                    start = -1;
                }
            }
            return best;
        }

        /// <summary>
        /// 从文本里抽出可用的 base64 载荷（已去空白、归一化 URL-safe 字符、补齐 padding）。
        /// 返回 null 表示"这段文本里没有像图片 base64 的东西"。
        /// </summary>
        private static string ExtractBase64Payload(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            string s = StripWhitespace(text);

            if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = s.IndexOf(',');
                if (comma <= 0) return null;

                string meta = s.Substring(0, comma);
                // 只支持 base64 形态的 data URI；URL 编码形态（data:image/svg+xml,<svg…>）
                // 不是位图，也没有二维码可解，直接不认。
                if (meta.IndexOf("base64", StringComparison.OrdinalIgnoreCase) < 0) return null;

                s = s.Substring(comma + 1);

                // 网页里拷出来时可能带 URL 编码（%2B、%2F、%3D）
                if (s.IndexOf('%') >= 0)
                {
                    try { s = Uri.UnescapeDataString(s); } catch { /* 还原失败就按原样试 */ }
                }
            }

            string run = LongestBase64Run(s);
            if (run == null) return null;

            // base64url（- 和 _）统一回标准字母表，否则 Convert.FromBase64String 会拒收
            run = run.Replace('-', '+').Replace('_', '/');

            int pad = run.Length % 4;
            if (pad == 1) return null;                       // 长度 4n+1 不可能是合法 base64
            if (pad > 0) run += new string('=', 4 - pad);    // 补齐 padding（很多来源会省掉'='）

            return run;
        }

        /// <summary>从已加载的 Bitmap 解码。</summary>
        public static Result Decode(Bitmap source)
        {
            if (source == null || source.Width <= 0 || source.Height <= 0)
                return Fail("图片无效");

            try
            {
                using (var work = Normalize(source))
                {
                    // ---- 第 1 轮：原图 ----
                    var r = TryDecode(work, "原图");
                    if (r != null) return r;

                    // ---- 第 2 轮：放大 ----
                    using (var up = Resize(work, work.Width * UpscaleFactor, work.Height * UpscaleFactor))
                    {
                        r = TryDecode(up, "放大 " + UpscaleFactor + "×");
                        if (r != null) return r;
                    }

                    // ---- 第 3 轮：灰度增强对比 ----
                    using (var gray = ToGray(work, contrast: true))
                    {
                        r = TryDecode(gray, "灰度增强");
                        if (r != null) return r;

                        // ---- 第 4 轮：灰度 + 二值化（Otus 阈值） ----
                        using (var bw = Binarize(gray))
                        {
                            r = TryDecode(bw, "二值化");
                            if (r != null) return r;
                        }
                    }

                    // ---- 第 5 轮：反转色（深色主题截图里的"反色二维码"） ----
                    using (var inv = Invert(work))
                    {
                        r = TryDecode(inv, "反色");
                        if (r != null) return r;
                    }

                    // ---- 第 6 轮：中央裁剪（码只占画面一小块） ----
                    foreach (int keepPct in new[] { 80, 60, 40 })
                    {
                        using (var crop = CenterCrop(work, keepPct))
                        {
                            if (crop == null) continue;
                            r = TryDecode(crop, "中央裁剪 " + keepPct + "%");
                            if (r != null) return r;
                        }
                    }
                }

                return Fail("没识别到二维码。试试换一张更清晰、二维码更完整的图。");
            }
            catch (Exception ex)
            {
                return Fail("解码出错：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        // ---------------- 内部实现 ----------------

        /// <summary>单次解码尝试；失败返回 null（不抛异常，避免噪声图打断整条链）。</summary>
        private static Result TryDecode(Bitmap bmp, string attempt)
        {
            try
            {
                var reader = new BarcodeReader
                {
                    // 只认需要的码制：范围越窄，误识别概率越低、速度越快
                    Options = new DecodingOptions
                    {
                        PossibleFormats = Formats,
                        // 二维码在照片里常有倾斜；开这两项让定位图形更易被找到
                        TryHarder = true,
                        TryInverted = false,   // 反色我们自己单独做一轮（更可控）
                    },
                    AutoRotate = true,
                };

                var res = reader.Decode(bmp);
                if (res == null || string.IsNullOrEmpty(res.Text)) return null;

                return new Result
                {
                    Ok = true,
                    Text = res.Text,
                    Format = res.BarcodeFormat.ToString(),
                    Attempt = attempt,
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 归一化：超大图等比缩小（避免内存与耗时失控），并把像素格式统一成 32bppArgb，
        /// 让后续所有变换都在同一种格式上跑（某些格式的 LockBits 会抛异常）。
        /// </summary>
        private static Bitmap Normalize(Bitmap src)
        {
            int w = src.Width, h = src.Height;

            // 图像太大 → 等比缩到 MaxSide 以内且总像素不超 MaxPixels
            double scale = 1.0;
            if (w > MaxSide || h > MaxSide)
                scale = Math.Min((double)MaxSide / w, (double)MaxSide / h);
            if ((long)(w * scale) * (long)(h * scale) > MaxPixels)
                scale = Math.Sqrt((double)MaxPixels / ((long)w * h));

            Bitmap bmp = scale < 1.0
                ? Resize(src, Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)))
                : new Bitmap(src);

            // 统一格式
            if (bmp.PixelFormat != PixelFormat.Format32bppArgb)
            {
                var converted = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(converted))
                {
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                }
                bmp.Dispose();
                bmp = converted;
            }
            return bmp;
        }

        private static Bitmap Resize(Bitmap src, int w, int h)
        {
            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                // 放大时用 NearestNeighbor 对 QR 更友好（保持模块边界锐利），
                // 但 ZXing 自带采样容错，这里统一用高质量插值，避免锯齿反而干扰定位。
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            return dst;
        }

        /// <summary>转灰度；contrast=true 时把对比度拉开（用逐像素 lut 映射，快且无副作用）。</summary>
        private static Bitmap ToGray(Bitmap src, bool contrast)
        {
            var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                // 用 ColorMatrix 做灰度 + 对比度，比逐像素快得多
                var cm = new ColorMatrix(new[]
                {
                    new[] { .299f, .299f, .299f, 0f, 0f },
                    new[] { .587f, .587f, .587f, 0f, 0f },
                    new[] { .114f, .114f, .114f, 0f, 0f },
                    new[] { 0f,    0f,    0f,    1f, 0f },
                    new[] { 0f,    0f,    0f,    0f, 1f },
                });

                if (contrast)
                {
                    // 围绕 0.5 提升对比度：1.6 倍是实测对扫描件/截图较稳的值
                    const float c = 1.6f, t = (1f - c) / 2f;
                    var boost = new ColorMatrix(new[]
                    {
                        new[] { c, 0f, 0f, 0f, 0f },
                        new[] { 0f, c, 0f, 0f, 0f },
                        new[] { 0f, 0f, c, 0f, 0f },
                        new[] { 0f, 0f, 0f, 1f, 0f },
                        new[] { t, t, t, 0f, 1f },
                    });
                    cm = Multiply(boost, cm);
                }

                using (var ia = new ImageAttributes())
                {
                    ia.SetColorMatrix(cm);
                    g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height),
                        0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                }
            }
            return dst;
        }

        /// <summary>矩阵相乘（先应用 a 再应用 b 的合成；GDI+ 的约定：结果 = a × b）。</summary>
        private static ColorMatrix Multiply(ColorMatrix a, ColorMatrix b)
        {
            var r = new float[5][];
            for (int i = 0; i < 5; i++) r[i] = new float[5];
            for (int i = 0; i < 5; i++)
                for (int j = 0; j < 5; j++)
                {
                    float s = 0f;
                    for (int k = 0; k < 5; k++) s += a[i, k] * b[k, j];
                    r[i][j] = s;
                }
            return new ColorMatrix(r);
        }

        /// <summary>Otsu 大津法自动阈值二值化（比固定阈值稳，能适应不同亮度）。</summary>
        private static Bitmap Binarize(Bitmap gray)
        {
            int w = gray.Width, h = gray.Height;
            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);

            var rect = new Rectangle(0, 0, w, h);
            var sd = gray.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var dd = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            try
            {
                int stride = sd.Stride;
                byte[] sb = new byte[stride * h];
                System.Runtime.InteropServices.Marshal.Copy(sd.Scan0, sb, 0, sb.Length);

                // 灰度直方图（取 B 通道即可，前面已转灰度，三通道相等）
                var hist = new int[256];
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++) hist[sb[row + x * 4]]++;
                }

                int threshold = OtsuThreshold(hist, w * h);

                byte[] db = new byte[stride * h];
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        byte v = sb[row + x * 4] >= threshold ? (byte)255 : (byte)0;
                        int o = row + x * 4;
                        db[o] = v; db[o + 1] = v; db[o + 2] = v; db[o + 3] = 255;
                    }
                }
                System.Runtime.InteropServices.Marshal.Copy(db, 0, dd.Scan0, db.Length);
            }
            finally
            {
                gray.UnlockBits(sd);
                dst.UnlockBits(dd);
            }
            return dst;
        }

        private static int OtsuThreshold(int[] hist, int total)
        {
            double sum = 0;
            for (int i = 0; i < 256; i++) sum += (double)i * hist[i];

            double sumB = 0, maxVar = -1;
            int wB = 0, best = 127;
            for (int t = 0; t < 256; t++)
            {
                wB += hist[t];
                if (wB == 0) continue;
                int wF = total - wB;
                if (wF == 0) break;

                sumB += (double)t * hist[t];
                double mB = sumB / wB;
                double mF = (sum - sumB) / wF;
                double between = (double)wB * wF * (mB - mF) * (mB - mF);
                if (between > maxVar) { maxVar = between; best = t; }
            }
            return best;
        }

        /// <summary>RGB 反转（处理"反色二维码"）。</summary>
        private static Bitmap Invert(Bitmap src)
        {
            var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                var cm = new ColorMatrix(new[]
                {
                    new[] { -1f,  0f,  0f, 0f, 0f },
                    new[] {  0f, -1f,  0f, 0f, 0f },
                    new[] {  0f,  0f, -1f, 0f, 0f },
                    new[] {  0f,  0f,  0f, 1f, 0f },
                    new[] {  1f,  1f,  1f, 0f, 1f },
                });
                using (var ia = new ImageAttributes())
                {
                    ia.SetColorMatrix(cm);
                    g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height),
                        0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                }
            }
            return dst;
        }

        /// <summary>按百分比取中央区域（<paramref name="keepPct"/> = 保留边长百分比）。</summary>
        private static Bitmap CenterCrop(Bitmap src, int keepPct)
        {
            int cw = src.Width * keepPct / 100;
            int ch = src.Height * keepPct / 100;
            if (cw < 32 || ch < 32) return null;             // 太小没意义
            if (cw >= src.Width && ch >= src.Height) return null;

            int x = (src.Width - cw) / 2;
            int y = (src.Height - ch) / 2;
            var dst = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.DrawImage(src, new Rectangle(0, 0, cw, ch),
                    new Rectangle(x, y, cw, ch), GraphicsUnit.Pixel);
            }
            return dst;
        }

        private static Result Fail(string msg)
        {
            return new Result { Ok = false, Error = msg };
        }
    }
}
