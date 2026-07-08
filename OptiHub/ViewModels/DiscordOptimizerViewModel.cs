using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using OptiHub.Models;
using OptiHub.Services;
using Windows.UI;

namespace OptiHub.ViewModels;

public partial class DiscordOptimizerViewModel : ObservableObject
{
    private static readonly SolidColorBrush SuccessBrush =
        new(Color.FromArgb(255, 255, 255, 255));
    private static readonly SolidColorBrush ErrorBrush =
        new(Color.FromArgb(255, 248, 113, 113));

    private readonly AppServices _services;
    private CancellationTokenSource? _runCts;

    public DiscordOptimizerViewModel(AppServices services)
    {
        _services = services;
        LastResultBrush = SuccessBrush;
    }

    [ObservableProperty]
    private string _title = "Discord Optimizer";

    public string LogoPath => "Assets/Logos/discord.png";

    [ObservableProperty]
    private string _statusText = "Checking status…";

    [ObservableProperty]
    private string _detailText = string.Empty;

    public ObservableCollection<string> Checks { get; } = new();

    [ObservableProperty]
    private string _runButtonLabel = "Run";

    [ObservableProperty]
    private bool _isApplied;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isProgressVisible;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _progressStatus = string.Empty;

    [ObservableProperty]
    private string _lastResult = string.Empty;

    [ObservableProperty]
    private bool _hasLastResult;

    [ObservableProperty]
    private string _lastResultGlyph = "\uE73E";

    [ObservableProperty]
    private Brush _lastResultBrush;

    public event EventHandler? RequestGoBack;

    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    [RelayCommand]
    private void GoBack() => RequestGoBack?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            StatusText = "Checking status…";
            var state = await _services.OptimizerState.DetectDiscordAsync();
            ApplyState(state);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsBusy) return;

        var settings = _services.Settings.Current;
        var action = IsApplied ? "reapply" : "run";

        if (settings.ConfirmBeforeRun)
        {
            var warning = settings.DryRun
                ? "Dry-run is enabled. No system changes will be made — the optimizer will verify only."
                : "This will close Discord, apply optimizations (Equicord, OpenASAR, kernel, cache, Windows tweaks), and may request Administrator approval.\n\n" +
                  "Your login/session is preserved. A repair path is available if anything goes wrong.\n\n" +
                  "Run mode is detected automatically (Quick reapply when already optimized).";

            if (settings.AutoRestorePoint && !settings.DryRun)
                warning += "\n\nA system restore point will be created first (if Windows allows).";

            var ok = ConfirmAsync is not null
                ? await ConfirmAsync($"Confirm Discord Optimizer ({action})", warning)
                : true;
            if (!ok) return;
        }

        IsBusy = true;
        IsProgressVisible = true;
        ProgressPercent = 0;
        ProgressStatus = "Preparing…";
        SetResult(string.Empty, success: true);
        _runCts = new CancellationTokenSource();

        try
        {
            var args = new List<string>();
            if (settings.DryRun)
                args.Add("-DryRun");
            if (settings.AutoRestorePoint && !settings.DryRun)
                args.Add("-CreateRestorePoint");
            args.Add("-NoLaunch");
            args.Add("-NonInteractive");

            var progress = new Progress<ScriptRunProgress>(p =>
            {
                ProgressPercent = p.Percent;
                ProgressStatus = p.Status;
            });

            var script = _services.Scripts.DiscordOptimizerScript;
            var result = await _services.PowerShell.RunAsync(
                script,
                arguments: args,
                elevate: !settings.DryRun,
                progress: progress,
                cancellationToken: _runCts.Token,
                workingDirectory: _services.Scripts.GetDiscordRoot());

            if (result.Success)
            {
                SetResult(
                    settings.DryRun
                        ? "Dry-run finished. No changes were applied."
                        : "Optimizer finished successfully.",
                    success: true);
                if (!settings.DryRun)
                {
                    _services.Settings.Update(s =>
                        s.LastDiscordRunUtc = DateTime.UtcNow.ToString("o"));
                }
            }
            else
            {
                SetResult(result.ErrorMessage ?? result.Summary, success: false);
            }

            await RefreshAfterRunAsync();
        }
        catch (Exception ex)
        {
            SetResult(ex.Message, success: false);
            ProgressStatus = "Failed";
        }
        finally
        {
            IsBusy = false;
            IsProgressVisible = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    [RelayCommand]
    private async Task RepairAsync()
    {
        if (IsBusy) return;

        var settings = _services.Settings.Current;
        if (settings.ConfirmBeforeRun)
        {
            var ok = ConfirmAsync is not null
                ? await ConfirmAsync(
                    "Repair Discord?",
                    "This restores a clean, stock Discord install while keeping your login. Optimizations will be removed. Administrator approval may be required.")
                : true;
            if (!ok) return;
        }

        IsBusy = true;
        IsProgressVisible = true;
        ProgressPercent = 0;
        ProgressStatus = "Starting repair…";
        SetResult(string.Empty, success: true);
        _runCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<ScriptRunProgress>(p =>
            {
                ProgressPercent = p.Percent;
                ProgressStatus = p.Status;
            });

            var result = await _services.PowerShell.RunAsync(
                _services.Scripts.DiscordRepairScript,
                arguments: new[] { "-NonInteractive" },
                elevate: true,
                progress: progress,
                cancellationToken: _runCts.Token,
                workingDirectory: _services.Scripts.GetDiscordRoot());

            SetResult(
                result.Success
                    ? "Repair finished. Discord should be stock and bootable."
                    : (result.ErrorMessage ?? result.Summary),
                success: result.Success);

            await RefreshAfterRunAsync();
        }
        catch (Exception ex)
        {
            SetResult(ex.Message, success: false);
        }
        finally
        {
            IsBusy = false;
            IsProgressVisible = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private async Task RefreshAfterRunAsync()
    {
        try
        {
            var state = await _services.OptimizerState.DetectDiscordAsync(fastOnly: false);
            ApplyState(state);
        }
        catch { /* ignore */ }
    }

    private void ApplyState(OptimizerStateInfo state)
    {
        IsApplied = state.IsApplied;
        StatusText = state.StatusText;
        DetailText = state.Detail;
        Checks.Clear();
        foreach (var check in state.Checks)
            Checks.Add(check);
        RunButtonLabel = state.IsApplied ? "Reapply" : "Run";
    }

    private void SetResult(string message, bool success)
    {
        LastResult = message;
        HasLastResult = !string.IsNullOrWhiteSpace(message);
        LastResultGlyph = success ? "\uE73E" : "\uE783";
        LastResultBrush = success ? SuccessBrush : ErrorBrush;
    }

    public async Task InitializeAsync()
    {
        ApplyState(await _services.OptimizerState.DetectDiscordAsync(fastOnly: true));
        if (!IsBusy)
            await RefreshAsync();
    }
}
