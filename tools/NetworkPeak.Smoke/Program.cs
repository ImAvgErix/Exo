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
Expect("eth DMA coalescing off", latScript.Contains("DMACoalescing", StringComparison.OrdinalIgnoreCase)
    || latScript.Contains("DMA Coalescing", StringComparison.OrdinalIgnoreCase));
Expect("wifi transmit power", latScript.Contains("Transmit Power", StringComparison.OrdinalIgnoreCase));
Expect("wifi MU-MIMO", latScript.Contains("MU-MIMO", StringComparison.OrdinalIgnoreCase));
Expect("NetBIOS disable", latScript.Contains("NetbiosOptions", StringComparison.OrdinalIgnoreCase));
Expect("no wifi Restart-NetAdapter force",
    !System.Text.RegularExpressions.Regex.IsMatch(latScript, @"Restart-NetAdapter.*Wi-?Fi",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase));

// --- Tailored detection helpers ---
ExpectEq("vendor Intel I226",
    NetworkPeakLogic.ClassifyNicVendor("Intel(R) Ethernet Controller I226-V"), "Intel");
ExpectEq("vendor Realtek",
    NetworkPeakLogic.ClassifyNicVendor("Realtek PCIe GbE Family Controller"), "Realtek");
ExpectEq("vendor Killer",
    NetworkPeakLogic.ClassifyNicVendor("Killer E3100G 2.5 Gigabit Ethernet Controller"), "Killer");
Expect("buffer latency mid", NetworkPeakLogic.BufferStrategy(NetworkPreset.LowestLatency) == "mid");
Expect("buffer throughput max", NetworkPeakLogic.BufferStrategy(NetworkPreset.HighestThroughput) == "max");
Expect("rss latency caps cores",
    NetworkPeakLogic.RssQueueBudget(NetworkPreset.LowestLatency, 16) == 8);
Expect("rss throughput full cores",
    NetworkPeakLogic.RssQueueBudget(NetworkPreset.HighestThroughput, 16) == 16);
Expect("prefer ipv4 on latency",
    NetworkPeakLogic.PreferIpv4First(NetworkPreset.LowestLatency, ethernetInUse: false));
var plan = NetworkPeakLogic.BuildTailoredPlan(
    NetworkPreset.LowestLatency, "Intel", "Ethernet", 2_500_000_000, 16, false, false);
Expect("tailored plan has intel", plan.Contains("Intel", StringComparison.Ordinal));
Expect("tailored plan has 2.5G", plan.Contains("2.5G", StringComparison.Ordinal));
Expect("tailored plan has latency", plan.Contains("latency", StringComparison.Ordinal));
Expect("lat script BufferStrategy", latScript.Contains("BufferStrategy", StringComparison.Ordinal));
Expect("lat script RssQueueBudget", latScript.Contains("RssQueueBudget", StringComparison.Ordinal));
Expect("lat script TailoredPlan", latScript.Contains("TailoredPlan", StringComparison.Ordinal));
Expect("lat script PreferIpv4", latScript.Contains("PreferIpv4First", StringComparison.Ordinal));
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

Log($"=== SUMMARY failed={failed} ===");
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
File.WriteAllLines(logPath, lines);
Console.WriteLine("Wrote " + logPath);
Environment.Exit(failed == 0 ? 0 : 1);
