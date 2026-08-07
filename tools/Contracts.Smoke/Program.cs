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

// Release and dependency hygiene are product contracts, not comments in a roadmap.
// Keep the UI package metadata synchronized with the root VERSION so a single
// release never advertises two product versions.
var rootVersion = File.ReadAllText(Path.Combine(repo, "VERSION")).Trim();
using (var packageDoc = System.Text.Json.JsonDocument.Parse(
           File.ReadAllText(Path.Combine(repo, "ui", "package.json"))))
{
    var uiVersion = packageDoc.RootElement.GetProperty("version").GetString();
    Expect("UI package version matches root VERSION", uiVersion == rootVersion,
        $"ui={uiVersion}, root={rootVersion}");
}

var ciWorkflow = File.ReadAllText(Path.Combine(repo, ".github", "workflows", "ci.yml"));
Expect("Discord CI process shutdown tolerates process-exit races",
    ciWorkflow.Contains("Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue", StringComparison.Ordinal)
    && !ciWorkflow.Contains("Stop-Process -Id $_.Id -Force }", StringComparison.Ordinal));

var releaseWorkflow = File.ReadAllText(Path.Combine(repo, ".github", "workflows", "release.yml"));
Expect("release validates and publishes one immutable checked-out SHA",
    releaseWorkflow.Contains("ref: ${{ github.sha }}", StringComparison.Ordinal)
    && !releaseWorkflow.Contains("ref: main", StringComparison.Ordinal)
    && releaseWorkflow.Contains("actions/upload-artifact", StringComparison.Ordinal)
    && releaseWorkflow.Contains("actions/download-artifact", StringComparison.Ordinal)
    && releaseWorkflow.Contains("Exo.exe.sha256", StringComparison.Ordinal)
    && releaseWorkflow.Contains("gh release create", StringComparison.Ordinal)
    && !releaseWorkflow.Contains("-ReplaceExisting", StringComparison.Ordinal)
    && !releaseWorkflow.Contains("-PruneOldReleases", StringComparison.Ordinal));

var prereleaseWorkflow = File.ReadAllText(Path.Combine(repo, ".github", "workflows", "prerelease.yml"));
Expect("prerelease publishes the tested SHA with a verifiable checksum",
    prereleaseWorkflow.Contains("$sha = (git rev-parse HEAD).Trim()", StringComparison.Ordinal)
    && prereleaseWorkflow.Contains("--target $sha", StringComparison.Ordinal)
    && prereleaseWorkflow.Contains("Exo.exe.sha256", StringComparison.Ordinal)
    && prereleaseWorkflow.Contains("Get-FileHash", StringComparison.Ordinal));

var releaseScript = File.ReadAllText(Path.Combine(repo, "Release-Exo.ps1"));
Expect("local release helper preserves immutable history by default",
    !releaseScript.Contains("[switch]$PruneOldReleases = $true", StringComparison.Ordinal)
    && !releaseScript.Contains("gh release delete", StringComparison.Ordinal)
    && !releaseScript.Contains("git push origin \":refs/tags/$t\"", StringComparison.Ordinal));
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

// --- Legacy WinUI optimizer surface stays deleted (WebView2 shell owns the UI now) ---
Expect("SharedModulePlate removed",
    !File.Exists(Path.Combine(repo, "Exo", "Views", "Controls", "SharedModulePlate.xaml")));
foreach (var page in new[] { "Discord", "Steam", "Internet", "Nvidia" })
{
    Expect(page + "OptimizerPage removed",
        !File.Exists(Path.Combine(repo, "Exo", "Views", page + "OptimizerPage.xaml")));
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
var exceptionDoc = File.ReadAllText(Path.Combine(repo, "AGENTS.md"));
if (steamBytes > 80_000 || nvBytes > 80_000)
{
    Expect("god-file exception note in AGENTS.md",
        exceptionDoc.Contains("god-file", StringComparison.OrdinalIgnoreCase)
        || exceptionDoc.Contains("80 KB", StringComparison.Ordinal)
        || exceptionDoc.Contains("exception", StringComparison.OrdinalIgnoreCase));
}

// --- Behavioural: drive the classifier with the machines that actually broke ---
// Every gate in this repo was green while three modules were unreachable, because they
// all check source text. These call the real classifier with a made-up machine and check
// the answer — the first tests here that could have caught any of today's bugs.
{
    static ModuleStatusClassifier.Row R(string t, bool on) => new(t, on);


    // The generic form of the same bug: any module whose rows are all info titles.
    var allInfo = ModuleStatusClassifier.Classify(
        "steam", isApplied: false, statusText: "Ready", detail: "",
        new[] { R("Policy", true), R("Safe repair", true), R("Last apply", true) });
    Expect("a module with only info rows never reads applied on an untouched machine",
        allInfo.Kind != "applied", $"got {allInfo.Kind}");

    // Normal shapes still behave.
    var untouched = ModuleStatusClassifier.Classify(
        "brave", isApplied: false, statusText: "", detail: "",
        new[] { R("Telemetry off", false), R("GPU routing", false) });
    Expect("an untouched normal module reads ready", untouched.Kind == "ready", $"got {untouched.Kind}");

    var half = ModuleStatusClassifier.Classify(
        "brave", isApplied: false, statusText: "", detail: "",
        new[] { R("Telemetry off", true), R("GPU routing", false) });
    Expect("a half-applied module reads partial", half.Kind == "partial", $"got {half.Kind}");

    var allOn = ModuleStatusClassifier.Classify(
        "brave", isApplied: false, statusText: "", detail: "",
        new[] { R("Telemetry off", true), R("GPU routing", true) });
    Expect("every real row active reads applied", allOn.Kind == "applied", $"got {allOn.Kind}");

    var notThere = ModuleStatusClassifier.Classify(
        "discord", isApplied: false, statusText: "Discord is not installed", detail: "",
        new[] { R("Kernel", false) });
    Expect("an absent app reads missing", notThere.Kind == "missing", $"got {notThere.Kind}");

    // ── Brave must never destroy the user's sessions ───────────────────────────────────
    // Debloat is not the same thing as signing someone out of every site they use. Three
    // settings have each done exactly that, and two of them shipped together: a global
    // "forget me when I close this site" policy and the matching first-party-ephemeral-storage
    // labs flag, which between them wiped first-party cookies and localStorage on every close.
    // Blocking THIRD-party cookies is where the tracking is, and it costs no logins.
    // Values, not prose — the file's own comment said first-party cookies were kept while two
    // active settings were discarding them.
    {
        var brave = File.ReadAllText(Path.Combine(repo, "Exo", "Services", "BraveNativeApply.cs"));
        var live = string.Join("\n", brave.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Expect("Brave does not force 'forget me when I close this site'",
            !live.Contains("\"DefaultBraveRemember1PStorageSetting\", 2", StringComparison.Ordinal));
        Expect("Brave does not set cookies to session-only",
            !live.Contains("\"DefaultCookiesSetting\", 4", StringComparison.Ordinal));
        Expect("Brave does not enable first-party ephemeral storage",
            !live.Contains("brave-first-party-ephemeral-storage", StringComparison.Ordinal));
        // The cache sweep is allowed to remove regenerable trees only. These four hold logins
        // and site state and regenerate as "signed out".
        foreach (var sacred in new[] { "\"Cookies\"", "\"Local Storage\"", "\"Session Storage\"", "\"IndexedDB\"" })
        {
            Expect($"Brave cache sweep does not delete {sacred.Trim('"')}",
                !live.Contains(sacred, StringComparison.Ordinal));
        }
    }

    // ── One owner for "advisory row" ───────────────────────────────────────────────────
    // NativeLiveDetect.IsInfo and ModuleStatusClassifier.IsInfoTitle used to be two lists
    // that disagreed, and both are consulted on the same request: detect sets IsApplied with
    // one, this classifier sets the status kind with the other. Brave with Proton Pass absent
    // came back IsApplied=true / "Already optimized" from detect and "partial - 1 still off"
    // from here. Both titles the lists disagreed on are asserted directly.
    foreach (var advisory in new[] { "Proton Pass (optional)", "Launcher junk cleaned" })
    {
        Expect($"'{advisory}' is advisory to the classifier too",
            ModuleStatusClassifier.IsInfoTitle(advisory));
    }
    var braveNoProton = ModuleStatusClassifier.Classify(
        "brave", isApplied: true, statusText: "Already optimized", detail: "",
        new[]
        {
            R("Brave installed", true), R("Telemetry off", true),
            R("Proton Pass (optional)", false),
        });
    Expect("Brave reads applied when only the optional extension is absent",
        braveNoProton.Kind == "applied", $"got {braveNoProton.Kind} / {braveNoProton.Text}");

    // Info rows must never be able to carry a module to "applied" on their own.
    var infoPlusOff = ModuleStatusClassifier.Classify(
        "nvidia", isApplied: false, statusText: "", detail: "",
        new[] { R("Policy", true), R("Latency / sync policy", true), R("3D profiles", false) });
    Expect("info rows cannot mask a real row that is off",
        infoPlusOff.Kind is "ready" or "partial", $"got {infoPlusOff.Kind}");

    // ── System module: firmware rows are advisory, never blocking ──────────────────────
    // Exo cannot switch XMP on. If a firmware row counted as checkable, a machine with XMP
    // off could never read applied no matter how much Exo correctly set — the module would
    // be permanently stuck at "partial" and keep re-prompting for work already done.
    var firmwareOff = ModuleStatusClassifier.Classify(
        "system", isApplied: true, statusText: "Applied · 16/16 on", detail: "",
        new[]
        {
            R("cpu-core-parking", true), R("hags", true), R("trim", true),
            R("Memory profile (XMP/EXPO) (firmware)", false),
            R("Resizable BAR (firmware)", false),
        });
    Expect("firmware rows do not stop a fully applied machine reading applied",
        firmwareOff.Kind == "applied", $"got {firmwareOff.Kind}");

    // Rows Exo only reports on are advisory too. The AMD module surfaces the chipset driver
    // it deliberately does not install; if that counted as checkable, a Ryzen box would sit
    // at "partial" forever for a thing Exo will never change.
    var amdInfoOnly = ModuleStatusClassifier.Classify(
        "amd", isApplied: true, statusText: "", detail: "",
        new[]
        {
            R("Chipset driver (info)", true),
            R("Radeon background tasks off", true),
            R("Radeon telemetry off", true),
        });
    Expect("an (info) row cannot hold a module at partial",
        amdInfoOnly.Kind == "applied", $"got {amdInfoOnly.Kind}");
    Expect("'(info)' rows are advisory to the classifier",
        ModuleStatusClassifier.IsInfoTitle("Chipset driver (info)"));

    // ...and equally must not carry a machine to applied on their own.
    var firmwareOnlyGood = ModuleStatusClassifier.Classify(
        "system", isApplied: false, statusText: "", detail: "",
        new[]
        {
            R("Memory profile (XMP/EXPO) (firmware)", true),
            R("cpu-core-parking", false), R("hags", false),
        });
    Expect("good firmware cannot mask unset system levers",
        firmwareOnlyGood.Kind is "ready" or "partial", $"got {firmwareOnlyGood.Kind}");

    // A brand-new machine: nothing Exo owns is set. Must be offered, not skipped.
    var freshSystem = ModuleStatusClassifier.Classify(
        "system", isApplied: false, statusText: "11 of 11 to tune", detail: "",
        new[] { R("cpu-boost-mode", false), R("hags", false), R("mmcss-responsiveness", false) });
    Expect("an untuned machine offers the system module",
        freshSystem.Kind == "ready", $"got {freshSystem.Kind}");

    // Spotify absent must read missing, not ready — otherwise the orb offers to tune an app
    // that is not there, which is the Games bug pointed the other way.
    var noSpotify = ModuleStatusClassifier.Classify(
        "spotify", isApplied: false,
        statusText: "Not installed", detail: "Spotify is not installed on this PC.",
        new[] { R("Spotify installed", false) });
    Expect("absent Spotify reads missing", noSpotify.Kind == "missing", $"got {noSpotify.Kind}");
}

// ── System / Spotify apply safety ──────────────────────────────────────────────────────
{
    var sysPath = Path.Combine(repo, "Exo", "Services", "SystemNativeApply.cs");
    Expect("SystemNativeApply.cs exists", File.Exists(sysPath));
    if (File.Exists(sysPath))
    {
        var sys = File.ReadAllText(sysPath);

        // Battery profile stays untouched. setdcvalueindex anywhere in this module would mean
        // a laptop keeps every core unparked and PCIe links awake on battery for nothing.
        Expect("system module never writes the battery power profile",
            !HasNonCommentText(sys, "setdcvalueindex") && !HasNonCommentText(sys, "DCSettingIndex"));

        // Repair must refuse rather than invent. Writing Windows' defaults over settings the
        // user chose is not a restore.
        Expect("Repair without a snapshot refuses instead of guessing defaults",
            sys.Contains("No snapshot from a previous Apply", StringComparison.Ordinal));

        // The second Apply must not capture Exo's own values as the restore point.
        Expect("the original pre-Exo snapshot is never overwritten",
            sys.Contains("keeping the original pre-Exo snapshot", StringComparison.Ordinal));

        // Power now lives in the Exo plan rather than being written onto the user's own plan.
        Expect("system module no longer edits the plan the user chose",
            sys.Contains("ExoPowerPlan.BuildApplyOps", StringComparison.Ordinal)
            && !HasNonCommentText(sys, "powercfg:"));

        // Folklore that must never appear here. Each was considered and rejected in
        // docs/SYSTEM-EVIDENCE.md; a future edit reintroducing one should fail the build.
        foreach (var banned in new[]
        {
            "Win32PrioritySeparation",  // real key, cargo-culted value, no measurable effect
            "DisableDynamicTick",       // breaks timer coalescing, costs battery, gains nothing
            "EmptyStandbyList",         // the standby list is cache; dropping it slows reads
            "TimerResolution",          // Windows 11 manages this per-process already
        })
        {
            Expect($"system module does not ship folklore: {banned}", !HasNonCommentText(sys, banned));
        }

        // NetworkLogic.ForbiddenApplyPatterns already bans NetworkThrottlingIndex -1 and
        // 4294967295 as folklore ("ffffffff can raise DPC/audio issues" — docs/TWEAK-AUDIT.md,
        // docs/INTERNET-GOLDEN-PATH.md, removed once already in 3.13.7). But that ban is
        // enforced by scanning generated PowerShell text, and 4.4.0 reintroduced the exact same
        // value here as a C# RegLever, which no gate was looking at. It then fought Steam's
        // restamp on the identical key — two owners, two values, neither module able to read
        // applied while the other had run. Same rule, now enforced on the native path too.
        foreach (var bannedThrottle in new[] { "\"NetworkThrottlingIndex\", -1", "\"NetworkThrottlingIndex\", 4294967295" })
        {
            Expect($"system module does not disable network throttling ({bannedThrottle})",
                !HasNonCommentText(sys, bannedThrottle));
        }

        // One owner for machine-wide MMCSS / power policy. AGENTS.md scopes host policy to the
        // system module; a second writer in the bridge is what created the ping-pong above.
        var bridgeForHostPolicy = ReadBridgeSources(repo);
        Expect("only the system module writes the machine-wide MMCSS / power-throttling keys",
            !HasNonCommentText(bridgeForHostPolicy, "\"NetworkThrottlingIndex\"")
            && !HasNonCommentText(bridgeForHostPolicy, "\"PowerThrottlingOff\""));

        // A refusal that happened BEFORE the script ran can never be soft-failed away. Steam's
        // deep-pack soft-fail exists to forgive "it ran and partly failed"; a missing script, a
        // SHA-256 mismatch against the shipped manifest, or PowerShell 7 being unavailable are
        // none of those. Folding them in reported "Optimizer integrity failed (SHA-256
        // mismatch). Reinstall Exo before applying." to the user as a successful Steam optimize
        // — the one message that must not be swallowed, hidden by the one branch built to be
        // forgiving. Asserted on the typed flag rather than message wording, because wording is
        // not a contract and rephrasing it would silently re-open the hole.
        var runnerSource = File.ReadAllText(Path.Combine(repo, "Exo", "Services", "PowerShellRunnerService.cs"));
        Expect("every pre-execution refusal is flagged as one",
            System.Text.RegularExpressions.Regex.Matches(runnerSource, @"RefusedBeforeExecution = true").Count >= 3,
            "script-missing, integrity-failed and pwsh-unavailable must each set it");
        Expect("the deep-pack soft-fail cannot swallow a pre-execution refusal",
            bridgeForHostPolicy.Contains("!result.RefusedBeforeExecution", StringComparison.Ordinal));

        // The failure shown to the user is picked out of the whole transcript. Matching every
        // line containing "failed" put four of the script's own [+] success lines — including
        // a "failed=0" count — and a raw EXO_PROGRESS protocol line into the dialog, ahead of
        // the single [-] line that said what went wrong.
        Expect("the error shown to the user prefers the line the script marked fatal",
            runnerSource.Contains("l.StartsWith(\"[-]\", StringComparison.Ordinal) ||", StringComparison.Ordinal)
            && runnerSource.Contains("static bool IsNoise(string line)", StringComparison.Ordinal));
        Expect("the progress protocol never leaks into an error message",
            runnerSource.Contains("line.StartsWith(\"EXO_PROGRESS\"", StringComparison.Ordinal));

        // Detect rows are read by a human. Surfacing the internal lever id ("cpu-core-parking")
        // instead of its title ("Core parking") turns the module's whole feature list into
        // debug output.
        Expect("system detect rows use human titles, not internal ids",
            sys.Contains("rows.Add((lever.Title,", StringComparison.Ordinal)
            && !sys.Contains("rows.Add((lever.Id,", StringComparison.Ordinal));

        // Staged ops must run whether or not Exo is already elevated. powercfg is a process
        // invocation, not a key write, so it is ALWAYS staged — and the "already admin" branch
        // used to be empty, which meant running Exo as administrator silently skipped every
        // CPU power setting while still reporting each step ok.
        var nas = File.ReadAllText(Path.Combine(repo, "Exo", "Services", "NativeApplyService.cs"));
        Expect("staged ops still run when already elevated",
            nas.Contains("Already admin, but staged ops still have to RUN", StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.IsMatch(nas,
                @"IsAdministrator\(\)\s*&&\s*result\.ElevatedHklmOps\.Count\s*>\s*0\s*\)\s*\{[^}]*ApplyElevatedOpsAsync"));

        // Apply must not proceed past a failed snapshot: this module changes CPU power policy
        // and machine-wide registry state, and without a snapshot none of it can be undone.
        Expect("a failed snapshot aborts apply rather than leaving unundoable changes",
            sys.Contains("Could not record the current settings, so nothing was changed", StringComparison.Ordinal));

    }

    // ── Reachability: every service must have a caller ────────────────────────────────────
// This release's headline fix was Set-NvidiaDisplayPreferences: defined, never called, and the
// UI reported success anyway. Then four Phase C services shipped the same way - written, unit
// tested, and reachable from nothing but their own tests. Every gate passed, because they test
// functions directly instead of asking whether anything reaches them.
//
// A service whose only references are itself and the smoke suite is dead code, however well
// tested. This is the check that catches the next one.
{
    var servicesDir = Path.Combine(repo, "Exo", "Services");
    var allSource = new List<string>();
    foreach (var dir in new[]
             {
                 Path.Combine(repo, "Exo", "Services"),
                 Path.Combine(repo, "Exo", "ViewModels"),
                 Path.Combine(repo, "Exo", "Helpers"),
                 Path.Combine(repo, "Exo", "Models"),
             })
    {
        if (Directory.Exists(dir)) allSource.AddRange(Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories));
    }
    var mainWindow = Path.Combine(repo, "Exo", "MainWindow.xaml.cs");
    if (File.Exists(mainWindow)) allSource.Add(mainWindow);

    foreach (var file in Directory.GetFiles(servicesDir, "*.cs"))
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        // Partial classes split across files reference each other by definition.
        if (stem.Contains('.')) continue;

        // Match the types the file DECLARES, not its name. NativeApplyModels.cs declares
        // NativeApplyStep, NativeApplyResult and NativeModuleState - all used everywhere - and
        // its filename is not a type at all, so a filename match reported it as dead.
        var declared = System.Text.RegularExpressions.Regex
            .Matches(File.ReadAllText(file), @"(?:class|record|enum|interface)\s+(\w+)")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        if (declared.Count == 0) continue;

        var others = allSource
            .Where(f => !string.Equals(f, file, StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).StartsWith(stem + ".", StringComparison.Ordinal))
            .Select(File.ReadAllText).ToList();

        var reachable = declared.Any(t => others.Any(src => src.Contains(t, StringComparison.Ordinal)));
        Expect($"service is reachable from app code: {stem}", reachable,
            $"declares [{string.Join(", ", declared)}] and nothing outside its own tests uses any of them");
    }
}

// ── The log has to answer the question without a follow-up ────────────────────────────
// Diagnosing 4.4.0 from real logs meant asking which GPU, which driver, which Windows, and
// whether a setting had actually changed - all facts the app already had and was not writing
// down. "STEP hags|ok" cannot distinguish "already correct" from "Exo changed it" from "Exo
// wrote it and it did not take", and those are the only three answers that matter.
{
    var applyLog = File.ReadAllText(Path.Combine(repo, "Exo", "Services", "ModuleApplyLog.cs"));
    foreach (var fact in new[] { "os", "cpu", "gpu", "nvidiaDriver", "disk", "kits" })
        Expect($"every run records {fact}", applyLog.Contains($"Fact(\"{fact}\"", StringComparison.Ordinal));

    // A probe that throws must cost one line, never the run - a logger that can fail an apply
    // is worse than no logger.
    Expect("a machine probe that throws cannot break the apply",
        applyLog.Contains("catch (Exception ex) { Line($\"  {label}=<unreadable: {ex.GetType().Name}>\"); }", StringComparison.Ordinal));

    var native = File.ReadAllText(Path.Combine(repo, "Exo", "Services", "NativeApplyService.cs"));
    Expect("registry writes record the value they replaced",
        native.Contains("function Get-ExoRegValue", StringComparison.Ordinal)
        && native.Contains("EXO_CHANGE:", StringComparison.Ordinal));
    Expect("already-correct is distinguished from changed",
        native.Contains("if ($before -eq $after) { 'already' } else { 'changed' }", StringComparison.Ordinal));
    // Both write kinds, or half the pack is invisible.
    Expect("both dword and string writes capture before/after",
        System.Text.RegularExpressions.Regex.Matches(native, @"\$wasVal = Get-ExoRegValue").Count >= 2);
}

// ── "Applied" must mean "applied with THESE tweaks" ───────────────────────────────────
// Internet was reported applied and never re-offered after its tweak set changed underneath
// it, so a machine sat on a superseded configuration and the orb still called the rig good to
// go. Every module had the hole; Internet is only where it showed.
{
    var vt = Path.Combine(repo, "Exo", "Services", "ModuleTweakVersion.cs");
    Expect("ModuleTweakVersion.cs exists", File.Exists(vt));
    if (File.Exists(vt))
    {
        var text = File.ReadAllText(vt);

        // An absent stamp means Exo does not know what is on this machine, which is stale, not
        // current. The first version of this said the opposite and reproduced the very bug it
        // was meant to avoid: a machine whose Internet tuning had changed was still never
        // offered, because it had no stamp to be stale against.
        Expect("a machine with no stamp is offered, not assumed current",
            text.Contains("if (!Read().TryGetValue(module, out var seen)) return true;", StringComparison.Ordinal),
            "an unknown configuration must be re-offered");

        // Every module the shell walks needs an entry, or its tweaks can change silently.
        foreach (var m in new[] { "internet", "nvidia", "brave", "discord", "steam", "system", "spotify", "amd" })
            Expect($"tweak version tracked for {m}", text.Contains($"[\"{m}\"]", StringComparison.Ordinal));
    }

    var bridgeText = ReadBridgeSources(repo);
    Expect("a stale tweak set downgrades applied so the orb re-offers it",
        bridgeText.Contains("ModuleTweakVersion.IsStale(id)", StringComparison.Ordinal)
        && bridgeText.Contains("ModuleTweakVersion.IsStale(\"internet\")", StringComparison.Ordinal));

    // Stamping an apply that came back "ready" would mark a superseded config as current.
    //
    // Matched on the guard rather than on one exact source line. There are two call sites now:
    // the games branch returns early and needs its own stamp (without it Games could never be
    // stamped at all, so it read "partial - tuning changed since" forever), and both sites
    // re-read detect afterwards because the payload was previously built before the stamp
    // existed. Pinning the old single-line spelling would have failed on a correct refactor
    // while still passing if someone added a third, unguarded Stamp call. Counting guards
    // against calls is what the contract actually is.
    var stampCalls = System.Text.RegularExpressions.Regex
        .Matches(bridgeText, @"ModuleTweakVersion\.Stamp\(").Count;
    var stampGuards = System.Text.RegularExpressions.Regex
        .Matches(bridgeText, @"KindOf\(\w+\) is ""applied"" or ""partial""").Count;
    Expect("the stamp is only written when the machine agrees the apply landed",
        stampCalls > 0 && stampCalls == stampGuards,
        $"{stampCalls} Stamp call(s), {stampGuards} applied/partial guard(s)");
}

// ── Reachability, one layer up: every bridge RPC must have a UI caller ────────────────
// The gate above checks that C# services are reached from C#. That is not the whole chain.
// The Phase C driver RPCs passed it - WebHostBridge calls the installer, so the service was
// "reachable" - while the React side never called the RPCs. Backend complete, frontend
// silent, every gate green, and pressing nothing in the app could ever run it.
//
// The UI is the only caller of the bridge. An RPC no page invokes is a feature that exists
// only in the transcript.
{
    var uiDir = Path.Combine(repo, "ui", "src");
    if (Directory.Exists(uiDir))
    {
        var bridgeSource = ReadBridgeSources(repo);

        // Only the dispatch switch, not every string in the file: `method switch {` through
        // its default arm. Scanning the whole file would sweep up unrelated literals.
        var dispatch = System.Text.RegularExpressions.Regex.Match(
            bridgeSource, @"method switch\s*\{(.+?)_\s*=>\s*throw",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Expect("the bridge dispatch switch is still shaped the way this gate reads it", dispatch.Success,
            "no `method switch { ... _ => throw` block found - the gate would silently check nothing");

        if (dispatch.Success)
        {
            var methods = System.Text.RegularExpressions.Regex
                .Matches(dispatch.Groups[1].Value, @"""([a-z][a-zA-Z]*\.[a-zA-Z]+)""\s*=>")
                .Select(m => m.Groups[1].Value).Distinct().ToList();

            Expect("the gate found the bridge's RPC names", methods.Count >= 15,
                $"only found {methods.Count} - the dispatch shape probably changed");

            var uiText = string.Concat(Directory
                .GetFiles(uiDir, "*.ts*", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

            foreach (var m in methods)
                Expect($"bridge RPC has a UI caller: {m}",
                    uiText.Contains($"'{m}'", StringComparison.Ordinal)
                    || uiText.Contains($"\"{m}\"", StringComparison.Ordinal)
                    || uiText.Contains($"`{m}`", StringComparison.Ordinal),
                    "the host answers it and nothing in ui/src ever asks");
        }
    }
}

// ── The Exo power plan ─────────────────────────────────────────────────────────────
    var planPath = Path.Combine(repo, "Exo", "Services", "ExoPowerPlan.cs");
    Expect("ExoPowerPlan.cs exists", File.Exists(planPath));
    if (File.Exists(planPath))
    {
        var plan = File.ReadAllText(planPath);

        // The minimum processor state is chassis-dependent, and that is the whole contract.
        // These two assertions previously pinned a flat 5 as correct; that was the conservative
        // choice, not the fastest one, and it is now decided from measured hardware instead:
        //
        //   desktop  100  clocks never drop, no ramp latency, costs idle watts a tower has
        //   laptop     5  a thin chassis is power- and thermally-limited, and holding base
        //                 clocks at idle spends the exact headroom boost needs under load,
        //                 so pinning it there is slower in practice
        //
        // Asserting the split rather than either value on its own.
        Expect("minimum processor state is decided from the chassis, not fixed",
            System.Text.RegularExpressions.Regex.IsMatch(plan,
                @"ProcThrottleMin,\s*cpu\.IsDesktop\s*\?\s*100\s*:\s*5"));
        Expect("chassis is measured, not assumed",
            plan.Contains("GetSystemPowerStatus", StringComparison.Ordinal)
            && plan.Contains("BatteryFlag == 128", StringComparison.Ordinal));

        // Base plan preference: Ultimate Performance, then High Performance, then Balanced.
        // The base only sets a starting point since every value is written explicitly after,
        // but starting from the fastest one means anything Exo does not set is already right.
        Expect("the plan prefers Ultimate Performance as its base",
            plan.Contains("e9a42b02-d5df-448d-aa00-03f14749eb61", StringComparison.Ordinal));
        Expect("the base falls back through High Performance to Balanced",
            plan.Contains("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", StringComparison.Ordinal)
            && plan.Contains("381b4222-f694-41f0-9685-ff5bb260df2e", StringComparison.Ordinal));

        // Processor idle disable is the most-recommended "max performance" setting on the
        // internet and it is excluded on purpose: stopping cores from idling removes the
        // thermal and power headroom modern boost algorithms spend, which lowers peak clocks.
        // A performance setting that costs performance.
        Expect("processor idle disable stays out",
            !HasNonCommentText(plan, "5d76a2ca-e8c0-402f-a133-2158492d58ad"));

        // The settings that make this a performance plan rather than a renamed Balanced.
        foreach (var (name, needle) in new[]
        {
            ("energy performance preference at max", "PerfEpp, 0,"),
            ("rocket clock ramp", "PerfIncPolicy, 2,"),
            ("slow clock step-down", "PerfDecPolicy, 1,"),
            ("latency hint at full performance", "LatencyHintPerf, 100,"),
            ("all cores available", "CpMaxCores, 100,"),
        })
        {
            Expect($"performance plan sets {name}", plan.Contains(needle, StringComparison.Ordinal));
        }

        // Every setting is probed before it is written. Power settings vary by CPU driver,
        // Windows edition and OEM policy - writing one blind is how a plan fills with values
        // that quietly do nothing, which is the failure this repo keeps finding.
        Expect("settings are probed for existence before being written",
            plan.Contains("SettingExists(s.Subgroup, s.Setting)", StringComparison.Ordinal));

        // Hybrid scheduling is gated on measured topology, not on a parsed model name. An
        // i5-14400 and an i9-14900K are both hybrid and neither string carries the core split.
        Expect("hybrid scheduling keys off measured EfficiencyClass, not a name string",
            plan.Contains("EfficiencyClass", StringComparison.Ordinal)
            && plan.Contains("cpu.IsHybrid", StringComparison.Ordinal)
            && !System.Text.RegularExpressions.Regex.IsMatch(plan, @"Name\.Contains\(""(12th|13th|14th|Ryzen)"));

        // AC only. The duplicated plan keeps Balanced's battery profile untouched.
        Expect("the plan never writes the battery profile",
            !HasNonCommentText(plan, "setdcvalueindex") && !HasNonCommentText(plan, "DCSettingIndex"));

        // Order matters: Windows refuses to delete the active scheme, so Repair must switch
        // away first or it leaves the plan behind AND the machine still running it.
        var restoreBody = plan[plan.IndexOf("BuildRestoreOps", StringComparison.Ordinal)..];
        var activeIdx = restoreBody.IndexOf("planactive:", StringComparison.Ordinal);
        var deleteIdx = restoreBody.IndexOf("plandelete:", StringComparison.Ordinal);
        Expect("Repair re-activates the previous plan before deleting the Exo one",
            activeIdx >= 0 && deleteIdx > activeIdx,
            $"active@{activeIdx} delete@{deleteIdx}");

        // A fixed GUID, so a second Apply reuses the plan instead of stacking duplicates.
        Expect("the Exo plan uses a fixed GUID",
            plan.Contains("ExoSchemeGuid = \"7ae4b8a5", StringComparison.Ordinal));

        // The plan name is interpolated into a powercfg command line.
        Expect("the plan name is sanitised before reaching a command line",
            plan.Contains("char.IsLetterOrDigit(c)", StringComparison.Ordinal));

        // Detect must require the plan to be ACTIVE, not merely present. A created-but-inactive
        // plan changes nothing, and reporting it as applied is the exact false green this
        // release exists to remove.
        Expect("an inactive Exo plan does not read as applied",
            plan.Contains("var ok = active && drifted.Count == 0", StringComparison.Ordinal)
            && plan.Contains("Windows is running a different plan", StringComparison.Ordinal));
        // ...and neither does one the user has since edited.
        Expect("a drifted Exo plan does not read as applied",
            plan.Contains("have been changed since", StringComparison.Ordinal));
    }

    var spotPath = Path.Combine(repo, "Exo", "Services", "SpotifyNativeApply.cs");
    Expect("SpotifyNativeApply.cs exists", File.Exists(spotPath));
    if (File.Exists(spotPath))
    {
        var spot = File.ReadAllText(spotPath);

        // Spotify rewrites prefs from memory on exit. Applying under a running client is a
        // guaranteed-false green, so it must be a hard failure rather than a warning.
        Expect("apply fails rather than writing prefs under a running Spotify",
            spot.Contains("Spotify would not close", StringComparison.Ordinal)
            && spot.Contains("NativeApplyResult.Fail", StringComparison.Ordinal));

        // Same snapshot discipline as every other module.
        Expect("Spotify keeps the original prefs backup",
            spot.Contains("keeping the original backup", StringComparison.Ordinal));
        Expect("Spotify Repair restores from a recorded GPU snapshot",
            spot.Contains("GpuTopology.RestorePreferences", StringComparison.Ordinal)
            && spot.Contains("GpuTopology.SnapshotPreferences", StringComparison.Ordinal));

        // Unknown lines in the prefs file must survive a write.
        Expect("Spotify prefs writer preserves keys Exo does not know about",
            spot.Contains("Unknown lines are preserved", StringComparison.Ordinal));
    }

    // Both modules must be reachable end to end. Being unreachable while every gate stayed
    // green is precisely what shipped three times this month.
    var bridge = ReadBridgeSources(repo);
    foreach (var id in new[] { "system", "spotify" })
    {
        Expect($"{id} is routed in detect", bridge.Contains($"\"{id}\" => MapState(\"{id}\"", StringComparison.Ordinal));
        Expect($"{id} is in the verify-all sweep",
            System.Text.RegularExpressions.Regex.IsMatch(bridge, $@"""{id}""[,\s\}}]"));
    }
    Expect("system and spotify have a native apply route",
        File.ReadAllText(Path.Combine(repo, "Exo", "Services", "NativeApplyService.cs"))
            .Contains("or \"system\" or \"spotify\"", StringComparison.Ordinal));

    var amdPath = Path.Combine(repo, "Exo", "Services", "AmdNativeApply.cs");
    Expect("AmdNativeApply.cs exists", File.Exists(amdPath));
    if (File.Exists(amdPath))
    {
        var amd = File.ReadAllText(amdPath);
        var snapshotAt = amd.IndexOf("var snapshot = WriteSnapshot(snap)", StringComparison.Ordinal);
        var taskMutationAt = amd.IndexOf("schtask:disable", StringComparison.Ordinal);
        Expect("Radeon snapshot is durable before the first mutation",
            snapshotAt >= 0 && taskMutationAt > snapshotAt
            && amd.Contains("Could not record the current Radeon settings, so nothing was changed", StringComparison.Ordinal));
        Expect("Radeon task changes stage one elevated batch",
            amd.Contains("schtask:disable|", StringComparison.Ordinal)
            && amd.Contains("schtask:enable|", StringComparison.Ordinal)
            && amd.Contains("pending-elev", StringComparison.Ordinal));
        Expect("Radeon telemetry reports failed writes",
            amd.Contains("machineFailures", StringComparison.Ordinal)
            && amd.Contains("userFailures", StringComparison.Ordinal));
    }

    var nativeApply = File.ReadAllText(Path.Combine(repo, "Exo", "Services", "NativeApplyService.cs"));
    Expect("Radeon is routed through the shared native elevation path",
        nativeApply.Contains("or \"amd\"", StringComparison.Ordinal)
        && nativeApply.Contains("AmdNativeApply.Apply", StringComparison.Ordinal)
        && nativeApply.Contains("schtask:", StringComparison.Ordinal));
    Expect("Radeon is verified from live state after Apply",
        bridge.Contains("AmdNativeApply.Detect()", StringComparison.Ordinal)
        && bridge.Contains("Radeon changes ran but live verification failed", StringComparison.Ordinal));

    // Every backend module must be reachable in the shell, or it is invisible however well
    // the engine supports it. This used to read the Shell nav and the ModulePage route list;
    // the single-screen shell has neither, and the row table is where the ids live now. Same
    // rule, pointed at the file that decides it.
    var shellModulesTs = Path.Combine(repo, "ui", "src", "lib", "modules.ts");
    Expect("the shell's module table exists", File.Exists(shellModulesTs));
    if (File.Exists(shellModulesTs))
    {
        var shellModules = File.ReadAllText(shellModulesTs);
        foreach (var id in new[] { "system", "spotify" })
            Expect($"{id} is a shell row", shellModules.Contains($"id: '{id}'", StringComparison.Ordinal));
    }
}

// The two blocks that used to sit here matched source text in WebHostBridge to prove the
// Games classification and the empty-checkable rule. That logic now lives in
// ModuleStatusClassifier and is exercised directly by the scenarios above, with made-up
// machines and checked answers. Testing the behaviour supersedes grepping for the code
// that implements it, so the string matches are gone rather than repointed — they broke
// the moment the code moved, which is exactly what makes them the weaker check.

// --- Chipset drivers (AMD / Intel CPU platform software) ---
{
    var chipSrc = Path.Combine(repo, "Exo", "Services", "ChipsetDriverInstaller.cs");
    Expect("ChipsetDriverInstaller.cs exists", File.Exists(chipSrc));
    if (File.Exists(chipSrc))
    {
        var src = File.ReadAllText(chipSrc);
        Expect("chipset has three-stage Plan/Prepare/Execute",
            src.Contains("PrepareAsync", StringComparison.Ordinal)
            && src.Contains("ExecuteAsync", StringComparison.Ordinal)
            && src.Contains("InstallPlan", StringComparison.Ordinal));
        Expect("chipset never flashes BIOS",
            !HasNonCommentText(src, "flash") || src.Contains("never flashes BIOS", StringComparison.OrdinalIgnoreCase));
        Expect("chipset auto path uses Windows Update / MUC",
            src.Contains("ChipsetMucClient", StringComparison.Ordinal)
            && src.Contains("ChipsetWindowsUpdate", StringComparison.Ordinal)
            && src.Contains("windows-update", StringComparison.Ordinal));
        Expect("chipset check and prepare are time bounded",
            src.Contains("CheckWindowsUpdateBudget = TimeSpan.FromSeconds(12)", StringComparison.Ordinal)
            && src.Contains("PrepareBudget = TimeSpan.FromMinutes(2)", StringComparison.Ordinal)
            && src.Contains("CancelAfter(PrepareBudget)", StringComparison.Ordinal));
        Expect("chipset elevated INF install is stoppable and time bounded",
            src.Contains("budget.CancelAfter(ClassicInstallBudget)", StringComparison.Ordinal)
            && src.Contains("p.Kill(entireProcessTree: true)", StringComparison.Ordinal)
            && src.Contains("Chipset INF installation timed out", StringComparison.Ordinal));
    }
    Expect("ChipsetMucClient exists",
        File.Exists(Path.Combine(repo, "Exo", "Services", "ChipsetMucClient.cs")));
    Expect("ChipsetWindowsUpdate exists",
        File.Exists(Path.Combine(repo, "Exo", "Services", "ChipsetWindowsUpdate.cs")));
    var chipsetWuSrc = Path.Combine(repo, "Exo", "Services", "ChipsetWindowsUpdate.cs");
    if (File.Exists(chipsetWuSrc))
    {
        var wu = File.ReadAllText(chipsetWuSrc);
        Expect("chipset Windows Update install is stoppable and time bounded",
            wu.Contains("InstallBudget = TimeSpan.FromMinutes(20)", StringComparison.Ordinal)
            && wu.Contains("budget.CancelAfter(InstallBudget)", StringComparison.Ordinal)
            && wu.Contains("p.Kill(entireProcessTree: true)", StringComparison.Ordinal));
    }
    var logicSrc = Path.Combine(repo, "Exo", "Services", "ChipsetDriverLogic.cs");
    Expect("ChipsetDriverLogic.cs exists", File.Exists(logicSrc));
    if (File.Exists(logicSrc))
    {
        var logic = File.ReadAllText(logicSrc);
        Expect("chipset strip has never-strip protectors",
            logic.Contains("NeverStripContains", StringComparison.Ordinal)
            || logic.Contains("neverStrip", StringComparison.OrdinalIgnoreCase));
    }
    var catalog = Path.Combine(repo, "Exo", "Data", "chipset-catalog.json");
    Expect("chipset catalog ships", File.Exists(catalog));
    if (File.Exists(catalog))
    {
        var cat = File.ReadAllText(catalog);
        Expect("catalog has AMD chipset entry", cat.Contains("amd-chipset", StringComparison.Ordinal));
        Expect("catalog has Intel chipset entry", cat.Contains("intel-chipset", StringComparison.Ordinal));
        Expect("catalog support URLs are vendor hosts",
            cat.Contains("amd.com", StringComparison.OrdinalIgnoreCase)
            && cat.Contains("intel.com", StringComparison.OrdinalIgnoreCase));
    }
    var bridge = ReadBridgeSources(repo);
    Expect("bridge exposes chipset.driverCheck", bridge.Contains("chipset.driverCheck", StringComparison.Ordinal));
    Expect("bridge exposes chipset.driverPrepare", bridge.Contains("chipset.driverPrepare", StringComparison.Ordinal));
    Expect("bridge exposes chipset.driverInstall", bridge.Contains("chipset.driverInstall", StringComparison.Ordinal));
    var hostTs = File.ReadAllText(Path.Combine(repo, "ui", "src", "lib", "host.ts"));
    Expect("UI host has chipsetCheck", hostTs.Contains("chipsetCheck", StringComparison.Ordinal));
    Expect("UI host has chipsetPrepare", hostTs.Contains("chipsetPrepare", StringComparison.Ordinal));
    Expect("UI host has chipsetInstall", hostTs.Contains("chipsetInstall", StringComparison.Ordinal));

    var nvidiaInstallerSrc = File.ReadAllText(Path.Combine(repo, "Exo", "Services", "NvidiaDriverInstaller.cs"));
    Expect("NVIDIA driver install is stoppable and time bounded",
        nvidiaInstallerSrc.Contains("DriverInstallBudget = TimeSpan.FromMinutes(20)", StringComparison.Ordinal)
        && nvidiaInstallerSrc.Contains("budget.CancelAfter(DriverInstallBudget)", StringComparison.Ordinal)
        && nvidiaInstallerSrc.Contains("KillProcessTree(proc)", StringComparison.Ordinal));
    var nvidiaPackageSrc = File.ReadAllText(Path.Combine(repo, "Exo", "Services", "NvidiaDriverPackage.cs"));
    Expect("standard NVIDIA clean install requests no automatic reboot",
        nvidiaPackageSrc.Contains("\"noreboot\"", StringComparison.Ordinal));

    // Pure version / socket / strip rules without a machine.
    Expect("version compare 7.01 > 6.10",
        ChipsetDriverLogic.CompareVersions("7.01.08.129", "6.10.22.527") > 0);
    Expect("5600X maps to AM4",
        ChipsetDriverLogic.InferSocket("AMD Ryzen 5 5600X 6-Core Processor", "amd") == "AM4");
    Expect("7800X3D maps to AM5",
        ChipsetDriverLogic.InferSocket("AMD Ryzen 7 7800X3D 8-Core Processor", "amd") == "AM5");
    Expect("i9-14900K maps to LGA1700",
        ChipsetDriverLogic.InferSocket("Intel(R) Core(TM) i9-14900K", "intel") == "LGA1700");

    var catalogPath = Path.Combine(repo, "Exo", "Data", "chipset-catalog.json");
    var amdSpec = ChipsetDriverLogic.LoadCatalog(catalogPath)
        .FirstOrDefault(p => p.Vendor.Equals("amd", StringComparison.OrdinalIgnoreCase));
    if (amdSpec is not null)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "exo-chipset-strip-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(tmp, "GPIO"));
            Directory.CreateDirectory(Path.Combine(tmp, "RyzenMaster"));
            File.WriteAllText(Path.Combine(tmp, "GPIO", "x.inf"), "x");
            File.WriteAllText(Path.Combine(tmp, "RyzenMaster", "junk.exe"), "x");
            var strip = ChipsetDriverLogic.StripPackage(tmp, amdSpec);
            Expect("strip removes RyzenMaster", !Directory.Exists(Path.Combine(tmp, "RyzenMaster")),
                string.Join(",", strip.Removed));
            Expect("strip keeps GPIO", Directory.Exists(Path.Combine(tmp, "GPIO")));
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* best effort */ }
        }
    }
}

Log(failed == 0 ? "=== ALL CONTRACTS PASS ===" : $"=== {failed} CONTRACT FAILURE(S) ===");
try { File.WriteAllLines(logPath, lines); } catch { /* best effort */ }
return failed == 0 ? 0 : 1;


// True when `needle` appears on a line that is actual code rather than a comment.
// Lets the source explain why a folklore option is excluded without that
// explanation tripping the assertion that it is never written.
static bool HasNonCommentText(string source, string needle)
{
    foreach (var raw in source.Split('\n'))
    {
        var line = raw.Trim();
        if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("*", StringComparison.Ordinal))
            continue;
        if (line.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
    }
    return false;
}

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
static string ReadBridgeSources(string repo)
{
    var dir = Path.Combine(repo, "Exo", "Services");
    var files = Directory.GetFiles(dir, "WebHostBridge*.cs").OrderBy(f => f, StringComparer.Ordinal).ToArray();
    return string.Join("\n", files.Select(f => File.ReadAllText(f)));
}


