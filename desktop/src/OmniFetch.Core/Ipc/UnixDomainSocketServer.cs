using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OmniFetch.Core.Ipc;

/// <summary>
/// High-performance Unix Domain Socket IPC server for macOS.
/// Receives native messaging payloads, dispatches actions, and sends framed JSON responses.
/// </summary>
public sealed class UnixDomainSocketServer : IIpcServer
{
    private const int MaxMessageLength = 1024 * 1024; // 1 MB limit (matches Chrome Native Messaging)
    private readonly string _socketPath;
    private readonly object _lock = new();

    private Socket? _listenerSocket;
    private CancellationTokenSource? _serverCts;
    private Task? _listenerTask;
    private bool _isDisposed;

    public string SocketPath => _socketPath;
    public bool IsRunning => _listenerSocket != null && _serverCts != null && !_serverCts.IsCancellationRequested;

    public event Func<NativeDownloadRequest, Task<IpcResponse>>? OnDownloadRequested;
    public event Func<NativeRefreshUrlRequest, Task<IpcResponse>>? OnUrlRefreshed;
    public event Func<NativePingRequest, Task<IpcResponse>>? OnPing;

    public UnixDomainSocketServer(string? socketPath = null)
    {
        _socketPath = socketPath ?? GetDefaultSocketPath();
        ValidateSocketPath(_socketPath);
    }

    private static void ValidateSocketPath(string path)
    {
        if (Encoding.UTF8.GetByteCount(path) >= 104)
        {
            throw new ArgumentException($"Unix domain socket path '{path}' exceeds macOS sockaddr_un limit of 103 bytes.");
        }
    }

    public static string GetDefaultSocketPath()
    {
        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string defaultPath = Path.Combine(userHome, ".omnifetch.sock");
        if (Encoding.UTF8.GetByteCount(defaultPath) >= 104)
        {
            defaultPath = $"/tmp/.of_{Environment.UserName}.sock";
        }
        return defaultPath;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (IsRunning) return;
        }

        // 1. Detect and resolve stale or existing socket file
        await ResolveSocketFileAsync(ct);

        var endpoint = new UnixDomainSocketEndPoint(_socketPath);
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        bool boundSuccessfully = false;

        try
        {
            listener.Bind(endpoint);
            boundSuccessfully = true;
            listener.Listen(64);

            // 2. Enforce strict POSIX permissions (0600 - user read/write only) on macOS
            try
            {
                if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                {
                    File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch
            {
                // Best-effort permission enforcement (may fail in restricted test environments)
            }

            lock (_lock)
            {
                _listenerSocket = listener;
                _serverCts = new CancellationTokenSource();
                _listenerTask = Task.Run(() => AcceptConnectionsAsync(listener, _serverCts.Token));
            }
        }
        catch
        {
            listener.Dispose();
            if (boundSuccessfully)
            {
                CleanupSocketFile();
            }
            throw;
        }
    }

    private async Task ResolveSocketFileAsync(CancellationToken ct)
    {
        if (!File.Exists(_socketPath)) return;

        // Probe if existing socket has an active listener
        using var probeSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(500);
            await probeSocket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), probeCts.Token);

            // If connection succeeded, an active instance is running
            throw new InvalidOperationException($"Another OmniFetch instance is actively listening on socket: {_socketPath}");
        }
        catch (SocketException)
        {
            // Socket exists on disk but connection was refused -> stale socket from prior crash
            CleanupSocketFile();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Probe timed out -> assume dead/stale socket
            CleanupSocketFile();
        }
    }

    private async Task AcceptConnectionsAsync(Socket listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Socket clientSocket = await listener.AcceptAsync(ct);
                _ = Task.Run(() => ProcessClientAsync(clientSocket, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                if (ct.IsCancellationRequested) break;
            }
        }
    }

    private async Task ProcessClientAsync(Socket client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                byte[] lengthBuffer = new byte[4];

                while (!ct.IsCancellationRequested)
                {
                    // 1. Read 4-byte little-endian message length prefix
                    int bytesRead = await ReadExactAsync(client, lengthBuffer, 0, 4, ct);
                    if (bytesRead == 0)
                    {
                        // Client gracefully closed connection
                        break;
                    }

                    int messageLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
                    if (messageLength <= 0 || messageLength > MaxMessageLength)
                    {
                        var errResponse = IpcResponse.Error("INVALID_MESSAGE_LENGTH", $"Message length {messageLength} is invalid or exceeds 1 MB.");
                        await SendResponseAsync(client, errResponse, ct);
                        break;
                    }

                    // 2. Read full message payload
                    byte[] messageBuffer = ArrayPool<byte>.Shared.Rent(messageLength);
                    string jsonPayload;
                    try
                    {
                        int payloadRead = await ReadExactAsync(client, messageBuffer, 0, messageLength, ct);
                        if (payloadRead < messageLength)
                        {
                            break;
                        }
                        jsonPayload = Encoding.UTF8.GetString(messageBuffer, 0, messageLength);
                    }
                    finally
                    {
                        // Clear buffer to avoid retaining sensitive auth credentials / cookies in pool
                        ArrayPool<byte>.Shared.Return(messageBuffer, clearArray: true);
                    }

                    // 3. Dispatch and compute response
                    IpcResponse response;
                    try
                    {
                        response = await DispatchMessageAsync(jsonPayload);
                    }
                    catch (Exception ex)
                    {
                        response = IpcResponse.Error("DISPATCH_ERROR", ex.Message);
                    }

                    // 4. Send framed response back to client
                    await SendResponseAsync(client, response, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation on server shutdown
            }
            catch (SocketException)
            {
                // Client aborted or closed connection abruptly
            }
            catch (Exception)
            {
                // Suppress unobserved task exceptions from rogue clients
            }
        }
    }

    private async Task<IpcResponse> DispatchMessageAsync(string jsonPayload)
    {
        using var doc = JsonDocument.Parse(jsonPayload);
        var root = doc.RootElement;

        string action = "download";
        if (root.TryGetProperty("action", out var actionProp))
        {
            action = actionProp.GetString() ?? "download";
        }

        switch (action.ToLowerInvariant())
        {
            case "download":
            {
                var req = JsonSerializer.Deserialize(jsonPayload, IpcJsonSerializerContext.Default.NativeDownloadRequest);
                if (req == null || string.IsNullOrWhiteSpace(req.Url))
                {
                    return IpcResponse.Error("INVALID_REQUEST", "Download request missing required 'url'.");
                }

                if (OnDownloadRequested != null)
                {
                    return await OnDownloadRequested.Invoke(req);
                }

                return IpcResponse.Accepted(Guid.NewGuid(), req.SuggestedFileName, "Download request received.");
            }

            case "refresh_url":
            {
                var req = JsonSerializer.Deserialize(jsonPayload, IpcJsonSerializerContext.Default.NativeRefreshUrlRequest);
                if (req == null || req.JobId == Guid.Empty || string.IsNullOrWhiteSpace(req.NewUrl))
                {
                    return IpcResponse.Error("INVALID_REQUEST", "Refresh URL request missing 'jobId' or 'newUrl'.");
                }

                if (OnUrlRefreshed != null)
                {
                    return await OnUrlRefreshed.Invoke(req);
                }

                return IpcResponse.Ok($"URL for job {req.JobId} refreshed.");
            }

            case "ping":
            {
                var req = JsonSerializer.Deserialize(jsonPayload, IpcJsonSerializerContext.Default.NativePingRequest);
                if (OnPing != null && req != null)
                {
                    return await OnPing.Invoke(req);
                }

                return IpcResponse.Ok("pong");
            }

            default:
                return IpcResponse.Error("UNSUPPORTED_ACTION", $"Action '{action}' is not supported.");
        }
    }

    private static async Task SendResponseAsync(Socket client, IpcResponse response, CancellationToken ct)
    {
        byte[] responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, IpcJsonSerializerContext.Default.IpcResponse);
        byte[] frame = new byte[4 + responseBytes.Length];

        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), responseBytes.Length);
        responseBytes.CopyTo(frame.AsSpan(4));

        await client.SendAsync(frame, SocketFlags.None, ct);
    }

    private static async Task<int> ReadExactAsync(Socket socket, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await socket.ReceiveAsync(buffer.AsMemory(offset + totalRead, count - totalRead), SocketFlags.None, ct);
            if (read == 0)
            {
                return totalRead; // End of stream
            }
            totalRead += read;
        }
        return totalRead;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        CancellationTokenSource? cts;
        Socket? listener;
        Task? task;

        lock (_lock)
        {
            if (!IsRunning) return;
            cts = _serverCts;
            listener = _listenerSocket;
            task = _listenerTask;

            _serverCts = null;
            _listenerSocket = null;
            _listenerTask = null;
        }

        try
        {
            cts?.Cancel();
            listener?.Close();

            if (task != null)
            {
                try
                {
                    await task.WaitAsync(TimeSpan.FromSeconds(2), ct);
                }
                catch
                {
                    // Ignore cancellation or wait timeout
                }
            }
        }
        finally
        {
            cts?.Dispose();
            listener?.Dispose();
            CleanupSocketFile();
        }
    }

    private void CleanupSocketFile()
    {
        try
        {
            if (File.Exists(_socketPath))
            {
                File.Delete(_socketPath);
            }
        }
        catch
        {
            // Ignore cleanup failure
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        await StopAsync(CancellationToken.None);
        GC.SuppressFinalize(this);
    }
}
