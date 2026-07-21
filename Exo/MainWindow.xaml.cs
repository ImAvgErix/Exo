using System.Runtime.InteropServices;
using Exo.Helpers;
using Exo.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using WinRT.Interop;

namespace Exo;

/// <summary>Fixed native WinUI shell for Exo's home and optimizer modules.</summary>
public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int command);

    private const int ShowRestore = 9;
    private const int FixedWindowWidth = 1200;
    private const int FixedWindowHeight = 800;

    private enum ShellMode
    {
        Home,
        Discord,
        Steam,
        Internet,
        Nvidia,
        Riot,
        Epic
    }

    private ShellMode _mode = ShellMode.Home;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private bool _gearSpinning;
    private bool _firstFrameMarked;
    private bool _homeBootstrapped;
    private bool _postFirstFrameWorkStarted;
    private bool _stickySafeMode;

    public MainWindow()
    {
        StartupLog.Mark("main-window-ctor");
        ExoMotion.MotionDisabled = true;
        _stickySafeMode = StartupLog.PreviousLaunchDiedBeforeFirstFrame;
        if (_stickySafeMode)
            StartupLog.Mark("safe-mode-motion-off");

        InitializeComponent();
        StartupLog.Mark("main-window-xaml-loaded");
        App.MainAppWindow = this;

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendered += OnFirstFrameRendered;

        try
        {
            ApplyResponsiveWindowChrome();
            ApplyInitialWindowBounds();
            TryCenterOnScreen();
            TrySetWindowIcon();
        }
        catch { StartupLog.Mark("chrome-setup-partial"); }

        ExtendsContentIntoTitleBar = true;

        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPresenterChange)
                ApplyResponsiveWindowChrome();
        };

        RootGrid.Loaded += (_, _) =>
        {
            ApplyResponsiveWindowChrome();
            SyncContentHostWidth();
            ClearChromeFocus();
            BootstrapHomeOnce("root-loaded");
        };
        RootGrid.ActualThemeChanged += (_, _) => ApplyShellChrome();
        Activated += OnWindowActivatedClearFocus;
        Activated += OnFirstActivationBootstrap;
        Closed += (_, _) =>
        {
            _lifetimeCts.Cancel();
            App.Services.Settings.Flush();
            App.MainAppWindow = null;
        };

        ApplyShellChrome();
        ContentFrame.Navigated += OnContentNavigated;
        Activated += OnFirstActivationReapplyIcon;
        StartupLog.Mark("main-window-ctor-done");
    }

    private void OnFirstFrameRendered(object? sender, Microsoft.UI.Xaml.Media.RenderedEventArgs e)
    {
        if (_firstFrameMarked) return;
        _firstFrameMarked = true;
        try { Microsoft.UI.Xaml.Media.CompositionTarget.Rendered -= OnFirstFrameRendered; } catch { }
        StartupLog.Mark(StartupLog.FirstFrameMarker);

        if (!_stickySafeMode)
        {
            ExoMotion.MotionDisabled = false;
            StartupLog.Mark("boot-motion-enabled");
        }

        try
        {
            SetTitleBar(AppTitleBar);
            StartupLog.Mark("titlebar-set");
        }
        catch { StartupLog.Mark("titlebar-set-failed"); }

        StartPostFirstFrameWork();
    }

    private void OnFirstActivationBootstrap(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated) return;
        Activated -= OnFirstActivationBootstrap;
        BootstrapHomeOnce("window-activated");
    }

    private void BootstrapHomeOnce(string reason)
    {
        if (_homeBootstrapped) return;
        _homeBootstrapped = true;
        try
        {
            NavigateHome(suppressTransition: true);
            StartupLog.Mark("home-navigated:" + reason);
            ClearChromeFocus();
        }
        catch (Exception ex)
        {
            StartupLog.Mark("home-navigate-failed:" + ex.GetType().Name);
        }
    }

    private void StartPostFirstFrameWork()
    {
        if (_postFirstFrameWorkStarted) return;
        _postFirstFrameWorkStarted = true;
        try
        {
            App.Services.WarmInBackground();
            StartupLog.Mark("optimizer-warm-started");
        }
        catch { }
        // Auto-update consent popup (with release TLDR) when the setting is on.
        _ = MaybeAutoUpdateAsync();
    }

    /// <summary>
    /// After first paint: if "Updates on launch" is on, check GitHub and show the
    /// branded Update available dialog with a plain-language TLDR of the release.
    /// </summary>
    private async Task MaybeAutoUpdateAsync()
    {
        try
        {
            if (!App.Services.Settings.Current.CheckForUpdatesOnLaunch) return;
            await Task.Delay(1400, _lifetimeCts.Token).ConfigureAwait(true);
            if (_lifetimeCts.IsCancellationRequested) return;

            var check = await App.Services.Updater
                .CheckAppUpdateAsync(status: null, progress: null, _lifetimeCts.Token)
                .ConfigureAwait(true);
            if (!check.UpdateAvailable || string.IsNullOrWhiteSpace(check.DownloadUrl))
                return;
            if (string.IsNullOrWhiteSpace(check.Sha256))
                return; // install path would block; don't nag

            var root = RootGrid.XamlRoot;
            if (root is null) return;

            var go = await ExoUpdateDialog.ConfirmInstallAsync(
                root,
                check.LocalVersion,
                check.RemoteVersion,
                check.ReleaseSummary).ConfigureAwait(true);
            if (!go || _lifetimeCts.IsCancellationRequested) return;

            var install = await ExoUpdateDialog.InstallWithProgressAsync(
                root,
                check,
                App.Services.Updater,
                _lifetimeCts.Token).ConfigureAwait(true);

            if (install.ShouldExit)
            {
                // Exit promptly so the SFX (/waitpid) can replace %LocalAppData%\Exo\app.
                try { await Task.Delay(200, _lifetimeCts.Token).ConfigureAwait(true); } catch { }
                try { Microsoft.UI.Xaml.Application.Current?.Exit(); } catch { }
                try { Environment.Exit(0); } catch { }
            }
            else if (!string.IsNullOrWhiteSpace(install.Message))
            {
                // Surface install failure (previously silent when quiet SFX failed).
                try
                {
                    await ExoUpdateDialog.ShowMessageAsync(
                        root,
                        "Update did not finish",
                        install.Message + "\n\nYou can also download Exo.exe from GitHub Releases.").ConfigureAwait(true);
                }
                catch { /* ignore */ }
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            /* window closed */
        }
        catch (Exception ex)
        {
            StartupLog.Mark("auto-update-failed:" + ex.GetType().Name + ":" + ex.Message);
            try
            {
                var root = RootGrid.XamlRoot;
                if (root is not null)
                {
                    await ExoUpdateDialog.ShowMessageAsync(
                        root,
                        "Update failed",
                        ex.Message).ConfigureAwait(true);
                }
            }
            catch { /* ignore */ }
        }
    }

    public void BringToForeground()
    {
        try
        {
            var hWnd = WindowNative.GetWindowHandle(this);
            _ = ShowWindow(hWnd, ShowRestore);
            Activate();
            _ = SetForegroundWindow(hWnd);
        }
        catch { Activate(); }
    }

    private void OnContentNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (e.Content is not FrameworkElement page) return;
        try
        {
            ExoMotion.EnsureVisible(page);
            ExoMotion.EnsureVisible(ContentFrame);
            if (page is not HomePage)
                ExoMotion.PlayPageEnter(page);
        }
        catch
        {
            try { ExoMotion.EnsureVisible(page); } catch { }
        }
    }

    private bool _reappliedIcon;

    private void OnFirstActivationReapplyIcon(object sender, WindowActivatedEventArgs args)
    {
        if (_reappliedIcon || args.WindowActivationState == WindowActivationState.Deactivated) return;
        _reappliedIcon = true;
        Activated -= OnFirstActivationReapplyIcon;
        TrySetWindowIcon();
    }

    private bool _clearedInitialFocus;

    private void OnWindowActivatedClearFocus(object sender, WindowActivatedEventArgs args)
    {
        if (_clearedInitialFocus || args.WindowActivationState == WindowActivationState.Deactivated) return;
        _clearedInitialFocus = true;
        Activated -= OnWindowActivatedClearFocus;
        DispatcherQueue.TryEnqueue(ClearChromeFocus);
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

    private static NavigationTransitionInfo Slide() => new SuppressNavigationTransitionInfo();
    private static NavigationTransitionInfo SlideBack() => new SuppressNavigationTransitionInfo();

    public void NavigateHome(bool suppressTransition = false) =>
        Navigate(
            ShellMode.Home,
            typeof(HomePage),
            suppressTransition ? new SuppressNavigationTransitionInfo() : SlideBack());

    public void NavigateToDiscord() =>
        Navigate(ShellMode.Discord, typeof(DiscordOptimizerPage), Slide());

    public void NavigateToSteam() =>
        Navigate(ShellMode.Steam, typeof(SteamOptimizerPage), Slide());

    public void NavigateToInternet() =>
        Navigate(ShellMode.Internet, typeof(InternetOptimizerPage), Slide());

    public void NavigateToNvidia()
    {
        try
        {
            StabilizeShellAfterExternalWork();
            Navigate(ShellMode.Nvidia, typeof(NvidiaOptimizerPage), Slide());
            StabilizeShellAfterExternalWork();
        }
        catch (Exception ex)
        {
            StartupLog.Mark("nav-nvidia-failed:" + ex.GetType().Name);
            try
            {
                ExoMotion.MotionDisabled = true;
                StabilizeShellAfterExternalWork();
                Navigate(ShellMode.Nvidia, typeof(NvidiaOptimizerPage), null);
            }
            catch { StartupLog.Mark("nav-nvidia-hard-failed"); }
        }
    }

    public void NavigateToRiot() =>
        Navigate(ShellMode.Riot, typeof(RiotOptimizerPage), Slide());

    public void NavigateToEpic() =>
        Navigate(ShellMode.Epic, typeof(EpicOptimizerPage), Slide());

    // These modules do not yet have native pages; keep callers safe until they ship.
    public void NavigateToWindows() => NavigateHome();
    public void NavigateToGames() => NavigateHome();

    public void StabilizeShellAfterExternalWork()
    {
        try
        {
            ApplyResponsiveWindowChrome();
            if (_firstFrameMarked)
            {
                try { SetTitleBar(AppTitleBar); } catch { }
            }
            try
            {
                ExoMotion.EnsureVisible(ContentFrame);
                ExoMotion.EnsureVisible(RootGrid);
            }
            catch { }
        }
        catch { }
    }

    private void ApplyResponsiveWindowChrome()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
            presenter.IsMinimizable = true;
            presenter.PreferredMinimumWidth = FixedWindowWidth;
            presenter.PreferredMinimumHeight = FixedWindowHeight;
            presenter.PreferredMaximumWidth = FixedWindowWidth;
            presenter.PreferredMaximumHeight = FixedWindowHeight;
        }
    }

    private void ApplyInitialWindowBounds()
    {
        try { AppWindow.Resize(new SizeInt32(FixedWindowWidth, FixedWindowHeight)); }
        catch { }
    }

    private void ApplyShellChrome() => App.Services.Theme.Apply();

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

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e) => SyncContentHostWidth();

    private void SyncContentHostWidth()
    {
        try
        {
            if (ContentHost is null) return;
            ContentHost.ClearValue(FrameworkElement.WidthProperty);
            ContentHost.ClearValue(FrameworkElement.MaxWidthProperty);
            ContentHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        catch { }
    }

    private void ApplyChrome(ShellMode mode)
    {
        _mode = mode;
        SettingsButton.Visibility = Visibility.Visible;
        NavHome.Visibility = Visibility.Visible;
        ExoBrandPill.Visibility = Visibility.Visible;
        UpdateRailSelection(mode);
    }

    private void UpdateRailSelection(ShellMode mode)
    {
        var selected = mode switch
        {
            ShellMode.Discord => NavDiscord,
            ShellMode.Steam => NavSteam,
            ShellMode.Internet => NavInternet,
            ShellMode.Nvidia => NavNvidia,
            ShellMode.Riot => NavRiot,
            ShellMode.Epic => NavEpic,
            _ => null
        };

        var selectionFill = ResourceBrush("ExoAccentSoftBrush");
        var selectionOff = ResourceBrush("ExoTransparentBrush");
        var ringOn = ResourceBrush("ExoGlassCircleStrokeBrush");
        var ringOff = ResourceBrush("ExoTransparentBrush");
        var homeOn = mode == ShellMode.Home;

        ExoBrandPill.Opacity = homeOn ? 1.0 : 0.92;
        ExoBrandPill.Background = homeOn ? selectionFill : selectionOff;
        ExoBrandPill.BorderBrush = homeOn ? ringOn : ringOff;
        NavHome.Opacity = 1.0;
        NavHome.Background = selectionOff;
        NavHome.BorderBrush = ringOff;
        SettingsButton.Opacity = 1.0;
        SettingsButton.Background = selectionOff;
        SettingsButton.BorderBrush = ringOff;

        foreach (var button in new[] { NavDiscord, NavSteam, NavInternet, NavNvidia, NavRiot, NavEpic })
        {
            var on = selected is not null && ReferenceEquals(button, selected);
            button.Opacity = on ? 1.0 : 0.76;
            button.Background = on ? selectionFill : selectionOff;
            button.BorderBrush = on ? ringOn : ringOff;
            button.BorderThickness = new Thickness(1);
        }
    }

    private static Brush ResourceBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Colors.Transparent);

    private void Navigate(ShellMode mode, Type pageType, NavigationTransitionInfo? transition)
    {
        HideSettingsFlyout();

        if (_mode == mode && ContentFrame.CurrentSourcePageType == pageType)
            return;

        if (ContentFrame.Navigate(pageType, null, transition))
            ApplyChrome(mode);
    }

    private void NavHome_Click(object sender, RoutedEventArgs e) => NavigateHome();
    private void NavDiscord_Click(object sender, RoutedEventArgs e) => NavigateToDiscord();
    private void NavSteam_Click(object sender, RoutedEventArgs e) => NavigateToSteam();
    private void NavInternet_Click(object sender, RoutedEventArgs e) => NavigateToInternet();
    private void NavNvidia_Click(object sender, RoutedEventArgs e) => NavigateToNvidia();
    private void NavRiot_Click(object sender, RoutedEventArgs e) => NavigateToRiot();
    private void NavEpic_Click(object sender, RoutedEventArgs e) => NavigateToEpic();

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (SettingsFlyout.IsOpen)
            {
                SettingsFlyout.Hide();
                return;
            }
        }
        catch { }

        try { FlyoutBase.ShowAttachedFlyout(SettingsButton); }
        catch
        {
            try { SettingsFlyout.ShowAt(SettingsButton); } catch { }
        }
        SpinSettingsGear();
    }

    private void SettingsFlyout_Opened(object sender, object e)
    {
        try { SettingsSheetHost?.PlayOpenAnimation(); } catch { }
    }

    private bool _settingsCloseAnimated;

    private void SettingsFlyout_Closing(FlyoutBase sender, FlyoutBaseClosingEventArgs args)
    {
        if (_settingsCloseAnimated) return;
        if (SettingsSheetHost is null) return;
        args.Cancel = true;
        _settingsCloseAnimated = true;
        SpinSettingsGearBack();
        try
        {
            SettingsSheetHost.PlayCloseAnimation(() =>
            {
                try { SettingsFlyout.Hide(); } catch { }
            });
        }
        catch
        {
            try { SettingsFlyout.Hide(); } catch { }
        }
    }

    private void SettingsFlyout_Closed(object sender, object e)
    {
        _settingsCloseAnimated = false;
        try { SettingsSheetHost?.ResetOpenVisual(); } catch { }
        try
        {
            if (SettingsGearRotate is not null)
                SettingsGearRotate.Angle = 0;
        }
        catch { }
        _gearSpinning = false;
    }

    private void HideSettingsFlyout()
    {
        try
        {
            if (SettingsFlyout.IsOpen)
                SettingsFlyout.Hide();
        }
        catch { }
    }

    private void SpinSettingsGear()
    {
        if (_gearSpinning) return;
        _gearSpinning = true;
        try
        {
            if (SettingsGearRotate is null)
            {
                _gearSpinning = false;
                return;
            }

            SettingsGearRotate.Angle = 0;
            var animation = new DoubleAnimation
            {
                From = 0,
                To = 180,
                Duration = TimeSpan.FromMilliseconds(Views.Controls.SettingsSheet.OpenMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(animation, SettingsGearRotate);
            Storyboard.SetTargetProperty(animation, "Angle");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Completed += (_, _) =>
            {
                try { SettingsGearRotate.Angle = 180; } catch { }
                _gearSpinning = false;
            };
            storyboard.Begin();
        }
        catch
        {
            _gearSpinning = false;
        }
    }

    private void SpinSettingsGearBack()
    {
        if (_gearSpinning) return;
        _gearSpinning = true;
        try
        {
            if (SettingsGearRotate is null)
            {
                _gearSpinning = false;
                return;
            }

            var animation = new DoubleAnimation
            {
                From = SettingsGearRotate.Angle,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(Views.Controls.SettingsSheet.CloseMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(animation, SettingsGearRotate);
            Storyboard.SetTargetProperty(animation, "Angle");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Completed += (_, _) =>
            {
                try { SettingsGearRotate.Angle = 0; } catch { }
                _gearSpinning = false;
            };
            storyboard.Begin();
        }
        catch
        {
            _gearSpinning = false;
        }
    }

    private void TrySetWindowIcon()
    {
        try
        {
            var icoPath = ResolveAppIconPath();
            if (icoPath is null) return;
            try { AppWindow.SetIcon(icoPath); } catch { }

            var hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero) return;
            const uint imageIcon = 1;
            const int lrLoadFromFile = 0x0010;
            const int wmSetIcon = 0x0080;
            var big = LoadImage(IntPtr.Zero, icoPath, imageIcon, 32, 32, lrLoadFromFile);
            var small = LoadImage(IntPtr.Zero, icoPath, imageIcon, 16, 16, lrLoadFromFile);
            if (big != IntPtr.Zero) SendMessage(hwnd, wmSetIcon, new IntPtr(1), big);
            if (small != IntPtr.Zero) SendMessage(hwnd, wmSetIcon, new IntPtr(0), small);
        }
        catch { }
    }

    private static string? ResolveAppIconPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var assets = Path.Combine(baseDir, "Assets", "Exo.ico");
        var root = Path.Combine(baseDir, "Exo.ico");
        try
        {
            if (File.Exists(assets))
            {
                File.Copy(assets, root, true);
                return root;
            }
        }
        catch
        {
            if (File.Exists(assets)) return assets;
        }
        return File.Exists(root) ? root : (File.Exists(assets) ? assets : null);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, int fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
