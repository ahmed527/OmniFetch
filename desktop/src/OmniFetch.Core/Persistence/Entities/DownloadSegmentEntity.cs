namespace OmniFetch.Core.Persistence.Entities;

/// <summary>
/// SQLite entity model representing an individual segment boundary and progress within a download job.
/// Corresponds to Blueprint §3.1 specification.
/// </summary>
public class DownloadSegmentEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public int SegmentIndex { get; set; }
    public long StartByte { get; set; }
    public long EndByte { get; set; }
    public long CurrentByte { get; set; }
    public bool IsCompleted => EndByte >= 0 && CurrentByte > EndByte;

    /// <summary>
    /// Navigation reference back to the owning download job.
    /// </summary>
    public DownloadJobEntity Job { get; set; } = null!;
}
