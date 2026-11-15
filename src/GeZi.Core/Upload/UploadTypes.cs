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

namespace GeZi.Core.Upload
{
    /// <summary>
    /// 上传阶段（2026-11-10 新增）。
    ///
    /// <para>【为什么要分阶段给用户看】大文件算 MD5/SHA1 要读一遍整个文件，
    /// 几十秒没有任何反馈，用户会以为卡死。三个阶段文案来自对标实现（YunX）：
    /// 「正在校验文件…」→「正在上传」→「等待网盘确认…」。</para>
    /// </summary>
    public enum UploadPhase
    {
        /// <summary>预处理：算整文件哈希、建上传任务。</summary>
        Preparing,

        /// <summary>正在传字节。</summary>
        Uploading,

        /// <summary>合并分片 + 等网盘落库。</summary>
        Finishing,
    }

    /// <summary>
    /// 一次上传的进度快照（2026-11-10 新增）。
    ///
    /// <para>会通过 <see cref="IProgress{T}"/> 报到 UI 线程。字段刻意保持扁平，
    /// 让界面不用再算就能直接画。</para>
    /// </summary>
    public sealed class UploadProgress
    {
        /// <summary>本批第几个文件（从 1 开始）。</summary>
        public int Index { get; set; }

        /// <summary>本批一共几个文件。</summary>
        public int Count { get; set; }

        /// <summary>当前文件名。</summary>
        public string FileName { get; set; }

        /// <summary>已处理字节。</summary>
        public long Sent { get; set; }

        /// <summary>当前文件总字节。</summary>
        public long Size { get; set; }

        /// <summary>当前阶段。</summary>
        public UploadPhase Phase { get; set; }

        /// <summary>用户已点取消、正在收尾。</summary>
        public bool Cancelling { get; set; }

        /// <summary>完成比例（0–1）。</summary>
        public double Fraction
        {
            get { return Size <= 0 ? 0 : Math.Min(1.0, Math.Max(0.0, (double)Sent / Size)); }
        }

        /// <summary>阶段的中文说明（界面直接显示）。</summary>
        public string PhaseText
        {
            get
            {
                switch (Phase)
                {
                    case UploadPhase.Preparing: return "正在校验文件…";
                    case UploadPhase.Uploading: return "正在上传";
                    case UploadPhase.Finishing: return "等待网盘确认…";
                    default: return "正在上传";
                }
            }
        }
    }

    /// <summary>
    /// 一批上传的收尾结果（2026-11-10 新增）。
    /// UI 用它拼提示文案，并决定要不要刷新目录。
    /// </summary>
    public sealed class UploadOutcome
    {
        /// <summary>成功上传的文件数。</summary>
        public int Uploaded { get; set; }

        /// <summary>命中秒传的文件数（含在 Uploaded 里，单独计数只为文案更准确）。</summary>
        public int RapidUploaded { get; set; }

        /// <summary>失败的文件数。</summary>
        public int Failed { get; set; }

        /// <summary>是否被用户取消。</summary>
        public bool Cancelled { get; set; }

        /// <summary>失败原因（第一条）。成功时为 null。</summary>
        public string Error { get; set; }

        /// <summary>收尾提示文案（界面直接显示）。</summary>
        public string SummaryText
        {
            get
            {
                if (!string.IsNullOrEmpty(Error)) return Error;
                if (Cancelled) return "已取消上传";
                if (Uploaded <= 0) return "没有文件被上传";
                if (RapidUploaded > 0 && RapidUploaded == Uploaded)
                    return "秒传成功 " + RapidUploaded + " 个文件（网盘已有相同内容，未消耗流量）";
                if (RapidUploaded > 0)
                    return "已上传 " + Uploaded + " 个文件（其中 " + RapidUploaded + " 个走秒传）";
                return "已上传 " + Uploaded + " 个文件";
            }
        }
    }

    /// <summary>
    /// 上传操作的异常（2026-11-10 新增）。
    ///
    /// <para>单独一个类型是为了让 UI 能区分「可预期的业务失败」
    /// （网盘拒绝、空间不足、格式不支持）与真正的编程错误。</para>
    /// </summary>
    public class UploadException : Exception
    {
        public UploadException(string message) : base(message) { }
        public UploadException(string message, Exception inner) : base(message, inner) { }
    }
}
