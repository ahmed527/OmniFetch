using OmniFetch.Core.Common;

namespace OmniFetch.Core.Models;

/// <summary>
/// Configuration options governing multi-stream downloading, bandwidth shaping, and connection parameters.
/// </summary>
public record DownloadOptions
{
    /// <summary>
    /// Maximum parallel TCP/HTTP connections to allocate for this download (default: 8, max: 32).
    /// </summary>
    public int MaxConnections { get; init; } = Constants.DefaultMaxConnections;

    /// <summary>
    /// Minimum remaining unread bytes in a segment required before bisection is allowed (default: 5 MB).
    /// </summary>
    public long MinSplitThresholdBytes { get; init; } = Constants.DefaultMinSplitThreshold;

    /// <summary>
    /// Size of the memory buffer rented from ArrayPool for socket reading (default: 81,920 bytes).
    /// </summary>
    public int BufferSizeBytes { get; init; } = Constants.DefaultBufferSize;

    /// <summary>
    /// Bandwidth ceiling in bytes per second for the Token Bucket Rate Limiter (0 = Unlimited).
    /// </summary>
    public long SpeedLimitBytesPerSecond { get; init; } = 0;

    /// <summary>
    /// Maximum retries per segment on transient network drops before failing the segment.
    /// </summary>
    public int MaxRetriesPerSegment { get; init; } = Constants.DefaultMaxSegmentRetries;

    /// <summary>
    /// Throttle interval in milliseconds for firing UI progress events (default: 250ms = 4 Hz).
    /// </summary>
    public int ProgressThrottleIntervalMs { get; init; } = Constants.DefaultProgressThrottleIntervalMs;

    /// <summary>
    /// Interval in milliseconds between asynchronous database write-behind state commits (default: 2,000ms).
    /// </summary>
    public int WriteBehindFlushIntervalMs { get; init; } = Constants.DefaultWriteBehindFlushIntervalMs;

    /// <summary>
    /// Custom User-Agent header (passed from browser extension).
    /// </summary>
    public string? UserAgent { get; init; }

    /// <summary>
    /// Origin Referrer URL (for hot-swapping expired links).
    /// </summary>
    public string? Referrer { get; init; }

    /// <summary>
    /// Serialized cookie header string (e.g. "session_id=123; auth=abc").
    /// </summary>
    public string? Cookies { get; init; }

    /// <summary>
    /// Additional custom HTTP headers.
    /// </summary>
    public Dictionary<string, string> CustomHeaders { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When resuming an existing download, validate the server's ETag matches the previously stored ETag.
    /// </summary>
    public bool ValidateETagOnResume { get; init; } = true;
}
