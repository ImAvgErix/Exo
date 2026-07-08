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
        // Fast local heuristic first
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
            var checks = new List<string>();
            if (root.TryGetProperty("checks", out var c) && c.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in c.EnumerateArray())
                {
                    var t = item.GetString();
                    if (!string.IsNullOrWhiteSpace(t)) checks.Add(t!);
                }
            }

            return new OptimizerStateInfo
            {
                IsApplied = applied,
                StatusText = status,
                Detail = detail,
                Checks = checks
            };
        }
        catch
        {
            return heuristic;
        }
    }

    private OptimizerStateInfo DetectDiscordHeuristic()
    {
        var checks = new List<string>();
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
                Detail = "Install Discord stable first, or let the optimizer install it.",
                Checks = new[] { "Discord folder missing under LocalAppData" }
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
                Detail = "No app-* folder found.",
                Checks = new[] { "No active Discord build" }
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
        if (equicordOk) checks.Add("Equicord loader present");
        else checks.Add("Equicord not detected");

        var openAsarOk = File.Exists(stock);
        if (openAsarOk) checks.Add("OpenASAR stock backup present");
        else checks.Add("OpenASAR backup not found");

        var kernelOk = File.Exists(versionDll) && File.Exists(ffmpeg) && File.Exists(configIni);
        if (kernelOk)
        {
            try
            {
                // DiscOpt ffmpeg proxy is small (~24KB); stock is multi-MB
                if (new FileInfo(ffmpeg).Length < 500_000)
                    checks.Add("DiscOpt kernel on disk");
                else
                {
                    kernelOk = false;
                    checks.Add("Stock ffmpeg.dll (kernel not applied)");
                }
            }
            catch
            {
                checks.Add("Kernel files present");
            }
        }
        else checks.Add("DiscOpt kernel missing");

        var lastRun = _settings.Current.LastDiscordRunUtc;
        if (!string.IsNullOrEmpty(lastRun) && DateTime.TryParse(lastRun, out var when))
            checks.Add($"Last OptiHub run: {when.ToLocalTime():g}");

        var applied = equicordOk && (kernelOk || openAsarOk);
        return new OptimizerStateInfo
        {
            IsApplied = applied,
            StatusText = applied ? "Already optimized" : "Ready to optimize",
            Detail = applied
                ? "Optimizations detected. You can reapply after Discord updates."
                : "Run the optimizer to apply performance, privacy, and AMOLED tweaks.",
            Checks = checks
        };
    }
}
