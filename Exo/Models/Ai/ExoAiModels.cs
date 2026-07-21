namespace Exo.Models.Ai;

/// <summary>Full machine inventory captured for Grok analysis and optimal-state memory.</summary>
public sealed class ExoSystemState
{
    public string CapturedUtc { get; set; } = DateTime.UtcNow.ToString("o");
    public string ExoVersion { get; set; } = string.Empty;
    public string Digest { get; set; } = string.Empty;

    public Dictionary<string, string> Hardware { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Os { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, object?> Domains { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> InstalledApps { get; set; } = [];
    public List<string> RunningProcesses { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

/// <summary>Persisted optimal state after a successful deep maximize run.</summary>
public sealed class ExoOptimalState
{
    public string SavedUtc { get; set; } = DateTime.UtcNow.ToString("o");
    public string ExoVersion { get; set; } = string.Empty;
    public string StateDigest { get; set; } = string.Empty;
    public string AnalysisSummary { get; set; } = string.Empty;
    public List<string> AppliedActionIds { get; set; } = [];
    public Dictionary<string, string> Metrics { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ExoSystemState? Snapshot { get; set; }
}

/// <summary>Drift between current and optimal state.</summary>
public sealed class ExoStateDrift
{
    public string Domain { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string? Expected { get; set; }
    public string? Actual { get; set; }
    public string Severity { get; set; } = "info";
}

/// <summary>Fast compare result used for on-open gating.</summary>
public sealed class ExoOptimalGateStatus
{
    public bool HasOptimal { get; set; }
    public bool IsOptimal { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<ExoStateDrift> Drifts { get; set; } = [];
}

/// <summary>Grok / local analysis result contract.</summary>
public sealed class ExoAnalysisResult
{
    public string Analysis { get; set; } = string.Empty;
    public List<ExoPlanStep> Plan { get; set; } = [];
    public List<ExoToolAction> Actions { get; set; } = [];
    public string Source { get; set; } = "local";
}

public sealed class ExoPlanStep
{
    public int Priority { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Reasoning { get; set; } = string.Empty;
    public string CleanSlate { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
}

public sealed class ExoToolAction
{
    public string ToolId { get; set; } = string.Empty;
    public Dictionary<string, string> Params { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Reason { get; set; } = string.Empty;
}

public sealed class ExoToolResult
{
    public string ToolId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Status { get; set; } = "ok";
    public string Message { get; set; } = string.Empty;
    public string? Before { get; set; }
    public string? After { get; set; }
}

public sealed class ExoAgentRunResult
{
    public bool Success { get; set; }
    public bool SkippedOptimal { get; set; }
    public string Message { get; set; } = string.Empty;
    public ExoAnalysisResult? Analysis { get; set; }
    public List<ExoToolResult> Results { get; set; } = [];
}

public enum ExoToolCategory
{
    Networking,
    PowerScheduler,
    Services,
    Registry,
    FileSystem,
    Browser,
    Display,
    Input,
    Audio,
    Storage,
    Apps,
    OsCore,
    Gpu,
    Upscaler,
    Companion,
    HostOs,
    Automation,
    Firmware
}
