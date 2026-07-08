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
    private string _title = "Discord";

    public string LogoPath => "Assets/Logos/discord.png";

    [ObservableProperty]
    private string _statusText = "Checking statusâ€¦";

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
            StatusText = "Checking statusâ€¦";
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

        var action = IsApplied ? "reapply" : "run";
        var warning =
            "This will close Discord, apply optimizations (Equicord, OpenASAR, kernel, cache, Windows tweaks), and may request Administrator approval.\n\n" +
            "Your login/session is preserved. A repair path is available if anything goes wrong.\n\n" +
            "Run mode is detected automatically. A system restore point will be created first when Windows allows.";

        var ok = ConfirmAsync is not null
            ? await ConfirmAsync($"Confirm Discord Optimizer ({action})", warning)
            : true;
        if (!ok) return;

        IsBusy = true;
        IsProgressVisible = true;
        ProgressPercent = 0;
        ProgressStatus = "Preparingâ€¦";
        SetResult(string.Empty, success: true);
        _runCts = new CancellationTokenSource();

        try
        {
            var args = new List<string>
            {
                "-CreateRestorePoint",
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
                SetResult("Optimizer finished successfully. Open Discord when you are ready.", success: true);
                _services.Settings.Update(s =>
                    s.LastDiscordRunUtc = DateTime.UtcNow.ToString("o"));
            }
            else
            {
                SetResult(result.ErrorMessage ?? result.Summary, success: false);
            }

            await RefreshAfterRunAsync();
            // Brief hold so the finished bar is visible
            await Task.Delay(700);
        }
        catch (Exception ex)
        {
            SetResult(ex.Message, success: false);
            ProgressStatus = "Failed";
        }
        finally
        {
            IsBusy = false;
            if (ProgressPercent >= 100)
                await Task.Delay(900);
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
        ProgressStatus = "Starting repairâ€¦";
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
        ApplyState(await _services.OptimizerState.DetectDiscordAsync(fastOnly: true));
        if (!IsBusy)
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
