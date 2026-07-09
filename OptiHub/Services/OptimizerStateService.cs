using System.Text.Json;
using OptiHub.Helpers;
using OptiHub.Models;

namespace OptiHub.Services;

public sealed class OptimizerStateService
{
    private readonly PowerShellRunnerService _runner;
    private readonly ScriptBundleService _scripts;
    private readonly SettingsService _settings;

    public OptimizerStateService(
        PowerShellRunnerService runner,
        ScriptBundleService scripts,
        SettingsService settings)
    {
        _runner = runner;
        _scripts = scripts;
        _settings = settings;
    }

    public async Task<OptimizerStateInfo> DetectDiscordAsync(
        CancellationToken ct = default,
        bool fastOnly = false)
    {
        var heuristic = DetectDiscordHeuristic();
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
            return MakeFeature("Lower memory use", "DiscOpt kernel trims idle RAM and keeps Discord on a higher process priority.", active);
        if (lower.Contains("amoled"))
            return MakeFeature("True black AMOLED theme", "Pure black UI saves OLED power and cuts eye strain at night.", active);
        if (lower.Contains("startup"))
            return MakeFeature("Quieter Windows startup", "Discord stays closed on boot so it is not sitting in the tray.", active);

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

        var appDir = Directory.GetDirectories(discordRoot, "app-*")
            .OrderByDescending(d => d)
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
        var configIni = Path.Combine(appDir, "config.ini");

        var loaderLen = File.Exists(appAsar) ? new FileInfo(appAsar).Length : 0L;
        var equicordOk = File.Exists(equicordAsar) &&
                         loaderLen >= 64 &&
                         loaderLen < 4096;
        features.Add(MakeFeature(
            "Client mods & privacy",
            "Equicord loads privacy plugins and strips noisy telemetry.",
            equicordOk));

        var innerAsar = Path.Combine(resources, "_app.asar"); var openAsarOk = File.Exists(innerAsar) && new FileInfo(innerAsar).Length > 10000 && new FileInfo(innerAsar).Length < 500000;
        features.Add(MakeFeature(
            "Faster Discord startup",
            "OpenASAR replaces the heavy launcher path so Discord opens quicker.",
            openAsarOk));

        var kernelOk = false;
        if (File.Exists(versionDll) && File.Exists(ffmpeg) && File.Exists(configIni))
        {
            try { kernelOk = new FileInfo(ffmpeg).Length < 500_000; }
            catch { /* ignore */ }
        }
        features.Add(MakeFeature(
            "Lower memory use",
            "DiscOpt kernel trims idle RAM and keeps Discord on a higher process priority.",
            kernelOk));

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
        features.Add(MakeFeature(
            "Quieter Windows startup",
            "Discord stays closed on boot so it is not sitting in the tray.",
            startupOk));

        var applied = equicordOk && (kernelOk || openAsarOk);
        return new OptimizerStateInfo
        {
            IsApplied = applied,
            StatusText = applied ? "Already optimized" : "Ready to optimize",
            Detail = applied
                ? "These savings are active. Reapply after Discord updates itself."
                : "Some pieces are missing. Run to finish setup and unlock the savings below.",
            Features = features
        };
    }

    public async Task<OptimizerStateInfo> DetectSteamAsync(
        CancellationToken ct = default,
        bool fastOnly = false)
    {
        var heuristic = DetectSteamHeuristic();
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
        var hasMarker = File.Exists(statePath);

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

        var startupOk = true;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            if (key is not null)
            {
                foreach (var name in key.GetValueNames())
                {
                    var val = key.GetValue(name)?.ToString() ?? "";
                    if (val.Contains("steam.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        startupOk = false;
                        break;
                    }
                }
            }
        }
        catch { /* ignore */ }

        if (hasMarker)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
                if (doc.RootElement.TryGetProperty("startupDisabled", out var sd) &&
                    sd.ValueKind == JsonValueKind.True)
                    startupOk = true;
            }
            catch { /* ignore */ }
        }

        features.Add(MakeFeature(
            "Quieter Windows startup",
            "Steam is not forced to launch when Windows starts.",
            startupOk));
        features.Add(MakeFeature(
            "Lean client caches",
            "HTML/log/temp caches cleaned safely; game installs kept.",
            hasMarker));
        features.Add(MakeFeature(
            "Client config tuned",
            "Download / web-client hints applied when Steam exposes them.",
            hasMarker));

        var applied = hasMarker && startupOk;
        return new OptimizerStateInfo
        {
            IsApplied = applied,
            StatusText = applied ? "Already optimized" : "Ready to optimize",
            Detail = applied
                ? "These savings are active. Reapply after big Steam updates."
                : "Run to quiet startup, clear safe caches, and apply client hints.",
            Features = features
        };
    }

    private static string? FindSteamPath()
    {
        try
        {
            using var hkcu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var p = hkcu?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(Path.Combine(p, "steam.exe")))
                return p.Replace('/', '\\');
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

        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var classic = Path.Combine(pf86, "Steam");
        if (File.Exists(Path.Combine(classic, "steam.exe")))
            return classic;

        return null;
    }

    public async Task<OptimizerStateInfo> DetectNvidiaAsync(
        CancellationToken ct = default,
        bool fastOnly = false)
    {
        var heuristic = DetectNvidiaHeuristic();
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

            return new OptimizerStateInfo
            {
                IsApplied = applied,
                StatusText = status,
                Detail = detail,
                Features = features,
                Extra = extra
            };
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
        var hasMarker = File.Exists(statePath);

        string? gpuName = null;
        string? series = null;
        if (hasMarker)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
                if (doc.RootElement.TryGetProperty("gpuName", out var g))
                    gpuName = g.GetString();
                if (doc.RootElement.TryGetProperty("series", out var s))
                    series = s.GetString();
            }
            catch { /* ignore */ }
        }

        var nvidiaDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation");
        var gpuOk = hasMarker || Directory.Exists(nvidiaDir);

        features.Add(MakeFeature(
            "NVIDIA stack",
            gpuName ?? (gpuOk ? "NVIDIA software present" : "No NVIDIA GPU/driver found yet."),
            gpuOk));
        features.Add(MakeFeature(
            "OptiHub profile applied",
            hasMarker
                ? $"Marker present{(string.IsNullOrEmpty(series) ? "" : $" ({series} Series)")}."
                : "Not applied yet.",
            hasMarker));

        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(series))
            extra["series"] = series!;
        if (!string.IsNullOrEmpty(gpuName))
            extra["gpuName"] = gpuName!;

        return new OptimizerStateInfo
        {
            IsApplied = hasMarker,
            StatusText = hasMarker ? "Already optimized" : (gpuOk ? "Ready to optimize" : "No NVIDIA GPU"),
            Detail = hasMarker
                ? "NVIDIA pack marker found. Refresh for live detect details."
                : "Apply series profile, App/debloat, and display prefs.",
            Features = features,
            Extra = extra
        };
    }
}

