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
    public NvidiaPanelSettingsService NvidiaPanel { get; }
    public NetworkOptimizerService Network { get; } = new();

    public AppServices()
    {
        Theme = new ThemeService(Settings);
        Scripts = new ScriptBundleService(Settings);
        OptimizerState = new OptimizerStateService(PowerShell, Scripts);
        Updater = new GitHubUpdateService(Settings, Scripts);
        NvidiaPanel = new NvidiaPanelSettingsService(Scripts, PowerShell);
    }

    public void Initialize()
    {
        Settings.Load();
        // Bind working kits to this exact app version first (full replace on upgrade),
        // then warm Discord root off the UI thread.
        try { Scripts.EnsureKitsMatchThisApp(); }
        catch { /* first-run stamp is best-effort; Get*Root retries */ }

        _ = Task.Run(async () =>
        {
            try
            {
                // Prefer PowerShell 7 Preview + Windows Terminal Preview; install via winget if missing.
                await PowerShellRunnerService.EnsurePowerShellRuntimeAsync().ConfigureAwait(false);
            }
            catch { /* install is best-effort; RunAsync still surfaces clear errors */ }

            try
            {
                Scripts.GetDiscordRoot();
                Scripts.GetSteamRoot();
                Scripts.GetNvidiaRoot();
            }
            catch { /* first-run sync is best-effort */ }
        });
    }
}
