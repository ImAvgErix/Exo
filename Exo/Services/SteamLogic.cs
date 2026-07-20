using System.Text.RegularExpressions;

namespace Exo.Services;

/// <summary>
/// Pure Steam detect classifiers (no I/O). Aligned with Scripts/Steam/SteamDetectCore.ps1.
/// </summary>
public static partial class SteamLogic
{
    [GeneratedRegex(@"(?i)steam\.exe")]
    private static partial Regex SteamExeRegex();

    [GeneratedRegex(@"(?i)start\s+""""\s+/HIGH")]
    private static partial Regex HighPriorityStartRegex();

    [GeneratedRegex(@"Start-Sleep\s+-Seconds\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SleepSecondsRegex();

    [GeneratedRegex(@"Start-Sleep\s+-Milliseconds\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SleepMillisecondsRegex();

    public static bool IsCefLauncherText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        // GPU-disable CEF flags blank modern steamwebhelper — treat as invalid.
        if (text.Contains("-cef-disable-gpu", StringComparison.OrdinalIgnoreCase))
            return false;
        return SteamExeRegex().IsMatch(text) &&
               text.Contains("-nofriendsui", StringComparison.OrdinalIgnoreCase) &&
               text.Contains("-nointro", StringComparison.OrdinalIgnoreCase) &&
               HighPriorityStartRegex().IsMatch(text);
    }

    /// <summary>
    /// Steam background policy: low memory priority for background renderers, normal
    /// foreground behavior, in-game CPU yield, and quiet-start re-enforcement.
    /// It must never force-trim, suspend, cap, or kill Chromium processes.
    /// </summary>
    public static bool IsMemoryGuardText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!text.Contains("Exo.SteamMemoryGuard", StringComparison.Ordinal)) return false;
        if (!text.Contains("SetProcessInformation", StringComparison.Ordinal)) return false;
        if (!text.Contains("SetMemoryPriority", StringComparison.Ordinal)) return false;
        if (!text.Contains("SetPowerThrottled", StringComparison.Ordinal)) return false;
        if (!text.Contains("ForegroundPid", StringComparison.Ordinal)) return false;
        if (!text.Contains("ProcessPriorityClass]::Normal", StringComparison.Ordinal)) return false;
        if (!text.Contains("ProcessPriorityClass]::BelowNormal", StringComparison.Ordinal)) return false;
        if (!text.Contains("$steamCls = if ($InGame)", StringComparison.Ordinal)) return false;
        if (!text.Contains("$backgroundWebCls = if ($InGame)", StringComparison.Ordinal)) return false;
        if (!text.Contains("$webCls = if ($_.Id -eq $foregroundPid)", StringComparison.Ordinal)) return false;
        if (!text.Contains("$_.PriorityClass = $webCls", StringComparison.Ordinal)) return false;
        if (!Regex.IsMatch(text,
            @"(?s)\$memoryPriority\s*=\s*if\s*\(\$_.Id\s*-eq\s*\$foregroundPid\).*?5.*?elseif\s*\(\$InGame\).*?1.*?else\s*\{\s*2\s*\}")) return false;
        // EcoQoS / soft reclaim gated on non-foreground (v3: library + in-game).
        if (!text.Contains("SetPowerThrottled($_.Id, ($_.Id -ne $foregroundPid))", StringComparison.Ordinal) &&
            !text.Contains("SetPowerThrottled($_.Id, ($InGame -and $_.Id -ne $foregroundPid))", StringComparison.Ordinal))
            return false;
        // EmptyWorkingSet always thrashing CEF — banned in any code line.
        // SoftReclaimWorkingSet allowed when gated on non-foreground CEF.
        var allowsSoftReclaim = text.Contains("SoftReclaimWorkingSet", StringComparison.Ordinal)
            && text.Contains("$_.Id -ne $foregroundPid", StringComparison.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimStart();
            if (line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.Contains("EmptyWorkingSet(", StringComparison.Ordinal)) return false;
            if (line.Contains("SetProcessWorkingSetSize", StringComparison.Ordinal) && !allowsSoftReclaim)
                return false;
            if (line.Contains("Stop-Process", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("steamwebhelper", StringComparison.OrdinalIgnoreCase)) return false;
            if (line.Contains("Suspend-Process", StringComparison.OrdinalIgnoreCase)) return false;
        }

        // Competitive cadence: 1s in-game / 2s library. Any loop sleep in 1-15s is valid.
        // Do not require the first match (often the 1s game branch) to be >= 2.
        foreach (Match sec in SleepSecondsRegex().Matches(text))
        {
            if (int.TryParse(sec.Groups[1].Value, out var s) && s is >= 1 and <= 15)
                return true;
        }

        foreach (Match ms in SleepMillisecondsRegex().Matches(text))
        {
            if (int.TryParse(ms.Groups[1].Value, out var m) && m is >= 1000 and <= 15000)
                return true;
        }

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
            "Steam-Exo-Aggressive.cmd",
            "Steam-Exo-Lean.cmd",
            "Steam-Exo-Legacy.cmd"
        };
        return !fileNamesInSteamRoot.Any(banned.Contains);
    }

    public static readonly string[] RequiredApplyMarkers =
    {
        "Steam-Exo.cmd",
        "Exo-SteamMemoryGuard",
        "SetProcessInformation",
        "SetMemoryPriority",
        "ForegroundPid",
        "-cef-disable-gpu",
        "/HIGH",
        "IsPromoted",
        // Structured last-apply report persisted to steam-optimizer.json
        "EXO_REPORT:",
        "applyReport",
        // VDF-aware injector (insert missing target keys, .exo-bak first)
        "Set-SteamVdfKeyAtPath",
        ".exo-bak",
        // Stable PowerShell 7 host resolution (never 5.1, preview only fallback)
        "Get-ExoPwsh",
        "winget install Microsoft.PowerShell",
    };

    public static readonly string[] ForbiddenApplyPatterns =
    {
        "Register-ScheduledTask -TaskName 'Exo-Steam",
        "schtasks /Create /TN \"Exo-Steam",
        "MaxUserPort",
        "FPS Unlocker",
        "Win32PrioritySeparation",
        // CEF flags with documented client blanking/hangs
        "-cef-disable-occlusion",
        "-cef-disable-renderer-accessibility",
        // Steam must not start minimized from explicit Start Menu launches
        "'-silent'",
        // Preview-host hard requirement was replaced by stable pwsh 7
        "Assert-ExoPwshPreview",
        "Test-ExoIsPwshPreviewHost",
        "Microsoft.PowerShell.Preview",
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
