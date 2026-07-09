# OptiHub NVIDIA Optimizer
# - Apply series + G-SYNC OptiHub Base Profile via Profile Inspector
# - Ensure NVIDIA App (optional download) + privacy/debloat
# - Best-effort display: full RGB, high bpc preference, GPU scaling notes in state
#
#   Nvidia-Optimizer.ps1
#   Nvidia-Optimizer.ps1 -Gsync
#   Nvidia-Optimizer.ps1 -Repair
#   Nvidia-Optimizer.ps1 -Series 40 -Gsync -SkipApp

param(
    [switch]$Gsync,
    [ValidateSet('', '10', '20', '30', '40', '50')]
    [string]$Series = '',
    [switch]$Repair,
    [switch]$NonInteractive,
    [switch]$SkipDownload,
    [switch]$SkipApp,
    [switch]$SkipProfile,
    [switch]$SkipDriver,
    [switch]$ForceDriver
)

$ErrorActionPreference = 'Stop'
$Script:NvidiaOptVersion = '1.2.0'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProfilesDir = Join-Path $Root 'profiles'
$StateDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'OptiHub'
$StatePath = Join-Path $StateDir 'nvidia-optimizer.json'
$NpiDir = Join-Path $StateDir 'nvidia-profile-inspector'
$NpiExeName = 'nvidiaProfileInspector.exe'

function Write-HubProgress([int]$Percent, [string]$Status) {
    $p = [Math]::Max(0, [Math]::Min(100, $Percent))
    $line = "OPTIHUB_PROGRESS:$p|$Status"
    Write-Output $line
    if ($env:OPTIHUB_LOG) {
        try { Add-Content -LiteralPath $env:OPTIHUB_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    }
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
    $list = New-Object System.Collections.Generic.List[object]
    try {
        Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | ForEach-Object {
            $n = [string]$_.Name
            if ($n -match '(?i)nvidia|geforce|rtx|gtx|quadro|titan') {
                $list.Add([pscustomobject]@{ Name = $n; Driver = [string]$_.DriverVersion })
            }
        }
    } catch { }
    return @($list)
}

function Get-GpuSeriesFromName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) { return $null }
    if ($Name -match '(?i)\b(?:RTX|GTX)\s*([1-5])0\d{2}\b') { return $Matches[1] + '0' }
    if ($Name -match '(?i)\b([1-5])0\d{2}\s*(?:Ti|SUPER)?\b') { return $Matches[1] + '0' }
    if ($Name -match '(?i)\b16\d{2}\b') { return '20' }
    return $null
}

function Get-ProfileFile([string]$SeriesId, [bool]$UseGsync) {
    $name = if ($UseGsync) { "$SeriesId Series G-SYNC.nip" } else { "$SeriesId Series.nip" }
    $path = Join-Path $ProfilesDir $name
    if (Test-Path -LiteralPath $path) { return $path }
    return $null
}

function Find-NpiExe {
    $candidates = @(
        (Join-Path $NpiDir $NpiExeName),
        (Join-Path $Root "tools\$NpiExeName"),
        (Join-Path $env:USERPROFILE "Documents\nvidiaProfileInspector\$NpiExeName")
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath $c)) { return $c }
    }
    # winget install location (best-effort)
    $pf = @(
        (Join-Path ${env:ProgramFiles(x86)} 'NVIDIA Corporation\NVIDIA Profile Inspector\nvidiaProfileInspector.exe'),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages')
    )
    if (Test-Path $pf[1]) {
        $hit = Get-ChildItem $pf[1] -Recurse -Filter $NpiExeName -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

function Install-Npi {
    Write-Step 'Installing NVIDIA Profile Inspector...'
    New-Item -ItemType Directory -Force -Path $NpiDir | Out-Null

    # Prefer winget package
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        try {
            Write-Ok 'Trying winget Orbmu2k.nvidiaProfileInspector...'
            $prev = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            & winget install --id Orbmu2k.nvidiaProfileInspector -e --accept-package-agreements --accept-source-agreements --silent 2>&1 | Out-Null
            $ErrorActionPreference = $prev
            $found = Find-NpiExe
            if ($found) { Write-Ok "NPI via winget: $found"; return $found }
        } catch { Write-Warn "winget NPI: $($_.Exception.Message)" }
    }

    $api = 'https://api.github.com/repos/Orbmu2k/nvidiaProfileInspector/releases/latest'
    $headers = @{ 'User-Agent' = 'OptiHub-Nvidia/1.1'; 'Accept' = 'application/vnd.github+json' }
    $rel = Invoke-RestMethod -Uri $api -Headers $headers
    $asset = @($rel.assets | Where-Object { $_.name -match '\.zip$' }) | Select-Object -First 1
    if (-not $asset) { throw 'No zip on nvidiaProfileInspector latest release' }
    $zip = Join-Path $env:TEMP ("npi-" + $rel.tag_name + ".zip")
    Write-Ok "Downloading $($asset.name) ($($rel.tag_name))..."
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing -Headers @{ 'User-Agent' = 'OptiHub-Nvidia/1.1' }
    $extract = Join-Path $env:TEMP ("npi-extract-" + [guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force
    $found = Get-ChildItem -LiteralPath $extract -Recurse -Filter $NpiExeName -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found) { throw 'nvidiaProfileInspector.exe missing from zip' }
    Copy-Item $found.FullName $NpiDir -Force
    $ref = Get-ChildItem -LiteralPath $extract -Recurse -Filter 'Reference.xml' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($ref) { Copy-Item $ref.FullName $NpiDir -Force }
    Write-Ok "Installed NPI to $NpiDir"
    return (Join-Path $NpiDir $NpiExeName)
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

function Stop-NvidiaClientProcesses {
    Write-Step 'Stopping NVIDIA App / GFE / Control Panel clients (driver stays)...'
    $killNames = @(
        'NVIDIA App', 'NVIDIA Overlay', 'NVIDIA Share', 'NVIDIA Web Helper',
        'nvcontainer', 'NVDisplay.Container', 'nvsphelper64', 'nvsphelper',
        'GFExperience', 'GeForce Experience', 'NVIDIA Control Panel',
        'nvidia-smi' # harmless if running
    )
    foreach ($n in $killNames) {
        Get-Process -Name $n -ErrorAction SilentlyContinue | ForEach-Object {
            try {
                # Never kill the kernel-mode stack; only user clients matched above
                Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
            } catch { }
        }
    }
    foreach ($im in @('NVIDIA App.exe', 'nvcontainer.exe', 'NVIDIA Share.exe', 'GFExperience.exe', 'nvcplui.exe')) {
        try { & taskkill.exe /F /IM $im /T 2>$null | Out-Null } catch { }
    }
    Start-Sleep -Milliseconds 600
    Write-Ok 'Client processes stopped'
}

function Remove-ConflictingNvidiaClients {
    # Wipe leftover App/GFE/CPL userland so a fresh NVIDIA App + profiles do not fight ghosts.
    Write-Step 'Removing conflicting NVIDIA App / GFE / old Control Panel leftovers...'
    Stop-NvidiaClientProcesses

    $removed = 0
    $pf = $env:ProgramFiles
    $pf86 = ${env:ProgramFiles(x86)}
    # Safe to remove - NOT Display.Driver / PhysX core
    $dirs = @(
        (Join-Path $pf 'NVIDIA Corporation\NVIDIA App'),
        (Join-Path $pf 'NVIDIA Corporation\NVIDIA Overlay'),
        (Join-Path $pf 'NVIDIA Corporation\NVIDIA GeForce Experience'),
        (Join-Path $pf 'NVIDIA Corporation\GeForce Experience'),
        (Join-Path $pf 'NVIDIA Corporation\ShadowPlay'),
        (Join-Path $pf 'NVIDIA Corporation\NVIDIA Control Panel'),
        (Join-Path $pf86 'NVIDIA Corporation\NVIDIA GeForce Experience'),
        (Join-Path $pf86 'NVIDIA Corporation\NVIDIA App'),
        (Join-Path $env:LOCALAPPDATA 'NVIDIA Corporation\NVIDIA App'),
        (Join-Path $env:LOCALAPPDATA 'NVIDIA Corporation\NVIDIA GeForce Experience'),
        (Join-Path $env:LOCALAPPDATA 'NVIDIA Corporation\GFExperience'),
        (Join-Path $env:ProgramData 'NVIDIA Corporation\NVIDIA App'),
        (Join-Path $env:ProgramData 'NVIDIA Corporation\GeForce Experience')
    )
    foreach ($d in $dirs) {
        if (-not (Test-Path -LiteralPath $d)) { continue }
        try {
            Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction Stop
            $removed++
            Write-Ok "Removed $d"
        } catch {
            Write-Warn "Could not fully remove $d : $($_.Exception.Message)"
            try {
                Get-ChildItem -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -match '(?i)cache|GPUCache|Code Cache|ShaderCache|Crashpad|Temp' } |
                    ForEach-Object {
                        try { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue; $removed++ } catch { }
                    }
            } catch { }
        }
    }

    # Store packages that conflict with clean App install
    Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match '(?i)GeForceExperience|NVIDIAApp|NVIDIAControlPanel'
    } | ForEach-Object {
        try {
            Write-Ok "Removing AppX $($_.Name)"
            Remove-AppxPackage -Package $_.PackageFullName -ErrorAction SilentlyContinue
            $removed++
        } catch { Write-Warn "AppX $($_.Name): $($_.Exception.Message)" }
    }

    Write-Ok "Conflict cleanup actions: $removed"
    return $removed
}

function Install-NvidiaApp {
    Write-Step 'Installing fresh NVIDIA App (display / 3D control surface)...'
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $winget) {
        Write-Warn 'winget not available - install NVIDIA App from nvidia.com if you want the App UI'
        return $false
    }
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    # MS Store listing for NVIDIA App
    & winget install --id XP8CLZL93F5Z4P -e --source msstore --accept-package-agreements --accept-source-agreements --silent 2>&1 | Out-Null
    $code = $LASTEXITCODE
    # Also ensure classic Control Panel AppX for Full RGB / resolution (optional)
    & winget install --id 9NF8H0H7WMLT -e --source msstore --accept-package-agreements --accept-source-agreements --silent 2>&1 | Out-Null
    $ErrorActionPreference = $prev
    if ($code -eq 0 -or (Test-NvidiaAppInstalled)) {
        Write-Ok 'NVIDIA App present (or install started)'
        return $true
    }
    Write-Warn "NVIDIA App winget exit $code - you can install manually from NVIDIA later"
    return $false
}

function Get-DriverAgeDays {
    try {
        $gpu = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '(?i)nvidia|geforce|rtx|gtx' } |
            Select-Object -First 1
        if (-not $gpu -or -not $gpu.DriverDate) { return 999 }
        $dt = [Management.ManagementDateTimeConverter]::ToDateTime($gpu.DriverDate)
        return [int]((Get-Date) - $dt).TotalDays
    } catch { return 999 }
}

function Get-WindowsDriverVersionString {
    try {
        $gpu = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '(?i)nvidia|geforce|rtx|gtx' } |
            Select-Object -First 1
        return [string]$gpu.DriverVersion
    } catch { return '' }
}

function Install-NVCleanstall {
    Write-Step 'Ensuring TechPowerUp NVCleanstall is installed...'
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        & winget install --id TechPowerUp.NVCleanstall -e --accept-package-agreements --accept-source-agreements --silent 2>&1 | Out-Null
        $ErrorActionPreference = $prev
    }
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\NVCleanstall\NVCleanstall.exe'),
        (Join-Path $env:ProgramFiles 'NVCleanstall\NVCleanstall.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'NVCleanstall\NVCleanstall.exe')
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    $hit = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages') -Recurse -Filter 'NVCleanstall.exe' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($hit) { return $hit.FullName }

    # Direct download fallback
    $url = 'https://www.techpowerup.com/download/techpowerup-nvcleanstall/'
    Write-Warn 'NVCleanstall not found after winget - download from techpowerup.com/nvcleanstall if needed'
    return $null
}

function Write-NVCleanstallGuide([string]$OutDir) {
    $path = Join-Path $OutDir 'OptiHub-NVCleanstall-Recommended.txt'
    $text = @"
OptiHub recommended NVCleanstall settings (QoL + performance + privacy + speed)
================================================================================
1) Download latest Game Ready driver for your GPU inside NVCleanstall.
2) Components: keep Display Driver (+ HD Audio if you use HDMI/DP audio).
   Uncheck: GeForce Experience / NVIDIA App components here if you install App separately,
   Telemetry, ShadowPlay/Overlay extras you do not use, backend leftovers.
3) Installation Tweaks:
   [x] Disable Installer Telemetry & Advertising
   [x] Perform a Clean Installation
   [x] Disable Ansel (or leave if you use freecam tools)
   [x] Show Expert Tweaks
4) Expert Tweaks (recommended for gaming QoL / latency / privacy):
   [x] Disable driver telemetry
   [x] Enable Message Signaled Interrupts (MSI) - Priority High when offered
   [x] Disable HDCP (if you do not need protected video playback)
   [x] Disable NVIDIA HD Audio device sleep timer (if using NV audio)
   [ ] Disable driver signature / re-sign packages - leave OFF for anti-cheat safety
5) Install, reboot if asked, then re-run OptiHub NVIDIA Optimizer Apply
   to import series .nip profile + App/debloat + display prefs.

Generated by OptiHub NVIDIA pack $Script:NvidiaOptVersion
"@
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
    Write-Ok "Wrote NVCleanstall guide: $path"
    return $path
}

function Start-DriverUpdateIfNeeded {
    param([bool]$Force)

    $age = Get-DriverAgeDays
    $ver = Get-WindowsDriverVersionString
    Write-Ok "Current driver version string: $ver (age ~$age days)"

    $stale = $Force -or ($age -ge 45)
    if (-not $stale) {
        Write-Ok 'Driver looks recent - skipping NVCleanstall driver pass (use -ForceDriver to run anyway)'
        return @{ Ran = $false; AgeDays = $age; Version = $ver }
    }

    Write-Step 'Driver is outdated or -ForceDriver set - launching NVCleanstall pipeline...'
    $exe = Install-NVCleanstall
    $guide = Write-NVCleanstallGuide $StateDir

    if ($exe -and (Test-Path $exe)) {
        try {
            Start-Process -FilePath $exe -Verb RunAs -ErrorAction SilentlyContinue
            Write-Ok "Launched NVCleanstall: $exe"
            Write-Ok 'Complete the clean driver install using OptiHub recommended tweaks, reboot if needed, then Reapply NVIDIA in OptiHub.'
            try { Start-Process notepad.exe -ArgumentList $guide -ErrorAction SilentlyContinue } catch { }
        } catch {
            Write-Warn "Could not launch NVCleanstall elevated: $($_.Exception.Message)"
            Write-Ok "Open NVCleanstall manually and follow: $guide"
        }
    } else {
        Write-Warn 'Install NVCleanstall from TechPowerUp, then re-run Apply with -ForceDriver'
        Write-Ok "Guide: $guide"
    }

    return @{ Ran = $true; AgeDays = $age; Version = $ver; Guide = $guide; Exe = $exe }
}

function Disable-NvidiaTelemetry {
    Write-Step 'Privacy / debloat: telemetry services and scheduled tasks...'
    $svcNames = @(
        'NvTelemetryContainer',
        'NVIDIA Display Container LS',
        'NVIDIA Telemetry Container'
    )
    # Only stop pure telemetry containers - do not kill Display Container LS (breaks driver)
    foreach ($name in @('NvTelemetryContainer')) {
        $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
        if (-not $svc) { continue }
        try {
            if ($svc.Status -eq 'Running') { Stop-Service -Name $name -Force -ErrorAction SilentlyContinue }
            Set-Service -Name $name -StartupType Disabled -ErrorAction SilentlyContinue
            Write-Ok "Service disabled: $name"
        } catch { Write-Warn "Service $name : $($_.Exception.Message)" }
    }

    $taskPatterns = @(
        '*NvTm*',
        '*NVIDIA*Telemetry*',
        '*NvProfile*',
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
            Disable-ScheduledTask -TaskName $tn -TaskPath $tp -ErrorAction SilentlyContinue | Out-Null
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
    Write-Ok 'Telemetry / auto-download privacy hints applied'
}

function Set-NvidiaDisplayPreferences {
    Write-Step 'Display color / scaling preferences (best-effort)...'
    $applied = @()

    # Prefer NVIDIA to own desktop color (NVTweak) + full range style flags where present
    $nvTweak = 'HKCU:\Software\NVIDIA Corporation\Global\NVTweak'
    if (-not (Test-Path $nvTweak)) {
        try { New-Item -Path $nvTweak -Force | Out-Null } catch { }
    }
    if (Test-Path $nvTweak) {
        # 1 = use NVIDIA settings for desktop color (classic NVCP behavior)
        Set-ItemProperty -Path $nvTweak -Name 'Gestalt' -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
        $applied += 'NVTweak Gestalt (NVIDIA color path hint)'
    }

    # Windows: disable "Fix apps that are blurry" can fight GPU scaling - leave alone
    # Set high performance GPU preference for common shells (optional noise - skip)

    # Desktop color management: set ICC to system default is complex; document Full RGB
    # Many full-range bits live in display EDID / CRU - we set a user preference file
    $pref = Join-Path $StateDir 'nvidia-display-prefs.json'
    $obj = @{
        preferFullRgb      = $true
        preferHighestBpc   = $true
        preferGpuScaling   = $true
        scalingMode        = 'NoScalingOrFullScreen'
        colorSource        = 'NVIDIA'
        note               = '3D settings forced via OptiHub .nip Base Profile. Confirm Output dynamic range Full and highest bpc under NVIDIA Control Panel / NVIDIA App > Display > Resolution if the driver exposes them for your monitor.'
    }
    [IO.File]::WriteAllText($pref, ($obj | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $applied += 'Saved OptiHub display preference manifest'

    # Try NVIDIA App / NVCP display registry database (driver-version specific)
    $dispDb = 'HKLM:\SYSTEM\CurrentControlSet\Services\nvlddmkm\DisplayDatabase'
    if (Test-Path $dispDb) {
        Write-Ok 'NVIDIA DisplayDatabase present (driver owns per-monitor timing/color)'
        $applied += 'DisplayDatabase detected'
    }

    foreach ($a in $applied) { Write-Ok $a }
    Write-Warn 'Open NVIDIA App or Control Panel once: set Output dynamic range = Full, highest available bpc, scaling = GPU / No scaling as preferred for your setup.'
    return $applied
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

    $gpus = Get-NvidiaGpus
    if ($gpus.Count -eq 0) {
        throw 'No NVIDIA GPU detected. Install Game Ready / Studio drivers first.'
    }
    $primary = $gpus[0]
    Write-Ok "GPU: $($primary.Name)"
    if ($primary.Driver) { Write-Ok "Driver: $($primary.Driver)" }
    Write-HubProgress 12 "GPU: $($primary.Name)"

    $seriesId = if ($Series) { $Series } else { Get-GpuSeriesFromName $primary.Name }
    if (-not $seriesId) {
        throw 'Could not map GPU to series 10/20/30/40/50. Pass -Series 30 (example).'
    }
    Write-Ok "Series: $seriesId"
    $useGsync = [bool]$Gsync
    Write-Ok ("G-SYNC profile: {0}" -f $(if ($useGsync) { 'Yes' } else { 'No (ULL Ultra / max FPS)' }))
    Write-HubProgress 18 "Series $seriesId"

    # --- Driver currency via NVCleanstall (if stale) ---
    $driverInfo = @{ Ran = $false }
    if (-not $SkipDriver) {
        Write-HubProgress 22 'Checking driver age / NVCleanstall...'
        $driverInfo = Start-DriverUpdateIfNeeded -Force:([bool]$ForceDriver)
        if ($driverInfo.Ran) {
            Write-Warn 'If you install a new driver now, reboot, then Reapply NVIDIA in OptiHub for profiles + App polish.'
        }
    } else {
        Write-Ok 'Driver/NVCleanstall step skipped (-SkipDriver)'
    }

    # --- Strip conflicting App/GFE/CPL leftovers, then fresh App ---
    Write-HubProgress 30 'Cleaning conflicting NVIDIA App / GFE / CPL leftovers...'
    $cleaned = 0
    if (-not $SkipApp) {
        $cleaned = [int](Remove-ConflictingNvidiaClients)
    }

    $appInstalled = Test-NvidiaAppInstalled
    if (-not $SkipApp) {
        Write-HubProgress 38 'NVIDIA App (clean install)...'
        if ($SkipDownload -and -not $appInstalled) {
            Write-Warn 'NVIDIA App not installed and -SkipDownload set'
        } else {
            [void](Install-NvidiaApp)
            $appInstalled = Test-NvidiaAppInstalled
        }
    } else {
        Write-Ok 'NVIDIA App step skipped (-SkipApp)'
    }

    Write-HubProgress 48 'Privacy / debloat...'
    Disable-NvidiaTelemetry

    Write-HubProgress 55 'Display preferences...'
    $disp = @(Set-NvidiaDisplayPreferences)

    # --- Profile Inspector import ---
    $nip = $null
    $npi = $null
    if (-not $SkipProfile) {
        $nip = Get-ProfileFile $seriesId $useGsync
        if (-not $nip) { throw "Missing profile for series $seriesId (G-SYNC=$useGsync)" }
        Write-Ok "Profile: $(Split-Path $nip -Leaf)"
        Write-HubProgress 65 'Profile Inspector...'

        $npi = Find-NpiExe
        if (-not $npi -and -not $SkipDownload) { $npi = Install-Npi }
        if (-not $npi) { throw 'nvidiaProfileInspector.exe not found' }
        Write-Ok "Inspector: $npi"
        Write-HubProgress 78 'Importing Base Profile (may need Administrator)...'

        $importArgs = @('-silentImport', $nip)
        $p = Start-Process -FilePath $npi -ArgumentList $importArgs -Wait -PassThru -WindowStyle Hidden
        $code = 0
        if ($p -and $null -ne $p.ExitCode) { $code = $p.ExitCode }
        if ($code -ne 0) {
            Write-Warn "NPI exit $code - elevating..."
            $p2 = Start-Process -FilePath $npi -ArgumentList $importArgs -Wait -PassThru -Verb RunAs
            if ($p2 -and $p2.ExitCode -ne 0) {
                throw "Profile import failed (exit $($p2.ExitCode)). Run OptiHub as Administrator."
            }
        }
        Write-Ok 'OptiHub Base Profile imported (3D / latency / series tweaks)'
    } else {
        Write-Ok 'Profile import skipped (-SkipProfile)'
    }

    Write-HubProgress 92 'Saving status...'
    Save-State @{
        version          = $Script:NvidiaOptVersion
        appliedUtc       = (Get-Date).ToUniversalTime().ToString('o')
        gpuName          = $primary.Name
        driver           = $primary.Driver
        series           = $seriesId
        gsync            = $useGsync
        profileFile      = $(if ($nip) { Split-Path $nip -Leaf } else { $null })
        npiPath          = $npi
        nvidiaApp        = $appInstalled
        displayPrefs     = $disp
        debloatApplied   = $true
        conflictCleanup  = $cleaned
        driverUpdatePass = $driverInfo
    }

    Write-Ok 'NVIDIA Optimizer finished'
    Write-Ok 'Confirm in NVIDIA App/Control Panel: Full RGB, highest bpc, preferred scaling.'
    if ($driverInfo.Ran) {
        Write-Ok 'After NVCleanstall driver install + reboot, Reapply this optimizer for profiles.'
    }
    Write-HubProgress 100 'Completed successfully'
    Write-Output "DONE - NVIDIA $seriesId$(if($useGsync){' G-SYNC'}) + clean App + debloat + display prefs"
    exit 0
} catch {
    Write-Err $_.Exception.Message
    Write-HubProgress 100 'Failed'
    exit 1
}
