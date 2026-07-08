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
                    SaveUnlocked();
                }
            }
            catch
            {
                _settings = new AppSettings();
            }
        }
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
            SaveUnlocked();
        }
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SaveUnlocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathHelper.SettingsPath)!);
        var json = JsonSerializer.Serialize(_settings, JsonOptions);
        File.WriteAllText(PathHelper.SettingsPath, json);
    }
}
