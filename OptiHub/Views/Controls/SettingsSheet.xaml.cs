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

    /// <summary>Call when the overlay opens so rows stagger in (Kinetics-style).</summary>
    public void PlayOpenMotion()
    {
        _staggerPlayed = false;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_staggerPlayed) return;
            _staggerPlayed = true;
            OptiMotion.PlayStaggerIn(
            [
                RowAppearance,
                Div1,
                RowUpdates,
                Div2,
                RowVersion,
                Div3,
                RowSupport
            ], baseDelayMs: 60, stepMs: 48);
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);
}
