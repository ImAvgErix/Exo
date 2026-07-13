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
            Card("discord", "Discord", "Assets/Logos/discord.png", OptimizerStatus.Available),
            Card("steam", "Steam", "Assets/Logos/steam.png", OptimizerStatus.Available),
            Card("internet", "Internet", "Assets/Logos/internet.png", OptimizerStatus.Available),
            Card("nvidia", "NVIDIA", "Assets/Logos/nvidia.png", OptimizerStatus.Available),
            Card("amd", "AMD", "Assets/Logos/amd.png", OptimizerStatus.ComingSoon),
            Card("brave", "Brave", "Assets/Logos/brave.png", OptimizerStatus.ComingSoon),
            Card("riot", "Riot", "Assets/Logos/riot.png", OptimizerStatus.ComingSoon),
            Card("epic", "Epic", "Assets/Logos/epic.png", OptimizerStatus.ComingSoon),
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

    /// <summary>Fast status chips for available modules (plain labels only).</summary>
    public async Task RefreshStatesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        foreach (var card in Cards)
        {
            if (card.IsComingSoon)
            {
                card.SetStatus("Coming soon", loading: false);
                continue;
            }

            card.SetStatus("Checking…", loading: true);
        }

        var tasks = Cards
            .Where(c => !c.IsComingSoon)
            .Select(c => RefreshOneAsync(c, ct))
            .ToArray();

        if (tasks.Length > 0)
            await Task.WhenAll(tasks).ConfigureAwait(true);
    }

    private async Task RefreshOneAsync(OptimizerCardViewModel card, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            // Same detect path as the module pages (not fast-only heuristic).
            // Fast-only was falsely showing "Not applied" while Discord/Steam pages said applied.
            var label = card.Definition.Id switch
            {
                "discord" => await DetectOptimizerChipAsync(
                    () => _services.OptimizerState.DetectDiscordAsync(ct, fastOnly: false), ct),
                "steam" => await DetectOptimizerChipAsync(
                    () => _services.OptimizerState.DetectSteamAsync(ct, fastOnly: false), ct),
                "nvidia" => await DetectOptimizerChipAsync(
                    () => _services.OptimizerState.DetectNvidiaAsync(ct, fastOnly: false), ct),
                "internet" => await DetectInternetChipAsync(ct),
                _ => "Ready"
            };
            card.SetStatus(label, loading: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // leaving
        }
        catch
        {
            card.SetStatus("Unavailable", loading: false);
        }
    }

    private static async Task<string> DetectOptimizerChipAsync(
        Func<Task<OptimizerStateInfo>> detect,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var state = await detect().ConfigureAwait(false);
        return ToChip(state);
    }

    private async Task<string> DetectInternetChipAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var snap = await _services.Network.ProbeAsync(ct).ConfigureAwait(false);
        if (!snap.ProbeOk)
            return "Unavailable";

        if (snap.ActivePreset is NetworkPreset.LowestLatency)
            return snap.Features.Count > 0 && snap.Features.All(f => f.IsOk)
                ? "Latency set"
                : "Check settings";
        if (snap.ActivePreset is NetworkPreset.HighestThroughput)
            return snap.Features.Count > 0 && snap.Features.All(f => f.IsOk)
                ? "Download set"
                : "Check settings";

        if (snap.Media.EthernetInUse || snap.Media.EthernetUp)
            return "Ready";
        if (snap.Media.WifiUp)
            return "Wi‑Fi";
        return "Ready";
    }

    /// <summary>Map detect output to short, normal labels (same truth as module pages).</summary>
    internal static string ToChip(OptimizerStateInfo state)
    {
        var text = state.StatusText ?? string.Empty;
        if (text.Contains("Not installed", StringComparison.OrdinalIgnoreCase))
            return "Not installed";
        if (text.Contains("Incomplete", StringComparison.OrdinalIgnoreCase))
            return "Incomplete";

        // Prefer IsApplied + "All applied" from detect (module pages use the same).
        if (state.IsApplied ||
            text.Contains("All applied", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Applied", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("optimized", StringComparison.OrdinalIgnoreCase))
            return "Applied";

        // If every feature row is active, treat as applied (detect script sometimes
        // sets status text loosely while features are all green).
        if (state.Features is { Count: > 0 } && state.Features.All(f => f.IsActive))
            return "Applied";

        if (text.Contains("Not applied", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(text))
            return "Not applied";

        return text.Length <= 18 ? text : "Not applied";
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

    [ObservableProperty] private bool _isLoadingState;
    [ObservableProperty] private bool _isComingSoon;
    [ObservableProperty] private string _statusLabel = string.Empty;
    [ObservableProperty] private bool _hasStatus;

    public void InitializePresentation()
    {
        IsComingSoon = Definition.Status == OptimizerStatus.ComingSoon;
        if (IsComingSoon)
            SetStatus("Coming soon", loading: false);
        else
            SetStatus("Checking…", loading: true);
    }

    public void SetStatus(string label, bool loading)
    {
        IsLoadingState = loading;
        StatusLabel = label ?? string.Empty;
        HasStatus = !string.IsNullOrWhiteSpace(StatusLabel);
    }
}
