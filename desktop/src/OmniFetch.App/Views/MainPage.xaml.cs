using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using OmniFetch.App.ViewModels;
using OmniFetch.App.Views.Dialogs;

namespace OmniFetch.App.Views;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;

    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;

        _viewModel.RequestAddUrlHandler = PromptAddDownloadAsync;
        _viewModel.RequestOpenProgressDialogHandler = OpenProgressDialogAsync;
        _viewModel.ShowAlertHandler = async (title, message) =>
        {
            await DisplayAlertAsync(title, message, "OK");
        };
    }

    private async Task<AddDownloadParams?> PromptAddDownloadAsync()
    {
        var addVm = new AddDownloadViewModel();
        var dialog = new AddDownloadDialog(addVm);
        await Navigation.PushModalAsync(dialog);

        // Wait until dismissed
        while (Navigation.ModalStack.Contains(dialog))
        {
            await Task.Delay(100);
        }

        if (addVm.IsConfirmed && !string.IsNullOrWhiteSpace(addVm.Url))
        {
            return new AddDownloadParams(addVm.Url, addVm.DestinationPath, addVm.FileName, addVm.Cookies);
        }

        return null;
    }

    private async Task OpenProgressDialogAsync(DownloadItemViewModel item)
    {
        var progressVm = new DownloadProgressViewModel(item, _viewModel);
        var dialog = new DownloadProgressDialog(progressVm);
        await Navigation.PushModalAsync(dialog);
    }

    private async void OnDownloadRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel.SelectedDownload != null)
        {
            await OpenProgressDialogAsync(_viewModel.SelectedDownload);
        }
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        _viewModel.Dispose();
        Environment.Exit(0);
    }

    private async void OnAboutClicked(object? sender, EventArgs e)
    {
        await DisplayAlertAsync(
            "About OmniFetch", 
            "OmniFetch Download Accelerator v1.0\nBuilt for Apple Silicon (macOS ARM64)\nMulti-Stream Lock-Free Engine with IDM Experience", 
            "OK");
    }
}
