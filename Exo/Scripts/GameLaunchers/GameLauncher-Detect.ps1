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

function Get-LiveTargets {
    $paths = [System.Collections.Generic.List[string]]::new()
    if ($Module -eq 'Riot') {
        $riotRoot = Join-Path ${env:SystemDrive} 'Riot Games'
        if (Test-Path -LiteralPath $riotRoot -PathType Container) {
            $names = @('VALORANT-Win64-Shipping.exe','VALORANT.exe','League of Legends.exe')
            Get-ChildItem -LiteralPath $riotRoot -File -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -in $names } | ForEach-Object { [void]$paths.Add($_.FullName) }
        }
    } else {
        $manifestRoot = Join-Path $env:ProgramData 'Epic\EpicGamesLauncher\Data\Manifests'
        foreach ($file in @(Get-ChildItem -LiteralPath $manifestRoot -Filter '*.item' -File -ErrorAction SilentlyContinue)) {
            try {
                $item = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
                $path = Join-Path ([string]$item.InstallLocation) ([string]$item.LaunchExecutable)
                if ($path -and (Test-Path -LiteralPath $path -PathType Leaf) -and $path -notmatch '(?i)EpicGamesLauncher') {
                    [void]$paths.Add([IO.Path]::GetFullPath($path))
                }
            } catch { }
        }
    }
    return @($paths | Sort-Object -Unique)
}

function Get-TargetLabels([string[]]$Paths) {
    $labels = [System.Collections.Generic.List[string]]::new()
    foreach ($path in $Paths) {
        $name = [IO.Path]::GetFileNameWithoutExtension($path)
        if ($name -match '(?i)VALORANT') { $label = 'VALORANT' }
        elseif ($name -match '(?i)League') { $label = 'League of Legends' }
        else { $label = $name }
        if ($label -and $labels -notcontains $label) { [void]$labels.Add($label) }
    }
    return @($labels)
}

$targets = @(Get-LiveTargets)
$targetsPresent = $targets.Count
$labels = @(Get-TargetLabels $targets)

$startupQuiet = $true
$run = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue
if ($run) {
    foreach ($property in $run.PSObject.Properties) {
        if ($property.Name -like 'PS*') { continue }
        # Exo yield Run entry is intentional; do not count as launcher autostart.
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

# Launcher-scoped only. Windows GPU preference / FSO belong in a future Windows module.
$applied = [bool]($installed -and $markerOk -and $startupQuiet -and $yieldOk -and $snapshotReady -and $targetsPresent -gt 0)

$installDetail = if (-not $installed) {
    "Install $Module, open it once, then return."
} elseif ($targetsPresent -eq 0) {
    "Client found; install at least one game so Exo can attach the launcher yield companion."
} elseif ($labels.Count -gt 0) {
    "Found: " + ($labels -join ', ') + "."
} else {
    "$targetsPresent game executable(s) discovered."
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

# 7 tiles: launcher-scoped only (no Windows Graphics / FSO policy here).
$features = @(
    [ordered]@{ title = "$Module install"; detail = $installDetail; active = [bool]$installed },
    [ordered]@{
        title = 'Game discovery'
        detail = if ($targetsPresent -gt 0) { "$targetsPresent executable(s) used only to detect when a game is running." } else { 'No game executables found yet - install a game, then Apply.' }
        active = [bool]($installed -and $targetsPresent -gt 0)
    },
    [ordered]@{
        title = 'Startup quiet'
        detail = 'Launcher brand is removed from Windows Run; Exo yield companion may remain as Exo-* only.'
        active = [bool]$startupQuiet
    },
    [ordered]@{ title = 'Launcher yield while gaming'; detail = $yieldDetail; active = [bool]$yieldOk },
    [ordered]@{ title = 'Anti-cheat boundary'; detail = $boundaryDetail; active = [bool]$installed },
    [ordered]@{
        title = 'Exact Repair snapshot'
        detail = if ($snapshotReady) { 'Pre-Exo Run entries are saved so Repair restores startup exactly.' } else { 'Apply captures a pre-change snapshot before any registry edit.' }
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
    "Verified launcher policy: startup quiet + yield while gaming. Windows GPU/FSO left for a future Windows module. Anti-cheat untouched."
} elseif ($state -and $state.lastError) {
    [string]$state.lastError
} elseif ($missing.Count -gt 0) {
    'Run Apply to restore: ' + ($missing -join ', ') + '.'
} else {
    'Apply a reversible launcher policy (startup quiet, yield while gaming).'
}

[ordered]@{
    isApplied  = $applied
    statusText = $status
    detail     = $detail
    features   = $features
} | ConvertTo-Json -Compress -Depth 6
