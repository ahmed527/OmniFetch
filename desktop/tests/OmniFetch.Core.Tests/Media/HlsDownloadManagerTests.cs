using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OmniFetch.Core.Common;
using OmniFetch.Core.Media;
using OmniFetch.Core.Models;
using OmniFetch.Core.Persistence;
using OmniFetch.Core.Tests.Persistence;
using Xunit;

namespace OmniFetch.Core.Tests.Media;

public class HlsDownloadManagerTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _tempDbPath;
    private readonly DownloadRepository _repository;

    public HlsDownloadManagerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "OmniFetch_HlsManagerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _tempDbPath = Path.Combine(_tempDirectory, "test.db");
        var options = OmniFetchDbContext.CreateOptions(_tempDbPath);
        using (var ctx = new OmniFetchDbContext(options))
        {
            ctx.Database.EnsureCreated();
        }
        _repository = new DownloadRepository(options);
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath);
        }
        catch { }
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task DownloadHlsStreamAsync_EndToEndCoordination_CompletesSuccessfully()
    {
        string masterUrl = "https://media.example.com/live/master.m3u8";
        string mediaUrl = "https://media.example.com/live/1080p.m3u8";
        string seg0Url = "https://media.example.com/live/seg0.ts";
        string seg1Url = "https://media.example.com/live/seg1.ts";

        string masterContent = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720
            720p.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080
            1080p.m3u8
            """;

        string mediaContent = """
            #EXTM3U
            #EXT-X-TARGETDURATION:6
            #EXTINF:6.0,
            seg0.ts
            #EXTINF:6.0,
            seg1.ts
            #EXT-X-ENDLIST
            """;

        byte[] seg0Data = Encoding.UTF8.GetBytes("VIDEO_PAYLOAD_CHUNK_0_CONTENT");
        byte[] seg1Data = Encoding.UTF8.GetBytes("VIDEO_PAYLOAD_CHUNK_1_CONTENT");

        var handler = new MockHttpMessageHandler(async (request, ct) =>
        {
            string url = request.RequestUri!.ToString();
            if (url == masterUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(masterContent) };
            }
            if (url == mediaUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(mediaContent) };
            }
            if (url == seg0Url)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(seg0Data) };
            }
            if (url == seg1Url)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(seg1Data) };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new HttpClient(handler);
        var parser = new HlsParser();
        var segmentDownloader = new HlsSegmentDownloader(client);
        var videoAssembler = new HlsVideoAssembler();
        var manager = new HlsDownloadManager(parser, segmentDownloader, videoAssembler, client, _repository);

        string destinationFile = Path.Combine(_tempDirectory, "assembled_stream.ts");
        int progressCalls = 0;
        var progress = new Progress<DownloadProgressSnapshot>(_ =>
        {
            Interlocked.Increment(ref progressCalls);
        });

        var job = await manager.DownloadHlsStreamAsync(
            masterUrl,
            destinationFile,
            preferredResolutionHeight: 1080,
            progress: progress);

        Assert.Equal(DownloadStatus.Completed, job.Status);
        Assert.True(File.Exists(destinationFile));

        byte[] finalBytes = await File.ReadAllBytesAsync(destinationFile);
        Assert.Equal(seg0Data.Length + seg1Data.Length, finalBytes.Length);

        // Verify database persistence
        var dbJob = await _repository.GetJobAsync(job.Id);
        Assert.NotNull(dbJob);
        Assert.Equal(DownloadStatus.Completed, dbJob.Status);
        Assert.NotNull(dbJob.CompletedAtUtc);
    }

    private class MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return handlerFunc(request, cancellationToken);
        }
    }
}
