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
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Api;
using GeZi.Core.Download;
using GeZi.Core.Models;
using GeZi.Core.Support;

namespace GeZi.Core.Pan
{
    /// <summary>
    /// UC 网盘的 <see cref="IPanProvider"/> 实现（2026-11-02 新增，P1）。
    ///
    /// <para>【能力位必须如实】现在 <see cref="UcClient"/> 只有分享解析 + 取链，
    /// <b>没有</b>转存 / 删除 / 我的网盘浏览 / 分页。所以这里的 Caps 只声明
    /// <c>Share | QrLogin | CookieLogin | Capacity</c>，UI 会据此隐藏那些入口。
    /// 谎报能力会让用户点到"点了没反应/报不支持"的按钮。</para>
    ///
    /// <para>【与夸克的关键差异】UC 的取链必须同时传出 <c>fids_token</c>，
    /// 且响应里可能下发游客态 <c>__pugs</c>（登录态则不需要）。这些差异被
    /// <see cref="BuildDownloadContext"/> 与 <see cref="UcClient"/> 各自封装好了。</para>
    /// </summary>
    public sealed class UcProvider : IPanProvider
    {
        private readonly UcClient _client;
        private readonly string _cookie;

        public UcProvider(string cookie)
        {
            _cookie = cookie ?? "";
            _client = new UcClient(_cookie);
        }

        /// <summary>
        /// 用一个**已存在**的 <see cref="UcClient"/> 包装（2026-11-02 P1 新增）。
        ///
        /// <para>【为什么需要】主窗口的 <c>_ucClient</code> 是登录时建好、跨会话复用的长期存活实例，
        /// 上面挂着 <c>__pugs</code> 等运行时状态。如果 provider 自己再 new 一个，
        /// 就变成**两个底层客户端、两份 Cookie 状态** —— 登录态/取链结果会不一致。
        /// 所以必须能"包住现有实例"。</para>
        /// </summary>
        public UcProvider(UcClient existing)
        {
            _client = existing;
            _cookie = existing == null ? "" : existing.Cookie;
        }

        /// <summary>底层客户端（供确实需要 UC 特有能力的老代码过渡使用）。</summary>
        public UcClient Inner => _client;

        public PanMeta Meta { get; } = new PanMeta
        {
            Id = "uc",
            DisplayName = "UC 网盘",
            ShortName = "UC",
            IconKey = "IconUc",
            LoginKind = "both",
            ScanAppHint = "UC 浏览器 / UC 网盘 App",
            // ⚠️ 如实声明：暂无 Mine / Save / Delete / MineDownload
            Caps = PanCapabilities.Share | PanCapabilities.QrLogin
                 | PanCapabilities.CookieLogin | PanCapabilities.Capacity,
        };

        public string Cookie
        {
            get
            {
                string live = _client.Cookie;
                return string.IsNullOrEmpty(live) ? _cookie : live;
            }
        }

        public bool IsLoggedIn => _client.IsLoggedIn;

        public string AvatarUrl => _client.AvatarUrl;

        // ==================== 下载身份 ====================

        /// <summary>
        /// UC 的下载上下文（P0 已验证跑通的部分）。
        /// <para>把取链时记下的 <c>__pugs</c> 一并带进去 —— 游客态必需，登录态无害。</para>
        /// </summary>
        public DownloadContext BuildDownloadContext()
        {
            return DownloadContext.UcDefaults(Cookie, _client.LastPugs);
        }

        // ==================== 账号 ====================

        public Task<(bool ok, string nick)> ValidateAsync() => _client.ValidateAsync();

        public Task<(long total, long used)> GetCapacityAsync(CancellationToken ct = default)
            => _client.GetCapacityAsync(ct);

        // ==================== 分享 ====================

        public async Task<ShareTicket> ResolveShareAsync(string pwdId, string passcode,
            CancellationToken ct = default)
        {
            var (stoken, title) = await _client
                .GetShareTokenAsync(pwdId, passcode ?? "", ct).ConfigureAwait(false);
            return new ShareTicket { Stoken = stoken, Title = title };
        }

        // ==================== 分享：列表 ====================

        /// <summary>
        /// 取分享目录的一页。
        /// <para>【UC 的接口特点】<c>transfer_share/detail</c> 用 <c>_page</c>/<c>_size</c>
        /// 分页，且返回 <c>metadata._total</c>。这里按"本页取满就认为还有下一页"来判断，
        /// 因为 <c>UcClient</c> 目前不解析 _total（它原本只服务于一次性全量取）。</para>
        /// </summary>
        public async Task<DirPage> ListSharePageAsync(string pwdId, string stoken, string pdirFid,
            int page, CancellationToken ct = default)
        {
            const int pageSize = 50;
            var items = await _client
                .ListShareDirAsync(pwdId, stoken, pdirFid, page, pageSize, ct).ConfigureAwait(false);

            var one = new DirPage();
            if (items != null)
            {
                foreach (var it in items) one.Items.Add(it);
            }
            one.HasMore = one.Items.Count >= pageSize;
            return one;
        }

        /// <summary>
        /// 取分享目录的全部（内部翻页）。
        /// <para>⚠️ 有 200 页硬上限兜底 —— 与 QuarkClient.ShareDetailAsync 同口径，
        /// 防异常目录把请求打爆。</para>
        /// </summary>
        public async Task<List<QuarkFileEntry>> ListShareAllAsync(string pwdId, string stoken,
            string pdirFid, CancellationToken ct = default)
        {
            const int pageSize = 50;
            var merged = new List<QuarkFileEntry>();
            for (int page = 1; page <= 200; page++)
            {
                ct.ThrowIfCancellationRequested();
                var items = await _client
                    .ListShareDirAsync(pwdId, stoken, pdirFid, page, pageSize, ct).ConfigureAwait(false);
                if (items == null || items.Count == 0) break;
                merged.AddRange(items);
                if (items.Count < pageSize) break;
            }
            return merged;
        }

        // ==================== 分享：取直链 ====================

        /// <summary>
        /// 从分享文件取直链。
        /// <para>⚠️ <c>UcClient.GetDownloadLinkAsync</c> 一次只取一个 fid（响应也只回一条），
        /// 所以这里逐个调用再拼起来。多文件时请求数会跟着涨，但 UC 没有批量取链接口。</para>
        /// </summary>
        public async Task<List<DownloadLink>> LinkFromShareAsync(IList<QuarkFileEntry> refs,
            string pwdId, string stoken, CancellationToken ct = default)
        {
            var result = new List<DownloadLink>();
            if (refs == null) return result;

            foreach (var r in refs)
            {
                if (r == null || string.IsNullOrEmpty(r.Fid)) continue;
                ct.ThrowIfCancellationRequested();
                var (url, size, _pugs) = await _client
                    .GetDownloadLinkAsync(pwdId, stoken, r.Fid, r.Token ?? "", ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(url)) continue;

                result.Add(new DownloadLink
                {
                    Fid = r.Fid,
                    DownloadUrl = url,
                    Name = r.Name,
                    Size = size > 0 ? size : r.Size,
                });
            }
            return result;
        }

        // ==================== 可选能力（未实现 → 明确抛，不静默返回空）====================

        public Task<DirPage> ListMinePageAsync(string pdirFid, int page, CancellationToken ct = default)
            => throw new NotSupportedException("UC 网盘暂不支持浏览「我的网盘」。");

        public Task<List<DownloadLink>> LinkFromMineAsync(IList<string> fids, CancellationToken ct = default)
            => throw new NotSupportedException("UC 网盘暂不支持从「我的网盘」取直链。");

        public Task<List<string>> SaveShareAsync(IList<QuarkFileEntry> refs,
            string pwdId, string stoken, string toPdirFid, CancellationToken ct = default)
            => throw new NotSupportedException("UC 网盘暂不支持转存到自己的网盘。");

        public Task DeleteAsync(IList<string> fids, CancellationToken ct = default)
            => throw new NotSupportedException("UC 网盘暂不支持删除文件。");

        public Task<string> EnsureFolderAsync(string path, CancellationToken ct = default)
            => throw new NotSupportedException("UC 网盘暂不支持按路径建目录。");

        public void Dispose()
        {
            _client.Dispose();
        }
    }
}
