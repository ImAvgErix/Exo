using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OptiHub.Helpers;
using OptiHub.Models;
using OptiHub.Services;

namespace OptiHub.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private bool _suppressThemeSync;
    private bool _suppressSettingsSync;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        LoadFromSettings();
    }

    [ObservableProperty] private bool _isDarkMode = true;
    [ObservableProperty] private bool _isLightMode;
    [ObservableProperty] private bool _autoUpdateScripts;

    /// <summary>Live continuous setting 0–100. Drives entrance travel via OptiMotion.</summary>
    [ObservableProperty] private double _motionIntensity = 100;

    /// <summary>Current theme name shown large (what you are in right now).</summary>
    public string CurrentThemeLabel => IsDarkMode ? "Dark mode" : "Light mode";

    /// <summary>Secondary line on the theme control (what a tap will do).</summary>
    public string ThemeSwitchHint => IsDarkMode ? "Tap to switch to light" : "Tap to switch to dark";

    /// <summary>Back-compat for smoke / older binds — always the current mode name.</summary>
    public string ThemeToggleLabel => CurrentThemeLabel;

    /// <summary>Live label next to the motion slider (e.g. 80%).</summary>
    public string MotionIntensityLabel => $"{MotionIntensity:0}%";

    [ObservableProperty] private string _appVersion = "-";
    [ObservableProperty] private string _kitVersion = "-";
    [ObservableProperty] private string _updateStatus = string.Empty;
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private double _updateProgressPercent;
    [ObservableProperty] private bool _isUpdateProgressIndeterminate = true;

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

    /// <summary>Modal install UI with OptiLoader + progress bar.</summary>
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
                "OptiHub", "logs");
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
                FileName = "https://github.com/UhhErix/OptiHub/issues",
                UseShellExecute = true
            });
            UpdateStatus = "Opened GitHub issues.";
        }
        catch (Exception ex)
        {
            UpdateStatus = "Could not open GitHub: " + ex.Message;
        }
    }

    partial void OnIsDarkModeChanged(bool value)
    {
        if (_suppressThemeSync) return;
        if (value)
        {
            _suppressThemeSync = true;
            IsLightMode = false;
            _suppressThemeSync = false;
            _services.Theme.SetTheme(AppSettings.DarkTheme);
        }
        else if (!IsLightMode)
        {
            _suppressThemeSync = true;
            IsLightMode = true;
            _suppressThemeSync = false;
            _services.Theme.SetTheme(AppSettings.LightTheme);
        }
        NotifyThemeLabels();
    }

    partial void OnIsLightModeChanged(bool value)
    {
        if (_suppressThemeSync) return;
        if (value)
        {
            _suppressThemeSync = true;
            IsDarkMode = false;
            _suppressThemeSync = false;
            _services.Theme.SetTheme(AppSettings.LightTheme);
        }
        else if (!IsDarkMode)
        {
            _suppressThemeSync = true;
            IsDarkMode = true;
            _suppressThemeSync = false;
            _services.Theme.SetTheme(AppSettings.DarkTheme);
        }
        NotifyThemeLabels();
    }

    private void NotifyThemeLabels()
    {
        OnPropertyChanged(nameof(CurrentThemeLabel));
        OnPropertyChanged(nameof(ThemeSwitchHint));
        OnPropertyChanged(nameof(ThemeToggleLabel));
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        // Flip: dark → light, light → dark
        IsDarkMode = !IsDarkMode;
    }

    partial void OnAutoUpdateScriptsChanged(bool value)
    {
        if (!_suppressSettingsSync)
            _services.Settings.Update(s => s.AutoUpdateScripts = value);
    }

    partial void OnMotionIntensityChanged(double value)
    {
        var clamped = Math.Clamp(value, 0, 100);
        if (Math.Abs(clamped - value) > 0.01)
        {
            MotionIntensity = clamped;
            return;
        }

        OptiMotion.MotionStrength = clamped / 100.0;
        OnPropertyChanged(nameof(MotionIntensityLabel));
        if (!_suppressSettingsSync)
            _services.Settings.Update(s => s.MotionIntensity = clamped);
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
        UpdateStatus = "Checking for OptiHub updates...";
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
            RefreshKitVersionText();

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
                    RefreshKitVersionText();
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
                        ? $"You're on the latest OptiHub (v{AppVersion})."
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

    // Back-compat command names (XAML / callers that still bind old names).
    [RelayCommand]
    private Task CheckAppUpdatesAsync() => CheckForUpdatesAsync();

    [RelayCommand]
    private Task CheckScriptUpdatesAsync() => CheckForUpdatesAsync();

    private void LoadFromSettings()
    {
        var s = _services.Settings.Current;
        var dark = !string.Equals(s.Theme, AppSettings.LightTheme, StringComparison.OrdinalIgnoreCase);
        _suppressSettingsSync = true;
        _suppressThemeSync = true;
        try
        {
            IsDarkMode = dark;
            IsLightMode = !dark;
            AutoUpdateScripts = s.AutoUpdateScripts;
            MotionIntensity = Math.Clamp(s.MotionIntensity, 0, 100);
            OptiMotion.MotionStrength = MotionIntensity / 100.0;
        }
        finally
        {
            _suppressThemeSync = false;
            _suppressSettingsSync = false;
        }

        RefreshKitVersionText();
        AppVersion = GetAppVersionText();
        UpdateStatus = string.Empty;
        OnPropertyChanged(nameof(MotionIntensityLabel));
    }

    private void RefreshKitVersionText()
    {
        // Compact: Discord · Steam · NVIDIA
        KitVersion =
            $"{_services.Scripts.GetWorkingKitVersion("Discord")} · " +
            $"{_services.Scripts.GetWorkingKitVersion("Steam")} · " +
            $"{_services.Scripts.GetWorkingKitVersion("Nvidia")}";
    }

    private static string GetAppVersionText()
    {
        var ver = typeof(SettingsViewModel).Assembly.GetName().Version;
        return ver is null ? "1.0.0" : $"{ver.Major}.{ver.Minor}.{ver.Build}";
    }
}
