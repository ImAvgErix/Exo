using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Exo.ViewModels;

namespace Exo.Views;

public sealed partial class DiscordOptimizerPage : Page
{
    public DiscordOptimizerViewModel ViewModel { get; }

    public DiscordOptimizerPage()
    {
        ViewModel = new DiscordOptimizerViewModel(App.Services);
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
