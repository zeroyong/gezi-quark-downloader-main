# create-release.ps1 - GeZi 2.9.0 GitHub Release
# ASCII-only on purpose (PowerShell 5.1 reads .ps1 as ANSI).
# Reads the GitHub token from Windows Credential Manager (never hardcoded),
# creates the release, uploads the zip as an asset, and verifies both.

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class CredReader {
  [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  public static extern bool CredReadW(string target, int type, int flags, out IntPtr cred);
  [StructLayout(LayoutKind.Sequential)]
  public struct CREDENTIAL {
    public int Flags; public int Type; public IntPtr TargetName; public IntPtr Comment;
    public long LastWritten; public int CredentialBlobSize; public IntPtr CredentialBlob;
    public int Persist; public int AttributeCount; public IntPtr Attributes;
    public IntPtr TargetAlias; public IntPtr UserName;
  }
  public static string Get(string target) {
    IntPtr p;
    if (!CredReadW(target, 1, 0, out p)) return null;
    var c = (CREDENTIAL)Marshal.PtrToStructure(p, typeof(CREDENTIAL));
    return Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2);
  }
}
'@

$repo  = 'zeroyong/gezi-quark-downloader-main'
$tag   = $args[0]
$zip   = $args[1]
$notes = $args[2]

if (-not $tag)   { throw 'usage: create-release.ps1 <sha> <zipPath> <notesFile>' }
if (-not (Test-Path $zip))   { throw "zip not found: $zip" }
if (-not (Test-Path $notes)) { throw "notes not found: $notes" }

$tok = [CredReader]::Get('git:https://github.com')
if (-not $tok) { throw 'GitHub token not found in Credential Manager' }
Write-Host ("token: len=" + $tok.Length)

$headers = @{
    Authorization          = "token $tok"
    Accept                 = 'application/vnd.github+json'
    'User-Agent'           = 'GeZi-Release-Script'
}

# UTF-8 body is mandatory: Chinese notes turn into garbage otherwise.
$bodyText = [System.IO.File]::ReadAllText($notes, [System.Text.Encoding]::UTF8)
$payload  = @{
    tag_name         = $tag
    target_commitish = 'master'
    name             = 'GeZi v2.9.0'
    body             = $bodyText
    draft            = $false
    prerelease       = $false
} | ConvertTo-Json -Depth 5

# ConvertTo-Json escapes non-ASCII as \uXXXX, which is valid JSON and safe.
$bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)

Write-Host 'creating release...'
try {
    $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases" `
        -Method Post -Headers $headers -Body $bytes -ContentType 'application/json; charset=utf-8' `
        -Proxy 'http://127.0.0.1:7897'
} catch {
    $msg = $_.Exception.Message
    if ($_.ErrorDetails) { $msg = $_.ErrorDetails.Message }
    throw "create release failed: $msg"
}

$uploadUrl = $rel.upload_url -replace '\{.*\}', ''
Write-Host ("release created: " + $rel.html_url)
Write-Host ("upload_url: " + $uploadUrl)

# Upload the zip
$name = [System.IO.Path]::GetFileName($zip)
$upHeaders = @{
    Authorization  = "token $tok"
    Accept         = 'application/vnd.github+json'
    'User-Agent'   = 'GeZi-Release-Script'
}
Write-Host ("uploading " + $name + " ...")
$data = [System.IO.File]::ReadAllBytes($zip)
try {
    $asset = Invoke-RestMethod -Uri ($uploadUrl + '?name=' + [uri]::EscapeDataString($name)) `
        -Method Post -Headers $upHeaders -Body $data `
        -ContentType 'application/octet-stream' -TimeoutSec 600 `
        -Proxy 'http://127.0.0.1:7897'
} catch {
    $msg = $_.Exception.Message
    if ($_.ErrorDetails) { $msg = $_.ErrorDetails.Message }
    throw "upload failed: $msg"
}
Write-Host ("asset uploaded: " + $asset.name + "  " + $asset.size + " bytes")
Write-Host ("download: " + $asset.browser_download_url)

# ---- verify by reading back ----
Write-Host ''
Write-Host '=== VERIFY ==='
$chk = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/tags/$tag" `
    -Headers $headers -Proxy 'http://127.0.0.1:7897'
Write-Host ("tag         : " + $chk.tag_name)
Write-Host ("name        : " + $chk.name)
Write-Host ("draft       : " + $chk.draft)
Write-Host ("assets      : " + $chk.assets.Count)
foreach ($a in $chk.assets) {
    Write-Host ("  - " + $a.name + " (" + $a.size + " bytes)")
}
Write-Host ("body length : " + $chk.body.Length)
Write-Host ("html_url    : " + $chk.html_url)
