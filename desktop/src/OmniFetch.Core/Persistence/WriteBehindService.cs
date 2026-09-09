using System.Collections.Concurrent;
using OmniFetch.Core.Common;
using OmniFetch.Core.Segmentation;

namespace OmniFetch.Core.Persistence;

/// <summary>
/// Background write-behind progress flusher for SQLite state persistence.
/// Flushes thread-safe segment memory accumulators to the database in batched transactions every 2,000ms.
/// </summary>
public class WriteBehindService : IWriteBehindService
{
    private readonly IDownloadRepository _repository;
    private readonly int _flushIntervalMs;
    private readonly ConcurrentDictionary<Guid, ISegmentManager> _activeSessions = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushLoopTask;
    private bool _disposed;

    public WriteBehindService(IDownloadRepository repository, int flushIntervalMs = Constants.DefaultWriteBehindFlushIntervalMs)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _flushIntervalMs = Math.Max(100, flushIntervalMs);
        _flushLoopTask = Task.Run(RunFlushLoopAsync);
    }

    public void RegisterSession(Guid jobId, ISegmentManager segmentManager)
    {
        ArgumentNullException.ThrowIfNull(segmentManager);
        _activeSessions[jobId] = segmentManager;
    }

    public void UnregisterSession(Guid jobId)
    {
        _activeSessions.TryRemove(jobId, out _);
    }

    public async Task FlushJobAsync(Guid jobId, CancellationToken ct = default)
    {
        if (_activeSessions.TryGetValue(jobId, out var segmentManager))
        {
            var segments = segmentManager.GetSegments();
            if (segments.Count > 0)
            {
                await _repository.UpdateSegmentProgressAsync(jobId, segments, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task FlushAllAsync(CancellationToken ct = default)
    {
        if (_activeSessions.IsEmpty) return;

        var tasks = _activeSessions.Select(async kvp =>
        {
            try
            {
                var segments = kvp.Value.GetSegments();
                if (segments.Count > 0)
                {
                    await _repository.UpdateSegmentProgressAsync(kvp.Key, segments, ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // Best-effort flush
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task RunFlushLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_flushIntervalMs));
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
                {
                    break;
                }

                await FlushAllAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Suppress background flush exceptions to prevent crashing the host
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _flushLoopTask.ConfigureAwait(false);
        }
        catch
        {
            // Ignore cancellation on shutdown
        }

        // Final atomic flush of all active sessions
        try
        {
            using var finalCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await FlushAllAsync(finalCts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Ignore final flush errors on dispose
        }

        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
