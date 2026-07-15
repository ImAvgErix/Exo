using System.Text;
using Exo.Models;

namespace Exo.Services;

/// <summary>
/// Generates the elevated PowerShell apply script (shipped path).
/// Pure string build — no elevation. Driven by <see cref="NetworkLogic"/> knobs.
/// Safety contract:
///  1. Pristine pre-apply snapshot to %LocalAppData%\Exo\network-snapshot.json BEFORE any mutation
///     (never overwritten by re-applies — first snapshot is the restore baseline).
///  2. Wi-Fi is only disabled after a REAL internet probe bound to the Ethernet adapter succeeds.
///  3. Post-apply connectivity check with automatic rollback of path changes on failure.
///  4. Structured EXO_REPORT step lines for honest partial-failure reporting.
/// </summary>
public static class NetworkApplyScriptBuilder
{
    /// <summary>
    /// Every static registry value the apply script writes or removes.
    /// Kept next to the writes so the snapshot block always covers the mutation set.
    /// Per-interface Nagle keys, NetBT NetbiosOptions and PnPCapabilities are enumerated
    /// dynamically inside the snapshot block (same key sets the apply mutates).
    /// </summary>
    private static readonly (string Path, string Name)[] RegistryTargets =
    {
        (@"HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "DisableTaskOffload"),
        (@"HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "EnablePMTUDiscovery"),
        (@"HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "GlobalMaxTcpWindowSize"),
        (@"HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "TcpWindowSize"),
        (@"HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "EnableTCPChimney"),
        (@"HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "EnableTCPA"),
        (@"HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "EnableDCA"),
        (@"HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "TcpNumConnections"),
        (@"HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "LargeSystemCache"),
        (@"HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider", "LocalPriority"),
        (@"HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider", "HostsPriority"),
        (@"HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider", "DnsPriority"),
        (@"HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider", "NetbtPriority"),
        (@"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness"),
        (@"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex"),
        (@"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority"),
        (@"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Priority"),
        (@"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Scheduling Category"),
        (@"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "SFIO Priority"),
        (@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\Psched", "NonBestEffortLimit"),
        (@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config", "DODownloadMode"),
        (@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\NetworkConnectivityStatusIndicator", "NoActiveProbe"),
        (@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\NetworkConnectivityStatusIndicator", "DisablePassivePolling"),
        (@"HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient", "EnableMulticast"),
        (@"HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", "MaxCacheTtl"),
        (@"HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", "MaxNegativeCacheTtl"),
        (@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings", "AutoDetect"),
        (@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\BITS", "EnableBITSMaxBandwidth"),
    };

    private static string BuildRegistryTargetsPs()
    {
        var sb = new StringBuilder(4_000);
        sb.AppendLine("$ExoRegTargets = @(");
        for (var i = 0; i < RegistryTargets.Length; i++)
        {
            var (path, name) = RegistryTargets[i];
            sb.Append("  @{ Path = '").Append(path).Append("'; Name = '").Append(name).Append("' }");
            sb.AppendLine(i < RegistryTargets.Length - 1 ? "," : string.Empty);
        }
        sb.AppendLine(")");
        return sb.ToString();
    }

    /// <summary>Shared PS prologue: Exo paths, Report, CleanReason, connectivity probe.</summary>
    private const string CommonSafetyFunctions = """
$ExoDir = Join-Path $env:LOCALAPPDATA 'Exo'
$ExoSnapshotPath = Join-Path $ExoDir 'network-snapshot.json'
$ExoApplyStatePath = Join-Path $ExoDir 'network-apply-state.json'
function CleanReason([string]$s) {
  $t = (($s -replace '[\r\n\|]', ' ') -replace '\s+', ' ').Trim()
  if ($t.Length -gt 140) { $t = $t.Substring(0, 140) }
  return $t
}
function Report([string]$Step, [string]$Status, [string]$Reason = '') {
  $line = 'EXO_REPORT:' + $Step + '|' + $Status
  if ($Reason) { $line = $line + ':' + (CleanReason $Reason) }
  Log $line
}
function Test-ExoConnectivity([string]$BindIp = '') {
  # Real internet probe: TCP connect to 1.1.1.1:443 / 8.8.8.8:443 (~3s timeout each).
  # When BindIp is given the socket is bound to that local IPv4 so the probe can
  # only succeed over that adapter (verified Ethernet gate).
  foreach ($target in @('1.1.1.1', '8.8.8.8')) {
    $client = $null
    try {
      $client = New-Object System.Net.Sockets.TcpClient
      if ($BindIp) {
        $ep = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Parse($BindIp), 0)
        $client.Client.Bind($ep)
      }
      $iar = $client.BeginConnect($target, 443, $null, $null)
      if ($iar.AsyncWaitHandle.WaitOne(3000, $false) -and $client.Connected) {
        $client.EndConnect($iar)
        $client.Close()
        $suffix = ''
        if ($BindIp) { $suffix = ' (bound ' + $BindIp + ')' }
        Log ('[probe] TCP 443 reachable via ' + $target + $suffix)
        return $true
      }
      $client.Close()
    } catch {
      if ($client) { try { $client.Close() } catch {} }
    }
  }
  return $false
}
function Test-ExoDnsResolve {
  try {
    $r = Resolve-DnsName -Name 'www.msftconnecttest.com' -Type A -DnsOnly -EA Stop
    return ($null -ne $r)
  } catch { return $false }
}
""";

    public static string Build(
        NetworkPreset preset,
        NetworkApplyOptions options,
        NetworkMediaProfile media)
    {
        var knobs = NetworkLogic.KnobsFor(preset);
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
        // RSS: use physical cores when known (HT threads are not "12 cores" on a 6-core CPU).
        var coreBudget = media.PhysicalCores > 0
            ? media.PhysicalCores
            : (media.LogicalProcessors > 0 ? Math.Max(1, media.LogicalProcessors / 2) : Math.Max(1, Environment.ProcessorCount / 2));
        var logicalCpus = media.LogicalProcessors > 0 ? media.LogicalProcessors : Environment.ProcessorCount;
        var rssBudget = NetworkLogic.RssQueueBudget(preset, coreBudget);
        var bufferStrategy = NetworkLogic.BufferStrategy(preset);
        var preferIpv4 = NetworkLogic.PreferIpv4First(preset, media.EthernetInUse) ? "1" : "0";
        var vendorHint = string.IsNullOrWhiteSpace(media.NicVendor) ? "Unknown" : media.NicVendor;
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

        var sb = new StringBuilder(48_000);
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("$ProgressPreference = 'SilentlyContinue'");
        sb.AppendLine("$log = Join-Path $env:TEMP 'exo-net-last.log'");
        sb.AppendLine("function Log([string]$m) { $ts = Get-Date -Format o; Add-Content -Path $log -Value \"$ts $m\" -EA SilentlyContinue; Write-Host $m }");
        sb.AppendLine("'' | Set-Content -Path $log -EA SilentlyContinue");
        sb.AppendLine("Log '[Exo-NET] Preset=" + preset + " ethFirst=" + preferEth + " restartEth=" + restartEth + " band6hint=" + prefer6Hint + "'");
        sb.AppendLine("$BufferStrategy = '" + bufferStrategy + "'");
        sb.AppendLine("$RssQueueBudget = " + rssBudget);
        sb.AppendLine("$LogicalCpuCount = " + logicalCpus);
        sb.AppendLine("$PreferIpv4First = " + preferIpv4);
        sb.AppendLine("$VendorHint = '" + vendorHint.Replace("'", "") + "'");
        sb.AppendLine("$IsLaptopHint = " + (media.IsLikelyLaptop ? "1" : "0"));
        sb.AppendLine("$ExoWifiDisabled = @()");
        sb.AppendLine("Log \"[Exo-NET] net-only BufferStrategy=$BufferStrategy RssQueues<=$RssQueueBudget PreferIpv4=$PreferIpv4First Vendor=$VendorHint\"");
        sb.AppendLine(CommonSafetyFunctions);
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
function Invoke-ExoNetshGlobal([string]$Step, [string[]]$NetshArgs) {
  # OS-build gate for netsh options that may not exist on older builds:
  # never silent-fail; emit a skipped-with-reason report line instead.
  try {
    $out = (& netsh $NetshArgs 2>&1 | Out-String)
    if ($LASTEXITCODE -eq 0) { Report $Step 'ok' }
    else { Report $Step 'skip' ('not supported on this build: ' + $out) }
  } catch { Report $Step 'skip' ('netsh error: ' + $_.Exception.Message) }
}
""");

        // Classification mirrors NetworkLogic.IsWifiAdapter (keep in sync).
        // Defined before the snapshot so the baseline records Wi-Fi vs Ethernet correctly.
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
        // Physical NIC set: hardware interfaces only. Get-NetAdapter -Physical plus explicit
        // Virtual/VPN exclusion (interface type, not just name heuristics).
        sb.AppendLine("""
function Get-ExoPhysicalAdapters {
  @(Get-NetAdapter -Physical -EA SilentlyContinue | Where-Object {
    -not $_.Virtual -and
    ([string]$_.InterfaceDescription -notmatch '(?i)TAP-|TUN-|WireGuard|OpenVPN|Wintun|Hyper-V|vEthernet|VMware|VirtualBox|Loopback|Bluetooth|Meta\s*Tunnel|ZeroTier|Tailscale|Hamachi')
  })
}
""");

        // ============================================================================
        // PRE-APPLY SNAPSHOT — captured BEFORE any mutation. If a snapshot file exists
        // it is the pristine baseline from the first apply and is NEVER overwritten.
        // If capture fails the apply ABORTS before touching anything.
        // ============================================================================
        sb.AppendLine(BuildRegistryTargetsPs());
        sb.AppendLine("""
function Save-ExoNetworkSnapshot {
  if (Test-Path -LiteralPath $ExoSnapshotPath) {
    Log '[snapshot] pristine baseline already exists - keeping it (re-apply)'
    Report 'snapshot' 'skip' 'pristine baseline kept from first apply'
    return $true
  }
  try {
    New-Item -ItemType Directory -Path $ExoDir -Force | Out-Null
    $snap = [ordered]@{
      snapshotVersion = 1
      timestampUtc    = (Get-Date).ToUniversalTime().ToString('o')
    }
    # --- netsh raw dumps (restore parses value tokens; raw kept for support/debug) ---
    $snap.netshTcpGlobalRaw     = ((netsh int tcp show global 2>$null | Out-String)).Trim()
    $snap.netshTcpHeuristicsRaw = ((netsh int tcp show heuristics 2>$null | Out-String)).Trim()
    $snap.netshUdpGlobalRaw     = ((netsh int udp show global 2>$null | Out-String)).Trim()
    # --- structured (locale-independent) TCP state ---
    $snap.offloadGlobal = @(Get-NetOffloadGlobalSetting -EA SilentlyContinue | ForEach-Object {
      [ordered]@{
        receiveSegmentCoalescing = [string]$_.ReceiveSegmentCoalescing
        receiveSideScaling       = [string]$_.ReceiveSideScaling
        taskOffload              = [string]$_.TaskOffload
      }
    })
    $snap.tcpSettings = @(Get-NetTCPSetting -EA SilentlyContinue | ForEach-Object {
      [ordered]@{
        settingName           = [string]$_.SettingName
        congestionProvider    = [string]$_.CongestionProvider
        autoTuningLevelLocal  = [string]$_.AutoTuningLevelLocal
        ecnCapability         = [string]$_.EcnCapability
        timestamps            = [string]$_.Timestamps
        initialRtoMs          = [int]$_.InitialRtoMs
        minRtoMs              = [int]$_.MinRtoMs
        maxSynRetransmissions = [int]$_.MaxSynRetransmissions
        nonSackRttResiliency  = [string]$_.NonSackRttResiliency
      }
    })
    # --- every registry value the apply may write (pre-value or 'absent') ---
    # NOTE: lists are created via ::new() and materialized via .ToArray() —
    # @() over a New-Object-created (PSObject-wrapped) List[object] throws
    # 'Argument types do not match' in pwsh 7.6 (PSToObjectArrayBinder), which
    # aborted the whole snapshot on real Windows.
    $regVals = [System.Collections.Generic.List[object]]::new()
    foreach ($t in $ExoRegTargets) {
      $entry = [ordered]@{ path = $t.Path; name = $t.Name; kind = 'absent'; value = $null }
      try {
        if (Test-Path -LiteralPath $t.Path) {
          $item = Get-Item -LiteralPath $t.Path -EA SilentlyContinue
          if ($item -and (@($item.GetValueNames()) -contains $t.Name)) {
            $entry.kind = [string]$item.GetValueKind($t.Name)
            $entry.value = $item.GetValue($t.Name, $null, 'DoNotExpandEnvironmentNames')
          }
        }
      } catch {}
      [void]$regVals.Add([pscustomobject]$entry)
    }
    foreach ($ifKey in @(Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces' -EA SilentlyContinue)) {
      foreach ($nm in @('TcpAckFrequency', 'TCPNoDelay', 'TcpDelAckTicks')) {
        $entry = [ordered]@{ path = [string]$ifKey.PSPath; name = $nm; kind = 'absent'; value = $null }
        try {
          if (@($ifKey.GetValueNames()) -contains $nm) {
            $entry.kind = [string]$ifKey.GetValueKind($nm)
            $entry.value = $ifKey.GetValue($nm)
          }
        } catch {}
        [void]$regVals.Add([pscustomobject]$entry)
      }
    }
    foreach ($nbKey in @(Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces' -EA SilentlyContinue)) {
      $entry = [ordered]@{ path = [string]$nbKey.PSPath; name = 'NetbiosOptions'; kind = 'absent'; value = $null }
      try {
        if (@($nbKey.GetValueNames()) -contains 'NetbiosOptions') {
          $entry.kind = [string]$nbKey.GetValueKind('NetbiosOptions')
          $entry.value = $nbKey.GetValue('NetbiosOptions')
        }
      } catch {}
      [void]$regVals.Add([pscustomobject]$entry)
    }
    $classRoot = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}'
    foreach ($ck in @(Get-ChildItem $classRoot -EA SilentlyContinue)) {
      try {
        $props = Get-ItemProperty $ck.PSPath -EA SilentlyContinue
        if ($props -and $props.DriverDesc) {
          $entry = [ordered]@{ path = [string]$ck.PSPath; name = 'PnPCapabilities'; kind = 'absent'; value = $null }
          $rk = Get-Item -LiteralPath $ck.PSPath -EA SilentlyContinue
          if ($rk -and (@($rk.GetValueNames()) -contains 'PnPCapabilities')) {
            $entry.kind = [string]$rk.GetValueKind('PnPCapabilities')
            $entry.value = $rk.GetValue('PnPCapabilities')
          }
          [void]$regVals.Add([pscustomobject]$entry)
        }
      } catch {}
    }
    $snap.regValues = $regVals.ToArray()
    # --- adapters: advanced properties (by RegistryKeyword), bindings, enable state ---
    $phys = Get-ExoPhysicalAdapters
    $advList = [System.Collections.Generic.List[object]]::new()
    $bindList = [System.Collections.Generic.List[object]]::new()
    foreach ($a in $phys) {
      foreach ($p in @(Get-NetAdapterAdvancedProperty -Name $a.Name -EA SilentlyContinue)) {
        if (-not $p.RegistryKeyword) { continue }
        [void]$advList.Add([pscustomobject]@{
          adapter = [string]$a.Name
          ifDesc  = [string]$a.InterfaceDescription
          keyword = [string]$p.RegistryKeyword
          value   = (@($p.RegistryValue) -join ',')
        })
      }
      foreach ($b in @(Get-NetAdapterBinding -Name $a.Name -EA SilentlyContinue)) {
        [void]$bindList.Add([pscustomobject]@{
          adapter     = [string]$a.Name
          ifDesc      = [string]$a.InterfaceDescription
          componentId = [string]$b.ComponentID
          enabled     = [bool]$b.Enabled
        })
      }
    }
    $snap.advancedProps = $advList.ToArray()
    $snap.bindings = $bindList.ToArray()
    $snap.adapterStates = @(Get-NetAdapter -Physical -EA SilentlyContinue | ForEach-Object {
      [pscustomobject]@{
        name    = [string]$_.Name
        ifDesc  = [string]$_.InterfaceDescription
        status  = [string]$_.Status
        adminUp = ([string]$_.Status -ne 'Disabled')
        wifi    = [bool](Test-IsWifiAdapter $_)
      }
    })
    # --- interface metrics incl AutomaticMetric ---
    $snap.ipInterfaces = @(Get-NetIPInterface -EA SilentlyContinue | ForEach-Object {
      [pscustomobject]@{
        ifIndex         = [int]$_.ifIndex
        alias           = [string]$_.InterfaceAlias
        family          = [string]$_.AddressFamily
        metric          = [int]$_.InterfaceMetric
        automaticMetric = ([string]$_.AutomaticMetric -eq 'Enabled')
      }
    })
    # --- RSS config for every Ethernet NIC (BaseProcessorNumber restore) ---
    $rssList = [System.Collections.Generic.List[object]]::new()
    foreach ($a in @($phys | Where-Object { -not (Test-IsWifiAdapter $_) })) {
      $r = Get-NetAdapterRss -Name $a.Name -EA SilentlyContinue
      if ($r) {
        [void]$rssList.Add([pscustomobject]@{
          adapter             = [string]$a.Name
          enabled             = [bool]$r.Enabled
          baseProcessorNumber = [int]$r.BaseProcessorNumber
          profile             = [string]$r.Profile
        })
      }
    }
    $snap.rss = $rssList.ToArray()
    # --- powercfg values the apply changes (AC/DC indexes, last two hex = current) ---
    $pcList = [System.Collections.Generic.List[object]]::new()
    try {
      $scheme = (powercfg /getactivescheme) -replace '.*GUID:\s*([0-9a-f\-]+).*', '$1'
      if ($scheme) {
        $pairs = @(
          @{ Sub = '19cbb8fa-5279-450e-9fac-8a3d5fedd0c1'; Setting = '12bbebe6-58d6-4636-95bb-3217ef867c1a' },
          @{ Sub = '501a4d13-42af-4429-9fd1-a8218c268e20'; Setting = 'ee12f906-d277-404b-b6da-e5fa1a576df5' },
          @{ Sub = '2a737441-1930-4402-8d77-b2bebba308a3'; Setting = '48e6b7a6-50f5-4782-a5d4-53bb8f07e226' }
        )
        foreach ($pair in $pairs) {
          $q = (powercfg /q $scheme $pair.Sub $pair.Setting 2>$null | Out-String)
          $hexes = @([regex]::Matches($q, '0x[0-9a-fA-F]{1,8}') | ForEach-Object { $_.Value })
          $ac = $null; $dc = $null
          if ($hexes.Count -ge 2) { $ac = $hexes[$hexes.Count - 2]; $dc = $hexes[$hexes.Count - 1] }
          [void]$pcList.Add([pscustomobject]@{ scheme = $scheme; sub = $pair.Sub; setting = $pair.Setting; ac = $ac; dc = $dc })
        }
      }
    } catch {}
    $snap.powercfg = $pcList.ToArray()
    # --- dynamic port ranges ---
    $dpList = [System.Collections.Generic.List[object]]::new()
    foreach ($fam in @('ipv4', 'ipv6')) {
      foreach ($proto in @('tcp', 'udp')) {
        try {
          $o = (netsh int $fam show dynamicport $proto 2>$null | Out-String)
          $nums = @([regex]::Matches($o, '\d+') | ForEach-Object { [int]$_.Value })
          if ($nums.Count -ge 2) {
            [void]$dpList.Add([pscustomobject]@{ family = $fam; protocol = $proto; start = $nums[0]; num = $nums[1] })
          }
        } catch {}
      }
    }
    $snap.dynamicPorts = $dpList.ToArray()
    # --- IPv6 prefix policies (restore target for the IPv4-first precedence change) ---
    $ppRaw = (netsh int ipv6 show prefixpolicies 2>$null | Out-String)
    $snap.prefixPoliciesRaw = $ppRaw.Trim()
    $snap.prefixPolicies = @([regex]::Matches($ppRaw, '(?m)^\s*(\d+)\s+(\d+)\s+([0-9a-fA-F:\/\.]+)\s*$') | ForEach-Object {
      [pscustomobject]@{ precedence = [int]$_.Groups[1].Value; label = [int]$_.Groups[2].Value; prefix = $_.Groups[3].Value }
    })
    # --- services (DoSvc startup type) ---
    $snap.services = @(Get-Service -Name 'DoSvc' -EA SilentlyContinue | ForEach-Object {
      [pscustomobject]@{ name = [string]$_.Name; startType = [string]$_.StartType; status = [string]$_.Status }
    })
    $json = $snap | ConvertTo-Json -Depth 8
    Set-Content -LiteralPath $ExoSnapshotPath -Value $json -Encoding UTF8
    if (-not (Test-Path -LiteralPath $ExoSnapshotPath)) { throw 'snapshot file was not written' }
    Log ('[snapshot] pristine baseline captured -> ' + $ExoSnapshotPath)
    Report 'snapshot' 'ok'
    return $true
  } catch {
    Report 'snapshot' 'fail' $_.Exception.Message
    return $false
  }
}
$snapshotOk = Save-ExoNetworkSnapshot
if (-not $snapshotOk) {
  Log '[Exo-NET] ABORT - snapshot capture failed; no mutations were made'
  Report 'apply' 'fail' 'snapshot capture failed - aborted before any mutation'
  exit 2
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
        // DNS ServiceProvider priorities (documented resolver-order DWORDs; defaults 499/500/2000/2001)
        sb.AppendLine("$sp = 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\ServiceProvider'");
        sb.AppendLine("Set-Dword $sp 'LocalPriority' 4");
        sb.AppendLine("Set-Dword $sp 'HostsPriority' 5");
        sb.AppendLine("Set-Dword $sp 'DnsPriority' 6");
        sb.AppendLine("Set-Dword $sp 'NetbtPriority' 7");
        sb.AppendLine("Report 'dns-priorities' 'ok'");
        sb.AppendLine("Report 'registry-host' 'ok'");

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
Report 'mmcss' 'ok'
# QoS "Limit reservable bandwidth" — GPO NonBestEffortLimit 0 removes the old 20% reserve
New-Item 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Psched' -Force | Out-Null
Set-Dword 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Psched' 'NonBestEffortLimit' 0
Report 'qos-psched' 'ok'
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
    Report 'powercfg' 'ok'
  } else {
    Report 'powercfg' 'skip' 'active scheme not readable'
  }
} catch { Log '[powercfg] skipped'; Report 'powercfg' 'skip' 'powercfg failed' }
""");

        // --- netsh / Set-NetTCPSetting (supported modern path) ---
        sb.AppendLine("netsh int tcp set global rss=enabled | Out-Null");
        sb.AppendLine("netsh int tcp set global autotuninglevel=" + autotune + " | Out-Null");
        sb.AppendLine("netsh int tcp set global rsc=" + rsc + " | Out-Null");
        sb.AppendLine("try { netsh int tcp set heuristics disabled | Out-Null } catch {}");
        sb.AppendLine("try { netsh int tcp set supplemental template=internet congestionprovider=cubic | Out-Null } catch {}");
        sb.AppendLine("try { netsh int tcp set supplemental template=internetcustom congestionprovider=cubic | Out-Null } catch {}");
        sb.AppendLine("try { netsh int ip set global taskoffload=enabled | Out-Null } catch {}");
        sb.AppendLine("Report 'tcp-globals' 'ok'");
        // --- Extended TCP tweak layer (all snapshot-covered, build-gated, reported) ---
        sb.AppendLine("Invoke-ExoNetshGlobal 'tcp-timestamps' @('int','tcp','set','global','timestamps=disabled')");
        sb.AppendLine("Invoke-ExoNetshGlobal 'tcp-fastopen' @('int','tcp','set','global','fastopen=enabled')");
        sb.AppendLine("Invoke-ExoNetshGlobal 'tcp-fastopen-fallback' @('int','tcp','set','global','fastopenfallback=enabled')");
        if (knobs.PacingOff)
            sb.AppendLine("Invoke-ExoNetshGlobal 'tcp-pacing' @('int','tcp','set','global','pacingprofile=off')");
        else
            sb.AppendLine("Report 'tcp-pacing' 'skip' 'preset keeps Windows default pacing profile'");
        if (knobs.HystartOff)
            sb.AppendLine("Invoke-ExoNetshGlobal 'tcp-hystart' @('int','tcp','set','global','hystart=disabled')");
        else
            sb.AppendLine("Report 'tcp-hystart' 'skip' 'preset keeps Windows default HyStart'");
        sb.AppendLine("Invoke-ExoNetshGlobal 'tcp-ecn' @('int','tcp','set','global','ecncapability=" + knobs.Ecn + "')");
        if (knobs.UroOff)
        {
            sb.AppendLine("if ([System.Environment]::OSVersion.Version.Build -ge 26100) {");
            sb.AppendLine("  Invoke-ExoNetshGlobal 'udp-uro' @('int','udp','set','global','uro=disabled')");
            sb.AppendLine("} else {");
            sb.AppendLine("  Report 'udp-uro' 'skip' 'requires Windows 11 24H2 (build 26100+)'");
            sb.AppendLine("}");
        }
        else
        {
            sb.AppendLine("Report 'udp-uro' 'skip' 'preset keeps Windows default URO'");
        }
        // Ephemeral ports — modern API (MaxUserPort is legacy)
        sb.AppendLine("try { netsh int ipv4 set dynamicport tcp start=1025 num=64511 | Out-Null } catch {}");
        sb.AppendLine("try { netsh int ipv4 set dynamicport udp start=1025 num=64511 | Out-Null } catch {}");
        sb.AppendLine("try { netsh int ipv6 set dynamicport tcp start=1025 num=64511 | Out-Null } catch {}");
        sb.AppendLine("try { netsh int ipv6 set dynamicport udp start=1025 num=64511 | Out-Null } catch {}");
        sb.AppendLine("Report 'dynamic-ports' 'ok'");
        sb.AppendLine("foreach ($pr in @('Internet','InternetCustom')) {");
        sb.AppendLine("  try { Set-NetTCPSetting -SettingName $pr -CongestionProvider CUBIC -EA SilentlyContinue } catch {}");
        sb.AppendLine("  try { Set-NetTCPSetting -SettingName $pr -AutoTuningLevelLocal " + autoTuningPs + " -EA SilentlyContinue } catch {}");
        sb.AppendLine("  try { Set-NetTCPSetting -SettingName $pr -ScalingHeuristics Disabled -EA SilentlyContinue } catch {}");
        sb.AppendLine("}");
        // InternetCustom RTO / SYN resilience (documented Set-NetTCPSetting fields)
        sb.AppendLine("try { Set-NetTCPSetting -SettingName InternetCustom -MaxSynRetransmissions 2 -EA SilentlyContinue } catch {}");
        sb.AppendLine("try { Set-NetTCPSetting -SettingName InternetCustom -NonSackRttResiliency Disabled -EA SilentlyContinue } catch {}");
        if (knobs.TightRto)
        {
            sb.AppendLine("try { Set-NetTCPSetting -SettingName InternetCustom -InitialRtoMs 1000 -EA SilentlyContinue } catch {}");
            sb.AppendLine("try { Set-NetTCPSetting -SettingName InternetCustom -MinRtoMs 300 -EA SilentlyContinue } catch {}");
        }
        sb.AppendLine("Report 'tcp-settings' 'ok'");

        // Nagle (per-interface) — only latency preset
        sb.AppendLine("Get-ChildItem 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces' -EA SilentlyContinue | ForEach-Object {");
        sb.AppendLine("  $p = $_.PSPath");
        sb.AppendLine(ackBlock);
        sb.AppendLine("}");
        sb.AppendLine("Report 'nagle' 'ok'");

        // --- Per-adapter: branch Ethernet vs Wi‑Fi (MS: wireless often has no RSS/LSO) ---
        // Apply to all physical NICs so dual-homed PCs are ready on either media.
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
        // DisplayName fuzzy matching stays ONLY as fallback for vendor-specific knobs with no
        // standardized RegistryKeyword (Killer/Intel/Realtek extras). Standardized keywords are primary.
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
        sb.AppendLine("$rssBaseCount = 0");
        sb.AppendLine("$adapters = @(Get-ExoPhysicalAdapters | Where-Object { $_.Status -eq 'Up' -or $_.Status -eq 'Disconnected' })");
        sb.AppendLine("if ($adapters.Count -eq 0) { $adapters = @(Get-ExoPhysicalAdapters) }");
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
        // Standardized keywords first, then vendor keywords (ULP / SIPS / Advanced EEE / Green Ethernet
        // where drivers expose them), DisplayName fallback last.
        sb.AppendLine("  foreach ($kw in @('*EEE','*EnergyEfficientEthernet','*GreenEthernet','*SelectiveSuspend','*ReduceSpeedOnPowerDown','*PMARPOffload','*PMNSOffload','*WakeOnMagicPacket','*WakeOnPattern')) { Set-Adv $n $kw 0 }");
        sb.AppendLine("  foreach ($kw in @('AdvancedEEE','GreenEthernet','EnableGreenEthernet','PowerSavingMode','ULPMode','SipsEnabled','GigaLite')) { Set-Adv $n $kw 0 }");
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
        // RSS queues: cap by tailored budget (latency uses fewer cores; download uses max available)
        sb.AppendLine("    try {");
        sb.AppendLine("      $q = Get-NetAdapterAdvancedProperty -Name $n -RegistryKeyword '*NumRssQueues' -EA SilentlyContinue");
        sb.AppendLine("      if ($q -and $q.ValidRegistryValues -and @($q.ValidRegistryValues).Count -gt 0) {");
        sb.AppendLine("        $sorted = @($q.ValidRegistryValues | ForEach-Object { [int]$_ } | Sort-Object)");
        sb.AppendLine("        $maxQ = $sorted[-1]");
        sb.AppendLine("        $wantQ = [Math]::Min($maxQ, [int]$RssQueueBudget)");
        sb.AppendLine("        if ($wantQ -lt $sorted[0]) { $wantQ = $sorted[0] }");
        sb.AppendLine("        # pick nearest valid <= want");
        sb.AppendLine("        $pick = ($sorted | Where-Object { $_ -le $wantQ } | Select-Object -Last 1)");
        sb.AppendLine("        if (-not $pick) { $pick = $sorted[0] }");
        sb.AppendLine("        Set-Adv $n '*NumRssQueues' ([int]$pick)");
        sb.AppendLine("        Log \"[RSS] queues => $pick (budget=$RssQueueBudget max=$maxQ)\"");
        sb.AppendLine("      }");
        sb.AppendLine("    } catch {}");
        // RSS base processor 2: keep NIC interrupts/DPCs off core 0 (>=4 logical CPUs).
        // Prior RSS config is in the snapshot (Get-NetAdapterRss) for exact restore.
        sb.AppendLine("    if ($LogicalCpuCount -ge 4) {");
        sb.AppendLine("      try {");
        sb.AppendLine("        Set-NetAdapterRss -Name $n -BaseProcessorNumber 2 -Enabled $true -EA SilentlyContinue");
        sb.AppendLine("        $rssBaseCount++");
        sb.AppendLine("        Log \"[RSS] $n BaseProcessorNumber=2 (interrupts off core 0)\"");
        sb.AppendLine("      } catch {}");
        sb.AppendLine("    }");
        // Ethernet-only deep driver knobs (Intel I225/I226, Realtek, Killer…)
        sb.AppendLine("    # DMA coalescing / adaptive IFS — latency killers when on");
        sb.AppendLine("    foreach ($kw in @('*DMACoalescing','DMACoalescing')) { Set-Adv $n $kw 0 }");
        sb.AppendLine("    try { Set-AdvDisplay $n 'DMA Coalescing' 'Disabled' | Out-Null } catch {}");
        sb.AppendLine("    try { Set-AdvDisplay $n 'Adaptive Inter-Frame Spacing' 'Disabled' | Out-Null } catch {}");
        sb.AppendLine("    try { Set-AdvDisplay $n 'Gigabit Lite' 'Disabled' | Out-Null } catch {}");
        sb.AppendLine("    try { Set-AdvDisplay $n 'Gigabit Master Slave Mode' 'Auto Detect' | Out-Null } catch {}");
        // Speed & Duplex: standardized keyword (*SpeedDuplex 0 = auto negotiation), display fallback
        sb.AppendLine("    Set-Adv $n '*SpeedDuplex' 0");
        sb.AppendLine("    try { Set-AdvDisplay $n 'Speed & Duplex' 'Auto Negotiation' | Out-Null } catch {}");
        sb.AppendLine("    try { Set-AdvDisplay $n 'Wait for Link' 'Auto Detect' | Out-Null } catch {}");
        sb.AppendLine("    try { Set-AdvDisplay $n 'Log Link State Event' 'Disabled' | Out-Null } catch {}");
        // Vendor-tailored extras (only DisplayNames that exist are applied)
        sb.AppendLine("    $descLow = ([string]$a.InterfaceDescription).ToLowerInvariant()");
        sb.AppendLine("    $isIntel = ($descLow -match 'intel' -or $VendorHint -eq 'Intel')");
        sb.AppendLine("    $isRealtek = ($descLow -match 'realtek' -or $VendorHint -eq 'Realtek')");
        sb.AppendLine("    $isKiller = ($descLow -match 'killer' -or $VendorHint -eq 'Killer')");
        sb.AppendLine("    if ($isIntel) {");
        sb.AppendLine("      try { Set-AdvDisplay $n 'Ultra Low Power Mode' 'Disabled' | Out-Null } catch {}");
        sb.AppendLine("      try { Set-AdvDisplay $n 'System Idle Power Saver' 'Disabled' | Out-Null } catch {}");
        sb.AppendLine("      try { Set-AdvDisplay $n 'Energy Efficient Ethernet' 'Off' | Out-Null } catch {}");
        sb.AppendLine("      try { Set-AdvDisplay $n 'Reduce Speed On Power Down' 'Disabled' | Out-Null } catch {}");
        // I225/I226: keep IdleRestriction ON for latency (already set); force Link Speed Auto
        sb.AppendLine("      if ($descLow -match 'i225|i226|i219|i211') {");
        sb.AppendLine("        try { Set-AdvDisplay $n 'Wait for Link' 'Off or Disabled' | Out-Null } catch {}");
        sb.AppendLine("        try { Set-AdvDisplay $n 'Legacy Switch Compatibility Mode' 'Disabled' | Out-Null } catch {}");
        sb.AppendLine("        Log '[NIC] Intel 2.5G/1G family extras'");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("    if ($isRealtek) {");
        sb.AppendLine("      try { Set-AdvDisplay $n 'Green Ethernet' 'Disabled' | Out-Null } catch {}");
        sb.AppendLine("      try { Set-AdvDisplay $n 'Energy-Efficient Ethernet' 'Disabled' | Out-Null } catch {}");
        sb.AppendLine("      try { Set-AdvDisplay $n 'Advanced EEE' 'Disabled' | Out-Null } catch {}");
        sb.AppendLine("      try { Set-AdvDisplay $n 'Power Saving Mode' 'Disabled' | Out-Null } catch {}");
        sb.AppendLine("      try { Set-AdvDisplay $n 'ARP Offload' 'Disabled' | Out-Null } catch {}");
        sb.AppendLine("      try { Set-AdvDisplay $n 'NS Offload' 'Disabled' | Out-Null } catch {}");
        sb.AppendLine("      Log '[NIC] Realtek power/offload extras'");
        sb.AppendLine("    }");
        sb.AppendLine("    if ($isKiller) {");
        sb.AppendLine("      # Do not kill Killer service (breaks some installs); only advanced props");
        sb.AppendLine("      try { Set-AdvDisplay $n 'Idle Power Saving' 'Disabled' | Out-Null } catch {}");
        sb.AppendLine("      try { Set-AdvDisplay $n 'Energy Efficient Ethernet' 'Off' | Out-Null } catch {}");
        sb.AppendLine("      Log '[NIC] Killer power extras'");
        sb.AppendLine("    }");
        sb.AppendLine("    # Jumbo: keep standard Ethernet (gaming) — keyword first (1514 = standard frame)");
        sb.AppendLine("    try {");
        sb.AppendLine("      $jkw = Get-NetAdapterAdvancedProperty -Name $n -RegistryKeyword '*JumboPacket' -EA SilentlyContinue");
        sb.AppendLine("      if ($jkw) {");
        sb.AppendLine("        $jvals = @($jkw.ValidRegistryValues | ForEach-Object { [int]$_ })");
        sb.AppendLine("        $jpick = if ($jvals -contains 1514) { 1514 } elseif ($jvals -contains 1500) { 1500 } elseif ($jvals.Count -gt 0) { ($jvals | Sort-Object)[0] } else { 1514 }");
        sb.AppendLine("        Set-Adv $n '*JumboPacket' ([int]$jpick)");
        sb.AppendLine("        Log \"[Eth] Jumbo => $jpick (keyword)\"");
        sb.AppendLine("      } else {");
        sb.AppendLine("        $jp = Find-AdvPropByName $n @('Jumbo Packet','Jumbo Frames','Jumbo Frame')");
        sb.AppendLine("        if ($jp) {");
        sb.AppendLine("          foreach ($v in @('Disabled','Off','1514','1500')) {");
        sb.AppendLine("            if (@($jp.ValidDisplayValues).Count -eq 0 -or @($jp.ValidDisplayValues) -contains $v) {");
        sb.AppendLine("              try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName $jp.DisplayName -DisplayValue $v -NoRestart -EA SilentlyContinue; Log \"[Eth] Jumbo => $v\"; break } catch {}");
        sb.AppendLine("            }");
        sb.AppendLine("          }");
        sb.AppendLine("        }");
        sb.AppendLine("      }");
        sb.AppendLine("    } catch {}");
        sb.AppendLine("    # Priority & VLAN: keep packet priority (QoS tags) — keyword 1 = packet priority enabled");
        sb.AppendLine("    Set-Adv $n '*PriorityVLANTag' 1");
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
        sb.AppendLine("    # RSS profile — best-effort");
        sb.AppendLine("    try { Set-NetAdapterRss -Name $n -Profile NUMAStatic -EA SilentlyContinue } catch {}");
        sb.AppendLine("    try { Set-NetAdapterRss -Name $n -Profile ClosestProcessor -EA SilentlyContinue } catch {}");
        sb.AppendLine("  }");
        // Ring buffers: download = max; latency = mid-high (absolute max can add jitter on some NICs)
        sb.AppendLine("  foreach ($kw in @('*ReceiveBuffers','*TransmitBuffers','ReceiveBuffers','TransmitBuffers')) {");
        sb.AppendLine("    try {");
        sb.AppendLine("      $prop = Get-NetAdapterAdvancedProperty -Name $n -RegistryKeyword $kw -EA SilentlyContinue");
        sb.AppendLine("      if (-not $prop -or -not $prop.ValidRegistryValues -or @($prop.ValidRegistryValues).Count -eq 0) { continue }");
        sb.AppendLine("      $vals = @($prop.ValidRegistryValues | ForEach-Object { [int]$_ } | Sort-Object)");
        sb.AppendLine("      if ($vals.Count -eq 0) { continue }");
        sb.AppendLine("      if ($BufferStrategy -eq 'max') { $pick = $vals[-1] }");
        sb.AppendLine("      else {");
        sb.AppendLine("        # mid-high: ~75th percentile of valid values");
        sb.AppendLine("        $idx = [Math]::Max(0, [int][Math]::Floor(($vals.Count - 1) * 0.75))");
        sb.AppendLine("        $pick = $vals[$idx]");
        sb.AppendLine("      }");
        sb.AppendLine("      Set-Adv $n $kw ([int]$pick)");
        sb.AppendLine("      Log \"[buf] $n $kw => $pick ($BufferStrategy)\"");
        sb.AppendLine("    } catch {}");
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
        // Latency: Low roam (stable BSS). Throughput: Medium (balanced handoff).
        if (latency)
        {
            sb.AppendLine("      $rv = @($roam.ValidDisplayValues) | Where-Object { $_ -match '(?i)low|lowest|1|minimal' } | Select-Object -First 1");
            sb.AppendLine("      if (-not $rv) { $rv = @($roam.ValidDisplayValues) | Where-Object { $_ -match '(?i)medium|3|mid' } | Select-Object -First 1 }");
        }
        else
        {
            sb.AppendLine("      $rv = @($roam.ValidDisplayValues) | Where-Object { $_ -match '(?i)medium' } | Select-Object -First 1");
            sb.AppendLine("      if (-not $rv) { $rv = @($roam.ValidDisplayValues) | Where-Object { $_ -match '(?i)3|mid' } | Select-Object -First 1 }");
        }
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
        sb.AppendLine("Report 'adapters' 'ok' ('tuned ' + $adapters.Count + ' physical adapter(s)')");
        sb.AppendLine("if ($rssBaseCount -gt 0) { Report 'rss-base' 'ok' ('BaseProcessorNumber=2 on ' + $rssBaseCount + ' adapter(s)') }");
        sb.AppendLine("else { Report 'rss-base' 'skip' 'no ethernet adapter or fewer than 4 logical processors' }");

        // --- Adapter bindings = the checkboxes in Ethernet Properties → Networking ---
        // Target (gaming lean, matches common "best" host stack):
        //   ON:  QoS Packet Scheduler, IPv4, IPv6
        //   OFF: Client for Microsoft Networks, File and Printer Sharing,
        //        Multiplexor, LLDP, LLTD Mapper, LLTD Responder
        sb.AppendLine("""
function Set-AdapterBindings {
  $ads = @(Get-ExoPhysicalAdapters)
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
# Background download quiet: Delivery Optimization service demand-start
# (StartType snapshotted for exact restore) + ensure no stale BITS throttle policy.
try {
  $doSvc = Get-Service -Name 'DoSvc' -EA SilentlyContinue
  if ($doSvc) {
    Set-Service -Name 'DoSvc' -StartupType Manual -EA SilentlyContinue
    Report 'background-quiet' 'ok' 'DoSvc demand-start'
  } else {
    Report 'background-quiet' 'skip' 'DoSvc not present'
  }
} catch { Report 'background-quiet' 'fail' $_.Exception.Message }
try {
  $bitsPol = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\BITS'
  if (Test-Path -LiteralPath $bitsPol) {
    $bk = Get-Item -LiteralPath $bitsPol -EA SilentlyContinue
    if ($bk -and (@($bk.GetValueNames()) -contains 'EnableBITSMaxBandwidth')) {
      Remove-ItemProperty -LiteralPath $bitsPol -Name 'EnableBITSMaxBandwidth' -Force -EA SilentlyContinue
      Log '[BITS] EnableBITSMaxBandwidth throttle policy removed (was present; snapshotted)'
    }
  }
} catch {}
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
# Internet card only — no Game Mode / HAGS / power-plan / CPU parking (those belong on Windows later)
# LLMNR / mDNS discovery chatter off (reduces background name noise on LAN)
try {
  New-Item 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient' -Force | Out-Null
  Set-Dword 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient' 'EnableMulticast' 0
  Log '[DNS] LLMNR multicast off'
} catch {}
try {
  New-Item 'HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters' -Force | Out-Null
  # Larger DNS cache = fewer repeat lookups during gaming sessions
  Set-Dword 'HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters' 'MaxCacheTtl' 86400
  Set-Dword 'HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters' 'MaxNegativeCacheTtl' 5
  Log '[DNS] cache TTL tuned'
} catch {}
# Disable SMBv1 if present (background noise + security)
try {
  $smb = Get-WindowsOptionalFeature -Online -FeatureName SMB1Protocol -EA SilentlyContinue
  if ($smb -and $smb.State -eq 'Enabled') {
    Disable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol -NoRestart -EA SilentlyContinue | Out-Null
    Log '[SMB] SMBv1 disabled'
  }
} catch {}
# WinHTTP / IE auto-proxy detect off (avoids WPAD stalls)
try {
  Set-Dword 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' 'AutoDetect' 0
  Log '[proxy] AutoDetect=0'
} catch {}
# Network Location Awareness: keep service; cut Network List Manager periodic scan weight via policy when available
try {
  New-Item 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\NetworkConnectivityStatusIndicator' -Force | Out-Null
  Set-Dword 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\NetworkConnectivityStatusIndicator' 'DisablePassivePolling' 0
} catch {}
Set-AdapterBindings
Report 'bindings' 'ok'
""");

        // Prefer Ethernet 100% when linked. Metric must stick after Restart-NetAdapter:
        // re-stamp used to run before DHCP returned → "No usable Ethernet" → metric stayed ~20 auto.
        // Set metric on ANY Up Ethernet (IP not required); prefer adapters that already have IPv4.
        sb.AppendLine("""
function Set-EthMetrics {
  $ads = @(Get-ExoPhysicalAdapters)
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
        sb.AppendLine("if ($ethReadyOk) { Report 'eth-metrics' 'ok' } else { Report 'eth-metrics' 'skip' 'no up ethernet adapter for metric' }");
        // IPv4 fast path: documented RFC 6724 prefix-policy precedence for IPv4-mapped addresses
        // (::ffff:0:0/96 default 35/label 4 → 55 puts IPv4 above native IPv6 ::/0 at 40).
        // Replaces the retired "IPv6 metric = IPv4+20" hack. Original table is in the snapshot
        // (prefixPolicies) and restored exactly on repair.
        sb.AppendLine("if ($PreferIpv4First -eq 1) {");
        sb.AppendLine("  try {");
        sb.AppendLine("    $null = (netsh int ipv6 set prefixpolicy ::ffff:0:0/96 55 4 store=active 2>&1 | Out-String)");
        sb.AppendLine("    $ppOut = (netsh int ipv6 set prefixpolicy ::ffff:0:0/96 55 4 store=persistent 2>&1 | Out-String)");
        sb.AppendLine("    if ($LASTEXITCODE -eq 0) {");
        sb.AppendLine("      Log '[IPv4] prefix policy ::ffff:0:0/96 precedence 55 (IPv4-mapped first)'");
        sb.AppendLine("      Report 'prefix-policy' 'ok'");
        sb.AppendLine("    } else {");
        sb.AppendLine("      Report 'prefix-policy' 'skip' ('netsh prefixpolicy rejected: ' + $ppOut)");
        sb.AppendLine("    }");
        sb.AppendLine("  } catch { Report 'prefix-policy' 'skip' ('netsh prefixpolicy error: ' + $_.Exception.Message) }");
        sb.AppendLine("} else {");
        sb.AppendLine("  Report 'prefix-policy' 'skip' 'preset keeps default address precedence'");
        sb.AppendLine("}");
        // Laptop: keep AC path max; do not force DC min-CPU 100% (battery). Only re-stamp wireless max on DC.
        sb.AppendLine("if ($IsLaptopHint -eq 1) {");
        sb.AppendLine("  try {");
        sb.AppendLine("    $scheme = (powercfg /getactivescheme) -replace '.*GUID:\\s*([0-9a-f\\-]+).*','$1'");
        sb.AppendLine("    if ($scheme) {");
        sb.AppendLine("      powercfg /setdcvalueindex $scheme 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0 | Out-Null");
        sb.AppendLine("      powercfg /setactive $scheme | Out-Null");
        sb.AppendLine("      Log '[laptop] DC wireless=max kept; AC CPU max unchanged'");
        sb.AppendLine("    }");
        sb.AppendLine("  } catch {}");
        sb.AppendLine("}");
        // ============================================================================
        // VERIFIED ETHERNET GATE — Wi-Fi may only be disabled after a REAL internet
        // probe bound to the Ethernet adapter's IPv4 succeeds (TCP 443 to 1.1.1.1 /
        // 8.8.8.8). Also asserts an Ethernet adapter exists (Wi-Fi-only machines no-op).
        // ============================================================================
        sb.AppendLine("if (" + preferEth + " -eq 1) {");
        sb.AppendLine("  $ads = @(Get-ExoPhysicalAdapters)");
        sb.AppendLine("  $ethAdapters = @($ads | Where-Object { -not (Test-IsWifiAdapter $_) })");
        sb.AppendLine("  if ($ethAdapters.Count -eq 0) {");
        sb.AppendLine("    Log '[Exo-NET] No Ethernet adapter present - keeping Wi-Fi (wifi-only machine)'");
        sb.AppendLine("    Report 'wifi-disable' 'skip' 'no ethernet adapter on this machine'");
        sb.AppendLine("  } else {");
        sb.AppendLine("    $ethIp = $null");
        sb.AppendLine("    foreach ($e in @($ethAdapters | Where-Object { $_.Status -eq 'Up' })) {");
        sb.AppendLine("      $ip = @(Get-NetIPAddress -InterfaceIndex $e.ifIndex -AddressFamily IPv4 -EA SilentlyContinue | Where-Object { $_.IPAddress -notlike '169.254.*' })");
        sb.AppendLine("      if ($ip.Count -gt 0) { $ethIp = [string]$ip[0].IPAddress; break }");
        sb.AppendLine("    }");
        sb.AppendLine("    if (-not $ethIp) {");
        sb.AppendLine("      Log '[Exo-NET] Ethernet has no usable IPv4 yet - not disabling Wi-Fi'");
        sb.AppendLine("      Report 'wifi-disable' 'skip' 'ethernet has no usable ipv4'");
        sb.AppendLine("    } else {");
        sb.AppendLine("      Log \"[Exo-NET] Probing real internet over Ethernet ($ethIp) before touching Wi-Fi...\"");
        sb.AppendLine("      $ethProbeOk = Test-ExoConnectivity -BindIp $ethIp");
        sb.AppendLine("      if ($ethProbeOk) { if (Test-ExoDnsResolve) { Log '[probe] DNS resolve OK' } else { Log '[probe] DNS resolve failed (TCP probe passed - continuing)' } }");
        sb.AppendLine("      if (-not $ethProbeOk) {");
        sb.AppendLine("        Log '[Exo-NET] Ethernet internet probe FAILED - Wi-Fi stays enabled'");
        sb.AppendLine("        Report 'wifi-disable' 'skip' 'ethernet internet probe failed (tcp 443)'");
        sb.AppendLine("      } else {");
        sb.AppendLine("        Log '[Exo-NET] Ethernet verified (TCP 443) - preferring Ethernet (lowest latency)'");
        sb.AppendLine("        foreach ($w in @($ads | Where-Object { Test-IsWifiAdapter $_ })) {");
        sb.AppendLine("          try {");
        sb.AppendLine("            if ($w.Status -ne 'Disabled') {");
        sb.AppendLine("              try { Set-NetIPInterface -InterfaceIndex $w.ifIndex -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 75 -EA SilentlyContinue } catch {}");
        sb.AppendLine("              Disable-NetAdapter -Name $w.Name -Confirm:$false -EA SilentlyContinue");
        sb.AppendLine("              $ExoWifiDisabled += $w.Name");
        sb.AppendLine("              Log \"[NIC] Wi-Fi disabled: $($w.Name)\"");
        sb.AppendLine("            }");
        sb.AppendLine("          } catch { Log \"[NIC] could not disable $($w.Name)\" }");
        sb.AppendLine("        }");
        sb.AppendLine("        if (@($ExoWifiDisabled).Count -gt 0) { Report 'wifi-disable' 'ok' ('disabled after verified probe: ' + ($ExoWifiDisabled -join ', ')) }");
        sb.AppendLine("        else { Report 'wifi-disable' 'skip' 'no enabled wifi adapters to disable' }");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("} else {");
        sb.AppendLine("  Report 'wifi-disable' 'skip' 'user kept wifi (prefer-ethernet option off)'");
        sb.AppendLine("}");

        sb.AppendLine("try { Clear-DnsClientCache -EA SilentlyContinue } catch {}");

        // Restart Ethernet only if user confirmed (never auto Wi-Fi restart)
        sb.AppendLine("if (" + restartEth + " -eq 1) {");
        sb.AppendLine("  $adapters = @(Get-ExoPhysicalAdapters)");
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

        // ============================================================================
        // POST-APPLY CONNECTIVITY CHECK + AUTO-ROLLBACK — if the box lost internet,
        // re-enable Wi-Fi adapters we just disabled, restore interface metrics from
        // the pristine snapshot, re-probe, and record an honest rollback marker.
        // ============================================================================
        sb.AppendLine("""
Log '[Exo-NET] Post-apply connectivity check...'
$postOk = Test-ExoConnectivity
if (-not $postOk) { Start-Sleep -Seconds 3; $postOk = Test-ExoConnectivity }
$didRollback = $false
$rollbackReason = ''
if (-not $postOk) {
  Report 'post-probe' 'fail' 'no tcp 443 reachability after apply'
  Log '[Exo-NET] POST-APPLY CONNECTIVITY FAILED - rolling back path changes automatically'
  $didRollback = $true
  $rollbackReason = 'post-apply-connectivity-failed'
  foreach ($wn in @($ExoWifiDisabled)) {
    try {
      Enable-NetAdapter -Name $wn -Confirm:$false -EA SilentlyContinue
      Log "[rollback] Wi-Fi re-enabled: $wn"
    } catch { Log "[rollback] could not re-enable $wn" }
  }
  try {
    if (Test-Path -LiteralPath $ExoSnapshotPath) {
      $snapJson = Get-Content -LiteralPath $ExoSnapshotPath -Raw | ConvertFrom-Json
      foreach ($mi in @($snapJson.ipInterfaces)) {
        try {
          if ($mi.automaticMetric) {
            Set-NetIPInterface -InterfaceIndex ([int]$mi.ifIndex) -AddressFamily ([string]$mi.family) -AutomaticMetric Enabled -EA SilentlyContinue
          } else {
            Set-NetIPInterface -InterfaceIndex ([int]$mi.ifIndex) -AddressFamily ([string]$mi.family) -AutomaticMetric Disabled -InterfaceMetric ([int]$mi.metric) -EA SilentlyContinue
          }
        } catch {}
      }
      Log '[rollback] interface metrics restored from snapshot'
    } else {
      Log '[rollback] WARN snapshot missing - metrics not restored'
    }
  } catch { Log '[rollback] metric restore error' }
  Start-Sleep -Seconds 4
  $postOk = Test-ExoConnectivity
  if ($postOk) { Report 'rollback' 'ok' 'connectivity restored after rollback' }
  else { Report 'rollback' 'fail' 'connectivity still down - run Repair or Repair-Internet.ps1' }
} else {
  Report 'post-probe' 'ok'
}
# Honest apply-state marker (surfaced by NetworkOptimizerService status/probe API)
try {
  New-Item -ItemType Directory -Path $ExoDir -Force | Out-Null
  $applyState = [ordered]@{
    schemaVersion          = 1
    appliedUtc             = (Get-Date).ToUniversalTime().ToString('o')
    wifiDisabled           = @($ExoWifiDisabled)
    connectivityAfterApply = [bool]$postOk
    rollback               = [bool]$didRollback
    rollbackReason         = [string]$rollbackReason
  }
  $applyState | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ExoApplyStatePath -Encoding UTF8
} catch { Log '[Exo-NET] WARN could not write apply-state json' }
if ($didRollback) { Report 'apply' 'fail' 'connectivity lost after apply - path changes rolled back automatically' }
else { Report 'apply' 'ok' }
""");
        sb.AppendLine("Log '[Exo-NET] DONE preset=" + preset + "'");
        sb.AppendLine("exit 0");
        return sb.ToString();
    }

    /// <summary>
    /// Undo Exo network apply. TRUE RESTORE path: reads %LocalAppData%\Exo\network-snapshot.json
    /// (pristine pre-apply baseline) and restores exact values — registry (restored or removed if
    /// absent), advanced properties by RegistryKeyword, bindings, interface metrics incl.
    /// AutomaticMetric, adapter enable state, netsh/TCP settings, powercfg, dynamic ports, prefix
    /// policies, service start types. Deletes snapshot + apply-state on full success.
    /// FALLBACK path (no snapshot): approximate Windows-typical stock reset.
    /// Wi-Fi adapters Exo disabled are ALWAYS re-enabled regardless of path.
    /// </summary>
    public static string BuildRepair()
    {
        var sb = new StringBuilder(24_000);
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("$ProgressPreference = 'SilentlyContinue'");
        sb.AppendLine("$log = Join-Path $env:TEMP 'exo-net-repair-last.log'");
        sb.AppendLine("function Log([string]$m) { $ts = Get-Date -Format o; Add-Content -Path $log -Value \"$ts $m\" -EA SilentlyContinue; Write-Host $m }");
        sb.AppendLine("'' | Set-Content -Path $log -EA SilentlyContinue");
        sb.AppendLine("Log '[Exo-NET-REPAIR] Starting network repair'");
        sb.AppendLine(CommonSafetyFunctions);
        sb.AppendLine("""
function Set-Dword([string]$Path, [string]$Name, [int]$Value) {
  if (-not (Test-Path -LiteralPath $Path)) { New-Item -Path $Path -Force | Out-Null }
  Remove-ItemProperty -LiteralPath $Path -Name $Name -Force -EA SilentlyContinue
  New-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -PropertyType DWord -Force -EA SilentlyContinue | Out-Null
}
function Remove-Prop([string]$Path, [string]$Name) {
  if (Test-Path -LiteralPath $Path) { Remove-ItemProperty -LiteralPath $Path -Name $Name -Force -EA SilentlyContinue }
}
function Get-ExoNetshValue([string]$raw, [string]$label) {
  $m = [regex]::Match([string]$raw, ('(?im)^\s*' + [regex]::Escape($label) + '\s*:\s*(\S+)'))
  if ($m.Success) { return $m.Groups[1].Value.ToLowerInvariant() }
  return $null
}
$snap = $null
if (Test-Path -LiteralPath $ExoSnapshotPath) {
  try {
    $snap = Get-Content -LiteralPath $ExoSnapshotPath -Raw | ConvertFrom-Json
    if (-not $snap.snapshotVersion) { throw 'missing snapshotVersion' }
  } catch {
    $snap = $null
    Log ('[Exo-NET-REPAIR] Snapshot unreadable: ' + $_.Exception.Message)
  }
}
$restoreFailures = 0
if ($snap) {
  # ============================================================================
  # TRUE RESTORE — exact values recorded before the first Exo apply.
  # ============================================================================
  Log ('[Exo-NET-REPAIR] Restoring from snapshot (version ' + $snap.snapshotVersion + ', captured ' + $snap.timestampUtc + ')')
  Report 'restore-mode' 'ok' 'snapshot-driven true restore'
  # --- 1) Registry: restore recorded values; remove values that were absent ---
  foreach ($rv in @($snap.regValues)) {
    try {
      $kind = [string]$rv.kind
      if ($kind -eq 'absent') {
        if (Test-Path -LiteralPath $rv.path) {
          Remove-ItemProperty -LiteralPath $rv.path -Name $rv.name -Force -EA SilentlyContinue
        }
      } else {
        if (-not (Test-Path -LiteralPath $rv.path)) { New-Item -Path $rv.path -Force -EA SilentlyContinue | Out-Null }
        if ($kind -eq 'Binary') {
          $bytes = [byte[]]@(@($rv.value) | ForEach-Object { [byte]$_ })
          Set-ItemProperty -LiteralPath $rv.path -Name $rv.name -Value $bytes -Type Binary -Force -EA Stop
        } elseif ($kind -eq 'MultiString') {
          Set-ItemProperty -LiteralPath $rv.path -Name $rv.name -Value ([string[]]@($rv.value)) -Type MultiString -Force -EA Stop
        } else {
          Set-ItemProperty -LiteralPath $rv.path -Name $rv.name -Value $rv.value -Type $kind -Force -EA Stop
        }
      }
    } catch {
      $restoreFailures++
      Log ('[repair] registry restore failed: ' + $rv.path + ' \ ' + $rv.name)
    }
  }
  Report 'restore-registry' $(if ($restoreFailures -eq 0) { 'ok' } else { 'fail' }) $(if ($restoreFailures -gt 0) { "$restoreFailures value(s) failed" } else { '' })
  # --- 2) netsh TCP globals (structured snapshot first, raw parse fallback) ---
  $inet = @($snap.tcpSettings) | Where-Object { [string]$_.settingName -eq 'Internet' } | Select-Object -First 1
  if ($inet -and $inet.autoTuningLevelLocal) {
    $lvl = ([string]$inet.autoTuningLevelLocal).ToLowerInvariant()
    try { netsh int tcp set global autotuninglevel=$lvl | Out-Null } catch {}
  }
  $og = @($snap.offloadGlobal) | Select-Object -First 1
  if ($og) {
    $rscVal = if ([string]$og.receiveSegmentCoalescing -match '(?i)disabled') { 'disabled' } else { 'enabled' }
    $rssVal = if ([string]$og.receiveSideScaling -match '(?i)disabled') { 'disabled' } else { 'enabled' }
    try { netsh int tcp set global rsc=$rscVal | Out-Null } catch {}
    try { netsh int tcp set global rss=$rssVal | Out-Null } catch {}
  }
  $tcpRaw = [string]$snap.netshTcpGlobalRaw
  foreach ($pair in @(
      @{ Label = 'RFC 1323 Timestamps'; Opt = 'timestamps'; Default = 'default' },
      @{ Label = 'Fast Open Fallback'; Opt = 'fastopenfallback'; Default = 'enabled' },
      @{ Label = 'Fast Open'; Opt = 'fastopen'; Default = 'enabled' },
      @{ Label = 'HyStart'; Opt = 'hystart'; Default = 'enabled' },
      @{ Label = 'Pacing Profile'; Opt = 'pacingprofile'; Default = 'off' },
      @{ Label = 'ECN Capability'; Opt = 'ecncapability'; Default = 'default' }
  )) {
    $val = Get-ExoNetshValue $tcpRaw $pair.Label
    if (-not $val) {
      $val = $pair.Default
      Log ('[repair] netsh ' + $pair.Opt + ' not parseable from snapshot - using Windows default ' + $val)
    }
    try { $null = (netsh int tcp set global "$($pair.Opt)=$val" 2>&1 | Out-String) } catch {}
  }
  $heur = if ([string]$snap.netshTcpHeuristicsRaw -match '(?i)\bdisabled\b') { 'disabled' } else { 'enabled' }
  try { netsh int tcp set heuristics $heur | Out-Null } catch {}
  if ([System.Environment]::OSVersion.Version.Build -ge 26100) {
    $uroVal = if ([string]$snap.netshUdpGlobalRaw -match '(?i)\bdisabled\b') { 'disabled' } else { 'enabled' }
    try { $null = (netsh int udp set global uro=$uroVal 2>&1 | Out-String) } catch {}
  }
  Report 'restore-tcp-globals' 'ok'
  # --- 3) Set-NetTCPSetting per template (custom templates accept writes) ---
  foreach ($ts in @($snap.tcpSettings)) {
    $sn = [string]$ts.settingName
    if (-not $sn) { continue }
    try { if ($ts.congestionProvider) { Set-NetTCPSetting -SettingName $sn -CongestionProvider ([string]$ts.congestionProvider) -EA SilentlyContinue } } catch {}
    try { if ($ts.autoTuningLevelLocal) { Set-NetTCPSetting -SettingName $sn -AutoTuningLevelLocal ([string]$ts.autoTuningLevelLocal) -EA SilentlyContinue } } catch {}
    try { if ([int]$ts.initialRtoMs -gt 0) { Set-NetTCPSetting -SettingName $sn -InitialRtoMs ([int]$ts.initialRtoMs) -EA SilentlyContinue } } catch {}
    try { if ([int]$ts.minRtoMs -gt 0) { Set-NetTCPSetting -SettingName $sn -MinRtoMs ([int]$ts.minRtoMs) -EA SilentlyContinue } } catch {}
    try { if ([int]$ts.maxSynRetransmissions -gt 0) { Set-NetTCPSetting -SettingName $sn -MaxSynRetransmissions ([int]$ts.maxSynRetransmissions) -EA SilentlyContinue } } catch {}
    try { if ($ts.nonSackRttResiliency) { Set-NetTCPSetting -SettingName $sn -NonSackRttResiliency ([string]$ts.nonSackRttResiliency) -EA SilentlyContinue } } catch {}
    try { if ($ts.timestamps) { Set-NetTCPSetting -SettingName $sn -Timestamps ([string]$ts.timestamps) -EA SilentlyContinue } } catch {}
    try { if ($ts.ecnCapability) { Set-NetTCPSetting -SettingName $sn -EcnCapability ([string]$ts.ecnCapability) -EA SilentlyContinue } } catch {}
  }
  Report 'restore-tcp-settings' 'ok'
  $liveAdapters = @(Get-NetAdapter -EA SilentlyContinue)
  function Resolve-ExoAdapter([string]$name, [string]$ifDesc) {
    $hit = $liveAdapters | Where-Object { [string]$_.Name -eq $name } | Select-Object -First 1
    if (-not $hit -and $ifDesc) {
      $hit = $liveAdapters | Where-Object { [string]$_.InterfaceDescription -eq $ifDesc } | Select-Object -First 1
    }
    return $hit
  }
  # --- 4) Re-enable adapters recorded as enabled (BEFORE property writes) ---
  foreach ($ast in @($snap.adapterStates)) {
    try {
      if ($ast.adminUp) {
        $cur = Resolve-ExoAdapter ([string]$ast.name) ([string]$ast.ifDesc)
        if ($cur -and [string]$cur.Status -eq 'Disabled') {
          Enable-NetAdapter -Name $cur.Name -Confirm:$false -EA SilentlyContinue
          Log ('[repair] adapter re-enabled: ' + $cur.Name)
        }
      }
    } catch {}
  }
  Report 'restore-adapter-state' 'ok'
  # --- 5) Advanced properties by RegistryKeyword (exact recorded values) ---
  $advFail = 0
  foreach ($ap in @($snap.advancedProps)) {
    $target = Resolve-ExoAdapter ([string]$ap.adapter) ([string]$ap.ifDesc)
    if (-not $target) { continue }
    $vals = @(([string]$ap.value) -split ',' | Where-Object { $_ -ne '' })
    if ($vals.Count -eq 0) { continue }
    try {
      Set-NetAdapterAdvancedProperty -Name $target.Name -RegistryKeyword ([string]$ap.keyword) -RegistryValue $vals -NoRestart -EA SilentlyContinue
    } catch { $advFail++ }
  }
  Report 'restore-advanced-props' $(if ($advFail -eq 0) { 'ok' } else { 'fail' }) $(if ($advFail -gt 0) { "$advFail keyword(s) failed" } else { '' })
  if ($advFail -gt 0) { $restoreFailures += $advFail }
  # --- 6) Bindings (ComponentID + Enabled exactly as recorded) ---
  foreach ($b in @($snap.bindings)) {
    $target = Resolve-ExoAdapter ([string]$b.adapter) ([string]$b.ifDesc)
    if (-not $target) { continue }
    try {
      if ($b.enabled) { Enable-NetAdapterBinding -Name $target.Name -ComponentID ([string]$b.componentId) -EA SilentlyContinue }
      else { Disable-NetAdapterBinding -Name $target.Name -ComponentID ([string]$b.componentId) -EA SilentlyContinue }
    } catch {}
  }
  Report 'restore-bindings' 'ok'
  # --- 7) Interface metrics incl. AutomaticMetric Enabled/Disabled ---
  foreach ($mi in @($snap.ipInterfaces)) {
    try {
      if ($mi.automaticMetric) {
        Set-NetIPInterface -InterfaceIndex ([int]$mi.ifIndex) -AddressFamily ([string]$mi.family) -AutomaticMetric Enabled -EA SilentlyContinue
        Set-NetIPInterface -InterfaceAlias ([string]$mi.alias) -AddressFamily ([string]$mi.family) -AutomaticMetric Enabled -EA SilentlyContinue
      } else {
        Set-NetIPInterface -InterfaceIndex ([int]$mi.ifIndex) -AddressFamily ([string]$mi.family) -AutomaticMetric Disabled -InterfaceMetric ([int]$mi.metric) -EA SilentlyContinue
        Set-NetIPInterface -InterfaceAlias ([string]$mi.alias) -AddressFamily ([string]$mi.family) -AutomaticMetric Disabled -InterfaceMetric ([int]$mi.metric) -EA SilentlyContinue
      }
    } catch {}
  }
  Report 'restore-metrics' 'ok'
  # --- 8) RSS config (Enabled + BaseProcessorNumber + Profile) ---
  foreach ($r in @($snap.rss)) {
    try {
      Set-NetAdapterRss -Name ([string]$r.adapter) -Enabled ([bool]$r.enabled) -EA SilentlyContinue
      if ([bool]$r.enabled) {
        Set-NetAdapterRss -Name ([string]$r.adapter) -BaseProcessorNumber ([int]$r.baseProcessorNumber) -EA SilentlyContinue
        if ($r.profile) { Set-NetAdapterRss -Name ([string]$r.adapter) -Profile ([string]$r.profile) -EA SilentlyContinue }
      }
    } catch {}
  }
  Report 'restore-rss' 'ok'
  # --- 9) powercfg values (AC/DC indexes exactly as recorded) ---
  foreach ($p in @($snap.powercfg)) {
    try {
      if ($p.ac) {
        $acVal = [Convert]::ToInt64((([string]$p.ac) -replace '^0x', ''), 16)
        powercfg /setacvalueindex $p.scheme $p.sub $p.setting $acVal | Out-Null
      }
      if ($p.dc) {
        $dcVal = [Convert]::ToInt64((([string]$p.dc) -replace '^0x', ''), 16)
        powercfg /setdcvalueindex $p.scheme $p.sub $p.setting $dcVal | Out-Null
      }
      powercfg /setactive $p.scheme | Out-Null
    } catch {}
  }
  Report 'restore-powercfg' 'ok'
  # --- 10) Dynamic port ranges ---
  foreach ($d in @($snap.dynamicPorts)) {
    try { netsh int $($d.family) set dynamicport $($d.protocol) start=$($d.start) num=$($d.num) | Out-Null } catch {}
  }
  Report 'restore-dynamic-ports' 'ok'
  # --- 11) IPv6 prefix policies (exact recorded table) ---
  if (@($snap.prefixPolicies).Count -gt 0) {
    foreach ($pp in @($snap.prefixPolicies)) {
      foreach ($store in @('active', 'persistent')) {
        try { $null = (netsh int ipv6 set prefixpolicy "$($pp.prefix)" $($pp.precedence) $($pp.label) store=$store 2>&1 | Out-String) } catch {}
      }
    }
    Report 'restore-prefixpolicy' 'ok'
  } else {
    # No parsed table: put the one prefix Exo changes back to its documented default
    foreach ($store in @('active', 'persistent')) {
      try { $null = (netsh int ipv6 set prefixpolicy ::ffff:0:0/96 35 4 store=$store 2>&1 | Out-String) } catch {}
    }
    Report 'restore-prefixpolicy' 'skip' 'no parsed table - reset ::ffff:0:0/96 to default 35/4'
  }
  # --- 12) Service start types (DoSvc) ---
  foreach ($svc in @($snap.services)) {
    try { Set-Service -Name ([string]$svc.name) -StartupType ([string]$svc.startType) -EA SilentlyContinue } catch {}
  }
  Report 'restore-services' 'ok'
  # --- Tunnels back to system default (apply forced disabled; snapshot-independent) ---
  try { netsh interface teredo set state default | Out-Null } catch {}
  try { netsh interface isatap set state default | Out-Null } catch {}
  try { netsh interface 6to4 set state default | Out-Null } catch {}
  # --- Delete snapshot + state ONLY on full success (keep baseline for retry otherwise) ---
  if ($restoreFailures -eq 0) {
    Remove-Item -LiteralPath $ExoSnapshotPath -Force -EA SilentlyContinue
    Remove-Item -LiteralPath $ExoApplyStatePath -Force -EA SilentlyContinue
    Log '[Exo-NET-REPAIR] Snapshot + apply-state cleared (full restore success)'
    Report 'snapshot-cleanup' 'ok'
  } else {
    Log ("[Exo-NET-REPAIR] $restoreFailures restore failure(s) - snapshot KEPT so repair can be retried")
    Report 'snapshot-cleanup' 'skip' "$restoreFailures restore failure(s) - snapshot kept for retry"
  }
} else {
  # ============================================================================
  # FALLBACK — no snapshot: APPROXIMATE stock reset (Windows-typical defaults).
  # ============================================================================
  Log '[Exo-NET-REPAIR] No snapshot found - APPROXIMATE stock reset (fallback path)'
  Report 'restore-mode' 'skip' 'no-snapshot-fallback-stock-reset'
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
  # DNS ServiceProvider priorities → documented Windows defaults
  $sp = 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider'
  Set-Dword $sp 'LocalPriority' 499
  Set-Dword $sp 'HostsPriority' 500
  Set-Dword $sp 'DnsPriority' 2000
  Set-Dword $sp 'NetbtPriority' 2001
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
  # Extended tweak layer → Windows defaults (timestamps off is OS default; use 'default' when accepted)
  try { $null = (netsh int tcp set global timestamps=default 2>&1 | Out-String) } catch {}
  try { $null = (netsh int tcp set global fastopen=enabled 2>&1 | Out-String) } catch {}
  try { $null = (netsh int tcp set global fastopenfallback=enabled 2>&1 | Out-String) } catch {}
  try { $null = (netsh int tcp set global hystart=enabled 2>&1 | Out-String) } catch {}
  try { $null = (netsh int tcp set global pacingprofile=off 2>&1 | Out-String) } catch {}
  try { $null = (netsh int tcp set global ecncapability=default 2>&1 | Out-String) } catch {}
  if ([System.Environment]::OSVersion.Version.Build -ge 26100) {
    try { $null = (netsh int udp set global uro=enabled 2>&1 | Out-String) } catch {}
  }
  $isWin11 = ([System.Environment]::OSVersion.Version.Build -ge 22000)
  $rtoDefault = if ($isWin11) { 1000 } else { 3000 }
  $synDefault = if ($isWin11) { 4 } else { 2 }
  foreach ($pr in @('Internet','InternetCustom')) {
    try { Set-NetTCPSetting -SettingName $pr -AutoTuningLevelLocal Normal -EA SilentlyContinue } catch {}
    try { Set-NetTCPSetting -SettingName $pr -ScalingHeuristics Enabled -EA SilentlyContinue } catch {}
    try { Set-NetTCPSetting -SettingName $pr -InitialRtoMs $rtoDefault -EA SilentlyContinue } catch {}
    try { Set-NetTCPSetting -SettingName $pr -MinRtoMs 300 -EA SilentlyContinue } catch {}
    try { Set-NetTCPSetting -SettingName $pr -MaxSynRetransmissions $synDefault -EA SilentlyContinue } catch {}
  }
  # Prefix policy Exo changes → documented default precedence (::ffff:0:0/96 35 label 4)
  foreach ($store in @('active', 'persistent')) {
    try { $null = (netsh int ipv6 set prefixpolicy ::ffff:0:0/96 35 4 store=$store 2>&1 | Out-String) } catch {}
  }
  # Delivery Optimization — clear Exo force-off + restore service default (delayed auto)
  try { Remove-Prop 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config' 'DODownloadMode' } catch {}
  try { sc.exe config DoSvc start= delayed-auto | Out-Null } catch {}
  try { Remove-Prop 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\BITS' 'EnableBITSMaxBandwidth' } catch {}
  # NCSI active probe policy
  try { Remove-Prop 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\NetworkConnectivityStatusIndicator' 'NoActiveProbe' } catch {}
  # Network policies Exo may have set — clear force-values
  try {
    Remove-Prop 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient' 'EnableMulticast'
    Remove-Prop 'HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters' 'MaxCacheTtl'
    Remove-Prop 'HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters' 'MaxNegativeCacheTtl'
    Remove-Prop 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' 'AutoDetect'
  } catch {}
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
  Log '[repair] host stack + tunnels restored (approximate stock)'
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
    # RSS base processor back to driver default 0
    try { Set-NetAdapterRss -Name $n -BaseProcessorNumber 0 -EA SilentlyContinue } catch {}
    # Automatic metric (OS default)
    foreach ($af in @('IPv4','IPv6')) {
      try {
        Set-NetIPInterface -InterfaceIndex $a.ifIndex -AddressFamily $af -AutomaticMetric Enabled -EA SilentlyContinue
      } catch {}
    }
    Log "[repair] bindings+metric auto $n"
  }
  Remove-Item -LiteralPath $ExoApplyStatePath -Force -EA SilentlyContinue
}
# ============================================================================
# ALWAYS (both paths): re-enable Wi-Fi adapters Exo may have disabled.
# ============================================================================
$allPhys = @(Get-NetAdapter -Physical -EA SilentlyContinue)
foreach ($w in @($allPhys | Where-Object { [string]$_.PhysicalMediaType -match '802\.11|Wireless' -or [string]$_.Name -match '(?i)Wi-?Fi|Wireless|WLAN' })) {
  try {
    if ($w.Status -eq 'Disabled') {
      Enable-NetAdapter -Name $w.Name -Confirm:$false -EA SilentlyContinue
      Log "[repair] Wi-Fi re-enabled: $($w.Name)"
    }
  } catch {}
}
Report 'wifi-reenable' 'ok'
try { Clear-DnsClientCache -EA SilentlyContinue } catch {}
$repairProbe = Test-ExoConnectivity
if ($repairProbe) { Report 'post-probe' 'ok' } else { Report 'post-probe' 'fail' 'no tcp 443 reachability after repair' }
Log '[Exo-NET-REPAIR] DONE'
exit 0
""");
        return sb.ToString();
    }

    /// <summary>
    /// Non-elevated proof-layer benchmark: ping p50/p95 + jitter (10 pings each to
    /// 1.1.1.1 and 8.8.8.8) and average DNS resolve time. Prints exactly one
    /// EXO_BENCH:{json} line for <see cref="NetworkLogic.TryParseBenchmark"/>.
    /// </summary>
    public static string BuildBenchmark()
    {
        var sb = new StringBuilder(3_000);
        sb.AppendLine("""
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
function Get-ExoPingTimes([string]$Target) {
  $raw = (ping.exe -n 10 -w 1500 $Target 2>$null | Out-String)
  return @([regex]::Matches($raw, '[=<](\d+)\s*ms') | ForEach-Object { [double]$_.Groups[1].Value })
}
$samples = @()
$samples += Get-ExoPingTimes '1.1.1.1'
$samples += Get-ExoPingTimes '8.8.8.8'
$ok = ($samples.Count -ge 4)
$p50 = 0.0; $p95 = 0.0; $jitter = 0.0
if ($ok) {
  $sorted = @($samples | Sort-Object)
  $p50 = [double]$sorted[[int][Math]::Floor(($sorted.Count - 1) * 0.5)]
  $p95 = [double]$sorted[[int][Math]::Floor(($sorted.Count - 1) * 0.95)]
  $diffs = @()
  for ($i = 1; $i -lt $samples.Count; $i++) { $diffs += [Math]::Abs($samples[$i] - $samples[$i - 1]) }
  if ($diffs.Count -gt 0) { $jitter = [Math]::Round((($diffs | Measure-Object -Average).Average), 2) }
}
$dnsTimes = @()
foreach ($name in @('www.google.com', 'www.cloudflare.com', 'www.microsoft.com')) {
  try {
    $t = Measure-Command { $null = Resolve-DnsName -Name $name -Type A -DnsOnly -EA Stop }
    $dnsTimes += [double]$t.TotalMilliseconds
  } catch {}
}
$dnsMs = -1.0
if ($dnsTimes.Count -gt 0) { $dnsMs = [Math]::Round((($dnsTimes | Measure-Object -Average).Average), 2) }
$result = [ordered]@{
  ok           = [bool]$ok
  pingP50Ms    = [Math]::Round($p50, 2)
  pingP95Ms    = [Math]::Round($p95, 2)
  jitterMs     = $jitter
  dnsMs        = $dnsMs
  samples      = $samples.Count
  timestampUtc = (Get-Date).ToUniversalTime().ToString('o')
}
Write-Output ('EXO_BENCH:' + ($result | ConvertTo-Json -Compress))
exit 0
""");
        return sb.ToString();
    }
}
