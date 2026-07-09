namespace OptiHub.Helpers;

public static class PathHelper
{
    public static string AppDirectory =>
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static string ScriptsRoot
    {
        get
        {
            var bundled = Path.Combine(AppDirectory, "Scripts");
            return bundled;
        }
    }

    public static string DiscordScriptsDir => Path.Combine(ScriptsRoot, "Discord");

    public static string SteamScriptsDir => Path.Combine(ScriptsRoot, "Steam");

    public static string AppDataDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OptiHub");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SettingsPath => Path.Combine(AppDataDir, "settings.json");

    public static string LogsDir
    {
        get
        {
            var dir = Path.Combine(AppDataDir, "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string WorkingScriptsDir
    {
        get
        {
            var dir = Path.Combine(AppDataDir, "scripts");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
