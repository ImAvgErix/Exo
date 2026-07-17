using System.ComponentModel;
using Exo.Helpers;
using Exo.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Exo.Views;

public sealed partial class NvidiaOptimizerPage : Page
{
    private bool _tilesEntered;

    public NvidiaOptimizerViewModel ViewModel { get; }

    public NvidiaOptimizerPage()
    {
        ViewModel = new NvidiaOptimizerViewModel(App.Services);
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
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
        try { ExoMotion.EnsureVisible(this); } catch { }
        await ViewModel.InitializeAsync();
    }

    /// <summary>Staggered tile entrance on the first loading → loaded transition only.</summary>
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_tilesEntered) return;
        if (e.PropertyName != nameof(ViewModel.IsFeatureListVisible) || !ViewModel.IsFeatureListVisible)
            return;
        _tilesEntered = true;
        try
        {
            // After a heavy driver Apply, skip stagger if motion was freeze-killed.
            if (ExoMotion.MotionDisabled)
            {
                ExoMotion.EnsureVisible(Plate);
                return;
            }
            ExoMotion.PlayListEnter(Plate.FeatureTileGrid.TileRepeaterControl, ViewModel.Features.Count);
        }
        catch
        {
            try { ExoMotion.EnsureVisible(Plate); } catch { }
        }
    }

    private void Run_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RunCommand.Execute(null);

    private void Repair_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RepairCommand.Execute(null);

}
