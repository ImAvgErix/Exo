using OptiHub.Helpers;

namespace OptiHub.Services;

/// <summary>
/// Ensures bundled scripts are available under LocalAppData for updates and runs.
/// Syncs the working Discord kit from the app-bundled Scripts\Discord folder when needed.
/// </summary>
public sealed class ScriptBundleService
{
    private readonly SettingsService _settings;
    private readonly object _syncLock = new();
    private string? _cachedRoot;
    private string? _cachedBundledVersion;
    private string? _lastSyncedWorkingVersion;
    private string? _lastSyncedBundledVersion;
    private bool _syncDone;

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

        // Skip filesystem work when this process already synced the same versions.
        if (_syncDone &&
            string.Equals(_lastSyncedBundledVersion, bundledVersion, StringComparison.Ordinal) &&
            string.Equals(_lastSyncedWorkingVersion, workingVersion, StringComparison.Ordinal) &&
            File.Exists(Path.Combine(working, "Disc-Optimizer.ps1")) &&
            File.Exists(Path.Combine(working, "OptiHub-Discord-Run.ps1")))
        {
            return;
        }

        var marker = Path.Combine(working, "Disc-Optimizer.ps1");
        var hubRun = Path.Combine(working, "OptiHub-Discord-Run.ps1");
        var desktopAsar = Path.Combine(working, "kit", "tools", "desktop.asar");
        var workingBroken =
            !File.Exists(marker) ||
            !File.Exists(hubRun) ||
            !File.Exists(desktopAsar) ||
            !File.ReadAllText(marker).Contains("Install-EquicordDirect", StringComparison.Ordinal) ||
            !File.ReadAllText(marker).Contains("Write-DiscordResourceBytes", StringComparison.Ordinal);

        // Never overwrite a newer GitHub-updated kit with an older app-bundled kit.
        if (IsVersionNewer(workingVersion, bundledVersion) && !workingBroken)
        {
            RememberSync(bundledVersion, workingVersion);
            return;
        }

        var needsFullSync =
            workingBroken ||
            IsVersionNewer(bundledVersion, workingVersion);

        if (needsFullSync)
        {
            CopyDirectory(bundled, working);
            workingVersion = File.Exists(workingVersionPath)
                ? File.ReadAllText(workingVersionPath).Trim()
                : bundledVersion;
            RememberSync(bundledVersion, workingVersion);
            return;
        }

        // Same version: refresh wrapper scripts from the app bundle.
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

        RememberSync(bundledVersion, workingVersion);
    }

    private void RememberSync(string bundledVersion, string workingVersion)
    {
        _lastSyncedBundledVersion = bundledVersion;
        _lastSyncedWorkingVersion = workingVersion;
        _syncDone = true;
    }

    public void ReplaceDiscordScriptsFrom(string sourceDir)
    {
        lock (_syncLock)
        {
            var working = Path.Combine(PathHelper.WorkingScriptsDir, "Discord");
            Directory.CreateDirectory(working);
            CopyDirectory(sourceDir, working);
            // Do not call EnsureDiscordScriptsSynced here — that can downgrade a newer kit.
            _cachedRoot = working;
            _cachedBundledVersion = null;
            _syncDone = false;
            _lastSyncedBundledVersion = null;
            _lastSyncedWorkingVersion = null;
        }
    }

    private static bool IsVersionNewer(string candidate, string baseline)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (string.IsNullOrWhiteSpace(baseline)) return true;
        static string Norm(string v) => v.Trim().TrimStart('v', 'V');
        if (Version.TryParse(Norm(candidate), out var c) &&
            Version.TryParse(Norm(baseline), out var b))
            return c > b;
        return !string.Equals(candidate, baseline, StringComparison.OrdinalIgnoreCase);
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
