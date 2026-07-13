using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OptiHub.Services;

namespace OptiHub.ViewModels;

/// <summary>One NVIDIA display with Control Panel–style pickers (res, Hz, depth, color, scaling).</summary>
public partial class NvidiaDisplayColorRowViewModel : ObservableObject
{
    public uint DisplayId { get; init; }
    public string Title { get; init; } = "";
    public bool IsPrimary { get; init; }

    /// <summary>All raw modes from helper (WxH@Hz).</summary>
    public List<string> AllModes { get; } = new();

    public ObservableCollection<string> ResolutionOptions { get; } = new();
    public ObservableCollection<string> RefreshOptions { get; } = new();
    public ObservableCollection<string> DepthOptions { get; } = new();
    public ObservableCollection<string> ColorRangeOptions { get; } = new();
    public ObservableCollection<string> ScalingOptions { get; } = new();

    [ObservableProperty] private string _currentSummary = "—";
    [ObservableProperty] private string? _selectedResolution;
    [ObservableProperty] private string? _selectedRefresh;
    [ObservableProperty] private string? _selectedDepth;
    [ObservableProperty] private string? _selectedColorRange;
    [ObservableProperty] private string? _selectedScaling;
    [ObservableProperty] private bool _canApply;
    [ObservableProperty] private bool _isApplying;

    private bool _suppressRefreshRebuild;

    public void LoadFrom(NvidiaDisplayInfo info)
    {
        AllModes.Clear();
        AllModes.AddRange(info.Modes);
        ResolutionOptions.Clear();
        foreach (var r in NvidiaPanelLogic.DistinctResolutions(info.Modes))
            ResolutionOptions.Add(r);
        if (ResolutionOptions.Count == 0 && info.CurrentWidth > 0)
            ResolutionOptions.Add($"{info.CurrentWidth}x{info.CurrentHeight}");

        DepthOptions.Clear();
        foreach (var d in info.SupportedDepths.Distinct(StringComparer.OrdinalIgnoreCase))
            DepthOptions.Add(d);
        if (DepthOptions.Count == 0)
        {
            DepthOptions.Add("8-bit");
            DepthOptions.Add("10-bit");
            DepthOptions.Add("12-bit");
        }

        ColorRangeOptions.Clear();
        foreach (var o in NvidiaPanelLogic.ColorRangeOptions)
            ColorRangeOptions.Add(o);

        ScalingOptions.Clear();
        foreach (var o in NvidiaPanelLogic.ScalingOptions)
            ScalingOptions.Add(o);

        _suppressRefreshRebuild = true;
        SelectedResolution = ResolutionOptions.FirstOrDefault(r =>
            r.Equals($"{info.CurrentWidth}x{info.CurrentHeight}", StringComparison.OrdinalIgnoreCase))
            ?? ResolutionOptions.FirstOrDefault();
        RebuildRefreshOptions(selectHz: info.CurrentHz);
        SelectedDepth = DepthOptions.FirstOrDefault(d =>
            string.Equals(d, info.CurrentDepth, StringComparison.OrdinalIgnoreCase))
            ?? DepthOptions.FirstOrDefault();
        SelectedColorRange = ColorRangeOptions.FirstOrDefault(c =>
            string.Equals(c, info.CurrentRange, StringComparison.OrdinalIgnoreCase))
            ?? ColorRangeOptions.FirstOrDefault();
        SelectedScaling = ScalingOptions.FirstOrDefault(s =>
            string.Equals(s, info.Scaling, StringComparison.OrdinalIgnoreCase))
            ?? ScalingOptions.FirstOrDefault();
        _suppressRefreshRebuild = false;

        CurrentSummary = string.IsNullOrWhiteSpace(info.CurrentMode)
            ? $"{info.CurrentDepth} · {info.CurrentRange} · {info.Scaling}"
            : $"{info.CurrentMode} · {info.CurrentDepth} · {info.CurrentRange} · {info.Scaling}";
        UpdateCanApply();
    }

    partial void OnSelectedResolutionChanged(string? value)
    {
        if (_suppressRefreshRebuild) return;
        RebuildRefreshOptions(selectHz: NvidiaPanelLogic.ParseHzLabel(SelectedRefresh));
        UpdateCanApply();
    }

    partial void OnSelectedRefreshChanged(string? value) => UpdateCanApply();
    partial void OnSelectedDepthChanged(string? value) => UpdateCanApply();
    partial void OnSelectedColorRangeChanged(string? value) => UpdateCanApply();
    partial void OnSelectedScalingChanged(string? value) => UpdateCanApply();

    private void RebuildRefreshOptions(int selectHz)
    {
        RefreshOptions.Clear();
        if (string.IsNullOrWhiteSpace(SelectedResolution)) return;
        foreach (var r in NvidiaPanelLogic.RefreshRatesForResolution(AllModes, SelectedResolution))
            RefreshOptions.Add(r);
        if (RefreshOptions.Count == 0)
            RefreshOptions.Add(selectHz > 0 ? $"{selectHz} Hz" : "60 Hz");

        SelectedRefresh = RefreshOptions.FirstOrDefault(r => NvidiaPanelLogic.ParseHzLabel(r) == selectHz)
            ?? RefreshOptions.FirstOrDefault();
    }

    private void UpdateCanApply()
    {
        CanApply = !IsApplying && (
            !string.IsNullOrWhiteSpace(SelectedResolution) ||
            !string.IsNullOrWhiteSpace(SelectedDepth) ||
            !string.IsNullOrWhiteSpace(SelectedScaling));
    }

    public bool TryGetSelectedMode(out int w, out int h, out int hz)
    {
        w = h = hz = 0;
        if (string.IsNullOrWhiteSpace(SelectedResolution)) return false;
        var res = SelectedResolution.Contains('@')
            ? SelectedResolution
            : $"{SelectedResolution}@{NvidiaPanelLogic.ParseHzLabel(SelectedRefresh)}";
        if (!NvidiaPanelLogic.TryParseModeLabel(res, out w, out h, out hz))
        {
            var parts = SelectedResolution.Split('x', 'X');
            if (parts.Length != 2) return false;
            if (!int.TryParse(parts[0], out w) || !int.TryParse(parts[1], out h)) return false;
            hz = NvidiaPanelLogic.ParseHzLabel(SelectedRefresh);
            if (hz <= 0) hz = 60;
        }
        if (hz <= 0) hz = NvidiaPanelLogic.ParseHzLabel(SelectedRefresh);
        if (hz <= 0) hz = 60;
        return w >= 640 && h >= 480;
    }
}
