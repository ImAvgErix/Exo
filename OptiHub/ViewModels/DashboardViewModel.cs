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
                    Subtitle = string.Empty,
                    Description = string.Empty,
                    AccentGlyph = "\uE8BD",
                    LogoPath = "Assets/Logos/discord.png",
                    Status = OptimizerStatus.Available
                }
            },
            new()
            {
                Definition = new OptimizerDefinition
                {
                    Id = "brave",
                    Title = "Brave Optimizer",
                    Subtitle = "Coming soon",
                    Description = string.Empty,
                    AccentGlyph = "\uE774",
                    LogoPath = "Assets/Logos/brave.png",
                    Status = OptimizerStatus.ComingSoon,
                    Teaser = "Flags, shields, and quiet performance for Brave."
                }
            },
            new()
            {
                Definition = new OptimizerDefinition
                {
                    Id = "steam",
                    Title = "Steam Optimizer",
                    Subtitle = "Coming soon",
                    Description = string.Empty,
                    AccentGlyph = "\uE7FC",
                    LogoPath = "Assets/Logos/steam.png",
                    Status = OptimizerStatus.ComingSoon,
                    Teaser = "Faster Steam, less clutter, better game-ready defaults."
                }
            },
            new()
            {
                Definition = new OptimizerDefinition
                {
                    Id = "riot",
                    Title = "Riot Games Optimizer",
                    Subtitle = "Coming soon",
                    Description = string.Empty,
                    AccentGlyph = "\uE7FC",
                    LogoPath = "Assets/Logos/riot.png",
                    Status = OptimizerStatus.ComingSoon,
                    Teaser = "Leaner Riot Client without fighting anti-cheat."
                }
            },
            new()
            {
                Definition = new OptimizerDefinition
                {
                    Id = "epic",
                    Title = "Epic Launcher Optimizer",
                    Subtitle = "Coming soon",
                    Description = string.Empty,
                    AccentGlyph = "\uE7FC",
                    LogoPath = "Assets/Logos/epic.png",
                    Status = OptimizerStatus.ComingSoon,
                    Teaser = "Less launcher noise, more game time."
                }
            }
        };

        foreach (var card in Cards)
            card.InitializePresentation();
    }

    public List<OptimizerCardViewModel> Cards { get; }

    public event EventHandler<string>? NavigateToOptimizer;
    public event EventHandler? NavigateToSettings;

    [RelayCommand]
    private void OpenSettings() => NavigateToSettings?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenOptimizer(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var card = Cards.FirstOrDefault(c => c.Definition.Id == id);
        if (card is null || card.Definition.Status == OptimizerStatus.ComingSoon) return;
        NavigateToOptimizer?.Invoke(this, id);
    }

    public async Task RefreshStatesAsync()
    {
        var discord = Cards.FirstOrDefault(c => c.Definition.Id == "discord");
        if (discord is null) return;

        discord.IsLoadingState = true;
        try
        {
            var state = await _services.OptimizerState.DetectDiscordAsync(fastOnly: true);
            discord.ApplyState(state);
        }
        finally
        {
            discord.IsLoadingState = false;
        }
    }
}

public partial class OptimizerCardViewModel : ObservableObject
{
    public required OptimizerDefinition Definition { get; init; }

    [ObservableProperty]
    private bool _isLoadingState;

    [ObservableProperty]
    private bool _isComingSoon;

    [ObservableProperty]
    private bool _isApplied;

    public void ApplyState(OptimizerStateInfo state)
    {
        IsComingSoon = Definition.Status == OptimizerStatus.ComingSoon;
        if (IsComingSoon) return;

        IsApplied = state.IsApplied;
        Definition.Status = state.IsApplied ? OptimizerStatus.Applied : OptimizerStatus.Available;
    }

    public void InitializePresentation()
    {
        IsComingSoon = Definition.Status == OptimizerStatus.ComingSoon;
    }
}
