namespace Exo.Helpers;

/// <summary>
/// Best-effort startup breadcrumbs (%LocalAppData%\Exo\logs\startup.log).
/// Survives crashes that bypass Application.UnhandledException (native/
/// composition failures) — the last line names the failing phase.
/// Also detects crash loops: if the previous launch never reached
/// "first-frame-rendered", callers can boot in a reduced-risk safe mode.
/// </summary>
public static class StartupLog
{
    public const string FirstFrameMarker = "first-frame-rendered";

    private static readonly object Gate = new();
    private static string? _path;
    private static bool _previousRunReachedFirstFrame = true;
    private static bool _previousRunExists;

    /// <summary>True when the last launch died before presenting a frame.</summary>
    public static bool PreviousLaunchDiedBeforeFirstFrame
    {
        get
        {
            EnsureInitialized();
            return _previousRunExists && !_previousRunReachedFirstFrame;
        }
    }

    public static void Mark(string phase)
    {
        try
        {
            lock (Gate)
            {
                EnsureInitializedUnlocked();
                File.AppendAllText(
                    _path!,
                    $"[{DateTime.UtcNow:O}] {phase}{Environment.NewLine}");
            }
        }
        catch
        {
            // Never let diagnostics break startup.
        }
    }

    private static void EnsureInitialized()
    {
        try
        {
            lock (Gate) EnsureInitializedUnlocked();
        }
        catch { }
    }

    private static void EnsureInitializedUnlocked()
    {
        if (_path is not null) return;

        _path = Path.Combine(PathHelper.LogsDir, "startup.log");
        try
        {
            if (File.Exists(_path))
            {
                var previous = File.ReadAllText(_path);
                _previousRunExists = previous.Length > 0;
                _previousRunReachedFirstFrame =
                    previous.Contains(FirstFrameMarker, StringComparison.Ordinal);
            }
        }
        catch
        {
            _previousRunExists = false;
            _previousRunReachedFirstFrame = true;
        }

        // Keep only the latest launch.
        File.WriteAllText(_path, string.Empty);
    }
}
