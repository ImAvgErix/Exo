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

    /// <summary>Raised when UI should confirm apply options (Ethernet-first + restart).</summary>
    public event Func<NetworkSnapshot, NetworkPreset, Task<NetworkApplyOptions?>>? RequestApplyConfirm;

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

            if (!string.IsNullOrWhiteSpace(snap.Detail) && !snap.ProbeOk)
                SetMessage(snap.Detail, success: false);
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

    [RelayCommand]
    private async Task ApplyLatencyAsync() => await ApplyAsync(NetworkPreset.LowestLatency);

    [RelayCommand]
    private async Task ApplyThroughputAsync() => await ApplyAsync(NetworkPreset.HighestThroughput);

    private async Task ApplyAsync(NetworkPreset preset)
    {
        if (IsBusy) return;
        IsBusy = true;
        ProgressStatus = "Detecting...";
        HasMessage = false;
        try
        {
            var snap = _lastSnap ?? await _services.Network.ProbeAsync();
            _lastSnap = snap;

            NetworkApplyOptions options;
            if (RequestApplyConfirm is not null)
            {
                var chosen = await RequestApplyConfirm.Invoke(snap, preset);
                if (chosen is null)
                {
                    SetMessage("Apply cancelled.", success: false);
                    return;
                }
                options = chosen;
            }
            else
            {
                options = new NetworkApplyOptions
                {
                    PreferEthernetDisableWifi = true,
                    RestartEthernet = snap.Media.EthernetUp || snap.Media.EthernetAvailable
                };
            }

            ProgressStatus = "Applying...";
            var progress = new Progress<string>(s => ProgressStatus = s);
            var (ok, msg) = await _services.Network.ApplyPresetAsync(preset, options, progress);
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

    private static string BuildStatus(NetworkSnapshot snap)
    {
        if (!snap.ProbeOk) return "Probe incomplete";

        var media = snap.Media.EthernetInUse
            ? "Ethernet preferred"
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
