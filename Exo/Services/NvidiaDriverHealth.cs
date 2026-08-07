using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Exo.Services;

/// <summary>
/// Reads the state of NVIDIA's driver installation without changing any of it.
///
/// This is the half of the cleaner that can be written honestly from here: parsing is pure,
/// the process calls are thin, and nothing it does can leave a machine without a display. It
/// exists to answer the question "does this PC need a clean sweep" while the user still has a
/// working desktop, rather than after an install has already gone wrong.
///
/// It is also the gate on <see cref="NvidiaDriverCleaner"/>. Removing driver-store packages
/// from Safe Mode is the most destructive thing Exo can do; doing it on a machine that has
/// nothing wrong with it is all risk and no benefit.
/// </summary>
internal static class NvidiaDriverHealth
{
    // Types and the verdict live in NvidiaDriverHealthLogic so they can be tested without
    // a registry. Aliased here so callers read naturally.
    // ── Reading the live machine ──────────────────────────────────────────────────────────

    /// <summary>Everything in the driver store. Empty on any failure — never a partial guess.</summary>
    public static IReadOnlyList<NvidiaDriverHealthLogic.StorePackage> ReadStore()
    {
        try
        {
            var psi = new ProcessStartInfo("pnputil.exe", "/enum-drivers")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return Array.Empty<NvidiaDriverHealthLogic.StorePackage>();
            var text = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(60_000)) { try { p.Kill(true); } catch { } return Array.Empty<NvidiaDriverHealthLogic.StorePackage>(); }
            return NvidiaDriverHealthLogic.ParseEnumDrivers(text);
        }
        catch { return Array.Empty<NvidiaDriverHealthLogic.StorePackage>(); }
    }

    /// <summary>
    /// NVIDIA services whose ImagePath is gone. A service key pointing at a binary that no
    /// longer exists is unambiguous residue, which is a stronger signal than the name alone.
    /// </summary>
    public static IReadOnlyList<string> ReadOrphanServices()
    {
        var orphans = new List<string>();
        const string root = @"SYSTEM\CurrentControlSet\Services";
        try
        {
            foreach (var name in NativeReg.GetSubKeyNames("HKLM", root))
            {
                if (!name.StartsWith("nv", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("NVDisplay", StringComparison.OrdinalIgnoreCase)) continue;

                var image = NativeReg.GetValue("HKLM", $@"{root}\{name}", "ImagePath")?.ToString();
                if (string.IsNullOrWhiteSpace(image)) continue;

                // Strip the NT prefix and any arguments before testing the path.
                var path = image.Replace("\\??\\", "").Trim('"');
                var space = path.IndexOf("\" ", StringComparison.Ordinal);
                if (space > 0) path = path[..space];
                else if (path.Contains(".exe ", StringComparison.OrdinalIgnoreCase))
                    path = path[..(path.IndexOf(".exe ", StringComparison.OrdinalIgnoreCase) + 4)];
                path = path.Trim('"');

                // Service image paths come in forms File.Exists cannot read verbatim:
                // \SystemRoot\System32\...\nvlddmkm.sys (kernel drivers), %SystemRoot%\...
                // (REG_EXPAND_SZ, read unexpanded by NativeReg), and system32\drivers\...
                // (relative to the WINDOWS directory, not System32). Every NVIDIA kernel
                // service uses one of these, so testing them verbatim flagged healthy
                // services as orphans — and this list gates the destructive sweep.
                path = Environment.ExpandEnvironmentVariables(path);
                var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                    path = Path.Combine(windir, path[@"\SystemRoot\".Length..]);
                else if (!Path.IsPathRooted(path))
                    path = Path.Combine(windir, path);

                try { if (!File.Exists(path)) orphans.Add(name); } catch { }
            }
        }
        catch { }
        return orphans;
    }

    /// <summary>CM_PROB_* on the NVIDIA display device; 0 when healthy or unreadable.</summary>
    public static int ReadDeviceProblemCode()
    {
        // The problem code lives in the PnP manager, not the registry: ConfigFlags — what
        // this used to read — is a CONFIGFLAG_* bitmask, so a device merely flagged for
        // reinstall reported "problem code 32" (gating the destructive sweep) while a
        // genuine Code 43 device, whose ConfigFlags can be 0, read as healthy. The class
        // key only locates the device; cfgmgr32 answers the question.
        const string enumPci = @"SYSTEM\CurrentControlSet\Enum\PCI";
        const string displayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
        try
        {
            foreach (var hw in NativeReg.GetSubKeyNames("HKLM", enumPci))
            {
                if (!hw.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var inst in NativeReg.GetSubKeyNames("HKLM", $@"{enumPci}\{hw}"))
                {
                    var path = $@"{enumPci}\{hw}\{inst}";
                    var cls = NativeReg.GetValue("HKLM", path, "ClassGUID")?.ToString();
                    if (!string.Equals(cls, displayClassGuid, StringComparison.OrdinalIgnoreCase)) continue;
                    if (CM_Locate_DevNodeW(out var devInst, $@"PCI\{hw}\{inst}", 0) != 0) continue;
                    if (CM_Get_DevNode_Status(out var status, out var problem, devInst, 0) != 0) continue;
                    if ((status & DN_HAS_PROBLEM) != 0 && problem != 0) return (int)problem;
                }
            }
        }
        catch { }
        return 0;
    }

    private const uint DN_HAS_PROBLEM = 0x00000400;

    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);

    /// <summary>The whole check, against this machine.</summary>
    public static NvidiaDriverHealthLogic.Report Check() => NvidiaDriverHealthLogic.Evaluate(
        ReadStore(),
        NativeLiveDetect.InstalledNvidiaDriverVersion(),
        ReadOrphanServices(),
        ReadDeviceProblemCode());
}
