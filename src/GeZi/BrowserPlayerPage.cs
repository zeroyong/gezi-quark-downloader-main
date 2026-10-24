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
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace GeZi
{
    /// <summary>
    /// 生成「浏览器内播放正在下载的视频」的播放页 HTML。
    ///
    /// <para>【用户需求】「能不能在下载视频过程中，比如右键文件夹 弹出一键播放选项，
    /// 然后可以在默认浏览器打开一个播放器界面，然后加载并播放正在下载的视频」。</para>
    ///
    /// <para>【为什么走得通】<see cref="LocalStreamServer"/> 已经把"正在增长的目标文件"
    /// 变成了标准 HTTP 视频地址（支持 Range + 边下边播 + 优先分片）。
    /// 浏览器原生支持 mp4/webm 的增量播放：它先发 Range 拿 moov，再按播放位置连续请求
    /// —— 与 VLC/PotPlayer 的行为完全一致。所以**不需要任何转码**，
    /// 只要一个 &lt;video&gt; 指向那个地址。</para>
    ///
    /// <para>【为什么不内嵌在自己的窗口里】用户明确要求"在默认浏览器打开"。
    /// 浏览器自带硬件解码、字幕、画中画、全屏，比自绘 WPF 播放器可靠得多，
    /// 也不用引入任何 NuGet 依赖（本项目零依赖）。</para>
    ///
    /// <para>⚠️ 浏览器能不能播取决于**容器与编码**：</para>
    /// <list type="bullet">
    /// <item>MP4 + H.264/AVC + AAC → 所有现代浏览器都能播（最常见）。</item>
    /// <item>MP4 + H.265/HEVC → Chrome/Edge 多数不能播。</item>
    /// <item>MKV → Chrome/Edge 支持较好，Firefox 较弱。</item>
    /// <item>AVI/WMV/RMVB → 基本都不支持。</item>
    /// </list>
    /// <para>浏览器的 <c>video.canPlayType</c> 只能判断**容器**、判断不了**编码**，
    /// 所以无法事前准确预判。因此页面上必须有明确的"播不了"提示与替代方案，
    /// 而不是让用户对着黑屏猜。</para>
    ///
    /// <para>【模板为什么放独立的 .html 资源】把整页 HTML 写成 C# verbatim 字符串
    /// 需要把每个双引号写成 <c>""</c>，而 HTML/CSS/JS 里引号极多 —— 实测编译直接
    /// 报出一串 CS1003/CS1010。放到真实 .html 文件里做占位符替换，
    /// 既不会有转义地雷，也让页面可以用浏览器直接打开调试。</para>
    /// </summary>
    internal static class BrowserPlayerPage
    {
        private const string TemplateResource = "GeZi.Resources.player.html";

        /// <summary>模板只读一次（同进程内页面结构不变）。</summary>
        private static string _template;

        private static string Template()
        {
            if (_template != null) return _template;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream(TemplateResource))
                {
                    if (s == null) return null;
                    using (var r = new StreamReader(s, Encoding.UTF8))
                        _template = r.ReadToEnd();
                }
            }
            catch { _template = null; }
            return _template;
        }

        /// <summary>
        /// 构造播放页。
        /// </summary>
        /// <param name="streamUrl">视频流地址（LocalStreamServer.Publish 的返回值）。</param>
        /// <param name="fileName">展示用的文件名。</param>
        /// <param name="totalBytes">文件总大小（未知填 0）。</param>
        /// <param name="isComplete">打开时是否已下载完成（决定初始文案）。</param>
        /// <param name="apiUrl">
        /// 进度查询接口（同源）。返回 JSON：
        /// <c>{"done":123,"total":456,"speed":789,"state":"Downloading","canGrow":true}</c>。
        /// 传 null 则不轮询（已完成的任务没必要）。
        /// </param>
        /// <returns>完整 HTML；模板缺失时返回一个最小的兜底页面。</returns>
        public static string Build(string streamUrl, string fileName, long totalBytes,
                                   bool isComplete, string apiUrl)
        {
            string tpl = Template();

            // 所有插入页面的字符串都必须转义：文件名来自网盘，
            // 可能含引号、尖括号、& 等（实测有「#编号」这类）。
            string jsStream = JsString(streamUrl ?? "");
            string jsApi = apiUrl == null ? "null" : JsString(apiUrl);
            string jsName = JsString(fileName ?? "video");
            string title = HtmlEscape(fileName ?? "视频播放");
            string totalText = totalBytes > 0 ? FormatSize(totalBytes) : "未知大小";

            if (string.IsNullOrEmpty(tpl))
            {
                // 兜底：资源没打包进来时，至少还能播（不带进度条与美化）。
                return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>"
                     + title + "</title></head><body style=\"margin:0;background:#000\">"
                     + "<video src=\"" + HtmlEscape(streamUrl ?? "")
                     + "\" controls autoplay style=\"width:100%;height:100vh\"></video>"
                     + "</body></html>";
            }

            return tpl
                .Replace("__TITLE__", title)
                .Replace("__TOTAL__", totalText)
                .Replace("__STREAM__", jsStream)
                .Replace("__API__", jsApi)
                .Replace("__NAME__", jsName)
                .Replace("__ISDONE__", isComplete ? "true" : "false");
        }

        /// <summary>转义成可安全嵌入 JS 源文本的双引号字符串字面量（含引号本身）。</summary>
        private static string JsString(string s)
        {
            if (s == null) s = "";
            var sb = new StringBuilder(s.Length + 16);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    // 防 </script> 提前截断脚本块
                    case '<': sb.Append("\\u003c"); break;
                    case '>': sb.Append("\\u003e"); break;
                    case '&': sb.Append("\\u0026"); break;
                    case '\u2028': sb.Append("\\u2028"); break;
                    case '\u2029': sb.Append("\\u2029"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>转义成 HTML 文本/属性值。</summary>
        private static string HtmlEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&#39;");
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            string[] u = { "KB", "MB", "GB", "TB" };
            double x = bytes;
            int i = -1;
            do { x /= 1024; i++; } while (x >= 1024 && i < u.Length - 1);
            string f = x < 10 ? "0.00" : (x < 100 ? "0.0" : "0");
            return x.ToString(f, CultureInfo.InvariantCulture) + " " + u[i];
        }
    }
}
