using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniFetch.App.Services;
using OmniFetch.Core.Settings;

namespace OmniFetch.App.ViewModels;

public partial class OptionsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;

    [ObservableProperty]
    private string _defaultDownloadDirectory = string.Empty;

    [ObservableProperty]
    private string _videoDirectory = string.Empty;

    [ObservableProperty]
    private string _musicDirectory = string.Empty;

    [ObservableProperty]
    private string _compressedDirectory = string.Empty;

    [ObservableProperty]
    private string _documentsDirectory = string.Empty;

    [ObservableProperty]
    private string _programsDirectory = string.Empty;

    [ObservableProperty]
    private bool _showStartDialog = true;

    [ObservableProperty]
    private bool _showCompleteDialog = true;

    [ObservableProperty]
    private int _maxConnections = 8;

    [ObservableProperty]
    private bool _isSaved;

    public Func<Task>? RequestCloseHandler { get; set; }

    public OptionsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        LoadFromSettings();
    }

    partial void OnDefaultDownloadDirectoryChanged(string? oldValue, string newValue)
    {
        if (string.IsNullOrWhiteSpace(newValue)) return;

        VideoDirectory = Path.Combine(newValue, "Video");
        MusicDirectory = Path.Combine(newValue, "Music");
        CompressedDirectory = Path.Combine(newValue, "Compressed");
        DocumentsDirectory = Path.Combine(newValue, "Documents");
        ProgramsDirectory = Path.Combine(newValue, "Programs");
    }

    private void LoadFromSettings()
    {
        var settings = _settingsService.GetSettings();
        DefaultDownloadDirectory = settings.DefaultDownloadDirectory;
        ShowStartDialog = settings.ShowStartDialog;
        ShowCompleteDialog = settings.ShowCompleteDialog;
        MaxConnections = settings.MaxConnections;

        VideoDirectory = settings.CategoryDirectories.TryGetValue("Video", out var vd) ? vd : DefaultDownloadDirectory;
        MusicDirectory = settings.CategoryDirectories.TryGetValue("Music", out var md) ? md : DefaultDownloadDirectory;
        CompressedDirectory = settings.CategoryDirectories.TryGetValue("Compressed", out var cd) ? cd : DefaultDownloadDirectory;
        DocumentsDirectory = settings.CategoryDirectories.TryGetValue("Documents", out var dd) ? dd : DefaultDownloadDirectory;
        ProgramsDirectory = settings.CategoryDirectories.TryGetValue("Programs", out var pd) ? pd : DefaultDownloadDirectory;
    }

    [RelayCommand]
    public async Task BrowseDefaultDirectoryAsync()
    {
        string? picked = await FolderPickerHelper.PickFolderAsync(DefaultDownloadDirectory);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            DefaultDownloadDirectory = picked;
        }
    }

    [RelayCommand]
    public async Task BrowseVideoDirectoryAsync()
    {
        string? picked = await FolderPickerHelper.PickFolderAsync(VideoDirectory);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            VideoDirectory = picked;
        }
    }

    [RelayCommand]
    public async Task BrowseMusicDirectoryAsync()
    {
        string? picked = await FolderPickerHelper.PickFolderAsync(MusicDirectory);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            MusicDirectory = picked;
        }
    }

    [RelayCommand]
    public async Task BrowseCompressedDirectoryAsync()
    {
        string? picked = await FolderPickerHelper.PickFolderAsync(CompressedDirectory);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            CompressedDirectory = picked;
        }
    }

    [RelayCommand]
    public async Task BrowseDocumentsDirectoryAsync()
    {
        string? picked = await FolderPickerHelper.PickFolderAsync(DocumentsDirectory);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            DocumentsDirectory = picked;
        }
    }

    [RelayCommand]
    public async Task BrowseProgramsDirectoryAsync()
    {
        string? picked = await FolderPickerHelper.PickFolderAsync(ProgramsDirectory);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            ProgramsDirectory = picked;
        }
    }

    [RelayCommand]
    public void DecreaseMaxConnections()
    {
        if (MaxConnections > 1) MaxConnections--;
    }

    [RelayCommand]
    public void IncreaseMaxConnections()
    {
        if (MaxConnections < 32) MaxConnections++;
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        try
        {
            var settings = _settingsService.GetSettings();
            settings.DefaultDownloadDirectory = DefaultDownloadDirectory;
            settings.ShowStartDialog = ShowStartDialog;
            settings.ShowCompleteDialog = ShowCompleteDialog;
            settings.MaxConnections = Math.Clamp(MaxConnections, 1, 32);

            settings.CategoryDirectories["General"] = DefaultDownloadDirectory;
            settings.CategoryDirectories["Video"] = VideoDirectory;
            settings.CategoryDirectories["Music"] = MusicDirectory;
            settings.CategoryDirectories["Compressed"] = CompressedDirectory;
            settings.CategoryDirectories["Documents"] = DocumentsDirectory;
            settings.CategoryDirectories["Programs"] = ProgramsDirectory;

            await _settingsService.SaveSettingsAsync(settings);
            IsSaved = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OptionsViewModel.SaveAsync] Error saving settings: {ex.Message}");
        }
        finally
        {
            if (RequestCloseHandler != null)
            {
                await RequestCloseHandler.Invoke();
            }
        }
    }

    [RelayCommand]
    public async Task CancelAsync()
    {
        IsSaved = false;
        if (RequestCloseHandler != null)
        {
            await RequestCloseHandler.Invoke();
        }
    }
}
