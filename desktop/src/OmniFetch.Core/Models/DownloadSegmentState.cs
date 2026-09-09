namespace OmniFetch.Core.Models;

/// <summary>
/// Thread-safe tracking state for an individual download segment.
/// Implements IDM dynamic bisection semantics where EndByte can be clamped while CurrentByte advances.
/// </summary>
public class DownloadSegmentState
{
    private readonly object _syncLock = new();

    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public int SegmentIndex { get; set; }

    /// <summary>
    /// Initial byte offset where this segment began downloading.
    /// </summary>
    public long StartByte { get; init; }

    /// <summary>
    /// Ending byte offset of this segment (inclusive). 
    /// Clamped downwards if another thread dynamically bisects this segment.
    /// A value of -1 signifies an unbounded stream (single-stream fallback when length is unknown).
    /// </summary>
    public long EndByte { get; private set; }

    /// <summary>
    /// Current write pointer offset. All bytes up to CurrentByte have been written to disk.
    /// </summary>
    public long CurrentByte { get; private set; }

    private bool _isAssigned;

    /// <summary>
    /// Whether this segment is currently assigned to an active worker thread.
    /// </summary>
    public bool IsAssigned
    {
        get
        {
            lock (_syncLock) return _isAssigned;
        }
        set
        {
            lock (_syncLock) _isAssigned = value;
        }
    }

    public bool IsCompleted
    {
        get
        {
            lock (_syncLock)
            {
                return EndByte >= 0 && CurrentByte > EndByte;
            }
        }
    }

    public DownloadSegmentState(long startByte, long endByte, long currentByte = -1)
    {
        StartByte = startByte;
        EndByte = endByte;
        CurrentByte = currentByte < startByte ? startByte : currentByte;
    }

    /// <summary>
    /// Atomically records newly written bytes and advances the CurrentByte pointer.
    /// </summary>
    public long Advance(int bytesRead)
    {
        lock (_syncLock)
        {
            CurrentByte += bytesRead;
            return CurrentByte;
        }
    }

    /// <summary>
    /// Thread-safe retrieval of current progress and end boundaries.
    /// </summary>
    public (long current, long end, long remaining) GetProgress()
    {
        lock (_syncLock)
        {
            long remaining = EndByte >= 0 ? Math.Max(0, EndByte - CurrentByte + 1) : -1;
            return (CurrentByte, EndByte, remaining);
        }
    }

    /// <summary>
    /// Atomically clamps the EndByte to a new split point (splitPoint - 1),
    /// ensuring the current worker thread has not already passed the proposed split point.
    /// </summary>
    /// <param name="newEnd">The proposed new end byte (splitPoint - 1).</param>
    /// <returns>True if successfully clamped; false if the worker has already progressed past it.</returns>
    public bool TryClampEndByte(long newEnd)
    {
        lock (_syncLock)
        {
            if (newEnd < CurrentByte)
            {
                // Worker has already downloaded past this split point!
                return false;
            }

            if (newEnd >= EndByte && EndByte >= 0)
            {
                // Not actually shrinking the segment
                return false;
            }

            EndByte = newEnd;
            return true;
        }
    }

    /// <summary>
    /// Forcefully marks an unbounded segment as completed (for single-stream fallback).
    /// </summary>
    public void CompleteUnbounded(long finalByte)
    {
        lock (_syncLock)
        {
            EndByte = finalByte - 1;
            CurrentByte = finalByte;
        }
    }
}
