using OptiHub.Services;

var logPath = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "steam-detect-tests.log");
var lines = new List<string>();
var failed = 0;
void Log(string s) { lines.Add(s); Console.WriteLine(s); }
void Expect(string name, bool cond, string detail = "")
{
    if (cond) Log($"PASS  {name}");
    else { failed++; Log($"FAIL  {name}" + (detail.Length > 0 ? " :: " + detail : "")); }
}

Log("=== SteamPeak.Smoke (shipped SteamPeakLogic + SteamDetectCore.ps1) ===");
Log(DateTime.UtcNow.ToString("o"));

var cefGood = """
@echo off
start "" /HIGH /D "C:\Steam" "C:\Steam\steam.exe" -cef-disable-gpu -nofriendsui -nointro %*
""";
var cefMissingHigh = """
start "" "C:\Steam\steam.exe" -cef-disable-gpu -nofriendsui -nointro
""";
Expect("CEF peak launcher", SteamPeakLogic.IsCefLauncherText(cefGood));
Expect("CEF missing /HIGH fails", !SteamPeakLogic.IsCefLauncherText(cefMissingHigh));

var trim5 = """
# OptiHub.SteamWebHelper
EmptyWorkingSet
[System.Diagnostics.ProcessPriorityClass]::High
[System.Diagnostics.ProcessPriorityClass]::BelowNormal
Start-Sleep -Seconds 5
""";
var trim4 = trim5.Replace("Seconds 5", "Seconds 4");
var trimMs = trim5.Replace("Start-Sleep -Seconds 5", "Start-Sleep -Milliseconds 4000");
var trimBad = trim5.Replace("Seconds 5", "Seconds 60");
Expect("trim 5s ok", SteamPeakLogic.IsTrimHelperText(trim5));
Expect("trim 4s ok (not hard-fail 5-only)", SteamPeakLogic.IsTrimHelperText(trim4));
Expect("trim 4000ms ok", SteamPeakLogic.IsTrimHelperText(trimMs));
Expect("trim 60s fail", !SteamPeakLogic.IsTrimHelperText(trimBad));
Expect("trim missing EmptyWorkingSet fail",
    !SteamPeakLogic.IsTrimHelperText(trim5.Replace("EmptyWorkingSet", "Nope")));

Expect("toasts intentional off",
    SteamPeakLogic.AreToastsOff(new Dictionary<string, int?> { ["Steam"] = 0, ["Other"] = null }));
Expect("toasts none not applied",
    !SteamPeakLogic.AreToastsOff(new Dictionary<string, int?> { ["Steam"] = null }));
Expect("legacy aggressive absent",
    SteamPeakLogic.LegacyAggressiveCmdNamesAbsent(new[] { "steam.exe", "Steam-OptiHub.cmd" }));
Expect("legacy aggressive present fail",
    !SteamPeakLogic.LegacyAggressiveCmdNamesAbsent(new[] { "Steam-OptiHub-Aggressive.cmd" }));

// Fully-applied fixture
var fullCef = SteamPeakLogic.IsCefLauncherText(cefGood);
var fullTrim = SteamPeakLogic.IsTrimHelperText(trim5);
var fullToast = SteamPeakLogic.AreToastsOff(new Dictionary<string, int?> { ["Steam"] = 0 });
Expect("fully applied fixture false_fail_count=0", fullCef && fullTrim && fullToast);

var repo = FindRepoRoot();
var core = Path.Combine(repo, "OptiHub", "Scripts", "Steam", "SteamDetectCore.ps1");
Expect("SteamDetectCore.ps1 exists", File.Exists(core), core);

if (File.Exists(core))
{
    var dir = Path.Combine(Path.GetTempPath(), "optihub-steam-peak-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var script = $@"
. '{core.Replace("'", "''")}'
$failed=0
function E($n,$c){{ if($c){{'PASS  '+$n}} else {{$script:failed++; 'FAIL  '+$n}} }}
@(
 (E 'ps cef' (Test-SteamCefLauncherText -Text 'start """" /HIGH steam.exe -cef-disable-gpu -nofriendsui -nointro')),
 (E 'ps trim 5' (Test-SteamTrimHelperText -Text @'
OptiHub.SteamWebHelper
EmptyWorkingSet
ProcessPriorityClass]::High
ProcessPriorityClass]::BelowNormal
Start-Sleep -Seconds 5
'@)),
 (E 'ps trim 4' (Test-SteamTrimHelperText -Text @'
OptiHub.SteamWebHelper
EmptyWorkingSet
ProcessPriorityClass]::High
ProcessPriorityClass]::BelowNormal
Start-Sleep -Seconds 4
'@)),
 (E 'ps toast' (Test-SteamToastsOffFromMap -Map @{{ Steam = 0 }})),
 (E 'ps toast missing' (-not (Test-SteamToastsOffFromMap -Map @{{ Steam = $null }})))
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
        Expect("SteamDetectCore CORE_FAILED=0", stdout.Contains("CORE_FAILED=0"), stdout.Trim());
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

var applyFiles = new[]
{
    Path.Combine(repo, "OptiHub", "Scripts", "Steam", "Steam-Optimizer.ps1"),
    Path.Combine(repo, "OptiHub", "Scripts", "Steam", "OptiHub-Steam-Run.ps1"),
};
var blob = string.Join("\n", applyFiles.Where(File.Exists).Select(File.ReadAllText));
Expect("apply sources readable", blob.Length > 1000);
var (ok, issues) = SteamPeakLogic.AuditApplyScriptText(blob);
Expect("apply audit", ok, string.Join("; ", issues));
Expect("no OptiHub-Steam scheduled task create",
    blob.IndexOf("Register-ScheduledTask -TaskName 'OptiHub-Steam", StringComparison.OrdinalIgnoreCase) < 0);

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
        if (File.Exists(Path.Combine(dir.FullName, "OptiHub", "Scripts", "Steam", "SteamDetectCore.ps1")))
            return dir.FullName;
        if (File.Exists(Path.Combine(dir.FullName, "VERSION")) &&
            Directory.Exists(Path.Combine(dir.FullName, "OptiHub", "Scripts", "Steam")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
