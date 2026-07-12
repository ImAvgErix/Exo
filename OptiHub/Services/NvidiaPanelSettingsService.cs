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
    /// <summary>False = needs full Apply profile on NVIDIA card (e.g. 3D pack import).</summary>
    public bool CanApplyFromPanel { get; init; } = true;
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
        // Always enforce fixed refresh policy
        settings.PrimaryRefresh = "max";
        settings.SecondaryRefresh = "60";
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public async Task<IReadOnlyList<NvidiaPolicyProbeItem>> ProbePolicyAsync(CancellationToken ct = default)
    {
        var items = new List<NvidiaPolicyProbeItem>();
        var nv = await ReadNvDisplayStatusAsync(ct).ConfigureAwait(false);

        items.Add(new NvidiaPolicyProbeItem
        {
            Id = "full-rgb",
            Title = "Full RGB",
            IsApplied = nv.ColorOk,
            Detail = string.Empty,
            CanApplyFromPanel = true
        });

        items.Add(new NvidiaPolicyProbeItem
        {
            Id = "gpu-scale",
            Title = "GPU no-scaling + override",
            IsApplied = nv.ScalingOk && nv.RegistryOk,
            Detail = string.Empty,
            CanApplyFromPanel = true
        });

        items.Add(new NvidiaPolicyProbeItem
        {
            Id = "refresh",
            Title = "Primary max Hz · secondary 60 Hz",
            IsApplied = nv.RefreshOk,
            Detail = string.Empty,
            CanApplyFromPanel = true
        });

        var videoOk = ProbeVideoNvidia();
        items.Add(new NvidiaPolicyProbeItem
        {
            Id = "video",
            Title = "Video color + image (NVIDIA)",
            IsApplied = videoOk,
            Detail = string.Empty,
            CanApplyFromPanel = true
        });

        var overlayOff = ProbeOverlayOff();
        items.Add(new NvidiaPolicyProbeItem
        {
            Id = "overlay",
            Title = "Overlay / ShadowPlay off",
            IsApplied = overlayOff,
            Detail = string.Empty,
            CanApplyFromPanel = true
        });

        var countersOk = ProbeDeveloperCounters();
        items.Add(new NvidiaPolicyProbeItem
        {
            Id = "counters",
            Title = "GPU performance counters",
            IsApplied = countersOk,
            Detail = string.Empty,
            CanApplyFromPanel = true
        });

        var appGone = !ProbeNvidiaAppPresent();
        var cplGone = !ProbeNvidiaControlPanelPresent();
        items.Add(new NvidiaPolicyProbeItem
        {
            Id = "clients",
            Title = "No NVIDIA App / Control Panel",
            IsApplied = appGone && cplGone,
            Detail = string.Empty,
            CanApplyFromPanel = true
        });

        var trayClean = ProbeTrayClean();
        items.Add(new NvidiaPolicyProbeItem
        {
            Id = "tray",
            Title = "No NVIDIA tray icon",
            IsApplied = trayClean,
            Detail = string.Empty,
            CanApplyFromPanel = true
        });

        var profileOk = Probe3dProfileApplied();
        items.Add(new NvidiaPolicyProbeItem
        {
            Id = "3d-profile",
            Title = "3D profiles",
            IsApplied = profileOk,
            Detail = string.Empty,
            CanApplyFromPanel = false
        });

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

        // Always clear tray after display apply (service soft-refresh re-registers icons)
        await ClearTrayIconsAsync(ct).ConfigureAwait(false);

        if (result.Success)
            return (true, "Applied.");

        var err = result.ErrorMessage ?? result.Summary ?? "Display apply failed.";
        if (err.Length > 450)
            err = err[..450] + "…";
        return (false, err);
    }

    public async Task<(bool Success, string Message)> ClearTrayIconsAsync(CancellationToken ct = default)
    {
        try
        {
            // Prefer elevated script for thorough wipe; also do user-level wipe inline
            ClearTrayIconsLocal();

            var script = Path.Combine(_scripts.GetNvidiaRoot(), "OptiHub-Nvidia-TrayClear.ps1");
            if (File.Exists(script))
            {
                var result = await _powerShell.RunAsync(
                    script,
                    elevate: true,
                    cancellationToken: ct,
                    workingDirectory: _scripts.GetNvidiaRoot()).ConfigureAwait(false);
                if (result.Success)
                    return (true, "NVIDIA tray icons cleared. Open the overflow once if Windows still caches a name.");
                // Local clear may still have helped
            }

            var clean = ProbeTrayClean();
            return clean
                ? (true, "NVIDIA tray icon entries removed.")
                : (false, "Some NVIDIA tray entries remain (often NVDisplay.Container). Cleared what we could.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<string> GetLiveStatusSummaryAsync(CancellationToken ct = default)
    {
        var nv = await ReadNvDisplayStatusAsync(ct).ConfigureAwait(false);
        return nv.Ok
            ? $"Driver policy OK — {nv.Detail}"
            : $"Driver policy incomplete — {nv.Detail}";
    }

    private static void ClearTrayIconsLocal()
    {
        try
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Control Panel\NotifyIconSettings", writable: true);
            if (root is null) return;
            foreach (var name in root.GetSubKeyNames().ToArray())
            {
                try
                {
                    using var key = root.OpenSubKey(name);
                    var exe = key?.GetValue("ExecutablePath") as string ?? "";
                    if (exe.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                        exe.Contains("nvcontainer", StringComparison.OrdinalIgnoreCase) ||
                        exe.Contains("NVDisplay", StringComparison.OrdinalIgnoreCase) ||
                        exe.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                        exe.Contains("ShadowPlay", StringComparison.OrdinalIgnoreCase))
                    {
                        root.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                    }
                }
                catch { }
            }
        }
        catch { }

        // Leftover App ProgramData
        try
        {
            var pd = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"NVIDIA Corporation\NVIDIA App");
            if (Directory.Exists(pd))
            {
                try { Directory.Delete(pd, recursive: true); } catch { }
            }
        }
        catch { }
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
            // Ensure refresh policy env for status verification
            psi.Environment["OPTIHUB_PRIMARY_REFRESH"] = "max";
            psi.Environment["OPTIHUB_SECONDARY_REFRESH"] = "60";
            psi.Environment["OPTIHUB_FULL_RGB"] = "1";
            psi.Environment["OPTIHUB_GPU_NOSCALE"] = "1";

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

    private static bool ProbeOverlayOff()
    {
        try
        {
            // Soft check: common overlay enable keys off or missing
            using var sp = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\NVIDIA Corporation\Global\ShadowPlay\NVSPCAPS");
            if (sp is null) return true;
            var v = sp.GetValue("OverlayState");
            if (v is int i) return i == 0;
        }
        catch { }
        return true;
    }

    private static bool ProbeDeveloperCounters()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\nvlddmkm\Parameters\Global\NVTweak");
            if (k is null) return false;
            var prof = k.GetValue("RmProfilingAdminOnly");
            return prof is int p && p == 0;
        }
        catch { return false; }
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
                return Directory.EnumerateDirectories(local, "NVIDIACorp.NVIDIAControlPanel*").Any();
        }
        catch { }
        return false;
    }

    private static bool ProbeTrayClean()
    {
        try
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Control Panel\NotifyIconSettings");
            if (root is null) return true;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                var exe = key?.GetValue("ExecutablePath") as string ?? "";
                if (exe.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                    exe.Contains("nvcontainer", StringComparison.OrdinalIgnoreCase) ||
                    exe.Contains("NVDisplay", StringComparison.OrdinalIgnoreCase) ||
                    exe.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }
        catch { }
        return true;
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
        catch { return false; }
    }
}
