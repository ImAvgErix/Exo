namespace OptiHub.Models;

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
}

public sealed class OptimizerStateInfo
{
    public bool IsApplied { get; init; }
    public string StatusText { get; init; } = "Not applied";
    public string Detail { get; init; } = string.Empty;
    public IReadOnlyList<string> Checks { get; init; } = Array.Empty<string>();
}

public sealed class ScriptUpdateResult
{
    public bool Updated { get; init; }
    public bool AlreadyLatest { get; init; }
    public string LocalVersion { get; init; } = string.Empty;
    public string RemoteVersion { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
