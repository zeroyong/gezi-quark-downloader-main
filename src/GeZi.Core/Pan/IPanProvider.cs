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
using GeZi.Core.Upload;

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

        // ==================== ④-b 分享目录的祖先路径（2026-11-17 新增） ====================

        /// <summary>
        /// 取某个分享目录**从根到它自己**的完整路径（最后一项就是 <paramref name="pdirFid"/> 本身）。
        ///
        /// <para>【解决什么】分享链接形如 <c>.../s/&lt;pwd&gt;#/list/share/&lt;fid&gt;</c> 时，
        /// 解析会直接把位置瞬移到那个深层 fid，而 GeZi 的面包屑依赖
        /// <c>_fidStack</c>/<c>_pathList</c> 两条链 —— 它们当然是空的，于是只画得出一层，
        /// 「上一级」也直接跳回根。用户实测真实层级有 4 层却只显示 1 层。</para>
        ///
        /// <para>【为什么不用 BFS 逐层下钻】第一版试过：从根 BFS 找目标，但用户那个分享
        /// 第 2 层就有几十个「每日荐书丨MM月DD日」目录，40 次请求的预算瞬间烧光还没到目标层
        /// （日志原话：「未能在预算内找到目标目录的完整路径」）。而夸克接口本来就提供
        /// <c>fetch_share_full_path=1</c>，**一次请求**直接返回整条路径 —— 快、准、无猜测。</para>
        ///
        /// <para>返回空列表 = 该网盘/该场景不支持（调用方按"只有一层"显示，不报错）。
        /// 支持的实现应返回<b>不含根节点</b>的链（第一项 = 根下的第一层文件夹）。</para>
        /// </summary>
        Task<List<PanPathNode>> GetSharePathAsync(string pwdId, string stoken, string pdirFid,
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

        /// <summary>
        /// 上传一个本地文件到指定目录（2026-11-10 新增）。需 <see cref="PanCapabilities.Upload"/>。
        ///
        /// <para>返回 true 表示命中**秒传** —— 服务端已有同内容文件，
        /// 一个字节都没传（不消耗上行流量）。UI 用这个值给用户更准确的提示。</para>
        ///
        /// <para>⚠️ 不支持上传的网盘必须抛 <see cref="NotSupportedException"/>，
        /// 不能静默返回 false —— 否则用户会看到"上传完成"但网盘里什么都没有。</para>
        /// </summary>
        /// <param name="localPath">本地文件绝对路径。</param>
        /// <param name="parentFid">目标目录 fid（根目录为 <c>"0"</c>）。</param>
        /// <param name="progress">阶段 + 字节进度回调（可为 null）。</param>
        /// <param name="ct">取消令牌。</param>
        Task<bool> UploadAsync(string localPath, string parentFid,
            Action<UploadPhase, long, long> progress = null, CancellationToken ct = default);
    }
}
