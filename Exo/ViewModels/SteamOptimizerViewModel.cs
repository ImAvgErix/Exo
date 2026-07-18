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
            var state = await _services.OptimizerState.DetectSteamAsync();
            ApplyState(state);
        }
        catch (Exception)
        {
            StatusText = "Unavailable";
            DetailText = string.Empty;
            Features.Clear();
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

            var result = await _services.PowerShell.RunAsync(
                _services.Scripts.SteamOptimizerScript,
                arguments: new[] { "-NonInteractive" },
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
        var failSteps = ApplyReportRows
            .Where(r => r.Status == "fail")
            .Select(r => r.Text.Split('·')[0].Trim())
            .Where(s => s.Length > 0)
            .Take(4)
            .ToList();
        GuidanceText = OptimizerAdvisor.BuildV2(
            "Steam",
            IsApplied,
            StatusText,
            DetailText,
            Features.Select(f => (f.Title, f.IsActive, f.Detail)).ToList(),
            failSteps);
        HasGuidance = !string.IsNullOrWhiteSpace(GuidanceText);
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

    public async Task InitializeAsync()
    {
        // Full live detect only — no fast heuristic flash of "Already optimized".
        if (IsBusy) return;
        await RefreshAsync();
    }
}
