# Disc Optimizer - PowerShell 7 Preview only (+ Windows Terminal Preview via OptiHub host).
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
$Script:DiscOptVersion = '1.3.18'
$Script:SelfPath = $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent $Script:SelfPath
$KitDir = Join-Path $Root 'kit'
if (-not (Test-Path $KitDir)) { $KitDir = $Root }
$ToolsDir = Join-Path $KitDir 'tools'
$DownloadDir = Join-Path $KitDir 'downloads'
$BootstrapLogDir = Join-Path $KitDir 'logs'

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

function Test-DiscOptIsPwshPreviewPath([string]$Exe) {
    if ([string]::IsNullOrWhiteSpace($Exe)) { return $false }
    if ($Exe -match 'WindowsPowerShell') { return $false }
    if ($Exe -match '(?i)7-preview|PowerShellPreview|pwsh-preview') { return $true }
    if ($Exe -match '(?i)PowerShell[\\/]7[\\/]' -and $Exe -notmatch '(?i)preview') { return $false }
    return $false
}

function Test-DiscOptIsPwshPreviewHost {
    if ($PSVersionTable.PSEdition -ne 'Core') { return $false }
    if ([int]$PSVersionTable.PSVersion.Major -lt 7) { return $false }
    $hostPath = ''
    try { $hostPath = [string](Get-Process -Id $PID -ErrorAction Stop).Path } catch { }
    if (Test-DiscOptIsPwshPreviewPath $hostPath) { return $true }
    $pre = ''
    try { $pre = [string]$PSVersionTable.PSVersion.PreReleaseLabel } catch { }
    if (-not [string]::IsNullOrWhiteSpace($pre)) { return $true }
    if ([string]$PSVersionTable.GitCommitId -match '(?i)preview') { return $true }
    return $false
}

function Get-DiscOptPwshVersion([string]$Exe) {
    if (-not (Test-Path -LiteralPath $Exe)) { return $null }
    if (-not (Test-DiscOptIsPwshPreviewPath $Exe)) { return $null }
    try {
        $raw = & $Exe -NoProfile -Command '$PSVersionTable.PSVersion.ToString()' 2>$null
        if ($raw -and ("$raw" -match '^(\d+)\.') -and [int]$Matches[1] -ge 7) { return "$raw".Trim() }
    } catch {}
    return $null
}

function Get-DiscOptPwsh7 {
    # PowerShell 7 Preview only. Never Windows PowerShell 5.1 / never stable 7.
    $candidates = [System.Collections.Generic.List[string]]::new()

    foreach ($path in @(
        (Get-DiscOptEnvPath 'ProgramFiles' 'PowerShell\7-preview\pwsh.exe'),
        (Get-DiscOptEnvPath 'LOCALAPPDATA' 'Microsoft\WindowsApps\pwsh-preview.exe')
    )) {
        if ($path) { [void]$candidates.Add($path) }
    }

    $cmdPreview = Get-Command pwsh-preview -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmdPreview -and $cmdPreview.Source) { [void]$candidates.Add([string]$cmdPreview.Source) }

    $appsRoot = Get-DiscOptEnvPath 'ProgramFiles' 'WindowsApps'
    if ($appsRoot -and (Test-Path -LiteralPath $appsRoot)) {
        Get-ChildItem -LiteralPath $appsRoot -Directory -Filter 'Microsoft.PowerShellPreview*' -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object {
                $exe = Join-Path $_.FullName 'pwsh.exe'
                if (Test-Path -LiteralPath $exe) { [void]$candidates.Add($exe) }
            }
    }

    foreach ($exe in ($candidates | Select-Object -Unique)) {
        if (-not (Test-DiscOptIsPwshPreviewPath $exe)) { continue }
        $ver = Get-DiscOptPwshVersion $exe
        if ($ver) {
            return @{ Exe = $exe; Version = $ver }
        }
        if (Test-Path -LiteralPath $exe) {
            return @{ Exe = $exe; Version = 'preview' }
        }
    }
    return $null
}

function Install-DiscOptPwsh7 {
    # Preview only via winget/Store. No stable portable zip (OptiHub requires Preview + Terminal Preview).
    $winget = Get-Command winget -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $winget) {
        throw 'winget is required to install PowerShell 7 Preview. Install Microsoft.PowerShell.Preview and Windows Terminal Preview, then retry.'
    }
    Write-Host '[*] Installing PowerShell 7 Preview via winget (Microsoft.PowerShell.Preview)...' -ForegroundColor Cyan
    $proc = Start-Process -FilePath $winget.Source -ArgumentList @(
        'install', '-e', '-id', 'Microsoft.PowerShell.Preview',
        '--accept-package-agreements', '--accept-source-agreements', '--silent'
    ) -PassThru -WindowStyle Hidden
    if (-not $proc.WaitForExit(600000)) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
        throw 'winget PowerShell Preview install timed out'
    }
    if ($proc.ExitCode -ne 0 -and $proc.ExitCode -ne $null) {
        #  -1978335189 already installed etc. still re-probe
        Write-Host "[!] winget exit $($proc.ExitCode) - probing for Preview install..." -ForegroundColor Yellow
    }
    Start-Sleep -Seconds 2
    $found = Get-DiscOptPwsh7
    if ($found) { return $found }
    throw 'PowerShell 7 Preview install finished but pwsh-preview was not found. Install Microsoft.PowerShell.Preview and Windows Terminal Preview, then retry.'
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

    $isAdmin = Test-DiscOptIsElevated
    $needElevate = (-not $Launch) -and (-not $isAdmin)
    # OptiHub already elevates + hosts PowerShell 7 Preview silently.
    $hostedByOptiHub = ($env:OPTIHUB -eq '1' -or $env:DISCOPT_NONINTERACTIVE -eq '1')

    # Fast path: already on Preview and elevated (or OptiHub-hosted apply).
    if ((Test-DiscOptIsPwshPreviewHost) -and (-not $needElevate -or $hostedByOptiHub)) {
        $env:DISCOPT_RUNTIME_READY = '1'
        $env:DISCOPT_PS7_PREVIEW = '1'
        if ($isAdmin) { $env:DISCOPT_ELEVATED = '1' }
        return
    }

    $pwshInfo = Get-DiscOptPwsh7
    if (-not $pwshInfo) {
        Write-Host ''
        Write-Host '  Disc Optimizer - PowerShell 7 Preview setup' -ForegroundColor Magenta
        Write-Host '[*] PowerShell 7 Preview not found - installing Microsoft.PowerShell.Preview...' -ForegroundColor Cyan
        try {
            $pwshInfo = Install-DiscOptPwsh7
        } catch {
            Write-Host "[-] $($_.Exception.Message)" -ForegroundColor Red
            Wait-DiscOptClosePrompt
            exit 1
        }
        if (-not $pwshInfo) {
            Write-Host '[-] Could not install PowerShell 7 Preview. Check internet / winget and try again.' -ForegroundColor Red
            Wait-DiscOptClosePrompt
            exit 1
        }
        Write-Host "[+] PowerShell 7 Preview ready ($($pwshInfo.Version))" -ForegroundColor Green
    }

    $extraArgs = Get-DiscOptBoundScriptArgs
    $baseArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass')

    if ($needElevate -and -not $hostedByOptiHub) {
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

    # Re-enter under Preview when the current host is not Preview.
    if (-not (Test-DiscOptIsPwshPreviewHost)) {
        $allArgs = $baseArgs + @('-File', $Script:SelfPath) + $extraArgs
        & $pwshInfo.Exe @allArgs
        if ($null -eq $LASTEXITCODE) { exit 1 }
        exit $LASTEXITCODE
    }

    $env:DISCOPT_RUNTIME_READY = '1'
    $env:DISCOPT_PS7_PREVIEW = '1'
    if ($isAdmin) { $env:DISCOPT_ELEVATED = '1' }
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
    # Keep member-list role section headers (Admin, Mods, etc.) - NoRoleHeaders strips them.
    'NoRoleHeaders',
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
