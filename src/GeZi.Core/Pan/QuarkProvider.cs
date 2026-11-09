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
    /// 夸克网盘的 <see cref="IPanProvider"/> 实现（2026-11-02 新增，P1）。
    ///
    /// <para>【这是纯包装，不是重写】所有逻辑都委托给既有的 <see cref="QuarkClient"/> ——
    /// 里面有 5000 行实测磨出来的东西（直链多入口择优、错误码优先级、OpenSSL 桥、
    /// 续传自愈），重写等于把这些坑重踩一遍。本类只做类型适配。</para>
    /// </summary>
    public sealed class QuarkProvider : IPanProvider
    {
        private readonly QuarkClient _client;
        private readonly string _cookie;

        public QuarkProvider(string cookie)
        {
            _cookie = cookie ?? "";
            _client = new QuarkClient(_cookie);
        }

        /// <summary>
        /// 用一个**已存在**的 <see cref="QuarkClient"/> 包装（2026-11-02 P1 新增）。
        /// <para>理由同 <see cref="UcProvider(UcClient)"/>：主窗口的 <c>_client</c> 是长期存活实例，
        /// 上面挂着保活心跳与 <c>LoginExpired</c> 事件。让 provider 再 new 一个会产生
        /// 两份会话状态，导致"登录了但取链说没登录"这类诡异现象。</para>
        /// </summary>
        public QuarkProvider(QuarkClient existing)
        {
            _client = existing;
            _cookie = existing == null ? "" : existing.CookieStr;
        }

        /// <summary>底层客户端（供确实需要夸克特有能力的老代码过渡使用）。</summary>
        public QuarkClient Inner => _client;

        public PanMeta Meta { get; } = new PanMeta
        {
            Id = "quark",
            DisplayName = "夸克网盘",
            ShortName = "夸克",
            IconKey = "IconQuark",
            LoginKind = "both",
            ScanAppHint = "夸克 App",
            Caps = PanCapabilities.Share | PanCapabilities.Mine | PanCapabilities.Save
                 | PanCapabilities.Delete | PanCapabilities.QrLogin | PanCapabilities.CookieLogin
                 | PanCapabilities.MineDownload | PanCapabilities.Capacity | PanCapabilities.Export,
        };

        public string Cookie => _client.CookieStr;

        public bool IsLoggedIn => _client.IsLoggedIn;

        public string AvatarUrl => _client.AvatarUrl;

        // ==================== 下载身份 ====================

        /// <summary>
        /// 夸克的下载上下文。
        /// <para>用 Cookie 优先取运行期的实时值（<c>QuarkClient.CookieStr</c> 含服务端下发的新
        /// <c>__puus</c>），退化才用构造时那份 —— 与主窗口既有的取法一致。</para>
        /// </summary>
        public DownloadContext BuildDownloadContext()
        {
            string live = _client.CookieStr;
            return DownloadContext.QuarkDefaults(string.IsNullOrEmpty(live) ? _cookie : live);
        }

        // ==================== 账号 ====================

        public Task<(bool ok, string nick)> ValidateAsync() => _client.ValidateAsync();

        public Task<(long total, long used)> GetCapacityAsync(CancellationToken ct = default)
            => _client.GetCapacityAsync();

        // ==================== 分享 ====================

        public async Task<ShareTicket> ResolveShareAsync(string pwdId, string passcode,
            CancellationToken ct = default)
        {
            var info = await _client.GetShareInfoAsync(pwdId, passcode ?? "").ConfigureAwait(false);
            return new ShareTicket
            {
                Stoken = info == null ? "" : info.Stoken,
                Title = info == null ? "" : info.Title,
            };
        }

        public Task<DirPage> ListSharePageAsync(string pwdId, string stoken, string pdirFid,
            int page, CancellationToken ct = default)
            => _client.ShareDetailPageAsync(pwdId, stoken, pdirFid, page);

        public Task<List<QuarkFileEntry>> ListShareAllAsync(string pwdId, string stoken,
            string pdirFid, CancellationToken ct = default)
            => _client.ShareDetailAsync(pwdId, stoken, pdirFid);

        // ==================== 取直链 ====================

        public Task<List<DownloadLink>> LinkFromShareAsync(IList<QuarkFileEntry> refs,
            string pwdId, string stoken, CancellationToken ct = default)
        {
            var fids = new List<string>();
            var tokens = new List<string>();
            foreach (var r in refs)
            {
                if (r == null) continue;
                fids.Add(r.Fid);
                tokens.Add(r.Token ?? "");
            }
            return _client.GetShareDownloadAsync(fids, tokens, pwdId, stoken);
        }

        // ==================== 可选能力 ====================

        public Task<DirPage> ListMinePageAsync(string pdirFid, int page, CancellationToken ct = default)
            => _client.ListDirPageAsync(pdirFid, page);

        public Task<List<DownloadLink>> LinkFromMineAsync(IList<string> fids, CancellationToken ct = default)
            => _client.GetDownloadAsync(fids);

        public async Task<List<string>> SaveShareAsync(IList<QuarkFileEntry> refs,
            string pwdId, string stoken, string toPdirFid, CancellationToken ct = default)
        {
            var fids = new List<string>();
            var tokens = new List<string>();
            foreach (var r in refs)
            {
                if (r == null) continue;
                fids.Add(r.Fid);
                tokens.Add(r.Token ?? "");
            }
            return await _client.SaveShareAndGetFidsAsync(fids, tokens, toPdirFid, pwdId, stoken)
                .ConfigureAwait(false);
        }

        public async Task DeleteAsync(IList<string> fids, CancellationToken ct = default)
        {
            await _client.DeleteAsync(fids).ConfigureAwait(false);
            try { await _client.PurgeRecycleAsync(fids).ConfigureAwait(false); } catch { }
        }

        public async Task<string> EnsureFolderAsync(string path, CancellationToken ct = default)
        {
            // QuarkClient.PathFidAsync 内部已经"找不到就建"，与这里语义一致
            return await _client.PathFidAsync(path).ConfigureAwait(false);
        }

        public void Dispose()
        {
            try { _client.StopKeepAlive(); } catch { }
            _client.Dispose();
        }
    }
}
