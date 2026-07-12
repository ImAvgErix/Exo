using System.Diagnostics;
using System.Text.Json;
using OptiHub.Models;

namespace OptiHub.Services;

public sealed class NvidiaPolicyProbeItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required bool IsApplied { get; init; }
    public required string Detail { get; init; }
}

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
    /// Probe live driver/registry for each OptiHub NVIDIA policy row.
    /// </summary>
    public async Task<IReadOnlyList<NvidiaPolicyProbeItem>> ProbePolicyAsync(CancellationToken ct = default)
    {
        var items = new List<NvidiaPolicyProbeItem>();

        // Live NVAPI display helper
        var nv = await ReadNvDisplayStatusAsync(ct).ConfigureAwait(false);
        var colorOk = nv.ColorOk;
        var refreshOk = nv.RefreshOk;
        var scalingOk = nv.ScalingOk;
        var registryOk = nv.RegistryOk;
        var detailBase = nv.Detail;

        items.Add(new NvidiaPolicyProbeItem
        {
            Id = "full-rgb",
            Title = "Full RGB / Full dynamic range",
            IsApplied = colorOk,
            Detail = colorOk ? "Driver reports Full RGB" : "Apply sets Full RGB via NVAPI"
        });

        items.Add(new NvidiaPolicyProbeItem
        {
            Id = "gpu-scale",
            Title = "GPU no-scaling + override games",
            IsApplied = scalingOk && registryOk,
            Detail = scalingOk && registryOk
                ? "GPU no-scaling and override active"
                : "Apply sets GPU no-scaling and override"
        });

        items.Add(new NvidiaPolicyProbeItem
        {
            Id = "refresh",
            Title = "Primary highest Hz · secondary 60 Hz",
            IsApplied = refreshOk,
            Detail = refreshOk
                ? "Primary highest available, secondary 60 Hz"
                : "Apply sets primary highest (gaming), secondary 60 Hz"
        });

        var videoOk = ProbeVideoNvidia();
        items.Add(new NvidiaPolicyProbeItem
        {
            Id = "video",
            Title = "Video color + image (NVIDIA settings)",
            IsApplied = videoOk,
            Detail = videoOk
                ? "Video sources use NVIDIA settings"
                : "Apply forces NVIDIA video color and image settings"
        });

        var appGone = !ProbeNvidiaAppPresent();
        var cplGone = !ProbeNvidiaControlPanelPresent();
        items.Add(new NvidiaPolicyProbeItem
        {
            Id = "clients",
            Title = "No NVIDIA App / Control Panel",
            IsApplied = appGone && cplGone,
            Detail = appGone && cplGone
                ? "Clients removed — OptiHub is the panel"
                : appGone
                    ? "App gone, Control Panel still present — use Apply profile to strip CPL"
                    : "Still installed — use Apply profile on the NVIDIA card to strip App + CPL"
        });

        var profileOk = Probe3dProfileApplied();
        items.Add(new NvidiaPolicyProbeItem
        {
            Id = "3d-profile",
            Title = "3D performance profiles (DRS)",
            IsApplied = profileOk,
            Detail = profileOk
                ? "Base + game profiles applied"
                : "Use Apply profile on the NVIDIA card (full pass imports 3D packs)"
        });

        if (!string.IsNullOrWhiteSpace(detailBase))
        {
            // Append helper detail into refresh row only for debugging density
        }

        return items;
    }

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
            return (true, "OptiHub NVIDIA policy applied to the driver.");

        var err = result.ErrorMessage ?? result.Summary ?? "Display apply failed.";
        if (err.Length > 400)
            err = err[..400] + "…";
        return (false, err);
    }

    public async Task<string> GetLiveStatusSummaryAsync(CancellationToken ct = default)
    {
        var nv = await ReadNvDisplayStatusAsync(ct).ConfigureAwait(false);
        return nv.Ok
            ? $"Driver policy OK — {nv.Detail}"
            : $"Driver policy incomplete — {nv.Detail}";
    }

    private async Task<(bool Ok, bool ColorOk, bool RefreshOk, bool ScalingOk, bool RegistryOk, string Detail)> ReadNvDisplayStatusAsync(
        CancellationToken ct)
    {
        try
        {
            var root = _scripts.GetNvidiaRoot();
            var exe = Path.Combine(root, "tools", "OptiHub.NvDisplay.exe");
            if (!File.Exists(exe))
                return (false, false, false, false, false, "helper missing");

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
            if (proc is null)
                return (false, false, false, false, false, "helper failed to start");

            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(l => l.StartsWith("OPTIHUB_NVDISPLAY_JSON:", StringComparison.Ordinal));
            if (line is null)
                return (false, false, false, false, false, "no status JSON");

            var json = line["OPTIHUB_NVDISPLAY_JSON:".Length..];
            using var doc = JsonDocument.Parse(json);
            var el = doc.RootElement;
            var ok = el.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            var colorOk = false;
            var refreshOk = false;
            var scalingOk = false;
            var registryOk = false;
            if (el.TryGetProperty("checks", out var checks))
            {
                colorOk = checks.TryGetProperty("colorOk", out var c) && c.GetBoolean();
                refreshOk = checks.TryGetProperty("refreshOk", out var r) && r.GetBoolean();
                scalingOk = checks.TryGetProperty("scalingOk", out var s) && s.GetBoolean();
                registryOk = checks.TryGetProperty("registryOk", out var g) && g.GetBoolean();
            }
            var detail = $"color={colorOk}, refresh={refreshOk}, scaling={scalingOk}, registry={registryOk}";
            return (ok, colorOk, refreshOk, scalingOk, registryOk, detail);
        }
        catch (Exception ex)
        {
            return (false, false, false, false, false, ex.Message);
        }
    }

    private static bool ProbeVideoNvidia()
    {
        try
        {
            using var devices = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\NVIDIA Corporation\Global\NVTweak\Devices");
            if (devices is null) return false;
            foreach (var name in devices.GetSubKeyNames())
            {
                using var video = devices.OpenSubKey(name + @"\Video");
                if (video is null) continue;
                var c = video.GetValue("VideoColorSettingsSource");
                var i = video.GetValue("VideoImageSettingsSource");
                if (c is int ci && i is int ii && ci == 1 && ii == 1)
                    return true;
            }
        }
        catch { }
        return false;
    }

    private static bool ProbeNvidiaAppPresent()
    {
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"NVIDIA Corporation\NVIDIA App\NVIDIA App.exe")
        };
        return paths.Any(File.Exists);
    }

    private static bool ProbeNvidiaControlPanelPresent()
    {
        try
        {
            // Appx package name
            // Lightweight: known install paths + WindowsApps folder pattern
            var paths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    @"NVIDIA Corporation\Control Panel Client\nvcplui.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    @"NVIDIA Corporation\NVIDIA Control Panel\nvcplui.exe")
            };
            if (paths.Any(File.Exists)) return true;
            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Packages");
            if (Directory.Exists(local))
            {
                return Directory.EnumerateDirectories(local, "NVIDIACorp.NVIDIAControlPanel*")
                    .Any();
            }
        }
        catch { }
        return false;
    }

    private static bool Probe3dProfileApplied()
    {
        try
        {
            var statePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OptiHub", "nvidia-optimizer.json");
            if (!File.Exists(statePath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
            var root = doc.RootElement;
            var profile = root.TryGetProperty("profileApplied", out var p) && p.GetBoolean();
            var games = root.TryGetProperty("gameProfilesApplied", out var g) && g.GetBoolean();
            return profile && games;
        }
        catch
        {
            return false;
        }
    }
}
