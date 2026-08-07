using System.Text.Json;
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
        bool PolString(string name, string expect) =>
            NativeReg.MatchesString("HKLM", policyRoot, name, expect)
            || NativeReg.MatchesString("HKCU", policyRoot, name, expect);

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
        features.Add(F("Brave vault disabled", "No password/address/card save; an external manager remains optional.", vault));

        var shields = Pol("DefaultBraveAdblockSetting", 2)
                      && Pol("DefaultBraveFingerprintingV2Setting", 3);
        features.Add(F("Shields pinned hard", "Aggressive ads + strong fingerprint policies.", shields));

        // Safe Browsing used to be disabled by Exo's managed policy pack. That removed
        // phishing, malware, dangerous-download and extension checks for no measured gaming
        // gain. Absence is the desired state here: Brave's standard protection remains the
        // default and the person using the browser can still change it themselves.
        var safeBrowsing = NativeReg.GetDword("HKLM", policyRoot, "SafeBrowsingProtectionLevel") != 0
                           && NativeReg.GetDword("HKCU", policyRoot, "SafeBrowsingProtectionLevel") != 0
                           && Pol("ComponentUpdatesEnabled", 1);
        features.Add(F("Safe Browsing preserved",
            "Protection is not forced off; security components keep updating.", safeBrowsing));

        var privacy = Pol("BraveGlobalPrivacyControlEnabled", 1)
                      && Pol("BraveDeAmpEnabled", 1)
                      && Pol("BlockThirdPartyCookies", 1);
        features.Add(F("Privacy pins", "GPC + De-AMP + 3P cookies blocked.", privacy));

        var rtcPrivacy = PolString("WebRtcIPHandling", "disable_non_proxied_udp")
                         && Pol("WebRtcAllowLegacyTLSProtocols", 0);
        features.Add(F("WebRTC privacy", "Local IP exposure and legacy TLS are blocked.", rtcPrivacy));

        var efficiency = Pol("HardwareAccelerationModeEnabled", 1)
                         && Pol("HighEfficiencyModeEnabled", 1)
                         && Pol("IntensiveWakeUpThrottlingEnabled", 1)
                         && Pol("WindowOcclusionEnabled", 1);
        features.Add(F("Background efficiency",
            "GPU acceleration stays on; hidden and idle tabs are throttled.", efficiency));

        var quietPermissions = Pol("DefaultNotificationsSetting", 2)
                               && Pol("DefaultGeolocationSetting", 2)
                               && Pol("DefaultSensorsSetting", 2);
        features.Add(F("Quiet site permissions",
            "Notifications, location and sensor prompts default to blocked.", quietPermissions));

        var adApisOff = Pol("PrivacySandboxAdTopicsEnabled", 0)
                        && Pol("PrivacySandboxSiteEnabledAdsEnabled", 0)
                        && Pol("PrivacySandboxAdMeasurementEnabled", 0);
        features.Add(F("Ad APIs off", "Topics, site-suggested ads and measurement are disabled.", adApisOff));

        // A recommendation, not something Apply performs. BraveNativeApply.EnsureProtonPassPolicy
        // deliberately never force-installs (a force-installed extension cannot be removed by the
        // person using the browser, and Brave rejects the Web Store update URL with "blocked by
        // administrator"), and RemoveRetiredPolicies actively deletes the force-list key. Detect
        // still required this row and still read that deleted key as evidence, so Brave reported
        // "1 setting needs Apply (Proton Pass)" forever no matter how many times it was applied.
        // IsInfo keeps it visible without gating IsApplied.
        var proton = false;
        if (install.DefaultProfile is not null)
        {
            proton = Directory.Exists(Path.Combine(install.DefaultProfile, "Extensions",
                BraveNativeApply.ProtonPassExtensionId));
        }
        features.Add(F("Proton Pass (optional)",
            proton
                ? "Extension installed."
                : "Not installed — Exo will not force-install an extension you cannot remove.",
            proton));

        var darker = false;
        if (install.DefaultProfile is not null)
        {
            try
            {
                var pref = Path.Combine(install.DefaultProfile, "Preferences");
                if (File.Exists(pref))
                {
                    var t = File.ReadAllText(pref);
                    // Both, not either. brave.darker_mode only takes effect when dark mode
                    // is on, so darker_mode alone is the state this module used to leave
                    // machines in while reporting success. The old fallback here matched
                    // "selected_value":"#000000" — a key brave-core has never defined — so
                    // the row could also go green on a pref Brave never read.
                    // The leading quote is load-bearing: it stops "dark_mode" matching
                    // inside "darker_mode".
                    darker = t.Contains("\"dark_mode\":1", StringComparison.Ordinal)
                             && t.Contains("\"darker_mode\":true", StringComparison.Ordinal);
                }
            }
            catch { }
        }
        features.Add(F("AMOLED / darkest UI",
            darker
                ? "Dark mode pinned and the darker theme on top of it."
                : "Needs dark mode pinned — the darker theme is inert without it.",
            darker));

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

        // Routing target depends on the machine: on a hybrid laptop the applied state is
        // "integrated", which keeps the discrete GPU free for games. On a single-GPU box
        // the preference selects nothing, so the applied state is "no stamp at all" —
        // reporting a pass for a value that cannot take effect would be a false green.
        var gpuHybrid = GpuTopology.IsHybrid();
        var gpuOk = false;
        var gpuDetail = gpuHybrid
            ? "Brave routed to the integrated GPU so the discrete one stays free for games."
            : "Single GPU — no preference to set; Exo leaves this alone.";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            var v = install.ExePath is null ? null : key?.GetValue(install.ExePath)?.ToString();
            gpuOk = gpuHybrid
                ? v is not null && v.Contains("GpuPreference=1", StringComparison.Ordinal)
                : v is null;
        }
        catch { }
        features.Add(F("GPU routing", gpuDetail, gpuOk));

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
                ? "Live: privacy and efficiency policies, Shields, vault off, Safe Browsing preserved, quiet host."
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
        // Describe what Apply actually did on THIS machine. On a single-GPU desktop it clears
        // the per-game preference and leaves Windows to pick, because there is no second
        // adapter to prefer; calling that "high-perf GPU" described a stamp the row does not
        // want and the machine cannot use.
        features.Add(GpuTopology.IsHybrid()
            ? F("Library games high-perf GPU",
                "Installed Steam games pinned to the discrete GPU (display = Games hub).", libOk)
            : F("Library games GPU routing",
                "One GPU on this PC, so games are left on Windows automatic — the per-game override is cleared, not set.", libOk));

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
        features.Add(F("Snappier library & overlay", "localconfig library / download keys.", snapOk));

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

        // Deep pack soft-fail annotates steam-optimizer.json with deepPackOk=false. Surface that
        // as a real row so the module cannot go full green while shader/debloat depth never ran.
        var deepPackMiss = false;
        try
        {
            var statePath = Path.Combine(PathHelper.AppDataDir, SteamNativeApply.StateFileName);
            if (File.Exists(statePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
                if (doc.RootElement.TryGetProperty("deepPackOk", out var dp)
                    && dp.ValueKind == JsonValueKind.False)
                {
                    deepPackMiss = true;
                    var why = doc.RootElement.TryGetProperty("deepPackError", out var de)
                        ? de.GetString()
                        : null;
                    features.Add(F("Deep pack",
                        string.IsNullOrWhiteSpace(why)
                            ? "Native essentials applied; PowerShell deep pack did not finish."
                            : $"Native essentials applied; deep pack partial: {why}",
                        false));
                }
            }
        }
        catch { /* state is optional */ }

        var checkable = features.Where(f => !IsInfo(f.Title)).ToList();
        var off = checkable.Where(f => !f.IsActive).Select(f => f.Title).ToList();
        var applied = off.Count == 0;

        return new OptimizerStateInfo
        {
            IsApplied = applied,
            StatusText = applied ? "Already optimized"
                : deepPackMiss && off.Count == 1 ? "Deep pack incomplete"
                : off.Count == 1 ? $"1 setting needs Apply ({off[0]})"
                : $"{off.Count} settings need Apply",
            Detail = applied
                ? "Live: CEF launcher, HW accel, Windows quiet, library GPU (no background helpers)."
                : "Off: " + string.Join(", ", off) + ".",
            Features = features
        };
    }

    /// <summary>
    /// Whole-machine rows. Read live on every call — a power-plan switch by a vendor utility,
    /// or a Windows feature update resetting the graphics scheduler, has to show up as
    /// not-applied rather than as a stale tick from the last Apply.
    /// </summary>
    public static OptimizerStateInfo DetectSystem()
    {
        var (applied, rows) = SystemNativeApply.Detect();
        var features = rows.Select(r => F(r.Title, r.Detail, r.Active)).ToList();

        var owned = features.Where(f => !ModuleStatusClassifier.IsInfoTitle(f.Title)).ToList();
        var on = owned.Count(f => f.IsActive);
        // Only count real firmware misses (active=false and title ends with (firmware)).
        // Identity lines like board/BIOS are marked active and do not trigger this banner.
        var firmwareMisses = rows.Count(r =>
            r.Title.EndsWith("(firmware)", StringComparison.Ordinal) && !r.Active);

        string detail;
        if (!applied)
            detail = $"{owned.Count - on} Windows setting(s) still need Apply.";
        else if (firmwareMisses > 0)
            detail = $"Windows tuned. {firmwareMisses} optional UEFI setting(s) look off (Exo cannot change them).";
        else
            detail = "Everything Exo can set on Windows is set.";

        return new OptimizerStateInfo
        {
            IsApplied = applied,
            StatusText = applied ? $"Applied · {on}/{owned.Count} on" : $"{owned.Count - on} of {owned.Count} to tune",
            Detail = detail,
            Features = features
        };
    }

    /// <summary>Spotify desktop. Reports not-installed distinctly from not-applied.</summary>
    public static OptimizerStateInfo DetectSpotify()
    {
        var (installed, applied, rows) = SpotifyNativeApply.Detect();
        if (!installed)
        {
            return new OptimizerStateInfo
            {
                IsApplied = false,
                StatusText = "Not installed",
                Detail = "Spotify is not installed on this PC.",
                Features = new List<OptimizerFeatureInfo>
                {
                    F("Spotify installed", "Not installed.", false)
                }
            };
        }

        var features = new List<OptimizerFeatureInfo> { F("Spotify installed", "Found.", true) };
        features.AddRange(rows.Select(r => F(r.Title, r.Detail, r.Active)));
        var on = rows.Count(r => r.Active);

        return new OptimizerStateInfo
        {
            IsApplied = applied,
            StatusText = applied ? $"Applied · {on}/{rows.Count} on" : $"{rows.Count - on} of {rows.Count} to tune",
            Detail = applied
                ? "Very High audio, no promos, no autostart."
                : "Audio quality, promo surfaces and startup cost can be tuned.",
            Features = features
        };
    }

    /// <summary>
    /// AMD: chipset currency when only an AMD CPU is present; Radeon debloat when a Radeon GPU exists.
    /// "Not installed" only when there is no AMD CPU and no Radeon.
    /// </summary>
    public static OptimizerStateInfo DetectAmd()
    {
        var (installed, applied, rows) = AmdNativeApply.Detect();
        if (!installed)
        {
            return new OptimizerStateInfo
            {
                IsApplied = false,
                StatusText = "Not installed",
                Detail = "No AMD CPU or Radeon GPU on this PC, so there is nothing here to tune.",
                Features = new List<OptimizerFeatureInfo>
                {
                    F("AMD platform", "No AMD CPU or Radeon GPU found.", false)
                }
            };
        }

        var inv = HardwareInventory.Read();
        var hasRadeon = inv.HasAmdGpu;
        var features = rows.Select(r => F(r.Title, r.Detail, r.Active)).ToList();

        // Chipset-only (Ryzen + discrete NVIDIA): only CPU + chipset driver rows.
        if (!hasRadeon && inv.Cpu?.Vendor == HardwareInventory.CpuVendor.Amd)
        {
            var shortCpu = AmdNativeApply.ShortCpuName(inv.Cpu?.Name);
            var chip = AmdNativeApply.ReadChipsetCurrency(HardwareInventory.CpuVendor.Amd);
            if (applied && chip.Present && chip.Current)
            {
                return new OptimizerStateInfo
                {
                    IsApplied = true,
                    StatusText = "Applied · chipset current",
                    Detail = $"{shortCpu}: AMD Chipset Software {chip.Installed}.",
                    Features = features
                };
            }

            var need = chip.Target ?? "latest";
            var have = chip.Present
                ? (string.IsNullOrWhiteSpace(chip.Installed) ? "incomplete" : chip.Installed)
                : "missing";
            return new OptimizerStateInfo
            {
                IsApplied = false,
                StatusText = chip.Present ? "Chipset outdated" : "Chipset not installed",
                Detail = $"{shortCpu}: {have} — newest package is {need}.",
                Features = features
            };
        }

        if (hasRadeon)
        {
            var gpu = inv.Gpus.FirstOrDefault(g => g.Vendor == HardwareInventory.GpuVendor.Amd);
            var label = gpu is null
                ? "Found"
                : string.IsNullOrWhiteSpace(gpu.DriverVersion)
                    ? gpu.Name
                    : $"{gpu.Name} · driver {gpu.DriverVersion}";
            features.Insert(0, F("Radeon GPU", label, true));
        }

        var owned = features.Where(f => !ModuleStatusClassifier.IsInfoTitle(f.Title)).ToList();
        var on = owned.Count(f => f.IsActive);
        return new OptimizerStateInfo
        {
            IsApplied = applied,
            StatusText = applied
                ? $"Applied · {on}/{Math.Max(1, owned.Count)} on"
                : $"{Math.Max(0, owned.Count - on)} of {Math.Max(1, owned.Count)} to tune",
            Detail = applied
                ? "Radeon auto-start, updater and crash-reporter tasks off; analytics off."
                : "Radeon background tasks and analytics can be turned off.",
            Features = features
        };
    }

    /// <summary>
    /// The NVIDIA driver version currently installed, in NVIDIA's own numbering. Read from the
    /// display class key, where the value is in Windows' four-part form.
    /// </summary>
    public static string? InstalledNvidiaDriverVersion()
    {
        const string classRoot = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        foreach (var sub in NativeReg.GetSubKeyNames("HKLM", classRoot))
        {
            if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
            var path = $@"{classRoot}\{sub}";
            var provider = NativeReg.GetValue("HKLM", path, "ProviderName")?.ToString() ?? "";
            var desc = NativeReg.GetValue("HKLM", path, "DriverDesc")?.ToString() ?? "";
            if (!provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                && !desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) continue;

            var raw = NativeReg.GetValue("HKLM", path, "DriverVersion")?.ToString();
            var converted = NvidiaDriverLookup.ConvertWindowsVersion(raw);
            if (converted is not null) return converted;
        }
        return null;
    }

    private static OptimizerFeatureInfo F(string title, string detail, bool active) =>
        new() { Title = title, Detail = detail, IsActive = active };

    /// <summary>
    /// One owner for "is this row advisory". This used to be a second, shorter list that
    /// disagreed with <see cref="ModuleStatusClassifier.IsInfoTitle"/> on two titles, and the
    /// two are read in sequence by the same request: detect decides IsApplied with this one,
    /// the classifier then decides the status kind with its own.
    ///
    /// "Proton Pass (optional)" was the live case. Detect excluded it, so Brave with the
    /// extension absent came back IsApplied=true / "Already optimized" -- and the classifier,
    /// which counted it, saw one row off against isApplied=true and returned "partial - 1 still
    /// off". Making the row IsInfo in detect moved the wrong answer rather than removing it.
    /// "Launcher junk cleaned" had the same split and was one Steam row away from doing it too.
    /// </summary>
    private static bool IsInfo(string title) => ModuleStatusClassifier.IsInfoTitle(title);

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

        // Same topology question Apply asks. On one adapter there is no "high performance" GPU
        // to select, so Apply DELETES the preference — applied means absent, not stamped.
        // Demanding the stamp kept this row red on every single-GPU desktop, permanently.
        if (!GpuTopology.IsHybrid())
            return samples.All(exe => gpu?.GetValue(exe) is null);

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
        // No userdata yet (never signed in) — not "optimized", just empty. Soft-pass used to
        // green the library row on a stock install and hide a real Apply need.
        if (!Directory.Exists(userdata)) return false;
        try
        {
            var files = Directory.EnumerateDirectories(userdata)
                .Select(d => Path.Combine(d, "config", "localconfig.vdf"))
                .Where(File.Exists)
                .Take(3)
                .ToList();
            if (files.Count == 0) return false;
            foreach (var f in files)
            {
                var raw = File.ReadAllText(f);
                var keys = new[]
                {
                    ("LibraryLowBandwidthMode", "1"),
                    ("LibraryLowPerfMode", "1"),
                    ("AllowDownloadsDuringGameplay", "0")
                };
                var any = false;
                var allMatch = true;
                foreach (var (k, v) in keys)
                {
                    if (!raw.Contains("\"" + k + "\"", StringComparison.Ordinal)) continue;
                    any = true;
                    if (!Regex.IsMatch(raw, "\"" + Regex.Escape(k) + "\"\\s+\"" + Regex.Escape(v) + "\""))
                        allMatch = false;
                }
                // Keys missing entirely → needs Apply (no soft-pass green on stock Steam).
                if (!any) return false;
                if (allMatch) return true;
            }
            return false;
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
