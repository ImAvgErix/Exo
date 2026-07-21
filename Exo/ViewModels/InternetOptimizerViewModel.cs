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
    private ExoInternetSnapshot? _lastSnap;
    private ExoInternetBenchmarkResult? _lastQuality;
    private IReadOnlyList<ExoInternetApplyReportStep> _lastApplyReport = Array.Empty<ExoInternetApplyReportStep>();

    public InternetOptimizerViewModel(AppServices services)
    {
        _services = services;
        var banner = UiStatusPresentation.BannerForSuccess(true);
        MessageGlyph = banner.Glyph;
        MessageBrush = ResolveBrush(banner.BrushKey, Color.FromArgb(255, 34, 197, 94));
        // Gaming default: lowest latency. User can flip to high throughput.
        var preferred = _services.Internet.LoadPreferredPolicy();
        PreferLowestLatency = preferred != ExoInternetPreset.HighestThroughput;
        SelectedProfileOption = PreferLowestLatency ? "Lowest latency" : "High throughput";
        try
        {
            PreferExperimental = services.Settings.Current.ExperimentalInternet;
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
        try { _services.Settings.Update(s => s.ExperimentalInternet = value); } catch { }
    }

    partial void OnSelectedProfileOptionChanged(string value)
    {
        var latency = string.Equals(value, "Lowest latency", StringComparison.OrdinalIgnoreCase);
        if (PreferLowestLatency != latency) PreferLowestLatency = latency;
    }

    public ObservableCollection<FeatureRowViewModel> Rows { get; } = new();

    /// <summary>When true, Analyze &amp; Apply uses LowestLatency knobs (FC/IM off).</summary>
    [ObservableProperty] public partial bool PreferLowestLatency { get; set; } = true;

    public bool PreferHighThroughput
    {
        get => !PreferLowestLatency;
        set
        {
            if (PreferLowestLatency == !value) return;
            PreferLowestLatency = !value;
        }
    }

    public ExoInternetPreset SelectedPolicy =>
        PreferLowestLatency ? ExoInternetPreset.LowestLatency : ExoInternetPreset.HighestThroughput;

    partial void OnPreferLowestLatencyChanged(bool value)
    {
        var opt = value ? "Lowest latency" : "High throughput";
        if (!string.Equals(SelectedProfileOption, opt, StringComparison.Ordinal))
            SelectedProfileOption = opt;
        OnPropertyChanged(nameof(PreferHighThroughput));
        OnPropertyChanged(nameof(SelectedPolicy));
        try { _services.Internet.SavePreferredPolicy(SelectedPolicy); } catch { }
    }

    public IReadOnlyList<string> ApplyModeOptions { get; } = new[] { "Stable", "Experimental" };
    [ObservableProperty] public partial string SelectedApplyMode { get; set; } = "Stable";
    public IReadOnlyList<string> ProfileOptions { get; } = new[] { "Lowest latency", "High throughput" };
    [ObservableProperty] public partial string SelectedProfileOption { get; set; } = "Lowest latency";

    [ObservableProperty] public partial string HeaderStatus { get; set; } = "Checking...";
    [ObservableProperty] public partial string GuidanceText { get; set; } = "Detecting this PC...";
    [ObservableProperty] public partial bool HasGuidance { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; } = true;
    [ObservableProperty] public partial bool PreferExperimental { get; set; }
    [ObservableProperty] public partial bool IsFeatureListVisible { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string ProgressStatus { get; set; } = string.Empty;
    [ObservableProperty] public partial string Message { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasMessage { get; set; }
    [ObservableProperty] public partial string MessageGlyph { get; set; } = "\uE73E";
    [ObservableProperty] public partial Brush MessageBrush { get; set; }

    // Proof layer - persisted benchmark delta, honest rollback marker, restore capability.
    [ObservableProperty] public partial bool HasBenchmark { get; set; }
    [ObservableProperty] public partial string BenchmarkSummary { get; set; } = string.Empty;
    [ObservableProperty] public partial Brush BenchmarkBrush { get; set; } = new SolidColorBrush(Color.FromArgb(255, 161, 161, 170));
    [ObservableProperty] public partial bool HasRollback { get; set; }
    [ObservableProperty] public partial string RollbackNotice { get; set; } = string.Empty;
    [ObservableProperty] public partial string RepairHint { get; set; } = string.Empty;
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
    private void SelectLatencyPolicy() => PreferLowestLatency = true;

    [RelayCommand]
    private void SelectThroughputPolicy() => PreferLowestLatency = false;

    [RelayCommand]
    public Task RefreshAsync() => LoadSnapshotAsync(showFullPageLoading: !IsBusy);

    /// <summary>
    /// Probe and update header + feature rows.
    /// Does not early-return on IsBusy so apply/repair can refresh in place.
    /// </summary>
    private DateTimeOffset _lastProbeUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan ProbeFreshness = TimeSpan.FromSeconds(90);
    private int _probeGen;
    private CancellationTokenSource? _probeCts;

    public void CancelBackgroundWork()
    {
        try { _probeCts?.Cancel(); } catch { }
        Interlocked.Increment(ref _probeGen);
    }

    private async Task LoadSnapshotAsync(bool showFullPageLoading, bool force = true)
    {
        if (!force && Rows.Count > 0 && DateTimeOffset.UtcNow - _lastProbeUtc < ProbeFreshness)
        {
            IsLoading = false;
            IsFeatureListVisible = true;
            return;
        }

        try { _probeCts?.Cancel(); } catch { }
        _probeCts?.Dispose();
        _probeCts = new CancellationTokenSource();
        var ct = _probeCts.Token;
        var gen = Interlocked.Increment(ref _probeGen);
        if (Rows.Count == 0)
        {
            IsLoading = true;
            IsFeatureListVisible = false;
            HeaderStatus = "Checking...";
        }

        try
        {
            var snap = await _services.Internet.ProbeAsync(ct).ConfigureAwait(true);
            if (gen != _probeGen || ct.IsCancellationRequested) return;
            ApplySnapshotToUi(snap, preserveSuccessMessage: false);
            _lastProbeUtc = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (gen == _probeGen && Rows.Count == 0)
            {
                HeaderStatus = "Unavailable";
                SetMessage(ex.Message, success: false);
            }
        }
        finally
        {
            if (gen == _probeGen)
            {
                IsLoading = false;
                IsFeatureListVisible = Rows.Count > 0;
            }
        }
        if (gen == _probeGen && !ct.IsCancellationRequested)
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
                var r = _services.Internet.LoadLastApplyReport();
                var b = _services.Internet.LoadBenchmark();
                var rb = _services.Internet.LoadRollbackStatus();
                var hs = ExoInternetOptimizerService.HasRestoreSnapshot();
                var qb = _services.Internet.LoadQualityBenchmark();
                return (r, b, rb, hs, qb);
            });

            HasRollback = rollback?.RolledBack == true;
            RollbackNotice = HasRollback
                ? "Apply rolled back: " + (string.IsNullOrWhiteSpace(rollback!.Reason)
                    ? "connectivity check failed"
                    : rollback.Reason)
                : string.Empty;

            if (bench.Before is { Ok: true } && bench.After is { Ok: true } after)
            {
                BenchmarkSummary =
                    $"Verification sample - {FormatMs(after.PingP50Ms)} ms path latency" +
                    $" - {FormatMs(after.JitterMs)} ms jitter - conditions vary";
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
            // Proof layer is additive - never break the page over it.
        }
    }

    private static string FormatMs(double value) =>
        value < 0 ? "-" : value.ToString(value >= 100 ? "0" : "0.0");

    private void ApplyQualityResult(ExoInternetBenchmarkResult? result)
    {
        if (result is not { Ok: true, IsQualityTest: true })
        {
            HasQualityResult = false;
            QualitySummary = string.Empty;
            return;
        }

        var downPenalty = Math.Max(0, result.DownloadLoadedMs - result.PingP50Ms);
        var upPenalty = Math.Max(0, result.UploadLoadedMs - result.PingP50Ms);
        // +N under load is bufferbloat (ping rise while saturating), not idle RTT.
        QualitySummary =
            $"{result.PingP50Ms:0.#} ms idle · load spike +{downPenalty:0.#} down / +{upPenalty:0.#} up · {result.PacketLossPercent:0.##}% idle loss · {result.DnsProvider} DNS";
        HasQualityResult = true;
    }

    private void ApplySnapshotToUi(ExoInternetSnapshot snap, bool preserveSuccessMessage)
    {
        _lastSnap = snap;
        HeaderStatus = BuildStatus(snap);

        RefreshInfoCards(snap);

        if (!IsLoading)
            IsFeatureListVisible = Rows.Count > 0;

        // Path (Ethernet vs Wi‑Fi) lives in the header only - no redundant banner on open.
        if (!string.IsNullOrWhiteSpace(snap.Detail) && !snap.ProbeOk)
            SetMessage(snap.Detail, success: false);
        else if (!preserveSuccessMessage)
            ClearMessage();

        RefreshGuidance(snap);
    }

    private void RefreshGuidance(ExoInternetSnapshot snap)
    {
        _ = snap;
        GuidanceText = string.Empty;
        HasGuidance = false;
    }

    /// <summary>
    /// Full probe feature list (SMB, LLMNR, multi-app DSCP, host MMCSS, NIC knobs, …)
    /// plus a short path/DNS/repair summary so new tweaks are visible, not buried.
    /// </summary>
    private void RefreshInfoCards(ExoInternetSnapshot snap)
    {
        Rows.Clear();
        var applied = snap.ActivePreset is ExoInternetPreset.LowestLatency or ExoInternetPreset.HighestThroughput;

        var path = snap.Media.EthernetInUse
            ? $"{snap.LinkSpeed} Ethernet gets the lowest route metric; Wi-Fi is never disabled."
            : snap.Media.WifiUp
                ? $"Wi-Fi stays enabled and prefers {snap.Media.PreferredBandTarget} when the adapter supports it."
                : "Keeps every adapter recoverable and changes route priority only when a healthy path exists.";
        AddInfoCard("Connection path", path, snap.ProbeOk);

        var policy = applied
            ? $"Last apply used {PresetLabel(snap.ActivePreset)}. Stack profile selects Lowest latency or High throughput."
            : $"Selected: {PresetLabel(SelectedPolicy)}. Analyze measures DNS/quality, then applies your stack profile.";
        AddInfoCard("Stack profile", policy, PreferLowestLatency || PreferHighThroughput);

        // Live engine knobs from probe (includes SMB, LLMNR, multi-app DSCP, host multimedia, …)
        if (snap.Features is { Count: > 0 })
        {
            foreach (var f in snap.Features)
            {
                if (string.IsNullOrWhiteSpace(f.Title)) continue;
                // Skip redundant path/policy echoes if present
                if (f.Title.Equals("Path policy", StringComparison.OrdinalIgnoreCase)) continue;
                AddInfoCard(f.Title, string.IsNullOrWhiteSpace(f.Status) ? "—" : f.Status, f.IsOk);
            }
        }

        var dnsStep = _lastApplyReport.LastOrDefault(r =>
            string.Equals(r.Name, "dns-auto", StringComparison.OrdinalIgnoreCase));
        var dnsDetail = dnsStep is not null && !string.IsNullOrWhiteSpace(dnsStep.Reason)
            ? SanitizeUiAscii(dnsStep.Reason).Replace(
                "Windows rejected automatic DoH",
                "automatic DoH needs a 3.5.1 re-apply",
                StringComparison.OrdinalIgnoreCase)
            : _lastQuality is { Ok: true, IsQualityTest: true } dnsQuality && !string.IsNullOrWhiteSpace(dnsQuality.DnsProvider)
                ? $"{dnsQuality.DnsProvider} won the live resolver test; Apply also registers its encrypted DNS template."
                : "Tests Cloudflare, Google, and Quad9 on this route, selects the fastest healthy resolver, and requests automatic DoH when Windows supports it.";
        AddInfoCard("DNS privacy", dnsDetail, dnsStep?.Status == "ok" || !applied);

        AddInfoCard(
            "Safe repair",
            ExoInternetOptimizerService.HasRestoreSnapshot()
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

    private static string PresetLabel(ExoInternetPreset preset) => preset switch
    {
        ExoInternetPreset.HighestThroughput => "high throughput",
        ExoInternetPreset.LowestLatency => "lowest latency",
        _ => "balanced"
    };

    [RelayCommand]
    private async Task AnalyzeAndApplyAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        HasMessage = false;
        ProgressStatus = "Starting sustained connection analysis...";
        ExoInternetBenchmarkResult? result = null;
        // Toggle owns the stack (latency vs throughput). Analyze picks DNS + quality proof only.
        var preset = SelectedPolicy;
        try
        {
            var snap = _lastSnap ?? await _services.Internet.ProbeAsync();
            var testProgress = new Progress<string>(s => ProgressStatus = s);
            result = await _services.Internet.RunQualityBenchmarkAsync(snap.Media, testProgress);
            if (result is not { Ok: true, IsQualityTest: true })
            {
                SetMessage("Connection test could not finish. No settings were changed.", success: false);
                return;
            }

            // Surface what auto-mode would have chosen, but never override the toggle.
            var suggested = ExoInternetLogic.RecommendPreset(result, snap.Media);
            _services.Internet.PersistQualityBenchmark(result);
            ApplyQualityResult(result);
            ProgressStatus = suggested == preset
                ? $"Applying {PresetLabel(preset)} stack..."
                : $"Applying {PresetLabel(preset)} stack (measure suggested {PresetLabel(suggested)})...";
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

        if (result is null) return;

        var applied = await ApplyPresetAsync(preset, result);
        if (applied)
        {
            var bufferbloat = Math.Max(result.DownloadLoadedMs, result.UploadLoadedMs) - result.PingP50Ms;
            var note = bufferbloat >= 25
                ? " Loaded latency is high; router SQM/CAKE can help more than Windows tuning."
                : string.Empty;
            SetMessage($"Optimized ({PresetLabel(preset)}) - {result.DnsProvider} DNS selected.{note}", success: true);
        }
    }

    /// <summary>Apply chosen stack immediately, then refresh header + feature rows in place.</summary>
    private async Task<bool> ApplyPresetAsync(ExoInternetPreset preset, ExoInternetBenchmarkResult result)
    {
        if (IsBusy) return false;

        IsBusy = true;
        HasMessage = false;
        ProgressStatus = "Detecting path...";
        var succeeded = false;
        try
        {
            var snap = await _services.Internet.ProbeAsync();
            _lastSnap = snap;
            HeaderStatus = BuildStatus(snap);

            // Fail-closed: never disable Wi-Fi. Restart Ethernet briefly so advanced
            // NIC props (flow control, interrupt moderation) actually stick on drivers
            // that ignore NoRestart writes (Intel I226 etc.).
            var options = new ExoInternetApplyOptions
            {
                PreferEthernetDisableWifi = false,
                RestartEthernet = snap.Media.EthernetInUse || snap.Media.EthernetUp,
                Experimental = true,
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
            var (ok, msg) = await _services.Internet.ApplyPresetAsync(preset, options, progress);
            succeeded = ok;
            // Same finish banner as Discord / Steam / NVIDIA.
            SetMessage(ok ? Helpers.OptimizerMessages.Done : msg, ok);

            // In-place refresh so the verified unified state appears without leaving the page.
            ProgressStatus = "Refreshing...";
            try
            {
                var after = await _services.Internet.ProbeAsync();
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

    public Task InitializeAsync() => LoadSnapshotAsync(showFullPageLoading: Rows.Count == 0, force: false);

    [RelayCommand]
    private async Task RepairAsync()
    {
        if (IsBusy) return;

        // Quiet secondary action - runs immediately. The RepairHint caption states
        // whether this restores the exact pre-Exo snapshot or resets to stock defaults.
        IsBusy = true;
        HasMessage = false;
        ProgressStatus = "Repairing...";
        try
        {
            var progress = new Progress<string>(s => ProgressStatus = s);
            var (success, msg) = await _services.Internet.RepairAsync(progress);
            SetMessage(success ? Helpers.OptimizerMessages.RepairFinished : msg, success);
            ProgressStatus = "Refreshing...";
            try
            {
                var after = await _services.Internet.ProbeAsync();
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

    private static string BuildStatus(ExoInternetSnapshot snap)
    {
        if (!snap.ProbeOk) return "Probe incomplete";

        // ASCII separators only in the plate title - middle-dot often mojibakes
        // in UIA snapshots / logs and looks broken to users.
        var media = snap.Media.EthernetInUse
            ? "Ethernet path"
            : snap.Media.EthernetUp
                ? "Ethernet (no IP yet)"
                : snap.Media.WifiUp
                    ? $"Wi-Fi - {snap.Media.PreferredBandTarget}"
                    : snap.ConnectionType;

        if (snap.ActivePreset == ExoInternetPreset.Balanced)
            return string.IsNullOrWhiteSpace(snap.LinkSpeed)
                ? media
                : $"{media} - {snap.LinkSpeed}";

        // Ignore soft N/A style rows that are informational, not failures.
        static bool IsRealOpen(ExoInternetFeatureRow f)
        {
            if (f.IsOk || string.IsNullOrWhiteSpace(f.Title)) return false;
            var s = f.Status ?? string.Empty;
            if (s.Contains("Not exposed", StringComparison.OrdinalIgnoreCase)) return false;
            if (s.Contains("not required", StringComparison.OrdinalIgnoreCase)) return false;
            if (s.Contains("Unsupported", StringComparison.OrdinalIgnoreCase)) return false;
            if (s.Contains("probe unavailable", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        var open = snap.Features
            .Where(IsRealOpen)
            .Select(f => f.Title)
            .Take(2)
            .ToList();
        if (open.Count == 0)
            return $"Optimized - {media}";
        return open.Count == 1
            ? $"Optimized - open: {open[0]}"
            : $"Optimized - open: {open[0]}, {open[1]}";
    }

    /// <summary>
    /// UIA / Cua logs mojibake middle-dots and smart punctuation; keep status ASCII.
    /// </summary>
    private static string SanitizeUiAscii(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text
            .Replace("\u00B7", " - ", StringComparison.Ordinal) // ·
            .Replace("\u2022", " - ", StringComparison.Ordinal) // •
            .Replace("\u2013", "-", StringComparison.Ordinal)   // –
            .Replace("\u2014", "-", StringComparison.Ordinal)   // —
            .Replace("  -  ", " - ", StringComparison.Ordinal)
            .Replace("  ", " ", StringComparison.Ordinal);
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
