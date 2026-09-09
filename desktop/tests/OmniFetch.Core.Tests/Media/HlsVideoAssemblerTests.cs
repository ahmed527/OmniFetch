using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using OmniFetch.Core.Media;
using Xunit;

namespace OmniFetch.Core.Tests.Media;

public class HlsVideoAssemblerTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly FFmpegLocator _locator = new();

    public HlsVideoAssemblerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "OmniFetch_AssemblerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
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
        catch { }
    }

    [Fact]
    public void FFmpegLocator_FindsFFmpegOnAppleSilicon()
    {
        string? path = _locator.FindFFmpeg();
        // If ffmpeg is installed on this Mac (e.g. /opt/homebrew/bin/ffmpeg)
        if (path != null)
        {
            Assert.True(File.Exists(path));
            Assert.True(_locator.IsAvailable());
        }
    }

    [Fact]
    public async Task FFmpegLocator_GetVersionAsync_ReturnsValidBanner()
    {
        if (!_locator.IsAvailable()) return;

        string? version = await _locator.GetVersionAsync();
        Assert.NotNull(version);
        Assert.Contains("ffmpeg version", version, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateConcatFileAsync_FormatsPathsCorrectlyWithEscapedQuotes()
    {
        string manifestPath = Path.Combine(_tempDirectory, "concat_test.txt");
        string seg1 = Path.Combine(_tempDirectory, "seg1.ts");
        string seg2 = Path.Combine(_tempDirectory, "it's_seg2.ts");

        await File.WriteAllTextAsync(seg1, "chunk1");
        await File.WriteAllTextAsync(seg2, "chunk2");

        await HlsVideoAssembler.GenerateConcatFileAsync([seg1, seg2], manifestPath);

        Assert.True(File.Exists(manifestPath));
        string content = await File.ReadAllTextAsync(manifestPath);

        string expectedSeg1 = Path.GetFullPath(seg1);
        string expectedSeg2 = Path.GetFullPath(seg2).Replace("'", "'\\''");

        Assert.Contains($"file '{expectedSeg1}'", content);
        Assert.Contains($"file '{expectedSeg2}'", content);
    }

    [Fact]
    public async Task ConcatStreamsDirectAsync_ConcatenatesChunksInSequence()
    {
        string seg1 = Path.Combine(_tempDirectory, "part1.ts");
        string seg2 = Path.Combine(_tempDirectory, "part2.ts");
        string output = Path.Combine(_tempDirectory, "combined.ts");

        byte[] part1Data = Encoding.UTF8.GetBytes("HEAD_CHUNK_DATA_12345");
        byte[] part2Data = Encoding.UTF8.GetBytes("TAIL_CHUNK_DATA_67890");

        await File.WriteAllBytesAsync(seg1, part1Data);
        await File.WriteAllBytesAsync(seg2, part2Data);

        var assembler = new HlsVideoAssembler(_locator);
        await assembler.ConcatStreamsDirectAsync([seg1, seg2], output);

        Assert.True(File.Exists(output));
        byte[] combinedData = await File.ReadAllBytesAsync(output);

        Assert.Equal(part1Data.Length + part2Data.Length, combinedData.Length);
        string combinedStr = Encoding.UTF8.GetString(combinedData);
        Assert.Equal("HEAD_CHUNK_DATA_12345TAIL_CHUNK_DATA_67890", combinedStr);
    }

    [Fact]
    public async Task AssembleAsync_FallbackToDirectConcat_WhenOutputIsTs()
    {
        string seg1 = Path.Combine(_tempDirectory, "part1.ts");
        string seg2 = Path.Combine(_tempDirectory, "part2.ts");
        string output = Path.Combine(_tempDirectory, "output.ts");

        await File.WriteAllBytesAsync(seg1, Encoding.UTF8.GetBytes("AAA"));
        await File.WriteAllBytesAsync(seg2, Encoding.UTF8.GetBytes("BBB"));

        // Use a mock locator that reports ffmpeg is missing to test fallback
        var assembler = new HlsVideoAssembler(new MissingFFmpegLocator());
        await assembler.AssembleAsync([seg1, seg2], output, deleteSegmentsAfterAssembly: true);

        Assert.True(File.Exists(output));
        string combined = await File.ReadAllTextAsync(output);
        Assert.Equal("AAABBB", combined);

        // Segments should be cleaned up
        Assert.False(File.Exists(seg1));
        Assert.False(File.Exists(seg2));
    }

    [Fact]
    public async Task AssembleAsync_WhenFFmpegAvailable_LosslesslyRemuxesToMp4()
    {
        string? ffmpegPath = _locator.FindFFmpeg();
        if (string.IsNullOrEmpty(ffmpegPath)) return;

        string segPath = Path.Combine(_tempDirectory, "real_chunk.ts");
        string outMp4 = Path.Combine(_tempDirectory, "remuxed.mp4");

        // Generate tiny 0.2s synthetic MPEG-TS stream using FFmpeg testsrc
        using var genProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-y -f lavfi -i testsrc=duration=0.2:size=160x120:rate=10 -c:v libx264 -f mpegts \"{segPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Assert.NotNull(genProc);
        await genProc.WaitForExitAsync();
        Assert.True(File.Exists(segPath));

        var assembler = new HlsVideoAssembler(_locator);
        await assembler.AssembleAsync([segPath], outMp4, deleteSegmentsAfterAssembly: true);

        Assert.True(File.Exists(outMp4));
        Assert.True(new FileInfo(outMp4).Length > 0);
        Assert.False(File.Exists(segPath)); // Segment was deleted
    }

    private class MissingFFmpegLocator : IFFmpegLocator
    {
        public string? FindFFmpeg() => null;
        public bool IsAvailable() => false;
        public Task<string?> GetVersionAsync(System.Threading.CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
}
