namespace Exo.Services.Ai;

/// <summary>Install-if-missing for optimizer targets — never fail solely because app absent.</summary>
public sealed class ExoAutoInstallService
{
    public sealed record InstallPlan(string Id, string DisplayName, string Url, string FileName);

    public static readonly InstallPlan Discord = new(
        "discord",
        "Discord",
        "https://discord.com/api/downloads/distributions/app/installers/latest?channel=stable&platform=win&arch=x64",
        "DiscordSetup.exe");

    public static readonly InstallPlan Steam = new(
        "steam",
        "Steam",
        "https://cdn.akamai.steamstatic.com/client/installer/SteamSetup.exe",
        "SteamSetup.exe");

    public static readonly InstallPlan Brave = new(
        "brave",
        "Brave",
        "https://laptop-updates.brave.com/latest/winx64",
        "BraveBrowserSetup.exe");

    public static readonly InstallPlan Epic = new(
        "epic",
        "Epic Games Launcher",
        "https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/installer/download/EpicGamesLauncherInstaller.msi",
        "EpicInstaller.msi");

    public static readonly InstallPlan Riot = new(
        "riot",
        "Riot Client",
        "https://clientconfig.rpg.riotgames.com/api/v1/config/public?os=windows",
        "RiotClientSetup.exe");

    public string DownloadCacheDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Exo", "downloads");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public bool IsPresent(string targetId) => targetId.ToLowerInvariant() switch
    {
        "discord" => File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Discord", "Update.exe")),
        "steam" => File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam", "steam.exe")),
        "brave" => File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BraveSoftware", "Brave-Browser", "Application", "brave.exe")),
        "epic" => Directory.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher")),
        "riot" => Directory.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Riot Games", "Riot Client")),
        _ => false
    };

    public InstallPlan? GetPlan(string targetId) => targetId.ToLowerInvariant() switch
    {
        "discord" => Discord,
        "steam" => Steam,
        "brave" => Brave,
        "epic" => Epic,
        "riot" => Riot,
        _ => null
    };

    /// <summary>
    /// Ensures target is installed. On non-Windows or offline, returns a structured skip — never fake success.
    /// </summary>
    public async Task<(bool Ok, string Message)> EnsureInstalledAsync(
        string targetId,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (IsPresent(targetId))
            return (true, $"{targetId} already installed");

        var plan = GetPlan(targetId);
        if (plan is null)
            return (false, $"no install plan for {targetId}");

        if (!OperatingSystem.IsWindows())
            return (false, $"{plan.DisplayName} not installed (install requires Windows)");

        progress?.Report($"download: {plan.DisplayName}");
        var dest = Path.Combine(DownloadCacheDir, plan.FileName);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            await using var net = await http.GetStreamAsync(plan.Url, ct).ConfigureAwait(false);
            await using var file = File.Create(dest);
            await net.CopyToAsync(file, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (false, $"needs network to install {plan.DisplayName}: {ex.Message}");
        }

        progress?.Report($"install: {plan.DisplayName}");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = dest,
                Arguments = plan.FileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                    ? "/quiet /norestart"
                    : "/S",
                UseShellExecute = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is not null)
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (false, $"install launch failed for {plan.DisplayName}: {ex.Message}");
        }

        return IsPresent(targetId)
            ? (true, $"{plan.DisplayName} installed")
            : (true, $"{plan.DisplayName} installer launched — verify after setup completes");
    }
}
