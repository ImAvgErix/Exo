#Requires -Version 7.0
# Exo Windows detect  -  fast path: registry + state JSON only (no DISM, no full task catalog, minimal powercfg).
$ErrorActionPreference = 'SilentlyContinue'

$common = Join-Path (Split-Path -Parent $PSScriptRoot) 'lib\Exo.Common.ps1'
if (-not (Test-Path -LiteralPath $common) -and $env:LOCALAPPDATA) {
    $common = Join-Path $env:LOCALAPPDATA 'Exo\app\Scripts\lib\Exo.Common.ps1'
}
# Load only what detect needs (not every shared lib)
if (Test-Path -LiteralPath $common) { . $common }
$libDir = if (Get-Command Get-ExoLibDir -ErrorAction SilentlyContinue) {
    Get-ExoLibDir -From $PSScriptRoot
} else {
    Join-Path (Split-Path -Parent $PSScriptRoot) 'lib'
}
foreach ($name in @('Exo.GameBar.ps1', 'Exo.GamingStack.ps1', 'Exo.NoBackground.ps1', 'Exo.ScheduledTasks.ps1', 'Exo.OptionalFeatures.ps1', 'Exo.ShellDebloat.ps1', 'Exo.Controllers.ps1', 'Exo.InputDevices.ps1', 'Exo.WindowsUpdate.ps1', 'Exo.DefenderPurge.ps1')) {
    $p = Join-Path $libDir $name
    if (Test-Path -LiteralPath $p) { . $p }
}

$statePath = Join-Path $env:LOCALAPPDATA 'Exo\windows-optimizer.json'
$state = $null
if (Test-Path -LiteralPath $statePath) {
    try { $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
}

$t = if (Get-Command Test-ExoCompetitiveGamingGlue -ErrorAction SilentlyContinue) {
    Test-ExoCompetitiveGamingGlue
} else {
    [pscustomobject]@{
        gameBar = $false; hags = $false; gameMode = $false; win32Priority = $false
        mousePrecision = $false; mpo = $false; stickyKeys = $false; ok = $false
    }
}

$menuOk = $false
try {
    $menuOk = [int](Get-ItemPropertyValue -Path 'HKCU:\Control Panel\Desktop' -Name 'MenuShowDelay' -ErrorAction Stop) -eq 0
} catch { }

# Power plan: prefer state / name-only powercfg string (no scheme GUID walks)
$powerOk = $false
$planTitle = 'Exo competitive power plan'
$planDetail = 'Custom Intel/AMD scheme with hidden CPU parking, boost, PCIe ASPM, USB suspend knobs.'
if ($state -and $state.powerPlanOk -eq $true) {
    $powerOk = $true
    if ($state.powerPlanName) { $planTitle = [string]$state.powerPlanName }
    if ($state.powerPlanVendor) { $planTitle = "Exo power plan ($($state.powerPlanVendor))" }
} else {
    try {
        $active = powercfg /getactivescheme 2>$null | Out-String
        $powerOk = [bool]($active -match 'Exo Competitive|(?i)High performance|Ultimate Performance')
    } catch { }
}

$hostOk = if ($state -and $state.hostLatencyOk -eq $true) { $true }
elseif (Get-Command Test-ExoHostLatencyProfile -ErrorAction SilentlyContinue) { [bool](Test-ExoHostLatencyProfile) }
else { $false }

$markerOk = $false
if ($state) {
    try {
        $markerOk = ([string]$state.applyStatus -eq 'applied') -and ($state.applied -eq $true)
    } catch { }
}

$mouseOk = if (Get-Command Test-ExoMouseGaming -ErrorAction SilentlyContinue) { [bool](Test-ExoMouseGaming) } else { [bool]$t.mousePrecision }
$kbdOk = if (Get-Command Test-ExoKeyboardGaming -ErrorAction SilentlyContinue) { [bool](Test-ExoKeyboardGaming) } else { $false }
$micOk = if (Get-Command Test-ExoMicCommunications -ErrorAction SilentlyContinue) { [bool](Test-ExoMicCommunications) } else { $false }
$usbOk = if (Get-Command Test-ExoUsbPowerGaming -ErrorAction SilentlyContinue) { [bool](Test-ExoUsbPowerGaming) } else { $false }
$deskOk = if (Get-Command Test-ExoDesktopSnappiness -ErrorAction SilentlyContinue) { [bool](Test-ExoDesktopSnappiness) } else { $false }
$amoledOk = if (Get-Command Test-ExoAmoledTheme -ErrorAction SilentlyContinue) { [bool](Test-ExoAmoledTheme) } else { [bool]($state -and $state.amoledOk) }

$features = [System.Collections.Generic.List[object]]::new()
foreach ($row in @(
    [ordered]@{ title = 'Xbox Game Bar quiet'; detail = 'Game Bar and DVR stay quiet while you play.'; active = [bool]$t.gameBar },
    [ordered]@{ title = 'Hardware GPU scheduling'; detail = 'HAGS on for lower CPU tax.'; active = [bool]$t.hags },
    [ordered]@{ title = 'Windows Game Mode'; detail = 'Game Mode on for focused games.'; active = [bool]$t.gameMode },
    [ordered]@{ title = 'Foreground boost'; detail = 'Foreground process priority bias for gaming.'; active = [bool]$t.win32Priority },
    [ordered]@{ title = 'Smoother multi-monitor'; detail = 'MPO disabled to reduce hitching.'; active = [bool]$t.mpo },
    [ordered]@{ title = 'No sticky-key popups'; detail = 'Sticky Keys prompts disabled.'; active = [bool]$t.stickyKeys },
    [ordered]@{ title = $planTitle; detail = $planDetail; active = [bool]$powerOk },
    [ordered]@{ title = 'Host latency profile'; detail = 'Power throttling off, MMCSS SystemResponsiveness tuned, Games task High.'; active = [bool]$hostOk },
    [ordered]@{ title = 'Raw mouse feel'; detail = 'Pointer acceleration off, no trails, no snap-to-button.'; active = [bool]$mouseOk },
    [ordered]@{ title = 'Fast keyboard repeat'; detail = 'Shortest key delay and max repeat rate.'; active = [bool]$kbdOk },
    [ordered]@{ title = 'No mic ducking'; detail = 'Communications set to Do nothing.'; active = [bool]$micOk },
    [ordered]@{ title = 'USB always awake'; detail = 'USB selective suspend off.'; active = [bool]$usbOk },
    [ordered]@{ title = 'Snappy desktop'; detail = 'Zero menu delay, no startup delay.'; active = [bool]$deskOk },
    [ordered]@{ title = 'AMOLED pure black'; detail = 'Dark theme pure black UI surfaces.'; active = [bool]$amoledOk },
    [ordered]@{ title = 'Instant menus'; detail = 'MenuShowDelay 0.'; active = [bool]$menuOk }
)) { [void]$features.Add($row) }

# Shell pack  -  registry tests only
$uacOk = if (Get-Command Test-ExoUacNeverNotify -EA SilentlyContinue) { [bool](Test-ExoUacNeverNotify) } else { [bool]($state.uacOff) }
$aiOk = if (Get-Command Test-ExoWindowsAiGone -EA SilentlyContinue) { [bool](Test-ExoWindowsAiGone) } else { [bool]($state.windowsAiGone) }
$exOk = if (Get-Command Test-ExoExplorerDeclutter -EA SilentlyContinue) { [bool](Test-ExoExplorerDeclutter) } else { [bool]($state.explorerDecluttered) }
$inOk = if (Get-Command Test-ExoInboxAppsOptimized -EA SilentlyContinue) { [bool](Test-ExoInboxAppsOptimized) } else { $false }
[void]$features.Add([ordered]@{ title = 'No UAC prompts'; detail = 'Admin elevation without prompts.'; active = [bool]$uacOk })
[void]$features.Add([ordered]@{ title = 'Windows AI removed'; detail = 'Copilot/Recall/AI components off.'; active = [bool]$aiOk })
[void]$features.Add([ordered]@{ title = 'Explorer decluttered'; detail = 'Recycle bin off desktop; This PC default.'; active = [bool]$exOk })
[void]$features.Add([ordered]@{ title = 'Inbox apps quiet'; detail = 'Photos/Snipping background + web search off.'; active = [bool]$inOk })

$padOk = if (Get-Command Test-ExoControllersReady -EA SilentlyContinue) { [bool](Test-ExoControllersReady) } else { [bool]($state.controllersOk) }
[void]$features.Add([ordered]@{ title = 'Controllers stay awake'; detail = 'USB power-save off for gamepads.'; active = [bool]$padOk })
[void]$features.Add([ordered]@{ title = 'Controller overlays quiet'; detail = 'Xbox Game Bar overlays quieted.'; active = [bool]$padOk })

$wuOk = if (Get-Command Test-ExoWindowsUpdatePaused -EA SilentlyContinue) { [bool](Test-ExoWindowsUpdatePaused) } else { [bool]($state.windowsUpdatePaused) }
$defOk = if (Get-Command Test-ExoDefenderPurged -EA SilentlyContinue) { [bool](Test-ExoDefenderPurged) } else { [bool]($state.defenderPurged) }
[void]$features.Add([ordered]@{ title = 'Windows Update paused'; detail = 'Max pause/defer + auto-update policy off.'; active = [bool]$wuOk })
[void]$features.Add([ordered]@{ title = 'Defender purged'; detail = 'Realtime policy off, services disabled when possible.'; active = [bool]$defOk })

$noBgOk = if (Get-Command Test-ExoNoBackground -ErrorAction SilentlyContinue) { [bool](Test-ExoNoBackground) } else { $true }
$tasksOk = if (Get-Command Test-ExoScheduledTasksQuieted -ErrorAction SilentlyContinue) { [bool](Test-ExoScheduledTasksQuieted) } else { [bool]($state.scheduledTasksOk) }
$optFeatOk = if (Get-Command Test-ExoOptionalFeaturesQuieted -ErrorAction SilentlyContinue) { [bool](Test-ExoOptionalFeaturesQuieted) } else { [bool]($state.optionalFeaturesOk) }

[void]$features.Add([ordered]@{
    title = 'No Exo background'
    detail = 'No noisy Exo console Run keys. Silent yield companions allowed.'
    active = [bool]$noBgOk
})
[void]$features.Add([ordered]@{
    title = 'Scheduled tasks quieted'
    detail = 'Telemetry/CEIP-style tasks disabled on last Apply (detect uses marker  -  no full task scan).'
    active = [bool]$tasksOk
})
[void]$features.Add([ordered]@{
    title = 'Optional components quieted'
    detail = 'DISM shortlist applied on last Apply (SMB1/Fax/etc).'
    active = [bool]$optFeatOk
})
[void]$features.Add([ordered]@{
    title = 'Optimization verified'
    detail = if ($markerOk) { 'A completed Windows apply is on record for this PC.' } else { 'No verified Windows apply yet  -  run Apply.' }
    active = [bool]$markerOk
})

$coreOk = [bool]$t.gameBar -and [bool]$t.hags -and [bool]$t.gameMode -and [bool]$t.win32Priority
$missing = @()
foreach ($f in $features) {
    if (-not [bool]$f.active -and [string]$f.title -ne 'Optimization verified') {
        $missing += [string]$f.title
    }
}
# Honest applied: core + marker + no checkable gaps
$isApplied = $coreOk -and $markerOk -and $noBgOk -and ($missing.Count -eq 0)

$statusText = if ($isApplied) { 'Already optimized' }
elseif ($missing.Count -eq 1) { "1 setting needs Apply ($($missing[0]))" }
elseif ($missing.Count -gt 1) { "$($missing.Count) settings need Apply" }
else { 'Ready to optimize' }

$detail = if ($isApplied) {
    "Host stack applied: $planTitle, Game Mode, HAGS, Game Bar quiet, priority, host latency."
} elseif ($missing.Count -gt 0) {
    'Off: ' + ($missing -join ', ') + '.'
} else {
    'Windows host stack: CPU-matched Exo power plan + Game Mode/HAGS/priority.'
}

[ordered]@{
    isApplied  = [bool]$isApplied
    statusText = $statusText
    detail     = $detail
    features   = @($features)
} | ConvertTo-Json -Compress -Depth 6
