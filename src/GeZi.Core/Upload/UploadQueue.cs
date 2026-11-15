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
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.Core.Upload
{
    /// <summary>
    /// 一批文件串行上传的队列（2026-11-10 新增）。
    ///
    /// <para>【为什么串行不并发】上传吃的是**上行带宽**，多文件并发只会互相抢，
    /// 总时长不变而进度更乱。对标实现（YunX）也是串行。
    /// 单文件内部已经是分片顺序 PUT，够用了。</para>
    ///
    /// <para>【节流】进度回调来自上传线程，直接转 UI 会打爆主线程
    /// （每 80KB 一次）。这里统一节流到约 120ms 一次，阶段变化与收尾不节流。</para>
    /// </summary>
    public sealed class UploadQueue
    {
        /// <summary>进度上报最小间隔（毫秒）。</summary>
        private const int ProgressIntervalMs = 120;

        private readonly object _gate = new object();
        private CancellationTokenSource _cts;
        private volatile bool _running;
        private volatile bool _cancelRequested;

        /// <summary>是否正在上传。</summary>
        public bool IsRunning { get { return _running; } }

        /// <summary>
        /// 开始上传一批文件。
        ///
        /// <para>同一时刻只允许一批在跑（重复调用直接忽略）——
        /// 界面上按钮会禁用，这里再兜一层。</para>
        /// </summary>
        /// <param name="files">本地文件绝对路径列表。</param>
        /// <param name="parentFid">目标目录 fid。</param>
        /// <param name="uploadOne">
        /// 单个文件的上传动作：<c>(本地路径, 文件名, 进度回调, 取消令牌) → 是否秒传</c>。
        /// 由调用方（provider）注入，这样队列本身与具体网盘无关。
        /// </param>
        /// <param name="progress">节流后的进度回调（在调用方上下文里触发）。</param>
        /// <param name="ct">外层取消令牌（关窗口时用）。</param>
        public async Task<UploadOutcome> RunAsync(
            IList<string> files, string parentFid,
            Func<string, string, Action<UploadPhase, long, long>, CancellationToken, Task<bool>> uploadOne,
            IProgress<UploadProgress> progress,
            CancellationToken ct = default)
        {
            var outcome = new UploadOutcome();
            if (files == null || files.Count == 0)
            {
                outcome.Error = "没有选择文件";
                return outcome;
            }
            if (uploadOne == null) throw new ArgumentNullException("uploadOne");

            lock (_gate)
            {
                if (_running) return new UploadOutcome { Error = "已有上传任务在进行中" };
                _running = true;
                _cancelRequested = false;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            }

            try
            {
                int total = files.Count;
                for (int i = 0; i < total; i++)
                {
                    string path = files[i];
                    string name = SafeName(path);

                    if (_cts.IsCancellationRequested)
                    {
                        outcome.Cancelled = true;
                        break;
                    }

                    int index = i + 1;
                    var throttle = new ProgressThrottle(index, total, name, progress);

                    try
                    {
                        bool rapid = await uploadOne(path, name,
                            (phase, sent, size) => throttle.Report(phase, sent, size),
                            _cts.Token).ConfigureAwait(false);

                        outcome.Uploaded++;
                        if (rapid) outcome.RapidUploaded++;
                    }
                    catch (OperationCanceledException)
                    {
                        outcome.Cancelled = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        // 一个文件失败就停下：后面的文件大概率也是同一个原因
                        // （登录失效 / 空间不足），继续传只会浪费用户时间。
                        if (_cancelRequested) outcome.Cancelled = true;
                        else
                        {
                            outcome.Failed = 1;
                            outcome.Error = "「" + name + "」上传失败："
                                + Describe(ex);
                        }
                        break;
                    }
                    finally
                    {
                        throttle.Flush();
                    }
                }
            }
            finally
            {
                lock (_gate)
                {
                    _running = false;
                    if (_cts != null)
                    {
                        try { _cts.Dispose(); } catch { }
                        _cts = null;
                    }
                }
            }

            return outcome;
        }

        /// <summary>请求取消（正在发的那一片会立刻被打断）。</summary>
        public void Cancel()
        {
            lock (_gate)
            {
                if (!_running) return;
                _cancelRequested = true;
                if (_cts != null)
                {
                    try { _cts.Cancel(); } catch { }
                }
            }
        }

        private static string SafeName(string path)
        {
            try { return Path.GetFileName(path); }
            catch { return path ?? ""; }
        }

        private static string Describe(Exception ex)
        {
            if (ex is UploadException) return ex.Message;
            if (ex is IOException) return "读写本地文件失败（" + ex.Message + "）";
            var inner = ex.InnerException;
            if (inner != null && !string.IsNullOrEmpty(inner.Message))
                return ex.Message + "（" + inner.Message + "）";
            return string.IsNullOrEmpty(ex.Message) ? "未知错误" : ex.Message;
        }

        /// <summary>把高频进度压到约 120ms 一次（阶段变化与 100% 立即放行）。</summary>
        private sealed class ProgressThrottle
        {
            private readonly int _index;
            private readonly int _total;
            private readonly string _name;
            private readonly IProgress<UploadProgress> _sink;
            private readonly DateTime _start = DateTime.UtcNow;
            private DateTime _last = DateTime.MinValue;
            private UploadPhase _lastPhase = (UploadPhase)(-1);
            // 记住最后一次真实进度：Flush 必须用它，不能写死 0
            // （否则界面会在每个文件结束时闪一下「0 B / 0 B」）
            private long _lastSent;
            private long _lastSize;

            public ProgressThrottle(int index, int total, string name,
                IProgress<UploadProgress> sink)
            {
                _index = index;
                _total = total;
                _name = name;
                _sink = sink;
            }

            public void Report(UploadPhase phase, long sent, long size)
            {
                if (_sink == null) return;
                bool phaseChanged = phase != _lastPhase;
                bool done = size > 0 && sent >= size;
                var now = DateTime.UtcNow;
                if (!phaseChanged && !done
                    && (now - _last).TotalMilliseconds < ProgressIntervalMs) return;

                _last = now;
                _lastPhase = phase;
                _lastSent = sent;
                _lastSize = size;
                Push(phase, sent, size);
            }

            /// <summary>
            /// 补发一次当前状态（保证收尾那一帧一定到达界面）。
            /// ★ 必须带**最后一次真实进度**：写死 0 会让界面在文件结束时
            /// 闪一下「0 B / 0 B」，看起来像进度回退了。
            /// </summary>
            public void Flush()
            {
                if (_sink == null) return;
                Push(_lastPhase < 0 ? UploadPhase.Uploading : _lastPhase, _lastSent, _lastSize);
            }

            private void Push(UploadPhase phase, long sent, long size)
            {
                try
                {
                    _sink.Report(new UploadProgress
                    {
                        Index = _index,
                        Count = _total,
                        FileName = _name,
                        Sent = sent,
                        Size = size,
                        Phase = phase,
                    });
                }
                catch { }
            }
        }
    }
}
