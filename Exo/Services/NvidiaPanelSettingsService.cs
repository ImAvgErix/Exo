using System.Diagnostics;
using System.Text.Json;
using Exo.Models;

namespace Exo.Services;

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

/// <summary>Digital vibrance (DVC) snapshot from Exo.NvDisplay --get-vibrance.</summary>
public sealed class NvidiaDisplayVibranceInfo
{
    public required uint DisplayId { get; init; }
    public required string Connection { get; init; }
    public int CurrentLevel { get; init; }
    public int DefaultLevel { get; init; }
    public int MinimumLevel { get; init; }
    public int MaximumLevel { get; init; } = NvidiaPanelLogic.VibranceDefaultMaximum;
    public string Title =>
        string.IsNullOrWhiteSpace(Connection)
            ? $"Display #{DisplayId}"
            : $"{Connection} · #{DisplayId}";
}

/// <summary>Full Control Panel–style display snapshot from Exo.NvDisplay --list-displays.</summary>
public sealed class NvidiaDisplayInfo
{
    public required uint DisplayId { get; init; }
    public required string Connection { get; init; }
    public string GdiName { get; init; } = "";
    public bool IsPrimary { get; init; }
    public int CurrentWidth { get; init; }
    public int CurrentHeight { get; init; }
    public int CurrentHz { get; init; }
    public string CurrentMode { get; init; } = "";
    public string CurrentDepth { get; init; } = "—";
    public string CurrentRange { get; init; } = "Full RGB";
    public string Scaling { get; init; } = "GPU no-scaling";
    public IReadOnlyList<string> Modes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SupportedDepths { get; init; } = Array.Empty<string>();
    public string Title =>
        (IsPrimary ? "Primary · " : "") +
        (string.IsNullOrWhiteSpace(Connection) ? $"Display #{DisplayId}" : $"{Connection} · #{DisplayId}");
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
            "Exo",
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
        var script = Path.Combine(_scripts.GetNvidiaRoot(), "Exo-Display-Apply.ps1");
        if (!File.Exists(script))
            return (false, "Display apply script missing from Exo kit.");

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

            var script = Path.Combine(_scripts.GetNvidiaRoot(), "Exo-Nvidia-TrayClear.ps1");
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

    /// <summary>List active NVIDIA displays with color bit depths (compat).</summary>
    public async Task<IReadOnlyList<NvidiaDisplayColorInfo>> ListColorDepthsAsync(CancellationToken ct = default)
    {
        var full = await ListDisplaysAsync(ct).ConfigureAwait(false);
        return full.Select(d => new NvidiaDisplayColorInfo
        {
            DisplayId = d.DisplayId,
            Connection = d.Connection,
            CurrentDepth = d.CurrentDepth,
            SupportedDepths = d.SupportedDepths
        }).ToList();
    }

    /// <summary>Full display inventory for Control Panel–style panel (modes, depth, scaling, color).</summary>
    public async Task<IReadOnlyList<NvidiaDisplayInfo>> ListDisplaysAsync(CancellationToken ct = default)
    {
        var result = new List<NvidiaDisplayInfo>();
        try
        {
            var (ok, root, _) = await RunNvDisplayJsonAsync(NvidiaPanelLogic.BuildListDisplaysArgs(), ct)
                .ConfigureAwait(false);
            if (!ok || root is null) return result;
            if (!root.Value.TryGetProperty("displays", out var displays) ||
                displays.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var d in displays.EnumerateArray())
            {
                var id = d.TryGetProperty("displayId", out var idEl) ? idEl.GetUInt32() : 0u;
                var connection = d.TryGetProperty("connection", out var c) ? c.GetString() ?? "" : "";
                var gdi = d.TryGetProperty("gdiName", out var g) ? g.GetString() ?? "" : "";
                var isPrimary = d.TryGetProperty("isPrimary", out var ip) && ip.GetBoolean();
                var cw = d.TryGetProperty("currentWidth", out var wEl) ? wEl.GetInt32() : 0;
                var ch = d.TryGetProperty("currentHeight", out var hEl) ? hEl.GetInt32() : 0;
                var cz = d.TryGetProperty("currentHz", out var zEl) ? zEl.GetInt32() : 0;
                var curMode = d.TryGetProperty("currentMode", out var cm) && cm.ValueKind != JsonValueKind.Null
                    ? cm.GetString() ?? ""
                    : (cw > 0 ? NvidiaPanelLogic.FormatModeLabel(cw, ch, cz) : "");
                var depth = d.TryGetProperty("currentDepth", out var cur) && cur.ValueKind != JsonValueKind.Null
                    ? NvidiaPanelLogic.NormalizeDepthLabel(cur.GetString())
                    : "—";
                var range = d.TryGetProperty("currentRange", out var rg) && rg.ValueKind != JsonValueKind.Null
                    ? NvidiaPanelLogic.NormalizeColorRangeLabel(rg.GetString())
                    : "Full RGB";
                var scaling = d.TryGetProperty("scaling", out var sc) && sc.ValueKind != JsonValueKind.Null
                    ? NvidiaPanelLogic.NormalizeScalingLabel(sc.GetString())
                    : "GPU no-scaling";

                var modes = new List<string>();
                if (d.TryGetProperty("modes", out var modesEl) && modesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in modesEl.EnumerateArray())
                    {
                        var v = m.GetString();
                        if (!string.IsNullOrWhiteSpace(v)) modes.Add(v!);
                    }
                }

                var supported = new List<string>();
                if (d.TryGetProperty("supportedDepths", out var sup) && sup.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in sup.EnumerateArray())
                    {
                        var v = s.GetString();
                        if (!string.IsNullOrWhiteSpace(v))
                            supported.Add(NvidiaPanelLogic.NormalizeDepthLabel(v!));
                    }
                }
                if (supported.Count == 0)
                    supported.AddRange(new[] { "8-bit", "10-bit", "12-bit" });

                result.Add(new NvidiaDisplayInfo
                {
                    DisplayId = id,
                    Connection = connection,
                    GdiName = gdi,
                    IsPrimary = isPrimary,
                    CurrentWidth = cw,
                    CurrentHeight = ch,
                    CurrentHz = cz,
                    CurrentMode = curMode,
                    CurrentDepth = depth,
                    CurrentRange = range,
                    Scaling = scaling,
                    Modes = modes,
                    SupportedDepths = supported
                });
            }
        }
        catch { }
        return result;
    }

    public Task<(bool Success, string Message)> SetColorDepthAsync(
        string depthLabel, uint? displayId = null, CancellationToken ct = default) =>
        RunPanelMutateAsync(
            NvidiaPanelLogic.BuildSetDepthArgs(depthLabel, displayId),
            $"Color depth → {NvidiaPanelLogic.NormalizeDepthLabel(depthLabel)}",
            ct);

    public Task<(bool Success, string Message)> SetModeAsync(
        int width, int height, int hz, uint? displayId = null, CancellationToken ct = default) =>
        RunPanelMutateAsync(
            NvidiaPanelLogic.BuildSetModeArgs(width, height, hz, displayId),
            $"Mode → {NvidiaPanelLogic.FormatModeLabel(width, height, hz)}",
            ct);

    public Task<(bool Success, string Message)> SetScalingAsync(
        string scalingLabel, uint? displayId = null, CancellationToken ct = default) =>
        RunPanelMutateAsync(
            NvidiaPanelLogic.BuildSetScalingArgs(scalingLabel, displayId),
            $"Scaling → {NvidiaPanelLogic.NormalizeScalingLabel(scalingLabel)}",
            ct);

    public Task<(bool Success, string Message)> SetColorRangeAsync(
        string rangeLabel, uint? displayId = null, CancellationToken ct = default) =>
        RunPanelMutateAsync(
            NvidiaPanelLogic.BuildSetColorRangeArgs(rangeLabel, displayId),
            $"NVIDIA color → {NvidiaPanelLogic.NormalizeColorRangeLabel(rangeLabel)}",
            ct);

    /// <summary>Digital vibrance per active display (panel vibrance row source).</summary>
    public async Task<IReadOnlyList<NvidiaDisplayVibranceInfo>> ListVibranceAsync(CancellationToken ct = default)
    {
        var result = new List<NvidiaDisplayVibranceInfo>();
        try
        {
            var (ok, root, _) = await RunNvDisplayJsonAsync(NvidiaPanelLogic.BuildGetVibranceArgs(), ct)
                .ConfigureAwait(false);
            if (!ok || root is null) return result;
            if (!root.Value.TryGetProperty("displays", out var displays) ||
                displays.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var d in displays.EnumerateArray())
            {
                // Entries with an "error" field are displays whose DVC API is unavailable.
                if (d.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                    continue;
                result.Add(new NvidiaDisplayVibranceInfo
                {
                    DisplayId = d.TryGetProperty("displayId", out var idEl) ? idEl.GetUInt32() : 0u,
                    Connection = d.TryGetProperty("connection", out var c) ? c.GetString() ?? "" : "",
                    CurrentLevel = d.TryGetProperty("currentLevel", out var cur) ? cur.GetInt32() : 0,
                    DefaultLevel = d.TryGetProperty("defaultLevel", out var def) ? def.GetInt32() : 0,
                    MinimumLevel = d.TryGetProperty("minimumLevel", out var min) ? min.GetInt32() : NvidiaPanelLogic.VibranceDefaultMinimum,
                    MaximumLevel = d.TryGetProperty("maximumLevel", out var max) ? max.GetInt32() : NvidiaPanelLogic.VibranceDefaultMaximum
                });
            }
        }
        catch { }
        return result;
    }

    public Task<(bool Success, string Message)> SetVibranceAsync(
        int level, uint? displayId = null, CancellationToken ct = default) =>
        RunPanelMutateAsync(
            NvidiaPanelLogic.BuildSetVibranceArgs(level, displayId),
            $"Digital vibrance → {NvidiaPanelLogic.ClampVibranceLevel(level)}",
            ct);

    private async Task<(bool Success, string Message)> RunPanelMutateAsync(
        string args, string successLabel, CancellationToken ct)
    {
        try
        {
            var (ok, root, stderr) = await RunNvDisplayJsonAsync(args, ct).ConfigureAwait(false);
            if (ok && root is not null &&
                root.Value.TryGetProperty("ok", out var okEl) && okEl.GetBoolean())
            {
                // Bit-depth: prefer verified readback so the UI does not lie.
                if (root.Value.TryGetProperty("verified", out var ver) &&
                    ver.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(ver.GetString()))
                {
                    var live = NvidiaPanelLogic.NormalizeDepthLabel(ver.GetString());
                    return (true, $"Color depth → {live} (driver verified).");
                }
                return (true, successLabel + " applied.");
            }

            // Surface real helper errors (unsupported mode, no map, NVAPI down).
            var detail = "";
            if (root is not null)
            {
                if (root.Value.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                    detail = err.GetString() ?? "";
                else if (root.Value.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
                    detail = d.GetString() ?? "";
                else if (root.Value.TryGetProperty("skipped", out var sk) && sk.ValueKind == JsonValueKind.String)
                    detail = sk.GetString() ?? "";
                else if (root.Value.TryGetProperty("verified", out var stuck) &&
                         stuck.ValueKind == JsonValueKind.String)
                    detail = $"panel stayed {NvidiaPanelLogic.NormalizeDepthLabel(stuck.GetString())}";
            }
            if (string.IsNullOrWhiteSpace(detail) && !string.IsNullOrWhiteSpace(stderr))
                detail = stderr.Trim();
            if (string.IsNullOrWhiteSpace(detail))
                detail = "mode may be unsupported on this display or GPU.";
            if (detail.Length > 280)
                detail = detail[..280] + "…";
            return (false, $"Could not apply ({successLabel}): {detail}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Ok, JsonElement? Root, string Stderr)> RunNvDisplayJsonAsync(
        string arguments,
        CancellationToken ct)
    {
        try
        {
            var rootDir = _scripts.GetNvidiaRoot();
            var exe = Path.Combine(rootDir, "tools", "Exo.NvDisplay.exe");
            if (!File.Exists(exe))
                return (false, null, "Exo.NvDisplay.exe missing from kit.");

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
            if (proc is null) return (false, null, "Could not start display helper.");
            var outputTask = proc.StandardOutput.ReadToEndAsync(ct);
            var errorTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var stderr = await errorTask.ConfigureAwait(false);
            var jsonLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(l => l.StartsWith("EXO_NVDISPLAY_JSON:", StringComparison.Ordinal));
            if (jsonLine is null)
                return (false, null, string.IsNullOrWhiteSpace(stderr) ? "No status JSON from display helper." : stderr);
            using var parsed = JsonDocument.Parse(jsonLine["EXO_NVDISPLAY_JSON:".Length..]);
            // Exit 0 OR ok:true in JSON (e.g. skipped Optimus path).
            var jsonOk = parsed.RootElement.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
            return (proc.ExitCode == 0 || jsonOk, parsed.RootElement.Clone(), stderr ?? "");
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
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
            var exe = Path.Combine(root, "tools", "Exo.NvDisplay.exe");
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
            psi.Environment["EXO_PRIMARY_REFRESH"] = "max";
            psi.Environment["EXO_SECONDARY_REFRESH"] = "60";
            psi.Environment["EXO_FULL_RGB"] = "1";
            // GPU + No scaling (peak default)
            psi.Environment["EXO_GPU_NOSCALE"] = "1";

            using var proc = Process.Start(psi);
            if (proc is null)
                return (false, false, false, false, false, "helper failed to start");

            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(l => l.StartsWith("EXO_NVDISPLAY_JSON:", StringComparison.Ordinal));
            if (line is null)
                return (false, false, false, false, false, "no status JSON");

            var json = line["EXO_NVDISPLAY_JSON:".Length..];
            using var doc = JsonDocument.Parse(json);
            var el = doc.RootElement;
            var ok = el.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            // Optimus: no NVIDIA-attached panels — treat as OK (display N/A).
            if (el.TryGetProperty("skipped", out var skipped) &&
                skipped.ValueKind == JsonValueKind.String &&
                (skipped.GetString() ?? "").Contains("no-active-nvidia", StringComparison.OrdinalIgnoreCase))
            {
                return (true, true, true, true, true, "no-active-nvidia-displays");
            }
            var colorOk = false;
            var refreshOk = false;
            var scalingOk = false;
            var registryOk = false;
            if (el.TryGetProperty("checks", out var checks))
            {
                colorOk = checks.TryGetProperty("colorOk", out var c) && c.GetBoolean();
                refreshOk = checks.TryGetProperty("refreshOk", out var r) && r.GetBoolean();
                if (checks.TryGetProperty("modesOk", out var m) && m.GetBoolean())
                    refreshOk = true;
                scalingOk = checks.TryGetProperty("scalingOk", out var s) && s.GetBoolean();
                if (checks.TryGetProperty("pathScalingOk", out var p) && p.GetBoolean())
                    scalingOk = true;
                registryOk = checks.TryGetProperty("registryOk", out var g) && g.GetBoolean();
            }
            // Peak gate: refresh + (registry OR color+scaling)
            if (el.TryGetProperty("checks", out _))
                ok = NvidiaPeakLogic.IsDisplayStatusPeakOk(refreshOk, registryOk, colorOk, scalingOk) || ok;
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

    public bool IsControlPanelInstalled() => ProbeNvidiaControlPanelPresent();

    /// <summary>Launch classic NVIDIA Control Panel when Exo.NvDisplay is unavailable.</summary>
    public bool TryLaunchControlPanel(out string? error)
    {
        error = null;
        try
        {
            foreach (var path in new[]
                     {
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                             @"NVIDIA Corporation\Control Panel Client\nvcplui.exe"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                             @"NVIDIA Corporation\NVIDIA Control Panel\nvcplui.exe")
                     })
            {
                if (!File.Exists(path)) continue;
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }

            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages");
            if (Directory.Exists(local))
            {
                foreach (var pkg in Directory.EnumerateDirectories(local, "NVIDIACorp.NVIDIAControlPanel*"))
                {
                    var aumid = Path.GetFileName(pkg) + "!NVIDIACorp.NVIDIAControlPanel";
                    Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{aumid}")
                    {
                        UseShellExecute = true
                    });
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        error = "NVIDIA Control Panel not found. Run NVIDIA Apply to install it, or use driver settings.";
        return false;
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
                "Exo", "nvidia-optimizer.json");
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
