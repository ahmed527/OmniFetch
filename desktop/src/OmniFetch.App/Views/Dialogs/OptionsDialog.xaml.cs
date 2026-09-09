using System;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using OmniFetch.App.ViewModels;

namespace OmniFetch.App.Views.Dialogs;

public partial class OptionsDialog : ContentPage
{
    private readonly OptionsViewModel _viewModel;

    public OptionsDialog(OptionsViewModel viewModel)
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
                    Console.WriteLine($"[OptionsDialog] Error popping modal via local Navigation: {ex.Message}");
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
                    Console.WriteLine($"[OptionsDialog] Error popping modal via Application Navigation: {ex.Message}");
                }
            });
        };
    }

    private async void OnOkClicked(object? sender, EventArgs e)
    {
        await _viewModel.SaveAsync();
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        await _viewModel.CancelAsync();
    }
}
