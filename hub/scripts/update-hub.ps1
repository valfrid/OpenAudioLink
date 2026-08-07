<#
.SYNOPSIS
    Downloads the latest published Hub and installs it.

.DESCRIPTION
    The whole upgrade, as one command, from an elevated PowerShell prompt:

        & "$env:ProgramFiles\OpenAudioLink\scripts\update-hub.ps1"

    It asks GitHub for the latest release, compares that version with the
    one installed, and stops there if they match. Otherwise it downloads
    the Windows package, hands it to install-service.ps1, and that keeps
    the data directory, the port, the settings and librespot.exe.

    Nothing here needs a token or a login: the repository is public and a
    release asset is a plain download. That is the whole reason upgrades
    come from releases rather than from CI artifacts, which need both.

.NOTES
    A release has to exist for this to find anything. Tag a build with
    hub-v<version> and the release workflow publishes the package; until
    the first tag this reports that it found no release, which is accurate
    rather than broken.
#>
[CmdletBinding()]
param(
    [string]$Repository  = 'valfrid/OpenAudioLink',
    [string]$InstallPath = "$env:ProgramFiles\OpenAudioLink",
    [string]$ServiceName = 'OpenAudioLinkHub',

    # Install even when the installed version already matches. For putting
    # a machine back to a known state after hand-editing it.
    [switch]$Force,

    # Report what would happen and change nothing.
    [switch]$WhatIfOnly
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell prompt.'
}

# Windows PowerShell 5.1 still defaults to TLS 1.0 on some builds, which
# GitHub refuses. The symptom is a connection error that reads like the
# network is down.
[Net.ServicePointManager]::SecurityProtocol =
    [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$installedExe = Join-Path $InstallPath 'OpenAudioLink.Hub.exe'
$installed = $null
if (Test-Path $installedExe) {
    try {
        $info = (Get-Item $installedExe).VersionInfo
        $installed = if ($info.ProductVersion) { ($info.ProductVersion -split '\+')[0] } else { $info.FileVersion }
    }
    catch { }
}

Write-Host "Installed: $(if ($installed) { $installed } else { 'nothing found' })"

Write-Host "Asking GitHub for the latest release of $Repository"
try {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases/latest" `
                                 -Headers @{ 'User-Agent' = 'OpenAudioLink-update' } `
                                 -TimeoutSec 30
}
catch {
    throw "Could not reach GitHub: $($_.Exception.Message)"
}

if (-not $release) {
    throw "No release found. Tag a build with hub-v<version> first."
}

# The tag carries the version, the asset carries the bytes. Matching on the
# name rather than taking the first asset, so adding a second one later —
# firmware, a checksum file — does not quietly install the wrong thing.
$asset = $release.assets | Where-Object { $_.name -like 'OpenAudioLink-Hub-win-x64*.zip' } |
         Select-Object -First 1
if (-not $asset) {
    throw "Release $($release.tag_name) has no Windows Hub package attached."
}

$available = $release.tag_name -replace '^hub-v', ''
Write-Host "Available: $available  ($($asset.name))"

if ($installed -and $available -eq $installed -and -not $Force) {
    Write-Host ""
    Write-Host "Already on $installed. Nothing to do." -ForegroundColor Green
    Write-Host "  Re-run with -Force to install it again anyway."
    return
}

if ($WhatIfOnly) {
    Write-Host ""
    Write-Host "Would install $available over $(if ($installed) { $installed } else { 'a clean machine' })."
    return
}

$download = Join-Path ([IO.Path]::GetTempPath()) $asset.name
Write-Host "Downloading $([math]::Round($asset.size / 1MB, 1)) MB"
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $download `
                  -Headers @{ 'User-Agent' = 'OpenAudioLink-update' } -UseBasicParsing

# The installer next to this script, not the one inside the download: an
# upgrade is applied by the installer already on the machine, so a broken
# script in a new package cannot leave the Hub half-installed.
$installer = Join-Path $PSScriptRoot 'install-service.ps1'
if (-not (Test-Path $installer)) {
    throw "install-service.ps1 is not beside this script ($PSScriptRoot)."
}

try {
    & $installer -FromZip $download -InstallPath $InstallPath -ServiceName $ServiceName
}
finally {
    Remove-Item -Path $download -Force -ErrorAction SilentlyContinue
}
