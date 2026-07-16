namespace Exo.Helpers;

/// <summary>
/// Best-effort startup breadcrumbs (%LocalAppData%\Exo\logs\startup.log).
/// Survives crashes that bypass Application.UnhandledException (native/
/// composition failures) — the last line names the failing phase.
/// </summary>
public static class StartupLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Mark(string phase)
    {
        try
        {
            lock (Gate)
            {
                if (_path is null)
                {
                    _path = Path.Combine(PathHelper.LogsDir, "startup.log");
                    // Keep only the latest launch.
                    File.WriteAllText(_path, string.Empty);
                }

                File.AppendAllText(
                    _path,
                    $"[{DateTime.UtcNow:O}] {phase}{Environment.NewLine}");
            }
        }
        catch
        {
            // Never let diagnostics break startup.
        }
    }
}
