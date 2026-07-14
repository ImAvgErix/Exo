using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OptiHub.Helpers;
using OptiHub.ViewModels;

namespace OptiHub.Views.Controls;

public sealed partial class SettingsSheet : UserControl
{
    public event EventHandler? CloseRequested;

    public SettingsViewModel ViewModel { get; }

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

    /// <summary>Force every row fully visible at identity (re-open / fail-safe).</summary>
    public void ResetRowVisuals()
    {
        foreach (var r in MotionRows())
            OptiMotion.EnsureVisible(r);
        OptiMotion.EnsureVisible(SheetRoot);
    }

    /// <summary>
    /// Soft row stagger on open. Never leaves rows permanently primed-hidden —
    /// EnsureVisible is always applied first and again after the stagger window.
    /// </summary>
    public void PlayOpenMotion()
    {
        var gen = ++_openGeneration;
        // Always show content first so a failed animation cannot blank the sheet.
        ResetRowVisuals();

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            if (gen != _openGeneration) return;
            var rows = MotionRows();
            // Light stagger from already-visible state (PlayEnter fails → EnsureVisible).
            OptiMotion.PlayStagger(rows, baseDelayMs: 20, stepMs: 28, fromY: 6f, fromScale: 0.99f);

            // Absolute fail-safe: after stagger window, everything is fully on.
            _ = Task.Delay(500).ContinueWith(_ =>
            {
                try
                {
                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        if (gen != _openGeneration) return;
                        ResetRowVisuals();
                    });
                }
                catch { }
            });
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);
}
