using Exo.Services;

// Smoke tests drive shipped DiscordLogic + invoke shipped DiscordDetectCore.ps1 fixtures.
var logPath = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "discord-detect-tests.log");
var lines = new List<string>();
var failed = 0;

void Log(string s) { lines.Add(s); Console.WriteLine(s); }
void Expect(string name, bool cond, string detail = "")
{
    if (cond) Log($"PASS  {name}");
    else { failed++; Log($"FAIL  {name}" + (string.IsNullOrEmpty(detail) ? "" : " :: " + detail)); }
}

Log("=== Discord.Smoke (shipped DiscordLogic + DiscordDetectCore.ps1) ===");
Log(DateTime.UtcNow.ToString("o"));

// --- Kernel config: kit 4000 and prior 5000 both OK; folklore interval not ---
var kitCfg = """
[Settings]
EnableTrim=1
TrimIntervalMs=4000
PriorityClass=3
""";
var oldCfg = """
[Settings]
EnableTrim=1
TrimIntervalMs=5000
PriorityClass=3
""";
var badCfg = """
EnableTrim=0
TrimIntervalMs=5000
PriorityClass=3
""";
Expect("kit config 4000 valid", DiscordLogic.IsKernelConfigText(kitCfg));
Expect("prior config 5000 valid", DiscordLogic.IsKernelConfigText(oldCfg));
Expect("EnableTrim=0 rejected", !DiscordLogic.IsKernelConfigText(badCfg));
Expect("wrong PriorityClass fails",
    !DiscordLogic.IsKernelConfigText("EnableTrim=1\nTrimIntervalMs=4000\nPriorityClass=2\n"));

// Layout
Expect("kernel layout good", DiscordLogic.IsKernelLayout(24000, 2_000_000, 120000));
Expect("kernel layout stock ffmpeg fails", !DiscordLogic.IsKernelLayout(2_000_000, 2_000_000, 120000));

// Full kernel applied: hashes + config
Expect("kernel applied with kit cfg",
    DiscordLogic.IsKernelApplied(24000, 2_000_000, 120000, kitCfg, true, true));
Expect("kernel applied with 5000 cfg",
    DiscordLogic.IsKernelApplied(24000, 2_000_000, 120000, oldCfg, true, true));
Expect("kernel fails without proxy hash",
    !DiscordLogic.IsKernelApplied(24000, 2_000_000, 120000, kitCfg, false, true));
// Old false-fail: requiring exact TrimIntervalMs=5000 while kit is 4000
Expect("4000 is not rejected as non-5000", DiscordLogic.IsKernelConfigText(kitCfg));

// Equicord loader (legacy OpenAsar size classifier is intentionally removed)
var loader = "module.exports = require('C:\\\\Users\\\\x\\\\AppData\\\\Roaming\\\\Equicord\\\\equicord.asar');";
Expect("equicord loader text", DiscordLogic.IsEquicordLoaderText(loader, loader.Length));
Expect("empty loader fail", !DiscordLogic.IsEquicordLoaderText("", 0));
Expect("OpenAsar size classifier removed from DiscordLogic",
    typeof(DiscordLogic).GetMethod("IsOpenAsarSize") is null);

// Toasts: intentional off active; missing all → not applied; one enabled → fail
var toastOk = new Dictionary<string, int?>
{
    ["Discord"] = 0,
    ["Discord.Desktop"] = null,
};
Expect("toasts intentional off active", DiscordLogic.AreToastsOff(toastOk));
Expect("toasts none seen not applied",
    !DiscordLogic.AreToastsOff(new Dictionary<string, int?> { ["Discord"] = null }));
Expect("toasts one enabled fail",
    !DiscordLogic.AreToastsOff(new Dictionary<string, int?> { ["Discord"] = 1 }));

// Settings JSON — Exo Host only; legacy OpenAsar acceptance removed (negative)
Expect("legacy openasar quickstart NOT accepted",
    !DiscordLogic.IsQuickStartSettingsJson("""{"openasar":{"quickstart":true}}"""));
Expect("exo host skip + chromium",
    DiscordLogic.IsQuickStartSettingsJson(
        """{"SKIP_HOST_UPDATE":true,"chromiumSwitches":{"no-pings":1}}"""));
Expect("exo host skip + tti",
    DiscordLogic.IsQuickStartSettingsJson(
        """{"SKIP_HOST_UPDATE":true,"DESKTOP_TTI_DNSTCP_WARMUP":true}"""));
Expect("chromium without skip fails",
    !DiscordLogic.IsQuickStartSettingsJson("""{"chromiumSwitches":{"no-pings":1}}"""));
Expect("startup off",
    DiscordLogic.IsStartupOffSettingsJson("""{"OPEN_ON_STARTUP":false}"""));
Expect("startup on fail",
    !DiscordLogic.IsStartupOffSettingsJson("""{"OPEN_ON_STARTUP":true}"""));

// --- Voice QoS DSCP policy classifier ---
var qosGood = new Dictionary<string, string?>
{
    ["Version"] = "1.0",
    ["Application Name"] = "Discord.exe",
    ["Protocol"] = "UDP",
    ["Local Port"] = "*",
    ["Remote Port"] = "*",
    ["Local IP"] = "*",
    ["Remote IP"] = "*",
    ["DSCP Value"] = "46",
    ["Throttle Rate"] = "-1",
};
Expect("qos policy good map", DiscordLogic.IsQosPolicyMap(qosGood, "Discord.exe"));
Expect("qos policy exe mismatch fails", !DiscordLogic.IsQosPolicyMap(qosGood, "DiscordPTB.exe"));
var qosWrongDscp = new Dictionary<string, string?>(qosGood) { ["DSCP Value"] = "0" };
Expect("qos policy wrong dscp fails", !DiscordLogic.IsQosPolicyMap(qosWrongDscp));
var qosNoApp = new Dictionary<string, string?>(qosGood);
qosNoApp.Remove("Application Name");
Expect("qos policy missing app fails", !DiscordLogic.IsQosPolicyMap(qosNoApp));
Expect("qos policy empty map fails", !DiscordLogic.IsQosPolicyMap(new Dictionary<string, string?>()));

// --- Variant (PTB/Canary) classifiers ---
Expect("variant definitions cover stable+ptb+canary",
    DiscordLogic.VariantDefinitions.Length == 3 &&
    DiscordLogic.VariantDefinitions.Any(v => v.LocalDir == "DiscordPTB" && v.Exe == "DiscordPTB.exe") &&
    DiscordLogic.VariantDefinitions.Any(v => v.LocalDir == "DiscordCanary" && v.AppDataDir == "discordcanary"));
Expect("variant settings good",
    DiscordLogic.IsVariantSettingsJson("""{"OPEN_ON_STARTUP":false,"chromiumSwitches":{"no-pings":1}}"""));
Expect("variant settings startup on fails",
    !DiscordLogic.IsVariantSettingsJson("""{"OPEN_ON_STARTUP":true,"chromiumSwitches":{"no-pings":1}}"""));
Expect("variant settings missing chromium fails",
    !DiscordLogic.IsVariantSettingsJson("""{"OPEN_ON_STARTUP":false}"""));
Expect("variant optimized all true", DiscordLogic.IsVariantOptimized(true, true, true));
Expect("variant optimized missing qos fails", !DiscordLogic.IsVariantOptimized(true, true, false));

// Path stability
var root = @"C:\Users\Erix\AppData\Local\Discord";
Expect("stable path under root",
    DiscordLogic.IsStableDiscordPathText(root + @"\Update.exe", root));
Expect("unrelated path not stable",
    !DiscordLogic.IsStableDiscordPathText(@"C:\Windows\System32\cmd.exe", root));

// --- Invoke shipped DiscordDetectCore.ps1 (not a reimplementation) ---
var repoRoot = FindRepoRoot();
var corePs1 = Path.Combine(repoRoot, "Exo", "Scripts", "Discord", "DiscordDetectCore.ps1");
Expect("DiscordDetectCore.ps1 exists", File.Exists(corePs1), corePs1);

if (File.Exists(corePs1))
{
    var debloatPs1 = Path.Combine(repoRoot, "Exo", "Scripts", "Discord", "kit", "lib", "40-DebloatWindows.ps1");
    var loggingPs1 = Path.Combine(repoRoot, "Exo", "Scripts", "Discord", "kit", "lib", "10-Logging.ps1");
    var fixtureDir = Path.Combine(Path.GetTempPath(), "exo-discord-smoke-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(fixtureDir);
    try
    {
        var psLog = Path.Combine(fixtureDir, "core-out.txt");
        var ps = $@"
. '{corePs1.Replace("'", "''")}'
. '{debloatPs1.Replace("'", "''")}'
. '{loggingPs1.Replace("'", "''")}'
$failed = 0
function E($n,$c) {{ if ($c) {{ 'PASS  ' + $n }} else {{ $script:failed++; 'FAIL  ' + $n }} }}
function Test-ScheduledTaskSweepSafe {{
    if (-not (Get-Command Get-ScheduledTask -ErrorAction SilentlyContinue)) {{ return $true }}
    try {{ $null = Get-StableDiscordTasks; return $true }} catch {{ return $false }}
}}
function Test-NullApplyReportSafe {{
    $Script:ExoApplyReport = $null
    try {{
        Add-ExoReport 'fixture-null' 'ok' | Out-Null
        $entries = @(Get-ExoReportEntries)
        return $entries.Count -eq 1 -and $entries[0] -eq 'fixture-null|ok'
    }} catch {{
        return $false
    }}
}}
@(
  (E 'ps scheduled-task action sweep survives strictmode' (Test-ScheduledTaskSweepSafe)),
  (E 'ps null apply report initializes safely' (Test-NullApplyReportSafe)),
  (E 'ps kit config' (Test-DiscOptKernelConfigText -ConfigText @'
EnableTrim=1
TrimIntervalMs=4000
PriorityClass=3
'@)),
  (E 'ps old 5000 config' (Test-DiscOptKernelConfigText -ConfigText @'
EnableTrim=1
TrimIntervalMs=5000
PriorityClass=3
'@)),
  (E 'ps bad trim off' (-not (Test-DiscOptKernelConfigText -ConfigText 'EnableTrim=0`nTrimIntervalMs=4000`nPriorityClass=3'))),
  (E 'ps openasar classifier removed' (-not (Get-Command Test-DiscOptOpenAsarSize -ErrorAction SilentlyContinue))),
  (E 'ps legacy openasar quickstart not accepted' (-not (Test-DiscOptQuickStartFromSettingsJson -JsonText '{{""openasar"":{{""quickstart"":true}}}}'))),
  (E 'ps exo host quickstart accepted' (Test-DiscOptQuickStartFromSettingsJson -JsonText '{{""SKIP_HOST_UPDATE"":true,""chromiumSwitches"":{{""no-pings"":1}}}}')),
  (E 'ps qos map good' (Test-DiscOptQosPolicyMap -Map @{{ 'Version' = '1.0'; 'Application Name' = 'Discord.exe'; 'Protocol' = 'UDP'; 'DSCP Value' = '46'; 'Throttle Rate' = '-1' }} -ExpectedExe 'Discord.exe')),
  (E 'ps qos map wrong dscp' (-not (Test-DiscOptQosPolicyMap -Map @{{ 'Version' = '1.0'; 'Application Name' = 'Discord.exe'; 'Protocol' = 'UDP'; 'DSCP Value' = '0'; 'Throttle Rate' = '-1' }}))),
  (E 'ps qos map exe mismatch' (-not (Test-DiscOptQosPolicyMap -Map @{{ 'Version' = '1.0'; 'Application Name' = 'Discord.exe'; 'Protocol' = 'UDP'; 'DSCP Value' = '46'; 'Throttle Rate' = '-1' }} -ExpectedExe 'DiscordPTB.exe'))),
  (E 'ps variant defs 3' (@(Get-DiscOptVariantDefinitions).Count -eq 3)),
  (E 'ps variant defs ptb' (@(Get-DiscOptVariantDefinitions | Where-Object {{ $_.LocalDir -eq 'DiscordPTB' -and $_.Exe -eq 'DiscordPTB.exe' -and $_.QosPolicy -eq 'Exo Discord PTB Voice' }}).Count -eq 1)),
  (E 'ps variant settings good' (Test-DiscOptVariantSettingsJson -JsonText '{{""OPEN_ON_STARTUP"":false,""chromiumSwitches"":{{""no-pings"":1}}}}')),
  (E 'ps variant settings startup on' (-not (Test-DiscOptVariantSettingsJson -JsonText '{{""OPEN_ON_STARTUP"":true,""chromiumSwitches"":{{""no-pings"":1}}}}'))),
  (E 'ps variant optimized' (Test-DiscOptVariantOptimized -SettingsFlagsOk $true -AutostartQuiet $true -QosOk $true)),
  (E 'ps variant not optimized without qos' (-not (Test-DiscOptVariantOptimized -SettingsFlagsOk $true -AutostartQuiet $true -QosOk $false))),
  (E 'ps toast intentional' (Test-DiscOptToastsOffFromMap -Map @{{ Discord = 0; Other = $null }})),
  (E 'ps toast missing all' (-not (Test-DiscOptToastsOffFromMap -Map @{{ Discord = $null }}))),
  (E 'ps kernel applied' (Test-DiscOptKernelApplied -FfmpegProxyBytes 24000 -FfmpegRealBytes 2000000 -VersionDllBytes 120000 -ConfigText @'
EnableTrim=1
TrimIntervalMs=4000
PriorityClass=3
'@ -ProxyHashMatchesKit $true -VersionHashMatchesKit $true)),
  (E 'ps kernel no hash' (-not (Test-DiscOptKernelApplied -FfmpegProxyBytes 24000 -FfmpegRealBytes 2000000 -VersionDllBytes 120000 -ConfigText @'
EnableTrim=1
TrimIntervalMs=4000
PriorityClass=3
'@ -ProxyHashMatchesKit $false -VersionHashMatchesKit $true)))
) | ForEach-Object {{ $_ }}
'CORE_FAILED=' + $failed
";
        var scriptPath = Path.Combine(fixtureDir, "run-core.ps1");
        File.WriteAllText(scriptPath, ps);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(60000);
        File.WriteAllText(psLog, stdout);
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("PASS", StringComparison.Ordinal) || line.StartsWith("FAIL", StringComparison.Ordinal))
                Log("CORE  " + line);
            if (line.StartsWith("FAIL", StringComparison.Ordinal)) failed++;
        }
        Expect("DiscordDetectCore.ps1 exit 0", proc.ExitCode == 0, "exit=" + proc.ExitCode);
        Expect("DiscordDetectCore.ps1 CORE_FAILED=0",
            stdout.Contains("CORE_FAILED=0", StringComparison.Ordinal), stdout.Trim());
    }
    finally
    {
        try { Directory.Delete(fixtureDir, true); } catch { }
    }
}

// --- Apply path audit: concatenate shipped apply sources ---
var applyFiles = new[]
{
    Path.Combine(repoRoot, "Exo", "Scripts", "Discord", "Disc-Optimizer.ps1"),
    Path.Combine(repoRoot, "Exo", "Scripts", "Discord", "Exo-Discord-Run.ps1"),
    Path.Combine(repoRoot, "Exo", "Scripts", "Discord", "kit", "lib", "10-Logging.ps1"),
    Path.Combine(repoRoot, "Exo", "Scripts", "Discord", "kit", "lib", "40-DebloatWindows.ps1"),
    Path.Combine(repoRoot, "Exo", "Scripts", "Discord", "kit", "lib", "60-KernelBoot.ps1"),
};
var applyBlob = string.Join("\n", applyFiles.Where(File.Exists).Select(File.ReadAllText));
Expect("apply sources readable", applyBlob.Length > 1000);
var (auditOk, auditIssues) = DiscordLogic.AuditApplyScriptText(applyBlob);
Expect("apply audit", auditOk, string.Join("; ", auditIssues));
Expect("no Exo-Discord scheduled task create",
    applyBlob.IndexOf("Register-ScheduledTask -TaskName 'Exo-Discord", StringComparison.OrdinalIgnoreCase) < 0);
// Elevated Exo Apply must disarm kernel (cannot boot-check) so Discord opens.
Expect("elevated apply disarms kernel for launch safety",
    applyBlob.Contains("Disable-DiscOptKernelOnDisk", StringComparison.Ordinal) &&
    (applyBlob.Contains("Disarming DiscOpt kernel after elevated Apply", StringComparison.Ordinal) ||
     applyBlob.Contains("disarmed after user-token boot fail", StringComparison.Ordinal) ||
     applyBlob.Contains("Boot failed with DiscOpt kernel - disarming kernel", StringComparison.Ordinal) ||
     applyBlob.Contains("kernel disarmed for launch safety", StringComparison.Ordinal) ||
     applyBlob.Contains("elevated host: kernel disarmed", StringComparison.Ordinal) ||
     applyBlob.Contains("launch-safe (kernel off under elevated", StringComparison.Ordinal) ||
     applyBlob.Contains("kernel off under elevated", StringComparison.Ordinal) ||
     applyBlob.Contains("half-kernel disarmed", StringComparison.Ordinal) ||
     applyBlob.Contains("half-state (version.dll without valid ffmpeg proxy)", StringComparison.Ordinal)));
Expect("elevated host boot-check honest",
    applyBlob.Contains("Disable-DiscOptKernelOnDisk", StringComparison.Ordinal) &&
    (applyBlob.Contains("user-token boot", StringComparison.Ordinal) ||
     applyBlob.Contains("elevated host", StringComparison.Ordinal) ||
     applyBlob.Contains("Confirm-DiscordBootsAsUser", StringComparison.Ordinal)));
Expect("Install-DiscOptKernel present",
    applyBlob.Contains("Install-DiscOptKernel", StringComparison.Ordinal));
Expect("Apply-WindowsTweaks present",
    applyBlob.Contains("Apply-WindowsTweaks", StringComparison.Ordinal));
Expect("QoS apply present (Set-DiscordVoiceQosPolicies + DSCP 46)",
    applyBlob.Contains("Set-DiscordVoiceQosPolicies", StringComparison.Ordinal) &&
    applyBlob.Contains("'DSCP Value'; V = '46'", StringComparison.Ordinal));
Expect("variant quiet apply present",
    applyBlob.Contains("Set-DiscordVariantQuiet", StringComparison.Ordinal));
Expect("spellcheck debloat present (keeps en-US + system locale)",
    applyBlob.Contains("Remove-DiscordExtraSpellcheckDictionaries", StringComparison.Ordinal));
Expect("structured apply report emitted",
    applyBlob.Contains("EXO_REPORT:", StringComparison.Ordinal) &&
    applyBlob.Contains("applyReport", StringComparison.Ordinal));
var debloatText = File.ReadAllText(applyFiles[3]);
var kernelText = File.ReadAllText(applyFiles[4]);
Expect("Krisp CDN failure soft-skips",
    debloatText.Contains("Krisp module skipped", StringComparison.Ordinal) &&
    debloatText.Contains("Add-DiscordModuleSkipReport 'krisp'", StringComparison.Ordinal));
Expect("optional runtime module failure soft-skips",
    debloatText.Contains("optional runtime module", StringComparison.Ordinal) &&
    debloatText.Contains("Add-DiscordModuleSkipReport 'runtime-modules'", StringComparison.Ordinal) &&
    debloatText.Contains("if ($isBootCritical) { throw }", StringComparison.Ordinal));
Expect("kernel proxy fallback keeps stock ffmpeg",
    kernelText.Contains("Restore-DiscOptStockFfmpeg", StringComparison.Ordinal) &&
    kernelText.Contains("stock ffmpeg.dll kept", StringComparison.Ordinal) &&
    kernelText.Contains("DiscOptKernelProxyActive", StringComparison.Ordinal));
Expect("kernel install failure continues to boot safety",
    applyBlob.Contains("DiscOpt kernel install failed:", StringComparison.Ordinal) &&
    (applyBlob.Contains("Confirm-DiscordBootsAfterMods $app.FullName", StringComparison.Ordinal) ||
     applyBlob.Contains("Confirm-DiscordBootsAsUser $app.FullName", StringComparison.Ordinal)));
Expect("launch heal verifies boot after reinstall",
    applyBlob.Contains("Launch heal boot check failed", StringComparison.Ordinal) &&
    applyBlob.Contains("Kernel heal failed", StringComparison.Ordinal));

// Stable PowerShell 7 host (preview requirement removed; preview = last-resort probe only)
var discOptText = File.ReadAllText(applyFiles[0]);
Expect("stable pwsh host classifier present",
    discOptText.Contains("function Test-DiscOptIsPwsh7Host", StringComparison.Ordinal) &&
    discOptText.Contains("function Test-DiscOptIsPwsh7Path", StringComparison.Ordinal) &&
    discOptText.Contains("function Get-DiscOptPwsh7", StringComparison.Ordinal));
Expect("pwsh candidate order: stable Program Files first",
    discOptText.IndexOf(@"PowerShell\7\pwsh.exe", StringComparison.OrdinalIgnoreCase) >= 0 &&
    discOptText.IndexOf(@"PowerShell\7\pwsh.exe", StringComparison.OrdinalIgnoreCase) <
    discOptText.IndexOf(@"PowerShell\7-preview\pwsh.exe", StringComparison.OrdinalIgnoreCase));
Expect("portable fallback downloads STABLE release into Exo runtime",
    discOptText.Contains(@"Exo\runtime\PowerShell", StringComparison.Ordinal) &&
    discOptText.Contains("PowerShell-7*-win-x64.zip", StringComparison.Ordinal) &&
    discOptText.Contains("$release.prerelease) { continue }", StringComparison.Ordinal));
Expect("retired PowerShellPreview runtime dir not referenced",
    !applyBlob.Contains(@"runtime\PowerShellPreview", StringComparison.OrdinalIgnoreCase));
Expect("pwsh install hint uses stable winget id",
    applyBlob.Contains("winget install Microsoft.PowerShell", StringComparison.Ordinal) &&
    !applyBlob.Contains("Microsoft.PowerShell.Preview", StringComparison.Ordinal));
Expect("preview host requirement removed",
    !applyBlob.Contains("Test-DiscOptIsPwshPreviewHost", StringComparison.Ordinal) &&
    !applyBlob.Contains("requires PowerShell 7 Preview", StringComparison.Ordinal) &&
    !applyBlob.Contains("DISCOPT_PS7_PREVIEW", StringComparison.Ordinal));

// Repair must remove Exo QoS policies and variant flags
var repairPath = Path.Combine(repoRoot, "Exo", "Scripts", "Discord", "Exo-Discord-Repair.ps1");
var repairText = File.Exists(repairPath) ? File.ReadAllText(repairPath) : "";
Expect("repair removes Exo QoS policies",
    repairText.Contains("Remove-ExoDiscordQosPolicies", StringComparison.Ordinal));
Expect("repair restores variant settings",
    repairText.Contains("Restore-ExoDiscordVariantSettings", StringComparison.Ordinal));

// Detect script rows for QoS + variants; OpenAsar acceptance removed (negative)
var detectText = File.ReadAllText(Path.Combine(repoRoot, "Exo", "Scripts", "Discord", "Exo-Discord-Detect.ps1"));
Expect("detect has QoS row", detectText.Contains("Voice priority", StringComparison.Ordinal));
Expect("detect has variants row", detectText.Contains("Discord variants", StringComparison.Ordinal));
Expect("detect no legacy OpenAsar acceptance",
    !detectText.Contains("legacyOpenAsarOk", StringComparison.Ordinal) &&
    !detectText.Contains("Test-DiscOptOpenAsarSize", StringComparison.Ordinal));

// Fully-applied fixture: intentional quiet/kernel should score active
var fullToast = new Dictionary<string, int?> { ["Discord"] = 0, ["Discord.Desktop"] = 0 };
var fullKernel = DiscordLogic.IsKernelApplied(24000, 2_000_000, 120000, kitCfg, true, true);
var fullQuiet = DiscordLogic.AreToastsOff(fullToast) &&
                DiscordLogic.IsStartupOffSettingsJson("""{"OPEN_ON_STARTUP":false}""");
Expect("fully applied fixture kernel active", fullKernel);
Expect("fully applied fixture quiet active", fullQuiet);
Expect("fully applied fixture false_fail_count=0", fullKernel && fullQuiet);

// --- Client debloat pure classifiers (shipped DiscordLogic) ---
Expect("debloat clean all zero active",
    DiscordLogic.IsClientDebloatApplied(0, 0, 0, 0, false));
Expect("debloat empty optional payload count 0 active",
    DiscordLogic.IsClientDebloatApplied(0, 0, 0, 0, false));
Expect("debloat soft locale drift without state inactive",
    !DiscordLogic.IsClientDebloatApplied(0, 0, 0, 2, false));
Expect("debloat soft locale drift with state active",
    DiscordLogic.IsClientDebloatApplied(0, 0, 0, 2, true));
Expect("debloat soft sdk drift with state active",
    DiscordLogic.IsClientDebloatApplied(0, 0, 1, 0, true));
Expect("debloat hard leftover app never trusts state",
    !DiscordLogic.IsClientDebloatApplied(1, 0, 0, 0, true));
Expect("debloat hard optional payload never trusts state",
    !DiscordLogic.IsClientDebloatApplied(0, 1, 0, 0, true));
Expect("debloat hard+soft with state still inactive",
    !DiscordLogic.IsClientDebloatApplied(1, 0, 1, 0, true));

// Empty module dir has no payload
var emptyMod = Path.Combine(Path.GetTempPath(), "exo-empty-mod-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(emptyMod);
try
{
    Expect("empty module dir no payload", !DiscordLogic.ModuleDirHasPayload(emptyMod));
    File.WriteAllText(Path.Combine(emptyMod, "x.dll"), "x");
    Expect("module dir with file has payload", DiscordLogic.ModuleDirHasPayload(emptyMod));
}
finally
{
    try { Directory.Delete(emptyMod, true); } catch { }
}
Expect("missing module dir no payload", !DiscordLogic.ModuleDirHasPayload(null));
Expect("missing module path no payload",
    !DiscordLogic.ModuleDirHasPayload(Path.Combine(Path.GetTempPath(), "no-such-exo-mod")));

// --- Invoke shipped DiscordDetectCore debloat + fixture tree matching detect collection ---
if (File.Exists(corePs1))
{
    var debloatFixture = Path.Combine(Path.GetTempPath(), "exo-discord-debloat-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(debloatFixture);
    try
    {
        // Fixture: app tree with empty optional dirs, only en-US.pak, no game SDK, no leftover apps
        var appDir = Path.Combine(debloatFixture, "app-1.0.9000");
        var modPath = Path.Combine(appDir, "modules");
        var localePath = Path.Combine(appDir, "locales");
        Directory.CreateDirectory(Path.Combine(modPath, "discord_hook-1")); // empty
        Directory.CreateDirectory(Path.Combine(modPath, "discord_clips-1")); // empty
        Directory.CreateDirectory(Path.Combine(modPath, "discord_desktop_core-1"));
        Directory.CreateDirectory(localePath);
        File.WriteAllText(Path.Combine(localePath, "en-US.pak"), "locale");
        // Soft-drift file for recovery case
        var softLocale = Path.Combine(localePath, "fr.pak");

        var psDebloat = $@"
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. '{corePs1.Replace("'", "''")}'
$failed = 0
function E($n,$c) {{ if ($c) {{ 'PASS  ' + $n }} else {{ $script:failed++; 'FAIL  ' + $n }} }}

# Classic empty-array Count must not throw
$extraLocales = @()
if (Test-Path -LiteralPath '{localePath.Replace("'", "''")}') {{
  $extraLocales = @(Get-ChildItem -LiteralPath '{localePath.Replace("'", "''")}' -Filter '*.pak' -ErrorAction SilentlyContinue |
    Where-Object {{ $_.Name -ne 'en-US.pak' }})
}}
$extraCount = @($extraLocales).Count
@(
  (E 'ps empty extraLocales Count no throw' ($extraCount -eq 0)),
  (E 'ps empty optional hook no payload' (-not (Test-DiscOptModuleDirHasPayload -ModuleDir '{Path.Combine(modPath, "discord_hook-1").Replace("'", "''")}'))),
  (E 'ps empty optional clips no payload' (-not (Test-DiscOptModuleDirHasPayload -ModuleDir '{Path.Combine(modPath, "discord_clips-1").Replace("'", "''")}'))),
  (E 'ps clean fixture debloat active' (Test-DiscOptClientDebloat -LeftoverAppBuildCount 0 -OptionalModulePayloadCount 0 -GameSdkFileCount 0 -ExtraLocaleCount $extraCount -StateDebloatVerifiedSameApp:$false)),
  (E 'ps soft drift without state inactive' (-not (Test-DiscOptClientDebloat -LeftoverAppBuildCount 0 -OptionalModulePayloadCount 0 -GameSdkFileCount 0 -ExtraLocaleCount 1 -StateDebloatVerifiedSameApp:$false))),
  (E 'ps soft drift with state active' (Test-DiscOptClientDebloat -LeftoverAppBuildCount 0 -OptionalModulePayloadCount 0 -GameSdkFileCount 0 -ExtraLocaleCount 1 -StateDebloatVerifiedSameApp:$true)),
  (E 'ps hard leftover never trusts fullApply/state' (-not (Test-DiscOptClientDebloat -LeftoverAppBuildCount 1 -OptionalModulePayloadCount 0 -GameSdkFileCount 0 -ExtraLocaleCount 0 -StateDebloatVerifiedSameApp:$true))),
  (E 'ps hard optional payload never trusts state' (-not (Test-DiscOptClientDebloat -LeftoverAppBuildCount 0 -OptionalModulePayloadCount 1 -GameSdkFileCount 0 -ExtraLocaleCount 0 -StateDebloatVerifiedSameApp:$true)))
) | ForEach-Object {{ $_ }}

# Simulate detect collection path end-to-end on fixture (shipped helpers)
$oldApps = @()
$optionalPresent = @()
foreach ($name in @('discord_hook-1','discord_clips-1')) {{
  $p = Join-Path '{modPath.Replace("'", "''")}' $name
  if (Test-DiscOptModuleDirHasPayload -ModuleDir $p) {{ $optionalPresent += $name }}
}}
$gameSdk = @()
$debloatOk = Test-DiscOptClientDebloat `
  -LeftoverAppBuildCount (@($oldApps).Count) `
  -OptionalModulePayloadCount (@($optionalPresent).Count) `
  -GameSdkFileCount (@($gameSdk).Count) `
  -ExtraLocaleCount (@($extraLocales).Count) `
  -StateDebloatVerifiedSameApp:$false
E 'ps detect-path empty dirs + zero locales active' $debloatOk

# Soft drift: extra locale present + verified state → active
Set-Content -LiteralPath '{softLocale.Replace("'", "''")}' -Value 'x' -Encoding utf8
$extraLocales2 = @(Get-ChildItem -LiteralPath '{localePath.Replace("'", "''")}' -Filter '*.pak' -ErrorAction SilentlyContinue |
  Where-Object {{ $_.Name -ne 'en-US.pak' }})
$debloatSoft = Test-DiscOptClientDebloat `
  -LeftoverAppBuildCount 0 `
  -OptionalModulePayloadCount (@($optionalPresent).Count) `
  -GameSdkFileCount 0 `
  -ExtraLocaleCount (@($extraLocales2).Count) `
  -StateDebloatVerifiedSameApp:$true
E 'ps detect-path soft-drift + state active' $debloatSoft
'DEBLOAT_CORE_FAILED=' + $failed
";
        var debloatScript = Path.Combine(debloatFixture, "run-debloat.ps1");
        File.WriteAllText(debloatScript, psDebloat);
        var psi2 = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{debloatScript}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc2 = System.Diagnostics.Process.Start(psi2)!;
        var out2 = proc2.StandardOutput.ReadToEnd();
        var err2 = proc2.StandardError.ReadToEnd();
        proc2.WaitForExit(60000);
        foreach (var line in out2.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("PASS", StringComparison.Ordinal) || line.StartsWith("FAIL", StringComparison.Ordinal))
                Log("DEBLOAT  " + line);
            if (line.StartsWith("FAIL", StringComparison.Ordinal)) failed++;
        }
        if (!string.IsNullOrWhiteSpace(err2))
            Log("DEBLOAT_STDERR  " + err2.Trim());
        Expect("DiscordDetectCore debloat exit 0", proc2.ExitCode == 0, "exit=" + proc2.ExitCode + " " + err2);
        Expect("DiscordDetectCore DEBLOAT_CORE_FAILED=0",
            out2.Contains("DEBLOAT_CORE_FAILED=0", StringComparison.Ordinal), out2.Trim());
    }
    finally
    {
        try { Directory.Delete(debloatFixture, true); } catch { }
    }
}

// Live detect script: a feature row must be present (no Count throw skip).
// Without Discord installed (CI runners) detect takes the install branch,
// so assert the row that branch emits via the same Add-Feature path.
var detectPs1 = Path.Combine(repoRoot, "Exo", "Scripts", "Discord", "Exo-Discord-Detect.ps1");
Expect("Exo-Discord-Detect.ps1 exists", File.Exists(detectPs1), detectPs1);
if (File.Exists(detectPs1))
{
    var discordInstalled = Directory.Exists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord"));
    var expectedRow = discordInstalled ? "Complete client debloat" : "Discord install";
    var liveDir = Path.Combine(Path.GetTempPath(), "exo-discord-live-detect-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(liveDir);
    try
    {
        var timesWithDebloat = 0;
        for (var i = 0; i < 5; i++)
        {
            var psiLive = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{detectPs1}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var pl = System.Diagnostics.Process.Start(psiLive)!;
            var liveOut = pl.StandardOutput.ReadToEnd();
            var liveErr = pl.StandardError.ReadToEnd();
            pl.WaitForExit(90000);
            File.WriteAllText(Path.Combine(liveDir, $"detect-{i}.json.txt"), liveOut);
            if (!string.IsNullOrWhiteSpace(liveErr))
                File.WriteAllText(Path.Combine(liveDir, $"detect-{i}.err.txt"), liveErr);
            var hasDebloat = liveOut.Contains(expectedRow, StringComparison.OrdinalIgnoreCase);
            if (hasDebloat) timesWithDebloat++;
            Log($"LIVE_DETECT[{i}] exit={pl.ExitCode} hasDebloatRow={hasDebloat} len={liveOut.Length}");
        }
        Expect($"live detect '{expectedRow}' row present 5/5", timesWithDebloat == 5,
            $"present={timesWithDebloat}/5 (Count throw would skip Add-Feature)");
    }
    finally
    {
        // keep last logs under temp for SCRATCH copy; delete tree is optional
        try { /* leave liveDir for implementer copy */ } catch { }
        Log("LIVE_DETECT_DIR " + liveDir);
    }
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
        if (File.Exists(Path.Combine(dir.FullName, "Exo", "Scripts", "Discord", "DiscordDetectCore.ps1")))
            return dir.FullName;
        if (File.Exists(Path.Combine(dir.FullName, "VERSION")) &&
            Directory.Exists(Path.Combine(dir.FullName, "Exo", "Scripts", "Discord")))
            return dir.FullName;
        dir = dir.Parent;
    }
    // tools/Discord.Smoke/bin/Release/net8.0 → up to repo
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
