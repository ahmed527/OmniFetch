using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OmniFetch.Core.Common;
using OmniFetch.Core.Persistence;
using OmniFetch.Core.Persistence.Entities;

namespace OmniFetch.Core.Tests.Persistence;

public class OmniFetchDbContextTests : IDisposable
{
    private readonly string _tempDbPath;

    public OmniFetchDbContextTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"omnifetch_test_{Guid.NewGuid():N}.db");
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
    public async Task DatabaseCreation_AppliesWALMode_AndCreatesTables()
    {
        var options = OmniFetchDbContext.CreateOptions(_tempDbPath);
        await using var context = new OmniFetchDbContext(options);
        
        bool created = await context.Database.EnsureCreatedAsync();
        Assert.True(created);

        // Verify SQLite PRAGMA journal_mode is WAL
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var journalMode = (string?)await cmd.ExecuteScalarAsync();
        Assert.Equal("wal", journalMode, ignoreCase: true);

        // Verify PRAGMA synchronous is 1 (NORMAL)
        cmd.CommandText = "PRAGMA synchronous;";
        var syncMode = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.Equal(1, syncMode);
    }

    [Fact]
    public async Task CascadeDelete_WhenJobDeleted_DeletesAssociatedSegments()
    {
        var options = OmniFetchDbContext.CreateOptions(_tempDbPath);
        await using (var context = new OmniFetchDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();

            var jobId = Guid.NewGuid();
            var job = new DownloadJobEntity
            {
                Id = jobId,
                Url = "https://example.com/file.iso",
                DestinationFilePath = "/tmp/file.iso",
                TotalBytes = 100_000_000,
                Status = DownloadStatus.Downloading,
                SupportsRange = true,
                CreatedAtUtc = DateTime.UtcNow
            };

            var segments = new List<DownloadSegmentEntity>
            {
                new() { Id = Guid.NewGuid(), JobId = jobId, SegmentIndex = 0, StartByte = 0, CurrentByte = 1000, EndByte = 49_999_999 },
                new() { Id = Guid.NewGuid(), JobId = jobId, SegmentIndex = 1, StartByte = 50_000_000, CurrentByte = 50_001_000, EndByte = 99_999_999 }
            };

            context.Jobs.Add(job);
            context.Segments.AddRange(segments);
            await context.SaveChangesAsync();
        }

        // Now delete the job in a fresh context
        await using (var context = new OmniFetchDbContext(options))
        {
            var job = await context.Jobs.Include(j => j.Segments).FirstAsync();
            context.Jobs.Remove(job);
            await context.SaveChangesAsync();
        }

        // Verify segments are deleted as well
        await using (var context = new OmniFetchDbContext(options))
        {
            var jobCount = await context.Jobs.CountAsync();
            var segCount = await context.Segments.CountAsync();

            Assert.Equal(0, jobCount);
            Assert.Equal(0, segCount);
        }
    }
}
