using OptiHub.Services;

// Smoke tests drive shipped DiscordPeakLogic + invoke shipped DiscordDetectCore.ps1 fixtures.
var logPath = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "discord-detect-tests.log");
var lines = new List<string>();
var failed = 0;

void Log(string s) { lines.Add(s); Console.WriteLine(s); }
void Expect(string name, bool cond, string detail = "")
{
    if (cond) Log($"PASS  {name}");
    else { failed++; Log($"FAIL  {name}" + (string.IsNullOrEmpty(detail) ? "" : " :: " + detail)); }
}

Log("=== DiscordPeak.Smoke (shipped DiscordPeakLogic + DiscordDetectCore.ps1) ===");
Log(DateTime.UtcNow.ToString("o"));

// --- Kernel config: kit 4000 and prior 5000 both OK; folklore interval not ---
var kitCfg = """
[Settings]
EnableTrim=1
TrimIntervalMs=4000
PriorityClass=3
""";
var oldPeakCfg = """
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
Expect("kit config 4000 peak-valid", DiscordPeakLogic.IsKernelConfigText(kitCfg));
Expect("prior config 5000 peak-valid", DiscordPeakLogic.IsKernelConfigText(oldPeakCfg));
Expect("EnableTrim=0 not peak", !DiscordPeakLogic.IsKernelConfigText(badCfg));
Expect("wrong PriorityClass fails",
    !DiscordPeakLogic.IsKernelConfigText("EnableTrim=1\nTrimIntervalMs=4000\nPriorityClass=2\n"));

// Layout
Expect("kernel layout good", DiscordPeakLogic.IsKernelLayout(24000, 2_000_000, 120000));
Expect("kernel layout stock ffmpeg fails", !DiscordPeakLogic.IsKernelLayout(2_000_000, 2_000_000, 120000));

// Full kernel applied: hashes + config
Expect("kernel applied with kit cfg",
    DiscordPeakLogic.IsKernelApplied(24000, 2_000_000, 120000, kitCfg, true, true));
Expect("kernel applied with 5000 cfg",
    DiscordPeakLogic.IsKernelApplied(24000, 2_000_000, 120000, oldPeakCfg, true, true));
Expect("kernel fails without proxy hash",
    !DiscordPeakLogic.IsKernelApplied(24000, 2_000_000, 120000, kitCfg, false, true));
// Old false-fail: requiring exact TrimIntervalMs=5000 while kit is 4000
Expect("4000 is not rejected as non-5000", DiscordPeakLogic.IsKernelConfigText(kitCfg));

// OpenAsar / Equicord loader
Expect("openasar size ok", DiscordPeakLogic.IsOpenAsarSize(41385));
Expect("openasar stock asar too big", !DiscordPeakLogic.IsOpenAsarSize(50_000_000));
var loader = "module.exports = require('C:\\\\Users\\\\x\\\\AppData\\\\Roaming\\\\Equicord\\\\equicord.asar');";
Expect("equicord loader text", DiscordPeakLogic.IsEquicordLoaderText(loader, loader.Length));
Expect("empty loader fail", !DiscordPeakLogic.IsEquicordLoaderText("", 0));

// Toasts: intentional off active; missing all → not applied; one enabled → fail
var toastOk = new Dictionary<string, int?>
{
    ["Discord"] = 0,
    ["Discord.Desktop"] = null,
};
Expect("toasts intentional off active", DiscordPeakLogic.AreToastsOff(toastOk));
Expect("toasts none seen not applied",
    !DiscordPeakLogic.AreToastsOff(new Dictionary<string, int?> { ["Discord"] = null }));
Expect("toasts one enabled fail",
    !DiscordPeakLogic.AreToastsOff(new Dictionary<string, int?> { ["Discord"] = 1 }));

// Settings JSON
Expect("quickstart true",
    DiscordPeakLogic.IsQuickStartSettingsJson("""{"openasar":{"quickstart":true}}"""));
Expect("startup off",
    DiscordPeakLogic.IsStartupOffSettingsJson("""{"OPEN_ON_STARTUP":false}"""));
Expect("startup on fail",
    !DiscordPeakLogic.IsStartupOffSettingsJson("""{"OPEN_ON_STARTUP":true}"""));

// Path stability
var root = @"C:\Users\Erix\AppData\Local\Discord";
Expect("stable path under root",
    DiscordPeakLogic.IsStableDiscordPathText(root + @"\Update.exe", root));
Expect("unrelated path not stable",
    !DiscordPeakLogic.IsStableDiscordPathText(@"C:\Windows\System32\cmd.exe", root));

// --- Invoke shipped DiscordDetectCore.ps1 (not a reimplementation) ---
var repoRoot = FindRepoRoot();
var corePs1 = Path.Combine(repoRoot, "OptiHub", "Scripts", "Discord", "DiscordDetectCore.ps1");
Expect("DiscordDetectCore.ps1 exists", File.Exists(corePs1), corePs1);

if (File.Exists(corePs1))
{
    var fixtureDir = Path.Combine(Path.GetTempPath(), "optihub-discord-peak-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(fixtureDir);
    try
    {
        var psLog = Path.Combine(fixtureDir, "core-out.txt");
        var ps = $@"
. '{corePs1.Replace("'", "''")}'
$failed = 0
function E($n,$c) {{ if ($c) {{ 'PASS  ' + $n }} else {{ $script:failed++; 'FAIL  ' + $n }} }}
@(
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
  (E 'ps openasar size' (Test-DiscOptOpenAsarSize -SizeBytes 41385)),
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
    Path.Combine(repoRoot, "OptiHub", "Scripts", "Discord", "Disc-Optimizer.ps1"),
    Path.Combine(repoRoot, "OptiHub", "Scripts", "Discord", "OptiHub-Discord-Run.ps1"),
    Path.Combine(repoRoot, "OptiHub", "Scripts", "Discord", "kit", "lib", "40-DebloatWindows.ps1"),
    Path.Combine(repoRoot, "OptiHub", "Scripts", "Discord", "kit", "lib", "60-KernelBoot.ps1"),
};
var applyBlob = string.Join("\n", applyFiles.Where(File.Exists).Select(File.ReadAllText));
Expect("apply sources readable", applyBlob.Length > 1000);
var (auditOk, auditIssues) = DiscordPeakLogic.AuditApplyScriptText(applyBlob);
Expect("apply audit", auditOk, string.Join("; ", auditIssues));
Expect("no OptiHub-Discord scheduled task create",
    applyBlob.IndexOf("Register-ScheduledTask -TaskName 'OptiHub-Discord", StringComparison.OrdinalIgnoreCase) < 0);
Expect("Install-DiscOptKernel present",
    applyBlob.Contains("Install-DiscOptKernel", StringComparison.Ordinal));
Expect("Apply-WindowsTweaks present",
    applyBlob.Contains("Apply-WindowsTweaks", StringComparison.Ordinal));

// Fully-applied fixture: intentional quiet/kernel should score active
var fullToast = new Dictionary<string, int?> { ["Discord"] = 0, ["Discord.Desktop"] = 0 };
var fullKernel = DiscordPeakLogic.IsKernelApplied(24000, 2_000_000, 120000, kitCfg, true, true);
var fullQuiet = DiscordPeakLogic.AreToastsOff(fullToast) &&
                DiscordPeakLogic.IsStartupOffSettingsJson("""{"OPEN_ON_STARTUP":false}""");
Expect("fully applied fixture kernel active", fullKernel);
Expect("fully applied fixture quiet active", fullQuiet);
Expect("fully applied fixture false_fail_count=0", fullKernel && fullQuiet);

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
        if (File.Exists(Path.Combine(dir.FullName, "OptiHub", "Scripts", "Discord", "DiscordDetectCore.ps1")))
            return dir.FullName;
        if (File.Exists(Path.Combine(dir.FullName, "VERSION")) &&
            Directory.Exists(Path.Combine(dir.FullName, "OptiHub", "Scripts", "Discord")))
            return dir.FullName;
        dir = dir.Parent;
    }
    // tools/DiscordPeak.Smoke/bin/Release/net8.0 → up to repo
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
