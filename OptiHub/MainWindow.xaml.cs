using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using OptiHub.Helpers;
using OptiHub.Views;
using Windows.Graphics;
using WinRT.Interop;

namespace OptiHub;

public sealed partial class MainWindow : Window
{
    private enum ShellMode
    {
        Home,
        Discord,
        Settings
    }

    private ShellMode _mode = ShellMode.Home;

    public MainWindow()
    {
        InitializeComponent();
        App.MainAppWindow = this;

        AppWindow.Resize(new SizeInt32(1100, 720));
        TryCenterOnScreen();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarHost);

        RootGrid.ActualThemeChanged += (_, _) => ApplyShellChrome();
        ApplyShellChrome();
        PlayBackdropPulse();

        NavigateHome(suppressTransition: true);
        _ = MaybeAutoUpdateAsync();
    }

    private void ApplyShellChrome()
    {
        var dark = RootGrid.ActualTheme != ElementTheme.Light;
        RootGrid.Background = new SolidColorBrush(
            dark ? ColorHelper.FromArgb(255, 0, 0, 0)
                 : ColorHelper.FromArgb(255, 244, 244, 245));
        App.Services.Theme.Apply();
    }

    private void PlayBackdropPulse()
    {
        try
        {
            var anim = new DoubleAnimation
            {
                From = 0.4,
                To = 0.7,
                Duration = new Duration(TimeSpan.FromSeconds(4.5)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(anim, BackdropOrb);
            Storyboard.SetTargetProperty(anim, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(anim);
            sb.Begin();
        }
        catch
        {
            // animation is decorative
        }
    }

    private void TryCenterOnScreen()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var id = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(id);
            var display = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Nearest);
            if (display is null) return;
            var work = display.WorkArea;
            var x = work.X + (work.Width - appWindow.Size.Width) / 2;
            var y = work.Y + (work.Height - appWindow.Size.Height) / 2;
            appWindow.Move(new PointInt32(x, y));
        }
        catch
        {
            // best-effort
        }
    }

    private void ApplyChrome(ShellMode mode)
    {
        _mode = mode;
        var home = mode == ShellMode.Home;
        HomeChrome.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
        ContextChrome.Visibility = home ? Visibility.Collapsed : Visibility.Visible;
        SettingsButton.Visibility = home ? Visibility.Visible : Visibility.Collapsed;

        switch (mode)
        {
            case ShellMode.Discord:
                ContextTitleText.Text = "Discord Optimizer";
                ContextLogoHost.Visibility = Visibility.Visible;
                TrySetContextLogo("Assets/Logos/discord.png");
                break;
            case ShellMode.Settings:
                ContextTitleText.Text = "Settings";
                ContextLogoHost.Visibility = Visibility.Collapsed;
                ContextLogo.Source = null;
                break;
            default:
                ContextTitleText.Text = string.Empty;
                ContextLogoHost.Visibility = Visibility.Collapsed;
                ContextLogo.Source = null;
                break;
        }
    }

    private void TrySetContextLogo(string relativePath)
    {
        try
        {
            var full = Path.Combine(PathHelper.AppDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                ContextLogo.Source = null;
                return;
            }
            ContextLogo.Source = new BitmapImage(new Uri(full));
        }
        catch
        {
            ContextLogo.Source = null;
        }
    }

    private static NavigationTransitionInfo Slide() =>
        new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight };

    private static NavigationTransitionInfo SlideBack() =>
        new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft };

    public void NavigateHome(bool suppressTransition = false)
    {
        ApplyChrome(ShellMode.Home);
        ContentFrame.Navigate(
            typeof(DashboardPage),
            null,
            suppressTransition ? (NavigationTransitionInfo)new SuppressNavigationTransitionInfo() : SlideBack());
    }

    public void NavigateToDashboard() => NavigateHome();

    public void NavigateToDiscord()
    {
        ApplyChrome(ShellMode.Discord);
        ContentFrame.Navigate(typeof(DiscordOptimizerPage), null, Slide());
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == ShellMode.Settings) return;
        ApplyChrome(ShellMode.Settings);
        ContentFrame.Navigate(typeof(SettingsPage), null, Slide());
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => NavigateHome();

    private async Task MaybeAutoUpdateAsync()
    {
        try
        {
            if (!App.Services.Settings.Current.AutoUpdateScripts) return;
            await Task.Delay(8000);
            await App.Services.Updater.CheckAndUpdateDiscordScriptsAsync(force: false);
        }
        catch
        {
            // ignore network issues on startup
        }
    }
}
