using OmniFetch.Core.Common;

namespace OmniFetch.Core.Persistence.Entities;

/// <summary>
/// SQLite entity model representing a download job record.
/// Corresponds to Blueprint §3.1 specification.
/// </summary>
public class DownloadJobEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = string.Empty;
    public string DestinationFilePath { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    public string? Referrer { get; set; }
    public string? ContentType { get; set; }
    public bool SupportsRange { get; set; } = true;
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Navigation collection of segment records belonging to this download job.
    /// </summary>
    public ICollection<DownloadSegmentEntity> Segments { get; set; } = [];
}
