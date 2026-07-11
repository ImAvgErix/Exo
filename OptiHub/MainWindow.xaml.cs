using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using OptiHub.Helpers;
using OptiHub.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace OptiHub;

/// <summary>
/// Minimal WinUI 3 host. Product UI is WebView2 (Assets/Web).
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private WebUiBridge? _bridge;
    private bool _webReady;

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

        WebView.Loaded += async (_, _) => await InitWebViewAsync();
        RootGrid.Loaded += async (_, _) =>
        {
            UpdateCaptionInset();
            ApplyFixedWindowChrome();
            await InitWebViewAsync();
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
            catch { /* best-effort */ }
        };

        ApplyShellChrome();
        _ = MaybeAutoUpdateAsync(_lifetimeCts.Token);
    }

    private async Task InitWebViewAsync()
    {
        if (_webReady) return;
        if (!await _initGate.WaitAsync(0)) return; // another init in flight

        try
        {
            if (_webReady) return;
            SetHostStatus("Starting WebView2…");

            // WebView2 needs a real HWND — wait until it is in the live tree.
            for (var i = 0; i < 80 && (WebView.XamlRoot is null || !WebView.IsLoaded); i++)
                await Task.Delay(50);

            if (WebView.XamlRoot is null)
                throw new InvalidOperationException("WebView is not in the visual tree yet (XamlRoot null).");

            var userData = Path.Combine(PathHelper.AppDataDir, "WebView2");
            Directory.CreateDirectory(userData);

            // Capture init result via the WinUI event (more reliable than reading CoreWebView2 immediately).
            var initTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnInitialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
            {
                try
                {
                    initTcs.TrySetResult(args.Exception);
                }
                catch (Exception ex)
                {
                    initTcs.TrySetException(ex);
                }
            }

            WebView.CoreWebView2Initialized += OnInitialized;
            try
            {
                // WinAppSDK 2.2: CreateWithOptionsAsync(browserFolder, userDataFolder, options)
                // Explicit user-data folder under %LocalAppData% is required for reliable init.
                CoreWebView2Environment? env = null;
                try
                {
                    env = await CoreWebView2Environment.CreateWithOptionsAsync(
                        null,
                        userData,
                        null);
                }
                catch (Exception envEx)
                {
                    LogWeb("CreateWithOptionsAsync failed: " + envEx);
                    // Fallback: default environment
                    try { env = await CoreWebView2Environment.CreateAsync(); }
                    catch (Exception envEx2)
                    {
                        throw new InvalidOperationException(
                            "Could not create WebView2 environment. " +
                            "Install/repair the Evergreen WebView2 Runtime.\n\n" + envEx2.Message,
                            envEx2);
                    }
                }

                if (env is not null)
                    await WebView.EnsureCoreWebView2Async(env);
                else
                    await WebView.EnsureCoreWebView2Async();

                // Wait for the initialized event (or short timeout, then re-check property).
                var finished = await Task.WhenAny(initTcs.Task, Task.Delay(15000));
                if (finished == initTcs.Task)
                {
                    var initEx = await initTcs.Task;
                    if (initEx is not null)
                        throw new InvalidOperationException("WebView2 initialization failed: " + initEx.Message, initEx);
                }
            }
            finally
            {
                WebView.CoreWebView2Initialized -= OnInitialized;
            }

            // Give the control a beat to publish CoreWebView2 after the event.
            CoreWebView2? core = null;
            for (var i = 0; i < 40; i++)
            {
                core = WebView.CoreWebView2;
                if (core is not null) break;
                await Task.Delay(50);
            }

            if (core is null)
            {
                throw new InvalidOperationException(
                    "WebView2 Runtime did not attach (CoreWebView2 stayed null).\n\n" +
                    "Fix:\n" +
                    "1) Install/repair Evergreen WebView2 Runtime (x64)\n" +
                    "   https://go.microsoft.com/fwlink/p/?LinkId=2124703\n" +
                    "2) Reboot once after install\n" +
                    "3) Delete %LocalAppData%\\OptiHub\\WebView2 and reopen OptiHub\n\n" +
                    "Runtime looks for Edge WebView under Program Files (x86)\\Microsoft\\EdgeWebView.");
            }

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
                    "app.optihub.local",
                    webRoot,
                    CoreWebView2HostResourceAccessKind.Allow);
            }
            catch (Exception mapEx)
            {
                LogWeb("Virtual host map failed: " + mapEx);
            }

            _bridge = new WebUiBridge(
                App.Services,
                DispatcherQueue,
                this,
                async json =>
                {
                    try { WebView.CoreWebView2?.PostWebMessageAsJson(json); }
                    catch { /* ignore */ }
                    await Task.CompletedTask;
                });

            core.WebMessageReceived += async (_, args) =>
            {
                try
                {
                    var raw = args.TryGetWebMessageAsString();
                    if (string.IsNullOrWhiteSpace(raw))
                        raw = args.WebMessageAsJson;
                    if (string.IsNullOrWhiteSpace(raw)) return;

                    if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
                    {
                        try { raw = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? raw; }
                        catch { /* keep */ }
                    }

                    if (_bridge is not null)
                        await _bridge.HandleMessageAsync(raw);
                }
                catch (Exception ex)
                {
                    LogWeb("bridge: " + ex);
                }
            };

            core.NavigationCompleted += async (_, args) =>
            {
                if (!args.IsSuccess)
                {
                    SetHostStatus("Retrying UI load…");
                    try { await LoadWebUiDocumentAsync(core, webRoot, indexPath); }
                    catch (Exception ex) { SetHostStatus("Load failed: " + ex.Message); }
                    return;
                }

                SetHostStatus(string.Empty);
                try
                {
                    if (_bridge is not null)
                        await _bridge.PushThemeAsync(App.Services.Settings.Current.Theme);
                }
                catch { /* best-effort */ }
            };

            core.ProcessFailed += (_, args) =>
            {
                SetHostStatus("WebView process failed: " + args.ProcessFailedKind);
                _webReady = false;
            };

            SetHostStatus("Loading UI…");
            await LoadWebUiDocumentAsync(core, webRoot, indexPath);
            _webReady = true;
            SetHostStatus(string.Empty);
        }
        catch (Exception ex)
        {
            LogWeb(ex.ToString());
            SetHostStatus("WebView2 failed");
            await ShowFatalWebViewErrorAsync(ex);
        }
        finally
        {
            _initGate.Release();
        }
    }

    private static async Task LoadWebUiDocumentAsync(
        CoreWebView2 core,
        string webRoot,
        string indexPath)
    {
        var html = await File.ReadAllTextAsync(indexPath);
        var cssPath = Path.Combine(webRoot, "app.css");
        var jsPath = Path.Combine(webRoot, "app.js");
        var css = File.Exists(cssPath) ? await File.ReadAllTextAsync(cssPath) : "";
        var js = File.Exists(jsPath) ? await File.ReadAllTextAsync(jsPath) : "";

        var rootUri = new Uri(webRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        var logosUri = new Uri(rootUri, "logos/").AbsoluteUri;

        // Inline CSS/JS — NavigateToString + external file:// scripts often block.
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

        await Task.Delay(30);
        core.NavigateToString(html);
    }

    private async Task ShowFatalWebViewErrorAsync(Exception ex)
    {
        var msg =
            "WebView2 failed to start.\n\n" +
            ex.Message +
            "\n\n1) Install Evergreen WebView2 Runtime (x64):\n" +
            "   https://go.microsoft.com/fwlink/p/?LinkId=2124703\n" +
            "2) Delete folder: %LocalAppData%\\OptiHub\\WebView2\n" +
            "3) Reopen OptiHub\n\n" +
            "Log: %LocalAppData%\\OptiHub\\logs\\webview-init.log";

        await DispatcherQueue.EnqueueAsync(() =>
        {
            try
            {
                if (RootGrid.Children.Contains(WebView))
                    RootGrid.Children.Remove(WebView);
            }
            catch { /* ignore */ }

            RootGrid.Children.Add(new ScrollViewer
            {
                Margin = new Thickness(28, 48, 28, 28),
                Content = new TextBlock
                {
                    Text = msg,
                    TextWrapping = TextWrapping.WrapWholeWords,
                    Foreground = new SolidColorBrush(Colors.White),
                    FontSize = 14,
                    IsTextSelectionEnabled = true
                }
            });
        });
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
            if (HostStatus is null) return;
            HostStatus.Text = text ?? string.Empty;
            HostStatus.Visibility = string.IsNullOrWhiteSpace(text)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        catch { /* ignore */ }
    }

    private static void LogWeb(string text)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(PathHelper.LogsDir, "webview-init.log"),
                $"[{DateTime.UtcNow:O}] {text}\n");
        }
        catch { /* ignore */ }
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
        catch { /* best-effort */ }
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
        var bg = dark
            ? ColorHelper.FromArgb(255, 0, 0, 0)
            : ColorHelper.FromArgb(255, 243, 237, 227);
        RootGrid.Background = new SolidColorBrush(bg);
        TitleBarHost.Background = new SolidColorBrush(bg);
        try
        {
            WebView.DefaultBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(255, 0, 0, 0)
                : Windows.UI.Color.FromArgb(255, 243, 237, 227);
        }
        catch { /* not ready */ }
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
        catch { /* best-effort */ }
    }

    private void TrySetWindowIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "OptiHub.ico");
            if (File.Exists(path))
                AppWindow.SetIcon(path);
        }
        catch { /* best-effort */ }
    }

    public void NavigateHome(bool suppressTransition = false) { }
    public void NavigateToDiscord() { }
    public void NavigateToSteam() { }
    public void NavigateToNvidia() { }

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
                Content =
                    $"Version {appCheck.RemoteVersion} is available.\n" +
                    $"You have {appCheck.LocalVersion}.\n\nInstall now?",
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
        catch { /* ignore */ }
    }
}

internal static class DispatcherQueueExtensions
{
    public static Task EnqueueAsync(this Microsoft.UI.Dispatching.DispatcherQueue queue, Action action)
    {
        var tcs = new TaskCompletionSource();
        if (!queue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
        {
            tcs.TrySetException(new InvalidOperationException("DispatcherQueue unavailable"));
        }
        return tcs.Task;
    }
}
