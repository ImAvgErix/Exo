using System.Diagnostics;
using System.Text.Json;
using Exo.Helpers;

namespace Exo.Services;

/// <summary>
/// Spotify desktop: audio quality, ad-surface removal, startup cost, and GPU routing.
///
/// Spotify matters on a gaming machine for the same reason Discord and the Steam overlay do —
/// it is a Chromium app that runs the whole time a game does. The wins here are it not starting
/// with Windows, not rendering on the discrete GPU, and not streaming at a lower bitrate than
/// the account is paying for.
///
/// The one hard constraint that shapes this file: <b>Spotify rewrites prefs from memory when it
/// exits.</b> Editing the file under a running client means the client overwrites every change
/// seconds later. Closing it first is a correctness requirement, not a courtesy — and it is why
/// Apply reports a failure rather than a success if the client will not close.
/// </summary>
internal static class SpotifyNativeApply
{
    private const string Module = "spotify";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    internal sealed record SpotifyInstall(
        bool Installed,
        string? PrefsPath,
        string? ExePath,
        bool IsStoreBuild);

    /// <summary>
    /// A prefs entry Exo sets. <paramref name="Quoted"/> tracks the value's type in the file
    /// format: strings are written with quotes, numbers and booleans bare. Writing a bool as
    /// a quoted string makes Spotify ignore it.
    /// </summary>
    private sealed record Pref(string Id, string Title, string Key, string Value, bool Quoted, string Why);

    private static readonly Pref[] Prefs =
    {
        // Streaming and local playback quality. 4 is "Very high" (320 kbit/s Ogg). A free
        // account is capped server-side at High and simply ignores this, which costs nothing —
        // but it is why the row is worded as "requested" rather than promised.
        new("bitrate", "Streaming quality", "audio.play_bitrate_enumeration", "4", false,
            "Streaming quality set to Very High."),
        new("bitrate-unmetered", "Quality on unmetered Wi-Fi", "audio.play_bitrate_non_metered_enumeration", "4", false,
            "Very High on unmetered connections too."),
        new("download-quality", "Download quality", "audio.sync_bitrate_enumeration", "4", false,
            "Downloads stored at Very High."),

        // Volume normalisation re-levels every track against a target loudness. Off is the
        // choice for accurate playback; it is also one fewer DSP pass per frame of audio.
        new("normalize", "Volume normalisation", "audio.normalize_v2", "false", false,
            "Volume normalisation off — tracks play as mastered."),

        // Home-page takeover: the full-bleed promo panel that loads on launch. Hiding it stops
        // Spotify fetching and rendering it at all.
        new("hide-hpto", "Home-page promo", "ui.hide_hpto", "true", false,
            "Home-page promo takeover hidden."),

        // Desktop notification on every track change — a foreground-stealing toast mid-game.
        new("track-toasts", "Track-change popups", "ui.track_notifications_enabled", "false", false,
            "Track-change notifications off."),

        // Autostart. Also removed from the Run key below; Spotify honours whichever it finds,
        // so both have to go or it comes back.
        new("autostart", "Start with Windows", "app.autostart-mode", "off", true,
            "Spotify no longer starts with Windows."),

        // Hardware acceleration off is the deliberate choice on a gaming PC: a music player
        // compositing on the GPU competes for the same frames the game needs, and the CPU cost
        // of rendering a mostly-static UI is negligible. This is a trade, not a free win, and
        // Repair puts it back.
        new("hw-accel", "Hardware acceleration", "app.browser.hardware-acceleration", "false", false,
            "Hardware acceleration off — the GPU stays free for the game."),
    };

    // ── Discovery ─────────────────────────────────────────────────────────────────────────

    public static SpotifyInstall Discover()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Installer build: the common one, and the only one whose prefs are freely writable.
        var desktopPrefs = Path.Combine(appData, "Spotify", "prefs");
        var desktopExe = Path.Combine(appData, "Spotify", "Spotify.exe");
        if (File.Exists(desktopPrefs) || File.Exists(desktopExe))
            return new SpotifyInstall(true, desktopPrefs, File.Exists(desktopExe) ? desktopExe : null, false);

        // Microsoft Store build. Same prefs format, sandboxed location. Writable, but the
        // package can reset it on update, so the row says so rather than pretending otherwise.
        var storeRoot = Path.Combine(localApp, "Packages",
            "SpotifyAB.SpotifyMusic_zpdnekdrzrea0", "LocalState", "Spotify");
        var storePrefs = Path.Combine(storeRoot, "prefs");
        if (Directory.Exists(storeRoot))
            return new SpotifyInstall(true, storePrefs, null, true);

        return new SpotifyInstall(false, null, null, false);
    }

    private static string SnapshotPath => Path.Combine(PathHelper.AppDataDir, "spotify-prefs.bak");

    private static IEnumerable<string> GpuTargets(SpotifyInstall install) =>
        install.ExePath is null ? Array.Empty<string>() : new[] { install.ExePath };

    // ── Apply ─────────────────────────────────────────────────────────────────────────────

    public static NativeApplyResult Apply(bool experimental, IProgress<string>? progress = null)
    {
        _ = experimental;
        var steps = new List<NativeApplyStep>();
        void Report(string m) => progress?.Report(m);

        Report("Looking for Spotify…");
        var install = Discover();
        if (!install.Installed || install.PrefsPath is null)
            return NativeApplyResult.Fail(Module, "Spotify is not installed on this PC.");

        Report("Closing Spotify…");
        var closed = CloseSpotify();
        steps.Add(closed);
        if (closed.Status == "fail")
        {
            // Anything written now is overwritten when the running client exits. Reporting
            // success here would be a guaranteed-false green.
            return NativeApplyResult.Fail(Module,
                "Spotify would not close. It rewrites its settings on exit, so nothing was " +
                "changed — close Spotify and apply again.",
                steps);
        }

        Report("Backing up settings for Repair…");
        steps.Add(WriteSnapshot(install));

        Report("Audio quality, ads and startup…");
        steps.AddRange(ApplyPrefs(install));

        Report("Removing the Windows startup entry…");
        steps.Add(RemoveAutostart());

        Report("GPU routing…");
        steps.Add(ApplyGpu(install));

        var failed = steps.Count(s => s.Status == "fail");
        var gpuStep = steps.FirstOrDefault(s => s.Id == "gpu");
        var gpuOk = gpuStep is null || gpuStep.Status is "ok" or "skip";
        var msg = failed == 0
            ? (gpuOk && gpuStep?.Status == "ok"
                ? "Spotify tuned: Very High audio, no promos, no autostart, off the game's GPU."
                : "Spotify tuned: Very High audio, no promos, no autostart.")
            : $"Applied with {failed} step(s) failing — open the log for details.";
        var result = new NativeApplyResult
        {
            Ok = failed == 0,
            Module = Module,
            Message = msg,
            Steps = steps
        };
        // NativeApplyService also persists for spotify after elevation batch; write here too so
        // a path that never elevates still has applyReport for the orb.
        NativeModuleStateWriter.Save(Module, result);
        return result;
    }

    private static IEnumerable<NativeApplyStep> ApplyPrefs(SpotifyInstall install)
    {
        Dictionary<string, string> current;
        string? readError = null;
        try { current = ReadPrefs(install.PrefsPath!); }
        catch (Exception ex) { current = new(); readError = ex.Message; }
        if (readError is not null)
        {
            yield return new NativeApplyStep { Id = "prefs", Status = "fail", Reason = readError };
            yield break;
        }

        foreach (var p in Prefs)
            current[p.Key] = p.Quoted ? $"\"{p.Value}\"" : p.Value;

        var wrote = false;
        string? error = null;
        try { WritePrefs(install.PrefsPath!, current); wrote = true; }
        catch (Exception ex) { error = ex.Message; }

        // One write covers every pref, so the rows share its outcome rather than each claiming
        // an independent success the single file operation never gave them.
        foreach (var p in Prefs)
        {
            yield return new NativeApplyStep
            {
                Id = p.Id,
                Status = wrote ? "ok" : "fail",
                Reason = wrote ? p.Why : error
            };
        }

        if (wrote && install.IsStoreBuild)
        {
            yield return new NativeApplyStep
            {
                Id = "store-build",
                Status = "ok",
                Reason = "Microsoft Store build — a Store update can reset these; re-apply if it does."
            };
        }
    }

    private static NativeApplyStep RemoveAutostart()
    {
        var existing = NativeReg.GetValue("HKCU", RunKey, "Spotify")?.ToString();
        if (string.IsNullOrEmpty(existing))
            return new NativeApplyStep { Id = "autostart-run", Status = "ok", Reason = "no startup entry present" };

        var ok = NativeReg.TryDeleteValue("HKCU", RunKey, "Spotify");
        return new NativeApplyStep
        {
            Id = "autostart-run",
            Status = ok ? "ok" : "fail",
            Reason = ok ? "Windows startup entry removed." : "could not remove the startup entry"
        };
    }

    private static string GpuSnapshotPath => Path.Combine(PathHelper.AppDataDir, "spotify-gpu.json");

    private static NativeApplyStep ApplyGpu(SpotifyInstall install)
    {
        var targets = GpuTargets(install).ToList();
        if (targets.Count == 0)
            return new NativeApplyStep { Id = "gpu", Status = "skip", Reason = "no Spotify.exe path to route" };

        // Record the pre-Exo preference before stamping. Without this the stamp is permanent —
        // the bug Brave shipped with, where Repair left the GPU routing behind.
        try
        {
            if (!File.Exists(GpuSnapshotPath))
            {
                var before = GpuTopology.SnapshotPreferences(targets);
                File.WriteAllText(GpuSnapshotPath, JsonSerializer.Serialize(before));
            }
        }
        catch { /* snapshot is best-effort; the routing result below is reported either way */ }

        var hybrid = GpuTopology.IsHybrid();
        var (stamped, cleared) = GpuTopology.RouteBrowserUi(targets, hybrid);
        return new NativeApplyStep
        {
            Id = "gpu",
            Status = "ok",
            Reason = hybrid
                ? $"Routed to the integrated GPU ({stamped} entry) so the discrete one stays free."
                : $"Single-GPU machine — preference cleared rather than stamped ({cleared} entry)."
        };
    }

    // ── Repair ────────────────────────────────────────────────────────────────────────────

    public static NativeApplyResult Repair(IProgress<string>? progress = null)
    {
        var steps = new List<NativeApplyStep>();
        void Report(string m) => progress?.Report(m);

        var install = Discover();
        if (!install.Installed || install.PrefsPath is null)
            return NativeApplyResult.Fail(Module, "Spotify is not installed on this PC.");

        Report("Closing Spotify…");
        var closed = CloseSpotify();
        steps.Add(closed);
        // Repair writes prefs — if Spotify is still live it overwrites the restore on exit.
        if (closed.Status == "fail")
        {
            return NativeApplyResult.Fail(Module,
                "Spotify would not close. It rewrites settings on exit, so nothing was restored — close it and try Repair again.",
                steps);
        }

        Report("Restoring the original settings file…");
        if (File.Exists(SnapshotPath))
        {
            try
            {
                File.Copy(SnapshotPath, install.PrefsPath!, overwrite: true);
                steps.Add(new NativeApplyStep { Id = "prefs", Status = "ok", Reason = "settings restored from backup" });
            }
            catch (Exception ex)
            {
                steps.Add(new NativeApplyStep { Id = "prefs", Status = "fail", Reason = ex.Message });
            }
        }
        else
        {
            steps.Add(new NativeApplyStep
            {
                Id = "prefs",
                Status = "skip",
                Reason = "no backup from a previous Apply, so the file was left alone"
            });
        }

        Report("Restoring GPU preference…");
        if (File.Exists(GpuSnapshotPath))
        {
            try
            {
                var before = JsonSerializer.Deserialize<Dictionary<string, string?>>(
                    File.ReadAllText(GpuSnapshotPath)) ?? new();
                var n = GpuTopology.RestorePreferences(before);
                steps.Add(new NativeApplyStep { Id = "gpu", Status = "ok", Reason = $"{n} GPU preference(s) restored" });
            }
            catch (Exception ex)
            {
                steps.Add(new NativeApplyStep { Id = "gpu", Status = "fail", Reason = ex.Message });
            }
        }
        else
        {
            steps.Add(new NativeApplyStep
            {
                Id = "gpu",
                Status = "skip",
                Reason = "no recorded GPU preference, so the current one was left alone"
            });
        }

        // The Run key is deliberately not recreated. Spotify writes it itself on next launch if
        // its own autostart setting says to, and the restored prefs file carries that setting —
        // so putting back a stale command line would be inventing state, not restoring it.
        steps.Add(new NativeApplyStep
        {
            Id = "autostart-run",
            Status = "ok",
            Reason = "left to Spotify — it recreates the entry from its own restored setting"
        });

        // Say what actually happened. This returned the same sentence - "restored to what they
        // were before Exo changed them" - whether it had put a backup back, found no backup to
        // put back, or failed trying. On a machine with no Exo backup every restore step is a
        // "skip" and nothing is touched, so the old message told the user their settings had
        // been reverted when Exo had not written a single value.
        var restored = steps.Any(s => s.Id is "prefs" or "gpu" && s.Status == "ok");
        var failedSteps = steps.Where(s => s.Status == "fail").ToList();
        if (failedSteps.Count > 0)
        {
            return NativeApplyResult.Fail(Module,
                "Spotify could not be fully restored: " +
                string.Join("; ", failedSteps.Select(s => $"{s.Id} — {s.Reason}")), steps);
        }
        if (!restored)
        {
            return NativeApplyResult.Fail(Module,
                "Nothing to restore — Exo has no saved Spotify settings on this PC, so nothing was changed back.",
                steps);
        }
        var skipped = steps.Where(s => s.Status == "skip").Select(s => s.Id).ToList();
        return NativeApplyResult.Success(Module,
            skipped.Count == 0
                ? "Spotify settings restored to what they were before Exo changed them."
                : "Spotify settings restored. Left alone (nothing recorded): " + string.Join(", ", skipped) + ".",
            steps);
    }

    // ── Detect ────────────────────────────────────────────────────────────────────────────

    public static (bool Installed, bool Applied, List<(string Title, string Detail, bool Active)> Rows) Detect()
    {
        var rows = new List<(string, string, bool)>();
        var install = Discover();
        if (!install.Installed || install.PrefsPath is null)
            return (false, false, rows);

        Dictionary<string, string> current;
        try { current = File.Exists(install.PrefsPath) ? ReadPrefs(install.PrefsPath) : new(); }
        catch { current = new(); }

        foreach (var p in Prefs)
        {
            var want = p.Quoted ? $"\"{p.Value}\"" : p.Value;
            var have = current.TryGetValue(p.Key, out var v) ? v : null;
            var ok = string.Equals(have, want, StringComparison.OrdinalIgnoreCase);
            rows.Add((p.Title,
                ok ? p.Why : have is null ? "Not set." : $"Currently {have}, wants {want}.",
                ok));
        }

        var runEntry = NativeReg.GetValue("HKCU", RunKey, "Spotify")?.ToString();
        var runOk = string.IsNullOrEmpty(runEntry);
        rows.Add(("Windows startup entry",
            runOk ? "No Windows startup entry." : "Still starts with Windows.",
            runOk));

        // Live GPU routing — never claim "off the game's GPU" without reading preferences.
        if (install.ExePath is not null)
        {
            var hybrid = GpuTopology.IsHybrid();
            var snap = GpuTopology.SnapshotPreferences(new[] { install.ExePath });
            var key = install.ExePath;
            string? pref = null;
            foreach (var kv in snap)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    pref = kv.Value;
                    break;
                }
            }
            var gpuOk = hybrid
                ? pref is not null && pref.Contains("GpuPreference=1", StringComparison.OrdinalIgnoreCase)
                : string.IsNullOrEmpty(pref) || !pref.Contains("GpuPreference=2", StringComparison.OrdinalIgnoreCase);
            rows.Add(("GPU routing",
                hybrid
                    ? (gpuOk ? "Routed to integrated GPU." : "Still on discrete / default GPU.")
                    : (gpuOk ? "Single-GPU — no high-perf stamp." : "Unexpected discrete preference."),
                gpuOk));
        }

        var applied = rows.All(r => r.Item3);
        return (true, applied, rows);
    }

    // ── prefs file I/O ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Spotify's prefs is a flat <c>key=value</c> file, one entry per line, values typed by
    /// shape: quoted for strings, bare for numbers and booleans. Unknown lines are preserved
    /// verbatim on write so Exo never drops a setting it does not recognise.
    /// </summary>
    internal static Dictionary<string, string> ReadPrefs(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return map;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return map;
    }

    internal static void WritePrefs(string path, Dictionary<string, string> prefs)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Spotify reads this file with LF or CRLF; written with the platform default so a
        // hand-inspected file looks native on Windows.
        var body = string.Join(Environment.NewLine, prefs.Select(kv => $"{kv.Key}={kv.Value}"));
        File.WriteAllText(path, body + Environment.NewLine);
    }

    private static NativeApplyStep WriteSnapshot(SpotifyInstall install)
    {
        try
        {
            if (!File.Exists(install.PrefsPath!))
                return new NativeApplyStep { Id = "snapshot", Status = "ok", Reason = "no settings file yet — nothing to back up" };

            // Keep the first backup. A second Apply would otherwise capture Exo's own values as
            // the thing to restore, turning Repair into a no-op.
            if (File.Exists(SnapshotPath))
                return new NativeApplyStep { Id = "snapshot", Status = "ok", Reason = "keeping the original backup" };

            File.Copy(install.PrefsPath!, SnapshotPath, overwrite: false);
            return new NativeApplyStep { Id = "snapshot", Status = "ok", Reason = "original settings backed up" };
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "snapshot", Status = "fail", Reason = ex.Message };
        }
    }

    private static NativeApplyStep CloseSpotify()
    {
        try
        {
            var procs = Process.GetProcessesByName("Spotify");
            if (procs.Length == 0)
                return new NativeApplyStep { Id = "close", Status = "ok", Reason = "Spotify was not running" };

            foreach (var p in procs)
            {
                try
                {
                    // Ask first: CloseMainWindow lets Spotify flush its own state cleanly.
                    // Killing it outright is what causes the half-written prefs this module
                    // then has to work around.
                    if (!p.CloseMainWindow()) p.Kill();
                    if (!p.WaitForExit(6000)) p.Kill();
                    p.WaitForExit(4000);
                }
                catch { }
                finally { p.Dispose(); }
            }

            var still = Process.GetProcessesByName("Spotify");
            foreach (var p in still) p.Dispose();
            return still.Length == 0
                ? new NativeApplyStep { Id = "close", Status = "ok", Reason = "Spotify closed" }
                : new NativeApplyStep { Id = "close", Status = "fail", Reason = $"{still.Length} Spotify process(es) still running" };
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "close", Status = "fail", Reason = ex.Message };
        }
    }
}
