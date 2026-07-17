using System.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Exo.Views.Controls;

public sealed partial class SharedModulePlate : UserControl
{
    public SharedModulePlate()
    {
        InitializeComponent();
    }

    /// <summary>Feature tile grid for list-enter motion.</summary>
    public FeatureTileGrid FeatureTileGrid => FeatureGrid;

    public event RoutedEventHandler? ToggleReportClick;
    public event RoutedEventHandler? SecondaryLeftClick;

    private void ToggleReport_Click(object sender, RoutedEventArgs e) =>
        ToggleReportClick?.Invoke(sender, e);

    private void SecondaryLeft_Click(object sender, RoutedEventArgs e) =>
        SecondaryLeftClick?.Invoke(sender, e);

    public static readonly DependencyProperty ModuleTitleProperty =
        DependencyProperty.Register(nameof(ModuleTitle), typeof(string), typeof(SharedModulePlate), new PropertyMetadata("MODULE"));
    public string ModuleTitle { get => (string)GetValue(ModuleTitleProperty); set => SetValue(ModuleTitleProperty, value); }

    public static readonly DependencyProperty StatusTextProperty =
        DependencyProperty.Register(nameof(StatusText), typeof(string), typeof(SharedModulePlate), new PropertyMetadata(string.Empty));
    public string StatusText { get => (string)GetValue(StatusTextProperty); set => SetValue(StatusTextProperty, value); }

    public static readonly DependencyProperty GuidanceTextProperty =
        DependencyProperty.Register(nameof(GuidanceText), typeof(string), typeof(SharedModulePlate), new PropertyMetadata(string.Empty));
    public string GuidanceText { get => (string)GetValue(GuidanceTextProperty); set => SetValue(GuidanceTextProperty, value); }

    public static readonly DependencyProperty HasGuidanceProperty =
        DependencyProperty.Register(nameof(HasGuidance), typeof(bool), typeof(SharedModulePlate), new PropertyMetadata(false));
    public bool HasGuidance { get => (bool)GetValue(HasGuidanceProperty); set => SetValue(HasGuidanceProperty, value); }

    public static readonly DependencyProperty HeaderExtraProperty =
        DependencyProperty.Register(nameof(HeaderExtra), typeof(UIElement), typeof(SharedModulePlate), new PropertyMetadata(null));
    public UIElement? HeaderExtra { get => (UIElement?)GetValue(HeaderExtraProperty); set => SetValue(HeaderExtraProperty, value); }

    public static readonly DependencyProperty FootExtraProperty =
        DependencyProperty.Register(nameof(FootExtra), typeof(UIElement), typeof(SharedModulePlate), new PropertyMetadata(null));
    public UIElement? FootExtra { get => (UIElement?)GetValue(FootExtraProperty); set => SetValue(FootExtraProperty, value); }

    public static readonly DependencyProperty PrimaryActionsProperty =
        DependencyProperty.Register(nameof(PrimaryActions), typeof(UIElement), typeof(SharedModulePlate), new PropertyMetadata(null));
    public UIElement? PrimaryActions { get => (UIElement?)GetValue(PrimaryActionsProperty); set => SetValue(PrimaryActionsProperty, value); }

    public static readonly DependencyProperty FeatureItemsProperty =
        DependencyProperty.Register(nameof(FeatureItems), typeof(IEnumerable), typeof(SharedModulePlate), new PropertyMetadata(null));
    public IEnumerable? FeatureItems { get => (IEnumerable?)GetValue(FeatureItemsProperty); set => SetValue(FeatureItemsProperty, value); }

    public static readonly DependencyProperty IsLoadingProperty =
        DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(SharedModulePlate), new PropertyMetadata(false));
    public bool IsLoading { get => (bool)GetValue(IsLoadingProperty); set => SetValue(IsLoadingProperty, value); }

    public static readonly DependencyProperty IsFeatureListVisibleProperty =
        DependencyProperty.Register(nameof(IsFeatureListVisible), typeof(bool), typeof(SharedModulePlate), new PropertyMetadata(false));
    public bool IsFeatureListVisible { get => (bool)GetValue(IsFeatureListVisibleProperty); set => SetValue(IsFeatureListVisibleProperty, value); }

    public static readonly DependencyProperty HasLastResultProperty =
        DependencyProperty.Register(nameof(HasLastResult), typeof(bool), typeof(SharedModulePlate), new PropertyMetadata(false));
    public bool HasLastResult { get => (bool)GetValue(HasLastResultProperty); set => SetValue(HasLastResultProperty, value); }

    public static readonly DependencyProperty LastResultProperty =
        DependencyProperty.Register(nameof(LastResult), typeof(string), typeof(SharedModulePlate), new PropertyMetadata(string.Empty));
    public string LastResult { get => (string)GetValue(LastResultProperty); set => SetValue(LastResultProperty, value); }

    public static readonly DependencyProperty LastResultGlyphProperty =
        DependencyProperty.Register(nameof(LastResultGlyph), typeof(string), typeof(SharedModulePlate), new PropertyMetadata("\uE73E"));
    public string LastResultGlyph { get => (string)GetValue(LastResultGlyphProperty); set => SetValue(LastResultGlyphProperty, value); }

    public static readonly DependencyProperty LastResultBrushProperty =
        DependencyProperty.Register(nameof(LastResultBrush), typeof(Brush), typeof(SharedModulePlate), new PropertyMetadata(null));
    public Brush? LastResultBrush { get => (Brush?)GetValue(LastResultBrushProperty); set => SetValue(LastResultBrushProperty, value); }

    public static readonly DependencyProperty IsProgressVisibleProperty =
        DependencyProperty.Register(nameof(IsProgressVisible), typeof(bool), typeof(SharedModulePlate), new PropertyMetadata(false));
    public bool IsProgressVisible { get => (bool)GetValue(IsProgressVisibleProperty); set => SetValue(IsProgressVisibleProperty, value); }

    public static readonly DependencyProperty ProgressPercentProperty =
        DependencyProperty.Register(nameof(ProgressPercent), typeof(double), typeof(SharedModulePlate), new PropertyMetadata(0d));
    public double ProgressPercent { get => (double)GetValue(ProgressPercentProperty); set => SetValue(ProgressPercentProperty, value); }

    public static readonly DependencyProperty ProgressStatusProperty =
        DependencyProperty.Register(nameof(ProgressStatus), typeof(string), typeof(SharedModulePlate), new PropertyMetadata(string.Empty));
    public string ProgressStatus { get => (string)GetValue(ProgressStatusProperty); set => SetValue(ProgressStatusProperty, value); }

    public static readonly DependencyProperty HasApplyReportProperty =
        DependencyProperty.Register(nameof(HasApplyReport), typeof(bool), typeof(SharedModulePlate), new PropertyMetadata(false));
    public bool HasApplyReport { get => (bool)GetValue(HasApplyReportProperty); set => SetValue(HasApplyReportProperty, value); }

    public static readonly DependencyProperty IsApplyReportOpenProperty =
        DependencyProperty.Register(nameof(IsApplyReportOpen), typeof(bool), typeof(SharedModulePlate), new PropertyMetadata(false));
    public bool IsApplyReportOpen { get => (bool)GetValue(IsApplyReportOpenProperty); set => SetValue(IsApplyReportOpenProperty, value); }

    public static readonly DependencyProperty ApplyReportSummaryProperty =
        DependencyProperty.Register(nameof(ApplyReportSummary), typeof(string), typeof(SharedModulePlate), new PropertyMetadata("Last apply"));
    public string ApplyReportSummary { get => (string)GetValue(ApplyReportSummaryProperty); set => SetValue(ApplyReportSummaryProperty, value); }

    public static readonly DependencyProperty ApplyReportChevronProperty =
        DependencyProperty.Register(nameof(ApplyReportChevron), typeof(string), typeof(SharedModulePlate), new PropertyMetadata("\uE70D"));
    public string ApplyReportChevron { get => (string)GetValue(ApplyReportChevronProperty); set => SetValue(ApplyReportChevronProperty, value); }

    public static readonly DependencyProperty ApplyReportRowsProperty =
        DependencyProperty.Register(nameof(ApplyReportRows), typeof(IEnumerable), typeof(SharedModulePlate), new PropertyMetadata(null));
    public IEnumerable? ApplyReportRows { get => (IEnumerable?)GetValue(ApplyReportRowsProperty); set => SetValue(ApplyReportRowsProperty, value); }

    public static readonly DependencyProperty ShowSecondaryActionsProperty =
        DependencyProperty.Register(nameof(ShowSecondaryActions), typeof(bool), typeof(SharedModulePlate), new PropertyMetadata(true));
    public bool ShowSecondaryActions { get => (bool)GetValue(ShowSecondaryActionsProperty); set => SetValue(ShowSecondaryActionsProperty, value); }

    public static readonly DependencyProperty SecondaryLeftLabelProperty =
        DependencyProperty.Register(nameof(SecondaryLeftLabel), typeof(string), typeof(SharedModulePlate), new PropertyMetadata("Repair"));
    public string SecondaryLeftLabel { get => (string)GetValue(SecondaryLeftLabelProperty); set => SetValue(SecondaryLeftLabelProperty, value); }

    public static readonly DependencyProperty SecondaryActionsEnabledProperty =
        DependencyProperty.Register(nameof(SecondaryActionsEnabled), typeof(bool), typeof(SharedModulePlate), new PropertyMetadata(true));
    public bool SecondaryActionsEnabled { get => (bool)GetValue(SecondaryActionsEnabledProperty); set => SetValue(SecondaryActionsEnabledProperty, value); }
}
