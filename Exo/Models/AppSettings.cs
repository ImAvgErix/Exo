using System.Text.Json.Serialization;

namespace Exo.Models;

public sealed class AppSettings
{
    public const string DarkTheme = "Dark";
    public const string LightTheme = "Light";

    /// <summary>Dark | Light</summary>
    public string Theme { get; set; } = DarkTheme;

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
    public string DiscordKitVersion { get; set; } = "1.3.54";

    public AppSettings Clone() => new()
    {
        Theme = Theme,
        CheckForUpdatesOnLaunch = CheckForUpdatesOnLaunch,
        CustomScriptsPath = CustomScriptsPath,
        LastDiscordRunUtc = LastDiscordRunUtc,
        DiscordKitVersion = DiscordKitVersion
    };
}
