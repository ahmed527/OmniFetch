using System;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using OmniFetch.App.ViewModels;

namespace OmniFetch.App.Views.Dialogs;

public partial class DownloadProgressDialog : ContentPage
{
    private readonly DownloadProgressViewModel _viewModel;

    public DownloadProgressDialog(DownloadProgressViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _viewModel.RequestCloseHandler = async () =>
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                try
                {
                    if (Navigation.ModalStack.Count > 0)
                    {
                        await Navigation.PopModalAsync(true);
                    }
                }
                catch
                {
                    try
                    {
                        var nav = Application.Current?.Windows.Count > 0 ? Application.Current.Windows[0].Page?.Navigation : null;
                        if (nav != null && nav.ModalStack.Count > 0)
                        {
                            await nav.PopModalAsync(true);
                        }
                    }
                    catch { /* Ignore */ }
                }
            });
        };
    }

    private async void OnPauseResumeClicked(object? sender, EventArgs e)
    {
        await _viewModel.PauseResumeAsync();
    }
}
