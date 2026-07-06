# Repair-Discord.ps1 - restores a stock, bootable Discord after a bad optimization run.
# Safe to run repeatedly. Never touches your login/session data.
#
#   irm "https://raw.githubusercontent.com/BarcusEric/DiscOpti/main/Repair-Discord.ps1" | iex

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

function Restore-RepairStockAsar([string]$AppDir) {
    $resources = Join-Path $AppDir 'resources'
    if (-not (Test-Path $resources)) { return $false }

    $appAsar = Join-Path $resources 'app.asar'
    $candidates = @(
        (Join-Path $resources '_app.asar.stock'),
        (Join-Path $resources 'app.asar.backup'),
        (Join-Path $resources '_app.asar')
    )

    $currentOk = (Test-Path $appAsar) -and ((Get-Item $appAsar).Length -gt 1000000)
    if ($currentOk) {
        Write-RepOk 'app.asar is already the stock bootstrap'
    } else {
        $restored = $false
        foreach ($candidate in $candidates) {
            if ((Test-Path $candidate) -and ((Get-Item $candidate).Length -gt 1000000)) {
                Copy-Item $candidate $appAsar -Force
                Write-RepOk "Restored stock app.asar from $([IO.Path]::GetFileName($candidate))"
                $restored = $true
                break
            }
        }
        if (-not $restored) { return $false }
    }

    # Remove mod loader leftovers so stock bootstrap runs clean.
    $innerAsar = Join-Path $resources '_app.asar'
    if ((Test-Path $innerAsar) -and ((Get-Item $innerAsar).Length -lt 500000)) {
        Remove-Item $innerAsar -Force -ErrorAction SilentlyContinue
        Write-RepOk 'Removed OpenASAR loader (_app.asar)'
    }
    return $true
}

function Remove-RepairKernel([string]$AppDir) {
    $real = Join-Path $AppDir 'ffmpeg_real.dll'
    $current = Join-Path $AppDir 'ffmpeg.dll'
    if ((Test-Path $real) -and ((Get-Item $real).Length -gt 500000)) {
        Copy-Item $real $current -Force
        Write-RepOk 'Restored stock ffmpeg.dll'
    }
    foreach ($name in @('version.dll', 'version.dll.disabled', 'config.ini', 'config.ini.disabled')) {
        $path = Join-Path $AppDir $name
        if (Test-Path $path) {
            attrib -R $path 2>$null
            Remove-Item $path -Force -ErrorAction SilentlyContinue
            Write-RepOk "Removed $name"
        }
    }
}

function Repair-RepairSettingsJson([string]$AppDataDiscord) {
    $settingsPath = Join-Path $AppDataDiscord 'settings.json'
    if (-not (Test-Path $settingsPath)) { return }

    attrib -R $settingsPath 2>$null
    $settings = @{}
    try {
        $parsed = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($prop in $parsed.PSObject.Properties) { $settings[$prop.Name] = $prop.Value }
    } catch {
        Write-RepWarn 'settings.json was corrupt - resetting to safe defaults'
        $settings = @{}
    }

    foreach ($risky in @('openasar', 'chromiumSwitches')) {
        if ($settings.ContainsKey($risky)) {
            $settings.Remove($risky)
            Write-RepOk "Removed $risky block from settings.json"
        }
    }
    # Let the stock updater repair modules on next launch.
    $settings['SKIP_HOST_UPDATE'] = $false

    $json = $settings | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText($settingsPath, $json, [Text.UTF8Encoding]::new($false))
    Write-RepOk 'settings.json cleaned (updater re-enabled)'
}

function Test-RepairAppComplete([string]$AppDir) {
    # Older optimizer versions deleted Chromium rendering files, which causes a
    # blank/black Discord window. If any are missing, only a reinstall fixes it.
    foreach ($name in @(
        'Discord.exe', 'd3dcompiler_47.dll', 'vulkan-1.dll',
        'vk_swiftshader.dll', 'chrome_100_percent.pak'
    )) {
        if (-not (Test-Path (Join-Path $AppDir $name))) {
            Write-RepWarn "Missing $name (needed for rendering)"
            return $false
        }
    }
    return $true
}

function Start-RepairDiscord([string]$DiscordRoot, [string]$AppDir) {
    # Launch Discord.exe directly - Update.exe silently does nothing when its
    # Squirrel state is broken.
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

function Wait-RepairDiscordWindow([int]$TimeoutSec = 90) {
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

function Install-RepairFreshDiscord([string]$DiscordRoot) {
    Write-RepStep 'Downloading the official Discord installer (login is kept)...'
    $setup = Join-Path ([IO.Path]::GetTempPath()) 'DiscordSetup-repair.exe'
    $url = 'https://discord.com/api/downloads/distributions/app/installers/latest?channel=stable&platform=win&arch=x64'
    Invoke-WebRequest -Uri $url -OutFile $setup -UseBasicParsing -Headers @{ 'User-Agent' = 'DiscOpt-Repair/1.0' }
    if (-not (Test-Path $setup) -or ((Get-Item $setup).Length -lt 50000000)) {
        throw 'Discord installer download failed - check your internet connection'
    }

    Write-RepStep 'Reinstalling Discord (silent)...'
    Start-Process -FilePath $setup -ArgumentList '-s' -Wait | Out-Null

    $deadline = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $deadline) {
        $app = Get-RepairActiveApp $DiscordRoot
        if ($app -and (Test-Path (Join-Path $app.FullName 'Discord.exe'))) { return $app }
        Start-Sleep -Seconds 3
    }
    throw 'Discord reinstall did not complete - run the installer from discord.com manually'
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

    Write-Host ''
    Write-Host '  Discord Repair (DiscOpt rescue)' -ForegroundColor Magenta
    Write-Host '  Restores stock, bootable Discord. Login/session is preserved.' -ForegroundColor DarkGray
    Write-Host ''

    Write-RepStep 'Closing Discord...'
    Stop-RepairDiscord

    $app = $null
    if (Test-Path $discordRoot) { $app = Get-RepairActiveApp $discordRoot }

    $needReinstall = $true
    if ($app) {
        Write-RepStep "Repairing $($app.Name)..."
        Remove-RepairKernel $app.FullName
        $asarOk = Restore-RepairStockAsar $app.FullName
        $filesOk = Test-RepairAppComplete $app.FullName
        if ($asarOk -and $filesOk) {
            $needReinstall = $false
        } elseif (-not $asarOk) {
            Write-RepWarn 'No stock app.asar backup found - a clean reinstall is needed'
        } else {
            Write-RepWarn 'Rendering files were removed by an old debloat - reinstalling to restore them'
        }
    } else {
        Write-RepWarn 'No Discord installation found under %LOCALAPPDATA%\Discord'
    }

    if ($needReinstall) {
        if (Test-Path $discordRoot) {
            Write-RepStep 'Removing the broken install (your login lives elsewhere and is kept)...'
            Remove-Item $discordRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
        $app = Install-RepairFreshDiscord $discordRoot
        Write-RepOk "Discord $($app.Name) reinstalled"
        Remove-RepairKernel $app.FullName
    }

    Repair-RepairSettingsJson $appDataDiscord

    Write-RepStep 'Starting Discord (stock)...'
    Start-RepairDiscord $discordRoot $app.FullName

    if (Wait-RepairDiscordWindow 120) {
        Write-Host ''
        Write-RepOk 'Discord is open again. Repair complete.'
        Write-Host '    Your login and settings were preserved.' -ForegroundColor DarkGray
        Write-Host '    Discord may spend a minute updating its modules on this first launch.' -ForegroundColor DarkGray
        Write-Host ''
    } else {
        Write-Host ''
        Write-RepWarn 'Discord was repaired on disk but the window was not detected yet.'
        Write-RepWarn 'Give it a minute; if it still does not open, reinstall from https://discord.com/download'
        Write-Host ''
    }
} catch {
    Write-Host ''
    Write-RepErr 'Repair failed.'
    Write-RepErr $_.Exception.Message
    Write-Host ''
    Write-Host 'Manual fallback: download the installer from https://discord.com/download and run it.' -ForegroundColor Yellow
    Write-Host 'A reinstall keeps your login (it lives in %APPDATA%\discord, which is not deleted).' -ForegroundColor Yellow
    Write-Host ''
}
