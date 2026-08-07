<#
.SYNOPSIS
    Downloads the published librespot binary and puts it beside the Hub.

.DESCRIPTION
    Spotify Connect needs a receiver binary that this project deliberately
    does not bundle. Whether a reimplementation of somebody's streaming
    protocol is licensed for use with that service is the operator's
    decision rather than this project's (docs/CAST-POINTS.md), so fetching
    it is an explicit act:

        .\get-librespot.ps1

    or, as part of an install:

        .\install-service.ps1 -WithLibrespot

    The binary comes from a release of this repository, built by the
    librespot workflow, because the librespot project publishes no Windows
    binaries of its own. It is verified against the SHA256 published
    beside it — an executable pulled over the network and then run as a
    service is exactly the thing worth checking.

    librespot is MIT-licensed and is not affiliated with Spotify.
#>
[CmdletBinding()]
param(
    [string]$Repository  = 'valfrid/OpenAudioLink',
    [string]$InstallPath = "$env:ProgramFiles\OpenAudioLink",

    # A specific librespot version, e.g. 0.8.0. The newest published build
    # otherwise.
    [string]$Version,

    # Replace an existing librespot.exe. Without this an existing one is
    # left alone, because it may be a build the operator made themselves
    # with features this one does not have.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 still defaults to TLS 1.0 on some builds, which
# GitHub refuses. The symptom is a connection error that reads like the
# network is down.
[Net.ServicePointManager]::SecurityProtocol =
    [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$target = Join-Path $InstallPath 'librespot.exe'

if ((Test-Path $target) -and -not $Force) {
    Write-Host "librespot.exe is already at $InstallPath; leaving it alone." -ForegroundColor DarkGray
    Write-Host "  Re-run with -Force to replace it." -ForegroundColor DarkGray
    return
}

Write-Host "Asking GitHub for a published librespot build"
try {
    $releases = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases?per_page=50" `
                                  -Headers @{ 'User-Agent' = 'OpenAudioLink-update' } `
                                  -TimeoutSec 30
}
catch {
    throw "Could not reach GitHub: $($_.Exception.Message)"
}

if ($Version) {
    $releases = $releases | Where-Object { $_.tag_name -eq "librespot-v$Version" }
}

# Newest first from the API, so the first release carrying the asset wins.
$release = $null
$asset   = $null
$sums    = $null
foreach ($candidate in $releases) {
    $match = $candidate.assets | Where-Object { $_.name -eq 'librespot-win-x64.exe' } | Select-Object -First 1
    if ($match) {
        $release = $candidate
        $asset   = $match
        $sums    = $candidate.assets | Where-Object { $_.name -eq 'librespot-win-x64.exe.sha256' } |
                   Select-Object -First 1
        break
    }
}

if (-not $asset) {
    Write-Host ""
    Write-Host "No published librespot build found." -ForegroundColor Yellow
    Write-Host "  Build one: Actions -> librespot -> Run workflow, with Publish ticked." -ForegroundColor Yellow
    Write-Host "  Or build it yourself: docs/BUILDING-LIBRESPOT-WINDOWS.md" -ForegroundColor Yellow
    throw "librespot is not available to download."
}

Write-Host "Found $($release.tag_name)  ($([math]::Round($asset.size / 1MB, 1)) MB)"

$temp = Join-Path ([IO.Path]::GetTempPath()) ("librespot-" + [Guid]::NewGuid().ToString('N') + '.exe')
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $temp `
                  -Headers @{ 'User-Agent' = 'OpenAudioLink-update' } -UseBasicParsing

try {
    <#
        Verified before it is ever placed where something will run it.

        A missing checksum is a refusal rather than a shrug: this is a
        downloaded executable that a Windows service will launch, and
        "the file that was meant to say whether this is right is absent"
        is not a reason to carry on.
    #>
    if (-not $sums) {
        throw "$($release.tag_name) publishes no SHA256 for librespot-win-x64.exe. Refusing to install it."
    }

    $published = (Invoke-WebRequest -Uri $sums.browser_download_url -UseBasicParsing `
                                    -Headers @{ 'User-Agent' = 'OpenAudioLink-update' }).Content
    if ($published -is [byte[]]) {
        $published = [Text.Encoding]::ASCII.GetString($published)
    }
    $expected = ($published -split '\s+')[0].Trim().ToLower()
    $actual   = (Get-FileHash $temp -Algorithm SHA256).Hash.ToLower()

    if ($expected -ne $actual) {
        throw "Checksum mismatch. Expected $expected, got $actual. Not installing it."
    }
    Write-Host "SHA256 verified: $actual"

    New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
    Copy-Item -Path $temp -Destination $target -Force
}
finally {
    Remove-Item -Path $temp -Force -ErrorAction SilentlyContinue
}

Write-Host "Installed librespot to $target" -ForegroundColor Green
Write-Host "  Each cast point becomes a Spotify device under its own name."
Write-Host "  A room has to be signed in once; see docs/LIBRESPOT.md."
