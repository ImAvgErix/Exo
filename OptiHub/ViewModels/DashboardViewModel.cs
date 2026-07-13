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
            Card("discord", "Discord", "Assets/Logos/discord.png", OptimizerStatus.Available, HubSection.Apps),
            Card("steam", "Steam", "Assets/Logos/steam.png", OptimizerStatus.Available, HubSection.Apps),
            Card("brave", "Brave", "Assets/Logos/brave.png", OptimizerStatus.ComingSoon, HubSection.Apps),
            Card("riot", "Riot", "Assets/Logos/riot.png", OptimizerStatus.ComingSoon, HubSection.Apps),
            Card("epic", "Epic", "Assets/Logos/epic.png", OptimizerStatus.ComingSoon, HubSection.Apps),
            Card("internet", "Internet", "Assets/Logos/optihub.png", OptimizerStatus.Available, HubSection.Internet),
            Card("nvidia", "NVIDIA", "Assets/Logos/nvidia.png", OptimizerStatus.Available, HubSection.Gpu),
            Card("amd", "AMD", "Assets/Logos/amd.png", OptimizerStatus.ComingSoon, HubSection.Gpu),
        };

        foreach (var card in _allCards)
            card.InitializePresentation();

        Cards = new ObservableCollection<OptimizerCardViewModel>();
        ApplySection(HubSection.Apps);
    }

    public ObservableCollection<OptimizerCardViewModel> Cards { get; }

    [ObservableProperty] private string _sectionTitle = "Apps";
    [ObservableProperty] private string _sectionCaption = "App optimizers";

    public event EventHandler<string>? NavigateToOptimizer;

    public void ApplySection(HubSection section)
    {
        SectionTitle = section switch
        {
            HubSection.Internet => "Internet",
            HubSection.Gpu => "GPU",
            _ => "Apps"
        };
        SectionCaption = section switch
        {
            HubSection.Internet => "TCP · NIC · latency · throughput",
            HubSection.Gpu => "NVIDIA · AMD",
            _ => "Discord · Steam · more"
        };

        Cards.Clear();
        foreach (var c in _allCards.Where(c => c.Section == section))
            Cards.Add(c);
    }

    [RelayCommand]
    private void OpenOptimizer(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var card = _allCards.FirstOrDefault(c => c.Definition.Id == id);
        if (card is null || card.Definition.Status == OptimizerStatus.ComingSoon) return;
        NavigateToOptimizer?.Invoke(this, id);
    }

    public Task RefreshStatesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var card in _allCards)
            card.IsLoadingState = false;
        return Task.CompletedTask;
    }

    private static OptimizerCardViewModel Card(
        string id, string title, string logo, OptimizerStatus status, HubSection section) =>
        new()
        {
            Definition = new OptimizerDefinition
            {
                Id = id,
                Title = title,
                LogoPath = logo,
                Status = status
            },
            Section = section
        };
}

public partial class OptimizerCardViewModel : ObservableObject
{
    public required OptimizerDefinition Definition { get; init; }
    public HubSection Section { get; init; }

    [ObservableProperty] private bool _isLoadingState;
    [ObservableProperty] private bool _isComingSoon;

    public void InitializePresentation()
    {
        IsComingSoon = Definition.Status == OptimizerStatus.ComingSoon;
    }
}
