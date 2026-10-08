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

namespace GeZi
{
    /// <summary>取直链后的下载条目（名字/大小/直链/相对目录），与分享原 fid 解耦。</summary>
    internal class DownloadItem
    {
        public string Name;
        public long Size;
        public string Url;
        public string RelDir;

        /// <summary>文件在网盘/分享里的 fid。直链过期时需要它去重新取链。</summary>
        public string Fid;

        /// <summary>分享内下载用的 share_fid_token（免转存路径需要）。</summary>
        public string Token;

        /// <summary>true = 走「分享免转存」取链（需要 pwd_id + stoken + token），false = 走我的网盘取链。</summary>
        public bool FromShare;
    }
}
