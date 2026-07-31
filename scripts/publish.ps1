$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$uiProject = Join-Path $root "src\VoxLink.UI\VoxLink.UI.csproj"
$engineProject = Join-Path $root "src\VoxLink.Engine\VoxLink.Engine.csproj"
$openVrLicense = Join-Path $root "src\VoxLink\ThirdParty\OpenVR\LICENSE.txt"
$sherpaLicense = Join-Path $root "src\VoxLink\ThirdParty\SherpaOnnx\LICENSE.txt"
$sherpaReadme = Join-Path $root "src\VoxLink\ThirdParty\SherpaOnnx\README.md"
$onnxRuntimeLicense = Join-Path $root "src\VoxLink\ThirdParty\SherpaOnnx\ONNXRUNTIME-LICENSE.txt"
$onnxRuntimeNotices = Join-Path $root "src\VoxLink\ThirdParty\SherpaOnnx\ONNXRUNTIME-THIRD-PARTY-NOTICES.txt"
$releaseRoot = Join-Path $root "artifacts\release"
$publishDir = Join-Path $releaseRoot "VoxLink-win-x64"
$engineDir = Join-Path $publishDir "engine"
$uiStage = Join-Path $releaseRoot ".ui-win-x64"
$engineStage = Join-Path $releaseRoot ".engine-win-x64"
$archivePath = Join-Path $releaseRoot "VoxLink-win-x64.zip"
$hashPath = "$archivePath.sha256"
$installerScript = Join-Path $root "scripts\installer.iss"

function Invoke-DotNetPublish {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Project,
        [Parameter(Mandatory = $true)]
        [string]$Output,
        [switch]$WindowsAppSdk
    )

    $arguments = @(
        "publish",
        $Project,
        "--configuration", "Release",
        "--runtime", "win-x64",
        "--self-contained", "true",
        "--output", $Output,
        "-p:DebugType=None",
        "-p:DebugSymbols=false"
    )
    if ($WindowsAppSdk) {
        $arguments += "-p:WindowsAppSDKSelfContained=true"
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Project with exit code $LASTEXITCODE"
    }
}

function Assert-FileExists {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Release is missing required file: $Path"
    }
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Force |
        Copy-Item -Destination $Destination -Recurse -Force
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $hashBytes = $sha256.ComputeHash($stream)
        return ([System.BitConverter]::ToString($hashBytes) -replace "-", "").ToLowerInvariant()
    }
    finally {
        $stream.Dispose()
        $sha256.Dispose()
    }
}

function Write-Sha256Sidecar {
    param([Parameter(Mandatory = $true)][string]$FilePath)
    $hash = Get-Sha256Hex $FilePath
    $name = Split-Path -Leaf $FilePath
    [System.IO.File]::WriteAllText(
        "$FilePath.sha256",
        "$hash  $name`n",
        [System.Text.Encoding]::ASCII)
    return $hash
}

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
foreach ($path in @($publishDir, $uiStage, $engineStage, $archivePath, $hashPath)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
Get-ChildItem -LiteralPath $releaseRoot -Filter "Setup-VoxLink-*.exe*" -ErrorAction SilentlyContinue |
    Remove-Item -Force

Invoke-DotNetPublish -Project $uiProject -Output $uiStage -WindowsAppSdk
Invoke-DotNetPublish -Project $engineProject -Output $engineStage

Copy-DirectoryContents -Source $uiStage -Destination $publishDir
Copy-DirectoryContents -Source $engineStage -Destination $engineDir

$unsupportedWorkloadsResource = Join-Path $publishDir "Microsoft.Windows.Workloads.Resources_ec.dll"
if (Test-Path -LiteralPath $unsupportedWorkloadsResource) {
    Remove-Item -LiteralPath $unsupportedWorkloadsResource -Force
}

foreach ($legacyFile in @(
    "VoxLink.exe",
    "VoxLink.deps.json",
    "VoxLink.runtimeconfig.json",
    "VoxLink.pdb",
    "VoxLink.Engine.pdb"
)) {
    $legacyPath = Join-Path $engineDir $legacyFile
    if (Test-Path -LiteralPath $legacyPath) {
        Remove-Item -LiteralPath $legacyPath -Force
    }
}

foreach ($unsupportedRuntime in @("win-arm64", "win-x86")) {
    $unsupportedRuntimePath = Join-Path $engineDir "runtimes\$unsupportedRuntime"
    if (Test-Path -LiteralPath $unsupportedRuntimePath) {
        Remove-Item -LiteralPath $unsupportedRuntimePath -Recurse -Force
    }
}

$dotnetCommand = Get-Command dotnet -ErrorAction Stop
$dotnetRoot = Split-Path -Parent $dotnetCommand.Source
$dotnetLicense = Join-Path $dotnetRoot "LICENSE.txt"
$dotnetNotices = Join-Path $dotnetRoot "ThirdPartyNotices.txt"
Assert-FileExists $dotnetLicense
Assert-FileExists $dotnetNotices
foreach ($destination in @($publishDir, $engineDir)) {
    Copy-Item -LiteralPath $dotnetLicense -Destination (Join-Path $destination "DOTNET-LICENSE.txt")
    Copy-Item -LiteralPath $dotnetNotices -Destination (Join-Path $destination "DOTNET-THIRD-PARTY-NOTICES.txt")
}

foreach ($thirdPartyLicense in @(
    $openVrLicense,
    $sherpaLicense,
    $sherpaReadme,
    $onnxRuntimeLicense,
    $onnxRuntimeNotices
) ) {
    Assert-FileExists $thirdPartyLicense
}
Copy-Item -LiteralPath $openVrLicense -Destination (Join-Path $engineDir "OPENVR-LICENSE.txt")
Copy-Item -LiteralPath $sherpaLicense -Destination (Join-Path $engineDir "SHERPA-ONNX-LICENSE.txt")
Copy-Item -LiteralPath $sherpaReadme -Destination (Join-Path $engineDir "SHERPA-ONNX-NOTICES.md")
Copy-Item -LiteralPath $onnxRuntimeLicense -Destination (Join-Path $engineDir "ONNXRUNTIME-LICENSE.txt")
Copy-Item -LiteralPath $onnxRuntimeNotices -Destination (Join-Path $engineDir "ONNXRUNTIME-THIRD-PARTY-NOTICES.txt")
[xml]$uiProjectXml = Get-Content -LiteralPath $uiProject -Raw
$windowsAppSdkReference = @($uiProjectXml.Project.ItemGroup.PackageReference) |
    Where-Object { $_.Include -eq "Microsoft.WindowsAppSDK" } |
    Select-Object -First 1
if (-not $windowsAppSdkReference -or -not $windowsAppSdkReference.Version) {
    throw "Microsoft.WindowsAppSDK package version was not found in $uiProject"
}
$windowsAppSdkVersion = [string]$windowsAppSdkReference.Version
$nugetRoot = if ($env:NUGET_PACKAGES) {
    $env:NUGET_PACKAGES
}
else {
    Join-Path ([Environment]::GetFolderPath("UserProfile")) ".nuget\packages"
}
$windowsAppSdkPackage = Join-Path $nugetRoot "microsoft.windowsappsdk\$windowsAppSdkVersion"
$windowsAppSdkLicense = Join-Path $windowsAppSdkPackage "license.txt"
$windowsAppSdkNotices = Join-Path $windowsAppSdkPackage "NOTICE.txt"
Assert-FileExists $windowsAppSdkLicense
Assert-FileExists $windowsAppSdkNotices
Copy-Item -LiteralPath $windowsAppSdkLicense -Destination (Join-Path $publishDir "WINDOWS-APP-SDK-LICENSE.txt")
Copy-Item -LiteralPath $windowsAppSdkNotices -Destination (Join-Path $publishDir "WINDOWS-APP-SDK-NOTICES.txt")

$versionNode = $uiProjectXml.SelectSingleNode("//Project/PropertyGroup/Version")
$appVersion = if ($versionNode -and $versionNode.InnerText) {
    $versionNode.InnerText.Trim()
}
else {
    "1.0.0"
}
$installerPath = Join-Path $releaseRoot "Setup-VoxLink-$appVersion.exe"

$readmePath = Join-Path $root "README.md"
$licensePath = Join-Path $root "LICENSE"
$noticesPath = Join-Path $root "THIRD-PARTY-NOTICES.md"
Assert-FileExists $readmePath
Assert-FileExists $licensePath
Assert-FileExists $noticesPath
Copy-Item -LiteralPath $readmePath -Destination $publishDir
Copy-Item -LiteralPath $licensePath -Destination $publishDir
Copy-Item -LiteralPath $noticesPath -Destination $publishDir
$publishedArtifactsDir = Join-Path $publishDir "artifacts"
New-Item -ItemType Directory -Path $publishedArtifactsDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root "artifacts\voxlink-main.png") -Destination $publishedArtifactsDir

$requiredFiles = @(
    (Join-Path $publishDir "VoxLink.exe"),
    (Join-Path $publishDir "VoxLink.dll"),
    (Join-Path $publishDir "VoxLink.deps.json"),
    (Join-Path $publishDir "VoxLink.runtimeconfig.json"),
    (Join-Path $publishDir "VoxLink.UI.Core.dll"),
    (Join-Path $publishDir "Microsoft.WindowsAppRuntime.dll"),
    (Join-Path $publishDir "Microsoft.ui.xaml.dll"),
    (Join-Path $publishDir "App.xbf"),
    (Join-Path $publishDir "MainWindow.xbf"),
    (Join-Path $publishDir "Controls\HeaderEditor.xbf"),
    (Join-Path $publishDir "Controls\OnboardingDialog.xbf"),
    (Join-Path $publishDir "Pages\AdvancedPage.xbf"),
    (Join-Path $publishDir "Pages\AudioPage.xbf"),
    (Join-Path $publishDir "Pages\LivePage.xbf"),
    (Join-Path $publishDir "Pages\ProvidersPage.xbf"),
    (Join-Path $publishDir "Pages\VRChatPage.xbf"),
    (Join-Path $publishDir "VoxLink.pri"),
    (Join-Path $publishDir "Assets\AppIcon.ico"),
    (Join-Path $publishDir "artifacts\voxlink-main.png"),
    (Join-Path $publishDir "DOTNET-LICENSE.txt"),
    (Join-Path $publishDir "DOTNET-THIRD-PARTY-NOTICES.txt"),
    (Join-Path $publishDir "WINDOWS-APP-SDK-LICENSE.txt"),
    (Join-Path $publishDir "WINDOWS-APP-SDK-NOTICES.txt"),
    (Join-Path $engineDir "VoxLink.Engine.exe"),
    (Join-Path $engineDir "VoxLink.Engine.deps.json"),
    (Join-Path $engineDir "VoxLink.Engine.runtimeconfig.json"),
    (Join-Path $engineDir "VoxLink.dll"),
    (Join-Path $engineDir "openvr_api.dll"),
    (Join-Path $engineDir "sherpa-onnx.dll"),
    (Join-Path $engineDir "sherpa-onnx-c-api.dll"),
    (Join-Path $engineDir "onnxruntime.dll"),
    (Join-Path $engineDir "OPENVR-LICENSE.txt"),
    (Join-Path $engineDir "SHERPA-ONNX-LICENSE.txt"),
    (Join-Path $engineDir "SHERPA-ONNX-NOTICES.md"),
    (Join-Path $engineDir "ONNXRUNTIME-LICENSE.txt"),
    (Join-Path $engineDir "ONNXRUNTIME-THIRD-PARTY-NOTICES.txt"),
    (Join-Path $engineDir "DOTNET-LICENSE.txt"),
    (Join-Path $engineDir "DOTNET-THIRD-PARTY-NOTICES.txt")
)
foreach ($requiredFile in $requiredFiles) {
    Assert-FileExists $requiredFile
}

$whisperRuntime = Join-Path $engineDir "runtimes\win-x64\whisper.dll"
Assert-FileExists $whisperRuntime

$forbiddenPaths = @(
    (Join-Path $publishDir "data\flutter_assets"),
    (Join-Path $publishDir "Microsoft.Windows.Workloads.Resources_ec.dll"),
    (Join-Path $engineDir "VoxLink.exe"),
    (Join-Path $engineDir "runtimes\win-arm64"),
    (Join-Path $engineDir "runtimes\win-x86")
)
foreach ($forbiddenPath in $forbiddenPaths) {
    if (Test-Path -LiteralPath $forbiddenPath) {
        throw "Release contains a forbidden path: $forbiddenPath"
    }
}

$forbiddenNames = @(
    "flutter_windows.dll",
    "flutter_secure_storage_windows_plugin.dll"
)
$forbiddenFile = Get-ChildItem -LiteralPath $publishDir -Recurse -Force -File |
    Where-Object { $_.Name -in $forbiddenNames } |
    Select-Object -First 1
if ($forbiddenFile) {
    throw "Release contains a retired Flutter binary: $($forbiddenFile.FullName)"
}
$forbiddenDirectory = Get-ChildItem -LiteralPath $publishDir -Recurse -Force -Directory |
    Where-Object { $_.Name -eq "flutter_assets" } |
    Select-Object -First 1
if ($forbiddenDirectory) {
    throw "Release contains retired Flutter assets: $($forbiddenDirectory.FullName)"
}

Get-ChildItem -LiteralPath $publishDir -Recurse -File -Filter "*.pdb" |
    Remove-Item -Force

$iscc = $null
if ($env:VOXLINK_ISCC -and (Test-Path -LiteralPath $env:VOXLINK_ISCC)) {
    $iscc = $env:VOXLINK_ISCC
}
elseif (Test-Path -LiteralPath (Join-Path $root "scripts\tools\InnoSetup\iscc.exe")) {
    $iscc = Join-Path $root "scripts\tools\InnoSetup\iscc.exe"
}
else {
    $isccCommand = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($isccCommand) {
        $iscc = $isccCommand.Source
    }
}
if (-not $iscc) {
    throw "Inno Setup compiler (iscc.exe) was not found. Run scripts\fetch-inno.ps1 or set VOXLINK_ISCC."
}
Write-Output "Compiling installer with $iscc ..."
& $iscc "/DAppVersion=$appVersion" "/DReleaseDir=$publishDir" "/O$releaseRoot" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compile failed with exit code $LASTEXITCODE"
}
Assert-FileExists $installerPath
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipArchive = [System.IO.Compression.ZipFile]::Open(
    $archivePath,
    [System.IO.Compression.ZipArchiveMode]::Create
)
try {
    $publishPrefix = $publishDir.TrimEnd("\") + "\"
    Get-ChildItem -LiteralPath $publishDir -Recurse -Force -File |
        Sort-Object FullName |
        ForEach-Object {
            $entryName = $_.FullName.Substring($publishPrefix.Length).Replace("\", "/")
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zipArchive,
                $_.FullName,
                $entryName,
                [System.IO.Compression.CompressionLevel]::Optimal
            ) | Out-Null
        }
}
finally {
    $zipArchive.Dispose()
}

foreach ($stage in @($uiStage, $engineStage)) {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}

$hash = Write-Sha256Sidecar $archivePath
$installerHash = Write-Sha256Sidecar $installerPath
$archive = Get-Item -LiteralPath $archivePath
$installer = Get-Item -LiteralPath $installerPath
Write-Output "Release: $($archive.FullName)"
Write-Output "Size: $([Math]::Round($archive.Length / 1MB, 1)) MB"
Write-Output "SHA256: $hash"
Write-Output "Hash file: $hashPath"
Write-Output "Installer: $($installer.FullName)"
Write-Output "Installer size: $([Math]::Round($installer.Length / 1MB, 1)) MB"
Write-Output "Installer SHA256: $installerHash"
Write-Output "Installer hash file: $installerPath.sha256"
