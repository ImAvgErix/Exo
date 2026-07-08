using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OptiHub.Models;
using OptiHub.Services;

namespace OptiHub.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        LoadFromSettings();
    }

    [ObservableProperty]
    private bool _isDarkMode = true;

    [ObservableProperty]
    private bool _autoUpdateScripts = true;

    [ObservableProperty]
    private bool _dryRun;

    [ObservableProperty]
    private bool _autoRestorePoint = true;

    [ObservableProperty]
    private bool _confirmBeforeRun = true;

    [ObservableProperty]
    private string _scriptsRepo = "BarcusEric/DiscOpti";

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

    public event EventHandler? RequestGoBack;

    [RelayCommand]
    private void GoBack() => RequestGoBack?.Invoke(this, EventArgs.Empty);

    partial void OnIsDarkModeChanged(bool value)
    {
        _services.Theme.SetTheme(value ? AppSettings.DarkTheme : AppSettings.LightTheme);
    }

    partial void OnAutoUpdateScriptsChanged(bool value) =>
        _services.Settings.Update(s => s.AutoUpdateScripts = value);

    partial void OnDryRunChanged(bool value) =>
        _services.Settings.Update(s => s.DryRun = value);

    partial void OnAutoRestorePointChanged(bool value) =>
        _services.Settings.Update(s => s.AutoRestorePoint = value);

    partial void OnConfirmBeforeRunChanged(bool value) =>
        _services.Settings.Update(s => s.ConfirmBeforeRun = value);

    partial void OnScriptsRepoChanged(string value) =>
        _services.Settings.Update(s => s.DiscordScriptsRepo = value?.Trim() ?? "BarcusEric/DiscOpti");

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
        IsDarkMode = !s.Theme.Equals(AppSettings.LightTheme, StringComparison.OrdinalIgnoreCase);
        AutoUpdateScripts = s.AutoUpdateScripts;
        DryRun = s.DryRun;
        AutoRestorePoint = s.AutoRestorePoint;
        ConfirmBeforeRun = s.ConfirmBeforeRun;
        ScriptsRepo = s.DiscordScriptsRepo;
        ScriptsBranch = s.DiscordScriptsBranch;
        CustomScriptsPath = s.CustomScriptsPath;
        KitVersion = _services.Scripts.GetWorkingVersion();
    }
}
