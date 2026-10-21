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
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.Core.Update
{
    /// <summary>安装阶段（UI 用来显示"下到哪儿了"）。</summary>
    public enum UpdateStage
    {
        /// <summary>正在下载安装包。</summary>
        Downloading,
        /// <summary>正在解压。</summary>
        Extracting,
        /// <summary>正在准备接管进程的外部脚本。</summary>
        Preparing,
        /// <summary>外部脚本已启动，主程序准备退出。</summary>
        Restarting,
        /// <summary>失败。</summary>
        Failed,
    }

    /// <summary>安装进度回调。</summary>
    public sealed class UpdateProgress
    {
        /// <summary>当前阶段。</summary>
        public UpdateStage Stage { get; set; }

        /// <summary>已下载字节（下载阶段）。</summary>
        public long Downloaded { get; set; }

        /// <summary>安装包总字节；未知为 0。</summary>
        public long Total { get; set; }

        /// <summary>给用户看的一句话。</summary>
        public string Message { get; set; }
    }

    /// <summary>
    /// 「一键更新」的安装编排：下载 → 解压 → 外部小脚本接力 → 覆盖 → 重启。
    ///
    /// 【为什么必须借外部进程的手】覆盖 <c>GeZi.exe</c> 时它正被自己运行着，
    /// Windows 会拒绝写入映像文件（"另一个程序正在使用此文件"）。所以顺序必须是：
    /// 主程序<b>先退出</b> → 脚本覆盖文件 → 脚本把新版本拉起来。
    /// 只要主进程还活着，就不可能由它自己完成覆盖。
    ///
    /// 【为什么用 PowerShell 而不是自带 updater.exe】本程序刻意保持"零安装、绿色便携"，
    /// 多带一个 updater.exe 既破坏这个性质，也会被杀软盯着看。PowerShell
    /// 在 Win10 1903+ / Win11 上必然存在（系统组件），写一小段 .cmd/.ps1 放进临时目录，
    /// 让它在主进程退出后执行，是最薄的一层。
    ///
    /// 【安全性】安装包一律只从<b>检查更新那一刻确认过</b>的地址下载
    /// （GitHub 的 <c>browser_download_url</c> 或夸克分享直链），
    /// 且下载后校验 ZIP 结构完整性；解压只往程序目录写，不碰别处。
    /// </summary>
    public sealed class UpdateInstaller
    {
        /// <summary>主进程退出后，脚本最多等多少毫秒再开始覆盖。</summary>
        /// <remarks>
        /// 取 2500ms 的理由：主进程从 <c>Environment.Exit</c> 到句柄真正释放有几百毫秒
        /// 抖动（托盘窗口、文件流 flushing）。等太久用户只会觉得"卡"，太短覆盖会失败。
        /// </remarks>
        private const int DelayBeforeOverwriteMs = 2500;

        /// <summary>等待主进程真正结束的最长时间（脚本侧）。</summary>
        private const int WaitProcessExitMs = 60000;

        private readonly string _appDir;

        public UpdateInstaller(string appDir)
        {
            if (string.IsNullOrEmpty(appDir)) throw new ArgumentNullException("appDir");
            _appDir = appDir;
        }

        /// <summary>程序所在目录（用于把更新内容覆盖到哪里）。</summary>
        public string AppDir
        {
            get { return _appDir; }
        }

        /// <summary>
        /// 执行一次「一键更新」。成功返回 true，并由 <paramref name="onRestarting"/>
        /// 的调用方负责 <c>Environment.Exit</c>。
        /// 失败抛 <see cref="UpdateInstallException"/>（含用户可读的中文原因）。
        /// </summary>
        public async Task<bool> InstallAsync(string packageUrl, string expectedFileName,
            UpdateVersion version, IProgress<UpdateProgress> progress = null,
            CancellationToken ct = default(CancellationToken))
        {
            string work = Path.Combine(Path.GetTempPath(), "GeZi-Update-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(work);

                string zip = await DownloadAsync(packageUrl, work,
                    string.IsNullOrEmpty(expectedFileName) ? "GeZi-update.zip" : expectedFileName,
                    progress, ct).ConfigureAwait(false);
                if (zip == null || !File.Exists(zip))
                    throw new UpdateInstallException("安装包下载失败，请稍后重试或改用「查看详情」手动更新。");

                Report(progress, UpdateStage.Extracting, 0, 0, "正在解压安装包…");
                string extracted;
                try
                {
                    extracted = Path.Combine(work, "extracted");
                    ZipFile.ExtractToDirectory(zip, extracted);
                }
                catch (InvalidDataException)
                {
                    throw new UpdateInstallException("安装包不完整或不是有效的 ZIP，请重新下载。");
                }
                // 兼容"zip 里还套了一层目录"的打包方式
                extracted = NormalizeRoot(extracted);

                if (!LooksLikeAppDir(extracted))
                    throw new UpdateInstallException(
                        "安装包里没有找到 GeZi.exe，来源可能不对，已中止更新。");

                Report(progress, UpdateStage.Preparing, 0, 0, "正在准备重启…");
                string script = WriteUpdaterScript(work, extracted);
                LaunchDetached(script);

                Report(progress, UpdateStage.Restarting, 0, 0, "即将重启完成更新…");
                return true;
            }
            catch (UpdateInstallException)
            {
                TryDelete(work);
                throw;
            }
            catch (OperationCanceledException)
            {
                TryDelete(work);
                throw new UpdateInstallException("更新已取消。");
            }
            catch (Exception ex)
            {
                TryDelete(work);
                throw new UpdateInstallException("更新失败：" + ex.Message);
            }
        }

        // ---------------- 下载 ----------------

        private async Task<string> DownloadAsync(string url, string dir, string fileName,
            IProgress<UpdateProgress> progress, CancellationToken ct)
        {
            string zip = Path.Combine(dir, SafeFileName(fileName));
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = "GeZi-Updater";
            request.Timeout = 60000;
            request.ReadWriteTimeout = 60000;
            request.Proxy = null;      // 与下载器一致：不跟系统代理走

            using (ct.Register(() => { try { request.Abort(); } catch { } }))
            using (var resp = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
            using (var src = resp.GetResponseStream())
            using (var dst = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (src == null) return null;
                long total = resp.ContentLength;
                long got = 0;
                var buf = new byte[256 * 1024];
                int n;
                while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                    got += n;
                    Report(progress, UpdateStage.Downloading, got, total, null);
                }
            }
            return zip;
        }

        // ---------------- 脚本接力 ----------------

        /// <summary>
        /// 生成并返回接管脚本的路径。脚本做四件事：
        /// ① 等主进程退出 → ② 覆盖程序目录 → ③ 删掉旧的用户配置?（不删）→ ④ 拉起新版本。
        /// 用 .cmd 而不是 .ps1：cmd 的启动延迟更低、不受执行策略（ExecutionPolicy）影响。
        /// </summary>
        private string WriteUpdaterScript(string work, string extracted)
        {
            string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            string exeName = Path.GetFileName(exe);
            string exeBase = Path.GetFileNameWithoutExtension(exe);
            string pid = System.Diagnostics.Process.GetCurrentProcess().Id.ToString();
            string log = Path.Combine(Path.GetTempPath(), "GeZi-Update.log");

            // 覆盖用 xcopy：能递归、能只更新已有/新增的文件、退出码明确。
            // /Y 覆盖不询问，/I 允许目标是目录，/Q 安静，/K 保留文件属性。
            // 刻意不用 /D：那是"较新才复制"，而打包时间戳不一定比本机新。
            string script = Path.Combine(work, "gezi-updater.cmd");
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine("setlocal");
            sb.Append("echo [").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
              .AppendLine("] GeZi updater started");
            sb.Append("set PID=").AppendLine(pid);
            sb.Append("set APP=\"").Append(_appDir).AppendLine("\"");
            sb.Append("set SRC=\"").Append(extracted).AppendLine("\"");
            sb.Append("set EXEBASE=\"").Append(exeBase).AppendLine("\"");
            sb.Append("set EXEFILE=\"").Append(exeName).AppendLine("\"");
            sb.Append("set LOG=\"").Append(log).AppendLine("\"");

            // ① 等主进程退出。用 tasklist 而不是 Get-Process —— 后者要加载 .NET，
            // 在 2500ms 的等待窗里多花一秒以上。失败（进程已不存在）就继续往下走。
            // 最多等 120 轮 × 500ms = 60 秒，之后照样继续（那时它多半已经退了）。
            sb.AppendLine("echo waiting for main process to exit...");
            sb.AppendLine("set TRIES=120");
            sb.AppendLine(":waitloop");
            sb.AppendLine("tasklist /fi \"pid eq %PID%\" 2>nul | find /i \"%EXEBASE%\" >nul || goto copied");            sb.AppendLine("set /a TRIES-=1");
            sb.AppendLine("if %TRIES% LEQ 0 goto copied");
            sb.AppendLine("ping -n 2 127.0.0.1 >nul");
            sb.AppendLine("goto waitloop");
            sb.AppendLine(":copied");
            sb.AppendLine("echo copying files...");
            sb.AppendLine("xcopy /Y /I /Q /K /E %SRC%\\* %APP%");
            sb.AppendLine("if errorlevel 1 (echo COPY FAILED >> %LOG% & ping -n 3 127.0.0.1 >nul & exit /b 1)");

            // ③ 清理临时目录（解压出来的包 + 本脚本所在的目录）
            sb.Append("rd /s /q \"").Append(work).AppendLine("\" 2>nul");

            // ④ 拉起新版本。start 的第二个参数是窗口标题，不能省
            //    （省了 cmd 会把第一个带引号的参数当成标题）。
            sb.Append("start \"GeZi updater\" \"").Append(_appDir).Append("\\").Append(exeName).AppendLine("\"");
            sb.AppendLine("echo done >> %LOG%");
            sb.AppendLine("del \"%~f0\" 2>nul");
            sb.AppendLine("exit /b 0");

            File.WriteAllText(script, sb.ToString(), new UTF8Encoding(false));
            return script;
        }

        /// <summary>以独立进程组启动脚本，主进程退出后它仍能继续跑。</summary>
        private static void LaunchDetached(string script)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = script,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(script),
            };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                // 不 WaitForExit：脚本自己要跑几十秒（覆盖 900MB 安装包时更久）
            }
        }

        // ---------------- 小工具 ----------------

        private static void Report(IProgress<UpdateProgress> progress, UpdateStage stage,
            long got, long total, string message)
        {
            if (progress == null) return;
            try
            {
                progress.Report(new UpdateProgress
                {
                    Stage = stage,
                    Downloaded = got,
                    Total = total,
                    Message = message,
                });
            }
            catch { }
        }

        /// <summary>zip 常常把文件放在一个顶层目录里；这里把"真正的程序根"找出来。</summary>
        private static string NormalizeRoot(string dir)
        {
            try
            {
                var subs = Directory.GetDirectories(dir);
                var files = Directory.GetFiles(dir);
                if (files.Length == 0 && subs.Length == 1)
                    return subs[0];
            }
            catch { }
            return dir;
        }

        /// <summary>确认这个目录里确实有 GeZi.exe（防止解压出个不相干的东西）。</summary>
        private static bool LooksLikeAppDir(string dir)
        {
            try
            {
                return File.Exists(Path.Combine(dir, "GeZi.exe"));
            }
            catch { return false; }
        }

        /// <summary>去掉路径符号，防止安装包名里带 ../.. 跳出工作目录。</summary>
        private static string SafeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Replace("..", "_");
        }

        private static void TryDelete(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { }
        }
    }

    /// <summary>一键更新失败（消息保持用户可读）。</summary>
    public sealed class UpdateInstallException : Exception
    {
        public UpdateInstallException(string message) : base(message) { }
        public UpdateInstallException(string message, Exception inner) : base(message, inner) { }
    }
}