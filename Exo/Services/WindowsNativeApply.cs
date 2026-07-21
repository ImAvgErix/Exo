using System.Diagnostics;
using System.Text.Json;
using Exo.Helpers;
using Microsoft.Win32;

namespace Exo.Services;

/// <summary>
/// Pure C# Windows host pack: Game Mode, HAGS, Game Bar quiet, priority,
/// mouse precision, sticky keys, menu delay, host latency, MPO, shell quiet.
/// HKCU is always applied here. HKLM is applied when elevated, else staged
/// for the elevated native pack.
/// </summary>
public static class WindowsNativeApply
{
    public const string StateFileName = "windows-optimizer.json";
    public const string Version = "native-3.13.11";

    /// <summary>Hard-timeout process runner — kill tree on timeout so Apply never hangs.</summary>
    private static (int ExitCode, string StdOut, bool TimedOut) RunTimed(
        string file, string args, int timeoutMs)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p is null) return (-1, "", true);
            var stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { try { p.Kill(); } catch { } }
                try { p.WaitForExit(2000); } catch { }
                return (-1, stdout, true);
            }
            return (p.ExitCode, stdout, false);
        }
        catch
        {
            return (-1, "", true);
        }
    }

    public static NativeApplyResult Apply(bool experimental, IProgress<string>? progress = null)
    {
        var steps = new List<NativeApplyStep>();
        var elevOps = new List<string>();
        var admin = NativeReg.IsAdministrator();

        void Report(string msg) => progress?.Report(msg);

        // Full competitive host pack — every detect row has a writer here.
        // Hang sources (DISM/schtasks) always use RunTimed hard timeouts.

        Report("Game Bar / Game DVR quiet...");
        steps.Add(ApplyGameBarQuiet(admin, elevOps));

        Report("Game Mode...");
        steps.Add(SetGameModeOn());

        Report("HAGS...");
        steps.Add(SetHags(admin, elevOps));

        Report("Win32 priority...");
        steps.Add(SetWin32Priority(admin, elevOps));

        Report("Mouse precision off...");
        steps.Add(SetMousePrecisionOff());

        Report("Sticky keys off...");
        steps.Add(SetStickyKeysOff());

        Report("Menu snap...");
        steps.Add(SetMenuShowDelayZero());

        Report("Host latency profile...");
        steps.Add(SetHostLatency(admin, elevOps));

        Report("MPO / overlays...");
        steps.Add(SetMpoDisabled(admin, elevOps));

        Report("Shell declutter (HKCU)...");
        steps.Add(SetShellQuietHkcu());

        Report("Input pack (mouse/kbd/mic/USB/desktop)...");
        steps.Add(SetInputPack(admin, elevOps));

        Report("AMOLED pure black...");
        steps.Add(SetAmoledTheme());

        Report("Windows AI / Copilot quiet...");
        steps.Add(SetWindowsAiQuiet(admin, elevOps));

        Report("UAC never notify...");
        steps.Add(SetUacNeverNotify(admin, elevOps));

        Report("Inbox apps quiet...");
        steps.Add(SetInboxQuiet());

        Report("Windows Update pause...");
        steps.Add(SetWindowsUpdatePause(admin, elevOps));

        Report("Defender purge (policy)...");
        steps.Add(SetDefenderPurged(admin, elevOps));

        Report("Exo Competitive power plan...");
        steps.Add(SetHighPerfPower());

        Report("USB selective suspend off (controllers/devices)...");
        steps.Add(SetUsbNoSelectiveSuspend(admin, elevOps));

        Report("Scheduled-task quiet (expanded list + empty folders)...");
        steps.Add(DisableKnownBloatTasks());

        Report("Optional features (DISM shortlist, hard-timeout each)...");
        steps.Add(DisableOptionalFeaturesShortlist());

        Report("No Exo background Run companions...");
        steps.Add(PurgeNoisyExoRunKeys());

        var essentialOk = steps.FirstOrDefault(s => s.Id == "game-mode")?.Status == "ok"
                          && steps.FirstOrDefault(s => s.Id == "game-bar")?.Status is "ok" or "partial"
                          && steps.FirstOrDefault(s => s.Id == "power-plan")?.Status == "ok";

        SaveState(essentialOk, experimental, steps, elevOps);

        return new NativeApplyResult
        {
            Ok = essentialOk,
            Module = "windows",
            Message = essentialOk
                ? "Windows full host stack applied (native C#, timeout-safe)"
                : "Windows native apply incomplete — open as Administrator for full HKLM pack",
            Steps = steps,
            NeedsElevation = elevOps.Count > 0 && !admin,
            ElevatedHklmOps = elevOps
        };
    }

    private static NativeApplyStep ApplyGameBarQuiet(bool admin, List<string> elevOps)
    {
        var written = 0;
        var targets = new (string Hive, string Path, string Name, int Val)[]
        {
            ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0),
            ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", 0),
            ("HKCU", @"System\GameConfigStore", "GameDVR_Enabled", 0),
            ("HKCU", @"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", 0),
            ("HKCU", @"Software\Microsoft\GameBar", "ShowStartupPanel", 0),
            ("HKCU", @"Software\Microsoft\GameBar", "GamePanelStartupTipIndex", 3),
            ("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0),
        };

        foreach (var t in targets)
        {
            if (t.Hive.Equals("HKLM", StringComparison.OrdinalIgnoreCase) && !admin)
            {
                elevOps.Add($"dword:{t.Hive}\\{t.Path}|{t.Name}|{t.Val}");
                continue;
            }
            if (NativeReg.TrySetDword(t.Hive, t.Path, t.Name, t.Val)) written++;
        }

        var core =
            NativeReg.MatchesDword("HKCU", @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0) &&
            NativeReg.MatchesDword("HKCU", @"System\GameConfigStore", "GameDVR_Enabled", 0) &&
            NativeReg.MatchesDword("HKCU", @"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", 0);

        return new NativeApplyStep
        {
            Id = "game-bar",
            Status = core ? "ok" : "fail",
            Reason = $"written={written}"
        };
    }

    private static NativeApplyStep SetGameModeOn()
    {
        var n = 0;
        if (NativeReg.TrySetDword("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1)) n++;
        if (NativeReg.TrySetDword("HKCU", @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1)) n++;
        if (NativeReg.TrySetDword("HKCU", @"System\GameConfigStore", "GameMode_Enabled", 1)) n++;
        var ok = NativeReg.MatchesDword("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1)
                 || NativeReg.MatchesDword("HKCU", @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1);
        return new NativeApplyStep { Id = "game-mode", Status = ok ? "ok" : "fail", Reason = $"written={n}" };
    }

    private static NativeApplyStep SetHags(bool admin, List<string> elevOps)
    {
        const string path = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
        if (!admin)
        {
            elevOps.Add($"dword:HKLM\\{path}|HwSchMode|2");
            // Already on?
            if (NativeReg.MatchesDword("HKLM", path, "HwSchMode", 2))
                return new NativeApplyStep { Id = "hags", Status = "ok", Reason = "already" };
            return new NativeApplyStep { Id = "hags", Status = "pending-elev", Reason = "needs admin" };
        }
        var ok = NativeReg.TrySetDword("HKLM", path, "HwSchMode", 2);
        return new NativeApplyStep { Id = "hags", Status = ok ? "ok" : "fail", Reason = ok ? "HwSchMode=2" : "write failed" };
    }

    private static NativeApplyStep SetWin32Priority(bool admin, List<string> elevOps)
    {
        const string path = @"SYSTEM\CurrentControlSet\Control\PriorityControl";
        if (!admin)
        {
            elevOps.Add($"dword:HKLM\\{path}|Win32PrioritySeparation|38");
            if (NativeReg.MatchesDword("HKLM", path, "Win32PrioritySeparation", 38))
                return new NativeApplyStep { Id = "win32-priority", Status = "ok", Reason = "already" };
            return new NativeApplyStep { Id = "win32-priority", Status = "pending-elev", Reason = "needs admin" };
        }
        var ok = NativeReg.TrySetDword("HKLM", path, "Win32PrioritySeparation", 38);
        return new NativeApplyStep { Id = "win32-priority", Status = ok ? "ok" : "fail" };
    }

    private static NativeApplyStep SetMousePrecisionOff()
    {
        var ok =
            NativeReg.TrySetString("HKCU", @"Control Panel\Mouse", "MouseSpeed", "0") &&
            NativeReg.TrySetString("HKCU", @"Control Panel\Mouse", "MouseThreshold1", "0") &&
            NativeReg.TrySetString("HKCU", @"Control Panel\Mouse", "MouseThreshold2", "0");
        return new NativeApplyStep { Id = "mouse", Status = ok ? "ok" : "fail" };
    }

    private static NativeApplyStep SetStickyKeysOff()
    {
        var ok = NativeReg.TrySetString("HKCU", @"Control Panel\Accessibility\StickyKeys", "Flags", "506");
        return new NativeApplyStep { Id = "sticky-keys", Status = ok ? "ok" : "fail" };
    }

    private static NativeApplyStep SetMenuShowDelayZero()
    {
        var ok = NativeReg.TrySetString("HKCU", @"Control Panel\Desktop", "MenuShowDelay", "0");
        return new NativeApplyStep { Id = "menu-snap", Status = ok ? "ok" : "fail" };
    }

    private static NativeApplyStep SetHostLatency(bool admin, List<string> elevOps)
    {
        if (!admin)
        {
            elevOps.Add(@"dword:HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling|PowerThrottlingOff|1");
            elevOps.Add(@"dword:HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile|SystemResponsiveness|10");
            elevOps.Add(@"dword:HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile|NetworkThrottlingIndex|10");
            elevOps.Add(@"dword:HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games|GPU Priority|8");
            elevOps.Add(@"dword:HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games|Priority|6");
            elevOps.Add(@"string:HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games|Scheduling Category|High");
            elevOps.Add(@"string:HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games|SFIO Priority|High");
            return new NativeApplyStep { Id = "host-latency", Status = "pending-elev", Reason = "needs admin" };
        }

        var n = 0;
        if (NativeReg.TrySetDword("HKLM", @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1)) n++;
        if (NativeReg.TrySetDword("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 10)) n++;
        if (NativeReg.TrySetDword("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", 10)) n++;
        if (NativeReg.TrySetDword("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority", 8)) n++;
        if (NativeReg.TrySetDword("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Priority", 6)) n++;
        if (NativeReg.TrySetString("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Scheduling Category", "High")) n++;
        if (NativeReg.TrySetString("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "SFIO Priority", "High")) n++;
        return new NativeApplyStep { Id = "host-latency", Status = n >= 3 ? "ok" : "partial", Reason = $"written={n}" };
    }

    private static NativeApplyStep SetMpoDisabled(bool admin, List<string> elevOps)
    {
        if (!admin)
        {
            elevOps.Add(@"dword:HKLM\SOFTWARE\Microsoft\Windows\Dwm|OverlayTestMode|5");
            elevOps.Add(@"dword:HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers|DisableOverlays|1");
            return new NativeApplyStep { Id = "mpo", Status = "pending-elev", Reason = "needs admin" };
        }
        var n = 0;
        if (NativeReg.TrySetDword("HKLM", @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5)) n++;
        if (NativeReg.TrySetDword("HKLM", @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "DisableOverlays", 1)) n++;
        return new NativeApplyStep { Id = "mpo", Status = n > 0 ? "ok" : "fail" };
    }

    private static NativeApplyStep SetShellQuietHkcu()
    {
        var n = 0;
        var adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        if (NativeReg.TrySetDword("HKCU", adv, "ShowTaskViewButton", 0)) n++;
        if (NativeReg.TrySetDword("HKCU", adv, "TaskbarDa", 0)) n++;
        if (NativeReg.TrySetDword("HKCU", adv, "TaskbarMn", 0)) n++;
        if (NativeReg.TrySetDword("HKCU", adv, "ShowCopilotButton", 0)) n++;
        if (NativeReg.TrySetDword("HKCU", adv, "HideFileExt", 0)) n++;
        if (NativeReg.TrySetDword("HKCU", adv, "LaunchTo", 1)) n++; // This PC
        if (NativeReg.TrySetDword("HKCU", adv, "Hidden", 1)) n++;
        if (NativeReg.TrySetDword("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 0)) n++;
        if (NativeReg.TrySetDword("HKCU", @"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", 0)) n++;
        // Hide recycle bin from desktop (detect: explorer declutter)
        if (NativeReg.TrySetDword("HKCU",
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel",
                "{645FF040-5081-101B-9F08-00AA002F954E}", 1)) n++;
        return new NativeApplyStep { Id = "explorer", Status = n >= 3 ? "ok" : "partial", Reason = $"written={n}" };
    }

    private static NativeApplyStep SetInputPack(bool admin, List<string> elevOps)
    {
        var n = 0;
        // Mouse
        foreach (var (name, val) in new[]
                 {
                     ("MouseSpeed", "0"), ("MouseThreshold1", "0"), ("MouseThreshold2", "0"),
                     ("MouseTrails", "0"), ("MouseSensitivity", "10"), ("SnapToDefaultButton", "0"),
                     ("MouseHoverTime", "10")
                 })
        {
            if (NativeReg.TrySetString("HKCU", @"Control Panel\Mouse", name, val)) n++;
        }
        // Keyboard
        if (NativeReg.TrySetString("HKCU", @"Control Panel\Keyboard", "KeyboardDelay", "0")) n++;
        if (NativeReg.TrySetString("HKCU", @"Control Panel\Keyboard", "KeyboardSpeed", "31")) n++;
        if (NativeReg.TrySetString("HKCU", @"Control Panel\Accessibility\Keyboard Response", "Flags", "122")) n++;
        if (NativeReg.TrySetString("HKCU", @"Control Panel\Accessibility\ToggleKeys", "Flags", "58")) n++;
        // Mic ducking off
        if (NativeReg.TrySetDword("HKCU", @"Software\Microsoft\Multimedia\Audio", "UserDuckingPreference", 3)) n++;
        // Desktop snappiness
        if (NativeReg.TrySetDword("HKCU", @"Control Panel\Desktop", "ForegroundLockTimeout", 0)) n++;
        if (NativeReg.TrySetString("HKCU", @"Control Panel\Desktop", "MenuShowDelay", "0")) n++;
        if (NativeReg.TrySetString("HKCU", @"Control Panel\Desktop", "AutoEndTasks", "1")) n++;
        if (NativeReg.TrySetString("HKCU", @"Control Panel\Desktop", "HungAppTimeout", "1000")) n++;
        if (NativeReg.TrySetString("HKCU", @"Control Panel\Desktop", "WaitToKillAppTimeout", "2000")) n++;
        if (NativeReg.TrySetDword("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0)) n++;
        // USB selective suspend off
        TryPowerCfg("/setacvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0");
        TryPowerCfg("/setdcvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0");
        TryPowerCfg("/S SCHEME_CURRENT");
        if (admin)
        {
            if (NativeReg.TrySetDword("HKLM", @"SYSTEM\CurrentControlSet\Services\USB", "DisableSelectiveSuspend", 1)) n++;
        }
        else
        {
            elevOps.Add(@"dword:HKLM\SYSTEM\CurrentControlSet\Services\USB|DisableSelectiveSuspend|1");
        }
        return new NativeApplyStep { Id = "input-pack", Status = n >= 8 ? "ok" : "partial", Reason = $"written={n}" };
    }

    private static NativeApplyStep SetAmoledTheme()
    {
        var n = 0;
        var pers = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        if (NativeReg.TrySetDword("HKCU", pers, "AppsUseLightTheme", 0)) n++;
        if (NativeReg.TrySetDword("HKCU", pers, "SystemUsesLightTheme", 0)) n++;
        if (NativeReg.TrySetDword("HKCU", pers, "EnableTransparency", 0)) n++;
        if (NativeReg.TrySetDword("HKCU", pers, "ColorPrevalence", 1)) n++;
        // 0xFF000000 as signed int
        var black = unchecked((int)0xFF000000);
        try
        {
            using var dwm = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\DWM", true);
            if (dwm is not null)
            {
                dwm.SetValue("ColorPrevalence", 1, Microsoft.Win32.RegistryValueKind.DWord);
                dwm.SetValue("EnableWindowColorization", 1, Microsoft.Win32.RegistryValueKind.DWord);
                dwm.SetValue("AccentColor", black, Microsoft.Win32.RegistryValueKind.DWord);
                dwm.SetValue("ColorizationColor", black, Microsoft.Win32.RegistryValueKind.DWord);
                n += 4;
            }
        }
        catch { }
        var ok = NativeReg.MatchesDword("HKCU", pers, "AppsUseLightTheme", 0) &&
                 NativeReg.MatchesDword("HKCU", pers, "SystemUsesLightTheme", 0);
        return new NativeApplyStep { Id = "amoled", Status = ok ? "ok" : "fail", Reason = $"written={n}" };
    }

    private static NativeApplyStep SetWindowsAiQuiet(bool admin, List<string> elevOps)
    {
        var n = 0;
        if (NativeReg.TrySetDword("HKCU", @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1)) n++;
        if (NativeReg.TrySetDword("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowCopilotButton", 0)) n++;
        if (NativeReg.TrySetDword("HKCU", @"Software\Microsoft\Windows\Shell\Copilot", "IsCopilotAvailable", 0)) n++;
        if (NativeReg.TrySetDword("HKCU", @"Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1)) n++;
        if (admin)
        {
            if (NativeReg.TrySetDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1)) n++;
            if (NativeReg.TrySetDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1)) n++;
            if (NativeReg.TrySetDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "TurnOffSavingSnapshots", 1)) n++;
            if (NativeReg.TrySetDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecallEnablement", 0)) n++;
        }
        else
        {
            elevOps.Add(@"dword:HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot|TurnOffWindowsCopilot|1");
            elevOps.Add(@"dword:HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI|DisableAIDataAnalysis|1");
            elevOps.Add(@"dword:HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI|TurnOffSavingSnapshots|1");
            elevOps.Add(@"dword:HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI|AllowRecallEnablement|0");
        }
        return new NativeApplyStep { Id = "windows-ai", Status = n > 0 ? "ok" : "skip", Reason = $"written={n}" };
    }

    private static NativeApplyStep SetUacNeverNotify(bool admin, List<string> elevOps)
    {
        if (!admin)
        {
            elevOps.Add(@"dword:HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System|ConsentPromptBehaviorAdmin|0");
            elevOps.Add(@"dword:HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System|ConsentPromptBehaviorUser|0");
            elevOps.Add(@"dword:HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System|PromptOnSecureDesktop|0");
            if (NativeReg.MatchesDword("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "ConsentPromptBehaviorAdmin", 0))
                return new NativeApplyStep { Id = "uac", Status = "ok", Reason = "already" };
            return new NativeApplyStep { Id = "uac", Status = "pending-elev", Reason = "needs admin" };
        }
        var n = 0;
        var path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        if (NativeReg.TrySetDword("HKLM", path, "ConsentPromptBehaviorAdmin", 0)) n++;
        if (NativeReg.TrySetDword("HKLM", path, "ConsentPromptBehaviorUser", 0)) n++;
        if (NativeReg.TrySetDword("HKLM", path, "PromptOnSecureDesktop", 0)) n++;
        var ok = NativeReg.MatchesDword("HKLM", path, "ConsentPromptBehaviorAdmin", 0);
        return new NativeApplyStep { Id = "uac", Status = ok ? "ok" : "fail", Reason = $"written={n}" };
    }

    private static NativeApplyStep SetInboxQuiet()
    {
        var n = 0;
        var cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
        foreach (var name in new[]
                 {
                     "SubscribedContent-338389Enabled", "SubscribedContent-310093Enabled",
                     "SystemPaneSuggestionsEnabled", "SoftLandingEnabled",
                     "SilentInstalledAppsEnabled", "PreInstalledAppsEnabled", "OemPreInstalledAppsEnabled"
                 })
        {
            if (NativeReg.TrySetDword("HKCU", cdm, name, 0)) n++;
        }
        if (NativeReg.TrySetDword("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0)) n++;
        if (NativeReg.TrySetDword("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Search", "CortanaConsent", 0)) n++;
        return new NativeApplyStep { Id = "inbox", Status = n >= 3 ? "ok" : "partial", Reason = $"written={n}" };
    }

    private static NativeApplyStep SetWindowsUpdatePause(bool admin, List<string> elevOps)
    {
        if (!admin)
        {
            elevOps.Add(@"dword:HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU|NoAutoUpdate|1");
            elevOps.Add(@"dword:HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate|DeferFeatureUpdates|1");
            elevOps.Add(@"dword:HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate|DeferFeatureUpdatesPeriodInDays|365");
            elevOps.Add(@"dword:HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate|DeferQualityUpdates|1");
            elevOps.Add(@"dword:HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate|DeferQualityUpdatesPeriodInDays|30");
            elevOps.Add(@"dword:HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings|FlightSettingsMaxPauseDays|10000");
            if (NativeReg.MatchesDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", 1))
                return new NativeApplyStep { Id = "windows-update", Status = "ok", Reason = "already" };
            return new NativeApplyStep { Id = "windows-update", Status = "pending-elev", Reason = "needs admin" };
        }
        var n = 0;
        if (NativeReg.TrySetDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", 1)) n++;
        if (NativeReg.TrySetDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "DeferFeatureUpdates", 1)) n++;
        if (NativeReg.TrySetDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "DeferFeatureUpdatesPeriodInDays", 365)) n++;
        if (NativeReg.TrySetDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "DeferQualityUpdates", 1)) n++;
        if (NativeReg.TrySetDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "DeferQualityUpdatesPeriodInDays", 30)) n++;
        if (NativeReg.TrySetDword("HKLM", @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "FlightSettingsMaxPauseDays", 10000)) n++;
        var expiry = DateTime.UtcNow.AddYears(10).ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
        if (NativeReg.TrySetString("HKLM", @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "PauseUpdatesExpiryTime", expiry)) n++;
        var ok = NativeReg.MatchesDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", 1);
        return new NativeApplyStep { Id = "windows-update", Status = ok ? "ok" : "partial", Reason = $"written={n}" };
    }

    private static NativeApplyStep SetDefenderPurged(bool admin, List<string> elevOps)
    {
        if (!admin)
        {
            elevOps.Add(@"dword:HKLM\SOFTWARE\Policies\Microsoft\Windows Defender|DisableAntiSpyware|1");
            elevOps.Add(@"dword:HKLM\SOFTWARE\Policies\Microsoft\Windows Defender|DisableAntiVirus|1");
            elevOps.Add(@"dword:HKLM\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection|DisableRealtimeMonitoring|1");
            elevOps.Add(@"dword:HKLM\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection|DisableBehaviorMonitoring|1");
            elevOps.Add(@"dword:HKLM\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection|DisableOnAccessProtection|1");
            elevOps.Add(@"dword:HKLM\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection|DisableScanOnRealtimeEnable|1");
            elevOps.Add(@"dword:HKLM\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection|DisableIOAVProtection|1");
            elevOps.Add(@"dword:HKLM\SOFTWARE\Microsoft\Windows Defender\Features|TamperProtection|0");
            if (NativeReg.MatchesDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", 1))
                return new NativeApplyStep { Id = "defender", Status = "ok", Reason = "already" };
            return new NativeApplyStep { Id = "defender", Status = "pending-elev", Reason = "needs admin" };
        }
        var n = 0;
        if (NativeReg.TrySetDword("HKLM", @"SOFTWARE\Microsoft\Windows Defender\Features", "TamperProtection", 0)) n++;
        if (NativeReg.TrySetDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", 1)) n++;
        if (NativeReg.TrySetDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiVirus", 1)) n++;
        if (NativeReg.TrySetDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows Defender", "ServiceKeepAlive", 0)) n++;
        foreach (var name in new[]
                 {
                     "DisableRealtimeMonitoring", "DisableBehaviorMonitoring",
                     "DisableOnAccessProtection", "DisableScanOnRealtimeEnable", "DisableIOAVProtection"
                 })
        {
            if (NativeReg.TrySetDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", name, 1)) n++;
        }
        // Best-effort stop WinDefend
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "config WinDefend start= disabled",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p?.WaitForExit(5000);
            using var p2 = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "stop WinDefend",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p2?.WaitForExit(8000);
        }
        catch { }
        var ok = NativeReg.MatchesDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", 1);
        return new NativeApplyStep { Id = "defender", Status = ok ? "ok" : "fail", Reason = $"written={n}" };
    }

    private static NativeApplyStep SetHighPerfPower()
    {
        try
        {
            // Real Exo Competitive plan (Intel/AMD) + purge Ultimate Performance spam.
            // Never leave bare duplicatescheme Ultimate clones active.
            var (ok, name, guid, written, deleted) = ExoPowerPlanNative.EnsureAndActivate();
            return new NativeApplyStep
            {
                Id = "power-plan",
                Status = ok ? "ok" : "fail",
                Reason = ok
                    ? $"{name} active guid={guid}; knobs={written}; purged={deleted}"
                    : $"failed to activate {name}; purged={deleted} guid={guid}"
            };
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "power-plan", Status = "fail", Reason = ex.Message };
        }
    }

    private static NativeApplyStep SetUsbNoSelectiveSuspend(bool admin, List<string> elevOps)
    {
        if (!admin)
        {
            elevOps.Add(@"dword:HKLM\SYSTEM\CurrentControlSet\Services\USB|DisableSelectiveSuspend|1");
            // Also stamp USB power settings via powercfg (user context OK for active scheme)
            TryPowerCfg("/SETACVALUEINDEX SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0");
            TryPowerCfg("/SETDCVALUEINDEX SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0");
            TryPowerCfg("/S SCHEME_CURRENT");
            return new NativeApplyStep { Id = "usb", Status = "ok", Reason = "powercfg + elev USB reg" };
        }
        var n = 0;
        if (NativeReg.TrySetDword("HKLM", @"SYSTEM\CurrentControlSet\Services\USB", "DisableSelectiveSuspend", 1)) n++;
        TryPowerCfg("/SETACVALUEINDEX SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0");
        TryPowerCfg("/SETDCVALUEINDEX SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0");
        TryPowerCfg("/S SCHEME_CURRENT");
        return new NativeApplyStep { Id = "usb", Status = "ok", Reason = $"reg={n}; powercfg USB suspend off" };
    }

    private static NativeApplyStep PurgeNoisyExoRunKeys()
    {
        var removed = 0;
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run is null) return new NativeApplyStep { Id = "no-background", Status = "ok", Reason = "no Run key" };
            foreach (var name in run.GetValueNames().ToArray())
            {
                if (!name.StartsWith("Exo-", StringComparison.OrdinalIgnoreCase)) continue;
                var v = run.GetValue(name)?.ToString() ?? "";
                // Drop ALL Exo Run companions (yield, memory guard, any pwsh/cmd/wscript).
                if (v.Contains("yield-guard", StringComparison.OrdinalIgnoreCase) ||
                    v.Contains("wscript", StringComparison.OrdinalIgnoreCase) ||
                    v.Contains("pwsh", StringComparison.OrdinalIgnoreCase) ||
                    v.Contains("powershell", StringComparison.OrdinalIgnoreCase) ||
                    v.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
                    v.Contains("MemoryGuard", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Yield", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Memory", StringComparison.OrdinalIgnoreCase))
                {
                    try { run.DeleteValue(name, false); removed++; } catch { }
                }
            }
        }
        catch { }
        return new NativeApplyStep { Id = "no-background", Status = "ok", Reason = $"purged={removed}" };
    }

    /// <summary>
    /// PC-aware Task Scheduler quiet: enumerate THIS PC's tree, classify each task,
    /// disable only noise that is present and enabled. Then drop empty folders.
    /// Never deletes Microsoft task definitions; never touches protected stacks.
    /// </summary>
    private static NativeApplyStep DisableKnownBloatTasks()
    {
        var budgetMs = 120_000;
        var sw = Stopwatch.StartNew();
        var disabled = 0;
        var timedOut = 0;
        var skippedProtected = 0;
        var skippedAlreadyOff = 0;
        var skippedUnknown = 0;
        var seen = 0;

        // Live inventory first — every PC has a different task set.
        var live = EnumerateScheduledTasksLive(budgetMs, sw);
        seen = live.Count;
        foreach (var task in live)
        {
            if (sw.ElapsedMilliseconds > budgetMs)
            {
                timedOut++;
                break;
            }

            var decision = ClassifyScheduledTask(task.Path, task.Name, task.Enabled);
            if (decision == TaskQuietDecision.Protect)
            {
                skippedProtected++;
                continue;
            }
            if (decision == TaskQuietDecision.Leave)
            {
                skippedUnknown++;
                continue;
            }
            // Quiet
            if (!task.Enabled)
            {
                skippedAlreadyOff++;
                continue;
            }

            var tn = task.FullPath;
            var (code, _, to) = RunTimed("schtasks.exe", $"/Change /TN \"{tn}\" /DISABLE", 2500);
            if (to) timedOut++;
            else if (code == 0) disabled++;
            // access denied / race → ignore
        }

        var foldersRemoved = 0;
        if (sw.ElapsedMilliseconds < budgetMs)
            foldersRemoved = PurgeEmptyTaskFolders(budgetMs, sw);

        var status = timedOut > 0
            ? "partial"
            : disabled > 0 || foldersRemoved > 0 || skippedAlreadyOff > 0
                ? "ok"
                : seen == 0
                    ? "skip"
                    : "ok";
        return new NativeApplyStep
        {
            Id = "scheduled-tasks",
            Status = status,
            Reason =
                $"live={seen}; disabled={disabled}; alreadyOff={skippedAlreadyOff}; " +
                $"protected={skippedProtected}; leftAlone={skippedUnknown}; timedOut={timedOut}; " +
                $"emptyFoldersRemoved={foldersRemoved}; ms={sw.ElapsedMilliseconds}"
        };
    }

    private enum TaskQuietDecision
    {
        Protect,
        Quiet,
        Leave
    }

    private sealed record LiveTask(string Path, string Name, string FullPath, bool Enabled);

    /// <summary>Walk Schedule.Service on this PC (folder tree). Timeout-budgeted.</summary>
    private static List<LiveTask> EnumerateScheduledTasksLive(int budgetMs, Stopwatch sw)
    {
        var list = new List<LiveTask>(256);
        try
        {
            var t = Type.GetTypeFromProgID("Schedule.Service");
            if (t is null) return list;
            dynamic? service = Activator.CreateInstance(t);
            if (service is null) return list;
            service.Connect();
            dynamic root = service.GetFolder("\\");
            EnumerateTaskFolder(service, root, list, budgetMs, sw);
        }
        catch { /* COM unavailable */ }
        return list;
    }

    private static void EnumerateTaskFolder(
        dynamic service, dynamic folder, List<LiveTask> list, int budgetMs, Stopwatch sw)
    {
        if (sw.ElapsedMilliseconds > budgetMs) return;
        string folderPath;
        try { folderPath = (string)folder.Path; }
        catch { return; }
        if (string.IsNullOrEmpty(folderPath)) folderPath = "\\";
        if (!folderPath.EndsWith('\\')) folderPath += "\\";

        try
        {
            dynamic tasks = folder.GetTasks(0);
            foreach (dynamic task in tasks)
            {
                if (sw.ElapsedMilliseconds > budgetMs) return;
                try
                {
                    string name = (string)task.Name;
                    if (string.IsNullOrEmpty(name)) continue;
                    // Enabled property: 1 enabled, 0 disabled (Task Scheduler 2.0)
                    var enabled = true;
                    try { enabled = Convert.ToInt32(task.Enabled) != 0; }
                    catch { enabled = true; }
                    var full = folderPath == "\\" ? "\\" + name : folderPath.TrimEnd('\\') + "\\" + name;
                    // schtasks wants leading backslash path
                    if (!full.StartsWith('\\')) full = "\\" + full;
                    list.Add(new LiveTask(folderPath, name, full, enabled));
                }
                catch { /* skip bad task */ }
            }
        }
        catch { /* no tasks */ }

        try
        {
            dynamic children = folder.GetFolders(0);
            var childPaths = new List<string>();
            foreach (dynamic child in children)
            {
                try { childPaths.Add((string)child.Path); }
                catch { }
            }
            foreach (var cp in childPaths)
            {
                if (sw.ElapsedMilliseconds > budgetMs) return;
                try
                {
                    dynamic child = service.GetFolder(cp);
                    EnumerateTaskFolder(service, child, list, budgetMs, sw);
                }
                catch { }
            }
        }
        catch { /* no subfolders */ }
    }

    /// <summary>
    /// Classify a live task. Rules are portable across PCs — match path/name patterns,
    /// not a single machine's GUID list.
    /// </summary>
    private static TaskQuietDecision ClassifyScheduledTask(string folderPath, string name, bool enabled)
    {
        var path = (folderPath + name).Replace('/', '\\');
        var full = path;

        // ── Always protect (security / recovery / shell / AC / user tools) ──
        if (IsProtectedScheduledTask(full, name))
            return TaskQuietDecision.Protect;

        // ── Quiet: Xbox game-save noise ──
        if (full.Contains(@"\XblGameSave\", StringComparison.OrdinalIgnoreCase))
            return TaskQuietDecision.Quiet;

        // ── Quiet: root-level browser / store updaters (GUIDs differ per PC) ──
        if (folderPath is "\\" or "")
        {
            if (name.StartsWith("MicrosoftEdgeUpdate", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("BraveSoftwareUpdate", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("GoogleUpdate", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("GoogleUpdater", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Adobe Acrobat Update", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("AdobeAAMUpdater", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("CCleaner", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("EqualizerAPOUpdateChecker", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("UpdateTaskMachine", StringComparison.OrdinalIgnoreCase) ||
                (name.Contains("Update", StringComparison.OrdinalIgnoreCase) &&
                 name.Contains("Machine", StringComparison.OrdinalIgnoreCase) &&
                 !name.Contains("Windows", StringComparison.OrdinalIgnoreCase)))
                return TaskQuietDecision.Quiet;
        }

        // ── Quiet: common third-party OEM telemetry folders ──
        if (folderPath.Contains(@"\Microsoft\Windows\WindowsAI\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\Customer Experience", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\Application Experience\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\Feedback\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\Flighting\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\UsageAndQualityInsights\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\DeviceDirectoryClient\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\Device Directory Client\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\UpdateOrchestrator\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\InstallService\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\EDP\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\Maps\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\CloudExperienceHost\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\CloudRestore\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\PushToInstall\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\RetailDemo\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\Sustainability\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\SpacePort\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\Work Folders\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\WaaSMedic\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\SettingSync\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\AppListBackup\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\PerformanceTrace\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\PI\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\Diagnosis\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\DiskFootprint\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\DiskCleanup\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\Defrag\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\DiskDiagnostic\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\MemoryDiagnostic\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\Power Efficiency\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\Maintenance\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\Location\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\Speech\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\Windows Media Sharing\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\UPnP\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\WOF\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\DUSM\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\DirectX\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\Subscription\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\Clip\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\Device Information\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\Management\Provisioning\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\International\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\LanguageComponentsInstaller\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\Windows Error Reporting\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\Customer Experience Improvement\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\Windows Defender\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\WindowsUpdate\", StringComparison.OrdinalIgnoreCase) ||
            folderPath.Contains(@"\Microsoft\Windows\NlaSvc\", StringComparison.OrdinalIgnoreCase))
            return TaskQuietDecision.Quiet;

        // Shell family safety / picture updaters only (not all Shell)
        if (folderPath.Contains(@"\Microsoft\Windows\Shell\", StringComparison.OrdinalIgnoreCase) &&
            (name.Contains("FamilySafety", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("UpdateUserPicture", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("IndexerAutomaticMaintenance", StringComparison.OrdinalIgnoreCase)))
            return TaskQuietDecision.Quiet;

        // Unknown third-party: leave alone (PC-aware = don't invent policy for random apps)
        _ = enabled;
        return TaskQuietDecision.Leave;
    }

    private static bool IsProtectedScheduledTask(string full, string name)
    {
        if (name.Contains("cua-driver", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("CreateExplorerShell", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("Vanguard", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("FACEIT", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("BattlEye", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Ricochet", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Steam", StringComparison.OrdinalIgnoreCase) &&
            name.Contains("Service", StringComparison.OrdinalIgnoreCase))
            return true;

        // Security / recovery / credentials / integrity
        if (full.Contains("BitLocker", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\TPM\", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("CertificateServices", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("SystemRestore", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("RecoveryEnvironment", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("Data Integrity", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\Chkdsk\", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("SecureBoot", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("Pluton", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("BrokerInfrastructure", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("SystemSoundsService", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\Multimedia\", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\Plug and Play\", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\Time Synchronization\", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\Time Zone\", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@".NET Framework\", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\Registry\", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\Servicing\", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\SoftwareProtectionPlatform\", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\WindowsColorSystem\", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\TextServicesFramework\", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\StateRepository\", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\AppxDeploymentClient\", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\User Profile Service\", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\Bluetooth\", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\USB\", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\Setup\", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\ApplicationData\", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\AppID\", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Delete Task Scheduler folders that contain zero tasks and zero subfolders.
    /// Bottom-up via Schedule.Service COM. Never deletes root.
    /// </summary>
    private static int PurgeEmptyTaskFolders(int budgetMs, Stopwatch sw)
    {
        try
        {
            var t = Type.GetTypeFromProgID("Schedule.Service");
            if (t is null) return 0;
            dynamic? service = Activator.CreateInstance(t);
            if (service is null) return 0;
            service.Connect();
            dynamic root = service.GetFolder("\\");
            return PurgeEmptyTaskFolderRecursive(service, root, true, budgetMs, sw);
        }
        catch
        {
            return 0;
        }
    }

    private static int PurgeEmptyTaskFolderRecursive(
        dynamic service, dynamic folder, bool isRoot, int budgetMs, Stopwatch sw)
    {
        var removed = 0;
        try
        {
            string path;
            try { path = (string)folder.Path; }
            catch { return 0; }

            // Collect child folder paths first (COM collections invalidate on mutation)
            var childPaths = new List<string>();
            try
            {
                dynamic children = folder.GetFolders(0);
                foreach (dynamic child in children)
                {
                    try { childPaths.Add((string)child.Path); }
                    catch { /* skip */ }
                }
            }
            catch { /* no children API */ }

            foreach (var childPath in childPaths)
            {
                if (sw.ElapsedMilliseconds > budgetMs) return removed;
                try
                {
                    dynamic child = service.GetFolder(childPath);
                    removed += PurgeEmptyTaskFolderRecursive(service, child, false, budgetMs, sw);
                }
                catch { /* gone */ }
            }

            if (isRoot) return removed;
            if (sw.ElapsedMilliseconds > budgetMs) return removed;

            // Protect security stacks even if emptied
            if (path.Contains("BitLocker", StringComparison.OrdinalIgnoreCase) ||
                path.Contains(@"\TPM", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("CertificateServices", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("SystemRestore", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("RecoveryEnvironment", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Windows Defender", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Data Integrity", StringComparison.OrdinalIgnoreCase))
                return removed;

            int taskCount = 0;
            int folderCount = 0;
            try { taskCount = (int)folder.GetTasks(0).Count; } catch { return removed; }
            try { folderCount = (int)folder.GetFolders(0).Count; } catch { return removed; }
            if (taskCount != 0 || folderCount != 0) return removed;

            // Delete via parent
            var trimmed = path.TrimEnd('\\');
            var slash = trimmed.LastIndexOf('\\');
            if (slash < 0) return removed;
            var parentPath = slash == 0 ? "\\" : trimmed[..slash];
            var leaf = trimmed[(slash + 1)..];
            if (string.IsNullOrEmpty(leaf)) return removed;
            try
            {
                dynamic parent = service.GetFolder(parentPath);
                parent.DeleteFolder(leaf, 0);
                removed++;
            }
            catch
            {
                /* access denied / not empty race */
            }
        }
        catch
        {
            /* folder vanished */
        }

        return removed;
    }

    /// <summary>
    /// Bounded DISM shortlist. Never uses Get-WindowsOptionalFeature (hangs).
    /// Each dism.exe call hard-killed after timeout.
    /// </summary>
    private static NativeApplyStep DisableOptionalFeaturesShortlist()
    {
        var features = new[]
        {
            "SMB1Protocol",
            "SMB1Protocol-Client",
            "SMB1Protocol-Server",
            "FaxServicesClientPackage",
            "Printing-XPSServices-Features",
            "WorkFolders-Client",
            "SimpleTCP",
            "Internet-Explorer-Optional-amd64",
            "MicrosoftWindowsPowerShellV2Root",
            "MicrosoftWindowsPowerShellV2",
            "LegacyComponents",
            "DirectPlay",
            "WindowsMediaPlayer",
            "MediaPlayback",
            "Printing-Foundation-Features",
            "Printing-Foundation-InternetPrinting-Client",
            "MSRDC-Infrastructure",
            "SearchEngine-Client-Package",
        };
        var disabled = 0;
        var alreadyOff = 0;
        var notPresent = 0;
        var failed = 0;
        var timedOut = 0;
        var budgetMs = 120_000; // max 2 min for all DISM
        var sw = Stopwatch.StartNew();

        foreach (var name in features)
        {
            if (sw.ElapsedMilliseconds > budgetMs)
            {
                timedOut++;
                break;
            }

            var (qCode, outText, qTo) = RunTimed(
                "dism.exe",
                $"/Online /Get-FeatureInfo /FeatureName:{name}",
                8000);
            if (qTo) { timedOut++; continue; }
            if (qCode != 0 || string.IsNullOrEmpty(outText)) { notPresent++; continue; }
            if (outText.Contains("State : Disabled", StringComparison.OrdinalIgnoreCase) ||
                outText.Contains("State : Disable Pending", StringComparison.OrdinalIgnoreCase))
            {
                alreadyOff++;
                continue;
            }
            if (!outText.Contains("State : Enabled", StringComparison.OrdinalIgnoreCase))
            {
                notPresent++;
                continue;
            }

            var (dCode, _, dTo) = RunTimed(
                "dism.exe",
                $"/Online /Disable-Feature /FeatureName:{name} /NoRestart",
                25_000);
            if (dTo) { timedOut++; continue; }
            if (dCode is 0 or 3010) disabled++;
            else failed++;
        }

        // ok = nothing left enabled that we failed on; no timeouts
        // skip = no enabled targets existed (all absent/already off)
        // partial = timeouts or disable failures
        string status;
        if (timedOut > 0 || failed > 0)
            status = "partial";
        else if (disabled > 0 || alreadyOff > 0)
            status = "ok";
        else
            status = "skip"; // nothing actionable on this SKU

        return new NativeApplyStep
        {
            Id = "optional-features",
            Status = status,
            Reason =
                $"disabled={disabled}; alreadyOff={alreadyOff}; notPresent={notPresent}; failed={failed}; timedOut={timedOut}; list={features.Length}; ms={sw.ElapsedMilliseconds}"
        };
    }

    private static void TryPowerCfg(string args)
    {
        RunTimed("powercfg", args, 8000);
    }

    private static string RunCapture(string file, string args)
    {
        var (_, o, _) = RunTimed(file, args, 8000);
        return o;
    }

    private static void SaveState(bool ok, bool experimental, List<NativeApplyStep> steps, List<string> elevOps)
    {
        try
        {
            var path = Path.Combine(PathHelper.AppDataDir, StateFileName);
            Directory.CreateDirectory(PathHelper.AppDataDir);
            var powerOk = steps.Any(s => s.Id == "power-plan" && s.Status == "ok");
            var state = new Dictionary<string, object?>
            {
                ["version"] = Version,
                ["applyStatus"] = ok ? "applied" : "incomplete",
                ["applied"] = ok,
                ["appliedUtc"] = DateTime.UtcNow.ToString("o"),
                ["experimental"] = experimental,
                ["path"] = "native-csharp",
                ["gameBarQuiet"] = steps.Any(s => s.Id == "game-bar" && s.Status is "ok" or "partial"),
                ["hags"] = steps.Any(s => s.Id == "hags" && s.Status == "ok") ||
                           NativeReg.MatchesDword("HKLM", @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2),
                ["gameMode"] = steps.Any(s => s.Id == "game-mode" && s.Status == "ok"),
                ["win32Priority"] = NativeReg.MatchesDword("HKLM", @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 38),
                ["mousePrecision"] = steps.Any(s => (s.Id is "mouse" or "input-pack") && s.Status == "ok"),
                ["stickyKeys"] = steps.Any(s => s.Id == "sticky-keys" && s.Status == "ok"),
                ["menuShowDelay"] = steps.Any(s => s.Id == "menu-snap" && s.Status == "ok"),
                ["hostLatencyOk"] = steps.Any(s => s.Id == "host-latency" && s.Status == "ok") ||
                                    NativeReg.MatchesDword("HKLM",
                                        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                                        "SystemResponsiveness", 10),
                ["powerPlanOk"] = powerOk && ExoPowerPlanNative.IsExoPlanActive(),
                ["powerPlanName"] = ExoPowerPlanNative.TargetNameForCpu(),
                ["powerPlanGuid"] = ExoPowerPlanNative.GetActiveGuid(),
                ["amoledOk"] = steps.Any(s => s.Id == "amoled" && s.Status == "ok"),
                ["uacOff"] = steps.Any(s => s.Id == "uac" && s.Status == "ok") ||
                             NativeReg.MatchesDword("HKLM",
                                 @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System",
                                 "ConsentPromptBehaviorAdmin", 0),
                ["windowsAiGone"] = steps.Any(s => s.Id == "windows-ai" && s.Status is "ok" or "skip"),
                ["explorerDecluttered"] = steps.Any(s => s.Id == "explorer" && s.Status is "ok" or "partial"),
                ["windowsUpdatePaused"] = steps.Any(s => s.Id == "windows-update" && s.Status == "ok") ||
                                          NativeReg.MatchesDword("HKLM",
                                              @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU",
                                              "NoAutoUpdate", 1),
                ["defenderPurged"] = steps.Any(s => s.Id == "defender" && s.Status == "ok") ||
                                     NativeReg.MatchesDword("HKLM",
                                         @"SOFTWARE\Policies\Microsoft\Windows Defender",
                                         "DisableAntiSpyware", 1),
                ["controllersOk"] = NativeReg.MatchesDword("HKLM",
                                        @"SYSTEM\CurrentControlSet\Services\USB", "DisableSelectiveSuspend", 1)
                                    || steps.Any(s => s.Id == "usb" && s.Status == "ok"),
                ["noBackgroundOk"] = steps.Any(s => s.Id == "no-background" && s.Status == "ok"),
                // Deep-pass flags only when the step truly completed without timeout/fail.
                // "skip" means nothing to do (OK for apply) but detect still live-probes.
                ["scheduledTasksOk"] = steps.Any(s => s.Id == "scheduled-tasks" && s.Status is "ok" or "skip"),
                ["scheduledTasksDeepPass"] = IsTaskDeepPass(steps),
                ["scheduledTasksPct"] = TaskQuietPct(steps),
                ["optionalFeaturesOk"] = steps.Any(s => s.Id == "optional-features" && s.Status is "ok" or "skip"),
                ["optionalFeaturesDeepPass"] = IsOptionalDeepPass(steps),
                ["optionalFeaturesDisabled"] = ParseReasonInt(steps, "optional-features", "disabled")
                                               + ParseReasonInt(steps, "optional-features", "alreadyOff"),
                ["pendingElevOps"] = elevOps,
                ["applyReport"] = steps.Select(s => s.ToReportLine()).ToList()
            };
            File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }

    private static bool IsTaskDeepPass(List<NativeApplyStep> steps)
    {
        var s = steps.FirstOrDefault(x => x.Id == "scheduled-tasks");
        if (s is null || s.Status is not ("ok" or "skip")) return false;
        // Require real disables OR clean skip (nothing present) — never partial/timeout
        if (s.Status == "skip") return true;
        var disabled = ParseReasonField(s.Reason, "disabled");
        var timedOut = ParseReasonField(s.Reason, "timedOut");
        return timedOut == 0 && disabled > 0;
    }

    private static bool IsOptionalDeepPass(List<NativeApplyStep> steps)
    {
        var s = steps.FirstOrDefault(x => x.Id == "optional-features");
        if (s is null || s.Status is not ("ok" or "skip")) return false;
        var failed = ParseReasonField(s.Reason, "failed");
        var timedOut = ParseReasonField(s.Reason, "timedOut");
        if (failed > 0 || timedOut > 0) return false;
        // ok with alreadyOff/disabled, or skip with nothing actionable
        return true;
    }

    private static double TaskQuietPct(List<NativeApplyStep> steps)
    {
        var s = steps.FirstOrDefault(x => x.Id == "scheduled-tasks");
        if (s is null) return 0;
        var disabled = ParseReasonField(s.Reason, "disabled");
        var list = ParseReasonField(s.Reason, "list");
        if (list <= 0) return 0;
        return Math.Round(100.0 * disabled / list, 1);
    }

    private static int ParseReasonInt(List<NativeApplyStep> steps, string id, string field)
    {
        var s = steps.FirstOrDefault(x => x.Id == id);
        return s is null ? 0 : ParseReasonField(s.Reason, field);
    }

    private static int ParseReasonField(string? reason, string field)
    {
        if (string.IsNullOrEmpty(reason)) return 0;
        var m = System.Text.RegularExpressions.Regex.Match(
            reason, $@"\b{System.Text.RegularExpressions.Regex.Escape(field)}=(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }
}
