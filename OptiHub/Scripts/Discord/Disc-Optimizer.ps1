# Disc Optimizer - right-click -> Run with PowerShell (auto-installs PS 7.7 + elevates)
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

$ErrorActionPreference = 'Stop'
$Script:DiscOptVersion = '1.1.5'
$Script:SelfPath = $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent $Script:SelfPath
$KitDir = Join-Path $Root 'kit'
if (-not (Test-Path $KitDir)) { $KitDir = $Root }
$ToolsDir = Join-Path $KitDir 'tools'
$DownloadDir = Join-Path $KitDir 'downloads'
$BootstrapLogDir = Join-Path $KitDir 'logs'
$Script:DiscOptPwshZipName = 'PowerShell-7.7.0-preview.2-win-x64.zip'
$Script:DiscOptPwshZipUrl = 'https://github.com/PowerShell/PowerShell/releases/download/v7.7.0-preview.2/PowerShell-7.7.0-preview.2-win-x64.zip'

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
    if ($VersionText -match '^(\d+)\.(\d+)\.') {
        $major = [int]$Matches[1]
        $minor = [int]$Matches[2]
        if ($major -gt 7) { return $true }
        if ($major -eq 7 -and $minor -ge 7) { return $true }
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

function Get-DiscOptPwsh77 {
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

function Install-DiscOptPwsh77Portable {
    if (-not (Test-Path $ToolsDir)) { New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null }
    if (-not (Test-Path $DownloadDir)) { New-Item -ItemType Directory -Path $DownloadDir -Force | Out-Null }

    $zipPath = Join-Path $DownloadDir $Script:DiscOptPwshZipName
    $destDir = Join-Path $ToolsDir 'pwsh'
    $pwshExe = Join-Path $destDir 'pwsh.exe'
    $cachedVer = Get-DiscOptPwshVersion $pwshExe
    if ($cachedVer) {
        return @{ Exe = $pwshExe; Version = $cachedVer }
    }

    if (-not (Test-Path $zipPath) -or (Get-Item $zipPath).Length -lt 50000000) {
        Write-Host '[*] Downloading portable PowerShell 7.7...' -ForegroundColor Cyan
        $ua = @{ 'User-Agent' = 'Disc-Optimizer/1.0 (Windows; PowerShell)' }
        Invoke-WebRequest -Uri $Script:DiscOptPwshZipUrl -OutFile $zipPath -UseBasicParsing -Headers $ua
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

function Install-DiscOptPwsh77 {
    if (Test-DiscOptIsElevated) {
        $winget = Get-Command winget -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($winget) {
            Write-Host '[*] Installing PowerShell 7.7 via winget...' -ForegroundColor Cyan
            $proc = Start-Process -FilePath $winget.Source -ArgumentList @(
                'install', '-e', '--id', 'Microsoft.PowerShell.Preview',
                '--accept-package-agreements', '--accept-source-agreements', '--silent'
            ) -PassThru -Wait -WindowStyle Hidden
            if ($proc.ExitCode -ne 0) {
                Write-Host '[!] winget install returned non-zero - trying portable download' -ForegroundColor Yellow
            } else {
                Start-Sleep -Seconds 3
                $found = Get-DiscOptPwsh77
                if ($found) { return $found }
            }
        }
    }

    return Install-DiscOptPwsh77Portable
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

    $pwshInfo = Get-DiscOptPwsh77
    if (-not $pwshInfo) {
        Write-Host ''
        Write-Host '  Disc Optimizer - PowerShell 7.7 setup' -ForegroundColor Magenta
        Write-Host '[*] PowerShell 7.7 not found - installing...' -ForegroundColor Cyan
        $pwshInfo = Install-DiscOptPwsh77
        if (-not $pwshInfo) {
            Write-Host '[-] Could not install PowerShell 7.7. Check internet and try again.' -ForegroundColor Red
            Wait-DiscOptClosePrompt
            exit 1
        }
        Write-Host "[+] Installed PowerShell $($pwshInfo.Version)" -ForegroundColor Green
    }

    $isCore77 = ($PSVersionTable.PSEdition -eq 'Core') -and (Test-DiscOptPwshVersionMeetsMinimum $PSVersionTable.PSVersion.ToString())
    $isAdmin = Test-DiscOptIsElevated
    $needElevate = (-not $Launch) -and (-not $isAdmin)

    if ($isCore77 -and -not $needElevate) {
        $env:DISCOPT_RUNTIME_READY = '1'
        if ($isAdmin) { $env:DISCOPT_ELEVATED = '1' }
        return
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
$EquilotCliUrl = 'https://github.com/Equicord/Equilotl/releases/latest/download/EquilotlCli.exe'
$DiscordSetupUrl = 'https://discord.com/api/downloads/distributions/app/installers/latest?channel=stable&platform=win&arch=x64'
$RequiredModules = @(
    'discord_desktop_core-1', 'discord_dispatch-1', 'discord_media-1',
    'discord_modules-1', 'discord_utils-1', 'discord_voice-1'
)
$KeepModules = @(
    'discord_desktop_core-1', 'discord_dispatch-1', 'discord_media-1',
    'discord_modules-1', 'discord_utils-1', 'discord_voice-1', 'discord_krisp-1',
    'discord_notifications-1'
)
$RuntimeModules = @('discord_notifications')
# The original, battle-tested AMOLED theme. Do NOT swap in broad
# [class*="..."] selector themes: painting layerContainer black covers the
# whole app with a black overlay (tooltips still show, everything else hidden).
$EnabledTheme = 'amoled-cord.theme.css'
$ForceDisabledPlugins = @(
    'BlockKrisp', 'AltKrispSwitch', 'RelationshipNotifier',
    'Dearrow', 'ImplicitRelationships', 'OpenInApp', 'SplitLargeMessages', 'EquicordToolbox',
    'IdleAutoRestart', 'FixSpotifyEmbeds', 'ReplaceGoogleSearch', 'SupportHelper',
    'BetterUploadButton', 'FixYoutubeEmbeds',
    'ConcatenatedComponentExtractor', 'CancelFriendRequest',
    'StartupTimings', 'NewPluginsManager', 'WebContextMenus', 'WebKeybinds', 'WebScreenShareFixes',
    'MessageNotifier', 'KeywordNotify', 'ReplyPingControl', 'BypassStatus', 'PingNotifications',
    'NotificationTitle', 'ToastNotifications', 'VoiceJoinMessages', 'VcNarrator', 'VcNarratorCustom',
    'XSOverlay', 'VoiceChannelLog', 'VoiceStats', 'Streaks', 'FriendshipRanks'
)
# Pure caches only - never touches web app storage, service workers, or session data.
$SafeCacheTargets = @(
    'Cache', 'Code Cache', 'GPUCache', 'ShaderCache', 'DawnCache', 'GraphiteDawnCache',
    'VideoDecodeStats', 'Media Cache', 'logs', 'Crashpad', 'crashpad', 'debug', 'sentry'
)

function Write-Banner {
    $psLabel = "PowerShell $($PSVersionTable.PSVersion)"
    if ($PSVersionTable.PSEdition) { $psLabel += " ($($PSVersionTable.PSEdition))" }
    Write-Host ''
    Write-Host "  Disc Optimizer v$Script:DiscOptVersion" -ForegroundColor Magenta
    Write-Host '  AMOLED | privacy | perf | cache trim | raw input' -ForegroundColor DarkGray
    Write-Host "  $psLabel" -ForegroundColor Cyan
    if ($env:DISCOPT_PS7 -eq '1') {
        Write-Host '  (upgraded from Windows PowerShell 5.1)' -ForegroundColor DarkGray
    }
    if ($env:DISCOPT_ELEVATED -eq '1') {
        Write-Host '  (running as Administrator)' -ForegroundColor DarkGray
    }
    Write-Host ''
}

function Invoke-KitMaintenance {
    Prune-DiscOptimizerLogs -Keep 5

    $bundleDir = Get-ModulesBundleDir
    if (Test-Path $bundleDir) {
        Get-ChildItem $bundleDir -Directory -ErrorAction SilentlyContinue |
            Where-Object { $RequiredModules -notcontains $_.Name } |
            ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
        Get-ChildItem $bundleDir -Recurse -Filter '*.log' -File -ErrorAction SilentlyContinue |
            ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
    }

    # installer.db backups accumulate one per repair - keep only the 2 newest.
    if ($DiscordRoot -and (Test-Path $DiscordRoot)) {
        Get-ChildItem $DiscordRoot -Filter 'installer.db.bak-*' -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -Skip 2 |
            ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
    }

    if (Test-Path $DownloadDir) {
        Get-ChildItem $DownloadDir -File -ErrorAction SilentlyContinue |
            Where-Object { ((Get-Date) - $_.LastWriteTime).TotalDays -gt 14 } |
            ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
    }

    $gui = Join-Path $ToolsDir 'Equilotl.exe'
    $cli = Join-Path $ToolsDir 'EquilotlCli.exe'
    if ((Test-Path $gui) -and (Test-Path $cli)) {
        Remove-Item $gui -Force -ErrorAction SilentlyContinue
    }
}

function Prune-DiscOptimizerLogs([int]$Keep = 5) {
    if (-not (Test-Path $LogDir)) { return }
    Get-ChildItem $LogDir -Filter 'disc-optimizer-*.log' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -Skip $Keep |
        ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
}

function Initialize-DiscOptimizerLog {
    if (-not (Test-Path $LogDir)) {
        New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
    }
    Prune-DiscOptimizerLogs
    $stamp = Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'
    $Script:LogPath = Join-Path $LogDir "disc-optimizer-$stamp.log"
    $header = @(
        'Disc Optimizer log',
        "Started: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
        "User: $env:USERNAME",
        "Computer: $env:COMPUTERNAME",
        "DiscOptVersion: $Script:DiscOptVersion",
        "PowerShell: $($PSVersionTable.PSVersion)",
        "Launch=$Launch SkipDebloat=$SkipDebloat ForceDebloat=$ForceDebloat SkipEquicord=$SkipEquicord SkipOpenAsar=$SkipOpenAsar SkipKernel=$SkipKernel NoLaunch=$NoLaunch VerifyOnly=$VerifyOnly SkipDiscordInstall=$SkipDiscordInstall FreshInstall=$FreshInstall Quick=$Quick SkipManifestSync=$SkipManifestSync SkipCacheClean=$SkipCacheClean",
        "Kit: $Root",
        "Discord: $DiscordRoot",
        ('=' * 60),
        ''
    ) -join [Environment]::NewLine
    Set-Content -Path $Script:LogPath -Value $header -Encoding UTF8
}

function Write-LogLine([string]$Level, [string]$Msg) {
    if (-not $Script:LogPath) { return }
    $line = "[$(Get-Date -Format 'HH:mm:ss')] [$Level] $Msg"
    Add-Content -Path $Script:LogPath -Value $line -Encoding UTF8
}

function Write-LogFailure($ErrorRecord) {
    if (-not $Script:LogPath) { Initialize-DiscOptimizerLog }

    $err = $ErrorRecord.Exception
    $inv = $ErrorRecord.InvocationInfo
    $lines = @(
        '',
        ('=' * 60),
        "FAILED: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
        "Message: $($err.Message)",
        "Type: $($err.GetType().FullName)",
        "ErrorId: $($ErrorRecord.FullyQualifiedErrorId)",
        "Category: $($ErrorRecord.CategoryInfo)",
        "Script: $($inv.ScriptName)",
        "Line: $($inv.ScriptLineNumber)",
        "Command: $($inv.Line.Trim())",
        "Position: $($ErrorRecord.InvocationInfo.PositionMessage)",
        'Stack trace:',
        $err.StackTrace
    )
    if ($err.InnerException) {
        $lines += @(
            '',
            'Inner exception:',
            "  Message: $($err.InnerException.Message)",
            "  Type: $($err.InnerException.GetType().FullName)",
            "  Stack: $($err.InnerException.StackTrace)"
        )
    }
    $lines += @('', "Full log: $Script:LogPath", ('=' * 60), '')
    $body = $lines -join [Environment]::NewLine

    Add-Content -Path $Script:LogPath -Value $body -Encoding UTF8
    Set-Content -Path (Join-Path $LogDir 'last-error.log') -Value $body -Encoding UTF8
    return $body
}

function Write-Step([string]$Msg) {
    Write-Host "[*] $Msg" -ForegroundColor Cyan
    Write-LogLine 'STEP' $Msg
}
function Write-Ok([string]$Msg) {
    Write-Host "[+] $Msg" -ForegroundColor Green
    Write-LogLine 'OK' $Msg
}
function Write-Warn([string]$Msg) {
    Write-Host "[!] $Msg" -ForegroundColor Yellow
    Write-LogLine 'WARN' $Msg
}
function Write-Err([string]$Msg) {
    Write-Host "[-] $Msg" -ForegroundColor Red
    Write-LogLine 'ERROR' $Msg
}

function Initialize-Network {
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    } catch {}
}

function Get-GitHubHeaders {
    return @{
        'User-Agent' = 'Disc-Optimizer/1.0 (Windows; PowerShell)'
        'Accept'     = 'application/vnd.github+json'
    }
}

function Get-EquicordLatestRelease {
    try {
        return Invoke-RestMethod -Uri 'https://api.github.com/repos/Equicord/Equicord/releases/latest' -Headers (Get-GitHubHeaders)
    } catch {
        Write-LogLine 'WARN' "GitHub API unavailable: $($_.Exception.Message)"
        return $null
    }
}

function Get-EquicordReleaseFile {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string]$OutFile
    )

    $ua = @{ 'User-Agent' = 'Disc-Optimizer/1.0 (Windows; PowerShell)' }
    $release = Get-EquicordLatestRelease
    if ($release) {
        $asset = $release.assets | Where-Object { $_.name -eq $FileName } | Select-Object -First 1
        if ($asset) {
            try {
                Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $OutFile -UseBasicParsing -Headers $ua
                return @{
                    Tag    = $release.tag_name
                    Size   = $asset.size
                    Source = 'api'
                }
            } catch {
                Write-LogLine 'WARN' "GitHub asset download failed ($FileName): $($_.Exception.Message)"
            }
        }
    }

    $direct = "https://github.com/Equicord/Equicord/releases/latest/download/$FileName"
    Write-Warn "GitHub API blocked - downloading $FileName directly"
    Invoke-WebRequest -Uri $direct -OutFile $OutFile -UseBasicParsing -Headers $ua
    if (-not (Test-Path $OutFile)) { throw "Failed to download $FileName" }
    return @{
        Tag    = 'latest'
        Size   = (Get-Item $OutFile).Length
        Source = 'direct'
    }
}

function Get-ActiveApp {
    Get-ChildItem $DiscordRoot -Directory -Filter 'app-*' -ErrorAction SilentlyContinue |
        Sort-Object { [version]($_.Name -replace '^app-', '') } -Descending |
        Select-Object -First 1
}

function Get-FolderSize([string]$Path) {
    if (-not (Test-Path $Path)) { return 0 }
    try {
        (Get-ChildItem $Path -Recurse -Force -File -ErrorAction SilentlyContinue |
            Measure-Object -Property Length -Sum -ErrorAction SilentlyContinue).Sum
    } catch { 0 }
}

function Remove-Safe([string]$Path, [ref]$Freed) {
    if (-not (Test-Path $Path)) { return $false }
    $item = Get-Item $Path -ErrorAction SilentlyContinue
    if (-not $item) { return $false }
    $size = if ($item.PSIsContainer) { Get-FolderSize $Path } else { $item.Length }
    Remove-Item $Path -Recurse -Force -ErrorAction SilentlyContinue
    $Freed.Value += [long]$size
    return $true
}

function Stop-Discord {
    $procs = @(Get-Process Discord, Discord.bin, Update -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return }
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    try {
        Wait-Process -Id ($procs.Id) -Timeout 3 -ErrorAction SilentlyContinue
    } catch { }
    $left = @(Get-Process Discord, Discord.bin, Update -ErrorAction SilentlyContinue)
    if ($left.Count -gt 0) {
        $left | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 400
    }
}

function Test-DiscordReady {
    if (-not (Test-Path $DiscordRoot)) { return $false }
    if (-not (Test-Path (Join-Path $DiscordRoot 'Update.exe'))) { return $false }
    $app = Get-ActiveApp
    if (-not $app) { return $false }
    return (Test-Path (Join-Path $app.FullName 'Discord.exe'))
}

function Confirm-WindowsDiscordTarget {
    $os64 = [Environment]::Is64BitOperatingSystem
    if (-not $os64) {
        throw 'Disc Optimizer requires 64-bit Windows. Discord desktop is x64-only.'
    }
    Write-Ok 'Target: Discord stable x64 for Windows'
}

function Test-ValidDiscordSetup([string]$Path) {
    return (Test-Path $Path) -and (Get-Item $Path).Length -gt 50000000
}

function Test-ValidEquicordAsar([string]$Path) {
    return (Test-Path $Path) -and (Get-Item $Path).Length -gt 1000000
}

function Get-BundledDiscordSetup {
    foreach ($name in @('DiscordSetup.exe', 'DiscordSetup-x64.exe')) {
        $path = Join-Path $ToolsDir $name
        if (Test-ValidDiscordSetup $path) { return $path }
    }
    return $null
}

function Get-DiscordSetup {
    if (-not (Test-Path $ToolsDir)) {
        New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null
    }

    $bundled = Get-BundledDiscordSetup
    if ($bundled) {
        Write-Ok "Using bundled Discord installer ($([math]::Round((Get-Item $bundled).Length / 1MB, 1)) MB from tools/)"
        return $bundled
    }

    $cached = Join-Path $DownloadDir 'DiscordSetup-x64.exe'
    if (Test-ValidDiscordSetup $cached) {
        $age = (Get-Date) - (Get-Item $cached).LastWriteTime
        if ($age.TotalDays -lt 7) {
            Write-Ok "Using cached x64 installer ($([math]::Round((Get-Item $cached).Length / 1MB, 1)) MB)"
            return $cached
        }
    }

    Write-Step 'Downloading latest Discord stable x64...'
    $bundled = Join-Path $ToolsDir 'DiscordSetup.exe'
    $ua = @{ 'User-Agent' = 'Disc-Optimizer/1.0 (Windows; PowerShell)' }
    Invoke-WebRequest -Uri $DiscordSetupUrl -OutFile $bundled -UseBasicParsing -Headers $ua
    if (-not (Test-ValidDiscordSetup $bundled)) {
        throw 'Discord x64 installer download failed'
    }
    Write-Ok "Downloaded x64 installer to tools/ ($([math]::Round((Get-Item $bundled).Length / 1MB, 1)) MB)"
    return $bundled
}

function Get-ModulesBundleDir {
    return Join-Path $ToolsDir 'discord-modules'
}

function Get-ModulesBundleVersion {
    $versionFile = Join-Path (Get-ModulesBundleDir) 'version.txt'
    if (-not (Test-Path $versionFile)) { return $null }
    return (Get-Content $versionFile -Raw).Trim()
}

function Test-ModulesBundleReady {
    $bundleDir = Get-ModulesBundleDir
    if (-not (Test-Path $bundleDir)) { return $false }
    foreach ($name in $RequiredModules) {
        if (-not (Test-Path (Join-Path $bundleDir $name))) { return $false }
    }
    return $true
}

function Export-DiscordModulesBundle([string]$AppDir) {
    if (-not (Test-DiscordModulesReady $AppDir)) { return }

    $moduleNames = @($RequiredModules)

    $bundleDir = Get-ModulesBundleDir
    if (-not (Test-Path $bundleDir)) {
        New-Item -ItemType Directory -Path $bundleDir -Force | Out-Null
    }

    Get-ChildItem $bundleDir -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

    $version = (Split-Path $AppDir -Leaf) -replace '^app-', ''
    foreach ($name in $moduleNames) {
        $src = Join-Path $AppDir "modules\$name"
        $dest = Join-Path $bundleDir $name
        Copy-Item $src $dest -Recurse -Force
    }

    Set-Content -Path (Join-Path $bundleDir 'version.txt') -Value $version -NoNewline
    $sizeMb = [math]::Round((Get-FolderSize $bundleDir) / 1MB, 1)
    Write-Ok "Cached $($moduleNames.Count) Discord modules in tools/discord-modules ($version, $sizeMb MB)"
}

function Restore-DiscordModulesBundle([string]$AppDir) {
    if (-not (Test-ModulesBundleReady)) { return $false }

    $bundleDir = Get-ModulesBundleDir
    $appVersion = (Split-Path $AppDir -Leaf) -replace '^app-', ''
    $bundleVersion = Get-ModulesBundleVersion
    if ($bundleVersion -and $bundleVersion -ne $appVersion) {
        Write-Warn "Module bundle is $bundleVersion, Discord is $appVersion - using bundle anyway"
    }

    Write-Step 'Restoring Discord modules from tools/ bundle (instant)...'
    $modDir = Join-Path $AppDir 'modules'
    if (-not (Test-Path $modDir)) {
        New-Item -ItemType Directory -Path $modDir -Force | Out-Null
    }

    $moduleNames = @(Get-ChildItem $bundleDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne 'version.txt' } |
        Select-Object -ExpandProperty Name)

    foreach ($name in $moduleNames) {
        $src = Join-Path $bundleDir $name
        $dest = Join-Path $modDir $name
        if (Test-Path $dest) { Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue }
        Copy-Item $src $dest -Recurse -Force
    }

    if (Test-DiscordModulesReady $AppDir) {
        Write-Ok "Discord modules restored from bundle ($bundleVersion, $($moduleNames.Count) modules)"
        return $true
    }

    Write-Warn 'Module bundle incomplete for boot - falling back to stock first-run'
    return $false
}

function Get-BundledEquicordAsar {
    foreach ($name in @('desktop.asar', 'equicord.asar')) {
        $path = Join-Path $ToolsDir $name
        if (Test-ValidEquicordAsar $path) { return $path }
    }
    return $null
}

function Get-BundledOpenAsar {
    foreach ($name in @('openasar.asar', 'OpenAsar.asar')) {
        $path = Join-Path $ToolsDir $name
        if ((Test-Path $path) -and (Get-Item $path).Length -gt 10000 -and (Get-Item $path).Length -lt 500000) {
            return $path
        }
    }
    return $null
}

function Get-BundledEquilotGui {
    foreach ($name in @('Equilotl.exe', 'equilotl.exe')) {
        $path = Join-Path $ToolsDir $name
        if ((Test-Path $path) -and (Get-Item $path).Length -gt 1000000) { return $path }
    }
    return $null
}

function Get-BundledEquilotCli {
    foreach ($name in @('EquilotlCli.exe', 'equilotlcli.exe')) {
        $path = Join-Path $ToolsDir $name
        if ((Test-Path $path) -and (Get-Item $path).Length -gt 1000000) { return $path }
    }
    return $null
}

function Ensure-EquilotCli {
    $bundled = Get-BundledEquilotCli
    if ($bundled) {
        Write-Ok "Using bundled EquilotlCli ($([math]::Round((Get-Item $bundled).Length / 1MB, 1)) MB from tools/)"
        return $bundled
    }

    if (-not (Test-Path $ToolsDir)) {
        New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null
    }

    # Always prefer the official Equicord installer over our manual patch path -
    # it is the battle-tested way to install Equicord + OpenASAR.
    $cli = Join-Path $ToolsDir 'EquilotlCli.exe'
    Write-Step 'Downloading official Equicord installer (EquilotlCli.exe)...'
    try {
        $ua = @{ 'User-Agent' = 'Disc-Optimizer/1.0 (Windows; PowerShell)' }
        Invoke-WebRequest -Uri $EquilotCliUrl -OutFile $cli -UseBasicParsing -Headers $ua
    } catch {
        Write-Warn "EquilotlCli download failed: $($_.Exception.Message)"
        Write-LogLine 'WARN' "EquilotlCli download failed: $($_.Exception.Message)"
        return $null
    }
    if (-not (Test-Path $cli) -or (Get-Item $cli).Length -lt 1000000) {
        Write-Warn 'EquilotlCli.exe download looked invalid - falling back to direct install'
        return $null
    }
    Write-Ok "EquilotlCli ready ($([math]::Round((Get-Item $cli).Length / 1MB, 1)) MB)"
    return $cli
}

function Test-EquicordLoaderPatched([string]$AppDir) {
    $appAsar = Join-Path $AppDir 'resources\app.asar'
    return (Test-Path $appAsar) -and (Get-Item $appAsar).Length -lt 4096
}

function Invoke-EquilotCli {
    param(
        [Parameter(Mandatory)][string]$EquilotPath,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    Write-LogLine 'STEP' "Equilot: $([IO.Path]::GetFileName($EquilotPath)) $($Arguments -join ' ')"
    $proc = Start-Process -FilePath $EquilotPath -ArgumentList (Join-DiscOptProcessArguments $Arguments) -Wait -PassThru -NoNewWindow
    if ($null -ne $proc.ExitCode -and $proc.ExitCode -ne 0) {
        throw "Equilot exited with code $($proc.ExitCode)"
    }
}

function Install-ViaEquilot([string]$AppDir) {
    $equilot = Ensure-EquilotCli
    if (-not $equilot) { return $false }

    $resources = Join-Path $AppDir 'resources'
    $openasarOk = $SkipOpenAsar -or (Test-OpenAsarInstalled $resources)
    if ((Test-EquicordLoaderPatched $AppDir) -and $openasarOk) {
        Write-Ok 'Equicord + OpenASAR OK - skipped Equilot repair (already patched)'
        Apply-EquicordProfile -AppDir $AppDir
        return $true
    }

    Write-Step 'Installing Equicord + OpenASAR via Equilot (tools/)...'
    $locArgs = @('--location', $DiscordRoot)
    Stop-Discord

    if (Test-EquicordLoaderPatched $AppDir) {
        Write-Ok 'Equicord already patched - repairing/updating via Equilot'
        Invoke-EquilotCli -EquilotPath $equilot -Arguments (@('--repair') + $locArgs)
    } else {
        Invoke-EquilotCli -EquilotPath $equilot -Arguments (@('--install') + $locArgs)
        Write-Ok 'Equicord installed via Equilot'
    }

    if (-not $SkipOpenAsar) {
        $resources = Join-Path $AppDir 'resources'
        if (Test-OpenAsarInstalled $resources) {
            Write-Ok 'OpenASAR already active'
        } else {
            Invoke-EquilotCli -EquilotPath $equilot -Arguments (@('--install-openasar') + $locArgs)
            Write-Ok 'OpenASAR installed via Equilot'
        }
    } else {
        Write-Warn 'Skipped OpenASAR install (-SkipOpenAsar)'
    }

    Apply-EquicordProfile -AppDir $AppDir
    return $true
}

function Resolve-EquicordDesktopAsar([string]$DestPath) {
    $bundled = Get-BundledEquicordAsar
    if ($bundled) {
        Copy-Item $bundled $DestPath -Force
        Write-Ok "Using bundled Equicord ($([math]::Round((Get-Item $bundled).Length / 1MB, 1)) MB from tools/)"
        return @{ Tag = 'bundled'; Size = (Get-Item $bundled).Length; Source = 'tools' }
    }

    try {
        $result = Get-EquicordReleaseFile -FileName 'desktop.asar' -OutFile $DestPath
        $toolsCopy = Join-Path $ToolsDir 'desktop.asar'
        if (-not (Test-Path $ToolsDir)) { New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null }
        Copy-Item $DestPath $toolsCopy -Force -ErrorAction SilentlyContinue
        return $result
    } catch {
        if (Test-ValidEquicordAsar $DestPath) {
            $cached = (Get-Item $DestPath).Length
            Write-Warn "Download failed - using cached equicord.asar ($([math]::Round($cached / 1MB, 1)) MB)"
            Write-LogLine 'WARN' "Equicord download failed, using cache: $($_.Exception.Message)"
            return @{ Tag = 'cached'; Size = $cached; Source = 'cache' }
        }
        throw
    }
}

function Wait-DiscordReady {
    param([int]$TimeoutSec = 180)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-DiscordReady) { return $true }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Test-DiscordModulesReady([string]$AppDir) {
    $modPath = Join-Path $AppDir 'modules'
    if (-not (Test-Path $modPath)) { return $false }
    foreach ($name in $RequiredModules) {
        if (-not (Test-Path (Join-Path $modPath $name))) { return $false }
    }
    return $true
}

function Use-StockDiscordRuntime([string]$AppDir) {
    $resources = Join-Path $AppDir 'resources'
    $appAsar = Join-Path $resources 'app.asar'
    $innerAsar = Join-Path $resources '_app.asar'
    $stockBackup = Join-Path $resources '_app.asar.stock'
    $equilotBackup = Join-Path $resources 'app.asar.backup'

    if ((Test-Path $appAsar) -and (Get-Item $appAsar).Length -lt 4096) {
        if (Test-Path $stockBackup) {
            Copy-Item $stockBackup $appAsar -Force
        } elseif (Test-Path $equilotBackup) {
            Copy-Item $equilotBackup $appAsar -Force
        } elseif (Test-Path $innerAsar) {
            $inner = Get-Item $innerAsar
            if ($inner.Length -gt 1000000) { Copy-Item $inner.FullName $appAsar -Force }
        }
    }

    if ((Test-Path $innerAsar) -and (Get-Item $innerAsar).Length -lt 500000) {
        if (Test-Path $stockBackup) {
            Copy-Item $stockBackup $innerAsar -Force
        } elseif (Test-Path $equilotBackup) {
            Copy-Item $equilotBackup $innerAsar -Force
        }
    }

    $ffmpegReal = Join-Path $AppDir 'ffmpeg_real.dll'
    if (Test-Path $ffmpegReal) {
        Copy-Item $ffmpegReal (Join-Path $AppDir 'ffmpeg.dll') -Force
    }

    foreach ($name in @('version.dll', 'config.ini')) {
        $path = Join-Path $AppDir $name
        if (Test-Path $path) {
            $disabled = "$path.disabled"
            if (Test-Path $disabled) { Remove-Item $disabled -Force }
            Rename-Item $path $disabled -Force -ErrorAction SilentlyContinue
        }
    }
}

function Restore-StockDiscordBase {
    if (-not (Test-DiscordReady)) { return }
    $app = Get-ActiveApp
    Write-Step 'Restoring stock Discord base (default, before updates/mods)...'
    Use-StockDiscordRuntime $app.FullName
    Write-Ok 'Stock Discord base restored'
}

function Update-DiscordSilent {
    Repair-DiscordInstallerState
    $setup = Get-DiscordSetup
    $before = if (Test-DiscordReady) { (Get-ActiveApp).Name } else { $null }

    Write-Step 'Updating Discord to latest stable x64 (silent, keeps your install)...'
    $proc = Start-Process -FilePath $setup -ArgumentList '-s' -PassThru -Wait
    if ($null -ne $proc.ExitCode -and $proc.ExitCode -ne 0) {
        Write-Warn "DiscordSetup exited with code $($proc.ExitCode)"
    }

    if (-not (Wait-DiscordReady 120)) {
        throw 'Discord update timed out - check internet or put DiscordSetup-x64.exe in tools/'
    }

    $after = (Get-ActiveApp).Name
    if ($before -and $before -ne $after) {
        Write-Ok "Discord updated: $before -> $after"
    } else {
        Write-Ok "Discord up to date ($after)"
    }
}

function Invoke-SquirrelFirstRun([string]$AppDir) {
    $exe = Join-Path $AppDir 'Discord.exe'
    if (-not (Test-Path $exe)) { return }
    Write-Step 'Discord first-run init (--squirrel-firstrun)...'
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = '--squirrel-firstrun'
    $psi.WorkingDirectory = $AppDir
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardOutput = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $null = $proc.StandardError.ReadToEndAsync()
    $null = $proc.StandardOutput.ReadToEndAsync()
    $deadline = (Get-Date).AddSeconds(60)
    while (-not $proc.HasExited -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 1
    }
    if (-not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
    Stop-Discord
    Write-Ok 'Discord first-run init done'
}

function Invoke-DiscordSetupSilent {
    $setup = Get-DiscordSetup
    Write-Step 'Installing Discord (stock, silent)...'
    $proc = Start-Process -FilePath $setup -ArgumentList '-s' -PassThru -Wait
    if ($null -ne $proc.ExitCode -and $proc.ExitCode -ne 0) {
        Write-Warn "DiscordSetup exited with code $($proc.ExitCode)"
    }
    if (-not (Wait-DiscordReady 120)) {
        throw 'Discord install timed out - check internet or put DiscordSetup.exe in tools/'
    }
    $app = Get-ActiveApp
    Invoke-SquirrelFirstRun $app.FullName
    $Script:DiscordInstalledThisRun = $true
}

function Backup-DiscordInstallerDb {
    $src = Join-Path $DiscordRoot 'installer.db'
    if (-not (Test-Path $src)) { return }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    Copy-Item $src (Join-Path $DiscordRoot "installer.db.bak-$stamp") -Force -ErrorAction SilentlyContinue
}

function Test-DiscordInstallerHealthy {
    $db = Join-Path $DiscordRoot 'installer.db'
    if (-not (Test-Path $db)) { return $false }
    if ((Get-Item $db).Length -lt 4096) { return $false }
    $log = Join-Path $AppData 'logs\Discord_updater_rCURRENT.log'
    if (Test-Path $log) {
        $last = Select-String -Path $log -Pattern 'hosts_req_modules_installed: true' -ErrorAction SilentlyContinue |
            Select-Object -Last 1
        if ($last) { return $true }
    }
    return (Test-DiscordModulesReady (Get-ActiveApp).FullName)
}

function Repair-DiscordInstallerState {
    Stop-Discord
    Start-Sleep -Seconds 3
    Get-Process Discord, Discord.bin, Update -ErrorAction SilentlyContinue | Stop-Process -Force
    Backup-DiscordInstallerDb
    $log = Join-Path $DiscordRoot 'SquirrelSetup.log'
    if (Test-Path $log) { Remove-Item $log -Force -ErrorAction SilentlyContinue }
}

function Wait-DiscordMainWindow {
    param([int]$TimeoutSec = 90)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $win = Get-Process Discord -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowTitle -like '*Discord*' } |
            Select-Object -First 1
        if ($win) { return $true }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Get-DiscordWindowState {
    $win = Get-Process Discord -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero -and $_.MainWindowTitle } |
        Sort-Object WorkingSet64 -Descending |
        Select-Object -First 1
    if (-not $win) { return 'none' }

    $title = $win.MainWindowTitle
    if ($title -match ' - Discord$' -and $title -notmatch '^discord\.com') { return 'logged_in' }
    if ($title -match '^(Friends|Inbox|Library|Nitro|Shop|Discover|Activity) - Discord$') { return 'logged_in' }
    if ($title -match 'discord\.com') { return 'loading' }
    if ($title -eq 'Discord') { return 'login_or_loading' }
    return 'unknown'
}

function Test-DiscordSessionStorage {
    param([string]$DbDir, [int]$MinBytes = 2048)
    if (-not (Test-Path $DbDir)) { return $false }
    $files = Get-ChildItem $DbDir -File -ErrorAction SilentlyContinue |
        Where-Object { ($_.Extension -in @('.ldb', '.log')) -and $_.Name -ne 'LOCK' }
    if (-not $files) { return $false }
    return (($files | Measure-Object -Property Length -Sum).Sum -ge $MinBytes)
}

function Test-DiscordLoggedIn {
    $indexedDb = Join-Path $AppData 'IndexedDB\https_discord.com_0.indexeddb.leveldb'
    $localDb = Join-Path $AppData 'Local Storage\leveldb'
    if ((Test-DiscordSessionStorage $indexedDb) -and (Test-DiscordSessionStorage $localDb)) {
        return $true
    }

    if ((Get-DiscordWindowState) -eq 'logged_in') {
        $rendererLog = Join-Path $AppData 'logs\renderer_js.log'
        if (Test-Path $rendererLog) {
            $tail = @(Get-Content $rendererLog -Tail 100 -ErrorAction SilentlyContinue) -join "`n"
            if ($tail -match 'Dispatching CONNECTION_OPEN|Dispatching LOGIN_SUCCESS|\[GatewaySocket\] \[READY\]') {
                return $true
            }
        }
    }
    return $false
}

function Ensure-DiscordLoggedIn([string]$AppDir) {
    if (Test-DiscordLoggedIn) {
        Write-Ok 'Discord session found - already logged in (session will not be touched)'
        return
    }

    Write-Host ''
    Write-Host '  >>> Log in to Discord in the window that opens.' -ForegroundColor Yellow
    Write-Host '  >>> The optimizer waits until you are logged in, then applies mods.' -ForegroundColor Yellow
    Write-Host '  >>> Your login is saved before any optimization runs.' -ForegroundColor Yellow
    Write-Host ''

    if (-not (Get-Process Discord -ErrorAction SilentlyContinue)) {
        [void](Invoke-DiscordLaunch -AppDir $AppDir)
    }

    $deadline = (Get-Date).AddMinutes(15)
    $lastHint = [DateTime]::MinValue
    while ((Get-Date) -lt $deadline) {
        if (Test-DiscordLoggedIn) { break }

        $state = Get-DiscordWindowState
        if ($state -eq 'logged_in') {
            Start-Sleep -Seconds 5
            if (Test-DiscordLoggedIn) { break }
        }

        if (((Get-Date) - $lastHint).TotalSeconds -ge 30) {
            Write-Host "  ... waiting for login (window: $state)" -ForegroundColor DarkGray
            $lastHint = Get-Date
        }
        Start-Sleep -Seconds 2
    }

    if (-not (Test-DiscordLoggedIn)) {
        Stop-Discord
        throw 'Login not detected within 15 minutes. Log in to Discord, then rerun Disc-Optimizer.ps1'
    }

    Write-Ok 'Login verified - session saved; applying optimizations now'
    Start-Sleep -Seconds 4
    Stop-Discord
    Start-Sleep -Seconds 2
}

function Ensure-DiscordBootReady([string]$AppDir) {
    if (Test-DiscordInstallerHealthy) {
        Write-Ok 'Discord installer state healthy'
        return
    }

    $ffmpegReal = Join-Path $AppDir 'ffmpeg_real.dll'
    if (Test-Path $ffmpegReal) {
        Copy-Item $ffmpegReal (Join-Path $AppDir 'ffmpeg.dll') -Force
    }

    Write-Step 'Repairing Discord boot (installer DB + module handshake)...'
    Repair-DiscordInstallerState
    Invoke-SquirrelFirstRun $AppDir
    $updateExe = Join-Path $DiscordRoot 'Update.exe'

    if (-not (Test-DiscordModulesReady $AppDir)) {
        Write-Step 'Waiting for Discord updater to install required modules...'
        [void](Invoke-DiscordLaunch -AppDir $AppDir)
        $deadline = (Get-Date).AddMinutes(6)
        while ((Get-Date) -lt $deadline) {
            if (Test-DiscordModulesReady $AppDir) { break }
            Start-Sleep -Seconds 2
        }
        Stop-Discord
    }

    if (-not (Test-DiscordModulesReady $AppDir)) {
        throw 'Discord modules not ready - quit Discord from tray, rerun Disc-Optimizer.ps1'
    }

    [void](Invoke-DiscordLaunch -AppDir $AppDir)
    if (-not (Wait-DiscordMainWindow 90)) {
        Stop-Discord
        throw 'Discord did not reach the main window - boot repair failed'
    }
    Stop-Discord
    if (-not $SkipKernel) { Install-DiscOptKernel $AppDir }
    Write-Ok 'Discord boot verified (main window reached)'
}

function Install-DiscordModulesFromManifest([string]$AppDir) {
    $helper = Join-Path $ToolsDir 'Install-DiscordModules.ps1'
    if (-not (Test-Path $helper)) { return $false }

    $version = (Split-Path $AppDir -Leaf) -replace '^app-', ''
    Write-Step 'Downloading Discord modules from CDN (fast)...'

    $helperPwsh = Get-DiscOptPowerShellExe
    if (-not (Test-Path $helperPwsh)) {
        Write-Warn 'PowerShell 7 (pwsh) required for fast module download - using stock first-run'
        return $false
    }

    $global:LASTEXITCODE = 0
    & $helperPwsh -NoProfile -File $helper -AppDir $AppDir -Version $version
    $helperExit = $LASTEXITCODE
    if ($null -ne $helperExit -and $helperExit -ne 0) {
        Write-Warn 'CDN module install failed - falling back to stock first-run'
        return $false
    }

    if (Test-DiscordModulesReady $AppDir) {
        Write-Ok 'Discord modules installed from CDN'
        Export-DiscordModulesBundle $AppDir
        return $true
    }

    Write-Warn 'CDN modules incomplete - falling back to stock first-run'
    return $false
}

function Initialize-DiscordModules([string]$AppDir) {
    if (Test-DiscordModulesReady $AppDir) {
        Write-Ok 'Discord modules already installed'
        return
    }

    if (Restore-DiscordModulesBundle $AppDir) {
        return
    }

    if (Test-DiscordInstallerHealthy) {
        if (Install-DiscordModulesFromManifest $AppDir) {
            return
        }
    } else {
        Write-Warn 'Installer DB not healthy - skipping CDN module drop (use updater first)'
    }

    Repair-DiscordInstallerState

    Write-Step 'Installing Discord modules (stock first-run - needs internet)...'
    Use-StockDiscordRuntime $AppDir

    $updateExe = Join-Path $DiscordRoot 'Update.exe'
    $packagesDir = Join-Path $DiscordRoot 'packages'
    if (Test-Path $packagesDir) {
        $nupkg = Get-ChildItem $packagesDir -Filter '*.nupkg' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($nupkg) {
            Write-LogLine 'STEP' "Applying pending package: $($nupkg.Name)"
            Start-Process -FilePath $updateExe -ArgumentList (Join-DiscOptProcessArguments @('--update', $nupkg.FullName)) -Wait -ErrorAction SilentlyContinue | Out-Null
            if (Test-DiscordModulesReady $AppDir) {
                Write-Ok 'Discord modules installed via update package'
                Stop-Discord
                Export-DiscordModulesBundle $AppDir
                return
            }
        }
    }

    for ($attempt = 1; $attempt -le 2; $attempt++) {
        if ($attempt -gt 1) {
            Write-Warn 'Retrying module install (one more stock launch)...'
        }
        [void](Invoke-DiscordLaunch -AppDir $AppDir)

        $deadline = (Get-Date).AddMinutes(6)
        $lastMsg = Get-Date
        while ((Get-Date) -lt $deadline) {
        if (Test-DiscordModulesReady $AppDir) {
            Write-Ok 'Discord modules installed'
            Stop-Discord
            Export-DiscordModulesBundle $AppDir
            return
        }
            if (((Get-Date) - $lastMsg).TotalSeconds -ge 10) {
                $missing = @($RequiredModules | Where-Object { -not (Test-Path (Join-Path $AppDir "modules\$_")) })
                Write-LogLine 'STEP' "Waiting for modules: $($missing -join ', ')"
                $lastMsg = Get-Date
            }
            Start-Sleep -Seconds 1
        }
        Stop-Discord
        Start-Sleep -Seconds 1
    }

    throw 'Discord module install timed out - open stock Discord once manually, then run -Quick'
}

function Remove-DiscordInstall {
    Write-Step 'Removing existing Discord install...'
    Stop-Discord

    if (Test-Path $DiscordRoot) {
        Remove-Item $DiscordRoot -Recurse -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        if (Test-Path $DiscordRoot) {
            Get-ChildItem $DiscordRoot -Recurse -Force -ErrorAction SilentlyContinue |
                Remove-Item -Force -Recurse -ErrorAction SilentlyContinue
            Remove-Item $DiscordRoot -Force -ErrorAction SilentlyContinue
        }
    }

    foreach ($shortcut in @(
        (Get-DiscOptEnvPath 'APPDATA' 'Microsoft\Windows\Start Menu\Programs\Discord Inc'),
        (Get-DiscOptEnvPath 'APPDATA' 'Microsoft\Windows\Start Menu\Programs\Discord.lnk')
    )) {
        if ($shortcut -and (Test-Path $shortcut)) {
            Remove-Item $shortcut -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    if (Test-Path $DiscordRoot) {
        throw 'Could not remove Discord folder - close Discord completely and run again'
    }
    Write-Ok 'Old Discord install removed'
}

function Prepare-Discord {
    if ($SkipDiscordInstall) {
        Write-Ok 'Discord prep skipped (-SkipDiscordInstall / -Quick)'
        return
    }

    if ($FreshInstall) {
        Remove-DiscordInstall
        Invoke-DiscordSetupSilent
        $app = Get-ActiveApp
        Invoke-SquirrelFirstRun $app.FullName
        if (-not (Test-DiscordModulesReady $app.FullName)) {
            Initialize-DiscordModules $app.FullName
        }
        Stop-Discord
        Sync-DiscordModulesBundle $app.FullName
        Write-Ok "Discord $($app.Name) x64 fresh install ready"
        return
    }

    if (Test-DiscordReady) {
        $app = Get-ActiveApp
        if (Test-DiscordModulesReady $app.FullName) {
            Sync-DiscordModulesBundle $app.FullName
            Write-Ok "Discord $($app.Name) ready (modules OK - skipping host update)"
            return
        }
        Write-Warn 'Modules missing - repairing without wiping install...'
        Repair-DiscordInstallerState
        Invoke-SquirrelFirstRun $app.FullName
        Initialize-DiscordModules $app.FullName
        Stop-Discord
        Sync-DiscordModulesBundle $app.FullName
        return
    }

    Invoke-DiscordSetupSilent
    $app = Get-ActiveApp
    if (-not (Test-DiscordModulesReady $app.FullName)) {
        Initialize-DiscordModules $app.FullName
    }
    Stop-Discord
    Sync-DiscordModulesBundle $app.FullName
    Write-Ok "Discord $($app.Name) x64 installed"
}

function Sync-DiscordModulesBundle([string]$AppDir) {
    if (Test-DiscordModulesReady $AppDir) {
        $bundleVersion = Get-ModulesBundleVersion
        $appVersion = (Split-Path $AppDir -Leaf) -replace '^app-', ''
        if (-not (Test-ModulesBundleReady) -or $bundleVersion -ne $appVersion) {
            Export-DiscordModulesBundle $AppDir
        }
    }
}

function Assert-DiscordInstall {
    if (-not (Test-Path $DiscordRoot)) {
        throw 'Discord not installed. Run without -SkipDiscordInstall to download fresh x64 Discord.'
    }
    if (-not (Test-Path (Join-Path $DiscordRoot 'Update.exe'))) {
        throw 'Update.exe missing. Run without -SkipDiscordInstall to reinstall Discord.'
    }
    $app = Get-ActiveApp
    if (-not $app) { throw 'No app-* folder found. Finish the Discord installer first.' }
    return $app
}

function Test-KitIntegrity {
    Write-Step 'Checking kit (portable / new-PC ready)...'
    $required = @(
        (Join-Path $KitDir 'version.dll'),
        (Join-Path $KitDir 'ffmpeg.dll'),
        (Join-Path $KitDir 'config.ini'),
        (Join-Path $Profiles 'equicord-overrides.json'),
        (Join-Path $Profiles 'equicordplugins.json'),
        (Join-Path $Profiles 'vencordplugins.json'),
        (Join-Path $Profiles 'discord.json')
    )
    foreach ($file in $required) {
        if (-not (Test-Path $file)) { throw "Kit incomplete - missing $file" }
    }
    if ((Get-Item (Join-Path $KitDir 'ffmpeg.dll')).Length -lt 10000) {
        throw 'Bundled ffmpeg.dll proxy looks invalid'
    }
    if ((Get-Item (Join-Path $KitDir 'version.dll')).Length -lt 50000) {
        throw 'Bundled version.dll looks invalid'
    }
    Write-Ok 'Kit OK (kernel: ffmpeg proxy + version.dll + config.ini)'
    if (Test-Path (Join-Path $Themes $EnabledTheme)) {
        Write-Ok "Theme: $EnabledTheme"
    } else {
        Write-Warn "Missing theme: $EnabledTheme"
    }
    $hasDiscordSetup = $null -ne (Get-BundledDiscordSetup)
    $hasEquilotCli = $null -ne (Get-BundledEquilotCli)
    $hasModulesBundle = Test-ModulesBundleReady
    if ($hasDiscordSetup -and $hasEquilotCli -and $hasModulesBundle) {
        Write-Ok "tools/ fully ready: Discord + EquilotlCli + modules ($(Get-ModulesBundleVersion))"
    } elseif ($hasDiscordSetup -and $hasEquilotCli) {
        Write-Ok 'tools/ has Discord + EquilotlCli (modules bundle will cache on first run)'
    } elseif ($hasDiscordSetup -and (Get-BundledEquilotGui)) {
        Write-Ok 'tools/ has Discord + Equilotl GUI (add EquilotlCli.exe to skip CLI download)'
    } elseif ($hasDiscordSetup) {
        Write-Ok 'tools/ has Discord installer'
    } else {
        Write-Ok 'Tip: put DiscordSetup.exe + EquilotlCli.exe in tools/ for fast setup'
    }
}

function Sync-PluginManifests {
    if ($SkipManifestSync) {
        Write-Ok 'Plugin manifest sync skipped (-Quick / -SkipManifestSync)'
        return
    }
    foreach ($name in @('equicordplugins.json', 'vencordplugins.json')) {
        $dest = Join-Path $Profiles $name
        if ((Test-Path $dest) -and (((Get-Date) - (Get-Item $dest).LastWriteTime).TotalDays -lt 7)) {
            Write-LogLine 'OK' "Manifest fresh (<7 days), skipping download: $name"
            continue
        }
        try {
            Get-EquicordReleaseFile -FileName $name -OutFile $dest | Out-Null
            Write-LogLine 'OK' "Refreshed manifest: $name"
        } catch {
            Write-Warn "Could not refresh $name - using bundled copy"
            Write-LogLine 'WARN' "Manifest refresh failed ($name): $($_.Exception.Message)"
        }
    }
}

function Build-FullEquicordSettings {
    $overridesPath = Join-Path $Profiles 'equicord-overrides.json'
    if (-not (Test-Path $overridesPath)) {
        $overridesPath = Join-Path $Profiles 'equicord.json'
    }
    if (-not (Test-Path $overridesPath)) { throw 'Missing equicord-overrides.json' }

    $overrides = ConvertTo-HashtableDeep (Get-Content $overridesPath -Raw | ConvertFrom-Json)
    $eq = Get-Content (Join-Path $Profiles 'equicordplugins.json') -Raw | ConvertFrom-Json
    $vc = Get-Content (Join-Path $Profiles 'vencordplugins.json') -Raw | ConvertFrom-Json

    $defaultOn = @{}
    foreach ($p in (@($eq) + @($vc))) {
        if ($p.enabledByDefault -eq $true) { $defaultOn[$p.name] = $true }
    }

    $allNames = @($eq.name) + @($vc.name) | Select-Object -Unique
    $plugins = [ordered]@{}
    foreach ($name in ($allNames | Sort-Object)) {
        $plugins[$name] = @{ enabled = [bool]$defaultOn[$name] }
    }

    if ($overrides.plugins) {
        foreach ($key in $overrides.plugins.Keys) {
            $plugins[$key] = $overrides.plugins[$key]
        }
    }

    $enabledThemes = @($EnabledTheme)
    if ($overrides.enabledThemes) { $enabledThemes = @($overrides.enabledThemes) }

    $settings = [ordered]@{
        autoUpdate             = $true
        autoUpdateNotification = $false
        useQuickCss            = $false
        themeLinks             = @()
        eagerPatches           = $false
        enabledThemes          = $enabledThemes
        enabledThemeLinks      = @()
        enableOnlineThemes     = $false
        enableReactDevtools    = $false
        pinnedThemes           = @()
        themeNames             = @{}
        themeActivationModes   = @{}
        mainWindowFrameless    = $false
        frameless              = $false
        transparent            = $false
        winCtrlQ               = $false
        windowsMaterial        = 'none'
        disableMinSize         = $false
        winNativeTitleBar      = $false
        cloud                  = @{
            authenticated       = $false
            url                 = 'https://cloud.equicord.org/'
            settingsSync        = $false
            settingsSyncVersion = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        }
        notifications          = @{
            timeout   = 5000
            position  = 'bottom-right'
            useNative = 'not-focused'
            missed    = $true
            logLimit  = 50
        }
        uiElements             = @{
            chatBarButtons        = @{}
            messagePopoverButtons = @{}
        }
        ignoreResetWarning     = $false
        userCssVars            = @{}
        plugins                = $plugins
    }

    if ($overrides.notifications) { $settings.notifications = $overrides.notifications }

    $enabledCount = ($plugins.Values | Where-Object { $_.enabled -eq $true }).Count
    Write-Ok "Built full Equicord profile: $($allNames.Count) plugins, $enabledCount enabled"

    return $settings
}

function ConvertTo-HashtableDeep($InputObject) {
    if ($null -eq $InputObject) { return $null }
    if ($InputObject -is [string]) { return $InputObject }
    if ($InputObject -is [System.Collections.IDictionary]) {
        $table = @{}
        foreach ($key in $InputObject.Keys) { $table[$key] = ConvertTo-HashtableDeep $InputObject[$key] }
        return $table
    }
    if ($InputObject -is [System.Array]) {
        if ($InputObject.Length -eq 0) { return ,@() }
        return [object[]]@($InputObject | ForEach-Object { ConvertTo-HashtableDeep $_ })
    }
    if ($InputObject -is [System.Collections.IEnumerable] -and -not ($InputObject -is [string])) {
        $items = @($InputObject | ForEach-Object { ConvertTo-HashtableDeep $_ })
        if ($items.Count -eq 0) { return ,@() }
        return [object[]]$items
    }
    if ($InputObject -is [pscustomobject]) {
        $table = @{}
        foreach ($prop in $InputObject.PSObject.Properties) {
            $table[$prop.Name] = ConvertTo-HashtableDeep $prop.Value
        }
        return $table
    }
    return $InputObject
}

function Set-DeepValue([hashtable]$Root, [string[]]$Path, $Value) {
    $node = $Root
    for ($i = 0; $i -lt $Path.Length - 1; $i++) {
        $key = $Path[$i]
        if (-not $node.ContainsKey($key) -or -not ($node[$key] -is [hashtable])) {
            $node[$key] = @{}
        }
        $node = $node[$key]
    }
    $node[$Path[-1]] = $Value
}

function Invoke-Debloat([string]$AppDir, [ref]$Freed) {
    Write-Step 'Debloating Discord...'

    Get-ChildItem $DiscordRoot -Directory -Filter 'app-*' |
        Where-Object { $_.FullName -ne $AppDir } |
        ForEach-Object { if (Remove-Safe $_.FullName $freed) { Write-Ok "Removed $($_.Name)" } }

    $modPath = Join-Path $AppDir 'modules'
    if (Test-Path $modPath) {
        Get-ChildItem $modPath -Directory | Where-Object { $KeepModules -notcontains $_.Name } |
            ForEach-Object { if (Remove-Safe $_.FullName $freed) { Write-Ok "Removed $($_.Name)" } }
        Get-ChildItem "$modPath\discord_modules-1" -Recurse -Filter 'discord_game_sdk_*.dll' -ErrorAction SilentlyContinue |
            ForEach-Object { if (Remove-Safe $_.FullName $freed) { Write-Ok 'Removed game SDK' } }
    }

    $localePath = Join-Path $AppDir 'locales'
    if (Test-Path $localePath) {
        Get-ChildItem "$localePath\*.pak" | Where-Object { $_.Name -ne 'en-US.pak' } |
            ForEach-Object { if (Remove-Safe $_.FullName $freed) { Write-Ok "Removed locale $($_.Name)" } }
    }

    # NOTE: never remove d3dcompiler_47.dll, vulkan-1.dll, vk_swiftshader*, or
    # chrome_*_percent.pak - Chromium needs them for rendering and removing them
    # causes blank/black windows on many GPUs.
    foreach ($pattern in @(
        '.first-run', 'Discord.exe.sig', 'discord_wer.*',
        'Microsoft.Gaming.XboxApp.XboxNetwork.winmd', '*.log'
    )) {
        Get-ChildItem (Join-Path $AppDir $pattern) -ErrorAction SilentlyContinue | ForEach-Object {
            if ($Protected -contains $_.Name) { return }
            if (Remove-Safe $_.FullName $freed) { Write-Ok "Removed $($_.Name)" }
        }
    }

    Write-Ok "Debloat saved ~$([math]::Round($freed.Value / 1MB, 1)) MB"
}

function Clear-DiscordSafeCache([ref]$Freed) {
    if ($SkipCacheClean) {
        Write-Ok 'Cache clean skipped (-SkipCacheClean)'
        return
    }

    Write-Step 'Cleaning safe Discord caches (login/session preserved)...'
    $before = $Freed.Value
    foreach ($relative in $SafeCacheTargets) {
        $path = Join-Path $AppData $relative
        if (Remove-Safe $path $Freed) {
            Write-Ok "Cleaned $relative"
        }
    }

    # Squirrel state (packages\RELEASES, installer.db) is never touched -
    # Update.exe silently refuses to launch Discord without it.
    $saved = $Freed.Value - $before
    if ($saved -gt 0) {
        Write-Ok "Safe cache clean saved ~$([math]::Round($saved / 1MB, 1)) MB"
    } else {
        Write-Ok 'Safe cache clean found nothing to remove'
    }
}

function Test-CacheCleanNeeded {
    if ($SkipCacheClean) { return $false }
    foreach ($relative in $SafeCacheTargets) {
        $path = Join-Path $AppData $relative
        if (-not (Test-Path $path)) { continue }
        # Sample first files only — enough to decide if a clean is worth it
        $sample = @(Get-ChildItem $path -Recurse -Force -File -ErrorAction SilentlyContinue | Select-Object -First 50)
        if ($sample.Count -eq 0) { continue }
        $sum = ($sample | Measure-Object -Property Length -Sum).Sum
        if ($sum -gt 1MB -or $sample.Count -ge 50) { return $true }
    }
    return $false
}

function Get-DiscordManifestCached {
    if ($Script:DiscordManifest) { return $Script:DiscordManifest }
    $Script:DiscordManifest = Invoke-RestMethod -Uri 'https://updates.discord.com/distributions/app/manifests/latest?channel=stable&platform=win&arch=x64' -Headers @{ 'User-Agent' = 'OptiHub-Discord/1.0' }
    return $Script:DiscordManifest
}

function Install-DiscordModuleFromManifest([string]$AppDir, [string]$ModuleName) {
    $folder = Join-Path $AppDir "modules\$ModuleName-1"
    if (Test-Path $folder) { return $true }

    $manifest = Get-DiscordManifestCached
    $mod = $manifest.modules.$ModuleName
    if (-not $mod -or -not $mod.full.url) { throw "$ModuleName missing from Discord manifest" }

    $work = Get-DiscOptTempPath "discopt-$ModuleName"
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item -ItemType Directory -Path $work -Force | Out-Null

    $distro = Join-Path $work 'pkg.distro'
    $tar = Join-Path $work 'pkg.tar'
    $extract = Join-Path $work 'extract'
    try {
        Invoke-WebRequest -Uri $mod.full.url -OutFile $distro -UseBasicParsing

        $in = $out = $br = $null
        try {
            $in = [IO.File]::OpenRead($distro)
            $out = [IO.File]::Create($tar)
            $br = [System.IO.Compression.BrotliStream]::new($in, [IO.Compression.CompressionMode]::Decompress)
            $br.CopyTo($out)
        } finally {
            if ($br) { $br.Dispose() }
            if ($out) { $out.Dispose() }
            if ($in) { $in.Dispose() }
        }

        New-Item -ItemType Directory -Path $extract -Force | Out-Null
        $global:LASTEXITCODE = 0
        & tar -xf $tar -C $extract 2>$null
        if ($LASTEXITCODE -ne 0) { throw "tar failed while extracting $ModuleName" }

        $files = Join-Path $extract 'files'
        if (-not (Test-Path $files)) { throw "$ModuleName package had no files/" }

        $modRoot = Join-Path $AppDir 'modules'
        if (-not (Test-Path $modRoot)) { New-Item -ItemType Directory -Path $modRoot -Force | Out-Null }
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        Copy-Item -Path (Join-Path $files '*') -Destination $folder -Recurse -Force
        return $true
    } finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Ensure-RuntimeModules([string]$AppDir) {
    foreach ($name in $RuntimeModules) {
        $folder = Join-Path $AppDir "modules\$name-1"
        if (Test-Path $folder) {
            Write-Ok "$name module present"
            continue
        }
        Write-Step "Installing $name module (required by Discord core)..."
        Install-DiscordModuleFromManifest $AppDir $name | Out-Null
        Write-Ok "$name module installed"
    }
}

function Ensure-KrispModule([string]$AppDir) {
    $krisp = Join-Path $AppDir 'modules\discord_krisp-1'
    if (Test-Path $krisp) {
        Write-Ok 'Krisp module present (noise suppression UI)'
        return
    }

    Write-Step 'Installing Krisp module (noise suppression dropdown)...'
    Install-DiscordModuleFromManifest $AppDir 'discord_krisp' | Out-Null
    if (-not (Test-Path $krisp)) { throw 'Krisp module missing after CDN install' }
    Write-Ok 'Krisp module installed'
}

function Test-DebloatNeeded([string]$AppDir) {
    $reasons = @()

    $oldApps = @(Get-ChildItem $DiscordRoot -Directory -Filter 'app-*' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $AppDir })
    if ($oldApps.Count -gt 0) { $reasons += "$($oldApps.Count) old app-* folder(s)" }

    $modPath = Join-Path $AppDir 'modules'
    if (Test-Path $modPath) {
        $extraMods = @(Get-ChildItem $modPath -Directory -ErrorAction SilentlyContinue |
            Where-Object { $KeepModules -notcontains $_.Name })
        if ($extraMods.Count -gt 0) { $reasons += "$($extraMods.Count) extra module(s)" }
    }

    $localePath = Join-Path $AppDir 'locales'
    if (Test-Path $localePath) {
        $extraLocales = @(Get-ChildItem "$localePath\*.pak" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne 'en-US.pak' })
        if ($extraLocales.Count -gt 0) { $reasons += "$($extraLocales.Count) extra locale(s)" }
    }

    return @{
        Needed  = ($reasons.Count -gt 0)
        Reasons = $reasons
    }
}

function Disable-DiscordWindowsAutostart {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (-not (Test-Path $runKey)) { return }
    $props = Get-ItemProperty $runKey -ErrorAction SilentlyContinue
    if (-not $props) { return }
    foreach ($prop in $props.PSObject.Properties) {
        if ($prop.Name -match '^PS') { continue }
        if ($prop.Value -match 'Discord') {
            Remove-ItemProperty -Path $runKey -Name $prop.Name -Force -ErrorAction SilentlyContinue
            Write-Ok "Removed startup entry: $($prop.Name)"
        }
    }

    $startupApproved = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
    if (Test-Path $startupApproved) {
        $approved = Get-ItemProperty $startupApproved -ErrorAction SilentlyContinue
        if ($approved) {
            foreach ($prop in $approved.PSObject.Properties) {
                if ($prop.Name -match '^PS') { continue }
                if ($prop.Name -match 'Discord') {
                    Remove-ItemProperty -Path $startupApproved -Name $prop.Name -Force -ErrorAction SilentlyContinue
                    Write-Ok "Removed startup approval: $($prop.Name)"
                }
            }
        }
    }
}

function Disable-DiscordScheduledTasks {
    try {
        # Discord only - matching plain 'Squirrel' would disable other apps'
        # updaters (Slack, GitHub Desktop, Teams classic all use Squirrel).
        $tasks = @(Get-ScheduledTask -ErrorAction SilentlyContinue |
            Where-Object { $_.TaskName -match 'Discord' -or $_.TaskPath -match 'Discord' })
        foreach ($task in $tasks) {
            Disable-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -ErrorAction SilentlyContinue | Out-Null
            Write-Ok "Disabled scheduled task: $($task.TaskPath)$($task.TaskName)"
        }
    } catch {
        Write-LogLine 'WARN' "Scheduled task cleanup skipped: $($_.Exception.Message)"
    }
}

function Set-DiscordWindowsNotificationsOff {
    $base = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
    if (-not (Test-Path $base)) { New-Item -Path $base -Force | Out-Null }

    $setOff = {
        param([string]$Id)
        $path = Join-Path $base $Id
        if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
        Set-ItemProperty -Path $path -Name 'Enabled' -Value 0 -Type DWord -Force
    }

    foreach ($id in @('Discord', 'Discord.Desktop', 'DiscordInc.Discord', 'com.squirrel.Discord.Discord')) {
        & $setOff $id
    }

    Get-ChildItem $base -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match 'Discord' } |
        ForEach-Object {
            Set-ItemProperty -Path $_.PSPath -Name 'Enabled' -Value 0 -Type DWord -Force
            Write-Ok "Windows toasts off: $($_.PSChildName)"
        }
}

function Set-DiscordTrayIconHidden([string]$AppDir) {
    $notifyKey = 'HKCU:\Control Panel\NotifyIconSettings'
    if (-not (Test-Path $notifyKey)) { return }

    $targets = @(
        (Join-Path $AppDir 'Discord.exe'),
        (Get-DiscOptEnvPath 'LOCALAPPDATA' 'Discord\Update.exe'),
        (Join-Path $AppDir 'Discord.bin.exe')
    ) | Where-Object { Test-Path $_ }

    $hidden = 0
    Get-ChildItem $notifyKey -ErrorAction SilentlyContinue | ForEach-Object {
        $props = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
        $path = $props.ExecutablePath
        if (-not $path) { return }
        foreach ($target in $targets) {
            if ($path -ieq $target -or $path -match [regex]::Escape('Discord')) {
                Set-ItemProperty -Path $_.PSPath -Name 'IsPromoted' -Value 0 -Type DWord -Force
                $hidden++
                break
            }
        }
    }

    if ($hidden -gt 0) { Write-Ok "Tray icon hidden ($hidden entries)" }
    else { Write-Warn 'Tray icon registry entry not found yet - launch once, then re-run' }
}

function Apply-WindowsTweaks([string]$AppDir) {
    Write-Step 'Applying Windows tweaks (notifications, tray, startup)...'
    Disable-DiscordWindowsAutostart
    Disable-DiscordScheduledTasks
    Set-DiscordWindowsNotificationsOff
    Set-DiscordTrayIconHidden $AppDir
    Write-Ok 'Windows tweaks applied'
}

function Test-OpenAsarInstalled([string]$ResourcesDir) {
    $target = Join-Path $ResourcesDir '_app.asar'
    if (-not (Test-Path $target)) { return $false }
    $size = (Get-Item $target).Length
    return ($size -gt 10000 -and $size -lt 500000)
}

function Ensure-AsarStockBackup([string]$AppDir) {
    $resources = Join-Path $AppDir 'resources'
    $stockBackup = Join-Path $resources '_app.asar.stock'
    if (Test-Path $stockBackup) { return }

    $candidates = @(
        (Join-Path $resources 'app.asar.backup'),
        (Join-Path $resources '_app.asar'),
        (Join-Path $resources 'app.asar')
    )
    foreach ($src in $candidates) {
        if ((Test-Path $src) -and (Get-Item $src).Length -gt 1000000) {
            Copy-Item $src $stockBackup -Force
            Write-Ok 'Backed up stock bootstrap -> _app.asar.stock'
            return
        }
    }
    Write-Warn 'No _app.asar.stock backup yet'
}

function Install-OpenAsar([string]$AppDir) {
    Write-Step 'Installing OpenASAR (Equicord-compatible)...'
    $resources = Join-Path $AppDir 'resources'
    $target = Join-Path $resources '_app.asar'
    $stockBackup = Join-Path $resources '_app.asar.stock'

    if ($Quick -and (Test-OpenAsarInstalled $resources)) {
        Write-Ok 'OpenASAR already active on _app.asar (-Quick)'
        return
    }

    Ensure-AsarStockBackup $AppDir

    if (-not (Test-Path $target)) {
        throw 'Missing _app.asar - install Equicord loader first'
    }

    if (-not (Test-Path $stockBackup)) {
        if ((Get-Item $target).Length -gt 1000000) {
            Copy-Item $target $stockBackup -Force
            Write-Ok 'Backed up stock Discord bootstrap -> _app.asar.stock'
        }
    }

    $temp = Get-DiscOptTempPath 'discopt-openasar-app.asar'
    $bundled = Get-BundledOpenAsar
    if ($bundled) {
        Copy-Item $bundled $temp -Force
        Write-Ok "Using bundled OpenASAR from tools/ ($([math]::Round((Get-Item $bundled).Length / 1KB, 1)) KB)"
    } else {
        Invoke-WebRequest -Uri $OpenAsarUrl -OutFile $temp -UseBasicParsing
    }
    if ((Get-Item $temp).Length -lt 10000) {
        throw 'Downloaded OpenASAR app.asar looks invalid'
    }

    if (-not (Test-Path $ToolsDir)) { New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null }
    Copy-Item $temp (Join-Path $ToolsDir 'openasar.asar') -Force

    Copy-Item $temp $target -Force
    Write-Ok "OpenASAR nightly installed ($([math]::Round((Get-Item $target).Length / 1KB, 1)) KB on _app.asar)"
}

function Unlock-DiscordSettings([string]$DestPath = '') {
    if (-not $DestPath) { $DestPath = Join-Path $AppData 'settings.json' }
    if (Test-Path $DestPath) { attrib -R $DestPath 2>$null }
}

function Get-DiscOptPowerShellExe {
    $found = Get-DiscOptPwsh77
    if ($found) { return $found.Exe }
    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($pwsh) { return $pwsh.Source }
    return (Get-DiscOptEnvPath 'SystemRoot' 'System32\WindowsPowerShell\v1.0\powershell.exe')
}

function Apply-DiscordProfile([string]$DestPath) {
    Write-Step 'Applying boot/optimizer flags (preserving your in-app settings)...'
    $profilePath = Join-Path $Profiles 'discord.json'
    if (-not (Test-Path $profilePath)) { throw 'Missing profiles/discord.json' }

    $kit = ConvertTo-HashtableDeep (Get-Content $profilePath -Raw -Encoding UTF8 | ConvertFrom-Json)
    $merged = @{}
    if (Test-Path $DestPath) {
        try {
            $merged = ConvertTo-HashtableDeep (Get-Content $DestPath -Raw -Encoding UTF8 | ConvertFrom-Json)
        } catch {}
    }

    if ($merged.ContainsKey('DANGEROUS_ENABLE_DEVTOOLS_ONLY_ENABLE_IF_YOU_KNOW_WHAT_YOURE_DOING')) {
        $merged.Remove('DANGEROUS_ENABLE_DEVTOOLS_ONLY_ENABLE_IF_YOU_KNOW_WHAT_YOURE_DOING')
    }

    # If hardware acceleration was turned off on this PC (GPU-driver black
    # screens, repair fallback, or the user's own choice), never force it back on.
    $hwAccelOff = ($merged.Keys -contains 'enableHardwareAcceleration') -and
        ($merged['enableHardwareAcceleration'] -eq $false)

    $allowed = @(
        'SKIP_HOST_UPDATE', 'OPEN_ON_STARTUP', 'MINIMIZE_TO_TRAY', 'START_MINIMIZED',
        'IS_MAXIMIZED', 'IS_MINIMIZED', 'enableHardwareAcceleration', 'debugLogging', 'offloadAdmControls',
        'asyncVideoInputDeviceInit', 'DESKTOP_TTI_REMOVE_V8_CACHE_CLEAR',
        'DESKTOP_TTI_DNSTCP_WARMUP', 'DESKTOP_TTI_EARLY_UPDATE_CHECK',
        'DESKTOP_TTI_UPDATE_BACKOFF_MAX_MS', 'BACKGROUND_COLOR',
        'audioSubsystem', 'useLegacyAudioDevice'
    )
    foreach ($key in $allowed) {
        if ($kit.Keys -contains $key) { $merged[$key] = $kit[$key] }
    }
    if ($hwAccelOff) {
        $merged['enableHardwareAcceleration'] = $false
        Write-LogLine 'OK' 'Hardware acceleration kept OFF (was disabled on this PC)'
    }

    if ($kit.chromiumSwitches) {
        # Replace (not merge) so stale/risky switches from older runs are removed.
        $merged.chromiumSwitches = ConvertTo-HashtableDeep $kit.chromiumSwitches
    }
    if ($kit.openasar) {
        $merged.openasar = ConvertTo-HashtableDeep $kit.openasar
        $merged.openasar.setup = $true
        # quickstart and domOptimizer are experimental OpenASAR features that can
        # prevent Discord from booting on some machines - keep them off.
        $merged.openasar.quickstart = $false
        $merged.openasar.domOptimizer = $false
        $merged.openasar.themeSync = $false
        $merged.openasar.noTrack = $true
        $merged.openasar.noTyping = $true
    }

    $merged['DESKTOP_TTI_EARLY_UPDATE_CHECK'] = $false
    $merged['DESKTOP_TTI_DNSTCP_WARMUP'] = $false
    $merged['audioSubsystem'] = 'standard'
    $merged['BACKGROUND_COLOR'] = '#000000'

    if ($merged.audioSubsystem -and $merged.audioSubsystem -ne 'standard') {
        Write-LogLine 'WARN' "Reset audioSubsystem $($merged.audioSubsystem) -> standard"
        $merged.audioSubsystem = 'standard'
    }

    $dir = Split-Path $DestPath -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Unlock-DiscordSettings $DestPath

    Write-JsonFile $DestPath $merged 20
    Write-Ok 'Boot/optimizer flags applied (audioSubsystem + TTI flags from profile)'
}

function Invoke-DiscordLaunch {
    param(
        [string]$AppDir,
        [string[]]$ExtraArgs = @('--disable-logging', '--log-level=3')
    )

    $argStr = ($ExtraArgs | Where-Object { $_ }) -join ' '

    # Launch Discord.exe directly - it is the reliable path. Update.exe
    # --processStart depends on Squirrel state (RELEASES/installer.db) and
    # exits silently when that state is unhappy.
    if (-not $AppDir) {
        $active = Get-ActiveApp
        if ($active) { $AppDir = $active.FullName }
    }
    $exe = if ($AppDir) { Join-Path $AppDir 'Discord.exe' } else { $null }
    if ($exe -and (Test-Path $exe)) {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $exe
        $psi.WorkingDirectory = $AppDir
        $psi.Arguments = $argStr
        $psi.UseShellExecute = $true
        return [System.Diagnostics.Process]::Start($psi)
    }

    $updateExe = Join-Path $DiscordRoot 'Update.exe'
    if (Test-Path $updateExe) {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $updateExe
        if ($argStr) {
            $psi.Arguments = "--processStart Discord.exe --process-start-args `"$argStr`""
        } else {
            $psi.Arguments = '--processStart Discord.exe'
        }
        $psi.WorkingDirectory = $DiscordRoot
        $psi.UseShellExecute = $true
        return [System.Diagnostics.Process]::Start($psi)
    }

    throw "Discord.exe not found in $AppDir and Update.exe missing"
}

function Start-Discord([string]$AppDir) {
    Get-Process Discord, Discord.bin, Update -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2

    Unlock-DiscordSettings
    Apply-DiscordProfile (Join-Path $AppData 'settings.json')
    # A version.dll.disabled marker means the kernel was rolled back on this PC
    # (boot safety). Only a full optimize run re-tests and clears it.
    $kernelBlocked = Test-Path (Join-Path $AppDir 'version.dll.disabled')
    if (-not $SkipKernel -and -not $kernelBlocked -and
        -not $Script:KernelRolledBack -and -not $Script:ModsRolledBack) {
        Install-DiscOptKernel $AppDir
    }

    [void](Invoke-DiscordLaunch -AppDir $AppDir)
}

function Wait-UserThenStartDiscord([string]$AppDir) {
    Write-Host '   >> Press any key to restart Discord and close this window.' -ForegroundColor Cyan
    Write-Host ''
    try {
        if ($Host.Name -eq 'ConsoleHost' -and $Host.UI.RawUI) {
            $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
        } else {
            Read-Host 'Press Enter to restart Discord' | Out-Null
        }
    } catch {
        Wait-DiscOptClosePrompt 'Press Enter to restart Discord...'
    }
    Write-Step 'Restarting Discord...'
    Start-Discord $AppDir
    Write-Ok 'Discord restarted - closing this window.'
}

function Write-JsonFile([string]$Path, $Object, [int]$Depth = 20) {
    $dir = Split-Path $Path -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $json = $Object | ConvertTo-Json -Depth $Depth -Compress:$false
    [System.IO.File]::WriteAllText($Path, $json, [System.Text.UTF8Encoding]::new($false))
}

function Merge-HashtableDeep([hashtable]$Base, [hashtable]$Overlay) {
    foreach ($key in @($Overlay.Keys)) {
        $val = $Overlay[$key]
        if ($val -is [hashtable] -and ($Base.Keys -contains $key) -and $Base[$key] -is [hashtable]) {
            Merge-HashtableDeep $Base[$key] $val
        } else {
            $Base[$key] = $val
        }
    }
}

function Get-EquicordSettingsHealth([string]$Path) {
    $result = @{
        Healthy = $false
        Reason  = 'missing'
        Size    = 0
        Plugins = 0
        Enabled = 0
        HasBom  = $false
    }
    if (-not (Test-Path $Path)) { return $result }

    $result.Size = (Get-Item $Path).Length
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $result.HasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191)
    if ($result.HasBom) { $result.Reason = 'utf8-bom'; return $result }
    if ($result.Size -lt 8000) { $result.Reason = 'too-small'; return $result }

    try {
        $s = Get-Content $Path -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $s.plugins) { $result.Reason = 'no-plugins'; return $result }
        $props = @($s.plugins.PSObject.Properties)
        $result.Plugins = $props.Count
        $result.Enabled = (@($props | Where-Object { $_.Value.enabled -eq $true })).Count
        if ($result.Plugins -lt 200) { $result.Reason = 'plugin-count-low'; return $result }
        if ($props.Name -notcontains 'NoTrack') { $result.Reason = 'missing-notrack'; return $result }
        $result.Healthy = $true
        $result.Reason = 'ok'
    } catch {
        $result.Reason = 'parse-error'
    }
    return $result
}

function Test-EquicordSettingsHealthy([string]$Path) {
    return (Get-EquicordSettingsHealth $Path).Healthy
}

function Initialize-EquicordSettingsBase([string]$DestPath) {
    if (Test-EquicordSettingsHealthy $DestPath) {
        return ConvertTo-HashtableDeep (Get-Content $DestPath -Raw -Encoding UTF8 | ConvertFrom-Json)
    }

    Write-Step 'Bootstrapping Equicord plugin registry (one quick launch)...'
    [void](Invoke-DiscordLaunch -AppDir (Get-ActiveApp).FullName)
    Start-Sleep -Seconds 12
    Stop-Discord

    if (Test-EquicordSettingsHealthy $DestPath) {
        return ConvertTo-HashtableDeep (Get-Content $DestPath -Raw -Encoding UTF8 | ConvertFrom-Json)
    }

    Write-Warn 'Using bundled manifests for settings base'
    return Build-FullEquicordSettings
}

function Apply-EquicordProfile {
    param([string]$AppDir = '')

    Write-Step 'Applying Equicord profile (all plugins + your enabled set)...'

    $settingsDir = Join-Path $EquicordData 'settings'
    $themesDir = Join-Path $EquicordData 'themes'
    $destPath = Join-Path $settingsDir 'settings.json'
    if (-not (Test-Path $settingsDir)) { New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null }
    if (-not (Test-Path $themesDir)) { New-Item -ItemType Directory -Path $themesDir -Force | Out-Null }

    Sync-PluginManifests

    $overridesPath = Join-Path $Profiles 'equicord-overrides.json'
    if (-not (Test-Path $overridesPath)) { throw 'Missing equicord-overrides.json' }
    $overrides = ConvertTo-HashtableDeep (Get-Content $overridesPath -Raw -Encoding UTF8 | ConvertFrom-Json)

    $settings = Build-FullEquicordSettings
    $base = Initialize-EquicordSettingsBase $destPath

    foreach ($key in @(
        'mainWindowFrameless', 'frameless', 'transparent', 'winCtrlQ', 'windowsMaterial',
        'disableMinSize', 'winNativeTitleBar', 'themeNames', 'themeActivationModes',
        'uiElements', 'notifications', 'cloud', 'userCssVars', 'ignoreResetWarning'
    )) {
        if ($base.Keys -contains $key) { $settings[$key] = $base[$key] }
    }

    foreach ($key in @($base.plugins.Keys)) {
        if (-not ($settings.plugins.Keys -contains $key)) {
            $settings.plugins[$key] = $base.plugins[$key]
        }
    }

    if ($overrides.plugins) {
        foreach ($key in @($overrides.plugins.Keys)) {
            if (-not ($settings.plugins.Keys -contains $key)) {
                $settings.plugins[$key] = @{}
            }
            Merge-HashtableDeep $settings.plugins[$key] (ConvertTo-HashtableDeep $overrides.plugins[$key])
        }
    }

    $settings.autoUpdateNotification = $false
    $settings.eagerPatches = $false
    $settings.enableOnlineThemes = $false
    $settings.useQuickCss = $false
    $settings.enableReactDevtools = $false
    $settings.mainWindowFrameless = $false
    $settings.frameless = $false
    $settings.transparent = $false
    $settings.windowsMaterial = 'none'
    $settings.winNativeTitleBar = $false
    $settings.cloud.settingsSync = $false
    $settings.cloud.authenticated = $false
    $settings.cloud.url = 'https://cloud.equicord.org/'
    $settings.notifications = @{
        timeout   = 3000
        position  = 'bottom-right'
        useNative = 'never'
        missed    = $false
        logLimit  = 10
    }
    $settings.enabledThemes = @($EnabledTheme)

    foreach ($name in $ForceDisabledPlugins) {
        if (-not ($settings.plugins.Keys -contains $name)) { $settings.plugins[$name] = @{} }
        $settings.plugins[$name].enabled = $false
    }

    Write-JsonFile $destPath $settings 30

    # Remove the broken v1.1 custom theme from user machines - its broad
    # [class*="layer"] selector painted a black overlay over the whole app.
    Get-ChildItem $themesDir -Filter 'discopt-amoled*.theme.css' -ErrorAction SilentlyContinue |
        ForEach-Object {
            Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
            Write-Ok "Removed broken theme: $($_.Name)"
        }

    $themeSrc = Join-Path $Themes $EnabledTheme
    if (Test-Path $themeSrc) {
        Copy-Item $themeSrc (Join-Path $themesDir $EnabledTheme) -Force
    }

    $enabled = ($settings.plugins.Values | Where-Object { $_.enabled -eq $true }).Count
    Write-Ok "Profile: $enabled plugins, AMOLED, voice UI native"
    Write-Ok "Themes on: $($settings.enabledThemes -join ', ')"
    Write-Ok "Plugins enabled: $enabled / $($settings.plugins.Count)"
}

function New-EquicordLoaderAsar([string]$EquicordAsarPath) {
    $escaped = $EquicordAsarPath.Replace('\', '\\')
    $indexJs = "require(`"$escaped`")`n"
    $packageJson = "{`n`t`"name`": `"discord`",`n`t`"main`": `"index.js`"`n}"
    $indexBytes = [Text.Encoding]::UTF8.GetBytes($indexJs)
    $pkgBytes = [Text.Encoding]::UTF8.GetBytes($packageJson)
    $json = '{"files":{"index.js":{"size":' + $indexBytes.Length + ',"offset":"0"},"package.json":{"size":' + $pkgBytes.Length + ',"offset":"' + $indexBytes.Length + '"}}}'
    $jsonBytes = [Text.Encoding]::UTF8.GetBytes($json)
    $jsonPad = (4 - ($jsonBytes.Length % 4)) % 4
    $ms = [IO.MemoryStream]::new()
    $bw = [IO.BinaryWriter]::new($ms)
    $bw.Write([uint32]4)
    $bw.Write([uint32](8 + $jsonBytes.Length))
    $bw.Write([uint32]($jsonBytes.Length + 4))
    $bw.Write([uint32]$jsonBytes.Length)
    $bw.Write($jsonBytes)
    for ($i = 0; $i -lt $jsonPad; $i++) { $bw.Write([byte]0) }
    $bw.Write($indexBytes)
    $bw.Write($pkgBytes)
    $bw.Close()
    return $ms.ToArray()
}

function Test-EquicordReady([string]$AppDir) {
    $resources = Join-Path $AppDir 'resources'
    $loaderOk = Test-EquicordLoaderPatched $AppDir
    $openAsarOk = $SkipOpenAsar -or (Test-OpenAsarInstalled $resources)
    return ($loaderOk -and $openAsarOk)
}

function Install-Equicord([string]$AppDir) {
    Write-Step 'Verifying Equicord + OpenASAR...'
    if (Test-EquicordReady $AppDir) {
        Write-Ok 'Equicord + OpenASAR already installed - applying tweaks only'
        Apply-EquicordProfile -AppDir $AppDir
        return
    }

    Write-Step 'Equicord/OpenASAR missing - installing automatically...'
    if (Install-ViaEquilot $AppDir) { return }

    Write-Step 'Installing Equicord (direct download)...'
    $equicordAsar = Join-Path $EquicordData 'equicord.asar'
    if (-not (Test-Path $EquicordData)) { New-Item -ItemType Directory -Path $EquicordData -Force | Out-Null }

    $dl = Resolve-EquicordDesktopAsar $equicordAsar
    if ($dl.Size -lt 1000000) { throw 'Equicord desktop.asar looks invalid (too small)' }
    $tagLabel = switch ($dl.Source) {
        'tools'   { 'bundled (tools/)' }
        'cache'   { 'cached' }
        'direct'  { 'latest (direct)' }
        'api'     { $dl.Tag }
        default   { $dl.Tag }
    }
    Write-Ok "Equicord $tagLabel ($([math]::Round($dl.Size / 1MB, 1)) MB)"

    $resources = Join-Path $AppDir 'resources'
    $appAsar = Join-Path $resources 'app.asar'
    $backupAsar = Join-Path $resources '_app.asar'
    if (-not (Test-Path $resources)) { throw "Missing $resources" }

    if ((Test-Path $appAsar) -and (Get-Item $appAsar).Length -lt 4096) {
        Write-Ok 'Equicord loader already patched'
    } else {
        if (-not (Test-Path $backupAsar)) {
            if (-not (Test-Path $appAsar)) { throw 'Stock app.asar missing - reinstall Discord' }
            Move-Item $appAsar $backupAsar -Force
            Write-Ok 'Backed up stock app.asar'
        }
        [IO.File]::WriteAllBytes($appAsar, (New-EquicordLoaderAsar $equicordAsar))
        Write-Ok 'Installed Equicord loader'
    }

    Apply-EquicordProfile -AppDir $AppDir
    if (-not $SkipOpenAsar) {
        Install-OpenAsar $AppDir
    } else {
        Write-Warn 'Skipped OpenASAR install (-SkipOpenAsar)'
    }
}

function Install-DiscOptKernel([string]$AppDir) {
    Write-Step 'Installing DiscOpt kernel (memory trim, priority, raw input)...'

    $proxy = Join-Path $KitDir 'ffmpeg.dll'
    $dll = Join-Path $KitDir 'version.dll'
    $ini = Join-Path $KitDir 'config.ini'
    foreach ($file in @($proxy, $dll, $ini)) {
        if (-not (Test-Path $file)) { throw "Missing kernel file: $file" }
    }

    $real = Join-Path $AppDir 'ffmpeg_real.dll'
    $current = Join-Path $AppDir 'ffmpeg.dll'
    if (-not (Test-Path $real)) {
        if (-not (Test-Path $current)) { throw 'Stock ffmpeg.dll missing' }
        if ((Get-Item $current).Length -lt 500000) {
            throw 'ffmpeg_real.dll missing - reinstall Discord'
        }
        Copy-Item $current $real -Force
        Write-Ok 'Saved stock ffmpeg.dll backup'
    }

    Copy-Item $proxy $current -Force
    $verDest = Join-Path $AppDir 'version.dll'
    if (Test-Path $verDest) { attrib -R $verDest 2>$null }
    Copy-Item $dll $verDest -Force
    Copy-Item $ini (Join-Path $AppDir 'config.ini') -Force
    Write-Ok 'DiscOpt kernel active (ffmpeg proxy - memory trim loads on start)'
}

function Disable-DiscOptKernelOnDisk([string]$AppDir) {
    $real = Join-Path $AppDir 'ffmpeg_real.dll'
    if ((Test-Path $real) -and ((Get-Item $real).Length -gt 500000)) {
        Copy-Item $real (Join-Path $AppDir 'ffmpeg.dll') -Force
    }
    foreach ($name in @('version.dll', 'config.ini')) {
        $path = Join-Path $AppDir $name
        if (Test-Path $path) {
            attrib -R $path 2>$null
            $disabled = "$path.disabled"
            if (Test-Path $disabled) { Remove-Item $disabled -Force -ErrorAction SilentlyContinue }
            Rename-Item $path $disabled -Force -ErrorAction SilentlyContinue
        }
    }
}

function Wait-DiscordHealthy {
    param([int]$TimeoutSec = 120)

    # A blank/black page keeps the plain 'Discord' title forever. A healthy,
    # logged-in client reaches a real title ('Friends - Discord', '#chan - Discord').
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $sawWindow = $false
    while ((Get-Date) -lt $deadline) {
        $state = Get-DiscordWindowState
        if ($state -eq 'logged_in') { return $true }
        if ($state -ne 'none') { $sawWindow = $true }
        Start-Sleep -Seconds 2
    }
    if ($sawWindow) {
        Write-LogLine 'WARN' "Discord window stayed in state '$(Get-DiscordWindowState)' (blank page?)"
    } else {
        Write-LogLine 'WARN' 'Discord window never appeared'
    }
    return $false
}

function Confirm-DiscordBootsAfterMods([string]$AppDir) {
    Write-Step 'Boot check: verifying Discord opens and fully loads...'
    Stop-Discord
    [void](Invoke-DiscordLaunch -AppDir $AppDir)
    if (Wait-DiscordHealthy 120) {
        Stop-Discord
        Write-Ok 'Boot check passed (Discord loaded to a real page)'
        return
    }

    Write-Warn 'Discord did not fully load - disabling DiscOpt kernel and retrying...'
    Write-LogLine 'WARN' 'Boot check failed with kernel - trying without kernel'
    Stop-Discord
    Disable-DiscOptKernelOnDisk $AppDir
    [void](Invoke-DiscordLaunch -AppDir $AppDir)
    if (Wait-DiscordHealthy 120) {
        Stop-Discord
        $Script:KernelRolledBack = $true
        Write-Warn 'Kernel disabled automatically - Discord loads without it on this PC.'
        Write-Warn 'Everything else (Equicord, OpenASAR, theme, tweaks) is still active.'
        return
    }

    Write-Warn 'Still not loading - restoring stock Discord (all mods off)...'
    Write-LogLine 'WARN' 'Boot check failed without kernel - restoring stock runtime'
    Stop-Discord
    Use-StockDiscordRuntime $AppDir
    [void](Invoke-DiscordLaunch -AppDir $AppDir)
    if (Wait-DiscordHealthy 150) {
        Stop-Discord
        $Script:KernelRolledBack = $true
        $Script:ModsRolledBack = $true
        Write-Warn 'Stock Discord restored and it loads. Mods were rolled back for safety.'
        return
    }

    Stop-Discord
    throw 'Discord failed to load even in stock mode. Use Repair Discord in OptiHub, or: irm "https://raw.githubusercontent.com/BarcusEric/DiscOpti/main/Repair-Discord.ps1" | iex'
}

function Disable-Fso([string]$AppDir) {
    $exe = Join-Path $AppDir 'Discord.exe'
    if (-not (Test-Path $exe)) { return }
    $key = 'HKCU:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers'
    if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    Set-ItemProperty -Path $key -Name $exe -Value '~ DISABLEDXMAXIMIZEDWINDOWEDMODE' -Force
    Write-Ok 'Disabled fullscreen optimizations'
}

function Restore-StartMenu {
    $app = Get-ActiveApp
    $vbs = Join-Path $KitDir 'Discord.vbs'
    $psExe = (Get-DiscOptPowerShellExe) -replace '"', '""'
    $vbsContent = @"
Set fso = CreateObject("Scripting.FileSystemObject")
kitDir = fso.GetParentFolderName(WScript.ScriptFullName)
rootDir = fso.GetParentFolderName(kitDir)
optimizer = rootDir & "\Disc-Optimizer.ps1"
ps = "$psExe"
CreateObject("WScript.Shell").Run """" & ps & """ -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File """ & optimizer & """ -Launch", 0, False
"@
    Set-Content -Path $vbs -Value $vbsContent -Encoding ASCII

    $icon = Join-Path $app.FullName 'app.ico'
    $folder = Get-DiscOptEnvPath 'APPDATA' 'Microsoft\Windows\Start Menu\Programs\Discord Inc'
    if (-not $folder) { throw 'APPDATA is not set; cannot create Start menu shortcut' }
    $shortcut = Join-Path $folder 'Discord.lnk'
    if (-not (Test-Path $folder)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }
    Remove-Item (Join-Path (Split-Path $folder -Parent) 'Discord.lnk') -Force -ErrorAction SilentlyContinue
    $sc = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
    $wscript = Get-DiscOptEnvPath 'SystemRoot' 'System32\wscript.exe'
    if (-not $wscript) { throw 'SystemRoot is not set; cannot find wscript.exe' }
    $sc.TargetPath = $wscript
    $sc.Arguments = "`"$vbs`" //B"
    $sc.WorkingDirectory = $Root
    $sc.Description = 'Discord (Disc Optimizer)'
    if (Test-Path $icon) { $sc.IconLocation = "$icon,0" }
    $sc.Save()
    Write-Ok 'Start menu -> Discord.vbs (-Launch)'
}

function Test-KernelOnDisk([string]$AppDir) {
    $ok = $true
    foreach ($name in @('version.dll', 'config.ini')) {
        $path = Join-Path $AppDir $name
        if (-not (Test-Path $path)) {
            Write-Warn "Kernel file missing on disk: $name"
            $ok = $false
            continue
        }
        if ($name -eq 'version.dll' -and (Get-Item $path).Length -lt 50000) {
            Write-Warn 'version.dll looks invalid'
            $ok = $false
        } else {
            Write-Ok "Kernel on disk: $name"
        }
    }
    $ffmpegReal = Join-Path $AppDir 'ffmpeg_real.dll'
    if (Test-Path $ffmpegReal) { Write-Ok 'Kernel on disk: ffmpeg_real.dll (stock backup)' }
    $ffmpeg = Join-Path $AppDir 'ffmpeg.dll'
    if (Test-Path $ffmpeg) {
        if ((Get-Item $ffmpeg).Length -lt 500000) {
            Write-Ok 'Kernel on disk: ffmpeg.dll (proxy - memory trim active)'
        } else {
            Write-Warn 'Kernel on disk: ffmpeg.dll still stock (trim inactive until -Launch)'
            $ok = $false
        }
    }
    return $ok
}

function Test-DiscOptimizer {
    $app = Get-ActiveApp
    if (-not $app) { Write-Warn 'No active Discord app folder'; return }

    $resources = Join-Path $app.FullName 'resources'
    $loader = Join-Path $resources 'app.asar'
    $bootstrap = Join-Path $resources '_app.asar'
    $stockBackup = Join-Path $resources '_app.asar.stock'

    if ((Test-Path (Join-Path $EquicordData 'equicord.asar')) -and (Test-Path $loader) -and (Get-Item $loader).Length -lt 4096) {
        Write-Ok 'Equicord loader active (app.asar stub)'
    } elseif (-not $SkipEquicord) {
        Write-Warn 'Equicord loader not verified'
    }

    if (Test-OpenAsarInstalled $resources) {
        $size = [math]::Round((Get-Item $bootstrap).Length / 1KB, 1)
        Write-Ok "OpenASAR active on _app.asar ($size KB)"
    } elseif (-not $SkipOpenAsar -and -not $SkipEquicord) {
        Write-Warn 'OpenASAR not detected on _app.asar'
    }

    if (Test-Path $stockBackup) {
        Write-Ok 'Stock bootstrap backup present (_app.asar.stock)'
    } elseif (-not $SkipOpenAsar -and -not $SkipEquicord) {
        Write-Warn 'No _app.asar.stock backup yet'
    }

    $krispPath = Join-Path $app.FullName 'modules\discord_krisp-1'
    if (Test-Path $krispPath) { Write-Ok 'Krisp module present (voice UI)' }
    else { Write-Warn 'Krisp module missing - None dropdown may not work' }

    if (-not $SkipKernel) {
        if (-not (Test-KernelOnDisk $app.FullName)) {
            Write-Warn 'DiscOpt kernel files incomplete on disk'
        }
        if (Get-Process Discord -ErrorAction SilentlyContinue) {
            try {
                $kernelRunning = Get-Process Discord -ErrorAction SilentlyContinue | ForEach-Object {
                    $ff = $_.Modules | Where-Object { $_.FileName -like '*\Discord\app-*\ffmpeg.dll' } | Select-Object -First 1
                    if ($ff -and (Get-Item $ff.FileName -ErrorAction SilentlyContinue).Length -lt 500000) { return $true }
                } | Select-Object -First 1
                if ($kernelRunning) { Write-Ok 'DiscOpt kernel loaded (ffmpeg proxy in process)' }
            } catch {
                Write-Warn 'Could not inspect running Discord modules; on-disk kernel check completed'
                Write-LogLine 'WARN' "Process module inspection failed: $($_.Exception.Message)"
            }
        }
    }

    $settingsPath = Join-Path $EquicordData 'settings\settings.json'
    if (Test-Path $settingsPath) {
        $eqHealth = Get-EquicordSettingsHealth $settingsPath
        if ($eqHealth.Healthy) {
            $sizeKb = [math]::Round($eqHealth.Size / 1KB, 1)
            Write-Ok "Equicord settings OK ($($eqHealth.Plugins) plugins, $sizeKb KB, no BOM)"
        } else {
            Write-Warn "Equicord settings issue: $($eqHealth.Reason) ($($eqHealth.Plugins) plugins, $($eqHealth.Size) bytes)"
        }
        try {
            $s = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
            Write-Ok "Equicord plugins: $($eqHealth.Enabled) enabled / $($eqHealth.Plugins) listed"
            if ($s.enabledThemes -and $s.enabledThemes.Count -gt 0) {
                Write-Ok "Themes on: $($s.enabledThemes -join ', ')"
            } else {
                Write-Warn 'No themes enabled in settings'
            }
            $bk = $s.plugins.BlockKrisp
            if ($bk -and $bk.enabled -eq $true) { Write-Warn 'BlockKrisp enabled - None dropdown may break' }
            else { Write-Ok 'BlockKrisp off (native noise UI)' }
            $dc = $s.plugins.Declutter
            if ($dc -and $dc.removeAudioMenus -eq $true) { Write-Warn 'Declutter hiding audio menus' }
        } catch {}
    }
}

function Write-RunSummary {
    param(
        [string]$AppDir,
        [bool]$Launched
    )

    $checks = [System.Collections.Generic.List[string]]::new()
    $psLabel = "PowerShell $($PSVersionTable.PSVersion)"
    if ($PSVersionTable.PSEdition) { $psLabel += " ($($PSVersionTable.PSEdition))" }
    $checks.Add($psLabel)

    if ($Launched) {
        $proc = Get-Process Discord -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($proc) { $checks.Add("Discord running (PID $($proc.Id))") }
        else { $checks.Add('Discord launch requested (process not seen yet)') }
    } elseif (-not $NoLaunch) {
        $checks.Add('Discord will restart when you press a key below')
    } else {
        $checks.Add('Discord not started (use Start menu or -Launch)')
    }

    $settingsPath = Join-Path $EquicordData 'settings\settings.json'
    if (Test-Path $settingsPath) {
        $eq = Get-EquicordSettingsHealth $settingsPath
        if ($eq.Healthy) { $checks.Add("Equicord OK - $($eq.Enabled) plugins on") }
        else { $checks.Add("Equicord settings: $($eq.Reason)") }
    }

    if ($Script:ModsRolledBack) {
        $checks.Add('SAFETY: mods rolled back to stock (Discord would not boot with them)')
    } elseif ($Script:KernelRolledBack) {
        $checks.Add('SAFETY: kernel disabled (Discord boots fine without it; all other tweaks active)')
    } elseif ($AppDir) {
        $ff = Join-Path $AppDir 'ffmpeg.dll'
        if ((Test-Path $ff) -and (Get-Item $ff).Length -lt 500000) {
            $checks.Add('DiscOpt kernel on disk (memory trim)')
        } elseif (Test-Path $ff) {
            $checks.Add('Kernel proxy installs on next Discord start')
        }
    }

    if (Test-DiscordLoggedIn) {
        $checks.Add('Login session preserved')
    }

    Write-Host ''
    Write-Host '  ========================================' -ForegroundColor DarkGray
    Write-Host '   DONE - everything applied successfully' -ForegroundColor Green
    Write-Host '  ========================================' -ForegroundColor DarkGray
    foreach ($line in $checks) {
        Write-Host "   [+] $line" -ForegroundColor Green
    }
    Write-Host "   [i] Log: $(Join-Path $LogDir 'last-run.log')" -ForegroundColor DarkGray
    Write-Host ''
}

# --- main ---
try {
Initialize-Network

if ($Launch) {
    $app = Assert-DiscordInstall
    Start-Discord $app.FullName
    exit 0
}

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
        Disable-Fso $app.FullName
    } else {
        Write-Ok 'Debloat skipped (already lean)'
    }
}

if ($Quick -and -not (Test-CacheCleanNeeded)) {
    Write-Ok 'Cache clean skipped (-Quick, caches already lean)'
} else {
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

# 5) DiscOpt kernel
if (-not $SkipKernel) {
    Install-DiscOptKernel $app.FullName
} else {
    Write-Warn 'Skipped DiscOpt kernel (-SkipKernel)'
}

# 5b) Boot safety: full verify on first apply; Quick uses a shorter smoke check
if ($Quick) {
    Write-Step 'Quick boot smoke check...'
    Stop-Discord
    [void](Invoke-DiscordLaunch -AppDir $app.FullName)
    if (Wait-DiscordHealthy 45) {
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
    Disable-DiscordWindowsAutostart
    Write-Ok 'Windows tweaks skipped (-Quick)'
}
Restore-StartMenu

Test-DiscOptimizer

Write-RunSummary -AppDir $app.FullName -Launched $false

if (-not $NoLaunch) {
    Wait-UserThenStartDiscord $app.FullName
} else {
    Write-Ok 'Disc Optimizer finished (no launch - use Start menu or -Launch).'
}

Write-LogLine 'OK' 'Run finished successfully'
Copy-Item -Path $Script:LogPath -Destination (Join-Path $LogDir 'last-run.log') -Force
exit 0
} catch {
    $detail = Write-LogFailure $_
    Write-Host ''
    Write-Err 'Disc Optimizer failed.'
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ''
    Write-Host '  If Discord will not open, paste this into PowerShell to restore it:' -ForegroundColor Yellow
    Write-Host '    irm "https://raw.githubusercontent.com/BarcusEric/DiscOpti/main/Repair-Discord.ps1" | iex' -ForegroundColor Cyan
    Write-Host ''
    Write-Host "  Error log: $(Join-Path $LogDir 'last-error.log')" -ForegroundColor Yellow
    if ($Script:LogPath) {
        Write-Host "  Full log:  $Script:LogPath" -ForegroundColor Yellow
    }
    Write-Host ''
    Wait-DiscOptClosePrompt
    exit 1
}
