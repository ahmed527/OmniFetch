using OmniFetch.Core.Common;
using OmniFetch.Core.Models;

namespace OmniFetch.Core.Persistence;

/// <summary>
/// Repository interface for download job and segment state persistence.
/// </summary>
public interface IDownloadRepository
{
    /// <summary>
    /// Persists a newly created download job with its initial segments.
    /// </summary>
    Task<DownloadJobInfo> AddJobAsync(DownloadJobInfo jobInfo, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a download job with all associated segments by ID.
    /// </summary>
    Task<DownloadJobInfo?> GetJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all persisted download jobs, optionally filtered by status.
    /// </summary>
    Task<IReadOnlyList<DownloadJobInfo>> GetAllJobsAsync(DownloadStatus? statusFilter = null, CancellationToken ct = default);

    /// <summary>
    /// Updates the status and completion timestamp of a job.
    /// </summary>
    Task UpdateJobStatusAsync(Guid jobId, DownloadStatus status, DateTime? completedAt = null, CancellationToken ct = default);

    /// <summary>
    /// Hot-swaps the download URL and cookies for an expired link while keeping segment progress intact.
    /// </summary>
    Task UpdateJobUrlAsync(Guid jobId, string newUrl, string? cookies = null, CancellationToken ct = default);

    /// <summary>
    /// Updates the destination file path of a download job.
    /// </summary>
    Task UpdateJobDestinationAsync(Guid jobId, string newDestinationFilePath, CancellationToken ct = default);

    /// <summary>
    /// Synchronizes the complete segment topology for a job (e.g. after bisection or initial setup).
    /// </summary>
    Task SaveSegmentsAsync(Guid jobId, IEnumerable<DownloadSegmentState> segments, CancellationToken ct = default);

    /// <summary>
    /// Updates in-place the write pointer (CurrentByte) for existing segments.
    /// Used by the write-behind flusher for high-speed batch commits.
    /// </summary>
    Task UpdateSegmentProgressAsync(Guid jobId, IEnumerable<DownloadSegmentState> segments, CancellationToken ct = default);

    /// <summary>
    /// Deletes a download job record and all its segments. Optionally deletes the physical file from disk.
    /// </summary>
    Task DeleteJobAsync(Guid jobId, bool deleteFileFromDisk = false, CancellationToken ct = default);
}
