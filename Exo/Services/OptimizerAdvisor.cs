using System.Text;

namespace Exo.Services;

/// <summary>
/// Real-time next-step guidance from live detect state. Pure (no I/O) so UI and smokes share it.
/// Goal: tell the user exactly what to click so Apply works on any PC layout.
/// </summary>
public static class OptimizerAdvisor
{
    public static string Build(
        string module,
        bool isApplied,
        string? statusText,
        string? detailText,
        IReadOnlyList<(string Name, bool Applied, string? Status)> features)
    {
        var missing = features
            .Where(f => !f.Applied)
            .Select(f => f.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Take(6)
            .ToList();

        var sb = new StringBuilder();
        var st = (statusText ?? "").Trim();
        var det = (detailText ?? "").Trim();

        if (isApplied && missing.Count == 0)
        {
            sb.Append("All detected items look applied. ");
            sb.Append(module switch
            {
                "Internet" => "Re-Apply only if latency regressed or you changed adapters.",
                "Discord" => "If Discord updated itself, hit Apply once to re-seal mods.",
                "Steam" => "If Steam updated, hit Apply to re-verify launch path and quiet settings.",
                "NVIDIA" => "If the driver updated, Apply again to re-import profiles.",
                _ => "Refresh if something still feels off."
            });
            return sb.ToString();
        }

        if (missing.Count > 0)
        {
            sb.Append("Next: Apply to finish — still open: ");
            sb.Append(string.Join(", ", missing));
            sb.Append('.');
        }
        else if (!string.IsNullOrEmpty(st) &&
                 !st.Equals("Checking status...", StringComparison.OrdinalIgnoreCase) &&
                 !st.Equals("All applied", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("Next: Apply. Status: ").Append(st).Append('.');
        }
        else
        {
            sb.Append("Next: hit Apply. Exo will detect this PC and apply the aggressive pack.");
        }

        if (!string.IsNullOrEmpty(det) && det.Length < 180)
        {
            sb.Append(' ').Append(det);
        }

        // Hardware / install hints
        var blob = $"{st} {det} {string.Join(' ', features.Select(f => f.Status))}".ToLowerInvariant();
        if (module == "NVIDIA" && (blob.Contains("no nvidia") || blob.Contains("needs an nvidia")))
            sb.Append(" Needs an NVIDIA GPU + Game Ready / Studio driver.");
        if (module == "Discord" && (blob.Contains("not installed") || blob.Contains("no discord")))
            sb.Append(" Install Discord Stable first, then Apply.");
        if (module == "Steam" && (blob.Contains("not installed") || blob.Contains("no steam")))
            sb.Append(" Install Steam first, then Apply.");
        if (module == "Internet" && blob.Contains("no physical"))
            sb.Append(" Connect Ethernet or Wi-Fi, then Refresh.");

        sb.Append(" No Exo background tasks are installed — only runs when you click.");
        return sb.ToString();
    }
}
