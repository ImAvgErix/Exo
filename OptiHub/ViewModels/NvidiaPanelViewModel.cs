using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using OptiHub.Models;
using OptiHub.Services;
using Windows.UI;

namespace OptiHub.ViewModels;

public partial class NvidiaPanelViewModel : ObservableObject
{
    private readonly AppServices _services;

    public NvidiaPanelViewModel(AppServices services)
    {
        _services = services;
        MessageBrush = ResolveBrush("OptiSuccessBrush", Color.FromArgb(255, 34, 197, 94));
    }

    public ObservableCollection<NvidiaPolicyRowViewModel> Rows { get; } = new();
    public ObservableCollection<NvidiaDisplayColorRowViewModel> Displays { get; } = new();

    [ObservableProperty] private string _headerStatus = "Checking...";
    [ObservableProperty] private string _headerDetail = string.Empty;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasDisplays;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressStatus = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private bool _hasMessage;
    [ObservableProperty] private string _messageGlyph = "\uE73E";
    [ObservableProperty] private Brush _messageBrush;
    [ObservableProperty] private string _applyAllLabel = "Apply all";
    [ObservableProperty] private bool _canApplyAll;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsLoading = true;
        try
        {
            var colorTask = _services.NvidiaPanel.ListColorDepthsAsync();
            var policyTask = _services.NvidiaPanel.ProbePolicyAsync();
            await Task.WhenAll(colorTask, policyTask).ConfigureAwait(true);

            Displays.Clear();
            foreach (var d in colorTask.Result)
            {
                var row = new NvidiaDisplayColorRowViewModel
                {
                    DisplayId = d.DisplayId,
                    Title = d.Title
                };
                foreach (var opt in d.SupportedDepths.Distinct(StringComparer.OrdinalIgnoreCase))
                    row.DepthOptions.Add(opt);
                if (row.DepthOptions.Count == 0)
                {
                    row.DepthOptions.Add("8-bit");
                    row.DepthOptions.Add("10-bit");
                    row.DepthOptions.Add("12-bit");
                }
                row.CurrentDepth = string.IsNullOrWhiteSpace(d.CurrentDepth) ? "—" : d.CurrentDepth;
                row.SelectedDepth = row.DepthOptions.FirstOrDefault(o =>
                    string.Equals(o, row.CurrentDepth, StringComparison.OrdinalIgnoreCase))
                    ?? row.DepthOptions.FirstOrDefault();
                Displays.Add(row);
            }
            HasDisplays = Displays.Count > 0;

            Rows.Clear();
            foreach (var item in policyTask.Result)
            {
                var row = new NvidiaPolicyRowViewModel
                {
                    Id = item.Id,
                    Title = item.Title,
                    CanApplyFromPanel = item.CanApplyFromPanel
                };
                row.SetResult(item.IsApplied, item.Detail);
                Rows.Add(row);
            }

            var missing = Rows.Count(r => !r.IsApplied);
            var fixable = Rows.Count(r => !r.IsApplied && r.CanApplyFromPanel);
            HeaderStatus = missing == 0 ? "All applied" : $"{missing} not applied";
            HeaderDetail = HasDisplays
                ? $"{Displays.Count} display(s) — pick color depth below, or Apply all for peak defaults"
                : string.Empty;
            CanApplyAll = fixable > 0;
            ApplyAllLabel = fixable > 0 ? "Apply all peak defaults" : "All applied";
            if (missing == 0)
                HasMessage = false;
        }
        catch (Exception ex)
        {
            HeaderStatus = "Status unavailable";
            HeaderDetail = ex.Message;
            CanApplyAll = true;
            ApplyAllLabel = "Apply all peak defaults";
            SetMessage($"Could not probe driver: {ex.Message}", success: false);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task ApplyColorDepthAsync(NvidiaDisplayColorRowViewModel? row)
    {
        if (row is null || IsBusy || string.IsNullOrWhiteSpace(row.SelectedDepth)) return;
        IsBusy = true;
        row.IsApplying = true;
        ProgressPercent = 30;
        ProgressStatus = $"Setting {row.SelectedDepth} on {row.Title}...";
        try
        {
            var (ok, msg) = await _services.NvidiaPanel.SetColorDepthAsync(
                row.SelectedDepth!, row.DisplayId);
            if (ok)
                row.MarkApplied(row.SelectedDepth!);
            SetMessage(msg, ok);
            // Refresh list to confirm live depth
            await RefreshDisplaysOnlyAsync();
        }
        catch (Exception ex)
        {
            SetMessage($"Color depth failed: {ex.Message}", success: false);
        }
        finally
        {
            row.IsApplying = false;
            IsBusy = false;
            ProgressPercent = 0;
            ProgressStatus = string.Empty;
        }
    }

    [RelayCommand]
    public async Task ApplyAllAsync()
    {
        if (IsBusy) return;
        await RunApplyAsync();
    }

    [RelayCommand]
    public async Task ApplyRowAsync(string? id)
    {
        if (IsBusy) return;
        await RunApplyAsync();
    }

    [RelayCommand]
    public async Task ClearTrayAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ProgressPercent = 20;
        ProgressStatus = "Clearing NVIDIA tray icons...";
        try
        {
            var (ok, msg) = await _services.NvidiaPanel.ClearTrayIconsAsync();
            SetMessage(msg, ok);
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
            ProgressPercent = 0;
            ProgressStatus = string.Empty;
        }
    }

    private async Task RefreshDisplaysOnlyAsync()
    {
        try
        {
            var list = await _services.NvidiaPanel.ListColorDepthsAsync();
            foreach (var d in list)
            {
                var row = Displays.FirstOrDefault(x => x.DisplayId == d.DisplayId);
                if (row is null) continue;
                var depth = string.IsNullOrWhiteSpace(d.CurrentDepth) ? "—" : d.CurrentDepth;
                row.CurrentDepth = depth;
                if (row.DepthOptions.Any(o =>
                        string.Equals(o, depth, StringComparison.OrdinalIgnoreCase)))
                    row.SelectedDepth = depth;
            }
        }
        catch { }
    }

    private async Task RunApplyAsync()
    {
        if (!IsLoading && Rows.Count > 0 &&
            Rows.Where(r => r.CanApplyFromPanel).All(r => r.IsApplied))
        {
            CanApplyAll = false;
            ApplyAllLabel = "All applied";
            return;
        }

        IsBusy = true;
        ProgressPercent = 8;
        ProgressStatus = "Applying peak display policy...";
        try
        {
            var settings = NvidiaPanelSettings.CreateDefaults();
            settings.PrimaryRefresh = "max";
            settings.SecondaryRefresh = "60";
            settings.FullRgb = true;
            settings.GpuNoScaling = true;
            settings.ScalingOverride = true;
            settings.VideoNvidiaColor = true;
            settings.VideoNvidiaImage = true;
            settings.DeveloperCounters = true;
            settings.StripAppAndControlPanel = true;

            var progress = new Progress<ScriptRunProgress>(p =>
            {
                if (p.Percent >= ProgressPercent)
                    ProgressPercent = p.Percent;
                if (!string.IsNullOrWhiteSpace(p.Status))
                    ProgressStatus = p.Status;
            });

            var (ok, message) = await _services.NvidiaPanel.ApplyDisplayPolicyAsync(settings, progress);
            ProgressPercent = 100;
            ProgressStatus = ok ? "Done" : "Failed";
            SetMessage(ok ? "Peak defaults applied (Full RGB, max primary Hz, GPU no-scaling)." : message, ok);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            SetMessage($"Apply failed: {ex.Message}", success: false);
        }
        finally
        {
            IsBusy = false;
            ProgressPercent = 0;
            ProgressStatus = string.Empty;
        }
    }

    private void SetMessage(string text, bool success)
    {
        Message = text;
        HasMessage = !string.IsNullOrWhiteSpace(text);
        var banner = Helpers.UiStatusPresentation.BannerForSuccess(success);
        MessageGlyph = banner.Glyph;
        MessageBrush = ResolveBrush(
            banner.BrushKey,
            success ? Color.FromArgb(255, 34, 197, 94) : Color.FromArgb(255, 220, 38, 38));
    }

    private static Brush ResolveBrush(string key, Color fallback)
    {
        try
        {
            if (Microsoft.UI.Xaml.Application.Current?.Resources.TryGetValue(key, out var value) == true
                && value is Brush brush)
                return brush;
        }
        catch { }
        return new SolidColorBrush(fallback);
    }
}
