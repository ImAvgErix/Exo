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
    public NetworkOptimizerService Network { get; } = new();

    public AppServices()
    {
        Theme = new ThemeService(Settings);
        Scripts = new ScriptBundleService(Settings);
        OptimizerState = new OptimizerStateService(PowerShell, Scripts);
        Updater = new GitHubUpdateService();
        NvidiaPanel = new NvidiaPanelSettingsService(Scripts, PowerShell);
    }

    public void Initialize()
    {
        Settings.Load();
        // No startup downloads, package installs, or script copies. Optimizer kits
        // self-sync in Get*Root(), and PowerShell 7 is prepared only after a user
        // explicitly starts Apply/Repair.
    }
}
