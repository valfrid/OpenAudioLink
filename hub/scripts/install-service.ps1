<#
.SYNOPSIS
    Installs or upgrades the OpenAudioLink Hub as a Windows service.

.DESCRIPTION
    One command for both. Run it on a machine with no Hub and it installs
    one; run it on a machine that already has one and it upgrades in place,
    keeping the settings, the data and the librespot binary.

    From the folder produced by the CI artifact, in an elevated PowerShell
    prompt:

        .\scripts\install-service.ps1

    Or point it straight at the downloaded zip, without extracting first:

        .\install-service.ps1 -FromZip $HOME\Downloads\OpenAudioLink-Hub-win-x64.zip

    What it keeps across an upgrade:

      - the data directory, which holds the Hub's identity, its cast points,
        the saved stations and each room's Spotify credentials
      - appsettings.json, if you have edited it
      - librespot.exe, which you supplied and the package does not contain
      - the port and data directory chosen at first install, recovered from
        the registered service rather than silently reset to the defaults

    Everything else under InstallPath is replaced, so a file that a previous
    version shipped and this one does not is gone rather than left behind.

.NOTES
    Capturing this computer's audio does NOT work from a service. Windows
    services run in session 0 and cannot reach a logged-in user's audio
    endpoints, so "Stream this computer's audio" will fail. Everything else
    — discovery, device control, OTA, radio, the test tone, the switchboard
    and the web UI — works normally. See docs/WINDOWS-AUDIO-CAPTURE.md.
#>
[CmdletBinding()]
param(
    # A zip as downloaded from CI or a release. Extracted to a temporary
    # folder and installed from there, so an upgrade is download-and-run
    # with no unpacking step in between.
    [string]$FromZip,

    [string]$InstallPath = "$env:ProgramFiles\OpenAudioLink",
    [string]$DataPath,
    [string]$ServiceName = 'OpenAudioLinkHub',
    [string]$DisplayName = 'OpenAudioLink Hub',
    [int]   $Port,

    # Opens the Hub's ports on networks Windows has classified Public as
    # well. Off by default, because "Public" is meant to mean a network you
    # do not trust. Use it only when Windows has mislabelled a home LAN and
    # you would rather not reclassify it — the script says so if it has.
    [switch]$AllowPublicNetworks,

    # Replace appsettings.json with the packaged one, discarding edits.
    [switch]$ResetSettings
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell prompt.'
}

# --- what to install from ----------------------------------------------

$temporary = $null
if ($FromZip) {
    if (-not (Test-Path $FromZip)) {
        throw "No such file: $FromZip"
    }
    $temporary = Join-Path ([IO.Path]::GetTempPath()) ("oal-hub-" + [Guid]::NewGuid().ToString('N'))
    Write-Host "Extracting $FromZip"
    Expand-Archive -Path $FromZip -DestinationPath $temporary -Force

    # A zip may hold the package at its root or inside one folder, depending
    # on who made it. Find the executable rather than assuming either.
    $found = Get-ChildItem -Path $temporary -Filter 'OpenAudioLink.Hub.exe' -Recurse |
             Select-Object -First 1
    if (-not $found) {
        throw "That zip does not contain OpenAudioLink.Hub.exe."
    }
    $sourceDir = $found.DirectoryName
}
else {
    # Run as .\scripts\install-service.ps1 from the package root, or from
    # inside the package folder itself.
    $sourceDir = Split-Path -Parent $PSScriptRoot
    if (-not (Test-Path (Join-Path $sourceDir 'OpenAudioLink.Hub.exe'))) {
        $sourceDir = $PSScriptRoot
    }
    if (-not (Test-Path (Join-Path $sourceDir 'OpenAudioLink.Hub.exe'))) {
        throw "OpenAudioLink.Hub.exe not found. Run this from the extracted Hub folder, or pass -FromZip."
    }
}

function Get-HubVersion {
    param([string]$Exe)
    if (-not (Test-Path $Exe)) { return $null }
    try {
        $info = (Get-Item $Exe).VersionInfo
        if ($info.ProductVersion) { return ($info.ProductVersion -split '\+')[0] }
        return $info.FileVersion
    }
    catch { return $null }
}

$installedExe = Join-Path $InstallPath 'OpenAudioLink.Hub.exe'
$wasInstalled = Test-Path $installedExe
$oldVersion   = Get-HubVersion $installedExe
$newVersion   = Get-HubVersion (Join-Path $sourceDir 'OpenAudioLink.Hub.exe')

# --- settings chosen at first install ------------------------------------
# Recovered from the registered service rather than reset to the defaults.
# An upgrade that silently moved the data directory would look exactly like
# a Hub that had lost every room in the house.

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService -and (-not $DataPath -or -not $Port)) {
    try {
        $imagePath = (Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" `
                                       -Name ImagePath -ErrorAction Stop).ImagePath
        if (-not $DataPath -and $imagePath -match '--Hub:DataDirectory="([^"]+)"') {
            $DataPath = $Matches[1]
            Write-Host "Keeping the data directory this Hub was installed with: $DataPath"
        }
        if (-not $Port -and $imagePath -match '--Urls="http://\*:(\d+)"') {
            $Port = [int]$Matches[1]
            Write-Host "Keeping the port this Hub was installed with: $Port"
        }
    }
    catch {
        Write-Host "Could not read the existing service's settings; using defaults." -ForegroundColor DarkGray
    }
}

if (-not $DataPath) { $DataPath = "$env:ProgramData\OpenAudioLink" }
if (-not $Port)     { $Port     = 41080 }

Write-Host ""
if ($wasInstalled) {
    Write-Host "Upgrading OpenAudioLink Hub $oldVersion -> $newVersion" -ForegroundColor Cyan
} else {
    Write-Host "Installing OpenAudioLink Hub $newVersion" -ForegroundColor Cyan
}
Write-Host ""

# --- stop the running service -------------------------------------------
# Stopped rather than deleted at this point: the files cannot be replaced
# while it holds them, but the registration is worth keeping until the new
# files are actually in place.

if ($existingService -and $existingService.Status -ne 'Stopped') {
    Write-Host "Stopping $ServiceName"
    Stop-Service -Name $ServiceName -Force
    $existingService.WaitForStatus('Stopped', '00:00:30')
    # The service object reports Stopped before the process has always
    # released its files, and a copy that races it fails on the exe.
    Start-Sleep -Seconds 2
}

# --- replace the program files ------------------------------------------

New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
New-Item -ItemType Directory -Force -Path $DataPath    | Out-Null

<#
    Kept out of the wipe below. Everything else goes, so a file an older
    version shipped and this one does not cannot survive to confuse a
    later diagnosis.

      librespot.exe      you supplied it; the package has never contained it
      appsettings.json   yours if you have edited it, see below
#>
$keep = @('librespot.exe', 'appsettings.json')

if ($wasInstalled) {
    Get-ChildItem -Path $InstallPath -Force |
        Where-Object { $keep -notcontains $_.Name } |
        ForEach-Object { Remove-Item -Path $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host "Installing to $InstallPath"
Copy-Item -Path (Join-Path $sourceDir '*') -Destination $InstallPath -Recurse -Force

# --- settings ------------------------------------------------------------
<#
    Never silently discarded and never silently withheld.

    An operator who has edited appsettings.json — a fixed data directory, a
    librespot path, a zeroconf interface — must not lose it to an upgrade.
    But a new version's defaults may contain settings that did not exist
    before, and hiding those is its own kind of unhelpful. So the edited
    file stays and the packaged one lands beside it, named, and the script
    says so.
#>
$settings = Join-Path $InstallPath 'appsettings.json'
$packaged = Join-Path $sourceDir  'appsettings.json'

if ($wasInstalled -and (Test-Path $packaged) -and (Test-Path $settings) -and -not $ResetSettings) {
    $mine   = (Get-Content $settings -Raw)
    $theirs = (Get-Content $packaged -Raw)
    if ($mine -ne $theirs) {
        Copy-Item -Path $packaged -Destination "$settings.new" -Force
        Write-Host ""
        Write-Host "Your appsettings.json was kept." -ForegroundColor Yellow
        Write-Host "  This version ships a different one; it is at appsettings.json.new" -ForegroundColor Yellow
        Write-Host "  Compare them if a new setting matters to you, or re-run with -ResetSettings." -ForegroundColor Yellow
        Write-Host ""
    }
}

# --- register the service ------------------------------------------------
# Recreated rather than edited, so what it ends up configured with is
# entirely what this script says and not a mixture with whatever a previous
# version set.

$exe = Join-Path $InstallPath 'OpenAudioLink.Hub.exe'
$binaryPath = '"{0}" --Hub:DataDirectory="{1}" --Urls="http://*:{2}"' -f $exe, $DataPath, $Port

if ($existingService) {
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

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

# --- firewall ------------------------------------------------------------
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
    Write-Host "  copy it into $InstallPath and re-run this script" -ForegroundColor DarkGray
}

# --- the trap that cost an evening ---------------------------------------
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

# --- start and check -----------------------------------------------------

Write-Host "Starting service"
Start-Service -Name $ServiceName
(Get-Service -Name $ServiceName).WaitForStatus('Running', '00:00:30')

<#
    Asking the Hub what version it is, rather than trusting that the copy
    worked. A service that is Running has started a process, which is not
    the same as a Hub that is serving — and this project has already spent
    a session on a version number that was stale because nobody checked
    the one thing that could have said so.
#>
$running = $null
for ($attempt = 0; $attempt -lt 15; $attempt++) {
    try {
        $running = Invoke-RestMethod -Uri "http://localhost:$Port/api/health" -TimeoutSec 2
        break
    }
    catch { Start-Sleep -Milliseconds 700 }
}

if ($temporary -and (Test-Path $temporary)) {
    Remove-Item -Path $temporary -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($running) {
    Write-Host "OpenAudioLink Hub $($running.version) is running as a service." -ForegroundColor Green
    if ($wasInstalled -and $oldVersion -and $running.version -eq $oldVersion) {
        Write-Host "  ...but that is the version that was already installed." -ForegroundColor Yellow
        Write-Host "  The upgrade did not take. Check that the package you ran this from is the new one." -ForegroundColor Yellow
    }
}
else {
    Write-Host "The service started but did not answer on port $Port within ten seconds." -ForegroundColor Yellow
    Write-Host "  Look in Event Viewer -> Windows Logs -> Application." -ForegroundColor Yellow
}

Write-Host "  Switchboard : http://localhost:$Port/play"
Write-Host "  Setup       : http://localhost:$Port"
Write-Host "  Data        : $DataPath"
Write-Host "  Logs        : Event Viewer -> Windows Logs -> Application"
Write-Host ""
Write-Host "Note: capturing this computer's audio does not work from a service" -ForegroundColor Yellow
Write-Host "      (session 0 cannot reach user audio endpoints)." -ForegroundColor Yellow
