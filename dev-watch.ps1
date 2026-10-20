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

    & $dotnet build $proj -c Debug --nologo -v minimal
    if ($LASTEXITCODE -ne 0) {
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
