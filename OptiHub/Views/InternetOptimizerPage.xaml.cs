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

        if (media.EthernetUp && media.WifiAvailable)
        {
            lines.Add("");
            lines.Add("Ethernet is linked. Wi‑Fi adapters will be disabled so traffic uses Ethernet (best for gaming).");
        }
        else if (!media.EthernetUp && media.WifiUp)
        {
            lines.Add("");
            lines.Add($"Wi‑Fi only. Preferred band target: {media.PreferredBandTarget} (based on your adapter, not a cloud model).");
            if (media.ConnectedRadioHint is not "—")
                lines.Add($"  Connected radio hint: {media.ConnectedRadioHint}");
        }

        lines.Add("");
        lines.Add("Some driver properties need an Ethernet adapter restart to fully apply.");
        lines.Add("Restart Ethernet adapters after apply?");

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
            PrimaryButtonText = "Apply + restart Ethernet",
            SecondaryButtonText = "Apply without restart",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None)
            return null;

        return new NetworkApplyOptions
        {
            PreferEthernetDisableWifi = true,
            RestartEthernet = result == ContentDialogResult.Primary
        };
    }

    private void Latency_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ApplyLatencyCommand.Execute(null);

    private void Throughput_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ApplyThroughputCommand.Execute(null);

    private void Refresh_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RefreshCommand.Execute(null);
}
