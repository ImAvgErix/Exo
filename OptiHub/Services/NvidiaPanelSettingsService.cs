using System.Diagnostics;
using System.Text.Json;
using OptiHub.Models;

namespace OptiHub.Services;

public sealed class NvidiaPanelSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly ScriptBundleService _scripts;
    private readonly PowerShellRunnerService _powerShell;

    public NvidiaPanelSettingsService(ScriptBundleService scripts, PowerShellRunnerService powerShell)
    {
        _scripts = scripts;
        _powerShell = powerShell;
    }

    public string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OptiHub",
            NvidiaPanelSettings.FileName);

    public NvidiaPanelSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return NvidiaPanelSettings.CreateDefaults();
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<NvidiaPanelSettings>(json, JsonOptions)
                   ?? NvidiaPanelSettings.CreateDefaults();
        }
        catch
        {
            return NvidiaPanelSettings.CreateDefaults();
        }
    }

    public void Save(NvidiaPanelSettings settings)
    {
        var dir = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    /// <summary>
    /// Apply display/color/scaling/video policy only (elevated), using saved settings.
    /// </summary>
    public async Task<(bool Success, string Message)> ApplyDisplayPolicyAsync(
        NvidiaPanelSettings settings,
        IProgress<ScriptRunProgress>? progress = null,
        CancellationToken ct = default)
    {
        Save(settings);
        var script = Path.Combine(_scripts.GetNvidiaRoot(), "OptiHub-Display-Apply.ps1");
        if (!File.Exists(script))
            return (false, "Display apply script missing from OptiHub kit.");

        var result = await _powerShell.RunAsync(
            script,
            arguments: Array.Empty<string>(),
            elevate: true,
            progress: progress,
            cancellationToken: ct,
            workingDirectory: _scripts.GetNvidiaRoot()).ConfigureAwait(false);

        if (result.Success)
            return (true, "OptiHub NVIDIA panel settings applied to the driver (NVAPI + registry).\n\n" + settings.Summary);

        // Surface a short, useful reason (not a wall of log)
        var err = result.ErrorMessage ?? result.Summary ?? "Display apply failed.";
        if (err.Length > 400)
            err = err[..400] + "…";
        return (false,
            "Could not apply panel settings.\n\n" + err +
            "\n\nTip: fully close NVIDIA Control Panel if it is open, then try again.\n" + settings.Summary);
    }

    public async Task<string> GetLiveStatusSummaryAsync(CancellationToken ct = default)
    {
        try
        {
            var root = _scripts.GetNvidiaRoot();
            var exe = Path.Combine(root, "tools", "OptiHub.NvDisplay.exe");
            if (!File.Exists(exe))
                return "Live status helper missing.";

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--status",
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "Could not start display helper.";
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(l => l.StartsWith("OPTIHUB_NVDISPLAY_JSON:", StringComparison.Ordinal));
            if (line is null) return "No live status JSON.";
            var json = line["OPTIHUB_NVDISPLAY_JSON:".Length..];
            using var doc = JsonDocument.Parse(json);
            var rootEl = doc.RootElement;
            var ok = rootEl.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            var checks = rootEl.TryGetProperty("checks", out var c) ? c.ToString() : "";
            return ok ? $"Driver policy OK — {checks}" : $"Driver policy incomplete — {checks}";
        }
        catch (Exception ex)
        {
            return $"Live status error: {ex.Message}";
        }
    }
}
