<#
.SYNOPSIS
    Removes the OpenAudioLink Hub Windows service.

.DESCRIPTION
    Stops and deletes the service and its firewall rules. Installed files and
    the data directory are left alone by default, so the Hub's identity and
    uploaded firmware survive a reinstall; pass -RemoveFiles to delete them.
#>
[CmdletBinding()]
param(
    [string]$InstallPath = "$env:ProgramFiles\OpenAudioLink",
    [string]$DataPath    = "$env:ProgramData\OpenAudioLink",
    [string]$ServiceName = 'OpenAudioLinkHub',
    [switch]$RemoveFiles
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell prompt.'
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Write-Host "Stopping $ServiceName"
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', '00:00:30')
    }
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Service removed"
} else {
    Write-Host "Service $ServiceName is not installed"
}

foreach ($name in @('OpenAudioLink Hub - web UI and API', 'OpenAudioLink Hub - discovery')) {
    Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue
}
Write-Host "Firewall rules removed"

if ($RemoveFiles) {
    foreach ($path in @($InstallPath, $DataPath)) {
        if (Test-Path $path) {
            Remove-Item -Path $path -Recurse -Force
            Write-Host "Deleted $path"
        }
    }
} else {
    Write-Host "Files kept: $InstallPath"
    Write-Host "Data kept:  $DataPath  (Hub identity and uploaded firmware)"
}
