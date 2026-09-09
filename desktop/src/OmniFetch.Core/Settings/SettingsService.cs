using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OmniFetch.Core.Settings;

/// <summary>
/// Thread-safe service for managing and persisting OmniFetch global configuration.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly string _settingsFilePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private OmniFetchSettings _currentSettings;

    public SettingsService(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            _settingsFilePath = customPath;
        }
        else
        {
            string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string omniFetchDir = Path.Combine(homeDir, ".omnifetch");
            _settingsFilePath = Path.Combine(omniFetchDir, "settings.json");
        }

        _currentSettings = LoadSettings();
    }

    public OmniFetchSettings GetSettings()
    {
        // Return active in-memory copy
        return _currentSettings;
    }

    public async Task SaveSettingsAsync(OmniFetchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            _currentSettings = settings;

            string? dir = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            string json = JsonSerializer.Serialize(settings, options);
            await File.WriteAllTextAsync(_settingsFilePath, json).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public string GetSaveDirectoryForCategory(string categoryName)
    {
        var settings = _currentSettings;

        if (!string.IsNullOrWhiteSpace(categoryName) &&
            settings.CategoryDirectories.TryGetValue(categoryName, out var dir) &&
            !string.IsNullOrWhiteSpace(dir))
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
            catch { /* Ignore creation errors */ }
            return dir;
        }

        string defaultDir = settings.DefaultDownloadDirectory;
        if (string.IsNullOrWhiteSpace(defaultDir))
        {
            defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        try
        {
            if (!Directory.Exists(defaultDir))
            {
                Directory.CreateDirectory(defaultDir);
            }
        }
        catch { /* Ignore creation errors */ }

        return defaultDir;
    }

    private OmniFetchSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                string json = File.ReadAllText(_settingsFilePath);
                var loaded = JsonSerializer.Deserialize<OmniFetchSettings>(json);
                if (loaded != null)
                {
                    // Ensure categories dictionary has default keys if missing
                    var defaults = new OmniFetchSettings();
                    foreach (var (k, v) in defaults.CategoryDirectories)
                    {
                        if (!loaded.CategoryDirectories.ContainsKey(k))
                        {
                            loaded.CategoryDirectories[k] = v;
                        }
                    }
                    return loaded;
                }
            }
        }
        catch
        {
            // If corrupt or inaccessible, fallback to standard defaults
        }

        return new OmniFetchSettings();
    }
}
