using System.Text.RegularExpressions;

namespace Exo.Services;

/// <summary>
/// Pure Discord detect classifiers (no I/O). Keep aligned with
/// Scripts/Discord/DiscordDetectCore.ps1 — host heuristic + smokes drive this type.
/// </summary>
public static class DiscordPeakLogic
{
    public static bool IsStableDiscordPathText(string? text, string root)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var expanded = Environment.ExpandEnvironmentVariables(text).Replace('/', '\\');
            return expanded.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    public static bool IsEquicordLoaderText(string? text, long length)
    {
        if (length is < 64 or >= 4096) return false;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("equicord.asar", StringComparison.OrdinalIgnoreCase) &&
               text.Contains("require", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOpenAsarSize(long sizeBytes) =>
        sizeBytes is > 10_000 and < 500_000;

    public static bool IsKernelLayout(long ffmpegProxyBytes, long ffmpegRealBytes, long versionDllBytes) =>
        ffmpegProxyBytes is > 0 and < 500_000 &&
        ffmpegRealBytes > 500_000 &&
        versionDllBytes > 50_000;

    /// <summary>
    /// Valid gaming DiscOpt config: EnableTrim=1, PriorityClass=3, TrimIntervalMs in 2000–15000.
    /// Accepts kit (4000) and prior peak applies (5000) — does not hardcode a single interval.
    /// </summary>
    public static bool IsKernelConfigText(string? configText)
    {
        if (string.IsNullOrWhiteSpace(configText)) return false;
        if (!Regex.IsMatch(configText, @"(?m)^\s*EnableTrim\s*=\s*1\s*$")) return false;
        if (!Regex.IsMatch(configText, @"(?m)^\s*PriorityClass\s*=\s*3\s*$")) return false;
        var m = Regex.Match(configText, @"(?m)^\s*TrimIntervalMs\s*=\s*(\d+)\s*$");
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups[1].Value, out var trimMs)) return false;
        return trimMs is >= 2000 and <= 15000;
    }

    public static bool IsKernelApplied(
        long ffmpegProxyBytes,
        long ffmpegRealBytes,
        long versionDllBytes,
        string? configText,
        bool proxyHashMatchesKit,
        bool versionHashMatchesKit)
    {
        if (!IsKernelLayout(ffmpegProxyBytes, ffmpegRealBytes, versionDllBytes)) return false;
        if (!IsKernelConfigText(configText)) return false;
        return proxyHashMatchesKit && versionHashMatchesKit;
    }

    /// <summary>
    /// Toast policy: at least one Discord notification key present; every present key Enabled=0.
    /// Missing keys are ignored (not a hard fail for every known id).
    /// </summary>
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

    public static bool IsQuickStartSettingsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        // Minimal structural check without full JSON dependency for smoke fixtures
        return Regex.IsMatch(json, @"""quickstart""\s*:\s*true", RegexOptions.IgnoreCase) &&
               Regex.IsMatch(json, @"""openasar""", RegexOptions.IgnoreCase);
    }

    public static bool IsStartupOffSettingsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        return Regex.IsMatch(json, @"""OPEN_ON_STARTUP""\s*:\s*false", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Complete client debloat classifier.
    /// Hard: leftover app-* builds and optional hook/clips modules that still have payload files.
    /// Soft: game SDK DLLs and extra locale paks (updater may re-add) — recoverable via verified state
    /// only when hard signals are still clean. Never trust state when hard fails.
    /// </summary>
    public static bool IsClientDebloatApplied(
        int leftoverAppBuildCount,
        int optionalModulePayloadCount,
        int gameSdkFileCount,
        int extraLocaleCount,
        bool stateDebloatVerifiedSameApp)
    {
        if (leftoverAppBuildCount < 0) leftoverAppBuildCount = 0;
        if (optionalModulePayloadCount < 0) optionalModulePayloadCount = 0;
        if (gameSdkFileCount < 0) gameSdkFileCount = 0;
        if (extraLocaleCount < 0) extraLocaleCount = 0;

        var hardOk = leftoverAppBuildCount == 0 && optionalModulePayloadCount == 0;
        var softOk = gameSdkFileCount == 0 && extraLocaleCount == 0;
        if (hardOk && softOk) return true;
        // Soft-drift recovery only — never mask leftover builds / payload modules.
        if (hardOk && stateDebloatVerifiedSameApp) return true;
        return false;
    }

    /// <summary>True when a module directory exists and contains at least one file (empty recreated dirs ≠ present).</summary>
    public static bool ModuleDirHasPayload(string? moduleDir)
    {
        if (string.IsNullOrWhiteSpace(moduleDir) || !Directory.Exists(moduleDir)) return false;
        try
        {
            return Directory.EnumerateFiles(moduleDir, "*", SearchOption.AllDirectories).Any();
        }
        catch
        {
            // Unreadable with path present: treat as payload to avoid false-clean.
            return true;
        }
    }

    /// <summary>Forbidden apply markers (folklore / scheduled task noise).</summary>
    public static readonly string[] ForbiddenApplyPatterns =
    {
        "Register-ScheduledTask -TaskName 'Exo-Discord",
        "schtasks /Create /TN \"Exo-Discord",
        "MaxUserPort",
        "SystemResponsiveness' 0",
        "FPS Unlocker",
        "Win32PrioritySeparation",
    };

    public static readonly string[] RequiredApplyMarkers =
    {
        "Install-DiscOptKernel",
        "Apply-WindowsTweaks",
        "IsPromoted",
        "OPEN_ON_STARTUP",
        "EXO",
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
