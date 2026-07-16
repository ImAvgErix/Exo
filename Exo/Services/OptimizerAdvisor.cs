using System.Text;

namespace Exo.Services;

/// <summary>
/// Real-time next-step guidance from live detect state. Pure (no I/O) so UI and smokes share it.
/// Wave 2 (v2): structured CTA = missing essentials + optional last-fail hints + one action.
/// </summary>
public static class OptimizerAdvisor
{
    public static string Build(
        string module,
        bool isApplied,
        string? statusText,
        string? detailText,
        IReadOnlyList<(string Name, bool Applied, string? Status)> features)
        => BuildV2(module, isApplied, statusText, detailText, features, reportFailSteps: null);

    /// <summary>
    /// Advisor v2: same as Build, plus optional last-apply fail step ids (from EXO_REPORT).
    /// </summary>
    public static string BuildV2(
        string module,
        bool isApplied,
        string? statusText,
        string? detailText,
        IReadOnlyList<(string Name, bool Applied, string? Status)> features,
        IReadOnlyList<string>? reportFailSteps)
    {
        var missing = features
            .Where(f => !f.Applied)
            .Select(f => f.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Take(6)
            .ToList();

        var fails = (reportFailSteps ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();

        var sb = new StringBuilder();
        var st = (statusText ?? "").Trim();
        var det = (detailText ?? "").Trim();

        if (isApplied && missing.Count == 0 && fails.Count == 0)
        {
            sb.Append("All good on this PC. ");
            sb.Append(module switch
            {
                "Internet" => "CTA: only Re-Apply if latency got worse or you changed adapters.",
                "Discord" => "CTA: if Discord self-updated, hit Apply once to re-seal mods.",
                "Steam" => "CTA: if Steam updated, Apply again to re-verify launch path.",
                "NVIDIA" => "CTA: after a driver update, Apply again to re-import profiles.",
                _ => "CTA: Refresh if something still feels off."
            });
            sb.Append(" Exo installs no background tasks.");
            return sb.ToString();
        }

        // Primary CTA
        if (missing.Count > 0)
        {
            sb.Append("CTA: hit Apply. Still open: ");
            sb.Append(string.Join(", ", missing));
            sb.Append('.');
        }
        else if (fails.Count > 0)
        {
            sb.Append("CTA: hit Apply (or Repair if Apply already failed). Last fail: ");
            sb.Append(string.Join(", ", fails));
            sb.Append('.');
        }
        else if (!string.IsNullOrEmpty(st) &&
                 !st.Equals("Checking status...", StringComparison.OrdinalIgnoreCase) &&
                 !st.Equals("Already optimized", StringComparison.OrdinalIgnoreCase) &&
                 !st.Equals("All applied", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("CTA: Apply. Status: ").Append(st).Append('.');
        }
        else
        {
            sb.Append("CTA: Apply. Exo will detect this machine and run the aggressive pack.");
        }

        if (!string.IsNullOrEmpty(det) && det.Length < 160)
            sb.Append(' ').Append(det);

        var blob = $"{st} {det} {string.Join(' ', features.Select(f => f.Status))}".ToLowerInvariant();
        if (module == "NVIDIA" && (blob.Contains("no nvidia") || blob.Contains("needs an nvidia")))
            sb.Append(" Needs an NVIDIA GPU + current driver.");
        if (module == "Discord" && (blob.Contains("not installed") || blob.Contains("no discord")))
            sb.Append(" Install Discord Stable first.");
        if (module == "Steam" && (blob.Contains("not installed") || blob.Contains("no steam")))
            sb.Append(" Install Steam first.");
        if (module == "Internet" && blob.Contains("no physical"))
            sb.Append(" Connect Ethernet or Wi-Fi, then Refresh.");
        if (module == "Steam" && blob.Contains("open steam once"))
            sb.Append(" Open Steam once, then Reapply for VDF keys.");

        sb.Append(" No Exo background tasks.");
        return sb.ToString();
    }
}
