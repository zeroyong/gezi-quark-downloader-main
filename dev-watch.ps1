# dev-watch.ps1 - GeZi dev loop: rebuild + relaunch on save.
#
# Usage (run it yourself, leave the window open):
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\dev-watch.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\dev-watch.ps1 -Once   # build+run once, then exit
#
# Stop with Ctrl+C.
#
# Notes:
#  * ASCII-only on purpose: Windows PowerShell 5.1 reads .ps1 as ANSI, so UTF-8
#    Chinese literals in this file would be mangled and break the parser.
#  * Only the Debug build under src\GeZi\bin\Debug\net48 is killed/restarted;
#    a release build running from somewhere else is left alone.
#  * The SDK is the one at C:\Users\zsm\.dotnet (the dotnet on PATH has no SDK).

param([switch]$Once)

$ErrorActionPreference = 'Stop'

$root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj   = Join-Path $root 'src\GeZi\GeZi.csproj'
$outDir = Join-Path $root 'src\GeZi\bin\Debug\net48'
$exe    = Join-Path $outDir 'GeZi.exe'
$watch  = Join-Path $root 'src'

$dotnet = 'C:\Users\zsm\.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

# Bring the app window to the foreground after (re)start.
# Without this the freshly started app can sit behind the console / other windows,
# which looks like "BUILD OK but no app window".
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class WatchWin {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
}
"@

function Focus-App {
    for ($i = 0; $i -lt 25; $i++) {
        $p = $null
        try {
            $p = Get-Process -Name GeZi -ErrorAction SilentlyContinue |
                 Where-Object { $_.MainWindowHandle -ne 0 -and $_.Path -eq $exe } |
                 Select-Object -First 1
        } catch { }
        if ($p -ne $null) {
            try {
                [void][WatchWin]::ShowWindow($p.MainWindowHandle, 9)   # SW_RESTORE
                [void][WatchWin]::SetForegroundWindow($p.MainWindowHandle)
            } catch { }
            return
        }
        Start-Sleep -Milliseconds 300
    }
}

function Get-Stamp {
    Get-ChildItem $watch -Recurse -File -Include *.cs,*.xaml,*.csproj -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        Sort-Object FullName |
        ForEach-Object { $_.FullName + '|' + $_.LastWriteTimeUtc.Ticks }
}

function Stop-App {
    try {
        Get-Process -Name GeZi -ErrorAction SilentlyContinue | ForEach-Object {
            $p = $_
            try {
                if ($p.Path -eq $exe) {
                    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
                    try { $p.WaitForExit(3000) | Out-Null } catch { }
                }
            } catch { }
        }
    } catch { }
}

function Build-And-Run {
    $ts = Get-Date -Format 'HH:mm:ss'
    Write-Host "[$ts] building..." -ForegroundColor Cyan
    Stop-App
    Start-Sleep -Milliseconds 250

    # 【2026-11-02】只把 error 打到控制台。
    # 原来 -v minimal 会把 10 条 NU1900/CS0649 警告也刷出来，
    # 连续改界面时满屏都是警告，真正有用的错误反而被淹没。
    #
    # ⚠️ 成败判定不能依赖 $LASTEXITCODE：
    #    `& $dotnet ... 2>&1` 走管道后它在某些情况下不可靠（实测"成功却判失败"）。
    # ⚠️ 也不能用中文标记判断（如 "已成功生成"）：PowerShell 5.1 按 ANSI 读本脚本，
    #    UTF-8 中文字面量会乱码导致语法错误（实测踩到）。
    #    改用"有没有 error 行" + "有没有输出 exe 路径"两个纯 ASCII 判据。
    $log = & $dotnet build $proj -c Debug --nologo -v minimal 2>&1
    $logText = ($log | Out-String)

    $hasError = $logText -match 'error [A-Z]{2,}\d+'

    # [2026-11-10 fix] Decide success by OBJECTIVE FACTS, not by parsing the log.
    #
    # The old check was `$logText -match 'GeZi\.exe'`. In a Chinese locale dotnet
    # localizes its output, so that path line can be wrapped or mangled and the
    # regex silently misses -> a successful build prints "BUILD FAILED" and the
    # app never restarts. Symptom: "I changed the code and nothing happened".
    #
    # New rule: success == no compiler error AND GeZi.exe exists.
    # (Incremental builds may not relink, so mtime alone is not reliable.)
    $exeOk = (Test-Path $exe)
    $failed = $hasError -or (-not $exeOk)

    if ($failed) {
        $log | Where-Object { $_ -match 'error [A-Z]{2,}\d+' } | ForEach-Object {
            Write-Host "  $_" -ForegroundColor Red
        }
        if (-not $hasError) {
            Write-Host "  (no compiler errors, but the exe is missing - is GeZi.exe still running?)" -ForegroundColor Yellow
        }
        Write-Host "[$ts] BUILD FAILED - fix the error and save again" -ForegroundColor Red
        return
    }

    for ($i = 0; $i -lt 10; $i++) {
        try {
            Start-Process -FilePath $exe -WorkingDirectory $outDir
            Focus-App
            Write-Host "[$ts] BUILD OK - app restarted" -ForegroundColor Green
            return
        } catch {
            Start-Sleep -Milliseconds 300
        }
    }
    Write-Host "[$ts] BUILD OK but could not start the app (file still locked?)" -ForegroundColor Yellow
}

Write-Host 'GeZi dev watch'
Write-Host "  project : $proj"
Write-Host "  dotnet  : $dotnet"
Write-Host "  watching: $watch  (*.cs  *.xaml  *.csproj)"
Write-Host '  Ctrl+C to stop'
Write-Host ''

$last = Get-Stamp
Build-And-Run
if ($Once) { exit 0 }

while ($true) {
    Start-Sleep -Milliseconds 800
    $now = Get-Stamp
    if (Compare-Object $last $now -SyncWindow 0) {
        # coalesce a burst of saves (multi-file edits) into one rebuild
        Start-Sleep -Milliseconds 500
        $last = Get-Stamp
        Build-And-Run
    }
}
