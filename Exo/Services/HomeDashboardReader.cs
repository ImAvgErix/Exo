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
        int GameProfileCount,
        int VerifiedSettingCount);

    /// <summary>
    /// Discord live WS + session peak. "Reclaimed" = peak − live when DiscOpt/kernel
    /// has trimmed idle pages (honest estimate; no invented FPS).
    /// </summary>
    public sealed record DiscordRamSnapshot(
        long LiveBytes,
        long PeakBytes,
        long ReclaimedBytes);

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

    /// <summary>True only while the optimized Steam launcher memory guard is running.</summary>
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
    /// show RAM reclaimed when idle trim drops the live set below peak.
    /// </summary>
    public static DiscordRamSnapshot? TrySampleDiscordRam()
    {
        try
        {
            var live = TryReadProcessWorkingSetBytes("Discord", "DiscordPTB", "DiscordCanary");
            var path = Path.Combine(PathHelper.AppDataDir, "discord-ram-stats.json");
            long peak = 0;
            long sessionReclaimed = 0;

            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                peak = ReadInt64(root, "peakWorkingSetBytes");
                sessionReclaimed = ReadInt64(root, "sessionReclaimedBytes");
            }

            if (live <= 0)
            {
                // Process gone — keep peak/reclaimed for display until next open.
                if (peak <= 0 && sessionReclaimed <= 0) return null;
                return new DiscordRamSnapshot(0, peak, Math.Max(0, sessionReclaimed));
            }

            if (live > peak)
                peak = live;

            // When WS drops below peak, credit the drop as reclaimed (idle trim / GC).
            var drop = peak - live;
            if (drop > sessionReclaimed)
                sessionReclaimed = drop;

            try
            {
                Directory.CreateDirectory(PathHelper.AppDataDir);
                var json =
                    "{\n" +
                    $"  \"peakWorkingSetBytes\": {peak},\n" +
                    $"  \"liveWorkingSetBytes\": {live},\n" +
                    $"  \"sessionReclaimedBytes\": {sessionReclaimed},\n" +
                    $"  \"updatedUtc\": \"{DateTime.UtcNow:O}\"\n" +
                    "}\n";
                File.WriteAllText(path, json);
            }
            catch { /* non-fatal */ }

            return new DiscordRamSnapshot(live, peak, sessionReclaimed);
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
                return step.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
                    ? reason.GetString()
                    : null;
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
            return new NvidiaPathSnapshot(
                applied,
                gsync,
                profileFile,
                gpuName,
                series,
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

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
