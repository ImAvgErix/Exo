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

    [ObservableProperty] private string _headerStatus = "Checking...";
    [ObservableProperty] private string _headerDetail = string.Empty;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isBusy;
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
            var snapshot = await _services.NvidiaPanel.ProbePolicyAsync();
            Rows.Clear();
            foreach (var item in snapshot)
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
            HeaderDetail = string.Empty;
            CanApplyAll = fixable > 0;
            ApplyAllLabel = fixable > 0 ? "Apply all" : "All applied";
            if (missing == 0)
                HasMessage = false;
        }
        catch (Exception ex)
        {
            HeaderStatus = "Status unavailable";
            HeaderDetail = ex.Message;
            CanApplyAll = true;
            ApplyAllLabel = "Apply all";
            SetMessage($"Could not probe driver: {ex.Message}", success: false);
        }
        finally
        {
            IsLoading = false;
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
        // Same path — OptiHub policy is one atomic NVAPI/registry apply
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

    private async Task RunApplyAsync()
    {
        // Never re-run when every panel-applicable row is already applied.
        if (!IsLoading && Rows.Count > 0 &&
            Rows.Where(r => r.CanApplyFromPanel).All(r => r.IsApplied))
        {
            CanApplyAll = false;
            ApplyAllLabel = "All applied";
            return;
        }

        IsBusy = true;
        ProgressPercent = 8;
        ProgressStatus = "Applying...";
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
            SetMessage(ok ? "Applied." : message, ok);
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
        MessageGlyph = success ? "\uE73E" : "\uE783";
        MessageBrush = success
            ? ResolveBrush("OptiSuccessBrush", Color.FromArgb(255, 34, 197, 94))
            : ResolveBrush("OptiErrorBrush", Color.FromArgb(255, 220, 38, 38));
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
