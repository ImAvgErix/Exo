using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Exo.Services;

public sealed class ThemeService
{
    private Window? _window;

    // One deliberate dark visual system keeps every surface consistent.
    public static readonly Color CozyBlack = Color.FromArgb(255, 0, 0, 0);

    public ThemeService(SettingsService settings) { }

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

        root.RequestedTheme = ElementTheme.Dark;

        if (root is Panel panel)
        {
            panel.Background = new SolidColorBrush(
                CozyBlack);
        }
        else if (root is Border border)
        {
            border.Background = new SolidColorBrush(
                CozyBlack);
        }

        TrySetTitleBarColors();
    }

    private void TrySetTitleBarColors()
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

            titleBar.ForegroundColor = Colors.White;
            titleBar.InactiveForegroundColor = Color.FromArgb(255, 168, 162, 158);
            titleBar.ButtonForegroundColor = Color.FromArgb(255, 245, 245, 244);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 168, 162, 158);
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 20, 20, 20);
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonPressedForegroundColor = Colors.White;
        }
        catch
        {
            // Title bar customization is best-effort on unpackaged apps
        }
    }
}
