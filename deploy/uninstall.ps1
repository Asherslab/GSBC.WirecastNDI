#Requires -Version 5.1
<#
.SYNOPSIS
    Removes GSBC.WirecastNDI: scheduled task, firewall rules and program files. Logs are kept
    unless -RemoveLogs is given.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'GSBC.WirecastNDI'),
    [switch]$RemoveLogs
)

$ErrorActionPreference = 'Stop'
$TaskName = 'GSBC WirecastNDI'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this as Administrator.'
}

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "Removed scheduled task '$TaskName'."
}

Get-Process -Name 'GSBC.WirecastNDI' -ErrorAction SilentlyContinue | Stop-Process -Force
Get-NetFirewallRule -DisplayName 'GSBC WirecastNDI*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Start-Sleep -Seconds 1

if (Test-Path $InstallDir) {
    # This script may be running from inside InstallDir; remove everything else first.
    Get-ChildItem $InstallDir | Where-Object { $_.FullName -ne $PSCommandPath } | Remove-Item -Recurse -Force
    if ($PSCommandPath -notlike "$InstallDir*") { Remove-Item $InstallDir -Recurse -Force }
    Write-Host "Removed $InstallDir."
}

if ($RemoveLogs) {
    Remove-Item (Join-Path $env:ProgramData 'GSBC.WirecastNDI') -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Removed logs.'
}

Write-Host 'Uninstalled.'
