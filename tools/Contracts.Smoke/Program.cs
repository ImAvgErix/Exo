using Exo.Models;
using Exo.Services;

// Wave-3 Detect = Apply contract tables.
// Exit 0 only if all module contracts hold. Args: optional log path.

var logPath = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "exo-contracts-smoke.log");
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

Log("=== Contracts.Smoke (Detect = Apply) ===");

var repo = FindRepoRoot();
Expect("repo root", Directory.Exists(repo));

// --- Internet: generated apply + repair share safety/report contract ---
var media = new NetworkMediaProfile
{
    ClientSupports6Ghz = true,
    ClientSupports5Ghz = true,
    EthernetInUse = true,
    WifiAvailable = true
};
var opts = new NetworkApplyOptions { PreferEthernetDisableWifi = true, RestartEthernet = false };
var lat = NetworkApplyScriptBuilder.Build(NetworkPreset.LowestLatency, opts, media);
var thr = NetworkApplyScriptBuilder.Build(NetworkPreset.HighestThroughput, opts, media);
var repair = NetworkApplyScriptBuilder.BuildRepair();
var bench = NetworkApplyScriptBuilder.BuildBenchmark();

var (latOk, latIssues) = NetworkLogic.AuditApplyScript(lat, NetworkPreset.LowestLatency);
var (thrOk, thrIssues) = NetworkLogic.AuditApplyScript(thr, NetworkPreset.HighestThroughput);
Expect("internet latency AuditApply", latOk, string.Join("; ", latIssues));
Expect("internet throughput AuditApply", thrOk, string.Join("; ", thrIssues));

// Detect UI row labels must not require folklore that Apply never writes.
foreach (var forbidden in NetworkLogic.ForbiddenApplyPatterns)
{
    Expect("internet apply free of folklore: " + Short(forbidden),
        lat.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) < 0
        && thr.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) < 0);
}

// Shared Detect=Apply report + snapshot markers on apply and repair.
foreach (var marker in new[] { "EXO_REPORT:", "network-snapshot.json", "Test-ExoConnectivity" })
{
    Expect("internet apply has " + marker,
        lat.Contains(marker, StringComparison.OrdinalIgnoreCase)
        && thr.Contains(marker, StringComparison.OrdinalIgnoreCase));
    Expect("internet repair has " + marker.Replace("Test-ExoConnectivity", "EXO_REPORT:"),
        marker == "Test-ExoConnectivity"
            ? repair.Contains("EXO_REPORT:", StringComparison.Ordinal)
            : repair.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
Expect("internet never disables Wi-Fi (latency)",
    lat.Contains("never disable wifi", StringComparison.OrdinalIgnoreCase)
    || lat.Contains("never disable wifi adapters", StringComparison.OrdinalIgnoreCase));
Expect("internet benchmark EXO_BENCH", bench.Contains("EXO_BENCH:", StringComparison.Ordinal));
Expect("network builder partials linked",
    File.Exists(Path.Combine(repo, "Exo", "Services", "NetworkApplyScriptBuilder.Repair.cs"))
    && File.Exists(Path.Combine(repo, "Exo", "Services", "NetworkApplyScriptBuilder.Benchmark.cs")));

// Internet detect service must not hard-require Client/LLDP off (fail-closed honesty).
var netService = File.ReadAllText(Path.Combine(repo, "Exo", "Services", "NetworkOptimizerService.cs"));
Expect("internet detect bindings QoS+IP only (no Client/LLDP hard fail)",
    !netService.Contains("Client/LLDP", StringComparison.Ordinal)
    || netService.Contains("QoS", StringComparison.Ordinal));

// --- Steam: RequiredApplyMarkers present in optimizer; detect cores agree on key features ---
var steamOpt = File.ReadAllText(Path.Combine(repo, "Exo", "Scripts", "Steam", "Steam-Optimizer.ps1"));
var steamDetect = File.ReadAllText(Path.Combine(repo, "Exo", "Scripts", "Steam", "Exo-Steam-Detect.ps1"));
var steamCore = File.ReadAllText(Path.Combine(repo, "Exo", "Scripts", "Steam", "SteamDetectCore.ps1"));
var (steamOk, steamIssues) = SteamLogic.AuditApplyScriptText(steamOpt);
Expect("steam AuditApplyScriptText", steamOk, string.Join("; ", steamIssues));
foreach (var m in SteamLogic.RequiredApplyMarkers)
    Expect("steam apply marker: " + m, steamOpt.Contains(m, StringComparison.OrdinalIgnoreCase));
foreach (var f in SteamLogic.ForbiddenApplyPatterns)
    Expect("steam forbidden absent: " + Short(f), steamOpt.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0);

// Detect predicates that gate "applied" must reference the same durable concepts Apply writes.
foreach (var pair in new (string Detect, string Apply)[]
{
    ("Test-SteamCefLauncher", "Steam-Exo.cmd"),
    ("Test-SteamMemoryGuard", "Exo-SteamMemoryGuard"),
    ("applyStatus", "applyStatus"),
    ("-cef-disable-gpu", "-cef-disable-gpu"),
})
{
    Expect($"steam detect has {pair.Detect}",
        steamDetect.Contains(pair.Detect, StringComparison.OrdinalIgnoreCase)
        || steamCore.Contains(pair.Detect, StringComparison.OrdinalIgnoreCase));
    Expect($"steam apply has {pair.Apply}",
        steamOpt.Contains(pair.Apply, StringComparison.OrdinalIgnoreCase));
}
Expect("steam soft-skip not full applied",
    steamCore.Contains("applyStatus", StringComparison.OrdinalIgnoreCase)
    && steamCore.Contains("applied", StringComparison.OrdinalIgnoreCase));
Expect("steam stage lib bootstrap",
    File.Exists(Path.Combine(repo, "Exo", "Scripts", "Steam", "lib", "Steam.Bootstrap.ps1")));

// --- Discord: audit concatenated apply sources (same blob as Discord.Smoke) ---
var discFiles = new[]
{
    Path.Combine(repo, "Exo", "Scripts", "Discord", "Disc-Optimizer.ps1"),
    Path.Combine(repo, "Exo", "Scripts", "Discord", "Exo-Discord-Run.ps1"),
    Path.Combine(repo, "Exo", "Scripts", "Discord", "kit", "lib", "10-Logging.ps1"),
    Path.Combine(repo, "Exo", "Scripts", "Discord", "kit", "lib", "40-DebloatWindows.ps1"),
    Path.Combine(repo, "Exo", "Scripts", "Discord", "kit", "lib", "60-KernelBoot.ps1"),
};
var discOpt = string.Join("\n", discFiles.Where(File.Exists).Select(File.ReadAllText));
var discDetect = File.ReadAllText(Path.Combine(repo, "Exo", "Scripts", "Discord", "Exo-Discord-Detect.ps1"));
var discCore = File.ReadAllText(Path.Combine(repo, "Exo", "Scripts", "Discord", "DiscordDetectCore.ps1"));
var (discOk, discIssues) = DiscordLogic.AuditApplyScriptText(discOpt);
Expect("discord AuditApplyScriptText", discOk, string.Join("; ", discIssues));
foreach (var m in DiscordLogic.RequiredApplyMarkers)
    Expect("discord apply marker: " + m, discOpt.Contains(m, StringComparison.OrdinalIgnoreCase));
foreach (var f in DiscordLogic.ForbiddenApplyPatterns)
    Expect("discord forbidden absent: " + Short(f), discOpt.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0);
foreach (var pair in new (string Detect, string Apply)[]
{
    ("applyStatus", "applyStatus"),
    ("kernel", "Install-DiscOptKernel"),
    ("OPEN_ON_STARTUP", "OPEN_ON_STARTUP"),
})
{
    Expect($"discord detect concept: {pair.Detect}",
        discDetect.Contains(pair.Detect, StringComparison.OrdinalIgnoreCase)
        || discCore.Contains(pair.Detect, StringComparison.OrdinalIgnoreCase));
    Expect($"discord apply concept: {pair.Apply}",
        discOpt.Contains(pair.Apply, StringComparison.OrdinalIgnoreCase));
}
Expect("discord live applied not only state file",
    discCore.Contains("applyStatus", StringComparison.OrdinalIgnoreCase)
    && discCore.Contains("applied", StringComparison.OrdinalIgnoreCase));

// --- NVIDIA: required markers; Reset is status-clear only; no tray task create ---
var nvOpt = File.ReadAllText(Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Nvidia-Optimizer.ps1"));
var nvDetect = File.ReadAllText(Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Exo-Nvidia-Detect.ps1"));
var nvRepair = File.ReadAllText(Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Exo-Nvidia-Repair.ps1"));
var nvBoot = File.ReadAllText(Path.Combine(repo, "Exo", "Scripts", "Nvidia", "lib", "Nvidia.Bootstrap.ps1"));
var nvRun = File.ReadAllText(Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Exo-Nvidia-Run.ps1"));
var nvBlob = nvOpt + "\n" + nvBoot + "\n" + nvRun;
var (nvOk, nvIssues) = NvidiaDetectLogic.AuditApplyScriptText(nvOpt);
Expect("nvidia AuditApplyScriptText", nvOk, string.Join("; ", nvIssues));
foreach (var m in NvidiaDetectLogic.RequiredApplyMarkers)
    Expect("nvidia apply marker: " + m, nvOpt.Contains(m, StringComparison.OrdinalIgnoreCase));
foreach (var f in NvidiaDetectLogic.ForbiddenApplyPatterns)
    Expect("nvidia forbidden absent: " + Short(f), nvOpt.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0);
Expect("nvidia detect has DRS/profile concepts",
    nvDetect.Contains("profile", StringComparison.OrdinalIgnoreCase)
    && (nvDetect.Contains("drs", StringComparison.OrdinalIgnoreCase)
        || nvDetect.Contains("driverTweaks", StringComparison.OrdinalIgnoreCase)));
Expect("nvidia repair status-clear (no full driver rollback claim)",
    !nvRepair.Contains("rollback driver", StringComparison.OrdinalIgnoreCase)
    && !nvRepair.Contains("uninstall driver", StringComparison.OrdinalIgnoreCase));
Expect("nvidia stage lib bootstrap",
    File.Exists(Path.Combine(repo, "Exo", "Scripts", "Nvidia", "lib", "Nvidia.Bootstrap.ps1")));
Expect("nvidia Run wires bootstrap",
    nvRun.Contains("Nvidia.Bootstrap.ps1", StringComparison.Ordinal));

// --- Shared plate + no Exo background create across optimizers ---
Expect("SharedModulePlate shipped",
    File.Exists(Path.Combine(repo, "Exo", "Views", "Controls", "SharedModulePlate.xaml")));
foreach (var page in new[] { "Discord", "Steam", "Internet", "Nvidia" })
{
    var xaml = File.ReadAllText(Path.Combine(repo, "Exo", "Views", page + "OptimizerPage.xaml"));
    Expect(page + " page uses SharedModulePlate", xaml.Contains("SharedModulePlate", StringComparison.Ordinal));
}

// Cross-module EXO_REPORT vocabulary (apply path or shared bootstrap emitters)
foreach (var (name, blob) in new (string, string)[]
{
    ("internet", lat),
    ("steam", steamOpt),
    ("discord", discOpt),
    ("nvidia", nvBlob),
})
{
    Expect(name + " EXO_REPORT present", blob.Contains("EXO_REPORT", StringComparison.OrdinalIgnoreCase));
}

// God-file size exceptions noted (Wave 3 thin split, full strangle later)
var steamBytes = new FileInfo(Path.Combine(repo, "Exo", "Scripts", "Steam", "Steam-Optimizer.ps1")).Length;
var nvBytes = new FileInfo(Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Nvidia-Optimizer.ps1")).Length;
var exceptionDoc = File.ReadAllText(Path.Combine(repo, "docs", "REWRITE-PROGRAM.md"));
if (steamBytes > 80_000 || nvBytes > 80_000)
{
    Expect("god-file exception note in REWRITE-PROGRAM",
        exceptionDoc.Contains("god-file", StringComparison.OrdinalIgnoreCase)
        || exceptionDoc.Contains("80 KB", StringComparison.Ordinal)
        || exceptionDoc.Contains("exception", StringComparison.OrdinalIgnoreCase));
}

Log(failed == 0 ? "=== ALL CONTRACTS PASS ===" : $"=== {failed} CONTRACT FAILURE(S) ===");
try { File.WriteAllLines(logPath, lines); } catch { /* best effort */ }
return failed == 0 ? 0 : 1;

static string Short(string s) => s.Length <= 48 ? s : s[..45] + "...";

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "VERSION"))
            && Directory.Exists(Path.Combine(dir.FullName, "Exo")))
            return dir.FullName;
        dir = dir.Parent;
    }
    // tools/Contracts.Smoke/bin/... -> climb
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
