using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Exo.Services;

public sealed class ThemeService
{
    private Window? _window;
    private readonly AccessibilitySettings _accessibility = new();
    private readonly UISettings _uiSettings = new();
    private bool _accessibilityHooked;

    public ThemeService(SettingsService settings) { }

    public void Attach(Window window)
    {
        _window = window;
        if (!_accessibilityHooked)
        {
            try
            {
                _accessibility.HighContrastChanged += OnHighContrastChanged;
                _accessibilityHooked = true;
            }
            catch
            {
                // Some Windows Insider builds expose AccessibilitySettings but
                // fail event registration (ERROR_NOT_FOUND). Root.ActualThemeChanged
                // still reapplies the live High Contrast state.
            }
        }
        Apply();
    }

    private void OnHighContrastChanged(AccessibilitySettings sender, object args) => Apply();

    public void Apply()
    {
        if (_window?.Content is not FrameworkElement root) return;

        if (!root.DispatcherQueue.HasThreadAccess)
        {
            _ = root.DispatcherQueue.TryEnqueue(Apply);
            return;
        }

        // High Contrast is controlled by Windows. Exo otherwise has one dark
        // visual system and no product theme toggle.
        root.RequestedTheme = _accessibility.HighContrast
            ? ElementTheme.Default
            : ElementTheme.Dark;

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

            var highContrast = _accessibility.HighContrast;
            var foreground = highContrast
                ? _uiSettings.GetColorValue(UIColorType.Foreground)
                : Color.FromArgb(255, 245, 245, 244);
            var inactive = highContrast
                ? _uiSettings.GetColorValue(UIColorType.Foreground)
                : Color.FromArgb(255, 168, 162, 158);
            var hover = highContrast
                ? _uiSettings.GetColorValue(UIColorType.Accent)
                : Color.FromArgb(255, 20, 20, 20);

            // System caption is disabled (custom glass min/close). Keep bar colors
            // transparent so any residual OS paint does not flash light chrome.
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.BackgroundColor = Color.FromArgb(255, 5, 5, 6);
            titleBar.InactiveBackgroundColor = Color.FromArgb(255, 5, 5, 6);
            titleBar.ForegroundColor = foreground;
            titleBar.InactiveForegroundColor = inactive;
            titleBar.ButtonForegroundColor = Colors.Transparent;
            titleBar.ButtonInactiveForegroundColor = Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = Colors.Transparent;
            titleBar.ButtonHoverForegroundColor = Colors.Transparent;
            titleBar.ButtonPressedBackgroundColor = Colors.Transparent;
            titleBar.ButtonPressedForegroundColor = Colors.Transparent;
        }
        catch
        {
            // Title bar customization is best-effort on unpackaged apps
        }
    }
}
