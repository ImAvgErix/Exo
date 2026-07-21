using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Exo.Helpers;
using Exo.Models;
using Exo.Services;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Exo.ViewModels;

public partial class GameLauncherOptimizerViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly string _module;
    private CancellationTokenSource? _runCts;

    public GameLauncherOptimizerViewModel(AppServices services, string module)
    {
        _services = services;
        _module = module is "Riot" or "Epic" ? module : throw new ArgumentOutOfRangeException(nameof(module));
        LastResultBrush = ResolveBrush("ExoSuccessBrush", Color.FromArgb(255, 34, 197, 94));
        try
        {
            PreferExperimental = _module == "Riot"
                ? services.Settings.Current.ExperimentalRiot
                : services.Settings.Current.ExperimentalEpic;
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
        try
        {
            if (_module == "Riot")
                _services.Settings.Update(s => s.ExperimentalRiot = value);
            else
                _services.Settings.Update(s => s.ExperimentalEpic = value);
        }
        catch { }
    }

    [ObservableProperty] public partial string StatusText { get; set; } = "Checking status...";
    [ObservableProperty] public partial string GuidanceText { get; set; } = "Detecting this PC...";
    [ObservableProperty] public partial bool HasGuidance { get; set; } = true;
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
    [ObservableProperty] public partial string RunButtonLabel { get; set; } = "Apply";
    [ObservableProperty] public partial string LastResult { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasLastResult { get; set; }
    [ObservableProperty] public partial string LastResultGlyph { get; set; } = "\uE73E";
    [ObservableProperty] public partial Brush LastResultBrush { get; set; }
    [ObservableProperty] public partial bool HasApplyReport { get; set; }
    [ObservableProperty] public partial bool IsApplyReportOpen { get; set; }
    [ObservableProperty] public partial string ApplyReportSummary { get; set; } = "Last apply";

    public ObservableCollection<FeatureRowViewModel> Features { get; } = new();
    public ObservableCollection<ApplyReportRowViewModel> ApplyReportRows { get; } = new();
    public string ApplyReportChevron => IsApplyReportOpen ? "\uE70E" : "\uE70D";

    partial void OnIsApplyReportOpenChanged(bool value) => OnPropertyChanged(nameof(ApplyReportChevron));
    [RelayCommand] private void ToggleApplyReport() => IsApplyReportOpen = !IsApplyReportOpen;

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
            ApplyState(await DetectAsync().WaitAsync(ct).ConfigureAwait(true));
            if (gen != _detectGen || ct.IsCancellationRequested) return;
            _lastFullDetectUtc = DateTimeOffset.UtcNow;
            if (HasLastResult && string.Equals(LastResult, OptimizerMessages.StatusFailed, StringComparison.Ordinal))
                SetResult(string.Empty, true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (gen == _detectGen && Features.Count == 0)
            {
                StatusText = "Unavailable";
                Features.Clear();
                SetResult(string.IsNullOrWhiteSpace(ex.Message) ? OptimizerMessages.StatusFailed : ex.Message, false);
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
    private async Task RunAsync() => await ExecuteAsync(repair: false);

    [RelayCommand]
    private async Task RepairAsync() => await ExecuteAsync(repair: true);

    private async Task ExecuteAsync(bool repair)
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressVisible = true;
        ProgressPercent = 0;
        ProgressStatus = repair ? "Restoring..." : "Applying...";
        SetResult(string.Empty, true);
        _runCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<ScriptRunProgress>(p =>
            {
                if (p.Percent >= ProgressPercent) ProgressPercent = p.Percent;
                if (!string.IsNullOrWhiteSpace(p.Status)) ProgressStatus = p.Status;
            });
            var script = (_module, repair) switch
            {
                ("Riot", false) => _services.Scripts.RiotApplyScript,
                ("Riot", true) => _services.Scripts.RiotRepairScript,
                ("Epic", false) => _services.Scripts.EpicApplyScript,
                _ => _services.Scripts.EpicRepairScript
            };
            var args = new List<string> { "-NonInteractive" };
            if (!repair) args.Add("-Experimental");
            var result = await _services.PowerShell.RunAsync(
                script, args, elevate: true, progress,
                _runCts.Token, _services.Scripts.GetGameLaunchersRoot(), ensureRuntime: true);
            if (result.Success)
            {
                ProgressPercent = 100;
                ProgressStatus = repair ? "Repair complete" : "Verified";
                SetResult(repair ? OptimizerMessages.RepairFinished : OptimizerMessages.Done, true);
            }
            else
            {
                ProgressStatus = result.ExitCode == -2 ? "Cancelled" : "Failed";
                SetResult(result.ErrorMessage ?? result.Summary ?? "Failed.", false);
            }
            ApplyState(await DetectAsync());
        }
        catch (OperationCanceledException)
        {
            ProgressStatus = "Cancelled";
            SetResult(OptimizerMessages.Cancelled, false);
        }
        catch (Exception ex)
        {
            ProgressStatus = "Failed";
            SetResult(ex.Message, false);
        }
        finally
        {
            IsBusy = false;
            IsProgressVisible = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private Task<OptimizerStateInfo> DetectAsync() =>
        _module == "Riot" ? _services.OptimizerState.DetectRiotAsync() : _services.OptimizerState.DetectEpicAsync();

    private void ApplyState(OptimizerStateInfo state)
    {
        Features.Clear();
        foreach (var feature in state.Features)
        {
            Features.Add(new FeatureRowViewModel
            {
                Title = feature.Title,
                Detail = feature.Detail,
                Glyph = UiStatusPresentation.FeatureGlyph(feature.IsActive),
                Opacity = UiStatusPresentation.FeatureOpacity(feature.IsActive),
                IsActive = feature.IsActive,
                RailOpacity = UiStatusPresentation.FeatureRailOpacity(feature.IsActive)
            });
        }

        // Honesty + Discord-grade status: never claim optimized when install/games rows are open.
        var installMissing = Features.Any(f =>
            f.Title.Contains("install", StringComparison.OrdinalIgnoreCase) && !f.IsActive);
        var statusLooksMissing = (state.StatusText ?? string.Empty)
            .Contains("not installed", StringComparison.OrdinalIgnoreCase);

        if (installMissing || statusLooksMissing)
        {
            IsApplied = false;
            StatusText = "Not installed";
            RunButtonLabel = "Apply";
        }
        else
        {
            IsApplied = state.IsApplied;
            var raw = (state.StatusText ?? string.Empty).Trim();
            var missing = Features.Count(f => !f.IsActive && !string.IsNullOrWhiteSpace(f.Title));
            if (!state.IsApplied
                && missing > 0
                && missing <= 3
                && (string.Equals(raw, "Not applied", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(raw, "Ready to optimize", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(raw)))
            {
                StatusText = missing == 1
                    ? "1 setting needs Apply"
                    : $"{missing} settings need Apply";
            }
            else
            {
                StatusText = string.IsNullOrWhiteSpace(raw) ? "Ready" : raw;
            }
            RunButtonLabel = state.IsApplied ? "Reapply" : "Apply";
        }

        LoadApplyReport();
        GuidanceText = string.Empty;
        HasGuidance = false;
        if (!IsStatusLoading) IsFeatureListVisible = Features.Count > 0;
    }

    private void LoadApplyReport()
    {
        var rows = ApplyReportPresentation.FromEntries(
            OptimizerStateService.TryReadApplyReport(_module.ToLowerInvariant()));
        ApplyReportRows.Clear();
        foreach (var row in rows) ApplyReportRows.Add(row);
        HasApplyReport = rows.Count > 0;
        ApplyReportSummary = ApplyReportPresentation.Summarize(rows);
    }

    private void SetResult(string message, bool success)
    {
        LastResult = message;
        HasLastResult = !string.IsNullOrWhiteSpace(message);
        var banner = UiStatusPresentation.BannerForSuccess(success);
        LastResultGlyph = banner.Glyph;
        LastResultBrush = ResolveBrush(banner.BrushKey,
            success ? Color.FromArgb(255, 34, 197, 94) : Color.FromArgb(255, 220, 38, 38));
    }

    private static Brush ResolveBrush(string key, Color fallback)
    {
        try
        {
            if (Microsoft.UI.Xaml.Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Brush brush)
                return brush;
        }
        catch { }
        return new SolidColorBrush(fallback);
    }

    public Task InitializeAsync() => RefreshCoreAsync(force: false);
}
