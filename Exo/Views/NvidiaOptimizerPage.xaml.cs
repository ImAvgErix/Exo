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
        NavigationCacheMode = NavigationCacheMode.Enabled;
        ViewModel = new NvidiaOptimizerViewModel(App.Services);
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            if (App.MainAppWindow is MainWindow main)
                main.StabilizeShellAfterExternalWork();
        }
        catch { }
        try { ExoMotion.EnsureVisible(this); } catch { }
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
        if (_tilesEntered) return;
        if (e.PropertyName != nameof(ViewModel.IsFeatureListVisible) || !ViewModel.IsFeatureListVisible)
            return;
        _tilesEntered = true;
        try
        {
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
