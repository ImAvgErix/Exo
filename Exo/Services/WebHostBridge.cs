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
                "shell.navigate" => null,
                "shell.settings" => RequestSettings(),
                "shell.openLogs" => OpenLogsFolder(),
                "shell.openIssues" => OpenIssues(),
                "shell.openNvidiaControlPanel" => OpenNvidiaControlPanel(),
                "shell.minimize" => MinimizeWindow(),
                "shell.close" => CloseWindow(),
                "settings.get" => BuildSettings(),
                "settings.set" => SetSettings(paramsEl, hasParams),
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

    private object BuildSettings()
    {
        var s = _services.Settings.Current;
        return new
        {
            appVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "3.7.2",
            checkForUpdatesOnLaunch = s.CheckForUpdatesOnLaunch,
            experimentalDefaults = new
            {
                discord = s.ExperimentalDiscord,
                steam = s.ExperimentalSteam,
                internet = s.ExperimentalInternet,
                nvidia = s.ExperimentalNvidia,
                riot = s.ExperimentalRiot,
                epic = s.ExperimentalEpic
            }
        };
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
        });
        return BuildSettings();
    }

    private object OpenLogsFolder()
    {
        try
        {
            var logs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Exo", "logs");
            Directory.CreateDirectory(logs);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = logs,
                UseShellExecute = true
            });
            return new { ok = true, path = logs };
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
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/ImAvgErix/Exo/issues",
                UseShellExecute = true
            });
            return new { ok = true };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
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
                    remoteVersion = check.RemoteVersion
                };
            }

            // Auto-download + apply (no "Update available" card).
            PushProgress($"Downloading Exo v{check.RemoteVersion}…", 0);
            var install = await _services.Updater
                .InstallAppUpdateAsync(check, status: status, progress: detail)
                .ConfigureAwait(true);

            if (install.ShouldExit)
            {
                PushProgress(install.Message, 100);
                // Quiet installer was started — leave so stage-swap can replace files.
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(900).ConfigureAwait(false); } catch { }
                    try
                    {
                        _queue.TryEnqueue(() =>
                        {
                            try { Microsoft.UI.Xaml.Application.Current?.Exit(); } catch { }
                        });
                    }
                    catch { }
                });
            }
            else
            {
                PushProgress(install.Message, install.UpdateAvailable ? -1 : 100);
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
                remoteVersion = install.RemoteVersion
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
            "internet" => "internet",
            "nvidia" => "nvidia",
            "riot" => "riot",
            "epic" => "epic",
            _ => "discord"
        };

    private async Task<object> DetectModuleAsync(JsonElement p, bool hasParams)
    {
        var module = ReadString(p, hasParams, "module") ?? "discord";
        return await DetectCoreAsync(module).ConfigureAwait(true);
    }

    private async Task<object> DetectCoreAsync(string module)
    {
        var ct = CancellationToken.None;
        return module.ToLowerInvariant() switch
        {
            "discord" => MapState("discord", await _services.OptimizerState.DetectDiscordAsync(ct).ConfigureAwait(true)),
            "steam" => MapState("steam", await _services.OptimizerState.DetectSteamAsync(ct).ConfigureAwait(true)),
            "nvidia" => MapState("nvidia", await _services.OptimizerState.DetectNvidiaAsync(ct).ConfigureAwait(true)),
            "riot" => MapState("riot", await _services.OptimizerState.DetectRiotAsync(ct).ConfigureAwait(true)),
            "epic" => MapState("epic", await _services.OptimizerState.DetectEpicAsync(ct).ConfigureAwait(true)),
            "internet" => await MapInternetAsync().ConfigureAwait(true),
            _ => throw new InvalidOperationException($"Unknown module: {module}")
        };
    }

    /// <summary>
    /// Internet detect surfaces the same four plain-language cards as the native
    /// InternetOptimizerViewModel (path / policy / DNS / repair), plus adapter
    /// identity from ProbeAsync when available.
    /// </summary>
    private async Task<object> MapInternetAsync()
    {
        NetworkSnapshot? snap = null;
        try
        {
            snap = await _services.Network.ProbeAsync().ConfigureAwait(true);
        }
        catch
        {
            /* probe optional — fall back to persisted state */
        }

        var savedPreset = snap?.ActivePreset ?? _services.Network.LoadSavedPreset();
        var applied = savedPreset is NetworkPreset.LowestLatency or NetworkPreset.HighestThroughput
            || !string.IsNullOrWhiteSpace(HomeDashboardReader.TryReadInternetStatus());
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

        var statusText = applied
            ? (savedPreset == NetworkPreset.HighestThroughput
                ? "High-throughput stack applied"
                : "Lowest-latency stack applied")
            : "Ready to optimize";
        var detail = applied
            ? "Stack applied. Use Apply to re-measure and reapply, or Repair to undo."
            : "Measure the live path and apply the adaptive stack for your selected profile.";

        if (snap is { ProbeOk: true, InternetPingMs: int ping })
            detail = applied
                ? $"Live ~{ping} ms · {presetLabel} stack. Reapply or Repair."
                : $"Live ~{ping} ms · ready to apply {(preferLowest ? "lowest latency" : "high throughput")}.";

        return new
        {
            id = "internet",
            isApplied = applied,
            statusText,
            detail,
            features = features.ToArray(),
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
        return new
        {
            id,
            isApplied = state.IsApplied,
            statusText = state.StatusText,
            detail = state.Detail,
            features = state.Features.Select(f => new
            {
                title = f.Title,
                detail = f.Detail,
                active = f.IsActive
            }).ToArray(),
            options = new
            {
                experimental = id switch
                {
                    "discord" => _services.Settings.Current.ExperimentalDiscord,
                    "steam" => _services.Settings.Current.ExperimentalSteam,
                    "internet" => _services.Settings.Current.ExperimentalInternet,
                    "nvidia" => _services.Settings.Current.ExperimentalNvidia,
                    "riot" => _services.Settings.Current.ExperimentalRiot,
                    "epic" => _services.Settings.Current.ExperimentalEpic,
                    _ => false
                },
                useGsync,
                preferLowestLatency = true
            }
        };
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
        // Persist experimental toggle so next open matches last Apply intent.
        try
        {
            _services.Settings.Update(s =>
            {
                switch (module)
                {
                    case "discord": s.ExperimentalDiscord = experimental; break;
                    case "steam": s.ExperimentalSteam = experimental; break;
                    case "internet": s.ExperimentalInternet = experimental; break;
                    case "nvidia": s.ExperimentalNvidia = experimental; break;
                    case "riot": s.ExperimentalRiot = experimental; break;
                    case "epic": s.ExperimentalEpic = experimental; break;
                }
            });
        }
        catch { /* non-fatal */ }

        await RunModuleScriptAsync(module, repair: false, experimental, useGsync, preferLowestLatency)
            .ConfigureAwait(true);
        return await DetectCoreAsync(module).ConfigureAwait(true);
    }

    private async Task<object> RepairModuleAsync(JsonElement p, bool hasParams)
    {
        var module = (ReadString(p, hasParams, "module") ?? "discord").ToLowerInvariant();
        await RunModuleScriptAsync(module, repair: true, experimental: false).ConfigureAwait(true);
        return await DetectCoreAsync(module).ConfigureAwait(true);
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

        void Report(double percent, string status) =>
            PostEvent("module.progress", new { module, percent, status });

        var progress = new Progress<ScriptRunProgress>(pr => Report(pr.Percent, pr.Status));

        if (module == "internet")
        {
            var net = _services.Network;
            var strProgress = new Progress<string>(s => Report(-1, s));
            if (repair)
            {
                var (ok, msg) = await net.RepairAsync(strProgress).ConfigureAwait(true);
                if (!ok) throw new InvalidOperationException(msg);
                return;
            }

            var preset = preferLowestLatency ? NetworkPreset.LowestLatency : NetworkPreset.HighestThroughput;
            var (aok, amsg) = await net.ApplyPresetAsync(
                preset,
                new NetworkApplyOptions { Experimental = experimental, RestartEthernet = true },
                strProgress).ConfigureAwait(true);
            if (!aok) throw new InvalidOperationException(amsg);
            return;
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

        if (!File.Exists(script))
            throw new FileNotFoundException("Optimizer script missing", script);

        var result = await runner.RunAsync(
            script,
            arguments: args.ToArray(),
            elevate: true,
            progress: progress,
            cancellationToken: CancellationToken.None,
            workingDirectory: workDir).ConfigureAwait(true);

        if (!result.Success)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? (string.IsNullOrWhiteSpace(result.Summary) ? "Apply failed" : result.Summary)
                    : result.ErrorMessage!);
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
