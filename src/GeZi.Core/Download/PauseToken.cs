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

using System.Threading;
using System.Threading.Tasks;

namespace GeZi.Core.Download
{
    /// <summary>暂停令牌：替代 Python 里轮询 pause() 回调的方式。</summary>
    public sealed class PauseToken
    {
        private readonly ManualResetEventSlim _resumed = new ManualResetEventSlim(true);

        /// <summary>
        /// 「暂停时立刻掐断在途读」用的取消源（2026-10-13 新增）。
        ///
        /// 【为什么光有 WaitIfPaused 不够】原来的暂停只在"下一次读之前"检查，
        /// 于是已经发出去的 256 条连接要各自把 TCP 缓冲里那口数据慢慢读完才停 ——
        /// 用户点暂停后速度从几百 KB/s 一路衰减到 10 KB/s，界面长时间卡在
        /// 「暂停中…」（实测反馈：「每次会暂停很久，并发数 256 有时候又会变成暂停中」）。
        /// 现在 Pause() 直接 Cancel 这个令牌，worker 正在 await 的 ReadAsync 立刻抛出，
        /// 已写字节都记在 part.W 里，恢复后从断点重新申请 Range 即可，不重下。
        /// </summary>
        private CancellationTokenSource _abort = new CancellationTokenSource();

        public bool IsPaused => !_resumed.IsSet;

        /// <summary>当前这一轮暂停对应的"掐断"令牌；Resume 后会换成新的。</summary>
        public CancellationToken AbortToken => _abort.Token;

        public void Pause()
        {
            _resumed.Reset();
            var a = _abort;
            try { a.Cancel(); } catch { }
        }

        public void Resume()
        {
            if (_resumed.IsSet) return;
            // ⚠️ 顺序：先换新令牌，再放行。
            //    否则 worker 恢复后拿到的还是那个已取消的旧令牌，会立刻再次抛 OCE。
            var old = Interlocked.Exchange(ref _abort, new CancellationTokenSource());
            _resumed.Set();
            // 不 Dispose old：可能还有在途操作持有它的令牌；CTS 没有非托管资源，
            // 交给 GC 即可（一次暂停泄漏一个很小的对象，代价可忽略）。
        }

        /// <summary>暂停期间阻塞；取消时抛 OperationCanceledException。</summary>
        public async Task WaitIfPausedAsync(CancellationToken ct)
        {
            while (!_resumed.IsSet)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
    }
}
