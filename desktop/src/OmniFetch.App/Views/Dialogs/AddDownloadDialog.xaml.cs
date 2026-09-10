using System;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using OmniFetch.App.Models;
using OmniFetch.App.ViewModels;

namespace OmniFetch.App.Views.Dialogs;

public partial class AddDownloadDialog : ContentPage
{
    private readonly AddDownloadViewModel _viewModel;
    private TaskCompletionSource<AddDownloadParams?>? _tcs;
    private INavigation? _parentNav;

    public AddDownloadDialog(AddDownloadViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        _viewModel.RequestCloseHandler = async () =>
        {
            await DismissAsync(_viewModel.IsConfirmed);
        };
    }

    private bool _isDismissed;

    public async Task<AddDownloadParams?> ShowModalAsync(INavigation nav)
    {
        _parentNav = nav;
        _isDismissed = false;
        _tcs = new TaskCompletionSource<AddDownloadParams?>();
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            await _parentNav.PushModalAsync(this, false);
        });
        return await _tcs.Task;
    }

    private async Task DismissAsync(bool confirmed)
    {
        if (_isDismissed) return;
        _isDismissed = true;

        if (confirmed && !string.IsNullOrWhiteSpace(_viewModel.Url))
        {
            _tcs?.TrySetResult(new AddDownloadParams(_viewModel.Url, _viewModel.DestinationPath, _viewModel.FileName, _viewModel.Cookies));
        }
        else
        {
            _tcs?.TrySetResult(null);
        }

        try
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                try
                {
                    if (_parentNav != null && _parentNav.ModalStack.Contains(this))
                    {
                        await _parentNav.PopModalAsync(false);
                    }
                    else if (Navigation != null && Navigation.ModalStack.Contains(this))
                    {
                        await Navigation.PopModalAsync(false);
                    }
                    else
                    {
                        var appNav = Application.Current?.Windows.Count > 0 ? Application.Current.Windows[0].Page?.Navigation : null;
                        if (appNav != null && appNav.ModalStack.Contains(this))
                        {
                            await appNav.PopModalAsync(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AddDownloadDialog] PopModalAsync error: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AddDownloadDialog] DismissAsync error: {ex.Message}");
        }
    }

    private async void OnStartDownloadClicked(object? sender, EventArgs e)
    {
        _viewModel.DownloadNow = true;
        _viewModel.IsConfirmed = true;
        await DismissAsync(true);
    }

    private async void OnDownloadLaterClicked(object? sender, EventArgs e)
    {
        _viewModel.DownloadNow = false;
        _viewModel.IsConfirmed = true;
        await DismissAsync(true);
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        _viewModel.IsConfirmed = false;
        await DismissAsync(false);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (string.IsNullOrWhiteSpace(_viewModel.Url))
        {
            await _viewModel.CheckClipboardForUrlAsync();
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Safety net: if dismissed via external platform gesture without clicking a button
        if (!_isDismissed)
        {
            _isDismissed = true;
            _tcs?.TrySetResult(null);
        }
    }
}
