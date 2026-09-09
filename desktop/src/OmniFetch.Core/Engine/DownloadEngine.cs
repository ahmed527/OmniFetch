using System.Collections.Concurrent;
using OmniFetch.Core.Common;
using OmniFetch.Core.Exceptions;
using OmniFetch.Core.Models;
using OmniFetch.Core.Network;
using OmniFetch.Core.Segmentation;
using OmniFetch.Core.Storage;

namespace OmniFetch.Core.Engine;

/// <summary>
/// Main orchestration engine for OmniFetch multi-stream accelerated downloading.
/// Coordinates capability discovery, sparse file pre-allocation, dynamic half-splitting bisection,
/// token-bucket bandwidth shaping, and 4 Hz telemetry dispatch.
/// </summary>
public class DownloadEngine : IDownloadEngine
{
    private readonly IHttpProbeService _probeService;
    private readonly IDiskStorageService _diskStorage;
    private readonly HttpClient _httpClient;

    public event EventHandler<DownloadProgressSnapshot>? ProgressChanged;
    public event EventHandler<(Guid JobId, DownloadStatus Status)>? StatusChanged;
    public event EventHandler<ExpiredUrlException>? ExpiredUrlDetected;

    public DownloadEngine(
        IHttpProbeService? probeService = null,
        IDiskStorageService? diskStorage = null,
        HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SocketsHttpHandlerFactory.CreateClient();
        _probeService = probeService ?? new HttpProbeService(_httpClient);
        _diskStorage = diskStorage ?? new DiskStorageService();
    }

    public async Task<DownloadJobInfo> CreateJobAsync(
        string url,
        string destinationFilePath,
        DownloadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);
        options ??= new DownloadOptions();

        var probe = await _probeService.ProbeAsync(url, options, cancellationToken).ConfigureAwait(false);

        if (probe.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Gone)
        {
            throw new ExpiredUrlException(probe.OriginalUrl, probe.StatusCode, 0, options.Referrer);
        }

        string resolvedPath = ResolveDestinationPath(destinationFilePath, probe.SuggestedFileName);

        return new DownloadJobInfo
        {
            Url = probe.FinalUrl,
            DestinationFilePath = resolvedPath,
            TotalBytes = probe.ContentLength ?? -1,
            ETag = probe.ETag,
            LastModified = probe.LastModified,
            ContentType = probe.ContentType,
            SupportsRange = probe.SupportsRange,
            Cookies = options.Cookies,
            UserAgent = options.UserAgent,
            Referrer = options.Referrer,
            Status = DownloadStatus.Queued
        };
    }

    public async Task<DownloadJobInfo> StartDownloadAsync(
        DownloadJobInfo jobInfo,
        DownloadOptions? options = null,
        IProgress<DownloadProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobInfo);
        options ??= new DownloadOptions();

        jobInfo.Status = DownloadStatus.Downloading;
        NotifyStatus(jobInfo.Id, DownloadStatus.Downloading);

        bool supportsRange = jobInfo.SupportsRange && jobInfo.TotalBytes > 0;
        await ExecuteSessionAsync(jobInfo, options, supportsRange, isResume: false, progress, cancellationToken).ConfigureAwait(false);

        return jobInfo;
    }

    public async Task<DownloadJobInfo> StartDownloadAsync(
        string url,
        string destinationFilePath,
        DownloadOptions? options = null,
        IProgress<DownloadProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var jobInfo = await CreateJobAsync(url, destinationFilePath, options, cancellationToken).ConfigureAwait(false);
        return await StartDownloadAsync(jobInfo, options, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DownloadJobInfo> ResumeDownloadAsync(
        DownloadJobInfo jobInfo,
        DownloadOptions? options = null,
        IProgress<DownloadProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobInfo);
        options ??= new DownloadOptions();

        if (options.ValidateETagOnResume && !string.IsNullOrWhiteSpace(jobInfo.ETag))
        {
            var probe = await _probeService.ProbeAsync(jobInfo.Url, options, cancellationToken).ConfigureAwait(false);
            if (probe.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Gone)
            {
                long downloadedSoFar = jobInfo.Segments.Sum(s => Math.Max(0, s.CurrentByte - s.StartByte));
                throw new ExpiredUrlException(probe.OriginalUrl, probe.StatusCode, downloadedSoFar, options.Referrer);
            }

            if (!string.IsNullOrWhiteSpace(probe.ETag) && !string.Equals(probe.ETag, jobInfo.ETag, StringComparison.Ordinal))
            {
                throw new OmniFetchException($"Remote file has changed on server. Previous ETag: {jobInfo.ETag}, New ETag: {probe.ETag}");
            }
        }

        jobInfo.Status = DownloadStatus.Downloading;
        NotifyStatus(jobInfo.Id, DownloadStatus.Downloading);

        await ExecuteSessionAsync(jobInfo, options, supportsRange: true, isResume: true, progress, cancellationToken).ConfigureAwait(false);

        return jobInfo;
    }

    private async Task ExecuteSessionAsync(
        DownloadJobInfo jobInfo,
        DownloadOptions options,
        bool supportsRange,
        bool isResume,
        IProgress<DownloadProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        // 1. Pre-allocate complete file on disk (eliminates rebuilding phase)
        var fileHandle = _diskStorage.OpenAndPreallocate(jobInfo.DestinationFilePath, Math.Max(0, jobInfo.TotalBytes));

        // 2. Initialize Dynamic Segment Manager
        var segmentManager = new DynamicSegmentManager();
        if (!isResume)
        {
            if (supportsRange && jobInfo.TotalBytes > 0)
            {
                segmentManager.InitializeNewJob(jobInfo.Id, jobInfo.TotalBytes, options.MaxConnections);
            }
            else
            {
                segmentManager.InitializeSingleStream(jobInfo.Id);
            }
        }
        else
        {
            segmentManager.InitializeResumedJob(jobInfo.Id, jobInfo.TotalBytes, jobInfo.Segments);
        }

        var rateLimiter = new TokenBucketRateLimiter(options.SpeedLimitBytesPerSecond);
        await using var session = new DownloadSession(jobInfo, options, fileHandle, segmentManager, rateLimiter, cancellationToken);

        // 3. Start high-precision 4 Hz UI telemetry timer
        using var telemetryCts = new CancellationTokenSource();
        var telemetryTask = RunTelemetryLoopAsync(session, options.ProgressThrottleIntervalMs, progress, telemetryCts.Token);

        // Background monitor to bisect stragglers when worker slots become available
        using var stragglerCts = CancellationTokenSource.CreateLinkedTokenSource(session.CancellationTokenSource.Token);
        Task? stragglerMonitor = null;

        try
        {
            // 4. Dispatch worker tasks
            var initialSegments = segmentManager.GetSegments().Where(s => !s.IsCompleted).ToList();
            if (initialSegments.Count == 0 && segmentManager.IsCompleted)
            {
                jobInfo.Status = DownloadStatus.Completed;
                jobInfo.CompletedAtUtc = DateTime.UtcNow;
                NotifyStatus(jobInfo.Id, DownloadStatus.Completed);
                return;
            }

            var worker = new SegmentWorker(_httpClient, _diskStorage, rateLimiter, options);

            void StartWorker(DownloadSegmentState initialSegment)
            {
                var workerId = Guid.NewGuid();
                var task = Task.Run(async () =>
                {
                    var currentSegment = initialSegment;
                    try
                    {
                        while (!session.CancellationTokenSource.IsCancellationRequested)
                        {
                            await worker.ExecuteAsync(
                                fileHandle,
                                jobInfo.Url,
                                currentSegment,
                                jobInfo.ETag,
                                session.CancellationTokenSource.Token
                            ).ConfigureAwait(false);

                            if (segmentManager.IsCompleted)
                            {
                                break;
                            }

                            // 1. Check for unassigned incomplete segments
                            if (segmentManager.TryGetUnassignedSegment(out var unassignedSegment) && unassignedSegment != null)
                            {
                                currentSegment = unassignedSegment;
                            }
                            // 2. Dynamic Bisection Work-Stealing:
                            // When a segment finishes, steal half the workload from the largest remaining segment
                            else if (segmentManager.TryBisectLargestSegment(options.MinSplitThresholdBytes, out var bisectedSegment) 
                                && bisectedSegment != null)
                            {
                                currentSegment = bisectedSegment;
                            }
                            else
                            {
                                break; // No work remaining
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        // Cancel peer workers immediately on failure to conserve bandwidth
                        await session.CancellationTokenSource.CancelAsync().ConfigureAwait(false);
                        throw;
                    }
                    finally
                    {
                        if (!currentSegment.IsCompleted)
                        {
                            currentSegment.IsAssigned = false;
                        }
                        session.ActiveWorkerTasks.TryRemove(workerId, out _);
                    }
                });

                session.ActiveWorkerTasks[workerId] = task;
            }

            // Dispatch initial workers up to MaxConnections
            int maxInitial = Math.Min(options.MaxConnections, initialSegments.Count);
            for (int i = 0; i < maxInitial; i++)
            {
                var seg = initialSegments[i];
                seg.IsAssigned = true;
                StartWorker(seg);
            }

            stragglerMonitor = RunStragglerMonitorAsync(session, StartWorker, options, stragglerCts.Token);

            // Dynamically wait for all active workers to complete or fault
            while (!session.ActiveWorkerTasks.IsEmpty)
            {
                var currentTasks = session.ActiveWorkerTasks.Values.ToArray();
                if (currentTasks.Length == 0) break;

                var completedTask = await Task.WhenAny(currentTasks).ConfigureAwait(false);
                if (completedTask.IsCanceled || session.CancellationTokenSource.IsCancellationRequested)
                {
                    throw new OperationCanceledException(session.CancellationTokenSource.Token);
                }

                if (completedTask.IsFaulted && completedTask.Exception != null)
                {
                    var inner = completedTask.Exception.InnerExceptions.Count == 1
                        ? completedTask.Exception.InnerExceptions[0]
                        : completedTask.Exception;
                    throw inner;
                }
            }

            if (session.CancellationTokenSource.IsCancellationRequested)
            {
                throw new OperationCanceledException(session.CancellationTokenSource.Token);
            }

            // 5. Finalize download
            _diskStorage.Flush(fileHandle);

            if (segmentManager.IsCompleted)
            {
                long finalDownloadedBytes = segmentManager.TotalDownloadedBytes;
                if (jobInfo.TotalBytes <= 0)
                {
                    jobInfo.TotalBytes = finalDownloadedBytes;
                }

                jobInfo.Status = DownloadStatus.Completed;
                jobInfo.CompletedAtUtc = DateTime.UtcNow;
                jobInfo.Segments = [.. segmentManager.GetSegments()];
                NotifyStatus(jobInfo.Id, DownloadStatus.Completed);

                // Send 100% completion snapshot
                var finalSnapshot = session.GenerateSnapshot();
                progress?.Report(finalSnapshot);
                ProgressChanged?.Invoke(this, finalSnapshot);
            }
        }
        catch (OperationCanceledException)
        {
            jobInfo.Status = DownloadStatus.Paused;
            jobInfo.Segments = [.. segmentManager.GetSegments()];
            NotifyStatus(jobInfo.Id, DownloadStatus.Paused);
            throw new DownloadPausedException(jobInfo, cancellationToken);
        }
        catch (ExpiredUrlException ex)
        {
            jobInfo.Status = DownloadStatus.Expired;
            jobInfo.Segments = [.. segmentManager.GetSegments()];
            NotifyStatus(jobInfo.Id, DownloadStatus.Expired);
            ExpiredUrlDetected?.Invoke(this, ex);
            throw;
        }
        catch (Exception)
        {
            jobInfo.Status = DownloadStatus.Failed;
            jobInfo.Segments = [.. segmentManager.GetSegments()];
            NotifyStatus(jobInfo.Id, DownloadStatus.Failed);
            throw;
        }
        finally
        {
            await telemetryCts.CancelAsync();
            try { await telemetryTask.ConfigureAwait(false); } catch { /* Ignore cancellation */ }

            await stragglerCts.CancelAsync();
            if (stragglerMonitor != null)
            {
                try { await stragglerMonitor.ConfigureAwait(false); } catch { /* Ignore cancellation */ }
            }
        }
    }

    private static async Task RunStragglerMonitorAsync(
        DownloadSession session,
        Action<DownloadSegmentState> startWorkerAction,
        DownloadOptions options,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested 
               && !session.CancellationTokenSource.IsCancellationRequested 
               && !session.SegmentManager.IsCompleted)
        {
            try
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (cancellationToken.IsCancellationRequested || session.CancellationTokenSource.IsCancellationRequested)
            {
                break;
            }

            // If we have capacity under MaxConnections
            if (session.ActiveWorkerTasks.Count < options.MaxConnections)
            {
                // 1. Check for unassigned incomplete segments first
                if (session.SegmentManager.TryGetUnassignedSegment(out var unassignedSegment) && unassignedSegment != null)
                {
                    startWorkerAction(unassignedSegment);
                }
                // 2. Otherwise bisect the largest active segment
                else if (session.SegmentManager.TryBisectLargestSegment(options.MinSplitThresholdBytes, out var newSegment)
                    && newSegment != null)
                {
                    startWorkerAction(newSegment);
                }
            }
        }
    }

    private async Task RunTelemetryLoopAsync(
        DownloadSession session,
        int intervalMs,
        IProgress<DownloadProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        intervalMs = Math.Max(50, intervalMs);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(intervalMs, cancellationToken).ConfigureAwait(false);
                var snapshot = session.GenerateSnapshot();
                progress?.Report(snapshot);
                ProgressChanged?.Invoke(this, snapshot);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void NotifyStatus(Guid jobId, DownloadStatus status)
    {
        StatusChanged?.Invoke(this, (jobId, status));
    }

    private static string ResolveDestinationPath(string destinationPath, string? suggestedFileName)
    {
        if (Directory.Exists(destinationPath) || destinationPath.EndsWith(Path.DirectorySeparatorChar) || destinationPath.EndsWith('/'))
        {
            string fileName = string.IsNullOrWhiteSpace(suggestedFileName) ? "download.bin" : suggestedFileName;
            return Path.Combine(destinationPath, fileName);
        }

        return destinationPath;
    }
}
