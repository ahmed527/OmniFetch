using System.Net;
using System.Net.Http.Headers;

namespace OmniFetch.Core.Tests.TestHelpers;

/// <summary>
/// Configurable in-memory HTTP handler for simulating real-world server responses:
/// range slicing (HTTP 206), non-range servers (HTTP 200), HEAD 405 fallbacks, and 403 expirations.
/// </summary>
public class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly byte[] _payload;
    private readonly bool _supportsRanges;
    private readonly bool _rejectHead;
    private readonly string _etag;
    private readonly string _filename;
    private int _requestCount;

    public int RequestCount => _requestCount;
    public bool SimulateExpiredLink { get; set; }
    public TimeSpan ArtificialDelayPerChunk { get; set; } = TimeSpan.Zero;

    public MockHttpMessageHandler(
        byte[] payload,
        bool supportsRanges = true,
        bool rejectHead = false,
        string etag = "omnifetch-test-etag-123",
        string filename = "test_file.bin")
    {
        _payload = payload;
        _supportsRanges = supportsRanges;
        _rejectHead = rejectHead;
        _etag = etag;
        _filename = filename;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);

        if (SimulateExpiredLink)
        {
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                RequestMessage = request
            };
        }

        // Simulate server rejecting HEAD requests (e.g. S3 / pre-signed URLs)
        if (request.Method == HttpMethod.Head && _rejectHead)
        {
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
            {
                RequestMessage = request
            };
        }

        if (request.Method == HttpMethod.Head)
        {
            var headResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request
            };
            headResponse.Content = new ByteArrayContent([]);
            headResponse.Content.Headers.ContentLength = _payload.Length;
            headResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            headResponse.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileName = $"\"{_filename}\""
            };
            headResponse.Headers.ETag = new EntityTagHeaderValue($"\"{_etag}\"");

            if (_supportsRanges)
            {
                headResponse.Headers.AcceptRanges.Add("bytes");
            }

            return headResponse;
        }

        if (request.Method == HttpMethod.Get)
        {
            if (ArtificialDelayPerChunk > TimeSpan.Zero)
            {
                await Task.Delay(ArtificialDelayPerChunk, cancellationToken).ConfigureAwait(false);
            }

            // Handle Range requests
            if (_supportsRanges && request.Headers.Range != null && request.Headers.Range.Ranges.Count > 0)
            {
                var range = request.Headers.Range.Ranges.First();
                long from = range.From ?? 0;
                long to = range.To ?? (_payload.Length - 1);

                from = Math.Clamp(from, 0, _payload.Length);
                to = Math.Clamp(to, from, _payload.Length - 1);

                int sliceLength = (int)(to - from + 1);
                byte[] slice = new byte[sliceLength];
                Array.Copy(_payload, from, slice, 0, sliceLength);

                HttpContent content = ArtificialDelayPerChunk > TimeSpan.Zero
                    ? new StreamContent(new DelayedStream(slice, ArtificialDelayPerChunk))
                    : new ByteArrayContent(slice);

                var partialResponse = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    RequestMessage = request,
                    Content = content
                };

                partialResponse.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, _payload.Length);
                partialResponse.Content.Headers.ContentLength = sliceLength;
                partialResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                partialResponse.Headers.ETag = new EntityTagHeaderValue($"\"{_etag}\"");

                return partialResponse;
            }

            // Standard GET (non-range or full file)
            HttpContent fullContent = ArtificialDelayPerChunk > TimeSpan.Zero
                ? new StreamContent(new DelayedStream(_payload, ArtificialDelayPerChunk))
                : new ByteArrayContent(_payload);

            var fullResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = fullContent
            };

            fullResponse.Content.Headers.ContentLength = _payload.Length;
            fullResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            fullResponse.Headers.ETag = new EntityTagHeaderValue($"\"{_etag}\"");

            if (_supportsRanges)
            {
                fullResponse.Headers.AcceptRanges.Add("bytes");
            }

            return fullResponse;
        }

        return new HttpResponseMessage(HttpStatusCode.BadRequest) { RequestMessage = request };
    }

    private class DelayedStream : MemoryStream
    {
        private readonly TimeSpan _delay;

        public DelayedStream(byte[] buffer, TimeSpan delay) : base(buffer)
        {
            _delay = delay;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            }
            return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            }
            return await base.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }
    }
}
