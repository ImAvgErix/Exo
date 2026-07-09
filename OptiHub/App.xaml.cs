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
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OptiHub", "logs");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(
                    Path.Combine(logDir, "unhandled.log"),
                    $"[{DateTime.UtcNow:O}] {e.Exception}{Environment.NewLine}");
            }
            catch { /* best-effort */ }
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        Services.Theme.Attach(_window);
        _window.Activate();

        // Background auto-update for Discord kit when enabled (any machine).
        if (Services.Settings.Current.AutoUpdateScripts)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Services.Updater.CheckAndUpdateDiscordScriptsAsync(force: false);
                }
                catch { /* best-effort */ }
            });
        }
    }

    public static Window? MainAppWindow { get; set; }
}
