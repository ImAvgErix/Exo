using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Exo.Models;
using Exo.Services;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Exo.ViewModels;

public partial class DiscordOptimizerViewModel : ObservableObject
{
    private readonly AppServices _services;
    private CancellationTokenSource? _runCts;

    public DiscordOptimizerViewModel(AppServices services)
    {
        _services = services;
        LastResultBrush = ResolveBrush("ExoSuccessBrush", Color.FromArgb(255, 34, 197, 94));
        try
        {
            PreferExperimental = services.Settings.Current.ExperimentalDiscord;
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
        try { _services.Settings.Update(s => s.ExperimentalDiscord = value); } catch { }
    }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Checking status...";

    [ObservableProperty]
    public partial string DetailText { get; set; } = string.Empty;

    /// <summary>Live next-step coach from detect (what to click / what's missing).</summary>
    [ObservableProperty]
    public partial string GuidanceText { get; set; } = "Detecting this PC...";

    [ObservableProperty]
    public partial bool HasGuidance { get; set; } = true;

    public ObservableCollection<FeatureRowViewModel> Features { get; } = new();

    [ObservableProperty]
    public partial string RunButtonLabel { get; set; } = "Run";

    [ObservableProperty]
    public partial bool IsApplied { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsStatusLoading { get; set; } = true;

    /// <summary>When true, Apply passes -Experimental for a more aggressive pass.</summary>
    [ObservableProperty]
    public partial bool PreferExperimental { get; set; }

    public IReadOnlyList<string> ApplyModeOptions { get; } = new[] { "Stable", "Experimental" };

    [ObservableProperty]
    public partial string SelectedApplyMode { get; set; } = "Stable";

    [ObservableProperty]
    public partial bool IsFeatureListVisible { get; set; }

    [ObservableProperty]
    public partial bool IsProgressVisible { get; set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string ProgressStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LastResult { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasLastResult { get; set; }

    [ObservableProperty]
    public partial string LastResultGlyph { get; set; } = "\uE73E";

    [ObservableProperty]
    public partial Brush LastResultBrush { get; set; }

    // Compact expandable "Last apply" report (state-file applyReport array).
    public ObservableCollection<ApplyReportRowViewModel> ApplyReportRows { get; } = new();

    [ObservableProperty]
    public partial bool HasApplyReport { get; set; }

    [ObservableProperty]
    public partial bool IsApplyReportOpen { get; set; }

    [ObservableProperty]
    public partial string ApplyReportSummary { get; set; } = "Last apply";

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

    /// <summary>Leave-page cancel so rapid module switches do not stack PowerShell detects.</summary>
    public void CancelBackgroundWork()
    {
        try { _detectCts?.Cancel(); } catch { }
        Interlocked.Increment(ref _detectGen);
    }

    /// <summary>
    /// Detect never uses IsBusy (so module switches stay snappy). Cold open: loader only
    /// until full check finishes. Warm re-entry within TTL: show cached UI immediately.
    /// </summary>
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

        var cold = Features.Count == 0;
        if (cold)
        {
            IsStatusLoading = true;
            IsFeatureListVisible = false;
            StatusText = "Checking status...";
        }

        try
        {
            var state = await _services.OptimizerState.DetectDiscordAsync(ct, fastOnly: false)
                .ConfigureAwait(true);
            if (gen != _detectGen || ct.IsCancellationRequested) return;
            ApplyState(state);
            _lastFullDetectUtc = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) { /* left page */ }
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
        ProgressStatus = "Preparing...";
        SetResult(string.Empty, success: true);
        _runCts = new CancellationTokenSource();

        try
        {
            var args = new List<string> { "-NonInteractive" };
            // Always competitive max-aggression (UI no longer offers Stable/Experimental).
            args.Add("-Experimental");

            var progress = new Progress<ScriptRunProgress>(p =>
            {
                // Never let the bar jump backwards during a live run
                if (p.Percent >= ProgressPercent)
                    ProgressPercent = p.Percent;
                if (!string.IsNullOrWhiteSpace(p.Status))
                    ProgressStatus = p.Status;
            });

            var script = _services.Scripts.DiscordApplyScript;
            var result = await _services.PowerShell.RunAsync(
                script,
                arguments: args,
                elevate: true,
                progress: progress,
                cancellationToken: _runCts.Token,
                workingDirectory: _services.Scripts.GetDiscordRoot(),
                ensureRuntime: true);

            if (result.Success)
            {
                ProgressPercent = 100;
                ProgressStatus = "Completed successfully";
                SetResult(Helpers.OptimizerMessages.Done, success: true);
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

        // Quiet secondary action — runs immediately. Stock Discord, login kept.
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
                workingDirectory: _services.Scripts.GetDiscordRoot(),
                ensureRuntime: true);

            SetResult(
                result.Success
                    ? Helpers.OptimizerMessages.RepairFinished
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
            var state = await _services.OptimizerState.DetectDiscordAsync(fastOnly: false);
            ApplyState(state);
        }
        catch { /* ignore */ }
    }

    private void ApplyState(OptimizerStateInfo state)
    {
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

        // Honesty: never show "Already optimized" when Discord is not present.
        // CUA GUI QA (docs/cua-qa) caught Status=Already optimized + banner "not installed".
        var installMissing = Features.Any(f =>
            f.Title.Contains("install", StringComparison.OrdinalIgnoreCase) && !f.IsActive);
        var statusLooksMissing = (state.StatusText ?? string.Empty)
            .Contains("not installed", StringComparison.OrdinalIgnoreCase);

        if (installMissing || statusLooksMissing)
        {
            IsApplied = false;
            StatusText = "Not installed";
            DetailText = string.Empty;
            RunButtonLabel = "Apply";
        }
        else
        {
            IsApplied = state.IsApplied;
            StatusText = state.StatusText ?? "Ready";
            DetailText = string.Empty;
            RunButtonLabel = state.IsApplied ? "Reapply" : "Apply";
        }

        if (!IsStatusLoading)
            IsFeatureListVisible = Features.Count > 0;
        LoadApplyReport();
        UpdateGuidance();
    }

    private void UpdateGuidance()
    {
        // Advisor tidbits removed from module UI — keep properties empty for binding safety.
        GuidanceText = string.Empty;
        HasGuidance = false;
    }

    private void LoadApplyReport()
    {
        var rows = ApplyReportPresentation.FromEntries(
            OptimizerStateService.TryReadApplyReport("discord"));
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
        catch { /* theme resource may be unavailable early */ }
        return new SolidColorBrush(fallback);
    }

    public Task InitializeAsync() => RefreshCoreAsync(force: false);
}

public sealed class FeatureRowViewModel
{
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string Glyph { get; init; } = "\uE73E";
    public double Opacity { get; init; } = 1.0;
    /// <summary>Live applied bit — drives the AMOLED status rail on feature tiles.</summary>
    public bool IsActive { get; init; }
    public double RailOpacity { get; init; } = 0.28;
}
