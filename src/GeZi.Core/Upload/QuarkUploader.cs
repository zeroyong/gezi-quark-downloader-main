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
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using GeZi.Core.Support;

namespace GeZi.Core.Upload
{
    /// <summary>
    /// 夸克 / UC 网盘上传器（2026-11-10 新增）。
    ///
    /// <para>【协议来源】与 YunX v1.3.1 的 <c>QuarkUpload.kt</c> 同源，已用真实 Cookie
    /// 端到端实测跑通（GeZi v2.9.0 安装包实传成功）。两家只有三个常量不同：
    /// <c>apiBase</c> / <c>product</c> / <c>origin</c>。</para>
    ///
    /// <para>【核心认知】OSS 的 <c>Authorization</c> **不需要客户端自己算**：
    /// 客户端只负责把待签字符串拼好，发给 <c>file/upload/auth</c> 换 <c>auth_key</c>，
    /// 服务端把签好的授权还回来。所以这里不实现任何 HMAC 签名算法 ——
    /// 只要拼接**逐字节正确**。</para>
    ///
    /// <para>【五步流程】
    /// ① <c>file/upload/pre</c> 建任务 →
    /// ② <c>file/update/hash</c> 报 MD5+SHA1（<c>finish=true</c> 即秒传，直接结束）→
    /// ③ 逐片 <c>file/upload/auth</c> 换授权 + PUT 到 OSS →
    /// ④ 带 <c>x-oss-callback</c> 的 XML 合并 →
    /// ⑤ <c>file/upload/finish</c> 收尾。</para>
    /// </summary>
    public sealed class QuarkUploader : IDisposable
    {
        // ---------------- 常量 ----------------

        /// <summary>OSS 分片默认大小（服务端会在 pre.metadata.part_size 给更精确的值）。</summary>
        private const long DefaultPartSize = 8L * 1024 * 1024;

        /// <summary>
        /// 签名串里的 <c>x-oss-user-agent</c>。
        ///
        /// <para>★ 必须与请求头里 <c>x-oss-user-agent</c> **逐字一致**，
        /// 且按网页端上传 SDK 的值写死 —— 改了就是 403 签名不匹配。</para>
        /// </summary>
        private const string OssAgent =
            "aliyun-sdk-js/6.6.1 Chrome 98.0.4758.80 on Windows 10 64-bit";

        /// <summary>算哈希时的读缓冲（256 KiB，兼顾进度粒度与吞吐）。</summary>
        private const int HashBuffer = 256 * 1024;

        private static readonly JavaScriptSerializer Json =
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        // ---------------- 字段 ----------------

        private readonly string _apiBase;
        private readonly string _product;
        private readonly string _origin;
        private readonly string _userAgent;
        private readonly string _cookie;
        private readonly Func<string, object, Task<Dictionary<string, object>>> _postJson;
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;

        /// <summary>诊断日志出口（为空则静默）。</summary>
        public Action<string> Log { get; set; }

        private void Trace(string message)
        {
            var h = Log;
            if (h != null) { try { h(message); } catch { } }
        }

        /// <param name="apiBase">如 <c>https://drive-pc.quark.cn</c>。</param>
        /// <param name="product">如 <c>ucpro</c>（夸克）/ <c>UCBrowser</c>（UC）。</param>
        /// <param name="origin">如 <c>https://pan.quark.cn</c>，用于 Referer。</param>
        /// <param name="userAgent">API 请求用的 UA。</param>
        /// <param name="cookie">登录 Cookie 串。</param>
        /// <param name="postJson">调用网盘 JSON 接口的委托（返回已反序列化的响应对象）。</param>
        public QuarkUploader(string apiBase, string product, string origin,
            string userAgent, string cookie,
            Func<string, object, Task<Dictionary<string, object>>> postJson)
        {
            if (string.IsNullOrEmpty(apiBase)) throw new ArgumentException("apiBase 不能为空", "apiBase");
            if (string.IsNullOrEmpty(product)) throw new ArgumentException("product 不能为空", "product");
            if (postJson == null) throw new ArgumentNullException("postJson");

            _apiBase = apiBase.TrimEnd('/');
            _product = product;
            _origin = (origin ?? apiBase).TrimEnd('/');
            _userAgent = userAgent;
            _cookie = cookie ?? "";
            _postJson = postJson;

            // OSS 阶段不带 Cookie（Authorization 已含全部权限），
            // 且必须关掉自动重定向 —— 签名是按原始 URL 算的，跳转后必然失败。
            var handler = new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
            _ownsHttp = true;
        }

        public void Dispose()
        {
            if (_ownsHttp) { try { _http.Dispose(); } catch { } }
        }

        // ---------------- 对外入口 ----------------

        /// <summary>
        /// 上传一个本地文件到指定目录。
        ///
        /// <para>返回 true 表示走了秒传（服务端已有同内容文件，没有消耗上传流量）。</para>
        /// </summary>
        /// <param name="localPath">本地文件绝对路径。</param>
        /// <param name="parentFid">目标目录 fid（根目录为 <c>"0"</c>）。</param>
        /// <param name="displayName">网盘里的文件名（null = 用本地文件名）。</param>
        /// <param name="progress">阶段 + 字节进度回调（可为 null）。</param>
        /// <param name="ct">取消令牌。</param>
        public async Task<bool> UploadAsync(string localPath, string parentFid,
            string displayName = null, Action<UploadPhase, long, long> progress = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(localPath))
                throw new UploadException("没有指定要上传的文件");
            if (!File.Exists(localPath))
                throw new UploadException("文件不存在：" + localPath);

            var info = new FileInfo(localPath);
            long size = info.Length;
            // 网盘不支持空文件 —— 提前拒绝，别等到 pre 接口报个看不懂的错
            if (size <= 0)
                throw new UploadException("网盘不支持上传空文件：" + info.Name);

            string fileName = string.IsNullOrEmpty(displayName) ? info.Name : displayName;
            string mime = GuessMime(fileName);

            // ---------- ① pre：建上传任务 ----------
            Report(progress, UploadPhase.Preparing, 0, size);
            Trace("[上传] " + fileName + " (" + size + " 字节) → 目录 " + parentFid);

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var preBody = new Dictionary<string, object>
            {
                { "ccp_hash_update", true },
                { "dir_name", "" },
                { "file_name", fileName },
                { "format_type", mime },
                { "pdir_fid", string.IsNullOrEmpty(parentFid) ? "0" : parentFid },
                { "size", size },
                { "l_created_at", now },
                { "l_updated_at", now },
                { "same_path_reuse", false },
            };

            var pre = await PostAsync("file/upload/pre", preBody, ct).ConfigureAwait(false);
            var preData = Data(pre);
            if (preData == null)
                throw new UploadException("网盘没有返回上传任务"
                    + Describe(pre));

            string taskId = Str(preData, "task_id");
            if (string.IsNullOrEmpty(taskId))
                throw new UploadException("网盘没有返回上传任务 ID");

            // ---------- ② 哈希 + 秒传判定 ----------
            Report(progress, UploadPhase.Preparing, 0, size);
            ct.ThrowIfCancellationRequested();

            string md5, sha1;
            await ComputeHashAsync(localPath, size, progress, ct).ConfigureAwait(false);
            HashFile(localPath, out md5, out sha1);

            var hashBody = new Dictionary<string, object>
            {
                { "md5", md5 }, { "sha1", sha1 }, { "task_id", taskId },
            };
            var hashRes = await PostAsync("file/update/hash", hashBody, ct).ConfigureAwait(false);
            var hashData = Data(hashRes);
            bool finish = hashData != null && Bool(hashData, "finish");

            if (finish)
            {
                // 秒传：服务端已有同内容文件，一个字节都不用传。
                // 照对标实现的做法，直接结束（连 finish 接口都不用调）。
                Trace("[秒传] 命中，未消耗上传流量：" + fileName);
                Report(progress, UploadPhase.Finishing, size, size);
                return true;
            }

            // ---------- ③ 逐片上传到 OSS ----------
            string bucket = Str(preData, "bucket");
            string objKey = Str(preData, "obj_key");
            string uploadId = Str(preData, "upload_id");
            string uploadUrl = Str(preData, "upload_url");
            string callbackJson = CallbackText(preData);

            if (string.IsNullOrEmpty(bucket) || string.IsNullOrEmpty(objKey)
                || string.IsNullOrEmpty(uploadId) || string.IsNullOrEmpty(uploadUrl))
            {
                throw new UploadException("网盘没有返回完整的上传凭据"
                    + "（bucket/obj_key/upload_id/upload_url 有缺项）");
            }

            string host;
            try { host = new Uri(uploadUrl).Host; }
            catch { throw new UploadException("网盘返回的上传地址无法解析：" + uploadUrl); }

            string bucketHost = host.StartsWith(bucket + ".", StringComparison.OrdinalIgnoreCase)
                ? host : bucket + "." + host;
            // ★ 对象键按 / 分段，每段单独编码，斜杠本身保留
            string address = "https://" + bucketHost + "/" + EncodeObjectKey(objKey);

            long partSize = DefaultPartSize;
            var meta = preData.ContainsKey("metadata")
                ? preData["metadata"] as Dictionary<string, object> : null;
            if (meta != null && meta.ContainsKey("part_size"))
            {
                long declared = AsLong(meta["part_size"]);
                if (declared > 0) partSize = declared;
            }

            int partCount = (int)((size + partSize - 1) / partSize);
            if (partCount < 1) partCount = 1;
            Trace("[上传] 分片 " + partSize + " 字节 × " + partCount + " 片");

            var etags = new List<string>(partCount);
            using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.SequentialScan))
            {
                for (int index = 0; index < partCount; index++)
                {
                    ct.ThrowIfCancellationRequested();

                    long start = index * partSize;
                    long length = Math.Min(size - start, partSize);
                    int partNumber = index + 1;

                    string date = HttpDate();
                    string authMeta =
                        "PUT\n\n" + mime + "\n" + date + "\n"
                        + "x-oss-date:" + date + "\n"
                        + "x-oss-user-agent:" + OssAgent + "\n"
                        + "/" + bucket + "/" + objKey
                        + "?partNumber=" + partNumber + "&uploadId=" + uploadId;

                    string authKey = await AuthorizeAsync(preData, authMeta, taskId, ct)
                        .ConfigureAwait(false);

                    fs.Seek(start, SeekOrigin.Begin);
                    long baseBytes = start;
                    Action<long> onRead = read =>
                        Report(progress, UploadPhase.Uploading, baseBytes + read, size);

                    using (var content = new UploadRangeContent(fs, length, onRead, ct))
                    {
                        content.Headers.ContentType = new MediaTypeHeaderValue(mime);
                        var req = new HttpRequestMessage(HttpMethod.Put,
                            address + "?partNumber=" + partNumber + "&uploadId=" + uploadId);
                        req.Content = content;
                        req.Headers.TryAddWithoutValidation("Authorization", authKey);
                        req.Headers.TryAddWithoutValidation("Referer", _origin + "/");
                        req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
                        req.Headers.TryAddWithoutValidation("x-oss-date", date);
                        req.Headers.TryAddWithoutValidation("x-oss-user-agent", OssAgent);

                        using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                            .ConfigureAwait(false))
                        {
                            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (!resp.IsSuccessStatusCode)
                            {
                                throw new UploadException("第 " + partNumber + "/" + partCount
                                    + " 片上传失败（HTTP " + (int)resp.StatusCode + "）"
                                    + Snippet(body));
                            }
                            string etag = "";
                            IEnumerable<string> values;
                            if (resp.Headers.TryGetValues("ETag", out values))
                                etag = string.Join("", values);
                            if (string.IsNullOrEmpty(etag))
                                throw new UploadException("第 " + partNumber
                                    + " 片上传后网盘没有返回 ETag，无法合并");
                            etags.Add(etag);
                        }
                    }
                }
            }

            // ---------- ④ 合并分片 ----------
            Report(progress, UploadPhase.Finishing, size, size);
            ct.ThrowIfCancellationRequested();

            string xml = BuildCompleteXml(etags);
            string completeDate = HttpDate();
            byte[] xmlBytes = Encoding.UTF8.GetBytes(xml);
            string contentMd5;
            using (var md5Alg = MD5.Create())
                contentMd5 = Convert.ToBase64String(md5Alg.ComputeHash(xmlBytes));

            string callbackB64 = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(callbackJson));

            string completeMeta =
                "POST\n" + contentMd5 + "\napplication/xml\n" + completeDate + "\n"
                + "x-oss-callback:" + callbackB64 + "\n"
                + "x-oss-date:" + completeDate + "\n"
                + "x-oss-user-agent:" + OssAgent + "\n"
                + "/" + bucket + "/" + objKey + "?uploadId=" + uploadId;

            string completeKey = await AuthorizeAsync(preData, completeMeta, taskId, ct)
                .ConfigureAwait(false);

            using (var completeReq = new HttpRequestMessage(HttpMethod.Post,
                address + "?uploadId=" + uploadId))
            {
                var bc = new ByteArrayContent(xmlBytes);
                bc.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                bc.Headers.TryAddWithoutValidation("Content-MD5", contentMd5);
                completeReq.Content = bc;
                completeReq.Headers.TryAddWithoutValidation("Authorization", completeKey);
                completeReq.Headers.TryAddWithoutValidation("Referer", _origin + "/");
                completeReq.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
                completeReq.Headers.TryAddWithoutValidation("x-oss-callback", callbackB64);
                completeReq.Headers.TryAddWithoutValidation("x-oss-date", completeDate);
                completeReq.Headers.TryAddWithoutValidation("x-oss-user-agent", OssAgent);

                using (var resp = await _http.SendAsync(completeReq, ct).ConfigureAwait(false))
                {
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        throw new UploadException("合并分片失败（HTTP "
                            + (int)resp.StatusCode + "）" + Snippet(body));
                    }
                    // OSS 的错误可能是 200 + XML <Error>，只看状态码会漏判
                    if (body.IndexOf("<Error", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new UploadException("合并分片时网盘返回错误" + Snippet(body));
                }
            }

            // ---------- ⑤ finish ----------
            var finishBody = new Dictionary<string, object>
            {
                { "obj_key", objKey }, { "task_id", taskId },
            };
            await PostAsync("file/upload/finish", finishBody, ct).ConfigureAwait(false);

            Trace("[上传] 完成：" + fileName);
            return false;
        }

        // ---------------- 内部 ----------------

        /// <summary>换一片的 OSS 授权（客户端不签名，让服务端签）。</summary>
        private async Task<string> AuthorizeAsync(Dictionary<string, object> preData,
            string authMeta, string taskId, CancellationToken ct)
        {
            var body = new Dictionary<string, object>
            {
                // ★ auth_info 必须原样回传（它是 pre 返回的原始对象）
                { "auth_info", preData.ContainsKey("auth_info") ? preData["auth_info"] : null },
                { "auth_meta", authMeta },
                { "task_id", taskId },
            };
            var res = await PostAsync("file/upload/auth", body, ct).ConfigureAwait(false);
            var data = Data(res);
            string key = data != null ? Str(data, "auth_key") : "";
            if (string.IsNullOrEmpty(key))
                throw new UploadException("网盘没有签发上传授权" + Describe(res));
            return key;
        }

        private Task<Dictionary<string, object>> PostAsync(string path, object body,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string url = _apiBase + "/1/clouddrive/" + path + "?pr=" + _product + "&fr=pc";
            return _postJson(url, body);
        }

        /// <summary>
        /// 算整文件 MD5/SHA1（秒传判定要用，缺一不可）。
        ///
        /// <para>边读边报进度：大文件这一步要几十秒，没有反馈用户会以为卡死。</para>
        /// </summary>
        private static Task ComputeHashAsync(string path, long size,
            Action<UploadPhase, long, long> progress, CancellationToken ct)
        {
            // 交给线程池：这是纯 CPU+IO 的同步活，不占调用方的线程
            return Task.Run(() =>
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 4096, FileOptions.SequentialScan))
                {
                    byte[] buffer = new byte[HashBuffer];
                    long read = 0;
                    int n;
                    while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        read += n;
                        Report(progress, UploadPhase.Preparing, read, size);
                    }
                }
            }, ct);
        }

        private static void HashFile(string path, out string md5, out string sha1)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.SequentialScan))
            using (var m = MD5.Create())
            using (var s = SHA1.Create())
            {
                byte[] buffer = new byte[HashBuffer];
                int n;
                while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    m.TransformBlock(buffer, 0, n, null, 0);
                    s.TransformBlock(buffer, 0, n, null, 0);
                }
                m.TransformFinalBlock(new byte[0], 0, 0);
                s.TransformFinalBlock(new byte[0], 0, 0);
                md5 = ToHex(m.Hash);
                sha1 = ToHex(s.Hash);
            }
        }

        private static void Report(Action<UploadPhase, long, long> progress,
            UploadPhase phase, long sent, long size)
        {
            if (progress == null) return;
            try { progress(phase, sent, size); } catch { }
        }

        /// <summary>
        /// 取 pre 响应里的 callback 并转成 JSON 字符串。
        ///
        /// <para>★ 这是 C# 侧最容易踩的坑：JSON 反序列化后 <c>callback</c> 是
        /// <c>Dictionary</c> 而不是字符串，直接 <c>ToString()</c> 会得到
        /// <c>System.Collections.Generic.Dictionary`2[...]</c>，
        /// Base64 之后合并必然失败。必须自己序列化回 JSON。</para>
        /// </summary>
        private static string CallbackText(Dictionary<string, object> preData)
        {
            if (preData == null || !preData.ContainsKey("callback")) return "";
            object value = preData["callback"];
            if (value == null) return "";
            var text = value as string;
            if (text != null) return text;
            try { return Json.Serialize(value); }
            catch { return ""; }
        }

        /// <summary>HTTP 日期（RFC 1123 GMT）。OSS 的 Date 与 x-oss-date 都用它。</summary>
        private static string HttpDate()
        {
            // ★ 必须 InvariantCulture：否则中文文化的月份缩写会毁掉签名
            return DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string BuildCompleteXml(List<string> etags)
        {
            var sb = new StringBuilder("<CompleteMultipartUpload>");
            for (int i = 0; i < etags.Count; i++)
            {
                sb.Append("<Part><PartNumber>").Append(i + 1)
                  .Append("</PartNumber><ETag>").Append(XmlEscape(etags[i]))
                  .Append("</ETag></Part>");
            }
            sb.Append("</CompleteMultipartUpload>");
            return sb.ToString();
        }

        private static string XmlEscape(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            return v.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        /// <summary>对象键按 / 分段编码（斜杠保留，否则对象键就错了）。</summary>
        private static string EncodeObjectKey(string key)
        {
            string[] parts = key.Split('/');
            for (int i = 0; i < parts.Length; i++)
                parts[i] = Uri.EscapeDataString(parts[i]).Replace("+", "%20");
            return string.Join("/", parts);
        }

        private static string GuessMime(string name)
        {
            string ext = (Path.GetExtension(name) ?? "").ToLowerInvariant();
            switch (ext)
            {
                case ".zip": return "application/zip";
                case ".rar": return "application/x-rar-compressed";
                case ".7z": return "application/x-7z-compressed";
                case ".md": case ".txt": return "text/plain";
                case ".pdf": return "application/pdf";
                case ".mp4": return "video/mp4";
                case ".mkv": return "video/x-matroska";
                case ".mp3": return "audio/mpeg";
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".exe": return "application/x-msdownload";
                case ".doc": return "application/msword";
                case ".docx": return
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                case ".xls": return "application/vnd.ms-excel";
                case ".xlsx": return
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                default: return "application/octet-stream";
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static Dictionary<string, object> Data(Dictionary<string, object> res)
        {
            if (res == null || !res.ContainsKey("data")) return null;
            return res["data"] as Dictionary<string, object>;
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key)) return "";
            object v = d[key];
            return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private static bool Bool(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key)) return false;
            object v = d[key];
            if (v == null) return false;
            if (v is bool) return (bool)v;
            bool parsed;
            return bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out parsed)
                && parsed;
        }

        private static long AsLong(object v)
        {
            if (v == null) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        /// <summary>把网盘响应的 message 摘出来给用户看。</summary>
        private static string Describe(Dictionary<string, object> res)
        {
            if (res == null) return "";
            string message = Str(res, "message");
            string status = Str(res, "status");
            if (string.IsNullOrEmpty(message) && string.IsNullOrEmpty(status)) return "";
            var sb = new StringBuilder("（");
            if (!string.IsNullOrEmpty(status)) sb.Append("status=").Append(status);
            if (!string.IsNullOrEmpty(message))
            {
                if (sb.Length > 1) sb.Append(", ");
                sb.Append(message);
            }
            sb.Append("）");
            return sb.ToString();
        }

        private static string Snippet(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            string text = body.Length > 300 ? body.Substring(0, 300) + "…" : body;
            return "：" + text;
        }
    }
}
