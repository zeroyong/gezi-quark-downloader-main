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

        /// <summary>
        /// 「转存」路径写入用户网盘的那份中转文件的 fid。
        /// 下载完成后应立即清理的就是它；「免转存」/「我的网盘」路径此字段为 null/空
        /// （那些 fid 指向分享源文件或用户原文件，绝不能删）。
        /// </summary>
        public string TransferredFid;

        // ==================== 转存副本失效 → 回退免转存（2026-10-22 新增） ====================
        //
        // 【问题】转存路径下 <see cref="Fid"/> 记的是**中转副本**的 fid。
        // 用户把副本删掉后，续传就再也拿不到直链（"file not found"），
        // 而分享里的**源文件明明还在** —— 白白下不去。
        //
        // 【修法】把源文件的定位信息也带下来，续传取链失败时据此回退到免转存。

        /// <summary>
        /// 分享内**源文件**的 fid。免转存路径下 == <see cref="Fid"/>；
        /// 转存路径下是分享里的原始 fid（副本没了还能靠它找回来）。
        /// </summary>
        public string SourceFid;

        /// <summary>
        /// 分享内源文件的**文件名**。源 fid 也失效时按名字在分享里定位。
        /// </summary>
        public string SourceName;

        /// <summary>分享内下载用的 share_fid_token（免转存路径需要）。</summary>
        public string Token;

        /// <summary>true = 走「分享免转存」取链（需要 pwd_id + stoken + token），false = 走我的网盘取链。</summary>
        public bool FromShare;

        // ==================== 免转存续传自愈用（2026-10-20） ====================
        // stoken 会过期（默认几小时）。过期后原来只能提示"无法续传，请重新解析分享"，
        // 用户得手动回退、重贴链接、重新勾选文件。下面三个字段让程序能**自己重新解析**：
        //   ① 重新申请 stoken（需要 pwdId + Passcode）；
        //   ② 从 StartFid 开始沿 ShareRelDir 逐级下钻回原目录；
        //   ③ 按 Fid 找回这个文件的新 share_fid_token。

        /// <summary>分享提取码（无密码分享为 null/空）。重新解析分享时需要。</summary>
        public string Passcode;

        /// <summary>进入分享时的起点目录 fid（"0" 或空 = 根目录）。重新下钻的起点。</summary>
        public string StartFid;

        /// <summary>文件相对于分享根目录的相对路径（形如 "A\B"）。逐级下钻用。</summary>
        public string ShareRelDir;
    }
}
