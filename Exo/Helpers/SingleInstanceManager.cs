using Microsoft.Windows.AppLifecycle;

namespace Exo.Helpers;

/// <summary>Routes later launches to the first Exo process.</summary>
public static class SingleInstanceManager
{
    private const string InstanceKey = "ImAvgErix.Exo.Desktop";
    private static AppInstance? _mainInstance;

    public static bool IsPrimaryInstance()
    {
        var current = AppInstance.GetCurrent();
        var registered = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!registered.IsCurrent)
        {
            registered.RedirectActivationToAsync(current.GetActivatedEventArgs())
                .AsTask().GetAwaiter().GetResult();
            return false;
        }

        _mainInstance = registered;
        _mainInstance.Activated += (_, _) => App.TryActivateMainWindow();
        return true;
    }
}
