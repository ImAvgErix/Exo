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
    private NetworkBenchmarkResult? _lastQuality;
    private IReadOnlyList<NetworkApplyReportStep> _lastApplyReport = Array.Empty<NetworkApplyReportStep>();

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
    [ObservableProperty] public partial string RepairHint { get; set; } =
        "Repair always available - restores the pre-Exo snapshot. Wi-Fi is never permanently disabled.";
    [ObservableProperty] public partial bool HasQualityResult { get; set; }
    [ObservableProperty] public partial string QualitySummary { get; set; } = string.Empty;

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
    /// Persisted verification layer: latest benchmark, honest rollback marker,
    /// last-apply step report and restore capability. All reads are defensive.
    /// </summary>
    private async Task LoadProofLayerAsync()
    {
        try
        {
            var (report, bench, rollback, hasSnapshot, quality) = await Task.Run(() =>
            {
                var r = _services.Network.LoadLastApplyReport();
                var b = _services.Network.LoadBenchmark();
                var rb = _services.Network.LoadRollbackStatus();
                var hs = NetworkOptimizerService.HasRestoreSnapshot();
                var qb = _services.Network.LoadQualityBenchmark();
                return (r, b, rb, hs, qb);
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

            if (bench.Before is { Ok: true } && bench.After is { Ok: true } after)
            {
                BenchmarkSummary =
                    $"Verification sample · {FormatMs(after.PingP50Ms)} ms path latency" +
                    $" · {FormatMs(after.JitterMs)} ms jitter · conditions vary";
                BenchmarkBrush = ResolveBrush(
                    "ExoMutedTextBrush",
                    Color.FromArgb(255, 95, 95, 105));
                HasBenchmark = true;
            }
            else
            {
                HasBenchmark = false;
                BenchmarkSummary = string.Empty;
            }

            ApplyQualityResult(quality);
            _lastQuality = quality;
            _lastApplyReport = report;

            ApplyReportRows.Clear();
            foreach (var step in report)
                ApplyReportRows.Add(ApplyReportPresentation.Row(step.Name, step.Status, step.Reason));
            HasApplyReport = ApplyReportRows.Count > 0;
            ApplyReportSummary = ApplyReportPresentation.Summarize(ApplyReportRows.ToList());
            if (_lastSnap is not null)
            {
                RefreshInfoCards(_lastSnap);
                RefreshGuidance(_lastSnap);
            }
        }
        catch
        {
            // Proof layer is additive — never break the page over it.
        }
    }

    private static string FormatMs(double value) =>
        value < 0 ? "—" : value.ToString(value >= 100 ? "0" : "0.0");

    private void ApplyQualityResult(NetworkBenchmarkResult? result)
    {
        if (result is not { Ok: true, IsQualityTest: true })
        {
            HasQualityResult = false;
            QualitySummary = string.Empty;
            return;
        }

        var downPenalty = Math.Max(0, result.DownloadLoadedMs - result.PingP50Ms);
        var upPenalty = Math.Max(0, result.UploadLoadedMs - result.PingP50Ms);
        QualitySummary = $"{result.PingP50Ms:0.#} ms idle · full-load +{downPenalty:0.#} ms download / +{upPenalty:0.#} ms upload · {result.PacketLossPercent:0.##}% idle loss · {result.DnsProvider} DNS";
        HasQualityResult = true;
    }

    private void ApplySnapshotToUi(NetworkSnapshot snap, bool preserveSuccessMessage)
    {
        _lastSnap = snap;
        HeaderStatus = BuildStatus(snap);

        RefreshInfoCards(snap);

        if (!IsLoading)
            IsFeatureListVisible = Rows.Count > 0;

        // Path (Ethernet vs Wi‑Fi) lives in the header only — no redundant banner on open.
        if (!string.IsNullOrWhiteSpace(snap.Detail) && !snap.ProbeOk)
            SetMessage(snap.Detail, success: false);
        else if (!preserveSuccessMessage)
            ClearMessage();

        RefreshGuidance(snap);
    }

    private void RefreshGuidance(NetworkSnapshot snap)
    {
        var applied = snap.Features.Count > 0 && snap.Features.All(r => r.IsOk);
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
            snap.Features.Select(r => (r.Title, r.IsOk, r.Status)).ToList(),
            failSteps);
        // Always surface brick-safety: Wi-Fi stays up, snapshot first, auto-rollback on probe fail.
        const string safety =
            "Safety: Wi-Fi is never disabled. Apply snapshots first and auto-rolls back if connectivity fails. Repair restores the pre-Exo snapshot.";
        if (string.IsNullOrWhiteSpace(GuidanceText))
            GuidanceText = safety;
        else if (!GuidanceText.Contains("Wi-Fi is never disabled", StringComparison.OrdinalIgnoreCase))
            GuidanceText = GuidanceText.TrimEnd() + " " + safety;
        HasGuidance = !string.IsNullOrWhiteSpace(GuidanceText);
    }

    /// <summary>
    /// Four plain-language cards explain outcomes instead of exposing a wall of
    /// registry/driver implementation details. Deep verification stays internal.
    /// </summary>
    private void RefreshInfoCards(NetworkSnapshot snap)
    {
        Rows.Clear();
        var applied = snap.ActivePreset is NetworkPreset.LowestLatency or NetworkPreset.HighestThroughput;

        var path = snap.Media.EthernetInUse
            ? $"{snap.LinkSpeed} Ethernet gets the lowest route metric; Wi-Fi is never disabled."
            : snap.Media.WifiUp
                ? $"Wi-Fi stays enabled and prefers {snap.Media.PreferredBandTarget} when the adapter supports it."
                : "Keeps every adapter recoverable and changes route priority only when a healthy path exists.";
        AddInfoCard("Connection path", path, snap.ProbeOk);

        var policy = _lastQuality is { Ok: true, IsQualityTest: true } quality
            ? $"Measured idle latency, full-load queueing, and throughput; applied {PresetLabel(snap.ActivePreset)} policy."
            : "Measures idle latency, full-load queueing, and throughput before choosing one combined policy.";
        AddInfoCard("Adaptive tuning", policy, applied);

        var dnsStep = _lastApplyReport.LastOrDefault(r =>
            string.Equals(r.Name, "dns-auto", StringComparison.OrdinalIgnoreCase));
        var dnsDetail = dnsStep is not null && !string.IsNullOrWhiteSpace(dnsStep.Reason)
            ? dnsStep.Reason.Replace(
                "Windows rejected automatic DoH",
                "automatic DoH needs a 3.5.1 re-apply",
                StringComparison.OrdinalIgnoreCase)
            : _lastQuality is { Ok: true, IsQualityTest: true } dnsQuality && !string.IsNullOrWhiteSpace(dnsQuality.DnsProvider)
                ? $"{dnsQuality.DnsProvider} won the live resolver test; Apply also registers its encrypted DNS template."
                : "Tests Cloudflare, Google, and Quad9 on this route, selects the fastest healthy resolver, and requests automatic DoH when Windows supports it.";
        AddInfoCard("DNS privacy", dnsDetail, dnsStep?.Status == "ok" || !applied);

        AddInfoCard(
            "Safe repair",
            NetworkOptimizerService.HasRestoreSnapshot()
                ? "A pre-Exo snapshot is ready; Repair restores DNS, DoH, routes, TCP, and NIC settings."
                : "Apply takes a pre-change snapshot; Repair can return the Windows network stack to stock defaults.",
            true);

        IsFeatureListVisible = Rows.Count > 0;
    }

    private void AddInfoCard(string title, string detail, bool active)
    {
        Rows.Add(new FeatureRowViewModel
        {
            Title = title,
            Detail = detail,
            Glyph = UiStatusPresentation.FeatureGlyph(active),
            Opacity = 1,
            IsActive = active,
            RailOpacity = UiStatusPresentation.FeatureRailOpacity(active)
        });
    }

    private static string PresetLabel(NetworkPreset preset) => preset switch
    {
        NetworkPreset.HighestThroughput => "multi-gig throughput + latency",
        NetworkPreset.LowestLatency => "latency-safe",
        _ => "balanced"
    };

    [RelayCommand]
    private async Task AnalyzeAndApplyAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        HasMessage = false;
        ProgressStatus = "Starting sustained connection analysis...";
        NetworkBenchmarkResult? result = null;
        NetworkPreset preset = NetworkPreset.LowestLatency;
        try
        {
            var snap = _lastSnap ?? await _services.Network.ProbeAsync();
            var testProgress = new Progress<string>(s => ProgressStatus = s);
            result = await _services.Network.RunQualityBenchmarkAsync(snap.Media, testProgress);
            if (result is not { Ok: true, IsQualityTest: true })
            {
                SetMessage("Connection test could not finish. No settings were changed.", success: false);
                return;
            }

            preset = NetworkLogic.RecommendPreset(result, snap.Media);
            _services.Network.PersistQualityBenchmark(result);
            ApplyQualityResult(result);
            ProgressStatus = "Applying the best verified settings for this link...";
        }
        catch (Exception ex)
        {
            SetMessage(ex.Message, success: false);
            return;
        }
        finally
        {
            IsBusy = false;
            if (result is null) ProgressStatus = string.Empty;
        }

        var applied = await ApplyPresetAsync(preset, result);
        if (result is not null && applied)
        {
            var bufferbloat = Math.Max(result.DownloadLoadedMs, result.UploadLoadedMs) - result.PingP50Ms;
            var note = bufferbloat >= 25
                ? " Loaded latency is high; router SQM/CAKE can help more than Windows tuning."
                : string.Empty;
            SetMessage($"Optimized · {result.DnsProvider} DNS selected.{note}", success: true);
        }
    }

    /// <summary>Apply chosen stack immediately, then refresh header + feature rows in place.</summary>
    private async Task<bool> ApplyPresetAsync(NetworkPreset preset, NetworkBenchmarkResult result)
    {
        if (IsBusy) return false;

        IsBusy = true;
        HasMessage = false;
        ProgressStatus = "Detecting path...";
        var succeeded = false;
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
                RestartEthernet = false,
                DnsProvider = string.IsNullOrWhiteSpace(result.DnsProvider) ? "Cloudflare" : result.DnsProvider,
                DnsPrimary = string.IsNullOrWhiteSpace(result.DnsPrimary) ? "1.1.1.1" : result.DnsPrimary,
                DnsSecondary = string.IsNullOrWhiteSpace(result.DnsSecondary) ? "1.0.0.1" : result.DnsSecondary,
                DnsPrimaryV6 = string.IsNullOrWhiteSpace(result.DnsPrimaryV6) ? "2606:4700:4700::1111" : result.DnsPrimaryV6,
                DnsSecondaryV6 = string.IsNullOrWhiteSpace(result.DnsSecondaryV6) ? "2606:4700:4700::1001" : result.DnsSecondaryV6,
                DnsOverHttpsTemplate = string.IsNullOrWhiteSpace(result.DnsOverHttpsTemplate) ? "https://cloudflare-dns.com/dns-query" : result.DnsOverHttpsTemplate
            };

            ProgressStatus = snap.Media.EthernetInUse
                ? "Applying Ethernet stack..."
                : snap.Media.WifiUp
                    ? "Applying Wi‑Fi stack..."
                    : "Applying...";
            var progress = new Progress<string>(s => ProgressStatus = s);
            var (ok, msg) = await _services.Network.ApplyPresetAsync(preset, options, progress);
            succeeded = ok;
            // Same finish banner as Discord / Steam / NVIDIA.
            SetMessage(ok ? Helpers.OptimizerMessages.Done : msg, ok);

            // In-place refresh so the verified unified state appears without leaving the page.
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
        return succeeded;
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

        // ASCII separators only in the plate title — middle-dot often mojibakes
        // in UIA snapshots / logs and looks broken to users.
        var media = snap.Media.EthernetInUse
            ? "Ethernet path"
            : snap.Media.EthernetUp
                ? "Ethernet (no IP yet)"
                : snap.Media.WifiUp
                    ? $"Wi-Fi - {snap.Media.PreferredBandTarget}"
                    : snap.ConnectionType;

        if (snap.ActivePreset == NetworkPreset.Balanced)
            return string.IsNullOrWhiteSpace(snap.LinkSpeed)
                ? media
                : $"{media} - {snap.LinkSpeed}";

        var open = snap.Features
            .Where(f => !f.IsOk && !string.IsNullOrWhiteSpace(f.Title))
            .Select(f => f.Title)
            .Take(2)
            .ToList();
        if (open.Count == 0)
            return $"Optimized - {media}";
        // Name the open row instead of vague "check rows" (Cua stress honesty).
        return open.Count == 1
            ? $"Optimized - open: {open[0]}"
            : $"Optimized - open: {open[0]}, {open[1]}";
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
