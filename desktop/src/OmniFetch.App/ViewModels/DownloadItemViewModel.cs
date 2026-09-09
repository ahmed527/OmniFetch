using System;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Maui.Graphics;
using OmniFetch.App.Models;
using OmniFetch.Core.Common;
using OmniFetch.Core.Models;

namespace OmniFetch.App.ViewModels;

public partial class DownloadItemViewModel : ObservableObject
{
    private static readonly Color[] SegmentColors =
    [
        Color.FromArgb("#10B981"), // Emerald
        Color.FromArgb("#06B6D4"), // Cyan
        Color.FromArgb("#3B82F6"), // Blue
        Color.FromArgb("#8B5CF6"), // Violet
        Color.FromArgb("#EC4899"), // Pink
        Color.FromArgb("#F59E0B"), // Amber
        Color.FromArgb("#14B8A6"), // Teal
        Color.FromArgb("#6366F1")  // Indigo
    ];

    [ObservableProperty]
    private Guid _jobId;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private string _destinationFilePath = string.Empty;

    [ObservableProperty]
    private long _totalBytes = -1;

    [ObservableProperty]
    private long _downloadedBytes;

    [ObservableProperty]
    private double _progressPercentage;

    [ObservableProperty]
    private double _progressFraction;

    [ObservableProperty]
    private DownloadStatus _status = DownloadStatus.Queued;

    [ObservableProperty]
    private string _statusText = "Queued";

    [ObservableProperty]
    private string _fileSizeText = "Unknown";

    [ObservableProperty]
    private string _transferRateText = "0 KB/s";

    [ObservableProperty]
    private string _timeLeftText = "Unknown";

    [ObservableProperty]
    private DateTime _lastTryDate = DateTime.Now;

    [ObservableProperty]
    private string _lastTryDateText = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private CategoryFilterType _category = CategoryFilterType.All;

    [ObservableProperty]
    private string _categoryText = "All Downloads";

    [ObservableProperty]
    private double _speedBytesPerSecond;

    [ObservableProperty]
    private bool _isSelected;

    public ObservableCollection<SegmentDisplayItem> Segments { get; } = [];

    public bool IsDownloading => Status == DownloadStatus.Downloading;
    public bool CanResume => Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Expired or DownloadStatus.Queued;
    public bool CanPause => Status == DownloadStatus.Downloading;

    public DownloadItemViewModel(Guid jobId, string url, string destinationPath, long totalBytes = -1)
    {
        JobId = jobId;
        Url = url;
        DestinationFilePath = destinationPath;
        FileName = Path.GetFileName(destinationPath);
        if (string.IsNullOrWhiteSpace(FileName))
        {
            FileName = "download";
        }
        TotalBytes = totalBytes;
        FileSizeText = FormatBytes(totalBytes);
        LastTryDate = DateTime.Now;
        LastTryDateText = LastTryDate.ToString("yyyy-MM-dd HH:mm");
        Category = DeduceCategory(FileName);
        CategoryText = Category.ToString();
    }

    public static CategoryFilterType DeduceCategory(string fileName)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".iso" => CategoryFilterType.Compressed,
            ".pdf" or ".doc" or ".docx" or ".txt" or ".rtf" or ".xls" or ".xlsx" or ".ppt" or ".pptx" => CategoryFilterType.Documents,
            ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".ogg" or ".wma" => CategoryFilterType.Music,
            ".dmg" or ".pkg" or ".app" or ".sh" or ".bin" or ".run" or ".deb" or ".rpm" => CategoryFilterType.Programs,
            ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" or ".flv" or ".ts" or ".m4v" => CategoryFilterType.Video,
            _ => CategoryFilterType.All
        };
    }

    public void UpdateFromSnapshot(DownloadProgressSnapshot snapshot)
    {
        DownloadedBytes = snapshot.DownloadedBytes;
        SpeedBytesPerSecond = snapshot.SmoothedSpeedBytesPerSecond;
        TransferRateText = snapshot.FormattedSpeed;
        ProgressPercentage = snapshot.ProgressPercentage;
        ProgressFraction = Math.Clamp(snapshot.ProgressPercentage / 100.0, 0.0, 1.0);

        if (TotalBytes <= 0 && snapshot.DownloadedBytes > 0)
        {
            FileSizeText = FormatBytes(snapshot.DownloadedBytes);
        }
        else if (TotalBytes > 0)
        {
            FileSizeText = FormatBytes(TotalBytes);
        }

        TimeLeftText = snapshot.EstimatedTimeRemaining.HasValue
            ? FormatDuration(snapshot.EstimatedTimeRemaining.Value)
            : (Status == DownloadStatus.Completed ? "0 sec" : "Unknown");

        LastTryDate = DateTime.Now;
        LastTryDateText = LastTryDate.ToString("yyyy-MM-dd HH:mm");

        // Sync segments
        if (snapshot.Segments != null)
        {
            for (int i = 0; i < snapshot.Segments.Count; i++)
            {
                var segState = snapshot.Segments[i];
                SegmentDisplayItem item;

                if (i < Segments.Count)
                {
                    item = Segments[i];
                }
                else
                {
                    item = new SegmentDisplayItem
                    {
                        SegmentIndex = segState.SegmentIndex,
                        SegmentColor = SegmentColors[i % SegmentColors.Length]
                    };
                    Segments.Add(item);
                }

                item.SegmentIndex = segState.SegmentIndex;
                item.StartByte = segState.StartByte;
                item.EndByte = segState.EndByte;
                item.CurrentByte = segState.CurrentByte;
                item.UpdateMetrics(TotalBytes > 0 ? TotalBytes : snapshot.TotalBytes, segState.IsCompleted, segState.SpeedBytesPerSecond);
            }

            while (Segments.Count > snapshot.Segments.Count)
            {
                Segments.RemoveAt(Segments.Count - 1);
            }
        }
    }

    public void SetStatus(DownloadStatus status)
    {
        Status = status;
        StatusText = status switch
        {
            DownloadStatus.Downloading => "Downloading",
            DownloadStatus.Completed => "100% Complete",
            DownloadStatus.Paused => "Paused",
            DownloadStatus.Failed => "Failed",
            DownloadStatus.Expired => "Expired Link",
            DownloadStatus.Queued => "Queued",
            _ => status.ToString()
        };

        if (status == DownloadStatus.Completed)
        {
            TransferRateText = "Completed";
            TimeLeftText = "0 sec";
            ProgressPercentage = 100.0;
            ProgressFraction = 1.0;
        }
        else if (status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Expired)
        {
            TransferRateText = "0 KB/s";
        }

        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanPause));
    }

    public static string FormatBytes(double bytes)
    {
        if (bytes < 0) return "Unknown";
        if (bytes < 1024) return $"{bytes:F0} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024 * 1024):F2} MB";
        return $"{bytes / (1024 * 1024 * 1024):F2} GB";
    }

    public static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60)
        {
            return $"{(int)ts.TotalSeconds} sec";
        }
        if (ts.TotalMinutes < 60)
        {
            return $"{(int)ts.TotalMinutes} min {ts.Seconds} sec";
        }
        return $"{(int)ts.TotalHours} hr {ts.Minutes} min";
    }
}
