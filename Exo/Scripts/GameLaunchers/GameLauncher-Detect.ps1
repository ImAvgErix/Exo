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

# Shared Game Bar / DSCP + gaming glue (optional; detect stays soft if missing)
$__common = Join-Path (Split-Path -Parent $PSScriptRoot) 'lib\Exo.Common.ps1'
if (-not (Test-Path -LiteralPath $__common)) { $__common = Join-Path $PSScriptRoot '..\lib\Exo.Common.ps1' }
if (-not (Test-Path -LiteralPath $__common) -and $env:LOCALAPPDATA) {
    $__common = Join-Path $env:LOCALAPPDATA 'Exo\app\Scripts\lib\Exo.Common.ps1'
}
if (Test-Path -LiteralPath $__common) {
    . $__common
    foreach ($__libPath in @(Import-ExoSharedLibFiles -From $PSScriptRoot)) { . $__libPath }
} else {
    foreach ($name in @('Exo.GameBar.ps1', 'Exo.GamingStack.ps1')) {
        foreach ($c in @(
            (Join-Path (Split-Path -Parent $PSScriptRoot) "lib\$name"),
            (Join-Path $PSScriptRoot "..\lib\$name"),
            (Join-Path $env:LOCALAPPDATA "Exo\scripts\lib\$name"),
            (Join-Path $env:LOCALAPPDATA "Exo\app\Scripts\lib\$name")
        )) {
            if ($c -and (Test-Path -LiteralPath $c)) { . $c; break }
        }
    }
}

$installed = if ($Module -eq 'Riot') {
    (Test-Path (Join-Path ${env:SystemDrive} 'Riot Games')) -or
    (Test-Path (Join-Path ${env:ProgramFiles} 'Riot Vanguard'))
} else {
    (Test-Path (Join-Path ${env:ProgramFiles} 'Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe')) -or
    (Test-Path (Join-Path ${env:ProgramFiles(x86)} 'Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe')) -or
    (Test-Path (Join-Path $env:ProgramData 'Epic\EpicGamesLauncher\Data\Manifests'))
}

# Returns list of @{ path; label }  -  same shape for Riot and Epic.
function Get-LiveGameEntries {
    $entries = [System.Collections.Generic.List[object]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

    if ($Module -eq 'Riot') {
        # Known relative paths only  -  full tree recurse under Riot Games is multi-second.
        $riotRoot = Join-Path ${env:SystemDrive} 'Riot Games'
        $known = @(
            @{ Rel = 'VALORANT\live\VALORANT\Binaries\Win64\VALORANT-Win64-Shipping.exe'; Label = 'VALORANT' },
            @{ Rel = 'VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe'; Label = 'VALORANT' },
            @{ Rel = 'VALORANT\VALORANT.exe'; Label = 'VALORANT' },
            @{ Rel = 'League of Legends\Game\League of Legends.exe'; Label = 'League of Legends' },
            @{ Rel = 'League of Legends\League of Legends.exe'; Label = 'League of Legends' }
        )
        if (Test-Path -LiteralPath $riotRoot -PathType Container) {
            foreach ($k in $known) {
                $full = Join-Path $riotRoot $k.Rel
                if ((Test-Path -LiteralPath $full -PathType Leaf) -and $seen.Add($full)) {
                    [void]$entries.Add([pscustomobject]@{ path = $full; label = [string]$k.Label })
                }
            }
            # Shallow fallback: one level of product folders only (no deep recurse).
            if ($entries.Count -eq 0) {
                $names = @{
                    'VALORANT-Win64-Shipping.exe' = 'VALORANT'
                    'VALORANT.exe' = 'VALORANT'
                    'League of Legends.exe' = 'League of Legends'
                }
                Get-ChildItem -LiteralPath $riotRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                    Get-ChildItem -LiteralPath $_.FullName -File -Recurse -Depth 4 -ErrorAction SilentlyContinue |
                        Where-Object { $names.ContainsKey($_.Name) } |
                        ForEach-Object {
                            if ($seen.Add($_.FullName)) {
                                [void]$entries.Add([pscustomobject]@{ path = $_.FullName; label = [string]$names[$_.Name] })
                            }
                        }
                }
            }
        }
        # Running process paths as last resort
        foreach ($pair in @(
            @{ N = 'VALORANT-Win64-Shipping'; L = 'VALORANT' },
            @{ N = 'VALORANT'; L = 'VALORANT' },
            @{ N = 'League of Legends'; L = 'League of Legends' }
        )) {
            Get-Process -Name $pair.N -ErrorAction SilentlyContinue | ForEach-Object {
                try {
                    $p = [string]$_.Path
                    if ($p -and $seen.Add($p)) {
                        [void]$entries.Add([pscustomobject]@{ path = $p; label = [string]$pair.L })
                    }
                } catch { }
            }
        }
        return @($entries)
    }

    # Epic: prefer DisplayName from manifests (same "Found: ..." language as Riot).
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
# Yield guard is retired (no background Run-key pwsh). "Ok" means purged / not installed.
$yieldRunPresent = $false
try {
    $rv = [string](Get-ItemPropertyValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $yieldRunName -ErrorAction Stop)
    $yieldRunPresent = $rv -match 'yield-guard|pwsh'
} catch { }
$yieldHelperPresent = Test-Path -LiteralPath $yieldHelper -PathType Leaf
$yieldSilent = $false
if ($yieldRunPresent) {
  try {
    $rv2 = [string](Get-ItemPropertyValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $yieldRunName -EA Stop)
    $yieldSilent = $rv2 -match '(?i)wscript|RunHidden'
  } catch {}
}
# Ok if purged OR installed as silent wscript companion (no console)
$yieldOk = ((-not $yieldRunPresent) -and (-not $yieldHelperPresent)) -or ($yieldSilent -and $yieldHelperPresent)

$snapshotReady = Test-Path -LiteralPath $snapshotPath -PathType Leaf
$markerOk = [bool]($state -and [bool]$state.applied -and [string]$state.applyStatus -eq 'applied')

# Live-ish: marker records last apply intent; startupQuiet is the live Run-key probe.
# Do not force shellQuiet true from marker alone (false applied).
$shellQuiet = [bool]($state -and $state.shellQuiet -eq $true -and $startupQuiet)

$gpuOk = $false
# Green when games do NOT have FSO-off (legacy exclusive path fights Games borderless).
$fsoClean = $true
$gpuHigh = 'GpuPreference=2;'
try {
    $gpuKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\DirectX\UserGpuPreferences')
    $fsoKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers')
    try {
        $gpuHits = 0
        $need = 0
        $fsoDirty = 0
        foreach ($p in @($targets)) {
            $p = [string]$p
            if ([string]::IsNullOrWhiteSpace($p)) { continue }
            $need++
            # Games must be high-perf (2) only  -  never count iGPU (1) as game OK.
            if ($gpuKey) {
                $v = [string]$gpuKey.GetValue($p, '')
                if ($v -eq $gpuHigh) { $gpuHits++ }
            }
            if ($fsoKey) {
                $fv = [string]$fsoKey.GetValue($p, '')
                if ($fv -match 'DISABLEDXMAXIMIZEDWINDOWEDMODE') { $fsoDirty++ }
            }
        }
        # Live registry only for isApplied honesty (markers are informational).
        $gpuOk = ($need -gt 0 -and $gpuHits -ge $need)
        $fsoClean = ($need -eq 0 -or $fsoDirty -eq 0)
    } finally {
        if ($gpuKey) { $gpuKey.Dispose() }
        if ($fsoKey) { $fsoKey.Dispose() }
    }
} catch {
    $gpuOk = $false
    $fsoClean = $false
}

# Per-game DSCP 46 (Game Bar quiet lives on the Windows card)
$dscpOk = $false
$dscpCount = 0
try {
    $hits = 0
    $need = 0
    foreach ($p in @($targets)) {
        $exe = [IO.Path]::GetFileName([string]$p)
        if ([string]::IsNullOrWhiteSpace($exe)) { continue }
        $need++
        $safe = ($exe -replace '[^\w\.\-]', '_')
        $pol = "Exo-$Module-DSCP-$safe"
        $path = Join-Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\QoS' $pol
        if (-not (Test-Path -LiteralPath $path)) { continue }
        try {
            $item = Get-Item -LiteralPath $path -ErrorAction Stop
            if ([string]$item.GetValue('DSCP Value') -eq '46' -and
                ([string]$item.GetValue('Application Name') -ieq $exe)) {
                $hits++
            }
        } catch { }
    }
    $dscpCount = $hits
    if ($need -eq 0) {
        $dscpOk = $true # no games -> not a blocker
    } else {
        $dscpOk = ($hits -ge $need)
    }
} catch {
    $dscpOk = $false
}

$applied = [bool]($installed -and $markerOk -and $startupQuiet -and $shellQuiet -and $yieldOk -and $snapshotReady -and $targetsPresent -gt 0 -and $gpuOk -and $fsoClean -and $dscpOk)

# Consistent install copy for Riot + Epic: "Installed . Found: A, B" or guidance.
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
    'Vanguard, Riot Client services, game files, and updates are never modified. Only safe launcher logs/cache junk is cleared.'
} else {
    'Epic Online Services, game installs, and updates are never modified. Only safe launcher logs/webcache junk is cleared.'
}

$cacheOk = [bool]($state -and $state.launcherCacheCleaned -eq $true -and $markerOk)
$menuQuietOk = $false
$exoCmd = Join-Path $root ("launchers\{0}-Exo.cmd" -f $Module)
if (-not (Test-Path -LiteralPath $exoCmd)) {
    $exoCmd = Join-Path $root ("launchers\{0}-Exo.cmd" -f $(if ($Module -eq 'Epic') { 'Epic' } else { 'Riot' }))
}
if (Test-Path -LiteralPath $exoCmd) {
    $menuQuietOk = $true
} elseif ($state -and $state.startMenuQuiet -eq $true -and $markerOk) {
    $menuQuietOk = $true
}

$yieldDetail = if ($yieldOk) {
    'Silent VBS companion: soft-reclaim launcher RAM + close UI when a game is running.'
} else {
    'Companion missing or still using a visible pwsh Run key  -  re-Apply.'
}

$discoveryDetail = if ($targetsPresent -gt 0) {
    $found = Format-FoundGames $labels $targetsPresent
    "$found Used for Windows high-perf GPU policy (display = Games hub)."
} else {
    'No game executables found yet  -  install a game, then Apply.'
}

# Skip Get-CimInstance on detect (WMI tax)  -  static copy is enough for the tile.
$gpuDetail = if ($gpuOk) {
    'Games are pinned to high-performance graphics for maximum frame rates.'
} else {
    'Apply pins games to high-performance graphics so Windows does not strand them on weak GPU paths.'
}

$features = [System.Collections.Generic.List[object]]::new()
[void]$features.Add([ordered]@{ title = "$Module ready"; detail = $installDetail; active = [bool]$installed })
[void]$features.Add([ordered]@{
    title = 'Games found'
    detail = $discoveryDetail
    active = [bool]($installed -and $targetsPresent -gt 0)
})
[void]$features.Add([ordered]@{
    title = 'Silent startup'
    detail = 'Launcher stays out of Windows Run. Only the Exo yield companion may remain.'
    active = [bool]$startupQuiet
})
[void]$features.Add([ordered]@{
    title = 'Silent Windows integration'
    detail = 'Toasts muted and non-anti-cheat scheduled tasks quieted - scoped to this launcher only.'
    active = [bool]($installed -and $shellQuiet)
})
[void]$features.Add([ordered]@{
    title = 'High-performance GPU'
    detail = $gpuDetail
    active = [bool]$gpuOk
})
[void]$features.Add([ordered]@{
    title = 'Display left to Games hub'
    detail = if ($fsoClean) {
        'No exclusive FSO-off on game EXEs - Games hub owns borderless.'
    } else {
        'Legacy FSO-off still on some game EXEs - re-Apply to clear (borderless-friendly).'
    }
    active = [bool]$fsoClean
})

# Host Game Mode / HAGS / Game Bar live on the Windows card only.

[void]$features.Add([ordered]@{
    title = 'Priority game traffic'
    detail = if ($dscpOk -and $dscpCount -gt 0) {
        "Network priority for $dscpCount game executable(s) on routers that honor DSCP."
    } else {
        'Apply marks game UDP traffic for priority on routers that honor DSCP.'
    }
    active = [bool]$dscpOk
})
[void]$features.Add([ordered]@{ title = 'Silent launcher companion'; detail = $yieldDetail; active = [bool]$yieldOk })
[void]$features.Add([ordered]@{
    title = 'Launcher junk cleaned'
    detail = if ($cacheOk) { 'Launcher logs/webcache/crash junk cleared  -  game installs untouched.' } else { 'Apply clears safe launcher logs and web cache only.' }
    active = [bool]$cacheOk
})
[void]$features.Add([ordered]@{
    title = 'Quiet Start Menu launch'
    detail = if ($menuQuietOk) { 'Start Menu opens the Exo high-priority quiet launcher.' } else { 'Apply retargets Start Menu to a quiet Exo launcher cmd when a shortcut exists.' }
    active = [bool]$menuQuietOk
})
[void]$features.Add([ordered]@{ title = 'Anti-cheat untouched'; detail = $boundaryDetail; active = [bool]$installed })
[void]$features.Add([ordered]@{
    title = 'One-click Repair ready'
    detail = if ($snapshotReady) { 'Pre-Exo settings are saved so Repair can restore exactly what was there before.' } else { 'Apply captures a safety snapshot before any change.' }
    active = [bool]$snapshotReady
})
[void]$features.Add([ordered]@{
    title = 'Optimization verified'
    detail = if ($markerOk) { "A completed $Module apply is on record for this PC." } else { 'No verified apply yet - run Apply to finish.' }
    active = [bool]$markerOk
})
$features = @($features)

# Status from the same rows the UI shows - exclude pure info tiles.
$infoTitles = @(
    'Optimization verified',
    'Anti-cheat untouched',
    'One-click Repair ready'
)
$missing = @()
foreach ($f in $features) {
    $t = [string]$f.title
    if ($infoTitles -contains $t) { continue }
    if (-not [bool]$f.active) { $missing += $t }
}

$allCheckableOn = ($missing.Count -eq 0) -and $installed -and ($targetsPresent -gt 0)
$appliedHonest = [bool]$applied -and $allCheckableOn

$status = if (-not $installed) {
    'Not installed'
} elseif ($appliedHonest) {
    'Already optimized'
} elseif ($missing.Count -eq 1) {
    "1 setting needs Apply ($($missing[0]))"
} elseif ($missing.Count -gt 1) {
    "$($missing.Count) settings need Apply"
} elseif ($state -and ($state.PSObject.Properties.Name -contains 'lastError') -and $state.lastError) {
    'Needs attention'
} else {
    'Ready to optimize'
}

$detail = if (-not $installed) {
    "Install $Module before applying."
} elseif ($appliedHonest) {
    "Verified: startup quiet, high-perf GPU, game DSCP; display owned by Games (borderless). Anti-cheat untouched."
} elseif ($state -and ($state.PSObject.Properties.Name -contains 'lastError') -and $state.lastError) {
    [string]$state.lastError
} elseif ($missing.Count -gt 0) {
    'Off: ' + ($missing -join ', ') + '.'
} else {
    'Apply a reversible policy: startup quiet, high-perf GPU, game DSCP; Games hub owns borderless.'
}

[ordered]@{
    isApplied  = $appliedHonest
    statusText = $status
    detail     = $detail
    features   = $features
} | ConvertTo-Json -Compress -Depth 6
