using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OmniFetch.Bridge;

/// <summary>
/// Relays binary-framed JSON payloads to the OmniFetch Unix Domain Socket.
/// </summary>
public static class UnixSocketRelay
{
    public static string GetDefaultSocketPath()
    {
        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string defaultPath = Path.Combine(userHome, ".omnifetch.sock");
        if (System.Text.Encoding.UTF8.GetByteCount(defaultPath) >= 104)
        {
            defaultPath = $"/tmp/.of_{Environment.UserName}.sock";
        }
        return defaultPath;
    }

    public static async Task<byte[]> RelayAsync(byte[] messagePayload, string? socketPath = null, CancellationToken ct = default)
    {
        string path = socketPath ?? GetDefaultSocketPath();

        if (!File.Exists(path))
        {
            var fallback = new BridgeResponse
            {
                Status = "error",
                ErrorCode = "DAEMON_OFFLINE",
                Message = "OmniFetch desktop daemon is not running (socket not found)."
            };
            return JsonSerializer.SerializeToUtf8Bytes(fallback, BridgeJsonContext.Default.BridgeResponse);
        }

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        operationCts.CancelAfter(TimeSpan.FromSeconds(5));
        var opToken = operationCts.Token;

        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), opToken);

            // Send 4-byte length + payload
            byte[] frame = new byte[4 + messagePayload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), messagePayload.Length);
            messagePayload.CopyTo(frame.AsSpan(4));

            await socket.SendAsync(frame, SocketFlags.None, opToken);

            // Read 4-byte length prefix of response
            byte[] lengthBuffer = new byte[4];
            await ReadExactAsync(socket, lengthBuffer, 0, 4, opToken);
            int responseLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);

            if (responseLength <= 0 || responseLength > NativeProtocol.MaxMessageSize)
            {
                var err = new BridgeResponse
                {
                    Status = "error",
                    ErrorCode = "INVALID_RESPONSE_LENGTH",
                    Message = $"Invalid response length {responseLength} from daemon."
                };
                return JsonSerializer.SerializeToUtf8Bytes(err, BridgeJsonContext.Default.BridgeResponse);
            }

            byte[] responseBytes = new byte[responseLength];
            await ReadExactAsync(socket, responseBytes, 0, responseLength, opToken);
            return responseBytes;
        }
        catch (SocketException ex)
        {
            var err = new BridgeResponse
            {
                Status = "error",
                ErrorCode = "DAEMON_CONNECTION_REFUSED",
                Message = $"Cannot connect to OmniFetch daemon at {path}: {ex.Message}"
            };
            return JsonSerializer.SerializeToUtf8Bytes(err, BridgeJsonContext.Default.BridgeResponse);
        }
        catch (EndOfStreamException ex)
        {
            var err = new BridgeResponse
            {
                Status = "error",
                ErrorCode = "DAEMON_DISCONNECTED",
                Message = $"OmniFetch daemon closed connection prematurely: {ex.Message}"
            };
            return JsonSerializer.SerializeToUtf8Bytes(err, BridgeJsonContext.Default.BridgeResponse);
        }
        catch (IOException ex)
        {
            var err = new BridgeResponse
            {
                Status = "error",
                ErrorCode = "DAEMON_IO_ERROR",
                Message = $"I/O error communicating with OmniFetch daemon: {ex.Message}"
            };
            return JsonSerializer.SerializeToUtf8Bytes(err, BridgeJsonContext.Default.BridgeResponse);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var err = new BridgeResponse
            {
                Status = "error",
                ErrorCode = "DAEMON_TIMEOUT",
                Message = "Connection or response from OmniFetch daemon timed out."
            };
            return JsonSerializer.SerializeToUtf8Bytes(err, BridgeJsonContext.Default.BridgeResponse);
        }
    }

    private static async Task<int> ReadExactAsync(Socket socket, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await socket.ReceiveAsync(buffer.AsMemory(offset + totalRead, count - totalRead), SocketFlags.None, ct);
            if (read == 0)
            {
                throw new EndOfStreamException($"Socket closed before reading {count} bytes.");
            }
            totalRead += read;
        }
        return totalRead;
    }
}
