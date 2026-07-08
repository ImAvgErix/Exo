# Repair-Discord.ps1 - OptiHub / Discord Optimizer clean reset.
# Wipes program files AND all cached renderer state (the usual cause of black
# screens), keeps your login, reinstalls fresh from discord.com.
#
#   irm "https://raw.githubusercontent.com/BarcusEric/OptiHub/main/Repair-Discord.ps1" | iex
#
# Optional full logout reset (also clears login/session) - run this first:
#   $env:OPTIHUB_REPAIR_FULL = '1'
#   # legacy alias still works: $env:DISCOPT_REPAIR_FULL = '1'

$ErrorActionPreference = 'Stop'

function Write-RepStep([string]$Msg) { Write-Host "[*] $Msg" -ForegroundColor Cyan }
function Write-RepOk([string]$Msg)   { Write-Host "[+] $Msg" -ForegroundColor Green }
function Write-RepWarn([string]$Msg) { Write-Host "[!] $Msg" -ForegroundColor Yellow }
function Write-RepErr([string]$Msg)  { Write-Host "[-] $Msg" -ForegroundColor Red }

function Stop-RepairDiscord {
    Get-Process Discord, Discord.bin, Update -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Get-Process Discord, Discord.bin, Update -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

function Get-RepairActiveApp([string]$DiscordRoot) {
    Get-ChildItem $DiscordRoot -Directory -Filter 'app-*' -ErrorAction SilentlyContinue |
        Sort-Object { [version]($_.Name -replace '^app-', '') } -Descending |
        Select-Object -First 1
}

function Remove-RepairProgramFiles([string]$DiscordRoot) {
    if (-not (Test-Path $DiscordRoot)) {
        Write-RepOk 'No old program files to remove'
        return
    }
    Write-RepStep 'Removing Discord program files completely (login is stored elsewhere)...'
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        Remove-Item $DiscordRoot -Recurse -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path $DiscordRoot)) { break }
        Start-Sleep -Seconds 2
        Get-Process Discord, Discord.bin, Update -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path $DiscordRoot) {
        throw "Could not delete $DiscordRoot - close everything Discord-related (check Task Manager) and rerun"
    }
    Write-RepOk 'Old program files removed'
}

function Clear-RepairRendererState([string]$AppDataDiscord, [bool]$FullReset) {
    if (-not (Test-Path $AppDataDiscord)) {
        Write-RepOk 'No cached app data to clean'
        return
    }

    if ($FullReset) {
        Write-RepStep 'FULL reset requested - clearing app data including login...'
        Remove-Item $AppDataDiscord -Recurse -Force -ErrorAction SilentlyContinue
        Write-RepOk 'App data fully cleared (you will need to log in again)'
        return
    }

    # Black screens are usually corrupt GPU/shader/code caches. Wipe everything
    # under %APPDATA%\discord EXCEPT the folders that store the login session.
    $keep = @('Local Storage', 'IndexedDB', 'Cookies', 'Cookies-journal', 'databases', 'Network')
    Write-RepStep 'Purging all cached renderer state (GPU/shader/code caches, settings)...'
    $removed = 0
    Get-ChildItem $AppDataDiscord -Force -ErrorAction SilentlyContinue | ForEach-Object {
        if ($keep -contains $_.Name) { return }
        attrib -R $_.FullName 2>$null
        Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        $removed++
    }
    Write-RepOk "Renderer state purged ($removed item(s) removed, login kept)"
}

function Install-RepairFreshDiscord([string]$DiscordRoot) {
    Write-RepStep 'Downloading the official Discord installer...'
    $setup = Join-Path ([IO.Path]::GetTempPath()) 'DiscordSetup-repair.exe'
    if (Test-Path $setup) { Remove-Item $setup -Force -ErrorAction SilentlyContinue }
    $url = 'https://discord.com/api/downloads/distributions/app/installers/latest?channel=stable&platform=win&arch=x64'
    Invoke-WebRequest -Uri $url -OutFile $setup -UseBasicParsing -Headers @{ 'User-Agent' = 'OptiHub-Repair/1.0' }
    if (-not (Test-Path $setup) -or ((Get-Item $setup).Length -lt 50000000)) {
        throw 'Discord installer download failed - check your internet connection'
    }

    Write-RepStep 'Installing a brand new Discord (silent)...'
    Start-Process -FilePath $setup -ArgumentList '-s' -Wait | Out-Null

    $deadline = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $deadline) {
        $app = Get-RepairActiveApp $DiscordRoot
        if ($app -and (Test-Path (Join-Path $app.FullName 'Discord.exe'))) { return $app }
        Start-Sleep -Seconds 3
    }
    throw 'Discord install did not complete - run the installer from discord.com manually'
}

function Start-RepairDiscord([string]$DiscordRoot, [string]$AppDir) {
    $exe = Join-Path $AppDir 'Discord.exe'
    if (Test-Path $exe) {
        Start-Process -FilePath $exe -WorkingDirectory $AppDir | Out-Null
        return
    }
    $updateExe = Join-Path $DiscordRoot 'Update.exe'
    if (Test-Path $updateExe) {
        Start-Process -FilePath $updateExe -ArgumentList '--processStart', 'Discord.exe' -WorkingDirectory $DiscordRoot | Out-Null
    }
}

function Wait-RepairDiscordWindow([int]$TimeoutSec = 120) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $win = Get-Process Discord -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowTitle } |
            Select-Object -First 1
        if ($win) { return $true }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Set-RepairHardwareAccelerationOff([string]$AppDataDiscord) {
    if (-not (Test-Path $AppDataDiscord)) {
        New-Item -ItemType Directory -Path $AppDataDiscord -Force | Out-Null
    }
    $settingsPath = Join-Path $AppDataDiscord 'settings.json'
    $json = '{' + [Environment]::NewLine +
        '  "enableHardwareAcceleration": false,' + [Environment]::NewLine +
        '  "SKIP_HOST_UPDATE": false' + [Environment]::NewLine +
        '}'
    attrib -R $settingsPath 2>$null
    [IO.File]::WriteAllText($settingsPath, $json, [Text.UTF8Encoding]::new($false))
    Write-RepOk 'Hardware acceleration disabled (fixes GPU-driver black screens)'
}

function Read-RepairYesNo([string]$Question) {
    while ($true) {
        try {
            $answer = Read-Host "$Question (y/n)"
        } catch {
            return $true
        }
        if ($answer -match '^[yY]') { return $true }
        if ($answer -match '^[nN]') { return $false }
        Write-Host 'Please answer y or n.' -ForegroundColor DarkGray
    }
}

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Repair-Discord.ps1 must be run on Windows.'
    }

    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $localAppData = [Environment]::GetEnvironmentVariable('LOCALAPPDATA')
    $appData = [Environment]::GetEnvironmentVariable('APPDATA')
    if (-not $localAppData -or -not $appData) { throw 'LOCALAPPDATA/APPDATA not set.' }

    $discordRoot = Join-Path $localAppData 'Discord'
    $appDataDiscord = Join-Path $appData 'discord'
    $fullReset = (
        [Environment]::GetEnvironmentVariable('OPTIHUB_REPAIR_FULL') -eq '1' -or
        [Environment]::GetEnvironmentVariable('DISCOPT_REPAIR_FULL') -eq '1'
    )

    Write-Host ''
    Write-Host '  Discord Clean Reset (OptiHub repair)' -ForegroundColor Cyan
    Write-Host '  Fresh install + full cache purge. Login is preserved.' -ForegroundColor DarkGray
    Write-Host ''

    Write-RepStep 'Closing Discord...'
    Stop-RepairDiscord

    # 1) Brand new program files - never repair-in-place, always clean.
    Remove-RepairProgramFiles $discordRoot

    # 2) Purge every cache that survives a reinstall (the usual black-screen cause).
    Clear-RepairRendererState $appDataDiscord $fullReset

    # 2b) Remove the broken v1.1 custom theme (painted a black overlay over the
    # whole app) so it can't come back if Equicord is ever re-enabled.
    $equicordThemes = Join-Path $appData 'Equicord\themes'
    Get-ChildItem $equicordThemes -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'discopt-amoled*' -or $_.Name -like 'amoled-cord*' } |
        ForEach-Object {
            Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
            Write-RepOk "Removed theme: $($_.Name)"
        }

    # 3) Fresh install from discord.com.
    $app = Install-RepairFreshDiscord $discordRoot
    Write-RepOk "Discord $($app.Name) installed clean"

    # 4) Launch and verify with the user.
    Write-RepStep 'Starting Discord...'
    Start-RepairDiscord $discordRoot $app.FullName
    if (-not (Wait-RepairDiscordWindow 120)) {
        Write-RepWarn 'Discord window not detected yet - give it a minute.'
    }

    Write-Host ''
    Write-Host '  >>> Wait for Discord to finish loading (it may update modules first).' -ForegroundColor Yellow
    Write-Host ''
    if (Read-RepairYesNo '  Is Discord showing your servers/chat normally') {
        Write-Host ''
        Write-RepOk 'Repair complete. Discord is clean and working.'
        Write-Host ''
        return
    }

    # 5) Still black => GPU driver rendering issue. Disable hardware acceleration.
    Write-Host ''
    Write-RepStep 'Still black - disabling hardware acceleration and restarting Discord...'
    Stop-RepairDiscord
    Set-RepairHardwareAccelerationOff $appDataDiscord
    Start-RepairDiscord $discordRoot $app.FullName
    [void](Wait-RepairDiscordWindow 120)

    Write-Host ''
    if (Read-RepairYesNo '  Is Discord rendering normally now') {
        Write-Host ''
        Write-RepOk 'Fixed: your GPU driver was the cause. Hardware acceleration is now off.'
        Write-Host '    Tip: updating your graphics driver may let you re-enable it later' -ForegroundColor DarkGray
        Write-Host '    (Discord Settings > Advanced > Hardware Acceleration).' -ForegroundColor DarkGray
        Write-Host ''
        return
    }

    Write-Host ''
    Write-RepWarn 'Still not rendering. Two remaining options:'
    Write-Host '    1. Full reset including login (fixes corrupt session storage):' -ForegroundColor Yellow
    Write-Host '         $env:OPTIHUB_REPAIR_FULL = ''1''' -ForegroundColor Cyan
    Write-Host '         irm "https://raw.githubusercontent.com/BarcusEric/OptiHub/main/Repair-Discord.ps1" | iex' -ForegroundColor Cyan
    Write-Host '    2. Update your GPU drivers (NVIDIA/AMD/Intel), then start Discord again.' -ForegroundColor Yellow
    Write-Host ''
} catch {
    Write-Host ''
    Write-RepErr 'Repair failed.'
    Write-RepErr $_.Exception.Message
    Write-Host ''
    Write-Host 'Manual fallback: download the installer from https://discord.com/download and run it.' -ForegroundColor Yellow
    Write-Host ''
}
