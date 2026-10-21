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
    /// 夸克分享里找到的一个安装包发布。
    /// <para>
    /// 【取直链为什么是惰性的】分享直链带时效（通常几小时），而用户完全可能
    /// 隔天才点「一键更新」。检查更新时又把直链取出来存着是无意义的浪费，
    /// 所以 <see cref="PackageUrl"/> 恒为空，直链一律在用户确认安装那一刻才现场取。
    /// </para>
    /// </summary>
    public sealed class QuarkRelease : IReleaseInfo
    {
        /// <summary>三段版本号。</summary>
        public UpdateVersion Version { get; set; }

        /// <summary>展示用标签（<c>v2.7.1</c>）。</summary>
        public string TagName { get; set; }

        /// <summary>文件名（如 <c>GeZi-v2.7.1-win64.zip</c>）。</summary>
        public string FileName { get; set; }

        /// <summary>字节数。</summary>
        public long Size { get; set; }

        /// <summary>分享里的文件 id。</summary>
        public string Fid { get; set; }

        /// <summary>取直链要用的文件 token（分享详情里带的）。</summary>
        public string Token { get; set; }

        /// <summary>分享 id。</summary>
        public string PwdId { get; set; }

        /// <summary>分享凭证。</summary>
        public string Stoken { get; set; }

        /// <summary>分享页地址。</summary>
        public string ShareUrl { get; set; }

        /// <summary>排序用的时间刻度（分享接口不返发布时间，恒为 0）。</summary>
        internal long PublishedTicks { get; set; }

        /// <summary>是否可用（版本号 + 取直链所需的三件套都齐了）。</summary>
        public bool IsValid
        {
            get
            {
                return Version != null
                    && !string.IsNullOrEmpty(Fid)
                    && !string.IsNullOrEmpty(PwdId)
                    && !string.IsNullOrEmpty(Stoken);
            }
        }

        // ---------------- IReleaseInfo ----------------

        /// <summary>夸克分享没有"发布说明"这个概念，标题就用文件名。</summary>
        public string Title
        {
            get { return FileName ?? ""; }
        }

        /// <summary>说明固定为空，UI 会提示"去分享页看"。</summary>
        public string Notes
        {
            get { return ""; }
        }

        /// <summary>「查看详情」打开分享页。</summary>
        public string OpenUrl
        {
            get { return ShareUrl ?? ""; }
        }

        /// <summary>安装包大小。</summary>
        public long PackageSize
        {
            get { return Size; }
        }

        /// <summary>没有发布时间。</summary>
        public DateTime? PublishedAt
        {
            get { return null; }
        }

        public string SourceName
        {
            get { return ReleaseSourceNames.Quark; }
        }

        /// <summary>分享包支持一键更新（能取到直链就能自己下）。</summary>
        public bool CanAutoInstall
        {
            get { return IsValid; }
        }

        /// <summary>恒为空：直链惰性解析，见类型注释。</summary>
        public string PackageUrl
        {
            get { return ""; }
        }
    }
}
