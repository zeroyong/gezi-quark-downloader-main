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
using System.IO;
using System.Text.RegularExpressions;

namespace GeZi.Core.Support
{
    /// <summary>文件/路径工具，等价于核心里的 sanitize_filename / unique_path / base_name。</summary>
    public static class PathUtil
    {
        private static readonly Regex InvalidChars = new Regex(@"[\\/:*?""<>|]", RegexOptions.Compiled);

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "quark_file";

            // 【2026-11-10 修】先把 HTML 实体还原，再替换非法字符。
            //
            // 【用户实测】下载直接失败：
            //   失败: 无法创建分片目录 D:\Downloads\gezi\[…]\I&#39;m in the &#39;Stranger Things&#39; world…
            // 网盘的 file_name 返回的是实体转义文本（' → &#39;），
            // 而 & # ; 都是 Windows 合法字符，下面这条正则一个都拦不住 ——
            // 于是实体原样进了磁盘路径，名字凭空长十几个字符，
            // 在中文 Windows 的 MAX_PATH 预算里直接抛异常，下载还没开始就失败。
            //
            // 这是**生成磁盘名的唯一收口**（界面、任务快照、下载器全都走它），
            // 所以修在这里能一次覆盖所有入口，不用逐处补救。
            string s = FileNameSanitizer.DecodeEntities(name);
            s = InvalidChars.Replace(s, "_");

            // ⚠️ 【2026-11-11 关键修复】必须 Trim() 两端空白，不能只 TrimEnd(' ', '.')。
            //
            // 【用户实测】下载报「无法创建分片目录 …… 未能找到路径的一部分」。
            // 逐层复现后才看清根因，和长度/非法字符/emoji 全都无关：
            // 分享里的目录名带了一个**首尾空白**（`韩国丰满女神「…」油管视频合集 `），
            // 而 Windows 规范化文件名时会把首尾空格**吃掉**——
            //   · 我们拿 "…合集 "（末尾带空格）去 CreateDirectory → 成功；
            //   · 但磁盘上真正落下的名字是 "…合集"（空格被规范化掉）；
            //   · 之后 Directory.Exists("…合集 ") 返回 **false**（名字对不上）；
            //   · 于是建子目录时父路径"不存在" → DirectoryNotFoundException
            //     →「未能找到路径的一部分」，下载还没开始就失败。
            //
            // 更要命的是它不稳定：同一份数据有时空格跑到中间、有时在末尾，
            // 于是每次重试都落出一个**名字不同**的空目录（用户机器上就出现了两个）。
            //
            // 修法：落盘前把首尾空白彻底去掉，让"我们算出的名字"与"磁盘上的名字"恒等。
            s = s.Trim().TrimEnd(' ', '.');
            return string.IsNullOrEmpty(s) ? "quark_file" : s;
        }

        public static string UniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                return path;
            string dir = Path.GetDirectoryName(path) ?? "";
            string root = Path.Combine(dir, Path.GetFileNameWithoutExtension(path));
            string ext = Path.GetExtension(path);
            int i = 1;
            while (File.Exists(root + " (" + i + ")" + ext) || Directory.Exists(root + " (" + i + ")" + ext))
                i++;
            return root + " (" + i + ")" + ext;
        }

    }
}
