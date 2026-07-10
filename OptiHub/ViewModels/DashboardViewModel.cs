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

    public Task RefreshStatesAsync(CancellationToken ct = default) =>
        Task.WhenAll(
            RefreshOneAsync("discord", token => _services.OptimizerState.DetectDiscordAsync(token, fastOnly: true), ct),
            RefreshOneAsync("steam", token => _services.OptimizerState.DetectSteamAsync(token, fastOnly: true), ct),
            RefreshOneAsync("nvidia", token => _services.OptimizerState.DetectNvidiaAsync(token, fastOnly: true), ct));

    private async Task RefreshOneAsync(
        string id,
        Func<CancellationToken, Task<OptimizerStateInfo>> detect,
        CancellationToken ct)
    {
        var card = Cards.FirstOrDefault(c => c.Definition.Id == id);
        if (card is null) return;
        card.IsLoadingState = true;
        try
        {
            var state = await detect(ct);
            card.ApplyState(state);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The dashboard was navigated away from; no status update is needed.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not detect {id} state: {ex}");
        }
        finally
        {
            card.IsLoadingState = false;
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
