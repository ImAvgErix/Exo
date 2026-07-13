using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OptiHub.Models;
using OptiHub.ViewModels;

namespace OptiHub.Views;

public sealed partial class InternetOptimizerPage : Page
{
    public InternetOptimizerViewModel ViewModel { get; }

    public InternetOptimizerPage()
    {
        ViewModel = new InternetOptimizerViewModel(App.Services);
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.RequestApplyConfirm += OnRequestApplyConfirmAsync;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
    }

    private async Task<NetworkApplyOptions?> OnRequestApplyConfirmAsync(
        NetworkSnapshot snap, NetworkPreset preset)
    {
        var media = snap.Media;
        var presetLabel = preset == NetworkPreset.LowestLatency
            ? "Lowest latency"
            : "Highest download";

        var lines = new List<string>
        {
            $"Preset: {presetLabel}",
            "",
            "Smart path detection:",
            $"  {media.PolicyLine}"
        };

        if (media.EthernetInUse && media.WifiAvailable)
        {
            lines.Add("");
            lines.Add("Usable Ethernet detected (linked + IP). Wi‑Fi will be disabled — Ethernet is preferred for lowest latency.");
        }
        else if (media.EthernetUp && !media.EthernetInUse)
        {
            lines.Add("");
            lines.Add("Ethernet is linked but has no usable IPv4 yet — Wi‑Fi stays on until Ethernet gets an address.");
        }
        else if (media.WifiUp)
        {
            lines.Add("");
            lines.Add($"Wi‑Fi only. Preferred band: {media.PreferredBandTarget} (from your radio/driver).");
            if (media.ConnectedRadioHint is not "—")
                lines.Add($"  Connected radio hint: {media.ConnectedRadioHint}");
        }

        lines.Add("");
        if (media.EthernetUp || media.EthernetAvailable)
        {
            lines.Add("Some Ethernet driver properties need an adapter restart to fully apply.");
            lines.Add("Restart Ethernet adapters after apply?");
        }
        else
        {
            lines.Add("No Ethernet restart needed (Wi‑Fi is not force-restarted).");
            lines.Add("Continue with apply?");
        }

        var hasEth = media.EthernetUp || media.EthernetAvailable;
        var dialog = new ContentDialog
        {
            Title = "Apply network stack",
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = string.Join(Environment.NewLine, lines),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13
                },
                MaxHeight = 320
            },
            PrimaryButtonText = hasEth ? "Apply + restart Ethernet" : "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (hasEth)
            dialog.SecondaryButtonText = "Apply without restart";

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None)
            return null;

        return new NetworkApplyOptions
        {
            PreferEthernetDisableWifi = true,
            // Only restart when user picked primary and Ethernet exists
            RestartEthernet = result == ContentDialogResult.Primary && hasEth
        };
    }

    private void Latency_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ApplyLatencyCommand.Execute(null);

    private void Throughput_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ApplyThroughputCommand.Execute(null);

    private void Refresh_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RefreshCommand.Execute(null);
}
