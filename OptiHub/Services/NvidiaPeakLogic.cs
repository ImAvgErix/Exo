using System.Text.RegularExpressions;

namespace OptiHub.Services;

/// <summary>
/// Pure NVIDIA detect classifiers (no I/O). Aligned with Scripts/Nvidia/NvidiaDetectCore.ps1.
/// </summary>
public static class NvidiaPeakLogic
{
    /// <summary>Map GPU name to series id used for NIP packs (10/20/30/40/50).</summary>
    public static string? GetGpuSeriesFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var m = Regex.Match(name, @"(?i)\b(?:RTX|GTX)\s*([1-5])0\d{2}\b");
        if (m.Success) return m.Groups[1].Value + "0";
        m = Regex.Match(name, @"(?i)\b([1-5])0\d{2}\b");
        if (m.Success) return m.Groups[1].Value + "0";
        // GTX 16xx → 10-series non-RT pack
        if (Regex.IsMatch(name, @"(?i)\b16\d{2}\b")) return "10";
        return null;
    }

    public static string ExpectedProfileFileName(string seriesId, bool gsync) =>
        gsync ? $"{seriesId} Series G-SYNC.nip" : $"{seriesId} Series.nip";

    public static bool IsNotebookGpuName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return Regex.IsMatch(name, @"(?i)\b(?:Laptop GPU|Notebook|Mobile|Max-Q)\b|\bMX\d+\b|\b\d{3,4}M\b");
    }

    /// <summary>Display container tray should be hidden (IsPromoted=0), not deleted.</summary>
    public static bool IsDisplayContainerTrayHidden(bool keyExists, int? isPromoted) =>
        !keyExists || isPromoted is 0;

    /// <summary>App/GFE tray ghosts should be gone (no key).</summary>
    public static bool IsAppTrayGhostGone(bool keyExists) => !keyExists;

    public static bool IsDisplayContainerExe(string? exe) =>
        !string.IsNullOrWhiteSpace(exe) &&
        Regex.IsMatch(exe, @"(?i)NVDisplay\.Container|Display\.NvContainer|nv_dispi\.inf");

    public static bool IsNvidiaAppTrayExe(string? exe) =>
        !string.IsNullOrWhiteSpace(exe) &&
        Regex.IsMatch(exe, @"(?i)NVIDIA App|GFExperience|NvBackend|NvNode|ShadowPlay|nvsphelper|nvapp") &&
        !IsDisplayContainerExe(exe);

    /// <summary>
    /// Live display status JSON peak: refresh + (registry active-keys OR color+scaling live).
    /// Mirrors OptiHub.NvDisplay ok gate after peak fix.
    /// </summary>
    public static bool IsDisplayStatusPeakOk(
        bool refreshOk,
        bool registryOk,
        bool colorOk,
        bool pathScalingOk) =>
        refreshOk && (registryOk || (colorOk && pathScalingOk));

    public static bool ProfileNameMatchesSeries(string? profileFile, string? series, bool gsync)
    {
        if (string.IsNullOrWhiteSpace(profileFile) || string.IsNullOrWhiteSpace(series))
            return false;
        return string.Equals(
            profileFile.Trim(),
            ExpectedProfileFileName(series, gsync),
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSha256Hex(string? hash) =>
        !string.IsNullOrWhiteSpace(hash) && Regex.IsMatch(hash, @"^[a-fA-F0-9]{64}$");

    public static readonly string[] RequiredApplyMarkers =
    {
        "Import-OptiHubNipProfile",
        "Apply-OptiHubGameProfileDeltas",
        "OptiHub-Display-Apply",
        "OptiHub-Nvidia-TrayClear",
        "IsPromoted",
        "silentImport",
    };

    public static readonly string[] ForbiddenApplyPatterns =
    {
        "Register-ScheduledTask -TaskName 'OptiHub-NvidiaTrayHide",
        "Register-ScheduledTask -TaskName 'OptiHub-NvidiaDisplayPersist",
        "schtasks /Create /TN \"OptiHub-Nvidia",
        "MaxUserPort",
        "FPS Unlocker",
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
        // Must not re-create logon tray tasks (unregister only is OK)
        if (Regex.IsMatch(script, @"Register-ScheduledTask[^\r\n]*OptiHub-Nvidia", RegexOptions.IgnoreCase))
            issues.Add("forbidden: Register-ScheduledTask OptiHub-Nvidia*");
        return (issues.Count == 0, issues);
    }
}
