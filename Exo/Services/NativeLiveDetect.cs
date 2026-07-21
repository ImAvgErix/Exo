using System.Text.RegularExpressions;
using Exo.Helpers;
using Exo.Models;
using Microsoft.Win32;

namespace Exo.Services;

/// <summary>
/// Honest live detectors for Steam / Windows / Riot / Epic.
/// No soft "marker-only green" — every row is a real registry/file/powercfg probe.
/// </summary>
public static class NativeLiveDetect
{
    public static OptimizerStateInfo DetectWindows()
    {
        var features = new List<OptimizerFeatureInfo>();

        bool GameBar()
        {
            return NativeReg.MatchesDword("HKCU", @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0)
                   && NativeReg.MatchesDword("HKCU", @"System\GameConfigStore", "GameDVR_Enabled", 0)
                   && NativeReg.MatchesDword("HKCU", @"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", 0);
        }

        bool Hags() => NativeReg.MatchesDword("HKLM", @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2);
        bool GameMode() =>
            NativeReg.MatchesDword("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1)
            || NativeReg.MatchesDword("HKCU", @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1);
        bool Win32() => NativeReg.MatchesDword("HKLM", @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 38);
        bool Mpo() => NativeReg.MatchesDword("HKLM", @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5);
        bool Sticky()
        {
            var v = NativeReg.GetValue("HKCU", @"Control Panel\Accessibility\StickyKeys", "Flags")?.ToString();
            return v is "506" or "58" or "0";
        }
        bool Mouse() =>
            (NativeReg.GetValue("HKCU", @"Control Panel\Mouse", "MouseSpeed")?.ToString() == "0")
            && (NativeReg.GetValue("HKCU", @"Control Panel\Mouse", "MouseThreshold1")?.ToString() == "0")
            && (NativeReg.GetValue("HKCU", @"Control Panel\Mouse", "MouseThreshold2")?.ToString() == "0");
        bool Kbd() =>
            NativeReg.GetValue("HKCU", @"Control Panel\Keyboard", "KeyboardDelay")?.ToString() == "0"
            && NativeReg.GetValue("HKCU", @"Control Panel\Keyboard", "KeyboardSpeed")?.ToString() == "31";
        bool Mic() => NativeReg.MatchesDword("HKCU", @"Software\Microsoft\Multimedia\Audio", "UserDuckingPreference", 3);
        bool Usb()
        {
            if (NativeReg.MatchesDword("HKLM", @"SYSTEM\CurrentControlSet\Services\USB", "DisableSelectiveSuspend", 1))
                return true;
            // powercfg probe
            try
            {
                var o = RunCapture("powercfg", "/q SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
                return o.Contains("0x00000000", StringComparison.Ordinal);
            }
            catch { return false; }
        }
        bool Desk() =>
            NativeReg.GetValue("HKCU", @"Control Panel\Desktop", "MenuShowDelay")?.ToString() == "0"
            && NativeReg.MatchesDword("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0);
        bool Amoled() =>
            NativeReg.MatchesDword("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0)
            && NativeReg.MatchesDword("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0);
        // MS clamps SystemResponsiveness <10 to 20 (stock default). Only 10 is the real Exo pin.
        bool HostLatency() =>
            NativeReg.MatchesDword("HKLM", @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1)
            && NativeReg.MatchesDword("HKLM",
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                "SystemResponsiveness", 10);
        bool Uac() => NativeReg.MatchesDword("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "ConsentPromptBehaviorAdmin", 0);
        bool Ai() =>
            NativeReg.MatchesDword("HKCU", @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1)
            || NativeReg.MatchesDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1);
        bool Explorer() =>
            NativeReg.MatchesDword("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LaunchTo", 1)
            && NativeReg.MatchesDword("HKCU",
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel",
                "{645FF040-5081-101B-9F08-00AA002F954E}", 1);
        bool Inbox() => NativeReg.MatchesDword("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0);
        bool Wu() =>
            NativeReg.MatchesDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", 1)
            || !string.IsNullOrEmpty(NativeReg.GetValue("HKLM", @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "PauseUpdatesExpiryTime")?.ToString());
        bool Defender() =>
            NativeReg.MatchesDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", 1)
            || NativeReg.MatchesDword("HKLM", @"SOFTWARE\Microsoft\Windows Defender", "DisableAntiSpyware", 1);
        bool Power() => ExoPowerPlanNative.IsExoPlanActive();
        var powerName = ExoPowerPlanNative.TargetNameForCpu();
        var activeLine = "";
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powercfg", Arguments = "/getactivescheme",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
            });
            activeLine = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(5000);
        }
        catch { }

        features.Add(F("Xbox Game Bar quiet", "Game Bar and DVR stay quiet while you play.", GameBar()));
        features.Add(F("Hardware GPU scheduling", "HAGS on (HwSchMode=2).", Hags()));
        features.Add(F("Windows Game Mode", "Game Mode on for focused games.", GameMode()));
        features.Add(F("Foreground boost", "Win32PrioritySeparation=38.", Win32()));
        features.Add(F("Smoother multi-monitor", "MPO OverlayTestMode=5.", Mpo()));
        features.Add(F("No sticky-key popups", "Sticky Keys flags quiet.", Sticky()));
        features.Add(F(powerName, Power()
            ? $"Active live: {activeLine.Trim()}"
            : $"NOT active — currently: {activeLine.Trim()} (need {powerName})", Power()));
        features.Add(F("Host latency profile", "PowerThrottlingOff + SystemResponsiveness=10 (MS-safe min; 0 clamps to 20).", HostLatency()));
        features.Add(F("Raw mouse feel", "Pointer acceleration off.", Mouse()));
        features.Add(F("Fast keyboard repeat", "Delay 0 / Speed 31.", Kbd()));
        features.Add(F("No mic ducking", "UserDuckingPreference=3.", Mic()));
        features.Add(F("USB always awake", "USB selective suspend off.", Usb()));
        features.Add(F("Snappy desktop", "MenuShowDelay 0 + StartupDelayInMSec 0.", Desk()));
        features.Add(F("AMOLED pure black", "Apps + system dark theme.", Amoled()));
        // Removed duplicate "Instant menus" (same MenuShowDelay probe as Snappy desktop).
        features.Add(F("No UAC prompts", "ConsentPromptBehaviorAdmin=0.", Uac()));
        features.Add(F("Windows AI removed", "Copilot / WindowsAI policy.", Ai()));
        features.Add(F("Explorer decluttered", "LaunchTo This PC + recycle bin hidden.", Explorer()));
        features.Add(F("Inbox apps quiet", "BingSearchEnabled=0.", Inbox()));
        // Honest: USB selective suspend only — Game Bar quiet is a different row.
        features.Add(F("Controllers stay awake", "USB selective suspend off (live).", Usb()));
        features.Add(F("Controller overlays quiet", "GameDVR_Enabled=0.", GameBar()));
        features.Add(F("Windows Update paused", "NoAutoUpdate or pause expiry set.", Wu()));
        features.Add(F("Defender purged", "DisableAntiSpyware policy=1.", Defender()));

        // No noisy Exo console helpers. Hidden yield companions (-WindowStyle Hidden -File)
        // are allowed; visible/console-style Exo Run keys are not.
        var noBg = true;
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (run is not null)
            {
                foreach (var n in run.GetValueNames())
                {
                    if (!n.StartsWith("Exo-", StringComparison.OrdinalIgnoreCase)) continue;
                    var v = run.GetValue(n)?.ToString() ?? "";
                    // Allowed: hidden PowerShell yield companions only (no wscript — WSH pops errors)
                    if (v.Contains("-WindowStyle", StringComparison.OrdinalIgnoreCase) &&
                        v.Contains("Hidden", StringComparison.OrdinalIgnoreCase) &&
                        v.Contains("-File", StringComparison.OrdinalIgnoreCase) &&
                        v.Contains("yield-guard", StringComparison.OrdinalIgnoreCase) &&
                        !v.Contains("wscript", StringComparison.OrdinalIgnoreCase) &&
                        !v.Contains(@"WindowsApps\pwsh", StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Anything else Exo on Run is noisy (including legacy wscript)
                    if (v.Contains("pwsh", StringComparison.OrdinalIgnoreCase) ||
                        v.Contains("powershell", StringComparison.OrdinalIgnoreCase) ||
                        v.Contains("wscript", StringComparison.OrdinalIgnoreCase) ||
                        v.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase))
                        noBg = false;
                }
            }
        }
        catch { }

        features.Add(F("No Exo background", "No noisy Exo console Run keys (hidden yield companions OK).", noBg));

        // Deep rows: only green when last applyReport proves real work (not a fake "ok" marker).
        var (deepTasks, deepTasksDetail) = ReadWindowsDeepPass(
            "scheduledTasksDeepPass", "scheduled-tasks", requireDisabled: true);
        var (deepOpt, deepOptDetail) = ReadWindowsDeepPass(
            "optionalFeaturesDeepPass", "optional-features", requireDisabled: false);
        features.Add(F("Scheduled tasks quieted", deepTasksDetail, deepTasks));
        features.Add(F("Optional components quieted", deepOptDetail, deepOpt));

        var checkable = features.Where(f => !IsInfo(f.Title)).ToList();
        var off = checkable.Where(f => !f.IsActive).Select(f => f.Title).ToList();
        var core = GameBar() && Hags() && GameMode() && Win32() && Power();
        var applied = core && off.Count == 0;

        return new OptimizerStateInfo
        {
            IsApplied = applied,
            StatusText = applied ? "Already optimized"
                : off.Count == 1 ? $"1 setting needs Apply ({off[0]})"
                : off.Count > 1 ? $"{off.Count} settings need Apply"
                : "Ready to optimize",
            Detail = applied
                ? $"Live: {powerName}, Game Mode, HAGS, Game Bar, priority, host latency."
                : off.Count > 0 ? "Off: " + string.Join(", ", off) + "." : "",
            Features = features
        };
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

        // Library: sample installed game EXEs for GpuPreference=2
        var libOk = LiveSteamLibraryGpuFso(steam);
        features.Add(F("Library games GPU & FSO", "Live GpuPreference=2 + FSO on library games.", libOk));

        var helper = Path.Combine(steam, "Exo-SteamMemoryGuard.ps1");
        var guardOk = false;
        try
        {
            if (File.Exists(helper))
                guardOk = SteamLogic.IsMemoryGuardText(File.ReadAllText(helper));
        }
        catch { }
        features.Add(F("Yield to your game", "Memory guard classifier pass on disk.", guardOk));

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

        // Steam rewrites StartupMode after every client launch (often back to 7).
        // Core quiet = no Run key + Startup apps Off + toasts Off. Re-pin StartupMode
        // on every detect so opening Exo does not leave a permanent red "need Apply".
        var noRun = !RunKeyHasSteam();
        var toastOk = LiveSteamToasts();
        var approvedOff = IsStartupApprovedDisabled("Steam");
        if (noRun && toastOk && approvedOff)
            NativeReg.TrySetDword("HKCU", @"Software\Valve\Steam", "StartupMode", 0);
        var modeOk = NativeReg.MatchesDword("HKCU", @"Software\Valve\Steam", "StartupMode", 0);
        // Silent is green when Windows won't autostart Steam. StartupMode is best-effort
        // (Steam rewrites it) — do not keep the whole module red forever because of it.
        var silentOk = noRun && toastOk && approvedOff;
        var silentDetail = silentOk
            ? (modeOk
                ? "No Run key, Startup apps Off, toasts Off, StartupMode=0."
                : "No Run key, Startup apps Off, toasts Off. (Steam rewrites StartupMode after it opens — re-pinned when you open Exo.)")
            : string.Join("; ", new[]
            {
                noRun ? null : "Steam still in Run",
                toastOk ? null : "toast keys not fully Off",
                approvedOff ? null : "Windows Startup apps still On for Steam",
            }.Where(s => s is not null));
        features.Add(F("Silent Windows integration", silentDetail, silentOk));

        var launchOk = LiveStartMenuPointsToExo(steam, cmdPath);
        features.Add(F("Clean Start Menu launch", "Start Menu Steam.lnk → Steam-Exo.cmd.", launchOk));

        var runtimeOk = File.Exists(Path.Combine(steam, "steam.exe")) && (guardOk || File.Exists(helper));
        features.Add(F("Helpers stay healthy", "steam.exe + memory guard / CEF launcher on disk.", runtimeOk && cefOk && guardOk));

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
                ? "Live: CEF launcher, memory guard, HW accel, Windows quiet, library GPU/FSO."
                : "Off: " + string.Join(", ", off) + ".",
            Features = features
        };
    }

    public static OptimizerStateInfo DetectLauncher(string module)
    {
        module = module.ToLowerInvariant();
        var label = char.ToUpper(module[0]) + module[1..];
        var features = new List<OptimizerFeatureInfo>();

        var games = module == "riot" ? DiscoverRiot() : DiscoverEpic();
        var launchers = module == "riot" ? DiscoverRiotLaunchers() : DiscoverEpicLaunchers();
        var installed = games.Count > 0 || launchers.Count > 0 ||
                        (module == "riot"
                            ? Directory.Exists(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "Riot Games"))
                            : Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games")));

        features.Add(F($"{label} ready", installed ? "Installed." : "Not installed.", installed));
        features.Add(F("Games found", games.Count > 0 ? $"Found {games.Count} game EXE(s)." : "No game EXEs yet.", games.Count > 0));

        var startupQuiet = !RunKeyHasModule(module);
        features.Add(F("Silent startup", "No launcher Run keys (Exo yield allowed).", startupQuiet));

        var shellQuiet = LiveShellQuiet(module);
        var approvedQuiet = module == "riot"
            ? IsStartupApprovedDisabled("RiotClient") && IsStartupApprovedDisabled("Riot Client")
            : IsStartupApprovedDisabled("EpicGamesLauncher") && IsStartupApprovedDisabled("EpicGames");
        var silentWin = shellQuiet && approvedQuiet;
        features.Add(F(
            "Silent Windows integration",
            silentWin
                ? "Toast keys Off + Windows Startup apps Off for this launcher."
                : !shellQuiet
                    ? "Toast notification keys missing or still Enabled — re-Apply."
                    : "Windows Startup apps still On for this launcher — re-Apply.",
            silentWin));

        var (gpuOk, fsoOk) = LiveGpuFso(games);
        features.Add(F("High-performance GPU", "Every game path GpuPreference=2; (live readback).", gpuOk));
        features.Add(F("True fullscreen path", "FSO flag on every game path.", fsoOk));

        var dscpOk = LiveDscp(module, games);
        features.Add(F("Priority game traffic", "DSCP 46 policies for game EXEs.", dscpOk));

        var yieldOk = LiveYieldOk(module);
        features.Add(F(
            "Silent launcher companion",
            yieldOk
                ? "Yield guard on Run: demotes + minimizes launcher, closes UI ~10s after game starts."
                : "Missing/broken yield companion — re-Apply Riot/Epic.",
            yieldOk));

        // Soft "Apply recorded it" — informational only, never blocks Applied.
        features.Add(F("Launcher junk cleaned", "Recorded on last Apply (not a live disk scan).", true));

        var menuOk = File.Exists(Path.Combine(PathHelper.AppDataDir, "launchers", $"{label}-Exo.cmd"));
        features.Add(F("Quiet Start Menu launch", $"{label}-Exo.cmd present under LocalAppData\\Exo\\launchers.", menuOk));

        features.Add(F("Anti-cheat untouched", "Never modified.", installed));

        var snapOk = File.Exists(Path.Combine(PathHelper.AppDataDir, $"{module}-snapshot.json"));
        features.Add(F("One-click Repair ready", "Snapshot file on disk.", snapOk));

        var checkable = features.Where(f =>
            !IsInfo(f.Title)
            && f.Title is not "Anti-cheat untouched"
            and not "One-click Repair ready"
            and not "Launcher junk cleaned").ToList();
        // Include games found only if installed
        var off = checkable.Where(f => !f.IsActive).Select(f => f.Title).ToList();
        // If no games, "Games found" stays off — not a full fail if user only has launcher
        if (games.Count == 0)
            off.RemoveAll(t => t == "Games found" || t == "High-performance GPU" || t == "True fullscreen path" || t == "Priority game traffic");

        var applied = installed && startupQuiet && silentWin && yieldOk && snapOk && menuOk
                      && (games.Count == 0 || (gpuOk && fsoOk && dscpOk))
                      && off.Count == 0;

        return new OptimizerStateInfo
        {
            IsApplied = applied,
            StatusText = !installed ? "Not installed"
                : applied ? "Already optimized"
                : off.Count == 1 ? $"1 setting needs Apply ({off[0]})"
                : off.Count > 1 ? $"{off.Count} settings need Apply"
                : "Ready to optimize",
            Detail = applied
                ? "Live: GPU/FSO/DSCP/yield/startup verified."
                : off.Count > 0 ? "Off: " + string.Join(", ", off) + "." : "",
            Features = features
        };
    }

    // ---- helpers ----

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
    private static (bool Ok, string Detail) ReadWindowsDeepPass(
        string flagName, string reportId, bool requireDisabled)
    {
        try
        {
            var st = Path.Combine(PathHelper.AppDataDir, "windows-optimizer.json");
            if (!File.Exists(st))
                return (false, "Not proven live — will run on next Windows Apply.");

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(st));
            var root = doc.RootElement;
            var flag = root.TryGetProperty(flagName, out var fl) &&
                       fl.ValueKind == System.Text.Json.JsonValueKind.True;

            string? reportLine = null;
            if (root.TryGetProperty("applyReport", out var rep) &&
                rep.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in rep.EnumerateArray())
                {
                    var s = item.GetString();
                    if (s is not null &&
                        s.StartsWith(reportId + "|", StringComparison.OrdinalIgnoreCase))
                    {
                        reportLine = s;
                        break;
                    }
                }
            }

            if (reportLine is null)
            {
                return flag
                    ? (false, "Legacy marker only — re-Apply Windows for an honest deep pass.")
                    : (false, "Not proven live — will run on next Windows Apply.");
            }

            // Parse status word after |
            var pipe = reportLine.IndexOf('|');
            var rest = pipe >= 0 ? reportLine[(pipe + 1)..] : reportLine;
            var status = rest.Split(':')[0].Trim().ToLowerInvariant();
            var timedOut = ParseReportInt(rest, "timedOut");
            var failed = ParseReportInt(rest, "failed");
            var disabled = ParseReportInt(rest, "disabled");
            var alreadyOff = ParseReportInt(rest, "alreadyOff");

            if (status is "partial" or "fail" || timedOut > 0 || failed > 0)
                return (false, $"Last deep pass incomplete ({rest}). Re-Apply Windows.");

            if (status == "skip")
                return (true, "Nothing to change on this SKU (last Apply).");

            if (status != "ok")
                return (false, "Not proven live — will run on next Windows Apply.");

            if (requireDisabled && disabled <= 0)
                return (false, $"Last pass disabled={disabled} — re-Apply if tasks still noisy.");

            // Prefer flag from honest SaveState, but report line wins when present
            return (true, flag
                ? $"Last Apply: {rest}"
                : $"Last Apply report OK: {rest}");
        }
        catch
        {
            return (false, "Could not read windows-optimizer.json — re-Apply Windows.");
        }
    }

    private static int ParseReportInt(string reason, string field)
    {
        var m = Regex.Match(reason, $@"\b{Regex.Escape(field)}=(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }

    private static bool LiveSteamLibraryGpuFso(string steamPath)
    {
        // Probe up to 12 real game EXEs under steamapps\common
        var common = Path.Combine(steamPath, "steamapps", "common");
        if (!Directory.Exists(common)) return true; // nothing to pin yet
        var samples = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(common).Take(20))
            {
                foreach (var exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories).Take(8))
                {
                    var leaf = Path.GetFileName(exe);
                    if (leaf.Contains("Crash", StringComparison.OrdinalIgnoreCase) ||
                        leaf.Contains("Unins", StringComparison.OrdinalIgnoreCase) ||
                        leaf.Contains("vcredist", StringComparison.OrdinalIgnoreCase) ||
                        leaf.Contains("EasyAntiCheat", StringComparison.OrdinalIgnoreCase))
                        continue;
                    samples.Add(exe);
                    if (samples.Count >= 12) break;
                }
                if (samples.Count >= 12) break;
            }
        }
        catch { return false; }

        if (samples.Count == 0) return true;

        using var gpu = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
        using var fso = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers");
        var ok = 0;
        foreach (var exe in samples)
        {
            var g = gpu?.GetValue(exe)?.ToString() ?? "";
            var f = fso?.GetValue(exe)?.ToString() ?? "";
            if (g.Contains("GpuPreference=2", StringComparison.OrdinalIgnoreCase) &&
                f.Contains("DISABLEDXMAXIMIZEDWINDOWEDMODE", StringComparison.OrdinalIgnoreCase))
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

    private static bool RunKeyHasModule(string module)
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (run is null) return false;
            var yield = module == "riot" ? "Exo-Riot-Yield" : "Exo-Epic-Yield";
            foreach (var n in run.GetValueNames())
            {
                if (n.Equals(yield, StringComparison.OrdinalIgnoreCase)) continue;
                if (n.StartsWith("Exo-", StringComparison.OrdinalIgnoreCase)) continue;
                var v = $"{n} {run.GetValue(n)}";
                if (module == "riot" && Regex.IsMatch(v, "(?i)Riot|VALORANT|League")) return true;
                if (module == "epic" && Regex.IsMatch(v, "(?i)Epic")) return true;
            }
        }
        catch { }
        return false;
    }

    private static bool LiveShellQuiet(string module)
    {
        // Keep in sync with LauncherNativeApply.QuietShell (+ extra AUMIDs Windows creates)
        var ids = module == "riot"
            ? new[]
            {
                "Riot Client", "RiotClient", "VALORANT", "League of Legends", "riotgameclient",
                "Riot Games", "RiotClientUx", "riotclientservices.exe"
            }
            : new[]
            {
                "EpicGamesLauncher", "com.epicgames.launcher", "Epic Games Launcher", "EpicGames",
                "EpicGamesLauncher.exe"
            };
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
        // Require we actually own toast keys (Apply writes them). None present = not quiet yet.
        return seen > 0 && on == 0;
    }

    private static (bool Gpu, bool Fso) LiveGpuFso(List<string> games)
    {
        if (games.Count == 0) return (true, true);
        using var gpu = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
        using var fso = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers");
        var gOk = 0;
        var fOk = 0;
        foreach (var path in games)
        {
            var g = gpu?.GetValue(path)?.ToString() ?? "";
            var f = fso?.GetValue(path)?.ToString() ?? "";
            if (g.Contains("GpuPreference=2", StringComparison.OrdinalIgnoreCase)) gOk++;
            if (f.Contains("DISABLEDXMAXIMIZEDWINDOWEDMODE", StringComparison.OrdinalIgnoreCase)) fOk++;
        }
        return (gOk == games.Count, fOk == games.Count);
    }

    private static bool LiveDscp(string module, List<string> games)
    {
        if (games.Count == 0) return true;
        var mod = char.ToUpper(module[0]) + module[1..];
        var need = 0;
        var hit = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in games)
        {
            var leaf = Path.GetFileName(path);
            if (string.IsNullOrEmpty(leaf) || !seen.Add(leaf)) continue;
            need++;
            var safe = Regex.Replace(leaf, @"[^\w\.\-]", "_");
            var pol = $@"SOFTWARE\Policies\Microsoft\Windows\QoS\Exo-{mod}-DSCP-{safe}";
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(pol);
                if (k is null) continue;
                if (string.Equals(k.GetValue("DSCP Value")?.ToString(), "46", StringComparison.Ordinal) &&
                    string.Equals(k.GetValue("Application Name")?.ToString(), leaf, StringComparison.OrdinalIgnoreCase))
                    hit++;
            }
            catch { }
        }
        return need == 0 || hit >= need;
    }

    private static bool LiveYieldOk(string module)
    {
        var mod = char.ToUpper(module[0]) + module[1..];
        var yieldName = $"Exo-{mod}-Yield";
        var helper = Path.Combine(PathHelper.AppDataDir, $"{module}-yield-guard.ps1");
        string? runVal = null;
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            runVal = run?.GetValue(yieldName)?.ToString();
        }
        catch { }

        var helperPresent = File.Exists(helper);

        // Broken hosts: WSH, or a bare WindowsApps\pwsh alias stub (not real PS7).
        // Store-installed PowerShell (…\WindowsApps\Microsoft.PowerShell*…\pwsh.exe) is valid.
        if (!string.IsNullOrEmpty(runVal))
        {
            if (runVal.Contains("wscript", StringComparison.OrdinalIgnoreCase))
                return false;
            if (runVal.Contains(@"WindowsApps\pwsh", StringComparison.OrdinalIgnoreCase) &&
                !runVal.Contains("Microsoft.PowerShell", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Good: Hidden PowerShell -File yield-guard + helper on disk
        var goodRun = !string.IsNullOrEmpty(runVal) &&
                      runVal.Contains("yield-guard", StringComparison.OrdinalIgnoreCase) &&
                      (runVal.Contains("pwsh", StringComparison.OrdinalIgnoreCase) ||
                       runVal.Contains("powershell", StringComparison.OrdinalIgnoreCase)) &&
                      runVal.Contains("-File", StringComparison.OrdinalIgnoreCase) &&
                      runVal.Contains("Hidden", StringComparison.OrdinalIgnoreCase);

        // Helper without Run key = Windows "no background" wrongly stripped it (not ok)
        if (helperPresent && !goodRun) return false;

        // Neither companion nor Run key:
        // - No game EXEs yet → intentional purge on Apply (clean / launcher-only) = OK
        // - Games installed → yield required = off until reapply
        if (!helperPresent && string.IsNullOrEmpty(runVal))
        {
            var games = module.Equals("riot", StringComparison.OrdinalIgnoreCase)
                ? DiscoverRiot()
                : DiscoverEpic();
            return games.Count == 0;
        }

        return goodRun && helperPresent;
    }

    private static List<string> DiscoverRiot()
    {
        var list = new List<string>();
        var drive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var root = Path.Combine(drive, "Riot Games");
        foreach (var rel in new[]
                 {
                     @"VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe",
                     @"VALORANT\live\VALORANT\Binaries\Win64\VALORANT-Win64-Shipping.exe",
                     @"VALORANT\VALORANT.exe",
                     @"League of Legends\Game\League of Legends.exe"
                 })
        {
            var p = Path.Combine(root, rel);
            if (File.Exists(p)) list.Add(Path.GetFullPath(p));
        }
        return list;
    }

    private static List<string> DiscoverRiotLaunchers()
    {
        var drive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var p = Path.Combine(drive, @"Riot Games\Riot Client\RiotClientServices.exe");
        return File.Exists(p) ? new List<string> { Path.GetFullPath(p) } : new List<string>();
    }

    private static List<string> DiscoverEpic()
    {
        var list = new List<string>();
        var manifestRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestRoot)) return list;
        foreach (var file in Directory.EnumerateFiles(manifestRoot, "*.item"))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                var launch = r.TryGetProperty("LaunchExecutable", out var le) ? le.GetString() : null;
                var install = r.TryGetProperty("InstallLocation", out var il) ? il.GetString() : null;
                if (string.IsNullOrWhiteSpace(launch) || string.IsNullOrWhiteSpace(install)) continue;
                if (launch.Contains("EpicGamesLauncher", StringComparison.OrdinalIgnoreCase)) continue;
                var full = Path.Combine(install, launch);
                if (File.Exists(full)) list.Add(Path.GetFullPath(full));
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    foreach (var sibling in Directory.EnumerateFiles(dir, "*.exe"))
                    {
                        var leaf = Path.GetFileName(sibling);
                        if (leaf.Equals("Launcher.exe", StringComparison.OrdinalIgnoreCase)) continue;
                        if (leaf.Contains("Crash", StringComparison.OrdinalIgnoreCase) ||
                            leaf.Contains("Unins", StringComparison.OrdinalIgnoreCase)) continue;
                        if (leaf.Equals("RocketLeague.exe", StringComparison.OrdinalIgnoreCase) ||
                            leaf.Contains("Shipping", StringComparison.OrdinalIgnoreCase) ||
                            new FileInfo(sibling).Length > 5_000_000)
                        {
                            var sp = Path.GetFullPath(sibling);
                            if (!list.Contains(sp, StringComparer.OrdinalIgnoreCase))
                                list.Add(sp);
                        }
                    }
                }
            }
            catch { }
        }
        return list;
    }

    private static List<string> DiscoverEpicLaunchers()
    {
        var list = new List<string>();
        foreach (var root in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         "Epic Games", "Launcher", "Portal", "Binaries", "Win64"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         "Epic Games", "Launcher", "Portal", "Binaries", "Win64")
                 })
        {
            var p = Path.Combine(root, "EpicGamesLauncher.exe");
            if (File.Exists(p)) list.Add(Path.GetFullPath(p));
        }
        return list;
    }

    private static string RunCapture(string file, string args)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p is null) return "";
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            return o;
        }
        catch { return ""; }
    }
}
