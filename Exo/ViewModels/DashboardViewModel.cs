using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Exo.Helpers;
using Exo.Models;
using Exo.Services;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Exo.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private const int SparkCapacity = 36;
    private readonly AppServices _services;
    private readonly Queue<double> _ramSpark = new();
    private readonly Queue<double> _cpuSpark = new();
    private readonly Queue<double> _gpuSpark = new();
    private readonly Queue<double> _netSpark = new();

    public DashboardViewModel(AppServices services)
    {
        _services = services;
        Cards = new List<OptimizerCardViewModel>
        {
            Card("discord", "Discord", "Assets/Logos/discord.png", OptimizerStatus.Available),
            Card("steam", "Steam", "Assets/Logos/steam.png", OptimizerStatus.Available),
            Card("windows", "Windows", "Assets/Logos/windows.png", OptimizerStatus.Available),
            Card("internet", "Internet", "Assets/Logos/internet.png", OptimizerStatus.Available),
            Card("nvidia", "NVIDIA", "Assets/Logos/nvidia.png", OptimizerStatus.Available),
            Card("amd", "AMD", "Assets/Logos/amd.png", OptimizerStatus.ComingSoon),
            Card("brave", "Brave", "Assets/Logos/brave.png", OptimizerStatus.ComingSoon),
            Card("riot", "Riot", "Assets/Logos/riot.png", OptimizerStatus.Available),
            Card("epic", "Epic", "Assets/Logos/epic.png", OptimizerStatus.Available),
        };

        foreach (var card in Cards)
            card.InitializePresentation();

        SoonCards = Cards.Where(c => c.IsComingSoon).ToList();

        CheckRows =
        [
            new OptimizerCheckRowViewModel("Discord", "discord"),
            new OptimizerCheckRowViewModel("Steam", "steam"),
            new OptimizerCheckRowViewModel("Games", "games"),
            new OptimizerCheckRowViewModel("Windows", "windows"),
            new OptimizerCheckRowViewModel("Internet", "internet"),
            new OptimizerCheckRowViewModel("NVIDIA", "nvidia"),
            new OptimizerCheckRowViewModel("Riot", "riot"),
            new OptimizerCheckRowViewModel("Epic", "epic"),
        ];

        RamSeries = new ObservableCollection<double>();
        CpuSeries = new ObservableCollection<double>();
        GpuSeries = new ObservableCollection<double>();
        NetSeries = new ObservableCollection<double>();

        RefreshDashboard(seedChecks: true);
    }

    public IReadOnlyList<OptimizerCardViewModel> Cards { get; }
    public IReadOnlyList<OptimizerCardViewModel> SoonCards { get; }
    public ObservableCollection<OptimizerCheckRowViewModel> CheckRows { get; }
    public ObservableCollection<double> RamSeries { get; }
    public ObservableCollection<double> CpuSeries { get; }
    public ObservableCollection<double> GpuSeries { get; }
    public ObservableCollection<double> NetSeries { get; }

    [ObservableProperty] public partial string HeroSummary { get; set; } = "Maximum performance. No compromise.";
    [ObservableProperty] public partial string OverviewPrimary { get; set; } = "0 / 7 verified";
    [ObservableProperty] public partial string AppliedModulesList { get; set; } = "None applied yet";

    [ObservableProperty] public partial string NextActionModule { get; set; } = string.Empty;
    [ObservableProperty] public partial string NextActionLabel { get; set; } = string.Empty;
    [ObservableProperty] public partial string NextActionDetail { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasNextAction { get; set; }

    [ObservableProperty] public partial string SpecsCpu { get; set; } = "—";
    [ObservableProperty] public partial string SpecsGpu { get; set; } = "—";
    [ObservableProperty] public partial string SpecsRam { get; set; } = "—";
    [ObservableProperty] public partial string SpecsOs { get; set; } = "—";
    [ObservableProperty] public partial string SpecsSecondary { get; set; } = "Reading machine…";

    [ObservableProperty] public partial string MemoryPrimary { get; set; } = "—";
    [ObservableProperty] public partial string MemorySecondary { get; set; } = "Reading…";
    [ObservableProperty] public partial string MemoryLoadText { get; set; } = "";
    [ObservableProperty] public partial double MemoryLoadPercent { get; set; }
    [ObservableProperty] public partial bool HasMemory { get; set; }

    [ObservableProperty] public partial string CpuPrimary { get; set; } = "—";
    [ObservableProperty] public partial string CpuSecondary { get; set; } = "Sampling…";
    [ObservableProperty] public partial double CpuLoadPercent { get; set; }
    [ObservableProperty] public partial bool HasCpuLoad { get; set; }

    [ObservableProperty] public partial string GpuPrimary { get; set; } = "—";
    [ObservableProperty] public partial string GpuSecondary { get; set; } = "GPU";
    [ObservableProperty] public partial double GpuLoadPercent { get; set; }
    [ObservableProperty] public partial bool HasGpuLoad { get; set; }

    [ObservableProperty] public partial string MemSpeedPrimary { get; set; } = "—";
    [ObservableProperty] public partial string MemSpeedSecondary { get; set; } = "DRAM speed";

    [ObservableProperty] public partial string NetPrimary { get; set; } = "—";
    [ObservableProperty] public partial string NetSecondary { get; set; } = "Link / idle latency";
    [ObservableProperty] public partial double NetMetricPercent { get; set; }

    // Kept for RefreshDashboard tile logic + Ui.Smoke source contracts.
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
    [ObservableProperty] public partial string WindowsStatusPrimary { get; set; } = "Windows ready";
    [ObservableProperty] public partial string WindowsStatusSecondary { get; set; } = "Apply host Game Mode, HAGS, Game Bar, and priority";
    [ObservableProperty] public partial string WindowsStatusTag { get; set; } = "NOT APPLIED";
    [ObservableProperty] public partial string WindowsLiveMetric { get; set; } = "Host gaming stack not applied";
    [ObservableProperty] public partial string GamesStatusPrimary { get; set; } = "Games ready";
    [ObservableProperty] public partial string GamesStatusSecondary { get; set; } = "Potato or Optimized profiles for installed games";
    [ObservableProperty] public partial string GamesStatusTag { get; set; } = "NOT APPLIED";
    [ObservableProperty] public partial string GamesLiveMetric { get; set; } = "No game profile applied";
    [ObservableProperty] public partial string FpsPrimary { get; set; } = "—";
    [ObservableProperty] public partial string FpsSecondary { get; set; } = "";
    [ObservableProperty] public partial string FrameTimePrimary { get; set; } = "—";
    [ObservableProperty] public partial string FrameTimeSecondary { get; set; } = "";

    /// <summary>True outcomes after last silent detect pass (used by checklist animation).</summary>
    public IReadOnlyDictionary<string, bool> LastCheckOutcomes { get; private set; } =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    public Task RefreshStatesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RefreshDashboard(seedChecks: false);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Drive the checklist UI through Checking → Applied/Missing for each row.
    /// Call from the page after RefreshStatesAsync so outcomes are ready.
    /// </summary>
    /// <summary>Raised when a chip settles to Applied/Missing so the page can pop the glyph.</summary>
    public event Action<OptimizerCheckRowViewModel>? CheckRowSettled;

    public async Task PlayCheckSequenceAsync(CancellationToken ct = default)
    {
        foreach (var row in CheckRows)
            row.SetPhase(OptimizerCheckPhase.Idle);

        await Task.Delay(60, ct).ConfigureAwait(true);

        foreach (var row in CheckRows)
        {
            ct.ThrowIfCancellationRequested();
            row.SetPhase(OptimizerCheckPhase.Checking);
            await Task.Delay(120, ct).ConfigureAwait(true);

            var ok = LastCheckOutcomes.TryGetValue(row.ModuleId, out var applied) && applied;
            row.SetPhase(ok ? OptimizerCheckPhase.Applied : OptimizerCheckPhase.Missing);
            try { CheckRowSettled?.Invoke(row); } catch { }
            await Task.Delay(55, ct).ConfigureAwait(true);
        }
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
            MemoryPrimary = $"{mem.LoadPercent}%";
            MemorySecondary =
                $"{HomeDashboardReader.FormatBytes(used)} / {HomeDashboardReader.FormatBytes(mem.TotalBytes)}";
            PushSpark(_ramSpark, RamSeries, mem.LoadPercent);
        }

        var cpu = HomeDashboardReader.TryReadCpuLoadPercent();
        if (cpu is null)
        {
            if (!HasCpuLoad)
            {
                CpuPrimary = "—";
                CpuSecondary = "Sampling…";
                CpuLoadPercent = 0;
            }
        }
        else
        {
            HasCpuLoad = true;
            CpuLoadPercent = Math.Round(cpu.Value, 0);
            CpuPrimary = $"{CpuLoadPercent:0}%";
            CpuSecondary = "Load";
            PushSpark(_cpuSpark, CpuSeries, CpuLoadPercent);
        }

        var gpu = HomeDashboardReader.TryReadGpuLoadPercent();
        if (gpu is null)
        {
            if (!HasGpuLoad)
            {
                GpuPrimary = "—";
                GpuSecondary = "Load";
                GpuLoadPercent = 0;
            }
        }
        else
        {
            HasGpuLoad = true;
            GpuLoadPercent = Math.Round(gpu.Value, 0);
            GpuPrimary = $"{GpuLoadPercent:0}%";
            GpuSecondary = "Load";
            PushSpark(_gpuSpark, GpuSeries, GpuLoadPercent);
        }

        // DRAM speed kept for API/smokes if needed, but not shown as a redundant tile.
        var mhz = HomeDashboardReader.TryReadMemorySpeedMhz();
        if (mhz is > 0)
        {
            MemSpeedPrimary = $"{mhz} MT/s";
            MemSpeedSecondary = "DRAM";
        }
        else if (mem is not null)
        {
            MemSpeedPrimary = HomeDashboardReader.FormatBytes(mem.TotalBytes);
            MemSpeedSecondary = "Installed";
        }

        RefreshNetworkLiveTile();
        RefreshDiscordRamTile();
        RefreshSteamRamTile();
        RefreshInternetTile();
    }

    private void RefreshNetworkLiveTile()
    {
        var link = HomeDashboardReader.TryReadPrimaryLinkSpeed();
        var latency = HomeDashboardReader.TryReadLatency(_services.Network);
        var quality = _services.Network.LoadQualityBenchmark();

        // Caption = link; hero = idle latency (honest sample, not a fake “health” score).
        if (link is not null && link.BitsPerSecond > 0)
            NetPrimary = $"{link.Label} {link.MediaKind}";
        else
            NetPrimary = "No link";

        double? idleMs = null;
        if (quality is { Ok: true, IsQualityTest: true })
            idleMs = quality.PingP50Ms;
        else if (latency is not null)
            idleMs = latency.AfterP50Ms;

        if (idleMs is not null)
        {
            NetSecondary = $"{idleMs.Value:0.#} ms idle";
            // Bar: lower latency fills more (visual only; label is the ms).
            var fill = Math.Clamp(100.0 - (idleMs.Value / 80.0 * 100.0), 4, 100);
            NetMetricPercent = fill;
            PushSpark(_netSpark, NetSeries, fill);
        }
        else
        {
            NetSecondary = "No sample yet";
            NetMetricPercent = 0;
        }
    }

    private static void PushSpark(Queue<double> queue, ObservableCollection<double> series, double value)
    {
        var v = Math.Clamp(value, 0, 100);
        queue.Enqueue(v);
        while (queue.Count > SparkCapacity)
            queue.Dequeue();

        // Replace series contents so ExoSparkline CollectionChanged rebuilds the path.
        series.Clear();
        foreach (var sample in queue)
            series.Add(sample);
    }

    private void RefreshSystemSpecs()
    {
        var specs = HomeDashboardReader.TryReadSystemSpecs();
        if (specs is null)
        {
            SpecsCpu = "—";
            SpecsGpu = "—";
            SpecsRam = "—";
            SpecsOs = "—";
            SpecsSecondary = "System specs unavailable";
            return;
        }

        SpecsCpu = specs.CpuName;
        SpecsGpu = !string.IsNullOrWhiteSpace(specs.GpuName) ? specs.GpuName! : "—";
        SpecsRam = !string.IsNullOrWhiteSpace(specs.RamLabel)
            ? specs.RamLabel
            : specs.TotalRamBytes > 0
                ? HomeDashboardReader.FormatBytes(specs.TotalRamBytes)
                : "—";
        SpecsOs = !string.IsNullOrWhiteSpace(specs.OsName) ? specs.OsName : "—";
        SpecsSecondary = string.Join(" · ", new[] { SpecsCpu, SpecsGpu, SpecsRam, SpecsOs }
            .Where(s => !string.IsNullOrWhiteSpace(s) && s != "—"));
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
            LatencySecondary = $"Adaptive stack applied - {dns}";
            InternetLiveMetric = quality is { Ok: true, IsQualityTest: true }
                ? BuildInternetLiveMetric(quality, latency)
                : latency is not null
                    ? $"{latency.AfterP50Ms:0.#} ms idle - {latency.AfterJitterMs:0.#} ms jitter"
                    : "Run Analyze & Apply to refresh the route sample";
            return;
        }

        if (latency is not null)
        {
            HasLatency = true;
            InternetStatusTag = "MEASURED";
            LatencyPrimary = linkBit ?? "Route measured";
            LatencySecondary = "Current route sample available - settings not verified";
            InternetLiveMetric = $"{latency.AfterP50Ms:0.#} ms idle - {latency.AfterJitterMs:0.#} ms jitter";
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
        return $"{quality.PingP50Ms:0.#} ms idle - full-load +{downLoaded:0.#} down / +{upLoaded:0.#} up - {quality.PacketLossPercent:0.##}% idle loss";
    }

    private void RefreshDiscordRamTile()
    {
        var sample = HomeDashboardReader.TrySampleDiscordRam();
        var kernel = HomeDashboardReader.TryReadDiscordKernelOnDisk();
        var applied = HomeDashboardReader.TryReadDiscordApplied();
        var live = sample?.LiveBytes ?? 0;
        var belowPeak = sample?.BelowPeakBytes ?? 0;

        if (belowPeak >= 8L << 20)
        {
            DiscordLiveMetric = live > 0
                ? $"{HomeDashboardReader.FormatBytes(live)} resident - {HomeDashboardReader.FormatBytes(belowPeak)} below session peak"
                : $"{HomeDashboardReader.FormatBytes(belowPeak)} below session peak";
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
            DiscordStatusSecondary = "Privacy patch - voice QoS - idle memory guard";
        }
        else if (applied)
        {
            DiscordStatusTag = "APPLIED";
            DiscordStatusPrimary = "Stock-safe mode";
            DiscordStatusSecondary = "Voice QoS and privacy settings applied - custom kernel skipped";
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
            SteamLiveMetric = $"{HomeDashboardReader.FormatBytes(memory.WorkingSetBytes)} resident - {HomeDashboardReader.FormatBytes(memory.PrivateBytes)} private";
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
                ? "Foreground stays responsive - background CEF yields while gaming"
                : "Starts with the optimized launcher - no unsafe RAM purges";
        }
        else
        {
            SteamStatusTag = "NOT APPLIED";
            SteamStatusPrimary = "Steam ready";
            SteamStatusSecondary = "Apply lean library, background-memory, and in-game CPU policy";
        }
    }

    private void RefreshDashboard(bool seedChecks)
    {
        var appliedCount = 0;
        var appliedNames = new List<string>(6);
        var outcomes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        RefreshSystemSpecs();

        RefreshDiscordRamTile();
        RefreshSteamRamTile();
        var discordOk = HomeDashboardReader.TryReadDiscordApplied() || HomeDashboardReader.TryReadDiscordKernelOnDisk();
        outcomes["discord"] = discordOk;
        if (discordOk) { appliedCount++; appliedNames.Add("Discord"); }

        var steamOk = ReadModuleState("steam-optimizer.json").Applied;
        outcomes["steam"] = steamOk;
        if (steamOk) { appliedCount++; appliedNames.Add("Steam"); }

        var windowsOk = ReadModuleState("windows-optimizer.json").Applied;
        outcomes["windows"] = windowsOk;
        if (windowsOk)
        {
            appliedCount++;
            appliedNames.Add("Windows");
            WindowsStatusTag = "VERIFIED";
            WindowsStatusPrimary = "Host stack active";
            WindowsStatusSecondary = "Game Mode, HAGS, Game Bar quiet, foreground boost";
            WindowsLiveMetric = "Windows host gaming stack verified";
        }
        else
        {
            WindowsStatusTag = "NOT APPLIED";
            WindowsStatusPrimary = "Windows ready";
            WindowsStatusSecondary = "Apply host Game Mode, HAGS, Game Bar, and priority";
            WindowsLiveMetric = "Host gaming stack not applied";
        }

        var beforeNet = appliedCount;
        RefreshInternetTile(ref appliedCount);
        var internetOk = appliedCount > beforeNet;
        outcomes["internet"] = internetOk;
        if (internetOk) appliedNames.Add("Internet");

        var nvidia = HomeDashboardReader.TryReadNvidiaPath();
        if (nvidia is not null)
        {
            appliedCount++;
            appliedNames.Add("NVIDIA");
            outcomes["nvidia"] = true;
            HasNvidiaPath = true;
            NvidiaStatusTag = "VERIFIED";
            NvidiaPathPrimary = !string.IsNullOrWhiteSpace(nvidia.GpuName)
                ? nvidia.GpuName!
                : !string.IsNullOrWhiteSpace(nvidia.Series) ? nvidia.Series! : "NVIDIA profile active";
            var display = !string.IsNullOrWhiteSpace(nvidia.PrimaryMode)
                ? $" - {nvidia.PrimaryMode} {nvidia.PrimaryConnection}".TrimEnd()
                : string.Empty;
            NvidiaPathSecondary = nvidia.Gsync
                ? $"G-SYNC/VRR profile{display}"
                : $"Raw-latency profile{display}";
            var proof = new List<string>();
            if (nvidia.VerifiedSettingCount > 0) proof.Add($"{nvidia.VerifiedSettingCount} driver pins");
            if (nvidia.GameProfileCount > 0) proof.Add($"{nvidia.GameProfileCount} game profiles");
            NvidiaLiveMetric = proof.Count > 0
                ? string.Join(" - ", proof) + " verified"
                : "Driver profile import verified";
        }
        else
        {
            outcomes["nvidia"] = false;
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

        var beforeRiot = appliedCount;
        RefreshLauncherTile("riot", ref appliedCount);
        outcomes["riot"] = appliedCount > beforeRiot;
        if (outcomes["riot"]) appliedNames.Add("Riot");

        var beforeEpic = appliedCount;
        RefreshLauncherTile("epic", ref appliedCount);
        outcomes["epic"] = appliedCount > beforeEpic;
        if (outcomes["epic"]) appliedNames.Add("Epic");

        // Games module (config profiles) — pure C# state file.
        var gamesOk = false;
        try
        {
            var gamesState = _services.Games.Detect();
            gamesOk = gamesState.IsApplied;
            if (gamesOk)
            {
                appliedCount++;
                appliedNames.Add("Games");
                GamesStatusTag = "VERIFIED";
                GamesStatusPrimary = gamesState.StatusText;
                GamesStatusSecondary = gamesState.Detail;
                GamesLiveMetric = gamesState.Extra is not null
                    && gamesState.Extra.TryGetValue("activePreset", out var ap)
                    && !string.IsNullOrWhiteSpace(ap)
                    ? $"Profile: {ap}"
                    : "Game profile verified";
            }
            else
            {
                GamesStatusTag = "NOT APPLIED";
                GamesStatusPrimary = gamesState.StatusText;
                GamesStatusSecondary = gamesState.Detail;
                GamesLiveMetric = "Pick Potato or Optimized, then Apply";
            }
        }
        catch
        {
            GamesStatusTag = "NOT APPLIED";
            GamesStatusPrimary = "Games ready";
            GamesStatusSecondary = "Potato or Optimized profiles for installed games";
            GamesLiveMetric = "No game profile applied";
        }
        outcomes["games"] = gamesOk;

        LastCheckOutcomes = outcomes;

        if (seedChecks)
        {
            foreach (var row in CheckRows)
            {
                var ok = outcomes.TryGetValue(row.ModuleId, out var a) && a;
                row.SetPhase(ok ? OptimizerCheckPhase.Applied : OptimizerCheckPhase.Missing);
            }
        }

        FpsPrimary = NvidiaPathPrimary;
        FpsSecondary = NvidiaPathSecondary;
        FrameTimePrimary = DiscordStatusPrimary;
        FrameTimeSecondary = DiscordStatusSecondary;

        OverviewPrimary = $"{appliedCount} / 8 verified";
        AppliedModulesList = appliedNames.Count > 0
            ? string.Join(" · ", appliedNames)
            : "None applied yet";
        UpdateNextAction(appliedCount);
        // Keep one short line for any residual bindings; header no longer stacks essays.
        HeroSummary = appliedCount == 8
            ? "All optimizers verified"
            : HasNextAction
                ? $"Next: {NextActionModule}"
                : $"{appliedCount}/8 verified";

        RefreshLiveMemory();
    }

    private void UpdateNextAction(int appliedCount)
    {
        if (appliedCount >= 8)
        {
            HasNextAction = false;
            NextActionModule = string.Empty;
            NextActionLabel = string.Empty;
            NextActionDetail = string.Empty;
            return;
        }

        (string Id, string Label, string Tag, string Detail)[] candidates =
        [
            ("Discord", "Open Discord", DiscordStatusTag, DiscordStatusSecondary),
            ("Steam", "Open Steam", SteamStatusTag, SteamStatusSecondary),
            ("Games", "Open Games", GamesStatusTag, GamesStatusSecondary),
            ("Windows", "Open Windows", WindowsStatusTag, WindowsStatusSecondary),
            ("Internet", "Open Internet", InternetStatusTag, LatencySecondary),
            ("NVIDIA", "Open NVIDIA", NvidiaStatusTag, NvidiaPathSecondary),
            ("Riot", "Open Riot", RiotStatusTag, RiotStatusSecondary),
            ("Epic", "Open Epic", EpicStatusTag, EpicStatusSecondary),
        ];

        foreach (var c in candidates)
        {
            if (string.Equals(c.Tag, "VERIFIED", StringComparison.OrdinalIgnoreCase))
                continue;

            HasNextAction = true;
            NextActionModule = c.Id;
            NextActionLabel = string.Equals(c.Tag, "NEEDS ATTENTION", StringComparison.OrdinalIgnoreCase)
                ? $"Fix {c.Id}"
                : c.Id;
            NextActionDetail = string.IsNullOrWhiteSpace(c.Detail)
                ? "Detect this PC, then Apply only supported changes."
                : c.Detail;
            return;
        }

        HasNextAction = false;
        NextActionModule = string.Empty;
        NextActionLabel = string.Empty;
        NextActionDetail = string.Empty;
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
        var primary = state.Applied ? "Launcher policy active" : module == "riot" ? "Riot ready" : "Epic ready";
        var secondary = state.Applied
            ? "Startup quiet - launcher yield while gaming"
            : module == "riot" ? "Detect VALORANT and League automatically" : "Discover installed games from Epic manifests";
        var metric = state.Applied
            ? $"{targetCount} game executable(s) for yield detect - anti-cheat untouched"
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

public enum OptimizerCheckPhase
{
    Idle,
    Checking,
    Applied,
    Missing
}

public partial class OptimizerCheckRowViewModel : ObservableObject
{
    private static readonly SolidColorBrush IdleBrush = new(Color.FromArgb(0xFF, 0x6B, 0x6B, 0x70));
    private static readonly SolidColorBrush CheckingBrush = new(Color.FromArgb(0xFF, 0xC8, 0xC8, 0xCC));
    private static readonly SolidColorBrush AppliedBrush = new(Color.FromArgb(0xFF, 0x3D, 0xDC, 0x84));
    private static readonly SolidColorBrush MissingBrush = new(Color.FromArgb(0xFF, 0xF0, 0x5B, 0x5B));

    public OptimizerCheckRowViewModel(string title, string moduleId)
    {
        Title = title;
        ModuleId = moduleId;
        SetPhase(OptimizerCheckPhase.Idle);
    }

    public string Title { get; }
    public string ModuleId { get; }

    [ObservableProperty] public partial string StatusLabel { get; set; } = "…";
    [ObservableProperty] public partial string Glyph { get; set; } = "\uE915"; // circle
    [ObservableProperty] public partial Brush GlyphBrush { get; set; } = IdleBrush;
    [ObservableProperty] public partial double GlyphOpacity { get; set; } = 0.45;
    [ObservableProperty] public partial double PulseOpacity { get; set; }
    [ObservableProperty] public partial OptimizerCheckPhase Phase { get; set; } = OptimizerCheckPhase.Idle;

    public void SetPhase(OptimizerCheckPhase phase)
    {
        Phase = phase;
        switch (phase)
        {
            case OptimizerCheckPhase.Checking:
                StatusLabel = "Checking…";
                Glyph = "\uE915";
                GlyphBrush = CheckingBrush;
                GlyphOpacity = 0.35;
                PulseOpacity = 0.9;
                break;
            case OptimizerCheckPhase.Applied:
                StatusLabel = "Applied";
                Glyph = "\uE73E";
                GlyphBrush = AppliedBrush;
                GlyphOpacity = 1.0;
                PulseOpacity = 0;
                break;
            case OptimizerCheckPhase.Missing:
                StatusLabel = "Not applied";
                Glyph = "\uE711";
                GlyphBrush = MissingBrush;
                GlyphOpacity = 0.95;
                PulseOpacity = 0;
                break;
            default:
                StatusLabel = "…";
                Glyph = "\uE915";
                GlyphBrush = IdleBrush;
                GlyphOpacity = 0.35;
                PulseOpacity = 0;
                break;
        }
    }
}

public partial class SparkBarViewModel : ObservableObject
{
    [ObservableProperty] public partial double Height { get; set; } = 2;
    [ObservableProperty] public partial double Opacity { get; set; } = 0.15;
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
