using System.Text;
using Exo.Models;

namespace Exo.Services;

/// <summary>
/// Generates the elevated PowerShell apply script (shipped path).
/// Pure string build — no elevation. Driven by <see cref="NetworkPeakLogic"/> knobs.
/// </summary>
public static class NetworkApplyScriptBuilder
{
    public static string Build(
        NetworkPreset preset,
        NetworkApplyOptions options,
        NetworkMediaProfile media)
    {
        var knobs = NetworkPeakLogic.KnobsFor(preset);
        var latency = knobs.NagleOff;
        var autotune = knobs.AutotuneNetsh;
        var autoTuningPs = knobs.AutotunePs;
        var rsc = knobs.Rsc;
        var lso = knobs.Lso;
        var im = knobs.InterruptMod;
        var flow = knobs.FlowControl;
        var idleRestrict = knobs.IdleRestrict;
        var restartEth = options.RestartEthernet ? "1" : "0";
        var preferEth = options.PreferEthernetDisableWifi ? "1" : "0";
        // Hint only — apply script re-probes live for band capability
        var prefer6Hint = media.ClientSupports6Ghz ? "1" : "0";
        // Nagle keys only for latency (TCP games); throughput clears them
        var ackBlock = latency
            ? """
  Set-Dword $p 'TcpAckFrequency' 1
  Set-Dword $p 'TCPNoDelay' 1
  Set-Dword $p 'TcpDelAckTicks' 0
"""
            : """
  Remove-Prop $p 'TcpAckFrequency'
  Remove-Prop $p 'TCPNoDelay'
  Remove-Prop $p 'TcpDelAckTicks'
""";

        var sb = new StringBuilder(18_000);
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("$ProgressPreference = 'SilentlyContinue'");
        sb.AppendLine("$log = Join-Path $env:TEMP 'exo-net-last.log'");
        sb.AppendLine("function Log([string]$m) { $ts = Get-Date -Format o; Add-Content -Path $log -Value \"$ts $m\" -EA SilentlyContinue; Write-Host $m }");
        sb.AppendLine("'' | Set-Content -Path $log -EA SilentlyContinue");
        sb.AppendLine("Log '[Exo-NET] Preset=" + preset + " ethFirst=" + preferEth + " restartEth=" + restartEth + " band6hint=" + prefer6Hint + "'");
        sb.AppendLine("""
function Set-Dword([string]$Path, [string]$Name, [int]$Value) {
  if (-not (Test-Path -LiteralPath $Path)) { New-Item -Path $Path -Force | Out-Null }
  # Force clean write (overwrites ffffffff / wrong types)
  Remove-ItemProperty -LiteralPath $Path -Name $Name -Force -EA SilentlyContinue
  New-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -PropertyType DWord -Force -EA SilentlyContinue | Out-Null
  Set-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -Type DWord -Force -EA SilentlyContinue
}
function Remove-Prop([string]$Path, [string]$Name) {
  if (Test-Path -LiteralPath $Path) { Remove-ItemProperty -LiteralPath $Path -Name $Name -Force -EA SilentlyContinue }
}
function Set-Adv($Name, $Kw, $Val) {
  try { Set-NetAdapterAdvancedProperty -Name $Name -RegistryKeyword $Kw -RegistryValue $Val -NoRestart -EA SilentlyContinue } catch {}
}
function Set-AdvDisplay($adapterName, $displayName, $displayValue) {
  try { Set-NetAdapterAdvancedProperty -Name $adapterName -DisplayName $displayName -DisplayValue $displayValue -NoRestart -EA SilentlyContinue; return $true } catch { return $false }
}
""");

        // --- Registry: only keys that still matter ---
        // DisableTaskOffload=1 is a real footgun (kills checksum/LSO at stack level)
        sb.AppendLine("$tcp = 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters'");
        sb.AppendLine("Set-Dword $tcp 'DisableTaskOffload' 0");
        sb.AppendLine("Set-Dword $tcp 'EnablePMTUDiscovery' 1");
        // Clear obsolete static RWIN if present (auto-tuning owns window size)
        sb.AppendLine("Remove-Prop $tcp 'GlobalMaxTcpWindowSize'");
        sb.AppendLine("Remove-Prop $tcp 'TcpWindowSize'");
        sb.AppendLine("Remove-Prop $tcp 'EnableTCPChimney'");
        sb.AppendLine("Remove-Prop $tcp 'EnableTCPA'");
        sb.AppendLine("Remove-Prop $tcp 'EnableDCA'");
        sb.AppendLine("Remove-Prop $tcp 'TcpNumConnections'");
        sb.AppendLine("Remove-Prop $tcp 'LargeSystemCache'");

        // MMCSS — Microsoft docs:
        // SystemResponsiveness: % for low-priority; default 20; <10 or >100 clamp to 20 → use 10
        // NetworkThrottlingIndex: default 10. ffffffff can raise DPC latency / audio issues → force 10
        sb.AppendLine("$mm = 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile'");
        sb.AppendLine("Set-Dword $mm 'SystemResponsiveness' 10");
        sb.AppendLine("Set-Dword $mm 'NetworkThrottlingIndex' 10");
        sb.AppendLine("Log '[MMCSS] SystemResponsiveness=10 NetworkThrottlingIndex=10 (forced)'");
        sb.AppendLine("""
$mmGames = Join-Path $mm 'Tasks\Games'
if (-not (Test-Path $mmGames)) { New-Item $mmGames -Force | Out-Null }
Set-Dword $mmGames 'GPU Priority' 8
Set-Dword $mmGames 'Priority' 6
try { Set-ItemProperty $mmGames -Name 'Scheduling Category' -Value 'High' -Force -EA SilentlyContinue } catch {}
try { Set-ItemProperty $mmGames -Name 'SFIO Priority' -Value 'High' -Force -EA SilentlyContinue } catch {}
# QoS "Limit reservable bandwidth" — GPO NonBestEffortLimit 0 removes the old 20% reserve
New-Item 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Psched' -Force | Out-Null
Set-Dword 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Psched' 'NonBestEffortLimit' 0
# Power plan: wireless max perf, PCIe ASPM off, USB selective suspend off (AC)
# GUIDs are Windows built-ins (powercfg /q)
try {
  $scheme = (powercfg /getactivescheme) -replace '.*GUID:\s*([0-9a-f\-]+).*','$1'
  if ($scheme) {
    # Wireless Adapter Settings → Power Saving Mode = Maximum Performance (0)
    powercfg /setacvalueindex $scheme 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0 | Out-Null
    powercfg /setdcvalueindex $scheme 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0 | Out-Null
    # PCI Express → Link State Power Management = Off (0)
    powercfg /setacvalueindex $scheme 501a4d13-42af-4429-9fd1-a8218c268e20 ee12f906-d277-404b-b6da-e5fa1a576df5 0 | Out-Null
    powercfg /setdcvalueindex $scheme 501a4d13-42af-4429-9fd1-a8218c268e20 ee12f906-d277-404b-b6da-e5fa1a576df5 0 | Out-Null
    # USB selective suspend = Disabled (0) on AC
    powercfg /setacvalueindex $scheme 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0 | Out-Null
    powercfg /setactive $scheme | Out-Null
    Log '[powercfg] wireless=max, PCIe ASPM=off, USB sel-suspend AC=off'
  }
} catch { Log '[powercfg] skipped' }
""");

        // --- netsh / Set-NetTCPSetting (supported modern path) ---
        sb.AppendLine("netsh int tcp set global rss=enabled | Out-Null");
        sb.AppendLine("netsh int tcp set global autotuninglevel=" + autotune + " | Out-Null");
        sb.AppendLine("netsh int tcp set global rsc=" + rsc + " | Out-Null");
        sb.AppendLine("try { netsh int tcp set heuristics disabled | Out-Null } catch {}");
        sb.AppendLine("try { netsh int tcp set supplemental template=internet congestionprovider=cubic | Out-Null } catch {}");
        sb.AppendLine("try { netsh int tcp set supplemental template=internetcustom congestionprovider=cubic | Out-Null } catch {}");
        sb.AppendLine("try { netsh int ip set global taskoffload=enabled | Out-Null } catch {}");
        // Ephemeral ports — modern API (MaxUserPort is legacy)
        sb.AppendLine("try { netsh int ipv4 set dynamicport tcp start=1025 num=64511 | Out-Null } catch {}");
        sb.AppendLine("try { netsh int ipv4 set dynamicport udp start=1025 num=64511 | Out-Null } catch {}");
        sb.AppendLine("try { netsh int ipv6 set dynamicport tcp start=1025 num=64511 | Out-Null } catch {}");
        sb.AppendLine("try { netsh int ipv6 set dynamicport udp start=1025 num=64511 | Out-Null } catch {}");
        sb.AppendLine("foreach ($pr in @('Internet','InternetCustom')) {");
        sb.AppendLine("  try { Set-NetTCPSetting -SettingName $pr -CongestionProvider CUBIC -EA SilentlyContinue } catch {}");
        sb.AppendLine("  try { Set-NetTCPSetting -SettingName $pr -AutoTuningLevelLocal " + autoTuningPs + " -EA SilentlyContinue } catch {}");
        sb.AppendLine("  try { Set-NetTCPSetting -SettingName $pr -ScalingHeuristics Disabled -EA SilentlyContinue } catch {}");
        sb.AppendLine("}");

        // Nagle (per-interface) — only latency preset
        sb.AppendLine("Get-ChildItem 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces' -EA SilentlyContinue | ForEach-Object {");
        sb.AppendLine("  $p = $_.PSPath");
        sb.AppendLine(ackBlock);
        sb.AppendLine("}");

        // --- Per-adapter: branch Ethernet vs Wi‑Fi (MS: wireless often has no RSS/LSO) ---
        // Apply to all physical NICs so dual-homed PCs are ready on either media.
        // Classification mirrors NetworkPeakLogic.IsWifiAdapter (keep in sync)
        sb.AppendLine("function Test-IsWifiAdapter($a) {");
        sb.AppendLine("  $pm = [string]$a.PhysicalMediaType; $m = [string]$a.MediaType");
        sb.AppendLine("  $desc = [string]$a.InterfaceDescription; $name = [string]$a.Name");
        sb.AppendLine("  if ($pm -match '(?i)Native 802\\.11|802\\.11|Wireless') { return $true }");
        sb.AppendLine("  if ($pm -match '(?i)^802\\.3$') { return $false }");
        sb.AppendLine("  if ($m -match '(?i)Native 802|802\\.11|Wireless|Wi-?Fi') { return $true }");
        sb.AppendLine("  if ($desc -match '(?i)Bluetooth|Virtual|Hyper-V|vEthernet|TAP-|TUN-|WireGuard|OpenVPN|Wintun|Meta\\s*Tunnel') { return $false }");
        sb.AppendLine("  if ($desc -match '(?i)Wi-?Fi|Wireless|802\\.11|WLAN|MediaTek.*Wi|Intel.*Wi-?Fi|Realtek.*802\\.11|Killer.*Wireless|Qualcomm.*Wi|Broadcom.*802|AX\\d{3,4}|BE\\d{3,4}|Wi-Fi\\s*\\d') { return $true }");
        sb.AppendLine("  if ($name -match '(?i)^Wi-?Fi|Wireless|WLAN') { return $true }");
        sb.AppendLine("  return $false");
        sb.AppendLine("}");
        // Fuzzy pick among ValidDisplayValues — Intel / Realtek / MediaTek / Qualcomm / Killer strings vary
        // Prefer-* beats Only-* (never force band-only). Score picks best available option.
        sb.AppendLine("function Select-BandDisplayValue([object[]]$vals, [bool]$want6) {");
        sb.AppendLine("  if (-not $vals -or $vals.Count -eq 0) { return $null }");
        sb.AppendLine("  $list = @($vals | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })");
        sb.AppendLine("  $scored = foreach ($v in $list) {");
        sb.AppendLine("    $s = 0");
        sb.AppendLine("    $isOnly = ($v -match '(?i)\\bonly\\b|\\bexclusive\\b')");
        sb.AppendLine("    $isPref = ($v -match '(?i)prefer|preferred|preferable|priority|favou?r')");
        sb.AppendLine("    # 2.4 — never choose for gaming when higher exists");
        sb.AppendLine("    if ($v -match '(?i)2\\.4|2,4|2400|2GHz|2\\s*GHz') {");
        sb.AppendLine("      $s = if ($isOnly) { -200 } elseif ($isPref) { -100 } else { -50 }");
        sb.AppendLine("    }");
        sb.AppendLine("    elseif ($v -match '(?i)no\\s*pref|no\\s*preference|auto|default|disabled|not\\s*set|any\\s*band|best\\s*performance|\\b802\\.11\\s*auto\\b') { $s = 1 }");
        sb.AppendLine("    # 6 GHz family: Prefer 6GHz band | Prefer 6 GHz | 6GHz preferred | Wi-Fi 6E preferred | …");
        sb.AppendLine("    elseif ($v -match '(?i)6\\s*GHz|6GHz|6,?0\\s*GHz|Wi-?Fi\\s*6E|802\\.11be.*6|band\\s*6') {");
        sb.AppendLine("      if ($want6) { $s = if ($isOnly) { 45 } elseif ($isPref) { 100 } else { 90 } }");
        sb.AppendLine("      else { $s = if ($isOnly) { 5 } else { 25 } }");
        sb.AppendLine("    }");
        sb.AppendLine("    # 5 GHz family: Prefer 5GHz band | 5 GHz preferred | Preferable 5GHz | 5.2 GHz | …");
        sb.AppendLine("    elseif ($v -match '(?i)5\\s*GHz|5GHz|5\\.2|5,0|5\\.0|5800|band\\s*5|802\\.11a(?!x)|802\\.11ac|802\\.11n.*5') {");
        sb.AppendLine("      $s = if ($isOnly) { 35 } elseif ($isPref) { 80 } else { 70 }");
        sb.AppendLine("    }");
        sb.AppendLine("    [pscustomobject]@{ V=$v; S=$s }");
        sb.AppendLine("  }");
        sb.AppendLine("  $best = $scored | Sort-Object @{Expression='S';Descending=$true}, @{Expression='V';Descending=$false} | Select-Object -First 1");
        sb.AppendLine("  if ($best -and $best.S -gt 1) { return $best.V }");
        sb.AppendLine("  # Last resort: any prefer-5/6 string even if scoring missed odd punctuation");
        sb.AppendLine("  if ($want6) { $fb = $list | Where-Object { $_ -match '(?i)6' -and $_ -notmatch '(?i)2\\.4|only' } | Select-Object -First 1; if ($fb) { return $fb } }");
        sb.AppendLine("  $fb5 = $list | Where-Object { $_ -match '(?i)5' -and $_ -notmatch '(?i)2\\.4|only|6' } | Select-Object -First 1");
        sb.AppendLine("  if ($fb5) { return $fb5 }");
        sb.AppendLine("  return $null");
        sb.AppendLine("}");
        sb.AppendLine("function Find-AdvPropByName($adapterName, [string[]]$nameHints) {");
        sb.AppendLine("  $all = @(Get-NetAdapterAdvancedProperty -Name $adapterName -EA SilentlyContinue)");
        sb.AppendLine("  if ($all.Count -eq 0) { return $null }");
        sb.AppendLine("  # 1) exact DisplayName");
        sb.AppendLine("  foreach ($h in $nameHints) {");
        sb.AppendLine("    $hit = $all | Where-Object { [string]$_.DisplayName -eq $h } | Select-Object -First 1");
        sb.AppendLine("    if ($hit) { return $hit }");
        sb.AppendLine("  }");
        sb.AppendLine("  # 2) case-insensitive contains / whole-hint match (weird spacing, prefixes)");
        sb.AppendLine("  foreach ($h in $nameHints) {");
        sb.AppendLine("    $esc = [regex]::Escape($h)");
        sb.AppendLine("    $hit = $all | Where-Object { [string]$_.DisplayName -match ('(?i)' + $esc) } | Select-Object -First 1");
        sb.AppendLine("    if ($hit) { return $hit }");
        sb.AppendLine("  }");
        sb.AppendLine("  # 3) token fuzzy: all significant tokens from first hint present in DisplayName");
        sb.AppendLine("  foreach ($h in $nameHints) {");
        sb.AppendLine("    $tokens = @($h -split '\\s+' | Where-Object { $_.Length -ge 3 })");
        sb.AppendLine("    if ($tokens.Count -eq 0) { continue }");
        sb.AppendLine("    $hit = $all | Where-Object {");
        sb.AppendLine("      $dn = [string]$_.DisplayName");
        sb.AppendLine("      ($tokens | Where-Object { $dn -match ('(?i)' + [regex]::Escape($_)) }).Count -eq $tokens.Count");
        sb.AppendLine("    } | Select-Object -First 1");
        sb.AppendLine("    if ($hit) { return $hit }");
        sb.AppendLine("  }");
        sb.AppendLine("  # 4) registry keyword when hints look like band / power / roam");
        sb.AppendLine("  $joined = ($nameHints -join ' ')");
        sb.AppendLine("  if ($joined -match '(?i)band') {");
        sb.AppendLine("    $hit = $all | Where-Object { $_.RegistryKeyword -match '(?i)preferred.?band|band.?pref|preferable.?band|WirelessMode' } | Select-Object -First 1");
        sb.AppendLine("    if ($hit) { return $hit }");
        sb.AppendLine("  }");
        sb.AppendLine("  if ($joined -match '(?i)roam') {");
        sb.AppendLine("    $hit = $all | Where-Object { $_.RegistryKeyword -match '(?i)roam' } | Select-Object -First 1");
        sb.AppendLine("    if ($hit) { return $hit }");
        sb.AppendLine("  }");
        sb.AppendLine("  if ($joined -match '(?i)power|uapsd|mimo') {");
        sb.AppendLine("    $hit = $all | Where-Object { $_.RegistryKeyword -match '(?i)power.?save|uapsd|mimo.?power|PSMode' } | Select-Object -First 1");
        sb.AppendLine("    if ($hit) { return $hit }");
        sb.AppendLine("  }");
        sb.AppendLine("  return $null");
        sb.AppendLine("}");
        // Live band capability re-probe once (do not trust only pre-apply C# snapshot)
        sb.AppendLine("$wantBand6Live = $false");
        sb.AppendLine("$drvLive = (netsh wlan show drivers 2>$null | Out-String)");
        sb.AppendLine("if ($drvLive -match '(?i)802\\.11be|6\\s*GHz|Wi-?Fi\\s*6E') { $wantBand6Live = $true }");
        sb.AppendLine("if (-not $wantBand6Live -and " + prefer6Hint + " -eq 1) { $wantBand6Live = $true }");
        sb.AppendLine("Log \"[band] want6Live=$wantBand6Live\"");
        sb.AppendLine("$adapters = @(Get-NetAdapter -Physical -EA SilentlyContinue | Where-Object { $_.Status -eq 'Up' -or $_.Status -eq 'Disconnected' })");
        sb.AppendLine("if ($adapters.Count -eq 0) { $adapters = @(Get-NetAdapter -Physical -EA SilentlyContinue) }");
        sb.AppendLine("foreach ($a in $adapters) {");
        sb.AppendLine("  $n = $a.Name");
        sb.AppendLine("  $isWifi = Test-IsWifiAdapter $a");
        sb.AppendLine("  $kind = $(if ($isWifi) { 'Wi-Fi' } else { 'Ethernet' })");
        sb.AppendLine("  Log \"[NIC] $n ($kind) $($a.InterfaceDescription)\"");
        sb.AppendLine("  foreach ($kw in @('*IPChecksumOffloadIPv4','*TCPChecksumOffloadIPv4','*TCPChecksumOffloadIPv6','*UDPChecksumOffloadIPv4','*UDPChecksumOffloadIPv6')) { Set-Adv $n $kw 3 }");
        sb.AppendLine("  try { Set-NetAdapterChecksumOffload -Name $n -IpIPv4Enabled RxTxEnabled -TcpIPv4Enabled RxTxEnabled -TcpIPv6Enabled RxTxEnabled -UdpIPv4Enabled RxTxEnabled -UdpIPv6Enabled RxTxEnabled -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("  Set-Adv $n '*LsoV2IPv4' " + lso);
        sb.AppendLine("  Set-Adv $n '*LsoV2IPv6' " + lso);
        sb.AppendLine("  try { if (" + lso + " -eq 1) { Enable-NetAdapterLso -Name $n -NoRestart -EA SilentlyContinue } else { Disable-NetAdapterLso -Name $n -NoRestart -EA SilentlyContinue } } catch {}");
        sb.AppendLine("  try { if ('" + rsc + "' -eq 'enabled') { Enable-NetAdapterRsc -Name $n -EA SilentlyContinue } else { Disable-NetAdapterRsc -Name $n -EA SilentlyContinue } } catch {}");
        sb.AppendLine("  Set-Adv $n '*InterruptModeration' " + im);
        sb.AppendLine("  if (" + im + " -eq 0) {");
        sb.AppendLine("    try { Set-Adv $n 'ITR' 0 } catch {}");
        sb.AppendLine("    try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName 'Interrupt Moderation Rate' -DisplayValue 'Off' -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("  } else {");
        sb.AppendLine("    try {");
        sb.AppendLine("      $vals = @((Get-NetAdapterAdvancedProperty -Name $n -RegistryKeyword ITR -EA SilentlyContinue).ValidDisplayValues)");
        sb.AppendLine("      if ($vals -contains 'Adaptive') { Set-NetAdapterAdvancedProperty -Name $n -DisplayName 'Interrupt Moderation Rate' -DisplayValue 'Adaptive' -NoRestart -EA SilentlyContinue }");
        sb.AppendLine("      elseif ($vals -contains 'Medium') { Set-NetAdapterAdvancedProperty -Name $n -DisplayName 'Interrupt Moderation Rate' -DisplayValue 'Medium' -NoRestart -EA SilentlyContinue }");
        sb.AppendLine("    } catch {}");
        sb.AppendLine("  }");
        // Flow control: pause frames add latency under load — off for gaming, Rx+Tx for bulk
        sb.AppendLine("  Set-Adv $n '*FlowControl' " + flow);
        sb.AppendLine("  if (" + flow + " -eq 0) { try { Set-AdvDisplay $n 'Flow Control' 'Disabled' | Out-Null } catch {} }");
        // Power: EEE/green/selective off; IdleRestriction ON for latency (Intel: prevent low-power idle)
        sb.AppendLine("  foreach ($kw in @('*EEE','*EnergyEfficientEthernet','*GreenEthernet','*SelectiveSuspend','*ReduceSpeedOnPowerDown','*PMARPOffload','*PMNSOffload','*WakeOnMagicPacket','*WakeOnPattern')) { Set-Adv $n $kw 0 }");
        sb.AppendLine("  Set-Adv $n '*IdleRestriction' " + idleRestrict);
        sb.AppendLine("  if (" + idleRestrict + " -eq 1) { try { Set-AdvDisplay $n 'Idle power down restriction' 'Enabled' | Out-Null } catch {} }");
        sb.AppendLine("  try {");
        sb.AppendLine("    Set-NetAdapterPowerManagement -Name $n -SelectiveSuspend Disabled -WakeOnMagicPacket Disabled -WakeOnPattern Disabled -DeviceSleepOnDisconnect Disabled -ArpOffload Disabled -NSOffload Disabled -NoRestart -EA SilentlyContinue");
        sb.AppendLine("  } catch {");
        sb.AppendLine("    try { Set-NetAdapterPowerManagement -Name $n -SelectiveSuspend Disabled -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("  }");
        // RSS: Microsoft — many wireless NICs do not support RSS
        sb.AppendLine("  if (-not $isWifi) {");
        sb.AppendLine("    Set-Adv $n '*RSS' 1");
        sb.AppendLine("    try { Set-NetAdapterRss -Name $n -Enabled $true -EA SilentlyContinue } catch {}");
        sb.AppendLine("    try { $q = Get-NetAdapterAdvancedProperty -Name $n -RegistryKeyword '*NumRssQueues' -EA SilentlyContinue; if ($q -and $q.ValidRegistryValues) { $max = ($q.ValidRegistryValues | Measure-Object -Maximum).Maximum; if ($max -gt 0) { Set-Adv $n '*NumRssQueues' ([int]$max) } } } catch {}");
        // Ethernet-only deep driver knobs (Intel I225/I226, Realtek, Killer…)
        sb.AppendLine("    # DMA coalescing / adaptive IFS — latency killers when on");
        sb.AppendLine("    foreach ($kw in @('*DMACoalescing','DMACoalescing')) { Set-Adv $n $kw 0 }");
        sb.AppendLine("    try { Set-AdvDisplay $n 'DMA Coalescing' 'Disabled' | Out-Null } catch {}");
        sb.AppendLine("    try { Set-AdvDisplay $n 'Adaptive Inter-Frame Spacing' 'Disabled' | Out-Null } catch {}");
        sb.AppendLine("    try { Set-AdvDisplay $n 'Gigabit Lite' 'Disabled' | Out-Null } catch {}");
        sb.AppendLine("    try { Set-AdvDisplay $n 'Gigabit Master Slave Mode' 'Auto Detect' | Out-Null } catch {}");
        sb.AppendLine("    try { Set-AdvDisplay $n 'Speed & Duplex' 'Auto Negotiation' | Out-Null } catch {}");
        sb.AppendLine("    try { Set-AdvDisplay $n 'Wait for Link' 'Auto Detect' | Out-Null } catch {}");
        sb.AppendLine("    try { Set-AdvDisplay $n 'Log Link State Event' 'Disabled' | Out-Null } catch {}");
        sb.AppendLine("    # Jumbo: keep standard Ethernet (gaming); only force 1514/Disabled if exposed");
        sb.AppendLine("    try {");
        sb.AppendLine("      $jp = Find-AdvPropByName $n @('Jumbo Packet','Jumbo Frames','Jumbo Frame')");
        sb.AppendLine("      if ($jp) {");
        sb.AppendLine("        foreach ($v in @('Disabled','Off','1514','1500')) {");
        sb.AppendLine("          if (@($jp.ValidDisplayValues).Count -eq 0 -or @($jp.ValidDisplayValues) -contains $v) {");
        sb.AppendLine("            try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName $jp.DisplayName -DisplayValue $v -NoRestart -EA SilentlyContinue; Log \"[Eth] Jumbo => $v\"; break } catch {}");
        sb.AppendLine("          }");
        sb.AppendLine("        }");
        sb.AppendLine("      }");
        sb.AppendLine("    } catch {}");
        sb.AppendLine("    # Priority & VLAN: keep packet priority when available (QoS tags)");
        sb.AppendLine("    try {");
        sb.AppendLine("      $pv = Find-AdvPropByName $n @('Packet Priority & VLAN','Priority & VLAN','Priority and VLAN')");
        sb.AppendLine("      if ($pv) {");
        sb.AppendLine("        foreach ($v in @('Packet Priority Enabled','Priority Enabled','Enabled')) {");
        sb.AppendLine("          if (@($pv.ValidDisplayValues) -contains $v) {");
        sb.AppendLine("            try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName $pv.DisplayName -DisplayValue $v -NoRestart -EA SilentlyContinue; Log \"[Eth] $($pv.DisplayName) => $v\"; break } catch {}");
        sb.AppendLine("          }");
        sb.AppendLine("        }");
        sb.AppendLine("      }");
        sb.AppendLine("    } catch {}");
        sb.AppendLine("    # RSS profile / base processor — best-effort");
        sb.AppendLine("    try { Set-NetAdapterRss -Name $n -Profile NUMAStatic -EA SilentlyContinue } catch {}");
        sb.AppendLine("    try { Set-NetAdapterRss -Name $n -Profile ClosestProcessor -EA SilentlyContinue } catch {}");
        sb.AppendLine("  }");
        sb.AppendLine("  foreach ($kw in @('*ReceiveBuffers','*TransmitBuffers','ReceiveBuffers','TransmitBuffers')) {");
        sb.AppendLine("    try { $prop = Get-NetAdapterAdvancedProperty -Name $n -RegistryKeyword $kw -EA SilentlyContinue; if ($prop -and $prop.ValidRegistryValues -and @($prop.ValidRegistryValues).Count -gt 0) { $max = ($prop.ValidRegistryValues | Measure-Object -Maximum).Maximum; if ($max -gt 0) { Set-Adv $n $kw ([int]$max) } } } catch {}");
        sb.AppendLine("  }");
        // Wi-Fi: full gaming radio path
        sb.AppendLine("  if ($isWifi) {");
        sb.AppendLine("    function Set-WifiOff($adapterName, [string[]]$hints) {");
        sb.AppendLine("      $pp = Find-AdvPropByName $adapterName $hints");
        sb.AppendLine("      if (-not $pp) { return }");
        sb.AppendLine("      foreach ($off in @('Disabled','Off','Disable','No','Maximum Performance','Highest','0')) {");
        sb.AppendLine("        if (@($pp.ValidDisplayValues).Count -eq 0 -or @($pp.ValidDisplayValues) -contains $off) {");
        sb.AppendLine("          try { Set-NetAdapterAdvancedProperty -Name $adapterName -DisplayName $pp.DisplayName -DisplayValue $off -NoRestart -EA SilentlyContinue; Log \"[Wi-Fi] $($pp.DisplayName) => $off\"; return } catch {}");
        sb.AppendLine("        }");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("    function Set-WifiBest($adapterName, [string[]]$hints, [string[]]$prefer) {");
        sb.AppendLine("      $pp = Find-AdvPropByName $adapterName $hints");
        sb.AppendLine("      if (-not $pp) { return }");
        sb.AppendLine("      $vals = @($pp.ValidDisplayValues)");
        sb.AppendLine("      foreach ($want in $prefer) {");
        sb.AppendLine("        $hit = $vals | Where-Object { $_ -match ('(?i)' + [regex]::Escape($want)) } | Select-Object -First 1");
        sb.AppendLine("        if (-not $hit -and ($vals.Count -eq 0)) { $hit = $want }");
        sb.AppendLine("        if ($hit) {");
        sb.AppendLine("          try { Set-NetAdapterAdvancedProperty -Name $adapterName -DisplayName $pp.DisplayName -DisplayValue $hit -NoRestart -EA SilentlyContinue; Log \"[Wi-Fi] $($pp.DisplayName) => $hit\"; return } catch {}");
        sb.AppendLine("        }");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        // Power / coalescing / BT coexistence — always off for gaming
        sb.AppendLine("    foreach ($hint in @(");
        sb.AppendLine("      'MIMO Power Save','uAPSD support','uAPSD','Power Saving Mode','Power Saving','Power Save Mode','Power Save',");
        sb.AppendLine("      'Packet Coalescing','Ultra Low Power Mode','Ultra Low Power','Idle Power Save','Wireless Mode Power',");
        sb.AppendLine("      'System Idle Power Saver','Modern Standby WoWLAN','Wake on Magic Packet','Wake on Pattern Match',");
        sb.AppendLine("      'WoWLAN','Wake on WLAN','ARP offload for WoWLAN','NS offload for WoWLAN',");
        sb.AppendLine("      'Bluetooth Collaboration','Bluetooth AMP','Bluetooth Cooperation','Fat Channel Intolerant',");
        sb.AppendLine("      'Mixed Mode Protection','Throughput Booster','Network Address' )) {");
        sb.AppendLine("      # Throughput Booster: on for download preset only");
        sb.AppendLine("      if ($hint -match '(?i)Throughput Booster') { continue }");
        sb.AppendLine("      if ($hint -match '(?i)Network Address') { continue }");
        sb.AppendLine("      Set-WifiOff $n @($hint)");
        sb.AppendLine("    }");
        sb.AppendLine("    foreach ($p in @(Get-NetAdapterAdvancedProperty -Name $n -EA SilentlyContinue)) {");
        sb.AppendLine("      if ($p.RegistryKeyword -match '(?i)power.?save|uapsd|mimo.?power|packet.?coalesc|ulp|IdlePower|WoW|WakeOn|Bluetooth') {");
        sb.AppendLine("        foreach ($off in @('Disabled','Off','0','Maximum Performance')) {");
        sb.AppendLine("          if (@($p.ValidDisplayValues).Count -eq 0 -or @($p.ValidDisplayValues) -contains $off) {");
        sb.AppendLine("            try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName $p.DisplayName -DisplayValue $off -NoRestart -EA SilentlyContinue; break } catch {}");
        sb.AppendLine("          }");
        sb.AppendLine("        }");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        // Transmit power highest
        sb.AppendLine("    Set-WifiBest $n @('Transmit Power','Tx Power','Transmission Power','Output Power') @('Highest','Maximum','100','5','Level 5')");
        // Channel width: best / auto / 160 / 80
        sb.AppendLine("    Set-WifiBest $n @('Channel Width','Channel Width for 5GHz','Channel Width for 5 GHz','802.11n Channel Width for band 2','802.11n Channel Width for band 1') @('Auto','160','80','40','Best')");
        sb.AppendLine("    Set-WifiBest $n @('Channel Width for 2.4GHz','Channel Width for 2.4 GHz') @('Auto','20')");
        // 802.11 mode — prefer latest
        sb.AppendLine("    Set-WifiBest $n @('Wireless Mode','802.11a/b/g Wireless Mode','802.11 Mode','Wi-Fi Mode') @('802.11be','802.11ax','802.11ac','6','5','Auto','Default')");
        // MU-MIMO / OFDMA / Beamform — on when present
        sb.AppendLine("    Set-WifiBest $n @('MU-MIMO','Multi-User MIMO') @('Enabled','On','Enable')");
        sb.AppendLine("    Set-WifiBest $n @('OFDMA','Orthogonal Frequency Division Multiple Access') @('Enabled','On','Enable','Auto')");
        sb.AppendLine("    Set-WifiBest $n @('Beamforming','Explicit Beamforming','Implicit Beamforming','Transmit Beamforming') @('Enabled','On','Enable')");
        sb.AppendLine("    Set-WifiBest $n @('BSS Color','BSS Coloring') @('Enabled','On','Enable','Auto')");
        // Throughput booster only for highest-download preset
        if (!latency)
        {
            sb.AppendLine("    Set-WifiBest $n @('Throughput Booster') @('Enabled','On','Enable')");
        }
        else
        {
            sb.AppendLine("    Set-WifiOff $n @('Throughput Booster')");
        }
        // Preferred band + roam
        sb.AppendLine("    $adapterWants6 = $wantBand6Live");
        sb.AppendLine("    foreach ($p in @(Get-NetAdapterAdvancedProperty -Name $n -EA SilentlyContinue)) {");
        sb.AppendLine("      $blob = \"$($p.DisplayName) $(($p.ValidDisplayValues) -join ' ')\"");
        sb.AppendLine("      if ($blob -match '(?i)6\\s*GHz|6GHz|Wi-?Fi\\s*6E') { $adapterWants6 = $true }");
        sb.AppendLine("    }");
        sb.AppendLine("    $bandProp = Find-AdvPropByName $n @('Preferred Band','Preferable Band','Band Preference','Preferred Band Selection','Preferred WLAN Band','Wireless Band Preference','Band Selection')");
        sb.AppendLine("    if ($bandProp) {");
        sb.AppendLine("      $vals = @($bandProp.ValidDisplayValues)");
        sb.AppendLine("      if ($vals.Count -eq 0 -and $bandProp.DisplayValue) { $vals = @($bandProp.DisplayValue) }");
        sb.AppendLine("      $pick = Select-BandDisplayValue -vals $vals -want6 $adapterWants6");
        sb.AppendLine("      if ($pick) {");
        sb.AppendLine("        try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName $bandProp.DisplayName -DisplayValue $pick -NoRestart -EA SilentlyContinue");
        sb.AppendLine("          Log \"[Wi-Fi] $($bandProp.DisplayName) => $pick (want6=$adapterWants6)\" } catch {}");
        sb.AppendLine("      } else { Log \"[Wi-Fi] no suitable band value in: $($vals -join ' | ')\" }");
        sb.AppendLine("    } else { Log '[Wi-Fi] no Preferred Band-like property on this driver' }");
        sb.AppendLine("    $roam = Find-AdvPropByName $n @('Roaming Aggressiveness','Roaming Sensitivity','Roam Aggressiveness','Roaming Aggressive')");
        sb.AppendLine("    if ($roam) {");
        sb.AppendLine("      $rv = @($roam.ValidDisplayValues) | Where-Object { $_ -match '(?i)medium' } | Select-Object -First 1");
        sb.AppendLine("      if (-not $rv) { $rv = @($roam.ValidDisplayValues) | Where-Object { $_ -match '(?i)3|mid' } | Select-Object -First 1 }");
        sb.AppendLine("      if ($rv) { try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName $roam.DisplayName -DisplayValue $rv -NoRestart -EA SilentlyContinue; Log \"[Wi-Fi] roam => $rv\" } catch {} }");
        sb.AppendLine("    }");
        // Prefer 5/6 GHz via netsh wlan profiles is too invasive; band prop is enough
        sb.AppendLine("  }");
        sb.AppendLine("  try {");
        sb.AppendLine("    $class = 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e972-e325-11ce-bfc1-08002be10318}'");
        sb.AppendLine("    Get-ChildItem $class -EA SilentlyContinue | ForEach-Object {");
        sb.AppendLine("      $props = Get-ItemProperty $_.PSPath -EA SilentlyContinue");
        sb.AppendLine("      if ($props.DriverDesc -eq $a.InterfaceDescription) { Set-ItemProperty $_.PSPath -Name PnPCapabilities -Value 24 -Type DWord -Force -EA SilentlyContinue }");
        sb.AppendLine("    }");
        sb.AppendLine("  } catch {}");
        sb.AppendLine("}");

        // --- Adapter bindings = the checkboxes in Ethernet Properties → Networking ---
        // Target (gaming lean, matches common "best" host stack):
        //   ON:  QoS Packet Scheduler, IPv4, IPv6
        //   OFF: Client for Microsoft Networks, File and Printer Sharing,
        //        Multiplexor, LLDP, LLTD Mapper, LLTD Responder
        sb.AppendLine("""
function Set-AdapterBindings {
  $ads = @(Get-NetAdapter -Physical -EA SilentlyContinue)
  # ComponentIDs from Get-NetAdapterBinding (same list as the Properties UI checkboxes)
  $enable = @('ms_pacer','ms_tcpip','ms_tcpip6')
  $disable = @('ms_msclient','ms_server','ms_implat','ms_lldp','ms_lltdio','ms_rspndr')
  foreach ($a in $ads) {
    $n = $a.Name
    foreach ($id in $enable) {
      try { Enable-NetAdapterBinding -Name $n -ComponentID $id -EA SilentlyContinue } catch {}
    }
    foreach ($id in $disable) {
      try { Disable-NetAdapterBinding -Name $n -ComponentID $id -EA SilentlyContinue } catch {}
    }
    $bits = @()
    try {
      $b = Get-NetAdapterBinding -Name $n -EA SilentlyContinue
      foreach ($row in $b) {
        $on = if ($row.Enabled) { 'on' } else { 'off' }
        $bits += "$($row.ComponentID)=$on"
      }
    } catch {}
    Log ("[bind] $n " + ($bits -join ' '))
  }
}
# Delivery Optimization — stop peer upload stealing bandwidth on gaming PCs
try {
  New-Item 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config' -Force | Out-Null
  Set-Dword 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config' 'DODownloadMode' 0
  Log '[DO] DownloadMode=0 (no peer sharing)'
} catch { Log '[DO] skipped' }
# Ensure QoS / Packet Scheduler service can run
try { Set-Service -Name Psched -StartupType Automatic -EA SilentlyContinue } catch {}
try { Start-Service -Name Psched -EA SilentlyContinue } catch {}
# Disable Teredo / ISATAP tunnels (not used for gaming; can add background noise)
try { netsh interface teredo set state disabled | Out-Null } catch {}
try { netsh interface isatap set state disabled | Out-Null } catch {}
try { netsh interface 6to4 set state disabled | Out-Null } catch {}
Log '[tunnel] teredo/isatap/6to4 disabled'
# Network Discovery / NetBIOS chatter — leave Client binding off; disable NetBIOS over TCP/IP on IPv4
try {
  Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces' -EA SilentlyContinue | ForEach-Object {
    Set-Dword $_.PSPath 'NetbiosOptions' 2
  }
  Log '[NetBIOS] NetbiosOptions=2 (disabled over TCP/IP)'
} catch { Log '[NetBIOS] skipped' }
# NCSI active probes off reduces background chatter (connectivity still works via passive)
try {
  New-Item 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\NetworkConnectivityStatusIndicator' -Force | Out-Null
  Set-Dword 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\NetworkConnectivityStatusIndicator' 'NoActiveProbe' 1
  Log '[NCSI] NoActiveProbe=1'
} catch {}
Set-AdapterBindings
""");

        // Prefer Ethernet 100% when linked. Metric must stick after Restart-NetAdapter:
        // re-stamp used to run before DHCP returned → "No usable Ethernet" → metric stayed ~20 auto.
        // Set metric on ANY Up Ethernet (IP not required); prefer adapters that already have IPv4.
        sb.AppendLine("""
function Set-EthMetrics {
  $ads = @(Get-NetAdapter -Physical -EA SilentlyContinue)
  $ethUp = @($ads | Where-Object { -not (Test-IsWifiAdapter $_) -and $_.Status -eq 'Up' })
  if ($ethUp.Count -eq 0) {
    Log '[Exo-NET] No Up Ethernet adapters for metric'
    return $false
  }
  # Rank: real IPv4 first, then link speed
  $ranked = foreach ($e in $ethUp) {
    $hasIp = @(Get-NetIPAddress -InterfaceIndex $e.ifIndex -AddressFamily IPv4 -EA SilentlyContinue |
      Where-Object { $_.IPAddress -notlike '169.254.*' }).Count -gt 0
    $spd = 0L
    try { $spd = [int64]$e.ReceiveLinkSpeed } catch { $spd = 0 }
    [pscustomobject]@{ A=$e; HasIp=$hasIp; Spd=$spd }
  }
  $ordered = @($ranked | Sort-Object @{Expression='HasIp';Descending=$true}, @{Expression='Spd';Descending=$true} | ForEach-Object { $_.A })
  $i = 0
  $okAny = $false
  foreach ($e in $ordered) {
    if ($i -eq 0) { $metric = 1 } else { $metric = 5 + $i }
    foreach ($af in @('IPv4','IPv6')) {
      try {
        Set-NetIPInterface -InterfaceIndex $e.ifIndex -AddressFamily $af -AutomaticMetric Disabled -EA SilentlyContinue
        Set-NetIPInterface -InterfaceIndex $e.ifIndex -AddressFamily $af -InterfaceMetric $metric -EA SilentlyContinue
      } catch {}
    }
    # netsh belt-and-suspenders (some drivers ignore Set-NetIPInterface until link settles)
    try { netsh interface ipv4 set interface interface=$($e.ifIndex) metric=$metric | Out-Null } catch {}
    try { netsh interface ipv6 set interface interface=$($e.ifIndex) metric=$metric | Out-Null } catch {}
    $live = $null; $auto = $null
    try {
      $mi = Get-NetIPInterface -InterfaceIndex $e.ifIndex -AddressFamily IPv4 -EA SilentlyContinue
      if ($mi) { $live = [int]$mi.InterfaceMetric; $auto = [string]$mi.AutomaticMetric }
    } catch {}
    Log "[NIC] Ethernet metric $($e.Name) => want $metric live=$live auto=$auto"
    if ($live -eq $metric) { $okAny = $true }
    $i++
  }
  return $okAny
}
""");
        sb.AppendLine("$ethReadyOk = Set-EthMetrics");
        sb.AppendLine("if (" + preferEth + " -eq 1) {");
        sb.AppendLine("  # Prefer eth when any eth is Up with IP; still raise Wi-Fi metric if eth only linked");
        sb.AppendLine("  $ads = @(Get-NetAdapter -Physical -EA SilentlyContinue)");
        sb.AppendLine("  $ethHasIp = $false");
        sb.AppendLine("  foreach ($e in @($ads | Where-Object { -not (Test-IsWifiAdapter $_) -and $_.Status -eq 'Up' })) {");
        sb.AppendLine("    $ip = @(Get-NetIPAddress -InterfaceIndex $e.ifIndex -AddressFamily IPv4 -EA SilentlyContinue | Where-Object { $_.IPAddress -notlike '169.254.*' })");
        sb.AppendLine("    if ($ip.Count -gt 0) { $ethHasIp = $true; break }");
        sb.AppendLine("  }");
        sb.AppendLine("  if ($ethHasIp) {");
        sb.AppendLine("    Log '[Exo-NET] Ethernet ready - preferring Ethernet (lowest latency)'");
        sb.AppendLine("    foreach ($w in @($ads | Where-Object { Test-IsWifiAdapter $_ })) {");
        sb.AppendLine("      try {");
        sb.AppendLine("        if ($w.Status -ne 'Disabled') {");
        sb.AppendLine("          try { Set-NetIPInterface -InterfaceIndex $w.ifIndex -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 75 -EA SilentlyContinue } catch {}");
        sb.AppendLine("          Disable-NetAdapter -Name $w.Name -Confirm:$false -EA SilentlyContinue");
        sb.AppendLine("          Log \"[NIC] Wi-Fi disabled: $($w.Name)\"");
        sb.AppendLine("        }");
        sb.AppendLine("      } catch { Log \"[NIC] could not disable $($w.Name)\" }");
        sb.AppendLine("    }");
        sb.AppendLine("  } else {");
        sb.AppendLine("    Log '[Exo-NET] Ethernet Up but no IPv4 yet - not disabling Wi-Fi'");
        sb.AppendLine("  }");
        sb.AppendLine("}");

        sb.AppendLine("try { Clear-DnsClientCache -EA SilentlyContinue } catch {}");

        // Restart Ethernet only if user confirmed (never auto Wi-Fi restart)
        sb.AppendLine("if (" + restartEth + " -eq 1) {");
        sb.AppendLine("  $adapters = @(Get-NetAdapter -Physical -EA SilentlyContinue)");
        sb.AppendLine("  foreach ($a in @($adapters | Where-Object { $_.Status -eq 'Up' -and -not (Test-IsWifiAdapter $_) })) {");
        sb.AppendLine("    try { Restart-NetAdapter -Name $a.Name -Confirm:$false -EA SilentlyContinue; Log \"[NIC] restarted (Ethernet) $($a.Name)\" } catch {}");
        sb.AppendLine("  }");
        // Wait for link + re-stamp metrics repeatedly (DHCP lag was wiping metric=1)
        sb.AppendLine("  Log '[NIC] Waiting for Ethernet after restart, then re-stamping metrics...'");
        sb.AppendLine("  $metricOk = $false");
        sb.AppendLine("  for ($t = 0; $t -lt 20; $t++) {");
        sb.AppendLine("    Start-Sleep -Seconds 1");
        sb.AppendLine("    $metricOk = [bool](Set-EthMetrics)");
        sb.AppendLine("    if ($metricOk) { Log \"[NIC] Metric verified after $($t+1)s\"; break }");
        sb.AppendLine("  }");
        sb.AppendLine("  if (-not $metricOk) { Log '[NIC] WARN metric not verified after restart wait — last Set-EthMetrics attempt done' }");
        sb.AppendLine("} else {");
        sb.AppendLine("  Log '[Exo-NET] Ethernet restart skipped (user declined)'");
        sb.AppendLine("  # Still re-stamp once more so AutomaticMetric cannot race");
        sb.AppendLine("  Start-Sleep -Milliseconds 400");
        sb.AppendLine("  [void](Set-EthMetrics)");
        sb.AppendLine("}");
        sb.AppendLine("Log '[Exo-NET] DONE preset=" + preset + "'");
        sb.AppendLine("exit 0");
        return sb.ToString();
    }

    /// <summary>
    /// Undo Exo network apply → Windows-typical defaults.
    /// Restores Ethernet Properties checkboxes to stock-ish state.
    /// </summary>
    public static string BuildRepair()
    {
        var sb = new StringBuilder(8_000);
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("$ProgressPreference = 'SilentlyContinue'");
        sb.AppendLine("$log = Join-Path $env:TEMP 'exo-net-repair-last.log'");
        sb.AppendLine("function Log([string]$m) { $ts = Get-Date -Format o; Add-Content -Path $log -Value \"$ts $m\" -EA SilentlyContinue; Write-Host $m }");
        sb.AppendLine("'' | Set-Content -Path $log -EA SilentlyContinue");
        sb.AppendLine("Log '[Exo-NET-REPAIR] Restoring stock-ish network stack'");
        sb.AppendLine("""
function Set-Dword([string]$Path, [string]$Name, [int]$Value) {
  if (-not (Test-Path -LiteralPath $Path)) { New-Item -Path $Path -Force | Out-Null }
  Remove-ItemProperty -LiteralPath $Path -Name $Name -Force -EA SilentlyContinue
  New-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -PropertyType DWord -Force -EA SilentlyContinue | Out-Null
}
function Remove-Prop([string]$Path, [string]$Name) {
  if (Test-Path -LiteralPath $Path) { Remove-ItemProperty -LiteralPath $Path -Name $Name -Force -EA SilentlyContinue }
}
# Host stack → Windows defaults
$tcp = 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters'
Set-Dword $tcp 'DisableTaskOffload' 0
Remove-Prop $tcp 'GlobalMaxTcpWindowSize'
Remove-Prop $tcp 'TcpWindowSize'
$mm = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile'
# Default SystemResponsiveness is 20
Set-Dword $mm 'SystemResponsiveness' 20
# Default NetworkThrottlingIndex is 10
Set-Dword $mm 'NetworkThrottlingIndex' 10
# Remove QoS reserve policy so OS default applies again
Remove-Prop 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Psched' 'NonBestEffortLimit'
try { Remove-Item 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Psched' -Recurse -Force -EA SilentlyContinue } catch {}
# Clear Nagle / ACK gaming keys
Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces' -EA SilentlyContinue | ForEach-Object {
  $p = $_.PSPath
  Remove-Prop $p 'TcpAckFrequency'
  Remove-Prop $p 'TCPNoDelay'
  Remove-Prop $p 'TcpDelAckTicks'
}
# TCP global defaults
netsh int tcp set global autotuninglevel=normal | Out-Null
netsh int tcp set global rsc=enabled | Out-Null
netsh int tcp set global rss=enabled | Out-Null
try { netsh int tcp set heuristics enabled | Out-Null } catch {}
try { netsh int tcp set supplemental template=internet congestionprovider=cubic | Out-Null } catch {}
foreach ($pr in @('Internet','InternetCustom')) {
  try { Set-NetTCPSetting -SettingName $pr -AutoTuningLevelLocal Normal -EA SilentlyContinue } catch {}
  try { Set-NetTCPSetting -SettingName $pr -ScalingHeuristics Enabled -EA SilentlyContinue } catch {}
}
# Delivery Optimization — clear Exo force-off
try { Remove-Prop 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config' 'DODownloadMode' } catch {}
# NCSI active probe policy
try { Remove-Prop 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\NetworkConnectivityStatusIndicator' 'NoActiveProbe' } catch {}
# NetBIOS over TCP/IP — default (system / DHCP)
try {
  Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces' -EA SilentlyContinue | ForEach-Object {
    Remove-Prop $_.PSPath 'NetbiosOptions'
  }
} catch {}
# Tunnels — default (system managed)
try { netsh interface teredo set state default | Out-Null } catch {}
try { netsh interface isatap set state default | Out-Null } catch {}
try { netsh interface 6to4 set state default | Out-Null } catch {}
Log '[repair] host stack + tunnels restored'

# Ethernet Properties checkboxes → stock Windows-like (most ON except Multiplexor)
$enable = @('ms_msclient','ms_server','ms_pacer','ms_tcpip','ms_tcpip6','ms_lldp','ms_lltdio','ms_rspndr')
$disable = @('ms_implat')
$ads = @(Get-NetAdapter -Physical -EA SilentlyContinue)
foreach ($a in $ads) {
  $n = $a.Name
  foreach ($id in $enable) {
    try { Enable-NetAdapterBinding -Name $n -ComponentID $id -EA SilentlyContinue } catch {}
  }
  foreach ($id in $disable) {
    try { Disable-NetAdapterBinding -Name $n -ComponentID $id -EA SilentlyContinue } catch {}
  }
  # LSO / RSC default-ish (on for modern NICs)
  try { Set-NetAdapterAdvancedProperty -Name $n -RegistryKeyword '*LsoV2IPv4' -RegistryValue 1 -NoRestart -EA SilentlyContinue } catch {}
  try { Set-NetAdapterAdvancedProperty -Name $n -RegistryKeyword '*LsoV2IPv6' -RegistryValue 1 -NoRestart -EA SilentlyContinue } catch {}
  try { Set-NetAdapterAdvancedProperty -Name $n -RegistryKeyword '*RscIPv4' -RegistryValue 1 -NoRestart -EA SilentlyContinue } catch {}
  # Automatic metric (OS default)
  foreach ($af in @('IPv4','IPv6')) {
    try {
      Set-NetIPInterface -InterfaceIndex $a.ifIndex -AddressFamily $af -AutomaticMetric Enabled -EA SilentlyContinue
    } catch {}
  }
  Log "[repair] bindings+metric auto $n"
}
# Re-enable Wi-Fi adapters Exo may have disabled
foreach ($w in @($ads | Where-Object { [string]$_.PhysicalMediaType -match '802\.11|Wireless' -or [string]$_.Name -match '(?i)Wi-?Fi|Wireless' })) {
  try {
    if ($w.Status -eq 'Disabled') {
      Enable-NetAdapter -Name $w.Name -Confirm:$false -EA SilentlyContinue
      Log "[repair] Wi-Fi re-enabled: $($w.Name)"
    }
  } catch {}
}
try { Clear-DnsClientCache -EA SilentlyContinue } catch {}
Log '[Exo-NET-REPAIR] DONE'
exit 0
""");
        return sb.ToString();
    }
}

