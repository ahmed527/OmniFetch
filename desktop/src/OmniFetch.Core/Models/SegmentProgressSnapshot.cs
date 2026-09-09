namespace OmniFetch.Core.Models;

/// <summary>
/// Point-in-time telemetry snapshot of an individual download segment.
/// Used for rendering IDM-style multi-color segmented progress bars in the UI.
/// </summary>
public record SegmentProgressSnapshot
{
    public int SegmentIndex { get; init; }
    public long StartByte { get; init; }
    public long CurrentByte { get; init; }
    public long EndByte { get; init; }
    public double ProgressPercentage { get; init; }
    public double SpeedBytesPerSecond { get; init; }
    public bool IsCompleted { get; init; }

    public long TotalBytes => EndByte >= 0 ? Math.Max(0, EndByte - StartByte + 1) : -1;
    public long DownloadedBytes => Math.Max(0, CurrentByte - StartByte);
    public long RemainingBytes => EndByte >= 0 ? Math.Max(0, EndByte - CurrentByte + 1) : -1;
}
