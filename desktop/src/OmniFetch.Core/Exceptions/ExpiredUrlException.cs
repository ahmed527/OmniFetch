using System.Net;

namespace OmniFetch.Core.Exceptions;

/// <summary>
/// Thrown when a remote host rejects an HTTP range request with HTTP 401 Unauthorized,
/// 403 Forbidden, or 410 Gone during resumption, indicating an expiring URL signature.
/// Retains the downloaded offset and job context so the link can be hot-swapped without byte loss.
/// </summary>
public class ExpiredUrlException : OmniFetchException
{
    public HttpStatusCode StatusCode { get; }
    public string ExpiredUrl { get; }
    public long DownloadedBytesSoFar { get; }
    public string? Referrer { get; }

    public ExpiredUrlException(string expiredUrl, HttpStatusCode statusCode, long downloadedBytesSoFar, string? referrer = null)
        : base($"The remote download URL has expired or was rejected with HTTP {(int)statusCode} ({statusCode}). Expired URL: {expiredUrl}")
    {
        ExpiredUrl = expiredUrl;
        StatusCode = statusCode;
        DownloadedBytesSoFar = downloadedBytesSoFar;
        Referrer = referrer;
    }
}
