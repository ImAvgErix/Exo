using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OptiHub.Helpers;
using OptiHub.ViewModels;

namespace OptiHub.Views.Controls;

public sealed partial class SettingsSheet : UserControl
{
    public event EventHandler? CloseRequested;

    public SettingsViewModel ViewModel { get; }

    private bool _staggerPlayed;
    private int _openGeneration;

    public SettingsSheet()
    {
        ViewModel = new SettingsViewModel(App.Services);
        InitializeComponent();
        DataContext = ViewModel;
    }

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ConfirmUpdateAsync = (local, remote) =>
            OptiUpdateDialog.ConfirmInstallAsync(XamlRoot, local, remote);
        ViewModel.InstallUpdateAsync = check =>
            OptiUpdateDialog.InstallWithProgressAsync(XamlRoot, check, App.Services.Updater);
    }

    private UIElement[] MotionRows() =>
    [
        RowAppearance, Div1, RowMotion, Div2, RowUpdates, Div3, RowVersion, Div4, RowSupport
    ];

    /// <summary>Clear leftover composition on every row (re-open safety).</summary>
    public void ResetRowVisuals()
    {
        foreach (var r in MotionRows())
            OptiMotion.ResetVisual(r, show: true);
        OptiMotion.ResetVisual(SheetRoot, show: true);
    }

    /// <summary>Call when the overlay opens so rows stagger in (shared motion language).</summary>
    public void PlayOpenMotion()
    {
        var gen = ++_openGeneration;
        _staggerPlayed = false;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            if (gen != _openGeneration) return;
            if (_staggerPlayed) return;
            _staggerPlayed = true;
            var rows = MotionRows();
            // Soft fade+rise on rows only — host stays layout-centered.
            foreach (var r in rows)
                OptiMotion.PrimeHidden(r, fromY: 6f, fromScale: 0.99f);
            OptiMotion.PlayStagger(rows, baseDelayMs: 40, stepMs: 36, fromY: 6f, fromScale: 0.99f);
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);
}
