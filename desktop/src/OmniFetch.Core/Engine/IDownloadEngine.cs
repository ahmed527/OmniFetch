using OmniFetch.Core.Models;

namespace OmniFetch.Core.Engine;

/// <summary>
/// Core Download Engine contract for initiating, pausing, resuming, and monitoring downloads.
/// </summary>
public interface IDownloadEngine
{
    /// <summary>
    /// Event raised when download progress or segment telemetry updates (throttled at 4 Hz / 250ms).
    /// </summary>
    event EventHandler<DownloadProgressSnapshot>? ProgressChanged;

    /// <summary>
    /// Event raised when a download job status changes (Queued, Downloading, Paused, Completed, Failed, Expired).
    /// </summary>
    event EventHandler<(Guid JobId, Common.DownloadStatus Status)>? StatusChanged;

    /// <summary>
    /// Event raised when an expiring cloud link returns HTTP 401/403/410, triggering the Refresh Download Address flow.
    /// </summary>
    event EventHandler<Exceptions.ExpiredUrlException>? ExpiredUrlDetected;

    /// <summary>
    /// Probes the remote server and prepares the DownloadJobInfo metadata before starting.
    /// </summary>
    Task<DownloadJobInfo> CreateJobAsync(
        string url,
        string destinationFilePath,
        DownloadOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Initiates a multi-stream download task for an existing or prepared job.
    /// </summary>
    Task<DownloadJobInfo> StartDownloadAsync(
        DownloadJobInfo jobInfo,
        DownloadOptions? options = null,
        IProgress<DownloadProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Initiates a brand new multi-stream download task by URL.
    /// </summary>
    Task<DownloadJobInfo> StartDownloadAsync(
        string url,
        string destinationFilePath,
        DownloadOptions? options = null,
        IProgress<DownloadProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes an interrupted or paused download task from persisted segment offsets without byte loss.
    /// </summary>
    Task<DownloadJobInfo> ResumeDownloadAsync(
        DownloadJobInfo jobInfo,
        DownloadOptions? options = null,
        IProgress<DownloadProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes an interrupted or paused download task by Job ID, reloading its state from the persistent repository.
    /// </summary>
    Task<DownloadJobInfo> ResumeDownloadAsync(
        Guid jobId,
        DownloadOptions? options = null,
        IProgress<DownloadProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default);
}
