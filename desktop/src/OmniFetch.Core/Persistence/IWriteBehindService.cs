using OmniFetch.Core.Segmentation;

namespace OmniFetch.Core.Persistence;

/// <summary>
/// Service managing batched, asynchronous SQLite persistence of segment progress.
/// Decouples high-throughput network threads from database disk I/O.
/// Corresponds to Blueprint §3.2 and §5.2 specifications.
/// </summary>
public interface IWriteBehindService : IAsyncDisposable
{
    /// <summary>
    /// Registers an active download session to participate in periodic write-behind progress flushing.
    /// </summary>
    void RegisterSession(Guid jobId, ISegmentManager segmentManager);

    /// <summary>
    /// Unregisters an active download session upon completion, pause, or failure.
    /// </summary>
    void UnregisterSession(Guid jobId);

    /// <summary>
    /// Forces an immediate synchronous progress commit for a specific job (e.g. on pause or completion).
    /// </summary>
    Task FlushJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Forces an immediate commit of all currently active sessions.
    /// </summary>
    Task FlushAllAsync(CancellationToken ct = default);
}
