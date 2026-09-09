using System;
using System.Threading;
using System.Threading.Tasks;
using OmniFetch.Core.Models;

namespace OmniFetch.Core.Media;

public record HlsDownloadProgress
{
    public int CompletedSegments { get; init; }
    public int TotalSegments { get; init; }
    public long DownloadedBytes { get; init; }
    public double ProgressPercentage => TotalSegments > 0 ? (double)CompletedSegments / TotalSegments * 100.0 : 0.0;
    public double SpeedBytesPerSecond { get; init; }
    public TimeSpan? EstimatedTimeRemaining { get; init; }
}

public interface IHlsSegmentDownloader
{
    Task<string[]> DownloadSegmentsAsync(
        HlsMediaPlaylist playlist,
        string stagingDirectory,
        DownloadOptions? options = null,
        IProgress<HlsDownloadProgress>? progress = null,
        int maxConcurrency = 8,
        CancellationToken ct = default);
}
