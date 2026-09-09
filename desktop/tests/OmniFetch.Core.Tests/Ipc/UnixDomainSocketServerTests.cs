using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OmniFetch.Core.Ipc;
using Xunit;

namespace OmniFetch.Core.Tests.Ipc;

public class UnixDomainSocketServerTests
{
    private static string GetTempSocketPath()
    {
        return $"/tmp/of_srv_{Guid.NewGuid().ToString("N")[..8]}.sock";
    }

    [Fact]
    public async Task Server_StartsAndStops_CleansUpSocketFile()
    {
        string socketPath = GetTempSocketPath();
        var server = new UnixDomainSocketServer(socketPath);

        try
        {
            Assert.False(server.IsRunning);
            Assert.False(File.Exists(socketPath));

            await server.StartAsync();
            Assert.True(server.IsRunning);
            Assert.True(File.Exists(socketPath));

            await server.StopAsync();
            Assert.False(server.IsRunning);
            Assert.False(File.Exists(socketPath));
        }
        finally
        {
            await server.DisposeAsync();
            if (File.Exists(socketPath)) File.Delete(socketPath);
        }
    }

    [Fact]
    public async Task Server_EnforcesStrictPosixPermissions_OnUnix()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;

        string socketPath = GetTempSocketPath();
        await using var server = new UnixDomainSocketServer(socketPath);

        try
        {
            await server.StartAsync();
            var mode = File.GetUnixFileMode(socketPath);

            // Verify user read and user write are set, and other/group write are NOT set
            Assert.True(mode.HasFlag(UnixFileMode.UserRead));
            Assert.True(mode.HasFlag(UnixFileMode.UserWrite));
            Assert.False(mode.HasFlag(UnixFileMode.GroupWrite));
            Assert.False(mode.HasFlag(UnixFileMode.OtherWrite));
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task Server_PingPong_ReturnsOkStatus()
    {
        string socketPath = GetTempSocketPath();
        await using var server = new UnixDomainSocketServer(socketPath);
        await server.StartAsync();

        using var client = new IpcClient(socketPath);
        await client.ConnectAsync();

        var response = await client.PingAsync("TestRunner");
        Assert.NotNull(response);
        Assert.Equal("ok", response.Status);
        Assert.Equal("pong", response.Message);
    }

    [Fact]
    public async Task Server_DownloadRequest_InvokesHandlerAndReturnsAccepted()
    {
        string socketPath = GetTempSocketPath();
        await using var server = new UnixDomainSocketServer(socketPath);

        NativeDownloadRequest? capturedRequest = null;
        var expectedJobId = Guid.NewGuid();

        server.OnDownloadRequested += async (req) =>
        {
            capturedRequest = req;
            await Task.Yield();
            return IpcResponse.Accepted(expectedJobId, req.SuggestedFileName, "Queued for download");
        };

        await server.StartAsync();

        using var client = new IpcClient(socketPath);
        await client.ConnectAsync();

        var request = new NativeDownloadRequest
        {
            Action = "download",
            Url = "https://example.com/file.dmg",
            Referrer = "https://example.com",
            SuggestedFileName = "file.dmg"
        };

        var response = await client.SendRequestAsync(request);

        Assert.NotNull(capturedRequest);
        Assert.Equal("https://example.com/file.dmg", capturedRequest.Url);
        Assert.Equal("file.dmg", capturedRequest.SuggestedFileName);

        Assert.NotNull(response);
        Assert.Equal("accepted", response.Status);
        Assert.Equal(expectedJobId, response.JobId);
        Assert.Equal("file.dmg", response.FileName);
    }

    [Fact]
    public async Task Server_RefreshUrlRequest_InvokesHandlerAndReturnsOk()
    {
        string socketPath = GetTempSocketPath();
        await using var server = new UnixDomainSocketServer(socketPath);

        var targetJobId = Guid.NewGuid();
        NativeRefreshUrlRequest? capturedRefresh = null;

        server.OnUrlRefreshed += async (req) =>
        {
            capturedRefresh = req;
            await Task.Yield();
            return IpcResponse.Ok($"Job {req.JobId} refreshed");
        };

        await server.StartAsync();

        using var client = new IpcClient(socketPath);
        await client.ConnectAsync();

        var refreshReq = new NativeRefreshUrlRequest
        {
            JobId = targetJobId,
            NewUrl = "https://cdn.example.com/fresh-token/file.iso",
            Cookies = "auth=fresh_secret"
        };

        var response = await client.SendRefreshUrlAsync(refreshReq);

        Assert.NotNull(capturedRefresh);
        Assert.Equal(targetJobId, capturedRefresh.JobId);
        Assert.Equal("https://cdn.example.com/fresh-token/file.iso", capturedRefresh.NewUrl);

        Assert.NotNull(response);
        Assert.Equal("ok", response.Status);
    }

    [Fact]
    public async Task Server_UnsupportedAction_ReturnsError()
    {
        string socketPath = GetTempSocketPath();
        await using var server = new UnixDomainSocketServer(socketPath);
        await server.StartAsync();

        using var client = new IpcClient(socketPath);
        await client.ConnectAsync();

        var response = await client.SendRawJsonAsync("{\"action\":\"unknown_command\"}");

        Assert.NotNull(response);
        Assert.Equal("error", response.Status);
        Assert.Equal("UNSUPPORTED_ACTION", response.ErrorCode);
    }

    [Fact]
    public async Task Server_StaleSocketFile_IsCleanedUpOnStartup()
    {
        string socketPath = GetTempSocketPath();

        // Create an orphaned dummy file simulating a crashed process
        await File.WriteAllTextAsync(socketPath, "orphaned socket placeholder");
        Assert.True(File.Exists(socketPath));

        await using var server = new UnixDomainSocketServer(socketPath);
        // Starting should detect that no process is actively listening on this socket, unlink it, and bind cleanly
        await server.StartAsync();

        Assert.True(server.IsRunning);

        using var client = new IpcClient(socketPath);
        await client.ConnectAsync();
        var pong = await client.PingAsync();
        Assert.Equal("ok", pong.Status);
    }

    [Fact]
    public async Task Server_ActiveSocketCollision_ThrowsAndDoesNotDeleteWinningSocket()
    {
        string socketPath = GetTempSocketPath();
        await using var server1 = new UnixDomainSocketServer(socketPath);
        await server1.StartAsync();

        try
        {
            await using var server2 = new UnixDomainSocketServer(socketPath);
            // Attempting to start server2 on the same active socket path must throw
            await Assert.ThrowsAsync<InvalidOperationException>(() => server2.StartAsync());

            // CRITICAL: The losing server must NOT unlink the active server's socket file!
            Assert.True(File.Exists(socketPath));

            // Verify server1 is still healthy and accepting traffic
            using var client = new IpcClient(socketPath);
            await client.ConnectAsync();
            var pong = await client.PingAsync("LivenessCheck");
            Assert.Equal("ok", pong.Status);
        }
        finally
        {
            await server1.StopAsync();
        }
    }

    [Fact]
    public void Server_ExceedingPathLimit_ThrowsArgumentException()
    {
        // 105 chars exceeds macOS 104-byte limit
        string longPath = "/tmp/" + new string('a', 105) + ".sock";
        Assert.Throws<ArgumentException>(() => new UnixDomainSocketServer(longPath));
    }

    [Fact]
    public async Task Server_HighConcurrency_HandlesMultipleClientsSimultaneously()
    {
        string socketPath = GetTempSocketPath();
        await using var server = new UnixDomainSocketServer(socketPath);

        int counter = 0;
        server.OnPing += async (req) =>
        {
            Interlocked.Increment(ref counter);
            await Task.Delay(10); // Simulate light processing
            return IpcResponse.Ok($"pong-{req.Client}");
        };

        await server.StartAsync();

        const int clientCount = 20;
        var tasks = Enumerable.Range(0, clientCount).Select(async i =>
        {
            using var client = new IpcClient(socketPath);
            await client.ConnectAsync();
            var resp = await client.PingAsync($"Client-{i}");
            Assert.Equal("ok", resp.Status);
            Assert.Equal($"pong-Client-{i}", resp.Message);
        });

        await Task.WhenAll(tasks);
        Assert.Equal(clientCount, counter);
    }
}
