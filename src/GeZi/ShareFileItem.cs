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
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GeZi
{
    /// <summary>
    /// 分享文件条目（供列表展示与下载）。
    /// </summary>
    public class ShareFileItem : INotifyPropertyChanged
    {
        public string Fid { get; set; }
        public string Token { get; set; }
        public string Name { get; set; }

        /// <summary>文件自身的字节数。**文件夹恒为 0** —— 夸克列表接口对目录不返回大小。</summary>
        public long Size { get; set; }

        public string RelDir { get; set; } = "";
        public bool IsDir { get; set; }

        /// <summary>
        /// 该文件在分享里的**父目录 fid 链**（'/'+fid，2026-11-21 新增）。
        ///
        /// <para>勾选文件夹深度展开时，里面的文件比当前浏览层深好几层，
        /// 光靠界面的 <c>_fidStack</c> 采不到 —— 所以让每个文件自带自己的链
        /// （由 <c>CollectFilesAsync</c> 逐层累积）。下载入队时直接取它，
        /// stoken 过期重解析就能按 fid 一步定位，不必按名字下钻（那条路会因为
        /// SanitizeFileName 改名而失败，再掉进全分享 BFS 卡几十秒）。</para>
        ///
        /// <para>null / 空 = 无链（就在分享根目录或非分享模式）→ 重解析降级走旧逻辑。</para>
        /// </summary>
        public string ShareFidChain { get; set; }

        /// <summary>
        /// 网盘返回的缩略图 URL（2026-11-15，图片/视频类才有；<c>//</c> 开头要补 https:）。
        /// 由 MainWindow.Preview 的缩略图服务异步拉取后填进 <see cref="Thumbnail"/>。
        /// </summary>
        public string ThumbnailUrl { get; set; }

        /// <summary>
        /// 已解码的缩略图位图（DecodePixelWidth 已压到小图，内存可忽略）。
        /// null = 还没拉到 / 拉失败 → 列表回落类型图标。拉到后在 UI 线程 Raise。
        /// </summary>
        private BitmapImage _thumbnail;
        public BitmapImage Thumbnail
        {
            get { return _thumbnail; }
            set
            {
                _thumbnail = value;
                Raise(nameof(Thumbnail));
                Raise(nameof(HasThumbnail));
            }
        }

        /// <summary>缩略图是否就位。XAML 用它做正向 DataTrigger（bool 判定比 {x:Null} 反向判定可靠）。</summary>
        public bool HasThumbnail { get { return _thumbnail != null; } }

        // ==================== 类型图标（2026-11-15 P0） ====================

        /// <summary>
        /// 文件类型的**图标键**（按扩展名映射）：
        /// folder / image / video / audio / docx / xlsx / pptx / pdf / txt / zip /
        /// code / exe / subtitle / torrent / other。列表行 XAML 的 DataTrigger 靠它切符号。
        /// </summary>
        public string IconKey { get; set; }

        /// <summary>该类型图标的主题色（取自 Colors.Dark.xaml 的既有 token，已冻结）。</summary>
        public SolidColorBrush IconBrush { get; set; }

        private long? _dirSize;
        private bool _dirSizeFailed;

        /// <summary>
        /// 文件夹的**递归总大小**（后台算出来后填进来）。
        /// null = 还没算出来 → 显示「计算中…」。
        /// </summary>
        public long? DirSize
        {
            get { return _dirSize; }
            set
            {
                _dirSize = value;
                Raise(nameof(DirSize));
                Raise(nameof(SizeText));
            }
        }

        /// <summary>该文件夹大小算失败了（接口报错/被取消）→ 显示「—」，不显示误导性的 0 B。</summary>
        public bool DirSizeFailed
        {
            get { return _dirSizeFailed; }
            set
            {
                _dirSizeFailed = value;
                Raise(nameof(DirSizeFailed));
                Raise(nameof(SizeText));
            }
        }

        // ==================== 第二行：修改日期（2026-11-15 晚间定稿） ====================
        //
        // 交互演进：P0 首版是「第二行 = 大小 + 日期、右侧 = ···按钮」；
        // 用户实测「眼睛好累」→ 大小回右侧列、···删除，第二行只留淡色日期。

        /// <summary>修改时间的显示文案（yyyy-MM-dd HH:mm）。接口没给（UpdatedAt=0）为空串。</summary>
        public string UpdatedAtText { get; private set; } = "";

        /// <summary>设置修改时间（Unix 秒，网盘接口返回）。0 / 负数 = 没有日期。</summary>
        public long UpdatedAt
        {
            set
            {
                if (value <= 0)
                {
                    UpdatedAtText = "";
                }
                else
                {
                    try
                    {
                        UpdatedAtText = DateTimeOffset
                            .FromUnixTimeSeconds(value).LocalDateTime
                            .ToString("yyyy-MM-dd HH:mm");
                    }
                    catch { UpdatedAtText = ""; }
                }
                Raise(nameof(UpdatedAtText));
                Raise(nameof(DateLine)); Raise(nameof(HasDate));
            }
        }

        /// <summary>列表行**第二行**文案：修改日期（2026-11-15 晚间定稿）。
        ///
        /// <para>用户原话：「大小 还是移到右侧吧，去掉三个点 不然看起来眼睛好累」——
        /// P0 当时的"第二行大小+日期 + 右侧···"三元素太挤，改回：
        /// 大小回右侧列（SizeText，与文件夹递归大小同列）、「···」按钮删除
        /// （操作走右键菜单）、第二行只留淡色日期。
        /// 受 <see cref="ShowDatesInList"/> 开关控制（设置页「个性化」）。</para>
        /// </summary>
        public string DateLine
        {
            get { return HasDate ? UpdatedAtText : ""; }
        }

        /// <summary>第二行该不该显示（开关开 且 接口给了时间）。XAML 用它做正向显隐判定。</summary>
        public bool HasDate
        {
            get { return ShowDatesInList && !string.IsNullOrEmpty(UpdatedAtText); }
        }

        // ====================================================================
        //  「显示修改日期」开关（2026-11-15 用户定稿：有些用户可能不喜欢）
        // ====================================================================

        /// <summary>
        /// 文件列表是否显示修改日期。**静态**：一行一个开关，全部行同进退；
        /// 由 MainWindow 启动时从 Settings 同步、设置页切换时更新并逐行刷新。
        /// </summary>
        public static bool ShowDatesInList = true;

        /// <summary>开关切换后让这一行的第二行文案重新计算（外部触发刷新用）。</summary>
        public void RefreshDateLine()
        {
            Raise(nameof(DateLine)); Raise(nameof(HasDate));
        }

        /// <summary>
        /// 列表右侧显示的大小文案。
        ///
        /// 【2026-10-03】原来文件夹直接显示接口给的 <c>Size</c>，而夸克对目录**恒返回 0**
        /// → 所有文件夹都是「0 B」（用户反馈「文件夹右边显示文件大小一直是 0 B」）。
        /// 现在文件夹改显示后台递归求和的结果；没算出来时显示「计算中…」而不是 0 B。
        /// </summary>
        public string SizeText
        {
            get
            {
                if (!IsDir)
                    return FormatSize(Size);
                if (_dirSizeFailed)
                    return "—";
                if (_dirSize == null)
                    return "计算中…";
                return FormatSize(_dirSize.Value);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }

        private static string FormatSize(long n)
        {
            // 【2026-11-10 改回 1024 进制】与 Util.FormatSize 保持完全一致。
            // 这里是文件列表右侧那一列，用户拿它跟资源管理器逐个核对字面数字，
            // 用 1000 进制会显示成 6.15 MB 而资源管理器是 5.9M（差 4~5%），
            // 看起来像下载出错。详细裁决理由见 Util.FormatSize 的注释。
            if (n < 0) n = 0;
            double v = n;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (v >= 1024 && i < units.Length - 1)
            {
                v /= 1024.0;
                i++;
            }
            return i == 0 ? n + " B" : v.ToString("F2") + " " + units[i];
        }

        // ====================================================================
        //  类型图标 / 预览判定（2026-11-15 P0）
        //
        //  设计对齐 wenxi-main 的 file_types.dart + FileGlyph（调研见
        //  「文件预览与图标方案.html」），颜色全部取 GeZi Colors.Dark.xaml
        //  的既有 token，只新增压缩包一个棕金色（#C99A5B）。
        // ====================================================================

        /// <summary>扩展名 → 图标键。命中顺序即优先级；最后一个兜底 other。</summary>
        private static readonly string[][] IconMap =
        {
            // 图标键 | 主题色 | 扩展名（小写，不含点）
            new[]{ "image",    "#8B6FE8", "jpg","jpeg","png","gif","bmp","webp","heic","heif","tif","tiff" },
            new[]{ "video",    "#14B8B8", "mp4","mkv","avi","mov","flv","ts","m3u8","m4v","webm","wmv" },
            new[]{ "audio",    "#E0698F", "mp3","m4a","aac","flac","wav","ogg","opus" },
            new[]{ "docx",     "#5B74F0", "doc","docx","rtf","odt" },
            new[]{ "xlsx",     "#10B981", "xls","xlsx","ods" },
            new[]{ "pptx",     "#EE9070", "ppt","pptx","odp" },
            new[]{ "pdf",      "#EF4444", "pdf" },
            new[]{ "zip",      "#C99A5B", "zip","rar","7z","gz","tar","iso","bz2" },
            new[]{ "code",     "#6D83F3", "js","mjs","cjs","ts","tsx","jsx","py","cs","cpp","cc","c","h","hpp",
                              "go","rs","java","kt","kts","php","rb","lua","swift","dart","sql","vue",
                              "json","xml","html","htm","svg","css","scss","ini","toml","yaml","yml",
                              "bat","cmd","ps1","sh","config","properties" },
            new[]{ "exe",      "#14B8B8", "exe","msi","msix","apk","appx" },
            new[]{ "subtitle", "#8B6FE8", "srt","ass","ssa","vtt","lrc" },
            new[]{ "torrent",  "#EE9070", "torrent" },
            new[]{ "txt",      "#8A93A6", "txt","md","markdown","log","nfo","csv","conf" },
        };

        /// <summary>按扩展名取图标键。文件夹在外层特判（不进这张表）。</summary>
        public static string IconKeyOf(string name)
        {
            var ext = GetExt(name);
            foreach (var row in IconMap)
                for (int i = 2; i < row.Length; i++)
                    if (row[i] == ext) return row[0];
            return "other";
        }

        /// <summary>图标键 → 主题色画刷（冻结，跨线程安全）。</summary>
        public static SolidColorBrush IconBrushOf(string key)
        {
            foreach (var row in IconMap)
                if (row[0] == key)
                {
                    try
                    {
                        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(row[1]));
                        b.Freeze();
                        return b;
                    }
                    catch { break; }
                }
            var gray = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA6));
            gray.Freeze();
            return gray;
        }

        /// <summary>
        /// 给条目补齐图标属性（构建 ShareFileItem 时调用一次）。
        /// 文件夹 → folder 键；颜色照常返回（XAML 里文件夹分支固定用 WarningBrush，此值不用）。
        /// </summary>
        public static void ApplyIcon(ShareFileItem item)
        {
            if (item == null) return;
            item.IconKey = item.IsDir ? "folder" : IconKeyOf(item.Name);
            item.IconBrush = IconBrushOf(item.IconKey);
        }

        /// <summary>取小写扩展名（无点 → 空串）。</summary>
        private static string GetExt(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int dot = name.LastIndexOf('.');
            if (dot < 0 || dot == name.Length - 1) return "";
            var ext = name.Substring(dot + 1).ToLowerInvariant();
            // 复合扩展（.tar.gz 之类）取最后一段就够用了。
            return ext;
        }

        // ==================== 点击预览判定（2026-11-15 用户定稿） ====================
        //
        // 用户原话：「对于图片和txt 直接点击 就是预览即可，只在s多选模式下才是选中」。
        // P0 范围：**图片 + 文本类**点击预览；docx/zip/pdf 等点击仍是默认选中行为
        //（docx 内容提取是 P1）。webp/heic 等 WPF 解不了的照片格式归「其他」不预览。

        /// <summary>可点击预览的**图片**扩展名（WPF 原生解码器覆盖面）。</summary>
        private static readonly string[] PreviewImageExts =
            { "jpg", "jpeg", "png", "gif", "bmp" };

        /// <summary>可点击预览的**文本**扩展名（按行文本拉前 256KB 显示）。</summary>
        private static readonly string[] PreviewTextExts =
        {
            "txt", "md", "markdown", "log", "nfo", "csv", "conf",
            "srt", "ass", "ssa", "vtt", "lrc",
            "ini", "json", "xml", "html", "htm", "svg", "css", "scss",
            "js", "mjs", "cjs", "ts", "tsx", "jsx", "py", "cs", "cpp", "cc", "c", "h", "hpp",
            "go", "rs", "java", "kt", "php", "rb", "lua", "swift", "dart", "sql", "vue",
            "bat", "cmd", "ps1", "sh", "yaml", "yml", "toml", "properties", "config",
        };

        /// <summary>预览种类。None = 点击保持默认（选中）。</summary>
        public enum PreviewKind { None, Image, Text }

        /// <summary>该文件点击后应进入哪种预览。</summary>
        public static PreviewKind PreviewKindOf(string name)
        {
            var ext = GetExt(name);
            foreach (var e in PreviewImageExts) if (e == ext) return PreviewKind.Image;
            foreach (var e in PreviewTextExts) if (e == ext) return PreviewKind.Text;
            return PreviewKind.None;
        }
    }
}
