using Microsoft.EntityFrameworkCore;
using OmniFetch.Core.Common;
using OmniFetch.Core.Models;
using OmniFetch.Core.Persistence.Entities;

namespace OmniFetch.Core.Persistence;

/// <summary>
/// Thread-safe Entity Framework Core repository implementation for OmniFetch download metadata.
/// Uses short-lived DbContext instances per operation to prevent concurrency contention.
/// </summary>
public class DownloadRepository : IDownloadRepository
{
    private readonly DbContextOptions<OmniFetchDbContext> _options;

    public DownloadRepository(DbContextOptions<OmniFetchDbContext> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public DownloadRepository(string? databasePath = null)
        : this(OmniFetchDbContext.CreateOptions(databasePath))
    {
    }

    private readonly object _initLock = new();
    private bool _isInitialized;

    private OmniFetchDbContext CreateContext()
    {
        var context = new OmniFetchDbContext(_options);
        if (!_isInitialized)
        {
            lock (_initLock)
            {
                if (!_isInitialized)
                {
                    context.Database.EnsureCreated();
                    _isInitialized = true;
                }
            }
        }
        return context;
    }

    public async Task<DownloadJobInfo> AddJobAsync(DownloadJobInfo jobInfo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jobInfo);

        await using var context = CreateContext();
        var entity = MapToEntity(jobInfo);

        context.Jobs.Add(entity);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);

        return MapToInfo(entity);
    }

    public async Task<DownloadJobInfo?> GetJobAsync(Guid jobId, CancellationToken ct = default)
    {
        await using var context = CreateContext();
        var entity = await context.Jobs
            .AsNoTracking()
            .Include(j => j.Segments)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct)
            .ConfigureAwait(false);

        return entity == null ? null : MapToInfo(entity);
    }

    public async Task<IReadOnlyList<DownloadJobInfo>> GetAllJobsAsync(DownloadStatus? statusFilter = null, CancellationToken ct = default)
    {
        await using var context = CreateContext();
        var query = context.Jobs
            .AsNoTracking()
            .Include(j => j.Segments)
            .AsQueryable();

        if (statusFilter.HasValue)
        {
            query = query.Where(j => j.Status == statusFilter.Value);
        }

        var entities = await query
            .OrderByDescending(j => j.CreatedAtUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return entities.Select(MapToInfo).ToList();
    }

    public async Task UpdateJobStatusAsync(Guid jobId, DownloadStatus status, DateTime? completedAt = null, CancellationToken ct = default)
    {
        await using var context = CreateContext();
        var job = await context.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
        if (job != null)
        {
            job.Status = status;
            if (completedAt.HasValue)
            {
                job.CompletedAtUtc = completedAt.Value;
            }
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task UpdateJobUrlAsync(Guid jobId, string newUrl, string? cookies = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newUrl);

        await using var context = CreateContext();
        var job = await context.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
        if (job != null)
        {
            job.Url = newUrl;
            if (!string.IsNullOrWhiteSpace(cookies))
            {
                job.Cookies = cookies;
            }
            // Transition from Expired to Paused so the user / daemon can resume immediately
            if (job.Status == DownloadStatus.Expired)
            {
                job.Status = DownloadStatus.Paused;
            }
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task UpdateJobDestinationAsync(Guid jobId, string newDestinationFilePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newDestinationFilePath);

        await using var context = CreateContext();
        var job = await context.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
        if (job != null)
        {
            job.DestinationFilePath = newDestinationFilePath;
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task SaveSegmentsAsync(Guid jobId, IEnumerable<DownloadSegmentState> segments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(segments);

        await using var context = CreateContext();
        var existingSegments = await context.Segments
            .Where(s => s.JobId == jobId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var segmentList = segments.ToList();
        var existingMap = existingSegments.ToDictionary(s => s.SegmentIndex);

        foreach (var segState in segmentList)
        {
            if (existingMap.TryGetValue(segState.SegmentIndex, out var existingEntity))
            {
                existingEntity.StartByte = segState.StartByte;
                existingEntity.EndByte = segState.EndByte;
                existingEntity.CurrentByte = segState.CurrentByte;
            }
            else
            {
                context.Segments.Add(new DownloadSegmentEntity
                {
                    Id = Guid.NewGuid(),
                    JobId = jobId,
                    SegmentIndex = segState.SegmentIndex,
                    StartByte = segState.StartByte,
                    EndByte = segState.EndByte,
                    CurrentByte = segState.CurrentByte
                });
            }
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateSegmentProgressAsync(Guid jobId, IEnumerable<DownloadSegmentState> segments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(segments);

        await using var context = CreateContext();
        var existingSegments = await context.Segments
            .Where(s => s.JobId == jobId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var existingMap = existingSegments.ToDictionary(s => s.SegmentIndex);
        bool hasChanges = false;

        foreach (var segState in segments)
        {
            var (currentByte, endByte, _) = segState.GetProgress();
            if (existingMap.TryGetValue(segState.SegmentIndex, out var existingEntity))
            {
                bool segmentChanged = false;
                if (currentByte > existingEntity.CurrentByte)
                {
                    existingEntity.CurrentByte = currentByte;
                    segmentChanged = true;
                }
                if (endByte != existingEntity.EndByte)
                {
                    existingEntity.EndByte = endByte;
                    segmentChanged = true;
                }
                if (segmentChanged)
                {
                    hasChanges = true;
                }
            }
            else
            {
                // Newly bisected segment discovered during flush
                context.Segments.Add(new DownloadSegmentEntity
                {
                    Id = Guid.NewGuid(),
                    JobId = jobId,
                    SegmentIndex = segState.SegmentIndex,
                    StartByte = segState.StartByte,
                    EndByte = endByte,
                    CurrentByte = currentByte
                });
                hasChanges = true;
            }
        }

        if (hasChanges)
        {
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task DeleteJobAsync(Guid jobId, bool deleteFileFromDisk = false, CancellationToken ct = default)
    {
        await using var context = CreateContext();
        var job = await context.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
        if (job != null)
        {
            string destinationPath = job.DestinationFilePath;
            context.Jobs.Remove(job);
            await context.SaveChangesAsync(ct).ConfigureAwait(false);

            if (deleteFileFromDisk && !string.IsNullOrWhiteSpace(destinationPath) && File.Exists(destinationPath))
            {
                try
                {
                    File.Delete(destinationPath);
                }
                catch
                {
                    // Best-effort file deletion
                }
            }
        }
    }

    public static DownloadJobInfo MapToInfo(DownloadJobEntity entity)
    {
        return new DownloadJobInfo
        {
            Id = entity.Id,
            Url = entity.Url,
            DestinationFilePath = entity.DestinationFilePath,
            TotalBytes = entity.TotalBytes,
            ETag = entity.ETag,
            LastModified = DateTimeOffset.TryParse(entity.LastModified, out var dt) ? dt : null,
            Cookies = entity.Cookies,
            UserAgent = entity.UserAgent,
            Referrer = entity.Referrer,
            ContentType = entity.ContentType,
            SupportsRange = entity.SupportsRange,
            Status = entity.Status,
            CreatedAtUtc = entity.CreatedAtUtc,
            CompletedAtUtc = entity.CompletedAtUtc,
            Segments = entity.Segments
                .OrderBy(s => s.SegmentIndex)
                .Select(s => new DownloadSegmentState(s.StartByte, s.EndByte, s.CurrentByte)
                {
                    Id = s.Id,
                    JobId = s.JobId,
                    SegmentIndex = s.SegmentIndex
                })
                .ToList()
        };
    }

    public static DownloadJobEntity MapToEntity(DownloadJobInfo info)
    {
        return new DownloadJobEntity
        {
            Id = info.Id,
            Url = info.Url,
            DestinationFilePath = info.DestinationFilePath,
            TotalBytes = info.TotalBytes,
            ETag = info.ETag,
            LastModified = info.LastModified?.ToString("o"),
            Cookies = info.Cookies,
            UserAgent = info.UserAgent,
            Referrer = info.Referrer,
            ContentType = info.ContentType,
            SupportsRange = info.SupportsRange,
            Status = info.Status,
            CreatedAtUtc = info.CreatedAtUtc,
            CompletedAtUtc = info.CompletedAtUtc,
            Segments = info.Segments.Select(s => new DownloadSegmentEntity
            {
                Id = s.Id == Guid.Empty ? Guid.NewGuid() : s.Id,
                JobId = info.Id,
                SegmentIndex = s.SegmentIndex,
                StartByte = s.StartByte,
                EndByte = s.EndByte,
                CurrentByte = s.CurrentByte
            }).ToList()
        };
    }
}
