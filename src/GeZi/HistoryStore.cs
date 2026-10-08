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
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace GeZi
{
    /// <summary>一条下载历史记录。字段与 Python 版 records 对齐。</summary>
    [DataContract]
    public class HistoryRecord
    {
        [DataMember(Order = 1)] public string Name { get; set; }
        [DataMember(Order = 2)] public string Size { get; set; }     // 显示用文本（"1.23 GB"），与 Python 一致
        [DataMember(Order = 3)] public string Path { get; set; }     // 本地完整路径，供"打开/定位"
        [DataMember(Order = 4)] public string Status { get; set; }   // 完成 / 失败 / 已取消 ...
        [DataMember(Order = 5)] public string Time { get; set; }     // "yyyy-MM-dd HH:mm:ss"
    }

    /// <summary>
    /// 下载历史 + 未完成任务记录的持久化。
    ///
    /// 移植自 Python Flet 版的 `records`（历史，上限 200）与 `flet_pending`（未完成任务）。
    /// 两者原先都存在 core 的 state 里（同一个 state.json），这里合并成一个文件，
    /// 放在与配置同目录（`SettingsStore.FilePath` 的同级 `GeZi.state.json`），
    /// 保证便携（跟 exe 走）与可写回退（%LOCALAPPDATA%）行为一致。
    ///
    /// JSON 用 `DataContractJsonSerializer`（.NET Framework 自带，零 NuGet）。
    /// ⚠️ 不能用 `JavaScriptSerializer`：它所在的 System.Web.Extensions 依赖 System.Web，
    /// 而 **WPF 项目引用 System.Web 会导致 XAML 编译器找不到程序集**（实测 MC1000）。
    /// </summary>
    internal static class HistoryStore
    {
        private const int MaxRecords = 200;   // 与 Python Flet 版一致

        [DataContract]
        private sealed class State
        {
            [DataMember(Order = 1)] public List<HistoryRecord> Records { get; set; }
            [DataMember(Order = 2)] public List<PendingTask> Pending { get; set; }
        }

        private static string StatePath
        {
            get
            {
                try
                {
                    string dir = Path.GetDirectoryName(SettingsStore.FilePath);
                    return Path.Combine(dir, "GeZi.state.json");
                }
                catch
                {
                    return Path.Combine(Util.GetLocalAppDataDir(), "GeZi.state.json");
                }
            }
        }

        private static State Load()
        {
            try
            {
                if (File.Exists(StatePath))
                {
                    using (var fs = File.OpenRead(StatePath))
                    {
                        var ser = new DataContractJsonSerializer(typeof(State));
                        var s = ser.ReadObject(fs) as State;
                        if (s != null)
                        {
                            if (s.Records == null) s.Records = new List<HistoryRecord>();
                            if (s.Pending == null) s.Pending = new List<PendingTask>();
                            return s;
                        }
                    }
                }
            }
            catch { }
            return new State { Records = new List<HistoryRecord>(), Pending = new List<PendingTask>() };
        }

        private static void Save(State s)
        {
            try
            {
                string path = StatePath;
                string tmp = path + ".tmp";
                // 原子写：先写临时文件再替换，避免写一半留下半截 JSON 导致下次读崩。
                using (var fs = File.Create(tmp))
                {
                    var ser = new DataContractJsonSerializer(typeof(State));
                    ser.WriteObject(fs, s);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch { }
        }

        // ---------------- 历史记录 ----------------

        public static List<HistoryRecord> GetHistory()
        {
            var s = Load();
            var list = new List<HistoryRecord>(s.Records);
            list.Reverse();   // 新的在前
            return list;
        }

        /// <summary>追加一条历史。超出上限时丢最旧的。</summary>
        public static void AddHistory(string name, string size, string path, string status)
        {
            var s = Load();
            s.Records.Add(new HistoryRecord
            {
                Name = name,
                Size = size,
                Path = path,
                Status = status,
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            });
            if (s.Records.Count > MaxRecords)
                s.Records.RemoveRange(0, s.Records.Count - MaxRecords);
            Save(s);
        }

        public static void ClearHistory()
        {
            var s = Load();
            s.Records.Clear();
            Save(s);
        }

        // ---------------- 未完成任务 ----------------

        public static List<PendingTask> GetPending()
        {
            return Load().Pending;
        }

        /// <summary>整体覆盖未完成任务列表（由 UI 在任务状态变化时调用）。</summary>
        public static void SetPending(List<PendingTask> pending)
        {
            var s = Load();
            s.Pending = pending ?? new List<PendingTask>();
            Save(s);
        }

        public static void ClearPending()
        {
            var s = Load();
            s.Pending.Clear();
            Save(s);
        }
    }

    /// <summary>
    /// 未完成任务的跨重启记录。
    ///
    /// 与 Python 的 flet_pending 对齐。字段经过一次扩充：早期版本只记 9 个字段，
    /// 不足以**重新取链**（续传必须重新申请直链，因为直链带时效签名）。
    /// 现在补齐了 <see cref="FromShare"/> / <see cref="Fid"/> / <see cref="Token"/> /
    /// <see cref="PwdId"/> / <see cref="Stoken"/> / <see cref="Passcode"/>，
    /// 使启动时可以**不依赖原界面上下文**地重走"取链 → 续传"。
    ///
    /// 新增字段一律用 <c>Order &gt; 9</c>，且旧文件没有这些字段时反序列化为默认值
    /// （DataContractJsonSerializer 允许缺字段），保证向后兼容。
    /// </summary>
    [DataContract]
    public class PendingTask
    {
        [DataMember(Order = 1)] public string Name { get; set; }
        [DataMember(Order = 2)] public string Dest { get; set; }
        [DataMember(Order = 3)] public string Url { get; set; }
        [DataMember(Order = 4)] public string Fid { get; set; }
        [DataMember(Order = 5)] public string Token { get; set; }
        [DataMember(Order = 6)] public bool FromShare { get; set; }
        [DataMember(Order = 7)] public long Size { get; set; }
        [DataMember(Order = 8)] public bool StreamPlay { get; set; }
        [DataMember(Order = 9)] public string Status { get; set; }

        // ---- 以下为续传所需（Order ≥ 10），旧记录里缺省 = 默认值 ----

        /// <summary>分享链接的 pwd_id —— 分享模式下重取链要用。</summary>
        [DataMember(Order = 10)] public string PwdId { get; set; }

        /// <summary>分享的 stoken（取链凭证）—— 分享模式下重取链要用。</summary>
        [DataMember(Order = 11)] public string Stoken { get; set; }

        /// <summary>分享提取码；无码时为空串。</summary>
        [DataMember(Order = 12)] public string Passcode { get; set; }

        /// <summary>该任务的相对目录（用于重建目标路径，保持目录结构）。</summary>
        [DataMember(Order = 13)] public string RelDir { get; set; }

        /// <summary>写入模式是否为"边下边播"（独立分片+边合并）。</summary>
        [DataMember(Order = 14)] public bool StreamPlayMode { get; set; }

        /// <summary>
        /// 是否具备"可自动重取链"的完整信息。
        /// 免转存（游客直链）任务缺 stoken/token，重启后无法重取 → 不可续传。
        /// </summary>
        public bool CanResume
        {
            get
            {
                if (string.IsNullOrEmpty(Dest) || string.IsNullOrEmpty(Fid)) return false;
                if (FromShare) return !string.IsNullOrEmpty(Token) && !string.IsNullOrEmpty(PwdId)
                                  && !string.IsNullOrEmpty(Stoken);
                return true;   // 我的网盘：只需 fid
            }
        }
    }
}
