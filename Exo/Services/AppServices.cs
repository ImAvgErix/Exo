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
    public NetworkOptimizerService Network { get; }
    public NativeApplyService NativeApply { get; }
    public GameOptimizerService Games { get; } = new();

    public AppServices()
    {
        Theme = new ThemeService(Settings);
        Scripts = new ScriptBundleService(Settings);
        OptimizerState = new OptimizerStateService(PowerShell, Scripts);
        Updater = new GitHubUpdateService();
        NvidiaPanel = new NvidiaPanelSettingsService(Scripts, PowerShell);
        Network = new NetworkOptimizerService(PowerShell);
        NativeApply = new NativeApplyService(PowerShell);
    }

    public void Initialize()
    {
        Settings.Load();
        // No startup downloads, package installs, or script copies. Optimizer kits
        // self-sync in Get*Root(), and PowerShell 7 is prepared only after a user
        // explicitly starts Apply/Repair.
    }

    /// <summary>
    /// After first paint: stage kit trees + resolve pwsh off the UI thread so the
    /// first module open does not pay a cold kit copy + PATH scan.
    /// </summary>
    public void WarmInBackground()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                Scripts.EnsureKitsMatchThisApp();
                // Touch each root once so working kits exist before the user clicks.
                _ = Scripts.GetDiscordRoot();
                _ = Scripts.GetSteamRoot();
                _ = Scripts.GetNvidiaRoot();
                PowerShell.WarmResolvePowerShell();
                // Fast heuristics only — prime status JSON path so first module
                // open is less cold (full script detect still runs on open).
                _ = await OptimizerState.DetectDiscordAsync(fastOnly: true).ConfigureAwait(false);
                _ = await OptimizerState.DetectSteamAsync(fastOnly: true).ConfigureAwait(false);
                _ = await OptimizerState.DetectNvidiaAsync(fastOnly: true).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort warm; real opens re-run the same paths.
            }
        });
    }
}
