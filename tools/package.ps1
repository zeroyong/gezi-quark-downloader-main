# Package GeZi into dist/GeZi-vX.Y.Z-win64.zip (as small as possible).
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\package.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\package.ps1 -Version 2.8.1
#
# Size strategy:
#   1) ship ONLY runtime-required files (whitelist). No .pdb, no XML docs,
#      and never the account/config files (they hold the user's login cookie).
#   2) zip with .NET ZipArchive Optimal (max deflate).
#   Measured: 10.6 MB raw -> 3.22 MB.
#
#   libcrypto-3-x64.dll (7.8 MB -> ~2.0 MB) dominates the size but is REQUIRED:
#   quark's pan.quark.cn filters by TLS ClientHello fingerprint, and only
#   OpenSSL 3.5.7 passes that wall. Without it, QR-code login is impossible.
#
# NOTE: the script deliberately does NOT put GeZi.config.xml / GeZi.state.json /
#       accounts\ into the zip. Those carry the user's session cookie and
#       history; shipping them would leak the login state to whoever gets
#       the package. Copy them over manually if you want a portable profile.
#
# 【2026-10-22】更进一步：连**暂存目录里都不放**它们了。数据目录已固定为
#       %LOCALAPPDATA%\鸽子下载\（Debug / 发行包 / 任何副本共享同一份），
#       以前把 Debug 的 state.json 拷进发行包，导致发行包里躺着一份
#       "打包那一刻的旧快照" —— 用户实测表现为"重启后任务不见了"
#       （下载是发行包发起的，记录却只在 Debug 那份文件里）。

param(
    [string]$Version = "",
    [string]$OutDir  = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$projRoot = Split-Path -Parent $root

# ---------- 1. Release build ----------
$bin = Join-Path $projRoot 'src\GeZi\bin\Release\net48'
Write-Host "[1/4] Release build ..." -ForegroundColor Cyan
$dotnet = 'C:\Users\zsm\.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
& $dotnet build (Join-Path $projRoot 'src\GeZi\GeZi.csproj') -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "build failed" }

# ---------- 2. Collect files ----------
if (-not $Version) {
    $asm = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $bin 'GeZi.exe'))
    $Version = $asm.Version.ToString(3)
}
if (-not $OutDir) { $OutDir = Join-Path $projRoot 'dist' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$stageName = "GeZi-v$Version-win64"
$stage = Join-Path $OutDir $stageName

# A running GeZi.exe holds its own dlls open, so the folder cannot be
# wiped. Detect that early and say so instead of failing halfway.
if (Test-Path $stage) {
    $locked = Get-Process -Name GeZi -ErrorAction SilentlyContinue |
              Where-Object { $_.Path -and $_.Path.StartsWith($stage, [StringComparison]::OrdinalIgnoreCase) }
    if ($locked) {
        Write-Host "      stop the running GeZi first (pid: $($locked.Id -join ', '))" -ForegroundColor Yellow
        throw "a GeZi instance is running from $stage - close it and retry"
    }
}

# Keep the user's profile (login cookie / history) across repacks.
# These live next to the exe in the staged folder and would otherwise be
# wiped when the folder is recreated below. Copy them to a scratch folder
# FIRST, because the originals disappear with the staged folder.
#
# 【2026-10-22】数据目录已固定为 %LOCALAPPDATA%\鸽子下载\（见 SettingsStore.ResolveFilePath），
# 所以**发行包目录里不再需要任何 profile 文件** —— 删掉这一整段"保留 profile"的逻辑：
#   * 保留它会把**打包那一刻的旧快照**带进发行包（用户实测：发行包里那份 state.json
#     是打包时的旧副本，里面没有后来下载的任务 → "重启后任务不见了"）；
#   * GeZi.config.xml 里是 DPAPI 加密的登录 Cookie，随包分发属于隐私事故。
# 现在发行包是**干净的**：第一次启动会在统一目录里新建配置，用户自己登录即可。
$profileFiles = @()
$profileScratch = $null

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# Whitelist: every one of these is required to run.
$mustHave = @(
    'GeZi.exe',
    'GeZi.exe.config',    # Server GC -> CPU usage under high concurrency
    'GeZi.Core.dll',
    'zxing.dll',          # QR-code decoding
    'libcrypto-3-x64.dll',# OpenSSL -> the only way past quark's TLS filter
    'libssl-3-x64.dll',
    'vcruntime140.dll',
    'vcruntime140_1.dll'
)

Write-Host "[2/4] Collect files ..." -ForegroundColor Cyan
foreach ($f in $mustHave) {
    $src = Join-Path $bin $f
    if (-not (Test-Path $src)) { throw "missing required file: $f" }
    Copy-Item $src $stage
}

# Two different kinds of files live in the staged folder:
#   * program files  -> go into the zip
#   * user data      -> must NEVER go into the zip (and now must not even
#                       be staged): GeZi.config.xml holds the DPAPI-encrypted
#                       session cookie, GeZi.state.json holds the task list /
#                       history. Shipping them leaks the login state and
#                       injects a stale snapshot into the fresh install.
#                       Since 2026-10-22 the app stores all of it under
#                       %LOCALAPPDATA%\鸽子下载\, so the staged folder needs
#                       none of these.
$userDataNames = @('GeZi.config.xml', 'GeZi.state.json', 'GeZi.state.json.bak',
                   'GeZi.config.xml.bak', 'GeZi.state.json.tmp', 'GeZi.config.xml.tmp')

# Safety net: drop anything that is not on the program whitelist
# (e.g. .pdb, stray .xml, leftover logs, and any user data).
$keep = $mustHave | ForEach-Object { $_.ToLowerInvariant() }
Get-ChildItem $stage -File | Where-Object {
    $n = $_.Name.ToLowerInvariant()
    ($keep -notcontains $n) -and ($userDataNames -notcontains $n)
} | ForEach-Object {
    Write-Host "      excluded $($_.Name)" -ForegroundColor DarkGray
    Remove-Item $_.FullName -Force
}
Get-ChildItem $stage -Directory | Where-Object { $_.Name -eq 'accounts' } | ForEach-Object {
    Write-Host "      excluded accounts\" -ForegroundColor DarkGray
    Remove-Item $_.FullName -Recurse -Force
}

# ---------- 3. Compress ----------
# Zip only the program files. Build a temp folder with just those, so the
# profile never ends up inside the package.
Write-Host "[3/4] Compress ..." -ForegroundColor Cyan
$zipPath = Join-Path $OutDir "$stageName.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

$zipStage = Join-Path $env:TEMP ("gezi_zip_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $zipStage | Out-Null
try {
    foreach ($f in $mustHave) {
        Copy-Item (Join-Path $stage $f) $zipStage -Force
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $zipStage, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
} finally {
    Remove-Item $zipStage -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------- 4. Report ----------
$zipMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
$rawMB = [math]::Round(((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 2)
Write-Host "[4/4] Done" -ForegroundColor Green
Write-Host "      version   : v$Version"
Write-Host "      raw size  : $rawMB MB"
Write-Host "      zipped    : $zipMB MB"
Write-Host "      output    : $zipPath"
Write-Host ""
Write-Host "      package contents:"
Get-ChildItem $stage -File | Sort-Object Name | ForEach-Object {
    Write-Host ("        {0,10:N0} KB  {1}" -f ($_.Length/1KB), $_.Name)
}
Write-Host ""
Write-Host "      NOTE: user data is NOT in the package. Config / accounts / task"
Write-Host "            history live in %LOCALAPPDATA%\鸽子下载\ and are created"
Write-Host "            on first run (log in once and they are kept there)."
