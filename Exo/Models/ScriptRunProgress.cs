namespace Exo.Models;

public sealed class ScriptRunProgress
{
    public double Percent { get; init; }
    public string Status { get; init; } = string.Empty;
}

public sealed class ScriptRunResult
{
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string FullOutput { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
    public string? LogPath { get; init; }
}

public sealed class OptimizerFeatureInfo
{
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string Glyph { get; set; } = "\uE73E";
}

public sealed class OptimizerStateInfo
{
    public bool IsApplied { get; init; }
    public string StatusText { get; init; } = "Not applied";
    public string Detail { get; init; } = string.Empty;
    public IReadOnlyList<OptimizerFeatureInfo> Features { get; init; } = Array.Empty<OptimizerFeatureInfo>();
    /// <summary>Optional extra fields from detect scripts (series, gsync, etc.).</summary>
    public IReadOnlyDictionary<string, string>? Extra { get; init; }
}

public sealed class AppUpdateResult
{
    public bool UpdateAvailable { get; init; }
    public bool AlreadyLatest { get; init; }
    public string LocalVersion { get; init; } = string.Empty;
    public string RemoteVersion { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    /// <summary>Plain-language TLDR for the update popup (short bullets).</summary>
    public string? ReleaseSummary { get; init; }
    public string? DownloadUrl { get; init; }
    public long? DownloadSize { get; init; }
    public string? Sha256 { get; init; }
    public bool ShouldExit { get; init; }
}

/// <summary>In-app update download/install progress (status text + optional percent).</summary>
public sealed class AppUpdateProgress
{
    /// <summary>0–100 when known; negative = indeterminate phase.</summary>
    public double Percent { get; init; } = -1;
    public string Status { get; init; } = string.Empty;
}
