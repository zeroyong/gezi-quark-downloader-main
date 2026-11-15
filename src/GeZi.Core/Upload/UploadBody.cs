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
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.Core.Upload
{
    /// <summary>
    /// 只读文件的 <c>[start, end)</c> 区间流（2026-11-10 新增）。
    ///
    /// <para>【为什么要单独包一层】分片上传要求**请求体长度必须与声明一致**。
    /// 直接把整个 FileStream 塞给 StreamContent 会把整个文件发出去；
    /// 这里按区间截断，读到区间末尾就返回 0，让 StreamContent 正常收尾。</para>
    ///
    /// <para>★ 每次 <see cref="Read"/> 都回调已读字节数，界面的进度条就靠它。
    /// 不用轮询文件位置 —— 那样在 buffered 流上会读到滞后值。</para>
    /// </summary>
    internal sealed class UploadRangeStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _ownsInner;
        private long _remaining;
        private readonly Action<long> _onProgress;
        private readonly CancellationToken _ct;

        /// <summary>已读字节数（供进度计算）。</summary>
        public long ReadBytes { get; private set; }

        /// <param name="inner">底层流，位置必须已在区间起点。</param>
        /// <param name="length">要读的长度。</param>
        /// <param name="onProgress">每读完一块回调一次（累计字节数）。可为 null。</param>
        /// <param name="ct">取消令牌。</param>
        /// <param name="ownsInner">是否在 Dispose 时一并关掉底层流。</param>
        public UploadRangeStream(Stream inner, long length, Action<long> onProgress,
            CancellationToken ct = default, bool ownsInner = false)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            if (length < 0) throw new ArgumentOutOfRangeException("length");
            _inner = inner;
            _remaining = length;
            _onProgress = onProgress;
            _ct = ct;
            _ownsInner = ownsInner;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }

        public override long Length { get { throw new NotSupportedException(); } }

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _ct.ThrowIfCancellationRequested();
            if (_remaining <= 0) return 0;

            int want = (int)Math.Min(count, _remaining);
            int n = _inner.Read(buffer, offset, want);
            if (n <= 0) return 0;

            _remaining -= n;
            ReadBytes += n;
            if (_onProgress != null)
            {
                try { _onProgress(ReadBytes); } catch { }
            }
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            _ct.ThrowIfCancellationRequested();
            if (_remaining <= 0) return 0;

            int want = (int)Math.Min(count, _remaining);
            int n = await _inner.ReadAsync(buffer, offset, want, cancellationToken)
                .ConfigureAwait(false);
            if (n <= 0) return 0;

            _remaining -= n;
            ReadBytes += n;
            if (_onProgress != null)
            {
                try { _onProgress(ReadBytes); } catch { }
            }
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _ownsInner)
            {
                try { _inner.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 定长流内容（2026-11-10 新增）。
    ///
    /// <para>【为什么不用 <c>new StreamContent(stream)</c>】StreamContent 对
    /// 非 seekable 流会把 Content-Length 判成未知并改用 chunked 传输，
    /// 而 OSS 的分片 PUT **不接受 chunked**（签名里的 Content-Length 也是按实际长度算的）。
    /// 这里显式给出长度，保证发的是定长请求体。</para>
    /// </summary>
    internal sealed class UploadRangeContent : HttpContent
    {
        private readonly Stream _source;
        private readonly long _length;
        private readonly Action<long> _onProgress;
        private readonly CancellationToken _ct;

        public UploadRangeContent(Stream source, long length, Action<long> onProgress,
            CancellationToken ct)
        {
            _source = source;
            _length = length;
            _onProgress = onProgress;
            _ct = ct;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
        {
            var range = new UploadRangeStream(_source, _length, _onProgress, _ct);
            byte[] buffer = new byte[81920];
            while (true)
            {
                _ct.ThrowIfCancellationRequested();
                int n = await range.ReadAsync(buffer, 0, buffer.Length, _ct).ConfigureAwait(false);
                if (n <= 0) break;
                await stream.WriteAsync(buffer, 0, n, _ct).ConfigureAwait(false);
            }
            await stream.FlushAsync(_ct).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }
}
