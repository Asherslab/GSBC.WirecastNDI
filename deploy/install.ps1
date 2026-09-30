#Requires -Version 5.1
<#
.SYNOPSIS
    Installs GSBC.WirecastNDI on the Wirecast PC so it runs hidden for whoever logs in.

.DESCRIPTION
    Run from the unzipped release folder, as Administrator (Install.cmd does this for you).
      - copies the app to C:\Program Files\GSBC.WirecastNDI (keeps an existing appsettings.json)
      - copies ffmpeg.exe next to it (from this folder, -FfmpegExe, or PATH)
      - opens Windows Firewall for NDI (so nobody gets a firewall prompt to click "Cancel" on)
      - registers a hidden scheduled task: at every logon, plus a 5-minute "still running?" check

    Why a logon task and not a Windows Service: services run in session 0, where Wirecast's
    virtual camera and microphone (which live in the logged-in user's session) aren't reachable.

.EXAMPLE
    .\install.ps1
.EXAMPLE
    .\install.ps1 -FfmpegExe C:\tools\ffmpeg\bin\ffmpeg.exe
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'GSBC.WirecastNDI'),
    [string]$FfmpegExe,
    [switch]$ResetConfig,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$TaskName = 'GSBC WirecastNDI'
$ExeName = 'GSBC.WirecastNDI.exe'
$Source = $PSScriptRoot

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this as Administrator (or double-click Install.cmd).'
}

if (-not (Test-Path (Join-Path $Source $ExeName))) {
    throw "$ExeName not found next to this script. Run install.ps1 from the unzipped release folder."
}

Write-Host "== GSBC.WirecastNDI installer ==" -ForegroundColor Cyan

# --- NDI runtime -----------------------------------------------------------------------------
$ndiDir = [Environment]::GetEnvironmentVariable('NDI_RUNTIME_DIR_V6', 'Machine')
$ndiDll = if ($ndiDir) { Join-Path $ndiDir 'Processing.NDI.Lib.x64.dll' }
if ((-not $ndiDll -or -not (Test-Path $ndiDll)) -and -not (Test-Path (Join-Path $Source 'Processing.NDI.Lib.x64.dll'))) {
    Write-Warning 'NDI Runtime not found. Install it (or NDI Tools) before this will work: https://ndi.link/NDIRedistV6'
} else {
    Write-Host "NDI runtime: $(if ($ndiDll) { $ndiDll } else { 'bundled' })"
}

# --- ffmpeg ----------------------------------------------------------------------------------
function Resolve-Ffmpeg {
    foreach ($candidate in @($FfmpegExe, (Join-Path $Source 'ffmpeg.exe'), (Join-Path $InstallDir 'ffmpeg.exe'))) {
        if ($candidate -and (Test-Path $candidate)) { return (Resolve-Path $candidate).Path }
    }
    $cmd = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($cmd) {
        $item = Get-Item $cmd.Source
        # winget installs ffmpeg behind a symlink in ...\WinGet\Links; copy the real file.
        if ($item.LinkType -and $item.Target) { return [string]($item.Target | Select-Object -First 1) }
        return $cmd.Source
    }
    return $null
}

$ffmpeg = Resolve-Ffmpeg
if (-not $ffmpeg) {
    throw @'
ffmpeg.exe not found. Either:
  - winget install Gyan.FFmpeg      (then re-run this installer), or
  - download https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip and pass
    -FfmpegExe <path to bin\ffmpeg.exe>, or drop ffmpeg.exe next to this script.
'@
}
Write-Host "ffmpeg: $ffmpeg"

# --- stop anything running -------------------------------------------------------------------
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
}
Get-Process -Name 'GSBC.WirecastNDI' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

# --- copy files ------------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$configPath = Join-Path $InstallDir 'appsettings.json'
$keepConfig = (Test-Path $configPath) -and -not $ResetConfig
if ($keepConfig) { Copy-Item $configPath "$configPath.keep" -Force }

Get-ChildItem $Source -File |
    Where-Object { $_.Extension -notin '.ps1', '.cmd' } |
    Copy-Item -Destination $InstallDir -Force

if ($keepConfig) {
    Move-Item "$configPath.keep" $configPath -Force
    Write-Host 'Kept existing appsettings.json (use -ResetConfig to replace it).'
}

if ($ffmpeg -ne (Join-Path $InstallDir 'ffmpeg.exe')) {
    Copy-Item $ffmpeg (Join-Path $InstallDir 'ffmpeg.exe') -Force
}
Copy-Item (Join-Path $Source 'uninstall.ps1') $InstallDir -Force -ErrorAction SilentlyContinue

# --- log folder writable by any user ---------------------------------------------------------
$logDir = Join-Path $env:ProgramData 'GSBC.WirecastNDI\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$usersSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')
$acl = Get-Acl $logDir
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
    $usersSid, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
Set-Acl $logDir $acl
Write-Host "Logs: $logDir"

# --- firewall (NDI discovery + streams) ------------------------------------------------------
$exePath = Join-Path $InstallDir $ExeName
Get-NetFirewallRule -DisplayName 'GSBC WirecastNDI*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName 'GSBC WirecastNDI (TCP-In)' -Direction Inbound -Program $exePath -Protocol TCP -Action Allow -Profile Any | Out-Null
New-NetFirewallRule -DisplayName 'GSBC WirecastNDI (UDP-In)' -Direction Inbound -Program $exePath -Protocol UDP -Action Allow -Profile Any | Out-Null
Write-Host 'Firewall rules added.'

# --- scheduled task --------------------------------------------------------------------------
$action = New-ScheduledTaskAction -Execute $exePath -WorkingDirectory $InstallDir
$atLogon = New-ScheduledTaskTrigger -AtLogOn
# Watchdog: re-launch if it ever died. MultipleInstances=IgnoreNew makes this a no-op while running.
$watchdog = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5)
$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew `
    -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -DontStopOnIdleEnd `
    -StartWhenAvailable -Hidden
# Any interactive user: runs in whoever is logged in, without storing a password.
$taskPrincipal = New-ScheduledTaskPrincipal -GroupId 'S-1-5-32-545' -RunLevel Limited

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger @($atLogon, $watchdog) `
    -Settings $settings -Principal $taskPrincipal -Force `
    -Description 'Publishes the Wirecast virtual camera + microphone as an NDI source. See C:\ProgramData\GSBC.WirecastNDI\logs' | Out-Null
Write-Host "Scheduled task '$TaskName' registered."

if (-not $NoStart) {
    Start-ScheduledTask -TaskName $TaskName
    Write-Host 'Started.'
}

Write-Host ''
Write-Host 'Next steps:' -ForegroundColor Cyan
Write-Host "  1. In Wirecast, turn on Output > Virtual Camera and Virtual Microphone."
Write-Host "  2. Check the devices were found:   & '$exePath' list-devices | Out-Host"
Write-Host "  3. Check the NDI output:           & '$exePath' probe 'Wirecast Program' | Out-Host"
Write-Host "  4. Settings: $configPath (restart the task after editing)"
