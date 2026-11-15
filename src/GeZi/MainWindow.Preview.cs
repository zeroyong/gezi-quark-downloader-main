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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace GeZi
{
    /// <summary>
    /// MainWindow 的**文件预览 / 缩略图 / 行内更多操作**分区（2026-11-15 P0）。
    ///
    /// <para>三个职责，都只服务于文件列表（分享模式与「我的网盘」共用）：
    /// ① 缩略图服务 —— 图片/视频行左侧直接渲染网盘 <c>thumbnail</c> 字段；
    /// ② 「…」更多操作按钮的弹出菜单（替代原右侧大小列）；
    /// ③ 单击预览 —— 用户定稿（原话）：「对于图片和txt 直接点击 就是预览即可，
    ///    只在s多选模式下才是选中」。接线在 OnListPreviewMouseDown 第④分支。</para>
    ///
    /// <para>【为什么预览走免转存取链】预览只是"看一眼"，往用户网盘写中转文件
    /// 既慢又留垃圾 —— 统一 <c>forceNoSave:true</c>。一次性拉取没有续传需求，
    /// 免转存"断后无法续传"的短板在这里不成立。</para>
    /// </summary>
    public partial class MainWindow
    {
        // ====================================================================
        //  一、缩略图服务
        // ====================================================================

        /// <summary>
        /// 缩略图/预览共用的 HttpClient。UseCookies=false —— 手写 Cookie 头，
        /// 不让 .NET 的 CookieContainer 按域过滤掉直链域的登录态
        ///（与下载引擎 DisableCookieJar 同一个理由，见 DownloadContext 注释）。
        /// </summary>
        private static readonly HttpClient PreviewHttp = new HttpClient(new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
        })
        {
            Timeout = TimeSpan.FromSeconds(45),
        };

        /// <summary>缩略图内存缓存：key = 当前平台 id + fid。DecodePixelWidth 已压到 64px，一张 ~16KB。</summary>
        private readonly Dictionary<string, BitmapImage> _thumbCache = new Dictionary<string, BitmapImage>();

        /// <summary>正在拉的 fid（并发去重：同一目录快速刷新时不会重复请求）。</summary>
        private readonly HashSet<string> _thumbInFlight = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>缓存上限。超过就整体清空重来（缩略图是廉价小图，不值得做 LRU）。</summary>
        private const int ThumbCacheCap = 400;

        /// <summary>
        /// 异步拉取一行文件的缩略图并回填 <see cref="ShareFileItem.Thumbnail"/>。
        /// 失败**静默**（保持类型图标）—— 缩略图是锦上添花，弹错误提示反而打扰。
        /// </summary>
        private async void KickThumbnail(ShareFileItem it)
        {
            if (it == null || it.IsDir) return;
            if (string.IsNullOrEmpty(it.ThumbnailUrl)) return;
            if (it.HasThumbnail) return;

            string key = (CurrentProviderId() ?? "") + "|" + (it.Fid ?? "");
            lock (_thumbCache)
            {
                if (_thumbCache.ContainsKey(key))
                {
                    var hit = _thumbCache[key];
                    if (it.Thumbnail != hit) it.Thumbnail = hit;
                    return;
                }
                if (!_thumbInFlight.Add(key)) return; // 已有同 fid 的请求在跑
            }

            try
            {
                var img = await LoadThumbnailAsync(it.ThumbnailUrl).ConfigureAwait(true);
                if (img == null) return;

                lock (_thumbCache)
                {
                    if (_thumbCache.Count >= ThumbCacheCap) _thumbCache.Clear();
                    _thumbCache[key] = img;
                }
                // 行可能已被虚拟化回收 / 用户切了目录 —— 只回填还挂在列表里的同一实例。
                it.Thumbnail = img;
            }
            catch
            {
                // 静默：缩略图拉不到就继续显示类型图标（wenxi 的 errorBuilder 同策略）。
            }
            finally
            {
                lock (_thumbCache) _thumbInFlight.Remove(key);
            }
        }

        /// <summary>缩略图并发闸门 —— 翻一页可能一口气进来 100 个媒体文件，
        /// 100 路并发请求 CDN 很容易触发限流；排队 6 路，稳。</summary>
        private static readonly System.Threading.SemaphoreSlim _thumbGate =
            new System.Threading.SemaphoreSlim(6, 6);

        /// <summary>下载缩略图字节并解码成 64px 小图（Freeze 后可跨线程回填）。</summary>
        private async Task<BitmapImage> LoadThumbnailAsync(string url)
        {
            // wenxi 的 CloudThumbnail 同款处理：列表接口给的是 "//$host/…" 协议相对地址
            var requestUri = url.StartsWith("//", StringComparison.Ordinal)
                ? "https:" + url
                : url;

            await _thumbGate.WaitAsync().ConfigureAwait(true);
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, requestUri))
                {
                    // 缩略图端点校验 Referer + 常规 UA（wenxi 用 'Mozilla/5.0' 短 UA），
                    // 登录态 Cookie 兜底 —— 部分缩略图域校验账号。
                    var ctx = BuildDownloadContext();
                    req.Headers.TryAddWithoutValidation("User-Agent", ctx.UserAgent);
                    req.Headers.TryAddWithoutValidation("Referer", ctx.Referer);
                    req.Headers.TryAddWithoutValidation("Origin", ctx.Origin);
                    if (!string.IsNullOrEmpty(ctx.Cookie))
                        req.Headers.TryAddWithoutValidation("Cookie", ctx.Cookie);

                    using (var resp = await PreviewHttp.SendAsync(req).ConfigureAwait(true))
                    {
                        resp.EnsureSuccessStatusCode();
                        var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(true);
                        return DecodeThumb(bytes);
                    }
                }
            }
            finally
            {
                _thumbGate.Release();
            }
        }

        private static BitmapImage DecodeThumb(byte[] bytes)
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;   // 流读完即释放，不留文件句柄
            img.DecodePixelWidth = 64;                    // 列表 34px 显示，64 足够（1x~2x DPI）
            img.StreamSource = new System.IO.MemoryStream(bytes);
            img.EndInit();
            img.Freeze();                                 // 跨线程回填 UI 必需
            return img;
        }

        // ====================================================================
        //  二、「…」行内更多操作菜单
        // ====================================================================
        //  【2026-11-15 晚间已删】用户实测「大小 还是移到右侧吧 去掉三个点
        //  不然看起来眼睛好累」→ 行内「···」按钮整体下线，大小回右侧列。
        //  OnFileRowMoreClick / MakeMenuItem / 行模板按钮一并移除；
        //  原有操作（下载/免转存/获取直链/删除/预览）全部仍在**右键菜单**里，
        //  预览还能直接单击图片/文本行触发。此分区仅留注释占位。
        // ====================================================================

        // ====================================================================
        //  三、单击预览（图片 / 文本）
        // ====================================================================

        // ---------------- 预览字节缓存 + 相邻预加载（2026-11-15 用户反馈） ----------------
        // 「左右切换图片太慢了，打开图片后 就预先加载后面两张 减少卡顿」。
        // 切换慢的根源：每次 A/D 都要完整走「取直链 → 拉字节」两跳网络。
        // 方案：字节按 fid 缓存（字节本身不会过期，直链过期也无所谓 ——
        // 缓存的是拉回来的内容），开窗/切换后后台预拉前后各 2 张。

        /// <summary>预览字节缓存：fid → 原始字节。软上限超了就整体清空（预加载窗口 ±2 只需 ~5 项）。</summary>
        private readonly Dictionary<string, byte[]> _previewCache = new Dictionary<string, byte[]>();

        /// <summary>预加载并发闸门：后台预拉与当前加载共用带宽，2 路足够且不抢主请求。</summary>
        private readonly SemaphoreSlim _preloadGate = new SemaphoreSlim(2, 2);

        private const int PreviewCacheCap = 10;

        /// <summary>预加载 focus 前后各 2 张（fire-and-forget，失败静默 —— 预加载是锦上添花）。</summary>
        private void KickPreload(List<ShareFileItem> seq, int focusIdx)
        {
            TryPreloadOne(seq, focusIdx + 1);
            TryPreloadOne(seq, focusIdx + 2);
            TryPreloadOne(seq, focusIdx - 1);
            TryPreloadOne(seq, focusIdx - 2);
        }

        private async void TryPreloadOne(List<ShareFileItem> seq, int i)
        {
            if (i < 0 || i >= seq.Count) return;
            var it = seq[i];
            if (it == null || it.IsDir || string.IsNullOrEmpty(it.Fid)) return;
            lock (_previewCache)
            {
                if (_previewCache.ContainsKey(it.Fid)) return;   // 已有，零成本
            }
            var kind = ShareFileItem.PreviewKindOf(it.Name);
            if (kind == ShareFileItem.PreviewKind.None) return;

            await _preloadGate.WaitAsync().ConfigureAwait(true);
            try
            {
                await MakePreviewLoader(it, kind)(CancellationToken.None).ConfigureAwait(true);
                // 结果已由 loader 写入 _previewCache；异常吞掉 —— 预加载失败不影响任何主流程
            }
            catch { /* 静默 */ }
            finally
            {
                _preloadGate.Release();
            }
        }

        // ---------------- 预览主流程 ----------------

        /// <summary>
        /// 打开单文件预览窗。流程：开窗（loading 态）→ 免转存取直链 →
        /// 带下载身份头拉前 N 字节 → 窗内渲染。取链/下载任何一步失败都**在窗内**
        /// 显示错误 + 「另存为」出口（窗已开，不能再用提示条）。
        ///
        /// <para>【A/D 切换】2026-11-15 用户定稿：预览窗里 A/D（图片态另有 ←/→）
        /// 切换前/后一个可预览文件。导航序列 = 当前列表（<see cref="_files"/>）里
        /// 全部可预览文件的显示顺序，zip/docx 等不可预览项直接跳过。
        /// 【预加载】开窗后后台预拉前后各 2 张 —— 切换时命中缓存近乎秒开。</para>
        /// </summary>
        private async Task OpenPreviewAsync(ShareFileItem item)
        {
            if (item == null || item.IsDir) return;
            if (ShareFileItem.PreviewKindOf(item.Name) == ShareFileItem.PreviewKind.None) return;

            // 可预览序列（按列表显示顺序）；起点不在序列里（异常兜底）就挂到末尾
            var seq = new List<ShareFileItem>();
            foreach (var f in _files)
                if (!f.IsDir && ShareFileItem.PreviewKindOf(f.Name) != ShareFileItem.PreviewKind.None)
                    seq.Add(f);
            int idx = seq.IndexOf(item);
            if (idx < 0) { seq.Add(item); idx = seq.Count - 1; }

            var win = new PreviewWindow { Owner = this };
            win.NavigateDelta = delta =>
            {
                int ni = idx + delta;
                if (ni < 0 || ni >= seq.Count) return;   // 到头就停（箭头已隐藏，键盘兜底也不越界）
                idx = ni;
                var target = seq[idx];
                var kind = ShareFileItem.PreviewKindOf(target.Name);
                win.SetItem(target.Name, target.Size, kind, MakePreviewLoader(target, kind),
                    target.UpdatedAtText, idx, seq.Count);
                KickPreload(seq, idx);                    // 每次切换后重新预拉前后各 2 张
            };

            var firstKind = ShareFileItem.PreviewKindOf(item.Name);
            win.SetItem(item.Name, item.Size, firstKind, MakePreviewLoader(item, firstKind),
                item.UpdatedAtText, idx, seq.Count);
            KickPreload(seq, idx);                        // 开窗就预拉
            win.ShowDialog();
        }

        /// <summary>
        /// 构造单文件预览的 loader：**先查字节缓存**（命中即秒开）→ 未命中走
        /// 免转存取直链 → 带下载身份头拉前 N 字节 → 写入缓存。
        /// 图片 ≤32MB、文本 ≤256KB —— 预览不是下载，够用且省流量。
        /// </summary>
        private Func<CancellationToken, Task<byte[]>> MakePreviewLoader(
            ShareFileItem item, ShareFileItem.PreviewKind kind)
        {
            return async ct =>
            {
                // ⓪ 缓存命中：直接返回（A/D 往回切/重复打开同一文件都是零网络）
                byte[] cached;
                lock (_previewCache)
                {
                    _previewCache.TryGetValue(item.Fid ?? "", out cached);
                }
                if (cached != null) return cached;

                // ① 取直链（免转存：不往网盘写中转文件；我的网盘模式本来就直取）
                List<DownloadItem> links;
                try
                {
                    links = await GetDownloadItemsAsync(new List<ShareFileItem> { item }, true);
                }
                catch (Exception ex)
                {
                    throw new Exception("取直链失败：" + ex.Message);
                }
                var link = links != null && links.Count > 0 ? links[0] : null;
                if (link == null || string.IsNullOrEmpty(link.Url))
                    throw new Exception("没有取到可用直链（分享可能已失效或限制了游客下载）");

                // ② 拉字节
                long cap = kind == ShareFileItem.PreviewKind.Image
                    ? 32L * 1024 * 1024
                    : 256L * 1024;
                var bytes = await FetchPreviewBytesAsync(link.Url, cap, ct).ConfigureAwait(true);

                // ③ 写缓存（超上限整体清空 —— 预加载窗口 ±2 只需要 ~5 项，不值得 LRU）
                lock (_previewCache)
                {
                    if (_previewCache.Count >= PreviewCacheCap) _previewCache.Clear();
                    _previewCache[item.Fid ?? ""] = bytes;
                }
                return bytes;
            };
        }

        /// <summary>
        /// 带下载身份头拉取文件前 cap 字节。头与下载引擎同源
        ///（<see cref="BuildDownloadContext"/>：UA/Referer/Origin + 手写 Cookie 头），
        /// 夸克直链缺 Cookie 是 412，缺 Referer 会被部分 CDN 拒。
        /// </summary>
        private async Task<byte[]> FetchPreviewBytesAsync(string url, long cap, CancellationToken ct)
        {
            var ctx = BuildDownloadContext();
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                var headers = ctx.BuildBaseHeaders();
                foreach (var kv in headers)
                    req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                if (!string.IsNullOrEmpty(ctx.Cookie))
                    req.Headers.TryAddWithoutValidation("Cookie", ctx.Cookie);
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, cap - 1);

                using (var resp = await PreviewHttp.SendAsync(req, ct).ConfigureAwait(true))
                {
                    // 200（服务器不理 Range）→ 读全量再截断；206 → 按 cap 截断即可
                    var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(true);
                    if (bytes.Length > cap)
                    {
                        var trimmed = new byte[cap];
                        Array.Copy(bytes, trimmed, cap);
                        return trimmed;
                    }
                    return bytes;
                }
            }
        }
    }
}
