using Microsoft.Win32;

namespace Exo.Engine;

/// <summary>
/// Narrow registry surface the catalog adapters need. Production uses
/// <see cref="WindowsRegistryValueStore"/>; smoke tests substitute an in-memory
/// store so the full tweak lifecycle can be exercised without touching a machine.
/// </summary>
public interface IRegistryValueStore
{
    int? GetDword(string hive, string path, string name);

    bool TrySetDword(string hive, string path, string name, int value);

    bool TryDeleteValue(string hive, string path, string name);
}

/// <summary>
/// Production registry store. Never throws to callers; returns null/false like the
/// native apply helpers so a tweak reports a failed step instead of aborting a pack.
/// </summary>
public sealed class WindowsRegistryValueStore : IRegistryValueStore
{
    private static RegistryKey Root(string hive) =>
        hive.Equals("HKLM", StringComparison.OrdinalIgnoreCase)
            ? Registry.LocalMachine
            : Registry.CurrentUser;

    public int? GetDword(string hive, string path, string name)
    {
        try
        {
            using var key = Root(hive).OpenSubKey(path, writable: false);
            var value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is int i) return i;
            if (value is long l) return (int)l;
            if (value is not null && int.TryParse(value.ToString(), out var parsed)) return parsed;
            return null;
        }
        catch
        {
            return null;
        }
    }

    public bool TrySetDword(string hive, string path, string name, int value)
    {
        try
        {
            using var key = Root(hive).CreateSubKey(path, writable: true);
            if (key is null) return false;
            key.SetValue(name, value, RegistryValueKind.DWord);
            var read = key.GetValue(name);
            return read is int i && i == value
                   || read is long l && (int)l == value
                   || read is not null && int.TryParse(read.ToString(), out var p) && p == value;
        }
        catch
        {
            return false;
        }
    }

    public bool TryDeleteValue(string hive, string path, string name)
    {
        try
        {
            using var key = Root(hive).OpenSubKey(path, writable: true);
            if (key is null) return true;
            try { key.DeleteValue(name, throwOnMissingValue: false); } catch { /* ok */ }
            return key.GetValue(name) is null;
        }
        catch
        {
            return false;
        }
    }
}
