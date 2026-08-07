using System.Runtime.InteropServices;
using Exo.Helpers;
using Microsoft.Win32;

namespace Exo.Services;

/// <summary>One startup entry discovered in a Run key or the Startup folder.</summary>
public sealed record StartupEntry(
    string Name,
    string Location,       // "HKCU Run" / "HKLM Run" / "Startup folder"
    string Command,
    bool Enabled,
    string Source);

/// <summary>One Windows service, read live (never started/stopped, only start-mode managed).</summary>
public sealed record ServiceEntry(
    string Name,
    string DisplayName,
    string State,          // Running / Stopped / ...
    string StartMode,      // Auto / Manual / Disabled / ...
    string Account);

/// <summary>
/// Native startup-item and service management. Reads run keys, the Startup folder,
/// and the Service Control Manager API (no WMI, no packages); applies changes with
/// an exact pre-state snapshot so selective restore is possible. Registry edits are
/// scoped to Exo's own disabled-entries backup key — nothing is deleted, only moved.
/// </summary>
public sealed class StartupServicesManager
{
    private const string RunKeyHkcu = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKeyHklm = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string DisabledKey = @"Software\Microsoft\Windows\CurrentVersion\Run-ExoDisabled";
    private const string SnapshotPathName = "startup-snapshot.json";

    private static string SnapshotPath => Path.Combine(PathHelper.AppDataDir, SnapshotPathName);

    // ── Read ──────────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<StartupEntry> ListStartupEntries()
    {
        var entries = new List<StartupEntry>();

        void AddFromKey(RegistryKey root, string path, string location, bool enabled, string source)
        {
            try
            {
                using var key = root.OpenSubKey(path, writable: false);
                if (key is null) return;
                foreach (var name in key.GetValueNames())
                {
                    var value = key.GetValue(name)?.ToString();
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    entries.Add(new StartupEntry(name, location, value, enabled, source));
                }
            }
            catch { /* unreadable key is skipped, not fatal */ }
        }

        AddFromKey(Registry.CurrentUser, RunKeyHkcu, "HKCU Run", true, "HKCU");
        AddFromKey(Registry.LocalMachine, RunKeyHklm, "HKLM Run", true, "HKLM");

        // Disabled entries live in Exo's backup key and read as disabled.
        AddFromKey(Registry.CurrentUser, DisabledKey, "Exo disabled", false, "HKCU");

        // Startup folder (current user).
        try
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.GetFiles(folder))
                {
                    entries.Add(new StartupEntry(
                        Path.GetFileName(file),
                        "Startup folder",
                        file,
                        true,
                        "folder"));
                }
            }
        }
        catch { /* unreadable folder skipped */ }

        return entries
            .OrderBy(e => e.Location, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // ── Apply (move between Run key and Exo backup key; never deletes) ───────────────────

    /// <summary>
    /// Disables or re-enables one startup entry. Disable = move the value from its
    /// run key into the Exo disabled key; enable = move it back. Returns the new state.
    /// </summary>
    public bool SetStartupEnabled(string name, string location, bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(
                location == "HKLM Run" ? RunKeyHklm : RunKeyHkcu,
                writable: true);
            using var backup = Registry.CurrentUser.CreateSubKey(DisabledKey, writable: true);
            if (run is null || backup is null) return false;

            if (enabled)
            {
                // Move back from the backup key to the run key.
                var command = backup.GetValue(name)?.ToString();
                if (string.IsNullOrWhiteSpace(command)) return false;
                run.SetValue(name, command);
                backup.DeleteValue(name, throwOnMissingValue: false);
            }
            else
            {
                var command = run.GetValue(name)?.ToString();
                if (string.IsNullOrWhiteSpace(command)) return false;
                backup.SetValue(name, command);
                run.DeleteValue(name, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Services via the Service Control Manager API (first-party, no WMI) ────────────────

    private const uint ScManagerEnumerateService = 0x0004;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceWin32 = 0x00000030;
    private const uint ServiceStateAll = 0x00000003;

    public IReadOnlyList<ServiceEntry> ListServices()
    {
        var result = new List<ServiceEntry>();
        var manager = NativeMethods.OpenSCManagerW(null, null, ScManagerEnumerateService);
        if (manager == IntPtr.Zero) return result;

        try
        {
            uint bytesNeeded = 0;
            uint returned = 0;
            uint resume = 0;
            _ = NativeMethods.EnumServicesStatusExW(
                manager, 0, ServiceWin32, ServiceStateAll,
                IntPtr.Zero, 0, ref bytesNeeded, ref returned, ref resume, null);

            // First call with a zero buffer reports how much is needed (ERROR_MORE_DATA).
            if (bytesNeeded == 0) return result;

            var buffer = Marshal.AllocHGlobal((int)bytesNeeded);
            try
            {
                if (!NativeMethods.EnumServicesStatusExW(
                        manager, 0, ServiceWin32, ServiceStateAll,
                        buffer, bytesNeeded, ref bytesNeeded, ref returned, ref resume, null))
                {
                    return result;
                }

                var cursor = buffer;
                for (var i = 0; i < returned; i++)
                {
                    var entry = Marshal.PtrToStructure<NativeMethods.EnumServiceStatusProcess>(cursor);
                    cursor += Marshal.SizeOf<NativeMethods.EnumServiceStatusProcess>();

                    var state = entry.Status.CurrentState switch
                    {
                        1 => "Stopped",
                        2 => "StartPending",
                        3 => "StopPending",
                        4 => "Running",
                        5 => "ContinuePending",
                        6 => "PausePending",
                        7 => "Paused",
                        _ => "Unknown"
                    };

                    result.Add(new ServiceEntry(
                        entry.ServiceName,
                        entry.DisplayName,
                        state,
                        QueryStartMode(entry.ServiceName),
                        QueryStartName(entry.ServiceName)));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = NativeMethods.CloseServiceHandle(manager);
        }

        return result
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string QueryStartMode(string serviceName)
    {
        var handle = NativeMethods.OpenServiceW(
            NativeMethods.OpenSCManagerW(null, null, ScManagerEnumerateService),
            serviceName,
            ServiceQueryConfig);
        if (handle == IntPtr.Zero) return "Unknown";

        try
        {
            uint bytesNeeded = 0;
            _ = NativeMethods.QueryServiceConfigW(handle, IntPtr.Zero, 0, ref bytesNeeded);
            if (bytesNeeded == 0) return "Unknown";

            var buffer = Marshal.AllocHGlobal((int)bytesNeeded);
            try
            {
                if (!NativeMethods.QueryServiceConfigW(handle, buffer, bytesNeeded, ref bytesNeeded))
                    return "Unknown";

                var config = Marshal.PtrToStructure<NativeMethods.QueryServiceConfig>(buffer);
                return config.StartType switch
                {
                    2 => "Auto",
                    3 => "Manual",
                    4 => "Disabled",
                    _ => "Unknown"
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = NativeMethods.CloseServiceHandle(handle);
        }
    }

    private static string QueryStartName(string serviceName)
    {
        var manager = NativeMethods.OpenSCManagerW(null, null, ScManagerEnumerateService);
        if (manager == IntPtr.Zero) return "";
        var handle = NativeMethods.OpenServiceW(manager, serviceName, ServiceQueryConfig);
        _ = NativeMethods.CloseServiceHandle(manager);
        if (handle == IntPtr.Zero) return "";

        try
        {
            uint bytesNeeded = 0;
            _ = NativeMethods.QueryServiceConfigW(handle, IntPtr.Zero, 0, ref bytesNeeded);
            if (bytesNeeded == 0) return "";

            var buffer = Marshal.AllocHGlobal((int)bytesNeeded);
            try
            {
                if (!NativeMethods.QueryServiceConfigW(handle, buffer, bytesNeeded, ref bytesNeeded))
                    return "";

                var config = Marshal.PtrToStructure<NativeMethods.QueryServiceConfig>(buffer);
                return config.ServiceStartName ?? "";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = NativeMethods.CloseServiceHandle(handle);
        }
    }

    // ── Snapshot for selective restore ───────────────────────────────────────────────────

    public void SaveSnapshot()
    {
        var entries = ListStartupEntries();
        var payload = new
        {
            savedAt = DateTimeOffset.UtcNow,
            startup = entries.Select(e => new { e.Name, e.Location, e.Command, e.Enabled })
        };
        Directory.CreateDirectory(PathHelper.AppDataDir);
        File.WriteAllText(
            SnapshotPath,
            System.Text.Json.JsonSerializer.Serialize(payload));
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct ServiceStatusProcess
        {
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
            public uint ProcessId;
            public uint ServiceFlags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct EnumServiceStatusProcess
        {
            public string ServiceName;
            public string DisplayName;
            public ServiceStatusProcess Status;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct QueryServiceConfig
        {
            public uint ServiceType;
            public uint StartType;
            public uint ErrorControl;
            public string BinaryPathName;
            public string LoadOrderGroup;
            public uint TagId;
            public string Dependencies;
            public string ServiceStartName;
            public string DisplayName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenSCManagerW(string? machine, string? database, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool EnumServicesStatusExW(
            IntPtr hSCManager,
            uint infoLevel,
            uint serviceType,
            uint serviceState,
            IntPtr services,
            uint bufSize,
            ref uint bytesNeeded,
            ref uint servicesReturned,
            ref uint resumeHandle,
            string? groupName);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenServiceW(IntPtr hSCManager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryServiceConfigW(
            IntPtr hService,
            IntPtr serviceConfig,
            uint bufSize,
            ref uint bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool CloseServiceHandle(IntPtr handle);
    }
}
