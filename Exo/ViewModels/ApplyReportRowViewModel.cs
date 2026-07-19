using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Exo.ViewModels;

/// <summary>One structured step from the last apply run (name + ok/fail/skip + reason).</summary>
public sealed class ApplyReportRowViewModel
{
    public string Name { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    /// <summary>Normalized step status: ok | fail | skip.</summary>
    public string Status { get; init; } = "ok";
    public string Reason { get; init; } = string.Empty;
    public string Glyph { get; init; } = "\uE73E";
    public Brush Brush { get; init; } = new SolidColorBrush(Color.FromArgb(255, 34, 197, 94));
}

/// <summary>
/// Maps persisted apply-report data to display rows for the compact
/// "Last apply" section on the module pages. Never throws.
/// </summary>
public static class ApplyReportPresentation
{
    /// <summary>Parse state-file entries: "step|ok", "step|fail:reason", "step|skip:reason".</summary>
    public static List<ApplyReportRowViewModel> FromEntries(IReadOnlyList<string> entries)
    {
        var rows = new List<ApplyReportRowViewModel>();
        if (entries is null) return rows;
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var bar = entry.IndexOf('|');
            var name = bar > 0 ? entry[..bar].Trim() : entry.Trim();
            var rest = bar >= 0 && bar + 1 < entry.Length ? entry[(bar + 1)..].Trim() : "ok";
            var colon = rest.IndexOf(':');
            var status = colon > 0 ? rest[..colon].Trim() : rest;
            var reason = colon > 0 && colon + 1 < rest.Length ? rest[(colon + 1)..].Trim() : string.Empty;
            rows.Add(Row(name, status, reason));
        }
        return rows;
    }

    /// <summary>Build one display row from a structured step.</summary>
    public static ApplyReportRowViewModel Row(string name, string status, string reason)
    {
        var normalized = (status ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is not ("ok" or "fail" or "skip"))
            normalized = "skip";

        var text = string.IsNullOrWhiteSpace(reason)
            ? $"{name} - {normalized}"
            : $"{name} - {normalized} - {reason}";

        return new ApplyReportRowViewModel
        {
            Name = name ?? string.Empty,
            Text = text,
            Status = normalized,
            Reason = reason ?? string.Empty,
            Glyph = normalized switch
            {
                "ok" => "\uE73E",   // CheckMark
                "fail" => "\uE711", // Cancel
                _ => "\uE738"       // Remove (skipped)
            },
            Brush = normalized switch
            {
                "ok" => ResolveBrush("ExoSuccessBrush", Color.FromArgb(255, 34, 197, 94)),
                "fail" => ResolveBrush("ExoErrorBrush", Color.FromArgb(255, 220, 38, 38)),
                _ => ResolveBrush("ExoMutedTextBrush", Color.FromArgb(255, 161, 161, 170))
            }
        };
    }

    /// <summary>
    /// Honest header: do not count bookkeeping / already-set steps as "ok work".
    /// Example: "Last apply · 4 done · 7 already set"
    /// </summary>
    public static string Summarize(IReadOnlyList<ApplyReportRowViewModel> rows)
    {
        if (rows is null || rows.Count == 0) return "Last apply";

        var done = 0;
        var already = 0;
        var fail = 0;
        var skip = 0;
        foreach (var row in rows)
        {
            if (row.Status == "fail")
            {
                fail++;
                continue;
            }
            if (row.Status == "skip")
            {
                skip++;
                continue;
            }
            if (IsAlreadyOrMeta(row))
                already++;
            else
                done++;
        }

        if (fail > 0 && done > 0) return $"Last apply · {fail} failed · {done} done";
        if (fail > 0) return $"Last apply · {fail} failed";
        if (done > 0 && already > 0) return $"Last apply · {done} done · {already} already set";
        if (done > 0) return $"Last apply · {done} done";
        if (already > 0) return "Last apply · already applied";
        if (skip > 0) return $"Last apply · {skip} skipped";
        return "Last apply";
    }

    /// <summary>
    /// Pipeline bookkeeping and no-op "ok" lines that used to inflate the count.
    /// </summary>
    public static bool IsAlreadyOrMeta(ApplyReportRowViewModel row)
    {
        if (row is null || row.Status != "ok") return false;

        var name = (row.Name ?? string.Empty).Trim().ToLowerInvariant();
        var reason = (row.Reason ?? string.Empty).Trim().ToLowerInvariant();
        var text = (row.Text ?? string.Empty).Trim().ToLowerInvariant();

        // Explicit no-op language in reason.
        if (reason.Contains("already", StringComparison.Ordinal)
            || reason.Contains("removed 0", StringComparison.Ordinal)
            || reason.Contains("no change", StringComparison.Ordinal)
            || reason.Contains("unchanged", StringComparison.Ordinal)
            || reason.Contains("never opened", StringComparison.Ordinal)
            || reason.Contains("detect only", StringComparison.Ordinal)
            || reason.Contains("not installed", StringComparison.Ordinal)
            || reason.Contains("client present", StringComparison.Ordinal)
            || reason.Contains("present", StringComparison.Ordinal) && name is "install")
            return true;

        // Bookkeeping step names (presence checks, snapshots, verify wrappers).
        return name is "install"
            or "game-discovery"
            or "snapshot"
            or "verified-record"
            or "verify"
            or "boot-check"
            or "anti-cheat-boundary"
            or "variants"
            or "discord-install"
            or "recovery"
            or "apply"; // wrap-up line from network EXO_REPORT
    }

    private static Brush ResolveBrush(string key, Color fallback)
    {
        try
        {
            if (Microsoft.UI.Xaml.Application.Current?.Resources.TryGetValue(key, out var value) == true
                && value is Brush brush)
                return brush;
        }
        catch { }
        return new SolidColorBrush(fallback);
    }
}
