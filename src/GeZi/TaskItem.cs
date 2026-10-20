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
using System.ComponentModel;
using GeZi.Core.Download;

namespace GeZi
{
    /// <summary>任务列表条目（带变更通知，供进度条/状态实时刷新）。</summary>
    public class TaskItem : INotifyPropertyChanged
    {
        public DownloadJob Job { get; }
        public string Name { get; }

        /// <summary>是否已写入下载历史。终态只记一次，防止重复上报把历史刷屏。</summary>
        public bool HistoryRecorded { get; set; }

        /// <summary>
        /// 这一行是**从上次运行的任务快照恢复**出来的、没有挂到调度器上（2026-10-13 新增）。
        ///
        /// 已完成的任务：只作为记录/播放/定位/删除的入口。
        /// 未完成的任务：显示为「已暂停」并保留上次进度，点 ▶ 才走"重新取链 → 续传"，
        /// 见 <c>MainWindow.ResumeTask</c> 里的 Dormant 分支。
        /// </summary>
        public bool Dormant { get; set; }

        private static long _nextSeq;

        // ==================== 连接车道（Ketch 式） ====================
        //
        // 【为什么要有】任务行原本只有一根进度条：速度掉了只能看到"慢"，
        // 看不出"是不是根本没把连接发满"。服务端限速 vs 客户端没发满连接，
        // 两者的处置完全不同，必须能一眼区分。
        // 这里用一排小格子显示"活跃连接 / 工作线程"的填充比例：
        //   · 格子几乎全亮 + 速度低  → 服务端限速（客户端无解）
        //   · 格子只亮一小截 + 速度低 → 客户端没把连接发出去（可调高线程数）

        /// <summary>车道格子总数（固定，按比例填充，避免宽度随连接数跳动）。</summary>
        public const int LaneSlots = 20;

        private int _activeConns;
        /// <summary>当前活跃连接数（由 MainWindow.OnDiagnostics 从内核快照喂入）。</summary>
        public int ActiveConnections
        {
            get => _activeConns;
            set
            {
                if (value == _activeConns) return;
                _activeConns = value;
                OnChanged(nameof(Lanes));
                OnChanged(nameof(LaneText));
            }
        }

        private int _workerCount;
        /// <summary>本任务分配到的 worker 数（并发上限）。</summary>
        public int WorkerCount
        {
            get => _workerCount;
            set
            {
                if (value == _workerCount) return;
                _workerCount = value;
                OnChanged(nameof(Lanes));
                OnChanged(nameof(LaneText));
            }
        }

        /// <summary>
        /// 车道格子的填充状态（true = 亮）。按 活跃/上限 的比例填充前 N 格。
        /// </summary>
        public System.Collections.Generic.IEnumerable<bool> Lanes
        {
            get
            {
                int wc = _workerCount;
                int ac = _activeConns;
                if (ac < 0) ac = 0;
                if (wc > 0 && ac > wc) ac = wc;      // 活跃数偶尔会短暂超过上限（收尾竞态），夹一下

                int filled = wc > 0 ? (int)System.Math.Round((double)ac / wc * LaneSlots) : 0;
                var arr = new bool[LaneSlots];
                for (int i = 0; i < filled && i < LaneSlots; i++) arr[i] = true;
                return arr;
            }
        }

        /// <summary>车道旁边的文字，如「12/32 连接」。没有数据时返回空串（不占位）。</summary>
        public string LaneText
        {
            get
            {
                if (_workerCount > 0) return _activeConns + "/" + _workerCount + " 连接";
                return _activeConns > 0 ? _activeConns + " 连接" : "";
            }
        }

        /// <summary>
        /// 构造次序（进程内自增，仅用于任务列表排序）。
        ///
        /// 【2026-10-13】用户要求任务列表可切换"新任务在前 / 在后"。列表里同一批
        /// 任务（比如一次加入 20 个文件）几乎同时创建，用 <see cref="DateTime.Now"/>
        /// 排序会因精度不够而次序不稳，所以用一个单调递增的序号当"时间戳"。
        /// 越新加入的任务 Seq 越大 ⇒ 倒序排列即"新任务在前"。
        /// </summary>
        public long Seq { get; }

        /// <summary>
        /// 「单独选择的文件」这一组的名字。
        /// 没经过文件夹的任务（直接勾选文件下载）归到这一组，避免它们被硬塞进
        /// 某个真实文件夹名下。
        /// </summary>
        public const string SingleFileGroupName = "单独选择的文件";

        /// <summary>
        /// 任务所属的**顶层文件夹名**（任务页「文件夹分组」用，2026-10-13 新增）。
        ///
        /// 来源是 <see cref="GeZi.Core.Download.PendingInfo.RelDir"/>：下载文件夹时
        /// CollectFilesAsync 会把"所选文件夹名"作为最外层前缀写进每个文件的 RelDir，
        /// 所以取第一段就是用户选中的那个文件夹。多级子目录（A\B）也只取顶层 A ——
        /// 按子目录再分一层会让分组爆炸，而且用户要的是"这个文件夹下载了多少"。
        ///
        /// 空 RelDir（单独勾选的文件）→ <see cref="SingleFileGroupName"/>。
        /// </summary>
        public string GroupName
        {
            get
            {
                string rel = (Job != null && Job.Pending != null) ? (Job.Pending.RelDir ?? "") : "";
                if (rel.Length == 0)
                {
                    // 直接在当前打开的目录里勾的文件：RelDir 是空串，
                    // 用下载时所在目录名分类（分享子目录 / 网盘子目录）。见 PendingInfo.GroupName。
                    string folder = (Job != null && Job.Pending != null) ? (Job.Pending.GroupName ?? "") : "";
                    return folder.Length == 0 ? SingleFileGroupName : folder;
                }
                int i = rel.IndexOfAny(new[] { '\\', '/' });
                return i <= 0 ? rel : rel.Substring(0, i);
            }
        }

        public TaskItem(DownloadJob job, string name)
        {
            Job = job;
            Name = name;
            Seq = ++_nextSeq;
        }

        private JobState _state = JobState.Queued;
        public JobState State
        {
            get => _state;
            set { _state = value; OnChanged(nameof(State)); OnChanged(nameof(StatusText)); OnChanged(nameof(DetailText)); OnChanged(nameof(SpeedText)); OnChanged(nameof(CanPlay)); }
        }

        /// <summary>
        /// 这个任务是**视频**、且可以「播放」—— 决定任务行显示「播放」按钮还是「打开」按钮。
        /// </summary>
        /// <remarks>
        /// 【2026-10-20】按文件格式分流：
        /// · mkv / mp4 / avi … → 显示「播放」，点击用 VLC 打开（未下完走本地串流边下边播）
        /// · 其它（zip / pdf / jpg / mp3 …）→ 显示「打开」，点击用系统默认程序打开
        ///
        /// 为什么区分：非视频文件交给 VLC 毫无意义（VLC 只会弹"无法播放"），
        /// 而视频文件用系统默认程序打开又可能关联到不支持的播放器。
        /// </remarks>
        public bool CanPlay
        {
            get
            {
                if (Job == null) return false;
                return MediaProbe.IsVideo(Job.Dest);
            }
        }

        /// <summary>
        /// 这个任务**不是视频**、可以「打开」（用系统默认应用）—— 与 <see cref="CanPlay"/> 互斥。
        /// </summary>
        public bool CanOpen
        {
            get
            {
                if (Job == null) return false;
                return !MediaProbe.IsVideo(Job.Dest);
            }
        }

        private string _error = "";
        public string Error
        {
            get => _error;
            set { _error = value ?? ""; OnChanged(nameof(Error)); OnChanged(nameof(DetailText)); }
        }

        private double _percent;
        public double Percent
        {
            get => _percent;
            set { _percent = value; OnChanged(nameof(Percent)); OnChanged(nameof(PercentText)); }
        }

        /// <summary>
        /// 当前阶段说明（来自核心层的 <see cref="GeZi.Core.Download.DownloadProgress.Phase"/>）。
        /// 空串 = 常规下载阶段。非空时 <see cref="StatusText"/> 直接显示它
        /// （例如「正在合并分片」），避免收尾期界面看起来像卡死。
        /// </summary>
        private string _phase = "";
        public string Phase
        {
            get => _phase;
            set
            {
                var v = value ?? "";
                if (v == _phase) return;          // 每 500ms 上报一次，值不变就别刷 UI
                _phase = v;
                OnChanged(nameof(Phase));
                OnChanged(nameof(StatusText));
            }
        }

        private double _speed;
        public double Speed
        {
            get => _speed;
            // SpeedText 要看它决定显示速度还是留空；StatusText 也要看它决定
            // 显示「暂停中…」还是「已暂停」（见下）。EtaText 也依赖它。
            set
            {
                _speed = value;
                OnChanged(nameof(SpeedText));
                OnChanged(nameof(StatusText));
                OnChanged(nameof(EtaText));
            }
        }

        /// <summary>
        /// 用户**已经按过暂停**，但下载器还在收尾（worker 要跑完手上那一次读）。
        /// </summary>
        /// <remarks>
        /// 【为什么需要这个标志】暂停是**闸门式**的：`PauseToken.WaitIfPausedAsync` 让 worker
        /// 在每次读完之后才阻塞，所以在途的请求要跑完、速度是**渐降**到 0 的（不是瞬间归零）。
        /// 而核心层的进度上报 `JobProgressAdapter` **永远报 `Downloading`**
        /// （见 DownloadScheduler），于是 UI 刚设好的 `Paused` 会被下一帧进度覆盖掉 ——
        /// 表现就是用户看到的「图标先变成三角、随后又变回两个竖杠」。
        ///
        /// 所以：置位后，<c>OnJobUpdate</c> 会**忽略核心报来的 Downloading**，
        /// 状态文案在速度归零前显示「暂停中…」，归零后才显示「已暂停」。
        /// </remarks>
        private bool _pauseRequested;
        public bool PauseRequested
        {
            get => _pauseRequested;
            set
            {
                _pauseRequested = value;
                OnChanged(nameof(PauseRequested));
                OnChanged(nameof(StatusText));
                OnChanged(nameof(DetailText));
            }
        }

        private long _done;
        public long Done
        {
            get => _done;
            set
            {
                // 只有【值真的变化】才刷新时间戳。
                // 进度回调每 500ms 就来一次，若无条件刷新，LastProgressUtc 永远是"刚刚"，
                // 保活那边的"无进展超时"就永远判不出来（卡死的任务会被当成还在跑）。
                if (value != _done)
                {
                    _done = value;
                    LastProgressUtc = DateTime.UtcNow;
                }
                OnChanged(nameof(DoneText));
            }
        }

        /// <summary>
        /// 最近一次**进度真的前进**的时刻（UTC）。
        ///
        /// 供 <see cref="SleepGuard"/> 的「无进展超时」判定使用：
        /// 任务卡死时（worker 卡在掐不断的调用里、`Task.WhenAll` 永不返回）
        /// 状态会**永远停在 Downloading**，仅看状态无法区分"在下载"和"卡死了"，
        /// 必须靠"多久没进展"来兜底，否则电脑会整夜不睡。
        /// </summary>
        public DateTime LastProgressUtc { get; private set; } = DateTime.UtcNow;

        private long _total;
        public long Total
        {
            get => _total;
            set { _total = value; OnChanged(nameof(DoneText)); }
        }

        public string StatusText
        {
            get
            {
                switch (State)
                {
                    case JobState.Queued: return "排队中";
                    case JobState.Downloading:
                        // 收尾（合并分片）阶段：核心层会上报阶段说明。
                        // 这段**没有任何网络流量**、速度自然为 0，若仍显示「下载中」+ 0 速度，
                        // 用户会以为程序卡死（实测反馈「就显示 0 kb 不动」）。
                        return string.IsNullOrEmpty(_phase) ? "下载中" : _phase;
                    case JobState.Paused:
                        // 暂停是渐进的（在途请求要跑完）→ 速度还没归零时显示「暂停中…」，
                        // 真正停下来（速度 0）才显示「已暂停」。
                        return _pauseRequested && _speed > 0.5 ? "暂停中…" : "已暂停";
                    case JobState.Completed: return "完成";
                    case JobState.Failed: return "失败";
                    case JobState.Cancelled: return "已取消";
                    case JobState.Cancelling: return "取消中…";
                    default: return "";
                }
            }
        }

        /// <summary>状态 + 失败原因，用于任务列表显示，确保错误一眼可见。</summary>
        public string DetailText
        {
            get
            {
                if (State == JobState.Failed && !string.IsNullOrEmpty(Error))
                    return "失败: " + Error;
                return StatusText;
            }
        }

        /// <summary>
        /// 百分比文本。
        ///
        /// 🚨 【2026-10-06 修】**必须截断，不能四舍五入** ——
        /// 原来直接 `ToString("F1")`，于是 99.95% 会被舍入成「**100.0%**」：
        /// 用户看到 100% 却还在跑、文件也没下完（实测反馈：「明明9gb只下了8.99gb却显示100%」）。
        /// 改成先向下取整到 0.1 再格式化 ⇒ 只有**真正** 100% 才会显示 100.0%。
        /// </summary>
        public string PercentText
        {
            get
            {
                if (_percent >= 100.0)
                    return "100.0%";
                double truncated = Math.Floor(_percent * 10.0) / 10.0;
                if (truncated < 0) truncated = 0;
                return truncated.ToString("F1") + "%";
            }
        }

        /// <summary>
        /// 速度文本。只有在"下载中"才展示速度，其余状态返回空串——
        /// 否则任务完成/暂停后最后一帧的残留速度会一直挂在界面上，看起来像还在跑。
        /// </summary>
        public string SpeedText
        {
            get
            {
                // 暂停中（在途请求还在跑）也把速度显示出来 —— 这样用户能看到
                // 它是"正在停"而不是"卡住了"，速度归零后自然变成「已暂停」。
                if (State != JobState.Downloading &&
                    !(State == JobState.Paused && _pauseRequested && _speed > 0.5))
                    return "";
                if (_speed <= 0)
                    return "…";      // 刚开始还没测出速度
                return Util.FormatSize((long)_speed) + "/s";
            }
        }

        public string DoneText => _total > 0
            ? Util.FormatSize(_done) + " / " + Util.FormatSize(_total)
            : Util.FormatSize(_done);

        /// <summary>
        /// 预计剩余时间文本（2026-10-20 新增，方案 A 行内内联）。
        ///
        /// <para>【为什么需要】用户下载大文件时最想知道的是"还要多久"，而不是"下了多少"。
        /// 百分比只说明进度，剩余时间才决定"要不要等"。</para>
        ///
        /// <para>【算法】剩余字节 ÷ 当前速度。速度来自核心层的 EWMA 平滑值，
        /// 已经滤掉了瞬时抖动，所以算出来不会乱跳。速度 ≤ 0（刚启动 / 暂停）时返回空串，
        /// 不显示"—"这种噪音。超过 24 小时显示「> 1 天」，避免出现"37 小时"这种怪数字。</para>
        /// </summary>
        public string EtaText
        {
            get
            {
                // 【2026-10-20 用户反馈】只在**下载中**才显示剩余时间。
                // 已完成/暂停/排队时整列在 XAML 里 Collapsed —— 否则会白占宽度，
                // 把进度条越挤越短（用户实测：「进度条越来越短」）。
                if (State != JobState.Downloading) return "";

                if (_total <= 0 || _done >= _total) return "";
                if (_speed <= 1024) return "";      // 速度太低（<1KB/s）时估算没有意义

                double remainBytes = _total - _done;
                double seconds = remainBytes / _speed;
                if (seconds < 0) return "";

                return "剩 " + FormatEta(seconds);
            }
        }

        /// <summary>把秒数格式化成人类可读的「15 分」/「1 小时 38 分」/「> 1 天」。</summary>
        internal static string FormatEta(double seconds)
        {
            if (seconds < 60) return "不到 1 分";

            double minutes = seconds / 60.0;
            if (minutes < 60)
                return (int)Math.Round(minutes) + " 分";

            double hours = minutes / 60.0;
            if (hours < 24)
            {
                int h = (int)hours;
                int m = (int)Math.Round(minutes - h * 60);
                // 四舍五入到 60 分时进位，避免出现「1 小时 60 分」
                if (m >= 60) { h++; m = 0; }
                return m == 0 ? h + " 小时" : h + " 小时 " + m + " 分";
            }

            double days = hours / 24.0;
            if (days < 99)
                return (int)Math.Round(days) + " 天";
            return "> 99 天";
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
