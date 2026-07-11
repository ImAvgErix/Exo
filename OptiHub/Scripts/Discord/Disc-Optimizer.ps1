# Disc Optimizer - right-click -> Run with PowerShell (uses stable PS 7 + elevates)
# First run installs + optimizes Discord. After that, use the Start menu Discord shortcut.
#
#   Disc-Optimizer.ps1           first-time / full setup (log in when prompted)
#   Disc-Optimizer.ps1 -Launch   start Discord only (daily)
#   Disc-Optimizer.ps1 -Quick    re-apply after a Discord update
#   Disc-Optimizer.ps1 -SkipCacheClean

param(
    [switch]$Launch,
    [switch]$SkipDebloat,
    [switch]$ForceDebloat,
    [switch]$SkipEquicord,
    [switch]$SkipOpenAsar,
    [switch]$SkipKernel,
    [switch]$NoLaunch,
    [switch]$VerifyOnly,
    [switch]$SkipDiscordInstall,
    [switch]$FreshInstall,
    [switch]$Quick,
    [switch]$SkipManifestSync,
    [switch]$SkipCacheClean
)

if ($Launch) {
    $SkipDiscordInstall = $true
    $SkipManifestSync = $true
    $NoLaunch = $true
    $SkipDebloat = $true
    $SkipEquicord = $true
    $SkipOpenAsar = $true
}

if ($Quick) {
    $SkipDiscordInstall = $true
    $SkipManifestSync = $true
}

if ($env:OPTIHUB -eq '1' -or $env:DISCOPT_NONINTERACTIVE -eq '1') {
    $NoLaunch = $true
    # Prefer bundled manifests for speed; Sync-PluginManifests still downloads if missing.
    if (-not $PSBoundParameters.ContainsKey('SkipManifestSync')) {
        $SkipManifestSync = $true
    }
}

$ErrorActionPreference = 'Stop'
$Script:DiscOptVersion = '1.3.3'
$Script:SelfPath = $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent $Script:SelfPath
$KitDir = Join-Path $Root 'kit'
if (-not (Test-Path $KitDir)) { $KitDir = $Root }
$ToolsDir = Join-Path $KitDir 'tools'
$DownloadDir = Join-Path $KitDir 'downloads'
$BootstrapLogDir = Join-Path $KitDir 'logs'
$Script:DiscOptPwshReleaseApi = 'https://api.github.com/repos/PowerShell/PowerShell/releases/latest'

function Test-DiscOptIsWindows {
    return [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
}

function Test-DiscOptIsElevated {
    if (-not (Test-DiscOptIsWindows)) { return $false }
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal $id
        return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch {
        return $false
    }
}

function Get-DiscOptEnvPath([string]$Name, [string]$Child = '') {
    $base = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($base)) { return $null }
    if ([string]::IsNullOrWhiteSpace($Child)) { return $base }
    return (Join-Path $base $Child)
}

function Get-DiscOptTempPath([string]$Child = '') {
    $base = Get-DiscOptEnvPath 'TEMP'
    if (-not $base) { $base = [IO.Path]::GetTempPath() }
    if ([string]::IsNullOrWhiteSpace($Child)) { return $base }
    return (Join-Path $base $Child)
}

function ConvertTo-DiscOptProcessArgument([string]$Arg) {
    if ($null -eq $Arg) { return '""' }
    if ($Arg -notmatch '[\s"]') { return $Arg }
    return '"' + ($Arg -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

function Join-DiscOptProcessArguments([string[]]$Args) {
    return (($Args | ForEach-Object { ConvertTo-DiscOptProcessArgument $_ }) -join ' ')
}

function Wait-DiscOptClosePrompt {
    param([string]$Prompt = 'Press Enter to close...')

    # OptiHub / non-interactive must never block on a keypress.
    if ($env:OPTIHUB -eq '1' -or $env:DISCOPT_NONINTERACTIVE -eq '1' -or $NoLaunch) {
        return
    }

    try {
        Write-Host $Prompt
        Read-Host | Out-Null
    } catch {
        Start-Sleep -Seconds 8
    }
}

function Write-DiscOptBootstrapFailure($ErrorRecord) {
    try {
        if (-not (Test-Path $BootstrapLogDir)) {
            New-Item -ItemType Directory -Path $BootstrapLogDir -Force | Out-Null
        }
        $err = $ErrorRecord.Exception
        $inv = $ErrorRecord.InvocationInfo
        $body = @(
            '',
            ('=' * 60),
            "BOOTSTRAP FAILED: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
            "Message: $($err.Message)",
            "Type: $($err.GetType().FullName)",
            "Line: $($inv.ScriptLineNumber)",
            "Command: $($inv.Line.Trim())",
            "Position: $($inv.PositionMessage)",
            "Stack: $($err.StackTrace)",
            ('=' * 60),
            ''
        ) -join [Environment]::NewLine
        Set-Content -Path (Join-Path $BootstrapLogDir 'last-error.log') -Value $body -Encoding UTF8
    } catch {}
}

function Test-DiscOptPwshVersionMeetsMinimum([string]$VersionText) {
    if (-not $VersionText) { return $false }
    if ($VersionText -match '^(\d+)\.') {
        $major = [int]$Matches[1]
        return $major -ge 7
    }
    return $false
}

function Get-DiscOptPwshVersion([string]$Exe) {
    if (-not (Test-Path $Exe)) { return $null }
    try {
        $raw = & $Exe -NoProfile -Command '$PSVersionTable.PSVersion.ToString()' 2>$null
        if ($raw -and (Test-DiscOptPwshVersionMeetsMinimum $raw)) { return "$raw".Trim() }
    } catch {}
    return $null
}

function Get-DiscOptPwsh7 {
    $candidates = [System.Collections.Generic.List[string]]::new()
    $portable = Join-Path $ToolsDir 'pwsh\pwsh.exe'
    if (Test-Path $portable) { $candidates.Add($portable) }

    $cmd = Get-Command pwsh -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { $candidates.Add($cmd.Source) }

    foreach ($path in @(
        (Get-DiscOptEnvPath 'ProgramFiles' 'PowerShell\7\pwsh.exe'),
        (Get-DiscOptEnvPath 'ProgramFiles' 'PowerShell\7-preview\pwsh.exe'),
        (Get-DiscOptEnvPath 'LOCALAPPDATA' 'Microsoft\WindowsApps\pwsh.exe')
    )) {
        if ($path) { $candidates.Add($path) }
    }

    $appsRoot = Get-DiscOptEnvPath 'ProgramFiles' 'WindowsApps'
    if ($appsRoot -and (Test-Path $appsRoot)) {
        Get-ChildItem $appsRoot -Directory -Filter 'Microsoft.PowerShell*' -ErrorAction SilentlyContinue |
            ForEach-Object {
                $exe = Join-Path $_.FullName 'pwsh.exe'
                if (Test-Path $exe) { $candidates.Add($exe) }
            }
    }

    foreach ($exe in ($candidates | Select-Object -Unique)) {
        $ver = Get-DiscOptPwshVersion $exe
        if ($ver) {
            return @{ Exe = $exe; Version = $ver }
        }
    }
    return $null
}

function Get-DiscOptLatestPwshAsset {
    $headers = @{ 'User-Agent' = 'OptiHub-Discord/1.2' }
    $release = Invoke-RestMethod -Uri $Script:DiscOptPwshReleaseApi -Headers $headers -TimeoutSec 45
    $asset = $release.assets |
        Where-Object { $_.name -match '^PowerShell-\d+\.\d+\.\d+-win-x64\.zip$' } |
        Select-Object -First 1
    $hashes = $release.assets | Where-Object { $_.name -eq 'hashes.sha256' } | Select-Object -First 1
    if (-not $asset -or -not $hashes) {
        throw 'Latest stable PowerShell release is missing its x64 ZIP or checksum manifest'
    }
    return @{
        Name      = [string]$asset.name
        Url       = [string]$asset.browser_download_url
        HashesUrl = [string]$hashes.browser_download_url
        Version   = ([string]$release.tag_name).TrimStart('v')
    }
}

function Install-DiscOptPwsh7Portable {
    if (-not (Test-Path $ToolsDir)) { New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null }
    if (-not (Test-Path $DownloadDir)) { New-Item -ItemType Directory -Path $DownloadDir -Force | Out-Null }

    $destDir = Join-Path $ToolsDir 'pwsh'
    $pwshExe = Join-Path $destDir 'pwsh.exe'
    $cachedVer = Get-DiscOptPwshVersion $pwshExe
    if ($cachedVer) {
        return @{ Exe = $pwshExe; Version = $cachedVer }
    }

    $asset = Get-DiscOptLatestPwshAsset
    $zipPath = Join-Path $DownloadDir $asset.Name
    $hashPath = Join-Path $DownloadDir 'powershell-hashes.sha256'
    $ua = @{ 'User-Agent' = 'OptiHub-Discord/1.2' }
    if (-not (Test-Path -LiteralPath $zipPath) -or (Get-Item -LiteralPath $zipPath).Length -lt 50000000) {
        Write-Host "[*] Downloading portable PowerShell $($asset.Version)..." -ForegroundColor Cyan
        $partial = "$zipPath.partial"
        Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
        Invoke-WebRequest -Uri $asset.Url -OutFile $partial -UseBasicParsing -Headers $ua -TimeoutSec 180
        if ((Get-Item -LiteralPath $partial).Length -lt 50000000) {
            Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
            throw 'Portable PowerShell download is incomplete'
        }
        Move-Item -LiteralPath $partial -Destination $zipPath -Force
    }

    Invoke-WebRequest -Uri $asset.HashesUrl -OutFile $hashPath -UseBasicParsing -Headers $ua -TimeoutSec 45
    $hashLine = Get-Content -LiteralPath $hashPath -ErrorAction Stop |
        Where-Object { $_ -match ('(?i)\*?' + [regex]::Escape($asset.Name) + '$') } |
        Select-Object -First 1
    $expectedHash = if ($hashLine -match '^([0-9a-fA-F]{64})\s+') { $Matches[1] } else { $null }
    $actualHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    Remove-Item -LiteralPath $hashPath -Force -ErrorAction SilentlyContinue
    if (-not $expectedHash -or $actualHash -ine $expectedHash) {
        Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
        throw 'Portable PowerShell checksum verification failed'
    }

    $tempDir = Join-Path $DownloadDir 'pwsh-extract'
    if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue }
    Expand-Archive -Path $zipPath -DestinationPath $tempDir -Force
    $sourceExe = Get-ChildItem $tempDir -Recurse -Filter 'pwsh.exe' -File -ErrorAction SilentlyContinue |
        Sort-Object { $_.FullName.Length } |
        Select-Object -First 1
    if (-not $sourceExe) {
        throw 'Portable PowerShell ZIP did not contain pwsh.exe'
    }
    $sourceDir = Split-Path -Parent $sourceExe.FullName

    if (Test-Path $destDir) { Remove-Item $destDir -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    Copy-Item -Path (Join-Path $sourceDir '*') -Destination $destDir -Recurse -Force
    Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue

    $ver = Get-DiscOptPwshVersion $pwshExe
    if ($ver) {
        return @{ Exe = $pwshExe; Version = $ver }
    }
    return $null
}

function Install-DiscOptPwsh7 {
    if (Test-DiscOptIsElevated) {
        $winget = Get-Command winget -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($winget) {
            Write-Host '[*] Installing stable PowerShell 7 via winget...' -ForegroundColor Cyan
            $proc = Start-Process -FilePath $winget.Source -ArgumentList @(
                'install', '-e', '-id', 'Microsoft.PowerShell',
                '-accept-package-agreements', '-accept-source-agreements', '-silent'
            ) -PassThru -WindowStyle Hidden
            if (-not $proc.WaitForExit(600000)) {
                try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
                Write-Host '[!] winget PowerShell install timed out - trying portable download' -ForegroundColor Yellow
            } elseif ($proc.ExitCode -ne 0) {
                Write-Host '[!] winget install returned non-zero - trying portable download' -ForegroundColor Yellow
            } else {
                Start-Sleep -Seconds 3
                $found = Get-DiscOptPwsh7
                if ($found) { return $found }
            }
        }
    }

    return Install-DiscOptPwsh7Portable
}

function Get-DiscOptBoundScriptArgs {
    $args = @()
    foreach ($key in $PSBoundParameters.Keys) {
        if ($PSBoundParameters[$key] -eq $true) {
            $args += "-$key"
        } elseif ($null -ne $PSBoundParameters[$key] -and $PSBoundParameters[$key] -ne $false) {
            $args += "-$key", "$($PSBoundParameters[$key])"
        }
    }
    return $args
}

function Initialize-DiscOptRuntime {
    if (-not (Test-DiscOptIsWindows)) {
        throw 'Disc Optimizer must be run on 64-bit Windows with Discord Desktop installed.'
    }

    if ($PSVersionTable.PSVersion.Major -lt 7) { $env:DISCOPT_PS7 = '1' }

    $isCore7 = ($PSVersionTable.PSEdition -eq 'Core') -and (Test-DiscOptPwshVersionMeetsMinimum $PSVersionTable.PSVersion.ToString())
    $isAdmin = Test-DiscOptIsElevated
    $needElevate = (-not $Launch) -and (-not $isAdmin)

    # Fast path for normal launches and already-elevated OptiHub runs. Avoid
    # spawning another pwsh merely to query the version we are already using.
    if ($isCore7 -and -not $needElevate) {
        $env:DISCOPT_RUNTIME_READY = '1'
        if ($isAdmin) { $env:DISCOPT_ELEVATED = '1' }
        return
    }

    $pwshInfo = Get-DiscOptPwsh7
    if (-not $pwshInfo) {
        Write-Host ''
        Write-Host '  Disc Optimizer - PowerShell 7 setup' -ForegroundColor Magenta
        Write-Host '[*] PowerShell 7 not found - installing the current stable release...' -ForegroundColor Cyan
        $pwshInfo = Install-DiscOptPwsh7
        if (-not $pwshInfo) {
            Write-Host '[-] Could not install PowerShell 7. Check internet and try again.' -ForegroundColor Red
            Wait-DiscOptClosePrompt
            exit 1
        }
        Write-Host "[+] Installed PowerShell $($pwshInfo.Version)" -ForegroundColor Green
    }

    $extraArgs = Get-DiscOptBoundScriptArgs
    $baseArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass')

    if ($needElevate) {
        Write-Host '[*] Requesting Administrator (needed for Discord optimization)...' -ForegroundColor Cyan
        $allArgs = $baseArgs + @('-File', $Script:SelfPath) + $extraArgs
        Start-Process -FilePath $pwshInfo.Exe -Verb RunAs -ArgumentList (Join-DiscOptProcessArguments $allArgs) -WorkingDirectory $Root | Out-Null
        exit 0
    }

    if ($Launch) {
        $allArgs = $baseArgs + @('-WindowStyle', 'Hidden', '-File', $Script:SelfPath, '-Launch')
        Start-Process -FilePath $pwshInfo.Exe -ArgumentList (Join-DiscOptProcessArguments $allArgs) -WindowStyle Hidden -WorkingDirectory $Root | Out-Null
        exit 0
    }

    $allArgs = $baseArgs + @('-File', $Script:SelfPath) + $extraArgs
    & $pwshInfo.Exe @allArgs
    if ($null -eq $LASTEXITCODE) { exit 1 }
    exit $LASTEXITCODE
}

if (-not $env:DISCOPT_RUNTIME_READY) {
    try {
        Initialize-DiscOptRuntime
    } catch {
        Write-DiscOptBootstrapFailure $_
        Write-Host ''
        Write-Host 'Disc Optimizer failed before setup could start.' -ForegroundColor Red
        Write-Host $_.Exception.Message -ForegroundColor Red
        Write-Host "Error log: $(Join-Path $BootstrapLogDir 'last-error.log')" -ForegroundColor Yellow
        Write-Host ''
        Wait-DiscOptClosePrompt
        exit 1
    }
}

$Profiles = Join-Path $KitDir 'profiles'
$Themes = Join-Path $KitDir 'themes'
$LogDir = Join-Path $KitDir 'logs'
$DiscordRoot = Get-DiscOptEnvPath 'LOCALAPPDATA' 'Discord'
$AppData = Get-DiscOptEnvPath 'APPDATA' 'discord'
$EquicordData = Get-DiscOptEnvPath 'APPDATA' 'Equicord'
$Script:LogPath = $null
$Script:DiscordInstalledThisRun = $false
$Script:KernelRolledBack = $false
$Script:ModsRolledBack = $false

$Protected = @(
    'version.dll', 'config.ini', 'Discord.exe', 'ffmpeg.dll', 'ffmpeg_real.dll',
    'Discord.bin.exe', 'Update.exe', 'app.asar', '_app.asar', '_app.asar.stock'
)
$OpenAsarUrl = 'https://github.com/GooseMod/OpenAsar/releases/download/nightly/app.asar'
$DiscordSetupUrl = 'https://discord.com/api/downloads/distributions/app/installers/latest?channel=stable&platform=win&arch=x64'
# Modern Discord (1.0.92xx+) no longer ships discord_dispatch / discord_modules.
# Only require modules that always exist on a healthy stable install.
$RequiredModules = @(
    'discord_desktop_core-1',
    'discord_utils-1',
    'discord_voice-1',
    'discord_media-1'
)
# Known nonessential modules removed by the no-compromise debloat pass. Never
# remove unknown modules: Discord can add new boot dependencies at any time.
$OptionalModules = @(
    'discord_hook-1',
    'discord_clips-1'
)
$RuntimeModules = @('discord_notifications')
# The original, battle-tested AMOLED theme. Do NOT swap in broad
# [class*="..."] selector themes: painting layerContainer black covers the
# whole app with a black overlay (tooltips still show, everything else hidden).
$EnabledTheme = 'amoled-cord.theme.css'
$ForceDisabledPlugins = @(
    # StreamerModeOn forces Discord Streamer Mode while live even if the user turned it off.
    'StreamerModeOn',
    # Lean minimalism: cut convenience / UI chrome plugins (overhead for little gain)
    'ImageZoom', 'ViewIcons', 'CopyUserURLs', 'ReadAllNotificationsButton',
    'FixImagesQuality', 'CallTimer', 'RoleColorEverywhere', 'ShowTimeouts',
    'FixCodeblockGap', 'FixFileExtensions', 'BetterGifPicker', 'MessageClickActions',
    'GameActivityToggle',
    'BlockKrisp', 'AltKrispSwitch', 'RelationshipNotifier',
    'Dearrow', 'ImplicitRelationships', 'OpenInApp', 'SplitLargeMessages', 'EquicordToolbox',
    'IdleAutoRestart', 'FixSpotifyEmbeds', 'ReplaceGoogleSearch', 'SupportHelper',
    'BetterUploadButton', 'FixYoutubeEmbeds',
    'ConcatenatedComponentExtractor', 'CancelFriendRequest',
    'StartupTimings', 'NewPluginsManager', 'WebKeybinds', 'WebScreenShareFixes',
    'MessageNotifier', 'KeywordNotify', 'ReplyPingControl', 'BypassStatus', 'PingNotifications',
    'NotificationTitle', 'ToastNotifications', 'VoiceJoinMessages', 'VcNarrator', 'VcNarratorCustom',
    'XSOverlay', 'VoiceChannelLog', 'VoiceStats', 'Streaks', 'FriendshipRanks',
    'DisableCameras'
)
# Pure caches only - never touches web app storage, service workers, or session data.
$SafeCacheTargets = @(
    'Cache', 'Code Cache', 'GPUCache', 'ShaderCache', 'DawnCache', 'GraphiteDawnCache',
    'VideoDecodeStats', 'Media Cache', 'logs', 'Crashpad', 'crashpad', 'debug', 'sentry'
)


# Load modular function library (keeps this entrypoint thin).
$script:DiscOptLibDir = Join-Path $KitDir 'lib'
if (-not (Test-Path -LiteralPath $script:DiscOptLibDir)) { throw "Missing kit/lib at $script:DiscOptLibDir" }
Get-ChildItem -LiteralPath $script:DiscOptLibDir -Filter '*.ps1' | Sort-Object Name | ForEach-Object {
    . $_.FullName
}

# - main -
try {
# Fast daily launch: no network init, no banner/kit maintenance. Only heal if
# OpenAsar/kernel/Equicord files are missing (Discord update), then start.
if ($Launch) {
    $app = Assert-DiscordInstall
    try {
        $resources = Join-Path $app.FullName 'resources'
        $openAsarOk = Test-OpenAsarInstalled $resources
        $kernelOk = (Test-Path (Join-Path $app.FullName 'version.dll')) -and
            (Test-Path (Join-Path $app.FullName 'config.ini')) -and
            (Test-Path (Join-Path $app.FullName 'ffmpeg_real.dll')) -and
            (Test-Path (Join-Path $app.FullName 'ffmpeg.dll')) -and
            ((Get-Item (Join-Path $app.FullName 'ffmpeg.dll') -ErrorAction SilentlyContinue).Length -lt 500000)
        # Skip heavy EquicordReady (settings parse) when loader + asar look present.
        $eqAsar = Join-Path $EquicordData 'equicord.asar'
        $equicordOk = (Test-Path -LiteralPath $eqAsar) -and
            ((Get-Item -LiteralPath $eqAsar -ErrorAction SilentlyContinue).Length -gt 1000000) -and
            (Test-EquicordLoaderPatched $app.FullName)
        if (-not $openAsarOk -or -not $kernelOk -or -not $equicordOk) {
            Write-Host '[*] Discord client changed - restoring OptiHub mods...'
            Stop-Discord
            if (-not $openAsarOk -or -not $equicordOk) {
                try { Install-Equicord $app.FullName } catch {
                    try { Install-OpenAsar $app.FullName } catch { }
                    try { Apply-EquicordProfile -AppDir $app.FullName } catch { }
                }
            }
            if (-not $kernelOk) {
                try { Install-DiscOptKernel $app.FullName } catch { }
            }
            try { Restore-StartMenu } catch { }
        }
    } catch { }
    Start-Discord $app.FullName
    exit 0
}

Initialize-Network
Initialize-DiscOptimizerLog
Invoke-KitMaintenance
Write-Banner
Test-KitIntegrity

if ($VerifyOnly) {
    Write-Ok 'Verify-only passed. Kit is ready - run Disc-Optimizer.ps1 on any PC with internet.'
    Write-Host '  The script will download Discord, then apply all mods automatically.'
    Write-LogLine 'OK' 'Verify-only finished successfully'
    exit 0
}

Stop-Discord
Unlock-DiscordSettings
Confirm-WindowsDiscordTarget
$Script:DiscordWindowsRecovery = Initialize-DiscordApplyState

# 1) Discord - restore stock + update latest, or fresh install (-FreshInstall)
Prepare-Discord

Stop-Discord
$app = Assert-DiscordInstall
Write-Step "Target: $($app.FullName)"

if (-not (Test-DiscordModulesReady $app.FullName)) {
    Write-Warn 'Modules still missing - running stock first-run...'
    Initialize-DiscordModules $app.FullName
}

# Login gate - verify session before touching settings or mods (never clears session data)
Ensure-DiscordLoggedIn $app.FullName

foreach ($name in @('version.dll', 'config.ini')) {
    $disabled = Join-Path $app.FullName "$name.disabled"
    if (Test-Path $disabled) { Remove-Item $disabled -Force -ErrorAction SilentlyContinue }
}

# 2) Debloat, then Krisp (dropdown needs module; BlockKrisp stays off)
$freed = [ref]0L
if ($SkipDebloat) {
    Write-Ok 'Debloat skipped (-SkipDebloat)'
} else {
    $debloatCheck = Test-DebloatNeeded $app.FullName
    if ($Quick -and -not $debloatCheck.Needed) {
        Write-Ok 'Debloat skipped (-Quick, already lean)'
    } elseif ($ForceDebloat -or $debloatCheck.Needed) {
        Write-Step "Debloating Discord ($($debloatCheck.Reasons -join ', '))..."
        Invoke-Debloat $app.FullName $freed
        Ensure-DiscordCompatibilityRecovery $app.FullName
        Disable-Fso $app.FullName
    } else {
        Write-Ok 'Debloat skipped (already lean)'
    }
}

if ($SkipCacheClean) {
    Write-Ok 'Cache clean skipped (-SkipCacheClean)'
} else {
    Clear-DiscordConflictLeftovers | Out-Null
    Clear-DiscordSafeCache $freed
}
Ensure-KrispModule $app.FullName
Ensure-RuntimeModules $app.FullName
if (-not $Quick) {
    Ensure-DiscordBootReady $app.FullName
}
Ensure-AsarStockBackup $app.FullName

# 3) Boot flags only (never touches login/session storage)
Apply-DiscordProfile (Join-Path $AppData 'settings.json')

# 4) Equicord + OpenASAR + profile
if (-not $SkipEquicord) {
    Install-Equicord $app.FullName
} else {
    Apply-EquicordProfile -AppDir $app.FullName
    if (-not $SkipOpenAsar) {
        Install-OpenAsar $app.FullName
    }
}

# 5) DiscOpt kernel - core feature (aggressive 5s trim + raw input + Above Normal priority)
if (-not $SkipKernel) {
    Install-DiscOptKernel $app.FullName
} else {
    Write-Warn 'Skipped DiscOpt kernel (-SkipKernel) - aggressive trim / raw input not installed'
}

# 5b) Boot safety
# OptiHub runs elevated. Launching Discord from an elevated host makes Discord
# elevated too (black screens / window-state never detected). Skip the open/close
# flash whenever OptiHub asked for quiet mode and only verify files on disk.
$optiHubQuiet = ($env:OPTIHUB -eq '1' -and $NoLaunch) -or ($env:OPTIHUB_SKIP_BOOT_FLASH -eq '1')
if ($optiHubQuiet) {
    Write-HubProgress 90 'Verifying files on disk...'
    Write-Step 'Quiet verify (no Discord window flash)...'
    $exeOk = Test-Path (Join-Path $app.FullName 'Discord.exe')
    $asarPath = Join-Path $app.FullName 'resources\app.asar'
    $asarOk = (Test-Path $asarPath) -and ((Get-Item $asarPath).Length -ge 64)
    $eqAsar = Join-Path $EquicordData 'equicord.asar'
    $eqOk = $SkipEquicord -or ((Test-Path $eqAsar) -and ((Get-Item $eqAsar).Length -gt 1000000))
    $modsOk = Test-DiscordModulesReady $app.FullName
    $kernelOk = $SkipKernel -or (
        (Test-Path (Join-Path $app.FullName 'version.dll')) -and
        (Test-Path (Join-Path $app.FullName 'config.ini')) -and
        (Test-Path (Join-Path $app.FullName 'ffmpeg_real.dll')) -and
        (Test-Path (Join-Path $app.FullName 'ffmpeg.dll')) -and
        ((Get-Item (Join-Path $app.FullName 'ffmpeg.dll')).Length -lt 500000)
    )
    if ($exeOk -and $asarOk -and $eqOk -and $modsOk -and $kernelOk) {
        Write-Ok 'Quiet verify passed (loader + modules + kernel on disk)'
        Write-Ok 'Open Discord from the Start menu (not as admin) when ready'
    } else {
        Write-Warn "Quiet verify incomplete (exe=$exeOk asar=$asarOk eq=$eqOk mods=$modsOk kernel=$kernelOk)"
        if (-not $modsOk) {
            Write-Warn 'Modules incomplete - leaving runtime stock-safe'
            Use-StockDiscordRuntime $app.FullName
        } elseif (-not $kernelOk -and -not $SkipKernel) {
            Write-Warn 'Kernel files incomplete - reinstalling kernel...'
            try { Install-DiscOptKernel $app.FullName } catch { Write-Warn $_.Exception.Message }
        }
    }
} elseif ($Quick) {
    Write-Step 'Quick boot smoke check...'
    Stop-Discord
    [void](Invoke-DiscordLaunch -AppDir $app.FullName)
    if (Wait-DiscordHealthy 30) {
        Stop-Discord
        Write-Ok 'Quick boot check passed'
    } else {
        Write-Warn 'Quick boot check inconclusive - running full safety check...'
        Confirm-DiscordBootsAfterMods $app.FullName
    }
} else {
    Confirm-DiscordBootsAfterMods $app.FullName
}

# 6) Windows tweaks + shortcut
if (-not $Quick) {
    Apply-WindowsTweaks $app.FullName
} else {
    Refresh-DiscordWindowsRecovery
    Disable-DiscordWindowsAutostart
    Write-Ok 'Windows tweaks skipped (-Quick)'
}
Restore-StartMenu

Test-DiscOptimizer

if ($Quick) {
    Save-DiscordOptState @{
        version       = $Script:DiscOptVersion
        applyStatus   = 'incomplete'
        applied       = $false
        fullApply     = $false
        quick         = $true
        recovery      = $Script:DiscordWindowsRecovery
        appliedUtc    = (Get-Date).ToUniversalTime().ToString('o')
    }
    Write-Warn 'Quick pass completed; the verified no-compromise applied state still requires a full run'
} else {
    $debloatState = Test-DebloatNeeded $app.FullName
    if ($debloatState.Needed) {
        throw "Discord debloat verification incomplete: $($debloatState.Reasons -join ', ')"
    }
    if (-not (Test-DiscordWindowsSuppression)) {
        throw 'Stable Discord Windows suppression verification failed after apply'
    }
    $settingsPath = Join-Path $AppData 'settings.json'
    try {
        $settingsState = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json
        if ($settingsState.OPEN_ON_STARTUP -ne $false) { throw 'OPEN_ON_STARTUP is not false' }
    } catch {
        throw "Discord startup settings verification failed: $($_.Exception.Message)"
    }
    $fullApplyVerified = $true
}

Write-RunSummary -AppDir $app.FullName -Launched $false

if (-not $NoLaunch) {
    Wait-UserThenStartDiscord $app.FullName
} else {
    Write-Ok 'Disc Optimizer finished (no launch - use Start menu or -Launch).'
}

Write-HubProgress 100 'Completed successfully'
Write-LogLine 'OK' 'Run finished successfully'
Copy-Item -Path $Script:LogPath -Destination (Join-Path $LogDir 'last-run.log') -Force
if (-not $Quick -and $fullApplyVerified) {
    Complete-DiscordApplyState $app.FullName
}
exit 0
} catch {
    $detail = Write-LogFailure $_
    Write-Host ''
    Write-Err 'Disc Optimizer failed.'
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ''
    Write-Host '  If Discord will not open, paste this into PowerShell to restore it:' -ForegroundColor Yellow
    Write-Host '    irm "https://raw.githubusercontent.com/BarcusEric/OptiHub/main/Repair-Discord.ps1" | iex' -ForegroundColor Cyan
    Write-Host ''
    Write-Host "  Error log: $(Join-Path $LogDir 'last-error.log')" -ForegroundColor Yellow
    if ($Script:LogPath) {
        Write-Host "  Full log:  $Script:LogPath" -ForegroundColor Yellow
    }
    Write-Host ''
    Wait-DiscOptClosePrompt
    exit 1
}
