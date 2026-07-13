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

Log("=== UiPeak.Smoke (shipped UiStatusPresentation) ===");
Log(DateTime.UtcNow.ToString("o"));

var busy = UiStatusPresentation.FromFlags(isBusy: true, hasError: false, hasSuccess: false);
Expect("busy tone", busy == UiStatusPresentation.Tone.Busy);
Expect("busy glyph", UiStatusPresentation.GlyphFor(busy) == "\uE895");

var err = UiStatusPresentation.FromFlags(isBusy: false, hasError: true, hasSuccess: true);
Expect("error beats success", err == UiStatusPresentation.Tone.Error);
Expect("error brush key", UiStatusPresentation.BrushKeyFor(err) == "OptiErrorBrush");

var ok = UiStatusPresentation.FromFlags(isBusy: false, hasError: false, hasSuccess: true);
Expect("success tone", ok == UiStatusPresentation.Tone.Success);
Expect("success glyph checkmark", UiStatusPresentation.GlyphFor(ok) == "\uE73E");

var warn = UiStatusPresentation.FromFlags(isBusy: false, hasError: false, hasSuccess: false, isWarning: true);
Expect("warning tone", warn == UiStatusPresentation.Tone.Warning);

var neutral = UiStatusPresentation.FromFlags(false, false, false);
Expect("neutral tone", neutral == UiStatusPresentation.Tone.Neutral);

Expect("active feature opacity 1", Math.Abs(UiStatusPresentation.FeatureOpacity(true) - 1.0) < 0.001);
Expect("inactive feature dimmed", UiStatusPresentation.FeatureOpacity(false) < 1.0 && UiStatusPresentation.FeatureOpacity(false) >= 0.5);

var okBanner = UiStatusPresentation.BannerForSuccess(true);
Expect("banner success glyph", okBanner.Glyph == UiStatusPresentation.GlyphFor(UiStatusPresentation.Tone.Success));
Expect("banner success brush", okBanner.BrushKey == "OptiSuccessBrush");
var errBanner = UiStatusPresentation.BannerForSuccess(false);
Expect("banner error glyph", errBanner.Glyph == UiStatusPresentation.GlyphFor(UiStatusPresentation.Tone.Error));
Expect("banner error brush", errBanner.BrushKey == "OptiErrorBrush");
Expect("feature glyph active", UiStatusPresentation.FeatureGlyph(true) == "\uE73E");
Expect("feature glyph inactive", UiStatusPresentation.FeatureGlyph(false) == "\uE711");

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
    foreach (var key in new[] { "OptiPageTitle", "OptiFeatureTile", "OptiMessageBanner", "OptiFeatureDetail", "OptiPrimaryButton", "OptiPagePadding", "OptiNavButton" })
        Expect("theme has " + key, t.Contains(key, StringComparison.Ordinal));
}

if (File.Exists(appXaml))
{
    var a = File.ReadAllText(appXaml);
    Expect("theme has OptiMutedTextBrush", a.Contains("OptiMutedTextBrush", StringComparison.Ordinal));
    Expect("theme has OptiDividerBrush", a.Contains("OptiDividerBrush", StringComparison.Ordinal));
    Expect("soul indigo accent", a.Contains("#7C9CFF", StringComparison.Ordinal) || a.Contains("#005FB8", StringComparison.Ordinal));
    Expect("ink card surface", a.Contains("#1C1F2A", StringComparison.Ordinal) || a.Contains("#FFFFFF", StringComparison.Ordinal));
    Expect("not orange forge accent", !a.Contains("#F59E0B", StringComparison.Ordinal));
    Expect("not glass cyan accent", !a.Contains("#64D2FF", StringComparison.Ordinal));
}

foreach (var page in new[]
{
    "InternetOptimizerPage.xaml",
    "DiscordOptimizerPage.xaml",
    "SteamOptimizerPage.xaml",
    "NvidiaOptimizerPage.xaml",
    "NvidiaPanelPage.xaml",
    "DashboardPage.xaml",
    "SettingsPage.xaml"
})
{
    var p = Path.Combine(repo, "OptiHub", "Views", page);
    Expect(page + " exists", File.Exists(p));
    if (!File.Exists(p)) continue;
    var x = File.ReadAllText(p);
    if (page.Contains("Optimizer") || page == "NvidiaPanelPage.xaml")
    {
        Expect(page + " has primary CTA style", x.Contains("OptiPrimaryButton", StringComparison.Ordinal));
        Expect(page + " has feature tile", x.Contains("OptiFeatureTile", StringComparison.Ordinal));
        Expect(page + " has page title", x.Contains("OptiPageTitle", StringComparison.Ordinal));
        Expect(page + " has page padding", x.Contains("OptiPagePadding", StringComparison.Ordinal));
        Expect(page + " has message banner", x.Contains("OptiMessageBanner", StringComparison.Ordinal));
    }
}

foreach (var vm in new[]
{
    "InternetOptimizerViewModel.cs",
    "DiscordOptimizerViewModel.cs",
    "SteamOptimizerViewModel.cs",
    "NvidiaOptimizerViewModel.cs",
    "NvidiaPanelViewModel.cs"
})
{
    var p = Path.Combine(repo, "OptiHub", "ViewModels", vm);
    Expect(vm + " exists", File.Exists(p));
    if (!File.Exists(p)) continue;
    var src = File.ReadAllText(p);
    Expect(vm + " uses BannerForSuccess", src.Contains("BannerForSuccess", StringComparison.Ordinal));
    Expect(vm + " no hardcoded success glyph in Set*", !System.Text.RegularExpressions.Regex.IsMatch(
        src, @"MessageGlyph\s*=\s*success\s*\?\s*""\\uE73E""|LastResultGlyph\s*=\s*success\s*\?\s*""\\uE73E"""));
}

if (File.Exists(main))
{
    var m = File.ReadAllText(main);
    Expect("MainWindow has ContentFrame", m.Contains("ContentFrame", StringComparison.Ordinal));
    Expect("MainWindow NavigationView shell", m.Contains("NavigationView", StringComparison.Ordinal));
    Expect("MainWindow NavHome", m.Contains("NavHome", StringComparison.Ordinal));
    Expect("MainWindow settings via NavigationView", m.Contains("IsSettingsVisible", StringComparison.Ordinal));
    Expect("MainWindow no content transition block", !m.Contains("<Frame.ContentTransitions>", StringComparison.Ordinal));
}

if (File.Exists(mainCs))
{
    var cs = File.ReadAllText(mainCs);
    Expect("mica backdrop", cs.Contains("MicaBackdrop", StringComparison.Ordinal));
    Expect("resizable chrome method", cs.Contains("ApplyResizableWindowChrome", StringComparison.Ordinal));
    Expect("IsResizable true", cs.Contains("IsResizable = true", StringComparison.Ordinal));
    Expect("IsMaximizable true", cs.Contains("IsMaximizable = true", StringComparison.Ordinal));
    Expect("no fixed re-assert helper", !cs.Contains("ApplyFixedWindowChrome", StringComparison.Ordinal));
    Expect("no DisableMaximize hook", !cs.Contains("DisableMaximizeViaSystemMenu", StringComparison.Ordinal));
    Expect("no liquid glass acrylic primary", !cs.Contains("TryEnableLiquidGlassBackdrop", StringComparison.Ordinal));
}

var dash = Path.Combine(repo, "OptiHub", "Views", "DashboardPage.xaml");
if (File.Exists(dash))
{
    var d = File.ReadAllText(dash);
    Expect("dashboard modules", d.Contains("CardList", StringComparison.Ordinal));
    Expect("dashboard home title", d.Contains("Home", StringComparison.Ordinal));
    Expect("dashboard wrap grid", d.Contains("ItemsWrapGrid", StringComparison.Ordinal));
}

var settings = Path.Combine(repo, "OptiHub", "Views", "SettingsPage.xaml");
if (File.Exists(settings))
{
    var s = File.ReadAllText(settings);
    Expect("settings cards", s.Contains("OptiCard", StringComparison.Ordinal));
    Expect("settings Appearance", s.Contains("Appearance", StringComparison.Ordinal));
}

var nvProg = Path.Combine(repo, "tools", "OptiHub.NvDisplay", "Program.cs");
if (File.Exists(nvProg))
{
    var np = File.ReadAllText(nvProg);
    Expect("path clear uses Closest not Native for apply",
        np.Contains("GPUScanOutToClosest", StringComparison.Ordinal) &&
        np.Contains("ClearNativeUnscaledPath", StringComparison.Ordinal));
    Expect("no live SetDisplaysConfig to Native in apply path",
        !System.Text.RegularExpressions.Regex.IsMatch(np,
            @"Scaling\s*=\s*Scaling\.GPUScanOutToNative"));
}

var panelXaml = Path.Combine(repo, "OptiHub", "Views", "NvidiaPanelPage.xaml");
if (File.Exists(panelXaml))
{
    var px = File.ReadAllText(panelXaml);
    Expect("panel has Resolution", px.Contains("Resolution", StringComparison.Ordinal));
    Expect("panel has Refresh rate", px.Contains("Refresh rate", StringComparison.Ordinal));
    Expect("panel has Color depth", px.Contains("Color depth", StringComparison.Ordinal));
    Expect("panel has NVIDIA color", px.Contains("NVIDIA color", StringComparison.Ordinal));
    Expect("panel has Scaling", px.Contains("Scaling", StringComparison.Ordinal));
    Expect("panel has ComboBox", px.Contains("ComboBox", StringComparison.Ordinal));
}

Expect("NvidiaPanelLogic exists", File.Exists(Path.Combine(repo, "OptiHub", "Services", "NvidiaPanelLogic.cs")));

var panelVm = Path.Combine(repo, "OptiHub", "ViewModels", "NvidiaPanelViewModel.cs");
if (File.Exists(panelVm))
{
    var pvm = File.ReadAllText(panelVm);
    Expect("panel has RefreshCoreAsync", pvm.Contains("RefreshCoreAsync", StringComparison.Ordinal));
    Expect("panel force refresh after apply",
        pvm.Contains("RefreshCoreAsync(force: true", StringComparison.Ordinal));
    Expect("busy guard allows force",
        pvm.Contains("if (IsBusy && !force)", StringComparison.Ordinal) ||
        pvm.Contains("if (IsBusy && !force) return", StringComparison.Ordinal));
    Expect("no bare IsBusy return alone as only refresh path",
        !System.Text.RegularExpressions.Regex.IsMatch(pvm, @"public async Task RefreshAsync\(\)\s*\{\s*if \(IsBusy\) return;"));
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
        if (File.Exists(Path.Combine(dir.FullName, "OptiHub", "Helpers", "UiStatusPresentation.cs")))
            return dir.FullName;
        if (File.Exists(Path.Combine(dir.FullName, "VERSION")) && Directory.Exists(Path.Combine(dir.FullName, "OptiHub", "Views")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
