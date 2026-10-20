// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
// This program is free software; you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation; either version 3 of the License, or
// (at your option) any later version.
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Net;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 出站连接的网络开关（进程级，静态）。
    ///
    /// 程序默认**强制直连**：QuarkSession 与 SegmentedDownloader 构造
    /// HttpClientHandler 时显式 Proxy = null、UseProxy = false。
    /// 理由：本机代理软件常把夸克接口劫持到 127.0.0.1；代理还会改变 TLS ClientHello
    /// 指纹，而 pan.quark.cn 的 WAF 对指纹敏感。
    ///
    /// 但公司网络 / 只能经代理出口的环境里直连根本出不去，所以提供这个显式开关，
    /// 由设置页「跟随系统代理」控制（AppSettings.UseSystemProxy）。
    /// ⚠️ 默认 false = 直连，与旧版本行为完全一致。
    /// ⚠️ 只影响**此后新建**的 HttpClientHandler：改完要重新登录/新建任务才生效。
    /// </summary>
    public static class NetworkOptions
    {
        private static volatile bool _useSystemProxy;

        /// <summary>是否跟随 Windows 系统代理（WinINET 设置，含 PAC 脚本）。</summary>
        public static bool UseSystemProxy
        {
            get { return _useSystemProxy; }
            set { _useSystemProxy = value; }
        }

        /// <summary>
        /// 按当前开关给 handler 配置代理。所有创建 HttpClientHandler 的地方都应调它，
        /// 保证 API 层与下载层行为一致（否则会出现"能解析但下不动"）。
        /// </summary>
        public static void ApplyTo(System.Net.Http.HttpClientHandler handler)
        {
            if (handler == null) return;

            if (!_useSystemProxy)
            {
                handler.Proxy = null;
                handler.UseProxy = false;
                return;
            }

            try
            {
                var proxy = WebRequest.GetSystemWebProxy();
                if (proxy == null)
                {
                    handler.Proxy = null;
                    handler.UseProxy = false;
                    return;
                }

                // 企业代理常要身份验证；需要时带上当前 Windows 凭据
                try { proxy.Credentials = CredentialCache.DefaultCredentials; } catch { }

                handler.Proxy = proxy;
                handler.UseProxy = true;
            }
            catch
            {
                // 取系统代理失败 → 退回直连，绝不让设置项把程序变成不可用
                handler.Proxy = null;
                handler.UseProxy = false;
            }
        }
    }
}
