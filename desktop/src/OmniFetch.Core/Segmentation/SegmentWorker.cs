using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;
using OmniFetch.Core.Common;
using OmniFetch.Core.Exceptions;
using OmniFetch.Core.Models;
using OmniFetch.Core.Network;
using OmniFetch.Core.Storage;

namespace OmniFetch.Core.Segmentation;

/// <summary>
/// Executes high-throughput streaming for an individual byte range directly to disk.
/// Implements zero-allocation buffer recycling, lock-free RandomAccess writing,
/// and dynamic boundary clamp detection.
/// </summary>
public class SegmentWorker
{
    private readonly HttpClient _httpClient;
    private readonly IDiskStorageService _diskStorage;
    private readonly ITokenBucketRateLimiter _rateLimiter;
    private readonly DownloadOptions _options;

    public SegmentWorker(
        HttpClient httpClient,
        IDiskStorageService diskStorage,
        ITokenBucketRateLimiter rateLimiter,
        DownloadOptions options)
    {
        _httpClient = httpClient;
        _diskStorage = diskStorage;
        _rateLimiter = rateLimiter;
        _options = options;
    }

    /// <summary>
    /// Downloads the assigned segment directly into the destination file.
    /// Handles boundary clamping during dynamic bisection, transient retries, and token-bucket throttling.
    /// </summary>
    public async Task ExecuteAsync(
        SafeFileHandle fileHandle,
        string url,
        DownloadSegmentState segment,
        string? etag,
        CancellationToken cancellationToken)
    {
        int retryCount = 0;
        int maxRetries = Math.Max(0, _options.MaxRetriesPerSegment);

        while (true)
        {
            try
            {
                await DownloadLoopAsync(fileHandle, url, segment, etag, cancellationToken).ConfigureAwait(false);
                return; // Completed successfully or bisected endpoint reached
            }
            catch (OperationCanceledException)
            {
                throw; // Clean cancellation requested
            }
            catch (ExpiredUrlException)
            {
                throw; // Expired URL requires user / extension refresh
            }
            catch (Exception ex)
            {
                retryCount++;
                if (retryCount > maxRetries || cancellationToken.IsCancellationRequested)
                {
                    throw new OmniFetchException($"Segment {segment.SegmentIndex} failed after {retryCount} attempts: {ex.Message}", ex);
                }

                // Exponential backoff before retry (500ms, 1000ms, 2000ms...)
                int backoffMs = (int)(Math.Pow(2, retryCount - 1) * 500);
                await Task.Delay(backoffMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task DownloadLoopAsync(
        SafeFileHandle fileHandle,
        string url,
        DownloadSegmentState segment,
        string? etag,
        CancellationToken cancellationToken)
    {
        var (currentOffset, endOffset, _) = segment.GetProgress();

        // Check if segment has already reached or exceeded its end boundary
        if (endOffset >= 0 && currentOffset > endOffset)
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Apply Range header if range is bounded or start offset > 0
        if (endOffset >= 0)
        {
            request.Headers.Range = new RangeHeaderValue(currentOffset, endOffset);
        }
        else if (currentOffset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(currentOffset, null);
        }

        // Cache validation header for safe resumption
        if (!string.IsNullOrWhiteSpace(etag) && _options.ValidateETagOnResume)
        {
            request.Headers.IfRange = new RangeConditionHeaderValue(etag.StartsWith('\"') ? etag : $"\"{etag}\"");
        }

        // Custom headers & cookies
        ApplyRequestHeaders(request);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        ).ConfigureAwait(false);

        // Check for expiring cloud links (401 Unauthorized, 403 Forbidden, 410 Gone)
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.Gone)
        {
            throw new ExpiredUrlException(url, response.StatusCode, segment.CurrentByte, _options.Referrer);
        }

        // Prevent silent data corruption: if range was requested, verify server returned HTTP 206 Partial Content
        if ((currentOffset > 0 || endOffset >= 0) && response.StatusCode == HttpStatusCode.OK)
        {
            throw new RangeNotSupportedException($"Server returned HTTP 200 OK for range request [{currentOffset}-{endOffset}]. Range requests are not supported by this endpoint.");
        }

        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        // Zero-allocation buffer renting from ArrayPool
        int bufferSize = Math.Max(4096, _options.BufferSizeBytes);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var (current, clampedEnd, _) = segment.GetProgress();

                // If segment has been bisected and CurrentByte reached clampedEnd, we're done
                if (clampedEnd >= 0 && current > clampedEnd)
                {
                    break;
                }

                // Calculate maximum bytes to read without overshooting clampedEnd
                int maxRead = buffer.Length;
                if (clampedEnd >= 0)
                {
                    long remainingBytes = clampedEnd - current + 1;
                    if (remainingBytes <= 0)
                    {
                        break;
                    }
                    maxRead = (int)Math.Min(buffer.Length, remainingBytes);
                }

                int bytesRead = await responseStream.ReadAsync(
                    buffer.AsMemory(0, maxRead),
                    cancellationToken
                ).ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    // Stream ended naturally
                    if (clampedEnd < 0)
                    {
                        segment.CompleteUnbounded(current);
                    }
                    break;
                }

                // Defensive post-read check: if segment was clamped concurrently while ReadAsync was awaiting
                var (_, latestClampedEnd, _) = segment.GetProgress();
                int validBytes = bytesRead;
                if (latestClampedEnd >= 0 && current + bytesRead - 1 > latestClampedEnd)
                {
                    validBytes = Math.Max(0, (int)(latestClampedEnd - current + 1));
                }

                if (validBytes > 0)
                {
                    // Apply Token Bucket rate-limiting before or during disk write
                    await _rateLimiter.ConsumeAsync(validBytes, cancellationToken).ConfigureAwait(false);

                    // Lock-free direct asynchronous disk write
                    await _diskStorage.WriteAsync(
                        fileHandle,
                        buffer.AsMemory(0, validBytes),
                        current,
                        cancellationToken
                    ).ConfigureAwait(false);

                    // Advance segment tracking pointer atomically
                    segment.Advance(validBytes);
                }

                if (validBytes < bytesRead)
                {
                    // Boundary clamped during in-flight read; complete this segment
                    break;
                }
            }
        }
        finally
        {
            // Guaranteed return of rented buffer to the pool
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ApplyRequestHeaders(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_options.UserAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        }

        if (!string.IsNullOrWhiteSpace(_options.Referrer))
        {
            request.Headers.TryAddWithoutValidation("Referer", _options.Referrer);
        }
        else if (request.RequestUri?.Host.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) == true || request.RequestUri?.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) == true)
        {
            request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
        }

        if (request.RequestUri?.Host.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) == true || request.RequestUri?.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) == true)
        {
            request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
        }

        if (!string.IsNullOrWhiteSpace(_options.Cookies))
        {
            request.Headers.TryAddWithoutValidation("Cookie", _options.Cookies);
        }

        foreach (var header in _options.CustomHeaders)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }
}
