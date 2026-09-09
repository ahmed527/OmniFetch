using OmniFetch.Core.Models;

namespace OmniFetch.Core.Segmentation;

/// <summary>
/// Manages dynamic byte-range partitioning, bisection (half-splitting), and straggler elimination.
/// </summary>
public interface ISegmentManager
{
    /// <summary>
    /// Total file size in bytes (-1 for unknown single-stream).
    /// </summary>
    long TotalBytes { get; }

    /// <summary>
    /// Total bytes downloaded and confirmed written across all segments.
    /// </summary>
    long TotalDownloadedBytes { get; }

    /// <summary>
    /// Whether all segments have completed downloading.
    /// </summary>
    bool IsCompleted { get; }

    /// <summary>
    /// Returns a point-in-time snapshot of all segments.
    /// </summary>
    IReadOnlyList<DownloadSegmentState> GetSegments();

    /// <summary>
    /// Initializes segments for a new download job.
    /// Partitions the continuous byte range [0, totalBytes - 1] across initial connection slots.
    /// </summary>
    void InitializeNewJob(Guid jobId, long totalBytes, int initialConnections);

    /// <summary>
    /// Initializes segments from previously persisted state for resumption without byte loss.
    /// </summary>
    void InitializeResumedJob(Guid jobId, long totalBytes, IEnumerable<DownloadSegmentState> existingSegments);

    /// <summary>
    /// Initializes a single unbounded stream for servers lacking byte-range support.
    /// </summary>
    void InitializeSingleStream(Guid jobId);

    /// <summary>
    /// Attempts to dynamically bisect the active segment possessing the largest remaining unread workload.
    /// Implements IDM half-splitting straggler elimination.
    /// </summary>
    /// <param name="minThreshold">Minimum remaining unread bytes needed to permit splitting (default: 5 MB).</param>
    /// <param name="newSegment">The newly created child segment to be assigned to an idle worker.</param>
    /// <returns>True if a segment was successfully bisected; false if no segment met the threshold.</returns>
    bool TryBisectLargestSegment(long minThreshold, out DownloadSegmentState? newSegment);

    /// <summary>
    /// Attempts to retrieve an incomplete segment that is not currently assigned to any active worker.
    /// </summary>
    /// <param name="segment">The unassigned segment ready for work, or null.</param>
    /// <returns>True if an unassigned segment was found; otherwise false.</returns>
    bool TryGetUnassignedSegment(out DownloadSegmentState? segment);
}
