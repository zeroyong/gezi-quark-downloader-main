using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi
{
    /// <summary>
    /// 本地边下边播流服务：把「正在增长的目标文件」通过 http://127.0.0.1:随机端口 暴露出去。
    ///
    /// 为什么不直接把半截文件交给播放器：
    ///   · 播放器读到当前 EOF 就停，不会自己等文件继续变长；
    ///   · MP4 的索引在尾部时，半截文件根本没有 moov，播放器直接判损坏。
    /// 走 HTTP 后，播放器把它当成一个"很慢的远程视频"：服务端先发头，再按下载进度
    /// 持续写 body；播放器自然缓冲、继续读，也可以发 Range 请求跳转。
    ///
    /// 只监听回环地址 + 每次播放生成随机 token，不对外暴露，也不映射任意路径。
    /// </summary>
    internal sealed class LocalStreamServer : IDisposable
    {
        public static readonly LocalStreamServer Instance = new LocalStreamServer();

        /// <summary>日志出口，由 MainWindow 注入（可空）。</summary>
        public static Action<string> Log;

        private sealed class Entry
        {
            public long Total;
            public string Dest;
            /// <summary>按偏移读取：返回实际读到的字节数；暂时不可用返回 0。</summary>
            public Func<long, byte[], int, int> ReadAt;
            /// <summary>任务已完成（文件不会再增长）。</summary>
            public Func<bool> IsComplete;
            /// <summary>任务还在下载/排队（文件还会继续增长）。</summary>
            public Func<bool> CanGrow;
            /// <summary>把某段字节涉及的分片提到队首（播放器 seek / 读尾部索引时用）。</summary>
            public Func<long, long, bool> Boost;
        }

        private const int ReadBufSize = 128 * 1024;
        private const int PollMs = 120;
        // 播放位置每前进这么多，就把后面的 32MB 提成优先分片
        private const long BoostAhead = 32L * 1024 * 1024;
        private const long BoostStep = 16L * 1024 * 1024;
        // 连续这么久没有任何新数据可发就结束响应：
        // 不结束的话，任务暂停/失败后连接会一直挂着，播放器显示"正在缓冲"永远不动。
        private const int IdleLimitMs = 30_000;

        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();

        private HttpListener _listener;
        private int _port;

        /// <summary>
        /// 把一个可增长的字节源登记成播放器可打开的 URL。
        /// <paramref name="readAt"/> 由下载器提供（同时能读已合并前缀和未合并分片）。
        /// </summary>
        public string Publish(long total, string dest,
                              Func<long, byte[], int, int> readAt,
                              Func<bool> isComplete, Func<bool> canGrow,
                              Func<long, long, bool> boost)
        {
            if (total <= 0 || readAt == null || string.IsNullOrEmpty(dest)) return null;
            if (!EnsureStarted()) return null;

            string token = Guid.NewGuid().ToString("N");
            lock (_gate)
            {
                _entries[token] = new Entry
                {
                    Total = total,
                    Dest = dest,
                    ReadAt = readAt,
                    IsComplete = isComplete ?? (() => true),
                    CanGrow = canGrow ?? (() => false),
                    Boost = boost,
                };
            }

            // 路径里带上原文件名（只为让播放器看到 .mkv/.mp4 扩展名，据此选分离器）
            string name = Uri.EscapeDataString(Path.GetFileName(dest) ?? "video");
            return "http://127.0.0.1:" + _port + "/stream/" + token + "/" + name;
        }

        /// <summary>移除登记项（任务被删除时调用，避免字典无限增长）。</summary>
        public void Unpublish(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                var uri = new Uri(url);
                var segs = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (segs.Length >= 2)
                {
                    lock (_gate) _entries.Remove(segs[1]);
                }
            }
            catch { }
        }

        private bool EnsureStarted()
        {
            lock (_gate)
            {
                if (_listener != null && _listener.IsListening) return true;

                Exception last = null;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    int port = FreeLoopbackPort();
                    var listener = new HttpListener();
                    listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                    try
                    {
                        listener.Start();
                        _listener = listener;
                        _port = port;
                        last = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        try { listener.Close(); } catch { }
                    }
                }

                if (_listener == null)
                {
                    Log?.Invoke("边下边播服务启动失败: " + (last == null ? "未知原因" : last.Message));
                    return false;
                }

                _ = Task.Run(() => AcceptLoopAsync(_listener));
                Log?.Invoke("边下边播服务已启动: http://127.0.0.1:" + _port + "/");
                return true;
            }
        }

        private static int FreeLoopbackPort()
        {
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            try { return ((IPEndPoint)tcp.LocalEndpoint).Port; }
            finally { try { tcp.Stop(); } catch { } }
        }

        private async Task AcceptLoopAsync(HttpListener listener)
        {
            while (listener.IsListening && !_shutdown.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Log?.Invoke("边下边播服务接收请求失败: " + ex.Message);
                    break;
                }

                // 每个请求独立线程：PotPlayer 会同时开"索引 + 媒体"两条连接
                _ = Task.Run(() => HandleSafe(ctx));
            }
        }

        private void HandleSafe(HttpListenerContext ctx)
        {
            try { Handle(ctx); }
            catch (HttpListenerException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { Log?.Invoke("边下边播请求异常: " + ex.Message); }
            finally
            {
                try { ctx.Response.Close(); } catch { }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;

            if (!string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(req.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                res.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            var segs = req.Url.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length < 2 || segs[0] != "stream")
            {
                res.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            Entry entry;
            lock (_gate) _entries.TryGetValue(segs[1], out entry);
            if (entry == null)
            {
                res.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            long total = entry.Total;
            if (total <= 0)
            {
                res.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            long start = 0, end = total - 1;
            bool partial = false;
            if (!TryParseRange(req.Headers["Range"], total, out start, out end))
            {
                res.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                res.AddHeader("Content-Range", "bytes */" + total);
                return;
            }
            partial = start != 0 || end != total - 1;

            // 播放器一请求某个位置，就把它后面 32MB 的分片提到队首：
            // 跳转 / 读 MP4 尾部 moov 时不必再等"按顺序下到那一片"。
            Boost(entry, start, Math.Min(total - 1, start + BoostAhead));

            res.StatusCode = partial ? (int)HttpStatusCode.PartialContent : (int)HttpStatusCode.OK;
            res.ContentType = ContentTypeFor(entry.Dest);
            res.AddHeader("Accept-Ranges", "bytes");
            res.AddHeader("Cache-Control", "no-store");
            res.ContentLength64 = end - start + 1;
            if (partial)
                res.AddHeader("Content-Range",
                    "bytes " + start + "-" + end + "/" + total);

            if (string.Equals(req.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
                return;

            StreamBody(ctx, entry, start, end, total);
        }

        /// <summary>
        /// 把 [start,end] 写到响应体：有多少读多少；暂时没有数据就等下载线程继续写，
        /// 直到任务结束或连续 <see cref="IdleLimitMs"/> 没有进展。
        /// </summary>
        private void StreamBody(HttpListenerContext ctx, Entry entry,
                                long start, long end, long total)
        {
            var output = ctx.Response.OutputStream;
            try
            {
                // 先把响应头发出去：播放器等的是"连上了"，不是第一帧数据。
                // 尤其跳转到还没下到的位置时，可能要在循环里等几秒。
                try { output.Flush(); } catch { }

                var buf = new byte[ReadBufSize];
                long pos = start;
                long nextBoostAt = start;
                int waitedMs = 0;
                var token = _shutdown.Token;

                while (pos <= end && !token.IsCancellationRequested)
                {
                    int want = (int)Math.Min(buf.Length, end - pos + 1);
                    // 下载器自己决定读目标文件的已合并前缀，还是读某个分片文件
                    int n = entry.ReadAt(pos, buf, want);
                    if (n > 0)
                    {
                        output.Write(buf, 0, n);
                        output.Flush();       // 立刻下发，播放器才不用等缓冲
                        pos += n;
                        waitedMs = 0;

                        // 播放位置每前进 16MB，再把后面 32MB 提成优先分片 ——
                        // 连续播放 / 跳转后都能"边播边把后面的先下下来"。
                        if (pos >= nextBoostAt)
                        {
                            Boost(entry, pos, Math.Min(total - 1, pos + BoostAhead));
                            nextBoostAt = pos + BoostStep;
                        }
                        continue;
                    }

                    // 没有可发的新数据
                    bool complete = SafeBool(entry.IsComplete);
                    bool canGrow = SafeBool(entry.CanGrow);
                    if (complete || !canGrow) break;
                    if (waitedMs >= IdleLimitMs)
                    {
                        Log?.Invoke("边下边播：连续 " + (IdleLimitMs / 1000) + " 秒没有新数据，结束本次串流。");
                        break;
                    }

                    try { Task.Delay(PollMs, token).Wait(token); }
                    catch (OperationCanceledException) { break; }
                    waitedMs += PollMs;
                }
            }
            catch (HttpListenerException) { /* 播放器关掉/拖动进度条时会主动断连 */ }
            catch (IOException) { }
        }

        private static void Boost(Entry entry, long start, long end)
        {
            try { entry.Boost?.Invoke(start, end); }
            catch { }
        }

        /// <summary>解析单段 Range；非法/超出范围返回 false（调用方回 416）。</summary>
        private static bool TryParseRange(string header, long total,
                                          out long start, out long end)
        {
            start = 0;
            end = total - 1;
            if (string.IsNullOrWhiteSpace(header)) return true;
            if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return true;

            string spec = header.Substring(6).Trim();
            int comma = spec.IndexOf(',');
            if (comma >= 0) spec = spec.Substring(0, comma).Trim();   // 只支持单段

            int dash = spec.IndexOf('-');
            if (dash < 0) return false;

            string a = spec.Substring(0, dash).Trim();
            string b = spec.Substring(dash + 1).Trim();

            if (a.Length == 0)
            {
                long suffix;
                if (!long.TryParse(b, out suffix) || suffix <= 0) return false;
                start = Math.Max(0, total - suffix);
                end = total - 1;
                return true;
            }

            long s;
            if (!long.TryParse(a, out s) || s < 0) return false;
            start = s;
            if (b.Length > 0)
            {
                long e;
                if (!long.TryParse(b, out e)) return false;
                if (e < start) return false;
                end = Math.Min(e, total - 1);
            }
            if (start >= total) return false;
            return true;
        }

        private static string ContentTypeFor(string path)
        {
            switch ((Path.GetExtension(path) ?? "").ToLowerInvariant())
            {
                case ".mp4":
                case ".m4v":
                    return "video/mp4";
                case ".mkv":
                    return "video/x-matroska";
                case ".webm":
                    return "video/webm";
                case ".mov":
                    return "video/quicktime";
                case ".avi":
                    return "video/x-msvideo";
                case ".wmv":
                    return "video/x-ms-wmv";
                case ".flv":
                    return "video/x-flv";
                case ".ts":
                case ".m2ts":
                    return "video/mp2t";
                default:
                    return "application/octet-stream";
            }
        }

        private static bool SafeBool(Func<bool> f)
        {
            try { return f != null && f(); }
            catch { return false; }
        }

        public void Dispose()
        {
            try { _shutdown.Cancel(); } catch { }
            try { if (_listener != null) { _listener.Stop(); _listener.Close(); } } catch { }
            lock (_gate) _entries.Clear();
        }
    }
}
