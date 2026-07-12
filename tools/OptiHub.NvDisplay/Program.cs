// OptiHub.NvDisplay — performance display settings via NVAPI + NVTweak registry.
// No Control Panel mouse automation for color/scaling.
//
// Applies per active display:
//   - Current resolution kept
//   - PRIMARY monitor: highest supported refresh (gaming)
//   - SECONDARY monitors: 60 Hz (performance / less GPU load for desktop clone)
//   - Color policy User (NVIDIA color settings)
//   - RGB + Full (VESA) + current supported color depth (10/8 bpc fallback)
//   - HDMI info-frame RGB quantization Full (fixes "Limited" on HDMI)
//   - GPU no-scaling path where the driver allows it
//   - NVTweak: PerformScalingOn=GPU, ScalingOverride=ON, No-scaling mode
//   - NVTweak Gestalt=2 (Use the advanced 3D image settings)
//
// Usage: OptiHub.NvDisplay.exe [--apply|--status]

using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using NvAPIWrapper;
using NvAPIWrapper.Display;
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
    private const int RegNoScaling = 2;
    private const int RegFullRange = 0; // matches ColorDataDynamicRange.VESA = 0
    private const int RegLimitedRange = 1; // CEA

    static int Main(string[] args)
    {
        var normalizedArgs = args.Select(a => a.Trim().ToLowerInvariant()).ToArray();
        var knownArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--status", "-s", "/status", "--apply", "-a", "/apply", "--help", "-h", "/?"
        };
        var unknown = normalizedArgs.FirstOrDefault(a => !knownArgs.Contains(a));
        if (unknown != null)
        {
            Console.Error.WriteLine($"Unknown argument: {unknown}");
            return 64;
        }

        var statusOnly = normalizedArgs.Any(a => a is "--status" or "-s" or "/status");
        var apply = !statusOnly;
        if (normalizedArgs.Any(a => a is "--apply" or "-a" or "/apply")) apply = true;
        if (statusOnly && apply)
        {
            Console.Error.WriteLine("Choose either --status or --apply, not both.");
            return 64;
        }
        if (normalizedArgs.Any(a => a is "--help" or "-h" or "/?"))
        {
            Console.WriteLine("OptiHub.NvDisplay — NVAPI + NVTweak display performance settings");
            Console.WriteLine("  --apply   Apply Full RGB, primary max-Hz / secondary 60Hz, GPU no-scaling (default)");
            Console.WriteLine("  --status  Verify Full RGB, refresh policy, and GPU no-scaling without changing settings");
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
                Console.WriteLine("OPTIHUB_NVDISPLAY_JSON:" + failureJson);
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
                Console.WriteLine("OPTIHUB_NVDISPLAY_JSON:" + json);
                return 0;
            }

            Console.WriteLine($"[NVAPI] Displays: {devices.Count}");
            PrintPathScaling("BEFORE");
            var idToGdi = MapDisplayIdToGdiName();
            var missingDisplayIds = devices
                .Where(d => !idToGdi.TryGetValue(d.DisplayId, out var name) || string.IsNullOrWhiteSpace(name))
                .Select(d => d.DisplayId)
                .ToArray();
            if (missingDisplayIds.Length > 0)
            {
                var missing = string.Join(", ", missingDisplayIds.Select(id => $"#{id}"));
                Console.Error.WriteLine($"[MODE] Complete NVIDIA-to-Windows mapping unavailable: {missing}");
                var mappingJson = JsonSerializer.Serialize(new
                {
                    ok = false,
                    mode = apply ? "apply" : "status",
                    error = "incomplete-display-mapping",
                    missingDisplayIds
                });
                Console.WriteLine("OPTIHUB_NVDISPLAY_JSON:" + mappingJson);
                return 6;
            }
            var nvidiaGdiNames = devices
                .Select(d => idToGdi[d.DisplayId])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (nvidiaGdiNames.Count == 0)
            {
                Console.Error.WriteLine("[MODE] No usable Windows display names were mapped.");
                return 6;
            }
            PrintWindowsModes("AVAILABLE", nvidiaGdiNames);

            var results = new List<object>();
            Dictionary<string, BestMode>? bestModes = null;
            var modesOk = true;
            var colorReadCount = 0;
            var colorAppliedCount = 0;
            var colorOptimalCount = 0;

            if (apply && nvidiaGdiNames.Count > 0)
            {
                // Keep current resolution. Primary -> max Hz; secondary -> 60 Hz.
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
                    Console.WriteLine($"[NVAPI] Display #{dev.DisplayId}: read color failed: {ex.Message}");
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

                // 1) Color: User policy + RGB + Full + best BPC
                var chosenDepth = PickBestDepth(dev, before.ColorDepth);
                var appliedColor = ApplyColorWithFallbacks(dev, chosenDepth);

                // 2) HDMI: force Full range in info-frame (fixes Limited look on TVs/monitors)
                if (dev.ConnectionType == MonitorConnectionType.HDMI ||
                    dev.ConnectionType.ToString().Contains("HDMI", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyHdmiFullRange(dev);
                }

                ColorData after;
                try { after = dev.CurrentColorData; }
                catch { after = appliedColor ?? before; }
                if (appliedColor != null && IsFullRgbUserColor(after))
                    colorAppliedCount++;

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
                // 3) Path scaling + push current-resolution / target-Hz mode into NVAPI where possible
                var scalingOk = ApplyGpuNoScaling(bestModes);

                // 4) NVTweak registry — GPU + No scaling + Override ON + Full + advanced 3D
                ApplyNvtweakRegistry();

                PrintPathScaling("AFTER");
                PrintWindowsModes("CURRENT", nvidiaGdiNames);
                DumpNvtweakSummary();

                var colorOk = colorReadCount == devices.Count && colorAppliedCount == devices.Count;
                var ok = colorOk && modesOk && scalingOk;
                try
                {
                    var json = JsonSerializer.Serialize(new
                    {
                        ok,
                        mode = "apply",
                        displays = results,
                        checks = new { colorOk, modesOk, scalingOk },
                        refreshPolicy = "primary-max-hz, secondary-60hz"
                    });
                    Console.WriteLine("OPTIHUB_NVDISPLAY_JSON:" + json);
                }
                catch { }

                if (!ok)
                {
                    Console.Error.WriteLine(
                        $"[NVAPI] Apply incomplete: color={colorOk}, modes={modesOk}, scaling={scalingOk}");
                    return 6;
                }
            }
            else
            {
                var colorOk = colorReadCount == devices.Count && colorOptimalCount == devices.Count;
                var refreshOk = VerifyTargetRefreshModes(nvidiaGdiNames);
                var scalingOk = VerifyGpuScaling();
                var registryOk = VerifyNvtweakRegistry();
                var ok = colorOk && refreshOk && scalingOk && registryOk;
                try
                {
                    var json = JsonSerializer.Serialize(new
                    {
                        ok,
                        mode = "status",
                        displays = results,
                        checks = new { colorOk, refreshOk, scalingOk, registryOk },
                        refreshPolicy = "primary-max-hz, secondary-60hz"
                    });
                    Console.WriteLine("OPTIHUB_NVDISPLAY_JSON:" + json);
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
                    return new DisplayEnumerationResult
                    {
                        Succeeded = false,
                        Error = "connected-display enumeration failed: " +
                                cachedError.Message + " / " + uncachedError.Message
                    };
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
        return new DisplayEnumerationResult { Succeeded = true, Devices = list };
    }

    static ColorDataDepth PickBestDepth(DisplayDevice dev, ColorDataDepth? current)
    {
        // Preserve the current color depth when the target mode supports it. This
        // avoids needlessly downgrading a valid 12 bpc HDMI mode. If it is no
        // longer valid at the selected refresh rate, prefer 10 bpc and then 8 bpc.
        var candidates = current is null
            ? new[] { ColorDataDepth.BPC10, ColorDataDepth.BPC8, ColorDataDepth.BPC6 }
            : new[] { current.Value, ColorDataDepth.BPC10, ColorDataDepth.BPC8, ColorDataDepth.BPC6 }
                .Distinct();

        foreach (var depth in candidates)
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
                var role = primary ? "PRIMARY max-Hz" : "SECONDARY 60Hz";
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

        if (primary)
        {
            // Gaming / main monitor: highest refresh at current resolution
            return sameResolution.OrderByDescending(m => m.Hz).ThenByDescending(m => m.Bpp).First();
        }

        // Secondary: lock to 60 Hz for desktop performance (less GPU compositor load).
        // Prefer exact 60, then 59-61 (59.94), then highest <= 60, else lowest available.
        var exact60 = sameResolution.Where(m => m.Hz == 60).OrderByDescending(m => m.Bpp).FirstOrDefault();
        if (exact60 != null) return exact60;

        var near60 = sameResolution
            .Where(m => m.Hz >= 59 && m.Hz <= 61)
            .OrderBy(m => Math.Abs(m.Hz - 60))
            .ThenByDescending(m => m.Bpp)
            .FirstOrDefault();
        if (near60 != null) return near60;

        var atOrBelow60 = sameResolution.Where(m => m.Hz <= 60).OrderByDescending(m => m.Hz).ThenByDescending(m => m.Bpp).FirstOrDefault();
        if (atOrBelow60 != null) return atOrBelow60;

        return sameResolution.OrderBy(m => m.Hz).ThenByDescending(m => m.Bpp).First();
    }

    // Back-compat name used by older call sites
    static BestMode? FindBestModeForDevice(string device) => FindTargetModeForDevice(device);

    static ModeApplyResult ApplyTargetRefreshModes(IReadOnlySet<string> allowedDevices)
    {
        if (allowedDevices.Count == 0)
            return new ModeApplyResult { Success = true };

        Console.WriteLine("[MODE] Primary = max Hz; secondary = 60 Hz (current resolution kept)...");
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
            // Brief settle - path handles invalidate after mode-set.
            Thread.Sleep(800);
        }

        foreach (var kv in usable)
        {
            var current = new Win32.DEVMODE { dmSize = (short)Marshal.SizeOf<Win32.DEVMODE>() };
            var verified = Win32.EnumDisplaySettings(kv.Key, Win32.ENUM_CURRENT_SETTINGS, ref current) &&
                           current.dmPelsWidth == kv.Value.Width &&
                           current.dmPelsHeight == kv.Value.Height &&
                           current.dmDisplayFrequency == kv.Value.Hz;
            Console.WriteLine($"[MODE] Verify {kv.Key}: target {kv.Value}, applied={verified}");
            success &= verified;
        }

        return new ModeApplyResult { Modes = usable, Success = success };
    }

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

    static bool ApplyGpuNoScaling(Dictionary<string, BestMode>? bestModes)
    {
        PathInfo[] paths;
        try { paths = PathInfo.GetDisplaysConfig(); }
        catch (Exception ex)
        {
            Console.WriteLine("[NVAPI] GetDisplaysConfig: " + ex.Message);
            return false;
        }

        if (paths == null || paths.Length == 0)
        {
            Console.WriteLine("[NVAPI] No path config.");
            return false;
        }

        var idToGdi = MapDisplayIdToGdiName();

        var rebuilt = new List<PathInfo>();
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
                    Console.WriteLine(
                        $"[NVAPI] Path target #{t.DisplayDevice.DisplayId} ({gdi}): use mode {bm} ({hzMHz} mHz)");
                }

                var nt = new PathTargetInfo(t.DisplayDevice)
                {
                    // Prefer GPU no-scaling. Some HDMI sinks refuse this and stay GPUScanOutToClosest (still GPU).
                    Scaling = Scaling.GPUScanOutToNative,
                    IsPreferredUnscaledTarget = true,
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
                    $"[NVAPI] Path target #{t.DisplayDevice.DisplayId}: {t.Scaling} -> GPUScanOutToNative, refresh={hzMHz}mHz");
                targets.Add(nt);
            }

            var res = path.Resolution;
            if (pathBest != null)
            {
                try { res = new Resolution(pathBest.Width, pathBest.Height, 32); }
                catch { res = path.Resolution; }
            }

            var np = new PathInfo(res, path.ColorFormat, targets.ToArray())
            {
                SourceId = path.SourceId,
                IsGDIPrimary = path.IsGDIPrimary,
                Position = path.Position
            };
            rebuilt.Add(np);
        }

        try
        {
            PathInfo.SetDisplaysConfig(rebuilt.ToArray(),
                DisplayConfigFlags.SaveToPersistence | DisplayConfigFlags.ForceModeEnumeration);
            Console.WriteLine("[NVAPI] SetDisplaysConfig (GPU no-scaling request) OK");
            return VerifyGpuScaling();
        }
        catch (Exception ex1)
        {
            try
            {
                PathInfo.SetDisplaysConfig(rebuilt.ToArray(), DisplayConfigFlags.SaveToPersistence);
                Console.WriteLine("[NVAPI] SetDisplaysConfig SaveToPersistence OK");
                return VerifyGpuScaling();
            }
            catch (Exception ex2)
            {
                Console.WriteLine("[NVAPI] SetDisplaysConfig failed: " + ex1.Message + " / " + ex2.Message);
                return false;
            }
        }
    }

    static bool VerifyTargetRefreshModes(IReadOnlySet<string> allowedDevices)
    {
        if (allowedDevices.Count == 0) return false;
        var allOptimal = true;
        foreach (var device in allowedDevices)
        {
            var current = new Win32.DEVMODE { dmSize = (short)Marshal.SizeOf<Win32.DEVMODE>() };
            var primary = IsPrimaryDisplayDevice(device);
            var target = FindTargetModeForDevice(device, primary);
            if (target == null || !Win32.EnumDisplaySettings(device, Win32.ENUM_CURRENT_SETTINGS, ref current))
            {
                Console.WriteLine($"[MODE] Verify {device}: unable to read current/target mode");
                allOptimal = false;
                continue;
            }
            var optimal = current.dmPelsWidth == target.Width && current.dmPelsHeight == target.Height &&
                          current.dmDisplayFrequency == target.Hz;
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
            var targets = PathInfo.GetDisplaysConfig()
                .SelectMany(path => path.TargetsInfo)
                .ToArray();
            if (targets.Length == 0)
                return false;

            var allGpu = true;
            foreach (var target in targets)
            {
                var scaling = target.Scaling.ToString();
                var gpu = scaling.StartsWith("GPUScanOut", StringComparison.Ordinal);
                Console.WriteLine($"[NVAPI] Verify path #{target.DisplayDevice.DisplayId}: Scaling={scaling} GPU={gpu}");
                allGpu &= gpu;
            }

            return allGpu;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[NVAPI] Scaling verification failed: " + ex.Message);
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

                // Desktop size/position: GPU + No scaling + Override ON (all aliases CPL may read)
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

    static bool VerifyNvtweakRegistry()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Software\NVIDIA Corporation\Global\NVTweak\Devices");
            if (root == null || root.GetSubKeyNames().Length == 0) return true;
            var ok = true;
            foreach (var name in root.GetSubKeyNames())
            {
                using var dev = root.OpenSubKey(name);
                if (dev == null) { ok = false; continue; }
                var deviceOk = Convert.ToInt32(dev.GetValue("PerformScalingOn", -1)) == RegGpu &&
                               Convert.ToInt32(dev.GetValue("ScalingOverride", -1)) == 1 &&
                               Convert.ToInt32(dev.GetValue("ScalingMode", -1)) == RegNoScaling;
                Console.WriteLine($"[NVTweak] Verify {name}: performance scaling={deviceOk}");
                ok &= deviceOk;
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
