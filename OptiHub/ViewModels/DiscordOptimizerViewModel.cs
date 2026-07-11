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
    private readonly AppServices _services;
    private CancellationTokenSource? _runCts;

    public DiscordOptimizerViewModel(AppServices services)
    {
        _services = services;
        LastResultBrush = ResolveBrush("OptiSuccessBrush", Color.FromArgb(255, 34, 197, 94));
    }

    [ObservableProperty]
    private string _statusText = "Checking status...";

    [ObservableProperty]
    private string _detailText = string.Empty;

    public ObservableCollection<FeatureRowViewModel> Features { get; } = new();

    [ObservableProperty]
    private string _runButtonLabel = "Run";

    [ObservableProperty]
    private bool _isApplied;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isStatusLoading = true;

    [ObservableProperty]
    private bool _isFeatureListVisible;

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

    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsStatusLoading = true;
        IsFeatureListVisible = false;
        try
        {
            StatusText = "Checking status...";
            var state = await _services.OptimizerState.DetectDiscordAsync();
            ApplyState(state);
        }
        catch (Exception ex)
        {
            StatusText = "Status check failed";
            DetailText = "Discord was not changed. Try Refresh status again.";
            Features.Clear();
            SetResult($"Could not check Discord: {ex.Message}", success: false);
        }
        finally
        {
            IsStatusLoading = false;
            IsFeatureListVisible = Features.Count > 0;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsBusy) return;

        var action = IsApplied ? "reapply" : "run";
        var warning =
            "This is an AGGRESSIVE, no-compromise performance pass. It will close Discord and apply:\n\n" +
            "• Equicord + OpenASAR privacy/startup tuning\n" +
            "• DiscOpt kernel RAM reclamation every 5 seconds, Above Normal priority, and raw-input/thread tuning\n" +
            "• Deep cache cleaning and full allowlisted debloat\n" +
            "• Removal of clips/hook modules, game SDK files, and non-English locale packs\n" +
            "• Disabled Windows startup, scheduled tasks, toasts, and tray promotion\n\n" +
            "This trades background convenience, multilingual UI assets, notifications, and optional Discord features for latency, RAM, and startup performance. Your login/session and updater integrity data are preserved. Administrator approval is required.\n\n" +
            "Use Repair Discord to return to a clean stock client.";

        var ok = ConfirmAsync is not null
            ? await ConfirmAsync($"Confirm aggressive Discord optimizer ({action})", warning)
            : true;
        if (!ok) return;

        IsBusy = true;
        IsProgressVisible = true;
        ProgressPercent = 0;
        ProgressStatus = "Preparing...";
        SetResult(string.Empty, success: true);
        _runCts = new CancellationTokenSource();

        try
        {
            var args = new List<string>
            {
                "-NonInteractive"
            };

            var progress = new Progress<ScriptRunProgress>(p =>
            {
                // Never let the bar jump backwards during a live run
                if (p.Percent >= ProgressPercent)
                    ProgressPercent = p.Percent;
                if (!string.IsNullOrWhiteSpace(p.Status))
                    ProgressStatus = p.Status;
            });

            var script = _services.Scripts.DiscordOptimizerScript;
            var result = await _services.PowerShell.RunAsync(
                script,
                arguments: args,
                elevate: true,
                progress: progress,
                cancellationToken: _runCts.Token,
                workingDirectory: _services.Scripts.GetDiscordRoot());

            if (result.Success)
            {
                ProgressPercent = 100;
                ProgressStatus = "Completed successfully";
                SetResult("Done. Open Discord when ready.", success: true);
                try
                {
                    _services.Settings.Update(s =>
                        s.LastDiscordRunUtc = DateTime.UtcNow.ToString("o"));
                }
                catch
                {
                    // The optimization succeeded even if optional run history
                    // could not be persisted.
                }
            }
            else
            {
                ProgressStatus = result.ExitCode == -2 ? "Cancelled" : "Failed";
                SetResult(result.ErrorMessage ?? result.Summary, success: false);
            }

            await RefreshAfterRunAsync();
        }
        catch (OperationCanceledException)
        {
            ProgressStatus = "Cancelled";
            SetResult("Discord optimization was cancelled.", success: false);
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

        var ok = ConfirmAsync is not null
            ? await ConfirmAsync(
                "Repair Discord?",
                "This restores a clean, stock Discord install while keeping your login. Optimizations will be removed. Administrator approval may be required.")
            : true;
        if (!ok) return;

        IsBusy = true;
        IsProgressVisible = true;
        ProgressPercent = 0;
        ProgressStatus = "Starting repair...";
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
            ProgressStatus = result.Success ? "Repair complete" : (result.ExitCode == -2 ? "Cancelled" : "Repair failed");
            if (result.Success)
                ProgressPercent = 100;

            await RefreshAfterRunAsync();
        }
        catch (OperationCanceledException)
        {
            ProgressStatus = "Cancelled";
            SetResult("Discord repair was cancelled.", success: false);
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
        Features.Clear();
        foreach (var feature in state.Features)
        {
            Features.Add(new FeatureRowViewModel
            {
                Title = feature.Title,
                Detail = feature.Detail,
                Glyph = feature.IsActive ? "\uE73E" : "\uE711",
                Opacity = feature.IsActive ? 1.0 : 0.55
            });
        }
        RunButtonLabel = state.IsApplied ? "Reapply" : "Run";
        if (!IsStatusLoading)
            IsFeatureListVisible = Features.Count > 0;
    }

    private void SetResult(string message, bool success)
    {
        LastResult = message;
        HasLastResult = !string.IsNullOrWhiteSpace(message);
        LastResultGlyph = success ? "\uE73E" : "\uE783";
        LastResultBrush = success
            ? ResolveBrush("OptiSuccessBrush", Color.FromArgb(255, 34, 197, 94))
            : ResolveBrush("OptiErrorBrush", Color.FromArgb(255, 220, 38, 38));
    }

    private static Brush ResolveBrush(string key, Color fallback)
    {
        try
        {
            if (Microsoft.UI.Xaml.Application.Current?.Resources.TryGetValue(key, out var value) == true
                && value is Brush brush)
                return brush;
        }
        catch { /* theme resource may be unavailable early */ }
        return new SolidColorBrush(fallback);
    }

    public async Task InitializeAsync()
    {
        // Full live detect only — no fast heuristic flash of "Already optimized".
        if (IsBusy) return;
        await RefreshAsync();
    }
}

public sealed class FeatureRowViewModel
{
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string Glyph { get; init; } = "\uE73E";
    public double Opacity { get; init; } = 1.0;
}
