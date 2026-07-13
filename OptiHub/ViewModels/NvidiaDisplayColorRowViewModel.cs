using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OptiHub.Services;

namespace OptiHub.ViewModels;

/// <summary>One NVIDIA display with Control Panel–style pickers. Stable combo items (no clear on select).</summary>
public partial class NvidiaDisplayColorRowViewModel : ObservableObject
{
    public uint DisplayId { get; init; }
    public string Title { get; init; } = "";
    public bool IsPrimary { get; init; }

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

    // Snapshot of values loaded from driver — only Apply diffs.
    public string LoadedResolution { get; private set; } = "";
    public int LoadedHz { get; private set; }
    public string LoadedDepth { get; private set; } = "";
    public string LoadedColorRange { get; private set; } = "";
    public string LoadedScaling { get; private set; } = "";

    private bool _suppress;

    public void LoadFrom(NvidiaDisplayInfo info)
    {
        _suppress = true;
        try
        {
            AllModes.Clear();
            AllModes.AddRange(info.Modes);

            ReplaceOptions(ResolutionOptions, NvidiaPanelLogic.DistinctResolutions(info.Modes));
            if (ResolutionOptions.Count == 0 && info.CurrentWidth > 0)
                ResolutionOptions.Add($"{info.CurrentWidth}x{info.CurrentHeight}");

            ReplaceOptions(DepthOptions, info.SupportedDepths.Distinct(StringComparer.OrdinalIgnoreCase));
            if (DepthOptions.Count == 0)
                DepthOptions.Add("8-bit");

            ReplaceOptions(ColorRangeOptions, NvidiaPanelLogic.ColorRangeOptions);
            ReplaceOptions(ScalingOptions, NvidiaPanelLogic.ScalingOptions);

            LoadedResolution = $"{info.CurrentWidth}x{info.CurrentHeight}";
            LoadedHz = info.CurrentHz;
            LoadedDepth = string.IsNullOrWhiteSpace(info.CurrentDepth) ? "8-bit" : info.CurrentDepth;
            LoadedColorRange = string.IsNullOrWhiteSpace(info.CurrentRange) ? "Full RGB" : info.CurrentRange;
            LoadedScaling = string.IsNullOrWhiteSpace(info.Scaling) ? "GPU no-scaling" : info.Scaling;

            SelectedResolution = MatchOption(ResolutionOptions, LoadedResolution)
                                 ?? ResolutionOptions.FirstOrDefault();
            RebuildRefreshOptions(selectHz: LoadedHz, force: true);
            SelectedDepth = MatchOption(DepthOptions, LoadedDepth) ?? DepthOptions.FirstOrDefault();
            SelectedColorRange = MatchOption(ColorRangeOptions, LoadedColorRange)
                                 ?? ColorRangeOptions.FirstOrDefault();
            SelectedScaling = MatchOption(ScalingOptions, LoadedScaling)
                              ?? ScalingOptions.FirstOrDefault();

            CurrentSummary = string.IsNullOrWhiteSpace(info.CurrentMode)
                ? $"{LoadedDepth} · {LoadedColorRange} · {LoadedScaling}"
                : $"{info.CurrentMode} · {LoadedDepth} · {LoadedColorRange} · {LoadedScaling}";
        }
        finally
        {
            _suppress = false;
            UpdateCanApply();
        }
    }

    /// <summary>Update summary from driver without rebuilding combo lists (keeps selection stable).</summary>
    public void SoftUpdateSummary(NvidiaDisplayInfo info)
    {
        CurrentSummary = string.IsNullOrWhiteSpace(info.CurrentMode)
            ? $"{info.CurrentDepth} · {info.CurrentRange} · {info.Scaling}"
            : $"{info.CurrentMode} · {info.CurrentDepth} · {info.CurrentRange} · {info.Scaling}";
        LoadedResolution = $"{info.CurrentWidth}x{info.CurrentHeight}";
        LoadedHz = info.CurrentHz;
        LoadedDepth = info.CurrentDepth;
        LoadedColorRange = info.CurrentRange;
        LoadedScaling = info.Scaling;
        UpdateCanApply();
    }

    partial void OnSelectedResolutionChanged(string? value)
    {
        if (_suppress) return;
        // Keep current Hz if still valid for the new res; never blank the refresh box.
        RebuildRefreshOptions(selectHz: NvidiaPanelLogic.ParseHzLabel(SelectedRefresh), force: false);
        UpdateCanApply();
    }

    partial void OnSelectedRefreshChanged(string? value)
    {
        if (_suppress) return;
        UpdateCanApply();
    }

    partial void OnSelectedDepthChanged(string? value)
    {
        if (_suppress) return;
        UpdateCanApply();
    }

    partial void OnSelectedColorRangeChanged(string? value)
    {
        if (_suppress) return;
        UpdateCanApply();
    }

    partial void OnSelectedScalingChanged(string? value)
    {
        if (_suppress) return;
        UpdateCanApply();
    }

    private void RebuildRefreshOptions(int selectHz, bool force)
    {
        var rates = string.IsNullOrWhiteSpace(SelectedResolution)
            ? Array.Empty<string>()
            : NvidiaPanelLogic.RefreshRatesForResolution(AllModes, SelectedResolution);
        if (rates.Count == 0)
            rates = new[] { selectHz > 0 ? $"{selectHz} Hz" : "60 Hz" };

        // Avoid Clear() flash — only replace if the set changed.
        if (!force && OptionsEqual(RefreshOptions, rates))
        {
            if (selectHz > 0)
            {
                var match = RefreshOptions.FirstOrDefault(r => NvidiaPanelLogic.ParseHzLabel(r) == selectHz);
                if (match is not null && !string.Equals(SelectedRefresh, match, StringComparison.Ordinal))
                    SelectedRefresh = match;
            }
            return;
        }

        var prev = SelectedRefresh;
        ReplaceOptions(RefreshOptions, rates);
        SelectedRefresh = RefreshOptions.FirstOrDefault(r => NvidiaPanelLogic.ParseHzLabel(r) == selectHz)
                          ?? MatchOption(RefreshOptions, prev)
                          ?? RefreshOptions.FirstOrDefault();
    }

    private void UpdateCanApply()
    {
        if (IsApplying)
        {
            CanApply = false;
            return;
        }

        var dirty = IsModeDirty() ||
                    !string.Equals(SelectedDepth, LoadedDepth, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(SelectedColorRange, LoadedColorRange, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(SelectedScaling, LoadedScaling, StringComparison.OrdinalIgnoreCase);
        CanApply = dirty;
    }

    public bool IsModeDirty()
    {
        if (string.IsNullOrWhiteSpace(SelectedResolution)) return false;
        var hz = NvidiaPanelLogic.ParseHzLabel(SelectedRefresh);
        if (hz <= 0) hz = LoadedHz;
        return !string.Equals(SelectedResolution, LoadedResolution, StringComparison.OrdinalIgnoreCase) ||
               hz != LoadedHz;
    }

    public bool IsDepthDirty() =>
        !string.IsNullOrWhiteSpace(SelectedDepth) &&
        !string.Equals(SelectedDepth, LoadedDepth, StringComparison.OrdinalIgnoreCase);

    public bool IsColorRangeDirty() =>
        !string.IsNullOrWhiteSpace(SelectedColorRange) &&
        !string.Equals(SelectedColorRange, LoadedColorRange, StringComparison.OrdinalIgnoreCase);

    public bool IsScalingDirty() =>
        !string.IsNullOrWhiteSpace(SelectedScaling) &&
        !string.Equals(SelectedScaling, LoadedScaling, StringComparison.OrdinalIgnoreCase);

    public bool TryGetSelectedMode(out int w, out int h, out int hz)
    {
        w = h = hz = 0;
        if (string.IsNullOrWhiteSpace(SelectedResolution)) return false;
        var parts = SelectedResolution.Split('x', 'X');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0].Trim(), out w) || !int.TryParse(parts[1].Trim(), out h)) return false;
        hz = NvidiaPanelLogic.ParseHzLabel(SelectedRefresh);
        if (hz <= 0) hz = LoadedHz > 0 ? LoadedHz : 60;
        return w >= 640 && h >= 480;
    }

    private static void ReplaceOptions(ObservableCollection<string> target, IEnumerable<string> source)
    {
        var list = source.ToList();
        // In-place sync avoids ComboBox blanking from Clear() under open dropdown.
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!list.Contains(target[i], StringComparer.OrdinalIgnoreCase))
                target.RemoveAt(i);
        }
        foreach (var item in list)
        {
            if (!target.Any(t => string.Equals(t, item, StringComparison.OrdinalIgnoreCase)))
                target.Add(item);
        }
        // Reorder to match source
        for (var i = 0; i < list.Count && i < target.Count; i++)
        {
            var want = list[i];
            var idx = -1;
            for (var j = i; j < target.Count; j++)
            {
                if (string.Equals(target[j], want, StringComparison.OrdinalIgnoreCase))
                {
                    idx = j;
                    break;
                }
            }
            if (idx >= 0 && idx != i)
                target.Move(idx, i);
            else if (idx < 0)
                target.Insert(i, want);
            else if (!string.Equals(target[i], want, StringComparison.Ordinal))
                target[i] = want;
        }
        while (target.Count > list.Count)
            target.RemoveAt(target.Count - 1);
    }

    private static bool OptionsEqual(ObservableCollection<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string? MatchOption(ObservableCollection<string> options, string? want)
    {
        if (string.IsNullOrWhiteSpace(want)) return null;
        return options.FirstOrDefault(o => string.Equals(o, want, StringComparison.OrdinalIgnoreCase));
    }
}
