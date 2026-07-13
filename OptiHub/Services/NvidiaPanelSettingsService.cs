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

public sealed class NvidiaDisplayColorInfo
{
    public required uint DisplayId { get; init; }
    public required string Connection { get; init; }
    public required string CurrentDepth { get; init; }
    public required IReadOnlyList<string> SupportedDepths { get; init; }
    public string Title =>
        string.IsNullOrWhiteSpace(Connection)
            ? $"Display #{DisplayId}"
            : $"{Connection} · #{DisplayId}";
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

    /// <summary>List active NVIDIA displays with current/supported color bit depths (NVAPI).</summary>
    public async Task<IReadOnlyList<NvidiaDisplayColorInfo>> ListColorDepthsAsync(CancellationToken ct = default)
    {
        var result = new List<NvidiaDisplayColorInfo>();
        try
        {
            var (ok, root) = await RunNvDisplayJsonAsync("--list-color", ct).ConfigureAwait(false);
            if (!ok || root is null) return result;
            if (!root.Value.TryGetProperty("displays", out var displays) ||
                displays.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var d in displays.EnumerateArray())
            {
                var id = d.TryGetProperty("displayId", out var idEl) ? idEl.GetUInt32() : 0u;
                var connection = d.TryGetProperty("connection", out var c) ? c.GetString() ?? "" : "";
                var current = d.TryGetProperty("currentDepth", out var cur) && cur.ValueKind != JsonValueKind.Null
                    ? cur.GetString() ?? ""
                    : "";
                var supported = new List<string>();
                if (d.TryGetProperty("supportedDepths", out var sup) && sup.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in sup.EnumerateArray())
                    {
                        var v = s.GetString();
                        if (!string.IsNullOrWhiteSpace(v))
                            supported.Add(NormalizeDepthLabel(v!));
                    }
                }
                if (supported.Count == 0)
                    supported.AddRange(new[] { "8-bit", "10-bit", "12-bit" });

                result.Add(new NvidiaDisplayColorInfo
                {
                    DisplayId = id,
                    Connection = connection,
                    CurrentDepth = NormalizeDepthLabel(current),
                    SupportedDepths = supported
                });
            }
        }
        catch { }
        return result;
    }

    /// <summary>Set color bit depth on one or all NVIDIA displays via elevated NVAPI helper.</summary>
    public async Task<(bool Success, string Message)> SetColorDepthAsync(
        string depthLabel,
        uint? displayId = null,
        CancellationToken ct = default)
    {
        var depthArg = ToDepthCliArg(depthLabel);
        if (depthArg is null)
            return (false, "Invalid color depth. Use 8-bit, 10-bit, or 12-bit.");

        try
        {
            // Non-elevated first (NVAPI often works in-session).
            var args = displayId is null or 0
                ? $"--set-depth {depthArg}"
                : $"--set-depth {depthArg} --display-id {displayId.Value}";
            var (ok, root) = await RunNvDisplayJsonAsync(args, ct).ConfigureAwait(false);
            if (!(ok && root is not null &&
                  root.Value.TryGetProperty("ok", out var okEl) && okEl.GetBoolean()))
            {
                // Elevated script path for locked sessions.
                var script = Path.Combine(_scripts.GetNvidiaRoot(), "OptiHub-ColorDepth-Set.ps1");
                if (!File.Exists(script))
                    return (false, "Color depth script missing from OptiHub kit.");

                var psArgs = displayId is null or 0
                    ? new[] { "-Depth", depthArg }
                    : new[] { "-Depth", depthArg, "-DisplayId", displayId.Value.ToString() };
                var result = await _powerShell.RunAsync(
                    script,
                    arguments: psArgs,
                    elevate: true,
                    cancellationToken: ct,
                    workingDirectory: _scripts.GetNvidiaRoot()).ConfigureAwait(false);
                ok = result.Success;
                // Re-list to verify after elevate
                if (ok)
                {
                    var list = await ListColorDepthsAsync(ct).ConfigureAwait(false);
                    var want = NormalizeDepthLabel(depthLabel);
                    if (displayId is null or 0)
                        ok = list.Count == 0 || list.Any(d =>
                            string.Equals(d.CurrentDepth, want, StringComparison.OrdinalIgnoreCase));
                    else
                        ok = list.Any(d => d.DisplayId == displayId.Value &&
                            string.Equals(d.CurrentDepth, want, StringComparison.OrdinalIgnoreCase))
                             || list.Count == 0;
                }
            }

            if (ok)
            {
                var label = NormalizeDepthLabel(depthLabel);
                return (true, displayId is null or 0
                    ? $"Color depth set to {label}."
                    : $"Color depth set to {label} on display #{displayId}.");
            }

            return (false, "Could not set color depth. Check that the monitor supports this mode.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Ok, JsonElement? Root)> RunNvDisplayJsonAsync(
        string arguments,
        CancellationToken ct)
    {
        try
        {
            var rootDir = _scripts.GetNvidiaRoot();
            var exe = Path.Combine(rootDir, "tools", "OptiHub.NvDisplay.exe");
            if (!File.Exists(exe))
                return (false, null);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, null);
            var output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var jsonLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(l => l.StartsWith("OPTIHUB_NVDISPLAY_JSON:", StringComparison.Ordinal));
            if (jsonLine is null) return (false, null);
            using var parsed = JsonDocument.Parse(jsonLine["OPTIHUB_NVDISPLAY_JSON:".Length..]);
            return (proc.ExitCode == 0, parsed.RootElement.Clone());
        }
        catch
        {
            return (false, null);
        }
    }

    private static string NormalizeDepthLabel(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "—";
        var s = raw.Trim().ToUpperInvariant().Replace(" ", "");
        if (s is "BPC8" or "8" or "8BPC" or "8-BIT" or "8BIT") return "8-bit";
        if (s is "BPC10" or "10" or "10BPC" or "10-BIT" or "10BIT") return "10-bit";
        if (s is "BPC12" or "12" or "12BPC" or "12-BIT" or "12BIT") return "12-bit";
        if (s is "BPC6" or "6") return "6-bit";
        if (raw.Contains("8", StringComparison.Ordinal)) return "8-bit";
        if (raw.Contains("10", StringComparison.Ordinal)) return "10-bit";
        if (raw.Contains("12", StringComparison.Ordinal)) return "12-bit";
        return raw;
    }

    private static string? ToDepthCliArg(string label)
    {
        var n = NormalizeDepthLabel(label);
        return n switch
        {
            "8-bit" => "8",
            "10-bit" => "10",
            "12-bit" => "12",
            "6-bit" => "6",
            _ => null
        };
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
                    using var key = root.OpenSubKey(name, writable: true);
                    var exe = key?.GetValue("ExecutablePath") as string ?? "";
                    var isNvidia =
                        exe.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                        exe.Contains("nvcontainer", StringComparison.OrdinalIgnoreCase) ||
                        exe.Contains("NVDisplay", StringComparison.OrdinalIgnoreCase) ||
                        exe.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                        exe.Contains("ShadowPlay", StringComparison.OrdinalIgnoreCase) ||
                        exe.Contains("nv_dispi", StringComparison.OrdinalIgnoreCase);
                    if (!isNvidia) continue;

                    // Display container re-registers if deleted — hide instead.
                    var isDisplay =
                        exe.Contains("NVDisplay", StringComparison.OrdinalIgnoreCase) ||
                        exe.Contains("Display.NvContainer", StringComparison.OrdinalIgnoreCase) ||
                        exe.Contains("nv_dispi", StringComparison.OrdinalIgnoreCase);
                    if (isDisplay)
                    {
                        key?.SetValue("IsPromoted", 0, Microsoft.Win32.RegistryValueKind.DWord);
                        continue;
                    }

                    root.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                }
                catch { }
            }
        }
        catch { }

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
                var isNvidia =
                    exe.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                    exe.Contains("nvcontainer", StringComparison.OrdinalIgnoreCase) ||
                    exe.Contains("NVDisplay", StringComparison.OrdinalIgnoreCase) ||
                    exe.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                    exe.Contains("nv_dispi", StringComparison.OrdinalIgnoreCase);
                if (!isNvidia) continue;

                // Display container left with IsPromoted=0 is considered clean (must not delete).
                var isDisplay =
                    exe.Contains("NVDisplay", StringComparison.OrdinalIgnoreCase) ||
                    exe.Contains("Display.NvContainer", StringComparison.OrdinalIgnoreCase) ||
                    exe.Contains("nv_dispi", StringComparison.OrdinalIgnoreCase);
                if (isDisplay)
                {
                    var promoted = key?.GetValue("IsPromoted");
                    if (promoted is int i && i == 0) continue;
                    // Missing IsPromoted often still shows — treat as dirty
                    return false;
                }

                // Any App/GFE/overlay entry is dirty
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
