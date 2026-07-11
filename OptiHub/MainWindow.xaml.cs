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
    private WebUiBridge? _bridge;
    private bool _webReady;
    private int _initAttempts;

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

        // Init when the WebView control itself is ready (not only the grid).
        WebView.Loaded += async (_, _) => await InitWebViewAsync();
        RootGrid.Loaded += (_, _) =>
        {
            UpdateCaptionInset();
            ApplyFixedWindowChrome();
            // Fallback if WebView.Loaded already fired before we subscribed.
            _ = InitWebViewAsync();
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
        if (Interlocked.Increment(ref _initAttempts) > 8) return;

        try
        {
            SetHostStatus("Starting WebView2…");
            await WebView.EnsureCoreWebView2Async();

            var core = WebView.CoreWebView2;
            if (core is null)
            {
                SetHostStatus("WebView2 core is null");
                return;
            }

            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = true; // F12 if needed while diagnosing
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;

            // Opaque fill — Transparent WebView2 is a known blank-screen footgun.
            WebView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0);

            var webRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets", "Web"));
            if (!Directory.Exists(webRoot))
                throw new DirectoryNotFoundException("Web UI assets missing: " + webRoot);

            var indexPath = Path.Combine(webRoot, "index.html");
            if (!File.Exists(indexPath))
                throw new FileNotFoundException("index.html missing", indexPath);

            // Virtual host for clean https origins; file:// is the reliable fallback.
            try
            {
                core.SetVirtualHostNameToFolderMapping(
                    "app.optihub.local",
                    webRoot,
                    CoreWebView2HostResourceAccessKind.Allow);
            }
            catch (Exception mapEx)
            {
                SetHostStatus("Virtual host map failed: " + mapEx.Message);
            }

            _bridge = new WebUiBridge(
                App.Services,
                DispatcherQueue,
                this,
                async json =>
                {
                    try
                    {
                        WebView.CoreWebView2?.PostWebMessageAsJson(json);
                    }
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

                    // JS postMessage(string) often arrives as a JSON-encoded string literal.
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
                    try
                    {
                        File.AppendAllText(
                            Path.Combine(PathHelper.LogsDir, "webview-bridge.log"),
                            $"[{DateTime.UtcNow:O}] {ex}\n");
                    }
                    catch { /* ignore */ }
                }
            };

            core.NavigationCompleted += async (_, args) =>
            {
                if (!args.IsSuccess)
                {
                    SetHostStatus($"Navigation failed (0x{args.WebErrorStatus}) — trying file fallback…");
                    try
                    {
                        // file:// fallback if virtual host is blocked
                        var fileUri = new Uri(indexPath).AbsoluteUri;
                        core.Navigate(fileUri);
                    }
                    catch (Exception ex2)
                    {
                        SetHostStatus("Load failed: " + ex2.Message);
                    }
                    return;
                }

                SetHostStatus(string.Empty);
                try
                {
                    var theme = App.Services.Settings.Current.Theme;
                    if (_bridge is not null)
                        await _bridge.PushThemeAsync(theme);
                }
                catch { /* best-effort */ }
            };

            core.ProcessFailed += (_, args) =>
            {
                SetHostStatus("WebView process failed: " + args.ProcessFailedKind);
                _webReady = false;
            };

            SetHostStatus("Loading UI…");
            // Most reliable for unpackaged apps: load HTML as string with absolute
            // file:// links so CSS/JS/images always resolve (virtual host can silent-fail).
            await LoadWebUiDocumentAsync(core, webRoot, indexPath);
            _webReady = true;
        }
        catch (Exception ex)
        {
            SetHostStatus("WebView2 error");
            try
            {
                File.AppendAllText(
                    Path.Combine(PathHelper.LogsDir, "webview-init.log"),
                    $"[{DateTime.UtcNow:O}] {ex}\n");
            }
            catch { /* ignore */ }

            // Visible in-window error (not only a blank black frame).
            await DispatcherQueue.EnqueueAsync(() =>
            {
                var msg =
                    "WebView2 failed to start.\n\n" +
                    ex.Message +
                    "\n\nInstall/repair the Evergreen WebView2 Runtime, then reopen OptiHub.\n" +
                    "Log: %LocalAppData%\\OptiHub\\logs\\webview-init.log";

                // Replace webview area with text
                if (RootGrid.Children.Contains(WebView))
                    RootGrid.Children.Remove(WebView);

                RootGrid.Children.Add(new TextBlock
                {
                    Text = msg,
                    TextWrapping = TextWrapping.WrapWholeWords,
                    Margin = new Thickness(28, 48, 28, 28),
                    Foreground = new SolidColorBrush(Colors.White),
                    FontSize = 14
                });
            });
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

        // Inline CSS/JS so NavigateToString cannot fail to load external files
        // (about:blank origin often blocks file:// script tags).
        html = html
            .Replace(
                "<link rel=\"stylesheet\" href=\"app.css\" />",
                "<style>\n" + css + "\n</style>")
            .Replace(
                "<script src=\"app.js\"></script>",
                "<script>\nwindow.__OPTIHUB_LOGOS__ = " +
                System.Text.Json.JsonSerializer.Serialize(logosUri) +
                ";\n" + js + "\n</script>");

        // 1) Virtual host (best case for messaging + relative logos).
        try { core.Navigate("https://app.optihub.local/index.html"); }
        catch { /* fall through */ }

        // 2) Guaranteed paint: fully inlined document.
        await Task.Delay(30);
        core.NavigateToString(html);
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
