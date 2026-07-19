using System.ComponentModel;
using Exo.Helpers;
using Exo.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Exo.Views;

public sealed partial class RiotOptimizerPage : Page
{
    private bool _tilesEntered;
    public GameLauncherOptimizerViewModel ViewModel { get; }

    public RiotOptimizerPage()
    {
        NavigationCacheMode = NavigationCacheMode.Enabled;
        ViewModel = new GameLauncherOptimizerViewModel(App.Services, "Riot");
        InitializeComponent();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
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

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_tilesEntered || e.PropertyName != nameof(ViewModel.IsFeatureListVisible) || !ViewModel.IsFeatureListVisible) return;
        _tilesEntered = true;
        ExoMotion.PlayListEnter(Plate.FeatureTileGrid.TileRepeaterControl, ViewModel.Features.Count);
    }

    private void Run_Click(object sender, RoutedEventArgs e) => ViewModel.RunCommand.Execute(null);
    private void Repair_Click(object sender, RoutedEventArgs e) => ViewModel.RepairCommand.Execute(null);
    private void ToggleReport_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleApplyReportCommand.Execute(null);
}
