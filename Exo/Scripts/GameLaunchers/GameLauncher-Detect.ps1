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
$targets = @(Get-LiveTargets)
$targetsPresent = $targets.Count
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
$applied = [bool]($state -and [bool]$state.applied -and $startupQuiet -and $policyOk -and $snapshotReady)
$features = @(
    [ordered]@{ title = "$Module detected"; detail = $(if ($installed) { 'Installed client found' } else { 'Client not found' }); active = [bool]$installed },
    [ordered]@{ title = 'Startup quiet'; detail = 'Launcher no longer starts with Windows'; active = [bool]$startupQuiet },
    [ordered]@{ title = 'Per-game GPU preference'; detail = "$policyVerified of $targetsPresent detected executable(s) use the high-performance GPU"; active = [bool]$policyOk },
    [ordered]@{ title = 'Hybrid GPU split'; detail = $(if ($hybridGraphics) { 'Games use the discrete GPU; launcher UI uses integrated graphics' } else { 'Single-GPU path; no unnecessary launcher override' }); active = $true },
    [ordered]@{ title = 'Anti-cheat and updates'; detail = 'Services, anti-cheat, client files, and update paths are outside Exo policy'; active = [bool]$installed },
    [ordered]@{ title = 'Exact Repair'; detail = 'Pre-Exo registry values are saved for restore'; active = [bool]$snapshotReady }
)
$status = if (-not $installed) { 'Not installed' } elseif ($applied) { 'All applied' } elseif ($state -and $state.lastError) { 'Needs attention' } else { 'Not applied' }
$detail = if (-not $installed) { "Install $Module before applying." } elseif ($applied) { "Verified on $targetsPresent game executable(s)." } elseif ($state -and $state.lastError) { [string]$state.lastError } else { 'Apply a reversible per-game Windows policy.' }
[ordered]@{ isApplied = $applied; statusText = $status; detail = $detail; features = $features } | ConvertTo-Json -Compress -Depth 6
