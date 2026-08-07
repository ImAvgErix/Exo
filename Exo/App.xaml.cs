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

        // Before anything else. A driver sweep sets a one-shot Safe Mode boot flag, and if the
        // previous run died between setting it and clearing it, this machine boots into Safe
        // Mode forever with no working Exo to undo it. Clearing it — or, in Safe Mode with a
        // sweep armed, running the sweep and then clearing it — has to happen before the UI,
        // the WebView, or anything else that can fail and skip it.
        try
        {
            // Not "Services.NvidiaDriverCleaner": NvidiaDriverCleaner is a static class in the
            // Exo.Services *namespace*, but `Services` binds to the AppServices property on line
            // 8, which shadows the namespace and does not have such a member. `using Exo.Services`
            // above already brings the class into scope unqualified.
            var sweep = NvidiaDriverCleaner.ResumeOrRecover();
            if (!string.IsNullOrEmpty(sweep)) Helpers.StartupLog.Mark("driver-sweep:" + sweep);
        }
        catch (Exception ex) { Helpers.StartupLog.Mark("driver-sweep-failed:" + ex.GetType().Name); }

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
