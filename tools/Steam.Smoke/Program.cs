using Exo.Services;

var logPath = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "steam-detect-tests.log");
var lines = new List<string>();
var failed = 0;
void Log(string s) { lines.Add(s); Console.WriteLine(s); }
void Expect(string name, bool cond, string detail = "")
{
    if (cond) Log($"PASS  {name}");
    else { failed++; Log($"FAIL  {name}" + (detail.Length > 0 ? " :: " + detail : "")); }
}

Log("=== Steam.Smoke (shipped SteamLogic + SteamDetectCore.ps1) ===");
Log(DateTime.UtcNow.ToString("o"));

var cefGood = """
@echo off
start "" /HIGH /D "C:\Steam" "C:\Steam\steam.exe" -cef-disable-gpu -nofriendsui -nointro %*
""";
var cefMissingHigh = """
start "" "C:\Steam\steam.exe" -cef-disable-gpu -nofriendsui -nointro
""";
Expect("CEF launcher", SteamLogic.IsCefLauncherText(cefGood));
Expect("CEF missing /HIGH fails", !SteamLogic.IsCefLauncherText(cefMissingHigh));

var trim5 = """
# Exo.SteamWebHelper
EmptyWorkingSet
[System.Diagnostics.ProcessPriorityClass]::High
[System.Diagnostics.ProcessPriorityClass]::BelowNormal
Start-Sleep -Seconds 5
""";
var trim4 = trim5.Replace("Seconds 5", "Seconds 4");
var trimMs = trim5.Replace("Start-Sleep -Seconds 5", "Start-Sleep -Milliseconds 4000");
var trimBad = trim5.Replace("Seconds 5", "Seconds 60");
Expect("trim 5s ok", SteamLogic.IsTrimHelperText(trim5));
Expect("trim 4s ok (not hard-fail 5-only)", SteamLogic.IsTrimHelperText(trim4));
Expect("trim 4000ms ok", SteamLogic.IsTrimHelperText(trimMs));
Expect("trim 60s fail", !SteamLogic.IsTrimHelperText(trimBad));
Expect("trim missing EmptyWorkingSet fail",
    !SteamLogic.IsTrimHelperText(trim5.Replace("EmptyWorkingSet", "Nope")));

Expect("toasts intentional off",
    SteamLogic.AreToastsOff(new Dictionary<string, int?> { ["Steam"] = 0, ["Other"] = null }));
Expect("toasts none not applied",
    !SteamLogic.AreToastsOff(new Dictionary<string, int?> { ["Steam"] = null }));
Expect("legacy aggressive absent",
    SteamLogic.LegacyAggressiveCmdNamesAbsent(new[] { "steam.exe", "Steam-Exo.cmd" }));
Expect("legacy aggressive present fail",
    !SteamLogic.LegacyAggressiveCmdNamesAbsent(new[] { "Steam-Exo-Aggressive.cmd" }));

// Fully-applied fixture
var fullCef = SteamLogic.IsCefLauncherText(cefGood);
var fullTrim = SteamLogic.IsTrimHelperText(trim5);
var fullToast = SteamLogic.AreToastsOff(new Dictionary<string, int?> { ["Steam"] = 0 });
Expect("fully applied fixture false_fail_count=0", fullCef && fullTrim && fullToast);

var repo = FindRepoRoot();
var core = Path.Combine(repo, "Exo", "Scripts", "Steam", "SteamDetectCore.ps1");
Expect("SteamDetectCore.ps1 exists", File.Exists(core), core);

if (File.Exists(core))
{
    var dir = Path.Combine(Path.GetTempPath(), "exo-steam-smoke-" + Guid.NewGuid().ToString("N"));
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
Exo.SteamWebHelper
EmptyWorkingSet
ProcessPriorityClass]::High
ProcessPriorityClass]::BelowNormal
Start-Sleep -Seconds 5
'@)),
 (E 'ps trim 4' (Test-SteamTrimHelperText -Text @'
Exo.SteamWebHelper
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
    Path.Combine(repo, "Exo", "Scripts", "Steam", "Steam-Optimizer.ps1"),
    Path.Combine(repo, "Exo", "Scripts", "Steam", "Exo-Steam-Run.ps1"),
};
var blob = string.Join("\n", applyFiles.Where(File.Exists).Select(File.ReadAllText));
Expect("apply sources readable", blob.Length > 1000);
var (ok, issues) = SteamLogic.AuditApplyScriptText(blob);
Expect("apply audit", ok, string.Join("; ", issues));
Expect("no Exo-Steam scheduled task create",
    blob.IndexOf("Register-ScheduledTask -TaskName 'Exo-Steam", StringComparison.OrdinalIgnoreCase) < 0);

// --- Stable PowerShell 7 host (preview requirement removed) ---
var optimizerPath = applyFiles[0];
var optimizerText = File.Exists(optimizerPath) ? File.ReadAllText(optimizerPath) : "";
Expect("stable pwsh host classifier present",
    optimizerText.Contains("function Test-ExoIsPwsh7Host", StringComparison.Ordinal) &&
    optimizerText.Contains("function Get-ExoPwsh", StringComparison.Ordinal) &&
    optimizerText.Contains("function Assert-ExoPwsh7", StringComparison.Ordinal));
Expect("pwsh candidate order: stable Program Files first",
    IndexAfter(optimizerText, @"PowerShell\7\pwsh.exe", 0) >= 0 &&
    IndexAfter(optimizerText, @"PowerShell\7\pwsh.exe", 0) <
    IndexAfter(optimizerText, @"PowerShell\7-preview\pwsh.exe", 0));
Expect("pwsh 5.1 rejected",
    optimizerText.Contains("PSEdition -ne 'Core'", StringComparison.Ordinal) &&
    optimizerText.Contains("WindowsPowerShell", StringComparison.Ordinal));
Expect("pwsh install hint uses stable winget id",
    blob.Contains("winget install Microsoft.PowerShell", StringComparison.Ordinal) &&
    !blob.Contains("Microsoft.PowerShell.Preview", StringComparison.Ordinal));
Expect("preview host asserts removed",
    !blob.Contains("Assert-ExoPwshPreview", StringComparison.Ordinal) &&
    !blob.Contains("Test-ExoIsPwshPreviewHost", StringComparison.Ordinal));

// --- Trim stats proof (steam-trim-stats.json) ---
Expect("trim stats accumulation markers",
    optimizerText.Contains("steam-trim-stats.json", StringComparison.Ordinal) &&
    optimizerText.Contains("Read-TrimStats", StringComparison.Ordinal) &&
    optimizerText.Contains("Save-TrimStats", StringComparison.Ordinal) &&
    optimizerText.Contains("reclaimed", StringComparison.OrdinalIgnoreCase));

// --- EXO_REPORT structured apply report ---
Expect("EXO_REPORT emitter + state persistence",
    optimizerText.Contains("EXO_REPORT:", StringComparison.Ordinal) &&
    optimizerText.Contains("applyReport", StringComparison.Ordinal) &&
    optimizerText.Contains("Get-ExoReportEntries", StringComparison.Ordinal));
Expect("EXO_REPORT persisted on failed apply too",
    System.Text.RegularExpressions.Regex.IsMatch(optimizerText,
        @"applyStatus\s*=\s*'incomplete'[\s\S]{0,600}applyReport"));

// --- 3s messaging consistency (trim interval is 3 seconds everywhere) ---
var steamScriptFiles = Directory.GetFiles(Path.Combine(repo, "Exo", "Scripts", "Steam"))
    .Where(f => f.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
    .ToArray();
var staleFiveSecond = new List<string>();
foreach (var f in steamScriptFiles)
{
    var text = File.ReadAllText(f);
    if (System.Text.RegularExpressions.Regex.IsMatch(text, @"(?i)(?<![\d-])5s\b|every 5 seconds|5-second"))
        staleFiveSecond.Add(Path.GetFileName(f));
}
Expect("no stale 5s messaging in Steam scripts", staleFiveSecond.Count == 0,
    string.Join(", ", staleFiveSecond));
Expect("3s trim messaging present",
    optimizerText.Contains("3s", StringComparison.Ordinal) &&
    optimizerText.Contains("Start-Sleep -Seconds 3", StringComparison.Ordinal));

// --- Placeholder cleanup (dead Steam stub deleted) ---
Expect("Placeholders Steam stub deleted",
    !File.Exists(Path.Combine(repo, "Exo", "Scripts", "Placeholders", "Steam-Optimizer.ps1")));

// --- VDF injector: real fixture execution via pwsh ---
RunVdfInjectorFixtureTests(optimizerPath);

void RunVdfInjectorFixtureTests(string optimizer)
{
    var dir = Path.Combine(Path.GetTempPath(), "exo-steam-vdf-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        // Fixture mimics a modern localconfig.vdf that omits most target keys.
        var fixtureVdf = "\"UserLocalConfigStore\"\n{\n\t\"friends\"\n\t{\n\t\t\"PersonaStateDesired\"\t\t\"1\"\n\t}\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n\t\t\t\t\"SmoothScrollWebViews\"\t\t\"1\"\n\t\t\t}\n\t\t}\n\t}\n}\n";
        var fixturePath = Path.Combine(dir, "localconfig.vdf");
        File.WriteAllText(fixturePath, fixtureVdf);

        var testScript = """
$ErrorActionPreference = 'Stop'
$failed = 0
function E($n, $c) { if ($c) { 'PASS  ' + $n } else { $script:failed++; 'FAIL  ' + $n } }

# Extract the injector functions from the shipped optimizer via AST (the
# script runs main on load, so it can never be dot-sourced directly).
$tok = $null; $err = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile('__OPTIMIZER__', [ref]$tok, [ref]$err)
$wanted = @('Find-ExoVdfSection', 'Set-SteamVdfKeyAtPath', 'Set-SteamVdfKey', 'Test-SteamVdfExpectations')
$funcs = $ast.FindAll({ param($n)
    $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $wanted -contains $n.Name }, $true)
E 'injector functions found' ($funcs.Count -eq 4)
foreach ($f in $funcs) { . ([scriptblock]::Create($f.Extent.Text)) }

$path = '__FIXTURE__'
$orig = [IO.File]::ReadAllText($path)
$raw = $orig

# 1. Insert a missing key into an EXISTING nested section
$raw = Set-SteamVdfKeyAtPath $raw @('UserLocalConfigStore', 'friends') 'Notifications_ShowIngame' '0'
E 'insert into existing nested section' ($raw -match '"Notifications_ShowIngame"\t\t"0"')

# 2. Create a MISSING intermediate section and insert there
$raw = Set-SteamVdfKeyAtPath $raw @('UserLocalConfigStore', 'News') 'NotifyAvailableGames' '0'
E 'create missing section + insert' (($raw -match '"News"') -and ($raw -match '"NotifyAvailableGames"\t\t"0"'))

# 3. Insert a direct root-level key
$raw = Set-SteamVdfKeyAtPath $raw @('UserLocalConfigStore') 'LibraryLowBandwidthMode' '0'
E 'insert root-level key' ($raw -match '"LibraryLowBandwidthMode"\t\t"0"')

# 4. Rewrite a key that already exists anywhere (no duplicate insert)
$raw = Set-SteamVdfKeyAtPath $raw @('UserLocalConfigStore') 'SmoothScrollWebViews' '0'
E 'rewrite existing key in place' (
    ($raw -match '"SmoothScrollWebViews"\t\t"0"') -and
    (@([regex]::Matches($raw, '"SmoothScrollWebViews"')).Count -eq 1))

# 5. Structure stays balanced after all edits
$open = @([regex]::Matches($raw, '\{')).Count
$close = @([regex]::Matches($raw, '\}')).Count
E 'braces stay balanced' ($open -eq $close -and $open -ge 5)

# 6. Nested key stays inside its section (friends block still closes after it)
$friendsOpen = $raw.IndexOf('"friends"')
$keyPos = $raw.IndexOf('"Notifications_ShowIngame"')
$softwarePos = $raw.IndexOf('"Software"')
E 'inserted key lands inside friends section' ($friendsOpen -ge 0 -and $keyPos -gt $friendsOpen -and $keyPos -lt $softwarePos)

# 7. Backup-first write flow (same flow Set-SteamLocalConfigTweaks uses)
if ($raw -ne $orig) {
    $bak = $path + '.exo-bak'
    if (-not (Test-Path $bak)) { Copy-Item $path $bak -Force }
    [IO.File]::WriteAllText($path, $raw, [Text.UTF8Encoding]::new($false))
}
E 'exo-bak written before patch' ((Test-Path ($path + '.exo-bak')) -and ([IO.File]::ReadAllText($path + '.exo-bak') -eq $orig))
E 'patched file persisted' ([IO.File]::ReadAllText($path) -match '"LibraryLowBandwidthMode"')

# 8. Verification helper flags present-but-wrong values
$check = Test-SteamVdfExpectations $raw @(@{ K = 'SmoothScrollWebViews'; V = '0' })
$checkBad = Test-SteamVdfExpectations $raw @(@{ K = 'SmoothScrollWebViews'; V = '1' })
E 'expectations valid on target value' ([bool]$check.Valid -and [int]$check.Observed -eq 1)
E 'expectations flag wrong value' (-not [bool]$checkBad.Valid)

# 9. Empty file: root section is created from scratch
$fresh = Set-SteamVdfKeyAtPath '' @('UserLocalConfigStore', 'system') 'EnableGameOverlay' '1'
E 'empty file gets root + nested + key' (
    ($fresh -match '"UserLocalConfigStore"') -and ($fresh -match '"system"') -and
    ($fresh -match '"EnableGameOverlay"\t\t"1"'))

'VDF_FAILED=' + $failed
""";
        testScript = testScript
            .Replace("__OPTIMIZER__", optimizer.Replace("'", "''"))
            .Replace("__FIXTURE__", fixturePath.Replace("'", "''"));
        var ps1 = Path.Combine(dir, "vdf-tests.ps1");
        File.WriteAllText(ps1, testScript);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{ps1}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(120000);
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("PASS") || line.StartsWith("FAIL"))
            {
                Log("VDF   " + line);
                if (line.StartsWith("FAIL")) failed++;
            }
        }
        Expect("VDF injector fixture VDF_FAILED=0", stdout.Contains("VDF_FAILED=0"),
            (stdout.Trim() + " " + stderr.Trim()).Trim());
    }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

static int IndexAfter(string text, string needle, int from) =>
    text.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);

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
        if (File.Exists(Path.Combine(dir.FullName, "Exo", "Scripts", "Steam", "SteamDetectCore.ps1")))
            return dir.FullName;
        if (File.Exists(Path.Combine(dir.FullName, "VERSION")) &&
            Directory.Exists(Path.Combine(dir.FullName, "Exo", "Scripts", "Steam")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
