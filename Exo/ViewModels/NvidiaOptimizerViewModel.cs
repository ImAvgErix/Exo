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
        try
        {
            PreferExperimental = services.Settings.Current.ExperimentalNvidia;
            SelectedApplyMode = PreferExperimental ? "Experimental" : "Stable";
            SelectedProfileOption = UseGsync ? "G-SYNC / VRR" : "Raw latency";
        }
        catch { }
    }

    partial void OnSelectedApplyModeChanged(string value)
    {
        var exp = string.Equals(value, "Experimental", StringComparison.OrdinalIgnoreCase);
        if (PreferExperimental != exp) PreferExperimental = exp;
    }

    partial void OnPreferExperimentalChanged(bool value)
    {
        var mode = value ? "Experimental" : "Stable";
        if (!string.Equals(SelectedApplyMode, mode, StringComparison.Ordinal))
            SelectedApplyMode = mode;
        try { _services.Settings.Update(s => s.ExperimentalNvidia = value); } catch { }
    }

    partial void OnSelectedProfileOptionChanged(string value)
    {
        var gsync = string.Equals(value, "G-SYNC / VRR", StringComparison.OrdinalIgnoreCase);
        if (UseGsync != gsync) UseGsync = gsync;
    }

    partial void OnUseGsyncChanged(bool value)
    {
        var opt = value ? "G-SYNC / VRR" : "Raw latency";
        if (!string.Equals(SelectedProfileOption, opt, StringComparison.Ordinal))
            SelectedProfileOption = opt;
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
    [ObservableProperty] public partial bool PreferExperimental { get; set; }
    public IReadOnlyList<string> ApplyModeOptions { get; } = new[] { "Stable", "Experimental" };
    [ObservableProperty] public partial string SelectedApplyMode { get; set; } = "Stable";
    public IReadOnlyList<string> ProfileOptions { get; } = new[] { "Raw latency", "G-SYNC / VRR" };
    [ObservableProperty] public partial string SelectedProfileOption { get; set; } = "Raw latency";
    [ObservableProperty] public partial bool IsFeatureListVisible { get; set; }
    [ObservableProperty] public partial double ProgressPercent { get; set; }
    [ObservableProperty] public partial string ProgressStatus { get; set; } = string.Empty;
    [ObservableProperty] public partial string LastResult { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasLastResult { get; set; }
    [ObservableProperty] public partial string LastResultGlyph { get; set; } = "\uE73E";
    [ObservableProperty] public partial Brush LastResultBrush { get; set; }
    [ObservableProperty] public partial string HardwarePolicyText { get; set; } = "Detecting GPU and displays...";
    [ObservableProperty] public partial bool UseGsync { get; set; }

    private DateTimeOffset _lastFullDetectUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan DetectFreshness = TimeSpan.FromSeconds(120);
    private int _detectGen;
    private CancellationTokenSource? _detectCts;

    [RelayCommand]
    private Task RefreshAsync() => RefreshCoreAsync(force: true);

    public void CancelBackgroundWork()
    {
        try { _detectCts?.Cancel(); } catch { }
        Interlocked.Increment(ref _detectGen);
    }

    private async Task RefreshCoreAsync(bool force)
    {
        if (!force && Features.Count > 0 && DateTimeOffset.UtcNow - _lastFullDetectUtc < DetectFreshness)
        {
            IsStatusLoading = false;
            IsFeatureListVisible = true;
            return;
        }

        try { _detectCts?.Cancel(); } catch { }
        _detectCts?.Dispose();
        _detectCts = new CancellationTokenSource();
        var ct = _detectCts.Token;
        var gen = Interlocked.Increment(ref _detectGen);
        if (Features.Count == 0)
        {
            IsStatusLoading = true;
            IsFeatureListVisible = false;
            StatusText = "Checking status...";
        }
        try
        {
            var state = await _services.OptimizerState.DetectNvidiaAsync(ct, fastOnly: false).ConfigureAwait(true);
            if (gen != _detectGen || ct.IsCancellationRequested) return;
            ApplyState(state);
            SelectedProfileOption = UseGsync ? "G-SYNC / VRR" : "Raw latency";
            _lastFullDetectUtc = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (gen == _detectGen && Features.Count == 0)
            {
                StatusText = "Unavailable";
                DetailText = string.Empty;
                SetResult(Helpers.OptimizerMessages.StatusFailed, success: false);
            }
        }
        finally
        {
            if (gen == _detectGen)
            {
                IsStatusLoading = false;
                IsFeatureListVisible = Features.Count > 0;
            }
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

            var args = new List<string> { "-NonInteractive", "-SafePolicy" };
            args.Add(UseGsync ? "-Gsync" : "-RawLatency");
            args.Add("-Experimental");

            var result = await _services.PowerShell.RunAsync(
                _services.Scripts.NvidiaOptimizerScript,
                arguments: args,
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
    private void OpenControlPanel()
    {
        if (_services.NvidiaPanel.TryLaunchControlPanel(out var error))
            SetResult("Opened NVIDIA Control Panel.", success: true);
        else
            SetResult(error ?? "NVIDIA Control Panel is not installed yet. Run Apply first.", success: false);
    }

    [RelayCommand]
    private async Task RepairAsync()
    {
        if (IsBusy) return;

        // Repair restores the complete DRS snapshot captured before Exo imported
        // its Base + per-game profiles. The safe policy does not mutate drivers.
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
                elevate: true,
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
        HardwarePolicyText = state.Extra is { Count: > 0 } &&
            state.Extra.TryGetValue("hardwareSummary", out var hardware) &&
            !string.IsNullOrWhiteSpace(hardware)
                ? hardware
                : "Apply detects the GPU, display path, refresh range, and laptop/desktop policy automatically.";
        UseGsync = state.Extra is { Count: > 0 } &&
            state.Extra.TryGetValue("gsync", out var gsync) &&
            string.Equals(gsync, "true", StringComparison.OrdinalIgnoreCase);

        Features.Clear();
        foreach (var feature in state.Features)
        {
            Features.Add(new FeatureRowViewModel
            {
                Title = feature.Title,
                Detail = feature.Detail,
                Glyph = Helpers.UiStatusPresentation.FeatureGlyph(feature.IsActive),
                Opacity = Helpers.UiStatusPresentation.FeatureOpacity(feature.IsActive),
                IsActive = feature.IsActive,
                RailOpacity = Helpers.UiStatusPresentation.FeatureRailOpacity(feature.IsActive)
            });
        }
        RunButtonLabel = state.IsApplied ? "Reapply" : "Apply";
        IsFeatureListVisible = Features.Count > 0;
        GuidanceText = string.Empty;
        HasGuidance = false;
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

    public Task InitializeAsync() => RefreshCoreAsync(force: false);
}
