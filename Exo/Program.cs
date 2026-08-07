using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace Exo;

public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    /// <summary>Required so taskbar/start menu group to a stable identity with our icon.</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    public const string AppUserModelId = "ImAvgErix.Exo";

    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            Helpers.NativeProcessSecurity.HardenDllSearch();

            // Every optimizer parses app/game config files with regexes. A corrupt or
            // hostile config (one enormous line, no newline) can drive catastrophic
            // backtracking and hang an Apply with no way out. This process-wide default
            // bounds every Regex call that doesn't set its own timeout, turning a hang
            // into a caught RegexMatchTimeoutException the module can report.
            AppDomain.CurrentDomain.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", TimeSpan.FromSeconds(5));

            // A redirected secondary launch must not reset the primary process's
            // crash-loop log or initialize a second WinUI compositor.
            if (!Helpers.SingleInstanceManager.IsPrimaryInstance())
                return;

            Helpers.StartupDiagnostics.EnterPhase("main");

            // Must run before any HWND is created or taskbar uses a generic icon.
            try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); } catch { }

            Helpers.StartupDiagnostics.EnterPhase("xaml-requirements");
            XamlCheckProcessRequirements();
            ComWrappersSupport.InitializeComWrappers();
            Helpers.StartupDiagnostics.EnterPhase("xaml-runtime-ready");
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
            Helpers.StartupDiagnostics.EnterPhase("application-exited");
        }
        catch (Exception ex)
        {
            Helpers.StartupDiagnostics.WriteFatal(ex);
            throw;
        }
    }
}
