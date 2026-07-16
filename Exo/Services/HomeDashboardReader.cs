using System.Runtime.InteropServices;
using System.Text.Json;
using Exo.Helpers;

namespace Exo.Services;

/// <summary>
/// Defensive home-dashboard reads. File-backed reclaim / latency / NVIDIA path only —
/// never invents FPS or frame-time totals. Live memory uses GlobalMemoryStatusEx on
/// Windows; returns null elsewhere.
/// </summary>
public static class HomeDashboardReader
{
    public sealed record TrimSnapshot(
        long TotalBytes,
        long Last24hBytes,
        long Passes,
        IReadOnlyList<long> HourlyBytes);

    public sealed record MemorySnapshot(
        ulong TotalBytes,
        ulong AvailableBytes,
        uint LoadPercent);

    public sealed record LatencySnapshot(
        double BeforeP50Ms,
        double AfterP50Ms);

    public sealed record NvidiaPathSnapshot(
        bool ProfileApplied,
        bool Gsync,
        string? ProfileFile);

    public static TrimSnapshot? TryReadTrimStats()
    {
        try
        {
            // Prefer live trim helper stats; fall back to Steam Apply cache free.
            var path = Path.Combine(PathHelper.AppDataDir, "steam-trim-stats.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                var total = ReadInt64(root, "totalReclaimedBytes");
                var last24h = ReadInt64(root, "last24hReclaimedBytes");
                var passes = ReadInt64(root, "totalTrimPasses");
                if (total > 0 || last24h > 0 || passes > 0)
                {
                    var hourly = new List<(DateTime Utc, long Bytes)>();
                    if (root.TryGetProperty("hourly", out var h) && h.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in h.EnumerateObject())
                        {
                            if (!DateTime.TryParseExact(
                                    prop.Name,
                                    "yyyy-MM-ddTHH",
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.AssumeUniversal
                                    | System.Globalization.DateTimeStyles.AdjustToUniversal,
                                    out var t))
                                continue;
                            var bytes = prop.Value.ValueKind == JsonValueKind.Number
                                && prop.Value.TryGetInt64(out var v)
                                    ? v
                                    : 0L;
                            if (bytes > 0) hourly.Add((t, bytes));
                        }
                    }

                    hourly.Sort((a, b) => a.Utc.CompareTo(b.Utc));
                    var series = hourly.TakeLast(24).Select(x => x.Bytes).ToList();
                    return new TrimSnapshot(total, last24h, passes, series);
                }
            }

            // Steam Apply writes cacheFreedBytes even before the webhelper has run long.
            var steamPath = Path.Combine(PathHelper.AppDataDir, "steam-optimizer.json");
            if (!File.Exists(steamPath)) return null;
            using var steamDoc = JsonDocument.Parse(File.ReadAllText(steamPath));
            var steam = steamDoc.RootElement;
            var freed = ReadInt64(steam, "cacheFreedBytes");
            if (freed <= 0) freed = ReadInt64(steam, "shaderCacheFreedBytes");
            var applied = steam.TryGetProperty("applied", out var ap) && ap.ValueKind == JsonValueKind.True;
            if (freed <= 0 && !applied) return null;
            return new TrimSnapshot(Math.Max(0, freed), 0, applied ? 1 : 0, Array.Empty<long>());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Internet apply state for home when no before/after benchmark exists yet.</summary>
    public static string? TryReadInternetStatus()
    {
        try
        {
            var path = Path.Combine(PathHelper.AppDataDir, "network-optimizer.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("lastPreset", out var p) && p.ValueKind == JsonValueKind.String)
            {
                var preset = p.GetString();
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

    /// <summary>Discord apply marker for home honesty (no live detect probe).</summary>
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
            return new LatencySnapshot(before.PingP50Ms, after.PingP50Ms);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// NVIDIA pack status from state file only (no Detect probe).
    /// FPS / frame-time capture is not shipped yet — UI keeps those as empty.
    /// </summary>
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
            return new NvidiaPathSnapshot(applied, gsync, profileFile);
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
