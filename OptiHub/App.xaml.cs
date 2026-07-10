using Microsoft.UI.Xaml;
using OptiHub.Services;

namespace OptiHub;

public partial class App : Application
{
    public static AppServices Services { get; } = new();
    private Window? _window;

    public App()
    {
        InitializeComponent();
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
        _window = new MainWindow();
        Services.Theme.Attach(_window);
        _window.Activate();
    }

    public static Window? MainAppWindow { get; set; }
}
