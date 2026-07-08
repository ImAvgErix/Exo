using OptiHub.Helpers;
using OptiHub.Models;

namespace OptiHub.Services;

/// <summary>
/// Ensures bundled scripts are available under LocalAppData for updates and runs.
/// </summary>
public sealed class ScriptBundleService
{
    private readonly SettingsService _settings;

    public ScriptBundleService(SettingsService settings)
    {
        _settings = settings;
    }

    public string GetDiscordRoot()
    {
        var custom = _settings.Current.CustomScriptsPath;
        if (!string.IsNullOrWhiteSpace(custom) && Directory.Exists(custom))
            return custom;

        var working = Path.Combine(PathHelper.WorkingScriptsDir, "Discord");
        EnsureDiscordScriptsSynced(working);
        return working;
    }

    public string DiscordOptimizerScript =>
        Path.Combine(GetDiscordRoot(), "OptiHub-Discord-Run.ps1");

    public string DiscordDetectScript =>
        Path.Combine(GetDiscordRoot(), "OptiHub-Discord-Detect.ps1");

    public string DiscordRepairScript =>
        Path.Combine(GetDiscordRoot(), "OptiHub-Discord-Repair.ps1");

    public string GetBundledVersion()
    {
        var versionFile = Path.Combine(PathHelper.DiscordScriptsDir, "VERSION");
        if (File.Exists(versionFile))
            return File.ReadAllText(versionFile).Trim();
        return _settings.Current.DiscordKitVersion;
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

        // First run or incomplete working copy: full copy
        var marker = Path.Combine(working, "Disc-Optimizer.ps1");
        var hubRun = Path.Combine(working, "OptiHub-Discord-Run.ps1");
        if (!File.Exists(marker) || !File.Exists(hubRun))
        {
            CopyDirectory(bundled, working);
            return;
        }

        // Always refresh OptiHub wrappers from bundle (they may improve with app updates)
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
    }

    public void ReplaceDiscordScriptsFrom(string sourceDir)
    {
        var working = Path.Combine(PathHelper.WorkingScriptsDir, "Discord");
        if (Directory.Exists(working))
        {
            // Preserve OptiHub wrappers if source is pure DiscOpti
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

            // If source already has wrappers, keep them; else restore previous/bundle
            foreach (var (name, content) in wrappers)
            {
                var dst = Path.Combine(working, name);
                if (!File.Exists(dst))
                    File.WriteAllText(dst, content);
            }

            // Prefer bundled wrappers if still missing
            EnsureDiscordScriptsSynced(working);
        }
        else
        {
            Directory.CreateDirectory(working);
            CopyDirectory(sourceDir, working);
            EnsureDiscordScriptsSynced(working);
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
