using System.Text.Json;
using System.Text.Json.Serialization;
using Exo.Helpers;
using Exo.Models;
using Exo.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace Exo.Services;

/// <summary>
/// JSON-RPC bridge between the React UI (WebView2) and native optimizer services.
/// UI owns pixels; this host owns elevation, scripts, and live machine reads.
/// </summary>
public sealed class WebHostBridge
{
    private readonly AppServices _services;
    private readonly DispatcherQueue _queue;
    private CoreWebView2? _web;

    /// <summary>Internet ProbeAsync cache — full probe is multi-process + ping.</summary>
    private NetworkSnapshot? _internetProbeCache;
    private DateTimeOffset _internetProbeCacheUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan InternetProbeFreshness = TimeSpan.FromSeconds(90);

    /// <summary>Module detect JSON cache (web UI re-open without re-spawning pwsh).</summary>
    private readonly Dictionary<string, (DateTimeOffset At, object Payload)> _detectCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ModuleDetectFreshness = TimeSpan.FromSeconds(120);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public WebHostBridge(AppServices services, DispatcherQueue queue)
    {
        _services = services;
        _queue = queue;
    }

    public event EventHandler? SettingsRequested;

    public void Attach(CoreWebView2 web)
    {
        _web = web;
        web.WebMessageReceived += OnMessage;
    }

    public void Detach()
    {
        if (_web is null) return;
        try { _web.WebMessageReceived -= OnMessage; } catch { }
        _web = null;
    }

    private void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // postMessage(object) arrives as JSON; postMessage(string) as string.
        // TryGetWebMessageAsString throws when the payload is not a string — always
        // fall back to WebMessageAsJson.
        string? raw = null;
        try { raw = e.TryGetWebMessageAsString(); } catch { /* not a string */ }
        if (string.IsNullOrWhiteSpace(raw))
        {
            try { raw = e.WebMessageAsJson; } catch { return; }
        }
        if (string.IsNullOrWhiteSpace(raw)) return;
        _ = HandleAsync(raw);
    }

    private async Task HandleAsync(string raw)
    {
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var method = root.TryGetProperty("method", out var mEl) ? mEl.GetString() : null;
            var hasParams = root.TryGetProperty("params", out var paramsEl);

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(method))
                return;

            object? result = method switch
            {
                "dashboard.get" => BuildDashboard(),
                "dashboard.live" => BuildLive(),
                "module.detect" => await DetectModuleAsync(paramsEl, hasParams).ConfigureAwait(true),
                "module.apply" => await ApplyModuleAsync(paramsEl, hasParams).ConfigureAwait(true),
                "module.repair" => await RepairModuleAsync(paramsEl, hasParams).ConfigureAwait(true),
                "module.verifyAll" => await VerifyAllModulesAsync().ConfigureAwait(true),
                "games.list" => MapGamesHub(
                    _services.Games.ListGames(ReadString(paramsEl, hasParams, "gameId"))),
                "games.apply" => await ApplyGameHubAsync(paramsEl, hasParams).ConfigureAwait(true),
                "games.repair" => await RepairGameHubAsync(paramsEl, hasParams).ConfigureAwait(true),
                "games.openInstall" => OpenGameInstall(paramsEl, hasParams),
                "shell.navigate" => null,
                "shell.settings" => RequestSettings(),
                "shell.openLogs" => OpenLogsFolder(),
                "shell.openIssues" => OpenIssues(),
                "shell.openUrl" => OpenExternalUrl(paramsEl, hasParams),
                "shell.openNvidiaControlPanel" => OpenNvidiaControlPanel(),
                "shell.minimize" => MinimizeWindow(),
                "shell.close" => CloseWindow(),
                "settings.get" => BuildSettings(),
                "settings.set" => SetSettings(paramsEl, hasParams),
                "settings.getChangelog" => BuildChangelog(),
                "settings.checkUpdates" => await CheckUpdatesAsync().ConfigureAwait(true),
                _ => throw new InvalidOperationException($"Unknown method: {method}")
            };

            PostResponse(id!, ok: true, result: result);
        }
        catch (Exception ex)
        {
            if (id is not null)
                PostResponse(id, ok: false, error: ex.Message);
        }
    }

    private object? RequestSettings()
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
        return null;
    }

    private object MinimizeWindow()
    {
        void Go()
        {
            try
            {
                if (App.MainAppWindow?.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
                    p.Minimize();
            }
            catch { }
        }

        if (!_queue.HasThreadAccess)
            _queue.TryEnqueue(Go);
        else
            Go();
        return new { ok = true };
    }

    private object CloseWindow()
    {
        void Go()
        {
            try { App.MainAppWindow?.Close(); }
            catch { }
        }

        if (!_queue.HasThreadAccess)
            _queue.TryEnqueue(Go);
        else
            Go();
        return new { ok = true };
    }

    private void PostResponse(string id, bool ok, object? result = null, string? error = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["ok"] = ok
        };
        if (ok) payload["result"] = result;
        else payload["error"] = error ?? "error";
        PostJson(payload);
    }

    private void PostEvent(string name, object? data)
    {
        PostJson(new Dictionary<string, object?>
        {
            ["event"] = name,
            ["data"] = data
        });
    }

    private void PostJson(object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            void Send()
            {
                try { _web?.PostWebMessageAsJson(json); } catch { }
            }

            if (!_queue.HasThreadAccess)
            {
                _queue.TryEnqueue(Send);
                return;
            }
            Send();
        }
        catch { }
    }

    private object BuildDashboard()
    {
        var vm = new DashboardViewModel(_services);
        var modules = new[]
        {
            Row("discord", "Discord", vm.DiscordStatusTag),
            Row("steam", "Steam", vm.SteamStatusTag),
            Row("games", "Games", vm.GamesStatusTag),
            Row("windows", "Windows", vm.WindowsStatusTag),
            Row("internet", "Internet", vm.InternetStatusTag),
            Row("nvidia", "NVIDIA", vm.NvidiaStatusTag),
            Row("riot", "Riot", vm.RiotStatusTag),
            Row("epic", "Epic", vm.EpicStatusTag),
        };

        object? next = null;
        if (vm.HasNextAction && !string.IsNullOrWhiteSpace(vm.NextActionModule))
        {
            next = new
            {
                id = MapNextId(vm.NextActionModule),
                label = string.IsNullOrWhiteSpace(vm.NextActionLabel) ? vm.NextActionModule : vm.NextActionLabel
            };
        }

        return new
        {
            overview = vm.OverviewPrimary,
            heroSummary = vm.HeroSummary,
            specs = new
            {
                cpu = vm.SpecsCpu,
                gpu = vm.SpecsGpu,
                ram = vm.SpecsRam,
                os = vm.SpecsOs
            },
            // Prefer lightweight live snapshot so home meters never depend on a
            // second full DashboardViewModel construct (detect + seed).
            live = BuildLiveSnapshot(),
            modules,
            next
        };
    }

    /// <summary>
    /// Lightweight live tick — never construct DashboardViewModel (that runs full
    /// detect/seed and starves the UI every ~1s, emptying meter cards).
    /// </summary>
    private object BuildLive() => BuildLiveSnapshot();

    private object BuildLiveSnapshot()
    {
        var mem = HomeDashboardReader.TryReadMemory();
        double memPct = 0;
        var used = "—";
        var total = "—";
        var memSecondary = "—";
        if (mem is not null)
        {
            memPct = mem.LoadPercent;
            var usedB = mem.TotalBytes > mem.AvailableBytes ? mem.TotalBytes - mem.AvailableBytes : 0UL;
            used = HomeDashboardReader.FormatBytes(usedB);
            total = HomeDashboardReader.FormatBytes(mem.TotalBytes);
            memSecondary = $"{used} / {total}";
        }

        var cpu = HomeDashboardReader.TryReadCpuLoadPercent();
        var gpu = HomeDashboardReader.TryReadGpuLoadPercent();

        var link = HomeDashboardReader.TryReadPrimaryLinkSpeed();
        var netLinkSpeed = link is not null && link.BitsPerSecond > 0 ? link.Label : "—";
        var netLinkMedia = link is not null && link.BitsPerSecond > 0 ? link.MediaKind : "No link";
        var netLink = link is not null && link.BitsPerSecond > 0
            ? $"{link.Label} {link.MediaKind}"
            : "No link";

        var quality = _services.Network.LoadQualityBenchmark();
        double? idleMs = null;
        double? down = null, up = null, loadedDown = null, loadedUp = null, loss = null;
        string? dns = null;
        var hasQuality = false;
        if (quality is { Ok: true, IsQualityTest: true })
        {
            hasQuality = true;
            idleMs = quality.PingP50Ms;
            if (quality.DownloadMbps > 0) down = Math.Round(quality.DownloadMbps, 0);
            if (quality.UploadMbps > 0) up = Math.Round(quality.UploadMbps, 0);
            // Absolute loaded path latency (not only delta) for clear cards.
            if (quality.DownloadLoadedMs > 0)
                loadedDown = Math.Round(quality.DownloadLoadedMs, 1);
            if (quality.UploadLoadedMs > 0)
                loadedUp = Math.Round(quality.UploadLoadedMs, 1);
            loss = quality.PacketLossPercent;
            if (!string.IsNullOrWhiteSpace(quality.DnsProvider))
                dns = quality.DnsProvider;
        }
        else
        {
            var latency = HomeDashboardReader.TryReadLatency(_services.Network);
            if (latency is not null)
                idleMs = latency.AfterP50Ms;
            dns ??= HomeDashboardReader.TryReadInternetDnsStatus();
        }

        var idleLabel = idleMs is not null ? $"{idleMs.Value:0.#} ms" : "—";
        var (rating, ratingDetail) = RateNetwork(idleMs, loss, loadedDown, loadedUp, hasQuality);

        return new
        {
            memoryPercent = memPct,
            memoryUsed = used,
            memoryTotal = total,
            memorySecondary = memSecondary,
            cpuPercent = cpu is null ? 0 : Math.Round(cpu.Value, 0),
            hasCpu = cpu is not null,
            gpuPercent = gpu is null ? 0 : Math.Round(gpu.Value, 0),
            hasGpu = gpu is not null,
            netLink,
            netLinkSpeed,
            netLinkMedia,
            netIdleMs = idleLabel,
            netIdleMsValue = idleMs ?? 0,
            netDownMbps = down,
            netUpMbps = up,
            netLoadedDownMs = loadedDown,
            netLoadedUpMs = loadedUp,
            netLoss = loss is null ? null : $"{loss:0.##}%",
            netLossPercent = loss,
            netDns = dns,
            netRating = rating,
            netRatingDetail = ratingDetail,
            // Kept for older UI; no longer mapped to a fake “health” bar.
            netMetricPercent = 0
        };
    }

    /// <summary>Simple honest grade from last quality sample (or idle-only).</summary>
    private static (string Rating, string Detail) RateNetwork(
        double? idleMs,
        double? lossPercent,
        double? loadedDownMs,
        double? loadedUpMs,
        bool hasQuality)
    {
        if (idleMs is null && !hasQuality)
            return ("—", "Run Internet → Apply for a full quality sample.");

        var score = 100.0;
        var notes = new List<string>();

        if (idleMs is double idle)
        {
            if (idle <= 15) { /* excellent */ }
            else if (idle <= 30) { score -= 10; notes.Add("idle latency ok"); }
            else if (idle <= 50) { score -= 25; notes.Add("idle latency elevated"); }
            else if (idle <= 80) { score -= 40; notes.Add("idle latency high"); }
            else { score -= 55; notes.Add("idle latency poor"); }
        }

        if (lossPercent is double loss)
        {
            if (loss <= 0.1) { /* clean */ }
            else if (loss <= 1) { score -= 15; notes.Add("light packet loss"); }
            else if (loss <= 3) { score -= 30; notes.Add("packet loss"); }
            else { score -= 50; notes.Add("heavy packet loss"); }
        }

        if (loadedDownMs is double ld && idleMs is double idle2 && idle2 > 0)
        {
            var inflate = ld / Math.Max(1, idle2);
            if (inflate > 4) score -= 20;
            else if (inflate > 2.5) score -= 10;
        }
        if (loadedUpMs is double lu && idleMs is double idle3 && idle3 > 0)
        {
            var inflate = lu / Math.Max(1, idle3);
            if (inflate > 4) score -= 15;
            else if (inflate > 2.5) score -= 8;
        }

        if (!hasQuality)
            score = Math.Min(score, 75);

        score = Math.Clamp(score, 0, 100);
        var rating = score >= 85 ? "Excellent"
            : score >= 70 ? "Good"
            : score >= 50 ? "Fair"
            : "Poor";
        // Keep detail empty — home card shows rating grade only (no prose notes).
        return (rating, "");
    }

    /// <summary>Public tip jar — free app; optional support.</summary>
    public const string BuyMeACoffeeUrl = "https://www.buymeacoffee.com/UhhErix";
    public const string IssuesUrl = "https://github.com/ImAvgErix/Exo/issues";

    private object BuildSettings()
    {
        var s = _services.Settings.Current;
        return new
        {
            appVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "3.7.2",
            checkForUpdatesOnLaunch = s.CheckForUpdatesOnLaunch,
            welcomePromptSeen = s.WelcomePromptSeen,
            buyMeACoffeeUrl = BuyMeACoffeeUrl,
            issuesUrl = IssuesUrl,
            experimentalDefaults = new
            {
                discord = s.ExperimentalDiscord,
                steam = s.ExperimentalSteam,
                windows = s.ExperimentalWindows,
                internet = s.ExperimentalInternet,
                nvidia = s.ExperimentalNvidia,
                riot = s.ExperimentalRiot,
                epic = s.ExperimentalEpic
            }
        };
    }

    /// <summary>
    /// In-app changelog from bundled CHANGELOG.md (repo root next to app).
    /// Parsed into version sections for the glass settings sheet.
    /// </summary>
    private object BuildChangelog()
    {
        try
        {
            var path = ResolveChangelogPath();
            if (path is null || !File.Exists(path))
            {
                return new
                {
                    ok = false,
                    message = "Changelog file not found.",
                    sections = Array.Empty<object>()
                };
            }

            var text = File.ReadAllText(path);
            var sections = ParseChangelogMarkdown(text);
            return new
            {
                ok = true,
                path,
                sections
            };
        }
        catch (Exception ex)
        {
            return new
            {
                ok = false,
                message = ex.Message,
                sections = Array.Empty<object>()
            };
        }
    }

    private static string? ResolveChangelogPath()
    {
        // Published: next to Exo.exe. Dev: repo root / AppDirectory parents.
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md"),
            Path.Combine(PathHelper.AppDirectory, "CHANGELOG.md"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CHANGELOG.md")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "CHANGELOG.md")),
        };
        foreach (var c in candidates)
        {
            try
            {
                if (File.Exists(c)) return c;
            }
            catch { /* skip */ }
        }
        return null;
    }

    /// <summary>Parse ## version headers + - bullets into UI sections (newest first, cap 40).</summary>
    internal static List<object> ParseChangelogMarkdown(string text)
    {
        var sections = new List<object>();
        string? version = null;
        var bullets = new List<string>();

        void Flush()
        {
            if (version is null) return;
            sections.Add(new
            {
                version,
                bullets = bullets.ToArray()
            });
            bullets.Clear();
            version = null;
        }

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                version = line[3..].Trim().TrimStart('v', 'V');
                continue;
            }
            if (version is null) continue;
            var t = line.Trim();
            if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal))
            {
                var b = t[2..].Trim();
                // Drop markdown bold markers for cleaner in-app text
                b = b.Replace("**", "", StringComparison.Ordinal);
                if (b.Length > 0) bullets.Add(b);
            }
        }
        Flush();

        // Newest first already if file is newest-first; cap for UI
        if (sections.Count > 40)
            sections = sections.Take(40).ToList();
        return sections;
    }

    private object SetSettings(JsonElement p, bool hasParams)
    {
        if (!hasParams || p.ValueKind != JsonValueKind.Object)
            return BuildSettings();

        _services.Settings.Update(s =>
        {
            if (p.TryGetProperty("checkForUpdatesOnLaunch", out var u) &&
                (u.ValueKind is JsonValueKind.True or JsonValueKind.False))
                s.CheckForUpdatesOnLaunch = u.ValueKind == JsonValueKind.True;
            if (p.TryGetProperty("welcomePromptSeen", out var w) &&
                (w.ValueKind is JsonValueKind.True or JsonValueKind.False))
                s.WelcomePromptSeen = w.ValueKind == JsonValueKind.True;
        });
        return BuildSettings();
    }

    /// <summary>Last URL + tick so a double-attached bridge or double-click cannot open two tabs.</summary>
    private string? _lastOpenUrl;
    private long _lastOpenUrlTick;

    private object OpenExternalUrl(JsonElement p, bool hasParams)
    {
        try
        {
            var url = ReadString(p, hasParams, "url")?.Trim();
            if (string.IsNullOrWhiteSpace(url))
                url = BuyMeACoffeeUrl;
            // Only allow http(s) so the bridge cannot launch local files/shells.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return new { ok = false, message = "Only http(s) links are allowed." };

            if (!TryOpenUrlOnce(uri.AbsoluteUri))
                return new { ok = true, url = uri.AbsoluteUri, deduped = true };

            return new { ok = true, url = uri.AbsoluteUri };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
    }

    private object OpenLogsFolder()
    {
        try
        {
            var logs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Exo", "logs");
            Directory.CreateDirectory(logs);
            // Always open the folder (user asked for logs directory, not newest file).
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + logs + "\"",
                UseShellExecute = true
            });
            return new { ok = true, path = logs, folder = logs };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
    }

    private object OpenIssues()
    {
        try
        {
            if (!TryOpenUrlOnce(IssuesUrl))
                return new { ok = true, deduped = true };
            return new { ok = true };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
    }

    /// <returns>false if the same URL was opened within the last 800ms (skip second tab).</returns>
    private bool TryOpenUrlOnce(string absoluteUrl)
    {
        var now = Environment.TickCount64;
        if (_lastOpenUrl is not null &&
            string.Equals(_lastOpenUrl, absoluteUrl, StringComparison.OrdinalIgnoreCase) &&
            now - _lastOpenUrlTick < 800)
            return false;

        _lastOpenUrl = absoluteUrl;
        _lastOpenUrlTick = now;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = absoluteUrl,
            UseShellExecute = true
        });
        return true;
    }

    private object OpenNvidiaControlPanel()
    {
        try
        {
            if (_services.NvidiaPanel.TryLaunchControlPanel(out var error))
                return new { ok = true };
            return new { ok = false, message = error ?? "NVIDIA Control Panel is not installed." };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
    }

    /// <summary>
    /// Check GitHub latest; when an update is available, download + quiet-install
    /// without a native ContentDialog card. Progress streams to the WebView settings panel.
    /// </summary>
    private async Task<object> CheckUpdatesAsync()
    {
        string AppVer()
        {
            var v = typeof(App).Assembly.GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }

        void PushProgress(string status, double percent) =>
            PostEvent("settings.updateProgress", new { status, percent });

        try
        {
            var status = new Progress<string>(m => PushProgress(m, -1));
            var detail = new Progress<AppUpdateProgress>(p =>
                PushProgress(p.Status, p.Percent));

            PushProgress("Checking GitHub releases…", -1);
            var check = await _services.Updater
                .CheckAppUpdateAsync(status: status, progress: detail)
                .ConfigureAwait(true);

            if (!check.UpdateAvailable)
            {
                PushProgress(check.Message, check.AlreadyLatest ? 100 : -1);
                return new
                {
                    message = check.Message,
                    updateAvailable = false,
                    alreadyLatest = check.AlreadyLatest,
                    installed = false,
                    shouldExit = false,
                    appVersion = AppVer(),
                    localVersion = check.LocalVersion,
                    remoteVersion = check.RemoteVersion,
                    releaseSummary = check.ReleaseSummary
                };
            }

            // InstallAppUpdateAsync already reports Downloading / Verifying / Installing —
            // do not pre-push a second "Downloading" line (UI showed it twice).
            var install = await _services.Updater
                .InstallAppUpdateAsync(check, status: status, progress: detail)
                .ConfigureAwait(true);

            if (install.ShouldExit)
            {
                PushProgress(install.Message, 100);
                // SFX is waiting on our PID (/waitpid) — exit quickly so it can replace the app folder.
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(250).ConfigureAwait(false); } catch { }
                    try
                    {
                        _queue.TryEnqueue(() =>
                        {
                            try { Microsoft.UI.Xaml.Application.Current?.Exit(); } catch { }
                            try { Environment.Exit(0); } catch { }
                        });
                    }
                    catch
                    {
                        try { Environment.Exit(0); } catch { }
                    }
                });
            }
            else
            {
                // Installer never launched or refused — show the real error in Settings.
                PushProgress(install.Message, -1);
            }

            return new
            {
                message = install.Message,
                updateAvailable = true,
                alreadyLatest = false,
                installed = install.ShouldExit,
                shouldExit = install.ShouldExit,
                appVersion = AppVer(),
                localVersion = install.LocalVersion,
                remoteVersion = install.RemoteVersion,
                releaseSummary = check.ReleaseSummary
            };
        }
        catch (Exception ex)
        {
            PushProgress(ex.Message, -1);
            return new
            {
                message = ex.Message,
                updateAvailable = false,
                alreadyLatest = false,
                installed = false,
                shouldExit = false,
                appVersion = AppVer()
            };
        }
    }

    private static object Row(string id, string title, string tag) =>
        new
        {
            id,
            title,
            applied = string.Equals(tag, "VERIFIED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, "APPLIED", StringComparison.OrdinalIgnoreCase)
        };

    private static string MapNextId(string label) =>
        label.ToLowerInvariant() switch
        {
            "discord" => "discord",
            "steam" => "steam",
            "games" => "games",
            "windows" => "windows",
            "internet" => "internet",
            "nvidia" => "nvidia",
            "riot" => "riot",
            "epic" => "epic",
            _ => "discord"
        };

    private async Task<object> DetectModuleAsync(JsonElement p, bool hasParams)
    {
        var module = ReadString(p, hasParams, "module") ?? "discord";
        var force = false;
        if (hasParams && p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty("force", out var forceEl) &&
            (forceEl.ValueKind == JsonValueKind.True ||
             (forceEl.ValueKind == JsonValueKind.String &&
              bool.TryParse(forceEl.GetString(), out var fb) && fb)))
            force = true;
        return await DetectCoreAsync(module, force).ConfigureAwait(true);
    }

    private async Task<object> DetectCoreAsync(string module, bool force = false)
    {
        var key = (module ?? "discord").Trim().ToLowerInvariant();
        if (!force &&
            _detectCache.TryGetValue(key, out var hit) &&
            DateTimeOffset.UtcNow - hit.At < ModuleDetectFreshness)
            return hit.Payload;

        var ct = CancellationToken.None;
        // Always full detect so the UI feature list matches Apply (heuristics hide tweaks).
        // Scripts are Get-ScheduledTask-free; host cache (120s) keeps re-opens instant.
        object payload = key switch
        {
            "discord" => MapState("discord", await _services.OptimizerState.DetectDiscordAsync(ct, fastOnly: false).ConfigureAwait(true)),
            "steam" => MapState("steam", await _services.OptimizerState.DetectSteamAsync(ct, fastOnly: false).ConfigureAwait(true)),
            "games" => MapState("games", await Task.Run(() => _services.Games.Detect()).ConfigureAwait(true)),
            "windows" => MapState("windows", await _services.OptimizerState.DetectWindowsAsync(ct).ConfigureAwait(true)),
            "nvidia" => MapState("nvidia", await _services.OptimizerState.DetectNvidiaAsync(ct, fastOnly: false).ConfigureAwait(true)),
            "riot" => MapState("riot", await _services.OptimizerState.DetectRiotAsync(ct).ConfigureAwait(true)),
            "epic" => MapState("epic", await _services.OptimizerState.DetectEpicAsync(ct).ConfigureAwait(true)),
            "internet" => await MapInternetAsync(force).ConfigureAwait(true),
            _ => throw new InvalidOperationException($"Unknown module: {module}")
        };
        _detectCache[key] = (DateTimeOffset.UtcNow, payload);
        return payload;
    }

    private void InvalidateDetectCache(string? module = null)
    {
        if (string.IsNullOrWhiteSpace(module))
        {
            _detectCache.Clear();
            _internetProbeCache = null;
            _internetProbeCacheUtc = DateTimeOffset.MinValue;
            return;
        }
        _detectCache.Remove(module.Trim().ToLowerInvariant());
        if (string.Equals(module, "internet", StringComparison.OrdinalIgnoreCase))
        {
            _internetProbeCache = null;
            _internetProbeCacheUtc = DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Internet detect surfaces the same four plain-language cards as the native
    /// InternetOptimizerViewModel (path / policy / DNS / repair), plus adapter
    /// identity from ProbeAsync when available.
    /// </summary>
    private async Task<object> MapInternetAsync(bool force = false)
    {
        NetworkSnapshot? snap = null;
        try
        {
            if (!force &&
                _internetProbeCache is not null &&
                DateTimeOffset.UtcNow - _internetProbeCacheUtc < InternetProbeFreshness)
            {
                snap = _internetProbeCache;
            }
            else
            {
                snap = await _services.Network.ProbeAsync().ConfigureAwait(true);
                _internetProbeCache = snap;
                _internetProbeCacheUtc = DateTimeOffset.UtcNow;
            }
        }
        catch
        {
            /* probe optional — fall back to persisted state */
        }

        var savedPreset = snap?.ActivePreset ?? _services.Network.LoadSavedPreset();
        // Only competitive presets count as applied — not Balanced / leftover status strings.
        var applied = savedPreset is NetworkPreset.LowestLatency or NetworkPreset.HighestThroughput;
        var preferLowest = savedPreset != NetworkPreset.HighestThroughput;
        var presetLabel = savedPreset switch
        {
            NetworkPreset.HighestThroughput => "high throughput",
            NetworkPreset.LowestLatency => "lowest latency",
            _ => "balanced"
        };

        var pathDetail = snap is null
            ? "Probe the live path on detect; Ethernet gets the lowest route metric when present."
            : snap.Media.EthernetInUse
                ? $"{snap.LinkSpeed} Ethernet gets the lowest route metric; Wi-Fi is never disabled."
                : snap.Media.WifiUp
                    ? $"Wi-Fi stays enabled and prefers {snap.Media.PreferredBandTarget} when the adapter supports it."
                    : "Keeps every adapter recoverable and changes route priority only when a healthy path exists.";

        var policyDetail = applied
            ? $"Last apply used {presetLabel}. Toggle selects Lowest latency (FC/IM off) or High throughput (FC/IM on)."
            : $"Selected: {(preferLowest ? "lowest latency" : "high throughput")}. Analyze measures DNS/quality, then applies your toggle.";

        var dnsStatus = HomeDashboardReader.TryReadInternetDnsStatus();
        var dnsDetail = !string.IsNullOrWhiteSpace(dnsStatus)
            ? dnsStatus!
            : snap is { DnsServers: var dns } && !string.IsNullOrWhiteSpace(dns) && dns is not ("—" or "-")
                ? $"Current resolvers: {dns}"
                : "Tests Cloudflare, Google, and Quad9 on this route, selects the fastest healthy resolver, and requests automatic DoH when Windows supports it.";

        var hasSnapshot = NetworkOptimizerService.HasRestoreSnapshot();
        var repairDetail = hasSnapshot
            ? "A pre-Exo snapshot is ready; Repair restores DNS, DoH, routes, TCP, and NIC settings."
            : "Apply takes a pre-change snapshot; Repair can return the Windows network stack to stock defaults.";

        var features = new List<object>
        {
            new { title = "Connection path", detail = pathDetail, active = snap?.ProbeOk ?? false },
            new { title = "Policy", detail = policyDetail, active = true },
            new { title = "DNS privacy", detail = dnsDetail, active = applied || !string.IsNullOrWhiteSpace(dnsStatus) },
            new { title = "Safe repair", detail = repairDetail, active = true },
        };

        // Last apply steps (compact) when available.
        try
        {
            var report = _services.Network.LoadLastApplyReport();
            if (report is { Count: > 0 })
            {
                var fails = report.Where(r =>
                        string.Equals(r.Status, "fail", StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.Name)
                    .Take(3)
                    .ToList();
                var oks = report.Count(r =>
                    string.Equals(r.Status, "ok", StringComparison.OrdinalIgnoreCase));
                features.Add(new
                {
                    title = "Last apply",
                    detail = fails.Count > 0
                        ? $"Issues: {string.Join(", ", fails)} ({oks} ok steps)."
                        : $"{oks} steps ok on last apply (DNS, path, TCP, NIC).",
                    active = fails.Count == 0
                });
            }
        }
        catch { /* optional */ }

        // Adapter / NIC identity when probe succeeded (compact — keeps the card grid non-scrolling).
        if (snap is not null)
        {
            if (!string.IsNullOrWhiteSpace(snap.Media.NicVendor) &&
                snap.Media.NicVendor is not ("Unknown" or "Other" or ""))
            {
                var link = snap.Media.PrimaryLinkSpeedBps >= 2_500_000_000 ? "2.5G+"
                    : snap.Media.PrimaryLinkSpeedBps >= 1_000_000_000 ? "1G"
                    : snap.Media.PrimaryLinkSpeedBps >= 100_000_000 ? "100M" : snap.LinkSpeed;
                features.Add(new
                {
                    title = "Adapter",
                    detail = $"{snap.Media.PrimaryMediaKind} · {snap.Media.NicVendor} · {link}",
                    active = true
                });
            }

            if (!string.IsNullOrWhiteSpace(snap.Media.NicHints) && snap.Media.NicHints is not ("—" or "-"))
            {
                features.Add(new
                {
                    title = "NIC status",
                    detail = snap.Media.NicHints,
                    active = snap.Media.NicOk
                });
            }
        }

        var pathOk = snap?.ProbeOk ?? false;
        var dnsOk = applied || !string.IsNullOrWhiteSpace(dnsStatus);
        var checkableOff = new List<string>();
        if (!pathOk) checkableOff.Add("Connection path");
        if (!dnsOk) checkableOff.Add("DNS privacy");
        if (snap is not null &&
            !string.IsNullOrWhiteSpace(snap.Media.NicHints) &&
            snap.Media.NicHints is not ("—" or "-") &&
            !snap.Media.NicOk)
            checkableOff.Add("NIC status");

        // Feature tiles always include Policy + Safe repair as active; count from list size
        var visibleTotal = features.Count;
        var visibleOn = Math.Max(0, visibleTotal - checkableOff.Count);

        string statusKind;
        string statusText;
        if (applied && checkableOff.Count == 0)
        {
            statusKind = "applied";
            statusText = visibleTotal > 0
                ? $"Applied · {visibleOn}/{visibleTotal} on"
                : "Applied";
        }
        else if (applied && checkableOff.Count > 0)
        {
            statusKind = "partial";
            statusText = $"Partial · {checkableOff.Count} still off · {visibleOn}/{visibleTotal} on";
        }
        else if (checkableOff.Count > 0)
        {
            statusKind = "ready";
            statusText = checkableOff.Count == 1
                ? $"Ready · 1 need Apply ({checkableOff[0]})"
                : $"Ready · {checkableOff.Count} need Apply";
        }
        else
        {
            statusKind = applied ? "applied" : "ready";
            statusText = applied
                ? (savedPreset == NetworkPreset.HighestThroughput
                    ? "High-throughput stack applied"
                    : "Lowest-latency stack applied")
                : "Ready to optimize";
        }

        var detail = applied
            ? "Stack applied. Use Apply to re-measure and reapply, or Repair to undo."
            : "Measure the live path and apply the adaptive stack for your selected profile.";

        if (snap is { ProbeOk: true, InternetPingMs: int ping })
            detail = applied
                ? $"Live ~{ping} ms · {presetLabel} stack. Reapply or Repair."
                : $"Live ~{ping} ms · ready to apply {(preferLowest ? "lowest latency" : "high throughput")}.";

        if (checkableOff.Count > 0)
            detail = "Off: " + string.Join(", ", checkableOff) + ".";
        else if (statusKind == "applied")
            detail = "Verified on this PC from live checks.";

        var applyReport = OptimizerStateService.TryReadApplyReport("network");
        if (applyReport.Count == 0)
            applyReport = OptimizerStateService.TryReadApplyReport("internet");

        return new
        {
            id = "internet",
            isApplied = statusKind == "applied",
            statusKind,
            statusText,
            detail,
            features = features.ToArray(),
            applyReport = applyReport.ToArray(),
            options = new
            {
                experimental = _services.Settings.Current.ExperimentalInternet,
                useGsync = true,
                preferLowestLatency = preferLowest
            }
        };
    }

    private object MapState(string id, OptimizerStateInfo state)
    {
        var useGsync = true;
        if (id == "nvidia" && state.Extra is not null)
        {
            if (state.Extra.TryGetValue("gsync", out var g) || state.Extra.TryGetValue("Gsync", out g))
                useGsync = string.Equals(g, "true", StringComparison.OrdinalIgnoreCase) || g == "1";
        }

        // Recompute status from feature rows so "N need Apply" matches red tiles.
        static bool IsInfoTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return true;
            var t = title.Trim();
            return t.Equals("Optimization verified", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Anti-cheat untouched", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("One-click Repair ready", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Launcher junk cleaned", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Safe repair", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Policy", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Adapter", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Last apply", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Display scaling & color", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Latency / sync policy", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Stack profile", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Gaming multimedia stack", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Host gaming stack", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Profile", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("DLSS left alone", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Exo packs ready", StringComparison.OrdinalIgnoreCase)
                   // Games hub: informational / diagnostic only (must not fail Apply status)
                   || t.Equals("Install / configs", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Method", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Ban-safe surface", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Already competitive lows?", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Anisotropic filter", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Vanguard", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Scalability shadows", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Character / Effects quality", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("NVIDIA Reflex", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("FPS limits off (menu/bg/battery)", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Texture / Material / Detail", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("Shadows / Bloom / AA", StringComparison.OrdinalIgnoreCase);
        }

        var checkable = state.Features
            .Where(f => !IsInfoTitle(f.Title))
            .ToList();
        var off = checkable.Where(f => !f.IsActive).Select(f => f.Title).ToList();
        var visibleOn = state.Features.Count(f => f.IsActive);
        var visibleTotal = state.Features.Count;

        // Shared vocabulary: Ready / Applied / Partial / Missing
        var hostBlob = $"{state.StatusText} {state.Detail}".ToLowerInvariant();
        var missing = hostBlob.Contains("not installed") ||
                      hostBlob.Contains("no nvidia") ||
                      hostBlob.Contains("marvel rivals not installed") ||
                      hostBlob.Contains("not found in steam");
        string statusKind;
        string statusText;
        // Games hub: host IsApplied / profile marker is source of truth. Live quality rows are diagnostics.
        // Keep "Potato applied" / "Optimized applied" from the service — never generic "optimized".
        if (string.Equals(id, "games", StringComparison.OrdinalIgnoreCase) && state.IsApplied)
        {
            statusKind = "applied";
            statusText = !string.IsNullOrWhiteSpace(state.StatusText)
                ? state.StatusText
                : visibleTotal > 0
                    ? $"Applied · {visibleOn}/{visibleTotal} on"
                    : "Applied";
        }
        else if (missing && !state.IsApplied)
        {
            statusKind = "missing";
            statusText = "Missing target";
        }
        else if (off.Count == 0 && (state.IsApplied || checkable.Count == 0 || checkable.All(f => f.IsActive)))
        {
            statusKind = "applied";
            statusText = visibleTotal > 0
                ? $"Applied · {visibleOn}/{visibleTotal} on"
                : "Applied";
        }
        else if (off.Count > 0 && (state.IsApplied || state.Features.Any(f => f.IsActive)))
        {
            statusKind = "partial";
            statusText = $"Partial · {off.Count} still off · {visibleOn}/{visibleTotal} on";
        }
        else if (off.Count > 0)
        {
            statusKind = "ready";
            statusText = off.Count == 1
                ? $"Ready · 1 need Apply ({off[0]})"
                : $"Ready · {off.Count} need Apply";
        }
        else
        {
            statusKind = "ready";
            statusText = "Ready";
        }

        // Honest applied flag for UI — for games, trust host after Apply
        var isApplied = statusKind == "applied"
                        || (string.Equals(id, "games", StringComparison.OrdinalIgnoreCase) && state.IsApplied);

        // applyReport lives in *-optimizer.json (aliases for games/network)
        var reportId = id switch
        {
            "games" => "game",
            "internet" => "network",
            _ => id
        };
        // game-optimizer.json (not games-); TryReadApplyReport uses "{module}-optimizer.json"
        if (reportId == "game")
        {
            // Prefer game-optimizer.json; also try games-
        }
        var applyReport = OptimizerStateService.TryReadApplyReport(reportId);
        if (applyReport.Count == 0 && id == "games")
            applyReport = OptimizerStateService.TryReadApplyReport("games");
        if (applyReport.Count == 0 && id == "internet")
        {
            // network-optimizer may not use applyReport array — leave empty
        }

        return new
        {
            id,
            isApplied,
            statusKind,
            statusText,
            detail = off.Count > 0
                ? "Off: " + string.Join(", ", off) + "."
                : string.IsNullOrWhiteSpace(state.Detail)
                    ? (statusKind == "applied" ? "Verified on this PC from live checks." : state.Detail)
                    : state.Detail,
            features = state.Features.Select(f => new
            {
                title = f.Title,
                detail = f.Detail,
                active = f.IsActive
            }).ToArray(),
            applyReport = applyReport.ToArray(),
            options = new
            {
                experimental = id switch
                {
                    "discord" => _services.Settings.Current.ExperimentalDiscord,
                    "steam" => _services.Settings.Current.ExperimentalSteam,
                    "windows" => _services.Settings.Current.ExperimentalWindows,
                    "internet" => _services.Settings.Current.ExperimentalInternet,
                    "nvidia" => _services.Settings.Current.ExperimentalNvidia,
                    "riot" => _services.Settings.Current.ExperimentalRiot,
                    "epic" => _services.Settings.Current.ExperimentalEpic,
                    _ => false
                },
                useGsync,
                preferLowestLatency = true,
                // Prefer activePreset (last applied) so Potato titles don't open as Optimized.
                gamePreset = state.Extra is not null &&
                             state.Extra.TryGetValue("activePreset", out var ap) &&
                             !string.IsNullOrWhiteSpace(ap)
                    ? ap
                    : state.Extra is not null &&
                      state.Extra.TryGetValue("preset", out var gp) &&
                      !string.IsNullOrWhiteSpace(gp)
                        ? gp
                        : "optimized",
                displayMode = state.Extra is not null &&
                              state.Extra.TryGetValue("displayMode", out var dm) &&
                              !string.IsNullOrWhiteSpace(dm)
                    ? dm
                    : "leave"
            }
        };
    }

    /// <summary>
    /// Settings → Verify: force live detect for every module (no Apply).
    /// </summary>
    private async Task<object> VerifyAllModulesAsync()
    {
        var modules = new[]
        {
            "discord", "steam", "windows", "internet", "nvidia", "riot", "epic", "games"
        };
        var results = new List<object>();
        var applied = 0;
        var partial = 0;
        var ready = 0;
        var missing = 0;

        for (var i = 0; i < modules.Length; i++)
        {
            var m = modules[i];
            PostEvent("settings.verifyProgress", new
            {
                percent = (i + 1) * 100.0 / modules.Length,
                status = $"Verifying {m}…"
            });
            InvalidateDetectCache(m);
            try
            {
                var row = await DetectCoreAsync(m, force: true).ConfigureAwait(true);
                results.Add(row);
                CountKind(ExtractStatusKind(row), ref applied, ref partial, ref ready, ref missing);
            }
            catch (Exception ex)
            {
                results.Add(new
                {
                    id = m,
                    statusKind = "failed",
                    statusText = "Failed",
                    detail = ex.Message,
                    isApplied = false
                });
            }
        }

        PostEvent("settings.verifyProgress", new { percent = 100.0, status = "Verify complete" });
        InvalidateDetectCache();
        return new
        {
            results,
            summary = $"{applied} applied · {partial} partial · {ready} ready · {missing} missing",
            applied,
            partial,
            ready,
            missing
        };
    }

    private static void CountKind(string kind, ref int applied, ref int partial, ref int ready, ref int missing)
    {
        switch (kind)
        {
            case "applied": applied++; break;
            case "partial": partial++; break;
            case "missing": missing++; break;
            default: ready++; break;
        }
    }

    private static string ExtractStatusKind(object mapped)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(mapped);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("statusKind", out var k))
                return k.GetString() ?? "ready";
            if (doc.RootElement.TryGetProperty("isApplied", out var a) && a.ValueKind == JsonValueKind.True)
                return "applied";
        }
        catch { }
        return "ready";
    }

    private async Task<object> ApplyModuleAsync(JsonElement p, bool hasParams)
    {
        var module = (ReadString(p, hasParams, "module") ?? "discord").ToLowerInvariant();
        var experimental = ReadBool(p, hasParams, "experimental");
        var useGsync = !hasParams || !p.TryGetProperty("useGsync", out _) || ReadBool(p, hasParams, "useGsync");
        var preferLowestLatency = !hasParams || !p.TryGetProperty("preferLowestLatency", out _)
            || ReadBool(p, hasParams, "preferLowestLatency");
        // Defaults: G-SYNC on for NVIDIA when key omitted; lowest latency for Internet when omitted.
        if (hasParams && p.TryGetProperty("useGsync", out var gs))
            useGsync = gs.ValueKind == JsonValueKind.True;
        if (hasParams && p.TryGetProperty("preferLowestLatency", out var ll))
            preferLowestLatency = ll.ValueKind == JsonValueKind.True;
        var gamePreset = ReadString(p, hasParams, "gamePreset")
            ?? ReadString(p, hasParams, "preset")
            ?? GameOptimizerService.PresetOptimized;
        // Persist experimental toggle so next open matches last Apply intent.
        try
        {
            _services.Settings.Update(s =>
            {
                switch (module)
                {
                    case "discord": s.ExperimentalDiscord = experimental; break;
                    case "steam": s.ExperimentalSteam = experimental; break;
                    case "windows": s.ExperimentalWindows = experimental; break;
                    case "internet": s.ExperimentalInternet = experimental; break;
                    case "nvidia": s.ExperimentalNvidia = experimental; break;
                    case "riot": s.ExperimentalRiot = experimental; break;
                    case "epic": s.ExperimentalEpic = experimental; break;
                }
            });
        }
        catch { /* non-fatal */ }

        try
        {
            if (module == "games")
            {
                var gameId = ReadString(p, hasParams, "gameId")
                             ?? GameOptimizerService.GameIdMarvelRivals;
                using var log = new ModuleApplyLog("games");
                void Report(double percent, string status)
                {
                    log.Progress(percent, status);
                    PostEvent("module.progress", new { module, percent, status });
                }
                Report(5, "Applying game profile…");
                var strProgress = new Progress<string>(s =>
                {
                    log.Line(s);
                    Report(-1, s);
                });
                var (ok, msg) = await _services.Games
                    .ApplyAsync(gameId, gamePreset, strProgress, CancellationToken.None)
                    .ConfigureAwait(true);
                log.Line($"result ok={ok} msg={msg}");
                if (!ok)
                {
                    log.Finish(false, msg);
                    throw new InvalidOperationException(msg);
                }
                Report(100, "Done");
                log.Finish(true, msg);
                InvalidateDetectCache();
                return await DetectCoreAsync(module, force: true).ConfigureAwait(true);
            }

            await RunModuleScriptAsync(module, repair: false, experimental, useGsync, preferLowestLatency)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Always point at the detailed log so Riot/Windows failures are actionable.
            var latest = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Exo", "logs", $"apply-{module}-latest.log");
            var hint = File.Exists(latest)
                ? $"{ex.Message}{Environment.NewLine}Full log: {latest}"
                : ex.Message;
            throw new InvalidOperationException(hint, ex);
        }
        // Cross-module side effects (Windows Game Bar, yield companions, DSCP, …)
        InvalidateDetectCache();
        return await DetectCoreAsync(module, force: true).ConfigureAwait(true);
    }

    private async Task<object> RepairModuleAsync(JsonElement p, bool hasParams)
    {
        var module = (ReadString(p, hasParams, "module") ?? "discord").ToLowerInvariant();
        try
        {
            if (module == "games")
            {
                var gameId = ReadString(p, hasParams, "gameId")
                             ?? GameOptimizerService.GameIdMarvelRivals;
                using var log = new ModuleApplyLog("game-repair");
                void Report(double percent, string status)
                {
                    log.Progress(percent, status);
                    PostEvent("module.progress", new { module, percent, status });
                }
                Report(10, "Restoring game configs…");
                var strProgress = new Progress<string>(s =>
                {
                    log.Line(s);
                    Report(-1, s);
                });
                var (ok, msg) = await _services.Games
                    .RepairAsync(gameId, strProgress, CancellationToken.None)
                    .ConfigureAwait(true);
                log.Line($"result ok={ok} msg={msg}");
                if (!ok)
                {
                    log.Finish(false, msg);
                    throw new InvalidOperationException(msg);
                }
                Report(100, "Repair complete");
                log.Finish(true, msg);
                InvalidateDetectCache();
                return await DetectCoreAsync(module, force: true).ConfigureAwait(true);
            }

            await RunModuleScriptAsync(module, repair: true, experimental: false).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            var latest = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Exo", "logs", $"apply-{module}-latest.log");
            var hint = File.Exists(latest)
                ? $"{ex.Message}{Environment.NewLine}Full log: {latest}"
                : ex.Message;
            throw new InvalidOperationException(hint, ex);
        }
        InvalidateDetectCache();
        return await DetectCoreAsync(module, force: true).ConfigureAwait(true);
    }

    private async Task RunModuleScriptAsync(
        string module,
        bool repair,
        bool experimental,
        bool useGsync = true,
        bool preferLowestLatency = true)
    {
        var scripts = _services.Scripts;
        var runner = _services.PowerShell;
        using var log = new ModuleApplyLog(module + (repair ? "-repair" : ""));
        log.Line($"mode={(repair ? "repair" : "apply")} experimental={experimental} useGsync={useGsync} preferLowestLatency={preferLowestLatency}");

        void Report(double percent, string status)
        {
            log.Progress(percent, status);
            PostEvent("module.progress", new { module, percent, status });
        }

        var progress = new Progress<ScriptRunProgress>(pr => Report(pr.Percent, pr.Status));

        try
        {
            if (module == "internet")
            {
                var net = _services.Network;
                var strProgress = new Progress<string>(s =>
                {
                    log.Line("NET  " + s);
                    Report(-1, s);
                });
                if (repair)
                {
                    log.Line("Internet Repair starting...");
                    var (ok, msg) = await net.RepairAsync(strProgress).ConfigureAwait(true);
                    log.Line($"Internet Repair result ok={ok} msg={msg}");
                    if (!ok) throw new InvalidOperationException(msg);
                    log.Finish(true, "Internet repair ok");
                    return;
                }

                var preset = preferLowestLatency ? NetworkPreset.LowestLatency : NetworkPreset.HighestThroughput;
                log.Line($"Internet Apply preset={preset}");
                var (aok, amsg) = await net.ApplyPresetAsync(
                    preset,
                    new NetworkApplyOptions { Experimental = experimental, RestartEthernet = true },
                    strProgress).ConfigureAwait(true);
                log.Line($"Internet Apply result ok={aok} msg={amsg}");
                if (!aok) throw new InvalidOperationException(amsg);
                // Host gaming stack (MMCSS/HAGS/Game Mode) is Windows-owned — do not restamp from Internet.
                log.Finish(true, "Internet apply ok");
                return;
            }

            // ── Apply pipeline policy (repair always uses full PS kit) ──────────
            // discord / nvidia  → specialized PowerShell kits only
            // internet          → NetworkOptimizerService only (handled above)
            // riot / epic       → native C# ONLY (PS kit duplicates + broke yield)
            // windows / steam   → native C# primary; PS deep pack soft-fails if native OK
            //
            // Old hybrid always forced a full elevated PS kit after native → double
            // work, double elevation, hangs (Defender), and strip of yield Run keys.
            var supportsNative = !repair && _services.NativeApply.SupportsNativeApply(module);
            // Modules whose competitive apply is fully covered by native C#.
            var nativeComplete = module is "riot" or "epic" or "windows";
            // Steam still benefits from PS debloat depth; soft-fail if native essentials OK.
            var softFailDeepPack = module is "steam" or "windows";
            // Riot/Epic/Windows: full competitive apply is native C# (every detect row).
            // No redundant PS kit — hang sources (DISM/schtasks) are hard-timeout inside native.
            // Steam still runs PS deep pack for CEF/debloat depth (soft-fail if native OK).
            var skipDeepPack = supportsNative && (module is "riot" or "epic" or "windows");

            log.Line($"pipeline supportsNative={supportsNative} nativeComplete={nativeComplete} skipDeepPack={skipDeepPack} softFailDeep={softFailDeepPack} experimental={experimental}");

            NativeApplyResult? nativeResult = null;
            if (supportsNative)
            {
                Report(2, "Native apply (registry / files / policy)...");
                var step = 0;
                // Scale native progress: full range when no deep pack, else 2–55%.
                var nativeCap = skipDeepPack ? 92.0 : 55.0;
                var strProgress = new Progress<string>(s =>
                {
                    step++;
                    log.Line($"NATIVE  {s}");
                    var pct = Math.Min(nativeCap, 2 + step * (skipDeepPack ? 5.0 : 2.5));
                    Report(pct, s);
                });
                nativeResult = await _services.NativeApply.ApplyAsync(
                    module, experimental, strProgress, CancellationToken.None).ConfigureAwait(true);
                log.Line($"NATIVE result Ok={nativeResult.Ok} Message={nativeResult.Message} NeedsElev={nativeResult.NeedsElevation}");
                foreach (var s in nativeResult.Steps)
                    log.Step(s.Id, s.Status, s.Reason);
                if (nativeResult.ElevatedHklmOps.Count > 0)
                    log.Line("NATIVE elevOps=" + string.Join(" ; ", nativeResult.ElevatedHklmOps));

                if (!nativeResult.Ok)
                {
                    var essentialFailed = nativeResult.Steps.Any(s =>
                        s.Status == "fail" &&
                        s.Id is "startup" or "launcher-write"
                            or "cef-launcher" or "game-mode" or "game-bar"
                            or "gpu-fso" or "power-plan");
                    if (essentialFailed || nativeResult.Steps.Count == 0)
                    {
                        log.Finish(false, nativeResult.Message);
                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(nativeResult.Message)
                                ? "Native apply failed"
                                : nativeResult.Message);
                    }
                    log.Line("NATIVE non-essential gaps — continuing if deep pack allowed");
                }

                if (skipDeepPack)
                {
                    // Final host pins (safe even when Internet folklore previously wrote 0).
                    if (module is "windows")
                        RestampHostLatency(log);

                    Report(100, "Native apply complete (no redundant PowerShell kit)");
                    log.Finish(true, "native-only ok");
                    return;
                }

                Report(55, "Native done — optional deep pack (soft-fail if native OK)...");
            }

            string script;
            string workDir;
            var args = new List<string>();

            switch (module)
            {
                case "discord":
                    script = repair ? scripts.DiscordRepairScript : scripts.DiscordOptimizerScript;
                    workDir = scripts.GetDiscordRoot();
                    break;
                case "steam":
                    script = repair ? scripts.SteamRepairScript : scripts.SteamOptimizerScript;
                    workDir = scripts.GetSteamRoot();
                    break;
                case "windows":
                    script = repair ? scripts.WindowsRepairScript : scripts.WindowsOptimizerScript;
                    workDir = scripts.GetWindowsRoot();
                    break;
                case "nvidia":
                    script = repair ? scripts.NvidiaRepairScript : scripts.NvidiaOptimizerScript;
                    workDir = scripts.GetNvidiaRoot();
                    if (!repair)
                        args.Add(useGsync ? "-Gsync" : "-RawLatency");
                    break;
                case "riot":
                    script = repair ? scripts.RiotRepairScript : scripts.RiotOptimizerScript;
                    workDir = scripts.GetGameLaunchersRoot();
                    break;
                case "epic":
                    script = repair ? scripts.EpicRepairScript : scripts.EpicOptimizerScript;
                    workDir = scripts.GetGameLaunchersRoot();
                    break;
                default:
                    throw new InvalidOperationException($"Unknown module: {module}");
            }

            if (!repair && experimental)
                args.Add("-Experimental");

            log.Line($"script={script}");
            log.Line($"workDir={workDir}");
            log.Line($"args=[{string.Join(" ", args)}]");
            log.Line($"scriptExists={File.Exists(script)}");

            if (!File.Exists(script))
            {
                // Native-only modules already returned. Steam experimental without script still fails.
                if (nativeResult is { Ok: true } && softFailDeepPack)
                {
                    log.Line("Deep pack script missing — accepting native apply");
                    Report(100, "Native apply complete (deep pack script missing)");
                    log.Finish(true, "native ok; deep pack skipped");
                    return;
                }
                log.Finish(false, "Optimizer script missing");
                throw new FileNotFoundException("Optimizer script missing", script);
            }

            var deepBase = supportsNative ? 55.0 : 0.0;
            var deepSpan = supportsNative ? 40.0 : 95.0;
            var deepProgress = new Progress<ScriptRunProgress>(pr =>
            {
                if (pr.Percent < 0) Report(-1, pr.Status);
                else Report(deepBase + pr.Percent / 100.0 * deepSpan, pr.Status);
            });

            if (supportsNative)
                Report(56, "Deep pack (elevated; non-fatal if native already OK)...");
            else
                Report(5, repair ? "Repair (elevated)..." : "Apply (elevated)...");

            // Clean PC: Discord/Steam/NVIDIA kits need PowerShell 7. Internet already
            // bootstraps pwsh via NetworkOptimizerService; native-only modules skip this path.
            var needPwshBootstrap = module is "discord" or "steam" or "nvidia" or "windows";
            var result = await runner.RunAsync(
                script,
                arguments: args.ToArray(),
                elevate: true,
                progress: deepProgress,
                cancellationToken: CancellationToken.None,
                workingDirectory: workDir,
                ensureRuntime: needPwshBootstrap).ConfigureAwait(true);

            log.Line($"PS Success={result.Success} ExitCode={result.ExitCode} Summary={result.Summary}");
            log.Line($"PS LogPath={result.LogPath}");
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                log.Line($"PS ErrorMessage={result.ErrorMessage}");
            ModuleApplyLog.MirrorElevatedTransaction(module, result.LogPath, log);
            if (!string.IsNullOrWhiteSpace(result.FullOutput))
            {
                log.Line("----- PS FullOutput (truncated if huge) -----");
                var fo = result.FullOutput;
                if (fo.Length > 80_000) fo = fo[^80_000..];
                foreach (var line in fo.Split('\n'))
                    log.Line(line.TrimEnd('\r'));
            }

            if (!result.Success)
            {
                var err = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? (string.IsNullOrWhiteSpace(result.Summary) ? "Deep pack failed" : result.Summary)
                    : result.ErrorMessage!;

                // Soft-fail: native already applied the competitive stack.
                if (softFailDeepPack && nativeResult is { Ok: true })
                {
                    log.Line($"DEEP PACK soft-fail (native OK): {err}");
                    log.Step("deep-pack", "partial", err.Length > 200 ? err[..200] : err);
                    if (module is "windows" or "steam")
                        RestampHostLatency(log);
                    Report(100, "Native apply complete (deep pack partial — see log)");
                    log.Finish(true, "native ok; deep pack partial");
                    return;
                }

                // Windows integrity: retry once from bundled Scripts root.
                if (!repair && module == "windows" &&
                    err.Contains("signed script manifest", StringComparison.OrdinalIgnoreCase))
                {
                    log.Line("RETRY windows deep pack from bundled ScriptsRoot...");
                    var bundled = Path.Combine(PathHelper.ScriptsRoot, "Windows", "Exo-Windows-Run.ps1");
                    log.Line($"bundled={bundled} exists={File.Exists(bundled)}");
                    if (File.Exists(bundled))
                    {
                        var retry = await runner.RunAsync(
                            bundled,
                            arguments: args.ToArray(),
                            elevate: true,
                            progress: deepProgress,
                            cancellationToken: CancellationToken.None,
                            workingDirectory: Path.GetDirectoryName(bundled)!).ConfigureAwait(true);
                        log.Line($"RETRY Success={retry.Success} Exit={retry.ExitCode} Summary={retry.Summary}");
                        ModuleApplyLog.MirrorElevatedTransaction(module, retry.LogPath, log);
                        if (retry.Success)
                        {
                            result = retry;
                            err = null!;
                        }
                        else if (softFailDeepPack && nativeResult is { Ok: true })
                        {
                            log.Line($"RETRY soft-fail (native OK): {retry.ErrorMessage ?? retry.Summary}");
                            Report(100, "Native apply complete (deep pack retry partial)");
                            log.Finish(true, "native ok; deep pack partial");
                            return;
                        }
                        else
                        {
                            err = string.IsNullOrWhiteSpace(retry.ErrorMessage)
                                ? (retry.Summary ?? err)
                                : retry.ErrorMessage!;
                        }
                    }
                }

                if (err is not null)
                {
                    log.Finish(false, err);
                    throw new InvalidOperationException(err + Environment.NewLine + "Full log: " + log.LatestPath);
                }
            }

            // Product: zero always-on yield companions. If a deep pack ever ran for
            // riot/epic, re-run native apply to purge leftovers + restamp GPU/DSCP.
            if (!repair && (module is "riot" or "epic") && !skipDeepPack)
            {
                log.Line("Re-stamp native launcher policy (purge yield; GPU/DSCP)...");
                Report(97, "Re-stamping launcher policy...");
                try
                {
                    var restamp = LauncherNativeApply.Apply(module, experimental, new Progress<string>(s => log.Line("RESTAMP  " + s)));
                    foreach (var s in restamp.Steps.Where(x => x.Id is "yield" or "gpu-fso" or "game-dscp"))
                        log.Step(s.Id, s.Status, s.Reason);
                }
                catch (Exception ex)
                {
                    log.Line("RESTAMP failed (non-fatal): " + ex.Message);
                }
            }

            // Host latency (MMCSS / PowerThrottling) is Windows-owned — never restamp from Steam.
            if (!repair && module is "windows")
                RestampHostLatency(log);

            var doneMsg = supportsNative
                ? (result.Success ? "native + deep pack ok" : "native ok; deep pack partial")
                : "apply ok";
            Report(100, "Completed successfully");
            log.Finish(true, doneMsg);
        }
        catch (Exception ex)
        {
            log.Exception(ex, "RunModuleScriptAsync");
            log.Finish(false, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// MS-safe host latency pins. Values &lt;10 for SystemResponsiveness clamp to 20 (stock).
    /// </summary>
    private static void RestampHostLatency(ModuleApplyLog log)
    {
        try
        {
            NativeReg.TrySetDword("HKLM",
                @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling",
                "PowerThrottlingOff", 1);
            NativeReg.TrySetDword("HKLM",
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                "SystemResponsiveness", 10);
            NativeReg.TrySetDword("HKLM",
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                "NetworkThrottlingIndex", 10);
            log.Line(
                $"Host latency restamp PowerThrottlingOff=1 SystemResponsiveness={NativeReg.GetDword("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness")} NTI={NativeReg.GetDword("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex")}");
        }
        catch (Exception ex)
        {
            log.Line("Host latency restamp: " + ex.Message);
        }
    }

    private async Task<object> ApplyGameHubAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "gameId")
                     ?? GameOptimizerService.GameIdMarvelRivals;
        var gamePreset = ReadString(p, hasParams, "gamePreset")
                         ?? ReadString(p, hasParams, "preset")
                         ?? GameOptimizerService.PresetOptimized;
        var displayMode = ReadString(p, hasParams, "displayMode")
                          ?? GameOptimizerService.DisplayLeave;
        using var log = new ModuleApplyLog("games");
        void Report(double percent, string status)
        {
            log.Progress(percent, status);
            PostEvent("module.progress", new { module = "games", percent, status });
        }
        Report(5, "Applying…");
        var strProgress = new Progress<string>(s =>
        {
            log.Line(s);
            Report(-1, s);
        });
        var (ok, msg) = await _services.Games
            .ApplyAsync(gameId, gamePreset, displayMode, strProgress, CancellationToken.None)
            .ConfigureAwait(true);
        log.Line($"result ok={ok} msg={msg}");
        if (!ok)
        {
            log.Finish(false, msg);
            throw new InvalidOperationException(msg);
        }
        Report(100, "Done");
        log.Finish(true, msg);
        InvalidateDetectCache("games");
        return MapGamesHub(_services.Games.ListGames(gameId));
    }

    private async Task<object> RepairGameHubAsync(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "gameId")
                     ?? GameOptimizerService.GameIdMarvelRivals;
        using var log = new ModuleApplyLog("games-repair");
        void Report(double percent, string status)
        {
            log.Progress(percent, status);
            PostEvent("module.progress", new { module = "games", percent, status });
        }
        Report(10, "Repairing…");
        var strProgress = new Progress<string>(s =>
        {
            log.Line(s);
            Report(-1, s);
        });
        var (ok, msg) = await _services.Games
            .RepairAsync(gameId, strProgress, CancellationToken.None)
            .ConfigureAwait(true);
        log.Line($"result ok={ok} msg={msg}");
        if (!ok)
        {
            log.Finish(false, msg);
            throw new InvalidOperationException(msg);
        }
        Report(100, "Repair complete");
        log.Finish(true, msg);
        InvalidateDetectCache("games");
        return MapGamesHub(_services.Games.ListGames(gameId));
    }

    private object OpenGameInstall(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "gameId")
                     ?? GameOptimizerService.GameIdMarvelRivals;
        var (ok, msg) = _services.Games.OpenInstallPage(gameId);
        if (!ok)
            throw new InvalidOperationException(msg);
        return new { ok = true, message = msg, gameId };
    }

    private object MapGamesHub(GameOptimizerService.GamesHubSnapshot hub)
    {
        var selectedMapped = MapState("games", hub.Selected);
        return new
        {
            selectedGameId = hub.SelectedGameId,
            statusText = hub.StatusText,
            detail = hub.Detail,
            games = hub.Games.Select(g => new
            {
                id = g.Id,
                title = g.Title,
                platform = g.Platform,
                blurb = g.Blurb,
                icon = string.IsNullOrWhiteSpace(g.Icon)
                    ? $"/logos/{g.Id}.png"
                    : g.Icon,
                ready = g.Ready,
                installed = g.Installed,
                applied = g.Applied,
                activePreset = g.ActivePreset,
                statusText = g.StatusText,
                detail = g.Detail,
                installUrl = g.InstallUrl,
                installLabel = g.InstallLabel
            }).ToArray(),
            selected = selectedMapped
        };
    }

    private static string? ReadString(JsonElement p, bool has, string name)
    {
        if (!has || p.ValueKind != JsonValueKind.Object) return null;
        return p.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }

    private static bool ReadBool(JsonElement p, bool has, string name)
    {
        if (!has || p.ValueKind != JsonValueKind.Object) return false;
        return p.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;
    }
}
