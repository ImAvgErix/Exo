using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Exo.Helpers;
using Exo.Models;
using Exo.Services;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Exo.ViewModels;

public partial class InternetOptimizerViewModel : ObservableObject
{
    private readonly AppServices _services;
    private NetworkSnapshot? _lastSnap;

    public InternetOptimizerViewModel(AppServices services)
    {
        _services = services;
        var banner = UiStatusPresentation.BannerForSuccess(true);
        MessageGlyph = banner.Glyph;
        MessageBrush = ResolveBrush(banner.BrushKey, Color.FromArgb(255, 34, 197, 94));
    }

    public ObservableCollection<FeatureRowViewModel> Rows { get; } = new();

    [ObservableProperty] public partial string HeaderStatus { get; set; } = "Checking...";
    [ObservableProperty] public partial string GuidanceText { get; set; } = "Detecting this PC...";
    [ObservableProperty] public partial bool HasGuidance { get; set; } = true;
    [ObservableProperty] public partial bool IsLoading { get; set; } = true;
    [ObservableProperty] public partial bool IsFeatureListVisible { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string ProgressStatus { get; set; } = string.Empty;
    [ObservableProperty] public partial string Message { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasMessage { get; set; }
    [ObservableProperty] public partial string MessageGlyph { get; set; } = "\uE73E";
    [ObservableProperty] public partial Brush MessageBrush { get; set; }

    // Proof layer — persisted benchmark delta, honest rollback marker, restore capability.
    [ObservableProperty] public partial bool HasBenchmark { get; set; }
    [ObservableProperty] public partial string BenchmarkSummary { get; set; } = string.Empty;
    [ObservableProperty] public partial Brush BenchmarkBrush { get; set; } = new SolidColorBrush(Color.FromArgb(255, 161, 161, 170));
    [ObservableProperty] public partial bool HasRollback { get; set; }
    [ObservableProperty] public partial string RollbackNotice { get; set; } = string.Empty;
    [ObservableProperty] public partial string RepairHint { get; set; } = "Repair: reset to stock defaults";

    // Compact expandable "Last apply" report (EXO_REPORT structured steps).
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
    public Task RefreshAsync() => LoadSnapshotAsync(showFullPageLoading: !IsBusy);

    /// <summary>
    /// Probe and update header + feature rows.
    /// Does not early-return on IsBusy so apply/repair can refresh in place.
    /// </summary>
    private async Task LoadSnapshotAsync(bool showFullPageLoading)
    {
        if (showFullPageLoading)
        {
            IsLoading = true;
            IsFeatureListVisible = false;
        }
        try
        {
            var snap = await _services.Network.ProbeAsync();
            ApplySnapshotToUi(snap, preserveSuccessMessage: false);
        }
        catch (Exception ex)
        {
            HeaderStatus = "Unavailable";
            SetMessage(ex.Message, success: false);
        }
        finally
        {
            if (showFullPageLoading)
                IsLoading = false;
            IsFeatureListVisible = Rows.Count > 0;
        }
        await LoadProofLayerAsync();
    }

    /// <summary>
    /// Persisted proof layer: before/after benchmark, honest rollback marker,
    /// last-apply step report and restore capability. All reads are defensive.
    /// </summary>
    private async Task LoadProofLayerAsync()
    {
        try
        {
            var (report, bench, rollback, hasSnapshot) = await Task.Run(() =>
            {
                var r = _services.Network.LoadLastApplyReport();
                var b = _services.Network.LoadBenchmark();
                var rb = _services.Network.LoadRollbackStatus();
                var hs = NetworkOptimizerService.HasRestoreSnapshot();
                return (r, b, rb, hs);
            });

            RepairHint = hasSnapshot
                ? "Repair: restore exact pre-Exo state"
                : "Repair: reset to stock defaults";

            HasRollback = rollback?.RolledBack == true;
            RollbackNotice = HasRollback
                ? "Apply rolled back: " + (string.IsNullOrWhiteSpace(rollback!.Reason)
                    ? "connectivity check failed"
                    : rollback.Reason)
                : string.Empty;

            if (bench.Before is { Ok: true } before && bench.After is { Ok: true } after)
            {
                var improved = after.PingP50Ms < before.PingP50Ms;
                BenchmarkSummary =
                    $"Ping p50 {FormatMs(before.PingP50Ms)} → {FormatMs(after.PingP50Ms)} ms" +
                    $" · jitter {FormatMs(before.JitterMs)} → {FormatMs(after.JitterMs)} ms" +
                    $" · DNS {FormatDns(before.DnsMs)} → {FormatDns(after.DnsMs)}";
                BenchmarkBrush = ResolveBrush(
                    improved ? "ExoSuccessBrush" : "ExoMutedTextBrush",
                    improved ? Color.FromArgb(255, 34, 197, 94) : Color.FromArgb(255, 161, 161, 170));
                HasBenchmark = true;
            }
            else
            {
                HasBenchmark = false;
                BenchmarkSummary = string.Empty;
            }

            ApplyReportRows.Clear();
            foreach (var step in report)
                ApplyReportRows.Add(ApplyReportPresentation.Row(step.Name, step.Status, step.Reason));
            HasApplyReport = ApplyReportRows.Count > 0;
            ApplyReportSummary = ApplyReportPresentation.Summarize(ApplyReportRows.ToList());
        }
        catch
        {
            // Proof layer is additive — never break the page over it.
        }
    }

    private static string FormatMs(double value) =>
        value < 0 ? "—" : value.ToString(value >= 100 ? "0" : "0.0");

    private static string FormatDns(double value) =>
        value < 0 ? "fail" : value.ToString(value >= 100 ? "0" : "0.0") + " ms";

    private void ApplySnapshotToUi(NetworkSnapshot snap, bool preserveSuccessMessage)
    {
        _lastSnap = snap;
        HeaderStatus = BuildStatus(snap);

        Rows.Clear();
        foreach (var f in snap.Features)
        {
            Rows.Add(new FeatureRowViewModel
            {
                Title = f.Title,
                Detail = f.Status,
                Glyph = UiStatusPresentation.FeatureGlyph(f.IsOk),
                Opacity = UiStatusPresentation.FeatureOpacity(f.IsOk),
                IsActive = f.IsOk,
                RailOpacity = UiStatusPresentation.FeatureRailOpacity(f.IsOk)
            });
        }

        if (!IsLoading)
            IsFeatureListVisible = Rows.Count > 0;

        // Path (Ethernet vs Wi‑Fi) lives in the header only — no redundant banner on open.
        if (!string.IsNullOrWhiteSpace(snap.Detail) && !snap.ProbeOk)
            SetMessage(snap.Detail, success: false);
        else if (!preserveSuccessMessage)
            ClearMessage();

        var applied = Rows.Count > 0 && Rows.All(r => r.IsActive);
        var failSteps = ApplyReportRows
            .Where(r => r.Status == "fail")
            .Select(r => r.Text.Split('·')[0].Trim())
            .Where(s => s.Length > 0)
            .Take(4)
            .ToList();
        GuidanceText = OptimizerAdvisor.BuildV2(
            "Internet",
            applied,
            HeaderStatus,
            snap.Detail,
            Rows.Select(r => (r.Title, r.IsActive, r.Detail)).ToList(),
            failSteps);
        HasGuidance = !string.IsNullOrWhiteSpace(GuidanceText);
    }

    [RelayCommand]
    private Task ApplyLatencyAsync() => ApplyPresetAsync(NetworkPreset.LowestLatency);

    [RelayCommand]
    private Task ApplyThroughputAsync() => ApplyPresetAsync(NetworkPreset.HighestThroughput);

    /// <summary>Apply chosen stack immediately, then refresh header + feature rows in place.</summary>
    private async Task ApplyPresetAsync(NetworkPreset preset)
    {
        if (IsBusy) return;

        IsBusy = true;
        HasMessage = false;
        ProgressStatus = "Detecting path...";
        try
        {
            var snap = await _services.Network.ProbeAsync();
            _lastSnap = snap;
            HeaderStatus = BuildStatus(snap);

            // Fail-closed defaults: never disable Wi-Fi, never restart NICs.
            // Ethernet-first is metrics-only (see NetworkApplyScriptBuilder).
            var options = new NetworkApplyOptions
            {
                PreferEthernetDisableWifi = false,
                RestartEthernet = false
            };

            ProgressStatus = snap.Media.EthernetInUse
                ? "Applying Ethernet stack..."
                : snap.Media.WifiUp
                    ? "Applying Wi‑Fi stack..."
                    : "Applying...";
            var progress = new Progress<string>(s => ProgressStatus = s);
            var (ok, msg) = await _services.Network.ApplyPresetAsync(preset, options, progress);
            // Same finish banner as Discord / Steam / NVIDIA.
            SetMessage(ok ? Helpers.OptimizerMessages.Done : msg, ok);

            // In-place refresh so header shows Lowest latency / Highest download without leaving the page.
            ProgressStatus = "Refreshing...";
            try
            {
                var after = await _services.Network.ProbeAsync();
                ApplySnapshotToUi(after, preserveSuccessMessage: true);
                SetMessage(ok ? Helpers.OptimizerMessages.Done : msg, ok);
            }
            catch
            {
                // Keep apply message even if re-probe fails
            }
            await LoadProofLayerAsync();
        }
        catch (Exception ex)
        {
            SetMessage(ex.Message, success: false);
        }
        finally
        {
            IsBusy = false;
            ProgressStatus = string.Empty;
        }
    }

    public Task InitializeAsync() => LoadSnapshotAsync(showFullPageLoading: true);

    [RelayCommand]
    private async Task RepairAsync()
    {
        if (IsBusy) return;

        // Quiet secondary action — runs immediately. The RepairHint caption states
        // whether this restores the exact pre-Exo snapshot or resets to stock defaults.
        IsBusy = true;
        HasMessage = false;
        ProgressStatus = "Repairing...";
        try
        {
            var progress = new Progress<string>(s => ProgressStatus = s);
            var (success, msg) = await _services.Network.RepairAsync(progress);
            SetMessage(success ? Helpers.OptimizerMessages.RepairFinished : msg, success);
            ProgressStatus = "Refreshing...";
            try
            {
                var after = await _services.Network.ProbeAsync();
                ApplySnapshotToUi(after, preserveSuccessMessage: true);
                SetMessage(success ? Helpers.OptimizerMessages.RepairFinished : msg, success);
            }
            catch { }
            await LoadProofLayerAsync();
        }
        catch (Exception ex)
        {
            SetMessage(ex.Message, success: false);
        }
        finally
        {
            IsBusy = false;
            ProgressStatus = string.Empty;
        }
    }

    private static string BuildStatus(NetworkSnapshot snap)
    {
        if (!snap.ProbeOk) return "Probe incomplete";

        var media = snap.Media.EthernetInUse
            ? "Ethernet path"
            : snap.Media.EthernetUp
                ? "Ethernet (no IP yet)"
                : snap.Media.WifiUp
                    ? $"Wi‑Fi · {snap.Media.PreferredBandTarget}"
                    : snap.ConnectionType;

        var preset = snap.ActivePreset switch
        {
            NetworkPreset.LowestLatency => "Lowest latency",
            NetworkPreset.HighestThroughput => "Highest download",
            _ => null
        };

        if (preset is null)
            return $"{media} · {snap.LinkSpeed}";

        var allOk = snap.Features.Count > 0 && snap.Features.All(f => f.IsOk);
        return allOk ? $"{preset} · {media}" : $"{preset} · check rows";
    }

    private void ClearMessage()
    {
        Message = string.Empty;
        HasMessage = false;
    }

    private void SetMessage(string text, bool success)
    {
        Message = text;
        HasMessage = !string.IsNullOrWhiteSpace(text);
        var banner = UiStatusPresentation.BannerForSuccess(success);
        MessageGlyph = banner.Glyph;
        MessageBrush = ResolveBrush(
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
}
