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
            string? zipUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.Equals(name, "optihub-build.zip", StringComparison.OrdinalIgnoreCase) ||
                        (name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true))
                    {
                        zipUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        if (string.Equals(name, "optihub-build.zip", StringComparison.OrdinalIgnoreCase))
                            break;
                    }
                }
            }

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
                DownloadUrl = zipUrl,
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
        if (string.IsNullOrWhiteSpace(check.DownloadUrl))
        {
            return new AppUpdateResult
            {
                UpdateAvailable = true,
                LocalVersion = check.LocalVersion,
                RemoteVersion = check.RemoteVersion,
                Message = "Update found but no download asset was available."
            };
        }

        status?.Report($"Downloading OptiHub v{check.RemoteVersion}...");
        var work = Path.Combine(PathHelper.AppDataDir, "app-update", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var zipPath = Path.Combine(work, "optihub-build.zip");
        var extract = Path.Combine(work, "extract");

        try
        {
            await using (var fs = File.Create(zipPath))
            await using (var stream = await Http.GetStreamAsync(check.DownloadUrl, ct))
                await stream.CopyToAsync(fs, ct);

            status?.Report("Extracting update...");
            ZipFile.ExtractToDirectory(zipPath, extract, overwriteFiles: true);

            var newExe = Directory.GetFiles(extract, "OptiHub.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (newExe is null)
            {
                return new AppUpdateResult
                {
                    UpdateAvailable = true,
                    LocalVersion = check.LocalVersion,
                    RemoteVersion = check.RemoteVersion,
                    Message = "Downloaded update did not contain OptiHub.exe."
                };
            }

            var sourceDir = Path.GetDirectoryName(newExe)!;
            var targetDir = PathHelper.AppDirectory;
            var bat = Path.Combine(work, "apply-update.bat");
            var batBody =
                "@echo off" + Environment.NewLine +
                "timeout /t 2 /nobreak >nul" + Environment.NewLine +
                "xcopy /E /Y /I /Q \"" + sourceDir + "\\*\" \"" + targetDir + "\\\" >nul" + Environment.NewLine +
                "start \"\" \"" + Path.Combine(targetDir, "OptiHub.exe") + "\"" + Environment.NewLine +
                "rmdir /S /Q \"" + work + "\"" + Environment.NewLine;
            await File.WriteAllTextAsync(bat, batBody, ct);

            status?.Report("Restarting into the new version...");
            Process.Start(new ProcessStartInfo
            {
                FileName = bat,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            });

            return new AppUpdateResult
            {
                UpdateAvailable = true,
                LocalVersion = check.LocalVersion,
                RemoteVersion = check.RemoteVersion,
                Message = $"Installing OptiHub v{check.RemoteVersion}. The app will restart."
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