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
        AiTools.Rebind("hostOs.maximize", async (_, ct) =>
        {
            var results = await AiOptimizer
                .ExecuteAsync(ExoHostOsService.BuildMaximizeActions(), ct: ct)
                .ConfigureAwait(false);
            var ok = results.Count == 0 || results.Any(r => r.Success);
            return new Models.Ai.ExoToolResult
            {
                Success = ok,
                Status = ok ? "ok" : "partial",
                Message = $"Host OS maximize: {results.Count(r => r.Success)}/{results.Count} ok"
            };
        });

        AiTools.Rebind("power.exoCompetitive", async (_, ct) =>
            await AiPower.ApplyAsync(ct: ct).ConfigureAwait(false));

        AiTools.Rebind("windows.aiPurge", async (_, ct) =>
            await AiPurge.PurgeAsync(ct: ct).ConfigureAwait(false));

        AiTools.Rebind("windows.backgroundQuiet", async (_, ct) =>
            await AiPurge.BackgroundQuietAsync(ct).ConfigureAwait(false));

        AiTools.Rebind("browser.braveOnly", async (_, ct) =>
        {
            var (ok, msg, n) = await AiBraveOnly.EnforceAsync(ct: ct).ConfigureAwait(false);
            return new Models.Ai.ExoToolResult
            {
                Success = ok,
                Status = ok ? "ok" : "error",
                Message = msg,
                After = n.ToString()
            };
        });

        AiTools.Rebind("upscaler.maximizeSupportedGames", (_, _) =>
        {
            if (!Settings.Current.UpscalerRiskAcknowledged)
            {
                return Task.FromResult(new Models.Ai.ExoToolResult
                {
                    Success = false,
                    Status = "blocked",
                    Message = "Acknowledge upscaler risk in Settings before swapping DLLs."
                });
            }

            return Task.FromResult(new Models.Ai.ExoToolResult
            {
                Success = true,
                Status = "ok",
                Message = "Upscaler maximize ready (scan+swap on Windows with backups)"
            });
        });

        AiTools.Rebind("gpu.control.maximize", (_, _) =>
        {
            var (ok, msg) = AiGpu.Maximize();
            return Task.FromResult(new Models.Ai.ExoToolResult
            {
                Success = ok,
                Status = ok ? "ok" : "error",
                Message = msg
            });
        });

        foreach (var (toolId, companionId) in new[]
                 {
                     ("companion.snip.install", "snip"),
                     ("companion.notepad.install", "notepad"),
                     ("companion.photos.install", "photos"),
                     ("companion.taskManager.install", "taskManager"),
                     ("search.everything", "everything"),
                     ("eartrumpet.install", "earTrumpet")
                 })
        {
            var capture = companionId;
            AiTools.Rebind(toolId, (_, _) =>
            {
                var (ok, msg) = AiCompanions.EnsureInstalled(capture);
                return Task.FromResult(new Models.Ai.ExoToolResult
                {
                    Success = ok,
                    Status = ok ? "ok" : "error",
                    Message = msg
                });
            });
        }

        foreach (var target in new[] { "discord", "steam", "brave", "riot", "epic" })
        {
            var capture = target;
            AiTools.Rebind($"module.{capture}.apply", async (_, ct) =>
            {
                var (ok, msg) = await AiAutoInstall.EnsureInstalledAsync(capture, ct: ct)
                    .ConfigureAwait(false);
                return new Models.Ai.ExoToolResult
                {
                    Success = ok || AiAutoInstall.IsPresent(capture),
                    Status = ok ? "ok" : "needs-network",
                    Message = msg + " — module Apply follows when present"
                };
            });
        }

        AiTools.Rebind("automation.ui.settings", async (p, ct) =>
        {
            var page = p.GetValueOrDefault("page") ?? "display";
            var (ok, msg) = await AiPcControl.RunUiSequenceAsync("settings:" + page, ct)
                .ConfigureAwait(false);
            return new Models.Ai.ExoToolResult
            {
                Success = ok,
                Status = ok ? "ok" : "error",
                Message = msg
            };
        });
    }

    public void Initialize()
    {
        Settings.Load();
        // No startup downloads, package installs, or script copies. Optimizer kits
        // self-sync in Get*Root(), and PowerShell 7 is prepared only after a user
        // explicitly starts Apply/Repair.
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
                _ = Scripts.GetGameLaunchersRoot();
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
