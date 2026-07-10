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

    [ObservableProperty] private string _statusText = "Checking status...";
    [ObservableProperty] private string _detailText = string.Empty;
    public ObservableCollection<FeatureRowViewModel> Features { get; } = new();

    [ObservableProperty] private string _runButtonLabel = "Apply profile";
    [ObservableProperty] private bool _isApplied;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isProgressVisible;
    [ObservableProperty] private bool _isStatusLoading = true;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressStatus = string.Empty;
    [ObservableProperty] private string _lastResult = string.Empty;
    [ObservableProperty] private bool _hasLastResult;
    [ObservableProperty] private string _lastResultGlyph = "\uE73E";
    [ObservableProperty] private Brush _lastResultBrush;
    [ObservableProperty] private bool _useGsync;

    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsStatusLoading = true;
        try
        {
            StatusText = "Checking status...";
            var state = await _services.OptimizerState.DetectNvidiaAsync();
            ApplyState(state);
        }
        catch (Exception ex)
        {
            StatusText = "Status unavailable";
            DetailText = "OptiHub could not read the NVIDIA driver state. You can retry without applying changes.";
            SetResult($"Status refresh failed: {ex.Message}", success: false);
        }
        finally
        {
            IsStatusLoading = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsBusy) return;

        var action = IsApplied ? "reapply" : "apply";
        var gsyncLine = UseGsync
            ? "• G-SYNC pack: adaptive sync stays on; Ultra Low Latency is off to avoid conflicts."
            : "• Max FPS / latency pack: Ultra Low Latency Ultra; G-SYNC and V-Sync forced off.";
        var warning =
            "This is an aggressive maximum-performance pass. It prioritizes FPS and input latency over power savings, NVIDIA background features, and recording tools.\n\n" +
            "OptiHub will:\n" +
            "1) Update the Game Ready display driver only when needed, then enable MSI High priority and disable Ansel/telemetry services.\n" +
            "2) Import the matching 3D Base Profile (maximum-performance power mode, high-performance filtering, max refresh, shader cache, and latency settings).\n" +
            "3) Keep each NVIDIA-connected display's current resolution, select its highest verified refresh rate, and apply Full RGB plus GPU no-scaling.\n" +
            "4) Stop NVIDIA App/GFE background clients and disable overlay, FrameView, updater, telemetry, and auto-start paths while preserving installed files and HDMI/DisplayPort audio.\n\n" +
            gsyncLine + "\n\n" +
            "Tradeoffs: higher idle power/heat, no NVIDIA overlay or background recording, and a brief display flicker. A driver update may require a restart.\n\n" +
            "Reset OptiHub status only clears OptiHub's record; it does not undo driver/profile/display changes. Undo through NVIDIA settings or a driver reinstall. Administrator approval is required.";

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
                var output = $"{result.FullOutput}\n{result.Summary}";
                if (output.Contains("RESTART_REQUIRED", StringComparison.OrdinalIgnoreCase))
                {
                    ProgressStatus = "Restart required";
                    SetResult(
                        "The display driver installed successfully. Restart Windows, then Apply once more to finish the 3D profile and display settings.",
                        success: true);
                }
                else if (output.Contains("Clean Driver failed", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("Clean driver failed", StringComparison.OrdinalIgnoreCase))
                {
                    ProgressStatus = "Clean driver failed";
                    SetResult(
                        "OptiHub Clean Driver did not finish. Check the log, free disk space, close games, and Apply again as Administrator.",
                        success: false);
                }
                else if (output.Contains("clean driver -> 3D", StringComparison.OrdinalIgnoreCase) ||
                         output.Contains("Completed successfully", StringComparison.OrdinalIgnoreCase) ||
                         output.Contains("one pass", StringComparison.OrdinalIgnoreCase))
                {
                    ProgressStatus = "Completed successfully";
                    SetResult(
                        "Done in one pass: clean driver (if needed), 3D profile, privacy preferences, and verified NVAPI display settings. No reboot required unless Windows prompts.",
                        success: true);
                }
                else
                {
                    ProgressStatus = "Completed successfully";
                    SetResult("Done. Profile imported into the NVIDIA driver.", success: true);
                }
            }
            else
            {
                ProgressStatus = result.ExitCode == -2 ? "Cancelled" : "Failed";
                SetResult(result.ErrorMessage ?? result.Summary, success: false);
            }

            await RefreshAfterRunAsync();
        }
        catch (OperationCanceledException)
        {
            SetResult("NVIDIA optimization was cancelled. Changes completed before cancellation were kept.", success: false);
            ProgressStatus = "Cancelled";
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
        catch (OperationCanceledException)
        {
            SetResult("Status reset was cancelled.", success: false);
            ProgressStatus = "Cancelled";
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
        IsStatusLoading = false;
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
        IsStatusLoading = true;
        try
        {
            ApplyState(await _services.OptimizerState.DetectNvidiaAsync(fastOnly: true));
            if (!IsBusy)
                await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = "Status unavailable";
            DetailText = "OptiHub could not initialize the NVIDIA optimizer.";
            SetResult(ex.Message, success: false);
        }
        finally
        {
            IsStatusLoading = false;
        }
    }
}
