// OptiHub.NvDisplay — performance display settings via NVAPI + NVTweak registry.
// No Control Panel mouse automation for color/scaling.
//
// Applies per active display:
//   - Best native resolution + highest refresh rate (Windows EnumDisplaySettings)
//   - Color policy User (NVIDIA color settings)
//   - RGB + Full (VESA) + highest supported BPC
//   - HDMI info-frame RGB quantization Full (fixes "Limited" on HDMI)
//   - GPU no-scaling path where the driver allows it
//   - NVTweak: PerformScalingOn=GPU, ScalingOverride=ON, No-scaling mode
//   - Video color/image sources = NVIDIA
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
        var statusOnly = args.Any(a => a is "--status" or "-s" or "/status");
        var apply = !statusOnly;
        if (args.Any(a => a is "--apply" or "-a" or "/apply")) apply = true;
        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            Console.WriteLine("OptiHub.NvDisplay — NVAPI + NVTweak display performance settings");
            Console.WriteLine("  --apply   Apply Full RGB, GPU no-scaling, override ON, video=NVIDIA (default)");
            Console.WriteLine("  --status  Print current color + path scaling only");
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
            var devices = GetActiveDisplays().ToList();
            if (devices.Count == 0)
            {
                Console.Error.WriteLine("[NVAPI] No active NVIDIA displays found.");
                return 3;
            }

            Console.WriteLine($"[NVAPI] Displays: {devices.Count}");
            PrintPathScaling("BEFORE");
            PrintWindowsModes("AVAILABLE");

            var results = new List<object>();
            Dictionary<string, BestMode>? bestModes = null;

            if (apply)
            {
                // 0) Native resolution + highest refresh for every GDI display
                bestModes = ApplyBestNativeModes();
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

                Console.WriteLine(
                    $"[NVAPI] Display #{dev.DisplayId} ({dev.ConnectionType}) BEFORE: " +
                    $"format={before.ColorFormat} range={before.DynamicRange} depth={before.ColorDepth} policy={before.SelectionPolicy}");

                if (!apply)
                {
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
                // 3) Path scaling + push native res / max Hz into NVAPI path where possible
                ApplyGpuNoScaling(bestModes);

                // 4) NVTweak registry — GPU + No scaling + Override ON + Full
                ApplyNvtweakRegistry();

                // 5) Video color/image = NVIDIA (registry sources)
                ApplyVideoNvidiaSources();

                PrintPathScaling("AFTER");
                PrintWindowsModes("CURRENT");
                DumpNvtweakSummary();
            }

            try
            {
                var json = JsonSerializer.Serialize(new { ok = true, mode = statusOnly ? "status" : "apply", displays = results });
                Console.WriteLine("OPTIHUB_NVDISPLAY_JSON:" + json);
            }
            catch { }

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

    static IEnumerable<DisplayDevice> GetActiveDisplays()
    {
        var list = new List<DisplayDevice>();
        try
        {
            foreach (var gpu in PhysicalGPU.GetPhysicalGPUs())
            {
                DisplayDevice[] connected;
                try { connected = gpu.GetConnectedDisplayDevices(ConnectedIdsFlag.None); }
                catch
                {
                    try { connected = gpu.GetConnectedDisplayDevices(ConnectedIdsFlag.UnCached); }
                    catch { continue; }
                }

                foreach (var dev in connected)
                {
                    if (dev == null) continue;
                    if (!(dev.IsActive || dev.IsOSVisible || dev.IsConnected)) continue;
                    list.Add(dev);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[NVAPI] Enum GPUs/displays: " + ex.Message);
        }

        list = list.GroupBy(d => d.DisplayId).Select(g => g.First()).ToList();
        if (list.Count > 0) return list;

        try
        {
            var primary = DisplayDevice.GetGDIPrimaryDisplayDevice();
            if (primary != null) return new[] { primary };
        }
        catch { }

        return Array.Empty<DisplayDevice>();
    }

    static ColorDataDepth PickBestDepth(DisplayDevice dev, ColorDataDepth? current)
    {
        foreach (var depth in new[]
                 {
                     ColorDataDepth.BPC16, ColorDataDepth.BPC12, ColorDataDepth.BPC10,
                     ColorDataDepth.BPC8, ColorDataDepth.BPC6
                 })
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
                     preferredDepth, ColorDataDepth.BPC12, ColorDataDepth.BPC10, ColorDataDepth.BPC8,
                     ColorDataDepth.Default, ColorDataDepth.BPC16, ColorDataDepth.BPC6
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

    static Dictionary<string, BestMode> EnumerateBestModes()
    {
        var map = new Dictionary<string, BestMode>(StringComparer.OrdinalIgnoreCase);
        // Enumerate \\.\DISPLAYn devices via EnumDisplayDevices
        for (uint i = 0; ; i++)
        {
            var dd = new Win32.DISPLAY_DEVICE { cb = Marshal.SizeOf<Win32.DISPLAY_DEVICE>() };
            if (!Win32.EnumDisplayDevices(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & Win32.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            var device = dd.DeviceName; // \\.\DISPLAY1
            var best = FindBestModeForDevice(device);
            if (best != null)
            {
                map[device] = best;
                Console.WriteLine($"[MODE] {device}: best native candidate {best}");
            }
        }
        return map;
    }

    static BestMode? FindBestModeForDevice(string device)
    {
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
        if (modes.Count == 0) return null;

        // "Native" = largest pixel area advertised; at that res, pick highest refresh.
        var maxArea = modes.Max(m => m.Width * m.Height);
        var atNative = modes.Where(m => m.Width * m.Height == maxArea).ToList();
        return atNative.OrderByDescending(m => m.Hz).ThenByDescending(m => m.Bpp).First();
    }

    static Dictionary<string, BestMode> ApplyBestNativeModes()
    {
        Console.WriteLine("[MODE] Applying best native resolution + highest refresh per monitor...");
        var best = EnumerateBestModes();
        if (best.Count == 0)
        {
            Console.WriteLine("[MODE] No modes enumerated");
            return best;
        }

        // Stage each display with CDS_NORESET|CDS_UPDATEREGISTRY, then one global apply.
        foreach (var kv in best)
        {
            var device = kv.Key;
            var mode = kv.Value;
            var dm = new Win32.DEVMODE { dmSize = (short)Marshal.SizeOf<Win32.DEVMODE>() };
            if (!Win32.EnumDisplaySettings(device, Win32.ENUM_CURRENT_SETTINGS, ref dm))
            {
                Console.WriteLine($"[MODE] {device}: could not read current mode");
                continue;
            }

            var cur = $"{dm.dmPelsWidth}x{dm.dmPelsHeight}@{dm.dmDisplayFrequency}";
            if (dm.dmPelsWidth == mode.Width && dm.dmPelsHeight == mode.Height && dm.dmDisplayFrequency == mode.Hz)
            {
                Console.WriteLine($"[MODE] {device}: already {mode}");
                continue;
            }

            dm.dmPelsWidth = mode.Width;
            dm.dmPelsHeight = mode.Height;
            dm.dmDisplayFrequency = mode.Hz;
            dm.dmBitsPerPel = mode.Bpp > 0 ? mode.Bpp : 32;
            dm.dmFields = Win32.DM_PELSWIDTH | Win32.DM_PELSHEIGHT | Win32.DM_DISPLAYFREQUENCY | Win32.DM_BITSPERPEL;

            var flags = Win32.CDS_UPDATEREGISTRY | Win32.CDS_NORESET;
            var rc = Win32.ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
            Console.WriteLine($"[MODE] {device}: stage {cur} -> {mode} (cds={rc})");
        }

        // Apply staged multi-mon changes
        var applyRc = Win32.ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        Console.WriteLine($"[MODE] Commit modes result={applyRc} (0=SUCCESSFUL)");
        // Brief settle — path handles invalidate after mode-set
        Thread.Sleep(800);
        return best;
    }

    static void PrintWindowsModes(string label)
    {
        try
        {
            var best = EnumerateBestModes();
            foreach (var kv in best)
            {
                var dm = new Win32.DEVMODE { dmSize = (short)Marshal.SizeOf<Win32.DEVMODE>() };
                if (Win32.EnumDisplaySettings(kv.Key, Win32.ENUM_CURRENT_SETTINGS, ref dm))
                {
                    Console.WriteLine(
                        $"[MODE] {label} {kv.Key}: current {dm.dmPelsWidth}x{dm.dmPelsHeight}@{dm.dmDisplayFrequency} | best {kv.Value}");
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

    static void ApplyGpuNoScaling(Dictionary<string, BestMode>? bestModes)
    {
        PathInfo[] paths;
        try { paths = PathInfo.GetDisplaysConfig(); }
        catch (Exception ex)
        {
            Console.WriteLine("[NVAPI] GetDisplaysConfig: " + ex.Message);
            return;
        }

        if (paths == null || paths.Length == 0)
        {
            Console.WriteLine("[NVAPI] No path config.");
            return;
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
        }
        catch (Exception ex1)
        {
            try
            {
                PathInfo.SetDisplaysConfig(rebuilt.ToArray(), DisplayConfigFlags.SaveToPersistence);
                Console.WriteLine("[NVAPI] SetDisplaysConfig SaveToPersistence OK");
            }
            catch (Exception ex2)
            {
                Console.WriteLine("[NVAPI] SetDisplaysConfig failed: " + ex1.Message + " / " + ex2.Message);
            }
        }
    }

    static void ApplyNvtweakRegistry()
    {
        // Ensure device keys exist for every connected display (CPL reads these).
        var devicesRoot = @"Software\NVIDIA Corporation\Global\NVTweak\Devices";
        using (var root = Registry.CurrentUser.CreateSubKey(devicesRoot))
        {
            if (root == null) return;
            var subnames = root.GetSubKeyNames();
            if (subnames.Length == 0)
            {
                Console.WriteLine("[NVTweak] No device keys yet — creating from display IDs");
            }

            // Update every existing device key (covers multi-mon)
            foreach (var name in root.GetSubKeyNames())
            {
                using var dev = root.OpenSubKey(name, writable: true);
                if (dev == null) continue;

                // GPU + No scaling + Override games
                dev.SetValue("PerformScalingOn", RegGpu, RegistryValueKind.DWord);
                dev.SetValue("ScalingOverride", 1, RegistryValueKind.DWord);
                dev.SetValue("AppControlledScaling", 0, RegistryValueKind.DWord);
                dev.SetValue("Scaling", RegNoScaling, RegistryValueKind.DWord);
                dev.SetValue("ScalingMode", RegNoScaling, RegistryValueKind.DWord);

                using (var color = dev.CreateSubKey("Color"))
                {
                    if (color != null)
                    {
                        color.SetValue("NvCplUseColorSettings", 1, RegistryValueKind.DWord); // NVIDIA color
                        color.SetValue("ColorFormat", 0, RegistryValueKind.DWord); // RGB
                        color.SetValue("NvCplColorFormat", 0, RegistryValueKind.DWord);
                        color.SetValue("NvCplDigitalColorFormat", 0, RegistryValueKind.DWord);
                        color.SetValue("DynamicRange", RegFullRange, RegistryValueKind.DWord); // Full
                        color.SetValue("NvCplDynamicRange", RegFullRange, RegistryValueKind.DWord);
                        // Prefer 10 bpc in CPL cache; NVAPI already set highest supported
                        color.SetValue("ColorDepth", 10, RegistryValueKind.DWord);
                        color.SetValue("NvCplOutputColorDepthBpc", 10, RegistryValueKind.DWord);
                        color.SetValue("NvCplOutputColorDepth", 3, RegistryValueKind.DWord);
                    }
                }

                Console.WriteLine($"[NVTweak] {name}: GPU + NoScaling + OverrideON + Full RGB");
            }
        }

        // Global Gestalt (unlock advanced UI)
        using (var g = Registry.CurrentUser.CreateSubKey(@"Software\NVIDIA Corporation\Global\NVTweak"))
        {
            g?.SetValue("Gestalt", 1, RegistryValueKind.DWord);
        }

        using (var g = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\NVIDIA Corporation\Global\NVTweak"))
        {
            g?.SetValue("Gestalt", 1, RegistryValueKind.DWord);
        }

        // Client prefs (OptiHub markers + EULA)
        using (var c = Registry.CurrentUser.CreateSubKey(@"Software\NVIDIA Corporation\NVControlPanel2\Client"))
        {
            if (c != null)
            {
                c.SetValue("OptiHubPreferGpuScaling", 1, RegistryValueKind.DWord);
                c.SetValue("OptiHubPreferNoScaling", 1, RegistryValueKind.DWord);
                c.SetValue("OptiHubPreferScalingOverride", 1, RegistryValueKind.DWord);
                c.SetValue("OptiHubPreferFullRgb", 1, RegistryValueKind.DWord);
                c.SetValue("EulaAccepted", 1, RegistryValueKind.DWord);
                c.SetValue("ShowSedoanEula", 0, RegistryValueKind.DWord);
            }
        }
    }

    static void ApplyVideoNvidiaSources()
    {
        // Adjust video color / image settings → "With the NVIDIA settings"
        // Stored under each device\Video (driver/CPL may pick these up on next apply/session).
        var devicesRoot = @"Software\NVIDIA Corporation\Global\NVTweak\Devices";
        using var root = Registry.CurrentUser.CreateSubKey(devicesRoot);
        if (root == null) return;

        foreach (var name in root.GetSubKeyNames())
        {
            using var dev = root.OpenSubKey(name, writable: true);
            if (dev == null) continue;
            using var video = dev.CreateSubKey("Video");
            if (video == null) continue;

            // 1 = use NVIDIA settings (not player / not default)
            void D(string n, int v) => video.SetValue(n, v, RegistryValueKind.DWord);

            D("VideoColorSettingsSource", 1);
            D("VideoImageSettingsSource", 1);
            D("VideoColorSettings", 1);
            D("VideoImageSettings", 1);
            D("UseNVIDIAColorSettings", 1);
            D("UseNVIDIAImageSettings", 1);
            D("ColorSetting", 1);
            D("EdgeEnhanceSetting", 1);
            D("NoiseReductionSetting", 1);
            D("EdgeEnhanceSource", 1);
            D("NoiseReductionSource", 1);
            D("DynamicRange", RegFullRange);
            D("ColorRange", RegFullRange);

            Console.WriteLine($"[NVTweak] {name}\\Video: color/image source = NVIDIA");
        }
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
        public const uint CDS_NORESET = 0x10000000;
        public const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;

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
