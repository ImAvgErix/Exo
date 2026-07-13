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
        Internet,
        Nvidia,
        NvidiaPanel,
        Settings
    }

    private ShellMode _mode = ShellMode.Home;
    private readonly CancellationTokenSource _lifetimeCts = new();

    public MainWindow()
    {
        InitializeComponent();
        App.MainAppWindow = this;

        // Default open size only — user may freely resize and maximize.
        AppWindow.Resize(new SizeInt32(1280, 820));
        ApplyResizableWindowChrome();
        TryCenterOnScreen();
        TrySetWindowIcon();

        ExtendsContentIntoTitleBar = true;
        // Only the empty middle strip is draggable. Chrome buttons sit outside
        // so they receive real pointer hits (not non-client drag / maximize).
        SetTitleBar(TitleBarDragRegion);

        AppWindow.Changed += (_, args) =>
        {
            UpdateCaptionInset();
            // Never re-lock size after user resize/maximize.
            if (args.DidPresenterChange)
                ApplyResizableWindowChrome();
        };
        RootGrid.Loaded += (_, _) =>
        {
            UpdateCaptionInset();
            ApplyResizableWindowChrome();
            ClearChromeFocus();
        };
        RootGrid.SizeChanged += (_, _) => UpdateCaptionInset();
        RootGrid.ActualThemeChanged += (_, _) => ApplyShellChrome();
        Activated += OnWindowActivatedClearFocus;
        Closed += (_, _) =>
        {
            _lifetimeCts.Cancel();
            App.Services.Settings.Flush();
            App.MainAppWindow = null;
        };

        ApplyShellChrome();
        UpdateCaptionInset();

        NavigateHome(suppressTransition: true);
        ClearChromeFocus();
        _ = MaybeAutoUpdateAsync(_lifetimeCts.Token);
    }

    private bool _clearedInitialFocus;

    private void OnWindowActivatedClearFocus(object sender, WindowActivatedEventArgs args)
    {
        if (_clearedInitialFocus) return;
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
        _clearedInitialFocus = true;
        Activated -= OnWindowActivatedClearFocus;
        DispatcherQueue.TryEnqueue(() => ClearChromeFocus());
    }

    private void ClearChromeFocus()
    {
        try
        {
            ContentFrame.IsTabStop = true;
            if (ContentFrame.Content is UIElement page)
            {
                page.IsTabStop = true;
                _ = page.Focus(FocusState.Programmatic);
            }
            else
            {
                _ = ContentFrame.Focus(FocusState.Programmatic);
            }
        }
        catch { }
    }

    /// <summary>User-resizable shell: maximize + edge drag allowed. Sensible minimum only.</summary>
    private void ApplyResizableWindowChrome()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = true;
            presenter.IsResizable = true;
            presenter.IsMinimizable = true;
            // Soft floor so chrome never collapses; not a fixed frame.
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 560;
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
        try
        {
            if (Application.Current.Resources.TryGetValue("OptiPageBackgroundBrush", out var b) && b is Brush brush)
                RootGrid.Background = brush;
        }
        catch
        {
            RootGrid.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 7, 8, 11));
        }
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
        catch { }
    }

    private void ApplyChrome(ShellMode mode)
    {
        _mode = mode;
        var home = mode == ShellMode.Home;
        var optimizer = mode is ShellMode.Discord or ShellMode.Steam or ShellMode.Internet
            or ShellMode.Nvidia or ShellMode.NvidiaPanel;

        BackButton.Visibility = home ? Visibility.Collapsed : Visibility.Visible;
        SettingsButton.Visibility = home ? Visibility.Visible : Visibility.Collapsed;

        // No logo + name in the title bar on optimizer pages — the page already shows
        // the module title (DISCORD / STEAM / …). Avoids top-left duplicate chrome.
        ContextLogoHost.Visibility = Visibility.Collapsed;
        ContextLogo.Source = null;

        if (mode == ShellMode.Settings)
        {
            AppTitleText.Text = "Settings";
            AppTitleText.Visibility = Visibility.Visible;
        }
        else
        {
            AppTitleText.Text = string.Empty;
            AppTitleText.Visibility = Visibility.Collapsed;
        }
    }

    private void TrySetWindowIcon()
    {
        try
        {
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
        catch { }
    }

    private void TrySetContextLogo(string relativePath)
    {
        ContextLogo.Source = AssetPathToImageSourceConverter.Resolve(relativePath);
    }

    // Suppress transitions so navigation feels like one click (no double-press wait).
    private static NavigationTransitionInfo Slide() =>
        new SuppressNavigationTransitionInfo();

    private static NavigationTransitionInfo SlideBack() =>
        new SuppressNavigationTransitionInfo();

    public void NavigateHome(bool suppressTransition = false)
    {
        Navigate(
            ShellMode.Home,
            typeof(DashboardPage),
            suppressTransition ? (NavigationTransitionInfo)new SuppressNavigationTransitionInfo() : SlideBack());
    }

    public void NavigateToDiscord() =>
        Navigate(ShellMode.Discord, typeof(DiscordOptimizerPage), Slide());

    public void NavigateToSteam() =>
        Navigate(ShellMode.Steam, typeof(SteamOptimizerPage), Slide());

    public void NavigateToInternet() =>
        Navigate(ShellMode.Internet, typeof(InternetOptimizerPage), Slide());

    public void NavigateToNvidia() =>
        Navigate(ShellMode.Nvidia, typeof(NvidiaOptimizerPage), Slide());

    public void NavigateToNvidiaPanel() =>
        Navigate(ShellMode.NvidiaPanel, typeof(NvidiaPanelPage), Slide());

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

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == ShellMode.NvidiaPanel)
            NavigateToNvidia();
        else
            NavigateHome();
    }

    private async Task MaybeAutoUpdateAsync(CancellationToken ct)
    {
        try
        {
            if (!App.Services.Settings.Current.AutoUpdateScripts) return;

            await Task.Delay(1200, ct);
            for (var i = 0; i < 10 && RootGrid.XamlRoot is null; i++)
                await Task.Delay(200, ct);

            if (RootGrid.XamlRoot is null) return;

            var appCheck = await App.Services.Updater.CheckAppUpdateAsync(ct: ct);
            if (!appCheck.UpdateAvailable) return;

            var installNow = await OptiUpdateDialog.ConfirmInstallAsync(
                RootGrid.XamlRoot,
                appCheck.LocalVersion,
                appCheck.RemoteVersion);
            ct.ThrowIfCancellationRequested();
            if (!installNow) return;

            var install = await OptiUpdateDialog.InstallWithProgressAsync(
                RootGrid.XamlRoot,
                appCheck,
                App.Services.Updater,
                ct);
            if (install.ShouldExit)
            {
                await Task.Delay(400, ct);
                Application.Current?.Exit();
                return;
            }

            await OptiUpdateDialog.ShowMessageAsync(
                RootGrid.XamlRoot,
                "Update could not finish",
                install.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { }
    }
}
