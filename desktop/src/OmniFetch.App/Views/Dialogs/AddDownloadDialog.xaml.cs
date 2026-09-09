using System;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using OmniFetch.App.ViewModels;

namespace OmniFetch.App.Views.Dialogs;

public partial class AddDownloadDialog : ContentPage
{
    private readonly AddDownloadViewModel _viewModel;

    public AddDownloadDialog(AddDownloadViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _viewModel.RequestCloseHandler = async () =>
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                try
                {
                    if (Navigation != null && Navigation.ModalStack.Count > 0)
                    {
                        await Navigation.PopModalAsync(false);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AddDownloadDialog] Local nav PopModalAsync error: {ex.Message}");
                }

                try
                {
                    var nav = Application.Current?.Windows.Count > 0 ? Application.Current.Windows[0].Page?.Navigation : null;
                    if (nav != null && nav.ModalStack.Count > 0)
                    {
                        await nav.PopModalAsync(false);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AddDownloadDialog] App nav PopModalAsync error: {ex.Message}");
                }
            });
        };
    }

    private void OnStartDownloadClicked(object? sender, EventArgs e)
    {
        if (_viewModel.StartDownloadCommand.CanExecute(null))
        {
            _viewModel.StartDownloadCommand.Execute(null);
        }
    }

    private void OnDownloadLaterClicked(object? sender, EventArgs e)
    {
        if (_viewModel.DownloadLaterCommand.CanExecute(null))
        {
            _viewModel.DownloadLaterCommand.Execute(null);
        }
    }

    private void OnCancelClicked(object? sender, EventArgs e)
    {
        if (_viewModel.CancelCommand.CanExecute(null))
        {
            _viewModel.CancelCommand.Execute(null);
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (string.IsNullOrWhiteSpace(_viewModel.Url))
        {
            await _viewModel.CheckClipboardForUrlAsync();
        }
    }
}
