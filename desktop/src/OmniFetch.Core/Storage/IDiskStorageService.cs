using Microsoft.Win32.SafeHandles;

namespace OmniFetch.Core.Storage;

/// <summary>
/// High-throughput disk storage service for lock-free concurrent writes and zero-stitch pre-allocation.
/// </summary>
public interface IDiskStorageService
{
    /// <summary>
    /// Opens or creates the target file on disk with asynchronous multi-threaded access and pre-allocates its total size.
    /// </summary>
    /// <param name="filePath">Target destination path.</param>
    /// <param name="totalBytes">Total size in bytes to pre-allocate (0 for unbounded single-stream).</param>
    /// <returns>SafeFileHandle configured for concurrent asynchronous I/O.</returns>
    SafeFileHandle OpenAndPreallocate(string filePath, long totalBytes);

    /// <summary>
    /// Thread-safe direct write at a specific file offset using kernel-level position addressing.
    /// Eliminates file pointer lock contention between parallel download streams.
    /// </summary>
    ValueTask WriteAsync(SafeFileHandle fileHandle, ReadOnlyMemory<byte> buffer, long fileOffset, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flushes unwritten disk cache buffers to physical storage.
    /// </summary>
    void Flush(SafeFileHandle fileHandle);
}
