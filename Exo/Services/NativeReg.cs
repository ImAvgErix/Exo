using Microsoft.Win32;

namespace Exo.Services;

/// <summary>
/// Reliable registry helpers for native apply. Never throws to callers;
/// returns success/fail so modules can report partial without aborting the pack.
/// Prefer this over PowerShell for every registry optimization.
/// </summary>
internal static class NativeReg
{
    public static RegistryKey? OpenRoot(string hive, bool writable)
    {
        var root = hive.Equals("HKLM", StringComparison.OrdinalIgnoreCase)
            ? Registry.LocalMachine
            : Registry.CurrentUser;
        return writable ? null : root;
    }

    public static RegistryKey Root(string hive) =>
        hive.Equals("HKLM", StringComparison.OrdinalIgnoreCase)
            ? Registry.LocalMachine
            : Registry.CurrentUser;

    public static bool TrySetDword(string hive, string path, string name, int value)
    {
        try
        {
            using var key = Root(hive).CreateSubKey(path, writable: true);
            if (key is null) return false;
            key.SetValue(name, value, RegistryValueKind.DWord);
            var read = key.GetValue(name);
            return read is int i && i == value
                   || read is long l && (int)l == value
                   || (read is not null && int.TryParse(read.ToString(), out var p) && p == value);
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySetString(string hive, string path, string name, string value)
    {
        try
        {
            using var key = Root(hive).CreateSubKey(path, writable: true);
            if (key is null) return false;
            key.SetValue(name, value, RegistryValueKind.String);
            return string.Equals(key.GetValue(name)?.ToString(), value, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySetExpandString(string hive, string path, string name, string value)
    {
        try
        {
            using var key = Root(hive).CreateSubKey(path, writable: true);
            if (key is null) return false;
            key.SetValue(name, value, RegistryValueKind.ExpandString);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryDeleteValue(string hive, string path, string name)
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

    public static object? GetValue(string hive, string path, string name)
    {
        try
        {
            using var key = Root(hive).OpenSubKey(path, writable: false);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch
        {
            return null;
        }
    }

    public static int? GetDword(string hive, string path, string name)
    {
        var v = GetValue(hive, path, name);
        if (v is int i) return i;
        if (v is long l) return (int)l;
        if (v is not null && int.TryParse(v.ToString(), out var p)) return p;
        return null;
    }

    public static bool MatchesDword(string hive, string path, string name, int expected)
    {
        var v = GetDword(hive, path, name);
        return v is int i && i == expected;
    }

    public static bool MatchesString(string hive, string path, string name, string expected)
    {
        var v = GetValue(hive, path, name)?.ToString();
        return string.Equals(v, expected, StringComparison.Ordinal);
    }

    public static IEnumerable<string> GetValueNames(string hive, string path)
    {
        RegistryKey? key = null;
        try
        {
            key = Root(hive).OpenSubKey(path, writable: false);
            if (key is null) yield break;
            foreach (var n in key.GetValueNames())
                yield return n;
        }
        finally
        {
            key?.Dispose();
        }
    }

    public static IEnumerable<string> GetSubKeyNames(string hive, string path)
    {
        RegistryKey? key = null;
        try
        {
            key = Root(hive).OpenSubKey(path, writable: false);
            if (key is null) yield break;
            foreach (var n in key.GetSubKeyNames())
                yield return n;
        }
        finally
        {
            key?.Dispose();
        }
    }

    public static bool IsAdministrator()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var p = new System.Security.Principal.WindowsPrincipal(id);
            return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
