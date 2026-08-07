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
    Two kinds of build are published, and this takes the newer:

      hub-latest   whatever the working branch last built, marked
                   prerelease. Exists so that upgrading never requires
                   remembering to tag first.
      hub-v*       a deliberate, permanent release. -StableOnly restricts
                   to these, which is what a machine somebody else depends
                   on should be set to.

    Between two commits that did not bump the version, the rolling build
    reports the same number as the one installed and this stops. -Force
    installs it anyway.
#>
[CmdletBinding()]
param(
    [string]$Repository  = 'valfrid/OpenAudioLink',
    [string]$InstallPath = "$env:ProgramFiles\OpenAudioLink",
    [string]$ServiceName = 'OpenAudioLinkHub',

    # Install even when the installed version already matches. For putting
    # a machine back to a known state after hand-editing it, and the only
    # way to reinstall the rolling build, whose version does not change
    # between two commits that did not bump it.
    [switch]$Force,

    # Ignore the rolling hub-latest build and take only tagged releases.
    # What a machine somebody else depends on should be set to.
    [switch]$StableOnly,

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

<#
    The whole list, not /releases/latest.

    That endpoint excludes prereleases by design, and the rolling
    hub-latest release — the one that exists so upgrading never requires
    remembering to tag first — is a prerelease. Asking for "latest" would
    quietly never see it.

    The list comes back newest first, so the newest release that carries a
    Hub package wins, whichever kind it is.
#>
Write-Host "Asking GitHub what $Repository has published"
try {
    $releases = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases?per_page=20" `
                                  -Headers @{ 'User-Agent' = 'OpenAudioLink-update' } `
                                  -TimeoutSec 30
}
catch {
    throw "Could not reach GitHub: $($_.Exception.Message)"
}

if ($StableOnly) {
    $releases = $releases | Where-Object { -not $_.prerelease }
}

# Matching on the name rather than taking the first asset, so adding a
# second one later — firmware, a checksum, librespot — cannot quietly
# install the wrong thing.
$release = $null
$asset   = $null
foreach ($candidate in $releases) {
    $match = $candidate.assets |
             Where-Object { $_.name -like 'OpenAudioLink-Hub-win-x64*.zip' } |
             Select-Object -First 1
    if ($match) {
        $release = $candidate
        $asset   = $match
        break
    }
}

if (-not $asset) {
    throw ("No published Hub package found for $Repository" +
           $(if ($StableOnly) { ' (stable only). Tag a build with hub-v<version>.' }
             else { '. Push to a build branch, or tag with hub-v<version>.' }))
}

<#
    The version comes from the asset's filename rather than from the tag.

    A tagged release carries it in both, but the rolling one is always
    tagged hub-latest — the tag has to stay put for the release to replace
    itself instead of accumulating — so the tag says nothing about which
    build it holds. The filename does.
#>
if ($asset.name -match 'OpenAudioLink-Hub-win-x64-([0-9][^-]*)\.zip$') {
    $available = $Matches[1]
} else {
    $available = $release.tag_name -replace '^hub-v', ''
}

$kind = if ($release.prerelease) { 'latest build' } else { 'release' }
Write-Host "Available: $available  ($kind, $($asset.name))"

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
