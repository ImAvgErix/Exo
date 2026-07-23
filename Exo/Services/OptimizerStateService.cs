using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Exo.Helpers;
using Exo.Models;

namespace Exo.Services;

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

    /// <summary>
    /// Last-apply structured report from a module state file
    /// (%LocalAppData%\Exo\&lt;module&gt;-optimizer.json, "applyReport" array).
    /// Entries have the form "&lt;step&gt;|ok", "&lt;step&gt;|fail:&lt;reason&gt;" or
    /// "&lt;step&gt;|skip:&lt;reason&gt;". Returns an empty list when the state file or
    /// the array is missing (older applies) — never throws.
    /// Module names: "discord", "steam", "brave", "games".
    /// </summary>
    public static IReadOnlyList<string> TryReadApplyReport(string module)
    {
        try
        {
            var statePath = Path.Combine(PathHelper.AppDataDir, $"{module}-optimizer.json");
            if (!File.Exists(statePath)) return Array.Empty<string>();
            using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
            if (!doc.RootElement.TryGetProperty("applyReport", out var report) ||
                report.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            var entries = new List<string>();
            foreach (var item in report.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text)) entries.Add(text!);
            }
            return entries;
        }
        catch { return Array.Empty<string>(); }
    }

    public Task<OptimizerStateInfo> DetectBraveAsync(CancellationToken ct = default) =>
        Task.Run(() => NativeLiveDetect.DetectBrave(), ct);

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
            var applied = root.TryGetProperty("isApplied", out var a) && a.ValueKind == JsonValueKind.True;
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

    /// <summary>Panel-style row. Preserve detector detail when it explains the live policy.</summary>
    private static OptimizerFeatureInfo MakeFeature(string title, string detail, bool active) =>
        new()
        {
            Title = title,
            Detail = string.IsNullOrWhiteSpace(detail)
                ? (active ? "Applied" : "Not applied")
                : detail,
            IsActive = active,
            Glyph = active ? "\uE73E" : "\uE711"
        };

    private static OptimizerFeatureInfo MapLegacyCheck(string text)
    {
        var lower = text.ToLowerInvariant();
        var active = !(lower.Contains("not ") || lower.Contains("missing") || lower.Contains("not found") || lower.Contains("not detected"));

        if (lower.Contains("equicord"))
            return MakeFeature("Equicord", "", active);
        if (lower.Contains("openasar"))
            return MakeFeature("OpenASAR", "", active);
        if (lower.Contains("kernel") || lower.Contains("ffmpeg") || lower.Contains("discopt"))
            return MakeFeature("RAM / latency kernel", "", active);
        if (lower.Contains("debloat") || lower.Contains("game sdk") || lower.Contains("locale"))
            return MakeFeature("Client debloat", "", active);
        if (lower.Contains("amoled"))
            return MakeFeature("Dark mode", "", active);
        if (lower.Contains("startup") || lower.Contains("toast") || lower.Contains("tray"))
            return MakeFeature("Windows quiet", "", active);

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
                StatusText = "Not installed",
                Detail = string.Empty,
                Features = new[]
                {
                    MakeFeature("Discord install", "", false)
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
                StatusText = "Incomplete",
                Detail = string.Empty,
                Features = new[]
                {
                    MakeFeature("Discord build", "", false)
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
        var debloatVerifiedSameApp = false;
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
                // Accept any modern kit stamp; require full applied flags for this Discord build path.
                _ = stateVersion;
                var pathMatch = PathsEqual(markerAppDir, appDir);
                markerOk = string.Equals(applyStatus, "applied", StringComparison.Ordinal) &&
                           IsTrue(state, "applied") && IsTrue(state, "fullApply") &&
                           IsTrue(state, "windowsVerified") && IsTrue(state, "debloatVerified") &&
                           pathMatch;
                // Soft-drift recovery for debloat row only needs verified debloat on this build path.
                debloatVerifiedSameApp = IsTrue(state, "debloatVerified") && pathMatch;
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
        features.Add(MakeFeature("Equicord", "", equicordOk));

        // Exo Host: stock shell on _app.asar (large) + host flags. Legacy OpenAsar
        // (small _app.asar rewrite) is intentionally not accepted anymore.
        var innerAsar = Path.Combine(resources, "_app.asar");
        var bootLen = File.Exists(innerAsar) ? new FileInfo(innerAsar).Length : 0L;
        var stockShellOk = bootLen > 1_000_000;
        var hostFlagsOk = false;
        var settingsPathEarly = Path.Combine(appData, "discord", "settings.json");
        if (File.Exists(settingsPathEarly))
        {
            try
            {
                var settingsRaw = File.ReadAllText(settingsPathEarly);
                hostFlagsOk = DiscordLogic.IsQuickStartSettingsJson(settingsRaw);
            }
            catch { /* ignore */ }
        }
        var exoHostOk = equicordOk && hostFlagsOk && stockShellOk;
        features.Add(MakeFeature("Exo Host", "", exoHostOk));

        var kernelOk = false;
        if (File.Exists(versionDll) && File.Exists(ffmpeg) &&
            File.Exists(ffmpegReal) && File.Exists(configIni))
        {
            try
            {
                var config = File.ReadAllText(configIni);
                var bundledKit = Path.Combine(PathHelper.DiscordScriptsDir, "kit");
                // Applied config (EnableTrim/PriorityClass/TrimIntervalMs range) + kit proxy/version hashes.
                // Do not require exact config.ini hash (kit interval may differ from a prior valid apply).
                kernelOk = DiscordLogic.IsKernelApplied(
                    new FileInfo(ffmpeg).Length,
                    new FileInfo(ffmpegReal).Length,
                    new FileInfo(versionDll).Length,
                    config,
                    FilesHaveSameSha256(Path.Combine(bundledKit, "ffmpeg.dll"), ffmpeg),
                    FilesHaveSameSha256(Path.Combine(bundledKit, "version.dll"), versionDll));
            }
            catch { /* ignore */ }
        }
        features.Add(MakeFeature("RAM / latency kernel", "", kernelOk));

        var leftoverAppCount = 0;
        try
        {
            leftoverAppCount = Directory.EnumerateDirectories(discordRoot, "app-*")
                .Count(path => !string.Equals(path, appDir, StringComparison.OrdinalIgnoreCase));
        }
        catch { leftoverAppCount = 1; /* unreadable install is not considered debloated */ }
        var modulesPath = Path.Combine(appDir, "modules");
        // Empty recreated hook/clips dirs are not "present" — need payload files.
        var optionalPayloadCount = new[] { "discord_hook-1", "discord_clips-1" }
            .Count(name => DiscordLogic.ModuleDirHasPayload(Path.Combine(modulesPath, name)));
        var gameSdkCount = 0;
        try
        {
            if (Directory.Exists(modulesPath))
            {
                gameSdkCount = Directory.EnumerateFiles(
                        modulesPath,
                        "discord_game_sdk_*.dll",
                        SearchOption.AllDirectories)
                    .Count();
            }
        }
        catch { gameSdkCount = 1; }
        var localesPath = Path.Combine(appDir, "locales");
        var extraLocaleCount = 0;
        try
        {
            if (Directory.Exists(localesPath))
            {
                extraLocaleCount = Directory.EnumerateFiles(localesPath, "*.pak")
                    .Count(path => !string.Equals(
                        Path.GetFileName(path),
                        "en-US.pak",
                        StringComparison.OrdinalIgnoreCase));
            }
        }
        catch { extraLocaleCount = 1; }
        // Soft-drift recovery only when hard signals clean (aligned with Exo-Discord-Detect.ps1).
        var debloatOk = DiscordLogic.IsClientDebloatApplied(
            leftoverAppCount,
            optionalPayloadCount,
            gameSdkCount,
            extraLocaleCount,
            debloatVerifiedSameApp);
        features.Add(MakeFeature("Client debloat", "", debloatOk));

        var runtimeOk = new[]
        {
            "discord_desktop_core-1", "discord_utils-1", "discord_voice-1", "discord_media-1"
        }.All(name => Directory.Exists(Path.Combine(modulesPath, name)));
        features.Add(MakeFeature("Runtime modules", "", runtimeOk));

        var amoledOk = false;
        var settingsPath = Path.Combine(appData, "discord", "settings.json");
        if (File.Exists(settingsPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (doc.RootElement.TryGetProperty("BACKGROUND_COLOR", out var bg) &&
                    bg.GetString() == "#000000")
                    amoledOk = true;
            }
            catch { /* ignore */ }
        }
        // Equicord AMOLED theme (primary signal — not BACKGROUND_COLOR)
        try
        {
            var eqTheme = Path.Combine(appData, "Equicord", "themes", "amoled-cord.theme.css");
            var eqSettings = Path.Combine(appData, "Equicord", "settings", "settings.json");
            if (File.Exists(eqTheme) && File.Exists(eqSettings))
            {
                var eqRaw = File.ReadAllText(eqSettings);
                if (eqRaw.Contains("amoled", StringComparison.OrdinalIgnoreCase))
                    amoledOk = true;
                else if (File.Exists(eqTheme))
                    amoledOk = true;
            }
            else if (File.Exists(eqTheme))
                amoledOk = true;
        }
        catch { /* ignore */ }

        features.Add(MakeFeature("Dark mode", "", amoledOk));

        var notificationIds = new[]
        {
            "Discord",
            "Discord.Desktop",
            "DiscordInc.Discord",
            "com.squirrel.Discord.Discord"
        };
        // Align with DiscordLogic / detect: present keys must be 0; missing ids ignored; need ≥1 key.
        var toastMap = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            const string notificationsRoot =
                @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
            foreach (var id in notificationIds)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    $@"{notificationsRoot}\{id}");
                if (key is null) { toastMap[id] = null; continue; }
                toastMap[id] = key.GetValue("Enabled") is int enabled ? enabled : null;
            }
        }
        catch
        {
            toastMap.Clear();
        }
        // Windows quiet = OS shell only (Run key, tasks, OS toasts, tray).
        // OPEN_ON_STARTUP in settings.json is a Discord in-app pref — not required.
        var notificationsOk = DiscordLogic.AreToastsOff(toastMap);
        var windowsQuietOk = notificationsOk &&
                             IsStableDiscordRunQuiet(discordRoot) &&
                             AreStableDiscordScheduledTasksDisabled(discordRoot) &&
                             AreStableDiscordTrayEntriesHidden(discordRoot);
        features.Add(MakeFeature("Windows quiet", "", windowsQuietOk));

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
                // Preferred: Update.exe --processStart; legacy: Discord.vbs; also Discord.exe
                launchOk =
                    ((unicode.Contains("Update.exe", StringComparison.OrdinalIgnoreCase) ||
                      utf8.Contains("Update.exe", StringComparison.OrdinalIgnoreCase)) &&
                     (unicode.Contains("processStart", StringComparison.OrdinalIgnoreCase) ||
                      utf8.Contains("processStart", StringComparison.OrdinalIgnoreCase))) ||
                    ((unicode.Contains("Discord.vbs", StringComparison.OrdinalIgnoreCase) ||
                      utf8.Contains("Discord.vbs", StringComparison.OrdinalIgnoreCase)) &&
                     (unicode.Contains("wscript.exe", StringComparison.OrdinalIgnoreCase) ||
                      utf8.Contains("wscript.exe", StringComparison.OrdinalIgnoreCase))) ||
                    unicode.Contains("Discord.exe", StringComparison.OrdinalIgnoreCase) ||
                    utf8.Contains("Discord.exe", StringComparison.OrdinalIgnoreCase);
            }
            catch { /* ignore */ }
        }
        features.Add(MakeFeature("Quiet launch", "", launchOk));

        // Voice QoS DSCP 46 for every installed variant (stable always; PTB/Canary when present).
        var qosOk = true;
        var variantsOk = true;
        foreach (var (name, localDir, appDataDir, exe, qosPolicy) in DiscordLogic.VariantDefinitions)
        {
            var variantRoot = Path.Combine(local, localDir);
            var installed = name == "stable" || Directory.Exists(variantRoot);
            if (!installed) continue;

            qosOk &= IsQosPolicyPresent(qosPolicy, exe);

            if (name == "stable") continue;
            var variantSettings = Path.Combine(appData, appDataDir, "settings.json");
            var variantFlagsOk = false;
            if (File.Exists(variantSettings))
            {
                try
                {
                    variantFlagsOk = DiscordLogic.IsVariantSettingsJson(File.ReadAllText(variantSettings));
                }
                catch { /* ignore */ }
            }
            variantsOk &= DiscordLogic.IsVariantOptimized(
                variantFlagsOk,
                IsStableDiscordRunQuiet(variantRoot),
                IsQosPolicyPresent(qosPolicy, exe));
        }
        features.Add(MakeFeature("Voice priority (QoS)", "", qosOk));
        features.Add(MakeFeature("Apply record", "", markerOk));

        var applied = markerOk && equicordOk && exoHostOk && kernelOk && debloatOk &&
                      windowsQuietOk && amoledOk && runtimeOk && launchOk && qosOk && variantsOk;
        return new OptimizerStateInfo
        {
            IsApplied = applied,
            StatusText = applied ? "All applied" : "Not applied",
            Detail = string.Empty,
            Features = features
        };
    }

    private static bool IsQosPolicyPresent(string policyName, string exe)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Policies\Microsoft\Windows\QoS\{policyName}");
            if (key is null) return false;
            var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in key.GetValueNames())
                map[name] = key.GetValue(name)?.ToString();
            return DiscordLogic.IsQosPolicyMap(map, exe);
        }
        catch { return false; }
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
        try
        {
            var tasksRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "Tasks");
            if (!Directory.Exists(tasksRoot)) return true;

            foreach (var taskPath in Directory.EnumerateFiles(tasksRoot, "*", SearchOption.AllDirectories))
            {
                string xml;
                try
                {
                    xml = File.ReadAllText(taskPath);
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                // Avoid XML allocation for the overwhelming majority of tasks.
                if (!IsTextScopedToRoot(xml, discordRoot)) continue;

                XDocument document;
                try { document = XDocument.Parse(xml, LoadOptions.None); }
                catch { continue; }

                var scopedExec = document.Descendants()
                    .Where(element => element.Name.LocalName == "Exec")
                    .Any(exec => exec.Elements().Any(value =>
                        (value.Name.LocalName is "Command" or "Arguments" or "WorkingDirectory") &&
                        IsTextScopedToRoot(value.Value, discordRoot)));
                if (!scopedExec) continue;

                var enabledText = document.Descendants()
                    .FirstOrDefault(element => element.Name.LocalName == "Enabled")
                    ?.Value;
                var enabled = !bool.TryParse(enabledText, out var parsedEnabled) || parsedEnabled;
                if (enabled)
                {
                    return false;
                }
            }

            return true;
        }
        catch { return false; }
    }

    public Task<OptimizerStateInfo> DetectSteamAsync(
        CancellationToken ct = default,
        bool fastOnly = false) =>
        // Pure C# live probes — no PS detect, no soft markers for greens.
        Task.Run(() => NativeLiveDetect.DetectSteam(), ct);

    private OptimizerStateInfo DetectSteamHeuristic()
    {
        var features = new List<OptimizerFeatureInfo>();
        var steam = FindSteamPath();
        var statePath = Path.Combine(PathHelper.AppDataDir, "steam-optimizer.json");
        var markerOk = false;
        var downloadMarkerOk = false;
        var clientMarkerOk = false;
        var clientHardwareMarkerOk = false;

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
                clientHardwareMarkerOk = IsTrue(root, "clientHardwareAcceleration");
                // Do not pin exact kit version — 1.7.3+ was falsely marked incomplete.
                _ = markerVersion;
                markerOk = string.Equals(applyStatus, "applied", StringComparison.Ordinal) &&
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
                StatusText = "Not installed",
                Detail = string.Empty,
                Features = new[]
                {
                    MakeFeature("Startup quiet", "", false),
                    MakeFeature("CEF launcher", "", false),
                    MakeFeature("Cache / download", "", false),
                    MakeFeature("Client tweaks", "", false),
                    MakeFeature("In-game contention guard", "", false)
                }
            };
        }

        var startupOk = IsSteamStartupQuiet();

        features.Add(MakeFeature("Startup quiet", "", startupOk));
        var launcherPath = Path.Combine(steam, "Steam-Exo.cmd");
        var cefLauncherOk = false;
        if (File.Exists(launcherPath))
        {
            try
            {
                cefLauncherOk = SteamLogic.IsCefLauncherText(File.ReadAllText(launcherPath));
            }
            catch { /* ignore */ }
        }
        var downloadOptimized = downloadMarkerOk && IsSteamDownloadConfigOptimized(steam);
        features.Add(MakeFeature("CEF launcher", "", cefLauncherOk));

        var clientTweaksApplied = clientMarkerOk && AreSteamClientTweaksOptimized(steam);
        features.Add(MakeFeature("Cache / download", "", downloadOptimized));
        features.Add(MakeFeature("Client tweaks", "", clientTweaksApplied));

        var clientHardwareOk = clientHardwareMarkerOk && IsSteamClientHardwareAccelerationEnabled();
        features.Add(MakeFeature("Hardware-accelerated client", "", clientHardwareOk));

        var helperPath = Path.Combine(steam, "Exo-SteamMemoryGuard.ps1");
        var memoryGuardOk = false;
        if (File.Exists(helperPath))
        {
            try
            {
                // Target: 2–15s reclaim (not hard-coded Seconds 5 only)
                memoryGuardOk = SteamLogic.IsMemoryGuardText(File.ReadAllText(helperPath));
            }
            catch { /* ignore */ }
        }
        features.Add(MakeFeature("Background priority policy", "No forced working-set trim", memoryGuardOk));

        var applied = markerOk && startupOk && cefLauncherOk && memoryGuardOk &&
                      downloadOptimized && clientTweaksApplied && clientHardwareOk;
        return new OptimizerStateInfo
        {
            IsApplied = applied,
            StatusText = applied ? "All applied" : "Not applied",
            Detail = string.Empty,
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
            ("H264HWAccel", "1"),
            ("GPUAccelWebViews", "1"),
            ("GPUAccelWebViews2", "1"),
            ("GPUAccelWebViewsD3D11", "1"),
            ("LibraryLowBandwidthMode", "1"),
            ("LibraryLowPerfMode", "1"),
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

    private static bool IsSteamClientHardwareAccelerationEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key is null) return false;
            foreach (var name in new[] { "H264HWAccel", "GPUAccelWebViews", "GPUAccelWebViewsV3" })
            {
                if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
                if (Convert.ToInt32(key.GetValue(name, 0)) != 1) return false;
            }
            return true;
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
            var applied = root.TryGetProperty("isApplied", out var a) && a.ValueKind == JsonValueKind.True;
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
            if (root.TryGetProperty("hardwareSummary", out var hs) && hs.ValueKind == JsonValueKind.String)
                extra["hardwareSummary"] = hs.GetString() ?? "";
            if (root.TryGetProperty("policySource", out var ps) && ps.ValueKind == JsonValueKind.String)
                extra["policySource"] = ps.GetString() ?? "";
            if (root.TryGetProperty("primaryRefreshHz", out var pr))
                extra["primaryRefreshHz"] = pr.ToString();
            if (root.TryGetProperty("primaryMaxRefreshHz", out var pm))
                extra["primaryMaxRefreshHz"] = pm.ToString();

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
        string? lastErrorStage = null;
        string? lastError = null;

        string? gpuName = null;
        string? series = null;
        string? driverTweaksVersion = null;
        string? profileFile = null;
        string? profileVersion = null;
        string? profileSha256 = null;
        string? profileDriverVersion = null;
        bool? gsync = null;
        // Product path is SafePolicy (DRS profile pack only) unless old state says otherwise.
        var safePolicy = true;
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
                if (root.TryGetProperty("safePolicy", out var sp))
                {
                    if (sp.ValueKind == JsonValueKind.False) safePolicy = false;
                    else if (sp.ValueKind == JsonValueKind.True) safePolicy = true;
                }
                if (root.TryGetProperty("policy", out var pol) && pol.ValueKind == JsonValueKind.String)
                {
                    var p = pol.GetString() ?? "";
                    if (p.Contains("safe", StringComparison.OrdinalIgnoreCase) ||
                        p.Contains("drs", StringComparison.OrdinalIgnoreCase))
                        safePolicy = true;
                }

                driverTweaksVersion = ReadString(root, "driverTweaksVersion");
                driverTweaksApplied = IsTrue(root, "driverTweaksVerified") &&
                                      !string.IsNullOrWhiteSpace(driverTweaksVersion);

                profileFile = ReadString(root, "profileFile");
                profileVersion = ReadString(root, "profileVersion");
                profileSha256 = ReadString(root, "profileSha256");
                profileDriverVersion = ReadString(root, "profileDriverVersion");
                var validProfileHash = profileSha256 is { Length: 64 } &&
                                       profileSha256.All(Uri.IsHexDigit);
                // Safe policy: profile applied when marker says so (driver version pair optional).
                if (safePolicy)
                {
                    profileApplied = IsTrue(root, "profileApplied") ||
                                     (validProfileHash && !string.IsNullOrWhiteSpace(profileFile));
                }
                else
                {
                    profileApplied = IsTrue(root, "profileApplied") &&
                                     !string.IsNullOrWhiteSpace(profileFile) &&
                                     !string.IsNullOrWhiteSpace(profileVersion) &&
                                     validProfileHash &&
                                     !string.IsNullOrWhiteSpace(profileDriverVersion) &&
                                     string.Equals(
                                         profileDriverVersion,
                                         driverTweaksVersion,
                                         StringComparison.OrdinalIgnoreCase);
                }

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
                lastErrorStage = ReadString(root, "lastErrorStage");
                lastError = ReadString(root, "lastError");
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

        // Display scaling/color are never forced — Control Panel button only.
        // Applied = Profile Inspector DRS pack (not display prefs).
        if (safePolicy)
        {
            features.Add(MakeFeature("Installed driver", "", gpuOk));
            features.Add(MakeFeature("3D profiles (Profile Inspector)", "", profileApplied && gpuOk));
            features.Add(MakeFeature("Latency / sync policy", "", profileApplied && gpuOk));
            features.Add(MakeFeature("Display scaling & color", "Manual in Control Panel", true));
            features.Add(MakeFeature("NVIDIA Control Panel", "", gpuOk));
        }
        else
        {
            features.Add(MakeFeature("Driver / MSI", "",
                notebookGpu ? gpuOk : (driverTweaksApplied && gpuOk)));
            features.Add(MakeFeature("3D profiles (Profile Inspector)", "", profileApplied && gpuOk));
            features.Add(MakeFeature("Debloat", "", debloatApplied && gpuOk));
            features.Add(MakeFeature("Display scaling & color", "Manual in Control Panel", true));
        }

        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(series))
            extra["series"] = series!;
        if (!string.IsNullOrEmpty(gpuName))
            extra["gpuName"] = gpuName!;
        if (gsync.HasValue)
            extra["gsync"] = gsync.Value ? "true" : "false";
        if (notebookGpu)
            extra["notebookGpu"] = "true";
        extra["safePolicy"] = safePolicy ? "true" : "false";

        // Applied = Profile Inspector DRS only (display scaling/color never gated).
        var applied = safePolicy
            ? hasMarker && !restartPending && !applyInProgress && profileApplied && gpuOk
            : hasMarker && !restartPending && !applyInProgress &&
              (notebookGpu || driverTweaksApplied) &&
              profileApplied && debloatApplied;
        var statusText = !gpuOk
            ? "No NVIDIA GPU"
            : restartPending
            ? "Restart required"
            : applyInProgress && !string.IsNullOrWhiteSpace(lastErrorStage)
                ? $"Failed at {lastErrorStage}"
            : !profileApplied
                ? "3D profile incomplete"
                : !safePolicy && !debloatApplied
                    ? "Background debloat incomplete"
                    : !safePolicy && !notebookGpu && !driverTweaksApplied
                        ? "Driver tweaks incomplete"
                        : applied
                            ? "All applied"
                            : "Not applied";
        var detail = applyInProgress && !string.IsNullOrWhiteSpace(lastError)
            ? lastError!
            : string.Empty;

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
                    StatusText = "Unavailable",
                    Detail = string.Empty,
                    Features = new[]
                    {
                        MakeFeature($"{optimizerName} status", "", false)
                    }
                };
            }
        }, ct);
}

