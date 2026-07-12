using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using OptiHub.Models;
using OptiHub.Services;
using Windows.UI;

namespace OptiHub.ViewModels;

public partial class NvidiaOptimizerViewModel : ObservableObject
{
    private readonly AppServices _services;
    private CancellationTokenSource? _runCts;

    public NvidiaOptimizerViewModel(AppServices services)
    {
        _services = services;
        LastResultBrush = ResolveBrush("OptiSuccessBrush", Color.FromArgb(255, 34, 197, 94));
    }

    [ObservableProperty] private string _statusText = "Checking status...";
    [ObservableProperty] private string _detailText = string.Empty;
    public ObservableCollection<FeatureRowViewModel> Features { get; } = new();

    [ObservableProperty] private string _runButtonLabel = "Apply profile";
    [ObservableProperty] private bool _isApplied;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isProgressVisible;
    [ObservableProperty] private bool _isStatusLoading = true;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressStatus = string.Empty;
    [ObservableProperty] private string _lastResult = string.Empty;
    [ObservableProperty] private bool _hasLastResult;
    [ObservableProperty] private string _lastResultGlyph = "\uE73E";
    [ObservableProperty] private Brush _lastResultBrush;
    [ObservableProperty] private bool _useGsync;

    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsStatusLoading = true;
        try
        {
            StatusText = "Checking status...";
            var state = await _services.OptimizerState.DetectNvidiaAsync();
            ApplyState(state);
        }
        catch (Exception)
        {
            StatusText = "Unavailable";
            DetailText = string.Empty;
            SetResult("Status failed.", success: false);
        }
        finally
        {
            IsStatusLoading = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsBusy) return;

        var action = IsApplied ? "reapply" : "apply";
        var pack = UseGsync ? "G-SYNC pack" : "Max FPS / latency pack";
        var warning =
            $"Driver (if needed) · 3D profiles · strip App/CPL · {pack} · display policy.\n\n" +
            "Needs Administrator. May flicker displays; restart if prompted.";

        var ok = ConfirmAsync is not null
            ? await ConfirmAsync($"Apply NVIDIA ({action})", warning)
            : true;
        if (!ok) return;

        IsBusy = true;
        IsProgressVisible = true;
        ProgressPercent = 0;
        ProgressStatus = "Applying...";
        SetResult(string.Empty, success: true);
        _runCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<ScriptRunProgress>(p =>
            {
                if (p.Percent >= ProgressPercent)
                    ProgressPercent = p.Percent;
                if (!string.IsNullOrWhiteSpace(p.Status))
                    ProgressStatus = p.Status;
            });

            var args = new List<string> { "-NonInteractive" };
            if (UseGsync)
                args.Add("-Gsync");

            var result = await _services.PowerShell.RunAsync(
                _services.Scripts.NvidiaOptimizerScript,
                arguments: args.ToArray(),
                elevate: true,
                progress: progress,
                cancellationToken: _runCts.Token,
                workingDirectory: _services.Scripts.GetNvidiaRoot());

            if (result.Success)
            {
                ProgressPercent = 100;
                var output = $"{result.FullOutput}\n{result.Summary}";
                if (output.Contains("RESTART_REQUIRED", StringComparison.OrdinalIgnoreCase))
                {
                    ProgressStatus = "Restart required";
                    SetResult("Driver installed. Restart Windows, then Apply again.", success: true);
                }
                else if (output.Contains("Clean Driver failed", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("Clean driver failed", StringComparison.OrdinalIgnoreCase))
                {
                    ProgressStatus = "Clean driver failed";
                    SetResult("Clean driver failed. Check log, free space, close games, Apply as Admin.", success: false);
                }
                else if (output.Contains("clean driver -> 3D", StringComparison.OrdinalIgnoreCase) ||
                         output.Contains("Completed successfully", StringComparison.OrdinalIgnoreCase) ||
                         output.Contains("one pass", StringComparison.OrdinalIgnoreCase))
                {
                    ProgressStatus = "Completed successfully";
                    SetResult("Done.", success: true);
                }
                else
                {
                    ProgressStatus = "Completed successfully";
                    SetResult("Done. Profile imported into the NVIDIA driver.", success: true);
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
            SetResult("Cancelled.", success: false);
            ProgressStatus = "Cancelled";
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
                "Reset OptiHub NVIDIA status?",
                "Clears OptiHub status only. Driver and profiles stay.")
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
                _services.Scripts.NvidiaRepairScript,
                arguments: new[] { "-NonInteractive" },
                elevate: false,
                progress: progress,
                cancellationToken: _runCts.Token,
                workingDirectory: _services.Scripts.GetNvidiaRoot());

            SetResult(
                result.Success ? "Status reset." : (result.ErrorMessage ?? result.Summary),
                success: result.Success);

            await RefreshAfterRunAsync();
        }
        catch (OperationCanceledException)
        {
            SetResult("Status reset was cancelled.", success: false);
            ProgressStatus = "Cancelled";
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
            ApplyState(await _services.OptimizerState.DetectNvidiaAsync(fastOnly: false));
        }
        catch { /* ignore */ }
    }

    private void ApplyState(OptimizerStateInfo state)
    {
        IsStatusLoading = false;
        IsApplied = state.IsApplied;
        StatusText = state.StatusText;
        DetailText = string.Empty;
        if (state.Extra is { Count: > 0 } && state.Extra.TryGetValue("gsync", out var g) &&
            bool.TryParse(g, out var gsyncApplied) && IsApplied)
            UseGsync = gsyncApplied;

        Features.Clear();
        foreach (var feature in state.Features)
        {
            Features.Add(new FeatureRowViewModel
            {
                Title = feature.Title,
                Detail = feature.IsActive ? "Applied" : "Not applied",
                Glyph = feature.IsActive ? "\uE73E" : "\uE711",
                Opacity = feature.IsActive ? 1.0 : 0.85
            });
        }
        RunButtonLabel = state.IsApplied ? "Reapply" : "Apply";
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
        catch { }
        return new SolidColorBrush(fallback);
    }

    public async Task InitializeAsync()
    {
        // Full live detect only - no fast heuristic flash of "Already optimized".
        if (IsBusy) return;
        await RefreshAsync();
    }
}
