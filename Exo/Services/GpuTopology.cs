using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Exo.Services;

/// <summary>
/// Shared GPU-topology facts and the one routing policy every module uses when
/// stamping <c>HKCU\Software\Microsoft\DirectX\UserGpuPreferences</c>.
///
/// Why this exists: Steam and Brave used to disagree. Steam routed its Chromium
/// UI to the integrated GPU on hybrid machines (correct — it keeps the discrete
/// GPU free for the game) while Brave unconditionally stamped high-performance,
/// which is the opposite call for the same class of process and is a no-op stamp
/// on the single-GPU desktops most users have. One helper, one policy.
///
/// UserGpuPreferences only means anything when Windows has more than one adapter
/// to choose between. On a single-GPU box the value is inert, so the policy is to
/// clear it rather than leave a stamp that implies an effect it cannot have.
/// </summary>
internal static partial class GpuTopology
{
    public const string PreferIntegrated = "GpuPreference=1;";
    public const string PreferHighPerformance = "GpuPreference=2;";

    private const string GpuPrefPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string DisplayClassPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex AdapterIndexRegex();

    /// <summary>
    /// True when the machine exposes both a discrete and an integrated adapter —
    /// the only case where a GPU preference changes which chip actually runs a process.
    /// </summary>
    public static bool IsHybrid()
    {
        try
        {
            // One classifier for the whole app. This used to carry its own copy of the
            // discrete/integrated regexes, and it disagreed with the two copies inside the
            // Steam kit - each had a vendor the others were missing. HardwareInventory is now
            // the single definition; keep it in step with Test-SteamHybridGpu in
            // SteamDetectCore.ps1, which is the standalone-kit mirror.
            return HardwareInventory.Read().HybridGpu;
        }
        catch
        {
            // Unknown topology: treat as single-GPU so we clear rather than stamp.
            return false;
        }
    }

    /// <summary>Physical display adapters, minus the software/virtual ones Windows also registers.</summary>
    public static List<string> AdapterDescriptions()
    {
        var names = new List<string>();
        using var classKey = Registry.LocalMachine.OpenSubKey(DisplayClassPath);
        if (classKey is null) return names;

        foreach (var sub in classKey.GetSubKeyNames())
        {
            if (!AdapterIndexRegex().IsMatch(sub)) continue;
            using var adapter = classKey.OpenSubKey(sub);
            var driver = adapter?.GetValue("DriverDesc")?.ToString()
                         ?? adapter?.GetValue("Device Description")?.ToString()
                         ?? "";
            if (string.IsNullOrWhiteSpace(driver)) continue;
            if (driver.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase)) continue;
            if (driver.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)) continue;
            if (driver.Contains("Remote", StringComparison.OrdinalIgnoreCase)) continue;
            names.Add(driver);
        }
        return names;
    }

    /// <summary>
    /// Existing preference strings for the given executables, so Repair can put
    /// back exactly what was there — including "was not set at all" (null value).
    /// </summary>
    public static Dictionary<string, string?> SnapshotPreferences(IEnumerable<string> exePaths)
    {
        var snap = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(GpuPrefPath);
            foreach (var exe in exePaths)
            {
                if (string.IsNullOrWhiteSpace(exe)) continue;
                snap[exe] = key?.GetValue(exe)?.ToString();
            }
        }
        catch { /* unreadable: an empty snapshot is honest, a fabricated one is not */ }
        return snap;
    }

    /// <summary>
    /// Restore a snapshot taken by <see cref="SnapshotPreferences"/>. A null recorded
    /// value means the key did not exist before Exo, so it is deleted, not zeroed.
    /// </summary>
    public static int RestorePreferences(IReadOnlyDictionary<string, string?> snapshot)
    {
        var restored = 0;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(GpuPrefPath, writable: true);
            if (key is null) return 0;
            foreach (var (exe, before) in snapshot)
            {
                try
                {
                    if (before is null) key.DeleteValue(exe, throwOnMissingValue: false);
                    else key.SetValue(exe, before, RegistryValueKind.String);
                    restored++;
                }
                catch { /* one locked entry must not abandon the rest */ }
            }
        }
        catch { }
        return restored;
    }

    /// <summary>
    /// Route background Chromium/CEF UI processes (Steam's webhelper, Brave) off the
    /// discrete GPU on hybrid machines, and clear the stamp entirely on single-GPU
    /// machines where it cannot do anything.
    /// </summary>
    /// <returns>How many paths were stamped, and how many stale stamps were cleared.</returns>
    public static (int Stamped, int Cleared) RouteBrowserUi(IEnumerable<string> exePaths, bool hybrid)
    {
        var stamped = 0;
        var cleared = 0;
        using var key = Registry.CurrentUser.CreateSubKey(GpuPrefPath, writable: true);
        if (key is null) return (0, 0);

        foreach (var exe in exePaths)
        {
            if (string.IsNullOrWhiteSpace(exe)) continue;
            try
            {
                if (hybrid)
                {
                    key.SetValue(exe, PreferIntegrated, RegistryValueKind.String);
                    stamped++;
                }
                else if (key.GetValue(exe) is not null)
                {
                    key.DeleteValue(exe, throwOnMissingValue: false);
                    cleared++;
                }
            }
            catch { }
        }
        return (stamped, cleared);
    }

    /// <summary>
    /// Route a game executable to the discrete GPU on hybrid machines, and clear the stamp on
    /// single-GPU machines where it cannot do anything.
    ///
    /// The mirror image of <see cref="RouteBrowserUi"/>, and deliberately a separate method:
    /// routing games through that one would pin them to the *integrated* GPU, which is the
    /// right call for a background Chromium UI and exactly the wrong one for a game.
    ///
    /// This exists because the two paths that stamp the most values — Steam's library sweep in
    /// both its native and PowerShell forms — bypassed this class entirely and wrote
    /// GpuPreference=2 unconditionally. On a single-GPU desktop that is an inert stamp implying
    /// an effect it cannot have, which is the precise thing the type comment above says the
    /// policy exists to prevent: a machine here carries 51 of them with one adapter installed.
    /// </summary>
    /// <returns>How many paths were stamped, and how many stale stamps were cleared.</returns>
    public static (int Stamped, int Cleared) RouteGameHighPerf(IEnumerable<string> exePaths, bool hybrid)
    {
        var stamped = 0;
        var cleared = 0;
        using var key = Registry.CurrentUser.CreateSubKey(GpuPrefPath, writable: true);
        if (key is null) return (0, 0);

        foreach (var exe in exePaths)
        {
            if (string.IsNullOrWhiteSpace(exe)) continue;
            try
            {
                if (hybrid)
                {
                    key.SetValue(exe, PreferHighPerformance, RegistryValueKind.String);
                    stamped++;
                }
                else if (key.GetValue(exe) is not null)
                {
                    key.DeleteValue(exe, throwOnMissingValue: false);
                    cleared++;
                }
            }
            catch { }
        }
        return (stamped, cleared);
    }
}
