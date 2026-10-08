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

namespace GeZi
{
    /// <summary>
    /// 直链短时缓存。沿用早期原型定下的 `LINK_TTL = 600`（10 分钟）。
    ///
    /// 为什么需要：同一次会话里，用户常对同一批文件反复操作
    /// （先「导出直链」看一眼、再点「下载」、再「重试失败的」）。
    /// 没有缓存就会每次都重新打一轮取链接口 —— 而夸克对取链有频率风控，
    /// 反复问很容易触发限流，反而让**真正需要**的那次取链失败。
    ///
    /// 缓存的直链必须是**未过期**的：夸克直链带时效签名，
    /// 缓存超过 TTL 就丢弃，否则会把过期链喂给下载器（下载器虽有自愈重取，
    /// 但那是兜底路径，正常路径不该依赖它）。
    ///
    /// 线程安全：取链发生在 UI 线程的 async 流程里，但 DownloadScheduler 的
    /// 续传回调（LinkRefresher）在后台线程 —— 所以加锁保护。
    /// </summary>
    internal sealed class LinkCache
    {
        /// <summary>直链缓存有效期（秒）。与 Python 版一致，取 600s = 10 分钟。</summary>
        private const int TtlSeconds = 600;

        private sealed class Entry
        {
            public string Url;
            public DateTime Stamp;
        }

        private readonly Dictionary<string, Entry> _map =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly object _lock = new object();

        /// <summary>命中且未过期时返回直链，否则返回 null。</summary>
        public string Get(string fid)
        {
            if (string.IsNullOrEmpty(fid))
                return null;
            lock (_lock)
            {
                Entry e;
                if (!_map.TryGetValue(fid, out e))
                    return null;
                if ((DateTime.UtcNow - e.Stamp).TotalSeconds > TtlSeconds)
                {
                    _map.Remove(fid);
                    return null;
                }
                return e.Url;
            }
        }

        /// <summary>写入（或刷新）一条直链。</summary>
        public void Put(string fid, string url)
        {
            if (string.IsNullOrEmpty(fid) || string.IsNullOrEmpty(url))
                return;
            lock (_lock)
                _map[fid] = new Entry { Url = url, Stamp = DateTime.UtcNow };
        }

        /// <summary>清空缓存。切换账号、重新解析分享时必须调用 —— 否则会拿旧账号的链。</summary>
        public void Clear()
        {
            lock (_lock)
                _map.Clear();
        }

        /// <summary>移除单条（下载器判定该链已失效时调用，避免继续命中坏链）。</summary>
        public void Invalidate(string fid)
        {
            if (string.IsNullOrEmpty(fid))
                return;
            lock (_lock)
                _map.Remove(fid);
        }
    }
}
