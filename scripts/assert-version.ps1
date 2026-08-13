# Verifies that the pushed tag (GITHUB_REF_NAME, e.g. v1.4.0) matches the
# project version declared in Directory.Build.props. Run by CI before creating
# a GitHub Release so a mis-tagged build never publishes.
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$tag = $env:GITHUB_REF_NAME
if ([string]::IsNullOrWhiteSpace($tag)) {
    Write-Error "GITHUB_REF_NAME is not set; this script only runs on tag builds."
}

$props = Join-Path $root "Directory.Build.props"
$version = [regex]::Match((Get-Content -Raw -LiteralPath $props), '<Version>([^<]+)</Version>').Groups[1].Value
if ([string]::IsNullOrWhiteSpace($version)) {
    Write-Error "No <Version> found in Directory.Build.props."
}

if ($tag -ne "v$version") {
    Write-Error "Tag '$tag' does not match project version 'v$version'; update Directory.Build.props before tagging."
}

Write-Host "Tag '$tag' matches Directory.Build.props version '$version'."
