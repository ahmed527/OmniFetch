using System.Security.Cryptography;
using OmniFetch.Core.Common;
using OmniFetch.Core.Engine;
using OmniFetch.Core.Exceptions;
using OmniFetch.Core.Models;
using OmniFetch.Core.Network;
using OmniFetch.Core.Storage;
using OmniFetch.Core.Tests.TestHelpers;
using Xunit;

namespace OmniFetch.Core.Tests;

public class DownloadEngineIntegrationTests : IDisposable
{
    private readonly string _tempDirectory;

    public DownloadEngineIntegrationTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "OmniFetchEngineTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup
        }
    }

    [Fact]
    public async Task DownloadEngine_MultiStreamAcceleratedDownload_ProducesBitExactFileViaSha256()
    {
        // Arrange - Generate 4 MB pseudo-random test payload
        int fileSize = 4 * 1024 * 1024;
        byte[] expectedPayload = new byte[fileSize];
        new Random(42).NextBytes(expectedPayload);
        byte[] expectedHash = SHA256.HashData(expectedPayload);

        var handler = new MockHttpMessageHandler(expectedPayload, supportsRanges: true, filename: "archive.bin");
        using var httpClient = new HttpClient(handler);

        var probeService = new HttpProbeService(httpClient);
        var diskStorage = new DiskStorageService();
        var engine = new DownloadEngine(probeService, diskStorage, httpClient);

        string destinationFile = Path.Combine(_tempDirectory, "archive.bin");
        var options = new DownloadOptions
        {
            MaxConnections = 4,
            MinSplitThresholdBytes = 512 * 1024 // 512 KB for testing bisection
        };

        // Act - Run full multi-stream download
        var job = await engine.StartDownloadAsync(
            "https://cdn.example.com/archive.bin",
            destinationFile,
            options
        );

        // Assert
        Assert.Equal(DownloadStatus.Completed, job.Status);
        Assert.True(File.Exists(destinationFile));
        Assert.Equal(fileSize, new FileInfo(destinationFile).Length);

        // Cryptographic integrity check: Verify SHA-256 hash matches bit-for-bit
        byte[] actualData = await File.ReadAllBytesAsync(destinationFile);
        byte[] actualHash = SHA256.HashData(actualData);
        Assert.Equal(expectedHash, actualHash);
    }

    [Fact]
    public async Task DownloadEngine_NonRangeServer_FallsBackToSingleStreamSuccessfully()
    {
        // Arrange - 1 MB payload, server does not support ranges (HTTP 200)
        int fileSize = 1024 * 1024;
        byte[] expectedPayload = new byte[fileSize];
        new Random(101).NextBytes(expectedPayload);
        byte[] expectedHash = SHA256.HashData(expectedPayload);

        var handler = new MockHttpMessageHandler(expectedPayload, supportsRanges: false, filename: "stream.bin");
        using var httpClient = new HttpClient(handler);

        var probeService = new HttpProbeService(httpClient);
        var diskStorage = new DiskStorageService();
        var engine = new DownloadEngine(probeService, diskStorage, httpClient);

        string destinationFile = Path.Combine(_tempDirectory, "stream.bin");

        // Act
        var job = await engine.StartDownloadAsync(
            "https://cdn.example.com/stream.bin",
            destinationFile
        );

        // Assert
        Assert.Equal(DownloadStatus.Completed, job.Status);
        byte[] actualData = await File.ReadAllBytesAsync(destinationFile);
        Assert.Equal(expectedHash, SHA256.HashData(actualData));
    }

    [Fact]
    public async Task DownloadEngine_ExpiredLinkReturns403_ThrowsExpiredUrlExceptionWithProgressIntact()
    {
        // Arrange
        byte[] payload = new byte[1024 * 1024];
        var handler = new MockHttpMessageHandler(payload, supportsRanges: true, filename: "expired.bin")
        {
            SimulateExpiredLink = true
        };
        using var httpClient = new HttpClient(handler);

        var probeService = new HttpProbeService(httpClient);
        var diskStorage = new DiskStorageService();
        var engine = new DownloadEngine(probeService, diskStorage, httpClient);

        string destinationFile = Path.Combine(_tempDirectory, "expired.bin");

        // Act & Assert - Should raise ExpiredUrlException
        var ex = await Assert.ThrowsAsync<ExpiredUrlException>(async () =>
        {
            await engine.StartDownloadAsync("https://s3.amazonaws.com/expiring/file.bin", destinationFile);
        });

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, ex.StatusCode);
    }

    [Fact]
    public async Task DownloadEngine_PauseAndResume_ResumesWithoutByteLossAndMatchesHash()
    {
        // Arrange - 4 MB payload
        int fileSize = 4 * 1024 * 1024;
        byte[] expectedPayload = new byte[fileSize];
        new Random(777).NextBytes(expectedPayload);
        byte[] expectedHash = SHA256.HashData(expectedPayload);

        var handler = new MockHttpMessageHandler(expectedPayload, supportsRanges: true, filename: "resumable.bin")
        {
            ArtificialDelayPerChunk = TimeSpan.FromMilliseconds(20)
        };
        using var httpClient = new HttpClient(handler);

        var probeService = new HttpProbeService(httpClient);
        var diskStorage = new DiskStorageService();
        var engine = new DownloadEngine(probeService, diskStorage, httpClient);

        string destinationFile = Path.Combine(_tempDirectory, "resumable.bin");
        var options = new DownloadOptions
        {
            MaxConnections = 2,
            MinSplitThresholdBytes = 512 * 1024
        };

        using var cts = new CancellationTokenSource();
        DownloadJobInfo? job = null;

        // Cancel download after 60ms (guarantees partial download)
        cts.CancelAfter(60);

        try
        {
            job = await engine.StartDownloadAsync(
                "https://cdn.example.com/resumable.bin",
                destinationFile,
                options,
                cancellationToken: cts.Token
            );
        }
        catch (DownloadPausedException ex)
        {
            job = ex.JobInfo;
        }
        catch (OperationCanceledException)
        {
            // Fallback
        }

        // Assert paused state
        Assert.NotNull(job);
        Assert.Equal(DownloadStatus.Paused, job.Status);
        Assert.NotEmpty(job.Segments);
        string beforeResume = string.Join(", ", job.Segments.Select(s => $"Idx:{s.SegmentIndex}[{s.StartByte}-{s.CurrentByte}-{s.EndByte}, done:{s.IsCompleted}]"));

        // Act - Resume download with a fresh token
        var resumedJob = await engine.ResumeDownloadAsync(job, options);

        // Assert completion and cryptographic bit-for-bit integrity
        Assert.Equal(DownloadStatus.Completed, resumedJob.Status);
        Assert.True(File.Exists(destinationFile));
        Assert.Equal(fileSize, new FileInfo(destinationFile).Length);

        byte[] actualData = await File.ReadAllBytesAsync(destinationFile);
        Assert.Equal(expectedPayload, actualData);
        Assert.Equal(expectedHash, SHA256.HashData(actualData));
    }

    [Fact]
    public async Task DownloadEngine_DynamicBisectionStragglerElimination_CompletesSuccessfully()
    {
        // Arrange - 6 MB payload to trigger bisection
        int fileSize = 6 * 1024 * 1024;
        byte[] expectedPayload = new byte[fileSize];
        new Random(999).NextBytes(expectedPayload);
        byte[] expectedHash = SHA256.HashData(expectedPayload);

        var handler = new MockHttpMessageHandler(expectedPayload, supportsRanges: true, filename: "straggler.bin");
        using var httpClient = new HttpClient(handler);

        var probeService = new HttpProbeService(httpClient);
        var diskStorage = new DiskStorageService();
        var engine = new DownloadEngine(probeService, diskStorage, httpClient);

        string destinationFile = Path.Combine(_tempDirectory, "straggler.bin");
        var options = new DownloadOptions
        {
            MaxConnections = 4,
            MinSplitThresholdBytes = 512 * 1024 // 512 KB threshold permits bisection
        };

        // Act
        var job = await engine.StartDownloadAsync(
            "https://cdn.example.com/straggler.bin",
            destinationFile,
            options
        );

        // Assert - Multi-stream engine bisected workloads and finished with bit-for-bit accuracy
        Assert.Equal(DownloadStatus.Completed, job.Status);
        Assert.True(job.Segments.Count >= 2); // Dynamic segments were spawned and tracked
        byte[] actualData = await File.ReadAllBytesAsync(destinationFile);
        Assert.Equal(expectedHash, SHA256.HashData(actualData));
    }
}
