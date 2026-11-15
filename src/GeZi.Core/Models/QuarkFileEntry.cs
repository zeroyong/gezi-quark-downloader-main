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

namespace GeZi.Core.Models
{
    /// <summary>夸克/UC 目录/文件条目，经过归一化后的形态。</summary>
    public class QuarkFileEntry
    {
        public string Fid { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }
        public bool IsDir { get; set; }
        /// <summary>分享场景下的 share_fid_token，取直链时需要。</summary>
        public string Token { get; set; }

        /// <summary>
        /// 网盘返回的缩略图 URL（2026-11-15 新增，图片/视频类文件才有）。
        /// 可能是 <c>//image.xxx/…</c> 这种协议相对地址 —— 展示方负责补 https:。
        /// 拿不到（接口没给 / 非媒体文件）为 null，列表回落到类型图标。
        /// </summary>
        public string Thumbnail { get; set; }

        /// <summary>修改时间（Unix 秒）。接口没给 / 解析失败为 0，界面就不显示日期。</summary>
        public long UpdatedAt { get; set; }

        /// <summary>
        /// 修改时间的**智能解析**（2026-11-15：实测夸克分享列表的日期没显示出来 ——
        /// 参照 wenxi（<c>j.str('updated_at').ifEmpty(j.str('modify_time'))</c>）发现该字段
        /// 形态不保证是数字，可能是字符串秒数甚至日期文本。所以这里按
        /// 「多字段回退 + 三种形态兼容」解析，全部失败返回 0（界面不显示，不猜测）。
        /// </summary>
        public static long ParseUpdatedAt(Dictionary<string, object> item)
        {
            if (item == null) return 0;
            // 字段名回退链：夸克/UC 用 updated_at；modify_time 是 wenxi 实测过的别名
            string[] keys = { "updated_at", "modify_time", "created_at", "last_update_time" };
            foreach (var key in keys)
            {
                object o;
                if (!item.TryGetValue(key, out o) || o == null) continue;
                var s = o.ToString().Trim();
                if (s.Length == 0) continue;

                // 形态一：纯数字（秒或毫秒）。>1e11 视为毫秒（1e11 秒 ≈ 公元 5138 年，不可能是秒）
                long n;
                if (long.TryParse(s, out n) && n > 0)
                    return n > 100000000000L ? n / 1000 : n;

                // 形态二：日期文本（"2024-12-23 19:46" / ISO8601 等）
                DateTime dt;
                if (DateTime.TryParse(s, out dt) && dt.Year > 2000)
                {
                    try { return new DateTimeOffset(dt).ToUnixTimeSeconds(); }
                    catch { }
                }
            }
            return 0;
        }
    }
}
