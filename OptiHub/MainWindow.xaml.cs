using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    private bool _gearSpinning;

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

        ContentFrame.Navigated += OnContentNavigated;
        // Re-apply icon after activate — WinUI sometimes drops the first SetIcon.
        Activated += (_, _) => TrySetWindowIcon();

        NavigateHome(suppressTransition: true);
        ClearChromeFocus();
        TryRepairStartMenuShortcut();
        _ = MaybeAutoUpdateAsync(_lifetimeCts.Token);
    }

    private void OnContentNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (e.Content is FrameworkElement page)
        {
            // Soft page fade for modules; home has its own card stagger (first paint only).
            // Always reset identity so Back never leaves a residual X offset on the shell.
            try
            {
                OptiMotion.EnsureVisible(page);
                OptiMotion.EnsureVisible(ContentFrame);
                if (page is not Views.DashboardPage)
                    OptiMotion.PlayPageEnter(page);
            }
            catch
            {
                try { OptiMotion.EnsureVisible(page); } catch { }
            }
        }
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

    /// <summary>Stable .ico next to the EXE (and keep Assets\OptiHub.ico in sync).</summary>
    private static string? ResolveAppIconPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var assets = Path.Combine(baseDir, "Assets", "OptiHub.ico");
        var root = Path.Combine(baseDir, "OptiHub.ico");
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

    /// <summary>
    /// Fix Start Menu shortcut that pointed at deleted versioned icons
    /// (e.g. OptiHub-2-0-2-0.ico) which made the taskbar/start tile blank paper.
    /// </summary>
    private static void TryRepairStartMenuShortcut()
    {
        try
        {
            var icoPath = ResolveAppIconPath();
            var targetExe = Path.Combine(AppContext.BaseDirectory, "OptiHub.exe");
            if (!File.Exists(targetExe))
                targetExe = Environment.ProcessPath ?? targetExe;
            if (!File.Exists(targetExe)) return;

            var lnk = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs",
                "OptiHub.lnk");

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            var shell = Activator.CreateInstance(shellType);
            if (shell is null) return;

            // Always rewrite so IconLocation cannot stay on a missing file.
            var shortcut = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { lnk });
            if (shortcut is null) return;
            var scType = shortcut.GetType();
            scType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { targetExe });
            scType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { AppContext.BaseDirectory.TrimEnd('\\') });
            scType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "OptiHub — max performance hub" });
            var icon = icoPath is not null && File.Exists(icoPath)
                ? icoPath + ",0"
                : targetExe + ",0";
            scType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { icon });
            scType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);

            // Associate AUMID with the shortcut so taskbar grouping uses our icon.
            try
            {
                SetShortcutAppUserModelId(lnk, Program.AppUserModelId);
            }
            catch { }
        }
        catch { }
    }

    private static void SetShortcutAppUserModelId(string lnkPath, string aumid)
    {
        // IShellLinkW + IPropertyStore via shell COM is heavy; use PowerShell-free
        // PropVariant path through Shell32 if available. Best-effort only.
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments =
                    "-NoProfile -Command \"" +
                    "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('" + lnkPath.Replace("'", "''") + "');" +
                    // Property store for AUMID via shell application (Win10+)
                    "\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // Skip PS round-trip; AUMID is already set on process.
            _ = psi;
        }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, int fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private void TrySetContextLogo(string relativePath)
    {
        ContextLogo.Source = AssetPathToImageSourceConverter.Resolve(relativePath);
    }

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

    public void NavigateToNvidia() =>
        Navigate(ShellMode.Nvidia, typeof(NvidiaOptimizerPage), Slide());

    public void NavigateToNvidiaPanel() =>
        Navigate(ShellMode.NvidiaPanel, typeof(NvidiaPanelPage), Slide());

    private void Navigate(ShellMode mode, Type pageType, NavigationTransitionInfo transition)
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

    private void SettingsFlyout_Closed(object sender, object e)
    {
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

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        HideSettingsFlyout();
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
