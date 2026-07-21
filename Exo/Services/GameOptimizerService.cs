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
                : $"{installedCount} installed · {appliedCount} optimized",
            Detail = "Pick a game, choose Potato or Optimized, then Apply."
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
            F("UTOC signature bypass",
                probe.BypassPresent
                    ? "dsound.dll + ASI present"
                    : "Missing — IoStore packs may not load (configs still apply)",
                probe.BypassPresent),
            F("~mods folder",
                probe.ModsDirPresent
                    ? $"{probe.ModPackCount} pack file(s)"
                    : "Missing — created on Apply",
                probe.ModsDirPresent || applied),
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
        else if (!probe.BypassPresent)
        {
            status = "Bypass missing";
            detail = "Signature bypass required for IoStore mods. Configs can still apply.";
        }
        else if (applied)
        {
            status = $"{presetLabel} applied";
            detail = $"Active: {presetLabel}. Repair undoes Exo game configs and optional packs.";
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
                ["preset"] = preferredPreset ?? activePreset ?? PresetOptimized,
                ["installed"] = probe.Installed ? "1" : "0",
                ["installPath"] = probe.InstallPath ?? "",
                ["bypass"] = probe.BypassPresent ? "1" : "0",
                ["mods"] = probe.ModsDirPresent ? "1" : "0",
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
                ActivePreset = applied ? presetLabel : null,
                StatusText = !probe.Installed
                    ? "Not installed"
                    : applied
                        ? $"{presetLabel} applied"
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
                PatchGameUserSettings(preset, DisplayBorderless);

                // Clean PC path: create folders → seed cache (bundled first) →
                // install bypass → install packs → verify.
                var modsDir = GetModsDir(probe.InstallPath!);
                Directory.CreateDirectory(modsDir);
                progress?.Report("Ensured ~mods folder…");

                progress?.Report("Loading pack + bypass seeds…");
                RefreshPackCacheFromKnownSources();

                progress?.Report("Installing UTOC signature bypass…");
                TryInstallBypassFromCache(probe.InstallPath!, progress);

                var bypassOk = HasBypassFiles(probe.InstallPath!);
                if (bypassOk || PackCacheHasMinimum())
                {
                    progress?.Report("Installing Exo packs into ~mods…");
                    InstallPacksForPreset(preset, modsDir, progress);
                }
                else
                {
                    progress?.Report("No pack seeds available — configs only…");
                }

                try
                {
                    Directory.CreateDirectory(Path.Combine(PathHelper.AppDataDir, "game-backups", GameIdMarvelRivals));
                    File.WriteAllText(
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

            var after = ProbeMarvelRivals();
            // Soft verify: Engine.ini marker preferred; marker file is enough if game rewrote ini.
            var marvelMarker = Path.Combine(PathHelper.AppDataDir, "game-backups", GameIdMarvelRivals, "exo-profile.txt");
            if (!ConfigLooksApplied(preset) && !File.Exists(marvelMarker))
                return (false, "Config write failed verification — close Marvel Rivals fully and try Apply again.");

            var parts = new List<string>
            {
                preset == PresetPotato ? "Potato configs written" : "Optimized configs written",
                after.ModsDirPresent ? $"~mods ready ({after.ModPackCount} files)" : "~mods missing",
                after.BypassPresent ? "bypass installed" : "bypass missing",
            };
            if (!after.BypassPresent)
                parts.Add("IoStore packs may not load without bypass");
            if (after.ModPackCount == 0)
                parts.Add("no packs installed (seeds missing from this Exo build)");

            var msg = string.Join(" · ", parts) + ". Restart Marvel Rivals.";
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
                    var mods = GetModsDir(install);
                    if (Directory.Exists(mods))
                    {
                        progress?.Report("Removing Exo packs from ~mods…");
                        foreach (var f in Directory.EnumerateFiles(mods, "Exo*.*"))
                        {
                            try { File.Delete(f); } catch { /* busy */ }
                        }
                    }
                }

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
        public bool BypassPresent { get; init; }
        public bool ModsDirPresent { get; init; }
        public int ModPackCount { get; init; }
        public bool ExoPacksReady { get; init; }
    }

    public MarvelProbe ProbeMarvelRivals()
    {
        var install = TryFindMarvelRivals();
        if (install is null)
        {
            return new MarvelProbe
            {
                Installed = false,
                ExoPacksReady = PackCacheHasMinimum()
            };
        }

        var bypass = HasBypassFiles(install);
        var mods = GetModsDir(install);
        var modsOk = Directory.Exists(mods);
        var packCount = 0;
        if (modsOk)
        {
            try
            {
                packCount = Directory.EnumerateFiles(mods, "*.*", SearchOption.TopDirectoryOnly)
                    .Count(f =>
                    {
                        var e = Path.GetExtension(f);
                        return e.Equals(".pak", StringComparison.OrdinalIgnoreCase)
                               || e.Equals(".ucas", StringComparison.OrdinalIgnoreCase)
                               || e.Equals(".utoc", StringComparison.OrdinalIgnoreCase);
                    });
            }
            catch { /* ignore */ }
        }

        return new MarvelProbe
        {
            Installed = true,
            InstallPath = install,
            BypassPresent = bypass,
            ModsDirPresent = modsOk,
            ModPackCount = packCount,
            ExoPacksReady = PackCacheHasMinimum() || packCount > 0
        };
    }

    private static bool HasBypassFiles(string installRoot)
    {
        var win64 = Path.Combine(installRoot, "MarvelGame", "Marvel", "Binaries", "Win64");
        var dsound = Path.Combine(win64, "dsound.dll");
        var asi = Path.Combine(win64, "plugins", "MarvelRivalsUTOCSignatureBypass.asi");
        // Also accept any .asi under plugins if dsound present (community naming)
        if (File.Exists(dsound) && File.Exists(asi)) return true;
        if (File.Exists(dsound) && Directory.Exists(Path.Combine(win64, "plugins")))
        {
            try
            {
                return Directory.EnumerateFiles(Path.Combine(win64, "plugins"), "*.asi").Any();
            }
            catch { return false; }
        }
        return false;
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

    // ── Pack cache + install ──────────────────────────────────────────────

    private static bool PackCacheHasMinimum()
    {
        try
        {
            if (!Directory.Exists(PackCacheDir)) return false;
            // Need at least one ExoFPS triple or Performance pak
            return Directory.EnumerateFiles(PackCacheDir, "Exo*.*", SearchOption.AllDirectories).Any();
        }
        catch { return false; }
    }

    /// <summary>
    /// Seed %LocalAppData%\Exo\game-packs from:
    /// 1) Bundled with Exo (Scripts/Games/MarvelRivals) — works on clean PCs
    /// 2) Dev machine caches / Downloads / existing ~mods
    /// </summary>
    private static void RefreshPackCacheFromKnownSources()
    {
        Directory.CreateDirectory(PackCacheDir);
        var destRaw = Path.Combine(PackCacheDir, "raw");
        var destBypass = Path.Combine(PackCacheDir, "bypass");
        Directory.CreateDirectory(destRaw);
        Directory.CreateDirectory(destBypass);

        // 1) App-bundled seeds (primary for clean PCs)
        foreach (var bundled in EnumerateBundledSeedDirs())
        {
            var packs = Path.Combine(bundled, "packs");
            var bypass = Path.Combine(bundled, "bypass");
            CopyPackFilesFromDir(packs, destRaw);
            if (Directory.Exists(bypass))
            {
                TryCopyFile(Path.Combine(bypass, "dsound.dll"), Path.Combine(destBypass, "dsound.dll"));
                TryCopyFile(
                    Path.Combine(bypass, "MarvelRivalsUTOCSignatureBypass.asi"),
                    Path.Combine(destBypass, "MarvelRivalsUTOCSignatureBypass.asi"));
            }
        }

        // 2) Optional local/dev sources
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sources = new List<string>
        {
            Path.Combine(home, "tools", "mod-build", "smarter-fps", "packed-lite"),
            Path.Combine(home, "tools", "mod-build", "exo-packed"),
            Path.Combine(home, "tools", "mod-build", "packed"),
            Path.Combine(home, "Downloads"),
        };

        var install = TryFindMarvelRivals();
        if (install is not null)
        {
            sources.Add(GetModsDir(install));
            var win64 = Path.Combine(install, "MarvelGame", "Marvel", "Binaries", "Win64");
            TryCopyFile(Path.Combine(win64, "dsound.dll"), Path.Combine(destBypass, "dsound.dll"));
            TryCopyFile(
                Path.Combine(win64, "plugins", "MarvelRivalsUTOCSignatureBypass.asi"),
                Path.Combine(destBypass, "MarvelRivalsUTOCSignatureBypass.asi"));
        }

        foreach (var src in sources)
            CopyPackFilesFromDir(src, destRaw);
    }

    private static IEnumerable<string> EnumerateBundledSeedDirs()
    {
        // Beside Exo.exe: Scripts/Games/MarvelRivals
        yield return Path.Combine(PathHelper.AppDirectory, "Scripts", "Games", "MarvelRivals");
        // Working scripts under %LocalAppData%\Exo\scripts (kit stage)
        yield return Path.Combine(PathHelper.WorkingScriptsDir, "Games", "MarvelRivals");
        // Source tree when running from build output
        yield return Path.Combine(PathHelper.ScriptsRoot, "Games", "MarvelRivals");
    }

    private static void CopyPackFilesFromDir(string src, string destRaw)
    {
        if (!Directory.Exists(src)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(src, "*.*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(f);
                var ext = Path.GetExtension(f);
                if (ext is not (".pak" or ".ucas" or ".utoc")) continue;
                if (!name.Contains("Exo", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("Rigs", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("NoShake", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("Evolve", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("FPS", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("Performance", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("SmartLite", StringComparison.OrdinalIgnoreCase))
                    continue;

                var dest = Path.Combine(destRaw, name);
                if (File.Exists(dest))
                {
                    var di = new FileInfo(dest);
                    var si = new FileInfo(f);
                    if (si.Length <= di.Length && si.LastWriteTimeUtc <= di.LastWriteTimeUtc)
                        continue;
                }
                try { File.Copy(f, dest, overwrite: true); } catch { /* locked */ }
            }
        }
        catch { /* ignore */ }
    }

    private static void InstallPacksForPreset(string preset, string modsDir, IProgress<string>? progress)
    {
        Directory.CreateDirectory(modsDir);
        var raw = Path.Combine(PackCacheDir, "raw");
        if (!Directory.Exists(raw)) return;

        // Clear old Exo* only so we don't leave dual Performance packs
        foreach (var f in Directory.EnumerateFiles(modsDir, "Exo*.*"))
        {
            try { File.Delete(f); } catch { /* busy */ }
        }

        // NoShake always (merged coverage)
        CopyPackFamily(raw, modsDir, "ExoNoShake", "NoShake", "zNoShake", "RigsNoShake");

        // Performance (encrypted Evolve) when available
        CopyPackFamily(raw, modsDir, "ExoPerformance", "zEvolve", "Performance");

        if (preset == PresetPotato)
        {
            // Full stub set preferred for potato
            if (!CopyPackFamily(raw, modsDir, "ExoFPSBoost", "zRigsFPSBoost", "RigsFPS", "FPSBoost"))
                CopyPackFamily(raw, modsDir, "ExoFPSBoostSmart", "ExoFPSBoostSmartLite");
            progress?.Report("Potato packs staged (full FPS stubs when available)…");
        }
        else
        {
            // Optimized: prefer smarter lite FPS; fall back to full
            if (!CopyPackFamily(raw, modsDir, "ExoFPSBoostSmartLite", "ExoFPSBoostSmart", "ExoFPSBoost"))
                CopyPackFamily(raw, modsDir, "ExoFPSBoost", "zRigsFPSBoost");
            // Normalize installed name to ExoFPSBoost_*
            NormalizeFpsBoostNames(modsDir);
            progress?.Report("Optimized packs staged (smarter FPS when available)…");
        }

        // Ensure Performance named consistently
        NormalizeNamePrefix(modsDir, "zEvolve", "ExoPerformance");
    }

    private static void NormalizeFpsBoostNames(string modsDir)
    {
        // If we copied ExoFPSBoostSmartLite_*, rename to ExoFPSBoost_*
        foreach (var ext in new[] { ".pak", ".ucas", ".utoc" })
        {
            foreach (var f in Directory.EnumerateFiles(modsDir, "*" + ext))
            {
                var name = Path.GetFileName(f);
                if (name.StartsWith("ExoFPSBoostSmart", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("zRigsFPSBoost", StringComparison.OrdinalIgnoreCase))
                {
                    var dest = Path.Combine(modsDir, "ExoFPSBoost_9999999_P" + ext);
                    try
                    {
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Move(f, dest);
                    }
                    catch { /* ignore */ }
                }
            }
        }
    }

    private static void NormalizeNamePrefix(string modsDir, string fromPrefix, string toPrefix)
    {
        foreach (var f in Directory.EnumerateFiles(modsDir))
        {
            var name = Path.GetFileName(f);
            if (!name.StartsWith(fromPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var dest = Path.Combine(modsDir, toPrefix + "_9999999_P" + Path.GetExtension(f));
            try
            {
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(f, dest);
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>Copy first matching family of .pak/.ucas/.utoc into modsDir as Exo-named when possible.</summary>
    private static bool CopyPackFamily(string rawDir, string modsDir, params string[] nameHints)
    {
        // Group by stem without extension
        var files = Directory.EnumerateFiles(rawDir).ToList();
        foreach (var hint in nameHints)
        {
            var matches = files
                .Where(f => Path.GetFileName(f).Contains(hint, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0) continue;

            // Prefer sets that include .utoc (IoStore)
            var stems = matches
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var stem in stems)
            {
                var group = files
                    .Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (group.Count == 0) continue;

                foreach (var src in group)
                {
                    var ext = Path.GetExtension(src);
                    // Map to Exo* product names
                    string destName;
                    if (hint.Contains("NoShake", StringComparison.OrdinalIgnoreCase) ||
                        stem.Contains("NoShake", StringComparison.OrdinalIgnoreCase))
                        destName = "ExoNoShake_9999999_P" + ext;
                    else if (hint.Contains("Performance", StringComparison.OrdinalIgnoreCase) ||
                             hint.Contains("Evolve", StringComparison.OrdinalIgnoreCase) ||
                             stem.Contains("Evolve", StringComparison.OrdinalIgnoreCase) ||
                             stem.Contains("Performance", StringComparison.OrdinalIgnoreCase))
                        destName = "ExoPerformance_9999999_P" + ext;
                    else if (stem.Contains("FPS", StringComparison.OrdinalIgnoreCase) ||
                             hint.Contains("FPS", StringComparison.OrdinalIgnoreCase))
                        destName = "ExoFPSBoost_9999999_P" + ext;
                    else
                        destName = Path.GetFileName(src);

                    try { File.Copy(src, Path.Combine(modsDir, destName), overwrite: true); }
                    catch { /* locked */ }
                }
                return true;
            }
        }
        return false;
    }

    private static void TryInstallBypassFromCache(string installRoot, IProgress<string>? progress)
    {
        if (HasBypassFiles(installRoot)) return;
        var cacheBypass = Path.Combine(PackCacheDir, "bypass");
        var ds = Path.Combine(cacheBypass, "dsound.dll");
        var asi = Path.Combine(cacheBypass, "MarvelRivalsUTOCSignatureBypass.asi");
        if (!File.Exists(ds) || !File.Exists(asi)) return;

        var win64 = Path.Combine(installRoot, "MarvelGame", "Marvel", "Binaries", "Win64");
        var plugins = Path.Combine(win64, "plugins");
        try
        {
            Directory.CreateDirectory(plugins);
            File.Copy(ds, Path.Combine(win64, "dsound.dll"), overwrite: true);
            File.Copy(asi, Path.Combine(plugins, "MarvelRivalsUTOCSignatureBypass.asi"), overwrite: true);
            progress?.Report("Installed UTOC signature bypass from cache…");
        }
        catch
        {
            progress?.Report("Could not write bypass (need admin / game closed)…");
        }
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

    private static void PatchGameUserSettings(string preset, string displayMode = DisplayLeave)
    {
        Directory.CreateDirectory(MarvelConfigDir);
        var path = GameUserSettingsPath;
        string text;
        if (File.Exists(path))
            text = File.ReadAllText(path);
        else
            text = "[ScalabilityGroups]\r\n[/Script/Marvel.MarvelGameUserSettings]\r\n";

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

        // UE EWindowMode: 0=Fullscreen exclusive, 1=WindowedFullscreen (borderless), 2=Windowed
        if (displayMode is DisplayBorderless or DisplayExclusive)
        {
            var mode = displayMode == DisplayBorderless ? "1" : "0";
            text = EnsureSectionLine(text, "/Script/Marvel.MarvelGameUserSettings", "FullscreenMode", mode);
            text = EnsureSectionLine(text, "/Script/Marvel.MarvelGameUserSettings", "LastConfirmedFullscreenMode", mode);
            text = EnsureSectionLine(text, "/Script/Marvel.MarvelGameUserSettings", "PreferredFullscreenMode", mode);
            text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "FullscreenMode", mode);
            text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "LastConfirmedFullscreenMode", mode);
            text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "PreferredFullscreenMode", mode);
        }

        WriteUtf16Le(path, text);
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
        File.WriteAllText(path, content.TrimStart('\uFEFF'), enc);
    }

    // ── State ─────────────────────────────────────────────────────────────

    private static GamesState LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return new GamesState();
            return JsonSerializer.Deserialize<GamesState>(File.ReadAllText(StatePath), JsonOpts)
                   ?? new GamesState();
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
