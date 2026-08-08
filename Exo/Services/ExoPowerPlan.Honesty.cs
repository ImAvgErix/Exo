namespace Exo.Services;

/// <summary>
/// Pure honesty helpers for the Exo power plan. Kept free of registry/native deps so
/// Contracts.Smoke can compile and drive the same code the host ships.
/// </summary>
internal static partial class ExoPowerPlan
{
    /// <summary>
    /// Fixed so a second Apply reuses the same plan instead of stacking duplicates, and so
    /// Detect can find it without matching on a display name the user may have renamed.
    /// </summary>
    internal const string ExoSchemeGuid = "7ae4b8a5-2c19-4d6f-9f3e-1b0c5d8e4a72";

    /// <summary>
    /// Older Exo builds used fixed GUIDs that still sit on machines after a Hub upgrade.
    /// Detect must never treat those as "current Applied" — they are a migration signal —
    /// but live-verify scripts and honesty messaging should recognize the family.
    /// </summary>
    internal static readonly string[] LegacyExoSchemeGuids =
    {
        "a1111111-e80e-4e0e-a111-0e0e0e0e0e01", // Exo Extreme (AM4 / early kits)
        "77777777-7777-7777-7777-777777777777", // Exo LiteOS Power Plan
    };

    /// <summary>
    /// True when Windows is running any historical Exo-branded plan GUID that is not the
    /// current scheme. Pure so Contracts.Smoke can assert the migration signal without a UI.
    /// </summary>
    public static bool IsLegacyExoSchemeGuid(string? schemeGuid)
    {
        if (string.IsNullOrWhiteSpace(schemeGuid)) return false;
        foreach (var g in LegacyExoSchemeGuids)
        {
            if (string.Equals(schemeGuid, g, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Live-verify / honesty helper: active scheme is the current Exo plan, a known legacy
    /// Exo GUID, or a display name that still starts with "Exo" (user may have renamed).
    /// Does <b>not</b> mean "Applied" for Detect — only that the machine is still in the family.
    /// </summary>
    public static bool IsExoFamilyScheme(string? schemeGuid, string? schemeName)
    {
        if (string.Equals(schemeGuid, ExoSchemeGuid, StringComparison.OrdinalIgnoreCase))
            return true;
        if (IsLegacyExoSchemeGuid(schemeGuid))
            return true;
        if (!string.IsNullOrWhiteSpace(schemeName) &&
            schemeName.TrimStart().StartsWith("Exo", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}
