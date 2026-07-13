using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OptiHub.ViewModels;

namespace OptiHub.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = new SettingsViewModel(App.Services);
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.ConfirmAsync = ConfirmAsync;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e) { }

    private void ThemeDark_Click(object sender, RoutedEventArgs e) => ViewModel.IsDarkMode = true;

    private void ThemeLight_Click(object sender, RoutedEventArgs e) => ViewModel.IsLightMode = true;

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "Install",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }
}
