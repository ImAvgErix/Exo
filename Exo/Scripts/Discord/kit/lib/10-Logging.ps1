# 10-Logging.ps1 - Logging, progress, GitHub downloads
# Dot-sourced by Disc-Optimizer.ps1 (load order = filename sort).
# Universal multi-PC kit - do not assume Equicord/Discord already configured.

# Auto-split from Disc-Optimizer.ps1 - functions only (dot-sourced).
# Requires caller to set kit paths and switches ($KitDir, $Quick, ...).

function Write-Banner {
    $psLabel = "PowerShell $($PSVersionTable.PSVersion)"
    if ($PSVersionTable.PSEdition) { $psLabel += " ($($PSVersionTable.PSEdition))" }
    $pre = ''
    try { $pre = [string]$PSVersionTable.PSVersion.PreReleaseLabel } catch { }
    if ($pre) { $psLabel += " [$pre]" }
    Write-Host ''
    Write-Host "  Disc Optimizer v$Script:DiscOptVersion" -ForegroundColor Magenta
    Write-Host '  AMOLED | privacy | perf | cache trim | raw input' -ForegroundColor DarkGray
    Write-Host "  $psLabel  -  requires PowerShell 7" -ForegroundColor Cyan
    if ($env:DISCOPT_ELEVATED -eq '1') {
        Write-Host '  (running as Administrator)' -ForegroundColor DarkGray
    }
    Write-Host ''
}

function Invoke-KitMaintenance {
    Prune-DiscOptimizerLogs -Keep 5

    # Drop legacy multi-MB module/equicord caches from older kits (download on demand now).
    foreach ($legacy in @(
        (Join-Path $ToolsDir 'discord-modules'),
        (Join-Path $ToolsDir 'desktop.asar'),
        (Join-Path $ToolsDir 'equicord.asar'),
        (Join-Path $ToolsDir 'DiscordSetup.exe'),
        (Join-Path $ToolsDir 'DiscordSetup-x64.exe'),
        (Join-Path $ToolsDir 'Equilotl.exe'),
        (Join-Path $ToolsDir 'EquilotlCli.exe')
    )) {
        if (Test-Path -LiteralPath $legacy) {
            Remove-Item -LiteralPath $legacy -Recurse -Force -ErrorAction SilentlyContinue
        }
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
            Where-Object { ((Get-Date) - $_.LastWriteTime).TotalDays -gt 7 } |
            ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
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

function Write-ExoLogMirror([string]$Line) {
    if (-not $env:EXO_LOG) { return }
    if ([string]::IsNullOrWhiteSpace($Line)) { return }
    try {
        $dir = Split-Path -Parent $env:EXO_LOG
        if ($dir -and -not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        Add-Content -LiteralPath $env:EXO_LOG -Value $Line -Encoding UTF8 -ErrorAction SilentlyContinue
    } catch { }
}

function Write-LogLine([string]$Level, [string]$Msg) {
    if (-not $Script:LogPath) { return }
    $line = "[$(Get-Date -Format 'HH:mm:ss')] [$Level] $Msg"
    Add-Content -Path $Script:LogPath -Value $line -Encoding UTF8
    # Mirror into Exo run log so elevated UI polling always has the trail
    Write-ExoLogMirror $line
}

function Write-LogFailure($ErrorRecord) {
    if (-not $Script:LogPath) { Initialize-DiscOptimizerLog }

    $err = $ErrorRecord.Exception
    $inv = $ErrorRecord.InvocationInfo
    $hubLog = if ($env:EXO_LOG) { $env:EXO_LOG } else { '(none)' }
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
        "KitLog: $Script:LogPath",
        "ExoLog: $hubLog",
        "PSVersion: $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition))",
        "Elevated: $(([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))",
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

    # Always copy failure into Exo logs so we can find it without digging kit/
    try {
        $optiLogs = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Exo\logs'
        if (-not (Test-Path -LiteralPath $optiLogs)) {
            New-Item -ItemType Directory -Path $optiLogs -Force | Out-Null
        }
        Set-Content -LiteralPath (Join-Path $optiLogs 'last-discord-error.log') -Value $body -Encoding UTF8
        if ($Script:LogPath -and (Test-Path -LiteralPath $Script:LogPath)) {
            Copy-Item -LiteralPath $Script:LogPath -Destination (Join-Path $optiLogs 'last-discord-run.log') -Force
        }
    } catch { }

    Write-ExoLogMirror ('[-] FAIL: ' + $err.Message)
    Write-ExoLogMirror ("[-] See: $(Join-Path $LogDir 'last-error.log')")
    if ($env:EXO_LOG) {
        Write-ExoLogMirror ('[-] Exo log: ' + $env:EXO_LOG)
    }
    return $body
}

function Write-HubProgress([int]$Percent, [string]$Status) {
    if ($env:EXO -ne '1') { return }
    $p = [Math]::Max(0, [Math]::Min(100, $Percent))
    $line = "EXO_PROGRESS:$p|$Status"
    # Prefer EXO_LOG so progress never pollutes function return values
    # (Write-Output inside Get-DiscordSetup made Start-Process -FilePath an Object[]).
    if ($env:EXO_LOG) {
        Write-ExoLogMirror $line
    } else {
        Write-Output $line
    }
}

function Write-Step([string]$Msg) {
    Write-Host "[*] $Msg" -ForegroundColor Cyan
    Write-LogLine 'STEP' $Msg
    if ($env:EXO -eq '1') {
        if ($null -eq $Script:HubStepPct) { $Script:HubStepPct = 28 }
        $Script:HubStepPct = [Math]::Min(94, [int]$Script:HubStepPct + 4)
        Write-HubProgress $Script:HubStepPct $Msg
    }
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

function Add-ExoReport([string]$Step, [string]$Status, [string]$Reason = '') {
    # Structured last-apply report line: EXO_REPORT:<step>|ok / |fail:<reason> / |skip:<reason>
    if ($null -eq $Script:ExoApplyReport) {
        $Script:ExoApplyReport = [Collections.Generic.List[string]]::new()
    }
    $entry = if ([string]::IsNullOrWhiteSpace($Reason)) { "$Step|$Status" } else { "$Step|$Status`:$Reason" }
    $Script:ExoApplyReport.Add($entry)
    $line = "EXO_REPORT:$entry"
    Write-Host $line
    Write-ExoLogMirror $line
}

function Get-ExoReportEntries {
    if ($null -eq $Script:ExoApplyReport) { return @() }
    return @($Script:ExoApplyReport)
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
        return Invoke-RestMethod -Uri 'https://api.github.com/repos/Equicord/Equicord/releases/latest' -Headers (Get-GitHubHeaders) -TimeoutSec 45
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
    Invoke-WebRequest -Uri $direct -OutFile $OutFile -UseBasicParsing -Headers $ua -TimeoutSec 120
    if (-not (Test-Path $OutFile)) { throw "Failed to download $FileName" }
    return @{
        Tag    = 'latest'
        Size   = (Get-Item $OutFile).Length
        Source = 'direct'
    }
}

