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

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int command);

    private const int ShowRestore = 9;

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
    private bool _stickySafeMode;
    private bool _homeBootstrapped;
    private bool _postFirstFrameWorkStarted;

    public MainWindow()
    {
        Helpers.StartupLog.Mark("main-window-ctor");

        // ALWAYS freeze entrance motion until the first pixel is on screen.
        // Pre-first-frame storyboards / composition pokes caused v2.6 flash-close
        // (0xC000027B) and still flicker-kill some GPUs on cold install.
        ExoMotion.MotionDisabled = true;
        // Sticky for the whole session if the previous run never painted.
        _stickySafeMode = Helpers.StartupLog.PreviousLaunchDiedBeforeFirstFrame;
        if (_stickySafeMode)
            Helpers.StartupLog.Mark("safe-mode-motion-off");
        else
            Helpers.StartupLog.Mark("boot-motion-frozen-until-first-frame");

        InitializeComponent();
        Helpers.StartupLog.Mark("main-window-xaml-loaded");
        App.MainAppWindow = this;

        // Prove composition presented before any deferred work or re-enabling motion.
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendered += OnFirstFrameRendered;

        // Start at the designed desktop size, but remain resizable and usable on
        // different work areas and DPI configurations.
        try
        {
            ApplyResponsiveWindowChrome();
            ApplyInitialWindowBounds();
            TryCenterOnScreen();
            TrySetWindowIcon();
        }
        catch
        {
            Helpers.StartupLog.Mark("chrome-setup-partial");
        }

        ExtendsContentIntoTitleBar = true;
        // SetTitleBar AFTER first frame — doing it pre-paint has killed cold boots.

        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPresenterChange)
                ApplyResponsiveWindowChrome();
        };
        RootGrid.Loaded += (_, _) =>
        {
            ApplyResponsiveWindowChrome();
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

        // Do NOT navigate / auto-update / SetTitleBar in the ctor. That work
        // runs after Activate so the HWND exists and composition can present.
        Helpers.StartupLog.Mark("main-window-ctor-done");
    }

    private void OnFirstFrameRendered(object? sender, Microsoft.UI.Xaml.Media.RenderedEventArgs e)
    {
        if (_firstFrameMarked) return;
        _firstFrameMarked = true;
        try { Microsoft.UI.Xaml.Media.CompositionTarget.Rendered -= OnFirstFrameRendered; } catch { }
        Helpers.StartupLog.Mark(Helpers.StartupLog.FirstFrameMarker);

        // Re-enable motion only when this session is not sticky safe-mode.
        if (!_stickySafeMode)
        {
            ExoMotion.MotionDisabled = false;
            Helpers.StartupLog.Mark("boot-motion-enabled");
        }

        try
        {
            SetTitleBar(AppTitleBar);
            Helpers.StartupLog.Mark("titlebar-set");
        }
        catch
        {
            Helpers.StartupLog.Mark("titlebar-set-failed");
        }

        StartPostFirstFrameWork();
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
        catch
        {
            Activate();
        }
    }

    /// <summary>First Activate: navigate home if Loaded hasn't already.</summary>
    private void OnFirstActivationBootstrap(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
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
            Helpers.StartupLog.Mark("home-navigated:" + reason);
            ClearChromeFocus();
        }
        catch (Exception ex)
        {
            Helpers.StartupLog.Mark("home-navigate-failed:" + ex.GetType().Name);
        }
    }

    private void StartPostFirstFrameWork()
    {
        if (_postFirstFrameWorkStarted) return;
        _postFirstFrameWorkStarted = true;
        try
        {
            _ = MaybeAutoUpdateAsync(_lifetimeCts.Token);
            Helpers.StartupLog.Mark("auto-update-started");
        }
        catch { }
    }

    private void OnContentNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (e.Content is FrameworkElement page)
        {
            // Soft page fade for modules; home has its own card stagger (first paint only).
            // Always reset identity so Back never leaves a residual X offset on the shell.
            try
            {
                ExoMotion.EnsureVisible(page);
                ExoMotion.EnsureVisible(ContentFrame);
                if (page is not Views.DashboardPage)
                    ExoMotion.PlayPageEnter(page);
            }
            catch
            {
                try { ExoMotion.EnsureVisible(page); } catch { }
            }
        }
    }

    private bool _reappliedIcon;

    private void OnFirstActivationReapplyIcon(object sender, WindowActivatedEventArgs args)
    {
        if (_reappliedIcon) return;
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
        _reappliedIcon = true;
        Activated -= OnFirstActivationReapplyIcon;
        TrySetWindowIcon();
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

    /// <summary>Windows 11 shell contract: normal resize/maximize with a safe minimum.</summary>
    private void ApplyResponsiveWindowChrome()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = true;
            presenter.IsResizable = true;
            presenter.IsMinimizable = true;
            presenter.PreferredMinimumWidth = 960;
            presenter.PreferredMinimumHeight = 600;
            presenter.PreferredMaximumWidth = null;
            presenter.PreferredMaximumHeight = null;
        }
    }

    private void ApplyInitialWindowBounds()
    {
        try
        {
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            var work = display?.WorkArea;
            var width = work is null ? 1180 : Math.Min(1180, Math.Max(640, work.Value.Width - 32));
            var height = work is null ? 760 : Math.Min(760, Math.Max(480, work.Value.Height - 32));
            AppWindow.Resize(new SizeInt32(width, height));
        }
        catch { }
    }

    private void ApplyShellChrome()
    {
        App.Services.Theme.Apply();
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

        // Navigation never changes meaning: EXO is Home and the gear is Settings.
        SettingsButton.Visibility = Visibility.Visible;
        NavHome.Visibility = Visibility.Visible;
        UpdateRailSelection(mode);
    }

    /// <summary>
    /// Highlight the active module circle: soft glass fill + accent selection ring.
    /// The active module keeps its rail item selected.
    /// </summary>
    private void UpdateRailSelection(ShellMode mode)
    {
        var selected = mode switch
        {
            ShellMode.Home => NavHome,
            ShellMode.Discord => NavDiscord,
            ShellMode.Steam => NavSteam,
            ShellMode.Internet => NavInternet,
            ShellMode.Nvidia => NavNvidia,
            ShellMode.Riot => NavRiot,
            ShellMode.Epic => NavEpic,
            _ => null
        };

        foreach (var btn in new[] { NavHome, NavDiscord, NavSteam, NavInternet, NavNvidia, NavRiot, NavEpic })
        {
            var on = selected is not null && ReferenceEquals(btn, selected);
            btn.Opacity = on ? 1.0 : 0.76;
            btn.Background = on ? _selectionFill : _selectionOff;
            btn.BorderBrush = on ? _ringOn : _ringOff;
            btn.BorderThickness = new Thickness(1);
        }
    }

    // Cached ring brushes — UpdateRailSelection runs on every navigation.
    private static readonly SolidColorBrush _ringOn =
        new(ColorHelper.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush _ringOff =
        new(ColorHelper.FromArgb(0x00, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush _selectionFill =
        new(ColorHelper.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush _selectionOff =
        new(ColorHelper.FromArgb(0x00, 0xFF, 0xFF, 0xFF));

    private void NavHome_Click(object sender, RoutedEventArgs e) => NavigateHome();

    private void NavDiscord_Click(object sender, RoutedEventArgs e) => NavigateToDiscord();

    private void NavSteam_Click(object sender, RoutedEventArgs e) => NavigateToSteam();

    private void NavInternet_Click(object sender, RoutedEventArgs e) => NavigateToInternet();

    private void NavNvidia_Click(object sender, RoutedEventArgs e) => NavigateToNvidia();

    private void NavRiot_Click(object sender, RoutedEventArgs e) => NavigateToRiot();

    private void NavEpic_Click(object sender, RoutedEventArgs e) => NavigateToEpic();

    private void TrySetWindowIcon()
    {
        try
        {
            var icoPath = ResolveAppIconPath();
            if (icoPath is null) return;

            // WinUI titlebar / window
            try { AppWindow.SetIcon(icoPath); } catch { }

            // Win32 icons (taskbar while running)
            try
            {
                var hwnd = WindowNative.GetWindowHandle(this);
                if (hwnd == IntPtr.Zero) return;

                const uint imageIcon = 1;
                const int lrLoadFromFile = 0x0010;
                const int wmSetIcon = 0x0080;

                // Prefer exact sizes; fall back to default-size load.
                var big = LoadImage(IntPtr.Zero, icoPath, imageIcon, 32, 32, lrLoadFromFile);
                if (big == IntPtr.Zero)
                    big = LoadImage(IntPtr.Zero, icoPath, imageIcon, 0, 0, lrLoadFromFile | 0x0040);
                var small = LoadImage(IntPtr.Zero, icoPath, imageIcon, 16, 16, lrLoadFromFile);
                if (small == IntPtr.Zero)
                    small = LoadImage(IntPtr.Zero, icoPath, imageIcon, 0, 0, lrLoadFromFile | 0x0040);

                if (big != IntPtr.Zero)
                    SendMessage(hwnd, wmSetIcon, new IntPtr(1), big);
                if (small != IntPtr.Zero)
                    SendMessage(hwnd, wmSetIcon, new IntPtr(0), small);
            }
            catch { }
        }
        catch { }
    }

    /// <summary>Stable .ico next to the EXE (and keep Assets\Exo.ico in sync).</summary>
    private static string? ResolveAppIconPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var assets = Path.Combine(baseDir, "Assets", "Exo.ico");
        var root = Path.Combine(baseDir, "Exo.ico");
        try
        {
            if (File.Exists(assets))
            {
                // Always refresh stable root copy for shortcuts.
                File.Copy(assets, root, true);
                return root;
            }
        }
        catch
        {
            if (File.Exists(assets)) return assets;
        }
        if (File.Exists(root)) return root;
        return File.Exists(assets) ? assets : null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, int fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // Soft continuum feel without multi-click lag (single Suppress keeps clicks snappy).
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

    public void NavigateToNvidia()
    {
        try
        {
            // Driver Apply / UAC can leave WinUI chrome half-invalid; re-pin size + titlebar.
            StabilizeShellAfterExternalWork();
            Navigate(ShellMode.Nvidia, typeof(NvidiaOptimizerPage), Slide());
            StabilizeShellAfterExternalWork();
        }
        catch (Exception ex)
        {
            Helpers.StartupLog.Mark("nav-nvidia-failed:" + ex.GetType().Name);
            try
            {
                ExoMotion.MotionDisabled = true;
                StabilizeShellAfterExternalWork();
                Navigate(ShellMode.Nvidia, typeof(NvidiaOptimizerPage), null);
            }
            catch
            {
                Helpers.StartupLog.Mark("nav-nvidia-hard-failed");
            }
        }
    }

    public void NavigateToRiot() =>
        Navigate(ShellMode.Riot, typeof(RiotOptimizerPage), Slide());

    public void NavigateToEpic() =>
        Navigate(ShellMode.Epic, typeof(EpicOptimizerPage), Slide());

    /// <summary>
    /// After elevated NVIDIA driver work the display stack can flicker. Re-assert
    /// the responsive presenter and title-bar contract without changing user size.
    /// </summary>
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
                if (ContentFrame is not null)
                    ExoMotion.EnsureVisible(ContentFrame);
                if (RootGrid is not null)
                    ExoMotion.EnsureVisible(RootGrid);
            }
            catch { }
        }
        catch { }
    }

    private void Navigate(ShellMode mode, Type pageType, NavigationTransitionInfo? transition)
    {
        HideSettingsFlyout();

        if (_mode == mode && ContentFrame.CurrentSourcePageType == pageType)
            return;

        if (ContentFrame.Navigate(pageType, null, transition))
            ApplyChrome(mode);
    }

    /// <summary>
    /// Open flyout + gear crank together — menu entrance is timed with the spin
    /// so the gear reads as opening the dropdown (not a delayed second step).
    /// </summary>
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

        // Show first (menu starts invisible and plays open in Opened); crank alongside.
        try
        {
            FlyoutBase.ShowAttachedFlyout(SettingsButton);
        }
        catch
        {
            try { SettingsFlyout.ShowAt(SettingsButton); } catch { }
        }
        SpinSettingsGear();
    }

    private void SettingsFlyout_Opened(object sender, object e)
    {
        // Cohesive open: panel drops/fades with the same duration as the gear crank.
        // Do NOT snap gear angle here — that fights the spin animation.
        try { SettingsSheetHost?.PlayOpenAnimation(); } catch { }
    }

    private bool _settingsCloseAnimated;

    /// <summary>
    /// Mirrored close: cancel the first dismiss, play the menu rise/fade with the
    /// gear counter-crank, then hide for real once the storyboard (or its safety
    /// fallback) completes.
    /// </summary>
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

    /// <summary>Gear crank — same duration as SettingsSheet.OpenMs so spin = open.</summary>
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
            var ms = Views.Controls.SettingsSheet.OpenMs;
            var anim = new DoubleAnimation
            {
                From = 0,
                To = 180,
                Duration = TimeSpan.FromMilliseconds(ms),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(anim, SettingsGearRotate);
            Storyboard.SetTargetProperty(anim, "Angle");
            var sb = new Storyboard();
            sb.Children.Add(anim);
            sb.Completed += (_, _) =>
            {
                try { SettingsGearRotate.Angle = 180; } catch { }
                _gearSpinning = false;
            };
            sb.Begin();
        }
        catch
        {
            _gearSpinning = false;
        }
    }

    /// <summary>Gear counter-crank — mirrors the open spin while the menu rises away.</summary>
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

            var ms = Views.Controls.SettingsSheet.CloseMs;
            var anim = new DoubleAnimation
            {
                From = SettingsGearRotate.Angle,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(ms),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(anim, SettingsGearRotate);
            Storyboard.SetTargetProperty(anim, "Angle");
            var sb = new Storyboard();
            sb.Children.Add(anim);
            sb.Completed += (_, _) =>
            {
                try { SettingsGearRotate.Angle = 0; } catch { }
                _gearSpinning = false;
            };
            sb.Begin();
        }
        catch
        {
            _gearSpinning = false;
        }
    }

    private async Task MaybeAutoUpdateAsync(CancellationToken ct)
    {
        try
        {
            if (!App.Services.Settings.Current.CheckForUpdatesOnLaunch) return;

            await Task.Delay(1200, ct);
            for (var i = 0; i < 10 && RootGrid.XamlRoot is null; i++)
                await Task.Delay(200, ct);

            if (RootGrid.XamlRoot is null) return;

            var appCheck = await App.Services.Updater.CheckAppUpdateAsync(ct: ct);
            if (!appCheck.UpdateAvailable) return;

            var installNow = await ExoUpdateDialog.ConfirmInstallAsync(
                RootGrid.XamlRoot,
                appCheck.LocalVersion,
                appCheck.RemoteVersion);
            ct.ThrowIfCancellationRequested();
            if (!installNow) return;

            var install = await ExoUpdateDialog.InstallWithProgressAsync(
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

            await ExoUpdateDialog.ShowMessageAsync(
                RootGrid.XamlRoot,
                "Update could not finish",
                install.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { }
    }
}
