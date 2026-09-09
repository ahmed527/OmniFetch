using System;
using Microsoft.Maui.Controls;
using OmniFetch.App.ViewModels;

namespace OmniFetch.App.Views.Dialogs;

public partial class AddDownloadDialog : ContentPage
{
    private readonly AddDownloadViewModel _viewModel;

    public AddDownloadDialog(AddDownloadViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _viewModel.RequestCloseHandler = async () =>
        {
            await Navigation.PopModalAsync();
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.CheckClipboardForUrlAsync();
    }

    private void OnBrowseClicked(object? sender, EventArgs e)
    {
        // Keep current path or set standard downloads
        if (string.IsNullOrWhiteSpace(_viewModel.DestinationPath))
        {
            _viewModel.DestinationPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/Downloads";
        }
    }
}
