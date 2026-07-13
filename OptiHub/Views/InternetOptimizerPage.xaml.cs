using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
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
        ViewModel.ConfirmAsync = ConfirmRepairAsync;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
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
                FontSize = 13,
                LineHeight = 20
            },
            PrimaryButtonText = "Repair",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1)
        };
        try
        {
            dialog.Background = (Brush)Application.Current.Resources["OptiCardFillBrush"];
            dialog.BorderBrush = (Brush)Application.Current.Resources["OptiCardStrokeBrush"];
            if (Application.Current.Resources.TryGetValue("OptiPrimaryButton", out var ps) && ps is Style primary)
                dialog.PrimaryButtonStyle = primary;
            if (Application.Current.Resources.TryGetValue("OptiQuietButton", out var qs) && qs is Style quiet)
                dialog.CloseButtonStyle = quiet;
        }
        catch { }
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private void Latency_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ApplyLatencyCommand.Execute(null);

    private void Throughput_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ApplyThroughputCommand.Execute(null);

    private void Repair_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RepairCommand.Execute(null);

    private void Refresh_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RefreshCommand.Execute(null);
}
