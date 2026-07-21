namespace Exo.Services.Ai;

/// <summary>
/// Desktop control for Settings/CPL/vendor UIs when APIs are missing.
/// Scoped to optimization tools — never global game hooks / anti-cheat UI.
/// </summary>
public sealed class ExoPcControl
{
    public bool IsAvailable => OperatingSystem.IsWindows();

    public (bool Ok, string Message) OpenUri(string uri)
    {
        if (!IsAvailable)
            return (false, "ExoPcControl requires Windows");

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true
            });
            return (true, "opened " + uri);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public (bool Ok, string Message) OpenWindowsSettings(string page) =>
        OpenUri("ms-settings:" + page.TrimStart(':'));

    public (bool Ok, string Message) OpenControlPanel(string applet) =>
        OpenUri("control.exe " + applet);

    /// <summary>Placeholder for UIA click/type sequences — bound on Windows host builds.</summary>
    public Task<(bool Ok, string Message)> RunUiSequenceAsync(
        string sequenceId,
        CancellationToken ct = default)
    {
        if (!IsAvailable)
            return Task.FromResult((false, "UI automation requires Windows"));

        // Sequences registered by Host OS / GPU Control (display Hz, default apps, etc.).
        return Task.FromResult((true, $"ui sequence queued: {sequenceId}"));
    }
}
