using System;
using System.Threading;
using System.Threading.Tasks;

namespace OmniFetch.Core.Ipc;

/// <summary>
/// Defines the Unix Domain Socket IPC server contract for receiving commands from browser bridges or CLI tools.
/// </summary>
public interface IIpcServer : IAsyncDisposable
{
    /// <summary>
    /// Gets the filesystem path of the Unix Domain Socket.
    /// </summary>
    string SocketPath { get; }

    /// <summary>
    /// Gets whether the server is currently actively listening for incoming connections.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Starts the IPC socket server listener loop.
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Stops the IPC socket server and unlinks the socket file.
    /// </summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Event triggered when a browser download interception request is received.
    /// </summary>
    event Func<NativeDownloadRequest, Task<IpcResponse>>? OnDownloadRequested;

    /// <summary>
    /// Event triggered when an expired link refresh request is received.
    /// </summary>
    event Func<NativeRefreshUrlRequest, Task<IpcResponse>>? OnUrlRefreshed;

    /// <summary>
    /// Event triggered when a ping or health check request is received.
    /// </summary>
    event Func<NativePingRequest, Task<IpcResponse>>? OnPing;
}
