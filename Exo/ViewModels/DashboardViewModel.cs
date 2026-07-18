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
            Card("riot", "Riot", "Assets/Logos/riot.png", OptimizerStatus.Available),
            Card("epic", "Epic", "Assets/Logos/epic.png", OptimizerStatus.Available),
        };

        foreach (var card in Cards)
            card.InitializePresentation();

        SoonCards = Cards.Where(c => c.IsComingSoon).ToList();
        RefreshDashboard();
    }

    public IReadOnlyList<OptimizerCardViewModel> Cards { get; }
    public IReadOnlyList<OptimizerCardViewModel> SoonCards { get; }

    [ObservableProperty] public partial string HeroSummary { get; set; } = "Maximum performance. No compromise.";
    [ObservableProperty] public partial string OverviewPrimary { get; set; } = "0 / 6 verified";

    [ObservableProperty] public partial string MemoryPrimary { get; set; } = "—";
    [ObservableProperty] public partial string MemorySecondary { get; set; } = "Reading system memory...";
    [ObservableProperty] public partial string MemoryLoadText { get; set; } = "";
    [ObservableProperty] public partial double MemoryLoadPercent { get; set; }
    [ObservableProperty] public partial bool HasMemory { get; set; }

    [ObservableProperty] public partial string DiscordStatusPrimary { get; set; } = "—";
    [ObservableProperty] public partial string DiscordStatusSecondary { get; set; } = "Not optimized yet";
    [ObservableProperty] public partial string DiscordStatusTag { get; set; } = "NOT APPLIED";
    [ObservableProperty] public partial string DiscordLiveMetric { get; set; } = "No live process sample";
    [ObservableProperty] public partial string SteamStatusPrimary { get; set; } = "—";
    [ObservableProperty] public partial string SteamStatusSecondary { get; set; } = "Not optimized yet";
    [ObservableProperty] public partial string SteamStatusTag { get; set; } = "NOT APPLIED";
    [ObservableProperty] public partial string SteamLiveMetric { get; set; } = "No live process sample";

    [ObservableProperty] public partial bool HasLatency { get; set; }
    [ObservableProperty] public partial string LatencyPrimary { get; set; } = "—";
    [ObservableProperty] public partial string LatencySecondary { get; set; } = "Apply Internet to measure the current connection";
    [ObservableProperty] public partial string InternetStatusTag { get; set; } = "NOT APPLIED";
    [ObservableProperty] public partial string InternetLiveMetric { get; set; } = "No current route sample";

    [ObservableProperty] public partial bool HasNvidiaPath { get; set; }
    [ObservableProperty] public partial string NvidiaPathPrimary { get; set; } = "—";
    [ObservableProperty] public partial string NvidiaPathSecondary { get; set; } = "Apply NVIDIA profiles";
    [ObservableProperty] public partial string NvidiaStatusTag { get; set; } = "NOT APPLIED";
    [ObservableProperty] public partial string NvidiaLiveMetric { get; set; } = "No verified driver profile";

    [ObservableProperty] public partial string RiotStatusPrimary { get; set; } = "Riot ready";
    [ObservableProperty] public partial string RiotStatusSecondary { get; set; } = "Apply reversible per-game hardware policy";
    [ObservableProperty] public partial string RiotStatusTag { get; set; } = "NOT APPLIED";
    [ObservableProperty] public partial string RiotLiveMetric { get; set; } = "Client and anti-cheat stay untouched";
    [ObservableProperty] public partial string EpicStatusPrimary { get; set; } = "Epic ready";
    [ObservableProperty] public partial string EpicStatusSecondary { get; set; } = "Apply policy to games discovered from manifests";
    [ObservableProperty] public partial string EpicStatusTag { get; set; } = "NOT APPLIED";
    [ObservableProperty] public partial string EpicLiveMetric { get; set; } = "Launcher files and updates stay untouched";

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
            MemoryLoadPercent = 0;
        }
        else
        {
            var used = mem.TotalBytes > mem.AvailableBytes
                ? mem.TotalBytes - mem.AvailableBytes
                : 0UL;
            HasMemory = true;
            MemoryLoadText = $"{mem.LoadPercent}% in use";
            MemoryLoadPercent = mem.LoadPercent;
            MemoryPrimary = HomeDashboardReader.FormatBytes(used);
            MemorySecondary =
                $"{HomeDashboardReader.FormatBytes(mem.AvailableBytes)} free · {HomeDashboardReader.FormatBytes(mem.TotalBytes)} total";
        }

        // Visible-dashboard refresh: Discord/Steam reclaim + Internet proof.
        RefreshDiscordRamTile();
        RefreshSteamRamTile();
        RefreshInternetTile();
    }

    private void RefreshInternetTile(ref int appliedCount)
    {
        RefreshInternetTile();
        if (string.Equals(InternetStatusTag, "VERIFIED", StringComparison.Ordinal)) appliedCount++;
    }

    private void RefreshInternetTile()
    {
        var latency = HomeDashboardReader.TryReadLatency(_services.Network);
        var quality = _services.Network.LoadQualityBenchmark();
        var link = HomeDashboardReader.TryReadPrimaryLinkSpeed();
        var linkBit = link is not null && link.BitsPerSecond > 0
            ? $"{link.Label} {link.MediaKind}"
            : null;
        var preset = HomeDashboardReader.TryReadInternetStatus();
        var dnsApply = HomeDashboardReader.TryReadInternetDnsStatus();

        if (!string.IsNullOrWhiteSpace(preset))
        {
            HasLatency = true;
            InternetStatusTag = "VERIFIED";
            LatencyPrimary = linkBit ?? "Connection optimized";
            var dns = !string.IsNullOrWhiteSpace(dnsApply)
                ? dnsApply
                : quality is { Ok: true, IsQualityTest: true } && !string.IsNullOrWhiteSpace(quality.DnsProvider)
                    ? quality.DnsProvider + " DNS selected"
                    : "automatic DNS selection";
            LatencySecondary = $"Adaptive stack applied · {dns}";
            InternetLiveMetric = quality is { Ok: true, IsQualityTest: true }
                ? BuildInternetLiveMetric(quality, latency)
                : latency is not null
                    ? $"{latency.AfterP50Ms:0.#} ms idle · {latency.AfterJitterMs:0.#} ms jitter"
                    : "Run Analyze & Apply to refresh the route sample";
            return;
        }

        if (latency is not null)
        {
            HasLatency = true;
            InternetStatusTag = "MEASURED";
            LatencyPrimary = linkBit ?? "Route measured";
            LatencySecondary = "Current route sample available · settings not verified";
            InternetLiveMetric = $"{latency.AfterP50Ms:0.#} ms idle · {latency.AfterJitterMs:0.#} ms jitter";
            return;
        }

        HasLatency = false;
        InternetStatusTag = "NOT APPLIED";
        LatencyPrimary = linkBit ?? "Connection ready";
        LatencySecondary = "Analyze the live path, tune the stack, and select the fastest healthy DNS";
        InternetLiveMetric = "No current route sample";
    }

    private static string BuildInternetLiveMetric(NetworkBenchmarkResult quality, HomeDashboardReader.LatencySnapshot? latency)
    {
        var downLoaded = Math.Max(0, quality.DownloadLoadedMs - quality.PingP50Ms);
        var upLoaded = Math.Max(0, quality.UploadLoadedMs - quality.PingP50Ms);
        return $"{quality.PingP50Ms:0.#} ms idle · full-load +{downLoaded:0.#} down / +{upLoaded:0.#} up · {quality.PacketLossPercent:0.##}% idle loss";
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
            DiscordLiveMetric = live > 0
                ? $"{HomeDashboardReader.FormatBytes(live)} live · {HomeDashboardReader.FormatBytes(reclaimed)} reclaimed this session"
                : $"{HomeDashboardReader.FormatBytes(reclaimed)} reclaimed this session";
        }
        else if (live > 0)
        {
            DiscordLiveMetric = $"{HomeDashboardReader.FormatBytes(live)} live process memory";
        }
        else
        {
            DiscordLiveMetric = "Open Discord for a live memory sample";
        }

        if (kernel)
        {
            DiscordStatusTag = "VERIFIED";
            DiscordStatusPrimary = "Lean client active";
            DiscordStatusSecondary = "Privacy patch · voice QoS · idle memory guard";
        }
        else if (applied)
        {
            DiscordStatusTag = "APPLIED";
            DiscordStatusPrimary = "Stock-safe mode";
            DiscordStatusSecondary = "Voice QoS and privacy settings applied · custom kernel skipped";
        }
        else
        {
            DiscordStatusTag = "NOT APPLIED";
            DiscordStatusPrimary = "Discord ready";
            DiscordStatusSecondary = "Apply privacy, voice, launch, and background-memory policy";
        }
    }

    private void RefreshSteamRamTile()
    {
        var memory = HomeDashboardReader.TryReadProcessMemory("steam", "steamwebhelper");
        var steam = ReadModuleState("steam-optimizer.json");
        var guardRunning = HomeDashboardReader.TryReadSteamMemoryGuardRunning();

        if (memory.ProcessCount > 0)
        {
            SteamLiveMetric = $"{HomeDashboardReader.FormatBytes(memory.PrivateBytes)} private · {memory.ProcessCount} processes";
        }
        else
        {
            SteamLiveMetric = "Open Steam for a live memory sample";
        }

        if (steam.Applied)
        {
            SteamStatusTag = "VERIFIED";
            SteamStatusPrimary = guardRunning ? "Background policy active" : "Background policy ready";
            SteamStatusSecondary = guardRunning
                ? "Foreground stays responsive · background CEF yields while gaming"
                : "Starts with the optimized launcher · no unsafe RAM purges";
        }
        else
        {
            SteamStatusTag = "NOT APPLIED";
            SteamStatusPrimary = "Steam ready";
            SteamStatusSecondary = "Apply lean library, background-memory, and in-game CPU policy";
        }
    }

    private void RefreshDashboard()
    {
        var appliedCount = 0;

        // Discord / Steam tiles filled by live RAM helpers (and on timer)
        RefreshDiscordRamTile();
        RefreshSteamRamTile();
        if (HomeDashboardReader.TryReadDiscordApplied() || HomeDashboardReader.TryReadDiscordKernelOnDisk())
            appliedCount++;
        if (ReadModuleState("steam-optimizer.json").Applied)
            appliedCount++;

        // Internet — real before/after ping + live link speed
        RefreshInternetTile(ref appliedCount);

        // NVIDIA
        var nvidia = HomeDashboardReader.TryReadNvidiaPath();
        if (nvidia is not null)
        {
            appliedCount++;
            HasNvidiaPath = true;
            NvidiaStatusTag = "VERIFIED";
            NvidiaPathPrimary = !string.IsNullOrWhiteSpace(nvidia.GpuName)
                ? nvidia.GpuName!
                : !string.IsNullOrWhiteSpace(nvidia.Series) ? nvidia.Series! : "NVIDIA profile active";
            var display = !string.IsNullOrWhiteSpace(nvidia.PrimaryMode)
                ? $" · {nvidia.PrimaryMode} {nvidia.PrimaryConnection}".TrimEnd()
                : string.Empty;
            NvidiaPathSecondary = nvidia.Gsync
                ? $"G-SYNC/VRR profile{display}"
                : $"Raw-latency profile{display}";
            var proof = new List<string>();
            if (nvidia.VerifiedSettingCount > 0) proof.Add($"{nvidia.VerifiedSettingCount} driver pins");
            if (nvidia.GameProfileCount > 0) proof.Add($"{nvidia.GameProfileCount} game profiles");
            NvidiaLiveMetric = proof.Count > 0
                ? string.Join(" · ", proof) + " verified"
                : "Driver profile import verified";
        }
        else
        {
            var nvState = ReadModuleState("nvidia-optimizer.json");
            HasNvidiaPath = false;
            if (!string.IsNullOrWhiteSpace(nvState.Detail))
            {
                NvidiaStatusTag = "NEEDS ATTENTION";
                NvidiaPathPrimary = "Needs work";
                NvidiaPathSecondary = nvState.Detail!;
                NvidiaLiveMetric = "Open NVIDIA to review the failed stage";
            }
            else
            {
                NvidiaStatusTag = "NOT APPLIED";
                NvidiaPathPrimary = "GPU ready";
                NvidiaPathSecondary = "Apply a verified driver profile and per-game overrides";
                NvidiaLiveMetric = "No verified driver profile";
            }
        }

        RefreshLauncherTile("riot", ref appliedCount);
        RefreshLauncherTile("epic", ref appliedCount);

        FpsPrimary = NvidiaPathPrimary;
        FpsSecondary = NvidiaPathSecondary;
        FrameTimePrimary = DiscordStatusPrimary;
        FrameTimeSecondary = DiscordStatusSecondary;

        OverviewPrimary = $"{appliedCount} / 6 verified";
        HeroSummary = appliedCount switch
        {
            0 => "No optimizer has a verified apply record yet.",
            1 => "One optimizer is verified; five are ready to configure.",
            6 => "Every optimizer has a verified apply record.",
            _ => $"{appliedCount} optimizers are verified; {6 - appliedCount} still " +
                 ((6 - appliedCount) == 1 ? "needs" : "need") + " attention."
        };

        RefreshLiveMemory();
    }

    private void RefreshLauncherTile(string module, ref int appliedCount)
    {
        var state = ReadModuleState($"{module}-optimizer.json");
        var targetCount = 0;
        try
        {
            var path = Path.Combine(PathHelper.AppDataDir, $"{module}-optimizer.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("targetCount", out var count) && count.TryGetInt32(out var parsed))
                    targetCount = parsed;
            }
        }
        catch { }
        if (state.Applied) appliedCount++;
        var tag = state.Applied ? "VERIFIED" : !string.IsNullOrWhiteSpace(state.Detail) ? "NEEDS ATTENTION" : "NOT APPLIED";
        var primary = state.Applied ? "Game policy active" : module == "riot" ? "Riot ready" : "Epic ready";
        var secondary = state.Applied
            ? "High-performance GPU · Above Normal CPU · startup quiet"
            : module == "riot" ? "Detect VALORANT and League automatically" : "Discover installed games from Epic manifests";
        var metric = state.Applied
            ? $"{targetCount} game executable(s) verified · exact Repair ready"
            : module == "riot" ? "Anti-cheat and Riot services stay untouched" : "Launcher files, caches, and updates stay untouched";
        if (module == "riot")
        {
            RiotStatusTag = tag; RiotStatusPrimary = primary; RiotStatusSecondary = secondary; RiotLiveMetric = metric;
        }
        else
        {
            EpicStatusTag = tag; EpicStatusPrimary = primary; EpicStatusSecondary = secondary; EpicLiveMetric = metric;
        }
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

public partial class OptimizerCardViewModel : ObservableObject
{
    public required OptimizerDefinition Definition { get; init; }

    [ObservableProperty] public partial bool IsComingSoon { get; set; }

    public void InitializePresentation()
    {
        IsComingSoon = Definition.Status == OptimizerStatus.ComingSoon;
    }
}
