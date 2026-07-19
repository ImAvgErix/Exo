using System.Diagnostics;

var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var root = Path.Combine(repo, "Exo", "Scripts", "GameLaunchers");
var optimizerPath = Path.Combine(root, "GameLauncher-Optimizer.ps1");
var detectPath = Path.Combine(root, "GameLauncher-Detect.ps1");
var source = File.Exists(optimizerPath) ? File.ReadAllText(optimizerPath) : string.Empty;
var detect = File.Exists(detectPath) ? File.ReadAllText(detectPath) : string.Empty;
var failed = 0;
void Expect(string name, bool condition)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {name}");
    if (!condition) failed++;
}

Console.WriteLine("=== GameLaunchers.Smoke ===");
Expect("shared optimizer exists", source.Length > 10_000);
Expect("detect exists", detect.Contains("isApplied", StringComparison.Ordinal));
Expect("Riot and Epic parameter gate", source.Contains("ValidateSet('Riot','Epic')", StringComparison.Ordinal));
Expect("pristine snapshot precedes startup mutation",
    source.IndexOf("New-Snapshot $targets", StringComparison.Ordinal) is var snapshot && snapshot >= 0 &&
    source.IndexOf("Remove-StartupEntries", snapshot, StringComparison.Ordinal) > snapshot);
// Windows high-perf GPU + FSO are re-owned by Riot/Epic apply (user-requested).
Expect("applies high-perf GPU preference",
    source.Contains("GpuPreference=2;", StringComparison.Ordinal) &&
    source.Contains("Apply-WindowsGamePolicy", StringComparison.Ordinal));
Expect("applies FSO off for game exes",
    source.Contains("DISABLEDXMAXIMIZEDWINDOWEDMODE", StringComparison.Ordinal) &&
    source.Contains("fso", StringComparison.OrdinalIgnoreCase));
Expect("experimental forces Windows policy rewrite",
    source.Contains("-Force:$Experimental", StringComparison.Ordinal) ||
    source.Contains("Force:$Experimental", StringComparison.Ordinal));
Expect("no forced game scheduling priority",
    !source.Contains("CpuPriorityClass", StringComparison.Ordinal) &&
    !source.Contains("PerfOptions", StringComparison.Ordinal));
Expect("hybrid graphics capability flag only",
    source.Contains("Test-HybridGraphics", StringComparison.Ordinal));
Expect("Steam-parity shell quiet cross-connect",
    source.Contains("Apply-LauncherShellQuiet", StringComparison.Ordinal) &&
    source.Contains("shell-quiet", StringComparison.Ordinal));
Expect("detect verifies GPU and FSO",
    detect.Contains("High-perf GPU", StringComparison.Ordinal) &&
    detect.Contains("Fullscreen Optimizations", StringComparison.Ordinal));
Expect("Epic uses launcher manifests",
    source.Contains("EpicGamesLauncher\\Data\\Manifests", StringComparison.Ordinal) &&
    source.Contains("LaunchExecutable", StringComparison.Ordinal));
Expect("Riot detects VALORANT and League",
    source.Contains("VALORANT-Win64-Shipping.exe", StringComparison.Ordinal) &&
    source.Contains("League of Legends.exe", StringComparison.Ordinal));
// Anti-cheat brand names may appear only as exclusion filters / never-touch docs.
Expect("anti-cheat and services are never mutated",
    !source.Contains("Stop-Service", StringComparison.OrdinalIgnoreCase) &&
    !source.Contains("Set-Service", StringComparison.OrdinalIgnoreCase) &&
    !source.Contains("sc.exe", StringComparison.OrdinalIgnoreCase) &&
    source.Contains("anti-cheat-boundary", StringComparison.Ordinal));
Expect("no Exo background footprint",
    !source.Contains("Register-ScheduledTask", StringComparison.OrdinalIgnoreCase) &&
    !source.Contains("New-Service", StringComparison.OrdinalIgnoreCase));
// Repair still undoes legacy GPU values from older Exo builds.
Expect("repair restores every touched GPU value",
    source.Contains("Restore-Value $gpu", StringComparison.Ordinal));
Expect("snapshot removed only after successful restore",
    source.IndexOf("Add-Report 'restore' 'ok'", StringComparison.Ordinal) >
    source.IndexOf("Remove-Item -LiteralPath $SnapshotPath -Force -ErrorAction Stop", StringComparison.Ordinal));
Expect("apply report and durable state", source.Contains("EXO_REPORT:", StringComparison.Ordinal) &&
    source.Contains("applyReport = @($Report)", StringComparison.Ordinal));
foreach (var module in new[] { "Riot", "Epic" })
{
    foreach (var action in new[] { "Run", "Detect", "Repair" })
        Expect($"{module} {action} wrapper", File.Exists(Path.Combine(root, $"Exo-{module}-{action}.ps1")));
}

var pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
if (!File.Exists(pwsh)) pwsh = "pwsh";
foreach (var module in new[] { "Riot", "Epic" })
{
    var psi = new ProcessStartInfo(pwsh, $"-NoProfile -File \"{Path.Combine(root, $"Exo-{module}-Detect.ps1")}\"")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    using var process = Process.Start(psi)!;
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit(15_000);
    Expect($"{module} live detect JSON", process.ExitCode == 0 && output.Contains("\"features\"", StringComparison.Ordinal));
}

Console.WriteLine($"=== SUMMARY failed={failed} ===");
return failed == 0 ? 0 : 1;
