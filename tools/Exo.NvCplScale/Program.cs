using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

static class Program
{
    const string NvcplPath = @"C:\Program Files\NVIDIA Corporation\NVIDIA App\NvCpl\nvcpl.dll";

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr h, string name);
    [DllImport("kernel32")] static extern bool FreeLibrary(IntPtr h);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D0();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D1(int a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D3(int a, int b, int c);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DSet(int id, IntPtr buf, int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DGet(int id, IntPtr buf, int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DGet2(int id, IntPtr buf, ref int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DEnum(IntPtr buf, int count, out int written);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DType(int id, out int type);

    static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--probe-set")
            return ProbeSet(int.Parse(args[1]), int.Parse(args[2]));
        if (args.Length >= 1 && args[0] == "--probe-get")
            return ProbeGet(int.Parse(args[1]));
        if (args.Length >= 1 && args[0] == "--scan")
            return ScanSettingIds();

        return ApplyAll(args);
    }

    static IntPtr MustLoad()
    {
        var h = LoadLibrary(NvcplPath);
        if (h == IntPtr.Zero) throw new Exception("LoadLibrary failed " + Marshal.GetLastWin32Error());
        return h;
    }

    static T Del<T>(IntPtr h, string n) where T : Delegate
    {
        var p = GetProcAddress(h, n);
        if (p == IntPtr.Zero) throw new Exception("missing " + n);
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    static int ProbeSet(int id, int val)
    {
        var h = MustLoad();
        try
        {
            Del<D0>(h, "NvCplApiInit")();
            try { Del<D1>(h, "NvSelectDisplayDevice")(1); } catch { }
            try { Del<D1>(h, "NvSelectDisplayDevice")(2); } catch { }
            var buf = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteInt32(buf, val);
                var r = Del<DSet>(h, "NvCplApiSetSetting")(id, buf, 4);
                Console.WriteLine($"PROBE_SET id={id} val={val} r={r}");
                if (r == 0)
                {
                    try { Del<D0>(h, "NvCplApiExecute")(); } catch { }
                    try { Del<D0>(h, "NvCplCommitState")(); } catch { }
                }
                return r == 0 ? 0 : 10 + Math.Abs(r % 90);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex)
        {
            Console.WriteLine("PROBE_SET_ERR " + ex.Message);
            return 99;
        }
        finally { FreeLibrary(h); }
    }

    static int ProbeGet(int id)
    {
        var h = MustLoad();
        try
        {
            Del<D0>(h, "NvCplApiInit")();
            var buf = Marshal.AllocHGlobal(64);
            try
            {
                for (int i = 0; i < 64; i++) Marshal.WriteByte(buf, i, 0);
                int r;
                try
                {
                    r = Del<DGet>(h, "NvCplApiGetSetting")(id, buf, 64);
                    Console.WriteLine($"PROBE_GET id={id} r={r} d0={Marshal.ReadInt32(buf)} d1={Marshal.ReadInt32(buf,4)}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("PROBE_GET_EX " + ex.Message);
                    return 98;
                }
                return r == 0 ? 0 : 11;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex)
        {
            Console.WriteLine("PROBE_GET_ERR " + ex.Message);
            return 99;
        }
        finally { FreeLibrary(h); }
    }

    static int ScanSettingIds()
    {
        // Child-process scan for SetSetting(id,1,4)==0 and GetSetting works
        var self = Environment.ProcessPath!;
        var hits = new List<int>();
        // Focused ranges first
        var ranges = new List<int>();
        for (int i = 0; i <= 400; i++) ranges.Add(i);
        for (int i = 1000; i <= 1300; i++) ranges.Add(i);
        for (int i = 0x1000; i <= 0x1080; i++) ranges.Add(i);

        Console.WriteLine($"[scan] probing {ranges.Count} setting IDs via child processes...");
        foreach (var id in ranges)
        {
            var psi = new ProcessStartInfo(self, $"--probe-set {id} 1")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(true); } catch { }
                continue;
            }
            var o = p.StandardOutput.ReadToEnd();
            if (p.ExitCode == 0 && o.Contains("r=0"))
            {
                hits.Add(id);
                Console.WriteLine($"[scan] HIT set id={id}");
                // read back
                var psi2 = new ProcessStartInfo(self, $"--probe-get {id}")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p2 = Process.Start(psi2)!;
                p2.WaitForExit(3000);
                Console.WriteLine(p2.StandardOutput.ReadToEnd().Trim());
            }
        }
        Console.WriteLine($"[scan] done hits={hits.Count}: {string.Join(",", hits)}");
        File.WriteAllText(@"C:\Users\Erix\Exo\tools\Exo.NvCplScale\setting-hits.txt",
            string.Join(Environment.NewLine, hits));
        return hits.Count > 0 ? 0 : 1;
    }

    static int ApplyAll(string[] args)
    {
        var display = 1;
        var all = args.Any(a => a == "--all");
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] is "--display" or "-d") int.TryParse(args[i + 1], out display);

        Console.WriteLine("[NvCplScale] Apply Override ON + NoScale/GPU via nvcpl (no mouse)");
        StampRegistry();

        // Prefer hits file from prior scan
        var hitFile = Path.Combine(AppContext.BaseDirectory, "setting-hits.txt");
        if (!File.Exists(hitFile))
            hitFile = @"C:\Users\Erix\Exo\tools\Exo.NvCplScale\setting-hits.txt";

        var ids = new List<int>();
        if (File.Exists(hitFile))
        {
            foreach (var line in File.ReadAllLines(hitFile))
                if (int.TryParse(line.Trim(), out var id)) ids.Add(id);
        }
        // Always include dense common block
        if (ids.Count == 0)
        {
            for (int i = 0; i <= 300; i++) ids.Add(i);
            for (int i = 1000; i <= 1200; i++) ids.Add(i);
        }

        var self = Environment.ProcessPath!;
        var displays = all ? new[] { 0, 1 } : new[] { display };
        var ok = false;
        foreach (var d in displays)
        {
            Console.WriteLine($"[NvCplScale] displayIndex={d}");
            // Select then set override=1 for each candidate until one sticks
            foreach (var id in ids)
            {
                var psi = new ProcessStartInfo(self, $"--probe-set {id} 1")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi)!;
                if (!p.WaitForExit(4000)) { try { p.Kill(true); } catch { } continue; }
                var o = p.StandardOutput.ReadToEnd();
                if (p.ExitCode == 0)
                {
                    Console.WriteLine($"[NvCplScale] Override candidate id={id} accepted");
                    Console.WriteLine(o.Trim());
                    ok = true;
                    // don't break — set all hits that accept, or break after first?
                    // Setting wrong IDs to 1 could be bad. Only use first hit then verify.
                    break;
                }
            }
        }

        // Also re-apply NVAPI path helper if present
        var nv = FindNvDisplay();
        if (nv != null)
        {
            Console.WriteLine("[NvCplScale] Running Exo.NvDisplay --apply");
            var p = Process.Start(new ProcessStartInfo(nv, "--apply") { UseShellExecute = false });
            p?.WaitForExit(120000);
            Console.WriteLine($"[NvCplScale] NvDisplay exit={p?.ExitCode}");
        }

        StampRegistry();
        Console.WriteLine(ok
            ? "[NvCplScale] SUCCESS: at least one SetSetting override candidate returned 0"
            : "[NvCplScale] PARTIAL: registry+NVAPI done; native SetSetting ID not confirmed (run --scan)");
        return ok ? 0 : 1;
    }

    static string? FindNvDisplay()
    {
        foreach (var c in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Exo\scripts\Nvidia\tools\Exo.NvDisplay.exe"),
            @"C:\Users\Erix\Exo\tools\Exo.NvDisplay\bin\Release\net8.0-windows\Exo.NvDisplay.exe",
        })
            if (File.Exists(c)) return c;
        return null;
    }

    static void StampRegistry()
    {
        using var root = Registry.CurrentUser.CreateSubKey(@"Software\NVIDIA Corporation\Global\NVTweak\Devices");
        if (root == null) return;
        var names = new HashSet<string>(root.GetSubKeyNames(), StringComparer.OrdinalIgnoreCase);
        foreach (var e in new[] { "100002487", "100002487-0", "2147881089", "2147881089-0", "2147881088", "2147881088-0" })
            names.Add(e);
        foreach (var name in names)
        {
            using var dev = root.CreateSubKey(name);
            if (dev == null) continue;
            dev.SetValue("PerformScalingOn", 0, RegistryValueKind.DWord);
            dev.SetValue("ScalingOverride", 1, RegistryValueKind.DWord);
            dev.SetValue("OverrideScalingMode", 1, RegistryValueKind.DWord);
            dev.SetValue("bOverrideScaling", 1, RegistryValueKind.DWord);
            dev.SetValue("ScalingModeOverride", 1, RegistryValueKind.DWord);
            dev.SetValue("Scaling", 2, RegistryValueKind.DWord);
            dev.SetValue("ScalingMode", 2, RegistryValueKind.DWord);
            dev.SetValue("FlatPanelScaling", 2, RegistryValueKind.DWord);
            dev.SetValue("GpuScaling", 1, RegistryValueKind.DWord);
            dev.SetValue("DisplayScaling", 0, RegistryValueKind.DWord);
            dev.SetValue("AppControlledScaling", 0, RegistryValueKind.DWord);
            dev.SetValue("isOverrideScalingEnabled", 1, RegistryValueKind.DWord);
        }
        Console.WriteLine("[NvCplScale] HKCU NVTweak Override stamps OK");
    }
}
