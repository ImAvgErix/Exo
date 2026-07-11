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
    [ObservableProperty] private string _updateStatus = string.Empty;
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

    [RelayCommand]
    private async Task CheckAppUpdatesAsync()
    {
        if (IsUpdating) return;
        IsUpdating = true;
        UpdateStatus = "Checking for OptiHub updates...";
        try
        {
            var progress = new Progress<string>(m => UpdateStatus = m);
            var result = await _services.Updater.CheckAppUpdateAsync(status: progress);
            UpdateStatus = result.Message;
            AppVersion = GetAppVersionText();
            if (result.UpdateAvailable)
            {
                // Manual check: still ask before replacing the running install.
                var installNow = true;
                if (ConfirmAsync is not null)
                {
                    installNow = await ConfirmAsync(
                        "Install OptiHub update?",
                        $"Version {result.RemoteVersion} is available (you have {result.LocalVersion}).\n\n" +
                        "OptiHub will close, install in place, and reopen.");
                }

                if (!installNow)
                {
                    UpdateStatus = $"Update v{result.RemoteVersion} available — install skipped.";
                    return;
                }

                UpdateStatus = result.Message + " Installing...";
                var install = await _services.Updater.InstallAppUpdateAsync(result, status: progress);
                UpdateStatus = install.Message;
                AppVersion = GetAppVersionText();
                if (install.ShouldExit)
                {
                    // Give the SFX a moment to start, then exit so %LocalAppData%\OptiHub\app unlocks.
                    UpdateStatus = install.Message;
                    await Task.Delay(900);
                    Microsoft.UI.Xaml.Application.Current?.Exit();
                    return;
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
        }
    }

    [RelayCommand]
    private async Task CheckScriptUpdatesAsync()
    {
        if (IsUpdating) return;
        IsUpdating = true;
        UpdateStatus = "Checking Discord / Steam / NVIDIA script updates...";
        try
        {
            var progress = new Progress<string>(m => UpdateStatus = m);
            // force:true so a stuck equal/older VERSION still re-pulls kits from GitHub main
            // when the user explicitly clicks Update Scripts.
            var result = await _services.Updater.CheckAndUpdateAllScriptsAsync(force: true, status: progress);
            UpdateStatus = result.Message;
            KitVersion =
                $"D{_services.Scripts.GetWorkingKitVersion("Discord")} / " +
                $"S{_services.Scripts.GetWorkingKitVersion("Steam")} / " +
                $"N{_services.Scripts.GetWorkingKitVersion("Nvidia")}";
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

        KitVersion = _services.Scripts.GetWorkingVersion();
        AppVersion = GetAppVersionText();
        AboutFooter = "OptiHub " + AppVersion;
    }

    private static string GetAppVersionText()
    {
        var ver = typeof(SettingsViewModel).Assembly.GetName().Version;
        return ver is null ? "1.0.0" : $"{ver.Major}.{ver.Minor}.{ver.Build}";
    }
}
