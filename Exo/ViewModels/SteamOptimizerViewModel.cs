using System.Collections.ObjectModel;
using System.Text.Json;
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

    [ObservableProperty] private string _statusText = "Checking status...";
    [ObservableProperty] private string _detailText = string.Empty;
    [ObservableProperty] private string _guidanceText = "Detecting this PC...";
    [ObservableProperty] private bool _hasGuidance = true;
    public ObservableCollection<FeatureRowViewModel> Features { get; } = new();

    [ObservableProperty] private string _runButtonLabel = "Run";
    [ObservableProperty] private bool _isApplied;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isStatusLoading = true;
    [ObservableProperty] private bool _isFeatureListVisible;
    [ObservableProperty] private bool _isProgressVisible;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressStatus = string.Empty;
    [ObservableProperty] private string _lastResult = string.Empty;
    [ObservableProperty] private bool _hasLastResult;
    [ObservableProperty] private string _lastResultGlyph = "\uE73E";
    [ObservableProperty] private Brush _lastResultBrush;

    // RAM reclaimed by the resident trim loop (steam-trim-stats.json — may not exist).
    [ObservableProperty] private bool _hasTrimStats;
    [ObservableProperty] private string _trimStatsText = string.Empty;

    // Compact expandable "Last apply" report (state-file applyReport array).
    public ObservableCollection<ApplyReportRowViewModel> ApplyReportRows { get; } = new();
    [ObservableProperty] private bool _hasApplyReport;
    [ObservableProperty] private bool _isApplyReportOpen;
    [ObservableProperty] private string _applyReportSummary = "Last apply";

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
                workingDirectory: _services.Scripts.GetSteamRoot());

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
                workingDirectory: _services.Scripts.GetSteamRoot());

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
        StatusText = state.StatusText;
        DetailText = string.Empty;
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
        if (!IsStatusLoading)
            IsFeatureListVisible = Features.Count > 0;
        LoadApplyReport();
        LoadTrimStats();
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

    /// <summary>Read %LocalAppData%\Exo\steam-trim-stats.json defensively (may not exist).</summary>
    private void LoadTrimStats()
    {
        try
        {
            var path = Path.Combine(PathHelper.AppDataDir, "steam-trim-stats.json");
            if (!File.Exists(path))
            {
                HasTrimStats = false;
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var total = ReadInt64(root, "totalReclaimedBytes");
            var last24h = ReadInt64(root, "last24hReclaimedBytes");
            if (total <= 0 && last24h <= 0)
            {
                HasTrimStats = false;
                return;
            }

            TrimStatsText = $"RAM reclaimed · {FormatBytes(last24h)} last 24 h · {FormatBytes(total)} total";
            HasTrimStats = true;
        }
        catch
        {
            HasTrimStats = false;
        }
    }

    private static long ReadInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) &&
        el.ValueKind == JsonValueKind.Number &&
        el.TryGetInt64(out var value)
            ? value
            : 0;

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30)
            return $"{bytes / (double)(1L << 30):0.0} GB";
        return $"{Math.Max(0, bytes) / (double)(1L << 20):0} MB";
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
