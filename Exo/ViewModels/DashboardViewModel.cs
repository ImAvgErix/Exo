using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Exo.Helpers;
using Exo.Models;
using Exo.Services;

namespace Exo.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly AppServices _services;

    public DashboardViewModel(AppServices services)
    {
        _services = services;
        Cards = new List<OptimizerCardViewModel>
        {
            Card("discord", "Discord", "Assets/Logos/discord.png", OptimizerStatus.Available),
            Card("steam", "Steam", "Assets/Logos/steam.png", OptimizerStatus.Available),
            Card("internet", "Internet", "Assets/Logos/internet.png", OptimizerStatus.Available),
            Card("nvidia", "NVIDIA", "Assets/Logos/nvidia.png", OptimizerStatus.Available),
            Card("windows", "Windows", "Assets/Logos/windows.png", OptimizerStatus.ComingSoon),
            Card("amd", "AMD", "Assets/Logos/amd.png", OptimizerStatus.ComingSoon),
            Card("brave", "Brave", "Assets/Logos/brave.png", OptimizerStatus.ComingSoon),
            Card("riot", "Riot", "Assets/Logos/riot.png", OptimizerStatus.ComingSoon),
            Card("epic", "Epic", "Assets/Logos/epic.png", OptimizerStatus.ComingSoon),
        };

        foreach (var card in Cards)
            card.InitializePresentation();

        SoonCards = Cards.Where(c => c.IsComingSoon).ToList();
        RefreshDashboard();
    }

    public IReadOnlyList<OptimizerCardViewModel> Cards { get; }
    public IReadOnlyList<OptimizerCardViewModel> SoonCards { get; }
    public ObservableCollection<HomeSparkBar> SparkBars { get; } = new();

    [ObservableProperty] private string _heroSummary = "Maximum performance. No compromise.";

    [ObservableProperty] private string _memoryPrimary = "—";
    [ObservableProperty] private string _memorySecondary = "Reading system memory...";
    [ObservableProperty] private string _memoryLoadText = "";
    [ObservableProperty] private bool _hasMemory;

    [ObservableProperty] private bool _hasTrimStats;
    [ObservableProperty] private string _reclaimedPrimary = "—";
    [ObservableProperty] private string _reclaimedSecondary = "Apply Steam to reclaim RAM";

    [ObservableProperty] private string _discordStatusPrimary = "—";
    [ObservableProperty] private string _discordStatusSecondary = "Not optimized yet";
    [ObservableProperty] private string _steamStatusPrimary = "—";
    [ObservableProperty] private string _steamStatusSecondary = "Not optimized yet";

    [ObservableProperty] private bool _hasLatency;
    [ObservableProperty] private string _latencyPrimary = "—";
    [ObservableProperty] private string _latencySecondary = "Apply Internet for ping";

    [ObservableProperty] private bool _hasNvidiaPath;
    [ObservableProperty] private string _nvidiaPathPrimary = "—";
    [ObservableProperty] private string _nvidiaPathSecondary = "Apply NVIDIA profiles";

    // Kept for any leftover bindings / smokes that still mention FPS fields.
    [ObservableProperty] private string _fpsPrimary = "—";
    [ObservableProperty] private string _fpsSecondary = "";
    [ObservableProperty] private string _frameTimePrimary = "—";
    [ObservableProperty] private string _frameTimeSecondary = "";

    public Task RefreshStatesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RefreshDashboard();
        return Task.CompletedTask;
    }

    public void RefreshLiveMemory()
    {
        var mem = HomeDashboardReader.TryReadMemory();
        if (mem is null)
        {
            HasMemory = false;
            MemoryPrimary = "—";
            MemorySecondary = "Live memory unavailable";
            MemoryLoadText = "";
            return;
        }

        var used = mem.TotalBytes > mem.AvailableBytes
            ? mem.TotalBytes - mem.AvailableBytes
            : 0UL;
        HasMemory = true;
        MemoryLoadText = $"{mem.LoadPercent}% in use";
        MemoryPrimary = HomeDashboardReader.FormatBytes(used);
        MemorySecondary = $"{HomeDashboardReader.FormatBytes(mem.AvailableBytes)} free · {HomeDashboardReader.FormatBytes(mem.TotalBytes)} total";
    }

    private void RefreshDashboard()
    {
        SparkBars.Clear();
        var appliedCount = 0;

        // Discord
        var discord = ReadModuleState("discord-optimizer.json");
        if (discord.Applied)
        {
            appliedCount++;
            DiscordStatusPrimary = "Optimized";
            DiscordStatusSecondary = discord.Detail ?? "Last Apply OK · kernel off under elevated Exo for launch safety";
        }
        else
        {
            DiscordStatusPrimary = "Not yet";
            DiscordStatusSecondary = "Open Discord module → Apply";
        }

        // Steam
        var steam = ReadModuleState("steam-optimizer.json");
        if (steam.Applied)
        {
            appliedCount++;
            SteamStatusPrimary = "Optimized";
            SteamStatusSecondary = steam.Detail ?? "CEF quiet + webhelper trim";
        }
        else
        {
            SteamStatusPrimary = "Not yet";
            SteamStatusSecondary = "Open Steam module → Apply";
        }

        var trim = HomeDashboardReader.TryReadTrimStats();
        if (trim is null)
        {
            HasTrimStats = false;
            ReclaimedPrimary = steam.Applied ? "Active" : "—";
            ReclaimedSecondary = steam.Applied
                ? "Open Steam to accumulate live webhelper reclaim"
                : "Apply Steam to reclaim RAM / cache";
        }
        else
        {
            HasTrimStats = true;
            var hero = trim.Last24hBytes > 0 ? trim.Last24hBytes : trim.TotalBytes;
            if (hero > 0)
            {
                ReclaimedPrimary = HomeDashboardReader.FormatBytes(hero);
                ReclaimedSecondary = trim.Last24hBytes > 0
                    ? $"last 24h · {HomeDashboardReader.FormatBytes(trim.TotalBytes)} total · {trim.Passes} trim passes"
                    : $"total · {trim.Passes} trim passes";
            }
            else
            {
                ReclaimedPrimary = "Ready";
                ReclaimedSecondary = "Helper installed · open Steam for live reclaim";
            }

            if (trim.HourlyBytes.Count > 0)
            {
                var max = Math.Max(1L, trim.HourlyBytes.Max());
                foreach (var b in trim.HourlyBytes)
                    SparkBars.Add(new HomeSparkBar { Height = 8 + (40.0 * b / max) });
            }
        }

        // Internet
        var latency = HomeDashboardReader.TryReadLatency(_services.Network);
        if (latency is not null)
        {
            appliedCount++;
            HasLatency = true;
            var delta = latency.AfterP50Ms - latency.BeforeP50Ms;
            var sign = delta > 0 ? "+" : "";
            LatencyPrimary = $"{sign}{delta:0.0} ms";
            LatencySecondary = $"ping {latency.BeforeP50Ms:0.0} → {latency.AfterP50Ms:0.0} ms";
        }
        else
        {
            var netStatus = HomeDashboardReader.TryReadInternetStatus();
            if (netStatus is not null)
            {
                appliedCount++;
                HasLatency = true;
                LatencyPrimary = "Applied";
                LatencySecondary = $"{netStatus} · Reapply for ping delta";
            }
            else
            {
                HasLatency = false;
                LatencyPrimary = "Not yet";
                LatencySecondary = "Apply Internet for ping before/after";
            }
        }

        // NVIDIA
        var nvidia = HomeDashboardReader.TryReadNvidiaPath();
        if (nvidia is not null)
        {
            appliedCount++;
            HasNvidiaPath = true;
            NvidiaPathPrimary = nvidia.Gsync ? "G-SYNC" : "Max FPS";
            NvidiaPathSecondary = string.IsNullOrWhiteSpace(nvidia.ProfileFile)
                ? "Profile pack applied"
                : nvidia.ProfileFile!;
        }
        else
        {
            // Show last error / incomplete honestly
            var nvState = ReadModuleState("nvidia-optimizer.json");
            HasNvidiaPath = false;
            if (!string.IsNullOrWhiteSpace(nvState.Detail))
            {
                NvidiaPathPrimary = "Needs work";
                NvidiaPathSecondary = nvState.Detail!;
            }
            else
            {
                NvidiaPathPrimary = "Not yet";
                NvidiaPathSecondary = "Apply NVIDIA for 3D profiles";
            }
        }

        FpsPrimary = NvidiaPathPrimary;
        FpsSecondary = NvidiaPathSecondary;
        FrameTimePrimary = DiscordStatusPrimary;
        FrameTimeSecondary = DiscordStatusSecondary;

        HeroSummary = appliedCount switch
        {
            0 => "Apply optimizers — home fills with real results.",
            1 => "1 module optimized. Keep going.",
            4 => "All four modules optimized.",
            _ => $"{appliedCount} modules optimized."
        };

        RefreshLiveMemory();
    }

    private static (bool Applied, string? Detail) ReadModuleState(string fileName)
    {
        try
        {
            var path = Path.Combine(PathHelper.AppDataDir, fileName);
            if (!File.Exists(path)) return (false, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var applied = root.TryGetProperty("applied", out var a) && a.ValueKind == JsonValueKind.True;
            if (!applied && root.TryGetProperty("applyStatus", out var s) && s.ValueKind == JsonValueKind.String)
                applied = string.Equals(s.GetString(), "applied", StringComparison.OrdinalIgnoreCase);

            string? detail = null;
            if (root.TryGetProperty("lastError", out var err) && err.ValueKind == JsonValueKind.String)
            {
                var e = err.GetString();
                if (!string.IsNullOrWhiteSpace(e) && !applied)
                    detail = e.Length > 90 ? e[..87] + "..." : e;
            }
            if (detail is null && root.TryGetProperty("appliedUtc", out var utc) && utc.ValueKind == JsonValueKind.String)
            {
                if (DateTime.TryParse(utc.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                    detail = "Last Apply " + t.ToLocalTime().ToString("g");
            }
            return (applied, detail);
        }
        catch
        {
            return (false, null);
        }
    }

    private static OptimizerCardViewModel Card(
        string id, string title, string logo, OptimizerStatus status) =>
        new()
        {
            Definition = new OptimizerDefinition
            {
                Id = id,
                Title = title,
                LogoPath = logo,
                Status = status
            }
        };
}

public sealed class HomeSparkBar
{
    public double Height { get; init; }
}

public partial class OptimizerCardViewModel : ObservableObject
{
    public required OptimizerDefinition Definition { get; init; }

    [ObservableProperty] private bool _isComingSoon;

    public void InitializePresentation()
    {
        IsComingSoon = Definition.Status == OptimizerStatus.ComingSoon;
    }
}
