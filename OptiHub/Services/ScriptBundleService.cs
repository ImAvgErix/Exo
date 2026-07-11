using OptiHub.Helpers;
using System.Security.Cryptography;

namespace OptiHub.Services;

/// <summary>
/// Ensures bundled scripts are available under LocalAppData for updates and runs.
/// Syncs Discord, Steam, and NVIDIA kits from the app-bundled Scripts folders when needed.
/// </summary>
public sealed class ScriptBundleService
{
    private readonly SettingsService _settings;
    private readonly object _syncLock = new();
    private string? _cachedBundledVersion;
    private string? _lastSyncedWorkingVersion;
    private string? _lastSyncedBundledVersion;
    private bool _discordSyncDone;
    private bool _steamSyncDone;
    private bool _nvidiaSyncDone;

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
            return working;
        }
    }

    public string GetSteamRoot()
    {
        lock (_syncLock)
        {
            var working = Path.Combine(PathHelper.WorkingScriptsDir, "Steam");
            EnsureSteamScriptsSynced(working);
            return working;
        }
    }

    public string GetNvidiaRoot()
    {
        lock (_syncLock)
        {
            var working = Path.Combine(PathHelper.WorkingScriptsDir, "Nvidia");
            EnsureNvidiaScriptsSynced(working);
            return working;
        }
    }

    public string DiscordOptimizerScript =>
        Path.Combine(GetDiscordRoot(), "OptiHub-Discord-Run.ps1");

    public string DiscordDetectScript =>
        Path.Combine(GetDiscordRoot(), "OptiHub-Discord-Detect.ps1");

    public string DiscordRepairScript =>
        Path.Combine(GetDiscordRoot(), "OptiHub-Discord-Repair.ps1");

    public string SteamOptimizerScript =>
        Path.Combine(GetSteamRoot(), "OptiHub-Steam-Run.ps1");

    public string SteamDetectScript =>
        Path.Combine(GetSteamRoot(), "OptiHub-Steam-Detect.ps1");

    public string SteamRepairScript =>
        Path.Combine(GetSteamRoot(), "OptiHub-Steam-Repair.ps1");

    public string NvidiaOptimizerScript =>
        Path.Combine(GetNvidiaRoot(), "OptiHub-Nvidia-Run.ps1");

    public string NvidiaDetectScript =>
        Path.Combine(GetNvidiaRoot(), "OptiHub-Nvidia-Detect.ps1");

    public string NvidiaRepairScript =>
        Path.Combine(GetNvidiaRoot(), "OptiHub-Nvidia-Repair.ps1");

    public string GetBundledVersion()
    {
        lock (_syncLock)
        {
            if (_cachedBundledVersion is not null)
                return _cachedBundledVersion;

            var versionFile = Path.Combine(PathHelper.DiscordScriptsDir, "VERSION");
            _cachedBundledVersion = File.Exists(versionFile)
                ? File.ReadAllText(versionFile).Trim()
                : _settings.Current.DiscordKitVersion;
            return _cachedBundledVersion;
        }
    }

    public string GetWorkingVersion()
    {
        lock (_syncLock)
        {
            var root = GetDiscordRoot();
            var versionFile = Path.Combine(root, "VERSION");
            if (File.Exists(versionFile))
                return File.ReadAllText(versionFile).Trim();
            return GetBundledVersion();
        }
    }

    private void EnsureSteamScriptsSynced(string working)
    {
        var bundled = PathHelper.SteamScriptsDir;
        if (!Directory.Exists(bundled))
            return;

        Directory.CreateDirectory(working);

        var marker = Path.Combine(working, "Steam-Optimizer.ps1");
        var hubRun = Path.Combine(working, "OptiHub-Steam-Run.ps1");
        var bundledVersionPath = Path.Combine(bundled, "VERSION");
        var workingVersionPath = Path.Combine(working, "VERSION");
        var bundledVersion = File.Exists(bundledVersionPath)
            ? File.ReadAllText(bundledVersionPath).Trim()
            : "0";
        var workingVersion = File.Exists(workingVersionPath)
            ? File.ReadAllText(workingVersionPath).Trim()
            : "";

        if (_steamSyncDone &&
            File.Exists(marker) &&
            File.Exists(hubRun) &&
            string.Equals(bundledVersion, workingVersion, StringComparison.Ordinal))
            return;

        var broken =
            !File.Exists(marker) ||
            !File.Exists(hubRun) ||
            !File.Exists(Path.Combine(working, "OptiHub-Steam-Detect.ps1")) ||
            IsVersionNewer(bundledVersion, workingVersion);

        if (broken || IsVersionNewer(bundledVersion, workingVersion))
            CopyDirectory(bundled, working);
        else
        {
            foreach (var name in new[]
                     {
                         "Steam-Optimizer.ps1",
                         "OptiHub-Steam-Run.ps1",
                         "OptiHub-Steam-Detect.ps1",
                         "OptiHub-Steam-Repair.ps1",
                         "VERSION"
                     })
            {
                var src = Path.Combine(bundled, name);
                var dst = Path.Combine(working, name);
                if (File.Exists(src))
                    File.Copy(src, dst, overwrite: true);
            }
        }

        _steamSyncDone = true;
    }

    private void EnsureNvidiaScriptsSynced(string working)
    {
        var bundled = PathHelper.NvidiaScriptsDir;
        if (!Directory.Exists(bundled))
            return;

        Directory.CreateDirectory(working);

        var marker = Path.Combine(working, "Nvidia-Optimizer.ps1");
        var hubRun = Path.Combine(working, "OptiHub-Nvidia-Run.ps1");
        var bundledVersionPath = Path.Combine(bundled, "VERSION");
        var workingVersionPath = Path.Combine(working, "VERSION");
        var bundledVersion = File.Exists(bundledVersionPath)
            ? File.ReadAllText(bundledVersionPath).Trim()
            : "0";
        var workingVersion = File.Exists(workingVersionPath)
            ? File.ReadAllText(workingVersionPath).Trim()
            : "";
        var bundledHelper = Path.Combine(bundled, "tools", "OptiHub.NvDisplay.exe");
        var workingHelper = Path.Combine(working, "tools", "OptiHub.NvDisplay.exe");
        var helperMismatch = File.Exists(bundledHelper) && !FilesMatch(bundledHelper, workingHelper);

        if (_nvidiaSyncDone &&
            File.Exists(marker) &&
            File.Exists(hubRun) &&
            Directory.Exists(Path.Combine(working, "profiles")) &&
            !helperMismatch &&
            string.Equals(bundledVersion, workingVersion, StringComparison.Ordinal))
            return;

        var broken =
            !File.Exists(marker) ||
            !File.Exists(hubRun) ||
            !File.Exists(Path.Combine(working, "OptiHub-Nvidia-Detect.ps1")) ||
            !Directory.Exists(Path.Combine(working, "profiles")) ||
            helperMismatch ||
            IsVersionNewer(bundledVersion, workingVersion);

        if (broken || IsVersionNewer(bundledVersion, workingVersion))
        {
            CopyDirectory(bundled, working);
            if (File.Exists(bundledHelper) && !FilesMatch(bundledHelper, workingHelper))
                throw new InvalidDataException("The NVIDIA display helper did not synchronize correctly.");
        }

        _nvidiaSyncDone = true;
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

        if (_discordSyncDone &&
            string.Equals(_lastSyncedBundledVersion, bundledVersion, StringComparison.Ordinal) &&
            string.Equals(_lastSyncedWorkingVersion, workingVersion, StringComparison.Ordinal) &&
            File.Exists(Path.Combine(working, "Disc-Optimizer.ps1")) &&
            File.Exists(Path.Combine(working, "OptiHub-Discord-Run.ps1")))
        {
            return;
        }

        var marker = Path.Combine(working, "Disc-Optimizer.ps1");
        var hubRun = Path.Combine(working, "OptiHub-Discord-Run.ps1");
        var repairScript = Path.Combine(working, "OptiHub-Discord-Repair.ps1");
        var repairText = File.Exists(repairScript) ? File.ReadAllText(repairScript) : string.Empty;
        var repairBroken =
            !File.Exists(repairScript) ||
            !repairText.Contains("ASCII-only source", StringComparison.Ordinal) ||
            !repairText.Contains("Write-HubProgress", StringComparison.Ordinal) ||
            repairText.Any(c => c > 127);

        var profilesDir = Path.Combine(working, "kit", "profiles");
        var eqManifest = Path.Combine(profilesDir, "equicordplugins.json");
        var vcManifest = Path.Combine(profilesDir, "vencordplugins.json");
        var overrides = Path.Combine(profilesDir, "equicord-overrides.json");
        var libLogging = Path.Combine(working, "kit", "lib", "10-Logging.ps1");
        var manifestsBroken =
            !File.Exists(eqManifest) ||
            !File.Exists(vcManifest) ||
            !File.Exists(overrides) ||
            !File.Exists(libLogging) ||
            new FileInfo(eqManifest).Length < 10_000;

        var workingBroken =
            !File.Exists(marker) ||
            !File.Exists(hubRun) ||
            repairBroken ||
            manifestsBroken ||
            !File.ReadAllText(marker).Contains("Install-EquicordDirect", StringComparison.Ordinal) ||
            !File.ReadAllText(marker).Contains("Write-DiscordResourceBytes", StringComparison.Ordinal);

        if (IsVersionNewer(workingVersion, bundledVersion) && !workingBroken)
        {
            RememberDiscordSync(bundledVersion, workingVersion);
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
            RememberDiscordSync(bundledVersion, workingVersion);
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

        // Always refresh modular lib when present in bundle.
        var libSrc = Path.Combine(bundled, "kit", "lib");
        var libDst = Path.Combine(working, "kit", "lib");
        if (Directory.Exists(libSrc))
            CopyDirectory(libSrc, libDst);

        RememberDiscordSync(bundledVersion, workingVersion);
    }

    private void RememberDiscordSync(string bundledVersion, string workingVersion)
    {
        _lastSyncedBundledVersion = bundledVersion;
        _lastSyncedWorkingVersion = workingVersion;
        _discordSyncDone = true;
    }

    public void ReplaceDiscordScriptsFrom(string sourceDir) =>
        ReplaceKitScriptsFrom(
            sourceDir,
            kitName: "Discord",
            requiredFiles: ["Disc-Optimizer.ps1", "OptiHub-Discord-Run.ps1"],
            resetCache: ResetDiscordCache);

    public void ReplaceSteamScriptsFrom(string sourceDir) =>
        ReplaceKitScriptsFrom(
            sourceDir,
            kitName: "Steam",
            requiredFiles: ["Steam-Optimizer.ps1", "OptiHub-Steam-Run.ps1", "OptiHub-Steam-Detect.ps1"],
            resetCache: () => _steamSyncDone = false);

    public void ReplaceNvidiaScriptsFrom(string sourceDir) =>
        ReplaceKitScriptsFrom(
            sourceDir,
            kitName: "Nvidia",
            requiredFiles: ["Nvidia-Optimizer.ps1", "OptiHub-Nvidia-Run.ps1", "OptiHub-Nvidia-Detect.ps1"],
            resetCache: () => _nvidiaSyncDone = false);

    public string GetWorkingKitVersion(string kitName)
    {
        lock (_syncLock)
        {
            var versionFile = Path.Combine(PathHelper.WorkingScriptsDir, kitName, "VERSION");
            if (File.Exists(versionFile))
                return File.ReadAllText(versionFile).Trim();
            var bundled = Path.Combine(PathHelper.ScriptsRoot, kitName, "VERSION");
            return File.Exists(bundled) ? File.ReadAllText(bundled).Trim() : "0";
        }
    }

    private void ReplaceKitScriptsFrom(
        string sourceDir,
        string kitName,
        string[] requiredFiles,
        Action resetCache)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(kitName);

        lock (_syncLock)
        {
            var working = Path.Combine(PathHelper.WorkingScriptsDir, kitName);
            var staging = Path.Combine(PathHelper.WorkingScriptsDir, $"{kitName}.update-{Guid.NewGuid():N}");
            var backup = Path.Combine(PathHelper.WorkingScriptsDir, $"{kitName}.backup-{Guid.NewGuid():N}");
            var movedCurrent = false;

            try
            {
                CopyDirectory(sourceDir, staging);
                foreach (var required in requiredFiles)
                {
                    if (!File.Exists(Path.Combine(staging, required)))
                        throw new InvalidDataException($"Updated {kitName} script bundle is incomplete (missing {required}).");
                }

                if (Directory.Exists(working))
                {
                    Directory.Move(working, backup);
                    movedCurrent = true;
                }

                Directory.Move(staging, working);
                resetCache();
            }
            catch
            {
                if (movedCurrent && !Directory.Exists(working) && Directory.Exists(backup))
                {
                    try { Directory.Move(backup, working); } catch { /* preserve original exception */ }
                }
                throw;
            }
            finally
            {
                TryDeleteDirectory(staging);
                if (Directory.Exists(working))
                    TryDeleteDirectory(backup);
            }
        }
    }

    private void ResetDiscordCache()
    {
        _cachedBundledVersion = null;
        _discordSyncDone = false;
        _lastSyncedBundledVersion = null;
        _lastSyncedWorkingVersion = null;
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

    private static bool FilesMatch(string source, string destination)
    {
        try
        {
            if (!File.Exists(source) || !File.Exists(destination))
                return false;
            if (new FileInfo(source).Length != new FileInfo(destination).Length)
                return false;

            using var sourceStream = File.OpenRead(source);
            using var destinationStream = File.OpenRead(destination);
            var sourceHash = SHA256.HashData(sourceStream);
            var destinationHash = SHA256.HashData(destinationStream);
            return sourceHash.AsSpan().SequenceEqual(destinationHash);
        }
        catch
        {
            return false;
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                continue;
            var name = Path.GetFileName(file);
            File.Copy(file, Path.Combine(dest, name), overwrite: true);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                continue;
            var name = Path.GetFileName(dir);
            CopyDirectory(dir, Path.Combine(dest, name));
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // The active bundle is already selected; stale cleanup is best-effort.
        }
    }
}
