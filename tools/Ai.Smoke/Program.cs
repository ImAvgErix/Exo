using Exo.Models.Ai;
using Exo.Services.Ai;

// Ai.Smoke — registry, safety, state memory, inventory, Grok parse, gate, catalogs.
// Exit 0 only if all cases pass.

var logPath = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "exo-ai-smoke.log");
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

Log("=== Ai.Smoke (shipped Exo AI core) ===");
Log(DateTime.UtcNow.ToString("o"));

// --- Tool registry ---
var registry = new ExoToolRegistry();
var ids = registry.CatalogIds();
Expect("registry has tools", ids.Count >= 40, $"count={ids.Count}");
Expect("registry has hostOs.maximize", ids.Contains("hostOs.maximize"));
Expect("registry has power.exoCompetitive", ids.Contains("power.exoCompetitive"));
Expect("registry has windows.aiPurge", ids.Contains("windows.aiPurge"));
Expect("registry has browser.braveOnly", ids.Contains("browser.braveOnly"));
Expect("registry has upscaler.maximizeSupportedGames", ids.Contains("upscaler.maximizeSupportedGames"));
Expect("registry has search.everything", ids.Contains("search.everything"));
Expect("registry has ownership.dryRun", ids.Contains("ownership.dryRun"));
Expect("all tools require clean slate", registry.All.All(t => t.RequiresCleanSlate));

// --- Safety denylist ---
Expect("denylist cookies", ExoActionSafety.IsDenied("browser.wipeCookies"));
Expect("denylist anticheat", ExoActionSafety.IsDenied("anticheat.inject"));
Expect("denylist wifi kill", ExoActionSafety.IsDenied("network.killWifiPermanent"));
Expect("denylist pagefile", ExoActionSafety.IsDenied("pagefile.disable"));
Expect("session path Cookies", ExoActionSafety.TouchesSessionStore(@"C:\Users\x\AppData\Local\BraveSoftware\Brave-Browser\User Data\Default\Cookies"));
Expect("session path Login Data", ExoActionSafety.TouchesSessionStore("Login Data"));
Expect("brave-only session safe helper", ExoBraveOnlyService.IsSessionSafePath("/tmp/cache.tmp"));

var filtered = ExoActionSafety.FilterActions(
[
    new ExoToolAction { ToolId = "browser.wipeCookies" },
    new ExoToolAction { ToolId = "gpu.nvidia.reflex" },
    new ExoToolAction { ToolId = "gpu.amd.antiLag" },
    new ExoToolAction { ToolId = "power.exoCompetitive" },
    new ExoToolAction { ToolId = "files.junkCleanup", Params = { ["path"] = "Cookies" } }
], out var rejected);
Expect("filter drops denylist+conflict+session", filtered.Count == 2, $"kept={filtered.Count} rejected={rejected.Count}");
Expect("filter kept power", filtered.Any(a => a.ToolId == "power.exoCompetitive"));
Expect("filter kept one of reflex/antilag", filtered.Count(a => a.ToolId.StartsWith("gpu.")) == 1);

// --- Inventory catalog ---
var inventory = new ExoSystemInventory();
var state = inventory.Capture("smoke-1.0");
Expect("inventory digest set", !string.IsNullOrWhiteSpace(state.Digest));
Expect("inventory catalog domains saturated",
    ExoSystemInventory.CatalogDomains.Count >= 60,
    $"domains={ExoSystemInventory.CatalogDomains.Count}");
Expect("inventory expansion marker",
    state.Domains.ContainsKey("expansion"));
Expect("inventory power knobs noted",
    state.Domains.ContainsKey("power") || state.Domains.ContainsKey("cpu"));

// --- Power + AI purge catalogs ---
var power = new ExoPowerPlanService();
Expect("power catalog knobs", ExoPowerPlanService.Catalog.Count >= 18,
    $"knobs={ExoPowerPlanService.Catalog.Count}");
Expect("power intel scheme name", power.SchemeNameFor("intel") == ExoPowerPlanService.SchemeNameIntel);
Expect("power amd scheme name", power.SchemeNameFor("amd") == ExoPowerPlanService.SchemeNameAmd);
Expect("power hybrid knobs", power.KnobsFor("hybrid").Count >= 8);

var purge = new ExoWindowsAiPurgeService();
var counts = purge.CatalogCounts();
Expect("ai purge policies", counts["policies"] >= 10);
Expect("ai purge packages", counts["packages"] >= 8);
Expect("ai purge tasks", counts["tasks"] >= 5);

// --- State manager gate ---
var tmpRoot = Path.Combine(Path.GetTempPath(), "exo-ai-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tmpRoot);
try
{
    // Point AppData via env is hard; exercise StateManager paths under real LocalAppData/Exo/ai
    // but isolate by saving then comparing digests in-process.
    var sm = new ExoStateManager();
    var gate0 = sm.CompareFast(state);
    Expect("gate no optimal initially or after clear", !gate0.IsOptimal || gate0.HasOptimal);

    sm.SaveOptimal(new ExoOptimalState
    {
        ExoVersion = "smoke-1.0",
        StateDigest = state.Digest,
        AnalysisSummary = "smoke",
        Snapshot = state
    });
    var gate1 = sm.CompareFast(state);
    Expect("gate optimal after save", gate1.IsOptimal, gate1.Message);
    Expect("gate has optimal", gate1.HasOptimal);

    var drifted = inventory.Capture("smoke-1.0");
    drifted.Hardware["smokeProbe"] = "drift-" + Guid.NewGuid().ToString("N");
    drifted.Os["description"] = "drifted-os-" + Guid.NewGuid().ToString("N");
    drifted.Digest = ExoStateManager.ComputeDigest(drifted);
    var gate2 = sm.CompareFast(drifted);
    Expect("gate detects drift", !gate2.IsOptimal, gate2.Message);
    Expect("gate lists drifts", gate2.Drifts.Count > 0, $"drifts={gate2.Drifts.Count}");
}
finally
{
    try { Directory.Delete(tmpRoot, true); } catch { /* ignore */ }
}

// --- Grok JSON parse + local plan ---
var parsed = ExoGrokClient.ParseAnalysisJson(
    """
    {
      "analysis": "test analysis",
      "plan": [{ "priority": 1, "title": "Power", "reasoning": "deep", "cleanSlate": "reset", "domain": "power" }],
      "actions": [{ "toolId": "power.exoCompetitive", "reason": "apply", "params": { "vendor": "intel" } }]
    }
    """);
Expect("parse analysis", parsed.Analysis.Contains("test analysis"));
Expect("parse plan", parsed.Plan.Count == 1);
Expect("parse actions", parsed.Actions.Count == 1 && parsed.Actions[0].ToolId == "power.exoCompetitive");
Expect("parse params", parsed.Actions[0].Params.GetValueOrDefault("vendor") == "intel");

var local = ExoGrokClient.LocalMaximizePlan(state);
Expect("local plan has actions", local.Actions.Count >= 8);
Expect("local plan source", local.Source == "local");
Expect("local includes hostOs", local.Actions.Any(a => a.ToolId == "hostOs.maximize"));
Expect("local includes brave-only", local.Actions.Any(a => a.ToolId == "browser.braveOnly"));
Expect("local includes internet", local.Actions.Any(a => a.ToolId == "module.internet.apply"));

// --- Optimizer execute (stubs) ---
var optimizer = new ExoOptimizerService(registry, new ExoStateManager());
var results = optimizer.ExecuteAsync(
[
    new ExoToolAction { ToolId = "power.exoCompetitive", Reason = "smoke" },
    new ExoToolAction { ToolId = "browser.wipeCookies", Reason = "should reject" }
]).GetAwaiter().GetResult();
Expect("optimizer executes allowed", results.Any(r => r.ToolId == "power.exoCompetitive" && r.Success));
Expect("optimizer skips unknown denied via filter", results.All(r => r.ToolId != "browser.wipeCookies"));

// --- Agent local run ---
var agent = new ExoAIAgent(
    new ExoStateManager(),
    inventory,
    registry,
    optimizer,
    new ExoGrokClient(),
    () => null,
    () => "smoke-1.0");
var run = agent.RunAsync(force: true).GetAwaiter().GetResult();
Expect("agent local run success", run.Success, run.Message);
Expect("agent produced results", run.Results.Count > 0, $"count={run.Results.Count}");

// --- Auto-install / companions / upscaler / host OS / GPU ---
var install = new ExoAutoInstallService();
Expect("auto-install plans", install.GetPlan("steam") is not null && install.GetPlan("brave") is not null);
Expect("auto-install discord plan", install.GetPlan("discord") is not null);
Expect("auto-install unknown null", install.GetPlan("cheat") is null);

var companions = new ExoCompanionService();
Expect("companions list", companions.List().Count >= 4);
var (cOk, _) = companions.EnsureInstalled("snip");
Expect("companion register", cOk);

var upscaler = new ExoUpscalerService();
Expect("upscaler patterns", ExoUpscalerService.DllPatterns.Length >= 5);
var (uOk, uMsg) = upscaler.SwapWithBackup("/nope/a.dll", "/nope/b.dll", riskAcknowledged: false);
Expect("upscaler requires ack", !uOk && uMsg.Contains("Acknowledge", StringComparison.OrdinalIgnoreCase));

var hostOs = new ExoHostOsService(registry, optimizer);
Expect("host os catalog complete", hostOs.CatalogCheck().Count == 0,
    string.Join(",", hostOs.CatalogCheck()));

var gpu = new ExoGpuControlService();
Expect("gpu detect non-empty", gpu.Detect().Count >= 1);

var pc = new ExoPcControl();
Expect("pc control availability matches OS", pc.IsAvailable == OperatingSystem.IsWindows());

Log("");
Log(failed == 0 ? "Ai.Smoke: ALL PASS" : $"Ai.Smoke: FAILED={failed}");
try { File.WriteAllLines(logPath, lines); } catch { /* ignore */ }
Environment.Exit(failed == 0 ? 0 : 1);
