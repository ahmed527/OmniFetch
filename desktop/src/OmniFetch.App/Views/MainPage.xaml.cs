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
    private const double RowHeightApprox = 38.0;
    private bool _isPointerDown;
    private bool _isDragging;
    private Point _dragStartPoint;
    private bool _hasDragStartPoint;
    private DownloadItemViewModel? _dragAnchorItem;

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

    private void OnDownloadRowTapped(object? sender, TappedEventArgs e)
    {
        if (_isDragging) return;
        if (sender is VisualElement visual && visual.BindingContext is DownloadItemViewModel item)
        {
            _viewModel.SelectDownload(item, isToggle: false, isRange: false);
        }
    }

    private void OnDownloadRowPointerPressed(object? sender, PointerEventArgs e)
    {
        if (sender is VisualElement visual && visual.BindingContext is DownloadItemViewModel item)
        {
            _isPointerDown = true;
            _isDragging = false;
            _dragAnchorItem = item;

            // Capture exact click coordinates relative to ListContainer
            var pos = e.GetPosition(ListContainer);
            if (pos.HasValue)
            {
                _dragStartPoint = pos.Value;
                _hasDragStartPoint = true;
            }
            else
            {
                int anchorIdx = _viewModel.FilteredDownloads.IndexOf(item);
                double rowTop = anchorIdx >= 0 ? anchorIdx * RowHeightApprox : 0;
                var localPos = e.GetPosition(visual);
                double localX = localPos?.X ?? 100;
                double localY = localPos?.Y ?? (RowHeightApprox / 2.0);
                _dragStartPoint = new Point(localX, rowTop + localY);
                _hasDragStartPoint = true;
            }

            // Immediate feedback on mouse press (left or right click)
            if (!item.IsSelected)
            {
                _viewModel.SelectDownload(item, isToggle: false, isRange: false);
            }
            else
            {
                _viewModel.SelectedDownload = item;
            }
        }
    }

    private void OnTablePointerPressed(object? sender, PointerEventArgs e)
    {
        _isPointerDown = true;
        _isDragging = false;
        _dragAnchorItem = null;

        var pos = e.GetPosition(ListContainer);
        if (pos.HasValue)
        {
            _dragStartPoint = pos.Value;
            _hasDragStartPoint = true;
        }
        else
        {
            _dragStartPoint = new Point(0, 0);
            _hasDragStartPoint = false;
        }
    }

    private void OnDownloadRowPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_isPointerDown && _dragAnchorItem != null)
        {
            if (sender is VisualElement visual && visual.BindingContext is DownloadItemViewModel item)
            {
                if (_isDragging || item != _dragAnchorItem)
                {
                    _isDragging = true;
                    _viewModel.SelectRange(_dragAnchorItem, item, keepExisting: false);
                }
            }
        }
    }

    private void OnTablePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPointerDown) return;

        var pos = e.GetPosition(ListContainer);
        if (!pos.HasValue) return;

        double currentX = pos.Value.X;
        double currentY = pos.Value.Y;

        if (!_isDragging)
        {
            if (!_hasDragStartPoint)
            {
                _dragStartPoint = new Point(currentX, currentY);
                _hasDragStartPoint = true;
            }

            double deltaX = Math.Abs(currentX - _dragStartPoint.X);
            double deltaY = Math.Abs(currentY - _dragStartPoint.Y);
            if (deltaX > 3 || deltaY > 3)
            {
                _isDragging = true;
            }
        }

        if (_isDragging)
        {
            UpdateMarqueeGeometry(currentX, currentY);
        }
    }

    private void OnTablePanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _isDragging = true;
                break;

            case GestureStatus.Running:
                _isDragging = true;

                if (!_hasDragStartPoint)
                {
                    double anchorY = _dragAnchorItem != null
                        ? Math.Max(0, _viewModel.FilteredDownloads.IndexOf(_dragAnchorItem) * RowHeightApprox)
                        : 0;
                    _dragStartPoint = new Point(30, anchorY);
                    _hasDragStartPoint = true;
                }

                double currentX = _dragStartPoint.X + e.TotalX;
                double currentY = _dragStartPoint.Y + e.TotalY;

                UpdateMarqueeGeometry(currentX, currentY);
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                FinishDrag();
                break;
        }
    }

    private void UpdateMarqueeGeometry(double currentX, double currentY)
    {
        if (!_hasDragStartPoint)
        {
            _dragStartPoint = new Point(currentX, currentY);
            _hasDragStartPoint = true;
        }

        // Clamp coordinates to container bounds (non-negative)
        double startX = Math.Max(0, _dragStartPoint.X);
        double startY = Math.Max(0, _dragStartPoint.Y);
        double curX = Math.Max(0, currentX);
        double curY = Math.Max(0, currentY);

        double minX = Math.Min(startX, curX);
        double minY = Math.Min(startY, curY);
        double maxX = Math.Max(startX, curX);
        double maxY = Math.Max(startY, curY);

        double width = Math.Max(2, maxX - minX);
        double height = Math.Max(2, maxY - minY);

        MarqueeSelectionBox.Margin = new Thickness(minX, minY, 0, 0);
        MarqueeSelectionBox.WidthRequest = width;
        MarqueeSelectionBox.HeightRequest = height;
        MarqueeSelectionBox.IsVisible = true;

        UpdateMarqueeSelection(startY, curY);
    }

    private void UpdateMarqueeSelection(double anchorY, double cursorY)
    {
        if (_viewModel.FilteredDownloads.Count == 0) return;

        double totalItemsHeight = _viewModel.FilteredDownloads.Count * RowHeightApprox;
        double minY = Math.Min(anchorY, cursorY);
        double maxY = Math.Max(anchorY, cursorY);
        
        // If the marquee is completely outside the item area, clear selection (unless anchor item exists)
        if (minY >= totalItemsHeight || maxY <= 0)
        {
            if (_dragAnchorItem == null)
            {
                _viewModel.ClearDownloadsSelection();
            }
            return;
        }

        int anchorIdx = Math.Clamp((int)(anchorY / RowHeightApprox), 0, _viewModel.FilteredDownloads.Count - 1);
        int cursorIdx = Math.Clamp((int)(cursorY / RowHeightApprox), 0, _viewModel.FilteredDownloads.Count - 1);

        _viewModel.SelectRangeByIndex(anchorIdx, cursorIdx);
    }

    private void FinishDrag()
    {
        bool wasDragging = _isDragging;
        var anchor = _dragAnchorItem;

        _isPointerDown = false;
        _hasDragStartPoint = false;
        _dragAnchorItem = null;
        HideMarqueeBox();

        if (!wasDragging && anchor == null)
        {
            // Clicked in empty space without dragging -> deselect all (like Windows Explorer / Finder)
            _viewModel.ClearDownloadsSelection();
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await Task.Delay(50);
            _isDragging = false;
        });
    }

    private void OnDownloadRowPointerReleased(object? sender, PointerEventArgs e)
    {
        FinishDrag();
    }

    private void OnTablePointerReleased(object? sender, PointerEventArgs e)
    {
        FinishDrag();
    }

    private void HideMarqueeBox()
    {
        if (MarqueeSelectionBox != null)
        {
            MarqueeSelectionBox.IsVisible = false;
            MarqueeSelectionBox.WidthRequest = 0;
            MarqueeSelectionBox.HeightRequest = 0;
            MarqueeSelectionBox.Margin = new Thickness(0);
        }
    }

    private void OnDownloadsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel == null || e.CurrentSelection == null) return;
        var selectedItems = e.CurrentSelection.OfType<DownloadItemViewModel>().ToList();
        _viewModel.SyncSelectionFromUi(selectedItems);
    }

    private async void OnDownloadRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        var target = (sender as VisualElement)?.BindingContext as DownloadItemViewModel ?? _viewModel.SelectedDownload;
        if (target != null)
        {
            await OpenProgressDialogAsync(target);
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
