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
using GeZi.Core.Support;

namespace GeZi.Core.Download
{
    /// <summary>
    /// 下载层的**身份上下文**（2026-11-02 新增，网盘抽象 P0）。
    ///
    /// <para>【为什么需要它】在此之前 <see cref="SegmentedDownloader"/> 的所有身份字段
    /// 都硬编码夸克：<c>Origin</c> 来自 <see cref="QuarkConstants.Origin"/>、
    /// 种子 Cookie 的域写死 <c>.quark.cn</c>、降级头的 UA/Referer 也全是夸克常量。
    /// 于是 **UC 的下载实际带着夸克身份** —— 而 UC 直链必须带 <c>__pugs</c> +
    /// <c>Referer: https://drive.uc.cn/</c>，缺了会被 OSS 直接 403。
    /// 这不是"某处漏传"，是下载器结构性地只认识夸克。</para>
    ///
    /// <para>【定位】本类型是下载层与"具体是哪个网盘"之间**唯一的耦合面**。
    /// 下载器只认它，不认识任何平台。新增网盘时只需由对应 provider 造一个实例，
    /// 下载器本身一行不用改 —— 这是整个网盘抽象的地基。</para>
    /// </summary>
    public sealed class DownloadContext
    {
        /// <summary>下载时使用的 User-Agent。空则回退夸克常量（兼容老调用点）。</summary>
        public string UserAgent { get; set; }

        /// <summary>Referer。部分 CDN 会对缺失 Referer 的请求限速或拒绝。</summary>
        public string Referer { get; set; }

        /// <summary>
        /// Origin。可与 <see cref="Referer"/> 不同：
        /// UC 是 <c>https://drive.uc.cn</c>，夸克是 <c>https://pan.quark.cn</c>。
        /// </summary>
        public string Origin { get; set; }

        /// <summary>
        /// 完整 Cookie 串（会显式下发到请求头，同时种进 CookieContainer）。
        ///
        /// <para>⚠️ 必须<b>同时</b>两件事都做：只靠 CookieContainer 隐式附加并不可靠 ——
        /// 它按域/路径过滤，而直链域名（如 <c>dl-pc-zb.drive.quark.cn</c>、
        /// <c>*.uc.cn</c> 的 OSS 域）与种子域未必总能匹配上。</para>
        /// </summary>
        public string Cookie { get; set; }

        /// <summary>
        /// 种子 Cookie 的归属域：夸克 <c>.quark.cn</c>、UC <c>.uc.cn</c>。
        ///
        /// <para>★ 这个字段就是 2026-11-02 那个「UC Cookie 明明有效却报过期」bug 的根因。
        /// 留空则回退 <c>.quark.cn</c>（保持老调用点行为）。</para>
        /// </summary>
        public string CookieSeedDomain { get; set; }

        /// <summary>
        /// 额外请求头（网盘特有）。典型用法：
        /// <list type="bullet">
        ///   <item>UC 的 <c>__pugs</c> —— 必须放进 Cookie 头，且与本次直链同响应绑定；</item>
        ///   <item>迅雷等平台的定制 <c>Sec-*</c> 或签名头。</item>
        /// </list>
        /// 键名不区分大小写地覆盖默认头（与 <see cref="UserAgent"/> 等同名时会覆盖）。
        /// </summary>
        public IDictionary<string, string> ExtraHeaders { get; set; }

        /// <summary>
        /// 降级时要依次尝试的 UA 列表（可选）。原 <c>FallbackHeaders()</summary>
        /// 在没有候选时退化为内置默认；给了它就用这里的。
        /// </summary>
        public IList<string> FallbackUserAgents { get; set; }

        /// <summary>
        /// 该网盘「并发上限」（0 = 不限）。迅雷实测限 8 路，夸克/UC 可到 16+。
        /// 交给 provider 声明，避免下载器自己猜。
        /// </summary>
        public int MaxConcurrency { get; set; }

        /// <summary>
        /// 是否**关闭** CookieContainer 的自动装配（2026-11-02 实测，UC 必须为 true）。
        ///
        /// <para>【实测数据（真实 UC 直链 + 真登录 Cookie）】
        /// 同样的头集合，只有一处不同：
        /// <list type="bullet">
        ///   <item><c>UseCookies=true</code>（HttpClient 默认）→ <b>HTTP 403</b>，
        ///         OSS 原文 <c>RequestDeniedByCallback / Callback deny this request reason:
        ///         require login [auth not found]</code>；</item>
        ///   <item><c>UseCookies=false</c> → <b>HTTP 206</b>，正常出数据。</item>
        /// </list>
        /// 原因：<c>UseCookies=true</c> 时 .NET 会用 handler 自己的 CookieContainer **覆盖**
        /// 手工写进请求头的 <c>Cookie</c> —— 而那个容器可能根本没收到这份 Cookie
        /// （域匹配/移植时机问题），于是发出去的请求**没有登录态**。
        /// 手写完整 Cookie 头才是可靠路径（YunX 的 OkHttp 实现也是显式 header("Cookie")）。</para>
        ///
        /// <para>所以：让它为 true 时，<b>必须</b>自己把 Cookie 种进容器（见 SeedCookies），
        /// 并且不能再依赖请求头里的那份。默认 false —— 与 YunX 行为一致、也最稳。</para>
        /// </summary>
        public bool DisableCookieJar { get; set; }

        public DownloadContext()
        {
            ExtraHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 用夸克默认值造一个上下文 —— 让**既有调用点零改动**。
        /// <para>这与以前 <c>new SegmentedDownloader(ua: null, referer: null)</c>
        /// 的隐含行为完全一致，所以老路径（未改造的调用方）表现分毫不差。</para>
        /// </summary>
        public static DownloadContext QuarkDefaults(string cookie)
        {
            return new DownloadContext
            {
                UserAgent = QuarkConstants.DlUa,
                Referer = QuarkConstants.Referer,
                Origin = QuarkConstants.Origin,
                Cookie = cookie ?? "",
                CookieSeedDomain = ".quark.cn",
            };
        }

        /// <summary>
        /// 造 UC 的上下文。
        /// <para>关键差异：Origin/Referer 都是 <c>drive.uc.cn</c>，种子域 <c>.uc.cn</c>。</para>
        /// <param name="pugs">
        /// 取链响应里捞到的 <c>__pugs</c>。它会以 <c>__pugs=值</c> 的形式并入 Cookie 串 —— 
        /// 缺了它，UC 的直链请求一律 403（2026-11-02 实测）。
        /// </param>
        /// </summary>
        public static DownloadContext UcDefaults(string cookie, string pugs = null)
        {
            var ctx = new DownloadContext
            {
                UserAgent = UcConstants.UserAgent,
                Referer = UcConstants.DownloadReferer,
                Origin = UcConstants.WebOrigin,
                Cookie = cookie ?? "",
                CookieSeedDomain = UcConstants.CookieSeedDomain,
                // 实测：开着 CookieContainer 会把手写的 Cookie 头覆盖掉 → OSS 403
                DisableCookieJar = true,
            };

            if (!string.IsNullOrEmpty(pugs))
                ctx.Cookie = CookieText.SetField(ctx.Cookie, "__pugs", pugs);

            return ctx;
        }

        /// <summary>把上下文自身归一化成一份可直接下发的头字典（不含 Cookie）。</summary>
        public Dictionary<string, string> BuildBaseHeaders()
        {
            var h = new Dictionary<string, string>
            {
                { "User-Agent", string.IsNullOrEmpty(UserAgent) ? QuarkConstants.DlUa : UserAgent },
                { "Referer", string.IsNullOrEmpty(Referer) ? QuarkConstants.Referer : Referer },
                { "Origin", string.IsNullOrEmpty(Origin) ? QuarkConstants.Origin : Origin },
            };
            if (ExtraHeaders != null)
            {
                foreach (var kv in ExtraHeaders)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    h[kv.Key] = kv.Value ?? "";
                }
            }
            return h;
        }
    }
}
