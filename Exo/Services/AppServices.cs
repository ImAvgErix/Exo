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
        Updater = new GitHubUpdateService(Settings, Scripts);
        NvidiaPanel = new NvidiaPanelSettingsService(Scripts, PowerShell);
    }

    public void Initialize()
    {
        Settings.Load();

        _ = Task.Run(async () =>
        {
            // Bind working kits to this exact app version (full replace on upgrade).
            // Safe off the startup path: every Get*Root() self-ensures the stamp
            // under the same lock, so early consumers stay correct while the
            // upgrade copy no longer delays first paint.
            try { Scripts.EnsureKitsMatchThisApp(); }
            catch { /* first-run stamp is best-effort; Get*Root retries */ }

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
