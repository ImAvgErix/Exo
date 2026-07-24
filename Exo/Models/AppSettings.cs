using System.Text.Json.Serialization;

namespace Exo.Models;

public sealed class AppSettings
{
    /// <summary>
    /// When true (default), the brain peeks GitHub Releases on launch and ASKS
    /// before installing anything. New JSON name on purpose: the legacy
    /// "autoUpdateScripts" default was false, which silently disabled update
    /// prompts for every existing install — consent now happens in the ask.
    /// </summary>
    [JsonPropertyName("promptUpdatesOnLaunch")]
    public bool CheckForUpdatesOnLaunch { get; set; } = true;

    /// <summary>Optional custom scripts root; empty = app-bundled Scripts folder.</summary>
    public string CustomScriptsPath { get; set; } = string.Empty;

    /// <summary>ISO timestamp of last successful Discord optimizer run.</summary>
    public string? LastDiscordRunUtc { get; set; }

    /// <summary>Bundled / last known Discord kit version string.</summary>
    public string DiscordKitVersion { get; set; } = "1.3.74";

    /// <summary>Per-module experimental Apply (more aggressive; default stable/off).</summary>
    public bool ExperimentalDiscord { get; set; }
    public bool ExperimentalSteam { get; set; }
    public bool ExperimentalNvidia { get; set; }
    public bool ExperimentalInternet { get; set; }

    /// <summary>
    /// When true, the first-install “Exo is free / Buy Me a Coffee” prompt was dismissed.
    /// Missing on upgrade → false so existing installs see it once.
    /// </summary>
    public bool WelcomePromptSeen { get; set; }

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
        WelcomePromptSeen = WelcomePromptSeen
    };
}
