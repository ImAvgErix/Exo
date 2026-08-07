using Exo.Services;

var logPath = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "nvidia-detect-tests.log");
var lines = new List<string>();
var failed = 0;
void Log(string s) { lines.Add(s); Console.WriteLine(s); }
void Expect(string name, bool cond, string detail = "")
{
    if (cond) Log($"PASS  {name}");
    else { failed++; Log($"FAIL  {name}" + (detail.Length > 0 ? " :: " + detail : "")); }
}

Log("=== Nvidia.Smoke (shipped NvidiaDetectLogic + NvidiaDetectCore.ps1) ===");
Log(DateTime.UtcNow.ToString("o"));

Expect("RTX 3070 series 30", NvidiaDetectLogic.GetGpuSeriesFromName("NVIDIA GeForce RTX 3070") == "30");
Expect("RTX 4070 series 40", NvidiaDetectLogic.GetGpuSeriesFromName("GeForce RTX 4070 SUPER") == "40");
Expect("GTX 1660 series 10", NvidiaDetectLogic.GetGpuSeriesFromName("NVIDIA GeForce GTX 1660 Ti") == "10");
Expect("notebook name", NvidiaDetectLogic.IsNotebookGpuName("GeForce RTX 4060 Laptop GPU"));
Expect("desktop not notebook", !NvidiaDetectLogic.IsNotebookGpuName("GeForce RTX 3070"));

Expect("max fps profile name",
    NvidiaDetectLogic.ExpectedProfileFileName("30", false) == "30 Series.nip");
Expect("gsync profile name",
    NvidiaDetectLogic.ExpectedProfileFileName("30", true) == "30 Series G-SYNC.nip");
Expect("profile name matches",
    NvidiaDetectLogic.ProfileNameMatchesSeries("30 Series.nip", "30", false));
Expect("profile name mismatch",
    !NvidiaDetectLogic.ProfileNameMatchesSeries("40 Series.nip", "30", false));

// Display status gate: every term is required, and none of them may be inferred.
Expect("display status: refresh+color+scaling passes",
    NvidiaDetectLogic.IsDisplayStatusOk(true, true, true));
Expect("display status: bad color fails even when everything else is OK",
    !NvidiaDetectLogic.IsDisplayStatusOk(true, false, true));
Expect("display status: no refresh fails",
    !NvidiaDetectLogic.IsDisplayStatusOk(false, true, true));
Expect("display status: no scaling fails",
    !NvidiaDetectLogic.IsDisplayStatusOk(true, true, false));

Expect("sha256 hex", NvidiaDetectLogic.IsSha256Hex(new string('a', 64)));
Expect("sha256 bad", !NvidiaDetectLogic.IsSha256Hex("zz"));

// --- Live DRS verification classifier (post-import + detect drsLive) ---
var drsExpected = new Dictionary<string, string>
{
    ["274197361"] = "1",  // power management: prefer maximum performance
    ["390467"] = "2",     // ULL CPL ultra
    ["277041152"] = "1",  // ULL enabled
    ["277041154"] = "0",  // frame limiter off
    ["294973784"] = "0",  // G-SYNC global (max-FPS pack)
    ["11041279"] = "0",   // OS VRR override off (explicit toggle owns VRR)
    ["11041231"] = "138504007", // VSync force off (max-FPS pack)
    ["549528094"] = "1",  // threaded optimization
};
var drsRequired = NvidiaDetectLogic.DrsRequiredPinIds;
Expect("drs required pins cover ULL/FRL/G-SYNC/VSync without global max power",
    !drsRequired.Contains("274197361") && drsRequired.Contains("390467") &&
    drsRequired.Contains("277041152") && drsRequired.Contains("277041154") &&
    drsRequired.Contains("294973784") && drsRequired.Contains("11041279") &&
    drsRequired.Contains("11041231"));

var drsMatch = new Dictionary<string, string>(drsExpected);
var (vStatus, vCount, vMism) = NvidiaDetectLogic.ClassifyDrsVerification(drsExpected, drsMatch, drsRequired);
Expect("drs verified when export matches", vStatus == "verified" && vCount == drsExpected.Count && vMism.Count == 0,
    $"{vStatus}/{vCount}/{string.Join(";", vMism)}");

var drsDrift = new Dictionary<string, string>(drsExpected) { ["274197361"] = "0" };
var (dStatus, _, dMism) = NvidiaDetectLogic.ClassifyDrsVerification(drsExpected, drsDrift, drsRequired);
Expect("drs drifted on pin mismatch", dStatus == "drifted" && dMism.Count == 1 && dMism[0].Contains("274197361"),
    string.Join(";", dMism));

var (uStatus, _, _) = NvidiaDetectLogic.ClassifyDrsVerification(drsExpected, null, drsRequired);
Expect("drs unavailable when export missing (old NPI)", uStatus == "unavailable");
var (nStatus, _, _) = NvidiaDetectLogic.ClassifyDrsVerification(null, drsMatch, drsRequired);
Expect("drs unavailable when pack missing", nStatus == "unavailable");

var (eStatus, _, eMism) = NvidiaDetectLogic.ClassifyDrsVerification(
    drsExpected, new Dictionary<string, string>(), drsRequired);
Expect("drs drifted when export has no base pins", eStatus == "drifted" && eMism.Count > 0,
    string.Join(";", eMism));

var drsPartial = new Dictionary<string, string>(drsExpected);
drsPartial.Remove("277041154"); // frame limiter pin missing from driver export
var (pStatus, _, pMism) = NvidiaDetectLogic.ClassifyDrsVerification(drsExpected, drsPartial, drsRequired);
Expect("drs drifted when required pin missing from export",
    pStatus == "drifted" && pMism.Any(m => m.Contains("277041154") && m.Contains("missing")),
    string.Join(";", pMism));

// Non-required extra pack pins missing from export are tolerated (intersection compare)
var drsIntersect = new Dictionary<string, string>(drsExpected);
drsIntersect.Remove("549528094");
var (iStatus, iCount, _) = NvidiaDetectLogic.ClassifyDrsVerification(drsExpected, drsIntersect, drsRequired);
Expect("drs intersection tolerates missing optional pin", iStatus == "verified" && iCount == drsExpected.Count - 1);

Expect("profile stage applied = record + not drifted",
    NvidiaDetectLogic.IsProfileStageApplied(true, "verified") &&
    NvidiaDetectLogic.IsProfileStageApplied(true, "unavailable") &&
    !NvidiaDetectLogic.IsProfileStageApplied(true, "drifted") &&
    !NvidiaDetectLogic.IsProfileStageApplied(false, "verified"));

Expect("drs verified row string", NvidiaDetectLogic.DrsVerifiedDetailText == "Verified in driver");
Expect("drs drifted row string uses em dash",
    NvidiaDetectLogic.DrsDriftedDetailText == "Drifted \u2014 re-apply");
Expect("drs live states", NvidiaDetectLogic.DrsLiveStates.SequenceEqual(new[] { "verified", "drifted", "unavailable" }));

// Fully applied fixture intentional actives
var full = NvidiaDetectLogic.IsDisplayStatusOk(true, true, true) &&
           NvidiaDetectLogic.ProfileNameMatchesSeries("30 Series.nip", "30", false);
Expect("fully applied fixture false_fail_count=0", full);

var repo = FindRepoRoot();
var core = Path.Combine(repo, "Exo", "Scripts", "Nvidia", "NvidiaDetectCore.ps1");
Expect("NvidiaDetectCore.ps1 exists", File.Exists(core), core);

if (File.Exists(core))
{
    var dir = Path.Combine(Path.GetTempPath(), "exo-nv-smoke-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var script = $@"
. '{core.Replace("'", "''")}'
$failed=0
function E($n,$c){{ if($c){{'PASS  '+$n}} else {{$script:failed++; 'FAIL  '+$n}} }}
$exp = @{{ '274197361'='1'; '390467'='2'; '277041152'='1'; '277041154'='0'; '294973784'='0' }}
$req = @('274197361','390467','277041152','277041154','294973784')
$live = @{{ '274197361'='1'; '390467'='2'; '277041152'='1'; '277041154'='0'; '294973784'='0' }}
$drift = @{{ '274197361'='0'; '390467'='2'; '277041152'='1'; '277041154'='0'; '294973784'='0' }}
$rVerified = Get-ExoDrsVerificationResult -Expected $exp -Exported $live -RequiredIds $req
$rDrift = Get-ExoDrsVerificationResult -Expected $exp -Exported $drift -RequiredIds $req
$rUnavail = Get-ExoDrsVerificationResult -Expected $exp -Exported $null -RequiredIds $req
$rEmpty = Get-ExoDrsVerificationResult -Expected $exp -Exported @{{}} -RequiredIds $req
@(
 (E 'ps series 30' ((Get-ExoGpuSeriesFromName 'NVIDIA GeForce RTX 3070') -eq '30')),
 (E 'ps profile max' ((Get-ExoExpectedProfileFileName -SeriesId '40' -Gsync $false) -eq '40 Series.nip')),
 (E 'ps profile gsync' ((Get-ExoExpectedProfileFileName -SeriesId '40' -Gsync $true) -eq '40 Series G-SYNC.nip')),
 (E 'ps display status all terms required' (Test-ExoDisplayStatusOk -RefreshOk $true -ColorOk $true -ScalingOk $true)),
 (E 'ps display status bad color fails' (-not (Test-ExoDisplayStatusOk -RefreshOk $true -ColorOk $false -ScalingOk $true))),
 (E 'ps drs verified' ($rVerified.Status -eq 'verified' -and $rVerified.ComparedCount -eq 5)),
 (E 'ps drs drifted mismatch' ($rDrift.Status -eq 'drifted' -and @($rDrift.Mismatches).Count -eq 1)),
 (E 'ps drs unavailable null export' ($rUnavail.Status -eq 'unavailable')),
 (E 'ps drs drifted empty export' ($rEmpty.Status -eq 'drifted')),
 (E 'ps drs verified text' ((Get-ExoDrsVerifiedDetailText) -eq 'Verified in driver')),
 (E 'ps drs drifted text em dash' ((Get-ExoDrsDriftedDetailText) -eq ('Drifted ' + [char]0x2014 + ' re-apply')))
) | % {{ $_ }}
'CORE_FAILED=' + $failed
";
        var ps1 = Path.Combine(dir, "run.ps1");
        File.WriteAllText(ps1, script);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{ps1}\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(60000);
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("PASS") || line.StartsWith("FAIL"))
            {
                Log("CORE  " + line);
                if (line.StartsWith("FAIL")) failed++;
            }
        }
        Expect("NvidiaDetectCore CORE_FAILED=0", stdout.Contains("CORE_FAILED=0"), stdout.Trim());
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

var applyFiles = new[]
{
    Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Nvidia-Optimizer.ps1"),
    Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Exo-Display-Apply.ps1"),
};
var blob = string.Join("\n", applyFiles.Where(File.Exists).Select(File.ReadAllText));
Expect("apply sources readable", blob.Length > 5000);
var (ok, issues) = NvidiaDetectLogic.AuditApplyScriptText(blob);
Expect("apply audit", ok, string.Join("; ", issues));
Expect("no NVIDIA scheduled task create",
    !System.Text.RegularExpressions.Regex.IsMatch(blob, @"Register-ScheduledTask[^\r\n]*Exo-Nvidia",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase));
Expect("retired tray manipulation absent",
    !File.Exists(Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Exo-Nvidia-TrayClear.ps1")) &&
    !blob.Contains("Exo-Nvidia-TrayClear", StringComparison.OrdinalIgnoreCase) &&
    !blob.Contains("IsPromoted", StringComparison.OrdinalIgnoreCase));

// --- DRS verification + NPI pin + strip + catalog markers ---
var optimizerPath = Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Nvidia-Optimizer.ps1");
var displayApplyPath = Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Exo-Display-Apply.ps1");
var detectPath = Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Exo-Nvidia-Detect.ps1");
var corePath = Path.Combine(repo, "Exo", "Scripts", "Nvidia", "NvidiaDetectCore.ps1");
var nvDisplaySourcePath = Path.Combine(repo, "tools", "Exo.NvDisplay", "Program.cs");
var runScriptPath = Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Exo-Nvidia-Run.ps1");
var optimizerSrc = File.Exists(optimizerPath) ? File.ReadAllText(optimizerPath) : "";
var displayApplySrc = File.Exists(displayApplyPath) ? File.ReadAllText(displayApplyPath) : "";
var detectSrc = File.Exists(detectPath) ? File.ReadAllText(detectPath) : "";
var coreSrc = File.Exists(corePath) ? File.ReadAllText(corePath) : "";
var nvDisplaySrc = File.Exists(nvDisplaySourcePath) ? File.ReadAllText(nvDisplaySourcePath) : "";
var runScriptSrc = File.Exists(runScriptPath) ? File.ReadAllText(runScriptPath) : "";

Expect("optimizer runs -exportCustomized", optimizerSrc.Contains("-exportCustomized", StringComparison.Ordinal));
Expect("detect runs -exportCustomized", detectSrc.Contains("-exportCustomized", StringComparison.Ordinal));
Expect("optimizer post-import DRS verify", optimizerSrc.Contains("Test-ExoDrsImportVerified", StringComparison.Ordinal));
Expect("optimizer records drsVerified", optimizerSrc.Contains("drsVerified", StringComparison.Ordinal));
Expect("optimizer records drsVerifiedAt", optimizerSrc.Contains("drsVerifiedAt", StringComparison.Ordinal));
Expect("optimizer records drsVerifiedSettingCount", optimizerSrc.Contains("drsVerifiedSettingCount", StringComparison.Ordinal));
Expect("optimizer records drsMismatch", optimizerSrc.Contains("drsMismatch", StringComparison.Ordinal));
Expect("display apply retries NVAPI",
    displayApplySrc.Contains("NVAPI apply attempt", StringComparison.Ordinal) &&
    displayApplySrc.Contains("Invoke-NvApiHelperOnce", StringComparison.Ordinal));
Expect("display apply success without partial exit 2",
    displayApplySrc.Contains("SUCCESS registry", StringComparison.Ordinal) &&
    !displayApplySrc.Contains("PARTIAL registry-ok nvapi-failed", StringComparison.Ordinal));
// Policy change from 3.16.x. Back then display prefs really were Control-Panel-owned, and
// these assertions pinned "displayPrefs = $false" as the contract. Exo drives displays now
// via Exo.NvDisplay, so the contract is the split: SafePolicy still leaves them alone, the
// normal path applies them. Asserting the split rather than a blanket "never touched".
Expect("safe policy still leaves display settings alone",
    optimizerSrc.Contains("$(if ($SafePolicy) { $false } else { [bool]$displayPrefsOk })", StringComparison.Ordinal) &&
    optimizerSrc.Contains("$(if ($SafePolicy) { 'unchanged' } else { [string]$displayMethod })", StringComparison.Ordinal) &&
    optimizerSrc.Contains("Profile Inspector DRS policy", StringComparison.Ordinal));
Expect("non-safe policy records the display method it actually used",
    optimizerSrc.Contains("$displayMethod = [string]$dispResult.Method", StringComparison.Ordinal));

// ── GPU power / thermal ceilings ───────────────────────────────────────────────────────
// Set-NvidiaDisplayPreferences shipped defined-but-never-called, and the UI reported the rig
// optimized while Apply never touched a display. These assertions exist so the same thing
// cannot happen to the GPU power path: defined, called, saved, restored, and shown.
Expect("GPU power helper is defined",
    optimizerSrc.Contains("function Set-NvidiaGpuPower", StringComparison.Ordinal));
Expect("GPU power helper is actually CALLED in the apply flow",
    optimizerSrc.Contains("$gpuPowerResult = Set-NvidiaGpuPower", StringComparison.Ordinal)
    && optimizerSrc.Contains("Set-ExoStage 'gpu-power'", StringComparison.Ordinal));
Expect("GPU power result is initialized before the branch that can skip it",
    optimizerSrc.Contains("$gpuPowerOk = $false", StringComparison.Ordinal)
    && optimizerSrc.Contains("$gpuPowerDetail = 'skipped'", StringComparison.Ordinal));
Expect("GPU power state is persisted for detect",
    optimizerSrc.Contains("gpuPower            = $(if ($SafePolicy)", StringComparison.Ordinal)
    && optimizerSrc.Contains("gpuPowerDetail      = [string]$gpuPowerDetail", StringComparison.Ordinal));
Expect("Apply snapshots the pre-Exo ceilings so Repair can restore them",
    optimizerSrc.Contains("--gpu-apply --gpu-snapshot", StringComparison.Ordinal));
Expect("Repair restores the ceilings from the snapshot, never from a guessed default",
    optimizerSrc.Contains("--gpu-restore", StringComparison.Ordinal)
    && optimizerSrc.Contains("No GPU power snapshot exists", StringComparison.Ordinal));

// The helper must never invent a limit. Every target comes from the board's own reported
// maximum, which is what makes it safe on hardware nobody has tested it against.
var gpuPowerSrc = File.ReadAllText(Path.Combine(repo, "tools", "Exo.NvDisplay", "GpuPower.cs"));
Expect("power target comes from the board's reported maximum",
    gpuPowerSrc.Contains("MaximumPowerInPCM", StringComparison.Ordinal)
    && gpuPowerSrc.Contains("MaximumTemperature", StringComparison.Ordinal));
Expect("writes are read back and a clamped write is not reported as applied",
    gpuPowerSrc.Contains("power write did not take", StringComparison.Ordinal)
    && gpuPowerSrc.Contains("thermal write did not take", StringComparison.Ordinal));
Expect("a locked board reads as nothing-to-do, not as a failure",
    gpuPowerSrc.Contains("already at ceiling", StringComparison.Ordinal)
    && gpuPowerSrc.Contains("not supported on this board", StringComparison.Ordinal));
// The one lever here whose failure mode is thermal. Exo hands a cooler back to the driver
// and never takes it over, so it can raise cooling but never lower it.
Expect("no custom fan curve is written",
    !HasNonCommentText(gpuPowerSrc, "SetCoolerLevels")
    && !HasNonCommentText(gpuPowerSrc, "SetCoolerPolicyTable")
    && gpuPowerSrc.Contains("RestoreCoolerSettingsToDefault", StringComparison.Ordinal));
// Clock offsets and undervolting need a stress-validate-and-revert loop to be applied
// honestly. Until that exists, shipping them would be shipping instability.
Expect("no blind overclock or undervolt",
    !HasNonCommentText(gpuPowerSrc, "PerformanceStates20")
    && !HasNonCommentText(gpuPowerSrc, "SetPerformanceStates"));
// ── Phase B: DRS applied natively ──────────────────────────────────────────────────────
// The old path shelled out to nvidiaProfileInspector and trusted its exit code, which said
// nothing about which of ~80 settings actually landed. Native NVAPI writes each one and reads
// it back, so a partial apply reports as partial.
Expect("native DRS apply is tried before Profile Inspector",
    optimizerSrc.Contains("--drs-apply", StringComparison.Ordinal)
    && optimizerSrc.IndexOf("--drs-apply", StringComparison.Ordinal)
       < optimizerSrc.IndexOf("$npi = Install-NpiFresh", StringComparison.Ordinal));
// Profile Inspector stays as the fallback. Deleting a working path the day its replacement is
// written, before the replacement has run on a spread of driver branches, is not an upgrade.
Expect("Profile Inspector remains as a fallback",
    optimizerSrc.Contains("Install-NpiFresh", StringComparison.Ordinal)
    && optimizerSrc.Contains("falling back to Profile Inspector", StringComparison.Ordinal));

var drsSrc = File.ReadAllText(Path.Combine(repo, "tools", "Exo.NvDisplay", "GpuDrs.cs"));
Expect("every DRS setting is read back through a fresh session",
    drsSrc.Contains("Fresh-session readback", StringComparison.Ordinal)
    && drsSrc.Contains("CreateAndLoad()", StringComparison.Ordinal)
    && drsSrc.Contains("app-profiles", StringComparison.Ordinal));
// Qword entries (rBAR Size Limit) have no NVAPI 64-bit type and must be written as an 8-byte
// binary. Treating them as unsupported silently dropped Resizable BAR sizing from every
// 20-series-and-later pack.
Expect("Qword pack entries are written as binary, not skipped",
    drsSrc.Contains("BitConverter.GetBytes(qval)", StringComparison.Ordinal)
    && drsSrc.Contains("SetSetting(s.Id, s.Binary)", StringComparison.Ordinal));
// A byte[] compared by reference never matches, which would report every Qword as drifted.
Expect("binary values are compared element-wise on read-back",
    drsSrc.Contains("b.SequenceEqual(s.Binary)", StringComparison.Ordinal));
// Base + per-game app profiles. FindProfileByName throws NVAPI_PROFILE_NOT_FOUND when
// missing (does not return null) — ResolveAppProfile must catch that and prefer the
// stock profile already bound to each game executable.
Expect("Base Profile + app profiles via ResolveAppProfile",
    drsSrc.Contains("\"Base Profile\"", StringComparison.Ordinal)
    && drsSrc.Contains("ResolveAppProfile", StringComparison.Ordinal)
    && drsSrc.Contains("FindApplicationProfile", StringComparison.Ordinal)
    && drsSrc.Contains("app-profiles", StringComparison.Ordinal));

// ── Driver lookup parsing, against a REAL captured NVIDIA response ─────────────────────
// The fixture is an actual DriverManualLookup reply, not a hand-written approximation. Four
// things in it would have been got wrong by guessing, and each has an assertion below.
{
    var fixturePath = Path.Combine(repo, "tools", "Nvidia.Smoke", "fixtures", "ajax-driver-610.74.json");
    Expect("driver lookup fixture present", File.Exists(fixturePath));
    if (File.Exists(fixturePath))
    {
        var rel = NvidiaDriverLookup.Parse(File.ReadAllText(fixturePath));
        Expect("real lookup response parses", rel is not null);
        if (rel is not null)
        {
            Expect("version read", rel.Version == "610.74", rel.Version);
            Expect("branch read", rel.Branch == "610", rel.Branch);

            // The URL comes from the response. Most documentation shows
            // international.download.nvidia.com; the live reply used us.download. Building the
            // URL from a pattern instead of reading this field would 404 for real users.
            Expect("download URL is taken from the response, not constructed",
                rel.DownloadUrl.StartsWith("https://us.download.nvidia.com/", StringComparison.Ordinal),
                rel.DownloadUrl);

            // "979.17 MB" is a display string. Parsed as a number it is zero, and a zero-byte
            // expectation silently disables any size check downstream.
            Expect("file size string parses to real bytes",
                rel.SizeBytes > 900L * 1024 * 1024 && rel.SizeBytes < 1100L * 1024 * 1024,
                $"{rel.SizeDisplay} -> {rel.SizeBytes}");

            // Display fields are URL-encoded in this feed.
            Expect("URL-encoded names are decoded",
                rel.Name == "GeForce Game Ready Driver", rel.Name);

            // Flags are strings "1"/"0", never JSON booleans.
            Expect("string-typed flags are read correctly",
                rel.IsWhql && !rel.IsBeta && !rel.IsStudio);

            Expect("US display date parses", rel.Released?.Year == 2026, rel.Released?.ToString() ?? "null");

            // The supported-product list is what lets Exo confirm a driver still covers the
            // card in the machine, rather than assuming the newest release does.
            Expect("supported product list is populated", rel.SupportedProducts.Count >= 8,
                rel.SupportedProducts.Count.ToString());
            Expect("exact catalogue name matches",
                rel.Supports("NVIDIA GeForce RTX 4070"));
            // Windows reports laptop parts with a suffix the catalogue does not carry.
            Expect("laptop suffix still matches its desktop catalogue entry",
                rel.Supports("NVIDIA GeForce RTX 4070 Laptop GPU"));
            Expect("a card absent from the list is reported unsupported",
                !rel.Supports("NVIDIA GeForce GTX 780 Ti"));

            // Real hardware this was captured against. Windows reports
            // "NVIDIA GeForce RTX 3070"; the catalogue string is "GeForce RTX 3070" with no
            // vendor prefix, so the match has to survive that difference.
            Expect("the RTX 3070 this response was captured for is recognised",
                rel.Supports("NVIDIA GeForce RTX 3070"));
            // Matching is equality after normalising, not substring. Substring was tried
            // first and would report an UNLISTED card as supported whenever a longer listed
            // entry contained its name - claiming support that is not there, which sends a
            // user at a driver that will not install.
            Expect("a card is not matched by a longer entry that merely contains it",
                !rel.Supports("NVIDIA GeForce RTX 407"));
            Expect("neighbouring models stay distinct",
                rel.Supports("NVIDIA GeForce RTX 3070") && rel.Supports("GeForce RTX 3070 Ti"));

            // IsNewest came back "0" on BOTH the wrong-series and the correct per-series query
            // for a driver that IS the current release. The flag is not trustworthy, so Exo
            // decides newness by comparing version numbers and nothing else.
            Expect("newness is not taken from the IsNewest flag",
                !HasNonCommentText(
                    File.ReadAllText(Path.Combine(repo, "Exo", "Services", "NvidiaDriverLookup.cs")),
                    "IsNewest"));
        }
    }

    // The real "no driver matches" shape, captured from a beta/hotfix query. Hotfix drivers are
    // NOT in this feed - confirmed empirically, not assumed - so this response is what Exo will
    // actually get if it ever asks the feed for one.
    var notFoundPath = Path.Combine(repo, "tools", "Nvidia.Smoke", "fixtures", "ajax-driver-notfound.json");
    Expect("not-found fixture present", File.Exists(notFoundPath));
    if (File.Exists(notFoundPath))
    {
        Expect("a real not-found response parses to null, not a bogus release",
            NvidiaDriverLookup.Parse(File.ReadAllText(notFoundPath)) is null);
    }

    // String comparison ranks "610.9" above "610.74"; version comparison is the whole point.
    Expect("driver versions compare numerically, not as strings",
        NvidiaDriverLookup.CompareVersions("610.74", "610.9") > 0
        && NvidiaDriverLookup.CompareVersions("611.10", "610.99") > 0
        && NvidiaDriverLookup.CompareVersions("610.74", "610.74") == 0);

    // A live third-party endpoint must never be able to fail a detect pass.
    Expect("malformed responses return null instead of throwing",
        NvidiaDriverLookup.Parse("") is null
        && NvidiaDriverLookup.Parse("not json") is null
        && NvidiaDriverLookup.Parse("{\"Success\":\"0\"}") is null
        && NvidiaDriverLookup.Parse("{\"Success\":\"1\",\"IDS\":[]}") is null);
}

// ── Install decision, driven by the real captured artifacts ───────────────────────────
// The whole point is that deciding is separate from doing, and that the decision is pure -
// so it can be driven with made-up machines here instead of a live endpoint.
{
    var whqlFix = Path.Combine(repo, "tools", "Nvidia.Smoke", "fixtures", "ajax-driver-610.74.json");
    var hfFix = Path.Combine(repo, "tools", "Nvidia.Smoke", "fixtures", "hotfix-610.82.txt");
    if (File.Exists(whqlFix) && File.Exists(hfFix))
    {
        var whql = NvidiaDriverLookup.Parse(File.ReadAllText(whqlFix));
        var hotfix = NvidiaHotfixLookup.Parse(File.ReadAllText(hfFix));

        // The real machine these were captured from: RTX 3070 on 591.86.
        var p3070 = NvidiaDriverInstaller.Plan("NVIDIA GeForce RTX 3070", "591.86", whql, hotfix);
        Expect("an out-of-date 3070 is offered the WHQL driver",
            p3070.Kind == NvidiaDriverInstaller.Recommendation.UpgradeWhql
            && p3070.TargetVersion == "610.74" && !p3070.TargetIsBeta,
            $"{p3070.Kind} -> {p3070.TargetVersion}");
        // The hotfix is newer than both, and must still be declined - and explained.
        Expect("the 50-series-only hotfix is declined for a 3070, with a reason",
            p3070.Reasons.Any(r => r.Contains("610.82", StringComparison.Ordinal)
                                   && r.Contains("not worth", StringComparison.Ordinal)),
            string.Join(" | ", p3070.Reasons));

        // Same card, already current: nothing to do, and still no beta.
        var pCurrent = NvidiaDriverInstaller.Plan("NVIDIA GeForce RTX 3070", "610.74", whql, hotfix);
        Expect("a current 3070 is told it is up to date, not pushed to the hotfix",
            pCurrent.Kind == NvidiaDriverInstaller.Recommendation.UpToDate, pCurrent.Kind.ToString());

        // A 50-series card CAN use the hotfix, because a listed fix names its series.
        var p5090 = NvidiaDriverInstaller.Plan("NVIDIA GeForce RTX 5090", "610.74", whql, hotfix);
        Expect("a 50-series card is offered the hotfix that names its series",
            p5090.Kind == NvidiaDriverInstaller.Recommendation.UpgradeHotfix
            && p5090.TargetIsBeta, p5090.Kind.ToString());
        Expect("a beta recommendation says so in its reasons",
            p5090.Reasons.Any(r => r.Contains("beta", StringComparison.OrdinalIgnoreCase)));

        // End of support must not read as "up to date" - that would hide it permanently.
        var pOld = NvidiaDriverInstaller.Plan("NVIDIA GeForce GTX 780 Ti", "398.11", whql, hotfix);
        Expect("a dropped card is told support ended, not that it is current",
            pOld.Kind == NvidiaDriverInstaller.Recommendation.NoLongerSupported, pOld.Kind.ToString());

        // Nothing readable must be Unknown, never a silent "you are fine".
        var pBlind = NvidiaDriverInstaller.Plan("NVIDIA GeForce RTX 3070", "591.86", null, null);
        Expect("an unreadable driver list reports unknown, not up-to-date",
            pBlind.Kind == NvidiaDriverInstaller.Recommendation.Unknown);
    }

    // The download URL arrives from a third-party response and is validated before use.
    Expect("only HTTPS NVIDIA download hosts are accepted",
        NvidiaDriverInstaller.IsAcceptableDownloadUrl("https://us.download.nvidia.com/Windows/610.74/x.exe")
        && NvidiaDriverInstaller.IsAcceptableDownloadUrl("https://international.download.nvidia.com/Windows/610.82hf/x.hf.exe"));
    Expect("plain HTTP and non-NVIDIA hosts are refused",
        !NvidiaDriverInstaller.IsAcceptableDownloadUrl("http://us.download.nvidia.com/x.exe")
        && !NvidiaDriverInstaller.IsAcceptableDownloadUrl("https://evil.example.com/Windows/610.74/x.exe")
        && !NvidiaDriverInstaller.IsAcceptableDownloadUrl("https://nvidia.com.evil.example/x.exe")
        && !NvidiaDriverInstaller.IsAcceptableDownloadUrl(null));

    var installerSrc = File.ReadAllText(Path.Combine(repo, "Exo", "Services", "NvidiaDriverInstaller.cs"));

    // A truncated download must be removed, not merely reported as removed. Prepare reuses an
    // existing package by filename, so a short file left on disk fails the size check on every
    // subsequent attempt - one interrupted download would disable driver installs permanently.
    // Source-shape, which is the weak kind of check, but the alternative needs a fake
    // filesystem and the failure mode is a machine that can never install a driver again.
    {
        var i = installerSrc.IndexOf("that is not a driver", StringComparison.Ordinal);
        Expect("the truncated-download branch still exists", i > 0);
        if (i > 0)
        {
            var branch = installerSrc[Math.Max(0, i - 400)..i];
            Expect("a truncated download is actually deleted, not just said to be",
                branch.Contains("File.Delete(exePath)", StringComparison.Ordinal),
                "the message claims a deletion the code does not perform, and every retry then reuses the short file");
        }
    }

    // The unpacker decision, against made-up machines rather than this one.
    //
    // The first version of these called PrepareAsync directly. That asserted against whatever
    // the build agent happened to have: green on Linux, red on the Windows agent - which ships
    // with 7-Zip, so the call sailed past the prerequisite and issued a real request to NVIDIA
    // for a driver package. A test whose result depends on the agent's installed software is
    // not testing the code, and this one had a side effect on the internet.
    {
        Expect("an unpacker that is already there is just used",
            NvidiaDriverInstaller.DecideUnpacker(@"C:\Program Files\7-Zip\7z.exe", null, false)
                is (true, false, null));

        // Permission is required even when winget could do it. Installing software must never
        // be something that happens because the user asked about a driver.
        Expect("no unpacker and no permission is an error, not a silent install",
            NvidiaDriverInstaller.DecideUnpacker(null, @"C:\winget.exe", false)
                is (false, false, NvidiaDriverInstaller.NoSevenZip));

        // Not ready yet - the install still has to happen and can still fail.
        Expect("permission plus winget means install it, not assume it",
            NvidiaDriverInstaller.DecideUnpacker(null, @"C:\winget.exe", true)
                is (false, true, null));

        // Nothing to install with: say so up front rather than after the download.
        Expect("permission with no winget is reported, not attempted",
            NvidiaDriverInstaller.DecideUnpacker(null, null, true)
                is (false, false, NvidiaDriverInstaller.NoWinget));

        // An empty string is what a "not found" path looks like when it comes back from a
        // failed lookup rather than as null, and treating it as a usable exe would run "".
        Expect("an empty path counts as not found",
            NvidiaDriverInstaller.DecideUnpacker("", "", false)
                is (false, false, NvidiaDriverInstaller.NoSevenZip));
    }
    Expect("7-Zip is looked for somewhere other than a hardcoded C: drive",
        !installerSrc.Contains(@"""C:\Program Files\7-Zip", StringComparison.Ordinal)
        && installerSrc.Contains("SpecialFolder.ProgramFiles", StringComparison.Ordinal),
        "a hardcoded C: path misses every machine whose Windows is on another drive");

    // Every Exo function the shipped scripts CALL must be one some shipped script DEFINES.
//
// Write-ExoLog was called six times in Nvidia-Optimizer.ps1 and defined nowhere. PowerShell
// only finds out at the moment of the call, so it shipped: the whole NVIDIA apply died at
// stage 'profile-import' on a real machine, taking the native DRS path down with it. Phase B
// had never once run end to end, and every source-shape gate stayed green, because they all
// check that text exists rather than that a name resolves.
//
// Scripts dot-source their siblings and kit libs at runtime, so the definition may live in any
// shipped .ps1 - the set is the union. Note the word boundary on the definition match:
// Write-ExoLogMirror IS defined, and a substring test would have accepted Write-ExoLog as
// defined by it and missed this exact bug.
{
    var allScripts = Directory.GetFiles(Path.Combine(repo, "Exo", "Scripts"), "*.ps1", SearchOption.AllDirectories);
    var allText = allScripts.ToDictionary(f => f, File.ReadAllText);
    var defined = new HashSet<string>(
        allText.Values.SelectMany(t => System.Text.RegularExpressions.Regex
            .Matches(t, @"function\s+([A-Za-z]+-[A-Za-z0-9]+)\b")
            .Select(m => m.Groups[1].Value)),
        StringComparer.OrdinalIgnoreCase);

    foreach (var (script, text) in allText)
    {
        // Exo-namespaced names only. Anything else is a built-in or a module cmdlet, and this
        // gate has no business guessing at what the shell provides.
        var called = System.Text.RegularExpressions.Regex
            .Matches(text, @"(?<![-\w$.])((?:Write|Test|Get|Set|New|Invoke|Install|Confirm|Resolve|Import|Remove|Update|Save)-Exo[A-Za-z0-9]*)\b")
            .Select(m => m.Groups[1].Value).Distinct();

        foreach (var name in called)
            Expect($"{Path.GetFileName(script)} calls a function that exists: {name}",
                defined.Contains(name),
                "no shipped script defines it - PowerShell only finds out when the line runs");
    }
}

// The shipped invocation must not switch off what the rest of this suite tests.
//
// Exo-Nvidia-Run.ps1 forced SafePolicy on, and SafePolicy skipped NVIDIA App / GFE removal,
// bloat component stripping, the overlay kill, the GPU power ceiling and display prefs - then
// reported Ok=true for the overlay and debloat it had just skipped. Every one of those has
// assertions in this file. All of them passed. None of them ran on a user's machine, and the
// App branch explicitly KEPT the app and would install the Control Panel, which is how an
// NVIDIA app appears on a PC that never had one.
//
// A gate that proves a feature exists is worth nothing if the product path disables it.
{
    var runner = File.ReadAllText(Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Exo-Nvidia-Run.ps1"));
    var splat = System.Text.RegularExpressions.Regex.Match(runner, @"\$params\s*=\s*@\{([^}]*)\}");
    Expect("the shipped NVIDIA invocation is still shaped the way this gate reads it", splat.Success);
    if (splat.Success)
    {
        Expect("the shipped NVIDIA run does not force SafePolicy",
            !splat.Groups[1].Value.Contains("SafePolicy", StringComparison.OrdinalIgnoreCase),
            "SafePolicy skips app removal, debloat, overlay, GPU power and display prefs - everything this suite checks");

        // The two guards that are legitimate, kept explicit so removing one is a decision.
        Expect("driver install stays out of the script path (Phase C owns it)",
            splat.Groups[1].Value.Contains("SkipDriver", StringComparison.Ordinal));
        Expect("HD-audio component removal stays opt-in",
            splat.Groups[1].Value.Contains("SkipAudio", StringComparison.Ordinal));
    }

    var opt = File.ReadAllText(Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Nvidia-Optimizer.ps1"));
    Expect("skipped overlay/debloat is not reported as Ok",
        !System.Text.RegularExpressions.Regex.IsMatch(opt, @"\$(overlay|debloat)Result\s*=\s*\[pscustomobject\]@\{\s*Ok\s*=\s*\$true;\s*Issues\s*=\s*@\(\)"),
        "work that was skipped was being recorded as successful");
}

// The strip is a denylist now: everything unprotected goes. Behavioural, against the real
// manifest, because the previous allowlist kept every component nobody had named in advance -
// which is how an NVIDIA app reached a machine that never had one.
{
    var realCfg = File.ReadAllText(Path.Combine(repo, "tools", "Nvidia.Smoke", "fixtures", "setup.cfg"));
    var r = NvidiaDriverPackage.Strip(realCfg);

    foreach (var essential in new[] { "Display.Driver", "Display.PhysX", "Display.Optimus", "MSVCRuntime2019" })
        Expect($"strip keeps {essential}", r.Kept.Contains(essential, StringComparer.OrdinalIgnoreCase),
            string.Join(", ", r.Kept));

    foreach (var bloat in new[] { "NVIDIA.Update", "Update.Core", "Display.Update", "Ansel", "Display.NVWMI" })
        Expect($"strip removes {bloat}", r.Removed.Contains(bloat, StringComparer.OrdinalIgnoreCase),
            string.Join(", ", r.Removed));

    // The allowlist kept these purely because nobody had listed them.
    Expect("strip no longer keeps components merely because they were unlisted",
        r.Removed.Count > 4, $"removed only {r.Removed.Count}: {string.Join(", ", r.Removed)}");

    // DLSS is the silent one: no error, just fewer frames in every game that uses it.
    Expect("a DLSS/NGX component is protected by fragment, whatever it is named",
        NvidiaDriverPackage.ProtectedReason("Display.NGXCore") is not null
        && NvidiaDriverPackage.ProtectedReason("NvNGX.Runtime") is not null);
    Expect("an unknown vendor component is removed, not kept by default",
        NvidiaDriverPackage.ProtectedReason("NVIDIA.SomeNewThing") is null);
}

// Removing the client and then installing one back is not a cleanup.
//
// A real log, in order: "NVIDIA App removed" ... then three lines later "Installing NVIDIA
// Control Panel (display UI fallback)" -> a winget Microsoft Store install. The user watched
// Exo delete the NVIDIA client and still had one afterwards, because Exo put it back. Display
// applies through NVAPI regardless, so the panel was only ever a UI for the human.
{
    var opt = File.ReadAllText(Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Nvidia-Optimizer.ps1"));
    var calls = System.Text.RegularExpressions.Regex.Matches(opt, @"(?<!function )Install-NvidiaControlPanel").Count;
    Expect("nothing calls the Control Panel installer", calls == 0,
        $"{calls} call site(s) - a client install undoes the client removal in the same run");

    // "Already matches" must mean every check matched. A run reported
    // "Display already matches (color=False, ...)" and skipped, so colour never applied on a
    // machine whose whole reason for running was colour and scaling.
    // Same three terms as Test-ExoDisplayStatusOk and the Exo.NvDisplay gate, so this step and
    // the Display-Apply child cannot disagree -- they used to, in the same run: the parent said
    // "Display needs apply: color=False" and the child then said "SKIP: already matches panel
    // policy". RegistryOk is deliberately absent: the NVTweak keys are named from an EDID hash,
    // so an id-based verify matched nothing and returned "verified" having checked nothing.
    Expect("the display skip requires every check, not the helper's summary flag",
        opt.Contains("Ok         = ($refreshOk -and $colorOk -and $scalingOk)", StringComparison.Ordinal),
        "a False in the detail line must not read as a match");

    // Same run said "Display prefs applied" and "scaling and NVIDIA color were not forced".
    Expect("the closing display line no longer contradicts the applied line",
        !opt.Contains("Display scaling and NVIDIA color were not forced", StringComparison.Ordinal));
}

// ── Driver health: the read-only half of the cleaner ──────────────────────────────────
// Parsed and judged with no machine attached, so these run identically everywhere.
{
    // Real pnputil /enum-drivers shape, two NVIDIA generations plus an unrelated vendor.
    const string enumOut = """
Microsoft PnP Utility

Published Name:     oem12.inf
Original Name:      nvhda.inf
Provider Name:      NVIDIA Corporation
Class Name:         Sound, video and game controllers
Driver Version:     03/14/2026 1.4.10.1
Signer Name:        Microsoft Windows Hardware Compatibility Publisher

Published Name:     oem31.inf
Original Name:      nv_dispi.inf
Provider Name:      NVIDIA Corporation
Class Name:         Display adapters
Driver Version:     05/02/2026 32.0.16.1074
Signer Name:        Microsoft Windows Hardware Compatibility Publisher

Published Name:     oem7.inf
Original Name:      rt640x64.inf
Provider Name:      Realtek
Class Name:         Net
Driver Version:     01/09/2025 10.70.1216.2024
Signer Name:        Microsoft Windows Hardware Compatibility Publisher
""";

    var parsed = NvidiaDriverHealthLogic.ParseEnumDrivers(enumOut);
    Expect("pnputil output parses into one package per block", parsed.Count == 3, $"{parsed.Count}");
    Expect("NVIDIA packages are identified by provider, not by filename",
        parsed.Count(p => p.IsNvidia) == 2, string.Join(",", parsed.Select(p => p.Provider)));
    Expect("the published oem name is captured",
        parsed.Any(p => p.OemInf == "oem31.inf"), string.Join(",", parsed.Select(p => p.OemInf)));
    Expect("the version is separated from the date",
        parsed.Any(p => p.Version == "32.0.16.1074"), string.Join(",", parsed.Select(p => p.Version)));
    Expect("garbage in gives nothing out, not a half-populated package",
        NvidiaDriverHealthLogic.ParseEnumDrivers("not pnputil output at all").Count == 0);

    NvidiaDriverHealthLogic.StorePackage Pkg(string oem, string ver) =>
        new(oem, "nv_dispi.inf", "NVIDIA Corporation", ver, null);

    // A tidy machine must not be told to do the most destructive thing in the app.
    var clean = NvidiaDriverHealthLogic.Evaluate(
        new[] { Pkg("oem31.inf", "32.0.16.1074") }, "610.74", Array.Empty<string>(), 0);
    Expect("a clean machine is not offered a sweep", !clean.NeedsSweep,
        string.Join("; ", clean.Findings.Select(f => f.Title)));

    // Accumulated packages are the whole reason DDU exists.
    var stale = NvidiaDriverHealthLogic.Evaluate(
        new[] { Pkg("oem10.inf", "31.0.15.3623"), Pkg("oem22.inf", "32.0.15.6094"),
                Pkg("oem31.inf", "32.0.16.1074"), Pkg("oem40.inf", "32.0.16.2001") },
        "610.74", Array.Empty<string>(), 0);
    Expect("four stacked driver packages reads as needing a sweep", stale.NeedsSweep);

    var orphan = NvidiaDriverHealthLogic.Evaluate(
        new[] { Pkg("oem31.inf", "32.0.16.1074") }, "610.74", new[] { "nvlddmkm" }, 0);
    Expect("a service with no binary behind it needs a sweep", orphan.NeedsSweep);

    // Code 43 is the case a reinstall will not fix, which is exactly when a sweep earns itself.
    var prob = NvidiaDriverHealthLogic.Evaluate(
        new[] { Pkg("oem31.inf", "32.0.16.1074") }, "610.74", Array.Empty<string>(), 43);
    Expect("device problem code 43 needs a sweep", prob.NeedsSweep);
    Expect("code 43 is explained rather than printed as a number",
        prob.Findings.Any(f => f.Detail.Contains("refused to start", StringComparison.Ordinal)));

    // Informational only: being on an older driver than the newest present is usually fine.
    var older = NvidiaDriverHealthLogic.Evaluate(
        new[] { Pkg("oem31.inf", "32.0.16.1074"), Pkg("oem40.inf", "32.0.17.0001") },
        "610.74", Array.Empty<string>(), 0);
    Expect("running an older-than-newest driver is reported, not escalated",
        older.Findings.Any(f => f.Id == "not-newest" && !f.NeedsSweep));
}

// ── The cleaner: the flag that can strand a machine ───────────────────────────────────
{
    var cleaner = File.ReadAllText(Path.Combine(repo, "Exo", "Services", "NvidiaDriverCleaner.cs"));

    // The deletions are recoverable by reinstalling a driver. A safeboot flag left set is
    // not recoverable from inside Exo at all, because Exo is what would have cleared it.
    Expect("the Safe Mode flag is cleared at startup regardless of how the last run ended",
        cleaner.Contains("public static string? ClearPendingBootFlagIfAny()", StringComparison.Ordinal)
        && cleaner.Contains("/deletevalue {current} safeboot", StringComparison.Ordinal));
    Expect("a crashed sweep is not silently retried",
        cleaner.Contains("Deliberately does NOT try to resume", StringComparison.Ordinal));
    Expect("the state file is written before the boot flag is set",
        cleaner.IndexOf("File.WriteAllText(StatePath", StringComparison.Ordinal)
        < cleaner.IndexOf("/set {current} safeboot minimal", StringComparison.Ordinal));

    // Removing driver files while the driver is loaded is the worst of both worlds.
    Expect("the sweep refuses to run outside Safe Mode",
        cleaner.Contains("if (!IsSafeMode())", StringComparison.Ordinal));
    Expect("arming needs both a matching token and an explicit confirmation",
        cleaner.Contains("if (!userConfirmed)", StringComparison.Ordinal)
        && cleaner.Contains("!string.Equals(token, plan.Token", StringComparison.Ordinal));
    Expect("a failed flag-clear after sweeping is reported with the manual command",
        cleaner.Contains("bcdedit /deletevalue {current} safeboot", StringComparison.Ordinal));
}

// Execute is reachable only with BOTH a matching token and an explicit confirmation.
    Expect("install requires explicit confirmation and a matching token",
        installerSrc.Contains("if (!userConfirmed)", StringComparison.Ordinal)
        && installerSrc.Contains("Confirmation did not match this prepared install", StringComparison.Ordinal));
    // Exit code 1 means installed-but-reboot-required. Treating non-zero as failure would
    // report a successful install as broken.
    Expect("reboot-required is not reported as a failed install",
        installerSrc.Contains("reboot to finish", StringComparison.Ordinal));
    Expect("a restore point is attempted before installing",
        installerSrc.Contains("Checkpoint-Computer", StringComparison.Ordinal));
    // Silence about an unavailable restore point would be the dishonest option.
    Expect("a missing restore point is reported, not assumed",
        installerSrc.Contains("Could not create a restore point", StringComparison.Ordinal));
}

// ── Hotfix article parsing, against the REAL 610.82 page ──────────────────────────────
{
    var hfPath = Path.Combine(repo, "tools", "Nvidia.Smoke", "fixtures", "hotfix-610.82.txt");
    Expect("hotfix fixture present", File.Exists(hfPath));
    if (File.Exists(hfPath))
    {
        var hf = NvidiaHotfixLookup.Parse(File.ReadAllText(hfPath));
        Expect("real hotfix article parses", hf is not null);
        if (hf is not null)
        {
            Expect("hotfix version read from the URL path, not the prose",
                hf.Version == "610.82", hf.Version);
            Expect("the WHQL driver it is based on is read", hf.BasedOnVersion == "610.74", hf.BasedOnVersion);

            // The hotfix URL differs from the WHQL feed's in three ways: different host, an
            // "hf" suffix on the version directory, and ".hf.exe" on the filename. Reusing the
            // WHQL pattern here would 404.
            Expect("hotfix download URL captured exactly",
                hf.DownloadUrl.Contains("/Windows/610.82hf/", StringComparison.Ordinal)
                && hf.DownloadUrl.EndsWith(".hf.exe", StringComparison.Ordinal),
                hf.DownloadUrl);

            Expect("both listed fixes are parsed", hf.Fixes.Count == 2, hf.Fixes.Count.ToString());
            Expect("fix titles come from the bracketed game name",
                hf.Fixes.Any(f => f.Title == "Halo: Campaign Evolved")
                && hf.Fixes.Any(f => f.Title == "Path of Exile 2"));

            // The point of the whole thing. 610.82's Halo fix names RTX 50 series; the Path of
            // Exile 2 fix names no hardware. On a 50-series card both are candidates. On the
            // 30-series card this was captured against, only the unqualified one is - and a
            // tool that just compared version numbers would push a beta driver for a bug the
            // user cannot hit.
            Expect("hardware-specific fixes are attributed to their series",
                hf.Fixes.First(f => f.Title.StartsWith("Halo", StringComparison.Ordinal)).Series.Contains("50"));
            Expect("a fix naming no hardware is not attributed to one",
                hf.Fixes.First(f => f.Title.StartsWith("Path", StringComparison.Ordinal)).Series.Count == 0);

            Expect("a 30-series card sees only the non-hardware-specific fix",
                hf.FixesFor("30").Count == 1
                && hf.FixesFor("30")[0].Title == "Path of Exile 2",
                string.Join(", ", hf.FixesFor("30").Select(f => f.Title)));
            Expect("a 50-series card sees both", hf.FixesFor("50").Count == 2);
            // Unknown GPU must not silently filter everything away.
            Expect("an unknown GPU is not filtered", hf.RelevantTo(null) && hf.FixesFor(null).Count == 2);
        }
    }

    // Series extraction has to agree with how the DRS profile packs are keyed.
    Expect("GPU series parses for hotfix relevance",
        NvidiaHotfixLookup.SeriesOf("NVIDIA GeForce RTX 3070") == "30"
        && NvidiaHotfixLookup.SeriesOf("NVIDIA GeForce RTX 5090") == "50"
        && NvidiaHotfixLookup.SeriesOf("NVIDIA GeForce GTX 1660 Ti") == "10");

    // A page shape change must yield "no hotfix known", never a release built from a guess.
    Expect("a page without a hotfix link parses to null",
        NvidiaHotfixLookup.Parse("GeForce Hotfix Display Driver version 610.82 (page changed)") is null
        && NvidiaHotfixLookup.Parse("") is null);
    // The WHQL URL shape must not be mistaken for a hotfix one.
    Expect("a WHQL download URL is not accepted as a hotfix",
        NvidiaHotfixLookup.Parse(
            "https://us.download.nvidia.com/Windows/610.74/610.74-desktop-win10-win11-64bit-international-dch-whql.exe") is null);
}

// ── Driver package stripping, against a REAL setup.cfg ────────────────────────────────
// The fixture is an actual 591.86 manifest pulled off a live install, so the component names,
// the disposition attribute and the options list are what the installer really ships.
{
    var cfgPath = Path.Combine(repo, "tools", "Nvidia.Smoke", "fixtures", "setup.cfg");
    Expect("setup.cfg fixture present", File.Exists(cfgPath));
    if (File.Exists(cfgPath))
    {
        var cfg = File.ReadAllText(cfgPath);

        Expect("package version reads from the manifest root",
            NvidiaDriverPackage.ReadVersion(cfg) == "591.86", NvidiaDriverPackage.ReadVersion(cfg) ?? "null");
        Expect("components enumerate",
            NvidiaDriverPackage.ListComponents(cfg).Contains("Display.Driver")
            && NvidiaDriverPackage.ListComponents(cfg).Contains("Ansel"));

        var r = NvidiaDriverPackage.Strip(cfg);

        // The driver itself must survive any removal set, by both guards.
        Expect("the display driver is never removed", r.Kept.Contains("Display.Driver"));
        Expect("the updater and Ansel are removed",
            r.Removed.Contains("NVIDIA.Update") && r.Removed.Contains("Ansel")
            && r.Removed.Contains("Display.Update"));

        // Removing these looks like a saving and costs something real.
        Expect("HDMI/DisplayPort audio is kept", !r.Removed.Contains("HDAudio.Driver"));
        Expect("PhysX is kept - shipped games still link it", r.Kept.Contains("Display.PhysX"));
        Expect("Optimus is kept - removing it breaks hybrid laptops", r.Kept.Contains("Display.Optimus"));
        Expect("the MSVC runtimes are kept", r.Kept.Contains("MSVCRuntime2019"));

        // Dangling dependencies are the failure mode of a hand-edited setup.cfg: a surviving
        // package still names a removed one and the installer fails partway through.
        Expect("references to removed packages are cleaned up",
            r.DanglingReferencesCleaned.Any(d => d.Contains("Ansel", StringComparison.Ordinal)),
            string.Join("; ", r.DanglingReferencesCleaned));
        var stripped = r.Xml;
        Expect("no reference to a removed package survives in the output",
            !stripped.Contains("package=\"Ansel\"", StringComparison.Ordinal)
            && !stripped.Contains("name=\"Ansel\"", StringComparison.Ordinal));
        Expect("the stripped manifest is still valid XML with an install section",
            System.Xml.Linq.XDocument.Parse(stripped).Root?.Element("install") is not null);

        // Asking to remove a critical component must be refused and reported, not silently
        // ignored - the caller has to be able to say why it did not happen.
        var forced = NvidiaDriverPackage.Strip(cfg, new[] { "Display.Driver", "HDAudio.Driver" });
        Expect("removing the driver is refused with a reason",
            forced.Removed.Count == 0 && forced.RefusedToRemove.Any(x => x.StartsWith("Display.Driver", StringComparison.Ordinal)),
            string.Join("; ", forced.RefusedToRemove));

        // Install flags are taken from the package's own <options>, never invented. An unknown
        // flag turns a silent install into a dialog with nobody there to click it.
        var installArgs = NvidiaDriverPackage.BuildInstallArguments(cfg);
        Expect("install arguments come from the manifest options",
            installArgs.Contains("-s") && installArgs.Contains("-clean") && installArgs.Contains("-noeula")
            && installArgs.Contains("-noreboot"), installArgs);
        Expect("no flag is passed that the manifest does not declare",
            !installArgs.Contains("-nosplash") && !installArgs.Contains("-nofinish2"), installArgs);
    }
}

// ── Cross-setting conflict audit ───────────────────────────────────────────────────────
// Found by diffing the shipped packs against each other and against what other Exo modules
// do. Both of these were live in all 10 packs.
{
    var profDir = Path.Combine(repo, "Exo", "Scripts", "Nvidia", "profiles");
    foreach (var nip in Directory.Exists(profDir) ? Directory.GetFiles(profDir, "*.nip") : Array.Empty<string>())
    {
        var name = Path.GetFileName(nip);
        var isGsyncPack = name.Contains("G-SYNC", StringComparison.Ordinal);
        var doc = System.Xml.Linq.XDocument.Load(nip);
        var vals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prof in doc.Descendants("Profile"))
        {
            if (!string.Equals(prof.Element("ProfileName")?.Value?.Trim(), "Base Profile",
                    StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var ps in prof.Descendants("ProfileSetting"))
                vals[ps.Element("SettingID")!.Value.Trim()] = ps.Element("SettingValue")!.Value.Trim();
        }
        string V(string id) => vals.TryGetValue(id, out var v) ? v : "-";

        // Conflict 1: the raw-latency packs turned G-SYNC off globally (294973784 / 278196727)
        // but left the per-application mode and requested-state ON (279476687 / 279476652).
        // On a VRR display a game could re-enable G-SYNC for itself, which is the exact thing
        // the raw pack exists to prevent. The variants have to be internally consistent.
        if (!isGsyncPack)
        {
            Expect($"{name}: raw pack disables G-SYNC per-application too",
                V("279476687") == "0" && V("279476652") == "0",
                $"appMode={V("279476687")} appRequested={V("279476652")}");
            Expect($"{name}: raw pack disables G-SYNC globally",
                V("294973784") == "0" && V("278196727") == "0");
        }
        else
        {
            Expect($"{name}: G-SYNC pack enables it globally and per-application",
                V("294973784") == "1" && V("279476687") == "1");
        }

        // Conflict 2, and the one that actually cost frames: Background Application Max Frame
        // Rate was capped at 30 in every pack. Exo's own Games module FORCES borderless
        // windowed, so alt-tabbing to Discord makes the game a background application and
        // drops it to 30fps. Two Exo features fighting each other on the same machine.
        Expect($"{name}: no background frame cap (Exo forces borderless, so the game would hit it)",
            V("277041158") == "0", $"backgroundCap={V("277041158")}");
    }
}

Expect("detect surfaces the GPU power row",
    detectSrc.Contains("GPU power & thermal ceiling", StringComparison.Ordinal)
    && detectSrc.Contains("active = [bool]$gpuPowerOk", StringComparison.Ordinal));
Expect("optimizer keeps failure-state path without display retry thrash",
    optimizerSrc.Contains("Save-ExoFailureState -Stage $failStage -Message $failMessage", StringComparison.Ordinal) &&
    !optimizerSrc.Contains("forcing one more Display-Apply pass", StringComparison.Ordinal));
Expect("optimizer catch still saves failure state",
    optimizerSrc.Contains("if (-not [bool]$Script:CompletedPartialDisplayPolicy)", StringComparison.Ordinal) &&
    optimizerSrc.Contains("Save-ExoFailureState -Stage $failStage -Message $failMessage", StringComparison.Ordinal));
Expect("optimizer audio stage runs once",
    CountOf(optimizerSrc, "[void](Remove-NvidiaAudioComponents)") == 1);
Expect("optimizer bloat stage runs once",
    CountOf(optimizerSrc, "[void](Remove-NvidiaBloatComponents)") == 1);
// These two used to assert `SafePolicy = $true` in the shipped path and that it "blocks
// destructive NVIDIA stages" - pinning as required contract the exact flag that switched off
// app removal, debloat, overlay, GPU power and display prefs. The module's whole job was
// disabled and this is the test that kept it that way. Replaced by the invocation gate below.
Expect("the shipped path removes the NVIDIA App rather than keeping it",
    runScriptSrc.Contains("SkipDriver = $true", StringComparison.Ordinal) &&
    !runScriptSrc.Contains("SafePolicy = $true", StringComparison.Ordinal));
Expect("the App-removal path still exists to be reached",
    optimizerSrc.Contains("function Remove-NvidiaClientTraces", StringComparison.Ordinal) &&
    optimizerSrc.Contains("Remove-NvidiaClientTraces", StringComparison.Ordinal));
Expect("maximum performance is per-game rather than global",
    optimizerSrc.Contains("The clones above retain the pin", StringComparison.Ordinal) &&
    optimizerSrc.Contains("$map.Remove('274197361')", StringComparison.Ordinal));
Expect("NVIDIA DRS has exact backup and restore",
    optimizerSrc.Contains("--drs-backup", StringComparison.Ordinal) &&
    optimizerSrc.Contains("--drs-restore", StringComparison.Ordinal) &&
    optimizerSrc.Contains("nvidia-drs-pre-exo.bin", StringComparison.Ordinal) &&
    nvDisplaySrc.Contains("DriverSettingsSession.CreateAndLoad(fullPath)", StringComparison.Ordinal) &&
    nvDisplaySrc.Contains("session.Save(fullPath)", StringComparison.Ordinal));
// Packaging: Publish-Exo ships FDD NvDisplay (~0.7 MB) not 70 MB single-file.
var publishScript = Path.Combine(repo, "Publish-Exo.ps1");
if (File.Exists(publishScript))
{
    var pub = File.ReadAllText(publishScript);
    Expect("Publish-Exo ships NvDisplay FDD",
        pub.Contains("Exo.NvDisplay", StringComparison.Ordinal) &&
        pub.Contains("--self-contained false", StringComparison.Ordinal) &&
        pub.Contains("PublishSingleFile=false", StringComparison.Ordinal) &&
        pub.Contains("NvAPIWrapper.dll", StringComparison.Ordinal) &&
        !pub.Contains("PublishSingleFile=true", StringComparison.Ordinal));
}
// Repair matrix: DRS restore is the authoritative undo; drivers/display stay untouched.
Expect("NVIDIA Repair restores DRS snapshot",
    optimizerSrc.Contains("Repair: restore the exact pre-Exo NVIDIA DRS database", StringComparison.Ordinal) &&
    optimizerSrc.Contains("nvidia-drs-pre-exo.bin", StringComparison.Ordinal) &&
    File.Exists(Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Exo-Nvidia-Repair.ps1")));
Expect("optimizer selects policy from hardware inventory",
    optimizerSrc.Contains("function Get-NvidiaHardwarePolicy", StringComparison.Ordinal) &&
    optimizerSrc.Contains("--list-displays", StringComparison.Ordinal) &&
    optimizerSrc.Contains("adaptiveSyncSignal", StringComparison.Ordinal) &&
    optimizerSrc.Contains("hardwarePolicy", StringComparison.Ordinal));
Expect("G-SYNC requires explicit user selection",
    optimizerSrc.Contains("explicit-gsync", StringComparison.Ordinal) &&
    optimizerSrc.Contains("safe-default-raw-latency", StringComparison.Ordinal) &&
    !optimizerSrc.Contains("display-hardware-auto", StringComparison.Ordinal) &&
    optimizerSrc.Contains("[switch]$RawLatency", StringComparison.Ordinal));
Expect("raw-latency disables every VRR path",
    optimizerSrc.Contains("'11041279'  = '0'", StringComparison.Ordinal) &&
    optimizerSrc.Contains("'294973784' = '0'", StringComparison.Ordinal) &&
    optimizerSrc.Contains("'278196727' = '0'", StringComparison.Ordinal));
Expect("global ULL Ultra remains authoritative fallback",
    optimizerSrc.Contains("'390467'    = '2'", StringComparison.Ordinal) &&
    optimizerSrc.Contains("'277041152' = '1'", StringComparison.Ordinal) &&
    (detectSrc.Contains("Reflex takes priority automatically", StringComparison.Ordinal) ||
     detectSrc.Contains("Reflex still wins in supported titles", StringComparison.Ordinal) ||
     optimizerSrc.Contains("Reflex overrides this in supported games", StringComparison.Ordinal)));
Expect("display helper detects refresh and EDID evidence",
    nvDisplaySrc.Contains("maxHz", StringComparison.Ordinal) &&
    nvDisplaySrc.Contains("ReadMonitorVerticalRange", StringComparison.Ordinal) &&
    nvDisplaySrc.Contains("adaptiveSyncCandidate", StringComparison.Ordinal));
Expect("secondary display refresh stays unchanged",
    nvDisplaySrc.Contains("EXO_SECONDARY_REFRESH", StringComparison.Ordinal) &&
    nvDisplaySrc.Contains("?? \"keep\"", StringComparison.Ordinal) &&
    !nvDisplaySrc.Contains("?? \"60\"", StringComparison.Ordinal));
// Policy: always GitHub Latest for Profile Inspector (no hard-pinned old tags).
Expect("NPI resolves GitHub Latest",
    optimizerSrc.Contains("Resolve-LatestNpiRelease", StringComparison.Ordinal) &&
    optimizerSrc.Contains("Orbmu2k/nvidiaProfileInspector/releases/latest", StringComparison.Ordinal));
Expect("NPI no hard-pinned old tag constant",
    !optimizerSrc.Contains("NpiPinnedTag = 'v3.0.1.11'", StringComparison.Ordinal) &&
    !optimizerSrc.Contains("NpiPinnedZipSha256", StringComparison.Ordinal));
Expect("NPI version stamp kept", optimizerSrc.Contains("EXO-NPI-VERSION.txt", StringComparison.Ordinal));
Expect("NPI policy github-latest", optimizerSrc.Contains("policy=github-latest", StringComparison.Ordinal));

// --- PowerShell 7 host (Preview preferred, stable accepted) ---
Expect("optimizer host check accepts any pwsh 7.x", optimizerSrc.Contains("function Test-ExoIsPwsh7Host", StringComparison.Ordinal));
Expect("optimizer resolves Preview before stable",
    optimizerSrc.Contains("function Get-ExoPwsh", StringComparison.Ordinal) &&
    optimizerSrc.Contains(@"'PowerShell\7-preview\pwsh.exe'", StringComparison.Ordinal) &&
    optimizerSrc.IndexOf(@"'PowerShell\7-preview\pwsh.exe'", StringComparison.Ordinal) <
    optimizerSrc.IndexOf(@"'PowerShell\7\pwsh.exe'", StringComparison.Ordinal));
Expect("optimizer asserts pwsh 7 host", optimizerSrc.Contains("Assert-ExoPwsh7", StringComparison.Ordinal));
Expect("optimizer install hint mentions Preview or stable",
    optimizerSrc.Contains("Microsoft.PowerShell.Preview", StringComparison.Ordinal) ||
    optimizerSrc.Contains("winget install Microsoft.PowerShell", StringComparison.Ordinal));
Expect("optimizer does not use retired Assert-ExoPwshPreview",
    !optimizerSrc.Contains("Assert-ExoPwshPreview", StringComparison.Ordinal));
Expect("run script requires PowerShell 7",
    runScriptSrc.Contains("requires PowerShell 7", StringComparison.OrdinalIgnoreCase));

Expect("detect emits drsLive field", detectSrc.Contains("drsLive", StringComparison.Ordinal));
Expect("detect emits verified row string",
    detectSrc.Contains("'Verified in driver'", StringComparison.Ordinal) ||
    coreSrc.Contains("'Verified in driver'", StringComparison.Ordinal));
// PS sources must stay ASCII; the em dash in the drifted row string is built via char code.
Expect("detect drifted row string via char code",
    coreSrc.Contains("'Drifted ' + [char]0x2014 + ' re-apply'", StringComparison.Ordinal));
Expect("detect profile stage gates on drift",
    detectSrc.Contains("$profileOk -and ($drsLive -ne 'drifted')", StringComparison.Ordinal));
Expect("core + optimizer DRS classifier in sync",
    coreSrc.Contains("function Get-ExoDrsVerificationResult", StringComparison.Ordinal) &&
    optimizerSrc.Contains("function Get-ExoDrsVerificationResult", StringComparison.Ordinal));

// NVI2 install-time strip (ShadowPlay / NvBackend / NodeJS / telemetry; keep Display.Driver + PhysX)
Expect("NVI2 bloat classifier present", optimizerSrc.Contains("function Test-Nvi2BloatPackageName", StringComparison.Ordinal));
Expect("NVI2 bloat strip present", optimizerSrc.Contains("function Remove-NvidiaBloatComponents", StringComparison.Ordinal));
Expect("NVI2 bloat targets ShadowPlay/NvBackend/NodeJS/telemetry",
    optimizerSrc.Contains("ShadowPlay|NvBackend|NodeJS|Node\\.js|Telemetry", StringComparison.Ordinal));
Expect("NVI2 bloat strip preserves PhysX", optimizerSrc.Contains("PhysX", StringComparison.Ordinal));

// Per-game catalog spot checks (new titles + new exe aliases)
foreach (var exe in new[]
{
    "RustClient.exe", "GTA5.exe", "FiveM.exe", "FiveM_GTAProcess.exe",
    "marvel-rivals.exe", "MarvelRivals_Launcher.exe", "RainbowSix_BE.exe",
    "cod22-cod.exe", "cod23-cod.exe", "r5apex.exe", "TslGame.exe", "dota2.exe",
})
{
    Expect($"catalog has {exe}", optimizerSrc.Contains(exe, StringComparison.OrdinalIgnoreCase));
}
// Minecraft javaw is intentionally excluded (shared Java host process)
Expect("catalog excludes shared javaw.exe",
    !optimizerSrc.Contains("'javaw.exe'", StringComparison.OrdinalIgnoreCase) &&
    optimizerSrc.Contains("javaw.exe is shared", StringComparison.OrdinalIgnoreCase));

// Pack versions bumped in lockstep
var packVersion = File.ReadAllText(Path.Combine(repo, "Exo", "Scripts", "Nvidia", "VERSION")).Trim();
var profileVersion = File.ReadAllText(Path.Combine(repo, "Exo", "Scripts", "Nvidia", "profiles", "PROFILE_VERSION")).Trim();
Expect($"pack VERSION {packVersion}", !string.IsNullOrWhiteSpace(packVersion) && packVersion.StartsWith("1.", StringComparison.Ordinal), packVersion);
Expect($"PROFILE_VERSION {profileVersion}",
    !string.IsNullOrWhiteSpace(profileVersion) && profileVersion.StartsWith("1.", StringComparison.Ordinal),
    profileVersion);
Expect("optimizer version constant matches VERSION",
    optimizerSrc.Contains($"$Script:NvidiaOptVersion = '{packVersion}'", StringComparison.Ordinal));

// New Base Profile pins present in the 40 Series packs (values derived from NPI metadata:
// 0x10835006 background max FPS, 0x20D690F8 OGL_CPL_PREFER_DXPRESENT=PREFER_ENABLED(1))
foreach (var packName in new[] { "40 Series.nip", "40 Series G-SYNC.nip" })
{
    var packPath = Path.Combine(repo, "Exo", "Scripts", "Nvidia", "profiles", packName);
    Expect($"pack exists: {packName}", File.Exists(packPath), packPath);
    if (!File.Exists(packPath)) continue;
    var xml = System.Xml.Linq.XDocument.Parse(File.ReadAllText(packPath));
    var basePins = xml.Descendants("Profile")
        .Where(p => (string?)p.Element("ProfileName") == "Base Profile")
        .SelectMany(p => p.Descendants("ProfileSetting"))
        .ToDictionary(
            s => (string?)s.Element("SettingID") ?? "",
            s => (string?)s.Element("SettingValue") ?? "");
    // Was pinned at 30 here, which made this gate defend the conflict rather than catch it:
    // Exo's Games module forces borderless windowed, so alt-tabbing made the GAME a background
    // application and capped it to 30fps. The pin is now the absence of a cap.
    Expect($"{packName}: background frame cap off (277041158=0)",
        basePins.TryGetValue("277041158", out var bg) && bg == "0", bg ?? "missing");
    Expect($"{packName}: Vulkan/OpenGL present method pin (550867192=1)",
        basePins.TryGetValue("550867192", out var pm) && pm == "1", pm ?? "missing");
    Expect($"{packName}: rBAR enable retained (983226=1)",
        basePins.TryGetValue("983226", out var rbar) && rbar == "1", rbar ?? "missing");
    Expect($"{packName}: threaded optimization retained (549528094=1)",
        basePins.TryGetValue("549528094", out var thr) && thr == "1", thr ?? "missing");
    Expect($"{packName}: unlimited shader cache retained (11306135)",
        basePins.TryGetValue("11306135", out var sc) && sc == "4294967295", sc ?? "missing");
}

// --- Panel pure helpers (shipped NvidiaPanelLogic) ---
Expect("parse mode 2560x1440@165",
    NvidiaPanelLogic.TryParseModeLabel("2560x1440@165", out var mw, out var mh, out var mhz) &&
    mw == 2560 && mh == 1440 && mhz == 165);
Expect("parse mode with Hz suffix",
    NvidiaPanelLogic.TryParseModeLabel("1920x1080@144Hz", out _, out _, out var hz2) && hz2 == 144);
Expect("format mode", NvidiaPanelLogic.FormatModeLabel(1920, 1080, 60) == "1920x1080@60");
Expect("depth 10-bit -> 10", NvidiaPanelLogic.ToDepthCliArg("10-bit") == "10");
Expect("depth BPC12 -> 12", NvidiaPanelLogic.ToDepthCliArg("BPC12") == "12");
Expect("scaling gpu no-scaling", NvidiaPanelLogic.ToScalingCliArg("GPU no-scaling") == "gpu-noscaling");
Expect("scaling gpu default", NvidiaPanelLogic.ToScalingCliArg("GPU scaling") == "gpu");
Expect("scaling display", NvidiaPanelLogic.ToScalingCliArg("Display scaling") == "display");
Expect("default set-scaling is gpu-noscaling",
    NvidiaPanelLogic.BuildSetScalingArgs(null!, null).Contains("gpu-noscaling", StringComparison.Ordinal));
Expect("color full", NvidiaPanelLogic.ToColorRangeCliArg("Full RGB") == "full");
Expect("color limited", NvidiaPanelLogic.ToColorRangeCliArg("Limited") == "limited");
Expect("list-displays args", NvidiaPanelLogic.BuildListDisplaysArgs() == "--list-displays");
Expect("set-mode args with id",
    NvidiaPanelLogic.BuildSetModeArgs(2560, 1440, 165, 42) == "--set-mode 2560x1440@165 --display-id 42");
Expect("set-depth args",
    NvidiaPanelLogic.BuildSetDepthArgs("12-bit", null).Contains("--set-depth 12", StringComparison.Ordinal));
Expect("set-scaling args",
    NvidiaPanelLogic.BuildSetScalingArgs("GPU no-scaling", 7) ==
    "--set-scaling gpu-noscaling --display-id 7");
Expect("set-color-range args",
    NvidiaPanelLogic.BuildSetColorRangeArgs("Full RGB", null) == "--set-color-range full");
// Digital vibrance (DVC) CLI builders
Expect("get-vibrance args", NvidiaPanelLogic.BuildGetVibranceArgs() == "--get-vibrance");
Expect("set-vibrance args",
    NvidiaPanelLogic.BuildSetVibranceArgs(50, null) == "--set-vibrance 50");
Expect("set-vibrance args with id",
    NvidiaPanelLogic.BuildSetVibranceArgs(63, 7) == "--set-vibrance 63 --display-id 7");
Expect("set-vibrance clamps above driver max",
    NvidiaPanelLogic.BuildSetVibranceArgs(500, null) == "--set-vibrance 63");
Expect("set-vibrance clamps below zero",
    NvidiaPanelLogic.BuildSetVibranceArgs(-5, null) == "--set-vibrance 0");
Expect("vibrance clamp honors driver range",
    NvidiaPanelLogic.ClampVibranceLevel(80, 0, 100) == 80 &&
    NvidiaPanelLogic.ClampVibranceLevel(80) == 63);

var modes = new[] { "2560x1440@165", "2560x1440@144", "1920x1080@60", "1920x1080@144" };
var res = NvidiaPanelLogic.DistinctResolutions(modes);
Expect("distinct res largest first", res.Count >= 2 && res[0].StartsWith("2560", StringComparison.Ordinal));
var rates = NvidiaPanelLogic.RefreshRatesForResolution(modes, "2560x1440");
Expect("refresh rates for res", rates.Count == 2 && rates[0].Contains("165", StringComparison.Ordinal));

// Live helper when present (not a reimplementation)
var nvExe = Path.Combine(repo, "Exo", "Scripts", "Nvidia", "tools", "Exo.NvDisplay.exe");
if (!File.Exists(nvExe))
{
    // publish output path used by release
    var alt = Path.Combine(repo, "tools", "Exo.NvDisplay", "bin", "Release", "net10.0-windows", "win-x64", "Exo.NvDisplay.exe");
    if (File.Exists(alt)) nvExe = alt;
}
if (File.Exists(nvExe))
{
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = nvExe,
        Arguments = "--list-displays",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    using var p = System.Diagnostics.Process.Start(psi)!;
    var so = p.StandardOutput.ReadToEnd();
    p.WaitForExit(45000);
    Expect("list-displays JSON", so.Contains("EXO_NVDISPLAY_JSON:", StringComparison.Ordinal), so.Length > 0 ? so[^Math.Min(200, so.Length)..] : "empty");
    Expect("list-displays modes field", so.Contains("\"modes\"", StringComparison.Ordinal) || so.Contains("modes", StringComparison.Ordinal));
    Expect("list-displays ok", so.Contains("\"ok\":true", StringComparison.Ordinal) || so.Contains("\"ok\": true", StringComparison.Ordinal));
}
else
{
    Log("SKIP  live list-displays (helper exe missing — structural args covered)");
}

// Structural: helper Program exposes set-mode / set-scaling
var nvProg = Path.Combine(repo, "tools", "Exo.NvDisplay", "Program.cs");
if (File.Exists(nvProg))
{
    var src = File.ReadAllText(nvProg);
    Expect("helper has --list-displays", src.Contains("--list-displays", StringComparison.Ordinal));
    Expect("helper has --set-mode", src.Contains("--set-mode", StringComparison.Ordinal));
    Expect("helper has --set-scaling", src.Contains("--set-scaling", StringComparison.Ordinal));
    Expect("helper has --set-color-range", src.Contains("--set-color-range", StringComparison.Ordinal));
    Expect("helper has --set-vibrance", src.Contains("--set-vibrance", StringComparison.Ordinal));
    Expect("helper has --get-vibrance", src.Contains("--get-vibrance", StringComparison.Ordinal));
    Expect("helper uses NvAPIWrapper DVC", src.Contains("DigitalVibranceControl", StringComparison.Ordinal));
    Expect("helper status includes vibrance", src.Contains("vibrance = ListVibrance(devices, null)", StringComparison.Ordinal));
    Expect("helper verifies vibrance readback", src.Contains("verified = live", StringComparison.Ordinal));
}

// --- The verdict must include what Exo actually drives ---
// Found on real hardware: a 165 Hz panel running at 120 and a second monitor on limited
// RGB, with Exo reporting "your PC is good to go". Three separate defects let that pass,
// each pinned below.
{
    var helper = Path.Combine(repo, "tools", "Exo.NvDisplay", "Program.cs");
    var detect = Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Exo-Nvidia-Detect.ps1");
    var stateSvc = Path.Combine(repo, "Exo", "Services", "OptimizerStateService.cs");
    Expect("NVIDIA display sources present",
        File.Exists(helper) && File.Exists(detect) && File.Exists(stateSvc));

    if (File.Exists(helper))
    {
        var h = File.ReadAllText(helper);

        // 1. modesOk was initialised true and only recomputed inside the apply branch, so a
        //    status run never checked refresh at all.
        Expect("status runs verify refresh instead of assuming it",
            h.Contains("if (!apply && nvidiaGdiNames.Count > 0)", StringComparison.Ordinal)
            && h.Contains("modesOk = VerifyTargetRefreshModes(nvidiaGdiNames);", StringComparison.Ordinal));

        // 2. PickBestDepth probed the current depth first, so an 8-bit link that supports
        //    10-bit returned 8 immediately and could never climb.
        var pickStart = h.IndexOf("static ColorDataDepth PickBestDepth", StringComparison.Ordinal);
        var pickEnd = h.IndexOf("static ColorData? ApplyColorWithFallbacks", StringComparison.Ordinal);
        Expect("PickBestDepth body located", pickStart >= 0 && pickEnd > pickStart);
        if (pickStart >= 0 && pickEnd > pickStart)
        {
            var body = h[pickStart..pickEnd];
            Expect("bit depth probes highest-first, not current-first",
                !body.Contains("if (current is not null) order.Add(current.Value);", StringComparison.Ordinal));
            Expect("BPC10 is reachable from a BPC8 display",
                body.IndexOf("ColorDataDepth.BPC10", StringComparison.Ordinal)
                < body.IndexOf("ColorDataDepth.BPC8", StringComparison.Ordinal));
            Expect("BPC12 still only when already there",
                body.Contains("if (current == ColorDataDepth.BPC12)", StringComparison.Ordinal));
        }
    }

    if (File.Exists(detect))
    {
        var d = File.ReadAllText(detect);
        // 3. displayOk was measured from live NVAPI and then left out of isApplied.
        Expect("display state counts toward isApplied",
            d.Contains("$driverStageOk -and $latencyPolicyOk -and $displayOk", StringComparison.Ordinal));
        Expect("display feature row reports measured state, not a constant",
            d.Contains("active = [bool]$displayOk", StringComparison.Ordinal));
        Expect("the stale 'manual in Control Panel' claim is gone",
            !d.Contains("are manual in Control Panel", StringComparison.Ordinal)
            && !d.Contains("Left for you in Control Panel", StringComparison.Ordinal));
    }

    if (File.Exists(stateSvc))
    {
        Expect("heuristic no longer ships an always-green display row",
            !File.ReadAllText(stateSvc)
                .Contains("MakeFeature(\"Display scaling & color\", \"Manual in Control Panel\", true)", StringComparison.Ordinal));
    }

    // A second high-refresh monitor must not be silently halved. This shipped as
    // secondaryRefresh="60" enforced on every Save(), which is a downgrade sold as an
    // optimization — and untestable on a rig whose secondary maxes at 60 regardless.
    var panelSvc = Path.Combine(repo, "Exo", "Services", "NvidiaPanelSettingsService.cs");
    var applyPs1 = Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Exo-Display-Apply.ps1");
    Expect("panel service + display apply script present",
        File.Exists(panelSvc) && File.Exists(applyPs1));
    if (File.Exists(panelSvc))
    {
        var p = File.ReadAllText(panelSvc);
        Expect("Save no longer stomps the user's secondary-refresh choice",
            !p.Contains("settings.SecondaryRefresh = \"60\";", StringComparison.Ordinal));
        Expect("primary still forced to max refresh",
            p.Contains("settings.PrimaryRefresh = \"max\";", StringComparison.Ordinal));
        Expect("status verifies against the same policy apply used",
            !p.Contains("psi.Environment[\"EXO_SECONDARY_REFRESH\"] = \"60\";", StringComparison.Ordinal)
            && p.Contains("policy.SecondaryRefresh", StringComparison.Ordinal));
    }
    if (File.Exists(applyPs1))
    {
        Expect("shipped default leaves a secondary monitor alone",
            File.ReadAllText(applyPs1).Contains("secondaryRefresh        = 'keep'", StringComparison.Ordinal));
    }

    // The display path shipped wired to nothing: Set-NvidiaDisplayPreferences was defined
    // and never called, so Apply never touched refresh, colour range, bit depth or scaling
    // — while $dispResult.Details was read in Save-State without ever being assigned.
    // Detect reporting the truth is only useful if Apply can act on it.
    var optimizer = Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Nvidia-Optimizer.ps1");
    Expect("Nvidia-Optimizer.ps1 present", File.Exists(optimizer));
    if (File.Exists(optimizer))
    {
        var o = File.ReadAllText(optimizer);
        Expect("display policy is actually invoked during Apply",
            o.Contains("$dispResult = Set-NvidiaDisplayPreferences", StringComparison.Ordinal));
        Expect("Apply no longer announces that it skips displays",
            !o.Contains("Skipping display scaling/color", StringComparison.Ordinal)
            && !o.Contains("are not forced  -  open Control Panel", StringComparison.Ordinal));
        // No newline inside the literal: Windows CI checks out CRLF, this is authored LF.
        Expect("saved display marker reflects the real result",
            !o.Contains("# Always false: Exo does not force Control Panel", StringComparison.Ordinal)
            && o.Contains("displayPrefs        = $(if ($SafePolicy)", StringComparison.Ordinal)
            && o.Contains("[bool]$displayPrefsOk", StringComparison.Ordinal));
        Expect("the measured display result is not clobbered afterwards",
            !o.Contains("# Display scaling / NVIDIA color are intentionally not applied or re-tried.", StringComparison.Ordinal));
        Expect("$dispResult is initialised before use",
            o.IndexOf("$dispResult = @{ Success = $false", StringComparison.Ordinal) > 0
            && o.IndexOf("$dispResult = @{ Success = $false", StringComparison.Ordinal)
               < o.IndexOf("displayDetails      = @($dispResult.Details)", StringComparison.Ordinal));
    }
}

Log($"=== SUMMARY failed={failed} ===");
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
File.WriteAllLines(logPath, lines);
Console.WriteLine("Wrote " + logPath);
Environment.Exit(failed == 0 ? 0 : 1);

static int CountOf(string text, string value)
{
    var count = 0;
    var start = 0;
    while ((start = text.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
    {
        count++;
        start += value.Length;
    }
    return count;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Exo", "Scripts", "Nvidia", "NvidiaDetectCore.ps1")))
            return dir.FullName;
        if (File.Exists(Path.Combine(dir.FullName, "VERSION")) &&
            Directory.Exists(Path.Combine(dir.FullName, "Exo", "Scripts", "Nvidia")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}

// True when `needle` appears on a line that is actual code rather than a comment. The
// assertions that ban an NVAPI call must not be tripped by the comment explaining why it is
// banned. Splits on '\n' and trims, so it is CRLF-safe.
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
