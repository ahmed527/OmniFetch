using OmniFetch.Core.Common;
using OmniFetch.Core.Models;

namespace OmniFetch.Core.Segmentation;

/// <summary>
/// Implements IDM dynamic bisection (half-splitting) and straggler elimination.
/// Coordinates segment creation, boundary clamping, and work-stealing across threads.
/// </summary>
public class DynamicSegmentManager : ISegmentManager
{
    private readonly object _syncLock = new();
    private readonly List<DownloadSegmentState> _segments = [];
    private Guid _jobId;
    private long _totalBytes = -1;

    public long TotalBytes
    {
        get
        {
            lock (_syncLock) return _totalBytes;
        }
    }

    public long TotalDownloadedBytes
    {
        get
        {
            lock (_syncLock)
            {
                long total = 0;
                foreach (var seg in _segments)
                {
                    var (current, _, _) = seg.GetProgress();
                    total += Math.Max(0, current - seg.StartByte);
                }
                return total;
            }
        }
    }

    public bool IsCompleted
    {
        get
        {
            lock (_syncLock)
            {
                if (_segments.Count == 0) return false;
                foreach (var seg in _segments)
                {
                    if (!seg.IsCompleted) return false;
                }
                return true;
            }
        }
    }

    public IReadOnlyList<DownloadSegmentState> GetSegments()
    {
        lock (_syncLock)
        {
            return [.. _segments];
        }
    }

    public void InitializeNewJob(Guid jobId, long totalBytes, int initialConnections)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalBytes);
        initialConnections = Math.Clamp(initialConnections, 1, Constants.MaxAllowedConnections);

        lock (_syncLock)
        {
            _jobId = jobId;
            _totalBytes = totalBytes;
            _segments.Clear();

            // Guard against over-partitioning files smaller than the minimum threshold
            int effectiveConnections = initialConnections;
            if (totalBytes < initialConnections * Constants.AbsoluteMinSplitThreshold)
            {
                effectiveConnections = Math.Max(1, (int)(totalBytes / Constants.AbsoluteMinSplitThreshold));
                effectiveConnections = Math.Min(effectiveConnections, initialConnections);
            }

            long baseChunkSize = totalBytes / effectiveConnections;
            long remainder = totalBytes % effectiveConnections;

            long currentOffset = 0;
            for (int i = 0; i < effectiveConnections; i++)
            {
                long chunkSize = baseChunkSize + (i == effectiveConnections - 1 ? remainder : 0);
                long start = currentOffset;
                long end = start + chunkSize - 1;

                var segment = new DownloadSegmentState(start, end, start)
                {
                    JobId = _jobId,
                    SegmentIndex = i
                };

                _segments.Add(segment);
                currentOffset = end + 1;
            }
        }
    }

    public void InitializeResumedJob(Guid jobId, long totalBytes, IEnumerable<DownloadSegmentState> existingSegments)
    {
        ArgumentNullException.ThrowIfNull(existingSegments);

        lock (_syncLock)
        {
            _jobId = jobId;
            _totalBytes = totalBytes;
            _segments.Clear();
            foreach (var seg in existingSegments)
            {
                _segments.Add(new DownloadSegmentState(seg.StartByte, seg.EndByte, seg.CurrentByte)
                {
                    JobId = jobId,
                    SegmentIndex = seg.SegmentIndex,
                    IsAssigned = false
                });
            }
        }
    }

    public void InitializeSingleStream(Guid jobId)
    {
        lock (_syncLock)
        {
            _jobId = jobId;
            _totalBytes = -1;
            _segments.Clear();

            var segment = new DownloadSegmentState(0, -1, 0)
            {
                JobId = _jobId,
                SegmentIndex = 0
            };

            _segments.Add(segment);
        }
    }

    public bool TryGetUnassignedSegment(out DownloadSegmentState? segment)
    {
        segment = null;
        lock (_syncLock)
        {
            foreach (var seg in _segments)
            {
                if (!seg.IsCompleted && !seg.IsAssigned)
                {
                    seg.IsAssigned = true;
                    segment = seg;
                    return true;
                }
            }
            return false;
        }
    }

    public bool TryBisectLargestSegment(long minThreshold, out DownloadSegmentState? newSegment)
    {
        newSegment = null;
        minThreshold = Math.Max(minThreshold, Constants.AbsoluteMinSplitThreshold);

        lock (_syncLock)
        {
            DownloadSegmentState? candidate = null;
            long maxRemaining = 0;
            long candidateOriginalEnd = 0;

            // 1. Locate active segment with largest remaining byte delta: remaining = EndByte - CurrentByte + 1
            foreach (var segment in _segments)
            {
                if (segment.IsCompleted || segment.EndByte < 0)
                {
                    continue; // Skip finished or unbounded single streams
                }

                var (current, end, remaining) = segment.GetProgress();
                if (remaining > maxRemaining && remaining >= minThreshold)
                {
                    maxRemaining = remaining;
                    candidate = segment;
                    candidateOriginalEnd = end;
                }
            }

            if (candidate == null || maxRemaining < minThreshold)
            {
                return false;
            }

            // 2. Calculate midpoint: splitPoint = CurrentByte + (remaining / 2)
            var (currentPointer, currentEnd, _) = candidate.GetProgress();
            long remainingBytes = currentEnd - currentPointer + 1;
            if (remainingBytes < minThreshold)
            {
                return false;
            }

            long halfSpan = remainingBytes / 2;
            long splitPoint = currentPointer + halfSpan;

            if (splitPoint <= currentPointer || splitPoint > currentEnd)
            {
                return false;
            }

            // 3. Atomically clamp original segment's EndByte to splitPoint - 1
            long proposedClampEnd = splitPoint - 1;
            if (!candidate.TryClampEndByte(proposedClampEnd))
            {
                return false; // Worker thread advanced past split point during negotiation
            }

            // 4. Spawn new segment assigned to the stolen range: [splitPoint, originalEndByte]
            newSegment = new DownloadSegmentState(splitPoint, candidateOriginalEnd, splitPoint)
            {
                JobId = _jobId,
                SegmentIndex = _segments.Count,
                IsAssigned = true
            };

            _segments.Add(newSegment);
            return true;
        }
    }
}
