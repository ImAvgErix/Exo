#if EXO_HAS_DRAWING
#pragma warning disable CA1416 // System.Drawing is compiled and run only on Windows.
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
#endif
using System.Text.RegularExpressions;
using System.Security.Cryptography;
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

// Drive real AppSettings clone path (single dark theme; update preference remains).
var settingsA = new AppSettings { CheckForUpdatesOnLaunch = true };
var settingsB = settingsA.Clone();
Expect("AppSettings clone", settingsB.CheckForUpdatesOnLaunch);

var repo = FindRepoRoot();
var appXaml = Path.Combine(repo, "Exo", "App.xaml");
var main = Path.Combine(repo, "Exo", "MainWindow.xaml");
var dash = Path.Combine(repo, "Exo", "Views", "DashboardPage.xaml");
var settings = Path.Combine(repo, "Exo", "Views", "Controls", "SettingsSheet.xaml");
var mainXaml = Path.Combine(repo, "Exo", "MainWindow.xaml");
var theme = Path.Combine(repo, "Exo", "Styles", "ThemeResources.xaml");
var colorTokens = Path.Combine(repo, "Exo", "Styles", "Tokens.Colors.xaml");
var typeTokens = Path.Combine(repo, "Exo", "Styles", "Tokens.Type.xaml");
var metricTokens = Path.Combine(repo, "Exo", "Styles", "Tokens.Metrics.xaml");
var converters = Path.Combine(repo, "Exo", "Helpers", "ValueConverters.cs");
var logosDir = Path.Combine(repo, "Exo", "Assets", "Logos");
var appServicesCs = Path.Combine(repo, "Exo", "Services", "AppServices.cs");
var powerShellRunnerCs = Path.Combine(repo, "Exo", "Services", "PowerShellRunnerService.cs");
var updateServiceCs = Path.Combine(repo, "Exo", "Services", "GitHubUpdateService.cs");
var installerPs1 = Path.Combine(repo, "Install-Exo.ps1");
var programBootCs = Path.Combine(repo, "Exo", "Program.cs");
var singleInstanceCs = Path.Combine(repo, "Exo", "Helpers", "SingleInstanceManager.cs");
var startupDiagnosticsCs = Path.Combine(repo, "Exo", "Helpers", "StartupDiagnostics.cs");
var nativeSecurityCs = Path.Combine(repo, "Exo", "Helpers", "NativeProcessSecurity.cs");
var shippedManifestCs = Path.Combine(repo, "Exo", "Security", "ShippedScriptManifest.cs");
var generatedManifestCs = Path.Combine(repo, "Exo", "Security", "ShippedScriptManifest.g.cs");

Expect("files", File.Exists(appXaml) && File.Exists(main) && File.Exists(dash));
if (File.Exists(programBootCs) && File.Exists(singleInstanceCs) &&
    File.Exists(startupDiagnosticsCs) && File.Exists(nativeSecurityCs))
{
    var programSource = File.ReadAllText(programBootCs);
    var singleInstanceSource = File.ReadAllText(singleInstanceCs);
    var diagnosticsSource = File.ReadAllText(startupDiagnosticsCs);
    var nativeSecuritySource = File.ReadAllText(nativeSecurityCs);
    Expect("single instance redirects before WinUI startup",
        programSource.IndexOf("IsPrimaryInstance", StringComparison.Ordinal) <
        programSource.IndexOf("EnterPhase(\"xaml-requirements\")", StringComparison.Ordinal)
        && singleInstanceSource.Contains("RedirectActivationToAsync", StringComparison.Ordinal));
    Expect("fatal startup diagnostics redact user identity",
        programSource.Contains("StartupDiagnostics.WriteFatal", StringComparison.Ordinal)
        && diagnosticsSource.Contains("<user-path>", StringComparison.Ordinal)
        && diagnosticsSource.Contains("<user>", StringComparison.Ordinal));
    Expect("current directory removed from native DLL search",
        nativeSecuritySource.Contains("SetDllDirectory(string.Empty)", StringComparison.Ordinal)
        && !nativeSecuritySource.Contains("LoadLibrarySearchDefaultDirs", StringComparison.Ordinal));
}
if (File.Exists(shippedManifestCs) && File.Exists(generatedManifestCs))
{
    var integritySource = File.ReadAllText(shippedManifestCs);
    var generatedSource = File.ReadAllText(generatedManifestCs);
    var entries = Regex.Matches(generatedSource,
        "\\[\\\"(?<path>[^\\\"]+)\\\"\\] = new\\((?<length>\\d+)L, \\\"(?<hash>[A-F0-9]{64})\\\"\\)");
    var manifestFresh = entries.Count >= 50;
    foreach (Match entry in entries)
    {
        var file = Path.Combine(repo, "Exo", "Scripts",
            entry.Groups["path"].Value.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(file))
        {
            manifestFresh = false;
            break;
        }
        var extension = Path.GetExtension(file);
        var fileName = Path.GetFileName(file);
        var isText = extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".ini", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".c", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".def", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".css", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".vbs", StringComparison.OrdinalIgnoreCase) ||
                     fileName.Equals("VERSION", StringComparison.OrdinalIgnoreCase) ||
                     fileName.Equals("PROFILE_VERSION", StringComparison.OrdinalIgnoreCase);
        var bytes = isText
            ? System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(file)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal))
            : File.ReadAllBytes(file);
        if (bytes.LongLength != long.Parse(entry.Groups["length"].Value))
        {
            manifestFresh = false;
            break;
        }
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!hash.Equals(entry.Groups["hash"].Value, StringComparison.OrdinalIgnoreCase))
        {
            manifestFresh = false;
            break;
        }
    }
    Expect("compiled script manifest matches shipped bytes", manifestFresh);
    Expect("compiled script manifest excludes local build outputs",
        !generatedSource.Contains("Nvidia/tools/", StringComparison.OrdinalIgnoreCase));
    Expect("manifest validation fails closed",
        integritySource.Contains("SHA-256 mismatch", StringComparison.Ordinal)
        && integritySource.Contains("not present in this Exo build's signed script manifest", StringComparison.Ordinal));
}
if (File.Exists(appServicesCs) && File.Exists(powerShellRunnerCs))
{
    var servicesSource = File.ReadAllText(appServicesCs);
    var runnerSource = File.ReadAllText(powerShellRunnerCs);
    Expect("startup performs no dependency bootstrap",
        !servicesSource.Contains("EnsurePowerShellRuntimeAsync", StringComparison.Ordinal)
        && !servicesSource.Contains("Task.Run", StringComparison.Ordinal));
    Expect("PowerShell bootstrap requires explicit run opt-in",
        runnerSource.Contains("bool ensureRuntime = false", StringComparison.Ordinal)
        && runnerSource.Contains("if (ensureRuntime)", StringComparison.Ordinal)
        && runnerSource.Contains("Preparing PowerShell 7", StringComparison.Ordinal));
    Expect("elevation does not depend on deprecated VBScript",
        runnerSource.Contains("Verb = \"runas\"", StringComparison.Ordinal)
        && !runnerSource.Contains("wscript.exe", StringComparison.OrdinalIgnoreCase)
        && !runnerSource.Contains(".vbs", StringComparison.OrdinalIgnoreCase));
    Expect("elevated bootstrap is in-memory and rehashes the script",
        runnerSource.Contains("-EncodedCommand", StringComparison.Ordinal)
        && runnerSource.Contains("Get-FileHash -LiteralPath $script -Algorithm SHA256", StringComparison.Ordinal)
        && runnerSource.Contains("Optimizer script changed after approval; execution blocked.", StringComparison.Ordinal)
        && !runnerSource.Contains("wrap-{stamp}.ps1", StringComparison.Ordinal)
        && !runnerSource.Contains("& $pwsh -NoProfile -ExecutionPolicy Bypass -File $script", StringComparison.Ordinal));
    Expect("elevated results use protected machine transaction storage",
        runnerSource.Contains("MachineTransactionsDir", StringComparison.Ordinal)
        && runnerSource.Contains("Protect-Directory", StringComparison.Ordinal)
        && runnerSource.Contains("*S-1-5-32-545:(OI)(CI)RX", StringComparison.Ordinal)
        && runnerSource.Contains("Assert-PlainDirectory", StringComparison.Ordinal)
        && !runnerSource.Contains("$\"exit-{stamp}.txt\"", StringComparison.Ordinal));

    var actionSources = new[]
    {
        Path.Combine(repo, "Exo", "ViewModels", "DiscordOptimizerViewModel.cs"),
        Path.Combine(repo, "Exo", "ViewModels", "SteamOptimizerViewModel.cs"),
        Path.Combine(repo, "Exo", "ViewModels", "NvidiaOptimizerViewModel.cs"),
        Path.Combine(repo, "Exo", "Services", "NvidiaPanelSettingsService.cs")
    };
    Expect("Apply and Repair opt in to dependency preparation",
        actionSources.All(File.Exists)
        && actionSources.All(path => File.ReadAllText(path).Contains("ensureRuntime: true", StringComparison.Ordinal)));

    var updateSource = File.Exists(updateServiceCs) ? File.ReadAllText(updateServiceCs) : string.Empty;
    var installerSource = File.Exists(installerPs1) ? File.ReadAllText(installerPs1) : string.Empty;
    Expect("install and app update do not bootstrap dependencies",
        !updateSource.Contains("TryRunDependencyDoctor", StringComparison.Ordinal)
        && !installerSource.Contains("Exo-DependencyDoctor", StringComparison.Ordinal));
    Expect("app updater does not fetch script kits",
        !updateSource.Contains("raw.githubusercontent.com", StringComparison.Ordinal)
        && !updateSource.Contains("codeload.github.com", StringComparison.Ordinal));
    Expect("runtime and app downloads require SHA-256",
        runnerSource.Contains("release asset did not publish a SHA-256 digest", StringComparison.Ordinal)
        && updateSource.Contains("GitHub did not publish a SHA-256 digest", StringComparison.Ordinal)
        && !updateSource.Contains("latest/download/Exo.exe", StringComparison.Ordinal));
}
if (File.Exists(appXaml) && File.Exists(colorTokens) && File.Exists(typeTokens) && File.Exists(metricTokens))
{
    var a = File.ReadAllText(appXaml);
    var colors = File.ReadAllText(colorTokens);
    var types = File.ReadAllText(typeTokens);
    var metrics = File.ReadAllText(metricTokens);
    Expect("dark page token", colors.Contains("<Color x:Key=\"ExoColorPage\">#050505</Color>", StringComparison.Ordinal));
    Expect("stone white primary token", colors.Contains("<Color x:Key=\"ExoColorPrimaryText\">#F4F4F2</Color>", StringComparison.Ordinal));
    Expect("light theme removed", !colors.Contains("x:Key=\"Light\"", StringComparison.Ordinal)
        && !a.Contains("x:Key=\"Light\"", StringComparison.Ordinal));
    Expect("High Contrast dictionary", colors.Contains("x:Key=\"HighContrast\"", StringComparison.Ordinal)
        && colors.Contains("SystemColorWindowBrush", StringComparison.Ordinal));
    Expect("token dictionaries merged", a.Contains("Styles/Tokens.Colors.xaml", StringComparison.Ordinal)
        && a.Contains("Styles/Tokens.Type.xaml", StringComparison.Ordinal)
        && a.Contains("Styles/Tokens.Metrics.xaml", StringComparison.Ordinal));
    Expect("dark solid card lift", colors.Contains("#101113", StringComparison.Ordinal)
        && colors.Contains("#0A0A0B", StringComparison.Ordinal));
    Expect("liquid glass fill token", colors.Contains("ExoGlassFillBrush", StringComparison.Ordinal));
    Expect("settings solid surface brush",
        colors.Contains("ExoSettingsSurfaceBrush", StringComparison.Ordinal)
        && !colors.Contains("ExoSettingsAcrylicBrush", StringComparison.Ordinal)
        && !a.Contains("<media:AcrylicBrush", StringComparison.Ordinal));
    Expect("integer readable type ramp", types.Contains("ExoTypeCaptionSize\">12", StringComparison.Ordinal)
        && types.Contains("ExoTypeBodySize\">14", StringComparison.Ordinal)
        && !types.Contains("12.5", StringComparison.Ordinal));
    Expect("4px metric ramp", metrics.Contains("ExoSpaceXS\">4", StringComparison.Ordinal)
        && metrics.Contains("ExoSpaceL\">16", StringComparison.Ordinal)
        && metrics.Contains("ExoPageMaxWidth\">1120", StringComparison.Ordinal));

    var xamlFiles = Directory.EnumerateFiles(Path.Combine(repo, "Exo"), "*.xaml", SearchOption.AllDirectories)
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        .ToArray();
    var hexOutsideTokens = xamlFiles
        .Where(path => !Path.GetFullPath(path).Equals(Path.GetFullPath(colorTokens), StringComparison.OrdinalIgnoreCase))
        .Where(path => Regex.IsMatch(File.ReadAllText(path), "#[0-9A-Fa-f]{6,8}"))
        .Select(path => Path.GetRelativePath(repo, path))
        .ToArray();
    Expect("hex colors centralized", hexOutsideTokens.Length == 0, string.Join(", ", hexOutsideTokens));
}
var themeServiceCs = Path.Combine(repo, "Exo", "Services", "ThemeService.cs");
if (File.Exists(themeServiceCs))
{
    var ts = File.ReadAllText(themeServiceCs);
    Expect("theme service dark with OS High Contrast",
        ts.Contains("ElementTheme.Dark", StringComparison.Ordinal)
        && ts.Contains("ElementTheme.Default", StringComparison.Ordinal)
        && ts.Contains("HighContrast", StringComparison.Ordinal)
        && !ts.Contains("ElementTheme.Light", StringComparison.Ordinal));
}
if (File.Exists(main))
{
    var m = File.ReadAllText(main);
    // Labeled top bar: stable Home + modules + Settings with no semantic morphing.
    Expect("nav rail", m.Contains("NavRail", StringComparison.Ordinal));
    Expect("labeled tab nav", m.Contains("ExoNavTab", StringComparison.Ordinal)
        && m.Contains("Text=\"Discord\"", StringComparison.Ordinal)
        && m.Contains("Text=\"Internet\"", StringComparison.Ordinal)
        && !m.Contains("ExoRailGlassFillBrush", StringComparison.Ordinal));
    Expect("top bar workspace",
        m.Contains("<TitleBar.LeftHeader>", StringComparison.Ordinal)
        && m.Contains("<TitleBar.Content>", StringComparison.Ordinal)
        && m.Contains("<TitleBar.RightHeader>", StringComparison.Ordinal));
    Expect("top bar row layout",
        m.Contains("RowDefinitions", StringComparison.Ordinal)
        && m.Contains("Orientation=\"Horizontal\"", StringComparison.Ordinal));
    // Responsive shell: controls never change meaning; TitleBar reserves caption space.
    Expect("settings right rail", m.Contains("SettingsButton", StringComparison.Ordinal));
    Expect("stable home control", m.Contains("x:Name=\"NavHome\"", StringComparison.Ordinal)
        && !m.Contains("HomeChromeIcon", StringComparison.Ordinal));
    Expect("modules centered layer", m.Contains("ModuleIcons", StringComparison.Ordinal));
    Expect("native TitleBar control", m.Contains("<TitleBar x:Name=\"AppTitleBar\"", StringComparison.Ordinal)
        && !m.Contains("CaptionSpacerHost", StringComparison.Ordinal));
    Expect("rail nav discord", m.Contains("NavDiscord", StringComparison.Ordinal));
    var moduleRowStart = m.IndexOf("<StackPanel x:Name=\"ModuleIcons\"", StringComparison.Ordinal);
    var moduleRowEnd = m.IndexOf("</StackPanel>", moduleRowStart, StringComparison.Ordinal);
    var discordInRow = m.IndexOf("x:Name=\"NavDiscord\"", moduleRowStart, StringComparison.Ordinal);
    var settingsRightHeader = m.IndexOf("<TitleBar.RightHeader>", moduleRowEnd, StringComparison.Ordinal);
    var settingsInRightHeader = m.IndexOf("x:Name=\"SettingsButton\"", settingsRightHeader, StringComparison.Ordinal);
    var settingsRightHeaderEnd = m.IndexOf("</TitleBar.RightHeader>", settingsRightHeader, StringComparison.Ordinal);
    Expect("discord center and settings right cannot overlap",
        moduleRowStart >= 0
        && moduleRowEnd > moduleRowStart
        && discordInRow > moduleRowStart
        && discordInRow < moduleRowEnd
        && settingsRightHeader > moduleRowEnd
        && settingsInRightHeader > settingsRightHeader
        && settingsInRightHeader < settingsRightHeaderEnd);
    Expect("rail nav steam", m.Contains("NavSteam", StringComparison.Ordinal));
    Expect("rail nav internet", m.Contains("NavInternet", StringComparison.Ordinal));
    Expect("rail nav nvidia", m.Contains("NavNvidia", StringComparison.Ordinal));
    Expect("rail nav riot", m.Contains("NavRiot", StringComparison.Ordinal));
    Expect("rail nav epic", m.Contains("NavEpic", StringComparison.Ordinal));
    Expect("rail logo discord", m.Contains("discord.png", StringComparison.Ordinal));
    Expect("rail logo steam", m.Contains("steam.png", StringComparison.Ordinal));
    Expect("rail logo internet", m.Contains("internet.png", StringComparison.Ordinal));
    Expect("rail logo nvidia", m.Contains("nvidia.png", StringComparison.Ordinal));
    Expect("settings gear", m.Contains("SettingsButton", StringComparison.Ordinal));
    Expect("dead back chrome removed", !m.Contains("x:Name=\"BackButton\"", StringComparison.Ordinal)
        && !m.Contains("TitleBarDragRegion", StringComparison.Ordinal));
    Expect("no NavigationView", !m.Contains("<NavigationView", StringComparison.Ordinal));
    Expect("ContentFrame", m.Contains("ContentFrame", StringComparison.Ordinal));
    Expect("no tooltips in main", !m.Contains("ToolTip", StringComparison.OrdinalIgnoreCase));
}
// The WinUI TitleBar control owns caption layout and interactive content.
var mainCs = Path.Combine(repo, "Exo", "MainWindow.xaml.cs");
if (File.Exists(mainCs))
{
    var cs = File.ReadAllText(mainCs);
    Expect("SetTitleBar control", cs.Contains("SetTitleBar(AppTitleBar)", StringComparison.Ordinal));
    Expect("responsive shell maximize", cs.Contains("IsMaximizable = true", StringComparison.Ordinal));
    Expect("responsive shell resize", cs.Contains("IsResizable = true", StringComparison.Ordinal)
        && cs.Contains("PreferredMinimumWidth = 960", StringComparison.Ordinal)
        && cs.Contains("PreferredMinimumHeight = 600", StringComparison.Ordinal));
    Expect("rail selection helper", cs.Contains("UpdateRailSelection", StringComparison.Ordinal));
    Expect("settings always on rail",
        cs.Contains("SettingsButton.Visibility = Visibility.Visible", StringComparison.Ordinal)
        && !cs.Contains("SettingsButton.Visibility = Visibility.Collapsed", StringComparison.Ordinal));
    Expect("settings never morphs to home",
        !cs.Contains("HomeChromeIcon", StringComparison.Ordinal)
        && !cs.Contains("if (_mode != ShellMode.Home)", StringComparison.Ordinal)
        && cs.Contains("NavHome.Visibility = Visibility.Visible", StringComparison.Ordinal));
    Expect("dead titlebar fields removed", !cs.Contains("AppTitleText", StringComparison.Ordinal)
        && !cs.Contains("CaptionSpacerHost", StringComparison.Ordinal));
}
if (File.Exists(dash))
{
    var d = File.ReadAllText(dash);
    // Outcome dashboard — verified state, applied policy, and honest live signals.
    Expect("hero status identity",
        d.Contains("HeroBrand", StringComparison.Ordinal)
        && d.Contains("OverviewPrimary", StringComparison.Ordinal)
        && d.Contains("Optimization status", StringComparison.Ordinal));
    Expect("hero tagline",
        d.Contains("HeroTagline", StringComparison.Ordinal)
        && (d.Contains("Maximum performance", StringComparison.Ordinal)
            || d.Contains("HeroSummary", StringComparison.Ordinal)));
    Expect("home instrument plate", d.Contains("ExoModulePlate", StringComparison.Ordinal));
    Expect("home compact system memory",
        d.Contains("SYSTEM MEMORY", StringComparison.Ordinal)
        && d.Contains("MemoryPrimary", StringComparison.Ordinal));
    Expect("home memory load meter",
        d.Contains("MemoryLoadPercent", StringComparison.Ordinal)
        && d.Contains("<ProgressBar", StringComparison.Ordinal));
    Expect("home module identity rails",
        d.Contains("ExoDiscordBrush", StringComparison.Ordinal)
        && d.Contains("ExoSteamBrush", StringComparison.Ordinal)
        && d.Contains("ExoInternetBrush", StringComparison.Ordinal)
        && d.Contains("ExoNvidiaBrush", StringComparison.Ordinal));
    Expect("home steam live ram tile",
        d.Contains("Text=\"Steam\"", StringComparison.Ordinal)
        && d.Contains("SteamStatusPrimary", StringComparison.Ordinal)
        && !d.Contains("STEAM RECLAIMED", StringComparison.Ordinal));
    Expect("home module status row",
        d.Contains("DiscordStatusPrimary", StringComparison.Ordinal)
        && d.Contains("SteamStatusPrimary", StringComparison.Ordinal)
        && d.Contains("NvidiaPathPrimary", StringComparison.Ordinal)
        && d.Contains("LatencyPrimary", StringComparison.Ordinal));
    Expect("home six outcome cards",
        d.Contains("Text=\"Discord\"", StringComparison.Ordinal)
        && d.Contains("Text=\"Steam\"", StringComparison.Ordinal)
        && d.Contains("Text=\"Internet\"", StringComparison.Ordinal)
        && d.Contains("NVIDIA", StringComparison.Ordinal)
        && d.Contains("StatusTag", StringComparison.Ordinal)
        && d.Contains("LiveMetric", StringComparison.Ordinal));
    Expect("home explains optimizer outcomes",
        d.Contains("LIVE SYSTEM READ", StringComparison.Ordinal)
        && d.Contains("OverviewPrimary", StringComparison.Ordinal)
        && d.Contains("detects this PC first", StringComparison.Ordinal));
    Expect("home cards navigate", d.Contains("DiscordCard_Click", StringComparison.Ordinal)
        && d.Contains("SteamCard_Click", StringComparison.Ordinal)
        && d.Contains("InternetCard_Click", StringComparison.Ordinal)
        && d.Contains("NvidiaCard_Click", StringComparison.Ordinal));
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
    Expect("home decluttered",
        !d.Contains("SoonCards", StringComparison.Ordinal)
        && !d.Contains("Coming soon", StringComparison.Ordinal));
    Expect("Riot and Epic are live dashboard modules",
        d.Contains("RiotCard_Click", StringComparison.Ordinal) &&
        d.Contains("EpicCard_Click", StringComparison.Ordinal) &&
        d.Contains("RiotStatusTag", StringComparison.Ordinal) &&
        d.Contains("EpicStatusTag", StringComparison.Ordinal));
    Expect("home has verified status tags", d.Contains("StatusTag", StringComparison.Ordinal));
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
    Expect("settings appearance removed", !s.Contains("Appearance", StringComparison.Ordinal));
    Expect("settings updates", s.Contains("UPDATES", StringComparison.Ordinal) || s.Contains("Updates", StringComparison.Ordinal));
    Expect("settings app version", s.Contains("AppVersion", StringComparison.Ordinal)
        && s.Contains("App behavior and support", StringComparison.Ordinal)
        && !s.Contains("KitVersion", StringComparison.Ordinal));
    Expect("settings no theme controls",
        !s.Contains("DarkMode_Click", StringComparison.Ordinal)
        && !s.Contains("LightMode_Click", StringComparison.Ordinal)
        && !s.Contains("IsDarkMode", StringComparison.Ordinal)
        && !s.Contains("IsLightMode", StringComparison.Ordinal));
    Expect("settings exo chrome",
        s.Contains("ExoQuietButton", StringComparison.Ordinal)
        && !s.Contains("ExoPrimaryButton", StringComparison.Ordinal));
    Expect("settings compact flat hierarchy",
        s.Contains("Text=\"Settings\"", StringComparison.Ordinal)
        && !s.Contains("Text=\"Appearance\"", StringComparison.Ordinal)
        && !s.Contains("SYSTEM CONTROL", StringComparison.Ordinal)
        && !s.Contains("No translucent layers", StringComparison.Ordinal)
        && !s.Contains("Content=\"AMOLED\"", StringComparison.Ordinal));
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
    var typeSource = File.Exists(typeTokens) ? File.ReadAllText(typeTokens) : string.Empty;
    var metricSource = File.Exists(metricTokens) ? File.ReadAllText(metricTokens) : string.Empty;
    Expect("theme ExoPrimaryButton", t.Contains("ExoPrimaryButton", StringComparison.Ordinal));
    Expect("primary Apply button is white with dark text",
        t.Contains("Property=\"Background\" Value=\"{ThemeResource ExoAccentBrush}\"", StringComparison.Ordinal)
        && t.Contains("Property=\"Foreground\" Value=\"{ThemeResource ExoOnAccentBrush}\"", StringComparison.Ordinal)
        && !t.Contains("ExoPrimaryButtonFillBrush", StringComparison.Ordinal));
    Expect("theme ExoGlassCircle",
        t.Contains("ExoGlassCircle", StringComparison.Ordinal)
        && t.Contains("ExoPillRadius", StringComparison.Ordinal)
        && t.Contains("ExoGlassCircleFillBrush", StringComparison.Ordinal));
    Expect("native variable UI typography",
        typeSource.Contains("Segoe UI Variable Text", StringComparison.Ordinal)
        && typeSource.Contains("Segoe UI Variable Display", StringComparison.Ordinal)
        && !typeSource.Contains("PlusJakartaSans.ttf", StringComparison.Ordinal));
    Expect("theme ExoWhiteButton", t.Contains("ExoWhiteButton", StringComparison.Ordinal));
    Expect("theme ExoCardButton", t.Contains("ExoCardButton", StringComparison.Ordinal));
    Expect("theme ExoFeatureTile", t.Contains("ExoFeatureTile", StringComparison.Ordinal));
    Expect("theme ExoActionBar", t.Contains("ExoActionBar", StringComparison.Ordinal));
    Expect("theme compact message banners",
        t.Contains("ExoMessageText", StringComparison.Ordinal)
        && t.Contains("ExoInfoMessageText", StringComparison.Ordinal)
        && t.Contains("Property=\"Padding\" Value=\"10,6\"", StringComparison.Ordinal));
    Expect("theme ExoIconWell", t.Contains("ExoIconWell", StringComparison.Ordinal));
    Expect("theme ExoPagePadding", metricSource.Contains("ExoPagePadding", StringComparison.Ordinal));
    Expect("theme choice style removed", !t.Contains("ExoThemeChoice", StringComparison.Ordinal));
    Expect("decorative italic removed", !t.Contains("ExoDisplayFontItalic", StringComparison.Ordinal)
        && !typeSource.Contains("FontStyle=\"Italic\"", StringComparison.Ordinal));
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

// v3 SharedModulePlate hosts loader + action bar + feature grid for optimizers.
var sharedPlateXaml = Path.Combine(repo, "Exo", "Views", "Controls", "SharedModulePlate.xaml");
var sharedPlateCs = Path.Combine(repo, "Exo", "Views", "Controls", "SharedModulePlate.xaml.cs");
Expect("SharedModulePlate control", File.Exists(sharedPlateXaml) && File.Exists(sharedPlateCs));
if (File.Exists(sharedPlateXaml))
{
    var plate = File.ReadAllText(sharedPlateXaml);
    Expect("SharedModulePlate instrument chrome",
        plate.Contains("ExoModulePlate", StringComparison.Ordinal)
        && plate.Contains("ExoLoader", StringComparison.Ordinal)
        && plate.Contains("ExoActionBar", StringComparison.Ordinal)
        && plate.Contains("FeatureTileGrid", StringComparison.Ordinal));
    Expect("SharedModulePlate normal-flow actions",
        plate.Contains("One normal-flow work surface", StringComparison.Ordinal)
        && plate.Contains("WHAT EXO WILL CHANGE", StringComparison.Ordinal)
        && (plate.Contains("Hardware-aware, reversible", StringComparison.Ordinal)
            || plate.Contains("Hardware-aware", StringComparison.Ordinal))
        && plate.Contains("Reading this PC", StringComparison.Ordinal)
        && plate.Contains("Actions unlock when detection finishes", StringComparison.Ordinal)
        && plate.Contains("InverseBoolToVisibilityConverter", StringComparison.Ordinal));
    Expect("SharedModulePlate advisor + report",
        plate.Contains("GuidanceText", StringComparison.Ordinal)
        && plate.Contains("ApplyReportRows", StringComparison.Ordinal));
}
if (File.Exists(sharedPlateCs))
{
    var plateCs = File.ReadAllText(sharedPlateCs);
    Expect("SharedModulePlate FeatureTileGrid accessor",
        plateCs.Contains("FeatureTileGrid", StringComparison.Ordinal)
        && plateCs.Contains("FeatureGrid", StringComparison.Ordinal));
}

foreach (var page in new[]
         {
             "DiscordOptimizerPage.xaml", "SteamOptimizerPage.xaml", "InternetOptimizerPage.xaml",
             "NvidiaOptimizerPage.xaml", "RiotOptimizerPage.xaml", "EpicOptimizerPage.xaml"
         })
{
    var p = Path.Combine(repo, "Exo", "Views", page);
    if (!File.Exists(p)) continue;
    var x = File.ReadAllText(p);
    Expect(page + " CTA", x.Contains("ExoPrimaryButton", StringComparison.Ordinal) || x.Contains("ExoQuietButton", StringComparison.Ordinal));
    // Module chrome: SharedModulePlate (v3) or legacy plate / page pad.
    Expect(page + " page padding",
        x.Contains("SharedModulePlate", StringComparison.Ordinal)
        || x.Contains("ExoModulePlate", StringComparison.Ordinal)
        || x.Contains("ExoPagePadding", StringComparison.Ordinal)
        || x.Contains("ExoPageMaxWidth", StringComparison.Ordinal));
    Expect(page + " uses SharedModulePlate", x.Contains("SharedModulePlate", StringComparison.Ordinal));
    Expect(page + " no ProgressRing", !x.Contains("<ProgressRing", StringComparison.Ordinal));
    if (page.StartsWith("Internet", StringComparison.Ordinal))
    {
        Expect("internet unified analyze apply",
            x.Contains("Analyze &amp; Apply", StringComparison.Ordinal)
            && !x.Contains("Low latency", StringComparison.Ordinal)
            && !x.Contains("Highest download", StringComparison.Ordinal));
        Expect("internet DNS is automatic",
            !x.Contains("Private DNS", StringComparison.Ordinal)
            && !x.Contains("DNS toggle", StringComparison.OrdinalIgnoreCase));
        Expect("internet Repair button", x.Contains("Content=\"Repair\"", StringComparison.Ordinal));
        // Proof layer: benchmark delta, rollback banner, honest Repair caption.
        Expect("internet proof layer",
            !x.Contains("BenchmarkSummary", StringComparison.Ordinal)
            && x.Contains("QualitySummary", StringComparison.Ordinal)
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

// Feature tiles: responsive two-column layout; the module owns scrolling.
var featureGridXaml = Path.Combine(repo, "Exo", "Views", "Controls", "FeatureTileGrid.xaml");
var featureGridCs = Path.Combine(repo, "Exo", "Views", "Controls", "FeatureTileGrid.xaml.cs");
Expect("FeatureTileGrid control", File.Exists(featureGridXaml) && File.Exists(featureGridCs));
if (File.Exists(featureGridXaml))
{
    var fg = File.ReadAllText(featureGridXaml);
    Expect("feature grid stretch host", fg.Contains("HorizontalAlignment=\"Stretch\"", StringComparison.Ordinal));
    Expect("feature grid responsive layout",
        fg.Contains("UniformGridLayout", StringComparison.Ordinal)
        && fg.Contains("MinItemWidth=\"360\"", StringComparison.Ordinal)
        && fg.Contains("MinColumnSpacing=\"12\"", StringComparison.Ordinal)
        && fg.Contains("ItemsStretch=\"Fill\"", StringComparison.Ordinal));
    Expect("feature grid delegates scrolling", !fg.Contains("<ScrollViewer", StringComparison.Ordinal));
}
if (File.Exists(featureGridCs))
{
    var fgc = File.ReadAllText(featureGridCs);
    Expect("feature grid no manual width sync",
        !fgc.Contains("TileRepeater.Width", StringComparison.Ordinal)
        && !fgc.Contains("ScrollHost_SizeChanged", StringComparison.Ordinal));
}
foreach (var page in new[]
         {
             "DiscordOptimizerPage.xaml", "SteamOptimizerPage.xaml",
             "NvidiaOptimizerPage.xaml", "RiotOptimizerPage.xaml", "EpicOptimizerPage.xaml"
         })
{
    var p = Path.Combine(repo, "Exo", "Views", page);
    if (!File.Exists(p)) continue;
    var x = File.ReadAllText(p);
    // Features bind into SharedModulePlate.FeatureItems (grid lives in the plate).
    Expect(page + " binds feature items to plate",
        x.Contains("FeatureItems=", StringComparison.Ordinal)
        && x.Contains("SharedModulePlate", StringComparison.Ordinal)
        && !x.Contains("x:Name=\"FeatureRepeater\"", StringComparison.Ordinal));
    Expect(page + " plate motion host",
        x.Contains("x:Name=\"Plate\"", StringComparison.Ordinal));
}

var internetDensityXaml = Path.Combine(repo, "Exo", "Views", "InternetOptimizerPage.xaml");
if (File.Exists(internetDensityXaml))
{
    var ix = File.ReadAllText(internetDensityXaml);
    Expect("internet decluttered action density",
        ix.Contains("Content=\"Analyze &amp; Apply\"", StringComparison.Ordinal)
        && ix.Contains("Content=\"Repair\"", StringComparison.Ordinal)
        && !ix.Contains("Content=\"Refresh\"", StringComparison.Ordinal)
        && ix.Contains("IsFeatureListVisible=\"{x:Bind ViewModel.IsFeatureListVisible, Mode=OneWay}\"", StringComparison.Ordinal)
        && ix.Contains("HasApplyReport=\"False\"", StringComparison.Ordinal)
        && ix.Contains("FeatureItems=\"{x:Bind ViewModel.Rows, Mode=OneWay}\"", StringComparison.Ordinal)
        && !ix.Contains("ExoInternetActionGrid", StringComparison.Ordinal));
    Expect("internet compact honest messages",
        ix.Contains("RollbackNotice", StringComparison.Ordinal)
        && ix.Contains("Style=\"{StaticResource ExoMessageText}\"", StringComparison.Ordinal)
        && (ix.Contains("HasMessage", StringComparison.Ordinal)
            || ix.Contains("ExoInfoMessageText", StringComparison.Ordinal)));
}

// Friction-free apply/repair: no blocking ContentDialog confirmations on module pages
// (update consent dialogs live in ExoUpdateDialog only), and every module plays the
// staggered feature-tile entrance on its first loading → loaded transition.
foreach (var page in new[]
         {
             "DiscordOptimizerPage", "SteamOptimizerPage", "NvidiaOptimizerPage",
             "RiotOptimizerPage", "EpicOptimizerPage"
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
        && code.Contains("IsFeatureListVisible", StringComparison.Ordinal)
        && code.Contains("Plate.FeatureTileGrid", StringComparison.Ordinal));
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
var launcherVmPath = Path.Combine(repo, "Exo", "ViewModels", "GameLauncherOptimizerViewModel.cs");
if (File.Exists(launcherVmPath))
{
    var launcherVm = File.ReadAllText(launcherVmPath);
    Expect("Riot/Epic shared VM has no confirm gate", !launcherVm.Contains("ConfirmAsync", StringComparison.Ordinal));
    Expect("Riot/Epic shared VM wires Apply and exact Repair",
        launcherVm.Contains("RiotOptimizerScript", StringComparison.Ordinal) &&
        launcherVm.Contains("EpicOptimizerScript", StringComparison.Ordinal) &&
        launcherVm.Contains("RiotRepairScript", StringComparison.Ordinal) &&
        launcherVm.Contains("EpicRepairScript", StringComparison.Ordinal));
}

// Detailed last-apply reports stay on Discord / Steam. Internet is intentionally
// reduced to its quality result plus Apply / Repair; NVIDIA never fakes report data.
foreach (var page in new[] { "DiscordOptimizerPage", "SteamOptimizerPage", "RiotOptimizerPage", "EpicOptimizerPage" })
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
    Expect("steam retired trim stats row removed",
        !sx.Contains("TrimStatsText", StringComparison.Ordinal)
        && !sx.Contains("HasTrimStats", StringComparison.Ordinal));
}
var nvidiaXamlPath = Path.Combine(repo, "Exo", "Views", "NvidiaOptimizerPage.xaml");
if (File.Exists(nvidiaXamlPath))
{
    var nx = File.ReadAllText(nvidiaXamlPath);
    // NVIDIA Repair is status-clear only — honest caption, no rollback claim.
    Expect("nvidia repair honest caption",
        nx.Contains("Repair restores the complete NVIDIA profile database", StringComparison.Ordinal)
        && nx.Contains("Drivers and display settings stay untouched", StringComparison.Ordinal));
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
        && mc.Contains("TrySetWindowIcon", StringComparison.Ordinal));
    Expect("startup does not rewrite Start Menu shortcut",
        !mc.Contains("TryRepairStartMenuShortcut", StringComparison.Ordinal)
        && !mc.Contains("WScript.Shell", StringComparison.Ordinal));
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
        && sx.Contains("Exo.ico", StringComparison.Ordinal)
        && sx.Contains("CreateStartMenuShortcut", StringComparison.Ordinal));
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

// Dashboard cards fill their responsive cells; content alignment remains stretched.
if (File.Exists(theme))
{
    var tCard = File.ReadAllText(theme);
    var cardIdx = tCard.IndexOf("ExoCardButton", StringComparison.Ordinal);
    var cardSlice = cardIdx >= 0 ? tCard.Substring(cardIdx, Math.Min(800, tCard.Length - cardIdx)) : "";
    Expect("card button fills dashboard cell",
        cardIdx >= 0
        && cardSlice.Contains("HorizontalAlignment", StringComparison.Ordinal)
        && cardSlice.Contains("Value=\"Stretch\"", StringComparison.Ordinal)
        && !cardSlice.Contains("Value=\"Left\"", StringComparison.Ordinal));
}

// Version gate
var versionFile = Path.Combine(repo, "VERSION");
var csproj = Path.Combine(repo, "Exo", "Exo.csproj");
// Version-agnostic: assert the two stamps agree with each other and look like semver,
// so a version bump never requires editing this test (Bump-Version.ps1 only touches csproj).
var csprojText = File.Exists(csproj) ? File.ReadAllText(csproj) : "";
var csprojVersion = System.Text.RegularExpressions.Regex.Match(csprojText, "<Version>([^<]+)</Version>").Groups[1].Value;
Expect("csproj has semver Version", System.Text.RegularExpressions.Regex.IsMatch(csprojVersion, @"^\d+\.\d+\.\d+$"),
    $"got=[{csprojVersion}]");
if (File.Exists(versionFile))
    Expect("VERSION matches csproj Version", File.ReadAllText(versionFile).Trim() == csprojVersion,
        $"VERSION=[{File.ReadAllText(versionFile).Trim()}] csproj=[{csprojVersion}]");

// Live advisor (realtime next-step coach on every optimizer)
var advisorPath = Path.Combine(repo, "Exo", "Services", "OptimizerAdvisor.cs");
Expect("OptimizerAdvisor exists", File.Exists(advisorPath));
if (File.Exists(advisorPath))
{
    var adv = File.ReadAllText(advisorPath);
    Expect("OptimizerAdvisor v2 BuildV2 present",
        adv.Contains("BuildV2", StringComparison.Ordinal) &&
        adv.Contains("Ready to measure this connection", StringComparison.Ordinal));
    Expect("OptimizerAdvisor hides internal checklist prose",
        !adv.Contains("CTA:", StringComparison.Ordinal)
        && !adv.Contains("Still open:", StringComparison.Ordinal)
        && !adv.Contains("aggressive pack", StringComparison.Ordinal));
    Expect("OptimizerAdvisor covers all modules",
        adv.Contains("\"Internet\"", StringComparison.Ordinal)
        && adv.Contains("\"Discord\"", StringComparison.Ordinal)
        && adv.Contains("\"Steam\"", StringComparison.Ordinal)
        && adv.Contains("\"NVIDIA\"", StringComparison.Ordinal));
}
// Wave-2 shared script libs
Expect("Exo.Common.ps1 shared lib",
    File.Exists(Path.Combine(repo, "Exo", "Scripts", "lib", "Exo.Common.ps1")));
Expect("Exo.NoBackground.ps1 shared lib",
    File.Exists(Path.Combine(repo, "Exo", "Scripts", "lib", "Exo.NoBackground.ps1")));
var steamRun = File.ReadAllText(Path.Combine(repo, "Exo", "Scripts", "Steam", "Exo-Steam-Run.ps1"));
Expect("Steam Run wires shared libs",
    steamRun.Contains("Exo.Common.ps1", StringComparison.Ordinal) &&
    steamRun.Contains("Unregister-ExoBackground", StringComparison.Ordinal));
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
        && !svm.Contains("ThemeSwitchHint", StringComparison.Ordinal)
        && !svm.Contains("IsLightMode", StringComparison.Ordinal)
        && !svm.Contains("IsDarkMode", StringComparison.Ordinal));
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
    Expect("home NVIDIA policy is explicitly named",
        dvm.Contains("Raw-latency profile", StringComparison.Ordinal)
        && dvm.Contains("G-SYNC/VRR profile", StringComparison.Ordinal)
        && !dvm.Contains("Auto raw-latency path", StringComparison.Ordinal));
    Expect("home internet metrics are current samples, not causal deltas",
        dvm.Contains("ms idle", StringComparison.Ordinal)
        && dvm.Contains("ms jitter", StringComparison.Ordinal)
        && !dvm.Contains("BeforeP50Ms:0.0}→", StringComparison.Ordinal)
        && !dvm.Contains("vs before", StringComparison.Ordinal));
    Expect("windows coming soon card", dvm.Contains("Card(\"windows\"", StringComparison.Ordinal)
        && dvm.Contains("windows.png", StringComparison.Ordinal));
}
var homeDashReader = Path.Combine(repo, "Exo", "Services", "HomeDashboardReader.cs");
if (File.Exists(homeDashReader))
{
    var hdr = File.ReadAllText(homeDashReader);
    Expect("home does not surface retired Steam trim stats",
        !hdr.Contains("steam-trim-stats.json", StringComparison.Ordinal)
        && !hdr.Contains("TryReadTrimStats", StringComparison.Ordinal));
    Expect("home live memory api",
        hdr.Contains("GlobalMemoryStatusEx", StringComparison.Ordinal)
        && hdr.Contains("TryReadMemory", StringComparison.Ordinal));
    Expect("home latency file read", hdr.Contains("TryReadLatency", StringComparison.Ordinal));
    Expect("home nvidia path file read",
        hdr.Contains("TryReadNvidiaPath", StringComparison.Ordinal)
        && hdr.Contains("nvidia-optimizer.json", StringComparison.Ordinal));
    Expect("home discord reclaim sample",
        hdr.Contains("TrySampleDiscordRam", StringComparison.Ordinal)
        && hdr.Contains("discord-ram-stats.json", StringComparison.Ordinal));
    Expect("home link speed read",
        hdr.Contains("TryReadPrimaryLinkSpeed", StringComparison.Ordinal));
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

Expect("retired custom NVIDIA panel removed",
    !File.Exists(Path.Combine(repo, "Exo", "ViewModels", "NvidiaPanelViewModel.cs")) &&
    !File.Exists(Path.Combine(repo, "Exo", "Views", "NvidiaPanelPage.xaml")));

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
    Expect("optimizer card parser preserves detector detail",
        h.Contains("string.IsNullOrWhiteSpace(detail)", StringComparison.Ordinal));
    var optimizerViewModels = new[] { "DiscordOptimizerViewModel.cs", "SteamOptimizerViewModel.cs", "NvidiaOptimizerViewModel.cs" }
        .Select(name => Path.Combine(repo, "Exo", "ViewModels", name));
    Expect("optimizer cards render detector detail",
        optimizerViewModels.All(File.Exists) &&
        optimizerViewModels.All(path => File.ReadAllText(path).Contains("Detail = feature.Detail", StringComparison.Ordinal)));
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
#pragma warning restore CA1416
#endif
