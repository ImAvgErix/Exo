using System.Text.Json.Serialization;

namespace OptiHub.Models;

public sealed class AppSettings
{
    public const string DarkTheme = "Dark";
    public const string LightTheme = "Light";
    public const string SystemTheme = "System";

    /// <summary>Dark | Light | System</summary>
    public string Theme { get; set; } = DarkTheme;

    /// <summary>When true, scripts are updated from GitHub on launch if newer.</summary>
    public bool AutoUpdateScripts { get; set; } = true;

    /// <summary>Simulate optimizers without modifying the system.</summary>
    public bool DryRun { get; set; }

    /// <summary>Create a Windows restore point before applying optimizers.</summary>
    public bool AutoRestorePoint { get; set; } = true;

    /// <summary>Confirm before running any optimizer.</summary>
    public bool ConfirmBeforeRun { get; set; } = true;

    /// <summary>GitHub repo for Discord optimizer scripts (owner/name).</summary>
    public string DiscordScriptsRepo { get; set; } = "BarcusEric/DiscOpti";

    /// <summary>Branch to pull script updates from.</summary>
    public string DiscordScriptsBranch { get; set; } = "main";

    /// <summary>Optional custom scripts root; empty = app-bundled Scripts folder.</summary>
    public string CustomScriptsPath { get; set; } = string.Empty;

    /// <summary>ISO timestamp of last successful Discord optimizer run.</summary>
    public string? LastDiscordRunUtc { get; set; }

    /// <summary>Bundled / last known Discord kit version string.</summary>
    public string DiscordKitVersion { get; set; } = "1.1.4";

    [JsonIgnore]
    public bool IsDarkPreferred =>
        Theme.Equals(DarkTheme, StringComparison.OrdinalIgnoreCase);
}
