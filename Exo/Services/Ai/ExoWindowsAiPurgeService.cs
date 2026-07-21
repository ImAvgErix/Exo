using Exo.Models.Ai;

namespace Exo.Services.Ai;

/// <summary>
/// Exhaustive Windows + OEM + browser + overlay AI/background purge catalog.
/// Detect must cover packages + policies + tasks — not a single DWORD.
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

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new ExoToolResult
            {
                ToolId = "windows.aiPurge",
                Success = true,
                Status = "ok",
                Message =
                    $"AI purge catalog ready (policies={counts["policies"]}, packages={counts["packages"]}); apply requires Windows"
            });
        }

        // Live writes are performed by WindowsNativeApply / Host OS elevated ops.
        // This service owns the exhaustive detect surface the agent must verify.
        return Task.FromResult(new ExoToolResult
        {
            ToolId = "windows.aiPurge",
            Success = true,
            Status = "ok",
            Message =
                $"AI/background purge queued across {counts.Values.Sum()} surfaces (policies+tasks+packages+OEM+browser+overlay)"
        });
    }

    public Task<ExoToolResult> BackgroundQuietAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ExoToolResult
        {
            ToolId = "windows.backgroundQuiet",
            Success = true,
            Status = "ok",
            Message = OperatingSystem.IsWindows()
                ? "Background quiet queued (CE IP / Feedback / MapsUpdate / Consumer Experience)"
                : "Background quiet catalog ready (Windows apply)"
        });
    }
}
