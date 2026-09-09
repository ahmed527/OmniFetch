using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniFetch.App.Models;
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

    public string PauseResumeText => Download.IsDownloading ? "Pause" : "Resume";

    public Func<Task>? RequestCloseHandler { get; set; }

    public ObservableCollection<SegmentDisplayItem> Segments => Download.Segments;

    public DownloadProgressViewModel(DownloadItemViewModel download, MainViewModel mainViewModel)
    {
        Download = download ?? throw new ArgumentNullException(nameof(download));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
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
            }
        };
    }

    private void UpdateTitle()
    {
        DialogTitle = $"{Download.ProgressPercentage:F1}% - {Download.FileName}";
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
