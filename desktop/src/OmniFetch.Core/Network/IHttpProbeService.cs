using OmniFetch.Core.Models;

namespace OmniFetch.Core.Network;

/// <summary>
/// Probes remote servers to discover byte-range capability, content length, entity tags, and suggested filenames.
/// </summary>
public interface IHttpProbeService
{
    /// <summary>
    /// Executes capability probe using HTTP HEAD with automatic GET Range: bytes=0-0 fallback.
    /// </summary>
    Task<ProbeResult> ProbeAsync(string url, DownloadOptions? options = null, CancellationToken cancellationToken = default);
}
