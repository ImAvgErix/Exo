using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OptiHub.ViewModels;

namespace OptiHub.Views;

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
        await ViewModel.RefreshAsync();
    }

    private void ApplyAll_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ApplyAllCommand.Execute(null);

    private void ApplyRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
            ViewModel.ApplyRowCommand.Execute(id);
        else
            ViewModel.ApplyAllCommand.Execute(null);
    }

    private void SetDepth_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NvidiaDisplayColorRowViewModel row })
            ViewModel.ApplyColorDepthCommand.Execute(row);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RefreshCommand.Execute(null);

    private void ClearTray_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ClearTrayCommand.Execute(null);
}
