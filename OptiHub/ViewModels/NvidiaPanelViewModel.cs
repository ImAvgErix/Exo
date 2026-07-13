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
    [ObservableProperty] private string _applyAllLabel = "Apply peak defaults";
    [ObservableProperty] private bool _canApplyAll;

    [RelayCommand]
    public Task RefreshAsync() => RefreshCoreAsync(force: false, soft: false);

    private async Task RefreshCoreAsync(bool force, bool soft)
    {
        if (IsBusy && !force) return;
        // Soft refresh never shows full-page loading (avoids combos vanishing).
        if (!soft)
            IsLoading = true;
        try
        {
            var displayTask = _services.NvidiaPanel.ListDisplaysAsync();
            var policyTask = _services.NvidiaPanel.ProbePolicyAsync();
            await Task.WhenAll(displayTask, policyTask).ConfigureAwait(true);

            var infos = displayTask.Result;
            if (soft && Displays.Count > 0 &&
                Displays.Select(d => d.DisplayId).SequenceEqual(infos.Select(i => i.DisplayId)))
            {
                foreach (var info in infos)
                {
                    var row = Displays.FirstOrDefault(d => d.DisplayId == info.DisplayId);
                    row?.SoftUpdateSummary(info);
                }
            }
            else
            {
                Displays.Clear();
                foreach (var d in infos)
                {
                    var row = new NvidiaDisplayColorRowViewModel
                    {
                        DisplayId = d.DisplayId,
                        Title = d.Title,
                        IsPrimary = d.IsPrimary
                    };
                    row.LoadFrom(d);
                    Displays.Add(row);
                }
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
            HeaderStatus = HasDisplays
                ? $"{Displays.Count} display{(Displays.Count == 1 ? "" : "s")}"
                : (missing == 0 ? "Policy OK" : $"{missing} policy gaps");
            HeaderDetail = "Change a value, then Apply. Peak defaults: GPU + no-scaling + override.";
            CanApplyAll = true;
            ApplyAllLabel = "Apply peak defaults";
        }
        catch (Exception ex)
        {
            HeaderStatus = "Status unavailable";
            HeaderDetail = ex.Message;
            CanApplyAll = true;
            SetMessage($"Could not probe driver: {ex.Message}", success: false);
        }
        finally
        {
            if (!soft)
                IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task ApplyDisplaySettingsAsync(NvidiaDisplayColorRowViewModel? row)
    {
        if (row is null || IsBusy) return;
        IsBusy = true;
        // Do NOT set row.IsApplying — that grayed every ComboBox via IsEnabled binding.
        ProgressPercent = 15;
        ProgressStatus = $"Applying {row.Title}...";
        try
        {
            var messages = new List<string>();
            var allOk = true;
            var any = false;

            // Only apply dirty fields — never re-stamp unchanged mode (was resetting desktop).
            if (row.IsModeDirty() && row.TryGetSelectedMode(out var w, out var h, out var hz))
            {
                any = true;
                ProgressStatus = $"Setting {w}x{h}@{hz}...";
                ProgressPercent = 30;
                var (ok, msg) = await _services.NvidiaPanel.SetModeAsync(w, h, hz, row.DisplayId);
                messages.Add(msg);
                allOk &= ok;
            }

            if (row.IsDepthDirty())
            {
                any = true;
                ProgressStatus = $"Setting {row.SelectedDepth}...";
                ProgressPercent = 50;
                var (ok, msg) = await _services.NvidiaPanel.SetColorDepthAsync(row.SelectedDepth!, row.DisplayId);
                messages.Add(msg);
                allOk &= ok;
            }

            if (row.IsColorRangeDirty())
            {
                any = true;
                ProgressStatus = $"Setting {row.SelectedColorRange}...";
                ProgressPercent = 70;
                var (ok, msg) = await _services.NvidiaPanel.SetColorRangeAsync(row.SelectedColorRange!, row.DisplayId);
                messages.Add(msg);
                allOk &= ok;
            }

            if (row.IsScalingDirty())
            {
                any = true;
                ProgressStatus = $"Setting {row.SelectedScaling}...";
                ProgressPercent = 85;
                var (ok, msg) = await _services.NvidiaPanel.SetScalingAsync(row.SelectedScaling!, row.DisplayId);
                messages.Add(msg);
                allOk &= ok;
            }

            ProgressPercent = 100;
            if (!any)
                SetMessage("Nothing changed — pick a different value, then Apply.", success: true);
            else
                SetMessage(string.Join(" ", messages.Where(m => !string.IsNullOrWhiteSpace(m))), allOk);

            // Soft reload after busy cleared so controls never stay disabled/gray.
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
            row.IsApplying = false;
        }

        try
        {
            await RefreshCoreAsync(force: true, soft: true);
        }
        catch
        {
            // refresh best-effort
        }
    }

    [RelayCommand]
    public Task ApplyColorDepthAsync(NvidiaDisplayColorRowViewModel? row) =>
        ApplyDisplaySettingsAsync(row);

    [RelayCommand]
    public async Task ApplyAllAsync()
    {
        if (IsBusy) return;
        await RunPeakApplyAsync();
    }

    [RelayCommand]
    public async Task ApplyRowAsync(string? id)
    {
        if (IsBusy) return;
        await RunPeakApplyAsync();
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
            await RefreshCoreAsync(force: true, soft: true);
        }
        finally
        {
            IsBusy = false;
            ProgressPercent = 0;
            ProgressStatus = string.Empty;
        }
    }

    private async Task RunPeakApplyAsync()
    {
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
            SetMessage(ok
                ? "Peak defaults applied (Full RGB, GPU + no-scaling + override)."
                : message, ok);
            // Full rebuild after peak (modes may change).
            await RefreshCoreAsync(force: true, soft: false);
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
