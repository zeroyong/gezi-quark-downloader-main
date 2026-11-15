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
using System.Windows;
using System.Windows.Media;
using GeZi.Core.Upload;

namespace GeZi
{
    /// <summary>
    /// 上传进度窗口（2026-11-10 新增）。
    ///
    /// <para>【职责极窄】只负责把 <see cref="UploadProgress"/> 画出来，
    /// 以及让用户点「取消上传」。上传本身由主窗口驱动的
    /// <see cref="UploadQueue"/> 跑，这个窗口不持有任何业务状态。</para>
    ///
    /// <para>【为什么没有关闭按钮】上传真在后台跑，随手关掉会让用户以为停了。
    /// 唯一出口是「取消上传」（真的会中止）或等它自己完成。</para>
    /// </summary>
    public partial class UploadProgressWindow : Window
    {
        private readonly Action _onCancel;

        /// <summary>
        /// 当前界面状态。
        ///
        /// <para>【为什么必须是三态而不是一个 bool】原来只有一个 _cancelClicked，
        /// 而 MarkDone 会把它重置回 false —— 于是主窗口随后调 Close() 时，
        /// OnClosing 看到"没点过取消"就把它当成用户想中途关闭，
        /// 又转成一次取消动作并 e.Cancel = true，窗口**永远关不掉**
        /// （实测卡在「正在取消…」）。状态必须能区分
        /// 「还在传」/「用户点了取消」/「已经结束可以关」。</para>
        /// </summary>
        private enum Phase { Busy, Cancelling, Finished }
        private Phase _phase = Phase.Busy;

        /// <summary>
        /// 建一个进度窗口。
        /// </summary>
        /// <param name="owner">父窗口（用于居中）。</param>
        /// <param name="onCancel">用户点「取消上传」时的回调（由主窗口接到队列的 Cancel）。</param>
        public UploadProgressWindow(Window owner, Action onCancel)
        {
            InitializeComponent();
            if (owner != null && !ReferenceEquals(owner, this))
                Owner = owner;
            _onCancel = onCancel;
        }

        // ---------------- 速度与剩余时间（2026-11-10 新增）----------------

        /// <summary>最近一次记录的字节数（用来算增量）。</summary>
        private long _lastSampleBytes = -1;

        /// <summary>最近一次采样的时刻。</summary>
        private DateTime _lastSampleAt = DateTime.MinValue;

        /// <summary>滑动平均后的速度（字节/秒）。</summary>
        private double _smoothedBps;

        /// <summary>上次刷新速度文本的时刻（速度不必每帧都变，抖得厉害反而难读）。</summary>
        private DateTime _lastSpeedPaint = DateTime.MinValue;

        /// <summary>当前阶段的起点（阶段切换时重置统计，避免"校验"那段的低速拖累"上传"的速度）。</summary>
        private UploadPhase _statPhase = (UploadPhase)(-1);

        /// <summary>本阶段开始时的字节数与时刻（用于平均速度兜底）。</summary>
        private long _phaseStartBytes;
        private DateTime _phaseStartAt;

        /// <summary>
        /// 按本次进度更新速度估计。
        ///
        /// <para>【为什么用滑动平均而不是两点相除】进度回调是节流过的（约 120ms 一次），
        /// 两点相除会被单次抖动放大成几十 MB/s 或 0 B/s。
        /// 这里用指数滑动平均（新样本权重 0.35），既跟得上又不会跳。</para>
        /// </summary>
        private void UpdateSpeed(UploadProgress p)
        {
            var now = DateTime.UtcNow;

            // 阶段变了：重新计基准（校验阶段的读取速度与上传速度不是一回事）
            if (p.Phase != _statPhase)
            {
                _statPhase = p.Phase;
                _phaseStartBytes = p.Sent;
                _phaseStartAt = now;
                _lastSampleBytes = p.Sent;
                _lastSampleAt = now;
                _smoothedBps = 0;
                return;
            }

            if (_lastSampleBytes < 0)
            {
                _lastSampleBytes = p.Sent;
                _lastSampleAt = now;
                return;
            }

            double elapsedMs = (now - _lastSampleAt).TotalMilliseconds;
            long delta = p.Sent - _lastSampleBytes;

            // 进度回退（切文件/重传）时直接重置，别让它算出负速度
            if (delta < 0)
            {
                _lastSampleBytes = p.Sent;
                _lastSampleAt = now;
                _smoothedBps = 0;
                return;
            }

            if (elapsedMs >= 80 && delta >= 0)
            {
                double instant = delta / (elapsedMs / 1000.0);
                _smoothedBps = _smoothedBps <= 0
                    ? instant
                    : _smoothedBps * 0.65 + instant * 0.35;
                _lastSampleBytes = p.Sent;
                _lastSampleAt = now;
            }
        }

        /// <summary>当前速度（字节/秒）；样本不足时退回阶段平均。</summary>
        private double CurrentBps(UploadProgress p)
        {
            if (_smoothedBps > 1) return _smoothedBps;
            // 兜底：用本阶段平均速度（样本太少时比显示 0 强）
            double secs = (DateTime.UtcNow - _phaseStartAt).TotalSeconds;
            long moved = p.Sent - _phaseStartBytes;
            if (secs > 0.5 && moved > 0) return moved / secs;
            return 0;
        }

        /// <summary>更新进度显示（必须从 UI 线程调用）。</summary>
        public void Apply(UploadProgress p)
        {
            if (p == null) return;
            try
            {
                if (p.Count > 1)
                    TitleText.Text = CountText.Text = "正在上传（" + p.Index + "/" + p.Count + "）";
                else
                    TitleText.Text = CountText.Text = "正在上传";

                CountText.Text = "第 " + p.Index + " / " + p.Count + " 个";

                NameText.Text = p.FileName ?? "";

                // 进度条宽度按比例算（父 Border 的实际宽度 * 比例）
                double ratio = p.Fraction;
                double full = 0;
                var parent = BarFill.Parent as FrameworkElement;
                if (parent != null) full = parent.ActualWidth;
                if (full <= 0) full = 440;   // 首次布局前的兜底
                BarFill.Width = Math.Max(0, full * ratio);

                // 百分比
                PercentText.Text = (ratio * 100).ToString("0.0") + "%";

                // 速度 + 剩余时间
                UpdateSpeed(p);
                var now = DateTime.UtcNow;
                if ((now - _lastSpeedPaint).TotalMilliseconds >= 200 || p.Fraction >= 1)
                {
                    _lastSpeedPaint = now;
                    double bps = CurrentBps(p);
                    bool uploading = p.Phase == UploadPhase.Uploading;

                    if (!uploading)
                    {
                        // 校验 / 收尾阶段不显示速度 —— 那两个阶段的速率对用户没有意义，
                        // 显示出来只会让人以为"怎么这么慢"
                        SpeedText.Text = "";
                        EtaText.Text = "";
                    }
                    else if (bps > 1)
                    {
                        SpeedText.Text = FormatSpeed(bps);
                        long remain = p.Size - p.Sent;
                        if (remain > 0 && bps > 1)
                        {
                            double etaSec = remain / bps;
                            EtaText.Text = "剩 " + FormatEta(etaSec);
                        }
                        else EtaText.Text = "";
                    }
                    else
                    {
                        SpeedText.Text = "计算速度中…";
                        EtaText.Text = "";
                    }
                }

                PhaseText.Text = _phase == Phase.Cancelling ? "正在取消…" : p.PhaseText;
                SizeText.Text = FormatSize(p.Sent) + " / " + FormatSize(p.Size);
                HintText.Text = _phase == Phase.Cancelling
                    ? "正在中止当前分片，请稍候"
                    : "上传期间可以继续用其它页面";
            }
            catch
            {
                // 进度刷新绝不能因为界面异常把上传搞挂
            }
        }

        /// <summary>
        /// 标记上传已结束（成功 / 失败 / 用户取消都走这里）。
        /// 调用之后窗口可以被正常关闭。
        /// </summary>
        public void MarkDone(string message)
        {
            try
            {
                _phase = Phase.Finished;
                PhaseText.Text = message ?? "已完成";
                CancelBtn.Content = "关闭";
                CancelBtn.IsEnabled = true;
            }
            catch { }
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            if (_phase == Phase.Finished)
            {
                Close();
                return;
            }
            if (_phase == Phase.Busy)
            {
                _phase = Phase.Cancelling;
                CancelBtn.IsEnabled = false;
                PhaseText.Text = "正在取消…";
                HintText.Text = "正在中止当前分片，请稍候";
                var h = _onCancel;
                if (h != null)
                {
                    try { h(); } catch { }
                }
            }
            // Cancelling：已经在取消了，忽略重复点击（按钮也已禁用）
        }

        /// <summary>
        /// 拦掉所有关闭途径（Alt+F4 / 系统菜单）—— 只能通过「取消上传」退出，
        /// 避免用户以为关掉窗口上传就停了。
        /// </summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // 只有「已经结束」才允许关；还在传/正在取消都拦下来，
            // 把关闭动作转成一次「取消上传」（用户以为关窗口就停了，其实不会）。
            if (_phase != Phase.Finished)
            {
                e.Cancel = true;
                OnCancelClick(this, null);
                return;
            }
            base.OnClosing(e);
        }

        /// <summary>速度格式化：自动选 B/s、KB/s、MB/s。</summary>
        private static string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec < 1024) return bytesPerSec.ToString("0") + " B/s";
            if (bytesPerSec < 1024.0 * 1024) return (bytesPerSec / 1024.0).ToString("0.0") + " KB/s";
            return (bytesPerSec / 1048576.0).ToString("0.00") + " MB/s";
        }

        /// <summary>剩余时间格式化：不显示小数秒的抖动，按量级给合理精度。</summary>
        private static string FormatEta(double seconds)
        {
            if (seconds < 1) return "不到 1 秒";
            if (seconds < 60) return ((int)Math.Round(seconds)) + " 秒";
            if (seconds < 3600)
            {
                int m = (int)(seconds / 60);
                int s2 = (int)Math.Round(seconds % 60);
                return m + " 分 " + s2 + " 秒";
            }
            int h = (int)(seconds / 3600);
            int m2 = (int)Math.Round((seconds % 3600) / 60);
            return h + " 小时 " + m2 + " 分";
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576.0).ToString("0.0") + " MB";
            return (bytes / 1073741824.0).ToString("0.00") + " GB";
        }
    }
}
