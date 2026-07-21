using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Exo.Helpers;
using Exo.Models;

namespace Exo.Services;

public sealed class GitHubUpdateService
{
    private static readonly HttpClient Http = CreateClient();

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
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Exo", productVersion));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public async Task<AppUpdateResult> CheckAppUpdateAsync(
        IProgress<string>? status = null,
        IProgress<AppUpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        var local = typeof(GitHubUpdateService).Assembly.GetName().Version;
        var localText = local is null ? "0.0.0" : $"{local.Major}.{local.Minor}.{local.Build}";
        Report(status, progress, "Checking GitHub releases...", percent: -1);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/ImAvgErix/Exo/releases/latest");
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
            var body = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null;
            var summary = BuildReleaseTldr(body, remote);
            if (!TryParseReleaseVersion(remote, out _))
            {
                return new AppUpdateResult
                {
                    LocalVersion = localText,
                    RemoteVersion = string.IsNullOrWhiteSpace(remote) ? "?" : remote,
                    Message = "The latest GitHub release has invalid version metadata.",
                    ReleaseSummary = summary
                };
            }
            // Releases ship Exo.exe as a self-extracting installer. A legacy ZIP
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

                    if (string.Equals(name, "Exo.exe", StringComparison.OrdinalIgnoreCase))
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

            if (VersionsEqualOrLocalNewer(localText, remote))
            {
                return new AppUpdateResult
                {
                    AlreadyLatest = true,
                    LocalVersion = localText,
                    RemoteVersion = remote,
                    Message = $"Exo is up to date (v{localText}).",
                    ReleaseSummary = summary
                };
            }

            var headline = downloadUrl is null
                ? $"Exo v{remote} exists, but its Exo.exe release asset is missing. Nothing will be downloaded."
                : sha256 is null
                    ? $"Exo v{remote} is available, but GitHub did not publish its SHA-256 digest. Install is blocked."
                    : $"Exo v{remote} is available (you have v{localText}).";
            // Include TLDR in Message for WebView settings line + logs.
            var messageWithTldr = string.IsNullOrWhiteSpace(summary)
                ? headline
                : $"{headline}\n\nWhat's new:\n{summary}";

            return new AppUpdateResult
            {
                UpdateAvailable = downloadUrl is not null,
                LocalVersion = localText,
                RemoteVersion = remote,
                DownloadUrl = downloadUrl,
                DownloadSize = downloadSize,
                Sha256 = sha256,
                Message = messageWithTldr,
                ReleaseSummary = summary
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
        IProgress<AppUpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        var check = await CheckAppUpdateAsync(status, progress, ct).ConfigureAwait(false);
        return await InstallAppUpdateAsync(check, status, progress, ct).ConfigureAwait(false);
    }

    public async Task<AppUpdateResult> InstallAppUpdateAsync(
        AppUpdateResult check,
        IProgress<string>? status = null,
        IProgress<AppUpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (!check.UpdateAvailable)
            return check;
        var url = check.DownloadUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps ||
            !IsAllowedUpdateHost(downloadUri.Host))
        {
            return BlockedUpdate(check, "Update URL was not an allowlisted HTTPS GitHub asset. Nothing was downloaded.");
        }
        if (string.IsNullOrWhiteSpace(check.Sha256))
            return BlockedUpdate(check, "GitHub did not publish a SHA-256 digest for this update. Install is blocked.");

        // Download Exo.exe (SFX installer) and run it. In-place replace cannot
        // overwrite a locked running Exo.exe, so the installer stage-swaps under
        // %LocalAppData%\Exo\app after we exit.
        Report(status, progress, $"Downloading Exo v{check.RemoteVersion}...", percent: 0);

        try
        {
            var work = Path.Combine(PathHelper.AppDataDir, "updates");
            Directory.CreateDirectory(work);

            // Single fixed path - never pile up Exo-update-1.2.x.exe that can be
            // re-run later and downgrade a good install.
            foreach (var old in Directory.GetFiles(work, "Exo*.exe"))
            {
                try { File.Delete(old); } catch { /* locked */ }
            }

            var setupPath = Path.Combine(work, "Exo-Setup.exe");

            using (var response = await Http.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, ct)
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
                        // Download is 0–85% of the install bar; verify/apply fill the rest.
                        var pct = (int)Math.Min(85, (written * 85) / total);
                        if (pct != lastReport && (pct - lastReport >= 1 || pct >= 85))
                        {
                            lastReport = pct;
                            Report(status, progress,
                                $"Downloading v{check.RemoteVersion}…",
                                percent: pct);
                        }
                    }
                    else if (written > 0)
                    {
                        // Unknown size — pulse progress without MB spam.
                        var soft = (int)Math.Min(70, 10 + (written / (512 * 1024)));
                        if (soft != lastReport)
                        {
                            lastReport = soft;
                            Report(status, progress,
                                $"Downloading v{check.RemoteVersion}…",
                                percent: soft);
                        }
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

            if (!string.IsNullOrWhiteSpace(check.Sha256))
            {
                Report(status, progress, "Verifying integrity (SHA-256)…", percent: 88);
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

            Report(status, progress, $"Applying v{check.RemoteVersion}…", percent: 95);
            // Quiet in-app update: SFX waits for THIS process to exit (/waitpid) so it can
            // replace %LocalAppData%\Exo\app without "file in use" failures, then relaunches.
            var selfPid = Environment.ProcessId;
            Environment.SetEnvironmentVariable("EXO_SILENT_INSTALL", "1");
            Environment.SetEnvironmentVariable("EXO_UPDATE_WAIT_PID", selfPid.ToString());
            var args = $"/quiet /waitpid:{selfPid}";
            Process? started = null;
            try
            {
                started = Process.Start(new ProcessStartInfo
                {
                    FileName = setupPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = work,
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
                        Arguments = args,
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
                    Message = "Could not start the updater. Download Exo.exe from GitHub Releases and run it."
                };
            }

            // Brief settle so the child is fully running before we exit.
            try { await Task.Delay(350, ct).ConfigureAwait(false); } catch { /* ignore */ }

            Report(status, progress, $"Restarting into v{check.RemoteVersion}…", percent: 100);
            return new AppUpdateResult
            {
                UpdateAvailable = true,
                LocalVersion = check.LocalVersion,
                RemoteVersion = check.RemoteVersion,
                DownloadUrl = url,
                DownloadSize = check.DownloadSize,
                Sha256 = check.Sha256,
                ShouldExit = true,
                Message = $"Applying v{check.RemoteVersion}… Exo will close and reopen."
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

    /// <summary>
    /// GitHub release asset URLs (github.com/…/releases/download) and CDN hosts
    /// used after redirect (objects.githubusercontent.com, release-assets…).
    /// </summary>
    private static bool IsAllowedUpdateHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void Report(
        IProgress<string>? status,
        IProgress<AppUpdateProgress>? progress,
        string message,
        double percent)
    {
        status?.Report(message);
        progress?.Report(new AppUpdateProgress { Status = message, Percent = percent });
    }

    private static AppUpdateResult BlockedUpdate(AppUpdateResult check, string message) => new()
    {
        UpdateAvailable = check.UpdateAvailable,
        LocalVersion = check.LocalVersion,
        RemoteVersion = check.RemoteVersion,
        DownloadUrl = check.DownloadUrl,
        DownloadSize = check.DownloadSize,
        Sha256 = check.Sha256,
        Message = message,
        ReleaseSummary = check.ReleaseSummary
    };

    /// <summary>
    /// Turn a GitHub release body into a short plain-language list for the update popup.
    /// Prefers bullet lines; strips markdown; caps at 4 items.
    /// </summary>
    public static string BuildReleaseTldr(string? body, string remoteVersion)
    {
        if (string.IsNullOrWhiteSpace(body))
            return $"Bug fixes and improvements in v{remoteVersion}.";

        var bullets = new List<string>();
        foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            // Skip big headings / install noise
            if (line.StartsWith('#')) continue;
            if (line.StartsWith("### Install", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("|")) continue; // tables

            string? item = null;
            if (line.StartsWith("- ", StringComparison.Ordinal) ||
                line.StartsWith("* ", StringComparison.Ordinal))
                item = line[2..].Trim();
            else if (line.Length > 2 && char.IsDigit(line[0]) && line[1] is '.' or ')')
                item = line[2..].Trim();

            if (string.IsNullOrWhiteSpace(item)) continue;
            item = SimplifyReleaseBullet(item);
            if (item.Length < 4) continue;
            if (bullets.Any(b => string.Equals(b, item, StringComparison.OrdinalIgnoreCase)))
                continue;
            bullets.Add(item);
            if (bullets.Count >= 4) break;
        }

        if (bullets.Count == 0)
        {
            // Fallback: first non-empty prose line
            foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
            {
                var line = SimplifyReleaseBullet(raw.Trim());
                if (line.Length < 12 || line.StartsWith('#')) continue;
                return line.Length > 160 ? line[..157] + "…" : line;
            }
            return $"Bug fixes and improvements in v{remoteVersion}.";
        }

        return string.Join("\n", bullets.Select(b => "• " + b));
    }

    private static string SimplifyReleaseBullet(string item)
    {
        // Strip markdown emphasis / code / links
        item = System.Text.RegularExpressions.Regex.Replace(item, @"\*\*(.+?)\*\*", "$1");
        item = System.Text.RegularExpressions.Regex.Replace(item, @"`([^`]+)`", "$1");
        item = System.Text.RegularExpressions.Regex.Replace(item, @"\[([^\]]+)\]\([^)]+\)", "$1");
        item = item.Replace("**", "").Trim();
        // Soften a few dense tech phrases for the popup
        item = item
            .Replace("applyReport", "apply report", StringComparison.OrdinalIgnoreCase)
            .Replace("StartupApproved", "Windows Startup apps", StringComparison.OrdinalIgnoreCase)
            .Replace("StartupMode", "Steam startup setting", StringComparison.OrdinalIgnoreCase);
        if (item.Length > 120)
            item = item[..117].TrimEnd() + "…";
        return item;
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
