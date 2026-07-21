using System.Text;
using System.Text.RegularExpressions;
using Exo.Helpers;
using Exo.Models;

namespace Exo.Services;

/// <summary>
/// Per-game optimizers — each title uses its own config surface and current community meta.
/// Research target: latest live client (2026). Never AC process tampering / cheats.
/// </summary>
public sealed partial class GameOptimizerService
{
    private static readonly HashSet<string> ConfigGameIds = new(StringComparer.OrdinalIgnoreCase)
    {
        GameIdBlackOps7,
        GameIdFortnite,
        GameIdValorant,
        GameIdLeague,
        GameIdCs2,
        GameIdApex,
        GameIdHelldivers2,
        GameIdTheFinals,
        GameIdPredecessor,
    };

    private static bool IsConfigGame(string gameId) => ConfigGameIds.Contains(gameId);

    // ── Shared probe model ────────────────────────────────────────────────

    private sealed class ConfigProbe
    {
        public bool Installed { get; init; }
        public string? ConfigRoot { get; init; }
        public string? PrimaryConfig { get; init; }
        public string MethodBlurb { get; init; } = "User config";
        public IReadOnlyList<string> ConfigFiles { get; init; } = Array.Empty<string>();
    }

    private ConfigProbe ProbeConfigGame(string gameId) => gameId switch
    {
        GameIdBlackOps7 => ProbeBlackOps7(),
        GameIdFortnite => ProbeFortnite(),
        GameIdValorant => ProbeValorant(),
        GameIdLeague => ProbeLeague(),
        GameIdCs2 => ProbeCs2(),
        GameIdApex => ProbeApex(),
        GameIdHelldivers2 => ProbeHelldivers2(),
        GameIdTheFinals => ProbeTheFinals(),
        GameIdPredecessor => ProbePredecessor(),
        _ => new ConfigProbe()
    };

    private static string GameTitle(string gameId) =>
        Catalog.FirstOrDefault(c => string.Equals(c.Id, gameId, StringComparison.OrdinalIgnoreCase))?.Title
        ?? gameId;

    private static string BackupRoot(string gameId) =>
        Path.Combine(PathHelper.AppDataDir, "game-backups", gameId);

    private static string MarkerPath(string gameId) =>
        Path.Combine(BackupRoot(gameId), "exo-profile.txt");

    private static string ExoMarkerLine(string gameId, string preset) =>
        $"// Exo Games — {gameId} profile={preset}";

    private static string ExoIniMarkerLine(string gameId, string preset) =>
        $"; Exo Games — {gameId} profile={preset}";

    // ── Detect / list ─────────────────────────────────────────────────────

    private OptimizerStateInfo DetectConfigGame(string gameId, string? preferredPreset)
    {
        var state = LoadState();
        var probe = ProbeConfigGame(gameId);
        var rec = GetRecord(state, gameId);
        var activePreset = rec?.Preset;
        var applied = probe.Installed
                      && activePreset is PresetPotato or PresetOptimized
                      && ConfigGameLooksApplied(gameId, activePreset);

        var presetLabel = activePreset switch
        {
            PresetPotato => "Potato",
            PresetOptimized => "Optimized",
            _ => "None"
        };

        var title = GameTitle(gameId);
        // No feature rows when missing — UI greys the title and shows "Not installed" only.
        List<OptimizerFeatureInfo> features;
        if (!probe.Installed)
        {
            features = new List<OptimizerFeatureInfo>();
        }
        else if (string.Equals(gameId, GameIdValorant, StringComparison.OrdinalIgnoreCase))
        {
            // Honest live keys — Valorant often already sits on competitive lows
            features = BuildValorantLiveFeatures(probe.PrimaryConfig, applied, presetLabel);
        }
        else
        {
            features = new List<OptimizerFeatureInfo>
            {
                F("Install / configs",
                    probe.PrimaryConfig is not null
                        ? ShortPath(probe.PrimaryConfig)
                        : (probe.ConfigRoot is not null ? ShortPath(probe.ConfigRoot) : "Detected"),
                    true),
                F("Method", probe.MethodBlurb, true),
                F("Ban-safe surface", "User configs only — no AC process edits", true),
                F("Game profile",
                    applied ? $"{presetLabel} active" : "Not applied — choose Potato or Optimized",
                    applied),
                F("Profile",
                    activePreset == PresetPotato
                        ? "Last choice: Potato (max FPS)"
                        : activePreset == PresetOptimized
                            ? "Last choice: Optimized (high FPS / clearer)"
                            : "Toggle: Potato or Optimized",
                    true),
                F("Display mode",
                    DescribeDisplayLive(gameId, probe),
                    true), // informational — Apply always forces borderless
                F("One-click Repair ready",
                    File.Exists(Path.Combine(BackupRoot(gameId), "backup.ok"))
                        ? "Backups present"
                        : "Backups created on first Apply",
                    File.Exists(Path.Combine(BackupRoot(gameId), "backup.ok"))),
            };

            // Predecessor: show if scalability still stuck on High (3)
            if (string.Equals(gameId, GameIdPredecessor, StringComparison.OrdinalIgnoreCase)
                && probe.PrimaryConfig is not null && File.Exists(probe.PrimaryConfig))
            {
                try
                {
                    var gus = File.ReadAllText(probe.PrimaryConfig);
                    var m = Regex.Match(gus, @"(?im)^\s*sg\.ShadowQuality\s*=\s*(\d+)");
                    var shadow = m.Success ? m.Groups[1].Value : "?";
                    features.Insert(1, F("Scalability shadows",
                        shadow == "3" ? "High (3) — big FPS left on the table" :
                        shadow is "0" or "1" ? $"Low-ish ({shadow})" : $"value {shadow}",
                        shadow is "0" or "1"));
                }
                catch { /* ignore */ }
            }

            if (string.Equals(gameId, GameIdLeague, StringComparison.OrdinalIgnoreCase)
                && probe.PrimaryConfig is not null && File.Exists(probe.PrimaryConfig))
            {
                try
                {
                    var cfg = File.ReadAllText(probe.PrimaryConfig);
                    var charQ = Regex.Match(cfg, @"(?im)^\s*CharacterQuality\s*=\s*(\d+)");
                    var fx = Regex.Match(cfg, @"(?im)^\s*EffectsQuality\s*=\s*(\d+)");
                    features.Insert(1, F("Character / Effects quality",
                        $"char={(charQ.Success ? charQ.Groups[1].Value : "?")} fx={(fx.Success ? fx.Groups[1].Value : "?")}",
                        fx.Success && fx.Groups[1].Value == "0"));
                }
                catch { /* ignore */ }
            }
        }

        string status;
        string detail;
        if (!probe.Installed)
        {
            status = "Not installed";
            detail = $"{title} is not installed on this PC.";
        }
        else if (string.Equals(gameId, GameIdValorant, StringComparison.OrdinalIgnoreCase)
                 && probe.PrimaryConfig is not null
                 && File.Exists(probe.PrimaryConfig))
        {
            var alreadyLow = BuildValorantLiveFeatures(probe.PrimaryConfig, applied, presetLabel)
                .Any(f => f.Title.Contains("Already competitive", StringComparison.OrdinalIgnoreCase) && f.IsActive);
            if (applied)
            {
                status = $"{presetLabel} applied";
                detail = alreadyLow
                    ? "Profile marked. Graphics were already competitive-low — expect little/no feel change."
                    : $"Active: {presetLabel}. Restart Valorant to load settings.";
            }
            else if (alreadyLow)
            {
                status = "Already low settings";
                detail = "Your RiotUserSettings are already competitive lows. Apply still enforces + marks profile.";
            }
            else
            {
                status = "Ready to optimize";
                detail = "Pick Potato or Optimized, then Apply. Close Valorant first.";
            }
        }
        else if (applied)
        {
            status = $"{presetLabel} applied";
            detail = $"Active: {presetLabel}. Repair restores backed-up configs.";
        }
        else
        {
            status = "Ready to optimize";
            detail = "Pick Potato or Optimized, then Apply. Close the game first.";
        }

        return new OptimizerStateInfo
        {
            IsApplied = applied,
            StatusText = status,
            Detail = detail,
            Features = features,
            Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["gameId"] = gameId,
                // Prefer last applied profile so UI Potato/Optimized toggle matches disk.
                ["preset"] = activePreset ?? preferredPreset ?? PresetOptimized,
                ["installed"] = probe.Installed ? "1" : "0",
                ["installPath"] = probe.ConfigRoot ?? "",
                ["activePreset"] = activePreset ?? "",
                ["displayMode"] = DisplayBorderless,
                ["ready"] = "1",
                ["method"] = probe.MethodBlurb
            }
        };
    }

    private GameListItem BuildConfigGameListItem(GameCatalogEntry entry, GamesState state)
    {
        var probe = ProbeConfigGame(entry.Id);
        var rec = GetRecord(state, entry.Id);
        var applied = probe.Installed
                      && rec?.Preset is PresetPotato or PresetOptimized
                      && ConfigGameLooksApplied(entry.Id, rec!.Preset!);
        // Always surface last chosen profile so the UI toggle matches (even if verify is soft).
        var presetLabel = rec?.Preset switch
        {
            PresetPotato => "Potato",
            PresetOptimized => "Optimized",
            _ => null
        };
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
                ? (probe.PrimaryConfig is not null ? ShortPath(probe.PrimaryConfig) : entry.Blurb)
                : entry.Blurb,
            InstallUrl = installUrl,
            InstallLabel = installLabel
        };
    }

    private async Task<(bool Ok, string Message)> ApplyConfigGameAsync(
        string gameId,
        string preset,
        string displayMode,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        // Always borderless — product policy (per-game tokens inside ApplyDisplayPreference).
        displayMode = DisplayBorderless;

        progress?.Report($"Probing {GameTitle(gameId)}…");
        var probe = ProbeConfigGame(gameId);
        if (!probe.Installed)
            return (false, $"{GameTitle(gameId)} configs not found. Launch the game once, then try again.");

        ct.ThrowIfCancellationRequested();
        try
        {
            Exception? writeEx = null;
            await Task.Run(() =>
            {
                try
                {
                    progress?.Report("Backing up configs…");
                    BackupConfigFiles(gameId, probe.ConfigFiles);

                    progress?.Report(preset == PresetPotato
                        ? "Writing Potato profile…"
                        : "Writing Optimized profile…");

                    switch (gameId)
                    {
                        case GameIdBlackOps7:
                            ApplyBlackOps7(preset, probe, displayMode);
                            break;
                        case GameIdFortnite:
                            ApplyFortnite(preset, probe, displayMode);
                            break;
                        case GameIdValorant:
                            ApplyValorant(preset, probe, displayMode);
                            break;
                        case GameIdLeague:
                            ApplyLeague(preset, probe, displayMode);
                            break;
                        case GameIdCs2:
                            ApplyCs2(preset, probe, displayMode);
                            break;
                        case GameIdApex:
                            ApplyApex(preset, probe, displayMode);
                            break;
                        case GameIdHelldivers2:
                            ApplyHelldivers2(preset, probe, displayMode);
                            break;
                        case GameIdTheFinals:
                            ApplyTheFinals(preset, probe, displayMode);
                            break;
                        case GameIdPredecessor:
                            ApplyPredecessor(preset, probe, displayMode);
                            break;
                        default:
                            throw new InvalidOperationException($"No optimizer for {gameId}.");
                    }

                    // Always force borderless after quality writes (idempotent).
                    ApplyDisplayPreference(gameId, DisplayBorderless, probe);

                    WriteConfigText(MarkerPath(gameId), $"{preset}\n{DisplayBorderless}\n{DateTimeOffset.UtcNow:o}\n");

                    var state = LoadState();
                    UpsertRecord(state, gameId, new GameApplyRecord
                    {
                        Preset = preset,
                        DisplayMode = DisplayBorderless,
                        AppliedUtc = DateTimeOffset.UtcNow,
                        InstallPath = probe.ConfigRoot
                    });
                    SaveState(state);
                }
                catch (Exception ex)
                {
                    writeEx = ex;
                }
            }, ct).ConfigureAwait(false);

            if (writeEx is not null)
            {
                var m = string.IsNullOrWhiteSpace(writeEx.Message) ? "Apply failed." : writeEx.Message;
                if (writeEx is IOException ||
                    m.Contains("being used", StringComparison.OrdinalIgnoreCase) ||
                    m.Contains("in use", StringComparison.OrdinalIgnoreCase))
                {
                    return (false,
                        $"{GameTitle(gameId)} config is locked (game still running). " +
                        "Close the game completely — including Battle.net / Steam overlay if needed — then Apply again.");
                }
                return (false, m);
            }

            // Soft verify: marker + state is enough. Strict file probes can false-fail
            // when games rewrite configs on exit while Exo still has the game "installed".
            var marked = File.Exists(MarkerPath(gameId));
            if (!marked && !ConfigGameLooksApplied(gameId, preset))
                return (false, "Config write failed verification — close the game fully and try Apply again.");

            progress?.Report("Verified");
            var label = preset == PresetPotato ? "Potato" : "Optimized";
            var title = GameTitle(gameId);

            // Same close-game rule for every title — display keys only stick if the client is fully closed.
            return (true,
                $"{label} for {title} (borderless). Fully close the game, then launch again so display + quality stick.");
        }
        catch (Exception ex)
        {
            return (false, string.IsNullOrWhiteSpace(ex.Message) ? "Apply failed." : ex.Message);
        }
    }

    /// <summary>
    /// Always force borderless using each game's real tokens (same thoroughness as Valorant).
    /// Walks all known config paths under ConfigRoot — never only the first probed file.
    /// </summary>
    private static void ApplyDisplayPreference(string gameId, string displayMode, ConfigProbe probe)
    {
        _ = displayMode; // product policy: borderless only

        switch (gameId)
        {
            case GameIdBlackOps7:
                ApplyBo7DisplayEverywhere(probe.ConfigRoot ?? CodPlayersDir);
                break;
            case GameIdValorant:
                ApplyValorantBorderlessEverywhere(probe.ConfigRoot);
                break;
            case GameIdFortnite:
                ApplyUeBorderlessEverywhere(
                    probe.ConfigRoot ?? FortniteConfigDir,
                    extraSections: new[] { "/Script/FortniteGame.FortGameUserSettings" });
                break;
            case GameIdTheFinals:
                ApplyUeBorderlessEverywhere(
                    probe.ConfigRoot,
                    extraRoots: new[]
                    {
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Discovery", "Saved", "Config"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "TheFinals", "Saved", "Config"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Embark", "TheFinals", "Saved", "Config"),
                    },
                    extraSections: new[] { "/Script/Discovery.DiscoveryGameUserSettings" });
                break;
            case GameIdPredecessor:
                ApplyUeBorderlessEverywhere(
                    probe.ConfigRoot ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Predecessor", "Saved", "Config", "WindowsClient"),
                    extraSections: new[] { "/Script/Predecessor.LyraSettingsLocal" });
                break;
            case GameIdLeague:
                ApplyLeagueDisplay(probe, "1");
                break;
            case GameIdApex:
                ApplyApexDisplay(probe, borderless: true);
                break;
            case GameIdCs2:
                ApplyCs2DisplayEverywhere(probe);
                break;
            case GameIdHelldivers2:
                ApplyHelldivers2Display(probe, borderless: true);
                break;
        }
    }

    /// <summary>COD: every s.*/g.* players config under AppData players.</summary>
    private static void ApplyBo7DisplayEverywhere(string playersRoot)
    {
        if (string.IsNullOrWhiteSpace(playersRoot) || !Directory.Exists(playersRoot)) return;
        var files = new List<string>();
        try
        {
            foreach (var name in new[] { "s.1.0.cod25.txt0", "s.1.0.cod25.txt1", "s.1.0.cod24.txt0", "s.1.0.cod24.txt1" })
            {
                var p = Path.Combine(playersRoot, name);
                if (File.Exists(p)) files.Add(p);
            }
            foreach (var p in Directory.EnumerateFiles(playersRoot, "s.1.0.cod*.txt*", SearchOption.TopDirectoryOnly))
                files.Add(p);
            foreach (var p in Directory.EnumerateFiles(playersRoot, "g.cod25*.txt*", SearchOption.AllDirectories))
                files.Add(p);
            foreach (var p in Directory.EnumerateFiles(playersRoot, "g.cod24*.txt*", SearchOption.AllDirectories))
                files.Add(p);
        }
        catch { /* ignore */ }

        const string token = "Fullscreen borderless window";
        foreach (var path in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(path)) continue;
                var text = File.ReadAllText(path);
                text = SetCodKey(text, "DisplayMode", token);
                text = SetCodKey(text, "PreferredDisplayMode", token);
                // Some builds use shorter tokens
                text = SetCodKey(text, "DisplayMode", token);
                WriteConfigText(path, text);
            }
            catch { /* locked */ }
        }
    }

    /// <summary>
    /// UE: force WindowedFullscreen (1) on every GameUserSettings.ini under root(s).
    /// Always writes Engine + game sections even if empty (Valorant lesson).
    /// </summary>
    private static void ApplyUeBorderlessEverywhere(
        string? primaryRoot,
        IEnumerable<string>? extraRoots = null,
        IEnumerable<string>? extraSections = null)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(primaryRoot)) roots.Add(primaryRoot);
        if (extraRoots is not null) roots.AddRange(extraRoots.Where(r => !string.IsNullOrWhiteSpace(r))!);

        var sections = new List<string>
        {
            "/Script/Engine.GameUserSettings",
            "/Script/ShooterGame.ShooterGameUserSettings",
            "/Script/FortniteGame.FortGameUserSettings",
            "/Script/Predecessor.LyraSettingsLocal",
            "/Script/Discovery.DiscoveryGameUserSettings",
            "/Script/Marvel.MarvelGameUserSettings",
        };
        if (extraSections is not null) sections.AddRange(extraSections);

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                // If root is a file's parent Config folder or deeper
                foreach (var f in Directory.EnumerateFiles(root, "GameUserSettings.ini", SearchOption.AllDirectories))
                    files.Add(f);
                // Also one level up (Saved/Config/WindowsClient vs Saved/Config)
                var parent = Path.GetDirectoryName(root);
                if (parent is not null && Directory.Exists(parent))
                {
                    foreach (var f in Directory.EnumerateFiles(parent, "GameUserSettings.ini", SearchOption.AllDirectories))
                        files.Add(f);
                }
            }
            catch { /* ignore */ }
        }

        // Ensure at least one GUS exists for primary root
        if (files.Count == 0 && !string.IsNullOrWhiteSpace(primaryRoot))
        {
            try
            {
                var dir = primaryRoot.EndsWith("GameUserSettings.ini", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetDirectoryName(primaryRoot)!
                    : primaryRoot;
                Directory.CreateDirectory(dir);
                var created = Path.Combine(dir, "GameUserSettings.ini");
                if (!File.Exists(created)) File.WriteAllText(created, "");
                files.Add(created);
            }
            catch { /* ignore */ }
        }

        foreach (var path in files)
            WriteUeBorderlessFile(path, sections);
    }

    private static void WriteUeBorderlessFile(string path, IReadOnlyList<string> sections)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            const string mode = "1"; // WindowedFullscreen

            // Always force Engine section
            text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "FullscreenMode", mode);
            text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "LastConfirmedFullscreenMode", mode);
            text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "PreferredFullscreenMode", mode);
            text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "bUseVSync", "False");
            text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "bUseDynamicResolution", "False");

            // Always force every known game section (create if missing)
            foreach (var section in sections.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                text = EnsureSectionLine(text, section, "FullscreenMode", mode);
                text = EnsureSectionLine(text, section, "LastConfirmedFullscreenMode", mode);
                text = EnsureSectionLine(text, section, "PreferredFullscreenMode", mode);
                text = EnsureSectionLine(text, section, "bUseVSync", "False");
                // Letterbox makes exclusive-looking edges — kill when present/created
                text = EnsureSectionLine(text, section, "bShouldLetterbox", "False");
                text = EnsureSectionLine(text, section, "bLastConfirmedShouldLetterbox", "False");
            }

            // Regex sweep any leftover lines
            text = Regex.Replace(text, @"(?im)^(\s*FullscreenMode\s*=\s*).*$", $"${{1}}{mode}");
            text = Regex.Replace(text, @"(?im)^(\s*LastConfirmedFullscreenMode\s*=\s*).*$", $"${{1}}{mode}");
            text = Regex.Replace(text, @"(?im)^(\s*PreferredFullscreenMode\s*=\s*).*$", $"${{1}}{mode}");
            text = Regex.Replace(text, @"(?im)^(\s*bShouldLetterbox\s*=\s*).*$", "${1}False");
            text = Regex.Replace(text, @"(?im)^(\s*bLastConfirmedShouldLetterbox\s*=\s*).*$", "${1}False");

            WriteConfigText(path, text);
        }
        catch { /* one path locked */ }
    }

    private static void ApplyLeagueDisplay(ConfigProbe probe, string windowMode)
    {
        // WindowMode: 0=windowed, 1=borderless, 2=exclusive (League)
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (probe.PrimaryConfig is not null) paths.Add(probe.PrimaryConfig);
        foreach (var f in probe.ConfigFiles) paths.Add(f);
        var found = TryFindLeagueGameCfg();
        if (found is not null) paths.Add(found);
        // Also every game.cfg under Riot Games
        try
        {
            foreach (var root in new[]
                     {
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Riot Games"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Riot Games"),
                         @"C:\Riot Games",
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games"),
                     })
            {
                if (!Directory.Exists(root)) continue;
                foreach (var f in Directory.EnumerateFiles(root, "game.cfg", SearchOption.AllDirectories))
                    paths.Add(f);
            }
        }
        catch { /* ignore */ }

        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var text = File.ReadAllText(path);
                text = EnsureSectionLine(text, "General", "WindowMode", windowMode);
                text = EnsureSectionLine(text, "General", "WaitForVerticalSync", "0");
                WriteConfigText(path, text);
            }
            catch { /* locked */ }
        }
    }

    private static void ApplyApexDisplay(ConfigProbe probe, bool borderless)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (probe.PrimaryConfig is not null) paths.Add(probe.PrimaryConfig);
        foreach (var f in probe.ConfigFiles) paths.Add(f);
        var saved = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Saved Games", "Respawn", "Apex", "local");
        if (Directory.Exists(saved))
        {
            foreach (var name in new[] { "videoconfig.txt", "settings.cfg" })
            {
                var p = Path.Combine(saved, name);
                if (File.Exists(p)) paths.Add(p);
            }
        }

        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var text = File.ReadAllText(path);
                // fullscreen + nowindowborder = borderless (Source)
                text = SetQuotedSetting(text, "setting.fullscreen", "1");
                text = SetQuotedSetting(text, "setting.nowindowborder", borderless ? "1" : "0");
                text = SetQuotedSetting(text, "setting.mat_vsync", "0");
                WriteConfigText(path, text);
            }
            catch { /* locked */ }
        }
    }

    private static void ApplyCs2DisplayEverywhere(ConfigProbe probe)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in probe.ConfigFiles)
        {
            if (f.EndsWith("video.txt", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith("cs2_video.txt", StringComparison.OrdinalIgnoreCase))
                paths.Add(f);
        }
        // Re-scan Steam userdata for every CS2 video file
        try
        {
            foreach (var lib in EnumerateSteamLibraryRoots())
            {
                var userdata = Path.Combine(lib, "userdata");
                if (!Directory.Exists(userdata)) continue;
                foreach (var user in Directory.EnumerateDirectories(userdata))
                {
                    foreach (var name in new[] { "cs2_video.txt", "video.txt" })
                    {
                        var vid = Path.Combine(user, SteamAppIdCs2, "local", "cfg", name);
                        if (File.Exists(vid)) paths.Add(vid);
                    }
                }
            }
        }
        catch { /* ignore */ }

        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var text = File.ReadAllText(path);
                text = SetQuotedSetting(text, "setting.fullscreen", "1");
                text = SetQuotedSetting(text, "setting.nowindowborder", "1");
                text = SetQuotedSetting(text, "setting.mat_vsync", "0");
                WriteConfigText(path, text);
            }
            catch { /* locked */ }
        }
    }

    private static void ApplyHelldivers2Display(ConfigProbe probe, bool borderless)
    {
        var path = probe.PrimaryConfig ?? Helldivers2ConfigPath;
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // Force borderless tokens — create if missing (Arrowhead accepts extras)
        void Force(string key, string value) => text = SetConfigLooseKey(text, key, value);
        Force("fullscreen", borderless ? "false" : "true");
        Force("window_mode", borderless ? "borderless" : "fullscreen");
        Force("screen_mode", borderless ? "borderless" : "fullscreen");
        Force("vsync", "false");
        Force("vsync_enabled", "false");
        WriteConfigText(path, text);
    }

    private async Task<(bool Ok, string Message)> RepairConfigGameAsync(
        string gameId,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report("Restoring configs…");
        try
        {
            await Task.Run(() =>
            {
                RestoreConfigFiles(gameId);
                // CS2: remove autoexec if we created it and no backup
                if (string.Equals(gameId, GameIdCs2, StringComparison.OrdinalIgnoreCase))
                    TryRemoveCs2ExoAutoexec();

                try
                {
                    var marker = MarkerPath(gameId);
                    if (File.Exists(marker)) File.Delete(marker);
                }
                catch { /* ignore */ }

                var state = LoadState();
                if (state.Games.TryGetValue(gameId, out var rec))
                {
                    rec.Preset = null;
                    rec.AppliedUtc = null;
                }
                SaveState(state);
            }, ct).ConfigureAwait(false);

            progress?.Report("Repair complete");
            return (true, $"Exo configs restored for {GameTitle(gameId)}.");
        }
        catch (Exception ex)
        {
            return (false, string.IsNullOrWhiteSpace(ex.Message) ? "Repair failed." : ex.Message);
        }
    }

    private static bool ConfigGameLooksApplied(string gameId, string preset)
    {
        try
        {
            preset = NormalizePreset(preset);
            // Marker is authoritative — first line must match the stored preset (potato vs optimized).
            var marker = MarkerPath(gameId);
            if (File.Exists(marker))
            {
                var lines = File.ReadAllLines(marker);
                var first = lines.Length > 0 ? lines[0].Trim() : "";
                if (string.Equals(first, preset, StringComparison.OrdinalIgnoreCase))
                    return true;
                // Legacy markers: whole-file contains profile= token
                var m = File.ReadAllText(marker);
                if (m.Contains($"profile={preset}", StringComparison.OrdinalIgnoreCase))
                    return true;
                // Marker exists but wrong/empty preset — still count as applied only if preset matches anywhere as sole token
                if (Regex.IsMatch(m, $@"(?im)^\s*{Regex.Escape(preset)}\s*$"))
                    return true;
            }

            var probe = gameId switch
            {
                GameIdBlackOps7 => ProbeBlackOps7(),
                GameIdFortnite => ProbeFortnite(),
                GameIdValorant => ProbeValorant(),
                GameIdLeague => ProbeLeague(),
                GameIdCs2 => ProbeCs2(),
                GameIdApex => ProbeApex(),
                GameIdHelldivers2 => ProbeHelldivers2(),
                GameIdTheFinals => ProbeTheFinals(),
                GameIdPredecessor => ProbePredecessor(),
                _ => new ConfigProbe()
            };
            if (!probe.Installed || string.IsNullOrWhiteSpace(probe.PrimaryConfig) || !File.Exists(probe.PrimaryConfig))
                return false;

            var text = File.ReadAllText(probe.PrimaryConfig);
            // Must match this preset — do not treat "any Exo profile" as Potato when Optimized is stored (and vice versa).
            return text.Contains("Exo Games", StringComparison.OrdinalIgnoreCase)
                   && text.Contains($"profile={preset}", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Write text with retries — COD/Riot lock configs while the client is open.
    /// Uses share-friendly open so readers can hold the file; still fails after retries if exclusive-locked.
    /// </summary>
    private static void WriteConfigText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const int attempts = 10;
        Exception? last = null;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                // Prefer atomic-ish: write temp then replace when possible
                var tmp = path + ".exo-tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    sw.Write(text);
                }

                try
                {
                    File.Copy(tmp, path, overwrite: true);
                    try { File.Delete(tmp); } catch { /* ignore */ }
                    return;
                }
                catch (IOException)
                {
                    // Destination locked — try direct open with share
                    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                    using var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    sw.Write(text);
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

        var name = Path.GetFileName(path);
        var msg = last?.Message ?? "file locked";
        if (msg.Contains("being used", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("in use", StringComparison.OrdinalIgnoreCase) ||
            last is IOException)
        {
            throw new IOException(
                $"Config '{name}' is in use by the game. Fully close Call of Duty / the game (tray + launcher), wait a few seconds, then Apply again. ({msg})");
        }
        throw last ?? new IOException($"Could not write {name}.");
    }

    // ── Backup / restore ──────────────────────────────────────────────────

    private static void BackupConfigFiles(string gameId, IReadOnlyList<string> files)
    {
        var root = BackupRoot(gameId);
        Directory.CreateDirectory(root);
        foreach (var src in files)
        {
            if (string.IsNullOrWhiteSpace(src) || !File.Exists(src)) continue;
            try
            {
                var rel = MakeBackupName(src);
                var dest = Path.Combine(root, rel + ".bak");
                if (!File.Exists(dest))
                    File.Copy(src, dest, overwrite: false);
            }
            catch { /* skip busy */ }
        }
        WriteConfigText(Path.Combine(root, "backup.ok"), DateTimeOffset.UtcNow.ToString("o"));
    }

    private static void RestoreConfigFiles(string gameId)
    {
        var root = BackupRoot(gameId);
        if (!Directory.Exists(root)) return;
        foreach (var bak in Directory.EnumerateFiles(root, "*.bak"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(bak); // strip .bak
                // name is encoded path token
                var original = DecodeBackupName(name);
                if (string.IsNullOrWhiteSpace(original)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                File.Copy(bak, original, overwrite: true);
            }
            catch { /* skip */ }
        }
    }

    private static string MakeBackupName(string fullPath)
    {
        // Stable reversible token (avoid path separators)
        var bytes = Encoding.UTF8.GetBytes(fullPath);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string? DecodeBackupName(string token)
    {
        try
        {
            var s = token.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
        catch { return null; }
    }

    // ── Black Ops 7 ───────────────────────────────────────────────────────

    private static string CodPlayersDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Activision", "Call of Duty", "players");

    private static ConfigProbe ProbeBlackOps7()
    {
        var players = CodPlayersDir;
        if (!Directory.Exists(players))
        {
            // Steam COD HQ install without configs yet
            var install = TryFindSteamApp(SteamAppIdCallOfDuty, "Call of Duty", "Call of Duty HQ");
            return new ConfigProbe
            {
                Installed = install is not null,
                ConfigRoot = install,
                MethodBlurb = "AppData players s.1.0.cod25 configs",
                ConfigFiles = Array.Empty<string>()
            };
        }

        var files = new List<string>();
        foreach (var name in new[] { "s.1.0.cod25.txt0", "s.1.0.cod25.txt1", "s.1.0.cod24.txt0", "s.1.0.cod24.txt1" })
        {
            var p = Path.Combine(players, name);
            if (File.Exists(p)) files.Add(p);
        }
        // Profile-scoped g.cod25*.txt0/1 also hold graphics on some installs
        try
        {
            foreach (var p in Directory.EnumerateFiles(players, "g.cod25*.txt0", SearchOption.AllDirectories))
                files.Add(p);
            foreach (var p in Directory.EnumerateFiles(players, "g.cod25*.txt1", SearchOption.AllDirectories))
                files.Add(p);
        }
        catch { /* ignore */ }

        var primary = files.FirstOrDefault(f =>
            Path.GetFileName(f).StartsWith("s.1.0.cod25", StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault();

        return new ConfigProbe
        {
            Installed = files.Count > 0 || Directory.Exists(players),
            ConfigRoot = players,
            PrimaryConfig = primary,
            MethodBlurb = "cod25 players dvars (Season competitive meta) — RICOCHET untouched",
            ConfigFiles = files
        };
    }

    /// <summary>
    /// Policy for ALL games (do not violate without a title-specific reason):
    /// - Never force DisplayMode / Fullscreen / borderless / resolution / refresh / max FPS.
    ///   Exclusive fullscreen is NOT always best (multi-monitor, alt-tab, overlays).
    /// - Never touch audio, keybinds, crosshair, sens, account data, AC binaries.
    /// - Prefer: VSync off, motion blur/DOF off, quality ladders, frame-gen off, Reflex when present.
    /// </summary>
    private static void ApplyBlackOps7(string preset, ConfigProbe probe, string displayMode = DisplayLeave)
    {
        var potato = preset == PresetPotato;
        var keys = BlackOps7Keys(potato);

        var targets = probe.ConfigFiles.Count > 0
            ? probe.ConfigFiles
            : Array.Empty<string>();

        if (targets.Count == 0)
            throw new InvalidOperationException("No Black Ops 7 config files found under AppData\\Activision\\Call of Duty\\players.");

        var ordered = targets
            .OrderBy(p => Path.GetFileName(p).StartsWith("s.1.0.", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();

        foreach (var path in ordered)
        {
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path);
            text = Regex.Replace(text, @"(?m)^// Exo Games[^\r\n]*\r?\n?", "");
            text = ExoMarkerLine(GameIdBlackOps7, preset) + "\r\n"
                   + "// Quality + latency only — display mode / resolution / FPS cap left alone. RICOCHET untouched.\r\n"
                   + text.TrimStart('\uFEFF', '\r', '\n') + "\r\n";

            foreach (var (key, value) in keys)
                text = SetCodKey(text, key, value);

            WriteConfigText(path, text);
        }

        foreach (var path in ordered.Where(p => p.EndsWith("txt0", StringComparison.OrdinalIgnoreCase)))
        {
            var twin = path[..^1] + "1";
            if (File.Exists(path) && ordered.Any(t => string.Equals(t, twin, StringComparison.OrdinalIgnoreCase)))
            {
                try { File.Copy(path, twin, overwrite: true); } catch { /* busy */ }
            }
        }

        // Display mode after quality writes (re-reads files; tokens are game-specific)
        ApplyDisplayPreference(GameIdBlackOps7, displayMode, probe);
    }

    private static Dictionary<string, string> BlackOps7Keys(bool potato)
    {
        // Live cod25 tokens. Leave DisplayMode, Resolution, RefreshRate, MaxFpsInGame, GPU*, audio alone.
        var workers = Math.Clamp(Environment.ProcessorCount - 1, 2, 16).ToString();
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["VSync"] = "disabled",
            ["VSyncInMenu"] = "disabled",
            ["NvidiaReflex"] = "Enabled + boost",
            // Menu/out-of-focus caps only — never rewrite MaxFpsInGame (user choice)
            ["MaxFpsInMenu"] = "60",
            ["MaxFpsOutOfFocus"] = "30",
            ["DynamicSceneResolution"] = "false",
            ["FSRFrameInterpolation"] = "false",
            ["DLSSFrameGeneration"] = "false",
            ["VRS"] = "true",

            ["DepthOfField"] = "false",
            ["DepthOfFieldQuality"] = "Low",
            ["EnableVelocityBasedBlur"] = "false",
            ["DxrMode"] = "Off",

            // Optimized = competitive visibility (not muddy potato)
            ["GraphicsQuality"] = potato ? "Minimum" : "Basic",
            ["TextureQuality"] = potato ? "3" : "1",
            ["TextureFilter"] = potato ? "aniso 4x" : "aniso 8x",
            ["ShadowQuality"] = potato ? "Very_Low" : "Low",
            ["ScreenSpaceShadowQuality"] = "Off",
            ["ParticleQuality"] = "very low",
            ["ShaderQuality"] = "Low",
            ["ModelQuality"] = potato ? "Low Quality" : "Medium Quality",
            ["SSRQuality"] = "Off",
            ["AmbientLightingQuality"] = potato ? "Off" : "Low",
            ["VolumetricQuality"] = "QUALITY_LOW",
            ["TerrainQuality"] = potato ? "Very Low" : "Low",
            ["WeatherGridVolumesQuality"] = "Off",
            ["WaterCausticsMode"] = "Off",
            ["WaterWaveWetness"] = "false",
            ["DeferredPhysics"] = "Low Quality",
            ["WorldStreamingQuality"] = potato ? "Low" : "High",
            ["VideoMemoryScaleMP"] = potato ? "0.650000" : "0.800000",
            ["RendererWorkerCount"] = workers,
            ["BulletImpacts"] = "true",
            ["PersistentDamageLayer"] = "true",
            ["SustainabilityPauseRendering"] = "true",
        };
    }

    /// <summary>
    /// CoD format: KeyName@ids;ids;ids = value // comment
    /// Updates every line whose key prefix matches (all @ variants).
    /// </summary>
    private static string SetCodKey(string text, string keyName, string newValue)
    {
        // DepthOfField has bool and enum variants — map value carefully per existing value style
        var rx = new Regex(
            $@"(?m)^({Regex.Escape(keyName)}@[^\s=]+)\s*=\s*([^/\r\n]*?)(\s*//[^\r\n]*)?\s*$",
            RegexOptions.IgnoreCase);

        return rx.Replace(text, m =>
        {
            var head = m.Groups[1].Value;
            var oldVal = m.Groups[2].Value.Trim();
            var comment = m.Groups[3].Success ? m.Groups[3].Value : "";
            var val = AdaptCodValue(keyName, newValue, oldVal);
            return $"{head} = {val}{comment}";
        });
    }

    private static string AdaptCodValue(string keyName, string desired, string oldVal)
    {
        // DepthOfField@0 is bool; DepthOfField@1 is Off/On/Script
        if (keyName.Equals("DepthOfField", StringComparison.OrdinalIgnoreCase))
        {
            if (oldVal.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                oldVal.Equals("false", StringComparison.OrdinalIgnoreCase))
                return "false";
            if (oldVal.Contains("On", StringComparison.OrdinalIgnoreCase) ||
                oldVal.Contains("Off", StringComparison.OrdinalIgnoreCase) ||
                oldVal.Contains("Script", StringComparison.OrdinalIgnoreCase))
                return "Off";
        }

        if (keyName.Equals("DxrMode", StringComparison.OrdinalIgnoreCase))
        {
            // Keep Off for both enum styles
            return "Off";
        }

        return desired;
    }

    // ── Fortnite ──────────────────────────────────────────────────────────

    private static string FortniteConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FortniteGame", "Saved", "Config", "WindowsClient");

    private static ConfigProbe ProbeFortnite()
    {
        var dir = FortniteConfigDir;
        var gus = Path.Combine(dir, "GameUserSettings.ini");
        var files = new List<string>();
        if (File.Exists(gus)) files.Add(gus);
        var engine = Path.Combine(dir, "Engine.ini");
        if (File.Exists(engine)) files.Add(engine);

        var installed = files.Count > 0
                        || Directory.Exists(Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "FortniteGame"));

        return new ConfigProbe
        {
            Installed = installed && (files.Count > 0 || Directory.Exists(dir)),
            ConfigRoot = dir,
            PrimaryConfig = files.FirstOrDefault(),
            MethodBlurb = "Ch7 Performance GUS + Engine.ini (Epic AppData)",
            ConfigFiles = files
        };
    }

    /// <summary>
    /// Fortnite Chapter 7 meta (Epic + competitive GUS culture on YT 2026):
    /// Performance-oriented scalability, VSync off, dynamic res off.
    /// Keep 3D resolution quality at 100 for Optimized (Epic competitive guide).
    /// Potato may drop internal res scale. No pak / EasyAntiCheat touch.
    /// </summary>
    private static void ApplyFortnite(string preset, ConfigProbe probe, string displayMode = DisplayLeave)
    {
        var potato = preset == PresetPotato;
        var dir = probe.ConfigRoot ?? FortniteConfigDir;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "GameUserSettings.ini");
        string text = File.Exists(path) ? File.ReadAllText(path) : "";

        text = StripIniExoMarkers(text);
        text = ExoIniMarkerLine(GameIdFortnite, preset) + "\r\n"
               + "; Meta: Chapter 7 competitive GUS — close Fortnite before apply.\r\n"
               + text.TrimStart('\uFEFF');

        // Epic competitive: players stay clear even on Near view distance
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.ViewDistanceQuality", "0");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.AntiAliasingQuality", potato ? "0" : "0");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.ShadowQuality", "0");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.GlobalIlluminationQuality", "0");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.ReflectionQuality", "0");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.PostProcessQuality", "0");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.TextureQuality", potato ? "0" : "1");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.EffectsQuality", "0");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.FoliageQuality", "0");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.ShadingQuality", potato ? "0" : "1");
        // 3D resolution — competitive keeps 100; potato can pull down
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.ResolutionQuality", potato ? "75.000000" : "100.000000");

        const string fort = "/Script/FortniteGame.FortGameUserSettings";
        text = EnsureSectionLine(text, fort, "bUseVSync", "False");
        text = EnsureSectionLine(text, fort, "bUseDynamicResolution", "False");
        text = EnsureSectionLine(text, fort, "bShowFPS", "True");
        text = EnsureSectionLine(text, fort, "FrontendFrameRateLimit", "60");
        // Uncapped match FPS when already uncapped / missing — never clobber a user cap
        if (!Regex.IsMatch(text, @"(?im)^\s*FrameRateLimit\s*=") ||
            Regex.IsMatch(text, @"(?im)^\s*FrameRateLimit\s*=\s*0"))
            text = EnsureSectionLine(text, fort, "FrameRateLimit", "0.000000");
        // Mouse accel off is competitive standard (not a display-mode force)
        text = EnsureSectionLine(text, fort, "bDisableMouseAcceleration", "True");
        text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "bUseVSync", "False");
        // Borderless (1) — also re-walked under FortniteGame after this write
        text = EnsureSectionLine(text, fort, "FullscreenMode", "1");
        text = EnsureSectionLine(text, fort, "LastConfirmedFullscreenMode", "1");
        text = EnsureSectionLine(text, fort, "PreferredFullscreenMode", "1");
        text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "FullscreenMode", "1");
        text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "LastConfirmedFullscreenMode", "1");
        text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "PreferredFullscreenMode", "1");

        WriteConfigText(path, text);

        var enginePath = Path.Combine(dir, "Engine.ini");
        var eng = File.Exists(enginePath) ? File.ReadAllText(enginePath) : "";
        eng = StripIniExoMarkers(eng);
        eng = ExoIniMarkerLine(GameIdFortnite, preset) + "\r\n" + eng.TrimStart('\uFEFF');
        if (!eng.Contains("[SystemSettings]", StringComparison.OrdinalIgnoreCase))
            eng += "\r\n[SystemSettings]\r\n";
        eng = EnsureSectionLine(eng, "SystemSettings", "r.MotionBlurQuality", "0");
        eng = EnsureSectionLine(eng, "SystemSettings", "r.BloomQuality", "0");
        eng = EnsureSectionLine(eng, "SystemSettings", "r.DepthOfFieldQuality", "0");
        eng = EnsureSectionLine(eng, "SystemSettings", "r.DefaultFeature.MotionBlur", "0");
        eng = EnsureSectionLine(eng, "SystemSettings", "r.DefaultFeature.Bloom", "0");
        eng = EnsureSectionLine(eng, "SystemSettings", "r.SceneColorFringeQuality", "0");
        eng = EnsureSectionLine(eng, "SystemSettings", "r.Tonemapper.Quality", "0");
        WriteConfigText(enginePath, eng);

        ApplyDisplayPreference(GameIdFortnite, displayMode, probe);
    }

    // ── Valorant ──────────────────────────────────────────────────────────

    private static ConfigProbe ProbeValorant()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VALORANT", "Saved", "Config");
        var files = new List<string>();
        string? primary = null;
        if (Directory.Exists(baseDir))
        {
            try
            {
                // Real graphics live in RiotUserSettings.ini (not GUS) on current clients.
                foreach (var f in Directory.EnumerateFiles(baseDir, "RiotUserSettings.ini", SearchOption.AllDirectories))
                {
                    files.Add(f);
                    if (primary is null || new FileInfo(f).Length > new FileInfo(primary).Length)
                        primary = f;
                }
                // Always include every GameUserSettings.ini (even short/empty) — display lives here.
                foreach (var f in Directory.EnumerateFiles(baseDir, "GameUserSettings.ini", SearchOption.AllDirectories))
                    files.Add(f);
            }
            catch { /* ignore */ }
        }

        // Ensure global WindowsClient GUS path is always a write target
        var globalGus = Path.Combine(baseDir, "WindowsClient", "GameUserSettings.ini");
        if (!files.Any(f => string.Equals(f, globalGus, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(globalGus)!);
                if (!File.Exists(globalGus))
                    File.WriteAllText(globalGus, "");
                files.Add(globalGus);
            }
            catch { /* ignore */ }
        }

        var installed = files.Count > 0
                        || Directory.Exists(Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "VALORANT"))
                        || Directory.Exists(Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Riot Games", "VALORANT"));

        return new ConfigProbe
        {
            Installed = installed && files.Count > 0,
            ConfigRoot = baseDir,
            PrimaryConfig = primary,
            MethodBlurb = "RiotUserSettings + GUS borderless — Vanguard never touched",
            ConfigFiles = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    /// <summary>
    /// Valorant — graphics in RiotUserSettings.ini; display (borderless) in GameUserSettings.ini.
    /// Always force UE FullscreenMode=1 (WindowedFullscreen). Close client before apply for best stick.
    /// Potato = min quality. Optimized = same lows + AF 4x for clarity.
    /// </summary>
    private static void ApplyValorant(string preset, ConfigProbe probe, string displayMode = DisplayLeave)
    {
        var potato = preset == PresetPotato;
        var tex = "0";
        var mat = "0";
        var detail = "0";
        var ui = "0";
        var aa = "0";
        // AF: 0=1x … Optimized 4x for clearer targets at low cost
        var af = potato ? "0" : "2";
        var reflex = "2"; // On + Boost

        foreach (var path in probe.ConfigFiles)
        {
            if (!File.Exists(path)) continue;
            var name = Path.GetFileName(path);

            if (name.Equals("RiotUserSettings.ini", StringComparison.OrdinalIgnoreCase))
            {
                var text = File.ReadAllText(path);
                text = StripIniExoMarkers(text);
                text = "; Exo Games — valorant profile=" + preset + "\r\n" +
                       Regex.Replace(text.TrimStart('\uFEFF'), @"(?m)^;\s*Exo Games[^\r\n]*\r?\n?", "");

                // Competitive quality ladder (always re-write so game can't leave leftovers)
                text = EnsureSectionLine(text, "Settings", "EAresIntSettingName::TextureQuality", tex);
                text = EnsureSectionLine(text, "Settings", "EAresIntSettingName::MaterialQuality", mat);
                text = EnsureSectionLine(text, "Settings", "EAresIntSettingName::DetailQuality", detail);
                text = EnsureSectionLine(text, "Settings", "EAresIntSettingName::UIQuality", ui);
                text = EnsureSectionLine(text, "Settings", "EAresIntSettingName::AntiAliasing", aa);
                text = EnsureSectionLine(text, "Settings", "EAresIntSettingName::AnisotropicFiltering", af);
                text = EnsureSectionLine(text, "Settings", "EAresIntSettingName::BloomQuality", "0");
                text = EnsureSectionLine(text, "Settings", "EAresIntSettingName::NvidiaReflexLowLatencySetting", reflex);
                text = EnsureSectionLine(text, "Settings", "EAresBoolSettingName::ShadowsEnabled", "False");
                text = EnsureSectionLine(text, "Settings", "EAresBoolSettingName::VignetteEnabled", "False");
                text = EnsureSectionLine(text, "Settings", "EAresBoolSettingName::DisableDistortion", "True");
                text = EnsureSectionLine(text, "Settings", "EAresBoolSettingName::LimitFramerateOnBattery", "False");
                text = EnsureSectionLine(text, "Settings", "EAresBoolSettingName::LimitFramerateInMenu", "False");
                text = EnsureSectionLine(text, "Settings", "EAresBoolSettingName::LimitFramerateInBackground", "False");
                text = EnsureSectionLine(text, "Settings", "EAresIntSettingName::PlayerPerfShowFrameRate", "1");
                text = EnsureSectionLine(text, "Settings", "EAresBoolSettingName::MultithreadedRendering", "True");
                // Competitive visual clutter
                text = EnsureSectionLine(text, "Settings", "EAresBoolSettingName::ShowBulletTracers", "False");
                text = EnsureSectionLine(text, "Settings", "EAresBoolSettingName::ShowBlood", "False");
                text = EnsureSectionLine(text, "Settings", "EAresBoolSettingName::ShowBloodVisual", "False");
                text = EnsureSectionLine(text, "Settings", "EAresBoolSettingName::ShowCorpses", "False");
                text = EnsureSectionLine(text, "Settings", "EAresBoolSettingName::ShowCorpsesVisual", "False");
                text = EnsureSectionLine(text, "Settings", "EAresBoolSettingName::EnableInstabilityIndicators", "False");

                WriteConfigText(path, text);
                continue;
            }

            if (name.Equals("GameUserSettings.ini", StringComparison.OrdinalIgnoreCase))
                WriteValorantGameUserSettings(path, preset);
        }

        // Always walk every GUS under Config again (display is critical; probe list can miss new folders).
        ApplyValorantBorderlessEverywhere(probe.ConfigRoot);
        _ = displayMode;
    }

    /// <summary>
    /// Force WindowedFullscreen (borderless) on every Valorant GameUserSettings.ini.
    /// UE: 0=exclusive fullscreen, 1=borderless, 2=windowed.
    /// </summary>
    private static void ApplyValorantBorderlessEverywhere(string? configRoot)
    {
        if (string.IsNullOrWhiteSpace(configRoot) || !Directory.Exists(configRoot)) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(configRoot, "GameUserSettings.ini", SearchOption.AllDirectories))
                WriteValorantGameUserSettings(path, preset: null);
        }
        catch { /* busy */ }
    }

    private static void WriteValorantGameUserSettings(string path, string? preset)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            text = StripIniExoMarkers(text);
            if (!string.IsNullOrWhiteSpace(preset))
                text = ExoIniMarkerLine(GameIdValorant, preset) + "\r\n" + text.TrimStart('\uFEFF');

            const string shooter = "/Script/ShooterGame.ShooterGameUserSettings";
            const string engine = "/Script/Engine.GameUserSettings";

            // Always create the ShooterGame section so empty/stub GUS still get display.
            text = EnsureSectionLine(text, shooter, "bUseVSync", "False");
            text = EnsureSectionLine(text, shooter, "bUseDynamicResolution", "False");
            text = EnsureSectionLine(text, shooter, "FrameRateLimit", "0.000000");
            // Borderless / windowed fullscreen
            text = EnsureSectionLine(text, shooter, "FullscreenMode", "1");
            text = EnsureSectionLine(text, shooter, "LastConfirmedFullscreenMode", "1");
            text = EnsureSectionLine(text, shooter, "PreferredFullscreenMode", "1");
            // Letterbox + exclusive-looking scaling off
            text = EnsureSectionLine(text, shooter, "bShouldLetterbox", "False");
            text = EnsureSectionLine(text, shooter, "bLastConfirmedShouldLetterbox", "False");

            text = EnsureSectionLine(text, engine, "bUseDesiredScreenHeight", "False");
            // Patch any leftover FullscreenMode lines anywhere in the file
            text = Regex.Replace(text, @"(?im)^(\s*FullscreenMode\s*=\s*).*$", "${1}1");
            text = Regex.Replace(text, @"(?im)^(\s*LastConfirmedFullscreenMode\s*=\s*).*$", "${1}1");
            text = Regex.Replace(text, @"(?im)^(\s*PreferredFullscreenMode\s*=\s*).*$", "${1}1");
            text = Regex.Replace(text, @"(?im)^(\s*bShouldLetterbox\s*=\s*).*$", "${1}False");
            text = Regex.Replace(text, @"(?im)^(\s*bLastConfirmedShouldLetterbox\s*=\s*).*$", "${1}False");

            WriteConfigText(path, text);
        }
        catch
        {
            /* one path locked — others may still write */
        }
    }

    /// <summary>Read live Valorant graphics + display for honest feature tiles.</summary>
    private static List<OptimizerFeatureInfo> BuildValorantLiveFeatures(string? primaryConfig, bool applied, string presetLabel)
    {
        var list = new List<OptimizerFeatureInfo>();
        if (string.IsNullOrWhiteSpace(primaryConfig) || !File.Exists(primaryConfig))
            return list;

        string Get(string key, string fallback = "?")
        {
            try
            {
                var t = File.ReadAllText(primaryConfig);
                var m = Regex.Match(t, $@"(?im)^\s*{Regex.Escape(key)}\s*=\s*(.+?)\s*$");
                return m.Success ? m.Groups[1].Value.Trim() : fallback;
            }
            catch { return fallback; }
        }

        static string QualLabel(string v) => v switch
        {
            "0" => "Low",
            "1" => "Med",
            "2" => "High",
            _ => v
        };

        var tex = Get("EAresIntSettingName::TextureQuality");
        var mat = Get("EAresIntSettingName::MaterialQuality");
        var det = Get("EAresIntSettingName::DetailQuality");
        var sh = Get("EAresBoolSettingName::ShadowsEnabled");
        var bloom = Get("EAresIntSettingName::BloomQuality");
        var reflex = Get("EAresIntSettingName::NvidiaReflexLowLatencySetting");
        var af = Get("EAresIntSettingName::AnisotropicFiltering");
        var aa = Get("EAresIntSettingName::AntiAliasing");
        var menuCap = Get("EAresBoolSettingName::LimitFramerateInMenu");

        bool IsCompLow() =>
            tex is "0" && mat is "0" && det is "0" &&
            sh.Equals("False", StringComparison.OrdinalIgnoreCase) &&
            bloom is "0";

        // Live display from any GUS under VALORANT
        var (fsMode, letterbox) = ReadValorantDisplayLive();
        var borderlessOk = fsMode is "1";
        var displayLabel = fsMode switch
        {
            "0" => "Exclusive fullscreen",
            "1" => "Borderless (windowed fullscreen)",
            "2" => "Windowed",
            _ => $"Unknown ({fsMode})"
        };

        list.Add(F("Display mode",
            borderlessOk
                ? $"{displayLabel} · letterbox={letterbox}"
                : $"{displayLabel} — Apply sets borderless. Close Valorant first.",
            borderlessOk));
        list.Add(F("Texture / Material / Detail",
            $"{QualLabel(tex)} / {QualLabel(mat)} / {QualLabel(det)}",
            tex is "0" && mat is "0" && det is "0"));
        list.Add(F("Shadows / Bloom / AA",
            $"sh={sh} bloom={bloom} aa={aa}",
            sh.Equals("False", StringComparison.OrdinalIgnoreCase) && bloom is "0" && aa is "0"));
        list.Add(F("NVIDIA Reflex",
            reflex is "2" ? "On + Boost" : reflex is "1" ? "On" : $"value {reflex}",
            reflex is "2" or "1"));
        list.Add(F("FPS limits off (menu/bg/battery)",
            menuCap.Equals("False", StringComparison.OrdinalIgnoreCase) ? "Uncapped menus OK" : "Check limits",
            menuCap.Equals("False", StringComparison.OrdinalIgnoreCase)));
        list.Add(F("Already competitive lows?",
            IsCompLow()
                ? "Yes — quality already min; Apply still forces borderless + profile mark"
                : "No — Apply will lower quality keys",
            true)); // informational — never fails Applied status
        list.Add(F("Exo profile",
            applied ? $"{presetLabel} marked" : "Not marked yet",
            applied));
        list.Add(F("Vanguard", "Never touched by Exo", true));
        return list;
    }

    private static (string FullscreenMode, string Letterbox) ReadValorantDisplayLive()
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VALORANT", "Saved", "Config");
            if (!Directory.Exists(root)) return ("?", "?");
            foreach (var path in Directory.EnumerateFiles(root, "GameUserSettings.ini", SearchOption.AllDirectories)
                         .OrderByDescending(p => new FileInfo(p).Length))
            {
                if (new FileInfo(path).Length < 20) continue;
                var t = File.ReadAllText(path);
                var m = Regex.Match(t, @"(?im)^\s*FullscreenMode\s*=\s*(\d+)");
                var lb = Regex.Match(t, @"(?im)^\s*bShouldLetterbox\s*=\s*(\w+)");
                if (m.Success)
                    return (m.Groups[1].Value, lb.Success ? lb.Groups[1].Value : "?");
            }
        }
        catch { /* ignore */ }
        return ("?", "?");
    }

    // ── League of Legends ─────────────────────────────────────────────────

    private static string? TryFindLeagueGameCfg()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Riot Games", "League of Legends", "Config", "game.cfg"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Riot Games", "League of Legends", "Config", "game.cfg"),
            @"C:\Riot Games\League of Legends\Config\game.cfg",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Riot Games", "League of Legends", "Config", "game.cfg"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        // Search LocalAppData Riot for game.cfg
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games");
            if (Directory.Exists(root))
            {
                foreach (var f in Directory.EnumerateFiles(root, "game.cfg", SearchOption.AllDirectories))
                    return f;
            }
        }
        catch { /* ignore */ }

        return null;
    }

    private static ConfigProbe ProbeLeague()
    {
        var cfg = TryFindLeagueGameCfg();
        var installDir = cfg is not null
            ? Path.GetDirectoryName(Path.GetDirectoryName(cfg)) // …\League of Legends
            : Directory.Exists(@"C:\Riot Games\League of Legends")
                ? @"C:\Riot Games\League of Legends"
                : null;
        var files = new List<string>();
        if (cfg is not null) files.Add(cfg);

        return new ConfigProbe
        {
            Installed = cfg is not null || installDir is not null,
            ConfigRoot = cfg is not null ? Path.GetDirectoryName(cfg) : installDir,
            PrimaryConfig = cfg,
            MethodBlurb = "game.cfg [Performance] + General VSync (no Vanguard)",
            ConfigFiles = files
        };
    }

    /// <summary>
    /// League — Config\game.cfg only (official). Leave WindowMode alone (0/1/2 is user display choice).
    /// Leave FrameCapType alone (enum is version-sensitive; wrong value can soft-cap FPS badly).
    /// </summary>
    private static void ApplyLeague(string preset, ConfigProbe probe, string displayMode = DisplayLeave)
    {
        var potato = preset == PresetPotato;
        var path = probe.PrimaryConfig ?? TryFindLeagueGameCfg();
        if (path is null || !File.Exists(path))
            throw new InvalidOperationException("League game.cfg not found. Launch League once first.");

        var text = File.ReadAllText(path);
        text = StripIniExoMarkers(text);
        text = "; Exo Games — league-of-legends profile=" + preset + "\r\n"
               + "; Performance + borderless WindowMode=1. FrameCapType left alone.\r\n"
               + text.TrimStart('\uFEFF');

        text = EnsureSectionLine(text, "General", "WaitForVerticalSync", "0");
        text = EnsureSectionLine(text, "General", "HideEyeCandy", "1");
        text = EnsureSectionLine(text, "General", "ShowGodray", "0");
        text = EnsureSectionLine(text, "General", "EnableLightFx", "0");
        text = EnsureSectionLine(text, "General", "MinimizeCameraMotion", "1");

        text = EnsureSectionLine(text, "Performance", "ShadowQuality", "0");
        text = EnsureSectionLine(text, "Performance", "EnvironmentQuality", "0");
        text = EnsureSectionLine(text, "Performance", "EffectsQuality", "0");
        text = EnsureSectionLine(text, "Performance", "CharacterQuality", potato ? "0" : "1");
        text = EnsureSectionLine(text, "Performance", "EnableFXAA", "0");
        text = EnsureSectionLine(text, "Performance", "EnableHUDAnimations", potato ? "0" : "1");

        WriteConfigText(path, text);
        ApplyDisplayPreference(GameIdLeague, displayMode, probe);
    }

    // ── Predecessor ───────────────────────────────────────────────────────

    private static ConfigProbe ProbePredecessor()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Predecessor", "Saved", "Config", "WindowsClient");
        var gus = Path.Combine(dir, "GameUserSettings.ini");
        var eng = Path.Combine(dir, "Engine.ini");
        var files = new List<string>();
        if (File.Exists(gus)) files.Add(gus);
        if (File.Exists(eng)) files.Add(eng);

        var install = TryFindSteamApp(SteamAppIdPredecessor, "Predecessor");
        return new ConfigProbe
        {
            Installed = files.Count > 0 || install is not null,
            ConfigRoot = Directory.Exists(dir) ? dir : install,
            PrimaryConfig = files.FirstOrDefault(),
            MethodBlurb = "UE GameUserSettings scalability (Omeda / Steam)",
            ConfigFiles = files
        };
    }

    /// <summary>
    /// Predecessor — biggest win is ScalabilityGroups (often default High=3).
    /// Never touch FullscreenMode. Never force DLSS off (user upscaler choice).
    /// Frame-gen off is fine (adds latency). Reflex Boost when the key exists.
    /// </summary>
    private static void ApplyPredecessor(string preset, ConfigProbe probe, string displayMode = DisplayLeave)
    {
        var potato = preset == PresetPotato;
        var dir = probe.ConfigRoot;
        if (string.IsNullOrWhiteSpace(dir) || !dir.Contains("Config", StringComparison.OrdinalIgnoreCase))
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Predecessor", "Saved", "Config", "WindowsClient");
        }
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "GameUserSettings.ini");
        if (!File.Exists(path) && probe.PrimaryConfig is not null)
            path = probe.PrimaryConfig;
        if (!File.Exists(path))
            throw new InvalidOperationException("Predecessor GameUserSettings.ini not found. Launch the game once first.");

        var text = File.ReadAllText(path);
        text = StripIniExoMarkers(text);
        text = ExoIniMarkerLine(GameIdPredecessor, preset) + "\r\n"
               + "; Scalability + borderless + latency. DLSS mode left alone.\r\n"
               + text.TrimStart('\uFEFF');

        const string lyra = "/Script/Predecessor.LyraSettingsLocal";
        text = EnsureSectionLine(text, lyra, "bUseVSync", "False");
        text = EnsureSectionLine(text, lyra, "bUseDynamicResolution", "False");
        text = EnsureSectionLine(text, lyra, "FullscreenMode", "1");
        text = EnsureSectionLine(text, lyra, "LastConfirmedFullscreenMode", "1");
        text = EnsureSectionLine(text, lyra, "PreferredFullscreenMode", "1");
        text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "FullscreenMode", "1");
        text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "LastConfirmedFullscreenMode", "1");
        text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "PreferredFullscreenMode", "1");
        text = EnsureSectionLine(text, lyra, "bUseMotionBlur", "False");
        text = EnsureSectionLine(text, lyra, "ReflexMode", "Boost");
        text = EnsureSectionLine(text, lyra, "FrameEnhancementMode", "Off");
        text = EnsureSectionLine(text, lyra, "DLSSGMode", "Off");
        text = EnsureSectionLine(text, lyra, "bFsrFgEnabled", "False");
        // Do NOT set DLSSMode — Quality/Balanced/Performance is hardware preference

        // Optimized keeps a bit of view distance for a MOBA-like read of the map
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.ViewDistanceQuality", potato ? "0" : "2");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.AntiAliasingQuality", potato ? "0" : "1");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.ShadowQuality", potato ? "0" : "1");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.GlobalIlluminationQuality", "0");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.ReflectionQuality", "0");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.PostProcessQuality", potato ? "0" : "1");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.TextureQuality", potato ? "0" : "2");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.EffectsQuality", potato ? "0" : "1");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.FoliageQuality", potato ? "0" : "1");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.ShadingQuality", potato ? "0" : "1");
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.LandscapeQuality", potato ? "0" : "1");
        // Never pull internal 3D resolution below 100 on Optimized (blurs targets)
        text = EnsureSectionLine(text, "ScalabilityGroups", "sg.ResolutionQuality", potato ? "85" : "100");

        WriteConfigText(path, text);

        var engPath = Path.Combine(dir, "Engine.ini");
        var eng = File.Exists(engPath) ? File.ReadAllText(engPath) : "";
        eng = StripIniExoMarkers(eng);
        eng = ExoIniMarkerLine(GameIdPredecessor, preset) + "\r\n" + eng.TrimStart('\uFEFF');
        if (!eng.Contains("[SystemSettings]", StringComparison.OrdinalIgnoreCase))
            eng += "\r\n[SystemSettings]\r\n";
        eng = EnsureSectionLine(eng, "SystemSettings", "r.MotionBlurQuality", "0");
        eng = EnsureSectionLine(eng, "SystemSettings", "r.BloomQuality", "0");
        eng = EnsureSectionLine(eng, "SystemSettings", "r.DepthOfFieldQuality", "0");
        eng = EnsureSectionLine(eng, "SystemSettings", "r.DefaultFeature.MotionBlur", "0");
        eng = EnsureSectionLine(eng, "SystemSettings", "r.VolumetricFog", "0");
        WriteConfigText(engPath, eng);

        ApplyDisplayPreference(GameIdPredecessor, displayMode, probe);
    }

    // ── Helldivers 2 ──────────────────────────────────────────────────────

    private static string Helldivers2ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Arrowhead", "Helldivers2", "user_settings.config");

    private static ConfigProbe ProbeHelldivers2()
    {
        var path = Helldivers2ConfigPath;
        var install = TryFindSteamApp(SteamAppIdHelldivers2, "Helldivers 2", "Helldivers2");
        var exists = File.Exists(path);
        return new ConfigProbe
        {
            Installed = exists || install is not null,
            ConfigRoot = Path.GetDirectoryName(path),
            PrimaryConfig = exists ? path : null,
            MethodBlurb = "Arrowhead user_settings.config (existing keys only)",
            ConfigFiles = exists ? new[] { path } : Array.Empty<string>()
        };
    }

    /// <summary>
    /// Helldivers 2 — AppData\Roaming\Arrowhead\Helldivers2\user_settings.config
    /// Community (Steam guide + wiki): cut particles/volumetrics/SSAO first; shadows mid for potato vs playable.
    /// </summary>
    private static void ApplyHelldivers2(string preset, ConfigProbe probe, string displayMode = DisplayLeave)
    {
        var potato = preset == PresetPotato;
        var path = probe.PrimaryConfig ?? Helldivers2ConfigPath;
        if (!File.Exists(path))
            throw new InvalidOperationException("Helldivers 2 user_settings.config not found. Launch the game once first.");

        var text = File.ReadAllText(path);
        text = Regex.Replace(text, @"(?m)^// Exo Games[^\r\n]*\r?\n?", "");
        text = ExoMarkerLine(GameIdHelldivers2, preset) + "\n"
               + "// Meta: Arrowhead user_settings — heavy FX off first.\n"
               + text.TrimStart('\uFEFF');

        // Only rewrite keys that already exist (don't invent unknown keys that may crash parsers)
        void Try(string key, string value)
        {
            if (Regex.IsMatch(text, $@"(?im)^\s*{Regex.Escape(key)}\s*="))
                text = SetConfigLooseKey(text, key, value);
        }

        Try("particle_quality", potato ? "0" : "1");
        Try("lighting_quality", potato ? "0" : "1");
        Try("shadow_quality", potato ? "0" : "1");
        Try("texture_quality", potato ? "0" : "1");
        Try("volumetric_fog_quality", "0");
        Try("volumetrics_quality", "0");
        Try("ssao_quality", "0");
        Try("aa_quality", potato ? "0" : "1");
        Try("gi_quality", "0");
        Try("vegetation_quality", potato ? "0" : "1");
        Try("terrain_quality", potato ? "0" : "1");
        Try("environment_detail", potato ? "0" : "1");
        Try("ragdoll_quality", potato ? "0" : "1");
        Try("motion_blur", "false");
        Try("dof", "false");
        Try("vsync", "false");
        Try("vsync_enabled", "false");
        Try("async_compute", "true");

        WriteConfigText(path, text);
        ApplyDisplayPreference(GameIdHelldivers2, displayMode, probe);
    }

    // ── Apex Legends ──────────────────────────────────────────────────────

    private static ConfigProbe ProbeApex()
    {
        var saved = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Saved Games", "Respawn", "Apex", "local");
        var videoconfig = Path.Combine(saved, "videoconfig.txt");
        var install = TryFindSteamApp(SteamAppIdApex, "Apex Legends", "ApexLegends");
        var files = new List<string>();
        if (File.Exists(videoconfig)) files.Add(videoconfig);

        // Also check under Steam userdata profile autoexec paths later if needed
        return new ConfigProbe
        {
            Installed = files.Count > 0 || install is not null,
            ConfigRoot = saved,
            PrimaryConfig = files.FirstOrDefault(),
            MethodBlurb = "Respawn videoconfig.txt quality ladder",
            ConfigFiles = files
        };
    }

    /// <summary>
    /// Apex — %UserProfile%\Saved Games\Respawn\Apex\local\videoconfig.txt
    /// Competitive ladder: lower mat_picmip / particles / ragdolls; keep model detail readable.
    /// Does not touch EAC.
    /// </summary>
    private static void ApplyApex(string preset, ConfigProbe probe, string displayMode = DisplayLeave)
    {
        var potato = preset == PresetPotato;
        var path = probe.PrimaryConfig;
        if (path is null || !File.Exists(path))
            throw new InvalidOperationException("Apex videoconfig.txt not found. Launch Apex once first.");

        var text = File.ReadAllText(path);
        text = Regex.Replace(text, @"(?m)^// Exo Games[^\r\n]*\r?\n?", "");
        text = ExoMarkerLine(GameIdApex, preset) + "\n"
               + "// Meta: Respawn videoconfig — only keys already present are preferred.\n"
               + text.TrimStart('\uFEFF');

        void SetIfPresent(string key, string value)
        {
            if (text.Contains($"\"{key}\"", StringComparison.OrdinalIgnoreCase) ||
                text.Contains(key, StringComparison.OrdinalIgnoreCase))
                text = SetQuotedSetting(text, key, value);
            else
                text = SetQuotedSetting(text, key, value); // append if missing (videoconfig tolerates extras)
        }

        // mat_picmip: higher = blurrier textures (0 sharp … 4 potato)
        SetIfPresent("setting.mat_picmip", potato ? "4" : "1");
        SetIfPresent("setting.streamlined", "1");
        SetIfPresent("setting.cl_particle_fallback_base", potato ? "3" : "0");
        SetIfPresent("setting.cl_particle_fallback_multiplier", potato ? "2" : "1");
        SetIfPresent("setting.cl_ragdoll_maxcount", "0");
        SetIfPresent("setting.csm_enabled", potato ? "0" : "1");
        SetIfPresent("setting.shadow_depth_res", potato ? "512" : "1024");
        SetIfPresent("setting.shadow_viewmodel_res", potato ? "256" : "512");
        SetIfPresent("setting.modeldetail", potato ? "0" : "1");
        SetIfPresent("setting.r_lod_switch_scale", potato ? "0.6" : "0.9");
        SetIfPresent("setting.ssao", "0");
        SetIfPresent("setting.ssr", "0");
        SetIfPresent("setting.dvs_enable", "0");
        // Display mode only via ApplyDisplayPreference when user asks

        WriteConfigText(path, text);
        ApplyDisplayPreference(GameIdApex, displayMode, probe);
    }

    // ── CS2 ───────────────────────────────────────────────────────────────

    private static ConfigProbe ProbeCs2()
    {
        var install = TryFindSteamApp(SteamAppIdCs2, "Counter-Strike Global Offensive", "Counter-Strike 2");
        var files = new List<string>();
        string? primary = null;
        string? cfgDir = null;

        if (install is not null)
        {
            cfgDir = Path.Combine(install, "game", "csgo", "cfg");
            if (!Directory.Exists(cfgDir))
                cfgDir = Path.Combine(install, "csgo", "cfg");
            if (Directory.Exists(cfgDir))
            {
                var video = Path.Combine(cfgDir, "cs2_video.txt");
                if (File.Exists(video)) { files.Add(video); primary = video; }
                var auto = Path.Combine(cfgDir, "autoexec.cfg");
                if (File.Exists(auto)) files.Add(auto);
            }
        }

        // Steam userdata video settings
        try
        {
            foreach (var lib in EnumerateSteamLibraryRoots())
            {
                var userdata = Path.Combine(lib, "userdata");
                if (!Directory.Exists(userdata)) continue;
                foreach (var user in Directory.EnumerateDirectories(userdata))
                {
                    var vid = Path.Combine(user, SteamAppIdCs2, "local", "cfg", "cs2_video.txt");
                    if (File.Exists(vid))
                    {
                        files.Add(vid);
                        primary ??= vid;
                    }
                    var vid2 = Path.Combine(user, SteamAppIdCs2, "local", "cfg", "video.txt");
                    if (File.Exists(vid2))
                    {
                        files.Add(vid2);
                        primary ??= vid2;
                    }
                }
            }
        }
        catch { /* ignore */ }

        return new ConfigProbe
        {
            Installed = install is not null || files.Count > 0,
            ConfigRoot = cfgDir ?? install,
            PrimaryConfig = primary,
            MethodBlurb = "cs2_video.txt + 2026 autoexec (stock cvars only)",
            ConfigFiles = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    /// <summary>
    /// CS2 2026 meta: video settings live in cs2_video.txt / userdata (not old CSGO cfg for most graphics).
    /// Autoexec only holds still-valid stock cvars (fps_max, HUD/perf helpers). Avoid banned/removed junk.
    /// Shadows often kept Medium for competitive visibility — potato drops them.
    /// </summary>
    private static void ApplyCs2(string preset, ConfigProbe probe, string displayMode = DisplayLeave)
    {
        var potato = preset == PresetPotato;
        var install = TryFindSteamApp(SteamAppIdCs2, "Counter-Strike Global Offensive", "Counter-Strike 2");
        var cfgDir = probe.ConfigRoot;
        if (cfgDir is null || !Directory.Exists(cfgDir) || !cfgDir.EndsWith("cfg", StringComparison.OrdinalIgnoreCase))
        {
            if (install is not null)
            {
                cfgDir = Path.Combine(install, "game", "csgo", "cfg");
                if (!Directory.Exists(cfgDir))
                    cfgDir = Path.Combine(install, "csgo", "cfg");
            }
        }

        if (cfgDir is null || !Directory.Exists(cfgDir))
            throw new InvalidOperationException("CS2 cfg folder not found. Install CS2 on Steam first.");

        Directory.CreateDirectory(cfgDir);
        var autoexec = Path.Combine(cfgDir, "autoexec.cfg");
        BackupConfigFiles(GameIdCs2, File.Exists(autoexec) ? new[] { autoexec } : Array.Empty<string>());

        var sb = new StringBuilder();
        sb.AppendLine($"// Exo Games — {GameIdCs2} profile={(potato ? "potato" : "optimized")}");
        sb.AppendLine("// Stock cvars only. No display-mode force. No removed CSGO junk.");
        // Uncapped gameplay FPS is standard; UI cap saves desktop GPU when in menus
        sb.AppendLine("fps_max 0");
        sb.AppendLine("fps_max_ui 120");
        sb.AppendLine("r_player_visibility_mode 1");
        sb.AppendLine("cl_allow_animated_avatars false");
        sb.AppendLine("cl_autohelp false");
        sb.AppendLine("cl_hide_avatar_images 1");
        sb.AppendLine("engine_low_latency_sleep_after_client_tick true");
        sb.AppendLine("r_show_build_info false");
        sb.AppendLine("cl_teamcounter_playercount_instead_of_avatars true");
        sb.AppendLine("host_writeconfig");
        WriteConfigText(autoexec, sb.ToString());

        foreach (var path in probe.ConfigFiles.Where(f =>
                     f.EndsWith("video.txt", StringComparison.OrdinalIgnoreCase) ||
                     f.EndsWith("cs2_video.txt", StringComparison.OrdinalIgnoreCase)))
        {
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path);
            // Never blank VendorID/DeviceID — game ignores the whole file
            text = Regex.Replace(text, @"(?m)^// Exo Games[^\r\n]*\r?\n?", "");
            text = ExoMarkerLine(GameIdCs2, preset) + "\n"
                   + "// Video quality only — fullscreen / resolution left alone.\n"
                   + text.TrimStart('\uFEFF');
            text = SetQuotedSetting(text, "setting.mat_vsync", "0");
            text = SetQuotedSetting(text, "setting.msaa_samples", "0");
            text = SetQuotedSetting(text, "setting.shaderquality", potato ? "0" : "1");
            // Shadows help peeks — Optimized keeps a step; potato dumps them
            text = SetQuotedSetting(text, "setting.shadowquality", potato ? "0" : "1");
            text = SetQuotedSetting(text, "setting.texture_memory", potato ? "0" : "2");
            // Only touch CMAA if the key already exists
            if (text.Contains("r_csgo_cmaa_enable", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("setting.r_csgo_cmaa_enable", StringComparison.OrdinalIgnoreCase))
                text = SetQuotedSetting(text, "setting.r_csgo_cmaa_enable", "0");
            WriteConfigText(path, text);
        }

        ApplyDisplayPreference(GameIdCs2, displayMode, probe);
    }

    private static void TryRemoveCs2ExoAutoexec()
    {
        try
        {
            var install = TryFindSteamApp(SteamAppIdCs2, "Counter-Strike Global Offensive", "Counter-Strike 2");
            if (install is null) return;
            foreach (var cfgDir in new[]
                     {
                         Path.Combine(install, "game", "csgo", "cfg"),
                         Path.Combine(install, "csgo", "cfg"),
                     })
            {
                var auto = Path.Combine(cfgDir, "autoexec.cfg");
                if (!File.Exists(auto)) continue;
                var text = File.ReadAllText(auto);
                if (text.Contains("Exo Games", StringComparison.OrdinalIgnoreCase))
                {
                    // Prefer restore from backup; else delete exo-only autoexec
                    var root = BackupRoot(GameIdCs2);
                    var restored = false;
                    if (Directory.Exists(root))
                    {
                        foreach (var bak in Directory.EnumerateFiles(root, "*.bak"))
                        {
                            var orig = DecodeBackupName(Path.GetFileNameWithoutExtension(bak));
                            if (orig is not null &&
                                string.Equals(orig, auto, StringComparison.OrdinalIgnoreCase))
                            {
                                File.Copy(bak, auto, overwrite: true);
                                restored = true;
                                break;
                            }
                        }
                    }
                    if (!restored)
                        File.Delete(auto);
                }
            }
        }
        catch { /* ignore */ }
    }

    // ── The Finals ────────────────────────────────────────────────────────

    private static ConfigProbe ProbeTheFinals()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(local, "Discovery", "Saved", "Config", "WindowsClient"),
            Path.Combine(local, "Discovery", "Saved", "Config", "Windows"),
            Path.Combine(local, "TheFinals", "Saved", "Config", "WindowsClient"),
            Path.Combine(local, "Embark", "TheFinals", "Saved", "Config", "WindowsClient"),
        };

        var files = new List<string>();
        string? root = null;
        foreach (var dir in candidates)
        {
            var gus = Path.Combine(dir, "GameUserSettings.ini");
            if (File.Exists(gus))
            {
                files.Add(gus);
                root ??= dir;
            }
            var eng = Path.Combine(dir, "Engine.ini");
            if (File.Exists(eng)) files.Add(eng);
        }

        var install = TryFindSteamApp(SteamAppIdTheFinals, "The Finals", "TheFinals");
        return new ConfigProbe
        {
            Installed = files.Count > 0 || install is not null,
            ConfigRoot = root ?? install,
            PrimaryConfig = files.FirstOrDefault(),
            MethodBlurb = "Embark UE scalability (destruction maps / low GI)",
            ConfigFiles = files
        };
    }

    /// <summary>
    /// The Finals (Embark UE5) — Season performance guides: cut GI/reflections/post first,
    /// keep view distance a step up for playable Optimized, kill motion blur always.
    /// </summary>
    private static void ApplyTheFinals(string preset, ConfigProbe probe, string displayMode = DisplayLeave)
    {
        var potato = preset == PresetPotato;
        if (probe.ConfigFiles.Count == 0)
            throw new InvalidOperationException("The Finals configs not found. Launch the game once first.");

        foreach (var path in probe.ConfigFiles.Where(f =>
                     f.EndsWith("GameUserSettings.ini", StringComparison.OrdinalIgnoreCase)))
        {
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            text = StripIniExoMarkers(text);
            text = ExoIniMarkerLine(GameIdTheFinals, preset) + "\r\n"
                   + "; Meta: Embark UE scalability — destruction-heavy maps need low effects.\r\n"
                   + text.TrimStart('\uFEFF');

            // View distance helps fights in open arenas; potato still dumps it
            text = EnsureSectionLine(text, "ScalabilityGroups", "sg.ViewDistanceQuality", potato ? "0" : "2");
            text = EnsureSectionLine(text, "ScalabilityGroups", "sg.AntiAliasingQuality", potato ? "0" : "1");
            text = EnsureSectionLine(text, "ScalabilityGroups", "sg.ShadowQuality", potato ? "0" : "1");
            text = EnsureSectionLine(text, "ScalabilityGroups", "sg.GlobalIlluminationQuality", "0");
            text = EnsureSectionLine(text, "ScalabilityGroups", "sg.ReflectionQuality", "0");
            text = EnsureSectionLine(text, "ScalabilityGroups", "sg.PostProcessQuality", "0");
            text = EnsureSectionLine(text, "ScalabilityGroups", "sg.TextureQuality", potato ? "0" : "2");
            text = EnsureSectionLine(text, "ScalabilityGroups", "sg.EffectsQuality", potato ? "0" : "1");
            text = EnsureSectionLine(text, "ScalabilityGroups", "sg.FoliageQuality", "0");
            text = EnsureSectionLine(text, "ScalabilityGroups", "sg.ShadingQuality", potato ? "0" : "1");
            text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "bUseVSync", "False");
            text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "bUseDynamicResolution", "False");
            text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "FullscreenMode", "1");
            text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "LastConfirmedFullscreenMode", "1");
            text = EnsureSectionLine(text, "/Script/Engine.GameUserSettings", "PreferredFullscreenMode", "1");
            text = EnsureSectionLine(text, "/Script/Discovery.DiscoveryGameUserSettings", "FullscreenMode", "1");
            text = EnsureSectionLine(text, "/Script/Discovery.DiscoveryGameUserSettings", "LastConfirmedFullscreenMode", "1");
            text = EnsureSectionLine(text, "/Script/Discovery.DiscoveryGameUserSettings", "PreferredFullscreenMode", "1");
            WriteConfigText(path, text);
        }

        foreach (var path in probe.ConfigFiles.Where(f =>
                     f.EndsWith("Engine.ini", StringComparison.OrdinalIgnoreCase)))
        {
            var eng = File.Exists(path) ? File.ReadAllText(path) : "";
            eng = StripIniExoMarkers(eng);
            eng = ExoIniMarkerLine(GameIdTheFinals, preset) + "\r\n" + eng.TrimStart('\uFEFF');
            if (!eng.Contains("[SystemSettings]", StringComparison.OrdinalIgnoreCase))
                eng += "\r\n[SystemSettings]\r\n";
            eng = EnsureSectionLine(eng, "SystemSettings", "r.MotionBlurQuality", "0");
            eng = EnsureSectionLine(eng, "SystemSettings", "r.BloomQuality", "0");
            eng = EnsureSectionLine(eng, "SystemSettings", "r.DepthOfFieldQuality", "0");
            eng = EnsureSectionLine(eng, "SystemSettings", "r.VolumetricFog", "0");
            eng = EnsureSectionLine(eng, "SystemSettings", "r.DefaultFeature.MotionBlur", "0");
            eng = EnsureSectionLine(eng, "SystemSettings", "r.Lumen.DiffuseIndirect.Allow", "0");
            eng = EnsureSectionLine(eng, "SystemSettings", "r.Lumen.Reflections.Allow", "0");
            WriteConfigText(path, eng);
        }

        ApplyDisplayPreference(GameIdTheFinals, displayMode, probe);
    }

    // ── Generic text helpers ──────────────────────────────────────────────

    private static string StripIniExoMarkers(string text)
    {
        // Exo header + repeated meta lines from older applies
        text = Regex.Replace(text, @"(?m)^;\s*Exo Games[^\r\n]*\r?\n?", "");
        text = Regex.Replace(text, @"(?m)^;\s*(Performance qualities|Meta:|Scalability \+|Quality \+)[^\r\n]*\r?\n?", "");
        text = Regex.Replace(text, @"(?m)^//\s*Exo Games[^\r\n]*\r?\n?", "");
        text = Regex.Replace(text, @"(?m)^//\s*(Meta:|Quality \+|Video quality)[^\r\n]*\r?\n?", "");
        return text;
    }

    /// <summary>Honest live display readout for feature tiles (informational).</summary>
    private static string DescribeDisplayLive(string gameId, ConfigProbe probe)
    {
        try
        {
            if (string.Equals(gameId, GameIdLeague, StringComparison.OrdinalIgnoreCase))
            {
                var path = probe.PrimaryConfig ?? TryFindLeagueGameCfg();
                if (path is null || !File.Exists(path)) return "Unknown — Apply forces borderless";
                var t = File.ReadAllText(path);
                var m = Regex.Match(t, @"(?im)^\s*WindowMode\s*=\s*(\d+)");
                return m.Success
                    ? m.Groups[1].Value switch
                    {
                        "0" => "Windowed (Apply → borderless)",
                        "1" => "Borderless",
                        "2" => "Exclusive (Apply → borderless)",
                        _ => $"Mode {m.Groups[1].Value}"
                    }
                    : "Unknown — Apply forces borderless";
            }

            if (string.Equals(gameId, GameIdBlackOps7, StringComparison.OrdinalIgnoreCase))
            {
                var path = probe.PrimaryConfig;
                if (path is null || !File.Exists(path)) return "Unknown — Apply forces borderless";
                var t = File.ReadAllText(path);
                if (t.Contains("Fullscreen borderless", StringComparison.OrdinalIgnoreCase))
                    return "Borderless (COD token)";
                if (t.Contains("Fullscreen", StringComparison.OrdinalIgnoreCase))
                    return "Fullscreen token — re-Apply if not borderless in-game";
                return "Unknown — Apply forces borderless";
            }

            if (string.Equals(gameId, GameIdApex, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(gameId, GameIdCs2, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var path in probe.ConfigFiles.Where(f => File.Exists(f)))
                {
                    var t = File.ReadAllText(path);
                    var nb = Regex.Match(t, @"(?im)""setting\.nowindowborder""\s+""(\d+)""");
                    var fs = Regex.Match(t, @"(?im)""setting\.fullscreen""\s+""(\d+)""");
                    if (nb.Success || fs.Success)
                    {
                        var borderless = nb.Success && nb.Groups[1].Value == "1";
                        return borderless
                            ? "Borderless (fullscreen+nowindowborder)"
                            : "Not borderless yet — Apply forces it";
                    }
                }
                return "Unknown — Apply forces borderless";
            }

            if (string.Equals(gameId, GameIdHelldivers2, StringComparison.OrdinalIgnoreCase))
            {
                var path = probe.PrimaryConfig ?? Helldivers2ConfigPath;
                if (!File.Exists(path)) return "Unknown — Apply forces borderless";
                var t = File.ReadAllText(path);
                if (t.Contains("borderless", StringComparison.OrdinalIgnoreCase))
                    return "Borderless token present";
                return "Check after Apply — forces window_mode=borderless";
            }

            // UE family: Fortnite / Predecessor / Finals
            var roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(probe.ConfigRoot)) roots.Add(probe.ConfigRoot!);
            if (!string.IsNullOrWhiteSpace(probe.PrimaryConfig))
            {
                var d = Path.GetDirectoryName(probe.PrimaryConfig);
                if (d is not null) roots.Add(d);
            }
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(root)) continue;
                foreach (var path in Directory.EnumerateFiles(root, "GameUserSettings.ini", SearchOption.AllDirectories)
                             .OrderByDescending(p => new FileInfo(p).Length))
                {
                    if (new FileInfo(path).Length < 20) continue;
                    var t = File.ReadAllText(path);
                    var m = Regex.Match(t, @"(?im)^\s*FullscreenMode\s*=\s*(\d+)");
                    if (!m.Success) continue;
                    return m.Groups[1].Value switch
                    {
                        "0" => "Exclusive fullscreen (Apply → borderless)",
                        "1" => "Borderless (windowed fullscreen)",
                        "2" => "Windowed (Apply → borderless)",
                        _ => $"Mode {m.Groups[1].Value}"
                    };
                }
            }
        }
        catch { /* ignore */ }
        return "Apply forces borderless — close the game first";
    }

    /// <summary>Loose key = value (Helldivers-style nested config).</summary>
    private static string SetConfigLooseKey(string text, string key, string value)
    {
        var rx = new Regex(
            $@"(?m)^(\s*)({Regex.Escape(key)})(\s*=\s*)([^/\r\n#]+)",
            RegexOptions.IgnoreCase);
        if (rx.IsMatch(text))
            return rx.Replace(text, m => $"{m.Groups[1].Value}{m.Groups[2].Value}{m.Groups[3].Value}{value}", 1);

        // Append near end if missing
        return text.TrimEnd() + $"\n\t{key} = {value}\n";
    }

    /// <summary>Valve-style "setting.name"\t\t"value"</summary>
    private static string SetQuotedSetting(string text, string settingKey, string value)
    {
        var key = settingKey.Trim();
        var rx = new Regex(
            $@"(?m)^(\s*""{Regex.Escape(key)}""\s+)""[^""]*""",
            RegexOptions.IgnoreCase);
        if (rx.IsMatch(text))
            return rx.Replace(text, m => $"{m.Groups[1].Value}\"{value}\"", 1);

        // Also try without setting. prefix variants already included in key
        return text.TrimEnd() + $"\n\"{key}\"\t\t\"{value}\"\n";
    }
}
