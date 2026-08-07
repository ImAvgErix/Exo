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

    private static object Row(string id, string title, string tag) =>

        new

        {

            id,

            title,

            applied = string.Equals(tag, "VERIFIED", StringComparison.OrdinalIgnoreCase)

                || string.Equals(tag, "APPLIED", StringComparison.OrdinalIgnoreCase),

            // The shell shows four states, not a boolean. Derived here so the tag vocabulary

            // stays in one place: a UI re-deriving "is this missing" from title text would go

            // stale the moment a detect starts phrasing itself differently.

            state = RowState(tag)

        };



    /// <summary>

    /// Dashboard tag → the shell's four words. MEASURED counts as applied (Internet reports it

    /// after a verified apply); DRIFTED and NEEDS ATTENTION are the "applied but the machine

    /// moved" cases the shell shows as STUCK so they can be retried.

    /// </summary>

    private static string RowState(string tag) => tag.ToUpperInvariant() switch

    {

        "MISSING" => "missing",

        "VERIFIED" or "APPLIED" or "MEASURED" => "applied",

        "DRIFTED" or "NEEDS ATTENTION" => "blocked",

        _ => "ready"

    };



    private static string MapNextId(string label) =>

        label.ToLowerInvariant() switch

        {

            "discord" => "discord",

            "brave" => "brave",

            "system" => "system",

            "spotify" => "spotify",

            "amd" => "amd",

            "steam" => "steam",

            "internet" => "internet",

            "nvidia" => "nvidia",

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

            "brave" => MapState("brave", await _services.OptimizerState.DetectBraveAsync(ct).ConfigureAwait(true)),

            "system" => MapState("system", await _services.OptimizerState.DetectSystemAsync(ct).ConfigureAwait(true)),

            "spotify" => MapState("spotify", await _services.OptimizerState.DetectSpotifyAsync(ct).ConfigureAwait(true)),

            "amd" => MapState("amd", await _services.OptimizerState.DetectAmdAsync(ct).ConfigureAwait(true)),

            "steam" => MapState("steam", await _services.OptimizerState.DetectSteamAsync(ct, fastOnly: false).ConfigureAwait(true)),

            "nvidia" => MapState("nvidia", await _services.OptimizerState.DetectNvidiaAsync(ct, fastOnly: false).ConfigureAwait(true)),

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

        // Only competitive presets + live MatchesPreset can green. Saved preset alone is not verified.

        var applied = false;

        if (savedPreset is NetworkPreset.LowestLatency or NetworkPreset.HighestThroughput)

        {

            if (snap is not null)

                applied = _services.Network.MatchesPreset(snap, savedPreset);

            else

                applied = false; // no live probe → not verified

        }

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

                // "partial" counts as an issue, not as silence. The network apply reports it

                // when the levers were written but could not all be confirmed live, which is

                // precisely a step the user should be told did not fully take.

                var fails = report.Where(r =>

                        string.Equals(r.Status, "fail", StringComparison.OrdinalIgnoreCase)

                        || string.Equals(r.Status, "partial", StringComparison.OrdinalIgnoreCase))

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



        // Same staleness rule as every other module. Internet is where this was noticed: its

        // tweak set changed and a machine already reading "applied" was never re-offered.

        var statusReason = "";

        if (statusKind == "applied" && ModuleTweakVersion.IsStale("internet"))

        {

            statusKind = "partial";

            statusText = "Applied, but Exo's network tuning changed since";

            statusReason = "superseded";

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

            statusReason,

            detail,

            features = features.ToArray(),

            applyReport = applyReport.ToArray(),

            options = new

            {

                experimental = _services.Settings.Current.ExperimentalInternet,

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



        // Classification lives in ModuleStatusClassifier so it can be driven directly by

        // Contracts.Smoke with made-up machines. It used to be inline here, unreachable by

        // any test, which is how three modules shipped invisible to the UI.

        var classified = ModuleStatusClassifier.Classify(

            id,

            state.IsApplied,

            state.StatusText,

            state.Detail,

            state.Features.Select(f => new ModuleStatusClassifier.Row(f.Title, f.IsActive)).ToList());

        var statusKind = classified.Kind;

        var statusText = classified.Text;



        // Applied with WHICH tweaks? A module whose tweak set moved on since it was last run

        // is not done, however green its live checks look - the machine is sitting on a

        // superseded configuration and the orb would otherwise call the whole rig good to go.

        //

        // statusReason carries WHY it is partial. Both causes arrive here as the same

        // statusKind, and the orb derives its whole question from that kind alone - so a module

        // downgraded because Exo's own tuning changed was described to the user as

        // "something undid part of NVIDIA's tuning", which blames the machine for a change Exo

        // made. Different causes deserve different sentences.

        var statusReason = "";

        if (statusKind == "applied" && ModuleTweakVersion.IsStale(id))

        {

            statusKind = "partial";

            // "Changed since" and "never recorded" are different facts. A module that predates

            // the stamp file has not had its tuning changed at all, and saying so is simply

            // untrue - this machine has stamps for three of eight modules, so five were being

            // told something about themselves that had not happened.

            if (ModuleTweakVersion.HasNoStamp(id))

            {

                statusText = "Applied, but I have no record of which version";

                statusReason = "unstamped";

            }

            else

            {

                statusText = "Applied, but Exo's tuning changed since";

                statusReason = "superseded";

            }

        }

        var visibleOn = state.Features.Count(f => f.IsActive);

        var visibleTotal = state.Features.Count;

        var off = state.Features

            .Where(f => !ModuleStatusClassifier.IsInfoTitle(f.Title) && !f.IsActive)

            .Select(f => f.Title ?? "")

            .ToList();



        // Honest applied flag for UI — for games, trust host after Apply

        var isApplied = statusKind == "applied";



        // applyReport lives in *-optimizer.json (aliases for games/network)

        var reportId = id switch

        {

            "internet" => "network",

            _ => id

        };

        // game-optimizer.json (not games-); TryReadApplyReport uses "{module}-optimizer.json"

        if (reportId == "game")

        {

            // Prefer game-optimizer.json; also try games-

        }

        var applyReport = OptimizerStateService.TryReadApplyReport(reportId);

        if (applyReport.Count == 0 && id == "internet")

        {

            // network-optimizer may not use applyReport array — leave empty

        }



        // Apply reports are the host's durable record of work that ran but did

        // not fully verify. Keep that visible as Partial even when a module's

        // live detector has no row for the missing step.

        var reportIssues = applyReport

            .Where(line => line.Contains("|fail", StringComparison.OrdinalIgnoreCase)

                           || line.Contains("|partial", StringComparison.OrdinalIgnoreCase))

            .ToList();

        var explicitFailed = state.Extra is not null

                             && state.Extra.TryGetValue("applyStatus", out var failedStatus)

                             && string.Equals(failedStatus, "failed", StringComparison.OrdinalIgnoreCase);

        var explicitPartial = state.Extra is not null

                              && state.Extra.TryGetValue("applyStatus", out var persistedStatus)

                              && string.Equals(persistedStatus, "partial", StringComparison.OrdinalIgnoreCase);

        if (explicitFailed)

        {

            statusKind = "failed";

            statusText = "Apply failed";

            statusReason = "apply-state";

        }

        else if (explicitPartial || reportIssues.Count > 0)

        {

            statusKind = "partial";

            statusText = explicitPartial

                ? "Partially applied"

                : $"Partial · {reportIssues.Count} apply step(s) need review";

            statusReason = explicitPartial ? "apply-state" : "apply-report";

        }

        else if (state.Extra is not null

                 && state.Extra.TryGetValue("applyStatus", out var appliedStatus)

                 && string.Equals(appliedStatus, "applied", StringComparison.OrdinalIgnoreCase)

                 && statusKind is "partial" or "ready")

        {

            // Host recorded a full Apply. Soft detect rows (Base-only DRS status, etc.)

            // must not demote a clean applyStatus=applied back to Partial.

            statusKind = "applied";

            statusText = string.IsNullOrWhiteSpace(state.StatusText) || state.StatusText.Contains("Partial", StringComparison.OrdinalIgnoreCase)

                ? "Applied"

                : state.StatusText;

            statusReason = "apply-state";

        }



        // Keep the boolean and the visible status coupled. A durable partial/failure must

        // never leave the UI's Apply/Repair logic believing the module is green just because

        // an older live row or marker still says applied.

        if (statusKind is not ("applied" or "repaired"))

            isApplied = false;



        return new

        {

            id,

            isApplied,

            statusKind,

            statusText,

            statusReason,

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

                    "internet" => _services.Settings.Current.ExperimentalInternet,

                    "nvidia" => _services.Settings.Current.ExperimentalNvidia,

                    _ => false

                },

                useGsync,

                preferLowestLatency = true

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

            "discord", "brave", "steam", "internet", "nvidia", "system", "spotify", "amd"

        };

        var results = new List<object>();

        var applied = 0;

        var partial = 0;

        var ready = 0;

        var missing = 0;

        var failed = 0;



        for (var i = 0; i < modules.Length; i++)

        {

            var m = modules[i];

            PostEvent("settings.verifyProgress", new

            {

                module = m,

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

                failed++;

            }

        }



        PostEvent("settings.verifyProgress", new { percent = 100.0, status = "Verify complete" });

        InvalidateDetectCache();

        return new

        {

            results,

            summary = $"{applied} applied · {partial} partial · {ready} ready · {missing} missing · {failed} failed",

            applied,

            partial,

            ready,

            missing,

            failed

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

        // Absent is not false. ReadBool returns false for a missing key, and the shipped orb

        // calls host.apply(module) with no options at all — so every Apply wrote

        // experimental=false over whatever the user had saved. settings.json on a real machine

        // here holds experimentalSteam and experimentalInternet as true; a single Apply would

        // have silently cleared both, with nothing in the UI to say so or to turn them back on.

        // The two options immediately below already distinguish absent from false; this now

        // matches them, seeding from the saved value and only persisting when the caller

        // actually said something.

        var experimentalPresent = hasParams && p.ValueKind == JsonValueKind.Object

                                  && p.TryGetProperty("experimental", out _);

        var savedSettings = _services.Settings.Current;

        var experimental = experimentalPresent

            ? ReadBool(p, hasParams, "experimental")

            : module switch

            {

                "discord" => savedSettings.ExperimentalDiscord,

                "steam" => savedSettings.ExperimentalSteam,

                "internet" => savedSettings.ExperimentalInternet,

                "nvidia" => savedSettings.ExperimentalNvidia,

                _ => false

            };

        // Defaults when omitted: Max-FPS NVIDIA pack (G-SYNC false) and lowest-latency Internet.

        // G-SYNC used to default true and silently cap frames on competitive machines that never

        // answered the orb's mode question (API / older UI / missing key).

        var useGsync = false;

        var preferLowestLatency = true;

        if (hasParams && p.TryGetProperty("useGsync", out var gs))

            useGsync = gs.ValueKind == JsonValueKind.True

                       || (gs.ValueKind == JsonValueKind.String

                           && (gs.GetString() is "true" or "1"));

        if (hasParams && p.TryGetProperty("preferLowestLatency", out var ll))

            preferLowestLatency = ll.ValueKind != JsonValueKind.False

                                  && !(ll.ValueKind == JsonValueKind.String

                                       && (ll.GetString() is "false" or "0"));

        // Persist the experimental toggle so the next open matches the last Apply intent — but

        // only when this Apply actually carried one. Writing on every Apply is what turned a

        // missing key into a destructive default.

        if (experimentalPresent)

        {

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

                    }

                });

            }

            catch { /* non-fatal */ }

        }



        try

        {



            var applyToken = BeginRun(module);

            try

            {

                await RunModuleScriptAsync(module, repair: false, experimental, useGsync, preferLowestLatency, applyToken)

                    .ConfigureAwait(true);

            }

            finally { EndRun(module); }

        }

        catch (OperationCanceledException)

        {

            // Stopped on purpose, so this is not a failure to apologise for. Report the machine

            // as it now is: the kits stamp applyStatus='applying' before doing the work and only

            // promote it to 'applied' at the end, so detect reads the interrupted run as needing

            // Apply, which is exactly what it needs. No tweak-version stamp — nothing completed.

            InvalidateDetectCache();

            return await DetectCoreAsync(module, force: true).ConfigureAwait(true);

        }

        catch (Exception ex)

        {

            // Always point at the detailed log so Riot/Windows failures are actionable.

            // exo-apply.log, which is the file that exists. This pointed at

            // apply-{module}-latest.log — a name ModuleApplyLog actively DELETES on

            // construction (PruneLegacyFiles sweeps apply-*.log) because the per-module files

            // were consolidated into one. So File.Exists was always false and the hint was

            // always dropped: every failure the wrapper caught reached the user with no log

            // path at all, which is most of them. No existence test now either; the

            // ModuleApplyLog constructor creates this file unconditionally.

            var latest = Path.Combine(PathHelper.LogsDir, "exo-apply.log");

            var hint = $"{ex.Message}{Environment.NewLine}Full log: {latest}";

            throw new InvalidOperationException(hint, ex);

        }

        // Cross-module side effects (Windows Game Bar, yield companions, DSCP, …)

        InvalidateDetectCache();

        var detected = await DetectCoreAsync(module, force: true).ConfigureAwait(true);



        // Record WHICH tweak set just went on, but only if the machine agrees it landed.

        // Stamping an apply that came back "ready" would mark a superseded config as current.

        if (KindOf(detected) is "applied" or "partial")

        {

            var wasStale = ModuleTweakVersion.IsStale(module);

            ModuleTweakVersion.Stamp(module);



            // MapState downgrades "applied" to "partial - Applied, but Exo's tuning changed

            // since" whenever the stamp is stale, and the detect above ran while it still was:

            // the stamp for the apply that just succeeded is written on the line above it. So a

            // module's first successful Apply after a version bump handed the UI a payload

            // announcing that its tuning was already out of date, and the orb offered to redo

            // the thing it had just finished. Re-read once, now that the stamp is current.

            if (wasStale)

            {

                InvalidateDetectCache();

                detected = await DetectCoreAsync(module, force: true).ConfigureAwait(true);

            }

        }

        return detected;

    }



    /// <summary>Reads statusKind off an anonymous detect payload without re-running detect.</summary>

    private static string KindOf(object? detectResult)

    {

        var prop = detectResult?.GetType().GetProperty("statusKind");

        return prop?.GetValue(detectResult) as string ?? "";

    }



    /// <summary>

    /// After a Steam deep-pack soft-fail: mark the state file so we never claim a full apply

    /// from native essentials alone. Detect and the orb read applyReport for partial steps.

    /// </summary>

    private static void AnnotateSteamDeepPackPartial(string err)

    {

        try

        {

            var path = Path.Combine(PathHelper.AppDataDir, SteamNativeApply.StateFileName);

            if (!File.Exists(path)) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            var root = doc.RootElement;

            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in root.EnumerateObject())

            {

                dict[p.Name] = p.Value.ValueKind switch

                {

                    JsonValueKind.True => true,

                    JsonValueKind.False => false,

                    JsonValueKind.Number => p.Value.TryGetInt64(out var n) ? n : p.Value.GetDouble(),

                    JsonValueKind.String => p.Value.GetString(),

                    JsonValueKind.Array => p.Value.EnumerateArray()

                        .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())

                        .ToList(),

                    JsonValueKind.Object => p.Value.GetRawText(),

                    _ => null

                };

            }



            var shortErr = err.Length > 180 ? err[..180] : err;

            var report = new List<string>();

            if (dict.TryGetValue("applyReport", out var ar) && ar is List<string> existing)

                report.AddRange(existing);

            else if (dict.TryGetValue("applyReport", out var arObj) && arObj is List<object?> objs)

                report.AddRange(objs.Select(o => o?.ToString() ?? "").Where(s => s.Length > 0));

            report.RemoveAll(l => l.StartsWith("deep-pack|", StringComparison.OrdinalIgnoreCase));

            report.Add($"deep-pack|partial:{shortErr.Replace("|", "/", StringComparison.Ordinal)}");

            dict["applyReport"] = report;

            dict["fullApply"] = false;

            dict["cacheCleanupCompleted"] = false;

            // Drop deep-pack-only verify flags so Test-SteamApplyRecord fails closed.

            dict.Remove("shaderInventoryVerified");

            dict.Remove("installedShaderCachesPreserved");

            dict["deepPackOk"] = false;

            dict["deepPackError"] = shortErr;



            File.WriteAllText(path, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));

        }

        catch

        {

            // Annotation is best-effort; native essentials already landed.

        }

    }



    private async Task<object> RepairModuleAsync(JsonElement p, bool hasParams)

    {

        var module = (ReadString(p, hasParams, "module") ?? "discord").ToLowerInvariant();

        try

        {



            var repairToken = BeginRun(module);

            try

            {

                await RunModuleScriptAsync(module, repair: true, experimental: false, cancellationToken: repairToken)

                    .ConfigureAwait(true);

            }

            finally { EndRun(module); }

        }

        catch (OperationCanceledException)

        {

            // A stopped Repair leaves the machine part-restored, which detect will report

            // honestly. Do not dress it up as either success or failure.

            InvalidateDetectCache();

            return await DetectCoreAsync(module, force: true).ConfigureAwait(true);

        }

        catch (Exception ex)

        {

            // exo-apply.log, which is the file that exists. This pointed at

            // apply-{module}-latest.log — a name ModuleApplyLog actively DELETES on

            // construction (PruneLegacyFiles sweeps apply-*.log) because the per-module files

            // were consolidated into one. So File.Exists was always false and the hint was

            // always dropped: every failure the wrapper caught reached the user with no log

            // path at all, which is most of them. No existence test now either; the

            // ModuleApplyLog constructor creates this file unconditionally.

            var latest = Path.Combine(PathHelper.LogsDir, "exo-apply.log");

            var hint = $"{ex.Message}{Environment.NewLine}Full log: {latest}";

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

        bool preferLowestLatency = true,

        CancellationToken cancellationToken = default)

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

                // Every await below takes the token. They all accepted one from the start and

                // this branch passed none, so Stop did nothing here while CancelRun still

                // answered running=true and the orb committed to "Stopping…" — a 60-120s freeze

                // ending in a module that had fully applied being announced as stopped.

                if (repair)

                {

                    log.Line("Internet Repair starting...");

                    var (ok, msg) = await net.RepairAsync(strProgress, cancellationToken).ConfigureAwait(true);

                    log.Line($"Internet Repair result ok={ok} msg={msg}");

                    if (!ok) throw new InvalidOperationException(msg);

                    log.Finish(true, "Internet repair ok");

                    return;

                }



                // Analyze first — home dashboard Load ↓/↑, Loss, and full Rating come from

                // qualityBenchmark in network-optimizer.json. Without this step (old UI path

                // only applied TCP/DNS), friends only saw idle ping + DNS + a soft "Good".

                Report(4, "Analyzing connection quality…");

                log.Line("Internet quality benchmark starting...");

                var snap = await net.ProbeAsync(cancellationToken).ConfigureAwait(true);

                var quality = await net.RunQualityBenchmarkAsync(snap.Media, strProgress, cancellationToken)

                    .ConfigureAwait(true);

                if (quality is not { Ok: true, IsQualityTest: true })

                {

                    throw new InvalidOperationException(

                        "Connection quality test could not finish (need a working internet path). " +

                        "No network settings were changed. Try again on Ethernet or check firewall/VPN.");

                }

                net.PersistQualityBenchmark(quality);

                log.Line(

                    $"Quality ok idle={quality.PingP50Ms:0.#}ms load↓={quality.DownloadLoadedMs:0.#} " +

                    $"load↑={quality.UploadLoadedMs:0.#} loss={quality.PacketLossPercent:0.##}% " +

                    $"dns={quality.DnsProvider} down={quality.DownloadMbps:0}Mbps up={quality.UploadMbps:0}Mbps");

                Report(35, $"Quality: {quality.PingP50Ms:0.#} ms idle · {quality.DnsProvider} DNS");



                var preset = preferLowestLatency ? NetworkPreset.LowestLatency : NetworkPreset.HighestThroughput;

                log.Line($"Internet Apply preset={preset}");

                Report(40, $"Applying {preset} stack…");

                // Bake Analyze DNS winner into Apply — defaults alone left Cloudflare even when

                // the quality probe picked Google/Quad9.

                var netOpts = new NetworkApplyOptions

                {

                    Experimental = experimental,

                    RestartEthernet = true,

                    DnsProvider = string.IsNullOrWhiteSpace(quality.DnsProvider) ? "Cloudflare" : quality.DnsProvider,

                    DnsPrimary = string.IsNullOrWhiteSpace(quality.DnsPrimary) ? "1.1.1.1" : quality.DnsPrimary,

                    DnsSecondary = string.IsNullOrWhiteSpace(quality.DnsSecondary) ? "1.0.0.1" : quality.DnsSecondary,

                    DnsPrimaryV6 = string.IsNullOrWhiteSpace(quality.DnsPrimaryV6)

                        ? "2606:4700:4700::1111"

                        : quality.DnsPrimaryV6,

                    DnsSecondaryV6 = string.IsNullOrWhiteSpace(quality.DnsSecondaryV6)

                        ? "2606:4700:4700::1001"

                        : quality.DnsSecondaryV6,

                    DnsOverHttpsTemplate = string.IsNullOrWhiteSpace(quality.DnsOverHttpsTemplate)

                        ? "https://cloudflare-dns.com/dns-query"

                        : quality.DnsOverHttpsTemplate,

                };



                var (aok, amsg) = await net.ApplyPresetAsync(

                    preset,

                    netOpts,

                    strProgress,

                    cancellationToken).ConfigureAwait(true);

                log.Line($"Internet Apply result ok={aok} msg={amsg}");

                if (!aok) throw new InvalidOperationException(amsg);

                // Host gaming stack (MMCSS/HAGS/Game Mode) is Windows-owned — do not restamp from Internet.

                log.Finish(true, "Internet analyze + apply ok");

                return;

            }



            // Brave — native only for Apply + Repair (no PS kit).

            if (module == "brave")

            {

                Report(2, repair ? "Repairing Brave policies…" : "Brave absolute debloat…");

                var step = 0;

                var strProg = new Progress<string>(s =>

                {

                    step++;

                    log.Line("NATIVE  " + s);

                    Report(Math.Min(95, 2 + step * 8.0), s);

                });

                var braveResult = repair

                    ? await Task.Run(() => BraveNativeApply.Repair(strProg), cancellationToken).ConfigureAwait(true)

                    : await _services.NativeApply.ApplyAsync("brave", experimental, strProg, cancellationToken)

                        .ConfigureAwait(true);

                foreach (var s in braveResult.Steps)

                    log.Step(s.Id, s.Status, s.Reason);



                // Brave Repair can leave machine-wide policies behind when it runs unelevated,

                // and it now hands those back as ops instead of dropping them. Drain them the

                // same way the system/spotify branch does, or Repair "succeeds" while Brave is

                // still locked down by the policies it claims to have removed.

                if (braveResult.ElevatedHklmOps is { Count: > 0 })

                {

                    Report(90, "Removing machine-wide Brave policies (Administrator)…");

                    var elev = await _services.NativeApply

                        .RunElevatedOpsAsync(braveResult.ElevatedHklmOps, strProg, cancellationToken)

                        .ConfigureAwait(true);

                    log.Step("brave-elevated-hklm", elev.Ok ? "ok" : "fail", elev.Message);

                    if (!elev.Ok)

                    {

                        log.Finish(false, elev.Message);

                        throw new InvalidOperationException(

                            "Brave's machine-wide policies still need removing: " + elev.Message);

                    }

                }



                // The "&& !repair" was hiding a failed repair. A repair that did not repair is

                // exactly as much a failure as an apply that did not apply.

                if (!braveResult.Ok)

                {

                    log.Finish(false, braveResult.Message);

                    throw new InvalidOperationException(braveResult.Message);

                }

                if (!repair)

                {

                    var live = NativeLiveDetect.DetectBrave();

                    if (!live.IsApplied)

                    {

                        var off = (live.Features ?? Array.Empty<OptimizerFeatureInfo>())

                            .Where(f => !f.IsActive && !ModuleStatusClassifier.IsInfoTitle(f.Title))

                            .Select(f => f.Title).Take(8).ToList();

                        var detail = off.Count > 0 ? string.Join(", ", off) : "Brave settings did not read back";

                        log.Finish(false, "live detect failed: " + detail);

                        throw new InvalidOperationException(

                            $"Brave changes ran but live verification failed ({detail}). Full log: {log.LatestPath}");

                    }

                }

                Report(100, repair ? "Brave repair complete" : "Brave optimized");

                log.Finish(true, braveResult.Message);

                return;

            }



            // System and Spotify — native C# only, same shape as Brave above. Neither has a

            // PowerShell kit; the elevated half of System's work rides the shared native

            // reg/powercfg batch, so both still cost exactly one UAC prompt.

            // AMD: chipset-only machines (Ryzen + NVIDIA) get an NVCleanstall-style silent

            // chipset clean install. Radeon GPUs still use the reversible debloat path only.

            if (module == "amd")

            {

                var inv = HardwareInventory.Read();

                var chipsetOnly = inv.Cpu?.Vendor == HardwareInventory.CpuVendor.Amd && !inv.HasAmdGpu;

                if (chipsetOnly && !repair)

                {

                    Report(5, "AMD chipset clean install (silent)…");

                    var step = 0;

                    var chipProg = new Progress<string>(s =>

                    {

                        step++;

                        log.Line("CHIPSET  " + s);

                        Report(Math.Min(95, 5 + step * 4.0), s);

                    });

                    var (chipOk, chipMsg) = await ChipsetDriverInstaller

                        .RunCleanAmdInstallAsync(chipProg, cancellationToken)

                        .ConfigureAwait(true);

                    log.Line(chipMsg);

                    if (!chipOk)

                    {

                        log.Finish(false, chipMsg);

                        throw new InvalidOperationException(chipMsg);

                    }

                    Report(100, chipMsg);

                    log.Finish(true, chipMsg);

                    return;

                }

                Report(2, repair ? "Putting Radeon settings back…" : "Radeon debloat…");

                var stepR = 0;

                var strProg = new Progress<string>(s =>

                {

                    stepR++;

                    log.Line("NATIVE  " + s);

                    Report(Math.Min(95, 2 + stepR * 18.0), s);

                });

                var amdResult = repair

                    ? await Task.Run(() => AmdNativeApply.Repair(strProg), cancellationToken).ConfigureAwait(true)

                    : await _services.NativeApply.ApplyAsync(

                        "amd", experimental: false, progress: strProg, ct: cancellationToken)

                        .ConfigureAwait(true);



                if (repair && amdResult.ElevatedHklmOps.Count > 0)

                {

                    Report(90, "Restoring Radeon machine settings (Administrator)…");

                    var elevated = await _services.NativeApply

                        .RunElevatedOpsAsync(amdResult.ElevatedHklmOps, strProg, cancellationToken)

                        .ConfigureAwait(true);

                    amdResult.Steps.Add(new NativeApplyStep

                    {

                        Id = "elevated-restore",

                        Status = elevated.Ok ? "ok" : "fail",

                        Reason = elevated.Message

                    });

                    foreach (var pending in amdResult.Steps.Where(s => s.Status == "pending-elev").ToList())

                    {

                        var index = amdResult.Steps.IndexOf(pending);

                        amdResult.Steps[index] = new NativeApplyStep

                        {

                            Id = pending.Id,

                            Status = elevated.Ok ? "ok" : "fail",

                            Reason = elevated.Ok ? pending.Reason : elevated.Message

                        };

                    }

                    if (!elevated.Ok)

                    {

                        log.Finish(false, elevated.Message);

                        throw new InvalidOperationException(

                            "Radeon was only partly restored — " + elevated.Message);

                    }

                }

                foreach (var s in amdResult.Steps)

                    log.Step(s.Id, s.Status, s.Reason);

                if (!amdResult.Ok)

                {

                    log.Finish(false, amdResult.Message);

                    throw new InvalidOperationException(amdResult.Message);

                }



                if (!repair)

                {

                    var (_, liveApplied, liveRows) = AmdNativeApply.Detect();

                    if (!liveApplied)

                    {

                        var off = liveRows.Where(r => !r.Item3).Select(r => r.Item1).ToList();

                        var detail = off.Count > 0 ? string.Join(", ", off) : "Radeon settings did not read back";

                        log.Finish(false, "live detect failed: " + detail);

                        throw new InvalidOperationException(

                            $"Radeon changes ran but live verification failed ({detail}). Full log: {log.LatestPath}");

                    }

                }

                Report(100, repair ? "Radeon restored" : "Radeon debloated");

                log.Finish(true, amdResult.Message);

                return;

            }



            if (module is "system" or "spotify")

            {

                var friendly = module == "system" ? "machine" : "Spotify";

                Report(2, repair ? $"Restoring {friendly} settings…" : $"Tuning {friendly}…");

                var step = 0;

                var strProg = new Progress<string>(s =>

                {

                    step++;

                    log.Line("NATIVE  " + s);

                    Report(Math.Min(95, 2 + step * 8.0), s);

                });



                NativeApplyResult nativeOutcome;

                if (repair)

                {

                    nativeOutcome = module == "system"

                        ? await Task.Run(() => SystemNativeApply.Repair(strProg), cancellationToken).ConfigureAwait(true)

                        : await Task.Run(() => SpotifyNativeApply.Repair(strProg), cancellationToken).ConfigureAwait(true);



                    // Repair stages its restores into the same elevated batch Apply uses, so

                    // they have to actually be run. Returning here without draining them would

                    // report a successful undo that never touched the machine.

                    if (nativeOutcome.ElevatedHklmOps.Count > 0)

                    {

                        var elevated = await _services.NativeApply

                            .RunElevatedOpsAsync(nativeOutcome.ElevatedHklmOps, strProg, cancellationToken)

                            .ConfigureAwait(true);

                        nativeOutcome.Steps.Add(new NativeApplyStep

                        {

                            Id = "elevated-restore",

                            Status = elevated.Ok ? "ok" : "fail",

                            Reason = elevated.Message

                        });



                        // The comment above says draining the batch matters — and then nothing

                        // read the outcome. `elevated.Ok` was recorded on a step and never

                        // branched on, so a restore whose elevated half failed (or whose UAC

                        // prompt was declined) still reported "Machine settings restored to what

                        // they were before Exo changed them." at 100%. That is the single worst

                        // thing Repair can say untruthfully: the user stops looking.

                        if (!elevated.Ok)

                        {

                            var restoreErr = string.IsNullOrWhiteSpace(elevated.Message)

                                ? "The Administrator step did not complete, so part of the restore was not applied."

                                : elevated.Message;

                            // A half-drained batch leaves real state behind — the Exo power plan

                            // can still be the active one — so say that rather than implying the

                            // machine is untouched.

                            log.Finish(false, restoreErr);

                            throw new InvalidOperationException(

                                $"{friendly} was only partly restored — {restoreErr} " +

                                $"Full log: {log.LatestPath}");

                        }

                    }

                }

                else

                {

                    nativeOutcome = await _services.NativeApply

                        .ApplyAsync(module, experimental, strProg, cancellationToken)

                        .ConfigureAwait(true);

                }



                foreach (var s in nativeOutcome.Steps)

                    log.Step(s.Id, s.Status, s.Reason);

                // `&& !repair` used to be here, which meant a failed native repair could never

                // be reported as a failure. Every Ok=false a repair can produce (SystemNativeApply,

                // SpotifyNativeApply) is a genuine user-facing error — a missing snapshot, a

                // refused write — so there is nothing benign being surfaced by dropping it.

                if (!nativeOutcome.Ok)

                {

                    log.Finish(false, nativeOutcome.Message);

                    throw new InvalidOperationException(nativeOutcome.Message);

                }



                // NativeApplyResult.Ok is init-only and is computed before elevation runs, so it

                // cannot speak for the elevated batch — but the steps recorded what happened, and

                // nothing was reading them. For the system module the elevated batch IS the work:

                // decline the UAC prompt and no power plan is created and not one HKLM lever is

                // written, yet this reported 100% "machine optimized" and wrote RESULT OK.

                var elevStep = nativeOutcome.Steps.LastOrDefault(s =>

                    (s.Id is "elevated-hklm" or "elevated-restore") && s.Status != "ok");

                if (elevStep is not null)

                {

                    var verb = repair ? "restore" : "tuning";

                    var reason = string.IsNullOrWhiteSpace(elevStep.Reason)

                        ? $"the Administrator step did not complete, so the elevated half of the {verb} was not applied"

                        : elevStep.Reason;



                    // "skip" is the user declining the prompt — their call, not a crash. "fail" is

                    // the batch genuinely erroring, which is the same class as the !Ok path above.

                    if (elevStep.Status == "fail" && !repair)

                    {

                        log.Finish(false, reason);

                        throw new InvalidOperationException($"{friendly}: {reason}");

                    }



                    Report(100, $"{friendly} partly {(repair ? "restored" : "tuned")} — Administrator step not completed");

                    log.Finish(false, reason);

                    return;

                }



                // Post-apply live re-detect for system: never claim optimized without reading back.

                if (!repair && module == "system")

                {

                    var (liveApplied, liveRows) = SystemNativeApply.Detect();

                    if (!liveApplied)

                    {

                        var off = liveRows.Where(r => !r.Item3 && !ModuleStatusClassifier.IsInfoTitle(r.Item1))

                            .Select(r => r.Item1).Take(6).ToList();

                        var detail = off.Count > 0

                            ? string.Join(", ", off)

                            : "power plan or registry levers did not stick";

                        log.Finish(false, "live detect failed: " + detail);

                        throw new InvalidOperationException(

                            $"Machine tuning wrote settings but live re-check failed ({detail}). Full log: {log.LatestPath}");

                    }

                }



                if (!repair && module == "spotify")

                {

                    var (_, spotApplied, _) = SpotifyNativeApply.Detect();

                    if (!spotApplied)

                    {

                        log.Finish(false, "spotify live detect failed after apply");

                        throw new InvalidOperationException(

                            $"Spotify was written but live re-check failed. Full log: {log.LatestPath}");

                    }

                }



                Report(100, repair ? $"{friendly} restored" : $"{friendly} optimized");

                log.Finish(true, nativeOutcome.Message);

                return;

            }



            // ── Apply pipeline policy (repair always uses full PS kit) ──────────

            // discord / nvidia  → specialized PowerShell kits only

            // internet          → NetworkOptimizerService only (handled above)

            // brave             → native C# ONLY (handled above)

            // steam             → native C# primary; PS deep pack soft-fails if native OK

            var supportsNative = !repair && _services.NativeApply.SupportsNativeApply(module);

            // Steam still benefits from PS debloat depth; soft-fail if native essentials OK.

            var softFailDeepPack = module is "steam";



            log.Line($"pipeline supportsNative={supportsNative} softFailDeep={softFailDeepPack} experimental={experimental}");



            NativeApplyResult? nativeResult = null;

            if (supportsNative)

            {

                Report(2, "Native apply (registry / files / policy)...");

                var step = 0;

                // Native runs first, then the PS deep pack: scale progress 2–55%.

                var strProgress = new Progress<string>(s =>

                {

                    step++;

                    log.Line($"NATIVE  {s}");

                    var pct = Math.Min(55.0, 2 + step * 2.5);

                    Report(pct, s);

                });

                nativeResult = await _services.NativeApply.ApplyAsync(

                    module, experimental, strProgress, cancellationToken).ConfigureAwait(true);

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

                case "nvidia":

                    script = repair ? scripts.NvidiaRepairScript : scripts.NvidiaOptimizerScript;

                    workDir = scripts.GetNvidiaRoot();

                    if (!repair)

                        args.Add(useGsync ? "-Gsync" : "-RawLatency");

                    break;

                default:

                    throw new InvalidOperationException($"Unknown module: {module}");

            }



            // Only NVIDIA implements an experimental mode (Apply-ExoDriverInstallTweaks reads

            // the switch). Discord declared -Experimental and reached one Write-Host with it;

            // Steam declared it and never referenced it at all. Both switches are gone from

            // those scripts, and sending a parameter they no longer accept would fail the run

            // outright, so the flag now goes only where something acts on it.

            if (!repair && experimental && string.Equals(module, "nvidia", StringComparison.OrdinalIgnoreCase))

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

            var needPwshBootstrap = module is "discord" or "steam" or "nvidia";

            var result = await runner.RunAsync(

                script,

                arguments: args.ToArray(),

                elevate: true,

                progress: deepProgress,

                cancellationToken: cancellationToken,

                workingDirectory: workDir,

                ensureRuntime: needPwshBootstrap).ConfigureAwait(true);



            log.Line($"PS Success={result.Success} ExitCode={result.ExitCode} Summary={result.Summary}");

            log.Line($"PS LogPath={result.LogPath}");

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))

                log.Line($"PS ErrorMessage={result.ErrorMessage}");

            ModuleApplyLog.MirrorElevatedTransaction(module, result.LogPath, log);



            // FullOutput is not a second source: the runner builds it with

            // File.ReadAllText(logPath), so on the elevated path it is byte-for-byte the

            // file MirrorElevatedTransaction just attached. Writing both put the entire run

            // into exo-apply.log twice, on top of the live PROGRESS stream already above it

            // — a 580-line NVIDIA run was ~370 lines of duplicate, and the same [DRS] write

            // failures showed up three times, which reads like three separate failures.

            // Keep the dump only when there was no transaction file to attach.

            if (string.IsNullOrWhiteSpace(result.LogPath) && !string.IsNullOrWhiteSpace(result.FullOutput))

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

                //

                // Ok on its own is not enough to justify absolving the deep pack. It is fixed at

                // construction, before elevation runs, so a declined UAC prompt leaves it true

                // while the elevated half of the native apply never happened. Without the extra

                // condition, "native OK" could excuse a deep-pack failure on a machine where the

                // native side was itself only half applied — two partial results reported as one

                // success. The steps know which it was.

                var nativeElevOk = nativeResult is null ||

                    !nativeResult.Steps.Any(s => s.Id == "elevated-hklm" && s.Status != "ok");

                // And never soft-fail something that never ran. A missing script, a SHA-256

                // mismatch against the shipped manifest, or PowerShell 7 being unavailable are

                // refusals *before* execution — categorically different from "the deep pack ran

                // and partly failed", which is the only thing this branch was built to absorb.

                // Without this, "Optimizer integrity failed (SHA-256 mismatch). Reinstall Exo

                // before applying." was swallowed here and reported as a completed Steam

                // optimize: the one message the user must not miss, hidden by the one branch

                // designed to be forgiving.

                if (softFailDeepPack && nativeResult is { Ok: true } && nativeElevOk

                    && !result.RefusedBeforeExecution)

                {

                    log.Line($"DEEP PACK soft-fail (native OK): {err}");

                    log.Step("deep-pack", "partial", err.Length > 200 ? err[..200] : err);

                    // Annotate steam-optimizer.json so detect/UI cannot claim a full deep apply.

                    // Without this, native essentials alone greened the module and stamped

                    // fullApply-adjacent state while shader/debloat depth never ran.

                    AnnotateSteamDeepPackPartial(err);

                    Report(100, "Native apply complete (deep pack partial - see log)");

                    // Finish false: the log and RESULT line must not read as a clean win.

                    // ApplyModuleAsync still continues (no throw) and re-detects — statusKind

                    // becomes partial when deep-pack|partial is on applyReport.

                    log.Finish(false, "native ok; deep pack partial");

                    return;

                }



                // A user stop is not a failed apply. The elevated wrapper signals it with exit

                // code -2, while the token throws out of the polling loop on the other path -

                // which of the two lands first is a race, and the answer must not depend on it.

                if (result.ExitCode == -2 || cancellationToken.IsCancellationRequested)

                {

                    log.Finish(false, "Stopped by the user");

                    throw new OperationCanceledException("Stopped by the user.", cancellationToken);

                }



                log.Finish(false, err);

                throw new InvalidOperationException(err + Environment.NewLine + "Full log: " + log.LatestPath);

            }



            // The MMCSS / PowerThrottling pins used to be restamped here on every Steam apply.

            // They are machine-wide host policy, which AGENTS.md scopes to the system module,

            // and having two owners meant the two modules overwrote each other: Steam wrote

            // NetworkThrottlingIndex=10 while SystemNativeApply wanted 0xFFFFFFFF and detects by

            // exact equality, so applying either one turned the other's tile red and there was

            // no order in which both could be green. PowerThrottlingOff was worse — written

            // here, snapshotted nowhere, detected nowhere, repaired nowhere.

            // All three now live in SystemNativeApply's RegLevers, with a snapshot, a detect row

            // and a repair path each.



            var doneMsg = supportsNative

                ? (result.Success ? "native + deep pack ok" : "native ok; deep pack partial")

                : "apply ok";

            Report(100, "Completed successfully");

            log.Finish(true, doneMsg);

        }

        catch (OperationCanceledException)

        {

            // Do not write a stack trace and a failure verdict into exo-apply.log for

            // something the user chose. The log should read "stopped", because that is what

            // happened, and support reading it later should not go hunting for a crash.

            log.Line("STOPPED by the user");

            log.Finish(false, "Stopped by the user");

            throw;

        }

        catch (Exception ex)

        {

            log.Exception(ex, "RunModuleScriptAsync");

            log.Finish(false, ex.Message);

            throw;

        }

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

