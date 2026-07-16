using Exo.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Exo.Views;

public sealed partial class NvidiaPanelPage : Page
{
    public NvidiaPanelViewModel ViewModel { get; }

    public NvidiaPanelPage()
    {
        ViewModel = new NvidiaPanelViewModel(App.Services);
        InitializeComponent();
        DataContext = ViewModel;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            if (App.MainAppWindow is MainWindow main)
                main.StabilizeShellAfterExternalWork();
        }
        catch { }
        try { Helpers.ExoMotion.EnsureVisible(this); } catch { }
        try
        {
            await ViewModel.RefreshAsync();
        }
        catch (Exception ex)
        {
            // Display helper missing / NVAPI down must not blank the shell.
            ViewModel.HeaderStatus = "Display panel unavailable";
            ViewModel.HeaderDetail = ex.Message;
            ViewModel.IsLoading = false;
        }
    }

    private void ApplyDisplay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NvidiaDisplayColorRowViewModel row })
            ViewModel.ApplyDisplaySettingsCommand.Execute(row);
    }

    private void OpenControlPanel_Click(object sender, RoutedEventArgs e) =>
        ViewModel.OpenControlPanelCommand.Execute(null);

    private void Refresh_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RefreshCommand.Execute(null);
}
