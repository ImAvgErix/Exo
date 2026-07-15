using Exo.Models;
using Exo.Services;

// Smoke tests drive shipped NetworkPeakLogic + NetworkApplyScriptBuilder.
// Exit 0 only if all cases pass. Args: optional log path.

var logPath = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "band-media-tests.log");
var lines = new List<string>();
var failed = 0;

void Log(string s)
{
    lines.Add(s);
    Console.WriteLine(s);
}

void Expect(string name, bool cond, string detail = "")
{
    if (cond) Log($"PASS  {name}");
    else
    {
        failed++;
        Log($"FAIL  {name}" + (string.IsNullOrEmpty(detail) ? "" : " :: " + detail));
    }
}

void ExpectEq(string name, string? got, string? expect)
{
    Expect(name, string.Equals(got, expect, StringComparison.Ordinal), $"got=[{got}] expect=[{expect}]");
}

Log("=== NetworkPeak.Smoke (shipped NetworkPeakLogic + NetworkApplyScriptBuilder) ===");
Log(DateTime.UtcNow.ToString("o"));

// --- Band selection: Prefer beats Only; 2.4 never wins when higher exists ---
ExpectEq("Intel classic prefer 5",
    NetworkPeakLogic.SelectBandDisplayValue(
        new[] { "No Preference", "Prefer 2.4GHz band", "Prefer 5GHz band" }, want6: false),
    "Prefer 5GHz band");

ExpectEq("Intel 6E prefer 6",
    NetworkPeakLogic.SelectBandDisplayValue(
        new[] { "No Preference", "Prefer 2.4GHz band", "Prefer 5GHz band", "Prefer 6GHz band" }, want6: true),
    "Prefer 6GHz band");

ExpectEq("Realtek prefer over only",
    NetworkPeakLogic.SelectBandDisplayValue(
        new[] { "Auto", "2.4GHz only", "5GHz only", "Prefer 5GHz" }, want6: false),
    "Prefer 5GHz");

ExpectEq("MediaTek spaced 6 preferred",
    NetworkPeakLogic.SelectBandDisplayValue(
        new[] { "Auto", "2.4 GHz preferred", "5 GHz preferred", "6 GHz preferred" }, want6: true),
    "6 GHz preferred");

ExpectEq("Prefer 6 beats only 6",
    NetworkPeakLogic.SelectBandDisplayValue(
        new[] { "6GHz only", "Prefer 6GHz band", "Prefer 5GHz band" }, want6: true),
    "Prefer 6GHz band");

ExpectEq("No 6 client picks 5 not 6",
    NetworkPeakLogic.SelectBandDisplayValue(
        new[] { "Prefer 6GHz band", "Prefer 5GHz band", "No Preference" }, want6: false),
    "Prefer 5GHz band");

ExpectEq("Weird 5.2 GHz",
    NetworkPeakLogic.SelectBandDisplayValue(
        new[] { "Auto", "Prefer 5.2 GHz", "Prefer 2.4 GHz" }, want6: false),
    "Prefer 5.2 GHz");

// Score: 2.4 only is worst
Expect("2.4 only score negative",
    NetworkPeakLogic.ScoreBandDisplayValue("2.4GHz only", want6: true) < 0);

// --- Media classification ---
Expect("802.3 is ethernet",
    !NetworkPeakLogic.IsWifiAdapter("802.3", "802.3", "Intel(R) Ethernet Controller I226-V", "Ethernet"));
Expect("Native 802.11 is wifi",
    NetworkPeakLogic.IsWifiAdapter("Native 802.11", "Native 802.11", "Intel(R) Wi-Fi 6E AX211", "Wi-Fi"));
Expect("Realtek USB wifi by desc",
    NetworkPeakLogic.IsWifiAdapter("", "", "Realtek 8822CE Wireless LAN 802.11ac PCI-E NIC", "Ethernet 2"));
Expect("Bluetooth not wifi primary",
    !NetworkPeakLogic.IsWifiAdapter("BlueTooth", "", "Bluetooth Device (Personal Area Network)", "Bluetooth Network Connection"));
Expect("Hyper-V not wifi",
    !NetworkPeakLogic.IsWifiAdapter("", "", "Hyper-V Virtual Ethernet Adapter", "vEthernet (Default Switch)"));

// --- Path policy ---
var ethUsable = NetworkPeakLogic.DecidePath(
    ethAvailable: true, ethUp: true, ethInUse: true,
    wifiAvailable: true, wifiUp: true,
    supports6Ghz: true, supports5Ghz: true, wifi6: true, wifi7: false);
Expect("eth usable disables wifi path", ethUsable.DisableWifiWhenPreferEth);
Expect("eth usable policy mentions disable", ethUsable.PolicyLine.Contains("disable", StringComparison.OrdinalIgnoreCase));
Expect("eth usable band target 6", ethUsable.PreferredBandTarget == "6GHz");

var ethNoIp = NetworkPeakLogic.DecidePath(
    ethAvailable: true, ethUp: true, ethInUse: false,
    wifiAvailable: true, wifiUp: true,
    supports6Ghz: false, supports5Ghz: true, wifi6: false, wifi7: false);
Expect("link no IP keeps wifi", ethNoIp.KeepWifiBecauseEthNoIp);
Expect("link no IP does not disable wifi flag", !ethNoIp.DisableWifiWhenPreferEth);
Expect("ShouldDisableWifi false when no IP",
    !NetworkPeakLogic.ShouldDisableWifi(true, ethInUse: false, wifiAvailable: true));
Expect("ShouldDisableWifi true when eth in use",
    NetworkPeakLogic.ShouldDisableWifi(true, ethInUse: true, wifiAvailable: true));

var wifiOnly = NetworkPeakLogic.DecidePath(
    ethAvailable: false, ethUp: false, ethInUse: false,
    wifiAvailable: true, wifiUp: true,
    supports6Ghz: false, supports5Ghz: true, wifi6: true, wifi7: false);
Expect("wifi only prefer 5", wifiOnly.PreferredBandTarget == "5GHz");
Expect("wifi only no disable eth flag", !wifiOnly.DisableWifiWhenPreferEth);

// Usable IPv4
Expect("APIPA not usable", !NetworkPeakLogic.IsUsableIpv4("169.254.1.2"));
Expect("private usable", NetworkPeakLogic.IsUsableIpv4("192.168.1.10"));
Expect("empty not usable", !NetworkPeakLogic.IsUsableIpv4(""));

// Band infer
bool b5 = false, b6 = false, ax = false, be = false;
NetworkPeakLogic.InferBandSupport("Prefer 6GHz band 802.11be", ref b5, ref b6, ref ax, ref be);
Expect("infer 6 from prefer 6", b6);
Expect("infer be from 802.11be", be);

// --- Apply script builder (shipped) both presets ---
var media = new NetworkMediaProfile
{
    ClientSupports6Ghz = true,
    ClientSupports5Ghz = true,
    EthernetInUse = true,
    WifiAvailable = true
};
var opts = new NetworkApplyOptions { PreferEthernetDisableWifi = true, RestartEthernet = false };

var latScript = NetworkApplyScriptBuilder.Build(NetworkPreset.LowestLatency, opts, media);
var thrScript = NetworkApplyScriptBuilder.Build(NetworkPreset.HighestThroughput, opts, media);

var (latOk, latIssues) = NetworkPeakLogic.AuditApplyScript(latScript, NetworkPreset.LowestLatency);
var (thrOk, thrIssues) = NetworkPeakLogic.AuditApplyScript(thrScript, NetworkPreset.HighestThroughput);
Expect("latency apply script audit", latOk, string.Join("; ", latIssues));
Expect("throughput apply script audit", thrOk, string.Join("; ", thrIssues));

// Diverge on tradeoffs
Expect("latency autotune normal", latScript.Contains("autotuninglevel=normal", StringComparison.OrdinalIgnoreCase));
Expect("throughput autotune experimental", thrScript.Contains("autotuninglevel=experimental", StringComparison.OrdinalIgnoreCase));
Expect("latency rsc disabled", latScript.Contains("rsc=disabled", StringComparison.OrdinalIgnoreCase));
Expect("throughput rsc enabled", thrScript.Contains("rsc=enabled", StringComparison.OrdinalIgnoreCase));
Expect("latency LSO 0", latScript.Contains("'*LsoV2IPv4' 0", StringComparison.Ordinal));
Expect("throughput LSO 1", thrScript.Contains("'*LsoV2IPv4' 1", StringComparison.Ordinal));
Expect("latency flow 0", latScript.Contains("'*FlowControl' 0", StringComparison.Ordinal));
Expect("throughput flow 3", thrScript.Contains("'*FlowControl' 3", StringComparison.Ordinal));
Expect("scripts force throttle 10",
    latScript.Contains("NetworkThrottlingIndex' 10", StringComparison.Ordinal) &&
    thrScript.Contains("NetworkThrottlingIndex' 10", StringComparison.Ordinal));
Expect("scripts force responsiveness 10",
    latScript.Contains("SystemResponsiveness' 10", StringComparison.Ordinal));
Expect("live band re-probe present", latScript.Contains("wantBand6Live", StringComparison.Ordinal));
Expect("disable wifi when eth ready", latScript.Contains("Disable-NetAdapter", StringComparison.Ordinal));
Expect("eth metric restamp after restart", latScript.Contains("Re-stamping", StringComparison.OrdinalIgnoreCase)
    || latScript.Contains("Set-EthMetrics", StringComparison.Ordinal));
Expect("LLTD bindings off", latScript.Contains("ms_lltdio", StringComparison.OrdinalIgnoreCase));
Expect("QoS pacer on", latScript.Contains("ms_pacer", StringComparison.OrdinalIgnoreCase));
Expect("DO download mode 0", latScript.Contains("DODownloadMode", StringComparison.Ordinal));
Expect("binding client off", latScript.Contains("ms_msclient", StringComparison.OrdinalIgnoreCase));
Expect("binding lldp off", latScript.Contains("ms_lldp", StringComparison.OrdinalIgnoreCase));
var repairScript = NetworkApplyScriptBuilder.BuildRepair();
Expect("repair script restores client", repairScript.Contains("ms_msclient", StringComparison.OrdinalIgnoreCase));
Expect("repair script automatic metric", repairScript.Contains("AutomaticMetric Enabled", StringComparison.OrdinalIgnoreCase));
var benchScript = NetworkApplyScriptBuilder.BuildBenchmark();
Expect("eth DMA coalescing off", latScript.Contains("DMACoalescing", StringComparison.OrdinalIgnoreCase)
    || latScript.Contains("DMA Coalescing", StringComparison.OrdinalIgnoreCase));
Expect("wifi transmit power", latScript.Contains("Transmit Power", StringComparison.OrdinalIgnoreCase));
Expect("wifi MU-MIMO", latScript.Contains("MU-MIMO", StringComparison.OrdinalIgnoreCase));
Expect("NetBIOS disable", latScript.Contains("NetbiosOptions", StringComparison.OrdinalIgnoreCase));
Expect("no wifi Restart-NetAdapter force",
    !System.Text.RegularExpressions.Regex.IsMatch(latScript, @"Restart-NetAdapter.*Wi-?Fi",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase));

// --- NIC helpers (network-only; no Windows Game Mode markers) ---
ExpectEq("vendor Intel I226",
    NetworkPeakLogic.ClassifyNicVendor("Intel(R) Ethernet Controller I226-V"), "Intel");
ExpectEq("vendor Realtek",
    NetworkPeakLogic.ClassifyNicVendor("Realtek PCIe GbE Family Controller"), "Realtek");
ExpectEq("vendor Killer",
    NetworkPeakLogic.ClassifyNicVendor("Killer E3100G 2.5 Gigabit Ethernet Controller"), "Killer");
Expect("buffer latency mid", NetworkPeakLogic.BufferStrategy(NetworkPreset.LowestLatency) == "mid");
Expect("buffer throughput max", NetworkPeakLogic.BufferStrategy(NetworkPreset.HighestThroughput) == "max");
// Physical cores (e.g. 6-core/12-thread): budget uses cores, not HT threads
Expect("rss 6-core latency",
    NetworkPeakLogic.RssQueueBudget(NetworkPreset.LowestLatency, 6) >= 2 &&
    NetworkPeakLogic.RssQueueBudget(NetworkPreset.LowestLatency, 6) <= 6);
Expect("rss throughput uses cores",
    NetworkPeakLogic.RssQueueBudget(NetworkPreset.HighestThroughput, 6) == 6);
Expect("prefer ipv4 on latency",
    NetworkPeakLogic.PreferIpv4First(NetworkPreset.LowestLatency, ethernetInUse: false));
Expect("lat script BufferStrategy", latScript.Contains("BufferStrategy", StringComparison.Ordinal));
Expect("lat script RssQueueBudget", latScript.Contains("RssQueueBudget", StringComparison.Ordinal));
Expect("lat script PreferIpv4", latScript.Contains("PreferIpv4First", StringComparison.Ordinal));
// IPv4-first is now documented prefix-policy precedence — the old metric+20 hack must be gone
Expect("prefix policy IPv4-first present",
    latScript.Contains("set prefixpolicy ::ffff:0:0/96 55 4", StringComparison.Ordinal));
Expect("old IPv6 metric+20 hack removed",
    !latScript.Contains("$want6 = $base + 20", StringComparison.Ordinal) &&
    !thrScript.Contains("$want6 = $base + 20", StringComparison.Ordinal));
Expect("prefix policy snapshot captured", latScript.Contains("prefixPolicies", StringComparison.Ordinal));
Expect("lat script no GameMode", !latScript.Contains("AutoGameModeEnabled", StringComparison.Ordinal));
Expect("lat script no HAGS", !latScript.Contains("HwSchMode", StringComparison.Ordinal));
Expect("lat script no PowerThrottlingOff", !latScript.Contains("PowerThrottlingOff", StringComparison.Ordinal));
Expect("lat script Intel extras", latScript.Contains("isIntel", StringComparison.OrdinalIgnoreCase)
    || latScript.Contains("Intel 2.5G", StringComparison.OrdinalIgnoreCase));

// KnobsFor consistency
var lk = NetworkPeakLogic.KnobsFor(NetworkPreset.LowestLatency);
var tk = NetworkPeakLogic.KnobsFor(NetworkPreset.HighestThroughput);
Expect("knobs diverge rsc", lk.Rsc != tk.Rsc);
Expect("knobs diverge lso", lk.Lso != tk.Lso);
Expect("knobs diverge autotune", lk.AutotuneNetsh != tk.AutotuneNetsh);
Expect("latency nagle off", lk.NagleOff);
Expect("throughput nagle not forced", !tk.NagleOff);

// --- Preset-aware NIC peak (no false-fail for intentional download settings) ---
var latPeakGood = NetworkPeakLogic.EvaluateNicPeak(
    NetworkPreset.LowestLatency,
    new NetworkPeakLogic.NicPeakFacts(FlowControlOn: false, InterruptModerationOn: false, IdleRestrictOn: true, SelectiveSuspendOn: false));
Expect("latency peak OK when flow/IM off idle on", latPeakGood.Ok);

var latPeakBadFc = NetworkPeakLogic.EvaluateNicPeak(
    NetworkPreset.LowestLatency,
    new NetworkPeakLogic.NicPeakFacts(FlowControlOn: true, InterruptModerationOn: false, IdleRestrictOn: true, SelectiveSuspendOn: null));
Expect("latency peak FAIL when flow on", !latPeakBadFc.Ok);

var thrPeakGood = NetworkPeakLogic.EvaluateNicPeak(
    NetworkPreset.HighestThroughput,
    new NetworkPeakLogic.NicPeakFacts(FlowControlOn: true, InterruptModerationOn: true, IdleRestrictOn: false, SelectiveSuspendOn: false));
Expect("throughput peak OK when flow/IM on idle off", thrPeakGood.Ok);

var thrPeakBad = NetworkPeakLogic.EvaluateNicPeak(
    NetworkPreset.HighestThroughput,
    new NetworkPeakLogic.NicPeakFacts(FlowControlOn: false, InterruptModerationOn: true, IdleRestrictOn: false, SelectiveSuspendOn: null));
Expect("throughput peak FAIL when flow off", !thrPeakBad.Ok);

// Same hardware state: latency OK + throughput FAIL for flow-off (not false-fail for thr intentional)
var sharedLatencyState = new NetworkPeakLogic.NicPeakFacts(false, false, true, null);
Expect("shared state OK for latency",
    NetworkPeakLogic.EvaluateNicPeak(NetworkPreset.LowestLatency, sharedLatencyState).Ok);
Expect("shared latency state NOT ok for throughput",
    !NetworkPeakLogic.EvaluateNicPeak(NetworkPreset.HighestThroughput, sharedLatencyState).Ok);

// Autotune must match knobs
Expect("autotune normal matches latency",
    NetworkPeakLogic.AutotuneMatches(NetworkPreset.LowestLatency, "normal"));
Expect("autotune normal does NOT match throughput",
    !NetworkPeakLogic.AutotuneMatches(NetworkPreset.HighestThroughput, "normal"));
Expect("autotune experimental matches throughput",
    NetworkPeakLogic.AutotuneMatches(NetworkPreset.HighestThroughput, "experimental"));
Expect("autotune experimental does NOT match latency",
    !NetworkPeakLogic.AutotuneMatches(NetworkPreset.LowestLatency, "experimental"));
Expect("LSO off matches latency", NetworkPeakLogic.LsoMatches(NetworkPreset.LowestLatency, false));
Expect("LSO on matches throughput", NetworkPeakLogic.LsoMatches(NetworkPreset.HighestThroughput, true));
Expect("LSO off does not match throughput", !NetworkPeakLogic.LsoMatches(NetworkPreset.HighestThroughput, false));
Expect("null LSO skips", NetworkPeakLogic.LsoMatches(NetworkPreset.HighestThroughput, null));
// Unknown autotune must skip (probe gap ≠ fail closed after apply)
Expect("autotune unknown skips", NetworkPeakLogic.AutotuneMatches(NetworkPreset.LowestLatency, "—"));
Expect("autotune empty skips", NetworkPeakLogic.AutotuneMatches(NetworkPreset.HighestThroughput, ""));
Expect("autotune first-token normal", NetworkPeakLogic.AutotuneMatches(NetworkPreset.LowestLatency, "normal  "));

// ============================================================================
// SAFETY LAYER — ordering assertions (snapshot -> mutations -> probe gate ->
// wifi disable -> rollback), new tweak markers, repair restore, benchmark,
// EXO_REPORT, Repair-Internet.ps1, and full PowerShell parse of every script.
// ============================================================================

int IdxOf(string hay, string needle) => hay.IndexOf(needle, StringComparison.Ordinal);

// (a) Ordering: snapshot capture precedes the first mutation
var snapDef = IdxOf(latScript, "function Save-ExoNetworkSnapshot");
var snapCall = IdxOf(latScript, "$snapshotOk = Save-ExoNetworkSnapshot");
var firstMutation = IdxOf(latScript, "Set-Dword $tcp 'DisableTaskOffload' 0");
Expect("snapshot fn defined", snapDef >= 0);
Expect("snapshot called before first mutation",
    snapCall >= 0 && firstMutation > snapCall,
    $"snapCall={snapCall} firstMutation={firstMutation}");
var abortIdx = IdxOf(latScript, "exit 2");
Expect("snapshot-failure abort before first mutation",
    abortIdx > snapDef && abortIdx < firstMutation,
    $"abort={abortIdx} firstMutation={firstMutation}");
Expect("snapshot never overwritten (pristine baseline kept)",
    latScript.Contains("pristine baseline", StringComparison.OrdinalIgnoreCase) &&
    IdxOf(latScript, "if (Test-Path -LiteralPath $ExoSnapshotPath)") >= 0);
Expect("snapshot has version + timestamp",
    latScript.Contains("snapshotVersion", StringComparison.Ordinal) &&
    latScript.Contains("timestampUtc", StringComparison.Ordinal));
Expect("snapshot covers advanced props + bindings + metrics + adapters + powercfg + ports + services",
    latScript.Contains("advancedProps", StringComparison.Ordinal) &&
    latScript.Contains("$snap.bindings = ", StringComparison.Ordinal) &&
    latScript.Contains("ipInterfaces", StringComparison.Ordinal) &&
    latScript.Contains("adapterStates", StringComparison.Ordinal) &&
    latScript.Contains("$snap.powercfg = ", StringComparison.Ordinal) &&
    latScript.Contains("dynamicPorts", StringComparison.Ordinal) &&
    latScript.Contains("$snap.services = ", StringComparison.Ordinal) &&
    latScript.Contains("$snap.rss = ", StringComparison.Ordinal));

// (a) Ordering: verified Ethernet gate — probe fn + eth-bound call precede Disable-NetAdapter
var probeDef = IdxOf(latScript, "function Test-ExoConnectivity");
var probeCall = IdxOf(latScript, "Test-ExoConnectivity -BindIp $ethIp");
// NOTE: needle must include ' -Name' so it cannot match Disable-NetAdapterLso/Rsc.
var wifiDisableIdx = IdxOf(latScript, "Disable-NetAdapter -Name");
Expect("wifi Disable-NetAdapter present", wifiDisableIdx >= 0);
Expect("probe fn precedes wifi disable", probeDef >= 0 && wifiDisableIdx > probeDef,
    $"probeDef={probeDef} wifiDisable={wifiDisableIdx}");
Expect("eth-bound probe call precedes wifi disable", probeCall >= 0 && wifiDisableIdx > probeCall,
    $"probeCall={probeCall} wifiDisable={wifiDisableIdx}");
Expect("probe binds TcpClient to eth IPv4",
    latScript.Contains("System.Net.Sockets.TcpClient", StringComparison.Ordinal) &&
    latScript.Contains("$client.Client.Bind", StringComparison.Ordinal) &&
    latScript.Contains("'1.1.1.1'", StringComparison.Ordinal) &&
    latScript.Contains("'8.8.8.8'", StringComparison.Ordinal) &&
    latScript.Contains("443", StringComparison.Ordinal));
Expect("probe has DNS resolve option", latScript.Contains("Test-ExoDnsResolve", StringComparison.Ordinal));
Expect("eth existence asserted before wifi disable",
    IdxOf(latScript, "$ethAdapters.Count -eq 0") >= 0 &&
    IdxOf(latScript, "$ethAdapters.Count -eq 0") < wifiDisableIdx);
Expect("disabled wifi recorded for state", latScript.Contains("$ExoWifiDisabled += $w.Name", StringComparison.Ordinal));

// (a) Ordering: post-apply rollback block exists after apply body
var rollbackIdx = IdxOf(latScript, "rolling back path changes automatically");
Expect("rollback block after wifi disable", rollbackIdx > wifiDisableIdx,
    $"rollback={rollbackIdx} wifiDisable={wifiDisableIdx}");
Expect("rollback re-enables wifi", latScript.Contains("Enable-NetAdapter -Name $wn", StringComparison.Ordinal));
Expect("rollback restores metrics from snapshot",
    rollbackIdx >= 0 &&
    latScript.IndexOf("interface metrics restored from snapshot", StringComparison.Ordinal) > rollbackIdx);
Expect("apply-state json written", latScript.Contains("network-apply-state.json", StringComparison.Ordinal) &&
    latScript.Contains("rollbackReason", StringComparison.Ordinal));

// (b) Markers for every new tweak (+ preset divergence)
Expect("timestamps disabled both",
    latScript.Contains("timestamps=disabled", StringComparison.Ordinal) &&
    thrScript.Contains("timestamps=disabled", StringComparison.Ordinal));
Expect("fastopen both",
    latScript.Contains("fastopen=enabled", StringComparison.Ordinal) &&
    thrScript.Contains("fastopen=enabled", StringComparison.Ordinal) &&
    latScript.Contains("fastopenfallback=enabled", StringComparison.Ordinal) &&
    thrScript.Contains("fastopenfallback=enabled", StringComparison.Ordinal));
Expect("pacingprofile off latency only",
    latScript.Contains("pacingprofile=off", StringComparison.Ordinal) &&
    !thrScript.Contains("pacingprofile=off", StringComparison.Ordinal));
Expect("hystart disabled latency only",
    latScript.Contains("hystart=disabled", StringComparison.Ordinal) &&
    !thrScript.Contains("hystart=disabled", StringComparison.Ordinal));
Expect("uro disabled latency only + 26100 gate",
    latScript.Contains("uro=disabled", StringComparison.Ordinal) &&
    latScript.Contains("26100", StringComparison.Ordinal) &&
    !thrScript.Contains("uro=disabled", StringComparison.Ordinal));
Expect("ecn diverges per preset",
    latScript.Contains("ecncapability=disabled", StringComparison.Ordinal) &&
    thrScript.Contains("ecncapability=enabled", StringComparison.Ordinal));
Expect("RTO tightening latency only",
    latScript.Contains("-InitialRtoMs 1000", StringComparison.Ordinal) &&
    latScript.Contains("-MinRtoMs 300", StringComparison.Ordinal) &&
    !thrScript.Contains("-InitialRtoMs 1000", StringComparison.Ordinal));
Expect("MaxSyn + NonSackRttResiliency both presets",
    latScript.Contains("-MaxSynRetransmissions 2", StringComparison.Ordinal) &&
    thrScript.Contains("-MaxSynRetransmissions 2", StringComparison.Ordinal) &&
    latScript.Contains("-NonSackRttResiliency Disabled", StringComparison.Ordinal) &&
    thrScript.Contains("-NonSackRttResiliency Disabled", StringComparison.Ordinal));
Expect("DNS ServiceProvider priorities",
    latScript.Contains("LocalPriority' 4", StringComparison.Ordinal) &&
    latScript.Contains("HostsPriority' 5", StringComparison.Ordinal) &&
    latScript.Contains("DnsPriority' 6", StringComparison.Ordinal) &&
    latScript.Contains("NetbtPriority' 7", StringComparison.Ordinal));
Expect("DoSvc demand-start + snapshot of StartType",
    latScript.Contains("Set-Service -Name 'DoSvc' -StartupType Manual", StringComparison.Ordinal) &&
    latScript.Contains("startType", StringComparison.Ordinal));
Expect("BITS throttle policy removed only if present",
    latScript.Contains("EnableBITSMaxBandwidth", StringComparison.Ordinal));
Expect("RSS BaseProcessorNumber 2 gated on >=4 CPUs",
    latScript.Contains("BaseProcessorNumber 2", StringComparison.Ordinal) &&
    latScript.Contains("$LogicalCpuCount -ge 4", StringComparison.Ordinal));
Expect("RegistryKeyword-first adapter writes",
    latScript.Contains("'*FlowControl'", StringComparison.Ordinal) &&
    latScript.Contains("'*SpeedDuplex'", StringComparison.Ordinal) &&
    latScript.Contains("'*JumboPacket'", StringComparison.Ordinal) &&
    latScript.Contains("'*PriorityVLANTag'", StringComparison.Ordinal) &&
    latScript.Contains("'*InterruptModeration'", StringComparison.Ordinal) &&
    latScript.Contains("'*LsoV2IPv6'", StringComparison.Ordinal) &&
    latScript.Contains("'*WakeOnMagicPacket'", StringComparison.Ordinal));
Expect("deeper adapter power kill keywords",
    latScript.Contains("AdvancedEEE", StringComparison.Ordinal) &&
    latScript.Contains("GreenEthernet", StringComparison.Ordinal) &&
    latScript.Contains("ULPMode", StringComparison.Ordinal) &&
    latScript.Contains("SipsEnabled", StringComparison.Ordinal));
Expect("virtual/VPN adapters excluded by adapter facts",
    latScript.Contains("Get-ExoPhysicalAdapters", StringComparison.Ordinal) &&
    latScript.Contains("-not $_.Virtual", StringComparison.Ordinal) &&
    latScript.Contains("WireGuard", StringComparison.Ordinal));
Expect("netsh build gating helper (skip-with-reason)",
    latScript.Contains("Invoke-ExoNetshGlobal", StringComparison.Ordinal) &&
    latScript.Contains("not supported on this build", StringComparison.Ordinal));

// (e) EXO_REPORT structured lines
Expect("EXO_REPORT emitter present", latScript.Contains("EXO_REPORT:", StringComparison.Ordinal));
foreach (var step in new[]
{
    "'snapshot'", "'registry-host'", "'mmcss'", "'qos-psched'", "'powercfg'", "'tcp-globals'",
    "'tcp-timestamps'", "'tcp-fastopen'", "'tcp-pacing'", "'tcp-hystart'", "'udp-uro'", "'tcp-ecn'",
    "'tcp-settings'", "'dynamic-ports'", "'nagle'", "'dns-priorities'", "'adapters'", "'rss-base'",
    "'bindings'", "'background-quiet'", "'prefix-policy'", "'eth-metrics'", "'wifi-disable'",
    "'post-probe'", "'rollback'", "'apply'"
})
{
    // Steps are reported either directly (Report '<step>' ...) or through the
    // build-gated netsh helper (Invoke-ExoNetshGlobal '<step>' ...), which calls
    // Report internally with the same step name.
    bool Reported(string script) =>
        script.Contains("Report " + step, StringComparison.Ordinal) ||
        script.Contains("Invoke-ExoNetshGlobal " + step, StringComparison.Ordinal);
    Expect($"report step {step} in both presets", Reported(latScript) && Reported(thrScript));
}
var sampleLog = string.Join('\n', new[]
{
    "2026-01-01T00:00:00 EXO_REPORT:snapshot|ok",
    "2026-01-01T00:00:01 EXO_REPORT:udp-uro|skip:requires Windows 11 24H2 (build 26100+)",
    "2026-01-01T00:00:02 EXO_REPORT:wifi-disable|ok:disabled after verified probe: Wi-Fi",
    "2026-01-01T00:00:03 EXO_REPORT:rollback|fail:connectivity still down - run Repair or Repair-Internet.ps1",
});
var parsedReport = NetworkPeakLogic.ParseApplyReport(sampleLog);
Expect("report parser count", parsedReport.Count == 4, $"got {parsedReport.Count}");
Expect("report parser ok step", parsedReport[0].Name == "snapshot" && parsedReport[0].Status == "ok");
Expect("report parser skip reason",
    parsedReport[1].Status == "skip" && parsedReport[1].Reason.Contains("26100", StringComparison.Ordinal));
Expect("report parser fail reason",
    parsedReport[3].Status == "fail" && parsedReport[3].Reason.Contains("Repair-Internet", StringComparison.Ordinal));

// Benchmark script + parser
Expect("benchmark pings both anchors",
    benchScript.Contains("ping.exe -n 10", StringComparison.Ordinal) &&
    benchScript.Contains("'1.1.1.1'", StringComparison.Ordinal) &&
    benchScript.Contains("'8.8.8.8'", StringComparison.Ordinal));
Expect("benchmark DNS resolve timing",
    benchScript.Contains("Measure-Command", StringComparison.Ordinal) &&
    benchScript.Contains("Resolve-DnsName", StringComparison.Ordinal));
Expect("benchmark single JSON line", benchScript.Contains("EXO_BENCH:", StringComparison.Ordinal) &&
    benchScript.Contains("ConvertTo-Json -Compress", StringComparison.Ordinal));
var benchParsed = NetworkPeakLogic.TryParseBenchmark(
    "noise\nEXO_BENCH:{\"ok\":true,\"pingP50Ms\":12.5,\"pingP95Ms\":18,\"jitterMs\":1.2,\"dnsMs\":22.7,\"samples\":20,\"timestampUtc\":\"2026-01-01T00:00:00Z\"}\n");
Expect("benchmark parser values",
    benchParsed is { Ok: true, Samples: 20 } &&
    Math.Abs(benchParsed.PingP50Ms - 12.5) < 0.001 &&
    Math.Abs(benchParsed.DnsMs - 22.7) < 0.001,
    benchParsed is null ? "null" : $"p50={benchParsed.PingP50Ms}");
Expect("benchmark parser rejects garbage", NetworkPeakLogic.TryParseBenchmark("no marker here") is null);

// (c) BuildRepair: snapshot-driven true restore + fallback stock reset
Expect("repair reads snapshot json",
    repairScript.Contains("network-snapshot.json", StringComparison.Ordinal) &&
    repairScript.Contains("ConvertFrom-Json", StringComparison.Ordinal));
Expect("repair restores registry values incl absent removal",
    repairScript.Contains("$snap.regValues", StringComparison.Ordinal) &&
    repairScript.Contains("'absent'", StringComparison.Ordinal));
Expect("repair restores advanced props by keyword",
    repairScript.Contains("$snap.advancedProps", StringComparison.Ordinal) &&
    repairScript.Contains("-RegistryKeyword ([string]$ap.keyword)", StringComparison.Ordinal));
Expect("repair restores bindings + metrics + rss + powercfg + ports + prefixpolicy + services",
    repairScript.Contains("$snap.bindings", StringComparison.Ordinal) &&
    repairScript.Contains("$snap.ipInterfaces", StringComparison.Ordinal) &&
    repairScript.Contains("$snap.rss", StringComparison.Ordinal) &&
    repairScript.Contains("$snap.powercfg", StringComparison.Ordinal) &&
    repairScript.Contains("$snap.dynamicPorts", StringComparison.Ordinal) &&
    repairScript.Contains("$snap.prefixPolicies", StringComparison.Ordinal) &&
    repairScript.Contains("$snap.services", StringComparison.Ordinal));
Expect("repair re-enables adapters recorded enabled",
    repairScript.Contains("$snap.adapterStates", StringComparison.Ordinal) &&
    repairScript.Contains("Enable-NetAdapter", StringComparison.Ordinal));
Expect("repair deletes snapshot only on full success",
    repairScript.Contains("$restoreFailures -eq 0", StringComparison.Ordinal) &&
    repairScript.Contains("snapshot kept for retry", StringComparison.OrdinalIgnoreCase));
Expect("repair fallback path marker",
    repairScript.Contains("no-snapshot-fallback-stock-reset", StringComparison.Ordinal) &&
    repairScript.Contains("APPROXIMATE", StringComparison.Ordinal));
Expect("repair always re-enables wifi regardless of path",
    repairScript.Contains("Wi-Fi re-enabled", StringComparison.Ordinal) &&
    repairScript.Contains("'wifi-reenable' 'ok'", StringComparison.Ordinal));
Expect("repair emits EXO_REPORT", repairScript.Contains("EXO_REPORT:", StringComparison.Ordinal));

// (d) Repair-Internet.ps1 standalone rescue at repo root
string? repoRoot = null;
var probeDir = new DirectoryInfo(Environment.CurrentDirectory);
while (probeDir is not null)
{
    if (File.Exists(Path.Combine(probeDir.FullName, "Repair-Internet.ps1")) ||
        File.Exists(Path.Combine(probeDir.FullName, "Exo.sln")))
    {
        repoRoot = probeDir.FullName;
        break;
    }
    probeDir = probeDir.Parent;
}
if (repoRoot is null)
{
    var baseProbe = new DirectoryInfo(AppContext.BaseDirectory);
    while (baseProbe is not null)
    {
        if (File.Exists(Path.Combine(baseProbe.FullName, "Exo.sln")))
        {
            repoRoot = baseProbe.FullName;
            break;
        }
        baseProbe = baseProbe.Parent;
    }
}
var rescuePath = repoRoot is null ? null : Path.Combine(repoRoot, "Repair-Internet.ps1");
Expect("Repair-Internet.ps1 exists at repo root", rescuePath is not null && File.Exists(rescuePath),
    $"repoRoot={repoRoot}");
if (rescuePath is not null && File.Exists(rescuePath))
{
    var rescue = File.ReadAllText(rescuePath);
    Expect("rescue restores from snapshot", rescue.Contains("network-snapshot.json", StringComparison.Ordinal));
    Expect("rescue re-enables adapters", rescue.Contains("Enable-NetAdapter", StringComparison.Ordinal));
    Expect("rescue self-elevates", rescue.Contains("RunAs", StringComparison.Ordinal));
    Expect("rescue has stock fallback", rescue.Contains("stock reset", StringComparison.OrdinalIgnoreCase));
    Expect("rescue clears exo network state",
        rescue.Contains("network-apply-state.json", StringComparison.Ordinal) &&
        rescue.Contains("network-optimizer.json", StringComparison.Ordinal));
    Expect("rescue supports irm|iex bootstrap", rescue.Contains("| iex", StringComparison.Ordinal));
}

// (f) Parse EVERY generated script with the PowerShell language parser (zero errors)
string? RunPs(string args)
{
    foreach (var shell in new[] { "pwsh", "powershell" })
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = shell,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) continue;
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(60_000);
            return stdout;
        }
        catch { }
    }
    return null;
}

var parseDir = Path.Combine(Path.GetTempPath(), "exo-netsmoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(parseDir);
try
{
    var scriptsToParse = new (string Name, string Text)[]
    {
        ("apply-lowest-latency.ps1", latScript),
        ("apply-highest-throughput.ps1", thrScript),
        ("repair.ps1", repairScript),
        ("benchmark.ps1", benchScript),
    };
    foreach (var (name, text) in scriptsToParse)
        File.WriteAllText(Path.Combine(parseDir, name), text);
    var psCmd =
        "$dir = '" + parseDir.Replace("'", "''") + "'; " +
        "foreach ($f in (Get-ChildItem -LiteralPath $dir -Filter *.ps1)) { " +
        "$t = $null; $e = $null; " +
        "[void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$t, [ref]$e); " +
        "Write-Output ($f.Name + '=' + @($e).Count) }";
    var parseOut = RunPs("-NoProfile -Command \"" + psCmd.Replace("\"", "\\\"") + "\"");
    Expect("powershell available for parse check", parseOut is not null);
    if (parseOut is not null)
    {
        foreach (var (name, _) in scriptsToParse)
        {
            var marker = name + "=0";
            Expect($"parse zero errors: {name}",
                parseOut.Contains(marker, StringComparison.OrdinalIgnoreCase),
                parseOut.Replace('\n', ' ').Replace('\r', ' '));
        }
    }
}
finally
{
    try { Directory.Delete(parseDir, recursive: true); } catch { }
}

Log($"=== SUMMARY failed={failed} ===");
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
File.WriteAllLines(logPath, lines);
Console.WriteLine("Wrote " + logPath);
Environment.Exit(failed == 0 ? 0 : 1);
