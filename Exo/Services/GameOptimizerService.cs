using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Exo.Helpers;
using Exo.Models;

namespace Exo.Services;

/// <summary>
/// Multi-game catalog: detect installs / AppData configs, then apply Potato / Optimized profiles.
/// Methods are ban-safe user configs + optional Rivals packs — never AC process tampering.
/// </summary>
public sealed partial class GameOptimizerService
{
    public const string GameIdMarvelRivals = "marvel-rivals";
    public const string GameIdBlackOps7 = "black-ops-7";
    public const string GameIdFortnite = "fortnite";
    public const string GameIdValorant = "valorant";
    public const string GameIdCs2 = "cs2";
    public const string GameIdApex = "apex-legends";
    public const string GameIdHelldivers2 = "helldivers-2";
    public const string GameIdTheFinals = "the-finals";
    public const string GameIdPredecessor = "predecessor";
    public const string GameIdLeague = "league-of-legends";

    public const string SteamAppIdMarvelRivals = "2767030";
    public const string SteamAppIdCs2 = "730";
    public const string SteamAppIdApex = "1172470";
    public const string SteamAppIdHelldivers2 = "553850";
    public const string SteamAppIdTheFinals = "2073850";
    public const string SteamAppIdPredecessor = "961200";
    /// <summary>Call of Duty HQ / shared client (BO7 ships through it).</summary>
    public const string SteamAppIdCallOfDuty = "1938090";

    public const string PresetPotato = "potato";
    public const string PresetOptimized = "optimized";

    /// <summary>Leave the game's current display mode alone (default).</summary>
    public const string DisplayLeave = "leave";
    /// <summary>Borderless / fullscreen windowed — alt-tab friendly; can still hit independent flip.</summary>
    public const string DisplayBorderless = "borderless";
    /// <summary>Exclusive / true fullscreen — sometimes lower latency on single-monitor.</summary>
    public const string DisplayExclusive = "exclusive";

    /// <summary>Most-popular titles first, then Marvel Rivals (full pack path).</summary>
    public static readonly IReadOnlyList<GameCatalogEntry> Catalog =
    [
        // Each title uses a different surface — researched for current (2026) client versions.
        new(GameIdBlackOps7, "Black Ops 7", "Battle.net / Steam", SteamAppIdCallOfDuty, Ready: true,
            Blurb: "cod25 players dvars (competitive FPS meta)",
            Icon: "/logos/black-ops-7.png"),
        new(GameIdFortnite, "Fortnite", "Epic", null, Ready: true,
            Blurb: "Ch7 Performance-mode GameUserSettings",
            Icon: "/logos/fortnite.png"),
        new(GameIdValorant, "Valorant", "Riot", null, Ready: true,
            Blurb: "RiotUserSettings graphics (not Vanguard)",
            Icon: "/logos/valorant.png"),
        new(GameIdLeague, "League of Legends", "Riot", null, Ready: true,
            Blurb: "game.cfg Performance block (no Vanguard)",
            Icon: "/logos/league-of-legends.png"),
        new(GameIdCs2, "Counter-Strike 2", "Steam", SteamAppIdCs2, Ready: true,
            Blurb: "cs2_video.txt + modern autoexec",
            Icon: "/logos/cs2.png"),
        new(GameIdApex, "Apex Legends", "Steam / EA", SteamAppIdApex, Ready: true,
            Blurb: "Respawn videoconfig.txt quality ladder",
            Icon: "/logos/apex-legends.png"),
        new(GameIdHelldivers2, "Helldivers 2", "Steam", SteamAppIdHelldivers2, Ready: true,
            Blurb: "Arrowhead user_settings.config",
            Icon: "/logos/helldivers-2.png"),
        new(GameIdTheFinals, "The Finals", "Steam / Epic", SteamAppIdTheFinals, Ready: true,
            Blurb: "Embark UE scalability (Season meta)",
            Icon: "/logos/the-finals.png"),
        new(GameIdPredecessor, "Predecessor", "Steam", SteamAppIdPredecessor, Ready: true,
            Blurb: "UE GameUserSettings scalability",
            Icon: "/logos/predecessor.png"),
        new(GameIdMarvelRivals, "Marvel Rivals", "Steam", SteamAppIdMarvelRivals, Ready: true,
            Blurb: "Engine.ini + optional IoStore packs",
            Icon: "/logos/marvel-rivals.png"),
    ];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string StatePath => Path.Combine(PathHelper.AppDataDir, "game-optimizer.json");
    private static string PackCacheDir => Path.Combine(PathHelper.AppDataDir, "game-packs", "marvel-rivals");

    /// <summary>Full Games hub snapshot: every catalog title + status.</summary>
    public GamesHubSnapshot ListGames(string? selectedGameId = null)
    {
        var state = LoadState();
        var games = new List<GameListItem>();
        foreach (var entry in Catalog)
        {
            games.Add(BuildListItem(entry, state));
        }

        var selected = selectedGameId;
        if (string.IsNullOrWhiteSpace(selected) ||
            games.All(g => !string.Equals(g.Id, selected, StringComparison.OrdinalIgnoreCase)))
        {
            // Prefer first installed ready game, else first ready, else first.
            selected = games.FirstOrDefault(g => g.Installed && g.Ready)?.Id
                       ?? games.FirstOrDefault(g => g.Ready)?.Id
                       ?? games.FirstOrDefault()?.Id
                       ?? GameIdMarvelRivals;
        }

        var detail = DetectGame(selected, preferredPreset: null);
        var installedCount = games.Count(g => g.Installed);
        var appliedCount = games.Count(g => g.Applied);

        return new GamesHubSnapshot
        {
            SelectedGameId = selected!,
            Games = games,
            Selected = detail,
            StatusText = installedCount == 0
                ? "No supported games detected"
                : appliedCount == 0
                    ? $"{installedCount} installed · none applied yet"
                    : $"{installedCount} installed · {appliedCount} with profile",
            Detail = "Pick a game, choose Potato or Optimized, then Apply. Close the game first if Apply says in use."
        };
    }

    /// <summary>Legacy module detect (home / feature list) — any applied game counts.</summary>
    public OptimizerStateInfo Detect(string? preferredPreset = null, string? gameId = null)
    {
        var hub = ListGames(gameId);
        var selected = hub.Selected;
        // Prefer selected game's detect payload; surface catalog in Extra as JSON-ish flags.
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["preset"] = preferredPreset
                         ?? ExtraGet(selected.Extra, "activePreset")
                         ?? ExtraGet(selected.Extra, "preset")
                         ?? PresetOptimized,
            ["gameId"] = hub.SelectedGameId,
            ["gameCount"] = hub.Games.Count.ToString(),
            ["installedCount"] = hub.Games.Count(g => g.Installed).ToString(),
            ["selectedGameId"] = hub.SelectedGameId,
        };
        if (selected.Extra is not null)
        {
            foreach (var kv in selected.Extra)
                extra[kv.Key] = kv.Value;
        }

        return new OptimizerStateInfo
        {
            IsApplied = hub.Games.Any(g => g.Applied),
            StatusText = hub.StatusText,
            Detail = hub.Detail,
            Features = selected.Features,
            Extra = extra
        };
    }

    public OptimizerStateInfo DetectGame(string gameId, string? preferredPreset = null)
    {
        gameId = NormalizeGameId(gameId);
        var entry = Catalog.FirstOrDefault(c =>
            string.Equals(c.Id, gameId, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return new OptimizerStateInfo
            {
                IsApplied = false,
                StatusText = "Unknown game",
                Detail = $"No catalog entry for '{gameId}'.",
                Features = Array.Empty<OptimizerFeatureInfo>(),
                Extra = new Dictionary<string, string> { ["gameId"] = gameId }
            };
        }

        if (!entry.Ready)
        {
            return new OptimizerStateInfo
            {
                IsApplied = false,
                StatusText = "Coming soon",
                Detail = $"{entry.Title} is not wired yet.",
                Features =
                [
                    F(entry.Title, entry.Blurb, false),
                    F("Optimizer ready", "Not available yet", false),
                ],
                Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["gameId"] = entry.Id,
                    ["preset"] = preferredPreset ?? PresetOptimized,
                    ["installed"] = "0",
                    ["ready"] = "0"
                }
            };
        }

        return entry.Id switch
        {
            GameIdMarvelRivals => DetectMarvelRivals(preferredPreset),
            GameIdBlackOps7 => DetectConfigGame(GameIdBlackOps7, preferredPreset),
            GameIdFortnite => DetectConfigGame(GameIdFortnite, preferredPreset),
            GameIdValorant => DetectConfigGame(GameIdValorant, preferredPreset),
            GameIdLeague => DetectConfigGame(GameIdLeague, preferredPreset),
            GameIdCs2 => DetectConfigGame(GameIdCs2, preferredPreset),
            GameIdApex => DetectConfigGame(GameIdApex, preferredPreset),
            GameIdHelldivers2 => DetectConfigGame(GameIdHelldivers2, preferredPreset),
            GameIdTheFinals => DetectConfigGame(GameIdTheFinals, preferredPreset),
            GameIdPredecessor => DetectConfigGame(GameIdPredecessor, preferredPreset),
            _ => new OptimizerStateInfo
            {
                IsApplied = false,
                StatusText = "Coming soon",
                Detail = $"{entry.Title} is not wired yet.",
                Features = [F(entry.Title, entry.Blurb, false)],
                Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["gameId"] = entry.Id,
                    ["preset"] = preferredPreset ?? PresetOptimized,
                    ["ready"] = "0"
                }
            }
        };
    }

    private OptimizerStateInfo DetectMarvelRivals(string? preferredPreset)
    {
        var state = LoadState();
        var probe = ProbeMarvelRivals();
        var rec = GetRecord(state, GameIdMarvelRivals) ?? state.MarvelRivals;
        var activePreset = rec?.Preset;
        var applied = probe.Installed
            && !string.IsNullOrWhiteSpace(activePreset)
            && (activePreset is PresetPotato or PresetOptimized)
            && ConfigLooksApplied(activePreset);

        var presetLabel = activePreset switch
        {
            PresetPotato => "Potato",
            PresetOptimized => "Optimized",
            _ => "None"
        };

        var features = new List<OptimizerFeatureInfo>
        {
            F("Install",
                probe.Installed ? ShortPath(probe.InstallPath!) : "Not found in Steam libraries",
                probe.Installed),
            F("Ban-safe surface",
                "User configs only — no packs, no game-binary mutation",
                true),
            F("Game profile",
                applied
                    ? $"{presetLabel} active"
                    : "Not applied — choose Potato or Optimized",
                applied),
            F("Profile",
                activePreset == PresetPotato
                    ? "Last choice: Potato (max FPS / muddy)"
                    : activePreset == PresetOptimized
                        ? "Last choice: Optimized (high FPS / normal look)"
                        : "Toggle: Potato or Optimized",
                true),
            F("DLSS left alone",
                "SuperSampling / DLSS / Reflex not overridden",
                true),
            F("One-click Repair ready",
                File.Exists(BackupMarkerPath())
                    ? "Backups present"
                    : "Backups created on first Apply",
                File.Exists(BackupMarkerPath())),
        };

        string status;
        string detail;
        if (!probe.Installed)
        {
            status = "Not installed";
            detail = "Install Marvel Rivals on Steam, then reopen Games.";
        }
        else if (applied)
        {
            status = $"{presetLabel} applied";
            detail = $"Active: {presetLabel}. Repair restores backed-up configs.";
        }
        else
        {
            status = "Ready to optimize";
            detail = "Pick Potato or Optimized, then Apply.";
        }

        return new OptimizerStateInfo
        {
            IsApplied = applied,
            StatusText = status,
            Detail = detail,
            Features = features,
            Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["gameId"] = GameIdMarvelRivals,
                ["preset"] = activePreset ?? preferredPreset ?? PresetOptimized,
                ["installed"] = probe.Installed ? "1" : "0",
                ["installPath"] = probe.InstallPath ?? "",
                ["activePreset"] = activePreset ?? "",
                ["displayMode"] = DisplayBorderless,
                ["ready"] = "1"
            }
        };
    }

    private GameListItem BuildListItem(GameCatalogEntry entry, GamesState state)
    {
        if (string.Equals(entry.Id, GameIdMarvelRivals, StringComparison.OrdinalIgnoreCase))
        {
            var probe = ProbeMarvelRivals();
            var rec = GetRecord(state, GameIdMarvelRivals) ?? state.MarvelRivals;
            var applied = probe.Installed
                && rec?.Preset is PresetPotato or PresetOptimized
                && ConfigLooksApplied(rec!.Preset!);
            var presetLabel = rec?.Preset switch
            {
                PresetPotato => "Potato",
                PresetOptimized => "Optimized",
                _ => null
            };
            // Always surface last chosen profile for the UI toggle (Potato vs Optimized).
            var lastPreset = rec?.Preset is PresetPotato or PresetOptimized ? presetLabel : null;
            var (installUrl, installLabel) = GetInstallTarget(entry.Id);
            return new GameListItem
            {
                Id = entry.Id,
                Title = entry.Title,
                Platform = entry.Platform,
                Blurb = entry.Blurb,
                Icon = entry.Icon,
                Ready = entry.Ready,
                Installed = probe.Installed,
                Applied = applied,
                ActivePreset = lastPreset,
                StatusText = !probe.Installed
                    ? "Not installed"
                    : applied
                        ? $"{presetLabel} applied"
                        : lastPreset is not null
                            ? $"Installed · last {lastPreset}"
                            : "Installed",
                Detail = probe.Installed
                    ? ShortPath(probe.InstallPath!)
                    : "Steam App " + (entry.SteamAppId ?? "—"),
                InstallUrl = installUrl,
                InstallLabel = installLabel
            };
        }

        if (IsConfigGame(entry.Id))
            return BuildConfigGameListItem(entry, state);

        {
            var (installUrl, installLabel) = GetInstallTarget(entry.Id);
            return new GameListItem
            {
                Id = entry.Id,
                Title = entry.Title,
                Platform = entry.Platform,
                Blurb = entry.Blurb,
                Icon = entry.Icon,
                Ready = entry.Ready,
                Installed = false,
                Applied = false,
                ActivePreset = null,
                StatusText = entry.Ready ? "Not installed" : "Coming soon",
                Detail = entry.Blurb,
                InstallUrl = installUrl,
                InstallLabel = installLabel
            };
        }
    }

    public async Task<(bool Ok, string Message)> ApplyAsync(
        string preset,
        IProgress<string>? progress = null,
        CancellationToken ct = default) =>
        await ApplyAsync(GameIdMarvelRivals, preset, DisplayBorderless, progress, ct).ConfigureAwait(false);

    public async Task<(bool Ok, string Message)> ApplyAsync(
        string gameId,
        string preset,
        IProgress<string>? progress = null,
        CancellationToken ct = default) =>
        await ApplyAsync(gameId, preset, DisplayBorderless, progress, ct).ConfigureAwait(false);

    public async Task<(bool Ok, string Message)> ApplyAsync(
        string gameId,
        string preset,
        string? displayMode,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        gameId = NormalizeGameId(gameId);
        preset = NormalizePreset(preset);
        // Product policy: always force borderless (per-game tokens). Ignore leave/exclusive UI.
        _ = displayMode;
        displayMode = DisplayBorderless;

        if (!IsGameInstalled(gameId))
            return (false, $"{GameTitlePublic(gameId)} is not installed. Launch it once so configs exist, then Apply.");

        if (IsConfigGame(gameId))
            return await ApplyConfigGameAsync(gameId, preset, displayMode, progress, ct).ConfigureAwait(false);

        if (!string.Equals(gameId, GameIdMarvelRivals, StringComparison.OrdinalIgnoreCase))
            return (false, "That game is not available yet.");

        progress?.Report("Probing Marvel Rivals…");
        var probe = ProbeMarvelRivals();
        if (!probe.Installed || string.IsNullOrWhiteSpace(probe.InstallPath))
            return (false, "Marvel Rivals was not found in Steam library folders.");

        ct.ThrowIfCancellationRequested();
        var gusSchemaKnown = true;

        try
        {
            await Task.Run(() =>
            {
                progress?.Report("Backing up configs…");
                BackupConfigs();

                progress?.Report(preset == PresetPotato
                    ? "Writing Potato configs…"
                    : "Writing Optimized configs…");
                WriteEngineIni(preset);
                WriteScalabilityIni(preset);
                // Always borderless for Marvel (UE FullscreenMode=1)
                gusSchemaKnown = PatchGameUserSettings(preset, DisplayBorderless);

                try
                {
                    WriteConfigText(
                        Path.Combine(PathHelper.AppDataDir, "game-backups", GameIdMarvelRivals, "exo-profile.txt"),
                        $"{preset}\n{DisplayBorderless}\n{DateTimeOffset.UtcNow:o}\n");
                }
                catch { /* non-fatal */ }

                var state = LoadState();
                UpsertRecord(state, GameIdMarvelRivals, new GameApplyRecord
                {
                    Preset = preset,
                    DisplayMode = DisplayBorderless,
                    AppliedUtc = DateTimeOffset.UtcNow,
                    InstallPath = probe.InstallPath
                });
                state.MarvelRivals = state.Games[GameIdMarvelRivals];
                SaveState(state);
            }, ct).ConfigureAwait(false);

            // Soft verify: Engine.ini marker preferred; marker file is enough if game rewrote ini.
            var marvelMarker = Path.Combine(PathHelper.AppDataDir, "game-backups", GameIdMarvelRivals, "exo-profile.txt");
            if (!ConfigLooksApplied(preset) && !File.Exists(marvelMarker))
                return (false, "Config write failed verification — close Marvel Rivals fully and try Apply again.");

            var msg = (preset == PresetPotato ? "Potato configs written" : "Optimized configs written")
                + " (borderless enforced). Restart Marvel Rivals.";
            if (!gusSchemaKnown)
                msg += " Note: GameUserSettings.ini didn't match the sections Exo expects (a game update may have " +
                       "restructured it) — quality/borderless keys were still written, but verify they stuck in-game.";
            progress?.Report("Verified");
            return (true, msg);
        }
        catch (Exception ex)
        {
            return (false, string.IsNullOrWhiteSpace(ex.Message) ? "Apply failed." : ex.Message);
        }
    }

    public async Task<(bool Ok, string Message)> RepairAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default) =>
        await RepairAsync(GameIdMarvelRivals, progress, ct).ConfigureAwait(false);

    public async Task<(bool Ok, string Message)> RepairAsync(
        string gameId,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        gameId = NormalizeGameId(gameId);
        if (!IsGameInstalled(gameId))
            return (false, $"{GameTitlePublic(gameId)} is not installed — nothing to repair.");

        if (IsConfigGame(gameId))
            return await RepairConfigGameAsync(gameId, progress, ct).ConfigureAwait(false);

        if (!string.Equals(gameId, GameIdMarvelRivals, StringComparison.OrdinalIgnoreCase))
            return (false, "That game is not available yet.");

        progress?.Report("Restoring game configs…");
        try
        {
            await Task.Run(() =>
            {
                RestoreConfigs();
                var install = TryFindMarvelRivals();
                if (install is not null)
                {
                    // Legacy cleanup (≤3.16.x shipped IoStore packs + a UTOC signature
                    // bypass; 4.x is config-only). Remove any leftovers Exo installed.
                    var mods = GetModsDir(install);
                    if (Directory.Exists(mods))
                    {
                        progress?.Report("Removing legacy Exo packs from ~mods…");
                        foreach (var f in Directory.EnumerateFiles(mods, "Exo*.*"))
                        {
                            try { File.Delete(f); } catch { /* busy */ }
                        }
                    }

                    progress?.Report("Removing legacy signature bypass…");
                    var win64 = Path.Combine(install, "MarvelGame", "Marvel", "Binaries", "Win64");
                    try { File.Delete(Path.Combine(win64, "plugins", "MarvelRivalsUTOCSignatureBypass.asi")); } catch { }
                    try
                    {
                        // Only remove dsound.dll when the Exo-installed ASI loader owned it.
                        var ds = Path.Combine(win64, "dsound.dll");
                        if (File.Exists(ds) && !Directory.EnumerateFiles(
                                Path.Combine(win64, "plugins"), "*.asi").Any())
                            File.Delete(ds);
                    }
                    catch { }
                }

                // Purge the local pack cache from older Exo builds.
                try
                {
                    if (Directory.Exists(PackCacheDir))
                        Directory.Delete(PackCacheDir, recursive: true);
                }
                catch { }

                var state = LoadState();
                if (state.Games.TryGetValue(GameIdMarvelRivals, out var rec))
                {
                    rec.Preset = null;
                    rec.AppliedUtc = null;
                }
                if (state.MarvelRivals is not null)
                {
                    state.MarvelRivals.Preset = null;
                    state.MarvelRivals.AppliedUtc = null;
                }
                SaveState(state);
            }, ct).ConfigureAwait(false);

            progress?.Report("Repair complete");
            return (true, "Exo game configs restored; Exo* packs removed from ~mods.");
        }
        catch (Exception ex)
        {
            return (false, string.IsNullOrWhiteSpace(ex.Message) ? "Repair failed." : ex.Message);
        }
    }

    public static string NormalizeGameId(string? gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return GameIdMarvelRivals;
        return gameId.Trim().ToLowerInvariant();
    }

    public static string NormalizePreset(string? preset)
    {
        if (string.Equals(preset, PresetPotato, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(preset, "max", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(preset, "maxfps", StringComparison.OrdinalIgnoreCase))
            return PresetPotato;
        return PresetOptimized;
    }

    public static string NormalizeDisplayMode(string? mode)
    {
        if (string.Equals(mode, DisplayBorderless, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "borderless-windowed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "windowedfullscreen", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "windowed_fullscreen", StringComparison.OrdinalIgnoreCase))
            return DisplayBorderless;
        if (string.Equals(mode, DisplayExclusive, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "fullscreen", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "true-fullscreen", StringComparison.OrdinalIgnoreCase))
            return DisplayExclusive;
        return DisplayLeave;
    }

    // ── Probe ─────────────────────────────────────────────────────────────

    public sealed class MarvelProbe
    {
        public bool Installed { get; init; }
        public string? InstallPath { get; init; }
    }

    public MarvelProbe ProbeMarvelRivals()
    {
        var install = TryFindMarvelRivals();
        if (install is null)
            return new MarvelProbe { Installed = false };

        return new MarvelProbe
        {
            Installed = true,
            InstallPath = install
        };
    }

    private static string GetModsDir(string installRoot) =>
        Path.Combine(installRoot, "MarvelGame", "Marvel", "Content", "Paks", "~mods");

    // ── Steam discovery ───────────────────────────────────────────────────

    public static string? TryFindMarvelRivals() =>
        TryFindSteamApp(SteamAppIdMarvelRivals, "MarvelRivals")
        ?? TryFindSteamApp(SteamAppIdMarvelRivals, "Marvel Rivals");

    /// <summary>Locate a Steam app by appmanifest + optional common-folder name hints.</summary>
    public static string? TryFindSteamApp(string appId, params string[] commonFolderHints)
    {
        foreach (var lib in EnumerateSteamLibraryRoots())
        {
            foreach (var hint in commonFolderHints)
            {
                if (string.IsNullOrWhiteSpace(hint)) continue;
                var candidates = new[]
                {
                    Path.Combine(lib, "steamapps", "common", hint),
                    Path.Combine(lib, "common", hint),
                };
                foreach (var c in candidates)
                {
                    if (Directory.Exists(c)) return c;
                }
            }

            var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest))
                manifest = Path.Combine(lib, $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest)) continue;
            try
            {
                var text = File.ReadAllText(manifest);
                var m = Regex.Match(text, @"""installdir""\s+""([^""]+)""", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var dir = Path.Combine(lib, "steamapps", "common", m.Groups[1].Value);
                if (Directory.Exists(dir)) return dir;
            }
            catch { /* next */ }
        }
        return null;
    }

    public static IReadOnlyList<string> EnumerateSteamLibraryRoots()
    {
        var roots = new List<string>();
        var steam = TryFindSteamRoot();
        if (steam is null) return roots;

        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try
            {
                var full = Path.GetFullPath(p.Trim().TrimEnd('\\', '/'));
                if (Directory.Exists(full) &&
                    !roots.Any(r => string.Equals(r, full, StringComparison.OrdinalIgnoreCase)))
                    roots.Add(full);
            }
            catch { /* skip */ }
        }

        Add(steam);
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return roots;
        try
        {
            var text = File.ReadAllText(vdf);
            foreach (Match m in Regex.Matches(text, @"""path""\s+""([^""]+)""", RegexOptions.IgnoreCase))
                Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        catch { /* ignore */ }
        return roots;
    }

    private static string? TryFindSteamRoot()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var p = k?.GetValue("SteamPath") as string ?? k?.GetValue("SteamExe") as string;
            if (!string.IsNullOrWhiteSpace(p))
            {
                if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    p = Path.GetDirectoryName(p);
                if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                    return Path.GetFullPath(p);
            }
        }
        catch { }

        foreach (var c in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
                     @"C:\Program Files (x86)\Steam",
                 })
        {
            if (Directory.Exists(c)) return c;
        }
        return null;
    }

    private static void TryCopyFile(string src, string dest)
    {
        try
        {
            if (!File.Exists(src)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, overwrite: true);
        }
        catch { /* ignore */ }
    }

    // ── Config paths ──────────────────────────────────────────────────────

    private static string MarvelConfigDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Marvel", "Saved", "Config", "Windows");

    private static string EngineIniPath => Path.Combine(MarvelConfigDir, "Engine.ini");
    private static string ScalabilityIniPath => Path.Combine(MarvelConfigDir, "Scalability.ini");
    private static string GameUserSettingsPath => Path.Combine(MarvelConfigDir, "GameUserSettings.ini");
    private static string BackupDir => Path.Combine(PathHelper.AppDataDir, "game-backups", "marvel-rivals");
    private static string BackupMarkerPath() => Path.Combine(BackupDir, "backup.ok");

    private static void BackupConfigs()
    {
        Directory.CreateDirectory(BackupDir);
        foreach (var name in new[] { "Engine.ini", "Scalability.ini", "GameUserSettings.ini", "Game.ini", "DeviceProfiles.ini", "Compat.ini" })
        {
            var src = Path.Combine(MarvelConfigDir, name);
            if (!File.Exists(src)) continue;
            var dest = Path.Combine(BackupDir, name + ".bak");
            if (!File.Exists(dest))
                File.Copy(src, dest, overwrite: false);
        }
        File.WriteAllText(BackupMarkerPath(), DateTimeOffset.UtcNow.ToString("o"));
    }

    private static void RestoreConfigs()
    {
        if (!Directory.Exists(BackupDir)) return;
        Directory.CreateDirectory(MarvelConfigDir);
        foreach (var name in new[] { "Engine.ini", "Scalability.ini", "GameUserSettings.ini", "Game.ini", "DeviceProfiles.ini", "Compat.ini" })
        {
            var bak = Path.Combine(BackupDir, name + ".bak");
            var dest = Path.Combine(MarvelConfigDir, name);
            if (File.Exists(bak))
                File.Copy(bak, dest, overwrite: true);
            else if (File.Exists(dest))
            {
                try
                {
                    var text = File.ReadAllText(dest);
                    if (text.Contains("Exo Games", StringComparison.OrdinalIgnoreCase))
                        File.Delete(dest);
                }
                catch { /* ignore */ }
            }
        }
    }

    private static bool ConfigLooksApplied(string preset)
    {
        try
        {
            if (!File.Exists(EngineIniPath)) return false;
            var text = File.ReadAllText(EngineIniPath);
            if (!text.Contains("Exo Games", StringComparison.OrdinalIgnoreCase)) return false;
            if (preset == PresetPotato)
                return text.Contains("profile=potato", StringComparison.OrdinalIgnoreCase)
                       || text.Contains("r.MipMapLODBias=5", StringComparison.OrdinalIgnoreCase);
            return text.Contains("profile=optimized", StringComparison.OrdinalIgnoreCase)
                   || (text.Contains("Exo Games", StringComparison.OrdinalIgnoreCase)
                       && !text.Contains("r.MipMapLODBias=5", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    // ── Writers ───────────────────────────────────────────────────────────

    private static void WriteEngineIni(string preset)
    {
        Directory.CreateDirectory(MarvelConfigDir);
        var potato = preset == PresetPotato;
        var sb = new StringBuilder();
        sb.AppendLine("; Exo Games — Marvel Rivals");
        sb.AppendLine($"; profile={(potato ? "potato" : "optimized")}");
        sb.AppendLine("; DLSS / SuperSampling controlled by GameUserSettings — not forced here.");
        sb.AppendLine("; Safe to delete this file or use Exo → Games → Repair.");
        sb.AppendLine();
        sb.AppendLine("[SystemSettings]");

        if (potato)
        {
            sb.AppendLine("r.MipMapLODBias=5");
            sb.AppendLine("r.Streaming.MipBias=5");
            sb.AppendLine("r.Streaming.MaxEffectiveScreenSize=1");
            sb.AppendLine("r.Streaming.PoolSize=1536");
            sb.AppendLine("r.ViewDistanceScale=0.05");
            sb.AppendLine("r.SkeletalMeshLODBias=40");
            sb.AppendLine("r.StaticMeshLODDistanceScale=9999");
            sb.AppendLine("r.MaxAnisotropy=1");
            sb.AppendLine("r.DetailMode=0");
            sb.AppendLine("r.MaterialQualityLevel=0");
            sb.AppendLine("foliage.LODDistanceScale=0");
            sb.AppendLine("foliage.DensityScale=0");
            sb.AppendLine("grass.DensityScale=0");
        }
        else
        {
            sb.AppendLine("r.MipMapLODBias=0");
            sb.AppendLine("r.Streaming.MipBias=0");
            sb.AppendLine("r.Streaming.PoolSize=4096");
            sb.AppendLine("r.ViewDistanceScale=0.7");
            sb.AppendLine("r.SkeletalMeshLODBias=0");
            sb.AppendLine("r.StaticMeshLODDistanceScale=1");
            sb.AppendLine("r.MaxAnisotropy=4");
            sb.AppendLine("r.DetailMode=1");
            sb.AppendLine("r.MaterialQualityLevel=1");
            sb.AppendLine("foliage.LODDistanceScale=0.5");
            sb.AppendLine("foliage.DensityScale=0.5");
        }

        sb.AppendLine("r.BloomQuality=0");
        sb.AppendLine("r.DefaultFeature.Bloom=0");
        sb.AppendLine("r.LensFlareQuality=0");
        sb.AppendLine("r.DefaultFeature.LensFlare=0");
        sb.AppendLine("r.DepthOfFieldQuality=0");
        sb.AppendLine("r.MotionBlurQuality=0");
        sb.AppendLine("r.DefaultFeature.MotionBlur=0");
        sb.AppendLine("r.SceneColorFringeQuality=0");
        sb.AppendLine("r.Tonemapper.Quality=0");
        sb.AppendLine("r.EyeAdaptationQuality=0");
        sb.AppendLine("r.DefaultFeature.AmbientOcclusion=0");
        sb.AppendLine("r.AmbientOcclusionLevels=0");
        sb.AppendLine("r.DistanceFieldAO=0");
        sb.AppendLine("r.AOQuality=0");
        sb.AppendLine("r.SSR.Quality=0");
        sb.AppendLine("r.RefractionQuality=0");
        sb.AppendLine("r.VolumetricFog=0");
        sb.AppendLine("r.VolumetricCloud=0");
        sb.AppendLine("r.Fog=0");
        sb.AppendLine("r.ParticleLightQuality=0");
        sb.AppendLine("r.LightShaftQuality=0");
        sb.AppendLine("r.LightFunctionQuality=0");

        if (potato)
        {
            sb.AppendLine("r.ShadowQuality=0");
            sb.AppendLine("r.Shadow.CSM.MaxCascades=0");
            sb.AppendLine("r.Shadow.MaxResolution=4");
            sb.AppendLine("r.Shadow.MaxCSMResolution=4");
            sb.AppendLine("r.Nanite=0");
            sb.AppendLine("r.Lumen.DiffuseIndirect.Allow=0");
            sb.AppendLine("r.Lumen.Reflections.Allow=0");
            sb.AppendLine("fx.Niagara.QualityLevel=0");
            sb.AppendLine("r.SkipDrawOnNiagara=1");
        }
        else
        {
            sb.AppendLine("r.ShadowQuality=1");
            sb.AppendLine("r.Shadow.CSM.MaxCascades=1");
            sb.AppendLine("r.Shadow.MaxResolution=512");
            sb.AppendLine("r.Shadow.MaxCSMResolution=512");
            sb.AppendLine("r.Lumen.DiffuseIndirect.Allow=0");
            sb.AppendLine("r.Lumen.Reflections.Allow=0");
            sb.AppendLine("fx.Niagara.QualityLevel=1");
        }

        sb.AppendLine();
        sb.AppendLine("[ConsoleVariables]");
        if (potato)
        {
            sb.AppendLine("r.MipMapLODBias=5");
            sb.AppendLine("r.Streaming.MipBias=5");
            sb.AppendLine("r.ViewDistanceScale=0.05");
            sb.AppendLine("r.ShadowQuality=0");
            sb.AppendLine("fx.Niagara.QualityLevel=0");
        }
        else
        {
            sb.AppendLine("r.MipMapLODBias=0");
            sb.AppendLine("r.ViewDistanceScale=0.7");
            sb.AppendLine("r.ShadowQuality=1");
            sb.AppendLine("fx.Niagara.QualityLevel=1");
        }
        sb.AppendLine("r.BloomQuality=0");
        sb.AppendLine("r.MotionBlurQuality=0");
        sb.AppendLine("r.VolumetricFog=0");
        sb.AppendLine("r.SSR.Quality=0");
        sb.AppendLine("r.NGX.DLSS.Enable=1");

        WriteUtf16Le(EngineIniPath, sb.ToString());
    }

    private static void WriteScalabilityIni(string preset)
    {
        var potato = preset == PresetPotato;
        var tex = potato ? "0" : "2";
        var view = potato ? "0" : "1";
        var shadow = potato ? "0" : "1";
        var fx = potato ? "0" : "1";
        var sb = new StringBuilder();
        sb.AppendLine("; Exo Games — Marvel Rivals scalability");
        sb.AppendLine($"; profile={(potato ? "potato" : "optimized")}");
        sb.AppendLine();
        sb.AppendLine($"[ViewDistanceQuality@{view}]");
        sb.AppendLine(potato ? "r.ViewDistanceScale=0.05" : "r.ViewDistanceScale=0.7");
        sb.AppendLine();
        sb.AppendLine($"[ShadowQuality@{shadow}]");
        sb.AppendLine(potato ? "r.ShadowQuality=0" : "r.ShadowQuality=1");
        sb.AppendLine();
        sb.AppendLine("[PostProcessQuality@0]");
        sb.AppendLine("r.BloomQuality=0");
        sb.AppendLine("r.MotionBlurQuality=0");
        sb.AppendLine("r.LensFlareQuality=0");
        sb.AppendLine("r.DepthOfFieldQuality=0");
        sb.AppendLine();
        sb.AppendLine($"[TextureQuality@{tex}]");
        sb.AppendLine(potato ? "r.Streaming.MipBias=5" : "r.Streaming.MipBias=0");
        sb.AppendLine(potato ? "r.MaxAnisotropy=1" : "r.MaxAnisotropy=4");
        sb.AppendLine();
        sb.AppendLine($"[EffectsQuality@{fx}]");
        sb.AppendLine(potato ? "fx.Niagara.QualityLevel=0" : "fx.Niagara.QualityLevel=1");
        sb.AppendLine("r.ParticleLightQuality=0");
        WriteUtf16Le(ScalabilityIniPath, sb.ToString());
    }

    /// <summary>
    /// True when an existing GameUserSettings.ini already has at least one section this
    /// writer targets. False on a fresh file (nothing to compare against yet — not a
    /// concern) or when a game patch renamed/restructured the sections we look for; in that
    /// case EnsureSectionLine still safely appends a recognizable section rather than
    /// silently landing keys the game's current build won't read.
    /// </summary>
    private static bool MarvelConfigSchemaKnown(string existingText) =>
        existingText.Contains("[/Script/Marvel.MarvelGameUserSettings]", StringComparison.OrdinalIgnoreCase)
        || existingText.Contains("[/Script/Engine.GameUserSettings]", StringComparison.OrdinalIgnoreCase)
        || existingText.Contains("[ScalabilityGroups]", StringComparison.OrdinalIgnoreCase);

    private static bool PatchGameUserSettings(string preset, string displayMode = DisplayLeave)
    {
        Directory.CreateDirectory(MarvelConfigDir);
        var path = GameUserSettingsPath;
        string text;
        var hadExistingFile = File.Exists(path);
        if (hadExistingFile)
            text = File.ReadAllText(path);
        else
            text = "[ScalabilityGroups]\r\n[/Script/Marvel.MarvelGameUserSettings]\r\n";
        var schemaKnown = !hadExistingFile || MarvelConfigSchemaKnown(text);

        // Never clobber DLSS / SuperSampling / Reflex / resolution.
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.ViewDistanceQuality", preset == PresetPotato ? "0" : "1");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.ShadowQuality", preset == PresetPotato ? "0" : "1");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.PostProcessQuality", "0");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.TextureQuality", preset == PresetPotato ? "0" : "2");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.EffectsQuality", preset == PresetPotato ? "0" : "1");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.FoliageQuality", preset == PresetPotato ? "0" : "1");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.ShadingQuality", preset == PresetPotato ? "0" : "1");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.ReflectionQuality", "0");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.GlobalIlluminationQuality", "0");

        text = EnsureSectionLine(text, "/Script/Marvel.MarvelGameUserSettings", "bUseVSync", "False");
        text = EnsureSectionLine(text, "/Script/Marvel.MarvelGameUserSettings", "bNvidiaReflex", "True");

        // Always force borderless (product policy) — same as other UE titles.
        _ = displayMode;
        const string mode = "1";
        text = EnsureSectionLine(text, "/Script/Marvel.MarvelGameUserSettings", "FullscreenMode", mode);
        text = EnsureSectionLine(text, "/Script/Marvel.MarvelGameUserSettings", "LastConfirmedFullscreenMode", mode);
        text = EnsureSectionLine(text, "/Script/Marvel.MarvelGameUserSettings", "PreferredFullscreenMode", mode);
        text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "FullscreenMode", mode);
        text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "LastConfirmedFullscreenMode", mode);
        text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "PreferredFullscreenMode", mode);
        text = Regex.Replace(text, @"(?im)^(\s*FullscreenMode\s*=\s*).*$", $"${{1}}{mode}");
        text = Regex.Replace(text, @"(?im)^(\s*LastConfirmedFullscreenMode\s*=\s*).*$", $"${{1}}{mode}");
        text = Regex.Replace(text, @"(?im)^(\s*PreferredFullscreenMode\s*=\s*).*$", $"${{1}}{mode}");

        WriteUtf16Le(path, text);

        // Walk every Marvel GUS under Local AppData (WindowsClient + any mirrored folders).
        try
        {
            var root = Path.GetDirectoryName(MarvelConfigDir); // …\Saved\Config
            if (root is not null && Directory.Exists(root))
            {
                foreach (var gus in Directory.EnumerateFiles(root, "GameUserSettings.ini", SearchOption.AllDirectories))
                {
                    if (string.Equals(gus, path, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var t = File.ReadAllText(gus);
                        t = EnsureSectionLine(t, "/Script/Marvel.MarvelGameUserSettings", "FullscreenMode", mode);
                        t = EnsureSectionLine(t, "/Script/Marvel.MarvelGameUserSettings", "LastConfirmedFullscreenMode", mode);
                        t = EnsureSectionLine(t, "/Script/Marvel.MarvelGameUserSettings", "PreferredFullscreenMode", mode);
                        t = EnsureSectionLine(t, "/Script/Engine.GameUserSettings", "FullscreenMode", mode);
                        t = Regex.Replace(t, @"(?im)^(\s*FullscreenMode\s*=\s*).*$", $"${{1}}{mode}");
                        WriteUtf16Le(gus, t);
                    }
                    catch { /* locked */ }
                }
            }
        }
        catch { /* ignore */ }

        return schemaKnown;
    }

    private static string EnsureSectionLine(string text, string section, string key, string value)
    {
        var sectionHeader = $"[{section}]";
        var line = $"{key}={value}";
        var keyRx = new Regex($@"(?im)^\s*{Regex.Escape(key)}\s*=.*$");

        if (text.Contains(sectionHeader, StringComparison.OrdinalIgnoreCase))
        {
            if (keyRx.IsMatch(text))
                return keyRx.Replace(text, line, 1);

            var idx = text.IndexOf(sectionHeader, StringComparison.OrdinalIgnoreCase);
            var insertAt = idx + sectionHeader.Length;
            while (insertAt < text.Length && (text[insertAt] == '\r' || text[insertAt] == '\n'))
                insertAt++;
            return text.Insert(insertAt, line + "\r\n");
        }

        if (!text.EndsWith("\n", StringComparison.Ordinal))
            text += "\r\n";
        return text + sectionHeader + "\r\n" + line + "\r\n";
    }

    private static void WriteUtf16Le(string path, string content)
    {
        var enc = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        var body = content.TrimStart('\uFEFF');
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Exception? last = null;
        for (var i = 0; i < 10; i++)
        {
            try
            {
                var tmp = path + ".exo-tmp";
                File.WriteAllText(tmp, body, enc);
                try
                {
                    File.Copy(tmp, path, overwrite: true);
                    try { File.Delete(tmp); } catch { /* ignore */ }
                    return;
                }
                catch (IOException)
                {
                    File.WriteAllText(path, body, enc);
                    try { File.Delete(tmp); } catch { /* ignore */ }
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                Thread.Sleep(120 * (i + 1));
            }
        }
        throw last ?? new IOException($"Could not write {Path.GetFileName(path)} — close the game and retry.");
    }

    // ── State ─────────────────────────────────────────────────────────────

    private static GamesState LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return new GamesState();
            var state = JsonSerializer.Deserialize<GamesState>(File.ReadAllText(StatePath), JsonOpts)
                        ?? new GamesState();
            // System.Text.Json rebuilds Dictionary without our comparer — re-key case-insensitive.
            if (state.Games is not null && state.Games.Comparer != StringComparer.OrdinalIgnoreCase)
            {
                var rebuilt = new Dictionary<string, GameApplyRecord>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in state.Games)
                    rebuilt[kv.Key] = kv.Value;
                state.Games = rebuilt;
            }
            return state;
        }
        catch { return new GamesState(); }
    }

    private static void SaveState(GamesState state)
    {
        Directory.CreateDirectory(PathHelper.AppDataDir);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, JsonOpts));
    }

    private static OptimizerFeatureInfo F(string title, string detail, bool active) =>
        new() { Title = title, Detail = detail, IsActive = active };

    private static string ShortPath(string path)
    {
        try
        {
            if (path.Length <= 56) return path;
            return "…" + path[^52..];
        }
        catch { return path; }
    }

    private static string? ExtraGet(IReadOnlyDictionary<string, string>? d, string key)
    {
        if (d is null) return null;
        return d.TryGetValue(key, out var v) ? v : null;
    }

    private static GameApplyRecord? GetRecord(GamesState state, string gameId)
    {
        if (state.Games.TryGetValue(gameId, out var rec)) return rec;
        if (string.Equals(gameId, GameIdMarvelRivals, StringComparison.OrdinalIgnoreCase))
            return state.MarvelRivals;
        return null;
    }

    private static void UpsertRecord(GamesState state, string gameId, GameApplyRecord rec)
    {
        state.Games[gameId] = rec;
        if (string.Equals(gameId, GameIdMarvelRivals, StringComparison.OrdinalIgnoreCase))
            state.MarvelRivals = rec;
    }

    public sealed record GameCatalogEntry(
        string Id,
        string Title,
        string Platform,
        string? SteamAppId,
        bool Ready,
        string Blurb,
        string Icon = "");

    public sealed class GameListItem
    {
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public string Platform { get; init; } = "";
        public string Blurb { get; init; } = "";
        /// <summary>UI asset path under the web root, e.g. /logos/marvel-rivals.png</summary>
        public string Icon { get; init; } = "";
        public bool Ready { get; init; }
        public bool Installed { get; init; }
        public bool Applied { get; init; }
        public string? ActivePreset { get; init; }
        public string StatusText { get; init; } = "";
        public string Detail { get; init; } = "";
        /// <summary>steam:// or https:// store page to install this game.</summary>
        public string? InstallUrl { get; init; }
        /// <summary>Short button label e.g. "Open Steam".</summary>
        public string? InstallLabel { get; init; }
    }

    /// <summary>True when configs/install are present enough to Apply.</summary>
    public bool IsGameInstalled(string gameId)
    {
        gameId = NormalizeGameId(gameId);
        if (string.Equals(gameId, GameIdMarvelRivals, StringComparison.OrdinalIgnoreCase))
            return ProbeMarvelRivals().Installed;
        if (IsConfigGame(gameId))
            return ProbeConfigGame(gameId).Installed;
        return false;
    }

    private static string GameTitlePublic(string gameId) =>
        Catalog.FirstOrDefault(c => string.Equals(c.Id, gameId, StringComparison.OrdinalIgnoreCase))?.Title
        ?? gameId;

    /// <summary>Store / launcher URI for install. Prefer steam://install when Steam owns the title.</summary>
    public static (string? Url, string Label) GetInstallTarget(string gameId)
    {
        gameId = NormalizeGameId(gameId);
        return gameId switch
        {
            GameIdBlackOps7 => PreferSteamInstall(SteamAppIdCallOfDuty,
                "https://store.steampowered.com/app/1938090/Call_of_Duty/",
                "Open Steam — Call of Duty"),
            GameIdFortnite => ("https://store.epicgames.com/p/fortnite", "Open Epic — Fortnite"),
            GameIdValorant => ("https://playvalorant.com/", "Open Valorant install page"),
            GameIdLeague => ("https://www.leagueoflegends.com/", "Open League of Legends"),
            GameIdCs2 => PreferSteamInstall(SteamAppIdCs2,
                "https://store.steampowered.com/app/730/CounterStrike_2/",
                "Open Steam — CS2"),
            GameIdApex => PreferSteamInstall(SteamAppIdApex,
                "https://store.steampowered.com/app/1172470/Apex_Legends/",
                "Open Steam — Apex"),
            GameIdHelldivers2 => PreferSteamInstall(SteamAppIdHelldivers2,
                "https://store.steampowered.com/app/553850/HELLDIVERS_2/",
                "Open Steam — Helldivers 2"),
            GameIdTheFinals => PreferSteamInstall(SteamAppIdTheFinals,
                "https://store.steampowered.com/app/2073850/THE_FINALS/",
                "Open Steam — The Finals"),
            GameIdPredecessor => PreferSteamInstall(SteamAppIdPredecessor,
                "https://store.steampowered.com/app/961200/Predecessor/",
                "Open Steam — Predecessor"),
            GameIdMarvelRivals => PreferSteamInstall(SteamAppIdMarvelRivals,
                "https://store.steampowered.com/app/2767030/Marvel_Rivals/",
                "Open Steam — Marvel Rivals"),
            _ => (null, "Install")
        };
    }

    private static (string Url, string Label) PreferSteamInstall(string appId, string httpsFallback, string label)
    {
        // steam://install opens the install dialog when Steam is present
        if (TryFindSteamRoot() is not null)
            return ($"steam://install/{appId}", label);
        return (httpsFallback, label);
    }

    /// <summary>Open the platform store / install page for a catalog game.</summary>
    public (bool Ok, string Message) OpenInstallPage(string gameId)
    {
        gameId = NormalizeGameId(gameId);
        var (url, label) = GetInstallTarget(gameId);
        if (string.IsNullOrWhiteSpace(url))
            return (false, "No install page is configured for this game.");

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            return (true, label);
        }
        catch (Exception ex)
        {
            // steam:// may fail if Steam is broken — fall back to HTTPS store
            var https = url.StartsWith("steam://", StringComparison.OrdinalIgnoreCase)
                ? GetInstallTarget(gameId).Url // already steam preferred; force https from known apps
                : null;
            // Rebuild https fallbacks only
            https = gameId switch
            {
                GameIdBlackOps7 => "https://store.steampowered.com/app/1938090/Call_of_Duty/",
                GameIdCs2 => "https://store.steampowered.com/app/730/CounterStrike_2/",
                GameIdApex => "https://store.steampowered.com/app/1172470/Apex_Legends/",
                GameIdHelldivers2 => "https://store.steampowered.com/app/553850/HELLDIVERS_2/",
                GameIdTheFinals => "https://store.steampowered.com/app/2073850/THE_FINALS/",
                GameIdMarvelRivals => "https://store.steampowered.com/app/2767030/Marvel_Rivals/",
                GameIdFortnite => "https://store.epicgames.com/p/fortnite",
                GameIdValorant => "https://playvalorant.com/",
                _ => null
            };
            if (https is null)
                return (false, string.IsNullOrWhiteSpace(ex.Message) ? "Could not open install page." : ex.Message);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = https,
                    UseShellExecute = true
                });
                return (true, "Opened store page in browser");
            }
            catch (Exception ex2)
            {
                return (false, string.IsNullOrWhiteSpace(ex2.Message) ? "Could not open install page." : ex2.Message);
            }
        }
    }

    public sealed class GamesHubSnapshot
    {
        public string SelectedGameId { get; init; } = GameIdMarvelRivals;
        public IReadOnlyList<GameListItem> Games { get; init; } = Array.Empty<GameListItem>();
        public OptimizerStateInfo Selected { get; init; } = new();
        public string StatusText { get; init; } = "";
        public string Detail { get; init; } = "";
    }

    private sealed class GamesState
    {
        /// <summary>Per-game apply records (preferred).</summary>
        public Dictionary<string, GameApplyRecord> Games { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Legacy single-game field (Marvel Rivals).</summary>
        public GameApplyRecord? MarvelRivals { get; set; }
    }

    private sealed class GameApplyRecord
    {
        public string? Preset { get; set; }
        /// <summary>leave | borderless | exclusive — last Apply display preference.</summary>
        public string? DisplayMode { get; set; }
        public DateTimeOffset? AppliedUtc { get; set; }
        public string? InstallPath { get; set; }
    }
}
