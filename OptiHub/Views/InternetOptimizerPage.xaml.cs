using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
        // Only prompt that remains: latency vs download. No second confirm.
        ViewModel.RequestPresetChoice += OnRequestPresetChoiceAsync;
        ViewModel.ConfirmAsync = ConfirmRepairAsync;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Detect Ethernet vs Wi‑Fi path as soon as the page opens.
        await ViewModel.InitializeAsync();
    }

    /// <summary>Only choice: lowest latency vs highest download — then apply runs immediately.</summary>
    private async Task<NetworkPreset?> OnRequestPresetChoiceAsync()
    {
        var path = ViewModel.HeaderStatus;
        var body = new StackPanel { Spacing = 10, MaxWidth = 400 };
        body.Children.Add(new TextBlock
        {
            Text = "Which path do you want?",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(new TextBlock
        {
            Text =
                "Lowest latency — gaming / competitive (Nagle off, RSC/LSO off, tight NIC).\n\n" +
                "Highest download — bulk transfers (auto-tune experimental, LSO/RSC on).\n\n" +
                $"Detected: {path}",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.9,
            LineHeight = 20
        });

        var dialog = new ContentDialog
        {
            Title = "Apply network stack",
            Content = body,
            PrimaryButtonText = "Lowest latency",
            SecondaryButtonText = "Highest download",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            CornerRadius = new CornerRadius(16)
        };
        try
        {
            if (Application.Current.Resources.TryGetValue("OptiCardFillBrush", out var bg) && bg is Brush b)
                dialog.Background = b;
            if (Application.Current.Resources.TryGetValue("OptiPrimaryButton", out var ps) && ps is Style primary)
                dialog.PrimaryButtonStyle = primary;
            if (Application.Current.Resources.TryGetValue("OptiQuietButton", out var qs) && qs is Style quiet)
            {
                dialog.SecondaryButtonStyle = quiet;
                dialog.CloseButtonStyle = quiet;
            }
        }
        catch { }

        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => NetworkPreset.LowestLatency,
            ContentDialogResult.Secondary => NetworkPreset.HighestThroughput,
            _ => null
        };
    }

    private async Task<bool> ConfirmRepairAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 400,
                FontSize = 13
            },
            PrimaryButtonText = "Repair",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            CornerRadius = new CornerRadius(16)
        };
        try
        {
            if (Application.Current.Resources.TryGetValue("OptiPrimaryButton", out var ps) && ps is Style primary)
                dialog.PrimaryButtonStyle = primary;
            if (Application.Current.Resources.TryGetValue("OptiQuietButton", out var qs) && qs is Style quiet)
                dialog.CloseButtonStyle = quiet;
        }
        catch { }
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private void Apply_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ApplyCommand.Execute(null);

    private void Repair_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RepairCommand.Execute(null);

    private void Refresh_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RefreshCommand.Execute(null);
}
