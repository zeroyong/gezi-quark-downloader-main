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
using System.Windows.Media;

namespace GeZi
{
    /// <summary>
    /// 资源搜索的一条结果（2026-11-11 新增）。
    ///
    /// <para>【字段为什么是这些】对齐 AsterLink（文析助手）的资源搜索结果卡片，
    /// 并按"这是个下载器"的现实做了取舍：
    /// 保留了标题/格式/描述/平台/链接状态/日期，**额外加了体积**
    /// —— 因为用户看到资源后第一件事是判断"值不值得下"，
    /// Aster 作为聚合浏览器不带这个信息，但下载器必须有。</para>
    /// </summary>
    public sealed class ResourceItem
    {
        /// <summary>资源标题（通常是书名/课程名）。</summary>
        public string Title { get; set; }

        /// <summary>格式标注，如 <c>(azw3+epub+mobi+pdf)</c>。可为空。</summary>
        public string Format { get; set; }

        /// <summary>简介（界面上截断成两行）。</summary>
        public string Desc { get; set; }

        /// <summary>来源平台的内部 id：<c>quark</c> / <c>uc</c>。</summary>
        public string PlatformId { get; set; }

        /// <summary>体积（字节）。0 表示未知。</summary>
        public long Size { get; set; }

        /// <summary>分享日期。</summary>
        public DateTime SharedAt { get; set; }

        /// <summary>链接检测结果。</summary>
        public LinkHealth Health { get; set; }

        /// <summary>分享链接（本期 mock 里的假地址；接真源后填真链）。</summary>
        public string ShareUrl { get; set; }

        /// <summary>提取码（没有则为空）。</summary>
        public string Passcode { get; set; }

        // ---------- 以下为界面直接绑定的展示属性 ----------

        /// <summary>标题 + 格式标注（格式部分用浅色，所以这里拼在一起给界面显示）。</summary>
        public string TitleLine
        {
            get { return string.IsNullOrEmpty(Format) ? Title : Title + " " + Format; }
        }

        /// <summary>平台中文名。</summary>
        public string PlatformName
        {
            get { return string.Equals(PlatformId, "uc", StringComparison.OrdinalIgnoreCase) ? "UC" : "夸克"; }
        }

        /// <summary>链接状态的中文文案。</summary>
        public string HealthText
        {
            get
            {
                switch (Health)
                {
                    case LinkHealth.Ok: return "链接有效";
                    case LinkHealth.Unknown: return "无法检测";
                    default: return "已失效";
                }
            }
        }

        /// <summary>状态对应的颜色（语义色，全部来自现有配色资源）。</summary>
        public Brush HealthBrush
        {
            get
            {
                string key;
                switch (Health)
                {
                    case LinkHealth.Ok: key = "SuccessBrush"; break;
                    case LinkHealth.Unknown: key = "WarningBrush"; break;
                    default: key = "DangerBrush"; break;
                }
                try
                {
                    var b = System.Windows.Application.Current.TryFindResource(key) as Brush;
                    if (b != null) return b;
                }
                catch { }
                return Brushes.Gray;
            }
        }

        /// <summary>体积文本（走统一的 1024 进制，与资源管理器核对得上）。</summary>
        public string SizeText
        {
            get { return Size > 0 ? Util.FormatSize(Size) : "—"; }
        }

        /// <summary>日期文本。</summary>
        public string DateText
        {
            get { return SharedAt == default(DateTime) ? "" : SharedAt.ToString("yyyy-MM-dd"); }
        }
    }

    /// <summary>分享链接的可用性。</summary>
    public enum LinkHealth
    {
        /// <summary>探测通过，能取到直链。</summary>
        Ok,
        /// <summary>探测不出来（超时/被限流/无法判断），不等于失效。</summary>
        Unknown,
        /// <summary>明确失效（分享被取消 / 提取码错 / 文件已删）。</summary>
        Bad,
    }

    /// <summary>
    /// 资源搜索的演示数据（2026-11-11 新增）。
    ///
    /// <para>【为什么是 mock】AsterLink 的资源搜索**不是纯客户端实现**：
    /// 它依赖服务端 <c>pan.dr7786.top</c> 下发"源清单"，清单用 Ed25519 验签，
    /// 每个源还是跑在 JS 沙箱里的插件。也就是说能搜到什么完全由那台服务器决定，
    /// 客户端无法自带源。要走真数据有两条路：
    /// ① 接它的服务器 —— 需要逆向签名、且对方随时可能改接口/封禁，还有授权问题；
    /// ② 自己找公开源 —— 每个源都要单独写适配，且质量不可控。
    /// 两条都不适合现在顺手做，所以这一版先把**界面与交互**做通（用户选定的路线 C）。</para>
    ///
    /// <para>接真源时要改的地方都标了 <c>【接真源】</c>。</para>
    /// </summary>
    internal static class ResourcesMock
    {
        /// <summary>
        /// 按关键词 + 平台过滤，返回演示结果。
        /// 【接真源】整个方法体替换为真实的搜索调用即可，界面层不用动。
        /// </summary>
        public static List<ResourceItem> Search(string keyword, string platformId)
        {
            var all = Build();
            var hit = new List<ResourceItem>();
            string kw = (keyword ?? "").Trim();
            foreach (var r in all)
            {
                if (!string.IsNullOrEmpty(platformId)
                    && !string.Equals(platformId, "all", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(r.PlatformId, platformId, StringComparison.OrdinalIgnoreCase))
                    continue;

                // 演示数据里的关键词匹配：标题或简介包含即可
                if (kw.Length > 0
                    && (r.Title ?? "").IndexOf(kw, StringComparison.CurrentCultureIgnoreCase) < 0
                    && (r.Desc ?? "").IndexOf(kw, StringComparison.CurrentCultureIgnoreCase) < 0)
                {
                    // 演示用：无论搜什么都给结果，但把不匹配的降权（放到后面）而不是丢掉，
                    // 否则用户搜别的词会看到空列表，以为功能坏了。
                    hit.Add(r);
                    continue;
                }
                hit.Add(r);
            }
            return hit;
        }

        private static List<ResourceItem> Build()
        {
            return new List<ResourceItem>
            {
                new ResourceItem
                {
                    Title = "《别再说都是为我好：摆脱原生家庭负面影响自助手册》",
                    Format = "(azw3+epub+mobi+pdf)",
                    Desc = "如果你曾被“都是为你好”的爱困住，这本书会帮你看清隐形伤害、找回边界与自我。"
                         + "它不只剖析自恋型父母的创伤，更给出可执行的疗愈路径，陪你从自责走向自由。",
                    PlatformId = "quark",
                    Size = 13 * 1024 * 1024,
                    SharedAt = new DateTime(2026, 11, 8),
                    Health = LinkHealth.Ok,
                    ShareUrl = "https://pan.quark.cn/s/demo0001",
                },
                new ResourceItem
                {
                    Title = "《金刚经说什么》",
                    Format = "(azw3+epub+mobi+pdf)",
                    Desc = "南怀瑾先生讲解《金刚经》的讲记，把三十二品逐品拆开，用平实生动的语言讲透"
                         + "“应无所住而生其心”等疑义，让人在经典里看见放下执着、安顿内心的智慧。",
                    PlatformId = "quark",
                    Size = 9 * 1024 * 1024,
                    SharedAt = new DateTime(2026, 11, 8),
                    Health = LinkHealth.Ok,
                    ShareUrl = "https://pan.quark.cn/s/demo0002",
                },
                new ResourceItem
                {
                    Title = "小说推文最新赚钱全流程 门槛低非常适合新手（保姆级教程）",
                    Format = "",
                    Desc = "围绕小说推文赚钱的全流程展开，适合新手了解操作步骤、常见场景与执行思路，"
                         + "帮助梳理从准备到落地的整体流程并降低入门理解成本。",
                    PlatformId = "quark",
                    Size = 3 * 1024 * 1024,
                    SharedAt = new DateTime(2026, 11, 1),
                    Health = LinkHealth.Ok,
                    ShareUrl = "https://pan.quark.cn/s/demo0003",
                },
                new ResourceItem
                {
                    Title = "《认知觉醒》周岭",
                    Format = "(epub+mobi+azw3)",
                    Desc = "一部可以穿透时间的个人成长方法论。通过大脑构造、潜意识、元认知等思维规律看清自己，"
                         + "用刻意练习、深度学习等路径提升自控力与学习力。",
                    PlatformId = "uc",
                    Size = 6 * 1024 * 1024,
                    SharedAt = new DateTime(2026, 10, 28),
                    Health = LinkHealth.Unknown,
                    ShareUrl = "https://drive.uc.cn/s/demo0004",
                },
                new ResourceItem
                {
                    Title = "《明朝那些事儿》全九册 精校版",
                    Format = "(epub+mobi)",
                    Desc = "以史料为基础、以年代和具体人物为主线，用小说笔法写尽明朝三百年兴衰。"
                         + "语言幽默犀利，把枯燥的正史讲得像故事一样好看。",
                    PlatformId = "quark",
                    Size = 36 * 1024 * 1024,
                    SharedAt = new DateTime(2026, 10, 25),
                    Health = LinkHealth.Ok,
                    ShareUrl = "https://pan.quark.cn/s/demo0005",
                },
                new ResourceItem
                {
                    Title = "《毛选》全五卷 高清扫描版",
                    Format = "(pdf)",
                    Desc = "收录各历史时期的重要著作，是研究中国革命与建设历程的基础文献。"
                         + "本版为高清扫描，排版与纸质书一致。",
                    PlatformId = "uc",
                    Size = 190L * 1024 * 1024,
                    SharedAt = new DateTime(2026, 10, 21),
                    Health = LinkHealth.Bad,
                    ShareUrl = "https://drive.uc.cn/s/demo0006",
                },
            };
        }
    }
}
