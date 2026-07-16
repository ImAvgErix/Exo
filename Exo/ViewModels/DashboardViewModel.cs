using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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

        // Card set is fixed — materialize once instead of re-allocating per binding read.
        SoonCards = Cards.Where(c => c.IsComingSoon).ToList();

        RefreshDashboard();
    }

    public IReadOnlyList<OptimizerCardViewModel> Cards { get; }

    public IReadOnlyList<OptimizerCardViewModel> SoonCards { get; }

    public ObservableCollection<HomeSparkBar> SparkBars { get; } = new();

    // FPS / frame-time capture is not shipped — stay honest with empty until it is.
    [ObservableProperty] private string _fpsPrimary = "—";
    [ObservableProperty] private string _fpsSecondary = "No capture yet";
    [ObservableProperty] private string _frameTimePrimary = "—";
    [ObservableProperty] private string _frameTimeSecondary = "No frame-time capture yet";

    [ObservableProperty] private bool _hasTrimStats;
    [ObservableProperty] private string _reclaimedPrimary = "—";
    [ObservableProperty] private string _reclaimedSecondary = "Apply Steam to start reclaiming";

    [ObservableProperty] private bool _hasMemory;
    [ObservableProperty] private string _memoryPrimary = "—";
    [ObservableProperty] private string _memorySecondary = "Live memory unavailable";
    [ObservableProperty] private string _memoryLoadText = "";

    [ObservableProperty] private bool _hasLatency;
    [ObservableProperty] private string _latencyPrimary = "—";
    [ObservableProperty] private string _latencySecondary = "No Internet benchmark yet";

    [ObservableProperty] private bool _hasNvidiaPath;
    [ObservableProperty] private string _nvidiaPathPrimary = "—";
    [ObservableProperty] private string _nvidiaPathSecondary = "Apply NVIDIA to lock the frame path";

    /// <summary>
    /// Refresh file-backed stats + live memory. No Detect* probes — those stay on module pages.
    /// </summary>
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
        MemoryLoadText = $"{mem.LoadPercent}%";
        MemoryPrimary = HomeDashboardReader.FormatBytes(used);
        MemorySecondary = $"in use · {HomeDashboardReader.FormatBytes(mem.TotalBytes)} total";
    }

    private void RefreshDashboard()
    {
        // Hero pair: NVIDIA pack path + Discord apply (honest file-backed, no fake FPS).
        // FPS capture is not shipped — use pack/apply status so home is not all dashes.
        SparkBars.Clear();

        var nvidia = HomeDashboardReader.TryReadNvidiaPath();
        if (nvidia is null)
        {
            HasNvidiaPath = false;
            FpsPrimary = "—";
            FpsSecondary = "Apply NVIDIA to lock the pack";
            NvidiaPathPrimary = "—";
            NvidiaPathSecondary = "Apply NVIDIA to lock the frame path";
        }
        else
        {
            HasNvidiaPath = true;
            FpsPrimary = nvidia.Gsync ? "G-SYNC" : "Max FPS";
            FpsSecondary = string.IsNullOrWhiteSpace(nvidia.ProfileFile)
                ? "Profile pack applied"
                : nvidia.ProfileFile!;
            NvidiaPathPrimary = nvidia.Gsync ? "G-SYNC pack" : "Max FPS pack";
            NvidiaPathSecondary = string.IsNullOrWhiteSpace(nvidia.ProfileFile)
                ? "Profile applied"
                : nvidia.ProfileFile!;
        }

        // Frame-time slot: Discord apply status (real state file) until capture ships.
        var discordApplied = HomeDashboardReader.TryReadDiscordApplied();
        if (discordApplied)
        {
            FrameTimePrimary = "Applied";
            FrameTimeSecondary = "Discord optimizer last apply OK";
        }
        else
        {
            FrameTimePrimary = "—";
            FrameTimeSecondary = "Apply Discord · frame capture later";
        }

        var trim = HomeDashboardReader.TryReadTrimStats();
        if (trim is null)
        {
            HasTrimStats = false;
            ReclaimedPrimary = "—";
            ReclaimedSecondary = "Apply Steam to reclaim RAM / cache";
        }
        else
        {
            HasTrimStats = true;
            var heroBytes = trim.Last24hBytes > 0 ? trim.Last24hBytes : trim.TotalBytes;
            if (heroBytes > 0)
            {
                ReclaimedPrimary = HomeDashboardReader.FormatBytes(heroBytes);
                ReclaimedSecondary = trim.Last24hBytes > 0
                    ? $"last 24 h · {HomeDashboardReader.FormatBytes(trim.TotalBytes)} total"
                    : (trim.Passes > 0 ? "from last Steam Apply (cache free)" : "total reclaimed");
            }
            else
            {
                ReclaimedPrimary = "Active";
                ReclaimedSecondary = "Steam optimized · open Steam for live trim";
            }

            // Spark from hourly series when present.
            if (trim.HourlyBytes.Count > 0)
            {
                var max = Math.Max(1L, trim.HourlyBytes.Max());
                foreach (var b in trim.HourlyBytes)
                {
                    var h = 8 + (40.0 * b / max);
                    SparkBars.Add(new HomeSparkBar { Height = h });
                }
            }
        }

        var latency = HomeDashboardReader.TryReadLatency(_services.Network);
        if (latency is null)
        {
            var netStatus = HomeDashboardReader.TryReadInternetStatus();
            if (netStatus is not null)
            {
                HasLatency = true;
                LatencyPrimary = "Applied";
                LatencySecondary = $"Internet {netStatus} · run Apply again for ping delta";
            }
            else
            {
                HasLatency = false;
                LatencyPrimary = "—";
                LatencySecondary = "Apply Internet for ping before/after";
            }
        }
        else
        {
            HasLatency = true;
            var delta = latency.AfterP50Ms - latency.BeforeP50Ms;
            var sign = delta > 0 ? "+" : "";
            LatencyPrimary = $"{sign}{delta:0.0} ms";
            LatencySecondary =
                $"ping {latency.BeforeP50Ms:0.0} → {latency.AfterP50Ms:0.0} ms";
        }

        RefreshLiveMemory();
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
