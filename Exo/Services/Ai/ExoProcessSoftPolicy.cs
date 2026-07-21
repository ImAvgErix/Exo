using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Exo.Services.Ai;

/// <summary>
/// Soft process policy for Exo-owned helpers: EcoQoS (power throttling) + soft working-set reclaim.
/// Never EmptyWorkingSet. Never touch anti-cheat / game shipping processes.
/// Soft SetProcessWorkingSetSize(-1,-1) only on non-foreground CEF/helpers.
/// </summary>
internal static class ExoProcessSoftPolicy
{
    private const uint ProcessMemoryPriority = 0;
    private const uint ProcessPowerThrottling = 4;
    private const uint ProcessSetInformation = 0x0200;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessSetQuota = 0x0100;

    /// <summary>Exo-owned launcher / CEF helper names (not game anti-cheat).</summary>
    public static readonly string[] OwnedHelperNames =
    [
        "steamwebhelper",
        "steamerrorreporter",
        "steamerrorreporter64",
        "streaming_client",
        "steam_monitor",
        "Discord",
        "DiscordPTB",
        "DiscordCanary",
        "EpicGamesLauncher",
        "EpicWebHelper",
        "RiotClientServices",
        "RiotClientUx",
        "RiotClientCrashHandler"
    ];

    public static readonly string[] AntiCheatNeverTouch =
    [
        "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "BEDaisy", "BattlEye",
        "vgk", "vgc", "FACEIT", "FaceItService", "GameGuard", "XIGNCODE", "xigncode"
    ];

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_PRIORITY_INFORMATION
    {
        public uint MemoryPriority;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr process, uint infoClass, ref MEMORY_PRIORITY_INFORMATION info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr process, uint infoClass, ref PROCESS_POWER_THROTTLING_STATE info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);

    private static int ForegroundPid()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return (int)pid;
    }

    private static bool IsAntiCheatProcess(Process p)
    {
        try
        {
            var name = p.ProcessName;
            if (AntiCheatNeverTouch.Any(m =>
                    name.Contains(m, StringComparison.OrdinalIgnoreCase)))
                return true;
            var path = p.MainModule?.FileName;
            if (path is null) return false;
            return AntiCheatNeverTouch.Any(m =>
                path.Contains(m, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// EcoQoS + very-low memory priority on non-foreground owned helpers. Returns count touched.
    /// </summary>
    public static int ApplyEcoQosToOwnedHelpers()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var fg = ForegroundPid();
        var n = 0;
        foreach (var name in OwnedHelperNames)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                try
                {
                    if (p.Id == fg || IsAntiCheatProcess(p)) continue;
                    if (SetMemoryPriority(p.Id, 1)) n++;
                    if (SetPowerThrottled(p.Id, enabled: true)) n++;
                }
                catch { /* ignore per-process */ }
                finally { p.Dispose(); }
            }
        }

        return n;
    }

    /// <summary>
    /// Soft reclaim SetProcessWorkingSetSize(-1,-1) on non-foreground owned helpers.
    /// Never EmptyWorkingSet. Returns count touched.
    /// </summary>
    public static int SoftReclaimOwnedHelpers()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var fg = ForegroundPid();
        var n = 0;
        foreach (var name in OwnedHelperNames)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                try
                {
                    if (p.Id == fg || IsAntiCheatProcess(p)) continue;
                    if (SoftReclaimWorkingSet(p.Id)) n++;
                }
                catch { /* ignore */ }
                finally { p.Dispose(); }
            }
        }

        return n;
    }

    private static bool SetMemoryPriority(int pid, uint priority)
    {
        var handle = OpenProcess(ProcessSetInformation | ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var info = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = priority };
            return SetProcessInformation(handle, ProcessMemoryPriority, ref info, 4);
        }
        finally { CloseHandle(handle); }
    }

    private static bool SetPowerThrottled(int pid, bool enabled)
    {
        var handle = OpenProcess(ProcessSetInformation | ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var info = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1,
                ControlMask = 1,
                StateMask = enabled ? 1u : 0u
            };
            return SetProcessInformation(handle, ProcessPowerThrottling, ref info, 12);
        }
        finally { CloseHandle(handle); }
    }

    private static bool SoftReclaimWorkingSet(int pid)
    {
        var handle = OpenProcess(ProcessSetQuota | ProcessSetInformation, false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            return SetProcessWorkingSetSize(handle, new IntPtr(-1), new IntPtr(-1));
        }
        finally { CloseHandle(handle); }
    }
}
