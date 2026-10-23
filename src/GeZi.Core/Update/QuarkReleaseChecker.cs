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
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Api;
using GeZi.Core.Models;

namespace GeZi.Core.Update
{
    /// <summary>
    /// 夸克网盘分享里的安装包 —— 更新的第二个来源。
    /// <para>
    /// 【包名不含中文】安装包文件名规则见 <see cref="PackageNamePattern"/>
    /// （形如 <c>GeZi-v2.8.2-win64.zip</c>），与界面上的品牌名无关 ——
    /// 改名只影响界面文案，不要动包名规则，否则就找不到自己传上去的包了。
    /// </para>
    /// <para>
    /// 【为什么要第二个源】GitHub Releases 在国内网络下并不总通（api.github.com 偶发
    /// 超时/DNS 污染），启动时那次静默检查失败就等于"永远不提示更新"。
    /// 用户本来的发布习惯就是把安装包 zip 传到自己的固定分享里，
    /// 那里国内访问稳定，于是作为<b>回退源</b>接进来。
    /// </para>
    /// <para>
    /// 全部接口都是<b>游客（guest）可读</b>的：取 stoken、读分享目录、取下载直链，
    /// 都不需要登录态，也不写用户的网盘（不转存、不删除）。
    /// </para>
    /// </summary>
    public sealed class QuarkReleaseChecker
    {
        /// <summary>
        /// 更新包所在的固定分享。用户 2026-10-22 指定：
        /// https://pan.quark.cn/s/065af2172c40 （提取码 VD8m）。
        /// </summary>
        public const string DefaultPwdId = "065af2172c40";
        public const string DefaultPasscode = "VD8m";

        /// <summary>固定分享页面地址（详情窗的「查看详情」与出错提示都用它）。</summary>
        public const string SharePageUrl =
            "https://pan.quark.cn/s/" + DefaultPwdId;

        /// <summary>
        /// 安装包文件名规则：<c>GeZi-v2.7.1-win64.zip</c> /
        /// <c>GeZi_2.7.1.x64.zip</c> / <c>GeZi 2.7.1.zip</c> 都认，
        /// 版本号必须是三段（主.次.补），与 <see cref="UpdateVersion"/> 的解析口径一致。
        /// 【为什么强约束】分享目录里混着别的东西（旧包、说明文件、其它软件），
        /// 松散的匹配会把不是安装包的文件当成一个"版本"。
        /// </summary>
        public const string PackageNamePattern = @"^GeZi[-_ ]?v?(\d+)\.(\d+)\.(\d+).*\.zip$";

        /// <summary>递归找包的层数上限。分享里通常只有一层"备份"目录。</summary>
        public const int MaxScanDepth = 2;

        private readonly string _pwdId;
        private readonly string _passcode;
        private readonly TimeSpan _timeout;
        private readonly string _cookie;

        /// <summary>
        /// 构造夸克源检查器。
        ///
        /// <para>【2026-10-23 关键修复：必须传登录 Cookie】</para>
        /// <para>原来这里固定用 <c>new QuarkClient("")</c>（游客态）取链，
        /// 结果拿到的直链落在 <c>dl-guest-*</c> 域名上，下载时被 CDN 直接拒
        /// （<b>HTTP 412</b>）—— 于是"一键更新"在 GitHub 不通时必然失败。</para>
        /// <para>实测对照（同一个包 <c>GeZi-v2.8.4-win64.zip</c>）：</para>
        /// <list type="bullet">
        /// <item>游客（空 cookie）→ <c>dl-guest-sz-u.drive.quark.cn</c> → <b>412 失败</b></item>
        /// <item>登录（真实 cookie）→ <c>dl-pc-sz.drive.quark.cn</c> → <b>206 成功</b></item>
        /// </list>
        /// <para>游客态之所以"能查到版本、能取到直链"，是因为分享接口本身不要求登录；
        /// 但<b>取直链</b>这一步对身份敏感 —— 游客会被降级到只能预览的域名。
        /// 所以必须把登录态传进来。</para>
        /// </summary>
        /// <param name="cookie">
        /// 登录 Cookie；<b>传空串会退化成游客态，直链下不了</b>（仅用于诊断对照）。
        /// </param>
        /// <param name="sharedClient">
        /// 直接复用的 <see cref="QuarkClient"/>（2026-10-23 新增）。
        ///
        /// <para>【为什么需要它】新建的 client 用的是配置里那份**静态 Cookie 快照**，
        /// 取直链接口会放行，但换来的直链在真正下载时稳定 412
        /// （Tengine Precondition Failed）—— 实测连点三次全失败。
        /// 而主窗口那个**长期存活**的 client 从没失败过，
        /// 它的会话 jar 里有服务端滚动下发的最新字段。</para>
        ///
        /// <para>传了这个就不再新建、也不 <c>Dispose</c> 它（生命周期归调用方）；
        /// 传 null 则退回"用 cookie 新建一个"的老行为。</para>
        /// </param>
        public QuarkReleaseChecker(string pwdId = null, string passcode = null,
            string cookie = null, TimeSpan? timeout = null,
            QuarkClient sharedClient = null)
        {
            _pwdId = string.IsNullOrEmpty(pwdId) ? DefaultPwdId : pwdId;
            _passcode = passcode ?? DefaultPasscode;   // 空串 = 分享没有提取码
            _cookie = cookie ?? "";
            _sharedClient = sharedClient;
            _timeout = timeout ?? TimeSpan.FromSeconds(15);
        }

        /// <summary>外部传入的长期 client（null = 自己新建）。</summary>
        private readonly QuarkClient _sharedClient;

        /// <summary>这次查链用的是不是登录态（诊断用）。</summary>
        public bool IsLoggedIn
        {
            get { return !string.IsNullOrEmpty(_cookie); }
        }

        /// <summary>分享页地址（实例化后可取，方便 UI 用当前配置的源）。</summary>
        public string ShareUrl
        {
            get { return "https://pan.quark.cn/s/" + _pwdId; }
        }

        /// <summary>
        /// 找出分享里版本号最高、且版本号能被解析出来的那个安装包。
        /// 返回 null 表示"这个源现在用不了"（分享失效 / 接口改版 / 网络不通 / 没有安装包）——
        /// 上层按"回退失败"处理即可，不需要区分具体原因。
        /// </summary>
        public async Task<QuarkRelease> FindLatestAsync(CancellationToken ct = default(CancellationToken))
        {
            // ⚠️ 优先用外部传入的长期 client（与手动下载同一条会话），
            //    否则才 new 一个（会拿到 412 的直链）。
            var shared = _sharedClient;
            var own = shared == null ? new QuarkClient(_cookie) : null;
            try
            {
                var client = shared ?? own;
                Log("查夸克更新源: pwdId=" + _pwdId
                    + " passcode=" + (string.IsNullOrEmpty(_passcode) ? "(无)" : "(有)")
                    + " client=" + (shared != null ? "复用主窗口的（与手动下载同一会话）"
                                                  : "新建（cookie " + _cookie.Length + " 字节）"));

                ShareTokenInfo info;
                try
                {
                    info = await client.GetShareInfoAsync(_pwdId, _passcode).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log("夸克源失败：拿 stoken 异常 " + ex.GetType().Name + ": " + ex.Message);
                    return null;
                }
                if (info == null || string.IsNullOrEmpty(info.Stoken))
                {
                    Log("夸克源失败：拿不到 stoken（分享可能失效或提取码变了）");
                    return null;
                }
                Log("夸克源 stoken OK，分享标题: " + info.Title);

                var found = new List<QuarkRelease>();
                await ScanAsync(client, _pwdId, info.Stoken, "0", 0, found, ct).ConfigureAwait(false);
                if (found.Count == 0)
                {
                    Log("夸克源失败：分享里没有符合命名规则的安装包"
                        + "（规则 " + PackageNamePattern + "）");
                    return null;
                }

                found.Sort((a, b) =>
                {
                    int c = b.Version.CompareTo(a.Version);
                    if (c != 0) return c;
                    return b.PublishedTicks.CompareTo(a.PublishedTicks);
                });
                Log("夸克源找到 " + found.Count + " 个包，最新是 " + found[0].Version
                    + "（" + found[0].FileName + "，" + found[0].Size + " 字节）");
                return found[0];
            }
            finally
            {
                // 只释放自己创建的；外部传入的那个生命周期归调用方。
                if (own != null) { try { own.Dispose(); } catch { } }
            }
        }

        /// <summary>
        /// 取安装包的下载直链。
        ///
        /// <para>【2026-10-23 关键发现：必须**现查现取**，不能复用旧的 token】</para>
        ///
        /// <para>实测现象：用户手动下载同一个包能成（直链可探测 HTTP 206），
        /// 但「一键更新」拿到直链后探测 <b>403 RequestDeniedByCallback /
        /// require login [auth expired]</b>。</para>
        ///
        /// <para><b>根因</b>：<see cref="FindLatestAsync"/> 是在<b>用户点「检查更新」那一刻</b>
        /// 跑的，它记下的 <c>Token</c>（share_fid_token）与 <c>Stoken</c> 都有寿命；
        /// 而真正取链发生在用户点「一键更新」之后（中间可能隔几分钟、
        /// 甚至用户看完更新说明再点）。这时旧 token 已被服务端判过期，
        /// 拿它换来的直链在 OSS 回调校验时就会 auth expired。</para>
        ///
        /// <para><b>修法</b>：这里<b>不再用 rel 里存的 stoken/token</b>，
        /// 而是重新走一遍「申请 stoken → 进目录 → 按 fid 找 entry 拿新 token」，
        /// 拿到就用 —— 与手动下载的时序完全一致。</para>
        /// </summary>
        public async Task<string> ResolveDownloadUrlAsync(QuarkRelease rel,
            CancellationToken ct = default(CancellationToken))
        {
            if (rel == null || !rel.IsValid) return null;

            // ⚠️ 优先复用主窗口那个长期 client（与手动下载同一会话）。
            //    实测新建 client 取来的直链在下载阶段稳定 412。
            QuarkClient shared = _sharedClient;
            QuarkClient own = shared == null ? new QuarkClient(_cookie) : null;
            QuarkClient client = shared ?? own;

            try
            {
                Log("夸克源准备取直链: fid=" + rel.Fid
                    + " 旧token=" + Short(rel.Token)
                    + " client=" + (shared != null ? "复用主窗口的（与手动下载同一会话）" : "新建"));

                // ① 重新申请 stoken（旧的可能已过期）
                string stoken = rel.Stoken;
                try
                {
                    ShareTokenInfo fresh = await client.GetShareInfoAsync(rel.PwdId, _passcode)
                        .ConfigureAwait(false);
                    if (fresh != null && !string.IsNullOrEmpty(fresh.Stoken))
                    {
                        if (!string.Equals(fresh.Stoken, rel.Stoken, StringComparison.Ordinal))
                            Log("夸克源：stoken 已更新（旧的可能过期）");
                        stoken = fresh.Stoken;
                    }
                }
                catch (Exception ex)
                {
                    Log("夸克源重新申请 stoken 失败，沿用旧的: " + ex.Message);
                }

                // ② 重新定位该文件，拿**新的** share_fid_token
                //    （按 fid 找：fid 在同一分享内不变，名字可能改）
                string token = rel.Token;
                try
                {
                    QuarkFileEntry entry = await FindEntryByFidAsync(client, rel.PwdId, stoken, rel.Fid, ct)
                        .ConfigureAwait(false);
                    if (entry != null && !string.IsNullOrEmpty(entry.Token))
                    {
                        if (!string.Equals(entry.Token, rel.Token, StringComparison.Ordinal))
                            Log("夸克源：token 已刷新（旧的可能过期）");
                        token = entry.Token;
                    }
                    else
                    {
                        Log("夸克源：没能重新定位文件，沿用旧 token");
                    }
                }
                catch (Exception ex)
                {
                    Log("夸克源重新定位文件失败，沿用旧 token: " + ex.Message);
                }

                Log("夸克源取链参数: fid=" + Short(rel.Fid) + " token=" + Short(token)
                    + " stoken=" + Short(stoken));

                // ③ 取直链
                List<DownloadLink> links = await client.GetShareDownloadAsync(
                    new List<string> { rel.Fid },
                    new List<string> { token },
                    rel.PwdId, stoken).ConfigureAwait(false);

                if (links == null || links.Count == 0)
                {
                    Log("夸克源取直链失败：接口没返回任何链接");
                    return null;
                }

                foreach (DownloadLink l in links)
                {
                    if (string.IsNullOrEmpty(l.DownloadUrl)) continue;
                    string host = SafeHost(l.DownloadUrl);
                    Log("夸克源拿到直链 host=" + host
                        + " 长度=" + l.DownloadUrl.Length
                        + (l.DownloadUrl.IndexOf("Signature=", StringComparison.OrdinalIgnoreCase) >= 0
                            ? "（OSS 签名链）" : "（auth_key 链）")
                        + (host.IndexOf("guest", StringComparison.OrdinalIgnoreCase) >= 0
                            ? "  ⚠ 游客域名" : ""));
                    return l.DownloadUrl;
                }

                Log("夸克源取直链失败：返回的链接里没有 download_url");
                return null;
            }
            catch (Exception ex)
            {
                Log("夸克源取直链异常: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
            finally
            {
                // 只释放自己创建的；外部传入的那个生命周期归调用方。
                if (own != null) { try { own.Dispose(); } catch { } }
            }
        }


        private static string SafeHost(string url)
        {
            try { return new Uri(url).Host; } catch { return "?"; }
        }

        /// <summary>日志用：只留前 8 个字符，避免把凭证整条写进日志。</summary>
        private static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(空)";
            return s.Length <= 8 ? s : s.Substring(0, 8) + "…(" + s.Length + ")";
        }

        /// <summary>
        /// 在分享里**按 fid** 重新找出文件条目，拿它当前的 share_fid_token。
        /// <para>用于取链前刷新 token（旧 token 可能已过期）。
        /// fid 在同一分享内不变，比按名字可靠。</para>
        /// </summary>
        private async Task<QuarkFileEntry> FindEntryByFidAsync(QuarkClient client,
            string pwdId, string stoken, string fid, CancellationToken ct)
        {
            var queue = new Queue<string>();
            queue.Enqueue("0");
            int visited = 0;
            const int maxDirs = 100;   // 更新包所在的分享通常只有一两层

            while (queue.Count > 0 && visited < maxDirs)
            {
                ct.ThrowIfCancellationRequested();
                string dir = queue.Dequeue();
                visited++;

                List<QuarkFileEntry> items;
                try
                {
                    items = await client.ShareDetailAsync(pwdId, stoken, dir).ConfigureAwait(false);
                }
                catch { continue; }
                if (items == null) continue;

                foreach (var it in items)
                {
                    if (it == null) continue;
                    if (it.IsDir) { queue.Enqueue(it.Fid); continue; }
                    if (string.Equals(it.Fid, fid, StringComparison.Ordinal))
                        return it;
                }
            }
            return null;
        }

        /// <summary>
        /// 诊断日志出口。主窗口启动时挂上（转到「运行日志」面板）；
        /// 为空则静默 —— 让这个类在不关心日志的场景（单测/命令行）也能直接用。
        /// </summary>
        public static Action<string> Log { get; set; }

        /// <summary>递归扫分享目录（分享接口没有"全量递归列目录"，只能一层层来）。</summary>
        private async Task ScanAsync(QuarkClient client, string pwdId, string stoken,
            string pdirFid, int depth, List<QuarkRelease> found, CancellationToken ct)
        {
            if (depth > MaxScanDepth) return;
            List<QuarkFileEntry> items;
            try
            {
                items = await client.ShareDetailAsync(pwdId, stoken, pdirFid).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 单个目录读失败不该让整次检查失败，但要能看见
                Log("夸克源扫目录失败 (pdirFid=" + pdirFid + ", depth=" + depth
                    + "): " + ex.GetType().Name + ": " + ex.Message);
                return;
            }
            if (items == null)
            {
                Log("夸克源扫目录返回 null (pdirFid=" + pdirFid + ", depth=" + depth + ")");
                return;
            }
            Log("夸克源扫目录 depth=" + depth + " pdirFid=" + pdirFid
                + " 共 " + items.Count + " 项");

            foreach (var it in items)
            {
                ct.ThrowIfCancellationRequested();
                if (it == null) continue;

                if (it.IsDir)
                {
                    if (depth < MaxScanDepth)
                        await ScanAsync(client, pwdId, stoken, it.Fid, depth + 1, found, ct)
                            .ConfigureAwait(false);
                    continue;
                }

                var rel = TryParseName(it, pwdId, stoken);
                if (rel != null) found.Add(rel);
            }
        }

        /// <summary>文件名 → 发布信息。名字不像安装包返回 null。</summary>
        private static QuarkRelease TryParseName(QuarkFileEntry it, string pwdId, string stoken)
        {
            if (it == null || string.IsNullOrEmpty(it.Name)) return null;
            var m = Regex.Match(it.Name, PackageNamePattern, RegexOptions.IgnoreCase);
            if (!m.Success) return null;

            int major, minor, patch;
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out major)
                || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out minor)
                || !int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out patch))
                return null;

            return new QuarkRelease
            {
                Version = new UpdateVersion(major, minor, patch),
                TagName = "v" + major + "." + minor + "." + patch,
                FileName = it.Name,
                Size = it.Size,
                Fid = it.Fid,
                Token = it.Token ?? "",
                PwdId = pwdId,
                Stoken = stoken,
                ShareUrl = "https://pan.quark.cn/s/" + pwdId,
                // 分享列表不带发布时间，按大小兜底排序没意义，直接给 0
                PublishedTicks = 0,
            };
        }
    }
}