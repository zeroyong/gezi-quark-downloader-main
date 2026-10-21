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
    /// （形如 <c>GeZi-v2.8.1-win64.zip</c>），与界面上的品牌名无关 ——
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

        public QuarkReleaseChecker(string pwdId = null, string passcode = null,
            TimeSpan? timeout = null)
        {
            _pwdId = string.IsNullOrEmpty(pwdId) ? DefaultPwdId : pwdId;
            _passcode = passcode ?? DefaultPasscode;   // 空串 = 分享没有提取码
            _timeout = timeout ?? TimeSpan.FromSeconds(15);
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
            using (var client = new QuarkClient(""))
            {
                // 游客态：空 cookie 也能拿 stoken（分享本身没有登录要求）。
                ShareTokenInfo info;
                try
                {
                    info = await client.GetShareInfoAsync(_pwdId, _passcode).ConfigureAwait(false);
                }
                catch
                {
                    return null;
                }
                if (info == null || string.IsNullOrEmpty(info.Stoken))
                    return null;

                var found = new List<QuarkRelease>();
                await ScanAsync(client, _pwdId, info.Stoken, "0", 0, found, ct).ConfigureAwait(false);
                if (found.Count == 0)
                    return null;

                found.Sort((a, b) =>
                {
                    int c = b.Version.CompareTo(a.Version);
                    if (c != 0) return c;
                    return b.PublishedTicks.CompareTo(a.PublishedTicks);
                });
                return found[0];
            }
        }

        /// <summary>
        /// 取安装包的下载直链（不带 cookie，分享直链可直接下载）。
        /// 独立于 <see cref="FindLatestAsync"/>，因为直链有时效，
        /// 用户可能隔一会儿才点「一键更新」。
        /// </summary>
        public async Task<string> ResolveDownloadUrlAsync(QuarkRelease rel,
            CancellationToken ct = default(CancellationToken))
        {
            if (rel == null || !rel.IsValid) return null;
            using (var client = new QuarkClient(""))
            {
                try
                {
                    var links = await client.GetShareDownloadAsync(
                        new List<string> { rel.Fid },
                        new List<string> { rel.Token },
                        rel.PwdId, rel.Stoken).ConfigureAwait(false);
                    if (links == null || links.Count == 0) return null;
                    foreach (var l in links)
                        if (!string.IsNullOrEmpty(l.DownloadUrl))
                            return l.DownloadUrl;
                    return null;
                }
                catch
                {
                    return null;
                }
            }
        }

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
            catch
            {
                return;         // 单个目录读失败不该让整次检查失败
            }
            if (items == null) return;

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