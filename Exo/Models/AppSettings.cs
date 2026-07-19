using System.Text.Json.Serialization;

namespace Exo.Models;

public sealed class AppSettings
{
    /// <summary>
    /// When true, check GitHub Releases for a newer Exo app on launch.
    /// The legacy JSON name keeps existing user settings compatible.
    /// </summary>
    [JsonPropertyName("autoUpdateScripts")]
    public bool CheckForUpdatesOnLaunch { get; set; }

    /// <summary>Optional custom scripts root; empty = app-bundled Scripts folder.</summary>
    public string CustomScriptsPath { get; set; } = string.Empty;

    /// <summary>ISO timestamp of last successful Discord optimizer run.</summary>
    public string? LastDiscordRunUtc { get; set; }

    /// <summary>Bundled / last known Discord kit version string.</summary>
    public string DiscordKitVersion { get; set; } = "1.3.58";

    /// <summary>Per-module experimental Apply (more aggressive; default stable/off).</summary>
    public bool ExperimentalDiscord { get; set; }
    public bool ExperimentalSteam { get; set; }
    public bool ExperimentalNvidia { get; set; }
    public bool ExperimentalInternet { get; set; }
    public bool ExperimentalRiot { get; set; }
    public bool ExperimentalEpic { get; set; }

    public AppSettings Clone() => new()
    {
        CheckForUpdatesOnLaunch = CheckForUpdatesOnLaunch,
        CustomScriptsPath = CustomScriptsPath,
        LastDiscordRunUtc = LastDiscordRunUtc,
        DiscordKitVersion = DiscordKitVersion,
        ExperimentalDiscord = ExperimentalDiscord,
        ExperimentalSteam = ExperimentalSteam,
        ExperimentalNvidia = ExperimentalNvidia,
        ExperimentalInternet = ExperimentalInternet,
        ExperimentalRiot = ExperimentalRiot,
        ExperimentalEpic = ExperimentalEpic
    };
}
