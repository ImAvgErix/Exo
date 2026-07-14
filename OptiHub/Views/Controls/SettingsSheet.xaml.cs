using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OptiHub.Helpers;
using OptiHub.ViewModels;

namespace OptiHub.Views.Controls;

public sealed partial class SettingsSheet : UserControl
{
    public event EventHandler? CloseRequested;

    public SettingsViewModel ViewModel { get; }

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
        ResetRowVisuals();
    }

    private UIElement[] MotionRows() =>
    [
        RowAppearance, Div1, RowMotion, Div2, RowUpdates, Div3, RowVersion, Div4, RowSupport
    ];

    public void ResetRowVisuals()
    {
        foreach (var r in MotionRows())
            OptiMotion.EnsureVisible(r);
        OptiMotion.EnsureVisible(SheetRoot);
    }

    /// <summary>Open: force everything visible. No stagger that can blank rows.</summary>
    public void PlayOpenMotion() => ResetRowVisuals();

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);
}
