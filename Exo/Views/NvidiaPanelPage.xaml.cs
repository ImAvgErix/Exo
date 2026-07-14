using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Exo.ViewModels;

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
        await ViewModel.RefreshAsync();
    }

    private void ApplyDisplay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NvidiaDisplayColorRowViewModel row })
            ViewModel.ApplyDisplaySettingsCommand.Execute(row);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RefreshCommand.Execute(null);
}
