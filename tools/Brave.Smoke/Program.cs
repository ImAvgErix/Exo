// Brave has no PowerShell kit -- native C# is the entire apply/repair surface
// (see Exo/Services/BraveNativeApply.cs). This smoke reads the shipped source
// as text and asserts the safety properties promised in AGENTS.md / the
// cleanup plan, mirroring the source-shape checks Ui.Smoke/Contracts.Smoke
// already use for other native-only paths.

var logPath = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "brave-smoke.log");
var lines = new List<string>();
var failed = 0;
void Log(string s) { lines.Add(s); Console.WriteLine(s); }
void Expect(string name, bool cond, string detail = "")
{
    if (cond) Log($"PASS  {name}");
    else { failed++; Log($"FAIL  {name}" + (detail.Length > 0 ? " :: " + detail : "")); }
}

Log("=== Brave.Smoke ===");

var repo = FindRepoRoot();
var applyPath = Path.Combine(repo, "Exo", "Services", "BraveNativeApply.cs");
Expect("BraveNativeApply.cs exists", File.Exists(applyPath));
if (!File.Exists(applyPath))
{
    Log($"=== SUMMARY failed={failed} ===");
    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
    File.WriteAllLines(logPath, lines);
    Environment.Exit(1);
}
var src = File.ReadAllText(applyPath);

// Entry points the bridge/native-apply router depends on.
Expect("Apply entry point", src.Contains("public static NativeApplyResult Apply(", StringComparison.Ordinal));
Expect("Repair entry point", src.Contains("public static NativeApplyResult Repair(", StringComparison.Ordinal));
Expect("Discover entry point", src.Contains("public static BraveInstall Discover(", StringComparison.Ordinal));

// Privacy/telemetry policy pack: every reporting/tracking channel disabled.
foreach (var disabledPolicy in new[]
{
    "MetricsReportingEnabled", "CloudReportingEnabled", "SafeBrowsingExtendedReportingEnabled",
    "UrlKeyedAnonymizedDataCollectionEnabled", "BraveP3AEnabled", "BraveStatsPingEnabled",
    "UserFeedbackAllowed", "DeviceMetricsReportingEnabled", "WebRtcEventLogCollectionAllowed",
    "DomainReliabilityAllowed"
})
{
    Expect($"policy disables {disabledPolicy}",
        System.Text.RegularExpressions.Regex.IsMatch(src,
            $@"\(""{disabledPolicy}"",\s*0,\s*RegistryValueKind\.DWord\)"));
}

// Security-critical exception: component updates (safe-browsing/cert lists) stay ON
// even though every telemetry/reporting channel above is off.
Expect("component updates stay enabled (security exception)",
    System.Text.RegularExpressions.Regex.IsMatch(src,
        @"\(""ComponentUpdatesEnabled"",\s*1,\s*RegistryValueKind\.DWord\)"));

// High-perf GPU preference for the browser process (competitive-gaming host tweak).
Expect("high-perf GPU preference", src.Contains("GpuPreference=2;", StringComparison.Ordinal));

// Repair must be a real undo: full pre-apply snapshot + restore, not a partial reset.
Expect("full snapshot before apply", src.Contains("WriteFullSnapshot(install)", StringComparison.Ordinal));
Expect("full snapshot restore on repair", src.Contains("RestoreFullSnapshot(install)", StringComparison.Ordinal));

// Never silently wipe user data (passwords/bookmarks/history) -- only scoped,
// documented cache/vault table clears are allowed.
Expect("no full profile wipe",
    !src.Contains("Directory.Delete(install.UserData", StringComparison.Ordinal)
    && !src.Contains(".UserData, true)", StringComparison.Ordinal));

Log($"=== SUMMARY failed={failed} ===");
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
File.WriteAllLines(logPath, lines);
Environment.Exit(failed == 0 ? 0 : 1);

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "VERSION")) && Directory.Exists(Path.Combine(dir.FullName, "Exo", "Services")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
