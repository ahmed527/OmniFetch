using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using OmniFetch.App.Models;

namespace OmniFetch.App.ViewModels;

public partial class AddDownloadViewModel : ObservableObject
{
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

    public Func<Task>? RequestCloseHandler { get; set; }

    public AddDownloadViewModel()
    {
        string defaultDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
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

    private void UpdateDerivedFields(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                string name = Path.GetFileName(uri.AbsolutePath);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    FileName = name;
                    Category = DownloadItemViewModel.DeduceCategory(name);
                }
            }
        }
        catch
        {
            // Ignore parse errors
        }
    }

    [RelayCommand]
    public async Task OkAsync()
    {
        if (string.IsNullOrWhiteSpace(Url)) return;
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
