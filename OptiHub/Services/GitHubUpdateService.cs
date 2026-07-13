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
        // App installer is ~100-150 MB; a 3-minute ceiling made Check/Install look broken on average links.
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        var version = typeof(GitHubUpdateService).Assembly.GetName().Version;
        var productVersion = version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OptiHub", productVersion));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>
    /// Back-compat entry point — updates Discord, Steam, and NVIDIA kits from GitHub.
    /// </summary>
    public Task<ScriptUpdateResult> CheckAndUpdateDiscordScriptsAsync(
        bool force = false,
        IProgress<string>? status = null,
        CancellationToken ct = default) =>
        CheckAndUpdateAllScriptsAsync(force, status, ct);

    public async Task<ScriptUpdateResult> CheckAndUpdateAllScriptsAsync(
        bool force = false,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        await _scriptUpdateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CheckAndUpdateAllScriptsCoreAsync(force, status, ct).ConfigureAwait(false);
        }
        finally
        {
            _scriptUpdateGate.Release();
        }
    }

    private async Task<ScriptUpdateResult> CheckAndUpdateAllScriptsCoreAsync(
        bool force,
        IProgress<string>? status,
        CancellationToken ct)
    {
        var settings = _settings.Current;
        var repo = settings.DiscordScriptsRepo;
        var branch = settings.DiscordScriptsBranch;
        var localDiscord = _scripts.GetWorkingKitVersion("Discord");
        var localSteam = _scripts.GetWorkingKitVersion("Steam");
        var localNvidia = _scripts.GetWorkingKitVersion("Nvidia");
        var localSummary = $"D{localDiscord}/S{localSteam}/N{localNvidia}";

        status?.Report("Checking remote script kit versions...");

        string remoteDiscord;
        string remoteSteam;
        string remoteNvidia;
        try
        {
            remoteDiscord = (await TryGetRemoteTextAsync(
                $"https://raw.githubusercontent.com/{repo}/{branch}/OptiHub/Scripts/Discord/VERSION", ct)
                ?? await TryGetRemoteTextAsync(
                    $"https://raw.githubusercontent.com/{repo}/{branch}/VERSION", ct)
                ?? throw new InvalidOperationException("Remote Discord VERSION not found.")).Trim();
            remoteSteam = (await TryGetRemoteTextAsync(
                $"https://raw.githubusercontent.com/{repo}/{branch}/OptiHub/Scripts/Steam/VERSION", ct)
                ?? remoteDiscord).Trim();
            remoteNvidia = (await TryGetRemoteTextAsync(
                $"https://raw.githubusercontent.com/{repo}/{branch}/OptiHub/Scripts/Nvidia/VERSION", ct)
                ?? remoteDiscord).Trim();
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
                LocalVersion = localSummary,
                RemoteVersion = "?",
                Message = $"Could not reach GitHub: {ex.Message}"
            };
        }

        var remoteSummary = $"D{remoteDiscord}/S{remoteSteam}/N{remoteNvidia}";
        var needDiscord = force || !VersionsEqualOrLocalNewer(localDiscord, remoteDiscord);
        var needSteam = force || !VersionsEqualOrLocalNewer(localSteam, remoteSteam);
        var needNvidia = force || !VersionsEqualOrLocalNewer(localNvidia, remoteNvidia);

        if (!needDiscord && !needSteam && !needNvidia)
        {
            return new ScriptUpdateResult
            {
                Updated = false,
                AlreadyLatest = true,
                LocalVersion = localSummary,
                RemoteVersion = remoteSummary,
                Message = $"Scripts are up to date (Discord {localDiscord}, Steam {localSteam}, NVIDIA {localNvidia})."
            };
        }

        status?.Report("Downloading latest optimizer scripts from GitHub...");

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

            var updated = new List<string>();
            var skipped = new List<string>();

            if (needDiscord)
            {
                var discordRoot = FindScriptsKitRoot(extract, "Discord", "Disc-Optimizer.ps1", "OptiHub-Discord-Run.ps1");
                if (discordRoot is null)
                    throw new InvalidOperationException("Downloaded archive did not contain Discord optimizer scripts.");
                EnsureVersionFile(discordRoot, remoteDiscord);
                status?.Report($"Installing Discord scripts v{remoteDiscord}...");
                _scripts.ReplaceDiscordScriptsFrom(discordRoot);
                _settings.Update(s => s.DiscordKitVersion = remoteDiscord);
                updated.Add($"Discord {remoteDiscord}");
            }
            else
            {
                skipped.Add($"Discord {localDiscord}");
            }

            if (needSteam)
            {
                var steamRoot = FindScriptsKitRoot(extract, "Steam", "Steam-Optimizer.ps1", "OptiHub-Steam-Run.ps1");
                if (steamRoot is null)
                    throw new InvalidOperationException("Downloaded archive did not contain Steam optimizer scripts.");
                EnsureVersionFile(steamRoot, remoteSteam);
                status?.Report($"Installing Steam scripts v{remoteSteam}...");
                _scripts.ReplaceSteamScriptsFrom(steamRoot);
                updated.Add($"Steam {remoteSteam}");
            }
            else
            {
                skipped.Add($"Steam {localSteam}");
            }

            if (needNvidia)
            {
                var nvidiaRoot = FindScriptsKitRoot(extract, "Nvidia", "Nvidia-Optimizer.ps1", "OptiHub-Nvidia-Run.ps1");
                if (nvidiaRoot is null)
                    throw new InvalidOperationException("Downloaded archive did not contain NVIDIA optimizer scripts.");
                EnsureVersionFile(nvidiaRoot, remoteNvidia);
                status?.Report($"Installing NVIDIA scripts v{remoteNvidia}...");
                _scripts.ReplaceNvidiaScriptsFrom(nvidiaRoot);
                updated.Add($"NVIDIA {remoteNvidia}");
            }
            else
            {
                skipped.Add($"NVIDIA {localNvidia}");
            }

            var newLocal = $"D{_scripts.GetWorkingKitVersion("Discord")}/S{_scripts.GetWorkingKitVersion("Steam")}/N{_scripts.GetWorkingKitVersion("Nvidia")}";
            var msg = updated.Count > 0
                ? $"Updated: {string.Join(", ", updated)}."
                : "No script kits needed an update.";
            if (skipped.Count > 0)
                msg += $" Already current: {string.Join(", ", skipped)}.";

            return new ScriptUpdateResult
            {
                Updated = updated.Count > 0,
                AlreadyLatest = updated.Count == 0,
                LocalVersion = newLocal,
                RemoteVersion = remoteSummary,
                Message = msg
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
                LocalVersion = localSummary,
                RemoteVersion = remoteSummary,
                Message = $"Update failed: {ex.Message}"
            };
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    private static void EnsureVersionFile(string kitRoot, string version)
    {
        var verFile = Path.Combine(kitRoot, "VERSION");
        if (!File.Exists(verFile))
            File.WriteAllText(verFile, version);
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
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/UhhErix/OptiHub/releases/latest");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var code = (int)resp.StatusCode;
                var hint = code is 403 or 429
                    ? " GitHub rate limit — wait a few minutes and try again."
                    : code == 404
                        ? " No public releases found for this repo."
                        : string.Empty;
                return new AppUpdateResult
                {
                    LocalVersion = localText,
                    RemoteVersion = "?",
                    Message = $"Could not check releases (HTTP {code}).{hint}"
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
            downloadUrl ??= "https://github.com/UhhErix/OptiHub/releases/latest/download/OptiHub.exe";

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
            url = "https://github.com/UhhErix/OptiHub/releases/latest/download/OptiHub.exe";

        // Prefer GitHub asset digest when present; size + PE version still gate install.
        var requireSha = !string.IsNullOrWhiteSpace(check.Sha256);

        // Download OptiHub.exe (SFX installer) and run it. In-place replace cannot
        // overwrite a locked running OptiHub.exe, so the installer stage-swaps under
        // %LocalAppData%\OptiHub\app after we exit.
        status?.Report($"Downloading OptiHub v{check.RemoteVersion}...");

        try
        {
            var work = Path.Combine(PathHelper.AppDataDir, "updates");
            Directory.CreateDirectory(work);

            // Single fixed path - never pile up OptiHub-update-1.2.x.exe that can be
            // re-run later and downgrade a good install.
            foreach (var old in Directory.GetFiles(work, "OptiHub*.exe"))
            {
                try { File.Delete(old); } catch { /* locked */ }
            }

            var setupPath = Path.Combine(work, "OptiHub-Setup.exe");

            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                       .ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return new AppUpdateResult
                    {
                        UpdateAvailable = true,
                        LocalVersion = check.LocalVersion,
                        RemoteVersion = check.RemoteVersion,
                        Message = $"Download failed (HTTP {(int)response.StatusCode}). Try again, or install from GitHub Releases."
                    };
                }

                var total = response.Content.Headers.ContentLength
                            ?? check.DownloadSize
                            ?? -1;
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fs = new FileStream(
                    setupPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    useAsync: true);

                var buffer = new byte[128 * 1024];
                long written = 0;
                var lastReport = -1;
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    written += read;
                    if (total > 0)
                    {
                        var pct = (int)Math.Min(99, (written * 100) / total);
                        if (pct != lastReport && (pct % 5 == 0 || pct >= 99))
                        {
                            lastReport = pct;
                            var mb = written / (1024.0 * 1024.0);
                            var totalMb = total / (1024.0 * 1024.0);
                            status?.Report($"Downloading OptiHub v{check.RemoteVersion}... {pct}% ({mb:0.0}/{totalMb:0.0} MB)");
                        }
                    }
                    else if (written > 0 && written / (5 * 1024 * 1024) != lastReport)
                    {
                        lastReport = (int)(written / (5 * 1024 * 1024));
                        status?.Report($"Downloading OptiHub v{check.RemoteVersion}... {written / (1024.0 * 1024.0):0.0} MB");
                    }
                }
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
                    Message = "Downloaded update looks invalid (too small). Try again, or reinstall from GitHub Releases."
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

            if (requireSha)
            {
                status?.Report("Verifying update integrity (SHA-256)...");
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
            else
            {
                status?.Report("Release has no digest; verifying installer version stamp...");
            }

            string? downloadedVersion = null;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(setupPath);
                // Prefer ProductVersion (3-part Informational), then FileVersion (often 4-part).
                downloadedVersion = FirstNonEmpty(info.ProductVersion, info.FileVersion);
            }
            catch
            {
                downloadedVersion = null;
            }

            if (!VersionsRepresentSameRelease(downloadedVersion, check.RemoteVersion))
            {
                // If SHA already matched, still refuse mismatched PE stamps (wrong/corrupt asset).
                TryDelete(setupPath);
                return new AppUpdateResult
                {
                    UpdateAvailable = true,
                    LocalVersion = check.LocalVersion,
                    RemoteVersion = check.RemoteVersion,
                    Message = $"Downloaded installer version ({downloadedVersion ?? "unknown"}) did not match release v{check.RemoteVersion}. Nothing was launched."
                };
            }

            status?.Report($"Applying OptiHub v{check.RemoteVersion} quietly...");
            // Quiet in-app update: winexe SFX + /quiet + env - no console, no MessageBox.
            // Installer stages under %LocalAppData%\OptiHub\app, refreshes Start Menu, relaunches.
            Environment.SetEnvironmentVariable("OPTIHUB_SILENT_INSTALL", "1");
            Process? started = null;
            try
            {
                started = Process.Start(new ProcessStartInfo
                {
                    FileName = setupPath,
                    Arguments = "/quiet",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = work,
                    // Child inherits OPTIHUB_SILENT_INSTALL from this process environment.
                    ErrorDialog = false
                });
            }
            catch
            {
                started = null;
            }
            if (started is null)
            {
                try
                {
                    started = Process.Start(new ProcessStartInfo
                    {
                        FileName = setupPath,
                        Arguments = "/quiet",
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = work,
                        ErrorDialog = false
                    });
                }
                catch
                {
                    started = null;
                }
            }
            if (started is null)
            {
                return new AppUpdateResult
                {
                    UpdateAvailable = true,
                    LocalVersion = check.LocalVersion,
                    RemoteVersion = check.RemoteVersion,
                    Message = "Could not start the updater. Download OptiHub.exe from GitHub Releases and run it."
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
                Message = $"Applying v{check.RemoteVersion}... OptiHub will restart."
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

    private static string? FindDiscordScriptsRoot(string extractRoot) =>
        FindScriptsKitRoot(extractRoot, "Discord", "Disc-Optimizer.ps1", "OptiHub-Discord-Run.ps1")
        ?? Directory.GetDirectories(extractRoot, "Disc Optimizer", SearchOption.AllDirectories)
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "Disc-Optimizer.ps1")));

    private static string? FindScriptsKitRoot(
        string extractRoot,
        string kitFolderName,
        string primaryMarker,
        string secondaryMarker)
    {
        var modern = Directory.GetDirectories(extractRoot, kitFolderName, SearchOption.AllDirectories)
            .FirstOrDefault(d =>
                File.Exists(Path.Combine(d, primaryMarker)) &&
                File.Exists(Path.Combine(d, secondaryMarker)) &&
                d.Contains($"{Path.DirectorySeparatorChar}Scripts{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        if (modern is not null) return modern;

        return Directory.GetFiles(extractRoot, primaryMarker, SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .FirstOrDefault(d => d is not null && File.Exists(Path.Combine(d!, secondaryMarker)));
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

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v;
        }
        return null;
    }

    private static string Normalize(string v)
    {
        var normalized = v.Trim().TrimStart('v', 'V');
        // ProductVersion may be "1.8.10+gitsha" or "1.8.10-preview".
        var suffix = normalized.IndexOfAny(['-', '+']);
        return suffix >= 0 ? normalized[..suffix] : normalized;
    }
}
