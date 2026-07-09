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
        Steam,
        Settings
    }

    private ShellMode _mode = ShellMode.Home;

    public MainWindow()
    {
        InitializeComponent();
        App.MainAppWindow = this;

        // Fixed window — roomy enough for home cards + Discord features without scroll
        AppWindow.Resize(new SizeInt32(1020, 740));
        ApplyFixedWindowChrome();
        TryCenterOnScreen();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarHost);

        AppWindow.Changed += (_, args) =>
        {
            UpdateCaptionInset();
            // Re-assert fixed chrome if the system tries to maximize via title-bar double-click
            if (args.DidPresenterChange || args.DidSizeChange)
                ApplyFixedWindowChrome();
        };
        RootGrid.Loaded += (_, _) =>
        {
            UpdateCaptionInset();
            ApplyFixedWindowChrome();
        };
        RootGrid.SizeChanged += (_, _) => UpdateCaptionInset();
        RootGrid.ActualThemeChanged += (_, _) => ApplyShellChrome();

        ApplyShellChrome();
        UpdateCaptionInset();

        NavigateHome(suppressTransition: true);
        _ = MaybeAutoUpdateAsync();
    }

    private void ApplyFixedWindowChrome()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
            presenter.IsMinimizable = true;
            if (presenter.State == OverlappedPresenterState.Maximized)
                presenter.Restore();
        }

        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            NativeWindowHelper.DisableMaximizeViaSystemMenu(hwnd);
        }
        catch
        {
            // best-effort
        }
    }

    private void UpdateCaptionInset()
    {
        try
        {
            var right = AppWindow.TitleBar.RightInset;
            if (right < 100) right = 138;
            CaptionSpacer.Width = new GridLength(right);
        }
        catch
        {
            CaptionSpacer.Width = new GridLength(138);
        }
    }

    private void ApplyShellChrome()
    {
        var dark = RootGrid.ActualTheme != ElementTheme.Light;
        RootGrid.Background = new SolidColorBrush(
            dark ? ColorHelper.FromArgb(255, 11, 11, 12)
                 : ColorHelper.FromArgb(255, 245, 245, 244));
        App.Services.Theme.Apply();
        UpdateCaptionInset();
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
        BackButton.Visibility = home ? Visibility.Collapsed : Visibility.Visible;
        ContextLogoHost.Visibility = mode is ShellMode.Discord or ShellMode.Steam
            ? Visibility.Visible
            : Visibility.Collapsed;
        // Settings only on home — optimizer pages keep chrome clean
        SettingsButton.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
        AppTitleText.Text = mode switch
        {
            ShellMode.Discord => "Discord",
            ShellMode.Steam => "Steam",
            ShellMode.Settings => "Settings",
            _ => "OptiHub"
        };

        if (mode == ShellMode.Discord)
            TrySetContextLogo("Assets/Logos/discord.png");
        else if (mode == ShellMode.Steam)
            TrySetContextLogo("Assets/Logos/steam.png");
        else
            ContextLogo.Source = null;
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

    public void NavigateToSteam()
    {
        ApplyChrome(ShellMode.Steam);
        ContentFrame.Navigate(typeof(SteamOptimizerPage), null, Slide());
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
