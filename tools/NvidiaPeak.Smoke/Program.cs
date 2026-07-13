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
var modes = new[] { "2560x1440@165", "2560x1440@144", "1920x1080@60", "1920x1080@144" };
var res = NvidiaPanelLogic.DistinctResolutions(modes);
Expect("distinct res largest first", res.Count >= 2 && res[0].StartsWith("2560", StringComparison.Ordinal));
var rates = NvidiaPanelLogic.RefreshRatesForResolution(modes, "2560x1440");
Expect("refresh rates for res", rates.Count == 2 && rates[0].Contains("165", StringComparison.Ordinal));

// Live helper when present (not a reimplementation)
var nvExe = Path.Combine(repo, "OptiHub", "Scripts", "Nvidia", "tools", "OptiHub.NvDisplay.exe");
if (!File.Exists(nvExe))
{
    // publish output path used by release
    var alt = Path.Combine(repo, "tools", "OptiHub.NvDisplay", "bin", "Release", "net8.0-windows", "win-x64", "OptiHub.NvDisplay.exe");
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
    Expect("list-displays JSON", so.Contains("OPTIHUB_NVDISPLAY_JSON:", StringComparison.Ordinal), so.Length > 0 ? so[^Math.Min(200, so.Length)..] : "empty");
    Expect("list-displays modes field", so.Contains("\"modes\"", StringComparison.Ordinal) || so.Contains("modes", StringComparison.Ordinal));
    Expect("list-displays ok", so.Contains("\"ok\":true", StringComparison.Ordinal) || so.Contains("\"ok\": true", StringComparison.Ordinal));
}
else
{
    Log("SKIP  live list-displays (helper exe missing — structural args covered)");
}

// Structural: helper Program exposes set-mode / set-scaling
var nvProg = Path.Combine(repo, "tools", "OptiHub.NvDisplay", "Program.cs");
if (File.Exists(nvProg))
{
    var src = File.ReadAllText(nvProg);
    Expect("helper has --list-displays", src.Contains("--list-displays", StringComparison.Ordinal));
    Expect("helper has --set-mode", src.Contains("--set-mode", StringComparison.Ordinal));
    Expect("helper has --set-scaling", src.Contains("--set-scaling", StringComparison.Ordinal));
    Expect("helper has --set-color-range", src.Contains("--set-color-range", StringComparison.Ordinal));
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
        if (File.Exists(Path.Combine(dir.FullName, "OptiHub", "Scripts", "Nvidia", "NvidiaDetectCore.ps1")))
            return dir.FullName;
        if (File.Exists(Path.Combine(dir.FullName, "VERSION")) &&
            Directory.Exists(Path.Combine(dir.FullName, "OptiHub", "Scripts", "Nvidia")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
