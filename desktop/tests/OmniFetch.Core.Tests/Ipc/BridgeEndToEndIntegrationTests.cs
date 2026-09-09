using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OmniFetch.Bridge;
using OmniFetch.Core.Common;
using OmniFetch.Core.Engine;
using OmniFetch.Core.Ipc;
using OmniFetch.Core.Models;
using OmniFetch.Core.Network;
using OmniFetch.Core.Persistence;
using OmniFetch.Core.Storage;
using OmniFetch.Core.Tests.TestHelpers;
using Xunit;

namespace OmniFetch.Core.Tests.Ipc;

public class BridgeEndToEndIntegrationTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly string _socketPath;

    public BridgeEndToEndIntegrationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"omnifetch_e2e_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "e2e_state.db");
        _socketPath = $"/tmp/of_e2e_{Guid.NewGuid().ToString("N")[..8]}.sock";
    }

    [Fact]
    public async Task ChromeToBridgeToEngine_EndToEndDownloadDispatch_SucceedsAndPersists()
    {
        // 1. Setup mock HTTP server payload
        byte[] payload = new byte[2 * 1024 * 1024]; // 2 MB
        Random.Shared.NextBytes(payload);

        var mockHandler = new MockHttpMessageHandler(payload, supportsRanges: true, filename: "bundle.dmg");
        var httpClient = new HttpClient(mockHandler);
        var probeService = new HttpProbeService(httpClient);
        var storageService = new DiskStorageService();

        var repo = new DownloadRepository(_dbPath);
        var writeBehind = new WriteBehindService(repo, flushIntervalMs: 100);

        var engine = new DownloadEngine(probeService, storageService, httpClient: httpClient, repository: repo, writeBehindService: writeBehind);

        // 2. Setup IPC Server
        await using var server = new UnixDomainSocketServer(_socketPath);

        server.OnDownloadRequested += async (req) =>
        {
            var options = new DownloadOptions
            {
                Cookies = req.Cookies,
                UserAgent = req.UserAgent,
                Referrer = req.Referrer
            };

            string savePath = Path.Combine(_testDir, req.SuggestedFileName ?? "bundle.dmg");
            var job = await engine.CreateJobAsync(req.Url, savePath, options);
            return IpcResponse.Accepted(job.Id, job.DestinationFilePath, "Download started via IPC");
        };

        await server.StartAsync();

        try
        {
            // 3. Simulate Chrome Extension dispatching payload through the Bridge
            var chromeRequest = new NativeDownloadRequest
            {
                Action = "download",
                Url = "https://cdn.omnifetch.test/release/v1.0/bundle.dmg",
                Referrer = "https://omnifetch.test/download",
                Cookies = "session_token=secret123; tracking_id=987",
                UserAgent = "Chrome/130.0.0.0 (Macintosh; Apple Silicon)",
                SuggestedFileName = "bundle.dmg"
            };

            string requestJson = JsonSerializer.Serialize(chromeRequest, IpcJsonSerializerContext.Default.NativeDownloadRequest);
            byte[] requestBytes = Encoding.UTF8.GetBytes(requestJson);

            // 4. Send via UnixSocketRelay
            byte[] responseBytes = await UnixSocketRelay.RelayAsync(requestBytes, _socketPath);
            string responseJson = Encoding.UTF8.GetString(responseBytes);

            var ipcResponse = JsonSerializer.Deserialize(responseJson, IpcJsonSerializerContext.Default.IpcResponse);
            Assert.NotNull(ipcResponse);
            Assert.Equal("accepted", ipcResponse.Status);
            Assert.NotNull(ipcResponse.JobId);

            Guid jobId = ipcResponse.JobId.Value;

            // 5. Verify database contains the created job with captured metadata
            var jobInDb = await repo.GetJobAsync(jobId);
            Assert.NotNull(jobInDb);
            Assert.Equal(chromeRequest.Url, jobInDb.Url);
            Assert.Equal(chromeRequest.Referrer, jobInDb.Referrer);
            Assert.Equal(chromeRequest.Cookies, jobInDb.Cookies);
            Assert.Equal(chromeRequest.UserAgent, jobInDb.UserAgent);
            Assert.Equal(chromeRequest.SuggestedFileName, Path.GetFileName(jobInDb.DestinationFilePath));
        }
        finally
        {
            await server.StopAsync();
            await writeBehind.DisposeAsync();
        }
    }

    [Fact]
    public async Task ChromeToBridgeToRepository_EndToEndUrlRefresh_Succeeds()
    {
        var repo = new DownloadRepository(_dbPath);

        // Pre-populate an expired job
        var initialJob = new DownloadJobInfo
        {
            Id = Guid.NewGuid(),
            Url = "https://s3.amazonaws.com/expiring/archive.zip?token=expired",
            DestinationFilePath = Path.Combine(_testDir, "archive.zip"),
            TotalBytes = 100_000_000,
            Status = DownloadStatus.Expired
        };
        await repo.AddJobAsync(initialJob);

        // Setup IPC Server
        await using var server = new UnixDomainSocketServer(_socketPath);

        server.OnUrlRefreshed += async (req) =>
        {
            await repo.UpdateJobUrlAsync(req.JobId, req.NewUrl, req.Cookies);
            return IpcResponse.Ok($"Job {req.JobId} refreshed");
        };

        await server.StartAsync();

        try
        {
            // Simulate Chrome Extension detecting refreshed download link
            var refreshReq = new NativeRefreshUrlRequest
            {
                Action = "refresh_url",
                JobId = initialJob.Id,
                NewUrl = "https://s3.amazonaws.com/expiring/archive.zip?token=brand_new_valid_token",
                Cookies = "session=refreshed_auth"
            };

            string requestJson = JsonSerializer.Serialize(refreshReq, IpcJsonSerializerContext.Default.NativeRefreshUrlRequest);
            byte[] responseBytes = await UnixSocketRelay.RelayAsync(Encoding.UTF8.GetBytes(requestJson), _socketPath);

            var ipcResponse = JsonSerializer.Deserialize(responseBytes, IpcJsonSerializerContext.Default.IpcResponse);
            Assert.NotNull(ipcResponse);
            Assert.Equal("ok", ipcResponse.Status);

            // Verify database state updated
            var updatedJob = await repo.GetJobAsync(initialJob.Id);
            Assert.NotNull(updatedJob);
            Assert.Equal("https://s3.amazonaws.com/expiring/archive.zip?token=brand_new_valid_token", updatedJob.Url);
            Assert.Equal("session=refreshed_auth", updatedJob.Cookies);
            Assert.Equal(DownloadStatus.Paused, updatedJob.Status); // URL refresh transitions Expired -> Paused ready to resume
        }
        finally
        {
            await server.StopAsync();
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch
        {
            // Ignore cleanup failure
        }
    }
}
