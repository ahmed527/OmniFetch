using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Maui.Graphics;

namespace OmniFetch.App.Models;

public enum SegmentStatus
{
    Connecting,
    Downloading,
    Completed,
    Failed,
    Idle
}

public partial class SegmentDisplayItem : ObservableObject
{
    [ObservableProperty]
    private int _segmentIndex;

    [ObservableProperty]
    private long _startByte;

    [ObservableProperty]
    private long _endByte;

    [ObservableProperty]
    private long _currentByte;

    [ObservableProperty]
    private SegmentStatus _status = SegmentStatus.Downloading;

    [ObservableProperty]
    private string _statusText = "Receiving";

    [ObservableProperty]
    private string _speedText = "0 KB/s";

    [ObservableProperty]
    private Color _segmentColor = Colors.SeaGreen;

    [ObservableProperty]
    private double _relativeWidth = 0.25;

    [ObservableProperty]
    private double _fillFraction = 0.0;

    public long SegmentSize => Math.Max(0, EndByte - StartByte + 1);
    public long DownloadedBytes => Math.Max(0, CurrentByte - StartByte);

    public void UpdateMetrics(long totalFileSize, bool isCompleted, double speedBytesPerSec = 0)
    {
        long segSize = SegmentSize;
        long downloaded = DownloadedBytes;

        FillFraction = segSize > 0 ? Math.Clamp((double)downloaded / segSize, 0.0, 1.0) : 0.0;

        if (totalFileSize > 0)
        {
            RelativeWidth = Math.Max(0.02, (double)segSize / totalFileSize);
        }
        else
        {
            RelativeWidth = 0.25;
        }

        Status = isCompleted ? SegmentStatus.Completed : SegmentStatus.Downloading;
        StatusText = Status switch
        {
            SegmentStatus.Downloading => "Receiving data",
            SegmentStatus.Completed => "Complete",
            SegmentStatus.Connecting => "Connecting...",
            SegmentStatus.Failed => "Retrying",
            _ => "Idle"
        };

        if (speedBytesPerSec > 0)
        {
            if (speedBytesPerSec < 1024)
                SpeedText = $"{speedBytesPerSec:F0} B/s";
            else if (speedBytesPerSec < 1024 * 1024)
                SpeedText = $"{speedBytesPerSec / 1024.0:F1} KB/s";
            else
                SpeedText = $"{speedBytesPerSec / (1024.0 * 1024.0):F2} MB/s";
        }
        else if (isCompleted)
        {
            SpeedText = "-";
        }
    }
}
