using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniFetch.App.Models;
using OmniFetch.App.Services;
using OmniFetch.Core.Common;

namespace OmniFetch.App.ViewModels;

public partial class DownloadProgressViewModel : ObservableObject
{
    private readonly MainViewModel _mainViewModel;

    [ObservableProperty]
    private DownloadItemViewModel _download;

    [ObservableProperty]
    private string _dialogTitle = "Downloading...";

    [ObservableProperty]
    private string _resumeCapabilityText = "Yes";

    [ObservableProperty]
    private bool _speedLimitEnabled;

    [ObservableProperty]
    private double _speedLimitKbps = 1024;

    [ObservableProperty]
    private bool _autoCloseOnCompletion = true;

    public string PauseResumeText => Download.IsDownloading ? "Pause" : "Resume";

    public string SaveToPath => Download.DestinationFilePath;

    public bool CanChangeLocation => !Download.IsDownloading;

    public Func<Task>? RequestCloseHandler { get; set; }

    public ObservableCollection<SegmentDisplayItem> Segments => Download.Segments;

    public DownloadProgressViewModel(DownloadItemViewModel download, MainViewModel mainViewModel)
    {
        Download = download ?? throw new ArgumentNullException(nameof(download));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        AutoCloseOnCompletion = _mainViewModel.CurrentSettings.AutoCloseProgressDialogOnCompletion;
        UpdateTitle();
        Download.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(Download.ProgressPercentage) or nameof(Download.FileName))
            {
                UpdateTitle();
            }
            if (e.PropertyName is nameof(Download.IsDownloading) or nameof(Download.Status))
            {
                OnPropertyChanged(nameof(PauseResumeText));
                OnPropertyChanged(nameof(CanChangeLocation));

                if (Download.Status == DownloadStatus.Completed && AutoCloseOnCompletion)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(500);
                        if (RequestCloseHandler != null)
                        {
                            await RequestCloseHandler.Invoke();
                        }
                    });
                }
            }
            if (e.PropertyName is nameof(Download.DestinationFilePath))
            {
                OnPropertyChanged(nameof(SaveToPath));
            }
        };
    }

    private void UpdateTitle()
    {
        DialogTitle = $"{Download.ProgressPercentage:F1}% - {Download.FileName}";
    }

    [RelayCommand]
    public void OpenFolder()
    {
        FolderPickerHelper.RevealInFinder(Download.DestinationFilePath);
    }

    [RelayCommand]
    public async Task ChangeLocationAsync()
    {
        if (Download.IsDownloading) return;

        string currentDir = Path.GetDirectoryName(Download.DestinationFilePath) ?? string.Empty;
        string? pickedDir = await FolderPickerHelper.PickFolderAsync(currentDir);
        if (!string.IsNullOrWhiteSpace(pickedDir) && !string.Equals(pickedDir, currentDir, StringComparison.OrdinalIgnoreCase))
        {
            string fileName = Download.FileName;
            string newFilePath = Path.Combine(pickedDir, fileName);

            try
            {
                if (File.Exists(Download.DestinationFilePath))
                {
                    File.Move(Download.DestinationFilePath, newFilePath, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DownloadProgressViewModel] Could not move file on disk: {ex.Message}");
            }

            Download.DestinationFilePath = newFilePath;
            OnPropertyChanged(nameof(SaveToPath));
            await _mainViewModel.UpdateDownloadPathAsync(Download.JobId, newFilePath);
        }
    }

    [RelayCommand]
    public async Task PauseResumeAsync()
    {
        if (Download.IsDownloading)
        {
            await _mainViewModel.StopCommand.ExecuteAsync(null);
        }
        else if (Download.CanResume)
        {
            await _mainViewModel.ResumeCommand.ExecuteAsync(null);
        }
        OnPropertyChanged(nameof(PauseResumeText));
        OnPropertyChanged(nameof(CanChangeLocation));
    }

    [RelayCommand]
    public async Task CancelAsync()
    {
        await _mainViewModel.StopCommand.ExecuteAsync(null);
        if (RequestCloseHandler != null)
        {
            await RequestCloseHandler.Invoke();
        }
    }

    [RelayCommand]
    public async Task HideAsync()
    {
        if (RequestCloseHandler != null)
        {
            await RequestCloseHandler.Invoke();
        }
    }
}
