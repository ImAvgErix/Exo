using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Exo.Models;
using Exo.Services;

namespace Exo.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private bool _suppressSettingsSync;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        LoadFromSettings();
    }

    [ObservableProperty] public partial bool CheckForUpdatesOnLaunch { get; set; }

    [ObservableProperty] public partial string AppVersion { get; set; } = "-";
    [ObservableProperty] public partial string UpdateStatus { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsUpdating { get; set; }
    [ObservableProperty] public partial double UpdateProgressPercent { get; set; }
    [ObservableProperty] public partial bool IsUpdateProgressIndeterminate { get; set; } = true;

    /// <summary>True when there is status text to show (hides empty gray well).</summary>
    public bool HasUpdateStatus => !string.IsNullOrWhiteSpace(UpdateStatus);

    /// <summary>Idle status under the button (hidden while the progress bar is up).</summary>
    public bool ShowIdleUpdateStatus => HasUpdateStatus && !IsUpdating;

    /// <summary>Compact percent label for the update bar (e.g. 42%).</summary>
    public string UpdateProgressLabel =>
        IsUpdateProgressIndeterminate || UpdateProgressPercent <= 0
            ? "…"
            : $"{UpdateProgressPercent:0}%";

    /// <summary>Branded confirm (localVer, remoteVer) → Install / Later.</summary>
    public Func<string, string, Task<bool>>? ConfirmUpdateAsync { get; set; }

    /// <summary>Modal install UI with ExoLoader + progress bar.</summary>
    public Func<AppUpdateResult, Task<AppUpdateResult>>? InstallUpdateAsync { get; set; }

    partial void OnUpdateStatusChanged(string value)
    {
        OnPropertyChanged(nameof(HasUpdateStatus));
        OnPropertyChanged(nameof(ShowIdleUpdateStatus));
    }

    partial void OnIsUpdatingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowIdleUpdateStatus));
        OnPropertyChanged(nameof(UpdateProgressLabel));
    }

    partial void OnUpdateProgressPercentChanged(double value) =>
        OnPropertyChanged(nameof(UpdateProgressLabel));

    partial void OnIsUpdateProgressIndeterminateChanged(bool value) =>
        OnPropertyChanged(nameof(UpdateProgressLabel));

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            var logs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Exo", "logs");
            Directory.CreateDirectory(logs);
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = logs,
                UseShellExecute = true
            });
            UpdateStatus = "Opened logs folder.";
        }
        catch (Exception ex)
        {
            UpdateStatus = "Could not open logs: " + ex.Message;
        }
    }

    [RelayCommand]
    private void ReportIssue()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/ImAvgErix/Exo/issues",
                UseShellExecute = true
            });
            UpdateStatus = "Opened GitHub issues.";
        }
        catch (Exception ex)
        {
            UpdateStatus = "Could not open GitHub: " + ex.Message;
        }
    }

    partial void OnCheckForUpdatesOnLaunchChanged(bool value)
    {
        if (!_suppressSettingsSync)
            _services.Settings.Update(s => s.CheckForUpdatesOnLaunch = value);
    }

    /// <summary>
    /// App-only update path. Each app release ships matching optimizer kits —
    /// no separate script refresh from GitHub.
    /// </summary>
    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsUpdating) return;
        IsUpdating = true;
        IsUpdateProgressIndeterminate = true;
        UpdateProgressPercent = 0;
        UpdateStatus = "Checking for Exo updates...";
        try
        {
            var status = new Progress<string>(m => UpdateStatus = m);
            var detail = new Progress<AppUpdateProgress>(p =>
            {
                UpdateStatus = p.Status;
                if (p.Percent >= 0)
                {
                    IsUpdateProgressIndeterminate = false;
                    UpdateProgressPercent = p.Percent;
                }
                else
                {
                    IsUpdateProgressIndeterminate = true;
                }
            });

            var app = await _services.Updater.CheckAppUpdateAsync(status: status, progress: detail);
            AppVersion = GetAppVersionText();

            if (app.UpdateAvailable)
            {
                var installNow = true;
                if (ConfirmUpdateAsync is not null)
                    installNow = await ConfirmUpdateAsync(app.LocalVersion, app.RemoteVersion);

                if (installNow)
                {
                    UpdateStatus = "Installing…";
                    AppUpdateResult install;
                    if (InstallUpdateAsync is not null)
                    {
                        // Modal: orbit loader + progress bar (same as launch auto-update).
                        install = await InstallUpdateAsync(app);
                    }
                    else
                    {
                        install = await _services.Updater.InstallAppUpdateAsync(
                            app, status: status, progress: detail);
                    }

                    UpdateStatus = install.Message;
                    AppVersion = GetAppVersionText();
                    if (install.ShouldExit)
                    {
                        await Task.Delay(400);
                        Microsoft.UI.Xaml.Application.Current?.Exit();
                        return;
                    }
                }
                else
                {
                    UpdateStatus = $"v{app.RemoteVersion} available — install skipped.";
                }
            }
            else
            {
                // Be explicit about local vs GitHub latest so "not working" is not silent.
                if (app.AlreadyLatest)
                {
                    UpdateStatus = string.IsNullOrWhiteSpace(app.Message)
                        ? $"You're on the latest Exo (v{AppVersion})."
                        : app.Message.Trim().TrimEnd('.') + ".";
                }
                else
                {
                    UpdateStatus = string.IsNullOrWhiteSpace(app.Message)
                        ? $"Update check finished (local v{AppVersion})."
                        : app.Message.Trim().TrimEnd('.') + ".";
                }
            }
        }
        catch (Exception ex)
        {
            UpdateStatus = ex.Message;
        }
        finally
        {
            IsUpdating = false;
            IsUpdateProgressIndeterminate = true;
            UpdateProgressPercent = 0;
        }
    }

    private void LoadFromSettings()
    {
        var s = _services.Settings.Current;
        _suppressSettingsSync = true;
        try
        {
            CheckForUpdatesOnLaunch = s.CheckForUpdatesOnLaunch;
        }
        finally
        {
            _suppressSettingsSync = false;
        }

        AppVersion = GetAppVersionText();
        UpdateStatus = string.Empty;
    }

    private static string GetAppVersionText()
    {
        var ver = typeof(SettingsViewModel).Assembly.GetName().Version;
        return ver is null ? "1.0.0" : $"{ver.Major}.{ver.Minor}.{ver.Build}";
    }
}
