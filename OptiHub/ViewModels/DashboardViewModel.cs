using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OptiHub.Models;
using OptiHub.Services;

namespace OptiHub.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly List<OptimizerCardViewModel> _allCards;

    public DashboardViewModel(AppServices services)
    {
        _services = services;
        _allCards = new List<OptimizerCardViewModel>
        {
            Card("discord", "Discord", "App voice & client stack", "Assets/Logos/discord.png", OptimizerStatus.Available, HubSection.Apps),
            Card("steam", "Steam", "Downloads · library · client", "Assets/Logos/steam.png", OptimizerStatus.Available, HubSection.Apps),
            Card("brave", "Brave", "Browser performance", "Assets/Logos/brave.png", OptimizerStatus.ComingSoon, HubSection.Apps),
            Card("riot", "Riot", "Client · Vanguard stack", "Assets/Logos/riot.png", OptimizerStatus.ComingSoon, HubSection.Apps),
            Card("epic", "Epic", "Launcher · overlays", "Assets/Logos/epic.png", OptimizerStatus.ComingSoon, HubSection.Apps),
            Card("internet", "Internet", "TCP · NIC · latency · throughput", "Assets/Logos/optihub.png", OptimizerStatus.Available, HubSection.Internet),
            Card("nvidia", "NVIDIA", "Driver · panel · display", "Assets/Logos/nvidia.png", OptimizerStatus.Available, HubSection.Gpu),
            Card("amd", "AMD", "Driver stack · coming soon", "Assets/Logos/amd.png", OptimizerStatus.ComingSoon, HubSection.Gpu),
        };

        foreach (var card in _allCards)
            card.InitializePresentation();

        Cards = new ObservableCollection<OptimizerCardViewModel>();
        StatTiles = new ObservableCollection<HomeStatTileViewModel>();
        SidebarCollapsed = _services.Settings.Current.SidebarCollapsed;
        ApplySection(HubSection.Home);
    }

    public ObservableCollection<OptimizerCardViewModel> Cards { get; }
    public ObservableCollection<HomeStatTileViewModel> StatTiles { get; }

    [ObservableProperty] private string _sectionTitle = "Home";
    [ObservableProperty] private string _sectionCaption = "System overview";
    [ObservableProperty] private bool _isHome;
    [ObservableProperty] private bool _isLoadingStats;
    [ObservableProperty] private bool _sidebarCollapsed;
    [ObservableProperty] private string _greetingLine = "Welcome back";
    [ObservableProperty] private string _machineLine = string.Empty;
    [ObservableProperty] private string _statusFooter = string.Empty;

    public event EventHandler<string>? NavigateToOptimizer;

    public void ApplySection(HubSection section)
    {
        IsHome = section == HubSection.Home;
        SectionTitle = section switch
        {
            HubSection.Home => "Home",
            HubSection.Internet => "Internet",
            HubSection.Gpu => "GPU",
            _ => "Apps"
        };
        SectionCaption = section switch
        {
            HubSection.Home => "Live system · network · optimizers",
            HubSection.Internet => "TCP · NIC · latency · throughput",
            HubSection.Gpu => "NVIDIA · AMD",
            _ => "Discord · Steam · more"
        };

        Cards.Clear();
        if (!IsHome)
        {
            foreach (var c in _allCards.Where(c => c.Section == section))
                Cards.Add(c);
        }
    }

    [RelayCommand]
    private void ToggleSidebar()
    {
        SidebarCollapsed = !SidebarCollapsed;
        _services.Settings.Update(s => s.SidebarCollapsed = SidebarCollapsed);
    }

    [RelayCommand]
    private void OpenOptimizer(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var card = _allCards.FirstOrDefault(c => c.Definition.Id == id);
        if (card is null || card.Definition.Status == OptimizerStatus.ComingSoon) return;
        NavigateToOptimizer?.Invoke(this, id);
    }

    public async Task RefreshStatesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var card in _allCards)
            card.IsLoadingState = false;

        if (!IsHome) return;

        IsLoadingStats = true;
        try
        {
            var ready = _allCards.Count(c => c.Definition.Status == OptimizerStatus.Available);
            var soon = _allCards.Count(c => c.Definition.Status == OptimizerStatus.ComingSoon);
            var snap = await _services.Stats.CollectAsync(_services.Network, ready, soon, ct);

            GreetingLine = BuildGreeting();
            MachineLine = $"{snap.MachineName} · {snap.OsName}";
            StatusFooter = snap.ProbeOk
                ? $"OptiHub {snap.AppVersion} · network preset: {snap.NetworkPreset}"
                : $"OptiHub {snap.AppVersion} · {snap.Detail}";

            StatTiles.Clear();
            StatTiles.Add(Tile("PC", snap.MachineName, snap.OsName, "\uE770", null));
            StatTiles.Add(Tile("Uptime", snap.Uptime, "Since last boot", "\uE823", null));
            StatTiles.Add(Tile("CPU", Truncate(snap.CpuName, 42), "Processor", "\uE950", null));
            StatTiles.Add(Tile("GPU", Truncate(snap.GpuName, 42), "Primary display adapter", "\uE7F4", null));
            StatTiles.Add(Tile("Memory", snap.RamLine, $"{snap.RamPercent:0}% in use", "\uE964", snap.RamPercent));
            StatTiles.Add(Tile("Link", snap.LinkSpeed, Truncate(snap.NetworkLine, 48), "\uE839", null));
            StatTiles.Add(Tile("Latency", snap.LatencyLine, Truncate(snap.ProviderLine, 48), "\uE774", null));
            StatTiles.Add(Tile("Optimizers", $"{snap.OptimizersReady} ready", $"{snap.OptimizersSoon} coming soon", "\uE8F1", null));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            StatusFooter = ex.Message;
        }
        finally
        {
            IsLoadingStats = false;
        }
    }

    private static string BuildGreeting()
    {
        var hour = DateTime.Now.Hour;
        if (hour < 5) return "Late night session";
        if (hour < 12) return "Good morning";
        if (hour < 17) return "Good afternoon";
        if (hour < 21) return "Good evening";
        return "Night ops";
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length <= max) return s;
        return s[..(max - 1)].TrimEnd() + "…";
    }

    private static HomeStatTileViewModel Tile(
        string label, string value, string detail, string glyph, double? bar) => new()
    {
        Label = label,
        Value = value,
        Detail = detail,
        Glyph = glyph,
        ShowBar = bar is not null,
        BarValue = bar ?? 0
    };

    private static OptimizerCardViewModel Card(
        string id, string title, string caption, string logo, OptimizerStatus status, HubSection section) =>
        new()
        {
            Definition = new OptimizerDefinition
            {
                Id = id,
                Title = title,
                LogoPath = logo,
                Status = status
            },
            Caption = caption,
            Section = section
        };
}

public partial class OptimizerCardViewModel : ObservableObject
{
    public required OptimizerDefinition Definition { get; init; }
    public HubSection Section { get; init; }
    public string Caption { get; init; } = string.Empty;

    [ObservableProperty] private bool _isLoadingState;
    [ObservableProperty] private bool _isComingSoon;

    public void InitializePresentation()
    {
        IsComingSoon = Definition.Status == OptimizerStatus.ComingSoon;
    }
}

public sealed class HomeStatTileViewModel
{
    public required string Label { get; init; }
    public required string Value { get; init; }
    public required string Detail { get; init; }
    public required string Glyph { get; init; }
    public bool ShowBar { get; init; }
    public double BarValue { get; init; }
}
