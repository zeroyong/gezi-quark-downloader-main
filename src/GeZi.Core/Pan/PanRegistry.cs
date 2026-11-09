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

namespace GeZi.Core.Pan
{
    /// <summary>
    /// 网盘提供者注册表（2026-11-02 新增，P1）。
    ///
    /// <para>【职责】把「平台 id → 怎么创建客户端」这一层收到一处。
    /// UI 与下载调度层都通过它拿 provider，不再关心具体是哪个客户端类。</para>
    ///
    /// <para>【P2 会扩展成什么】下一步（P2）这里会接上 manifest 加载：
    /// <c>providers/*.json</c> 声明的网盘由 <c>DeclarativePanProvider</c> 直接驱动，
    /// 只有 manifest 表达不了（如百度的签名算法）才需要在 <see cref="Register"/> 里挂代码实现。
    /// 两条路对上层是同一个 <see cref="IPanProvider"/>。</para>
    /// </summary>
    public static class PanRegistry
    {
        /// <summary>工厂签名：给定 Cookie，造出一个 provider。</summary>
        public delegate IPanProvider Factory(string cookie);

        private static readonly Dictionary<string, Registration> Map =
            new Dictionary<string, Registration>(StringComparer.OrdinalIgnoreCase);

        private sealed class Registration
        {
            public PanMeta Meta;
            public Factory Create;
        }

        static PanRegistry()
        {
            // 目前编译期注册两个「同构家族」成员。
            // P2 之后，这里的每一项都应当能由 manifest 声明替代。
            Register(new PanMeta
            {
                Id = "quark", DisplayName = "夸克网盘", ShortName = "夸克",
                IconKey = "IconQuark", LoginKind = "both", ScanAppHint = "夸克 App",
                Caps = PanCapabilities.Share | PanCapabilities.Mine | PanCapabilities.Save
                     | PanCapabilities.Delete | PanCapabilities.QrLogin | PanCapabilities.CookieLogin
                     | PanCapabilities.MineDownload | PanCapabilities.Capacity | PanCapabilities.Export,
            }, cookie => new QuarkProvider(cookie));

            Register(new PanMeta
            {
                Id = "uc", DisplayName = "UC 网盘", ShortName = "UC",
                IconKey = "IconUc", LoginKind = "both", ScanAppHint = "UC 浏览器 / UC 网盘 App",
                Caps = PanCapabilities.Share | PanCapabilities.QrLogin
                     | PanCapabilities.CookieLogin | PanCapabilities.Capacity,
            }, cookie => new UcProvider(cookie));
        }

        /// <summary>注册（或覆盖）一个网盘实现。</summary>
        public static void Register(PanMeta meta, Factory create)
        {
            if (meta == null || string.IsNullOrEmpty(meta.Id) || create == null) return;
            Map[meta.Id] = new Registration { Meta = meta, Create = create };
        }

        /// <summary>所有已注册的网盘元信息（UI 按这个顺序摆平台切换）。</summary>
        public static PanMeta[] All()
        {
            var list = new List<PanMeta>();
            foreach (var kv in Map) list.Add(kv.Value.Meta);
            return list.ToArray();
        }

        /// <summary>按 id 取元信息；不存在返回 null。</summary>
        public static PanMeta MetaOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            Registration r;
            return Map.TryGetValue(id, out r) ? r.Meta : null;
        }

        /// <summary>平台的显示名（UI 用）。未注册则原样返回 id，绝不返回空串。</summary>
        public static string DisplayNameOf(string id)
        {
            var m = MetaOf(id);
            return m == null || string.IsNullOrEmpty(m.DisplayName) ? (id ?? "") : m.DisplayName;
        }

        /// <summary>
        /// 造一个 provider。
        /// <para>未注册的 id 回退夸克 —— 兼容老配置里没有 ProviderId 的记录，
        /// 宁可"按夸克试"也不要让老任务直接失败。</para>
        /// </summary>
        public static IPanProvider Create(string id, string cookie)
        {
            Registration r = null;
            if (!string.IsNullOrEmpty(id)) Map.TryGetValue(id, out r);
            if (r == null) Map.TryGetValue("quark", out r);
            if (r == null)
                throw new InvalidOperationException("没有任何网盘实现被注册（PanRegistry 初始化失败）。");
            return r.Create(cookie);
        }
    }
}
