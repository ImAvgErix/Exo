using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Exo.Services;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Exo.ViewModels;

public partial class NvidiaPanelViewModel : ObservableObject
{
    private readonly AppServices _services;

    public NvidiaPanelViewModel(AppServices services)
    {
        _services = services;
        MessageBrush = ResolveBrush("ExoSuccessBrush", Color.FromArgb(255, 34, 197, 94));
    }

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

    [RelayCommand]
    public Task RefreshAsync() => RefreshCoreAsync(force: false, soft: false);

    [ObservableProperty] private bool _hasControlPanel;

    [RelayCommand]
    public void OpenControlPanel()
    {
        if (_services.NvidiaPanel.TryLaunchControlPanel(out var error))
            SetMessage("Opened NVIDIA Control Panel.", success: true);
        else
            SetMessage(error ?? "Could not open NVIDIA Control Panel.", success: false);
    }

    private async Task RefreshCoreAsync(bool force, bool soft, bool commitSelections = false)
    {
        if (IsBusy && !force) return;
        // Soft refresh never shows full-page loading (avoids combos vanishing).
        if (!soft)
            IsLoading = true;
        try
        {
            var infosTask = _services.NvidiaPanel.ListDisplaysAsync();
            var vibranceTask = _services.NvidiaPanel.ListVibranceAsync();
            var infos = await infosTask.ConfigureAwait(true);
            var vibrance = await vibranceTask.ConfigureAwait(true);
            if (soft && Displays.Count > 0 &&
                Displays.Select(d => d.DisplayId).SequenceEqual(infos.Select(i => i.DisplayId)))
            {
                foreach (var info in infos)
                {
                    var row = Displays.FirstOrDefault(d => d.DisplayId == info.DisplayId);
                    row?.SoftUpdateSummary(info, commitSelections);
                    var v = vibrance.FirstOrDefault(x => x.DisplayId == info.DisplayId);
                    if (row is not null && v is not null)
                        row.SoftUpdateVibrance(v, commitSelections);
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
                    var v = vibrance.FirstOrDefault(x => x.DisplayId == d.DisplayId);
                    if (v is not null)
                        row.LoadVibrance(v);
                    Displays.Add(row);
                }
            }
            HasDisplays = Displays.Count > 0;
            HasControlPanel = _services.NvidiaPanel.IsControlPanelInstalled();

            HeaderStatus = HasDisplays
                ? $"{Displays.Count} display{(Displays.Count == 1 ? "" : "s")}"
                : "No displays found";
            HeaderDetail = HasDisplays
                ? "Change a value, then Apply. Only what you change is written."
                : "Connect a display driven by the NVIDIA GPU.";
        }
        catch (Exception ex)
        {
            HeaderStatus = "Display unavailable";
            HeaderDetail = ex.Message;
            SetMessage($"Could not list displays: {ex.Message}", success: false);
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

        if (!row.CanApply)
        {
            SetMessage(Helpers.OptimizerMessages.Done, success: true);
            return;
        }

        var pending = row.PendingChangeLabels().ToList();
        IsBusy = true;
        // Do NOT set row.IsApplying — that grayed every ComboBox via IsEnabled binding.
        ProgressPercent = 15;
        ProgressStatus = $"Applying {row.Title}…";
        try
        {
            var messages = new List<string>();
            var allOk = true;
            var any = false;

            // Only apply dirty fields — never re-stamp unchanged mode (was resetting desktop).
            if (row.IsModeDirty() && row.TryGetSelectedMode(out var w, out var h, out var hz))
            {
                any = true;
                ProgressStatus = $"Setting {w}×{h} @ {hz} Hz…";
                ProgressPercent = 30;
                var (ok, msg) = await _services.NvidiaPanel.SetModeAsync(w, h, hz, row.DisplayId);
                if (!string.IsNullOrWhiteSpace(msg)) messages.Add(msg);
                allOk &= ok;
            }

            if (row.IsDepthDirty())
            {
                any = true;
                ProgressStatus = $"Setting {row.SelectedDepth}…";
                ProgressPercent = 50;
                var (ok, msg) = await _services.NvidiaPanel.SetColorDepthAsync(row.SelectedDepth!, row.DisplayId);
                if (!string.IsNullOrWhiteSpace(msg)) messages.Add(msg);
                allOk &= ok;
            }

            if (row.IsColorRangeDirty())
            {
                any = true;
                ProgressStatus = $"Setting {row.SelectedColorRange}…";
                ProgressPercent = 70;
                var (ok, msg) = await _services.NvidiaPanel.SetColorRangeAsync(row.SelectedColorRange!, row.DisplayId);
                if (!string.IsNullOrWhiteSpace(msg)) messages.Add(msg);
                allOk &= ok;
            }

            if (row.IsScalingDirty())
            {
                any = true;
                ProgressStatus = $"Setting {row.SelectedScaling}…";
                ProgressPercent = 85;
                var (ok, msg) = await _services.NvidiaPanel.SetScalingAsync(row.SelectedScaling!, row.DisplayId);
                if (!string.IsNullOrWhiteSpace(msg)) messages.Add(msg);
                allOk &= ok;
            }

            if (row.IsVibranceDirty())
            {
                any = true;
                ProgressStatus = $"Setting vibrance {row.SelectedVibranceLevel}…";
                ProgressPercent = 92;
                var (ok, msg) = await _services.NvidiaPanel.SetVibranceAsync(row.SelectedVibranceLevel, row.DisplayId);
                if (!string.IsNullOrWhiteSpace(msg)) messages.Add(msg);
                if (ok)
                    PersistVibrance(row.SelectedVibranceLevel);
                allOk &= ok;
            }

            ProgressPercent = 100;
            if (!any)
            {
                SetMessage(Helpers.OptimizerMessages.Done, success: true);
            }
            else if (allOk)
            {
                var what = pending.Count > 0
                    ? string.Join(", ", pending)
                    : "selected settings";
                SetMessage(Helpers.OptimizerMessages.Done, success: true);
            }
            else
            {
                var detail = string.Join(" ", messages.Where(m => !string.IsNullOrWhiteSpace(m)));
                SetMessage(string.IsNullOrWhiteSpace(detail)
                    ? $"Some settings on {row.Title} did not apply."
                    : $"Some settings on {row.Title} failed. {detail}", success: false);
            }
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
            // After apply, commit pickers to live driver state so bit-depth cannot look "applied"
            // when the panel stayed on 8-bit.
            await RefreshCoreAsync(force: true, soft: true, commitSelections: true);
        }
        catch
        {
            // refresh best-effort
        }
    }

    private void PersistVibrance(int level)
    {
        try
        {
            var settings = _services.NvidiaPanel.Load();
            settings.DigitalVibrance = level;
            _services.NvidiaPanel.Save(settings);
        }
        catch
        {
            // Persistence is best-effort; the driver value is already applied.
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
