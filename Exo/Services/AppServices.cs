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

    public AppServices()
    {
        Theme = new ThemeService(Settings);
        Scripts = new ScriptBundleService(Settings);
        OptimizerState = new OptimizerStateService(PowerShell, Scripts);
        Updater = new GitHubUpdateService();
        NvidiaPanel = new NvidiaPanelSettingsService(Scripts, PowerShell);
        Network = new NetworkOptimizerService(PowerShell);
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
        _ = Task.Run(() =>
        {
            try
            {
                Scripts.EnsureKitsMatchThisApp();
                // Touch each root once so working kits exist before the user clicks.
                _ = Scripts.GetDiscordRoot();
                _ = Scripts.GetSteamRoot();
                _ = Scripts.GetNvidiaRoot();
                _ = Scripts.GetGameLaunchersRoot();
                PowerShell.WarmResolvePowerShell();
            }
            catch
            {
                // Best-effort warm; real opens re-run the same paths.
            }
        });
    }
}
