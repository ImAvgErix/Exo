using OptiHub.Helpers;

var logPath = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "ui-logic-tests.log");
var lines = new List<string>();
var failed = 0;
void Log(string s) { lines.Add(s); Console.WriteLine(s); }
void Expect(string name, bool cond, string detail = "")
{
    if (cond) Log($"PASS  {name}");
    else { failed++; Log($"FAIL  {name}" + (detail.Length > 0 ? " :: " + detail : "")); }
}

Log("=== UiPeak.Smoke ===");
var busy = UiStatusPresentation.FromFlags(isBusy: true, hasError: false, hasSuccess: false);
Expect("busy", busy == UiStatusPresentation.Tone.Busy);
Expect("success", UiStatusPresentation.FromFlags(false, false, true) == UiStatusPresentation.Tone.Success);

var repo = FindRepoRoot();
var appXaml = Path.Combine(repo, "OptiHub", "App.xaml");
var main = Path.Combine(repo, "OptiHub", "MainWindow.xaml");
var dash = Path.Combine(repo, "OptiHub", "Views", "DashboardPage.xaml");
var settings = Path.Combine(repo, "OptiHub", "Views", "SettingsPage.xaml");
var theme = Path.Combine(repo, "OptiHub", "Styles", "ThemeResources.xaml");

Expect("files", File.Exists(appXaml) && File.Exists(main) && File.Exists(dash));
if (File.Exists(appXaml))
{
    var a = File.ReadAllText(appXaml);
    Expect("amoled black", a.Contains("#000000", StringComparison.Ordinal));
    Expect("stone white accent", a.Contains("#F5F5F4", StringComparison.Ordinal));
}
if (File.Exists(main))
{
    var m = File.ReadAllText(main);
    Expect("settings gear top-left", m.Contains("SettingsButton", StringComparison.Ordinal));
    Expect("no sidebar NavHome", !m.Contains("NavHome", StringComparison.Ordinal));
    Expect("no NavigationView", !m.Contains("<NavigationView", StringComparison.Ordinal));
    Expect("ContentFrame", m.Contains("ContentFrame", StringComparison.Ordinal));
}
if (File.Exists(dash))
{
    var d = File.ReadAllText(dash);
    Expect("v17 hero line", d.Contains("Maximum performance", StringComparison.Ordinal));
    Expect("product card grid", d.Contains("ItemsWrapGrid", StringComparison.Ordinal));
}
if (File.Exists(settings))
{
    var s = File.ReadAllText(settings);
    Expect("settings appearance", s.Contains("APPEARANCE", StringComparison.Ordinal));
    Expect("settings updates", s.Contains("UPDATES", StringComparison.Ordinal));
}
if (File.Exists(theme))
{
    var t = File.ReadAllText(theme);
    Expect("theme OptiPrimaryButton", t.Contains("OptiPrimaryButton", StringComparison.Ordinal));
    Expect("theme OptiCardButton", t.Contains("OptiCardButton", StringComparison.Ordinal));
    Expect("display italic", t.Contains("OptiDisplayFontItalic", StringComparison.Ordinal));
}

foreach (var page in new[] { "DiscordOptimizerPage.xaml", "SteamOptimizerPage.xaml", "InternetOptimizerPage.xaml", "NvidiaOptimizerPage.xaml", "NvidiaPanelPage.xaml" })
{
    var p = Path.Combine(repo, "OptiHub", "Views", page);
    if (!File.Exists(p)) continue;
    var x = File.ReadAllText(p);
    Expect(page + " CTA", x.Contains("OptiPrimaryButton", StringComparison.Ordinal));
}

var panelVm = Path.Combine(repo, "OptiHub", "ViewModels", "NvidiaPanelViewModel.cs");
if (File.Exists(panelVm))
    Expect("panel force refresh", File.ReadAllText(panelVm).Contains("RefreshCoreAsync(force: true", StringComparison.Ordinal));

var nv = Path.Combine(repo, "tools", "OptiHub.NvDisplay", "Program.cs");
if (File.Exists(nv))
    Expect("path Closest", File.ReadAllText(nv).Contains("GPUScanOutToClosest", StringComparison.Ordinal));

Log($"=== SUMMARY failed={failed} ===");
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
File.WriteAllLines(logPath, lines);
Environment.Exit(failed == 0 ? 0 : 1);

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "VERSION")) && Directory.Exists(Path.Combine(dir.FullName, "OptiHub", "Views")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
