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
using System.Collections.Generic;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Support;

namespace GeZi.Core.Update
{
    /// <summary>
    /// 查 GitHub Releases 的「最新发布」。
    /// Release 天然带 tag_name（版本号）、body（更新说明）、html_url（下载页），
    /// 不依赖夸克登录态、不写用户网盘、无中转清理。
    /// GitHub API 对没有 User-Agent 的请求直接 403。
    /// 启动检查要快；国内访问 api.github.com 偶发不通，失败时由上层静默跳过。
    /// </summary>
    public sealed class GitHubReleaseChecker
    {
        // 发布仓库：本 fork 的 GitHub 地址。换仓库只改这两行（发布 tag 需为 vX.Y.Z）。
        public const string RepoOwner = "zeroyong";
        public const string RepoName = "gezi-quark-downloader-main";

        /// <summary>最新发布接口（GitHub 固定语义：永远指向 latest release）。</summary>
        public const string LatestApiUrl =
            "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases/latest";

        /// <summary>仓库 Releases 列表页（API 失败时给用户的手动入口）。</summary>
        public const string ReleasesPageUrl =
            "https://github.com/" + RepoOwner + "/" + RepoName + "/releases";

        /// <summary>GitHub API 要求的 User-Agent。</summary>
        private const string UserAgent = "GeZi-Updater (https://github.com/" + RepoOwner + "/" + RepoName + ")";

        private readonly TimeSpan _timeout;

        public GitHubReleaseChecker(TimeSpan? timeout = null)
        {
            _timeout = timeout ?? TimeSpan.FromSeconds(10);
        }

        /// <summary>查最新发布。取不到 / 解析失败 / 版本号不认识时返回 null。</summary>
        public async Task<GitHubRelease> FindLatestAsync(CancellationToken ct = default(CancellationToken))
        {
            string json = await GetStringAsync(LatestApiUrl, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json))
                return null;
            return Parse(json);
        }

        private async Task<string> GetStringAsync(string url, CancellationToken ct)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = UserAgent;
            request.Accept = "application/vnd.github+json";
            request.Timeout = (int)_timeout.TotalMilliseconds;
            request.ReadWriteTimeout = (int)_timeout.TotalMilliseconds;

            // 与下载器一致：默认直连；只有用户显式开启「跟随系统代理」才读 WinINET
            if (NetworkOptions.UseSystemProxy)
            {
                try
                {
                    var proxy = WebRequest.GetSystemWebProxy();
                    if (proxy != null)
                    {
                        try { proxy.Credentials = CredentialCache.DefaultCredentials; } catch { }
                        request.Proxy = proxy;
                    }
                }
                catch { request.Proxy = null; }
            }
            else
            {
                request.Proxy = null;
            }

            try
            {
                using (ct.Register(() => { try { request.Abort(); } catch { } }))
                using (var resp = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                using (var stream = resp.GetResponseStream())
                {
                    if (stream == null) return null;
                    using (var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8))
                        return await reader.ReadToEndAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>把 GitHub 的 release JSON 解析成 <see cref="GitHubRelease"/>。</summary>
        internal static GitHubRelease Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var ser = new DataContractJsonSerializer(typeof(ReleaseDto));
                using (var ms = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
                {
                    var dto = ser.ReadObject(ms) as ReleaseDto;
                    if (dto == null) return null;

                    var rel = new GitHubRelease
                    {
                        TagName = dto.TagName ?? "",
                        Title = dto.Name ?? "",
                        Body = dto.Body ?? "",
                        HtmlUrl = dto.HtmlUrl ?? "",
                    };

                    UpdateVersion v;
                    if (!UpdateVersion.TryParse(dto.TagName, out v) || v == null)
                        return null;   // tag 不符合 v主.次.补 → 当作没有（不误报）
                    rel.Version = v;

                    DateTime pub;
                    if (!string.IsNullOrEmpty(dto.PublishedAt)
                        && DateTime.TryParse(dto.PublishedAt,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind, out pub))
                        rel.PublishedAt = pub;

                    // assets 里挑 win64 安装包（发版附件名约定 GeZi-vX.Y.Z-win64.zip）
                    foreach (var a in NonNull(dto.Assets))
                    {
                        if (a != null && !string.IsNullOrEmpty(a.Name)
                            && a.Name.EndsWith("-win64.zip", StringComparison.OrdinalIgnoreCase))
                        {
                            rel.AssetSize = a.Size;
                            break;
                        }
                    }
                    return rel;
                }
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<AssetDto> NonNull(List<AssetDto> list)
        {
            return list ?? new List<AssetDto>();
        }

        /// <summary>GitHub release JSON 的最小字段集（snake_case → DataMember Name）。</summary>
        [DataContract]
        internal sealed class ReleaseDto
        {
            [DataMember(Name = "tag_name")] public string TagName { get; set; }
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "body")] public string Body { get; set; }
            [DataMember(Name = "html_url")] public string HtmlUrl { get; set; }
            [DataMember(Name = "published_at")] public string PublishedAt { get; set; }
            [DataMember(Name = "assets")] public List<AssetDto> Assets { get; set; }
        }

        [DataContract]
        internal sealed class AssetDto
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "size")] public long Size { get; set; }
        }
    }
}
