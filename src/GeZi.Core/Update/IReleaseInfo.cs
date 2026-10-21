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

namespace GeZi.Core.Update
{
    /// <summary>
    /// 「一次可更新到的版本」的最小信息集。
    /// <para>
    /// 【为什么需要接口】更新源从「只有 GitHub Releases」扩到「GitHub + 夸克分享双源」后，
    /// UI 层的更新详情窗、红点、忽略逻辑都必须同时吃得下两种来源。
    /// 与其在 UI 层写一堆 <c>if (source == "quark")</c>，不如让两种发布信息实现同一个接口，
    /// UI 只对着接口说话（版本号、说明、打开哪个页面、能否一键更新）。
    /// </para>
    /// </summary>
    public interface IReleaseInfo
    {
        /// <summary>三段版本号（"2.7.1"）。null / 解析失败表示这个发布不可用。</summary>
        UpdateVersion Version { get; }

        /// <summary>原始版本标签（如 <c>v2.7.1</c>），纯展示用。</summary>
        string TagName { get; }

        /// <summary>发布标题（会标到详情窗顶部的徽章里）。</summary>
        string Title { get; }

        /// <summary>更新说明（纯文本）。没有时为空串，UI 自己兜底文案。</summary>
        string Notes { get; }

        /// <summary>「查看详情」用的人类可访问地址。</summary>
        string OpenUrl { get; }

        /// <summary>安装包字节数；未知为 0。</summary>
        long PackageSize { get; }

        /// <summary>发布时间（本地时间）；未知为 null。</summary>
        DateTime? PublishedAt { get; }

        /// <summary>是否可用（至少要有版本号）。</summary>
        bool IsValid { get; }

        /// <summary>
        /// 是否支持「一键更新」：程序自己下包装好并接力重启。
        /// GitHub 发布历史上没有附件时不支持，只能打开网页让用户手动下。
        /// </summary>
        bool CanAutoInstall { get; }

        /// <summary>
        /// 一键更新用的安装包直链；<see cref="CanAutoInstall"/> 为 false 时为空。
        /// ⚠️ 该链接通常带时效，取到后应尽快用，不要长期缓存。
        /// </summary>
        string PackageUrl { get; }

        /// <summary>更新源显示名，如 "GitHub Releases" / "夸克网盘分享"。</summary>
        string SourceName { get; }
    }

    /// <summary>更新源显示名常量（避免各处硬编码字符串写错）。</summary>
    public static class ReleaseSourceNames
    {
        public const string GitHub = "GitHub Releases";
        public const string Quark = "夸克网盘分享";
    }
}
