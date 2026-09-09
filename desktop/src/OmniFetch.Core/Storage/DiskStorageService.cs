using Microsoft.Win32.SafeHandles;
using OmniFetch.Core.Exceptions;

namespace OmniFetch.Core.Storage;

/// <summary>
/// Implements lock-free zero-stitch file I/O using .NET's high-performance RandomAccess API.
/// Pre-allocates target files to eliminate IDM's 2x write rebuilding bottleneck completely.
/// </summary>
public class DiskStorageService : IDiskStorageService
{
    public SafeFileHandle OpenAndPreallocate(string filePath, long totalBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        try
        {
            // Ensure target directory exists
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Open SafeFileHandle with asynchronous non-locking flags
            var handle = File.OpenHandle(
                path: filePath,
                mode: FileMode.OpenOrCreate,
                access: FileAccess.ReadWrite,
                share: FileShare.ReadWrite,
                options: FileOptions.Asynchronous
            );

            // Pre-allocate or resize the file to exact total bytes
            if (totalBytes > 0)
            {
                long currentLength = RandomAccess.GetLength(handle);
                if (currentLength != totalBytes)
                {
                    RandomAccess.SetLength(handle, totalBytes);
                }
            }

            return handle;
        }
        catch (Exception ex) when (ex is not OmniFetchException)
        {
            throw new DiskAllocationException(filePath, totalBytes, $"Failed to open or pre-allocate disk file '{filePath}': {ex.Message}", ex);
        }
    }

    public ValueTask WriteAsync(SafeFileHandle fileHandle, ReadOnlyMemory<byte> buffer, long fileOffset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileHandle);

        if (fileHandle.IsClosed || fileHandle.IsInvalid)
        {
            throw new ObjectDisposedException(nameof(fileHandle), "File handle is closed or invalid.");
        }

        // RandomAccess.WriteAsync writes directly to the specific byte offset in the kernel
        // without locks, mutexes, or shared file pointers.
        return RandomAccess.WriteAsync(fileHandle, buffer, fileOffset, cancellationToken);
    }

    public void Flush(SafeFileHandle fileHandle)
    {
        if (fileHandle is { IsClosed: false, IsInvalid: false })
        {
            try
            {
                RandomAccess.FlushToDisk(fileHandle);
            }
            catch
            {
                // Best-effort flush
            }
        }
    }
}
