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

    /// <summary>Only choice prompt: Lowest latency vs Highest download.</summary>
    public event Func<Task<NetworkPreset?>>? RequestPresetChoice;

    /// <summary>Repair confirm (title, message) — same pattern as Discord/Steam.</summary>
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
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsLoading = true;
        try
        {
            var snap = await _services.Network.ProbeAsync();
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

            // Opening detect: always surface Ethernet vs Wi‑Fi path.
            if (!string.IsNullOrWhiteSpace(snap.Detail) && !snap.ProbeOk)
                SetMessage(snap.Detail, success: false);
            else
                SetMessage(BuildPathHint(snap), success: true);
        }
        catch (Exception ex)
        {
            HeaderStatus = "Unavailable";
            SetMessage(ex.Message, success: false);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Single Apply — only prompts for latency vs download, then runs with smart defaults
    /// (Ethernet-first + restart when Ethernet is present). No second confirm box.
    /// </summary>
    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (IsBusy) return;

        // Fresh detect so path (Ethernet vs Wi‑Fi) is current before apply.
        ProgressStatus = "Detecting path...";
        NetworkSnapshot snap;
        try
        {
            snap = await _services.Network.ProbeAsync();
            _lastSnap = snap;
            HeaderStatus = BuildStatus(snap);
        }
        catch (Exception ex)
        {
            SetMessage(ex.Message, success: false);
            ProgressStatus = string.Empty;
            return;
        }

        NetworkPreset? preset = NetworkPreset.LowestLatency;
        if (RequestPresetChoice is not null)
        {
            preset = await RequestPresetChoice.Invoke();
            if (preset is null)
            {
                SetMessage("Apply cancelled.", success: false);
                ProgressStatus = string.Empty;
                return;
            }
        }

        IsBusy = true;
        HasMessage = false;
        try
        {
            // Smart defaults from detect — no extra confirm.
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
            var (ok, msg) = await _services.Network.ApplyPresetAsync(preset.Value, options, progress);
            SetMessage(msg, ok);
            await RefreshAsync();
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

    public Task InitializeAsync() => RefreshAsync();

    /// <summary>Undo OptiHub network stack → stock-like defaults (bindings, metric auto, etc.).</summary>
    [RelayCommand]
    private async Task RepairAsync()
    {
        if (IsBusy) return;

        var ok = ConfirmAsync is not null
            ? await ConfirmAsync(
                "Repair Internet?",
                "Restores stock-like network settings: adapter bindings (Client/File share/LLDP back on), automatic metric, default TCP/auto-tune, re-enables Wi‑Fi if OptiHub disabled it.\n\nNeeds Administrator.")
            : true;
        if (!ok) return;

        IsBusy = true;
        HasMessage = false;
        ProgressStatus = "Repairing...";
        try
        {
            var progress = new Progress<string>(s => ProgressStatus = s);
            var (success, msg) = await _services.Network.RepairAsync(progress);
            SetMessage(msg, success);
            await RefreshAsync();
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

    /// <summary>Plain-language path summary after open detect.</summary>
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
