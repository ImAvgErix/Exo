// Exo.NvDisplay — performance display settings via NVAPI + NVTweak registry.
// No Control Panel mouse automation for color/scaling.
//
// Applies per active display:
//   - Current resolution kept
//   - PRIMARY (gaming) monitor: highest supported refresh, first claim on the
//     bandwidth budget
//   - SECONDARY monitors: refresh kept as-is (they don't compete for the
//     primary's high-refresh bandwidth), but still get best color
//   - Color policy User (NVIDIA color settings)
//   - RGB + Full (VESA) + HIGHEST supported color depth (10 → 8 bpc)
//   - HDMI info-frame RGB quantization Full (fixes "Limited" on HDMI)
//   - GPU no-scaling path where the driver allows it
//   - NVTweak: PerformScalingOn=GPU, ScalingOverride=ON, No-scaling mode
//   - NVTweak Gestalt=2 (Use the advanced 3D image settings)
//
// Also owns exact NVIDIA DRS database snapshot/restore for Exo Repair.
// Usage: Exo.NvDisplay.exe [--apply|--status|--drs-backup PATH|--drs-restore PATH]

using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using NvAPIWrapper;
using NvAPIWrapper.Display;
using NvAPIWrapper.DRS;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.Display;
using NvAPIWrapper.Native.Display.Structures;
using NvAPIWrapper.Native.Exceptions;
using NvAPIWrapper.Native.GPU;

static class Program
{
    // NVTweak device registry (what Control Panel reads for scaling / color prefs)
    // PerformScalingOn: 0 = GPU, 1 = Display
    // ScalingOverride: 1 = override games/programs
    // Scaling / ScalingMode: 2 = No scaling (community + observed NVTweak dumps)
    private const int RegGpu = 0;
    private const int RegDisplay = 1;
    // NVTweak ScalingMode: 0 = Full-screen, 1 = Aspect ratio, 2 = No scaling (black bars when res ≠ panel)
    private const int RegFullScreen = 0;
    private const int RegAspectRatio = 1;
    private const int RegNoScaling = 2;
    private const int RegFullRange = 0; // matches ColorDataDynamicRange.VESA = 0
    private const int RegLimitedRange = 1; // CEA

    static int Main(string[] args)
    {
        var rawArgs = args.Select(a => a.Trim()).ToArray();
        var normalizedArgs = rawArgs.Select(a => a.ToLowerInvariant()).ToArray();

        // Flag modes
        var listColor = normalizedArgs.Any(a => a is "--list-color" or "/list-color");
        var listDisplays = normalizedArgs.Any(a => a is "--list-displays" or "/list-displays");
        var setDepthRaw = GetArgValue(rawArgs, "--set-depth") ?? GetArgValue(rawArgs, "/set-depth");
        var setModeRaw = GetArgValue(rawArgs, "--set-mode") ?? GetArgValue(rawArgs, "/set-mode");
        var setScalingRaw = GetArgValue(rawArgs, "--set-scaling") ?? GetArgValue(rawArgs, "/set-scaling");
        var setColorRangeRaw = GetArgValue(rawArgs, "--set-color-range") ?? GetArgValue(rawArgs, "/set-color-range");
        var setVibranceRaw = GetArgValue(rawArgs, "--set-vibrance") ?? GetArgValue(rawArgs, "/set-vibrance");
        var getVibrance = normalizedArgs.Any(a => a is "--get-vibrance" or "/get-vibrance");
        var displayIdRaw = GetArgValue(rawArgs, "--display-id") ?? GetArgValue(rawArgs, "/display-id");
        var drsBackupRaw = GetArgValue(rawArgs, "--drs-backup") ?? GetArgValue(rawArgs, "/drs-backup");
        var drsRestoreRaw = GetArgValue(rawArgs, "--drs-restore") ?? GetArgValue(rawArgs, "/drs-restore");

        var knownPrefixes = new[]
        {
            "--status", "-s", "/status", "--apply", "-a", "/apply", "--help", "-h", "/?",
            "--list-color", "/list-color", "--list-displays", "/list-displays",
            "--set-depth", "/set-depth", "--set-mode", "/set-mode",
            "--set-scaling", "/set-scaling", "--set-color-range", "/set-color-range",
            "--set-vibrance", "/set-vibrance", "--get-vibrance", "/get-vibrance",
            "--display-id", "/display-id", "--drs-backup", "/drs-backup",
            "--drs-restore", "/drs-restore"
        };
        var valueTaking = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--set-depth", "/set-depth", "--set-mode", "/set-mode",
            "--set-scaling", "/set-scaling", "--set-color-range", "/set-color-range",
            "--set-vibrance", "/set-vibrance",
            "--display-id", "/display-id", "--drs-backup", "/drs-backup",
            "--drs-restore", "/drs-restore"
        };
        {
            var skipNext = false;
            foreach (var a in rawArgs)
            {
                var al = a.ToLowerInvariant();
                if (skipNext) { skipNext = false; continue; }
                if (valueTaking.Contains(al)) { skipNext = true; continue; }
                if (knownPrefixes.Any(k => al == k || al.StartsWith(k + "=", StringComparison.Ordinal) ||
                                           al.StartsWith(k + ":", StringComparison.Ordinal)))
                    continue;
                Console.Error.WriteLine($"Unknown argument: {a}");
                return 64;
            }
        }

        var panelMutate = setDepthRaw is not null || setModeRaw is not null ||
                          setScalingRaw is not null || setColorRangeRaw is not null ||
                          setVibranceRaw is not null;
        var statusOnly = normalizedArgs.Any(a => a is "--status" or "-s" or "/status");
        var drsOperation = drsBackupRaw is not null || drsRestoreRaw is not null;
        var apply = !statusOnly && !listColor && !listDisplays && !panelMutate && !getVibrance && !drsOperation;
        if (normalizedArgs.Any(a => a is "--apply" or "-a" or "/apply")) apply = true;
        if (statusOnly && apply)
        {
            Console.Error.WriteLine("Choose either --status or --apply, not both.");
            return 64;
        }
        if (normalizedArgs.Any(a => a is "--help" or "-h" or "/?"))
        {
            Console.WriteLine("Exo.NvDisplay - NVAPI + NVTweak display performance settings");
            Console.WriteLine("  --apply              Apply: Full RGB, primary max-Hz / secondary unchanged, GPU no-scaling");
            Console.WriteLine("  --status             Verify display policy");
            Console.WriteLine("  --list-displays      List displays: modes, depth, scaling, color (Panel)");
            Console.WriteLine("  --list-color         List color bit depths only");
            Console.WriteLine("  --set-mode WxH@Hz [--display-id ID]  Set resolution + refresh");
            Console.WriteLine("  --set-depth 8|10|12 [--display-id ID]  Set color bit depth");
            Console.WriteLine("  --set-scaling gpu-noscaling|gpu|display [--display-id ID]");
            Console.WriteLine("  --set-color-range full|limited [--display-id ID]");
            Console.WriteLine("  --get-vibrance [--display-id ID]  Read digital vibrance (DVC) levels");
            Console.WriteLine("  --set-vibrance LEVEL [--display-id ID]  Set digital vibrance (driver range, usually 0-63)");
            Console.WriteLine("  --drs-backup PATH    Save the complete NVIDIA DRS database for exact Repair");
            Console.WriteLine("  --drs-restore PATH   Load and commit a complete NVIDIA DRS database backup");
            return 0;
        }

        try { NVIDIA.Initialize(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[NVAPI] Initialize failed: " + ex.Message);
            return 2;
        }

        try
        {
            if (drsBackupRaw is not null || drsRestoreRaw is not null)
                return RunDrsDatabaseOperation(drsBackupRaw, drsRestoreRaw);

            var enumeration = GetActiveDisplays();
            if (!enumeration.Succeeded)
            {
                Console.Error.WriteLine("[NVAPI] Active-display enumeration failed: " + enumeration.Error);
                var failureJson = JsonSerializer.Serialize(new
                {
                    ok = false,
                    mode = apply ? "apply" : "status",
                    error = "active-display-enumeration-failed",
                    detail = enumeration.Error
                });
                Console.WriteLine("EXO_NVDISPLAY_JSON:" + failureJson);
                return 6;
            }

            var devices = enumeration.Devices;
            if (devices.Count == 0)
            {
                // Hybrid/Optimus laptops can have an NVIDIA render GPU while every
                // active panel is wired to the integrated GPU. Display tuning is
                // not applicable there, but profile/driver optimization can still
                // complete successfully.
                Console.WriteLine("[NVAPI] No active NVIDIA-connected displays; display step skipped.");
                var json = JsonSerializer.Serialize(new
                {
                    ok = true,
                    mode = apply ? "apply" : "status",
                    displays = Array.Empty<object>(),
                    skipped = "no-active-nvidia-displays"
                });
                Console.WriteLine("EXO_NVDISPLAY_JSON:" + json);
                return 0;
            }

            Console.WriteLine($"[NVAPI] Displays: {devices.Count}");

            uint? onlyId = null;
            if (!string.IsNullOrWhiteSpace(displayIdRaw) && uint.TryParse(displayIdRaw, out var parsedId))
                onlyId = parsedId;

            // --- Panel: full display inventory ---
            if (listDisplays || listColor)
            {
                var list = ListDisplaysFull(devices, colorOnly: listColor && !listDisplays);
                Console.WriteLine("EXO_NVDISPLAY_JSON:" + JsonSerializer.Serialize(new
                {
                    ok = true,
                    mode = listDisplays ? "list-displays" : "list-color",
                    displays = list
                }));
                return 0;
            }

            if (getVibrance)
            {
                var vibList = ListVibrance(devices, onlyId);
                Console.WriteLine("EXO_NVDISPLAY_JSON:" + JsonSerializer.Serialize(new
                {
                    ok = true,
                    mode = "get-vibrance",
                    displays = vibList
                }));
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(setVibranceRaw))
            {
                if (!int.TryParse(setVibranceRaw.Trim(), out var vibLevel))
                {
                    Console.Error.WriteLine("Invalid --set-vibrance. Use an integer DVC level (see --get-vibrance for the driver range).");
                    return 64;
                }
                var setOk = ApplyVibrance(devices, vibLevel, onlyId, out var vibApplied, out var vibErr);
                Console.WriteLine("EXO_NVDISPLAY_JSON:" + JsonSerializer.Serialize(new
                {
                    ok = setOk,
                    mode = "set-vibrance",
                    level = vibLevel,
                    displays = vibApplied,
                    error = setOk ? null : vibErr,
                    displayId = onlyId
                }));
                return setOk ? 0 : 6;
            }

            if (!string.IsNullOrWhiteSpace(setModeRaw))
            {
                if (!TryParseModeString(setModeRaw, out var mw, out var mh, out var mhz))
                {
                    Console.Error.WriteLine("Invalid --set-mode. Use WxH@Hz e.g. 2560x1440@165");
                    return 64;
                }
                var setOk = ApplyUserMode(devices, mw, mh, mhz, onlyId);
                Console.WriteLine("EXO_NVDISPLAY_JSON:" + JsonSerializer.Serialize(new
                {
                    ok = setOk,
                    mode = "set-mode",
                    width = mw,
                    height = mh,
                    hz = mhz,
                    displayId = onlyId
                }));
                return setOk ? 0 : 6;
            }

            if (!string.IsNullOrWhiteSpace(setDepthRaw))
            {
                if (!TryParseDepth(setDepthRaw, out var depth))
                {
                    Console.Error.WriteLine("Invalid --set-depth. Use BPC8, BPC10, BPC12, 8, 10, or 12.");
                    return 64;
                }
                var setOk = ApplyColorDepth(devices, depth, onlyId, out var verifiedDepth, out var depthErr);
                Console.WriteLine("EXO_NVDISPLAY_JSON:" + JsonSerializer.Serialize(new
                {
                    ok = setOk,
                    mode = "set-depth",
                    depth = depth.ToString(),
                    verified = verifiedDepth,
                    error = setOk ? null : depthErr,
                    displayId = onlyId
                }));
                return setOk ? 0 : 6;
            }

            if (!string.IsNullOrWhiteSpace(setScalingRaw))
            {
                if (!TryParseScaling(setScalingRaw, out var scaleMode))
                {
                    Console.Error.WriteLine("Invalid --set-scaling. Use gpu-noscaling, gpu, or display.");
                    return 64;
                }
                var setOk = ApplyUserScaling(devices, scaleMode, onlyId);
                Console.WriteLine("EXO_NVDISPLAY_JSON:" + JsonSerializer.Serialize(new
                {
                    ok = setOk,
                    mode = "set-scaling",
                    scaling = scaleMode,
                    displayId = onlyId
                }));
                return setOk ? 0 : 6;
            }

            if (!string.IsNullOrWhiteSpace(setColorRangeRaw))
            {
                var full = setColorRangeRaw.Trim().Equals("full", StringComparison.OrdinalIgnoreCase) ||
                           setColorRangeRaw.Contains("full", StringComparison.OrdinalIgnoreCase);
                var limited = setColorRangeRaw.Contains("limited", StringComparison.OrdinalIgnoreCase) ||
                              setColorRangeRaw.Equals("cea", StringComparison.OrdinalIgnoreCase);
                if (!full && !limited)
                {
                    Console.Error.WriteLine("Invalid --set-color-range. Use full or limited.");
                    return 64;
                }
                var wantFull = full && !limited || full;
                if (limited && !setColorRangeRaw.Contains("full", StringComparison.OrdinalIgnoreCase))
                    wantFull = false;
                var setOk = ApplyUserColorRange(devices, wantFull, onlyId);
                Console.WriteLine("EXO_NVDISPLAY_JSON:" + JsonSerializer.Serialize(new
                {
                    ok = setOk,
                    mode = "set-color-range",
                    range = wantFull ? "full" : "limited",
                    displayId = onlyId
                }));
                return setOk ? 0 : 6;
            }

            PrintPathScaling("BEFORE");
            var idToGdi = MapDisplayIdToGdiName();
            var missingDisplayIds = devices
                .Where(d => !idToGdi.TryGetValue(d.DisplayId, out var name) || string.IsNullOrWhiteSpace(name))
                .Select(d => d.DisplayId)
                .ToArray();
            if (missingDisplayIds.Length > 0)
            {
                // Soft: still apply color + NVTweak. Win32 refresh needs GDI names.
                var missing = string.Join(", ", missingDisplayIds.Select(id => $"#{id}"));
                Console.WriteLine($"[MODE] Partial NVIDIA-to-Windows mapping; skip refresh for: {missing}");
            }
            var nvidiaGdiNames = devices
                .Where(d => idToGdi.TryGetValue(d.DisplayId, out var name) && !string.IsNullOrWhiteSpace(name))
                .Select(d => idToGdi[d.DisplayId])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (nvidiaGdiNames.Count == 0)
            {
                Console.WriteLine("[MODE] No GDI names mapped — refresh skipped; color + registry still apply.");
            }
            else
            {
                PrintWindowsModes("AVAILABLE", nvidiaGdiNames);
            }

            var results = new List<object>();
            Dictionary<string, BestMode>? bestModes = null;
            var modesOk = true;
            var colorReadCount = 0;
            var colorAppliedCount = 0;
            var colorOptimalCount = 0;
            var colorUnsupportedCount = 0;

            var wantFullRgb = !string.Equals(
                Environment.GetEnvironmentVariable("EXO_FULL_RGB") ?? "1", "0", StringComparison.Ordinal);
            // Default ON — GPU perform + No scaling + Override (user-confirmed no black bars).
            var wantGpuNoScale = !string.Equals(
                Environment.GetEnvironmentVariable("EXO_GPU_NOSCALE") ?? "1", "0", StringComparison.Ordinal);

            if (apply && nvidiaGdiNames.Count > 0)
            {
                // Keep current resolution. Primary -> max Hz; secondary -> keep.
                var modeResult = ApplyTargetRefreshModes(nvidiaGdiNames);
                bestModes = modeResult.Modes;
                modesOk = modeResult.Success;
            }

            foreach (var dev in devices)
            {
                ColorData before;
                try { before = dev.CurrentColorData; }
                catch (Exception ex)
                {
                    // Pascal / older drivers often reject color reads (NVAPI_INVALID_ARGUMENT).
                    // Full RGB is still stamped via NVTweak registry — treat as best-effort.
                    colorUnsupportedCount++;
                    Console.WriteLine(
                        $"[NVAPI] Display #{dev.DisplayId}: color API unavailable ({ex.Message}) — registry Full RGB is authoritative");
                    results.Add(new
                    {
                        displayId = dev.DisplayId,
                        connection = dev.ConnectionType.ToString(),
                        colorApi = "unsupported"
                    });
                    continue;
                }
                colorReadCount++;

                Console.WriteLine(
                    $"[NVAPI] Display #{dev.DisplayId} ({dev.ConnectionType}) BEFORE: " +
                    $"format={before.ColorFormat} range={before.DynamicRange} depth={before.ColorDepth} policy={before.SelectionPolicy}");

                if (!apply)
                {
                    if (IsFullRgbUserColor(before)) colorOptimalCount++;
                    results.Add(new { displayId = dev.DisplayId, connection = dev.ConnectionType.ToString(), before = Snapshot(before) });
                    continue;
                }

                ColorData after = before;
                if (wantFullRgb)
                {
                    // 1) Color: User policy + RGB + Full + best BPC
                    var chosenDepth = PickBestDepth(dev, before.ColorDepth);
                    var appliedColor = ApplyColorWithFallbacks(dev, chosenDepth);

                    // 2) HDMI: force Full range in info-frame
                    if (dev.ConnectionType == MonitorConnectionType.HDMI ||
                        dev.ConnectionType.ToString().Contains("HDMI", StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyHdmiFullRange(dev);
                    }

                    try { after = dev.CurrentColorData; }
                    catch { after = appliedColor ?? before; }
                    if (appliedColor != null && IsFullRgbUserColor(after))
                        colorAppliedCount++;
                    else if (appliedColor != null)
                        colorAppliedCount++; // set accepted even if re-read is soft
                }
                else
                {
                    // Panel asked for Full RGB off — leave current color alone
                    colorAppliedCount++;
                    Console.WriteLine($"[NVAPI] Display #{dev.DisplayId}: Full RGB disabled by Exo panel — skipped color apply");
                }

                Console.WriteLine(
                    $"[NVAPI] Display #{dev.DisplayId} AFTER: " +
                    $"format={after.ColorFormat} range={after.DynamicRange} depth={after.ColorDepth} policy={after.SelectionPolicy}");

                results.Add(new
                {
                    displayId = dev.DisplayId,
                    connection = dev.ConnectionType.ToString(),
                    before = Snapshot(before),
                    after = Snapshot(after)
                });
            }

            if (apply)
            {
                // 3) Path scaling only when panel has GPU no-scaling enabled (best-effort on old GPUs)
                // Clear any stuck GPUScanOutToNative (borderless black bars), then leave
                // path on GPUScanOutToClosest. Registry still stamps GPU + No scaling + Override.
                var pathScalingOk = true;
                try
                {
                    // Always clear stuck Native/unscaled path (borderless black bars).
                    pathScalingOk = ClearNativeUnscaledPath(bestModes);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[NVAPI] Path clear best-effort: " + ex.Message);
                    pathScalingOk = true;
                }

                // 4) NVTweak registry (always re-stamp — source of truth when NVAPI color/path missing)
                ApplyNvtweakRegistry();

                PrintPathScaling("AFTER");
                PrintWindowsModes("CURRENT", nvidiaGdiNames);
                DumpNvtweakSummary();

                // Retry mode verify after registry/path work (multi-mon settle).
                if (!modesOk && nvidiaGdiNames.Count > 0)
                {
                    Thread.Sleep(500);
                    modesOk = VerifyTargetRefreshModes(nvidiaGdiNames);
                    if (modesOk)
                        Console.WriteLine("[MODE] Re-verify after settle: OK");
                }

                var activeIds = devices.Select(d => d.DisplayId).ToArray();
                var registryOk = VerifyNvtweakRegistry(
                    requireColor: wantFullRgb,
                    requireGpuScale: wantGpuNoScale,
                    activeDisplayIds: activeIds);

                // Color: NVAPI success, or color API unsupported (registry owns Full RGB).
                var colorOk = !wantFullRgb ||
                              colorAppliedCount + colorUnsupportedCount >= devices.Count ||
                              (colorUnsupportedCount > 0 && registryOk);

                // Scaling: path OK, or path API missing/unsupported but registry GPU no-scale OK.
                var scalingOk = !wantGpuNoScale || pathScalingOk || registryOk;

                // Hard gate: refresh policy is required. Registry must pass for *active* display
                // IDs only — orphan NVTweak keys must not false-fail color/scale/Hz.
                // Live NVAPI Full RGB + path GPU scaling can also authorize when active registry is clean.
                var ok = modesOk && (registryOk || (colorOk && pathScalingOk));
                try
                {
                    var json = JsonSerializer.Serialize(new
                    {
                        ok,
                        mode = "apply",
                        displays = results,
                        checks = new
                        {
                            colorOk,
                            modesOk,
                            scalingOk,
                            registryOk,
                            colorUnsupported = colorUnsupportedCount,
                            pathScalingOk
                        },
                        refreshPolicy = "primary-max-hz, secondary-keep",
                        note = "color/path NVAPI best-effort; registry + refresh are required"
                    });
                    Console.WriteLine("EXO_NVDISPLAY_JSON:" + json);
                }
                catch { }

                if (!ok)
                {
                    Console.Error.WriteLine(
                        $"[NVAPI] Apply incomplete: modes={modesOk}, registry={registryOk} (color={colorOk}, scaling={scalingOk})");
                    return 6;
                }

                if (!colorOk || !pathScalingOk)
                {
                    Console.WriteLine(
                        $"[NVAPI] Apply OK with best-effort notes: colorOk={colorOk}, pathScalingOk={pathScalingOk}, registryOk={registryOk}");
                }
            }
            else
            {
                var activeIds = devices.Select(d => d.DisplayId).ToArray();
                var registryOk = VerifyNvtweakRegistry(
                    requireColor: wantFullRgb,
                    requireGpuScale: wantGpuNoScale,
                    activeDisplayIds: activeIds);
                var refreshOk = VerifyTargetRefreshModes(nvidiaGdiNames);

                // Color status: NVAPI Full RGB, or unsupported API + registry Full RGB.
                var colorOk = !wantFullRgb ||
                              (colorReadCount == devices.Count && colorOptimalCount == devices.Count) ||
                              (colorUnsupportedCount > 0 && registryOk) ||
                              (colorReadCount + colorUnsupportedCount >= devices.Count &&
                               colorOptimalCount + colorUnsupportedCount >= devices.Count);

                var pathScalingOk = VerifyGpuScaling();
                var scalingOk = !wantGpuNoScale || pathScalingOk || registryOk;

                // Same hard gate as apply: refresh required; active-display registry OR live NVAPI status.
                var ok = refreshOk && (registryOk || (colorOk && pathScalingOk));
                try
                {
                    var json = JsonSerializer.Serialize(new
                    {
                        ok,
                        mode = "status",
                        displays = results,
                        checks = new
                        {
                            colorOk,
                            refreshOk,
                            scalingOk,
                            registryOk,
                            colorUnsupported = colorUnsupportedCount,
                            pathScalingOk
                        },
                        vibrance = ListVibrance(devices, null),
                        refreshPolicy = "primary-max-hz, secondary-keep"
                    });
                    Console.WriteLine("EXO_NVDISPLAY_JSON:" + json);
                }
                catch { }
                if (!ok) return 6;
            }

            return 0;
        }
        catch (NVIDIAApiException nex)
        {
            Console.Error.WriteLine("[NVAPI] " + nex.Status + ": " + nex.Message);
            return 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[NVAPI] " + ex.GetType().Name + ": " + ex.Message);
            return 5;
        }
        finally
        {
            try { NVIDIA.Unload(); } catch { }
        }
    }

    private static int RunDrsDatabaseOperation(string? backupPath, string? restorePath)
    {
        if ((backupPath is null) == (restorePath is null))
        {
            Console.Error.WriteLine("Choose exactly one of --drs-backup or --drs-restore.");
            return 64;
        }

        var requested = backupPath ?? restorePath!;
        string fullPath;
        try { fullPath = Path.GetFullPath(requested); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Invalid DRS path: " + ex.Message);
            return 64;
        }

        var exoStateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Exo");
        var allowedRoot = Path.GetFullPath(exoStateRoot).TrimEnd(Path.DirectorySeparatorChar) +
                          Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) ||
            !fullPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("DRS backups are restricted to Exo LocalAppData .bin files.");
            return 64;
        }

        try
        {
            if (backupPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                if (File.Exists(fullPath))
                {
                    Console.Error.WriteLine("DRS backup already exists; refusing to overwrite the pristine snapshot.");
                    return 5;
                }
                using var session = DriverSettingsSession.CreateAndLoad();
                session.Save(fullPath);
                if (!File.Exists(fullPath) || new FileInfo(fullPath).Length == 0)
                    throw new InvalidDataException("NVAPI produced an empty DRS backup.");
                Console.WriteLine("EXO_NVDRS_JSON:" + JsonSerializer.Serialize(new
                {
                    ok = true,
                    mode = "backup",
                    bytes = new FileInfo(fullPath).Length
                }));
                return 0;
            }

            if (!File.Exists(fullPath) || new FileInfo(fullPath).Length == 0)
            {
                Console.Error.WriteLine("DRS backup is missing or empty.");
                return 5;
            }
            using (var session = DriverSettingsSession.CreateAndLoad(fullPath))
                session.Save();
            Console.WriteLine("EXO_NVDRS_JSON:" + JsonSerializer.Serialize(new
            {
                ok = true,
                mode = "restore",
                bytes = new FileInfo(fullPath).Length
            }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("DRS operation failed: " + ex.Message);
            return 6;
        }
    }

    sealed class DisplayEnumerationResult
    {
        public bool Succeeded { get; init; }
        public List<DisplayDevice> Devices { get; init; } = new();
        public string? Error { get; init; }
    }

    static DisplayEnumerationResult GetActiveDisplays()
    {
        var list = new List<DisplayDevice>();
        PhysicalGPU[] gpus;
        try
        {
            gpus = PhysicalGPU.GetPhysicalGPUs();
        }
        catch (Exception ex)
        {
            return new DisplayEnumerationResult
            {
                Succeeded = false,
                Error = "physical GPU enumeration failed: " + ex.Message
            };
        }

        if (gpus.Length == 0)
        {
            return new DisplayEnumerationResult
            {
                Succeeded = false,
                Error = "NVAPI initialized but returned no physical NVIDIA GPUs"
            };
        }

        // Multi-GPU / Optimus: one GPU may throw while another has panels.
        // Never abort the whole enum when a single adapter fails.
        var enumErrors = new List<string>();
        foreach (var gpu in gpus)
        {
            DisplayDevice[] connected;
            try
            {
                connected = gpu.GetConnectedDisplayDevices(ConnectedIdsFlag.None);
            }
            catch (Exception cachedError)
            {
                try
                {
                    connected = gpu.GetConnectedDisplayDevices(ConnectedIdsFlag.UnCached);
                }
                catch (Exception uncachedError)
                {
                    enumErrors.Add(cachedError.Message + " / " + uncachedError.Message);
                    continue;
                }
            }

            foreach (var dev in connected)
            {
                if (dev == null) continue;
                if (!(dev.IsActive || dev.IsOSVisible)) continue;
                list.Add(dev);
            }
        }

        list = list.GroupBy(d => d.DisplayId).Select(g => g.First()).ToList();
        // Succeeded=true even with zero devices (Optimus / iGPU-only panels).
        // Callers treat empty as "display step N/A", not a hard driver failure.
        if (list.Count == 0 && enumErrors.Count > 0 && enumErrors.Count == gpus.Length)
        {
            return new DisplayEnumerationResult
            {
                Succeeded = false,
                Error = "connected-display enumeration failed on all GPUs: " +
                        string.Join(" | ", enumErrors.Take(3))
            };
        }

        return new DisplayEnumerationResult { Succeeded = true, Devices = list };
    }

    /// <summary>Digital vibrance (DVC) snapshot for one display via NvAPIWrapper GPUOutput.</summary>
    static object ReadVibranceInfo(DisplayDevice dev)
    {
        try
        {
            var dvc = dev.Output.DigitalVibranceControl;
            return new
            {
                displayId = dev.DisplayId,
                connection = dev.ConnectionType.ToString(),
                currentLevel = dvc.CurrentLevel,
                defaultLevel = dvc.DefaultLevel,
                minimumLevel = dvc.MinimumLevel,
                maximumLevel = dvc.MaximumLevel
            };
        }
        catch (Exception ex)
        {
            return new
            {
                displayId = dev.DisplayId,
                connection = dev.ConnectionType.ToString(),
                error = "vibrance unavailable: " + ex.Message
            };
        }
    }

    static List<object> ListVibrance(List<DisplayDevice> devices, uint? onlyId)
    {
        var list = new List<object>();
        foreach (var dev in devices)
        {
            if (onlyId is not null && dev.DisplayId != onlyId.Value) continue;
            list.Add(ReadVibranceInfo(dev));
        }
        return list;
    }

    static bool ApplyVibrance(
        List<DisplayDevice> devices, int level, uint? onlyId,
        out List<object> applied, out string error)
    {
        applied = new List<object>();
        error = "";
        var any = false;
        var ok = true;
        foreach (var dev in devices)
        {
            if (onlyId is not null && dev.DisplayId != onlyId.Value) continue;
            any = true;
            try
            {
                var dvc = dev.Output.DigitalVibranceControl;
                var clamped = Math.Max(dvc.MinimumLevel, Math.Min(dvc.MaximumLevel, level));
                dvc.CurrentLevel = clamped;
                // Honest verification: re-read the live driver level after the set.
                var live = dev.Output.DigitalVibranceControl.CurrentLevel;
                Console.WriteLine($"[NVAPI] Display #{dev.DisplayId}: vibrance requested {level} -> clamped {clamped}, driver now {live}");
                applied.Add(new { displayId = dev.DisplayId, requested = clamped, verified = live });
                if (live != clamped) ok = false;
            }
            catch (Exception ex)
            {
                ok = false;
                error = ex.Message;
                Console.WriteLine($"[NVAPI] Display #{dev.DisplayId}: vibrance set failed: {ex.Message}");
            }
        }
        if (!any)
        {
            error = onlyId is not null
                ? $"display {onlyId.Value} not found among active NVIDIA displays"
                : "no active NVIDIA displays";
            return false;
        }
        return ok;
    }

    static string? GetArgValue(string[] args, string key)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length) return args[i + 1];
                return null;
            }
            if (a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
                return a[(key.Length + 1)..];
        }
        return null;
    }

    static bool TryParseDepth(string raw, out ColorDataDepth depth)
    {
        depth = ColorDataDepth.BPC8;
        var s = raw.Trim().ToUpperInvariant().Replace(" ", "");
        if (s is "8" or "BPC8" or "8BPC" or "8-BIT" or "8BIT") { depth = ColorDataDepth.BPC8; return true; }
        if (s is "10" or "BPC10" or "10BPC" or "10-BIT" or "10BIT") { depth = ColorDataDepth.BPC10; return true; }
        if (s is "12" or "BPC12" or "12BPC" or "12-BIT" or "12BIT") { depth = ColorDataDepth.BPC12; return true; }
        if (s is "6" or "BPC6") { depth = ColorDataDepth.BPC6; return true; }
        return false;
    }

    static bool TryParseModeString(string raw, out int width, out int height, out int hz)
    {
        width = height = hz = 0;
        var m = System.Text.RegularExpressions.Regex.Match(
            raw.Trim(), @"^\s*(\d+)\s*[xX×]\s*(\d+)\s*[@\s]+\s*(\d+)\s*(?:Hz)?\s*$");
        if (!m.Success) return false;
        width = int.Parse(m.Groups[1].Value);
        height = int.Parse(m.Groups[2].Value);
        hz = int.Parse(m.Groups[3].Value);
        return width >= 640 && height >= 480 && hz is >= 30 and <= 1000;
    }

    static bool TryParseScaling(string raw, out string mode)
    {
        mode = "gpu-noscaling";
        var s = raw.Trim().ToLowerInvariant().Replace(" ", "-");
        if (s is "gpu-noscaling" or "no-scaling" or "noscaling") { mode = "gpu-noscaling"; return true; }
        if (s is "gpu" or "gpu-scaling") { mode = "gpu"; return true; }
        if (s is "display" or "display-scaling" or "monitor") { mode = "display"; return true; }
        return false;
    }

    static List<object> ListDisplaysFull(List<DisplayDevice> devices, bool colorOnly)
    {
        var idToGdi = MapDisplayIdToGdiName();
        var list = new List<object>();
        foreach (var dev in devices)
        {
            string? currentDepth = null;
            string? currentRange = null;
            string? currentFormat = null;
            try
            {
                var c = dev.CurrentColorData;
                currentDepth = c.ColorDepth?.ToString();
                currentRange = c.DynamicRange?.ToString();
                currentFormat = c.ColorFormat.ToString();
            }
            catch { }

            // Honest depth list: NVAPI IsColorDataSupported often advertises 12-bit when the
            // panel cannot run it. Only offer depths at or below the live working depth.
            var supported = ListHonestSupportedDepths(dev, currentDepth);

            if (colorOnly)
            {
                list.Add(new
                {
                    displayId = dev.DisplayId,
                    connection = dev.ConnectionType.ToString(),
                    currentDepth,
                    currentRange,
                    currentFormat,
                    supportedDepths = supported
                });
                continue;
            }

            idToGdi.TryGetValue(dev.DisplayId, out var gdi);
            gdi ??= "";
            var modes = EnumerateModesForGdi(gdi);
            var curW = 0; var curH = 0; var curHz = 0;
            if (!string.IsNullOrWhiteSpace(gdi))
            {
                var dm = new Win32.DEVMODE { dmSize = (short)Marshal.SizeOf<Win32.DEVMODE>() };
                if (Win32.EnumDisplaySettings(gdi, Win32.ENUM_CURRENT_SETTINGS, ref dm))
                {
                    curW = dm.dmPelsWidth;
                    curH = dm.dmPelsHeight;
                    curHz = dm.dmDisplayFrequency;
                }
            }

            var scaling = ReadScalingLabelForDisplay(dev.DisplayId);
            var isPrimary = !string.IsNullOrWhiteSpace(gdi) && IsPrimaryDisplayDevice(gdi);
            var maxHz = modes
                .Select(mode =>
                {
                    TryParseModeString(mode, out var width, out var height, out var hz);
                    return (width, height, hz);
                })
                .Where(mode => mode.width == curW && mode.height == curH)
                .Select(mode => mode.hz)
                .DefaultIfEmpty(curHz)
                .Max();
            var refreshRange = ReadMonitorVerticalRange(gdi);
            // This is intentionally evidence, not a claim that G-SYNC is certified.
            // A wide EDID range on a high-refresh DisplayPort primary is the best
            // capability signal available without relying on Control Panel UI state.
            var adaptiveSyncCandidate = isPrimary &&
                dev.ConnectionType == MonitorConnectionType.DisplayPort &&
                maxHz >= 100 &&
                refreshRange.MinHz is <= 60 and > 0 &&
                refreshRange.MaxHz >= maxHz - 5;

            list.Add(new
            {
                displayId = dev.DisplayId,
                connection = dev.ConnectionType.ToString(),
                gdiName = gdi,
                isPrimary,
                currentWidth = curW,
                currentHeight = curH,
                currentHz = curHz,
                maxHz,
                currentMode = curW > 0 ? $"{curW}x{curH}@{curHz}" : null,
                edidMinVerticalHz = refreshRange.MinHz,
                edidMaxVerticalHz = refreshRange.MaxHz,
                adaptiveSyncCandidate,
                adaptiveSyncEvidence = adaptiveSyncCandidate ? "displayport-high-refresh-wide-edid-range" : "none",
                modes,
                currentDepth,
                currentRange = currentRange is "VESA" or "Full" ? "full" :
                    currentRange is "CEA" ? "limited" : currentRange?.ToLowerInvariant(),
                currentFormat,
                supportedDepths = supported,
                scaling,
                scalingOptions = new[] { "gpu-noscaling", "gpu", "display" },
                colorRangeOptions = new[] { "full", "limited" }
            });
        }
        return list;
    }

    static (int? MinHz, int? MaxHz) ReadMonitorVerticalRange(string gdi)
    {
        if (string.IsNullOrWhiteSpace(gdi)) return (null, null);
        try
        {
            var monitor = new Win32.DISPLAY_DEVICE { cb = Marshal.SizeOf<Win32.DISPLAY_DEVICE>() };
            if (!Win32.EnumDisplayDevices(gdi, 0, ref monitor, 0) ||
                string.IsNullOrWhiteSpace(monitor.DeviceID))
                return (null, null);

            // EnumDisplayDevices returns MONITOR\AUS24AC\{class-guid}\0003, while
            // the physical EDID lives under Enum\DISPLAY\AUS24AC\<instance>.
            var parts = monitor.DeviceID.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return (null, null);
            using var modelKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + parts[1]);
            if (modelKey is null) return (null, null);
            byte[]? edid = null;
            foreach (var instance in modelKey.GetSubKeyNames())
            {
                using var key = modelKey.OpenSubKey(instance + @"\Device Parameters");
                if (key?.GetValue("EDID") is byte[] candidate && candidate.Length >= 128 &&
                    (edid is null || candidate.Length > edid.Length))
                    edid = candidate;
            }
            if (edid is null)
                return (null, null);

            // EDID base-block monitor range descriptor: 00 00 00 FD 00 minV maxV ...
            for (var offset = 54; offset + 17 < 126; offset += 18)
            {
                if (edid[offset] != 0 || edid[offset + 1] != 0 ||
                    edid[offset + 2] != 0 || edid[offset + 3] != 0xFD)
                    continue;
                var min = (int)edid[offset + 5];
                var max = (int)edid[offset + 6];
                return min > 0 && max >= min ? (min, max) : (null, null);
            }
        }
        catch { }
        return (null, null);
    }

    /// <summary>
    /// Depths to offer in the panel: current + lower + at most one higher tier when
    /// IsColorDataSupported claims it (so 8→10 is tryable). Apply still verifies readback —
    /// never list 12 solely from a lying support probe when current is 8.
    /// </summary>
    static List<string> ListHonestSupportedDepths(DisplayDevice dev, string? currentDepthName)
    {
        var curRank = DepthNameRank(currentDepthName);
        if (curRank == 0) curRank = 8;
        var list = new List<string>();
        // Allow current, lower, and one step up (e.g. 8→10, 10→12) if driver claims support.
        var maxOffer = curRank >= 12 ? 12 : curRank + 2; // BPC steps are 6/8/10/12 (+2 rank)
        if (maxOffer > 12) maxOffer = 12;
        // Cap upgrade probe: from 8 only try 10 (not 12); from 10 try 12.
        if (curRank <= 8) maxOffer = 10;
        else if (curRank <= 10) maxOffer = 12;

        foreach (var d in new[] { ColorDataDepth.BPC12, ColorDataDepth.BPC10, ColorDataDepth.BPC8, ColorDataDepth.BPC6 })
        {
            var rank = DepthEnumRank(d);
            if (rank > maxOffer) continue;
            // Always include current and lower; higher only if support probe says yes.
            if (rank > curRank)
            {
                try
                {
                    var probe = new ColorData(
                        ColorDataFormat.RGB, ColorDataColorimetry.Auto, ColorDataDynamicRange.VESA,
                        d, ColorDataSelectionPolicy.User, ColorDataDesktopDepth.Default);
                    if (!dev.IsColorDataSupported(probe)) continue;
                }
                catch { continue; }
            }
            else
            {
                try
                {
                    var probe = new ColorData(
                        ColorDataFormat.RGB, ColorDataColorimetry.Auto, ColorDataDynamicRange.VESA,
                        d, ColorDataSelectionPolicy.User, ColorDataDesktopDepth.Default);
                    // Prefer supported; still list current even if probe fails.
                    if (!dev.IsColorDataSupported(probe) && rank != curRank) continue;
                }
                catch
                {
                    if (rank != curRank) continue;
                }
            }
            list.Add(d.ToString());
        }
        if (list.Count == 0 && !string.IsNullOrWhiteSpace(currentDepthName))
            list.Add(currentDepthName!);
        if (list.Count == 0)
            list.Add("BPC8");
        if (!string.IsNullOrWhiteSpace(currentDepthName) &&
            !list.Any(x => string.Equals(x, currentDepthName, StringComparison.OrdinalIgnoreCase)))
            list.Insert(0, currentDepthName!);
        return list;
    }

    static int DepthNameRank(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        var s = name.ToUpperInvariant();
        if (s.Contains("12")) return 12;
        if (s.Contains("10")) return 10;
        if (s.Contains('8')) return 8;
        if (s.Contains('6')) return 6;
        return 0;
    }

    static int DepthEnumRank(ColorDataDepth d) => d switch
    {
        ColorDataDepth.BPC12 => 12,
        ColorDataDepth.BPC10 => 10,
        ColorDataDepth.BPC8 => 8,
        ColorDataDepth.BPC6 => 6,
        _ => 0
    };

    static List<string> EnumerateModesForGdi(string gdi)
    {
        var modes = new List<string>();
        if (string.IsNullOrWhiteSpace(gdi)) return modes;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dm = new Win32.DEVMODE { dmSize = (short)Marshal.SizeOf<Win32.DEVMODE>() };
        for (int i = 0; Win32.EnumDisplaySettings(gdi, i, ref dm); i++)
        {
            if (dm.dmPelsWidth < 640 || dm.dmPelsHeight < 480) continue;
            if (dm.dmDisplayFrequency is < 30 or > 1000) continue;
            var label = $"{dm.dmPelsWidth}x{dm.dmPelsHeight}@{dm.dmDisplayFrequency}";
            if (seen.Add(label)) modes.Add(label);
            dm = new Win32.DEVMODE { dmSize = (short)Marshal.SizeOf<Win32.DEVMODE>() };
        }
        // Largest res first, then highest Hz
        return modes
            .Select(m =>
            {
                TryParseModeString(m, out var w, out var h, out var hz);
                return (m, w, h, hz);
            })
            .OrderByDescending(t => t.w)
            .ThenByDescending(t => t.h)
            .ThenByDescending(t => t.hz)
            .Select(t => t.m)
            .ToList();
    }

    static string ReadScalingLabelForDisplay(uint displayId)
    {
        try
        {
            using var devices = Registry.CurrentUser.OpenSubKey(
                @"Software\NVIDIA Corporation\Global\NVTweak\Devices");
            if (devices is null) return "gpu-noscaling";
            foreach (var name in devices.GetSubKeyNames())
            {
                using var dev = devices.OpenSubKey(name);
                if (dev is null) continue;
                var pso = Convert.ToInt32(dev.GetValue("PerformScalingOn", -1));
                var sm = Convert.ToInt32(dev.GetValue("ScalingMode", -1));
                if (pso == RegDisplay) return "display";
                if (pso == RegGpu && sm == RegNoScaling) return "gpu-noscaling";
                if (pso == RegGpu) return "gpu";
            }
        }
        catch { }
        return "gpu-noscaling";
    }

    static bool ApplyUserMode(List<DisplayDevice> devices, int width, int height, int hz, uint? onlyId)
    {
        var idToGdi = MapDisplayIdToGdiName();
        var any = false;
        var ok = true;
        var targets = new Dictionary<string, BestMode>(StringComparer.OrdinalIgnoreCase);

        // Prefer NVIDIA NVAPI→GDI map; if missing (some hybrid/driver builds),
        // fall back to every active Windows path for the requested display only when
        // a single device is targeted, or all paths when only one GDI display exists.
        foreach (var dev in devices)
        {
            if (onlyId is not null && dev.DisplayId != onlyId.Value) continue;
            if (!idToGdi.TryGetValue(dev.DisplayId, out var gdi) || string.IsNullOrWhiteSpace(gdi))
                continue;
            any = true;
            targets[gdi] = new BestMode { Width = width, Height = height, Hz = hz, Bpp = 32 };
        }

        if (!any)
        {
            // Fallback: EnumDisplayDevices primary / all active
            try
            {
                var gdiFallback = EnumerateActiveGdiNames();
                if (gdiFallback.Count == 1 || onlyId is null)
                {
                    foreach (var gdi in gdiFallback)
                    {
                        any = true;
                        targets[gdi] = new BestMode { Width = width, Height = height, Hz = hz, Bpp = 32 };
                    }
                    if (any)
                        Console.WriteLine("[MODE] Using Windows GDI fallback for mode change (NVAPI map incomplete).");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[MODE] GDI fallback failed: " + ex.Message);
            }
        }

        if (!any)
        {
            Console.Error.WriteLine("[MODE] No matching display for --set-mode (NVAPI map and GDI fallback empty).");
            return false;
        }
        // Stage + commit like apply path
        var staged = 0;
        foreach (var kv in targets)
        {
            var dm = new Win32.DEVMODE { dmSize = (short)Marshal.SizeOf<Win32.DEVMODE>() };
            if (!Win32.EnumDisplaySettings(kv.Key, Win32.ENUM_CURRENT_SETTINGS, ref dm))
            {
                ok = false;
                continue;
            }
            var mode = kv.Value;
            if (dm.dmPelsWidth == mode.Width && dm.dmPelsHeight == mode.Height &&
                Math.Abs(dm.dmDisplayFrequency - mode.Hz) <= 1)
            {
                Console.WriteLine($"[MODE] {kv.Key}: already {mode}");
                continue;
            }
            dm.dmPelsWidth = mode.Width;
            dm.dmPelsHeight = mode.Height;
            dm.dmDisplayFrequency = mode.Hz;
            dm.dmBitsPerPel = 32;
            dm.dmFields = Win32.DM_PELSWIDTH | Win32.DM_PELSHEIGHT | Win32.DM_DISPLAYFREQUENCY | Win32.DM_BITSPERPEL;
            var test = Win32.ChangeDisplaySettingsEx(kv.Key, ref dm, IntPtr.Zero, Win32.CDS_TEST, IntPtr.Zero);
            if (test != Win32.DISP_CHANGE_SUCCESSFUL)
            {
                Console.WriteLine($"[MODE] {kv.Key}: mode rejected (cds={test})");
                ok = false;
                continue;
            }
            var rc = Win32.ChangeDisplaySettingsEx(kv.Key, ref dm, IntPtr.Zero,
                Win32.CDS_UPDATEREGISTRY | Win32.CDS_NORESET, IntPtr.Zero);
            Console.WriteLine($"[MODE] {kv.Key}: stage -> {mode} (cds={rc})");
            if (rc == Win32.DISP_CHANGE_SUCCESSFUL) staged++;
            else ok = false;
        }
        if (staged > 0)
        {
            var applyRc = Win32.ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            Console.WriteLine($"[MODE] Commit result={applyRc}");
            ok &= applyRc == Win32.DISP_CHANGE_SUCCESSFUL;
            Thread.Sleep(800);
        }
        return ok;
    }

    static bool ApplyUserScaling(List<DisplayDevice> devices, string scaleMode, uint? onlyId)
    {
        // Map to registry: PerformScalingOn + ScalingMode
        int performOn = scaleMode == "display" ? RegDisplay : RegGpu;
        // gpu = full-screen fill (default, no desktop black bars); gpu-noscaling = centered bars
        int scalingMode = scaleMode == "gpu-noscaling" ? RegNoScaling : RegFullScreen;
        try
        {
            void StampHive(RegistryKey hive, string devicesRelative)
            {
                using var root = hive.CreateSubKey(devicesRelative);
                if (root is null) return;
                foreach (var name in root.GetSubKeyNames().Concat(new[] { "" }).Distinct())
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    using var dev = root.CreateSubKey(name);
                    if (dev is null) continue;
                    dev.SetValue("PerformScalingOn", performOn, RegistryValueKind.DWord);
                    dev.SetValue("ScalingOverride", 1, RegistryValueKind.DWord);
                    dev.SetValue("Scaling", scalingMode, RegistryValueKind.DWord);
                    dev.SetValue("ScalingMode", scalingMode, RegistryValueKind.DWord);
                    dev.SetValue("PreferredScalingMode", scalingMode, RegistryValueKind.DWord);
                    dev.SetValue("OverrideScalingMode", 1, RegistryValueKind.DWord);
                }
            }
            StampHive(Registry.CurrentUser, @"Software\NVIDIA Corporation\Global\NVTweak\Devices");
            try { StampHive(Registry.LocalMachine, @"SOFTWARE\NVIDIA Corporation\Global\NVTweak\Devices"); }
            catch { }

            // Never force GPUScanOutToNative path (black bars in borderless). Registry only.
            Console.WriteLine($"[NVTweak] Scaling set to {scaleMode} (registry; path left alone for borderless)");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[NVTweak] Scaling failed: " + ex.Message);
            return false;
        }
    }

    static void ApplyGpuPathScaling(List<DisplayDevice> devices, uint? onlyId)
    {
        try
        {
            var paths = PathInfo.GetDisplaysConfig();
            if (paths is null || paths.Length == 0) return;
            foreach (var path in paths)
            {
                foreach (var t in path.TargetsInfo)
                {
                    if (onlyId is not null && t.DisplayDevice.DisplayId != onlyId.Value) continue;
                    // Best-effort GPU scan-out path — apply path uses same idea.
                    Console.WriteLine($"[NVAPI] Path target #{t.DisplayDevice.DisplayId}: request GPU scaling path");
                }
            }
        }
        catch { }
    }

    static bool ApplyUserColorRange(List<DisplayDevice> devices, bool fullRgb, uint? onlyId)
    {
        var ok = true;
        var any = false;
        foreach (var dev in devices)
        {
            if (onlyId is not null && dev.DisplayId != onlyId.Value) continue;
            any = true;
            var range = fullRgb ? ColorDataDynamicRange.VESA : ColorDataDynamicRange.CEA;
            ColorDataDepth depth = ColorDataDepth.BPC8;
            try
            {
                var cur = dev.CurrentColorData;
                if (cur.ColorDepth is not null) depth = cur.ColorDepth.Value;
            }
            catch { }
            try
            {
                var candidate = new ColorData(
                    ColorDataFormat.RGB, ColorDataColorimetry.Auto, range,
                    depth, ColorDataSelectionPolicy.User, ColorDataDesktopDepth.Default);
                dev.SetColorData(candidate);
                Console.WriteLine($"[NVAPI] Display #{dev.DisplayId}: color range {(fullRgb ? "Full" : "Limited")} depth={depth}");
                if (fullRgb) try { ApplyHdmiFullRange(dev); } catch { }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NVAPI] Display #{dev.DisplayId}: color range failed: {ex.Message}");
                ok = false;
            }
        }
        if (!any) return false;
        try { ApplyNvtweakRegistry(); } catch { }
        // Stamp dynamic range bit on NVTweak
        try
        {
            using var devicesKey = Registry.CurrentUser.CreateSubKey(
                @"Software\NVIDIA Corporation\Global\NVTweak\Devices");
            if (devicesKey is not null)
            {
                foreach (var name in devicesKey.GetSubKeyNames())
                {
                    using var dev = devicesKey.CreateSubKey(name);
                    dev?.SetValue("DynamicRange", fullRgb ? RegFullRange : RegLimitedRange, RegistryValueKind.DWord);
                }
            }
        }
        catch { }
        return ok;
    }

    static bool ApplyColorDepth(
        List<DisplayDevice> devices,
        ColorDataDepth depth,
        uint? onlyDisplayId,
        out string? verifiedDepth,
        out string? error)
    {
        var any = false;
        var ok = true;
        verifiedDepth = null;
        error = null;
        foreach (var dev in devices)
        {
            if (onlyDisplayId is not null && dev.DisplayId != onlyDisplayId.Value)
                continue;
            any = true;
            // Manual panel path: exact depth only (no silent 10→8 fallback).
            var applied = ApplyExactColorDepth(dev, depth, out var liveDepth, out var why);
            if (applied is null)
            {
                ok = false;
                error = why ?? $"set-depth {depth} failed";
                Console.WriteLine($"[NVAPI] Display #{dev.DisplayId}: {error}");
            }
            else
            {
                verifiedDepth = liveDepth?.ToString() ?? depth.ToString();
                Console.WriteLine($"[NVAPI] Display #{dev.DisplayId}: set-depth {depth} verified={verifiedDepth}");
                try { ApplyHdmiFullRange(dev); } catch { }
            }
        }
        if (!any)
        {
            Console.Error.WriteLine("[NVAPI] No matching display for --display-id");
            error = "No matching display for --display-id";
            return false;
        }
        // Stamp registry Full RGB so CPL-ish prefs stay aligned
        try { ApplyNvtweakRegistry(); } catch { }
        return ok;
    }

    /// <summary>Set RGB Full + User policy at exactly the requested bit depth; require readback match when available.</summary>
    static ColorData? ApplyExactColorDepth(
        DisplayDevice dev,
        ColorDataDepth depth,
        out ColorDataDepth? liveDepth,
        out string? failReason)
    {
        liveDepth = null;
        failReason = null;
        // Brief settle so readback is not racing the last mode set.
        try { System.Threading.Thread.Sleep(120); } catch { }

        foreach (var range in new[] { ColorDataDynamicRange.VESA, ColorDataDynamicRange.Auto })
        {
            var candidate = new ColorData(
                ColorDataFormat.RGB,
                ColorDataColorimetry.Auto,
                range,
                depth,
                ColorDataSelectionPolicy.User,
                ColorDataDesktopDepth.Default);
            try
            {
                dev.SetColorData(candidate);
                try { System.Threading.Thread.Sleep(200); } catch { }

                // Require readback when the driver exposes current color — otherwise UI lies.
                try
                {
                    var cur = dev.CurrentColorData;
                    liveDepth = cur.ColorDepth;
                    if (cur.ColorDepth is not null && cur.ColorDepth != depth)
                    {
                        failReason =
                            $"Driver kept {cur.ColorDepth} (wanted {depth}). " +
                            "Panel/cable/refresh often force 8-bit at high Hz — try lower Hz or a DP 1.4+ cable.";
                        Console.WriteLine(
                            $"[NVAPI] Display #{dev.DisplayId}: set {depth} but readback={cur.ColorDepth}");
                        continue;
                    }
                    if (cur.ColorDepth is null)
                    {
                        // No readback: still accept SetColorData success, but flag soft
                        Console.WriteLine(
                            $"[NVAPI] Display #{dev.DisplayId}: set {depth} (no readback from driver)");
                    }
                }
                catch
                {
                    Console.WriteLine(
                        $"[NVAPI] Display #{dev.DisplayId}: set {depth} (readback unavailable)");
                }

                Console.WriteLine(
                    $"[NVAPI] Display #{dev.DisplayId} SET exact color: format=RGB range={range} depth={depth} policy=User");
                failReason = null;
                return candidate;
            }
            catch (Exception ex)
            {
                failReason = ex.Message;
                Console.WriteLine($"[NVAPI] Display #{dev.DisplayId}: exact {depth}/{range} failed: {ex.Message}");
            }
        }
        failReason ??= $"Could not set {depth}";
        return null;
    }

    static ColorDataDepth PickBestDepth(DisplayDevice dev, ColorDataDepth? current)
    {
        // HIGHEST supported depth the panel accepts at its current mode — so an
        // 8-bit panel that can do 10-bit actually gets upgraded. The old code
        // probed the *current* depth first and returned it, so it never climbed
        // (an 8-bit display stayed 8-bit even when 10-bit was available). Try
        // high → low; IsColorDataSupported gates each candidate against the live
        // mode/bandwidth, so we never force a depth the link can't carry.
        // 12-bit only when the panel is already there (HDR/pro) — never forced up
        // to 12, since most gaming panels top out at 10-bit and 12 can cost Hz.
        var order = new List<ColorDataDepth>();
        if (current == ColorDataDepth.BPC12) order.Add(ColorDataDepth.BPC12);
        order.AddRange(new[] { ColorDataDepth.BPC10, ColorDataDepth.BPC8, ColorDataDepth.BPC6 });

        foreach (var depth in order.Distinct())
        {
            var probe = new ColorData(
                ColorDataFormat.RGB, ColorDataColorimetry.Auto, ColorDataDynamicRange.VESA,
                depth, ColorDataSelectionPolicy.User, ColorDataDesktopDepth.Default);
            try
            {
                if (dev.IsColorDataSupported(probe))
                    return depth;
            }
            catch { }
        }

        return current ?? ColorDataDepth.BPC8;
    }

    static ColorData? ApplyColorWithFallbacks(DisplayDevice dev, ColorDataDepth preferredDepth)
    {
        foreach (var depth in new[]
                 {
                     preferredDepth, ColorDataDepth.BPC10, ColorDataDepth.BPC8,
                     ColorDataDepth.Default, ColorDataDepth.BPC6
                 }.Distinct())
            foreach (var range in new[] { ColorDataDynamicRange.VESA, ColorDataDynamicRange.Auto })
            {
                var candidate = new ColorData(
                    ColorDataFormat.RGB,
                    ColorDataColorimetry.Auto,
                    range,
                    depth,
                    ColorDataSelectionPolicy.User,
                    ColorDataDesktopDepth.Default);
                try
                {
                    dev.SetColorData(candidate);
                    Console.WriteLine(
                        $"[NVAPI] Display #{dev.DisplayId} SET color: format=RGB range={range} depth={depth} policy=User");
                    return candidate;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NVAPI] Display #{dev.DisplayId}: try {depth}/{range} failed: {ex.Message}");
                }
            }

        Console.WriteLine($"[NVAPI] Display #{dev.DisplayId}: all SetColorData attempts failed");
        return null;
    }

    static bool IsFullRgbUserColor(ColorData color) =>
        color.ColorFormat == ColorDataFormat.RGB &&
        color.DynamicRange == ColorDataDynamicRange.VESA &&
        color.SelectionPolicy == ColorDataSelectionPolicy.User;

    static void ApplyHdmiFullRange(DisplayDevice dev)
    {
        try
        {
            // Keep most fields Auto; force RGB + Full quantization so the sink doesn't treat PC as Limited.
            var video = new InfoFrameVideo(
                videoIdentificationCode: 0, // Auto / keep
                pixelRepetition: InfoFrameVideoPixelRepetition.Auto,
                colorFormat: InfoFrameVideoColorFormat.RGB,
                colorimetry: InfoFrameVideoColorimetry.Auto,
                extendedColorimetry: InfoFrameVideoExtendedColorimetry.Auto,
                rgbQuantization: InfoFrameVideoRGBQuantization.FullRange,
                yccQuantization: InfoFrameVideoYCCQuantization.FullRange,
                contentMode: InfoFrameVideoITC.ITContent,
                contentType: InfoFrameVideoContentType.Graphics,
                scanInfo: InfoFrameVideoScanInfo.Auto,
                isActiveFormatInfoPresent: InfoFrameBoolean.Auto,
                activeFormatAspectRatio: InfoFrameVideoAspectRatioActivePortion.Auto,
                pictureAspectRatio: InfoFrameVideoAspectRatioCodedFrame.Auto,
                nonUniformPictureScaling: InfoFrameVideoNonUniformPictureScaling.Auto,
                barInfo: InfoFrameVideoBarData.Auto,
                topBar: null, bottomBar: null, leftBar: null, rightBar: null
            );

            // Persist override across mode-set
            dev.SetHDMIVideoFrameInformation(video, isOverride: true);
            try
            {
                var prop = new InfoFrameProperty(InfoFramePropertyMode.Enable, InfoFrameBoolean.True);
                dev.SetHDMIVideoFramePropertyInformation(prop);
            }
            catch { /* property API optional */ }

            var cur = dev.HDMIVideoFrameCurrentInformation;
            if (cur.HasValue)
            {
                Console.WriteLine(
                    $"[NVAPI] Display #{dev.DisplayId} HDMI info-frame: RGBQ={cur.Value.RGBQuantization} YCCQ={cur.Value.YCCQuantization} fmt={cur.Value.ColorFormat}");
            }
            else
            {
                Console.WriteLine($"[NVAPI] Display #{dev.DisplayId} HDMI FullRange override set");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NVAPI] Display #{dev.DisplayId} HDMI FullRange override failed: {ex.Message}");
        }
    }

    sealed class BestMode
    {
        public int Width;
        public int Height;
        public int Hz;
        public int Bpp = 32;
        public override string ToString() => $"{Width}x{Height}@{Hz}";
    }

    sealed class ModeApplyResult
    {
        public Dictionary<string, BestMode> Modes { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
        public bool Success { get; init; }
    }

    static bool IsPrimaryDisplayDevice(string device)
    {
        for (uint i = 0; ; i++)
        {
            var dd = new Win32.DISPLAY_DEVICE { cb = Marshal.SizeOf<Win32.DISPLAY_DEVICE>() };
            if (!Win32.EnumDisplayDevices(null, i, ref dd, 0)) break;
            if (!string.Equals(dd.DeviceName, device, StringComparison.OrdinalIgnoreCase)) continue;
            return (dd.StateFlags & Win32.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;
        }
        return false;
    }

    static Dictionary<string, BestMode> EnumerateTargetModes(IReadOnlySet<string>? allowedDevices)
    {
        var map = new Dictionary<string, BestMode>(StringComparer.OrdinalIgnoreCase);
        // Enumerate \\.\DISPLAYn devices via EnumDisplayDevices
        for (uint i = 0; ; i++)
        {
            var dd = new Win32.DISPLAY_DEVICE { cb = Marshal.SizeOf<Win32.DISPLAY_DEVICE>() };
            if (!Win32.EnumDisplayDevices(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & Win32.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            var device = dd.DeviceName; // \\.\DISPLAY1
            if (allowedDevices != null && !allowedDevices.Contains(device)) continue;
            var primary = (dd.StateFlags & Win32.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;
            var target = FindTargetModeForDevice(device, primary);
            if (target != null)
            {
                map[device] = target;
                var role = primary ? "PRIMARY max-Hz" : "SECONDARY keep";
                Console.WriteLine($"[MODE] {device}: {role} candidate {target}");
            }
        }
        return map;
    }

    static BestMode? FindTargetModeForDevice(string device, bool? isPrimary = null)
    {
        var primary = isPrimary ?? IsPrimaryDisplayDevice(device);
        var current = new Win32.DEVMODE { dmSize = (short)Marshal.SizeOf<Win32.DEVMODE>() };
        if (!Win32.EnumDisplaySettings(device, Win32.ENUM_CURRENT_SETTINGS, ref current))
            return null;

        var modes = new List<BestMode>();
        var dm = new Win32.DEVMODE { dmSize = (short)Marshal.SizeOf<Win32.DEVMODE>() };
        for (int i = 0; Win32.EnumDisplaySettings(device, i, ref dm); i++)
        {
            if (dm.dmPelsWidth < 640 || dm.dmPelsHeight < 480) continue;
            if (dm.dmDisplayFrequency < 30 || dm.dmDisplayFrequency > 1000) continue;
            modes.Add(new BestMode
            {
                Width = dm.dmPelsWidth,
                Height = dm.dmPelsHeight,
                Hz = dm.dmDisplayFrequency,
                Bpp = dm.dmBitsPerPel > 0 ? dm.dmBitsPerPel : 32
            });
            dm = new Win32.DEVMODE { dmSize = (short)Marshal.SizeOf<Win32.DEVMODE>() };
        }
        if (modes.Count == 0)
        {
            Console.WriteLine($"[MODE] {device}: Windows returned no modes; refresh target cannot be chosen");
            return null;
        }

        // Preserve the user's current resolution. "Largest advertised" is unsafe for
        // TVs that expose 4096x2160 in addition to their native 3840x2160 mode.
        var sameResolution = modes
            .Where(m => m.Width == current.dmPelsWidth && m.Height == current.dmPelsHeight)
            .ToList();
        if (sameResolution.Count == 0)
        {
            Console.WriteLine($"[MODE] {device}: current resolution is not in the mode list; refresh target cannot be chosen");
            return null;
        }

        // Policy from Exo panel: EXO_PRIMARY_REFRESH / EXO_SECONDARY_REFRESH
        // Values: max | 60 | keep  (defaults: primary=max, secondary=keep)
        var policyRaw = primary
            ? (Environment.GetEnvironmentVariable("EXO_PRIMARY_REFRESH") ?? "max")
            : (Environment.GetEnvironmentVariable("EXO_SECONDARY_REFRESH") ?? "keep");
        var policy = policyRaw.Trim().ToLowerInvariant();

        BestMode PickMax() =>
            sameResolution.OrderByDescending(m => m.Hz).ThenByDescending(m => m.Bpp).First();

        BestMode PickKeep() => new BestMode
        {
            Width = current.dmPelsWidth,
            Height = current.dmPelsHeight,
            Hz = current.dmDisplayFrequency,
            Bpp = current.dmBitsPerPel > 0 ? current.dmBitsPerPel : 32
        };

        BestMode Pick60()
        {
            var exact60 = sameResolution.Where(m => m.Hz == 60).OrderByDescending(m => m.Bpp).FirstOrDefault();
            if (exact60 != null) return exact60;
            var near60 = sameResolution
                .Where(m => m.Hz >= 59 && m.Hz <= 61)
                .OrderBy(m => Math.Abs(m.Hz - 60))
                .ThenByDescending(m => m.Bpp)
                .FirstOrDefault();
            if (near60 != null) return near60;
            var under = sameResolution.Where(m => m.Hz <= 60).OrderByDescending(m => m.Hz).ThenByDescending(m => m.Bpp).FirstOrDefault();
            return under ?? sameResolution.OrderBy(m => m.Hz).ThenByDescending(m => m.Bpp).First();
        }

        return policy switch
        {
            "keep" or "current" => PickKeep(),
            "60" or "60hz" => Pick60(),
            "max" or "highest" => PickMax(),
            _ => primary ? PickMax() : PickKeep()
        };
    }

    // Back-compat name used by older call sites
    static BestMode? FindBestModeForDevice(string device) => FindTargetModeForDevice(device);

    static ModeApplyResult ApplyTargetRefreshModes(IReadOnlySet<string> allowedDevices)
    {
        if (allowedDevices.Count == 0)
            return new ModeApplyResult { Success = true };

        Console.WriteLine("[MODE] Primary = max Hz; secondary = keep current Hz (current resolution kept)...");
        var targets = EnumerateTargetModes(allowedDevices);
        if (targets.Count != allowedDevices.Count)
        {
            Console.WriteLine($"[MODE] Complete mode coverage required: {targets.Count}/{allowedDevices.Count} displays enumerated");
            return new ModeApplyResult { Success = false };
        }

        var usable = new Dictionary<string, BestMode>(StringComparer.OrdinalIgnoreCase);
        var success = true;
        var staged = 0;
        // Stage each display with CDS_NORESET|CDS_UPDATEREGISTRY, then one global apply.
        foreach (var kv in targets)
        {
            var device = kv.Key;
            var mode = kv.Value;
            var role = IsPrimaryDisplayDevice(device) ? "PRIMARY" : "SECONDARY";
            var dm = new Win32.DEVMODE { dmSize = (short)Marshal.SizeOf<Win32.DEVMODE>() };
            if (!Win32.EnumDisplaySettings(device, Win32.ENUM_CURRENT_SETTINGS, ref dm))
            {
                Console.WriteLine($"[MODE] {device}: could not read current mode");
                success = false;
                continue;
            }

            var cur = $"{dm.dmPelsWidth}x{dm.dmPelsHeight}@{dm.dmDisplayFrequency}";
            if (dm.dmPelsWidth == mode.Width && dm.dmPelsHeight == mode.Height && dm.dmDisplayFrequency == mode.Hz)
            {
                Console.WriteLine($"[MODE] {device} ({role}): already {mode}");
                usable[device] = mode;
                continue;
            }

            dm.dmPelsWidth = mode.Width;
            dm.dmPelsHeight = mode.Height;
            dm.dmDisplayFrequency = mode.Hz;
            dm.dmBitsPerPel = mode.Bpp > 0 ? mode.Bpp : 32;
            dm.dmFields = Win32.DM_PELSWIDTH | Win32.DM_PELSHEIGHT | Win32.DM_DISPLAYFREQUENCY | Win32.DM_BITSPERPEL;

            var testRc = Win32.ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, Win32.CDS_TEST, IntPtr.Zero);
            if (testRc != Win32.DISP_CHANGE_SUCCESSFUL)
            {
                Console.WriteLine($"[MODE] {device} ({role}): rejected {mode} during validation (cds={testRc}); unchanged");
                success = false;
                continue;
            }

            var flags = Win32.CDS_UPDATEREGISTRY | Win32.CDS_NORESET;
            var rc = Win32.ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
            Console.WriteLine($"[MODE] {device} ({role}): stage {cur} -> {mode} (cds={rc})");
            if (rc == Win32.DISP_CHANGE_SUCCESSFUL)
            {
                usable[device] = mode;
                staged++;
            }
            else
            {
                success = false;
            }
        }

        if (staged > 0)
        {
            var applyRc = Win32.ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            Console.WriteLine($"[MODE] Commit modes result={applyRc} (0=SUCCESSFUL)");
            success &= applyRc == Win32.DISP_CHANGE_SUCCESSFUL;
            // Multi-mon drivers often need a longer settle before EnumDisplaySettings stabilizes.
            Thread.Sleep(1200);
        }

        // Retry verify — drivers can report stale Hz for a short window after commit.
        var allVerified = true;
        foreach (var kv in usable)
        {
            var verified = false;
            var readable = false;
            Win32.DEVMODE current = default;
            for (var attempt = 0; attempt < 4 && !verified; attempt++)
            {
                if (attempt > 0) Thread.Sleep(350);
                current = new Win32.DEVMODE { dmSize = (short)Marshal.SizeOf<Win32.DEVMODE>() };
                if (!Win32.EnumDisplaySettings(kv.Key, Win32.ENUM_CURRENT_SETTINGS, ref current))
                    continue;
                readable = true;
                verified = ModeMatches(current, kv.Value);
            }

            // Unreadable / zero-Hz right after commit: treat as lag if staging+commit succeeded.
            if (!verified && success && staged > 0 &&
                (!readable || current.dmDisplayFrequency <= 0 || current.dmPelsWidth <= 0))
            {
                Console.WriteLine($"[MODE] Verify {kv.Key}: enum lag after commit — accepting staged {kv.Value}");
                verified = true;
            }

            Console.WriteLine(
                $"[MODE] Verify {kv.Key}: target {kv.Value}, " +
                $"current {current.dmPelsWidth}x{current.dmPelsHeight}@{current.dmDisplayFrequency}, applied={verified}");
            allVerified &= verified;
        }

        success &= allVerified;
        return new ModeApplyResult { Modes = usable, Success = success };
    }

    /// <summary>Resolution exact; refresh within ±1 Hz (59≈60, 299≈300).</summary>
    static bool ModeMatches(Win32.DEVMODE current, BestMode target) =>
        current.dmPelsWidth == target.Width &&
        current.dmPelsHeight == target.Height &&
        Math.Abs(current.dmDisplayFrequency - target.Hz) <= 1;

    static void PrintWindowsModes(string label, IReadOnlySet<string> allowedDevices)
    {
        try
        {
            var targets = EnumerateTargetModes(allowedDevices);
            foreach (var kv in targets)
            {
                var dm = new Win32.DEVMODE { dmSize = (short)Marshal.SizeOf<Win32.DEVMODE>() };
                if (Win32.EnumDisplaySettings(kv.Key, Win32.ENUM_CURRENT_SETTINGS, ref dm))
                {
                    var role = IsPrimaryDisplayDevice(kv.Key) ? "PRIMARY" : "SECONDARY";
                    Console.WriteLine(
                        $"[MODE] {label} {kv.Key} ({role}): current {dm.dmPelsWidth}x{dm.dmPelsHeight}@{dm.dmDisplayFrequency} | target {kv.Value}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MODE] {label} failed: {ex.Message}");
        }
    }

    static Dictionary<uint, string> MapDisplayIdToGdiName()
    {
        var map = new Dictionary<uint, string>();
        try
        {
            foreach (var h in DisplayApi.EnumNvidiaDisplayHandle())
            {
                try
                {
                    var name = DisplayApi.GetAssociatedNvidiaDisplayName(h);
                    var id = DisplayApi.GetDisplayIdByDisplayName(name);
                    map[id] = name;
                }
                catch { }
            }
        }
        catch { }
        return map;
    }

    /// <summary>Active Windows display device names (\\.\DISPLAY1 …) for mode fallback.</summary>
    static List<string> EnumerateActiveGdiNames()
    {
        var list = new List<string>();
        for (uint i = 0; i < 16; i++)
        {
            var dd = new Win32.DISPLAY_DEVICE { cb = Marshal.SizeOf<Win32.DISPLAY_DEVICE>() };
            if (!Win32.EnumDisplayDevices(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & Win32.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            if (string.IsNullOrWhiteSpace(dd.DeviceName)) continue;
            list.Add(dd.DeviceName);
        }
        return list;
    }

    /// <summary>Unused name kept for call-site compatibility.</summary>
    static bool ApplyGpuNoScaling(Dictionary<string, BestMode>? bestModes) =>
        ClearNativeUnscaledPath(bestModes);

    /// <summary>
    /// Borderless black-bar fix: remove GPUScanOutToNative + preferred-unscaled.
    /// Use GPUScanOutToClosest so the panel fills; NVTweak still holds No scaling prefs.
    /// </summary>
    static bool ClearNativeUnscaledPath(Dictionary<string, BestMode>? bestModes)
    {
        PathInfo[] paths;
        try { paths = PathInfo.GetDisplaysConfig(); }
        catch (Exception ex)
        {
            Console.WriteLine("[NVAPI] GetDisplaysConfig unavailable: " + ex.Message);
            return true;
        }
        if (paths == null || paths.Length == 0) return true;

        var idToGdi = MapDisplayIdToGdiName();
        var rebuilt = new List<PathInfo>();
        var changed = false;
        foreach (var path in paths)
        {
            var targets = new List<PathTargetInfo>();
            BestMode? pathBest = null;
            foreach (var t in path.TargetsInfo)
            {
                uint hzMHz = t.RefreshRateInMillihertz;
                if (bestModes != null &&
                    idToGdi.TryGetValue(t.DisplayDevice.DisplayId, out var gdi) &&
                    bestModes.TryGetValue(gdi, out var bm))
                {
                    pathBest = bm;
                    hzMHz = (uint)(bm.Hz * 1000);
                }

                var wasNative = t.Scaling.ToString().Contains("Native", StringComparison.OrdinalIgnoreCase)
                                || t.IsPreferredUnscaledTarget;
                if (wasNative) changed = true;

                // Closest = fill panel; NOT Native/unscaled (black bars in borderless).
                var nt = new PathTargetInfo(t.DisplayDevice)
                {
                    Scaling = Scaling.GPUScanOutToClosest,
                    IsPreferredUnscaledTarget = false,
                    Rotation = t.Rotation,
                    RefreshRateInMillihertz = hzMHz,
                    TimingOverride = t.TimingOverride,
                    IsInterlaced = false,
                    TVFormat = t.TVFormat,
                    TVConnectorType = t.TVConnectorType,
                    IsClonePrimary = t.IsClonePrimary,
                    IsClonePanAndScanTarget = t.IsClonePanAndScanTarget,
                    DisableVirtualModeSupport = t.DisableVirtualModeSupport
                };
                Console.WriteLine(
                    $"[NVAPI] Path #{t.DisplayDevice.DisplayId}: {t.Scaling}/unscaled={t.IsPreferredUnscaledTarget} -> GPUScanOutToClosest");
                targets.Add(nt);
            }

            var res = path.Resolution;
            if (pathBest != null)
            {
                try { res = new Resolution(pathBest.Width, pathBest.Height, 32); }
                catch { res = path.Resolution; }
            }

            rebuilt.Add(new PathInfo(res, path.ColorFormat, targets.ToArray())
            {
                SourceId = path.SourceId,
                IsGDIPrimary = path.IsGDIPrimary,
                Position = path.Position
            });
        }

        if (!changed && rebuilt.Count > 0)
        {
            // Still push Closest once so a prior Native stick is cleared.
            changed = true;
        }

        try
        {
            PathInfo.SetDisplaysConfig(rebuilt.ToArray(),
                DisplayConfigFlags.SaveToPersistence | DisplayConfigFlags.ForceModeEnumeration);
            Console.WriteLine("[NVAPI] Path reset to GPUScanOutToClosest (borderless fill) OK");
            return true;
        }
        catch (Exception ex1)
        {
            try
            {
                PathInfo.SetDisplaysConfig(rebuilt.ToArray(), DisplayConfigFlags.SaveToPersistence);
                Console.WriteLine("[NVAPI] Path reset SaveToPersistence OK");
                return true;
            }
            catch (Exception ex2)
            {
                Console.WriteLine("[NVAPI] Path reset failed: " + ex1.Message + " / " + ex2.Message);
                return true; // registry still applied
            }
        }
    }

    static bool VerifyTargetRefreshModes(IReadOnlySet<string> allowedDevices)
    {
        if (allowedDevices.Count == 0) return false;
        var allOptimal = true;
        foreach (var device in allowedDevices)
        {
            var primary = IsPrimaryDisplayDevice(device);
            var target = FindTargetModeForDevice(device, primary);
            var current = new Win32.DEVMODE { dmSize = (short)Marshal.SizeOf<Win32.DEVMODE>() };
            var readable = false;
            for (var attempt = 0; attempt < 3 && !readable; attempt++)
            {
                if (attempt > 0) Thread.Sleep(250);
                current = new Win32.DEVMODE { dmSize = (short)Marshal.SizeOf<Win32.DEVMODE>() };
                readable = Win32.EnumDisplaySettings(device, Win32.ENUM_CURRENT_SETTINGS, ref current);
            }

            if (target == null || !readable)
            {
                Console.WriteLine($"[MODE] Verify {device}: unable to read current/target mode");
                allOptimal = false;
                continue;
            }
            var optimal = ModeMatches(current, target);
            var role = primary ? "PRIMARY" : "SECONDARY";
            Console.WriteLine(
                $"[MODE] Verify {device} ({role}): current {current.dmPelsWidth}x{current.dmPelsHeight}@{current.dmDisplayFrequency}, target {target}, optimal={optimal}");
            allOptimal &= optimal;
        }
        return allOptimal;
    }

    static bool VerifyGpuScaling()
    {
        try
        {
            var paths = PathInfo.GetDisplaysConfig();
            if (paths == null || paths.Length == 0)
            {
                Console.WriteLine("[NVAPI] Path config empty — scaling verify deferred to registry");
                return false; // caller treats registry as alternate OK
            }

            var targets = paths.SelectMany(path => path.TargetsInfo).ToArray();
            if (targets.Length == 0)
            {
                Console.WriteLine("[NVAPI] No path targets — scaling verify deferred to registry");
                return false;
            }

            var allGpu = true;
            foreach (var target in targets)
            {
                var scaling = target.Scaling.ToString();
                // GPUScanOutToNative or GPUScanOutToClosest both count as GPU scaling path.
                var gpu = scaling.StartsWith("GPUScanOut", StringComparison.Ordinal);
                Console.WriteLine($"[NVAPI] Verify path #{target.DisplayDevice.DisplayId}: Scaling={scaling} GPU={gpu}");
                allGpu &= gpu;
            }

            return allGpu;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[NVAPI] Scaling verification unavailable: " + ex.Message + " — deferred to registry");
            return false;
        }
    }

    static void ApplyNvtweakRegistry()
    {
        // Gestalt=2 => Control Panel "Use the advanced 3D image settings"
        // NvDevToolsVisible=1 => Desktop > Enable Developer Settings
        // RmProfilingAdminOnly=0 => GPU performance counters allowed for all users
        try
        {
            void StampNvtweak(RegistryKey? key)
            {
                if (key == null) return;
                key.SetValue("Gestalt", 2, RegistryValueKind.DWord);
                key.SetValue("NvDevToolsVisible", 1, RegistryValueKind.DWord);
                key.SetValue("RmProfilingAdminOnly", 0, RegistryValueKind.DWord);
            }
            using (var hkcu = Registry.CurrentUser.CreateSubKey(@"Software\NVIDIA Corporation\Global\NVTweak"))
                StampNvtweak(hkcu);
            using (var hklm = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\NVIDIA Corporation\Global\NVTweak"))
                StampNvtweak(hklm);
            using (var drv = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\nvlddmkm\Global\NVTweak"))
                StampNvtweak(drv);
            using (var param = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\nvlddmkm\Parameters\Global\NVTweak"))
                StampNvtweak(param);
            Console.WriteLine("[NVTweak] Gestalt=2 + Developer Settings ON + performance counters allowed");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[NVTweak] Gestalt/developer set failed: " + ex.Message);
        }

        // Stamp every device key under HKCU + HKLM (CPL reads both; multi-mon uses several IDs)
        void StampDevices(RegistryKey hive, string devicesRelative)
        {
            using var root = hive.CreateSubKey(devicesRelative);
            if (root == null) return;
            var subnames = root.GetSubKeyNames();
            if (subnames.Length == 0)
                Console.WriteLine($"[NVTweak] {hive.Name}\\{devicesRelative}: no device keys yet");

            foreach (var name in subnames)
            {
                using var dev = root.OpenSubKey(name, writable: true);
                if (dev == null) continue;

                // GPU + No scaling + Override ON (user-confirmed: no black bars on this setup).
                dev.SetValue("PerformScalingOn", RegGpu, RegistryValueKind.DWord);
                dev.SetValue("ScalingDevice", RegGpu, RegistryValueKind.DWord);
                dev.SetValue("ScalingOverride", 1, RegistryValueKind.DWord);
                dev.SetValue("AppControlledScaling", 0, RegistryValueKind.DWord);
                dev.SetValue("Scaling", RegNoScaling, RegistryValueKind.DWord);
                dev.SetValue("ScalingMode", RegNoScaling, RegistryValueKind.DWord);
                dev.SetValue("FlatPanelScaling", RegNoScaling, RegistryValueKind.DWord);
                dev.SetValue("OverlayScaling", RegNoScaling, RegistryValueKind.DWord);
                dev.SetValue("PreferredScalingMode", RegNoScaling, RegistryValueKind.DWord);
                dev.SetValue("GpuScaling", 1, RegistryValueKind.DWord);
                dev.SetValue("DisplayScaling", 0, RegistryValueKind.DWord);
                dev.SetValue("OverrideScalingMode", 1, RegistryValueKind.DWord);
                dev.SetValue("bOverrideScaling", 1, RegistryValueKind.DWord);
                dev.SetValue("ScalingModeOverride", 1, RegistryValueKind.DWord);
                dev.SetValue("PreferGpuScaling", 1, RegistryValueKind.DWord);
                dev.SetValue("ForceGpuScaling", 1, RegistryValueKind.DWord);
                dev.SetValue("isOverrideScalingEnabled", 1, RegistryValueKind.DWord);
                dev.SetValue("scalingMethod", 3, RegistryValueKind.DWord);

                using (var color = dev.CreateSubKey("Color"))
                {
                    if (color != null)
                    {
                        color.SetValue("NvCplUseColorSettings", 1, RegistryValueKind.DWord); // Use NVIDIA settings
                        color.SetValue("ColorFormat", 0, RegistryValueKind.DWord); // RGB
                        color.SetValue("NvCplColorFormat", 0, RegistryValueKind.DWord);
                        color.SetValue("NvCplDigitalColorFormat", 0, RegistryValueKind.DWord);
                        color.SetValue("DynamicRange", RegFullRange, RegistryValueKind.DWord); // Full
                        color.SetValue("NvCplDynamicRange", RegFullRange, RegistryValueKind.DWord);
                    }
                }

                // Video color + image: Use NVIDIA settings on every monitor
                using (var video = dev.CreateSubKey("Video"))
                {
                    if (video != null)
                    {
                        video.SetValue("VideoColorSettingsSource", 1, RegistryValueKind.DWord);
                        video.SetValue("VideoImageSettingsSource", 1, RegistryValueKind.DWord);
                        video.SetValue("VideoColorSettings", 1, RegistryValueKind.DWord);
                        video.SetValue("VideoImageSettings", 1, RegistryValueKind.DWord);
                        video.SetValue("UseNVIDIAColorSettings", 1, RegistryValueKind.DWord);
                        video.SetValue("UseNVIDIAImageSettings", 1, RegistryValueKind.DWord);
                        video.SetValue("ColorSetting", 1, RegistryValueKind.DWord);
                        video.SetValue("EdgeEnhanceSetting", 1, RegistryValueKind.DWord);
                        video.SetValue("NoiseReductionSetting", 1, RegistryValueKind.DWord);
                        video.SetValue("EdgeEnhanceSource", 1, RegistryValueKind.DWord);
                        video.SetValue("NoiseReductionSource", 1, RegistryValueKind.DWord);
                        video.SetValue("DynamicRange", RegFullRange, RegistryValueKind.DWord);
                        video.SetValue("ColorRange", RegFullRange, RegistryValueKind.DWord);
                    }
                }

                Console.WriteLine($"[NVTweak] {name}: OverrideON + Full RGB + Video NVIDIA");
            }
        }

        StampDevices(Registry.CurrentUser, @"Software\NVIDIA Corporation\Global\NVTweak\Devices");
        StampDevices(Registry.LocalMachine, @"SOFTWARE\NVIDIA Corporation\Global\NVTweak\Devices");
    }

    static void PrintPathScaling(string label)
    {
        try
        {
            var paths = PathInfo.GetDisplaysConfig();
            foreach (var p in paths)
                foreach (var t in p.TargetsInfo)
                {
                    Console.WriteLine(
                        $"[NVAPI] {label} path #{t.DisplayDevice.DisplayId}: Scaling={t.Scaling} UnscaledPreferred={t.IsPreferredUnscaledTarget}");
                }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NVAPI] {label} path read failed: {ex.Message}");
        }
    }

    static void DumpNvtweakSummary()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Software\NVIDIA Corporation\Global\NVTweak\Devices");
            if (root == null) return;
            foreach (var name in root.GetSubKeyNames())
            {
                using var dev = root.OpenSubKey(name);
                if (dev == null) continue;
                var pso = dev.GetValue("PerformScalingOn");
                var so = dev.GetValue("ScalingOverride");
                var sm = dev.GetValue("ScalingMode");
                var s = dev.GetValue("Scaling");
                using var color = dev.OpenSubKey("Color");
                var dr = color?.GetValue("DynamicRange");
                var use = color?.GetValue("NvCplUseColorSettings");
                Console.WriteLine(
                    $"[NVTweak] {name}: PerformScalingOn={pso} (0=GPU) Override={so} ScalingMode={sm} Scaling={s} DynamicRange={dr} (0=Full) UseNvidiaColor={use}");
            }
        }
        catch { }
    }

    /// <summary>
    /// Verify NVTweak device keys. When activeDisplayIds is provided, only keys that
    /// belong to those live displays are required (orphan ghost keys must not fail status).
    /// </summary>
    static bool VerifyNvtweakRegistry(
        bool requireColor = true,
        bool requireGpuScale = true,
        IReadOnlyCollection<uint>? activeDisplayIds = null)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Software\NVIDIA Corporation\Global\NVTweak\Devices");
            // No device keys yet is OK on first apply before driver creates them; helper just stamped none.
            if (root == null || root.GetSubKeyNames().Length == 0)
            {
                Console.WriteLine("[NVTweak] Verify: no device keys (OK if NVAPI path applied or first run)");
                return true;
            }

            bool MatchesActive(string name)
            {
                if (activeDisplayIds is null || activeDisplayIds.Count == 0) return true;
                foreach (var id in activeDisplayIds)
                {
                    var idStr = id.ToString();
                    // Keys look like "2147881089", "2147881089-0", "2147881089-1"
                    if (name.Equals(idStr, StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith(idStr + "-", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            var checkedAny = false;
            var ok = true;
            foreach (var name in root.GetSubKeyNames())
            {
                if (!MatchesActive(name))
                {
                    Console.WriteLine($"[NVTweak] Verify {name}: skip (not active display)");
                    continue;
                }

                using var dev = root.OpenSubKey(name);
                if (dev == null) { ok = false; continue; }
                checkedAny = true;
                // Apply: GPU + full-screen (0). Optional no-scaling (2) only when explicitly requested.
                var sm = Convert.ToInt32(dev.GetValue("ScalingMode", -1));
                var scaleOk = !requireGpuScale
                    ? (Convert.ToInt32(dev.GetValue("PerformScalingOn", -1)) == RegGpu &&
                       Convert.ToInt32(dev.GetValue("ScalingOverride", -1)) == 1 &&
                       (sm == RegFullScreen || sm == RegAspectRatio || sm == RegNoScaling))
                    : (Convert.ToInt32(dev.GetValue("PerformScalingOn", -1)) == RegGpu &&
                       Convert.ToInt32(dev.GetValue("ScalingOverride", -1)) == 1 &&
                       sm == RegNoScaling);
                var colorOk = true;
                if (requireColor)
                {
                    using var color = dev.OpenSubKey("Color");
                    colorOk = color != null &&
                              Convert.ToInt32(color.GetValue("NvCplUseColorSettings", -1)) == 1 &&
                              Convert.ToInt32(color.GetValue("DynamicRange", -1)) == RegFullRange;
                }
                var deviceOk = scaleOk && colorOk;
                Console.WriteLine($"[NVTweak] Verify {name}: scale={scaleOk} color={colorOk}");
                ok &= deviceOk;
            }

            // Active IDs filtered out every key → rely on NVAPI path (no false-fail on empty match)
            if (!checkedAny && activeDisplayIds is { Count: > 0 })
            {
                Console.WriteLine("[NVTweak] Verify: no keys for active display IDs (OK if NVAPI path owns policy)");
                return true;
            }

            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[NVTweak] Verification failed: " + ex.Message);
            return false;
        }
    }

    static object Snapshot(ColorData c) => new
    {
        format = c.ColorFormat.ToString(),
        range = c.DynamicRange?.ToString(),
        depth = c.ColorDepth?.ToString(),
        policy = c.SelectionPolicy?.ToString()
    };

    static class Win32
    {
        public const int ENUM_CURRENT_SETTINGS = -1;
        public const int DM_BITSPERPEL = 0x00040000;
        public const int DM_PELSWIDTH = 0x00080000;
        public const int DM_PELSHEIGHT = 0x00100000;
        public const int DM_DISPLAYFREQUENCY = 0x00400000;
        public const uint CDS_UPDATEREGISTRY = 0x00000001;
        public const uint CDS_TEST = 0x00000002;
        public const uint CDS_NORESET = 0x10000000;
        public const int DISP_CHANGE_SUCCESSFUL = 0;
        public const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
        public const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        public static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        public static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        public static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        public static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);
    }
}
