using System.Text.Json.Serialization;

namespace OptiHub.Models;

public sealed class AppSettings
{
    public const string DarkTheme = "Dark";
    public const string LightTheme = "Light";

    /// <summary>Dark | Light</summary>
    public string Theme { get; set; } = DarkTheme;

    /// <summary>
    /// When true, on launch: prompt if a newer OptiHub.exe is on GitHub, and
    /// silently refresh the Discord script kit when newer.
    /// </summary>
    public bool AutoUpdateScripts { get; set; }

    /// <summary>GitHub repo for Discord optimizer scripts (owner/name).</summary>
    public string DiscordScriptsRepo { get; set; } = "UhhErix/OptiHub";

    /// <summary>Branch to pull script updates from.</summary>
    public string DiscordScriptsBranch { get; set; } = "main";

    /// <summary>Optional custom scripts root; empty = app-bundled Scripts folder.</summary>
    public string CustomScriptsPath { get; set; } = string.Empty;

    /// <summary>ISO timestamp of last successful Discord optimizer run.</summary>
    public string? LastDiscordRunUtc { get; set; }

    /// <summary>Bundled / last known Discord kit version string.</summary>
    public string DiscordKitVersion { get; set; } = "1.3.27";

    public AppSettings Clone() => new()
    {
        Theme = Theme,
        AutoUpdateScripts = AutoUpdateScripts,
        DiscordScriptsRepo = DiscordScriptsRepo,
        DiscordScriptsBranch = DiscordScriptsBranch,
        CustomScriptsPath = CustomScriptsPath,
        LastDiscordRunUtc = LastDiscordRunUtc,
        DiscordKitVersion = DiscordKitVersion
    };
}
