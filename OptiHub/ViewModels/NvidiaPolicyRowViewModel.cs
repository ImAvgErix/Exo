using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace OptiHub.ViewModels;

/// <summary>One OptiHub NVIDIA policy line: Applied / Not applied + Fix.</summary>
public partial class NvidiaPolicyRowViewModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    [ObservableProperty] private string _status = "Checking...";
    [ObservableProperty] private bool _isApplied;
    [ObservableProperty] private string _glyph = "\uE895"; // clock / pending
    [ObservableProperty] private double _opacity = 0.7;

    public Visibility NeedsFix => IsApplied ? Visibility.Collapsed : Visibility.Visible;

    partial void OnIsAppliedChanged(bool value)
    {
        Glyph = value ? "\uE73E" : "\uE711";
        Opacity = value ? 1.0 : 0.85;
        if (Status is "Checking..." or "")
            Status = value ? "Applied" : "Not applied";
        OnPropertyChanged(nameof(NeedsFix));
    }

    public void SetResult(bool applied, string detail)
    {
        IsApplied = applied;
        // Short status label: Applied (check) or Not applied
        Status = applied
            ? (string.IsNullOrWhiteSpace(detail) ? "Applied" : $"Applied — {detail}")
            : (string.IsNullOrWhiteSpace(detail) ? "Not applied" : $"Not applied — {detail}");
        Glyph = applied ? "\uE73E" : "\uE711";
        Opacity = applied ? 1.0 : 0.85;
        OnPropertyChanged(nameof(NeedsFix));
    }
}
