using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OptiHub.Views;
using Windows.Graphics;
using WinRT.Interop;

namespace OptiHub;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        App.MainAppWindow = this;

        // Comfortable default size
        AppWindow.Resize(new SizeInt32(1100, 720));
        TryCenterOnScreen();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarHost);

        RootGrid.ActualThemeChanged += (_, _) => ApplyShellChrome();
        ApplyShellChrome();

        ContentFrame.Navigate(typeof(DashboardPage));
        _ = MaybeAutoUpdateAsync();
    }

    private void ApplyShellChrome()
    {
        var dark = RootGrid.ActualTheme != ElementTheme.Light;
        RootGrid.Background = new SolidColorBrush(
            dark ? ColorHelper.FromArgb(255, 0, 0, 0)
                 : ColorHelper.FromArgb(255, 244, 244, 245));
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
        catch
        {
            // best-effort
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is SettingsPage) return;
        ContentFrame.Navigate(typeof(SettingsPage));
    }

    public void NavigateToDashboard()
    {
        ContentFrame.Navigate(typeof(DashboardPage));
    }

    public void NavigateToDiscord()
    {
        ContentFrame.Navigate(typeof(DiscordOptimizerPage));
    }

    public void NavigateToSettings()
    {
        ContentFrame.Navigate(typeof(SettingsPage));
    }

    private async Task MaybeAutoUpdateAsync()
    {
        try
        {
            if (!App.Services.Settings.Current.AutoUpdateScripts) return;
            // Delay so first paint / detect aren't competing with a GitHub zip download
            await Task.Delay(8000);
            await App.Services.Updater.CheckAndUpdateDiscordScriptsAsync(force: false);
        }
        catch
        {
            // ignore network issues on startup
        }
    }
}
