// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
// This program is free software; you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation; either version 3 of the License, or
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
    /// <summary>GitHub Releases 里的一个发布版本。</summary>
    public sealed class GitHubRelease
    {
        /// <summary>从 tag_name（如 v2.7.3）解析出的三段版本号。</summary>
        public UpdateVersion Version { get; set; }

        /// <summary>原始 tag（如 v2.7.3），展示用。</summary>
        public string TagName { get; set; }

        /// <summary>Release 标题（GitHub 的 name 字段）。</summary>
        public string Title { get; set; }

        /// <summary>更新说明（Markdown 原文）。可能为空。</summary>
        public string Body { get; set; }

        /// <summary>Release 详情页地址（「前往下载」打开它）。</summary>
        public string HtmlUrl { get; set; }

        /// <summary>安装包字节数（从 assets 里挑 GeZi-*-win64.zip）；取不到为 0。</summary>
        public long AssetSize { get; set; }

        /// <summary>发布时间（UTC）；取不到为 null。</summary>
        public DateTime? PublishedAt { get; set; }

        /// <summary>是否可用（至少要有版本号和详情页地址）。</summary>
        public bool IsValid
        {
            get { return Version != null && !string.IsNullOrEmpty(HtmlUrl); }
        }
    }
}
