using System.Runtime.InteropServices;

namespace Exo.Helpers;

/// <summary>Applies process-wide loader hardening before WinUI loads.</summary>
public static class NativeProcessSecurity
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string? pathName);

    public static void HardenDllSearch()
    {
        try
        {
            // Removing the current directory closes the unsafe DLL-search location
            // without replacing Windows App SDK's package-graph search policy. A
            // blanket SetDefaultDllDirectories call breaks unpackaged WinUI 3 on
            // supported Windows builds because its bootstrap adds runtime paths.
            _ = SetDllDirectory(string.Empty);
        }
        catch
        {
            // Windows 11 supports these APIs. Diagnostics record the next phase if
            // a policy shim blocks them; loader hardening must not create a boot loop.
        }
    }
}
