#Requires -Version 5.1
<# Live verify Exo "Applied" claims against real registry/files/processes. #>
$ErrorActionPreference = 'Continue'
$exo = Join-Path $env:LOCALAPPDATA 'Exo'
$fail = 0
$warn = 0

function Ok($m) { Write-Host "  [OK]  $m" -ForegroundColor Green }
function Bad($m) { Write-Host "  [FAIL] $m" -ForegroundColor Red; $script:fail++ }
function Warn($m) { Write-Host "  [WARN] $m" -ForegroundColor Yellow; $script:warn++ }
function Sec($t) { Write-Host "`n==== $t ====" -ForegroundColor Cyan }

function Test-RegValue($path, $name, $expect) {
  try {
    $v = (Get-ItemProperty -Path $path -Name $name -ErrorAction Stop).$name
    return [string]$v -eq [string]$expect
  } catch { return $false }
}

function Get-RegValue($path, $name) {
  # Same guarded shape as Get-NvoRegValue / Get-ExoRegStringOrNull in the shipped kits. The
  # readings below used (Get-ItemProperty ... -EA SilentlyContinue).$name, which yields nothing
  # when the value is absent and then throws on the dereference under StrictMode. This script
  # does not set StrictMode itself, but StrictMode is inherited straight through `&`, so the
  # shape is wrong the moment anything calls it from a strict session.
  try {
    $item = Get-ItemProperty -LiteralPath $path -ErrorAction SilentlyContinue
    if (-not $item) { return $null }
    if ($item.PSObject.Properties.Name -notcontains $name) { return $null }
    return $item.$name
  } catch { return $null }
}

# -- WINDOWS --------------------------------------------------------------
Sec "WINDOWS"
$wp = powercfg /getactivescheme 2>&1 | Out-String
# Hub ships a fixed plan GUID (ExoPowerPlan.ExoSchemeGuid) and names it "Exo - …".
# Older kits left "Exo Extreme" / LiteOS GUIDs active. Live-verify accepts any Exo-family
# active scheme; Detect itself still requires the current GUID + settings for Applied.
$exoFamilyGuid = $wp -match '7ae4b8a5-2c19-4d6f-9f3e-1b0c5d8e4a72|a1111111-e80e-4e0e-a111-0e0e0e0e0e01|77777777-7777-7777-7777-777777777777'
$exoFamilyName = $wp -match 'Exo\s*(-|Extreme|Competitive|LiteOS)'
if ($exoFamilyGuid -or $exoFamilyName) {
  Ok "Active power plan is Exo family: $($wp.Trim())"
} else {
  Bad "Power plan is not an Exo-family scheme: $wp"
}

# Game Mode
$gm1 = Test-RegValue 'HKCU:\Software\Microsoft\GameBar' 'AllowAutoGameMode' 1
$gm2 = Test-RegValue 'HKCU:\Software\Microsoft\GameBar' 'AutoGameModeEnabled' 1
if ($gm1 -or $gm2) {
  Ok "Game Mode-related keys present"
} else {
  $gm = Get-ItemProperty 'HKCU:\Software\Microsoft\GameBar' -ErrorAction SilentlyContinue
  if ($gm) {
    $bits = ($gm.PSObject.Properties | Where-Object { $_.Name -notmatch '^PS' } | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', '
    Warn "GameBar keys exist but AutoGameMode not clearly 1: $bits"
  } else { Bad "GameBar keys missing" }
}

# HAGS
try {
  $hags = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' -Name HwSchMode -EA Stop).HwSchMode
  if ($hags -eq 2) { Ok "HAGS HwSchMode=2" } else { Bad "HAGS HwSchMode=$hags (want 2)" }
} catch { Bad "HAGS HwSchMode unreadable (need admin?)" }

# Win32PrioritySeparation is REFUSE folklore (docs/SYSTEM-EVIDENCE.md) - never required.
# Game Mode on is a real System lever.
$gm = Get-RegValue 'HKCU:\Software\Microsoft\GameBar' 'AutoGameModeEnabled'
if ($gm -eq 1) { Ok "Game Mode on" } else { Warn "Game Mode AutoGameModeEnabled=$gm (want 1)" }
$nexus = Get-RegValue 'HKCU:\Software\Microsoft\GameBar' 'UseNexusForGameBarEnabled'
if ($nexus -eq 0) { Ok "Game Bar nexus off" } else { Warn "UseNexusForGameBarEnabled=$nexus (want 0)" }

# Game DVR quiet
$gamedvr = Get-RegValue 'HKCU:\System\GameConfigStore' 'GameDVR_Enabled'
if ($gamedvr -eq 0) { Ok "GameDVR_Enabled=0" } else { Warn "GameDVR_Enabled=$gamedvr" }

# -- STEAM ----------------------------------------------------------------
Sec "STEAM"
$steam = $null
foreach ($c in @(
  (Get-RegValue 'HKCU:\Software\Valve\Steam' 'SteamPath'),
  "${env:ProgramFiles(x86)}\Steam",
  "$env:ProgramFiles\Steam"
)) {
  if ($c -and (Test-Path (Join-Path $c 'steam.exe'))) { $steam = (Resolve-Path $c).Path; break }
}
if (-not $steam) { Bad "Steam not found" }
else {
  Ok "Steam at $steam"
  $cmd = Join-Path $steam 'Steam-Exo.cmd'
  if (Test-Path $cmd) {
    Ok "Steam-Exo.cmd present"
    $t = Get-Content $cmd -Raw
    if ($t -match 'nofriendsui|nointro|/HIGH') { Ok "CEF launcher flags look present" } else { Warn "Steam-Exo.cmd missing expected flags" }
  } else { Bad "Steam-Exo.cmd missing" }

  $guard = @(
    (Join-Path $steam 'Exo-SteamMemoryGuard.ps1'),
    (Join-Path $exo 'Exo-SteamMemoryGuard.ps1')
  ) | Where-Object { Test-Path $_ } | Select-Object -First 1
  if ($guard) { Ok "Memory guard script: $guard" } else { Bad "Memory guard script missing" }

  # Sample DSCP policies
  $polRoot = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\QoS'
  if (Test-Path $polRoot) {
    $n = (Get-ChildItem $polRoot -EA SilentlyContinue | Where-Object { $_.PSChildName -like 'Exo-Steam*' }).Count
    if ($n -gt 0) { Ok "Steam DSCP policies: $n under QoS" } else { Warn "No Exo-Steam* DSCP policies (elev may have been skipped)" }
  } else { Warn "QoS policy root missing" }
}

# -- RIOT (optional launcher kit - not a Hub module row) -----------------
Sec "RIOT"
$riotCmd = Join-Path $exo 'launchers\Riot-Exo.cmd'
$riotGuard = Join-Path $exo 'riot-yield-guard.ps1'
$riotRun = Get-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' 'Exo-Riot-Yield'
if (Test-Path $riotCmd) { Ok "Riot-Exo.cmd present" } else { Warn "Riot-Exo.cmd missing (optional - not a Hub module)" }
if (Test-Path $riotGuard) { Ok "riot-yield-guard.ps1 present" } else { Warn "riot-yield-guard.ps1 missing (optional)" }
if ($riotRun -and $riotRun -match 'yield-guard' -and $riotRun -match 'Hidden') {
  Ok "Run\Exo-Riot-Yield looks good"
} elseif ($riotRun) {
  Warn "Run\Exo-Riot-Yield unexpected value: $riotRun"
} else {
  Warn "Run\Exo-Riot-Yield not armed (optional launcher path)"
}

# GPU preference sample for VALORANT if present
$val = Get-ChildItem 'C:\Riot Games' -Recurse -Filter 'VALORANT-Win64-Shipping.exe' -EA SilentlyContinue | Select-Object -First 1
if ($val) {
  $leaf = $val.Name
  $gpuKey = 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences'
  $gp = (Get-ItemProperty $gpuKey -Name $val.FullName -EA SilentlyContinue).($val.FullName)
  if (-not $gp) {
    # sometimes stored with different path casing
    $props = Get-ItemProperty $gpuKey -EA SilentlyContinue
    $match = $props.PSObject.Properties | Where-Object { $_.Name -like '*VALORANT*Shipping*' } | Select-Object -First 1
    $gp = $match.Value
  }
  if ($gp -match 'GpuPreference=2') { Ok "VALORANT GpuPreference=2 (high perf)" }
  elseif ($gp) { Warn "VALORANT GPU pref: $gp" }
  else { Warn "VALORANT GPU preference not found (path may differ)" }
} else { Warn "VALORANT shipping exe not found to sample GPU pref" }

$nRiot = (Get-ChildItem 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\QoS' -EA SilentlyContinue | Where-Object { $_.PSChildName -like 'Exo-Riot*' }).Count
if ($nRiot -gt 0) { Ok "Riot DSCP policies: $nRiot" } else { Warn "No Exo-Riot* DSCP policies" }

# -- EPIC (optional launcher kit - not a Hub module row) -----------------
Sec "EPIC"
$epicCmd = Join-Path $exo 'launchers\Epic-Exo.cmd'
$epicGuard = Join-Path $exo 'epic-yield-guard.ps1'
$epicRun = Get-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' 'Exo-Epic-Yield'
if (Test-Path $epicCmd) { Ok "Epic-Exo.cmd present" } else { Warn "Epic-Exo.cmd missing (optional - not a Hub module)" }
if (Test-Path $epicGuard) { Ok "epic-yield-guard.ps1 present" } else { Warn "epic-yield-guard.ps1 missing (optional)" }
if ($epicRun -and $epicRun -match 'yield-guard' -and $epicRun -match 'Hidden') {
  Ok "Run\Exo-Epic-Yield looks good"
} else { Warn "Run\Exo-Epic-Yield not armed (optional launcher path): $epicRun" }

$nEpic = (Get-ChildItem 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\QoS' -EA SilentlyContinue | Where-Object { $_.PSChildName -like 'Exo-Epic*' }).Count
if ($nEpic -gt 0) { Ok "Epic DSCP policies: $nEpic" } else { Warn "No Exo-Epic* DSCP policies" }

# -- DISCORD --------------------------------------------------------------
Sec "DISCORD"
$discordApp = Get-ChildItem "$env:LOCALAPPDATA\Discord" -Directory -Filter 'app-*' -EA SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
if ($discordApp) { Ok "Discord app: $($discordApp.FullName)" } else { Bad "Discord app-* not found" }

$equi = Join-Path $env:APPDATA 'Equicord\equicord.asar'
if (Test-Path $equi) { Ok "Equicord asar present" } else { Warn "Equicord asar missing" }

$settings = Join-Path $env:APPDATA 'discord\settings.json'
if (Test-Path $settings) {
  $s = Get-Content $settings -Raw
  if ($s -match 'DANGEROUS|OPENASAR|openasar|hardwareAcceleration') { Ok "discord settings.json has host flags content" }
  else { Warn "settings.json present but flags unclear" }
} else { Warn "discord settings.json missing" }

# DiscOpt kernel often version.dll next to Discord.exe
if ($discordApp) {
  $ver = Join-Path $discordApp.FullName 'version.dll'
  if (Test-Path $ver) { Ok "DiscOpt version.dll present" } else { Warn "version.dll missing in app dir (kernel?)" }
}

$nDisc = (Get-ChildItem 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\QoS' -EA SilentlyContinue | Where-Object { $_.PSChildName -like '*Discord*' -or $_.PSChildName -like 'Exo*Discord*' }).Count
if ($nDisc -gt 0) { Ok "Discord-related QoS policies: $nDisc" } else { Warn "No Discord QoS policies found" }

# -- NVIDIA ---------------------------------------------------------------
Sec "NVIDIA"
$nv = Get-Content (Join-Path $exo 'nvidia-optimizer.json') -Raw -EA SilentlyContinue | ConvertFrom-Json
if ($nv.drsVerified) { Ok "State says DRS verified at $($nv.drsVerifiedAt) profile=$($nv.profileFile) gsync=$($nv.gsync)" }
else { Bad "nvidia-optimizer.json not DRS verified" }
if ($nv.driverTweaksVerified) { Ok "Driver tweaks verified (driver $($nv.driverTweaksVersion))" }
else { Warn "Driver tweaks not verified in state" }
$npi = Get-ChildItem (Join-Path $exo 'tools\nvidiaProfileInspector') -Recurse -Filter 'nvidiaProfileInspector.exe' -EA SilentlyContinue | Select-Object -First 1
if ($npi) { Ok "NPI present: $($npi.FullName)" } else { Warn "NPI folder missing under Exo\tools" }
$drs = Join-Path $exo 'nvidia-drs-pre-exo.bin'
if (Test-Path $drs) { Ok "DRS pre-backup exists ($((Get-Item $drs).Length) bytes)" } else { Warn "No DRS pre-backup file" }

# -- INTERNET -------------------------------------------------------------
Sec "INTERNET"
$net = Get-Content (Join-Path $exo 'network-optimizer.json') -Raw -EA SilentlyContinue | ConvertFrom-Json
if ($net.qualityBenchmark.ok) {
  Ok "Quality sample ok: idle $($net.qualityBenchmark.pingP50Ms) ms . down $($net.qualityBenchmark.downloadMbps) Mbps . DNS $($net.qualityBenchmark.dnsProvider)"
} else { Warn "No quality benchmark ok" }
$snap = Join-Path $exo 'network-snapshot.json'
if (Test-Path $snap) { Ok "network-snapshot.json present (rollback base)" } else { Bad "network-snapshot.json missing" }
# TCP chimney-ish: TcpAckFrequency / TcpNoDelay are per-interface; check global TcpTimedWaitDelay or similar
try {
  $tcp = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters' -EA Stop
  Ok "Tcpip Parameters readable (GlobalMaxTcpWindowSize=$($tcp.GlobalMaxTcpWindowSize))"
} catch { Warn "Tcpip Parameters unreadable" }

# -- GAMES / MARVEL RIVALS ------------------------------------------------
Sec "GAMES (Marvel Rivals)"
$rivals = 'C:\Program Files (x86)\Steam\steamapps\common\MarvelRivals'
$eng = Join-Path $env:LOCALAPPDATA 'Marvel\Saved\Config\Windows\Engine.ini'
$mods = Join-Path $rivals 'MarvelGame\Marvel\Content\Paks\~mods'
$bin = Join-Path $rivals 'MarvelGame\Marvel\Binaries\Win64'
if (Test-Path $eng) {
  $t = Get-Content $eng -Raw
  if ($t -match 'Exo Games') { Ok "Engine.ini has Exo Games marker" } else { Bad "Engine.ini missing Exo Games marker" }
  if ($t -match 'profile=potato') { Ok "profile=potato" }
  elseif ($t -match 'profile=optimized') { Ok "profile=optimized" }
  else { Bad "No profile= potato/optimized in Engine.ini" }
  if ($t -match 'r\.MipMapLODBias=5') { Ok "MipMapLODBias=5 (potato textures)" }
  elseif ($t -match 'r\.MipMapLODBias=0') { Ok "MipMapLODBias=0 (optimized textures)" }
} else { Bad "Engine.ini missing" }

if (Test-Path (Join-Path $bin 'dsound.dll')) { Ok "dsound.dll bypass present" } else { Bad "dsound.dll missing" }
if (Test-Path (Join-Path $bin 'plugins\MarvelRivalsUTOCSignatureBypass.asi')) { Ok "ASI bypass present" } else { Bad "ASI bypass missing" }

if (Test-Path $mods) {
  $paks = Get-ChildItem $mods -File -EA SilentlyContinue
  $exoP = ($paks | Where-Object { $_.Name -like 'Exo*' }).Count
  Ok "~mods: $($paks.Count) files ($exoP Exo*)"
  foreach ($fam in @('ExoFPSBoost','ExoNoShake','ExoPerformance')) {
    $hit = @($paks | Where-Object { $_.Name -like ($fam + '*') })
    if ($hit.Count -gt 0) { Ok "Pack family $fam present ($($hit.Count) files)" }
    else { Warn "Pack family $fam missing" }
  }
} else { Bad "~mods missing" }

# -- SUMMARY --------------------------------------------------------------
Sec "SUMMARY"
Write-Host "FAIL=$fail  WARN=$warn"
if ($fail -eq 0) {
  Write-Host "Live checks: no hard FAILs. Review WARNs for partial elev/optional rows." -ForegroundColor Green
  exit 0
} else {
  Write-Host "Live checks: $fail hard FAIL(s) - UI 'Applied' overstates those." -ForegroundColor Red
  exit 1
}
