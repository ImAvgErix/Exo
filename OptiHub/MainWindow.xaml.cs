using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
        Nvidia,
        Settings
    }

    private ShellMode _mode = ShellMode.Home;
    private readonly CancellationTokenSource _lifetimeCts = new();

    public MainWindow()
    {
        InitializeComponent();
        App.MainAppWindow = this;

        // Fixed window, roomy enough for home cards and optimizer feature lists.
        AppWindow.Resize(new SizeInt32(1080, 860));
        ApplyFixedWindowChrome();
        TryCenterOnScreen();
        TrySetWindowIcon();

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
        Closed += (_, _) =>
        {
            _lifetimeCts.Cancel();
            App.Services.Settings.Flush();
            App.MainAppWindow = null;

            try
            {
                NativeWindowHelper.RestoreWindowProcedure(WindowNative.GetWindowHandle(this));
            }
            catch
            {
                // The native window may already have been released.
            }
        };

        ApplyShellChrome();
        UpdateCaptionInset();

        NavigateHome(suppressTransition: true);
        _ = MaybeAutoUpdateAsync(_lifetimeCts.Token);
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
                 : ColorHelper.FromArgb(255, 240, 239, 238));
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
        // Always show a mark: OptiHub logo on home/settings, product logos on optimizers.
        ContextLogoHost.Visibility = Visibility.Visible;
        // Settings only on home — optimizer pages keep chrome clean
        SettingsButton.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
        AppTitleText.Text = mode switch
        {
            ShellMode.Discord => "Discord",
            ShellMode.Steam => "Steam",
            ShellMode.Nvidia => "NVIDIA",
            ShellMode.Settings => "Settings",
            _ => "OptiHub"
        };

        if (mode == ShellMode.Discord)
            TrySetContextLogo("Assets/Logos/discord.png");
        else if (mode == ShellMode.Steam)
            TrySetContextLogo("Assets/Logos/steam.png");
        else if (mode == ShellMode.Nvidia)
            TrySetContextLogo("Assets/Logos/nvidia.png");
        else
            TrySetContextLogo("Assets/Logos/optihub.png");
    }

    private void TrySetWindowIcon()
    {
        try
        {
            // Taskbar / alt-tab / title-bar system icon (ApplicationIcon alone is not always enough for WinUI unpackaged).
            var baseDir = AppContext.BaseDirectory;
            foreach (var rel in new[]
                     {
                         Path.Combine("Assets", "OptiHub.ico"),
                         Path.Combine("Assets", "Logos", "optihub.png")
                     })
            {
                var path = Path.Combine(baseDir, rel);
                if (!File.Exists(path)) continue;
                AppWindow.SetIcon(path);
                return;
            }
        }
        catch
        {
            // best-effort
        }
    }

    private void TrySetContextLogo(string relativePath)
    {
        ContextLogo.Source = AssetPathToImageSourceConverter.Resolve(relativePath);
    }

    // Continuum + drill: heavier "weight" than a flat slide (Kinetics / Amicro motion language).
    private static NavigationTransitionInfo Slide() =>
        new DrillInNavigationTransitionInfo();

    private static NavigationTransitionInfo SlideBack() =>
        new ContinuumNavigationTransitionInfo();

    public void NavigateHome(bool suppressTransition = false)
    {
        Navigate(
            ShellMode.Home,
            typeof(DashboardPage),
            suppressTransition ? (NavigationTransitionInfo)new SuppressNavigationTransitionInfo() : SlideBack());
    }

    public void NavigateToDiscord()
    {
        Navigate(ShellMode.Discord, typeof(DiscordOptimizerPage), Slide());
    }

    public void NavigateToSteam()
    {
        Navigate(ShellMode.Steam, typeof(SteamOptimizerPage), Slide());
    }

    public void NavigateToNvidia()
    {
        Navigate(ShellMode.Nvidia, typeof(NvidiaOptimizerPage), Slide());
    }

    private void Navigate(ShellMode mode, Type pageType, NavigationTransitionInfo transition)
    {
        if (_mode == mode && ContentFrame.CurrentSourcePageType == pageType)
            return;

        if (ContentFrame.Navigate(pageType, null, transition))
            ApplyChrome(mode);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == ShellMode.Settings) return;
        Navigate(ShellMode.Settings, typeof(SettingsPage), Slide());
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => NavigateHome();

    private async Task MaybeAutoUpdateAsync(CancellationToken ct)
    {
        try
        {
            // App-only: each release ships matching optimizer kits. No separate script pull.
            if (!App.Services.Settings.Current.AutoUpdateScripts) return;

            // Let the window finish loading so ContentDialog has a valid XamlRoot.
            await Task.Delay(1200, ct);
            for (var i = 0; i < 10 && RootGrid.XamlRoot is null; i++)
                await Task.Delay(200, ct);

            var appCheck = await App.Services.Updater.CheckAppUpdateAsync(ct: ct);
            if (appCheck.UpdateAvailable && RootGrid.XamlRoot is not null)
            {
                var dialog = new ContentDialog
                {
                    Title = "OptiHub update available",
                    Content =
                        $"Version {appCheck.RemoteVersion} is available.\n" +
                        $"You have {appCheck.LocalVersion}.\n\n" +
                        "Install now? OptiHub will close, update in place, and reopen.\n" +
                        "This release includes the matching optimizers.",
                    PrimaryButtonText = "Install",
                    CloseButtonText = "Later",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = RootGrid.XamlRoot
                };
                var choice = await dialog.ShowAsync();
                ct.ThrowIfCancellationRequested();
                if (choice == ContentDialogResult.Primary)
                {
                    var install = await App.Services.Updater.InstallAppUpdateAsync(appCheck, ct: ct);
                    if (install.ShouldExit)
                    {
                        await Task.Delay(900, ct);
                        Microsoft.UI.Xaml.Application.Current?.Exit();
                        return;
                    }

                    if (RootGrid.XamlRoot is not null)
                    {
                        var err = new ContentDialog
                        {
                            Title = "Update could not finish",
                            Content = install.Message,
                            CloseButtonText = "OK",
                            XamlRoot = RootGrid.XamlRoot
                        };
                        await err.ShowAsync();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Window is closing.
        }
        catch
        {
            // ignore network issues on startup
        }
    }
}
