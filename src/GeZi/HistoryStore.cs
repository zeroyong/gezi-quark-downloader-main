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
    /// 一条「解析过的分享」记录（按 <c>h</c> 唤出的历史面板用）。
    ///
    /// 【为什么不复用 HistoryRecord】那条是**下载**历史（记的是本地文件路径与大小），
    /// 这条记的是**分享**（pwd_id / 提取码），两者字段没有交集，混在一起两边都要判空。
    /// </summary>
    [DataContract]
    public class ShareHistoryEntry
    {
        [DataMember(Order = 1)] public string PwdId { get; set; }

        /// <summary>提取码；无码时为空串。</summary>
        [DataMember(Order = 2)] public string Passcode { get; set; }

        /// <summary>解析时进入的目录（带 fid 的分享链接才有意义，普通分享是 "0"）。</summary>
        [DataMember(Order = 3)] public string StartFid { get; set; }

        /// <summary>
        /// 面板里显示的名字：解析后根目录里的**第一个文件夹名**。
        /// 没有文件夹（分享根目录只有散文件）时退化成「（根目录）· N 个文件」。
        /// </summary>
        [DataMember(Order = 4)] public string Name { get; set; }

        /// <summary>上面那个名字是不是文件夹 —— 决定面板里用文件夹图标还是文件图标。</summary>
        [DataMember(Order = 5)] public bool IsFolder { get; set; }

        /// <summary>解析时刻。"yyyy-MM-dd HH:mm:ss"，与下载历史同一个格式。</summary>
        [DataMember(Order = 6)] public string Time { get; set; }

        /// <summary>
        /// 用户手动「置顶」的标记：置顶的始终排在最前，**不受后续解析的影响**。
        ///
        /// 【为什么不是"把时间改成现在"】那样只是把它挪到最前面一次，而且显示的时间
        /// 会变成谎话（明明是上周解析的，却写着"今天"）。
        /// </summary>
        [DataMember(Order = 7)] public bool Pinned { get; set; }
    }

    /// <summary>
    /// 下载历史 + 未完成任务记录 + 解析历史的持久化。
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

        /// <summary>
        /// 解析历史的上限。比下载历史小得多：它是"最近打开过什么"，翻久了没意义，
        /// 而且每条都要在面板里画出来。
        /// </summary>
        private const int MaxShareHistory = 50;

        [DataContract]
        private sealed class State
        {
            [DataMember(Order = 1)] public List<HistoryRecord> Records { get; set; }
            [DataMember(Order = 2)] public List<PendingTask> Pending { get; set; }
            /// <summary>解析历史。⚠️ 老 state.json 里没有这个字段 → 反序列化为 null，Load 里补空表。</summary>
            [DataMember(Order = 3)] public List<ShareHistoryEntry> Shares { get; set; }
            /// <summary>
            /// 任务列表快照（2026-10-13 新增）：已完成 / 失败 / 取消 / 未完成的任务都记一份，
            /// 重启后任务页能原样恢复（含进度）。老 state.json 里没有 → Load 补空表。
            /// </summary>
            [DataMember(Order = 4)] public List<TaskSnapshot> Tasks { get; set; }
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
                    return Path.Combine(Util.GetDataDir(), "GeZi.state.json");
                }
            }
        }

        /// <summary>
        /// 读不出 state.json 时**不返回空表**，而是返回 null。
        ///
        /// <para><b>【2026-10-22 修复的数据丢失】</b>用户实测：打包脚本把程序目录整个
        /// 删掉重建（连带删掉迁移过来的 state.json），随后程序退出时
        /// <see cref="MainWindow.SaveTaskListNow"/> 调 <see cref="SetTasks"/> ——
        /// 而 <see cref="SetTasks"/> 是「Load() → 改 → Save()」：
        /// Load() 发现文件没了就返回**空表**，Save() 再把这个空表**写回去**。
        /// 于是原有的 166 条任务快照、189 条下载历史被一次"清空"覆盖，
        /// 而且**不可恢复**（原文件已经没了）。</para>
        ///
        /// <para>现在：读不出来就返回 null，调用方据此**拒绝写入**，
        /// 保住磁盘上已有的那份（哪怕它此刻也是空的，好过被反复覆盖）。
        /// 下次启动如果文件能正常读，数据照旧恢复。</para>
        /// </summary>
        private static State Load()
        {
            try
            {
                if (!File.Exists(StatePath))
                {
                    // 文件不存在 —— 可能是首次运行，也可能是被外部删掉了。
                    // 两种情况都**不能**返回空表（会被写回去覆盖别处的数据）。
                    return null;
                }

                using (var fs = File.OpenRead(StatePath))
                {
                    var ser = new DataContractJsonSerializer(typeof(State));
                    var s = ser.ReadObject(fs) as State;
                    if (s == null) return null;   // 反序列化出 null 也算读失败
                    if (s.Records == null) s.Records = new List<HistoryRecord>();
                    if (s.Pending == null) s.Pending = new List<PendingTask>();
                    if (s.Shares == null) s.Shares = new List<ShareHistoryEntry>();
                    if (s.Tasks == null) s.Tasks = new List<TaskSnapshot>();
                    return s;
                }
            }
            catch
            {
                // 读失败（半截 JSON / 被占用 / 权限）→ 同样不返回空表
                return null;
            }
        }

        /// <summary>
        /// 只读场景用：读不到就返回一个空 State。
        ///
        /// <para>⚠️ <b>只给"只读"用</b>。写场景（AddHistory / SetTasks / SetPending…）
        /// 必须自己判 <see cref="Load"/> 的返回值是不是 null ——
        /// 读不到时<b>不能</b>拿空表去写回（那会把磁盘上已有的数据覆盖掉）。</para>
        /// </summary>
        private static State EmptyState()
        {
            return new State
            {
                Records = new List<HistoryRecord>(),
                Pending = new List<PendingTask>(),
                Shares = new List<ShareHistoryEntry>(),
                Tasks = new List<TaskSnapshot>(),
            };
        }

        private static void Save(State s)
        {
            if (s == null) return;
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

                // ⚠️ 【2026-10-22】原来这里是 File.Delete(path) + File.Move(tmp, path) ——
                //   Delete 与 Move 之间**目标文件不存在**，这期间：
                //     · 本进程自己 Load() 会拿到空表；
                //     · 另一个进程 Load() 同样拿到空表。
                //   而 Load() 拿到空表后，Save() 又会把空表写回去 —— 数据就没了。
                //   改用 File.Replace：它内部是原子替换（NTFS 的 ReplaceFile），
                //   **不存在"文件消失"的窗口期**，任何时刻读到的都是完整的一份
                //   （旧内容或新内容）。
                if (File.Exists(path))
                {
                    // 先备份旧的（覆盖之前留一份上一版），再用原子替换换上去
                    BackupExisting(path);
                    try { File.Replace(tmp, path, null); }
                    catch
                    {
                        // 某些文件系统/权限下 Replace 会失败，退回到两步式
                        File.Delete(path);
                        File.Move(tmp, path);
                    }
                }
                else
                {
                    File.Move(tmp, path);
                }
            }
            catch { }
        }

        /// <summary>
        /// 备份当前 state.json 到 .bak。
        ///
        /// <para>【2026-10-22】用户实测丢过一次数据（166 条任务 + 189 条历史）。
        /// 根因是"读失败 → 用空表覆盖"。光靠代码不再写空表还不够 ——
        /// 万一 state.json 真的被外部破坏（杀软隔离、磁盘错误、误删），
        /// 至少还有一份**上一版**可以捞回来。</para>
        ///
        /// <para>备份用"先写 .bak.tmp 再替换"的方式，避免备份本身也是半截的。</para>
        /// </summary>
        private static void BackupExisting(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                string bak = path + ".bak";
                string bakTmp = bak + ".tmp";
                File.Copy(path, bakTmp, true);
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(bakTmp, bak);
            }
            catch { }
        }

        // ---------------- 历史记录 ----------------

        public static List<HistoryRecord> GetHistory()
        {
            var s = Load() ?? EmptyState();
            var list = new List<HistoryRecord>(s.Records);
            list.Reverse();   // 新的在前
            return list;
        }

        /// <summary>追加一条历史。超出上限时丢最旧的。</summary>
        public static void AddHistory(string name, string size, string path, string status)
        {
            // ⚠️ 读不到就**不写** —— 拿空表写回去会覆盖掉磁盘上已有的历史。
            var s = Load();
            if (s == null) return;
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
            if (s == null) return;
            s.Records.Clear();
            Save(s);
        }

        // ---------------- 解析历史（按 h 唤出的面板用） ----------------

        /// <summary>解析历史：**置顶的在最前**，其余按时间倒序（新的在前）。</summary>
        public static List<ShareHistoryEntry> GetShareHistory()
        {
            var s = Load() ?? EmptyState();
            var list = new List<ShareHistoryEntry>(s.Shares);
            list.Reverse();   // 存储是"旧的在前"，给 UI 时翻成"新的在前"
            if (list.TrueForAll(x => !x.Pinned))
                return list;

            // 置顶的提到最前；两段内部都保持"新的在前"
            var result = list.FindAll(x => x.Pinned);
            result.AddRange(list.FindAll(x => !x.Pinned));
            return result;
        }

        /// <summary>
        /// 记一条解析历史。
        ///
        /// 【同一条分享只留一条】pwd_id 相同就把旧的删掉再插到**末尾**（= 最新）——
        /// 再解析一次同一条链接，只是让它回到列表最前面并更新名字/提取码/时间，
        /// 而不是堆出好几条一模一样的记录。
        /// ⚠️ 旧那条若被置顶过，要**把置顶状态带过来** —— 置顶是用户的显式选择，
        ///    不能因为他又点了一次这条链接就悄悄取消。
        /// </summary>
        public static void AddShareHistory(string pwdId, string passcode, string startFid,
            string name, bool isFolder)
        {
            if (string.IsNullOrEmpty(pwdId)) return;
            try
            {
                var s = Load();
                if (s == null) return;      // 读不到就不写，别用空表覆盖
                bool wasPinned = false;
                for (int i = s.Shares.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(s.Shares[i].PwdId, pwdId, StringComparison.Ordinal))
                    {
                        wasPinned = wasPinned || s.Shares[i].Pinned;
                        s.Shares.RemoveAt(i);
                    }
                }
                s.Shares.Add(new ShareHistoryEntry
                {
                    PwdId = pwdId,
                    Passcode = passcode ?? "",
                    StartFid = string.IsNullOrEmpty(startFid) ? "0" : startFid,
                    Name = name ?? "",
                    IsFolder = isFolder,
                    Pinned = wasPinned,
                    Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                });
                if (s.Shares.Count > MaxShareHistory)
                    s.Shares.RemoveRange(0, s.Shares.Count - MaxShareHistory);
                Save(s);
            }
            catch { }
        }

        /// <summary>置顶 / 取消置顶某条解析历史。</summary>
        public static void SetSharePinned(string pwdId, bool pinned)
        {
            if (string.IsNullOrEmpty(pwdId)) return;
            try
            {
                var s = Load();
                if (s == null) return;
                foreach (var e in s.Shares)
                {
                    if (string.Equals(e.PwdId, pwdId, StringComparison.Ordinal))
                        e.Pinned = pinned;
                }
                Save(s);
            }
            catch { }
        }

        /// <summary>删掉某条解析历史（按 pwd_id）。</summary>
        public static void RemoveShareHistory(string pwdId)
        {
            if (string.IsNullOrEmpty(pwdId)) return;
            try
            {
                var s = Load();
                if (s == null) return;
                for (int i = s.Shares.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(s.Shares[i].PwdId, pwdId, StringComparison.Ordinal))
                        s.Shares.RemoveAt(i);
                }
                Save(s);
            }
            catch { }
        }

        public static void ClearShareHistory()
        {
            var s = Load();
            if (s == null) return;
            s.Shares.Clear();
            Save(s);
        }

        // ---------------- 未完成任务 ----------------

        public static List<PendingTask> GetPending()
        {
            var sp = Load();
            return sp == null ? new List<PendingTask>() : sp.Pending;
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

        // ---------------- 任务列表快照 ----------------

        /// <summary>取上次退出时的任务列表（按时间正序存，调用方自己决定显示顺序）。</summary>
        public static List<TaskSnapshot> GetTasks()
        {
            var st = Load();
            return st == null ? new List<TaskSnapshot>() : st.Tasks;
        }

        /// <summary>整体覆盖任务列表快照。由任务页在集合/状态变化时调用（代码里做了节流）。</summary>
        public static void SetTasks(List<TaskSnapshot> tasks)
        {
            var s = Load();
            s.Tasks = tasks ?? new List<TaskSnapshot>();
            Save(s);
        }
    }

    /// <summary>
    /// 一条任务列表快照（2026-10-13 新增）。
    ///
    /// 【要解决的问题】原来任务页只活在内存里：重开程序，**已完成的任务**和
    /// **正在下载的任务**都从列表里消失（前者只留在「下载历史」窗口，
    /// 后者要等「未完成的任务」弹窗再点续传才会重新出现）。
    /// 用户要求「应该保留记录和进度」→ 把每行的状态 / 已下字节也存下来，
    /// 启动时原样铺回任务页。
    ///
    /// 【字段取舍】只是"恢复显示 + 支持续传"，所以存的是 DownloadJob 的关键上下文
    /// （Dest / fid / token / 分享三件套 / 写入模式）与进度（Size / Done），
    /// 不存调度器内部对象。
    /// </summary>
    [DataContract]
    public class TaskSnapshot
    {
        [DataMember(Order = 1)] public string Name { get; set; }
        [DataMember(Order = 2)] public string Dest { get; set; }
        [DataMember(Order = 3)] public long Size { get; set; }
        [DataMember(Order = 4)] public long Done { get; set; }
        /// <summary>JobState 的名字（Completed / Failed / Canceled / Paused ...）。</summary>
        [DataMember(Order = 5)] public string State { get; set; }
        [DataMember(Order = 6)] public string Error { get; set; }
        [DataMember(Order = 7)] public string Url { get; set; }

        // ---- 续传 / 重取链所需（与 PendingTask 同口径）----
        [DataMember(Order = 8)] public string Fid { get; set; }
        [DataMember(Order = 9)] public string Token { get; set; }
        [DataMember(Order = 10)] public bool FromShare { get; set; }
        [DataMember(Order = 11)] public string PwdId { get; set; }
        [DataMember(Order = 12)] public string Stoken { get; set; }
        [DataMember(Order = 13)] public string Passcode { get; set; }
        [DataMember(Order = 14)] public string RelDir { get; set; }
        [DataMember(Order = 15)] public bool Segmented { get; set; }
        /// <summary>记录时刻（仅用于排查 / 将来做"保留最近 N 天"）。</summary>
        [DataMember(Order = 16)] public string Time { get; set; }

        /// <summary>任务分组名（下载时所在目录名，2026-10-20 新增）。</summary>
        [DataMember(Order = 17)] public string GroupName { get; set; }

        /// <summary>进入分享时的起点目录 fid（stoken 过期后重新下钻用，2026-10-20 新增）。</summary>
        [DataMember(Order = 18)] public string StartFid { get; set; }

        /// <summary>文件相对分享根目录的路径（stoken 过期后重新下钻用，2026-10-20 新增）。</summary>
        [DataMember(Order = 19)] public string ShareRelDir { get; set; }

        /// <summary>
        /// 分享内**源文件**的 fid（不是转存副本的 fid）。2026-10-22 新增。
        /// <para>转存路径下中转副本被删后，靠它（或 <see cref="SourceName"/>）回退免转存。</para>
        /// </summary>
        [DataMember(Order = 20)] public string SourceFid { get; set; }

        /// <summary>分享内源文件的文件名（源 fid 也失效时按名字定位）。2026-10-22 新增。</summary>
        [DataMember(Order = 21)] public string SourceName { get; set; }

        /// <summary>能不能"点一下就续传"——规则与 <see cref="PendingTask.CanResume"/> 一致。</summary>
        public bool CanResume
        {
            get
            {
                if (string.IsNullOrEmpty(Dest) || string.IsNullOrEmpty(Fid)) return false;
                if (FromShare) return !string.IsNullOrEmpty(Token) && !string.IsNullOrEmpty(PwdId)
                                  && !string.IsNullOrEmpty(Stoken);
                return true;
            }
        }

        /// <summary>转成续传流程认识的 <see cref="PendingTask"/>。</summary>
        public PendingTask ToPending()
        {
            return new PendingTask
            {
                Name = Name,
                Dest = Dest,
                Url = Url,
                Fid = Fid,
                Token = Token,
                FromShare = FromShare,
                Size = Size,
                StreamPlay = Segmented,
                StreamPlayMode = Segmented,
                Status = State,
                PwdId = PwdId,
                Stoken = Stoken,
                Passcode = Passcode,
                RelDir = RelDir,
                GroupName = GroupName,
                StartFid = StartFid,
                ShareRelDir = ShareRelDir,
                SourceFid = SourceFid,
                SourceName = SourceName,
            };
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
        /// 任务分组名（下载时所在目录名）；空 = 按 RelDir 推断。
        /// 只影响任务页分组显示，不参与路径重建。
        /// </summary>
        [DataMember(Order = 15)] public string GroupName { get; set; }

        /// <summary>进入分享时的起点目录 fid（stoken 过期后重新下钻用，2026-10-20 新增）。</summary>
        [DataMember(Order = 16)] public string StartFid { get; set; }

        /// <summary>文件相对分享根目录的路径（stoken 过期后重新下钻用，2026-10-20 新增）。</summary>
        [DataMember(Order = 17)] public string ShareRelDir { get; set; }

        /// <summary>
        /// 分享内**源文件**的 fid（不是转存副本的 fid）。2026-10-22 新增。
        /// <para>转存路径下中转副本被删后，靠它（或 <see cref="SourceName"/>）回退免转存。</para>
        /// </summary>
        [DataMember(Order = 18)] public string SourceFid { get; set; }

        /// <summary>分享内源文件的文件名（源 fid 也失效时按名字定位）。2026-10-22 新增。</summary>
        [DataMember(Order = 19)] public string SourceName { get; set; }

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
