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

        ViewModel.RequestGoBack += (_, _) =>
        {
            if (App.MainAppWindow is MainWindow mw)
                mw.NavigateToDashboard();
        };
    }

    private void CheckScriptUpdates_Click(object sender, RoutedEventArgs e) =>
        ViewModel.CheckScriptUpdatesCommand.Execute(null);

    private void CheckAppUpdates_Click(object sender, RoutedEventArgs e) =>
        ViewModel.CheckAppUpdatesCommand.Execute(null);
}
