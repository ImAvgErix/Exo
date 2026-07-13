using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using OptiHub.Helpers;
using OptiHub.Views;
using Windows.Graphics;
using Windows.UI;
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
        NvidiaPanel,
        Settings
    }

    private ShellMode _mode = ShellMode.Home;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Dictionary<string, Button> _navMap = new();

    public MainWindow()
    {
        InitializeComponent();
        App.MainAppWindow = this;

        AppWindow.Resize(new SizeInt32(1280, 840));
        ApplyResizableWindowChrome();
        TryCenterOnScreen();
        TrySetWindowIcon();
        TryEnableLiquidGlassBackdrop();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarHost);
        TryTransparentTitleBarButtons();

        _navMap["home"] = NavHome;
        _navMap["discord"] = NavDiscord;
        _navMap["steam"] = NavSteam;
        _navMap["internet"] = NavInternet;
        _navMap["nvidia"] = NavNvidia;
        _navMap["settings"] = NavSettings;

        AppWindow.Changed += (_, args) =>
        {
            UpdateCaptionInset();
            if (args.DidPresenterChange)
                ApplyResizableWindowChrome();
        };
        RootGrid.Loaded += (_, _) =>
        {
            UpdateCaptionInset();
            ApplyResizableWindowChrome();
            ClearChromeFocus();
            SyncNavSelection("home");
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
                _ = ContentFrame.Focus(FocusState.Programmatic);
        }
        catch { }
    }

    private void ApplyResizableWindowChrome()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = true;
            presenter.IsResizable = true;
            presenter.IsMinimizable = true;
            presenter.PreferredMinimumWidth = 960;
            presenter.PreferredMinimumHeight = 600;
        }
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
        // Keep root transparent so Desktop Acrylic / Mica shows through.
        RootGrid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        App.Services.Theme.Apply();
        UpdateCaptionInset();
        SyncNavSelection(ModeToNavKey(_mode));
    }

    private void TryEnableLiquidGlassBackdrop()
    {
        try
        {
            if (DesktopAcrylicController.IsSupported())
            {
                SystemBackdrop = new DesktopAcrylicBackdrop();
                return;
            }
        }
        catch { }

        try
        {
            if (MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            }
        }
        catch { }
    }

    private void TryTransparentTitleBarButtons()
    {
        try
        {
            var tb = AppWindow.TitleBar;
            tb.ButtonBackgroundColor = Color.FromArgb(0, 0, 0, 0);
            tb.ButtonInactiveBackgroundColor = Color.FromArgb(0, 0, 0, 0);
            tb.ButtonHoverBackgroundColor = Color.FromArgb(40, 255, 255, 255);
            tb.ButtonPressedBackgroundColor = Color.FromArgb(60, 255, 255, 255);
            tb.ButtonForegroundColor = Color.FromArgb(255, 245, 245, 247);
            tb.ButtonInactiveForegroundColor = Color.FromArgb(160, 174, 174, 178);
        }
        catch { }
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

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        switch (key)
        {
            case "home": NavigateHome(); break;
            case "discord": NavigateToDiscord(); break;
            case "steam": NavigateToSteam(); break;
            case "internet": NavigateToInternet(); break;
            case "nvidia": NavigateToNvidia(); break;
            case "settings": Navigate(ShellMode.Settings, typeof(SettingsPage), Slide()); break;
        }
    }

    private void SyncNavSelection(string activeKey)
    {
        Brush Soft() => Application.Current.Resources.TryGetValue("OptiAccentSoftBrush", out var s) && s is Brush sb
            ? sb : new SolidColorBrush(ColorHelper.FromArgb(0x33, 0x64, 0xD2, 0xFF));
        Brush Acc() => Application.Current.Resources.TryGetValue("OptiAccentBrush", out var a) && a is Brush ab
            ? ab : new SolidColorBrush(ColorHelper.FromArgb(255, 100, 210, 255));
        Brush Mut() => Application.Current.Resources.TryGetValue("OptiMutedTextBrush", out var m) && m is Brush mb
            ? mb : new SolidColorBrush(ColorHelper.FromArgb(255, 174, 174, 178));
        Brush Pri() => Application.Current.Resources.TryGetValue("OptiPrimaryTextBrush", out var p) && p is Brush pb
            ? pb : new SolidColorBrush(ColorHelper.FromArgb(255, 245, 245, 247));

        foreach (var kv in _navMap)
        {
            var on = string.Equals(kv.Key, activeKey, StringComparison.OrdinalIgnoreCase);
            var btn = kv.Value;
            btn.Background = on ? Soft() : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            btn.Foreground = on ? Acc() : Mut();
            btn.BorderThickness = new Thickness(0);
            PaintNavContent(btn.Content, on ? Acc() : Mut(), on ? Pri() : Mut());
        }
    }

    private static void PaintNavContent(object? content, Brush iconBrush, Brush labelBrush)
    {
        switch (content)
        {
            case FontIcon icon:
                icon.Foreground = iconBrush;
                break;
            case TextBlock label:
                label.Foreground = labelBrush;
                break;
            case Panel panel:
                foreach (var child in panel.Children)
                    PaintNavContent(child, iconBrush, labelBrush);
                break;
        }
    }

    private static string ModeToNavKey(ShellMode mode) => mode switch
    {
        ShellMode.Home => "home",
        ShellMode.Discord => "discord",
        ShellMode.Steam => "steam",
        ShellMode.Internet => "internet",
        ShellMode.Nvidia or ShellMode.NvidiaPanel => "nvidia",
        ShellMode.Settings => "settings",
        _ => "home"
    };

    private void ApplyChrome(ShellMode mode)
    {
        _mode = mode;
        var panel = mode == ShellMode.NvidiaPanel;
        BackButton.Visibility = panel ? Visibility.Visible : Visibility.Collapsed;
        ContextLogoHost.Visibility = mode is ShellMode.Discord or ShellMode.Steam or ShellMode.Internet
            or ShellMode.Nvidia or ShellMode.NvidiaPanel
            ? Visibility.Visible
            : Visibility.Collapsed;

        AppTitleText.Text = mode switch
        {
            ShellMode.Home => "",
            ShellMode.Discord => "Discord",
            ShellMode.Steam => "Steam",
            ShellMode.Internet => "Internet",
            ShellMode.Nvidia => "NVIDIA",
            ShellMode.NvidiaPanel => "Display",
            ShellMode.Settings => "Settings",
            _ => ""
        };

        if (mode == ShellMode.Discord)
            TrySetContextLogo("Assets/Logos/discord.png");
        else if (mode == ShellMode.Steam)
            TrySetContextLogo("Assets/Logos/steam.png");
        else if (mode == ShellMode.Internet)
            TrySetContextLogo("Assets/Logos/internet.png");
        else if (mode is ShellMode.Nvidia or ShellMode.NvidiaPanel)
            TrySetContextLogo("Assets/Logos/nvidia.png");
        else
            ContextLogo.Source = null;

        SyncNavSelection(ModeToNavKey(mode));
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

    private void TrySetContextLogo(string relativePath) =>
        ContextLogo.Source = AssetPathToImageSourceConverter.Resolve(relativePath);

    private static NavigationTransitionInfo Slide() => new DrillInNavigationTransitionInfo();
    private static NavigationTransitionInfo SlideBack() => new SuppressNavigationTransitionInfo();

    public void NavigateHome(bool suppressTransition = false)
    {
        Navigate(
            ShellMode.Home,
            typeof(DashboardPage),
            suppressTransition ? (NavigationTransitionInfo)new SuppressNavigationTransitionInfo() : SlideBack());
    }

    public void NavigateToDiscord() => Navigate(ShellMode.Discord, typeof(DiscordOptimizerPage), Slide());
    public void NavigateToSteam() => Navigate(ShellMode.Steam, typeof(SteamOptimizerPage), Slide());
    public void NavigateToInternet() => Navigate(ShellMode.Internet, typeof(InternetOptimizerPage), Slide());
    public void NavigateToNvidia() => Navigate(ShellMode.Nvidia, typeof(NvidiaOptimizerPage), Slide());
    public void NavigateToNvidiaPanel() => Navigate(ShellMode.NvidiaPanel, typeof(NvidiaPanelPage), Slide());

    private void Navigate(ShellMode mode, Type pageType, NavigationTransitionInfo transition)
    {
        if (_mode == mode && ContentFrame.CurrentSourcePageType == pageType)
            return;

        if (ContentFrame.Navigate(pageType, null, transition))
            ApplyChrome(mode);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        Navigate(ShellMode.Settings, typeof(SettingsPage), Slide());

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
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

            var appCheck = await App.Services.Updater.CheckAppUpdateAsync(ct: ct);
            if (appCheck.UpdateAvailable && RootGrid.XamlRoot is not null)
            {
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
                if (choice == ContentDialogResult.Primary)
                {
                    var install = await App.Services.Updater.InstallAppUpdateAsync(appCheck, ct: ct);
                    if (install.ShouldExit)
                    {
                        await Task.Delay(900, ct);
                        Application.Current?.Exit();
                        return;
                    }
                    if (RootGrid.XamlRoot is not null)
                    {
                        await new ContentDialog
                        {
                            Title = "Update could not finish",
                            Content = install.Message,
                            CloseButtonText = "OK",
                            XamlRoot = RootGrid.XamlRoot
                        }.ShowAsync();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { }
    }
}
