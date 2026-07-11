using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    [ObservableProperty] private string _appVersion = "-";
    [ObservableProperty] private string _kitVersion = "-";
    [ObservableProperty] private string _updateStatus = "You're set. Check anytime for a newer OptiHub.";
    [ObservableProperty] private bool _isUpdating;

    public string AboutFooter { get; private set; } = "OptiHub";

    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

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
    }

    partial void OnAutoUpdateScriptsChanged(bool value)
    {
        if (!_suppressSettingsSync)
            _services.Settings.Update(s => s.AutoUpdateScripts = value);
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
        UpdateStatus = "Checking for OptiHub updates...";
        try
        {
            var progress = new Progress<string>(m => UpdateStatus = m);
            var app = await _services.Updater.CheckAppUpdateAsync(status: progress);
            AppVersion = GetAppVersionText();
            RefreshKitVersionText();

            if (app.UpdateAvailable)
            {
                var installNow = true;
                if (ConfirmAsync is not null)
                {
                    installNow = await ConfirmAsync(
                        "Update available",
                        $"OptiHub {app.RemoteVersion} is ready (you have {app.LocalVersion}).\n\n" +
                        "Install now? The app updates quietly and restarts — no extra installer window.");
                }

                if (installNow)
                {
                    UpdateStatus = "Downloading update quietly…";
                    var install = await _services.Updater.InstallAppUpdateAsync(app, status: progress);
                    UpdateStatus = string.IsNullOrWhiteSpace(install.Message)
                        ? "Applying update… OptiHub will restart."
                        : install.Message;
                    AppVersion = GetAppVersionText();
                    RefreshKitVersionText();
                    if (install.ShouldExit)
                    {
                        UpdateStatus = "Applying update… OptiHub will restart.";
                        await Task.Delay(700);
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
                UpdateStatus = string.IsNullOrWhiteSpace(app.Message)
                    ? $"You're on the latest OptiHub ({AppVersion})."
                    : app.Message.Trim().TrimEnd('.') + ".";
            }
        }
        catch (Exception ex)
        {
            UpdateStatus = ex.Message;
        }
        finally
        {
            IsUpdating = false;
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
        }
        finally
        {
            _suppressThemeSync = false;
            _suppressSettingsSync = false;
        }

        RefreshKitVersionText();
        AppVersion = GetAppVersionText();
        AboutFooter = "OptiHub " + AppVersion;
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
