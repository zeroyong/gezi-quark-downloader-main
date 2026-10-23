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

        public UpdateInstaller(string appDir) : this(appDir, null) { }

        /// <summary>
        /// 构造安装器。
        /// </summary>
        /// <param name="appDir">程序所在目录（覆盖目标）。</param>
        /// <param name="cookie">
        /// 登录 Cookie。**必须传**：夸克源的直链在游客态会降级到
        /// <c>dl-guest-*</c> 域名，下载被 CDN 拒（412）；登录态才是 <c>dl-pc-*</c>。
        /// GitHub 源的直链不需要它，传空也能下。
        /// </param>
        public UpdateInstaller(string appDir, string cookie)
        {
            if (string.IsNullOrEmpty(appDir)) throw new ArgumentNullException("appDir");
            _appDir = appDir;
            _cookie = cookie ?? "";
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

        /// <summary>
        /// 下载安装包。
        ///
        /// <para>【2026-10-23 重写：复用下载器那套 HttpClient + CookieContainer】</para>
        ///
        /// <para><b>原来的实现为什么不行</b>：用的是 <see cref="HttpWebRequest"/> +
        /// 手写请求头（<c>req.Headers.Add("Cookie", ...)</c>），
        /// 实测对夸克 OSS 直链（<c>dl-pc-sz.pds.quark.cn</c>，带
        /// <c>Signature</c>/<c>callback</c>/<c>ud</c> 参数）<b>一律 403</b>，
        /// 而**同一个 URL 交给下载器就能下</b>。</para>
        ///
        /// <para><b>差别在哪</b>：下载器用 <c>HttpClient</c> + <see cref="CookieContainer"/>
        /// 种 cookie，并由 <see cref="NetworkOptions"/> 统一代理策略；
        /// 手写 <c>HttpWebRequest</c> 会走另一条 request pipeline
        /// （默认跟随系统代理、cookie 头手工拼、header 规范化不同），
        /// OSS 的签名校验对这类差异很敏感。</para>
        ///
        /// <para><b>结论</b>：既然"下载器能下"，就<b>直接用下载器</b> ——
        /// 这也正合用户的要求：「内置更新本质上也是下载最新版，
        /// 那就在下载成功的地方复用，相当于不用人点击下载」。</para>
        /// </summary>
        private async Task<string> DownloadAsync(string url, string dir, string fileName,
            IProgress<UpdateProgress> progress, CancellationToken ct)
        {
            string zip = Path.Combine(dir, SafeFileName(fileName));

            // 【2026-10-23 逐字节对照用的完整诊断】
            // 用户实测：同一个包、同一份登录态，手动免转存下载 HTTP 206 成功，
            // 但「一键更新」走到这里就 412。为了在不接触用户机器的情况下定位，
            // 这里把**请求的全部可观测面**打出来：
            //   · 直链的每一个查询参数（auth_key / token / sp ... 逐项列出）
            //   · Cookie 长度与头几个 key（不打值，避免泄漏登录态）
            //   · 实际发出的请求头键名
            // 拿到两份日志（成功的 + 失败的）就能逐字对比出差异。
            try
            {
                DumpUrlDiagnostics(url);
            }
            catch { }

            // 安装包只有几 MB，单线程足够；线程数给小值避免占满连接池。
            int threads = 4;
            // 关键：带上登录 Cookie。夸克分享直链在游客态会被降级到
            // dl-guest-* 域名并最终 412，登录态才是 dl-pc-*。
            using (var dl = new GeZi.Core.Download.SegmentedDownloader(
                       threads: threads, cookie: _cookie ?? "",
                       writeMode: GeZi.Core.Download.WriteMode.SegmentedFiles, partsRoot: null))
            {
                dl.Log = m => Log("  [upd-dl] " + m);

                var inner = new Progress<GeZi.Core.Download.DownloadProgress>(p =>
                {
                    if (p == null) return;
                    Report(progress, UpdateStage.Downloading, p.Done, p.Total, null);
                });

                var res = await dl.DownloadAsync(url, zip, inner, ct,
                    new GeZi.Core.Download.PauseToken()).ConfigureAwait(false);

                if (!res.Ok)
                {
                    Log("安装包下载失败: " + res.Message);
                    return null;
                }
            }

            if (!File.Exists(zip) || new FileInfo(zip).Length == 0)
            {
                Log("安装包下载后文件不存在或为空");
                return null;
            }
            Log("安装包下载完成: " + new FileInfo(zip).Length + " 字节");
            return zip;
        }

        /// <summary>
        /// 把直链与请求上下文的关键信息打进日志（2026-10-23，定位 412 用）。
        ///
        /// <para>【2026-10-23 精简】原来这里把每个查询参数、每个 Cookie 键名都列一遍，
        /// 排查时有用，但普通用户点一次「一键更新」会刷十几行看不懂的东西。
        /// 现在只留<b>两个真正有诊断价值</b>的：
        /// 直链落在哪个域名（`drive` vs `pds` vs `guest`）、以及是不是登录态。
        /// 想看完整参数请点日志框的「详细」—— 但即使详细级也只打 host/长度，
        /// 不再逐参数展开（那个信息量主要给我排查时用临时程序打）。</para>
        /// </summary>
        private void DumpUrlDiagnostics(string url)
        {
            try
            {
                var u = new Uri(url);
                string host = u.Host;
                string kind = host.IndexOf("guest", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "游客域名"
                    : (u.Query.IndexOf("Signature=", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "OSS 签名链" : "auth_key 链");
                Log("更新包直链: " + host + "（" + kind + "，"
                    + (_cookie ?? "").Length + " 字节登录态）");

                if (string.IsNullOrEmpty(_cookie))
                    Log("⚠ 更新流程没有登录态 —— 夸克会降级到游客域名，下载必被拒（412）");
            }
            catch { }
        }

        /// <summary>
        /// 下载安装包时用的登录 Cookie（来自设置）。
        /// <para>空串 = 游客态，夸克源会拿到 <c>dl-guest-*</c> 直链并最终 412，
        /// 所以主程序在构造本类时应当把登录 Cookie 传进来。</para>
        /// </summary>
        private readonly string _cookie;

        /// <summary>诊断日志出口（主窗口挂到「运行日志」）。为空则静默。</summary>
        public static Action<string> Log { get; set; }

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

            // ⚠️⚠️ 【2026-10-23 血泪教训：变量必须**先定义**再使用】⚠️⚠️
            //
            // 上一版把「重置日志」写在 `set LOG=` **之前**，于是脚本第 5 行变成：
            //     echo ---------------------------------------- >> %LOG%
            // 此时 %LOG% 还是空串 → 实际执行 `echo xxx >>`（重定向没有目标）。
            // CMD 遇到这种情况会直接**终止整个批处理**，日志里只剩第一行，
            // 覆盖 / 重启 / 清理全都没执行 —— 实测"更新后 exe 还是旧版本"。
            //
            // 所以这里先把所有变量一次性定义好，之后才允许出现任何 %LOG% / %APP% / %SRC%。
            sb.Append("set APP=\"").Append(_appDir.TrimEnd('\\')).AppendLine("\"");
            sb.Append("set SRC=\"").Append(extracted.TrimEnd('\\')).AppendLine("\"");
            sb.Append("set EXEBASE=\"").Append(exeBase).AppendLine("\"");
            sb.Append("set EXEFILE=\"").Append(exeName).AppendLine("\"");
            sb.Append("set LOG=\"").Append(log).AppendLine("\"");

            // 日志每次更新**重置**（不跨次累积）+ 首行写清"这是哪一次"。
            //
            // 【为什么必须清】日志文件名固定为 GeZi-Update.log。实测用户读完一次
            // 成功更新后，文件里同时躺着上一次的 `COPY OK`/`done` 和这一次的
            // `SKIPPED-FILES`/`COPY PARTIAL-FAILED`，根本无法判断这次到底成没成。
            sb.AppendLine("> \"%LOG%\" echo [%DATE% %TIME%] GeZi updater started (pid=" + pid + ")");
            sb.AppendLine("echo ---------------------------------------- >> \"%LOG%\"");
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
            sb.AppendLine("echo waiting for main process to exit... >> \"%LOG%\"");
            sb.AppendLine("set TRIES=120");
            sb.AppendLine(":waitloop");
            sb.AppendLine("tasklist /fi \"pid eq " + pid + "\" 2>nul | find /i \"%EXEBASE%\" >nul || goto copy_phase");
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
            // 🚨 【2026-10-23 修】这里必须用 **!SKIPPED! / !FAILED!**（延迟展开），
            //    不能用 %SKIPPED% / %FAILED%（解析时展开）。
            //
            // 【为什么】整段脚本在一个 `for ... do call` 里循环了 8 次，
            //    `%VAR%` 在**整行被解析的那一刻**就替换成当时的值，
            //    而 `!VAR!` 才在执行到该行时取值。
            //    用 %VAR% 的结果是：日志里打印的永远是循环**开始前**的值（空或 0），
            //    于是"明明有文件被跳过"却写成 COPY OK，或者反过来。
            sb.AppendLine("if not \"!SKIPPED!\"==\"\" echo SKIPPED-FILES: !SKIPPED! >> %LOG%");
            sb.AppendLine("if !FAILED! EQU 0 (echo COPY OK >> %LOG%) else (echo COPY PARTIAL-FAILED: !FAILED! file(s) >> %LOG%)");
            sb.AppendLine("if !FAILED! EQU 0 goto relaunch");

            // ③ 还有文件被占用 → 轮询重试（主进程可能刚退出，句柄还没释放；
            //    或者用户自己又开了一个实例）。最多 20 轮 × 1 秒。
            //
            // 【2026-10-23 实测确认这条路径**真的会走**】
            //   第一次更新时 GeZi.Core.dll 与 GeZi.exe 确实被占用（主进程刚退，
            //   映像句柄还没释放），首轮 FAILED=2，重试第 1 轮就成功了。
            //   最终 8 个文件的 SHA256 与官方包完全一致 —— 更新是成功的。
            //
            //   ⚠️ 但旧日志只留下「SKIPPED-FILES」而没有「重试已修好」，
            //   用户看到的就是"更新失败了"。所以这里补一行明确的 RETRY-OK。
            sb.AppendLine("echo some files are locked, retrying... >> %LOG%");
            sb.AppendLine("set RTRIES=20");
            sb.AppendLine("set ROUND=0");
            sb.AppendLine(":retryloop");
            sb.AppendLine("set /a ROUND+=1");
            sb.AppendLine("set FAILED=0");
            sb.AppendLine("set SKIPPED=");
            sb.AppendLine("for /f \"usebackq delims=\" %%F in (\"%TEMP%\\gezi_filelist.txt\") do call :copyone \"%SRC%\\%%F\"");
            sb.AppendLine("if !FAILED! EQU 0 (echo RETRY-OK on round !ROUND!: all files now match >> %LOG%) & goto relaunch");
            sb.AppendLine("set /a RTRIES-=1");
            sb.AppendLine("if %RTRIES% LEQ 0 goto giveup");
            sb.AppendLine("ping -n 2 127.0.0.1 >nul");
            sb.AppendLine("goto retryloop");

            // 全部重试都失败：记下细节，把已成功的部分留着（新版 dll 已就位，
            // 只是 exe 没换），仍然拉起程序 —— 用户至少能用到大部分新版本。
            sb.AppendLine(":giveup");
            sb.AppendLine("echo COPY FAILED after retries >> %LOG%");
            // 同上：必须用 !SKIPPED!（延迟展开），否则打印的是空串。
            sb.AppendLine("if not \"!SKIPPED!\"==\"\" echo STILL-LOCKED: !SKIPPED! >> %LOG%");
            sb.AppendLine("echo --- per-file xcopy output --- >> %LOG%");
            // ⚠️ 这里也不能用 xcopy "%SRC%\*"：cmd 不展开带引号的通配符。
            //    逐个文件再试一遍，把每个文件的真实报错写进日志。
            sb.AppendLine("for /f \"usebackq delims=\" %%F in (\"%TEMP%\\gezi_filelist.txt\") do (");
            sb.AppendLine("    xcopy /Y /I /Q \"%SRC%\\%%F\" \"%APP%\\%%F\" >> %LOG% 2>&1");
            sb.AppendLine(")");
            sb.AppendLine("goto relaunch");

            // ④ 清理 + 拉起新版本 + 自删除
            //
            // 🚨 【2026-10-23 实测修掉的两个坑】
            //
            // 【坑 1：删自己所在目录会让脚本当场毙命】
            //   上一版把 `rd /s /q "%work%"` 写在这里，而**脚本自己就在 %work% 里**
            //   （gezi-updater.cmd）。rd 会先把目录内的文件删光（包括脚本），
            //   cmd 随即无法读取下一行 → 后面的 `del gezi_filelist.txt`、
            //   `start explorer`、`echo done` **全部没有执行**。
            //   实测日志停在 COPY OK，没有 done；%TEMP% 残留空目录 + 文件清单。
            //   → 改成最后用 `(goto) 2>nul & rd /s /q ...`：
            //     (goto) 会让 cmd 先退出批处理上下文（不再读这个文件），
            //     然后才删目录 —— 这是自删除目录的标准做法。
            //
            // 【坑 2：清理顺序】filelist 必须在删目录**之前**独立删掉，
            //   它不在 work 里，删 work 带不走它。
            sb.AppendLine(":relaunch");
            sb.AppendLine("rd /s /q \"%SRC%\" 2>nul");
            sb.AppendLine("del \"%TEMP%\\gezi_filelist.txt\" 2>nul");

            // ⑤ 拉起新版本。
            //
            // 🚨 【2026-10-23 修】用户实测「更新完成就自动退出应用了，没有再起来」。
            //
            // 【旧写法】start "GeZi updater" "C:\...\GeZi.exe"
            // 【为什么没起来】本脚本由主进程以 CreateNoWindow + WindowStyle=Hidden 启动，
            //   继承了一个**隐藏的**控制台。`start` 启动 GUI 程序时，
            //   新进程会继承这个隐藏窗口的属性；再加上脚本紧接着自我删除，
            //   start 的异步拉起来不及稳定，最终什么都没出现。
            //
            // 【新写法】用 explorer.exe 代为启动：它属于**用户会话的 shell**，
            //   天然可见、独立、不继承本脚本任何窗口属性。
            //   即使脚本立刻退出，explorer 也已经把进程创建好了。
            sb.Append("start \"\" explorer.exe \"").Append(_appDir.TrimEnd('\\'))
              .Append("\\").Append(exeName).AppendLine("\"");
            sb.AppendLine("echo relaunch requested via explorer >> \"%LOG%\"");
            sb.AppendLine("echo done >> \"%LOG%\"");

            // ⑥ 自删除。
            //
            // 【2026-10-23 实测结论：不再在脚本里硬凑删目录】
            // `rd /s /q` 能把目录**内容**清空，但目录本身常留下一个空壳 ——
            // cmd.exe 自己还站在里面，脚本文件句柄释放要几十毫秒，
            // rd 删到最后一步因"目录非空"失败。
            // 试过 `(goto) 2>nul & rd ...` 与 `start /min cmd /c "timeout & rd"`
            // 两种绕法，在真实环境里都不稳定（嵌套引号太深，cmd 解析容易走偏）。
            //
            // → 脚本只负责清内容；**空壳交给主程序下次启动时清理**
            //   （见 CleanupStaleWorkDirs，.NET 里判断"是否为空 / 多久以前"可靠得多）。
            //   顺带也清理历史版本遗留的残留，用户不会看到 %TEMP% 越攒越多。
            sb.AppendLine("cd /d \"%TEMP%\"");
            sb.AppendLine("(goto) 2>nul & rd /s /q \"" + work + "\" 2>nul");
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
        /// <remarks>
        /// <para>【2026-10-23】<c>CreateNoWindow</c> 保持 true（不让用户看到黑框），
        /// 但**重启新程序不能靠这个隐藏进程里的 <c>start</c>** ——
        /// 见 <see cref="WriteUpdaterScript"/> 里 ⑤ 的说明，那边已改用
        /// <c>explorer.exe</c> 代为拉起，不继承本脚本的隐藏窗口属性。</para>
        /// </remarks>
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

        /// <summary>
        /// 清理历次更新留在 <c>%TEMP%</c> 的残留（2026-10-23 新增）。
        ///
        /// <para>【为什么需要】更新脚本最后会尽力 <c>rd /s /q</c> 自己的工作目录，
        /// 但那是 cmd 在删**自己正站在里面**的目录 —— 实测能把内容清空，
        /// 目录本身常常留下一个空壳（几百字节，无害但越攒越多）。
        /// 与其在 batch 里跟嵌套引号搏斗，不如让 .NET 在下次启动时扫一遍：
        /// 判断"是否为空 / 是否超过 N 小时"都可靠得多。</para>
        ///
        /// <para>【清理规则】只动 <c>GeZi-Update-&lt;32位hex&gt;</c> 这种自己命名格式的目录，
        /// 且<b>只删空目录</b>或<b>12 小时前的目录</b>。
        /// 绝不碰别的东西 —— 万一用户 %TEMP% 里有同名文件也会被格式过滤掉。</para>
        ///
        /// <para>整个方法吞掉所有异常：清理失败绝不该影响启动。</para>
        /// </summary>
        public static void CleanupStaleWorkDirs()
        {
            try
            {
                string temp = Path.GetTempPath();
                if (string.IsNullOrEmpty(temp) || !Directory.Exists(temp)) return;

                foreach (string dir in Directory.GetDirectories(temp, "GeZi-Update-*"))
                {
                    string name = Path.GetFileName(dir);
                    // 只认自己生成的格式：GeZi-Update- + 32 位十六进制（Guid "N"）
                    if (name.Length != "GeZi-Update-".Length + 32) continue;
                    string hex = name.Substring("GeZi-Update-".Length);
                    bool allHex = true;
                    foreach (char c in hex)
                    {
                        if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                        { allHex = false; break; }
                    }
                    if (!allHex) continue;

                    try
                    {
                        bool empty = Directory.GetFileSystemEntries(dir).Length == 0;
                        bool old = false;
                        try
                        {
                            old = (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir)) > TimeSpan.FromHours(12);
                        }
                        catch { }

                        if (empty || old)
                            Directory.Delete(dir, true);
                    }
                    catch { /* 单个目录删不掉不影响其它 */ }
                }
            }
            catch { }
        }

        /// <summary>顺带清掉脚本用的文件清单（异常时可能残留）。</summary>
        public static void CleanupStaleFileList()
        {
            try
            {
                string f = Path.Combine(Path.GetTempPath(), "gezi_filelist.txt");
                if (File.Exists(f)) File.Delete(f);
            }
            catch { }
        }

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