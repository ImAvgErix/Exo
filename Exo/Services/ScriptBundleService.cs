using System.Security.Cryptography;
using Exo.Helpers;

namespace Exo.Services;

/// <summary>
/// Materializes optimizer kits under LocalAppData for runs and GitHub refreshes.
/// Policy: each Exo app version owns a complete kit set. When the app version
/// changes, working kits are fully replaced from the bundled Scripts tree — never
/// merged with leftovers from an older install.
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
    private bool _appKitStampChecked;

    public ScriptBundleService(SettingsService settings)
    {
        _settings = settings;
    }

    private static string AppVersionText
    {
        get
        {
            var ver = typeof(ScriptBundleService).Assembly.GetName().Version;
            return ver is null ? "0.0.0" : $"{ver.Major}.{ver.Minor}.{ver.Build}";
        }
    }

    private static string KitStampPath =>
        Path.Combine(PathHelper.WorkingScriptsDir, ".app-kit-stamp");

    /// <summary>
    /// Call once at startup. If this Exo build differs from the stamp left by
    /// the previous run/install, wipe and reinstall Discord/Steam/NVIDIA kits from
    /// the app bundle so the UI and scripts always match.
    /// </summary>
    public void EnsureKitsMatchThisApp()
    {
        lock (_syncLock)
        {
            EnsureAppKitStampCore();
        }
    }

    public string GetDiscordRoot()
    {
        var custom = _settings.Current.CustomScriptsPath;
        if (!string.IsNullOrWhiteSpace(custom) && Directory.Exists(custom))
            return custom;

        lock (_syncLock)
        {
            EnsureAppKitStampCore();
            var working = Path.Combine(PathHelper.WorkingScriptsDir, "Discord");
            EnsureDiscordScriptsSynced(working);
            return working;
        }
    }

    public string GetSteamRoot()
    {
        lock (_syncLock)
        {
            EnsureAppKitStampCore();
            var working = Path.Combine(PathHelper.WorkingScriptsDir, "Steam");
            EnsureSteamScriptsSynced(working);
            return working;
        }
    }

    public string GetNvidiaRoot()
    {
        lock (_syncLock)
        {
            EnsureAppKitStampCore();
            var working = Path.Combine(PathHelper.WorkingScriptsDir, "Nvidia");
            EnsureNvidiaScriptsSynced(working);
            return working;
        }
    }

    public string DiscordOptimizerScript =>
        Path.Combine(GetDiscordRoot(), "Exo-Discord-Run.ps1");

    public string DiscordDetectScript =>
        Path.Combine(GetDiscordRoot(), "Exo-Discord-Detect.ps1");

    public string DiscordRepairScript =>
        Path.Combine(GetDiscordRoot(), "Exo-Discord-Repair.ps1");

    public string SteamOptimizerScript =>
        Path.Combine(GetSteamRoot(), "Exo-Steam-Run.ps1");

    public string SteamDetectScript =>
        Path.Combine(GetSteamRoot(), "Exo-Steam-Detect.ps1");

    public string SteamRepairScript =>
        Path.Combine(GetSteamRoot(), "Exo-Steam-Repair.ps1");

    public string NvidiaOptimizerScript =>
        Path.Combine(GetNvidiaRoot(), "Exo-Nvidia-Run.ps1");

    public string NvidiaDetectScript =>
        Path.Combine(GetNvidiaRoot(), "Exo-Nvidia-Detect.ps1");

    public string NvidiaRepairScript =>
        Path.Combine(GetNvidiaRoot(), "Exo-Nvidia-Repair.ps1");

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

    private void EnsureAppKitStampCore()
    {
        if (_appKitStampChecked)
            return;

        var appVersion = AppVersionText;
        var stamp = File.Exists(KitStampPath)
            ? File.ReadAllText(KitStampPath).Trim()
            : string.Empty;

        if (!string.Equals(stamp, appVersion, StringComparison.Ordinal))
        {
            // New app install/upgrade: full kit replace from this binary's Scripts/.
            // Never keep half of an older kit next to a newer UI.
            ResetWorkingKitsFromBundledUnlocked();
            try
            {
                Directory.CreateDirectory(PathHelper.WorkingScriptsDir);
                File.WriteAllText(KitStampPath, appVersion + Environment.NewLine);
            }
            catch
            {
                // Next launch will retry the stamp; kits were still refreshed best-effort.
            }
        }

        _appKitStampChecked = true;
    }

    /// <summary>
    /// Atomically reinstall Discord / Steam / NVIDIA working kits from the app bundle.
    /// </summary>
    private void ResetWorkingKitsFromBundledUnlocked()
    {
        ReplaceWorkingKitFromBundled("Discord", PathHelper.DiscordScriptsDir,
            ["Disc-Optimizer.ps1", "Exo-Discord-Run.ps1"]);
        ReplaceWorkingKitFromBundled("Steam", PathHelper.SteamScriptsDir,
            ["Steam-Optimizer.ps1", "Exo-Steam-Run.ps1", "Exo-Steam-Detect.ps1"]);
        ReplaceWorkingKitFromBundled("Nvidia", PathHelper.NvidiaScriptsDir,
            ["Nvidia-Optimizer.ps1", "Exo-Nvidia-Run.ps1", "Exo-Nvidia-Detect.ps1"]);

        _discordSyncDone = false;
        _steamSyncDone = false;
        _nvidiaSyncDone = false;
        _cachedBundledVersion = null;
        _lastSyncedBundledVersion = null;
        _lastSyncedWorkingVersion = null;
    }

    private static void ReplaceWorkingKitFromBundled(string kitName, string bundled, string[] requiredFiles)
    {
        if (!Directory.Exists(bundled))
            return;

        foreach (var required in requiredFiles)
        {
            if (!File.Exists(Path.Combine(bundled, required)))
                return; // incomplete bundle; leave existing working kit alone
        }

        var working = Path.Combine(PathHelper.WorkingScriptsDir, kitName);
        var staging = Path.Combine(PathHelper.WorkingScriptsDir, $"{kitName}.fresh-{Guid.NewGuid():N}");
        var backup = Path.Combine(PathHelper.WorkingScriptsDir, $"{kitName}.prev-{Guid.NewGuid():N}");
        var moved = false;

        try
        {
            CopyDirectory(bundled, staging);
            foreach (var required in requiredFiles)
            {
                if (!File.Exists(Path.Combine(staging, required)))
                    throw new InvalidDataException($"Bundled {kitName} kit is incomplete (missing {required}).");
            }

            if (Directory.Exists(working))
            {
                Directory.Move(working, backup);
                moved = true;
            }

            Directory.Move(staging, working);
        }
        catch
        {
            if (moved && !Directory.Exists(working) && Directory.Exists(backup))
            {
                try { Directory.Move(backup, working); } catch { /* preserve original */ }
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

    private void EnsureSteamScriptsSynced(string working)
    {
        var bundled = PathHelper.SteamScriptsDir;
        if (!Directory.Exists(bundled))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(working)!);

        var marker = Path.Combine(working, "Steam-Optimizer.ps1");
        var hubRun = Path.Combine(working, "Exo-Steam-Run.ps1");
        var detect = Path.Combine(working, "Exo-Steam-Detect.ps1");
        var bundledVersion = ReadVersionFile(Path.Combine(bundled, "VERSION")) ?? "0";
        var workingVersion = ReadVersionFile(Path.Combine(working, "VERSION")) ?? "";

        if (_steamSyncDone &&
            File.Exists(marker) &&
            File.Exists(hubRun) &&
            File.Exists(detect) &&
            string.Equals(bundledVersion, workingVersion, StringComparison.Ordinal))
            return;

        var workingBroken =
            !File.Exists(marker) ||
            !File.Exists(hubRun) ||
            !File.Exists(detect);

        // After an app upgrade stamp reset, kits already match the bundle.
        // Mid-version GitHub pulls may leave working newer than bundle — keep those.
        if (!workingBroken &&
            IsVersionNewer(workingVersion, bundledVersion))
        {
            _steamSyncDone = true;
            return;
        }

        if (workingBroken ||
            IsVersionNewer(bundledVersion, workingVersion) ||
            string.IsNullOrWhiteSpace(workingVersion) ||
            !string.Equals(bundledVersion, workingVersion, StringComparison.Ordinal))
        {
            ReplaceWorkingKitFromBundled("Steam", bundled,
                ["Steam-Optimizer.ps1", "Exo-Steam-Run.ps1", "Exo-Steam-Detect.ps1"]);
        }

        _steamSyncDone = true;
    }

    private void EnsureNvidiaScriptsSynced(string working)
    {
        var bundled = PathHelper.NvidiaScriptsDir;
        if (!Directory.Exists(bundled))
            return;

        var marker = Path.Combine(working, "Nvidia-Optimizer.ps1");
        var hubRun = Path.Combine(working, "Exo-Nvidia-Run.ps1");
        var detect = Path.Combine(working, "Exo-Nvidia-Detect.ps1");
        var bundledVersion = ReadVersionFile(Path.Combine(bundled, "VERSION")) ?? "0";
        var workingVersion = ReadVersionFile(Path.Combine(working, "VERSION")) ?? "";
        var bundledHelper = Path.Combine(bundled, "tools", "Exo.NvDisplay.exe");
        var workingHelper = Path.Combine(working, "tools", "Exo.NvDisplay.exe");
        var helperMismatch = File.Exists(bundledHelper) && !FilesMatch(bundledHelper, workingHelper);

        if (_nvidiaSyncDone &&
            File.Exists(marker) &&
            File.Exists(hubRun) &&
            Directory.Exists(Path.Combine(working, "profiles")) &&
            !helperMismatch &&
            string.Equals(bundledVersion, workingVersion, StringComparison.Ordinal))
            return;

        var workingBroken =
            !File.Exists(marker) ||
            !File.Exists(hubRun) ||
            !File.Exists(detect) ||
            !Directory.Exists(Path.Combine(working, "profiles")) ||
            helperMismatch;

        if (!workingBroken && IsVersionNewer(workingVersion, bundledVersion))
        {
            _nvidiaSyncDone = true;
            return;
        }

        if (workingBroken ||
            IsVersionNewer(bundledVersion, workingVersion) ||
            string.IsNullOrWhiteSpace(workingVersion) ||
            !string.Equals(bundledVersion, workingVersion, StringComparison.Ordinal))
        {
            ReplaceWorkingKitFromBundled("Nvidia", bundled,
                ["Nvidia-Optimizer.ps1", "Exo-Nvidia-Run.ps1", "Exo-Nvidia-Detect.ps1"]);
            if (File.Exists(bundledHelper) &&
                !FilesMatch(bundledHelper, Path.Combine(working, "tools", "Exo.NvDisplay.exe")))
                throw new InvalidDataException("The NVIDIA display helper did not synchronize correctly.");
        }

        _nvidiaSyncDone = true;
    }

    private void EnsureDiscordScriptsSynced(string working)
    {
        var bundled = PathHelper.DiscordScriptsDir;
        if (!Directory.Exists(bundled))
            return;

        var bundledVersion = GetBundledVersion();
        var workingVersionPath = Path.Combine(working, "VERSION");
        var workingVersion = ReadVersionFile(workingVersionPath) ?? string.Empty;

        if (_discordSyncDone &&
            string.Equals(_lastSyncedBundledVersion, bundledVersion, StringComparison.Ordinal) &&
            string.Equals(_lastSyncedWorkingVersion, workingVersion, StringComparison.Ordinal) &&
            File.Exists(Path.Combine(working, "Disc-Optimizer.ps1")) &&
            File.Exists(Path.Combine(working, "Exo-Discord-Run.ps1")))
        {
            return;
        }

        var marker = Path.Combine(working, "Disc-Optimizer.ps1");
        var hubRun = Path.Combine(working, "Exo-Discord-Run.ps1");
        var repairScript = Path.Combine(working, "Exo-Discord-Repair.ps1");
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

        // Mid-version GitHub kit may be newer than this app's bundle — keep it intact.
        if (IsVersionNewer(workingVersion, bundledVersion) && !workingBroken)
        {
            RememberDiscordSync(bundledVersion, workingVersion);
            return;
        }

        // Always full-tree replace. Never partial-copy a few .ps1 files over an old kit.
        if (workingBroken ||
            IsVersionNewer(bundledVersion, workingVersion) ||
            string.IsNullOrWhiteSpace(workingVersion) ||
            !string.Equals(bundledVersion, workingVersion, StringComparison.Ordinal))
        {
            ReplaceWorkingKitFromBundled("Discord", bundled,
                ["Disc-Optimizer.ps1", "Exo-Discord-Run.ps1"]);
            workingVersion = ReadVersionFile(workingVersionPath) ?? bundledVersion;
        }

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
            requiredFiles: ["Disc-Optimizer.ps1", "Exo-Discord-Run.ps1"],
            resetCache: ResetDiscordCache);

    public void ReplaceSteamScriptsFrom(string sourceDir) =>
        ReplaceKitScriptsFrom(
            sourceDir,
            kitName: "Steam",
            requiredFiles: ["Steam-Optimizer.ps1", "Exo-Steam-Run.ps1", "Exo-Steam-Detect.ps1"],
            resetCache: () => _steamSyncDone = false);

    public void ReplaceNvidiaScriptsFrom(string sourceDir) =>
        ReplaceKitScriptsFrom(
            sourceDir,
            kitName: "Nvidia",
            requiredFiles: ["Nvidia-Optimizer.ps1", "Exo-Nvidia-Run.ps1", "Exo-Nvidia-Detect.ps1"],
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

    private static string? ReadVersionFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return File.ReadAllText(path).Trim();
        }
        catch
        {
            return null;
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
