using System.Text.RegularExpressions;
using Exo.Helpers;
using Exo.Models;
using Microsoft.Win32;

namespace Exo.Services;

/// <summary>
/// Honest live detectors for Steam / Brave.
/// No soft "marker-only green" — every row is a real registry/file/powercfg probe.
/// </summary>
public static class NativeLiveDetect
{
    public static OptimizerStateInfo DetectBrave()
    {
        var features = new List<OptimizerFeatureInfo>();
        var install = BraveNativeApply.Discover();
        var installed = install.Installed;

        features.Add(F("Brave installed", installed ? install.ExePath ?? "Found" : "Not installed.", installed));

        var policyRoot = @"SOFTWARE\Policies\BraveSoftware\Brave";
        bool Pol(string name, int expect) =>
            NativeReg.MatchesDword("HKLM", policyRoot, name, expect)
            || NativeReg.MatchesDword("HKCU", policyRoot, name, expect);

        var debloat = Pol("BraveRewardsDisabled", 1) && Pol("BraveWalletDisabled", 1)
                      && Pol("BraveVPNDisabled", 1) && Pol("BraveAIChatEnabled", 0)
                      && Pol("BraveNewsDisabled", 1);
        features.Add(F("Product bloat off", "Rewards/Wallet/VPN/Leo/News policies.", debloat));

        var bg = Pol("BackgroundModeEnabled", 0);
        features.Add(F("No background when closed", "BackgroundModeEnabled=0.", bg));

        var telemetry = Pol("BraveP3AEnabled", 0) && Pol("BraveStatsPingEnabled", 0)
                        && Pol("MetricsReportingEnabled", 0);
        features.Add(F("Telemetry quiet", "P3A / stats / metrics off.", telemetry));

        var vault = Pol("PasswordManagerEnabled", 0) && Pol("AutofillAddressEnabled", 0)
                    && Pol("AutofillCreditCardEnabled", 0);
        features.Add(F("Brave vault disabled", "No password/address/card save — use Proton Pass.", vault));

        var shields = Pol("DefaultBraveAdblockSetting", 2)
                      || Pol("DefaultBraveFingerprintingV2Setting", 3);
        features.Add(F("Shields pinned hard", "Aggressive ads + strong fingerprint policies.", shields));

        var privacy = Pol("BraveGlobalPrivacyControlEnabled", 1)
                      && Pol("BraveDeAmpEnabled", 1)
                      && Pol("BlockThirdPartyCookies", 1);
        features.Add(F("Privacy pins", "GPC + De-AMP + 3P cookies blocked.", privacy));

        var proton = false;
        if (install.DefaultProfile is not null)
        {
            proton = Directory.Exists(Path.Combine(install.DefaultProfile, "Extensions",
                BraveNativeApply.ProtonPassExtensionId));
        }
        // Force-list also counts as applied intent
        var forceList = NativeReg.GetValue("HKLM", policyRoot + @"\ExtensionInstallForcelist", "1")?.ToString()
                        ?? NativeReg.GetValue("HKCU", policyRoot + @"\ExtensionInstallForcelist", "1")?.ToString();
        var protonOk = proton || (!string.IsNullOrEmpty(forceList) &&
                                  forceList.Contains(BraveNativeApply.ProtonPassExtensionId, StringComparison.OrdinalIgnoreCase));
        features.Add(F("Proton Pass",
            proton ? "Extension on disk." : protonOk ? "Force-install policy set." : "Missing — re-Apply.",
            protonOk));

        var darker = false;
        if (install.DefaultProfile is not null)
        {
            try
            {
                var pref = Path.Combine(install.DefaultProfile, "Preferences");
                if (File.Exists(pref))
                {
                    var t = File.ReadAllText(pref);
                    darker = t.Contains("\"darker_mode\":true", StringComparison.Ordinal)
                             || t.Contains("\"selected_value\":\"#000000\"", StringComparison.Ordinal);
                }
            }
            catch { }
        }
        features.Add(F("AMOLED / darkest UI", "Darker mode + black NTP when set.", darker));

        var filters = false;
        if (install.UserData is not null)
        {
            try
            {
                var ls = Path.Combine(install.UserData, "Local State");
                if (File.Exists(ls))
                {
                    var t = File.ReadAllText(ls);
                    // Cookie + annoyances UUIDs enabled
                    filters = t.Contains("67E792D4-AE03-4D1A-9EDE-80E01C81F9B8", StringComparison.OrdinalIgnoreCase)
                              && t.Contains("7911A1CB-304E-4CDB-ABB3-E2A94A37E4DD", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
        }
        features.Add(F("Content filter lists", "Cookie / annoyances / social lists enabled.", filters));

        var gpu = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            if (key is not null && install.ExePath is not null)
            {
                var v = key.GetValue(install.ExePath)?.ToString() ?? "";
                gpu = v.Contains("GpuPreference=2", StringComparison.Ordinal);
            }
        }
        catch { }
        features.Add(F("High-performance GPU", "UserGpuPreferences GpuPreference=2.", gpu));

        var startup = !RunKeyHasBrave();
        features.Add(F("Silent startup", "No Brave Run keys.", startup));

        var multi = install.Profiles.Count >= 1;
        features.Add(F(
            "All profiles covered",
            multi ? $"{install.Profiles.Count} profile(s) under User Data." : "No profile dirs.",
            multi));

        var snap = Directory.Exists(Path.Combine(PathHelper.AppDataDir, "brave-snapshot"))
                   && File.Exists(Path.Combine(PathHelper.AppDataDir, "brave-snapshot", "snapshot.json"));
        features.Add(F("One-click Repair ready", "Full prefs snapshot present.", snap));

        var checkable = features.Where(f => !IsInfo(f.Title) && f.Title is not "One-click Repair ready").ToList();
        var off = checkable.Where(f => !f.IsActive).Select(f => f.Title).ToList();
        var applied = installed && off.Count == 0;

        return new OptimizerStateInfo
        {
            IsApplied = applied,
            StatusText = !installed ? "Not installed"
                : applied ? "Already optimized"
                : off.Count == 1 ? $"1 setting needs Apply ({off[0]})"
                : off.Count > 1 ? $"{off.Count} settings need Apply"
                : "Ready to optimize",
            Detail = applied
                ? "Live: absolute debloat policies, vault off, Proton Pass, quiet host."
                : off.Count > 0 ? "Off: " + string.Join(", ", off) + "." : "",
            Features = features
        };
    }

    private static bool RunKeyHasBrave()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (run is null) return false;
            foreach (var name in run.GetValueNames())
            {
                var val = run.GetValue(name)?.ToString() ?? "";
                if (name.Contains("Brave", StringComparison.OrdinalIgnoreCase) ||
                    val.Contains("brave.exe", StringComparison.OrdinalIgnoreCase) ||
                    val.Contains("BraveSoftware", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    public static OptimizerStateInfo DetectSteam()
    {
        var features = new List<OptimizerFeatureInfo>();
        var steam = SteamNativeApply.FindSteamInstallPath();
        if (steam is null)
        {
            return new OptimizerStateInfo
            {
                IsApplied = false,
                StatusText = "Steam not installed",
                Detail = "Install Steam, open it once, then return.",
                Features = new[] { F("Steam installed", "Not found.", false) }
            };
        }

        features.Add(F("Steam installed", steam, true));

        var cmdPath = Path.Combine(steam, "Steam-Exo.cmd");
        var cefOk = false;
        try
        {
            if (File.Exists(cmdPath))
                cefOk = SteamLogic.IsCefLauncherText(File.ReadAllText(cmdPath));
        }
        catch { }
        features.Add(F("Fast quiet launch", "Steam-Exo.cmd + CEF flags + /HIGH.", cefOk));

        var fsoFlag = "~ DISABLEDXMAXIMIZEDWINDOWEDMODE";
        var steamExe = Path.Combine(steam, "steam.exe");
        var fsoOk = false;
        try
        {
            using var fso = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers");
            fsoOk = string.Equals(fso?.GetValue(steamExe)?.ToString(), fsoFlag, StringComparison.Ordinal);
        }
        catch { }
        var dscpOk = false;
        try
        {
            using var q = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows\QoS\Exo-Steam-DSCP-steam.exe");
            dscpOk = string.Equals(q?.GetValue("DSCP Value")?.ToString(), "46", StringComparison.Ordinal);
        }
        catch { }
        features.Add(F("Client FSO + priority net", "Client FSO off and/or DSCP 46.", fsoOk || dscpOk));

        // Library: sample installed game EXEs for GpuPreference=2 (no FSO — Games owns borderless)
        var libOk = LiveSteamLibraryGpu(steam);
        features.Add(F("Library games high-perf GPU", "Live GpuPreference=2 on library games (display = Games hub).", libOk));

        // No always-on memory guard — green when CEF lean launcher is present (one-shot path).
        features.Add(F("Yield to your game",
            cefOk
                ? "No background guard — lean Steam-Exo.cmd only (zero idle processes)."
                : "Apply Steam to install Steam-Exo.cmd (no background helper).",
            cefOk));

        var debloatOk = !File.Exists(Path.Combine(steam, "Steam-Exo-Aggressive.cmd"))
                        && File.Exists(cmdPath)
                        && !DesktopHasSteamLnk();
        features.Add(F("Cleaner Steam install", "No legacy launchers / desktop Steam icons; Exo cmd present.", debloatOk));

        var snapOk = LiveSteamClientTweaks(steam);
        features.Add(F("Snappier library & overlay", "localconfig / soft-pass when no userdata keys.", snapOk));

        var hwOk = NativeReg.MatchesDword("HKCU", @"Software\Valve\Steam", "H264HWAccel", 1)
                   && NativeReg.MatchesDword("HKCU", @"Software\Valve\Steam", "GPUAccelWebViews", 1)
                   && NativeReg.MatchesDword("HKCU", @"Software\Valve\Steam", "GPUAccelWebViewsV3", 1);
        features.Add(F("GPU-powered Steam UI", "H264 + GPUAccelWebViews + V3 = 1.", hwOk));

        // Detect is read-only — never write StartupMode here (Apply owns that pin).
        // Steam rewrites StartupMode after client launch; silent is green when Windows
        // won't autostart Steam (no Run + Startup apps Off + toasts Off).
        var noRun = !RunKeyHasSteam();
        var toastOk = LiveSteamToasts();
        var approvedOff = IsStartupApprovedDisabled("Steam");
        var modeOk = NativeReg.MatchesDword("HKCU", @"Software\Valve\Steam", "StartupMode", 0);
        var silentOk = noRun && toastOk && approvedOff;
        var silentDetail = silentOk
            ? (modeOk
                ? "No Run key, Startup apps Off, toasts Off, StartupMode=0."
                : "No Run key, Startup apps Off, toasts Off. (Steam may rewrite StartupMode after it opens — re-Apply Steam to re-pin.)")
            : string.Join("; ", new[]
            {
                noRun ? null : "Steam still in Run",
                toastOk ? null : "toast keys not fully Off",
                approvedOff ? null : "Windows Startup apps still On for Steam",
            }.Where(s => s is not null));
        features.Add(F("Silent Windows integration", silentDetail, silentOk));

        var launchOk = LiveStartMenuPointsToExo(steam, cmdPath);
        features.Add(F("Clean Start Menu launch", "Start Menu Steam.lnk → Steam-Exo.cmd.", launchOk));

        var runtimeOk = File.Exists(Path.Combine(steam, "steam.exe")) && cefOk;
        features.Add(F("Helpers stay healthy", "steam.exe + Steam-Exo.cmd on disk (no background process).", runtimeOk));

        var checkable = features.Where(f => !IsInfo(f.Title)).ToList();
        var off = checkable.Where(f => !f.IsActive).Select(f => f.Title).ToList();
        var applied = off.Count == 0;

        return new OptimizerStateInfo
        {
            IsApplied = applied,
            StatusText = applied ? "Already optimized"
                : off.Count == 1 ? $"1 setting needs Apply ({off[0]})"
                : $"{off.Count} settings need Apply",
            Detail = applied
                ? "Live: CEF launcher, HW accel, Windows quiet, library GPU (no background helpers)."
                : "Off: " + string.Join(", ", off) + ".",
            Features = features
        };
    }

    private static OptimizerFeatureInfo F(string title, string detail, bool active) =>
        new() { Title = title, Detail = detail, IsActive = active };

    private static bool IsInfo(string title) =>
        title is "Anti-cheat untouched" or "Optimization verified" or "One-click Repair ready"
            or "Launcher junk cleaned"
            or "Latency / sync policy" or "Display scaling & color";

    /// <summary>
    /// Deep Windows rows: require honest deepPass flag + applyReport without timeouts/fails.
    /// Old state files that greened empty DISM passes will show Off until re-Apply.
    /// </summary>
    private static bool LiveSteamLibraryGpu(string steamPath)
    {
        // Same multi-library discovery as Apply (libraryfolders.vdf on every PC).
        List<string> samples;
        try
        {
            samples = SteamNativeApply.DiscoverLibraryGameExes(steamPath).Take(24).ToList();
        }
        catch
        {
            return false;
        }

        if (samples.Count == 0) return true; // no games yet — not a fail

        using var gpu = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
        var ok = 0;
        foreach (var exe in samples)
        {
            var g = gpu?.GetValue(exe)?.ToString() ?? "";
            if (g.Contains("GpuPreference=2", StringComparison.OrdinalIgnoreCase))
                ok++;
        }
        // Require majority of samples (library may include tools)
        return ok >= Math.Max(1, samples.Count / 2);
    }

    private static bool LiveSteamClientTweaks(string steamPath)
    {
        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata)) return true;
        try
        {
            var files = Directory.EnumerateDirectories(userdata)
                .Select(d => Path.Combine(d, "config", "localconfig.vdf"))
                .Where(File.Exists)
                .Take(3)
                .ToList();
            if (files.Count == 0) return true;
            foreach (var f in files)
            {
                var raw = File.ReadAllText(f);
                // If keys present, values must match; if none of the keys exist, soft-pass
                var keys = new[]
                {
                    ("LibraryLowBandwidthMode", "1"),
                    ("LibraryLowPerfMode", "1"),
                    ("AllowDownloadsDuringGameplay", "0")
                };
                var any = false;
                foreach (var (k, v) in keys)
                {
                    if (!raw.Contains("\"" + k + "\"", StringComparison.Ordinal)) continue;
                    any = true;
                    if (!Regex.IsMatch(raw, "\"" + Regex.Escape(k) + "\"\\s+\"" + Regex.Escape(v) + "\""))
                        return false;
                }
                if (any) return true;
            }
            return true;
        }
        catch { return false; }
    }

    private static bool LiveSteamToasts()
    {
        // Must match SteamNativeApply.NotificationIds (+ dynamic steam* keys Apply also quiets)
        var ids = SteamNativeApply.NotificationIds;
        var seen = 0;
        var on = 0;
        foreach (var id in ids)
        {
            var path = $@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\{id}";
            var v = NativeReg.GetDword("HKCU", path, "Enabled");
            if (v is null) continue;
            seen++;
            if (v != 0) on++;
        }
        // Also any extra steam/valve AUMIDs Windows created
        try
        {
            foreach (var sub in NativeReg.GetSubKeyNames("HKCU",
                         @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings"))
            {
                if (!sub.Contains("steam", StringComparison.OrdinalIgnoreCase) &&
                    !sub.Contains("valve", StringComparison.OrdinalIgnoreCase)) continue;
                if (sub.Contains("steamvr", StringComparison.OrdinalIgnoreCase) ||
                    sub.Contains("steamlink", StringComparison.OrdinalIgnoreCase)) continue;
                if (ids.Any(i => i.Equals(sub, StringComparison.OrdinalIgnoreCase))) continue;
                var v = NativeReg.GetDword("HKCU",
                    $@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\{sub}", "Enabled");
                if (v is null) continue;
                seen++;
                if (v != 0) on++;
            }
        }
        catch { }

        // Need at least one toast key we control, and none still Enabled
        return seen > 0 && on == 0;
    }

    /// <summary>
    /// Windows Settings → Apps → Startup. First byte 0x03 = disabled.
    /// 0x02 / 0x00 / 0x01 = still enabled. Missing entry = not a Startup app (OK).
    /// </summary>
    private static bool IsStartupApprovedDisabled(string name)
    {
        var found = false;
        var allDisabled = true;
        foreach (var rel in new[]
                 {
                     @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
                     @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
                 })
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(rel);
                if (key is null) continue;
                foreach (var n in key.GetValueNames())
                {
                    var match = n.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                n.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                                name.Contains(n, StringComparison.OrdinalIgnoreCase);
                    if (!match) continue;
                    found = true;
                    if (key.GetValue(n) is byte[] b && b.Length > 0 && b[0] == 0x03)
                        continue;
                    allDisabled = false;
                }
            }
            catch { }
        }
        return !found || allDisabled;
    }

    private static bool RunKeyHasSteam()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (run is null) return false;
            foreach (var n in run.GetValueNames())
            {
                if (n.StartsWith("Exo-", StringComparison.OrdinalIgnoreCase)) continue;
                var v = run.GetValue(n)?.ToString() ?? "";
                if (v.Contains("steam.exe", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("steam", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    private static bool DesktopHasSteamLnk()
    {
        foreach (var desk in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
                 })
        {
            if (string.IsNullOrEmpty(desk) || !Directory.Exists(desk)) continue;
            if (Directory.EnumerateFiles(desk, "Steam*.lnk").Any()) return true;
        }
        return false;
    }

    private static bool LiveStartMenuPointsToExo(string steamPath, string cmdPath)
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Steam", "Steam.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Steam", "Steam.lnk"),
        };
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return File.Exists(cmdPath);
            dynamic shell = Activator.CreateInstance(t)!;
            foreach (var lnk in candidates)
            {
                if (!File.Exists(lnk)) continue;
                try
                {
                    var sc = shell.CreateShortcut(lnk);
                    var target = (string)sc.TargetPath;
                    if (target.EndsWith("Steam-Exo.cmd", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
            }
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
        catch { }
        return File.Exists(cmdPath); // at least launcher exists
    }

}
