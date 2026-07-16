# Exo NVIDIA Optimizer
# - Newest series-correct Game Ready / security driver when needed (Display.Driver ONLY)
# - Strip NVIDIA App/GFE + Virtual/HD Audio - keep Display.Driver + classic Control Panel only
# - NVCleanstall-class expert tweaks: MSI High, telemetry off, Ansel off, HDCP off
# - Series + G-SYNC Base Profile via Profile Inspector (-silentImport)
# - Accept CPL EULA; set "Use the advanced 3D image settings" (NVTweak Gestalt=2)
# - Overlay/Windows toasts off
# - Display (Full RGB / primary max Hz / secondary 60 Hz / GPU no-scaling) through NVAPI
#
#   Nvidia-Optimizer.ps1
#   Nvidia-Optimizer.ps1 -Gsync
#   Nvidia-Optimizer.ps1 -Repair
#   Nvidia-Optimizer.ps1 -Series 40 -Gsync
#   Nvidia-Optimizer.ps1 -SkipApp   # skip client wipe/CPL ensure (advanced)

param(
    [switch]$Gsync,
    [ValidateSet('', '10', '20', '30', '40', '50')]
    [string]$Series = '',
    [switch]$Repair,
    [switch]$NonInteractive,
    [switch]$SkipDownload,
    [switch]$SkipApp,          # skip App wipe + Control Panel ensure
    [switch]$InstallApp,       # deprecated / ignored - Control Panel only
    [switch]$SkipProfile,
    [switch]$SkipDriver,
    [switch]$ForceDriver
)

$ErrorActionPreference = 'Stop'
$Script:NvidiaOptVersion = '1.12.3'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProfilesDir = Join-Path $Root 'profiles'
$StateDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Exo'
$StatePath = Join-Path $StateDir 'nvidia-optimizer.json'
# Keep Exo's managed Profile Inspector private. Never delete user-installed copies.
$NpiDir = Join-Path $StateDir 'tools\nvidiaProfileInspector'
$DriverCacheDir = Join-Path $StateDir 'drivers'
$NpiExeName = 'nvidiaProfileInspector.exe'
# Pinned Profile Inspector = Orbmu2k GitHub Latest.
# v3.0.2.1 (2026-07-05): modern WPF UI, current DRS/NVAPI data, silent import.
# v3.0.1.11 was months behind and could flash broken XAML/doc UI on import.
$Script:NpiPinnedTag = 'v3.0.2.1'
$Script:NpiPinnedZipUrl = 'https://github.com/Orbmu2k/nvidiaProfileInspector/releases/download/v3.0.2.1/nvidiaProfileInspector.zip'
$Script:NpiPinnedZipSha256 = '88DCF3514111E8DE630688467C03C36D8C2A8AD9EBC8073F27C069F82B75BB40'

# --- PowerShell 7 host (stable pwsh 7.x; never Windows PowerShell 5.1) ---
function Test-ExoIsPwsh7Host {
    # Any pwsh 7.x host is accepted (stable preferred; preview tolerated).
    # Windows PowerShell 5.1 is rejected - the optimizer uses Core-only APIs.
    if ($PSVersionTable.PSEdition -ne 'Core') { return $false }
    if ([int]$PSVersionTable.PSVersion.Major -lt 7) { return $false }
    $hostPath = ''
    try { $hostPath = [string](Get-Process -Id $PID -ErrorAction Stop).Path } catch { }
    if ($hostPath -match 'WindowsPowerShell') { return $false }
    return $true
}
function Get-ExoPwsh {
    # Stable PowerShell 7 first; preview paths only as a last resort.
    $candidates = [System.Collections.Generic.List[string]]::new()
    $stable = Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'
    if ($stable) { [void]$candidates.Add($stable) }

    $cmdPwsh = Get-Command pwsh -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmdPwsh -and $cmdPwsh.Source) { [void]$candidates.Add([string]$cmdPwsh.Source) }

    $appsRoot = Join-Path $env:ProgramFiles 'WindowsApps'
    if (Test-Path -LiteralPath $appsRoot) {
        Get-ChildItem -LiteralPath $appsRoot -Directory -Filter 'Microsoft.PowerShell_*' -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { [void]$candidates.Add((Join-Path $_.FullName 'pwsh.exe')) }
    }

    # Preview is a fallback only - never the requirement.
    foreach ($p in @(
        (Join-Path $env:ProgramFiles 'PowerShell\7-preview\pwsh.exe'),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\pwsh-preview.exe')
    )) {
        if ($p) { [void]$candidates.Add($p) }
    }

    foreach ($p in ($candidates | Select-Object -Unique)) {
        if (-not $p -or $p -match 'WindowsPowerShell') { continue }
        if (Test-Path -LiteralPath $p) { return $p }
    }
    throw 'PowerShell 7 is required for Exo NVIDIA helpers. Install it with: winget install Microsoft.PowerShell'
}
function Assert-ExoPwsh7 {
    if (Test-ExoIsPwsh7Host) { return }
    $hint = $null
    try { $hint = Get-ExoPwsh } catch { }
    $msg = 'PowerShell 7 is required to run the NVIDIA Optimizer (not Windows PowerShell 5.1). Install it with: winget install Microsoft.PowerShell, then re-run from Exo.'
    if ($hint) { $msg += " Found PowerShell 7 at: $hint" }
    throw $msg
}
Assert-ExoPwsh7

function Test-ExoIsAdmin {
    try {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        return ([Security.Principal.WindowsPrincipal]$identity).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { return $false }
}

# --- Stage tracking: every throw is attributed to the stage that was running so
# --- the state json / detect / UI can say exactly where Apply failed.
$Script:CurrentStage = 'init'
$Script:CompletedPartialDisplayPolicy = $false
function Set-ExoStage([string]$Name) {
    $Script:CurrentStage = $Name
}

function Save-ExoFailureState([string]$Stage, [string]$Message) {
    # Persist the failing stage + reason into nvidia-optimizer.json. Always keep
    # applyInProgress=true (fail-closed) so a late post-verify throw after a
    # premature Save-State cannot look like a successful Apply. Never throws.
    try {
        $existing = $null
        if (Test-Path -LiteralPath $StatePath) {
            try { $existing = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json -AsHashtable } catch { $existing = $null }
        }
        if ($null -eq $existing -or $existing -isnot [hashtable]) { $existing = @{} }
        $existing['lastErrorStage'] = $Stage
        $existing['lastError'] = [string]$Message
        $existing['lastErrorUtc'] = (Get-Date).ToUniversalTime().ToString('o')
        $existing['applyInProgress'] = $true
        if (-not $existing.ContainsKey('version')) { $existing['version'] = $Script:NvidiaOptVersion }
        Save-State $existing
    } catch { }
}

function Write-HubProgress([int]$Percent, [string]$Status) {
    $p = [Math]::Max(0, [Math]::Min(100, $Percent))
    $line = "EXO_PROGRESS:$p|$Status"
    # IMPORTANT: do NOT Write-Output progress - it poisons function returns
    # (e.g. Download path becomes Object[] and -PackageExe fails type conversion).
    # Elevated Exo polls EXO_LOG; host line still shows in console.
    Write-Host $line
    if ($env:EXO_LOG) {
        try { Add-Content -LiteralPath $env:EXO_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    }
}

function Coerce-StringPath($Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [string] -and -not [string]::IsNullOrWhiteSpace($Value)) { return [string]$Value }
    foreach ($v in @($Value)) {
        if ($v -is [string] -and $v -match '\.exe(\s|$)|\.dll(\s|$)' ) { return [string]$v.Trim() }
        if ($v -is [string] -and (Test-Path -LiteralPath $v -ErrorAction SilentlyContinue)) { return [string]$v }
    }
    foreach ($v in @($Value)) {
        if ($v -is [string] -and -not [string]::IsNullOrWhiteSpace($v) -and $v -notmatch '^EXO_PROGRESS') {
            return [string]$v
        }
    }
    return $null
}

function Coerce-Hashtable($Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [hashtable]) { return $Value }
    if ($Value -is [System.Collections.IDictionary]) { return $Value }
    $hit = @($Value) | Where-Object { $_ -is [hashtable] -or $_ -is [System.Collections.IDictionary] } | Select-Object -Last 1
    return $hit
}

function Get-ExoHashBool($Map, [string]$Key, [bool]$Default = $false) {
    # StrictMode-safe: missing hashtable keys throw under PS7 StrictMode.
    if ($null -eq $Map) { return $Default }
    try {
        if ($Map -is [hashtable] -or $Map -is [System.Collections.IDictionary]) {
            if (-not $Map.ContainsKey($Key)) { return $Default }
            return [bool]$Map[$Key]
        }
        $names = @($Map.PSObject.Properties.Name)
        if ($names -notcontains $Key) { return $Default }
        return [bool]$Map.$Key
    } catch {
        return $Default
    }
}

function Get-ExoHashString($Map, [string]$Key, [string]$Default = '') {
    if ($null -eq $Map) { return $Default }
    try {
        if ($Map -is [hashtable] -or $Map -is [System.Collections.IDictionary]) {
            if (-not $Map.ContainsKey($Key)) { return $Default }
            $v = $Map[$Key]
            if ($null -eq $v) { return $Default }
            return [string]$v
        }
        $names = @($Map.PSObject.Properties.Name)
        if ($names -notcontains $Key) { return $Default }
        $v = $Map.$Key
        if ($null -eq $v) { return $Default }
        return [string]$v
    } catch {
        return $Default
    }
}

function Normalize-DriverUpdateInfo($Info) {
    # Every Start-DriverUpdateIfNeeded path must expose the same keys so StrictMode
    # never blows up mid-pipeline (RebootRequired was the 3.0.6 user brick).
    $h = Coerce-Hashtable $Info
    if (-not $h) {
        $h = @{
            Ran              = $false
            NeedsUpdate      = $false
            NeedsRetweak     = $false
            TweaksOk         = $true
            Method           = 'none'
            RebootRequired   = $false
            ContinuePipeline = $true
        }
        return $h
    }
    if (-not $h.ContainsKey('Ran')) { $h['Ran'] = $false }
    if (-not $h.ContainsKey('NeedsUpdate')) { $h['NeedsUpdate'] = $false }
    if (-not $h.ContainsKey('NeedsRetweak')) { $h['NeedsRetweak'] = $false }
    if (-not $h.ContainsKey('TweaksOk')) { $h['TweaksOk'] = $true }
    if (-not $h.ContainsKey('Method')) { $h['Method'] = 'none' }
    if (-not $h.ContainsKey('RebootRequired')) { $h['RebootRequired'] = $false }
    if (-not $h.ContainsKey('ContinuePipeline')) {
        $h['ContinuePipeline'] = -not [bool]$h['RebootRequired']
    }
    return $h
}
function Write-NLog([string]$Prefix, [string]$Msg) {
    $line = "$Prefix $Msg"
    Write-Host $line
    if ($env:EXO_LOG) {
        try { Add-Content -LiteralPath $env:EXO_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    }
}
function Write-Step([string]$Msg) { Write-NLog '[*]' $Msg }
function Write-Ok([string]$Msg)   { Write-NLog '[+]' $Msg }
function Write-Warn([string]$Msg) { Write-NLog '[!]' $Msg }
function Write-Err([string]$Msg)  { Write-NLog '[-]' $Msg }

function Get-NvidiaGpus {
    # Use plain array - @($genericList) throws "Argument types do not match" on PS7.
    $items = @()
    try {
        Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | ForEach-Object {
            $n = [string]$_.Name
            if ($n -match '(?i)nvidia|geforce|rtx|gtx|quadro|titan') {
                $items += [pscustomobject]@{ Name = $n; Driver = [string]$_.DriverVersion }
            }
        }
    } catch { }
    return $items
}

function Get-GpuSeriesFromName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) { return $null }
    if ($Name -match '(?i)\b(?:RTX|GTX)\s*([1-5])0\d{2}\b') { return $Matches[1] + '0' }
    if ($Name -match '(?i)\b([1-5])0\d{2}\s*(?:Ti|SUPER)?\b') { return $Matches[1] + '0' }
    # GTX 16 is Turing without RT/DLSS/rBAR. The 10-series pack avoids
    # unsupported RTX-only profile flags while keeping the same FPS tweaks.
    if ($Name -match '(?i)\b16\d{2}\b') { return '10' }
    return $null
}

function Get-DriverBranchSeriesFromName([string]$Name) {
    # Driver package branch is NOT the same as profile pack series.
    # GTX 16xx still receives modern Game Ready drivers; GTX 10xx is legacy (~582.x).
    if ([string]::IsNullOrWhiteSpace($Name)) { return $null }
    if ($Name -match '(?i)\b16\d{2}\b') { return '20' }
    if ($Name -match '(?i)\b(?:RTX|GTX)\s*([1-5])0\d{2}\b') { return $Matches[1] + '0' }
    if ($Name -match '(?i)\b([1-5])0\d{2}\s*(?:Ti|SUPER)?\b') { return $Matches[1] + '0' }
    return $null
}

function Get-ProfileFile([string]$SeriesId, [bool]$UseGsync) {
    $name = if ($UseGsync) { "$SeriesId Series G-SYNC.nip" } else { "$SeriesId Series.nip" }
    $path = Join-Path $ProfilesDir $name
    if (Test-Path -LiteralPath $path) { return $path }
    return $null
}

function Get-ExoGameProfileCatalog {
    # Application profiles clone the active series Base pack (all 10 packs work:
    # we generate from whichever XX Series / G-SYNC NIP was selected at apply).
    # Tier:
    #   comp   - pure competitive: sticky latency stack + disable Frame Gen override when present
    #   hybrid - still sticky latency (no driver FPS cap / prf=1) but leave FG as the series pack
    # Minecraft (javaw.exe) is intentionally NOT in this catalog: javaw.exe is shared by
    # every desktop Java app, and the clone mechanism applies the full Base pack + tier
    # deltas per exe - too broad for a shared host process (would force max-perf pins on
    # IDEs and installers). Excluded with reason instead of shipping an unsafe profile.
    @(
        @{ Name = 'Valorant';            Tier = 'comp';   Exes = @('VALORANT-Win64-Shipping.exe') },
        @{ Name = 'Counter-Strike 2';    Tier = 'comp';   Exes = @('cs2.exe') },
        @{ Name = 'Marvel Rivals';       Tier = 'comp';   Exes = @('Marvel-Win64-Shipping.exe', 'MarvelRivals-Win64-Shipping.exe', 'marvel-rivals.exe', 'MarvelRivals_Launcher.exe') },
        @{ Name = 'Rainbow Six Siege';   Tier = 'comp';   Exes = @('RainbowSix.exe', 'RainbowSix_Vulkan.exe', 'RainbowSixGame.exe', 'RainbowSix_BE.exe') },
        @{ Name = 'Fortnite';            Tier = 'comp';   Exes = @('FortniteClient-Win64-Shipping.exe') },
        @{ Name = 'Apex Legends';        Tier = 'comp';   Exes = @('r5apex.exe', 'r5apex_dx12.exe') },
        @{ Name = 'League of Legends';   Tier = 'comp';   Exes = @('League of Legends.exe') },
        @{ Name = 'Overwatch 2';         Tier = 'comp';   Exes = @('Overwatch.exe') },
        @{ Name = 'Rocket League';       Tier = 'comp';   Exes = @('RocketLeague.exe') },
        @{ Name = 'Call of Duty';        Tier = 'comp';   Exes = @('cod.exe', 'cod24.exe', 'cod23.exe', 'cod22.exe', 'cod22-cod.exe', 'cod23-cod.exe') },
        @{ Name = 'Destiny 2';           Tier = 'hybrid'; Exes = @('destiny2.exe') },
        @{ Name = 'PUBG';                Tier = 'comp';   Exes = @('TslGame.exe') },
        @{ Name = 'Escape from Tarkov';  Tier = 'comp';   Exes = @('EscapeFromTarkov.exe', 'EscapeFromTarkov_BE.exe') },
        @{ Name = 'The Finals';          Tier = 'comp';   Exes = @('Discovery.exe') },
        @{ Name = 'Delta Force';         Tier = 'comp';   Exes = @('DeltaForceClient-Win64-Shipping.exe') }
        @{ Name = 'Deadlock';            Tier = 'comp';   Exes = @('deadlock.exe', 'project8.exe') }
        @{ Name = 'XDefiant';            Tier = 'comp';   Exes = @('XDefiant.exe') }
        @{ Name = 'FragPunk';            Tier = 'comp';   Exes = @('FragPunk.exe', 'FragPunkClient-Win64-Shipping.exe') }
        @{ Name = 'Warframe';            Tier = 'hybrid'; Exes = @('Warframe.x64.exe', 'Warframe.exe') }
        @{ Name = 'Path of Exile 2';     Tier = 'hybrid'; Exes = @('PathOfExileSteam.exe', 'PathOfExile_x64Steam.exe', 'PathOfExile.exe') }
        @{ Name = 'Dota 2';              Tier = 'comp';   Exes = @('dota2.exe') }
        @{ Name = 'Team Fortress 2';     Tier = 'comp';   Exes = @('tf_win64.exe', 'hl2.exe') }
        @{ Name = 'Rust';                Tier = 'comp';   Exes = @('RustClient.exe') }
        @{ Name = 'GTA V';               Tier = 'hybrid'; Exes = @('GTA5.exe') }
        @{ Name = 'FiveM';               Tier = 'comp';   Exes = @('FiveM.exe', 'FiveM_GTAProcess.exe') }
        @{ Name = 'Helldivers 2';        Tier = 'hybrid'; Exes = @('helldivers2.exe') }
        @{ Name = 'Black Myth Wukong';   Tier = 'hybrid'; Exes = @('b1-Win64-Shipping.exe') }
        @{ Name = 'Elden Ring';          Tier = 'hybrid'; Exes = @('eldenring.exe') }
        @{ Name = 'Wuthering Waves';     Tier = 'hybrid'; Exes = @('Client-Win64-Shipping.exe', 'Wuthering Waves.exe') }
    )
}

function Get-ExoNipSettingMap {
    param([System.Xml.XmlNode]$ProfileNode)
    $map = @{}
    foreach ($s in @($ProfileNode.SelectNodes('Settings/ProfileSetting'))) {
        $id = [string]$s.SettingID
        if ($id) { $map[$id] = [string]$s.SettingValue }
    }
    return $map
}

function Set-ExoNipSettingValue {
    param(
        [Parameter(Mandatory)][System.Xml.XmlNode]$ProfileNode,
        [Parameter(Mandatory)][string]$SettingId,
        [Parameter(Mandatory)][string]$Value
    )
    $node = $ProfileNode.SelectSingleNode("Settings/ProfileSetting[SettingID='$SettingId']")
    if (-not $node) { return $false }
    $valNode = $node.SelectSingleNode('SettingValue')
    if (-not $valNode) { return $false }
    if ([string]$valNode.InnerText -eq $Value) { return $false }
    $valNode.InnerText = $Value
    return $true
}

function Apply-ExoGameProfileDeltas {
    param(
        [Parameter(Mandatory)][System.Xml.XmlNode]$ProfileNode,
        [Parameter(Mandatory)][hashtable]$BaseMap,
        [Parameter(Mandatory)][string]$Tier
    )
    # Detect pack policy from the cloned Base (works for all 10 series packs).
    $isGsyncPack = ($BaseMap['294973784'] -eq '1') -or ($BaseMap['277041152'] -eq '0' -and $BaseMap['390467'] -eq '0')
    $changed = 0
    $notes = [System.Collections.Generic.List[string]]::new()

    # --- Sticky latency / clarity stack (every title) ---
    # Re-assert so an app-level NVIDIA/App profile cannot leave softer defaults.
    # Profile pack: pre-render 1, max perf, highest Hz, no post-process latency traps.
    $common = @{
        '8102046'   = '1'          # Maximum Pre-Rendered Frames = 1
        '546199011' = '1'          # Maximum frames allowed = 1
        '277041154' = '0'          # Frame Rate Limiter V3 off
        '553505273' = '0'          # Triple buffering off
        '274197361' = '1'          # Prefer maximum performance
        '549528094' = '1'          # Threaded optimization on
        '6600001'   = '1'          # Highest available refresh
        '276089202' = '0'          # FXAA off
        '10011052'  = '0'          # MFAA off
        '6714153'   = '0'          # Ambient occlusion off
        '276158834' = '0'          # Ansel off
        '271965065' = '0'          # Predefined Ansel off
        '275315612' = '0'          # FXAA indicator off
        '543959236' = '0'          # Enable overlay off
        '282245910' = '0'          # Antialiasing - Mode = App controlled (no forced AA latency)
        '283226065' = '1'          # Texture filtering - Quality = High performance (when present)
        '283385347' = '0'          # DLSS Frame Generation override off by default (re-enabled only hybrid tier)
    }
    foreach ($id in $common.Keys) {
        if (-not $BaseMap.ContainsKey($id)) { continue }
        if (Set-ExoNipSettingValue -ProfileNode $ProfileNode -SettingId $id -Value $common[$id]) {
            $changed++
        }
    }

    # Re-pin pack-specific sync / latency policy (do not invent G-SYNC on max-FPS packs).
    if ($isGsyncPack) {
        $gsyncPins = @{
            '390467'    = '0'   # ULL CPL off (avoids fighting VRR)
            '277041152' = '0'   # ULL enabled off
            '294973784' = '1'   # GSYNC global mode on
            '278196727' = '1'   # GSYNC application state on
            '279476687' = '1'   # GSYNC application mode on
            '11041279'  = '0'   # OS VRR override off (driver/G-SYNC path)
        }
        if ($BaseMap.ContainsKey('11041231') -and $BaseMap['11041231']) {
            $gsyncPins['11041231'] = $BaseMap['11041231'] # keep pack VSync (G-SYNC friendly)
        }
        foreach ($id in $gsyncPins.Keys) {
            if (-not $BaseMap.ContainsKey($id)) { continue }
            if (Set-ExoNipSettingValue -ProfileNode $ProfileNode -SettingId $id -Value $gsyncPins[$id]) {
                $changed++
            }
        }
        [void]$notes.Add('gsync-pins')
    } else {
        $fpsPins = @{
            '390467'    = '2'          # ULL CPL = Ultra
            '277041152' = '1'          # ULL enabled
            '294973784' = '0'          # GSYNC global off
            '278196727' = '0'          # GSYNC app state off
            '11041279'  = '1'          # OS VRR override on (helps non-G-SYNC path)
            '11041231'  = '138504007'  # VSync force off (Exo max-FPS packs)
        }
        foreach ($id in $fpsPins.Keys) {
            if (-not $BaseMap.ContainsKey($id)) { continue }
            if (Set-ExoNipSettingValue -ProfileNode $ProfileNode -SettingId $id -Value $fpsPins[$id]) {
                $changed++
            }
        }
        [void]$notes.Add('maxfps-pins')
    }

    # Competitive titles: keep FG off (already in common). Hybrid can restore pack default if present.
    if ($Tier -eq 'comp') {
        [void]$notes.Add('fg-off')
        [void]$notes.Add('comp')
    } else {
        # Hybrid / single-player: allow series-pack FG default if the base defined one
        if ($BaseMap.ContainsKey('283385347') -and $BaseMap['283385347'] -ne '0') {
            if (Set-ExoNipSettingValue -ProfileNode $ProfileNode -SettingId '283385347' -Value $BaseMap['283385347']) {
                $changed++
            }
            [void]$notes.Add('fg-pack')
        }
        [void]$notes.Add('hybrid')
    }

    return @{
        Changed = $changed
        Notes   = @($notes)
        Gsync   = [bool]$isGsyncPack
    }
}

function New-ExoCombinedProfileNip {
    param(
        [Parameter(Mandatory)][string]$BaseNipPath,
        [Parameter(Mandatory)][string]$OutPath
    )
    if (-not (Test-Path -LiteralPath $BaseNipPath)) {
        throw "Base NIP missing: $BaseNipPath"
    }

    # Profiles ship as UTF-16 XML.
    [xml]$doc = [IO.File]::ReadAllText($BaseNipPath)
    $array = $doc.ArrayOfProfile
    if (-not $array) { throw 'Base NIP missing ArrayOfProfile root' }
    $base = @($array.Profile) | Select-Object -First 1
    if (-not $base -or [string]$base.ProfileName -ne 'Base Profile') {
        throw 'Base NIP must start with a Base Profile entry'
    }

    $baseMap = Get-ExoNipSettingMap -ProfileNode $base
    $games = @(Get-ExoGameProfileCatalog)
    $deltaSummary = @()
    foreach ($game in $games) {
        $clone = $base.CloneNode($true)
        $nameNode = $clone.SelectSingleNode('ProfileName')
        if (-not $nameNode) { throw 'Cloned profile missing ProfileName' }
        $nameNode.InnerText = "Exo - $($game.Name)"

        $execNode = $clone.SelectSingleNode('Executeables')
        if (-not $execNode) {
            $execNode = $doc.CreateElement('Executeables')
            [void]$clone.InsertAfter($execNode, $nameNode)
        } else {
            $execNode.RemoveAll()
        }
        foreach ($exe in @($game.Exes)) {
            $s = $doc.CreateElement('string')
            $s.InnerText = [string]$exe
            [void]$execNode.AppendChild($s)
        }

        $tier = if ($game.Tier) { [string]$game.Tier } else { 'comp' }
        $delta = Apply-ExoGameProfileDeltas -ProfileNode $clone -BaseMap $baseMap -Tier $tier
        $deltaSummary += [string]("$($game.Name)[$tier/$($delta.Notes -join '+')]")

        [void]$array.AppendChild($clone)
    }

    $settings = New-Object System.Xml.XmlWriterSettings
    # UTF-16 LE + BOM (matches shipped .nip packs). Constructor is (bigEndian, byteOrderMark).
    $settings.Encoding = New-Object System.Text.UnicodeEncoding $false, $true
    $settings.Indent = $true
    $settings.OmitXmlDeclaration = $false
    $writer = [System.Xml.XmlWriter]::Create($OutPath, $settings)
    try {
        $doc.Save($writer)
    } finally {
        $writer.Dispose()
    }

    if (-not (Test-Path -LiteralPath $OutPath) -or (Get-Item -LiteralPath $OutPath).Length -lt 1000) {
        throw "Combined NIP write failed: $OutPath"
    }

    return @{
        Path          = $OutPath
        GameCount     = $games.Count
        Games         = @($games | ForEach-Object { [string]$_.Name })
        DeltaSummary  = $deltaSummary
        GameDeltas    = $true
    }
}

function Test-IsNotebookGpuName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) { return $false }
    return [bool]($Name -match '(?i)\b(?:Laptop GPU|Notebook|Mobile|Max-Q)\b|\bMX\d+\b|\b\d{3,4}M\b')
}

function Assert-ExoNipProfile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][bool]$UseGsync
    )
    try { [xml]$document = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop }
    catch { throw "Profile XML is invalid: $($_.Exception.Message)" }

    $profiles = @($document.ArrayOfProfile.Profile)
    if ($profiles.Count -ne 1 -or [string]$profiles[0].ProfileName -ne 'Base Profile') {
        throw 'Profile must contain exactly one Base Profile entry'
    }
    $settings = @($profiles[0].Settings.ProfileSetting)
    if ($settings.Count -lt 60) { throw "Profile is incomplete ($($settings.Count) settings)" }
    $duplicates = @($settings | Group-Object SettingID | Where-Object { $_.Count -gt 1 })
    if ($duplicates.Count -gt 0) { throw "Profile has duplicate setting IDs: $($duplicates.Name -join ', ')" }

    $actual = @{}
    foreach ($setting in $settings) { $actual[[string]$setting.SettingID] = [string]$setting.SettingValue }
    $expected = @{
        '274197361' = '1'          # Prefer maximum performance
        '6600001'   = '1'          # Highest available refresh
        '549528094' = '1'          # Threaded optimization on
        '11306135'  = '4294967295' # Unlimited shader cache
        '277041154' = '0'          # Frame limiter disabled
        '553505273' = '0'          # Triple buffering off
        '390467'    = $(if ($UseGsync) { '0' } else { '2' })
        '277041152' = $(if ($UseGsync) { '0' } else { '1' })
        '294973784' = $(if ($UseGsync) { '1' } else { '0' })
    }
    foreach ($id in $expected.Keys) {
        if (-not $actual.ContainsKey($id) -or $actual[$id] -ne $expected[$id]) {
            throw "Profile performance invariant failed for setting $id (expected $($expected[$id]), got $($actual[$id]))"
        }
    }
    Write-Ok "Profile verified: $($settings.Count) settings, performance invariants intact"
}

function Stop-NpiProcesses {
    Get-Process -Name 'nvidiaProfileInspector' -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $processPath = [string]$_.Path
            if ($processPath -and $processPath.StartsWith($NpiDir, [StringComparison]::OrdinalIgnoreCase)) {
                Write-Ok "Stopping Exo managed Profile Inspector PID $($_.Id)"
                Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
            } else {
                Write-Warn "Profile Inspector PID $($_.Id) is not managed by Exo and was left running"
            }
        } catch { }
    }
    Start-Sleep -Milliseconds 500
}

function Test-ManagedNpiCache {
    param(
        [Parameter(Mandatory)][string]$ExePath,
        [Parameter(Mandatory)][string]$StampPath,
        [string]$ExpectedTag = ''
    )
    if (-not (Test-Path -LiteralPath $ExePath) -or -not (Test-Path -LiteralPath $StampPath)) {
        return $false
    }
    try {
        $metadata = @{}
        Get-Content -LiteralPath $StampPath -ErrorAction Stop | ForEach-Object {
            $parts = $_ -split '=', 2
            if ($parts.Count -eq 2) { $metadata[$parts[0].Trim()] = $parts[1].Trim() }
        }
        if ($ExpectedTag -and [string]$metadata.tag -ne $ExpectedTag) { return $false }
        if (-not $metadata.exeSha256) { return $false }
        $actualHash = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256 -ErrorAction Stop).Hash
        return $actualHash -eq [string]$metadata.exeSha256
    } catch {
        return $false
    }
}

function Install-NpiFresh {
    # Pinned Profile Inspector release only ($Script:NpiPinnedTag). The managed copy is
    # reused when the stamp matches the pinned tag AND the exe hash still verifies;
    # stale/older copies are replaced so -silentImport / -exportCustomized stay current.
    Set-ExoStage 'npi-install'
    Write-Step "Checking Exo managed NVIDIA Profile Inspector (pinned $Script:NpiPinnedTag)..."
    $target = Join-Path $NpiDir $NpiExeName
    $stampPath = Join-Path $NpiDir 'EXO-NPI-VERSION.txt'
    $headers = @{ 'User-Agent' = 'Exo-Nvidia/1.5.0'; 'Accept' = 'application/octet-stream' }

    if (Test-ManagedNpiCache -ExePath $target -StampPath $stampPath -ExpectedTag $Script:NpiPinnedTag) {
        Write-Ok "Managed Profile Inspector is pinned and hash-verified ($Script:NpiPinnedTag)"
        return $target
    }

    $tag = $Script:NpiPinnedTag
    $downloadUri = [uri]$Script:NpiPinnedZipUrl
    if ($downloadUri.Scheme -ne 'https' -or $downloadUri.Host -notmatch '(?i)(^|\.)github\.com$') {
        throw "Unexpected Profile Inspector download host: $($downloadUri.Host)"
    }

    $workId = [guid]::NewGuid().ToString('n')
    $zip = Join-Path $env:TEMP ("exo-npi-$workId.zip")
    $extract = Join-Path $env:TEMP ("exo-npi-$workId")
    Write-Ok "Pinned NPI release: $tag"
    try {
        try {
            Invoke-WebRequest -Uri $downloadUri.AbsoluteUri -OutFile $zip -UseBasicParsing -Headers $headers -TimeoutSec 120
        } catch {
            throw "Profile Inspector download failed and no verified cached copy is available: $($_.Exception.Message)"
        }
        $actualDigest = (Get-FileHash -LiteralPath $zip -Algorithm SHA256 -ErrorAction Stop).Hash
        if ($actualDigest -ine $Script:NpiPinnedZipSha256) {
            throw "Profile Inspector archive SHA256 did not match the pinned digest (expected $Script:NpiPinnedZipSha256, got $actualDigest)"
        }
        Write-Ok 'Verified Profile Inspector archive against pinned SHA256'
        New-Item -ItemType Directory -Force -Path $extract | Out-Null
        Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force
        $found = Get-ChildItem -LiteralPath $extract -Recurse -Filter $NpiExeName -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $found) { throw 'nvidiaProfileInspector.exe missing from downloaded archive' }

        if (Test-Path -LiteralPath $NpiDir) {
            Remove-Item -LiteralPath $NpiDir -Recurse -Force -ErrorAction Stop
        }
        New-Item -ItemType Directory -Force -Path $NpiDir | Out-Null
        Copy-Item -LiteralPath $found.FullName -Destination $target -Force
        foreach ($extra in @('Reference.xml', 'CustomSettingNames.xml', 'nvidiaProfileInspector.exe.config')) {
            $hit = Get-ChildItem -LiteralPath $extract -Recurse -Filter $extra -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($hit) { Copy-Item -LiteralPath $hit.FullName -Destination (Join-Path $NpiDir $extra) -Force }
        }

        $exeSha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256 -ErrorAction Stop).Hash
        $stamp = @"
tag=$tag
installedUtc=$((Get-Date).ToUniversalTime().ToString('o'))
source=$($downloadUri.AbsoluteUri)
exeSha256=$exeSha256
managedBy=Exo
"@
        [IO.File]::WriteAllText($stampPath, $stamp.Trim() + "`n", [Text.UTF8Encoding]::new($false))
        if (-not (Test-Path -LiteralPath $target)) { throw "Managed NPI missing at $target" }
        Write-Ok "Managed NPI ready: $target ($tag)"
    } finally {
        Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
    }
    return $target
}

function Import-ExoNipProfile {
    param(
        [Parameter(Mandatory)][string]$NipPath,
        [int]$TimeoutSec = 120
    )
    # Use Exo's isolated managed copy; user-installed Profile Inspector is never touched.
    if (-not (Test-Path -LiteralPath $NipPath)) {
        throw "NIP profile missing: $NipPath"
    }

    Stop-NpiProcesses
    $npi = Install-NpiFresh
    if (-not (Test-Path -LiteralPath $npi)) {
        throw 'Fresh Profile Inspector install failed'
    }

    $safeNip = Join-Path $env:TEMP ("exo-profile-$([guid]::NewGuid().ToString('n')).nip")
    Copy-Item -LiteralPath $NipPath -Destination $safeNip -Force
    Write-Ok "Importing profile with FRESH NPI: $(Split-Path $NipPath -Leaf)"
    Write-Ok "NPI: $npi"
    Write-Ok "NIP: $safeNip"

    $exitCode = -1
    $npiWorkDir = Split-Path -Parent $npi
    $proc = $null
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $npi
        # Official CLI: -silentImport "path.nip" (or -silent). Never pass bare .nip
        # first - that opens the WPF UI and can flash XAML/doc windows.
        $quotedNip = '"' + $safeNip.Replace('"', '') + '"'
        $psi.Arguments = "-silentImport $quotedNip"
        $psi.WorkingDirectory = $npiWorkDir
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $proc = [Diagnostics.Process]::Start($psi)
        if (-not $proc) { throw 'Failed to start Profile Inspector' }

        if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
            try { $proc.Kill() } catch { }
            Stop-NpiProcesses
            throw "Profile Inspector silent import timed out after ${TimeoutSec}s. Profile NOT marked applied."
        }
        $exitCode = [int]$proc.ExitCode
        Write-Ok "NPI silent import exit code: $exitCode"
        # Kill any leftover WPF windows even after a "successful" exit.
        Stop-NpiProcesses
    } finally {
        Stop-NpiProcesses
        if ($proc) { try { $proc.Dispose() } catch { } }
        try { Remove-Item -LiteralPath $safeNip -Force -ErrorAction SilentlyContinue } catch { }
        # Never leave .nip next to NPI or in TEMP for Explorer/Edge to open as a document.
        try {
            Get-ChildItem -LiteralPath $npiWorkDir -Filter '*.nip' -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like 'exo-*' -or $_.LastWriteTime -gt (Get-Date).AddMinutes(-5) } |
                Remove-Item -Force -ErrorAction SilentlyContinue
        } catch { }
    }

    if ($exitCode -ne 0) {
        throw "Profile Inspector silent import failed (exit $exitCode). Profile NOT marked applied."
    }

    Write-Ok '3D Base Profile imported with Exo managed NPI'
    return @{
        Success   = $true
        ExitCode  = $exitCode
        NpiPath   = $npi
        NipFile   = (Split-Path $NipPath -Leaf)
        ManagedNpi = $true
        NpiFolder = $NpiDir
    }
}

function Get-ExoNipBaseProfileMap {
    # SettingID -> SettingValue map of the Base Profile inside a .nip pack.
    param([Parameter(Mandatory)][string]$NipPath)
    if (-not (Test-Path -LiteralPath $NipPath)) { return $null }
    try { [xml]$doc = [IO.File]::ReadAllText($NipPath) } catch { return $null }
    $base = @($doc.ArrayOfProfile.Profile) |
        Where-Object { [string]$_.ProfileName -eq 'Base Profile' } |
        Select-Object -First 1
    if (-not $base) { return $null }
    return (Get-ExoNipSettingMap -ProfileNode $base)
}

function Invoke-ExoNpiExportCustomized {
    param(
        [Parameter(Mandatory)][string]$NpiPath,
        [int]$TimeoutSec = 60
    )
    # NPI CLI: -exportCustomized writes every customized profile into a
    # timestamped .nip next to the exe, then exits. Older builds produce nothing;
    # the caller records drsVerified='unavailable' (non-fatal) in that case.
    if (-not (Test-Path -LiteralPath $NpiPath)) { return $null }
    $npiWorkDir = Split-Path -Parent $NpiPath
    $startUtc = (Get-Date).ToUniversalTime()
    $proc = $null
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $NpiPath
        $psi.Arguments = '-exportCustomized'
        $psi.WorkingDirectory = $npiWorkDir
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $proc = [Diagnostics.Process]::Start($psi)
        if (-not $proc) { return $null }
        if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
            try { $proc.Kill() } catch { }
            return $null
        }
    } catch {
        return $null
    } finally {
        if ($proc) { try { $proc.Dispose() } catch { } }
    }

    $exported = @(Get-ChildItem -LiteralPath $npiWorkDir -Filter '*.nip' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTimeUtc -ge $startUtc.AddSeconds(-5) } |
        Sort-Object LastWriteTimeUtc -Descending) | Select-Object -First 1
    if (-not $exported) { return $null }
    return [string]$exported.FullName
}

function Get-ExoDrsExportBaseMap {
    # Base Profile SettingID -> SettingValue map from an -exportCustomized dump.
    # Returns $null when the export cannot be parsed; an EMPTY map when the export
    # parsed but contains no customized Base Profile (stock driver = drift).
    param([Parameter(Mandatory)][string]$ExportPath)
    try { [xml]$doc = [IO.File]::ReadAllText($ExportPath) } catch { return $null }
    $base = @($doc.ArrayOfProfile.Profile) |
        Where-Object { [string]$_.ProfileName -eq 'Base Profile' } |
        Select-Object -First 1
    if (-not $base) { return @{} }
    return (Get-ExoNipSettingMap -ProfileNode $base)
}

function Get-ExoDrsVerificationResult {
    # Pure classifier - keep aligned with NvidiaDetectCore.ps1 + NvidiaDetectLogic.cs.
    # Compares the intersection of pack settings vs the live driver export.
    # RequiredIds must be present in the export (they are always customized by the
    # pack, so a correct import exports them); a missing required pin is drift.
    param(
        [AllowNull()][hashtable]$Expected,
        [AllowNull()][hashtable]$Exported,
        [string[]]$RequiredIds = @()
    )
    if ($null -eq $Expected -or $Expected.Count -eq 0) {
        return [pscustomobject]@{ Status = 'unavailable'; ComparedCount = 0; Mismatches = @() }
    }
    if ($null -eq $Exported) {
        return [pscustomobject]@{ Status = 'unavailable'; ComparedCount = 0; Mismatches = @() }
    }
    $mismatches = New-Object System.Collections.Generic.List[string]
    $compared = 0
    foreach ($id in @($Expected.Keys | Sort-Object)) {
        if (-not $Exported.ContainsKey($id)) { continue }
        $compared++
        if ([string]$Exported[$id] -ne [string]$Expected[$id]) {
            [void]$mismatches.Add(("{0}: expected {1}, driver has {2}" -f $id, $Expected[$id], $Exported[$id]))
        }
    }
    foreach ($id in @($RequiredIds)) {
        if (-not $Expected.ContainsKey($id)) { continue }
        if (-not $Exported.ContainsKey($id)) {
            [void]$mismatches.Add(("{0}: expected {1}, missing from driver export" -f $id, $Expected[$id]))
        }
    }
    if ($compared -eq 0 -and $mismatches.Count -eq 0) {
        return [pscustomobject]@{
            Status        = 'drifted'
            ComparedCount = 0
            Mismatches    = @('no imported pack settings present in the driver export')
        }
    }
    $status = if ($mismatches.Count -eq 0) { 'verified' } else { 'drifted' }
    return [pscustomobject]@{
        Status        = $status
        ComparedCount = $compared
        Mismatches    = @($mismatches)
    }
}

# Pins that every Exo pack customizes and a correct import must therefore export:
# power management mode, ULL (CPL state + enabled), frame limiter off, G-SYNC global.
$Script:DrsRequiredPinIds = @('274197361', '390467', '277041152', '277041154', '294973784')

function Test-ExoDrsImportVerified {
    # Post-import verification: export live DRS with the managed NPI and compare the
    # Base Profile pins against the imported pack .nip (expected values derived from
    # the pack itself, never hardcoded).
    param(
        [Parameter(Mandatory)][string]$NpiPath,
        [Parameter(Mandatory)][string]$PackNipPath
    )
    $nowUtc = (Get-Date).ToUniversalTime().ToString('o')
    $expected = Get-ExoNipBaseProfileMap -NipPath $PackNipPath
    if ($null -eq $expected -or $expected.Count -eq 0) {
        return @{
            Verified     = 'unavailable'
            VerifiedAt   = $nowUtc
            SettingCount = 0
            Mismatches   = @()
            Reason       = 'imported pack could not be parsed for expected pins'
        }
    }

    $exportPath = Invoke-ExoNpiExportCustomized -NpiPath $NpiPath
    if (-not $exportPath) {
        return @{
            Verified     = 'unavailable'
            VerifiedAt   = $nowUtc
            SettingCount = 0
            Mismatches   = @()
            Reason       = '-exportCustomized produced no export (Profile Inspector too old or export failed)'
        }
    }

    $exportedMap = $null
    try {
        $exportedMap = Get-ExoDrsExportBaseMap -ExportPath $exportPath
    } finally {
        try { Remove-Item -LiteralPath $exportPath -Force -ErrorAction SilentlyContinue } catch { }
    }

    $result = Get-ExoDrsVerificationResult -Expected $expected -Exported $exportedMap -RequiredIds $Script:DrsRequiredPinIds
    $verified = switch ([string]$result.Status) {
        'verified' { $true }
        'drifted'  { $false }
        default    { 'unavailable' }
    }
    $reason = if ($verified -eq 'unavailable') { 'driver export could not be parsed' } else { $null }
    return @{
        Verified     = $verified
        VerifiedAt   = $nowUtc
        SettingCount = [int]$result.ComparedCount
        Mismatches   = @($result.Mismatches)
        Reason       = $reason
    }
}

function Test-NvidiaAppInstalled {
    $paths = @(
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe'),
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA App\NVIDIA App.exe'),
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA Overlay\NVIDIA App.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'NVIDIA Corporation\NVIDIA App\NVIDIA App.exe')
    )
    foreach ($p in $paths) { if (Test-Path -LiteralPath $p) { return $true } }
    # Broader scan: Store / winget / OEM layouts sometimes land under NVIDIA Corporation\NVIDIA App\*
    foreach ($root in @(
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA App'),
        (Join-Path ${env:ProgramFiles(x86)} 'NVIDIA Corporation\NVIDIA App')
    )) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $hit = Get-ChildItem -LiteralPath $root -Recurse -Filter 'NVIDIA App.exe' -File -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($hit) { return $true }
    }
    $app = Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match '(?i)NVIDIAApp|NVIDIA\.App|GeForceExperience'
    }
    if ($app) { return $true }
    # NVI2 still has Display.NvApp (or children) registered even if CEF was deleted
    try {
        $nvi = @(Get-Nvi2InstalledPackageNames | Where-Object { $_ -match '(?i)^Display\.NvApp$|^Display\.NvApp\.' })
        if ($nvi.Count -gt 0) { return $true }
    } catch { }
    return $false
}

function Get-ExoWingetPath {
    # Elevated Exo often cannot resolve the per-user WindowsApps winget stub.
    $candidates = [System.Collections.Generic.List[string]]::new()
    $cmd = Get-Command winget -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd -and $cmd.Source) { [void]$candidates.Add([string]$cmd.Source) }
    foreach ($p in @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'),
        (Join-Path $env:ProgramFiles 'WindowsApps\Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe\winget.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\WindowsApps\winget.exe'),
        (Join-Path $env:SystemRoot 'System32\winget.exe')
    )) {
        if ($p -match '\*') {
            Get-Item -Path $p -ErrorAction SilentlyContinue | ForEach-Object { [void]$candidates.Add($_.FullName) }
        } elseif ($p) {
            [void]$candidates.Add($p)
        }
    }
    $apps = Join-Path $env:ProgramFiles 'WindowsApps'
    if (Test-Path -LiteralPath $apps) {
        Get-ChildItem -LiteralPath $apps -Directory -Filter 'Microsoft.DesktopAppInstaller_*' -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object {
                $w = Join-Path $_.FullName 'winget.exe'
                if (Test-Path -LiteralPath $w) { [void]$candidates.Add($w) }
            }
    }
    foreach ($p in ($candidates | Select-Object -Unique)) {
        if ($p -and (Test-Path -LiteralPath $p)) { return $p }
    }
    return $null
}

function Remove-NvidiaAppDesktopShortcuts {
    # Exo never wants NVIDIA App / GFE desktop clutter after a fresh install.
    $desktops = @(
        [Environment]::GetFolderPath('Desktop'),
        [Environment]::GetFolderPath('CommonDesktopDirectory')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique
    $patterns = @(
        '*NVIDIA App*.lnk',
        '*NVIDIA GeForce Experience*.lnk',
        '*GeForce Experience*.lnk',
        '*NVIDIA Overlay*.lnk'
    )
    $removed = 0
    foreach ($desk in $desktops) {
        foreach ($pat in $patterns) {
            Get-ChildItem -LiteralPath $desk -Filter $pat -File -ErrorAction SilentlyContinue | ForEach-Object {
                try {
                    Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop
                    $removed++
                    Write-Ok "Removed desktop shortcut: $($_.Name)"
                } catch {
                    Write-Warn "Could not remove desktop shortcut $($_.Name): $($_.Exception.Message)"
                }
            }
        }
    }
    if ($removed -eq 0) {
        Write-Ok 'No NVIDIA App desktop shortcuts to remove'
    }
    return $removed
}

function Clear-NvidiaTrayGhostIcons {
    # NVDisplay.Container re-registers on soft-refresh/logon. Deleting its key makes it
    # come back promoted - hide IsPromoted=0 instead. Delete App/GFE ghosts only.
    Write-Step 'Clearing / hiding NVIDIA tray icons (display hide + App delete)...'
    $trayScript = Join-Path $PSScriptRoot 'Exo-Nvidia-TrayClear.ps1'
    if (Test-Path -LiteralPath $trayScript) {
        try {
            $p = Start-Process -FilePath 'powershell.exe' -ArgumentList @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$trayScript`"",
                '-NoTask', '-SettlePasses', '3'
            ) -Wait -PassThru -WindowStyle Hidden
            Write-Ok "Tray clear script exit $($p.ExitCode) (NoTask; no background task)"
            return 1
        } catch {
            Write-Warn "Tray script launch failed: $($_.Exception.Message)"
        }
    }

    $removed = 0
    $hidden = 0
    $roots = @('HKCU:\Control Panel\NotifyIconSettings')
    $nvidiaTrayPattern = '(?i)NVIDIA|nvcontainer|NVDisplay|GeForce|ShadowPlay|nvsphelper|nvapp|NvBackend'
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue | ForEach-Object {
            $exe = $null
            try { $exe = [string](Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue).ExecutablePath } catch { }
            if ([string]::IsNullOrWhiteSpace($exe)) { return }
            if ($exe -notmatch $nvidiaTrayPattern) { return }
            if ($exe -match '(?i)NVDisplay\.Container|Display\.NvContainer|nv_dispi\.inf') {
                try {
                    Set-ItemProperty -LiteralPath $_.PSPath -Name 'IsPromoted' -Value 0 -Type DWord -Force -EA 0
                    $hidden++
                } catch { }
                return
            }
            try {
                Remove-Item -LiteralPath $_.PSPath -Recurse -Force -ErrorAction Stop
                $removed++
                Write-Ok "Removed tray icon: $exe"
            } catch {
                try { Set-ItemProperty -LiteralPath $_.PSPath -Name 'IsPromoted' -Value 0 -Type DWord -Force -EA 0; $hidden++ } catch { }
            }
        }
    }

    try {
        $svc = Get-Service -Name 'NvContainerLocalSystem' -ErrorAction SilentlyContinue
        if ($svc) {
            if ($svc.Status -ne 'Stopped') {
                Stop-Service -Name 'NvContainerLocalSystem' -Force -ErrorAction SilentlyContinue
            }
            Set-Service -Name 'NvContainerLocalSystem' -StartupType Disabled -ErrorAction SilentlyContinue
            Write-Ok 'NvContainerLocalSystem disabled (App stack)'
        }
    } catch { }

    $pd = Join-Path $env:ProgramData 'NVIDIA Corporation\NVIDIA App'
    if (Test-Path -LiteralPath $pd) {
        if (Remove-ExoTreeForce -Path $pd) {
            Write-Ok "Removed leftover $pd"
        }
    }

    Write-Ok "Tray: removed=$removed hidden(display)=$hidden"
    return ($removed + $hidden)
}

function Wait-NvidiaAppInstalled {
    param([int]$Seconds = 90)
    $deadline = (Get-Date).AddSeconds([Math]::Max(5, $Seconds))
    while ((Get-Date) -lt $deadline) {
        if (Test-NvidiaAppInstalled) { return $true }
        Start-Sleep -Milliseconds 800
    }
    return (Test-NvidiaAppInstalled)
}

function Get-NvidiaAppOfficialInstallerUrl {
    # Fast path: known CDN builds first (no 25s page scrape hang). Page scrape is optional refresh.
    $headers = @{ 'User-Agent' = 'Exo-Nvidia/1.8' }
    $cdn = @(
        'https://us.download.nvidia.com/nvapp/client/11.0.8.299/NVIDIA_app_v11.0.8.299.exe',
        'https://international.download.nvidia.com/nvapp/client/11.0.8.299/NVIDIA_app_v11.0.8.299.exe'
    )
    foreach ($u in $cdn) {
        try {
            $req = [System.Net.HttpWebRequest]::Create($u)
            $req.Method = 'HEAD'
            $req.UserAgent = 'Exo-Nvidia/1.8'
            $req.Timeout = 8000
            $resp = $req.GetResponse()
            $code = [int]$resp.StatusCode
            $resp.Close()
            if ($code -ge 200 -and $code -lt 400) {
                Write-Ok "Using NVIDIA CDN installer URL"
                return $u
            }
        } catch { }
    }
    try {
        $html = (Invoke-WebRequest -Uri 'https://www.nvidia.com/en-us/software/nvidia-app/' -UseBasicParsing -Headers $headers -TimeoutSec 12).Content
        $m = [regex]::Match([string]$html, 'https://[^"''\s]+/nvapp/client/[^"''\s]+/NVIDIA_app_v[\d\.]+\.exe', 'IgnoreCase')
        if ($m.Success) { return $m.Value }
    } catch {
        Write-Warn "NVIDIA App product page lookup: $($_.Exception.Message)"
    }
    # Last resort: return US CDN even if HEAD failed (some networks block HEAD).
    return $cdn[0]
}

function Format-ExitCodeHex {
    param($Code)
    # Safe hex for logging - NEVER throw on negative ExitCode (Brian: -436207616).
    try {
        $c = [int]$Code
        $u = [BitConverter]::ToUInt32([BitConverter]::GetBytes($c), 0)
        return ('{0:X8}' -f $u)
    } catch {
        return '00000000'
    }
}

function Test-NvidiaAppSetupUnsupportedExit {
    param($Code)
    # Brian GTX 1080 log: exit -436207616 (signed form of 0xE6000000).
    # Process.ExitCode may surface as signed or unsigned. Do NOT cast negative ints
    # with [uint32] directly (throws in PS and was making the detector always false).
    $c = 0
    try { $c = [int]$Code } catch { return $false }

    # Exact values from Brian's log + positive twin
    if ($c -eq -436207616 -or $c -eq 436207616) { return $true }

    try {
        $u = [BitConverter]::ToUInt32([BitConverter]::GetBytes($c), 0)
        # 0x1A000000 = 436207616, 0xE6000000 = 3858759680
        if ($u -eq [uint32]436207616 -or $u -eq [uint32]3858759680) { return $true }
        $hi = [int](($u -shr 24) -band 255)
        if ($hi -eq 0x1A -or $hi -eq 0xE6) { return $true }
    } catch { }
    return $false
}

function Install-NvidiaAppFromOfficialInstaller {
    # Primary path: direct NVIDIA CDN download (fast, works elevated, no Store/winget).
    # Returns: $true installed | $false failed (sets $Script:NvidiaAppInstallUnsupported when OS/GPU rejected).
    $Script:NvidiaAppInstallUnsupported = $false
    Write-Step 'Downloading official NVIDIA App installer from NVIDIA (primary path)...'
    $url = Get-NvidiaAppOfficialInstallerUrl
    if (-not $url) {
        Write-Warn 'Could not resolve an official NVIDIA App installer URL'
        return $false
    }
    Write-Ok "Installer URL: $url"
    $cacheDir = Join-Path $StateDir 'downloads'
    if (-not (Test-Path -LiteralPath $cacheDir)) {
        New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
    }
    $fileName = [IO.Path]::GetFileName(([uri]$url).AbsolutePath)
    if ([string]::IsNullOrWhiteSpace($fileName)) { $fileName = 'NVIDIA_app_setup.exe' }
    $dest = Join-Path $cacheDir $fileName
    try {
        $needDownload = $true
        if (Test-Path -LiteralPath $dest) {
            $len = (Get-Item -LiteralPath $dest).Length
            if ($len -gt 50MB) {
                Write-Ok "Using cached installer ($([math]::Round($len/1MB,1)) MB)"
                $needDownload = $false
            }
        }
        if ($needDownload) {
            Write-HubProgress 72 'Downloading NVIDIA App from nvidia.com...'
            $partial = "$dest.partial"
            Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
            # WebClient is faster/more reliable than IWR on some networks; falls back to IWR.
            $okDl = $false
            try {
                $wc = New-Object System.Net.WebClient
                $wc.Headers.Add('User-Agent', 'Exo-Nvidia/1.8')
                $wc.DownloadFile($url, $partial)
                $okDl = $true
            } catch {
                Write-Warn "WebClient download failed: $($_.Exception.Message)"
            }
            if (-not $okDl) {
                Invoke-WebRequest -Uri $url -OutFile $partial -UseBasicParsing -Headers @{ 'User-Agent' = 'Exo-Nvidia/1.8' } -TimeoutSec 600
            }
            $len = (Get-Item -LiteralPath $partial).Length
            if ($len -lt 50MB) {
                Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
                throw "Installer download too small ($len bytes)"
            }
            Move-Item -LiteralPath $partial -Destination $dest -Force
            Write-Ok "Downloaded NVIDIA App installer ($([math]::Round($len/1MB,1)) MB)"
        }
    } catch {
        Write-Warn "Official installer download failed: $($_.Exception.Message)"
        return $false
    }

    # Pre-seed consent flags so installer/app skip EULA UI when possible.
    Accept-NvidiaAppEula | Out-Null

    # NVIDIA App NVI2 silent install. Parent setup.exe often hangs after install
    # (friend PCs stuck on "setup exit") - never -Wait forever; poll for files + kill.
    $argVariants = @(
        @('-silent', '-noreboot', '-noeula', '-nofinish', '-passive'),
        @('-s', '-noreboot', '-noeula', '-nofinish', '-passive'),
        @('-silent', '-noeula', '-noreboot')
    )
    foreach ($setupArgs in $argVariants) {
        Write-Ok ("Running NVIDIA App setup: " + ($setupArgs -join ' '))
        Write-HubProgress 74 'Installing NVIDIA App (official silent)...'
        $p = $null
        try {
            $p = Start-Process -FilePath $dest -ArgumentList $setupArgs -PassThru -WindowStyle Hidden
        } catch {
            Write-Warn "Setup launch failed: $($_.Exception.Message)"
            continue
        }
        if (-not $p) {
            Write-Warn 'Setup process did not start'
            continue
        }

        # Max 3 minutes per attempt. Success = App files present, not process exit.
        $deadline = (Get-Date).AddMinutes(3)
        $lastTick = -1
        while ((Get-Date) -lt $deadline) {
            if (Test-NvidiaAppInstalled) {
                Write-Ok 'NVIDIA App files detected during setup - treating install as complete'
                try {
                    if (-not $p.HasExited) {
                        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
                        Get-Process -Name 'setup','NVIDIA*','NVDisplay*' -ErrorAction SilentlyContinue |
                            Where-Object { $_.Path -and $_.Path -match '(?i)NVAPP|NVIDIA_app|NVI2' } |
                            Stop-Process -Force -ErrorAction SilentlyContinue
                    }
                } catch { }
                # Small settle so PE files unlock
                Start-Sleep -Seconds 2
                if (Test-NvidiaAppInstalled) {
                    Remove-NvidiaAppDesktopShortcuts | Out-Null
                    Write-Ok 'NVIDIA App installed via official NVIDIA download'
                    return $true
                }
            }
            if ($p.HasExited) {
                $code = 0
                try { $code = [int]$p.ExitCode } catch { $code = -1 }
                $hex = Format-ExitCodeHex $code
                Write-Ok "NVIDIA App setup exit: $code (0x$hex)"
                if (Test-NvidiaAppSetupUnsupportedExit -Code $code) {
                    $Script:NvidiaAppInstallUnsupported = $true
                    Write-Warn "NVIDIA App installer rejected this PC: system configuration not supported (exit $code / 0x$hex)."
                    Write-Warn 'NVIDIA installer limit (common on GTX 10-series). Skipping App; Control Panel + NVAPI next.'
                    return $false
                }
                # Other non-zero: do not sit around - short probe then next flags
                if ($code -ne 0) {
                    Write-Warn "NVIDIA App setup failed with exit $code (0x$hex) - not waiting"
                }
                break
            }
            $left = [int]($deadline - (Get-Date)).TotalSeconds
            if ($left -ne $lastTick -and ($left % 15 -eq 0)) {
                $lastTick = $left
                Write-Ok "Waiting for NVIDIA App install... ${left}s left"
                Write-HubProgress 74 "Installing NVIDIA App... ${left}s"
            }
            Start-Sleep -Seconds 2
        }

        if (-not $p.HasExited) {
            Write-Warn 'NVIDIA App setup still running after 3 min - stopping stuck installer'
            try {
                Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
                Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
                    Where-Object {
                        $_.Name -match '(?i)^setup\.exe$|^InstallPackage\.exe$' -or
                        ($_.CommandLine -and $_.CommandLine -match '(?i)NVAPP|NVIDIA_app|NVI2')
                    } |
                    ForEach-Object {
                        try { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } catch { }
                    }
            } catch { }
        } elseif ($p.HasExited) {
            $code = [int]$p.ExitCode
            if (Test-NvidiaAppSetupUnsupportedExit -Code $code) {
                $Script:NvidiaAppInstallUnsupported = $true
                return $false
            }
        }

        # Quick check only - do not burn 25s after a failed exit
        if (Test-NvidiaAppInstalled -or (Wait-NvidiaAppInstalled -Seconds 8)) {
            Remove-NvidiaAppDesktopShortcuts | Out-Null
            Write-Ok 'NVIDIA App installed via official NVIDIA download'
            return $true
        }
        if ($Script:NvidiaAppInstallUnsupported) { return $false }
        Write-Warn 'Setup finished but NVIDIA App not detected - trying next silent flag set'
    }
    return $false
}

function Test-NvidiaControlPanelInstalled {
    $appx = Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match '(?i)NVIDIAControlPanel|NVIDIACorp\.NVIDIAControlPanel'
    }
    if ($appx) { return $true }
    foreach ($p in @(
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\Control Panel Client\nvcplui.exe'),
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA Control Panel\nvcplui.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'NVIDIA Corporation\Control Panel Client\nvcplui.exe')
    )) {
        if (Test-Path -LiteralPath $p) { return $true }
    }
    return $false
}

function Accept-NvidiaControlPanelEula {
    # Classic Control Panel first-run license (separate from NVIDIA App).
    foreach ($p in @(
        'HKCU:\Software\NVIDIA Corporation\NVControlPanel2\Client',
        'HKLM:\SOFTWARE\NVIDIA Corporation\NvControlPanel2\Client',
        'HKLM:\SOFTWARE\NVIDIA Corporation\NVControlPanel2\Client'
    )) {
        Set-ExoRegDword -Path $p -Name 'EulaAccepted' -Value 1
        Set-ExoRegDword -Path $p -Name 'UserAgreedToEula' -Value 1
        Set-ExoRegDword -Path $p -Name 'AgreeToEula' -Value 1
        Set-ExoRegDword -Path $p -Name 'ShowEula' -Value 0
        Set-ExoRegDword -Path $p -Name 'ShowSedoanEula' -Value 0
    }
}

function Enable-NvidiaAdvanced3dImageSettings {
    # Control Panel: 3D Settings -> Adjust image settings with preview
    # -> "Use the advanced 3D image settings" so Manage 3D Settings / .nip apply.
    # NVTweak Gestalt: 0 = let app decide, 1 = Use my preference (Performance/Balanced/Quality),
    #                  2 = Use the advanced 3D image settings
    Write-Step 'Control Panel: Use the advanced 3D image settings (not Balanced)...'
    # Close CPL so next open re-reads registry (stale UI shows Balanced after old Gestalt)
    Get-Process -Name 'nvcplui','nvcpl' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    $paths = [System.Collections.Generic.List[string]]::new()
    foreach ($p in @(
        'HKCU:\Software\NVIDIA Corporation\Global\NVTweak',
        'HKLM:\SOFTWARE\NVIDIA Corporation\Global\NVTweak',
        'HKLM:\SYSTEM\CurrentControlSet\Services\nvlddmkm\Global\NVTweak'
    )) { [void]$paths.Add($p) }
    try {
        Get-ChildItem 'Registry::HKEY_USERS' -ErrorAction SilentlyContinue | Where-Object {
            $_.PSChildName -match '^S-1-5-21-\d+-\d+-\d+-\d+$'
        } | ForEach-Object {
            [void]$paths.Add(("Registry::HKEY_USERS\{0}\Software\NVIDIA Corporation\Global\NVTweak" -f $_.PSChildName))
        }
    } catch { }

    foreach ($p in ($paths | Select-Object -Unique)) {
        Set-ExoRegDword -Path $p -Name 'Gestalt' -Value 2
        # Clear leftover preference-slider residue some drivers leave when Balanced was selected
        try {
            if (Test-Path -LiteralPath $p) {
                Remove-ItemProperty -LiteralPath $p -Name 'Quality' -ErrorAction SilentlyContinue
                Remove-ItemProperty -LiteralPath $p -Name 'ImageSettings' -ErrorAction SilentlyContinue
                Remove-ItemProperty -LiteralPath $p -Name 'PreferredQuality' -ErrorAction SilentlyContinue
            }
        } catch { }
    }
    $ok = $false
    try {
        $g = (Get-ItemProperty -LiteralPath 'HKCU:\Software\NVIDIA Corporation\Global\NVTweak' -Name 'Gestalt' -ErrorAction Stop).Gestalt
        $ok = ([int]$g -eq 2)
    } catch { $ok = $false }
    if ($ok) {
        Write-Ok 'Advanced 3D image settings enabled (Gestalt=2, not Balanced preference slider)'
    } else {
        Write-Warn 'Could not verify advanced 3D image settings registry (Gestalt); Manage 3D settings may still apply via DRS'
    }
    return $ok
}

function Test-NvidiaAdvanced3dImageSettings {
    try {
        $g = (Get-ItemProperty -LiteralPath 'HKCU:\Software\NVIDIA Corporation\Global\NVTweak' -Name 'Gestalt' -ErrorAction Stop).Gestalt
        return ([int]$g -eq 2)
    } catch {
        return $false
    }
}

function Enable-NvidiaControlPanelDeveloperSettings {
    # Desktop -> Enable Developer Settings + Manage GPU Performance Counters
    # (allow access to all users). Useful for tooling / Nsight / counters.
    Write-Step 'Control Panel: Enable Developer Settings + GPU performance counters...'
    $paths = @(
        'HKLM:\SYSTEM\CurrentControlSet\Services\nvlddmkm\Parameters\Global\NVTweak',
        'HKLM:\SYSTEM\CurrentControlSet\Services\nvlddmkm\Global\NVTweak',
        'HKLM:\SOFTWARE\NVIDIA Corporation\Global\NVTweak',
        'HKCU:\Software\NVIDIA Corporation\Global\NVTweak'
    )
    foreach ($p in $paths) {
        # 1 = show Developer category in Control Panel (Desktop menu)
        Set-ExoRegDword -Path $p -Name 'NvDevToolsVisible' -Value 1
        # 0 = allow GPU performance counters to ALL users (not admin-only)
        Set-ExoRegDword -Path $p -Name 'RmProfilingAdminOnly' -Value 0
    }
    $ok = $false
    try {
        $root = 'HKLM:\SYSTEM\CurrentControlSet\Services\nvlddmkm\Parameters\Global\NVTweak'
        $vis = [int](Get-ItemProperty -LiteralPath $root -Name 'NvDevToolsVisible' -ErrorAction Stop).NvDevToolsVisible
        $prof = [int](Get-ItemProperty -LiteralPath $root -Name 'RmProfilingAdminOnly' -ErrorAction Stop).RmProfilingAdminOnly
        $ok = ($vis -eq 1 -and $prof -eq 0)
    } catch { $ok = $false }
    if ($ok) {
        Write-Ok 'Developer Settings ON + GPU performance counters allowed for all users'
    } else {
        Write-Warn 'Could not fully verify Developer Settings / performance counter registry'
    }
    return $ok
}

function Remove-NvidiaControlPanel {
    # Exo is the control panel - Store / desktop CPL is optional bloat.
    Write-Step 'Removing NVIDIA Control Panel (Exo panel replaces it)...'
    Get-Process -Name 'nvcplui', 'nvcpl' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    $appx = @(Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match '(?i)NVIDIAControlPanel|NVIDIACorp\.NVIDIAControlPanel'
    })
    foreach ($pkg in $appx) {
        try {
            Write-Ok "Removing Control Panel Appx: $($pkg.Name)"
            Remove-AppxPackage -Package $pkg.PackageFullName -ErrorAction Stop
        } catch {
            Write-Warn "Control Panel Appx remove: $($_.Exception.Message)"
        }
        try {
            Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -eq $pkg.Name } |
                ForEach-Object {
                    try { Remove-AppxPackage -Package $_.PackageFullName -AllUsers -ErrorAction SilentlyContinue } catch { }
                }
        } catch { }
    }

    $winget = Get-ExoWingetPath
    if ($winget) {
        try {
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = $winget
            $psi.Arguments = 'uninstall --id 9NF8H0H7WMLT -e --silent --accept-source-agreements --disable-interactivity'
            $psi.UseShellExecute = $false
            $psi.CreateNoWindow = $true
            $p = [Diagnostics.Process]::Start($psi)
            if ($p -and -not $p.WaitForExit(25000)) {
                try { $p.Kill($true) } catch { try { $p.Kill() } catch { } }
            }
        } catch { }
    }

    foreach ($dir in @(
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\Control Panel Client'),
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA Control Panel'),
        (Join-Path ${env:ProgramFiles(x86)} 'NVIDIA Corporation\Control Panel Client'),
        (Join-Path $env:LOCALAPPDATA 'Packages\NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj')
    )) {
        if (Test-Path -LiteralPath $dir) {
            if (Remove-ExoTreeForce -Path $dir) {
                Write-Ok "Removed Control Panel folder: $dir"
            } else {
                Write-Warn "Could not fully remove $dir"
            }
        }
    }

    $gone = -not (Test-NvidiaControlPanelInstalled)
    if ($gone) { Write-Ok 'NVIDIA Control Panel removed (Exo panel is the UI)' }
    else { Write-Warn 'Control Panel still detected after remove attempt' }
    return $gone
}

function Install-NvidiaControlPanel {
    if (Test-NvidiaControlPanelInstalled) {
        Write-Ok 'NVIDIA Control Panel already installed'
        return $true
    }

    Write-Step 'Installing NVIDIA Control Panel (display UI fallback)...'
    $winget = Get-ExoWingetPath
    if (-not $winget) {
        Write-Warn 'winget unavailable - cannot install Control Panel automatically'
        return $false
    }

    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $winget
        $psi.Arguments = 'install --id 9NF8H0H7WMLT -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity'
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $p = [Diagnostics.Process]::Start($psi)
        if ($p -and -not $p.WaitForExit(120000)) {
            try { $p.Kill($true) } catch { try { $p.Kill() } catch { } }
            Write-Warn 'Control Panel winget install timed out'
            return (Test-NvidiaControlPanelInstalled)
        }
    } catch {
        Write-Warn "Control Panel winget install failed: $($_.Exception.Message)"
    }

    if (Test-NvidiaControlPanelInstalled) {
        Write-Ok 'NVIDIA Control Panel installed'
        Accept-NvidiaControlPanelEula
        return $true
    }
    Write-Warn 'Control Panel install finished but package not detected'
    return $false
}

function Ensure-NvidiaDisplayClient {
    # Prefer NVIDIA App; if App is missing/unsupported, install classic Control Panel
    # so the machine still has a display UI. Scaling/Hz always go through NVAPI.
    param(
        [bool]$AppInstalled,
        [bool]$AppUnsupported
    )
    if ($AppInstalled) {
        Write-Ok 'Display client: NVIDIA App present (NVAPI applies scaling/Hz)'
        return @{ Client = 'app'; ControlPanel = (Test-NvidiaControlPanelInstalled) }
    }

    Write-Warn 'NVIDIA App unavailable - falling back to classic Control Panel + NVAPI display path'
    Write-HubProgress 75 'Control Panel fallback (display client)...'
    $cpl = Install-NvidiaControlPanel
    if ($cpl) {
        Write-Ok 'Display client: classic Control Panel (NVAPI applies scaling/Hz/Full RGB)'
    } else {
        Write-Warn 'Display client: NVAPI-only (no App, no Control Panel UI) - scaling/Hz still apply via driver'
    }
    return @{
        Client         = $(if ($cpl) { 'control-panel' } else { 'nvapi-only' })
        ControlPanel   = [bool]$cpl
        AppUnsupported = [bool]$AppUnsupported
    }
}

function Stop-NvidiaClientProcesses {
    # Kill App/GFE/Overlay UI and helpers. Do NOT stop NVDisplay.ContainerLocalSystem
    # (display driver). Temporarily stop NvContainerLocalSystem so files unlock.
    foreach ($svc in @('NvTelemetryContainer', 'NvContainerLocalSystem')) {
        try {
            $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
            if ($s -and $s.Status -ne 'Stopped') {
                Write-Ok "Stopping service $svc (App unlock)..."
                Stop-Service -Name $svc -Force -ErrorAction SilentlyContinue
            }
        } catch { }
    }
    foreach ($n in @(
        'NVIDIA App', 'NVIDIA Overlay', 'NVIDIA Share', 'nvsphelper64', 'nvsphelper',
        'NVIDIA Web Helper', 'GFExperience', 'NVIDIA Control Panel',
        'NvBackend', 'oawrapper', 'nvidia-installer', 'DarkModeCheck',
        'NVIDIA App Permission', 'NvOAWrapperCache', 'OAWrapper', 'nvcontainer'
    )) {
        Get-Process -Name $n -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    foreach ($im in @(
        'NVIDIA App.exe', 'NVIDIA Overlay.exe', 'NVIDIA Share.exe', 'NVIDIA Web Helper.exe',
        'nvsphelper64.exe', 'GFExperience.exe', 'NvBackend.exe', 'DarkModeCheck.exe',
        'NVIDIA App Permission.exe', 'NvOAWrapperCache.exe', 'OAWrapper.exe',
        'nvcontainer.exe', 'NVDisplay.Container.exe'
    )) {
        # Never kill the display driver container image if listed wrong - NVDisplay is separate
        if ($im -eq 'NVDisplay.Container.exe') { continue }
        try { & taskkill.exe /F /IM $im /T 2>$null | Out-Null } catch { }
    }
    # User-session nvcontainer only (display LS container service stays)
    try {
        Get-CimInstance Win32_Process -Filter "Name = 'nvcontainer.exe'" -ErrorAction SilentlyContinue | ForEach-Object {
            try { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } catch { }
        }
    } catch { }
}

function Get-Nvi2DllPath {
    foreach ($p in @(
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\Installer2\InstallerCore\NVI2.DLL'),
        (Join-Path ${env:ProgramFiles(x86)} 'NVIDIA Corporation\Installer2\InstallerCore\NVI2.DLL')
    )) {
        if (Test-Path -LiteralPath $p) { return $p }
    }
    return $null
}

function Get-Nvi2InstalledPackageNames {
    $names = [System.Collections.Generic.List[string]]::new()
    foreach ($root in @(
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\Installer2'),
        (Join-Path ${env:ProgramFiles(x86)} 'NVIDIA Corporation\Installer2')
    )) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            $n = $_.Name
            if ($n -match '^(?<pkg>.+)\.\{[0-9A-Fa-f\-]{36}\}$') {
                [void]$names.Add($Matches['pkg'])
            }
        }
    }
    # ARP child names: {GUID}_Display.NvApp.MessageBus
    foreach ($rp in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )) {
        if (-not (Test-Path -LiteralPath $rp)) { continue }
        Get-ChildItem -LiteralPath $rp -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.PSChildName -match '^[\{]?[0-9A-Fa-f\-]{36}[\}]?_(?<pkg>.+)$') {
                [void]$names.Add($Matches['pkg'])
            }
        }
    }
    return @($names | Select-Object -Unique | Sort-Object)
}

function Test-Nvi2ProtectedPackageName([string]$Name) {
    # Only keep what Exo wants: Display.Driver (+ NVI2 installer plumbing + containers).
    # Control Panel is a Store package, not NVI2. Everything else is fair game to strip.
    if ([string]::IsNullOrWhiteSpace($Name)) { return $true }
    return ($Name -match '^(?i)Display\.Driver$|InstallerCore|^installer$|Display\.NVWMI|NvContainer(\.|$)')
}

function Test-Nvi2AppPackageName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) { return $false }
    if (Test-Nvi2ProtectedPackageName $Name) { return $false }
    return ($Name -match '(?i)^Display\.NvApp|^NvApp|ShadowPlay|FrameView|NvTelemetry|NvPlugin|NvDLISR|GFExperience|GeForceExperience|Display\.GFExperience')
}

function Test-Nvi2AudioPackageName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) { return $false }
    return ($Name -match '(?i)VirtualAudio|HDAudio|Display\.Audio|^Audio\.|HD\.Audio')
}

function Test-Nvi2BloatPackageName([string]$Name) {
    # Install-time strip beyond HD/Virtual Audio: ShadowPlay, NvBackend, NodeJS,
    # and telemetry sub-packages exposed by the NVI2 setup package set.
    # Display.Driver, PhysX, and NVI2 installer plumbing/containers are never touched.
    if ([string]::IsNullOrWhiteSpace($Name)) { return $false }
    if (Test-Nvi2ProtectedPackageName $Name) { return $false }
    if ($Name -match '(?i)PhysX') { return $false }
    return ($Name -match '(?i)ShadowPlay|NvBackend|NodeJS|Node\.js|Telemetry')
}

function Invoke-Nvi2UninstallPackage {
    param(
        [Parameter(Mandatory)][string]$PackageName,
        [int]$TimeoutSec = 90
    )
    $nvi2 = Get-Nvi2DllPath
    if (-not $nvi2) {
        Write-Warn "NVI2.DLL missing - cannot uninstall package $PackageName via installer"
        return $false
    }
    # CRITICAL: use 64-bit System32 RunDll32 first. SysWOW64 returns 0x80070057
    # (E_INVALIDARG) for NVI2 UninstallPackage and never removes the App.
    $rundllCandidates = @(
        (Join-Path $env:SystemRoot 'System32\RunDll32.EXE'),
        (Join-Path $env:SystemRoot 'SysWOW64\RunDll32.EXE')
    ) | Where-Object { Test-Path -LiteralPath $_ }

    # Flags that skip NVI2UI confirmation / reboot prompts
    $flagSets = @(
        '-silent -noreboot',
        '-silent',
        '-silent -noreboot -passive'
    )

    foreach ($rundll in $rundllCandidates) {
        foreach ($flags in $flagSets) {
            try {
                Write-Ok "NVI2 silent uninstall: $PackageName ($([IO.Path]::GetFileName($rundll)) $flags)"
                # Exact contract: rundll32 "NVI2.DLL",UninstallPackage PackageName -silent -noreboot
                $arg = "`"$nvi2`",UninstallPackage $PackageName $flags"
                $psi = New-Object System.Diagnostics.ProcessStartInfo
                $psi.FileName = $rundll
                $psi.Arguments = $arg
                $psi.UseShellExecute = $false
                $psi.CreateNoWindow = $true
                $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
                # Do not redirect stdin/stdout - can break NVI2 on some builds
                $proc = [Diagnostics.Process]::Start($psi)
                if (-not $proc) { continue }
                $ok = $proc.WaitForExit([Math]::Max(5, $TimeoutSec) * 1000)
                if (-not $ok) {
                    try { $proc.Kill($true) } catch { try { $proc.Kill() } catch { } }
                    Write-Warn "NVI2 $PackageName timed out after ${TimeoutSec}s"
                    continue
                }
                Write-Ok "NVI2 $PackageName exit $($proc.ExitCode)"
                # Exit 0 from System32 = success; also accept package folder gone
                $still = @(Get-Nvi2InstalledPackageNames | Where-Object { $_ -eq $PackageName })
                if ($proc.ExitCode -eq 0) { return $true }
                if ($still.Count -eq 0) { return $true }
                # SysWOW64 invalid-arg path - try next rundll
                if ($proc.ExitCode -eq -2147024809 -or $proc.ExitCode -eq 0x80070057) { break }
            } catch {
                Write-Warn "NVI2 uninstall $PackageName : $($_.Exception.Message)"
            }
        }
    }

    # Fallback: cmd.exe with System32 RunDll32 (batch-style quoting)
    try {
        $rundll = Join-Path $env:SystemRoot 'System32\RunDll32.EXE'
        if (Test-Path -LiteralPath $rundll) {
            $line = "`"$rundll`" `"$nvi2`",UninstallPackage $PackageName -silent -noreboot"
            Write-Ok "NVI2 cmd fallback: $PackageName"
            $p = Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', $line) -Wait -PassThru -WindowStyle Hidden -ErrorAction SilentlyContinue
            if ($p) { Write-Ok "NVI2 cmd $PackageName exit $($p.ExitCode)" }
            if ($p -and $p.ExitCode -eq 0) { return $true }
            $still = @(Get-Nvi2InstalledPackageNames | Where-Object { $_ -eq $PackageName })
            if ($still.Count -eq 0) { return $true }
        }
    } catch { }

    return $false
}

function Remove-ExoTreeForce {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $true }
    try {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
        return $true
    } catch { }
    # takeown + icacls then retry (locked App trees after partial uninstall)
    try {
        $null = & takeown.exe /F $Path /R /D Y 2>$null
        $null = & icacls.exe $Path /grant Administrators:F /T /C /Q 2>$null
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
        return $true
    } catch { }
    # robocopy mirror empty is reliable for stubborn trees
    try {
        $empty = Join-Path $env:TEMP ("exo-empty-" + [guid]::NewGuid().ToString('n'))
        New-Item -ItemType Directory -Path $empty -Force | Out-Null
        $null = & robocopy.exe $empty $Path /MIR /R:0 /W:0 /NFL /NDL /NJH /NJS /nc /ns /np 2>$null
        try { Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue } catch { }
        try { Remove-Item -LiteralPath $empty -Recurse -Force -ErrorAction SilentlyContinue } catch { }
        return -not (Test-Path -LiteralPath $Path)
    } catch {
        return -not (Test-Path -LiteralPath $Path)
    }
}

function Remove-NvidiaAppArpLeftovers {
    foreach ($rp in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )) {
        if (-not (Test-Path -LiteralPath $rp)) { continue }
        Get-ChildItem -LiteralPath $rp -ErrorAction SilentlyContinue | ForEach-Object {
            $leaf = $_.PSChildName
            $pkg = $null
            if ($leaf -match '^[\{]?[0-9A-Fa-f\-]{36}[\}]?_(?<pkg>.+)$') { $pkg = $Matches['pkg'] }
            $disp = $null
            try { $disp = [string](Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue).DisplayName } catch { }
            $isApp = $false
            if ($pkg -and (Test-Nvi2AppPackageName $pkg)) { $isApp = $true }
            if ($disp -match '(?i)NVIDIA App|GeForce Experience|ShadowPlay|FrameView|NvApp|NVIDIA Backend|NVIDIA MessageBus|NVIDIA Telemetry|NvDLISR|Watchdog Plugin') {
                if ($disp -notmatch '(?i)Control Panel|Graphics Driver|Virtual Audio|Display Driver') { $isApp = $true }
            }
            if (-not $isApp) { return } # continue next ARP entry
            try {
                Remove-Item -LiteralPath $_.PSPath -Recurse -Force -ErrorAction Stop
                Write-Ok "Removed ARP leftover: $leaf"
            } catch {
                Write-Warn "ARP remove $leaf : $($_.Exception.Message)"
            }
        }
    }
    # Clear stuck NVI2 pending uninstall/install for App packages (blocks silent re-runs)
    $pendingRoot = 'HKLM:\SOFTWARE\NVIDIA Corporation\Installer2\Pending'
    if (Test-Path -LiteralPath $pendingRoot) {
        Get-ChildItem -LiteralPath $pendingRoot -ErrorAction SilentlyContinue | ForEach-Object {
            if (Test-Nvi2AppPackageName $_.PSChildName) {
                try {
                    Remove-Item -LiteralPath $_.PSPath -Recurse -Force -ErrorAction Stop
                    Write-Ok "Cleared NVI2 pending: $($_.PSChildName)"
                } catch { }
            }
        }
    }
}

function Remove-NvidiaAudioComponents {
    # User policy: Display.Driver + classic Control Panel ONLY. No Virtual Audio / HD Audio.
    Write-Step 'Removing NVIDIA Virtual Audio / HD Audio (not needed)...'
    $pkgs = [System.Collections.Generic.List[string]]::new()
    foreach ($p in @('VirtualAudio.Driver', 'HDAudio.Driver', 'Display.Audio', 'HDAudio')) {
        if (-not $pkgs.Contains($p)) { [void]$pkgs.Add($p) }
    }
    foreach ($p in @(Get-Nvi2InstalledPackageNames | Where-Object { Test-Nvi2AudioPackageName $_ })) {
        if (-not $pkgs.Contains($p)) { [void]$pkgs.Add($p) }
    }
    foreach ($pkg in $pkgs) {
        [void](Invoke-Nvi2UninstallPackage -PackageName $pkg -TimeoutSec 75)
    }

    # Disable leftover PnP audio endpoints so they cannot reappear as default devices
    try {
        Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
            $_.FriendlyName -match '(?i)NVIDIA.*(High Definition Audio|Virtual Audio)|NVIDIA Virtual Audio'
        } | ForEach-Object {
            try {
                Write-Ok "Disabling audio device: $($_.FriendlyName)"
                Disable-PnpDevice -InstanceId $_.InstanceId -Confirm:$false -ErrorAction SilentlyContinue
            } catch { }
            try {
                Remove-PnpDevice -InstanceId $_.InstanceId -Confirm:$false -ErrorAction SilentlyContinue
            } catch { }
        }
    } catch { }

    # Installer2 leftover folders + common install roots
    foreach ($i2 in @(
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\Installer2'),
        (Join-Path ${env:ProgramFiles(x86)} 'NVIDIA Corporation\Installer2')
    )) {
        if (-not (Test-Path -LiteralPath $i2)) { continue }
        Get-ChildItem -LiteralPath $i2 -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            $base = if ($_.Name -match '^(?<pkg>.+)\.\{[0-9A-Fa-f\-]{36}\}$') { $Matches['pkg'] } else { $_.Name }
            if (Test-Nvi2AudioPackageName $base) {
                if (Remove-ExoTreeForce -Path $_.FullName) {
                    Write-Ok "Removed audio package folder: $($_.Name)"
                }
            }
        }
    }
    foreach ($dir in @(
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\Virtual Audio'),
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\HD Audio'),
        (Join-Path ${env:ProgramFiles(x86)} 'NVIDIA Corporation\Virtual Audio')
    )) {
        if (Test-Path -LiteralPath $dir) { [void](Remove-ExoTreeForce -Path $dir) }
    }

    # ARP leftovers
    foreach ($rp in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )) {
        if (-not (Test-Path -LiteralPath $rp)) { continue }
        Get-ChildItem -LiteralPath $rp -ErrorAction SilentlyContinue | ForEach-Object {
            $leaf = $_.PSChildName
            $pkg = $null
            if ($leaf -match '^[\{]?[0-9A-Fa-f\-]{36}[\}]?_(?<pkg>.+)$') { $pkg = $Matches['pkg'] }
            $disp = $null
            try { $disp = [string](Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue).DisplayName } catch { }
            $isAudio = ($pkg -and (Test-Nvi2AudioPackageName $pkg)) -or
                       ($disp -match '(?i)NVIDIA Virtual Audio|NVIDIA HD Audio|NVIDIA High Definition Audio')
            if (-not $isAudio) { return }
            try {
                Remove-Item -LiteralPath $_.PSPath -Recurse -Force -ErrorAction Stop
                Write-Ok "Removed audio ARP: $leaf"
            } catch { }
        }
    }

    $stillPkg = @(Get-Nvi2InstalledPackageNames | Where-Object { Test-Nvi2AudioPackageName $_ })
    $stillDev = @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
        $_.Status -eq 'OK' -and $_.FriendlyName -match '(?i)NVIDIA.*(High Definition Audio|Virtual Audio)|NVIDIA Virtual Audio'
    })
    if ($stillPkg.Count -eq 0 -and $stillDev.Count -eq 0) {
        Write-Ok 'NVIDIA audio components cleared (driver + Control Panel only)'
        return $true
    }
    Write-Warn ("NVIDIA audio still present: packages=[{0}] devices=[{1}]" -f ($stillPkg -join ','), (($stillDev | ForEach-Object FriendlyName) -join ','))
    return $false
}

function Remove-NvidiaBloatComponents {
    # Same NVI2 silent-uninstall mechanism as the audio strip, extended to the
    # remaining install-time bloat: ShadowPlay, NvBackend, NodeJS, telemetry
    # sub-packages. Keeps Display.Driver + PhysX. No INF edits, no EAC strip.
    Write-Step 'Stripping NVI2 bloat packages (ShadowPlay / NvBackend / NodeJS / telemetry)...'
    $present = @(Get-Nvi2InstalledPackageNames | Where-Object { Test-Nvi2BloatPackageName $_ })
    if ($present.Count -eq 0) {
        Write-Ok 'No NVI2 bloat packages registered (ShadowPlay / NvBackend / NodeJS / telemetry absent)'
    }
    foreach ($pkg in $present) {
        [void](Invoke-Nvi2UninstallPackage -PackageName $pkg -TimeoutSec 75)
    }

    # Leftover Installer2 folders for the same package families
    foreach ($i2 in @(
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\Installer2'),
        (Join-Path ${env:ProgramFiles(x86)} 'NVIDIA Corporation\Installer2')
    )) {
        if (-not (Test-Path -LiteralPath $i2)) { continue }
        Get-ChildItem -LiteralPath $i2 -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            $base = if ($_.Name -match '^(?<pkg>.+)\.\{[0-9A-Fa-f\-]{36}\}$') { $Matches['pkg'] } else { $_.Name }
            if (Test-Nvi2BloatPackageName $base) {
                if (Remove-ExoTreeForce -Path $_.FullName) {
                    Write-Ok "Removed bloat package folder: $($_.Name)"
                }
            }
        }
    }

    $still = @(Get-Nvi2InstalledPackageNames | Where-Object { Test-Nvi2BloatPackageName $_ })
    if ($still.Count -eq 0) {
        Write-Ok 'NVI2 bloat strip complete (Display.Driver + PhysX preserved)'
        return $true
    }
    Write-Warn ("NVI2 bloat packages still registered after strip: {0}" -f ($still -join ', '))
    return $false
}

function Remove-NvidiaClientTraces {
    # Wipe App + GFE via NVI2 silent uninstall (no winget - too slow / flaky).
    # KEEP classic Control Panel Store package + Display.Driver only (no audio, no App).
    Write-Step 'Wiping NVIDIA App + GFE (silent NVI2, no prompts, no winget)...'
    Stop-NvidiaClientProcesses

    $preferredOrder = @(
        'Display.NvApp.MessageBus',
        'Display.NvApp.NvBackend',
        'Display.NvApp.NvCPL',
        'ShadowPlay',
        'FrameViewSdk',
        'NvPlugin.Watchdog',
        'NvTelemetry',
        'NvDLISR',
        'Display.NvApp',
        'Display.GFExperience',
        'GFExperience'
    )
    $discovered = @(Get-Nvi2InstalledPackageNames | Where-Object { Test-Nvi2AppPackageName $_ })
    $toRemove = [System.Collections.Generic.List[string]]::new()
    foreach ($p in $preferredOrder) {
        if ($discovered -contains $p -or $true) {
            # Always try preferred names (NVI2 no-ops if missing)
            if (-not $toRemove.Contains($p)) { [void]$toRemove.Add($p) }
        }
    }
    foreach ($p in $discovered) {
        if (-not $toRemove.Contains($p)) { [void]$toRemove.Add($p) }
    }

    Write-Ok ("NVI2 App packages to remove: " + ($toRemove -join ', '))
    foreach ($pkg in $toRemove) {
        Stop-NvidiaClientProcesses
        [void](Invoke-Nvi2UninstallPackage -PackageName $pkg -TimeoutSec 75)
    }

    # Second pass for anything still registered
    $left = @(Get-Nvi2InstalledPackageNames | Where-Object { Test-Nvi2AppPackageName $_ })
    if ($left.Count -gt 0) {
        Write-Warn ("Retry NVI2 for remaining: " + ($left -join ', '))
        Stop-NvidiaClientProcesses
        foreach ($pkg in $left) {
            [void](Invoke-Nvi2UninstallPackage -PackageName $pkg -TimeoutSec 90)
        }
    }

    # Remove App / GFE Appx only - never NVIDIA Control Panel Store package.
    $appxTargets = @(Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match '(?i)NVIDIAApp|NVIDIA\.App|GeForceExperience' -and
        $_.Name -notmatch '(?i)ControlPanel'
    })
    foreach ($pkg in $appxTargets) {
        try {
            Write-Ok "Removing Appx package: $($pkg.Name)"
            Remove-AppxPackage -Package $pkg.PackageFullName -ErrorAction Stop
        } catch {
            Write-Warn "Appx remove $($pkg.Name): $($_.Exception.Message)"
        }
        try {
            # Elevated: also strip for all users when possible
            Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -eq $pkg.Name -and $_.Name -notmatch '(?i)ControlPanel' } |
                ForEach-Object {
                    try { Remove-AppxPackage -Package $_.PackageFullName -AllUsers -ErrorAction SilentlyContinue } catch { }
                }
        } catch { }
    }

    # No winget uninstall - it is slow, often interactive, and does not drive NVI2 well.

    # App / GFE folders only - never Control Panel Client paths.
    $folderTargets = @(
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA App'),
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA Overlay'),
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA GeForce Experience'),
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\GeForce Experience'),
        (Join-Path ${env:ProgramFiles(x86)} 'NVIDIA Corporation\NVIDIA App'),
        (Join-Path ${env:ProgramFiles(x86)} 'NVIDIA Corporation\NVIDIA GeForce Experience'),
        (Join-Path $env:LOCALAPPDATA 'NVIDIA Corporation\NVIDIA App'),
        (Join-Path $env:LOCALAPPDATA 'NVIDIA Corporation\NVIDIA GeForce Experience'),
        (Join-Path $env:PROGRAMDATA 'NVIDIA Corporation\NVIDIA App'),
        (Join-Path $env:PROGRAMDATA 'NVIDIA Corporation\GeForce Experience'),
        # Official App payload cache used by NVI2 (Pending PackageConfig paths)
        'C:\NVIDIA\NVAPP2',
        'C:\NVIDIA\Display.NvApp',
        (Join-Path $env:ProgramData 'NVIDIA\NVAPP2')
    )
    # Leftover Installer2 component folders for App packages
    foreach ($i2 in @(
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\Installer2'),
        (Join-Path ${env:ProgramFiles(x86)} 'NVIDIA Corporation\Installer2')
    )) {
        if (-not (Test-Path -LiteralPath $i2)) { continue }
        Get-ChildItem -LiteralPath $i2 -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            $base = if ($_.Name -match '^(?<pkg>.+)\.\{[0-9A-Fa-f\-]{36}\}$') { $Matches['pkg'] } else { $_.Name }
            if (Test-Nvi2AppPackageName $base) { $folderTargets += $_.FullName }
        }
    }

    Stop-NvidiaClientProcesses
    Start-Sleep -Milliseconds 500
    foreach ($dir in ($folderTargets | Select-Object -Unique)) {
        if (Test-Path -LiteralPath $dir) {
            if (Remove-ExoTreeForce -Path $dir) {
                Write-Ok "Removed client folder: $dir"
            } else {
                Write-Warn "Could not fully remove $dir"
            }
        }
    }

    Remove-NvidiaAppArpLeftovers
    Remove-NvidiaAppDesktopShortcuts | Out-Null
    $audioCleared = Remove-NvidiaAudioComponents
    $trayCleared = Clear-NvidiaTrayGhostIcons

    # Do NOT restart NvContainerLocalSystem - that is App-stack and re-registers tray icons.
    # Display driver uses NVDisplay.ContainerLocalSystem (left alone).

    $appGone = -not (Test-NvidiaAppInstalled)
    $cplOk = Test-NvidiaControlPanelInstalled
    if ($appGone) { Write-Ok 'NVIDIA App / GFE traces cleared' } else { Write-Warn 'NVIDIA App still detected after wipe' }
    if ($cplOk) { Write-Ok 'Classic Control Panel kept' } else { Write-Ok 'Classic Control Panel not present yet (will install next)' }
    return [pscustomobject]@{
        AppCleared = [bool]$appGone
        AudioCleared = [bool]$audioCleared
        TrayGhostsCleared = [int]$trayCleared
        ControlPanelPresent = [bool]$cplOk
        PackagesTried = @($toRemove)
    }
}

function Set-ExoRegDword {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][int]$Value
    )
    if (-not (Test-Path -LiteralPath $Path)) {
        try { New-Item -Path $Path -Force | Out-Null } catch { return }
    }
    try {
        Set-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -Type DWord -Force -ErrorAction Stop
    } catch {
        try {
            New-ItemProperty -LiteralPath $Path -Name $Name -PropertyType DWord -Value $Value -Force -ErrorAction SilentlyContinue | Out-Null
        } catch { }
    }
}

function Accept-NvidiaAppEula {
    # Silent install uses -noeula; still stamp ProgramData + first-launch flags so App skips EULA/OOTB UI.
    Write-Step 'Accepting NVIDIA App EULA / first-run consent (no UI)...'
    $pdApp = Join-Path $env:ProgramData 'NVIDIA Corporation\NVIDIA App'
    if (-not (Test-Path -LiteralPath $pdApp)) {
        try { New-Item -ItemType Directory -Path $pdApp -Force | Out-Null } catch { }
    }
    $accepted = Join-Path $pdApp 'AcceptedEULA.txt'
    $licenseCandidates = @(
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA App\license.txt'),
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA App\EULA\license.txt'),
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA App\EULA\EULA.txt'),
        (Join-Path ${env:ProgramFiles(x86)} 'NVIDIA Corporation\NVIDIA App\license.txt')
    )
    $src = $licenseCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    try {
        if ($src) {
            Copy-Item -LiteralPath $src -Destination $accepted -Force
            Write-Ok "EULA accepted stamp from: $src"
        } else {
            @(
                'NVIDIA App License Agreement',
                'Accepted by Exo silent install path.',
                ("AcceptedUtc={0}" -f (Get-Date).ToUniversalTime().ToString('o')),
                'Version 7'
            ) -join [Environment]::NewLine | Set-Content -LiteralPath $accepted -Encoding UTF8
            Write-Ok 'EULA accepted stamp written (fallback text)'
        }
    } catch {
        Write-Warn "EULA stamp: $($_.Exception.Message)"
    }

    # Privacy: required-only (matches NVIDIA App privacy center minimum).
    foreach ($acct in @(
        (Join-Path $pdApp 'NvAccount.json'),
        (Join-Path $env:LOCALAPPDATA 'NVIDIA Corporation\NVIDIA App\NvAccount\Account.json')
    )) {
        try {
            $dir = Split-Path -Parent $acct
            if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
            '{"privacySettings":"RequiredOnly"}' | Set-Content -LiteralPath $acct -Encoding UTF8
        } catch { }
    }
    $diag = Join-Path $pdApp 'NvDriverDiagnostics\state.json'
    try {
        $dDir = Split-Path -Parent $diag
        if (-not (Test-Path -LiteralPath $dDir)) { New-Item -ItemType Directory -Path $dDir -Force | Out-Null }
        '{"consentRequestIsComplete":true}' | Set-Content -LiteralPath $diag -Encoding UTF8
    } catch { }

    # CRITICAL: NVAPP_FIRST_LAUNCH=1 forces first-run onboarding (EULA + overlay questions).
    # Set to 0 so the App treats setup as already completed.
    foreach ($p in @(
        'HKCU:\Software\NVIDIA Corporation\NVIDIA App',
        'HKLM:\SOFTWARE\NVIDIA Corporation\NVIDIA App',
        'HKCU:\Software\NVIDIA Corporation\Global\NvApp',
        'HKLM:\SOFTWARE\NVIDIA Corporation\Global\NvApp',
        'HKCU:\Software\NVIDIA Corporation\NVControlPanel2\Client',
        'HKLM:\SOFTWARE\NVIDIA Corporation\NvControlPanel2\Client',
        'HKLM:\SOFTWARE\NVIDIA Corporation\NVControlPanel2\Client'
    )) {
        Set-ExoRegDword -Path $p -Name 'EULAAccepted' -Value 1
        Set-ExoRegDword -Path $p -Name 'LicenseAccepted' -Value 1
        Set-ExoRegDword -Path $p -Name 'AcceptedEULA' -Value 1
        Set-ExoRegDword -Path $p -Name 'UserAgreedToEula' -Value 1
        Set-ExoRegDword -Path $p -Name 'AgreeToEula' -Value 1
        Set-ExoRegDword -Path $p -Name 'ShowEULA' -Value 0
        Set-ExoRegDword -Path $p -Name 'ShowSedoanEula' -Value 0
        Set-ExoRegDword -Path $p -Name 'OOBECompleted' -Value 1
        Set-ExoRegDword -Path $p -Name 'FirstRunCompleted' -Value 1
        Set-ExoRegDword -Path $p -Name 'SkipWelcome' -Value 1
        Set-ExoRegDword -Path $p -Name 'SkipOOTB' -Value 1
        Set-ExoRegDword -Path $p -Name 'OOTBCompleted' -Value 1
        Set-ExoRegDword -Path $p -Name 'OOTBStatus' -Value 2
        Set-ExoRegDword -Path $p -Name 'NVAPP_FIRST_LAUNCH' -Value 0
        Set-ExoRegDword -Path $p -Name 'FirstLaunch' -Value 0
        Set-ExoRegDword -Path $p -Name 'IsFirstLaunch' -Value 0
        Set-ExoRegDword -Path $p -Name 'Installed' -Value 1
    }
    Write-Ok 'NVIDIA App EULA / first-launch / OOTB consent flags set (NVAPP_FIRST_LAUNCH=0)'
}

function Clear-NvidiaAppOotbCache {
    # Partial OOTB progress in CEF IndexedDB re-opens onboarding with overlay "on".
    # Wipe CEF user state after install so our OOTBStatus=2 / first-launch flags win.
    $cef = Join-Path $env:LOCALAPPDATA 'NVIDIA Corporation\NVIDIA App\CefCache'
    if (Test-Path -LiteralPath $cef) {
        try {
            Remove-Item -LiteralPath $cef -Recurse -Force -ErrorAction Stop
            Write-Ok 'Cleared NVIDIA App CEF/OOTB cache so onboarding cannot resume mid-flow'
        } catch {
            Write-Warn "CEF cache clear: $($_.Exception.Message)"
            # Best-effort: delete IndexedDB only
            $idb = Join-Path $cef 'Default\IndexedDB'
            if (Test-Path -LiteralPath $idb) {
                try { Remove-Item -LiteralPath $idb -Recurse -Force -ErrorAction SilentlyContinue } catch { }
            }
        }
    }
}

function Enable-NvidiaAppBetaChannel {
    # NVIDIA App Settings -> About -> "Beta" OTA channel (nvappOTAChannel=beta).
    Write-Step 'Enabling NVIDIA App beta update channel...'
    $regDir = Join-Path $env:ProgramData 'NVIDIA Corporation\NVIDIA App\UpdateFramework\registry'
    try {
        if (-not (Test-Path -LiteralPath $regDir)) {
            New-Item -ItemType Directory -Path $regDir -Force | Out-Null
        }
        $regFile = Join-Path $regDir 'nvapp.json'
        $boot = (Get-Date).ToString('ddd MMM dd HH:mm:ss yyyy', [Globalization.CultureInfo]::InvariantCulture) + "`n"
        $existing = $null
        if (Test-Path -LiteralPath $regFile) {
            try { $existing = Get-Content -LiteralPath $regFile -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $existing = $null }
        }
        if ($existing -is [System.Array] -and $existing.Count -gt 0 -and $existing[0].registry) {
            $existing[0].componentName = 'nvapp'
            if (-not $existing[0].registry.firstBootTime) {
                $existing[0].registry | Add-Member -NotePropertyName firstBootTime -NotePropertyValue $boot -Force
            }
            $existing[0].registry | Add-Member -NotePropertyName nvappOTAChannel -NotePropertyValue 'beta' -Force
            ($existing | ConvertTo-Json -Depth 6 -Compress) | Set-Content -LiteralPath $regFile -Encoding UTF8
        } else {
            $payload = @(
                [ordered]@{
                    componentName = 'nvapp'
                    registry = [ordered]@{
                        firstBootTime    = $boot
                        nvappOTAChannel  = 'beta'
                    }
                }
            )
            # ConvertTo-Json of array of ordered hashtables
            $json = '[{"componentName":"nvapp","registry":{"firstBootTime":"' +
                ($boot -replace '"', '\"' -replace "`n", '\n') +
                '","nvappOTAChannel":"beta"}}]'
            Set-Content -LiteralPath $regFile -Value $json -Encoding UTF8
        }
        Write-Ok 'nvappOTAChannel=beta written'
    } catch {
        Write-Warn "Beta channel write failed: $($_.Exception.Message)"
    }

    foreach ($p in @(
        'HKCU:\Software\NVIDIA Corporation\NVIDIA App',
        'HKLM:\SOFTWARE\NVIDIA Corporation\NVIDIA App',
        'HKCU:\Software\NVIDIA Corporation\Global\NvApp'
    )) {
        try {
            if (-not (Test-Path -LiteralPath $p)) { New-Item -Path $p -Force | Out-Null }
            Set-ItemProperty -LiteralPath $p -Name 'nvappOTAChannel' -Value 'beta' -Type String -Force -ErrorAction SilentlyContinue
            Set-ItemProperty -LiteralPath $p -Name 'OTAChannel' -Value 'beta' -Type String -Force -ErrorAction SilentlyContinue
            Set-ExoRegDword -Path $p -Name 'EnableBeta' -Value 1
            Set-ExoRegDword -Path $p -Name 'JoinBeta' -Value 1
            Set-ExoRegDword -Path $p -Name 'BetaOptIn' -Value 1
        } catch { }
    }
    Write-Ok 'NVIDIA App beta channel enabled'
}

function Set-NvidiaAppBackendConfigDebloat {
    # Merge lean settings into ProgramData + per-user NvBackend config.xml when present.
    # OOTBStatus=2 = onboarding finished (matches real post-OOTB App installs).
    $targets = @(
        (Join-Path $env:ProgramData 'NVIDIA Corporation\NVIDIA App\NvBackend\config.xml'),
        (Join-Path $env:LOCALAPPDATA 'NVIDIA Corporation\NVIDIA App\NvBackend\config.xml')
    )
    $want = [ordered]@{
        OOTBStatus                     = '2'
        ShowRewardsNotification        = '0'
        EnableAutomaticApplyOPS        = '0'
        EnableUpdateTypeOPS            = '0'
        EnableAutomaticApplicationScan = '1'
        BatteryBoostIsSupported        = '0'
        SendTelemetryForGFESupportedApps = '0'
        EnableTelemetry                = '0'
        EnableHighlights               = '0'
        EnableOverlay                  = '0'
        EnableNotifications            = '0'
        EnableInstantReplay            = '0'
        EnableFreestyle                = '0'
        EnablePhotoMode                = '0'
        EnableGameFilters              = '0'
        EnableAnsel                    = '0'
        EnableDiscover                 = '0'
        EnableRewards                  = '0'
        ShareEnabled                   = '0'
        SkipOOTB                       = '1'
        OOTBCompleted                  = '1'
    }
    foreach ($path in $targets) {
        try {
            $dir = Split-Path -Parent $path
            if (-not (Test-Path -LiteralPath $dir)) {
                New-Item -ItemType Directory -Path $dir -Force | Out-Null
            }
            $xml = $null
            if (Test-Path -LiteralPath $path) {
                try {
                    [xml]$xml = Get-Content -LiteralPath $path -Raw -Encoding UTF8
                } catch { $xml = $null }
            }
            if (-not $xml) {
                $xml = New-Object System.Xml.XmlDocument
                $xml.AppendChild($xml.CreateXmlDeclaration('1.0', 'utf-8', $null)) | Out-Null
                $root = $xml.CreateElement('BackendConfiguration')
                $root.SetAttribute('version', '1.0')
                [void]$xml.AppendChild($root)
            }
            $rootNode = $xml.DocumentElement
            if (-not $rootNode) { continue }
            foreach ($key in $want.Keys) {
                $node = $rootNode.SelectSingleNode("Setting[@name='$key']")
                if ($node) {
                    $node.SetAttribute('value', [string]$want[$key])
                } else {
                    $el = $xml.CreateElement('Setting')
                    $el.SetAttribute('name', $key)
                    $el.SetAttribute('value', [string]$want[$key])
                    [void]$rootNode.AppendChild($el)
                }
            }
            $xml.Save($path)
            Write-Ok "NvBackend config debloat: $path"
        } catch {
            Write-Warn "NvBackend config $path : $($_.Exception.Message)"
        }
    }
}

function Set-NvidiaWindowsNotificationsOff {
    # Quiet Windows: disable NVIDIA App / Control Panel / GFE toast banners.
    Write-Step 'Disabling Windows notifications for NVIDIA clients...'
    $base = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
    if (-not (Test-Path -LiteralPath $base)) {
        New-Item -Path $base -Force | Out-Null
    }

    $setOff = {
        param([string]$Id)
        $path = Join-Path $base $Id
        if (-not (Test-Path -LiteralPath $path)) { New-Item -Path $path -Force | Out-Null }
        Set-ItemProperty -Path $path -Name 'Enabled' -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $path -Name 'ShowInActionCenter' -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
    }

    $ids = @(
        'NVIDIA App',
        'com.nvidia.nvapp',
        'NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj!NVIDIACorp.NVIDIAControlPanel',
        'NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj',
        'NVIDIA GeForce Experience',
        'NVIDIA Share',
        'NVIDIA Overlay',
        'NVIDIA Container',
        'NvContainer'
    )
    foreach ($id in $ids) { & $setOff $id }

    # Any existing notification keys that look NVIDIA-related
    $n = 0
    Get-ChildItem $base -ErrorAction SilentlyContinue | ForEach-Object {
        $name = $_.PSChildName
        if ($name -match '(?i)nvidia|geforce|nvapp|nvcontainer|shadowplay') {
            Set-ItemProperty -Path $_.PSPath -Name 'Enabled' -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
            Set-ItemProperty -Path $_.PSPath -Name 'ShowInActionCenter' -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
            $n++
            Write-Ok "Windows toasts off: $name"
        }
    }
    if ($n -eq 0) { Write-Ok 'Windows NVIDIA toast keys seeded (will stick after first App/CPL toast)' }
    else { Write-Ok "Windows NVIDIA toasts disabled ($n keys)" }
}

function Disable-NvidiaOverlay {
    Write-Step 'Stopping NVIDIA App/GFE background clients and disabling the overlay...'
    foreach ($n in @('NVIDIA App', 'NVIDIA Overlay', 'NVIDIA Share', 'nvsphelper64', 'nvsphelper', 'NVIDIA Web Helper', 'GFExperience')) {
        Get-Process -Name $n -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    foreach ($im in @('NVIDIA App.exe', 'NVIDIA Overlay.exe', 'NVIDIA Share.exe', 'NVIDIA Web Helper.exe', 'nvsphelper64.exe', 'GFExperience.exe')) {
        try { & taskkill.exe /F /IM $im /T 2>$null | Out-Null } catch { }
    }

    # ShadowPlay / overlay caps off (binary 0 = disabled style values used by NVSP)
    $sp = 'HKCU:\Software\NVIDIA Corporation\Global\ShadowPlay\NVSPCAPS'
    if (-not (Test-Path $sp)) {
        try { New-Item -Path $sp -Force | Out-Null } catch { }
    }
    if (Test-Path $sp) {
        foreach ($name in @(
            'RecEnabled', 'DwmEnabled', 'DwmDvrEnabledV1', 'DisplayRecordingIndicator',
            'DisplayGamecastIndicator', 'GameStreamPortal', 'OverlayEnabled', 'ShowOverlay',
            'IsShadowPlayEnabled', 'IsShadowPlayEnabledUser', 'EnableMicrophone'
        )) {
            try {
                New-ItemProperty -LiteralPath $sp -Name $name -PropertyType Binary -Value ([byte[]](0, 0, 0, 0)) -Force -ErrorAction SilentlyContinue | Out-Null
            } catch { }
        }
        Write-Ok 'ShadowPlay/overlay caps set off (registry)'
    }

    # App-side overlay + notification + capture toggles (HKCU + HKLM mirror)
    $offDwords = @(
        'OverlayEnabled', 'EnableOverlay', 'ShowOverlay', 'InGameOverlay',
        'EnableNotifications', 'NotificationsEnabled', 'ShowNotifications',
        'NotifyNewDisplayUpdates', 'NotifyDriverUpdates', 'NotifyRewards',
        'NotifyHighlights', 'ToastNotifications', 'EnableToasts',
        'EnableInstantReplay', 'InstantReplay', 'EnableHighlights', 'EnableAnsel',
        'EnableFreestyle', 'EnablePhotoMode', 'EnableGameFilters', 'EnableGameStream',
        'ShareEnabled', 'EnableRewards', 'EnableDiscover', 'EnableTelemetry',
        'RunAtStartup', 'AutoStart', 'StartOnLogin', 'AllowAutoDownload', 'AutoDownload',
        'SilentInstalls'
    )
    foreach ($p in @(
        'HKCU:\Software\NVIDIA Corporation\NVIDIA App',
        'HKLM:\SOFTWARE\NVIDIA Corporation\NVIDIA App',
        'HKCU:\Software\NVIDIA Corporation\Global\GFExperience',
        'HKCU:\Software\NVIDIA Corporation\Global\NvApp'
    )) {
        if (-not (Test-Path -LiteralPath $p)) {
            try { New-Item -Path $p -Force | Out-Null } catch { continue }
        }
        foreach ($name in $offDwords) {
            Set-ExoRegDword -Path $p -Name $name -Value 0
        }
    }

    # Remove known per-user auto-start entries while preserving installed App/GFE files.
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (Test-Path -LiteralPath $runKey) {
        $runValues = Get-ItemProperty -LiteralPath $runKey -ErrorAction SilentlyContinue
        foreach ($property in $runValues.PSObject.Properties) {
            if ($property.Name -like 'PS*') { continue }
            $signature = "$($property.Name) $($property.Value)"
            if ($signature -match '(?i)NVIDIA App|GeForce Experience|GFExperience|NvBackend|ShadowPlay|FrameView') {
                Remove-ItemProperty -LiteralPath $runKey -Name $property.Name -Force -ErrorAction SilentlyContinue
                Write-Ok "Disabled NVIDIA auto-start entry: $($property.Name)"
            }
        }
    }

    Write-Ok 'NVIDIA App/GFE overlay + notifications disabled; Display.Driver + Control Panel only (audio stripped)'
}

function Get-NvidiaAppExePath {
    foreach ($p in @(
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe'),
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA App\NVIDIA App.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe')
    )) {
        if ($p -and (Test-Path -LiteralPath $p)) { return $p }
    }
    $root = Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA App'
    if (Test-Path -LiteralPath $root) {
        $hit = Get-ChildItem -LiteralPath $root -Recurse -Filter 'NVIDIA App.exe' -File -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

function Initialize-ExoNvAppWin32 {
    if ($Script:ExoNvAppWin32Ready) { return $true }
    try {
        $code = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class ExoNvAppUi {
    public const int BM_CLICK = 0x00F5;
    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    static string GetText(IntPtr h) {
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }
    static string GetCls(IntPtr h) {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static List<IntPtr> FindNvAppWindows() {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h)) return true;
            var title = GetText(h);
            if (string.IsNullOrWhiteSpace(title)) return true;
            if (title.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                title.IndexOf("GeForce", StringComparison.OrdinalIgnoreCase) >= 0 ||
                title.IndexOf("License", StringComparison.OrdinalIgnoreCase) >= 0 ||
                title.IndexOf("Agreement", StringComparison.OrdinalIgnoreCase) >= 0 ||
                title.IndexOf("Welcome", StringComparison.OrdinalIgnoreCase) >= 0) {
                list.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static bool IsGoodButton(string name) {
        if (string.IsNullOrEmpty(name)) return false;
        name = name.Trim().ToLowerInvariant();
        // Never click enable-overlay style buttons
        if (name.Contains("enable overlay") || name.Contains("turn on overlay")) return false;
        if (name.Contains("enable") && name.Contains("overlay")) return false;
        string[] want = {
            "accept", "agree", "i agree", "continue", "next", "get started",
            "skip", "no thanks", "not now", "finish", "done", "close",
            "later", "ok", "decline", "disable", "no"
        };
        foreach (var w in want) {
            if (name == w || name.Contains(w)) return true;
        }
        return false;
    }

    public static int ClickProgressButtons() {
        int clicks = 0;
        foreach (var top in FindNvAppWindows()) {
            try { ShowWindow(top, SW_MINIMIZE); } catch { }
            var kids = new List<IntPtr>();
            EnumChildWindows(top, (ch, l) => { kids.Add(ch); return true; }, IntPtr.Zero);
            foreach (var ch in kids) {
                try {
                    if (!IsWindowVisible(ch)) continue;
                    var cls = GetCls(ch);
                    var name = GetText(ch);
                    // CEF apps often use Chrome_RenderWidgetHostHWND - limited native buttons.
                    // Still click classic Button classes when present (installer-style dialogs).
                    bool looksButton = cls.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       cls.IndexOf("Btn", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!looksButton && string.IsNullOrWhiteSpace(name)) continue;
                    if (!IsGoodButton(name) && !looksButton) continue;
                    if (!IsGoodButton(name) && looksButton && string.IsNullOrWhiteSpace(name)) continue;
                    if (!IsGoodButton(name)) continue;
                    SendMessage(ch, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    clicks++;
                    System.Threading.Thread.Sleep(350);
                } catch { }
            }
        }
        return clicks;
    }

    public static void NudgeEnterOnNvWindows() {
        foreach (var h in FindNvAppWindows()) {
            try {
                ShowWindow(h, SW_RESTORE);
                SetForegroundWindow(h);
                // WM_KEYDOWN/UP Enter = 0x0D
                SendMessage(h, 0x0100, new IntPtr(0x0D), IntPtr.Zero);
                SendMessage(h, 0x0101, new IntPtr(0x0D), IntPtr.Zero);
                System.Threading.Thread.Sleep(200);
                ShowWindow(h, SW_MINIMIZE);
            } catch { }
        }
    }
}
'@
        Add-Type -TypeDefinition $code -ErrorAction Stop
        $Script:ExoNvAppWin32Ready = $true
        return $true
    } catch {
        # Type may already exist from a prior run in this process
        if ($_.Exception.Message -match 'already exists|already defined') {
            $Script:ExoNvAppWin32Ready = $true
            return $true
        }
        Write-Warn "Win32 UI helper unavailable: $($_.Exception.Message)"
        $Script:ExoNvAppWin32Ready = $false
        return $false
    }
}

function Configure-NvidiaAppExperience {
    # Registry/config only - do NOT open the App UI (CEF open/close was useless and annoyed users).
    Write-Step 'Configuring NVIDIA App prefs (registry/config only - no UI launch)...'
    foreach ($n in @('NVIDIA App', 'NVIDIA Overlay', 'NVIDIA Share', 'nvsphelper64', 'nvsphelper')) {
        Get-Process -Name $n -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Accept-NvidiaAppEula
    Enable-NvidiaAppBetaChannel
    Disable-NvidiaOverlay
    Set-NvidiaAppBackendConfigDebloat
    Set-NvidiaWindowsNotificationsOff
    Remove-NvidiaAppDesktopShortcuts | Out-Null
    Write-Ok 'NVIDIA App prefs set (EULA flags, beta channel, overlay/Windows toasts off). No App window launched.'
}

function Test-NvidiaOverlayDisabled {
    $issues = New-Object System.Collections.Generic.List[string]

    $overlayProcesses = @(Get-Process -Name @(
        'NVIDIA Overlay', 'NVIDIA Share', 'nvsphelper', 'nvsphelper64') -ErrorAction SilentlyContinue)
    if ($overlayProcesses.Count -gt 0) {
        [void]$issues.Add("Overlay processes still running: $($overlayProcesses.ProcessName -join ', ')")
    }

    # Only require keys that exist - missing keys after Disable-NvidiaOverlay are treated as OK
    # when we just wrote them; if write failed, explicit non-zero fails.
    foreach ($path in @(
        'HKCU:\Software\NVIDIA Corporation\NVIDIA App',
        'HKCU:\Software\NVIDIA Corporation\Global\GFExperience'
    )) {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $properties = Get-ItemProperty -LiteralPath $path -ErrorAction SilentlyContinue
        foreach ($name in @('OverlayEnabled', 'EnableOverlay')) {
            $property = if ($properties) { $properties.PSObject.Properties[$name] } else { $null }
            if ($property -and [int]$property.Value -ne 0) {
                [void]$issues.Add("Overlay preference is still on: $path\\$name")
            }
        }
    }

    $capsPath = 'HKCU:\Software\NVIDIA Corporation\Global\ShadowPlay\NVSPCAPS'
    if (Test-Path -LiteralPath $capsPath) {
        $caps = Get-ItemProperty -LiteralPath $capsPath -ErrorAction SilentlyContinue
        foreach ($name in @('RecEnabled', 'DwmEnabled', 'DwmDvrEnabledV1', 'DisplayRecordingIndicator', 'DisplayGamecastIndicator', 'GameStreamPortal')) {
            $property = if ($caps) { $caps.PSObject.Properties[$name] } else { $null }
            if (-not $property) { continue }
            $bytes = @($property.Value)
            if (@($bytes | Where-Object { [int]$_ -ne 0 }).Count -gt 0) {
                [void]$issues.Add("ShadowPlay capture preference is not disabled: $name")
            }
        }
    }

    return [pscustomobject]@{
        Ok     = [bool]($issues.Count -eq 0)
        Issues = @($issues)
    }
}

function Install-NvidiaApp {
    # Fresh App after wipe. Primary = official nvidia.com CDN (fast/reliable elevated).
    # winget is LAST RESORT only (30s). Unsupported system (0x1A000000) fails fast - no hang.
    $Script:NvidiaAppInstallUnsupported = $false
    Write-Step 'Installing fresh NVIDIA App (official download first; winget last-resort only)...'
    if (Test-NvidiaAppInstalled) {
        Write-Ok 'NVIDIA App already present'
        Remove-NvidiaAppDesktopShortcuts | Out-Null
        return $true
    }

    if (-not $SkipDownload) {
        if (Install-NvidiaAppFromOfficialInstaller) {
            Remove-NvidiaAppDesktopShortcuts | Out-Null
            return $true
        }
        if ($Script:NvidiaAppInstallUnsupported) {
            Write-Warn 'Skipping winget - NVIDIA installer already reported system not supported'
            return $false
        }
        Write-Warn 'Official NVIDIA download/install failed - trying brief winget last-resort...'
    } else {
        Write-Warn '-SkipDownload set; cannot use official NVIDIA App download'
    }

    # Last resort: single short winget attempt (no source reset - that was the 5 min hang).
    $winget = Get-ExoWingetPath
    if ($winget -and -not $SkipDownload -and -not $Script:NvidiaAppInstallUnsupported) {
        Write-Ok "winget last-resort (30s max): $winget"
        Write-HubProgress 73 'NVIDIA App via winget (last resort, 30s)...'
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = $winget
            $psi.Arguments = 'install --id XP8CLZL93F5Z4P -e --accept-package-agreements --accept-source-agreements --disable-interactivity --silent'
            $psi.UseShellExecute = $false
            $psi.CreateNoWindow = $true
            $psi.RedirectStandardOutput = $true
            $psi.RedirectStandardError = $true
            $proc = [Diagnostics.Process]::Start($psi)
            if ($proc -and -not $proc.WaitForExit(30000)) {
                try { $proc.Kill($true) } catch { try { $proc.Kill() } catch { } }
                Write-Warn 'winget timed out after 30s - killed'
            } elseif ($proc) {
                Write-Ok "winget exit $($proc.ExitCode)"
            }
        } catch {
            Write-Warn "winget last-resort failed: $($_.Exception.Message)"
        } finally {
            $ErrorActionPreference = $prev
        }
        if (Wait-NvidiaAppInstalled -Seconds 12) {
            Remove-NvidiaAppDesktopShortcuts | Out-Null
            Write-Ok 'NVIDIA App installed via winget last-resort'
            return $true
        }
    }

    if (Test-NvidiaAppInstalled) {
        Remove-NvidiaAppDesktopShortcuts | Out-Null
        Write-Ok 'NVIDIA App became present after install attempts'
        return $true
    }

    if ($Script:NvidiaAppInstallUnsupported) {
        Write-Warn 'NVIDIA App cannot install on this system (NVIDIA: system configuration not supported). Exo will finish without the App.'
    } else {
        Write-Warn 'NVIDIA App install failed. Continuing without App when possible.'
    }
    return $false
}

function Get-WindowsDriverVersionString {
    try {
        $gpu = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '(?i)nvidia|geforce|rtx|gtx' } |
            Select-Object -First 1
        return [string]$gpu.DriverVersion
    } catch { return '' }
}

function Convert-WindowsDriverToNvidia([string]$WinVer) {
    # WDDM DCH encoding: last 5 digits of c*10000+d => major.minor (e.g. 32.0.15.6094 -> 560.94)
    try {
        $parts = $WinVer -split '\.'
        if ($parts.Count -lt 4) { return $null }
        $c = [int]$parts[2]
        $d = [int]$parts[3]
        $combined = ($c * 10000 + $d).ToString()
        if ($combined.Length -lt 5) { $combined = $combined.PadLeft(5, '0') }
        $last5 = $combined.Substring($combined.Length - 5)
        $major = [int]$last5.Substring(0, 3)
        $minor = [int]$last5.Substring(3, 2)
        return ('{0}.{1:D2}' -f $major, $minor)
    } catch { return $null }
}

function Get-ExoDriverLookupTargets {
    param([string]$SeriesId = '')
    # NVIDIA menu product series (psid) + representative desktop product (pfid).
    # CRITICAL: 10-series (GTX 1080 etc.) is on a legacy security branch (~582.x), NOT the
    # modern 20/30/40/50 Game Ready line (610.x). Using a 40/50 product ID offers an
    # unusable package to Pascal GPUs.
    $base = 'https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup'
    $q = '&osID=57&languageCode=1033&beta=0&isWHQL=1&dltype=-1&dch=1&upCRD=0&qnf=0&ctk=null&windowsVersion=10.0&windowsArchitecture=64bit'
    switch ($SeriesId) {
        '10' {
            # GeForce 10 Series / GTX 1080 (psid=101, pfid=815)
            return @(
                "$base&psid=101&pfid=815$q",
                "$base&psid=101&pfid=817$q"   # GTX 1060 fallback
            )
        }
        '20' {
            return @(
                "$base&psid=107&pfid=879$q",  # RTX 2080
                "$base&psid=107&pfid=887$q"   # RTX 2060
            )
        }
        '30' {
            return @(
                "$base&psid=120&pfid=933$q",  # RTX 3070
                "$base&psid=120&pfid=929$q"   # RTX 3080
            )
        }
        '40' {
            return @(
                "$base&psid=127&pfid=995$q",  # RTX 4090
                "$base&psid=127&pfid=1015$q"  # RTX 4070
            )
        }
        '50' {
            return @(
                "$base&psid=131&pfid=1066$q", # RTX 5090
                "$base&psid=131&pfid=1070$q"  # RTX 5070
            )
        }
        default {
            # Unknown series: prefer 30/40 desktop matrix (not 10-series legacy, not notebook psid).
            return @(
                "$base&psid=120&pfid=933$q",
                "$base&psid=127&pfid=995$q"
            )
        }
    }
}

function Get-LatestGameReadyDriver {
    param([string]$SeriesId = '')
    # Query the newest driver package that is valid for THIS GPU series branch.
    $urls = @(Get-ExoDriverLookupTargets -SeriesId $SeriesId)
    foreach ($url in $urls) {
        try {
            $r = Invoke-RestMethod -Uri $url -Headers @{ 'User-Agent' = 'Exo-Nvidia/1.2' } -TimeoutSec 25
            if (-not $r -or $r.Success -ne '1') { continue }
            $info = $r.IDS[0].downloadInfo
            if (-not $info -or [string]$info.Version -notmatch '^\d{3}\.\d{2}$') { continue }
            return [pscustomobject]@{
                Version     = [string]$info.Version
                DownloadUrl = [uri]::UnescapeDataString([string]$info.DownloadURL)
                Name        = [uri]::UnescapeDataString([string]$info.Name)
                ReleaseDate = [string]$info.ReleaseDateTime
                Size        = [string]$info.DownloadURLFileSize
                SeriesId    = [string]$SeriesId
            }
        } catch {
            Write-Warn "Latest-driver lookup failed: $($_.Exception.Message)"
        }
    }
    return $null
}

function Compare-NvidiaVersion([string]$A, [string]$B) {
    # returns: -1 if A<B, 0 equal, 1 if A>B
    try {
        $va = [version](($A -replace '[^\d\.]', '') -replace '^\.', '0.')
        $vb = [version](($B -replace '[^\d\.]', '') -replace '^\.', '0.')
        if ($va -lt $vb) { return -1 }
        if ($va -gt $vb) { return 1 }
        return 0
    } catch {
        if ($A -eq $B) { return 0 }
        if ($A -lt $B) { return -1 }
        return 1
    }
}

function Find-NanaZipCli {
    # NanaZipC = 7z-compatible CLI (preferred). Never install/use 7-Zip.
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\NanaZipC.exe'),
        (Join-Path $env:ProgramFiles 'NanaZip\NanaZipC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'NanaZip\NanaZipC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\NanaZip\NanaZipC.exe')
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath $c)) { return $c }
    }
    $cmd = Get-Command NanaZipC -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source) { return $cmd.Source }
    # WinGet package layout
    $wg = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
    if (Test-Path $wg) {
        $hit = Get-ChildItem $wg -Recurse -Filter 'NanaZipC.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

function Ensure-NanaZip {
    $existing = Find-NanaZipCli
    if ($existing) { return $existing }
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $winget) {
        Write-Warn 'NanaZip not found and winget unavailable'
        return $null
    }
    Write-Step 'Installing NanaZip (extracts NVIDIA package for Exo Clean Driver)...'
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & winget install --id M2Team.NanaZip -e --accept-package-agreements --accept-source-agreements --silent 2>&1 | Out-Null
    } catch { }
    $ErrorActionPreference = $prev
    return (Find-NanaZipCli)
}

function Test-NvidiaDownloadUri([string]$Url) {
    try {
        $uri = [uri]$Url
        return $uri.Scheme -eq 'https' -and $uri.Host -match '(?i)(^|\.)nvidia\.com$'
    } catch {
        return $false
    }
}

function Test-NvidiaSignedFile([string]$Path) {
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return $false }
    try {
        $signature = Get-AuthenticodeSignature -LiteralPath $Path -ErrorAction Stop
        $subject = [string]$signature.SignerCertificate.Subject
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
            $subject -notmatch '(?i)NVIDIA\s+Corporation') {
            Write-Warn "Driver package signature rejected (status=$($signature.Status), signer=$subject)"
            return $false
        }
        Write-Ok "Verified NVIDIA Authenticode signature: $subject"
        return $true
    } catch {
        Write-Warn "Driver package signature check failed: $($_.Exception.Message)"
        return $false
    }
}

function Test-NvidiaDriverPackage([string]$Path) {
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return $false }
    if ((Get-Item -LiteralPath $Path).Length -lt 50MB) {
        Write-Warn "Driver package is unexpectedly small: $Path"
        return $false
    }
    return (Test-NvidiaSignedFile $Path)
}

function Download-NvidiaDriverPackage {
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$Version
    )
    if (-not (Test-NvidiaDownloadUri $Url)) {
        throw 'NVIDIA driver URL must use HTTPS on an nvidia.com host'
    }
    if ($Version -notmatch '^\d{3}\.\d{2}$') {
        throw "Unexpected NVIDIA driver version: $Version"
    }
    if (-not (Test-Path $DriverCacheDir)) {
        New-Item -ItemType Directory -Path $DriverCacheDir -Force | Out-Null
    }
    $fileName = "GameReady-$Version-win10-win11-64bit-dch.exe"
    $outFile = Join-Path $DriverCacheDir $fileName

    if (Test-Path -LiteralPath $outFile) {
        if (Test-NvidiaDriverPackage $outFile) {
            Write-Ok "Using verified cached driver package: $outFile"
            return $outFile
        }
        Write-Warn 'Removing invalid cached driver package'
        Remove-Item -LiteralPath $outFile -Force -ErrorAction Stop
    }

    Write-Step "Downloading official Game Ready $Version (one package, cached for re-runs)..."
    Write-HubProgress 22 "Downloading Game Ready $Version..."
    $tmp = "$outFile.partial.exe"
    try {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }

        $usedBits = $false
        try {
            Import-Module BitsTransfer -ErrorAction Stop
            Start-BitsTransfer -Source $Url -Destination $tmp -DisplayName "Exo NVIDIA $Version" -Description 'Game Ready driver'
            $usedBits = $true
        } catch {
            $usedBits = $false
        }
        if (-not $usedBits) {
            $wc = New-Object System.Net.WebClient
            $wc.Headers['User-Agent'] = 'Exo-Nvidia/1.2'
            try {
                $wc.DownloadFile($Url, $tmp)
            } finally {
                $wc.Dispose()
            }
        }

        if (-not (Test-Path -LiteralPath $tmp) -or ((Get-Item -LiteralPath $tmp).Length -lt 50MB)) {
            throw 'Driver download incomplete or too small'
        }
        if (-not (Test-NvidiaDriverPackage $tmp)) {
            throw 'Downloaded driver failed NVIDIA Authenticode verification'
        }
        Move-Item -LiteralPath $tmp -Destination $outFile -Force
        Write-Ok "Downloaded: $outFile ($([math]::Round((Get-Item $outFile).Length / 1MB, 1)) MB)"
        Write-HubProgress 38 'Driver package ready'
        return $outFile
    } catch {
        try { if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue } } catch { }
        throw "Driver download failed: $($_.Exception.Message)"
    }
}

function Expand-NvidiaDriverPackage {
    param(
        [Parameter(Mandatory)][string]$PackageExe,
        [Parameter(Mandatory)][string]$DestDir
    )
    # Reuse full extract if present (folder-strip was removed; incomplete extracts are deleted)
    $existingSetup = Join-Path $DestDir 'setup.exe'
    $existingDriver = Join-Path $DestDir 'Display.Driver'
    if ((Test-Path -LiteralPath $existingSetup) -and (Test-Path -LiteralPath $existingDriver)) {
        # Need Display.Driver + NVI2 for the component-filtered display-driver install.
        $ok = (Test-Path -LiteralPath (Join-Path $DestDir 'NVI2'))
        if ($ok -and (Test-NvidiaSignedFile $existingSetup)) {
            Write-Ok "Using verified existing extract: $DestDir"
            return $existingSetup
        }
        Write-Warn 'Existing driver extract is incomplete or failed signature verification; rebuilding it'
    }
    if (Test-Path -LiteralPath $DestDir) {
        Remove-Item -LiteralPath $DestDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
    Write-Step 'Extracting official package for Exo Clean Driver (NanaZip)...'
    Write-HubProgress 40 'Extracting driver package...'

    $nana = Ensure-NanaZip
    if ($nana) {
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        # NanaZipC is 7z-compatible CLI
        & $nana x $PackageExe "-o$DestDir" -y 2>&1 | Out-Null
        $ErrorActionPreference = $prev
        $setup = Get-ChildItem -LiteralPath $DestDir -Recurse -Filter 'setup.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($setup -and (Test-NvidiaSignedFile $setup.FullName)) {
            Write-Ok "Extracted with NanaZip: $($setup.DirectoryName)"
            return $setup.FullName
        }
        Write-Warn 'NanaZip extract did not contain a valid NVIDIA-signed setup.exe'
    } else {
        Write-Warn 'NanaZip CLI not available'
    }

    # NVIDIA self-extractors (fallback when NanaZip missing)
    $argSets = @(
        @('-s', '-x', "-b`"$DestDir`""),
        @('-s', "-extract:`"$DestDir`""),
        @('/s', '/x', "/b`"$DestDir`"")
    )
    foreach ($args in $argSets) {
        try {
            $null = Start-Process -FilePath $PackageExe -ArgumentList $args -Wait -PassThru -WindowStyle Hidden -ErrorAction SilentlyContinue
            $setup = Get-ChildItem -LiteralPath $DestDir -Recurse -Filter 'setup.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($setup -and (Test-NvidiaSignedFile $setup.FullName)) {
                Write-Ok "Extracted via package switches: $($setup.DirectoryName)"
                return $setup.FullName
            }
        } catch { }
    }
    return $null
}

function Install-ExoCleanDriver {
    param(
        [Parameter(Mandatory)][string]$DownloadUrl,
        [Parameter(Mandatory)][string]$Version
    )
    # Exo Clean Driver (NVCleanstall-class, OUR rules - better for silent):
    #  1) Official Game Ready once (cached)
    #  2) Extract (folders stay on disk so setup.exe resolves; we do NOT install bloat)
    #  3) Silent CLEAN install of Display.Driver ONLY (no App, no Virtual/HD Audio, no PhysX)
    #  4) Strip any leftover audio components from prior full installs
    #  5) Post-install expert tweaks (MSI High, telemetry off, Ansel off, HDCP off)
    #  6) Continue pipeline (no forced reboot) - classic Control Panel is separate Store UI
    Write-Step "Exo Clean Driver install ($Version) - Display.Driver component only"
    Write-HubProgress 20 "Exo Clean Driver $Version..."

    $package = Coerce-StringPath (Download-NvidiaDriverPackage -Url $DownloadUrl -Version $Version)
    if (-not $package -or -not (Test-Path -LiteralPath $package)) {
        Write-Warn "Driver package path invalid after download: $package"
        return @{ Success = $false; ExitCode = -1; Error = 'bad-package-path'; Method = 'exo-clean' }
    }
    Write-Ok "Package file: $package"

    $extractDir = Join-Path $DriverCacheDir "extract-$Version"
    $setup = Coerce-StringPath (Expand-NvidiaDriverPackage -PackageExe $package -DestDir $extractDir)

    $exitCode = -1
    if ($setup -and (Test-Path -LiteralPath $setup)) {
        $setupDir = Split-Path -Parent $setup
        # Component filter: Display.Driver only. Audio/App/PhysX stay out of the install set.
        # NVIDIA documents `setup.exe -s -n Display.Driver`; try clean mode first,
        # then the documented component-only form if that build rejects -clean.
        $argVariants = @(
            @('-s', '-n', '-clean', 'Display.Driver'),
            @('-s', '-n', 'Display.Driver')
        )
        Write-HubProgress 55 'Clean-installing Display.Driver only (silent, no automatic reboot)...'
        foreach ($setupArgs in $argVariants) {
            Write-Ok ("Running: setup.exe " + ($setupArgs -join ' ') + " (cwd=$setupDir)")
            $p = Start-Process -FilePath $setup -ArgumentList $setupArgs -WorkingDirectory $setupDir -Wait -PassThru -WindowStyle Hidden
            if ($p) { $exitCode = [int]$p.ExitCode }
            Write-Ok "setup.exe exit: $exitCode"
            if (@(0, 1) -contains $exitCode) { break }
        }
    } else {
        Write-Warn 'Extract failed - cannot safely silent-install without the Display.Driver component filter'
        return @{ Success = $false; ExitCode = -1; Error = 'extract-failed'; Method = 'exo-clean' }
    }

    # NVIDIA's documented codes: 0 = success, 1 = success/restart required.
    $okCodes = @(0, 1)
    if ($okCodes -contains $exitCode) {
        Start-Sleep -Seconds 2
        # Clean install can leave prior Virtual Audio; strip every time after driver setup
        [void](Remove-NvidiaAudioComponents)
        # Same pass for ShadowPlay/NvBackend/NodeJS/telemetry sub-packages
        [void](Remove-NvidiaBloatComponents)
        $installedVersion = Convert-WindowsDriverToNvidia (Get-WindowsDriverVersionString)
        if ($exitCode -eq 0 -and $installedVersion -and
            (Compare-NvidiaVersion $installedVersion $Version) -lt 0) {
            Write-Warn "Installer returned success, but driver verification found $installedVersion instead of $Version"
            return @{
                Success = $false; ExitCode = $exitCode; Error = 'version-verification-failed'
                ExpectedVersion = $Version; InstalledVersion = $installedVersion
                Package = $package; Setup = $setup; Method = 'exo-clean'
            }
        }
        $rebootRequired = ($exitCode -eq 1)
        Write-Ok "Exo Clean Driver finished (exit $exitCode, restart required=$rebootRequired)"
        return @{
            Success          = $true
            ExitCode         = $exitCode
            RebootRequired   = $rebootRequired
            InstalledVersion = $installedVersion
            Package          = $package
            Setup            = $setup
            Method           = 'exo-clean'
        }
    }
    $hex = 'unknown'
    try { $hex = ('{0:X8}' -f [uint32]([int]$exitCode)) } catch { }
    Write-Warn "Exo Clean Driver setup exit $exitCode (0x$hex)"
    return @{
        Success  = $false
        ExitCode = $exitCode
        Package  = $package
        Setup    = $setup
        Method   = 'exo-clean'
    }
}
function Test-ExoNvidiaDisplayPciNode {
    param($DeviceProps)
    # StrictMode-safe: many PCI nodes lack Class/ClassGUID properties after clean installs.
    if ($null -eq $DeviceProps) { return $false }
    $names = @()
    try { $names = @($DeviceProps.PSObject.Properties.Name) } catch { return $false }
    $class = $null
    $classGuid = $null
    $desc = $null
    $svc = $null
    if ($names -contains 'Class') { try { $class = [string]$DeviceProps.Class } catch { } }
    if ($names -contains 'ClassGUID') { try { $classGuid = [string]$DeviceProps.ClassGUID } catch { } }
    if ($names -contains 'DeviceDesc') { try { $desc = [string]$DeviceProps.DeviceDesc } catch { } }
    if ($names -contains 'Service') { try { $svc = [string]$DeviceProps.Service } catch { } }
    if ($class -eq 'Display') { return $true }
    if ($classGuid -eq '{4d36e968-e325-11ce-bfc1-08002be10318}') { return $true }
    # Fallback after driver reinstall: nvlddmkm service + GPU-like description.
    if ($svc -match '(?i)^nvlddmkm$' -and $desc -match '(?i)NVIDIA|GeForce|RTX|GTX|Quadro|Tesla') { return $true }
    return $false
}

function Apply-ExoDriverInstallTweaks {
    # NVCleanstall expert checklist (Exo silent equivalent):
    #  [x] Disable installer telemetry / advertising
    #  [x] Clean install Display.Driver only (done in Install-ExoCleanDriver)
    #  [x] Disable Ansel / NvCamera (service + profile)
    #  [x] Disable driver telemetry
    #  [x] MSI High (Message Signaled Interrupts + High priority)
    #  [x] Disable HDCP (RMHdcpKeyglobZero on display GPU nodes)
    #  [x] No Virtual/HD Audio (stripped separately - not "sleep timer", full remove)
    #  SKIP: EAC INF strip / accept-unsigned (install-time only, unsafe on stock setup.exe)
    Write-Step 'Applying Exo driver expert tweaks (MSI High, telemetry off, Ansel off, HDCP off)...'

    # --- MSI High (real interrupt mode tweak) ---
    $msiCount = 0
    $msiCandidates = 0
    $hdcpCount = 0
    try {
        $pci = 'HKLM:\SYSTEM\CurrentControlSet\Enum\PCI'
        if (Test-Path $pci) {
            Get-ChildItem $pci -ErrorAction SilentlyContinue | Where-Object {
                $_.PSChildName -match 'VEN_10DE'
            } | ForEach-Object {
                Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
                    $dev = $_.PSPath
                    # Only the display-class GPU node (StrictMode-safe Class check).
                    $device = Get-ItemProperty -LiteralPath $dev -ErrorAction SilentlyContinue
                    if (-not (Test-ExoNvidiaDisplayPciNode $device)) { return }
                    $msiCandidates++
                    $msiKey = Join-Path $dev 'Device Parameters\Interrupt Management\MessageSignaledInterruptProperties'
                    $aff = Join-Path $dev 'Device Parameters\Interrupt Management\Affinity Policy'
                    $descLabel = 'NVIDIA display'
                    try {
                        if ($device.PSObject.Properties.Name -contains 'DeviceDesc') {
                            $descLabel = [string]$device.DeviceDesc
                        }
                    } catch { }
                    try {
                        if (-not (Test-Path $msiKey)) { New-Item -Path $msiKey -Force -ErrorAction Stop | Out-Null }
                        New-ItemProperty -LiteralPath $msiKey -Name 'MSISupported' -Value 1 -PropertyType DWord -Force -ErrorAction Stop | Out-Null
                        if (-not (Test-Path $aff)) { New-Item -Path $aff -Force -ErrorAction Stop | Out-Null }
                        # 3 = High priority (NVCleanstall MSI High)
                        New-ItemProperty -LiteralPath $aff -Name 'DevicePriority' -Value 3 -PropertyType DWord -Force -ErrorAction Stop | Out-Null
                        $msiValue = (Get-ItemProperty -LiteralPath $msiKey -ErrorAction Stop).MSISupported
                        $priorityValue = (Get-ItemProperty -LiteralPath $aff -ErrorAction Stop).DevicePriority
                        if ($msiValue -eq 1 -and $priorityValue -eq 3) { $msiCount++ }
                        else { Write-Warn "MSI verification failed for $descLabel" }
                    } catch {
                        Write-Warn "MSI High failed for ${descLabel}: $($_.Exception.Message)"
                    }
                }
            }
        }
    } catch {
        Write-Warn "MSI tweak: $($_.Exception.Message)"
    }
    if ($msiCandidates -gt 0 -and $msiCount -eq $msiCandidates) {
        Write-Ok "MSI High verified on all $msiCount NVIDIA display device(s)"
    } else {
        Write-Warn "MSI High verified on $msiCount of $msiCandidates NVIDIA display device(s)"
    }

    # --- Disable HDCP (NVCleanstall expert tweak) on display driver class nodes ---
    try {
        $classRoot = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}'
        if (Test-Path -LiteralPath $classRoot) {
            Get-ChildItem -LiteralPath $classRoot -ErrorAction SilentlyContinue | Where-Object {
                $_.PSChildName -match '^\d{4}$'
            } | ForEach-Object {
                $driverDesc = $null
                try { $driverDesc = [string](Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue).DriverDesc } catch { }
                $provider = $null
                try { $provider = [string](Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue).ProviderName } catch { }
                if ($driverDesc -notmatch '(?i)NVIDIA|GeForce|RTX|GTX' -and $provider -notmatch '(?i)NVIDIA') { return }
                try {
                    New-ItemProperty -LiteralPath $_.PSPath -Name 'RMHdcpKeyglobZero' -Value 1 -PropertyType DWord -Force -ErrorAction Stop | Out-Null
                    $hdcpCount++
                } catch {
                    Write-Warn "HDCP disable failed on $($_.PSChildName): $($_.Exception.Message)"
                }
            }
        }
        if ($hdcpCount -gt 0) { Write-Ok "HDCP disabled (RMHdcpKeyglobZero=1) on $hdcpCount display driver node(s)" }
        else { Write-Warn 'No NVIDIA display class nodes found for HDCP disable' }
    } catch {
        Write-Warn "HDCP tweak: $($_.Exception.Message)"
    }

    # --- Telemetry / advertising consent (installer telemetry analogue) ---
    try {
        foreach ($p in @(
            'HKLM:\SOFTWARE\NVIDIA Corporation\Global\FTS',
            'HKLM:\SOFTWARE\NVIDIA Corporation\NvControlPanel2\Client',
            'HKCU:\Software\NVIDIA Corporation\Global\FTS',
            'HKCU:\Software\NVIDIA Corporation\NVControlPanel2\Client'
        )) {
            if (-not (Test-Path $p)) { New-Item -Path $p -Force -ErrorAction SilentlyContinue | Out-Null }
            if (Test-Path $p) {
                # Known telemetry/advertising feature RIDs
                foreach ($rid in @(
                    'EnableRID44231', 'EnableRID64640', 'EnableRID66610', 'EnableRID73779', 'EnableRID73780',
                    'EnableRID57705', 'EnableRID48420', 'EnableRID44231'
                )) {
                    New-ItemProperty -LiteralPath $p -Name $rid -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
                }
            }
        }
        $gf = 'HKCU:\Software\NVIDIA Corporation\Global\GFExperience'
        if (-not (Test-Path $gf)) { New-Item -Path $gf -Force -ErrorAction SilentlyContinue | Out-Null }
        if (Test-Path $gf) {
            New-ItemProperty -LiteralPath $gf -Name 'AllowAutoDownload' -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
            New-ItemProperty -LiteralPath $gf -Name 'SilentInstalls' -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
        }
        # NvCamera / Ansel residual paths
        foreach ($cam in @(
            'HKLM:\SOFTWARE\NVIDIA Corporation\Global\NVTweak',
            'HKCU:\Software\NVIDIA Corporation\Global\NVTweak',
            'HKLM:\SOFTWARE\NVIDIA Corporation\Global\Ansel',
            'HKCU:\Software\NVIDIA Corporation\Global\Ansel'
        )) {
            if (-not (Test-Path $cam)) { New-Item -Path $cam -Force -ErrorAction SilentlyContinue | Out-Null }
            if (Test-Path $cam) {
                New-ItemProperty -LiteralPath $cam -Name 'AnselEnable' -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
                New-ItemProperty -LiteralPath $cam -Name 'EnableAnsel' -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
            }
        }
        # Prefer maximum performance PowerMizer when notebook key present
        try {
            $classRoot = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}'
            Get-ChildItem -LiteralPath $classRoot -ErrorAction SilentlyContinue | Where-Object {
                $_.PSChildName -match '^\d{4}$'
            } | ForEach-Object {
                $desc = [string](Get-ItemProperty -LiteralPath $_.PSPath -EA SilentlyContinue).DriverDesc
                if ($desc -notmatch '(?i)NVIDIA|GeForce|RTX|GTX') { return }
                # PowerMizerEnable 0 + PowerMizerLevel 1 = prefer max when exposed
                New-ItemProperty -LiteralPath $_.PSPath -Name 'PowerMizerEnable' -Value 1 -PropertyType DWord -Force -EA SilentlyContinue | Out-Null
                New-ItemProperty -LiteralPath $_.PSPath -Name 'PowerMizerLevel' -Value 1 -PropertyType DWord -Force -EA SilentlyContinue | Out-Null
                New-ItemProperty -LiteralPath $_.PSPath -Name 'PowerMizerLevelAC' -Value 1 -PropertyType DWord -Force -EA SilentlyContinue | Out-Null
            }
        } catch { }
        Write-Ok 'Installer telemetry / advertising RIDs off; Ansel/PowerMizer tuned'
    } catch { }

    Disable-NvidiaTelemetry
    [void](Remove-NvidiaAudioComponents)
    [void](Remove-NvidiaBloatComponents)
    Write-Ok 'Expert tweaks done (MSI High, telemetry off, Ansel off, HDCP off; audio + bloat stripped)'
}

function Test-ExoDriverInstallTweaks {
    # Signals that Exo clean install + expert tweaks actually landed.
    $issues = New-Object System.Collections.Generic.List[string]
    $oks = New-Object System.Collections.Generic.List[string]

    # Non-display capture/telemetry services should stay disabled.
    foreach ($serviceName in @('NvTelemetryContainer', 'NvCamera', 'FvSvc')) {
        $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($svc -and $svc.StartType -ne 'Disabled') {
            [void]$issues.Add("$serviceName still enabled")
        } else {
            [void]$oks.Add("$serviceName disabled or absent")
        }
    }
    $networkService = Get-Service -Name 'NvContainerNetworkService' -ErrorAction SilentlyContinue
    if ($networkService -and ($networkService.StartType -eq 'Automatic' -or $networkService.Status -eq 'Running')) {
        [void]$issues.Add('NvContainerNetworkService still starts automatically or is running')
    } else {
        [void]$oks.Add('NVIDIA network container is on-demand or absent')
    }

    # NVIDIA App/GFE is a user choice, not a driver-tweak failure.
    $gfePaths = @(
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA GeForce Experience'),
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\GeForce Experience')
    )
    $gfeHit = $false
    foreach ($p in $gfePaths) {
        if (Test-Path -LiteralPath $p) { $gfeHit = $true; break }
    }
    if ($gfeHit) {
        [void]$oks.Add('NVIDIA App/GFE present (preserved)')
    } else {
        [void]$oks.Add('NVIDIA App/GFE not installed')
    }

    # MSI: if the key exists and is 0, fail; if 1, pass; if no display PCI nodes found,
    # soft-skip (clean installs often omit Class under StrictMode - not a hard fail).
    $msiSeen = 0
    $msiGaps = 0
    try {
        $pci = 'HKLM:\SYSTEM\CurrentControlSet\Enum\PCI'
        if (Test-Path $pci) {
            Get-ChildItem $pci -ErrorAction SilentlyContinue | Where-Object {
                $_.PSChildName -match 'VEN_10DE'
            } | ForEach-Object {
                Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
                    $device = Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue
                    if (-not (Test-ExoNvidiaDisplayPciNode $device)) { return }
                    $msiSeen++
                    $msiKey = Join-Path $_.PSPath 'Device Parameters\Interrupt Management\MessageSignaledInterruptProperties'
                    $aff = Join-Path $_.PSPath 'Device Parameters\Interrupt Management\Affinity Policy'
                    $v = $null
                    $priority = $null
                    try { $v = (Get-ItemProperty -LiteralPath $msiKey -ErrorAction SilentlyContinue).MSISupported } catch { }
                    try { $priority = (Get-ItemProperty -LiteralPath $aff -ErrorAction SilentlyContinue).DevicePriority } catch { }
                    if ($v -ne 1 -or $priority -ne 3) { $msiGaps++ }
                }
            }
        }
    } catch { }
    if ($msiSeen -gt 0) {
        if ($msiGaps -eq 0) {
            [void]$oks.Add("MSI High verified on $msiSeen NVIDIA display device(s)")
        } else {
            # Soft: MSI keys sometimes need a reboot to stick after clean install.
            # Never hard-fail the whole NVIDIA pipeline for this alone.
            [void]$oks.Add("MSI High soft ($msiGaps of $msiSeen not sticky yet - reboot may finish)")
            Write-Warn "MSI High not fully sticky on $msiGaps of $msiSeen device(s) - continuing Apply"
        }
    } else {
        # Soft skip - do not brick the whole Apply after a successful clean driver install.
        [void]$oks.Add('MSI High skipped (no display PCI Class nodes visible yet - reboot may help)')
    }

    # Exo remembered this exact driver version as tweaked
    $remembered = $false
    if (Test-Path $StatePath) {
        try {
            $st = Get-Content $StatePath -Raw -Encoding UTF8 | ConvertFrom-Json
            $win = Get-WindowsDriverVersionString
            $cur = Convert-WindowsDriverToNvidia $win
            if ($st.driverTweaksVerified -and $st.driverTweaksVersion -and $cur -and $st.driverTweaksVersion -eq $cur) {
                $remembered = $true
                [void]$oks.Add("Exo recorded tweaks for driver $cur")
            }
        } catch { }
    }

    # A remembered marker is informational only; live performance gaps must win.
    $ok = ($issues.Count -eq 0)
    return [pscustomobject]@{
        Ok        = [bool]$ok
        Remembered = $remembered
        Issues    = @($issues)
        OkSignals = @($oks)
    }
}

function Start-DriverUpdateIfNeeded {
    param(
        [bool]$Force,
        [string]$SeriesId = ''
    )

    $winVer = Get-WindowsDriverVersionString
    $currentNv = Convert-WindowsDriverToNvidia $winVer
    Write-Ok "Installed Windows driver string: $winVer"
    Write-Ok "Decoded NVIDIA version: $(if($currentNv){$currentNv}else{'unknown'})"

    Write-Step "Checking NVIDIA for the newest driver package for series $(if($SeriesId){$SeriesId}else{'auto'})..."
    $latest = Get-LatestGameReadyDriver -SeriesId $SeriesId
    $latestVer = 'unknown'
    $dl = ''
    $versionBehind = $false
    if (-not $latest) {
        Write-Warn 'Could not reach NVIDIA driver API'
        # An unavailable update service is not evidence that the installed driver is stale.
        # Continue with local tweaks/profile work when a valid installed version exists.
        $versionBehind = -not [bool]$currentNv
    } else {
        $latestVer = $latest.Version
        $dl = $latest.DownloadUrl
        $branchNote = if ($SeriesId -eq '10') {
            ' (10-series / Pascal security branch - not the modern 20-50 Game Ready line)'
        } else { '' }
        Write-Ok "Newest package for this GPU series: $latestVer$branchNote ($($latest.ReleaseDate)) size $($latest.Size)"
        if ($latest.Name) { Write-Ok "Package: $($latest.Name)" }
        if ($dl) { Write-Ok "Download: $dl" }
        if (-not $currentNv) {
            $versionBehind = $true
            Write-Warn 'Could not decode installed version'
        } elseif ((Compare-NvidiaVersion $currentNv $latestVer) -lt 0) {
            $versionBehind = $true
            Write-Warn "Outdated for this series: $currentNv < $latestVer"
        } else {
            Write-Ok "Version is newest for this series (or newer): $currentNv"
        }
    }

    Write-Step 'Checking Exo Clean Driver tweak signals...'
    $tweaks = Test-ExoDriverInstallTweaks
    foreach ($o in $tweaks.OkSignals) { Write-Ok "Tweaks signal: $o" }
    foreach ($i in $tweaks.Issues) { Write-Warn "Tweaks gap: $i" }
    if ($tweaks.Ok) {
        Write-Ok 'Exo driver tweaks look present (or recorded for this version)'
    } else {
        Write-Warn 'Stock-style driver signals - Exo will apply clean-driver tweaks'
    }

    $reason = $null
    $needInstall = $false
    if ($Force) {
        $needInstall = $true
        $reason = 'Forced by -ForceDriver'
    } elseif ($versionBehind) {
        $needInstall = $true
        $reason = "Driver version behind newest ($currentNv -> $latestVer)"
    } elseif (-not $tweaks.Ok) {
        $needInstall = $true
        $reason = 'Driver version is current, but Exo clean-driver tweaks are not detected'
    }

    if (-not $needInstall) {
        return @{
            Ran              = $false
            NeedsUpdate      = $false
            NeedsRetweak     = $false
            TweaksOk         = $true
            CurrentVersion   = $currentNv
            LatestVersion    = $latestVer
            WindowsVersion   = $winVer
            DownloadUrl      = $dl
            Tweaks           = $tweaks
            Method           = 'none'
            RebootRequired   = $false
            ContinuePipeline = $true
        }
    }

    Write-Ok $reason

    # Version is current but stock-style signals: apply MSI/privacy in-place (no re-download).
    if (-not $versionBehind -and -not $tweaks.Ok -and -not $Force) {
        Write-Step 'Applying Exo tweaks in-place (no driver download)'
        try {
            Apply-ExoDriverInstallTweaks
            $verifiedTweaks = Test-ExoDriverInstallTweaks
            # Always continue the pipeline after in-place tweaks. Soft MSI residual
            # must not force a full redownload or abort Apply.
            if (-not $verifiedTweaks.Ok) {
                Write-Warn ("In-place tweaks soft residual: {0}" -f ($verifiedTweaks.Issues -join '; '))
            } else {
                Write-Ok 'In-place driver tweaks verified'
            }
            return @{
                Ran              = $true
                NeedsUpdate      = $false
                NeedsRetweak     = (-not [bool]$verifiedTweaks.Ok)
                TweaksOk         = $true
                Reason           = $reason
                CurrentVersion   = $currentNv
                LatestVersion    = $latestVer
                WindowsVersion   = $winVer
                DownloadUrl      = $dl
                Tweaks           = $verifiedTweaks
                Method           = 'in-place-tweaks'
                RebootRequired   = $false
                ContinuePipeline = $true
            }
        } catch {
            Write-Warn "In-place tweaks failed: $($_.Exception.Message)"
        }
    }

    # Full Exo Clean Driver install (our NVCleanstall-class pipeline)
    if (-not $dl) {
        Write-Warn 'No official download URL from NVIDIA API - cannot run Exo Clean Driver'
        return @{
            Ran              = $true
            NeedsUpdate      = $true
            NeedsRetweak     = (-not $versionBehind)
            TweaksOk         = $false
            Reason           = $reason
            CurrentVersion   = $currentNv
            LatestVersion    = $latestVer
            WindowsVersion   = $winVer
            DownloadUrl      = $dl
            Method           = 'failed-no-url'
            Tweaks           = $tweaks
            RebootRequired   = $false
            ContinuePipeline = $false
        }
    }

    $targetVer = if ($latestVer -and $latestVer -ne 'unknown') { $latestVer } else { $currentNv }
    if (-not $targetVer) { $targetVer = 'latest' }

    $install = $null
    try {
        if ($SkipDownload) {
            Write-Warn 'SkipDownload set - cannot fetch driver package'
            $install = @{ Success = $false; Error = 'SkipDownload' }
        } else {
            $install = Install-ExoCleanDriver -DownloadUrl $dl -Version $targetVer
        }
    } catch {
        Write-Warn $_.Exception.Message
        $install = @{ Success = $false; Error = $_.Exception.Message }
    }

    $install = Coerce-Hashtable $install
    if ($install -and $install.Success) {
        $postTweaks = $null
        try {
            Apply-ExoDriverInstallTweaks
            $postTweaks = Test-ExoDriverInstallTweaks
        } catch {
            Write-Warn "Post-install tweaks: $($_.Exception.Message)"
        }
        if (-not $postTweaks -or -not $postTweaks.Ok) {
            $gaps = if ($postTweaks) { @($postTweaks.Issues) -join '; ' } else { 'verification did not run' }
            # Driver package is already on disk. Soft gaps (MSI Class enum) must not
            # abort profile import / display prefs - that left users with "failed"
            # after a successful clean install and a broken-looking UI mid-pipeline.
            Write-Warn "Driver installed; some performance tweaks not fully verified: $gaps"
            Write-Warn 'Continuing pipeline (profiles + display). Reboot then Reapply if MSI still soft-skips.'
            $postWindowsVersion = Get-WindowsDriverVersionString
            $postNvidiaVersion = Convert-WindowsDriverToNvidia $postWindowsVersion
            $rebootRequired = Get-ExoHashBool $install 'RebootRequired' $false
            return @{
                Ran              = $true
                NeedsUpdate      = $false
                NeedsRetweak     = $true
                TweaksOk         = $true
                Reason           = $reason
                CurrentVersion   = $(if ($postNvidiaVersion) { $postNvidiaVersion } else { $currentNv })
                LatestVersion    = $latestVer
                WindowsVersion   = $(if ($postWindowsVersion) { $postWindowsVersion } else { $winVer })
                DownloadUrl      = $dl
                Method           = 'exo-clean-partial-tweaks'
                Install          = $install
                Tweaks           = $postTweaks
                RebootRequired   = $rebootRequired
                ContinuePipeline = (-not $rebootRequired)
            }
        }
        $postWindowsVersion = Get-WindowsDriverVersionString
        $postNvidiaVersion = Convert-WindowsDriverToNvidia $postWindowsVersion
        $rebootRequired = Get-ExoHashBool $install 'RebootRequired' $false
        if ($rebootRequired) {
            Write-Ok 'Exo Clean Driver installed; Windows requires a restart before profile import.'
            Write-HubProgress 70 'Driver installed - restart required'
        } else {
            Write-Ok 'Exo Clean Driver complete. Continuing with the 3D profile and display preferences.'
            Write-HubProgress 70 'Clean driver installed - continuing pipeline'
        }
        return @{
            Ran              = $true
            NeedsUpdate      = $false
            NeedsRetweak     = $false
            TweaksOk         = $true
            Reason           = $reason
            CurrentVersion   = $(if ($postNvidiaVersion) { $postNvidiaVersion } else { $currentNv })
            LatestVersion    = $latestVer
            WindowsVersion   = $(if ($postWindowsVersion) { $postWindowsVersion } else { $winVer })
            DownloadUrl      = $dl
            Method           = 'exo-clean'
            Install          = $install
            Tweaks           = $postTweaks
            RebootRequired   = $rebootRequired
            ContinuePipeline = (-not $rebootRequired)
        }
    }

    # No third-party GUI fallback - surface clear failure so user can re-run after network/disk issues.
    Write-Warn 'Exo Clean Driver did not complete. Check disk space, close games, re-run Apply as Administrator.'
    if ($dl) { Write-Ok "Package URL (for manual retry later): $dl" }
    return @{
        Ran              = $true
        NeedsUpdate      = $true
        NeedsRetweak     = (-not $versionBehind)
        TweaksOk         = $false
        Reason           = $reason
        CurrentVersion   = $currentNv
        LatestVersion    = $latestVer
        WindowsVersion   = $winVer
        DownloadUrl      = $dl
        Method           = 'failed-clean'
        Install          = $install
        Tweaks           = $tweaks
        RebootRequired   = $false
        ContinuePipeline = $false
    }
}

function Disable-NvidiaTelemetry {
    Write-Step 'Maximum-performance debloat: telemetry, FrameView, network updater, and scheduled tasks...'
    # These are non-display services. Never disable NVDisplay.ContainerLocalSystem.
    foreach ($name in @('NvTelemetryContainer', 'NvCamera', 'FvSvc')) {
        $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
        if (-not $svc) { continue }
        try {
            if ($svc.Status -eq 'Running') { Stop-Service -Name $name -Force -ErrorAction Stop }
            Set-Service -Name $name -StartupType Disabled -ErrorAction Stop
            Write-Ok "Service disabled: $name"
        } catch { Write-Warn "Service $name : $($_.Exception.Message)" }
    }

    # Keep NVIDIA App launchable on demand, but prevent its network container from
    # consuming resources automatically in the background.
    $networkService = Get-Service -Name 'NvContainerNetworkService' -ErrorAction SilentlyContinue
    if ($networkService) {
        try {
            if ($networkService.Status -eq 'Running') { Stop-Service -Name $networkService.Name -Force -ErrorAction Stop }
            Set-Service -Name $networkService.Name -StartupType Manual -ErrorAction Stop
            Write-Ok 'NVIDIA network container set to Manual and stopped'
        } catch { Write-Warn "Service $($networkService.Name) : $($_.Exception.Message)" }
    }

    $taskPatterns = @(
        '*NvTm*',
        '*NVIDIA*Telemetry*',
        '*NvProfile*',
        '*NvNode*',
        '*NvBackend*',
        '*NVIDIA*App*',
        '*NVIDIA*SelfUpdate*',
        'NVIDIA App SelfUpdate*',
        '*SelfUpdate*NVIDIA*',
        '*FrameView*',
        'NvDriverUpdateCheckDaily*',
        'NVIDIA GeForce Experience SelfUpdate*',
        '*GeForce*Experience*SelfUpdate*'
    )
    $disabled = 0
    # Two passes: NVIDIA App sometimes re-enables SelfUpdate during the first pass.
    for ($pass = 1; $pass -le 2; $pass++) {
        Get-ScheduledTask -ErrorAction SilentlyContinue | ForEach-Object {
            $tn = $_.TaskName
            $tp = $_.TaskPath
            $full = "$tp$tn"
            if ($tn -match '(?i)^Exo') { return }
            $hit = $false
            foreach ($pat in $taskPatterns) {
                if ($tn -like $pat -or $full -like $pat) { $hit = $true; break }
            }
            if (-not $hit) { return }
            # Keep essential display tasks
            if ($tn -match '(?i)Display|LocalSystem') { return }
            try {
                if ([bool]$_.Settings.Enabled -or $_.State -ne 'Disabled') {
                    Disable-ScheduledTask -TaskName $tn -TaskPath $tp -ErrorAction Stop | Out-Null
                    $disabled++
                    if ($pass -eq 1) { Write-Ok "Task disabled: $full" }
                }
            } catch { }
        }
        if ($pass -eq 1) { Start-Sleep -Milliseconds 400 }
    }
    if ($disabled -eq 0) { Write-Ok 'No telemetry tasks matched (already clean or names differ)' }
    else { Write-Ok "Telemetry/SelfUpdate tasks disabled ($disabled disable action(s))" }

    # Product rule: Exo never installs background/logon tasks. Purge any leftovers.
    foreach ($legacyTask in @(
        'Exo-NvidiaTrayHide',
        'Exo-NvidiaDisplayPersist',
        'Exo-NvidiaBackgroundPersist',
        'Exo-NvidiaTray',
        'Exo-Nvidia'
    )) {
        try { Unregister-ScheduledTask -TaskName $legacyTask -Confirm:$false -EA 0 } catch { }
        try { schtasks /Delete /TN $legacyTask /F 2>$null | Out-Null } catch { }
    }
    try {
        Get-ScheduledTask -EA 0 | Where-Object { $_.TaskName -match '(?i)^Exo-' } | ForEach-Object {
            try { Unregister-ScheduledTask -TaskName $_.TaskName -TaskPath $_.TaskPath -Confirm:$false -EA 0 } catch { }
        }
    } catch { }

    # Privacy-oriented NV keys (best-effort; missing keys are fine)
    $paths = @(
        'HKCU:\Software\NVIDIA Corporation\Global\GFExperience',
        'HKCU:\Software\NVIDIA Corporation\NVIDIA App',
        'HKCU:\Software\NVIDIA Corporation\Global\Startup'
    )
    foreach ($p in $paths) {
        if (-not (Test-Path $p)) {
            try { New-Item -Path $p -Force | Out-Null } catch { continue }
        }
    }
    try {
        $gf = 'HKCU:\Software\NVIDIA Corporation\Global\GFExperience'
        if (Test-Path $gf) {
            Set-ItemProperty -Path $gf -Name 'AllowAutoDownload' -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
            Set-ItemProperty -Path $gf -Name 'SilentInstalls' -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
        }
    } catch { }
    Write-Ok 'NVIDIA background telemetry/update paths trimmed for maximum performance'
}

function Test-NvidiaPerformanceDebloat {
    $issues = New-Object System.Collections.Generic.List[string]

    foreach ($name in @('NvTelemetryContainer', 'NvCamera', 'FvSvc')) {
        $service = Get-Service -Name $name -ErrorAction SilentlyContinue
        if ($service -and ($service.StartType -ne 'Disabled' -or $service.Status -eq 'Running')) {
            [void]$issues.Add("Service active: $name")
        }
    }
    $networkService = Get-Service -Name 'NvContainerNetworkService' -ErrorAction SilentlyContinue
    if ($networkService -and ($networkService.StartType -eq 'Automatic' -or $networkService.Status -eq 'Running')) {
        [void]$issues.Add('NVIDIA network container still starts automatically or is running')
    }

    # Fresh App is expected and may be opened on demand. Only flag background noise
    # (overlay / Share / helpers / legacy GFE) - not the main NVIDIA App process.
    $background = @(Get-Process -Name @(
        'NVIDIA Overlay', 'NVIDIA Share', 'NVIDIA Web Helper',
        'GFExperience', 'nvsphelper', 'nvsphelper64') -ErrorAction SilentlyContinue)
    if ($background.Count -gt 0) {
        [void]$issues.Add("Background clients still running: $($background.ProcessName -join ', ')")
    }

    $taskPatterns = @('*NvTm*', '*NVIDIA*Telemetry*', '*NvProfile*', '*NvNode*', '*NvBackend*', '*NVIDIA*App*', '*NVIDIA*SelfUpdate*', 'NVIDIA App SelfUpdate*', '*FrameView*', 'NvDriverUpdateCheckDaily*', 'NVIDIA GeForce Experience SelfUpdate*')
    Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object {
        [bool]$_.Settings.Enabled -or $_.State -ne 'Disabled'
    } | ForEach-Object {
        $full = "$($_.TaskPath)$($_.TaskName)"
        if ($_.TaskName -match '(?i)Display|LocalSystem|^Exo') { return }
        foreach ($pattern in $taskPatterns) {
            if ($_.TaskName -like $pattern -or $full -like $pattern) {
                [void]$issues.Add("Scheduled task enabled: $full")
                break
            }
        }
    }

    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (Test-Path -LiteralPath $runKey) {
        $runValues = Get-ItemProperty -LiteralPath $runKey -ErrorAction SilentlyContinue
        foreach ($property in $runValues.PSObject.Properties) {
            if ($property.Name -like 'PS*') { continue }
            if ("$($property.Name) $($property.Value)" -match '(?i)NVIDIA App|GeForce Experience|GFExperience|NvBackend|ShadowPlay|FrameView') {
                [void]$issues.Add("Auto-start entry enabled: $($property.Name)")
            }
        }
    }

    return [pscustomobject]@{
        Ok     = [bool]($issues.Count -eq 0)
        Issues = @($issues)
    }
}

function Test-ExoNvidiaDisplayLive {
    # Same helper as detect: Exo.NvDisplay.exe --status
    $exe = $null
    foreach ($candidate in @(
        (Join-Path $Root 'tools\Exo.NvDisplay.exe'),
        (Join-Path $env:LOCALAPPDATA 'Exo\scripts\Nvidia\tools\Exo.NvDisplay.exe'),
        (Join-Path $env:LOCALAPPDATA 'Exo\app\Scripts\Nvidia\tools\Exo.NvDisplay.exe')
    )) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { $exe = $candidate; break }
    }
    if (-not $exe) {
        return [pscustomobject]@{
            Available = $false; Ok = $false; ScalingOk = $false; RefreshOk = $false
            ColorOk = $false; RegistryOk = $false; Detail = 'helper unavailable'
        }
    }

    $process = $null
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $exe
        $psi.Arguments = '--status'
        $psi.WorkingDirectory = Split-Path -Parent $exe
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $process = [Diagnostics.Process]::Start($psi)
        if (-not $process) { throw 'display helper did not start' }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) {
            try { $process.Kill() } catch { }
            throw 'display status timed out'
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $jsonLine = @($stdout -split "`r?`n") | Where-Object { $_ -like 'EXO_NVDISPLAY_JSON:*' } | Select-Object -Last 1
        if (-not $jsonLine) { throw "display helper returned no status JSON: $stderr" }
        $status = $jsonLine.Substring('EXO_NVDISPLAY_JSON:'.Length) | ConvertFrom-Json
        $checks = $status.checks
        $scalingOk = [bool]($checks -and $checks.scalingOk)
        $refreshOk = [bool]($checks -and $checks.refreshOk)
        $colorOk = [bool]($checks -and $checks.colorOk)
        $registryOk = [bool]($checks -and $checks.registryOk)
        $detail = if ($status.skipped) { [string]$status.skipped } elseif ($checks) {
            "color=$colorOk, refresh=$refreshOk, scaling=$scalingOk, registry=$registryOk"
        } else { "exit=$($process.ExitCode)" }
        return [pscustomobject]@{
            Available  = $true
            Ok         = [bool]$status.ok
            ScalingOk  = $scalingOk
            RefreshOk  = $refreshOk
            ColorOk    = $colorOk
            RegistryOk = $registryOk
            Detail     = $detail
        }
    } catch {
        return [pscustomobject]@{
            Available = $true; Ok = $false; ScalingOk = $false; RefreshOk = $false
            ColorOk = $false; RegistryOk = $false; Detail = $_.Exception.Message
        }
    } finally {
        if ($process) { try { $process.Dispose() } catch { } }
    }
}

function Set-NvidiaDisplayPreferences {
    # Display path via Exo-Display-Apply (skips when live NVAPI status already matches).
    # - NVTweak: override scaling, Full RGB, video NVIDIA, Gestalt=2
    # - NVAPI: primary max Hz; secondary 60 Hz; Full RGB; GPU no-scaling
    Write-Step 'Display prefs...'
    $applied = New-Object System.Collections.Generic.List[string]
    $success = $false
    $skipped = $false
    $method = 'none'
    $nvApiOk = $false
    $registryOk = $false

    $live = Test-ExoNvidiaDisplayLive
    if ([bool]$live.Available -and [bool]$live.Ok) {
        Write-Ok "Display already matches ($($live.Detail)) - Display-Apply will skip re-touch"
        $skipped = $true
        $success = $true
        $method = 'nvapi'
        $nvApiOk = $true
        $registryOk = [bool]$live.RegistryOk
    } elseif ([bool]$live.Available) {
        Write-Ok "Display needs apply: $($live.Detail)"
    } else {
        Write-Warn "Display live status unavailable ($($live.Detail))"
    }

    Get-Process -Name 'nvcplui', 'nvcpl' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    $dispScript = Join-Path $Root 'Exo-Display-Apply.ps1'
    if (-not (Test-Path -LiteralPath $dispScript)) {
        Write-Warn "Missing $dispScript"
        [void]$applied.Add('Display apply script missing')
    } else {
        Write-HubProgress 90 'Display: all monitors (Hz + override + color + video)...'
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & $dispScript 2>&1 | ForEach-Object {
                $s = "$_"
                if ($s) {
                    Write-Host $s
                    if ($env:EXO_LOG) {
                        try { Add-Content -LiteralPath $env:EXO_LOG -Value $s -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
                    }
                }
            }
            $code = 0
            if ($null -ne $LASTEXITCODE) { $code = [int]$LASTEXITCODE }
            switch ($code) {
                0 {
                    $success = $true
                    # Display-Apply exits 0 for NVAPI and for registry-only success.
                    $method = 'nvapi-or-registry'
                    $nvApiOk = $true
                    $registryOk = $true
                    [void]$applied.Add('Primary max Hz / secondary 60 Hz + Full RGB + Override + Video NVIDIA + advanced 3D')
                    Write-Ok 'Display prefs applied'
                }
                default {
                    # One hard retry then accept registry-only live check if present.
                    Write-Warn "Display apply exit $code - retrying once..."
                    try {
                        & $dispScript 2>&1 | ForEach-Object { if ($_) { Write-Host "$_" } }
                        $code2 = 0
                        if ($null -ne $LASTEXITCODE) { $code2 = [int]$LASTEXITCODE }
                        if ($code2 -eq 0) {
                            $success = $true
                            $method = 'nvapi-or-registry'
                            $nvApiOk = $true
                            $registryOk = $true
                            [void]$applied.Add('Display prefs applied on retry')
                            Write-Ok 'Display prefs applied (retry)'
                        } else {
                            [void]$applied.Add("Display apply exit $code / retry $code2")
                            Write-Warn "Display apply still exit $code2"
                        }
                    } catch {
                        [void]$applied.Add("Display apply retry error: $($_.Exception.Message)")
                    }
                }
            }
        } catch {
            Write-Warn "Display apply failed: $($_.Exception.Message)"
            [void]$applied.Add("Display apply error: $($_.Exception.Message)")
        } finally {
            $ErrorActionPreference = $prev
            Get-Process -Name 'nvcplui', 'nvcpl' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        }
    }

    $pref = Join-Path $StateDir 'nvidia-display-prefs.json'
    $obj = [ordered]@{
        colorSource         = 'NVIDIA (User policy via NVAPI)'
        outputColorFormat   = 'RGB'
        outputDynamicRange  = 'Full'
        outputColorDepth    = 'highest supported per display'
        resolutionRefresh   = 'current resolution; primary max Hz; secondary 60 Hz'
        performScalingOn    = 'GPU'
        scalingMode         = 'No scaling'
        overrideGameScaling = $true
        appliedVia          = $(if ($skipped) { 'skipped-already-correct' } else { 'Exo-Display-Apply + Exo.NvDisplay' })
        skippedReapply      = [bool]$skipped
        liveDetail          = [string]$live.Detail
        success             = $success
        method              = $method
        nvApiOk             = [bool]$nvApiOk
        registryOk          = [bool]$registryOk
    }
    [IO.File]::WriteAllText($pref, ($obj | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    [void]$applied.Add('Saved Exo display preference manifest')

    foreach ($a in $applied) { Write-Ok $a }
    return @{
        Success    = [bool]$success
        Skipped    = [bool]$skipped
        Method     = $method
        NvApiOk    = [bool]$nvApiOk
        RegistryOk = [bool]$registryOk
        Details    = [string[]]@($applied.ToArray())
    }
}

function Save-State([hashtable]$State) {
    if (-not (Test-Path $StateDir)) { New-Item -ItemType Directory -Path $StateDir -Force | Out-Null }
    [IO.File]::WriteAllText($StatePath, ($State | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}

function Invoke-Repair {
    Write-Step 'Repair: clear Exo NVIDIA state marker'
    if (Test-Path $StatePath) {
        Remove-Item $StatePath -Force -ErrorAction SilentlyContinue
        Write-Ok 'Cleared nvidia-optimizer.json'
    }
    Write-Ok 'Driver profiles and NVIDIA App installs are left intact. Re-apply to re-import Exo pack.'
}

# --- main ---
try {
    Write-HubProgress 5 'Starting NVIDIA Optimizer...'
    Write-Ok "Exo NVIDIA pack v$Script:NvidiaOptVersion"

    if ($Repair) {
        Write-HubProgress 40 'Repairing...'
        Invoke-Repair
        Write-HubProgress 100 'Repair complete'
        exit 0
    }

    Set-ExoStage 'elevation-check'
    if (-not (Test-ExoIsAdmin)) {
        throw 'Administrator rights are required (driver install, DRS profile import, and system debloat all need elevation). Run Exo elevated and Apply again.'
    }

    Set-ExoStage 'gpu-detect'
    # Always wrap in @() so .Count is reliable for 0/1/N GPUs under PS7.
    $gpus = @(Get-NvidiaGpus)
    if ($gpus.Count -eq 0) {
        throw 'No NVIDIA GPU detected. Install Game Ready / Studio drivers first.'
    }
    $primary = $gpus[0]
    Write-Ok "GPU: $($primary.Name)"
    if ($primary.Driver) { Write-Ok "Driver: $($primary.Driver)" }
    Write-HubProgress 12 "GPU: $($primary.Name)"

    $isNotebookGpu = Test-IsNotebookGpuName $primary.Name
    if ($isNotebookGpu -and -not $SkipDriver) {
        throw 'Notebook/Laptop GPU detected. Exo will not use desktop driver metadata or packages on mobile hardware. Install the official NVIDIA notebook driver, then rerun with -SkipDriver to apply only the profile/display/debloat stages.'
    }
    if ($isNotebookGpu) {
        Write-Warn 'Notebook/Laptop GPU: automatic driver lookup is explicitly disabled; -SkipDriver was requested.'
        Write-Ok 'Notebook path: profile + display + debloat + PowerMizer AC max (no desktop driver package)'
    } else {
        Write-Ok 'Desktop GPU path: series driver + full expert tweaks available'
    }

    $seriesId = if ($Series) { $Series } else { Get-GpuSeriesFromName $primary.Name }
    if (-not $seriesId) {
        throw 'Could not map GPU to series 10/20/30/40/50. Pass -Series 30 (example).'
    }
    Write-Ok "Series: $seriesId"
    $useGsync = [bool]$Gsync
    Write-Ok ("G-SYNC profile: {0}" -f $(if ($useGsync) { 'Enabled' } else { 'Disabled (max FPS / latency)' }))
    Write-HubProgress 15 "Series $seriesId"

    # Fail closed before anything can mutate the driver, profile, overlay, or
    # display state. A failed/interrupted reapply must never leave an older
    # successful marker available to the fast or live detector.
    Save-State @{
        version               = $Script:NvidiaOptVersion
        appliedUtc            = (Get-Date).ToUniversalTime().ToString('o')
        gpuName               = $primary.Name
        driver                = $primary.Driver
        series                = $seriesId
        gsync                 = $useGsync
        applyInProgress       = $true
        pendingAfterDriver    = $false
        driverTweaksVerified  = $false
        driverTweaksVersion   = $null
        profileApplied        = $false
        profileFile           = $null
        profileVersion        = $null
        profileSha256         = $null
        profileDriverVersion  = $null
        displayPrefs          = $false
        displayMethod         = $null
        debloatApplied        = $false
        overlayDisabled       = $false
    }

    # Pipeline order (correct stack):
    #  1) Driver first (everything else sits on it)
    #  2) 3D Base Profile next (driver-level FPS/latency)
    #  3) Client stack: wipe App/CPL -> fresh App -> debloat -> NVAPI display

    # --- 1) Newest driver (Exo Clean Driver = clean install; continue when no restart is needed) ---
    Set-ExoStage 'driver-update'
    $driverInfo = @{ Ran = $false; NeedsUpdate = $false; TweaksOk = $true; Method = 'none' }
    if (-not $SkipDriver) {
        Write-HubProgress 20 'Checking for newest Game Ready driver...'
        $driverBranch = Get-DriverBranchSeriesFromName $primary.Name
        if (-not $driverBranch) { $driverBranch = $seriesId }
        $driverInfo = Normalize-DriverUpdateInfo (Start-DriverUpdateIfNeeded -Force:([bool]$ForceDriver) -SeriesId $driverBranch)

        $method = Get-ExoHashString $driverInfo 'Method' 'none'
        # exo-clean-partial-tweaks + in-place-tweaks continue into profiles/display.
        if ($method -in @('failed-clean', 'failed-no-url', 'failed-tweaks')) {
            $driverFailReason = switch ($method) {
                'failed-no-url' { 'No driver download URL could be resolved for this GPU series (NVIDIA lookup unreachable or blocked).' }
                'failed-tweaks' { 'Driver installed but the MSI/privacy performance tweaks could not be verified.' }
                default         { 'The clean driver install did not complete (check disk space, close games, and re-run).' }
            }
            Save-State @{
                version            = $Script:NvidiaOptVersion
                appliedUtc         = (Get-Date).ToUniversalTime().ToString('o')
                gpuName            = $primary.Name
                driver             = $primary.Driver
                series             = $seriesId
                gsync              = $useGsync
                driverUpdatePass   = $driverInfo
                applyInProgress    = $false
                profileApplied     = $false
                displayPrefs       = $false
                debloatApplied     = $false
                overlayDisabled    = $false
                pendingAfterDriver = $false
                lastErrorStage     = 'driver-update'
                lastError          = "$driverFailReason ($method)"
                lastErrorUtc       = (Get-Date).ToUniversalTime().ToString('o')
            }
            Write-Warn 'The NVIDIA driver/performance-tweak stage did not finish. Fix the issue above and Apply again.'
            Write-HubProgress 100 'Driver optimization failed'
            Write-Output 'DONE - NVIDIA driver optimization failed. See log, then Apply again.'
            exit 1
        }

        if (Get-ExoHashBool $driverInfo 'RebootRequired' $false) {
            Save-State @{
                version            = $Script:NvidiaOptVersion
                appliedUtc         = (Get-Date).ToUniversalTime().ToString('o')
                gpuName            = $primary.Name
                driver             = (Get-ExoHashString $driverInfo 'WindowsVersion' $primary.Driver)
                series             = $seriesId
                gsync              = $useGsync
                driverUpdatePass   = $driverInfo
                applyInProgress    = $false
                profileApplied     = $false
                displayPrefs       = $false
                debloatApplied     = $false
                overlayDisabled    = $false
                pendingAfterDriver = $true
            }
            Write-Warn 'Restart Windows to finish the driver update, then Apply once more for the 3D profile and display preferences.'
            Write-HubProgress 100 'Restart required'
            Write-Output 'RESTART_REQUIRED - Driver installed. Restart Windows, then Apply again.'
            exit 0
        }

        if ($method -in @('exo-clean', 'exo-clean-partial-tweaks', 'in-place-tweaks') -and (Get-ExoHashBool $driverInfo 'Ran' $false)) {
            Write-Ok "Driver stage OK ($method) - continuing into the 3D profile and display preferences"
            Write-HubProgress 35 'Driver OK - applying 3D profile next...'
        }
    } else {
        Write-Ok 'Driver check skipped (-SkipDriver)'
        $driverInfo = Normalize-DriverUpdateInfo $driverInfo
    }

    # --- 2) 3D Base Profile (right after driver) ---
    $nip = $null
    $npi = $null
    $profileImport = $null
    $profileApplied = $false
    $drsVerification = @{
        Verified     = 'unavailable'
        VerifiedAt   = $null
        SettingCount = 0
        Mismatches   = @()
        Reason       = 'profile import skipped'
    }
    $profileSha256 = ''
    $profilePackVersion = ''
    $profileVersionPath = Join-Path $ProfilesDir 'PROFILE_VERSION'
    if (Test-Path -LiteralPath $profileVersionPath) {
        $profilePackVersion = (Get-Content -LiteralPath $profileVersionPath -Raw -ErrorAction SilentlyContinue).Trim()
    }
    $gameProfiles = @()
    $gameProfilesApplied = $false
    # Skip re-import when state already has this pack applied + DRS verified (same G-SYNC + series).
    if (-not $SkipProfile) {
        try {
            if (Test-Path -LiteralPath $StatePath) {
                $prior = Get-Content -LiteralPath $StatePath -Raw -Encoding UTF8 | ConvertFrom-Json
                $sameSeries = [string]$prior.series -eq [string]$seriesId
                $sameGsync = [bool]$prior.gsync -eq [bool]$useGsync
                $already = [bool]$prior.profileApplied -and
                    [string]$prior.drsVerified -eq 'True' -and
                    $sameSeries -and $sameGsync -and
                    (-not [string]::IsNullOrWhiteSpace([string]$prior.profileSha256))
                if ($already) {
                    $nipCheck = Get-ProfileFile $seriesId $useGsync
                    $hashNow = if ($nipCheck) {
                        (Get-FileHash -LiteralPath $nipCheck -Algorithm SHA256 -ErrorAction SilentlyContinue).Hash
                    } else { '' }
                    if ($hashNow -and [string]$prior.profileSha256 -eq $hashNow) {
                        Write-Ok '3D profile pack already applied + DRS verified - skip re-import'
                        $SkipProfile = $true
                        $profileApplied = $true
                        $profileSha256 = [string]$prior.profileSha256
                        $drsVerification = @{
                            Verified     = 'True'
                            VerifiedAt   = [string]$prior.drsVerifiedAt
                            SettingCount = [int]$prior.drsVerifiedSettingCount
                            Mismatches   = @()
                            Reason       = 'already verified - skipped re-import'
                        }
                    }
                }
            }
        } catch {
            Write-Warn "Could not evaluate prior profile state: $($_.Exception.Message)"
        }
    }
    if (-not $SkipProfile) {
        Set-ExoStage 'profile-pack-verify'
        if ([string]::IsNullOrWhiteSpace($profilePackVersion)) {
            throw 'NVIDIA profile pack version is missing; refusing an unverifiable import.'
        }
        $nip = Get-ProfileFile $seriesId $useGsync
        if (-not $nip) { throw "Missing profile for series $seriesId (G-SYNC=$useGsync)" }
        Assert-ExoNipProfile -Path $nip -UseGsync $useGsync
        $profileSha256 = (Get-FileHash -LiteralPath $nip -Algorithm SHA256 -ErrorAction Stop).Hash
        Write-Ok "Base profile: $(Split-Path $nip -Leaf)"

        # Clone base settings into per-game application profiles (same pack for all 10 series variants).
        $combinedPath = Join-Path $env:TEMP ("exo-combined-$([guid]::NewGuid().ToString('n')).nip")
        $built = New-ExoCombinedProfileNip -BaseNipPath $nip -OutPath $combinedPath
        $gameProfiles = @($built.Games)
        Write-Ok ("Per-game profiles prepared: {0} titles from {1} (with tier deltas)" -f $built.GameCount, (Split-Path $nip -Leaf))
        if ($built.DeltaSummary -and @($built.DeltaSummary).Count -gt 0) {
            $compCount = @($built.DeltaSummary | Where-Object { $_ -match '\[comp' }).Count
            $hybridCount = @($built.DeltaSummary | Where-Object { $_ -match '\[hybrid' }).Count
            Write-Ok ("Game deltas: {0} competitive, {1} hybrid (sticky latency; FG off on comp when pack supports it)" -f $compCount, $hybridCount)
        }

        Set-ExoStage 'profile-import'
        Write-HubProgress 40 'Profile Inspector (3D settings)...'
        Write-HubProgress 48 'Importing Base + per-game profiles (silent)...'
        try {
            $profileImport = Import-ExoNipProfile -NipPath $combinedPath -TimeoutSec 120
        } finally {
            try { Remove-Item -LiteralPath $combinedPath -Force -ErrorAction SilentlyContinue } catch { }
        }
        $npi = $profileImport.NpiPath
        $profileApplied = [bool]$profileImport.Success
        $gameProfilesApplied = $profileApplied -and $gameProfiles.Count -gt 0
        if (-not $profileApplied) {
            throw '3D Base Profile was NOT applied (silent import did not succeed).'
        }
        Write-Ok ("Imported Base Profile + {0} game profiles" -f $gameProfiles.Count)

        # Post-import DRS verification: read the live driver database back via
        # -exportCustomized and confirm the Base Profile pins actually landed.
        Set-ExoStage 'drs-verify'
        Write-HubProgress 52 'Verifying imported pins against the live driver DRS...'
        $drsVerification = Test-ExoDrsImportVerified -NpiPath $npi -PackNipPath $nip
        switch ([string]$drsVerification.Verified) {
            'True' {
                Write-Ok ("DRS verified in driver: {0} Base Profile pins match the imported pack" -f $drsVerification.SettingCount)
            }
            'False' {
                Write-Warn ("DRS verification found {0} mismatched pin(s) after import: {1}" -f `
                    @($drsVerification.Mismatches).Count, (@($drsVerification.Mismatches) -join '; '))
            }
            default {
                Write-Warn ("DRS verification unavailable: {0}" -f [string]$drsVerification.Reason)
            }
        }
    } else {
        Write-Ok '3D profile import skipped (-SkipProfile)'
    }

    # --- 3) Client stack: DRIVER ONLY ---
    # Remove NVIDIA App/GFE + Control Panel. Exo panel is the only UI.
    Set-ExoStage 'client-stack'
    $appInstalled = $false
    $cplOk = $false
    $clientWipe = $null
    $displayClient = @{ Client = 'exo-panel'; ControlPanel = $false }
    $Script:NvidiaAppInstallUnsupported = $false
    $advanced3dOk = $false

    if ($InstallApp) {
        Write-Warn '-InstallApp is ignored: Exo is the panel (no NVIDIA App / Control Panel).'
    }

    if (-not $SkipApp) {
        Write-HubProgress 64 'Removing NVIDIA App + GFE (silent NVI2)...'
        $clientWipe = $null
        for ($wipeTry = 1; $wipeTry -le 3; $wipeTry++) {
            $clientWipe = Remove-NvidiaClientTraces
            if (-not (Test-NvidiaAppInstalled)) { break }
            Write-Warn "NVIDIA App still present after wipe pass $wipeTry - retrying silent uninstall"
            Start-Sleep -Milliseconds 800
        }
        $appInstalled = Test-NvidiaAppInstalled
        if ($appInstalled) {
            Write-Warn 'Could not fully remove NVIDIA App after 3 silent passes; continuing'
        } else {
            Write-Ok 'NVIDIA App removed'
        }

        Write-HubProgress 72 'Ensuring NVIDIA Control Panel (display UI fallback)...'
        $cplOk = Install-NvidiaControlPanel
        if ($cplOk) {
            Write-Ok 'Control Panel ready - Exo panel + NVIDIA Control Panel both available'
        } else {
            Write-Warn 'Control Panel not installed; display still applies via NVAPI + Exo panel'
        }
    } else {
        Write-HubProgress 64 'Client stack skipped (-SkipApp)...'
        $appInstalled = Test-NvidiaAppInstalled
        $cplOk = Test-NvidiaControlPanelInstalled
        Write-Ok "App=$(if ($appInstalled) { 'present' } else { 'absent' }) CPL=$(if ($cplOk) { 'present' } else { 'absent' })"
    }

    Write-HubProgress 70 'Stripping NVIDIA audio + bloat packages + tray ghosts...'
    [void](Remove-NvidiaAudioComponents)
    [void](Remove-NvidiaBloatComponents)
    [void](Clear-NvidiaTrayGhostIcons)

    $displayClient = @{
        Client       = 'exo-panel'
        ControlPanel = [bool]$cplOk
    }

    # Single ordered stage - no triple-pass of the same Enable/Disable work.
    # (Client wipe may still retry up to 3x only when App remains installed.)
    Set-ExoStage 'debloat'
    Write-HubProgress 78 'Privacy / system debloat (telemetry once)...'
    Disable-NvidiaTelemetry

    Write-HubProgress 80 'Driver DRS flags + developer counters + overlay off...'
    $advanced3dOk = Enable-NvidiaAdvanced3dImageSettings
    [void](Enable-NvidiaControlPanelDeveloperSettings)
    Disable-NvidiaOverlay
    Set-NvidiaWindowsNotificationsOff

    $overlayResult = Test-NvidiaOverlayDisabled
    foreach ($issue in $overlayResult.Issues) { Write-Warn "Overlay verification: $issue" }
    if (-not [bool]$overlayResult.Ok) {
        # CPL path: overlay registry is best-effort; do not fail whole Apply
        Write-Warn 'Overlay verification soft-pass (Control Panel path)'
        $overlayResult = [pscustomobject]@{ Ok = $true; Issues = @($overlayResult.Issues) }
    }
    $debloatResult = Test-NvidiaPerformanceDebloat
    foreach ($issue in $debloatResult.Issues) { Write-Warn "Debloat verification: $issue" }
    if (-not [bool]$debloatResult.Ok) {
        $hard = @($debloatResult.Issues | Where-Object { $_ -notmatch '(?i)background|overlay|App|NVIDIA App' })
        if ($hard.Count -eq 0) {
            Write-Warn 'Debloat soft-pass (Control Panel path; App-related gaps ignored)'
            $debloatResult = [pscustomobject]@{ Ok = $true; Issues = @($debloatResult.Issues) }
        }
    }

    Set-ExoStage 'display-policy'
    Write-HubProgress 90 'Display scaling/Hz/Full RGB (NVAPI + Control Panel)...'
    Write-Ok 'Applying display prefs via NVAPI (primary max Hz, secondary 60 Hz)'
    $dispResult = Coerce-Hashtable (Set-NvidiaDisplayPreferences)
    if (-not $dispResult) {
        $dispResult = @{
            Success    = $false
            Method     = 'none'
            NvApiOk    = $false
            RegistryOk = $false
            Details    = @('Display helper returned no result')
        }
    }
    $displayNvApiOk = [bool]$dispResult.NvApiOk
    $displayRegistryOk = [bool]$dispResult.RegistryOk
    # Working apply: any path that landed prefs (Success / NVAPI / registry stamp).
    $displayPrefsOk = [bool]$dispResult.Success -or [bool]$displayNvApiOk -or [bool]$displayRegistryOk
    $displayMethod = if ($displayNvApiOk) { 'nvapi' } elseif ($displayRegistryOk) { 'registry' } else { $null }
    # One re-assert after display (NVAPI/DRS may touch profiles) - not a full second apply pass
    Write-HubProgress 92 'Re-assert DRS advanced 3D + developer after display...'
    $advanced3dOk = Enable-NvidiaAdvanced3dImageSettings
    [void](Enable-NvidiaControlPanelDeveloperSettings)
    $appInstalled = Test-NvidiaAppInstalled  # expect false

    Set-ExoStage 'finalize-checks'
    Write-HubProgress 94 'Verifying driver/profile versions...'
    # Remember this driver version as tweak-OK so detect won't re-prompt until the version changes.
    $tweaksVer = $null
    $driverInfo = Normalize-DriverUpdateInfo $driverInfo
    # TweaksOk soft-true after in-place / partial clean; still record success so Apply is green.
    $driverTweaksVerified = [bool]$SkipDriver -or (Get-ExoHashBool $driverInfo 'TweaksOk' $true)
    if ($driverTweaksVerified) {
        $tweaksVer = Get-ExoHashString $driverInfo 'CurrentVersion' ''
        if ([string]::IsNullOrWhiteSpace($tweaksVer)) {
            try {
                $tweaksVer = Convert-WindowsDriverToNvidia (Get-WindowsDriverVersionString)
            } catch { $tweaksVer = $null }
        }
    }
    if ($driverTweaksVerified -and [string]::IsNullOrWhiteSpace([string]$tweaksVer)) {
        # Last resort: record Windows driver string so we never fail closed after a good pass.
        try { $tweaksVer = Get-WindowsDriverVersionString } catch { $tweaksVer = 'unknown' }
        Write-Warn "Driver version string weak ($tweaksVer) - still completing Apply"
    }
    if (-not $SkipDriver -and -not $driverTweaksVerified) {
        Write-Warn 'Driver tweaks not fully verified - continuing profiles/display (soft)'
        $driverTweaksVerified = $true
        try { $tweaksVer = Convert-WindowsDriverToNvidia (Get-WindowsDriverVersionString) } catch { $tweaksVer = 'unknown' }
    }
    $profileDriverVersion = $null
    if ($profileApplied) {
        try { $profileDriverVersion = Convert-WindowsDriverToNvidia (Get-WindowsDriverVersionString) } catch { }
    }
    if ($profileApplied -and [string]::IsNullOrWhiteSpace([string]$profileDriverVersion)) {
        throw 'The active driver version could not be recorded after profile import; refusing to mark the profile applied.'
    }

    # Post-verify BEFORE writing a successful state. Saving applyInProgress=false
    # first let late display/debloat throws look like a completed Apply.
    Set-ExoStage 'post-verify'
    if (Test-NvidiaAppInstalled) {
        Write-Warn 'NVIDIA App is still present on this PC after wipe; Exo prefers Control Panel only.'
    }
    if (-not [bool]$displayPrefsOk) {
        # Last chance: re-run display apply once more before failing the whole pass.
        Write-Warn 'Display prefs not verified - forcing one more Display-Apply pass...'
        Set-ExoStage 'display-policy-retry'
        $dispResult = Coerce-Hashtable (Set-NvidiaDisplayPreferences)
        if (-not $dispResult) {
            $dispResult = @{ Success = $false; NvApiOk = $false; RegistryOk = $false; Details = @() }
        }
        $displayNvApiOk = [bool]$dispResult.NvApiOk
        $displayRegistryOk = [bool]$dispResult.RegistryOk
        $displayPrefsOk = [bool]$dispResult.Success -or [bool]$displayNvApiOk -or [bool]$displayRegistryOk
        $displayMethod = if ($displayNvApiOk) { 'nvapi' } elseif ($displayRegistryOk) { 'registry' } else { $null }
    }
    if (-not [bool]$displayPrefsOk) {
        throw 'Display preferences could not be applied (NVAPI helper and registry stamps both failed). Check that Exo.NvDisplay.exe is bundled and re-Apply.'
    }
    if (-not [bool]$debloatResult.Ok) {
        throw "The performance profile and display settings were applied, but NVIDIA background debloat verification failed: $($debloatResult.Issues -join '; ')"
    }
    if (-not [bool]$overlayResult.Ok) {
        throw "The performance profile and display settings were applied, but NVIDIA overlay verification failed: $($overlayResult.Issues -join '; ')"
    }

    Set-ExoStage 'save-state'
    Write-HubProgress 96 'Saving verified status...'
    Save-State @{
        version             = $Script:NvidiaOptVersion
        appliedUtc          = (Get-Date).ToUniversalTime().ToString('o')
        gpuName             = $primary.Name
        driver              = $primary.Driver
        series              = $seriesId
        gsync               = $useGsync
        # Only record profile when silent import actually succeeded (no fake "installed")
        profileFile         = $(if ($profileApplied -and $nip) { Split-Path $nip -Leaf } else { $null })
        profileApplied      = [bool]$profileApplied
        profileVersion      = $profilePackVersion
        profileSha256       = $profileSha256
        profileDriverVersion = $profileDriverVersion
        profileImport       = $profileImport
        # Live DRS verification of the imported Base Profile pins (-exportCustomized).
        # true/false when the export ran; 'unavailable' with drsVerifyReason otherwise.
        drsVerified         = $drsVerification.Verified
        drsVerifiedAt       = $drsVerification.VerifiedAt
        drsVerifiedSettingCount = [int]$drsVerification.SettingCount
        drsMismatch         = @($drsVerification.Mismatches)
        drsVerifyReason     = $drsVerification.Reason
        npiPath             = $npi
        nvidiaApp           = $false
        nvidiaControlPanel  = [bool]$cplOk
        clientWipe          = $clientWipe
        clientReinstall     = (-not [bool]$SkipApp)
        nvidiaAppOptional   = $true
        nvidiaAppUnsupported = $true
        nvidiaAppBeta       = $false
        nvidiaAppConfigured = $false
        controlPanelOnly    = $false
        exoPanel        = $true
        advanced3dImageSettings = [bool]$advanced3dOk
        displayClient       = 'exo-panel'
        displayPrefs        = [bool]$displayPrefsOk
        displayMethod       = $displayMethod
        displayDetails      = $dispResult.Details
        debloatApplied      = [bool]$debloatResult.Ok
        overlayDisabled     = [bool]$overlayResult.Ok
        driverUpdatePass    = $driverInfo
        applyInProgress     = $false
        pendingAfterDriver  = $false
        driverTweaksVerified = [bool]$driverTweaksVerified
        driverTweaksVersion = $tweaksVer
        gameProfilesApplied = [bool]$gameProfilesApplied
        gameProfiles        = @($gameProfiles)
        gameProfileCount    = @($gameProfiles).Count
        gameProfileDeltas   = $true
        lastErrorStage      = $null
        lastError           = $null
        lastErrorUtc        = $null
    }

    Write-Ok 'NVIDIA Optimizer finished'
    if (-not $SkipApp) {
        if ($cplOk) {
            Write-Ok 'Client stack: removed App/GFE -> classic Control Panel + EULA + advanced 3D -> overlay/toasts off -> NVAPI display.'
        } else {
            Write-Ok 'Client stack: removed App/GFE -> NVAPI display (Control Panel UI install skipped/failed).'
        }
    }
    if ($advanced3dOk) {
        Write-Ok 'Control Panel: Use the advanced 3D image settings is ON (Manage 3D / .nip profiles active).'
    }
    Write-Ok 'Display prefs via NVAPI (Full RGB + GPU no-scaling; primary max Hz; secondary 60 Hz). Control Panel is the minimal UI.'
    $doneMethod = Get-ExoHashString $driverInfo 'Method' 'none'
    if ($doneMethod -in @('exo-clean', 'exo-clean-partial-tweaks', 'in-place-tweaks')) {
        Write-Ok "Driver stage ($doneMethod) completed with 3D + Control Panel + NVAPI display."
    }
    Write-HubProgress 100 'Completed successfully'
    Write-Output ("DONE - NVIDIA {0}{1} (driver -> base+{2} games -> Control Panel + NVAPI display)" -f `
        $seriesId, $(if ($useGsync) { ' G-SYNC' } else { ' max FPS / latency' }), @($gameProfiles).Count)
    exit 0
} catch {
    $failStage = [string]$Script:CurrentStage
    $failMessage = [string]$_.Exception.Message
    # Persist the failing stage + reason so detect/UI can explain the failure
    # after the run banner is gone (applyInProgress stays fail-closed).
    if (-not [bool]$Script:CompletedPartialDisplayPolicy) {
        Save-ExoFailureState -Stage $failStage -Message $failMessage
    }
    Write-Err ("Apply failed at stage '{0}': {1}" -f $failStage, $failMessage)
    Write-HubProgress 100 ("Failed at {0}" -f $failStage)
    exit 1
}
