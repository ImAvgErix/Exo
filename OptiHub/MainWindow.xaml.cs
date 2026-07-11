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

        // Init only after layout so WebView has a non-zero size + HWND.
        WebView.Loaded += async (_, _) =>
        {
            await Task.Delay(100);
            await TryInitWebViewAsync();
        };
        RootGrid.Loaded += (_, _) =>
        {
            UpdateCaptionInset();
            ApplyFixedWindowChrome();
            ApplyChrome(ShellMode.Home);
        };
        RootGrid.SizeChanged += (_, _) => UpdateCaptionInset();
        RootGrid.ActualThemeChanged += (_, _) => ApplyShellChrome();

        Closed += (_, _) =>
        {
            _lifetimeCts.Cancel();
            App.Services.Settings.Flush();
            App.MainAppWindow = null;
            try { NativeWindowHelper.RestoreWindowProcedure(WindowNative.GetWindowHandle(this)); }
            catch { }
        };

        ApplyShellChrome();
        // Default to XAML until WebView proves healthy (or takes over).
        ContentFrame.Visibility = Visibility.Visible;
        NavigateHome(suppressTransition: true);
        _ = MaybeAutoUpdateAsync(_lifetimeCts.Token);
    }

    private async Task TryInitWebViewAsync()
    {
        if (_webReady) return;
        if (!await _initGate.WaitAsync(0)) return;

        try
        {
            if (_webReady) return;
            SetHostStatus("Starting WebView2…");

            // CRITICAL: WebView must be Visible with real size (not Collapsed).
            WebView.Visibility = Visibility.Visible;
            WebView.Opacity = 0.01; // nearly invisible but still composited
            WebView.IsHitTestVisible = false;

            for (var i = 0; i < 40; i++)
            {
                if (WebView.XamlRoot is not null && WebView.ActualWidth > 8 && WebView.ActualHeight > 8)
                    break;
                await Task.Delay(50);
            }

            if (WebView.ActualWidth < 8 || WebView.ActualHeight < 8)
                throw new InvalidOperationException($"WebView has no size ({WebView.ActualWidth}x{WebView.ActualHeight}).");

            var userData = Path.Combine(PathHelper.AppDataDir, "WebView2");
            try
            {
                if (Directory.Exists(userData))
                {
                    // Clear corrupt profile once if previous runs failed.
                    var marker = Path.Combine(userData, ".optihub-reset-v2");
                    if (!File.Exists(marker))
                    {
                        try { Directory.Delete(userData, true); } catch { }
                        Directory.CreateDirectory(userData);
                        File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
                    }
                }
                else Directory.CreateDirectory(userData);
            }
            catch { Directory.CreateDirectory(userData); }

            var browserFolder = FindWebView2BrowserFolder();
            LogWeb($"size={WebView.ActualWidth}x{WebView.ActualHeight} browser={browserFolder ?? "auto"} udf={userData}");

            if (!string.IsNullOrWhiteSpace(browserFolder))
                Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", browserFolder);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userData);

            var initTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnInit(WebView2 s, CoreWebView2InitializedEventArgs e) => initTcs.TrySetResult(e.Exception);
            WebView.CoreWebView2Initialized += OnInit;

            try
            {
                // Prefer simplest path first: default Ensure (loader finds Evergreen).
                // Explicit folder paths sometimes throw FileNotFound with WinUI projection.
                Exception? last = null;
                var attempts = new List<Func<Task>>
                {
                    async () => await WebView.EnsureCoreWebView2Async(),
                    async () =>
                    {
                        var env = await CoreWebView2Environment.CreateAsync();
                        await WebView.EnsureCoreWebView2Async(env);
                    },
                    async () =>
                    {
                        if (browserFolder is null) throw new InvalidOperationException("no browser folder");
                        var env = await CoreWebView2Environment.CreateWithOptionsAsync(browserFolder, userData, null);
                        await WebView.EnsureCoreWebView2Async(env);
                    }
                };

                var ok = false;
                foreach (var attempt in attempts)
                {
                    try
                    {
                        if (initTcs.Task.IsCompleted)
                            initTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

                        await attempt();

                        var raced = await Task.WhenAny(initTcs.Task, Task.Delay(8000));
                        if (raced == initTcs.Task)
                        {
                            var ex = await initTcs.Task;
                            if (ex is not null) { last = ex; LogWeb("init event: " + ex.Message); continue; }
                        }

                        for (var i = 0; i < 30 && WebView.CoreWebView2 is null; i++)
                            await Task.Delay(50);

                        if (WebView.CoreWebView2 is not null) { ok = true; break; }
                        last = new InvalidOperationException("CoreWebView2 still null");
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        LogWeb("attempt: " + ex);
                    }
                }

                if (!ok || WebView.CoreWebView2 is null)
                    throw last ?? new InvalidOperationException("WebView2 failed to start");
            }
            finally
            {
                WebView.CoreWebView2Initialized -= OnInit;
            }

            var core = WebView.CoreWebView2!;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            WebView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0);

            var webRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets", "Web"));
            if (!Directory.Exists(webRoot))
                throw new DirectoryNotFoundException("Web UI missing: " + webRoot);
            var indexPath = Path.Combine(webRoot, "index.html");
            if (!File.Exists(indexPath))
                throw new FileNotFoundException("index.html missing", indexPath);

            try
            {
                core.SetVirtualHostNameToFolderMapping(
                    "app.optihub.local", webRoot, CoreWebView2HostResourceAccessKind.Allow);
            }
            catch (Exception mapEx) { LogWeb("map: " + mapEx.Message); }

            _bridge = new WebUiBridge(App.Services, DispatcherQueue, this, async json =>
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
                    catch (Exception ex) { LogWeb("nav: " + ex.Message); }
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

            await LoadWebUiDocumentAsync(core, webRoot, indexPath);

            // Hand UI to WebView; keep WinUI settings gear top-left always.
            ContentFrame.Visibility = Visibility.Collapsed;
            WebView.Opacity = 1;
            WebView.IsHitTestVisible = true;
            // Hide SPA chrome settings (host owns top-left gear). Inject CSS/JS to hide #btnSettings.
            try
            {
                await core.ExecuteScriptAsync(
                    "document.documentElement.style.setProperty('--chrome-pad-left','0px');" +
                    "var s=document.getElementById('btnSettings'); if(s) s.style.display='none';" +
                    "var c=document.getElementById('chrome'); if(c) c.style.display='none';");
            }
            catch { }

            // When using WebView full-bleed under title bar, SPA shouldn't duplicate chrome.
            // Host settings button navigates SPA via bridge message.
            _webReady = true;
            _useXamlFallback = false;
            SetHostStatus(string.Empty);
            ApplyChrome(ShellMode.Home);
            LogWeb("WebView2 ready");
        }
        catch (Exception ex)
        {
            LogWeb("FATAL: " + ex);
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
        _webReady = false;
        try
        {
            WebView.Opacity = 0;
            WebView.IsHitTestVisible = false;
            ContentFrame.Visibility = Visibility.Visible;
            SetHostStatus(string.Empty);
            LogWeb("XAML fallback: " + reason);
            if (ContentFrame.Content is null)
                NavigateHome(suppressTransition: true);
            ApplyChrome(_mode);
        }
        catch (Exception ex) { LogWeb("fallback fail: " + ex.Message); }
    }

    private static async Task LoadWebUiDocumentAsync(CoreWebView2 core, string webRoot, string indexPath)
    {
        var html = await File.ReadAllTextAsync(indexPath);
        var css = await File.ReadAllTextAsync(Path.Combine(webRoot, "app.css"));
        var js = await File.ReadAllTextAsync(Path.Combine(webRoot, "app.js"));
        var logosUri = new Uri(
            new Uri(webRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar),
            "logos/").AbsoluteUri;

        // Host owns window chrome — hide in-page chrome.
        css += "\n.chrome{display:none!important;}\n.view{padding-top:20px;}\n";

        html = html
            .Replace("<link rel=\"stylesheet\" href=\"app.css\" />", "<style>\n" + css + "\n</style>")
            .Replace(
                "<script src=\"app.js\"></script>",
                "<script>window.__OPTIHUB_LOGOS__=" +
                System.Text.Json.JsonSerializer.Serialize(logosUri) +
                ";window.__OPTIHUB_HOST_CHROME__=true;\n" + js + "\n</script>");

        core.NavigateToString(html);
    }

    private static string? FindWebView2BrowserFolder()
    {
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
        catch { }

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
                foreach (var dir in Directory.GetDirectories(root).OrderByDescending(d => d))
                {
                    if (File.Exists(Path.Combine(dir, "msedgewebview2.exe")))
                        return dir;
                }
            }
            catch { }
        }
        return null;
    }

    public void NavigateHome(bool suppressTransition = false)
    {
        if (_webReady && !_useXamlFallback)
        {
            _ = PostToWebNavigateAsync("home");
            ApplyChrome(ShellMode.Home);
            return;
        }
        Navigate(ShellMode.Home, typeof(DashboardPage),
            suppressTransition ? new SuppressNavigationTransitionInfo() : new ContinuumNavigationTransitionInfo());
    }

    public void NavigateToDiscord()
    {
        if (_webReady && !_useXamlFallback) { _ = PostToWebNavigateAsync("discord"); ApplyChrome(ShellMode.Discord); return; }
        Navigate(ShellMode.Discord, typeof(DiscordOptimizerPage), new DrillInNavigationTransitionInfo());
    }

    public void NavigateToSteam()
    {
        if (_webReady && !_useXamlFallback) { _ = PostToWebNavigateAsync("steam"); ApplyChrome(ShellMode.Steam); return; }
        Navigate(ShellMode.Steam, typeof(SteamOptimizerPage), new DrillInNavigationTransitionInfo());
    }

    public void NavigateToNvidia()
    {
        if (_webReady && !_useXamlFallback) { _ = PostToWebNavigateAsync("nvidia"); ApplyChrome(ShellMode.Nvidia); return; }
        Navigate(ShellMode.Nvidia, typeof(NvidiaOptimizerPage), new DrillInNavigationTransitionInfo());
    }

    private async Task PostToWebNavigateAsync(string route)
    {
        try
        {
            var core = WebView.CoreWebView2;
            if (core is null) return;
            await core.ExecuteScriptAsync(
                $"window.__optihubNavigate && window.__optihubNavigate({System.Text.Json.JsonSerializer.Serialize(route)})");
        }
        catch (Exception ex) { LogWeb("nav script: " + ex.Message); }
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
        _mode = mode;
        var home = mode == ShellMode.Home;
        // Settings always top-left
        SettingsButton.Visibility = Visibility.Visible;
        BackButton.Visibility = home ? Visibility.Collapsed : Visibility.Visible;

        AppTitleText.Text = mode switch
        {
            ShellMode.Discord => "Discord",
            ShellMode.Steam => "Steam",
            ShellMode.Nvidia => "NVIDIA",
            ShellMode.Settings => "Settings",
            _ => string.Empty
        };

        if (mode == ShellMode.Discord)
            SetContextLogo("Assets/Logos/discord.png");
        else if (mode == ShellMode.Steam)
            SetContextLogo("Assets/Logos/steam.png");
        else if (mode == ShellMode.Nvidia)
            SetContextLogo("Assets/Logos/nvidia.png");
        else
        {
            ContextLogo.Source = null;
            ContextLogo.Visibility = Visibility.Collapsed;
        }
    }

    private void SetContextLogo(string rel)
    {
        try
        {
            ContextLogo.Source = AssetPathToImageSourceConverter.Resolve(rel);
            ContextLogo.Visibility = Visibility.Visible;
        }
        catch
        {
            ContextLogo.Visibility = Visibility.Collapsed;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => NavigateHome();

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_webReady && !_useXamlFallback)
        {
            _ = PostToWebNavigateAsync("settings");
            ApplyChrome(ShellMode.Settings);
            return;
        }
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
            HostStatus.Text = text ?? "";
            HostStatus.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        }
        catch { }
    }

    private static void LogWeb(string text)
    {
        try
        {
            File.AppendAllText(Path.Combine(PathHelper.LogsDir, "webview-init.log"),
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
        try { NativeWindowHelper.DisableMaximizeViaSystemMenu(WindowNative.GetWindowHandle(this)); }
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
        var bg = dark ? ColorHelper.FromArgb(255, 0, 0, 0) : ColorHelper.FromArgb(255, 240, 233, 220);
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
            // Prefer standalone ico for taskbar + SetIcon
            foreach (var rel in new[]
                     {
                         Path.Combine("Assets", "OptiHub.ico"),
                         "OptiHub.ico"
                     })
            {
                var path = Path.Combine(AppContext.BaseDirectory, rel);
                if (File.Exists(path))
                {
                    AppWindow.SetIcon(path);
                    return;
                }
            }
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
                Title = "Update available",
                Content = $"OptiHub {appCheck.RemoteVersion} is ready (you have {appCheck.LocalVersion}).\n\nInstall now? The app will restart.",
                PrimaryButtonText = "Install",
                CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var install = await App.Services.Updater.InstallAppUpdateAsync(appCheck, ct: ct);
            if (install.ShouldExit)
            {
                await Task.Delay(600, ct);
                Application.Current?.Exit();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { }
    }
}
