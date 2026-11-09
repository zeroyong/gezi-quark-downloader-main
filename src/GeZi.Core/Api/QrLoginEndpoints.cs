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

namespace GeZi.Core.Api
{
    /// <summary>
    /// 扫码登录端点集合（2026-11-02 新增，为 UC 复用夸克的扫码流程而抽出来）。
    ///
    /// <para>【为什么要抽象】夸克与 UC 的扫码接口<b>路径完全一致</b>，只有三处不同：
    /// 域名、<c>client_id</c>、扫码落地页。原来 <see cref="LoginQrService"/> 里这些全是
    /// 硬编码的 <c>QuarkConstants</c>，加 UC 就得复制一整个类。
    /// 抽出这个类后，两套配置走同一份逻辑。</para>
    ///
    /// <para>【实测依据 2026-11-02】
    /// 夸克：<c>uop.quark.cn/cas/ajax/getTokenForQrcodeLogin</c>（client_id=532）
    /// UC：  <c>api.open.uc.cn/cas/ajax/getTokenForQrcodeLogin</c>（client_id=360）
    /// 两者响应结构相同：<c>{"status":2000000,"data":{"members":{"token":"..."}}}</c>。</para>
    /// </summary>
    public sealed class QrLoginEndpoints
    {
        /// <summary>取二维码 token 的接口。</summary>
        public string GetTokenUrl { get; set; }

        /// <summary>轮询 service_ticket 的接口。</summary>
        public string GetTicketUrl { get; set; }

        /// <summary>扫码落地页基址（拼 <c>?token=</c> 后生成二维码内容）。</summary>
        public string ScanBase { get; set; }

        /// <summary>客户端 id（夸克 532 / UC 360）。</summary>
        public string ClientId { get; set; }

        /// <summary>
        /// 用 service_ticket 激活登录态的入口（拼 <c>?st=</c>）。
        /// <para>夸克走 <c>uop.quark.cn/cas/login</c>；UC 走 <c>api.open.uc.cn/cas/login</c>。</para>
        /// </summary>
        public string CasLoginUrl { get; set; }

        /// <summary>
        /// 扫码 URL 附加的查询参数（不含 token）。
        /// <para>夸克需要 <c>ssb=weblogin</c> 与一段 <c>uc_biz_str</c>；
        /// UC 实测同样格式可用。</para>
        /// </summary>
        public string ScanExtraQuery { get; set; }

        /// <summary>平台显示名（日志与错误提示用）。</summary>
        public string PlatformName { get; set; }

        /// <summary>夸克扫码端点。</summary>
        public static QrLoginEndpoints Quark()
        {
            return new QrLoginEndpoints
            {
                PlatformName = "夸克",
                GetTokenUrl = "https://uop.quark.cn/cas/ajax/getTokenForQrcodeLogin",
                GetTicketUrl = "https://uop.quark.cn/cas/ajax/getServiceTicketByQrcodeToken",
                ScanBase = "https://su.quark.cn/4_eMHBJ",
                ClientId = "532",
                CasLoginUrl = "https://uop.quark.cn/cas/login",
                ScanExtraQuery =
                    "&ssb=weblogin&uc_param_str="
                    + "&uc_biz_str=S:custom|OPT:SAREA@0|OPT:IMMERSIVE@1|OPT:BACK_BTN_STYLE@0",
            };
        }

        /// <summary>UC 扫码端点（2026-11-02 实测确认）。</summary>
        public static QrLoginEndpoints Uc()
        {
            return new QrLoginEndpoints
            {
                PlatformName = "UC",
                GetTokenUrl = "https://api.open.uc.cn/cas/ajax/getTokenForQrcodeLogin",
                GetTicketUrl = "https://api.open.uc.cn/cas/ajax/getServiceTicketByQrcodeToken",
                ScanBase = "https://su.uc.cn/4_eMHBJ",
                ClientId = "360",
                CasLoginUrl = "https://api.open.uc.cn/cas/login",
                ScanExtraQuery =
                    "&ssb=weblogin&uc_param_str="
                    + "&uc_biz_str=S:custom|OPT:SAREA@0|OPT:IMMERSIVE@1|OPT:BACK_BTN_STYLE@0",
            };
        }
    }
}
