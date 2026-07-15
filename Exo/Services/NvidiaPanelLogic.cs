using System.Globalization;
using System.Text.RegularExpressions;

namespace Exo.Services;

/// <summary>
/// Pure NVIDIA Panel list/apply helpers (no I/O). CLI builders + mode parsing for smokes.
/// Keep aligned with tools/Exo.NvDisplay argument contract.
/// </summary>
public static class NvidiaPanelLogic
{
    public static readonly string[] ScalingOptions =
    {
        "GPU no-scaling",
        "GPU scaling",
        "Display scaling"
    };

    public static readonly string[] ColorRangeOptions =
    {
        "Full RGB",
        "Limited"
    };

    /// <summary>Parse "1920x1080@144" or "1920x1080 144Hz" style mode labels.</summary>
    public static bool TryParseModeLabel(string? label, out int width, out int height, out int hz)
    {
        width = height = hz = 0;
        if (string.IsNullOrWhiteSpace(label)) return false;
        var s = label.Trim();
        var m = Regex.Match(s, @"^\s*(\d+)\s*[xX×]\s*(\d+)\s*[@\s]+\s*(\d+)\s*(?:Hz)?\s*$",
            RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            m = Regex.Match(s, @"^\s*(\d+)\s*[xX×]\s*(\d+)\s*$");
            if (!m.Success) return false;
            width = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            height = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            hz = 60;
            return width >= 640 && height >= 480;
        }
        width = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        height = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        hz = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return width >= 640 && height >= 480 && hz is >= 30 and <= 1000;
    }

    public static string FormatModeLabel(int width, int height, int hz) =>
        $"{width}x{height}@{hz}";

    public static string? ToDepthCliArg(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var s = label.Trim().ToUpperInvariant().Replace(" ", "").Replace("-", "");
        if (s is "8" or "BPC8" or "8BPC" or "8BIT") return "8";
        if (s is "10" or "BPC10" or "10BPC" or "10BIT") return "10";
        if (s is "12" or "BPC12" or "12BPC" or "12BIT") return "12";
        if (s is "6" or "BPC6") return "6";
        if (label.Contains("12", StringComparison.Ordinal)) return "12";
        if (label.Contains("10", StringComparison.Ordinal)) return "10";
        if (label.Contains('8')) return "8";
        return null;
    }

    public static string NormalizeDepthLabel(string? raw)
    {
        var arg = ToDepthCliArg(raw);
        return arg switch
        {
            "8" => "8-bit",
            "10" => "10-bit",
            "12" => "12-bit",
            "6" => "6-bit",
            _ => string.IsNullOrWhiteSpace(raw) ? "—" : raw!
        };
    }

    public static string? ToScalingCliArg(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var s = label.Trim().ToLowerInvariant();
        if (s is "gpu-noscaling" or "gpu no-scaling" or "no-scaling" or "no scaling" or "noscaling")
            return "gpu-noscaling";
        if (s is "gpu" or "gpu scaling" or "gpu-scaling")
            return "gpu";
        if (s is "display" or "display scaling" or "display-scaling" or "monitor")
            return "display";
        return null;
    }

    public static string NormalizeScalingLabel(string? raw)
    {
        var arg = ToScalingCliArg(raw);
        return arg switch
        {
            "gpu-noscaling" => "GPU no-scaling",
            "gpu" => "GPU scaling",
            "display" => "Display scaling",
            _ => string.IsNullOrWhiteSpace(raw) ? "—" : raw!
        };
    }

    public static string? ToColorRangeCliArg(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var s = label.Trim().ToLowerInvariant();
        if (s is "full" or "full rgb" or "full-rgb" or "vesa" or "rgb full")
            return "full";
        if (s is "limited" or "cea" or "limited rgb" or "limited-rgb")
            return "limited";
        return null;
    }

    public static string NormalizeColorRangeLabel(string? raw)
    {
        var arg = ToColorRangeCliArg(raw);
        return arg switch
        {
            "full" => "Full RGB",
            "limited" => "Limited",
            _ => string.IsNullOrWhiteSpace(raw) ? "—" : raw!
        };
    }

    public static string BuildListDisplaysArgs() => "--list-displays";

    public static string BuildSetModeArgs(int width, int height, int hz, uint? displayId)
    {
        var mode = FormatModeLabel(width, height, hz);
        return displayId is null or 0
            ? $"--set-mode {mode}"
            : $"--set-mode {mode} --display-id {displayId.Value}";
    }

    public static string BuildSetDepthArgs(string depthLabel, uint? displayId)
    {
        var d = ToDepthCliArg(depthLabel) ?? "8";
        return displayId is null or 0
            ? $"--set-depth {d}"
            : $"--set-depth {d} --display-id {displayId.Value}";
    }

    public static string BuildSetScalingArgs(string scalingLabel, uint? displayId)
    {
        var s = ToScalingCliArg(scalingLabel) ?? "gpu-noscaling";
        return displayId is null or 0
            ? $"--set-scaling {s}"
            : $"--set-scaling {s} --display-id {displayId.Value}";
    }

    public static string BuildSetColorRangeArgs(string rangeLabel, uint? displayId)
    {
        var r = ToColorRangeCliArg(rangeLabel) ?? "full";
        return displayId is null or 0
            ? $"--set-color-range {r}"
            : $"--set-color-range {r} --display-id {displayId.Value}";
    }

    /// <summary>Digital vibrance (DVC) raw driver range fallback; live min/max come from --get-vibrance.</summary>
    public const int VibranceDefaultMinimum = 0;
    public const int VibranceDefaultMaximum = 63;

    public static int ClampVibranceLevel(int level, int minimum = VibranceDefaultMinimum, int maximum = VibranceDefaultMaximum)
    {
        if (maximum < minimum) (minimum, maximum) = (maximum, minimum);
        return Math.Clamp(level, minimum, maximum);
    }

    public static string BuildGetVibranceArgs() => "--get-vibrance";

    public static string BuildSetVibranceArgs(int level, uint? displayId)
    {
        var v = ClampVibranceLevel(level);
        return displayId is null or 0
            ? $"--set-vibrance {v}"
            : $"--set-vibrance {v} --display-id {displayId.Value}";
    }

    /// <summary>
    /// Distinct resolution labels from mode list (WxH only), sorted largest first.
    /// </summary>
    public static IReadOnlyList<string> DistinctResolutions(IEnumerable<string> modeLabels)
    {
        var set = new SortedSet<(int w, int h)>(Comparer<(int w, int h)>.Create((a, b) =>
        {
            var c = b.w.CompareTo(a.w);
            return c != 0 ? c : b.h.CompareTo(a.h);
        }));
        foreach (var label in modeLabels)
        {
            if (TryParseModeLabel(label, out var w, out var h, out _))
                set.Add((w, h));
        }
        return set.Select(t => $"{t.w}x{t.h}").ToList();
    }

    /// <summary>Refresh rates available for a resolution among mode labels, highest first.</summary>
    public static IReadOnlyList<string> RefreshRatesForResolution(
        IEnumerable<string> modeLabels, string resolutionLabel)
    {
        var m = Regex.Match(resolutionLabel.Trim(), @"^(\d+)\s*[xX]\s*(\d+)");
        if (!m.Success) return Array.Empty<string>();
        var tw = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var th = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);

        var rates = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        foreach (var label in modeLabels)
        {
            if (!TryParseModeLabel(label, out var w, out var h, out var hz)) continue;
            if (w == tw && h == th) rates.Add(hz);
        }
        return rates.Select(r => $"{r} Hz").ToList();
    }

    public static int ParseHzLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return 0;
        var m = Regex.Match(label, @"(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var hz) ? hz : 0;
    }
}
