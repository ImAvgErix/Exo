# Disc Optimizer - stable PowerShell 7 host (any 7.x; never Windows PowerShell 5.1).
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
    [switch]$SkipCacheClean,
    [switch]$Experimental
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

# Experimental: force full debloat + rebuild Equicord lean profile (no preserve).
if ($Experimental) {
    $ForceDebloat = $true
    $env:EXO_EXPERIMENTAL = '1'
    Write-Host '[*] Experimental Discord apply (force debloat + lean profile rebuild)' -ForegroundColor Cyan
}

if ($env:EXO -eq '1' -or $env:DISCOPT_NONINTERACTIVE -eq '1') {
    $NoLaunch = $true
    # Prefer bundled manifests for speed; Sync-PluginManifests still downloads if missing.
    if (-not $PSBoundParameters.ContainsKey('SkipManifestSync')) {
        $SkipManifestSync = $true
    }
}

$ErrorActionPreference = 'Stop'
$Script:DiscOptVersion = '1.3.59'
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

    # Exo / non-interactive must never block on a keypress.
    if ($env:EXO -eq '1' -or $env:DISCOPT_NONINTERACTIVE -eq '1' -or $NoLaunch) {
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

function Test-DiscOptIsPwsh7Path([string]$Exe) {
    # Any pwsh path is acceptable except Windows PowerShell 5.1.
    if ([string]::IsNullOrWhiteSpace($Exe)) { return $false }
    if ($Exe -match 'WindowsPowerShell') { return $false }
    return $true
}

function Test-DiscOptIsPwsh7Host {
    # Any pwsh 7.x host is accepted (stable preferred; preview tolerated).
    # Windows PowerShell 5.1 is rejected - the optimizer uses Core-only APIs.
    if ($PSVersionTable.PSEdition -ne 'Core') { return $false }
    if ([int]$PSVersionTable.PSVersion.Major -lt 7) { return $false }
    $hostPath = ''
    try { $hostPath = [string](Get-Process -Id $PID -ErrorAction Stop).Path } catch { }
    if ($hostPath -match 'WindowsPowerShell') { return $false }
    return $true
}

function Get-DiscOptPwshVersion([string]$Exe) {
    if (-not (Test-Path -LiteralPath $Exe)) { return $null }
    if (-not (Test-DiscOptIsPwsh7Path $Exe)) { return $null }
    try {
        $raw = & $Exe -NoProfile -Command '$PSVersionTable.PSVersion.ToString()' 2>$null
        if ($raw -and ("$raw" -match '^(\d+)\.') -and [int]$Matches[1] -ge 7) { return "$raw".Trim() }
    } catch {}
    return $null
}

function Get-DiscOptPwsh7 {
    # Stable PowerShell 7 first; existing preview installs only as a last resort.
    # Never Windows PowerShell 5.1.
    $candidates = [System.Collections.Generic.List[string]]::new()

    $stable = Get-DiscOptEnvPath 'ProgramFiles' 'PowerShell\7\pwsh.exe'
    if ($stable) { [void]$candidates.Add($stable) }

    $cmdPwsh = Get-Command pwsh -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmdPwsh -and $cmdPwsh.Source) { [void]$candidates.Add([string]$cmdPwsh.Source) }

    $appsRoot = Get-DiscOptEnvPath 'ProgramFiles' 'WindowsApps'
    if ($appsRoot -and (Test-Path -LiteralPath $appsRoot)) {
        Get-ChildItem -LiteralPath $appsRoot -Directory -Filter 'Microsoft.PowerShell_*' -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object {
                $exe = Join-Path $_.FullName 'pwsh.exe'
                if (Test-Path -LiteralPath $exe) { [void]$candidates.Add($exe) }
            }
    }

    # Exo-managed portable STABLE runtime (the legacy 'PowerShellPreview'
    # runtime dir is retired and pruned by the dependency doctor).
    $portable = Get-DiscOptEnvPath 'LOCALAPPDATA' 'Exo\runtime\PowerShell\pwsh.exe'
    if ($portable) { [void]$candidates.Add($portable) }

    # Preview installs are a last-resort fallback only - never downloaded,
    # never required.
    foreach ($path in @(
        (Get-DiscOptEnvPath 'ProgramFiles' 'PowerShell\7-preview\pwsh.exe'),
        (Get-DiscOptEnvPath 'LOCALAPPDATA' 'Microsoft\WindowsApps\pwsh-preview.exe')
    )) {
        if ($path) { [void]$candidates.Add($path) }
    }
    $cmdPreview = Get-Command pwsh-preview -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmdPreview -and $cmdPreview.Source) { [void]$candidates.Add([string]$cmdPreview.Source) }
    if ($appsRoot -and (Test-Path -LiteralPath $appsRoot)) {
        Get-ChildItem -LiteralPath $appsRoot -Directory -Filter 'Microsoft.PowerShellPreview*' -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object {
                $exe = Join-Path $_.FullName 'pwsh.exe'
                if (Test-Path -LiteralPath $exe) { [void]$candidates.Add($exe) }
            }
    }

    foreach ($exe in ($candidates | Select-Object -Unique)) {
        if (-not (Test-DiscOptIsPwsh7Path $exe)) { continue }
        $ver = Get-DiscOptPwshVersion $exe
        if ($ver) {
            return @{ Exe = $exe; Version = $ver }
        }
        if (Test-Path -LiteralPath $exe) {
            return @{ Exe = $exe; Version = '7' }
        }
    }
    return $null
}

function Install-DiscOptPwshPortable {
    # Winget-less fallback: official STABLE PowerShell portable zip from
    # github.com/PowerShell/PowerShell (latest non-prerelease, win-x64).
    # Per-user, no elevation. Preview releases are never downloaded.
    try {
        Write-Host '[*] Downloading PowerShell 7 portable from GitHub releases...' -ForegroundColor Cyan
        $releases = Invoke-RestMethod -Uri 'https://api.github.com/repos/PowerShell/PowerShell/releases?per_page=15' `
            -Headers @{ 'User-Agent' = 'Exo-DiscOpt' } -TimeoutSec 120
        foreach ($release in @($releases)) {
            if ($release.prerelease) { continue }
            if ([string]$release.tag_name -match '(?i)preview|rc') { continue }
            $asset = @($release.assets) | Where-Object { $_.name -like 'PowerShell-7*-win-x64.zip' } | Select-Object -First 1
            if (-not $asset) { continue }

            $dest = Get-DiscOptEnvPath 'LOCALAPPDATA' 'Exo\runtime\PowerShell'
            if (-not $dest) { return $null }
            $runtimeRoot = Split-Path -Parent $dest
            New-Item -ItemType Directory -Force -Path $runtimeRoot | Out-Null
            $zip = Join-Path $runtimeRoot 'pwsh-download.zip'
            $staging = "$dest-staging"

            Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing
            if ([long]$asset.size -gt 0 -and (Get-Item -LiteralPath $zip).Length -ne [long]$asset.size) {
                Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
                return $null
            }

            if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
            Expand-Archive -Path $zip -DestinationPath $staging -Force
            Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
            if (-not (Test-Path -LiteralPath (Join-Path $staging 'pwsh.exe'))) {
                Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
                return $null
            }

            if (Test-Path -LiteralPath $dest) {
                try { Remove-Item -LiteralPath $dest -Recurse -Force } catch { }
            }
            if (-not (Test-Path -LiteralPath $dest)) { Move-Item -LiteralPath $staging -Destination $dest }
            else { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }

            $exe = Join-Path $dest 'pwsh.exe'
            if (Test-Path -LiteralPath $exe) {
                Write-Host "[+] PowerShell 7 portable ready ($($release.tag_name))" -ForegroundColor Green
                return @{ Exe = $exe; Version = [string]$release.tag_name }
            }
            return $null
        }
    } catch {
        Write-Host "[!] Portable PowerShell 7 download failed: $($_.Exception.Message)" -ForegroundColor Yellow
    }
    return $null
}

function Install-DiscOptPwsh7 {
    # Stable PowerShell 7 only. Prefer winget; without winget (debloated Windows)
    # fall back to the official portable STABLE zip from GitHub releases.
    $winget = Get-Command winget -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($winget) {
        Write-Host '[*] Installing PowerShell 7 via winget (Microsoft.PowerShell)...' -ForegroundColor Cyan
        $proc = Start-Process -FilePath $winget.Source -ArgumentList @(
            'install', '-e', '-id', 'Microsoft.PowerShell',
            '--accept-package-agreements', '--accept-source-agreements', '--silent'
        ) -PassThru -WindowStyle Hidden
        if (-not $proc.WaitForExit(600000)) {
            try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
            Write-Host '[!] winget PowerShell 7 install timed out - trying portable fallback...' -ForegroundColor Yellow
        } elseif ($proc.ExitCode -ne 0 -and $proc.ExitCode -ne $null) {
            #  -1978335189 already installed etc. still re-probe
            Write-Host "[!] winget exit $($proc.ExitCode) - probing for PowerShell 7 install..." -ForegroundColor Yellow
        }
        Start-Sleep -Seconds 2
        $found = Get-DiscOptPwsh7
        if ($found) { return $found }
    } else {
        Write-Host '[!] winget not found - using the official portable fallback...' -ForegroundColor Yellow
    }

    $portable = Install-DiscOptPwshPortable
    if ($portable) { return $portable }

    throw 'PowerShell 7 is required and could not be installed automatically. Install it with: winget install Microsoft.PowerShell, then retry.'
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
    # Exo already elevates + hosts stable PowerShell 7 silently.
    $hostedByExo = ($env:EXO -eq '1' -or $env:DISCOPT_NONINTERACTIVE -eq '1')

    # Fast path: already on pwsh 7 and elevated (or Exo-hosted apply).
    if ((Test-DiscOptIsPwsh7Host) -and (-not $needElevate -or $hostedByExo)) {
        $env:DISCOPT_RUNTIME_READY = '1'
        $env:DISCOPT_PS7 = '1'
        if ($isAdmin) { $env:DISCOPT_ELEVATED = '1' }
        return
    }

    $pwshInfo = Get-DiscOptPwsh7
    if (-not $pwshInfo) {
        Write-Host ''
        Write-Host '  Disc Optimizer - PowerShell 7 setup' -ForegroundColor Magenta
        Write-Host '[*] PowerShell 7 not found - installing Microsoft.PowerShell...' -ForegroundColor Cyan
        try {
            $pwshInfo = Install-DiscOptPwsh7
        } catch {
            Write-Host "[-] $($_.Exception.Message)" -ForegroundColor Red
            Wait-DiscOptClosePrompt
            exit 1
        }
        if (-not $pwshInfo) {
            Write-Host '[-] Could not install PowerShell 7. Check internet / winget and try again.' -ForegroundColor Red
            Wait-DiscOptClosePrompt
            exit 1
        }
        Write-Host "[+] PowerShell 7 ready ($($pwshInfo.Version))" -ForegroundColor Green
    }

    $extraArgs = Get-DiscOptBoundScriptArgs
    $baseArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass')

    if ($needElevate -and -not $hostedByExo) {
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

    # Re-enter under pwsh 7 when the current host is not pwsh 7 (e.g. 5.1).
    if (-not (Test-DiscOptIsPwsh7Host)) {
        $allArgs = $baseArgs + @('-File', $Script:SelfPath) + $extraArgs
        & $pwshInfo.Exe @allArgs
        if ($null -eq $LASTEXITCODE) { exit 1 }
        exit $LASTEXITCODE
    }

    $env:DISCOPT_RUNTIME_READY = '1'
    $env:DISCOPT_PS7 = '1'
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
$Script:DiscordVariantResults = @()
$Script:DiscordQosResults = @()
$Script:ExoApplyReport = [Collections.Generic.List[string]]::new()

$Protected = @(
    'version.dll', 'config.ini', 'Discord.exe', 'ffmpeg.dll', 'ffmpeg_real.dll',
    'Discord.bin.exe', 'Update.exe', 'app.asar', '_app.asar', '_app.asar.stock'
)
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
    'IdleAutoRestart', 'FixSpotifyEmbeds', 'ReplaceGoogleSearch',
    'BetterUploadButton', 'FixYoutubeEmbeds',
    'CancelFriendRequest',
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
            Write-Host '[*] Discord client changed - restoring Exo mods...'
            Stop-Discord
            $healAttempted = $true
            $kernelInstallFailed = $false
            if (-not $openAsarOk -or -not $equicordOk) {
                try { Install-Equicord $app.FullName } catch {
                    try { Install-OpenAsar $app.FullName } catch { }
                    try { Apply-EquicordProfile -AppDir $app.FullName } catch { }
                }
            }
            if (-not $kernelOk) {
                try {
                    Install-DiscOptKernel $app.FullName
                    $kernelOk = [bool]$Script:DiscOptKernelProxyActive
                } catch {
                    $kernelInstallFailed = $true
                    Write-Warn "Kernel heal failed: $($_.Exception.Message)"
                    try { Disable-DiscOptKernelOnDisk $app.FullName } catch { }
                }
            }
            try { Restore-StartMenu } catch { }
            if ($healAttempted) {
                if ($kernelInstallFailed) {
                    try { Disable-DiscOptKernelOnDisk $app.FullName } catch { }
                } else {
                    try {
                        Confirm-DiscordBootsAfterMods $app.FullName
                    } catch {
                        Write-Warn "Launch heal boot check failed: $($_.Exception.Message)"
                        try { Disable-DiscOptKernelOnDisk $app.FullName } catch { }
                    }
                }
            }
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
try {
    Prepare-Discord
    Add-ExoReport 'discord-install' 'ok'
} catch {
    Add-ExoReport 'discord-install' 'fail' $_.Exception.Message
    throw
}

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
    Add-ExoReport 'debloat' 'skip' 'SkipDebloat switch'
} else {
    $debloatCheck = Test-DebloatNeeded $app.FullName
    if ($Quick -and -not $debloatCheck.Needed) {
        Write-Ok 'Debloat skipped (-Quick, already lean)'
        Add-ExoReport 'debloat' 'skip' 'already lean (Quick)'
    } elseif ($ForceDebloat -or $debloatCheck.Needed) {
        Write-Step "Debloating Discord ($($debloatCheck.Reasons -join ', '))..."
        try {
            Invoke-Debloat $app.FullName $freed
            Ensure-DiscordCompatibilityRecovery $app.FullName
            Disable-Fso $app.FullName
            Add-ExoReport 'debloat' 'ok'
        } catch {
            Add-ExoReport 'debloat' 'fail' $_.Exception.Message
            throw
        }
    } else {
        Write-Ok 'Debloat skipped (already lean)'
        Add-ExoReport 'debloat' 'ok' 'already lean'
    }
}

if ($SkipCacheClean) {
    Write-Ok 'Cache clean skipped (-SkipCacheClean)'
    Add-ExoReport 'cache-clean' 'skip' 'SkipCacheClean switch'
} else {
    try {
        Clear-DiscordConflictLeftovers | Out-Null
        Clear-DiscordSafeCache $freed
        Add-ExoReport 'cache-clean' 'ok'
    } catch {
        Add-ExoReport 'cache-clean' 'fail' $_.Exception.Message
        throw
    }
}
Ensure-KrispModule $app.FullName
Ensure-RuntimeModules $app.FullName
if (-not $Quick) {
    Ensure-DiscordBootReady $app.FullName
}
Ensure-AsarStockBackup $app.FullName

# 3) Boot flags only (never touches login/session storage)
try {
    Apply-DiscordProfile (Join-Path $AppData 'settings.json')
    Add-ExoReport 'host-flags' 'ok'
} catch {
    Add-ExoReport 'host-flags' 'fail' $_.Exception.Message
    throw
}

# 4) Equicord + Exo Host + profile
if (-not $SkipEquicord) {
    try {
        Install-Equicord $app.FullName
        Add-ExoReport 'equicord' 'ok'
    } catch {
        Add-ExoReport 'equicord' 'fail' $_.Exception.Message
        throw
    }
} else {
    Apply-EquicordProfile -AppDir $app.FullName
    if (-not $SkipOpenAsar) {
        Install-OpenAsar $app.FullName
    }
    Add-ExoReport 'equicord' 'skip' 'SkipEquicord switch'
}

# 5) DiscOpt kernel - memory trim / priority / raw input.
# Under elevated Exo we still INSTALL the kernel, then prove boot via explorer
# (user token). If boot fails, disarm kernel only - keep Equicord if it boots.
$elevatedExoQuiet = ($env:EXO -eq '1' -and $NoLaunch) -or ($env:EXO_SKIP_BOOT_FLASH -eq '1')
if (-not $SkipKernel) {
    try {
        if ((Get-Command Test-KernelOnDisk -ErrorAction SilentlyContinue) -and
            (Test-KernelOnDisk $app.FullName)) {
            Write-Ok 'DiscOpt kernel already on disk'
            Add-ExoReport 'kernel' 'ok' 'already on disk'
            $Script:DiscOptKernelProxyActive = $true
        } else {
            Install-DiscOptKernel $app.FullName
            if ($Script:DiscOptKernelProxyActive) {
                Add-ExoReport 'kernel' 'ok'
            } else {
                Add-ExoReport 'kernel' 'fail' 'ffmpeg proxy skipped; stock ffmpeg kept'
            }
        }
    } catch {
        Add-ExoReport 'kernel' 'fail' $_.Exception.Message
        Write-Warn "DiscOpt kernel install failed: $($_.Exception.Message)"
        try { Disable-DiscOptKernelOnDisk $app.FullName } catch { }
        $Script:DiscOptKernelProxyActive = $false
    }
} else {
    Write-Warn 'Skipped DiscOpt kernel (-SkipKernel)'
    try { Disable-DiscOptKernelOnDisk $app.FullName } catch { }
    Add-ExoReport 'kernel' 'skip' 'SkipKernel switch'
    $Script:DiscOptKernelProxyActive = $false
}

# 5b) Boot safety (disk only mid-apply — do NOT open Discord here).
# Opening Discord 2–3 times for check + kill + relaunch is excessive.
# One user-token open at the end of Apply proves boot and leaves Discord open.
$exoQuiet = ($env:EXO -eq '1' -and $NoLaunch) -or ($env:EXO_SKIP_BOOT_FLASH -eq '1')
$Script:DiscordDeferUserBoot = $false
if ($exoQuiet) {
    Write-HubProgress 90 'Verifying files on disk...'
    Write-Step 'Quiet disk verify (Discord stays closed until the end)...'
    $verDll = Join-Path $app.FullName 'version.dll'
    $exeOk = Test-Path (Join-Path $app.FullName 'Discord.exe')
    $asarPath = Join-Path $app.FullName 'resources\app.asar'
    $asarOk = (Test-Path $asarPath) -and ((Get-Item $asarPath).Length -ge 64)
    $eqAsar = Join-Path $EquicordData 'equicord.asar'
    $eqOk = $SkipEquicord -or ((Test-Path $eqAsar) -and ((Get-Item $eqAsar).Length -gt 1000000))
    $asarIsStub = $asarOk -and ((Get-Item $asarPath).Length -lt 4096)
    if (-not $SkipEquicord -and $asarIsStub -and -not $eqOk) {
        Write-Warn 'Equicord loader present but equicord.asar missing - restoring stock app.asar'
        try { Use-StockDiscordRuntime $app.FullName } catch { }
        $asarOk = (Test-Path $asarPath) -and ((Get-Item $asarPath).Length -ge 64)
        $eqOk = $true
    }
    $modsOk = Test-DiscordModulesReady $app.FullName
    if ($exeOk -and $asarOk -and $eqOk -and $modsOk) {
        $Script:DiscordDeferUserBoot = $true
        Write-Ok 'Disk verify passed — boot proof deferred to single end relaunch'
        Add-ExoReport 'boot-check' 'skip' 'deferred to single end relaunch'
    } else {
        Write-Warn "Quiet verify incomplete (exe=$exeOk asar=$asarOk eq=$eqOk mods=$modsOk)"
        if (-not $modsOk -or -not $asarOk) {
            try { Use-StockDiscordRuntime $app.FullName } catch { }
        }
        try { Disable-DiscOptKernelOnDisk $app.FullName } catch { }
        $Script:DiscordDeferUserBoot = $exeOk -and (Test-Path (Join-Path $app.FullName 'Discord.exe'))
        Add-ExoReport 'boot-check' 'fail' 'quiet disk verify incomplete; will still try one relaunch'
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
    try {
        Apply-WindowsTweaks $app.FullName
        Add-ExoReport 'windows-quiet' 'ok'
    } catch {
        Add-ExoReport 'windows-quiet' 'fail' $_.Exception.Message
        throw
    }
    $qosFailed = @($Script:DiscordQosResults | Where-Object { -not $_.Ok })
    if (@($Script:DiscordQosResults).Count -gt 0 -and $qosFailed.Count -eq 0) {
        Add-ExoReport 'voice-qos' 'ok'
    } elseif ($qosFailed.Count -gt 0) {
        Add-ExoReport 'voice-qos' 'fail' (($qosFailed | ForEach-Object { $_.Policy }) -join ', ')
    } else {
        Add-ExoReport 'voice-qos' 'skip' 'no installed variants detected'
    }

    # 6b) PTB / Canary variants (quiet + host flags; QoS handled above)
    [void](Set-DiscordVariantQuiet)
    $variantFailed = @($Script:DiscordVariantResults | Where-Object { -not ($_.SettingsFlags -and $_.AutostartQuiet) })
    if (@($Script:DiscordVariantResults).Count -eq 0) {
        Add-ExoReport 'variants' 'ok' 'stable only (PTB/Canary not installed)'
    } elseif ($variantFailed.Count -eq 0) {
        Add-ExoReport 'variants' 'ok'
    } else {
        Add-ExoReport 'variants' 'fail' (($variantFailed | ForEach-Object { $_.Variant }) -join ', ')
    }
} else {
    Refresh-DiscordWindowsRecovery
    Disable-DiscordWindowsAutostart
    Write-Ok 'Windows tweaks skipped (-Quick)'
    Add-ExoReport 'windows-quiet' 'skip' 'Quick pass'
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
        applyReport   = @(Get-ExoReportEntries)
    }
    Write-Warn 'Quick pass completed; the verified no-compromise applied state still requires a full run'
} else {
    try {
        # One more strip pass before verify - optional modules (e.g. discord_hook) often
        # reappear when Discord/Equicord touch the install mid-apply.
        $debloatState = Test-DebloatNeeded $app.FullName
        if ($debloatState.Needed -and -not $SkipDebloat) {
            Write-Step "Re-stripping leftovers before verify ($($debloatState.Reasons -join ', '))..."
            try {
                $reFreed = [ref]0L
                Invoke-Debloat $app.FullName $reFreed
            } catch {
                Write-Warn "Re-debloat pass: $($_.Exception.Message)"
            }
            $debloatState = Test-DebloatNeeded $app.FullName
        }
        if ($debloatState.HardNeeded) {
            throw "Discord debloat verification incomplete: $($debloatState.HardReasons -join ', ')"
        }
        if ($debloatState.SoftNeeded) {
            # Soft leftovers (optional modules / extra locales) must not fail Apply after
            # Equicord + Windows quiet succeeded - Discord may re-pull hook under lock.
            Write-Warn "Debloat soft residual (not failing Apply): $($debloatState.SoftReasons -join ', ')"
            Add-ExoReport 'verify-debloat' 'skip' ("soft residual: " + ($debloatState.SoftReasons -join ', '))
        }
        # Do not require OPEN_ON_STARTUP=false or full Windows suppression — those
        # are user choices. Verify host flags + install integrity only.
        $settingsPath = Join-Path $AppData 'settings.json'
        try {
            if (-not (Test-Path -LiteralPath $settingsPath)) {
                throw 'settings.json missing after host-flag merge'
            }
            $settingsState = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json
            if (-not $settingsState.chromiumSwitches) {
                throw 'chromiumSwitches missing after host-flag merge'
            }
        } catch {
            throw "Discord host settings verification failed: $($_.Exception.Message)"
        }
        $fullApplyVerified = $true
        Add-ExoReport 'verify' 'ok'
    } catch {
        Add-ExoReport 'verify' 'fail' $_.Exception.Message
        throw
    }
}

Write-RunSummary -AppDir $app.FullName -Launched $false

# Single user-token open: proves boot + leaves Discord running (no mid-apply flash).
# Worst case on kernel fail: one more launch after disarm (never 3 open/kill cycles).
$shouldRelaunch =
    ($env:EXO -eq '1' -or $NoLaunch) -and
    (-not $Quick) -and
    $fullApplyVerified -and
    $app -and
    (Test-Path -LiteralPath (Join-Path $app.FullName 'Discord.exe'))

if ($shouldRelaunch) {
    try {
        Write-HubProgress 97 'Opening Discord...'
        Write-Step 'One user-token open (boot proof + leave running)...'
        Stop-Discord
        Start-Sleep -Milliseconds 500
        if (Get-Command Invoke-DiscordLaunchAsUser -ErrorAction SilentlyContinue) {
            Invoke-DiscordLaunchAsUser $app.FullName
        } else {
            [void](Invoke-DiscordLaunch -AppDir $app.FullName)
        }
        $healthy = $false
        if (Get-Command Wait-DiscordHealthy -ErrorAction SilentlyContinue) {
            $healthy = [bool](Wait-DiscordHealthy 45)
        } else {
            Start-Sleep -Seconds 3
            $healthy = @(Get-Process -Name 'Discord' -ErrorAction SilentlyContinue).Count -gt 0
        }
        if (-not $healthy) {
            Write-Warn 'First open unhealthy — disarming DiscOpt kernel and trying once'
            try { Disable-DiscOptKernelOnDisk $app.FullName } catch { }
            $Script:DiscOptKernelProxyActive = $false
            Add-ExoReport 'kernel' 'fail' 'disarmed after end-open fail'
            Stop-Discord
            Start-Sleep -Milliseconds 500
            if (Get-Command Invoke-DiscordLaunchAsUser -ErrorAction SilentlyContinue) {
                Invoke-DiscordLaunchAsUser $app.FullName
            } else {
                [void](Invoke-DiscordLaunch -AppDir $app.FullName)
            }
            if (Get-Command Wait-DiscordHealthy -ErrorAction SilentlyContinue) {
                $healthy = [bool](Wait-DiscordHealthy 45)
            } else {
                Start-Sleep -Seconds 3
                $healthy = @(Get-Process -Name 'Discord' -ErrorAction SilentlyContinue).Count -gt 0
            }
        }
        # Leave Discord running — do not Stop-Discord after success.
        if ($healthy) {
            Write-Ok 'Discord open and healthy after Apply (single launch)'
            Add-ExoReport 'boot-check' 'ok' 'single end open verified'
            Add-ExoReport 'relaunch' 'ok' 'left running'
        } else {
            Write-Warn 'Discord did not stay healthy — try Start Menu if the window is missing'
            Add-ExoReport 'boot-check' 'fail' 'end open unhealthy'
            Add-ExoReport 'relaunch' 'fail' 'unhealthy after at most two opens'
        }
    } catch {
        Write-Warn "Could not open Discord after Apply: $($_.Exception.Message)"
        Add-ExoReport 'relaunch' 'fail' $_.Exception.Message
    }
} elseif (-not $NoLaunch) {
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
    # Persist the structured apply report on failure too (recovery preserved).
    try {
        $failedState = Read-DiscordOptState
        $failedRecovery = if ($failedState -and ($failedState.PSObject.Properties.Name -contains 'recovery')) {
            $failedState.recovery
        } else { $Script:DiscordWindowsRecovery }
        if ($failedState -or $Script:DiscordWindowsRecovery) {
            Save-DiscordOptState @{
                version     = $Script:DiscOptVersion
                applyStatus = 'incomplete'
                applied     = $false
                fullApply   = $false
                recovery    = $failedRecovery
                appliedUtc  = (Get-Date).ToUniversalTime().ToString('o')
                applyReport = @(Get-ExoReportEntries)
            }
        }
    } catch { }
    $detail = Write-LogFailure $_
    Write-Host ''
    Write-Err 'Disc Optimizer failed.'
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ''
    Write-Host '  If Discord will not open, paste this into PowerShell to restore it:' -ForegroundColor Yellow
    Write-Host '    irm "https://raw.githubusercontent.com/ImAvgErix/Exo/main/Repair-Discord.ps1" | iex' -ForegroundColor Cyan
    Write-Host ''
    Write-Host "  Error log: $(Join-Path $LogDir 'last-error.log')" -ForegroundColor Yellow
    if ($Script:LogPath) {
        Write-Host "  Full log:  $Script:LogPath" -ForegroundColor Yellow
    }
    Write-Host ''
    Wait-DiscOptClosePrompt
    exit 1
}
