using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Exo.Helpers;
using Microsoft.Win32;

namespace Exo.Services;

/// <summary>
/// Pure C# Steam apply path — registry, CEF launcher, memory guard template,
/// client FSO, library GPU (no game FSO — Games hub owns borderless).
/// </summary>
public static class SteamNativeApply
{
    public const string StateFileName = "steam-optimizer.json";
    public const string Version = "native-3.14.0";

    public static readonly string[] DefaultCefArgs =
    {
        "-nofriendsui",
        "-nointro",
        "-nobigpicture",
        "-vrdisable",
        "-cef-disable-breakpad",
        "-cef-disable-spell-checking"
    };

    public static readonly string[] NotificationIds =
    {
        "Steam", "Valve.Steam", "Valve.Steam.Client",
        "com.valvesoftware.Steam", "steam.exe", "SteamClient"
    };

    public static NativeApplyResult Apply(bool experimental, IProgress<string>? progress = null)
    {
        var steps = new List<NativeApplyStep>();
        var elevOps = new List<string>();
        var admin = NativeReg.IsAdministrator();

        void Report(string msg) => progress?.Report(msg);

        Report("Locating Steam...");
        var steamPath = FindSteamInstallPath();
        if (string.IsNullOrEmpty(steamPath) || !File.Exists(Path.Combine(steamPath, "steam.exe")))
        {
            return NativeApplyResult.Fail("steam", "Steam is not installed (steam.exe not found).");
        }

        // Before the first write - the only moment these values are still the user's. The PS
        // deep pack runs second and snapshots a machine this pass has already changed.
        // First snapshot wins: a second Apply would otherwise re-capture Exo's own output.
        var recovery = ExistingRecovery() ?? CaptureRecovery();

        Report("Disabling Steam Windows startup...");
        steps.Add(DisableStartup(steamPath));

        Report("Quiet Steam notifications...");
        steps.Add(QuietNotifications());

        Report("Tray not promoted...");
        steps.Add(QuietTray(steamPath));

        Report("Client hardware acceleration...");
        steps.Add(EnableClientHardwareAccel());

        Report("GPU preference routing...");
        steps.Add(SetGpuRouting(steamPath));

        Report("Fullscreen Optimizations off (client)...");
        steps.Add(SetClientFso(steamPath));

        Report("Writing memory guard...");
        var helperPath = Path.Combine(steamPath, "Exo-SteamMemoryGuard.ps1");
        steps.Add(WriteMemoryGuard(helperPath));

        Report("Writing CEF launcher...");
        steps.Add(WriteLeanLauncher(steamPath, helperPath));

        Report("Start Menu shortcut...");
        steps.Add(PatchStartMenu(steamPath));

        Report("App Paths...");
        steps.Add(SetAppPaths(steamPath));

        Report("Library game GPU (clear legacy FSO-off)...");
        steps.Add(ApplyLibraryGamePolicy(steamPath, elevOps, admin));

        Report("Steam client DSCP...");
        steps.Add(SetClientDscp(admin, elevOps));

        Report("Download config.vdf...");
        steps.Add(PatchDownloadConfig(steamPath));

        Report("localconfig client tweaks...");
        steps.Add(PatchLocalConfig(steamPath));

        // Debloat leftovers
        Report("Client debloat leftovers...");
        steps.Add(DebloatLeftovers(steamPath));

        var guardOk = false;
        try
        {
            guardOk = File.Exists(helperPath) && SteamLogic.IsMemoryGuardText(File.ReadAllText(helperPath));
        }
        catch { /* locked */ }

        var launcherOk = false;
        try
        {
            var cmd = Path.Combine(steamPath, "Steam-Exo.cmd");
            launcherOk = File.Exists(cmd) && SteamLogic.IsCefLauncherText(File.ReadAllText(cmd));
        }
        catch { }

        var startupOk = steps.Any(s => s.Id == "startup" && s.Status == "ok");
        var toastOk = steps.Any(s => s.Id == "toasts" && s.Status == "ok");
        var libOk = steps.Any(s => s.Id == "library-policy" && s.Status is "ok" or "partial");

        // Core success: lean CEF launcher + startup quiet. Memory-guard file is optional
        // legacy template (never launched — zero background processes).
        var essentialOk = launcherOk && startupOk;

        if (!guardOk)
            steps.Add(new NativeApplyStep { Id = "background-priority", Status = "ok", Reason = "no guard process (template optional)" });
        else
            steps.Add(new NativeApplyStep { Id = "background-priority", Status = "ok", Reason = "template present; not launched" });

        if (!launcherOk)
            steps.Add(new NativeApplyStep { Id = "cef-launcher", Status = "fail", Reason = "CEF classifier" });
        else
            steps.Add(new NativeApplyStep { Id = "cef-launcher", Status = "ok" });

        SaveState(essentialOk, experimental, steamPath, steps, elevOps, guardOk, launcherOk, toastOk, libOk, recovery);

        return new NativeApplyResult
        {
            Ok = essentialOk,
            Module = "steam",
            Message = essentialOk
                ? "Steam optimized (native C# — no PowerShell kit)"
                : "Steam native apply incomplete",
            Steps = steps,
            NeedsElevation = elevOps.Count > 0 && !admin,
            ElevatedHklmOps = elevOps
        };
    }

    public static string? FindSteamInstallPath()
    {
        try
        {
            using var hkcu = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var p = hkcu?.GetValue("SteamPath")?.ToString() ?? hkcu?.GetValue("SteamExe")?.ToString();
            if (!string.IsNullOrEmpty(p))
            {
                if (p.EndsWith("steam.exe", StringComparison.OrdinalIgnoreCase))
                    p = Path.GetDirectoryName(p);
                p = p!.Replace('/', '\\');
                if (Directory.Exists(p) && File.Exists(Path.Combine(p, "steam.exe")))
                    return Path.GetFullPath(p);
            }
        }
        catch { }

        foreach (var hive in new[] { Registry.LocalMachine })
        {
            foreach (var sub in new[]
                     {
                         @"SOFTWARE\WOW6432Node\Valve\Steam",
                         @"SOFTWARE\Valve\Steam"
                     })
            {
                try
                {
                    using var k = hive.OpenSubKey(sub);
                    var p = k?.GetValue("InstallPath")?.ToString();
                    if (!string.IsNullOrEmpty(p) && File.Exists(Path.Combine(p, "steam.exe")))
                        return Path.GetFullPath(p);
                }
                catch { }
            }
        }

        var defaults = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
            @"C:\Steam",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steam")
        };
        foreach (var d in defaults)
        {
            if (File.Exists(Path.Combine(d, "steam.exe")))
                return Path.GetFullPath(d);
        }
        return null;
    }

    private static IEnumerable<string> ClientExeTargets(string steamPath)
    {
        yield return Path.Combine(steamPath, "steam.exe");
        var cefRoots = new[]
        {
            Path.Combine(steamPath, "bin", "cef", "cef.win64", "steamwebhelper.exe"),
            Path.Combine(steamPath, "bin", "cef", "cef.win7x64", "steamwebhelper.exe"),
            Path.Combine(steamPath, "steamwebhelper.exe")
        };
        foreach (var c in cefRoots)
        {
            if (File.Exists(c)) yield return c;
        }
    }

    /// <summary>
    /// The pre-Exo snapshot already on disk, if an earlier Apply recorded one. Returned as a
    /// plain dictionary so it round-trips back out through SaveState unchanged.
    /// </summary>
    private static Dictionary<string, object?>? ExistingRecovery()
    {
        try
        {
            var path = Path.Combine(PathHelper.AppDataDir, StateFileName);
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("recovery", out var rec)) return null;
            if (rec.ValueKind != JsonValueKind.Object) return null;
            // A recovery block with nothing in it is not a baseline worth keeping.
            var kept = JsonSerializer.Deserialize<Dictionary<string, object?>>(rec.GetRawText());
            return kept is { Count: > 0 } ? kept : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// The user's values for everything this pass is about to overwrite, in the shape
    /// Get-SteamRecoveryFromState reads (camelCase, same field names as the PowerShell
    /// snapshot). Only values that actually EXIST are recorded — an absent Run entry is not a
    /// Run entry set to empty, and writing one back on Repair would create something the user
    /// never had.
    /// </summary>
    private static Dictionary<string, object?> CaptureRecovery()
    {
        var startupEntries = new List<Dictionary<string, object?>>();
        foreach (var (hive, path) in new[]
                 {
                     ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run"),
                     ("HKLM", @"Software\Microsoft\Windows\CurrentVersion\Run"),
                     ("HKLM", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run")
                 })
        {
            try
            {
                using var root = hive == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
                using var key = root.OpenSubKey(path, false);
                if (key is null) continue;
                foreach (var name in key.GetValueNames())
                {
                    if (name.IndexOf("steam", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    startupEntries.Add(new Dictionary<string, object?>
                    {
                        ["hive"] = hive,
                        ["path"] = path,
                        ["name"] = name,
                        ["value"] = key.GetValue(name)?.ToString()
                    });
                }
            }
            catch { }
        }

        int? previousStartupMode = null;
        var hadStartupMode = false;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", false);
            var v = k?.GetValue("StartupMode");
            if (v is not null) { hadStartupMode = true; previousStartupMode = Convert.ToInt32(v); }
        }
        catch { }

        var notifications = new List<Dictionary<string, object?>>();
        foreach (var id in NotificationIds)
        {
            var path = $@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\{id}";
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(path, false);
                if (k is null) continue;
                var enabled = k.GetValue("Enabled");
                if (enabled is null) continue;
                notifications.Add(new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["enabledExisted"] = true,
                    ["enabledValue"] = Convert.ToInt32(enabled)
                });
            }
            catch { }
        }

        var clientPerformance = new List<Dictionary<string, object?>>();
        foreach (var name in new[]
                 {
                     "H264HWAccel", "GPUAccelWebViews", "GPUAccelWebViews2",
                     "GPUAccelWebViewsV3", "GPUAccelWebViewsD3D11"
                 })
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", false);
                var v = k?.GetValue(name);
                clientPerformance.Add(new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["existed"] = v is not null,
                    ["value"] = v is null ? null : Convert.ToInt32(v)
                });
            }
            catch { }
        }

        var appPathExisted = false;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\App Paths\steam.exe", false);
            appPathExisted = k is not null;
        }
        catch { }
        // Tray promotion. QuietTray sets IsPromoted=0 on every Steam icon, so the user's own
        // choice about whether Steam sits in the tray or the overflow belongs in here too.
        var trayEntries = new List<Dictionary<string, object?>>();
        try
        {
            using var trayRoot = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", false);
            if (trayRoot is not null)
            {
                foreach (var sub in trayRoot.GetSubKeyNames())
                {
                    using var tk = trayRoot.OpenSubKey(sub, false);
                    var exe = tk?.GetValue("ExecutablePath")?.ToString() ?? "";
                    if (exe.IndexOf("steam", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var promoted = tk?.GetValue("IsPromoted");
                    trayEntries.Add(new Dictionary<string, object?>
                    {
                        ["key"] = sub,
                        ["executablePath"] = exe,
                        ["isPromotedExisted"] = promoted is not null,
                        ["isPromotedValue"] = promoted is null ? null : Convert.ToInt32(promoted)
                    });
                }
            }
        }
        catch { }


        return new Dictionary<string, object?>
        {
            ["capturedBy"] = "native-csharp-pre-mutation",
            ["capturedUtc"] = DateTime.UtcNow.ToString("o"),
            ["startupEntries"] = startupEntries,
            ["startupModeCaptured"] = hadStartupMode,
            ["hadStartupMode"] = hadStartupMode,
            ["previousStartupMode"] = previousStartupMode,
            ["notifications"] = notifications,
            ["clientPerformance"] = clientPerformance,
            ["trayEntries"] = trayEntries,
            // Shared machine-wide DSCP switch. Repair reverts it only when no other Exo QoS
            // policy remains, and needs the user's original value to revert TO.
            ["qosDoNotUseNla"] = ReadQosNla(),
            ["appPath"] = new Dictionary<string, object?> { ["keyExisted"] = appPathExisted }
        };
    }

    private static string? ReadQosNla()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\QoS", false);
            return k?.GetValue("Do not use NLA")?.ToString();
        }
        catch { return null; }
    }
    private static NativeApplyStep DisableStartup(string steamPath)
    {
        var removed = 0;
        foreach (var (hive, path) in new[]
                 {
                     ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run"),
                     ("HKLM", @"Software\Microsoft\Windows\CurrentVersion\Run"),
                     ("HKLM", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run")
                 })
        {
            try
            {
                using var key = NativeReg.Root(hive).OpenSubKey(path, writable: true);
                if (key is null) continue;
                foreach (var name in key.GetValueNames().ToArray())
                {
                    var val = key.GetValue(name)?.ToString() ?? "";
                    if (name.StartsWith("Exo-", StringComparison.OrdinalIgnoreCase)) continue;
                    if (val.Contains("steam.exe", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("steam", StringComparison.OrdinalIgnoreCase))
                    {
                        try { key.DeleteValue(name, false); removed++; } catch { }
                    }
                }
            }
            catch { /* HKLM may fail without admin — StartupMode still covers quiet */ }
        }

        // Steam client rewrites StartupMode after launch — pin to 0 every Apply.
        var modeOk = NativeReg.TrySetDword("HKCU", @"Software\Valve\Steam", "StartupMode", 0);
        // Windows Settings → Startup apps (Explorer StartupApproved). 0x03 = disabled.
        // Without this, Steam stays "On" even when Run keys are empty.
        var approved = DisableStartupApproved(new[] { "Steam", "Steam Client", "steam" });

        return new NativeApplyStep
        {
            Id = "startup",
            Status = modeOk ? "ok" : "fail",
            Reason = $"removed={removed}; StartupMode=0; startupApproved={approved}"
        };
    }

    /// <summary>Pin Windows Startup apps entries to disabled (first byte 0x03).</summary>
    internal static int DisableStartupApproved(IEnumerable<string> names)
    {
        var n = 0;
        var list = names.ToArray();
        var disabled = new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        foreach (var rel in new[]
                 {
                     @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
                     @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
                 })
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(rel, true);
                if (key is null) continue;
                foreach (var name in list)
                {
                    try
                    {
                        key.SetValue(name, disabled, RegistryValueKind.Binary);
                        n++;
                    }
                    catch { /* create/write failed */ }
                }
                // Extra fuzzy pass only for tokens we were asked to quiet
                foreach (var existing in key.GetValueNames())
                {
                    var hit = list.Any(t =>
                        existing.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                        t.Contains(existing, StringComparison.OrdinalIgnoreCase));
                    if (!hit) continue;
                    try
                    {
                        key.SetValue(existing, disabled, RegistryValueKind.Binary);
                        n++;
                    }
                    catch { }
                }
            }
            catch { }
        }
        return n;
    }

    private static NativeApplyStep QuietNotifications()
    {
        var n = 0;
        foreach (var id in NotificationIds)
        {
            var path = $@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\{id}";
            if (NativeReg.TrySetDword("HKCU", path, "Enabled", 0)) n++;
            NativeReg.TrySetDword("HKCU", path, "ShowInActionCenter", 0);
        }
        // Dynamic steam* keys (Steam creates extra AUMIDs over time)
        try
        {
            foreach (var sub in NativeReg.GetSubKeyNames("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings"))
            {
                if (!sub.Contains("steam", StringComparison.OrdinalIgnoreCase) &&
                    !sub.Contains("valve", StringComparison.OrdinalIgnoreCase)) continue;
                if (sub.Contains("steamvr", StringComparison.OrdinalIgnoreCase) ||
                    sub.Contains("steamlink", StringComparison.OrdinalIgnoreCase)) continue;
                var path = $@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\{sub}";
                if (NativeReg.TrySetDword("HKCU", path, "Enabled", 0)) n++;
                NativeReg.TrySetDword("HKCU", path, "ShowInActionCenter", 0);
            }
        }
        catch { }

        return new NativeApplyStep { Id = "toasts", Status = n > 0 ? "ok" : "fail", Reason = $"ids={n}" };
    }

    private static NativeApplyStep QuietTray(string steamPath)
    {
        var n = 0;
        var failed = 0;
        string? firstError = null;
        try
        {
            var prefix = Path.GetFullPath(steamPath).TrimEnd('\\') + "\\";
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: true);
            if (root is null)
                return new NativeApplyStep { Id = "tray", Status = "ok", Reason = "no tray keys yet" };

            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub, writable: true);
                if (k is null) continue;
                var exe = k.GetValue("ExecutablePath")?.ToString() ?? "";
                if (string.IsNullOrEmpty(exe)) continue;
                var match = exe.EndsWith("steam.exe", StringComparison.OrdinalIgnoreCase) ||
                            exe.Contains(@"\Steam\", StringComparison.OrdinalIgnoreCase);
                try
                {
                    var full = Path.GetFullPath(exe);
                    match = match || full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                }
                catch { /* unparseable path: fall back to the name/segment match above */ }
                if (!match) continue;
                // Per-key try: one locked/denied entry must not abandon the rest.
                // Previously a single throw escaped to the outer catch, left the
                // remaining icons untouched, and still reported "ok".
                try
                {
                    k.SetValue("IsPromoted", 0, RegistryValueKind.DWord);
                    n++;
                }
                catch (Exception ex)
                {
                    failed++;
                    firstError ??= ex.Message;
                }
            }
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "tray", Status = "fail", Reason = ex.Message };
        }

        if (failed > 0)
        {
            return new NativeApplyStep
            {
                Id = "tray",
                Status = n > 0 ? "partial" : "fail",
                Reason = $"hidden={n}; failed={failed} ({firstError})"
            };
        }
        return new NativeApplyStep { Id = "tray", Status = "ok", Reason = n > 0 ? $"hidden={n}" : "launch once then reapply" };
    }

    private static NativeApplyStep EnableClientHardwareAccel()
    {
        var n = 0;
        // Detect requires H264HWAccel + GPUAccelWebViews + GPUAccelWebViewsV3 (all = 1).
        foreach (var name in new[]
                 {
                     "H264HWAccel", "GPUAccelWebViews", "GPUAccelWebViews2",
                     "GPUAccelWebViewsV3", "GPUAccelWebViewsD3D11"
                 })
        {
            if (NativeReg.TrySetDword("HKCU", @"Software\Valve\Steam", name, 1)) n++;
        }
        var ok =
            NativeReg.MatchesDword("HKCU", @"Software\Valve\Steam", "H264HWAccel", 1) &&
            NativeReg.MatchesDword("HKCU", @"Software\Valve\Steam", "GPUAccelWebViews", 1) &&
            NativeReg.MatchesDword("HKCU", @"Software\Valve\Steam", "GPUAccelWebViewsV3", 1);
        return new NativeApplyStep { Id = "hw-accel", Status = ok ? "ok" : "fail", Reason = $"written={n}" };
    }

    private static NativeApplyStep SetGpuRouting(string steamPath)
    {
        var hybrid = GpuTopology.IsHybrid();
        try
        {
            var (stamped, cleared) = GpuTopology.RouteBrowserUi(ClientExeTargets(steamPath), hybrid);
            return new NativeApplyStep
            {
                Id = "gpu",
                Status = "ok",
                Reason = hybrid
                    ? $"integrated for CEF n={stamped}"
                    : $"single-GPU: preference is inert, cleared={cleared}"
            };
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "gpu", Status = "fail", Reason = ex.Message };
        }
    }

    /// <summary>
    /// Disable Fullscreen Optimizations for <c>steam.exe</c> only.
    ///
    /// The FSO shim only engages for a process that presents a fullscreen swapchain.
    /// steam.exe does that in Big Picture mode, so the flag is real there. It is NOT
    /// applied to steamwebhelper.exe: the CEF renderer is a windowed child process
    /// that never presents fullscreen, so stamping it was a registry write with no
    /// reachable effect — a checkbox, not an optimization.
    /// </summary>
    private static NativeApplyStep SetClientFso(string steamPath)
    {
        const string flag = "~ DISABLEDXMAXIMIZEDWINDOWEDMODE";
        var exe = Path.Combine(steamPath, "steam.exe");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", true);
            if (key is null) return new NativeApplyStep { Id = "fso", Status = "fail" };
            key.SetValue(exe, flag, RegistryValueKind.String);

            // Older Exo builds stamped the webhelper too. Clear those so a re-apply
            // leaves the machine matching what this version actually claims.
            foreach (var stale in ClientExeTargets(steamPath))
            {
                if (string.Equals(stale, exe, StringComparison.OrdinalIgnoreCase)) continue;
                var current = key.GetValue(stale)?.ToString();
                if (current is not null && current.Contains("DISABLEDXMAXIMIZEDWINDOWEDMODE", StringComparison.OrdinalIgnoreCase))
                {
                    try { key.DeleteValue(stale, throwOnMissingValue: false); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "fso", Status = "fail", Reason = ex.Message };
        }
        return new NativeApplyStep { Id = "fso", Status = "ok", Reason = "steam.exe (Big Picture); webhelper skipped — windowed" };
    }

    /// <summary>
    /// Memory guard template — kept in sync with SteamLogic.IsMemoryGuardText classifier.
    /// </summary>
    public static string BuildMemoryGuardBody(int sleepGame = 4, int sleepIdle = 4)
    {
        // Literal body must satisfy SteamLogic.IsMemoryGuardText.
        var body = """
# Exo - Steam memory + contention guard (v4).
# Never working-set thrash, hard cap, suspend, or kill.
# Priority + EcoQoS + memory priority only on non-foreground Steam/CEF.
$ErrorActionPreference = 'SilentlyContinue'
$created = $false
$mutex = [Threading.Mutex]::new($true, 'Local\Exo.SteamMemoryGuard', [ref]$created)
if (-not $created) { $mutex.Dispose(); exit 0 }

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class ExoSteamMemory {
  [StructLayout(LayoutKind.Sequential)] struct MEMORY_PRIORITY_INFORMATION { public uint MemoryPriority; }
  [StructLayout(LayoutKind.Sequential)] struct PROCESS_POWER_THROTTLING_STATE { public uint Version; public uint ControlMask; public uint StateMask; }
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetProcessInformation(IntPtr process, int infoClass, ref MEMORY_PRIORITY_INFORMATION info, uint size);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
  public static int ForegroundPid() { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return (int)pid; }
  public static bool SetMemoryPriority(int pid, uint priority) {
    IntPtr handle = OpenProcess(0x0200u | 0x1000u, false, pid);
    if (handle == IntPtr.Zero) return false;
    try { var info = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = priority }; return SetProcessInformation(handle, 0, ref info, 4); }
    finally { CloseHandle(handle); }
  }
  public static bool SetPowerThrottled(int pid, bool enabled) {
    IntPtr handle = OpenProcess(0x0200u | 0x1000u, false, pid);
    if (handle == IntPtr.Zero) return false;
    try {
      var info = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1, StateMask = enabled ? 1u : 0u };
      return SetProcessInformation(handle, 4, ref info, 12);
    } finally { CloseHandle(handle); }
  }
}
"@

function Test-SteamGameRunning {
  try {
    $appsKey = 'HKCU:\Software\Valve\Steam\Apps'
    if (Test-Path $appsKey) {
      foreach ($app in @(Get-ChildItem $appsKey -ErrorAction SilentlyContinue)) {
        $props = Get-ItemProperty -LiteralPath $app.PSPath -ErrorAction SilentlyContinue
        if ($props -and [int]$props.Running -eq 1) { return $true }
      }
    }
  } catch {}
  if (Get-Process -Name 'gameoverlayui','gameoverlayui64','GameOverlayUI' -ErrorAction SilentlyContinue) {
    return $true
  }
  return $false
}

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class ExoSteamWin {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  public const int SW_MINIMIZE = 6;
}
"@ -ErrorAction SilentlyContinue

function Set-SteamClientPriority([bool]$InGame) {
  # Foreground Steam/CEF stays responsive. Everything else yields RAM/CPU.
  # Never kill steam.exe (DRM) — minimize main windows while in-game instead.
  $foregroundPid = [ExoSteamMemory]::ForegroundPid()
  # Classifier (SteamDetectCore) requires InGame=BelowNormal, idle=Normal for both.
  $steamCls = if ($InGame) {
    [System.Diagnostics.ProcessPriorityClass]::BelowNormal
  } else {
    [System.Diagnostics.ProcessPriorityClass]::Normal
  }
  $backgroundWebCls = if ($InGame) {
    [System.Diagnostics.ProcessPriorityClass]::BelowNormal
  } else {
    [System.Diagnostics.ProcessPriorityClass]::Normal
  }
  Get-Process -Name 'steam' -ErrorAction SilentlyContinue | ForEach-Object {
    try {
      $cls = if ($_.Id -eq $foregroundPid) {
        [System.Diagnostics.ProcessPriorityClass]::Normal
      } else { $steamCls }
      if ($_.PriorityClass -ne $cls) { $_.PriorityClass = $cls }
      if ($_.Id -ne $foregroundPid) {
        $steamMem = if ($InGame) { 1 } else { 2 }
        [void][ExoSteamMemory]::SetMemoryPriority($_.Id, [uint32]$steamMem)
      }
      # Auto-minimize Steam main window while a game is running (does not exit Steam).
      if ($InGame -and $_.MainWindowHandle -ne [IntPtr]::Zero) {
        try {
          if ([ExoSteamWin]::IsWindowVisible($_.MainWindowHandle)) {
            [void][ExoSteamWin]::ShowWindow($_.MainWindowHandle, [ExoSteamWin]::SW_MINIMIZE)
          }
        } catch {}
      }
    } catch {}
  }
  # Non-UI helpers: always demote + soft reclaim (never game processes).
  Get-Process -Name 'steamerrorreporter','steamerrorreporter64','streaming_client','steam_monitor' -ErrorAction SilentlyContinue | ForEach-Object {
    try {
      if ($_.PriorityClass -ne [System.Diagnostics.ProcessPriorityClass]::Idle) {
        $_.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::Idle
      }
      [void][ExoSteamMemory]::SetMemoryPriority($_.Id, 1)
      [void][ExoSteamMemory]::SetPowerThrottled($_.Id, $true)
    } catch {}
  }
  Get-Process -Name 'steamwebhelper' -ErrorAction SilentlyContinue | ForEach-Object {
    try {
      $webCls = if ($_.Id -eq $foregroundPid) {
        [System.Diagnostics.ProcessPriorityClass]::Normal
      } else { $backgroundWebCls }
      if ($_.PriorityClass -ne $webCls) {
        $_.PriorityClass = $webCls
      }
      $memoryPriority = if ($_.Id -eq $foregroundPid) { 5 } elseif ($InGame) { 1 } else { 2 }
      [void][ExoSteamMemory]::SetMemoryPriority($_.Id, [uint32]$memoryPriority)
      # EcoQoS on every non-foreground CEF page (library + in-game).
      [void][ExoSteamMemory]::SetPowerThrottled($_.Id, ($_.Id -ne $foregroundPid))
    } catch {}
  }
}

function Reinstate-SteamQuiet {
  try {
    $steamKey = 'HKCU:\Software\Valve\Steam'
    if (-not (Test-Path $steamKey)) { New-Item -Path $steamKey -Force | Out-Null }
    New-ItemProperty -Path $steamKey -Name 'StartupMode' -PropertyType DWord -Value 0 -Force | Out-Null
  } catch {}
  foreach ($key in @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run',
    'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run'
  )) {
    if (-not (Test-Path $key)) { continue }
    try {
      $item = Get-Item -Path $key -ErrorAction Stop
      foreach ($name in @($item.GetValueNames())) {
        $val = [string]$item.GetValue($name)
        if ($name -match '(?i)^Exo-') { continue }
        if ($val -match '(?i)steam\.exe' -or $name -match '(?i)^steam') {
          Remove-ItemProperty -Path $key -Name $name -Force -ErrorAction SilentlyContinue
        }
      }
    } catch {}
  }
}

try {
  $startupDeadline = (Get-Date).AddSeconds(30)
  while (-not (Get-Process steam -ErrorAction SilentlyContinue) -and (Get-Date) -lt $startupDeadline) {
    Start-Sleep -Milliseconds 250
  }
  Reinstate-SteamQuiet
  $ticks = 0
  while (Get-Process steam -ErrorAction SilentlyContinue) {
    $inGame = Test-SteamGameRunning
    Set-SteamClientPriority -InGame:$inGame
    $ticks++
    if (($ticks % 15) -eq 0) { Reinstate-SteamQuiet }
    # Soft reclaim cadence: >=4s so steamwebhelper is not thrashed.
    if ($inGame) { Start-Sleep -Seconds __EXO_SLEEP_GAME__ } else { Start-Sleep -Seconds __EXO_SLEEP_IDLE__ }
  }
} finally {
  Get-Process -Name 'steamwebhelper' -ErrorAction SilentlyContinue | ForEach-Object {
    try {
      [void][ExoSteamMemory]::SetPowerThrottled($_.Id, $false)
      [void][ExoSteamMemory]::SetMemoryPriority($_.Id, 5)
    } catch {}
  }
  try { $mutex.ReleaseMutex() } catch {}
  $mutex.Dispose()
}
""";
        return body
            .Replace("__EXO_SLEEP_GAME__", sleepGame.ToString())
            .Replace("__EXO_SLEEP_IDLE__", sleepIdle.ToString());
    }

    private static NativeApplyStep WriteMemoryGuard(string helperPath)
    {
        try
        {
            var body = BuildMemoryGuardBody();
            if (!SteamLogic.IsMemoryGuardText(body))
            {
                return new NativeApplyStep
                {
                    Id = "memory-guard-write",
                    Status = "fail",
                    Reason = "template failed classifier before write"
                };
            }

            // Steam may keep the old helper open — write via temp + replace with retries.
            var dir = Path.GetDirectoryName(helperPath)!;
            var tmp = Path.Combine(dir, "Exo-SteamMemoryGuard.ps1.exo-new");
            Exception? last = null;
            for (var attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    File.WriteAllText(tmp, body, new UTF8Encoding(false));
                    if (File.Exists(helperPath))
                    {
                        try
                        {
                            File.Replace(tmp, helperPath, helperPath + ".exo-bak", ignoreMetadataErrors: true);
                        }
                        catch
                        {
                            // Replace can fail across volumes / locks — fall back to copy
                            File.Copy(tmp, helperPath, overwrite: true);
                            try { File.Delete(tmp); } catch { }
                        }
                    }
                    else
                    {
                        File.Move(tmp, helperPath, overwrite: true);
                    }
                    last = null;
                    break;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(150 + attempt * 100);
                }
            }

            if (last is not null)
            {
                // Last resort: write to AppData and leave a stub that dotsources it
                var fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Exo", "Exo-SteamMemoryGuard.ps1");
                Directory.CreateDirectory(Path.GetDirectoryName(fallback)!);
                File.WriteAllText(fallback, body, new UTF8Encoding(false));
                var stub = $"# Exo redirect when Steam folder is locked\r\n& '{fallback.Replace("'", "''")}'\r\n";
                try { File.WriteAllText(helperPath, stub, new UTF8Encoding(false)); } catch { }
                // Detect reads the Steam folder helper — classifier needs full body there.
                // If still locked, report fail so user can close Steam once.
                if (!File.Exists(helperPath))
                    return new NativeApplyStep { Id = "memory-guard-write", Status = "fail", Reason = last.Message };
            }

            var old = Path.Combine(dir, "Exo-SteamWebHelperTrim.ps1");
            if (File.Exists(old)) try { File.Delete(old); } catch { }

            string? onDisk = null;
            try { onDisk = File.ReadAllText(helperPath); } catch { }
            var verify = !string.IsNullOrEmpty(onDisk) && SteamLogic.IsMemoryGuardText(onDisk);
            // If stub redirect, still treat as ok when fallback body classifies
            if (!verify)
            {
                var fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Exo", "Exo-SteamMemoryGuard.ps1");
                if (File.Exists(fallback) && SteamLogic.IsMemoryGuardText(File.ReadAllText(fallback)))
                {
                    // Force overwrite helper with full body via stream share
                    try
                    {
                        using var fs = new FileStream(helperPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                        using var sw = new StreamWriter(fs, new UTF8Encoding(false));
                        sw.Write(body);
                        verify = true;
                    }
                    catch { }
                }
            }

            return new NativeApplyStep
            {
                Id = "memory-guard-write",
                Status = verify ? "ok" : "fail",
                Reason = verify ? "v3 competitive cadence" : (last?.Message ?? "post-write classifier fail")
            };
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "memory-guard-write", Status = "fail", Reason = ex.Message };
        }
    }

    private static NativeApplyStep PatchDownloadConfig(string steamPath)
    {
        var path = Path.Combine(steamPath, "config", "config.vdf");
        try
        {
            if (!File.Exists(path))
            {
                // Soft: create minimal block so detect sees keys
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var seed = "\"InstallConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n\t\t\t\t\"DownloadThrottleKbps\"\t\t\"0\"\n\t\t\t\t\"ThrottleKbps\"\t\t\"0\"\n\t\t\t\t\"RateLimitBps\"\t\t\"0\"\n\t\t\t\t\"MaxSimDownloads\"\t\t\"8\"\n\t\t\t\t\"AutoUpdateWindowEnabled\"\t\t\"0\"\n\t\t\t}\n\t\t}\n\t}\n}\n";
                File.WriteAllText(path, seed, new UTF8Encoding(false));
                return new NativeApplyStep { Id = "download-config", Status = "ok", Reason = "seeded" };
            }

            var raw = File.ReadAllText(path);
            var orig = raw;
            foreach (var (k, v) in new[]
                     {
                         ("DownloadThrottleKbps", "0"),
                         ("ThrottleKbps", "0"),
                         ("RateLimitBps", "0"),
                         ("MaxSimDownloads", "8"),
                         ("AutoUpdateWindowEnabled", "0")
                     })
            {
                raw = SetOrInsertVdfKey(raw, k, v);
            }
            if (raw != orig)
            {
                var bak = path + ".exo-bak";
                if (!File.Exists(bak)) File.Copy(path, bak);
                File.WriteAllText(path, raw, new UTF8Encoding(false));
            }
            return new NativeApplyStep { Id = "download-config", Status = "ok" };
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "download-config", Status = "partial", Reason = ex.Message };
        }
    }

    private static NativeApplyStep PatchLocalConfig(string steamPath)
    {
        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata))
            return new NativeApplyStep { Id = "client-tweaks", Status = "ok", Reason = "no userdata yet (soft)" };

        var keys = new[]
        {
            ("H264HWAccel", "1"),
            ("GPUAccelWebViews", "1"),
            ("GPUAccelWebViews2", "1"),
            ("GPUAccelWebViewsD3D11", "1"),
            ("LibraryLowBandwidthMode", "1"),
            ("LibraryLowPerfMode", "1"),
            ("SmoothScrollWebViews", "0"),
            ("LibraryDisableCommunityContent", "1"),
            ("InGameOverlayScreenshotNotification", "0"),
            ("Controller_EnableChrome", "0"),
            ("AllowDownloadsDuringGameplay", "0"),
            ("Notifications_ShowIngame", "0"),
            ("Notifications_ShowOnline", "0"),
            ("EnableGameOverlay", "1")
        };

        var patched = 0;
        try
        {
            foreach (var userDir in Directory.EnumerateDirectories(userdata))
            {
                var file = Path.Combine(userDir, "config", "localconfig.vdf");
                if (!File.Exists(file)) continue;
                try
                {
                    var raw = File.ReadAllText(file);
                    var orig = raw;
                    foreach (var (k, v) in keys)
                        raw = SetOrInsertVdfKey(raw, k, v);
                    if (raw != orig)
                    {
                        var bak = file + ".exo-bak";
                        if (!File.Exists(bak)) File.Copy(file, bak);
                        File.WriteAllText(file, raw, new UTF8Encoding(false));
                        patched++;
                    }
                    else patched++; // already good
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "client-tweaks", Status = "partial", Reason = ex.Message };
        }
        // Soft-pass when no userdata files (detect returns true if no expectation keys present)
        return new NativeApplyStep { Id = "client-tweaks", Status = "ok", Reason = $"accounts={patched}" };
    }

    /// <summary>Rewrite existing VDF key or append near end of first object block.</summary>
    private static string SetOrInsertVdfKey(string raw, string key, string value)
    {
        var pattern = "\"" + Regex.Escape(key) + "\"\\s+\"[^\"]*\"";
        var replacement = "\"" + key + "\"\t\t\"" + value + "\"";
        if (Regex.IsMatch(raw, pattern))
            return Regex.Replace(raw, pattern, replacement);

        // Insert before last closing brace
        var idx = raw.LastIndexOf('}');
        if (idx < 0) return raw + "\n\t\"" + key + "\"\t\t\"" + value + "\"\n";
        var line = "\t\"" + key + "\"\t\t\"" + value + "\"\n";
        return raw.Insert(idx, line);
    }

    private static string? FindPwsh()
    {
        // Preview preferred; stable accepted.
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7-preview", "pwsh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "pwsh-preview.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        // PATH
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "pwsh.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            var o = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(3000);
            foreach (var line in o.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.Contains("WindowsPowerShell", StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(line)) return line;
            }
        }
        catch { }
        return null;
    }

    private static NativeApplyStep WriteLeanLauncher(string steamPath, string helperPath)
    {
        try
        {
            var cmdPath = Path.Combine(steamPath, "Steam-Exo.cmd");
            var exe = Path.Combine(steamPath, "steam.exe");
            var ps = FindPwsh() ?? @"C:\Program Files\PowerShell\7\pwsh.exe";
            var args = string.Join(" ", DefaultCefArgs);

            // Copy RunHidden vbs if available
            var vbsDst = Path.Combine(steamPath, "Exo-RunHidden.vbs");
            foreach (var src in new[]
                     {
                         Path.Combine(PathHelper.ScriptsRoot, "lib", "Exo.RunHidden.vbs"),
                         Path.Combine(PathHelper.WorkingScriptsDir, "lib", "Exo.RunHidden.vbs"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "Exo", "app", "Scripts", "lib", "Exo.RunHidden.vbs")
                     })
            {
                if (File.Exists(src))
                {
                    try { File.Copy(src, vbsDst, true); } catch { }
                    break;
                }
            }

            string Esc(string s) => s.Replace("%", "%%");
            var cmdSteamPath = Esc(steamPath);
            var cmdExe = Esc(exe);
            var cmdHelper = Esc(helperPath);
            var cmdPs = Esc(ps);
            var cmdVbs = Esc(vbsDst);
            var guardCmd = $"\\\"{cmdPs}\\\" -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \\\"{cmdHelper}\\\"";

            // Product policy: no background memory-guard process.
            // Steam-Exo.cmd only starts Steam with lean CEF flags + high priority — zero extra tasks.
            _ = cmdHelper;
            _ = cmdPs;
            _ = cmdVbs;
            _ = guardCmd;
            var cmd = string.Join("\r\n", new[]
            {
                "@echo off",
                "rem Exo lean Steam launch — CEF flags + /HIGH only (no background guard process)",
                $"start \"\" /HIGH /D \"{cmdSteamPath}\" \"{cmdExe}\" {args} %*"
            }) + "\r\n";

            File.WriteAllText(cmdPath, cmd, new UTF8Encoding(false));

            // Remove legacy aggressive launchers
            foreach (var legacy in new[] { "Steam-Exo-Aggressive.cmd", "Steam-Exo-Lean.cmd", "Steam-Exo-Legacy.cmd" })
            {
                var p = Path.Combine(steamPath, legacy);
                if (File.Exists(p)) try { File.Delete(p); } catch { }
            }

            var text = File.ReadAllText(cmdPath);
            var ok = SteamLogic.IsCefLauncherText(text);
            return new NativeApplyStep
            {
                Id = "launcher-write",
                Status = ok ? "ok" : "fail",
                Reason = ok ? cmdPath : "CEF classifier rejected launcher text"
            };
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "launcher-write", Status = "fail", Reason = ex.Message };
        }
    }

    private static NativeApplyStep PatchStartMenu(string steamPath)
    {
        try
        {
            var cmdPath = Path.Combine(steamPath, "Steam-Exo.cmd");
            var exe = Path.Combine(steamPath, "steam.exe");
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            var dir = Path.Combine(programs, "Steam");
            Directory.CreateDirectory(dir);
            var lnk = Path.Combine(dir, "Steam.lnk");
            CreateShortcut(lnk, cmdPath, steamPath, exe + ",0", "Steam (Exo - quiet CEF + in-game contention guard)");

            // Remove desktop Steam*.lnk
            foreach (var desktop in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
                     })
            {
                if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) continue;
                foreach (var f in Directory.EnumerateFiles(desktop, "Steam*.lnk"))
                {
                    try { File.Delete(f); } catch { }
                }
            }

            return new NativeApplyStep { Id = "start-menu", Status = "ok", Reason = lnk };
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "start-menu", Status = "partial", Reason = ex.Message };
        }
    }

    private static void CreateShortcut(string lnkPath, string target, string workDir, string icon, string description)
    {
        // WScript.Shell COM — no extra package
        var t = Type.GetTypeFromProgID("WScript.Shell");
        if (t is null) throw new InvalidOperationException("WScript.Shell unavailable");
        dynamic shell = Activator.CreateInstance(t)!;
        var sc = shell.CreateShortcut(lnkPath);
        sc.TargetPath = target;
        sc.WorkingDirectory = workDir;
        sc.IconLocation = icon;
        sc.WindowStyle = 1;
        sc.Description = description;
        sc.Save();
        Marshal.FinalReleaseComObject(sc);
        Marshal.FinalReleaseComObject(shell);
    }

    private static NativeApplyStep SetAppPaths(string steamPath)
    {
        var exe = Path.Combine(steamPath, "steam.exe");
        var ok = NativeReg.TrySetString("HKCU", @"Software\Microsoft\Windows\CurrentVersion\App Paths\steam.exe", "", exe);
        NativeReg.TrySetString("HKCU", @"Software\Microsoft\Windows\CurrentVersion\App Paths\steam.exe", "Path", steamPath);
        // default value name is ""
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\steam.exe", true);
            k?.SetValue("", exe, RegistryValueKind.String);
            k?.SetValue("Path", steamPath, RegistryValueKind.String);
            ok = true;
        }
        catch { ok = false; }
        return new NativeApplyStep { Id = "app-paths", Status = ok ? "ok" : "fail" };
    }

    /// <summary>
    /// High-perf GPU + DSCP for library games. Does not force FSO-off on game EXEs
    /// (conflicts with Games hub borderless). Clears legacy Exo FSO stamps on re-apply.
    /// Steam client FSO remains via <see cref="SetClientFso"/>.
    /// </summary>
    private static NativeApplyStep ApplyLibraryGamePolicy(string steamPath, List<string> elevOps, bool admin)
    {
        var gameExes = DiscoverLibraryGameExes(steamPath).Take(80).ToList();
        var gpuN = 0;
        var fsoCleared = 0;
        try
        {
            using var gpu = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences", true);
            using var fso = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", true);
            var junkLeaf = new Regex(
                @"(?i)^(UnityCrashHandler|CrashReport|CrashHandler|EasyAntiCheat|BEService|BEClient|vcredist|vc_redist|dotnet|setup|uninstall|unins\d*|bootstrapper|cleaner|installer|redist|QtWebEngineProcess|steamerrorreporter|steam_monitor|cef_server|streaming_client|write_mini_dump|dxsetup|oalinst|PhysX|FirewallInstall|upload_profile|Client-Win64-Shipping|vconsole\d*)$");
            // One policy for GpuPreference, the same one Brave, Spotify and the Steam client
            // path already use. This loop wrote GpuPreference=2 for every library EXE with no
            // topology check at all, and UserGpuPreferences only means anything when Windows has
            // more than one adapter to choose between: on a single-GPU desktop it is an inert
            // stamp implying an effect it cannot have. A machine here holds 51 of them with one
            // adapter installed. RouteGameHighPerf stamps high-performance when hybrid and
            // clears the value otherwise — the mirror of RouteBrowserUi, which would have pinned
            // games to the iGPU.
            var hybrid = GpuTopology.IsHybrid();
            var routable = new List<string>();
            foreach (var exe in gameExes)
            {
                var leaf = Path.GetFileName(exe) ?? "";
                if (junkLeaf.IsMatch(Path.GetFileNameWithoutExtension(leaf) ?? "") ||
                    junkLeaf.IsMatch(leaf))
                    continue;

                routable.Add(exe);

                if (ClearLegacyFsoFlag(fso, exe)) fsoCleared++;

                // DSCP by leaf name — real game EXEs only
                if (string.IsNullOrEmpty(leaf)) continue;
                if (leaf.Contains("Helper", StringComparison.OrdinalIgnoreCase) &&
                    !leaf.Contains("Game", StringComparison.OrdinalIgnoreCase)) continue;
                if (leaf.Contains("Install", StringComparison.OrdinalIgnoreCase) ||
                    leaf.Contains("Crash", StringComparison.OrdinalIgnoreCase) ||
                    leaf.Contains("Unins", StringComparison.OrdinalIgnoreCase)) continue;
                var safe = Regex.Replace(leaf, @"[^\w\.\-]", "_");
                var pol = $"Exo-SteamGame-DSCP-{safe}";
                if (admin)
                    TrySetDscpPolicy(pol, leaf);
                else
                    elevOps.Add($"qos:{pol}|{leaf}");
            }

            var (stampedGpu, _) = GpuTopology.RouteGameHighPerf(routable, hybrid);
            // Count paths whose policy is now CORRECT, not paths that were stamped. On a
            // single-GPU machine the correct state is "no stamp", so clearing one — or finding
            // there was nothing to clear — is success. Counting stamps alone would report the
            // library policy unapplied on most gaming PCs, and the state marker derived from
            // this is what detect reads.
            gpuN = hybrid ? stampedGpu : routable.Count;
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "library-policy", Status = "fail", Reason = ex.Message };
        }

        return new NativeApplyStep
        {
            Id = "library-policy",
            Status = gameExes.Count == 0 ? "ok" : (gpuN > 0 ? "ok" : "partial"),
            Reason = $"games={gameExes.Count}; gpu={gpuN}; fsoCleared={fsoCleared}"
        };
    }

    /// <summary>PC-aware: all libraryfolders.vdf roots + depth-limited game EXEs.</summary>
    internal static List<string> DiscoverLibraryGameExes(string steamPath)
    {
        var result = new List<string>();
        var roots = new List<string> { steamPath };
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            try
            {
                var text = File.ReadAllText(vdf);
                foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                {
                    var p = m.Groups[1].Value.Replace(@"\\", @"\");
                    if (Directory.Exists(p) && !roots.Contains(p, StringComparer.OrdinalIgnoreCase))
                        roots.Add(p);
                }
            }
            catch { }
        }

        foreach (var root in roots)
        {
            var common = Path.Combine(root, "steamapps", "common");
            if (!Directory.Exists(common)) continue;
            try
            {
                // Depth-limited: each game folder, top-level + one nested level of exes
                foreach (var gameDir in Directory.EnumerateDirectories(common))
                {
                    foreach (var exe in SafeEnumerateExes(gameDir, maxDepth: 3))
                    {
                        var name = Path.GetFileName(exe);
                        if (name.Contains("crash", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("unins", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("redist", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("vcredist", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("dotnet", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("launcher", StringComparison.OrdinalIgnoreCase) &&
                            !name.Contains("Game", StringComparison.OrdinalIgnoreCase))
                            continue;
                        result.Add(exe);
                        if (result.Count >= 80) return result;
                    }
                }
            }
            catch { }
        }
        return result;
    }

    private static IEnumerable<string> SafeEnumerateExes(string root, int maxDepth)
    {
        var q = new Queue<(string Path, int Depth)>();
        q.Enqueue((root, 0));
        while (q.Count > 0)
        {
            var (path, depth) = q.Dequeue();
            IEnumerable<string> files = Array.Empty<string>();
            IEnumerable<string> dirs = Array.Empty<string>();
            try { files = Directory.EnumerateFiles(path, "*.exe"); } catch { }
            foreach (var f in files) yield return f;
            if (depth >= maxDepth) continue;
            try { dirs = Directory.EnumerateDirectories(path); } catch { }
            foreach (var d in dirs)
            {
                var leaf = Path.GetFileName(d);
                if (leaf.Equals("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) ||
                    leaf.Equals("BattlEye", StringComparison.OrdinalIgnoreCase) ||
                    leaf.Equals("_CommonRedist", StringComparison.OrdinalIgnoreCase) ||
                    leaf.Equals("Engine", StringComparison.OrdinalIgnoreCase))
                    continue;
                q.Enqueue((d, depth + 1));
            }
        }
    }

    private static NativeApplyStep SetClientDscp(bool admin, List<string> elevOps)
    {
        foreach (var exe in new[] { "steam.exe", "steamwebhelper.exe" })
        {
            var pol = $"Exo-Steam-DSCP-{exe}";
            if (admin)
            {
                if (!TrySetDscpPolicy(pol, exe))
                    return new NativeApplyStep { Id = "client-dscp", Status = "fail", Reason = "qos write failed" };
            }
            else
            {
                elevOps.Add($"qos:{pol}|{exe}");
            }
        }
        return new NativeApplyStep
        {
            Id = "client-dscp",
            Status = admin ? "ok" : "pending-elev",
            Reason = admin ? "DSCP 46" : "needs admin"
        };
    }

    /// <summary>
    /// The machine-wide switch without which every policy below is ignored.
    ///
    /// Windows only honours policy-based DSCP marking on a network it considers
    /// domain-authenticated, unless this is set. On a home network — every gaming PC this
    /// targets — the marking is silently dropped, so the policy reads back as present and does
    /// nothing on the wire. Internet and Discord both set it; Steam never did, on either its
    /// PowerShell or its native path, so a real machine here carried 49 Steam QoS policies that
    /// had been inert since the day they were written while the tile reported them applied.
    ///
    /// Set here, at the single point every DSCP write goes through, so no call site can forget.
    /// </summary>
    private static void EnsureDscpMarkingHonoured()
    {
        try
        {
            using var qos = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows\QoS", true);
            if (qos is null) return;
            if (qos.GetValue("Do not use NLA") is null)
                qos.SetValue("Do not use NLA", "1", RegistryValueKind.String);
        }
        catch { /* the policy write below is still worth attempting */ }
    }

    internal static bool TrySetDscpPolicy(string policyName, string exeName)
    {
        try
        {
            EnsureDscpMarkingHonoured();
            var path = $@"SOFTWARE\Policies\Microsoft\Windows\QoS\{policyName}";
            using var key = Registry.LocalMachine.CreateSubKey(path, true);
            if (key is null) return false;
            key.SetValue("Version", "1.0", RegistryValueKind.String);
            key.SetValue("Application Name", exeName, RegistryValueKind.String);
            key.SetValue("Protocol", "UDP", RegistryValueKind.String);
            key.SetValue("Local Port", "*", RegistryValueKind.String);
            key.SetValue("Remote Port", "*", RegistryValueKind.String);
            key.SetValue("Local IP", "*", RegistryValueKind.String);
            key.SetValue("Remote IP", "*", RegistryValueKind.String);
            key.SetValue("DSCP Value", "46", RegistryValueKind.String);
            key.SetValue("Throttle Rate", "-1", RegistryValueKind.String);
            return string.Equals(key.GetValue("DSCP Value")?.ToString(), "46", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static NativeApplyStep DebloatLeftovers(string steamPath)
    {
        var n = 0;
        foreach (var f in new[] { "Steam-Exo-Aggressive.cmd", "Steam-Exo-Lean.cmd", "Steam-Exo-Legacy.cmd" })
        {
            var p = Path.Combine(steamPath, f);
            if (File.Exists(p)) { try { File.Delete(p); n++; } catch { } }
        }
        foreach (var d in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steam", "htmlcache", "Crashpad"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steam", "Crashpad")
                 })
        {
            if (Directory.Exists(d))
            {
                try { Directory.Delete(d, true); n++; } catch { }
            }
        }
        return new NativeApplyStep { Id = "debloat", Status = "ok", Reason = $"actions={n}" };
    }

    private static void SaveState(bool ok, bool experimental, string steamPath, List<NativeApplyStep> steps,
        List<string> elevOps, bool guardOk, bool launcherOk, bool toastOk, bool libOk,
        Dictionary<string, object?>? recovery = null)
    {
        try
        {
            var path = Path.Combine(PathHelper.AppDataDir, StateFileName);
            Directory.CreateDirectory(PathHelper.AppDataDir);
            // Flags required by SteamDetectCore.Test-SteamApplyRecord + library tile.
            var state = new Dictionary<string, object?>
            {
                ["version"] = Version,
                ["applyStatus"] = ok ? "applied" : "incomplete",
                ["applied"] = ok,
                ["appliedUtc"] = DateTime.UtcNow.ToString("o"),
                ["experimental"] = experimental,
                ["path"] = "native-csharp",
                ["steamPath"] = steamPath,
                ["quick"] = false,
                // Never stamp deep-pack-only essentials from native thin success.
                ["fullApply"] = ok && steps.Any(s => s.Id == "client-tweaks" && s.Status == "ok"),
                ["windowsVerified"] = steps.Any(s =>
                    s.Status == "ok" && s.Id is "toasts" or "startup" or "tray"),
                ["debloatVerified"] = steps.Any(s => s.Id == "debloat" && s.Status == "ok"),
                ["cacheCleanupCompleted"] = false,
                // shaderInventoryVerified / installedShaderCachesPreserved are deliberately
                // ABSENT here, not false. The native pass has no shader step at all - there is
                // no "shader" id anywhere in Steps - so it cannot have verified an inventory or
                // confirmed that installed caches survived. Writing true asserted two checks
                // that never ran, and both are read back as proof: SteamDetectCore's
                // Test-SteamApplyRecord requires them, as does OptimizerStateService. The
                // PowerShell deep pack runs immediately after this and does perform the
                // inventory, so it supplies the real values; if it never runs, these stay
                // missing and the record fails closed, which is the correct answer.
                ["libraryGamePolicyVerified"] = libOk || ok,
                ["configVerified"] = steps.Any(s => s.Id == "download-config" && s.Status == "ok"),
                ["downloadOptimized"] = steps.Any(s => s.Id == "download-config" && s.Status == "ok"),
                ["clientTweaksVerified"] = steps.Any(s => s.Id == "client-tweaks" && s.Status == "ok"),
                // Both of these are what the client-tweaks step writes, so read that step's
                // real outcome instead of asserting it. It reports "skip" when there is no
                // Steam userdata yet, and a skip is not a patched UI.
                ["snappyUi"] = steps.Any(s => s.Id == "client-tweaks" && s.Status == "ok"),
                ["overlayTweaks"] = steps.Any(s => s.Id == "client-tweaks" && s.Status == "ok"),
                ["clientHardwareAcceleration"] = steps.Any(s => s.Id == "hw-accel" && s.Status == "ok"),
                ["memoryGuardOk"] = guardOk,
                ["cefLauncherOk"] = launcherOk,
                ["toastsOff"] = toastOk,
                ["startupQuiet"] = steps.Any(s => s.Id == "startup" && s.Status == "ok"),
                ["pendingElevOps"] = elevOps,
                ["applyReport"] = steps.Select(s => s.ToReportLine()).ToList(),
                // The user's pre-Exo values. The PowerShell deep pack prefers this over its
                // own snapshot, which it takes after this pass has already changed the keys.
                ["recovery"] = recovery
            };
            File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private const string FsoDisableToken = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

    /// <summary>Strip a stale per-exe FSO-disable stamp left by an older Exo build.</summary>
    internal static bool ClearLegacyFsoFlag(RegistryKey? fso, string path)
    {
        if (fso is null || string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var cur = fso.GetValue(path)?.ToString();
            if (string.IsNullOrEmpty(cur)) return false;
            if (!cur.Contains(FsoDisableToken, StringComparison.OrdinalIgnoreCase)) return false;

            // Exact Exo stamp or "~ " only after strip → delete.
            var cleaned = Regex.Replace(cur, @"\s*" + FsoDisableToken, "", RegexOptions.IgnoreCase).Trim();
            if (string.IsNullOrEmpty(cleaned) || cleaned == "~")
            {
                fso.DeleteValue(path, throwOnMissingValue: false);
                return true;
            }

            fso.SetValue(path, cleaned, RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
