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

Expect("display container exe",
    NvidiaDetectLogic.IsDisplayContainerExe(@"C:\Windows\System32\DriverStore\...\NVDisplay.Container.exe"));
Expect("app tray exe",
    NvidiaDetectLogic.IsNvidiaAppTrayExe(@"C:\Program Files\NVIDIA Corporation\NVIDIA App\NVIDIA App.exe"));
Expect("display not app tray",
    !NvidiaDetectLogic.IsNvidiaAppTrayExe(@"...\NVDisplay.Container.exe"));

Expect("tray hidden IsPromoted=0", NvidiaDetectLogic.IsDisplayContainerTrayHidden(true, 0));
Expect("tray not hidden IsPromoted=1", !NvidiaDetectLogic.IsDisplayContainerTrayHidden(true, 1));
Expect("tray missing key ok", NvidiaDetectLogic.IsDisplayContainerTrayHidden(false, null));
Expect("app ghost gone", NvidiaDetectLogic.IsAppTrayGhostGone(false));
Expect("app ghost present fail", !NvidiaDetectLogic.IsAppTrayGhostGone(true));

// Display status gate: orphan registry must not fail when color+scale live OK
Expect("display status: refresh+registry",
    NvidiaDetectLogic.IsDisplayStatusOk(true, true, false, false));
Expect("display status: refresh+live color/scale without registry",
    NvidiaDetectLogic.IsDisplayStatusOk(true, false, true, true));
Expect("display status: no refresh fails",
    !NvidiaDetectLogic.IsDisplayStatusOk(false, true, true, true));
Expect("display status: no registry and no live fails",
    !NvidiaDetectLogic.IsDisplayStatusOk(true, false, true, false));

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
    ["549528094"] = "1",  // threaded optimization
};
var drsRequired = NvidiaDetectLogic.DrsRequiredPinIds;
Expect("drs required pins cover PM/ULL/FRL/G-SYNC",
    drsRequired.Contains("274197361") && drsRequired.Contains("390467") &&
    drsRequired.Contains("277041152") && drsRequired.Contains("277041154") &&
    drsRequired.Contains("294973784"));

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
var full = NvidiaDetectLogic.IsDisplayStatusOk(true, false, true, true) &&
           NvidiaDetectLogic.IsDisplayContainerTrayHidden(true, 0) &&
           NvidiaDetectLogic.ProfileNameMatchesSeries("30 Series.nip", "30", false) &&
           NvidiaDetectLogic.IsAppTrayGhostGone(false);
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
 (E 'ps display status orphan reg' (Test-ExoDisplayStatusOk -RefreshOk $true -RegistryOk $false -ColorOk $true -PathScalingOk $true)),
 (E 'ps tray hidden' (Test-ExoDisplayTrayHidden -KeyExists $true -IsPromoted 0)),
 (E 'ps tray not hidden' (-not (Test-ExoDisplayTrayHidden -KeyExists $true -IsPromoted 1))),
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
    Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Exo-Nvidia-TrayClear.ps1"),
    Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Exo-Display-Apply.ps1"),
};
var blob = string.Join("\n", applyFiles.Where(File.Exists).Select(File.ReadAllText));
Expect("apply sources readable", blob.Length > 5000);
var (ok, issues) = NvidiaDetectLogic.AuditApplyScriptText(blob);
Expect("apply audit", ok, string.Join("; ", issues));
Expect("no tray logon task create",
    !System.Text.RegularExpressions.Regex.IsMatch(blob, @"Register-ScheduledTask[^\r\n]*Exo-Nvidia",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase));
Expect("Unregister tray tasks present",
    blob.Contains("Unregister-ExoTrayTasks", StringComparison.OrdinalIgnoreCase) ||
    blob.Contains("Exo-NvidiaTrayHide", StringComparison.OrdinalIgnoreCase));

// --- DRS verification + NPI pin + strip + catalog markers ---
var optimizerPath = Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Nvidia-Optimizer.ps1");
var displayApplyPath = Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Exo-Display-Apply.ps1");
var detectPath = Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Exo-Nvidia-Detect.ps1");
var corePath = Path.Combine(repo, "Exo", "Scripts", "Nvidia", "NvidiaDetectCore.ps1");
var messagesPath = Path.Combine(repo, "Exo", "Helpers", "OptimizerMessages.cs");
var nvidiaViewModelPath = Path.Combine(repo, "Exo", "ViewModels", "NvidiaOptimizerViewModel.cs");
var optimizerSrc = File.Exists(optimizerPath) ? File.ReadAllText(optimizerPath) : "";
var displayApplySrc = File.Exists(displayApplyPath) ? File.ReadAllText(displayApplyPath) : "";
var detectSrc = File.Exists(detectPath) ? File.ReadAllText(detectPath) : "";
var coreSrc = File.Exists(corePath) ? File.ReadAllText(corePath) : "";
var messagesSrc = File.Exists(messagesPath) ? File.ReadAllText(messagesPath) : "";
var nvidiaViewModelSrc = File.Exists(nvidiaViewModelPath) ? File.ReadAllText(nvidiaViewModelPath) : "";

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
Expect("optimizer displayPrefs accepts working path",
    optimizerSrc.Contains("$displayPrefsOk = [bool]$dispResult.Success -or [bool]$displayNvApiOk -or [bool]$displayRegistryOk", StringComparison.Ordinal) &&
    optimizerSrc.Contains("displayPrefs        = [bool]$displayPrefsOk", StringComparison.Ordinal));
Expect("optimizer records registry display method",
    optimizerSrc.Contains("$displayMethod = if ($displayNvApiOk) { 'nvapi' } elseif ($displayRegistryOk) { 'registry' } else { $null }", StringComparison.Ordinal));
Expect("optimizer retries display before fail",
    optimizerSrc.Contains("display-policy-retry", StringComparison.Ordinal) &&
    optimizerSrc.Contains("forcing one more Display-Apply pass", StringComparison.Ordinal));
Expect("tray clear never registers logon task",
    !System.Text.RegularExpressions.Regex.IsMatch(
        File.ReadAllText(Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Exo-Nvidia-TrayClear.ps1")),
        @"(?<!Un)Register-ScheduledTask|schtasks\s+/Create",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase));
Expect("tray clear purges Exo tasks",
    File.ReadAllText(Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Exo-Nvidia-TrayClear.ps1"))
        .Contains("Unregister-ExoTrayTasks", StringComparison.Ordinal));
Expect("optimizer catch still saves failure state",
    optimizerSrc.Contains("if (-not [bool]$Script:CompletedPartialDisplayPolicy)", StringComparison.Ordinal) &&
    optimizerSrc.Contains("Save-ExoFailureState -Stage $failStage -Message $failMessage", StringComparison.Ordinal));
Expect("optimizer tray clear passes NoTask",
    optimizerSrc.Contains("'-NoTask', '-SettlePasses', '3'", StringComparison.Ordinal) &&
    optimizerSrc.Contains("(NoTask; no background task)", StringComparison.Ordinal));
Expect("display apply tray clear passes NoTask",
    displayApplySrc.Contains("-File $trayScript -NoTask -SettlePasses 3", StringComparison.Ordinal) &&
    displayApplySrc.Contains("Tray clear script finished (no background task)", StringComparison.Ordinal));
Expect("NVIDIA reset uses status-cleared copy",
    messagesSrc.Contains("NvidiaStatusCleared = \"Status cleared. Driver and profiles unchanged.\"", StringComparison.Ordinal) &&
    nvidiaViewModelSrc.Contains("OptimizerMessages.NvidiaStatusCleared", StringComparison.Ordinal));

Expect("NPI pinned tag v3.0.1.11+", optimizerSrc.Contains("NpiPinnedTag = 'v3.0.1.11'", StringComparison.Ordinal));
Expect("NPI pinned download URL",
    optimizerSrc.Contains("https://github.com/Orbmu2k/nvidiaProfileInspector/releases/download/v3.0.1.11/nvidiaProfileInspector.zip", StringComparison.Ordinal));
Expect("NPI pinned SHA-256 embedded",
    optimizerSrc.Contains("NpiPinnedZipSha256 = '68DB1640186DD6FD78B5F7949348808B9A542EE95E2A52810B2EEED026E80236'", StringComparison.Ordinal));
Expect("NPI version stamp kept", optimizerSrc.Contains("EXO-NPI-VERSION.txt", StringComparison.Ordinal));

// --- Stable PowerShell 7 host (repo-wide migration off 7 Preview) ---
var runScriptPath = Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Exo-Nvidia-Run.ps1");
var runScriptSrc = File.Exists(runScriptPath) ? File.ReadAllText(runScriptPath) : "";
Expect("optimizer host check accepts any pwsh 7.x", optimizerSrc.Contains("function Test-ExoIsPwsh7Host", StringComparison.Ordinal));
Expect("optimizer resolves stable pwsh first",
    optimizerSrc.Contains("function Get-ExoPwsh", StringComparison.Ordinal) &&
    optimizerSrc.Contains(@"'PowerShell\7\pwsh.exe'", StringComparison.Ordinal));
Expect("optimizer asserts pwsh 7 (not preview)", optimizerSrc.Contains("Assert-ExoPwsh7", StringComparison.Ordinal));
Expect("optimizer install hint is winget stable", optimizerSrc.Contains("winget install Microsoft.PowerShell", StringComparison.Ordinal));
Expect("optimizer does not hard-require preview host",
    !optimizerSrc.Contains("Assert-ExoPwshPreview", StringComparison.Ordinal) &&
    !optimizerSrc.Contains("requires PowerShell 7 Preview", StringComparison.OrdinalIgnoreCase));
Expect("run script does not hard-require preview host",
    !runScriptSrc.Contains("requires PowerShell 7 Preview", StringComparison.OrdinalIgnoreCase) &&
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
Expect("pack VERSION 1.12.0", packVersion == "1.12.0", packVersion);
Expect("PROFILE_VERSION 1.4.0", profileVersion == "1.4.0", profileVersion);
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
    Expect($"{packName}: background max frame rate pin (277041158=30)",
        basePins.TryGetValue("277041158", out var bg) && bg == "30", bg ?? "missing");
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

Log($"=== SUMMARY failed={failed} ===");
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
File.WriteAllLines(logPath, lines);
Console.WriteLine("Wrote " + logPath);
Environment.Exit(failed == 0 ? 0 : 1);

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
