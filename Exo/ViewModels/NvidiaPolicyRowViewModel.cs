using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace Exo.ViewModels;

/// <summary>One Exo NVIDIA policy line: Applied / Not applied + Apply.</summary>
public partial class NvidiaPolicyRowViewModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    /// <summary>If false, row shows Not applied but Apply is disabled (needs full profile pass).</summary>
    public bool CanApplyFromPanel { get; init; } = true;

    [ObservableProperty] public partial string Status { get; set; } = "Checking...";
    [ObservableProperty] public partial bool IsApplied { get; set; }
    [ObservableProperty] public partial string Glyph { get; set; } = "\uE895";
    [ObservableProperty] public partial double Opacity { get; set; } = 0.7;

    public Visibility NeedsFix =>
        (!IsApplied && CanApplyFromPanel) ? Visibility.Visible : Visibility.Collapsed;

    public bool CanApply => !IsApplied && CanApplyFromPanel;

    partial void OnIsAppliedChanged(bool value)
    {
        Glyph = Helpers.UiStatusPresentation.FeatureGlyph(value);
        Opacity = Helpers.UiStatusPresentation.FeatureOpacity(value);
        OnPropertyChanged(nameof(NeedsFix));
        OnPropertyChanged(nameof(CanApply));
    }

    public void SetResult(bool applied, string detail)
    {
        IsApplied = applied;
        Status = applied ? "Applied" : "Not applied";
        Glyph = Helpers.UiStatusPresentation.FeatureGlyph(applied);
        Opacity = Helpers.UiStatusPresentation.FeatureOpacity(applied);
        OnPropertyChanged(nameof(NeedsFix));
        OnPropertyChanged(nameof(CanApply));
    }
}
