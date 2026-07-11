using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Web.WebView2.Core;
using OptiHub.Helpers;
using OptiHub.Services;
using OptiHub.Views;
using Windows.Graphics;
using WinRT.Interop;

namespace OptiHub;

/// <summary>
/// WinUI host: prefers WebView2 SPA; falls back to XAML pages if Runtime fails.
/// </summary>
public sealed partial class MainWindow : Window
{
    private enum ShellMode { Home, Discord, Steam, Nvidia, Settings }

    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private WebUiBridge? _bridge;
    private bool _webReady;
    private bool _useXamlFallback;
    private ShellMode _mode = ShellMode.Home;

    public MainWindow()
    {
        InitializeComponent();
        App.MainAppWindow = this;

        AppWindow.Resize(new SizeInt32(1080, 860));
        ApplyFixedWindowChrome();
        TryCenterOnScreen();
        TrySetWindowIcon();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarHost);
        UpdateCaptionInset();

        AppWindow.Changed += (_, args) =>
        {
            UpdateCaptionInset();
            if (args.DidPresenterChange || args.DidSizeChange)
                ApplyFixedWindowChrome();
        };

        WebView.Loaded += async (_, _) => await TryInitWebViewAsync();
        RootGrid.Loaded += async (_, _) =>
        {
            UpdateCaptionInset();
            ApplyFixedWindowChrome();
            await TryInitWebViewAsync();
        };
        RootGrid.SizeChanged += (_, _) => UpdateCaptionInset();
        RootGrid.ActualThemeChanged += (_, _) => ApplyShellChrome();

        Closed += (_, _) =>
        {
            _lifetimeCts.Cancel();
            App.Services.Settings.Flush();
            App.MainAppWindow = null;
            try { NativeWindowHelper.RestoreWindowProcedure(WindowNative.GetWindowHandle(this)); }
            catch { /* best-effort */ }
        };

        ApplyShellChrome();
        _ = MaybeAutoUpdateAsync(_lifetimeCts.Token);
    }

    private async Task TryInitWebViewAsync()
    {
        if (_webReady || _useXamlFallback) return;
        if (!await _initGate.WaitAsync(0)) return;

        try
        {
            if (_webReady || _useXamlFallback) return;
            SetHostStatus("Starting WebView2…");

            for (var i = 0; i < 60 && (WebView.XamlRoot is null || !WebView.IsLoaded); i++)
                await Task.Delay(50);

            var userData = Path.Combine(PathHelper.AppDataDir, "WebView2");
            Directory.CreateDirectory(userData);

            var browserFolder = FindWebView2BrowserFolder();
            LogWeb("Browser folder: " + (browserFolder ?? "(auto)"));
            LogWeb("User data: " + userData);

            // Env vars help the native loader when Create* APIs are picky.
            if (!string.IsNullOrWhiteSpace(browserFolder))
                Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", browserFolder);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userData);

            var initTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnInitialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
            {
                initTcs.TrySetResult(args.Exception);
            }

            WebView.CoreWebView2Initialized += OnInitialized;
            Exception? lastError = null;

            try
            {
                // Attempt matrix: explicit browser path → auto browser → default Ensure.
                var attempts = new List<Func<Task>>
                {
                    async () =>
                    {
                        if (browserFolder is null)
                            throw new InvalidOperationException("No Edge WebView runtime folder found.");
                        var env = await CoreWebView2Environment.CreateWithOptionsAsync(
                            browserFolder, userData, null);
                        await WebView.EnsureCoreWebView2Async(env);
                    },
                    async () =>
                    {
                        var env = await CoreWebView2Environment.CreateWithOptionsAsync(
                            string.Empty, userData, null);
                        await WebView.EnsureCoreWebView2Async(env);
                    },
                    async () =>
                    {
                        var env = await CoreWebView2Environment.CreateAsync();
                        await WebView.EnsureCoreWebView2Async(env);
                    },
                    async () => { await WebView.EnsureCoreWebView2Async(); }
                };

                var ok = false;
                foreach (var attempt in attempts)
                {
                    try
                    {
                        // Reset TCS between attempts if needed
                        if (initTcs.Task.IsCompleted)
                            initTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

                        await attempt();
                        var done = await Task.WhenAny(initTcs.Task, Task.Delay(12000));
                        if (done == initTcs.Task)
                        {
                            var ex = await initTcs.Task;
                            if (ex is not null)
                            {
                                lastError = ex;
                                LogWeb("Init event exception: " + ex);
                                continue;
                            }
                        }

                        if (WebView.CoreWebView2 is not null)
                        {
                            ok = true;
                            break;
                        }

                        // Poll briefly
                        for (var i = 0; i < 20 && WebView.CoreWebView2 is null; i++)
                            await Task.Delay(50);

                        if (WebView.CoreWebView2 is not null)
                        {
                            ok = true;
                            break;
                        }

                        lastError = new InvalidOperationException("CoreWebView2 still null after Ensure.");
                        LogWeb(lastError.Message);
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        LogWeb("Attempt failed: " + ex);
                    }
                }

                if (!ok || WebView.CoreWebView2 is null)
                {
                    throw lastError ?? new InvalidOperationException(
                        "WebView2 could not start (CoreWebView2 null).");
                }
            }
            finally
            {
                WebView.CoreWebView2Initialized -= OnInitialized;
            }

            var core = WebView.CoreWebView2!;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            WebView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0);

            var webRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets", "Web"));
            if (!Directory.Exists(webRoot))
                throw new DirectoryNotFoundException("Web UI assets missing: " + webRoot);
            var indexPath = Path.Combine(webRoot, "index.html");
            if (!File.Exists(indexPath))
                throw new FileNotFoundException("index.html missing", indexPath);

            try
            {
                core.SetVirtualHostNameToFolderMapping(
                    "app.optihub.local", webRoot, CoreWebView2HostResourceAccessKind.Allow);
            }
            catch (Exception mapEx) { LogWeb("map: " + mapEx.Message); }

            _bridge = new WebUiBridge(
                App.Services,
                DispatcherQueue,
                this,
                async json =>
                {
                    try { WebView.CoreWebView2?.PostWebMessageAsJson(json); } catch { }
                    await Task.CompletedTask;
                });

            core.WebMessageReceived += async (_, args) =>
            {
                try
                {
                    var raw = args.TryGetWebMessageAsString() ?? args.WebMessageAsJson;
                    if (string.IsNullOrWhiteSpace(raw)) return;
                    if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
                    {
                        try { raw = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? raw; }
                        catch { }
                    }
                    if (_bridge is not null)
                        await _bridge.HandleMessageAsync(raw);
                }
                catch (Exception ex) { LogWeb("bridge: " + ex.Message); }
            };

            core.NavigationCompleted += async (_, args) =>
            {
                if (!args.IsSuccess)
                {
                    try { await LoadWebUiDocumentAsync(core, webRoot, indexPath); }
                    catch (Exception ex) { LogWeb("nav retry: " + ex.Message); }
                    return;
                }
                SetHostStatus(string.Empty);
                try
                {
                    if (_bridge is not null)
                        await _bridge.PushThemeAsync(App.Services.Settings.Current.Theme);
                }
                catch { }
            };

            SetHostStatus("Loading UI…");
            await LoadWebUiDocumentAsync(core, webRoot, indexPath);

            WebView.Visibility = Visibility.Visible;
            ContentFrame.Visibility = Visibility.Collapsed;
            SettingsButton.Visibility = Visibility.Collapsed; // SPA has its own
            BackButton.Visibility = Visibility.Collapsed;
            _webReady = true;
            SetHostStatus(string.Empty);
            LogWeb("WebView2 ready");
        }
        catch (Exception ex)
        {
            LogWeb("FATAL: " + ex);
            SetHostStatus("Using classic UI");
            EnableXamlFallback(ex.Message);
        }
        finally
        {
            _initGate.Release();
        }
    }

    private void EnableXamlFallback(string reason)
    {
        _useXamlFallback = true;
        try
        {
            WebView.Visibility = Visibility.Collapsed;
            ContentFrame.Visibility = Visibility.Visible;
            SettingsButton.Visibility = Visibility.Visible;
            LogWeb("XAML fallback enabled: " + reason);
            NavigateHome(suppressTransition: true);
            SetHostStatus(string.Empty);
        }
        catch (Exception ex)
        {
            LogWeb("Fallback failed: " + ex);
            SetHostStatus("UI failed — see logs");
        }
    }

    private static string? FindWebView2BrowserFolder()
    {
        // Prefer registry Evergreen client location, then Program Files scan.
        try
        {
            foreach (var keyPath in new[]
                     {
                         @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
                         @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
                     })
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath);
                var loc = key?.GetValue("location") as string;
                var pv = key?.GetValue("pv") as string;
                if (!string.IsNullOrWhiteSpace(loc) && !string.IsNullOrWhiteSpace(pv))
                {
                    var dir = Path.Combine(loc, pv);
                    if (File.Exists(Path.Combine(dir, "msedgewebview2.exe")))
                        return dir;
                }
            }
        }
        catch { /* ignore */ }

        foreach (var root in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         "Microsoft", "EdgeWebView", "Application"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         "Microsoft", "EdgeWebView", "Application")
                 })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var dir in Directory.GetDirectories(root).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    if (File.Exists(Path.Combine(dir, "msedgewebview2.exe")))
                        return dir;
                }
            }
            catch { /* ignore */ }
        }

        return null;
    }

    private static async Task LoadWebUiDocumentAsync(CoreWebView2 core, string webRoot, string indexPath)
    {
        var html = await File.ReadAllTextAsync(indexPath);
        var cssPath = Path.Combine(webRoot, "app.css");
        var jsPath = Path.Combine(webRoot, "app.js");
        var css = File.Exists(cssPath) ? await File.ReadAllTextAsync(cssPath) : "";
        var js = File.Exists(jsPath) ? await File.ReadAllTextAsync(jsPath) : "";
        var rootUri = new Uri(webRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        var logosUri = new Uri(rootUri, "logos/").AbsoluteUri;

        html = html
            .Replace(
                "<link rel=\"stylesheet\" href=\"app.css\" />",
                "<style>\n" + css + "\n</style>")
            .Replace(
                "<script src=\"app.js\"></script>",
                "<script>\nwindow.__OPTIHUB_LOGOS__ = " +
                System.Text.Json.JsonSerializer.Serialize(logosUri) +
                ";\n" + js + "\n</script>");

        try { core.Navigate("https://app.optihub.local/index.html"); }
        catch { /* optional */ }
        await Task.Delay(20);
        core.NavigateToString(html);
    }

    // --- XAML fallback navigation (original pages) ---

    public void NavigateHome(bool suppressTransition = false)
    {
        if (!_useXamlFallback && _webReady) return;
        Navigate(ShellMode.Home, typeof(DashboardPage),
            suppressTransition
                ? new SuppressNavigationTransitionInfo()
                : new ContinuumNavigationTransitionInfo());
    }

    public void NavigateToDiscord()
    {
        if (!_useXamlFallback && _webReady) return;
        Navigate(ShellMode.Discord, typeof(DiscordOptimizerPage), new DrillInNavigationTransitionInfo());
    }

    public void NavigateToSteam()
    {
        if (!_useXamlFallback && _webReady) return;
        Navigate(ShellMode.Steam, typeof(SteamOptimizerPage), new DrillInNavigationTransitionInfo());
    }

    public void NavigateToNvidia()
    {
        if (!_useXamlFallback && _webReady) return;
        Navigate(ShellMode.Nvidia, typeof(NvidiaOptimizerPage), new DrillInNavigationTransitionInfo());
    }

    private void Navigate(ShellMode mode, Type pageType, NavigationTransitionInfo transition)
    {
        _mode = mode;
        ContentFrame.Visibility = Visibility.Visible;
        if (ContentFrame.Navigate(pageType, null, transition))
            ApplyChrome(mode);
    }

    private void ApplyChrome(ShellMode mode)
    {
        if (!_useXamlFallback) return;
        var home = mode == ShellMode.Home;
        BackButton.Visibility = home ? Visibility.Collapsed : Visibility.Visible;
        SettingsButton.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
        AppTitleText.Text = mode switch
        {
            ShellMode.Discord => "Discord",
            ShellMode.Steam => "Steam",
            ShellMode.Nvidia => "NVIDIA",
            ShellMode.Settings => "Settings",
            _ => string.Empty
        };
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => NavigateHome();

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_useXamlFallback && _webReady) return;
        Navigate(ShellMode.Settings, typeof(SettingsPage), new DrillInNavigationTransitionInfo());
    }

    private void SetHostStatus(string text)
    {
        try
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => SetHostStatus(text));
                return;
            }
            HostStatus.Text = text ?? string.Empty;
            HostStatus.Visibility = string.IsNullOrWhiteSpace(text)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        catch { }
    }

    private static void LogWeb(string text)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(PathHelper.LogsDir, "webview-init.log"),
                $"[{DateTime.UtcNow:O}] {text}\n");
        }
        catch { }
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
            NativeWindowHelper.DisableMaximizeViaSystemMenu(WindowNative.GetWindowHandle(this));
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
        catch { CaptionSpacer.Width = new GridLength(138); }
    }

    private void ApplyShellChrome()
    {
        var dark = RootGrid.ActualTheme != ElementTheme.Light;
        var bg = dark
            ? ColorHelper.FromArgb(255, 0, 0, 0)
            : ColorHelper.FromArgb(255, 243, 237, 227);
        RootGrid.Background = new SolidColorBrush(bg);
        TitleBarHost.Background = new SolidColorBrush(bg);
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
            appWindow.Move(new PointInt32(
                work.X + (work.Width - appWindow.Size.Width) / 2,
                work.Y + (work.Height - appWindow.Size.Height) / 2));
        }
        catch { }
    }

    private void TrySetWindowIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "OptiHub.ico");
            if (File.Exists(path)) AppWindow.SetIcon(path);
        }
        catch { }
    }

    private async Task MaybeAutoUpdateAsync(CancellationToken ct)
    {
        try
        {
            if (!App.Services.Settings.Current.AutoUpdateScripts) return;
            await Task.Delay(2000, ct);
            var appCheck = await App.Services.Updater.CheckAppUpdateAsync(ct: ct);
            if (!appCheck.UpdateAvailable || RootGrid.XamlRoot is null) return;
            var dialog = new ContentDialog
            {
                Title = "OptiHub update available",
                Content = $"Version {appCheck.RemoteVersion} is available (you have {appCheck.LocalVersion}). Install now?",
                PrimaryButtonText = "Install",
                CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var install = await App.Services.Updater.InstallAppUpdateAsync(appCheck, ct: ct);
            if (install.ShouldExit)
            {
                await Task.Delay(900, ct);
                Application.Current?.Exit();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { }
    }
}
