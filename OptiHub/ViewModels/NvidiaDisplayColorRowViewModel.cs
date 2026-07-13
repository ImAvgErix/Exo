using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OptiHub.ViewModels;

/// <summary>One NVIDIA display with selectable color bit depth for the Panel page.</summary>
public partial class NvidiaDisplayColorRowViewModel : ObservableObject
{
    public uint DisplayId { get; init; }
    public string Title { get; init; } = "";
    public ObservableCollection<string> DepthOptions { get; } = new();

    [ObservableProperty] private string _currentDepth = "—";
    [ObservableProperty] private string? _selectedDepth;
    [ObservableProperty] private bool _canApply;
    [ObservableProperty] private bool _isApplying;

    partial void OnSelectedDepthChanged(string? value)
    {
        CanApply = !string.IsNullOrWhiteSpace(value) &&
                   !string.Equals(value, CurrentDepth, StringComparison.OrdinalIgnoreCase) &&
                   !IsApplying;
    }

    public void MarkApplied(string depth)
    {
        CurrentDepth = depth;
        SelectedDepth = depth;
        CanApply = false;
    }
}
