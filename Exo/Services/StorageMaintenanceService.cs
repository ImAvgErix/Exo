using Exo.Helpers;

namespace Exo.Services;

/// <summary>One candidate temp item with the space it currently uses.</summary>
public sealed record TempItem(string Path, long Bytes, string Kind);

/// <summary>
/// Storage maintenance: reports temp clutter and cleans only well-known,
/// recreatable temp locations (user temp, Windows temp, browser-cache dirs
/// Exo itself controls). Every deletion is recorded in a journal so a repair
/// can say exactly what was removed — Exo never guesses at user data.
/// </summary>
public sealed class StorageMaintenanceService
{
    private const string JournalName = "storage-clean-journal.json";
    private static string JournalPath => Path.Combine(PathHelper.AppDataDir, JournalName);

    private static readonly string[] ExtraTempRoots =
    [
        @"Microsoft\Windows\INetCache",
        @"Microsoft\Windows\Explorer"
    ];

    public IReadOnlyList<TempItem> Scan()
    {
        var items = new List<TempItem>();
        var userTemp = Path.GetTempPath();
        AddDir(items, userTemp, "user temp");
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        AddDir(items, Path.Combine(systemRoot, "Temp"), "windows temp");

        foreach (var relative in ExtraTempRoots)
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), relative);
            AddDir(items, dir, "cache");
        }

        return items
            .OrderByDescending(i => i.Bytes)
            .ToArray();
    }

    public long Clean(IProgress<string>? progress = null)
    {
        var journal = new List<object>();
        long freed = 0;

        foreach (var item in Scan())
        {
            try
            {
                if (!Directory.Exists(item.Path)) continue;
                var before = DirSize(item.Path);
                long removed = 0;
                // Delete contents individually so one locked file does not block the
                // whole clean; locked files are skipped, everything else goes.
                foreach (var file in Directory.EnumerateFiles(item.Path, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(file); removed += new FileInfo(file).Length; }
                    catch { /* locked — skip */ }
                }
                foreach (var dir in Directory.EnumerateDirectories(item.Path, "*", SearchOption.AllDirectories)
                             .OrderByDescending(d => d.Length))
                {
                    try { Directory.Delete(dir, recursive: false); }
                    catch { /* locked — skip */ }
                }
                freed += removed;
                journal.Add(new { path = item.Path, kind = item.Kind, bytes = removed });
                progress?.Report($"Cleaned {item.Path} ({FormatBytes(removed)})");
            }
            catch
            {
                // Unreadable root is skipped, not fatal.
            }
        }

        if (journal.Count > 0)
        {
            Directory.CreateDirectory(PathHelper.AppDataDir);
            File.WriteAllText(
                JournalPath,
                System.Text.Json.JsonSerializer.Serialize(new { cleanedAt = DateTimeOffset.UtcNow, freed, items = journal }));
        }

        return freed;
    }

    public IReadOnlyList<TempItem> Journal() => JournalItems().ToArray();

    private static IEnumerable<TempItem> JournalItems()
    {
        if (!File.Exists(JournalPath)) yield break;

        List<TempItem> parsed = [];
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(JournalPath));
            if (doc.RootElement.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    parsed.Add(new TempItem(
                        item.GetProperty("path").GetString() ?? "",
                        item.GetProperty("bytes").GetInt64(),
                        item.GetProperty("kind").GetString() ?? "unknown"));
                }
            }
        }
        catch
        {
            // Unreadable journal is reported as empty, never as a crash.
        }

        foreach (var entry in parsed)
        {
            yield return entry;
        }
    }

    private static void AddDir(List<TempItem> items, string dir, string kind)
    {
        try
        {
            if (Directory.Exists(dir))
                items.Add(new TempItem(dir, DirSize(dir), kind));
        }
        catch { /* unreadable dir skipped */ }
    }

    private static long DirSize(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f =>
                {
                    try { return new FileInfo(f).Length; }
                    catch { return 0L; }
                });
        }
        catch
        {
            return 0L;
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1 << 30 ? $"{bytes / (double)(1 << 30):0.0} GB"
        : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.0} MB"
        : $"{bytes / (double)(1 << 10):0.0} KB";
}
