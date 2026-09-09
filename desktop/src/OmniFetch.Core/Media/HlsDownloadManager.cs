using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OmniFetch.Core.Common;
using OmniFetch.Core.Exceptions;
using OmniFetch.Core.Models;
using OmniFetch.Core.Network;
using OmniFetch.Core.Persistence;

namespace OmniFetch.Core.Media;

/// <summary>
/// High-level coordinator orchestrating end-to-end HLS video stream capture.
/// Integrates manifest parsing, variant selection, parallel segment downloading, AES-128 decryption, and FFmpeg remuxing.
/// </summary>
public class HlsDownloadManager : IHlsDownloadManager
{
    private readonly IHlsParser _parser;
    private readonly IHlsSegmentDownloader _segmentDownloader;
    private readonly IHlsVideoAssembler _videoAssembler;
    private readonly HttpClient _httpClient;
    private readonly IDownloadRepository? _repository;

    public HlsDownloadManager(
        IHlsParser? parser = null,
        IHlsSegmentDownloader? segmentDownloader = null,
        IHlsVideoAssembler? videoAssembler = null,
        HttpClient? httpClient = null,
        IDownloadRepository? repository = null)
    {
        _httpClient = httpClient ?? SocketsHttpHandlerFactory.CreateClient();
        _parser = parser ?? new HlsParser();
        _segmentDownloader = segmentDownloader ?? new HlsSegmentDownloader(_httpClient);
        _videoAssembler = videoAssembler ?? new HlsVideoAssembler();
        _repository = repository;
    }

    public async Task<DownloadJobInfo> DownloadHlsStreamAsync(
        string playlistUrl,
        string destinationFilePath,
        DownloadOptions? options = null,
        int? preferredResolutionHeight = null,
        IProgress<DownloadProgressSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);

        var jobInfo = new DownloadJobInfo
        {
            Url = playlistUrl,
            DestinationFilePath = destinationFilePath,
            TotalBytes = -1,
            ContentType = "application/x-mpegURL",
            SupportsRange = false,
            Status = DownloadStatus.Queued,
            CreatedAtUtc = DateTime.UtcNow
        };

        if (_repository != null)
        {
            await _repository.AddJobAsync(jobInfo, ct).ConfigureAwait(false);
        }

        return await DownloadHlsJobAsync(jobInfo, options, preferredResolutionHeight, progress, ct).ConfigureAwait(false);
    }

    public async Task<DownloadJobInfo> DownloadHlsJobAsync(
        DownloadJobInfo jobInfo,
        DownloadOptions? options = null,
        int? preferredResolutionHeight = null,
        IProgress<DownloadProgressSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jobInfo);
        options ??= new DownloadOptions();

        jobInfo.Status = DownloadStatus.Downloading;
        if (_repository != null)
        {
            await _repository.UpdateJobStatusAsync(jobInfo.Id, DownloadStatus.Downloading, ct: ct).ConfigureAwait(false);
        }

        string stagingDir = Path.Combine(Path.GetTempPath(), "OmniFetch", "staging", jobInfo.Id.ToString("N"));
        Directory.CreateDirectory(stagingDir);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // 1. Fetch & Parse Master / Media Playlist
            using var manifestReq = new HttpRequestMessage(HttpMethod.Get, jobInfo.Url);
            ApplyRequestHeaders(manifestReq, options, jobInfo);

            using var manifestResp = await _httpClient.SendAsync(manifestReq, ct).ConfigureAwait(false);
            manifestResp.EnsureSuccessStatusCode();
            string manifestContent = await manifestResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            HlsMediaPlaylist videoPlaylist;
            HlsMediaPlaylist? audioPlaylist = null;

            if (_parser.IsMasterPlaylist(manifestContent))
            {
                var master = _parser.ParseMasterPlaylist(manifestContent, jobInfo.Url);
                var selectedVariant = SelectVariant(master, preferredResolutionHeight);
                if (selectedVariant == null)
                {
                    throw new OmniFetchException("No playable variant streams were found in the HLS master playlist.");
                }

                // Fetch selected variant media playlist
                using var variantReq = new HttpRequestMessage(HttpMethod.Get, selectedVariant.Uri);
                ApplyRequestHeaders(variantReq, options, jobInfo);
                using var variantResp = await _httpClient.SendAsync(variantReq, ct).ConfigureAwait(false);
                variantResp.EnsureSuccessStatusCode();
                string variantContent = await variantResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                videoPlaylist = _parser.ParseMediaPlaylist(variantContent, selectedVariant.Uri);

                // Check for separate audio rendition
                var audioRendition = master.AudioRenditions.FirstOrDefault(r => !string.IsNullOrEmpty(r.Uri) && (r.IsDefault || r.IsAutoSelect))
                                     ?? master.AudioRenditions.FirstOrDefault(r => !string.IsNullOrEmpty(r.Uri));

                if (audioRendition?.Uri != null)
                {
                    using var audioReq = new HttpRequestMessage(HttpMethod.Get, audioRendition.Uri);
                    ApplyRequestHeaders(audioReq, options, jobInfo);
                    using var audioResp = await _httpClient.SendAsync(audioReq, ct).ConfigureAwait(false);
                    if (audioResp.IsSuccessStatusCode)
                    {
                        string audioContent = await audioResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        audioPlaylist = _parser.ParseMediaPlaylist(audioContent, audioRendition.Uri);
                    }
                }
            }
            else
            {
                videoPlaylist = _parser.ParseMediaPlaylist(manifestContent, jobInfo.Url);
            }

            if (videoPlaylist.Segments.Count == 0)
            {
                throw new OmniFetchException("HLS playlist contained 0 downloadable media segments.");
            }

            // 2. Download segments with telemetry conversion
            var progressAdapter = new Progress<HlsDownloadProgress>(p =>
            {
                if (progress == null) return;

                int segmentsToDisplay = Math.Min(p.TotalSegments, 16);
                var segmentSnapshots = new List<SegmentProgressSnapshot>();
                if (segmentsToDisplay > 0)
                {
                    double ratio = (double)p.CompletedSegments / p.TotalSegments;
                    int completedBlocks = (int)Math.Floor(ratio * segmentsToDisplay);

                    for (int i = 0; i < segmentsToDisplay; i++)
                    {
                        segmentSnapshots.Add(new SegmentProgressSnapshot
                        {
                            SegmentIndex = i,
                            StartByte = i * 1000L,
                            EndByte = (i + 1) * 1000L - 1,
                            CurrentByte = i < completedBlocks ? (i + 1) * 1000L - 1 : (i == completedBlocks ? (long)(i * 1000L + (ratio * segmentsToDisplay - completedBlocks) * 1000L) : i * 1000L),
                            ProgressPercentage = i < completedBlocks ? 100.0 : (i == completedBlocks ? (ratio * segmentsToDisplay - completedBlocks) * 100.0 : 0.0),
                            SpeedBytesPerSecond = p.SpeedBytesPerSecond,
                            IsCompleted = i < completedBlocks
                        });
                    }
                }

                long estimatedTotal = p.ProgressPercentage > 0
                    ? (long)(p.DownloadedBytes / (p.ProgressPercentage / 100.0))
                    : -1;

                progress.Report(new DownloadProgressSnapshot
                {
                    JobId = jobInfo.Id,
                    Status = DownloadStatus.Downloading,
                    TotalBytes = estimatedTotal,
                    DownloadedBytes = p.DownloadedBytes,
                    ProgressPercentage = p.ProgressPercentage,
                    InstantSpeedBytesPerSecond = p.SpeedBytesPerSecond,
                    SmoothedSpeedBytesPerSecond = p.SpeedBytesPerSecond,
                    EstimatedTimeRemaining = p.EstimatedTimeRemaining,
                    ElapsedTime = stopwatch.Elapsed,
                    ActiveConnections = options.MaxConnections,
                    Segments = segmentSnapshots
                });
            });

            string videoStaging = Path.Combine(stagingDir, "video");
            var videoSegments = await _segmentDownloader.DownloadSegmentsAsync(
                videoPlaylist,
                videoStaging,
                options,
                progressAdapter,
                maxConcurrency: options.MaxConnections,
                ct: ct).ConfigureAwait(false);

            string[]? audioSegments = null;
            if (audioPlaylist != null && audioPlaylist.Segments.Count > 0)
            {
                string audioStaging = Path.Combine(stagingDir, "audio");
                audioSegments = await _segmentDownloader.DownloadSegmentsAsync(
                    audioPlaylist,
                    audioStaging,
                    options,
                    progress: null, // Audio runs in secondary pass
                    maxConcurrency: options.MaxConnections,
                    ct: ct).ConfigureAwait(false);
            }

            // 3. Assemble and Losslessly Remux via FFmpeg
            if (audioSegments != null && audioSegments.Length > 0)
            {
                await _videoAssembler.AssembleDualStreamAsync(
                    videoSegments,
                    audioSegments,
                    jobInfo.DestinationFilePath,
                    deleteSegmentsAfterAssembly: true,
                    ct: ct).ConfigureAwait(false);
            }
            else
            {
                await _videoAssembler.AssembleAsync(
                    videoSegments,
                    jobInfo.DestinationFilePath,
                    deleteSegmentsAfterAssembly: true,
                    ct: ct).ConfigureAwait(false);
            }

            // 4. Finalize Job
            jobInfo.Status = DownloadStatus.Completed;
            jobInfo.CompletedAtUtc = DateTime.UtcNow;

            if (File.Exists(jobInfo.DestinationFilePath))
            {
                jobInfo.TotalBytes = new FileInfo(jobInfo.DestinationFilePath).Length;
            }

            if (_repository != null)
            {
                await _repository.UpdateJobStatusAsync(jobInfo.Id, DownloadStatus.Completed, jobInfo.CompletedAtUtc, ct).ConfigureAwait(false);
            }

            progress?.Report(new DownloadProgressSnapshot
            {
                JobId = jobInfo.Id,
                Status = DownloadStatus.Completed,
                TotalBytes = jobInfo.TotalBytes,
                DownloadedBytes = jobInfo.TotalBytes,
                ProgressPercentage = 100.0,
                InstantSpeedBytesPerSecond = 0,
                SmoothedSpeedBytesPerSecond = 0,
                EstimatedTimeRemaining = TimeSpan.Zero,
                ElapsedTime = stopwatch.Elapsed,
                ActiveConnections = 0,
                Segments = []
            });

            return jobInfo;
        }
        catch (OperationCanceledException)
        {
            jobInfo.Status = DownloadStatus.Paused;
            if (_repository != null)
            {
                await _repository.UpdateJobStatusAsync(jobInfo.Id, DownloadStatus.Paused, ct: CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        catch (Exception ex)
        {
            jobInfo.Status = DownloadStatus.Failed;
            if (_repository != null)
            {
                await _repository.UpdateJobStatusAsync(jobInfo.Id, DownloadStatus.Failed, ct: CancellationToken.None).ConfigureAwait(false);
            }
            throw new OmniFetchException($"HLS download session failed: {ex.Message}", ex);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDir))
                {
                    Directory.Delete(stagingDir, recursive: true);
                }
            }
            catch
            {
                // Ignored
            }
        }
    }

    private static HlsStreamVariant? SelectVariant(HlsMasterPlaylist master, int? preferredResolutionHeight)
    {
        if (master.Variants.Count == 0) return null;

        if (preferredResolutionHeight.HasValue)
        {
            var matching = master.Variants
                .Where(v => v.Height.HasValue)
                .OrderBy(v => Math.Abs(v.Height!.Value - preferredResolutionHeight.Value))
                .ThenByDescending(v => v.Bandwidth)
                .FirstOrDefault();

            if (matching != null) return matching;
        }

        return master.GetBestVariant();
    }

    private static void ApplyRequestHeaders(HttpRequestMessage request, DownloadOptions options, DownloadJobInfo jobInfo)
    {
        string? userAgent = !string.IsNullOrWhiteSpace(options.UserAgent) ? options.UserAgent : jobInfo.UserAgent;
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        string? cookies = !string.IsNullOrWhiteSpace(options.Cookies) ? options.Cookies : jobInfo.Cookies;
        if (!string.IsNullOrWhiteSpace(cookies))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookies);
        }

        string? referrer = !string.IsNullOrWhiteSpace(options.Referrer) ? options.Referrer : jobInfo.Referrer;
        if (!string.IsNullOrWhiteSpace(referrer))
        {
            request.Headers.TryAddWithoutValidation("Referer", referrer);
        }
    }
}
