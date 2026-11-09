using System;

namespace GeZi.Core.Support
{
    /// <summary>
    /// UC 网盘常量（2026-11-02 依据 YunX 的 UCConstants.kt + 本机实测确认）。
    ///
    /// <para>【与夸克的关系】UC 与夸克共用同一套 API 结构，源码注释原话：
    /// 「与夸克 API 结构一致，仅域名/UA/pr 参数不同」。
    /// 所以这里的路径大多与 <see cref="QuarkConstants"/> 一一对应，
    /// 差异只有三处：域名、查询参数 pr=UCBrowser、UA。</para>
    ///
    /// <para>【实测确认】2026-11-02 用真实分享链接跑通全链路：
    /// token → transfer_share/detail → file/download → Range 下载 206。</para>
    /// </summary>
    public static class UcConstants
    {
        /// <summary>业务 API 基础域名（与夸克的 drive-pc.quark.cn 对应）。</summary>
        public const string ApiBase = "https://pc-api.uc.cn";

        /// <summary>官方 Web 客户端域名（登录页、Referer 基准）。</summary>
        public const string WebOrigin = "https://drive.uc.cn";

        /// <summary>
        /// ★ 下载 OSS 直链必须携带的 Referer。
        /// <para>实测：不带它、只带 __pugs 时直链可能被 CDN 限速；
        /// 带上是稳妥做法（YunX 注释说缺它会被 Callback 限速到 ~100KB/s）。</para>
        /// </summary>
        public const string DownloadReferer = WebOrigin + "/";

        /// <summary>WebView 登录页。</summary>
        public const string LoginUrl = WebOrigin + "/";

        /// <summary>提取 Cookie 的域名。</summary>
        public const string CookieDomain = WebOrigin;

        /// <summary>验证登录状态 / 取昵称的接口（游客态返回 data 为空对象）。</summary>
        public const string AccountInfoUrl = WebOrigin + "/account/info";

        /// <summary>
        /// ★ Cookie 种子归属域。
        /// <para>【2026-11-02 关键修复】<see cref="QuarkSession"/> 原来把 Cookie 硬塞进
        /// <c>.quark.cn</c>，导致 UC 的 Cookie 发往 <c>pc-api.uc.cn</c> 时不匹配域、
        /// 一个都不附带 → 请求实际是游客态 → 报「Cookie 无效或已过期」。
        /// 所以 UC 必须用自己的域。</para>
        /// </summary>
        public const string CookieSeedDomain = ".uc.cn";

        /// <summary>普通浏览器 UA（登录态链路）。</summary>
        public const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        /// <summary>
        /// 游客态 UA（uc-cloud-drive 的 Electron 客户端）。
        /// <para>YunX 的注释说游客链路风控更严，用这个 UA 更稳。
        /// 实测普通浏览器 UA 也能走通游客链路，但保留此常量供需要时切换。</para>
        /// </summary>
        public const string GuestUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "uc-cloud-drive/2.5.20 Chrome/100.0.4896.160 Electron/18.3.5.4-b478491100 " +
            "Safari/537.36 Channel/pckk_other_ch";

        /// <summary>与 GuestUserAgent 配套的 Sec-Ch-Ua。</summary>
        public const string GuestSecChUa =
            "\"Not=A?Brand\";v=\"99\", \"Chromium\";v=\"100\", \"Google Chrome\";v=\"100\"";

        // ---------- 分享 ----------

        /// <summary>获取分享 token。</summary>
        public const string ShareTokenUrl = ApiBase + "/1/clouddrive/share/sharepage/token?pr=UCBrowser&fr=pc";

        /// <summary>
        /// ★ 获取分享文件列表 —— 注意是 <c>transfer_share/detail</c>（GET + query），
        /// <b>不是</b> <c>sharepage/v2/detail</c>。
        /// <para>【实测踩坑 2026-11-02】用 v2/detail（POST + body）返回
        /// <c>total= count=0</c> 空列表；换成本接口立刻拿到 4 个文件。
        /// YunX 的注释也写明这是「官方下载流程实际使用的文件列表接口」。</para>
        /// </summary>
        public const string TransferShareDetailUrl =
            ApiBase + "/1/clouddrive/transfer_share/detail?entry=ft&fr=pc&pr=UCBrowser";

        // ---------- 下载 ----------

        /// <summary>获取下载直链（官方抓包：entry=ft）。</summary>
        public const string DownloadUrl = ApiBase + "/1/clouddrive/file/download?entry=ft&fr=pc&pr=UCBrowser";

        // ---------- 账号 ----------

        /// <summary>
        /// 网盘空间详情：total_capacity / use_capacity。
        /// <para>【实测 2026-11-02】与夸克完全同一个接口路径与字段名，
        /// 只是域名不同。夸克的 QuarkClient.GetCapacityAsync 可直接照搬。</para>
        /// </summary>
        public const string MemberUrl = ApiBase + "/1/clouddrive/member?pr=UCBrowser&fr=pc&fetch_subscribe=true&_ch=home";

        /// <summary>
        /// 会话刷新探测接口：发一次 GET 让服务端重新下发 __puus。
        /// <para>用法：请求时先<b>去掉</b> Cookie 里的 __puus，逼服务端下发新的。</para>
        /// </summary>
        public const string ConfigUrl = ApiBase + "/1/clouddrive/config?pr=UCBrowser&fr=pc";

        // ---------- 其他 ----------

        /// <summary>根目录 fid。</summary>
        public const string DefaultPdirFid = "0";

        /// <summary>
        /// __puus 有效期约 3 小时，提前到 90 分钟主动刷新。
        /// <para>对齐 AList 的 100±5 分钟与 YunX 的 90 分钟。</para>
        /// </summary>
        public static readonly TimeSpan PuusRefreshInterval = TimeSpan.FromMinutes(90);

        /// <summary>
        /// 判定 Cookie 是否构成"已登录"。
        /// <para>UC 与夸克共用 __pus / __puus 两个会话字段，
        /// 必须<b>同时</b>存在才算登录（YunX UCConstants.isValidCookie 的原判据）。</para>
        /// </summary>
        public static bool IsValidCookie(string cookie)
        {
            if (string.IsNullOrEmpty(cookie))
                return false;
            return cookie.IndexOf("__pus=", StringComparison.Ordinal) >= 0
                && cookie.IndexOf("__puus=", StringComparison.Ordinal) >= 0;
        }
    }
}
