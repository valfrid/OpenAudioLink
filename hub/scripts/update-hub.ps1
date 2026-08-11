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

<#
    The highest version among every matching asset, not the first one found.

    The rolling release is meant to hold one build, but an asset whose name
    carries a version does not replace one carrying a different version, so
    it accumulates. GitHub returns assets in upload order, which makes the
    first the *oldest* — and that is not a hypothetical: a Hub was offered
    0.7.0 and told "already up to date" while 0.9.2 sat beside it in the
    same release.

    The version comes from the filename rather than the tag. A tagged
    release carries it in both, but the rolling one is always tagged
    hub-latest — the tag has to stay put for the release to replace itself —
    so only the filename says which build an asset holds.
#>
$candidates = @()
foreach ($candidate in $releases) {
    foreach ($file in $candidate.assets) {
        if ($file.name -match '^OpenAudioLink-Hub-win-x64-([0-9][^-]*)\.zip$') {
            $parsed = $null
            if ([version]::TryParse($Matches[1], [ref]$parsed)) {
                $candidates += [pscustomobject]@{
                    Asset = $file; Release = $candidate
                    Text = $Matches[1]; Version = $parsed
                }
            }
        }
    }
}

if ($candidates.Count -eq 0) {
    throw ("No published Hub package found for $Repository" +
           $(if ($StableOnly) { ' (stable only). Tag a build with hub-v<version>.' }
             else { '. Push to a build branch, or tag with hub-v<version>.' }))
}

$best      = $candidates | Sort-Object -Property Version -Descending | Select-Object -First 1
$asset     = $best.Asset
$release   = $best.Release
$available = $best.Text

$kind = if ($release.prerelease) { 'latest build' } else { 'release' }
Write-Host "Available: $available  ($kind, $($asset.name))"
if ($candidates.Count -gt 1) {
    Write-Host "  ($($candidates.Count) packages published; this is the newest)" -ForegroundColor DarkGray
}

$sameVersion = $installed -and $available -eq $installed
if ($sameVersion -and -not $Force) {
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

# Which installer runs the upgrade, in order of preference.
#
# 1. The one beside this script. An upgrade should be applied by the
#    installer already on the machine, so a broken script in a new package
#    cannot leave the Hub half-installed.
# 2. The installed copy under $InstallPath. Same reasoning; this is here
#    because a copy of *this* script fetched to a temp directory has no
#    sibling, and refusing at that point is unhelpful when a perfectly good
#    installer is sitting in Program Files.
# 3. The one inside the download. The only option on a machine with no Hub
#    on it yet, and strictly better than failing — the alternative is not
#    "a safer install", it is "no install".
#
# It used to be 1 or nothing, which turned fetching this script on its own
# into a dead end: it downloaded 41 MB and then refused, with an error
# naming the temp directory and no way forward.
$unpacked  = $null
$installer = Join-Path $PSScriptRoot 'install-service.ps1'

if (-not (Test-Path $installer)) {
    $installed = Join-Path $InstallPath 'scripts\install-service.ps1'
    if (Test-Path $installed) {
        Write-Host "Using the installer already on this machine ($installed)"
        $installer = $installed
    }
    else {
        Write-Host "No installer on this machine; using the one in the download"
        $unpacked = Join-Path ([IO.Path]::GetTempPath()) ("oal-update-" + [guid]::NewGuid().ToString('N'))
        Expand-Archive -Path $download -DestinationPath $unpacked -Force

        # Everything here came off the internet, so it carries the mark of
        # the web and PowerShell refuses to run it until that is cleared.
        Get-ChildItem -Path $unpacked -Recurse -Include *.ps1 |
            Unblock-File -ErrorAction SilentlyContinue

        $installer = Join-Path $unpacked 'scripts\install-service.ps1'
        if (-not (Test-Path $installer)) {
            throw "The package $($asset.name) has no scripts\install-service.ps1 in it."
        }
    }
}

try {
    & $installer -FromZip $download -InstallPath $InstallPath -ServiceName $ServiceName
}
finally {
    Remove-Item -Path $download -Force -ErrorAction SilentlyContinue
    if ($unpacked) {
        Remove-Item -Path $unpacked -Recurse -Force -ErrorAction SilentlyContinue
    }
}
