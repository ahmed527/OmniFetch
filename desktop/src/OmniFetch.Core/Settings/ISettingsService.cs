using System.Threading.Tasks;

namespace OmniFetch.Core.Settings;

public interface ISettingsService
{
    /// <summary>
    /// Gets the current in-memory settings.
    /// </summary>
    OmniFetchSettings GetSettings();

    /// <summary>
    /// Saves modified settings to persistent storage.
    /// </summary>
    Task SaveSettingsAsync(OmniFetchSettings settings);

    /// <summary>
    /// Resolves the save directory for the given category name (e.g. "Video", "Music", "All").
    /// </summary>
    string GetSaveDirectoryForCategory(string categoryName);
}
