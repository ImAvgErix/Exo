using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using OptiHub.Models;
using OptiHub.Services;
using Windows.UI;

namespace OptiHub.ViewModels;

public partial class InternetOptimizerViewModel : ObservableObject
{
    private readonly AppServices _services;

    public InternetOptimizerViewModel(AppServices services)
    {
        _services = services;
        MessageBrush = ResolveBrush("OptiSuccessBrush", Color.FromArgb(255, 34, 197, 94));
    }

    public ObservableCollection<FeatureRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string _headerStatus = "Checking...";
    [ObservableProperty] private string _adapterLine = string.Empty;
    [ObservableProperty] private string _providerLine = string.Empty;
    [ObservableProperty] private string _pathLine = string.Empty;
    [ObservableProperty] private string _latencyLine = string.Empty;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _progressStatus = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private bool _hasMessage;
    [ObservableProperty] private string _messageGlyph = "\uE73E";
    [ObservableProperty] private Brush _messageBrush;
    [ObservableProperty] private string _activePresetLabel = "None";

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsLoading = true;
        try
        {
            var snap = await _services.Network.ProbeAsync();
            HeaderStatus = snap.ProbeOk ? "Ready" : "Probe incomplete";
            AdapterLine = $"{snap.ConnectionType} · {snap.AdapterDescription} · {snap.LinkSpeed}";
            ProviderLine = string.IsNullOrWhiteSpace(snap.Provider) || snap.Provider == "—"
                ? $"IP {snap.PublicIp}"
                : $"{snap.Provider} · {snap.PublicIp}";
            PathLine = $"{snap.Area} · GW {snap.Gateway} · DNS {snap.DnsServers}";
            LatencyLine = $"Gateway {Fmt(snap.GatewayPingMs)} · Internet {Fmt(snap.InternetPingMs)} · MTU {snap.Mtu}";
            ActivePresetLabel = snap.ActivePreset switch
            {
                NetworkPreset.LowestLatency => "Lowest latency",
                NetworkPreset.HighestThroughput => "Highest download",
                _ => "Balanced / default"
            };

            Rows.Clear();
            foreach (var f in snap.Features)
            {
                Rows.Add(new FeatureRowViewModel
                {
                    Title = f.Title,
                    Detail = f.Status,
                    Glyph = f.IsOk ? "\uE73E" : "\uE711",
                    Opacity = f.IsOk ? 1.0 : 0.85
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
        ProgressStatus = "Applying...";
        HasMessage = false;
        try
        {
            var progress = new Progress<string>(s => ProgressStatus = s);
            var (ok, msg) = await _services.Network.ApplyPresetAsync(preset, progress);
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

    private static string Fmt(int? ms) => ms is int v ? $"{v} ms" : "—";

    private void SetMessage(string text, bool success)
    {
        Message = text;
        HasMessage = !string.IsNullOrWhiteSpace(text);
        MessageGlyph = success ? "\uE73E" : "\uE783";
        MessageBrush = success
            ? ResolveBrush("OptiSuccessBrush", Color.FromArgb(255, 34, 197, 94))
            : ResolveBrush("OptiErrorBrush", Color.FromArgb(255, 220, 38, 38));
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
