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
                : Color.FromArgb(255, 233, 233, 236);
            var inactive = highContrast
                ? _uiSettings.GetColorValue(UIColorType.Foreground)
                : Color.FromArgb(255, 106, 106, 112);
            var hover = highContrast
                ? _uiSettings.GetColorValue(UIColorType.Accent)
                : Color.FromArgb(255, 26, 26, 31);

            // Extended black caption with the REAL Windows min/close buttons (4.2.2).
            // Button backgrounds are transparent so the black orb shows through, but
            // the glyphs must stay a visible ink color — the old design painted them
            // transparent to hide them behind custom glass chrome, which is gone.
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.BackgroundColor = Color.FromArgb(255, 0, 0, 0);
            titleBar.InactiveBackgroundColor = Color.FromArgb(255, 0, 0, 0);
            titleBar.ForegroundColor = foreground;
            titleBar.InactiveForegroundColor = inactive;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = inactive;
            titleBar.ButtonHoverBackgroundColor = hover;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedBackgroundColor = hover;
            titleBar.ButtonPressedForegroundColor = foreground;
        }
        catch
        {
            // Title bar customization is best-effort on unpackaged apps
        }
    }
}
