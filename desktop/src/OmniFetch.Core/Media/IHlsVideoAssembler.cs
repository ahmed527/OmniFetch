using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OmniFetch.Core.Media;

/// <summary>
/// Service for assembling and remuxing downloaded HLS MPEG-TS / fMP4 segments into containerized media files (e.g. MP4).
/// </summary>
public interface IHlsVideoAssembler
{
    /// <summary>
    /// Assembles a sequential list of segment file paths into a single media file.
    /// Uses FFmpeg lossless stream copy (-c copy -bsf:a aac_adtstoasc -movflags +faststart) when available.
    /// Falls back to direct binary stream concatenation if FFmpeg is unavailable and output is .ts.
    /// </summary>
    Task<string> AssembleAsync(
        IReadOnlyList<string> segmentPaths,
        string destinationFilePath,
        bool deleteSegmentsAfterAssembly = true,
        CancellationToken ct = default);

    /// <summary>
    /// Muxes separate video and audio segment streams into a single synchronized output file.
    /// </summary>
    Task<string> AssembleDualStreamAsync(
        IReadOnlyList<string> videoSegmentPaths,
        IReadOnlyList<string> audioSegmentPaths,
        string destinationFilePath,
        bool deleteSegmentsAfterAssembly = true,
        CancellationToken ct = default);

    /// <summary>
    /// Fallback direct binary concatenation of MPEG-TS transport chunks without container remuxing.
    /// </summary>
    Task<string> ConcatStreamsDirectAsync(
        IReadOnlyList<string> segmentPaths,
        string destinationFilePath,
        CancellationToken ct = default);
}
