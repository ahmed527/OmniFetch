using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using OmniFetch.Core.Common;
using OmniFetch.Core.Models;
using OmniFetch.Core.Network;
using OmniFetch.Core.Segmentation;

namespace OmniFetch.Core.Engine;

/// <summary>
/// Encapsulates the runtime execution context, worker tasks, and telemetry calculations for an active download.
/// </summary>
public class DownloadSession : IAsyncDisposable
{
    private readonly object _snapshotLock = new();
    private readonly Stopwatch _stopwatch = new();
    private long _lastBytesCalculated;
    private long _lastTimestamp;
    private double _smoothedSpeed;
    private const double SpeedSmoothingAlpha = 0.25; // Exponential Moving Average smoothing factor

    public DownloadJobInfo JobInfo { get; }
    public DownloadOptions Options { get; }
    public SafeFileHandle FileHandle { get; }
    public ISegmentManager SegmentManager { get; }
    public ITokenBucketRateLimiter RateLimiter { get; }
    public CancellationTokenSource CancellationTokenSource { get; }
    public ConcurrentDictionary<Guid, Task> ActiveWorkerTasks { get; } = new();

    public DownloadSession(
        DownloadJobInfo jobInfo,
        DownloadOptions options,
        SafeFileHandle fileHandle,
        ISegmentManager segmentManager,
        ITokenBucketRateLimiter rateLimiter,
        CancellationToken externalToken)
    {
        JobInfo = jobInfo;
        Options = options;
        FileHandle = fileHandle;
        SegmentManager = segmentManager;
        RateLimiter = rateLimiter;
        CancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(externalToken);

        _stopwatch.Start();
        _lastTimestamp = Stopwatch.GetTimestamp();
        _lastBytesCalculated = segmentManager.TotalDownloadedBytes;
    }

    /// <summary>
    /// Computes real-time download telemetry, including instantaneous speed, EMA smoothed speed,
    /// estimated time remaining, and segment-level progress for IDM multi-color progress bars.
    /// </summary>
    public DownloadProgressSnapshot GenerateSnapshot()
    {
        long currentTotalDownloaded = SegmentManager.TotalDownloadedBytes;
        long totalBytes = SegmentManager.TotalBytes > 0 ? SegmentManager.TotalBytes : JobInfo.TotalBytes;

        long now = Stopwatch.GetTimestamp();
        double elapsedSeconds = (now - _lastTimestamp) / (double)Stopwatch.Frequency;

        double instantSpeed = 0;
        double smoothedSpeed;
        lock (_snapshotLock)
        {
            if (elapsedSeconds > 0.05)
            {
                long deltaBytes = currentTotalDownloaded - _lastBytesCalculated;
                instantSpeed = Math.Max(0, deltaBytes / elapsedSeconds);

                // Exponential Moving Average for smooth speed display
                if (_smoothedSpeed <= 0)
                {
                    _smoothedSpeed = instantSpeed;
                }
                else
                {
                    _smoothedSpeed = (SpeedSmoothingAlpha * instantSpeed) + ((1 - SpeedSmoothingAlpha) * _smoothedSpeed);
                }

                _lastBytesCalculated = currentTotalDownloaded;
                _lastTimestamp = now;
            }
            smoothedSpeed = _smoothedSpeed;
        }

        double percentage = 0;
        TimeSpan? eta = null;

        if (totalBytes > 0)
        {
            percentage = Math.Clamp((double)currentTotalDownloaded / totalBytes * 100.0, 0, 100.0);
            long remainingBytes = Math.Max(0, totalBytes - currentTotalDownloaded);
            if (smoothedSpeed > 1024)
            {
                double remainingSeconds = remainingBytes / smoothedSpeed;
                if (remainingSeconds >= 0 && remainingSeconds < 3600 * 24 * 7) // Up to 7 days
                {
                    eta = TimeSpan.FromSeconds(remainingSeconds);
                }
            }
        }

        // Build individual segment snapshots
        var rawSegments = SegmentManager.GetSegments();
        var segmentSnapshots = new List<SegmentProgressSnapshot>(rawSegments.Count);

        for (int i = 0; i < rawSegments.Count; i++)
        {
            var seg = rawSegments[i];
            var (cur, end, rem) = seg.GetProgress();
            long segTotal = end >= 0 ? Math.Max(0, end - seg.StartByte + 1) : -1;
            long segDone = Math.Max(0, cur - seg.StartByte);
            double segPct = (segTotal > 0) ? Math.Clamp((double)segDone / segTotal * 100.0, 0, 100.0) : 0;

            segmentSnapshots.Add(new SegmentProgressSnapshot
            {
                SegmentIndex = seg.SegmentIndex,
                StartByte = seg.StartByte,
                CurrentByte = cur,
                EndByte = end,
                ProgressPercentage = segPct,
                SpeedBytesPerSecond = smoothedSpeed / Math.Max(1, ActiveWorkerTasks.Count),
                IsCompleted = seg.IsCompleted
            });
        }

        return new DownloadProgressSnapshot
        {
            JobId = JobInfo.Id,
            Status = JobInfo.Status,
            TotalBytes = totalBytes,
            DownloadedBytes = currentTotalDownloaded,
            ProgressPercentage = percentage,
            InstantSpeedBytesPerSecond = instantSpeed,
            SmoothedSpeedBytesPerSecond = smoothedSpeed,
            EstimatedTimeRemaining = eta,
            ElapsedTime = _stopwatch.Elapsed,
            ActiveConnections = ActiveWorkerTasks.Count,
            Segments = segmentSnapshots
        };
    }

    public async ValueTask DisposeAsync()
    {
        _stopwatch.Stop();
        if (!CancellationTokenSource.IsCancellationRequested)
        {
            await CancellationTokenSource.CancelAsync();
        }
        CancellationTokenSource.Dispose();
        FileHandle.Dispose();
        GC.SuppressFinalize(this);
    }
}
