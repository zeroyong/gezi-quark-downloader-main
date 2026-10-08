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
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace GeZi.Core.Api
{
    /// <summary>
    /// 用 service_ticket 换取夸克登录 Cookie 的「Python 桥」。
    ///
    /// <para>【为什么必须有这一层】</para>
    /// <para>
    /// 夸克 <c>pan.quark.cn</c> 的前置 ALB/WAF 会按 TLS ClientHello 的指纹过滤：
    /// 只有 ClientHello 声明了足够大/足够新的扩展（曲线组列表）时才放行，否则直接 TCP RST。
    /// 2026-10-02 实测三方对照：
    /// </para>
    /// <list type="bullet">
    /// <item>.NET Framework 的 SChannel —— ClientHello 天然偏小，<b>任何协议版本都被 RST</b>；</item>
    /// <item>Python 3.14 自带的 OpenSSL <b>3.0.18</b> —— 同样被 RST；</item>
    /// <item>Python 3.13 自带的 OpenSSL <b>3.5.7</b> —— 放行，正常握手并取回完整 Cookie。</item>
    /// </list>
    /// <para>
    /// 而登录态 Cookie（<c>__pus</c> / <c>__puus</c>）<b>只能</b>由
    /// <c>pan.quark.cn/account/info?st=</c> 下发；<c>uop.quark.cn</c> 只会下发无用的
    /// <c>_UP_*</c> 系列。所以纯 .NET 架构上无法完成扫码登录，必须借一个"能被放行"的
    /// TLS 栈 —— 本类负责找到它并调用它。
    /// </para>
    ///
    /// <para>【设计取舍】</para>
    /// <list type="bullet">
    /// <item>★ <b>程序自带一份精简 Python 运行时</b>（<c>Runtime\win-x64\</c>，约 20MB，
    ///    CPython 3.13 + OpenSSL 3.5.7），候选列表里排第一 —— 这样**用户机器上没装 Python
    ///    也能扫码登录**，不必自己折腾环境。</item>
    /// <item>脚本以嵌入资源分发，运行时释放到 <c>%TEMP%\GeZi\</c>；内置运行时则直接用
    ///   其目录内的同名脚本，无需释放。</item>
    /// <item>候选解释器逐个用 <c>--selftest</c> 探测"能否连上 pan.quark.cn"，只挑真正能用的；
    ///      结果缓存在进程内，避免每次登录都重复探测。内置 Python 若有问题，会自动回退到
    ///      本机已安装的 Python（只要其 OpenSSL 足够新）。</item>
    /// <item>完全找不到可用 Python 时返回 <c>null</c>，由调用方回退到纯 .NET 路径 ——
    ///      绝不因此让整个程序不可用。</item>
    /// </list>
    /// </summary>
    public static class QuarkPythonBridge
    {
        private const string ResourceName = "GeZi.Core.Api.quark_cookie_helper.py";
        private const string HelperFileName = "quark_cookie_helper.py";
        private const string SelftestArg = "--selftest";

        /// <summary>一次换 Cookie 的结果。</summary>
        public struct ExchangeOutcome
        {
            /// <summary>为 true 表示拿到了 <c>__pus</c> 登录态。</summary>
            public bool Ok;
            /// <summary>完整 Cookie 串（失败时可能是部分 Cookie）。</summary>
            public string Cookie;
            /// <summary>账号昵称（成功时通常有）。</summary>
            public string Nickname;
            /// <summary>失败原因（面向用户，简短）。</summary>
            public string Error;
            /// <summary>诊断明细（面向排查，可含 stderr）。</summary>
            public string Detail;
            /// <summary>本次使用的 Python 可执行文件路径（失败时可能为 null）。</summary>
            public string PythonPath;
        }

        /// <summary>
        /// 账号信息（昵称 + 头像）的查询结果。
        /// </summary>
        /// <remarks>
        /// 【为什么必须走 Python】`pan.quark.cn` 从 .NET（SChannel）根本连不上 ——
        /// 前置 ALB/WAF 按 TLS ClientHello 的曲线组扩展大小做指纹过滤，SChannel 的
        /// ClientHello 天然偏小，必然被 RST。而 `account/info` 只挂在这个域上，
        /// 所以**昵称和头像只能借 Python 桥去取**（内置 Python 的 OpenSSL 3.5.7 能过）。
        /// 纯 .NET 路径只会拿到"已登录用户"这个兜底值，头像也永远是空的。
        /// </remarks>
        public struct AccountInfo
        {
            public bool Ok;
            /// <summary>昵称。实测来自 <c>data.nickname</c>。</summary>
            public string Nickname;
            /// <summary>头像地址。实测来自 <c>data.avatarUri</c>（形如 http://image.quark.cn/...）。</summary>
            public string Avatar;
            public string Error;
        }

        /// <summary>
        /// 取账号昵称 + 头像。没有任何可用 Python 时返回 <c>null</c>（调用方保持原值即可）。
        /// </summary>
        public static AccountInfo? FetchAccountInfo(string cookie)
        {
            if (string.IsNullOrWhiteSpace(cookie)) return null;

            string diag;
            string py = FindUsablePython(out diag);
            if (py == null) return null;

            string helper = EnsureHelperScript();
            string stdout, stderr;
            Run(py, helper, new[] { "--accountinfo", cookie }, 30, out stdout, out stderr);

            var result = ParseJson(stdout);
            if (result == null) return null;

            return new AccountInfo
            {
                Ok = GetBool(result, "ok"),
                Nickname = GetString(result, "nickname"),
                Avatar = GetString(result, "avatar"),
                Error = GetString(result, "error"),
            };
        }

        // 进程级缓存：探测一次就够。null 表示"还没探测过"，空串表示"探测过，没有可用的"。
        private static string _cachedPython;
        private static bool _probed;
        private static readonly object _probeLock = new object();

        /// <summary>
        /// 探测本机是否存在"能连上 pan.quark.cn"的 Python。
        /// 返回可执行文件绝对路径；找不到返回 null。结果进程内缓存。
        /// </summary>
        public static string FindUsablePython(out string diag)
        {
            lock (_probeLock)
            {
                if (_probed)
                {
                    diag = _cachedPython == null ? "（无可用 Python，已缓存）" : "（已缓存）";
                    return string.IsNullOrEmpty(_cachedPython) ? null : _cachedPython;
                }
                _probed = true;
            }

            var sb = new StringBuilder();
            string helper = null;
            try { helper = EnsureHelperScript(); }
            catch (Exception ex) { sb.Append("释放脚本失败: " + ex.Message + "；"); }

            if (helper == null)
            {
                diag = sb.ToString();
                _cachedPython = "";
                return null;
            }

            var candidates = EnumeratePythonCandidates();
            foreach (var py in candidates)
            {
                string reason;
                bool usable;
                try
                {
                    usable = Probe(py, helper, out reason);
                }
                catch (Exception ex)
                {
                    usable = false;
                    reason = "探测异常(" + ex.GetType().Name + ")";
                }
                sb.Append(Path.GetFileName(py)).Append(": ").Append(reason).Append("；");
                if (usable)
                {
                    diag = sb.ToString();
                    _cachedPython = py;
                    return py;
                }
            }

            diag = sb.ToString();
            _cachedPython = "";
            return null;
        }

        /// <summary>
        /// 用 service_ticket 换取登录 Cookie。成功时 <c>Ok=true</c> 且 <c>Cookie</c> 可用。
        /// 没有任何可用 Python 时返回 <c>null</c>（调用方据此回退到纯 .NET 实现）。
        /// </summary>
        public static ExchangeOutcome? Exchange(string ticket)
        {
            if (string.IsNullOrWhiteSpace(ticket))
            {
                return new ExchangeOutcome { Ok = false, Error = "service_ticket 为空" };
            }

            string diag;
            string py = FindUsablePython(out diag);
            if (py == null)
                return null; // 明确告知调用方：这条路走不了

            string helper = EnsureHelperScript();
            string stdout, stderr;
            int exit = Run(py, helper, new[] { ticket }, 40, out stdout, out stderr);

            var result = ParseJson(stdout);
            if (result == null)
            {
                return new ExchangeOutcome
                {
                    Ok = false,
                    PythonPath = py,
                    Error = "Python 助手未返回可解析的结果",
                    Detail = Trim(stderr) + " | stdout: " + Trim(stdout),
                };
            }

            bool ok = GetBool(result, "ok");
            string cookie = GetString(result, "cookie");
            string nickname = GetString(result, "nickname");

            if (ok && !string.IsNullOrEmpty(cookie))
            {
                return new ExchangeOutcome
                {
                    Ok = true, Cookie = cookie, Nickname = nickname, PythonPath = py,
                };
            }

            string detail = GetString(result, "detail");
            if (string.IsNullOrEmpty(detail)) detail = Trim(stderr);
            return new ExchangeOutcome
            {
                Ok = false,
                Cookie = cookie,
                PythonPath = py,
                Error = GetString(result, "error") ?? "换取登录态失败",
                Detail = detail,
            };
        }

        // ---------------- 探测候选 ----------------

        /// <summary>
        /// 按优先级列出候选 Python：**先内置运行时**，再常见安装位置（新版优先），
        /// 再 PATH，最后 py launcher。
        ///
        /// <para>内置运行时（随程序分发的 CPython 3.13 + OpenSSL 3.5.7）排在第一位，
        /// 保证在**任何机器**上都能扫码登录，不依赖用户是否装了 Python；
        /// 内置不可用时（例如文件被删）才回退到本机 Python。</para>
        /// </summary>
        private static IEnumerable<string> EnumeratePythonCandidates()
        {
            return EnumeratePythonCandidatesCore();
        }

        private static List<string> EnumeratePythonCandidatesCore()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();

            Action<string> add = delegate(string p)
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                string full;
                try { full = Path.GetFullPath(p); } catch { return; }
                if (!File.Exists(full)) return;
                if (!seen.Add(full)) return;
                list.Add(full);
            };

            // 0) ★ 内置运行时（最高优先级）
            add(GetBundledPythonPath());

            // 1) 用户级常见安装位置（新版优先：OpenSSL 更新，更可能被放行）
            AddPythonFromRoot(
                SafeCombine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Programs", "Python"), add);

            // 2) 全局安装位置
            AddPythonFromRoot(@"C:\Program Files\Python", add);
            add(@"C:\Python314\python.exe");
            add(@"C:\Python313\python.exe");
            add(@"C:\Python312\python.exe");
            add(@"C:\Python311\python.exe");

            // 3) PATH 上的 python / python3
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var seg in pathEnv.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = seg.Trim().Trim('"');
                if (s.Length == 0) continue;
                add(Path.Combine(s, "python.exe"));
                add(Path.Combine(s, "python3.exe"));
            }

            // 4) Windows 自带的 py launcher（不能直接当解释器，但可以拿来枚举已安装版本）
            try
            {
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string pyLauncher = string.IsNullOrEmpty(winDir) ? null : Path.Combine(winDir, "py.exe");
                if (pyLauncher != null && File.Exists(pyLauncher))
                {
                    foreach (var v in EnumerateViaPyLauncher(pyLauncher))
                        add(v);
                }
            }
            catch { }

            return list;
        }

        /// <summary>扫描某个 "Python" 根目录下的各版本子目录，加入 python.exe（新版本排前）。</summary>
        private static void AddPythonFromRoot(string pyRoot, Action<string> add)
        {
            if (string.IsNullOrEmpty(pyRoot)) return;
            try
            {
                if (!Directory.Exists(pyRoot)) return;
                var dirs = new List<string>(Directory.GetDirectories(pyRoot));
                dirs.Sort(StringComparer.OrdinalIgnoreCase);
                dirs.Reverse(); // Python314 排在 Python310 前
                foreach (var d in dirs)
                    add(Path.Combine(d, "python.exe"));
            }
            catch { }
        }

        // ---------------- 内置运行时 ----------------

        /// <summary>内置运行时相对于程序目录的子路径。</summary>
        private const string BundledRelDir = @"Runtime\win-x64";

        /// <summary>
        /// 返回内置 Python 的 <c>python.exe</c> 绝对路径；不存在返回 <c>null</c>。
        ///
        /// <para>查找顺序：程序集所在目录 → 上一级 → 上两级。
        /// 之所以要往上找，是因为 <c>GeZi.Core.dll</c> 与 <c>GeZi.exe</c> 可能不在同一层
        /// （单文件/子目录部署时），以及调试时 exe 在 <c>bin\Debug\net48</c> 而运行时被复制到
        /// 其下方 <c>Runtime\win-x64</c>。</para>
        /// </summary>
        public static string GetBundledPythonPath()
        {
            foreach (var root in EnumerateAssemblyRoots())
            {
                try
                {
                    string p = Path.Combine(root, BundledRelDir, "python.exe");
                    if (File.Exists(p)) return Path.GetFullPath(p);
                }
                catch { }
            }
            return null;
        }

        /// <summary>枚举可能作为"程序根"的目录（程序集目录及其上两级，去重）。</summary>
        private static IEnumerable<string> EnumerateAssemblyRoots()
        {
            string baseDir = null;
            try
            {
                var asm = typeof(QuarkPythonBridge).Assembly;
                string loc = asm.Location;
                if (!string.IsNullOrEmpty(loc)) baseDir = Path.GetDirectoryName(loc);
            }
            catch { }

            if (string.IsNullOrEmpty(baseDir))
            {
                try { baseDir = AppDomain.CurrentDomain.BaseDirectory; } catch { }
            }
            if (string.IsNullOrEmpty(baseDir)) yield break;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cur = new DirectoryInfo(baseDir);
            for (int i = 0; i < 3 && cur != null; i++)
            {
                if (seen.Add(cur.FullName)) yield return cur.FullName;
                cur = cur.Parent;
            }
        }

        private static string SafeCombine(string root, params string[] parts)
        {
            if (string.IsNullOrEmpty(root)) return null;
            string p = root;
            foreach (var seg in parts) p = Path.Combine(p, seg);
            return p;
        }

        /// <summary>用 py launcher 列出所有已安装解释器的路径。</summary>
        private static IEnumerable<string> EnumerateViaPyLauncher(string pyExe)
        {
            var result = new List<string>();
            string stdout, stderr;
            // py -0p 打印每个已安装版本及其路径
            int exit;
            try
            {
                exit = Run(pyExe, null, new[] { "-0p" }, 10, out stdout, out stderr);
            }
            catch
            {
                return result;
            }
            if (exit != 0 || string.IsNullOrEmpty(stdout)) return result;
            foreach (var raw in stdout.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                int idx = line.IndexOf(":\\", StringComparison.Ordinal);
                if (idx < 1) continue;
                int start = idx - 1;
                while (start > 0 && line[start - 1] != ' ' && line[start - 1] != '\t') start--;
                string p = line.Substring(start).Trim();
                if (p.EndsWith("python.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
                    result.Add(p);
            }
            return result;
        }

        // ---------------- 执行 ----------------

        /// <summary>用候选解释器跑 --selftest，判断它能否连上 pan.quark.cn。</summary>
        private static bool Probe(string pyExe, string helper, out string reason)
        {
            string stdout, stderr;
            int exit;
            try
            {
                exit = Run(pyExe, helper, new[] { SelftestArg }, 20, out stdout, out stderr);
            }
            catch (Exception ex)
            {
                reason = "启动失败(" + ex.GetType().Name + ")";
                return false;
            }

            var obj = ParseJson(stdout);
            if (obj == null)
            {
                // 带上 exit code、stdout/stderr 长度与片段，便于定位"到底跑没跑起来"。
                string snippet = string.IsNullOrEmpty(stderr) ? "" : (" stderr=" + Trim(stderr));
                reason = "自检无输出(exit=" + exit + ", outLen=" + (stdout == null ? -1 : stdout.Length)
                    + ", errLen=" + (stderr == null ? -1 : stderr.Length) + ")" + snippet;
                return false;
            }

            string sslVer = GetString(obj, "openssl") ?? "?";
            if (GetBool(obj, "can_exchange"))
            {
                reason = "可用 (" + sslVer + ")";
                return true;
            }

            // 自检失败：说清是 OpenSSL 太老还是别的
            bool panFail = false;
            var reach = GetDict(obj, "reachable");
            if (reach != null && reach.TryGetValue("pan.quark.cn", out var v))
            {
                string vs = v as string;
                if (vs != null && vs.StartsWith("FAIL", StringComparison.Ordinal)) panFail = true;
            }
            reason = panFail
                ? "连不上 pan.quark.cn (" + sslVer + "，版本偏旧)"
                : "自检未通过 (" + sslVer + ")";
            return false;
        }

        /// <summary>
        /// 运行进程并捕获 stdout/stderr（UTF-8，无 BOM 干扰）。
        /// </summary>
        private static int Run(string exe, string script, string[] args, int timeoutSec,
            out string stdout, out string stderr)
        {
            ScrubDuplicateEnvVars();

            // 工作目录：内置运行时就切到它自己的目录（便于相对定位 python313.zip、
            // DLLs 等）；否则用 %TEMP%（避免用户当前目录被写脏）。
            string workDir = Path.GetTempPath();
            bool isBundled = false;
            try
            {
                string bundled = GetBundledPythonPath();
                if (bundled != null &&
                    string.Equals(Path.GetFullPath(bundled), Path.GetFullPath(exe),
                                  StringComparison.OrdinalIgnoreCase))
                {
                    workDir = Path.GetDirectoryName(bundled);
                    isBundled = true;
                }
            }
            catch { }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workDir,
            };
            var argsSb = new StringBuilder();
            if (script != null) argsSb.Append(Quote(script));
            if (args != null)
            {
                foreach (var a in args)
                {
                    if (argsSb.Length > 0) argsSb.Append(' ');
                    argsSb.Append(Quote(a));
                }
            }
            psi.Arguments = argsSb.ToString();

            // 环境变量：加 UTF-8 提示、去掉代理。
            // （ScrubDuplicateEnvVars 已在最前面清掉了大小写重复项，这里才能安全访问字典。）
            SafeSetEnv(psi, "PYTHONIOENCODING", "utf-8");
            SafeSetEnv(psi, "PYTHONUTF8", "1");

            if (isBundled)
            {
                // 内置运行时是"可重定位"的：用户机器上的 PYTHONHOME/PYTHONPATH
                // 会把它指到别的地方，导致找不到标准库。必须清掉。
                SafeSetEnv(psi, "PYTHONHOME", null);
                SafeSetEnv(psi, "PYTHONPATH", null);
                SafeSetEnv(psi, "PYTHONSTARTUP", null);
                // 禁止加载用户级 site-packages（内置运行时也没有）。
                SafeSetEnv(psi, "PYTHONNOUSERSITE", "1");
            }

            using (var p = new Process { StartInfo = psi })
            {
                p.Start();
                // 同步整块读取。之前用 BeginOutputReadLine + WaitForExit 的异步方式，
                // 在 WaitForExit 返回时输出事件回调可能尚未跑完，导致 stdout 恒为空
                // （表现为"自检无输出"）。助手脚本输出量极小（单行 JSON），
                // 同步 ReadToEnd 既可靠又不会死锁（stderr 单独收集，缓冲不会写满）。
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutSec * 1000))
                {
                    try { p.Kill(); } catch { }
                    throw new TimeoutException("Python 助手超时(" + timeoutSec + "s)");
                }
                stdout = (outTask.Result ?? "").Trim();
                stderr = (errTask.Result ?? "").Trim();
                return p.ExitCode;
            }
        }

        /// <summary>
        /// 清理当前进程环境里的代理变量（含"仅大小写不同"的重复项）。
        ///
        /// <para>【为什么必须做这一步】</para>
        /// <para>
        /// 某些运行环境会同时注入大小写两套代理变量。实测（沙箱环境）：
        /// <c>HTTP_PROXY</c> + <c>http_proxy</c> + <c>HTTPS_PROXY</c> + <c>https_proxy</c>
        /// 四条，且注册表里查不到（是运行时注入的）。.NET 在**构造**
        /// <c>ProcessStartInfo.Environment</c> / <c>EnvironmentVariables</c> 字典时用的比较器
        /// 大小写不敏感，遇到这种重复键会直接抛
        /// <c>ArgumentException: 已添加项。字典中的关键字:"HTTPS_PROXY"所添加的关键字:"https_proxy"</c>。
        /// 致命之处在于该异常连 <c>ToString()</c> 都会二次失败，日志里只剩
        /// "由于 Exception.ToString() 失败，因此无法打印异常字符串"，极难定位。
        /// </para>
        /// <para>
        /// 【为什么不能"删一次就好"】
        /// Windows 的 <c>Environment.GetEnvironmentVariable</c> / <c>SetEnvironmentVariable</c>
        /// 都是**大小写不敏感**的，一次 <c>SetEnvironmentVariable(name, null)</c> 只能移除其中
        /// 一条（实测：删两次才真正干净）。因此这里**循环删到读不出来为止**，
        /// 每个名字最多尝试若干次，确保大小写两份都被清掉。
        /// </para>
        /// <para>
        /// 删除只作用于**本进程**（<c>EnvironmentVariableTarget.Process</c>），不改系统环境；
        /// 同时顺带消除了代理对本地/直连请求的劫持。
        /// </para>
        /// </summary>
        private static void ScrubDuplicateEnvVars()
        {
            ScrubOne("HTTP_PROXY");
            ScrubOne("HTTPS_PROXY");
            ScrubOne("ALL_PROXY");
            ScrubOne("NO_PROXY");
        }

        /// <summary>循环删除某个环境变量，直到它彻底读不出来（大小写两份都清掉）。</summary>
        private static void ScrubOne(string name)
        {
            for (int i = 0; i < 8; i++)
            {
                string cur;
                try { cur = Environment.GetEnvironmentVariable(name); }
                catch { return; }
                if (cur == null) return;
                try { Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process); }
                catch { return; }
            }
        }

        private static string Quote(string s)
        {
            return "\"" + (s ?? "").Replace("\"", "\\\"") + "\"";
        }

        /// <summary>安全地给子进程设置环境变量（字典访问失败不应中断流程）。</summary>
        private static void SafeSetEnv(ProcessStartInfo psi, string key, string value)
        {
            try { psi.Environment[key] = value; }
            catch { }
        }

        // ---------------- 嵌入资源释放 ----------------

        /// <summary>
        /// 取得可用的辅助脚本路径。
        ///
        /// <para>【两种来源，优先级从高到低】</para>
        /// <list type="number">
        /// <item>内置运行时目录里的 <c>quark_cookie_helper.py</c> —— 与内置 Python 配套，
        ///   路径固定、无需释放；</item>
        /// <item>嵌入资源释放到 <c>%TEMP%\GeZi\</c> —— 供"本机已有 Python"的路径使用。</item>
        /// </list>
        /// </summary>
        public static string EnsureHelperScript()
        {
            // 1) 内置运行时里的脚本
            string bundledPy = GetBundledPythonPath();
            if (bundledPy != null)
            {
                try
                {
                    string p = Path.Combine(Path.GetDirectoryName(bundledPy), HelperFileName);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }

            // 2) 释放嵌入资源到 %TEMP%
            string dir = Path.Combine(Path.GetTempPath(), "GeZi");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, HelperFileName);

            var asm = typeof(QuarkPythonBridge).Assembly;
            using (var res = asm.GetManifestResourceStream(ResourceName))
            {
                if (res == null)
                    throw new InvalidOperationException("找不到嵌入资源: " + ResourceName);

                // 已存在且内容一致则跳过（用长度做快速判断，足够）
                if (File.Exists(path) && new FileInfo(path).Length == res.Length)
                    return path;

                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    res.CopyTo(fs);
                }
            }
            return path;
        }

        // ---------------- JSON 小工具 ----------------

        private static Dictionary<string, object> ParseJson(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            try
            {
                // stdout 可能混有额外行，需截出完整的 JSON 对象。
                // 注意：必须取**第一个** '{' 到**最后一个** '}'（不能取最后一个 '{'）——
                // 助手输出里含嵌套对象（如 "reachable": {...}），用最后一个 '{' 会把
                // 内层对象当成整个 JSON，导致解析失败。
                int start = s.IndexOf('{');
                int end = s.LastIndexOf('}');
                if (start < 0 || end <= start) return null;
                string json = s.Substring(start, end - start + 1);
                var obj = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                    .DeserializeObject(json);
                return obj as Dictionary<string, object>;
            }
            catch
            {
                return null;
            }
        }

        private static string GetString(Dictionary<string, object> d, string key)
        {
            if (d != null && d.TryGetValue(key, out var v) && v is string s) return s;
            return null;
        }

        private static bool GetBool(Dictionary<string, object> d, string key)
        {
            if (d != null && d.TryGetValue(key, out var v))
            {
                if (v is bool b) return b;
                if (v is string s) return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
        {
            if (d != null && d.TryGetValue(key, out var v)) return v as Dictionary<string, object>;
            return null;
        }

        private static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 300 ? s.Substring(0, 300) + "…" : s;
        }
    }
}
