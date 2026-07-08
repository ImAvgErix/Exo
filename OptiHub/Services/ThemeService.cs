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

    public static readonly Color AmoledBlack = Color.FromArgb(255, 0, 0, 0);
    public static readonly Color SoftOffWhite = Color.FromArgb(255, 244, 244, 245);
    public static readonly Color WhiteAccent = Color.FromArgb(255, 255, 255, 255);
    public static readonly Color DarkAccent = Color.FromArgb(255, 17, 17, 17);

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
        root.RequestedTheme = theme switch
        {
            AppSettings.LightTheme => ElementTheme.Light,
            AppSettings.SystemTheme => ElementTheme.Default,
            _ => ElementTheme.Dark
        };

        if (root is Panel panel)
        {
            panel.Background = new SolidColorBrush(
                root.ActualTheme == ElementTheme.Light ? SoftOffWhite : AmoledBlack);
        }
        else if (root is Border border)
        {
            border.Background = new SolidColorBrush(
                root.ActualTheme == ElementTheme.Light ? SoftOffWhite : AmoledBlack);
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

            if (light)
            {
                titleBar.BackgroundColor = SoftOffWhite;
                titleBar.ForegroundColor = DarkAccent;
                titleBar.InactiveBackgroundColor = SoftOffWhite;
                titleBar.ButtonBackgroundColor = SoftOffWhite;
                titleBar.ButtonForegroundColor = DarkAccent;
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 229, 231, 235);
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 209, 213, 219);
            }
            else
            {
                titleBar.BackgroundColor = AmoledBlack;
                titleBar.ForegroundColor = Colors.White;
                titleBar.InactiveBackgroundColor = AmoledBlack;
                titleBar.ButtonBackgroundColor = AmoledBlack;
                titleBar.ButtonForegroundColor = Colors.White;
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 26, 26, 26);
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 42, 42, 42);
            }
            titleBar.ButtonInactiveBackgroundColor = light ? SoftOffWhite : AmoledBlack;
        }
        catch
        {
            // Title bar customization is best-effort on unpackaged apps
        }
    }
}
