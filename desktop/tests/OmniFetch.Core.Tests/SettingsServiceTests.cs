using System;
using System.IO;
using System.Threading.Tasks;
using OmniFetch.Core.Settings;
using Xunit;

namespace OmniFetch.Core.Tests;

public class SettingsServiceTests : IDisposable
{
    private readonly string _tempFile;

    public SettingsServiceTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"omnifetch_settings_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            try { File.Delete(_tempFile); } catch { }
        }
    }

    [Fact]
    public void Default_Settings_Are_Initialized_Correctly()
    {
        var service = new SettingsService(_tempFile);
        var settings = service.GetSettings();

        Assert.NotNull(settings);
        Assert.True(settings.ShowStartDialog);
        Assert.True(settings.ShowCompleteDialog);
        Assert.True(settings.AutoCloseProgressDialogOnCompletion);
        Assert.Equal(8, settings.MaxConnections);
        Assert.False(string.IsNullOrWhiteSpace(settings.DefaultDownloadDirectory));
        Assert.True(settings.CategoryDirectories.ContainsKey("Video"));
        Assert.True(settings.CategoryDirectories.ContainsKey("Music"));
    }

    [Fact]
    public async Task Save_And_Reload_Settings_Persists_Changes()
    {
        var service = new SettingsService(_tempFile);
        var settings = service.GetSettings();

        string customVideoDir = Path.Combine(Path.GetTempPath(), "CustomVideoDir");
        settings.CategoryDirectories["Video"] = customVideoDir;
        settings.MaxConnections = 16;
        settings.ShowStartDialog = false;
        settings.AutoCloseProgressDialogOnCompletion = false;

        await service.SaveSettingsAsync(settings);

        // Create new service instance reading from the same file
        var reloadedService = new SettingsService(_tempFile);
        var reloaded = reloadedService.GetSettings();

        Assert.Equal(16, reloaded.MaxConnections);
        Assert.False(reloaded.ShowStartDialog);
        Assert.False(reloaded.AutoCloseProgressDialogOnCompletion);
        Assert.Equal(customVideoDir, reloaded.CategoryDirectories["Video"]);
    }

    [Fact]
    public void GetSaveDirectoryForCategory_Resolves_Correct_Path()
    {
        var service = new SettingsService(_tempFile);
        string videoDir = service.GetSaveDirectoryForCategory("Video");
        string generalDir = service.GetSaveDirectoryForCategory("General");

        Assert.False(string.IsNullOrWhiteSpace(videoDir));
        Assert.False(string.IsNullOrWhiteSpace(generalDir));
        Assert.Contains("Video", videoDir);
    }
}
