using System.Text;
using OptiHub.Models;

namespace OptiHub.Services;

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
        sb.AppendLine("$log = Join-Path $env:TEMP 'optihub-net-last.log'");
        sb.AppendLine("function Log([string]$m) { $ts = Get-Date -Format o; Add-Content -Path $log -Value \"$ts $m\" -EA SilentlyContinue; Write-Host $m }");
        sb.AppendLine("'' | Set-Content -Path $log -EA SilentlyContinue");
        sb.AppendLine("Log '[OptiHub-NET] Preset=" + preset + " ethFirst=" + preferEth + " restartEth=" + restartEth + " band6hint=" + prefer6Hint + "'");
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
        sb.AppendLine("  }");
        sb.AppendLine("  foreach ($kw in @('*ReceiveBuffers','*TransmitBuffers','ReceiveBuffers','TransmitBuffers')) {");
        sb.AppendLine("    try { $prop = Get-NetAdapterAdvancedProperty -Name $n -RegistryKeyword $kw -EA SilentlyContinue; if ($prop -and $prop.ValidRegistryValues -and @($prop.ValidRegistryValues).Count -gt 0) { $max = ($prop.ValidRegistryValues | Measure-Object -Maximum).Maximum; if ($max -gt 0) { Set-Adv $n $kw ([int]$max) } } } catch {}");
        sb.AppendLine("  }");
        // Wi-Fi: power-save + fuzzy Preferred Band (vendor strings vary wildly)
        sb.AppendLine("  if ($isWifi) {");
        sb.AppendLine("    foreach ($hint in @('MIMO Power Save','uAPSD','Power Saving','Power Save Mode','Power Save','Packet Coalescing','Ultra Low Power','Ultra Low Power Mode','Idle Power Save','Wireless Mode Power')) {");
        sb.AppendLine("      $pp = Find-AdvPropByName $n @($hint)");
        sb.AppendLine("      if (-not $pp) { continue }");
        sb.AppendLine("      foreach ($off in @('Disabled','Off','Disable','No','Maximum Performance','0')) {");
        sb.AppendLine("        if (@($pp.ValidDisplayValues).Count -eq 0 -or @($pp.ValidDisplayValues) -contains $off) {");
        sb.AppendLine("          try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName $pp.DisplayName -DisplayValue $off -NoRestart -EA SilentlyContinue; Log \"[Wi-Fi] $($pp.DisplayName) => $off\"; break } catch {}");
        sb.AppendLine("        }");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("    # also kill any remaining power-ish props by registry keyword");
        sb.AppendLine("    foreach ($p in @(Get-NetAdapterAdvancedProperty -Name $n -EA SilentlyContinue)) {");
        sb.AppendLine("      if ($p.RegistryKeyword -match '(?i)power.?save|uapsd|mimo.?power|packet.?coalesc|ulp|IdlePower') {");
        sb.AppendLine("        foreach ($off in @('Disabled','Off','0','Maximum Performance')) {");
        sb.AppendLine("          if (@($p.ValidDisplayValues).Count -eq 0 -or @($p.ValidDisplayValues) -contains $off) {");
        sb.AppendLine("            try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName $p.DisplayName -DisplayValue $off -NoRestart -EA SilentlyContinue; break } catch {}");
        sb.AppendLine("          }");
        sb.AppendLine("        }");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("    # live 6 GHz from this adapter's own valid values");
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
        sb.AppendLine("  }");
        sb.AppendLine("  try {");
        sb.AppendLine("    $class = 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e972-e325-11ce-bfc1-08002be10318}'");
        sb.AppendLine("    Get-ChildItem $class -EA SilentlyContinue | ForEach-Object {");
        sb.AppendLine("      $props = Get-ItemProperty $_.PSPath -EA SilentlyContinue");
        sb.AppendLine("      if ($props.DriverDesc -eq $a.InterfaceDescription) { Set-ItemProperty $_.PSPath -Name PnPCapabilities -Value 24 -Type DWord -Force -EA SilentlyContinue }");
        sb.AppendLine("    }");
        sb.AppendLine("  } catch {}");
        sb.AppendLine("}");

        // Prefer Ethernet 100% when linked with a real IPv4 (gaming lowest-latency path).
        // Metric must stick: disable AutomaticMetric first, then set InterfaceMetric.
        // Restart-NetAdapter often re-enables automatic metric — re-stamp after restart.
        sb.AppendLine("function Set-EthMetrics {");
        sb.AppendLine("  $ethReady = @()");
        sb.AppendLine("  $ads = @(Get-NetAdapter -Physical -EA SilentlyContinue)");
        sb.AppendLine("  foreach ($e in @($ads | Where-Object { -not (Test-IsWifiAdapter $_) -and $_.Status -eq 'Up' })) {");
        sb.AppendLine("    $ip = @(Get-NetIPAddress -InterfaceIndex $e.ifIndex -AddressFamily IPv4 -EA SilentlyContinue | Where-Object { $_.IPAddress -notlike '169.254.*' })");
        sb.AppendLine("    if ($ip.Count -gt 0) { $ethReady += $e }");
        sb.AppendLine("  }");
        sb.AppendLine("  if ($ethReady.Count -eq 0) {");
        sb.AppendLine("    Log '[OptiHub-NET] No usable Ethernet (up+IPv4) - keeping Wi-Fi'");
        sb.AppendLine("    return $false");
        sb.AppendLine("  }");
        sb.AppendLine("  $ordered = @($ethReady | Sort-Object { try { [int64]$_.ReceiveLinkSpeed } catch { 0 } } -Descending)");
        sb.AppendLine("  $i = 0");
        sb.AppendLine("  foreach ($e in $ordered) {");
        sb.AppendLine("    if ($i -eq 0) { $metric = 1 } else { $metric = 5 + $i }");
        sb.AppendLine("    foreach ($af in @('IPv4','IPv6')) {");
        sb.AppendLine("      try {");
        sb.AppendLine("        Set-NetIPInterface -InterfaceIndex $e.ifIndex -AddressFamily $af -AutomaticMetric Disabled -EA SilentlyContinue");
        sb.AppendLine("        Set-NetIPInterface -InterfaceIndex $e.ifIndex -AddressFamily $af -InterfaceMetric $metric -EA SilentlyContinue");
        sb.AppendLine("      } catch {}");
        sb.AppendLine("    }");
        sb.AppendLine("    $live = $null");
        sb.AppendLine("    try { $live = (Get-NetIPInterface -InterfaceIndex $e.ifIndex -AddressFamily IPv4 -EA SilentlyContinue).InterfaceMetric } catch {}");
        sb.AppendLine("    Log \"[NIC] Ethernet metric $($e.Name) => want $metric live=$live autoOff\"");
        sb.AppendLine("    $i++");
        sb.AppendLine("  }");
        sb.AppendLine("  return $true");
        sb.AppendLine("}");
        sb.AppendLine("$ethReadyOk = Set-EthMetrics");
        sb.AppendLine("if ($ethReadyOk -and (" + preferEth + " -eq 1)) {");
        sb.AppendLine("  Log '[OptiHub-NET] Ethernet ready - preferring Ethernet (lowest latency)'");
        sb.AppendLine("  $adapters = @(Get-NetAdapter -Physical -EA SilentlyContinue)");
        sb.AppendLine("  foreach ($w in @($adapters | Where-Object { Test-IsWifiAdapter $_ })) {");
        sb.AppendLine("    try {");
        sb.AppendLine("      if ($w.Status -ne 'Disabled') {");
        sb.AppendLine("        try { Set-NetIPInterface -InterfaceIndex $w.ifIndex -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 75 -EA SilentlyContinue } catch {}");
        sb.AppendLine("        Disable-NetAdapter -Name $w.Name -Confirm:$false -EA SilentlyContinue");
        sb.AppendLine("        Log \"[NIC] Wi-Fi disabled: $($w.Name)\"");
        sb.AppendLine("      }");
        sb.AppendLine("    } catch { Log \"[NIC] could not disable $($w.Name)\" }");
        sb.AppendLine("  }");
        sb.AppendLine("}");

        sb.AppendLine("try { Clear-DnsClientCache -EA SilentlyContinue } catch {}");

        // Restart Ethernet only if user confirmed (never auto Wi-Fi restart)
        sb.AppendLine("if (" + restartEth + " -eq 1) {");
        sb.AppendLine("  $adapters = @(Get-NetAdapter -Physical -EA SilentlyContinue)");
        sb.AppendLine("  foreach ($a in @($adapters | Where-Object { $_.Status -eq 'Up' -and -not (Test-IsWifiAdapter $_) })) {");
        sb.AppendLine("    try { Restart-NetAdapter -Name $a.Name -Confirm:$false -EA SilentlyContinue; Log \"[NIC] restarted (Ethernet) $($a.Name)\" } catch {}");
        sb.AppendLine("  }");
        sb.AppendLine("  Start-Sleep -Seconds 3");
        // Re-stamp metrics — adapter restart reverts AutomaticMetric on many drivers
        sb.AppendLine("  Log '[NIC] Re-stamping Ethernet metrics after restart...'");
        sb.AppendLine("  [void](Set-EthMetrics)");
        sb.AppendLine("} else {");
        sb.AppendLine("  Log '[OptiHub-NET] Ethernet restart skipped (user declined)'");
        sb.AppendLine("}");
        sb.AppendLine("Log '[OptiHub-NET] DONE preset=" + preset + "'");
        sb.AppendLine("exit 0");
        return sb.ToString();
    }
}

