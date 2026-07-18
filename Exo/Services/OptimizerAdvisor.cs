namespace Exo.Services;

/// <summary>
/// Concise next action from live detect state. Feature rows carry technical
/// detail; the advisor does not repeat internal step names or script prose.
/// </summary>
public static class OptimizerAdvisor
{
    public static string Build(
        string module,
        bool isApplied,
        string? statusText,
        string? detailText,
        IReadOnlyList<(string Name, bool Applied, string Status)> features)
        => BuildV2(module, isApplied, statusText, detailText, features, reportFailSteps: null);

    public static string BuildV2(
        string module,
        bool isApplied,
        string? statusText,
        string? detailText,
        IReadOnlyList<(string Name, bool Applied, string Status)> features,
        IReadOnlyList<string>? reportFailSteps)
    {
        var missingCount = features.Count(f => !f.Applied && !string.IsNullOrWhiteSpace(f.Name));
        var hasFailure = (reportFailSteps ?? Array.Empty<string>())
            .Any(step => !string.IsNullOrWhiteSpace(step));
        var status = (statusText ?? string.Empty).Trim();
        var detail = (detailText ?? string.Empty).Trim();
        var state = $"{status} {detail} {string.Join(' ', features.Select(f => f.Status))}".ToLowerInvariant();

        if (module == "NVIDIA" && (state.Contains("no nvidia") || state.Contains("needs an nvidia")))
            return "No supported NVIDIA GPU was detected on this PC.";
        if (module == "Discord" && (state.Contains("not installed") || state.Contains("no discord")))
            return "Discord Stable is not installed. Install it, then refresh this page.";
        if (module == "Steam" && (state.Contains("not installed") || state.Contains("no steam")))
            return "Steam is not installed. Install and open it once, then refresh this page.";
        if (module == "Internet" && state.Contains("no physical"))
            return "No active physical connection was detected. Connect Ethernet or Wi-Fi, then refresh.";
        if (module == "Steam" && state.Contains("open steam once"))
            return "Open Steam once so its account configuration exists, then apply again.";

        if (isApplied && missingCount == 0 && !hasFailure)
        {
            return module switch
            {
                "Internet" => "Verified on this connection. Analyze again after changing adapters, routers, or service plans.",
                "Discord" => "Verified on this installation. Apply again after Discord replaces its client files.",
                "Steam" => "Verified on this installation. Apply again after Steam changes its launcher configuration.",
                "NVIDIA" => "Verified against the live driver profile. Apply again after a driver update.",
                _ => "Verified on this PC."
            };
        }

        if (hasFailure)
            return "The last Apply needs attention. Retry once; use Repair if verification still fails.";

        if (missingCount > 0)
        {
            return module switch
            {
                "Internet" => "Ready to measure this connection and apply one balanced policy.",
                "Discord" => $"{missingCount} settings are ready for this Discord installation.",
                "Steam" => $"{missingCount} settings are ready for this Steam installation.",
                "NVIDIA" => $"{missingCount} settings are ready for the detected GPU and display path.",
                _ => $"{missingCount} settings are ready for this PC."
            };
        }

        return module switch
        {
            "Internet" => "Ready to analyze the active connection.",
            "NVIDIA" => "Ready to verify the detected GPU and driver profile.",
            _ => "Ready to analyze this installation."
        };
    }
}
