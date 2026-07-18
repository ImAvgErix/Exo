using System.Text;

namespace Exo.Services;

public static partial class NetworkApplyScriptBuilder
{
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
  # Folklore DNS cache TTL overrides never come back - even when the pristine
  # baseline happened to contain them (legacy Exo pinned MaxCacheTtl=86400).
  Remove-Prop 'HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters' 'MaxCacheTtl'
  Remove-Prop 'HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters' 'MaxNegativeCacheTtl'
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
  # -NoRestart writes driver settings without applying them; we bounce touched
  # adapters below so Repair actually undoes the NIC tweaks that break links.
  $advFail = 0
  $touchedAdv = New-Object 'System.Collections.Generic.HashSet[string]'
  foreach ($ap in @($snap.advancedProps)) {
    $target = Resolve-ExoAdapter ([string]$ap.adapter) ([string]$ap.ifDesc)
    if (-not $target) { continue }
    $vals = @(([string]$ap.value) -split ',' | Where-Object { $_ -ne '' })
    if ($vals.Count -eq 0) { continue }
    try {
      Set-NetAdapterAdvancedProperty -Name $target.Name -RegistryKeyword ([string]$ap.keyword) -RegistryValue $vals -NoRestart -EA SilentlyContinue
      [void]$touchedAdv.Add([string]$target.Name)
    } catch { $advFail++ }
  }
  Report 'restore-advanced-props' $(if ($advFail -eq 0) { 'ok' } else { 'fail' }) $(if ($advFail -gt 0) { "$advFail keyword(s) failed" } else { '' })
  if ($advFail -gt 0) { $restoreFailures += $advFail }
  foreach ($n in @($touchedAdv)) {
    try {
      Restart-NetAdapter -Name $n -Confirm:$false -EA SilentlyContinue
      Log ('[repair] adapter restarted so advanced props apply: ' + $n)
    } catch {}
  }
  if ($touchedAdv.Count -gt 0) { Start-Sleep -Seconds 4 }
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
  # --- 6b) Per-adapter DNS servers + DoH registrations (privacy feature) ---
  if ($snap.dnsServers) {
    foreach ($ds in @($snap.dnsServers)) {
      try {
        $t = $null
        if ($ds.ifIndex) { $t = Get-NetAdapter -InterfaceIndex ([int]$ds.ifIndex) -EA SilentlyContinue }
        if (-not $t -and $ds.name) { $t = Get-NetAdapter -Name ([string]$ds.name) -EA SilentlyContinue }
        if (-not $t) { continue }
        $v4 = @($ds.ipv4 | Where-Object { $_ })
        $v6 = @($ds.ipv6 | Where-Object { $_ })
        if ($v4.Count -gt 0) { Set-DnsClientServerAddress -InterfaceIndex $t.ifIndex -AddressFamily IPv4 -ServerAddresses $v4 -EA SilentlyContinue }
        else { Set-DnsClientServerAddress -InterfaceIndex $t.ifIndex -AddressFamily IPv4 -ResetServerAddresses -EA SilentlyContinue }
        if ($v6.Count -gt 0) { Set-DnsClientServerAddress -InterfaceIndex $t.ifIndex -AddressFamily IPv6 -ServerAddresses $v6 -EA SilentlyContinue }
        else { Set-DnsClientServerAddress -InterfaceIndex $t.ifIndex -AddressFamily IPv6 -ResetServerAddresses -EA SilentlyContinue }
      } catch {}
    }
    Report 'restore-dns' 'ok'
  } else {
    Report 'restore-dns' 'skip' 'snapshot predates dns capture'
  }
  # DoH: remove only registrations that were absent before apply (leave user's own intact)
  try {
    $priorDoh = [string]$snap.dohRaw
    foreach ($svr in @('1.1.1.1','1.0.0.1','2606:4700:4700::1111','2606:4700:4700::1001')) {
      if ($priorDoh -notmatch [regex]::Escape($svr)) {
        netsh dns delete encryption server=$svr 2>&1 | Out-Null
      }
    }
  } catch {}
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
  # --- 8) Adapter power management (exact supported values) ---
  $powerRestoreFailures = 0
  foreach ($pm in @($snap.powerManagement)) {
    try {
      $command = Get-Command Set-NetAdapterPowerManagement -EA SilentlyContinue
      if (-not $command) { continue }
      $args = @{ Name = [string]$pm.adapter; NoRestart = $true; ErrorAction = 'SilentlyContinue' }
      foreach ($property in @('D0PacketCoalescing','ArpOffload','DeviceSleepOnDisconnect','NSOffload','RsnRekeyOffload','SelectiveSuspend','WakeOnMagicPacket','WakeOnPattern')) {
        $value = $pm.$property
        if ($command.Parameters.ContainsKey($property) -and [string]$value -in @('Enabled','Disabled')) {
          $args[$property] = [string]$value
        }
      }
      Set-NetAdapterPowerManagement @args
    } catch { $powerRestoreFailures++; $restoreFailures++ }
  }
  Report 'restore-adapter-power' $(if ($powerRestoreFailures -eq 0) { 'ok' } else { 'fail' }) $(if ($powerRestoreFailures) { "$powerRestoreFailures adapter(s) failed" } else { '' })
  # --- 9) RSS config (Enabled + placement + queue budget) ---
  $rssRestoreFailures = 0
  foreach ($r in @($snap.rss)) {
    try {
      $command = Get-Command Set-NetAdapterRss -EA SilentlyContinue
      $args = @{ Name = [string]$r.adapter; Enabled = [bool]$r.enabled; ErrorAction = 'SilentlyContinue' }
      if ([bool]$r.enabled) {
        if ($command.Parameters.ContainsKey('BaseProcessorNumber')) { $args.BaseProcessorNumber = [int]$r.baseProcessorNumber }
        if ($r.profile -and $command.Parameters.ContainsKey('Profile')) { $args.Profile = [string]$r.profile }
        if ($r.maxProcessors -and $command.Parameters.ContainsKey('MaxProcessors')) { $args.MaxProcessors = [int]$r.maxProcessors }
        if ($r.receiveQueues -and $command.Parameters.ContainsKey('NumberOfReceiveQueues')) { $args.NumberOfReceiveQueues = [int]$r.receiveQueues }
      }
      Set-NetAdapterRss @args
    } catch { $rssRestoreFailures++; $restoreFailures++ }
  }
  Report 'restore-rss' $(if ($rssRestoreFailures -eq 0) { 'ok' } else { 'fail' }) $(if ($rssRestoreFailures) { "$rssRestoreFailures adapter(s) failed" } else { '' })
  # --- 10) powercfg values (AC/DC indexes exactly as recorded) ---
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
    # Undo folklore ServiceProvider priorities if present
    try {
      $sp = 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider'
      if (Test-Path $sp) {
        Set-ItemProperty $sp -Name LocalPriority -Value 499 -Type DWord -Force -EA SilentlyContinue
        Set-ItemProperty $sp -Name HostsPriority -Value 500 -Type DWord -Force -EA SilentlyContinue
        Set-ItemProperty $sp -Name DnsPriority -Value 2000 -Type DWord -Force -EA SilentlyContinue
        Set-ItemProperty $sp -Name NetbtPriority -Value 2001 -Type DWord -Force -EA SilentlyContinue
      }
    } catch {}
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
# ALWAYS (both paths): re-enable EVERY disabled physical adapter, force critical
# bindings (IPv4/IPv6/QoS), clear DNS, renew DHCP. Old repair only re-enabled
# Wi-Fi by name heuristic and left advanced props unapplied (-NoRestart).
# ============================================================================
$allPhys = @(Get-NetAdapter -Physical -EA SilentlyContinue)
foreach ($a in $allPhys) {
  try {
    if ([string]$a.Status -eq 'Disabled') {
      Enable-NetAdapter -Name $a.Name -Confirm:$false -EA SilentlyContinue
      Log ('[repair] adapter re-enabled: ' + $a.Name)
    }
  } catch {}
  foreach ($id in @('ms_tcpip','ms_tcpip6','ms_pacer')) {
    try { Enable-NetAdapterBinding -Name $a.Name -ComponentID $id -EA SilentlyContinue } catch {}
  }
}
Report 'wifi-reenable' 'ok'
try { Clear-DnsClientCache -EA SilentlyContinue } catch {}
try { ipconfig /renew | Out-Null } catch {}
Start-Sleep -Seconds 3
$repairProbe = $false
$rpSw = [System.Diagnostics.Stopwatch]::StartNew()
while (-not $repairProbe -and $rpSw.Elapsed.TotalSeconds -lt 30) {
  if (Test-ExoConnectivity) { $repairProbe = $true }
  elseif (Test-ExoDnsResolve) { $repairProbe = $true }
  if (-not $repairProbe) { Start-Sleep -Seconds 2 }
}
$rpSw.Stop()
if ($repairProbe) {
  Report 'post-probe' 'ok'
  Remove-Item -LiteralPath (Join-Path $ExoDir 'network-optimizer.json') -Force -EA SilentlyContinue
  Log '[Exo-NET-REPAIR] DONE'
  exit 0
}
# Do NOT auto-run winsock/ip reset. That path is not snapshot-undoable and made
# bad situations worse. Explicit Repair-Internet.ps1 -Hard remains available.
Report 'post-probe' 'fail' 'no tcp 443 / dns after repair - run Repair-Internet.ps1 -Hard then reboot'
Report 'hard-reset' 'skip' 'not automatic - use Repair-Internet.ps1 -Hard explicitly'
Remove-Item -LiteralPath $ExoApplyStatePath -Force -EA SilentlyContinue
Remove-Item -LiteralPath (Join-Path $ExoDir 'network-optimizer.json') -Force -EA SilentlyContinue
Log '[Exo-NET-REPAIR] DONE - still offline; hard reset NOT applied automatically (exit 1)'
exit 1
""");
        return sb.ToString();
    }
}
