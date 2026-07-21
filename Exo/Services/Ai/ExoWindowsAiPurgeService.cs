using Exo.Models.Ai;

namespace Exo.Services.Ai;

/// <summary>
/// Exhaustive AI/background purge catalog. Live writes: ExoAiHands → WindowsNativeApply.ApplyAiPurgeOnly.
/// </summary>
public sealed class ExoWindowsAiPurgeService
{
    public static readonly IReadOnlyList<string> PolicyKeys =
    [
        @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot|TurnOffWindowsCopilot",
        @"HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot|TurnOffWindowsCopilot",
        @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI|DisableAIDataAnalysis",
        @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI|TurnOffSavingSnapshots",
        @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI|AllowRecallEnablement",
        @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI|DisableClickToDo",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced|ShowCopilotButton",
        @"HKCU\Software\Microsoft\Windows\Shell\Copilot|IsCopilotAvailable",
        @"HKLM\SOFTWARE\Policies\Microsoft\Dsh|AllowNewsAndInterests",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Feeds|ShellFeedsTaskbarViewMode",
        @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search|DisableWebSearch",
        @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search|ConnectedSearchUseWeb",
        @"HKCU\Software\Microsoft\Input\Settings|InsightsEnabled",
        @"HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent|DisableWindowsConsumerFeatures",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager|SubscribedContent-338387Enabled",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager|SystemPaneSuggestionsEnabled"
    ];

    public static readonly IReadOnlyList<string> ScheduledTaskPaths =
    [
        @"\Microsoft\Windows\WindowsAI\",
        @"\Microsoft\Windows\Shell\FamilySafetyMonitor",
        @"\Microsoft\Windows\CloudExperienceHost\",
        @"\Microsoft\Windows\Feedback\Siuf\",
        @"\Microsoft\Windows\Customer Experience Improvement Program\",
        @"\Microsoft\Windows\Application Experience\",
        @"\Microsoft\Windows\Maps\MapsUpdateTask"
    ];

    public static readonly IReadOnlyList<string> PackageNameHints =
    [
        "Microsoft.Copilot",
        "Microsoft.Windows.Ai.Studio",
        "MicrosoftWindows.Client.WebExperience",
        "Microsoft.BingNews",
        "Microsoft.BingWeather",
        "Microsoft.WidgetsPlatformRuntime",
        "Clipchamp.Clipchamp",
        "Microsoft.GetHelp",
        "Microsoft.Getstarted",
        "Microsoft.MicrosoftOfficeHub",
        "Microsoft.WindowsFeedbackHub"
    ];

    public static readonly IReadOnlyList<string> OemAiProcessHints =
    [
        "ArmouryCrate", "ASUSOptimization", "DragonCenter", "MysticLight",
        "LenovoVantage", "SmartAppearance", "KillerControlCenter", "KillerNetworkService",
        "DellSupportAssist", "PCManager", "HPJumpStart", "MyASUS"
    ];

    public static readonly IReadOnlyList<string> BrowserAiPrefs =
    [
        "brave.leo.enabled=false",
        "brave.ai_chat.enabled=false",
        "edge.sidebar.copilot=false"
    ];

    public static readonly IReadOnlyList<string> OverlayAiHints =
    [
        "NVIDIA Overlay AI", "AMD Broadcast AI noise", "Xbox Game Bar widgets AI"
    ];

    public IReadOnlyDictionary<string, int> CatalogCounts() => new Dictionary<string, int>
    {
        ["policies"] = PolicyKeys.Count,
        ["tasks"] = ScheduledTaskPaths.Count,
        ["packages"] = PackageNameHints.Count,
        ["oem"] = OemAiProcessHints.Count,
        ["browserAi"] = BrowserAiPrefs.Count,
        ["overlays"] = OverlayAiHints.Count
    };

    public Task<ExoToolResult> PurgeAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var counts = CatalogCounts();
        progress?.Report(
            $"ai-purge: policies={counts["policies"]} tasks={counts["tasks"]} packages={counts["packages"]}");
        return Task.FromResult(new ExoToolResult
        {
            ToolId = "windows.aiPurge",
            Success = true,
            Status = OperatingSystem.IsWindows() ? "ok" : "skip",
            Message = OperatingSystem.IsWindows()
                ? $"Route via ExoAiHands → ApplyAiPurgeOnly ({counts.Values.Sum()} surfaces)"
                : $"AI purge catalog ready (policies={counts["policies"]}, packages={counts["packages"]}); apply requires Windows"
        });
    }

    public Task<ExoToolResult> BackgroundQuietAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ExoToolResult
        {
            ToolId = "windows.backgroundQuiet",
            Success = true,
            Status = OperatingSystem.IsWindows() ? "ok" : "skip",
            Message = OperatingSystem.IsWindows()
                ? "Background quiet via Host OS / module.windows.apply (PC-aware Task Scheduler)"
                : "Background quiet catalog ready (Windows apply)"
        });
    }
}
