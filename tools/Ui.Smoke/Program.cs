#if EXO_HAS_DRAWING
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
#endif
using System.Text.RegularExpressions;
using Exo.Helpers;
using Exo.Models;

var logPath = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "ui-logic-tests.log");
var lines = new List<string>();
var failed = 0;
void Log(string s) { lines.Add(s); Console.WriteLine(s); }
void Expect(string name, bool cond, string detail = "")
{
    if (cond) Log($"PASS  {name}");
    else { failed++; Log($"FAIL  {name}" + (detail.Length > 0 ? " :: " + detail : "")); }
}

Log("=== Ui.Smoke ===");

// Real shipped helper — not a reimplementation.
var busy = UiStatusPresentation.FromFlags(isBusy: true, hasError: false, hasSuccess: false);
Expect("busy", busy == UiStatusPresentation.Tone.Busy);
Expect("success", UiStatusPresentation.FromFlags(false, false, true) == UiStatusPresentation.Tone.Success);

// Drive real AppSettings clone path (theme + auto-update).
var settingsA = new AppSettings { Theme = AppSettings.DarkTheme, AutoUpdateScripts = true };
var settingsB = settingsA.Clone();
Expect("AppSettings clone theme", settingsB.Theme == AppSettings.DarkTheme && settingsB.AutoUpdateScripts);

var repo = FindRepoRoot();
var appXaml = Path.Combine(repo, "Exo", "App.xaml");
var main = Path.Combine(repo, "Exo", "MainWindow.xaml");
var dash = Path.Combine(repo, "Exo", "Views", "DashboardPage.xaml");
var settings = Path.Combine(repo, "Exo", "Views", "Controls", "SettingsSheet.xaml");
var mainXaml = Path.Combine(repo, "Exo", "MainWindow.xaml");
var theme = Path.Combine(repo, "Exo", "Styles", "ThemeResources.xaml");
var converters = Path.Combine(repo, "Exo", "Helpers", "ValueConverters.cs");
var logosDir = Path.Combine(repo, "Exo", "Assets", "Logos");

Expect("files", File.Exists(appXaml) && File.Exists(main) && File.Exists(dash));
if (File.Exists(appXaml))
{
    var a = File.ReadAllText(appXaml);
    Expect("amoled black", a.Contains("#000000", StringComparison.Ordinal));
    Expect("stone white accent", a.Contains("#F5F5F4", StringComparison.Ordinal));
    // Single cream everywhere: #F3EDE3 (matches ThemeService.SoftStone); old #F2EBE0 must stay gone.
    Expect("cream light page unified", a.Contains("#F3EDE3", StringComparison.Ordinal)
        && !a.Contains("#F2EBE0", StringComparison.Ordinal)
        && !a.Contains("#F3EBE3", StringComparison.Ordinal));
    // v2.6 Liquid Glass — translucent lifted fill (ARGB), not opaque #0C0C0C slab
    Expect("dark glass card lift",
        a.Contains("#B30C0C0C", StringComparison.Ordinal)
        || a.Contains("#0C0C0C", StringComparison.Ordinal));
    Expect("liquid glass fill token", a.Contains("ExoGlassFillBrush", StringComparison.Ordinal));
    // Solid near-opaque sheet brush — real AcrylicBrush at startup dies with
    // 0xC000027B (composition failure) on real GPUs; flyout content is parsed
    // with MainWindow, so the app never launched (v2.6.0 regression).
    Expect("settings sheet brush",
        a.Contains("ExoSettingsAcrylicBrush", StringComparison.Ordinal)
        && !a.Contains("<media:AcrylicBrush", StringComparison.Ordinal));
}
var themeServiceCs = Path.Combine(repo, "Exo", "Services", "ThemeService.cs");
if (File.Exists(themeServiceCs))
{
    var ts = File.ReadAllText(themeServiceCs);
    Expect("theme service cream matches app.xaml",
        ts.Contains("243, 237, 227", StringComparison.Ordinal)
        && ts.Contains("#F3EDE3", StringComparison.Ordinal));
}
if (File.Exists(main))
{
    var m = File.ReadAllText(main);
    // v2.6 top bar — liquid-glass circles floating on black (no bar plate).
    Expect("nav rail", m.Contains("NavRail", StringComparison.Ordinal));
    Expect("glass circle nav", m.Contains("ExoGlassCircle", StringComparison.Ordinal)
        && !m.Contains("ExoRailGlassFillBrush", StringComparison.Ordinal));
    Expect("top bar workspace", m.Contains("Padding=\"16\"", StringComparison.Ordinal));
    Expect("top bar row layout",
        m.Contains("RowDefinitions", StringComparison.Ordinal)
        && m.Contains("Orientation=\"Horizontal\"", StringComparison.Ordinal));
    Expect("top bar equal end caps",
        m.Contains("Width=\"56\"", StringComparison.Ordinal)
        && m.Contains("HomeEnd", StringComparison.Ordinal));
    Expect("rail nav home", m.Contains("NavHome", StringComparison.Ordinal));
    Expect("rail nav discord", m.Contains("NavDiscord", StringComparison.Ordinal));
    Expect("rail nav steam", m.Contains("NavSteam", StringComparison.Ordinal));
    Expect("rail nav internet", m.Contains("NavInternet", StringComparison.Ordinal));
    Expect("rail nav nvidia", m.Contains("NavNvidia", StringComparison.Ordinal));
    // v2.5.1 — product logos on the rail (not glyph-only).
    Expect("rail logo discord", m.Contains("discord.png", StringComparison.Ordinal));
    Expect("rail logo steam", m.Contains("steam.png", StringComparison.Ordinal));
    Expect("rail logo internet", m.Contains("internet.png", StringComparison.Ordinal));
    Expect("rail logo nvidia", m.Contains("nvidia.png", StringComparison.Ordinal));
    Expect("settings gear", m.Contains("SettingsButton", StringComparison.Ordinal));
    Expect("back chrome", m.Contains("BackButton", StringComparison.Ordinal));
    Expect("drag region separate", m.Contains("TitleBarDragRegion", StringComparison.Ordinal));
    Expect("no NavigationView", !m.Contains("<NavigationView", StringComparison.Ordinal));
    Expect("ContentFrame", m.Contains("ContentFrame", StringComparison.Ordinal));
    Expect("no tooltips in main", !m.Contains("ToolTip", StringComparison.OrdinalIgnoreCase));
}
// SetTitleBar targets the full NavRail (52px). WinUI still delivers pointer hits to
// interactive children (EXO / module circles / Settings); the old 8px drag strip
// made the fixed window feel undraggable.
var mainCs = Path.Combine(repo, "Exo", "MainWindow.xaml.cs");
if (File.Exists(mainCs))
{
    var cs = File.ReadAllText(mainCs);
    Expect("SetTitleBar NavRail drag", cs.Contains("SetTitleBar(NavRail)", StringComparison.Ordinal));
    Expect("not SetTitleBar whole host", !cs.Contains("SetTitleBar(TitleBarHost)", StringComparison.Ordinal));
    Expect("fixed shell no maximize", cs.Contains("IsMaximizable = false", StringComparison.Ordinal));
    Expect("fixed shell no resize", cs.Contains("IsResizable = false", StringComparison.Ordinal));
    Expect("rail selection helper", cs.Contains("UpdateRailSelection", StringComparison.Ordinal));
    // Settings gear lives on the rail — ApplyChrome must keep it visible on modules.
    Expect("settings always on rail",
        cs.Contains("SettingsButton.Visibility = Visibility.Visible", StringComparison.Ordinal)
        && !cs.Contains("SettingsButton.Visibility = Visibility.Collapsed", StringComparison.Ordinal));
    Expect("home hides exo control",
        cs.Contains("NavHome.Visibility = mode == ShellMode.Home", StringComparison.Ordinal));
    Expect("no titlebar settings text", !cs.Contains("AppTitleText.Text = \"Settings\"", StringComparison.Ordinal));
}
if (File.Exists(dash))
{
    var d = File.ReadAllText(dash);
    // v2.6 home dashboard — FPS/frame heroes + memory/latency/RAM/NVIDIA path.
    Expect("hero brand",
        d.Contains("HeroBrand", StringComparison.Ordinal)
        && (d.Contains("FontSize=\"72\"", StringComparison.Ordinal)
            || d.Contains("FontSize=\"64\"", StringComparison.Ordinal)
            || d.Contains("FontSize=\"56\"", StringComparison.Ordinal)
            || d.Contains("FontSize=\"40\"", StringComparison.Ordinal)
            || d.Contains("FontSize=\"36\"", StringComparison.Ordinal)));
    Expect("hero tagline",
        d.Contains("HeroTagline", StringComparison.Ordinal)
        && d.Contains("Maximum performance", StringComparison.Ordinal));
    Expect("home instrument plate", d.Contains("ExoModulePlate", StringComparison.Ordinal));
    Expect("home fps gain hero",
        d.Contains("FPS GAIN", StringComparison.Ordinal)
        && d.Contains("FpsPrimary", StringComparison.Ordinal));
    Expect("home frame time hero",
        d.Contains("FRAME TIME", StringComparison.Ordinal)
        && d.Contains("FrameTimePrimary", StringComparison.Ordinal));
    Expect("home latency tile",
        d.Contains("LATENCY", StringComparison.Ordinal)
        && d.Contains("LatencyPrimary", StringComparison.Ordinal));
    Expect("home ram reclaim tile",
        d.Contains("RAM RECLAIMED", StringComparison.Ordinal)
        && d.Contains("ReclaimedPrimary", StringComparison.Ordinal));
    Expect("home four-metric dashboard",
        d.Contains("FPS GAIN", StringComparison.Ordinal)
        && d.Contains("FRAME TIME", StringComparison.Ordinal)
        && d.Contains("RAM RECLAIMED", StringComparison.Ordinal)
        && d.Contains("LATENCY", StringComparison.Ordinal)
        && !d.Contains("MEMORY", StringComparison.Ordinal)
        && !d.Contains("FRAME PATH", StringComparison.Ordinal));
    Expect("no wrap grid cards", !d.Contains("ItemsWrapGrid", StringComparison.Ordinal));
    Expect("no fixed product cards",
        !d.Contains("Width=\"248\"", StringComparison.Ordinal)
        && !d.Contains("Width=\"250\"", StringComparison.Ordinal)
        && !d.Contains("Height=\"148\"", StringComparison.Ordinal));
    Expect("no logo tiles on home",
        !d.Contains("Assets/Logos", StringComparison.Ordinal)
        && !d.Contains("BladeStrip", StringComparison.Ordinal)
        && !d.Contains("LiveCards", StringComparison.Ordinal)
        && !d.Contains("CardList", StringComparison.Ordinal)
        && !d.Contains("ReadyModules", StringComparison.Ordinal));
    Expect("coming soon row",
        d.Contains("SoonCards", StringComparison.Ordinal)
        || d.Contains("Coming soon", StringComparison.Ordinal));
    Expect("hero tagline style", d.Contains("ExoTagline", StringComparison.Ordinal));
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
    Expect("settings appearance", s.Contains("APPEARANCE", StringComparison.Ordinal) || s.Contains("Appearance", StringComparison.Ordinal));
    Expect("settings updates", s.Contains("UPDATES", StringComparison.Ordinal) || s.Contains("Updates", StringComparison.Ordinal));
    Expect("settings app version", s.Contains("App version", StringComparison.Ordinal)
        && s.Contains("AppVersion", StringComparison.Ordinal)
        && !s.Contains("KitVersion", StringComparison.Ordinal));
    Expect("settings dark light buttons",
        s.Contains("DarkMode_Click", StringComparison.Ordinal)
        && s.Contains("LightMode_Click", StringComparison.Ordinal)
        && (s.Contains("Content=\"Dark\"", StringComparison.Ordinal)
            || s.Contains("Content=\"AMOLED\"", StringComparison.Ordinal))
        && s.Contains("Content=\"Light\"", StringComparison.Ordinal));
    Expect("settings theme choice selected state",
        s.Contains("ExoThemeChoice", StringComparison.Ordinal)
        && s.Contains("IsDarkMode", StringComparison.Ordinal)
        && s.Contains("IsLightMode", StringComparison.Ordinal));
    Expect("settings exo chrome",
        s.Contains("ExoQuietButton", StringComparison.Ordinal)
        && s.Contains("ExoPrimaryButton", StringComparison.Ordinal));
    Expect("settings no modal title", !s.Contains("Text=\"Settings\"", StringComparison.Ordinal));
    Expect("settings quiet support buttons",
        s.Contains("ExoQuietButton", StringComparison.Ordinal)
        && s.Contains("Report issue", StringComparison.Ordinal)
        && s.Contains("Open logs", StringComparison.Ordinal));
    Expect("settings no motion slider",
        !s.Contains("MotionSlider", StringComparison.Ordinal)
        && !s.Contains("MotionIntensity", StringComparison.Ordinal)
        && !s.Contains("<Slider", StringComparison.Ordinal));
    Expect("settings update progress only", !s.Contains("ExoLoader", StringComparison.Ordinal)
        && s.Contains("IsUpdating", StringComparison.Ordinal)
        && s.Contains("UpdateProgressPercent", StringComparison.Ordinal)
        && s.Contains("UpdateProgressLabel", StringComparison.Ordinal));
    Expect("settings update progress bar", s.Contains("ProgressBar", StringComparison.Ordinal)
        && s.Contains("UpdateProgressPercent", StringComparison.Ordinal));
    Expect("no tooltips in settings", !s.Contains("ToolTip", StringComparison.OrdinalIgnoreCase)
        && !s.Contains("ToolTipService", StringComparison.OrdinalIgnoreCase));
    Expect("settings sheet open animation root",
        s.Contains("SheetRoot", StringComparison.Ordinal)
        && s.Contains("SheetTransform", StringComparison.Ordinal));
}
var settingsCs = Path.Combine(repo, "Exo", "Views", "Controls", "SettingsSheet.xaml.cs");
if (File.Exists(settingsCs))
{
    var sc = File.ReadAllText(settingsCs);
    Expect("settings play open animation",
        sc.Contains("PlayOpenAnimation", StringComparison.Ordinal)
        && sc.Contains("OpenMs", StringComparison.Ordinal)
        && sc.Contains("ResetOpenVisual", StringComparison.Ordinal));
    // Mirrored close (fade + rise) must exist and never strand a reopened sheet at opacity 0.
    Expect("settings play close animation",
        sc.Contains("PlayCloseAnimation", StringComparison.Ordinal)
        && sc.Contains("CloseMs", StringComparison.Ordinal)
        && sc.Contains("FinishClose", StringComparison.Ordinal));
}
// Settings is gear flyout (2.1.0 style).
if (File.Exists(mainXaml))
{
    var mx = File.ReadAllText(mainXaml);
    Expect("settings flyout on gear",
        mx.Contains("SettingsFlyout", StringComparison.Ordinal)
        && mx.Contains("SettingsSheetHost", StringComparison.Ordinal)
        && mx.Contains("SettingsGearRotate", StringComparison.Ordinal)
        && !mx.Contains("SettingsRail", StringComparison.Ordinal)
        && !mx.Contains("SettingsOverlay", StringComparison.Ordinal));
}
var updateDlg = Path.Combine(repo, "Exo", "Helpers", "ExoUpdateDialog.cs");
if (File.Exists(updateDlg))
{
    var u = File.ReadAllText(updateDlg);
    Expect("update dialog no loader", !u.Contains("ExoLoader", StringComparison.Ordinal));
    Expect("update dialog progress", u.Contains("ProgressBar", StringComparison.Ordinal)
        && u.Contains("statusTb", StringComparison.Ordinal));
    Expect("update dialog install", u.Contains("InstallWithProgressAsync", StringComparison.Ordinal));
}
if (File.Exists(theme))
{
    var t = File.ReadAllText(theme);
    Expect("theme ExoPrimaryButton", t.Contains("ExoPrimaryButton", StringComparison.Ordinal));
    Expect("theme ExoGlassCircle",
        t.Contains("ExoGlassCircle", StringComparison.Ordinal)
        && t.Contains("CornerRadius\" Value=\"22", StringComparison.Ordinal));
    Expect("theme ExoWhiteButton", t.Contains("ExoWhiteButton", StringComparison.Ordinal));
    Expect("theme ExoCardButton", t.Contains("ExoCardButton", StringComparison.Ordinal));
    Expect("theme ExoFeatureTile", t.Contains("ExoFeatureTile", StringComparison.Ordinal));
    Expect("theme ExoActionBar", t.Contains("ExoActionBar", StringComparison.Ordinal));
    Expect("theme compact message banners",
        t.Contains("ExoMessageText", StringComparison.Ordinal)
        && t.Contains("ExoInfoMessageText", StringComparison.Ordinal)
        && t.Contains("Property=\"Padding\" Value=\"10,6\"", StringComparison.Ordinal));
    Expect("theme ExoIconWell", t.Contains("ExoIconWell", StringComparison.Ordinal));
    Expect("theme ExoPagePadding", t.Contains("ExoPagePadding", StringComparison.Ordinal));
    Expect("theme ExoThemeChoice", t.Contains("ExoThemeChoice", StringComparison.Ordinal));
    Expect("display italic", t.Contains("ExoDisplayFontItalic", StringComparison.Ordinal));
    // Opti* theme keys must stay gone (Exo* rename).
    Expect("theme no Opti keys",
        !t.Contains("OptiPrimaryButton", StringComparison.Ordinal)
        && !t.Contains("OptiFeatureTile", StringComparison.Ordinal)
        && !t.Contains("OptiPagePadding", StringComparison.Ordinal)
        && !t.Contains("OptiThemeChoice", StringComparison.Ordinal)
        && !t.Contains("OptiDisplayFontItalic", StringComparison.Ordinal)
        && !t.Contains("x:Key=\"Opti", StringComparison.Ordinal));
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
    var p = Path.Combine(repo, "Exo", "Views", page);
    if (!File.Exists(p)) continue;
    var x = File.ReadAllText(p);
    Expect(page + " CTA", x.Contains("ExoPrimaryButton", StringComparison.Ordinal) || x.Contains("ExoQuietButton", StringComparison.Ordinal));
    // Module chrome: instrument plate and/or legacy page width pad.
    Expect(page + " page padding",
        x.Contains("ExoModulePlate", StringComparison.Ordinal)
        || x.Contains("ExoPagePadding", StringComparison.Ordinal)
        || x.Contains("ExoPageMaxWidth", StringComparison.Ordinal));
    Expect(page + " unique loader", x.Contains("ExoLoader", StringComparison.Ordinal) && !x.Contains("<ProgressRing", StringComparison.Ordinal));
    Expect(page + " action bar", x.Contains("ExoActionBar", StringComparison.Ordinal));
    if (page.Contains("NvidiaPanel", StringComparison.Ordinal))
    {
        Expect(page + " apply label", x.Contains("ApplyLabel", StringComparison.Ordinal) && x.Contains("ChangeHint", StringComparison.Ordinal));
        // Digital vibrance row: per-display slider, hidden when the driver DVC API is unavailable.
        Expect(page + " vibrance slider",
            x.Contains("Digital vibrance", StringComparison.Ordinal)
            && x.Contains("SelectedVibrance", StringComparison.Ordinal)
            && x.Contains("VibranceSupported", StringComparison.Ordinal));
        Expect(page + " control panel fallback",
            x.Contains("Open NVIDIA Control Panel", StringComparison.Ordinal)
            && x.Contains("HasControlPanel", StringComparison.Ordinal));
    }
    if (page.StartsWith("Internet", StringComparison.Ordinal))
    {
        Expect("internet dual white CTAs",
            x.Contains("Low latency", StringComparison.Ordinal)
            && x.Contains("Highest download", StringComparison.Ordinal)
            && x.Contains("ExoWhiteButton", StringComparison.Ordinal));
        Expect("internet Repair button", x.Contains("Content=\"Repair\"", StringComparison.Ordinal));
        // Proof layer: benchmark delta, rollback banner, honest Repair caption.
        Expect("internet proof layer",
            x.Contains("BenchmarkSummary", StringComparison.Ordinal)
            && x.Contains("RollbackNotice", StringComparison.Ordinal)
            && x.Contains("RepairHint", StringComparison.Ordinal));
        var ics = Path.Combine(repo, "Exo", "Views", "InternetOptimizerPage.xaml.cs");
        if (File.Exists(ics))
        {
            var ic = File.ReadAllText(ics);
            Expect("internet no preset dialog", !ic.Contains("RequestPresetChoice", StringComparison.Ordinal));
            Expect("internet repair wired", ic.Contains("Repair_Click", StringComparison.Ordinal));
        }
    }
}

// Feature tiles: vertical StackLayout + SizeChanged width sync (full-width list).
var featureGridXaml = Path.Combine(repo, "Exo", "Views", "Controls", "FeatureTileGrid.xaml");
var featureGridCs = Path.Combine(repo, "Exo", "Views", "Controls", "FeatureTileGrid.xaml.cs");
Expect("FeatureTileGrid control", File.Exists(featureGridXaml) && File.Exists(featureGridCs));
if (File.Exists(featureGridXaml))
{
    var fg = File.ReadAllText(featureGridXaml);
    Expect("feature grid stretch host",
        fg.Contains("ScrollHost_SizeChanged", StringComparison.Ordinal)
        && fg.Contains("HorizontalAlignment=\"Stretch\"", StringComparison.Ordinal));
    Expect("feature grid vertical layout",
        (fg.Contains("StackLayout", StringComparison.Ordinal)
            && fg.Contains("Orientation=\"Vertical\"", StringComparison.Ordinal))
        || (fg.Contains("Layout", StringComparison.Ordinal)
            && fg.Contains("Orientation=\"Vertical\"", StringComparison.Ordinal)));
    Expect("feature grid overlay scrollbar",
        fg.Contains("DefaultScrollBarStyle", StringComparison.Ordinal)
        && fg.Contains("ScrollBarTrackFill", StringComparison.Ordinal)
        && fg.Contains("ExoFeatureScrollTransparentBrush", StringComparison.Ordinal)
        && fg.Contains("ScrollBarThumbFill", StringComparison.Ordinal)
        && fg.Contains("VerticalScrollBarVisibility=\"Auto\"", StringComparison.Ordinal));
    Expect("feature grid bottom scroll cue",
        fg.Contains("Margin=\"0,0,0,16\"", StringComparison.Ordinal)
        && fg.Contains("Trailing scroll buffer", StringComparison.Ordinal));
}
if (File.Exists(featureGridCs))
{
    var fgc = File.ReadAllText(featureGridCs);
    Expect("feature grid width sync",
        fgc.Contains("TileRepeater.Width", StringComparison.Ordinal)
        && fgc.Contains("ScrollHost_SizeChanged", StringComparison.Ordinal));
}
foreach (var page in new[]
         {
             "DiscordOptimizerPage.xaml", "SteamOptimizerPage.xaml",
             "InternetOptimizerPage.xaml", "NvidiaOptimizerPage.xaml"
         })
{
    var p = Path.Combine(repo, "Exo", "Views", page);
    if (!File.Exists(p)) continue;
    var x = File.ReadAllText(p);
    Expect(page + " uses FeatureTileGrid",
        (x.Contains("FeatureTileGrid", StringComparison.Ordinal)
            || x.Contains("ViewerTileGrid", StringComparison.Ordinal))
        && !x.Contains("x:Name=\"FeatureRepeater\"", StringComparison.Ordinal));
    Expect(page + " compact action footer",
        x.Contains("Padding=\"16,8,16,10\"", StringComparison.Ordinal)
        && x.Contains("Spacing=\"6\"", StringComparison.Ordinal));
}

var internetDensityXaml = Path.Combine(repo, "Exo", "Views", "InternetOptimizerPage.xaml");
if (File.Exists(internetDensityXaml))
{
    var ix = File.ReadAllText(internetDensityXaml);
    Expect("internet single-row action density",
        ix.Contains("ExoInternetActionGrid", StringComparison.Ordinal)
        && ix.Contains("<ColumnDefinition Width=\"3*\"", StringComparison.Ordinal)
        && ix.Contains("Grid.Column=\"3\"", StringComparison.Ordinal));
    Expect("internet compact honest messages",
        ix.Contains("RollbackNotice", StringComparison.Ordinal)
        && ix.Contains("Style=\"{StaticResource ExoMessageText}\"", StringComparison.Ordinal)
        && ix.Contains("Style=\"{StaticResource ExoInfoMessageText}\"", StringComparison.Ordinal));
}

// Friction-free apply/repair: no blocking ContentDialog confirmations on module pages
// (update consent dialogs live in ExoUpdateDialog only), and every module plays the
// staggered feature-tile entrance on its first loading → loaded transition.
foreach (var page in new[]
         {
             "DiscordOptimizerPage", "SteamOptimizerPage", "InternetOptimizerPage", "NvidiaOptimizerPage"
         })
{
    var cs = Path.Combine(repo, "Exo", "Views", page + ".xaml.cs");
    if (!File.Exists(cs)) continue;
    var code = File.ReadAllText(cs);
    Expect(page + " no confirm dialog",
        !code.Contains("ContentDialog", StringComparison.Ordinal)
        && !code.Contains("ConfirmAsync", StringComparison.Ordinal));
    Expect(page + " tile entrance",
        code.Contains("PlayListEnter", StringComparison.Ordinal)
        && code.Contains("IsFeatureListVisible", StringComparison.Ordinal));
}
foreach (var vmName in new[]
         {
             "DiscordOptimizerViewModel", "SteamOptimizerViewModel",
             "InternetOptimizerViewModel", "NvidiaOptimizerViewModel"
         })
{
    var vmPath = Path.Combine(repo, "Exo", "ViewModels", vmName + ".cs");
    if (!File.Exists(vmPath)) continue;
    var vmCode = File.ReadAllText(vmPath);
    Expect(vmName + " no confirm gate", !vmCode.Contains("ConfirmAsync", StringComparison.Ordinal));
}

// Last-apply step report is surfaced on Internet / Discord / Steam (NVIDIA has no
// applyReport data — intentionally omitted, never faked).
foreach (var page in new[] { "DiscordOptimizerPage", "SteamOptimizerPage", "InternetOptimizerPage" })
{
    var px = Path.Combine(repo, "Exo", "Views", page + ".xaml");
    if (!File.Exists(px)) continue;
    var pxText = File.ReadAllText(px);
    Expect(page + " last apply report",
        pxText.Contains("ApplyReportRows", StringComparison.Ordinal)
        && pxText.Contains("ApplyReportSummary", StringComparison.Ordinal));
}
var steamXamlPath = Path.Combine(repo, "Exo", "Views", "SteamOptimizerPage.xaml");
if (File.Exists(steamXamlPath))
{
    var sx = File.ReadAllText(steamXamlPath);
    Expect("steam trim stats row",
        sx.Contains("TrimStatsText", StringComparison.Ordinal)
        && sx.Contains("HasTrimStats", StringComparison.Ordinal));
}
var nvidiaXamlPath = Path.Combine(repo, "Exo", "Views", "NvidiaOptimizerPage.xaml");
if (File.Exists(nvidiaXamlPath))
{
    var nx = File.ReadAllText(nvidiaXamlPath);
    // NVIDIA Reset is status-clear only — honest caption, no rollback wording.
    Expect("nvidia reset honest caption",
        nx.Contains("Reset clears Exo status only", StringComparison.Ordinal)
        && !nx.Contains("rollback", StringComparison.OrdinalIgnoreCase));
}

var loaderCs = Path.Combine(repo, "Exo", "Views", "Controls", "ExoLoader.xaml.cs");
Expect("ExoLoader control", File.Exists(loaderCs));
Expect("no OptiLoader", !File.Exists(Path.Combine(repo, "Exo", "Views", "Controls", "OptiLoader.xaml.cs"))
    && !File.Exists(Path.Combine(repo, "Exo", "Views", "Controls", "OptiLoader.xaml")));
if (File.Exists(loaderCs))
{
    var lc = File.ReadAllText(loaderCs);
    Expect("ExoLoader IsActive", lc.Contains("IsActiveProperty", StringComparison.Ordinal));
    // Pure XAML Storyboards — no ElementCompositionPreview (v2.6.0 crash class).
    Expect("ExoLoader XAML storyboard orbit",
        lc.Contains("Storyboard", StringComparison.Ordinal) &&
        lc.Contains("DoubleAnimation", StringComparison.Ordinal) &&
        lc.Contains("OrbitRotate", StringComparison.Ordinal) &&
        !lc.Contains("Bar0Scale", StringComparison.Ordinal));
    Expect("ExoLoader zero composition API",
        !lc.Contains("ElementCompositionPreview", StringComparison.Ordinal) &&
        !lc.Contains("Microsoft.UI.Xaml.Hosting", StringComparison.Ordinal) &&
        !lc.Contains("StartAnimation", StringComparison.Ordinal));
}

var motionCs = Path.Combine(repo, "Exo", "Helpers", "ExoMotion.cs");
Expect("no OptiMotion", !File.Exists(Path.Combine(repo, "Exo", "Helpers", "OptiMotion.cs")));
if (File.Exists(motionCs))
{
    var m = File.ReadAllText(motionCs);
    Expect("ExoMotion ResetVisual", m.Contains("ResetVisual", StringComparison.Ordinal));
    Expect("ExoMotion EnsureVisible", m.Contains("EnsureVisible", StringComparison.Ordinal));
    // Dead overlay/scrim era APIs must stay deleted (settings is a gear flyout now).
    Expect("ExoMotion dead overlay APIs gone",
        !m.Contains("PlayOverlayOpen", StringComparison.Ordinal)
        && !m.Contains("PlayOverlayClose", StringComparison.Ordinal)
        && !m.Contains("PlayScrimFade", StringComparison.Ordinal)
        && !m.Contains("ClearCompositionOnly", StringComparison.Ordinal)
        && !m.Contains("Spring()", StringComparison.Ordinal));
    Expect("ExoMotion list enter", m.Contains("PlayListEnter", StringComparison.Ordinal));
    // Hand-off composition visuals must never be touched: writing Visual.Offset/
    // Scale detaches elements from XAML layout (everything piles at the origin)
    // and pre-first-frame pokes crash real GPUs with 0xC000027B (v2.6.0 launch bug).
    Expect("ExoMotion no composition visual writes",
        !m.Contains("ElementCompositionPreview", StringComparison.Ordinal)
        && !m.Contains("visual.Offset", StringComparison.Ordinal)
        && !m.Contains("visual.Opacity", StringComparison.Ordinal)
        && !m.Contains("Microsoft.UI.Xaml.Hosting", StringComparison.Ordinal));
    // XAML storyboards only — no composition StartAnimation for shell motion.
    Expect("ExoMotion uses XAML storyboards",
        m.Contains("Storyboard", StringComparison.Ordinal)
        && m.Contains("DoubleAnimation", StringComparison.Ordinal)
        && !m.Contains("StartAnimation(\"Offset\"", StringComparison.Ordinal)
        && !m.Contains("StartAnimation(\"Opacity\"", StringComparison.Ordinal));
    Expect("ExoMotion PlaySelect", m.Contains("PlaySelect", StringComparison.Ordinal));
    Expect("ExoMotion page enter ensure visible",
        m.Contains("PlayPageEnter", StringComparison.Ordinal)
        && m.Contains("EnsureVisible", StringComparison.Ordinal)
        && !m.Contains("PrimeHidden", StringComparison.Ordinal));
}
var mainCsPath = Path.Combine(repo, "Exo", "MainWindow.xaml.cs");
if (File.Exists(mainCsPath))
{
    var mc = File.ReadAllText(mainCsPath);
    Expect("settings gear spin + flyout",
        mc.Contains("SpinSettingsGear", StringComparison.Ordinal)
        && mc.Contains("SettingsFlyout", StringComparison.Ordinal)
        && mc.Contains("ShowAttachedFlyout", StringComparison.Ordinal)
        && !mc.Contains("OpenSettingsRail", StringComparison.Ordinal)
        && !mc.Contains("SettingsRail", StringComparison.Ordinal));
    Expect("settings open is immediate",
        mc.Contains("ShowAttachedFlyout", StringComparison.Ordinal)
        && mc.IndexOf("ShowAttachedFlyout", StringComparison.Ordinal)
            < mc.IndexOf("SpinSettingsGear();", StringComparison.Ordinal));
    Expect("settings open plays menu entrance with gear",
        mc.Contains("PlayOpenAnimation", StringComparison.Ordinal)
        && mc.Contains("SettingsFlyout_Opened", StringComparison.Ordinal)
        && mc.Contains("SettingsSheet.OpenMs", StringComparison.Ordinal));
    Expect("settings close plays menu exit with gear",
        mc.Contains("PlayCloseAnimation", StringComparison.Ordinal)
        && mc.Contains("SettingsFlyout_Closing", StringComparison.Ordinal)
        && mc.Contains("SpinSettingsGearBack", StringComparison.Ordinal)
        && mc.Contains("SettingsSheet.CloseMs", StringComparison.Ordinal));
    Expect("taskbar icon win32 set",
        mc.Contains("SendMessage", StringComparison.Ordinal) && mc.Contains("LoadImage", StringComparison.Ordinal)
        && mc.Contains("TrySetWindowIcon", StringComparison.Ordinal)
        && mc.Contains("TryRepairStartMenuShortcut", StringComparison.Ordinal));
    Expect("navigate ensures page visible",
        mc.Contains("OnContentNavigated", StringComparison.Ordinal)
        && mc.Contains("EnsureVisible", StringComparison.Ordinal));
}
var programCs = Path.Combine(repo, "Exo", "Program.cs");
if (File.Exists(programCs))
{
    var p = File.ReadAllText(programCs);
    Expect("AppUserModelID set early",
        p.Contains("SetCurrentProcessExplicitAppUserModelID", StringComparison.Ordinal)
        && p.Contains("ImAvgErix.Exo", StringComparison.Ordinal));
}
var sfxCs = Path.Combine(repo, "tools", "ExoSfx.cs");
if (File.Exists(sfxCs))
{
    var sx = File.ReadAllText(sfxCs);
    Expect("SFX stable icon path",
        sx.Contains("Never use versioned names", StringComparison.Ordinal)
        && sx.Contains("Exo.ico", StringComparison.Ordinal));
}

var dashCs = Path.Combine(repo, "Exo", "Views", "DashboardPage.xaml.cs");
if (File.Exists(dashCs))
{
    var dc = File.ReadAllText(dashCs);
    Expect("home hero stagger entrance",
        dc.Contains("PlayStagger", StringComparison.Ordinal)
        && dc.Contains("EnsureVisible", StringComparison.Ordinal)
        && !dc.Contains("PrimeHidden", StringComparison.Ordinal));
    Expect("no home card select pulse",
        !dc.Contains("CardButton_Click", StringComparison.Ordinal));
    Expect("dashboard cache for clean back",
        dc.Contains("NavigationCacheMode.Enabled", StringComparison.Ordinal)
        && dc.Contains("StabilizeHome", StringComparison.Ordinal));
}

// Card button must not force Left/Top (top-left drift).
if (File.Exists(theme))
{
    var tCard = File.ReadAllText(theme);
    var cardIdx = tCard.IndexOf("ExoCardButton", StringComparison.Ordinal);
    var cardSlice = cardIdx >= 0 ? tCard.Substring(cardIdx, Math.Min(800, tCard.Length - cardIdx)) : "";
    Expect("card button not top-left aligned",
        cardIdx >= 0
        && cardSlice.Contains("HorizontalAlignment", StringComparison.Ordinal)
        && cardSlice.Contains("Value=\"Center\"", StringComparison.Ordinal)
        && !cardSlice.Contains("Value=\"Left\"", StringComparison.Ordinal));
}

// Version gate
var versionFile = Path.Combine(repo, "VERSION");
var csproj = Path.Combine(repo, "Exo", "Exo.csproj");
if (File.Exists(versionFile))
    Expect("VERSION is 2.7.0", File.ReadAllText(versionFile).Trim() == "2.7.0");
if (File.Exists(csproj))
    Expect("csproj Version 2.7.0", File.ReadAllText(csproj).Contains("<Version>2.7.0</Version>", StringComparison.Ordinal));

// Live advisor (realtime next-step coach on every optimizer)
var advisorPath = Path.Combine(repo, "Exo", "Services", "OptimizerAdvisor.cs");
Expect("OptimizerAdvisor exists", File.Exists(advisorPath));
if (File.Exists(advisorPath))
{
    var adv = File.ReadAllText(advisorPath);
    Expect("OptimizerAdvisor no background tasks message",
        adv.Contains("No Exo background tasks", StringComparison.Ordinal));
    Expect("OptimizerAdvisor covers all modules",
        adv.Contains("\"Internet\"", StringComparison.Ordinal)
        && adv.Contains("\"Discord\"", StringComparison.Ordinal)
        && adv.Contains("\"Steam\"", StringComparison.Ordinal)
        && adv.Contains("\"NVIDIA\"", StringComparison.Ordinal));
}
foreach (var page in new[] { "DiscordOptimizerPage.xaml", "SteamOptimizerPage.xaml", "NvidiaOptimizerPage.xaml", "InternetOptimizerPage.xaml" })
{
    var p = Path.Combine(repo, "Exo", "Views", page);
    if (!File.Exists(p)) continue;
    var xaml = File.ReadAllText(p);
    Expect($"live guidance on {page}",
        xaml.Contains("GuidanceText", StringComparison.Ordinal)
        && xaml.Contains("HasGuidance", StringComparison.Ordinal));
}
// Dead modal settings state must stay gone.
var overlayState = Path.Combine(repo, "Exo", "Helpers", "SettingsOverlayState.cs");
Expect("no dead SettingsOverlayState", !File.Exists(overlayState));

// Logos decode full-fidelity (no forced downscale that softens/pixelates).
var convertersCs = Path.Combine(repo, "Exo", "Helpers", "ValueConverters.cs");
if (File.Exists(convertersCs))
{
    var cv = File.ReadAllText(convertersCs);
    Expect("logo decode 2x display",
        cv.Contains("AssetPathToImageSourceConverter", StringComparison.Ordinal)
        && cv.Contains("DecodePixelWidth = 128", StringComparison.Ordinal)
        && cv.Contains("DecodePixelType.Logical", StringComparison.Ordinal));
    var motion = File.ReadAllText(Path.Combine(repo, "Exo", "Helpers", "ExoMotion.cs"));
    Expect("entrance rise then clear transform",
        motion.Contains("TranslateY", StringComparison.Ordinal)
        && motion.Contains("RenderTransform = null", StringComparison.Ordinal)
        && motion.Contains("PlayEnter", StringComparison.Ordinal));
}
// Card hover ring (focus without scale blur).
if (File.Exists(theme))
{
    var tMotion = File.ReadAllText(theme);
    Expect("card hover ring not scale",
        tMotion.Contains("HoverRing", StringComparison.Ordinal)
        && tMotion.Contains("HoverWash", StringComparison.Ordinal)
        && tMotion.Contains("ExoCardButton", StringComparison.Ordinal));
}

var appSettings = Path.Combine(repo, "Exo", "Models", "AppSettings.cs");
if (File.Exists(appSettings))
    Expect("AppSettings no MotionIntensity", !File.ReadAllText(appSettings).Contains("MotionIntensity", StringComparison.Ordinal));
var settingsVm = Path.Combine(repo, "Exo", "ViewModels", "SettingsViewModel.cs");
if (File.Exists(settingsVm))
{
    var svm = File.ReadAllText(settingsVm);
    Expect("VM no motion slider",
        !svm.Contains("MotionIntensity", StringComparison.Ordinal)
        && !svm.Contains("MotionStrength", StringComparison.Ordinal));
    // Old theme-toggle era leftovers must stay deleted.
    Expect("VM no dead settings leftovers",
        !svm.Contains("KitVersion", StringComparison.Ordinal)
        && !svm.Contains("CurrentThemeLabel", StringComparison.Ordinal)
        && !svm.Contains("ThemeSwitchHint", StringComparison.Ordinal));
}
if (File.Exists(theme))
{
    var t2 = File.ReadAllText(theme);
    // Dead styles must stay deleted; no BackEase (spring bounce) anywhere in the theme.
    Expect("theme dead styles gone",
        !t2.Contains("ExoSecondaryButton", StringComparison.Ordinal)
        && !t2.Contains("ExoThemeToggleButton", StringComparison.Ordinal)
        && !t2.Contains("ExoTaglineSupport", StringComparison.Ordinal)
        && !t2.Contains("ExoLogoWell", StringComparison.Ordinal));
    Expect("theme no BackEase", !t2.Contains("BackEase", StringComparison.Ordinal));
}

// Logo visual weight: measure real shipped PNG alpha ink (Windows only —
// System.Drawing.Common is not supported on Linux). On Linux we still assert
// the logo files exist so packaging regressions are caught.
if (Directory.Exists(logosDir))
{
#if EXO_HAS_DRAWING
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
#else
    Log("SKIP  logo ink measure (System.Drawing.Common Windows-only)");
    foreach (var name in new[] { "discord.png", "steam.png", "nvidia.png", "amd.png", "internet.png" })
        Expect("logo asset " + name, File.Exists(Path.Combine(logosDir, name)));
#endif
}

var dashVm = Path.Combine(repo, "Exo", "ViewModels", "DashboardViewModel.cs");
if (File.Exists(dashVm))
{
    var dvm = File.ReadAllText(dashVm);
    // Home must not probe Discord/Steam/NVIDIA — open the module for that.
    Expect("home no discord probe", !dvm.Contains("DetectDiscordAsync", StringComparison.Ordinal));
    Expect("home no steam probe", !dvm.Contains("DetectSteamAsync", StringComparison.Ordinal));
    Expect("home no nvidia probe", !dvm.Contains("DetectNvidiaAsync", StringComparison.Ordinal));
    Expect("home dashboard refresh", dvm.Contains("RefreshDashboard", StringComparison.Ordinal)
        && dvm.Contains("HomeDashboardReader", StringComparison.Ordinal));
    Expect("windows coming soon card", dvm.Contains("Card(\"windows\"", StringComparison.Ordinal)
        && dvm.Contains("windows.png", StringComparison.Ordinal));
}
var homeDashReader = Path.Combine(repo, "Exo", "Services", "HomeDashboardReader.cs");
if (File.Exists(homeDashReader))
{
    var hdr = File.ReadAllText(homeDashReader);
    Expect("home trim stats file read",
        hdr.Contains("steam-trim-stats.json", StringComparison.Ordinal)
        && hdr.Contains("TryReadTrimStats", StringComparison.Ordinal));
    Expect("home live memory api",
        hdr.Contains("GlobalMemoryStatusEx", StringComparison.Ordinal)
        && hdr.Contains("TryReadMemory", StringComparison.Ordinal));
    Expect("home latency file read", hdr.Contains("TryReadLatency", StringComparison.Ordinal));
    Expect("home nvidia path file read",
        hdr.Contains("TryReadNvidiaPath", StringComparison.Ordinal)
        && hdr.Contains("nvidia-optimizer.json", StringComparison.Ordinal));
    Expect("home no invented fps capture",
        !hdr.Contains("PresentMon", StringComparison.Ordinal)
        && !hdr.Contains("fpsGain", StringComparison.OrdinalIgnoreCase));
}
else
{
    Expect("home dashboard reader exists", false);
}
if (File.Exists(Path.Combine(logosDir, "windows.png")))
    Expect("windows logo asset", true);
else
    Expect("windows logo asset", false, "missing Assets/Logos/windows.png");

var panelVm = Path.Combine(repo, "Exo", "ViewModels", "NvidiaPanelViewModel.cs");
if (File.Exists(panelVm))
{
    var pv = File.ReadAllText(panelVm);
    Expect("panel force refresh", pv.Contains("RefreshCoreAsync(force: true", StringComparison.Ordinal));
    // Vibrance is loaded with the display list and applied through the same dirty-diff Apply.
    Expect("panel vibrance wired",
        pv.Contains("ListVibranceAsync", StringComparison.Ordinal)
        && pv.Contains("SetVibranceAsync", StringComparison.Ordinal)
        && pv.Contains("IsVibranceDirty", StringComparison.Ordinal));
}

var nv = Path.Combine(repo, "tools", "Exo.NvDisplay", "Program.cs");
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

var nvDetect = Path.Combine(repo, "Exo", "Scripts", "Nvidia", "Exo-Nvidia-Detect.ps1");
if (File.Exists(nvDetect))
{
    var det = File.ReadAllText(nvDetect);
    // Laptops must not permanently force "manual action only" for isApplied.
    Expect("nv detect no permanent notebook fail",
        !det.Contains("$needsDriverAction = $needsUpdate -or $needsRetweak -or $isNotebookGpu", StringComparison.Ordinal));
    Expect("nv optimus display skip ok",
        det.Contains("no-active-nvidia-displays", StringComparison.Ordinal));
}

var nvHeuristic = Path.Combine(repo, "Exo", "Services", "OptimizerStateService.cs");
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
        if (File.Exists(Path.Combine(dir.FullName, "VERSION")) && Directory.Exists(Path.Combine(dir.FullName, "Exo", "Views")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}

#if EXO_HAS_DRAWING
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
#endif
