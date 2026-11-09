using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using GeZi.Core.Models;
using GeZi.Core.Support;

namespace GeZi.Core.Api
{
    /// <summary>
    /// UC 网盘客户端（2026-11-02 新增，2.9.0）。
    ///
    /// <para>【为什么能复用夸克那套】UC 与夸克共用同一套 API 结构 ——
    /// YunX 源码注释原话：「与夸克 API 结构一致，仅域名/UA/pr 参数不同」。
    /// 所以本类刻意与 <see cref="QuarkClient"/> 保持相同的字段名与方法形态，
    /// 差别只在三处：域名、查询参数 <c>pr=UCBrowser</c>、以及 <b>__pugs</b> 的处理。</para>
    ///
    /// <para>【UC 相对夸克唯一真正新增的复杂度：__pugs】
    /// 取下载直链的响应会随 Set-Cookie 下发一个游客态 <c>__pugs</c>（约 3 小时有效），
    /// 它<b>与本次直链同响应绑定</b>。下载时缺它 → OSS 直接 403，
    /// 而且不管带不带 Referer 都一样（2026-11-02 实测）。
    /// 所以：<see cref="GetDownloadLinkAsync"/> 必须把 __pugs 一并返回，
    /// 调用方下载时把它放进 Cookie 头。</para>
    ///
    /// <para>【实测确认】2026-11-02 用真实分享链接跑通：
    /// token → transfer_share/detail → file/download → Range 206。</para>
    /// </summary>
    public class UcClient : IDisposable
    {
        /// <summary>
        /// 复用 <see cref="QuarkSession"/> 的 HttpClient / CookieJar 管理。
        /// <para>理由：它已经把 .NET Framework 那堆坑（连接池、KeepAlive、TLS、代理）
        /// 调好了，UC 与夸克同为 HTTP API，没有任何理由再造一套。
        /// 这也是"UC 与夸克同构"在代码层面的直接体现。</para>
        /// </summary>
        private readonly QuarkSession _session;
        private string _accountSig;
        private string _avatarUrl;

        /// <summary>最近一次取链下发的 __pugs（供后续同分享的下载复用；换直链时要一起换）。</summary>
        private string _lastPugs = "";

        /// <summary>上次主动刷新 __puus 的时刻（UTC）。</summary>
        private DateTime _lastPuusRefreshUtc = DateTime.MinValue;

        public UcClient(string cookie)
        {
            // ⚠️ 必须传 UC 的 Cookie 域！默认构造走的是硬编码 .quark.cn，
            // UC 的 Cookie 会被存进夸克域 → 发往 pc-api.uc.cn 时不附带 → 一直游客态。
            _session = new QuarkSession(CookieText.Parse(cookie), UcConstants.CookieSeedDomain, default(System.Net.DecompressionMethods));
        }

        /// <summary>当前 Cookie（含服务端 Set-Cookie 更新后的最新值）。</summary>
        public string Cookie => _session.BuildCookieString();

        /// <summary>登录态是否仍然存在（UC 与夸克一样，关键字段是 __puus）。</summary>
        public bool IsLoggedIn => _session.HasCookie("__puus");

        /// <summary>账号头像 URL（探测得到才有，否则 null）。</summary>
        public string AvatarUrl => _avatarUrl;

        /// <summary>账号稳定标识（uid / account_id），用于账号去重。</summary>
        public string AccountSig => _accountSig ?? "";

        // ==================== 底层请求 ====================

        private HttpRequestMessage Build(HttpMethod method, string url, string body = null, bool guest = false)
        {
            var req = new HttpRequestMessage(method, url);

            // ⚠️ 不能复用 QuarkSession.ApplyHeaders：它硬编码了夸克的
            // Origin: https://pan.quark.cn 与 Referer: pan.quark.cn，
            // UC 收到夸克来源会直接回 403「Invalid CORS request」（实测踩到）。
            // 所以这里自己设一套 UC 的头，风格与夸克那套保持一致（浏览器特征齐全）。
            Set(req, "User-Agent", guest ? UcConstants.GuestUserAgent : UcConstants.UserAgent);
            Set(req, "Accept", "application/json, text/plain, */*");
            Set(req, "Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            Set(req, "Referer", UcConstants.DownloadReferer);
            Set(req, "Origin", UcConstants.WebOrigin);
            Set(req, "Cache-Control", "no-cache");
            Set(req, "Pragma", "no-cache");
            Set(req, "DNT", "1");
            // Sec-Fetch-* 是现代浏览器必发的一组头，缺失是明显的非浏览器特征
            bool sameOrigin = url != null && url.IndexOf("uc.cn", StringComparison.OrdinalIgnoreCase) >= 0;
            Set(req, "Sec-Fetch-Dest", "empty");
            Set(req, "Sec-Fetch-Mode", "cors");
            Set(req, "Sec-Fetch-Site", sameOrigin ? "same-origin" : "same-site");
            if (guest)
                Set(req, "Sec-Ch-Ua", UcConstants.GuestSecChUa);

            if (body != null)
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return req;
        }

        private static void Set(HttpRequestMessage req, string name, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            req.Headers.TryAddWithoutValidation(name, value);
        }

        private async Task<Dictionary<string, object>> SendAsync(
            HttpMethod method, string url, string body = null, bool guest = false,
            CancellationToken ct = default)
        {
            using (var req = Build(method, url, body, guest))
            using (var res = await _session.Http.SendAsync(req, ct).ConfigureAwait(false))
            {
                string text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    // 401/403 一律当成"需要重新取链或刷新会话"，交由上层处理
                    throw new QuarkApiException(
                        string.Format("UC 请求失败 HTTP {0}：{1}", (int)res.StatusCode, Truncate(text)),
                        (int)res.StatusCode);
                }

                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                try
                {
                    var obj = ser.Deserialize<Dictionary<string, object>>(text);
                    if (obj == null)
                        throw new QuarkApiException("UC 响应为空");
                    return obj;
                }
                catch (QuarkApiException) { throw; }
                catch (Exception)
                {
                    throw new QuarkApiException("UC 响应解析失败：" + Truncate(text));
                }
            }
        }

        private static string Truncate(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= 300 ? s : s.Substring(0, 300) + "…";
        }

        private static Dictionary<string, object> AsDict(object o) => o as Dictionary<string, object>;

        private static object Get(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        private static string AsString(object o)
        {
            if (o == null) return null;
            var s = o as string;
            return s ?? o.ToString();
        }

        private static long AsLong(object o)
        {
            if (o == null) return 0;
            if (o is int) return (int)o;
            if (o is long) return (long)o;
            if (o is decimal) return (long)(decimal)o;
            if (o is double) return (long)(double)o;
            long n;
            return long.TryParse(o.ToString(), out n) ? n : 0;
        }

        private static long Pick(Dictionary<string, object> data, params string[] keys)
        {
            if (data == null) return 0;
            foreach (var k in keys)
            {
                long n = AsLong(Get(data, k));
                if (n > 0) return n;
            }
            return 0;
        }

        /// <summary>检查业务层状态码（UC 与夸克一样，HTTP 200 也可能带非 0 code）。</summary>
        private static void CheckBiz(Dictionary<string, object> json, string action)
        {
            object statusObj = Get(json, "status");
            object codeObj = Get(json, "code");
            long status = AsLong(statusObj);
            long code = AsLong(codeObj);

            // 成功：status==200 或 code==0
            if (status == 200 || code == 0)
                return;

            string msg = AsString(Get(json, "message")) ?? action + "失败";
            throw new QuarkApiException(string.Format("{0}（status={1} code={2}）", msg, status, code),
                (int)(code != 0 ? code : status));
        }

        // ==================== 会话 ====================

        /// <summary>
        /// 刷新 __puus（约 3 小时过期）。
        /// <para>做法：请求 <c>/1/clouddrive/config</c> 时<b>先去掉</b> Cookie 里的 __puus，
        /// 逼服务端重新下发一份，再从 Set-Cookie 合并回来。
        /// 距上次刷新不足 <see cref="UcConstants.PuusRefreshInterval"/> 时直接返回。</para>
        /// </summary>
        public async Task RefreshSessionIfDueAsync(CancellationToken ct = default)
        {
            if (DateTime.UtcNow - _lastPuusRefreshUtc < UcConstants.PuusRefreshInterval)
                return;
            await RefreshSessionAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 强制刷新一次会话。
        /// <para>做法：从 CookieJar 里移除 __puus，再请求 config —— 服务端发现会话字段缺失，
        /// 会在 Set-Cookie 里重新下发一份，由 CookieJar 自动收下（QuarkSession 已处理 jar 合并）。</para>
        /// </summary>
        public async Task RefreshSessionAsync(CancellationToken ct = default)
        {
            try
            {
                RemoveCookieFromJar("__puus");
                await SendAsync(HttpMethod.Get, UcConstants.ConfigUrl, ct: ct).ConfigureAwait(false);
                _lastPuusRefreshUtc = DateTime.UtcNow;
            }
            catch
            {
                // 刷新失败不抛：让调用方继续用当前 Cookie 尝试（可能本来就还有效）
                _lastPuusRefreshUtc = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// 从 CookieJar 里删掉某个字段。
        /// <para>⚠️ .NET Framework 的 <c>CookieContainer</c> 没有 <c>GetAllCookies()</c>
        /// （那是 .NET Core 才加的），只能按已知域取 <c>GetCookies(uri)</c> 再逐个 Expired。</para>
        /// </summary>
        private void RemoveCookieFromJar(string name)
        {
            try
            {
                var jar = _session.Jar;
                if (jar == null) return;

                string[] domains =
                {
                    "https://drive.uc.cn", "https://pc-api.uc.cn",
                    "https://drive.quark.cn", "https://pan.quark.cn",
                    "https://drive-pc.quark.cn",
                };
                foreach (var d in domains)
                {
                    Uri uri;
                    if (!Uri.TryCreate(d, UriKind.Absolute, out uri)) continue;
                    System.Net.CookieCollection col;
                    try { col = jar.GetCookies(uri); }
                    catch { continue; }
                    if (col == null) continue;
                    foreach (System.Net.Cookie c in col)
                    {
                        if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                            c.Expired = true;
                    }
                }
            }
            catch { }
        }

        // ==================== 账号信息 ====================

        /// <summary>
        /// 校验登录态并取昵称 / 头像 / 账号标识。
        /// <para>接口：<c>https://drive.uc.cn/account/info</c>。
        /// 游客态返回 <c>{"success":true,"data":{}}</c> —— data 为空对象，
        /// 所以"data 里有昵称"才判定为已登录。</para>
        /// </summary>
        public async Task<(bool ok, string nick)> ValidateAsync()
        {
            try
            {
                var json = await SendAsync(HttpMethod.Get, UcConstants.AccountInfoUrl).ConfigureAwait(false);
                var data = AsDict(Get(json, "data"));
                if (data != null && data.Count > 0)
                {
                    string nick = AsString(Get(data, "nickname"));
                    if (string.IsNullOrWhiteSpace(nick))
                        nick = AsString(Get(data, "name"));
                    if (string.IsNullOrWhiteSpace(nick))
                        nick = "UC 用户";

                    string sig = AsString(Get(data, "uid"))
                        ?? AsString(Get(data, "id"))
                        ?? AsString(Get(data, "account_id"))
                        ?? nick;
                    _accountSig = sig;

                    var av = PickAvatarUrl(data);
                    if (!string.IsNullOrEmpty(av)) _avatarUrl = av;

                    return (true, nick);
                }
            }
            catch { /* 落到下面的兜底 */ }

            // 兜底：account/info 拿不到时，用"能否取到容量"判断登录态
            try
            {
                var cap = await GetCapacityAsync().ConfigureAwait(false);
                if (cap.total > 0)
                    return (true, "UC 用户");
            }
            catch { }

            return (false, "未登录或登录已失效");
        }

        /// <summary>头像字段探测（与夸克同一套候选表，UC 的字段名未在文档中固定）。</summary>
        private static string PickAvatarUrl(Dictionary<string, object> data)
        {
            if (data == null) return null;
            string[] keys =
            {
                "avatarUri", "avatar_uri", "avatar", "avatar_url", "avatarUrl", "avatarurl",
                "headimg", "head_img", "headimgurl", "head_url", "headimg_url",
                "pic", "pic_url", "picurl", "icon", "icon_url",
                "user_avatar", "user_icon", "portrait", "photo", "photo_url",
                "figureurl", "figure_url",
            };
            foreach (var k in keys)
            {
                string v = AsString(Get(data, k));
                if (IsHttpUrl(v)) return v;
            }
            // 兜底：扫所有字符串值，挑"是 http 且路径里带 avatar/head/pic"的
            foreach (var kv in data)
            {
                string v = AsString(kv.Value);
                if (!IsHttpUrl(v)) continue;
                if (v.IndexOf("avatar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    v.IndexOf("/head", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    v.IndexOf("portrait", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    v.IndexOf("photo", StringComparison.OrdinalIgnoreCase) >= 0)
                    return v;
            }
            return null;
        }

        private static bool IsHttpUrl(string s)
        {
            return !string.IsNullOrEmpty(s) &&
                   (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    s.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 网盘容量。接口与夸克完全一致（<c>/1/clouddrive/member</c>，
        /// 字段 total_capacity / use_capacity），只是域名不同。
        /// </summary>
        public async Task<(long total, long used)> GetCapacityAsync(CancellationToken ct = default)
        {
            try
            {
                var json = await SendAsync(HttpMethod.Get, UcConstants.MemberUrl, ct: ct).ConfigureAwait(false);
                var data = AsDict(Get(json, "data"));
                long total = Pick(data, "total_capacity", "secret_total_capacity", "total");
                long used = Pick(data, "use_capacity", "secret_use_capacity", "used_capacity", "used");
                if (total > 0)
                    return (total, used);
            }
            catch { }
            return (0, 0);
        }

        // ==================== 分享解析 ====================

        /// <summary>
        /// 第 1 步：取分享 token。
        /// <para>POST <c>/1/clouddrive/share/sharepage/token</c>，
        /// body <c>{"pwd_id":"短码","passcode":""}</c>，返回 <c>data.stoken</c> 与 <c>data.title</c>。</para>
        /// </summary>
        public async Task<(string stoken, string title)> GetShareTokenAsync(
            string pwdId, string passcode = null, CancellationToken ct = default)
        {
            var ser = new JavaScriptSerializer();
            string body = ser.Serialize(new Dictionary<string, object>
            {
                { "pwd_id", pwdId },
                { "passcode", passcode ?? "" },
                { "share_for_transfer", true },
            });

            var json = await SendAsync(HttpMethod.Post, UcConstants.ShareTokenUrl, body, ct: ct).ConfigureAwait(false);
            CheckBiz(json, "获取分享信息");
            var data = AsDict(Get(json, "data"));
            if (data == null)
                throw new QuarkApiException("获取分享信息失败：响应缺少 data");

            string stoken = AsString(Get(data, "stoken")) ?? "";
            string title = AsString(Get(data, "title")) ?? "";
            if (string.IsNullOrEmpty(stoken))
                throw new QuarkApiException("获取分享信息失败：未返回 stoken");
            return (stoken, title);
        }

        /// <summary>
        /// 第 2 步：取分享文件列表。
        ///
        /// <para>★ <b>必须用 transfer_share/detail（GET + query），不是 sharepage/v2/detail。</b>
        /// 【2026-11-02 实测踩坑】用 v2/detail（POST + body 带 stoken）返回
        /// <c>total= count=0</c> 空列表；换成 transfer_share/detail 立刻拿到文件。
        /// YunX 的注释也写明这是「官方下载流程实际使用的文件列表接口」。</para>
        ///
        /// <para>返回的每项都带 <c>share_fid_token</c>，它与 stoken 绑定，
        /// 取直链时必须一起提交。</para>
        /// </summary>
        public async Task<List<QuarkFileEntry>> ListShareDirAsync(
            string pwdId, string stoken, string pdirFid = "0",
            int page = 1, int size = 50, CancellationToken ct = default)
        {
            var url = new StringBuilder(UcConstants.TransferShareDetailUrl);
            url.Append("&pwd_id=").Append(Uri.EscapeDataString(pwdId));
            url.Append("&pdir_fid=").Append(Uri.EscapeDataString(pdirFid ?? "0"));
            url.Append("&fetch_file_list=1&passcode=");
            url.Append("&_page=").Append(page);
            url.Append("&_size=").Append(size);
            url.Append("&_fetch_total=1&_fetch_task=1&_fetch_share=1&_sort=");
            url.Append("&stoken=").Append(Uri.EscapeDataString(stoken));

            var json = await SendAsync(HttpMethod.Get, url.ToString(), ct: ct).ConfigureAwait(false);
            CheckBiz(json, "获取文件列表");

            var data = AsDict(Get(json, "data"));
            if (data == null)
                return new List<QuarkFileEntry>();

            // 兼容 data.list 与 data.detail_info.list 两种结构
            var arr = Get(data, "list") as System.Collections.ArrayList;
            if (arr == null)
            {
                var di = AsDict(Get(data, "detail_info"));
                arr = di != null ? Get(di, "list") as System.Collections.ArrayList : null;
            }

            var list = new List<QuarkFileEntry>();
            if (arr == null) return list;

            foreach (var o in arr)
            {
                var item = AsDict(o);
                if (item == null) continue;
                // UC 的目录标志是布尔 dir，不是夸克的 file_type 数字
                bool isDir = false;
                var dirObj = Get(item, "dir");
                if (dirObj is bool) isDir = (bool)dirObj;
                else if (dirObj != null) isDir = AsString(dirObj) == "true" || AsString(dirObj) == "1";

                list.Add(new QuarkFileEntry
                {
                    Fid = AsString(Get(item, "fid")) ?? "",
                    Name = AsString(Get(item, "file_name")) ?? "",
                    Size = AsLong(Get(item, "size")),
                    IsDir = isDir,
                    Token = AsString(Get(item, "share_fid_token")) ?? "",
                });
            }
            return list;
        }

        /// <summary>
        /// 第 3 步：取下载直链。
        ///
        /// <para>POST <c>/1/clouddrive/file/download</c>，
        /// body <c>{"fids":[fid],"pwd_id":短码,"stoken":stoken,"fids_token":[share_fid_token]}</c>。</para>
        ///
        /// <para>★ 同时把响应 Set-Cookie 里的 <c>__pugs</c> 取出并返回 ——
        /// 下载时必须带上它，否则 403（实测）。</para>
        /// </summary>
        public async Task<(string url, long size, string pugs)> GetDownloadLinkAsync(
            string pwdId, string stoken, string fid, string shareFidToken,
            CancellationToken ct = default)
        {
            var ser = new JavaScriptSerializer();
            string body = ser.Serialize(new Dictionary<string, object>
            {
                { "fids", new[] { fid } },
                { "pwd_id", pwdId },
                { "stoken", stoken },
                { "fids_token", new[] { shareFidToken ?? "" } },
            });

            using (var req = Build(HttpMethod.Post, UcConstants.DownloadUrl, body))
            using (var res = await _session.Http.SendAsync(req, ct).ConfigureAwait(false))
            {
                string text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                    throw new QuarkApiException(
                        string.Format("获取下载直链失败 HTTP {0}：{1}", (int)res.StatusCode, Truncate(text)),
                        (int)res.StatusCode);

                // ★ 先把 __pugs 捞出来（与本次直链同响应绑定，不能复用上次的）
                string pugs = CookieText.ExtractField(res, "__pugs");
                if (!string.IsNullOrEmpty(pugs))
                    _lastPugs = pugs;

                var ser2 = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var json = ser2.Deserialize<Dictionary<string, object>>(text);
                CheckBiz(json, "获取下载直链");

                var arr = Get(json, "data") as System.Collections.ArrayList;
                if (arr == null || arr.Count == 0)
                    throw new QuarkApiException("获取下载直链失败：未返回数据");

                var item = AsDict(arr[0]);
                string dl = AsString(Get(item, "download_url"));
                if (string.IsNullOrEmpty(dl))
                    throw new QuarkApiException("获取下载直链失败：未返回 download_url");

                return (dl, AsLong(Get(item, "size")), pugs ?? _lastPugs ?? "");
            }
        }

        /// <summary>最近一次取链拿到的 __pugs（同一分享的后续文件可复用）。</summary>
        public string LastPugs => _lastPugs ?? "";

        public void Dispose()
        {
            // HttpClient 由调用方决定是否释放（可能来自共享池）
        }
    }
}
