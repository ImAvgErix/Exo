using OptiHub.Services;

var logPath = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "nvidia-detect-tests.log");
var lines = new List<string>();
var failed = 0;
void Log(string s) { lines.Add(s); Console.WriteLine(s); }
void Expect(string name, bool cond, string detail = "")
{
    if (cond) Log($"PASS  {name}");
    else { failed++; Log($"FAIL  {name}" + (detail.Length > 0 ? " :: " + detail : "")); }
}

Log("=== NvidiaPeak.Smoke (shipped NvidiaPeakLogic + NvidiaDetectCore.ps1) ===");
Log(DateTime.UtcNow.ToString("o"));

Expect("RTX 3070 series 30", NvidiaPeakLogic.GetGpuSeriesFromName("NVIDIA GeForce RTX 3070") == "30");
Expect("RTX 4070 series 40", NvidiaPeakLogic.GetGpuSeriesFromName("GeForce RTX 4070 SUPER") == "40");
Expect("GTX 1660 series 10", NvidiaPeakLogic.GetGpuSeriesFromName("NVIDIA GeForce GTX 1660 Ti") == "10");
Expect("notebook name", NvidiaPeakLogic.IsNotebookGpuName("GeForce RTX 4060 Laptop GPU"));
Expect("desktop not notebook", !NvidiaPeakLogic.IsNotebookGpuName("GeForce RTX 3070"));

Expect("max fps profile name",
    NvidiaPeakLogic.ExpectedProfileFileName("30", false) == "30 Series.nip");
Expect("gsync profile name",
    NvidiaPeakLogic.ExpectedProfileFileName("30", true) == "30 Series G-SYNC.nip");
Expect("profile name matches",
    NvidiaPeakLogic.ProfileNameMatchesSeries("30 Series.nip", "30", false));
Expect("profile name mismatch",
    !NvidiaPeakLogic.ProfileNameMatchesSeries("40 Series.nip", "30", false));

Expect("display container exe",
    NvidiaPeakLogic.IsDisplayContainerExe(@"C:\Windows\System32\DriverStore\...\NVDisplay.Container.exe"));
Expect("app tray exe",
    NvidiaPeakLogic.IsNvidiaAppTrayExe(@"C:\Program Files\NVIDIA Corporation\NVIDIA App\NVIDIA App.exe"));
Expect("display not app tray",
    !NvidiaPeakLogic.IsNvidiaAppTrayExe(@"...\NVDisplay.Container.exe"));

Expect("tray hidden IsPromoted=0", NvidiaPeakLogic.IsDisplayContainerTrayHidden(true, 0));
Expect("tray not hidden IsPromoted=1", !NvidiaPeakLogic.IsDisplayContainerTrayHidden(true, 1));
Expect("tray missing key ok", NvidiaPeakLogic.IsDisplayContainerTrayHidden(false, null));
Expect("app ghost gone", NvidiaPeakLogic.IsAppTrayGhostGone(false));
Expect("app ghost present fail", !NvidiaPeakLogic.IsAppTrayGhostGone(true));

// Display peak gate: orphan registry must not fail when color+scale live OK
Expect("display peak: refresh+registry",
    NvidiaPeakLogic.IsDisplayStatusPeakOk(true, true, false, false));
Expect("display peak: refresh+live color/scale without registry",
    NvidiaPeakLogic.IsDisplayStatusPeakOk(true, false, true, true));
Expect("display peak: no refresh fails",
    !NvidiaPeakLogic.IsDisplayStatusPeakOk(false, true, true, true));
Expect("display peak: no registry and no live fails",
    !NvidiaPeakLogic.IsDisplayStatusPeakOk(true, false, true, false));

Expect("sha256 hex", NvidiaPeakLogic.IsSha256Hex(new string('a', 64)));
Expect("sha256 bad", !NvidiaPeakLogic.IsSha256Hex("zz"));

// Fully applied fixture intentional actives
var full = NvidiaPeakLogic.IsDisplayStatusPeakOk(true, false, true, true) &&
           NvidiaPeakLogic.IsDisplayContainerTrayHidden(true, 0) &&
           NvidiaPeakLogic.ProfileNameMatchesSeries("30 Series.nip", "30", false) &&
           NvidiaPeakLogic.IsAppTrayGhostGone(false);
Expect("fully applied fixture false_fail_count=0", full);

var repo = FindRepoRoot();
var core = Path.Combine(repo, "OptiHub", "Scripts", "Nvidia", "NvidiaDetectCore.ps1");
Expect("NvidiaDetectCore.ps1 exists", File.Exists(core), core);

if (File.Exists(core))
{
    var dir = Path.Combine(Path.GetTempPath(), "optihub-nv-peak-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var script = $@"
. '{core.Replace("'", "''")}'
$failed=0
function E($n,$c){{ if($c){{'PASS  '+$n}} else {{$script:failed++; 'FAIL  '+$n}} }}
@(
 (E 'ps series 30' ((Get-OptiHubGpuSeriesFromName 'NVIDIA GeForce RTX 3070') -eq '30')),
 (E 'ps profile max' ((Get-OptiHubExpectedProfileFileName -SeriesId '40' -Gsync $false) -eq '40 Series.nip')),
 (E 'ps profile gsync' ((Get-OptiHubExpectedProfileFileName -SeriesId '40' -Gsync $true) -eq '40 Series G-SYNC.nip')),
 (E 'ps display peak orphan reg' (Test-OptiHubDisplayStatusPeakOk -RefreshOk $true -RegistryOk $false -ColorOk $true -PathScalingOk $true)),
 (E 'ps tray hidden' (Test-OptiHubDisplayTrayHidden -KeyExists $true -IsPromoted 0)),
 (E 'ps tray not hidden' (-not (Test-OptiHubDisplayTrayHidden -KeyExists $true -IsPromoted 1)))
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
    Path.Combine(repo, "OptiHub", "Scripts", "Nvidia", "Nvidia-Optimizer.ps1"),
    Path.Combine(repo, "OptiHub", "Scripts", "Nvidia", "OptiHub-Nvidia-TrayClear.ps1"),
    Path.Combine(repo, "OptiHub", "Scripts", "Nvidia", "OptiHub-Display-Apply.ps1"),
};
var blob = string.Join("\n", applyFiles.Where(File.Exists).Select(File.ReadAllText));
Expect("apply sources readable", blob.Length > 5000);
var (ok, issues) = NvidiaPeakLogic.AuditApplyScriptText(blob);
Expect("apply audit", ok, string.Join("; ", issues));
Expect("no tray logon task create",
    !System.Text.RegularExpressions.Regex.IsMatch(blob, @"Register-ScheduledTask[^\r\n]*OptiHub-Nvidia",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase));
Expect("Unregister tray tasks present",
    blob.Contains("Unregister-OptiHubTrayTasks", StringComparison.OrdinalIgnoreCase) ||
    blob.Contains("OptiHub-NvidiaTrayHide", StringComparison.OrdinalIgnoreCase));

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
        if (File.Exists(Path.Combine(dir.FullName, "OptiHub", "Scripts", "Nvidia", "NvidiaDetectCore.ps1")))
            return dir.FullName;
        if (File.Exists(Path.Combine(dir.FullName, "VERSION")) &&
            Directory.Exists(Path.Combine(dir.FullName, "OptiHub", "Scripts", "Nvidia")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
