using System.Text.Json;
using System.Text;
using OptiHub.Helpers;
using OptiHub.Models;

namespace OptiHub.Services;

public sealed class OptimizerStateService
{
    private readonly PowerShellRunnerService _runner;
    private readonly ScriptBundleService _scripts;

    public OptimizerStateService(
        PowerShellRunnerService runner,
        ScriptBundleService scripts)
    {
        _runner = runner;
        _scripts = scripts;
    }

    public async Task<OptimizerStateInfo> DetectDiscordAsync(
        CancellationToken ct = default,
        bool fastOnly = false)
    {
        var heuristic = await RunHeuristicAsync(DetectDiscordHeuristic, "Discord", ct)
            .ConfigureAwait(false);
        if (fastOnly)
            return heuristic;

        var detectScript = _scripts.DiscordDetectScript;
        if (!File.Exists(detectScript))
            return heuristic;

        try
        {
            var result = await _runner.RunAsync(
                detectScript,
                arguments: Array.Empty<string>(),
                elevate: false,
                progress: null,
                cancellationToken: ct,
                workingDirectory: _scripts.GetDiscordRoot());

            if (!result.Success && string.IsNullOrWhiteSpace(result.FullOutput))
                return heuristic;

            var jsonLine = result.FullOutput
                .Split('\n')
                .Select(l => l.TrimEnd('\r').Trim())
                .LastOrDefault(l => l.StartsWith('{') && l.Contains("isApplied", StringComparison.OrdinalIgnoreCase));

            if (jsonLine is null)
                return heuristic;

            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            var applied = root.TryGetProperty("isApplied", out var a) && a.GetBoolean();
            var status = root.TryGetProperty("statusText", out var s) ? s.GetString() ?? heuristic.StatusText : heuristic.StatusText;
            var detail = root.TryGetProperty("detail", out var d) ? d.GetString() ?? string.Empty : string.Empty;
            var features = ParseFeatures(root);

            if (features.Count == 0)
                features = heuristic.Features.ToList();

            return new OptimizerStateInfo
            {
                IsApplied = applied,
                StatusText = status,
                Detail = detail,
                Features = features
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return heuristic;
        }
    }

    private static List<OptimizerFeatureInfo> ParseFeatures(JsonElement root)
    {
        var features = new List<OptimizerFeatureInfo>();

        if (root.TryGetProperty("features", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
                var detail = item.TryGetProperty("detail", out var d) ? d.GetString() : null;
                var active = item.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True;
                if (string.IsNullOrWhiteSpace(title)) continue;
                features.Add(MakeFeature(title!, detail ?? string.Empty, active));
            }
        }

        if (features.Count == 0 &&
            root.TryGetProperty("checks", out var checks) &&
            checks.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in checks.EnumerateArray())
            {
                var text = item.GetString();
                if (string.IsNullOrWhiteSpace(text)) continue;
                features.Add(MapLegacyCheck(text!));
            }
        }

        return features;
    }

    private static OptimizerFeatureInfo MakeFeature(string title, string detail, bool active) =>
        new()
        {
            Title = title,
            Detail = detail,
            IsActive = active,
            Glyph = active ? "\uE73E" : "\uE711"
        };

    private static OptimizerFeatureInfo MapLegacyCheck(string text)
    {
        var lower = text.ToLowerInvariant();
        var active = !(lower.Contains("not ") || lower.Contains("missing") || lower.Contains("not found") || lower.Contains("not detected"));

        if (lower.Contains("equicord"))
            return MakeFeature("Client mods & privacy", "Equicord loads privacy plugins and strips noisy telemetry.", active);
        if (lower.Contains("openasar"))
            return MakeFeature("Faster Discord startup", "OpenASAR replaces the heavy launcher path so Discord opens quicker.", active);
        if (lower.Contains("kernel") || lower.Contains("ffmpeg") || lower.Contains("discopt"))
            return MakeFeature("Aggressive RAM + latency kernel", "DiscOpt reclaims memory every 5s, uses Above Normal process priority, and enables thread/raw-input tuning.", active);
        if (lower.Contains("debloat") || lower.Contains("game sdk") || lower.Contains("locale"))
            return MakeFeature("Complete client debloat", "Old builds, optional hook/clips modules, game SDK files, extra locales, and disposable caches are removed.", active);
        if (lower.Contains("amoled"))
            return MakeFeature("True black AMOLED theme", "Pure black UI saves OLED power and cuts eye strain at night.", active);
        if (lower.Contains("startup") || lower.Contains("toast") || lower.Contains("tray"))
            return MakeFeature("Windows background suppression", "Startup, scheduled background noise, Windows toasts, and tray promotion are disabled.", active);

        return MakeFeature(text, string.Empty, active);
    }

    private OptimizerStateInfo DetectDiscordHeuristic()
    {
        var features = new List<OptimizerFeatureInfo>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var discordRoot = Path.Combine(local, "Discord");
        var equicord = Path.Combine(appData, "Equicord");

        if (!Directory.Exists(discordRoot))
        {
            return new OptimizerStateInfo
            {
                IsApplied = false,
                StatusText = "Discord not installed",
                Detail = "Install Discord stable first, or let the optimizer install it for you.",
                Features = new[]
                {
                    MakeFeature("Discord install", "Stable Discord is required before optimizations can apply.", false)
                }
            };
        }

        var appDir = Directory.EnumerateDirectories(discordRoot, "app-*")
            .Select(path => new
            {
                Path = path,
                Version = Version.TryParse(Path.GetFileName(path).Replace("app-", string.Empty), out var version)
                    ? version
                    : new Version(0, 0)
            })
            .OrderByDescending(item => item.Version)
            .Select(item => item.Path)
            .FirstOrDefault();

        if (appDir is null)
        {
            return new OptimizerStateInfo
            {
                IsApplied = false,
                StatusText = "Discord incomplete",
                Detail = "No active Discord build folder was found.",
                Features = new[]
                {
                    MakeFeature("Discord build", "No app-* folder under LocalAppData\\Discord.", false)
                }
            };
        }

        var resources = Path.Combine(appDir, "resources");
        var equicordAsar = Path.Combine(equicord, "equicord.asar");
        var appAsar = Path.Combine(resources, "app.asar");
        var versionDll = Path.Combine(appDir, "version.dll");
        var ffmpeg = Path.Combine(appDir, "ffmpeg.dll");
        var ffmpegReal = Path.Combine(appDir, "ffmpeg_real.dll");
        var configIni = Path.Combine(appDir, "config.ini");

        var markerOk = false;
        var discordStatePath = Path.Combine(PathHelper.AppDataDir, "discord-optimizer.json");
        if (File.Exists(discordStatePath))
        {
            try
            {
                using var stateDocument = JsonDocument.Parse(File.ReadAllText(discordStatePath));
                var state = stateDocument.RootElement;
                var stateVersion = state.TryGetProperty("version", out var versionValue)
                    ? versionValue.GetString()
                    : null;
                var applyStatus = state.TryGetProperty("applyStatus", out var statusValue)
                    ? statusValue.GetString()
                    : null;
                var markerAppDir = state.TryGetProperty("appDir", out var appDirValue)
                    ? appDirValue.GetString()
                    : null;
                markerOk = string.Equals(stateVersion, "1.3.0", StringComparison.Ordinal) &&
                           string.Equals(applyStatus, "applied", StringComparison.Ordinal) &&
                           IsTrue(state, "applied") && IsTrue(state, "fullApply") &&
                           IsTrue(state, "windowsVerified") && IsTrue(state, "debloatVerified") &&
                           PathsEqual(markerAppDir, appDir);
            }
            catch { /* stale or interrupted markers are incomplete */ }
        }

        var loaderLen = File.Exists(appAsar) ? new FileInfo(appAsar).Length : 0L;
        var loaderText = string.Empty;
        if (loaderLen is >= 64 and < 4096)
        {
            try { loaderText = File.ReadAllText(appAsar); }
            catch { /* a locked loader is not trusted */ }
        }
        var equicordOk = File.Exists(equicordAsar) &&
                         new FileInfo(equicordAsar).Length > 1_000_000 &&
                         loaderText.Contains("equicord.asar", StringComparison.OrdinalIgnoreCase) &&
                         loaderText.Contains("require", StringComparison.OrdinalIgnoreCase);
        features.Add(MakeFeature(
            "Client mods & privacy",
            "Equicord loads privacy plugins and strips noisy telemetry.",
            equicordOk));

        var innerAsar = Path.Combine(resources, "_app.asar");
        var openAsarLength = File.Exists(innerAsar) ? new FileInfo(innerAsar).Length : 0L;
        var openAsarOk = openAsarLength is > 10_000 and < 500_000;
        features.Add(MakeFeature(
            "Faster Discord startup",
            "OpenASAR replaces the heavy launcher path so Discord opens quicker.",
            openAsarOk));

        var kernelOk = false;
        if (File.Exists(versionDll) && File.Exists(ffmpeg) &&
            File.Exists(ffmpegReal) && File.Exists(configIni))
        {
            try
            {
                var config = File.ReadAllText(configIni);
                var bundledKit = Path.Combine(PathHelper.DiscordScriptsDir, "kit");
                kernelOk = new FileInfo(ffmpeg).Length < 500_000 &&
                           new FileInfo(ffmpegReal).Length > 500_000 &&
                           new FileInfo(versionDll).Length > 50_000 &&
                           config.Split('\n').Any(line => line.Trim().Equals("TrimIntervalMs=5000", StringComparison.Ordinal)) &&
                           config.Split('\n').Any(line => line.Trim().Equals("PriorityClass=3", StringComparison.Ordinal)) &&
                           FilesHaveSameSha256(Path.Combine(bundledKit, "ffmpeg.dll"), ffmpeg) &&
                           FilesHaveSameSha256(Path.Combine(bundledKit, "version.dll"), versionDll) &&
                           FilesHaveSameSha256(Path.Combine(bundledKit, "config.ini"), configIni);
            }
            catch { /* ignore */ }
        }
        features.Add(MakeFeature(
            "Aggressive RAM + latency kernel",
            "DiscOpt reclaims memory every 5s, uses Above Normal process priority, and enables thread/raw-input tuning.",
            kernelOk));

        var oldAppsPresent = true;
        try
        {
            oldAppsPresent = Directory.EnumerateDirectories(discordRoot, "app-*")
                .Any(path => !string.Equals(path, appDir, StringComparison.OrdinalIgnoreCase));
        }
        catch { /* an unreadable install is not considered debloated */ }
        var modulesPath = Path.Combine(appDir, "modules");
        var optionalModulesPresent = new[] { "discord_hook-1", "discord_clips-1" }
            .Any(name => Directory.Exists(Path.Combine(modulesPath, name)));
        var gameSdkPresent = false;
        try
        {
            gameSdkPresent = Directory.Exists(modulesPath) &&
                             Directory.EnumerateFiles(
                                 modulesPath,
                                 "discord_game_sdk_*.dll",
                                 SearchOption.AllDirectories)
                                 .Any();
        }
        catch { gameSdkPresent = true; }
        var localesPath = Path.Combine(appDir, "locales");
        var extraLocalesPresent = false;
        try
        {
            extraLocalesPresent = Directory.Exists(localesPath) &&
                                  Directory.EnumerateFiles(localesPath, "*.pak")
                                      .Any(path => !string.Equals(
                                          Path.GetFileName(path),
                                          "en-US.pak",
                                          StringComparison.OrdinalIgnoreCase));
        }
        catch { extraLocalesPresent = true; }
        var debloatOk = !oldAppsPresent && !optionalModulesPresent &&
                        !gameSdkPresent && !extraLocalesPresent;
        features.Add(MakeFeature(
            "Complete client debloat",
            "Old builds, optional hook/clips modules, game SDK files, extra locales, and disposable caches are removed.",
            debloatOk));

        var runtimeOk = new[]
        {
            "discord_desktop_core-1", "discord_utils-1", "discord_voice-1", "discord_media-1"
        }.All(name => Directory.Exists(Path.Combine(modulesPath, name)));
        features.Add(MakeFeature(
            "Discord runtime integrity",
            "Required desktop, utility, voice, and media modules remain installed.",
            runtimeOk));

        var amoledOk = false;
        var startupOk = false;
        var settingsPath = Path.Combine(appData, "discord", "settings.json");
        if (File.Exists(settingsPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (doc.RootElement.TryGetProperty("BACKGROUND_COLOR", out var bg) &&
                    bg.GetString() == "#000000")
                    amoledOk = true;
                if (doc.RootElement.TryGetProperty("OPEN_ON_STARTUP", out var su) &&
                    su.ValueKind == JsonValueKind.False)
                    startupOk = true;
            }
            catch { /* ignore */ }
        }

        features.Add(MakeFeature(
            "True black AMOLED theme",
            "Pure black UI saves OLED power and cuts eye strain at night.",
            amoledOk));

        var notificationIds = new[]
        {
            "Discord",
            "Discord.Desktop",
            "DiscordInc.Discord",
            "com.squirrel.Discord.Discord"
        };
        var notificationsOk = true;
        try
        {
            const string notificationsRoot =
                @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
            foreach (var id in notificationIds)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    $@"{notificationsRoot}\{id}");
                if (key?.GetValue("Enabled") is not int enabled || enabled != 0)
                {
                    notificationsOk = false;
                    break;
                }
            }
        }
        catch
        {
            notificationsOk = false;
        }
        var windowsQuietOk = startupOk && notificationsOk &&
                             IsStableDiscordRunQuiet(discordRoot) &&
                             AreStableDiscordScheduledTasksDisabled(discordRoot) &&
                             AreStableDiscordTrayEntriesHidden(discordRoot);
        features.Add(MakeFeature(
            "Windows background suppression",
            "Startup, scheduled background noise, Windows toasts, and tray promotion are disabled.",
            windowsQuietOk));

        var launchOk = false;
        var shortcutPath = Path.Combine(
            appData,
            "Microsoft", "Windows", "Start Menu", "Programs", "Discord Inc", "Discord.lnk");
        if (File.Exists(shortcutPath))
        {
            try
            {
                var bytes = File.ReadAllBytes(shortcutPath);
                var unicode = Encoding.Unicode.GetString(bytes);
                var utf8 = Encoding.UTF8.GetString(bytes);
                launchOk = (unicode.Contains("Discord.vbs", StringComparison.OrdinalIgnoreCase) &&
                            unicode.Contains("wscript.exe", StringComparison.OrdinalIgnoreCase)) ||
                           (utf8.Contains("Discord.vbs", StringComparison.OrdinalIgnoreCase) &&
                            utf8.Contains("wscript.exe", StringComparison.OrdinalIgnoreCase));
            }
            catch { /* ignore */ }
        }
        features.Add(MakeFeature(
            "Start Menu / apps launch path",
            "Start Menu and taskbar Discord shortcuts use the OptiHub -Launch path (OpenASAR + kernel). No desktop icons created.",
            launchOk));

        features.Add(MakeFeature(
            "Verified optimizer record",
            "A completed 1.3.0 full apply is recorded for this exact Discord build.",
            markerOk));

        var applied = markerOk && equicordOk && openAsarOk && kernelOk && debloatOk &&
                      windowsQuietOk && amoledOk && runtimeOk && launchOk;
        return new OptimizerStateInfo
        {
            IsApplied = applied,
            StatusText = applied ? "Already optimized" : "Ready to optimize",
            Detail = applied
                ? "No-compromise pack active: aggressive trim, Above Normal priority, full debloat, OpenASAR, and Equicord."
                : "Some pieces are missing. Run to finish setup and unlock the savings below.",
            Features = features
        };
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool FilesHaveSameSha256(string left, string right)
    {
        if (!File.Exists(left) || !File.Exists(right)) return false;
        try
        {
            using var leftStream = File.OpenRead(left);
            using var rightStream = File.OpenRead(right);
            using var sha = System.Security.Cryptography.SHA256.Create();
            var leftHash = sha.ComputeHash(leftStream);
            sha.Initialize();
            var rightHash = sha.ComputeHash(rightStream);
            return leftHash.AsSpan().SequenceEqual(rightHash);
        }
        catch { return false; }
    }

    private static bool IsTextScopedToRoot(string? text, string root)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var expanded = Environment.ExpandEnvironmentVariables(text).Replace('/', '\\');
            return expanded.Contains(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool IsStableDiscordRunQuiet(string discordRoot)
    {
        try
        {
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            return run is null || run.GetValueNames().All(name =>
                !IsTextScopedToRoot(run.GetValue(name)?.ToString(), discordRoot));
        }
        catch { return false; }
    }

    private static bool AreStableDiscordTrayEntriesHidden(string discordRoot)
    {
        try
        {
            using var tray = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Control Panel\NotifyIconSettings");
            if (tray is null) return true;
            foreach (var subkeyName in tray.GetSubKeyNames())
            {
                using var entry = tray.OpenSubKey(subkeyName);
                if (!IsTextScopedToRoot(entry?.GetValue("ExecutablePath")?.ToString(), discordRoot))
                    continue;
                if (entry?.GetValue("IsPromoted") is not int promoted || promoted != 0)
                    return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static bool AreStableDiscordScheduledTasksDisabled(string discordRoot)
    {
        object? serviceObject = null;
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service");
            if (serviceType is null) return false;
            serviceObject = Activator.CreateInstance(serviceType);
            if (serviceObject is null) return false;
            dynamic service = serviceObject;
            service.Connect();

            bool InspectFolder(dynamic folder)
            {
                dynamic tasks = folder.GetTasks(0);
                for (var taskIndex = 1; taskIndex <= (int)tasks.Count; taskIndex++)
                {
                    dynamic task = tasks[taskIndex];
                    var stable = false;
                    dynamic actions = task.Definition.Actions;
                    for (var actionIndex = 1; actionIndex <= (int)actions.Count; actionIndex++)
                    {
                        dynamic action = actions[actionIndex];
                        if ((int)action.Type != 0) continue;
                        stable = IsTextScopedToRoot(Convert.ToString(action.Path), discordRoot) ||
                                 IsTextScopedToRoot(Convert.ToString(action.Arguments), discordRoot) ||
                                 IsTextScopedToRoot(Convert.ToString(action.WorkingDirectory), discordRoot);
                        if (stable) break;
                    }
                    if (stable && (bool)task.Enabled) return false;
                }

                dynamic subfolders = folder.GetFolders(0);
                for (var folderIndex = 1; folderIndex <= (int)subfolders.Count; folderIndex++)
                {
                    if (!InspectFolder(subfolders[folderIndex])) return false;
                }
                return true;
            }

            return InspectFolder(service.GetFolder("\\"));
        }
        catch { return false; }
        finally
        {
            if (serviceObject is not null && System.Runtime.InteropServices.Marshal.IsComObject(serviceObject))
            {
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(serviceObject); }
                catch { /* best-effort COM cleanup */ }
            }
        }
    }

    public async Task<OptimizerStateInfo> DetectSteamAsync(
        CancellationToken ct = default,
        bool fastOnly = false)
    {
        var heuristic = await RunHeuristicAsync(DetectSteamHeuristic, "Steam", ct)
            .ConfigureAwait(false);
        if (fastOnly)
            return heuristic;

        var detectScript = _scripts.SteamDetectScript;
        if (!File.Exists(detectScript))
            return heuristic;

        try
        {
            var result = await _runner.RunAsync(
                detectScript,
                arguments: Array.Empty<string>(),
                elevate: false,
                progress: null,
                cancellationToken: ct,
                workingDirectory: _scripts.GetSteamRoot());

            if (!result.Success && string.IsNullOrWhiteSpace(result.FullOutput))
                return heuristic;

            var jsonLine = result.FullOutput
                .Split('\n')
                .Select(l => l.TrimEnd('\r').Trim())
                .LastOrDefault(l => l.StartsWith('{') && l.Contains("isApplied", StringComparison.OrdinalIgnoreCase));

            if (jsonLine is null)
                return heuristic;

            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            var applied = root.TryGetProperty("isApplied", out var a) && a.GetBoolean();
            var status = root.TryGetProperty("statusText", out var s) ? s.GetString() ?? heuristic.StatusText : heuristic.StatusText;
            var detail = root.TryGetProperty("detail", out var d) ? d.GetString() ?? string.Empty : string.Empty;
            var features = ParseFeatures(root);
            if (features.Count == 0)
                features = heuristic.Features.ToList();

            return new OptimizerStateInfo
            {
                IsApplied = applied,
                StatusText = status,
                Detail = detail,
                Features = features
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return heuristic;
        }
    }

    private OptimizerStateInfo DetectSteamHeuristic()
    {
        var features = new List<OptimizerFeatureInfo>();
        var steam = FindSteamPath();
        var statePath = Path.Combine(PathHelper.AppDataDir, "steam-optimizer.json");
        var markerOk = false;
        var downloadMarkerOk = false;
        var clientMarkerOk = false;

        if (File.Exists(statePath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
                var root = doc.RootElement;
                var markerVersion = root.TryGetProperty("version", out var versionValue)
                    ? versionValue.GetString()
                    : null;
                var applyStatus = root.TryGetProperty("applyStatus", out var statusValue)
                    ? statusValue.GetString()
                    : null;
                var markerSteamPath = root.TryGetProperty("steamPath", out var steamPathValue)
                    ? steamPathValue.GetString()
                    : null;
                downloadMarkerOk = IsTrue(root, "configVerified") && IsTrue(root, "downloadOptimized");
                clientMarkerOk = IsTrue(root, "clientTweaksVerified") &&
                                 IsTrue(root, "snappyUi") && IsTrue(root, "overlayTweaks");
                markerOk = string.Equals(markerVersion, "1.6.0", StringComparison.Ordinal) &&
                           string.Equals(applyStatus, "applied", StringComparison.Ordinal) &&
                           IsTrue(root, "applied") &&
                           root.TryGetProperty("quick", out var quickValue) &&
                           quickValue.ValueKind == JsonValueKind.False &&
                           IsTrue(root, "fullApply") &&
                           IsTrue(root, "windowsVerified") &&
                           IsTrue(root, "debloatVerified") &&
                           IsTrue(root, "cacheCleanupCompleted") &&
                           IsTrue(root, "shaderInventoryVerified") &&
                           IsTrue(root, "installedShaderCachesPreserved") &&
                           PathsEqual(markerSteamPath, steam);
            }
            catch { /* invalid markers are not trusted */ }
        }

        if (steam is null)
        {
            return new OptimizerStateInfo
            {
                IsApplied = false,
                StatusText = "Steam not installed",
                Detail = "Install Steam, open it once, then return to OptiHub.",
                Features = new[]
                {
                    MakeFeature("Steam install", "steam.exe was not found in the usual locations.", false)
                }
            };
        }

        features.Add(MakeFeature("Steam install", steam, true));

        var startupOk = IsSteamStartupQuiet();

        features.Add(MakeFeature(
            "Quieter Windows startup",
            "Steam is not forced to launch when Windows starts.",
            startupOk));
        var launcherPath = Path.Combine(steam, "Steam-OptiHub.cmd");
        var cefLauncherOk = false;
        if (File.Exists(launcherPath))
        {
            try
            {
                var launcher = File.ReadAllText(launcherPath);
                cefLauncherOk = launcher.Contains("steam.exe", StringComparison.OrdinalIgnoreCase) &&
                                 launcher.Contains("-cef-disable-gpu", StringComparison.OrdinalIgnoreCase) &&
                                 launcher.Contains("start \"\" /HIGH", StringComparison.OrdinalIgnoreCase);
            }
            catch { /* ignore */ }
        }
        var downloadOptimized = downloadMarkerOk && IsSteamDownloadConfigOptimized(steam);
        features.Add(MakeFeature(
            "Quiet CEF launcher (default)",
            "Start Menu / taskbar Steam uses Steam-OptiHub.cmd (disable-gpu, nofriendsui, nointro, etc.). No desktop icons.",
            cefLauncherOk));

        var clientTweaksApplied = clientMarkerOk && AreSteamClientTweaksOptimized(steam);
        features.Add(MakeFeature(
            "Download tuning + deep client cache clean",
            "Throttle is cleared and disposable/orphaned caches are purged. Active downloads and installed-game shader caches stay intact.",
            downloadOptimized));

        features.Add(MakeFeature(
            "Overlay / library client tweaks",
            "GPU web views off, quieter overlay noise, no downloads while playing when keys exist.",
            clientTweaksApplied));

        var helperPath = Path.Combine(steam, "OptiHub-SteamWebHelperTrim.ps1");
        var aggressiveTrimOk = false;
        if (File.Exists(helperPath))
        {
            try
            {
                var helper = File.ReadAllText(helperPath);
                aggressiveTrimOk = helper.Contains("OptiHub.SteamWebHelper", StringComparison.Ordinal) &&
                                   helper.Contains("EmptyWorkingSet", StringComparison.Ordinal) &&
                                   helper.Contains("Start-Sleep -Seconds 5", StringComparison.Ordinal) &&
                                   helper.Contains("ProcessPriorityClass]::High", StringComparison.Ordinal) &&
                                   helper.Contains("ProcessPriorityClass]::BelowNormal", StringComparison.Ordinal);
            }
            catch { /* ignore */ }
        }
        features.Add(MakeFeature(
            "Aggressive 5s RAM trim + priority control",
            "Reclaims steamwebhelper working sets every 5s, runs the client High when idle, then yields CPU while gaming. No suspend.",
            aggressiveTrimOk));

        var applied = markerOk && startupOk && cefLauncherOk && aggressiveTrimOk &&
                      downloadOptimized && clientTweaksApplied;
        return new OptimizerStateInfo
        {
            IsApplied = applied,
            StatusText = applied ? "Already optimized" : "Ready to optimize",
            Detail = applied
                ? "Performance pack active. Open Steam from Start Menu or taskbar (no desktop shortcuts)."
                : "Run for the quiet CEF launcher, aggressive RAM reclamation, priority control, and deep client cleanup.",
            Features = features
        };
    }

    private static bool IsTrue(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool IsSteamStartupQuiet()
    {
        static bool RunKeyIsQuiet(Microsoft.Win32.RegistryKey? key)
        {
            if (key is null) return true;
            foreach (var name in key.GetValueNames())
            {
                if (name.StartsWith("steam", StringComparison.OrdinalIgnoreCase) ||
                    (key.GetValue(name)?.ToString() ?? string.Empty)
                        .Contains("steam.exe", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        try
        {
            using var hkcuRun = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            using var hklmRun = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            using var hklm32Run = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run");
            using var steam = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return RunKeyIsQuiet(hkcuRun) && RunKeyIsQuiet(hklmRun) && RunKeyIsQuiet(hklm32Run) &&
                   steam?.GetValue("StartupMode") is int startupMode && startupMode == 0;
        }
        catch { return false; }
    }

    private static (bool Valid, int Observed) TestSteamVdfExpectations(
        string raw,
        IReadOnlyList<(string Key, string Value)> expectations)
    {
        var observed = 0;
        foreach (var (key, expectedValue) in expectations)
        {
            var pattern = "\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\"\\s+\"([^\"]*)\"";
            var matches = System.Text.RegularExpressions.Regex.Matches(
                raw,
                pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            observed += matches.Count;
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                if (!string.Equals(match.Groups[1].Value, expectedValue, StringComparison.Ordinal))
                    return (false, observed);
            }
        }
        return (true, observed);
    }

    private static bool IsSteamDownloadConfigOptimized(string steamPath)
    {
        var config = Path.Combine(steamPath, "config", "config.vdf");
        if (!File.Exists(config)) return false;
        try
        {
            var result = TestSteamVdfExpectations(File.ReadAllText(config), new[]
            {
                ("DownloadThrottleKbps", "0"),
                ("ThrottleKbps", "0"),
                ("RateLimitBps", "0"),
                ("MaxSimDownloads", "8"),
                ("AutoUpdateWindowEnabled", "0")
            });
            return result.Valid;
        }
        catch { return false; }
    }

    private static bool AreSteamClientTweaksOptimized(string steamPath)
    {
        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata)) return false;
        var expectations = new[]
        {
            ("H264HWAccel", "0"),
            ("GPUAccelWebViews", "0"),
            ("GPUAccelWebViews2", "0"),
            ("GPUAccelWebViewsD3D11", "0"),
            ("SmoothScrollWebViews", "0"),
            ("LibraryDisableCommunityContent", "1"),
            ("InGameOverlayScreenshotNotification", "0"),
            ("Controller_EnableChrome", "0"),
            ("AllowDownloadsDuringGameplay", "0")
        };
        try
        {
            var files = Directory.EnumerateDirectories(userdata)
                .Select(path => Path.Combine(path, "config", "localconfig.vdf"))
                .Where(File.Exists)
                .ToArray();
            if (files.Length == 0) return false;
            var observed = 0;
            foreach (var file in files)
            {
                var result = TestSteamVdfExpectations(File.ReadAllText(file), expectations);
                if (!result.Valid) return false;
                observed += result.Observed;
            }
            return observed > 0;
        }
        catch { return false; }
    }

    private static string? FindSteamPath()
    {
        try
        {
            using var hkcu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var p = hkcu?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(Path.Combine(p, "steam.exe")))
                return p.Replace('/', '\\');

            var exe = hkcu?.GetValue("SteamExe") as string;
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                return Path.GetDirectoryName(exe)?.Replace('/', '\\');
        }
        catch { /* ignore */ }

        try
        {
            using var hklm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            var p = hklm?.GetValue("InstallPath") as string;
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(Path.Combine(p, "steam.exe")))
                return p;
        }
        catch { /* ignore */ }

        try
        {
            using var hklm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            var p = hklm?.GetValue("InstallPath") as string;
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(Path.Combine(p, "steam.exe")))
                return p;
        }
        catch { /* ignore */ }

        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var basePath in new[] { pf86, pf })
        {
            if (string.IsNullOrWhiteSpace(basePath)) continue;
            var classic = Path.Combine(basePath, "Steam");
            if (File.Exists(Path.Combine(classic, "steam.exe")))
                return classic;
        }

        return null;
    }

    public async Task<OptimizerStateInfo> DetectNvidiaAsync(
        CancellationToken ct = default,
        bool fastOnly = false)
    {
        var heuristic = await RunHeuristicAsync(DetectNvidiaHeuristic, "NVIDIA", ct)
            .ConfigureAwait(false);
        if (fastOnly)
            return heuristic;

        var detectScript = _scripts.NvidiaDetectScript;
        if (!File.Exists(detectScript))
            return heuristic;

        try
        {
            var result = await _runner.RunAsync(
                detectScript,
                arguments: Array.Empty<string>(),
                elevate: false,
                progress: null,
                cancellationToken: ct,
                workingDirectory: _scripts.GetNvidiaRoot());

            if (!result.Success && string.IsNullOrWhiteSpace(result.FullOutput))
                return heuristic;

            var jsonLine = result.FullOutput
                .Split('\n')
                .Select(l => l.TrimEnd('\r').Trim())
                .LastOrDefault(l => l.StartsWith('{') && l.Contains("isApplied", StringComparison.OrdinalIgnoreCase));

            if (jsonLine is null)
                return heuristic;

            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            var applied = root.TryGetProperty("isApplied", out var a) && a.GetBoolean();
            var status = root.TryGetProperty("statusText", out var s) ? s.GetString() ?? heuristic.StatusText : heuristic.StatusText;
            var detail = root.TryGetProperty("detail", out var d) ? d.GetString() ?? string.Empty : string.Empty;
            var features = ParseFeatures(root);
            if (features.Count == 0)
                features = heuristic.Features.ToList();

            var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("series", out var ser) && ser.ValueKind == JsonValueKind.String)
                extra["series"] = ser.GetString() ?? "";
            if (root.TryGetProperty("gsync", out var gs))
                extra["gsync"] = gs.ValueKind == JsonValueKind.True ? "true"
                    : gs.ValueKind == JsonValueKind.False ? "false"
                    : gs.ToString();
            if (root.TryGetProperty("gpuName", out var gn) && gn.ValueKind == JsonValueKind.String)
                extra["gpuName"] = gn.GetString() ?? "";
            if (root.TryGetProperty("currentDriver", out var cd) && cd.ValueKind == JsonValueKind.String)
                extra["currentDriver"] = cd.GetString() ?? "";
            if (root.TryGetProperty("latestDriver", out var ld) && ld.ValueKind == JsonValueKind.String)
                extra["latestDriver"] = ld.GetString() ?? "";
            if (root.TryGetProperty("needsDriverUpdate", out var nd))
                extra["needsDriverUpdate"] = nd.ValueKind == JsonValueKind.True ? "true"
                    : nd.ValueKind == JsonValueKind.False ? "false"
                    : nd.ToString();
            if (root.TryGetProperty("needsDriverRetweak", out var nr))
                extra["needsDriverRetweak"] = nr.ValueKind == JsonValueKind.True ? "true"
                    : nr.ValueKind == JsonValueKind.False ? "false"
                    : nr.ToString();
            if (root.TryGetProperty("driverTweaksOk", out var dt))
                extra["driverTweaksOk"] = dt.ValueKind == JsonValueKind.True ? "true"
                    : dt.ValueKind == JsonValueKind.False ? "false"
                    : dt.ToString();
            if (root.TryGetProperty("notebookGpu", out var nb))
                extra["notebookGpu"] = nb.ValueKind == JsonValueKind.True ? "true"
                    : nb.ValueKind == JsonValueKind.False ? "false"
                    : nb.ToString();

            return new OptimizerStateInfo
            {
                IsApplied = applied,
                StatusText = status,
                Detail = detail,
                Features = features,
                Extra = extra
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return heuristic;
        }
    }

    private OptimizerStateInfo DetectNvidiaHeuristic()
    {
        var features = new List<OptimizerFeatureInfo>();
        var statePath = Path.Combine(PathHelper.AppDataDir, "nvidia-optimizer.json");
        var hasMarker = false;
        var driverTweaksApplied = false;
        var profileApplied = false;
        var displayApplied = false;
        var debloatApplied = false;
        var restartPending = false;
        var applyInProgress = false;

        string? gpuName = null;
        string? series = null;
        string? driverTweaksVersion = null;
        string? profileFile = null;
        string? profileVersion = null;
        string? profileSha256 = null;
        string? profileDriverVersion = null;
        bool? gsync = null;
        if (File.Exists(statePath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
                var root = doc.RootElement;
                hasMarker = root.ValueKind == JsonValueKind.Object;
                if (root.TryGetProperty("gpuName", out var g) && g.ValueKind == JsonValueKind.String)
                    gpuName = g.GetString();
                if (root.TryGetProperty("series", out var s) && s.ValueKind == JsonValueKind.String)
                    series = s.GetString();
                if (root.TryGetProperty("gsync", out var gs) &&
                    gs.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    gsync = gs.GetBoolean();

                driverTweaksVersion = ReadString(root, "driverTweaksVersion");
                driverTweaksApplied = IsTrue(root, "driverTweaksVerified") &&
                                      !string.IsNullOrWhiteSpace(driverTweaksVersion);

                profileFile = ReadString(root, "profileFile");
                profileVersion = ReadString(root, "profileVersion");
                profileSha256 = ReadString(root, "profileSha256");
                profileDriverVersion = ReadString(root, "profileDriverVersion");
                var validProfileHash = profileSha256 is { Length: 64 } &&
                                       profileSha256.All(Uri.IsHexDigit);
                profileApplied = IsTrue(root, "profileApplied") &&
                                 !string.IsNullOrWhiteSpace(profileFile) &&
                                 !string.IsNullOrWhiteSpace(profileVersion) &&
                                 validProfileHash &&
                                 !string.IsNullOrWhiteSpace(profileDriverVersion) &&
                                 string.Equals(
                                     profileDriverVersion,
                                     driverTweaksVersion,
                                     StringComparison.OrdinalIgnoreCase);

                var displayMethod = root.TryGetProperty("displayMethod", out var method) &&
                                    method.ValueKind == JsonValueKind.String
                    ? method.GetString()
                    : null;
                displayApplied = IsTrue(root, "displayPrefs") &&
                                 string.Equals(displayMethod, "nvapi", StringComparison.OrdinalIgnoreCase);
                debloatApplied = IsTrue(root, "debloatApplied") &&
                                  IsTrue(root, "overlayDisabled");
                restartPending = IsTrue(root, "pendingAfterDriver");
                applyInProgress = IsTrue(root, "applyInProgress");
            }
            catch { /* invalid markers are not trusted */ }
        }

        var nvidiaDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation");
        var gpuOk = hasMarker || Directory.Exists(nvidiaDir);
        var notebookGpu = !string.IsNullOrWhiteSpace(gpuName) &&
                          (gpuName.Contains("Laptop GPU", StringComparison.OrdinalIgnoreCase) ||
                           gpuName.Contains("Notebook", StringComparison.OrdinalIgnoreCase) ||
                           gpuName.Contains("Mobile", StringComparison.OrdinalIgnoreCase) ||
                           gpuName.Contains("Max-Q", StringComparison.OrdinalIgnoreCase) ||
                           System.Text.RegularExpressions.Regex.IsMatch(
                               gpuName,
                               @"(?i)\bMX\d+\b|\b\d{3,4}M\b"));

        features.Add(MakeFeature(
            "NVIDIA stack",
            gpuName ?? (gpuOk ? "NVIDIA software present" : "No NVIDIA GPU/driver found yet."),
            gpuOk));
        features.Add(MakeFeature(
            "Driver (newest + install tweaks)",
            driverTweaksApplied
                ? $"OptiHub recorded verified driver/MSI tweaks for {driverTweaksVersion}. Refresh for a live check."
                : restartPending
                    ? "Restart Windows, then Apply again to verify the driver tweaks."
                    : "The marker does not contain a verified driver-tweak version.",
            driverTweaksApplied));
        features.Add(MakeFeature(
            "3D Base Profile",
            profileApplied
                ? $"OptiHub recorded the verified {profileFile} import ({profileVersion}, SHA-256 and driver {profileDriverVersion} checked)."
                : restartPending
                    ? "Restart Windows, then Apply again to import the profile."
                    : applyInProgress
                        ? "The previous Apply was interrupted before a verified profile marker was saved."
                        : "The profile file, pack version, SHA-256, and imported driver version are not all verified.",
            profileApplied));
        features.Add(MakeFeature(
            "Privacy / telemetry / overlay off",
            debloatApplied
                ? "OptiHub recorded overlay, telemetry, updater, FrameView, and auto-start debloat. Refresh for a live check."
                : "The background debloat and overlay-off markers are incomplete.",
            debloatApplied));
        features.Add(MakeFeature(
            "Display color / scaling prefs",
            displayApplied
                ? "OptiHub recorded a successful NVAPI apply. Refresh for live Full RGB, refresh-rate, and scaling verification."
                : "A verified NVAPI display apply is not recorded.",
            displayApplied));

        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(series))
            extra["series"] = series!;
        if (!string.IsNullOrEmpty(gpuName))
            extra["gpuName"] = gpuName!;
        if (gsync.HasValue)
            extra["gsync"] = gsync.Value ? "true" : "false";
        if (notebookGpu)
            extra["notebookGpu"] = "true";

        var applied = hasMarker && !restartPending && !applyInProgress && !notebookGpu && driverTweaksApplied &&
                      profileApplied && displayApplied && debloatApplied;
        var statusText = !gpuOk
            ? "No NVIDIA GPU"
            : restartPending
            ? "Restart required"
            : notebookGpu
                ? "Notebook driver requires manual action"
            : !driverTweaksApplied
                ? "Driver tweaks incomplete"
                : !profileApplied
                    ? "3D profile incomplete"
                    : !displayApplied
                        ? "Display setup incomplete"
                        : !debloatApplied
                            ? "Background debloat incomplete"
                            : applied
                                ? "Already optimized"
                                : "Ready to optimize";
        var detail = !gpuOk
            ? "Needs an NVIDIA GPU and current drivers."
            : restartPending
            ? "Restart Windows, then Apply once more to finish the 3D profile and display preferences."
            : notebookGpu
                ? "OptiHub blocks desktop driver metadata on Laptop/Notebook GPUs. Install the official NVIDIA notebook driver manually."
            : !driverTweaksApplied
                ? "The saved state does not prove the driver/MSI tweaks. Apply or Refresh for live verification."
                : !profileApplied
                    ? "The saved profile marker is missing its file, pack version, or SHA-256 proof. Apply again."
                    : !displayApplied
                        ? "The profile is present, but a verified NVAPI display apply is not recorded. Apply once to finish."
                        : !debloatApplied
                            ? "The overlay/background debloat state is incomplete. Apply again for the no-compromise state."
                            : "All phases are recorded. Refresh for live driver, service, profile, and NVAPI verification.";

        return new OptimizerStateInfo
        {
            IsApplied = applied,
            StatusText = statusText,
            Detail = detail,
            Features = features,
            Extra = extra
        };

        static string? ReadString(JsonElement root, string propertyName) =>
            root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static Task<OptimizerStateInfo> RunHeuristicAsync(
        Func<OptimizerStateInfo> detector,
        string optimizerName,
        CancellationToken ct) =>
        Task.Run(() =>
        {
            try
            {
                return detector();
            }
            catch
            {
                return new OptimizerStateInfo
                {
                    IsApplied = false,
                    StatusText = "State unavailable",
                    Detail = $"OptiHub could not inspect {optimizerName} right now. You can still open the optimizer and retry.",
                    Features = new[]
                    {
                        MakeFeature($"{optimizerName} status", "Detection failed; no changes were made.", false)
                    }
                };
            }
        }, ct);
}

