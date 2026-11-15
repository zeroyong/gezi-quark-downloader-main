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
using System.Text;
using System.Text.RegularExpressions;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 网盘返回的文件名清洗（2026-11-10 新增）。
    ///
    /// <para>【为什么需要】用户实测：下载直接失败，报
    /// <c>无法创建分片目录 D:\Downloads\gezi\[...]\I&amp;#39;m in the &amp;#39;Stranger Things&amp;#39; world...</c></para>
    ///
    /// <para>【根因】网盘的 <c>file_name</c> 字段返回的是 **HTML 实体转义后的文本** ——
    /// 文件名里的单引号 <c>'</c> 变成了 <c>&amp;#39;</c>（五个字符）。
    /// 这不是我们编码错了，是**服务端就这么返回**（实测 state.json 里存的就是这种串）。
    /// 后果不只是显示难看：这五个字符会原样进入磁盘路径，
    /// 而中文 Windows 上路径长度本就紧张，多出来的字符让 <c>Directory.CreateDirectory</c>
    /// 直接抛异常 —— 下载还没开始就失败。</para>
    ///
    /// <para>【在哪修】必须修在**解析入口**（把 API 响应转成 QuarkFileEntry 的地方），
    /// 而不是下载器里。否则文件名会带着实体一路流过界面、历史记录、任务快照，
    /// 每一处都要各自补救 —— 那正是这类 bug 反复出现的原因。</para>
    /// </summary>
    public static class FileNameSanitizer
    {
        /// <summary>常见命名实体表（覆盖会用到的少量）。数字实体走通用解析。</summary>
        private static readonly Dictionary<string, string> NamedEntities =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "amp", "&" },
                { "lt", "<" },
                { "gt", ">" },
                { "quot", "\"" },
                { "apos", "'" },
                { "nbsp", " " },
                { "hellip", "…" },
                { "mdash", "—" },
                { "ndash", "–" },
                { "lsquo", "‘" },
                { "rsquo", "’" },
                { "ldquo", "“" },
                { "rdquo", "”" },
                { "middot", "·" },
                { "times", "×" },
                { "divide", "÷" },
                { "copy", "©" },
                { "reg", "®" },
                { "trade", "™" },
                { "deg", "°" },
            };

        /// <summary>匹配 &amp;#39; / &amp;#x27; / &amp;amp; 这类实体。</summary>
        private static readonly Regex EntityPattern = new Regex(
            @"&(#x?[0-9A-Fa-f]{1,8}|[A-Za-z][A-Za-z0-9]{1,31});",
            RegexOptions.Compiled);

        /// <summary>
        /// 把 HTML 实体还原成真实字符。
        ///
        /// <para>只处理实体的**基本集合**，不做完整 HTML 解析 —— 文件名里不会出现
        /// 复杂的嵌套结构。无法识别的实体原样保留（宁可留着，也不要吞掉内容）。</para>
        /// </summary>
        public static string DecodeEntities(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.IndexOf('&') < 0) return text;   // 快速返回：绝大多数文件名没有 &

            try
            {
                return EntityPattern.Replace(text, m =>
                {
                    string body = m.Groups[1].Value;

                    // 数字实体：&#39; 或 &#x27;
                    if (body.Length > 0 && body[0] == '#')
                    {
                        string num = body.Substring(1);
                        int code;
                        bool ok;
                        if (num.Length > 0 && (num[0] == 'x' || num[0] == 'X'))
                            ok = int.TryParse(num.Substring(1), System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out code);
                        else
                            ok = int.TryParse(num, System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out code);

                        if (!ok || code <= 0 || code > 0x10FFFF) return m.Value;
                        // 代理对区间是非法码点，不能直接转
                        if (code >= 0xD800 && code <= 0xDFFF) return m.Value;
                        return char.ConvertFromUtf32(code);
                    }

                    string named;
                    return NamedEntities.TryGetValue(body, out named) ? named : m.Value;
                });
            }
            catch
            {
                return text;   // 解码失败就用原文，绝不因为清洗把文件名搞丢
            }
        }

        /// <summary>Windows 文件名里禁止出现的字符。</summary>
        private static readonly char[] InvalidChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

        /// <summary>
        /// 清洗一个**文件名**（不是完整路径）：
        /// ① 解 HTML 实体；② 把 Windows 非法字符换成全角或下划线；
        /// ③ 去掉首尾空白与结尾的点（Windows 会悄悄吃掉结尾的点，导致名字对不上）；
        /// ④ 空名兜底。
        ///
        /// <para>⚠️ 这里**不做**长度截断 —— 那样可能把扩展名切掉、或让两个文件重名。
        /// 路径过长的处理交给上层（它们在拼路径时能看到完整上下文）。</para>
        /// </summary>
        public static string CleanFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            string s = DecodeEntities(name);

            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (Array.IndexOf(InvalidChars, c) >= 0)
                {
                    // 保留「可读性」：常见字符换成语义接近的全角形式
                    switch (c)
                    {
                        case ':': sb.Append('：'); break;
                        case '*': sb.Append('＊'); break;
                        case '?': sb.Append('？'); break;
                        case '"': sb.Append('＂'); break;
                        case '<': sb.Append('＜'); break;
                        case '>': sb.Append('＞'); break;
                        case '|': sb.Append('｜'); break;
                        case '\\': sb.Append('＼'); break;
                        case '/': sb.Append('／'); break;
                    }
                }
                else if (c < 32)
                {
                    // 控制字符直接丢弃（它们会让某些 API 报错，且不可见）
                    continue;
                }
                else
                {
                    sb.Append(c);
                }
            }

            // ⚠️ 必须把**首尾空白全部去掉**（Trim 而不只是 TrimEnd）。
            //
            // 【2026-11-11 血泪】Windows 在落盘时会规范化掉首尾空格，
            // 但我们后续用 Directory.Exists 校验时用的是**未规范化**的原串，
            // 于是"明明创建成功了却查不到"，紧接着建子目录就报
            // 「未能找到路径的一部分」。用户机器上因此积出了两个名字相近的空目录。
            // 落盘用的名字必须与磁盘上的名字**恒等**。
            string result = sb.ToString().Trim();
            result = result.TrimEnd('.', ' ');
            if (result.Length == 0) return "未命名";
            return result;
        }

        /// <summary>
        /// 清洗一个**路径里的目录名**。与 <see cref="CleanFileName"/> 同规则，
        /// 但空值时保留原名语义（目录名一般不会为空）。
        /// </summary>
        public static string CleanDirName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            return CleanFileName(name);
        }
    }
}
