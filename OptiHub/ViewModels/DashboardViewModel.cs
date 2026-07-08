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
                    Subtitle = "Performance · Privacy · AMOLED",
                    Description = "Equicord, OpenASAR, DiscOpt kernel, cache trim, and calm gaming-focused tweaks.",
                    AccentGlyph = "\uE8BD",
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
                    Description = "Privacy flags, debloat, and smoother browsing.",
                    AccentGlyph = "\uE774",
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
                    Description = "Launch, overlay, and library smoothness for gamers.",
                    AccentGlyph = "\uE7FC",
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
                    Description = "Vanguard-aware housekeeping and client polish.",
                    AccentGlyph = "\uE7FC",
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
                    Description = "Startup, downloads, and background quieting.",
                    AccentGlyph = "\uE7FC",
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
            var state = await _services.OptimizerState.DetectDiscordAsync();
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
    private string _statusBadge = "Available";

    [ObservableProperty]
    private string _statusDetail = string.Empty;

    [ObservableProperty]
    private bool _isComingSoon;

    [ObservableProperty]
    private bool _isApplied;

    public void ApplyState(OptimizerStateInfo state)
    {
        IsComingSoon = Definition.Status == OptimizerStatus.ComingSoon;
        if (IsComingSoon)
        {
            StatusBadge = "Coming soon";
            StatusDetail = Definition.Teaser ?? string.Empty;
            return;
        }

        IsApplied = state.IsApplied;
        StatusBadge = state.IsApplied ? "Applied" : "Ready";
        StatusDetail = state.StatusText;
        Definition.Status = state.IsApplied ? OptimizerStatus.Applied : OptimizerStatus.Available;
    }

    public void InitializePresentation()
    {
        IsComingSoon = Definition.Status == OptimizerStatus.ComingSoon;
        StatusBadge = IsComingSoon ? "Coming soon" : "Ready";
        StatusDetail = IsComingSoon
            ? (Definition.Teaser ?? Definition.Description)
            : Definition.Description;
    }
}
