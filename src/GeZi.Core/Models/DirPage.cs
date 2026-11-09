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

using System.Collections.Generic;

namespace GeZi.Core.Models
{
    /// <summary>
    /// 目录**一页**的结果（2026-11-02 从 QuarkClient 内嵌类提升为顶层类型，网盘抽象 P1）。
    ///
    /// <para>【为什么要提升】原本它是 <c>QuarkClient.DirPage</c> 内嵌类，但分页读取是
    /// **所有网盘共有**的能力 —— UC、百度、阿里都一样要"取一页、够不够、还有没有下一页"。
    /// 内嵌在某个具体客户端里，接口层就没法表达它，只能被迫按平台写分支。</para>
    ///
    /// <para>字段语义与原来完全一致，所以既有调用点只需改类型名（<c>QuarkClient.DirPage</c>
    /// → <c>DirPage</c>），行为零变化。</para>
    /// </summary>
    public sealed class DirPage
    {
        public readonly List<QuarkFileEntry> Items = new List<QuarkFileEntry>();

        /// <summary>本页之后是否还有下一页。由各 provider 依据接口返回的 _total 推算。</summary>
        public bool HasMore;
    }
}
