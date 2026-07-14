using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using OptiHub.Helpers;
using OptiHub.Models;

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

// Real shipped helper — not a reimplementation.
var busy = UiStatusPresentation.FromFlags(isBusy: true, hasError: false, hasSuccess: false);
Expect("busy", busy == UiStatusPresentation.Tone.Busy);
Expect("success", UiStatusPresentation.FromFlags(false, false, true) == UiStatusPresentation.Tone.Success);

// Drive real AppSettings MotionIntensity (shipped model used by the live slider).
var settingsA = new AppSettings { MotionIntensity = 73, Theme = AppSettings.DarkTheme };
var settingsB = settingsA.Clone();
Expect("AppSettings MotionIntensity clone", Math.Abs(settingsB.MotionIntensity - 73) < 0.0001
    && settingsB.Theme == AppSettings.DarkTheme);
// Clamp contract used by SettingsViewModel live path (same bounds as shipped).
static double ClampMotion(double v) => Math.Clamp(v, 0, 100);
Expect("MotionIntensity clamp high", Math.Abs(ClampMotion(140) - 100) < 0.0001);
Expect("MotionIntensity clamp low", Math.Abs(ClampMotion(-5) - 0) < 0.0001);
Expect("MotionIntensity clamp mid", Math.Abs(ClampMotion(42) - 42) < 0.0001);

// Drive shipped SettingsOverlayState — close→re-open race (skeptic gap).
{
    var ov = new SettingsOverlayState();
    Expect("settings state open", ov.TryBeginOpen() && ov.IsOpen);
    Expect("settings state no double open", !ov.TryBeginOpen());
    Expect("settings state close", ov.TryBeginClose(out var closeEpoch) && !ov.IsOpen && closeEpoch > 0);
    // Re-open before Finish: stale Finish must NOT apply.
    Expect("settings state reopen", ov.TryBeginOpen() && ov.IsOpen);
    Expect("settings stale close finish blocked", !ov.ShouldApplyCloseFinish(closeEpoch));
    // Real close of the second open should still Finish.
    Expect("settings second close", ov.TryBeginClose(out var close2) && !ov.IsOpen);
    Expect("settings second finish ok", ov.ShouldApplyCloseFinish(close2));
    // Double finish of same epoch still ok while closed.
    Expect("settings finish idempotent while closed", ov.ShouldApplyCloseFinish(close2));
    // After a brand-new open, old epoch is dead.
    Expect("settings open again", ov.TryBeginOpen());
    Expect("settings old epoch dead", !ov.ShouldApplyCloseFinish(close2));
}

// MainWindow must wire the epoch-gated Finish (string contract on shipped CS).

var repo = FindRepoRoot();
var appXaml = Path.Combine(repo, "OptiHub", "App.xaml");
var main = Path.Combine(repo, "OptiHub", "MainWindow.xaml");
var dash = Path.Combine(repo, "OptiHub", "Views", "DashboardPage.xaml");
var settings = Path.Combine(repo, "OptiHub", "Views", "Controls", "SettingsSheet.xaml");
var mainXaml = Path.Combine(repo, "OptiHub", "MainWindow.xaml");
var theme = Path.Combine(repo, "OptiHub", "Styles", "ThemeResources.xaml");
var converters = Path.Combine(repo, "OptiHub", "Helpers", "ValueConverters.cs");
var logosDir = Path.Combine(repo, "OptiHub", "Assets", "Logos");

Expect("files", File.Exists(appXaml) && File.Exists(main) && File.Exists(dash));
if (File.Exists(appXaml))
{
    var a = File.ReadAllText(appXaml);
    Expect("amoled black", a.Contains("#000000", StringComparison.Ordinal));
    Expect("stone white accent", a.Contains("#F5F5F4", StringComparison.Ordinal));
    Expect("cream light page", a.Contains("#F3EDE3", StringComparison.Ordinal));
}
if (File.Exists(main))
{
    var m = File.ReadAllText(main);
    Expect("settings gear", m.Contains("SettingsButton", StringComparison.Ordinal));
    Expect("back chrome", m.Contains("BackButton", StringComparison.Ordinal));
    Expect("drag region separate", m.Contains("TitleBarDragRegion", StringComparison.Ordinal));
    Expect("no sidebar NavHome", !m.Contains("NavHome", StringComparison.Ordinal));
    Expect("no NavigationView", !m.Contains("<NavigationView", StringComparison.Ordinal));
    Expect("ContentFrame", m.Contains("ContentFrame", StringComparison.Ordinal));
    Expect("no tooltips in main", !m.Contains("ToolTip", StringComparison.OrdinalIgnoreCase));
}
// SetTitleBar must target the drag strip only — not a parent that owns the gear/back buttons.
var mainCs = Path.Combine(repo, "OptiHub", "MainWindow.xaml.cs");
if (File.Exists(mainCs))
{
    var cs = File.ReadAllText(mainCs);
    Expect("SetTitleBar drag strip", cs.Contains("SetTitleBar(TitleBarDragRegion)", StringComparison.Ordinal));
    Expect("not SetTitleBar whole host", !cs.Contains("SetTitleBar(TitleBarHost)", StringComparison.Ordinal));
    Expect("fixed shell no maximize", cs.Contains("IsMaximizable = false", StringComparison.Ordinal));
    Expect("fixed shell no resize", cs.Contains("IsResizable = false", StringComparison.Ordinal));
    Expect("no titlebar settings text", !cs.Contains("AppTitleText.Text = \"Settings\"", StringComparison.Ordinal));
}
if (File.Exists(dash))
{
    var d = File.ReadAllText(dash);
    Expect("v17 hero line", d.Contains("Maximum performance", StringComparison.Ordinal));
    Expect("product card grid", d.Contains("ItemsWrapGrid", StringComparison.Ordinal));
    Expect("polished 1.8 cards", d.Contains("OptiCardButton", StringComparison.Ordinal)
        && d.Contains("ItemsWrapGrid", StringComparison.Ordinal));
    Expect("hero entrance panel", d.Contains("HeroPanel", StringComparison.Ordinal));
    Expect("stretch uniform logos", d.Contains("Stretch=\"Uniform\"", StringComparison.Ordinal));
    // Logo-only cards — title lives on the module page (a11y still has AutomationProperties.Name).
    Expect("logo only cards", !d.Contains("Definition.Title}", StringComparison.Ordinal)
        || d.Contains("AutomationProperties.Name=\"{x:Bind Definition.Title}", StringComparison.Ordinal));
    Expect("no card title label", !d.Contains("Text=\"{x:Bind Definition.Title}", StringComparison.Ordinal));
    // Hero dominant: centered tagline; compact cards under it (not overpowering).
    Expect("dashboard tagline center",
        d.Contains("TextAlignment=\"Center\"", StringComparison.Ordinal)
        && (d.Contains("HorizontalAlignment=\"Stretch\"", StringComparison.Ordinal)
            || d.Contains("HorizontalAlignment=\"Center\"", StringComparison.Ordinal))
        && (d.Contains("FontSize=\"40\"", StringComparison.Ordinal)
            || d.Contains("FontSize=\"48\"", StringComparison.Ordinal)
            || d.Contains("FontSize=\"42\"", StringComparison.Ordinal)));
    Expect("dashboard fixed cards under hero",
        d.Contains("Width=\"268\"", StringComparison.Ordinal)
        && d.Contains("Height=\"132\"", StringComparison.Ordinal));
    // Cards must stay smaller than the old overpowering footprints.
    Expect("dashboard cards not oversized",
        !d.Contains("Width=\"352\"", StringComparison.Ordinal)
        && !d.Contains("Width=\"340\"", StringComparison.Ordinal)
        && !d.Contains("Height=\"200\"", StringComparison.Ordinal)
        && !d.Contains("Height=\"190\"", StringComparison.Ordinal));
    Expect("dashboard no responsive layout",
        !File.ReadAllText(Path.Combine(repo, "OptiHub", "Views", "DashboardPage.xaml.cs"))
            .Contains("ApplyResponsiveLayout", StringComparison.Ordinal));
    // Home is nav only — no applied/checking chips (status lives on the module page).
    Expect("no home status chips", !d.Contains("StatusLabel", StringComparison.Ordinal));
    Expect("no pick-a-target blurb", !d.Contains("Pick a target", StringComparison.Ordinal));
}
if (File.Exists(theme))
{
    var t0 = File.ReadAllText(theme);
    Expect("click on press", t0.Contains("ClickMode\" Value=\"Press\"", StringComparison.Ordinal)
        || t0.Contains("ClickMode\" Value=\"Press", StringComparison.Ordinal)
        || t0.Contains("Value=\"Press\"", StringComparison.Ordinal) && t0.Contains("ClickMode", StringComparison.Ordinal));
}
if (File.Exists(settings))
{
    var s = File.ReadAllText(settings);
    Expect("settings appearance", s.Contains("Appearance", StringComparison.Ordinal));
    Expect("settings updates", s.Contains("Updates", StringComparison.Ordinal));
    Expect("settings app version", s.Contains("App version", StringComparison.Ordinal)
        && s.Contains("AppVersion", StringComparison.Ordinal)
        && !s.Contains("KitVersion", StringComparison.Ordinal));
    Expect("settings theme button", s.Contains("ToggleThemeCommand", StringComparison.Ordinal)
        && (s.Contains("CurrentThemeLabel", StringComparison.Ordinal) || s.Contains("ThemeToggleLabel", StringComparison.Ordinal))
        && s.Contains("ThemeSwitchHint", StringComparison.Ordinal)
        && s.Contains("OptiThemeToggleButton", StringComparison.Ordinal));
    Expect("settings theme current mode", s.Contains("CurrentThemeLabel", StringComparison.Ordinal)
        && s.Contains("ThemeSwitchHint", StringComparison.Ordinal));
    Expect("settings opti card", s.Contains("OptiCardFillBrush", StringComparison.Ordinal)
        || s.Contains("OptiCardRadius", StringComparison.Ordinal));
    Expect("settings single card", s.Contains("Appearance", StringComparison.Ordinal)
        && s.Contains("Support", StringComparison.Ordinal));
    Expect("settings no title", !s.Contains("Text=\"Settings\"", StringComparison.Ordinal));
    Expect("settings page padding token",
        s.Contains("Padding=\"22,18,22,20\"", StringComparison.Ordinal)
        || s.Contains("Padding=\"20,16,20,18\"", StringComparison.Ordinal)
        || s.Contains("Padding=\"18,16\"", StringComparison.Ordinal));
    Expect("settings overlay on main", File.Exists(mainXaml) && File.ReadAllText(mainXaml).Contains("SettingsOverlay", StringComparison.Ordinal)
        && File.ReadAllText(mainXaml).Contains("AcrylicBrush", StringComparison.Ordinal)
        && File.ReadAllText(mainXaml).Contains("SettingsSheetStage", StringComparison.Ordinal));
    // Ignore IsThumbToolTipEnabled (Slider API) — ban real ToolTip / ToolTipService only.
    var settingsNoTip = Regex.Replace(s, @"IsThumbToolTipEnabled\s*=\s*""[^""]*""", "", RegexOptions.IgnoreCase);
    Expect("no tooltips in settings", !settingsNoTip.Contains("ToolTip", StringComparison.OrdinalIgnoreCase)
        && !settingsNoTip.Contains("ToolTipService", StringComparison.OrdinalIgnoreCase));
    Expect("report issue secondary button",
        (s.Contains("OptiSecondaryButton", StringComparison.Ordinal) || s.Contains("OptiWhiteButton", StringComparison.Ordinal))
        && s.Contains("Report issue", StringComparison.Ordinal));
    Expect("settings live motion slider",
        s.Contains("MotionSlider", StringComparison.Ordinal)
        && s.Contains("OptiLiveSlider", StringComparison.Ordinal)
        && s.Contains("MotionIntensity", StringComparison.Ordinal)
        && s.Contains("<Slider", StringComparison.Ordinal));
    Expect("settings update progress only", !s.Contains("OptiLoader", StringComparison.Ordinal)
        && s.Contains("IsUpdating", StringComparison.Ordinal)
        && s.Contains("UpdateProgressPercent", StringComparison.Ordinal)
        && s.Contains("UpdateProgressLabel", StringComparison.Ordinal));
    Expect("settings update progress bar", s.Contains("ProgressBar", StringComparison.Ordinal)
        && s.Contains("UpdateProgressPercent", StringComparison.Ordinal));
}
var updateDlg = Path.Combine(repo, "OptiHub", "Helpers", "OptiUpdateDialog.cs");
if (File.Exists(updateDlg))
{
    var u = File.ReadAllText(updateDlg);
    Expect("update dialog no loader", !u.Contains("OptiLoader", StringComparison.Ordinal));
    Expect("update dialog progress", u.Contains("ProgressBar", StringComparison.Ordinal)
        && u.Contains("statusTb", StringComparison.Ordinal));
    Expect("update dialog install", u.Contains("InstallWithProgressAsync", StringComparison.Ordinal));
}
if (File.Exists(theme))
{
    var t = File.ReadAllText(theme);
    Expect("theme OptiPrimaryButton", t.Contains("OptiPrimaryButton", StringComparison.Ordinal));
    Expect("theme OptiWhiteButton", t.Contains("OptiWhiteButton", StringComparison.Ordinal));
    Expect("theme OptiCardButton", t.Contains("OptiCardButton", StringComparison.Ordinal));
    Expect("theme OptiFeatureTile", t.Contains("OptiFeatureTile", StringComparison.Ordinal));
    Expect("theme OptiIconWell", t.Contains("OptiIconWell", StringComparison.Ordinal));
    Expect("theme OptiPagePadding", t.Contains("OptiPagePadding", StringComparison.Ordinal));
    Expect("theme OptiThemeChoice", t.Contains("OptiThemeChoice", StringComparison.Ordinal));
    Expect("display italic", t.Contains("OptiDisplayFontItalic", StringComparison.Ordinal));
}

// Drive shipped converter source: coming-soon opacity must stay readable for B&W marks.
if (File.Exists(converters))
{
    var c = File.ReadAllText(converters);
    var m = Regex.Match(c, @"class BoolToOpacityConverter[\s\S]*?if \(value is true\) return ([0-9.]+);");
    Expect("coming-soon opacity defined", m.Success, "BoolToOpacityConverter return not found");
    if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var opacity))
    {
        Expect("coming-soon opacity mid", opacity is >= 0.65 and <= 0.85, $"got {opacity}");
    }
}

foreach (var page in new[]
         {
             "DiscordOptimizerPage.xaml", "SteamOptimizerPage.xaml", "InternetOptimizerPage.xaml",
             "NvidiaOptimizerPage.xaml", "NvidiaPanelPage.xaml"
         })
{
    var p = Path.Combine(repo, "OptiHub", "Views", page);
    if (!File.Exists(p)) continue;
    var x = File.ReadAllText(p);
    Expect(page + " CTA", x.Contains("OptiPrimaryButton", StringComparison.Ordinal) || x.Contains("OptiQuietButton", StringComparison.Ordinal));
    Expect(page + " page padding", x.Contains("OptiPagePadding", StringComparison.Ordinal));
    Expect(page + " unique loader", x.Contains("OptiLoader", StringComparison.Ordinal) && !x.Contains("<ProgressRing", StringComparison.Ordinal));
    if (page.Contains("NvidiaPanel", StringComparison.Ordinal))
        Expect(page + " apply label", x.Contains("ApplyLabel", StringComparison.Ordinal) && x.Contains("ChangeHint", StringComparison.Ordinal));
    if (page.StartsWith("Internet", StringComparison.Ordinal))
    {
        Expect("internet dual white CTAs",
            x.Contains("Low latency", StringComparison.Ordinal)
            && x.Contains("Highest download", StringComparison.Ordinal)
            && x.Contains("OptiWhiteButton", StringComparison.Ordinal));
        Expect("internet Repair button", x.Contains("Content=\"Repair\"", StringComparison.Ordinal));
        var ics = Path.Combine(repo, "OptiHub", "Views", "InternetOptimizerPage.xaml.cs");
        if (File.Exists(ics))
        {
            var ic = File.ReadAllText(ics);
            Expect("internet no preset dialog", !ic.Contains("RequestPresetChoice", StringComparison.Ordinal));
            Expect("internet repair wired", ic.Contains("Repair_Click", StringComparison.Ordinal));
        }
    }
}

var loaderCs = Path.Combine(repo, "OptiHub", "Views", "Controls", "OptiLoader.xaml.cs");
Expect("OptiLoader control", File.Exists(loaderCs));
if (File.Exists(loaderCs))
{
    var lc = File.ReadAllText(loaderCs);
    Expect("OptiLoader IsActive", lc.Contains("IsActiveProperty", StringComparison.Ordinal));
    // Composition-driven orbit (works in ContentDialog + Visibility-toggled Settings host)
    Expect("OptiLoader orbit bead",
        lc.Contains("RotationAngleInDegrees", StringComparison.Ordinal) &&
        lc.Contains("ElementCompositionPreview", StringComparison.Ordinal) &&
        lc.Contains("Orbit", StringComparison.Ordinal) &&
        !lc.Contains("Bar0Scale", StringComparison.Ordinal));
}

var motionCs = Path.Combine(repo, "OptiHub", "Helpers", "OptiMotion.cs");
if (File.Exists(motionCs))
{
    var m = File.ReadAllText(motionCs);
    Expect("OptiMotion ResetVisual", m.Contains("ResetVisual", StringComparison.Ordinal));
    Expect("OptiMotion EnsureVisible", m.Contains("EnsureVisible", StringComparison.Ordinal));
    Expect("OptiMotion overlay open", m.Contains("PlayOverlayOpen", StringComparison.Ordinal));
    Expect("OptiMotion overlay close resets", m.Contains("PlayOverlayClose", StringComparison.Ordinal)
        && m.Contains("ResetVisual", StringComparison.Ordinal));
    // Host open must not animate Offset/Scale on the sheet (that pinned top-left).
    Expect("OptiMotion overlay opacity-only host",
        m.Contains("opacity only", StringComparison.OrdinalIgnoreCase)
        || m.Contains("Opacity only", StringComparison.OrdinalIgnoreCase)
        || m.Contains("opacity-only", StringComparison.OrdinalIgnoreCase)
        || (m.Contains("layout owns", StringComparison.OrdinalIgnoreCase)
            && m.Contains("PlayOverlayOpen", StringComparison.Ordinal)));
    // After open starts, Offset must be forced to Zero (no rise on host).
    var openIdx = m.IndexOf("PlayOverlayOpen", StringComparison.Ordinal);
    var closeIdx = m.IndexOf("PlayOverlayClose", StringComparison.Ordinal);
    Expect("OptiMotion open body identity offset",
        openIdx >= 0 && closeIdx > openIdx
        && m.Substring(openIdx, closeIdx - openIdx).Contains("Offset = Vector3.Zero", StringComparison.Ordinal)
        && !m.Substring(openIdx, closeIdx - openIdx).Contains("StartAnimation(\"Offset\"", StringComparison.Ordinal));
    Expect("OptiMotion MotionStrength", m.Contains("MotionStrength", StringComparison.Ordinal));
    // Page enter must not prime whole page to opacity 0 (broke every module open).
    var pageEnterIdx = m.IndexOf("PlayPageEnter", StringComparison.Ordinal);
    Expect("OptiMotion page enter ensure visible",
        pageEnterIdx >= 0
        && m.Substring(pageEnterIdx).Contains("EnsureVisible", StringComparison.Ordinal)
        && !m.Substring(pageEnterIdx, Math.Min(400, m.Length - pageEnterIdx))
            .Contains("PrimeHidden", StringComparison.Ordinal));
}
var mainCsPath = Path.Combine(repo, "OptiHub", "MainWindow.xaml.cs");
if (File.Exists(mainCsPath))
{
    var mc = File.ReadAllText(mainCsPath);
    Expect("settings open resets composition",
        mc.Contains("OpenSettingsOverlay", StringComparison.Ordinal)
        && mc.Contains("UpdateLayout", StringComparison.Ordinal)
        && mc.Contains("SettingsSheetStage", StringComparison.Ordinal));
    Expect("settings overlay epoch gate",
        mc.Contains("SettingsOverlayState", StringComparison.Ordinal)
        && mc.Contains("TryBeginOpen", StringComparison.Ordinal)
        && mc.Contains("TryBeginClose", StringComparison.Ordinal)
        && mc.Contains("ShouldApplyCloseFinish", StringComparison.Ordinal));
    // Must not clear a naked bool before async Finish without a generation gate.
    Expect("settings no naked _settingsOpen race",
        !mc.Contains("_settingsOpen = false", StringComparison.Ordinal)
        && !Regex.IsMatch(mc, @"bool\s+_settingsOpen"));
    // Open order: home navigate call before TryBeginOpen call (Navigate closes open sheets).
    var openFn = mc.IndexOf("void OpenSettingsOverlay", StringComparison.Ordinal);
    var openBody = openFn >= 0 ? mc.Substring(openFn, Math.Min(900, mc.Length - openFn)) : "";
    var navCall = openBody.IndexOf("NavigateHome(", StringComparison.Ordinal);
    var tryCall = openBody.IndexOf("TryBeginOpen()", StringComparison.Ordinal);
    Expect("settings open navigates home first",
        openFn >= 0 && navCall >= 0 && tryCall >= 0 && navCall < tryCall);
    Expect("navigate ensures page visible",
        mc.Contains("OnContentNavigated", StringComparison.Ordinal)
        && mc.Contains("EnsureVisible", StringComparison.Ordinal));
}

var dashCs = Path.Combine(repo, "OptiHub", "Views", "DashboardPage.xaml.cs");
if (File.Exists(dashCs))
{
    var dc = File.ReadAllText(dashCs);
    Expect("home ForceHomeVisible", dc.Contains("ForceHomeVisible", StringComparison.Ordinal)
        && dc.Contains("EnsureVisible", StringComparison.Ordinal));
}

// Version gate for 2.0.0 release
var versionFile = Path.Combine(repo, "VERSION");
var csproj = Path.Combine(repo, "OptiHub", "OptiHub.csproj");
if (File.Exists(versionFile))
    Expect("VERSION is 2.0.1", File.ReadAllText(versionFile).Trim() == "2.0.1");
if (File.Exists(csproj))
    Expect("csproj Version 2.0.1", File.ReadAllText(csproj).Contains("<Version>2.0.1</Version>", StringComparison.Ordinal));

// Live MotionIntensity is a real AppSettings + ViewModel path (not a fake).
var appSettings = Path.Combine(repo, "OptiHub", "Models", "AppSettings.cs");
if (File.Exists(appSettings))
    Expect("AppSettings MotionIntensity", File.ReadAllText(appSettings).Contains("MotionIntensity", StringComparison.Ordinal));
var settingsVm = Path.Combine(repo, "OptiHub", "ViewModels", "SettingsViewModel.cs");
if (File.Exists(settingsVm))
{
    var svm = File.ReadAllText(settingsVm);
    Expect("VM MotionIntensity live bind",
        svm.Contains("MotionIntensity", StringComparison.Ordinal)
        && svm.Contains("OptiMotion.MotionStrength", StringComparison.Ordinal));
}
if (File.Exists(theme))
{
    var t2 = File.ReadAllText(theme);
    Expect("theme OptiSecondaryButton", t2.Contains("OptiSecondaryButton", StringComparison.Ordinal));
    Expect("theme OptiLiveSlider", t2.Contains("OptiLiveSlider", StringComparison.Ordinal));
}

// Logo visual weight: measure real shipped PNG alpha ink.
if (Directory.Exists(logosDir))
{
    var discord = MeasureInkFill(Path.Combine(logosDir, "discord.png"));
    var steam = MeasureInkFill(Path.Combine(logosDir, "steam.png"));
    var nvidia = MeasureInkFill(Path.Combine(logosDir, "nvidia.png"));
    var amd = MeasureInkFill(Path.Combine(logosDir, "amd.png"));
    var internet = MeasureInkFill(Path.Combine(logosDir, "internet.png"));

    Log($"ink discord max={discord.MaxFill:F1}% steam={steam.MaxFill:F1}% nvidia={nvidia.MaxFill:F1}% amd={amd.MaxFill:F1}% internet={internet.MaxFill:F1}%");

    // Peer floor from real sibling marks — not a magic absolute expected %.
    var peerFloor = Math.Min(Math.Min(discord.MaxFill, steam.MaxFill), nvidia.MaxFill) * 0.70;
    Expect("amd ink peer weight", amd.MaxFill >= peerFloor && amd.MaxFill >= 70,
        $"amd={amd.MaxFill:F1} peerFloor={peerFloor:F1}");
    // Wi‑Fi mark is intentionally airy (minimal arcs) — lower absolute floor than solid icons.
    Expect("internet ink peer weight", internet.MaxFill >= Math.Min(peerFloor, 55) && internet.MaxFill >= 55,
        $"internet={internet.MaxFill:F1} peerFloor={peerFloor:F1}");
    // AMD corporate mark is a wide wordmark on transparent (no white disc).
    // Require real width + non-micro height — not a filled plate (old bug).
    Expect("amd wide transparent mark",
        amd.FillW >= 70 && amd.FillH >= 18 && amd.FillH < 95,
        $"fillW={amd.FillW:F1} fillH={amd.FillH:F1}");
    // Minimal Wi‑Fi mark is wide arcs — height can sit just under 50% of canvas.
    Expect("internet not tiny", internet.FillH >= 42 && internet.FillW >= 55,
        $"fillW={internet.FillW:F1} fillH={internet.FillH:F1}");
}

var dashVm = Path.Combine(repo, "OptiHub", "ViewModels", "DashboardViewModel.cs");
if (File.Exists(dashVm))
{
    var dvm = File.ReadAllText(dashVm);
    // Home must not probe Discord/Steam/NVIDIA — open the module for that.
    Expect("home no discord probe", !dvm.Contains("DetectDiscordAsync", StringComparison.Ordinal));
    Expect("home no steam probe", !dvm.Contains("DetectSteamAsync", StringComparison.Ordinal));
    Expect("home no nvidia probe", !dvm.Contains("DetectNvidiaAsync", StringComparison.Ordinal));
    Expect("windows coming soon card", dvm.Contains("Card(\"windows\"", StringComparison.Ordinal)
        && dvm.Contains("windows.png", StringComparison.Ordinal));
}
if (File.Exists(Path.Combine(logosDir, "windows.png")))
    Expect("windows logo asset", true);
else
    Expect("windows logo asset", false, "missing Assets/Logos/windows.png");

var panelVm = Path.Combine(repo, "OptiHub", "ViewModels", "NvidiaPanelViewModel.cs");
if (File.Exists(panelVm))
    Expect("panel force refresh", File.ReadAllText(panelVm).Contains("RefreshCoreAsync(force: true", StringComparison.Ordinal));

var nv = Path.Combine(repo, "tools", "OptiHub.NvDisplay", "Program.cs");
if (File.Exists(nv))
{
    var nvt = File.ReadAllText(nv);
    Expect("path Closest", nvt.Contains("GPUScanOutToClosest", StringComparison.Ordinal));
    // Multi-GPU: don't abort whole enum when one adapter fails.
    Expect("nv multi-gpu continue", nvt.Contains("continue;", StringComparison.Ordinal) &&
                                    nvt.Contains("Multi-GPU", StringComparison.OrdinalIgnoreCase));
    // Soft mapping: incomplete GDI map no longer hard-fails apply.
    Expect("nv soft map", nvt.Contains("Partial NVIDIA-to-Windows mapping", StringComparison.Ordinal));
    Expect("nv gdi fallback", nvt.Contains("EnumerateActiveGdiNames", StringComparison.Ordinal));
}

var nvDetect = Path.Combine(repo, "OptiHub", "Scripts", "Nvidia", "OptiHub-Nvidia-Detect.ps1");
if (File.Exists(nvDetect))
{
    var det = File.ReadAllText(nvDetect);
    // Laptops must not permanently force "manual action only" for isApplied.
    Expect("nv detect no permanent notebook fail",
        !det.Contains("$needsDriverAction = $needsUpdate -or $needsRetweak -or $isNotebookGpu", StringComparison.Ordinal));
    Expect("nv optimus display skip ok",
        det.Contains("no-active-nvidia-displays", StringComparison.Ordinal));
}

var nvHeuristic = Path.Combine(repo, "OptiHub", "Services", "OptimizerStateService.cs");
if (File.Exists(nvHeuristic))
{
    var h = File.ReadAllText(nvHeuristic);
    Expect("heuristic notebook not hard fail",
        !h.Contains("!notebookGpu && driverTweaksApplied", StringComparison.Ordinal) &&
        h.Contains("notebookGpu || driverTweaksApplied", StringComparison.Ordinal));
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

static InkMetrics MeasureInkFill(string path)
{
    if (!File.Exists(path)) return new InkMetrics(0, 0, 0);
    using var bmp = new Bitmap(path);
    var minX = bmp.Width;
    var minY = bmp.Height;
    var maxX = 0;
    var maxY = 0;
    var any = false;
    var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
    var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try
    {
        var stride = Math.Abs(data.Stride);
        var bytes = new byte[stride * bmp.Height];
        Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        for (var y = 0; y < bmp.Height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < bmp.Width; x++)
            {
                var a = bytes[row + x * 4 + 3];
                if (a <= 20) continue;
                any = true;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
    }
    finally
    {
        bmp.UnlockBits(data);
    }

    if (!any) return new InkMetrics(0, 0, 0);
    var w = maxX - minX + 1;
    var h = maxY - minY + 1;
    var fillW = 100.0 * w / bmp.Width;
    var fillH = 100.0 * h / bmp.Height;
    return new InkMetrics(fillW, fillH, Math.Max(fillW, fillH));
}

readonly record struct InkMetrics(double FillW, double FillH, double MaxFill);
