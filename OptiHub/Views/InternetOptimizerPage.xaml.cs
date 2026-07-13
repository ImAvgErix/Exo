using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
    }

    private void Latency_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ApplyLatencyCommand.Execute(null);

    private void Throughput_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ApplyThroughputCommand.Execute(null);

    private void Refresh_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RefreshCommand.Execute(null);
}
