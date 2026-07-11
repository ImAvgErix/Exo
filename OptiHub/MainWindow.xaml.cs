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
/// Minimal WinUI 3 host window. All product UI lives in WebView2 (Assets/Web).
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly CancellationTokenSource _lifetimeCts = new();
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
        try
        {
            await WebView.EnsureCoreWebView2Async();
            var core = WebView.CoreWebView2;
            if (core is null) return;

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;

            var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
            if (!Directory.Exists(webRoot))
                throw new DirectoryNotFoundException("Web UI assets missing: " + webRoot);

            core.SetVirtualHostNameToFolderMapping(
                "app.optihub",
                webRoot,
                CoreWebView2HostResourceAccessKind.Allow);

            _bridge = new WebUiBridge(
                App.Services,
                DispatcherQueue,
                this,
                async json =>
                {
                    if (WebView.CoreWebView2 is null) return;
                    WebView.CoreWebView2.PostWebMessageAsJson(json);
                    await Task.CompletedTask;
                });

            core.WebMessageReceived += async (_, args) =>
            {
                try
                {
                    var raw = args.TryGetWebMessageAsString() ?? args.WebMessageAsJson;
                    if (string.IsNullOrWhiteSpace(raw)) return;
                    // PostMessage from JS may wrap a JSON string; unwrap if needed.
                    if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
                    {
                        try { raw = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? raw; }
                        catch { /* keep raw */ }
                    }
                    if (_bridge is not null)
                        await _bridge.HandleMessageAsync(raw);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            };

            core.NavigationCompleted += async (_, args) =>
            {
                if (!args.IsSuccess) return;
                try
                {
                    var theme = App.Services.Settings.Current.Theme;
                    if (_bridge is not null)
                        await _bridge.PushThemeAsync(theme);
                }
                catch { /* best-effort */ }
            };

            core.Navigate("https://app.optihub/index.html");
            _webReady = true;
        }
        catch (Exception ex)
        {
            // Surface a hard failure in the window if WebView2 cannot start.
            RootGrid.Children.Clear();
            RootGrid.Children.Add(new TextBlock
            {
                Text = "WebView2 failed to start.\n\n" + ex.Message +
                       "\n\nInstall the WebView2 Runtime, then reopen OptiHub.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(32),
                Foreground = new SolidColorBrush(Colors.White)
            });
        }
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
        RootGrid.Background = new SolidColorBrush(
            dark ? ColorHelper.FromArgb(255, 0, 0, 0)
                 : ColorHelper.FromArgb(255, 243, 237, 227));
        App.Services.Theme.Apply();
        try
        {
            WebView.DefaultBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(255, 0, 0, 0)
                : Windows.UI.Color.FromArgb(255, 243, 237, 227);
        }
        catch { /* WebView may not be ready */ }
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
        catch { /* best-effort */ }
    }

    // Kept for any residual ViewModel navigation hooks (legacy XAML pages).
    public void NavigateHome(bool suppressTransition = false) { }
    public void NavigateToDiscord() { }
    public void NavigateToSteam() { }
    public void NavigateToNvidia() { }

    private async Task MaybeAutoUpdateAsync(CancellationToken ct)
    {
        try
        {
            if (!App.Services.Settings.Current.AutoUpdateScripts) return;
            await Task.Delay(1800, ct);
            var appCheck = await App.Services.Updater.CheckAppUpdateAsync(ct: ct);
            if (!appCheck.UpdateAvailable || RootGrid.XamlRoot is null) return;

            var dialog = new ContentDialog
            {
                Title = "OptiHub update available",
                Content =
                    $"Version {appCheck.RemoteVersion} is available.\n" +
                    $"You have {appCheck.LocalVersion}.\n\n" +
                    "Install now? OptiHub will close, update in place, and reopen.",
                PrimaryButtonText = "Install",
                CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot
            };
            var choice = await dialog.ShowAsync();
            ct.ThrowIfCancellationRequested();
            if (choice != ContentDialogResult.Primary) return;

            var install = await App.Services.Updater.InstallAppUpdateAsync(appCheck, ct: ct);
            if (install.ShouldExit)
            {
                await Task.Delay(900, ct);
                Application.Current?.Exit();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { /* ignore network issues on startup */ }
    }
}
