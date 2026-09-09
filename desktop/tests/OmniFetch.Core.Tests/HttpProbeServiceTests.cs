using System.Net;
using OmniFetch.Core.Models;
using OmniFetch.Core.Network;
using OmniFetch.Core.Tests.TestHelpers;
using Xunit;

namespace OmniFetch.Core.Tests;

public class HttpProbeServiceTests
{
    [Fact]
    public async Task ProbeAsync_StandardServer_DiscoversCapabilitiesViaHead()
    {
        // Arrange
        byte[] payload = new byte[1024 * 1024]; // 1 MB
        var handler = new MockHttpMessageHandler(payload, supportsRanges: true, rejectHead: false, filename: "archive.zip");
        using var client = new HttpClient(handler);
        var probeService = new HttpProbeService(client);

        // Act
        var result = await probeService.ProbeAsync("https://example.com/archive.zip");

        // Assert
        Assert.True(result.SupportsRange);
        Assert.Equal(payload.Length, result.ContentLength);
        Assert.Equal("archive.zip", result.SuggestedFileName);
        Assert.True(result.IsResumableAndSegmentable);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Fact]
    public async Task ProbeAsync_WhenHeadReturns405_FallsBackToGetRangeProbe()
    {
        // Arrange (rejectHead = true simulates S3 / CDN returning 405 on HEAD)
        byte[] payload = new byte[5 * 1024 * 1024]; // 5 MB
        var handler = new MockHttpMessageHandler(payload, supportsRanges: true, rejectHead: true, filename: "cloud_file.dmg");
        using var client = new HttpClient(handler);
        var probeService = new HttpProbeService(client);

        // Act
        var result = await probeService.ProbeAsync("https://s3.amazonaws.com/bucket/cloud_file.dmg");

        // Assert
        Assert.True(result.SupportsRange);
        Assert.Equal(payload.Length, result.ContentLength);
        Assert.True(result.IsResumableAndSegmentable);
        // Handler was invoked for HEAD (which failed with 405) and then GET range fallback
        Assert.True(handler.RequestCount >= 2);
    }

    [Fact]
    public async Task ProbeAsync_NonRangeServer_IdentifiesSingleStreamFallback()
    {
        // Arrange (supportsRanges = false)
        byte[] payload = new byte[500 * 1024];
        var handler = new MockHttpMessageHandler(payload, supportsRanges: false, rejectHead: false, filename: "script.sh");
        using var client = new HttpClient(handler);
        var probeService = new HttpProbeService(client);

        // Act
        var result = await probeService.ProbeAsync("https://example.com/script.sh");

        // Assert
        Assert.False(result.SupportsRange);
        Assert.False(result.IsResumableAndSegmentable);
    }
}
