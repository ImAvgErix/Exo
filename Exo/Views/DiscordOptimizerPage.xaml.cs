using System.ComponentModel;
using Exo.Helpers;
using Exo.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Exo.Views;

public sealed partial class DiscordOptimizerPage : Page
{
    private bool _tilesEntered;

    public DiscordOptimizerViewModel ViewModel { get; }

    public DiscordOptimizerPage()
    {
        NavigationCacheMode = NavigationCacheMode.Enabled;
        ViewModel = new DiscordOptimizerViewModel(App.Services);
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Defer detect so the navigation click returns immediately.
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

    /// <summary>Staggered tile entrance on the first loading → loaded transition only.</summary>
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_tilesEntered) return;
        if (e.PropertyName != nameof(ViewModel.IsFeatureListVisible) || !ViewModel.IsFeatureListVisible)
            return;
        _tilesEntered = true;
        ExoMotion.PlayListEnter(Plate.FeatureTileGrid.TileRepeaterControl, ViewModel.Features.Count);
    }

    private void Run_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RunCommand.Execute(null);

    private void Repair_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RepairCommand.Execute(null);

    private void ToggleReport_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ToggleApplyReportCommand.Execute(null);
}
