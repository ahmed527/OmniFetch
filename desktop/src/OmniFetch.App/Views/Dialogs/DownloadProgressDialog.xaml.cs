using System;
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
            await Navigation.PopModalAsync();
        };
    }

    private async void OnPauseResumeClicked(object? sender, EventArgs e)
    {
        await _viewModel.PauseResumeAsync();
    }
}
