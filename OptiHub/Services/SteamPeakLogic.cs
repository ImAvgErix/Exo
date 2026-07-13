using System.Text.RegularExpressions;

namespace OptiHub.Services;

/// <summary>
/// Pure Steam detect classifiers (no I/O). Aligned with Scripts/Steam/SteamDetectCore.ps1.
/// </summary>
public static class SteamPeakLogic
{
    public static bool IsCefLauncherText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Regex.IsMatch(text, @"(?i)steam\.exe") &&
               text.Contains("-cef-disable-gpu", StringComparison.OrdinalIgnoreCase) &&
               text.Contains("-nofriendsui", StringComparison.OrdinalIgnoreCase) &&
               text.Contains("-nointro", StringComparison.OrdinalIgnoreCase) &&
               Regex.IsMatch(text, @"(?i)start\s+""""\s+/HIGH");
    }

    /// <summary>
    /// WebHelper trim helper: marker + EmptyWorkingSet + priority classes + sleep 2–15s.
    /// Accepts Seconds or Milliseconds; does not hardcode only "Seconds 5".
    /// </summary>
    public static bool IsTrimHelperText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!text.Contains("OptiHub.SteamWebHelper", StringComparison.Ordinal)) return false;
        if (!text.Contains("EmptyWorkingSet", StringComparison.Ordinal)) return false;
        if (!text.Contains("ProcessPriorityClass]::High", StringComparison.Ordinal)) return false;
        if (!text.Contains("ProcessPriorityClass]::BelowNormal", StringComparison.Ordinal)) return false;

        var sec = Regex.Match(text, @"Start-Sleep\s+-Seconds\s+(\d+)", RegexOptions.IgnoreCase);
        if (sec.Success && int.TryParse(sec.Groups[1].Value, out var s) && s is >= 2 and <= 15)
            return true;

        var ms = Regex.Match(text, @"Start-Sleep\s+-Milliseconds\s+(\d+)", RegexOptions.IgnoreCase);
        if (ms.Success && int.TryParse(ms.Groups[1].Value, out var m) && m is >= 2000 and <= 15000)
            return true;

        return false;
    }

    public static bool AreToastsOff(IReadOnlyDictionary<string, int?> enabledById)
    {
        if (enabledById is null || enabledById.Count == 0) return false;
        var seen = false;
        foreach (var kv in enabledById)
        {
            if (kv.Value is null) continue;
            seen = true;
            if (kv.Value.Value != 0) return false;
        }
        return seen;
    }

    public static bool LegacyAggressiveCmdNamesAbsent(IEnumerable<string>? fileNamesInSteamRoot)
    {
        if (fileNamesInSteamRoot is null) return true;
        var banned = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Steam-OptiHub-Aggressive.cmd",
            "Steam-OptiHub-Lean.cmd",
            "Steam-OptiHub-Legacy.cmd"
        };
        return !fileNamesInSteamRoot.Any(banned.Contains);
    }

    public static readonly string[] RequiredApplyMarkers =
    {
        "Steam-OptiHub.cmd",
        "OptiHub-SteamWebHelperTrim",
        "-cef-disable-gpu",
        "/HIGH",
        "IsPromoted",
        "EmptyWorkingSet",
    };

    public static readonly string[] ForbiddenApplyPatterns =
    {
        "Register-ScheduledTask -TaskName 'OptiHub-Steam",
        "schtasks /Create /TN \"OptiHub-Steam",
        "MaxUserPort",
        "FPS Unlocker",
        "Win32PrioritySeparation",
    };

    public static (bool Ok, List<string> Issues) AuditApplyScriptText(string script)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(script))
        {
            issues.Add("empty");
            return (false, issues);
        }
        foreach (var m in RequiredApplyMarkers)
        {
            if (script.IndexOf(m, StringComparison.OrdinalIgnoreCase) < 0)
                issues.Add("missing: " + m);
        }
        foreach (var f in ForbiddenApplyPatterns)
        {
            if (script.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                issues.Add("forbidden: " + f);
        }
        return (issues.Count == 0, issues);
    }
}
