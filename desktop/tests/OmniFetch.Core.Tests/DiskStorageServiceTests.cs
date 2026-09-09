using Microsoft.Win32.SafeHandles;
using OmniFetch.Core.Storage;
using Xunit;

namespace OmniFetch.Core.Tests;

public class DiskStorageServiceTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly DiskStorageService _storageService;

    public DiskStorageServiceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "OmniFetchTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _storageService = new DiskStorageService();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore test cleanup errors
        }
    }

    [Fact]
    public void OpenAndPreallocate_SetsExactFileSizeOnDisk()
    {
        // Arrange
        string testFile = Path.Combine(_tempDirectory, "preallocated.iso");
        long targetSize = 25 * 1024 * 1024; // 25 MB

        // Act
        using (var handle = _storageService.OpenAndPreallocate(testFile, targetSize))
        {
            long lengthInHandle = RandomAccess.GetLength(handle);
            Assert.Equal(targetSize, lengthInHandle);
        }

        // Assert
        var fileInfo = new FileInfo(testFile);
        Assert.True(fileInfo.Exists);
        Assert.Equal(targetSize, fileInfo.Length);
    }

    [Fact]
    public async Task WriteAsync_ConcurrentDisjointWrites_AreThreadSafeAndLockFree()
    {
        // Arrange
        string testFile = Path.Combine(_tempDirectory, "concurrent_write.dat");
        int threadCount = 10;
        int chunkSize = 64 * 1024; // 64 KB per thread
        long totalSize = threadCount * chunkSize;

        byte[][] expectedChunks = new byte[threadCount][];
        for (int i = 0; i < threadCount; i++)
        {
            expectedChunks[i] = new byte[chunkSize];
            Array.Fill(expectedChunks[i], (byte)(i + 1));
        }

        using var handle = _storageService.OpenAndPreallocate(testFile, totalSize);

        // Act - Concurrently write chunks into disjoint regions of the file without locks
        var tasks = new Task[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            int index = i;
            long offset = index * chunkSize;
            tasks[i] = Task.Run(async () =>
            {
                await _storageService.WriteAsync(handle, expectedChunks[index].AsMemory(), offset);
            });
        }

        await Task.WhenAll(tasks);
        _storageService.Flush(handle);

        // Assert - Read file and verify all bytes match exactly
        byte[] writtenData = File.ReadAllBytes(testFile);
        Assert.Equal(totalSize, writtenData.Length);

        for (int i = 0; i < threadCount; i++)
        {
            int offset = i * chunkSize;
            for (int j = 0; j < chunkSize; j++)
            {
                Assert.Equal(expectedChunks[i][j], writtenData[offset + j]);
            }
        }
    }
}
