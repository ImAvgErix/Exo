using Exo.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Exo.Views;

public sealed partial class InternetOptimizerPage : Page
{
    public InternetOptimizerViewModel ViewModel { get; }

    public InternetOptimizerPage()
    {
        NavigationCacheMode = NavigationCacheMode.Enabled;
        ViewModel = new InternetOptimizerViewModel(App.Services);
        InitializeComponent();
        DataContext = ViewModel;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _ = ViewModel.InitializeAsync();
        });
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.CancelBackgroundWork();
        base.OnNavigatedFrom(e);
    }

    private void Repair_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RepairCommand.Execute(null);
}
