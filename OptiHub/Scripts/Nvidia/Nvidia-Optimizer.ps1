# OptiHub NVIDIA Optimizer
# - Apply series + G-SYNC OptiHub Base Profile via Profile Inspector
# - Display settings via classic NVIDIA Control Panel only (not NVIDIA App)
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
    [switch]$SkipApp,          # default behavior: App is skipped (kept for compatibility)
    [switch]$InstallApp,       # opt-in only — Control Panel is the display UI
    [switch]$SkipProfile,
    [switch]$SkipDriver,
    [switch]$ForceDriver
)

$ErrorActionPreference = 'Stop'
$Script:NvidiaOptVersion = '1.3.5'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProfilesDir = Join-Path $Root 'profiles'
$StateDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'OptiHub'
$StatePath = Join-Path $StateDir 'nvidia-optimizer.json'
$NpiDir = Join-Path $StateDir 'nvidia-profile-inspector'
$DriverCacheDir = Join-Path $StateDir 'drivers'
$NpiExeName = 'nvidiaProfileInspector.exe'

function Write-HubProgress([int]$Percent, [string]$Status) {
    $p = [Math]::Max(0, [Math]::Min(100, $Percent))
    $line = "OPTIHUB_PROGRESS:$p|$Status"
    # IMPORTANT: do NOT Write-Output progress — it poisons function returns
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
    # Prefer OptiHub-managed copy only (Documents NPI often opens GUI / replace dialogs).
    $managed = Join-Path $NpiDir $NpiExeName
    if (Test-Path -LiteralPath $managed) { return $managed }
    $tools = Join-Path $Root "tools\$NpiExeName"
    if (Test-Path -LiteralPath $tools) { return $tools }
    return $null
}

function Stop-NpiProcesses {
    Get-Process -Name 'nvidiaProfileInspector' -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            Write-Ok "Stopping stuck Profile Inspector PID $($_.Id)"
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        } catch { }
    }
    Start-Sleep -Milliseconds 400
}

function Install-Npi {
    Write-Step 'Installing NVIDIA Profile Inspector (OptiHub managed)...'
    New-Item -ItemType Directory -Force -Path $NpiDir | Out-Null

    $api = 'https://api.github.com/repos/Orbmu2k/nvidiaProfileInspector/releases/latest'
    $headers = @{ 'User-Agent' = 'OptiHub-Nvidia/1.2'; 'Accept' = 'application/vnd.github+json' }
    $rel = Invoke-RestMethod -Uri $api -Headers $headers
    $asset = @($rel.assets | Where-Object { $_.name -match '\.zip$' }) | Select-Object -First 1
    if (-not $asset) { throw 'No zip on nvidiaProfileInspector latest release' }
    $zip = Join-Path $env:TEMP ("npi-" + $rel.tag_name + ".zip")
    Write-Ok "Downloading $($asset.name) ($($rel.tag_name))..."
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing -Headers @{ 'User-Agent' = 'OptiHub-Nvidia/1.2' }
    $extract = Join-Path $env:TEMP ("npi-extract-" + [guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force
    $found = Get-ChildItem -LiteralPath $extract -Recurse -Filter $NpiExeName -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found) { throw 'nvidiaProfileInspector.exe missing from zip' }
    Copy-Item $found.FullName (Join-Path $NpiDir $NpiExeName) -Force
    foreach ($extra in @('Reference.xml', 'CustomSettingNames.xml', 'nvidiaProfileInspector.exe.config')) {
        $hit = Get-ChildItem -LiteralPath $extract -Recurse -Filter $extra -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { Copy-Item $hit.FullName (Join-Path $NpiDir $extra) -Force -ErrorAction SilentlyContinue }
    }
    Write-Ok "Installed NPI to $NpiDir"
    return (Join-Path $NpiDir $NpiExeName)
}

function Import-OptiHubNipProfile {
    param(
        [Parameter(Mandatory)][string]$NipPath,
        [int]$TimeoutSec = 90
    )
    # Fully silent import: kill GUI instances, use managed NPI, short path, -silentImport,
    # auto-accept replace dialogs if any, timeout so we never hang OptiHub forever.
    if (-not (Test-Path -LiteralPath $NipPath)) {
        throw "NIP profile missing: $NipPath"
    }

    Stop-NpiProcesses

    $npi = Find-NpiExe
    if (-not $npi) { $npi = Install-Npi }
    if (-not $npi -or -not (Test-Path -LiteralPath $npi)) {
        throw 'nvidiaProfileInspector.exe not available'
    }
    Write-Ok "Inspector: $npi"

    # Avoid spaces / long paths that break some NPI CLI parsing
    $safeNip = Join-Path $env:TEMP ('optihub-' + [IO.Path]::GetFileNameWithoutExtension($NipPath).Replace(' ', '') + '.nip')
    Copy-Item -LiteralPath $NipPath -Destination $safeNip -Force
    Write-Ok "Importing: $(Split-Path $NipPath -Leaf) via silent CLI"

    # Background dialog dismisser (replace / confirm / import prompts)
    $dismissJob = Start-Job -ScriptBlock {
        param([int]$Seconds)
        $end = [datetime]::UtcNow.AddSeconds($Seconds)
        try { $wshell = New-Object -ComObject WScript.Shell } catch { return }
        while ([datetime]::UtcNow -lt $end) {
            foreach ($title in @(
                'NVIDIA Profile Inspector',
                'Profile Inspector',
                'Import',
                'Confirm',
                'Replace',
                'Warning',
                'nvidiaProfileInspector'
            )) {
                try {
                    if ($wshell.AppActivate($title)) {
                        Start-Sleep -Milliseconds 150
                        # Prefer default button (Yes / Replace / OK)
                        $wshell.SendKeys('{ENTER}')
                        Start-Sleep -Milliseconds 120
                        $wshell.SendKeys('y')
                        Start-Sleep -Milliseconds 80
                        $wshell.SendKeys('{ENTER}')
                    }
                } catch { }
            }
            Start-Sleep -Milliseconds 350
        }
    } -ArgumentList $TimeoutSec

    $exitCode = -1
    $timedOut = $false
    $npiDir = Split-Path -Parent $npi
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $npi
        # Docs: nvidiaProfileInspector.exe -silentImport "profile.nip"
        $psi.Arguments = '-silentImport "' + $safeNip + '"'
        $psi.WorkingDirectory = $npiDir
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $proc = [Diagnostics.Process]::Start($psi)
        if (-not $proc) { throw 'Failed to start Profile Inspector' }

        if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
            $timedOut = $true
            try { $proc.Kill() } catch { }
            try { $proc.WaitForExit(5000) } catch { }
            Stop-NpiProcesses
            throw "Profile Inspector silent import timed out after ${TimeoutSec}s (GUI/replace dialog stuck). Profile NOT marked applied."
        }
        $exitCode = [int]$proc.ExitCode
    } finally {
        try { Stop-Job $dismissJob -ErrorAction SilentlyContinue; Remove-Job $dismissJob -Force -ErrorAction SilentlyContinue } catch { }
        Stop-NpiProcesses
        try { Remove-Item -LiteralPath $safeNip -Force -ErrorAction SilentlyContinue } catch { }
    }

    if ($exitCode -ne 0) {
        throw "Profile Inspector silent import failed (exit $exitCode). Profile NOT marked applied."
    }
    if ($timedOut) {
        throw 'Profile import timed out. Profile NOT marked applied.'
    }

    Write-Ok '3D Base Profile imported silently (no GUI / replace click needed)'
    return @{
        Success  = $true
        ExitCode = $exitCode
        NpiPath  = $npi
        NipFile  = (Split-Path $NipPath -Leaf)
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
    # Wipe GFE / Overlay / bloat — KEEP classic Control Panel (we use it for display settings).
    Write-Step 'Removing GFE / Overlay leftovers (keeping NVIDIA Control Panel)...'
    Stop-NvidiaClientProcesses

    $removed = 0
    $pf = $env:ProgramFiles
    $pf86 = ${env:ProgramFiles(x86)}
    $dirs = @(
        (Join-Path $pf 'NVIDIA Corporation\NVIDIA Overlay'),
        (Join-Path $pf 'NVIDIA Corporation\NVIDIA GeForce Experience'),
        (Join-Path $pf 'NVIDIA Corporation\GeForce Experience'),
        (Join-Path $pf 'NVIDIA Corporation\ShadowPlay'),
        (Join-Path $pf86 'NVIDIA Corporation\NVIDIA GeForce Experience'),
        (Join-Path $env:LOCALAPPDATA 'NVIDIA Corporation\NVIDIA GeForce Experience'),
        (Join-Path $env:LOCALAPPDATA 'NVIDIA Corporation\GFExperience'),
        (Join-Path $env:LOCALAPPDATA 'NVIDIA Corporation\NVIDIA Overlay'),
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
        }
    }

    # Remove GFE store packages only — never NVIDIA Control Panel, never Windows Settings
    Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match '(?i)GeForceExperience'
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
    Write-Step 'Ensuring NVIDIA Control Panel (display color / scaling UI)...'
    if (Test-NvidiaControlPanelInstalled) {
        Write-Ok 'NVIDIA Control Panel already present'
        return $true
    }
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $winget) {
        Write-Warn 'winget missing — install NVIDIA Control Panel from Microsoft Store if needed'
        return $false
    }
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    # Official Store package for classic NVIDIA Control Panel
    & winget install --id 9NF8H0H7WMLT -e --source msstore --accept-package-agreements --accept-source-agreements --silent 2>&1 | Out-Null
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    if ($code -eq 0 -or (Test-NvidiaControlPanelInstalled)) {
        Write-Ok 'NVIDIA Control Panel installed'
        return $true
    }
    Write-Warn "Control Panel winget exit $code"
    return $false
}

function Find-NvcpluiExe {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\Control Panel Client\nvcplui.exe'),
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA Control Panel\nvcplui.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'NVIDIA Corporation\Control Panel Client\nvcplui.exe')
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { return $c }
    }
    # AppX package path
    $pkg = Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match '(?i)^NVIDIACorp\.NVIDIAControlPanel$'
    } | Select-Object -First 1
    if ($pkg) {
        $hit = Get-ChildItem -LiteralPath $pkg.InstallLocation -Recurse -Filter 'nvcplui.exe' -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

function Disable-NvidiaOverlay {
    Write-Step 'Disabling / removing NVIDIA Overlay (in-game overlay)...'
    foreach ($n in @('NVIDIA Overlay', 'NVIDIA Share', 'nvsphelper64', 'nvsphelper', 'NVIDIA Web Helper')) {
        Get-Process -Name $n -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    foreach ($im in @('NVIDIA Overlay.exe', 'NVIDIA Share.exe', 'nvsphelper64.exe')) {
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

    # Remove Overlay install tree if present (does not touch driver)
    foreach ($d in @(
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA Overlay'),
        (Join-Path $env:LOCALAPPDATA 'NVIDIA Corporation\NVIDIA Overlay')
    )) {
        if (Test-Path -LiteralPath $d) {
            try {
                Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction Stop
                Write-Ok "Removed overlay tree: $d"
            } catch { Write-Warn "Could not remove $d" }
        }
    }

    # Do NOT call rundll32 ShadowPlayDisable — export is often missing after slim/clean driver installs
    # (nvspcap64.dll may exist without that entrypoint → "no DLL" style errors).
    Write-Ok 'NVIDIA Overlay disabled / stripped (registry + files; no ShadowPlay DLL call)'
}

function Remove-NvidiaAppLeftovers {
    Write-Step 'Removing NVIDIA App + leftovers (Control Panel kept)...'
    foreach ($n in @('NVIDIA App', 'NVIDIA Overlay', 'NVIDIA Share', 'nvsphelper64', 'nvsphelper', 'NVIDIA Web Helper')) {
        Get-Process -Name $n -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        & winget uninstall --id XP8CLZL93F5Z4P -e --silent 2>&1 | Out-Null
        $ErrorActionPreference = $prev
    }
    Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match '(?i)NVIDIAApp|GeForceExperience' -and $_.Name -notmatch 'ControlPanel'
    } | ForEach-Object {
        try { Remove-AppxPackage -Package $_.PackageFullName -ErrorAction SilentlyContinue } catch { }
    }
    foreach ($d in @(
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA App'),
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA Overlay'),
        (Join-Path $env:LOCALAPPDATA 'NVIDIA Corporation\NVIDIA App'),
        (Join-Path $env:LOCALAPPDATA 'NVIDIA Corporation\NVIDIA Overlay'),
        (Join-Path $env:ProgramData 'NVIDIA Corporation\NVIDIA App'),
        (Join-Path $env:ProgramData 'NVIDIA Corporation\NvBackend')
    )) {
        if (Test-Path -LiteralPath $d) {
            try { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue; Write-Ok "Removed $d" } catch { }
        }
    }
    Write-Ok 'NVIDIA App leftovers cleanup done'
}

function Accept-NvidiaControlPanelEula {
    # Store/UWP CPL first-run "Agree and continue" blocks all settings until accepted.
    Write-Step 'Accepting NVIDIA Control Panel license (so settings can apply)...'
    $client = 'HKCU:\Software\NVIDIA Corporation\NVControlPanel2\Client'
    if (-not (Test-Path $client)) { New-Item -Path $client -Force | Out-Null }
    # Forum-known + common first-run flags (0 = don't show / already agreed)
    foreach ($pair in @(
        @{ N = 'ShowSedoanEula'; V = 0 },
        @{ N = 'ShowEula'; V = 0 },
        @{ N = 'EulaAccepted'; V = 1 },
        @{ N = 'UserAgreedToEula'; V = 1 },
        @{ N = 'AgreeToEula'; V = 1 }
    )) {
        New-ItemProperty -LiteralPath $client -Name $pair.N -Value $pair.V -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
    }

    # Auto-click Agree / Continue if the dialog still appears
    $job = Start-Job -ScriptBlock {
        $end = [datetime]::UtcNow.AddSeconds(25)
        try { $w = New-Object -ComObject WScript.Shell } catch { return }
        while ([datetime]::UtcNow -lt $end) {
            foreach ($title in @('NVIDIA Control Panel', 'NVIDIA', 'License', 'Agreement', 'Control Panel')) {
                try {
                    if ($w.AppActivate($title)) {
                        Start-Sleep -Milliseconds 200
                        # Tab to primary button then Enter; also try Alt paths
                        $w.SendKeys('{TAB}{TAB}{ENTER}')
                        Start-Sleep -Milliseconds 150
                        $w.SendKeys('{ENTER}')
                        Start-Sleep -Milliseconds 100
                        $w.SendKeys('%a')  # Alt+A Agree if accelerator exists
                        Start-Sleep -Milliseconds 80
                        $w.SendKeys('%c')  # Continue
                    }
                } catch { }
            }
            Start-Sleep -Milliseconds 400
        }
    }
    return $job
}

function Install-NvidiaApp {
    # Optional — display settings use Control Panel, not the App.
    Write-Step 'Installing NVIDIA App (optional; display prefs use Control Panel)...'
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

function Download-NvidiaDriverPackage {
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$Version
    )
    if (-not (Test-Path $DriverCacheDir)) {
        New-Item -ItemType Directory -Path $DriverCacheDir -Force | Out-Null
    }
    $fileName = "GameReady-$Version-win10-win11-64bit-dch.exe"
    $outFile = Join-Path $DriverCacheDir $fileName

    if ((Test-Path -LiteralPath $outFile) -and ((Get-Item -LiteralPath $outFile).Length -gt 50MB)) {
        Write-Ok "Using cached driver package: $outFile"
        return $outFile
    }

    Write-Step "Downloading official Game Ready $Version (one package, cached for re-runs)..."
    Write-HubProgress 22 "Downloading Game Ready $Version..."
    $tmp = "$outFile.partial"
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
        # Need Display.Driver + NVI2 only (HD Audio is intentionally NOT installed)
        $ok = (Test-Path -LiteralPath (Join-Path $DestDir 'NVI2'))
        if ($ok) {
            Write-Ok "Using existing extract: $DestDir"
            return $existingSetup
        }
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
        if ($setup) {
            Write-Ok "Extracted with NanaZip: $($setup.DirectoryName)"
            return $setup.FullName
        }
        Write-Warn 'NanaZip ran but setup.exe not found in extract tree'
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
            if ($setup) {
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
    # OptiHub Clean Driver (NVCleanstall-class, OUR rules — better for silent):
    #  1) Official Game Ready once (cached)
    #  2) Extract (folders stay on disk so setup.exe resolves; we do NOT install bloat)
    #  3) Silent CLEAN install of Display.Driver ONLY — NO HD Audio, NO App, NO telemetry packages
    #  4) Post-install expert tweaks (MSI High, telemetry off, Ansel off, no HD Audio leftovers)
    #  5) Continue pipeline (no forced reboot)
    Write-Step "OptiHub Clean Driver install ($Version) — Display.Driver ONLY (no HD Audio)"
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
        # ONLY Display.Driver — matches NVCleanstall-style "Display Driver required", HD Audio off
        $argVariants = @(
            @('Display.Driver', '-s', '-clean', '-noreboot', '-noeula'),
            @('-s', '-clean', '-noreboot', '-noeula', 'Display.Driver'),
            @('-s', '-noreboot', '-clean', 'Display.Driver')
        )
        Write-HubProgress 55 'Clean-installing Display.Driver only (silent, no HD Audio, no reboot)...'
        foreach ($setupArgs in $argVariants) {
            Write-Ok ("Running: setup.exe " + ($setupArgs -join ' ') + " (cwd=$setupDir)")
            $p = Start-Process -FilePath $setup -ArgumentList $setupArgs -WorkingDirectory $setupDir -Wait -PassThru -WindowStyle Hidden
            if ($p) { $exitCode = [int]$p.ExitCode }
            Write-Ok "setup.exe exit: $exitCode"
            if (@(0, 1, 2, 3, 5) -contains $exitCode) { break }
            if ($exitCode -ne -2147024893) { break }
        }
    } else {
        Write-Warn 'Extract failed — cannot safely silent-install without component filter (would pull HD Audio/bloat)'
        return @{ Success = $false; ExitCode = -1; Error = 'extract-failed'; Method = 'optihub-clean' }
    }

    $okCodes = @(0, 1, 2, 3, 5)
    if ($okCodes -contains $exitCode) {
        Write-Ok "OptiHub Clean Driver finished (exit $exitCode) — continuing pipeline, no forced reboot"
        return @{
            Success  = $true
            ExitCode = $exitCode
            Package  = $package
            Setup    = $setup
            Method   = 'optihub-clean'
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
    #  KEEP: MSI High, disable telemetry, disable Ansel/NvCamera, strip HD Audio leftovers,
    #        quiet auto-download / telemetry consent RIDs
    #  SKIP: HD Audio sleep timer (we do NOT install HD Audio), unsigned-driver accept (install-time only),
    #        EAC-compatible strip method (install-time INF only — not safe on stock silent setup),
    #        Disable HDCP (unsigned/risky; skip), fake OptiHub-only tags that drivers ignore
    Write-Step 'Applying OptiHub driver expert tweaks (MSI High, telemetry off, no HD Audio, Ansel off)...'

    # --- MSI High (real interrupt mode tweak) ---
    $msiCount = 0
    try {
        $pci = 'HKLM:\SYSTEM\CurrentControlSet\Enum\PCI'
        if (Test-Path $pci) {
            Get-ChildItem $pci -ErrorAction SilentlyContinue | Where-Object {
                $_.PSChildName -match 'VEN_10DE'
            } | ForEach-Object {
                Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
                    $dev = $_.PSPath
                    # Only GPU-class nodes when possible (CC_03)
                    if ($_.PSChildName -notmatch 'DEV_|CC_03' -and $_.Name -notmatch 'VEN_10DE') { }
                    $msiKey = Join-Path $dev 'Device Parameters\Interrupt Management\MessageSignaledInterruptProperties'
                    if (-not (Test-Path $msiKey)) {
                        New-Item -Path $msiKey -Force -ErrorAction SilentlyContinue | Out-Null
                    }
                    if (Test-Path $msiKey) {
                        New-ItemProperty -LiteralPath $msiKey -Name 'MSISupported' -Value 1 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
                        $msiCount++
                    }
                    $aff = Join-Path $dev 'Device Parameters\Interrupt Management\Affinity Policy'
                    if (-not (Test-Path $aff)) {
                        New-Item -Path $aff -Force -ErrorAction SilentlyContinue | Out-Null
                    }
                    if (Test-Path $aff) {
                        # 3 = High priority (NVCleanstall MSI High)
                        New-ItemProperty -LiteralPath $aff -Name 'DevicePriority' -Value 3 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
                    }
                }
            }
        }
    } catch {
        Write-Warn "MSI tweak: $($_.Exception.Message)"
    }
    if ($msiCount -gt 0) { Write-Ok "MSI High on $msiCount NVIDIA PCI node(s)" }
    else { Write-Ok 'MSI registry apply attempted (may need reboot to bind)' }

    # --- No HD Audio: disable leftovers from prior stock installs (we never install HDAudio.Driver) ---
    foreach ($svcName in @('NVHDA', 'nvhda', 'HDAudBus')) {
        $svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
        if (-not $svc) { continue }
        # Only touch NVIDIA HD audio, not the system HDAudBus generic stack if shared — NVHDA only
        if ($svcName -eq 'HDAudBus') { continue }
        try {
            if ($svc.Status -eq 'Running') { Stop-Service -Name $svcName -Force -ErrorAction SilentlyContinue }
            Set-Service -Name $svcName -StartupType Disabled -ErrorAction SilentlyContinue
            Write-Ok "Disabled leftover HD Audio service: $svcName"
        } catch { }
    }
    # Disable NVHDA devices in PnP if present
    try {
        Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
            $_.FriendlyName -match '(?i)NVIDIA High Definition Audio|NVIDIA Virtual Audio'
        } | ForEach-Object {
            try {
                Disable-PnpDevice -InstanceId $_.InstanceId -Confirm:$false -ErrorAction SilentlyContinue
                Write-Ok "Disabled PnP: $($_.FriendlyName)"
            } catch { }
        }
    } catch { }

    # --- Ansel / camera off (NVCleanstall Disable Ansel) ---
    foreach ($svcName in @('NvCamera', 'NvTelemetryContainer')) {
        $svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
        if (-not $svc) { continue }
        try {
            if ($svc.Status -eq 'Running') { Stop-Service -Name $svcName -Force -ErrorAction SilentlyContinue }
            Set-Service -Name $svcName -StartupType Disabled -ErrorAction SilentlyContinue
            Write-Ok "Service disabled: $svcName"
        } catch { }
    }
    # Never disable NvContainerLocalSystem — required for modern DCH driver stack

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
    Write-Ok 'Expert tweaks done (MSI High, telemetry off, Ansel off, no HD Audio)'
}

function Test-OptiHubDriverInstallTweaks {
    # Signals that OptiHub clean install + expert tweaks actually landed.
    $issues = New-Object System.Collections.Generic.List[string]
    $oks = New-Object System.Collections.Generic.List[string]

    # Telemetry service should not be auto-start
    $svc = Get-Service -Name 'NvTelemetryContainer' -ErrorAction SilentlyContinue
    if ($svc) {
        if ($svc.StartType -eq 'Disabled') {
            [void]$oks.Add('NvTelemetryContainer disabled')
        } else {
            [void]$issues.Add('NvTelemetryContainer still enabled (stock-style telemetry)')
        }
    } else {
        [void]$oks.Add('NvTelemetryContainer absent')
    }

    # HD Audio should not be running (we never install it)
    $hda = Get-Service -Name 'NVHDA' -ErrorAction SilentlyContinue
    if ($hda -and $hda.Status -eq 'Running') {
        [void]$issues.Add('NVIDIA HD Audio service running (should not install HD Audio)')
    } else {
        [void]$oks.Add('NVIDIA HD Audio not active')
    }

    # GeForce Experience tree suggests bloated stock package still present
    $gfePaths = @(
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA GeForce Experience'),
        (Join-Path $env:ProgramFiles 'NVIDIA Corporation\GeForce Experience')
    )
    $gfeHit = $false
    foreach ($p in $gfePaths) {
        if (Test-Path -LiteralPath $p) { $gfeHit = $true; break }
    }
    if ($gfeHit) {
        [void]$issues.Add('GeForce Experience leftovers present (stock package signal)')
    } else {
        [void]$oks.Add('No GeForce Experience install tree')
    }

    # MSI: if the key exists and is 0, fail; if 1, pass; if missing, ignore
    $msiSeen = $false
    $msiOn = $false
    try {
        $pci = 'HKLM:\SYSTEM\CurrentControlSet\Enum\PCI'
        if (Test-Path $pci) {
            Get-ChildItem $pci -ErrorAction SilentlyContinue | Where-Object {
                $_.PSChildName -match 'VEN_10DE'
            } | ForEach-Object {
                Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
                    $msiKey = Join-Path $_.PSPath 'Device Parameters\Interrupt Management\MessageSignaledInterruptProperties'
                    if (-not (Test-Path $msiKey)) { return }
                    $msiSeen = $true
                    $v = (Get-ItemProperty -LiteralPath $msiKey -ErrorAction SilentlyContinue).MSISupported
                    if ($v -eq 1) { $msiOn = $true }
                }
            }
        }
    } catch { }
    if ($msiSeen) {
        if ($msiOn) { [void]$oks.Add('MSI enabled on NVIDIA PCI device') }
        else { [void]$issues.Add('MSISupported=0 on NVIDIA device (reinstall with MSI High)') }
    } else {
        [void]$oks.Add('MSI registry not exposed (skipped)')
    }

    # OptiHub remembered this exact driver version as tweaked
    $remembered = $false
    if (Test-Path $StatePath) {
        try {
            $st = Get-Content $StatePath -Raw -Encoding UTF8 | ConvertFrom-Json
            $win = Get-WindowsDriverVersionString
            $cur = Convert-WindowsDriverToNvidia $win
            if ($st.driverTweaksVersion -and $cur -and $st.driverTweaksVersion -eq $cur) {
                $remembered = $true
                [void]$oks.Add("OptiHub recorded tweaks for driver $cur")
            }
        } catch { }
    }

    # Pass if remembered for this version, or no hard issues
    $ok = $remembered -or ($issues.Count -eq 0)
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
        $versionBehind = $true
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
        Write-Warn 'Stock-style driver signals — OptiHub will apply clean-driver tweaks'
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
        Write-Step 'Version is newest — applying OptiHub tweaks in-place (no re-download)'
        try {
            Apply-OptiHubDriverInstallTweaks
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
                Tweaks          = $tweaks
                Method          = 'in-place-tweaks'
            }
        } catch {
            Write-Warn "In-place tweaks failed: $($_.Exception.Message)"
        }
    }

    # Full OptiHub Clean Driver install (our NVCleanstall-class pipeline)
    if (-not $dl) {
        Write-Warn 'No official download URL from NVIDIA API — cannot run OptiHub Clean Driver'
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
            Write-Warn 'SkipDownload set — cannot fetch driver package'
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
        try { Apply-OptiHubDriverInstallTweaks } catch { Write-Warn "Post-install tweaks: $($_.Exception.Message)" }
        Write-Ok 'OptiHub Clean Driver complete (clean install). Continuing 3D profile + App — no forced reboot.'
        Write-HubProgress 70 'Clean driver installed — continuing pipeline'
        # NeedsUpdate = false so main does NOT stop; clean install is enough to keep going (NVCleanstall-style).
        return @{
            Ran             = $true
            NeedsUpdate     = $false
            NeedsRetweak    = $false
            TweaksOk        = $true
            Reason          = $reason
            CurrentVersion  = $currentNv
            LatestVersion   = $latestVer
            WindowsVersion  = $winVer
            DownloadUrl     = $dl
            Method          = 'optihub-clean'
            Install         = $install
            Tweaks          = $tweaks
            ContinuePipeline = $true
        }
    }

    # No third-party GUI fallback — surface clear failure so user can re-run after network/disk issues.
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
    # Real apply path: drive NVIDIA Control Panel UI.
    # Order matters: Use NVIDIA color settings → unlock RGB/Full/bpc → Apply → Keep/don't-revert.
    # Then scaling page: GPU + No scaling + Override → Apply → Keep.
    Write-Step 'Display color / scaling via Control Panel UI (NVIDIA color + Apply + Keep)...'
    $applied = New-Object System.Collections.Generic.List[string]

    [void](Ensure-NvidiaControlPanel)
    $eulaJob = Accept-NvidiaControlPanelEula

    # Registry hints still help Gestalt / future CPL sessions
    foreach ($nvTweak in @(
        'HKCU:\Software\NVIDIA Corporation\Global\NVTweak',
        'HKLM:\SOFTWARE\NVIDIA Corporation\Global\NVTweak'
    )) {
        if (-not (Test-Path $nvTweak)) { try { New-Item -Path $nvTweak -Force | Out-Null } catch { } }
        if (Test-Path $nvTweak) {
            Set-ItemProperty -Path $nvTweak -Name 'Gestalt' -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
        }
    }
    $client = 'HKCU:\Software\NVIDIA Corporation\NVControlPanel2\Client'
    if (-not (Test-Path $client)) { try { New-Item -Path $client -Force | Out-Null } catch { } }
    if (Test-Path $client) {
        Set-ItemProperty -Path $client -Name 'ShowSedoanEula' -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $client -Name 'EulaAccepted' -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
    }

    # Wait for EULA assist briefly
    try {
        if ($eulaJob) {
            Wait-Job $eulaJob -Timeout 12 | Out-Null
            Stop-Job $eulaJob -ErrorAction SilentlyContinue
            Remove-Job $eulaJob -Force -ErrorAction SilentlyContinue
        }
    } catch { }

    # Full UI automation: NVIDIA color unlock → values → Apply → Keep changes / don't revert
    $cplScript = Join-Path $Root 'OptiHub-Cpl-ApplyDisplay.ps1'
    if (-not (Test-Path -LiteralPath $cplScript)) {
        Write-Warn "Missing $cplScript — cannot auto-click CPL Apply/Keep"
        [void]$applied.Add('CPL UI script missing')
    } else {
        Write-Ok 'Driving Control Panel UI (this clicks Apply and confirms Keep/Yes)...'
        Write-HubProgress 92 'Control Panel: NVIDIA color + Apply + Keep...'
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & $cplScript 2>&1 | ForEach-Object {
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
                [void]$applied.Add('CPL UI automation completed (Apply + Keep handled)')
                Write-Ok 'Control Panel settings applied (including Apply + keep confirmation)'
            } else {
                [void]$applied.Add("CPL UI automation exit $code")
                Write-Warn "CPL UI automation exit $code — open Control Panel and Apply once if needed"
            }
        } catch {
            Write-Warn "CPL UI automation failed: $($_.Exception.Message)"
            [void]$applied.Add("CPL UI error: $($_.Exception.Message)")
        } finally {
            $ErrorActionPreference = $prev
        }
    }

    $pref = Join-Path $StateDir 'nvidia-display-prefs.json'
    $obj = [ordered]@{
        colorSource         = 'NVIDIA'
        outputColorFormat   = 'RGB'
        outputDynamicRange  = 'Full'
        outputColorDepth    = '10 bpc when available'
        performScalingOn    = 'GPU'
        scalingMode         = 'No scaling'
        overrideGameScaling = $true
        appliedVia          = 'ControlPanel-UI-Apply-Keep'
        flow                = 'Use NVIDIA color settings -> RGB/Full/10bpc -> Apply -> Keep; then GPU/No scaling/Override -> Apply -> Keep'
    }
    [IO.File]::WriteAllText($pref, ($obj | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    [void]$applied.Add('Saved OptiHub display preference manifest')

    foreach ($a in $applied) { Write-Ok $a }
    return [string[]]@($applied.ToArray())
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

    # --- 1) Newest driver (OptiHub Clean Driver = clean install, then keep going — no forced reboot) ---
    $driverInfo = @{ Ran = $false; NeedsUpdate = $false; TweaksOk = $true; Method = 'none' }
    if (-not $SkipDriver) {
        Write-HubProgress 20 'Checking for newest Game Ready driver...'
        $driverInfo = Coerce-Hashtable (Start-DriverUpdateIfNeeded -Force:([bool]$ForceDriver))
        if (-not $driverInfo) { $driverInfo = @{ Ran = $false; NeedsUpdate = $false; TweaksOk = $true; Method = 'none' } }

        $method = [string]$driverInfo.Method
        if ($method -eq 'failed-clean' -or $method -eq 'failed-no-url') {
            Save-State @{
                version            = $Script:NvidiaOptVersion
                appliedUtc         = (Get-Date).ToUniversalTime().ToString('o')
                gpuName            = $primary.Name
                driver             = $primary.Driver
                series             = $seriesId
                gsync              = $useGsync
                driverUpdatePass   = $driverInfo
                pendingAfterDriver = $true
            }
            Write-Warn 'OptiHub Clean Driver did not finish. Fix the issue above and Apply again.'
            Write-HubProgress 100 'Clean driver failed'
            Write-Output 'DONE - OptiHub Clean Driver failed. See log, then Apply again.'
            exit 1
        }

        if ($method -eq 'optihub-clean' -and $driverInfo.Ran) {
            Write-Ok 'Clean driver installed — continuing straight into 3D profile + App (no reboot gate)'
            Write-HubProgress 35 'Clean driver OK — applying 3D profile next...'
        }
    } else {
        Write-Ok 'Driver check skipped (-SkipDriver)'
    }

    # --- 2) 3D Base Profile (right after driver) ---
    $nip = $null
    $npi = $null
    $profileImport = $null
    $profileApplied = $false
    if (-not $SkipProfile) {
        $nip = Get-ProfileFile $seriesId $useGsync
        if (-not $nip) { throw "Missing profile for series $seriesId (G-SYNC=$useGsync)" }
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

    # --- 3) Client stack: remove App leftovers -> GFE cleanup -> Control Panel only -> overlay off ---
    # NVIDIA App is NOT used (unless -InstallApp). Display = Control Panel only.
    Write-HubProgress 56 'Removing NVIDIA App leftovers (if any)...'
    Remove-NvidiaAppLeftovers

    Write-HubProgress 58 'Cleaning GFE / Overlay leftovers (keeping Control Panel)...'
    $cleaned = [int](Remove-ConflictingNvidiaClients)

    Write-HubProgress 64 'NVIDIA Control Panel (display settings UI)...'
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
        Write-Ok 'NVIDIA App skipped (Control Panel only for display) — pass -InstallApp to add App'
    }

    Write-HubProgress 76 'Disabling NVIDIA Overlay...'
    Disable-NvidiaOverlay

    Write-HubProgress 82 'Privacy / debloat...'
    Disable-NvidiaTelemetry

    Write-HubProgress 90 'Display color / scaling (Control Panel)...'
    $disp = @(Set-NvidiaDisplayPreferences)

    Write-HubProgress 94 'Saving status...'
    # Remember this driver version as tweak-OK so detect won't re-prompt until the version changes.
    $tweaksVer = $null
    if ($driverInfo -and $driverInfo.CurrentVersion) {
        $tweaksVer = [string]$driverInfo.CurrentVersion
    } else {
        try {
            $tweaksVer = Convert-WindowsDriverToNvidia (Get-WindowsDriverVersionString)
        } catch { $tweaksVer = $null }
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
        profileImport       = $profileImport
        npiPath             = $npi
        nvidiaApp           = $appInstalled
        nvidiaControlPanel  = [bool]$cplOk
        displayPrefs        = $disp
        debloatApplied      = $true
        overlayDisabled     = $true
        conflictCleanup     = $cleaned
        driverUpdatePass    = $driverInfo
        pendingAfterDriver  = $false
        driverTweaksVersion = $tweaksVer
    }

    Write-Ok 'NVIDIA Optimizer finished'
    Write-Ok 'In Control Panel: Display > Adjust desktop size and position = GPU + No scaling + Override (both monitors).'
    Write-Ok 'Display > Change resolution: Output color to NVIDIA settings / Full RGB when listed.'
    if ($driverInfo.Method -eq 'optihub-clean') {
        Write-Ok 'Clean install completed in one pass (driver + 3D + Control Panel display). No forced reboot.'
    }
    Write-HubProgress 100 'Completed successfully'
    Write-Output "DONE - NVIDIA $seriesId$(if($useGsync){' GSync'}else{' No Gsync'}) (clean driver -> 3D profile -> CPL display)"
    exit 0
} catch {
    Write-Err $_.Exception.Message
    Write-HubProgress 100 'Failed'
    exit 1
}
