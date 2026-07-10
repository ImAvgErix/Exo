using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using OptiHub.Helpers;
using OptiHub.Models;

namespace OptiHub.Services;

public sealed class GitHubUpdateService
{
    private readonly SettingsService _settings;
    private readonly ScriptBundleService _scripts;
    private readonly SemaphoreSlim _scriptUpdateGate = new(1, 1);
    private static readonly HttpClient Http = CreateClient();

    public GitHubUpdateService(SettingsService settings, ScriptBundleService scripts)
    {
        _settings = settings;
        _scripts = scripts;
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15)
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
        var version = typeof(GitHubUpdateService).Assembly.GetName().Version;
        var productVersion = version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OptiHub", productVersion));
        return c;
    }

    public async Task<ScriptUpdateResult> CheckAndUpdateDiscordScriptsAsync(
        bool force = false,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        await _scriptUpdateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CheckAndUpdateDiscordScriptsCoreAsync(force, status, ct).ConfigureAwait(false);
        }
        finally
        {
            _scriptUpdateGate.Release();
        }
    }

    private async Task<ScriptUpdateResult> CheckAndUpdateDiscordScriptsCoreAsync(
        bool force,
        IProgress<string>? status,
        CancellationToken ct)
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
            if (!TryParseReleaseVersion(remote, out _))
            {
                return new AppUpdateResult
                {
                    LocalVersion = localText,
                    RemoteVersion = string.IsNullOrWhiteSpace(remote) ? "?" : remote,
                    Message = "The latest GitHub release has invalid version metadata."
                };
            }
            // Releases ship OptiHub.exe as a self-extracting installer. A legacy ZIP
            // is not executable and must never be renamed and launched as one.
            string? downloadUrl = null;
            long? downloadSize = null;
            string? sha256 = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                string? exeUrl = null;
                long? exeSize = null;
                string? exeSha256 = null;
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (string.IsNullOrWhiteSpace(url)) continue;

                    var size = a.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize)
                        ? parsedSize
                        : (long?)null;
                    var digest = a.TryGetProperty("digest", out var digestElement)
                        ? NormalizeSha256(digestElement.GetString())
                        : null;

                    if (string.Equals(name, "OptiHub.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        exeUrl = url;
                        exeSize = size;
                        exeSha256 = digest;
                    }
                }

                if (exeUrl is not null)
                {
                    downloadUrl = exeUrl;
                    downloadSize = exeSize;
                    sha256 = exeSha256;
                }
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
                DownloadSize = downloadSize,
                Sha256 = sha256,
                Message = $"OptiHub v{remote} is available (you have v{localText})."
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
        var check = await CheckAppUpdateAsync(status, ct).ConfigureAwait(false);
        return await InstallAppUpdateAsync(check, status, ct).ConfigureAwait(false);
    }

    public async Task<AppUpdateResult> InstallAppUpdateAsync(
        AppUpdateResult check,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (!check.UpdateAvailable)
            return check;
        var url = check.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
            url = "https://github.com/BarcusEric/OptiHub/releases/latest/download/OptiHub.exe";
        if (string.IsNullOrWhiteSpace(check.Sha256))
        {
            return new AppUpdateResult
            {
                UpdateAvailable = true,
                LocalVersion = check.LocalVersion,
                RemoteVersion = check.RemoteVersion,
                Message = "The GitHub release did not provide SHA-256 integrity metadata. Nothing was downloaded."
            };
        }

        // Download OptiHub.exe (SFX installer) and run it. In-place replace cannot
        // overwrite a locked running OptiHub.exe, so the installer stage-swaps under
        // %LocalAppData%\OptiHub\app after we exit.
        status?.Report($"Downloading OptiHub v{check.RemoteVersion}...");

        try
        {
            var work = Path.Combine(PathHelper.AppDataDir, "updates");
            Directory.CreateDirectory(work);

            // Single fixed path — never pile up OptiHub-update-1.2.x.exe that can be
            // re-run later and downgrade a good install.
            foreach (var old in Directory.GetFiles(work, "OptiHub*.exe"))
            {
                try { File.Delete(old); } catch { /* locked */ }
            }

            var setupPath = Path.Combine(work, "OptiHub-Setup.exe");

            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fs = new FileStream(
                    setupPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    useAsync: true);
                await stream.CopyToAsync(fs, 128 * 1024, ct).ConfigureAwait(false);
            }

            var downloadedSize = File.Exists(setupPath) ? new FileInfo(setupPath).Length : 0;
            if (downloadedSize < 1_000_000)
            {
                TryDelete(setupPath);
                return new AppUpdateResult
                {
                    UpdateAvailable = true,
                    LocalVersion = check.LocalVersion,
                    RemoteVersion = check.RemoteVersion,
                    Message = "Downloaded update looks invalid. Try again, or reinstall from GitHub Releases."
                };
            }

            if (check.DownloadSize is > 0 && downloadedSize != check.DownloadSize.Value)
            {
                TryDelete(setupPath);
                return new AppUpdateResult
                {
                    UpdateAvailable = true,
                    LocalVersion = check.LocalVersion,
                    RemoteVersion = check.RemoteVersion,
                    Message = "Downloaded update size did not match the GitHub release metadata. Nothing was launched."
                };
            }

            if (!string.IsNullOrWhiteSpace(check.Sha256))
            {
                status?.Report("Verifying update integrity...");
                var actualSha256 = await ComputeFileSha256Async(setupPath, ct).ConfigureAwait(false);
                if (!string.Equals(actualSha256, check.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(setupPath);
                    return new AppUpdateResult
                    {
                        UpdateAvailable = true,
                        LocalVersion = check.LocalVersion,
                        RemoteVersion = check.RemoteVersion,
                        Message = "Downloaded update failed SHA-256 verification. Nothing was launched."
                    };
                }
            }

            string? downloadedVersion;
            try
            {
                downloadedVersion = FileVersionInfo.GetVersionInfo(setupPath).FileVersion;
            }
            catch
            {
                downloadedVersion = null;
            }
            if (!VersionsRepresentSameRelease(downloadedVersion, check.RemoteVersion))
            {
                TryDelete(setupPath);
                return new AppUpdateResult
                {
                    UpdateAvailable = true,
                    LocalVersion = check.LocalVersion,
                    RemoteVersion = check.RemoteVersion,
                    Message = "Downloaded installer version did not match the GitHub release. Nothing was launched."
                };
            }

            status?.Report($"Starting installer for OptiHub v{check.RemoteVersion}...");
            // SFX installs ONLY to %LocalAppData%\OptiHub\app, writes Start Menu + Desktop
            // shortcuts, verifies FileVersion, then relaunches. We exit so files unlock.
            using var started = Process.Start(new ProcessStartInfo
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
                    Message = "Could not start the installer. Run %LocalAppData%\\OptiHub\\updates\\OptiHub-Setup.exe, or reinstall from GitHub Releases."
                };
            }

            return new AppUpdateResult
            {
                UpdateAvailable = true,
                LocalVersion = check.LocalVersion,
                RemoteVersion = check.RemoteVersion,
                DownloadUrl = url,
                DownloadSize = check.DownloadSize,
                Sha256 = check.Sha256,
                ShouldExit = true,
                Message = $"Installing OptiHub v{check.RemoteVersion} to %LocalAppData%\\OptiHub\\app. Closing now..."
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? NormalizeSha256(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        var value = digest.Trim();
        const string prefix = "sha256:";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            value = value[prefix.Length..];

        return value.Length == 64 && value.All(Uri.IsHexDigit)
            ? value.ToLowerInvariant()
            : null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort cleanup */ }
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

    private static bool TryParseReleaseVersion(string value, out Version? version)
    {
        if (Version.TryParse(Normalize(value), out var parsed) && parsed.Build >= 0)
        {
            version = parsed;
            return true;
        }

        version = null;
        return false;
    }

    private static bool VersionsRepresentSameRelease(string? installed, string expected)
    {
        if (string.IsNullOrWhiteSpace(installed) ||
            !TryParseReleaseVersion(installed, out var left) ||
            !TryParseReleaseVersion(expected, out var right) ||
            left is null || right is null)
        {
            return false;
        }

        return left.Major == right.Major &&
               left.Minor == right.Minor &&
               left.Build == right.Build;
    }

    private static string Normalize(string v)
    {
        var normalized = v.Trim().TrimStart('v', 'V');
        var suffix = normalized.IndexOfAny(['-', '+']);
        return suffix >= 0 ? normalized[..suffix] : normalized;
    }
}
