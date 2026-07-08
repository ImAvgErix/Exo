using System.Text.Json;
using OptiHub.Helpers;
using OptiHub.Models;

namespace OptiHub.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private AppSettings _settings = new();
    private readonly object _lock = new();
    private readonly object _saveLock = new();
    private CancellationTokenSource? _debounceCts;
    private bool _dirty;

    public AppSettings Current
    {
        get
        {
            lock (_lock) return _settings;
        }
    }

    public event EventHandler? SettingsChanged;

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(PathHelper.SettingsPath))
                {
                    var json = File.ReadAllText(PathHelper.SettingsPath);
                    _settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                }
                else
                {
                    _settings = new AppSettings();
                }

                if (MigrateLegacySettings(_settings))
                    SaveUnlocked();
                else if (!File.Exists(PathHelper.SettingsPath))
                    SaveUnlocked();
            }
            catch
            {
                _settings = new AppSettings();
            }
        }
    }

    private static bool MigrateLegacySettings(AppSettings settings)
    {
        var changed = false;
        var repo = (settings.DiscordScriptsRepo ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(repo) ||
            repo.Contains("DiscOpti", StringComparison.OrdinalIgnoreCase))
        {
            settings.DiscordScriptsRepo = "BarcusEric/OptiHub";
            changed = true;
        }
        return changed;
    }

    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            _settings = settings;
            SaveUnlocked();
        }
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Update(Action<AppSettings> mutator)
    {
        lock (_lock)
        {
            mutator(_settings);
            _dirty = true;
        }
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        ScheduleDebouncedSave();
    }

    private void ScheduleDebouncedSave()
    {
        lock (_saveLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(400, token);
                    FlushIfDirty();
                }
                catch (OperationCanceledException)
                {
                    // newer update scheduled
                }
            }, token);
        }
    }

    public void FlushIfDirty()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            SaveUnlocked();
            _dirty = false;
        }
    }

    private void SaveUnlocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathHelper.SettingsPath)!);
        var json = JsonSerializer.Serialize(_settings, JsonOptions);
        File.WriteAllText(PathHelper.SettingsPath, json);
    }
}
