using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using OptiHub.Models;
using OptiHub.Services;
using Windows.UI;

namespace OptiHub.ViewModels;

public partial class NvidiaOptimizerViewModel : ObservableObject
{
    private readonly AppServices _services;
    private CancellationTokenSource? _runCts;

    public NvidiaOptimizerViewModel(AppServices services)
    {
        _services = services;
        LastResultBrush = ResolveBrush("OptiSuccessBrush", Color.FromArgb(255, 34, 197, 94));
    }

    [ObservableProperty] private string _title = "NVIDIA";
    public string LogoPath => "Assets/Logos/nvidia.png";

    [ObservableProperty] private string _statusText = "Checking status...";
    [ObservableProperty] private string _detailText = string.Empty;
    public ObservableCollection<FeatureRowViewModel> Features { get; } = new();

    [ObservableProperty] private string _runButtonLabel = "Apply profile";
    [ObservableProperty] private bool _isApplied;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isProgressVisible;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressStatus = string.Empty;
    [ObservableProperty] private string _lastResult = string.Empty;
    [ObservableProperty] private bool _hasLastResult;
    [ObservableProperty] private string _lastResultGlyph = "\uE73E";
    [ObservableProperty] private Brush _lastResultBrush;
    [ObservableProperty] private bool _useGsync;

    public event EventHandler? RequestGoBack;
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    [RelayCommand]
    private void GoBack() => RequestGoBack?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            StatusText = "Checking status...";
            var state = await _services.OptimizerState.DetectNvidiaAsync();
            ApplyState(state);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsBusy) return;

        var action = IsApplied ? "reapply" : "apply";
        var gsyncLine = UseGsync
            ? "• G-SYNC pack (adaptive sync friendly; ultra low latency off)"
            : "• Max FPS / latency pack (Ultra Low Latency Ultra; G-SYNC off)";
        var warning =
            "Apply order (correct stack):\n\n" +
            "1) Driver — check newest Game Ready; if behind, open NVCleanstall (your install/expert checklist)\n" +
            "2) 3D Base Profile — series pack right after driver is current\n" +
            "3) App stack — wipe old App/GFE/CPL leftovers, fresh NVIDIA App, telemetry trim, display prefs\n\n" +
            gsyncLine + "\n\n" +
            "If a driver update is needed, Apply stops after launching NVCleanstall — reboot, then Reapply.\n" +
            "Administrator approval may be required.";

        var ok = ConfirmAsync is not null
            ? await ConfirmAsync($"Confirm NVIDIA Optimizer ({action})", warning)
            : true;
        if (!ok) return;

        IsBusy = true;
        IsProgressVisible = true;
        ProgressPercent = 0;
        ProgressStatus = "Preparing...";
        SetResult(string.Empty, success: true);
        _runCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<ScriptRunProgress>(p =>
            {
                if (p.Percent >= ProgressPercent)
                    ProgressPercent = p.Percent;
                if (!string.IsNullOrWhiteSpace(p.Status))
                    ProgressStatus = p.Status;
            });

            var args = new List<string> { "-NonInteractive" };
            if (UseGsync)
                args.Add("-Gsync");

            var result = await _services.PowerShell.RunAsync(
                _services.Scripts.NvidiaOptimizerScript,
                arguments: args.ToArray(),
                elevate: true,
                progress: progress,
                cancellationToken: _runCts.Token,
                workingDirectory: _services.Scripts.GetNvidiaRoot());

            if (result.Success)
            {
                ProgressPercent = 100;
                ProgressStatus = "Completed successfully";
                SetResult("Done. Profile imported into the NVIDIA driver.", success: true);
            }
            else
            {
                SetResult(result.ErrorMessage ?? result.Summary, success: false);
            }

            await RefreshAfterRunAsync();
            await Task.Delay(500);
        }
        catch (Exception ex)
        {
            SetResult(ex.Message, success: false);
            ProgressStatus = "Failed";
        }
        finally
        {
            IsBusy = false;
            IsProgressVisible = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    [RelayCommand]
    private async Task RepairAsync()
    {
        if (IsBusy) return;

        var ok = ConfirmAsync is not null
            ? await ConfirmAsync(
                "Reset OptiHub NVIDIA status?",
                "Only clears OptiHub's record that this pack was applied (status checks reset). Does not uninstall the driver, remove 3D profiles, or remove NVIDIA App.")
            : true;
        if (!ok) return;

        IsBusy = true;
        IsProgressVisible = true;
        ProgressPercent = 0;
        ProgressStatus = "Starting repair...";
        SetResult(string.Empty, success: true);
        _runCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<ScriptRunProgress>(p =>
            {
                ProgressPercent = p.Percent;
                ProgressStatus = p.Status;
            });

            var result = await _services.PowerShell.RunAsync(
                _services.Scripts.NvidiaRepairScript,
                arguments: new[] { "-NonInteractive" },
                elevate: false,
                progress: progress,
                cancellationToken: _runCts.Token,
                workingDirectory: _services.Scripts.GetNvidiaRoot());

            SetResult(
                result.Success ? "OptiHub status reset. Apply again to re-run the full NVIDIA pack." : (result.ErrorMessage ?? result.Summary),
                success: result.Success);

            await RefreshAfterRunAsync();
        }
        catch (Exception ex)
        {
            SetResult(ex.Message, success: false);
        }
        finally
        {
            IsBusy = false;
            IsProgressVisible = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private async Task RefreshAfterRunAsync()
    {
        try
        {
            ApplyState(await _services.OptimizerState.DetectNvidiaAsync(fastOnly: false));
        }
        catch { /* ignore */ }
    }

    private void ApplyState(OptimizerStateInfo state)
    {
        IsApplied = state.IsApplied;
        StatusText = state.StatusText;
        DetailText = state.Detail;
        if (state.Extra is { Count: > 0 } && state.Extra.TryGetValue("gsync", out var g) &&
            bool.TryParse(g, out var gsyncApplied) && IsApplied)
            UseGsync = gsyncApplied;

        Features.Clear();
        foreach (var feature in state.Features)
        {
            Features.Add(new FeatureRowViewModel
            {
                Title = feature.Title,
                Detail = feature.Detail,
                Glyph = feature.IsActive ? "\uE73E" : "\uE711",
                Opacity = feature.IsActive ? 1.0 : 0.55
            });
        }
        RunButtonLabel = state.IsApplied ? "Reapply profile" : "Apply profile";
    }

    private void SetResult(string message, bool success)
    {
        LastResult = message;
        HasLastResult = !string.IsNullOrWhiteSpace(message);
        LastResultGlyph = success ? "\uE73E" : "\uE783";
        LastResultBrush = success
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

    public async Task InitializeAsync()
    {
        ApplyState(await _services.OptimizerState.DetectNvidiaAsync(fastOnly: true));
        if (!IsBusy)
            await RefreshAsync();
    }
}
