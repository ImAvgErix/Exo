using System.Text;
using Exo.Models;

namespace Exo.Services;

/// <summary>
/// Generates the elevated PowerShell apply script (shipped path).
/// Pure string build - no elevation. Driven by <see cref="NetworkLogic"/> knobs.
/// Split (v3): Build here; BuildRepair / BuildBenchmark in partial files.
/// Safety: pristine snapshot, metrics-only eth prefer, post-apply rollback, EXO_REPORT.
/// </summary>
public static partial class NetworkApplyScriptBuilder
{
    private static string PsQuote(string value) => "'" + (value ?? string.Empty).Replace("'", "''") + "'";

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
        // Host gaming stack (MMCSS / HAGS / Game Mode / Win32 priority) is owned by Windows —
        // never snapshot or mutate those keys from Internet (Repair must not undo Windows).
        (@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\Psched", "NonBestEffortLimit"),
        (@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config", "DODownloadMode"),
        (@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\NetworkConnectivityStatusIndicator", "NoActiveProbe"),
        (@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\NetworkConnectivityStatusIndicator", "DisablePassivePolling"),
        (@"HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient", "EnableMulticast"),
        (@"HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", "MaxCacheTtl"),
        (@"HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", "MaxNegativeCacheTtl"),
        (@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings", "AutoDetect"),
        (@"HKLM:\SOFTWARE\Policies\Microsoft\Windows\BITS", "EnableBITSMaxBandwidth"),
        // SMB transfer throttle (Nexus-style; reversible via snapshot/repair)
        (@"HKLM:\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters", "DisableBandwidthThrottling"),
        (@"HKLM:\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "DisableBandwidthThrottling"),
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
        var latency = preset == NetworkPreset.LowestLatency;
        var autotune = knobs.AutotuneNetsh;
        var autoTuningPs = knobs.AutotunePs;
        var rsc = knobs.Rsc;
        var lso = knobs.Lso;
        var im = knobs.InterruptMod;
        var flow = knobs.FlowControl;
        var idleRestrict = knobs.IdleRestrict;
        var restartEth = options.RestartEthernet ? "1" : "0";
        var preferEth = options.PreferEthernetDisableWifi ? "1" : "0";
        // Hint only - apply script re-probes live for band capability
        var prefer6Hint = media.ClientSupports6Ghz ? "1" : "0";
        // RSS: use physical cores when known (HT threads are not "12 cores" on a 6-core CPU).
        var coreBudget = media.PhysicalCores > 0
            ? media.PhysicalCores
            : (media.LogicalProcessors > 0 ? Math.Max(1, media.LogicalProcessors / 2) : Math.Max(1, Environment.ProcessorCount / 2));
        var logicalCpus = media.LogicalProcessors > 0 ? media.LogicalProcessors : Environment.ProcessorCount;
        var rssBudget = NetworkLogic.RssQueueBudget(preset, coreBudget);
        var bufferStrategy = NetworkLogic.BufferStrategy(preset);
        // Set-NetAdapterRss exposes the enum name "Closest" on supported Windows
        // builds. "ClosestProcessor" is not a valid Profile value and silently
        // left latency applies on the previous (usually NUMAStatic) policy.
        var rssProfile = latency ? "Closest" : "NUMAStatic";
        var vendorHint = string.IsNullOrWhiteSpace(media.NicVendor) ? "Unknown" : media.NicVendor;
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
        sb.AppendLine("$VendorHint = '" + vendorHint.Replace("'", "") + "'");
        sb.AppendLine("$IsLaptopHint = " + (media.IsLikelyLaptop ? "1" : "0"));
        sb.AppendLine("$ExoDnsProvider = " + PsQuote(options.DnsProvider));
        sb.AppendLine("$ExoDnsV4 = @(" + PsQuote(options.DnsPrimary) + "," + PsQuote(options.DnsSecondary) + ")");
        sb.AppendLine("$ExoDnsV6 = @(" + PsQuote(options.DnsPrimaryV6) + "," + PsQuote(options.DnsSecondaryV6) + ")");
        sb.AppendLine("$ExoDnsDohTemplate = " + PsQuote(options.DnsOverHttpsTemplate));
        sb.AppendLine("$ExoWifiDisabled = @()");
        sb.AppendLine("Log \"[Exo-NET] net-only BufferStrategy=$BufferStrategy RssQueues<=$RssQueueBudget IP-precedence=Windows-default Vendor=$VendorHint\"");
        sb.AppendLine(CommonSafetyFunctions);
        sb.AppendLine("""
function Set-Dword([string]$Path, [string]$Name, [int]$Value) {
  if (-not (Test-Path -LiteralPath $Path)) { New-Item -Path $Path -Force | Out-Null }
  # Force clean write (overwrites ffffffff / wrong types)
  Remove-ItemProperty -LiteralPath $Path -Name $Name -Force -EA SilentlyContinue
  New-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -PropertyType DWord -Force -EA SilentlyContinue | Out-Null
  Set-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -Type DWord -Force -EA SilentlyContinue
}
function Set-String([string]$Path, [string]$Name, [string]$Value) {
  if (-not (Test-Path -LiteralPath $Path)) { New-Item -Path $Path -Force | Out-Null }
  Remove-ItemProperty -LiteralPath $Path -Name $Name -Force -EA SilentlyContinue
  New-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -PropertyType String -Force -EA SilentlyContinue | Out-Null
  Set-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -Type String -Force -EA SilentlyContinue
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
        // PRE-APPLY SNAPSHOT - captured BEFORE any mutation. If a snapshot file exists
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
      snapshotVersion = 2
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
    # NOTE: lists are created via ::new() and materialized via .ToArray() -
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
    $powerList = [System.Collections.Generic.List[object]]::new()
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
      try {
        $pm = Get-NetAdapterPowerManagement -Name $a.Name -EA SilentlyContinue
        if ($pm) {
          $entry = [ordered]@{ adapter = [string]$a.Name }
          foreach ($property in @('D0PacketCoalescing','ArpOffload','DeviceSleepOnDisconnect','NSOffload','RsnRekeyOffload','SelectiveSuspend','WakeOnMagicPacket','WakeOnPattern')) {
            if ($pm.PSObject.Properties.Name -contains $property) { $entry[$property] = [string]$pm.$property }
          }
          [void]$powerList.Add([pscustomobject]$entry)
        }
      } catch {}
    }
    $snap.advancedProps = $advList.ToArray()
    $snap.bindings = $bindList.ToArray()
    $snap.powerManagement = $powerList.ToArray()
    # --- per-adapter DNS servers + DoH registrations (privacy feature + repair) ---
    $snap.dnsServers = @($phys | ForEach-Object {
      try {
        [pscustomobject]@{
          ifIndex = [int]$_.ifIndex
          name    = [string]$_.Name
          ipv4    = @((Get-DnsClientServerAddress -InterfaceIndex $_.ifIndex -AddressFamily IPv4 -EA SilentlyContinue).ServerAddresses)
          ipv6    = @((Get-DnsClientServerAddress -InterfaceIndex $_.ifIndex -AddressFamily IPv6 -EA SilentlyContinue).ServerAddresses)
        }
      } catch {}
    })
    $snap.dohServers = @(
      if (Get-Command Get-DnsClientDohServerAddress -EA SilentlyContinue) {
        Get-DnsClientDohServerAddress -EA SilentlyContinue | ForEach-Object {
          [pscustomobject]@{
            serverAddress      = [string]$_.ServerAddress
            dohTemplate        = [string]$_.DohTemplate
            allowFallbackToUdp = [bool]$_.AllowFallbackToUdp
            autoUpgrade        = [bool]$_.AutoUpgrade
          }
        }
      }
    )
    # Record exactly which resolver registrations this apply may add so Repair
    # can remove only Exo's additions and restore any pre-existing definitions.
    $snap.exoDohServers = @($ExoDnsV4 + $ExoDnsV6 | Where-Object { $_ })
    $snap.dohRaw = ((netsh dnsclient show encryption 2>$null | Out-String)).Trim()
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
          maxProcessors       = [int]$r.MaxProcessors
          receiveQueues       = [int]$r.NumberOfReceiveQueues
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
        // Clear obsolete static RWIN / chimney / server-era keys (auto-tuning owns window size).
        // Never write MaxUserPort / static TcpWindowSize / LargeSystemCache=1 / Chimney=1.
        sb.AppendLine("Remove-Prop $tcp 'GlobalMaxTcpWindowSize'");
        sb.AppendLine("Remove-Prop $tcp 'TcpWindowSize'");
        sb.AppendLine("Remove-Prop $tcp 'EnableTCPChimney'");
        sb.AppendLine("Remove-Prop $tcp 'EnableTCPA'");
        sb.AppendLine("Remove-Prop $tcp 'EnableDCA'");
        sb.AppendLine("Remove-Prop $tcp 'TcpNumConnections'");
        sb.AppendLine("Remove-Prop $tcp 'LargeSystemCache'");
        sb.AppendLine("Remove-Prop $tcp 'MaxUserPort'");
        // Network-only host QoS: remove the old 20% reserved-bandwidth tax.
        // MMCSS / Games MMCSS / Win32 priority / HAGS / Game Mode → Windows optimizer only.
        sb.AppendLine("$psched = 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\Psched'");
        sb.AppendLine("Set-Dword $psched 'NonBestEffortLimit' 0");
        sb.AppendLine("Report 'host-policy' 'ok' 'network: NonBestEffortLimit=0 (MMCSS/HAGS/Game Mode owned by Windows)'");
        sb.AppendLine("Report 'registry-host' 'ok' 'tcpip + psched only'");

        // --- netsh / Set-NetTCPSetting (safe competitive path) ---
        // Keep Windows adaptive TCP algorithms (timestamps / HyStart / pacing / ECN / URO /
        // Fast Open / RTO folklore are intentionally NOT forced — smoke + golden path).
        sb.AppendLine("netsh int tcp set global rss=enabled | Out-Null");
        sb.AppendLine("netsh int tcp set global autotuninglevel=" + autotune + " | Out-Null");
        sb.AppendLine("netsh int tcp set global rsc=" + rsc + " | Out-Null");
        sb.AppendLine("try { netsh int tcp set heuristics disabled | Out-Null } catch {}");
        sb.AppendLine("try { netsh int tcp set supplemental template=internet congestionprovider=cubic | Out-Null } catch {}");
        sb.AppendLine("try { netsh int tcp set supplemental template=internetcustom congestionprovider=cubic | Out-Null } catch {}");
        sb.AppendLine("try { netsh int ip set global taskoffload=enabled | Out-Null } catch {}");
        sb.AppendLine("Report 'tcp-globals' 'ok'");
        sb.AppendLine("Report 'tcp-algorithms' 'skip' 'Windows adaptive TCP retained (no timestamps/HyStart/pacing/ECN/URO force)'");
        sb.AppendLine("foreach ($pr in @('Internet','InternetCustom')) {");
        sb.AppendLine("  try { Set-NetTCPSetting -SettingName $pr -CongestionProvider CUBIC -EA SilentlyContinue } catch {}");
        sb.AppendLine("  try { Set-NetTCPSetting -SettingName $pr -AutoTuningLevelLocal " + autoTuningPs + " -EA SilentlyContinue } catch {}");
        sb.AppendLine("  try { Set-NetTCPSetting -SettingName $pr -ScalingHeuristics Disabled -EA SilentlyContinue } catch {}");
        sb.AppendLine("}");
        sb.AppendLine("Report 'tcp-settings' 'ok'");

        // Per-interface Nagle/ACK folklore: remove if present, never force pins.
        sb.AppendLine("Get-ChildItem 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces' -EA SilentlyContinue | ForEach-Object {");
        sb.AppendLine("  $p = $_.PSPath");
        sb.AppendLine("  Remove-Prop $p 'TcpAckFrequency'");
        sb.AppendLine("  Remove-Prop $p 'TCPNoDelay'");
        sb.AppendLine("  Remove-Prop $p 'TcpDelAckTicks'");
        sb.AppendLine("}");
        sb.AppendLine("Report 'legacy-ack-pins' 'ok' 'cleared folklore ACK/Nagle pins; Nagle left adaptive'");

        // --- Per-adapter: branch Ethernet vs Wi‑Fi (MS: wireless often has no RSS/LSO) ---
        // Apply to all physical NICs so dual-homed PCs are ready on either media.
        // Fuzzy pick among ValidDisplayValues - Intel / Realtek / MediaTek / Qualcomm / Killer strings vary
        // Prefer-* beats Only-* (never force band-only). Score picks best available option.
        sb.AppendLine("function Select-BandDisplayValue([object[]]$vals, [bool]$want6) {");
        sb.AppendLine("  if (-not $vals -or $vals.Count -eq 0) { return $null }");
        sb.AppendLine("  $list = @($vals | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })");
        sb.AppendLine("  $scored = foreach ($v in $list) {");
        sb.AppendLine("    $s = 0");
        sb.AppendLine("    $isOnly = ($v -match '(?i)\\bonly\\b|\\bexclusive\\b')");
        sb.AppendLine("    $isPref = ($v -match '(?i)prefer|preferred|preferable|priority|favou?r')");
        sb.AppendLine("    # 2.4 - never choose for gaming when higher exists");
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
        // Ensure WLAN AutoConfig is running so netsh/driver radio facts work on community PCs
        // where wlansvc was stopped (detect and band prefer both need it).
        sb.AppendLine("""
try {
  $wlanSvc = Get-Service -Name 'wlansvc' -EA SilentlyContinue
  if ($wlanSvc -and $wlanSvc.Status -ne 'Running') {
    if ($wlanSvc.StartType -eq 'Disabled') {
      Set-Service -Name 'wlansvc' -StartupType Manual -EA SilentlyContinue
    }
    Start-Service -Name 'wlansvc' -EA SilentlyContinue
    Start-Sleep -Milliseconds 600
    Log '[wlan] started Wireless AutoConfig (wlansvc)'
  }
} catch { Log ('[wlan] wlansvc start skipped: ' + $_.Exception.Message) }
""");
        // Live band capability re-probe once (do not trust only pre-apply C# snapshot)
        sb.AppendLine("$wantBand6Live = $false");
        sb.AppendLine("$drvLive = (netsh wlan show drivers 2>$null | Out-String)");
        sb.AppendLine("if ($drvLive -match '(?i)802\\.11be|6\\s*GHz|Wi-?Fi\\s*6E') { $wantBand6Live = $true }");
        sb.AppendLine("if (-not $wantBand6Live -and " + prefer6Hint + " -eq 1) { $wantBand6Live = $true }");
        sb.AppendLine("Log \"[band] want6Live=$wantBand6Live\"");
        sb.AppendLine("$rssBaseCount = 0");
        sb.AppendLine("$rssPolicyCount = 0");
        sb.AppendLine("$packetCoalescingCount = 0");
        sb.AppendLine("$wifiTuneCount = 0");
        sb.AppendLine("$wifiBandSet = 0");
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
        // Flow control: pause frames add latency under load - off for gaming, Rx+Tx for bulk
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
        sb.AppendLine("    $pmCommand = Get-Command Set-NetAdapterPowerManagement -EA SilentlyContinue");
        sb.AppendLine("    if ($pmCommand) {");
        sb.AppendLine("      $pmArgs = @{ Name=$n; NoRestart=$true; ErrorAction='SilentlyContinue' }");
        sb.AppendLine("      $pmWanted = @{ SelectiveSuspend='Disabled'; WakeOnMagicPacket='Disabled'; WakeOnPattern='Disabled'; DeviceSleepOnDisconnect='Disabled'; ArpOffload='Disabled'; NSOffload='Disabled'; D0PacketCoalescing='Disabled' }");
        sb.AppendLine("      foreach ($property in $pmWanted.Keys) { if ($pmCommand.Parameters.ContainsKey($property)) { $pmArgs[$property] = $pmWanted[$property] } }");
        sb.AppendLine("      Set-NetAdapterPowerManagement @pmArgs");
        sb.AppendLine("      if ($pmArgs.ContainsKey('D0PacketCoalescing')) { $packetCoalescingCount++; Log \"[Power] $n D0 packet coalescing disabled\" }");
        sb.AppendLine("    }");
        sb.AppendLine("  } catch { Log \"[Power] $n power-management tuning skipped: $($_.Exception.Message)\" }");
        // RSS: Microsoft - many wireless NICs do not support RSS
        sb.AppendLine("  if (-not $isWifi) {");
        sb.AppendLine("    Set-Adv $n '*RSS' 1");
        sb.AppendLine("    try { Set-NetAdapterRss -Name $n -Enabled $true -EA SilentlyContinue } catch {}");
        sb.AppendLine("    $rssQueueTarget = $null");
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
        sb.AppendLine("        $rssQueueTarget = [int]$pick");
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
        sb.AppendLine("    # DMA coalescing / adaptive IFS - latency killers when on");
        sb.AppendLine("    foreach ($kw in @('*DMACoalescing','DMACoalescing')) { Set-Adv $n $kw 0 }");
        sb.AppendLine("    try { Set-AdvDisplay $n 'DMA Coalescing' 'Disabled' | Out-Null } catch {}");
        sb.AppendLine("    try { Set-AdvDisplay $n 'Adaptive Inter-Frame Spacing' 'Disabled' | Out-Null } catch {}");
        sb.AppendLine("    try { Set-AdvDisplay $n 'Gigabit Lite' 'Disabled' | Out-Null } catch {}");
        sb.AppendLine("    try { Set-AdvDisplay $n 'Gigabit Master Slave Mode' 'Auto Detect' | Out-Null } catch {}");
        // Speed & Duplex / Wait for Link intentionally NOT written - forcing these
        // on dock/USB/odd NICs has bricked links that snapshot restore could not
        // always bring back before the user lost UI access.
        sb.AppendLine("    Log '[Eth] Speed & Duplex left at driver default (never force)'");
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
        sb.AppendLine("    # Jumbo: keep standard Ethernet (gaming) - keyword first (1514 = standard frame)");
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
        sb.AppendLine("    # Priority & VLAN: keep packet priority (QoS tags) - keyword 1 = packet priority enabled");
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
        sb.AppendLine("    # RSS placement and processor budget via supported Microsoft cmdlet parameters.");
        sb.AppendLine("    try {");
        sb.AppendLine("      $rssCommand = Get-Command Set-NetAdapterRss -EA Stop");
        sb.AppendLine("      # Some current Intel/Windows builds expose Enabled as null and reject");
        sb.AppendLine("      # Set-NetAdapterRss -Enabled even while RSS hashing is live. Enable via");
        sb.AppendLine("      # the dedicated cmdlet, then set only supported placement fields.");
        sb.AppendLine("      try { Enable-NetAdapterRss -Name $n -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("      $rssArgs = @{ Name=$n; Profile='" + rssProfile + "'; ErrorAction='Stop' }");
        sb.AppendLine("      if ($LogicalCpuCount -ge 4 -and $rssCommand.Parameters.ContainsKey('BaseProcessorNumber')) { $rssArgs.BaseProcessorNumber = 2 }");
        sb.AppendLine("      if ($rssCommand.Parameters.ContainsKey('MaxProcessors')) { $rssArgs.MaxProcessors = [Math]::Max(1, [Math]::Min([int]$RssQueueBudget, [int]$LogicalCpuCount - 2)) }");
        sb.AppendLine("      if ($rssQueueTarget -and $rssCommand.Parameters.ContainsKey('NumberOfReceiveQueues')) { $rssArgs.NumberOfReceiveQueues = [int]$rssQueueTarget }");
        sb.AppendLine("      Set-NetAdapterRss @rssArgs");
        sb.AppendLine("      $rssLive = Get-NetAdapterRss -Name $n -EA SilentlyContinue");
        sb.AppendLine("      $rssLiveOn = $rssLive -and ($rssLive.Enabled -eq $true -or $rssLive.IPv4HashEnabled -eq $true -or [int]$rssLive.RssProcessorArraySize -gt 0)");
        sb.AppendLine("      if (-not $rssLiveOn -or [string]$rssLive.Profile -ne '" + rssProfile + "') { throw 'RSS placement did not verify' }");
        sb.AppendLine("      $rssPolicyCount++; Log \"[RSS] $n profile=" + rssProfile + " processors=$($rssArgs.MaxProcessors) queues=$rssQueueTarget\"");
        sb.AppendLine("    } catch {");
        sb.AppendLine("      try { Set-NetAdapterRss -Name $n -Profile Closest -EA SilentlyContinue } catch {}");
        sb.AppendLine("      Log \"[RSS] $n adaptive placement fallback: $($_.Exception.Message)\"");
        sb.AppendLine("    }");
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
        // Wi-Fi: full gaming radio path (DisplayName + RegistryKeyword + powercfg above)
        sb.AppendLine("  if ($isWifi) {");
        sb.AppendLine("    $wifiTuneCount++");
        sb.AppendLine("    function Set-WifiOff($adapterName, [string[]]$hints) {");
        sb.AppendLine("      $pp = Find-AdvPropByName $adapterName $hints");
        sb.AppendLine("      if (-not $pp) { return $false }");
        sb.AppendLine("      foreach ($off in @('Disabled','Off','Disable','No','Maximum Performance','Highest','0')) {");
        sb.AppendLine("        if (@($pp.ValidDisplayValues).Count -eq 0 -or @($pp.ValidDisplayValues) -contains $off) {");
        sb.AppendLine("          try { Set-NetAdapterAdvancedProperty -Name $adapterName -DisplayName $pp.DisplayName -DisplayValue $off -NoRestart -EA SilentlyContinue; Log \"[Wi-Fi] $($pp.DisplayName) => $off\"; return $true } catch {}");
        sb.AppendLine("        }");
        sb.AppendLine("      }");
        sb.AppendLine("      return $false");
        sb.AppendLine("    }");
        sb.AppendLine("    function Set-WifiBest($adapterName, [string[]]$hints, [string[]]$prefer) {");
        sb.AppendLine("      $pp = Find-AdvPropByName $adapterName $hints");
        sb.AppendLine("      if (-not $pp) { return $false }");
        sb.AppendLine("      $vals = @($pp.ValidDisplayValues)");
        sb.AppendLine("      foreach ($want in $prefer) {");
        sb.AppendLine("        $hit = $vals | Where-Object { $_ -match ('(?i)' + [regex]::Escape($want)) } | Select-Object -First 1");
        sb.AppendLine("        if (-not $hit -and ($vals.Count -eq 0)) { $hit = $want }");
        sb.AppendLine("        if ($hit) {");
        sb.AppendLine("          try { Set-NetAdapterAdvancedProperty -Name $adapterName -DisplayName $pp.DisplayName -DisplayValue $hit -NoRestart -EA SilentlyContinue; Log \"[Wi-Fi] $($pp.DisplayName) => $hit\"; return $true } catch {}");
        sb.AppendLine("        }");
        sb.AppendLine("      }");
        sb.AppendLine("      return $false");
        sb.AppendLine("    }");
        sb.AppendLine("    function Set-WifiKw($adapterName, [string[]]$keywords, $value) {");
        sb.AppendLine("      foreach ($kw in $keywords) {");
        sb.AppendLine("        try {");
        sb.AppendLine("          $p = Get-NetAdapterAdvancedProperty -Name $adapterName -RegistryKeyword $kw -EA SilentlyContinue");
        sb.AppendLine("          if (-not $p) { continue }");
        sb.AppendLine("          Set-NetAdapterAdvancedProperty -Name $adapterName -RegistryKeyword $kw -RegistryValue $value -NoRestart -EA SilentlyContinue");
        sb.AppendLine("          Log \"[Wi-Fi] keyword $kw => $value\"");
        sb.AppendLine("          return $true");
        sb.AppendLine("        } catch {}");
        sb.AppendLine("      }");
        sb.AppendLine("      return $false");
        sb.AppendLine("    }");
        // Registry-keyword power-save / wake kill (locale-independent; Intel/Realtek/MediaTek/Qualcomm)
        sb.AppendLine("    foreach ($kw in @(");
        sb.AppendLine("      'PowerSaveMode','*PowerSaveMode','MIMOPowerSaveMode','uAPSDSupport','*uAPSDSupport',");
        sb.AppendLine("      '*DeviceSleepOnDisconnect','*PMWiFiRekeyOffload','*WakeOnMagicPacket','*WakeOnPattern',");
        sb.AppendLine("      'FatChannelIntolerant','*PacketCoalescing','UltraLowPowerMode','ULPMode'");
        sb.AppendLine("    )) { [void](Set-WifiKw $n @($kw) 0) }");
        // Power / coalescing / BT coexistence - always off for gaming
        sb.AppendLine("    foreach ($hint in @(");
        sb.AppendLine("      'MIMO Power Save','uAPSD support','uAPSD','Power Saving Mode','Power Saving','Power Save Mode','Power Save',");
        sb.AppendLine("      'Packet Coalescing','Ultra Low Power Mode','Ultra Low Power','Idle Power Save','Wireless Mode Power',");
        sb.AppendLine("      'System Idle Power Saver','Modern Standby WoWLAN','Wake on Magic Packet','Wake on Pattern Match',");
        sb.AppendLine("      'WoWLAN','Wake on WLAN','ARP offload for WoWLAN','NS offload for WoWLAN',");
        sb.AppendLine("      'Bluetooth Collaboration','Bluetooth AMP','Bluetooth Cooperation','Fat Channel Intolerant',");
        sb.AppendLine("      'Mixed Mode Protection','Intel(R) Throughput Enhancement','Throughput Booster' )) {");
        sb.AppendLine("      if ($hint -match '(?i)Throughput') { continue }");
        sb.AppendLine("      [void](Set-WifiOff $n @($hint))");
        sb.AppendLine("    }");
        sb.AppendLine("    foreach ($p in @(Get-NetAdapterAdvancedProperty -Name $n -EA SilentlyContinue)) {");
        sb.AppendLine("      if ($p.RegistryKeyword -match '(?i)power.?save|uapsd|mimo.?power|packet.?coalesc|ulp|IdlePower|WoW|WakeOn|Bluetooth|DeviceSleep') {");
        sb.AppendLine("        foreach ($off in @('Disabled','Off','0','Maximum Performance')) {");
        sb.AppendLine("          if (@($p.ValidDisplayValues).Count -eq 0 -or @($p.ValidDisplayValues) -contains $off) {");
        sb.AppendLine("            try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName $p.DisplayName -DisplayValue $off -NoRestart -EA SilentlyContinue; break } catch {}");
        sb.AppendLine("          }");
        sb.AppendLine("        }");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        // Transmit power highest
        sb.AppendLine("    [void](Set-WifiBest $n @('Transmit Power','Tx Power','Transmission Power','Output Power','Transmit Power Level') @('Highest','Maximum','100','5','Level 5'))");
        // Channel width: best / auto / 160 / 80
        sb.AppendLine("    [void](Set-WifiBest $n @('Channel Width','Channel Width for 5GHz','Channel Width for 5 GHz','802.11n Channel Width for band 2','Channel Width for 5.2GHz','VHT Channel Width') @('Auto','160 MHz','160','80 MHz','80','40','Best'))");
        sb.AppendLine("    [void](Set-WifiBest $n @('Channel Width for 2.4GHz','Channel Width for 2.4 GHz','802.11n Channel Width for band 1') @('Auto','20'))");
        // 802.11 mode - prefer latest
        sb.AppendLine("    [void](Set-WifiBest $n @('Wireless Mode','802.11a/b/g Wireless Mode','802.11 Mode','Wi-Fi Mode','Wireless Mode Selection') @('802.11be','802.11ax','802.11ac','6','5','Auto','Default'))");
        // MU-MIMO / OFDMA / Beamform - on when present
        sb.AppendLine("    [void](Set-WifiBest $n @('MU-MIMO','Multi-User MIMO') @('Enabled','On','Enable'))");
        sb.AppendLine("    [void](Set-WifiBest $n @('OFDMA','Orthogonal Frequency Division Multiple Access') @('Enabled','On','Enable','Auto'))");
        sb.AppendLine("    [void](Set-WifiBest $n @('Beamforming','Explicit Beamforming','Implicit Beamforming','Transmit Beamforming') @('Enabled','On','Enable'))");
        sb.AppendLine("    [void](Set-WifiBest $n @('BSS Color','BSS Coloring') @('Enabled','On','Enable','Auto'))");
        // Throughput booster only for highest-download preset
        if (!latency)
        {
            sb.AppendLine("    [void](Set-WifiBest $n @('Throughput Booster','Intel(R) Throughput Enhancement') @('Enabled','On','Enable'))");
        }
        else
        {
            sb.AppendLine("    [void](Set-WifiOff $n @('Throughput Booster','Intel(R) Throughput Enhancement'))");
        }
        // Preferred band + roam (DisplayName + keyword)
        sb.AppendLine("    $adapterWants6 = $wantBand6Live");
        sb.AppendLine("    foreach ($p in @(Get-NetAdapterAdvancedProperty -Name $n -EA SilentlyContinue)) {");
        sb.AppendLine("      $blob = \"$($p.DisplayName) $(($p.ValidDisplayValues) -join ' ')\"");
        sb.AppendLine("      if ($blob -match '(?i)6\\s*GHz|6GHz|Wi-?Fi\\s*6E') { $adapterWants6 = $true }");
        sb.AppendLine("    }");
        sb.AppendLine("    $bandProp = Find-AdvPropByName $n @('Preferred Band','Preferable Band','Band Preference','Preferred Band Selection','Preferred WLAN Band','Wireless Band Preference','Band Selection','Preferred Band (2.4/5/6 GHz)','802.11a/b/g Preferred Band')");
        sb.AppendLine("    if (-not $bandProp) {");
        sb.AppendLine("      $bandProp = @(Get-NetAdapterAdvancedProperty -Name $n -EA SilentlyContinue) | Where-Object {");
        sb.AppendLine("        $_.RegistryKeyword -match '(?i)preferred.?band|band.?pref|preferable.?band|PreferredBand' -or");
        sb.AppendLine("        [string]$_.DisplayName -match '(?i)prefer.*band|band.*prefer'");
        sb.AppendLine("      } | Select-Object -First 1");
        sb.AppendLine("    }");
        sb.AppendLine("    if ($bandProp) {");
        sb.AppendLine("      $vals = @($bandProp.ValidDisplayValues)");
        sb.AppendLine("      if ($vals.Count -eq 0 -and $bandProp.DisplayValue) { $vals = @($bandProp.DisplayValue) }");
        sb.AppendLine("      $pick = Select-BandDisplayValue -vals $vals -want6 $adapterWants6");
        sb.AppendLine("      if ($pick) {");
        sb.AppendLine("        try {");
        sb.AppendLine("          Set-NetAdapterAdvancedProperty -Name $n -DisplayName $bandProp.DisplayName -DisplayValue $pick -NoRestart -EA SilentlyContinue");
        sb.AppendLine("          $wifiBandSet++");
        sb.AppendLine("          Log \"[Wi-Fi] $($bandProp.DisplayName) => $pick (want6=$adapterWants6)\"");
        sb.AppendLine("        } catch { Log \"[Wi-Fi] band set failed: $($_.Exception.Message)\" }");
        sb.AppendLine("      } else { Log \"[Wi-Fi] no suitable band value in: $($vals -join ' | ')\" }");
        sb.AppendLine("    } else { Log '[Wi-Fi] no Preferred Band-like property on this driver' }");
        sb.AppendLine("    $roam = Find-AdvPropByName $n @('Roaming Aggressiveness','Roaming Sensitivity','Roam Aggressiveness','Roaming Aggressive','Roaming Aggressiveness Level')");
        sb.AppendLine("    if (-not $roam) {");
        sb.AppendLine("      $roam = @(Get-NetAdapterAdvancedProperty -Name $n -EA SilentlyContinue) | Where-Object {");
        sb.AppendLine("        $_.RegistryKeyword -match '(?i)roam' -or [string]$_.DisplayName -match '(?i)roam'");
        sb.AppendLine("      } | Select-Object -First 1");
        sb.AppendLine("    }");
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
        sb.AppendLine("if ($wifiTuneCount -gt 0) {");
        sb.AppendLine("  Report 'wifi-tune' 'ok' ('wifiAdapters=' + $wifiTuneCount + ' preferredBandSet=' + $wifiBandSet)");
        sb.AppendLine("} else {");
        sb.AppendLine("  Report 'wifi-tune' 'skip' 'no Wi-Fi adapter on this PC'");
        sb.AppendLine("}");
        sb.AppendLine("if ($rssBaseCount -gt 0) { Report 'rss-base' 'ok' ('BaseProcessorNumber=2 on ' + $rssBaseCount + ' adapter(s)') }");
        sb.AppendLine("else { Report 'rss-base' 'skip' 'no ethernet adapter or fewer than 4 logical processors' }");
        sb.AppendLine("if ($rssPolicyCount -gt 0) { Report 'rss-policy' 'ok' ('adaptive RSS placement on ' + $rssPolicyCount + ' adapter(s)') }");
        sb.AppendLine("else { Report 'rss-policy' 'skip' 'RSS policy unsupported or no Ethernet adapter' }");
        sb.AppendLine("if ($packetCoalescingCount -gt 0) { Report 'packet-coalescing' 'ok' 'D0 packet coalescing disabled on supported adapters' }");
        sb.AppendLine("else { Report 'packet-coalescing' 'skip' 'driver does not expose D0 packet coalescing' }");

        // --- Adapter bindings: ENABLE critical stack only. Never disable Client /
        // File Sharing / LLDP - that "gaming lean" path broke LAN recovery and
        // made Windows look bricked while Exo's TCP probe still passed.
        sb.AppendLine("""
function Set-AdapterBindings {
  $ads = @(Get-ExoPhysicalAdapters)
  $enable = @('ms_pacer','ms_tcpip','ms_tcpip6')
  foreach ($a in $ads) {
    $n = $a.Name
    foreach ($id in $enable) {
      try { Enable-NetAdapterBinding -Name $n -ComponentID $id -EA SilentlyContinue } catch {}
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
# Delivery Optimization - stop peer upload stealing bandwidth on gaming PCs
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
# Transition tunnels and NetBIOS stay stock (compat). SMB *protocol* is not disabled;
# only the legacy SMB bandwidth throttle is turned off (large copy / game share speed).
Log '[compat] transition tunnels and NetBIOS left unchanged'
try {
  Set-Dword 'HKLM:\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters' 'DisableBandwidthThrottling' 1
  Set-Dword 'HKLM:\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters' 'DisableBandwidthThrottling' 1
  Log '[SMB] DisableBandwidthThrottling=1 (workstation + server)'
  Report 'smb-throttle' 'ok' 'SMB bandwidth throttling disabled'
} catch {
  Report 'smb-throttle' 'fail' $_.Exception.Message
}
# LLMNR off (LAN multicast name resolution). NCSI active probe stays stock - that
# was the historical "No Internet" footgun, not LLMNR.
try {
  Set-Dword 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient' 'EnableMulticast' 0
  Log '[DNS] LLMNR disabled (EnableMulticast=0)'
  Report 'llmnr' 'ok' 'LLMNR multicast name resolution off'
} catch {
  Report 'llmnr' 'fail' $_.Exception.Message
}
# Multi-app UDP DSCP 46 (path-scoped by exe name). Discord owns its own policies;
# this covers Steam + common launcher/game clients when installed. Prefix Exo-Net-DSCP-*
# so Repair can remove only what we wrote.
try {
  $qosRoot = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\QoS'
  if (-not (Test-Path -LiteralPath $qosRoot)) { New-Item -Path $qosRoot -Force | Out-Null }
  $candidates = [System.Collections.Generic.List[object]]::new()
  $steamPaths = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Steam\steam.exe'),
    (Join-Path $env:ProgramFiles 'Steam\steam.exe'),
    (Join-Path $env:LOCALAPPDATA 'Steam\steam.exe')
  )
  foreach ($sp in $steamPaths) {
    if (Test-Path -LiteralPath $sp -PathType Leaf) {
      [void]$candidates.Add(@{ Exe = 'steam.exe'; Path = $sp })
      break
    }
  }
  $riotRoot = Join-Path $env:SystemDrive 'Riot Games'
  if (Test-Path -LiteralPath $riotRoot -PathType Container) {
    foreach ($name in @('RiotClientServices.exe', 'VALORANT-Win64-Shipping.exe', 'League of Legends.exe')) {
      $hit = Get-ChildItem -LiteralPath $riotRoot -Filter $name -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
      if ($hit) { [void]$candidates.Add(@{ Exe = $name; Path = $hit.FullName }) }
    }
  }
  foreach ($ep in @(
    (Join-Path $env:ProgramFiles 'Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe')
  )) {
    if (Test-Path -LiteralPath $ep -PathType Leaf) {
      [void]$candidates.Add(@{ Exe = 'EpicGamesLauncher.exe'; Path = $ep })
      break
    }
  }
  $fn = Get-ChildItem -LiteralPath (Join-Path $env:ProgramFiles 'Epic Games') -Filter 'FortniteClient-Win64-Shipping.exe' -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $fn) {
    $fn = Get-ChildItem -LiteralPath (Join-Path ${env:ProgramFiles(x86)} 'Epic Games') -Filter 'FortniteClient-Win64-Shipping.exe' -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
  }
  if ($fn) { [void]$candidates.Add(@{ Exe = 'FortniteClient-Win64-Shipping.exe'; Path = $fn.FullName }) }
  $written = 0
  $seenExe = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
  foreach ($c in @($candidates)) {
    $exe = [string]$c.Exe
    if (-not $seenExe.Add($exe)) { continue }
    $safe = ($exe -replace '[^\w\.\-]', '_')
    $pol = "Exo-Net-DSCP-$safe"
    $path = Join-Path $qosRoot $pol
    if (-not (Test-Path -LiteralPath $path)) { New-Item -Path $path -Force | Out-Null }
    foreach ($pair in @(
      @{ N = 'Version'; V = '1.0' },
      @{ N = 'Application Name'; V = $exe },
      @{ N = 'Protocol'; V = 'UDP' },
      @{ N = 'Local Port'; V = '*' },
      @{ N = 'Remote Port'; V = '*' },
      @{ N = 'Local IP'; V = '*' },
      @{ N = 'Remote IP'; V = '*' },
      @{ N = 'DSCP Value'; V = '46' },
      @{ N = 'Throttle Rate'; V = '-1' }
    )) {
      New-ItemProperty -LiteralPath $path -Name $pair.N -Value $pair.V -PropertyType String -Force -ErrorAction Stop | Out-Null
    }
    $written++
    Log ("[QoS] DSCP 46 policy $pol for $exe")
  }
  if ($written -gt 0) {
    Report 'multi-app-dscp' 'ok' ("DSCP 46 on $written installed gaming app(s)")
  } else {
    Report 'multi-app-dscp' 'skip' 'no Steam/Riot/Epic executables found for QoS policies'
  }
} catch {
  Report 'multi-app-dscp' 'fail' $_.Exception.Message
}
Log '[NCSI] left system default (active probe untouched)'
# DNS cache TTL overrides are NOT written. Legacy Exo pinned MaxCacheTtl=86400
# (stale records for up to 24h - the "dns cache" breakage users reported).
# Remove any leftover override so Windows honors the resolver's record TTLs.
Remove-Prop 'HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters' 'MaxCacheTtl'
Remove-Prop 'HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters' 'MaxNegativeCacheTtl'
Log '[DNS] cache TTL overrides removed (record TTLs honored)'
# Proxy AutoDetect / NCSI policy intentionally untouched (see NCSI note above).
Log '[proxy] AutoDetect left system default'
Set-AdapterBindings
Report 'bindings' 'ok'
""");

        // Prefer Ethernet 100% when linked. Metric must stick after Restart-NetAdapter:
        // re-stamp used to run before DHCP returned → "No usable Ethernet" → metric stayed ~20 auto.
        // Set metric on ANY Up Ethernet (IP not required); prefer adapters that already have IPv4.
        sb.AppendLine("""
function Set-EthMetrics {
  # Binding toggles can leave Status briefly non-Up. Retry + broad link detection.
  $okAny = $false
  for ($attempt = 1; $attempt -le 8; $attempt++) {
    $ads = @(Get-ExoPhysicalAdapters)
    # Up OR MediaConnected OR has non-APIPA IPv4 (cover driver Status flicker)
    $ethUp = @($ads | Where-Object {
      if (Test-IsWifiAdapter $_) { return $false }
      $st = [string]$_.Status
      if ($st -eq 'Up') { return $true }
      try {
        if ([string]$_.MediaConnectionState -eq 'Connected') { return $true }
      } catch {}
      $ipN = @(Get-NetIPAddress -InterfaceIndex $_.ifIndex -AddressFamily IPv4 -EA SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '169.254.*' }).Count
      return ($ipN -gt 0)
    })
    if ($ethUp.Count -eq 0) {
      Log "[Exo-NET] metric attempt $attempt/8: no Ethernet candidate yet"
      Start-Sleep -Milliseconds 500
      continue
    }
    $ranked = foreach ($e in $ethUp) {
      $hasIp = @(Get-NetIPAddress -InterfaceIndex $e.ifIndex -AddressFamily IPv4 -EA SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '169.254.*' }).Count -gt 0
      $spd = 0L
      try { $spd = [int64]$e.ReceiveLinkSpeed } catch { $spd = 0 }
      [pscustomobject]@{ A=$e; HasIp=$hasIp; Spd=$spd }
    }
    $ordered = @($ranked | Sort-Object @{Expression='HasIp';Descending=$true}, @{Expression='Spd';Descending=$true} | ForEach-Object { $_.A })
    $i = 0
    foreach ($e in $ordered) {
      if ($i -eq 0) { $metric = 1 } else { $metric = 5 + $i }
      foreach ($af in @('IPv4','IPv6')) {
        try {
          Set-NetIPInterface -InterfaceIndex $e.ifIndex -AddressFamily $af -AutomaticMetric Disabled -EA SilentlyContinue
          Set-NetIPInterface -InterfaceIndex $e.ifIndex -AddressFamily $af -InterfaceMetric $metric -EA SilentlyContinue
          Set-NetIPInterface -InterfaceAlias $e.Name -AddressFamily $af -AutomaticMetric Disabled -InterfaceMetric $metric -EA SilentlyContinue
        } catch {}
      }
      try { netsh interface ipv4 set interface interface=$($e.ifIndex) metric=$metric | Out-Null } catch {}
      try { netsh interface ipv6 set interface interface=$($e.ifIndex) metric=$metric | Out-Null } catch {}
      $live = $null; $auto = $null
      try {
        $mi = Get-NetIPInterface -InterfaceIndex $e.ifIndex -AddressFamily IPv4 -EA SilentlyContinue
        if ($mi) { $live = [int]$mi.InterfaceMetric; $auto = [string]$mi.AutomaticMetric }
      } catch {}
      Log "[NIC] Ethernet metric $($e.Name) => want $metric live=$live auto=$auto (attempt $attempt)"
      if ($null -ne $live -and [int]$live -eq $metric -and $auto -match 'Disabled') { $okAny = $true }
      elseif ($null -ne $live -and [int]$live -le 5) { $okAny = $true }
      $i++
    }
    if ($okAny) { break }
    Start-Sleep -Milliseconds 400
  }
  return $okAny
}
""");
        sb.AppendLine("$ethReadyOk = Set-EthMetrics");
        sb.AppendLine("$anyEth = @((Get-ExoPhysicalAdapters) | Where-Object { -not (Test-IsWifiAdapter $_) }).Count -gt 0");
        sb.AppendLine("if ($ethReadyOk) { Report 'eth-metrics' 'ok' }");
        sb.AppendLine("elseif (-not $anyEth) { Report 'eth-metrics' 'skip' 'no ethernet adapter (wifi-only PC)' }");
        sb.AppendLine("else { Report 'eth-metrics' 'fail' 'metric not verified (AutomaticMetric may still be on)' }");
        sb.AppendLine("Report 'prefix-policy' 'skip' 'Windows IPv4/IPv6 precedence retained'");
        // Analyze always chooses a healthy public resolver on this exact route.
        // Apply it to active physical adapters and register the matching encrypted
        // template when Windows supports automatic DoH. Repair restores the snapshot.
        sb.AppendLine("try {");
        sb.AppendLine("  $dnsTargets = @(Get-ExoPhysicalAdapters | Where-Object { [string]$_.Status -eq 'Up' -or @(Get-NetIPAddress -InterfaceIndex $_.ifIndex -AddressFamily IPv4 -EA SilentlyContinue).Count -gt 0 })");
        sb.AppendLine("  $dnsServers = @($ExoDnsV4 + $ExoDnsV6 | Where-Object { $_ })");
        sb.AppendLine("  foreach ($t in $dnsTargets) { Set-DnsClientServerAddress -InterfaceIndex $t.ifIndex -ServerAddresses $dnsServers -EA Stop }");
        sb.AppendLine("  $dohStatus = 'plain DNS (automatic DoH unavailable)'");
        sb.AppendLine("  if ($ExoDnsDohTemplate) {");
        sb.AppendLine("    $dohFail = @()");
        sb.AppendLine("    $dohErrors = @()");
        sb.AppendLine("    foreach ($svr in $dnsServers) {");
        sb.AppendLine("      $setDoh = ''");
        sb.AppendLine("      $dohVerified = $false");
        sb.AppendLine("      try {");
        sb.AppendLine("        if ((Get-Command Get-DnsClientDohServerAddress -EA SilentlyContinue) -and (Get-Command Add-DnsClientDohServerAddress -EA SilentlyContinue)) {");
        sb.AppendLine("          $beforeDoh = @(Get-DnsClientDohServerAddress -ServerAddress $svr -EA SilentlyContinue)");
        sb.AppendLine("          if ($beforeDoh.Count -gt 0 -and (Get-Command Set-DnsClientDohServerAddress -EA SilentlyContinue)) {");
        sb.AppendLine("            Set-DnsClientDohServerAddress -ServerAddress $svr -DohTemplate $ExoDnsDohTemplate -AutoUpgrade $true -AllowFallbackToUdp $true -EA Stop | Out-Null");
        sb.AppendLine("          } else {");
        sb.AppendLine("            Add-DnsClientDohServerAddress -ServerAddress $svr -DohTemplate $ExoDnsDohTemplate -AutoUpgrade $true -AllowFallbackToUdp $true -EA Stop | Out-Null");
        sb.AppendLine("          }");
        sb.AppendLine("          $liveDoh = @(Get-DnsClientDohServerAddress -ServerAddress $svr -EA SilentlyContinue | Select-Object -First 1)");
        sb.AppendLine("          $dohVerified = $liveDoh.Count -gt 0 -and [string]$liveDoh[0].DohTemplate -eq $ExoDnsDohTemplate -and [bool]$liveDoh[0].AutoUpgrade");
        sb.AppendLine("        }");
        sb.AppendLine("      } catch { $setDoh = $_.Exception.Message }");
        sb.AppendLine("      if (-not $dohVerified) {");
        sb.AppendLine("        try {");
        sb.AppendLine("          $beforeText = (netsh dnsclient show encryption server=$svr 2>&1 | Out-String)");
        sb.AppendLine("          if ($beforeText -match '(?i)Encryption settings for') {");
        sb.AppendLine("            $netshDoh = (netsh dnsclient set encryption server=$svr dohtemplate=$ExoDnsDohTemplate autoupgrade=yes udpfallback=yes 2>&1 | Out-String)");
        sb.AppendLine("          } else {");
        sb.AppendLine("            $netshDoh = (netsh dnsclient add encryption server=$svr dohtemplate=$ExoDnsDohTemplate autoupgrade=yes udpfallback=yes 2>&1 | Out-String)");
        sb.AppendLine("          }");
        sb.AppendLine("          $liveText = (netsh dnsclient show encryption server=$svr 2>&1 | Out-String)");
        sb.AppendLine("          $dohVerified = $liveText -match [regex]::Escape($ExoDnsDohTemplate) -and $liveText -match '(?im)^Auto-upgrade\\s*:\\s*yes\\s*$'");
        sb.AppendLine("          if (-not $dohVerified -and -not $setDoh) { $setDoh = (($netshDoh + ' ' + $liveText) -replace '\\s+',' ').Trim() }");
        sb.AppendLine("        } catch { if (-not $setDoh) { $setDoh = $_.Exception.Message } }");
        sb.AppendLine("      }");
        sb.AppendLine("      if (-not $dohVerified) {");
        sb.AppendLine("        $dohFail += $svr");
        sb.AppendLine("        $detail = ([string]$setDoh -replace '\\s+',' ').Trim()");
        sb.AppendLine("        if ($detail) { $dohErrors += ($svr + ': ' + $detail) }");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("    if ($dohFail.Count -gt 0) {");
        sb.AppendLine("      $dohStatus = 'selected by live test; encrypted DNS unavailable on this Windows build'");
        sb.AppendLine("      Log ('[DNS] automatic DoH unavailable for ' + ($dohFail -join ', ') + $(if ($dohErrors.Count) { ' - ' + ($dohErrors -join ' | ') } else { '' }))");
        sb.AppendLine("    } else { $dohStatus = 'selected by live test; automatic DoH active' }");
        sb.AppendLine("  }");
        sb.AppendLine("  Clear-DnsClientCache -EA SilentlyContinue");
        sb.AppendLine("  Report 'dns-auto' 'ok' ($ExoDnsProvider + ' - ' + $dohStatus)");
        sb.AppendLine("  Log ('[DNS] ' + $ExoDnsProvider + ' selected by Analyze: ' + ($dnsServers -join ', '))");
        sb.AppendLine("} catch { Report 'dns-auto' 'fail' $_.Exception.Message }");
        // ============================================================================
        // POWERCFG — wireless radio + PCIe ASPM + USB sel-suspend.
        // Was previously only snapshotted then "skip" — radio power-save stayed on,
        // so Wi-Fi "optimizer" looked applied but did nothing for latency on laptops.
        // GUIDs: Wireless Power Saving Mode / PCI Express Link State / USB selective suspend.
        // ============================================================================
        sb.AppendLine("""
$wifiPowerOk = 0; $pcieAspmOk = 0; $usbSsOk = 0
try {
  $schemeLine = (powercfg /getactivescheme 2>$null | Out-String)
  $scheme = $null
  if ($schemeLine -match 'GUID:\s*([0-9a-fA-F\-]{36})') { $scheme = $Matches[1] }
  if ($scheme) {
    # Wireless Adapter Settings \ Power Saving Mode = 0 (Maximum Performance) AC+DC
    $wSub = '19cbb8fa-5279-450e-9fac-8a3d5fedd0c1'
    $wSet = '12bbebe6-58d6-4636-95bb-3217ef867c1a'
    $qW = (powercfg /q $scheme $wSub $wSet 2>$null | Out-String)
    if ($qW -match 'Power Setting GUID') {
      powercfg /setacvalueindex $scheme $wSub $wSet 0 | Out-Null
      powercfg /setdcvalueindex $scheme $wSub $wSet 0 | Out-Null
      $wifiPowerOk = 1
      Log '[powercfg] Wireless Adapter Power Saving Mode = Maximum Performance (AC+DC)'
    } else {
      Log '[powercfg] Wireless Adapter Settings not exposed on this scheme/build'
    }
    # PCI Express \ Link State Power Management = 0 (Off) AC+DC
    $pSub = '501a4d13-42af-4429-9fd1-a8218c268e20'
    $pSet = 'ee12f906-d277-404b-b6da-e5fa1a576df5'
    $qP = (powercfg /q $scheme $pSub $pSet 2>$null | Out-String)
    if ($qP -match 'Power Setting GUID') {
      powercfg /setacvalueindex $scheme $pSub $pSet 0 | Out-Null
      powercfg /setdcvalueindex $scheme $pSub $pSet 0 | Out-Null
      $pcieAspmOk = 1
      Log '[powercfg] PCI Express Link State Power Management = Off (AC+DC)'
    }
    # USB \ USB selective suspend = 0 (Disabled) — AC always; DC when laptop (dongle NICs)
    $uSub = '2a737441-1930-4402-8d77-b2bebba308a3'
    $uSet = '48e6b7a6-50f5-4782-a5d4-53bb8f07e226'
    $qU = (powercfg /q $scheme $uSub $uSet 2>$null | Out-String)
    if ($qU -match 'Power Setting GUID') {
      powercfg /setacvalueindex $scheme $uSub $uSet 0 | Out-Null
      if ($IsLaptopHint -eq 1) { powercfg /setdcvalueindex $scheme $uSub $uSet 0 | Out-Null }
      $usbSsOk = 1
      Log ('[powercfg] USB selective suspend Off AC' + $(if ($IsLaptopHint -eq 1) { '+DC (laptop)' } else { '' }))
    }
    powercfg /setactive $scheme | Out-Null
  } else {
    Log '[powercfg] could not parse active scheme GUID'
  }
} catch { Log ('[powercfg] error: ' + $_.Exception.Message) }
$pcBits = @()
if ($wifiPowerOk -eq 1) { $pcBits += 'wifi-max-perf' }
if ($pcieAspmOk -eq 1) { $pcBits += 'pcie-aspm-off' }
if ($usbSsOk -eq 1) { $pcBits += 'usb-ss-off' }
if ($pcBits.Count -gt 0) {
  Report 'power-policy' 'ok' ($pcBits -join ', ')
} else {
  Report 'power-policy' 'skip' 'powercfg wireless/ASPM/USB settings not exposed'
}
""");
        // ============================================================================
        // ETHERNET-FIRST = METRICS ONLY. Never Disable-NetAdapter on Wi-Fi.
        // Disabling Wi-Fi after a brief Ethernet probe stranded users when the
        // cable/DHCP path later failed - Repair was unreachable without internet.
        // ============================================================================
        sb.AppendLine("if (" + preferEth + " -eq 1) {");
        sb.AppendLine("  $ads = @(Get-ExoPhysicalAdapters)");
        sb.AppendLine("  $ethAdapters = @($ads | Where-Object { -not (Test-IsWifiAdapter $_) -and ($_.Status -eq 'Up' -or [string]$_.MediaConnectionState -eq 'Connected') })");
        sb.AppendLine("  $wifiAdapters = @($ads | Where-Object { Test-IsWifiAdapter $_ })");
        sb.AppendLine("  if ($ethAdapters.Count -eq 0) {");
        sb.AppendLine("    # Wi-Fi only: ensure automatic metric is not stuck at raised 75 from a prior dual-NIC apply");
        sb.AppendLine("    foreach ($w in $wifiAdapters) {");
        sb.AppendLine("      try {");
        sb.AppendLine("        Set-NetIPInterface -InterfaceIndex $w.ifIndex -AddressFamily IPv4 -AutomaticMetric Enabled -EA SilentlyContinue");
        sb.AppendLine("        Set-NetIPInterface -InterfaceIndex $w.ifIndex -AddressFamily IPv6 -AutomaticMetric Enabled -EA SilentlyContinue");
        sb.AppendLine("        Log ('[NIC] Wi-Fi only: AutomaticMetric restored for ' + $w.Name)");
        sb.AppendLine("      } catch {}");
        sb.AppendLine("    }");
        sb.AppendLine("    Report 'wifi-disable' 'skip' 'wifi-only path - metrics left automatic (never disable wifi adapters)'");
        sb.AppendLine("  } else {");
        sb.AppendLine("    foreach ($w in $wifiAdapters) {");
        sb.AppendLine("      try {");
        sb.AppendLine("        Set-NetIPInterface -InterfaceIndex $w.ifIndex -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 75 -EA SilentlyContinue");
        sb.AppendLine("        Set-NetIPInterface -InterfaceIndex $w.ifIndex -AddressFamily IPv6 -AutomaticMetric Disabled -InterfaceMetric 75 -EA SilentlyContinue");
        sb.AppendLine("        Log ('[NIC] Wi-Fi metric raised (adapter STAYS ENABLED): ' + $w.Name)");
        sb.AppendLine("      } catch {}");
        sb.AppendLine("    }");
        sb.AppendLine("    Report 'wifi-disable' 'skip' 'metrics-only prefer-ethernet (never disable wifi adapters)'");
        sb.AppendLine("  }");
        sb.AppendLine("} else {");
        sb.AppendLine("  Report 'wifi-disable' 'skip' 'prefer-ethernet option off'");
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
        sb.AppendLine("  if (-not $metricOk) { Log '[NIC] WARN metric not verified after restart wait - last Set-EthMetrics attempt done' }");
        sb.AppendLine("} else {");
        sb.AppendLine("  Log '[Exo-NET] Ethernet restart skipped (user declined)'");
        sb.AppendLine("  # Still re-stamp once more so AutomaticMetric cannot race");
        sb.AppendLine("  Start-Sleep -Milliseconds 400");
        sb.AppendLine("  [void](Set-EthMetrics)");
        sb.AppendLine("}");

        // ============================================================================
        // POST-APPLY CONNECTIVITY CHECK + AUTO-ROLLBACK
        // Probe runs inside a FULL retry window (link renegotiation after NIC
        // advanced-property writes / adapter restart can take 5-20s+ with DHCP).
        // On failure: FULL snapshot restore (registry + advanced props + bindings +
        // TCP + metrics + adapter enable) - NOT the old Wi-Fi/metrics-only path that
        // left host-stack / NIC tweaks applied and stranded users.
        // ============================================================================
        sb.AppendLine("""
Log '[Exo-NET] Post-apply connectivity check (retry window - link renegotiation can take 5-20s)...'
$probeWindowSec = 60
$probeSw = [System.Diagnostics.Stopwatch]::StartNew()
$probeAttempts = 0
$probeLinkWaits = 0
$postOk = $false
$postVia = ''
while (-not $postOk -and $probeSw.Elapsed.TotalSeconds -lt $probeWindowSec) {
  # Link gate: while no physical adapter reports Up the stack is still renegotiating -
  # probing now burns the window on instant 'unreachable' failures.
  $linkUp = $true
  try { $linkUp = @(Get-NetAdapter -Physical -EA SilentlyContinue | Where-Object { $_.Status -eq 'Up' }).Count -gt 0 } catch { $linkUp = $true }
  if (-not $linkUp) {
    $probeLinkWaits++
    Log ('[probe] no adapter link yet at ' + [int]$probeSw.Elapsed.TotalSeconds + 's - waiting for renegotiation')
  } else {
    $probeAttempts++
    if (Test-ExoConnectivity) { $postOk = $true; $postVia = 'tcp-443' }
    elseif (Test-ExoDnsResolve) {
      $postOk = $true; $postVia = 'dns-resolve'
      Log '[probe] DNS resolve OK (TCP 443 anchors blocked - DNS round-trip proves connectivity)'
    }
  }
  if (-not $postOk) { Start-Sleep -Seconds 2 }
}
$probeSw.Stop()
$probeElapsedSec = [int][Math]::Round($probeSw.Elapsed.TotalSeconds)
$probeDetail = 'attempts=' + $probeAttempts + ' linkWaits=' + $probeLinkWaits + ' elapsed=' + $probeElapsedSec + 's window=' + $probeWindowSec + 's'
Log ('[probe] post-apply result ok=' + $postOk + ' ' + $probeDetail)
$didRollback = $false
$rollbackReason = ''
if (-not $postOk) {
  Report 'post-probe' 'fail' ('no tcp 443 / dns reachability after full retry window (' + $probeDetail + ')')
  Log '[Exo-NET] POST-APPLY CONNECTIVITY FAILED - rolling back path changes automatically'
  $didRollback = $true
  $rollbackReason = 'post-apply-connectivity-failed (' + $probeDetail + ')'
  # Always bring every physical adapter back (not only the Wi-Fi list we just disabled).
  foreach ($a in @(Get-NetAdapter -Physical -EA SilentlyContinue)) {
    try {
      if ([string]$a.Status -eq 'Disabled') {
        Enable-NetAdapter -Name $a.Name -Confirm:$false -EA SilentlyContinue
        Log ('[rollback] adapter re-enabled: ' + $a.Name)
      }
    } catch {}
  }
  foreach ($wn in @($ExoWifiDisabled)) {
    try {
      Enable-NetAdapter -Name $wn -Confirm:$false -EA SilentlyContinue
      Log "[rollback] Wi-Fi re-enabled: $wn"
    } catch { Log "[rollback] could not re-enable $wn" }
  }
  # FULL snapshot restore - Wi-Fi + metrics alone left host-stack / NIC props applied.
  $snapJson = $null
  try {
    if (Test-Path -LiteralPath $ExoSnapshotPath) {
      $snapJson = Get-Content -LiteralPath $ExoSnapshotPath -Raw | ConvertFrom-Json
    }
  } catch { $snapJson = $null }
  if ($snapJson) {
    Log '[rollback] FULL snapshot restore (registry, NIC advanced props, bindings, TCP, metrics)...'
    foreach ($rv in @($snapJson.regValues)) {
      try {
        $kind = [string]$rv.kind
        if ($kind -eq 'absent') {
          if (Test-Path -LiteralPath $rv.path) { Remove-ItemProperty -LiteralPath $rv.path -Name $rv.name -Force -EA SilentlyContinue }
        } else {
          if (-not (Test-Path -LiteralPath $rv.path)) { New-Item -Path $rv.path -Force -EA SilentlyContinue | Out-Null }
          if ($kind -eq 'Binary') {
            $bytes = [byte[]]@(@($rv.value) | ForEach-Object { [byte]$_ })
            Set-ItemProperty -LiteralPath $rv.path -Name $rv.name -Value $bytes -Type Binary -Force -EA SilentlyContinue
          } elseif ($kind -eq 'MultiString') {
            Set-ItemProperty -LiteralPath $rv.path -Name $rv.name -Value ([string[]]@($rv.value)) -Type MultiString -Force -EA SilentlyContinue
          } else {
            Set-ItemProperty -LiteralPath $rv.path -Name $rv.name -Value $rv.value -Type $kind -Force -EA SilentlyContinue
          }
        }
      } catch {}
    }
    Log '[rollback] registry restored from snapshot'
    $liveRb = @(Get-NetAdapter -EA SilentlyContinue)
    $touchedRb = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($ap in @($snapJson.advancedProps)) {
      $target = $liveRb | Where-Object { [string]$_.Name -eq [string]$ap.adapter } | Select-Object -First 1
      if (-not $target -and $ap.ifDesc) { $target = $liveRb | Where-Object { [string]$_.InterfaceDescription -eq [string]$ap.ifDesc } | Select-Object -First 1 }
      if (-not $target) { continue }
      $vals = @(([string]$ap.value) -split ',' | Where-Object { $_ -ne '' })
      if ($vals.Count -eq 0) { continue }
      try {
        Set-NetAdapterAdvancedProperty -Name $target.Name -RegistryKeyword ([string]$ap.keyword) -RegistryValue $vals -NoRestart -EA SilentlyContinue
        [void]$touchedRb.Add([string]$target.Name)
      } catch {}
    }
    foreach ($b in @($snapJson.bindings)) {
      $target = $liveRb | Where-Object { [string]$_.Name -eq [string]$b.adapter } | Select-Object -First 1
      if (-not $target -and $b.ifDesc) { $target = $liveRb | Where-Object { [string]$_.InterfaceDescription -eq [string]$b.ifDesc } | Select-Object -First 1 }
      if (-not $target) { continue }
      try {
        if ($b.enabled) { Enable-NetAdapterBinding -Name $target.Name -ComponentID ([string]$b.componentId) -EA SilentlyContinue }
        else { Disable-NetAdapterBinding -Name $target.Name -ComponentID ([string]$b.componentId) -EA SilentlyContinue }
      } catch {}
    }
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
    $inetRb = @($snapJson.tcpSettings) | Where-Object { [string]$_.settingName -eq 'Internet' } | Select-Object -First 1
    if ($inetRb -and $inetRb.autoTuningLevelLocal) {
      try { netsh int tcp set global autotuninglevel=$(([string]$inetRb.autoTuningLevelLocal).ToLowerInvariant()) | Out-Null } catch {}
    }
    $tcpRawRb = [string]$snapJson.netshTcpGlobalRaw
    foreach ($pair in @(
        @{ Label = 'RFC 1323 Timestamps'; Opt = 'timestamps'; Default = 'default' },
        @{ Label = 'Fast Open'; Opt = 'fastopen'; Default = 'enabled' },
        @{ Label = 'Fast Open Fallback'; Opt = 'fastopenfallback'; Default = 'enabled' },
        @{ Label = 'HyStart'; Opt = 'hystart'; Default = 'enabled' },
        @{ Label = 'Pacing Profile'; Opt = 'pacingprofile'; Default = 'off' },
        @{ Label = 'ECN Capability'; Opt = 'ecncapability'; Default = 'default' }
    )) {
      $val = $pair.Default
      $m = [regex]::Match($tcpRawRb, ('(?im)^\s*' + [regex]::Escape($pair.Label) + '\s*:\s*(\S+)'))
      if ($m.Success) { $val = $m.Groups[1].Value.ToLowerInvariant() }
      try { $null = (netsh int tcp set global "$($pair.Opt)=$val" 2>&1 | Out-String) } catch {}
    }
    # Advanced props with -NoRestart do not take effect until the NIC is bounced.
    foreach ($n in @($touchedRb)) {
      try {
        Restart-NetAdapter -Name $n -Confirm:$false -EA SilentlyContinue
        Log ('[rollback] adapter restarted so advanced props apply: ' + $n)
      } catch {}
    }
    Log '[rollback] full snapshot restore applied'
  } else {
    Log '[rollback] WARN snapshot missing - cannot full-restore; forcing critical bindings + stock DNS/NCSI'
  }
  # Hard safety net even when snapshot is incomplete/missing.
  foreach ($a in @(Get-NetAdapter -Physical -EA SilentlyContinue)) {
    foreach ($id in @('ms_tcpip','ms_tcpip6','ms_pacer')) {
      try { Enable-NetAdapterBinding -Name $a.Name -ComponentID $id -EA SilentlyContinue } catch {}
    }
    foreach ($af in @('IPv4','IPv6')) {
      try { Set-NetIPInterface -InterfaceIndex $a.ifIndex -AddressFamily $af -AutomaticMetric Enabled -EA SilentlyContinue } catch {}
    }
  }
  try { Remove-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\NetworkConnectivityStatusIndicator' -Name 'NoActiveProbe' -Force -EA SilentlyContinue } catch {}
  try { Clear-DnsClientCache -EA SilentlyContinue } catch {}
  try { ipconfig /renew | Out-Null } catch {}
  # Re-probe with its own window (Wi-Fi re-association + NIC bounce takes a few seconds)
  $rbSw = [System.Diagnostics.Stopwatch]::StartNew()
  while (-not $postOk -and $rbSw.Elapsed.TotalSeconds -lt 45) {
    Start-Sleep -Seconds 3
    if (Test-ExoConnectivity) { $postOk = $true }
    elseif (Test-ExoDnsResolve) { $postOk = $true }
  }
  $rbSw.Stop()
  if ($postOk) { Report 'rollback' 'ok' ('connectivity restored after full snapshot rollback (' + [int]$rbSw.Elapsed.TotalSeconds + 's)') }
  else { Report 'rollback' 'fail' 'connectivity still down after full restore - run Repair or Repair-Internet.ps1 -Hard' }
} else {
  Report 'post-probe' 'ok' ('reachable via ' + $postVia + ' (' + $probeDetail + ')')
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
if ($didRollback) { Report 'apply' 'fail' 'connectivity lost after apply - full snapshot rolled back automatically' }
else { Report 'apply' 'ok' }
""");
        sb.AppendLine("Log '[Exo-NET] DONE preset=" + preset + "'");
        sb.AppendLine("exit 0");
        return sb.ToString();
    }
}
