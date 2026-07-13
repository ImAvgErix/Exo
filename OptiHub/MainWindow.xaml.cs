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
        NvidiaPanel
    }

    private ShellMode _mode = ShellMode.Home;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private bool _settingsOpen;

    public MainWindow()
    {
        InitializeComponent();
        App.MainAppWindow = this;

        // Fixed shell — no maximize / no edge-resize (UI is designed for this frame).
        AppWindow.Resize(new SizeInt32(1180, 760));
        ApplyFixedWindowChrome();
        TryCenterOnScreen();
        TrySetWindowIcon();

        ExtendsContentIntoTitleBar = true;
        // Only the empty middle strip is draggable. Chrome buttons sit outside
        // so they receive real pointer hits (not non-client drag / maximize).
        SetTitleBar(TitleBarDragRegion);

        AppWindow.Changed += (_, args) =>
        {
            UpdateCaptionInset();
            // Re-apply fixed chrome if the system swaps presenters.
            if (args.DidPresenterChange)
                ApplyFixedWindowChrome();
        };
        RootGrid.Loaded += (_, _) =>
        {
            UpdateCaptionInset();
            ApplyFixedWindowChrome();
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

    /// <summary>Fixed shell: minimize only — no maximize, no free resize.</summary>
    private void ApplyFixedWindowChrome()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
            presenter.IsMinimizable = true;
            // Pin preferred size so the OS doesn't grow the frame.
            presenter.PreferredMinimumWidth = 1180;
            presenter.PreferredMinimumHeight = 760;
            presenter.PreferredMaximumWidth = 1180;
            presenter.PreferredMaximumHeight = 760;
        }

        try
        {
            // Re-assert size if something tried to change it.
            if (AppWindow.Size.Width != 1180 || AppWindow.Size.Height != 760)
                AppWindow.Resize(new SizeInt32(1180, 760));
        }
        catch { }
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

        // No logo/title next to Back — each page owns its own header (Settings, DISCORD, …).
        ContextLogoHost.Visibility = Visibility.Collapsed;
        ContextLogo.Source = null;
        AppTitleText.Text = string.Empty;
        AppTitleText.Visibility = Visibility.Collapsed;
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
        if (_settingsOpen)
            CloseSettingsOverlay();

        if (_mode == mode && ContentFrame.CurrentSourcePageType == pageType)
            return;

        if (ContentFrame.Navigate(pageType, null, transition))
            ApplyChrome(mode);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        OpenSettingsOverlay();

    private void SettingsSheet_CloseRequested(object? sender, EventArgs e) =>
        CloseSettingsOverlay();

    private void SettingsScrim_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) =>
        // Sheet is a sibling above the scrim — this only fires for backdrop hits.
        CloseSettingsOverlay();

    private void OpenSettingsOverlay()
    {
        if (_settingsOpen) return;
        // Always show home under the sheet so the blur has product chrome behind it.
        if (_mode != ShellMode.Home)
            NavigateHome(suppressTransition: true);

        _settingsOpen = true;
        SettingsOverlay.Visibility = Visibility.Visible;
        SettingsOverlay.Opacity = 0;
        SettingsSheetHost.Opacity = 0;
        if (SettingsSheetHost.RenderTransform is not CompositeTransform)
            SettingsSheetHost.RenderTransform = new CompositeTransform();
        if (SettingsSheetHost.RenderTransform is CompositeTransform ct)
            ct.TranslateY = 18;

        var sb = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fade, SettingsOverlay);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);

        var sheetFade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(220),
            BeginTime = TimeSpan.FromMilliseconds(40),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(sheetFade, SettingsSheetHost);
        Storyboard.SetTargetProperty(sheetFade, "Opacity");
        sb.Children.Add(sheetFade);

        if (SettingsSheetHost.RenderTransform is CompositeTransform sheetTf)
        {
            var slide = new DoubleAnimation
            {
                From = 18,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(240),
                BeginTime = TimeSpan.FromMilliseconds(40),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(slide, sheetTf);
            Storyboard.SetTargetProperty(slide, "TranslateY");
            sb.Children.Add(slide);
        }

        sb.Begin();
        SettingsButton.Visibility = Visibility.Collapsed;
    }

    private void CloseSettingsOverlay()
    {
        if (!_settingsOpen) return;
        _settingsOpen = false;
        SettingsOverlay.Visibility = Visibility.Collapsed;
        SettingsOverlay.Opacity = 1;
        SettingsSheetHost.Opacity = 1;
        // Gear only on home.
        SettingsButton.Visibility = _mode == ShellMode.Home ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsOpen)
        {
            CloseSettingsOverlay();
            return;
        }
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
