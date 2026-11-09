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
using GeZi.Core.Download;
using GeZi.Core.Models;

namespace GeZi.Core.Pan
{
    /// <summary>
    /// 网盘提供者接口（2026-11-02 新增，网盘抽象 P1 的核心）。
    ///
    /// <para>【设计目标】UI 与下载器**只认这个接口**，不认识 QuarkClient / UcClient /
    /// 以后的百度客户端。新增网盘 = 多一个实现类（同构的甚至可以只是多一份 manifest），
    /// 调用方一行不改。</para>
    ///
    /// <para>【为什么是这四个分组】按真实使用顺序切：
    /// <c>Meta</gt>（画界面用）→ <c>Auth</c>（登录）→ <c>Files</c>（浏览/解析）→
    /// <c>Link</c>（取直链 + 下载上下文）。网盘之间的差异恰好落在这些边界上。</para>
    ///
    /// <para>【能力不足时的约定】**必须抛 <see cref="NotSupportedException"/>，
    /// 不能静默返回空**。UI 会先查 <see cref="PanMeta.Caps"/> 来隐藏入口，
    /// 一旦真的调到就是编程错误，应该立刻暴露而不是让用户面对一个"点了没反应"的按钮。</para>
    /// </summary>
    public interface IPanProvider : IDisposable
    {
        // ==================== ① 元信息 ====================

        /// <summary>身份与展示信息。UI 只读它来画界面。</summary>
        PanMeta Meta { get; }

        /// <summary>当前 Cookie（含服务端 Set-Cookie 更新后的最新值）。</summary>
        string Cookie { get; }

        /// <summary>登录态是否仍然有效。</summary>
        bool IsLoggedIn { get; }

        // ==================== ② 下载身份（P0 的地基，必须实现）====================

        /// <summary>
        /// 造本次下载的身份上下文（UA / Referer / Origin / Cookie / 种子域 / 额外头）。
        ///
        /// <para>★ 这是网盘抽象能落地的关键一步：下载器只认
        /// <see cref="DownloadContext"/>，换网盘只要换这个返回值。</para>
        /// </summary>
        DownloadContext BuildDownloadContext();

        // ==================== ③ 账号 ====================

        /// <summary>校验登录态并取昵称。</summary>
        Task<(bool ok, string nick)> ValidateAsync();

        /// <summary>取网盘容量（已用 / 总量）。不支持时返回 (0,0) 而不是抛异常 —— 这是展示性信息。</summary>
        Task<(long total, long used)> GetCapacityAsync(CancellationToken ct = default);

        /// <summary>头像 URL（探测得到才有，否则 null）。</summary>
        string AvatarUrl { get; }

        // ==================== ④ 分享 ====================

        /// <summary>解析分享链接，换 stoken + 标题。</summary>
        Task<ShareTicket> ResolveShareAsync(string pwdId, string passcode,
            CancellationToken ct = default);

        /// <summary>取分享目录的**一页**（page 从 1 开始）。</summary>
        Task<DirPage> ListSharePageAsync(string pwdId, string stoken, string pdirFid,
            int page, CancellationToken ct = default);

        /// <summary>
        /// 取分享目录的**全部**（内部循环翻页）。
        /// <para>供递归算文件夹大小等场合用；正常浏览请用
        /// <see cref="ListSharePageAsync"/> 按需取页，否则大目录会很慢。</para>
        /// </summary>
        Task<List<QuarkFileEntry>> ListShareAllAsync(string pwdId, string stoken, string pdirFid,
            CancellationToken ct = default);

        // ==================== ⑤ 取直链 ====================

        /// <summary>
        /// 从分享里的文件取直链。
        /// <param name="refs">要取链的文件（fid + share_fid_token）。</param>
        /// </summary>
        Task<List<DownloadLink>> LinkFromShareAsync(IList<QuarkFileEntry> refs,
            string pwdId, string stoken, CancellationToken ct = default);

        // ---------- 以下为可选能力，实现前先声明对应的 Caps ----------

        /// <summary>取「我的网盘」目录的一页。需 <see cref="PanCapabilities.Mine"/>。</summary>
        Task<DirPage> ListMinePageAsync(string pdirFid, int page, CancellationToken ct = default);

        /// <summary>取「我的网盘」文件的直链。需 <see cref="PanCapabilities.MineDownload"/>。</summary>
        Task<List<DownloadLink>> LinkFromMineAsync(IList<string> fids, CancellationToken ct = default);

        /// <summary>转存分享文件到自己网盘。需 <see cref="PanCapabilities.Save"/>。</summary>
        Task<List<string>> SaveShareAsync(IList<QuarkFileEntry> refs,
            string pwdId, string stoken, string toPdirFid, CancellationToken ct = default);

        /// <summary>删除网盘文件 + 清回收站。需 <see cref="PanCapabilities.Delete"/>。</summary>
        Task DeleteAsync(IList<string> fids, CancellationToken ct = default);

        /// <summary>按路径找 fid（不存在就建）。需 <see cref="PanCapabilities.Mine"/>。</summary>
        Task<string> EnsureFolderAsync(string path, CancellationToken ct = default);
    }
}
