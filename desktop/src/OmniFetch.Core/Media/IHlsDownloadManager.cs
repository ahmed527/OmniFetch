using System;
using System.Threading;
using System.Threading.Tasks;
using OmniFetch.Core.Models;

namespace OmniFetch.Core.Media;

/// <summary>
/// High-level coordinator orchestrating end-to-end HLS / M3U8 video stream capture:
/// Manifest resolution, variant selection, parallel segment downloading, AES-128 decryption, and FFmpeg remuxing.
/// </summary>
public interface IHlsDownloadManager
{
    /// <summary>
    /// Downloads an HLS stream from a manifest URL and losslessly remuxes it to the target file path.
    /// </summary>
    Task<DownloadJobInfo> DownloadHlsStreamAsync(
        string playlistUrl,
        string destinationFilePath,
        DownloadOptions? options = null,
        int? preferredResolutionHeight = null,
        IProgress<DownloadProgressSnapshot>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Executes an existing DownloadJobInfo configured for HLS download.
    /// </summary>
    Task<DownloadJobInfo> DownloadHlsJobAsync(
        DownloadJobInfo jobInfo,
        DownloadOptions? options = null,
        int? preferredResolutionHeight = null,
        IProgress<DownloadProgressSnapshot>? progress = null,
        CancellationToken ct = default);
}
