using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OptiHub.Models;
using OptiHub.Services;

namespace OptiHub.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private bool _suppressThemeSync;

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

    public event EventHandler? RequestGoBack;

    [RelayCommand]
    private void GoBack() => RequestGoBack?.Invoke(this, EventArgs.Empty);

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

    partial void OnAutoUpdateScriptsChanged(bool value) =>
        _services.Settings.Update(s => s.AutoUpdateScripts = value);

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
                UpdateStatus = result.Message + " Installing...";
                var install = await _services.Updater.InstallLatestAppAsync(status: progress);
                UpdateStatus = install.Message;
                AppVersion = GetAppVersionText();
                if (install.ShouldExit)
                {
                    // Installer stage-swaps %LocalAppData%\OptiHub\app; exit so files unlock.
                    await Task.Delay(400);
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
        UpdateStatus = "Checking script updates...";
        try
        {
            var progress = new Progress<string>(m => UpdateStatus = m);
            var result = await _services.Updater.CheckAndUpdateDiscordScriptsAsync(force: false, status: progress);
            UpdateStatus = result.Message;
            KitVersion = _services.Scripts.GetWorkingVersion();
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
        var dark = !s.Theme.Equals(AppSettings.LightTheme, StringComparison.OrdinalIgnoreCase);
        _suppressThemeSync = true;
        IsDarkMode = dark;
        IsLightMode = !dark;
        _suppressThemeSync = false;
        AutoUpdateScripts = s.AutoUpdateScripts;
        KitVersion = _services.Scripts.GetWorkingVersion();
        AppVersion = GetAppVersionText();
        AboutFooter = "OptiHub " + AppVersion + " · https://github.com/BarcusEric/OptiHub";
    }

    private static string GetAppVersionText()
    {
        var ver = typeof(SettingsViewModel).Assembly.GetName().Version;
        return ver is null ? "1.0.0" : $"{ver.Major}.{ver.Minor}.{ver.Build}";
    }
}
