using OmniFetch.Core.Common;

namespace OmniFetch.Core.Models;

/// <summary>
/// Runtime and persistence metadata for a download task.
/// </summary>
public class DownloadJobInfo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = string.Empty;
    public string DestinationFilePath { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    public string? Referrer { get; set; }
    public string? ContentType { get; set; }
    public bool SupportsRange { get; set; } = true;
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public List<DownloadSegmentState> Segments { get; set; } = [];
}
