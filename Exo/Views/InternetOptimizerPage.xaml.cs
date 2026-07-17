using System.ComponentModel;
using Exo.Helpers;
using Exo.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Exo.Views;

public sealed partial class InternetOptimizerPage : Page
{
    private bool _tilesEntered;

    public InternetOptimizerViewModel ViewModel { get; }

    public InternetOptimizerPage()
    {
        ViewModel = new InternetOptimizerViewModel(App.Services);
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
    }

    /// <summary>Staggered tile entrance on the first loading → loaded transition only.</summary>
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_tilesEntered) return;
        if (e.PropertyName != nameof(ViewModel.IsFeatureListVisible) || !ViewModel.IsFeatureListVisible)
            return;
        _tilesEntered = true;
        ExoMotion.PlayListEnter(Plate.FeatureTileGrid.TileRepeaterControl, ViewModel.Rows.Count);
    }

    private void Repair_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RepairCommand.Execute(null);

    private void Refresh_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RefreshCommand.Execute(null);

    private void ToggleReport_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ToggleApplyReportCommand.Execute(null);
}
