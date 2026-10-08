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
            string s = InvalidChars.Replace(name, "_").Trim().TrimEnd(' ', '.');
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
