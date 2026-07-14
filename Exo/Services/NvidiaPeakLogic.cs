using System.Text.RegularExpressions;

namespace Exo.Services;

/// <summary>
/// Pure NVIDIA detect classifiers (no I/O). Aligned with Scripts/Nvidia/NvidiaDetectCore.ps1.
/// </summary>
public static partial class NvidiaPeakLogic
{
    [GeneratedRegex(@"(?i)\b(?:RTX|GTX)\s*([1-5])0\d{2}\b")]
    private static partial Regex GpuSeriesPrefixedRegex();

    [GeneratedRegex(@"(?i)\b([1-5])0\d{2}\b")]
    private static partial Regex GpuSeriesBareRegex();

    [GeneratedRegex(@"(?i)\b16\d{2}\b")]
    private static partial Regex Gtx16SeriesRegex();

    [GeneratedRegex(@"(?i)\b(?:Laptop GPU|Notebook|Mobile|Max-Q)\b|\bMX\d+\b|\b\d{3,4}M\b")]
    private static partial Regex NotebookGpuRegex();

    [GeneratedRegex(@"(?i)NVDisplay\.Container|Display\.NvContainer|nv_dispi\.inf")]
    private static partial Regex DisplayContainerExeRegex();

    [GeneratedRegex(@"(?i)NVIDIA App|GFExperience|NvBackend|NvNode|ShadowPlay|nvsphelper|nvapp")]
    private static partial Regex AppTrayExeRegex();

    [GeneratedRegex(@"^[a-fA-F0-9]{64}$")]
    private static partial Regex Sha256HexRegex();

    [GeneratedRegex(@"Register-ScheduledTask[^\r\n]*Exo-Nvidia", RegexOptions.IgnoreCase)]
    private static partial Regex TrayTaskCreateRegex();

    /// <summary>Map GPU name to series id used for NIP packs (10/20/30/40/50).</summary>
    public static string? GetGpuSeriesFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var m = GpuSeriesPrefixedRegex().Match(name);
        if (m.Success) return m.Groups[1].Value + "0";
        m = GpuSeriesBareRegex().Match(name);
        if (m.Success) return m.Groups[1].Value + "0";
        // GTX 16xx → 10-series non-RT pack
        if (Gtx16SeriesRegex().IsMatch(name)) return "10";
        return null;
    }

    public static string ExpectedProfileFileName(string seriesId, bool gsync) =>
        gsync ? $"{seriesId} Series G-SYNC.nip" : $"{seriesId} Series.nip";

    public static bool IsNotebookGpuName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return NotebookGpuRegex().IsMatch(name);
    }

    /// <summary>Display container tray should be hidden (IsPromoted=0), not deleted.</summary>
    public static bool IsDisplayContainerTrayHidden(bool keyExists, int? isPromoted) =>
        !keyExists || isPromoted is 0;

    /// <summary>App/GFE tray ghosts should be gone (no key).</summary>
    public static bool IsAppTrayGhostGone(bool keyExists) => !keyExists;

    public static bool IsDisplayContainerExe(string? exe) =>
        !string.IsNullOrWhiteSpace(exe) &&
        DisplayContainerExeRegex().IsMatch(exe);

    public static bool IsNvidiaAppTrayExe(string? exe) =>
        !string.IsNullOrWhiteSpace(exe) &&
        AppTrayExeRegex().IsMatch(exe) &&
        !IsDisplayContainerExe(exe);

    /// <summary>
    /// Live display status JSON peak: refresh + (registry active-keys OR color+scaling live).
    /// Mirrors Exo.NvDisplay ok gate after peak fix.
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
        !string.IsNullOrWhiteSpace(hash) && Sha256HexRegex().IsMatch(hash);

    public static readonly string[] RequiredApplyMarkers =
    {
        "Import-ExoNipProfile",
        "Apply-ExoGameProfileDeltas",
        "Exo-Display-Apply",
        "Exo-Nvidia-TrayClear",
        "IsPromoted",
        "silentImport",
    };

    public static readonly string[] ForbiddenApplyPatterns =
    {
        "Register-ScheduledTask -TaskName 'Exo-NvidiaTrayHide",
        "Register-ScheduledTask -TaskName 'Exo-NvidiaDisplayPersist",
        "schtasks /Create /TN \"Exo-Nvidia",
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
        if (TrayTaskCreateRegex().IsMatch(script))
            issues.Add("forbidden: Register-ScheduledTask Exo-Nvidia*");
        return (issues.Count == 0, issues);
    }
}
