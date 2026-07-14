# Saves your live Discord + Equicord settings back into the kit.
# Keeps the curated plugin list but pulls full per-plugin settings from live Discord.
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Profiles = Join-Path $Root 'profiles'
$Themes = Join-Path $Root 'themes'
$LogDir = Join-Path $Root 'logs'
$OverridesPath = Join-Path $Profiles 'equicord-overrides.json'
$Script:LogPath = $null

function Get-DiscOptEnvPath([string]$Name, [string]$Child = '') {
    $base = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($base)) { return $null }
    if ([string]::IsNullOrWhiteSpace($Child)) { return $base }
    return (Join-Path $base $Child)
}

function Wait-DiscOptClosePrompt {
    try {
        Write-Host 'Press Enter to close...'
        Read-Host | Out-Null
    } catch {
        Start-Sleep -Seconds 8
    }
}

$AppData = Get-DiscOptEnvPath 'APPDATA'
$LivePath = Get-DiscOptEnvPath 'APPDATA' 'Equicord\settings\settings.json'

function Initialize-ExportLog {
    if (-not (Test-Path $LogDir)) {
        New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
    }
    $stamp = Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'
    $Script:LogPath = Join-Path $LogDir "export-profile-$stamp.log"
    $header = @(
        'Export-Profile log',
        "Started: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
        "Kit: $Root",
        ('=' * 60),
        ''
    ) -join [Environment]::NewLine
    Set-Content -Path $Script:LogPath -Value $header -Encoding UTF8
}

function Write-LogLine([string]$Level, [string]$Msg) {
    if (-not $Script:LogPath) { return }
    Add-Content -Path $Script:LogPath -Value "[$(Get-Date -Format 'HH:mm:ss')] [$Level] $Msg" -Encoding UTF8
}

function Write-LogFailure($ErrorRecord) {
    if (-not $Script:LogPath) { Initialize-ExportLog }
    $err = $ErrorRecord.Exception
    $inv = $ErrorRecord.InvocationInfo
    $body = @(
        '',
        ('=' * 60),
        "FAILED: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
        "Message: $($err.Message)",
        "Line: $($inv.ScriptLineNumber)",
        "Command: $($inv.Line.Trim())",
        "Stack: $($err.StackTrace)",
        "Full log: $Script:LogPath",
        ('=' * 60)
    ) -join [Environment]::NewLine
    Add-Content -Path $Script:LogPath -Value $body -Encoding UTF8
    Set-Content -Path (Join-Path $LogDir 'last-export-error.log') -Value $body -Encoding UTF8
}

function Write-JsonFile([string]$Path, $Object, [int]$Depth = 20) {
    $dir = Split-Path $Path -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $json = $Object | ConvertTo-Json -Depth $Depth -Compress:$false
    [System.IO.File]::WriteAllText($Path, $json, [System.Text.UTF8Encoding]::new($false))
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

try {
    Initialize-ExportLog
    Write-LogLine 'STEP' 'Stopping Discord...'

    if (-not $AppData) {
        throw 'APPDATA is not set; this tool must run on Windows after Discord has been opened.'
    }

    Get-Process Discord, Discord.bin -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep 2

    if (-not (Test-Path $OverridesPath)) {
        throw "Missing $OverridesPath - run Disc-Optimizer.ps1 first."
    }
    if (-not (Test-Path $LivePath)) {
        throw 'Live Equicord settings not found. Open Discord once, then run this again.'
    }

    Write-LogLine 'STEP' 'Loading overrides and live settings...'
    $overrides = ConvertTo-HashtableDeep (Get-Content $OverridesPath -Raw -Encoding UTF8 | ConvertFrom-Json)
    $live = Get-Content $LivePath -Raw -Encoding UTF8 | ConvertFrom-Json

    if (-not $overrides.plugins) {
        throw 'equicord-overrides.json has no plugins section'
    }

    $exported = 0
    foreach ($name in @($overrides.plugins.Keys)) {
        $curated = $overrides.plugins[$name]
        if ($curated.enabled -ne $true) { continue }
        if (-not $live.plugins.PSObject.Properties.Name.Contains($name)) { continue }

        $merged = ConvertTo-HashtableDeep $live.plugins.$name
        $merged.enabled = $true
        $overrides.plugins[$name] = $merged
        $exported++
    }

    if ($live.enabledThemes) {
        $overrides.enabledThemes = @($live.enabledThemes)
    }

    Write-JsonFile $OverridesPath $overrides 30
    Write-LogLine 'OK' "Exported $exported curated plugins to equicord-overrides.json"

    Copy-Item (Join-Path $AppData 'discord\settings.json') (Join-Path $Profiles 'discord.json') -Force
    Write-LogLine 'OK' 'Copied discord.json'

    Get-ChildItem (Join-Path $AppData 'Equicord\themes\*.css') -ErrorAction SilentlyContinue |
        Copy-Item -Destination $Themes -Force
    Write-LogLine 'OK' 'Synced themes'

    Copy-Item -Path $Script:LogPath -Destination (Join-Path $LogDir 'last-export.log') -Force
    Write-Host 'Exported equicord-overrides.json (curated plugins + full live settings)' -ForegroundColor Green
    Write-Host 'Done. Plugin settings, Discord profile, and themes saved to the kit.' -ForegroundColor Green
    Write-Host "Log: $Script:LogPath" -ForegroundColor DarkGray
} catch {
    Write-LogFailure $_
    Write-Host ''
    Write-Host 'Export-Profile failed.' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host "Error log: $(Join-Path $LogDir 'last-export-error.log')" -ForegroundColor Yellow
    Write-Host ''
    Wait-DiscOptClosePrompt
    exit 1
}
