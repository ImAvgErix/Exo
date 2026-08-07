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

public sealed partial class WebHostBridge

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



    /// <summary>

    /// Live cancellation sources for in-flight Apply/Repair runs, keyed by module.

    /// Safe to expose because the kits stamp applyStatus='applying' BEFORE doing the work and

    /// only promote it to 'applied' at the end, so a killed run reads as "needs Apply".

    /// </summary>

    private readonly Dictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _inFlightLock = new();



    private CancellationToken BeginRun(string key)

    {

        var cts = new CancellationTokenSource();

        lock (_inFlightLock)

        {

            // A stale entry means a previous run never reached its finally. Cancel it rather

            // than leaking the process, then take the slot.

            if (_inFlight.Remove(key, out var stale))

            {

                try { stale.Cancel(); } catch { /* already disposed */ }

                stale.Dispose();

            }

            _inFlight[key] = cts;

        }

        return cts.Token;

    }



    private void EndRun(string key)

    {

        lock (_inFlightLock)

        {

            if (_inFlight.Remove(key, out var cts)) cts.Dispose();

        }

    }



    /// <summary>Cancel an in-flight Apply/Repair. Idempotent; unknown modules are not an error.</summary>

    private object CancelRun(JsonElement p, bool hasParams)

    {

        var module = (ReadString(p, hasParams, "module") ?? string.Empty).Trim().ToLowerInvariant();

        if (module.Length == 0) return new { ok = false, message = "No module given." };



        CancellationTokenSource? cts;

        lock (_inFlightLock) { _inFlight.TryGetValue(module, out cts); }

        if (cts is null) return new { ok = false, running = false, message = "Nothing is running for that module." };



        try { cts.Cancel(); }

        catch (ObjectDisposedException) { return new { ok = false, running = false, message = "That run already finished." }; }



        PostEvent("module.progress", new { module, percent = -1.0, status = "Stopping…" });

        return new { ok = true, running = true, message = "Stopping — the module will report as needing Apply." };

    }



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

                "module.cancel" => CancelRun(paramsEl, hasParams),

                "module.verifyAll" => await VerifyAllModulesAsync().ConfigureAwait(true),
                "startup.list" => ListStartupEntries(),
                "startup.set" => SetStartupEntry(paramsEl, hasParams),
                "startup.snapshot" => SaveStartupSnapshot(),
                "services.list" => ListServices(),
                "storage.scan" => ScanStorage(),
                "storage.clean" => CleanStorage(),
                "storage.journal" => StorageJournal(),
                "diagnostics.summary" => BuildDiagnosticsSummary(),

                // Driver work is three explicit calls, never one. Check reads; prepare downloads

                // and strips but installs nothing; install needs the token prepare issued.

                "nvidia.driverCheck" => await NvidiaDriverCheckAsync().ConfigureAwait(true),

                "nvidia.driverPrepare" => await NvidiaDriverPrepareAsync(paramsEl, hasParams).ConfigureAwait(true),

                "nvidia.driverInstall" => await NvidiaDriverInstallAsync(paramsEl, hasParams).ConfigureAwait(true),

                // The sweep: check reads only, arm needs the token check produced plus a yes.

                "nvidia.sweepCheck" => NvidiaSweepCheck(),

                "nvidia.sweepArm" => NvidiaSweepArm(paramsEl, hasParams),

                // AMD / Intel CPU chipset — same three-stage consent as NVIDIA.

                "chipset.driverCheck" => await ChipsetDriverCheckAsync().ConfigureAwait(true),

                "chipset.driverPrepare" => await ChipsetDriverPrepareAsync(paramsEl, hasParams).ConfigureAwait(true),

                "chipset.driverInstall" => await ChipsetDriverInstallAsync(paramsEl, hasParams).ConfigureAwait(true),

                "chipset.openDropFolder" => OpenChipsetDropFolder(),

                "chipset.openSupport" => OpenChipsetSupport(),

                "shell.openLogs" => OpenLogsFolder(),

                "shell.openIssues" => OpenIssues(),

                "shell.openUrl" => OpenExternalUrl(paramsEl, hasParams),

                "shell.openNvidiaControlPanel" => OpenNvidiaControlPanel(),

                "shell.minimize" => MinimizeWindow(),

                "shell.toggleMaximize" => ToggleMaximizeWindow(),

                "shell.close" => CloseWindow(),

                "settings.get" => BuildSettings(),

                "settings.set" => SetSettings(paramsEl, hasParams),

                "settings.getChangelog" => BuildChangelog(),

                "settings.checkUpdates" => await CheckUpdatesAsync().ConfigureAwait(true),

                "updates.peek" => await PeekUpdatesAsync().ConfigureAwait(true),

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



    private object ToggleMaximizeWindow()

    {

        var maximized = false;

        void Go()

        {

            try

            {

                if (App.MainAppWindow?.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)

                {

                    if (p.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized)

                        p.Restore();

                    else

                        p.Maximize();

                    maximized = p.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;

                }

            }

            catch { }

        }



        if (!_queue.HasThreadAccess)

            _queue.TryEnqueue(Go);

        else

            Go();

        return new { ok = true, maximized };

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



    /// <summary>Lightweight applied/not tag for modules without DashboardViewModel properties.</summary>

    private static string TagFromNativeDetect(string module)

    {

        try

        {

            switch (module.ToLowerInvariant())

            {

                case "system":

                    return SystemNativeApply.Detect().Applied ? "VERIFIED" : "NOT APPLIED";

                case "spotify":

                    {

                        var d = NativeLiveDetect.DetectSpotify();

                        if (d.IsApplied) return "VERIFIED";

                        if (d.StatusText.Contains("not installed", StringComparison.OrdinalIgnoreCase))

                            return "MISSING";

                        return "NOT APPLIED";

                    }

                case "amd":

                    {

                        var (installed, applied, _) = AmdNativeApply.Detect();

                        if (!installed) return "MISSING";

                        return applied ? "VERIFIED" : "NOT APPLIED";

                    }

                default:

                    return "NOT APPLIED";

            }

        }

        catch { return "NOT APPLIED"; }

    }



}

