using System.Text.Json;
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
        var stock = Path.Combine(resources, "_app.asar.stock");
        var versionDll = Path.Combine(appDir, "version.dll");
        var ffmpeg = Path.Combine(appDir, "ffmpeg.dll");
        var configIni = Path.Combine(appDir, "config.ini");

        var equicordOk = File.Exists(equicordAsar) &&
                         File.Exists(appAsar) &&
                         new FileInfo(appAsar).Length < 4096;
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

        _ = _settings;

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
}
