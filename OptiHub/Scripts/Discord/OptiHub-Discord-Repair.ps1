# OptiHub non-interactive Discord repair (based on DiscOpti Repair-Discord.ps1)
# Restores stock, bootable Discord while preserving login by default.

param(
    [switch]$NonInteractive,
    [switch]$FullReset
)

$ErrorActionPreference = 'Stop'
$env:OPTIHUB = '1'

function Write-HubProgress([int]$Percent, [string]$Status) {
    Write-Host "OPTIHUB_PROGRESS:$Percent|$Status"
}
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
    Write-RepStep 'Removing Discord program files…'
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        Remove-Item $DiscordRoot -Recurse -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path $DiscordRoot)) { break }
        Start-Sleep -Seconds 2
        Stop-RepairDiscord
    }
    if (Test-Path $DiscordRoot) {
        throw "Could not delete $DiscordRoot — close Discord in Task Manager and retry"
    }
    Write-RepOk 'Old program files removed'
}

function Clear-RepairRendererState([string]$AppDataDiscord, [bool]$DoFullReset) {
    if (-not (Test-Path $AppDataDiscord)) {
        Write-RepOk 'No cached app data to clean'
        return
    }
    if ($DoFullReset) {
        Write-RepStep 'FULL reset — clearing app data including login…'
        Remove-Item $AppDataDiscord -Recurse -Force -ErrorAction SilentlyContinue
        Write-RepOk 'App data fully cleared'
        return
    }
    $keep = @('Local Storage', 'IndexedDB', 'Cookies', 'Cookies-journal', 'databases', 'Network')
    Write-RepStep 'Purging renderer caches (login kept)…'
    $removed = 0
    Get-ChildItem $AppDataDiscord -Force -ErrorAction SilentlyContinue | ForEach-Object {
        if ($keep -contains $_.Name) { return }
        attrib -R $_.FullName 2>$null
        Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        $removed++
    }
    Write-RepOk "Renderer state purged ($removed item(s))"
}

function Install-RepairFreshDiscord([string]$DiscordRoot) {
    Write-RepStep 'Downloading official Discord installer…'
    $setup = Join-Path ([IO.Path]::GetTempPath()) 'DiscordSetup-optihub-repair.exe'
    if (Test-Path $setup) { Remove-Item $setup -Force -ErrorAction SilentlyContinue }
    $url = 'https://discord.com/api/downloads/distributions/app/installers/latest?channel=stable&platform=win&arch=x64'
    Invoke-WebRequest -Uri $url -OutFile $setup -UseBasicParsing -Headers @{ 'User-Agent' = 'OptiHub-Repair/1.0' }
    if (-not (Test-Path $setup) -or ((Get-Item $setup).Length -lt 50000000)) {
        throw 'Discord installer download failed — check your internet connection'
    }
    Write-RepStep 'Installing Discord (silent)…'
    Start-Process -FilePath $setup -ArgumentList '-s' -Wait | Out-Null
    $deadline = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $deadline) {
        $app = Get-RepairActiveApp $DiscordRoot
        if ($app -and (Test-Path (Join-Path $app.FullName 'Discord.exe'))) { return $app }
        Start-Sleep -Seconds 3
    }
    throw 'Discord install did not complete — run the installer from discord.com manually'
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

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Repair must run on Windows.'
    }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    $localAppData = [Environment]::GetEnvironmentVariable('LOCALAPPDATA')
    $appData = [Environment]::GetEnvironmentVariable('APPDATA')
    $discordRoot = Join-Path $localAppData 'Discord'
    $appDataDiscord = Join-Path $appData 'discord'
    $doFull = $FullReset -or
        ([Environment]::GetEnvironmentVariable('OPTIHUB_REPAIR_FULL') -eq '1') -or
        ([Environment]::GetEnvironmentVariable('DISCOPT_REPAIR_FULL') -eq '1')

    Write-Host ''
    Write-Host '  OptiHub · Discord Clean Reset' -ForegroundColor Cyan
    Write-Host '  Stock reinstall + cache purge. Login preserved by default.' -ForegroundColor DarkGray
    Write-Host ''

    Write-HubProgress 5 'Closing Discord…'
    Write-RepStep 'Closing Discord…'
    Stop-RepairDiscord

    Write-HubProgress 20 'Removing program files…'
    Remove-RepairProgramFiles $discordRoot

    Write-HubProgress 40 'Clearing renderer state…'
    Clear-RepairRendererState $appDataDiscord $doFull

    $equicordThemes = Join-Path $appData 'Equicord\themes'
    Get-ChildItem $equicordThemes -Filter 'discopt-amoled*.css' -ErrorAction SilentlyContinue |
        ForEach-Object {
            Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
            Write-RepOk "Removed theme: $($_.Name)"
        }

    Write-HubProgress 55 'Installing fresh Discord…'
    $app = Install-RepairFreshDiscord $discordRoot
    Write-RepOk "Discord $($app.Name) installed clean"

    Write-HubProgress 85 'Starting Discord…'
    Write-RepStep 'Starting Discord…'
    Start-RepairDiscord $discordRoot $app.FullName

    Write-RepOk 'Repair complete. Wait for Discord to finish loading.'
    Write-HubProgress 100 'Repair complete'
    exit 0
} catch {
    Write-RepErr 'Repair failed.'
    Write-RepErr $_.Exception.Message
    Write-HubProgress 100 'Repair failed'
    Write-Host 'Manual fallback: https://discord.com/download' -ForegroundColor Yellow
    exit 1
}
