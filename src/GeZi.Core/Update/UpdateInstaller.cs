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
        /// 生成并返回接管脚本的路径。脚本做五件事：
        /// ① 等主进程退出 → ② 逐个文件覆盖（被占用的跳过）→ ③ 重试占用项 →
        /// ④ 删掉临时目录 → ⑤ 拉起新版本。
        /// 用 .cmd 而不是 .ps1：cmd 的启动延迟更低、不受执行策略（ExecutionPolicy）影响。
        /// </summary>
        /// <remarks>
        /// <para><b>【2026-10-22 修复的三个真 bug】</b>（用户实测"一键更新失败，
        /// 进度显示下载成功但重启还是旧版本"）：</para>
        /// <list type="number">
        /// <item><b>目标路径末尾反斜杠</b>：原来写成
        ///   <c>set APP="D:\...\win64\"</c>，cmd 会把结尾的 <c>\"</c> 当转义引号，
        ///   路径被截断，xcopy 报 <c>Unable to create directory</c> +
        ///   <c>Cannot create a file when that file already exists</c>，<b>一个文件都拷不进去</b>。
        ///   现在 APP 变量里<b>不带</b>末尾反斜杠，反斜杠写在使用处（<c>%APP%\</c>）。</item>
        /// <item><b>"进程没了"不等于"文件锁释放了"</b>：原来只等 tasklist 里查不到 PID，
        ///   但映像文件的句柄释放要晚几百毫秒；用户若此时手动重开程序，
        ///   覆盖 <c>GeZi.exe</c> 必然 <c>Sharing violation</c>。
        ///   现在改成<b>以"能不能覆盖成功"为准</b>：轮询重试，覆盖成功才算完。</item>
        /// <item><b>失败只有一行日志</b>：原来 <c>if errorlevel 1</c> 只写
        ///   <c>COPY FAILED</c> 就退出，用户完全看不到原因。现在把 xcopy 的
        ///   <b>完整输出</b>写进日志，并区分"部分成功"与"完全失败"。</item>
        /// </list>
        /// </remarks>
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
            sb.AppendLine("setlocal EnableDelayedExpansion");
            sb.Append("echo [").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
              .AppendLine("] GeZi updater started");
            // ⚠️ APP **不能带末尾反斜杠**：cmd 里 "C:\dir\" 的 \" 是转义引号，
            //    会把路径截断掉一截，xcopy 立刻报"目录已存在/无法创建"。
            //    需要目录分隔符时写在变量外面（%APP%\）。
            sb.Append("set APP=\"").Append(_appDir.TrimEnd('\\')).AppendLine("\"");
            sb.Append("set SRC=\"").Append(extracted.TrimEnd('\\')).AppendLine("\"");
            sb.Append("set EXEBASE=\"").Append(exeBase).AppendLine("\"");
            sb.Append("set EXEFILE=\"").Append(exeName).AppendLine("\"");
            sb.Append("set LOG=\"").Append(log).AppendLine("\"");

            // ① 等主进程退出。用 tasklist 而不是 Get-Process —— 后者要加载 .NET，
            // 在 2500ms 的等待窗里多花一秒以上。失败（进程已不存在）就继续往下走。
            // 最多等 120 轮 × 500ms = 60 秒，之后照样继续（那时它多半已经退了）。
            sb.AppendLine("echo waiting for main process to exit...");
            sb.AppendLine("set TRIES=120");
            sb.AppendLine(":waitloop");
            sb.AppendLine("tasklist /fi \"pid eq %PID%\" 2>nul | find /i \"%EXEBASE%\" >nul || goto copy_phase");
            sb.AppendLine("set /a TRIES-=1");
            sb.AppendLine("if %TRIES% LEQ 0 goto copy_phase");
            sb.AppendLine("ping -n 2 127.0.0.1 >nul");
            sb.AppendLine("goto waitloop");

            // ② 覆盖阶段。
            //    关键设计：**先删目标再拷**。del 对"文件不存在"返回 0，
            //    对"被占用"返回 1 —— 正好用它的退出码区分两种情况。
            //    覆盖用 xcopy（能递归、能覆盖、退出码明确）。
            sb.AppendLine(":copy_phase");
            sb.AppendLine("echo copying files...");
            sb.AppendLine("set FAILED=0");
            sb.AppendLine("set SKIPPED=");
            // ⚠️ cmd 的 for 循环**不会展开带引号的通配符**：
            //    for %%F in ("%SRC%\*") 拿到的是字面量 "%SRC%\*"，一个文件都匹配不到；
            //    而 for %%F in (%SRC%\*)（不加引号）在路径含空格时会拆成多个参数。
            //    正确做法：先用 dir /b 列出文件名（每行一个），再用 for /f 逐行取。
            // ⚠️ 这里必须用 Append 拼完整行再 AppendLine —— 如果先
            //    AppendLine("dir /b \"") 再 Append(path)，AppendLine 会在
            //    前半段末尾插一个换行，路径被劈成两半，dir 就会去列**当前目录**
            //    （实测把项目根目录的文件全拷进了程序目录！）。
            sb.Append("dir /b \"").Append(extracted.TrimEnd('\\')).Append("\\*\" > \"%TEMP%\\gezi_filelist.txt\" 2>nul").AppendLine();
            // ⚠️ 必须传**全路径**：dir /b 出来的只是文件名。
            //    call :copyone "%%F" 里 %~1 拿到的是 "GeZi.exe"（相对路径），
            //    :copyone 里的 if not exist "%SRCF%" 会因为找不到而**整批跳过** ——
            //    表现为"日志写着 COPY OK，其实一个文件都没拷"。
            sb.AppendLine("for /f \"usebackq delims=\" %%F in (\"%TEMP%\\gezi_filelist.txt\") do call :copyone \"%SRC%\\%%F\"");
            sb.AppendLine("if not \"%SKIPPED%\"==\"\" echo SKIPPED-FILES: %SKIPPED% >> %LOG%");
            sb.AppendLine("if %FAILED% EQU 0 (echo COPY OK >> %LOG%) else (echo COPY PARTIAL-FAILED >> %LOG%)");
            sb.AppendLine("if %FAILED% EQU 0 goto relaunch");

            // ③ 还有文件被占用 → 轮询重试（主进程可能刚退出，句柄还没释放；
            //    或者用户自己又开了一个实例）。最多 20 轮 × 1 秒。
            sb.AppendLine("echo some files are locked, retrying...");
            sb.AppendLine("set RTRIES=20");
            sb.AppendLine(":retryloop");
            sb.AppendLine("set FAILED=0");
            sb.AppendLine("set SKIPPED=");
            sb.AppendLine("for /f \"usebackq delims=\" %%F in (\"%TEMP%\\gezi_filelist.txt\") do call :copyone \"%SRC%\\%%F\"");
            sb.AppendLine("if %FAILED% EQU 0 goto relaunch");
            sb.AppendLine("set /a RTRIES-=1");
            sb.AppendLine("if %RTRIES% LEQ 0 goto giveup");
            sb.AppendLine("ping -n 2 127.0.0.1 >nul");
            sb.AppendLine("goto retryloop");

            // 全部重试都失败：记下细节，把已成功的部分留着（新版 dll 已就位，
            // 只是 exe 没换），仍然拉起程序 —— 用户至少能用到大部分新版本。
            sb.AppendLine(":giveup");
            sb.AppendLine("echo COPY FAILED after retries >> %LOG%");
            sb.AppendLine("if not \"%SKIPPED%\"==\"\" echo STILL-LOCKED: %SKIPPED% >> %LOG%");
            sb.AppendLine("echo --- xcopy output --- >> %LOG%");
            // ⚠️ 这里也不能用 xcopy "%SRC%\*"：cmd 不展开带引号的通配符。
            //    逐个文件再试一遍，把每个文件的真实报错写进日志。
            sb.AppendLine("for /f \"usebackq delims=\" %%F in (\"%TEMP%\\gezi_filelist.txt\") do (");
            sb.AppendLine("    xcopy /Y /I /Q \"%SRC%\\%%F\" \"%APP%\\%%F\" >> %LOG% 2>&1");
            sb.AppendLine(")");
            sb.AppendLine("goto relaunch");

            // ④ 清理临时目录（解压出来的包 + 本脚本所在的目录）
            sb.AppendLine(":relaunch");
            // ⚠️ 同上：不能先 AppendLine 再 Append 路径，否则路径被换行截断。
            sb.Append("rd /s /q \"").Append(work).Append("\" 2>nul").AppendLine();

            // ⑤ 拉起新版本。start 的第二个参数是窗口标题，不能省
            //    （省了 cmd 会把第一个带引号的参数当成标题）。
            sb.Append("start \"GeZi updater\" \"").Append(_appDir.TrimEnd('\\'))
              .Append("\\").Append(exeName).AppendLine("\"");
            sb.AppendLine("echo done >> %LOG%");
            sb.AppendLine("del \"%~f0\" 2>nul");
            sb.AppendLine("exit /b 0");

            // ---- 子过程：拷贝单个文件 ----
            // %1 = 源文件**全路径**。
            //
            // 【判据为什么是"xcopy 退出码 + fc /b 复核"】
            //   实测（2026-10-22，用户报"下载成功但重启还是旧版本"）：
            //     · 对**正在运行、被锁住**的 exe 执行 `del /f /q` —— cmd 仍返回 **0**，
            //       完全不可靠；`xcopy` 返回 **4**（可靠）。
            //     · `fc /b` 返回 0 = 两文件**完全相同** = 覆盖真的生效；
            //       返回 1 = 内容不同（它读不到被占用文件时也会返回 1）。
            //   所以判据是两层：
            //     ① xcopy 退出码非 0 → 覆盖失败（被占用/无权限）→ 立刻算失败；
            //     ② xcopy 成功后再 fc /b 复核，返回 0（相同）才算这个文件成功。
            //   早期版本只判 xcopy 退出码、且用 del 判"文件已删"，
            //   于是"exe 没换成功"被当成成功，日志写 COPY OK，用户重启发现版本没变。
            sb.AppendLine();
            sb.AppendLine(":copyone");
            sb.AppendLine("set \"SRCF=%~1\"");
            sb.AppendLine("set \"DSTF=%APP%\\%~nx1\"");
            sb.AppendLine("if not exist \"%SRCF%\" goto :eof");
            sb.AppendLine("xcopy /Y /I /Q \"%SRCF%\" \"%DSTF%\" >nul 2>&1");
            sb.AppendLine("if errorlevel 1 goto :copyfail");
            sb.AppendLine("fc /b \"%SRCF%\" \"%DSTF%\" >nul 2>&1");
            sb.AppendLine("if errorlevel 1 goto :copyfail");
            sb.AppendLine("goto :eof");
            sb.AppendLine(":copyfail");
            sb.AppendLine("set /a FAILED+=1");
            sb.AppendLine("set SKIPPED=!SKIPPED! \"%~nx1\";");
            sb.AppendLine("goto :eof");

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