using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using OptiHub.ViewModels;

namespace OptiHub.Views;

public sealed partial class SettingsPage : Page
{
    private bool _entrancePlayed;
    private string _category = "appearance";
    private readonly Dictionary<string, Button> _cats = new();
    private readonly Dictionary<string, UIElement> _panes = new();

    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = new SettingsViewModel(App.Services);
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.ConfirmAsync = ConfirmAsync;

        _cats["appearance"] = CatAppearance;
        _cats["updates"] = CatUpdates;
        _cats["support"] = CatSupport;
        _cats["about"] = CatAbout;

        _panes["appearance"] = PaneAppearance;
        _panes["updates"] = PaneUpdates;
        _panes["support"] = PaneSupport;
        _panes["about"] = PaneAbout;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        SelectCategory("appearance");
        PlayEntrance();
    }

    private void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key })
            SelectCategory(key);
    }

    private void SelectCategory(string key)
    {
        _category = key;

        Brush Soft() => Application.Current.Resources.TryGetValue("OptiAccentSoftBrush", out var s) && s is Brush sb
            ? sb : new SolidColorBrush(ColorHelper.FromArgb(255, 26, 26, 26));
        Brush Acc() => Application.Current.Resources.TryGetValue("OptiAccentBrush", out var a) && a is Brush ab
            ? ab : new SolidColorBrush(ColorHelper.FromArgb(255, 245, 245, 245));
        Brush Mut() => Application.Current.Resources.TryGetValue("OptiMutedTextBrush", out var m) && m is Brush mb
            ? mb : new SolidColorBrush(ColorHelper.FromArgb(255, 115, 115, 115));
        Brush Pri() => Application.Current.Resources.TryGetValue("OptiPrimaryTextBrush", out var p) && p is Brush pb
            ? pb : new SolidColorBrush(ColorHelper.FromArgb(255, 250, 250, 250));

        foreach (var kv in _cats)
        {
            var on = string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase);
            kv.Value.Background = on ? Soft() : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            kv.Value.BorderThickness = on ? new Thickness(3, 0, 0, 0) : new Thickness(0);
            kv.Value.BorderBrush = on ? Acc() : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            PaintContent(kv.Value.Content, on ? Acc() : Mut(), on ? Pri() : Mut());
        }

        foreach (var kv in _panes)
            kv.Value.Visibility = string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private static void PaintContent(object? content, Brush iconBrush, Brush labelBrush)
    {
        switch (content)
        {
            case FontIcon icon:
                icon.Foreground = iconBrush;
                break;
            case TextBlock label:
                label.Foreground = labelBrush;
                break;
            case Panel panel:
                foreach (var child in panel.Children)
                    PaintContent(child, iconBrush, labelBrush);
                break;
        }
    }

    private void ThemeDark_Click(object sender, RoutedEventArgs e) => ViewModel.IsDarkMode = true;

    private void ThemeLight_Click(object sender, RoutedEventArgs e) => ViewModel.IsLightMode = true;

    private void PlayEntrance()
    {
        if (_entrancePlayed) return;
        _entrancePlayed = true;

        var sb = new Storyboard();

        void FadeSlide(UIElement target, CompositeTransform? transform, int delayMs, double fromY)
        {
            target.Opacity = 0;
            var delay = TimeSpan.FromMilliseconds(delayMs);
            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(380),
                BeginTime = delay,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fade, target);
            Storyboard.SetTargetProperty(fade, "Opacity");
            sb.Children.Add(fade);

            if (transform is null) return;
            transform.TranslateY = fromY;
            var slide = new DoubleAnimation
            {
                From = fromY,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(440),
                BeginTime = delay,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(slide, transform);
            Storyboard.SetTargetProperty(slide, "TranslateY");
            sb.Children.Add(slide);
        }

        FadeSlide(HeaderPanel, HeaderTransform, 0, 12);
        FadeSlide(BodyGrid, BodyTransform, 80, 16);
        sb.Begin();
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "Install",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }
}
