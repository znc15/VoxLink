# Downloads and extracts the portable Inno Setup compiler for local builds.
# Requires no administrator rights; used by publish.ps1 and GitHub Actions CI.
# Keep this file ASCII-only: Windows PowerShell 5.1 reads BOM-less files as ANSI.
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$toolsDir = Join-Path $root "scripts\tools"
$innoDir = Join-Path $toolsDir "InnoSetup"
$isccPath = Join-Path $innoDir "iscc.exe"
$installerPath = Join-Path $toolsDir "innosetup-installer.exe"

if (Test-Path -LiteralPath $isccPath) {
    Write-Output "Inno Setup compiler already present: $isccPath"
    exit 0
}

New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null

# jrsoftware.org now hosts the installer on GitHub Releases (issrc repository).
$url = "https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe"
Write-Output "Downloading Inno Setup from $url ..."
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri $url -OutFile $installerPath -UseBasicParsing

$bytes = [System.IO.File]::ReadAllBytes($installerPath)
if ($bytes.Length -lt 2 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
    Remove-Item -LiteralPath $installerPath -Force -ErrorAction SilentlyContinue
    throw "Downloaded file is not a Windows executable (MZ header missing)."
}

Write-Output "Extracting portable Inno Setup to $innoDir ..."
$process = Start-Process -FilePath $installerPath -ArgumentList @(
    "/VERYSILENT",
    "/SUPPRESSMSGBOXES",
    "/NORESTART",
    "/PORTABLE=1",
    "/DIR=$innoDir"
) -Wait -PassThru
if ($process.ExitCode -ne 0) {
    Remove-Item -LiteralPath $installerPath -Force -ErrorAction SilentlyContinue
    throw "Inno Setup installer exited with code $($process.ExitCode)."
}

Remove-Item -LiteralPath $installerPath -Force
if (-not (Test-Path -LiteralPath $isccPath)) {
    throw "iscc.exe was not created at $innoDir. The download may have been blocked, or this Inno Setup version does not support /PORTABLE."
}
Write-Output "Inno Setup ready: $isccPath"
