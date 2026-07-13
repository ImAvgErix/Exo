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

    /// <summary>Call when the overlay opens so rows stagger in (shared motion language).</summary>
    public void PlayOpenMotion()
    {
        _staggerPlayed = false;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_staggerPlayed) return;
            _staggerPlayed = true;
            var rows = new UIElement[]
            {
                RowAppearance, Div1, RowUpdates, Div2, RowVersion, Div3, RowSupport
            };
            foreach (var r in rows)
                OptiMotion.PrimeHidden(r, fromY: 10f, fromScale: 0.98f);
            OptiMotion.PlayStagger(rows, baseDelayMs: 70, stepMs: 45, fromY: 10f, fromScale: 0.98f);
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);
}
