using Microsoft.EntityFrameworkCore;
using OmniFetch.Core.Common;
using OmniFetch.Core.Models;
using OmniFetch.Core.Persistence;
using OmniFetch.Core.Segmentation;

namespace OmniFetch.Core.Tests.Persistence;

public class WriteBehindServiceTests : IAsyncDisposable
{
    private readonly string _tempDbPath;
    private readonly DownloadRepository _repository;
    private WriteBehindService? _service;

    public WriteBehindServiceTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"omnifetch_wb_{Guid.NewGuid():N}.db");
        var options = OmniFetchDbContext.CreateOptions(_tempDbPath);
        _repository = new DownloadRepository(options);
    }

    public async ValueTask DisposeAsync()
    {
        if (_service != null)
        {
            await _service.DisposeAsync();
        }

        try
        {
            if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath);
            var wal = _tempDbPath + "-wal";
            if (File.Exists(wal)) File.Delete(wal);
            var shm = _tempDbPath + "-shm";
            if (File.Exists(shm)) File.Delete(shm);
        }
        catch { }
    }

    [Fact]
    public async Task PeriodicFlush_FlushesSegmentProgressToRepository()
    {
        var jobId = Guid.NewGuid();
        var job = new DownloadJobInfo { Id = jobId, Url = "https://example.com/wb1", DestinationFilePath = "/tmp/wb1", TotalBytes = 2_000_000, Status = DownloadStatus.Downloading };
        await _repository.AddJobAsync(job);

        var segmentManager = new DynamicSegmentManager();
        segmentManager.InitializeNewJob(jobId, 2_000_000, 2);
        var initialSegments = segmentManager.GetSegments();
        await _repository.SaveSegmentsAsync(jobId, initialSegments);

        // Advance progress in segment 0
        initialSegments[0].Advance(25_000);

        // Flush interval 100ms for fast testing
        _service = new WriteBehindService(_repository, flushIntervalMs: 100);
        _service.RegisterSession(jobId, segmentManager);

        // Wait for at least 2 ticks
        await Task.Delay(350);

        var retrieved = await _repository.GetJobAsync(jobId);
        Assert.NotNull(retrieved);
        var seg0 = retrieved.Segments.First(s => s.SegmentIndex == 0);
        Assert.Equal(25_000, seg0.CurrentByte);
    }

    [Fact]
    public async Task FlushJobAsync_ForcesImmediatePersist()
    {
        var jobId = Guid.NewGuid();
        var job = new DownloadJobInfo { Id = jobId, Url = "https://example.com/wb2", DestinationFilePath = "/tmp/wb2", TotalBytes = 2_000_000, Status = DownloadStatus.Downloading };
        await _repository.AddJobAsync(job);

        var segmentManager = new DynamicSegmentManager();
        segmentManager.InitializeNewJob(jobId, 2_000_000, 2);
        var initialSegments = segmentManager.GetSegments();
        await _repository.SaveSegmentsAsync(jobId, initialSegments);

        // Long timer interval (1 minute) so periodic tick will not fire
        _service = new WriteBehindService(_repository, flushIntervalMs: 60_000);
        _service.RegisterSession(jobId, segmentManager);

        initialSegments[1].Advance(10_000);

        // Force immediate flush
        await _service.FlushJobAsync(jobId);

        var retrieved = await _repository.GetJobAsync(jobId);
        Assert.NotNull(retrieved);
        var seg1 = retrieved.Segments.First(s => s.SegmentIndex == 1);
        Assert.Equal(initialSegments[1].StartByte + 10_000, seg1.CurrentByte);
    }

    [Fact]
    public async Task UnregisterSession_StopsFlushingForJob()
    {
        var jobId = Guid.NewGuid();
        var job = new DownloadJobInfo { Id = jobId, Url = "https://example.com/wb3", DestinationFilePath = "/tmp/wb3", TotalBytes = 2_000_000, Status = DownloadStatus.Downloading };
        await _repository.AddJobAsync(job);

        var segmentManager = new DynamicSegmentManager();
        segmentManager.InitializeNewJob(jobId, 2_000_000, 2);
        var initialSegments = segmentManager.GetSegments();
        await _repository.SaveSegmentsAsync(jobId, initialSegments);

        _service = new WriteBehindService(_repository, flushIntervalMs: 60_000);
        _service.RegisterSession(jobId, segmentManager);

        initialSegments[0].Advance(5_000);
        await _service.FlushJobAsync(jobId);

        // Unregister session
        _service.UnregisterSession(jobId);

        // Advance further
        initialSegments[0].Advance(10_000);

        // Call FlushAllAsync
        await _service.FlushAllAsync();

        // Database should still have 5,000 because session was unregistered
        var retrieved = await _repository.GetJobAsync(jobId);
        Assert.NotNull(retrieved);
        var seg0 = retrieved.Segments.First(s => s.SegmentIndex == 0);
        Assert.Equal(5_000, seg0.CurrentByte);
    }
}
