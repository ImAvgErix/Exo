using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
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

        status?.Report("Checking remote Discord kit VERSION...");

        string remoteVersion;
        try
        {
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

        status?.Report($"Downloading Discord kit {remoteVersion}...");

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

            status?.Report("Extracting...");
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

            status?.Report("Installing updated scripts...");
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
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    public async Task<AppUpdateResult> CheckAppUpdateAsync(
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var local = typeof(GitHubUpdateService).Assembly.GetName().Version;
        var localText = local is null ? "0.0.0" : $"{local.Major}.{local.Minor}.{local.Build}";
        status?.Report("Checking GitHub releases...");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/BarcusEric/OptiHub/releases/latest");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return new AppUpdateResult
                {
                    LocalVersion = localText,
                    RemoteVersion = "?",
                    Message = $"Could not check releases ({(int)resp.StatusCode})."
                };
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            var remote = tag.Trim().TrimStart('v', 'V');
            // Releases ship OptiHub.exe only (self-extracting installer). Prefer that;
            // fall back to legacy zip names if an old release is still Latest.
            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                string? exeUrl = null;
                string? zipUrl = null;
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (string.IsNullOrWhiteSpace(url)) continue;

                    if (string.Equals(name, "OptiHub.exe", StringComparison.OrdinalIgnoreCase))
                        exeUrl = url;
                    else if (string.Equals(name, "optihub-build.zip", StringComparison.OrdinalIgnoreCase) ||
                             name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        zipUrl ??= url;
                }
                downloadUrl = exeUrl ?? zipUrl;
            }

            // Always fall back to the canonical latest exe URL when assets list is empty/misnamed.
            downloadUrl ??= "https://github.com/BarcusEric/OptiHub/releases/latest/download/OptiHub.exe";

            if (VersionsEqualOrLocalNewer(localText, remote))
            {
                return new AppUpdateResult
                {
                    AlreadyLatest = true,
                    LocalVersion = localText,
                    RemoteVersion = remote,
                    Message = $"OptiHub is up to date (v{localText})."
                };
            }

            return new AppUpdateResult
            {
                UpdateAvailable = true,
                LocalVersion = localText,
                RemoteVersion = remote,
                DownloadUrl = downloadUrl,
                Message = $"OptiHub v{remote} is available (you have v{localText})."
            };
        }
        catch (Exception ex)
        {
            return new AppUpdateResult
            {
                LocalVersion = localText,
                RemoteVersion = "?",
                Message = $"Could not check for app updates: {ex.Message}"
            };
        }
    }

    public async Task<AppUpdateResult> InstallLatestAppAsync(
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var check = await CheckAppUpdateAsync(status, ct);
        if (!check.UpdateAvailable)
            return check;
        var url = check.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
            url = "https://github.com/BarcusEric/OptiHub/releases/latest/download/OptiHub.exe";

        // Download OptiHub.exe (SFX installer) and run it. In-place replace cannot
        // overwrite a locked running OptiHub.exe, so the installer stage-swaps under
        // %LocalAppData%\OptiHub\app after we exit.
        status?.Report($"Downloading OptiHub v{check.RemoteVersion}...");

        try
        {
            var work = Path.Combine(PathHelper.AppDataDir, "updates");
            Directory.CreateDirectory(work);
            var setupPath = Path.Combine(work, $"OptiHub-update-{check.RemoteVersion}.exe");

            await using (var fs = File.Create(setupPath))
            {
                await using var stream = await Http.GetStreamAsync(url, ct);
                await stream.CopyToAsync(fs, ct);
            }

            if (!File.Exists(setupPath) || new FileInfo(setupPath).Length < 1_000_000)
            {
                return new AppUpdateResult
                {
                    UpdateAvailable = true,
                    LocalVersion = check.LocalVersion,
                    RemoteVersion = check.RemoteVersion,
                    Message = "Downloaded update looks invalid. Try again, or reinstall from GitHub Releases."
                };
            }

            status?.Report($"Starting installer for OptiHub v{check.RemoteVersion}...");
            // SFX installs to %LocalAppData%\OptiHub\app and relaunches. We must exit
            // so files unlock; installer kills any leftover OptiHub.exe by name.
            var started = Process.Start(new ProcessStartInfo
            {
                FileName = setupPath,
                UseShellExecute = true,
                WorkingDirectory = work
            });
            if (started is null)
            {
                return new AppUpdateResult
                {
                    UpdateAvailable = true,
                    LocalVersion = check.LocalVersion,
                    RemoteVersion = check.RemoteVersion,
                    Message = "Could not start the installer. Run the file manually from %LocalAppData%\\OptiHub\\updates, or reinstall from GitHub Releases."
                };
            }

            return new AppUpdateResult
            {
                UpdateAvailable = true,
                LocalVersion = check.LocalVersion,
                RemoteVersion = check.RemoteVersion,
                DownloadUrl = url,
                ShouldExit = true,
                Message = $"Installing OptiHub v{check.RemoteVersion}. Closing so the installer can replace files..."
            };
        }
        catch (Exception ex)
        {
            return new AppUpdateResult
            {
                UpdateAvailable = true,
                LocalVersion = check.LocalVersion,
                RemoteVersion = check.RemoteVersion,
                Message = $"App update failed: {ex.Message}"
            };
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
