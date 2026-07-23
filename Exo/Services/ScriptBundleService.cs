using System.Security.Cryptography;
using Exo.Helpers;

namespace Exo.Services;

/// <summary>
/// Materializes the optimizer kits bundled with this app under LocalAppData.
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
    /// If this Exo build differs from the stamp left by the previous run/install,
    /// wipe and reinstall Discord/Steam/NVIDIA kits from the app bundle so the UI
    /// and scripts always match. Called lazily when an optimizer is first opened.
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
            try
            {
                EnsureAppKitStampCore();
                var working = Path.Combine(PathHelper.WorkingScriptsDir, "Discord");
                EnsureDiscordScriptsSynced(working);
                if (Directory.Exists(working) && File.Exists(Path.Combine(working, "Exo-Discord-Detect.ps1")))
                    return working;
            }
            catch { }

            return PathHelper.DiscordScriptsDir;
        }
    }

    public string GetSteamRoot()
    {
        lock (_syncLock)
        {
            try
            {
                EnsureAppKitStampCore();
                var working = Path.Combine(PathHelper.WorkingScriptsDir, "Steam");
                EnsureSteamScriptsSynced(working);
                if (Directory.Exists(working) && File.Exists(Path.Combine(working, "Exo-Steam-Detect.ps1")))
                    return working;
            }
            catch
            {
                // File locks during parallel kit materialize — use bundled kit.
            }

            return PathHelper.SteamScriptsDir;
        }
    }

    public string GetNvidiaRoot()
    {
        lock (_syncLock)
        {
            try
            {
                EnsureAppKitStampCore();
                var working = Path.Combine(PathHelper.WorkingScriptsDir, "Nvidia");
                EnsureNvidiaScriptsSynced(working);
                if (Directory.Exists(working) && File.Exists(Path.Combine(working, "Exo-Nvidia-Detect.ps1")))
                    return working;
            }
            catch { }

            return PathHelper.NvidiaScriptsDir;
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
        // Sequential kit materialize — parallel Directory.Move races on Windows and
        // threw "file is being used by another process" (blanked every module page).
        foreach (var job in new (string Name, string Bundled, string[] Required)[]
                 {
                     ("Discord", PathHelper.DiscordScriptsDir,
                         ["Disc-Optimizer.ps1", "Exo-Discord-Run.ps1"]),
                     ("Steam", PathHelper.SteamScriptsDir,
                         ["Steam-Optimizer.ps1", "Exo-Steam-Run.ps1", "Exo-Steam-Detect.ps1"]),
                     ("Nvidia", PathHelper.NvidiaScriptsDir,
                         ["Nvidia-Optimizer.ps1", "Exo-Nvidia-Run.ps1", "Exo-Nvidia-Detect.ps1"])
                 })
        {
            try { ReplaceWorkingKitFromBundled(job.Name, job.Bundled, job.Required); }
            catch { /* best-effort; Get*Root falls back to bundled Scripts */ }
        }

        // Shared libs (Game Bar / Common) live under Scripts/lib.
        // Working kits resolve them as ..\lib relative to Steam etc.
        try { SyncSharedLibFromBundled(); }
        catch { /* best-effort */ }

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

        Exception? last = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
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
                TryDeleteDirectory(backup);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                if (moved && !Directory.Exists(working) && Directory.Exists(backup))
                {
                    try { Directory.Move(backup, working); } catch { /* preserve original */ }
                }
                TryDeleteDirectory(staging);
                Thread.Sleep(80 * (attempt + 1));
            }
            catch
            {
                if (moved && !Directory.Exists(working) && Directory.Exists(backup))
                {
                    try { Directory.Move(backup, working); } catch { /* preserve original */ }
                }
                TryDeleteDirectory(staging);
                throw;
            }
            finally
            {
                TryDeleteDirectory(staging);
                if (Directory.Exists(working) && Directory.Exists(backup))
                    TryDeleteDirectory(backup);
            }
        }

        if (last is not null)
            throw last;
    }

    /// <summary>
    /// Mirror bundled Scripts/lib into %LocalAppData%\Exo\scripts\lib so working
    /// kits can load Game Bar / GamingStack via ..\lib (same layout as the app bundle).
    /// </summary>
    private static void SyncSharedLibFromBundled()
    {
        var bundled = Path.Combine(PathHelper.ScriptsRoot, "lib");
        if (!Directory.Exists(bundled))
            return;
        if (!File.Exists(Path.Combine(bundled, "Exo.GameBar.ps1")) &&
            !File.Exists(Path.Combine(bundled, "Exo.GamingStack.ps1")))
            return;

        var dest = Path.Combine(PathHelper.WorkingScriptsDir, "lib");
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(bundled, "*.ps1", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            var target = Path.Combine(dest, name);
            try
            {
                File.Copy(file, target, overwrite: true);
            }
            catch (IOException)
            {
                // Locked by a concurrent elevated run — leave existing copy.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort; Import-ExoSharedLibs falls back to app\Scripts\lib.
            }
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

        try { SyncSharedLibFromBundled(); } catch { /* best-effort */ }
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

        // Preserve a newer legacy/custom working kit until an app upgrade resets it.
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
