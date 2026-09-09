using System;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OmniFetch.Core.Ipc;

/// <summary>
/// Client for communicating with the OmniFetch Unix Domain Socket IPC server.
/// </summary>
public sealed class IpcClient : IDisposable
{
    private readonly string _socketPath;
    private Socket? _socket;
    private bool _isDisposed;

    public IpcClient(string? socketPath = null)
    {
        _socketPath = socketPath ?? UnixDomainSocketServer.GetDefaultSocketPath();
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var endpoint = new UnixDomainSocketEndPoint(_socketPath);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            await socket.ConnectAsync(endpoint, ct);
            _socket = socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task<IpcResponse> SendRequestAsync(NativeDownloadRequest request, CancellationToken ct = default)
    {
        string json = JsonSerializer.Serialize(request, IpcJsonSerializerContext.Default.NativeDownloadRequest);
        return await SendRawJsonAsync(json, ct);
    }

    public async Task<IpcResponse> SendRefreshUrlAsync(NativeRefreshUrlRequest request, CancellationToken ct = default)
    {
        string json = JsonSerializer.Serialize(request, IpcJsonSerializerContext.Default.NativeRefreshUrlRequest);
        return await SendRawJsonAsync(json, ct);
    }

    public async Task<IpcResponse> PingAsync(string clientName = "IpcClient", CancellationToken ct = default)
    {
        var ping = new NativePingRequest
        {
            Client = clientName
        };
        string json = JsonSerializer.Serialize(ping, IpcJsonSerializerContext.Default.NativePingRequest);
        return await SendRawJsonAsync(json, ct);
    }

    public async Task<IpcResponse> SendRawJsonAsync(string jsonPayload, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_socket == null || !_socket.Connected)
        {
            throw new InvalidOperationException("IpcClient is not connected to the socket.");
        }

        byte[] payloadBytes = Encoding.UTF8.GetBytes(jsonPayload);
        byte[] frame = new byte[4 + payloadBytes.Length];

        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), payloadBytes.Length);
        payloadBytes.CopyTo(frame.AsSpan(4));

        await _socket.SendAsync(frame, SocketFlags.None, ct);

        // Read 4-byte response length
        byte[] lengthBuffer = new byte[4];
        await ReadExactAsync(_socket, lengthBuffer, 0, 4, ct);
        int responseLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);

        if (responseLength <= 0)
        {
            return IpcResponse.Error("EMPTY_RESPONSE", "Received empty response from IPC server.");
        }

        byte[] responseBuffer = new byte[responseLength];
        await ReadExactAsync(_socket, responseBuffer, 0, responseLength, ct);

        var response = JsonSerializer.Deserialize(responseBuffer, IpcJsonSerializerContext.Default.IpcResponse);
        return response ?? IpcResponse.Error("DESERIALIZATION_FAILED", "Failed to deserialize IPC response.");
    }

    private static async Task ReadExactAsync(Socket socket, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await socket.ReceiveAsync(buffer.AsMemory(offset + totalRead, count - totalRead), SocketFlags.None, ct);
            if (read == 0)
            {
                throw new EndOfStreamException($"Socket closed unexpectedly before reading {count} bytes.");
            }
            totalRead += read;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _socket?.Dispose();
    }
}
