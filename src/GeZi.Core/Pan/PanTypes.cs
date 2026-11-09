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

namespace GeZi.Core.Pan
{
    /// <summary>
    /// 网盘**能力位**（2026-11-02 新增，网盘抽象 P1）。
    ///
    /// <para>【为什么用能力位而不是 try/catch】不同网盘支持的功能本来就不对等：
    /// UC 目前只有分享解析 + 取链，没有转存/删除/我的网盘；百度则几乎全有但都带签名。
    /// 如果 UI 靠"调了试试、失败就吞掉"来决定显示什么，就会出现
    /// 「按钮在那儿、点了报不支持」这种糊弄用户的界面。
    /// 让 provider 显式声明能力，UI 按声明**灰度隐藏**，用户看到的就都是能用的。</para>
    /// </summary>
    [Flags]
    public enum PanCapabilities
    {
        None = 0,

        /// <summary>能解析分享链接（pwd_id → stoken → 目录列表）。</summary>
        Share = 1 << 0,

        /// <summary>能浏览「我的网盘」目录。</summary>
        Mine = 1 << 1,

        /// <summary>能把分享文件转存到自己网盘。</summary>
        Save = 1 << 2,

        /// <summary>能删除网盘文件 / 清空回收站。</summary>
        Delete = 1 << 3,

        /// <summary>支持扫码登录。</summary>
        QrLogin = 1 << 4,

        /// <summary>支持粘贴 Cookie 登录。</summary>
        CookieLogin = 1 << 5,

        /// <summary>能从「我的网盘」直接取直链（无需转存）。</summary>
        MineDownload = 1 << 6,

        /// <summary>能取网盘容量。</summary>
        Capacity = 1 << 7,

        /// <summary>能把直链导出给外部下载器（aria2 / curl / Motrix）。</summary>
        Export = 1 << 8,
    }

    /// <summary>
    /// 网盘的**身份与展示信息**（2026-11-02 新增，P1）。
    ///
    /// <para>UI 只读这个类型画界面，不读具体客户端 —— 这样新增网盘时
    /// 界面代码一行都不用改，加一个 manifest/注册项就自动出现。</para>
    /// </summary>
    public sealed class PanMeta
    {
        /// <summary>内部标识，小写英文，如 <c>"quark"</c> / <c>"uc"</c> / <c>"baidu"</c>。</summary>
        public string Id { get; set; }

        /// <summary>完整显示名，如「夸克网盘」「UC 网盘」。</summary>
        public string DisplayName { get; set; }

        /// <summary>短显示名，用于空间紧张处（导航、标签），如「夸克」「UC」。</summary>
        public string ShortName { get; set; }

        /// <summary>XAML 资源里的图标键名（UI 直接 FindResource），无图标留空。</summary>
        public string IconKey { get; set; }

        /// <summary>该网盘支持的能力。</summary>
        public PanCapabilities Caps { get; set; }

        /// <summary>
        /// 登录方式说明：<c>"qr"</c> 只能扫码、<c>"cookie"</c> 只能粘 Cookie、
        /// <c>"both"</c> 两者都支持。UI 据此决定登录页摆哪几个入口。
        /// </summary>
        public string LoginKind { get; set; } = "both";

        /// <summary>扫码登录时，提示用户用哪个 App 扫（"夸克 App" / "UC 浏览器"）。</summary>
        public string ScanAppHint { get; set; }

        public bool Has(PanCapabilities c)
        {
            return (Caps & c) == c;
        }
    }

    /// <summary>
    /// 分享凭证 + 分享标题（2026-11-02 从 QuarkClient.ShareTokenInfo 提升，P1）。
    /// <para>UC 与夸克逐字段同构，所以直接共用 —— 这正是"同构家族"在类型层的体现。</para>
    /// </summary>
    public sealed class ShareTicket
    {
        public string Stoken { get; set; }
        public string Title { get; set; }
    }
}
