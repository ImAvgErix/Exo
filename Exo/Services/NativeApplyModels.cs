namespace Exo.Services;

/// <summary>One optimization step outcome for UI Applied/Failed/partial.</summary>
public sealed class NativeApplyStep
{
    public string Id { get; init; } = "";
    public string Status { get; init; } = "ok"; // ok | fail | skip | partial
    public string? Reason { get; init; }

    public string ToReportLine() =>
        string.IsNullOrWhiteSpace(Reason) ? $"{Id}|{Status}" : $"{Id}|{Status}:{Reason}";
}

/// <summary>Result of a pure-C# (or hybrid elevated-reg) module apply.</summary>
public sealed class NativeApplyResult
{
    public bool Ok { get; init; }
    public string Module { get; init; } = "";
    public string Message { get; init; } = "";
    public List<NativeApplyStep> Steps { get; init; } = new();
    public bool NeedsElevation { get; init; }
    public List<string> ElevatedHklmOps { get; init; } = new();

    public IEnumerable<string> ReportLines => Steps.Select(s => s.ToReportLine());

    public static NativeApplyResult Fail(string module, string message, IEnumerable<NativeApplyStep>? steps = null) =>
        new()
        {
            Ok = false,
            Module = module,
            Message = message,
            Steps = steps?.ToList() ?? new List<NativeApplyStep>()
        };

    public static NativeApplyResult Success(string module, string message, IEnumerable<NativeApplyStep> steps) =>
        new()
        {
            Ok = true,
            Module = module,
            Message = message,
            Steps = steps.ToList()
        };
}

/// <summary>JSON state blob written next to PS optimizers so detect/UI stay compatible.</summary>
public sealed class NativeModuleState
{
    public string Version { get; set; } = "native-1.0";
    public string ApplyStatus { get; set; } = "applied";
    public bool Applied { get; set; } = true;
    public string AppliedUtc { get; set; } = DateTime.UtcNow.ToString("o");
    public bool Experimental { get; set; }
    public string Path { get; set; } = "native-csharp";
    public List<string> ApplyReport { get; set; } = new();
}
