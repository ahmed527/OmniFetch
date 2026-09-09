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

    private long _endByte;
    private long _currentByte;

    /// <summary>
    /// Initial byte offset where this segment began downloading.
    /// </summary>
    public long StartByte { get; init; }

    /// <summary>
    /// Ending byte offset of this segment (inclusive). 
    /// Clamped downwards if another thread dynamically bisects this segment.
    /// A value of -1 signifies an unbounded stream (single-stream fallback when length is unknown).
    /// </summary>
    public long EndByte
    {
        get
        {
            lock (_syncLock) return _endByte;
        }
        private set
        {
            lock (_syncLock) _endByte = value;
        }
    }

    /// <summary>
    /// Current write pointer offset. All bytes up to CurrentByte have been written to disk.
    /// </summary>
    public long CurrentByte
    {
        get
        {
            lock (_syncLock) return _currentByte;
        }
        private set
        {
            lock (_syncLock) _currentByte = value;
        }
    }

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
                return _endByte >= 0 && _currentByte > _endByte;
            }
        }
    }

    public DownloadSegmentState(long startByte, long endByte, long currentByte = -1)
    {
        StartByte = startByte;
        _endByte = endByte;
        _currentByte = currentByte < startByte ? startByte : currentByte;
    }

    /// <summary>
    /// Atomically records newly written bytes and advances the CurrentByte pointer.
    /// </summary>
    public long Advance(int bytesRead)
    {
        lock (_syncLock)
        {
            _currentByte += bytesRead;
            return _currentByte;
        }
    }

    /// <summary>
    /// Thread-safe retrieval of current progress and end boundaries.
    /// </summary>
    public (long current, long end, long remaining) GetProgress()
    {
        lock (_syncLock)
        {
            long remaining = _endByte >= 0 ? Math.Max(0, _endByte - _currentByte + 1) : -1;
            return (_currentByte, _endByte, remaining);
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
            if (newEnd < _currentByte)
            {
                // Worker has already downloaded past this split point!
                return false;
            }

            if (newEnd >= _endByte && _endByte >= 0)
            {
                // Not actually shrinking the segment
                return false;
            }

            _endByte = newEnd;
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
            _endByte = finalByte - 1;
            _currentByte = finalByte;
        }
    }
}
