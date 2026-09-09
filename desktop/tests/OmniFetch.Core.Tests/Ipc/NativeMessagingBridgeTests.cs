using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OmniFetch.Bridge;
using OmniFetch.Core.Ipc;
using Xunit;

namespace OmniFetch.Core.Tests.Ipc;

public class NativeMessagingBridgeTests
{
    private static string GetTempSocketPath()
    {
        return $"/tmp/of_br_{Guid.NewGuid().ToString("N")[..8]}.sock";
    }

    [Fact]
    public async Task NativeProtocol_ReadMessageAsync_ReadsFramedPayloadCorrectly()
    {
        string text = "{\"action\":\"ping\"}";
        byte[] payloadBytes = Encoding.UTF8.GetBytes(text);

        using var ms = new MemoryStream();
        byte[] lengthPrefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, payloadBytes.Length);
        await ms.WriteAsync(lengthPrefix);
        await ms.WriteAsync(payloadBytes);
        ms.Position = 0;

        byte[]? result = await NativeProtocol.ReadMessageAsync(ms);
        Assert.NotNull(result);
        Assert.Equal(text, Encoding.UTF8.GetString(result));
    }

    [Fact]
    public async Task NativeProtocol_ReadMessageAsync_ReturnsNullOnEof()
    {
        using var ms = new MemoryStream();
        byte[]? result = await NativeProtocol.ReadMessageAsync(ms);
        Assert.Null(result);
    }

    [Fact]
    public async Task NativeProtocol_ReadMessageAsync_ThrowsWhenMessageExceedsLimit()
    {
        using var ms = new MemoryStream();
        byte[] lengthPrefix = new byte[4];
        // 2 MB exceeds 1 MB limit
        BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, 2 * 1024 * 1024);
        await ms.WriteAsync(lengthPrefix);
        ms.Position = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeProtocol.ReadMessageAsync(ms));
    }

    [Fact]
    public async Task NativeProtocol_WriteMessageAsync_WritesLengthPrefixAndPayload()
    {
        using var ms = new MemoryStream();
        byte[] payload = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");

        await NativeProtocol.WriteMessageAsync(ms, payload);
        ms.Position = 0;

        byte[] lengthBuffer = new byte[4];
        await ms.ReadExactlyAsync(lengthBuffer);
        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        Assert.Equal(payload.Length, length);

        byte[] readPayload = new byte[length];
        await ms.ReadExactlyAsync(readPayload);
        Assert.Equal("{\"status\":\"ok\"}", Encoding.UTF8.GetString(readPayload));
    }

    [Fact]
    public async Task UnixSocketRelay_ReturnsDaemonOffline_WhenSocketMissing()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.sock");
        byte[] requestBytes = Encoding.UTF8.GetBytes("{\"action\":\"ping\"}");

        byte[] responseBytes = await UnixSocketRelay.RelayAsync(requestBytes, missingPath);
        string responseJson = Encoding.UTF8.GetString(responseBytes);

        Assert.Contains("DAEMON_OFFLINE", responseJson);
    }

    [Fact]
    public async Task UnixSocketRelay_RelaysPayloadToRunningServer_AndReturnsResponse()
    {
        string socketPath = GetTempSocketPath();
        await using var server = new UnixDomainSocketServer(socketPath);

        server.OnPing += async (req) =>
        {
            await Task.Yield();
            return IpcResponse.Ok($"pong-from-server");
        };

        await server.StartAsync();

        try
        {
            string pingJson = "{\"action\":\"ping\",\"client\":\"BridgeTest\"}";
            byte[] responseBytes = await UnixSocketRelay.RelayAsync(Encoding.UTF8.GetBytes(pingJson), socketPath);

            string responseString = Encoding.UTF8.GetString(responseBytes);
            Assert.Contains("ok", responseString);
            Assert.Contains("pong-from-server", responseString);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task NativeProtocol_ReadMessageAsync_ThrowsOnNegativeLength()
    {
        using var ms = new MemoryStream();
        byte[] lengthPrefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, -5);
        await ms.WriteAsync(lengthPrefix);
        ms.Position = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeProtocol.ReadMessageAsync(ms));
    }

    [Fact]
    public async Task NativeProtocol_ReadMessageAsync_ThrowsOnPrematureEofDuringPayload()
    {
        using var ms = new MemoryStream();
        byte[] lengthPrefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, 50); // expects 50 bytes
        await ms.WriteAsync(lengthPrefix);
        await ms.WriteAsync(Encoding.UTF8.GetBytes("only 10")); // only 7 bytes provided
        ms.Position = 0;

        await Assert.ThrowsAsync<EndOfStreamException>(() => NativeProtocol.ReadMessageAsync(ms));
    }

    [Fact]
    public void ManifestRegistrar_GeneratesValidHostPaths()
    {
        var paths = ManifestRegistrar.GetTargetManifestPaths();
        Assert.NotEmpty(paths);
        Assert.Contains(paths, p => p.Contains("Google/Chrome/NativeMessagingHosts/com.omnifetch.bridge.json"));
        Assert.Contains(paths, p => p.Contains("Microsoft Edge/NativeMessagingHosts/com.omnifetch.bridge.json"));
    }

    [Fact]
    public void ManifestRegistrar_Register_DoesNotContainWildcardInAllowedOrigins()
    {
        string tempManifestPath = Path.Combine(Path.GetTempPath(), $"manifest_{Guid.NewGuid():N}.json");
        try
        {
            // Register with custom extension ID
            string extId = "abcdefghijklmnopabcdefghijklmnop";
            var allowedOrigins = new System.Collections.Generic.List<string>();
            string cleanId = extId.Trim().TrimEnd('/');
            allowedOrigins.Add($"chrome-extension://{cleanId}/");

            var manifest = new NativeHostManifest
            {
                Name = ManifestRegistrar.HostName,
                Description = ManifestRegistrar.HostDescription,
                Path = "/dummy/path",
                Type = "stdio",
                AllowedOrigins = allowedOrigins
            };

            string json = JsonSerializer.Serialize(manifest, BridgeJsonContext.Default.NativeHostManifest);
            Assert.DoesNotContain("chrome-extension://*", json);
            Assert.Contains($"chrome-extension://{extId}/", json);
        }
        finally
        {
            if (File.Exists(tempManifestPath)) File.Delete(tempManifestPath);
        }
    }
}
