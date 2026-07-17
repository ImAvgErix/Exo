using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Exo.Models;
using Exo.Services;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Exo.ViewModels;

public partial class NvidiaOptimizerViewModel : ObservableObject
{
    private readonly AppServices _services;
    private CancellationTokenSource? _runCts;

    public NvidiaOptimizerViewModel(AppServices services)
    {
        _services = services;
        LastResultBrush = ResolveBrush("ExoSuccessBrush", Color.FromArgb(255, 34, 197, 94));
    }

    [ObservableProperty] public partial string StatusText { get; set; } = "Checking status...";
    [ObservableProperty] public partial string DetailText { get; set; } = string.Empty;
    [ObservableProperty] public partial string GuidanceText { get; set; } = "Detecting this PC...";
    [ObservableProperty] public partial bool HasGuidance { get; set; } = true;
    public ObservableCollection<FeatureRowViewModel> Features { get; } = new();

    [ObservableProperty] public partial string RunButtonLabel { get; set; } = "Apply profile";
    [ObservableProperty] public partial bool IsApplied { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsProgressVisible { get; set; }
    [ObservableProperty] public partial bool IsStatusLoading { get; set; } = true;
    [ObservableProperty] public partial bool IsFeatureListVisible { get; set; }
    [ObservableProperty] public partial double ProgressPercent { get; set; }
    [ObservableProperty] public partial string ProgressStatus { get; set; } = string.Empty;
    [ObservableProperty] public partial string LastResult { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasLastResult { get; set; }
    [ObservableProperty] public partial string LastResultGlyph { get; set; } = "\uE73E";
    [ObservableProperty] public partial Brush LastResultBrush { get; set; }
    [ObservableProperty] public partial bool UseGsync { get; set; }

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
            var state = await _services.OptimizerState.DetectNvidiaAsync();
            ApplyState(state);
        }
        catch (Exception)
        {
            StatusText = "Unavailable";
            DetailText = string.Empty;
            SetResult(Helpers.OptimizerMessages.StatusFailed, success: false);
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

        // No confirm on Apply — just run (Repair still confirms).
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
                workingDirectory: _services.Scripts.GetNvidiaRoot(),
                ensureRuntime: true);

            // Driver elevate can thrash the desktop; re-pin shell chrome.
            try
            {
                if (App.MainAppWindow is MainWindow mw)
                    mw.StabilizeShellAfterExternalWork();
            }
            catch { }

            if (result.Success)
            {
                ProgressPercent = 100;
                var output = $"{result.FullOutput}\n{result.Summary}";
                if (output.Contains("RESTART_REQUIRED", StringComparison.OrdinalIgnoreCase))
                {
                    ProgressStatus = "Restart required";
                    SetResult(Helpers.OptimizerMessages.RestartRequired, success: true);
                }
                else if (output.Contains("Clean Driver failed", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("Clean driver failed", StringComparison.OrdinalIgnoreCase))
                {
                    ProgressStatus = "Clean driver failed";
                    SetResult(Helpers.OptimizerMessages.CleanDriverFailed, success: false);
                }
                else
                {
                    ProgressStatus = "Completed successfully";
                    SetResult(Helpers.OptimizerMessages.Done, success: true);
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
            SetResult(Helpers.OptimizerMessages.Cancelled, success: false);
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

        // Reset is status-clear only (never a driver rollback) — runs immediately;
        // the page carries the honest one-line description next to the button.
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
                workingDirectory: _services.Scripts.GetNvidiaRoot(),
                ensureRuntime: true);

            SetResult(
                result.Success ? Helpers.OptimizerMessages.NvidiaStatusCleared : (result.ErrorMessage ?? result.Summary),
                success: result.Success);

            await RefreshAfterRunAsync();
        }
        catch (OperationCanceledException)
        {
            SetResult(Helpers.OptimizerMessages.Cancelled, success: false);
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
                Glyph = Helpers.UiStatusPresentation.FeatureGlyph(feature.IsActive),
                Opacity = Helpers.UiStatusPresentation.FeatureOpacity(feature.IsActive),
                IsActive = feature.IsActive,
                RailOpacity = Helpers.UiStatusPresentation.FeatureRailOpacity(feature.IsActive)
            });
        }
        RunButtonLabel = state.IsApplied ? "Reapply" : "Apply";
        IsFeatureListVisible = Features.Count > 0;
        GuidanceText = OptimizerAdvisor.BuildV2(
            "NVIDIA",
            IsApplied,
            StatusText,
            DetailText,
            Features.Select(f => (f.Title, f.IsActive, f.Detail)).ToList(),
            reportFailSteps: null);
        HasGuidance = !string.IsNullOrWhiteSpace(GuidanceText);
    }

    private void SetResult(string message, bool success)
    {
        LastResult = message;
        HasLastResult = !string.IsNullOrWhiteSpace(message);
        var banner = Helpers.UiStatusPresentation.BannerForSuccess(success);
        LastResultGlyph = banner.Glyph;
        LastResultBrush = ResolveBrush(
            banner.BrushKey,
            success ? Color.FromArgb(255, 34, 197, 94) : Color.FromArgb(255, 220, 38, 38));
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
