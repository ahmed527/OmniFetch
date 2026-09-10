using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using OmniFetch.App.ViewModels;
using OmniFetch.App.Views.Dialogs;
using OmniFetch.Core.Ipc;
using OmniFetch.Core.Settings;

namespace OmniFetch.App.Views;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;
    private readonly ISettingsService? _settingsService;

    public MainPage(MainViewModel viewModel, ISettingsService? settingsService = null)
    {
        InitializeComponent();
        _settingsService = settingsService;
        BindingContext = _viewModel = viewModel;

        _viewModel.RequestAddUrlHandler = PromptAddDownloadAsync;
        _viewModel.RequestIpcPromptHandler = PromptIpcDownloadAsync;
        _viewModel.RequestOpenProgressDialogHandler = OpenProgressDialogAsync;
        _viewModel.RequestOpenOptionsDialogHandler = OpenOptionsDialogAsync;
        _viewModel.ShowAlertHandler = async (title, message) =>
        {
            await DisplayAlertAsync(title, message, "OK");
        };
        _viewModel.RequestDeleteConfirmationHandler = async (targetDesc) =>
        {
            return await DisplayActionSheetAsync(
                $"Delete {targetDesc}?",
                "Cancel",
                "Delete from List & Disk",
                "Delete from List Only");
        };

        SizeChanged += OnPageSizeChanged;
    }

    private void OnPageSizeChanged(object? sender, EventArgs e)
    {
        AdjustLayout(Width, Height);
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        AdjustLayout(width, height);
    }

    private void AdjustLayout(double width, double height)
    {
        if (height <= 0 || width <= 0) return;

        double toolbarH = ToolbarBorder.Height > 0 ? ToolbarBorder.Height : 52.0;
        double statusH = StatusBarGrid.Height > 0 ? StatusBarGrid.Height : 26.0;
        double middleH = Math.Max(200.0, height - toolbarH - statusH);

        RootGrid.RowDefinitions[1].Height = new GridLength(middleH, GridUnitType.Absolute);
        RootGrid.HeightRequest = height;
        RootGrid.WidthRequest = width;
    }

    private async Task<AddDownloadParams?> PromptAddDownloadAsync()
    {
        var nav = Navigation;
        if (nav == null && Application.Current?.Windows.Count > 0)
        {
            nav = Application.Current.Windows[0].Page?.Navigation;
        }

        if (nav == null) return null;

        var addVm = new AddDownloadViewModel(_settingsService);
        var dialog = new AddDownloadDialog(addVm);
        return await dialog.ShowModalAsync(nav);
    }

    private async Task<AddDownloadParams?> PromptIpcDownloadAsync(NativeDownloadRequest request)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Logs/OmniFetch/omnifetch.log"),
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff UTC}] [MAINPAGE] PromptIpcDownloadAsync: URL={request.Url}, File={request.SuggestedFileName}\n");
        }
        catch { }

        var nav = Navigation;
        if (nav == null && Application.Current?.Windows.Count > 0)
        {
            nav = Application.Current.Windows[0].Page?.Navigation;
        }

        if (nav == null) return null;

        var addVm = new AddDownloadViewModel(_settingsService);
        addVm.SetExplicitFileDetails(request.Url, request.SuggestedFileName, request.Cookies, request.FileSize);

        var dialog = new AddDownloadDialog(addVm);
        var result = await dialog.ShowModalAsync(nav);

        try
        {
            File.AppendAllText(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Logs/OmniFetch/omnifetch.log"),
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff UTC}] [MAINPAGE] PromptIpcDownloadAsync finished: Confirmed={result != null}\n");
        }
        catch { }

        return result;
    }

    private async Task OpenProgressDialogAsync(DownloadItemViewModel item)
    {
        try
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var progressVm = new DownloadProgressViewModel(item, _viewModel);
                var dialog = new DownloadProgressDialog(progressVm);
                await Navigation.PushModalAsync(dialog);
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MainPage.OpenProgressDialogAsync] Error: {ex.Message}");
        }
    }

    public async Task OpenOptionsDialogPublicAsync()
    {
        await OpenOptionsDialogAsync();
    }

    private async void OnOptionsButtonClicked(object? sender, EventArgs e)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Logs/OmniFetch/omnifetch.log"),
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff UTC}] [MAINPAGE] OnOptionsButtonClicked fired\n");
        }
        catch { }
        await OpenOptionsDialogAsync();
    }

    private async Task OpenOptionsDialogAsync()
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Logs/OmniFetch/omnifetch.log"),
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff UTC}] [MAINPAGE] OpenOptionsDialogAsync entered\n");
        }
        catch { }

        var settingsService = _settingsService ?? new SettingsService();
        try
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var nav = Navigation;
                if (nav == null && Application.Current?.Windows.Count > 0)
                {
                    nav = Application.Current.Windows[0].Page?.Navigation;
                }

                if (nav == null)
                {
                    try
                    {
                        File.AppendAllText(
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Logs/OmniFetch/omnifetch.log"),
                            $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff UTC}] [MAINPAGE] Navigation is null\n");
                    }
                    catch { }
                    return;
                }

                if (nav.ModalStack.Count > 0)
                {
                    try
                    {
                        File.AppendAllText(
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Logs/OmniFetch/omnifetch.log"),
                            $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff UTC}] [MAINPAGE] Modal already open: count={nav.ModalStack.Count}\n");
                    }
                    catch { }
                    return;
                }

                var optionsVm = new OptionsViewModel(settingsService);
                var dialog = new OptionsDialog(optionsVm);
                await nav.PushModalAsync(dialog, true);
                try
                {
                    File.AppendAllText(
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Logs/OmniFetch/omnifetch.log"),
                        $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff UTC}] [MAINPAGE] Options dialog PushModalAsync succeeded\n");
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Logs/OmniFetch/omnifetch.log"),
                    $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff UTC}] [MAINPAGE] OpenOptionsDialogAsync EXCEPTION: {ex}\n");
            }
            catch { }
        }
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
