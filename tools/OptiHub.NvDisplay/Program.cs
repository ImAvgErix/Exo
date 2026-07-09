// OptiHub.NvDisplay — apply NVIDIA display color / scaling via NVAPI (no Control Panel UI).
// Usage:
//   OptiHub.NvDisplay.exe            (apply defaults)
//   OptiHub.NvDisplay.exe --status   (print current only)
//   OptiHub.NvDisplay.exe --apply
// Exit 0 = success (or status printed). Non-zero = hard failure.

using System.Text.Json;
using NvAPIWrapper;
using NvAPIWrapper.Display;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native.Display;
using NvAPIWrapper.Native.Exceptions;
using NvAPIWrapper.Native.GPU;

static class Program
{
    static int Main(string[] args)
    {
        var statusOnly = args.Any(a => a is "--status" or "-s" or "/status");
        var apply = !statusOnly; // default apply
        if (args.Any(a => a is "--apply" or "-a" or "/apply")) apply = true;
        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            Console.WriteLine("OptiHub.NvDisplay — NVAPI display color/scaling (no Control Panel clicks)");
            Console.WriteLine("  --apply   Apply RGB Full, highest BPC, User color policy, GPU no-scaling (default)");
            Console.WriteLine("  --status  Print current color data only");
            return 0;
        }

        try
        {
            NVIDIA.Initialize();
        }
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
                    $"[NVAPI] Display #{dev.DisplayId} BEFORE: " +
                    $"format={before.ColorFormat} range={before.DynamicRange} depth={before.ColorDepth} " +
                    $"policy={before.SelectionPolicy} desktopDepth={before.DesktopColorDepth}");

                if (!apply)
                {
                    results.Add(new
                    {
                        displayId = dev.DisplayId,
                        before = Snapshot(before)
                    });
                    continue;
                }

                // Highest BPC the monitor accepts among 16/12/10/8 (never force unsupported).
                var chosenDepth = PickBestDepth(dev, before.ColorDepth);
                var target = new ColorData(
                    colorFormat: ColorDataFormat.RGB,
                    colorimetry: ColorDataColorimetry.Auto,
                    dynamicRange: ColorDataDynamicRange.VESA, // Full 0-255
                    colorDepth: chosenDepth,
                    colorSelectionPolicy: ColorDataSelectionPolicy.User, // "Use NVIDIA color settings"
                    desktopColorDepth: ColorDataDesktopDepth.Default
                );

                var appliedOk = false;
                foreach (var candidate in BuildColorCandidates(chosenDepth))
                {
                    try
                    {
                        dev.SetColorData(candidate);
                        target = candidate;
                        appliedOk = true;
                        Console.WriteLine(
                            $"[NVAPI] Display #{dev.DisplayId} SET: " +
                            $"format={candidate.ColorFormat} range={candidate.DynamicRange} depth={candidate.ColorDepth} policy={candidate.SelectionPolicy}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[NVAPI] Display #{dev.DisplayId}: try {candidate.ColorDepth}/{candidate.DynamicRange} failed: {ex.Message}");
                    }
                }
                if (!appliedOk)
                    Console.WriteLine($"[NVAPI] Display #{dev.DisplayId}: all SetColorData attempts failed");

                ColorData after;
                try { after = dev.CurrentColorData; }
                catch { after = target; }

                Console.WriteLine(
                    $"[NVAPI] Display #{dev.DisplayId} AFTER: " +
                    $"format={after.ColorFormat} range={after.DynamicRange} depth={after.ColorDepth} " +
                    $"policy={after.SelectionPolicy}");

                results.Add(new
                {
                    displayId = dev.DisplayId,
                    before = Snapshot(before),
                    applied = Snapshot(target),
                    after = Snapshot(after)
                });
            }

            // Scaling: GPU scan-out to native ≈ "No scaling" performed on GPU
            if (apply)
            {
                try
                {
                    ApplyGpuNoScaling();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[NVAPI] Scaling adjust skipped/failed: " + ex.Message);
                }
            }

            // Machine-readable summary line for OptiHub logs
            try
            {
                var json = JsonSerializer.Serialize(new { ok = true, mode = statusOnly ? "status" : "apply", displays = results });
                Console.WriteLine("OPTIHUB_NVDISPLAY_JSON:" + json);
            }
            catch { /* ignore */ }

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
                try
                {
                    connected = gpu.GetConnectedDisplayDevices(ConnectedIdsFlag.None);
                }
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
        if (list.Count > 0)
            return list;

        // Fallback: GDI primary only
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
        // Prefer highest that IsColorDataSupported accepts
        foreach (var depth in new[]
                 {
                     ColorDataDepth.BPC16, ColorDataDepth.BPC12, ColorDataDepth.BPC10,
                     ColorDataDepth.BPC8, ColorDataDepth.BPC6
                 })
        {
            var probe = new ColorData(
                ColorDataFormat.RGB,
                ColorDataColorimetry.Auto,
                ColorDataDynamicRange.VESA,
                depth,
                ColorDataSelectionPolicy.User,
                ColorDataDesktopDepth.Default);
            try
            {
                if (dev.IsColorDataSupported(probe))
                    return depth;
            }
            catch { /* try next */ }
        }

        return current ?? ColorDataDepth.BPC8;
    }

    static IEnumerable<ColorData> BuildColorCandidates(ColorDataDepth preferredDepth)
    {
        var depths = new[]
        {
            preferredDepth, ColorDataDepth.BPC12, ColorDataDepth.BPC10, ColorDataDepth.BPC8,
            ColorDataDepth.Default, ColorDataDepth.BPC16, ColorDataDepth.BPC6
        }.Distinct();
        var ranges = new[] { ColorDataDynamicRange.VESA, ColorDataDynamicRange.Auto };
        foreach (var depth in depths)
        foreach (var range in ranges)
        {
            yield return new ColorData(
                ColorDataFormat.RGB,
                ColorDataColorimetry.Auto,
                range,
                depth,
                ColorDataSelectionPolicy.User,
                ColorDataDesktopDepth.Default);
        }
    }

    static void ApplyGpuNoScaling()
    {
        // Read current path config, set each target scaling to GPUScanOutToNative (GPU + no scaling).
        PathInfo[] paths;
        try
        {
            paths = PathInfo.GetDisplaysConfig();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[NVAPI] GetDisplaysConfig: " + ex.Message);
            return;
        }

        if (paths == null || paths.Length == 0)
        {
            Console.WriteLine("[NVAPI] No path config to adjust scaling.");
            return;
        }

        var changed = false;
        foreach (var path in paths)
        {
            foreach (var target in path.TargetsInfo)
            {
                var before = target.Scaling;
                // GPUScanOutToNative ≈ "No scaling" on GPU
                // GPUScanOutToClosest ≈ full-screen GPU scaling
                if (target.Scaling != Scaling.GPUScanOutToNative)
                {
                    try
                    {
                        target.Scaling = Scaling.GPUScanOutToNative;
                        Console.WriteLine($"[NVAPI] Path target scaling {before} -> GPUScanOutToNative");
                        changed = true;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[NVAPI] Could not set Scaling on target: {ex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine("[NVAPI] Path target already GPUScanOutToNative (no scaling)");
                }
            }
        }

        if (!changed)
        {
            Console.WriteLine("[NVAPI] Scaling unchanged (already desired or not writable).");
            return;
        }

        try
        {
            // SaveNonPersistent avoids writing a permanent topology the user can't undo easily;
            // but we want it to stick — use SaveToPersistence if available.
            PathInfo.SetDisplaysConfig(paths, DisplayConfigFlags.SaveToPersistence | DisplayConfigFlags.ForceModeEnumeration);
            Console.WriteLine("[NVAPI] SetDisplaysConfig applied (GPU no-scaling).");
        }
        catch (Exception ex1)
        {
            try
            {
                PathInfo.SetDisplaysConfig(paths, DisplayConfigFlags.SaveToPersistence);
                Console.WriteLine("[NVAPI] SetDisplaysConfig applied (SaveToPersistence only).");
            }
            catch (Exception ex2)
            {
                Console.WriteLine("[NVAPI] SetDisplaysConfig failed: " + ex1.Message + " / " + ex2.Message);
            }
        }
    }

    static object Snapshot(ColorData c) => new
    {
        format = c.ColorFormat.ToString(),
        range = c.DynamicRange?.ToString(),
        depth = c.ColorDepth?.ToString(),
        policy = c.SelectionPolicy?.ToString(),
        desktopDepth = c.DesktopColorDepth?.ToString()
    };
}
