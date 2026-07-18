[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Riot','Epic')][string]$Module)
$ErrorActionPreference = 'SilentlyContinue'
$root = Join-Path $env:LOCALAPPDATA 'Exo'
$statePath = Join-Path $root ("{0}-optimizer.json" -f $Module.ToLowerInvariant())
$snapshotPath = Join-Path $root ("{0}-snapshot.json" -f $Module.ToLowerInvariant())
$state = $null
if (Test-Path -LiteralPath $statePath) {
    try { $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
}

$installed = if ($Module -eq 'Riot') {
    (Test-Path (Join-Path ${env:SystemDrive} 'Riot Games')) -or
    (Test-Path (Join-Path ${env:ProgramFiles} 'Riot Vanguard'))
} else {
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
$hybridGraphics = $false
try {
    $gpuNames = @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
        ForEach-Object { [string]$_.Name } |
        Where-Object { $_ -and $_ -notmatch '(?i)Microsoft Basic|Remote|Hyper-V|Virtual' })
    $hybridGraphics = $gpuNames.Count -ge 2 -and
        @($gpuNames | Where-Object { $_ -match '(?i)NVIDIA|GeForce|RTX|GTX|Radeon\s+RX|Intel.*Arc' }).Count -gt 0 -and
        @($gpuNames | Where-Object { $_ -match '(?i)Intel.*(?:UHD|Iris|HD Graphics)|AMD Radeon\(TM\) Graphics|Radeon Vega' }).Count -gt 0
} catch { }

$startupQuiet = $true
$run = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue
if ($run) {
    foreach ($property in $run.PSObject.Properties) {
        if ($property.Name -like 'PS*') { continue }
        if ("$($property.Name) $($property.Value)" -match "(?i)$Module") { $startupQuiet = $false; break }
    }
}

$policyVerified = 0
$gpu = Get-ItemProperty 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences' -ErrorAction SilentlyContinue
foreach ($path in $targets) {
    $gpuOk = $gpu -and [string]$gpu.PSObject.Properties[[string]$path].Value -eq 'GpuPreference=2;'
    if ($gpuOk) { $policyVerified++ }
}
$policyOk = [bool]($targetsPresent -gt 0 -and $policyVerified -eq $targetsPresent)
$snapshotReady = Test-Path -LiteralPath $snapshotPath -PathType Leaf
$markerOk = [bool]($state -and [bool]$state.applied -and [string]$state.applyStatus -eq 'applied')
$applied = [bool]($installed -and $markerOk -and $startupQuiet -and $policyOk -and $snapshotReady)

$installDetail = if (-not $installed) {
    "Install $Module, open it once, then return."
} elseif ($targetsPresent -eq 0) {
    "Client found; install at least one game so Exo can pin GPU preference."
} elseif ($labels.Count -gt 0) {
    "Found: " + ($labels -join ', ') + "."
} else {
    "$targetsPresent game executable(s) discovered."
}

$gpuDetail = if ($targetsPresent -eq 0) {
    'No game executables detected yet.'
} elseif ($policyOk) {
    "All $targetsPresent detected game executable(s) use the high-performance GPU."
} else {
    "$policyVerified of $targetsPresent game executable(s) use the high-performance GPU."
}

$hybridDetail = if ($hybridGraphics) {
    'Games stay on the discrete GPU; launcher UI prefers integrated graphics so it does not wake dGPU idle power.'
} else {
    'Single-GPU PC: games use the only adapter; no launcher override is needed.'
}

$boundaryDetail = if ($Module -eq 'Riot') {
    'Vanguard, Riot Client services, game files, and updates are never modified.'
} else {
    'Epic Online Services, launcher files, caches, and updates are never modified.'
}

# Even tile count (8) keeps the two-column feature grid balanced like Discord.
$features = @(
    [ordered]@{
        title = "$Module install"
        detail = $installDetail
        active = [bool]$installed
    },
    [ordered]@{
        title = 'Game discovery'
        detail = if ($targetsPresent -gt 0) {
            "$targetsPresent executable(s) ready for GPU policy."
        } else {
            'No game executables found yet - install a game, then Apply.'
        }
        active = [bool]($installed -and $targetsPresent -gt 0)
    },
    [ordered]@{
        title = 'Startup quiet'
        detail = 'Launcher is removed from Windows Run so it does not autostart with the session.'
        active = [bool]$startupQuiet
    },
    [ordered]@{
        title = 'High-performance GPU'
        detail = $gpuDetail
        active = [bool]$policyOk
    },
    [ordered]@{
        title = 'Hybrid GPU split'
        detail = $hybridDetail
        active = $true
    },
    [ordered]@{
        title = 'Anti-cheat boundary'
        detail = $boundaryDetail
        active = [bool]$installed
    },
    [ordered]@{
        title = 'Exact Repair snapshot'
        detail = if ($snapshotReady) {
            'Pre-Exo registry values are saved so Repair can restore startup and GPU prefs exactly.'
        } else {
            'Apply captures a pre-change snapshot before any registry edit.'
        }
        active = [bool]$snapshotReady
    },
    [ordered]@{
        title = 'Verified optimizer record'
        detail = if ($markerOk) {
            "A completed full apply is recorded for this $Module installation."
        } else {
            'No verified apply record yet for this PC.'
        }
        active = [bool]$markerOk
    }
)

$missing = @()
if (-not $installed) { $missing += 'install' }
elseif ($targetsPresent -eq 0) { $missing += 'game discovery' }
else {
    if (-not $startupQuiet) { $missing += 'startup quiet' }
    if (-not $policyOk) { $missing += 'GPU preference' }
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
    "Verified Windows policy only on $targetsPresent game executable(s). Client files and anti-cheat stay untouched."
} elseif ($state -and $state.lastError) {
    [string]$state.lastError
} elseif ($missing.Count -gt 0) {
    'Run Apply to restore: ' + ($missing -join ', ') + '.'
} else {
    'Apply a reversible per-game Windows policy (startup quiet + GPU routing).'
}

[ordered]@{
    isApplied  = $applied
    statusText = $status
    detail     = $detail
    features   = $features
} | ConvertTo-Json -Compress -Depth 6
