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
Log(DateTime.UtcNow.ToString("o"));

var busy = UiStatusPresentation.FromFlags(isBusy: true, hasError: false, hasSuccess: false);
Expect("busy tone", busy == UiStatusPresentation.Tone.Busy);
Expect("busy glyph", UiStatusPresentation.GlyphFor(busy) == "\uE895");
var err = UiStatusPresentation.FromFlags(isBusy: false, hasError: true, hasSuccess: true);
Expect("error beats success", err == UiStatusPresentation.Tone.Error);
var ok = UiStatusPresentation.FromFlags(isBusy: false, hasError: false, hasSuccess: true);
Expect("success tone", ok == UiStatusPresentation.Tone.Success);
Expect("success glyph", UiStatusPresentation.GlyphFor(ok) == "\uE73E");

var repo = FindRepoRoot();
var theme = Path.Combine(repo, "OptiHub", "Styles", "ThemeResources.xaml");
var appXaml = Path.Combine(repo, "OptiHub", "App.xaml");
var main = Path.Combine(repo, "OptiHub", "MainWindow.xaml");
var mainCs = Path.Combine(repo, "OptiHub", "MainWindow.xaml.cs");
Expect("ThemeResources exists", File.Exists(theme));
Expect("App.xaml exists", File.Exists(appXaml));
Expect("MainWindow exists", File.Exists(main));

if (File.Exists(theme))
{
    var t = File.ReadAllText(theme);
    foreach (var key in new[] { "OptiPageTitle", "OptiFeatureTile", "OptiMessageBanner", "OptiPrimaryButton", "OptiPagePadding", "OptiNavButton" })
        Expect("theme has " + key, t.Contains(key, StringComparison.Ordinal));
}

if (File.Exists(appXaml))
{
    var a = File.ReadAllText(appXaml);
    Expect("white signal accent", a.Contains("#F2F2F2", StringComparison.Ordinal));
    Expect("pure black canvas", a.Contains("OptiPageBackgroundBrush\" Color=\"#000000\"", StringComparison.Ordinal));
    Expect("not linear purple", !a.Contains("#5E6AD2", StringComparison.Ordinal));
    Expect("not orange forge", !a.Contains("#F59E0B", StringComparison.Ordinal));
}

foreach (var page in new[]
{
    "InternetOptimizerPage.xaml", "DiscordOptimizerPage.xaml", "SteamOptimizerPage.xaml",
    "NvidiaOptimizerPage.xaml", "NvidiaPanelPage.xaml", "DashboardPage.xaml", "SettingsPage.xaml"
})
{
    var p = Path.Combine(repo, "OptiHub", "Views", page);
    Expect(page + " exists", File.Exists(p));
    if (!File.Exists(p)) continue;
    var x = File.ReadAllText(p);
    if (page.Contains("Optimizer") || page == "NvidiaPanelPage.xaml")
    {
        Expect(page + " primary CTA", x.Contains("OptiPrimaryButton", StringComparison.Ordinal));
        Expect(page + " feature tile", x.Contains("OptiFeatureTile", StringComparison.Ordinal));
        Expect(page + " page title", x.Contains("OptiPageTitle", StringComparison.Ordinal));
        Expect(page + " page padding", x.Contains("OptiPagePadding", StringComparison.Ordinal));
        Expect(page + " message banner", x.Contains("OptiMessageBanner", StringComparison.Ordinal));
    }
}

foreach (var vm in new[]
{
    "InternetOptimizerViewModel.cs", "DiscordOptimizerViewModel.cs", "SteamOptimizerViewModel.cs",
    "NvidiaOptimizerViewModel.cs", "NvidiaPanelViewModel.cs"
})
{
    var p = Path.Combine(repo, "OptiHub", "ViewModels", vm);
    if (!File.Exists(p)) continue;
    var src = File.ReadAllText(p);
    Expect(vm + " BannerForSuccess", src.Contains("BannerForSuccess", StringComparison.Ordinal));
}

if (File.Exists(main))
{
    var m = File.ReadAllText(main);
    Expect("ContentFrame", m.Contains("ContentFrame", StringComparison.Ordinal));
    Expect("custom rail NavHome", m.Contains("NavHome", StringComparison.Ordinal));
    Expect("no WORKSPACE saas label", !m.Contains("WORKSPACE", StringComparison.Ordinal));
    Expect("no stock NavigationView", !m.Contains("<NavigationView", StringComparison.Ordinal));
    Expect("italic brand font", m.Contains("OptiDisplayFontItalic", StringComparison.Ordinal));
}

if (File.Exists(mainCs))
{
    var cs = File.ReadAllText(mainCs);
    Expect("resizable", cs.Contains("IsResizable = true", StringComparison.Ordinal));
    Expect("no-anim navigate", cs.Contains("SuppressNavigationTransitionInfo", StringComparison.Ordinal));
}

var dash = Path.Combine(repo, "OptiHub", "Views", "DashboardPage.xaml");
if (File.Exists(dash))
{
    var d = File.ReadAllText(dash);
    Expect("modules lanes", d.Contains("Modules", StringComparison.Ordinal));
    Expect("not command center", !d.Contains("Command center", StringComparison.Ordinal));
    Expect("vertical stack not saas grid", d.Contains("ItemsStackPanel", StringComparison.Ordinal));
}

var nvProg = Path.Combine(repo, "tools", "OptiHub.NvDisplay", "Program.cs");
if (File.Exists(nvProg))
{
    var np = File.ReadAllText(nvProg);
    Expect("path Closest", np.Contains("GPUScanOutToClosest", StringComparison.Ordinal));
    Expect("ClearNativeUnscaledPath", np.Contains("ClearNativeUnscaledPath", StringComparison.Ordinal));
}

var panelVm = Path.Combine(repo, "OptiHub", "ViewModels", "NvidiaPanelViewModel.cs");
if (File.Exists(panelVm))
{
    var pvm = File.ReadAllText(panelVm);
    Expect("force refresh", pvm.Contains("RefreshCoreAsync(force: true", StringComparison.Ordinal));
}

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
