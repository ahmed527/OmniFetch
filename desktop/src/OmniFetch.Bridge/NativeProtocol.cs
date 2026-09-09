using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OmniFetch.Bridge;

/// <summary>
/// Handles Chrome Native Messaging 32-bit little-endian framed binary protocol.
/// </summary>
public static class NativeProtocol
{
    public const int MaxMessageSize = 1024 * 1024; // 1 MB limit enforced by Chrome Native Messaging

    public static async Task<byte[]?> ReadMessageAsync(Stream input, CancellationToken ct = default)
    {
        byte[] lengthBuffer = new byte[4];
        int read = await ReadExactAsync(input, lengthBuffer, 0, 4, ct);
        if (read == 0)
        {
            return null; // Stream closed (EOF)
        }

        if (read < 4)
        {
            throw new EndOfStreamException($"Expected 4 bytes for message length prefix, but received {read}.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        if (length < 0)
        {
            throw new InvalidOperationException($"Message length {length} cannot be negative.");
        }

        if (length == 0)
        {
            return Array.Empty<byte>();
        }

        if (length > MaxMessageSize)
        {
            throw new InvalidOperationException($"Message length {length} exceeds maximum allowed size of {MaxMessageSize} bytes.");
        }

        byte[] messageBytes = new byte[length];
        int payloadRead = await ReadExactAsync(input, messageBytes, 0, length, ct);
        if (payloadRead < length)
        {
            throw new EndOfStreamException($"Expected {length} bytes for message payload, but stream closed after {payloadRead} bytes.");
        }
        return messageBytes;
    }

    public static async Task WriteMessageAsync(Stream output, byte[] payload, CancellationToken ct = default)
    {
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), payload.Length);
        payload.CopyTo(frame.AsSpan(4));

        await output.WriteAsync(frame, ct);
        await output.FlushAsync(ct);
    }

    public static async Task WriteResponseAsync(Stream output, BridgeResponse response, CancellationToken ct = default)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(response, BridgeJsonContext.Default.BridgeResponse);
        await WriteMessageAsync(output, bytes, ct);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), ct);
            if (read == 0)
            {
                return totalRead;
            }
            totalRead += read;
        }
        return totalRead;
    }
}
