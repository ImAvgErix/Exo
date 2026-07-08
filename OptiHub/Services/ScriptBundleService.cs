using OptiHub.Helpers;

namespace OptiHub.Services;

/// <summary>
/// Ensures bundled scripts are available under LocalAppData for updates and runs.
/// Always keeps the working Discord kit in sync with the app-bundled Scripts\Discord folder.
/// </summary>
public sealed class ScriptBundleService
{
    private readonly SettingsService _settings;
    private readonly object _syncLock = new();
    private string? _cachedRoot;
    private string? _cachedBundledVersion;

    public ScriptBundleService(SettingsService settings)
    {
        _settings = settings;
    }

    public string GetDiscordRoot()
    {
        var custom = _settings.Current.CustomScriptsPath;
        if (!string.IsNullOrWhiteSpace(custom) && Directory.Exists(custom))
            return custom;

        lock (_syncLock)
        {
            var working = Path.Combine(PathHelper.WorkingScriptsDir, "Discord");
            EnsureDiscordScriptsSynced(working);
            _cachedRoot = working;
            return working;
        }
    }

    public string DiscordOptimizerScript =>
        Path.Combine(GetDiscordRoot(), "OptiHub-Discord-Run.ps1");

    public string DiscordDetectScript =>
        Path.Combine(GetDiscordRoot(), "OptiHub-Discord-Detect.ps1");

    public string DiscordRepairScript =>
        Path.Combine(GetDiscordRoot(), "OptiHub-Discord-Repair.ps1");

    public string GetBundledVersion()
    {
        if (_cachedBundledVersion is not null)
            return _cachedBundledVersion;

        var versionFile = Path.Combine(PathHelper.DiscordScriptsDir, "VERSION");
        _cachedBundledVersion = File.Exists(versionFile)
            ? File.ReadAllText(versionFile).Trim()
            : _settings.Current.DiscordKitVersion;
        return _cachedBundledVersion;
    }

    public string GetWorkingVersion()
    {
        var root = GetDiscordRoot();
        var versionFile = Path.Combine(root, "VERSION");
        if (File.Exists(versionFile))
            return File.ReadAllText(versionFile).Trim();
        return GetBundledVersion();
    }

    private void EnsureDiscordScriptsSynced(string working)
    {
        var bundled = PathHelper.DiscordScriptsDir;
        if (!Directory.Exists(bundled))
            return;

        Directory.CreateDirectory(working);

        var bundledVersion = GetBundledVersion();
        var workingVersionPath = Path.Combine(working, "VERSION");
        var workingVersion = File.Exists(workingVersionPath)
            ? File.ReadAllText(workingVersionPath).Trim()
            : string.Empty;

        var marker = Path.Combine(working, "Disc-Optimizer.ps1");
        var hubRun = Path.Combine(working, "OptiHub-Discord-Run.ps1");
        var desktopAsar = Path.Combine(working, "kit", "tools", "desktop.asar");
        var needsFullSync =
            !File.Exists(marker) ||
            !File.Exists(hubRun) ||
            !File.Exists(desktopAsar) ||
            !string.Equals(workingVersion, bundledVersion, StringComparison.OrdinalIgnoreCase) ||
            !File.ReadAllText(marker).Contains("Install-EquicordDirect", StringComparison.Ordinal);

        if (needsFullSync)
        {
            CopyDirectory(bundled, working);
            return;
        }

        foreach (var name in new[]
                 {
                     "OptiHub-Discord-Run.ps1",
                     "OptiHub-Discord-Detect.ps1",
                     "OptiHub-Discord-Repair.ps1",
                     "Disc-Optimizer.ps1",
                     "VERSION"
                 })
        {
            var src = Path.Combine(bundled, name);
            var dst = Path.Combine(working, name);
            if (File.Exists(src))
                File.Copy(src, dst, overwrite: true);
        }
    }

    public void ReplaceDiscordScriptsFrom(string sourceDir)
    {
        lock (_syncLock)
        {
            var working = Path.Combine(PathHelper.WorkingScriptsDir, "Discord");
            Directory.CreateDirectory(working);
            CopyDirectory(sourceDir, working);
            EnsureDiscordScriptsSynced(working);
            _cachedRoot = working;
            _cachedBundledVersion = null;
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            File.Copy(file, Path.Combine(dest, name), overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            CopyDirectory(dir, Path.Combine(dest, name));
        }
    }
}