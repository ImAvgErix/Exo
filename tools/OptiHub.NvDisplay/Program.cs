// OptiHub.NvDisplay — performance display settings via NVAPI + NVTweak registry.
// No Control Panel mouse automation for color/scaling.
//
// Applies per active display:
//   - Color policy User (NVIDIA color settings)
//   - RGB + Full (VESA) + highest supported BPC
//   - HDMI info-frame RGB quantization Full (fixes "Limited" on HDMI)
//   - GPU no-scaling path where the driver allows it
//   - NVTweak: PerformScalingOn=GPU, ScalingOverride=ON, No-scaling mode
//   - Video color/image sources = NVIDIA (registry; CPL video page may still need Apply once)
//
// Usage: OptiHub.NvDisplay.exe [--apply|--status]

using System.Text.Json;
using Microsoft.Win32;
using NvAPIWrapper;
using NvAPIWrapper.Display;
using NvAPIWrapper.GPU;
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

            var results = new List<object>();
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
                // 3) Path scaling — GPU no-scaling where driver allows
                ApplyGpuNoScaling();

                // 4) NVTweak registry — what CPL shows: GPU + No scaling + Override ON + Full
                ApplyNvtweakRegistry();

                // 5) Video color/image = NVIDIA (registry sources)
                ApplyVideoNvidiaSources();

                PrintPathScaling("AFTER");
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

    static void ApplyGpuNoScaling()
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

        var rebuilt = new List<PathInfo>();
        foreach (var path in paths)
        {
            var targets = new List<PathTargetInfo>();
            foreach (var t in path.TargetsInfo)
            {
                var nt = new PathTargetInfo(t.DisplayDevice)
                {
                    // Prefer GPU no-scaling. Some HDMI sinks refuse this and stay GPUScanOutToClosest (still GPU).
                    Scaling = Scaling.GPUScanOutToNative,
                    IsPreferredUnscaledTarget = true,
                    Rotation = t.Rotation,
                    RefreshRateInMillihertz = t.RefreshRateInMillihertz,
                    TimingOverride = t.TimingOverride,
                    IsInterlaced = t.IsInterlaced,
                    TVFormat = t.TVFormat,
                    TVConnectorType = t.TVConnectorType,
                    IsClonePrimary = t.IsClonePrimary,
                    IsClonePanAndScanTarget = t.IsClonePanAndScanTarget,
                    DisableVirtualModeSupport = t.DisableVirtualModeSupport
                };
                Console.WriteLine(
                    $"[NVAPI] Path target #{t.DisplayDevice.DisplayId}: {t.Scaling} -> GPUScanOutToNative (request)");
                targets.Add(nt);
            }

            var np = new PathInfo(path.Resolution, path.ColorFormat, targets.ToArray())
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
}
