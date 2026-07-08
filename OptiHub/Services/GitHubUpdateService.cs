using System.IO.Compression;
using System.Net.Http.Headers;
using OptiHub.Helpers;
using OptiHub.Models;

namespace OptiHub.Services;

public sealed class GitHubUpdateService
{
    private readonly SettingsService _settings;
    private readonly ScriptBundleService _scripts;
    private static readonly HttpClient Http = CreateClient();

    public GitHubUpdateService(SettingsService settings, ScriptBundleService scripts)
    {
        _settings = settings;
        _scripts = scripts;
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OptiHub", "1.0"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return c;
    }

    public async Task<ScriptUpdateResult> CheckAndUpdateDiscordScriptsAsync(
        bool force = false,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var settings = _settings.Current;
        var repo = settings.DiscordScriptsRepo;
        var branch = settings.DiscordScriptsBranch;
        var localVersion = _scripts.GetWorkingVersion();

        status?.Report("Checking remote Discord kit VERSION…");

        string remoteVersion;
        try
        {
            // Prefer per-kit VERSION under OptiHub/Scripts/Discord; fall back to repo root VERSION.
            remoteVersion = await TryGetRemoteTextAsync(
                $"https://raw.githubusercontent.com/{repo}/{branch}/OptiHub/Scripts/Discord/VERSION", ct)
                ?? await TryGetRemoteTextAsync(
                    $"https://raw.githubusercontent.com/{repo}/{branch}/VERSION", ct)
                ?? throw new InvalidOperationException("Remote VERSION not found.");
            remoteVersion = remoteVersion.Trim();
        }
        catch (Exception ex)
        {
            return new ScriptUpdateResult
            {
                Updated = false,
                AlreadyLatest = false,
                LocalVersion = localVersion,
                RemoteVersion = "?",
                Message = $"Could not reach GitHub: {ex.Message}"
            };
        }

        if (!force && VersionsEqualOrLocalNewer(localVersion, remoteVersion))
        {
            return new ScriptUpdateResult
            {
                Updated = false,
                AlreadyLatest = true,
                LocalVersion = localVersion,
                RemoteVersion = remoteVersion,
                Message = $"Scripts are up to date (v{localVersion})."
            };
        }

        status?.Report($"Downloading Discord kit {remoteVersion}…");

        var work = Path.Combine(PathHelper.AppDataDir, "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var zipPath = Path.Combine(work, "source.zip");

        try
        {
            var zipUrl = $"https://codeload.github.com/{repo}/zip/refs/heads/{Uri.EscapeDataString(branch)}";
            await using (var fs = File.Create(zipPath))
            {
                await using var stream = await Http.GetStreamAsync(zipUrl, ct);
                await stream.CopyToAsync(fs, ct);
            }

            status?.Report("Extracting…");
            var extract = Path.Combine(work, "extract");
            ZipFile.ExtractToDirectory(zipPath, extract, overwriteFiles: true);

            var discordScriptsRoot = FindDiscordScriptsRoot(extract);
            if (discordScriptsRoot is null)
            {
                return new ScriptUpdateResult
                {
                    Updated = false,
                    LocalVersion = localVersion,
                    RemoteVersion = remoteVersion,
                    Message = "Downloaded archive did not contain Discord optimizer scripts."
                };
            }

            var verFile = Path.Combine(discordScriptsRoot, "VERSION");
            if (!File.Exists(verFile))
                await File.WriteAllTextAsync(verFile, remoteVersion, ct);

            status?.Report("Installing updated scripts…");
            _scripts.ReplaceDiscordScriptsFrom(discordScriptsRoot);
            _settings.Update(s => s.DiscordKitVersion = remoteVersion);

            return new ScriptUpdateResult
            {
                Updated = true,
                LocalVersion = remoteVersion,
                RemoteVersion = remoteVersion,
                Message = $"Updated Discord scripts to v{remoteVersion}."
            };
        }
        catch (Exception ex)
        {
            return new ScriptUpdateResult
            {
                Updated = false,
                LocalVersion = localVersion,
                RemoteVersion = remoteVersion,
                Message = $"Update failed: {ex.Message}"
            };
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* ignore */ }
        }
    }

    private static async Task<string?> TryGetRemoteTextAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Locates Discord scripts in OptiHub layout, then legacy Disc Optimizer folder.
    /// </summary>
    private static string? FindDiscordScriptsRoot(string extractRoot)
    {
        var modern = Directory.GetDirectories(extractRoot, "Discord", SearchOption.AllDirectories)
            .FirstOrDefault(d =>
                File.Exists(Path.Combine(d, "Disc-Optimizer.ps1")) &&
                d.Contains($"{Path.DirectorySeparatorChar}Scripts{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        if (modern is not null) return modern;

        var withWrappers = Directory.GetFiles(extractRoot, "Disc-Optimizer.ps1", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .FirstOrDefault(d => d is not null && File.Exists(Path.Combine(d, "OptiHub-Discord-Run.ps1")));
        if (withWrappers is not null) return withWrappers;

        return Directory.GetDirectories(extractRoot, "Disc Optimizer", SearchOption.AllDirectories)
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "Disc-Optimizer.ps1")));
    }

    private static bool VersionsEqualOrLocalNewer(string local, string remote)
    {
        if (string.Equals(local, remote, StringComparison.OrdinalIgnoreCase))
            return true;
        if (Version.TryParse(Normalize(local), out var l) &&
            Version.TryParse(Normalize(remote), out var r))
            return l >= r;
        return false;
    }

    private static string Normalize(string v) => v.Trim().TrimStart('v', 'V');
}
