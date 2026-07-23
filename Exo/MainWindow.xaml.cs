using System.Runtime.InteropServices;
using Exo.Helpers;
using Exo.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using WinRT.Interop;

namespace Exo;

/// <summary>
/// Thin native shell: fixed window + WebView2 product UI.
/// Optimizers stay C#/PS via <see cref="WebHostBridge"/>.
/// </summary>
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

    private readonly CancellationTokenSource _lifetimeCts = new();
    private WebHostBridge? _bridge;
    private bool _firstFrameMarked;
    private bool _webReady;
    /// <summary>
    /// Single-flight for web init. RootGrid.Loaded and Activated both call EnsureWebAsync;
    /// without this, two concurrent inits each Attach() a bridge → every shell.openUrl runs twice.
    /// </summary>
    private Task? _ensureWebTask;
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

        // Custom glass caption owns min/close; hide stock Win11 title-bar buttons.
        ExtendsContentIntoTitleBar = true;
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter op)
                op.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        }
        catch { /* best-effort on older presenters */ }

        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPresenterChange)
                ApplyResponsiveWindowChrome();
        };

        RootGrid.Loaded += async (_, _) =>
        {
            ApplyResponsiveWindowChrome();
            SyncContentHostWidth();
            await EnsureWebAsync();
            BootstrapHomeOnce("root-loaded");
        };
        RootGrid.ActualThemeChanged += (_, _) => ApplyShellChrome();
        Activated += OnFirstActivationBootstrap;
        Closed += (_, _) =>
        {
            _lifetimeCts.Cancel();
            try { _bridge?.Detach(); } catch { }
            App.Services.Settings.Flush();
            App.MainAppWindow = null;
        };

        ApplyShellChrome();
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
        BootstrapHomeOnce("window-activated");
        _ = EnsureWebAsync();
    }

    private void BootstrapHomeOnce(string reason)
    {
        if (_homeBootstrapped) return;
        _homeBootstrapped = true;
        StartupLog.Mark("bootstrap-home:" + reason);
        NavigateHome(suppressTransition: true);
    }

    private void StartPostFirstFrameWork()
    {
        if (_postFirstFrameWorkStarted) return;
        _postFirstFrameWorkStarted = true;
        try { App.Services.WarmInBackground(); } catch { }
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
            _ = SetForegroundWindow(hWnd);
            Activate();
        }
        catch { }
    }

    private Task EnsureWebAsync()
    {
        if (_webReady) return Task.CompletedTask;
        // Single-flight: Loaded + Activated race must not double-Attach the bridge.
        return _ensureWebTask ??= EnsureWebCoreAsync();
    }

    private async Task EnsureWebCoreAsync()
    {
        if (_webReady) return;
        try
        {
            await WebHost.EnsureCoreWebView2Async();
            var core = WebHost.CoreWebView2;
            if (core is null)
            {
                // Allow a later EnsureWebAsync to retry instead of sticking on a no-op task.
                _ensureWebTask = null;
                return;
            }

            var www = ResolveWwwRoot();
            if (www is null || !Directory.Exists(www))
            {
                StartupLog.Mark("wwwroot-missing");
                // Dev fallback: show a tiny diagnostic page.
                core.NavigateToString(
                    "<html><body style='background:#000;color:#fff;font-family:Segoe UI;padding:24px'>" +
                    "<h2>Exo UI not built</h2><p>Run: <code>cd ui &amp;&amp; npm run build</code></p></body></html>");
                _webReady = true;
                return;
            }

            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            try { core.Settings.AreDevToolsEnabled = true; } catch { }

            core.SetVirtualHostNameToFolderMapping(
                "app.exo.local",
                www,
                CoreWebView2HostResourceAccessKind.Allow);

            // Replace any prior bridge so WebMessageReceived never stacks handlers.
            try { _bridge?.Detach(); } catch { }
            _bridge = new WebHostBridge(App.Services, DispatcherQueue);
            _bridge.Attach(core);
            _bridge.SettingsRequested += (_, _) =>
            {
                try { DispatcherQueue.TryEnqueue(() => SettingsButton_Click(SettingsButton, new RoutedEventArgs())); }
                catch { }
            };

            core.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess)
                    StartupLog.Mark("web-nav-ok");
                else
                    StartupLog.Mark("web-nav-fail:" + args.WebErrorStatus);
            };

            WebHost.Source = new Uri("https://app.exo.local/index.html");
            _webReady = true;
            StartupLog.Mark("web-host-ready");
        }
        catch (Exception ex)
        {
            // Allow a later call to retry if CoreWebView2 init failed mid-flight.
            _ensureWebTask = null;
            StartupLog.Mark("web-host-failed:" + ex.GetType().Name);
        }
    }

    private static string? ResolveWwwRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "wwwroot"),
            Path.Combine(baseDir, "ui"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "wwwroot")),
        };
        foreach (var c in candidates)
        {
            if (Directory.Exists(c) && File.Exists(Path.Combine(c, "index.html")))
                return c;
        }
        return null;
    }

    private async void NavigateWebHash(string hash)
    {
        try
        {
            await EnsureWebAsync();
            if (WebHost.CoreWebView2 is null) return;
            // HashRouter: set location hash without full reload when possible.
            var script = $"window.location.hash = {System.Text.Json.JsonSerializer.Serialize(hash)};";
            await WebHost.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch { }
    }

    public void NavigateHome(bool suppressTransition = false) => NavigateWebHash("#/");
    public void NavigateToDiscord() => NavigateWebHash("#/module/discord");
    public void NavigateToBrave() => NavigateWebHash("#/module/brave");
    public void NavigateToSteam() => NavigateWebHash("#/module/steam");
    public void NavigateToInternet() => NavigateWebHash("#/module/internet");
    public void NavigateToNvidia() => NavigateWebHash("#/module/nvidia");
    public void NavigateToGames() => NavigateWebHash("#/module/games");

    public void StabilizeShellAfterExternalWork()
    {
        try
        {
            ApplyResponsiveWindowChrome();
            if (_firstFrameMarked)
            {
                try { SetTitleBar(AppTitleBar); } catch { }
            }
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
            try { presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false); }
            catch { }
        }
    }

    private void CaptionMinimize_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.Minimize();
        }
        catch { }
    }

    private void CaptionClose_Click(object sender, RoutedEventArgs e)
    {
        try { Close(); }
        catch { }
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

    private void NavHome_Click(object sender, RoutedEventArgs e) => NavigateHome();

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
