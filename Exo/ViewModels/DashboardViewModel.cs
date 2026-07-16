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

    [ObservableProperty] public partial string HeroSummary { get; set; } = "Maximum performance. No compromise.";

    [ObservableProperty] public partial string MemoryPrimary { get; set; } = "—";
    [ObservableProperty] public partial string MemorySecondary { get; set; } = "Reading system memory...";
    [ObservableProperty] public partial string MemoryLoadText { get; set; } = "";
    [ObservableProperty] public partial bool HasMemory { get; set; }

    // Kept for smokes / leftover bindings that still reference reclaim hero fields.
    [ObservableProperty] public partial bool HasTrimStats { get; set; }
    [ObservableProperty] public partial string ReclaimedPrimary { get; set; } = "—";
    [ObservableProperty] public partial string ReclaimedSecondary { get; set; } = "Apply Steam to reclaim cache";

    [ObservableProperty] public partial string DiscordStatusPrimary { get; set; } = "—";
    [ObservableProperty] public partial string DiscordStatusSecondary { get; set; } = "Not optimized yet";
    [ObservableProperty] public partial string SteamStatusPrimary { get; set; } = "—";
    [ObservableProperty] public partial string SteamStatusSecondary { get; set; } = "Not optimized yet";

    [ObservableProperty] public partial bool HasLatency { get; set; }
    [ObservableProperty] public partial string LatencyPrimary { get; set; } = "—";
    [ObservableProperty] public partial string LatencySecondary { get; set; } = "Apply Internet for ping";

    [ObservableProperty] public partial bool HasNvidiaPath { get; set; }
    [ObservableProperty] public partial string NvidiaPathPrimary { get; set; } = "—";
    [ObservableProperty] public partial string NvidiaPathSecondary { get; set; } = "Apply NVIDIA profiles";

    [ObservableProperty] public partial string FpsPrimary { get; set; } = "—";
    [ObservableProperty] public partial string FpsSecondary { get; set; } = "";
    [ObservableProperty] public partial string FrameTimePrimary { get; set; } = "—";
    [ObservableProperty] public partial string FrameTimeSecondary { get; set; } = "";

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
        }
        else
        {
            var used = mem.TotalBytes > mem.AvailableBytes
                ? mem.TotalBytes - mem.AvailableBytes
                : 0UL;
            HasMemory = true;
            MemoryLoadText = $"{mem.LoadPercent}% in use";
            MemoryPrimary = HomeDashboardReader.FormatBytes(used);
            MemorySecondary =
                $"{HomeDashboardReader.FormatBytes(mem.AvailableBytes)} free · {HomeDashboardReader.FormatBytes(mem.TotalBytes)} total";
        }

        // Background refresh: Discord/Steam reclaim + Internet proof (file + live link)
        RefreshDiscordRamTile();
        RefreshSteamRamTile();
        RefreshInternetTile();
    }

    private void RefreshInternetTile(ref int appliedCount)
    {
        RefreshInternetTile();
        if (HasLatency) appliedCount++;
    }

    private void RefreshInternetTile()
    {
        var latency = HomeDashboardReader.TryReadLatency(_services.Network);
        var link = HomeDashboardReader.TryReadPrimaryLinkSpeed();
        var linkBit = link is not null && link.BitsPerSecond > 0
            ? $"{link.Label} {link.MediaKind}"
            : null;
        var preset = HomeDashboardReader.TryReadInternetStatus();

        if (latency is not null)
        {
            HasLatency = true;
            var dPing = latency.AfterP50Ms - latency.BeforeP50Ms;
            var dJit = latency.AfterJitterMs - latency.BeforeJitterMs;
            var dDns = latency.AfterDnsMs - latency.BeforeDnsMs;
            var better = dPing < -0.5;
            // Hero: show improvement when real; otherwise live after-ping.
            LatencyPrimary = better
                ? $"{dPing:0.0} ms"
                : $"{latency.AfterP50Ms:0.0} ms";

            var parts = new List<string>
            {
                $"ping {latency.BeforeP50Ms:0.0}→{latency.AfterP50Ms:0.0}",
                $"jit {latency.BeforeJitterMs:0.0}→{latency.AfterJitterMs:0.0}",
                $"DNS {FormatDns(latency.BeforeDnsMs)}→{FormatDns(latency.AfterDnsMs)}"
            };
            if (linkBit is not null) parts.Add(linkBit);
            if (!string.IsNullOrWhiteSpace(preset) &&
                !string.Equals(preset, "Applied", StringComparison.OrdinalIgnoreCase))
                parts.Add(preset);
            if (dPing > 0.5)
                parts.Add($"+{dPing:0.0} ms vs before");
            if (dJit > 1.0 || (latency.AfterDnsMs > 0 && dDns > 200))
                parts.Add("reapply if DNS still high");

            LatencySecondary = string.Join(" · ", parts);
            return;
        }

        if (!string.IsNullOrWhiteSpace(preset))
        {
            HasLatency = true;
            LatencyPrimary = link is not null && link.BitsPerSecond > 0
                ? link.Label
                : (preset.Length > 14 ? preset[..14] : preset);
            LatencySecondary = linkBit is not null
                ? $"{preset} · {linkBit} · re-run Apply for fresh ping proof"
                : $"{preset} · re-run Apply for before/after ping · jitter · DNS";
            return;
        }

        HasLatency = false;
        LatencyPrimary = "—";
        LatencySecondary = linkBit is not null
            ? $"{linkBit} · Apply Internet for ping proof"
            : "Apply Internet for before/after ping · jitter · DNS";
    }

    private void RefreshDiscordRamTile()
    {
        var sample = HomeDashboardReader.TrySampleDiscordRam();
        var kernel = HomeDashboardReader.TryReadDiscordKernelOnDisk();
        var applied = HomeDashboardReader.TryReadDiscordApplied();
        var live = sample?.LiveBytes ?? 0;
        var reclaimed = sample?.ReclaimedBytes ?? 0;

        // Prefer RAM reclaimed (peak − live) when DiscOpt/kernel has trimmed idle pages.
        if (reclaimed >= 8L << 20) // ≥ 8 MB so we don't flash noise
        {
            DiscordStatusPrimary = HomeDashboardReader.FormatBytes(reclaimed);
            DiscordStatusSecondary = live > 0
                ? $"reclaimed · live {HomeDashboardReader.FormatBytes(live)}" +
                  (kernel ? " · kernel on" : applied ? " · applied" : "")
                : kernel
                    ? "reclaimed this session · kernel on"
                    : "reclaimed this session · open Discord to refresh";
        }
        else if (live > 0)
        {
            DiscordStatusPrimary = HomeDashboardReader.FormatBytes(live);
            DiscordStatusSecondary = kernel
                ? "live process RAM · DiscOpt trim watching"
                : applied
                    ? "live process RAM · kernel off (stock-safe)"
                    : "live process RAM · Apply for full pack";
        }
        else if (kernel)
        {
            DiscordStatusPrimary = "Kernel on";
            DiscordStatusSecondary = "Open Discord — reclaim updates in background";
        }
        else if (applied)
        {
            DiscordStatusPrimary = "Partial";
            DiscordStatusSecondary = "Apply record incomplete · open Discord module";
        }
        else
        {
            DiscordStatusPrimary = "—";
            DiscordStatusSecondary = "Apply Discord · leave open for background reclaim";
        }
    }

    private void RefreshSteamRamTile()
    {
        var ws = HomeDashboardReader.TryReadProcessWorkingSetBytes("steam", "steamwebhelper");
        var trim = HomeDashboardReader.TryReadTrimStats();
        var steam = ReadModuleState("steam-optimizer.json");

        if (trim is not null && (trim.TotalBytes > 0 || trim.Last24hBytes > 0))
        {
            var hero = trim.Last24hBytes > 0 ? trim.Last24hBytes : trim.TotalBytes;
            ReclaimedPrimary = HomeDashboardReader.FormatBytes(hero);
            ReclaimedSecondary = trim.Last24hBytes > 0
                ? $"last 24h · {HomeDashboardReader.FormatBytes(trim.TotalBytes)} total · {trim.Passes} passes"
                : $"total reclaimed · {trim.Passes} passes";
            HasTrimStats = true;
            SteamStatusPrimary = HomeDashboardReader.FormatBytes(hero);
            SteamStatusSecondary = ws > 0
                ? $"reclaimed · live client {HomeDashboardReader.FormatBytes(ws)}"
                : ReclaimedSecondary;
        }
        else if (ws > 0)
        {
            HasTrimStats = false;
            SteamStatusPrimary = HomeDashboardReader.FormatBytes(ws);
            SteamStatusSecondary = steam.Applied
                ? "live client RAM · companion (no CEF thrash)"
                : "live client RAM · Apply Steam for quiet CEF";
            ReclaimedPrimary = HomeDashboardReader.FormatBytes(ws);
            ReclaimedSecondary = "live Steam + webhelper working set";
        }
        else if (steam.Applied)
        {
            HasTrimStats = false;
            SteamStatusPrimary = "Ready";
            SteamStatusSecondary = "Optimized · open Steam for background reclaim";
            ReclaimedPrimary = "Ready";
            ReclaimedSecondary = "Open Steam for live client RAM";
        }
        else
        {
            HasTrimStats = false;
            SteamStatusPrimary = "—";
            SteamStatusSecondary = "Apply Steam · background reclaim + quiet CEF";
            ReclaimedPrimary = "—";
            ReclaimedSecondary = "Apply Steam to free cache / quiet CEF";
        }
    }

    private void RefreshDashboard()
    {
        SparkBars.Clear();
        var appliedCount = 0;

        // Discord / Steam tiles filled by live RAM helpers (and on timer)
        RefreshDiscordRamTile();
        RefreshSteamRamTile();
        if (HomeDashboardReader.TryReadDiscordApplied() || HomeDashboardReader.TryReadDiscordKernelOnDisk())
            appliedCount++;
        if (ReadModuleState("steam-optimizer.json").Applied)
            appliedCount++;

        var trim = HomeDashboardReader.TryReadTrimStats();
        if (trim is not null && trim.HourlyBytes.Count > 0)
        {
            var max = Math.Max(1L, trim.HourlyBytes.Max());
            foreach (var b in trim.HourlyBytes)
                SparkBars.Add(new HomeSparkBar { Height = 8 + (40.0 * b / max) });
        }

        // Internet — real before/after ping + live link speed
        RefreshInternetTile(ref appliedCount);

        // NVIDIA
        var nvidia = HomeDashboardReader.TryReadNvidiaPath();
        if (nvidia is not null)
        {
            appliedCount++;
            HasNvidiaPath = true;
            NvidiaPathPrimary = nvidia.Gsync ? "G-SYNC" : "Max FPS";
            NvidiaPathSecondary = string.IsNullOrWhiteSpace(nvidia.ProfileFile)
                ? "3D Base Profile applied"
                : nvidia.ProfileFile!;
        }
        else
        {
            var nvState = ReadModuleState("nvidia-optimizer.json");
            HasNvidiaPath = false;
            if (!string.IsNullOrWhiteSpace(nvState.Detail))
            {
                NvidiaPathPrimary = "Needs work";
                NvidiaPathSecondary = nvState.Detail!;
            }
            else
            {
                NvidiaPathPrimary = "—";
                NvidiaPathSecondary = "Apply NVIDIA for max-FPS NIP";
            }
        }

        FpsPrimary = NvidiaPathPrimary;
        FpsSecondary = NvidiaPathSecondary;
        FrameTimePrimary = DiscordStatusPrimary;
        FrameTimeSecondary = DiscordStatusSecondary;

        HeroSummary = appliedCount switch
        {
            0 => "Apply optimizers — home fills with live RAM and real proof.",
            1 => "1 module active. Keep going.",
            4 => "All four modules reporting.",
            _ => $"{appliedCount} modules active."
        };

        RefreshLiveMemory();
    }

    private static string FormatDns(double ms) =>
        ms < 0 ? "—" : ms >= 1000 ? $"{ms / 1000:0.0}s" : $"{ms:0} ms";

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
            if (!applied && root.TryGetProperty("profileApplied", out var pa) && pa.ValueKind == JsonValueKind.True)
                applied = true;

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

    [ObservableProperty] public partial bool IsComingSoon { get; set; }

    public void InitializePresentation()
    {
        IsComingSoon = Definition.Status == OptimizerStatus.ComingSoon;
    }
}
