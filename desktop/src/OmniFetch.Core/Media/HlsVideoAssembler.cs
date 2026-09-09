using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OmniFetch.Core.Exceptions;

namespace OmniFetch.Core.Media;

/// <summary>
/// Assembles HLS segments and performs lossless container remuxing via FFmpeg or direct stream stitching.
/// </summary>
public class HlsVideoAssembler : IHlsVideoAssembler
{
    private readonly IFFmpegLocator _ffmpegLocator;

    public HlsVideoAssembler(IFFmpegLocator? ffmpegLocator = null)
    {
        _ffmpegLocator = ffmpegLocator ?? new FFmpegLocator();
    }

    public async Task<string> AssembleAsync(
        IReadOnlyList<string> segmentPaths,
        string destinationFilePath,
        bool deleteSegmentsAfterAssembly = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(segmentPaths);
        if (segmentPaths.Count == 0)
        {
            throw new ArgumentException("No segment files were provided for assembly.", nameof(segmentPaths));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);

        string? destinationDir = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrEmpty(destinationDir))
        {
            Directory.CreateDirectory(destinationDir);
        }

        string? ffmpegPath = _ffmpegLocator.FindFFmpeg();
        bool isTsOutput = string.Equals(Path.GetExtension(destinationFilePath), ".ts", StringComparison.OrdinalIgnoreCase);

        if (isTsOutput)
        {
            // MPEG-TS transport stream chunks can be concatenated directly and losslessly without spawning FFmpeg
            await ConcatStreamsDirectAsync(segmentPaths, destinationFilePath, ct).ConfigureAwait(false);
            if (deleteSegmentsAfterAssembly)
            {
                CleanupSegments(segmentPaths);
            }
            return destinationFilePath;
        }

        if (string.IsNullOrEmpty(ffmpegPath))
        {
            throw new OmniFetchException(
                "FFmpeg is required to losslessly remux HLS segments into MP4 containers, but no FFmpeg binary was found. " +
                "Install FFmpeg via Homebrew ('brew install ffmpeg') or place the ffmpeg binary in OmniFetch Resources.");
        }

        // Generate FFmpeg concat manifest
        string segmentDir = Path.GetDirectoryName(segmentPaths[0]) ?? Path.GetTempPath();
        string concatManifestPath = Path.Combine(segmentDir, $"concat_{Guid.NewGuid():N}.txt");

        try
        {
            await GenerateConcatFileAsync(segmentPaths, concatManifestPath, ct).ConfigureAwait(false);

            // Lossless remux: stream copy, aac bitstream filter, faststart for streaming
            string arguments = $"-y -f concat -safe 0 -i \"{concatManifestPath}\" -c copy -bsf:a aac_adtstoasc -movflags +faststart \"{destinationFilePath}\"";

            await RunFFmpegAsync(ffmpegPath, arguments, ct).ConfigureAwait(false);

            if (deleteSegmentsAfterAssembly)
            {
                CleanupSegments(segmentPaths);
            }

            return destinationFilePath;
        }
        finally
        {
            try
            {
                if (File.Exists(concatManifestPath))
                {
                    File.Delete(concatManifestPath);
                }
            }
            catch
            {
                // Ignored
            }
        }
    }

    public async Task<string> AssembleDualStreamAsync(
        IReadOnlyList<string> videoSegmentPaths,
        IReadOnlyList<string> audioSegmentPaths,
        string destinationFilePath,
        bool deleteSegmentsAfterAssembly = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(videoSegmentPaths);
        ArgumentNullException.ThrowIfNull(audioSegmentPaths);
        if (videoSegmentPaths.Count == 0 || audioSegmentPaths.Count == 0)
        {
            throw new ArgumentException("Both video and audio segment lists must be non-empty.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);

        string? ffmpegPath = _ffmpegLocator.FindFFmpeg();
        if (string.IsNullOrEmpty(ffmpegPath))
        {
            throw new OmniFetchException(
                "FFmpeg is required to mux separate video and audio streams, but no FFmpeg binary was found.");
        }

        string? destinationDir = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrEmpty(destinationDir))
        {
            Directory.CreateDirectory(destinationDir);
        }

        string segmentDir = Path.GetDirectoryName(videoSegmentPaths[0]) ?? Path.GetTempPath();
        string videoManifest = Path.Combine(segmentDir, $"concat_video_{Guid.NewGuid():N}.txt");
        string audioManifest = Path.Combine(segmentDir, $"concat_audio_{Guid.NewGuid():N}.txt");

        try
        {
            await GenerateConcatFileAsync(videoSegmentPaths, videoManifest, ct).ConfigureAwait(false);
            await GenerateConcatFileAsync(audioSegmentPaths, audioManifest, ct).ConfigureAwait(false);

            string arguments = $"-y -f concat -safe 0 -i \"{videoManifest}\" -f concat -safe 0 -i \"{audioManifest}\" -c copy -bsf:a aac_adtstoasc -movflags +faststart \"{destinationFilePath}\"";

            await RunFFmpegAsync(ffmpegPath, arguments, ct).ConfigureAwait(false);

            if (deleteSegmentsAfterAssembly)
            {
                CleanupSegments(videoSegmentPaths);
                CleanupSegments(audioSegmentPaths);
            }

            return destinationFilePath;
        }
        finally
        {
            try
            {
                if (File.Exists(videoManifest)) File.Delete(videoManifest);
                if (File.Exists(audioManifest)) File.Delete(audioManifest);
            }
            catch
            {
                // Ignored
            }
        }
    }

    public async Task<string> ConcatStreamsDirectAsync(
        IReadOnlyList<string> segmentPaths,
        string destinationFilePath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(segmentPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);

        string? dir = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using var outputStream = new FileStream(
            destinationFilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        byte[] buffer = new byte[81920];
        foreach (var segmentPath in segmentPaths)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(segmentPath)) continue;

            await using var inputStream = new FileStream(
                segmentPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);

            int bytesRead;
            while ((bytesRead = await inputStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                await outputStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
            }
        }

        await outputStream.FlushAsync(ct).ConfigureAwait(false);
        return destinationFilePath;
    }

    public static async Task GenerateConcatFileAsync(
        IReadOnlyList<string> segmentPaths,
        string manifestPath,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        foreach (var path in segmentPaths)
        {
            string fullPath = Path.GetFullPath(path);
            // FFmpeg concat demuxer escapes single quotes as '\''
            string escaped = fullPath.Replace("'", "'\\''");
            sb.AppendLine($"file '{escaped}'");
        }

        // Write UTF-8 without BOM so FFmpeg doesn't encounter a byte-order-mark keyword syntax error
        await File.WriteAllTextAsync(manifestPath, sb.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
    }

    private static async Task RunFFmpegAsync(string ffmpegPath, string arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        var stderrBuilder = new StringBuilder();

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                lock (stderrBuilder)
                {
                    stderrBuilder.AppendLine(e.Data);
                }
            }
        };

        if (!process.Start())
        {
            throw new OmniFetchException("Failed to launch FFmpeg process.");
        }

        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignored
            }
            throw;
        }

        if (process.ExitCode != 0)
        {
            string stderr = stderrBuilder.ToString();
            throw new OmniFetchException($"FFmpeg stream remuxing failed with exit code {process.ExitCode}. Diagnostic output: {stderr}");
        }
    }

    private static void CleanupSegments(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Ignored
            }
        }
    }
}
