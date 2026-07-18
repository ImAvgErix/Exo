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
