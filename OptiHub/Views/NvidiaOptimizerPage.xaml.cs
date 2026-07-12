using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OptiHub.ViewModels;

namespace OptiHub.Views;

public sealed partial class NvidiaOptimizerPage : Page
{
    public NvidiaOptimizerViewModel ViewModel { get; }

    public NvidiaOptimizerPage()
    {
        ViewModel = new NvidiaOptimizerViewModel(App.Services);
        InitializeComponent();
        DataContext = ViewModel;

        ViewModel.ConfirmAsync = ConfirmAsync;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
    }

    private void Run_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RunCommand.Execute(null);

    private void Repair_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RepairCommand.Execute(null);

    private void Refresh_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RefreshCommand.Execute(null);

    private async void Panel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NvidiaPanelSettingsDialog(App.Services.NvidiaPanel)
        {
            XamlRoot = XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return;

        var settings = dialog.CaptureSettings();
        ViewModel.IsBusy = true;
        ViewModel.IsProgressVisible = true;
        ViewModel.ProgressPercent = 10;
        ViewModel.ProgressStatus = "Applying OptiHub NVIDIA panel...";
        try
        {
            var progress = new Progress<Models.ScriptRunProgress>(p =>
            {
                if (p.Percent >= ViewModel.ProgressPercent)
                    ViewModel.ProgressPercent = p.Percent;
                if (!string.IsNullOrWhiteSpace(p.Status))
                    ViewModel.ProgressStatus = p.Status;
            });
            var (ok, message) = await App.Services.NvidiaPanel.ApplyDisplayPolicyAsync(settings, progress);
            ViewModel.ProgressPercent = 100;
            ViewModel.ProgressStatus = ok ? "Panel applied" : "Panel apply failed";
            // Reflect via refresh
            await ViewModel.RefreshCommand.ExecuteAsync(null);
            // Show result through status detail if refresh didn't clear busy
            var tip = new ContentDialog
            {
                Title = ok ? "NVIDIA panel applied" : "Apply failed",
                Content = new TextBlock { Text = message + "\n\n" + settings.Summary, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            };
            await tip.ShowAsync();
        }
        finally
        {
            ViewModel.IsBusy = false;
            ViewModel.IsProgressVisible = false;
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420
                }
            },
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }
}
