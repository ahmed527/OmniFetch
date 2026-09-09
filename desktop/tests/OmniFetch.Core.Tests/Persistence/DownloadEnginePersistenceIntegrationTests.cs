using System.Security.Cryptography;
using OmniFetch.Core.Common;
using OmniFetch.Core.Engine;
using OmniFetch.Core.Exceptions;
using OmniFetch.Core.Models;
using OmniFetch.Core.Network;
using OmniFetch.Core.Persistence;
using OmniFetch.Core.Storage;
using OmniFetch.Core.Tests.TestHelpers;

namespace OmniFetch.Core.Tests.Persistence;

public class DownloadEnginePersistenceIntegrationTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _tempDbPath;
    private readonly DownloadRepository _repository;
    private readonly WriteBehindService _writeBehindService;

    public DownloadEnginePersistenceIntegrationTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "OmniFetchPersist_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);

        _tempDbPath = Path.Combine(_tempDirectory, "omnifetch.db");
        var options = OmniFetchDbContext.CreateOptions(_tempDbPath);
        _repository = new DownloadRepository(options);
        _writeBehindService = new WriteBehindService(_repository, flushIntervalMs: 100);
    }

    public void Dispose()
    {
        try
        {
            _writeBehindService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch { }

        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task DownloadEngine_WithRepository_CreatesJobInDatabase()
    {
        // Arrange
        byte[] payload = new byte[100_000];
        var handler = new MockHttpMessageHandler(payload, supportsRanges: true, filename: "doc.pdf");
        using var httpClient = new HttpClient(handler);
        var probeService = new HttpProbeService(httpClient);
        var diskStorage = new DiskStorageService();

        var engine = new DownloadEngine(probeService, diskStorage, httpClient, _repository, _writeBehindService);
        string destinationFile = Path.Combine(_tempDirectory, "doc.pdf");

        // Act
        var job = await engine.CreateJobAsync("https://example.com/doc.pdf", destinationFile);

        // Assert
        Assert.Equal(DownloadStatus.Queued, job.Status);
        var persistedJob = await _repository.GetJobAsync(job.Id);
        Assert.NotNull(persistedJob);
        Assert.Equal(job.Id, persistedJob.Id);
        Assert.Equal("https://example.com/doc.pdf", persistedJob.Url);
        Assert.Equal(DownloadStatus.Queued, persistedJob.Status);
    }

    [Fact]
    public async Task DownloadEngine_EndToEndDownload_SavesInitialAndCompletedState()
    {
        // Arrange - 2 MB payload
        int fileSize = 2 * 1024 * 1024;
        byte[] expectedPayload = new byte[fileSize];
        new Random(777).NextBytes(expectedPayload);
        byte[] expectedHash = SHA256.HashData(expectedPayload);

        var handler = new MockHttpMessageHandler(expectedPayload, supportsRanges: true, filename: "bundle.zip");
        using var httpClient = new HttpClient(handler);
        var probeService = new HttpProbeService(httpClient);
        var diskStorage = new DiskStorageService();

        var engine = new DownloadEngine(probeService, diskStorage, httpClient, _repository, _writeBehindService);
        string destinationFile = Path.Combine(_tempDirectory, "bundle.zip");
        var options = new DownloadOptions { MaxConnections = 4, MinSplitThresholdBytes = 256 * 1024 };

        // Act
        var job = await engine.StartDownloadAsync("https://example.com/bundle.zip", destinationFile, options);

        // Assert
        Assert.Equal(DownloadStatus.Completed, job.Status);
        byte[] actualData = await File.ReadAllBytesAsync(destinationFile);
        Assert.Equal(expectedHash, SHA256.HashData(actualData));

        // Check SQLite Database
        var persisted = await _repository.GetJobAsync(job.Id);
        Assert.NotNull(persisted);
        Assert.Equal(DownloadStatus.Completed, persisted.Status);
        Assert.NotNull(persisted.CompletedAtUtc);
        Assert.NotEmpty(persisted.Segments);
        Assert.All(persisted.Segments, s => Assert.True(s.CurrentByte > s.EndByte));
    }

    [Fact]
    public async Task DownloadEngine_PauseAndResume_RestoresSegmentsFromSQLite_WithoutByteLoss()
    {
        // Arrange - 2 MB payload with artificial delay so we can pause mid-flight
        int fileSize = 2 * 1024 * 1024;
        byte[] expectedPayload = new byte[fileSize];
        new Random(888).NextBytes(expectedPayload);
        byte[] expectedHash = SHA256.HashData(expectedPayload);

        var handler = new MockHttpMessageHandler(expectedPayload, supportsRanges: true, filename: "paused_file.bin")
        {
            ArtificialDelayPerChunk = TimeSpan.FromMilliseconds(50)
        };
        using var httpClient = new HttpClient(handler);
        var probeService = new HttpProbeService(httpClient);
        var diskStorage = new DiskStorageService();

        var engine = new DownloadEngine(probeService, diskStorage, httpClient, _repository, _writeBehindService);
        string destinationFile = Path.Combine(_tempDirectory, "paused_file.bin");
        var options = new DownloadOptions { MaxConnections = 4, MinSplitThresholdBytes = 128 * 1024 };

        var job = await engine.CreateJobAsync("https://example.com/paused_file.bin", destinationFile, options);

        // Act 1: Start and pause mid-flight after 30ms (first chunk takes 50ms)
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(30);

        try
        {
            await engine.StartDownloadAsync(job, options, cancellationToken: cts.Token);
        }
        catch (DownloadPausedException)
        {
            // Expected
        }

        // Verify paused state in SQLite
        var pausedJob = await _repository.GetJobAsync(job.Id);
        Assert.NotNull(pausedJob);
        Assert.Equal(DownloadStatus.Paused, pausedJob.Status);
        Assert.NotEmpty(pausedJob.Segments);

        // Act 2: Simulate app restart by instantiating a FRESH engine connected to the same DB
        handler.ArtificialDelayPerChunk = TimeSpan.Zero; // Normal speed for resume
        var resumedEngine = new DownloadEngine(probeService, diskStorage, httpClient, _repository, _writeBehindService);

        // Resume using the JobId-only overload, which reloads from repository
        var completedJob = await resumedEngine.ResumeDownloadAsync(job.Id, options);

        // Assert
        Assert.Equal(DownloadStatus.Completed, completedJob.Status);

        // Cryptographic bit-exact verification
        byte[] downloadedData = await File.ReadAllBytesAsync(destinationFile);
        byte[] downloadedHash = SHA256.HashData(downloadedData);
        Assert.Equal(expectedHash, downloadedHash);

        // Check DB completed state
        var dbFinal = await _repository.GetJobAsync(job.Id);
        Assert.NotNull(dbFinal);
        Assert.Equal(DownloadStatus.Completed, dbFinal.Status);
    }

    [Fact]
    public async Task DownloadEngine_ExpiredUrl_SavesExpiredStatus_AndResumesAfterUpdateUrl()
    {
        // Arrange
        int fileSize = 1024 * 1024;
        byte[] expectedPayload = new byte[fileSize];
        new Random(999).NextBytes(expectedPayload);
        byte[] expectedHash = SHA256.HashData(expectedPayload);

        var handler = new MockHttpMessageHandler(expectedPayload, supportsRanges: true, filename: "cloud_file.bin");
        using var httpClient = new HttpClient(handler);
        var probeService = new HttpProbeService(httpClient);
        var diskStorage = new DiskStorageService();

        var engine = new DownloadEngine(probeService, diskStorage, httpClient, _repository, _writeBehindService);
        string destinationFile = Path.Combine(_tempDirectory, "cloud_file.bin");

        var job = await engine.CreateJobAsync("https://cloud.example.com/presigned?token=valid", destinationFile);

        // Now simulate link expiration before download starts
        handler.SimulateExpiredLink = true;

        await Assert.ThrowsAsync<ExpiredUrlException>(async () =>
        {
            await engine.StartDownloadAsync(job);
        });

        // Verify DB status is Expired
        var dbJob = await _repository.GetJobAsync(job.Id);
        Assert.NotNull(dbJob);
        Assert.Equal(DownloadStatus.Expired, dbJob.Status);

        // User obtains a refreshed URL
        string refreshedUrl = "https://cloud.example.com/presigned?token=refreshed_abc";
        await _repository.UpdateJobUrlAsync(job.Id, refreshedUrl, cookies: "fresh_session=1");

        // DB status should now be Paused
        dbJob = await _repository.GetJobAsync(job.Id);
        Assert.NotNull(dbJob);
        Assert.Equal(DownloadStatus.Paused, dbJob.Status);
        Assert.Equal(refreshedUrl, dbJob.Url);

        // Unblock mock server
        handler.SimulateExpiredLink = false;

        // Resume download
        var finishedJob = await engine.ResumeDownloadAsync(job.Id);
        Assert.Equal(DownloadStatus.Completed, finishedJob.Status);

        byte[] actualBytes = await File.ReadAllBytesAsync(destinationFile);
        Assert.Equal(expectedHash, SHA256.HashData(actualBytes));

        // Verify that the hot-swapped cookie header was transmitted in resumed requests
        var resumedGetRequests = handler.RecordedRequests
            .Where(r => r.Method == HttpMethod.Get && r.RequestUri?.ToString() == refreshedUrl)
            .ToList();
        Assert.NotEmpty(resumedGetRequests);
        Assert.All(resumedGetRequests, req =>
        {
            Assert.True(req.Headers.Contains("Cookie"));
            Assert.Contains("fresh_session=1", req.Headers.GetValues("Cookie").First());
        });
    }
}
