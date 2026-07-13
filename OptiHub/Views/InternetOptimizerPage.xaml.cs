using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OptiHub.Models;
using OptiHub.ViewModels;

namespace OptiHub.Views;

public sealed partial class InternetOptimizerPage : Page
{
    public InternetOptimizerViewModel ViewModel { get; }

    public InternetOptimizerPage()
    {
        ViewModel = new InternetOptimizerViewModel(App.Services);
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.RequestPresetChoice += OnRequestPresetChoiceAsync;
        ViewModel.ConfirmAsync = ConfirmRepairAsync;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
    }

    /// <summary>
    /// Minimal choice UI: two product-style cards (not a stock 3-button ContentDialog row).
    /// </summary>
    private async Task<NetworkPreset?> OnRequestPresetChoiceAsync()
    {
        var path = ViewModel.HeaderStatus;
        var tcs = new TaskCompletionSource<NetworkPreset?>();

        var root = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 400,
            MinWidth = 320
        };

        root.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(path) ? "Pick a path" : path,
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        });

        Button MakeOption(string title, string blurb, NetworkPreset preset, bool primary)
        {
            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            var blurbBlock = new TextBlock
            {
                Text = blurb,
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
            var panel = new StackPanel { Spacing = 0, Children = { titleBlock, blurbBlock } };

            var border = new Border
            {
                Child = panel,
                Padding = new Thickness(16, 14, 16, 14),
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1)
            };
            try
            {
                border.Background = (Brush)Application.Current.Resources[
                    primary ? "OptiAccentSoftBrush" : "OptiCardFillBrush"];
                border.BorderBrush = (Brush)Application.Current.Resources[
                    primary ? "OptiAccentBrush" : "OptiCardStrokeBrush"];
                titleBlock.Foreground = (Brush)Application.Current.Resources["OptiPrimaryTextBrush"];
                blurbBlock.Foreground = (Brush)Application.Current.Resources["OptiSecondaryTextBrush"];
            }
            catch { }

            var btn = new Button
            {
                Content = border,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(14)
            };
            btn.Click += (_, _) => tcs.TrySetResult(preset);
            return btn;
        }

        root.Children.Add(MakeOption(
            "Lowest latency",
            "Gaming / competitive — Nagle off, tight NIC, low lag.",
            NetworkPreset.LowestLatency,
            primary: true));
        root.Children.Add(MakeOption(
            "Highest download",
            "Bulk transfers — experimental auto-tune, LSO/RSC on.",
            NetworkPreset.HighestThroughput,
            primary: false));

        var dialog = new ContentDialog
        {
            Title = "Apply",
            Content = root,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1)
        };
        try
        {
            dialog.Background = (Brush)Application.Current.Resources["OptiCardFillBrush"];
            dialog.BorderBrush = (Brush)Application.Current.Resources["OptiCardStrokeBrush"];
            dialog.Foreground = (Brush)Application.Current.Resources["OptiPrimaryTextBrush"];
            if (Application.Current.Resources.TryGetValue("OptiQuietButton", out var qs) && qs is Style quiet)
                dialog.CloseButtonStyle = quiet;
        }
        catch { }

        // Race: card click sets result then we Hide; Cancel returns null via ShowAsync.
        var showTask = dialog.ShowAsync().AsTask();
        var completed = await Task.WhenAny(tcs.Task, showTask);
        if (completed == tcs.Task)
        {
            try { dialog.Hide(); } catch { }
            return await tcs.Task;
        }

        // User hit Cancel / dismissed
        tcs.TrySetResult(null);
        return null;
    }

    private async Task<bool> ConfirmRepairAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 400,
                FontSize = 13,
                LineHeight = 20
            },
            PrimaryButtonText = "Repair",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1)
        };
        try
        {
            dialog.Background = (Brush)Application.Current.Resources["OptiCardFillBrush"];
            dialog.BorderBrush = (Brush)Application.Current.Resources["OptiCardStrokeBrush"];
            if (Application.Current.Resources.TryGetValue("OptiPrimaryButton", out var ps) && ps is Style primary)
                dialog.PrimaryButtonStyle = primary;
            if (Application.Current.Resources.TryGetValue("OptiQuietButton", out var qs) && qs is Style quiet)
                dialog.CloseButtonStyle = quiet;
        }
        catch { }
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private void Apply_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ApplyCommand.Execute(null);

    private void Repair_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RepairCommand.Execute(null);

    private void Refresh_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RefreshCommand.Execute(null);
}
