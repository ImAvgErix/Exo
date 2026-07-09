# OptiHub non-interactive Discord repair
# Restores stock, bootable Discord while preserving login by default.
# ASCII-only source so Windows PowerShell and pwsh never mis-parse punctuation.

param(
    [switch]$NonInteractive,
    [switch]$FullReset
)

$ErrorActionPreference = 'Stop'
$env:OPTIHUB = '1'
$env:DISCOPT_NONINTERACTIVE = '1'

function Write-HubProgress([int]$Percent, [string]$Status) {
    $p = [Math]::Max(0, [Math]::Min(100, $Percent))
    $line = "OPTIHUB_PROGRESS:$p|$Status"
    [Console]::Out.WriteLine($line)
    [Console]::Out.Flush()
    if ($env:OPTIHUB_LOG) {
        try {
            $dir = Split-Path -Parent $env:OPTIHUB_LOG
            if ($dir -and (Test-Path -LiteralPath $dir)) {
                Add-Content -LiteralPath $env:OPTIHUB_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue
            }
        } catch { }
    }
}
function Write-RepStep([string]$Msg) { Write-Host "[*] $Msg" -ForegroundColor Cyan }
function Write-RepOk([string]$Msg)   { Write-Host "[+] $Msg" -ForegroundColor Green }
function Write-RepWarn([string]$Msg) { Write-Host "[!] $Msg" -ForegroundColor Yellow }
function Write-RepErr([string]$Msg)  { Write-Host "[-] $Msg" -ForegroundColor Red }

function Stop-RepairDiscord {
    $names = @('Discord', 'Discord.bin', 'Update', 'app')
    for ($round = 1; $round -le 4; $round++) {
        $procs = @(Get-Process -Name $names -ErrorAction SilentlyContinue)
        if ($procs.Count -eq 0) { break }
        foreach ($p in $procs) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
            try { & taskkill.exe /F /T /PID $p.Id 2>$null | Out-Null } catch { }
        }
        Start-Sleep -Milliseconds (250 * $round)
    }
    try { & taskkill.exe /F /IM Discord.exe /T 2>$null | Out-Null } catch { }
    try { & taskkill.exe /F /IM Update.exe /T 2>$null | Out-Null } catch { }
    Start-Sleep -Milliseconds 600
}

function Get-RepairActiveApp([string]$DiscordRoot) {
    Get-ChildItem -LiteralPath $DiscordRoot -Directory -Filter 'app-*' -ErrorAction SilentlyContinue |
        Sort-Object {
            try { [version]($_.Name -replace '^app-', '') }
            catch { [version]'0.0.0.0' }
        } -Descending |
        Select-Object -First 1
}

function Remove-RepairProgramFiles([string]$DiscordRoot) {
    if (-not (Test-Path -LiteralPath $DiscordRoot)) {
        Write-RepOk 'No old program files to remove'
        return
    }
    Write-RepStep 'Removing Discord program files...'
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        Stop-RepairDiscord
        try {
            Get-ChildItem -LiteralPath $DiscordRoot -Recurse -Force -ErrorAction SilentlyContinue |
                ForEach-Object {
                    try { attrib -R -S -H $_.FullName 2>$null } catch { }
                }
        } catch { }
        Remove-Item -LiteralPath $DiscordRoot -Recurse -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path -LiteralPath $DiscordRoot)) { break }
        Start-Sleep -Seconds 2
    }
    if (Test-Path -LiteralPath $DiscordRoot) {
        throw "Could not delete $DiscordRoot - close Discord in Task Manager and retry"
    }
    Write-RepOk 'Old program files removed'
}

function Clear-RepairRendererState([string]$AppDataDiscord, [bool]$DoFullReset) {
    if (-not (Test-Path -LiteralPath $AppDataDiscord)) {
        Write-RepOk 'No cached app data to clean'
        return
    }
    if ($DoFullReset) {
        Write-RepStep 'FULL reset - clearing app data including login...'
        Remove-Item -LiteralPath $AppDataDiscord -Recurse -Force -ErrorAction SilentlyContinue
        Write-RepOk 'App data fully cleared'
        return
    }
    # Keep login/session folders only.
    $keep = @('Local Storage', 'IndexedDB', 'Cookies', 'Cookies-journal', 'databases', 'Network')
    Write-RepStep 'Purging renderer caches (login kept)...'
    $removed = 0
    Get-ChildItem -LiteralPath $AppDataDiscord -Force -ErrorAction SilentlyContinue | ForEach-Object {
        if ($keep -contains $_.Name) { return }
        try { attrib -R $_.FullName 2>$null } catch { }
        Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        $removed++
    }
    Write-RepOk "Renderer state purged ($removed item(s))"
}

function Install-RepairFreshDiscord([string]$DiscordRoot) {
    Write-RepStep 'Downloading official Discord installer...'
    $setup = Join-Path ([IO.Path]::GetTempPath()) 'DiscordSetup-optihub-repair.exe'
    if (Test-Path -LiteralPath $setup) {
        Remove-Item -LiteralPath $setup -Force -ErrorAction SilentlyContinue
    }
    $url = 'https://discord.com/api/downloads/distributions/app/installers/latest?channel=stable&platform=win&arch=x64'
    $headers = @{ 'User-Agent' = 'OptiHub-Repair/1.0' }
    try {
        Invoke-WebRequest -Uri $url -OutFile $setup -UseBasicParsing -Headers $headers
    } catch {
        throw "Discord installer download failed - check your internet connection ($($_.Exception.Message))"
    }
    if (-not (Test-Path -LiteralPath $setup) -or ((Get-Item -LiteralPath $setup).Length -lt 50000000)) {
        throw 'Discord installer download failed - file missing or too small'
    }
    Write-RepStep 'Installing Discord (silent)...'
    $p = Start-Process -FilePath $setup -ArgumentList '-s' -PassThru -WindowStyle Hidden
    if ($null -eq $p) { throw 'Failed to start Discord installer' }
    $p.WaitForExit()
    $deadline = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $deadline) {
        $app = Get-RepairActiveApp $DiscordRoot
        if ($app -and (Test-Path -LiteralPath (Join-Path $app.FullName 'Discord.exe'))) {
            return $app
        }
        Start-Sleep -Seconds 3
    }
    throw 'Discord install did not complete - run the installer from discord.com manually'
}

function Start-RepairDiscord([string]$DiscordRoot, [string]$AppDir) {
    $exe = Join-Path $AppDir 'Discord.exe'
    if (Test-Path -LiteralPath $exe) {
        # Launch via explorer so Discord does not inherit an elevated token.
        try {
            Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$exe`"" | Out-Null
            return
        } catch { }
        Start-Process -FilePath $exe -WorkingDirectory $AppDir | Out-Null
        return
    }
    $updateExe = Join-Path $DiscordRoot 'Update.exe'
    if (Test-Path -LiteralPath $updateExe) {
        Start-Process -FilePath $updateExe -ArgumentList '--processStart', 'Discord.exe' -WorkingDirectory $DiscordRoot | Out-Null
    }
}

function Remove-BrokenThemes([string]$AppDataRoot) {
    $equicordThemes = Join-Path $AppDataRoot 'Equicord\themes'
    if (-not (Test-Path -LiteralPath $equicordThemes)) { return }
    Get-ChildItem -LiteralPath $equicordThemes -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'discopt-amoled*' -or $_.Name -like 'amoled-cord*' } |
        ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
            Write-RepOk "Removed theme: $($_.Name)"
        }
}

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Repair must run on Windows.'
    }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    $localAppData = [Environment]::GetEnvironmentVariable('LOCALAPPDATA')
    $appData = [Environment]::GetEnvironmentVariable('APPDATA')
    if (-not $localAppData -or -not $appData) {
        throw 'LOCALAPPDATA/APPDATA environment variables are not set.'
    }

    $discordRoot = Join-Path $localAppData 'Discord'
    $appDataDiscord = Join-Path $appData 'discord'
    $doFull = $FullReset -or
        ([Environment]::GetEnvironmentVariable('OPTIHUB_REPAIR_FULL') -eq '1') -or
        ([Environment]::GetEnvironmentVariable('DISCOPT_REPAIR_FULL') -eq '1')

    Write-Host ''
    Write-Host '  OptiHub - Discord Clean Reset' -ForegroundColor Cyan
    Write-Host '  Stock reinstall + cache purge. Login preserved by default.' -ForegroundColor DarkGray
    Write-Host ''

    Write-HubProgress 5 'Closing Discord...'
    Write-RepStep 'Closing Discord...'
    Stop-RepairDiscord

    Write-HubProgress 20 'Removing program files...'
    Remove-RepairProgramFiles $discordRoot

    Write-HubProgress 40 'Clearing renderer state...'
    Clear-RepairRendererState $appDataDiscord $doFull
    Remove-BrokenThemes $appData

    Write-HubProgress 55 'Installing fresh Discord...'
    $app = Install-RepairFreshDiscord $discordRoot
    Write-RepOk "Discord $($app.Name) installed clean"

    Write-HubProgress 85 'Starting Discord...'
    Write-RepStep 'Starting Discord...'
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
