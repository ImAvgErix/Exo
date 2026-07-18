using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Exo.ViewModels;

/// <summary>One structured step from the last apply run (name + ok/fail/skip + reason).</summary>
public sealed class ApplyReportRowViewModel
{
    public string Text { get; init; } = string.Empty;
    /// <summary>Normalized step status: ok | fail | skip.</summary>
    public string Status { get; init; } = "ok";
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
            Text = text,
            Status = normalized,
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

    /// <summary>Compact header line, e.g. "Last apply - 9 ok - 1 fail - 2 skip".</summary>
    public static string Summarize(IReadOnlyList<ApplyReportRowViewModel> rows)
    {
        if (rows is null || rows.Count == 0) return "Last apply";
        var ok = rows.Count(r => r.Status == "ok");
        var fail = rows.Count(r => r.Status == "fail");
        var skip = rows.Count(r => r.Status == "skip");
        var text = "Last apply";
        if (ok > 0) text += $" - {ok} ok";
        if (fail > 0) text += $" - {fail} fail";
        if (skip > 0) text += $" - {skip} skip";
        return text;
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
