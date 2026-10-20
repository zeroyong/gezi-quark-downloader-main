// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 用 **OpenSSL** 发 HTTPS 请求的最小传输层（2026-10-11 新增，用来取代内置 CPython）。
    /// <c>pan.quark.cn</c> 的前置 ALB 按 **TLS ClientHello 指纹**过滤。实测（2026-10-11，均强制直连）：
    /// <code>
    ///   SChannel（.NET HttpClient / Windows 自带 curl）        → ❌ 连接被重置
    ///   OpenSSL 3.0.18（系统 Python 3.14 的 ssl）              → ❌ 连接被强制关闭
    ///   BouncyCastle 2.7（纯托管 TLS，含 TLS1.2/1.3 两种组合）  → ❌ 同上
    ///   OpenSSL 3.5.7（内置 CPython 自带的那套）                → ✅ HTTP 200
    /// </code>
    /// ⇒ **只有 OpenSSL 3.5.7 能过**，所以既不能换托管栈、也不能换版本，
    ///   只能**直接调用项目里已经固化的那份 OpenSSL 3.5.7**
    ///   （<c>lib/OpenSSL/libssl-3-x64.dll</c> + <c>libcrypto-3-x64.dll</c>）。
    /// 同一件事以前是"起一个 CPython 子进程跑 .py"，
    /// 代价是包体积 +11MB、并且带着"解释器 + 脚本 + 子进程"这组特征（会被杀软当成
    /// PyInstaller 式载荷，实测命中 BitDefender 系的 <c>Gen:Variant.Yogi</c>）。
    /// 现在改成 P/Invoke，**指纹完全一致，但不再需要解释器**。
    /// 本类刻意做得很小：只发 GET、只读到一个响应就结束
    /// （固定 <c>Connection: close</c>）。它<b>不是</b>通用 HTTP 客户端 ——
    /// 业务接口一律走 <c>QuarkClient</c> / <c>HttpClient</c>。
    /// ⚠️ <b>平台</b>：DLL 是 x64 的，进程必须是 64 位（AnyCPU 在 64 位系统上即为 x64）。
    ///    32 位系统上 <see cref="IsAvailable"/> 会是 false，调用方需自行降级
    ///    （登录页的「导入并登录」可以手动粘 Cookie）。
    /// </summary>
    public static class OpenSslTls
    {
        private const string SslDll = "libssl-3-x64.dll";
        private const string CryptoDll = "libcrypto-3-x64.dll";

        private static readonly object ProbeLock = new object();
        private static bool _probed;
        private static bool _available;
        private static string _version = "";
        private static string _probeError = "";

        /// <summary>本机能否用（DLL 是否加载成功）。首次调用会真正试加载一次。</summary>
        public static bool IsAvailable
        {
            get { EnsureProbed(); return _available; }
        }

        /// <summary>OpenSSL 运行时版本串（诊断用，例如 <c>OpenSSL 3.5.7 9 Jun 2026</c>）。</summary>
        public static string VersionString
        {
            get { EnsureProbed(); return _version; }
        }

        /// <summary>探测失败的原因（诊断用）。</summary>
        public static string ProbeError
        {
            get { EnsureProbed(); return _probeError; }
        }

        private static void EnsureProbed()
        {
            lock (ProbeLock)
            {
                if (_probed) return;
                _probed = true;
                try
                {
                    Native.OPENSSL_init_ssl(0, IntPtr.Zero);
                    IntPtr p = Native.OpenSSL_version(0);
                    _version = p == IntPtr.Zero ? "(未知)" : Marshal.PtrToStringAnsi(p);
                    _available = Native.TLS_client_method() != IntPtr.Zero;
                }
                catch (Exception ex)
                {
                    _available = false;
                    _probeError = ex.GetType().Name + ": " + ex.Message;
                }
            }
        }

        // ---------------- 响应 ----------------

        /// <summary>一次请求的结果。</summary>
        public sealed class Response
        {
            /// <summary>HTTP 状态码（-1 = 没拿到响应）。</summary>
            public int StatusCode = -1;

            /// <summary>完整响应头原文（含多行 <c>Set-Cookie</c>）。</summary>
            public string RawHeaders = "";

            /// <summary>响应体（已按 Content-Length / chunked 处理）。</summary>
            public string Body = "";

            /// <summary>从响应头里解析出的所有 Set-Cookie（**只取名字=值，不含属性**）。</summary>
            public readonly List<KeyValuePair<string, string>> Cookies =
                new List<KeyValuePair<string, string>>();

            public bool Ok { get { return StatusCode >= 200 && StatusCode < 400; } }
        }

        // ---------------- 请求 ----------------

        /// <summary>
        /// 发一个 HTTPS GET。
        /// </summary>
        /// <param name="host">主机名（同时用作 SNI 与 Host 头），如 <c>pan.quark.cn</c>。</param>
        /// <param name="pathAndQuery">以 <c>/</c> 开头的路径 + 查询串。</param>
        /// <param name="headers">额外请求头（可含 Cookie / User-Agent / Referer…）。</param>
        /// <param name="timeoutMs">连接与读写的超时（毫秒）。</param>
        /// <exception cref="IOException">握手失败、连接被重置等。</exception>
        public static Response Get(string host, string pathAndQuery,
            IDictionary<string, string> headers = null, int timeoutMs = 20000)
        {
            if (string.IsNullOrEmpty(host)) throw new ArgumentNullException("host");
            if (string.IsNullOrEmpty(pathAndQuery)) pathAndQuery = "/";
            if (!pathAndQuery.StartsWith("/")) pathAndQuery = "/" + pathAndQuery;

            EnsureProbed();
            if (!_available)
                throw new IOException("OpenSSL 不可用（" + _probeError + "）");

            IntPtr ctx = IntPtr.Zero, ssl = IntPtr.Zero, bio = IntPtr.Zero;
            TcpClient tcp = null;
            try
            {
                ctx = Native.SSL_CTX_new(Native.TLS_client_method());
                if (ctx == IntPtr.Zero)
                    throw new IOException("SSL_CTX_new 失败: " + Native.ErrText());

                // 只做传输，不做证书链校验 —— 这里要的是"能过那道指纹墙"，
                // 数据可信性由业务层（QuarkClient 的 JSON 解析 + 返回码）把关。
                Native.SSL_CTX_set_verify(ctx, Native.SSL_VERIFY_NONE, IntPtr.Zero);

                tcp = new TcpClient();
                tcp.ReceiveTimeout = timeoutMs;
                tcp.SendTimeout = timeoutMs;
                tcp.Connect(host, 443);

                ssl = Native.SSL_new(ctx);
                if (ssl == IntPtr.Zero)
                    throw new IOException("SSL_new 失败: " + Native.ErrText());

                bio = Native.BIO_new_socket(tcp.Client.Handle, Native.BIO_NOCLOSE);
                Native.SSL_set_bio(ssl, bio, bio);

                // SNI（SSL_set_tlsext_host_name 是宏，这里展开成 SSL_ctrl）
                IntPtr namePtr = Marshal.StringToHGlobalAnsi(host);
                try
                {
                    Native.SSL_ctrl(ssl, Native.SSL_CTRL_SET_TLSEXT_HOSTNAME,
                        Native.TLSEXT_NAMETYPE_host_name, namePtr);
                }
                finally { Marshal.FreeHGlobal(namePtr); }

                // ⚠️ 刻意**不设 ALPN** —— 与内置 CPython 的 ssl 默认行为一致（实测能过）。
                int rc = Native.SSL_connect(ssl);
                if (rc != 1)
                {
                    int err = Native.SSL_get_error(ssl, rc);
                    throw new IOException("TLS 握手失败 (SSL_get_error=" + err + "): " + Native.ErrText());
                }

                byte[] req = Encoding.ASCII.GetBytes(BuildRequest(host, pathAndQuery, headers));
                if (Native.SSL_write(ssl, req, req.Length) <= 0)
                    throw new IOException("SSL_write 失败: " + Native.ErrText());

                byte[] raw = ReadAll(ssl, 65536);
                return ParseResponse(raw);
            }
            finally
            {
                if (ssl != IntPtr.Zero) Native.SSL_free(ssl);
                if (ctx != IntPtr.Zero) Native.SSL_CTX_free(ctx);
                if (tcp != null) tcp.Close();
            }
        }

        private static string BuildRequest(string host, string pathAndQuery,
            IDictionary<string, string> headers)
        {
            var sb = new StringBuilder();
            sb.Append("GET ").Append(pathAndQuery).Append(" HTTP/1.1\r\n");
            sb.Append("Host: ").Append(host).Append("\r\n");
            sb.Append("Accept: */*\r\n");
            sb.Append("Connection: close\r\n");
            if (headers != null)
            {
                foreach (var kv in headers)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    if (string.Equals(kv.Key, "Host", StringComparison.OrdinalIgnoreCase)) continue;
                    sb.Append(kv.Key).Append(": ").Append(kv.Value ?? "").Append("\r\n");
                }
            }
            sb.Append("\r\n");
            return sb.ToString();
        }

        private static byte[] ReadAll(IntPtr ssl, int maxBytes)
        {
            using (var ms = new MemoryStream())
            {
                byte[] buf = new byte[8192];
                while (ms.Length < maxBytes)
                {
                    int n = Native.SSL_read(ssl, buf, buf.Length);
                    if (n <= 0)
                    {
                        // n == 0 / 负值都表示结束或出错。已经读到东西就当成正常结束
                        // （对端 Connection: close 时本就是这个表现）。
                        if (ms.Length == 0)
                        {
                            int err = Native.SSL_get_error(ssl, n);
                            if (err != 6 /*SSL_ERROR_ZERO_RETURN*/)
                                throw new IOException("读响应失败 (SSL_get_error=" + err + "): " + Native.ErrText());
                        }
                        break;
                    }
                    ms.Write(buf, 0, n);
                }
                return ms.ToArray();
            }
        }

        /// <summary>把原始字节拆成「状态行 / 响应头 / 响应体」。</summary>
        private static Response ParseResponse(byte[] raw)
        {
            var r = new Response();
            if (raw == null || raw.Length == 0) return r;

            // 头部按字节找 \r\n\r\n，避免把响应体里的二进制当文本处理
            int sep = IndexOf(raw, new byte[] { 13, 10, 13, 10 });
            byte[] headBytes = sep > 0 ? Sub(raw, 0, sep) : raw;
            byte[] bodyBytes = sep > 0 ? Sub(raw, sep + 4, raw.Length - sep - 4) : new byte[0];

            r.RawHeaders = Encoding.UTF8.GetString(headBytes);

            string[] lines = r.RawHeaders.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length > 0)
            {
                // HTTP/1.1 200 OK
                string first = lines[0];
                int sp = first.IndexOf(' ');
                if (sp > 0)
                {
                    int sp2 = first.IndexOf(' ', sp + 1);
                    string code = sp2 > 0 ? first.Substring(sp + 1, sp2 - sp - 1) : first.Substring(sp + 1);
                    int c;
                    if (int.TryParse(code.Trim(), out c)) r.StatusCode = c;
                }
            }

            bool chunked = false;
            for (int i = 1; i < lines.Length; i++)
            {
                string ln = lines[i];
                if (ln.Length == 0) continue;
                int colon = ln.IndexOf(':');
                if (colon <= 0) continue;
                string name = ln.Substring(0, colon).Trim();
                string value = ln.Substring(colon + 1).Trim();

                if (string.Equals(name, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    // 只取 "名字=值"，属性（Path/Domain/…）不要
                    int semi = value.IndexOf(';');
                    string pair = semi >= 0 ? value.Substring(0, semi) : value;
                    int eq = pair.IndexOf('=');
                    if (eq > 0)
                    {
                        r.Cookies.Add(new KeyValuePair<string, string>(
                            pair.Substring(0, eq).Trim(), pair.Substring(eq + 1).Trim()));
                    }
                }
                else if (string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                         && value.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    chunked = true;
                }
            }

            r.Body = chunked
                ? DecodeChunked(bodyBytes)
                : Encoding.UTF8.GetString(bodyBytes);
            return r;
        }

        private static string DecodeChunked(byte[] b)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < b.Length)
            {
                int eol = IndexOf(b, new byte[] { 13, 10 }, i);
                if (eol < 0) break;
                string sizeLine = Encoding.ASCII.GetString(b, i, eol - i).Trim();
                int semi = sizeLine.IndexOf(';');
                if (semi >= 0) sizeLine = sizeLine.Substring(0, semi);
                int size;
                if (!int.TryParse(sizeLine, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out size) || size <= 0)
                    break;
                int start = eol + 2;
                if (start + size > b.Length) size = b.Length - start;
                sb.Append(Encoding.UTF8.GetString(b, start, size));
                i = start + size + 2;   // 跳过块尾的 CRLF
            }
            return sb.ToString();
        }

        private static int IndexOf(byte[] hay, byte[] needle, int from = 0)
        {
            for (int i = from; i <= hay.Length - needle.Length; i++)
            {
                bool hit = true;
                for (int j = 0; j < needle.Length; j++)
                    if (hay[i + j] != needle[j]) { hit = false; break; }
                if (hit) return i;
            }
            return -1;
        }

        private static byte[] Sub(byte[] src, int start, int len)
        {
            var d = new byte[len];
            Buffer.BlockCopy(src, start, d, 0, len);
            return d;
        }

        // ---------------- P/Invoke ----------------

        private static class Native
        {
            public const int SSL_VERIFY_NONE = 0;
            public const int SSL_CTRL_SET_TLSEXT_HOSTNAME = 55;
            public const int TLSEXT_NAMETYPE_host_name = 0;
            public const int BIO_NOCLOSE = 0;

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern long OPENSSL_init_ssl(long opts, IntPtr settings);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr TLS_client_method();

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr SSL_CTX_new(IntPtr method);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern void SSL_CTX_free(IntPtr ctx);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern void SSL_CTX_set_verify(IntPtr ctx, int mode, IntPtr callback);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr SSL_new(IntPtr ctx);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern void SSL_free(IntPtr ssl);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern void SSL_set_bio(IntPtr ssl, IntPtr rbio, IntPtr wbio);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern long SSL_ctrl(IntPtr ssl, int cmd, long larg, IntPtr parg);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern int SSL_connect(IntPtr ssl);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern int SSL_write(IntPtr ssl, byte[] buf, int len);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern int SSL_read(IntPtr ssl, byte[] buf, int len);

            [DllImport(SslDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern int SSL_get_error(IntPtr ssl, int ret);

            [DllImport(CryptoDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr BIO_new_socket(IntPtr sock, int closeFlag);

            [DllImport(CryptoDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr OpenSSL_version(int type);

            [DllImport(CryptoDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern ulong ERR_get_error();

            [DllImport(CryptoDll, CallingConvention = CallingConvention.Cdecl)]
            public static extern void ERR_error_string_n(ulong e, byte[] buf, int len);

            public static string ErrText()
            {
                ulong e = ERR_get_error();
                if (e == 0) return "(无 OpenSSL 错误)";
                byte[] buf = new byte[256];
                ERR_error_string_n(e, buf, buf.Length);
                int n = Array.IndexOf(buf, (byte)0);
                return Encoding.ASCII.GetString(buf, 0, n < 0 ? buf.Length : n);
            }
        }
    }
}
