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

    [ObservableProperty]
    private bool _isDarkMode = true;

    [ObservableProperty]
    private bool _isLightMode;

    [ObservableProperty]
    private bool _autoUpdateScripts;

    [ObservableProperty]
    private string _scriptsRepo = "BarcusEric/OptiHub";

    [ObservableProperty]
    private string _scriptsBranch = "main";

    [ObservableProperty]
    private string _customScriptsPath = string.Empty;

    [ObservableProperty]
    private string _kitVersion = "—";

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    [ObservableProperty]
    private bool _isUpdating;

    public string AboutFooter { get; private set; } = "OptiHub · https://github.com/BarcusEric/OptiHub";

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

    partial void OnScriptsRepoChanged(string value) =>
        _services.Settings.Update(s => s.DiscordScriptsRepo = value?.Trim() ?? "BarcusEric/OptiHub");

    partial void OnScriptsBranchChanged(string value) =>
        _services.Settings.Update(s => s.DiscordScriptsBranch = value?.Trim() ?? "main");

    partial void OnCustomScriptsPathChanged(string value) =>
        _services.Settings.Update(s => s.CustomScriptsPath = value?.Trim() ?? string.Empty);

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (IsUpdating) return;
        IsUpdating = true;
        UpdateStatus = "Checking GitHub…";
        try
        {
            var progress = new Progress<string>(m => UpdateStatus = m);
            var result = await _services.Updater.CheckAndUpdateDiscordScriptsAsync(
                force: false,
                status: progress);
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

    [RelayCommand]
    private async Task ForceUpdateAsync()
    {
        if (IsUpdating) return;
        IsUpdating = true;
        UpdateStatus = "Force-downloading latest scripts…";
        try
        {
            var progress = new Progress<string>(m => UpdateStatus = m);
            var result = await _services.Updater.CheckAndUpdateDiscordScriptsAsync(
                force: true,
                status: progress);
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
        ScriptsRepo = string.IsNullOrWhiteSpace(s.DiscordScriptsRepo)
            ? "BarcusEric/OptiHub"
            : s.DiscordScriptsRepo;
        ScriptsBranch = s.DiscordScriptsBranch;
        CustomScriptsPath = s.CustomScriptsPath;
        KitVersion = _services.Scripts.GetWorkingVersion();
        var ver = typeof(SettingsViewModel).Assembly.GetName().Version;
        var verText = ver is null ? "1.0" : $"{ver.Major}.{ver.Minor}.{ver.Build}";
        AboutFooter = $"OptiHub {verText} · https://github.com/BarcusEric/OptiHub";
    }
}
