using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Exo.Helpers;
using Exo.Models;
using Exo.Services;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Exo.ViewModels;

public partial class SteamOptimizerViewModel : ObservableObject
{
    private readonly AppServices _services;
    private CancellationTokenSource? _runCts;

    public SteamOptimizerViewModel(AppServices services)
    {
        _services = services;
        LastResultBrush = ResolveBrush("ExoSuccessBrush", Color.FromArgb(255, 34, 197, 94));
        try
        {
            PreferExperimental = services.Settings.Current.ExperimentalSteam;
            SelectedApplyMode = PreferExperimental ? "Experimental" : "Stable";
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
        try { _services.Settings.Update(s => s.ExperimentalSteam = value); } catch { }
    }

    [ObservableProperty] public partial string StatusText { get; set; } = "Checking status...";
    [ObservableProperty] public partial string DetailText { get; set; } = string.Empty;
    [ObservableProperty] public partial string GuidanceText { get; set; } = "Detecting this PC...";
    [ObservableProperty] public partial bool HasGuidance { get; set; } = true;
    public ObservableCollection<FeatureRowViewModel> Features { get; } = new();

    [ObservableProperty] public partial string RunButtonLabel { get; set; } = "Run";
    [ObservableProperty] public partial bool IsApplied { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsStatusLoading { get; set; } = true;
    [ObservableProperty] public partial bool PreferExperimental { get; set; }
    public IReadOnlyList<string> ApplyModeOptions { get; } = new[] { "Stable", "Experimental" };
    [ObservableProperty] public partial string SelectedApplyMode { get; set; } = "Stable";
    [ObservableProperty] public partial bool IsFeatureListVisible { get; set; }
    [ObservableProperty] public partial bool IsProgressVisible { get; set; }
    [ObservableProperty] public partial double ProgressPercent { get; set; }
    [ObservableProperty] public partial string ProgressStatus { get; set; } = string.Empty;
    [ObservableProperty] public partial string LastResult { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasLastResult { get; set; }
    [ObservableProperty] public partial string LastResultGlyph { get; set; } = "\uE73E";
    [ObservableProperty] public partial Brush LastResultBrush { get; set; }

    // Compact expandable "Last apply" report (state-file applyReport array).
    public ObservableCollection<ApplyReportRowViewModel> ApplyReportRows { get; } = new();
    [ObservableProperty] public partial bool HasApplyReport { get; set; }
    [ObservableProperty] public partial bool IsApplyReportOpen { get; set; }
    [ObservableProperty] public partial string ApplyReportSummary { get; set; } = "Last apply";

    public string ApplyReportChevron => IsApplyReportOpen ? "\uE70E" : "\uE70D";

    partial void OnIsApplyReportOpenChanged(bool value) =>
        OnPropertyChanged(nameof(ApplyReportChevron));

    [RelayCommand]
    private void ToggleApplyReport() => IsApplyReportOpen = !IsApplyReportOpen;

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
            var state = await _services.OptimizerState.DetectSteamAsync(ct, fastOnly: false).ConfigureAwait(true);
            if (gen != _detectGen || ct.IsCancellationRequested) return;
            ApplyState(state);
            _lastFullDetectUtc = DateTimeOffset.UtcNow;
            if (HasLastResult && string.Equals(LastResult, Helpers.OptimizerMessages.StatusFailed, StringComparison.Ordinal))
                SetResult(string.Empty, success: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (gen == _detectGen && Features.Count == 0)
            {
                StatusText = "Unavailable";
                DetailText = string.Empty;
                SetResult(string.IsNullOrWhiteSpace(ex.Message) ? Helpers.OptimizerMessages.StatusFailed : ex.Message, success: false);
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

            var args = new List<string> { "-NonInteractive" };
            args.Add("-Experimental");
            var result = await _services.PowerShell.RunAsync(
                _services.Scripts.SteamOptimizerScript,
                arguments: args,
                elevate: true,
                progress: progress,
                cancellationToken: _runCts.Token,
                workingDirectory: _services.Scripts.GetSteamRoot(),
                ensureRuntime: true);

            if (result.Success)
            {
                ProgressPercent = 100;
                ProgressStatus = "Done";
                SetResult(Helpers.OptimizerMessages.Done, success: true);
            }
            else
            {
                ProgressStatus = result.ExitCode == -2 ? "Cancelled" : "Failed";
                var err = result.ErrorMessage ?? result.Summary ?? "Failed.";
                if (err.Length > 200) err = err[..200] + "…";
                SetResult(err, success: false);
            }

            await RefreshAfterRunAsync();
        }
        catch (OperationCanceledException)
        {
            ProgressStatus = "Cancelled";
            SetResult(Helpers.OptimizerMessages.Cancelled, success: false);
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

        // Quiet secondary action — runs immediately. Restores Exo backups; games stay.
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
                _services.Scripts.SteamRepairScript,
                arguments: new[] { "-NonInteractive" },
                elevate: true,
                progress: progress,
                cancellationToken: _runCts.Token,
                workingDirectory: _services.Scripts.GetSteamRoot(),
                ensureRuntime: true);

            SetResult(
                result.Success ? Helpers.OptimizerMessages.RepairFinished : (result.ErrorMessage ?? result.Summary),
                success: result.Success);
            ProgressStatus = result.Success ? "Repair complete" : (result.ExitCode == -2 ? "Cancelled" : "Repair failed");
            if (result.Success)
                ProgressPercent = 100;

            await RefreshAfterRunAsync();
        }
        catch (OperationCanceledException)
        {
            ProgressStatus = "Cancelled";
            SetResult(Helpers.OptimizerMessages.Cancelled, success: false);
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
            ApplyState(await _services.OptimizerState.DetectSteamAsync(fastOnly: false));
        }
        catch { /* ignore */ }
    }

    private void ApplyState(OptimizerStateInfo state)
    {
        IsApplied = state.IsApplied;
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

        // Soften generic heuristic status when only a few rows are open.
        // Script detect already emits "1 setting needs Apply (...)" — keep that.
        var missing = Features.Count(f => !f.IsActive && !string.IsNullOrWhiteSpace(f.Title));
        var raw = (state.StatusText ?? string.Empty).Trim();
        if (!state.IsApplied
            && missing > 0
            && missing <= 3
            && (string.Equals(raw, "Not applied", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "Ready to optimize", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(raw)))
        {
            StatusText = missing == 1
                ? "1 setting needs Apply"
                : $"{missing} launcher settings need Apply";
        }
        else
        {
            StatusText = string.IsNullOrWhiteSpace(raw) ? "Ready" : raw;
        }

        DetailText = string.Empty;
        RunButtonLabel = state.IsApplied ? "Reapply" : "Apply";
        if (!IsStatusLoading)
            IsFeatureListVisible = Features.Count > 0;
        LoadApplyReport();
        GuidanceText = string.Empty;
        HasGuidance = false;
    }

    private void LoadApplyReport()
    {
        var rows = ApplyReportPresentation.FromEntries(
            OptimizerStateService.TryReadApplyReport("steam"));
        ApplyReportRows.Clear();
        foreach (var row in rows)
            ApplyReportRows.Add(row);
        HasApplyReport = ApplyReportRows.Count > 0;
        ApplyReportSummary = ApplyReportPresentation.Summarize(rows);
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
