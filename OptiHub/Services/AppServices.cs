namespace OptiHub.Services;

/// <summary>Simple composition root for OptiHub services.</summary>
public sealed class AppServices
{
    public SettingsService Settings { get; } = new();
    public ThemeService Theme { get; }
    public PowerShellRunnerService PowerShell { get; } = new();
    public ScriptBundleService Scripts { get; }
    public OptimizerStateService OptimizerState { get; }
    public GitHubUpdateService Updater { get; }

    public AppServices()
    {
        Theme = new ThemeService(Settings);
        Scripts = new ScriptBundleService(Settings);
        OptimizerState = new OptimizerStateService(PowerShell, Scripts);
        Updater = new GitHubUpdateService(Settings, Scripts);
    }

    public void Initialize()
    {
        Settings.Load();
        // Defer heavy script sync off the UI thread
        _ = Task.Run(() =>
        {
            try { Scripts.GetDiscordRoot(); }
            catch { /* first-run sync is best-effort */ }
        });
    }
}
