<#
.SYNOPSIS
    Installs the OpenAudioLink Hub as a Windows service.

.DESCRIPTION
    Copies the published Hub next to this script into InstallPath, registers
    it as an automatically starting service that restarts after a crash, and
    opens the firewall ports it needs.

    Run from an elevated PowerShell prompt, in the folder produced by the
    CI artifact:

        .\scripts\install-service.ps1

.NOTES
    Capturing this computer's audio does NOT work from a service. Windows
    services run in session 0 and cannot reach a logged-in user's audio
    endpoints, so "Stream this computer's audio" will fail. Everything else
    — discovery, device control, OTA, the test tone and the web UI — works
    normally. See docs/WINDOWS-AUDIO-CAPTURE.md.
#>
[CmdletBinding()]
param(
    [string]$InstallPath = "$env:ProgramFiles\OpenAudioLink",
    [string]$DataPath    = "$env:ProgramData\OpenAudioLink",
    [string]$ServiceName = 'OpenAudioLinkHub',
    [string]$DisplayName = 'OpenAudioLink Hub',
    [int]   $Port        = 41080,

    # Opens the Hub's ports on networks Windows has classified Public as
    # well. Off by default, because "Public" is meant to mean a network you
    # do not trust. Use it only when Windows has mislabelled a home LAN and
    # you would rather not reclassify it — the script says so if it has.
    [switch]$AllowPublicNetworks
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell prompt.'
}

$source = Join-Path (Split-Path -Parent $PSScriptRoot) 'OpenAudioLink.Hub.exe'
if (-not (Test-Path $source)) {
    $source = Join-Path $PSScriptRoot 'OpenAudioLink.Hub.exe'
}
if (-not (Test-Path $source)) {
    throw "OpenAudioLink.Hub.exe not found. Run this from the extracted Hub folder."
}
$sourceDir = Split-Path -Parent $source

# --- stop and remove any previous installation -------------------------
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping existing service..."
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $existing.WaitForStatus('Stopped', '00:00:30')
    }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# --- copy files --------------------------------------------------------
Write-Host "Installing to $InstallPath"
New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
New-Item -ItemType Directory -Force -Path $DataPath    | Out-Null
Copy-Item -Path (Join-Path $sourceDir '*') -Destination $InstallPath -Recurse -Force `
          -Exclude 'scripts'

$exe = Join-Path $InstallPath 'OpenAudioLink.Hub.exe'

# --- register the service ----------------------------------------------
# The data directory is passed on the command line so Hub state lives in
# ProgramData rather than under Program Files, which a service account
# should not be writing to.
$binaryPath = '"{0}" --Hub:DataDirectory="{1}" --Urls="http://*:{2}"' -f $exe, $DataPath, $Port

Write-Host "Registering service $ServiceName"
New-Service -Name $ServiceName `
            -BinaryPathName $binaryPath `
            -DisplayName $DisplayName `
            -Description 'OpenAudioLink Hub: device discovery, control, provisioning and RTP audio production.' `
            -StartupType Automatic | Out-Null

# Delayed start: the network stack should be up before discovery binds its
# multicast socket.
sc.exe config $ServiceName start= delayed-auto | Out-Null

# Restart on failure: after 5s, then 10s, then every 60s; counter resets daily.
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/60000 | Out-Null

# --- firewall ----------------------------------------------------------
# Every one of these is inbound multicast or an inbound connection, and
# each has a way of failing silently. A Hub that cannot receive on 41000
# discovers no devices; a receiver that cannot receive on 5353 is invisible
# to every phone in the house while insisting it is announcing correctly.
$firewallProfiles = if ($AllowPublicNetworks) { 'Private','Domain','Public' } else { 'Private','Domain' }

Write-Host "Opening firewall ports ($($firewallProfiles -join ', '))"
foreach ($rule in @(
    @{ Name = 'OpenAudioLink Hub - web UI and API'; Protocol = 'TCP'; Port = $Port },
    @{ Name = 'OpenAudioLink Hub - discovery';      Protocol = 'UDP'; Port = 41000 },
    # Spotify Connect receivers announce themselves here (docs/LIBRESPOT.md).
    # Without it they answer nobody, because nobody's question arrives.
    @{ Name = 'OpenAudioLink Hub - mDNS';           Protocol = 'UDP'; Port = 5353 }
)) {
    Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName $rule.Name `
                        -Direction Inbound -Action Allow `
                        -Protocol $rule.Protocol -LocalPort $rule.Port `
                        -Profile $firewallProfiles | Out-Null
}

# The receivers are separate processes, so a port rule is not enough on
# every configuration; a program rule covers whatever else they open.
$librespot = Join-Path $InstallPath 'librespot.exe'
if (Test-Path $librespot) {
    $name = 'OpenAudioLink Hub - librespot'
    Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName $name -Direction Inbound -Action Allow `
                        -Program $librespot -Profile $firewallProfiles | Out-Null
    Write-Host "  found librespot; Spotify Connect receivers are enabled"
}
else {
    Write-Host "  no librespot.exe here; cast points will not be offered to Spotify" -ForegroundColor DarkGray
}

# --- the trap that cost an evening -------------------------------------
# Windows classifies networks by itself and gets wired home LANs wrong. On
# a Public network it disables its own inbound mDNS rules, and the symptom
# is perfect: the Hub reaches everything, answers every request made to its
# address, and hears no multicast at all — so nothing discovers it and it
# discovers nothing. Worth a loud message rather than a silent failure.
$public = Get-NetConnectionProfile -ErrorAction SilentlyContinue |
          Where-Object { $_.NetworkCategory -eq 'Public' }
if ($public -and -not $AllowPublicNetworks) {
    Write-Host ""
    Write-Host "WARNING: Windows has classified these networks as Public:" -ForegroundColor Yellow
    foreach ($p in $public) {
        Write-Host "    $($p.InterfaceAlias)  ($($p.Name))" -ForegroundColor Yellow
    }
    Write-Host "  The rules above do not apply there, so discovery will not work." -ForegroundColor Yellow
    Write-Host "  If that is your home network, reclassify it:" -ForegroundColor Yellow
    foreach ($p in $public) {
        Write-Host "    Set-NetConnectionProfile -InterfaceAlias '$($p.InterfaceAlias)' -NetworkCategory Private" -ForegroundColor Yellow
    }
    Write-Host "  Or re-run this script with -AllowPublicNetworks." -ForegroundColor Yellow
}

# --- start -------------------------------------------------------------
Write-Host "Starting service"
Start-Service -Name $ServiceName
(Get-Service -Name $ServiceName).WaitForStatus('Running', '00:00:30')

Write-Host ""
Write-Host "OpenAudioLink Hub is running as a service." -ForegroundColor Green
Write-Host "  Web UI : http://localhost:$Port"
Write-Host "  Data   : $DataPath"
Write-Host "  Logs   : Event Viewer -> Windows Logs -> Application"
Write-Host ""
Write-Host "Note: capturing this computer's audio does not work from a service" -ForegroundColor Yellow
Write-Host "      (session 0 cannot reach user audio endpoints)." -ForegroundColor Yellow
