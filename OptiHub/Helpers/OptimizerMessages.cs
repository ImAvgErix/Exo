namespace OptiHub.Helpers;

/// <summary>Shared finish copy so every optimizer banner reads the same.</summary>
public static class OptimizerMessages
{
    public const string Done = "Done.";
    public const string RepairFinished = "Repair finished.";
    public const string Cancelled = "Cancelled.";
    public const string StatusFailed = "Status failed.";
    public const string RestartRequired = "Driver installed. Restart Windows, then Apply again.";
    public const string CleanDriverFailed =
        "Clean driver failed. Check log, free space, close games, Apply as Admin.";
}
