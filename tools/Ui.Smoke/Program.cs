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
var settingsSheet = Path.Combine(repo, "Exo", "Views", "Controls", "SettingsSheet.xaml");
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
var shippedClosureCs = Path.Combine(repo, "Exo", "Security", "ShippedArtifactClosureVerifier.cs");
var generatedManifestCs = Path.Combine(repo, "Exo", "Security", "ShippedScriptManifest.g.cs");

Expect("files", File.Exists(appXaml) && File.Exists(main));
Expect("dead DashboardPage removed", !File.Exists(dash));
// The native settings sheet was a second, unreachable settings surface: it hung off a
// collapsed zero-size button and only this smoke's string pins kept it in the tree.
Expect("dead native SettingsSheet removed", !File.Exists(settingsSheet));
var wwwIndex = Path.Combine(repo, "Exo", "wwwroot", "index.html");
var exoCsproj = Path.Combine(repo, "Exo", "Exo.csproj");
Expect("wwwroot index present", File.Exists(wwwIndex));
if (File.Exists(wwwIndex))
{
    var wwwIndexText = File.ReadAllText(wwwIndex);
    Expect("shipped UI carries a strict Content-Security-Policy",
        wwwIndexText.Contains("Content-Security-Policy", StringComparison.Ordinal)
        && wwwIndexText.Contains("default-src 'none'", StringComparison.Ordinal)
        && wwwIndexText.Contains("script-src 'self'", StringComparison.Ordinal)
        && wwwIndexText.Contains("object-src 'none'", StringComparison.Ordinal),
        "index.html must set a strict CSP (no remote scripts, no objects)");
    Expect("shipped UI never enables WebView DevTools in production",
        !wwwIndexText.Contains("devtools", StringComparison.OrdinalIgnoreCase));
}
var mainWindowCs = Path.Combine(repo, "Exo", "MainWindow.xaml.cs");
if (File.Exists(mainWindowCs))
{
    var mainWindowText = File.ReadAllText(mainWindowCs);
    Expect("WebView DevTools are release-disabled and DEBUG-gated",
        mainWindowText.Contains("#if DEBUG", StringComparison.Ordinal)
        && mainWindowText.Contains("AreDevToolsEnabled = false", StringComparison.Ordinal)
        && mainWindowText.Contains("AreDefaultContextMenusEnabled = false", StringComparison.Ordinal)
        && mainWindowText.Contains("IsZoomControlEnabled = false", StringComparison.Ordinal));
}
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
if (File.Exists(shippedManifestCs) && File.Exists(generatedManifestCs) && File.Exists(shippedClosureCs))
{
    var integritySource = File.ReadAllText(shippedManifestCs) + "\n" + File.ReadAllText(shippedClosureCs);
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
        && integritySource.Contains("not present in this Exo build's shipped manifest", StringComparison.Ordinal)
        && integritySource.Contains("ShippedArtifactClosureVerifier", StringComparison.Ordinal));
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
    // Short-path -File bootstrap (not multi-KB -EncodedCommand via ShellExecute runas —
    // that hit ERROR_FILENAME_EXCED_RANGE and broke every elevated Apply/Repair).
    Expect("elevated bootstrap uses short-path -File and rehashes the script",
        runnerSource.Contains("elevate-", StringComparison.Ordinal)
        && runnerSource.Contains("bootstrapPath", StringComparison.Ordinal)
        && runnerSource.Contains("WindowStyle Hidden -File", StringComparison.Ordinal)
        && !runnerSource.Contains("Hidden -EncodedCommand", StringComparison.Ordinal)
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

    var webBridgeCs = ReadBridgeSources(repo);
    var nvidiaPanelCs = Path.Combine(repo, "Exo", "Services", "NvidiaPanelSettingsService.cs");
    var networkOptCs = Path.Combine(repo, "Exo", "Services", "NetworkOptimizerService.cs");
    Expect("Apply and Repair opt in to dependency preparation",
        !string.IsNullOrEmpty(webBridgeCs) && File.Exists(nvidiaPanelCs) && File.Exists(networkOptCs)
        && webBridgeCs.Contains("ensureRuntime: needPwshBootstrap", StringComparison.Ordinal)
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
    Expect("an app update is offered only with a usable asset and SHA-256",
        updateSource.Contains("UpdateAvailable = downloadUrl is not null && sha256 is not null", StringComparison.Ordinal));
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
    // The transparent drag region is the only native overlay. React paints one
    // integrated caption set; XAML never paints a duplicate set of buttons.
    Expect("thin transparent drag strip (extended black caption)",
        m.Contains("x:Name=\"AppTitleBar\"", StringComparison.Ordinal)
        && m.Contains("Background=\"Transparent\"", StringComparison.Ordinal)
        // Must stay shorter than the React icon rail or it steals module clicks.
        && m.Contains("Height=\"8\"", StringComparison.Ordinal)
        && !m.Contains("Height=\"50\"", StringComparison.Ordinal)
        && !m.Contains("x:Name=\"TitleChrome\"", StringComparison.Ordinal)
        && !m.Contains("<TitleBar", StringComparison.Ordinal)
        && !m.Contains("CaptionSpacerHost", StringComparison.Ordinal)
        && !m.Contains("Text=\"EXO\"", StringComparison.Ordinal));
    // These used to assert the collapsed ExoBrandPill / NavHome / SettingsButton stubs were
    // PRESENT — a zero-size invisible Border whose only remaining purpose was satisfying this
    // gate. The settings surface is the React popover; the native flyout it shadowed is gone,
    // so the assertion is inverted: the stubs must stay deleted.
    Expect("no legacy native chrome stubs",
        !m.Contains("x:Name=\"ExoBrandPill\"", StringComparison.Ordinal)
        && !m.Contains("x:Name=\"NavHome\"", StringComparison.Ordinal)
        && !m.Contains("SettingsButton", StringComparison.Ordinal)
        && !m.Contains("Text=\"EXO\"", StringComparison.Ordinal));
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
// Liquid-glass product shell: Shell + Home/Module/Games pages, HashRouter, real glass CSS.
// The conversational orb (OrbApp / BrainOrb) is retired.
// ── The single-screen shell ────────────────────────────────────────────────────────────
// Exo is one screen: a title strip, a telemetry band, and one row per optimizer. The nine
// module pages, Driver Center, Games and "Verify all" were removed with it. These pin the
// shape that replaced them, because every one of them is a thing that can silently rot —
// a row list that starts scrolling, a state word with no source, an apply that never
// reports a percent.
var exoAppTsx = Path.Combine(repo, "ui", "src", "components", "ExoApp.tsx");
var appTsx = Path.Combine(repo, "ui", "src", "App.tsx");
var modulesTs = Path.Combine(repo, "ui", "src", "lib", "modules.ts");
var tokensCss = Path.Combine(repo, "ui", "src", "tokens.css");
var tweaksCss = Path.Combine(repo, "ui", "src", "tweaks.css");
// AMOLED ExoApp is the only product shell. Legacy Shell/HomePage/SettingsPopover are gone.
Expect("shell UI present",
    File.Exists(exoAppTsx)
    && File.Exists(modulesTs)
    && File.Exists(tokensCss)
    && File.Exists(tweaksCss));
Expect("orb UI retired",
    !File.Exists(Path.Combine(repo, "ui", "src", "pages", "OrbApp.tsx"))
    && !File.Exists(Path.Combine(repo, "ui", "src", "components", "BrainOrb.tsx"))
    && !File.Exists(Path.Combine(repo, "ui", "src", "pages", "ReelApp.tsx")));
// Old multi-page + 4.8.0 single-screen surfaces must stay deleted.
Expect("multi-page optimizer surface retired",
    !File.Exists(Path.Combine(repo, "ui", "src", "pages", "ModulePage.tsx"))
    && !File.Exists(Path.Combine(repo, "ui", "src", "pages", "GamesPage.tsx"))
    && !File.Exists(Path.Combine(repo, "ui", "src", "pages", "DriverCenterPage.tsx"))
    && !File.Exists(Path.Combine(repo, "ui", "src", "pages", "HomePage.tsx"))
    && !File.Exists(Path.Combine(repo, "ui", "src", "components", "Shell.tsx"))
    && !File.Exists(Path.Combine(repo, "ui", "src", "components", "SettingsPopover.tsx"))
    && !File.Exists(Path.Combine(repo, "ui", "src", "components", "ProfileSheet.tsx"))
    && !File.Exists(Path.Combine(repo, "ui", "src", "components", "UpdateOffer.tsx"))
    && !File.Exists(Path.Combine(repo, "ui", "src", "components", "LiquidGlass.tsx"))
    && !File.Exists(Path.Combine(repo, "ui", "src", "components", "SettingsDrawer.tsx")));
if (File.Exists(appTsx))
{
    var app = File.ReadAllText(appTsx);
    Expect("App mounts the one screen through Shell",
        app.Contains("ExoApp", StringComparison.Ordinal)
        && !app.Contains("Shell", StringComparison.Ordinal)
        && !app.Contains("HomePage", StringComparison.Ordinal)
        && !app.Contains("OrbApp", StringComparison.Ordinal)
        && !app.Contains("ModulePage", StringComparison.Ordinal)
        && !app.Contains("RouterProvider", StringComparison.Ordinal));
    Expect("Motion library follows the OS reduced-motion setting",
        app.Contains("MotionConfig", StringComparison.Ordinal)
        && app.Contains("reducedMotion=\"user\"", StringComparison.Ordinal));
}
if (File.Exists(exoAppTsx))
{
    var exo = File.ReadAllText(exoAppTsx);
    Expect("ExoApp wires Apply Repair Verify and View logs",
        exo.Contains("host.apply", StringComparison.Ordinal)
        && exo.Contains("host.repair", StringComparison.Ordinal)
        && exo.Contains("host.detect", StringComparison.Ordinal)
        && exo.Contains("openLogs", StringComparison.Ordinal)
        && exo.Contains("View logs", StringComparison.Ordinal));
    Expect("ExoApp AMOLED home meters present",
        exo.Contains("label=\"CPU\"", StringComparison.Ordinal)
        && exo.Contains("label=\"GPU\"", StringComparison.Ordinal)
        && exo.Contains("label=\"Memory\"", StringComparison.Ordinal)
        && exo.Contains("label=\"Disk\"", StringComparison.Ordinal));
    Expect("ExoApp uses brand icon plates",
        exo.Contains("icon-plate", StringComparison.Ordinal));
    // One Update button: install path + percent-only progress (no step prose).
    Expect("Settings Check for updates is check-only and Install update is explicit",
        exo.Contains("host.installUpdate()", StringComparison.Ordinal)
        && exo.Contains("settings.updateProgress", StringComparison.Ordinal)
        && exo.Contains("Update Exo", StringComparison.Ordinal)
        && exo.Contains("updatePercent", StringComparison.Ordinal)
        && !exo.Contains("SettingsPopover", StringComparison.Ordinal)
        && !exo.Contains("Checking GitHub", StringComparison.Ordinal)
        && !exo.Contains("Downloading and installing", StringComparison.Ordinal));
    Expect("Settings update outcome is announced accessibly",
        exo.Contains("aria-label=\"Update Exo\"", StringComparison.Ordinal));
    Expect("a failed check-only response is an error, not latest-build success",
        exo.Contains("Retry update", StringComparison.Ordinal)
        || exo.Contains("Failed", StringComparison.Ordinal));
    Expect("home reads the dashboard and live telemetry",
        exo.Contains("host.getDashboard()", StringComparison.Ordinal)
        && exo.Contains("host.getLive()", StringComparison.Ordinal));
    Expect("rows apply and follow host progress",
        exo.Contains("host.apply", StringComparison.Ordinal)
        && exo.Contains("module.progress", StringComparison.Ordinal));
    Expect("applied and failed rows expose confirmed snapshot Repair",
        exo.Contains("host.repair", StringComparison.Ordinal));
    Expect("the active Apply or Repair action becomes Stop without replacing the original await",
        exo.Contains("host.cancel", StringComparison.Ordinal)
        // Busy state is percent-only on the primary button (no step icons / status prose).
        && exo.Contains("Math.round(progress)}%", StringComparison.Ordinal));
    Expect("each row can force Verify and keeps detailed reasons",
        exo.Contains("host.detect", StringComparison.Ordinal));
    Expect("row state comes from the host answer, not from the press",
        exo.Contains("stateFromStatus", StringComparison.Ordinal)
        && exo.Contains("statusKind", StringComparison.Ordinal));
    Expect("DISK joins CPU/GPU/MEMORY in the telemetry band",
        exo.Contains("label=\"Disk\"", StringComparison.Ordinal)
        && (exo.Contains("diskPercent", StringComparison.Ordinal) || exo.Contains("diskSecondary", StringComparison.Ordinal)));
    // Default before dashboard is ready; missing only after host tags MISSING, then greyed.
    Expect("module icons are never hard-disabled by default missing",
        exo.Contains("states[id] ?? 'ready'", StringComparison.Ordinal)
        && exo.Contains("is-missing", StringComparison.Ordinal)
        && exo.Contains("disabled={missing}", StringComparison.Ordinal));
}
if (File.Exists(modulesTs))
{
    var mods = File.ReadAllText(modulesTs);
    // Fixed order, eight rows. Games is deliberately absent — it is per-title and cannot be
    // answered by the single button every row here carries.
    foreach (var id in new[] { "'nvidia'", "'amd'", "'system'", "'internet'", "'steam'", "'discord'", "'spotify'", "'brave'" })
        Expect($"module row present: {id}", mods.Contains($"id: {id}", StringComparison.Ordinal));
    Expect("games is not a shell row", !mods.Contains("id: 'games'", StringComparison.Ordinal));
    Expect("all four row states are presented",
        mods.Contains("applied:", StringComparison.Ordinal)
        && mods.Contains("ready:", StringComparison.Ordinal)
        && mods.Contains("blocked:", StringComparison.Ordinal)
        && mods.Contains("missing:", StringComparison.Ordinal));
    // Every brand row carries real artwork, not a tinted silhouette. The imports are the
    // assertion: a row that lost its logo would fall back to nothing at all.
    foreach (var logo in new[] { "nvidia", "amd", "windows", "steam", "discord", "spotify", "brave" })
        Expect($"module table imports the {logo} logo",
            mods.Contains($"/logos/{logo}.", StringComparison.Ordinal)
            || mods.Contains($"logos/{logo}.", StringComparison.Ordinal));
    // ExoApp ships Internet via ./assets/logos/internet.png (public → wwwroot), not a modules.ts import.
    Expect("brand rows use artwork, not glyph slugs",
        !mods.Contains("slug:", StringComparison.Ordinal));
    // The two modules with a real choice must ask before acting, and send the answer.
    Expect("NVIDIA and Internet ask before applying",
        mods.Contains("useGsync", StringComparison.Ordinal)
        && mods.Contains("preferLowestLatency", StringComparison.Ordinal));
}
if (File.Exists(tokensCss))
{
    var tokens = File.ReadAllText(tokensCss);
    Expect("text colour and scale are custom properties",
        tokens.Contains("--exo-text", StringComparison.Ordinal)
        && tokens.Contains("--exo-scale", StringComparison.Ordinal));
    // A keyframe that animates opacity from 0 with fill-mode: both leaves the element
    // invisible forever if the frame never ticks — a backgrounded WebView2 does exactly that.
    Expect("entrance keyframes animate transform only",
        !System.Text.RegularExpressions.Regex.IsMatch(tokens, @"@keyframes exo-(in|sheet|drop)[^}]*opacity"));
    Expect("reduced motion collapses animation",
        tokens.Contains("prefers-reduced-motion", StringComparison.Ordinal));
}
// Package size. The rows carry seven logo files; the webfont that used to back them is gone
// along with its dependency, because importing the simple-icons stylesheet pulls five formats
// of two faces — about 12 MB for six glyphs, into a product that ships offline.
{
    var logoDir = Path.Combine(repo, "ui", "src", "assets", "logos");
    Expect("brand artwork is committed", Directory.Exists(logoDir));
    if (Directory.Exists(logoDir))
    {
        foreach (var logo in new[] { "nvidia", "amd", "windows", "steam", "discord", "spotify", "brave", "internet" })
            Expect($"{logo} artwork present",
                Directory.GetFiles(logoDir, logo + ".*").Length > 0);
        var heavy = Directory.GetFiles(logoDir)
            .Where(f => new FileInfo(f).Length > 60_000)
            .Select(Path.GetFileName).ToArray();
        Expect("no oversized logo file", heavy.Length == 0, string.Join(", ", heavy));
    }
    // Runtime path the AMOLED shell loads for Internet (./assets/logos/internet.png).
    Expect("internet logo ships in ui/public/assets/logos",
        File.Exists(Path.Combine(repo, "ui", "public", "assets", "logos", "internet.png")));
    Expect("internet logo ships in wwwroot/assets/logos",
        File.Exists(Path.Combine(repo, "Exo", "wwwroot", "assets", "logos", "internet.png")));

    var uiPkgJson = Path.Combine(repo, "ui", "package.json");
    if (File.Exists(uiPkgJson))
    {
        Expect("the simple-icons webfont is gone",
            !File.ReadAllText(uiPkgJson).Contains("simple-icons-font", StringComparison.Ordinal));
    }
    var wwwrootAssets = Path.Combine(repo, "Exo", "wwwroot", "assets");
    if (Directory.Exists(wwwrootAssets))
    {
        var fat = Directory.GetFiles(wwwrootAssets)
            .Where(f => new FileInfo(f).Length > 600_000)
            .Select(Path.GetFileName)
            .ToArray();
        Expect("no multi-megabyte font ships in wwwroot", fat.Length == 0, string.Join(", ", fat));
    }
}

var uiPkg = Path.Combine(repo, "ui", "package.json");
if (File.Exists(uiPkg))
{
    var pkg = File.ReadAllText(uiPkg);
    Expect("shell UI deps present",
        pkg.Contains("\"motion\"", StringComparison.Ordinal)
        && pkg.Contains("@fontsource-variable/geist", StringComparison.Ordinal)
        && pkg.Contains("@phosphor-icons/react", StringComparison.Ordinal)
        && pkg.Contains("tailwindcss", StringComparison.Ordinal));
    // One screen means no routing surface at all, in-app or otherwise.
    Expect("no routing dependency for a single-screen app",
        !pkg.Contains("react-router-dom", StringComparison.Ordinal)
        && !File.Exists(Path.Combine(repo, "ui", "src", "lib", "router.tsx")));
    Expect("orb-only font packs removed",
        !pkg.Contains("@fontsource/instrument-serif", StringComparison.Ordinal)
        && !pkg.Contains("@fontsource/bricolage-grotesque", StringComparison.Ordinal)
        && !pkg.Contains("thinking-orbs", StringComparison.Ordinal));
}
// Bounded native drag region + WebView bridge — visible caption/nav are React.
var mainCs = Path.Combine(repo, "Exo", "MainWindow.xaml.cs");
if (File.Exists(mainCs))
{
    var cs = File.ReadAllText(mainCs);
    Expect("integrated React caption wired (native drag, no native buttons)",
        cs.Contains("ExtendsContentIntoTitleBar = true", StringComparison.Ordinal)
        && cs.Contains("hasTitleBar: false", StringComparison.Ordinal)
        && cs.Contains("SetTitleBar(AppTitleBar)", StringComparison.Ordinal)
        && cs.Contains("ButtonBackgroundColor = transparent", StringComparison.Ordinal)
        && !cs.Contains("ExtendsContentIntoTitleBar = false", StringComparison.Ordinal));
    // Forced AMOLED canvas — not resizable. React chrome owns min/close; layout is 1400×900.
    Expect("resizable shell with working maximize",
        cs.Contains("IsResizable = false", StringComparison.Ordinal)
        && cs.Contains("IsMaximizable = false", StringComparison.Ordinal)
        && cs.Contains("IsMinimizable = true", StringComparison.Ordinal)
        && cs.Contains("FixedWindowWidth", StringComparison.Ordinal)
        && cs.Contains("FixedWindowHeight", StringComparison.Ordinal)
        && cs.Contains("1400", StringComparison.Ordinal)
        && cs.Contains("900", StringComparison.Ordinal));
    Expect("webview bridge shell",
        cs.Contains("WebHostBridge", StringComparison.Ordinal)
        && cs.Contains("EnsureWebAsync", StringComparison.Ordinal)
        && cs.Contains("NavigateWebHashAsync", StringComparison.Ordinal));
    Expect("dead titlebar fields removed", !cs.Contains("AppTitleText", StringComparison.Ordinal)
        && !cs.Contains("CaptionSpacerHost", StringComparison.Ordinal)
        && !cs.Contains("UpdateRailSelection", StringComparison.Ordinal));
}
// Checklist navigation + sequence live in code-behind / view model.
if (File.Exists(theme))
{
    var t0 = File.ReadAllText(theme);
    Expect("click on press", t0.Contains("ClickMode\" Value=\"Press\"", StringComparison.Ordinal)
        || t0.Contains("ClickMode\" Value=\"Press", StringComparison.Ordinal)
        || t0.Contains("Value=\"Press\"", StringComparison.Ordinal) && t0.Contains("ClickMode", StringComparison.Ordinal));
}
// Settings is the React popover and nothing else. There is no second, native settings
// surface behind it — the flyout, its sheet control and its view model are deleted, so a
// change to settings cannot land in one of two places and be invisible in the other.
if (File.Exists(mainXaml))
{
    var mx = File.ReadAllText(mainXaml);
    Expect("no native settings surface",
        !mx.Contains("SettingsFlyout", StringComparison.Ordinal)
        && !mx.Contains("SettingsSheetHost", StringComparison.Ordinal)
        && !mx.Contains("SettingsRail", StringComparison.Ordinal)
        && !mx.Contains("SettingsOverlay", StringComparison.Ordinal));
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
    // Product settings live in the React popover. The host used to carry click and open/close
    // animation glue for a native flyout that no user could reach — it hung off a collapsed
    // zero-size button. Deleted, along with the sheet it animated.
    Expect("no native settings glue in the host",
        !mc.Contains("SettingsFlyout", StringComparison.Ordinal)
        && !mc.Contains("ShowAttachedFlyout", StringComparison.Ordinal)
        && !mc.Contains("PlayOpenAnimation", StringComparison.Ordinal)
        && !mc.Contains("PlayCloseAnimation", StringComparison.Ordinal)
        && !mc.Contains("OpenSettingsRail", StringComparison.Ordinal)
        && !mc.Contains("SettingsRail", StringComparison.Ordinal));
    Expect("taskbar icon win32 set",
        mc.Contains("SendMessage", StringComparison.Ordinal) && mc.Contains("LoadImage", StringComparison.Ordinal)
        && mc.Contains("TrySetWindowIcon", StringComparison.Ordinal));
    Expect("startup does not rewrite Start Menu shortcut",
        !mc.Contains("TryRepairStartMenuShortcut", StringComparison.Ordinal)
        && !mc.Contains("WScript.Shell", StringComparison.Ordinal));
    // The orb UI has no router: only the root hash is navigable. The old
    // "#/module/<id>" deep links pointed at routes that no longer exist, so
    // asserting them back would re-introduce dead code.
    Expect("navigate targets the router-less orb root",
        mc.Contains("NavigateWebHashAsync", StringComparison.Ordinal)
        && mc.Contains("EnsureWebAsync", StringComparison.Ordinal)
        && !mc.Contains("#/module/", StringComparison.Ordinal));
    // Navigation must not be `async void` — an exception there is unobservable
    // and takes the process down instead of just the navigation.
    Expect("no async void navigation", !mc.Contains("async void NavigateWebHash", StringComparison.Ordinal));
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
        && mx.Contains("<WebView2 x:Name=\"WebHost\"", StringComparison.Ordinal));
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
// Since 4.8.0 the single version source is the root VERSION file, read by
// Directory.Build.props into every project's Version. Assert the file is
// semver and that Exo.csproj no longer hardcodes a divergent copy.
var csprojText = File.Exists(csproj) ? File.ReadAllText(csproj) : "";
var csprojVersion = System.Text.RegularExpressions.Regex.Match(csprojText, "<Version>([^<]+)</Version>").Groups[1].Value;
var appVersion = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "";
Expect("VERSION file is semver",
    System.Text.RegularExpressions.Regex.IsMatch(appVersion, @"^\d+\.\d+\.\d+$"),
    $"got=[{appVersion}]");
Expect("VERSION is the single version source (no hardcoded csproj copy)",
    csprojVersion.Length == 0,
    $"csproj still hardcodes Version=[{csprojVersion}]; Directory.Build.props owns it");

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

// The live-advisor block that stood here asserted OptimizerAdvisor.cs existed and contained
// certain strings. Nothing in the app ever called it - the assertions were guarding dead code,
// which is why they passed for months while the feature did nothing. Service and gate deleted
// together; the reachability check in Contracts.Smoke is what catches the next one.


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

// --- A module whose detect failed must not vanish ---
// There is no module page any more; the row list is the only surface, so it is the thing
// that has to keep showing a module whose detect came back failed or partial rather than
// dropping it. Both keep a retry affordance, while the status reason/detail remains visible.
{
    var homeF = Path.Combine(repo, "ui", "src", "components", "ExoApp.tsx");
    if (File.Exists(homeF))
    {
        var o = File.ReadAllText(homeF);
        Expect("row list classifies host status",
            o.Contains("statusKind", StringComparison.Ordinal));
        Expect("a failed or partial apply reads as stuck, not as done",
            o.Contains("blocked", StringComparison.Ordinal)
            && o.Contains("partial", StringComparison.Ordinal));
        Expect("a dashboard read that throws still leaves a message on screen",
            o.Contains("setError", StringComparison.Ordinal));
    }
}

// --- Undo stays reachable end to end ---
// Applied and stuck rows expose the host's snapshot-based Repair path. The bridge and typed
// client remain part of that promise; if either stops answering it, the visible control lies.
{
    var hostTsRepair = Path.Combine(repo, "ui", "src", "lib", "host.ts");
    var bridgeRepair = ReadBridgeSources(repo);
    if (File.Exists(hostTsRepair))
    {
        var h = File.ReadAllText(hostTsRepair);
        Expect("host still exposes repair for every module",
            h.Contains("repair:", StringComparison.Ordinal)
            && h.Contains("'module.repair'", StringComparison.Ordinal));
        Expect("host exposes cancel for long runs",
            h.Contains("cancel:", StringComparison.Ordinal) || h.Contains("cancel(", StringComparison.Ordinal));
        // Games is gone, so its per-title undo goes with it. Asserting the absence keeps a
        // half-revival from leaving a client method pointing at an RPC the bridge dropped.
        Expect("no games client surface survives",
            !h.Contains("repairGame", StringComparison.Ordinal)
            && !h.Contains("listGames", StringComparison.Ordinal)
            && !h.Contains("games.", StringComparison.Ordinal));
    }
    if (File.Exists(bridgeRepair))
    {
        Expect("bridge still dispatches repair",
            bridgeRepair.Contains("\"module.repair\"", StringComparison.Ordinal));
    }
}

// --- "Good to go" must account for what Exo cannot fix ---
// RAM stuck at JEDEC base instead of its XMP/EXPO profile costs more frames than every
// registry tweak Exo applies combined, and it lives in firmware. Declaring the rig good
// while that is sitting there is the same dishonesty as a tweak that reports success
// without applying.
{
    var hostTs = Path.Combine(repo, "ui", "src", "lib", "host.ts");
    var bridge = ReadBridgeSources(repo);
    var advisor = Path.Combine(repo, "Exo", "Services", "FirmwareAdvisor.cs");
    Expect("firmware advisor sources present",
        File.Exists(hostTs) && !string.IsNullOrEmpty(bridge) && File.Exists(advisor));

    if (File.Exists(advisor) && !string.IsNullOrEmpty(bridge))
    {
        // The advisor must not be another define-and-never-call: two shipped features were
        // found this way, so the wiring itself is the assertion.
        Expect("firmware advisor is actually invoked",
            bridge.Contains("FirmwareAdvisor.Scan()", StringComparison.Ordinal));
        Expect("firmware findings reach the dashboard payload",
            bridge.Contains("firmware = BuildFirmwareFindings()", StringComparison.Ordinal));

        var adv = File.ReadAllText(advisor);
        Expect("advisor reads firmware, never writes it",
            !adv.Contains("SetValue(", StringComparison.Ordinal)
            && !adv.Contains("DeleteValue(", StringComparison.Ordinal));
        Expect("SMBIOS reads are length-guarded",
            adv.Contains("if (len >= 0x17)", StringComparison.Ordinal)
            && adv.Contains("if (len >= 0x22)", StringComparison.Ordinal));
        Expect("unreadable firmware is reported as unknown, not as a pass",
            adv.Contains("bool? Ok", StringComparison.Ordinal));
    }

    if (File.Exists(hostTs))
    {
        var hostTxt = File.ReadAllText(hostTs);
        Expect("host type distinguishes unknown from failing",
            File.ReadAllText(hostTs).Contains("ok: boolean | null", StringComparison.Ordinal));
    }
}

Log($"=== SUMMARY failed={failed} ===");
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
File.WriteAllLines(logPath, lines);
Environment.Exit(failed == 0 ? 0 : 1);

static string ReadBridgeSources(string repo)
{
    var dir = Path.Combine(repo, "Exo", "Services");
    var files = Directory.GetFiles(dir, "WebHostBridge*.cs").OrderBy(f => f, StringComparer.Ordinal).ToArray();
    return string.Join("\n", files.Select(f => File.ReadAllText(f)));
}

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
