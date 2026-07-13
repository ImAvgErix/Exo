using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using OptiHub.Helpers;
using OptiHub.Models;
using OptiHub.Services;
using Windows.UI;

namespace OptiHub.ViewModels;

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

    /// <summary>Repair confirm (title, message).</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    [ObservableProperty] private string _headerStatus = "Checking...";
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _progressStatus = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private bool _hasMessage;
    [ObservableProperty] private string _messageGlyph = "\uE73E";
    [ObservableProperty] private Brush _messageBrush;

    [RelayCommand]
    public Task RefreshAsync() => LoadSnapshotAsync(showFullPageLoading: !IsBusy);

    /// <summary>
    /// Probe and update header + feature rows.
    /// Does not early-return on IsBusy so apply/repair can refresh in place.
    /// </summary>
    private async Task LoadSnapshotAsync(bool showFullPageLoading)
    {
        if (showFullPageLoading)
            IsLoading = true;
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
        }
    }

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
                Opacity = UiStatusPresentation.FeatureOpacity(f.IsOk)
            });
        }

        if (!string.IsNullOrWhiteSpace(snap.Detail) && !snap.ProbeOk)
            SetMessage(snap.Detail, success: false);
        else if (!preserveSuccessMessage)
            SetMessage(BuildPathHint(snap), success: true);
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

            var options = new NetworkApplyOptions
            {
                PreferEthernetDisableWifi = true,
                RestartEthernet = snap.Media.EthernetUp || snap.Media.EthernetAvailable
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

        var ok = ConfirmAsync is not null
            ? await ConfirmAsync(
                "Repair Internet?",
                "Restores stock-like network settings: adapter bindings, automatic metric, default TCP/auto-tune, re-enables Wi‑Fi if OptiHub disabled it.\n\nNeeds Administrator.")
            : true;
        if (!ok) return;

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

    private static string BuildPathHint(NetworkSnapshot snap)
    {
        var m = snap.Media;
        if (m.EthernetInUse)
            return m.WifiAvailable
                ? "Detected Ethernet (usable). Apply will prefer Ethernet and can disable Wi‑Fi."
                : "Detected Ethernet. Apply will tune the wired stack.";
        if (m.EthernetUp && !m.EthernetInUse)
            return "Ethernet is linked but has no IPv4 yet — Wi‑Fi stays available until Ethernet gets an address.";
        if (m.WifiUp)
            return $"Detected Wi‑Fi only. Apply will tune Wi‑Fi (prefer {m.PreferredBandTarget}).";
        if (m.EthernetAvailable || m.WifiAvailable)
            return "Adapters found but none fully up — connect, then Refresh.";
        return "No active Ethernet or Wi‑Fi adapter detected.";
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
