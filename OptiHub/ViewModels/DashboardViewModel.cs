using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OptiHub.Models;
using OptiHub.Services;

namespace OptiHub.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly AppServices _services;

    public DashboardViewModel(AppServices services)
    {
        _services = services;
        Cards = new List<OptimizerCardViewModel>
        {
            new()
            {
                Definition = new OptimizerDefinition
                {
                    Id = "discord",
                    Title = "Discord Optimizer",
                    LogoPath = "Assets/Logos/discord.png",
                    Status = OptimizerStatus.Available
                }
            },
            new()
            {
                Definition = new OptimizerDefinition
                {
                    Id = "steam",
                    Title = "Steam Optimizer",
                    LogoPath = "Assets/Logos/steam.png",
                    Status = OptimizerStatus.Available
                }
            },
            new()
            {
                Definition = new OptimizerDefinition
                {
                    Id = "nvidia",
                    Title = "NVIDIA Optimizer",
                    LogoPath = "Assets/Logos/nvidia.png",
                    Status = OptimizerStatus.Available
                }
            },
            new()
            {
                Definition = new OptimizerDefinition
                {
                    Id = "brave",
                    Title = "Brave Optimizer",
                    LogoPath = "Assets/Logos/brave.png",
                    Status = OptimizerStatus.ComingSoon
                }
            },
            new()
            {
                Definition = new OptimizerDefinition
                {
                    Id = "riot",
                    Title = "Riot Games Optimizer",
                    LogoPath = "Assets/Logos/riot.png",
                    Status = OptimizerStatus.ComingSoon
                }
            },
            new()
            {
                Definition = new OptimizerDefinition
                {
                    Id = "epic",
                    Title = "Epic Launcher Optimizer",
                    LogoPath = "Assets/Logos/epic.png",
                    Status = OptimizerStatus.ComingSoon
                }
            }
        };

        foreach (var card in Cards)
            card.InitializePresentation();
    }

    public IReadOnlyList<OptimizerCardViewModel> Cards { get; }

    public event EventHandler<string>? NavigateToOptimizer;

    [RelayCommand]
    private void OpenOptimizer(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var card = Cards.FirstOrDefault(c => c.Definition.Id == id);
        if (card is null || card.Definition.Status == OptimizerStatus.ComingSoon) return;
        NavigateToOptimizer?.Invoke(this, id);
    }

    /// <summary>
    /// Home cards no longer pre-claim "Optimized" from a fast heuristic.
    /// Live status is only computed when the user opens that optimizer page.
    /// </summary>
    public Task RefreshStatesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var card in Cards)
            card.IsLoadingState = false;
        return Task.CompletedTask;
    }
}

public partial class OptimizerCardViewModel : ObservableObject
{
    public required OptimizerDefinition Definition { get; init; }

    [ObservableProperty]
    private bool _isLoadingState;

    [ObservableProperty]
    private bool _isComingSoon;

    public void InitializePresentation()
    {
        IsComingSoon = Definition.Status == OptimizerStatus.ComingSoon;
    }
}
