using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OptiHub.Models;
using Windows.UI;

namespace OptiHub.Services;

public sealed class ThemeService
{
    private readonly SettingsService _settings;
    private Window? _window;

    // AMOLED pure black (dark) / warm cream beige (light)
    public static readonly Color CozyBlack = Color.FromArgb(255, 0, 0, 0);
    public static readonly Color SoftStone = Color.FromArgb(255, 240, 233, 220); // #F0E9DC
    public static readonly Color DarkAccent = Color.FromArgb(255, 44, 36, 28);   // #2C241C

    public ThemeService(SettingsService settings)
    {
        _settings = settings;
        _settings.SettingsChanged += (_, _) => Apply();
    }

    public void Attach(Window window)
    {
        _window = window;
        Apply();
    }

    public void Apply()
    {
        if (_window?.Content is not FrameworkElement root) return;

        if (!root.DispatcherQueue.HasThreadAccess)
        {
            _ = root.DispatcherQueue.TryEnqueue(Apply);
            return;
        }

        var theme = _settings.Current.Theme;
        root.RequestedTheme = string.Equals(theme, AppSettings.LightTheme, StringComparison.OrdinalIgnoreCase)
            ? ElementTheme.Light
            : ElementTheme.Dark;

        if (root is Panel panel)
        {
            panel.Background = new SolidColorBrush(
                root.ActualTheme == ElementTheme.Light ? SoftStone : CozyBlack);
        }
        else if (root is Border border)
        {
            border.Background = new SolidColorBrush(
                root.ActualTheme == ElementTheme.Light ? SoftStone : CozyBlack);
        }

        TrySetTitleBarColors(root.ActualTheme == ElementTheme.Light);
    }

    public void SetTheme(string theme)
    {
        _settings.Update(s => s.Theme = theme);
    }

    private void TrySetTitleBarColors(bool light)
    {
        try
        {
            if (_window is null) return;
            var appWindow = _window.AppWindow;
            if (appWindow is null) return;

            var titleBar = appWindow.TitleBar;
            if (titleBar is null) return;

            // Transparent caption buttons blend into the custom title bar
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.BackgroundColor = Colors.Transparent;
            titleBar.InactiveBackgroundColor = Colors.Transparent;

            if (light)
            {
                // Cream title-bar chrome
                titleBar.ForegroundColor = DarkAccent;
                titleBar.InactiveForegroundColor = Color.FromArgb(255, 107, 95, 80);
                titleBar.ButtonForegroundColor = DarkAccent;
                titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 107, 95, 80);
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 232, 222, 208);
                titleBar.ButtonHoverForegroundColor = DarkAccent;
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 218, 206, 190);
                titleBar.ButtonPressedForegroundColor = DarkAccent;
            }
            else
            {
                // AMOLED: pure black hover plates stay near-black
                titleBar.ForegroundColor = Colors.White;
                titleBar.InactiveForegroundColor = Color.FromArgb(255, 168, 162, 158);
                titleBar.ButtonForegroundColor = Color.FromArgb(255, 245, 245, 244);
                titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 168, 162, 158);
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 20, 20, 20);
                titleBar.ButtonHoverForegroundColor = Colors.White;
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 32, 32, 32);
                titleBar.ButtonPressedForegroundColor = Colors.White;
            }
        }
        catch
        {
            // Title bar customization is best-effort on unpackaged apps
        }
    }
}
