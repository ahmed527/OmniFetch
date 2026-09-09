using System.Net;

namespace OmniFetch.Core.Models;

/// <summary>
/// Result of probing a remote HTTP/HTTPS resource for download capabilities.
/// </summary>
public record ProbeResult
{
    public required string OriginalUrl { get; init; }
    public required string FinalUrl { get; init; }
    public HttpStatusCode StatusCode { get; init; }
    public long? ContentLength { get; init; }
    public bool SupportsRange { get; init; }
    public string? ETag { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public string? SuggestedFileName { get; init; }
    public string? ContentType { get; init; }
    public IReadOnlyDictionary<string, IEnumerable<string>> ResponseHeaders { get; init; } 
        = new Dictionary<string, IEnumerable<string>>();

    /// <summary>
    /// Indicates whether the resource has an identifiable file size and supports range slicing.
    /// </summary>
    public bool IsResumableAndSegmentable => SupportsRange && ContentLength.HasValue && ContentLength.Value > 0;
}
