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
            if (-not $info -or -not $info.Version) { continue }
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

    Write-Warn 'NVCleanstall not found after winget - install from techpowerup.com/nvcleanstall'
    return $null
}

function Write-NVCleanstallGuide([string]$OutDir, [string]$CurrentNv, [string]$LatestNv, [string]$DownloadUrl) {
    $path = Join-Path $OutDir 'OptiHub-NVCleanstall-Recommended.txt'
    $text = @"
OptiHub NVCleanstall checklist (best QoL / performance / privacy / speed)
========================================================================
Installed NVIDIA driver : $CurrentNv
Newest Game Ready found : $LatestNv
Official download URL   : $DownloadUrl

In NVCleanstall:
1) Use "Download and install latest" (or point it at the Game Ready package above).
2) Components: Display Driver required. HD Audio only if you use HDMI/DP audio.
   Leave NVIDIA App out of the driver package (OptiHub installs a clean App separately).

Installation Tweaks - enable ALL of these:
   [x] Disable Installer Telemetry and Advertising
   [x] Unattended Express Installation
   [x] Automatic Reboot if Needed
   [x] Perform a Clean Installation
   [x] Disable Ansel
   [x] Show Expert Tweaks

Expert Tweaks - enable ALL of these:
   [x] Disable driver telemetry
   [x] Disable NVIDIA HD Audio device sleep timer
   [x] Enable Message Signaled Interrupts (MSI)
         - First option: Default (as offered)
         - Second / priority: High
   [x] Disable HDCP
   [x] Use method compatible with Easy Anti-Cheat
   [x] Auto-accept / allow unsigned driver (accept the unsigned driver prompt)

Then Install. After reboot, open OptiHub -> NVIDIA -> Reapply to import series
profiles, App/debloat, and display Full RGB / high bpc prefs.

Generated by OptiHub NVIDIA pack $Script:NvidiaOptVersion
"@
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
    Write-Ok "Wrote NVCleanstall guide: $path"
    return $path
}

function Start-DriverUpdateIfNeeded {
    param([bool]$Force)

    $winVer = Get-WindowsDriverVersionString
    $currentNv = Convert-WindowsDriverToNvidia $winVer
    Write-Ok "Installed Windows driver string: $winVer"
    Write-Ok "Decoded NVIDIA version: $(if($currentNv){$currentNv}else{'unknown'})"

    Write-Step 'Checking NVIDIA for the newest Game Ready driver...'
    $latest = Get-LatestGameReadyDriver
    if (-not $latest) {
        Write-Warn 'Could not reach NVIDIA driver API - launching NVCleanstall so you can still grab the newest package'
        $needUpdate = $true
        $latestVer = 'unknown'
        $dl = ''
    } else {
        $latestVer = $latest.Version
        $dl = $latest.DownloadUrl
        Write-Ok "Newest Game Ready: $latestVer ($($latest.ReleaseDate)) size $($latest.Size)"
        if ($dl) { Write-Ok "Download: $dl" }
        $needUpdate = $Force
        if (-not $currentNv) {
            $needUpdate = $true
            Write-Warn 'Could not decode installed version - treating as outdated'
        } elseif ((Compare-NvidiaVersion $currentNv $latestVer) -lt 0) {
            $needUpdate = $true
            Write-Warn "Outdated: $currentNv < newest $latestVer"
        } else {
            Write-Ok "Already on newest (or newer): $currentNv"
        }
    }

    if (-not $needUpdate) {
        return @{
            Ran            = $false
            NeedsUpdate    = $false
            CurrentVersion = $currentNv
            LatestVersion  = $latestVer
            WindowsVersion = $winVer
            DownloadUrl    = $dl
        }
    }

    Write-Step 'Prompting newest-driver install via NVCleanstall...'
    $exe = Install-NVCleanstall
    $guide = Write-NVCleanstallGuide $StateDir $(if($currentNv){$currentNv}else{$winVer}) $latestVer $dl

    if ($exe -and (Test-Path $exe)) {
        try {
            Start-Process -FilePath $exe -Verb RunAs -ErrorAction SilentlyContinue
            Write-Ok "Launched NVCleanstall: $exe"
            Write-Ok 'Install the newest Game Ready driver with the OptiHub checklist, reboot if asked, then Reapply NVIDIA.'
            try { Start-Process notepad.exe -ArgumentList "`"$guide`"" -ErrorAction SilentlyContinue } catch { }
            if ($dl) {
                try { Start-Process $dl -ErrorAction SilentlyContinue } catch { }
            }
        } catch {
            Write-Warn "Could not launch NVCleanstall elevated: $($_.Exception.Message)"
            Write-Ok "Open NVCleanstall manually and follow: $guide"
        }
    } else {
        Write-Warn 'Install NVCleanstall from TechPowerUp, then re-run Apply'
        Write-Ok "Guide: $guide"
        if ($dl) { try { Start-Process $dl -ErrorAction SilentlyContinue } catch { } }
    }

    return @{
        Ran            = $true
        NeedsUpdate    = $true
        CurrentVersion = $currentNv
        LatestVersion  = $latestVer
        WindowsVersion = $winVer
        DownloadUrl    = $dl
        Guide          = $guide
        Exe            = $exe
    }
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
    Write-Ok ("G-SYNC profile: {0}" -f $(if ($useGsync) { 'Yes' } else { 'No Gsync' }))
    Write-HubProgress 15 "Series $seriesId"

    # Pipeline order (correct stack):
    #  1) Driver first (everything else sits on it)
    #  2) 3D Base Profile next (driver-level FPS/latency)
    #  3) Then client stack: wipe conflicts, App, privacy, display

    # --- 1) Newest driver ---
    $driverInfo = @{ Ran = $false; NeedsUpdate = $false }
    if (-not $SkipDriver) {
        Write-HubProgress 20 'Checking for newest Game Ready driver...'
        $driverInfo = Start-DriverUpdateIfNeeded -Force:([bool]$ForceDriver)
        if ($driverInfo.NeedsUpdate -and $driverInfo.Ran) {
            # New driver install wipes 3D settings - stop here so user reboots then Reapply
            Save-State @{
                version          = $Script:NvidiaOptVersion
                appliedUtc       = (Get-Date).ToUniversalTime().ToString('o')
                gpuName          = $primary.Name
                driver           = $primary.Driver
                series           = $seriesId
                gsync            = $useGsync
                driverUpdatePass = $driverInfo
                pendingAfterDriver = $true
            }
            Write-Warn 'Finish NVCleanstall + reboot, then Reapply NVIDIA (3D profile + App polish run after driver is current).'
            Write-HubProgress 100 'Driver update started - reapply after reboot'
            Write-Output 'DONE - Driver update prompted. Reapply after NVCleanstall + reboot.'
            exit 0
        }
    } else {
        Write-Ok 'Driver check skipped (-SkipDriver)'
    }

    # --- 2) 3D Base Profile (right after driver) ---
    $nip = $null
    $npi = $null
    if (-not $SkipProfile) {
        $nip = Get-ProfileFile $seriesId $useGsync
        if (-not $nip) { throw "Missing profile for series $seriesId (G-SYNC=$useGsync)" }
        Write-Ok "Profile: $(Split-Path $nip -Leaf)"
        Write-HubProgress 40 'Profile Inspector (3D settings)...'

        $npi = Find-NpiExe
        if (-not $npi -and -not $SkipDownload) { $npi = Install-Npi }
        if (-not $npi) { throw 'nvidiaProfileInspector.exe not found' }
        Write-Ok "Inspector: $npi"
        Write-HubProgress 48 'Importing 3D Base Profile...'

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
        Write-Ok '3D Base Profile imported (FPS / latency / series tweaks)'
    } else {
        Write-Ok '3D profile import skipped (-SkipProfile)'
    }

    # --- 3) Client stack: conflicts -> App -> privacy -> display ---
    Write-HubProgress 58 'Cleaning conflicting NVIDIA App / GFE / CPL leftovers...'
    $cleaned = 0
    if (-not $SkipApp) {
        $cleaned = [int](Remove-ConflictingNvidiaClients)
    }

    $appInstalled = Test-NvidiaAppInstalled
    if (-not $SkipApp) {
        Write-HubProgress 68 'NVIDIA App (clean install)...'
        if ($SkipDownload -and -not $appInstalled) {
            Write-Warn 'NVIDIA App not installed and -SkipDownload set'
        } else {
            [void](Install-NvidiaApp)
            $appInstalled = Test-NvidiaAppInstalled
        }
    } else {
        Write-Ok 'NVIDIA App step skipped (-SkipApp)'
    }

    Write-HubProgress 80 'Privacy / debloat...'
    Disable-NvidiaTelemetry

    Write-HubProgress 88 'Display preferences...'
    $disp = @(Set-NvidiaDisplayPreferences)

    Write-HubProgress 94 'Saving status...'
    Save-State @{
        version            = $Script:NvidiaOptVersion
        appliedUtc         = (Get-Date).ToUniversalTime().ToString('o')
        gpuName            = $primary.Name
        driver             = $primary.Driver
        series             = $seriesId
        gsync              = $useGsync
        profileFile        = $(if ($nip) { Split-Path $nip -Leaf } else { $null })
        npiPath            = $npi
        nvidiaApp          = $appInstalled
        displayPrefs       = $disp
        debloatApplied     = $true
        conflictCleanup    = $cleaned
        driverUpdatePass   = $driverInfo
        pendingAfterDriver = $false
    }

    Write-Ok 'NVIDIA Optimizer finished'
    Write-Ok 'Confirm Full RGB / highest bpc / scaling in NVIDIA App or Control Panel if needed.'
    Write-HubProgress 100 'Completed successfully'
    Write-Output "DONE - NVIDIA $seriesId$(if($useGsync){' GSync'}else{' No Gsync'}) (driver ok -> 3D profile -> App/display)"
    exit 0
} catch {
    Write-Err $_.Exception.Message
    Write-HubProgress 100 'Failed'
    exit 1
}
