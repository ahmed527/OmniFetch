using OmniFetch.Core.Common;

namespace OmniFetch.Core.Models;

/// <summary>
/// Point-in-time telemetry snapshot of the entire download task.
/// Emitted at 4 Hz (every 250ms) to ensure smooth Mac Catalyst UI updates without thread contention.
/// </summary>
public record DownloadProgressSnapshot
{
    public Guid JobId { get; init; }
    public DownloadStatus Status { get; init; }
    public long TotalBytes { get; init; }
    public long DownloadedBytes { get; init; }
    public double ProgressPercentage { get; init; }
    public double InstantSpeedBytesPerSecond { get; init; }
    public double SmoothedSpeedBytesPerSecond { get; init; }
    public TimeSpan? EstimatedTimeRemaining { get; init; }
    public TimeSpan ElapsedTime { get; init; }
    public int ActiveConnections { get; init; }
    public IReadOnlyList<SegmentProgressSnapshot> Segments { get; init; } = [];

    /// <summary>
    /// Helper to format speed as human-readable string (e.g. "45.2 MB/s", "850 KB/s").
    /// </summary>
    public string FormattedSpeed => FormatBytesPerSecond(SmoothedSpeedBytesPerSecond);

    public static string FormatBytesPerSecond(double bytesPerSec)
    {
        if (bytesPerSec < 1024)
            return $"{bytesPerSec:F0} B/s";
        if (bytesPerSec < 1024 * 1024)
            return $"{bytesPerSec / 1024.0:F1} KB/s";
        if (bytesPerSec < 1024 * 1024 * 1024)
            return $"{bytesPerSec / (1024.0 * 1024.0):F2} MB/s";
        return $"{bytesPerSec / (1024.0 * 1024.0 * 1024.0):F2} GB/s";
    }
}
