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

Expect("files", File.Exists(appXaml) && File.Exists(main));
Expect("dead DashboardPage removed", !File.Exists(dash));
var wwwIndex = Path.Combine(repo, "Exo", "wwwroot", "index.html");
var exoCsproj = Path.Combine(repo, "Exo", "Exo.csproj");
Expect("wwwroot index present", File.Exists(wwwIndex));
if (File.Exists(exoCsproj))
{
    var exoProj = File.ReadAllText(exoCsproj);
    Expect("wwwroot always content-included",
        exoProj.Contains("Content Include=\"wwwroot\\", StringComparison.Ordinal)
        && exoProj.Contains("EnsureWwwRootPacked", StringComparison.Ordinal));
}
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
    // Kits may warm after first frame (Task.Run + WarmInBackground). PowerShell
    // runtime install still must not start at composition-root time.
    Expect("startup performs no dependency bootstrap",
        !servicesSource.Contains("EnsurePowerShellRuntimeAsync", StringComparison.Ordinal)
        && (servicesSource.Contains("WarmInBackground", StringComparison.Ordinal)
            || !servicesSource.Contains("Task.Run", StringComparison.Ordinal)));
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

    var webBridgeCs = Path.Combine(repo, "Exo", "Services", "WebHostBridge.cs");
    var nvidiaPanelCs = Path.Combine(repo, "Exo", "Services", "NvidiaPanelSettingsService.cs");
    var networkOptCs = Path.Combine(repo, "Exo", "Services", "NetworkOptimizerService.cs");
    Expect("Apply and Repair opt in to dependency preparation",
        File.Exists(webBridgeCs) && File.Exists(nvidiaPanelCs) && File.Exists(networkOptCs)
        && File.ReadAllText(webBridgeCs).Contains("ensureRuntime: needPwshBootstrap", StringComparison.Ordinal)
        && File.ReadAllText(nvidiaPanelCs).Contains("ensureRuntime: true", StringComparison.Ordinal)
        && File.ReadAllText(networkOptCs).Contains("ensureRuntime: true", StringComparison.Ordinal));

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
    Expect("dark page token", colors.Contains("<Color x:Key=\"ExoColorPage\">#000000</Color>", StringComparison.Ordinal));
    Expect("stone white primary token", colors.Contains("<Color x:Key=\"ExoColorPrimaryText\">#F2F2F0</Color>", StringComparison.Ordinal));
    Expect("discord brand blurple", colors.Contains("<Color x:Key=\"ExoColorDiscord\">#5865F2</Color>", StringComparison.Ordinal));
    Expect("no dead launcher brand colors",
        !colors.Contains("ExoColorRiot", StringComparison.Ordinal)
        && !colors.Contains("ExoColorEpic", StringComparison.Ordinal));
    Expect("light theme removed", !colors.Contains("x:Key=\"Light\"", StringComparison.Ordinal)
        && !a.Contains("x:Key=\"Light\"", StringComparison.Ordinal));
    Expect("High Contrast dictionary", colors.Contains("x:Key=\"HighContrast\"", StringComparison.Ordinal)
        && colors.Contains("SystemColorWindowBrush", StringComparison.Ordinal));
    Expect("token dictionaries merged", a.Contains("Styles/Tokens.Colors.xaml", StringComparison.Ordinal)
        && a.Contains("Styles/Tokens.Type.xaml", StringComparison.Ordinal)
        && a.Contains("Styles/Tokens.Metrics.xaml", StringComparison.Ordinal));
    Expect("dark solid card lift", colors.Contains("#0E0E0E", StringComparison.Ordinal)
        && colors.Contains("#0A0A0A", StringComparison.Ordinal));
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
        && metrics.Contains("ExoPageMaxWidth\">1160", StringComparison.Ordinal));

    // Product chrome may keep a few literal hex values that match the React shell
    // (WebView canvas + caption button states). Everything else stays tokenized.
    var hexAllow = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Path.Combine("Exo", "MainWindow.xaml"),
        Path.Combine("Exo", "Styles", "ThemeResources.xaml"),
    };
    var xamlFiles = Directory.EnumerateFiles(Path.Combine(repo, "Exo"), "*.xaml", SearchOption.AllDirectories)
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        .ToArray();
    var hexOutsideTokens = xamlFiles
        .Where(path => !Path.GetFullPath(path).Equals(Path.GetFullPath(colorTokens), StringComparison.OrdinalIgnoreCase))
        .Where(path => !hexAllow.Contains(Path.GetRelativePath(repo, path)))
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
    // Full-bleed WebView2 product shell (3.16.10+). Nav/settings/captions live in React.
    // Native overlay is only a thin drag strip — never rewrite UI to satisfy this smoke.
    Expect("webview2 product host",
        m.Contains("<WebView2 x:Name=\"WebHost\"", StringComparison.Ordinal)
        && m.Contains("x:Name=\"ContentHost\"", StringComparison.Ordinal)
        && m.Contains("Margin=\"0\"", StringComparison.Ordinal)
        && m.Contains("HorizontalAlignment=\"Stretch\"", StringComparison.Ordinal));
    Expect("native title bar (no custom caption chrome)",
        !m.Contains("x:Name=\"TitleChrome\"", StringComparison.Ordinal)
        && !m.Contains("x:Name=\"AppTitleBar\"", StringComparison.Ordinal)
        && !m.Contains("<TitleBar", StringComparison.Ordinal)
        && !m.Contains("CaptionSpacerHost", StringComparison.Ordinal)
        && !m.Contains("TitleBarDragRegion", StringComparison.Ordinal));
    Expect("legacy chrome stubs collapsed",
        m.Contains("x:Name=\"ExoBrandPill\"", StringComparison.Ordinal)
        && m.Contains("x:Name=\"NavHome\"", StringComparison.Ordinal)
        && m.Contains("SettingsButton", StringComparison.Ordinal)
        && !m.Contains("Text=\"EXO\"", StringComparison.Ordinal));
    Expect("settings gear", m.Contains("SettingsButton", StringComparison.Ordinal));
    Expect("dead back chrome removed", !m.Contains("x:Name=\"BackButton\"", StringComparison.Ordinal));
    Expect("no NavigationView", !m.Contains("<NavigationView", StringComparison.Ordinal));
    Expect("dead native nav rail removed — React Shell owns module nav",
        !m.Contains("x:Name=\"NavRail\"", StringComparison.Ordinal)
        && !m.Contains("x:Name=\"ModuleIcons\"", StringComparison.Ordinal)
        && !m.Contains("NavDiscord", StringComparison.Ordinal)
        && !m.Contains("NavRiot", StringComparison.Ordinal)
        && !m.Contains("NavEpic", StringComparison.Ordinal));
    Expect("dead ContentFrame removed — WebView2 is the only content host",
        !m.Contains("ContentFrame", StringComparison.Ordinal));
    Expect("no tooltips in main", !m.Contains("ToolTip", StringComparison.OrdinalIgnoreCase));
}
// The brain UI: the whole app is OrbApp (BrainOrb + conversation) — no shell,
// no router, no module grid. Old Shell/ModulePage/GamesPage/HomePage must stay gone.
var orbApp = Path.Combine(repo, "ui", "src", "pages", "OrbApp.tsx");
var brainOrb = Path.Combine(repo, "ui", "src", "components", "BrainOrb.tsx");
Expect("brain UI present", File.Exists(orbApp) && File.Exists(brainOrb));
Expect("old shell layer removed",
    !File.Exists(Path.Combine(repo, "ui", "src", "components", "Shell.tsx"))
    && !File.Exists(Path.Combine(repo, "ui", "src", "components", "SettingsDrawer.tsx"))
    && !File.Exists(Path.Combine(repo, "ui", "src", "pages", "HomePage.tsx"))
    && !File.Exists(Path.Combine(repo, "ui", "src", "pages", "ModulePage.tsx"))
    && !File.Exists(Path.Combine(repo, "ui", "src", "pages", "GamesPage.tsx"))
    && !File.Exists(Path.Combine(repo, "ui", "src", "pages", "ReelApp.tsx")));
if (File.Exists(orbApp))
{
    var orb = File.ReadAllText(orbApp);
    Expect("brain talks through real host bridge",
        orb.Contains("host.verifyAll()", StringComparison.Ordinal)
        && orb.Contains("host.apply(", StringComparison.Ordinal)
        && orb.Contains("host.getLive()", StringComparison.Ordinal));
    Expect("brain asks before acting (Skip/Stop answer chips)",
        orb.Contains("'Skip'", StringComparison.Ordinal)
        && orb.Contains("'Stop'", StringComparison.Ordinal)
        && orb.Contains("'reapply'", StringComparison.Ordinal));
    Expect("brain has no in-app captions (native title bar)",
        !orb.Contains("host.minimize()", StringComparison.Ordinal)
        && !orb.Contains("host.close()", StringComparison.Ordinal));
}
var uiPkg = Path.Combine(repo, "ui", "package.json");
if (File.Exists(uiPkg))
{
    var pkg = File.ReadAllText(uiPkg);
    Expect("self-hosted fonts bundled",
        pkg.Contains("@fontsource/space-grotesk", StringComparison.Ordinal)
        && pkg.Contains("@fontsource/jetbrains-mono", StringComparison.Ordinal));
    Expect("dead UI deps removed",
        !pkg.Contains("react-router-dom", StringComparison.Ordinal)
        && !pkg.Contains("framer-motion", StringComparison.Ordinal)
        && !pkg.Contains("thinking-orbs", StringComparison.Ordinal)
        && !pkg.Contains("tailwindcss", StringComparison.Ordinal));
}
// Thin native drag strip + WebView bridge — captions/nav are React.
var mainCs = Path.Combine(repo, "Exo", "MainWindow.xaml.cs");
if (File.Exists(mainCs))
{
    var cs = File.ReadAllText(mainCs);
    Expect("native title bar wired",
        cs.Contains("ExtendsContentIntoTitleBar = false", StringComparison.Ordinal)
        && cs.Contains("hasTitleBar: true", StringComparison.Ordinal)
        && !cs.Contains("SetTitleBar(AppTitleBar)", StringComparison.Ordinal));
    Expect("fixed shell size", cs.Contains("IsResizable = false", StringComparison.Ordinal)
        && cs.Contains("IsMaximizable = false", StringComparison.Ordinal)
        && cs.Contains("FixedWindowWidth", StringComparison.Ordinal)
        && cs.Contains("FixedWindowHeight", StringComparison.Ordinal)
        && cs.Contains("1200", StringComparison.Ordinal)
        && cs.Contains("800", StringComparison.Ordinal));
    Expect("webview bridge shell",
        cs.Contains("WebHostBridge", StringComparison.Ordinal)
        && cs.Contains("EnsureWebAsync", StringComparison.Ordinal)
        && cs.Contains("NavigateWebHash", StringComparison.Ordinal));
    Expect("dead titlebar fields removed", !cs.Contains("AppTitleText", StringComparison.Ordinal)
        && !cs.Contains("CaptionSpacerHost", StringComparison.Ordinal)
        && !cs.Contains("UpdateRailSelection", StringComparison.Ordinal));
}
if (File.Exists(dash))
{
    var d = File.ReadAllText(dash);
    // Dense home: machine strip + 2×2 meters (bars only) + optimizer chips.
    Expect("hero status identity",
        d.Contains("HeroBrand", StringComparison.Ordinal)
        && d.Contains("OverviewPrimary", StringComparison.Ordinal)
        && d.Contains("THIS PC", StringComparison.Ordinal));
    Expect("hero tagline",
        d.Contains("HeroTagline", StringComparison.Ordinal)
        && d.Contains("OverviewPrimary", StringComparison.Ordinal));
    Expect("home instrument plate", d.Contains("ExoModulePlate", StringComparison.Ordinal));
    Expect("home optimizer chips",
        d.Contains("CheckRows", StringComparison.Ordinal)
        && d.Contains("CheckRow_Click", StringComparison.Ordinal)
        && d.Contains("OPTIMIZERS", StringComparison.Ordinal)
        && d.Contains("UniformGridLayout", StringComparison.Ordinal));
    Expect("home applied summary",
        d.Contains("OverviewPrimary", StringComparison.Ordinal));
    Expect("home live system specs panel",
        d.Contains("SpecsCpu", StringComparison.Ordinal)
        && d.Contains("SpecsGpu", StringComparison.Ordinal)
        && d.Contains("SpecsRam", StringComparison.Ordinal)
        && d.Contains("SpecsOs", StringComparison.Ordinal));
    Expect("home live memory meter",
        d.Contains("MemoryPrimary", StringComparison.Ordinal)
        && d.Contains("MEMORY", StringComparison.Ordinal)
        && d.Contains("MemoryLoadPercent", StringComparison.Ordinal)
        && d.Contains("<ProgressBar", StringComparison.Ordinal));
    Expect("home live cpu meter",
        d.Contains("CpuPrimary", StringComparison.Ordinal)
        && d.Contains("CpuLoadPercent", StringComparison.Ordinal)
        && d.Contains("Text=\"CPU\"", StringComparison.Ordinal)
        && d.Contains("Text=\"Load\"", StringComparison.Ordinal));
    Expect("home live gpu meter",
        d.Contains("GpuPrimary", StringComparison.Ordinal)
        && d.Contains("GpuLoadPercent", StringComparison.Ordinal)
        && d.Contains("ExoNvidiaBrush", StringComparison.Ordinal));
    Expect("home no dead flat sparklines",
        !d.Contains("ExoSparkline", StringComparison.Ordinal)
        && !d.Contains("TileDram", StringComparison.Ordinal)
        && !d.Contains("Link health", StringComparison.Ordinal));
    Expect("home network meter",
        d.Contains("NETWORK", StringComparison.Ordinal)
        && d.Contains("NetPrimary", StringComparison.Ordinal)
        && d.Contains("NetSecondary", StringComparison.Ordinal)
        && d.Contains("ExoInternetBrush", StringComparison.Ordinal));
    Expect("home 2x2 meter grid",
        d.Contains("TileRam", StringComparison.Ordinal)
        && d.Contains("TileCpu", StringComparison.Ordinal)
        && d.Contains("TileGpu", StringComparison.Ordinal)
        && d.Contains("TileNet", StringComparison.Ordinal));
    Expect("home no redundant essay stack",
        !d.Contains("Optimization status", StringComparison.Ordinal)
        && !d.Contains("AppliedModulesList", StringComparison.Ordinal)
        && !d.Contains("detects this PC first", StringComparison.Ordinal));
    Expect("home consistent plate padding",
        d.Contains("Padding=\"14\"", StringComparison.Ordinal)
        || d.Contains("Padding=\"14,12\"", StringComparison.Ordinal));
    var motionPath = Path.Combine(repo, "Exo", "Helpers", "ExoMotion.cs");
    if (File.Exists(motionPath))
    {
        var mo = File.ReadAllText(motionPath);
        Expect("rich motion unlocked by default",
            mo.Contains("RichMotion", StringComparison.Ordinal)
            && mo.Contains("= true", StringComparison.Ordinal));
        Expect("motion never writes composition offset",
            !mo.Contains("ElementCompositionPreview", StringComparison.Ordinal)
            && !mo.Contains("Compositor", StringComparison.Ordinal));
    }
    Expect("home checklist rows clickable",
        d.Contains("CheckRow_Click", StringComparison.Ordinal));
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
    Expect("no pick-a-target blurb", !d.Contains("Pick a target", StringComparison.Ordinal));
}
// Checklist navigation + sequence live in code-behind / view model.
var dashPageCs = Path.Combine(repo, "Exo", "Views", "DashboardPage.xaml.cs");
if (File.Exists(dashPageCs))
{
    var dcs = File.ReadAllText(dashPageCs);
    Expect("home check sequence plays on navigate",
        dcs.Contains("PlayCheckSequenceAsync", StringComparison.Ordinal)
        && dcs.Contains("CheckRow_Click", StringComparison.Ordinal)
        && dcs.Contains("PlayResultPop", StringComparison.Ordinal));
    Expect("home checklist opens modules",
        dcs.Contains("NavigateToDiscord", StringComparison.Ordinal)
        && dcs.Contains("NavigateToSteam", StringComparison.Ordinal)
        && dcs.Contains("NavigateToInternet", StringComparison.Ordinal)
        && dcs.Contains("NavigateToNvidia", StringComparison.Ordinal)
        && dcs.Contains("NavigateToRiot", StringComparison.Ordinal)
        && dcs.Contains("NavigateToEpic", StringComparison.Ordinal));
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
// Settings: React drawer is the product surface; native flyout stubs remain for host glue.
if (File.Exists(mainXaml))
{
    var mx = File.ReadAllText(mainXaml);
    Expect("settings flyout stubs kept",
        mx.Contains("SettingsFlyout", StringComparison.Ordinal)
        && mx.Contains("SettingsSheetHost", StringComparison.Ordinal)
        && !mx.Contains("SettingsRail", StringComparison.Ordinal)
        && !mx.Contains("SettingsOverlay", StringComparison.Ordinal));
}
var updateDlg = Path.Combine(repo, "Exo", "Helpers", "ExoUpdateDialog.cs");
if (File.Exists(updateDlg))
{
    var u = File.ReadAllText(updateDlg);
    Expect("update dialog no loader", !u.Contains("ExoLoader", StringComparison.Ordinal));
    Expect("update dialog progress", u.Contains("ProgressBar", StringComparison.Ordinal)
        && (u.Contains("phaseTb", StringComparison.Ordinal) || u.Contains("pctTb", StringComparison.Ordinal) ||
            u.Contains("statusTb", StringComparison.Ordinal)));
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

// The legacy WinUI optimizer surface (SharedModulePlate, FeatureTileGrid, ExoLoader,
// ExoSparkline, per-module OptimizerPage.xaml, and the optimizer view-models) was
// deleted in the 2026 cleanup — the app is a WebView2 shell around the React UI
// (see "webview2 product host" above) and none of that XAML/VM tree was reachable
// at runtime. Assert it stays gone rather than re-testing content that no longer exists.
Expect("legacy optimizer XAML surface fully removed",
    !Directory.Exists(Path.Combine(repo, "Exo", "Views", "Controls"))
        || (!File.Exists(Path.Combine(repo, "Exo", "Views", "Controls", "SharedModulePlate.xaml"))
            && !File.Exists(Path.Combine(repo, "Exo", "Views", "Controls", "FeatureTileGrid.xaml"))
            && !File.Exists(Path.Combine(repo, "Exo", "Views", "Controls", "ExoLoader.xaml"))
            && !File.Exists(Path.Combine(repo, "Exo", "Views", "Controls", "ExoSparkline.xaml"))));
foreach (var deadPage in new[]
         {
             "DiscordOptimizerPage", "SteamOptimizerPage", "InternetOptimizerPage",
             "NvidiaOptimizerPage", "RiotOptimizerPage", "EpicOptimizerPage", "DashboardPage"
         })
{
    Expect(deadPage + " removed",
        !File.Exists(Path.Combine(repo, "Exo", "Views", deadPage + ".xaml"))
        && !File.Exists(Path.Combine(repo, "Exo", "Views", deadPage + ".xaml.cs")));
}
foreach (var deadVm in new[]
         {
             "DiscordOptimizerViewModel", "SteamOptimizerViewModel", "InternetOptimizerViewModel",
             "NvidiaOptimizerViewModel", "NvidiaPolicyRowViewModel", "ApplyReportRowViewModel",
             "GameLauncherOptimizerViewModel"
         })
{
    Expect(deadVm + " removed", !File.Exists(Path.Combine(repo, "Exo", "ViewModels", deadVm + ".cs")));
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
    // Product settings live in React SettingsDrawer; native flyout stubs stay wired for host glue.
    Expect("settings flyout host glue",
        mc.Contains("SettingsFlyout", StringComparison.Ordinal)
        && mc.Contains("ShowAttachedFlyout", StringComparison.Ordinal)
        && mc.Contains("SettingsFlyout_Opened", StringComparison.Ordinal)
        && mc.Contains("SettingsFlyout_Closing", StringComparison.Ordinal)
        && mc.Contains("PlayOpenAnimation", StringComparison.Ordinal)
        && mc.Contains("PlayCloseAnimation", StringComparison.Ordinal)
        && !mc.Contains("OpenSettingsRail", StringComparison.Ordinal)
        && !mc.Contains("SettingsRail", StringComparison.Ordinal));
    Expect("taskbar icon win32 set",
        mc.Contains("SendMessage", StringComparison.Ordinal) && mc.Contains("LoadImage", StringComparison.Ordinal)
        && mc.Contains("TrySetWindowIcon", StringComparison.Ordinal));
    Expect("startup does not rewrite Start Menu shortcut",
        !mc.Contains("TryRepairStartMenuShortcut", StringComparison.Ordinal)
        && !mc.Contains("WScript.Shell", StringComparison.Ordinal));
    Expect("navigate uses webview hash routes",
        mc.Contains("NavigateWebHash", StringComparison.Ordinal)
        && mc.Contains("#/module/", StringComparison.Ordinal)
        && mc.Contains("EnsureWebAsync", StringComparison.Ordinal));
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

// Full-bleed WebView host — React header owns settings inset, not native margins.
var mainWinCs = Path.Combine(repo, "Exo", "MainWindow.xaml.cs");
if (File.Exists(mainWinCs))
{
    var mwc = File.ReadAllText(mainWinCs);
    Expect("content host full-bleed stretch",
        mwc.Contains("ClearValue(FrameworkElement.WidthProperty)", StringComparison.Ordinal)
        && mwc.Contains("HorizontalAlignment.Stretch", StringComparison.Ordinal));
}
var mainWinXaml = Path.Combine(repo, "Exo", "MainWindow.xaml");
if (File.Exists(mainWinXaml))
{
    var mx = File.ReadAllText(mainWinXaml);
    Expect("content host full-bleed zero margin",
        mx.Contains("x:Name=\"ContentHost\"", StringComparison.Ordinal)
        && mx.Contains("Margin=\"0\"", StringComparison.Ordinal)
        && mx.Contains("<WebView2 x:Name=\"WebHost\"", StringComparison.Ordinal)
        && mx.Contains("x:Name=\"ExoBrandPill\"", StringComparison.Ordinal));
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

// Post-first-frame warm (kit stage + pwsh resolve) keeps first module open snappy.
var appServicesPath = Path.Combine(repo, "Exo", "Services", "AppServices.cs");
if (File.Exists(appServicesPath))
{
    var asrc = File.ReadAllText(appServicesPath);
    Expect("AppServices WarmInBackground present",
        asrc.Contains("WarmInBackground", StringComparison.Ordinal)
        && asrc.Contains("GetDiscordRoot", StringComparison.Ordinal)
        && asrc.Contains("WarmResolvePowerShell", StringComparison.Ordinal));
}
var mainCsWarm = Path.Combine(repo, "Exo", "MainWindow.xaml.cs");
if (File.Exists(mainCsWarm))
{
    var mc = File.ReadAllText(mainCsWarm);
    Expect("MainWindow starts optimizer warm after first frame",
        mc.Contains("WarmInBackground", StringComparison.Ordinal)
        && (mc.Contains("StartPostFirstFrameWork", StringComparison.Ordinal) ||
            mc.Contains("optimizer-warm-started", StringComparison.Ordinal)));
}

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
    Expect("OptimizerAdvisor singular copy for one open setting",
        adv.Contains("One setting is ready", StringComparison.Ordinal)
        || adv.Contains("One launcher setting is out of policy", StringComparison.Ordinal));
    Expect("OptimizerAdvisor rejects broken 1-settings grammar",
        !adv.Contains("{missingCount} settings are ready", StringComparison.Ordinal)
        || adv.Contains("missingCount == 1", StringComparison.Ordinal)
        || adv.Contains("One setting", StringComparison.Ordinal));
}

// Dashboard recommended-next deep-link state — the CTA itself is React now
// (HomePage/ModulePage), but DashboardViewModel still computes it for the
// WebHostBridge dashboard.get payload (next.id / next.label).
var nextActionVmPath = Path.Combine(repo, "Exo", "ViewModels", "DashboardViewModel.cs");
if (File.Exists(nextActionVmPath))
{
    var nextActionVm = File.ReadAllText(nextActionVmPath);
    Expect("dashboard NextAction state on view model",
        nextActionVm.Contains("HasNextAction", StringComparison.Ordinal)
        && nextActionVm.Contains("UpdateNextAction", StringComparison.Ordinal)
        && nextActionVm.Contains("NextActionModule", StringComparison.Ordinal));
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
    var brave = MeasureInkFill(Path.Combine(logosDir, "brave.png"));
    var internet = MeasureInkFill(Path.Combine(logosDir, "internet.png"));

    Log($"ink discord max={discord.MaxFill:F1}% steam={steam.MaxFill:F1}% nvidia={nvidia.MaxFill:F1}% brave={brave.MaxFill:F1}% internet={internet.MaxFill:F1}%");

    // Peer floor from real sibling marks — not a magic absolute expected %.
    var peerFloor = Math.Min(Math.Min(discord.MaxFill, steam.MaxFill), nvidia.MaxFill) * 0.70;
    Expect("brave ink peer weight", brave.MaxFill >= peerFloor && brave.MaxFill >= 70,
        $"brave={brave.MaxFill:F1} peerFloor={peerFloor:F1}");
    // Wi‑Fi mark is intentionally airy (minimal arcs) — lower absolute floor than solid icons.
    Expect("internet ink peer weight", internet.MaxFill >= Math.Min(peerFloor, 55) && internet.MaxFill >= 55,
        $"internet={internet.MaxFill:F1} peerFloor={peerFloor:F1}");
    // Minimal Wi‑Fi mark is wide arcs — height can sit just under 50% of canvas.
    Expect("internet not tiny", internet.FillH >= 42 && internet.FillW >= 55,
        $"fillW={internet.FillW:F1} fillH={internet.FillH:F1}");
#else
    Log("SKIP  logo ink measure (System.Drawing.Common Windows-only)");
    foreach (var name in new[] { "discord.png", "steam.png", "nvidia.png", "brave.png", "internet.png" })
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
    Expect("home applied modules list",
        dvm.Contains("AppliedModulesList", StringComparison.Ordinal)
        && dvm.Contains("RefreshSystemSpecs", StringComparison.Ordinal));
    Expect("home live cpu properties",
        dvm.Contains("CpuLoadPercent", StringComparison.Ordinal)
        && dvm.Contains("TryReadCpuLoadPercent", StringComparison.Ordinal));
    Expect("home checklist sequence",
        dvm.Contains("PlayCheckSequenceAsync", StringComparison.Ordinal)
        && dvm.Contains("OptimizerCheckRowViewModel", StringComparison.Ordinal)
        && dvm.Contains("Checking…", StringComparison.Ordinal));
    Expect("home live meter properties",
        dvm.Contains("MemoryLoadPercent", StringComparison.Ordinal)
        && dvm.Contains("CpuLoadPercent", StringComparison.Ordinal)
        && dvm.Contains("GpuLoadPercent", StringComparison.Ordinal)
        && dvm.Contains("NetMetricPercent", StringComparison.Ordinal)
        && dvm.Contains("PulseOpacity", StringComparison.Ordinal)
        && dvm.Contains("RamSeries", StringComparison.Ordinal));
    Expect("home NVIDIA policy is explicitly named",
        dvm.Contains("Raw-latency profile", StringComparison.Ordinal)
        && dvm.Contains("G-SYNC/VRR profile", StringComparison.Ordinal)
        && !dvm.Contains("Auto raw-latency path", StringComparison.Ordinal));
    Expect("home internet metrics are current samples, not causal deltas",
        dvm.Contains("ms idle", StringComparison.Ordinal)
        && dvm.Contains("ms jitter", StringComparison.Ordinal)
        && !dvm.Contains("BeforeP50Ms:0.0}→", StringComparison.Ordinal)
        && !dvm.Contains("vs before", StringComparison.Ordinal));
    Expect("dashboard module set is exactly the keeper six",
        dvm.Contains("Card(\"discord\"", StringComparison.Ordinal)
        && dvm.Contains("Card(\"brave\"", StringComparison.Ordinal)
        && dvm.Contains("Card(\"steam\"", StringComparison.Ordinal)
        && !dvm.Contains("Card(\"windows\"", StringComparison.Ordinal)
        && !dvm.Contains("Card(\"riot\"", StringComparison.Ordinal)
        && !dvm.Contains("Card(\"epic\"", StringComparison.Ordinal)
        && !dvm.Contains("Card(\"amd\"", StringComparison.Ordinal));
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
    Expect("home system specs api",
        hdr.Contains("TryReadSystemSpecs", StringComparison.Ordinal)
        && hdr.Contains("ProcessorNameString", StringComparison.Ordinal));
    Expect("home win11 build gate",
        hdr.Contains("ResolveOsLabel", StringComparison.Ordinal)
        && hdr.Contains("22000", StringComparison.Ordinal)
        && hdr.Contains("CurrentBuild", StringComparison.Ordinal));
    Expect("home live cpu api",
        hdr.Contains("TryReadCpuLoadPercent", StringComparison.Ordinal)
        && hdr.Contains("GetSystemTimes", StringComparison.Ordinal));
    Expect("home gpu and memory speed api",
        hdr.Contains("TryReadGpuLoadPercent", StringComparison.Ordinal)
        && hdr.Contains("TryReadMemorySpeedMhz", StringComparison.Ordinal));
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
Expect("dead-module logos removed",
    !File.Exists(Path.Combine(logosDir, "windows.png"))
    && !File.Exists(Path.Combine(logosDir, "riot.png"))
    && !File.Exists(Path.Combine(logosDir, "epic.png"))
    && !File.Exists(Path.Combine(logosDir, "amd.png")));
Expect("brave logo asset", File.Exists(Path.Combine(logosDir, "brave.png")));

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
