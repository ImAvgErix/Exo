using OptiHub.Helpers;
using OptiHub.Models;

namespace OptiHub.Services;

/// <summary>
/// Ensures bundled scripts are available under LocalAppData for updates and runs.
/// </summary>
public sealed class ScriptBundleService
{
    private readonly SettingsService _settings;
    private readonly object _syncLock = new();
    private string? _cachedRoot;
    private string? _cachedBundledVersion;
    private string? _lastSyncedBundleVersion;

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
            if (_cachedRoot is not null && Directory.Exists(_cachedRoot))
            {
                EnsureWrappersFresh(_cachedRoot);
                return _cachedRoot;
            }

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

        var marker = Path.Combine(working, "Disc-Optimizer.ps1");
        var hubRun = Path.Combine(working, "OptiHub-Discord-Run.ps1");
        var bundledVersion = GetBundledVersion();
        var workingVersionPath = Path.Combine(working, "VERSION");
        var workingVersion = File.Exists(workingVersionPath)
            ? File.ReadAllText(workingVersionPath).Trim()
            : string.Empty;

        if (!File.Exists(marker) || !File.Exists(hubRun) ||
            !string.Equals(workingVersion, bundledVersion, StringComparison.OrdinalIgnoreCase))
        {
            CopyDirectory(bundled, working);
            _lastSyncedBundleVersion = bundledVersion;
            return;
        }

        EnsureWrappersFresh(working);
        _lastSyncedBundleVersion = bundledVersion;
    }

    private void EnsureWrappersFresh(string working)
    {
        var bundled = PathHelper.DiscordScriptsDir;
        if (!Directory.Exists(bundled))
            return;

        // Only re-copy wrappers when the app bundle version changed
        var bundledVersion = GetBundledVersion();
        if (string.Equals(_lastSyncedBundleVersion, bundledVersion, StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var name in new[]
                 {
                     "OptiHub-Discord-Run.ps1",
                     "OptiHub-Discord-Detect.ps1",
                     "OptiHub-Discord-Repair.ps1"
                 })
        {
            var src = Path.Combine(bundled, name);
            var dst = Path.Combine(working, name);
            if (File.Exists(src))
                File.Copy(src, dst, overwrite: true);
        }

        _lastSyncedBundleVersion = bundledVersion;
    }

    public void ReplaceDiscordScriptsFrom(string sourceDir)
    {
        lock (_syncLock)
        {
            var working = Path.Combine(PathHelper.WorkingScriptsDir, "Discord");
            if (Directory.Exists(working))
            {
                var wrappers = new Dictionary<string, string>();
                foreach (var name in new[]
                         {
                             "OptiHub-Discord-Run.ps1",
                             "OptiHub-Discord-Detect.ps1",
                             "OptiHub-Discord-Repair.ps1"
                         })
                {
                    var p = Path.Combine(working, name);
                    if (File.Exists(p)) wrappers[name] = File.ReadAllText(p);
                }

                try
                {
                    Directory.Delete(working, recursive: true);
                }
                catch
                {
                    // fall through to overwrite copy
                }

                Directory.CreateDirectory(working);
                CopyDirectory(sourceDir, working);

                foreach (var (name, content) in wrappers)
                {
                    var dst = Path.Combine(working, name);
                    if (!File.Exists(dst))
                        File.WriteAllText(dst, content);
                }

                EnsureDiscordScriptsSynced(working);
            }
            else
            {
                Directory.CreateDirectory(working);
                CopyDirectory(sourceDir, working);
                EnsureDiscordScriptsSynced(working);
            }

            _cachedRoot = working;
            _cachedBundledVersion = null;
            _lastSyncedBundleVersion = null;
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
