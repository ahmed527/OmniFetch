using Microsoft.EntityFrameworkCore;
using OmniFetch.Core.Common;
using OmniFetch.Core.Models;
using OmniFetch.Core.Persistence;

namespace OmniFetch.Core.Tests.Persistence;

public class DownloadRepositoryTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly DownloadRepository _repository;

    public DownloadRepositoryTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"omnifetch_repo_{Guid.NewGuid():N}.db");
        var options = OmniFetchDbContext.CreateOptions(_tempDbPath);
        _repository = new DownloadRepository(options);
    }

    public void Dispose()
    {
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
    public async Task AddJobAsync_And_GetJobAsync_PersistsAndRestoresAllFields()
    {
        var job = new DownloadJobInfo
        {
            Id = Guid.NewGuid(),
            Url = "https://cdn.example.com/videos/archive.tar.gz",
            DestinationFilePath = "/Users/test/Downloads/archive.tar.gz",
            TotalBytes = 5_000_000_000,
            ETag = "\"3f82a-59281a\"",
            ContentType = "application/gzip",
            SupportsRange = true,
            Cookies = "session_token=abc123xyz",
            UserAgent = "OmniFetch/1.0",
            Referrer = "https://example.com/portal",
            Status = DownloadStatus.Queued,
            CreatedAtUtc = DateTime.UtcNow
        };

        var added = await _repository.AddJobAsync(job);
        Assert.Equal(job.Id, added.Id);

        var retrieved = await _repository.GetJobAsync(job.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(job.Id, retrieved.Id);
        Assert.Equal(job.Url, retrieved.Url);
        Assert.Equal(job.DestinationFilePath, retrieved.DestinationFilePath);
        Assert.Equal(job.TotalBytes, retrieved.TotalBytes);
        Assert.Equal(job.ETag, retrieved.ETag);
        Assert.Equal(job.ContentType, retrieved.ContentType);
        Assert.Equal(job.SupportsRange, retrieved.SupportsRange);
        Assert.Equal(job.Cookies, retrieved.Cookies);
        Assert.Equal(job.UserAgent, retrieved.UserAgent);
        Assert.Equal(job.Referrer, retrieved.Referrer);
        Assert.Equal(DownloadStatus.Queued, retrieved.Status);
    }

    [Fact]
    public async Task GetAllJobsAsync_FiltersByStatus_AndOrdersByDateDescending()
    {
        var job1 = new DownloadJobInfo { Id = Guid.NewGuid(), Url = "https://example.com/1", DestinationFilePath = "/tmp/1", Status = DownloadStatus.Downloading, CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5) };
        var job2 = new DownloadJobInfo { Id = Guid.NewGuid(), Url = "https://example.com/2", DestinationFilePath = "/tmp/2", Status = DownloadStatus.Paused, CreatedAtUtc = DateTime.UtcNow.AddMinutes(-3) };
        var job3 = new DownloadJobInfo { Id = Guid.NewGuid(), Url = "https://example.com/3", DestinationFilePath = "/tmp/3", Status = DownloadStatus.Paused, CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1) };

        await _repository.AddJobAsync(job1);
        await _repository.AddJobAsync(job2);
        await _repository.AddJobAsync(job3);

        var allJobs = await _repository.GetAllJobsAsync();
        Assert.Equal(3, allJobs.Count);

        var pausedJobs = await _repository.GetAllJobsAsync(statusFilter: DownloadStatus.Paused);
        Assert.Equal(2, pausedJobs.Count);
        Assert.Equal(job3.Id, pausedJobs[0].Id); // Most recent first
        Assert.Equal(job2.Id, pausedJobs[1].Id);
    }

    [Fact]
    public async Task UpdateJobStatusAsync_UpdatesStatusAndCompletedTimestamp()
    {
        var job = new DownloadJobInfo { Id = Guid.NewGuid(), Url = "https://example.com/app", DestinationFilePath = "/tmp/app", Status = DownloadStatus.Downloading };
        await _repository.AddJobAsync(job);

        var completedTime = DateTime.UtcNow;
        await _repository.UpdateJobStatusAsync(job.Id, DownloadStatus.Completed, completedTime);

        var retrieved = await _repository.GetJobAsync(job.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(DownloadStatus.Completed, retrieved.Status);
        Assert.NotNull(retrieved.CompletedAtUtc);
    }

    [Fact]
    public async Task UpdateJobUrlAsync_HotSwapsUrl_AndTransitionsFromExpiredToPaused()
    {
        var job = new DownloadJobInfo { Id = Guid.NewGuid(), Url = "https://expired-s3.com/file?token=expired", DestinationFilePath = "/tmp/file", Status = DownloadStatus.Expired };
        await _repository.AddJobAsync(job);

        string freshUrl = "https://expired-s3.com/file?token=fresh-token-123";
        await _repository.UpdateJobUrlAsync(job.Id, freshUrl, cookies: "new_cookie=value");

        var retrieved = await _repository.GetJobAsync(job.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(freshUrl, retrieved.Url);
        Assert.Equal("new_cookie=value", retrieved.Cookies);
        Assert.Equal(DownloadStatus.Paused, retrieved.Status); // Should transition to Paused so user can resume
    }

    [Fact]
    public async Task SaveSegmentsAsync_InsertsAndUpdatesSegmentBounds()
    {
        var jobId = Guid.NewGuid();
        var job = new DownloadJobInfo { Id = jobId, Url = "https://example.com/data", DestinationFilePath = "/tmp/data", TotalBytes = 1000, Status = DownloadStatus.Downloading };
        await _repository.AddJobAsync(job);

        var seg0 = new DownloadSegmentState(0, 499, 100) { SegmentIndex = 0, JobId = jobId };
        var seg1 = new DownloadSegmentState(500, 999, 550) { SegmentIndex = 1, JobId = jobId };
        var initialSegments = new List<DownloadSegmentState> { seg0, seg1 };

        await _repository.SaveSegmentsAsync(jobId, initialSegments);

        var retrieved = await _repository.GetJobAsync(jobId);
        Assert.NotNull(retrieved);
        Assert.Equal(2, retrieved.Segments.Count);
        Assert.Equal(100, retrieved.Segments[0].CurrentByte);

        // Modify bounds (e.g. bisection) and update
        seg0.TryClampEndByte(249);
        var seg2 = new DownloadSegmentState(250, 499, 250) { SegmentIndex = 2, JobId = jobId };
        initialSegments.Add(seg2);

        await _repository.SaveSegmentsAsync(jobId, initialSegments);

        retrieved = await _repository.GetJobAsync(jobId);
        Assert.NotNull(retrieved);
        Assert.Equal(3, retrieved.Segments.Count);
        var resSeg0 = retrieved.Segments.First(s => s.SegmentIndex == 0);
        var resSeg2 = retrieved.Segments.First(s => s.SegmentIndex == 2);
        Assert.Equal(249, resSeg0.EndByte);
        Assert.Equal(250, resSeg2.StartByte);
    }

    [Fact]
    public async Task UpdateSegmentProgressAsync_UpdatesCurrentByteFast_AndInsertsBisectedSegments()
    {
        var jobId = Guid.NewGuid();
        var job = new DownloadJobInfo { Id = jobId, Url = "https://example.com/stream", DestinationFilePath = "/tmp/stream", TotalBytes = 2000, Status = DownloadStatus.Downloading };
        await _repository.AddJobAsync(job);

        var seg0 = new DownloadSegmentState(0, 999) { SegmentIndex = 0, JobId = jobId };
        var seg1 = new DownloadSegmentState(1000, 1999) { SegmentIndex = 1, JobId = jobId };
        await _repository.SaveSegmentsAsync(jobId, new[] { seg0, seg1 });

        // Simulate write-behind progress advance + dynamic bisection of seg0
        seg0.Advance(300);
        seg0.TryClampEndByte(499);
        var seg2 = new DownloadSegmentState(500, 999) { SegmentIndex = 2, JobId = jobId };

        await _repository.UpdateSegmentProgressAsync(jobId, new[] { seg0, seg1, seg2 });

        var retrieved = await _repository.GetJobAsync(jobId);
        Assert.NotNull(retrieved);
        Assert.Equal(3, retrieved.Segments.Count);
        var retrieved0 = retrieved.Segments.First(s => s.SegmentIndex == 0);
        var retrieved2 = retrieved.Segments.First(s => s.SegmentIndex == 2);
        Assert.Equal(300, retrieved0.CurrentByte);
        Assert.Equal(499, retrieved0.EndByte);
        Assert.Equal(500, retrieved2.StartByte);
    }

    [Fact]
    public async Task DeleteJobAsync_DeletesJobAndCascadeSegments_AndOptionallyDeletesFile()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"omnifetch_del_{Guid.NewGuid():N}.bin");
        await File.WriteAllTextAsync(tempFile, "temporary content");
        Assert.True(File.Exists(tempFile));

        var jobId = Guid.NewGuid();
        var job = new DownloadJobInfo { Id = jobId, Url = "https://example.com/del", DestinationFilePath = tempFile, Status = DownloadStatus.Completed };
        await _repository.AddJobAsync(job);

        var seg = new DownloadSegmentState(0, 100, 100) { SegmentIndex = 0, JobId = jobId };
        await _repository.SaveSegmentsAsync(jobId, new[] { seg });

        // Delete with deleteFileFromDisk = true
        await _repository.DeleteJobAsync(jobId, deleteFileFromDisk: true);

        var retrieved = await _repository.GetJobAsync(jobId);
        Assert.Null(retrieved);
        Assert.False(File.Exists(tempFile));
    }

    [Fact]
    public async Task ConcurrentUpdates_AcrossMultipleJobs_ExecutesWithoutLockingContention()
    {
        int jobCount = 5;
        var jobs = new List<DownloadJobInfo>();
        for (int i = 0; i < jobCount; i++)
        {
            var j = new DownloadJobInfo
            {
                Id = Guid.NewGuid(),
                Url = $"https://example.com/job_{i}",
                DestinationFilePath = $"/tmp/job_{i}.bin",
                TotalBytes = 1_000_000,
                Status = DownloadStatus.Downloading
            };
            await _repository.AddJobAsync(j);

            var initialSegs = new List<DownloadSegmentState>
            {
                new(0, 499_999, 0) { SegmentIndex = 0, JobId = j.Id },
                new(500_000, 999_999, 500_000) { SegmentIndex = 1, JobId = j.Id }
            };
            await _repository.SaveSegmentsAsync(j.Id, initialSegs);
            jobs.Add(j);
        }

        // Run 50 concurrent updates across the jobs
        var tasks = Enumerable.Range(0, 50).Select(async iteration =>
        {
            var targetJob = jobs[iteration % jobCount];
            var segs = new List<DownloadSegmentState>
            {
                new(0, 499_999, (iteration + 1) * 1000) { SegmentIndex = 0, JobId = targetJob.Id },
                new(500_000, 999_999, 500_000 + (iteration + 1) * 1000) { SegmentIndex = 1, JobId = targetJob.Id }
            };
            await _repository.UpdateSegmentProgressAsync(targetJob.Id, segs);
        });

        await Task.WhenAll(tasks);

        // Verify all jobs are intact
        foreach (var j in jobs)
        {
            var persisted = await _repository.GetJobAsync(j.Id);
            Assert.NotNull(persisted);
            Assert.Equal(2, persisted.Segments.Count);
            Assert.True(persisted.Segments[0].CurrentByte > 0);
        }
    }
}
