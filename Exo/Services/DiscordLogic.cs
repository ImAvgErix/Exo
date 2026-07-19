using System.Text.RegularExpressions;

namespace Exo.Services;

/// <summary>
/// Pure Discord detect classifiers (no I/O). Keep aligned with
/// Scripts/Discord/DiscordDetectCore.ps1 — host heuristic + smokes drive this type.
/// </summary>
public static partial class DiscordLogic
{
    [GeneratedRegex(@"(?m)^\s*EnableTrim\s*=\s*1\s*$")]
    private static partial Regex EnableTrimRegex();

    [GeneratedRegex(@"(?m)^\s*PriorityClass\s*=\s*3\s*$")]
    private static partial Regex PriorityClassRegex();

    [GeneratedRegex(@"(?m)^\s*TrimIntervalMs\s*=\s*(\d+)\s*$")]
    private static partial Regex TrimIntervalRegex();

    [GeneratedRegex(@"""SKIP_HOST_UPDATE""\s*:\s*true", RegexOptions.IgnoreCase)]
    private static partial Regex SkipHostUpdateTrueRegex();

    [GeneratedRegex(@"""chromiumSwitches""", RegexOptions.IgnoreCase)]
    private static partial Regex ChromiumSwitchesKeyRegex();

    [GeneratedRegex(@"""OPEN_ON_STARTUP""\s*:\s*false", RegexOptions.IgnoreCase)]
    private static partial Regex StartupOffRegex();

    public static bool IsStableDiscordPathText(string? text, string root)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            // Discord installs are Windows-only. Normalize to backslashes for a stable
            // string compare so Linux smoke harnesses can exercise the classifier too
            // (Path.GetFullPath on a C:\... root is not meaningful on non-Windows).
            var expanded = Environment.ExpandEnvironmentVariables(text).Replace('/', '\\');
            string prefix;
            if (OperatingSystem.IsWindows())
            {
                prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + '\\';
            }
            else
            {
                prefix = root.Replace('/', '\\').TrimEnd('\\') + '\\';
            }
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

    /// <summary>
    /// Universal Discord variant map (stable + PTB + Canary).
    /// Keep in sync with Get-DiscOptVariantDefinitions in DiscordDetectCore.ps1.
    /// </summary>
    public static readonly (string Name, string LocalDir, string AppDataDir, string Exe, string QosPolicy)[] VariantDefinitions =
    {
        ("stable", "Discord", "discord", "Discord.exe", "Exo Discord Voice"),
        ("ptb", "DiscordPTB", "discordptb", "DiscordPTB.exe", "Exo Discord PTB Voice"),
        ("canary", "DiscordCanary", "discordcanary", "DiscordCanary.exe", "Exo Discord Canary Voice"),
    };

    /// <summary>
    /// True when a QoS policy value map matches the documented Exo Discord voice
    /// policy (DSCP 46, UDP, no throttle). Keep in sync with Test-DiscOptQosPolicyMap.
    /// </summary>
    public static bool IsQosPolicyMap(IReadOnlyDictionary<string, string?>? map, string? expectedExe = null)
    {
        if (map is null || map.Count == 0) return false;
        foreach (var (name, value) in new[]
                 {
                     ("Version", "1.0"),
                     ("Protocol", "UDP"),
                     ("DSCP Value", "46"),
                     ("Throttle Rate", "-1"),
                 })
        {
            if (!map.TryGetValue(name, out var actual)) return false;
            if (!string.Equals(actual, value, StringComparison.Ordinal)) return false;
        }
        if (!map.TryGetValue("Application Name", out var app) || string.IsNullOrWhiteSpace(app)) return false;
        if (!string.IsNullOrWhiteSpace(expectedExe) &&
            !string.Equals(app, expectedExe, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Keep in sync with Test-DiscOptVariantOptimized.</summary>
    public static bool IsVariantOptimized(bool settingsFlagsOk, bool autostartQuiet, bool qosOk) =>
        settingsFlagsOk && autostartQuiet && qosOk;

    /// <summary>
    /// Variant (PTB/Canary) settings.json: chromium lean present.
    /// Does not require OPEN_ON_STARTUP=false — that is a Discord in-app pref and
    /// must stay under the user's control (Windows autostart is handled via Run key).
    /// Keep in sync with Test-DiscOptVariantSettingsJson.
    /// </summary>
    public static bool IsVariantSettingsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        return ChromiumSwitchesKeyRegex().IsMatch(json);
    }

    public static bool IsKernelLayout(long ffmpegProxyBytes, long ffmpegRealBytes, long versionDllBytes) =>
        ffmpegProxyBytes is > 0 and < 500_000 &&
        ffmpegRealBytes > 500_000 &&
        versionDllBytes > 50_000;

    /// <summary>
    /// Valid gaming DiscOpt config: EnableTrim=1, PriorityClass=3, TrimIntervalMs in 2000–15000.
    /// Accepts kit (2500) and prior applies (4000/5000) — does not hardcode a single interval.
    /// </summary>
    public static bool IsKernelConfigText(string? configText)
    {
        if (string.IsNullOrWhiteSpace(configText)) return false;
        if (!EnableTrimRegex().IsMatch(configText)) return false;
        if (!PriorityClassRegex().IsMatch(configText)) return false;
        var m = TrimIntervalRegex().Match(configText);
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
        // Exo Host only (SKIP_HOST_UPDATE + chromium/TTI). Legacy OpenAsar
        // quickstart is intentionally not accepted anymore.
        return SkipHostUpdateTrueRegex().IsMatch(json) &&
               (ChromiumSwitchesKeyRegex().IsMatch(json) ||
                json.Contains("DESKTOP_TTI", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsStartupOffSettingsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        return StartupOffRegex().IsMatch(json);
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
        // StrictMode landmines: reading an unset Script var throws even inside a
        // $null probe (e2e-found crash v3.0.11) - probe with Get-Variable instead.
        "$null -eq $Script:HubStepPct",
        "$null -eq $Script:ExoApplyReport",
    };

    public static readonly string[] RequiredApplyMarkers =
    {
        "Install-DiscOptKernel",
        "Apply-WindowsTweaks",
        "IsPromoted",
        "OPEN_ON_STARTUP",
        "EXO",
        "Exo Discord Voice",
        "EXO_REPORT",
        // StrictMode-safe probes for the hub progress + apply-report Script vars.
        "Get-Variable -Name HubStepPct",
        "Get-Variable -Name ExoApplyReport",
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
