using Exo.Models;
using Exo.Services;

// Smoke tests drive shipped NetworkLogic + NetworkApplyScriptBuilder.
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

Log("=== Network.Smoke (shipped NetworkLogic + NetworkApplyScriptBuilder) ===");
Log(DateTime.UtcNow.ToString("o"));

// --- Band selection: Prefer beats Only; 2.4 never wins when higher exists ---
ExpectEq("Intel classic prefer 5",
    NetworkLogic.SelectBandDisplayValue(
        new[] { "No Preference", "Prefer 2.4GHz band", "Prefer 5GHz band" }, want6: false),
    "Prefer 5GHz band");

ExpectEq("Intel 6E prefer 6",
    NetworkLogic.SelectBandDisplayValue(
        new[] { "No Preference", "Prefer 2.4GHz band", "Prefer 5GHz band", "Prefer 6GHz band" }, want6: true),
    "Prefer 6GHz band");

ExpectEq("Realtek prefer over only",
    NetworkLogic.SelectBandDisplayValue(
        new[] { "Auto", "2.4GHz only", "5GHz only", "Prefer 5GHz" }, want6: false),
    "Prefer 5GHz");

ExpectEq("MediaTek spaced 6 preferred",
    NetworkLogic.SelectBandDisplayValue(
        new[] { "Auto", "2.4 GHz preferred", "5 GHz preferred", "6 GHz preferred" }, want6: true),
    "6 GHz preferred");

ExpectEq("Prefer 6 beats only 6",
    NetworkLogic.SelectBandDisplayValue(
        new[] { "6GHz only", "Prefer 6GHz band", "Prefer 5GHz band" }, want6: true),
    "Prefer 6GHz band");

ExpectEq("No 6 client picks 5 not 6",
    NetworkLogic.SelectBandDisplayValue(
        new[] { "Prefer 6GHz band", "Prefer 5GHz band", "No Preference" }, want6: false),
    "Prefer 5GHz band");

ExpectEq("Weird 5.2 GHz",
    NetworkLogic.SelectBandDisplayValue(
        new[] { "Auto", "Prefer 5.2 GHz", "Prefer 2.4 GHz" }, want6: false),
    "Prefer 5.2 GHz");

// Score: 2.4 only is worst
Expect("2.4 only score negative",
    NetworkLogic.ScoreBandDisplayValue("2.4GHz only", want6: true) < 0);

// --- Media classification ---
Expect("802.3 is ethernet",
    !NetworkLogic.IsWifiAdapter("802.3", "802.3", "Intel(R) Ethernet Controller I226-V", "Ethernet"));
Expect("Native 802.11 is wifi",
    NetworkLogic.IsWifiAdapter("Native 802.11", "Native 802.11", "Intel(R) Wi-Fi 6E AX211", "Wi-Fi"));
Expect("Realtek USB wifi by desc",
    NetworkLogic.IsWifiAdapter("", "", "Realtek 8822CE Wireless LAN 802.11ac PCI-E NIC", "Ethernet 2"));
Expect("Bluetooth not wifi primary",
    !NetworkLogic.IsWifiAdapter("BlueTooth", "", "Bluetooth Device (Personal Area Network)", "Bluetooth Network Connection"));
Expect("Hyper-V not wifi",
    !NetworkLogic.IsWifiAdapter("", "", "Hyper-V Virtual Ethernet Adapter", "vEthernet (Default Switch)"));

// --- Path policy ---
var ethUsable = NetworkLogic.DecidePath(
    ethAvailable: true, ethUp: true, ethInUse: true,
    wifiAvailable: true, wifiUp: true,
    supports6Ghz: true, supports5Ghz: true, wifi6: true, wifi7: false);
Expect("eth usable never disables wifi path", !ethUsable.DisableWifiWhenPreferEth);
Expect("eth usable policy is metrics-only",
    ethUsable.PolicyLine.Contains("stays enabled", StringComparison.OrdinalIgnoreCase) ||
    ethUsable.PolicyLine.Contains("higher metric", StringComparison.OrdinalIgnoreCase));
Expect("eth usable band target 6", ethUsable.PreferredBandTarget == "6GHz");

var ethNoIp = NetworkLogic.DecidePath(
    ethAvailable: true, ethUp: true, ethInUse: false,
    wifiAvailable: true, wifiUp: true,
    supports6Ghz: false, supports5Ghz: true, wifi6: false, wifi7: false);
Expect("link no IP keeps wifi", ethNoIp.KeepWifiBecauseEthNoIp);
Expect("link no IP does not disable wifi flag", !ethNoIp.DisableWifiWhenPreferEth);
Expect("ShouldDisableWifi always false (no IP)",
    !NetworkLogic.ShouldDisableWifi(true, ethInUse: false, wifiAvailable: true));
Expect("ShouldDisableWifi always false (eth in use)",
    !NetworkLogic.ShouldDisableWifi(true, ethInUse: true, wifiAvailable: true));

var wifiOnly = NetworkLogic.DecidePath(
    ethAvailable: false, ethUp: false, ethInUse: false,
    wifiAvailable: true, wifiUp: true,
    supports6Ghz: false, supports5Ghz: true, wifi6: true, wifi7: false);
Expect("wifi only prefer 5", wifiOnly.PreferredBandTarget == "5GHz");
Expect("wifi only no disable eth flag", !wifiOnly.DisableWifiWhenPreferEth);

// Usable IPv4
Expect("APIPA not usable", !NetworkLogic.IsUsableIpv4("169.254.1.2"));
Expect("private usable", NetworkLogic.IsUsableIpv4("192.168.1.10"));
Expect("empty not usable", !NetworkLogic.IsUsableIpv4(""));

// Loss is an idle-path metric. Missed ICMP while the benchmark deliberately
// saturates download/upload must never be folded into the connection-loss label.
Expect("idle loss clean", NetworkLogic.CalculateIdlePacketLossPercent(24, 24) == 0);
Expect("idle loss one miss", Math.Abs(NetworkLogic.CalculateIdlePacketLossPercent(24, 23) - (100d / 24d)) < 0.001);
Expect("idle loss bounds successes", NetworkLogic.CalculateIdlePacketLossPercent(24, 30) == 0);
Expect("idle loss no attempts", NetworkLogic.CalculateIdlePacketLossPercent(0, 0) == 100);

// Band infer
bool b5 = false, b6 = false, ax = false, be = false;
NetworkLogic.InferBandSupport("Prefer 6GHz band 802.11be", ref b5, ref b6, ref ax, ref be);
Expect("infer 6 from prefer 6", b6);
Expect("infer be from 802.11be", be);

// --- Apply script builder (shipped) both presets ---
var media = new NetworkMediaProfile
{
    ClientSupports6Ghz = true,
    ClientSupports5Ghz = true,
    EthernetInUse = true,
    WifiAvailable = true,
    PrimaryLinkSpeedBps = 1_000_000_000
};
var opts = new NetworkApplyOptions { PreferEthernetDisableWifi = true, RestartEthernet = false };

var latScript = NetworkApplyScriptBuilder.Build(NetworkPreset.LowestLatency, opts, media);
var thrScript = NetworkApplyScriptBuilder.Build(NetworkPreset.HighestThroughput, opts, media);

var (latOk, latIssues) = NetworkLogic.AuditApplyScript(latScript, NetworkPreset.LowestLatency);
var (thrOk, thrIssues) = NetworkLogic.AuditApplyScript(thrScript, NetworkPreset.HighestThroughput);
Expect("latency apply script audit", latOk, string.Join("; ", latIssues));
Expect("throughput apply script audit", thrOk, string.Join("; ", thrIssues));

// Diverge on tradeoffs
Expect("latency autotune normal", latScript.Contains("autotuninglevel=normal", StringComparison.OrdinalIgnoreCase));
Expect("throughput autotune normal", thrScript.Contains("autotuninglevel=normal", StringComparison.OrdinalIgnoreCase));
Expect("latency rsc disabled", latScript.Contains("rsc=disabled", StringComparison.OrdinalIgnoreCase));
Expect("throughput rsc enabled", thrScript.Contains("rsc=enabled", StringComparison.OrdinalIgnoreCase));
Expect("latency LSO 0", latScript.Contains("'*LsoV2IPv4' 0", StringComparison.Ordinal));
Expect("throughput LSO 1", thrScript.Contains("'*LsoV2IPv4' 1", StringComparison.Ordinal));
Expect("latency flow 0", latScript.Contains("'*FlowControl' 0", StringComparison.Ordinal));
Expect("throughput flow 3", thrScript.Contains("'*FlowControl' 3", StringComparison.Ordinal));
Expect("scripts leave global multimedia scheduler alone",
    !latScript.Contains("Set-Dword $mm 'NetworkThrottlingIndex'", StringComparison.Ordinal) &&
    !latScript.Contains("Set-Dword $mm 'SystemResponsiveness'", StringComparison.Ordinal) &&
    !thrScript.Contains("Set-Dword $mm 'NetworkThrottlingIndex'", StringComparison.Ordinal) &&
    !thrScript.Contains("Set-Dword $mm 'SystemResponsiveness'", StringComparison.Ordinal));
Expect("scripts leave global QoS reservation alone",
    !latScript.Contains("Set-Dword $ps 'NonBestEffortLimit'", StringComparison.Ordinal) &&
    !thrScript.Contains("Set-Dword $ps 'NonBestEffortLimit'", StringComparison.Ordinal));
Expect("live band re-probe present", latScript.Contains("wantBand6Live", StringComparison.Ordinal));
Expect("never disable wifi adapters",
    latScript.Contains("never disable wifi adapters", StringComparison.OrdinalIgnoreCase) &&
    !latScript.Contains("Disable-NetAdapter -Name", StringComparison.Ordinal));
Expect("eth metric restamp after restart", latScript.Contains("Re-stamping", StringComparison.OrdinalIgnoreCase)
    || latScript.Contains("Set-EthMetrics", StringComparison.Ordinal));
Expect("QoS pacer on", latScript.Contains("ms_pacer", StringComparison.OrdinalIgnoreCase));
Expect("DO download mode 0", latScript.Contains("DODownloadMode", StringComparison.Ordinal));
Expect("bindings enable critical only (no client/lldp disable)",
    latScript.Contains("ms_tcpip", StringComparison.OrdinalIgnoreCase) &&
    !latScript.Contains("$disable = @('ms_msclient'", StringComparison.Ordinal));
var repairScript = NetworkApplyScriptBuilder.BuildRepair();
Expect("repair script restores stock bindings fallback", repairScript.Contains("ms_msclient", StringComparison.OrdinalIgnoreCase));
Expect("repair script automatic metric", repairScript.Contains("AutomaticMetric Enabled", StringComparison.OrdinalIgnoreCase));
var benchScript = NetworkApplyScriptBuilder.BuildBenchmark();
Expect("eth DMA coalescing off", latScript.Contains("DMACoalescing", StringComparison.OrdinalIgnoreCase)
    || latScript.Contains("DMA Coalescing", StringComparison.OrdinalIgnoreCase));
Expect("wifi transmit power", latScript.Contains("Transmit Power", StringComparison.OrdinalIgnoreCase));
Expect("wifi MU-MIMO", latScript.Contains("MU-MIMO", StringComparison.OrdinalIgnoreCase));
Expect("NetBIOS is not changed by apply",
    !latScript.Contains("Set-Dword $nb 'NetbiosOptions'", StringComparison.OrdinalIgnoreCase) &&
    !latScript.Contains("Set-ItemProperty -LiteralPath $nb", StringComparison.OrdinalIgnoreCase));
Expect("no wifi Restart-NetAdapter force",
    !System.Text.RegularExpressions.Regex.IsMatch(latScript, @"Restart-NetAdapter.*Wi-?Fi",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase));

// --- Analyze-selected encrypted DNS contract ---
var dohScript = NetworkApplyScriptBuilder.Build(NetworkPreset.LowestLatency,
    new NetworkApplyOptions
    {
        DnsProvider = "Google",
        DnsPrimary = "8.8.8.8",
        DnsSecondary = "8.8.4.4",
        DnsPrimaryV6 = "2001:4860:4860::8888",
        DnsSecondaryV6 = "2001:4860:4860::8844",
        DnsOverHttpsTemplate = "https://dns.google/dns-query"
    }, media);
Expect("selected DNS provider baked", dohScript.Contains("$ExoDnsProvider = 'Google'", StringComparison.Ordinal));
Expect("DoH registers encryption", dohScript.Contains("Add-DnsClientDohServerAddress", StringComparison.Ordinal));
Expect("DoH updates existing registrations", dohScript.Contains("Set-DnsClientDohServerAddress", StringComparison.Ordinal));
Expect("DoH verifies live registration", dohScript.Contains("Get-DnsClientDohServerAddress", StringComparison.Ordinal)
    && dohScript.Contains("AutoUpgrade", StringComparison.Ordinal));
Expect("DoH uses analyzed template", dohScript.Contains("dns.google/dns-query", StringComparison.OrdinalIgnoreCase));
Expect("DoH pins analyzed resolvers", dohScript.Contains("8.8.8.8", StringComparison.Ordinal)
    && dohScript.Contains("2001:4860:4860::8888", StringComparison.Ordinal));
Expect("DoH capability gated by shipped cmdlets", dohScript.Contains("Get-Command Get-DnsClientDohServerAddress", StringComparison.Ordinal));
Expect("DoH falls back to correct netsh dnsclient context",
    dohScript.Contains("netsh dnsclient add encryption", StringComparison.OrdinalIgnoreCase) &&
    dohScript.Contains("netsh dnsclient show encryption", StringComparison.OrdinalIgnoreCase) &&
    !dohScript.Contains("netsh dns add encryption", StringComparison.OrdinalIgnoreCase));
Expect("DoH reports automatic selection", dohScript.Contains("dns-auto", StringComparison.Ordinal));
Expect("DoH provider failure keeps fastest DNS active and truthful",
    dohScript.Contains("encrypted DNS unavailable on this Windows build", StringComparison.Ordinal) &&
    !dohScript.Contains("Windows rejected automatic DoH", StringComparison.Ordinal) &&
    !dohScript.Contains("throw ('DoH registration failed", StringComparison.Ordinal));
Expect("default apply always has safe resolver", latScript.Contains("$ExoDnsProvider = 'Cloudflare'", StringComparison.Ordinal)
    && latScript.Contains("dns-auto", StringComparison.Ordinal));
// Snapshot must capture per-adapter DNS + existing DoH so Repair can truly restore.
Expect("apply snapshot captures adapter DNS", latScript.Contains("$snap.dnsServers", StringComparison.Ordinal));
Expect("apply snapshot captures typed DoH registrations", latScript.Contains("$snap.dohServers", StringComparison.Ordinal)
    && latScript.Contains("$snap.exoDohServers", StringComparison.Ordinal)
    && latScript.Contains("netsh dnsclient show encryption", StringComparison.OrdinalIgnoreCase));
Expect("repair restores DNS from snapshot", repairScript.Contains("restore-dns", StringComparison.Ordinal)
    && repairScript.Contains("snap.dnsServers", StringComparison.Ordinal));
Expect("repair preserves prior DoH and prunes only Exo-added DoH",
    repairScript.Contains("snap.dohServers", StringComparison.Ordinal)
    && repairScript.Contains("snap.exoDohServers", StringComparison.Ordinal)
    && repairScript.Contains("snap.dohRaw", StringComparison.Ordinal)
    && repairScript.Contains("Remove-DnsClientDohServerAddress", StringComparison.Ordinal)
    && repairScript.Contains("netsh dnsclient delete encryption", StringComparison.OrdinalIgnoreCase));
// DNS cache TTL folklore is gone: apply removes overrides; repair + rescue never bring them back.
Expect("apply never writes DNS cache TTL overrides",
    !latScript.Contains("Set-Dword 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Dnscache\\Parameters'", StringComparison.Ordinal) &&
    latScript.Contains("Remove-Prop 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Dnscache\\Parameters' 'MaxCacheTtl'", StringComparison.Ordinal));
Expect("repair removes TTL overrides even from baseline",
    repairScript.Contains("Folklore DNS cache TTL", StringComparison.Ordinal));

// --- NIC helpers (network-only; no Windows Game Mode markers) ---
ExpectEq("vendor Intel I226",
    NetworkLogic.ClassifyNicVendor("Intel(R) Ethernet Controller I226-V"), "Intel");
ExpectEq("vendor Realtek",
    NetworkLogic.ClassifyNicVendor("Realtek PCIe GbE Family Controller"), "Realtek");
ExpectEq("vendor Killer",
    NetworkLogic.ClassifyNicVendor("Killer E3100G 2.5 Gigabit Ethernet Controller"), "Killer");
Expect("buffer latency mid", NetworkLogic.BufferStrategy(NetworkPreset.LowestLatency) == "mid");
Expect("buffer throughput max", NetworkLogic.BufferStrategy(NetworkPreset.HighestThroughput) == "max");
// Physical cores (e.g. 6-core/12-thread): budget uses cores, not HT threads
Expect("rss 6-core latency",
    NetworkLogic.RssQueueBudget(NetworkPreset.LowestLatency, 6) >= 2 &&
    NetworkLogic.RssQueueBudget(NetworkPreset.LowestLatency, 6) <= 6);
Expect("rss throughput uses cores",
    NetworkLogic.RssQueueBudget(NetworkPreset.HighestThroughput, 6) == 6);
Expect("Windows IP precedence retained",
    !NetworkLogic.PreferIpv4First(NetworkPreset.LowestLatency, ethernetInUse: false));
Expect("lat script BufferStrategy", latScript.Contains("BufferStrategy", StringComparison.Ordinal));
Expect("lat script RssQueueBudget", latScript.Contains("RssQueueBudget", StringComparison.Ordinal));
Expect("lat script reports Windows IP precedence", latScript.Contains("IP-precedence=Windows-default", StringComparison.Ordinal));
Expect("prefix policy is not changed",
    !latScript.Contains("set prefixpolicy ::ffff:0:0/96", StringComparison.OrdinalIgnoreCase));
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
var lk = NetworkLogic.KnobsFor(NetworkPreset.LowestLatency);
var tk = NetworkLogic.KnobsFor(NetworkPreset.HighestThroughput);
Expect("knobs diverge rsc", lk.Rsc != tk.Rsc);
Expect("knobs diverge lso", lk.Lso != tk.Lso);
Expect("autotune stays supported normal for both policies", lk.AutotuneNetsh == "normal" && tk.AutotuneNetsh == "normal");
Expect("latency leaves Nagle adaptive", !lk.NagleOff);
Expect("throughput nagle not forced", !tk.NagleOff);

// --- Preset-aware NIC status (no false-fail for intentional download settings) ---
var latNicGood = NetworkLogic.EvaluateNic(
    NetworkPreset.LowestLatency,
    new NetworkLogic.NicFacts(FlowControlOn: false, InterruptModerationOn: false, IdleRestrictOn: true, SelectiveSuspendOn: false));
Expect("latency NIC OK when flow/IM off idle on", latNicGood.Ok);

var latNicBadFc = NetworkLogic.EvaluateNic(
    NetworkPreset.LowestLatency,
    new NetworkLogic.NicFacts(FlowControlOn: true, InterruptModerationOn: false, IdleRestrictOn: true, SelectiveSuspendOn: null));
Expect("latency NIC FAIL when flow on", !latNicBadFc.Ok);

var thrNicGood = NetworkLogic.EvaluateNic(
    NetworkPreset.HighestThroughput,
    new NetworkLogic.NicFacts(FlowControlOn: true, InterruptModerationOn: true, IdleRestrictOn: false, SelectiveSuspendOn: false));
Expect("throughput NIC OK when flow/IM on idle off", thrNicGood.Ok);

var thrNicBad = NetworkLogic.EvaluateNic(
    NetworkPreset.HighestThroughput,
    new NetworkLogic.NicFacts(FlowControlOn: false, InterruptModerationOn: true, IdleRestrictOn: false, SelectiveSuspendOn: null));
Expect("throughput NIC FAIL when flow off", !thrNicBad.Ok);

// Same hardware state: latency OK + throughput FAIL for flow-off (not false-fail for thr intentional)
var sharedLatencyState = new NetworkLogic.NicFacts(false, false, true, null);
Expect("shared state OK for latency",
    NetworkLogic.EvaluateNic(NetworkPreset.LowestLatency, sharedLatencyState).Ok);
Expect("shared latency state NOT ok for throughput",
    !NetworkLogic.EvaluateNic(NetworkPreset.HighestThroughput, sharedLatencyState).Ok);

// Autotune must match knobs
Expect("autotune normal matches latency",
    NetworkLogic.AutotuneMatches(NetworkPreset.LowestLatency, "normal"));
Expect("autotune normal matches throughput",
    NetworkLogic.AutotuneMatches(NetworkPreset.HighestThroughput, "normal"));
Expect("autotune experimental does NOT match throughput",
    !NetworkLogic.AutotuneMatches(NetworkPreset.HighestThroughput, "experimental"));
Expect("autotune experimental does NOT match latency",
    !NetworkLogic.AutotuneMatches(NetworkPreset.LowestLatency, "experimental"));
Expect("LSO off matches latency", NetworkLogic.LsoMatches(NetworkPreset.LowestLatency, false));
Expect("LSO on matches throughput", NetworkLogic.LsoMatches(NetworkPreset.HighestThroughput, true));
Expect("LSO off does not match throughput", !NetworkLogic.LsoMatches(NetworkPreset.HighestThroughput, false));
Expect("null LSO skips", NetworkLogic.LsoMatches(NetworkPreset.HighestThroughput, null));
// Unknown autotune must skip (probe gap ≠ fail closed after apply)
Expect("autotune unknown skips", NetworkLogic.AutotuneMatches(NetworkPreset.LowestLatency, "—"));
Expect("autotune empty skips", NetworkLogic.AutotuneMatches(NetworkPreset.HighestThroughput, ""));
Expect("autotune first-token normal", NetworkLogic.AutotuneMatches(NetworkPreset.LowestLatency, "normal  "));

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
    latScript.Contains("$snap.rss = ", StringComparison.Ordinal) &&
    latScript.Contains("$snap.powerManagement = ", StringComparison.Ordinal));

// (a) Connectivity probe + metrics-only prefer-ethernet (never Disable-NetAdapter)
var probeDef = IdxOf(latScript, "function Test-ExoConnectivity");
Expect("probe fn present", probeDef >= 0);
Expect("probe binds TcpClient to eth IPv4",
    latScript.Contains("System.Net.Sockets.TcpClient", StringComparison.Ordinal) &&
    latScript.Contains("$client.Client.Bind", StringComparison.Ordinal) &&
    latScript.Contains("'1.1.1.1'", StringComparison.Ordinal) &&
    latScript.Contains("'8.8.8.8'", StringComparison.Ordinal) &&
    latScript.Contains("443", StringComparison.Ordinal));
Expect("probe has DNS resolve option", latScript.Contains("Test-ExoDnsResolve", StringComparison.Ordinal));
Expect("no Disable-NetAdapter -Name in apply",
    !latScript.Contains("Disable-NetAdapter -Name", StringComparison.Ordinal));
Expect("NCSI left alone",
    latScript.Contains("active probe untouched", StringComparison.OrdinalIgnoreCase) &&
    !latScript.Contains("NoActiveProbe' 1", StringComparison.Ordinal));
Expect("default PreferEthernetDisableWifi is false",
    !new NetworkApplyOptions().PreferEthernetDisableWifi);

// (a) Ordering: post-apply rollback block exists after apply body
var rollbackIdx = IdxOf(latScript, "rolling back path changes automatically");
Expect("rollback block present", rollbackIdx > probeDef,
    $"rollback={rollbackIdx} probeDef={probeDef}");
Expect("rollback re-enables adapters", latScript.Contains("Enable-NetAdapter", StringComparison.Ordinal));
Expect("rollback restores metrics from snapshot",
    rollbackIdx >= 0 &&
    latScript.IndexOf("interface metrics restored from snapshot", StringComparison.Ordinal) > rollbackIdx);
// Full snapshot restore on connectivity failure (not Wi-Fi/metrics-only — that stranded users).
Expect("rollback full snapshot restore (registry + advanced props + bindings)",
    rollbackIdx >= 0 &&
    latScript.IndexOf("FULL snapshot restore", StringComparison.Ordinal) > rollbackIdx &&
    latScript.IndexOf("registry restored from snapshot", StringComparison.Ordinal) > rollbackIdx &&
    latScript.IndexOf("$snapJson.advancedProps", StringComparison.Ordinal) > rollbackIdx &&
    latScript.IndexOf("$snapJson.bindings", StringComparison.Ordinal) > rollbackIdx);
Expect("rollback restarts adapters so advanced props apply",
    latScript.Contains("adapter restarted so advanced props apply", StringComparison.Ordinal));
Expect("rollback forces critical tcpip bindings",
    latScript.Contains("Enable-NetAdapterBinding -Name $a.Name -ComponentID $id", StringComparison.Ordinal) &&
    latScript.Contains("'ms_tcpip','ms_tcpip6','ms_pacer'", StringComparison.Ordinal));
Expect("apply-state json written", latScript.Contains("network-apply-state.json", StringComparison.Ordinal) &&
    latScript.Contains("rollbackReason", StringComparison.Ordinal));

// (a2) Post-apply probe honesty: FULL retry window (link renegotiation after NIC
// advanced-property writes takes 5-20s; a single early probe must never rollback).
Expect("post-probe retry window >= 45s",
    latScript.Contains("$probeWindowSec = 60", StringComparison.Ordinal) &&
    thrScript.Contains("$probeWindowSec = 60", StringComparison.Ordinal));
Expect("post-probe loops until window elapses",
    latScript.Contains("while (-not $postOk -and $probeSw.Elapsed.TotalSeconds -lt $probeWindowSec)", StringComparison.Ordinal));
Expect("post-probe gates on adapter link state",
    latScript.Contains("Get-NetAdapter -Physical", StringComparison.Ordinal) &&
    latScript.Contains("$linkUp", StringComparison.Ordinal));
Expect("post-probe uses DNS resolve as second anchor",
    latScript.Contains("elseif (Test-ExoDnsResolve)", StringComparison.Ordinal));
Expect("post-probe reports attempts + elapsed in reason",
    latScript.Contains("'attempts=' + $probeAttempts", StringComparison.Ordinal) &&
    latScript.Contains("'post-probe' 'fail' ('no tcp 443 / dns reachability after full retry window", StringComparison.Ordinal) &&
    latScript.Contains("$probeDetail", StringComparison.Ordinal));
Expect("post-probe ok also carries timing detail",
    latScript.Contains("'post-probe' 'ok' ('reachable via '", StringComparison.Ordinal));
Expect("rollback reason includes probe timing",
    latScript.Contains("$rollbackReason = 'post-apply-connectivity-failed (' + $probeDetail + ')'", StringComparison.Ordinal));
Expect("rollback re-probe has its own retry window",
    latScript.Contains("$rbSw.Elapsed.TotalSeconds -lt 45", StringComparison.Ordinal));
// Old too-eager probe shape must be gone (single 3s retry then rollback)
Expect("old single-retry probe removed",
    !latScript.Contains("if (-not $postOk) { Start-Sleep -Seconds 3; $postOk = Test-ExoConnectivity }", StringComparison.Ordinal));
Expect("old wifi Disable-NetAdapter gate removed",
    !latScript.Contains("$ExoWifiDisabled += $w.Name", StringComparison.Ordinal));

// (b) Markers for every new tweak (+ preset divergence)
foreach (var forbiddenGlobal in new[]
{
    "timestamps=disabled", "fastopen=enabled", "fastopenfallback=enabled", "pacingprofile=off",
    "hystart=disabled", "uro=disabled", "ecncapability=enabled", "ecncapability=disabled",
    "-InitialRtoMs 1000", "-MinRtoMs 300", "-MaxSynRetransmissions 2",
    "-NonSackRttResiliency Disabled", "set dynamicport", "Set-Dword $sp"
})
{
    Expect($"global policy absent: {forbiddenGlobal}",
        !latScript.Contains(forbiddenGlobal, StringComparison.OrdinalIgnoreCase) &&
        !thrScript.Contains(forbiddenGlobal, StringComparison.OrdinalIgnoreCase));
}
Expect("legacy ACK pins are removed, never forced",
    latScript.Contains("Remove-Prop $p 'TcpAckFrequency'", StringComparison.Ordinal) &&
    !latScript.Contains("Set-Dword $p 'TcpAckFrequency'", StringComparison.Ordinal));
Expect("unrelated Windows features remain untouched",
    !latScript.Contains("Disable-WindowsOptionalFeature", StringComparison.OrdinalIgnoreCase) &&
    !latScript.Contains("Set-NetTeredoConfiguration", StringComparison.OrdinalIgnoreCase) &&
    !latScript.Contains("Set-NetIsatapConfiguration", StringComparison.OrdinalIgnoreCase) &&
    !latScript.Contains("Set-Net6to4Configuration", StringComparison.OrdinalIgnoreCase));
Expect("DoSvc demand-start + snapshot of StartType",
    latScript.Contains("Set-Service -Name 'DoSvc' -StartupType Manual", StringComparison.Ordinal) &&
    latScript.Contains("startType", StringComparison.Ordinal));
Expect("BITS throttle policy removed only if present",
    latScript.Contains("EnableBITSMaxBandwidth", StringComparison.Ordinal));
Expect("RSS BaseProcessorNumber 2 gated on >=4 CPUs",
    latScript.Contains("BaseProcessorNumber 2", StringComparison.Ordinal) &&
    latScript.Contains("$LogicalCpuCount -ge 4", StringComparison.Ordinal));
Expect("RSS null Enabled handled by live hash state",
    latScript.Contains("IPv4HashEnabled", StringComparison.Ordinal) &&
    latScript.Contains("RssProcessorArraySize", StringComparison.Ordinal) &&
    !latScript.Contains("Enabled=$true; Profile", StringComparison.Ordinal));
Expect("adaptive RSS profile diverges by preset",
    latScript.Contains("Profile='Closest'", StringComparison.Ordinal) &&
    thrScript.Contains("Profile='NUMAStatic'", StringComparison.Ordinal) &&
    latScript.Contains("MaxProcessors", StringComparison.Ordinal) &&
    latScript.Contains("NumberOfReceiveQueues", StringComparison.Ordinal));
Expect("D0 packet coalescing disabled when supported",
    latScript.Contains("D0PacketCoalescing='Disabled'", StringComparison.Ordinal) &&
    latScript.Contains("packet-coalescing", StringComparison.Ordinal));
Expect("RegistryKeyword-first adapter writes",
    latScript.Contains("'*FlowControl'", StringComparison.Ordinal) &&
    latScript.Contains("'*JumboPacket'", StringComparison.Ordinal) &&
    latScript.Contains("'*PriorityVLANTag'", StringComparison.Ordinal) &&
    latScript.Contains("'*InterruptModeration'", StringComparison.Ordinal) &&
    latScript.Contains("'*LsoV2IPv6'", StringComparison.Ordinal) &&
    latScript.Contains("'*WakeOnMagicPacket'", StringComparison.Ordinal));
Expect("SpeedDuplex never forced",
    !latScript.Contains("'*SpeedDuplex'", StringComparison.Ordinal));
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
    "'snapshot'", "'host-policy'", "'registry-host'", "'tcp-globals'", "'tcp-algorithms'",
    "'tcp-settings'", "'legacy-ack-pins'", "'adapters'", "'rss-base'",
    "'rss-policy'", "'packet-coalescing'",
    "'bindings'", "'background-quiet'", "'prefix-policy'", "'eth-metrics'", "'dns-auto'", "'power-policy'", "'wifi-disable'",
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
var parsedReport = NetworkLogic.ParseApplyReport(sampleLog);
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
var benchParsed = NetworkLogic.TryParseBenchmark(
    "noise\nEXO_BENCH:{\"ok\":true,\"pingP50Ms\":12.5,\"pingP95Ms\":18,\"jitterMs\":1.2,\"dnsMs\":22.7,\"samples\":20,\"timestampUtc\":\"2026-01-01T00:00:00Z\"}\n");
Expect("benchmark parser values",
    benchParsed is { Ok: true, Samples: 20 } &&
    Math.Abs(benchParsed.PingP50Ms - 12.5) < 0.001 &&
    Math.Abs(benchParsed.DnsMs - 22.7) < 0.001,
    benchParsed is null ? "null" : $"p50={benchParsed.PingP50Ms}");
Expect("benchmark parser rejects garbage", NetworkLogic.TryParseBenchmark("no marker here") is null);
var qualityParsed = NetworkLogic.TryParseBenchmark(
    "EXO_BENCH:{\"ok\":true,\"isQualityTest\":true,\"pingP50Ms\":12,\"jitterMs\":2,\"downloadMbps\":500,\"uploadMbps\":80,\"downloadLoadedMs\":18,\"uploadLoadedMs\":25,\"downloadLoadedJitterMs\":3,\"uploadLoadedJitterMs\":4,\"packetLossPercent\":0,\"dataUsedMb\":100,\"endpoint\":\"Cloudflare edge\",\"recommendedPreset\":\"highest-throughput\",\"recommendationReason\":\"stable\"}");
Expect("quality benchmark parser values",
    qualityParsed is { Ok: true, IsQualityTest: true } &&
    Math.Abs(qualityParsed.DownloadMbps - 500) < 0.001 &&
    qualityParsed.Endpoint == "Cloudflare edge");
Expect("adaptive preset stable fast ethernet",
    qualityParsed is not null && NetworkLogic.RecommendPreset(qualityParsed, media) == NetworkPreset.HighestThroughput);
var unstableQuality = NetworkLogic.TryParseBenchmark(
    "EXO_BENCH:{\"ok\":true,\"isQualityTest\":true,\"pingP50Ms\":12,\"jitterMs\":2,\"downloadMbps\":500,\"downloadLoadedMs\":18,\"uploadLoadedMs\":25,\"downloadLoadedJitterMs\":25,\"uploadLoadedJitterMs\":4,\"packetLossPercent\":0}");
Expect("multi-gig Ethernet keeps offloads despite loaded queueing",
    unstableQuality is not null && NetworkLogic.RecommendPreset(unstableQuality, media) == NetworkPreset.HighestThroughput);
var slowWifi = new NetworkMediaProfile
{
    EthernetInUse = false,
    WifiAvailable = true,
    WifiUp = true,
    PrimaryLinkSpeedBps = 300_000_000
};
Expect("unstable Wi-Fi chooses latency-safe host policy",
    unstableQuality is not null && NetworkLogic.RecommendPreset(unstableQuality, slowWifi) == NetworkPreset.LowestLatency);

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
    repairScript.Contains("$snap.powerManagement", StringComparison.Ordinal) &&
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
Expect("repair always re-enables adapters regardless of path",
    repairScript.Contains("adapter re-enabled", StringComparison.Ordinal) &&
    repairScript.Contains("'wifi-reenable' 'ok'", StringComparison.Ordinal));
Expect("repair restarts adapters after advanced prop restore",
    repairScript.Contains("adapter restarted so advanced props apply", StringComparison.Ordinal));
Expect("repair forces critical tcpip bindings",
    repairScript.Contains("'ms_tcpip','ms_tcpip6','ms_pacer'", StringComparison.Ordinal));
Expect("repair does not auto winsock reset",
    repairScript.Contains("'hard-reset' 'skip'", StringComparison.Ordinal) &&
    !repairScript.Contains("applying hard winsock/ip reset", StringComparison.Ordinal));
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
// Wave-1: UI service must not use brick-era success copy
if (repoRoot is not null)
{
    var netSvcPath = Path.Combine(repoRoot, "Exo", "Services", "NetworkOptimizerService.cs");
    if (File.Exists(netSvcPath))
    {
        var netSvc = File.ReadAllText(netSvcPath);
        Expect("bindings success label is QoS+IP not Client/LLDP off",
            netSvc.Contains("Applied (QoS + IPv4/IPv6 on)", StringComparison.Ordinal) &&
            !netSvc.Contains("Client/LLDP off)", StringComparison.Ordinal));
        Expect("Wi-Fi while Ethernet never hard-fails for Still up",
            netSvc.Contains("Up (metrics prefer Ethernet)", StringComparison.Ordinal) ||
            netSvc.Contains("Up (kept)", StringComparison.Ordinal));
        Expect("Internet apply and repair use the shared runner",
            netSvc.Contains("NetworkOptimizerService(PowerShellRunnerService runner)", StringComparison.Ordinal) &&
            netSvc.Contains("_runner.RunAsync", StringComparison.Ordinal) &&
            !netSvc.Contains("Verb = \"runas\"", StringComparison.Ordinal) &&
            !netSvc.Contains("FileName = \"powershell.exe\"", StringComparison.Ordinal));
        var qualityMethod = netSvc.IndexOf("RunQualityBenchmarkAsync", StringComparison.Ordinal);
        var qualityEnd = qualityMethod >= 0
            ? netSvc.IndexOf("LoadQualityBenchmark", qualityMethod, StringComparison.Ordinal)
            : -1;
        var qualityBlock = qualityMethod >= 0 && qualityEnd > qualityMethod
            ? netSvc[qualityMethod..qualityEnd]
            : string.Empty;
        Expect("quality benchmark uses sustained native parallel streams",
            qualityBlock.Contains("SocketsHttpHandler", StringComparison.Ordinal) &&
            qualityBlock.Contains("MeasureDownloadAsync", StringComparison.Ordinal) &&
            qualityBlock.Contains("MeasureUploadAsync", StringComparison.Ordinal) &&
            qualityBlock.Contains("ParallelStreams", StringComparison.Ordinal));
        Expect("quality benchmark measures loaded latency and DNS candidates",
            qualityBlock.Contains("SampleLoadedLatencyAsync", StringComparison.Ordinal) &&
            qualityBlock.Contains("SelectFastestDnsAsync", StringComparison.Ordinal) &&
            qualityBlock.Contains("UdpClient", StringComparison.Ordinal));
        Expect("quality latency is independent sequential ICMP",
            qualityBlock.Contains("SelectLatencyTargetAsync", StringComparison.Ordinal) &&
            qualityBlock.Contains("SamplePingSeriesAsync", StringComparison.Ordinal) &&
            qualityBlock.Contains("SendPingAsync", StringComparison.Ordinal) &&
            !qualityBlock.Contains("MeasureEdgeLatencyAsync", StringComparison.Ordinal) &&
            !qualityBlock.Contains("MeasurePacketLossAsync", StringComparison.Ordinal));
        Expect("quality uses one selected target for idle and load",
            qualityBlock.Contains("MeasureDownloadAsync(client, latencyTarget", StringComparison.Ordinal) &&
            qualityBlock.Contains("MeasureUploadAsync(client, latencyTarget", StringComparison.Ordinal));
        Expect("quality benchmark no PowerShell transfer host",
            !qualityBlock.Contains("PowerShellRunnerService.ResolvePowerShell()", StringComparison.Ordinal));
    }
}
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
    Expect("rescue has Hard switch + winsock reset",
        rescue.Contains("[switch]$Hard", StringComparison.Ordinal) &&
        rescue.Contains("netsh winsock reset", StringComparison.Ordinal) &&
        rescue.Contains("Invoke-ExoHardStackReset", StringComparison.Ordinal));
    Expect("rescue restarts adapters after advanced props",
        rescue.Contains("Restart-NetAdapter", StringComparison.Ordinal) &&
        rescue.Contains("advanced props apply", StringComparison.Ordinal));
    Expect("rescue documents offline emergency block",
        rescue.Contains("EMERGENCY", StringComparison.Ordinal) &&
        rescue.Contains("netsh int ip reset", StringComparison.Ordinal));
    Expect("rescue restores DNS servers from snapshot",
        rescue.Contains("$snap.dnsServers", StringComparison.Ordinal) &&
        rescue.Contains("-ResetServerAddresses", StringComparison.Ordinal));
    Expect("rescue removes TTL overrides even from baseline",
        rescue.Contains("Folklore DNS cache TTL", StringComparison.Ordinal));
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

// (g) EXECUTION regression gate: run the emitted snapshot capture + the full
// repair script under pwsh against Windows-shaped mocks with MIXED-TYPE
// registry values (Int32/Int64/String/ExpandString/String[]/Byte[]).
// Guards the real-Windows 'Argument types do not match' snapshot abort
// (PSObject-wrapped List[object] + @() in pwsh 7.6) and the inverse
// restore-side type-coercion bugs. String audits and AST parses cannot
// catch these — this actually executes the generated code.
var smokeDir = repoRoot is null ? null : Path.Combine(repoRoot, "tools", "Network.Smoke");
var harnessPath = smokeDir is null ? null : Path.Combine(smokeDir, "SnapshotExecHarness.ps1");
var mocksPath = smokeDir is null ? null : Path.Combine(smokeDir, "SnapshotExecMocks.ps1");
Expect("snapshot exec harness present",
    harnessPath is not null && File.Exists(harnessPath) && File.Exists(mocksPath!),
    $"harness={harnessPath}");
if (harnessPath is not null && File.Exists(harnessPath) && File.Exists(mocksPath!))
{
    var execDir = Path.Combine(Path.GetTempPath(), "exo-netexec-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(execDir);
    try
    {
        var repairPath = Path.Combine(execDir, "repair.ps1");
        File.WriteAllText(repairPath, repairScript);
        foreach (var (presetName, applyText) in new[] { ("latency", latScript), ("throughput", thrScript) })
        {
            var applyPath = Path.Combine(execDir, $"apply-{presetName}.ps1");
            File.WriteAllText(applyPath, applyText);
            var workDir = Path.Combine(execDir, $"work-{presetName}");
            var execOut = RunPs(
                "-NoProfile -ExecutionPolicy Bypass -File \"" + harnessPath + "\"" +
                " -ApplyScriptPath \"" + applyPath + "\"" +
                " -RepairScriptPath \"" + repairPath + "\"" +
                " -MocksPath \"" + mocksPath + "\"" +
                " -WorkDir \"" + workDir + "\"");
            Expect($"exec harness ran ({presetName})", execOut is not null);
            if (execOut is null) continue;
            var flat = execOut.Replace('\r', ' ').Replace('\n', ' ');
            Expect($"exec: snapshot serializes mixed types ({presetName})",
                execOut.Contains("EXOTEST:snapshot-exec succeeds (mixed-type registry values)|pass", StringComparison.Ordinal), flat);
            Expect($"exec: repair restores typed values ({presetName})",
                execOut.Contains("EXOTEST:repair-exec MultiString restored as String[]|pass", StringComparison.Ordinal) &&
                execOut.Contains("EXOTEST:repair-exec Binary restored as Byte[]|pass", StringComparison.Ordinal) &&
                execOut.Contains("EXOTEST:repair-exec DWord -1 restored|pass", StringComparison.Ordinal), flat);
            Expect($"exec: zero harness failures ({presetName})",
                execOut.Contains("EXOTEST-SUMMARY:failed=0", StringComparison.Ordinal), flat);
            var failLines = execOut.Split('\n')
                .Where(l => l.Contains("EXOTEST:", StringComparison.Ordinal) && l.Contains("|fail", StringComparison.Ordinal))
                .ToList();
            Expect($"exec: no individual assertion failed ({presetName})",
                failLines.Count == 0, string.Join(" // ", failLines));
        }
    }
    finally
    {
        try { Directory.Delete(execDir, recursive: true); } catch { }
    }
}

Log($"=== SUMMARY failed={failed} ===");
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
File.WriteAllLines(logPath, lines);
Console.WriteLine("Wrote " + logPath);
Environment.Exit(failed == 0 ? 0 : 1);
