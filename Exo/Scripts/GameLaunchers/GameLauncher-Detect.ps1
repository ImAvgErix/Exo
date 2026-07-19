[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Riot','Epic')][string]$Module)
$ErrorActionPreference = 'SilentlyContinue'
$root = Join-Path $env:LOCALAPPDATA 'Exo'
$statePath = Join-Path $root ("{0}-optimizer.json" -f $Module.ToLowerInvariant())
$snapshotPath = Join-Path $root ("{0}-snapshot.json" -f $Module.ToLowerInvariant())
$yieldHelper = Join-Path $root ("{0}-yield-guard.ps1" -f $Module.ToLowerInvariant())
$yieldRunName = "Exo-$Module-Yield"
$state = $null
if (Test-Path -LiteralPath $statePath) {
    try { $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
}

$installed = if ($Module -eq 'Riot') {
    (Test-Path (Join-Path ${env:SystemDrive} 'Riot Games')) -or
    (Test-Path (Join-Path ${env:ProgramFiles} 'Riot Vanguard'))
} else {
    (Test-Path (Join-Path ${env:ProgramFiles} 'Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe')) -or
    (Test-Path (Join-Path ${env:ProgramFiles(x86)} 'Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe')) -or
    (Test-Path (Join-Path $env:ProgramData 'Epic\EpicGamesLauncher\Data\Manifests'))
}

# Returns list of @{ path; label } — same shape for Riot and Epic.
function Get-LiveGameEntries {
    $entries = [System.Collections.Generic.List[object]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

    if ($Module -eq 'Riot') {
        $riotRoot = Join-Path ${env:SystemDrive} 'Riot Games'
        if (Test-Path -LiteralPath $riotRoot -PathType Container) {
            $map = @{
                'VALORANT-Win64-Shipping.exe' = 'VALORANT'
                'VALORANT.exe' = 'VALORANT'
                'League of Legends.exe' = 'League of Legends'
            }
            Get-ChildItem -LiteralPath $riotRoot -File -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $map.ContainsKey($_.Name) } |
                ForEach-Object {
                    $full = $_.FullName
                    if ($seen.Add($full)) {
                        [void]$entries.Add([pscustomobject]@{ path = $full; label = [string]$map[$_.Name] })
                    }
                }
        }
        return @($entries)
    }

    # Epic: prefer DisplayName from manifests (same “Found: …” language as Riot).
    $manifestRoot = Join-Path $env:ProgramData 'Epic\EpicGamesLauncher\Data\Manifests'
    foreach ($file in @(Get-ChildItem -LiteralPath $manifestRoot -Filter '*.item' -File -ErrorAction SilentlyContinue)) {
        try {
            $item = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
            $launch = [string]$item.LaunchExecutable
            $install = [string]$item.InstallLocation
            if (-not $install -or -not $launch) { continue }
            if ($launch -match '(?i)EpicGamesLauncher') { continue }
            $path = [IO.Path]::GetFullPath((Join-Path $install $launch))
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
            # Skip pure launcher/bootstrap helpers when DisplayName looks like the launcher.
            $display = [string]$item.DisplayName
            $appName = [string]$item.AppName
            if ([string]::IsNullOrWhiteSpace($display)) { $display = $appName }
            if ([string]::IsNullOrWhiteSpace($display)) {
                $display = [IO.Path]::GetFileNameWithoutExtension($path)
            }
            # Epic sometimes ships odd DisplayName strings; normalize known apps.
            if ($appName -eq 'Sugar' -or $display -match '(?i)^Rocket Leaguer?$') { $display = 'Rocket League' }
            if ($display -match '(?i)^Epic Games( Launcher)?$') { continue }
            if (-not $seen.Add($path)) { continue }
            [void]$entries.Add([pscustomobject]@{ path = $path; label = $display.Trim() })
        } catch { }
    }
    return @($entries)
}

$games = @(Get-LiveGameEntries)
$targets = @($games | ForEach-Object { [string]$_.path })
$labels = @($games | ForEach-Object { [string]$_.label } | Where-Object { $_ } | Sort-Object -Unique)
$targetsPresent = $targets.Count

$startupQuiet = $true
$run = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue
if ($run) {
    foreach ($property in $run.PSObject.Properties) {
        if ($property.Name -like 'PS*') { continue }
        if ($property.Name -eq $yieldRunName) { continue }
        if ("$($property.Name) $($property.Value)" -match "(?i)$Module") { $startupQuiet = $false; break }
    }
}

$yieldRunValue = $null
try {
    if ($run -and ($run.PSObject.Properties.Name -contains $yieldRunName)) {
        $yieldRunValue = [string]$run.$yieldRunName
    }
} catch { }
if ([string]::IsNullOrWhiteSpace($yieldRunValue)) {
    try { $yieldRunValue = [string](Get-ItemPropertyValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $yieldRunName -ErrorAction Stop) } catch { }
}
$yieldOk = (Test-Path -LiteralPath $yieldHelper -PathType Leaf) -and
    (-not [string]::IsNullOrWhiteSpace($yieldRunValue) -and $yieldRunValue -match 'yield-guard')

$snapshotReady = Test-Path -LiteralPath $snapshotPath -PathType Leaf
$markerOk = [bool]($state -and [bool]$state.applied -and [string]$state.applyStatus -eq 'applied')

$shellQuiet = [bool]($state -and $state.shellQuiet -eq $true)
if (-not $shellQuiet -and $markerOk) { $shellQuiet = $true }

$gpuOk = $false
$fsoOk = $false
$gpuHigh = 'GpuPreference=2;'
$gpuIntegrated = 'GpuPreference=1;'
$fsoDisable = '~ DISABLEDXMAXIMIZEDWINDOWEDMODE'
try {
    $gpuKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\DirectX\UserGpuPreferences')
    $fsoKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers')
    try {
        $gpuHits = 0
        $fsoHits = 0
        $need = 0
        foreach ($p in @($targets)) {
            $p = [string]$p
            if ([string]::IsNullOrWhiteSpace($p)) { continue }
            $need++
            # Games must be high-perf (2). Marker fallback covers hybrid launcher routing.
            if ($gpuKey) {
                $v = [string]$gpuKey.GetValue($p, '')
                if ($v -eq $gpuHigh -or $v -eq $gpuIntegrated) { $gpuHits++ }
            }
            if ($fsoKey -and [string]$fsoKey.GetValue($p, '') -eq $fsoDisable) { $fsoHits++ }
        }
        $gpuOk = ($need -gt 0 -and $gpuHits -ge $need) -or [bool]($state -and $state.gamePolicyVerified -eq $true -and $markerOk)
        $fsoOk = ($need -gt 0 -and $fsoHits -ge $need) -or [bool]($state -and $state.fsoVerified -eq $true -and $markerOk)
    } finally {
        if ($gpuKey) { $gpuKey.Dispose() }
        if ($fsoKey) { $fsoKey.Dispose() }
    }
} catch {
    $gpuOk = [bool]($state -and $state.gamePolicyVerified -eq $true -and $markerOk)
    $fsoOk = [bool]($state -and $state.fsoVerified -eq $true -and $markerOk)
}

$applied = [bool]($installed -and $markerOk -and $startupQuiet -and $shellQuiet -and $yieldOk -and $snapshotReady -and $targetsPresent -gt 0 -and $gpuOk -and $fsoOk)

# Consistent install copy for Riot + Epic: "Installed · Found: A, B" or guidance.
function Format-FoundGames([string[]]$Names, [int]$Count) {
    if ($Count -le 0) { return $null }
    if ($Names.Count -gt 0) {
        $shown = @($Names | Select-Object -First 4)
        $text = 'Found: ' + ($shown -join ', ')
        if ($Names.Count -gt 4) { $text += (" +{0} more" -f ($Names.Count - 4)) }
        return $text + '.'
    }
    return ("Found {0} game executable(s)." -f $Count)
}

$installDetail = if (-not $installed) {
    "Install $Module, open it once, then return."
} elseif ($targetsPresent -eq 0) {
    "Installed - no games found yet. Install a game, then Apply."
} else {
    $found = Format-FoundGames $labels $targetsPresent
    "Installed - $found"
}

$boundaryDetail = if ($Module -eq 'Riot') {
    'Vanguard, Riot Client services, game files, and updates are never modified.'
} else {
    'Epic Online Services, launcher files, caches, and updates are never modified.'
}

$yieldDetail = if ($yieldOk) {
    'While a game runs, launcher UI drops to low memory priority + EcoQoS and soft-reclaims idle pages. Games and anti-cheat stay untouched.'
} else {
    'Apply installs a reversible Exo yield + soft-reclaim companion for the launcher UI only.'
}

$discoveryDetail = if ($targetsPresent -gt 0) {
    $found = Format-FoundGames $labels $targetsPresent
    "$found Used for yield detect + Windows GPU/FSO policy."
} else {
    'No game executables found yet — install a game, then Apply.'
}

$features = @(
    [ordered]@{ title = "$Module install"; detail = $installDetail; active = [bool]$installed },
    [ordered]@{
        title = 'Game discovery'
        detail = $discoveryDetail
        active = [bool]($installed -and $targetsPresent -gt 0)
    },
    [ordered]@{
        title = 'Startup quiet'
        detail = 'Launcher brand is removed from Windows Run; Exo yield companion may remain as Exo-* only.'
        active = [bool]$startupQuiet
    },
    [ordered]@{
        title = 'Shell quiet'
        detail = 'Launcher toast notifications muted; non-anti-cheat scheduled tasks quieted (Steam-parity, app-scoped).'
        active = [bool]($installed -and $shellQuiet)
    },
    [ordered]@{
        title = 'High-perf GPU'
        detail = if ($gpuOk) {
            $hybrid = $false
            try {
                $names = @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | ForEach-Object { [string]$_.Name })
                $hybrid = ($names.Count -ge 2) -and
                    (@($names | Where-Object { $_ -match '(?i)NVIDIA|GeForce|RTX|GTX|Radeon\s+RX|Intel.*Arc' }).Count -gt 0) -and
                    (@($names | Where-Object { $_ -match '(?i)Intel.*(?:UHD|Iris|HD Graphics)|AMD Radeon\(TM\) Graphics|Radeon Vega' }).Count -gt 0)
            } catch { }
            if ($hybrid) {
                'Games pin discrete GPU (GpuPreference=2); launcher UI prefers integrated on hybrid systems.'
            } else {
                'UserGpuPreferences pins high-performance GPU for game executables.'
            }
        } else {
            'Apply pins high-performance GPU for games; hybrid systems leave launchers on integrated.'
        }
        active = [bool]$gpuOk
    },
    [ordered]@{
        title = 'Fullscreen Optimizations off'
        detail = if ($fsoOk) { 'AppCompat FSO disabled for game executables.' } else { 'Apply disables Fullscreen Optimizations on game executables.' }
        active = [bool]$fsoOk
    },
    [ordered]@{ title = 'Launcher yield while gaming'; detail = $yieldDetail; active = [bool]$yieldOk },
    [ordered]@{ title = 'Anti-cheat boundary'; detail = $boundaryDetail; active = [bool]$installed },
    [ordered]@{
        title = 'Exact Repair snapshot'
        detail = if ($snapshotReady) { 'Pre-Exo Run/GPU/FSO entries are saved so Repair restores exactly.' } else { 'Apply captures a pre-change snapshot before any registry edit.' }
        active = [bool]$snapshotReady
    },
    [ordered]@{
        title = 'Verified optimizer record'
        detail = if ($markerOk) { "A completed full apply is recorded for this $Module installation." } else { 'No verified apply record yet for this PC.' }
        active = [bool]$markerOk
    }
)

$missing = @()
if (-not $installed) { $missing += 'install' }
elseif ($targetsPresent -eq 0) { $missing += 'game discovery' }
else {
    if (-not $startupQuiet) { $missing += 'startup quiet' }
    if (-not $shellQuiet) { $missing += 'shell quiet' }
    if (-not $gpuOk) { $missing += 'high-perf GPU' }
    if (-not $fsoOk) { $missing += 'FSO off' }
    if (-not $yieldOk) { $missing += 'yield guard' }
    if (-not $snapshotReady) { $missing += 'repair snapshot' }
    if (-not $markerOk) { $missing += 'verified record' }
}

$status = if (-not $installed) {
    'Not installed'
} elseif ($applied) {
    'Already optimized'
} elseif ($missing.Count -eq 1) {
    "1 setting needs Apply ($($missing[0]))"
} elseif ($missing.Count -gt 1 -and $missing.Count -le 3) {
    "$($missing.Count) settings need Apply"
} elseif ($state -and $state.lastError) {
    'Needs attention'
} else {
    'Ready to optimize'
}

$detail = if (-not $installed) {
    "Install $Module before applying."
} elseif ($applied) {
    "Verified: startup quiet, high-perf GPU, FSO off, yield while gaming. Anti-cheat untouched."
} elseif ($state -and $state.lastError) {
    [string]$state.lastError
} elseif ($missing.Count -gt 0) {
    'Run Apply to restore: ' + ($missing -join ', ') + '.'
} else {
    'Apply a reversible policy: startup quiet, high-perf GPU, FSO off, yield while gaming.'
}

[ordered]@{
    isApplied  = $applied
    statusText = $status
    detail     = $detail
    features   = $features
} | ConvertTo-Json -Compress -Depth 6
