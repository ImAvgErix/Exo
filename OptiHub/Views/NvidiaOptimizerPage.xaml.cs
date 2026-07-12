using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OptiHub.Models;
using OptiHub.ViewModels;

namespace OptiHub.Views;

public sealed partial class NvidiaOptimizerPage : Page
{
    public NvidiaOptimizerViewModel ViewModel { get; }

    public NvidiaOptimizerPage()
    {
        ViewModel = new NvidiaOptimizerViewModel(App.Services);
        InitializeComponent();
        DataContext = ViewModel;

        ViewModel.ConfirmAsync = ConfirmAsync;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
    }

    private void Run_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RunCommand.Execute(null);

    private void Repair_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RepairCommand.Execute(null);

    private void Refresh_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RefreshCommand.Execute(null);

    private async void Panel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NvidiaPanelSettingsDialog(App.Services.NvidiaPanel)
        {
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();

        // Always refresh status after panel (Fix may have changed driver state)
        if (dialog.RequestedApply)
            await ViewModel.RefreshCommand.ExecuteAsync(null);
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420
                }
            },
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }
}
