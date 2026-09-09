using System.Diagnostics;
using OmniFetch.Core.Network;
using Xunit;

namespace OmniFetch.Core.Tests;

public class TokenBucketRateLimiterTests
{
    [Fact]
    public async Task ConsumeAsync_Unlimited_ExecutesImmediatelyWithoutDelay()
    {
        // Arrange
        var limiter = new TokenBucketRateLimiter(0); // Unlimited
        var sw = Stopwatch.StartNew();

        // Act - Consume 10 MB in unlimited mode
        for (int i = 0; i < 100; i++)
        {
            await limiter.ConsumeAsync(100 * 1024);
        }
        sw.Stop();

        // Assert - Should execute in under 50 milliseconds
        Assert.True(sw.ElapsedMilliseconds < 50);
    }

    [Fact]
    public async Task ConsumeAsync_Throttled_EnforcesBandwidthCeiling()
    {
        // Arrange: Limit to 200 KB/s
        long limitBytesPerSec = 200 * 1024; // 200 KB/s
        var limiter = new TokenBucketRateLimiter(limitBytesPerSec);

        var sw = Stopwatch.StartNew();

        // Act - Consume 150 KB twice (total 300 KB).
        // Initial bucket has 200 KB. Second chunk requires 100 KB beyond capacity,
        // which should take roughly 0.5s to replenish.
        await limiter.ConsumeAsync(150 * 1024);
        await limiter.ConsumeAsync(150 * 1024);
        sw.Stop();

        // Assert - Should take at least 300ms
        Assert.True(sw.ElapsedMilliseconds >= 300, $"Expected >= 300ms, actual: {sw.ElapsedMilliseconds}ms");
    }
}
