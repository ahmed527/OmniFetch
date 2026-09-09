namespace OmniFetch.Core.Common;

/// <summary>
/// Architecture constants and tuning defaults for the OmniFetch high-performance engine.
/// </summary>
public static class Constants
{
    /// <summary>
    /// Default buffer size rented from ArrayPool&lt;byte&gt;.Shared (81,920 bytes = 80 KB).
    /// Prevents GC LOH allocation while saturating modern network interfaces.
    /// </summary>
    public const int DefaultBufferSize = 81920;

    /// <summary>
    /// Minimum threshold of remaining unread bytes required to bisect a segment.
    /// Default is 5 MB per blueprint directive §2.3.
    /// </summary>
    public const long DefaultMinSplitThreshold = 5 * 1024 * 1024; // 5 MB

    /// <summary>
    /// Absolute floor below which splitting introduces more socket overhead than speed gains.
    /// Typically 512 KB.
    /// </summary>
    public const long AbsoluteMinSplitThreshold = 512 * 1024; // 512 KB

    /// <summary>
    /// Default number of parallel connection streams per download job.
    /// </summary>
    public const int DefaultMaxConnections = 8;

    /// <summary>
    /// Maximum allowable connections per download job to prevent server rate limiting or bans.
    /// </summary>
    public const int MaxAllowedConnections = 32;

    /// <summary>
    /// Default SocketsHttpHandler connection pool lifetime.
    /// Keeps sockets warm and bypasses TCP handshakes &amp; TLS renegotiation.
    /// </summary>
    public const int DefaultConnectionLifetimeMinutes = 15;

    /// <summary>
    /// SocketsHttpHandler connection idle timeout.
    /// </summary>
    public const int DefaultConnectionIdleTimeoutMinutes = 2;

    /// <summary>
    /// Maximum concurrent connections per host endpoint.
    /// </summary>
    public const int DefaultMaxConnectionsPerServer = 64;

    /// <summary>
    /// Telemetry emission throttle window for UI dispatch (4 Hz = 250ms).
    /// Protects the UI thread from being overwhelmed during gigabit transfers.
    /// </summary>
    public const int DefaultProgressThrottleIntervalMs = 250;

    /// <summary>
    /// SQLite write-behind progress flush frequency (2,000ms).
    /// </summary>
    public const int DefaultWriteBehindFlushIntervalMs = 2000;

    /// <summary>
    /// Default HTTP timeout for probing server capabilities.
    /// </summary>
    public const int DefaultProbeTimeoutSeconds = 15;

    /// <summary>
    /// Default number of retries per segment on transient network drop.
    /// </summary>
    public const int DefaultMaxSegmentRetries = 3;
}
