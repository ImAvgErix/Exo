using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using Exo.Helpers;
using Exo.Models;

namespace Exo.Services;

/// <summary>
/// Defensive home-dashboard reads. Live memory + process WS + file-backed
/// Steam/Internet/NVIDIA state — never invents FPS totals.
/// </summary>
public static class HomeDashboardReader
{
    public sealed record MemorySnapshot(
        ulong TotalBytes,
        ulong AvailableBytes,
        uint LoadPercent);

    /// <summary>
    /// Static-ish machine identity for the home strip. Resolved once and cached —
    /// not a live probe loop.
    /// </summary>
    public sealed record SystemSpecsSnapshot(
        string CpuName,
        int LogicalProcessors,
        string? GpuName,
        ulong TotalRamBytes,
        /// <summary>e.g. "Corsair 32 GB" or "32 GB · 3600 MT/s".</summary>
        string RamLabel,
        string OsName);

    public sealed record LatencySnapshot(
        double BeforeP50Ms,
        double AfterP50Ms,
        double BeforeJitterMs,
        double AfterJitterMs,
        double BeforeDnsMs,
        double AfterDnsMs);

    public sealed record NvidiaPathSnapshot(
        bool ProfileApplied,
        bool Gsync,
        string? ProfileFile,
        string? GpuName,
        string? Series,
        string? PrimaryMode,
        string? PrimaryConnection,
        string? PolicySource,
        int GameProfileCount,
        int VerifiedSettingCount);

    /// <summary>
    /// Discord live working set + session peak. The delta is only a session-level
    /// observation: it must not be described as memory reclaimed by Exo.
    /// </summary>
    public sealed record DiscordRamSnapshot(
        long LiveBytes,
        long PeakBytes,
        long BelowPeakBytes);

    public sealed record ProcessMemorySnapshot(
        int ProcessCount,
        long PrivateBytes,
        long WorkingSetBytes);

    /// <summary>Primary up NIC link speed for the Internet tile.</summary>
    public sealed record LinkSpeedSnapshot(
        string Label,
        long BitsPerSecond,
        string MediaKind);

    /// <summary>Sum WorkingSet64 for all processes matching any of the names (case-insensitive).</summary>
    public static long TryReadProcessWorkingSetBytes(params string[] processNames)
    {
        if (processNames is null || processNames.Length == 0) return 0;
        long total = 0;
        try
        {
            foreach (var name in processNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        total += p.WorkingSet64;
                    }
                    catch { /* access denied / exited */ }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
        }
        catch { /* ignore */ }
        return total;
    }

    public static ProcessMemorySnapshot TryReadProcessMemory(params string[] processNames)
    {
        var count = 0;
        long privateBytes = 0;
        long workingSetBytes = 0;
        foreach (var name in processNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var process in processes)
            {
                try
                {
                    count++;
                    privateBytes += Math.Max(0, process.PrivateMemorySize64);
                    workingSetBytes += Math.Max(0, process.WorkingSet64);
                }
                catch { }
                finally { process.Dispose(); }
            }
        }
        return new ProcessMemorySnapshot(count, privateBytes, workingSetBytes);
    }

    /// <summary>True only while the optimized Steam background policy is running.</summary>
    public static bool TryReadSteamMemoryGuardRunning()
    {
        try
        {
            using var mutex = Mutex.OpenExisting(@"Local\Exo.SteamMemoryGuard");
            return mutex is not null;
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch { return false; }
    }

    /// <summary>
    /// Sample Discord working set and persist a session peak so the home tile can
    /// show the current resident set relative to its observed session peak.
    /// </summary>
    public static DiscordRamSnapshot? TrySampleDiscordRam()
    {
        try
        {
            var live = TryReadProcessWorkingSetBytes("Discord", "DiscordPTB", "DiscordCanary");
            var path = Path.Combine(PathHelper.AppDataDir, "discord-ram-stats.json");
            long peak = 0;
            long belowPeak = 0;

            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                peak = ReadInt64(root, "peakWorkingSetBytes");
                belowPeak = ReadInt64(root, "sessionReclaimedBytes");
            }

            if (live <= 0)
            {
                // Process gone — keep the last session observation until next open.
                if (peak <= 0 && belowPeak <= 0) return null;
                return new DiscordRamSnapshot(0, peak, Math.Max(0, belowPeak));
            }

            if (live > peak)
                peak = live;

            // A peak delta can be caused by GC, paging, idle trim, or normal workload change.
            var drop = peak - live;
            if (drop > belowPeak)
                belowPeak = drop;

            try
            {
                Directory.CreateDirectory(PathHelper.AppDataDir);
                var json =
                    "{\n" +
                    $"  \"peakWorkingSetBytes\": {peak},\n" +
                    $"  \"liveWorkingSetBytes\": {live},\n" +
                    $"  \"sessionReclaimedBytes\": {belowPeak},\n" +
                    $"  \"updatedUtc\": \"{DateTime.UtcNow:O}\"\n" +
                    "}\n";
                File.WriteAllText(path, json);
            }
            catch { /* non-fatal */ }

            return new DiscordRamSnapshot(live, peak, belowPeak);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Best up NIC link speed (Ethernet preferred over Wi‑Fi).</summary>
    public static LinkSpeedSnapshot? TryReadPrimaryLinkSpeed()
    {
        try
        {
            NetworkInterface? best = null;
            long bestScore = -1;
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback
                    or NetworkInterfaceType.Tunnel) continue;

                var eth = n.NetworkInterfaceType == NetworkInterfaceType.Ethernet;
                var wifi = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                if (!eth && !wifi) continue;

                long speed = 0;
                try { speed = n.Speed; } catch { speed = 0; }
                // Prefer Ethernet; among equals prefer higher rate.
                var score = (eth ? 1_000_000_000_000L : 0L) + Math.Max(0, speed);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = n;
                }
            }

            if (best is null) return null;
            long bps = 0;
            try { bps = best.Speed; } catch { bps = 0; }
            var kind = best.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                ? "Wi‑Fi"
                : "Ethernet";
            return new LinkSpeedSnapshot(FormatLinkSpeed(bps), bps, kind);
        }
        catch
        {
            return null;
        }
    }

    public static string FormatLinkSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0) return "—";
        // NIC.Speed is bits/sec; 2.5G reports ~2_500_000_000.
        if (bitsPerSecond >= 2_400_000_000)
            return $"{bitsPerSecond / 1_000_000_000.0:0.#}G";
        if (bitsPerSecond >= 900_000_000)
            return "1G";
        if (bitsPerSecond >= 90_000_000)
            return $"{Math.Max(1, bitsPerSecond / 1_000_000)}M";
        return $"{Math.Max(1, bitsPerSecond / 1_000)}K";
    }

    public static string? TryReadInternetStatus()
    {
        try
        {
            var path = Path.Combine(PathHelper.AppDataDir, "network-optimizer.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("lastApplyReport", out var report) && report.ValueKind == JsonValueKind.Array)
            {
                foreach (var step in report.EnumerateArray())
                {
                    if (step.TryGetProperty("status", out var status) &&
                        status.ValueKind == JsonValueKind.String &&
                        string.Equals(status.GetString(), "fail", StringComparison.OrdinalIgnoreCase))
                        return null;
                }
            }
            if (root.TryGetProperty("preset", out var p) && p.ValueKind == JsonValueKind.String)
            {
                var preset = p.GetString();
                if (!string.IsNullOrWhiteSpace(preset))
                    return preset;
            }
            if (root.TryGetProperty("lastPreset", out var lp) && lp.ValueKind == JsonValueKind.String)
            {
                var preset = lp.GetString();
                if (!string.IsNullOrWhiteSpace(preset))
                    return preset;
            }
            if (root.TryGetProperty("applied", out var a) && a.ValueKind == JsonValueKind.True)
                return "Applied";
            return null;
        }
        catch
        {
            return null;
        }
    }

    public static string? TryReadInternetDnsStatus()
    {
        try
        {
            var path = Path.Combine(PathHelper.AppDataDir, "network-optimizer.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("lastApplyReport", out var report) || report.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var step in report.EnumerateArray())
            {
                if (!step.TryGetProperty("name", out var name) ||
                    !string.Equals(name.GetString(), "dns-auto", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!step.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String)
                    return null;
                var detail = reason.GetString();
                if (string.IsNullOrWhiteSpace(detail)) return null;
                var provider = detail.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                if (string.IsNullOrWhiteSpace(provider)) return null;
                return detail.Contains("automatic DoH active", StringComparison.OrdinalIgnoreCase)
                    ? provider + " DNS + automatic DoH"
                    : provider + " DNS selected";
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public static bool TryReadDiscordApplied()
    {
        try
        {
            var path = Path.Combine(PathHelper.AppDataDir, "discord-optimizer.json");
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("applied", out var a) && a.ValueKind == JsonValueKind.True)
                return true;
            if (root.TryGetProperty("applyStatus", out var s) && s.ValueKind == JsonValueKind.String)
                return string.Equals(s.GetString(), "applied", StringComparison.OrdinalIgnoreCase);
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryReadDiscordKernelOnDisk()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var root = Path.Combine(local, "Discord");
            if (!Directory.Exists(root)) return false;
            var apps = Directory.GetDirectories(root, "app-*");
            if (apps.Length == 0) return false;
            Array.Sort(apps, StringComparer.OrdinalIgnoreCase);
            var app = apps[^1];
            var ver = Path.Combine(app, "version.dll");
            var ini = Path.Combine(app, "config.ini");
            var ff = Path.Combine(app, "ffmpeg.dll");
            var real = Path.Combine(app, "ffmpeg_real.dll");
            if (!File.Exists(ver) || !File.Exists(ini) || !File.Exists(ff) || !File.Exists(real))
                return false;
            return new FileInfo(ff).Length < 500_000 && new FileInfo(ver).Length >= 50_000;
        }
        catch
        {
            return false;
        }
    }

    public static MemorySnapshot? TryReadMemory()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status)) return null;
            if (status.ullTotalPhys == 0) return null;
            return new MemorySnapshot(
                status.ullTotalPhys,
                status.ullAvailPhys,
                status.dwMemoryLoad);
        }
        catch
        {
            return null;
        }
    }

    private static SystemSpecsSnapshot? _cachedSpecs;
    private static long _prevIdleTicks;
    private static long _prevKernelTicks;
    private static long _prevUserTicks;
    private static bool _cpuSamplePrimed;

    /// <summary>
    /// Cheap system identity for the dashboard strip. Cached after first success.
    /// </summary>
    public static SystemSpecsSnapshot? TryReadSystemSpecs()
    {
        if (_cachedSpecs is not null) return _cachedSpecs;
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var cpu = ReadRegistryString(
                Microsoft.Win32.Registry.LocalMachine,
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString") ?? "CPU";
            cpu = CompactCpuName(cpu.Trim());

            var logicals = Environment.ProcessorCount;
            ulong totalRam = 0;
            var mem = TryReadMemory();
            if (mem is not null) totalRam = mem.TotalBytes;

            var gpu = TryReadPrimaryGpuName();
            var os = ResolveOsLabel();
            var ramLabel = BuildRamLabel(totalRam);

            _cachedSpecs = new SystemSpecsSnapshot(cpu, logicals, gpu, totalRam, ramLabel, os);
            return _cachedSpecs;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Live total CPU load (0–100). First call primes baselines and returns null.
    /// </summary>
    public static double? TryReadCpuLoadPercent()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
                return null;

            var idleTicks = FileTimeToInt64(idle);
            var kernelTicks = FileTimeToInt64(kernel);
            var userTicks = FileTimeToInt64(user);

            if (!_cpuSamplePrimed)
            {
                _prevIdleTicks = idleTicks;
                _prevKernelTicks = kernelTicks;
                _prevUserTicks = userTicks;
                _cpuSamplePrimed = true;
                return null;
            }

            var idleDelta = idleTicks - _prevIdleTicks;
            var kernelDelta = kernelTicks - _prevKernelTicks;
            var userDelta = userTicks - _prevUserTicks;
            _prevIdleTicks = idleTicks;
            _prevKernelTicks = kernelTicks;
            _prevUserTicks = userTicks;

            // Kernel includes idle time on Windows.
            var total = kernelDelta + userDelta;
            if (total <= 0) return null;
            var busy = total - idleDelta;
            if (busy < 0) busy = 0;
            var pct = 100.0 * busy / total;
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            return pct;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// ProductName still says "Windows 10" on real Windows 11 machines.
    /// Build ≥ 22000 is the reliable Win11 gate.
    /// </summary>
    public static string ResolveOsLabel()
    {
        try
        {
            var displayVersion = ReadRegistryString(
                Microsoft.Win32.Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                "DisplayVersion");
            var buildText = ReadRegistryString(
                Microsoft.Win32.Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                "CurrentBuild")
                ?? ReadRegistryString(
                    Microsoft.Win32.Registry.LocalMachine,
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                    "CurrentBuildNumber");
            var build = 0;
            _ = int.TryParse(buildText, out build);

            // Also honor CurrentMajorVersionNumber when present (10 for both 10/11).
            var isWin11 = build >= 22000;
            if (!isWin11)
            {
                // Fallback: some images only expose UBR/build under different keys.
                var product = ReadRegistryString(
                    Microsoft.Win32.Registry.LocalMachine,
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                    "ProductName") ?? "";
                isWin11 = product.Contains("Windows 11", StringComparison.OrdinalIgnoreCase);
            }

            if (isWin11)
                return string.IsNullOrWhiteSpace(displayVersion) ? "Windows 11" : $"Windows 11 {displayVersion}";

            var productName = ReadRegistryString(
                Microsoft.Win32.Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                "ProductName") ?? "Windows";
            if (productName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) || build >= 10240)
                return string.IsNullOrWhiteSpace(displayVersion) ? "Windows 10" : $"Windows 10 {displayVersion}";
            return string.IsNullOrWhiteSpace(productName) ? "Windows" : productName;
        }
        catch
        {
            return "Windows";
        }
    }

    private static int? _cachedMemoryMhz;
    private static double? _cachedGpuLoad;
    private static long _gpuLoadNextUtcTicks;

    /// <summary>Installed DRAM speed (MHz), average across modules. Cached.</summary>
    public static int? TryReadMemorySpeedMhz()
    {
        if (_cachedMemoryMhz.HasValue) return _cachedMemoryMhz;
        try
        {
            // Late-bound WMI — no System.Management package dependency.
            var t = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (t is null) return null;
            dynamic locator = Activator.CreateInstance(t)!;
            dynamic services = locator.ConnectServer(".", "root\\cimv2");
            dynamic items = services.ExecQuery("SELECT Speed FROM Win32_PhysicalMemory");
            double sum = 0;
            var n = 0;
            foreach (dynamic item in items)
            {
                try
                {
                    var speed = Convert.ToInt32(item.Speed);
                    if (speed > 0) { sum += speed; n++; }
                }
                catch { }
            }
            if (n <= 0) return null;
            _cachedMemoryMhz = (int)Math.Round(sum / n);
            return _cachedMemoryMhz;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Best-effort GPU utilization (0–100). Prefers nvidia-smi (whole-GPU), then
    /// sums engtype_3D process counters (capped). Throttled; null when unavailable.
    /// </summary>
    public static double? TryReadGpuLoadPercent()
    {
        var now = DateTime.UtcNow.Ticks;
        if (_cachedGpuLoad.HasValue && now < _gpuLoadNextUtcTicks)
            return _cachedGpuLoad;

        _gpuLoadNextUtcTicks = now + TimeSpan.FromSeconds(1.5).Ticks;

        var nv = TryReadNvidiaSmiGpuLoad();
        if (nv is not null)
        {
            _cachedGpuLoad = nv;
            return _cachedGpuLoad;
        }

        try
        {
            var t = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (t is null) return _cachedGpuLoad;
            dynamic locator = Activator.CreateInstance(t)!;
            dynamic services = locator.ConnectServer(".", "root\\cimv2");
            dynamic items = services.ExecQuery(
                "SELECT UtilizationPercentage, Name FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
            double sum3d = 0;
            var any3d = false;
            double bestAny = -1;
            foreach (dynamic item in items)
            {
                try
                {
                    var name = (string?)(item.Name?.ToString() ?? "");
                    var util = Convert.ToDouble(item.UtilizationPercentage);
                    if (util < 0 || util > 100) continue;
                    bestAny = Math.Max(bestAny, util);
                    if (!string.IsNullOrEmpty(name) &&
                        name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    {
                        sum3d += util;
                        any3d = true;
                    }
                }
                catch { }
            }

            double best = any3d ? Math.Min(100, sum3d) : bestAny;
            if (best < 0)
            {
                items = services.ExecQuery(
                    "SELECT UtilizationPercentage FROM Win32_PerfFormattedData_Counters_GPUEngine");
                foreach (dynamic item in items)
                {
                    try
                    {
                        var util = Convert.ToDouble(item.UtilizationPercentage);
                        if (util >= 0 && util <= 100)
                            best = Math.Max(best, util);
                    }
                    catch { }
                }
            }
            if (best < 0) return _cachedGpuLoad;
            _cachedGpuLoad = Math.Round(best, 0);
            return _cachedGpuLoad;
        }
        catch
        {
            return _cachedGpuLoad;
        }
    }

    private static double? TryReadNvidiaSmiGpuLoad()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=utilization.gpu --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return null;
            if (!p.WaitForExit(900))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            var line = p.StandardOutput.ReadToEnd().Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(line)) return null;
            // Multi-GPU: take the max load.
            double best = -1;
            foreach (var part in line.Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (double.TryParse(part, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var v) &&
                    v >= 0 && v <= 100)
                    best = Math.Max(best, v);
            }
            return best < 0 ? null : Math.Round(best, 0);
        }
        catch
        {
            return null;
        }
    }

    private static string CompactCpuName(string name)
    {
        // Strip common vendor noise so the strip stays one line.
        name = name
            .Replace("AMD ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Intel(R) ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Intel ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("CPU ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ")
            .Trim();
        // Drop trailing "@ 3.40GHz" style clock — cores/RAM already convey class.
        var at = name.IndexOf(" @", StringComparison.Ordinal);
        if (at > 0) name = name[..at].Trim();
        if (name.Length > 36) name = name[..33].TrimEnd() + "…";
        return name;
    }

    private static string? TryReadPrimaryGpuName()
    {
        // Prefer last verified NVIDIA apply identity when present.
        try
        {
            var nv = TryReadNvidiaPath();
            if (!string.IsNullOrWhiteSpace(nv?.GpuName))
                return CompactGpuName(nv!.GpuName!);
            if (!string.IsNullOrWhiteSpace(nv?.Series))
                return CompactGpuName(nv!.Series!);
        }
        catch { /* fall through */ }

        // Enumerate display adapter class keys (0000, 0001, …).
        try
        {
            const string classPath =
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(classPath);
            if (root is null) return null;
            string? best = null;
            foreach (var sub in root.GetSubKeyNames())
            {
                if (sub.Length != 4 || !char.IsDigit(sub[0])) continue;
                using var key = root.OpenSubKey(sub);
                var desc = key?.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(desc)) continue;
                // Skip Microsoft Basic / remote display adapters.
                if (desc.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                    !desc.Contains("Xbox", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (desc.Contains("Remote", StringComparison.OrdinalIgnoreCase)) continue;
                // Prefer discrete NVIDIA/AMD names over generic.
                if (desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("RX ", StringComparison.OrdinalIgnoreCase))
                    return CompactGpuName(desc);
                best ??= CompactGpuName(desc);
            }
            return best;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Keep product family (GeForce / Radeon) so the UI reads "GeForce RTX 3070",
    /// not just "RTX 3070". Strip only redundant vendor noise.
    /// </summary>
    private static string CompactGpuName(string name)
    {
        name = name
            .Replace("NVIDIA GeForce ", "GeForce ", StringComparison.OrdinalIgnoreCase)
            .Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("AMD Radeon ", "Radeon ", StringComparison.OrdinalIgnoreCase)
            .Replace("AMD ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ")
            .Trim();
        // Prefer "GeForce RTX 3070" over bare "RTX 3070" when the driver omitted GeForce.
        if (name.StartsWith("RTX ", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("GTX ", StringComparison.OrdinalIgnoreCase))
            name = "GeForce " + name;
        if (name.Length > 40) name = name[..37].TrimEnd() + "…";
        return name;
    }

    private static string? _cachedRamBrand;
    private static int? _cachedRamModules;

    /// <summary>Brand + capacity (+ speed when known), e.g. "Corsair 32 GB · 3600 MT/s".</summary>
    public static string BuildRamLabel(ulong totalBytes)
    {
        var size = totalBytes > 0 ? FormatBytes(totalBytes) : "—";
        var brand = TryReadRamBrand();
        var mhz = TryReadMemorySpeedMhz();
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(brand))
            parts.Add(brand!);
        parts.Add(size);
        if (mhz is > 0)
            parts.Add($"{mhz} MT/s");
        return string.Join(" · ", parts);
    }

    public static string? TryReadRamBrand()
    {
        if (_cachedRamBrand is not null) return _cachedRamBrand.Length == 0 ? null : _cachedRamBrand;
        try
        {
            var t = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (t is null) { _cachedRamBrand = ""; return null; }
            dynamic locator = Activator.CreateInstance(t)!;
            dynamic services = locator.ConnectServer(".", "root\\cimv2");
            dynamic items = services.ExecQuery("SELECT Manufacturer, PartNumber FROM Win32_PhysicalMemory");
            var brands = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var n = 0;
            foreach (dynamic item in items)
            {
                n++;
                string? mfr = null;
                try { mfr = item.Manufacturer?.ToString(); } catch { }
                if (string.IsNullOrWhiteSpace(mfr) ||
                    mfr.Contains("Unknown", StringComparison.OrdinalIgnoreCase) ||
                    mfr.Contains("Not Specified", StringComparison.OrdinalIgnoreCase) ||
                    mfr.Contains("To Be Filled", StringComparison.OrdinalIgnoreCase))
                {
                    // Part numbers sometimes encode vendor (CMK… = Corsair, F4- = G.Skill).
                    try
                    {
                        var pn = item.PartNumber?.ToString()?.Trim() ?? "";
                        mfr = GuessRamBrandFromPart(pn);
                    }
                    catch { mfr = null; }
                }
                if (string.IsNullOrWhiteSpace(mfr)) continue;
                mfr = CleanRamBrand(mfr!);
                brands[mfr] = brands.GetValueOrDefault(mfr) + 1;
            }
            _cachedRamModules = n;
            if (brands.Count == 0) { _cachedRamBrand = ""; return null; }
            // Majority brand across sticks.
            _cachedRamBrand = brands.OrderByDescending(kv => kv.Value).First().Key;
            return _cachedRamBrand;
        }
        catch
        {
            _cachedRamBrand = "";
            return null;
        }
    }

    private static string? GuessRamBrandFromPart(string part)
    {
        if (string.IsNullOrWhiteSpace(part)) return null;
        var p = part.ToUpperInvariant();
        if (p.StartsWith("CMK") || p.StartsWith("CMH") || p.StartsWith("CMT") || p.StartsWith("CMV"))
            return "Corsair";
        if (p.StartsWith("F4-") || p.StartsWith("F5-") || p.Contains("GSKILL") || p.StartsWith("F3-"))
            return "G.Skill";
        if (p.StartsWith("BL") && (p.Contains("G") || p.Length > 8)) return "Crucial";
        if (p.StartsWith("KF") || p.Contains("KINGSTON") || p.StartsWith("HX")) return "Kingston";
        if (p.Contains("TEAMGROUP") || p.StartsWith("TF") || p.StartsWith("TED")) return "TeamGroup";
        if (p.Contains("PATRIOT") || p.StartsWith("PV")) return "Patriot";
        if (p.Contains("ADATA") || p.StartsWith("AX") || p.StartsWith("AD")) return "ADATA";
        if (p.Contains("SAMSUNG") || p.StartsWith("M37") || p.StartsWith("M47")) return "Samsung";
        if (p.Contains("MICRON") || p.StartsWith("MTA")) return "Micron";
        if (p.Contains("HYNIX") || p.StartsWith("HMA") || p.StartsWith("HMC")) return "SK Hynix";
        return null;
    }

    private static string CleanRamBrand(string mfr)
    {
        mfr = mfr.Trim().Trim('\0');
        // Collapse common legal suffixes.
        foreach (var noise in new[] { " Inc.", " Inc", " Co., Ltd.", " Co Ltd", " Corporation", " Corp.", " Ltd.", " Ltd", " LLC" })
        {
            if (mfr.EndsWith(noise, StringComparison.OrdinalIgnoreCase))
                mfr = mfr[..^noise.Length].Trim();
        }
        if (mfr.Contains("Corsair", StringComparison.OrdinalIgnoreCase)) return "Corsair";
        if (mfr.Contains("G.Skill", StringComparison.OrdinalIgnoreCase) || mfr.Contains("GSkill", StringComparison.OrdinalIgnoreCase))
            return "G.Skill";
        if (mfr.Contains("Kingston", StringComparison.OrdinalIgnoreCase)) return "Kingston";
        if (mfr.Contains("Crucial", StringComparison.OrdinalIgnoreCase)) return "Crucial";
        if (mfr.Contains("Samsung", StringComparison.OrdinalIgnoreCase)) return "Samsung";
        if (mfr.Contains("Micron", StringComparison.OrdinalIgnoreCase)) return "Micron";
        if (mfr.Contains("Hynix", StringComparison.OrdinalIgnoreCase)) return "SK Hynix";
        if (mfr.Length > 18) mfr = mfr[..15].TrimEnd() + "…";
        return mfr;
    }

    private static string? ReadRegistryString(
        Microsoft.Win32.RegistryKey hive,
        string subKey,
        string valueName)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }

    private static long FileTimeToInt64(FILETIME ft) =>
        ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    public static LatencySnapshot? TryReadLatency(NetworkOptimizerService network)
    {
        try
        {
            var (before, after) = network.LoadBenchmark();
            if (before is not { Ok: true } || after is not { Ok: true }) return null;
            if (before.PingP50Ms < 0 || after.PingP50Ms < 0) return null;
            return new LatencySnapshot(
                before.PingP50Ms,
                after.PingP50Ms,
                before.JitterMs,
                after.JitterMs,
                before.DnsMs,
                after.DnsMs);
        }
        catch
        {
            return null;
        }
    }

    public static NvidiaPathSnapshot? TryReadNvidiaPath()
    {
        try
        {
            var path = Path.Combine(PathHelper.AppDataDir, "nvidia-optimizer.json");
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var applied = root.TryGetProperty("profileApplied", out var pa)
                && pa.ValueKind == JsonValueKind.True;
            if (!applied) return null;

            var gsync = root.TryGetProperty("gsync", out var gs)
                && gs.ValueKind == JsonValueKind.True;
            var profileFile = root.TryGetProperty("profileFile", out var pf)
                && pf.ValueKind == JsonValueKind.String
                    ? pf.GetString()
                    : null;
            var gpuName = root.TryGetProperty("gpuName", out var gn)
                && gn.ValueKind == JsonValueKind.String ? gn.GetString() : null;
            var series = root.TryGetProperty("series", out var se)
                && se.ValueKind == JsonValueKind.String ? se.GetString() : null;
            var gameProfileCount = root.TryGetProperty("gameProfileCount", out var gc)
                && gc.TryGetInt32(out var gameCount) ? gameCount : 0;
            var verifiedSettingCount = root.TryGetProperty("drsVerifiedSettingCount", out var vc)
                && vc.TryGetInt32(out var verifiedCount) ? verifiedCount : 0;
            string? primaryMode = null;
            string? primaryConnection = null;
            string? policySource = null;
            if (root.TryGetProperty("hardwarePolicy", out var hp) && hp.ValueKind == JsonValueKind.Object)
            {
                primaryMode = hp.TryGetProperty("primaryMode", out var pm) && pm.ValueKind == JsonValueKind.String
                    ? pm.GetString() : null;
                primaryConnection = hp.TryGetProperty("primaryConnection", out var pc) && pc.ValueKind == JsonValueKind.String
                    ? pc.GetString() : null;
                policySource = hp.TryGetProperty("selectionSource", out var ps) && ps.ValueKind == JsonValueKind.String
                    ? ps.GetString() : null;
            }
            return new NvidiaPathSnapshot(
                applied,
                gsync,
                profileFile,
                gpuName,
                series,
                primaryMode,
                primaryConnection,
                policySource,
                gameProfileCount,
                verifiedSettingCount);
        }
        catch
        {
            return null;
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30)
            return $"{bytes / (double)(1L << 30):0.0} GB";
        if (bytes >= 1L << 20)
            return $"{Math.Max(0, bytes) / (double)(1L << 20):0} MB";
        if (bytes >= 1L << 10)
            return $"{Math.Max(0, bytes) / (double)(1L << 10):0} KB";
        return $"{Math.Max(0, bytes)} B";
    }

    public static string FormatBytes(ulong bytes) => FormatBytes((long)Math.Min(bytes, long.MaxValue));

    private static long ReadInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) &&
        el.ValueKind == JsonValueKind.Number &&
        el.TryGetInt64(out var value)
            ? value
            : 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FILETIME lpIdleTime,
        out FILETIME lpKernelTime,
        out FILETIME lpUserTime);
}
