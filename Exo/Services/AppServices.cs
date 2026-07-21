using Exo.Services.Ai;

namespace Exo.Services;

/// <summary>Simple composition root for Exo services.</summary>
public sealed class AppServices
{
    public SettingsService Settings { get; } = new();
    public ThemeService Theme { get; }
    public PowerShellRunnerService PowerShell { get; } = new();
    public ScriptBundleService Scripts { get; }
    public OptimizerStateService OptimizerState { get; }
    public GitHubUpdateService Updater { get; }
    public NvidiaPanelSettingsService NvidiaPanel { get; }
    public ExoInternetOptimizerService Internet { get; }
    public NativeApplyService NativeApply { get; }
    public GameOptimizerService Games { get; } = new();

    public ExoStateManager AiState { get; } = new();
    public ExoSystemInventory AiInventory { get; } = new();
    public ExoToolRegistry AiTools { get; } = new();
    public ExoOptimizerService AiOptimizer { get; }
    public ExoGrokClient AiGrok { get; } = new();
    public ExoAutoInstallService AiAutoInstall { get; } = new();
    public ExoBraveOnlyService AiBraveOnly { get; }
    public ExoUpscalerService AiUpscaler { get; } = new();
    public ExoCompanionService AiCompanions { get; } = new();
    public ExoGpuControlService AiGpu { get; } = new();
    public ExoPcControl AiPcControl { get; } = new();
    public ExoHostOsService AiHostOs { get; }
    public ExoPowerPlanService AiPower { get; } = new();
    public ExoWindowsAiPurgeService AiPurge { get; } = new();
    public ExoAiHands AiHands { get; }
    public ExoAIAgent AiAgent { get; }

    private CancellationTokenSource? _aiRunCts;

    public AppServices()
    {
        Theme = new ThemeService(Settings);
        Scripts = new ScriptBundleService(Settings);
        OptimizerState = new OptimizerStateService(PowerShell, Scripts);
        Updater = new GitHubUpdateService();
        NvidiaPanel = new NvidiaPanelSettingsService(Scripts, PowerShell);
        Internet = new ExoInternetOptimizerService(PowerShell);
        NativeApply = new NativeApplyService(PowerShell);
        AiOptimizer = new ExoOptimizerService(AiTools, AiState);
        AiBraveOnly = new ExoBraveOnlyService(AiAutoInstall);
        AiHostOs = new ExoHostOsService(AiTools, AiOptimizer);
        AiHands = new ExoAiHands(
            NativeApply,
            Internet,
            PowerShell,
            Scripts,
            Settings,
            AiAutoInstall,
            AiBraveOnly,
            AiUpscaler,
            AiCompanions,
            AiGpu,
            AiPcControl,
            Games);
        AiAgent = new ExoAIAgent(
            AiState,
            AiInventory,
            AiTools,
            AiOptimizer,
            AiGrok,
            () => Settings.Current.XaiApiKey,
            () => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
        BindAiToolExecutors();
    }

    private void BindAiToolExecutors()
    {
        // Bind every registered tool id to live ExoAiHands (real Apply / native / PS).
        foreach (var id in AiTools.CatalogIds())
        {
            var toolId = id;
            AiTools.Rebind(toolId, (parameters, ct) =>
                AiHands.RunAsync(toolId, parameters, progress: null, ct));
        }
    }

    public void Initialize()
    {
        Settings.Load();
    }

    public CancellationToken BeginAiRun()
    {
        _aiRunCts?.Cancel();
        _aiRunCts?.Dispose();
        _aiRunCts = new CancellationTokenSource();
        return _aiRunCts.Token;
    }

    public void CancelAiRun()
    {
        try { _aiRunCts?.Cancel(); } catch { /* ignore */ }
    }

    public void WarmInBackground()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                Scripts.EnsureKitsMatchThisApp();
                _ = Scripts.GetDiscordRoot();
                _ = Scripts.GetSteamRoot();
                _ = Scripts.GetNvidiaRoot();
                _ = Scripts.GetGameLaunchersRoot();
                PowerShell.WarmResolvePowerShell();
                _ = await OptimizerState.DetectDiscordAsync(fastOnly: true).ConfigureAwait(false);
                _ = await OptimizerState.DetectSteamAsync(fastOnly: true).ConfigureAwait(false);
                _ = await OptimizerState.DetectNvidiaAsync(fastOnly: true).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort warm
            }
        });
    }
}
