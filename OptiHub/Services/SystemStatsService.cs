using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OptiHub.Services;

public sealed class SystemStatsSnapshot
{
    public string MachineName { get; init; } = "—";
    public string OsName { get; init; } = "—";
    public string Uptime { get; init; } = "—";
    public string CpuName { get; init; } = "—";
    public string GpuName { get; init; } = "—";
    public string RamLine { get; init; } = "—";
    public double RamPercent { get; init; }
    public string NetworkLine { get; init; } = "—";
    public string LinkSpeed { get; init; } = "—";
    public string ProviderLine { get; init; } = "—";
    public string LatencyLine { get; init; } = "—";
    public string AppVersion { get; init; } = "—";
    public int OptimizersReady { get; init; }
    public int OptimizersSoon { get; init; }
    public string NetworkPreset { get; init; } = "—";
    public bool ProbeOk { get; init; } = true;
    public string Detail { get; init; } = string.Empty;
}

/// <summary>Lightweight host snapshot for the Home dashboard.</summary>
public sealed class SystemStatsService
{
    public async Task<SystemStatsSnapshot> CollectAsync(
        NetworkOptimizerService network,
        int readyCount,
        int soonCount,
        CancellationToken ct = default)
    {
        var machine = Environment.MachineName;
        var os = RuntimeInformation.OSDescription;
        try
        {
            // Cleaner Windows product name when available.
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string;
            var display = key?.GetValue("DisplayVersion") as string;
            if (!string.IsNullOrWhiteSpace(product))
                os = string.IsNullOrWhiteSpace(display) ? product : $"{product} ({display})";
        }
        catch { }

        var uptime = FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64));
        var cpu = ReadCpuName();
        var gpu = ReadGpuName();
        var (ramLine, ramPct) = ReadRam();

        string netLine = "—", link = "—", provider = "—", latency = "—", preset = "—";
        var ok = true;
        var detail = string.Empty;
        try
        {
            var snap = await network.ProbeAsync(ct).ConfigureAwait(false);
            ok = snap.ProbeOk;
            detail = snap.Detail;
            netLine = $"{snap.ConnectionType} · {snap.AdapterDescription}";
            link = snap.LinkSpeed;
            provider = snap.Provider is "—" or ""
                ? snap.PublicIp
                : $"{snap.Provider}";
            if (snap.Area is not "—" and not "")
                provider = $"{provider} · {snap.Area}";
            latency = $"GW {FmtMs(snap.GatewayPingMs)} · Net {FmtMs(snap.InternetPingMs)}";
            preset = snap.ActivePreset switch
            {
                Models.NetworkPreset.LowestLatency => "Lowest latency",
                Models.NetworkPreset.HighestThroughput => "Highest download",
                _ => "Default"
            };
        }
        catch (Exception ex)
        {
            ok = false;
            detail = ex.Message;
        }

        var ver = typeof(SystemStatsService).Assembly.GetName().Version;
        var appVer = ver is null ? "—" : $"{ver.Major}.{ver.Minor}.{ver.Build}";

        return new SystemStatsSnapshot
        {
            MachineName = machine,
            OsName = os,
            Uptime = uptime,
            CpuName = cpu,
            GpuName = gpu,
            RamLine = ramLine,
            RamPercent = ramPct,
            NetworkLine = netLine,
            LinkSpeed = link,
            ProviderLine = provider,
            LatencyLine = latency,
            AppVersion = appVer,
            OptimizersReady = readyCount,
            OptimizersSoon = soonCount,
            NetworkPreset = preset,
            ProbeOk = ok,
            Detail = detail
        };
    }

    private static string FmtMs(int? ms) => ms is int v ? $"{v} ms" : "—";

    private static string FormatUptime(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{t.Minutes}m {t.Seconds}s";
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = key?.GetValue("ProcessorNameString") as string;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        catch { }

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "—";
    }

    private static string ReadGpuName()
    {
        // Enumerate display adapters via setup class GUID without WMI.
        try
        {
            const string classPath =
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using var root = Registry.LocalMachine.OpenSubKey(classPath);
            if (root is null) return "—";

            var names = new List<string>();
            foreach (var sub in root.GetSubKeyNames())
            {
                if (!char.IsDigit(sub[0])) continue;
                using var k = root.OpenSubKey(sub);
                var desc = k?.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(desc)) continue;
                if (desc.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase)) continue;
                if (desc.Contains("Remote Desktop", StringComparison.OrdinalIgnoreCase)) continue;
                names.Add(desc.Trim());
            }

            if (names.Count == 0) return "—";
            var preferred = names.FirstOrDefault(n =>
                n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("RX ", StringComparison.OrdinalIgnoreCase));
            return preferred ?? names[0];
        }
        catch { }

        return "—";
    }

    private static (string Line, double Percent) ReadRam()
    {
        try
        {
            var status = new MEMORYSTATUSEX
            {
                dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
            };
            if (GlobalMemoryStatusEx(ref status))
            {
                var total = status.ullTotalPhys;
                var avail = status.ullAvailPhys;
                var used = total > avail ? total - avail : 0;
                var pct = total == 0 ? 0 : used * 100.0 / total;
                return ($"{FormatBytes(used)} / {FormatBytes(total)}", pct);
            }
        }
        catch { }

        return ("—", 0);
    }

    private static string FormatBytes(ulong bytes)
    {
        double v = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }

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
