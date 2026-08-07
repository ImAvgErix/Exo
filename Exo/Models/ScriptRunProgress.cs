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

    /// <summary>
    /// True when the runner refused before a single line of the script executed — the file was
    /// missing, its SHA-256 did not match the shipped manifest, or PowerShell 7 could not be
    /// provided.
    ///
    /// This is categorically different from "it ran and partly failed", which is the only case
    /// Steam's deep-pack soft-fail was designed to absorb. Without the distinction, a
    /// script-integrity failure — "SHA-256 mismatch. Reinstall Exo before applying." — was
    /// swallowed by that soft-fail and reported to the user as a completed Steam optimize.
    /// A typed flag rather than a Summary string match: the wording is not a contract, and
    /// re-opening this hole by rephrasing a message would be silent.
    /// </summary>
    public bool RefusedBeforeExecution { get; init; }
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
