using Exo.Services;
using Microsoft.UI.Xaml;

namespace Exo;

public partial class App : Application
{
    public static AppServices Services { get; } = new();
    private Window? _window;

    public App()
    {
        Helpers.StartupLog.Mark("app-ctor");
        InitializeComponent();
        Helpers.StartupLog.Mark("app-resources-loaded");
        Services.Initialize();
        UnhandledException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine(e.Exception);
            try
            {
                File.AppendAllText(
                    Path.Combine(Helpers.PathHelper.LogsDir, "unhandled.log"),
                    $"[{DateTime.UtcNow:O}] {e.Exception}{Environment.NewLine}");
            }
            catch { /* best-effort */ }

            // Logging an unexpected exception does not make it safe to continue in a
            // potentially corrupted state. Let WinUI perform its normal fail-fast path.
            e.Handled = false;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Helpers.StartupLog.Mark("on-launched");
        _window = new MainWindow();
        Helpers.StartupLog.Mark("main-window-created");
        Services.Theme.Attach(_window);
        _window.Activate();
        Helpers.StartupLog.Mark("window-activated");
    }

    public static void TryActivateMainWindow()
    {
        var window = MainAppWindow;
        if (window is null) return;
        _ = window.DispatcherQueue.TryEnqueue(() =>
        {
            if (window is MainWindow main)
                main.BringToForeground();
            else
                window.Activate();
        });
    }

    public static Window? MainAppWindow { get; set; }
}
