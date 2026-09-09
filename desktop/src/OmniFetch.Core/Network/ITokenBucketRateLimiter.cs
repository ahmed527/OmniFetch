namespace OmniFetch.Core.Network;

/// <summary>
/// Token bucket rate limiter for bandwidth shaping across concurrent download threads.
/// </summary>
public interface ITokenBucketRateLimiter
{
    /// <summary>
    /// Current bandwidth limit in bytes per second (0 = Unlimited).
    /// </summary>
    long BytesPerSecondLimit { get; set; }

    /// <summary>
    /// Asynchronously requests permission to read or write the specified byte count,
    /// throttling the calling worker thread if the bucket is depleted.
    /// </summary>
    /// <param name="byteCount">Number of bytes to consume.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask ConsumeAsync(int byteCount, CancellationToken cancellationToken = default);
}
