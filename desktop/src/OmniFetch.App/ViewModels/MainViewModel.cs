using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.ApplicationModel;
using OmniFetch.App.Models;
using OmniFetch.App.Platforms.MacCatalyst;
using OmniFetch.Core.Common;
using OmniFetch.Core.Engine;
using OmniFetch.Core.Exceptions;
using OmniFetch.Core.Ipc;
using OmniFetch.Core.Media;
using OmniFetch.Core.Models;
using OmniFetch.Core.Persistence;

namespace OmniFetch.App.ViewModels;

public record AddDownloadParams(string Url, string? DestinationPath, string? FileName, string? Cookies);

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IDownloadEngine _engine;
    private readonly IIpcServer _ipcServer;
    private readonly IDownloadRepository? _repository;
    private readonly IActivityLockService? _activityLockService;
    private readonly IHlsDownloadManager? _hlsDownloadManager;

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeTokens = new();
    private IDisposable? _activeTransferLock;

    public ObservableCollection<DownloadItemViewModel> AllDownloads { get; } = [];
    public ObservableCollection<DownloadItemViewModel> FilteredDownloads { get; } = [];
    public ObservableCollection<CategoryItem> Categories { get; } = [];

    [ObservableProperty]
    private CategoryItem? _selectedCategory;

    [ObservableProperty]
    private DownloadItemViewModel? _selectedDownload;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _statusBarText = "Ready";

    [ObservableProperty]
    private string _totalSpeedText = "0 KB/s";

    [ObservableProperty]
    private int _activeDownloadsCount;

    public Func<Task<AddDownloadParams?>>? RequestAddUrlHandler { get; set; }
    public Func<DownloadItemViewModel, Task>? RequestOpenProgressDialogHandler { get; set; }
    public Func<string, string, Task>? ShowAlertHandler { get; set; }

    public MainViewModel(
        IDownloadEngine engine,
        IIpcServer ipcServer,
        IDownloadRepository? repository = null,
        IActivityLockService? activityLockService = null,
        IHlsDownloadManager? hlsDownloadManager = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _ipcServer = ipcServer ?? throw new ArgumentNullException(nameof(ipcServer));
        _repository = repository;
        _activityLockService = activityLockService;
        _hlsDownloadManager = hlsDownloadManager;

        InitializeCategories();

        _engine.ProgressChanged += OnEngineProgressChanged;
        _engine.StatusChanged += OnEngineStatusChanged;
        _engine.ExpiredUrlDetected += OnEngineExpiredUrlDetected;

        _ipcServer.OnDownloadRequested += HandleIpcDownloadAsync;
        _ipcServer.OnUrlRefreshed += HandleIpcRefreshUrlAsync;
        _ipcServer.OnPing += HandleIpcPingAsync;

        _ = Task.Run(InitializeAsync);
    }

    private void InitializeCategories()
    {
        Categories.Add(new CategoryItem("All Downloads", "cat_all.svg", CategoryFilterType.All) { IsSelected = true });
        Categories.Add(new CategoryItem("Compressed", "cat_compressed.svg", CategoryFilterType.Compressed));
        Categories.Add(new CategoryItem("Documents", "cat_documents.svg", CategoryFilterType.Documents));
        Categories.Add(new CategoryItem("Music", "cat_music.svg", CategoryFilterType.Music));
        Categories.Add(new CategoryItem("Programs", "cat_programs.svg", CategoryFilterType.Programs));
        Categories.Add(new CategoryItem("Video", "cat_video.svg", CategoryFilterType.Video));
        Categories.Add(new CategoryItem("Queues", "cat_queues.svg", CategoryFilterType.Queues));
        Categories.Add(new CategoryItem("Unfinished", "cat_unfinished.svg", CategoryFilterType.Unfinished));
        Categories.Add(new CategoryItem("Finished", "cat_finished.svg", CategoryFilterType.Finished));

        SelectedCategory = Categories[0];
    }

    public async Task InitializeAsync()
    {
        try
        {
            // Start IPC server
            await _ipcServer.StartAsync(CancellationToken.None).ConfigureAwait(false);

            // Load saved jobs from database with batch UI dispatch
            if (_repository != null)
            {
                var jobs = await _repository.GetAllJobsAsync().ConfigureAwait(false);
                var loadedItems = new List<DownloadItemViewModel>();
                foreach (var job in jobs)
                {
                    var item = new DownloadItemViewModel(job.Id, job.Url, job.DestinationFilePath, job.TotalBytes);
                    item.SetStatus(job.Status);
                    long downloaded = job.Segments.Sum(s => Math.Max(0, s.CurrentByte - s.StartByte));
                    item.DownloadedBytes = downloaded;
                    if (job.TotalBytes > 0)
                    {
                        item.ProgressPercentage = (double)downloaded / job.TotalBytes * 100.0;
                        item.ProgressFraction = (double)downloaded / job.TotalBytes;
                    }
                    loadedItems.Add(item);
                }

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    foreach (var item in loadedItems)
                    {
                        AllDownloads.Add(item);
                    }
                    ApplyCategoryFilter();
                    UpdateCategoryCounts();
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MainViewModel.InitializeAsync] Error: {ex.Message}");
        }
    }

    [RelayCommand]
    public void SelectCategory(CategoryItem? category)
    {
        if (category == null) return;

        foreach (var cat in Categories)
        {
            cat.IsSelected = (cat == category);
        }

        SelectedCategory = category;
        ApplyCategoryFilter();
    }

    private void ApplyCategoryFilter()
    {
        if (SelectedCategory == null) return;

        FilteredDownloads.Clear();
        var filter = SelectedCategory.FilterType;

        var matching = AllDownloads.Where(d => filter switch
        {
            CategoryFilterType.All => true,
            CategoryFilterType.Compressed => d.Category == CategoryFilterType.Compressed,
            CategoryFilterType.Documents => d.Category == CategoryFilterType.Documents,
            CategoryFilterType.Music => d.Category == CategoryFilterType.Music,
            CategoryFilterType.Programs => d.Category == CategoryFilterType.Programs,
            CategoryFilterType.Video => d.Category == CategoryFilterType.Video,
            CategoryFilterType.Unfinished => d.Status is DownloadStatus.Downloading or DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Queued or DownloadStatus.Expired,
            CategoryFilterType.Finished => d.Status == DownloadStatus.Completed,
            CategoryFilterType.Queues => d.Status == DownloadStatus.Queued,
            _ => true
        });

        foreach (var item in matching)
        {
            FilteredDownloads.Add(item);
        }
    }

    private void UpdateCategoryCounts()
    {
        foreach (var cat in Categories)
        {
            cat.Count = AllDownloads.Count(d => cat.FilterType switch
            {
                CategoryFilterType.All => true,
                CategoryFilterType.Compressed => d.Category == CategoryFilterType.Compressed,
                CategoryFilterType.Documents => d.Category == CategoryFilterType.Documents,
                CategoryFilterType.Music => d.Category == CategoryFilterType.Music,
                CategoryFilterType.Programs => d.Category == CategoryFilterType.Programs,
                CategoryFilterType.Video => d.Category == CategoryFilterType.Video,
                CategoryFilterType.Unfinished => d.Status is DownloadStatus.Downloading or DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Queued or DownloadStatus.Expired,
                CategoryFilterType.Finished => d.Status == DownloadStatus.Completed,
                CategoryFilterType.Queues => d.Status == DownloadStatus.Queued,
                _ => true
            });
        }
    }

    [RelayCommand]
    public async Task AddDownloadAsync()
    {
        if (RequestAddUrlHandler == null) return;

        var result = await RequestAddUrlHandler.Invoke();
        if (result == null || string.IsNullOrWhiteSpace(result.Url)) return;

        string? customDest = null;
        if (!string.IsNullOrWhiteSpace(result.DestinationPath) && !string.IsNullOrWhiteSpace(result.FileName))
        {
            customDest = Path.Combine(result.DestinationPath, result.FileName);
        }

        await StartNewDownloadUrlAsync(result.Url, customDest, result.Cookies);
    }

    public async Task<DownloadItemViewModel?> StartNewDownloadUrlAsync(
        string url, 
        string? customDestination = null, 
        string? cookies = null, 
        string? userAgent = null, 
        string? referrer = null)
    {
        try
        {
            string downloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            string fileName = Path.GetFileName(new Uri(url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "download_" + DateTime.Now.Ticks;

            string destPath = customDestination ?? Path.Combine(downloadsDir, fileName);

            var options = new DownloadOptions
            {
                Cookies = cookies,
                UserAgent = userAgent,
                Referrer = referrer
            };

            bool isHls = url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
            if (isHls && _hlsDownloadManager != null)
            {
                if (!destPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && !destPath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                {
                    destPath = Path.ChangeExtension(destPath, ".mp4");
                }

                var job = new DownloadJobInfo
                {
                    Url = url,
                    DestinationFilePath = destPath,
                    TotalBytes = -1,
                    ContentType = "application/x-mpegURL",
                    SupportsRange = false,
                    Cookies = cookies,
                    UserAgent = userAgent,
                    Referrer = referrer,
                    Status = DownloadStatus.Queued
                };

                if (_repository != null)
                {
                    await _repository.AddJobAsync(job).ConfigureAwait(false);
                }

                var item = new DownloadItemViewModel(job.Id, job.Url, job.DestinationFilePath, job.TotalBytes);

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    AllDownloads.Insert(0, item);
                    ApplyCategoryFilter();
                    UpdateCategoryCounts();
                    SelectedDownload = item;
                });

                var cts = new CancellationTokenSource();
                _activeTokens[job.Id] = cts;
                var progressHandler = new Progress<DownloadProgressSnapshot>(snapshot => OnEngineProgressChanged(this, snapshot));
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _hlsDownloadManager.DownloadHlsJobAsync(job, options, progress: progressHandler, ct: cts.Token).ConfigureAwait(false);
                        OnEngineStatusChanged(this, (job.Id, DownloadStatus.Completed));
                    }
                    catch (OperationCanceledException)
                    {
                        OnEngineStatusChanged(this, (job.Id, DownloadStatus.Paused));
                    }
                    catch (Exception)
                    {
                        OnEngineStatusChanged(this, (job.Id, DownloadStatus.Failed));
                    }
                    finally
                    {
                        _activeTokens.TryRemove(job.Id, out _);
                        MainThread.BeginInvokeOnMainThread(UpdateAggregateMetrics);
                    }
                });

                if (RequestOpenProgressDialogHandler != null)
                {
                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        await RequestOpenProgressDialogHandler.Invoke(item);
                    });
                }

                return item;
            }

            var regularJob = await _engine.CreateJobAsync(url, destPath, options).ConfigureAwait(false);
            var regularItem = new DownloadItemViewModel(regularJob.Id, regularJob.Url, regularJob.DestinationFilePath, regularJob.TotalBytes);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                AllDownloads.Insert(0, regularItem);
                ApplyCategoryFilter();
                UpdateCategoryCounts();
                SelectedDownload = regularItem;
            });

            var regCts = new CancellationTokenSource();
            _activeTokens[regularJob.Id] = regCts;
            _ = Task.Run(() => _engine.StartDownloadAsync(regularJob, options, cancellationToken: regCts.Token));

            if (RequestOpenProgressDialogHandler != null)
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    await RequestOpenProgressDialogHandler.Invoke(regularItem);
                });
            }

            return regularItem;
        }
        catch (Exception ex)
        {
            if (ShowAlertHandler != null)
            {
                await ShowAlertHandler.Invoke("Download Error", $"Failed to start download: {ex.Message}");
            }
            return null;
        }
    }

    [RelayCommand]
    public async Task ResumeAsync()
    {
        if (SelectedDownload == null || !SelectedDownload.CanResume) return;

        var download = SelectedDownload;
        download.SetStatus(DownloadStatus.Downloading);

        var cts = new CancellationTokenSource();
        _activeTokens[download.JobId] = cts;
        await Task.Run(() => _engine.ResumeDownloadAsync(download.JobId, cancellationToken: cts.Token));
    }

    [RelayCommand]
    public async Task StopAsync()
    {
        if (SelectedDownload == null || !SelectedDownload.CanPause) return;

        var download = SelectedDownload;
        if (_activeTokens.TryRemove(download.JobId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
        download.SetStatus(DownloadStatus.Paused);
        await Task.CompletedTask;
    }

    [RelayCommand]
    public async Task StopAllAsync()
    {
        var active = AllDownloads.Where(d => d.IsDownloading).ToList();
        foreach (var d in active)
        {
            if (_activeTokens.TryRemove(d.JobId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
            d.SetStatus(DownloadStatus.Paused);
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    public async Task StartQueueAsync()
    {
        var paused = AllDownloads.Where(d => d.CanResume).ToList();
        foreach (var d in paused)
        {
            d.SetStatus(DownloadStatus.Downloading);
            var cts = new CancellationTokenSource();
            _activeTokens[d.JobId] = cts;
            _ = Task.Run(() => _engine.ResumeDownloadAsync(d.JobId, cancellationToken: cts.Token));
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    public async Task StopQueueAsync()
    {
        await StopAllAsync();
    }

    [RelayCommand]
    public async Task DeleteAsync()
    {
        if (SelectedDownload == null) return;

        var download = SelectedDownload;
        if (download.IsDownloading && _activeTokens.TryRemove(download.JobId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        if (_repository != null)
        {
            await _repository.DeleteJobAsync(download.JobId);
        }

        AllDownloads.Remove(download);
        ApplyCategoryFilter();
        UpdateCategoryCounts();
        SelectedDownload = null;
    }

    [RelayCommand]
    public async Task DeleteCompletedAsync()
    {
        var completed = AllDownloads.Where(d => d.Status == DownloadStatus.Completed).ToList();
        foreach (var d in completed)
        {
            if (_repository != null)
            {
                await _repository.DeleteJobAsync(d.JobId);
            }
            AllDownloads.Remove(d);
        }

        ApplyCategoryFilter();
        UpdateCategoryCounts();
    }

    [RelayCommand]
    public async Task OpenFolderAsync()
    {
        if (SelectedDownload == null) return;

        string path = SelectedDownload.DestinationFilePath;
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            await Launcher.OpenAsync(new OpenFileRequest
            {
                File = new ReadOnlyFile(Directory.Exists(path) ? path : folder)
            });
        }
    }

    [RelayCommand]
    public async Task OpenFileAsync()
    {
        if (SelectedDownload == null) return;

        string path = SelectedDownload.DestinationFilePath;
        if (File.Exists(path))
        {
            await Launcher.OpenAsync(new OpenFileRequest
            {
                File = new ReadOnlyFile(path)
            });
        }
    }

    [RelayCommand]
    public async Task OptionsAsync()
    {
        if (ShowAlertHandler != null)
        {
            await ShowAlertHandler.Invoke("OmniFetch Options", "OmniFetch is configured with maximum 8 parallel streams per download, lock-free zero-stitch direct disk I/O, and Chrome extension integration enabled.");
        }
    }

    [RelayCommand]
    public async Task GrabberAsync()
    {
        if (ShowAlertHandler != null)
        {
            await ShowAlertHandler.Invoke("OmniFetch Site Grabber", "OmniFetch Chrome Extension is active. Whenever a video or audio stream is played in Chrome, the floating 'Download this video' grabber appears automatically.");
        }
    }

    private void OnEngineProgressChanged(object? sender, DownloadProgressSnapshot snapshot)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var item = AllDownloads.FirstOrDefault(d => d.JobId == snapshot.JobId);
            if (item != null)
            {
                item.UpdateFromSnapshot(snapshot);
            }

            UpdateAggregateMetrics();
        });
    }

    private void OnEngineStatusChanged(object? sender, (Guid JobId, DownloadStatus Status) args)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var item = AllDownloads.FirstOrDefault(d => d.JobId == args.JobId);
            if (item != null)
            {
                item.SetStatus(args.Status);
                if (args.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Paused or DownloadStatus.Expired)
                {
                    if (_activeTokens.TryRemove(args.JobId, out var cts))
                    {
                        cts.Dispose();
                    }
                }
                ApplyCategoryFilter();
                UpdateCategoryCounts();
            }

            UpdateAggregateMetrics();
        });
    }

    private void OnEngineExpiredUrlDetected(object? sender, ExpiredUrlException ex)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (ShowAlertHandler != null)
            {
                await ShowAlertHandler.Invoke("Link Expired", $"The download link has expired (HTTP {ex.StatusCode}). Please refresh the link in your browser to continue.");
            }
        });
    }

    private void UpdateAggregateMetrics()
    {
        int activeCount = AllDownloads.Count(d => d.IsDownloading);
        ActiveDownloadsCount = activeCount;

        double totalSpeed = AllDownloads.Where(d => d.IsDownloading).Sum(d => d.SpeedBytesPerSecond);
        TotalSpeedText = DownloadItemViewModel.FormatBytes(totalSpeed) + "/s";

        if (activeCount > 0)
        {
            StatusBarText = $"Downloading: {activeCount} active transfer(s) at {TotalSpeedText}";

            if (_activeTransferLock == null && _activityLockService != null)
            {
                _activeTransferLock = _activityLockService.AcquireTransferLock("OmniFetch Active Downloads");
            }
        }
        else
        {
            StatusBarText = "Ready";

            if (_activeTransferLock != null)
            {
                _activeTransferLock.Dispose();
                _activeTransferLock = null;
            }
        }
    }

    private Task<IpcResponse> HandleIpcDownloadAsync(NativeDownloadRequest request)
    {
        return Task.Run(async () =>
        {
            try
            {
                string? customDest = null;
                if (!string.IsNullOrWhiteSpace(request.SuggestedFileName))
                {
                    string downloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    customDest = Path.Combine(downloadsDir, request.SuggestedFileName);
                }

                var item = await StartNewDownloadUrlAsync(
                    request.Url,
                    customDest,
                    request.Cookies,
                    request.UserAgent,
                    request.Referrer);

                if (item != null)
                {
                    return IpcResponse.Accepted(item.JobId, item.FileName, "Download accepted by OmniFetch");
                }
                return IpcResponse.Error("FAILED_TO_CREATE", "Could not start download");
            }
            catch (Exception ex)
            {
                return IpcResponse.Error("INTERNAL_ERROR", ex.Message);
            }
        });
    }

    private Task<IpcResponse> HandleIpcRefreshUrlAsync(NativeRefreshUrlRequest request)
    {
        return Task.Run(async () =>
        {
            try
            {
                if (_repository != null)
                {
                    await _repository.UpdateJobUrlAsync(request.JobId, request.NewUrl, request.Cookies);
                }
                var cts = new CancellationTokenSource();
                _activeTokens[request.JobId] = cts;
                _ = Task.Run(() => _engine.ResumeDownloadAsync(request.JobId, cancellationToken: cts.Token));
                return IpcResponse.Accepted(request.JobId, null, "URL refreshed and download resumed");
            }
            catch (Exception ex)
            {
                return IpcResponse.Error("REFRESH_FAILED", ex.Message);
            }
        });
    }

    private Task<IpcResponse> HandleIpcPingAsync(NativePingRequest request)
    {
        return Task.FromResult(IpcResponse.Ok("OmniFetch daemon online"));
    }

    public void Dispose()
    {
        _engine.ProgressChanged -= OnEngineProgressChanged;
        _engine.StatusChanged -= OnEngineStatusChanged;
        _engine.ExpiredUrlDetected -= OnEngineExpiredUrlDetected;

        _ipcServer.OnDownloadRequested -= HandleIpcDownloadAsync;
        _ipcServer.OnUrlRefreshed -= HandleIpcRefreshUrlAsync;
        _ipcServer.OnPing -= HandleIpcPingAsync;

        foreach (var kvp in _activeTokens)
        {
            try
            {
                kvp.Value.Cancel();
                kvp.Value.Dispose();
            }
            catch { }
        }
        _activeTokens.Clear();

        _activeTransferLock?.Dispose();
        _activeTransferLock = null;
    }
}
