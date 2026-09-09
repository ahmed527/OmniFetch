using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using OmniFetch.App.Models;
using OmniFetch.App.Services;
using OmniFetch.Core.Common;
using OmniFetch.Core.Settings;

namespace OmniFetch.App.ViewModels;

public partial class AddDownloadViewModel : ObservableObject
{
    private readonly ISettingsService? _settingsService;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private string _destinationPath = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _cookies = string.Empty;

    [ObservableProperty]
    private CategoryFilterType _category = CategoryFilterType.All;

    [ObservableProperty]
    private bool _downloadNow = true;

    [ObservableProperty]
    private bool _isConfirmed;

    [ObservableProperty]
    private string _fileSizeText = "Unknown";

    public Func<Task>? RequestCloseHandler { get; set; }

    public AddDownloadViewModel(ISettingsService? settingsService = null)
    {
        _settingsService = settingsService;
        string defaultDownloads = _settingsService?.GetSaveDirectoryForCategory("General") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        DestinationPath = defaultDownloads;
    }

    public async Task CheckClipboardForUrlAsync()
    {
        try
        {
            if (Clipboard.Default.HasText)
            {
                string text = await Clipboard.Default.GetTextAsync() ?? string.Empty;
                text = text.Trim();
                if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    Url = text;
                    UpdateDerivedFields(text);
                }
            }
        }
        catch
        {
            // Ignore clipboard errors
        }
    }

    partial void OnUrlChanged(string value)
    {
        UpdateDerivedFields(value);
    }

    partial void OnCategoryChanged(CategoryFilterType value)
    {
        if (_settingsService != null)
        {
            string catDir = _settingsService.GetSaveDirectoryForCategory(value.ToString());
            if (!string.IsNullOrWhiteSpace(catDir))
            {
                DestinationPath = catDir;
            }
        }
    }

    public void SetExplicitFileDetails(string url, string? suggestedFileName, string? cookies, long? fileSize = null)
    {
        Url = url;
        if (!string.IsNullOrWhiteSpace(cookies))
        {
            Cookies = cookies;
        }

        if (fileSize.HasValue && fileSize.Value > 0)
        {
            FileSizeText = DownloadItemViewModel.FormatBytes(fileSize.Value);
        }

        if (!string.IsNullOrWhiteSpace(suggestedFileName))
        {
            FileName = MimeTypeMap.SanitizeAndEnsureExtension(suggestedFileName, null, url);
            Category = DownloadItemViewModel.DeduceCategory(FileName);
        }
        else
        {
            UpdateDerivedFields(url);
        }

        if (_settingsService != null)
        {
            DestinationPath = _settingsService.GetSaveDirectoryForCategory(Category.ToString());
        }
    }

    private void UpdateDerivedFields(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                string name = Path.GetFileName(uri.AbsolutePath);
                name = MimeTypeMap.SanitizeAndEnsureExtension(name, null, url);

                if (!string.IsNullOrWhiteSpace(name))
                {
                    FileName = name;
                    Category = DownloadItemViewModel.DeduceCategory(name);

                    if (_settingsService != null)
                    {
                        DestinationPath = _settingsService.GetSaveDirectoryForCategory(Category.ToString());
                    }
                }
            }
        }
        catch
        {
            // Ignore parse errors
        }
    }

    [RelayCommand]
    public async Task BrowseFolderAsync()
    {
        string? picked = await FolderPickerHelper.PickFolderAsync(DestinationPath);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            DestinationPath = picked;
        }
    }

    [RelayCommand]
    public async Task StartDownloadAsync()
    {
        if (string.IsNullOrWhiteSpace(Url)) return;
        DownloadNow = true;
        IsConfirmed = true;
        if (RequestCloseHandler != null)
        {
            await RequestCloseHandler.Invoke();
        }
    }

    [RelayCommand]
    public async Task DownloadLaterAsync()
    {
        if (string.IsNullOrWhiteSpace(Url)) return;
        DownloadNow = false;
        IsConfirmed = true;
        if (RequestCloseHandler != null)
        {
            await RequestCloseHandler.Invoke();
        }
    }

    [RelayCommand]
    public async Task CancelAsync()
    {
        IsConfirmed = false;
        if (RequestCloseHandler != null)
        {
            await RequestCloseHandler.Invoke();
        }
    }
}
