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
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 出站请求的会话层：统一请求头指纹、Cookie 载体、节流节奏和连接复用。
    ///
    /// 设计目标不是"伪装成官方客户端"，而是**让请求头完整且前后一致**——
    /// 一个真实的浏览器/网页端请求本来就带这一整套头，缺项或不一致才是异常特征。
    /// 同时统一节流与退避，避免因瞬时高频重试被服务端按可疑流量处理。
    ///
    /// 关键点：
    /// - 所有请求共用同一份浏览器标准头（含 Sec-Fetch-*、Accept-Language 等）
    /// - 请求间加入随机化最小间隔，避免固定频率的机器节奏
    /// - Cookie 始终从 CookieContainer 实时读取，保证 Set-Cookie 更新立即生效
    /// - HttpClient 复用，连接保活，减少 TCP/TLS 握手带来的额外特征
    /// </summary>
    public sealed class QuarkSession : IDisposable
    {
        private readonly HttpClient _http;
        private readonly HttpClientHandler _handler;
        private readonly CookieContainer _jar;
        private readonly SemaphoreSlim _gate;

        private DateTime _lastRequestAt = DateTime.MinValue;
        private readonly object _throttleLock = new object();

        /// <summary>两次请求之间的最小间隔（毫秒）。过低会被视为机器流量，过高影响体验。</summary>
        public int MinIntervalMs { get; set; } = 260;

        public CookieContainer Jar => _jar;
        public HttpClient Http => _http;

        public QuarkSession(string cookie = null, DecompressionMethods decompression = DecompressionMethods.None)
        {
            _jar = new CookieContainer { Capacity = 500, PerDomainCapacity = 200 };
            if (!string.IsNullOrEmpty(cookie))
                SeedCookies(cookie);

            _handler = new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = _jar,
                AllowAutoRedirect = true,
                AutomaticDecompression = decompression,
                // 复用连接：保持 TCP/TLS 会话，减少重复握手
                MaxConnectionsPerServer = 128,
                // 默认不读 Windows 系统代理(WinINET)。原因：
                //   1) 与 Python Flet 版行为一致（urllib 不读系统代理）；
                //   2) 本机代理软件常把请求劫持到 127.0.0.1，导致夸克接口失败；
                //   3) 代理会改变 TLS ClientHello，而 pan.quark.cn 的 WAF
                //      对指纹敏感，走代理可能直接被拒。
                // 需要走代理的用户可设 UseSystemProxy = true（在构造后、首次请求前）。
                Proxy = null,
                UseProxy = false,
            };
            _http = new HttpClient(_handler) { Timeout = Timeout.InfiniteTimeSpan };
            _gate = new SemaphoreSlim(1, 1);
        }

        // ---------------- Cookie ----------------

        private static readonly string[] CookieDomains =
        {
            "https://pan.quark.cn", "https://drive-pc.quark.cn", "https://drive-m.quark.cn",
            "https://drive.quark.cn", "https://drive-h.quark.cn", "https://uop.quark.cn",
            "https://su.quark.cn", "https://quark.cn",
        };

        public static Dictionary<string, string> ParseCookies(string s)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(s))
                return d;
            foreach (var part in s.Split(';'))
            {
                var p = part.Trim();
                if (p.Length == 0)
                    continue;
                int eq = p.IndexOf('=');
                if (eq <= 0)
                    continue;
                var k = p.Substring(0, eq).Trim();
                var v = p.Substring(eq + 1).Trim();
                if (k.Length > 0)
                    d[k] = v;
            }
            return d;
        }

        public void SeedCookies(string cookie)
        {
            foreach (var kv in ParseCookies(cookie))
            {
                try
                {
                    _jar.Add(new Cookie(kv.Key, kv.Value, "/", ".quark.cn"));
                }
                catch
                {
                    // 单个 Cookie 格式异常不影响整体
                }
            }
        }

        /// <summary>按域汇总 Cookie，后者覆盖前者，得到可下发给接口的整串。</summary>
        public string BuildCookieString()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in CookieDomains)
            {
                CookieCollection cookies;
                try
                {
                    cookies = _jar.GetCookies(new Uri(d));
                }
                catch
                {
                    continue;
                }
                foreach (Cookie c in cookies)
                    dict[c.Name] = c.Value;
            }
            var sb = new StringBuilder();
            foreach (var kv in dict)
            {
                if (sb.Length > 0)
                    sb.Append("; ");
                sb.Append(kv.Key).Append('=').Append(kv.Value);
            }
            return sb.ToString();
        }

        public bool HasCookie(string name)
        {
            foreach (var d in CookieDomains)
            {
                try
                {
                    foreach (Cookie c in _jar.GetCookies(new Uri(d)))
                        if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                            return true;
                }
                catch { }
            }
            return false;
        }

        // ---------------- 请求头指纹 ----------------

        /// <summary>
        /// 构造一套完整、自洽的浏览器标准请求头。
        /// 「完整」是重点：真实浏览器不会只带 UA 和 Referer，
        /// 缺项本身就是可疑特征。这里把常规头补齐并保持前后一致。
        /// </summary>
        public static void ApplyHeaders(HttpRequestMessage req, string url, bool api = true)
        {
            var ua = api ? QuarkConstants.ApiUa : QuarkConstants.DlUa;
            Set(req, "User-Agent", ua);
            Set(req, "Accept", api
                ? "application/json, text/plain, */*"
                : "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
            Set(req, "Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            Set(req, "Referer", QuarkConstants.Referer);
            Set(req, "Origin", "https://pan.quark.cn");
            Set(req, "Cache-Control", "no-cache");
            Set(req, "Pragma", "no-cache");
            Set(req, "DNT", "1");
            Set(req, "Connection", "keep-alive");

            // Sec-Fetch-* 是现代浏览器必发的一组头，缺失是明显的非浏览器特征
            bool sameOrigin = url != null && url.IndexOf("pan.quark.cn", StringComparison.OrdinalIgnoreCase) >= 0;
            Set(req, "Sec-Fetch-Dest", api ? "empty" : "document");
            Set(req, "Sec-Fetch-Mode", api ? "cors" : "navigate");
            Set(req, "Sec-Fetch-Site", sameOrigin ? "same-origin" : "same-site");
        }

        private static void Set(HttpRequestMessage req, string name, string value)
        {
            if (string.IsNullOrEmpty(value))
                return;
            req.Headers.TryAddWithoutValidation(name, value);
        }

        // ---------------- 节流 ----------------

        /// <summary>
        /// 请求前的节流等待：保证两次请求之间至少间隔 MinIntervalMs（带随机抖动）。
        /// 目的是避免固定频率的机器节奏，同时不至于让用户明显感到卡顿。
        /// </summary>
        public async Task ThrottleAsync(CancellationToken cancel = default)
        {
            int wait;
            lock (_throttleLock)
            {
                var now = DateTime.UtcNow;
                var elapsed = (now - _lastRequestAt).TotalMilliseconds;
                var jitter = 0.75 + 0.5 * new Random(Guid.NewGuid().GetHashCode()).NextDouble();
                double need = MinIntervalMs * jitter - elapsed;
                wait = need > 0 ? (int)need : 0;
                _lastRequestAt = now.AddMilliseconds(Math.Max(0, wait));
            }
            if (wait > 0)
                await Task.Delay(wait, cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// 带指数退避的请求执行。遇到可重试状态码（5xx / 408 / 429）时退避重试，
        /// 退避时间带随机抖动，避免所有分片同一时刻重试形成尖峰。
        /// </summary>
        public async Task<HttpResponseMessage> SendWithRetryAsync(
            Func<HttpRequestMessage> requestFactory, int maxAttempts = 3,
            CancellationToken cancel = default, Action<int> onRetry = null)
        {
            Exception last = null;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                cancel.ThrowIfCancellationRequested();
                await ThrottleAsync(cancel).ConfigureAwait(false);

                HttpRequestMessage req = null;
                HttpResponseMessage resp = null;
                try
                {
                    req = requestFactory();
                    resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel)
                                    .ConfigureAwait(false);

                    if (IsRetryableStatus((int)resp.StatusCode) && attempt < maxAttempts - 1)
                    {
                        onRetry?.Invoke((int)resp.StatusCode);
                        resp.Dispose();
                        req.Dispose();
                        await BackoffAsync(attempt, cancel).ConfigureAwait(false);
                        continue;
                    }
                    return resp;
                }
                catch (OperationCanceledException)
                {
                    resp?.Dispose();
                    req?.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    resp?.Dispose();
                    req?.Dispose();
                    if (attempt < maxAttempts - 1)
                    {
                        await BackoffAsync(attempt, cancel).ConfigureAwait(false);
                        continue;
                    }
                }
            }
            throw new HttpRequestException("请求失败(已重试 " + maxAttempts + " 次): " + (last?.Message ?? "未知错误"), last);
        }

        private static bool IsRetryableStatus(int code)
            => code == 408 || code == 429 || (code >= 500 && code < 600);

        /// <summary>指数退避 + 随机抖动：0.6s, 1.4s, 3.0s ...（带 ±40% 抖动）</summary>
        private static async Task BackoffAsync(int attempt, CancellationToken cancel)
        {
            double baseMs = 600 * Math.Pow(2.2, attempt);
            double jitter = 0.6 + 0.8 * new Random(Guid.NewGuid().GetHashCode()).NextDouble();
            int ms = (int)Math.Min(baseMs * jitter, 15000);
            await Task.Delay(ms, cancel).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _http?.Dispose();
            _handler?.Dispose();
            _gate?.Dispose();
        }
    }
}
