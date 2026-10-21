// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;

namespace GeZi.Core.Update
{
    /// <summary>
    /// GitHub Releases 里的一个发布版本（2026-10-18「新一代检查更新」）。
    /// <para>
    /// 【2026-10-22 双源化】GitHub 国内访问不稳定（启动检查常因 API 不通而静默失败），
    /// 用户又习惯在夸克分享里取安装包，于是新增 <see cref="QuarkRelease"/>
    /// （夸克分享源）。两者都实现 <see cref="IReleaseInfo"/>，由
    /// <see cref="UpdateService"/> 取版本号更高的那个当「最新版本」。
    /// </para>
    /// <para>
    /// GitHub release 的 asset 直链是公开可 GET 的（<c>browser_download_url</c>），
    /// 所以它天生支持「一键更新」；取不到附件时才退化为打开网页手动下载。
    /// </para>
    /// </summary>
    public sealed class GitHubRelease : IReleaseInfo
    {
        /// <summary>从 tag_name（如 <c>v2.2.0</c>）解析出的三段版本号。</summary>
        public UpdateVersion Version { get; set; }

        /// <summary>原始 tag（如 <c>v2.2.0</c>），展示用。</summary>
        public string TagName { get; set; }

        /// <summary>Release 标题（GitHub 的 name 字段，如「鸽子下载 v2.2.0 —— …」）。</summary>
        public string Title { get; set; }

        /// <summary>更新说明（Markdown 原文）。可能为空。</summary>
        public string Body { get; set; }

        /// <summary>Release 详情页地址（「前往下载」就打开它）。</summary>
        public string HtmlUrl { get; set; }

        /// <summary>安装包字节数（从 assets 里挑 <c>GeZi-*-win64.zip</c>）；取不到为 0。</summary>
        public long AssetSize { get; set; }

        /// <summary>发布时间（UTC）；取不到为 null。</summary>
        public DateTime? PublishedAt { get; set; }

        /// <summary>win64 安装包的浏览器直链（<c>browser_download_url</c>）；取不到为空串。</summary>
        public string AssetUrl { get; set; }

        /// <summary>是否可用（至少要有版本号）。</summary>
        public bool IsValid
        {
            get { return Version != null; }
        }

        // ---------------- IReleaseInfo ----------------

        /// <summary>更新说明（Release body 原文）。</summary>
        public string Notes
        {
            get { return Body ?? ""; }
        }

        /// <summary>「查看详情」打开 Release 详情页。</summary>
        public string OpenUrl
        {
            get { return HtmlUrl ?? ""; }
        }

        /// <summary>安装包字节数。</summary>
        public long PackageSize
        {
            get { return AssetSize; }
        }

        /// <summary>GitHub 发布一律标 GitHub，UI 转成中文标签。</summary>
        public string SourceName
        {
            get { return ReleaseSourceNames.GitHub; }
        }

        /// <summary>
        /// 能否一键更新：有 win64 安装包的直链就能。
        /// GitHub 侧通常是有的（发版附件固定 <c>GeZi-vX.Y.Z-win64.zip</c>）。
        /// </summary>
        public bool CanAutoInstall
        {
            get { return !string.IsNullOrEmpty(AssetUrl); }
        }

        /// <summary>安装包直链（<c>browser_download_url</c>）。</summary>
        public string PackageUrl
        {
            get { return AssetUrl ?? ""; }
        }
    }
}
