using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using OmniFetch.Core.Common;
using OmniFetch.Core.Models;

namespace OmniFetch.Core.Network;

/// <summary>
/// Probes remote servers to discover byte-range slicing capabilities, content length,
/// and metadata before launching multi-stream downloads.
/// </summary>
public partial class HttpProbeService : IHttpProbeService
{
    private readonly HttpClient _httpClient;

    public HttpProbeService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SocketsHttpHandlerFactory.CreateClient();
    }

    public async Task<ProbeResult> ProbeAsync(string url, DownloadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new DownloadOptions();

        // 1. Attempt lightweight HTTP HEAD probe first
        try
        {
            using var headRequest = CreateRequest(HttpMethod.Head, url, options);
            using var headResponse = await _httpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (headResponse.IsSuccessStatusCode)
            {
                var headResult = ParseResponseHeaders(url, headResponse);
                // If HEAD explicitly confirmed both Accept-Ranges: bytes and Content-Length, we have full capability
                if (headResult.ContentLength.HasValue && headResult.SupportsRange)
                {
                    return headResult;
                }
            }
        }
        catch (HttpRequestException)
        {
            // Many servers/CDNs reject HEAD requests; fallback to Range GET probe below
        }

        // 2. Fallback: Probe with lightweight GET Range: bytes=0-0 request
        using var rangeRequest = CreateRequest(HttpMethod.Get, url, options);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 0);

        using var rangeResponse = await _httpClient.SendAsync(rangeRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (rangeResponse.StatusCode == HttpStatusCode.PartialContent) // HTTP 206
        {
            var result = ParseResponseHeaders(url, rangeResponse);
            long? totalLength = null;

            if (rangeResponse.Content.Headers.ContentRange?.HasLength == true)
            {
                totalLength = rangeResponse.Content.Headers.ContentRange.Length;
            }

            return result with
            {
                SupportsRange = true,
                ContentLength = totalLength ?? result.ContentLength
            };
        }
        else if (rangeResponse.IsSuccessStatusCode) // HTTP 200 OK (server ignored Range header)
        {
            var result = ParseResponseHeaders(url, rangeResponse);
            return result with
            {
                SupportsRange = false
            };
        }
        else
        {
            // Server returned an error code (e.g. 401, 403, 404, 410)
            return ParseResponseHeaders(url, rangeResponse);
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, DownloadOptions options)
    {
        var request = new HttpRequestMessage(method, url);

        if (!string.IsNullOrWhiteSpace(options.UserAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);
        }

        if (!string.IsNullOrWhiteSpace(options.Referrer))
        {
            request.Headers.TryAddWithoutValidation("Referer", options.Referrer);
        }
        else if (url.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) || url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
        }

        if (url.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) || url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
        }

        if (!string.IsNullOrWhiteSpace(options.Cookies))
        {
            request.Headers.TryAddWithoutValidation("Cookie", options.Cookies);
        }

        foreach (var header in options.CustomHeaders)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return request;
    }

    private static ProbeResult ParseResponseHeaders(string originalUrl, HttpResponseMessage response)
    {
        string finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? originalUrl;
        var headers = response.Headers;
        var contentHeaders = response.Content.Headers;

        // Check range support
        bool supportsRange = false;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            supportsRange = true;
        }
        else if (headers.AcceptRanges.Contains("bytes"))
        {
            supportsRange = true;
        }

        // Extract content length
        long? contentLength = contentHeaders.ContentLength;
        if (!contentLength.HasValue && contentHeaders.ContentRange?.HasLength == true)
        {
            contentLength = contentHeaders.ContentRange.Length;
        }

        // Extract ETag
        string? etag = headers.ETag?.Tag?.Trim('\"');

        // Extract LastModified
        DateTimeOffset? lastModified = contentHeaders.LastModified;

        // Extract ContentType
        string? contentType = contentHeaders.ContentType?.MediaType;

        // Extract SuggestedFileName
        string? suggestedFileName = ExtractFileName(contentHeaders.ContentDisposition, finalUrl, contentType);

        // Snapshot all response headers
        var allHeaders = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in headers)
        {
            allHeaders[h.Key] = h.Value;
        }
        foreach (var h in contentHeaders)
        {
            allHeaders[h.Key] = h.Value;
        }

        return new ProbeResult
        {
            OriginalUrl = originalUrl,
            FinalUrl = finalUrl,
            StatusCode = response.StatusCode,
            ContentLength = contentLength,
            SupportsRange = supportsRange,
            ETag = etag,
            LastModified = lastModified,
            SuggestedFileName = suggestedFileName,
            ContentType = contentType,
            ResponseHeaders = allHeaders
        };
    }

    private static string ExtractFileName(ContentDispositionHeaderValue? disposition, string url, string? contentType)
    {
        string? rawName = null;

        // 1. Check Content-Disposition filename* (RFC 6266 / RFC 5987 UTF-8 encoding)
        if (disposition != null)
        {
            if (!string.IsNullOrWhiteSpace(disposition.FileNameStar))
            {
                rawName = disposition.FileNameStar;
            }
            else if (!string.IsNullOrWhiteSpace(disposition.FileName))
            {
                rawName = disposition.FileName.Trim('\"');
            }
        }

        // 2. Fallback: Parse from URL path
        if (string.IsNullOrWhiteSpace(rawName))
        {
            try
            {
                var uri = new Uri(url);
                string pathFileName = Path.GetFileName(uri.LocalPath);
                if (!string.IsNullOrWhiteSpace(pathFileName))
                {
                    rawName = Uri.UnescapeDataString(pathFileName);
                }
            }
            catch
            {
                // Ignored
            }
        }

        // 3. Guarantee valid name and accurate extension via MimeTypeMap
        return MimeTypeMap.SanitizeAndEnsureExtension(rawName, contentType, url);
    }
}
