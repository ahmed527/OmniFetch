using System.Diagnostics;

namespace OmniFetch.Core.Network;

/// <summary>
/// High-precision, thread-safe Token Bucket Rate Limiter for bandwidth shaping.
/// Distributes throughput fairly across all parallel connection streams.
/// </summary>
public sealed class TokenBucketRateLimiter : ITokenBucketRateLimiter
{
    private readonly object _syncLock = new();
    private long _bytesPerSecondLimit;
    private double _availableTokens;
    private long _lastRefillTimestamp;
    private readonly double _tickFrequency;

    public long BytesPerSecondLimit
    {
        get => Interlocked.Read(ref _bytesPerSecondLimit);
        set
        {
            lock (_syncLock)
            {
                Interlocked.Exchange(ref _bytesPerSecondLimit, Math.Max(0, value));
                // Set bucket capacity to 1 second worth of bandwidth or a sensible burst floor
                _availableTokens = Math.Max(_bytesPerSecondLimit, 64 * 1024);
                _lastRefillTimestamp = Stopwatch.GetTimestamp();
            }
        }
    }

    public TokenBucketRateLimiter(long bytesPerSecondLimit = 0)
    {
        _bytesPerSecondLimit = Math.Max(0, bytesPerSecondLimit);
        _tickFrequency = (double)Stopwatch.Frequency;
        _availableTokens = _bytesPerSecondLimit > 0 ? _bytesPerSecondLimit : 0;
        _lastRefillTimestamp = Stopwatch.GetTimestamp();
    }

    public async ValueTask ConsumeAsync(int byteCount, CancellationToken cancellationToken = default)
    {
        long limit = Interlocked.Read(ref _bytesPerSecondLimit);
        if (limit <= 0 || byteCount <= 0)
        {
            return; // Unlimited bandwidth mode - zero overhead
        }

        double delayMs = 0;

        lock (_syncLock)
        {
            limit = _bytesPerSecondLimit;
            if (limit <= 0) return;

            long now = Stopwatch.GetTimestamp();
            double elapsedSeconds = (now - _lastRefillTimestamp) / _tickFrequency;
            _lastRefillTimestamp = now;

            // Refill tokens based on elapsed time
            double maxTokens = Math.Max(limit, (double)byteCount);
            _availableTokens = Math.Min(maxTokens, _availableTokens + (elapsedSeconds * limit));

            // Subtract requested bytes
            _availableTokens -= byteCount;

            // If token deficit exists, calculate wait time
            if (_availableTokens < 0)
            {
                double neededTokens = -_availableTokens;
                delayMs = (neededTokens / limit) * 1000.0;
            }
        }

        if (delayMs > 0.5)
        {
            // Yield execution until token bucket replenishes
            await Task.Delay((int)Math.Ceiling(delayMs), cancellationToken).ConfigureAwait(false);
        }
    }
}
