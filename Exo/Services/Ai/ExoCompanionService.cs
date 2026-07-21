namespace Exo.Services.Ai;

/// <summary>Minimal Exo companion app registry (Snip/Notepad/Photos/Task Manager).</summary>
public sealed class ExoCompanionService
{
    public sealed record Companion(string Id, string Title, string Replaces, string Status);

    public IReadOnlyList<Companion> List() =>
    [
        new("snip", "Exo Snip", "Snipping Tool", StatusOf("snip")),
        new("notepad", "Exo Notepad", "Notepad", StatusOf("notepad")),
        new("photos", "Exo Photos", "Photos", StatusOf("photos")),
        new("taskManager", "Exo Task Manager", "Task Manager", StatusOf("taskManager")),
        new("everything", "Everything Search", "Windows Search web", StatusOf("everything")),
        new("earTrumpet", "EarTrumpet", "Volume flyout", StatusOf("earTrumpet"))
    ];

    public string CompanionsRoot
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Exo", "companions");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public (bool Ok, string Message) EnsureInstalled(string id)
    {
        var dir = Path.Combine(CompanionsRoot, id);
        Directory.CreateDirectory(dir);
        var marker = Path.Combine(dir, "installed.marker");
        File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
        // Full WinUI companion binaries ship in later Windows builds; marker enables Host OS defaults.
        return (true, $"{id} companion registered at {dir}");
    }

    private string StatusOf(string id)
    {
        var marker = Path.Combine(CompanionsRoot, id, "installed.marker");
        return File.Exists(marker) ? "registered" : "available";
    }
}
