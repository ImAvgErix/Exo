using System.Text.Json;
using Exo.Helpers;
using Exo.Models;
using Exo.Serialization;

namespace Exo.Services;

public sealed class SettingsService
{
    private AppSettings _settings = new();
    private readonly object _lock = new();
    private readonly object _saveLock = new();
    private CancellationTokenSource? _debounceCts;
    private bool _dirty;

    public AppSettings Current
    {
        get
        {
            lock (_lock) return _settings.Clone();
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
                    _settings = System.Text.Json.JsonSerializer.Deserialize(
                        json,
                        ExoJsonContext.Default.AppSettings) ?? new AppSettings();
                }
                else
                {
                    _settings = new AppSettings();
                }

                _dirty = false;
                if (MigrateLegacySettings(_settings) || !File.Exists(PathHelper.SettingsPath))
                    _dirty = !TrySaveUnlocked();
            }
            catch (JsonException)
            {
                _settings = new AppSettings();
                BackupInvalidSettings();
                _dirty = !TrySaveUnlocked();
            }
            catch (IOException)
            {
                _settings = new AppSettings();
                _dirty = true;
            }
            catch (UnauthorizedAccessException)
            {
                _settings = new AppSettings();
                _dirty = true;
            }
        }
    }

    private static bool MigrateLegacySettings(AppSettings settings)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(settings.DiscordKitVersion))
        {
            settings.DiscordKitVersion = "1.3.72";
            changed = true;
        }

        return changed;
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        bool needsRetry;
        lock (_lock)
        {
            _settings = settings.Clone();
            _dirty = !TrySaveUnlocked();
            needsRetry = _dirty;
        }
        if (needsRetry)
            ScheduleDebouncedSave();

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Update(Action<AppSettings> mutator)
    {
        ArgumentNullException.ThrowIfNull(mutator);

        lock (_lock)
        {
            mutator(_settings);
            _dirty = true;
        }
        ScheduleDebouncedSave();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
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
                    await Task.Delay(400, token).ConfigureAwait(false);
                    FlushIfDirty();
                }
                catch (OperationCanceledException)
                {
                    // newer update scheduled
                }
                catch (IOException)
                {
                    // Keep the dirty flag set so shutdown or a later update can retry.
                }
                catch (UnauthorizedAccessException)
                {
                    // Keep the dirty flag set so shutdown or a later update can retry.
                }
            }, token);
        }
    }

    public void FlushIfDirty()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            _dirty = !TrySaveUnlocked();
        }
    }

    public void Flush()
    {
        lock (_saveLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }

        try
        {
            FlushIfDirty();
        }
        catch (IOException)
        {
            // The application is closing; there is no safe recovery path here.
        }
        catch (UnauthorizedAccessException)
        {
            // The application is closing; there is no safe recovery path here.
        }
    }

    private bool TrySaveUnlocked()
    {
        var settingsPath = PathHelper.SettingsPath;
        var directory = Path.GetDirectoryName(settingsPath)!;
        var tempPath = settingsPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            var json = System.Text.Json.JsonSerializer.Serialize(
                _settings,
                ExoJsonContext.Default.AppSettings);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, settingsPath, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
        }
    }

    private static void BackupInvalidSettings()
    {
        var settingsPath = PathHelper.SettingsPath;
        if (!File.Exists(settingsPath)) return;

        try
        {
            var backupPath = settingsPath + $".invalid-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(settingsPath, backupPath, overwrite: true);
        }
        catch
        {
            // Preserve the original if it cannot be moved.
        }
    }
}
