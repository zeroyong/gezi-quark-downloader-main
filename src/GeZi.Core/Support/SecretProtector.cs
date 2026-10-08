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
using System.Security.Cryptography;
using System.Text;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 用 Windows DPAPI 加密/解密本地敏感字符串（Cookie 登录态等）。
    ///
    /// 设计要点：
    /// - 作用域 CurrentUser：密文只能被同一台机器的同一个 Windows 用户解开，
    ///   换个用户或把配置文件拷到别的机器都无法读取（这正是我们要的）。
    /// - 密文以 "enc:v1:" 前缀标记，用于识别与平滑迁移：
    ///   读到没有前缀的字符串 → 视为旧版明文，直接返回原文，下次保存时自动加密。
    /// - 加解密失败一律降级为“明文原样返回/返回原文”，绝不因加密问题让程序起不来。
    /// </summary>
    public static class SecretProtector
    {
        private const string Prefix = "enc:v1:";

        // 额外的熵，防止别的程序用无参 DPAPI 直接解出我们的数据
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GeZi.Downloader.Cookie.v1");

        /// <summary>是否已加密（带我们的前缀标记）。</summary>
        public static bool IsProtected(string value)
            => !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

        /// <summary>
        /// 加密明文。已是密文则原样返回；空字符串返回空字符串。
        /// </summary>
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain))
                return "";
            if (IsProtected(plain))
                return plain;

            try
            {
                byte[] raw = Encoding.UTF8.GetBytes(plain);
                byte[] enc = ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser);
                return Prefix + Convert.ToBase64String(enc);
            }
            catch
            {
                // 加密不可用（极罕见：用户配置文件损坏等）→ 保持可用性优先
                return plain;
            }
        }

        /// <summary>
        /// 解密。无前缀视为旧版明文直接原样返回；解不开（换了机器/换了用户/数据损坏）
        /// 则返回空串 —— 让调用方按“未登录”处理，而不是拿到一堆乱码去请求接口。
        /// </summary>
        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored))
                return "";
            if (!IsProtected(stored))
                return stored;   // 旧版明文，向后兼容

            try
            {
                string b64 = stored.Substring(Prefix.Length);
                byte[] enc = Convert.FromBase64String(b64);
                byte[] raw = ProtectedData.Unprotect(enc, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(raw);
            }
            catch
            {
                // 解密失败：多半是配置文件被复制到了别的机器/用户
                return "";
            }
        }
    }
}
