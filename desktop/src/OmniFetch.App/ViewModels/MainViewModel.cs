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
using OmniFetch.App.Services;
using OmniFetch.Core.Common;
using OmniFetch.Core.Engine;
using OmniFetch.Core.Exceptions;
using OmniFetch.Core.Ipc;
using OmniFetch.Core.Media;
using OmniFetch.Core.Models;
using OmniFetch.Core.Persistence;
using OmniFetch.Core.Settings;

namespace OmniFetch.App.ViewModels;

public record AddDownloadParams(string Url, string? DestinationPath, string? FileName, string? Cookies);

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IDownloadEngine _engine;
    private readonly IIpcServer _ipcServer;
    private readonly IDownloadRepository? _repository;
    private readonly IActivityLockService? _activityLockService;
    private readonly IHlsDownloadManager? _hlsDownloadManager;
    private readonly ISettingsService? _settingsService;

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeTokens = new();
    private IDisposable? _activeTransferLock;

    public ObservableCollection<DownloadItemViewModel> AllDownloads { get; } = [];
    public ObservableCollection<DownloadItemViewModel> FilteredDownloads { get; } = [];
    public ObservableCollection<CategoryItem> Categories { get; } = [];

    public OmniFetchSettings CurrentSettings => _settingsService?.GetSettings() ?? new OmniFetchSettings();

    [ObservableProperty]
    private CategoryItem? _selectedCategory;

    [ObservableProperty]
    private DownloadItemViewModel? _selectedDownload;

    public ObservableCollection<DownloadItemViewModel> SelectedDownloads { get; } = [];

    [ObservableProperty]
    private ObservableCollection<object> _selectedDownloadsList = [];

    private bool _isSyncingSelection = false;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _statusBarText = "Ready";

    [ObservableProperty]
    private string _totalSpeedText = "0 KB/s";

    [ObservableProperty]
    private int _activeDownloadsCount;

    public Func<Task<AddDownloadParams?>>? RequestAddUrlHandler { get; set; }
    public Func<NativeDownloadRequest, Task<AddDownloadParams?>>? RequestIpcPromptHandler { get; set; }
    public Func<DownloadItemViewModel, Task>? RequestOpenProgressDialogHandler { get; set; }
    public Func<Task>? RequestOpenOptionsDialogHandler { get; set; }
    public Func<string, string, Task>? ShowAlertHandler { get; set; }
    public Func<string, Task<string>>? RequestDeleteConfirmationHandler { get; set; }

    public MainViewModel(
        IDownloadEngine engine,
        IIpcServer ipcServer,
        IDownloadRepository? repository = null,
        IActivityLockService? activityLockService = null,
        IHlsDownloadManager? hlsDownloadManager = null,
        ISettingsService? settingsService = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _ipcServer = ipcServer ?? throw new ArgumentNullException(nameof(ipcServer));
        _repository = repository;
        _activityLockService = activityLockService;
        _hlsDownloadManager = hlsDownloadManager;
        _settingsService = settingsService;

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
        Categories.Add(new CategoryItem("All Downloads", "cat_all.png", CategoryFilterType.All) { IsSelected = true });
        Categories.Add(new CategoryItem("Compressed", "cat_compressed.png", CategoryFilterType.Compressed));
        Categories.Add(new CategoryItem("Documents", "cat_documents.png", CategoryFilterType.Documents));
        Categories.Add(new CategoryItem("Music", "cat_music.png", CategoryFilterType.Music));
        Categories.Add(new CategoryItem("Programs", "cat_programs.png", CategoryFilterType.Programs));
        Categories.Add(new CategoryItem("Video", "cat_video.png", CategoryFilterType.Video));
        Categories.Add(new CategoryItem("Queues", "cat_queues.png", CategoryFilterType.Queues));
        Categories.Add(new CategoryItem("Unfinished", "cat_unfinished.png", CategoryFilterType.Unfinished));
        Categories.Add(new CategoryItem("Finished", "cat_finished.png", CategoryFilterType.Finished));

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

    public void SelectDownload(DownloadItemViewModel item, bool isToggle = false, bool isRange = false)
    {
        if (isRange && SelectedDownload != null)
        {
            int idx1 = FilteredDownloads.IndexOf(SelectedDownload);
            int idx2 = FilteredDownloads.IndexOf(item);
            if (idx1 != -1 && idx2 != -1)
            {
                int start = Math.Min(idx1, idx2);
                int end = Math.Max(idx1, idx2);

                if (!isToggle)
                {
                    foreach (var d in AllDownloads)
                    {
                        d.IsSelected = false;
                    }
                    SelectedDownloads.Clear();
                }

                for (int i = start; i <= end; i++)
                {
                    var d = FilteredDownloads[i];
                    d.IsSelected = true;
                    if (!SelectedDownloads.Contains(d))
                    {
                        SelectedDownloads.Add(d);
                    }
                }
                SelectedDownload = item;
                SyncSelectedDownloadsList();
                return;
            }
        }

        if (isToggle)
        {
            item.IsSelected = !item.IsSelected;
            if (item.IsSelected)
            {
                if (!SelectedDownloads.Contains(item))
                {
                    SelectedDownloads.Add(item);
                }
                SelectedDownload = item;
            }
            else
            {
                SelectedDownloads.Remove(item);
                if (SelectedDownload == item)
                {
                    SelectedDownload = SelectedDownloads.LastOrDefault();
                }
            }
            SyncSelectedDownloadsList();
            return;
        }

        // Single selection (standard left-click)
        foreach (var d in AllDownloads)
        {
            if (d != item)
            {
                d.IsSelected = false;
            }
        }
        item.IsSelected = true;
        SelectedDownloads.Clear();
        SelectedDownloads.Add(item);
        SelectedDownload = item;
        SyncSelectedDownloadsList();
    }

    public void HandleRowRightClick(DownloadItemViewModel item)
    {
        // If already part of multi-selection, preserve selection and activate this item
        if (item.IsSelected)
        {
            SelectedDownload = item;
            return;
        }

        // Otherwise, select solely this item
        SelectDownload(item, isToggle: false, isRange: false);
    }

    public void SelectRange(DownloadItemViewModel startItem, DownloadItemViewModel endItem, bool keepExisting = false)
    {
        int idx1 = FilteredDownloads.IndexOf(startItem);
        int idx2 = FilteredDownloads.IndexOf(endItem);
        if (idx1 == -1 || idx2 == -1) return;

        SelectRangeByIndex(idx1, idx2, keepExisting);
    }

    public void SelectRangeByIndex(int startIndex, int endIndex, bool keepExisting = false)
    {
        if (FilteredDownloads.Count == 0) return;

        int start = Math.Clamp(Math.Min(startIndex, endIndex), 0, FilteredDownloads.Count - 1);
        int end = Math.Clamp(Math.Max(startIndex, endIndex), 0, FilteredDownloads.Count - 1);

        if (!keepExisting)
        {
            foreach (var d in AllDownloads)
            {
                d.IsSelected = false;
            }
            SelectedDownloads.Clear();
        }

        for (int i = start; i <= end; i++)
        {
            var d = FilteredDownloads[i];
            d.IsSelected = true;
            if (!SelectedDownloads.Contains(d))
            {
                SelectedDownloads.Add(d);
            }
        }

        SelectedDownload = FilteredDownloads[Math.Clamp(endIndex, 0, FilteredDownloads.Count - 1)];
        SyncSelectedDownloadsList();
    }

    [RelayCommand]
    public void SelectAllDownloads()
    {
        foreach (var d in FilteredDownloads)
        {
            d.IsSelected = true;
            if (!SelectedDownloads.Contains(d))
            {
                SelectedDownloads.Add(d);
            }
        }
        SelectedDownload = FilteredDownloads.LastOrDefault();
        SyncSelectedDownloadsList();
    }

    [RelayCommand]
    public void ClearDownloadsSelection()
    {
        foreach (var d in AllDownloads)
        {
            d.IsSelected = false;
        }
        SelectedDownloads.Clear();
        SelectedDownload = null;
        SyncSelectedDownloadsList();
    }

    public void SyncSelectionFromUi(IEnumerable<DownloadItemViewModel> selection)
    {
        if (_isSyncingSelection) return;

        var selectedSet = new HashSet<DownloadItemViewModel>(selection);
        foreach (var d in FilteredDownloads)
        {
            d.IsSelected = selectedSet.Contains(d);
        }

        SelectedDownloads.Clear();
        foreach (var d in selectedSet)
        {
            SelectedDownloads.Add(d);
        }
        SelectedDownload = SelectedDownloads.LastOrDefault();
    }

    private void SyncSelectedDownloadsList()
    {
        _isSyncingSelection = true;
        try
        {
            SelectedDownloadsList.Clear();
            foreach (var d in SelectedDownloads)
            {
                SelectedDownloadsList.Add(d);
            }
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    public List<DownloadItemViewModel> GetSelectedOrActiveDownloads()
    {
        var items = FilteredDownloads.Where(d => d.IsSelected).ToList();
        if (items.Count > 0) return items;
        if (SelectedDownload != null) return [SelectedDownload];
        return [];
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
            if (url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
            {
                if (ShowAlertHandler != null)
                {
                    await ShowAlertHandler.Invoke("Unsupported URL", "Browser blob: URLs cannot be transferred directly outside the browser. OmniFetch captures the underlying video stream automatically.");
                }
                return null;
            }

            string fileName = "download_" + DateTime.Now.Ticks;
            if (Uri.TryCreate(url, UriKind.Absolute, out var parsedUri))
            {
                var extracted = Path.GetFileName(parsedUri.AbsolutePath);
                if (!string.IsNullOrWhiteSpace(extracted))
                {
                    fileName = extracted;
                }
            }

            fileName = MimeTypeMap.SanitizeAndEnsureExtension(fileName, null, url);
            var category = DownloadItemViewModel.DeduceCategory(fileName);
            string defaultCategoryDir = _settingsService?.GetSaveDirectoryForCategory(category.ToString()) ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

            string destPath = customDestination ?? Path.Combine(defaultCategoryDir, fileName);
            string targetDir = Path.GetDirectoryName(destPath) ?? defaultCategoryDir;
            string targetFile = MimeTypeMap.SanitizeAndEnsureExtension(Path.GetFileName(destPath), null, url);
            destPath = Path.Combine(targetDir, targetFile);

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

            try
            {
                File.AppendAllText(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Logs/OmniFetch/omnifetch.log"),
                    $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff UTC}] [MAINVM] StartNewDownloadUrlAsync: URL={url}, Dest={destPath}\n");
            }
            catch { }

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
            try
            {
                File.AppendAllText(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Logs/OmniFetch/omnifetch.log"),
                    $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff UTC}] [MAINVM] StartNewDownloadUrlAsync EXCEPTION: {ex}\n");
            }
            catch { }

            if (ShowAlertHandler != null)
            {
                await ShowAlertHandler.Invoke("Download Error", $"Failed to start download: {ex.Message}");
            }
            return null;
        }
    }

    [RelayCommand]
    public async Task ResumeAsync(DownloadItemViewModel? item = null)
    {
        List<DownloadItemViewModel> targets;
        if (item != null && !item.IsSelected)
        {
            targets = [item];
        }
        else
        {
            targets = GetSelectedOrActiveDownloads();
            if (targets.Count == 0 && item != null)
            {
                targets = [item];
            }
        }

        var resumable = targets.Where(d => d.CanResume).ToList();
        if (resumable.Count == 0) return;

        foreach (var download in resumable)
        {
            download.SetStatus(DownloadStatus.Downloading);
            var cts = new CancellationTokenSource();
            _activeTokens[download.JobId] = cts;
            _ = Task.Run(() => _engine.ResumeDownloadAsync(download.JobId, cancellationToken: cts.Token));
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    public async Task StopAsync(DownloadItemViewModel? item = null)
    {
        List<DownloadItemViewModel> targets;
        if (item != null && !item.IsSelected)
        {
            targets = [item];
        }
        else
        {
            targets = GetSelectedOrActiveDownloads();
            if (targets.Count == 0 && item != null)
            {
                targets = [item];
            }
        }

        var pausable = targets.Where(d => d.CanPause).ToList();
        if (pausable.Count == 0) return;

        foreach (var download in pausable)
        {
            if (_activeTokens.TryRemove(download.JobId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
            download.SetStatus(DownloadStatus.Paused);
        }
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
    public async Task DeleteAsync(DownloadItemViewModel? item = null)
    {
        List<DownloadItemViewModel> targets;
        if (item != null && !item.IsSelected)
        {
            targets = [item];
        }
        else
        {
            targets = GetSelectedOrActiveDownloads();
            if (targets.Count == 0 && item != null)
            {
                targets = [item];
            }
        }

        if (targets.Count == 0) return;

        string promptDesc = targets.Count == 1 
            ? targets[0].FileName 
            : $"{targets.Count} selected downloads";

        string choice = "Delete from List & Disk";
        if (RequestDeleteConfirmationHandler != null)
        {
            choice = await RequestDeleteConfirmationHandler.Invoke(promptDesc);
            if (string.IsNullOrWhiteSpace(choice) || choice.Equals("Cancel", StringComparison.OrdinalIgnoreCase))
            {
                return; // User cancelled
            }
        }

        bool deleteFromDisk = choice.Contains("Disk", StringComparison.OrdinalIgnoreCase);

        foreach (var download in targets)
        {
            // Cancel active download if currently running
            if (download.IsDownloading && _activeTokens.TryRemove(download.JobId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }

            // Permanently delete physical file from disk if requested
            if (deleteFromDisk)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(download.DestinationFilePath) && File.Exists(download.DestinationFilePath))
                    {
                        File.Delete(download.DestinationFilePath);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MainViewModel.DeleteAsync] Could not delete physical file: {ex.Message}");
                }
            }

            if (_repository != null)
            {
                await _repository.DeleteJobAsync(download.JobId);
            }

            AllDownloads.Remove(download);
            SelectedDownloads.Remove(download);
            SelectedDownloadsList.Remove(download);
        }

        ApplyCategoryFilter();
        UpdateCategoryCounts();

        if (SelectedDownload != null && !AllDownloads.Contains(SelectedDownload))
        {
            SelectedDownload = SelectedDownloads.LastOrDefault();
        }
    }

    [RelayCommand]
    public async Task DeleteCompletedAsync()
    {
        var completed = AllDownloads.Where(d => d.Status == DownloadStatus.Completed).ToList();
        if (completed.Count == 0) return;

        string choice = "Delete from List & Disk";
        if (RequestDeleteConfirmationHandler != null)
        {
            choice = await RequestDeleteConfirmationHandler.Invoke($"{completed.Count} completed downloads");
            if (string.IsNullOrWhiteSpace(choice) || choice.Equals("Cancel", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        bool deleteFromDisk = choice.Contains("Disk", StringComparison.OrdinalIgnoreCase);

        foreach (var d in completed)
        {
            if (deleteFromDisk)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(d.DestinationFilePath) && File.Exists(d.DestinationFilePath))
                    {
                        File.Delete(d.DestinationFilePath);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MainViewModel.DeleteCompletedAsync] Could not delete file {d.DestinationFilePath}: {ex.Message}");
                }
            }

            if (_repository != null)
            {
                await _repository.DeleteJobAsync(d.JobId);
            }
            AllDownloads.Remove(d);
        }

        ApplyCategoryFilter();
        UpdateCategoryCounts();
        SelectedDownload = null;
    }

    [RelayCommand]
    public void RevealInFinder(DownloadItemViewModel? item = null)
    {
        List<DownloadItemViewModel> targets;
        if (item != null && !item.IsSelected)
        {
            targets = [item];
        }
        else
        {
            targets = GetSelectedOrActiveDownloads();
            if (targets.Count == 0 && item != null)
            {
                targets = [item];
            }
        }

        foreach (var target in targets)
        {
            FolderPickerHelper.RevealInFinder(target.DestinationFilePath);
        }
    }

    [RelayCommand]
    public void OpenFolder(DownloadItemViewModel? item = null)
    {
        var target = item ?? SelectedDownload;
        if (target == null) return;

        string path = target.DestinationFilePath;
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            FolderPickerHelper.OpenFolder(folder);
        }
    }

    [RelayCommand]
    public void OpenFile(DownloadItemViewModel? item = null)
    {
        var target = item ?? SelectedDownload;
        if (target == null) return;

        FolderPickerHelper.OpenFile(target.DestinationFilePath);
    }

    [RelayCommand]
    public async Task OptionsAsync()
    {
        if (RequestOpenOptionsDialogHandler != null)
        {
            await RequestOpenOptionsDialogHandler.Invoke();
        }
        else if (ShowAlertHandler != null)
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

    private static void BringAppToForeground()
    {
        try
        {
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = "-a OmniFetch",
                    UseShellExecute = false,
                    CreateNoWindow = true
                })?.Dispose();
            }
        }
        catch { }
    }

    private Task<IpcResponse> HandleIpcDownloadAsync(NativeDownloadRequest request)
    {
        return Task.Run(async () =>
        {
            try
            {
                var settings = _settingsService?.GetSettings() ?? new OmniFetchSettings();

                string sanitizedFileName = MimeTypeMap.SanitizeAndEnsureExtension(request.SuggestedFileName, request.MimeType, request.Url);
                var category = DownloadItemViewModel.DeduceCategory(sanitizedFileName);
                string targetDir = _settingsService?.GetSaveDirectoryForCategory(category.ToString()) ?? settings.DefaultDownloadDirectory;

                // Bring desktop application to front immediately
                BringAppToForeground();

                if (settings.ShowStartDialog && RequestIpcPromptHandler != null)
                {
                    // Decouple UI modal prompt from IPC socket response to prevent Native Messaging timeout.
                    // The socket server returns Accepted immediately; UI modal runs asynchronously.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var promptResult = await RequestIpcPromptHandler.Invoke(request);
                            if (promptResult != null)
                            {
                                string customDest;
                                if (!string.IsNullOrWhiteSpace(promptResult.DestinationPath) && !string.IsNullOrWhiteSpace(promptResult.FileName))
                                {
                                    customDest = Path.Combine(promptResult.DestinationPath, promptResult.FileName);
                                }
                                else
                                {
                                    customDest = Path.Combine(targetDir, sanitizedFileName);
                                }

                                await StartNewDownloadUrlAsync(
                                    request.Url,
                                    customDest,
                                    promptResult.Cookies ?? request.Cookies,
                                    request.UserAgent,
                                    request.Referrer);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[MainViewModel.HandleIpcDownloadAsync] Prompt error: {ex.Message}");
                        }
                    });

                    return IpcResponse.Accepted(Guid.NewGuid(), sanitizedFileName, "Download queued in OmniFetch");
                }
                else
                {
                    string customDest = Path.Combine(targetDir, sanitizedFileName);
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
            }
            catch (Exception ex)
            {
                return IpcResponse.Error("INTERNAL_ERROR", ex.Message);
            }
        });
    }

    public async Task UpdateDownloadPathAsync(Guid jobId, string newFilePath)
    {
        try
        {
            if (_repository != null)
            {
                await _repository.UpdateJobDestinationAsync(jobId, newFilePath).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MainViewModel.UpdateDownloadPathAsync] Error: {ex.Message}");
        }
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
