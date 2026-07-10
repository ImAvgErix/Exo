# OptiHub NVIDIA Optimizer
# - Apply series + G-SYNC OptiHub Base Profile via Profile Inspector
# - Display settings through NVAPI (no Control Panel UI automation)
# - Privacy/debloat + Overlay off
#
#   Nvidia-Optimizer.ps1
#   Nvidia-Optimizer.ps1 -Gsync
#   Nvidia-Optimizer.ps1 -Repair
#   Nvidia-Optimizer.ps1 -Series 40 -Gsync
#   Nvidia-Optimizer.ps1 -InstallApp   # optional; App not used for display prefs

param(
    [switch]$Gsync,
    [ValidateSet('', '10', '20', '30', '40', '50')]
    [string]$Series = '',
    [switch]$Repair,
    [switch]$NonInteractive,
    [switch]$SkipDownload,
    [switch]$SkipApp,          # leave NVIDIA App as-is; kept for command-line compatibility
    [switch]$InstallApp,       # opt-in only - NVIDIA App is not used for display prefs
    [switch]$SkipProfile,
    [switch]$SkipDriver,
    [switch]$ForceDriver
)

$ErrorActionPreference = 'Stop'
$Script:NvidiaOptVersion = '1.5.3'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProfilesDir = Join-Path $Root 'profiles'
$StateDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'OptiHub'
$StatePath = Join-Path $StateDir 'nvidia-optimizer.json'
# Keep OptiHub's managed Profile Inspector private. Never delete user-installed copies.
$NpiDir = Join-Path $StateDir 'tools\nvidiaProfileInspector'
$DriverCacheDir = Join-Path $StateDir 'drivers'
$NpiExeName = 'nvidiaProfileInspector.exe'

function Write-HubProgress([int]$Percent, [string]$Status) {
    $p = [Math]::Max(0, [Math]::Min(100, $Percent))
    $line = "OPTIHUB_PROGRESS:$p|$Status"
    # IMPORTANT: do NOT Write-Output progress - it poisons function returns
    # (e.g. Download path becomes Object[] and -PackageExe fails type conversion).
    # Elevated OptiHub polls OPTIHUB_LOG; host line still shows in console.
    Write-Host $line
    if ($env:OPTIHUB_LOG) {
        try { Add-Content -LiteralPath $env:OPTIHUB_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
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
        if ($v -is [string] -and -not [string]::IsNullOrWhiteSpace($v) -and $v -notmatch '^OPTIHUB_PROGRESS') {
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
function Write-NLog([string]$Prefix, [string]$Msg) {
    $line = "$Prefix $Msg"
    Write-Host $line
    if ($env:OPTIHUB_LOG) {
        try { Add-Content -LiteralPath $env:OPTIHUB_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
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

function Get-ProfileFile([string]$SeriesId, [bool]$UseGsync) {
    $name = if ($UseGsync) { "$SeriesId Series G-SYNC.nip" } else { "$SeriesId Series.nip" }
    $path = Join-Path $ProfilesDir $name
    if (Test-Path -LiteralPath $path) { return $path }
    return $null
}

function Test-IsNotebookGpuName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) { return $false }
    return [bool]($Name -match '(?i)\b(?:Laptop GPU|Notebook|Mobile|Max-Q)\b|\bMX\d+\b|\b\d{3,4}M\b')
}

function Assert-OptiHubNipProfile {
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
                Write-Ok "Stopping OptiHub managed Profile Inspector PID $($_.Id)"
                Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
            } else {
                Write-Warn "Profile Inspector PID $($_.Id) is not managed by OptiHub and was left running"
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
    # Reuse the managed copy when current, and keep it as an offline fallback.
    Write-Step 'Checking OptiHub managed NVIDIA Profile Inspector...'
    $target = Join-Path $NpiDir $NpiExeName
    $stampPath = Join-Path $NpiDir 'OPTIHUB-NPI-VERSION.txt'
    $api = 'https://api.github.com/repos/Orbmu2k/nvidiaProfileInspector/releases/latest'
    $headers = @{ 'User-Agent' = 'OptiHub-Nvidia/1.5.0'; 'Accept' = 'application/vnd.github+json' }
    $rel = $null
    try {
        $rel = Invoke-RestMethod -Uri $api -Headers $headers -TimeoutSec 20
    } catch {
        if (Test-ManagedNpiCache -ExePath $target -StampPath $stampPath) {
            Write-Warn "Could not check for a Profile Inspector update; using cached managed copy: $($_.Exception.Message)"
            return $target
        }
        throw "Profile Inspector lookup failed and no cached copy is available: $($_.Exception.Message)"
    }

    $tag = [string]$rel.tag_name
    if (-not $tag) { throw 'Profile Inspector release did not include a version tag' }
    if (Test-ManagedNpiCache -ExePath $target -StampPath $stampPath -ExpectedTag $tag) {
        Write-Ok "Managed Profile Inspector is current and hash-verified ($tag)"
        return $target
    }

    $asset = @($rel.assets | Where-Object { $_.name -match '(?i)^nvidiaProfileInspector.*\.zip$' }) | Select-Object -First 1
    if (-not $asset) { $asset = @($rel.assets | Where-Object { $_.name -match '(?i)\.zip$' }) | Select-Object -First 1 }
    if (-not $asset) { throw 'No zip on nvidiaProfileInspector latest release' }
    $downloadUri = [uri]$asset.browser_download_url
    if ($downloadUri.Scheme -ne 'https' -or $downloadUri.Host -notmatch '(?i)(^|\.)github\.com$') {
        throw "Unexpected Profile Inspector download host: $($downloadUri.Host)"
    }

    $workId = [guid]::NewGuid().ToString('n')
    $zip = Join-Path $env:TEMP ("optihub-npi-$workId.zip")
    $extract = Join-Path $env:TEMP ("optihub-npi-$workId")
    Write-Ok "Latest NPI release: $tag ($($asset.name))"
    try {
        Invoke-WebRequest -Uri $downloadUri.AbsoluteUri -OutFile $zip -UseBasicParsing -Headers $headers -TimeoutSec 120
        $actualSize = (Get-Item -LiteralPath $zip).Length
        $expectedSize = 0L
        try { $expectedSize = [long]$asset.size } catch { }
        if ($expectedSize -gt 0 -and $actualSize -ne $expectedSize) {
            throw "Profile Inspector archive size mismatch (expected $expectedSize, got $actualSize)"
        }
        $publishedDigest = [string]$asset.digest
        if ($publishedDigest -notmatch '(?i)^sha256:([a-f0-9]{64})$') {
            throw 'Profile Inspector release did not provide a valid GitHub SHA256 digest'
        }
        $expectedDigest = $publishedDigest.Substring('sha256:'.Length)
        $actualDigest = (Get-FileHash -LiteralPath $zip -Algorithm SHA256 -ErrorAction Stop).Hash
        if ($actualDigest -ine $expectedDigest) {
            throw 'Profile Inspector archive SHA256 did not match the GitHub release digest'
        }
        Write-Ok 'Verified Profile Inspector release SHA256'
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
managedBy=OptiHub
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

function Import-OptiHubNipProfile {
    param(
        [Parameter(Mandatory)][string]$NipPath,
        [int]$TimeoutSec = 120
    )
    # Use OptiHub's isolated managed copy; user-installed Profile Inspector is never touched.
    if (-not (Test-Path -LiteralPath $NipPath)) {
        throw "NIP profile missing: $NipPath"
    }

    Stop-NpiProcesses
    $npi = Install-NpiFresh
    if (-not (Test-Path -LiteralPath $npi)) {
        throw 'Fresh Profile Inspector install failed'
    }

    $safeNip = Join-Path $env:TEMP ("optihub-profile-$([guid]::NewGuid().ToString('n')).nip")
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
        # Use the PS5-compatible Arguments property. The profile path is generated
        # by OptiHub and quoted for user profiles whose TEMP path contains spaces.
        $quotedNip = '"' + $safeNip.Replace('"', '\"') + '"'
        $psi.Arguments = "-silentImport $quotedNip"
        $psi.WorkingDirectory = $npiWorkDir
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $proc = [Diagnostics.Process]::Start($psi)
        if (-not $proc) { throw 'Failed to start Profile Inspector' }

        if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
            try { $proc.Kill() } catch { }
            Stop-NpiProcesses
            throw "Profile Inspector silent import timed out after ${TimeoutSec}s. Profile NOT marked applied."
        }
        $exitCode = [int]$proc.ExitCode
        Write-Ok "NPI silent import exit code: $exitCode"
    } finally {
        Stop-NpiProcesses
        if ($proc) { try { $proc.Dispose() } catch { } }
        try { Remove-Item -LiteralPath $safeNip -Force -ErrorAction SilentlyContinue } catch { }
    }

    if ($exitCode -ne 0) {
        throw "Profile Inspector silent import failed (exit $exitCode). Profile NOT marked applied."
    }

    Write-Ok '3D Base Profile imported with OptiHub managed NPI'
    return @{
        Success   = $true
        ExitCode  = $exitCode
        NpiPath   = $npi
        NipFile   = (Split-Path $NipPath -Leaf)
        ManagedNpi = $true
        NpiFolder = $NpiDir
    }
}

function Test-NvidiaAppInstalled {
    $paths = @(
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe'),
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA Overlay\NVIDIA App.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe')
    )
    foreach ($p in $paths) { if (Test-Path $p) { return $true } }
    $app = Get-AppxPackage -Name '*NVIDIA*' -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '(?i)NVIDIAApp|GeForceExperience' }
    return [bool]$app
}

function Test-NvidiaControlPanelInstalled {
    $appx = Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match '(?i)^NVIDIACorp\.NVIDIAControlPanel$'
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

function Ensure-NvidiaControlPanel {
    Write-Step 'Checking optional NVIDIA Control Panel...'
    if (Test-NvidiaControlPanelInstalled) {
        Write-Ok 'NVIDIA Control Panel already present'
        return $true
    }
    Write-Ok 'NVIDIA Control Panel not installed; skipped because NVAPI applies display settings directly'
    return $false
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
        foreach ($name in @('RecEnabled', 'DwmEnabled', 'DwmDvrEnabledV1', 'DisplayRecordingIndicator', 'DisplayGamecastIndicator', 'GameStreamPortal')) {
            try {
                New-ItemProperty -LiteralPath $sp -Name $name -PropertyType Binary -Value ([byte[]](0, 0, 0, 0)) -Force -ErrorAction SilentlyContinue | Out-Null
            } catch { }
        }
        Write-Ok 'ShadowPlay/overlay caps set off (registry)'
    }

    # App-side overlay preference hints
    foreach ($p in @(
        'HKCU:\Software\NVIDIA Corporation\NVIDIA App',
        'HKCU:\Software\NVIDIA Corporation\Global\GFExperience'
    )) {
        if (-not (Test-Path $p)) { try { New-Item -Path $p -Force | Out-Null } catch { continue } }
        Set-ItemProperty -Path $p -Name 'OverlayEnabled' -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $p -Name 'EnableOverlay' -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
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

    Write-Ok 'NVIDIA App/GFE background clients and overlay disabled; installed files and NVIDIA audio preserved'
}

function Test-NvidiaOverlayDisabled {
    $issues = New-Object System.Collections.Generic.List[string]

    $overlayProcesses = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -match '(?i)^NVIDIA (Overlay|Share)$|^nvsphelper(64)?$'
    })
    if ($overlayProcesses.Count -gt 0) {
        [void]$issues.Add("Overlay processes still running: $($overlayProcesses.ProcessName -join ', ')")
    }

    foreach ($path in @(
        'HKCU:\Software\NVIDIA Corporation\NVIDIA App',
        'HKCU:\Software\NVIDIA Corporation\Global\GFExperience'
    )) {
        $properties = Get-ItemProperty -LiteralPath $path -ErrorAction SilentlyContinue
        foreach ($name in @('OverlayEnabled', 'EnableOverlay')) {
            $property = if ($properties) { $properties.PSObject.Properties[$name] } else { $null }
            if (-not $property -or [int]$property.Value -ne 0) {
                [void]$issues.Add("Overlay preference is not disabled: $path\\$name")
            }
        }
    }

    $capsPath = 'HKCU:\Software\NVIDIA Corporation\Global\ShadowPlay\NVSPCAPS'
    $caps = Get-ItemProperty -LiteralPath $capsPath -ErrorAction SilentlyContinue
    foreach ($name in @('RecEnabled', 'DwmEnabled', 'DwmDvrEnabledV1', 'DisplayRecordingIndicator', 'DisplayGamecastIndicator', 'GameStreamPortal')) {
        $property = if ($caps) { $caps.PSObject.Properties[$name] } else { $null }
        $bytes = if ($property) { @($property.Value) } else { @() }
        if ($bytes.Count -eq 0 -or @($bytes | Where-Object { [int]$_ -ne 0 }).Count -gt 0) {
            [void]$issues.Add("ShadowPlay capture preference is not disabled: $name")
        }
    }

    return [pscustomobject]@{
        Ok     = [bool]($issues.Count -eq 0)
        Issues = @($issues)
    }
}

function Install-NvidiaApp {
    # Optional - display settings use NVAPI, not the App.
    Write-Step 'Installing NVIDIA App (optional; display prefs use NVAPI)...'
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $winget) {
        Write-Warn 'winget not available'
        return $false
    }
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & winget install --id XP8CLZL93F5Z4P -e --source msstore --accept-package-agreements --accept-source-agreements --silent 2>&1 | Out-Null
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    if ($code -eq 0 -or (Test-NvidiaAppInstalled)) {
        Write-Ok 'NVIDIA App present'
        [void](Disable-NvidiaOverlay)
        return $true
    }
    Write-Warn "NVIDIA App winget exit $code"
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

function Get-LatestGameReadyDriver {
    # Always query NVIDIA for newest Game Ready (desktop Win10/11 x64 DCH WHQL).
    # psid/pfid picks a current desktop matrix; Version is the same GRD branch for 20-50 series.
    $urls = @(
        'https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&psid=129&pfid=995&osID=57&languageCode=1033&beta=0&isWHQL=1&dltype=-1&dch=1&upCRD=0&qnf=0&ctk=null&windowsVersion=10.0&windowsArchitecture=64bit',
        'https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&psid=120&pfid=929&osID=57&languageCode=1033&beta=0&isWHQL=1&dltype=-1&dch=1&upCRD=0&qnf=0'
    )
    foreach ($url in $urls) {
        try {
            $r = Invoke-RestMethod -Uri $url -Headers @{ 'User-Agent' = 'OptiHub-Nvidia/1.2' } -TimeoutSec 25
            if (-not $r -or $r.Success -ne '1') { continue }
            $info = $r.IDS[0].downloadInfo
            if (-not $info -or [string]$info.Version -notmatch '^\d{3}\.\d{2}$') { continue }
            return [pscustomobject]@{
                Version     = [string]$info.Version
                DownloadUrl = [uri]::UnescapeDataString([string]$info.DownloadURL)
                Name        = [uri]::UnescapeDataString([string]$info.Name)
                ReleaseDate = [string]$info.ReleaseDateTime
                Size        = [string]$info.DownloadURLFileSize
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
    Write-Step 'Installing NanaZip (extracts NVIDIA package for OptiHub Clean Driver)...'
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
            Start-BitsTransfer -Source $Url -Destination $tmp -DisplayName "OptiHub NVIDIA $Version" -Description 'Game Ready driver'
            $usedBits = $true
        } catch {
            $usedBits = $false
        }
        if (-not $usedBits) {
            $wc = New-Object System.Net.WebClient
            $wc.Headers['User-Agent'] = 'OptiHub-Nvidia/1.2'
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
    Write-Step 'Extracting official package for OptiHub Clean Driver (NanaZip)...'
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

function Install-OptiHubCleanDriver {
    param(
        [Parameter(Mandatory)][string]$DownloadUrl,
        [Parameter(Mandatory)][string]$Version
    )
    # OptiHub Clean Driver (NVCleanstall-class, OUR rules - better for silent):
    #  1) Official Game Ready once (cached)
    #  2) Extract (folders stay on disk so setup.exe resolves; we do NOT install bloat)
    #  3) Silent CLEAN install of Display.Driver ONLY - existing NVIDIA audio is left untouched
    #  4) Post-install expert tweaks (MSI High, telemetry off, Ansel off)
    #  5) Continue pipeline (no forced reboot)
    Write-Step "OptiHub Clean Driver install ($Version) - Display.Driver component only"
    Write-HubProgress 20 "OptiHub Clean Driver $Version..."

    $package = Coerce-StringPath (Download-NvidiaDriverPackage -Url $DownloadUrl -Version $Version)
    if (-not $package -or -not (Test-Path -LiteralPath $package)) {
        Write-Warn "Driver package path invalid after download: $package"
        return @{ Success = $false; ExitCode = -1; Error = 'bad-package-path'; Method = 'optihub-clean' }
    }
    Write-Ok "Package file: $package"

    $extractDir = Join-Path $DriverCacheDir "extract-$Version"
    $setup = Coerce-StringPath (Expand-NvidiaDriverPackage -PackageExe $package -DestDir $extractDir)

    $exitCode = -1
    if ($setup -and (Test-Path -LiteralPath $setup)) {
        $setupDir = Split-Path -Parent $setup
        # Install only Display.Driver. Do not stop/disable any existing NVIDIA audio device.
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
        return @{ Success = $false; ExitCode = -1; Error = 'extract-failed'; Method = 'optihub-clean' }
    }

    # NVIDIA's documented codes: 0 = success, 1 = success/restart required.
    $okCodes = @(0, 1)
    if ($okCodes -contains $exitCode) {
        Start-Sleep -Seconds 2
        $installedVersion = Convert-WindowsDriverToNvidia (Get-WindowsDriverVersionString)
        if ($exitCode -eq 0 -and $installedVersion -and
            (Compare-NvidiaVersion $installedVersion $Version) -lt 0) {
            Write-Warn "Installer returned success, but driver verification found $installedVersion instead of $Version"
            return @{
                Success = $false; ExitCode = $exitCode; Error = 'version-verification-failed'
                ExpectedVersion = $Version; InstalledVersion = $installedVersion
                Package = $package; Setup = $setup; Method = 'optihub-clean'
            }
        }
        $rebootRequired = ($exitCode -eq 1)
        Write-Ok "OptiHub Clean Driver finished (exit $exitCode, restart required=$rebootRequired)"
        return @{
            Success          = $true
            ExitCode         = $exitCode
            RebootRequired   = $rebootRequired
            InstalledVersion = $installedVersion
            Package          = $package
            Setup            = $setup
            Method           = 'optihub-clean'
        }
    }
    $hex = 'unknown'
    try { $hex = ('{0:X8}' -f [uint32]([int]$exitCode)) } catch { }
    Write-Warn "OptiHub Clean Driver setup exit $exitCode (0x$hex)"
    return @{
        Success  = $false
        ExitCode = $exitCode
        Package  = $package
        Setup    = $setup
        Method   = 'optihub-clean'
    }
}
function Apply-OptiHubDriverInstallTweaks {
    # Post-install expert set (NVCleanstall-equivalent where possible, only tweaks that matter):
    #  KEEP: MSI High, disable telemetry, disable Ansel/NvCamera,
    #        quiet auto-download / telemetry consent RIDs
    #  SKIP: NVIDIA audio changes, unsigned-driver accept (install-time only),
    #        EAC-compatible strip method (install-time INF only - not safe on stock silent setup),
    #        Disable HDCP (unsigned/risky; skip), fake OptiHub-only tags that drivers ignore
    Write-Step 'Applying OptiHub driver expert tweaks (MSI High, telemetry off, Ansel off)...'

    # --- MSI High (real interrupt mode tweak) ---
    $msiCount = 0
    $msiCandidates = 0
    try {
        $pci = 'HKLM:\SYSTEM\CurrentControlSet\Enum\PCI'
        if (Test-Path $pci) {
            Get-ChildItem $pci -ErrorAction SilentlyContinue | Where-Object {
                $_.PSChildName -match 'VEN_10DE'
            } | ForEach-Object {
                Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
                    $dev = $_.PSPath
                    # Only the display-class GPU node. Do not alter NVIDIA audio/USB devices.
                    $device = Get-ItemProperty -LiteralPath $dev -ErrorAction SilentlyContinue
                    if ($device.Class -ne 'Display' -and
                        $device.ClassGUID -ne '{4d36e968-e325-11ce-bfc1-08002be10318}') {
                        return
                    }
                    $msiCandidates++
                    $msiKey = Join-Path $dev 'Device Parameters\Interrupt Management\MessageSignaledInterruptProperties'
                    $aff = Join-Path $dev 'Device Parameters\Interrupt Management\Affinity Policy'
                    try {
                        if (-not (Test-Path $msiKey)) { New-Item -Path $msiKey -Force -ErrorAction Stop | Out-Null }
                        New-ItemProperty -LiteralPath $msiKey -Name 'MSISupported' -Value 1 -PropertyType DWord -Force -ErrorAction Stop | Out-Null
                        if (-not (Test-Path $aff)) { New-Item -Path $aff -Force -ErrorAction Stop | Out-Null }
                        # 3 = High priority (NVCleanstall MSI High)
                        New-ItemProperty -LiteralPath $aff -Name 'DevicePriority' -Value 3 -PropertyType DWord -Force -ErrorAction Stop | Out-Null
                        $msiValue = (Get-ItemProperty -LiteralPath $msiKey -ErrorAction Stop).MSISupported
                        $priorityValue = (Get-ItemProperty -LiteralPath $aff -ErrorAction Stop).DevicePriority
                        if ($msiValue -eq 1 -and $priorityValue -eq 3) { $msiCount++ }
                        else { Write-Warn "MSI verification failed for $($device.DeviceDesc)" }
                    } catch {
                        Write-Warn "MSI High failed for $($device.DeviceDesc): $($_.Exception.Message)"
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
                foreach ($rid in @('EnableRID44231', 'EnableRID64640', 'EnableRID66610', 'EnableRID73779', 'EnableRID73780')) {
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
        Write-Ok 'Installer telemetry / advertising RIDs off'
    } catch { }

    Disable-NvidiaTelemetry
    Write-Ok 'Expert tweaks done (MSI High, telemetry off, Ansel off; NVIDIA audio preserved)'
}

function Test-OptiHubDriverInstallTweaks {
    # Signals that OptiHub clean install + expert tweaks actually landed.
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

    # MSI: if the key exists and is 0, fail; if 1, pass; if missing, ignore
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
                    if ($device.Class -ne 'Display' -and
                        $device.ClassGUID -ne '{4d36e968-e325-11ce-bfc1-08002be10318}') {
                        return
                    }
                    $msiSeen++
                    $msiKey = Join-Path $_.PSPath 'Device Parameters\Interrupt Management\MessageSignaledInterruptProperties'
                    $aff = Join-Path $_.PSPath 'Device Parameters\Interrupt Management\Affinity Policy'
                    $v = (Get-ItemProperty -LiteralPath $msiKey -ErrorAction SilentlyContinue).MSISupported
                    $priority = (Get-ItemProperty -LiteralPath $aff -ErrorAction SilentlyContinue).DevicePriority
                    if ($v -ne 1 -or $priority -ne 3) { $msiGaps++ }
                }
            }
        }
    } catch { }
    if ($msiSeen -gt 0) {
        if ($msiGaps -eq 0) { [void]$oks.Add("MSI High verified on $msiSeen NVIDIA display device(s)") }
        else { [void]$issues.Add("MSI High missing on $msiGaps of $msiSeen NVIDIA display device(s)") }
    } else {
        [void]$issues.Add('NVIDIA display-class PCI device was not found for MSI verification')
    }

    # OptiHub remembered this exact driver version as tweaked
    $remembered = $false
    if (Test-Path $StatePath) {
        try {
            $st = Get-Content $StatePath -Raw -Encoding UTF8 | ConvertFrom-Json
            $win = Get-WindowsDriverVersionString
            $cur = Convert-WindowsDriverToNvidia $win
            if ($st.driverTweaksVerified -and $st.driverTweaksVersion -and $cur -and $st.driverTweaksVersion -eq $cur) {
                $remembered = $true
                [void]$oks.Add("OptiHub recorded tweaks for driver $cur")
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
    param([bool]$Force)

    $winVer = Get-WindowsDriverVersionString
    $currentNv = Convert-WindowsDriverToNvidia $winVer
    Write-Ok "Installed Windows driver string: $winVer"
    Write-Ok "Decoded NVIDIA version: $(if($currentNv){$currentNv}else{'unknown'})"

    Write-Step 'Checking NVIDIA for the newest Game Ready driver...'
    $latest = Get-LatestGameReadyDriver
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
        Write-Ok "Newest Game Ready: $latestVer ($($latest.ReleaseDate)) size $($latest.Size)"
        if ($dl) { Write-Ok "Download: $dl" }
        if (-not $currentNv) {
            $versionBehind = $true
            Write-Warn 'Could not decode installed version'
        } elseif ((Compare-NvidiaVersion $currentNv $latestVer) -lt 0) {
            $versionBehind = $true
            Write-Warn "Outdated: $currentNv < newest $latestVer"
        } else {
            Write-Ok "Version is newest (or newer): $currentNv"
        }
    }

    Write-Step 'Checking OptiHub Clean Driver tweak signals...'
    $tweaks = Test-OptiHubDriverInstallTweaks
    foreach ($o in $tweaks.OkSignals) { Write-Ok "Tweaks signal: $o" }
    foreach ($i in $tweaks.Issues) { Write-Warn "Tweaks gap: $i" }
    if ($tweaks.Ok) {
        Write-Ok 'OptiHub driver tweaks look present (or recorded for this version)'
    } else {
        Write-Warn 'Stock-style driver signals - OptiHub will apply clean-driver tweaks'
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
        $reason = 'Driver version is current, but OptiHub clean-driver tweaks are not detected'
    }

    if (-not $needInstall) {
        return @{
            Ran             = $false
            NeedsUpdate     = $false
            NeedsRetweak    = $false
            TweaksOk        = $true
            CurrentVersion  = $currentNv
            LatestVersion   = $latestVer
            WindowsVersion  = $winVer
            DownloadUrl     = $dl
            Tweaks          = $tweaks
            Method          = 'none'
        }
    }

    Write-Ok $reason

    # Version is current but stock-style signals: apply MSI/privacy in-place (no re-download).
    if (-not $versionBehind -and -not $tweaks.Ok -and -not $Force) {
        Write-Step 'Applying OptiHub tweaks in-place (no driver download)'
        try {
            Apply-OptiHubDriverInstallTweaks
            $verifiedTweaks = Test-OptiHubDriverInstallTweaks
            if (-not $verifiedTweaks.Ok) {
                throw "Tweak verification failed: $($verifiedTweaks.Issues -join '; ')"
            }
            return @{
                Ran             = $false
                NeedsUpdate     = $false
                NeedsRetweak    = $false
                TweaksOk        = $true
                Reason          = $reason
                CurrentVersion  = $currentNv
                LatestVersion   = $latestVer
                WindowsVersion  = $winVer
                DownloadUrl     = $dl
                Tweaks          = $verifiedTweaks
                Method          = 'in-place-tweaks'
            }
        } catch {
            Write-Warn "In-place tweaks failed: $($_.Exception.Message)"
        }
    }

    # Full OptiHub Clean Driver install (our NVCleanstall-class pipeline)
    if (-not $dl) {
        Write-Warn 'No official download URL from NVIDIA API - cannot run OptiHub Clean Driver'
        return @{
            Ran             = $true
            NeedsUpdate     = $true
            NeedsRetweak    = (-not $versionBehind)
            TweaksOk        = $false
            Reason          = $reason
            CurrentVersion  = $currentNv
            LatestVersion   = $latestVer
            WindowsVersion  = $winVer
            DownloadUrl     = $dl
            Method          = 'failed-no-url'
            Tweaks          = $tweaks
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
            $install = Install-OptiHubCleanDriver -DownloadUrl $dl -Version $targetVer
        }
    } catch {
        Write-Warn $_.Exception.Message
        $install = @{ Success = $false; Error = $_.Exception.Message }
    }

    $install = Coerce-Hashtable $install
    if ($install -and $install.Success) {
        $postTweaks = $null
        try {
            Apply-OptiHubDriverInstallTweaks
            $postTweaks = Test-OptiHubDriverInstallTweaks
        } catch {
            Write-Warn "Post-install tweaks: $($_.Exception.Message)"
        }
        if (-not $postTweaks -or -not $postTweaks.Ok) {
            $gaps = if ($postTweaks) { $postTweaks.Issues -join '; ' } else { 'verification did not run' }
            Write-Warn "Driver installed, but maximum-performance tweak verification failed: $gaps"
            return @{
                Ran = $true; NeedsUpdate = $false; NeedsRetweak = $true; TweaksOk = $false
                Reason = $reason; CurrentVersion = $currentNv; LatestVersion = $latestVer
                WindowsVersion = $winVer; DownloadUrl = $dl; Method = 'failed-tweaks'
                Install = $install; Tweaks = $postTweaks; ContinuePipeline = $false
            }
        }
        $postWindowsVersion = Get-WindowsDriverVersionString
        $postNvidiaVersion = Convert-WindowsDriverToNvidia $postWindowsVersion
        $rebootRequired = [bool]$install.RebootRequired
        if ($rebootRequired) {
            Write-Ok 'OptiHub Clean Driver installed; Windows requires a restart before profile import.'
            Write-HubProgress 70 'Driver installed - restart required'
        } else {
            Write-Ok 'OptiHub Clean Driver complete. Continuing with the 3D profile and display preferences.'
            Write-HubProgress 70 'Clean driver installed - continuing pipeline'
        }
        return @{
            Ran             = $true
            NeedsUpdate     = $false
            NeedsRetweak    = $false
            TweaksOk        = $true
            Reason          = $reason
            CurrentVersion  = $(if ($postNvidiaVersion) { $postNvidiaVersion } else { $currentNv })
            LatestVersion   = $latestVer
            WindowsVersion  = $(if ($postWindowsVersion) { $postWindowsVersion } else { $winVer })
            DownloadUrl     = $dl
            Method          = 'optihub-clean'
            Install         = $install
            Tweaks          = $postTweaks
            RebootRequired  = $rebootRequired
            ContinuePipeline = (-not $rebootRequired)
        }
    }

    # No third-party GUI fallback - surface clear failure so user can re-run after network/disk issues.
    Write-Warn 'OptiHub Clean Driver did not complete. Check disk space, close games, re-run Apply as Administrator.'
    if ($dl) { Write-Ok "Package URL (for manual retry later): $dl" }
    return @{
        Ran             = $true
        NeedsUpdate     = $true
        NeedsRetweak    = (-not $versionBehind)
        TweaksOk        = $false
        Reason          = $reason
        CurrentVersion  = $currentNv
        LatestVersion   = $latestVer
        WindowsVersion  = $winVer
        DownloadUrl     = $dl
        Method          = 'failed-clean'
        Install         = $install
        Tweaks          = $tweaks
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
        '*FrameView*',
        'NvDriverUpdateCheckDaily*',
        'NVIDIA GeForce Experience SelfUpdate*'
    )
    $disabled = 0
    Get-ScheduledTask -ErrorAction SilentlyContinue | ForEach-Object {
        $tn = $_.TaskName
        $tp = $_.TaskPath
        $full = "$tp$tn"
        $hit = $false
        foreach ($pat in $taskPatterns) {
            if ($tn -like $pat -or $full -like $pat) { $hit = $true; break }
        }
        if (-not $hit) { return }
        # Keep essential display tasks
        if ($tn -match '(?i)Display|LocalSystem') { return }
        try {
            Disable-ScheduledTask -TaskName $tn -TaskPath $tp -ErrorAction Stop | Out-Null
            $disabled++
            Write-Ok "Task disabled: $full"
        } catch { }
    }
    if ($disabled -eq 0) { Write-Ok 'No telemetry tasks matched (already clean or names differ)' }

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

    $background = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -match '(?i)^NVIDIA (App|Overlay|Share|Web Helper)$|^GFExperience$|^nvsphelper(64)?$'
    })
    if ($background.Count -gt 0) {
        [void]$issues.Add("Background clients still running: $($background.ProcessName -join ', ')")
    }

    $taskPatterns = @('*NvTm*', '*NVIDIA*Telemetry*', '*NvProfile*', '*NvNode*', '*NvBackend*', '*NVIDIA*App*', '*FrameView*', 'NvDriverUpdateCheckDaily*', 'NVIDIA GeForce Experience SelfUpdate*')
    Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.State -ne 'Disabled' } | ForEach-Object {
        $full = "$($_.TaskPath)$($_.TaskName)"
        if ($_.TaskName -match '(?i)Display|LocalSystem') { return }
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

function Set-NvidiaDisplayPreferences {
    # Sticky display path
    # - NVTweak stamp all monitors (GPU / NoScale / Override / Full)
    # - NVAPI: keep current resolution + max supported Hz + Full RGB
    # - No Control Panel mouse/keyboard automation
    # - Logon scheduled task re-stamps registry
    Write-Step 'Display prefs: NVAPI + registry (no Control Panel automation)...'
    $applied = New-Object System.Collections.Generic.List[string]
    $success = $false

    Get-Process -Name 'nvcplui' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    $dispScript = Join-Path $Root 'OptiHub-Display-Apply.ps1'
    if (-not (Test-Path -LiteralPath $dispScript)) {
        Write-Warn "Missing $dispScript"
        [void]$applied.Add('Display apply script missing')
    } else {
        Write-HubProgress 90 'Display: all monitors (res/Hz + GPU/Override)...'
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & $dispScript 2>&1 | ForEach-Object {
                $s = "$_"
                if ($s) {
                    Write-Host $s
                    if ($env:OPTIHUB_LOG) {
                        try { Add-Content -LiteralPath $env:OPTIHUB_LOG -Value $s -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
                    }
                }
            }
            $code = 0
            if ($null -ne $LASTEXITCODE) { $code = [int]$LASTEXITCODE }
            if ($code -eq 0) {
                $success = $true
                [void]$applied.Add('Active NVIDIA displays: current resolution at max Hz + Full RGB + GPU/NoScale/Override')
                Write-Ok 'Display settings applied (sticky path)'
            } else {
                [void]$applied.Add("Display apply exit $code")
                Write-Warn "Display apply exit $code"
            }
        } catch {
            Write-Warn "Display apply failed: $($_.Exception.Message)"
            [void]$applied.Add("Display apply error: $($_.Exception.Message)")
        } finally {
            $ErrorActionPreference = $prev
            Get-Process -Name 'nvcplui' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        }
    }

    $pref = Join-Path $StateDir 'nvidia-display-prefs.json'
    $obj = [ordered]@{
        colorSource         = 'NVIDIA (User policy via NVAPI)'
        outputColorFormat   = 'RGB'
        outputDynamicRange  = 'Full'
        outputColorDepth    = 'highest supported per display'
        resolutionRefresh   = 'current resolution + highest supported Hz per monitor'
        performScalingOn    = 'GPU'
        scalingMode         = 'No scaling'
        overrideGameScaling = $true
        appliedVia          = 'OptiHub-Display-Apply + OptiHub.NvDisplay'
        flow                = 'NVTweak -> soft container refresh -> NVAPI modes/color/path -> final registry stamp'
        note                = 'No Control Panel mouse or keyboard automation is used.'
        success             = $success
    }
    [IO.File]::WriteAllText($pref, ($obj | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    [void]$applied.Add('Saved OptiHub display preference manifest')

    foreach ($a in $applied) { Write-Ok $a }
    return @{
        Success = [bool]$success
        Details = [string[]]@($applied.ToArray())
    }
}

function Save-State([hashtable]$State) {
    if (-not (Test-Path $StateDir)) { New-Item -ItemType Directory -Path $StateDir -Force | Out-Null }
    [IO.File]::WriteAllText($StatePath, ($State | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}

function Invoke-Repair {
    Write-Step 'Repair: clear OptiHub NVIDIA state marker'
    if (Test-Path $StatePath) {
        Remove-Item $StatePath -Force -ErrorAction SilentlyContinue
        Write-Ok 'Cleared nvidia-optimizer.json'
    }
    Write-Ok 'Driver profiles and NVIDIA App installs are left intact. Re-apply to re-import OptiHub pack.'
}

# --- main ---
try {
    Write-HubProgress 5 'Starting NVIDIA Optimizer...'
    Write-Ok "OptiHub NVIDIA pack v$Script:NvidiaOptVersion"

    if ($Repair) {
        Write-HubProgress 40 'Repairing...'
        Invoke-Repair
        Write-HubProgress 100 'Repair complete'
        exit 0
    }

    # Force an array in Windows PowerShell 5.1; a single PSCustomObject does
    # not expose the synthetic .Count property that PowerShell 7 provides.
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
        throw 'Notebook/Laptop GPU detected. OptiHub will not use desktop driver metadata or packages on mobile hardware. Install the official NVIDIA notebook driver, then rerun with -SkipDriver to apply only the profile/display/debloat stages.'
    }
    if ($isNotebookGpu) {
        Write-Warn 'Notebook/Laptop GPU: automatic driver lookup is explicitly disabled; -SkipDriver was requested.'
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
    #  3) Then client stack: stop background clients, debloat, and apply display settings

    # --- 1) Newest driver (OptiHub Clean Driver = clean install; continue when no restart is needed) ---
    $driverInfo = @{ Ran = $false; NeedsUpdate = $false; TweaksOk = $true; Method = 'none' }
    if (-not $SkipDriver) {
        Write-HubProgress 20 'Checking for newest Game Ready driver...'
        $driverInfo = Coerce-Hashtable (Start-DriverUpdateIfNeeded -Force:([bool]$ForceDriver))
        if (-not $driverInfo) { $driverInfo = @{ Ran = $false; NeedsUpdate = $false; TweaksOk = $true; Method = 'none' } }

        $method = [string]$driverInfo.Method
        if ($method -in @('failed-clean', 'failed-no-url', 'failed-tweaks')) {
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
            }
            Write-Warn 'The NVIDIA driver/performance-tweak stage did not finish. Fix the issue above and Apply again.'
            Write-HubProgress 100 'Driver optimization failed'
            Write-Output 'DONE - NVIDIA driver optimization failed. See log, then Apply again.'
            exit 1
        }

        if ([bool]$driverInfo.RebootRequired) {
            Save-State @{
                version            = $Script:NvidiaOptVersion
                appliedUtc         = (Get-Date).ToUniversalTime().ToString('o')
                gpuName            = $primary.Name
                driver             = $driverInfo.WindowsVersion
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

        if ($method -eq 'optihub-clean' -and $driverInfo.Ran) {
            Write-Ok 'Clean driver installed - continuing into the 3D profile and display preferences'
            Write-HubProgress 35 'Clean driver OK - applying 3D profile next...'
        }
    } else {
        Write-Ok 'Driver check skipped (-SkipDriver)'
    }

    # --- 2) 3D Base Profile (right after driver) ---
    $nip = $null
    $npi = $null
    $profileImport = $null
    $profileApplied = $false
    $profileSha256 = ''
    $profilePackVersion = ''
    $profileVersionPath = Join-Path $ProfilesDir 'PROFILE_VERSION'
    if (Test-Path -LiteralPath $profileVersionPath) {
        $profilePackVersion = (Get-Content -LiteralPath $profileVersionPath -Raw -ErrorAction SilentlyContinue).Trim()
    }
    if (-not $SkipProfile) {
        if ([string]::IsNullOrWhiteSpace($profilePackVersion)) {
            throw 'NVIDIA profile pack version is missing; refusing an unverifiable import.'
        }
        $nip = Get-ProfileFile $seriesId $useGsync
        if (-not $nip) { throw "Missing profile for series $seriesId (G-SYNC=$useGsync)" }
        Assert-OptiHubNipProfile -Path $nip -UseGsync $useGsync
        $profileSha256 = (Get-FileHash -LiteralPath $nip -Algorithm SHA256 -ErrorAction Stop).Hash
        Write-Ok "Profile: $(Split-Path $nip -Leaf)"
        Write-HubProgress 40 'Profile Inspector (3D settings)...'
        Write-HubProgress 48 'Importing 3D Base Profile (silent)...'
        $profileImport = Import-OptiHubNipProfile -NipPath $nip -TimeoutSec 90
        $npi = $profileImport.NpiPath
        $profileApplied = [bool]$profileImport.Success
        if (-not $profileApplied) {
            throw '3D Base Profile was NOT applied (silent import did not succeed).'
        }
    } else {
        Write-Ok '3D profile import skipped (-SkipProfile)'
    }

    # --- 3) Client stack: preserve the user's NVIDIA App choice, ensure CPL, disable overlay ---
    # Display preferences use NVAPI and driver-created storage, never UI automation.
    Write-HubProgress 64 'Checking optional NVIDIA Control Panel...'
    $cplOk = Ensure-NvidiaControlPanel

    $appInstalled = Test-NvidiaAppInstalled
    $wantApp = [bool]$InstallApp -and -not [bool]$SkipApp
    if ($wantApp) {
        Write-HubProgress 70 'NVIDIA App (opt-in only)...'
        if ($SkipDownload -and -not $appInstalled) {
            Write-Warn 'NVIDIA App not installed and -SkipDownload set'
        } else {
            [void](Install-NvidiaApp)
            $appInstalled = Test-NvidiaAppInstalled
        }
    } else {
        Write-Ok "NVIDIA App left unchanged (currently $(if ($appInstalled) { 'installed' } else { 'not installed' }))"
    }

    Write-HubProgress 76 'Disabling NVIDIA Overlay...'
    Disable-NvidiaOverlay

    Write-HubProgress 82 'Privacy / debloat...'
    Disable-NvidiaTelemetry
    $overlayResult = Test-NvidiaOverlayDisabled
    foreach ($issue in $overlayResult.Issues) { Write-Warn "Overlay verification: $issue" }
    $debloatResult = Test-NvidiaPerformanceDebloat
    foreach ($issue in $debloatResult.Issues) { Write-Warn "Debloat verification: $issue" }

    Write-HubProgress 90 'Display color / scaling (NVAPI)...'
    $dispResult = Coerce-Hashtable (Set-NvidiaDisplayPreferences)
    if (-not $dispResult) {
        $dispResult = @{ Success = $false; Details = @('Display helper returned no result') }
    }

    Write-HubProgress 94 'Saving status...'
    # Remember this driver version as tweak-OK so detect won't re-prompt until the version changes.
    $tweaksVer = $null
    $driverTweaksVerified = (-not [bool]$SkipDriver) -and [bool]$driverInfo.TweaksOk
    if ($driverTweaksVerified -and $driverInfo -and $driverInfo.CurrentVersion) {
        $tweaksVer = [string]$driverInfo.CurrentVersion
    } elseif ($driverTweaksVerified) {
        try {
            $tweaksVer = Convert-WindowsDriverToNvidia (Get-WindowsDriverVersionString)
        } catch { $tweaksVer = $null }
    }
    if ($driverTweaksVerified -and [string]::IsNullOrWhiteSpace([string]$tweaksVer)) {
        Write-Warn 'Driver tweaks were live-verified, but the driver version could not be recorded; status will fail closed.'
        $driverTweaksVerified = $false
    }
    if (-not $SkipDriver -and -not $driverTweaksVerified) {
        throw 'Driver tweaks could not be tied to the active driver version; refusing to record a successful NVIDIA pass.'
    }
    $profileDriverVersion = $null
    if ($profileApplied) {
        try { $profileDriverVersion = Convert-WindowsDriverToNvidia (Get-WindowsDriverVersionString) } catch { }
    }
    if ($profileApplied -and [string]::IsNullOrWhiteSpace([string]$profileDriverVersion)) {
        throw 'The active driver version could not be recorded after profile import; refusing to mark the profile applied.'
    }
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
        npiPath             = $npi
        nvidiaApp           = $appInstalled
        nvidiaControlPanel  = [bool]$cplOk
        displayPrefs        = [bool]$dispResult.Success
        displayMethod       = 'nvapi'
        displayDetails      = $dispResult.Details
        debloatApplied      = [bool]$debloatResult.Ok
        overlayDisabled     = [bool]$overlayResult.Ok
        driverUpdatePass    = $driverInfo
        applyInProgress     = $false
        pendingAfterDriver  = $false
        driverTweaksVerified = [bool]$driverTweaksVerified
        driverTweaksVersion = $tweaksVer
    }

    if (-not [bool]$dispResult.Success) {
        throw 'The 3D profile was applied, but NVIDIA display preferences could not be verified. Check the log and Apply again.'
    }
    if (-not [bool]$debloatResult.Ok) {
        throw "The performance profile and display settings were applied, but NVIDIA background debloat verification failed: $($debloatResult.Issues -join '; ')"
    }
    if (-not [bool]$overlayResult.Ok) {
        throw "The performance profile and display settings were applied, but NVIDIA overlay verification failed: $($overlayResult.Issues -join '; ')"
    }

    Write-Ok 'NVIDIA Optimizer finished'
    Write-Ok 'In Control Panel: Display > Adjust desktop size and position = GPU + No scaling + Override (both monitors).'
    Write-Ok 'Display > Change resolution: Output color to NVIDIA settings / Full RGB when listed.'
    if ($driverInfo.Method -eq 'optihub-clean') {
        Write-Ok 'Clean install completed in one pass (driver + 3D + NVAPI display). No forced reboot.'
    }
    Write-HubProgress 100 'Completed successfully'
    Write-Output "DONE - NVIDIA $seriesId$(if($useGsync){' G-SYNC'}else{' max FPS / latency'}) (driver -> 3D profile -> NVAPI display)"
    exit 0
} catch {
    Write-Err $_.Exception.Message
    Write-HubProgress 100 'Failed'
    exit 1
}
