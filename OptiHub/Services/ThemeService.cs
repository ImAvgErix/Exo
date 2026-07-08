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

    public static readonly Color CozyBlack = Color.FromArgb(255, 11, 11, 12);
    public static readonly Color SoftStone = Color.FromArgb(255, 245, 245, 244);
    public static readonly Color DarkAccent = Color.FromArgb(255, 28, 25, 23);

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

        var theme = _settings.Current.Theme;
        root.RequestedTheme = theme.Equals(AppSettings.LightTheme, StringComparison.OrdinalIgnoreCase)
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
        Apply();
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
                titleBar.ForegroundColor = DarkAccent;
                titleBar.InactiveForegroundColor = Color.FromArgb(255, 120, 113, 108);
                titleBar.ButtonForegroundColor = DarkAccent;
                titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 120, 113, 108);
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 231, 229, 228);
                titleBar.ButtonHoverForegroundColor = DarkAccent;
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 214, 211, 209);
                titleBar.ButtonPressedForegroundColor = DarkAccent;
            }
            else
            {
                titleBar.ForegroundColor = Colors.White;
                titleBar.InactiveForegroundColor = Color.FromArgb(255, 168, 162, 158);
                titleBar.ButtonForegroundColor = Color.FromArgb(255, 245, 245, 244);
                titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 168, 162, 158);
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 36, 36, 40);
                titleBar.ButtonHoverForegroundColor = Colors.White;
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 52, 52, 56);
                titleBar.ButtonPressedForegroundColor = Colors.White;
            }
        }
        catch
        {
            // Title bar customization is best-effort on unpackaged apps
        }
    }
}
