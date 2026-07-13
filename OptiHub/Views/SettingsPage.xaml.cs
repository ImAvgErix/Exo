using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using OptiHub.ViewModels;

namespace OptiHub.Views;

public sealed partial class SettingsPage : Page
{
    private bool _entrancePlayed;

    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = new SettingsViewModel(App.Services);
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.ConfirmAsync = ConfirmAsync;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e) => PlayEntrance();

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
                Duration = TimeSpan.FromMilliseconds(420),
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
                Duration = TimeSpan.FromMilliseconds(480),
                BeginTime = delay,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(slide, transform);
            Storyboard.SetTargetProperty(slide, "TranslateY");
            sb.Children.Add(slide);
        }

        FadeSlide(HeaderPanel, HeaderTransform, 0, 10);
        FadeSlide(BodyGrid, BodyTransform, 70, 14);
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
