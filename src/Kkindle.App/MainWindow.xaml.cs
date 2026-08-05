using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public sealed partial class MainWindow : Window
{
    private static readonly HashSet<string> ImportableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw3"
    };
    private readonly AppPaths _paths;
    private readonly IBookLibraryService _library;
    private readonly IKindleDeviceService _kindle;
    private readonly EpubReaderPreparationService _epubReader;
    private readonly DispatcherQueueTimer _deviceTimer;
    private Book? _selectedBook;
    private KindleBookCardViewModel? _selectedDeviceBook;
    private IReadOnlyList<KindleDevice> _devices = [];
    private bool _isRefreshingDevices;
    private bool _isTransferring;
    private CancellationTokenSource? _transferCancellation;
    private bool _isUpdatingFilters;
    private string? _scannedDeviceId;
    private double _deviceUsedRatio;
    private string? _acceptedDeviceId;
    private string? _ignoredDeviceId;
    private Button? _activeNavigationButton;
    private readonly HashSet<Button> _hoveredSidebarSections = [];
    private TaskCompletionSource<bool>? _devicePromptCompletion;
    private bool _nativeChromeConfigured;
    private AppWindow? _appWindow;
    private OverlappedPresenter? _windowPresenter;
    private NativeDeviceChangeMonitor? _deviceChangeMonitor;
    private IReadOnlyList<string> _readerChapters = [];
    private int _readerChapterIndex = -1;
    private string? _readerAllowedRoot;
    private string? _readerAllowedFile;

    public MainWindow(AppPaths paths, IBookLibraryService library, IKindleDeviceService kindle)
    {
        _paths = paths;
        _library = library;
        _kindle = kindle;
        _epubReader = new EpubReaderPreparationService(paths);
        ViewModel = new LibraryViewModel(library, paths.Data);
        InitializeComponent();
        ConfigureTitleBar();
        SetActiveNavigation(AllBooksButton);
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;

        _deviceTimer = DispatcherQueue.CreateTimer();
        _deviceTimer.Interval = TimeSpan.FromSeconds(3);
        _deviceTimer.Tick += async (_, _) => await RefreshDevicesAsync();
        _deviceTimer.Start();
        RootGrid.Loaded += MainWindow_Loaded;
    }

    public LibraryViewModel ViewModel { get; }
    public ObservableCollection<KindleBookCardViewModel> DeviceBooks { get; } = [];

    private void ConfigureTitleBar()
    {
        Title = "Kkindle";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_nativeChromeConfigured) return;
        _nativeChromeConfigured = true;

        try
        {
            ConfigureNativeWindowChrome();
        }
        catch
        {
            // Native chrome is decorative; never prevent the application from opening.
        }
    }

    private void ConfigureNativeWindowChrome()
    {
        var windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow = appWindow;
        try
        {
            _deviceChangeMonitor = new NativeDeviceChangeMonitor(windowHandle);
            _deviceChangeMonitor.DeviceChanged += DeviceChangeMonitor_DeviceChanged;
        }
        catch
        {
            _deviceChangeMonitor = null; // The three-second polling timer remains the reliable fallback.
        }
        appWindow.Title = "Kkindle";
        appWindow.Changed += AppWindow_Changed;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Kkindle.ico");
        if (File.Exists(iconPath)) appWindow.SetIcon(iconPath);

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            _windowPresenter = presenter;
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            UpdateMaximizeGlyph();
        }

        ApplySquareWindowFrame();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplySquareWindowFrame);
    }

    private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e) => _windowPresenter?.Minimize();

    private void MaximizeWindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_windowPresenter is null) return;
        if (_windowPresenter.State == OverlappedPresenterState.Maximized)
            _windowPresenter.Restore();
        else
            _windowPresenter.Maximize();
        UpdateMaximizeGlyph();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplySquareWindowFrame);
    }

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeGlyph()
    {
        var isMaximized = _windowPresenter?.State == OverlappedPresenterState.Maximized;
        MaximizeWindowGlyph.Glyph = isMaximized ? "\uE923" : "\uE922";
        MaximizeWindowButton.SetValue(
            Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty,
            isMaximized ? "还原" : "最大化");
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPresenterChange) return;
        UpdateMaximizeGlyph();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplySquareWindowFrame);
    }

    private void ApplySquareWindowFrame()
    {
        var windowHandle = WindowNative.GetWindowHandle(this);
        var cornerPreference = 1; // DWMWCP_DONOTROUND
        _ = DwmSetWindowAttribute(windowHandle, 33, ref cornerPreference, sizeof(int));

        var borderColor = 0x000000;
        _ = DwmSetWindowAttribute(windowHandle, 34, ref borderColor, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int value,
        int valueSize);

    private void DeviceChangeMonitor_DeviceChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(async () => await RefreshDevicesAsync());
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _deviceTimer.Stop();
        _transferCancellation?.Cancel();
        _transferCancellation?.Dispose();
        if (_deviceChangeMonitor is not null)
        {
            _deviceChangeMonitor.DeviceChanged -= DeviceChangeMonitor_DeviceChanged;
            _deviceChangeMonitor.Dispose();
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySquareWindowFrame();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplySquareWindowFrame);
        await RefreshLibraryAsync();
        await RefreshDevicesAsync();
    }

    private async Task RefreshLibraryAsync()
    {
        try
        {
            await ViewModel.RefreshAsync();
            LibrarySummaryText.Text = ViewModel.StatusText;
            SidebarCountText.Text = ViewModel.Books.Count.ToString();
            UpdateFilterControls();
            UpdateEmptyLibraryState();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("无法读取书库", ex.Message);
        }
    }

    private async Task RefreshDevicesAsync()
    {
        if (_isRefreshingDevices) return;
        _isRefreshingDevices = true;
        try
        {
            var detectedDevices = await _kindle.DetectDevicesAsync();
            if (detectedDevices.Count == 0)
            {
                if (_isTransferring) _transferCancellation?.Cancel();
                _acceptedDeviceId = null;
                _ignoredDeviceId = null;
                SetDisconnectedDeviceState();
                return;
            }

            var device = detectedDevices[0];
            if (_isTransferring
                && _devices.Count > 0
                && !string.Equals(_devices[0].VolumeSerial, device.VolumeSerial, StringComparison.Ordinal))
                _transferCancellation?.Cancel();
            if (!string.Equals(_acceptedDeviceId, device.VolumeSerial, StringComparison.Ordinal))
            {
                if (string.Equals(_ignoredDeviceId, device.VolumeSerial, StringComparison.Ordinal))
                {
                    SetDisconnectedDeviceState($"已忽略 {device.Name}");
                    return;
                }

                if (!await ShowDevicePromptAsync(
                        "发现 Kindle 设备",
                        $"发现 {device.Name}（{device.ConnectionLabel}）。是否连接到 Kkindle？",
                        "连接",
                        "暂不连接"))
                {
                    _ignoredDeviceId = device.VolumeSerial;
                    SetDisconnectedDeviceState($"已忽略 {device.Name}");
                    return;
                }
                _acceptedDeviceId = device.VolumeSerial;
                _ignoredDeviceId = null;
            }

            _devices = [device];
            KindleStatusText.Text = device.Name;
            KindleConnectionText.Text = $"{device.ConnectionLabel} · 已连接";
            EjectDeviceButton.Visibility = Visibility.Visible;
            DeviceStorageText.Text = device.CapacityLabel;
            _deviceUsedRatio = device.TotalBytes <= 0
                ? 0
                : Math.Clamp((device.TotalBytes - device.FreeBytes) / (double)device.TotalBytes, 0, 1);
            UpdateDeviceStorageBar();
            DeviceNameText.Text = $"{device.Name} · {device.ConnectionLabel}";
            DeviceCapacityText.Text = device.CapacityLabel;
            if (!string.Equals(_scannedDeviceId, device.VolumeSerial, StringComparison.Ordinal))
                await ScanDeviceBooksAsync(device);
        }
        catch
        {
            SetDisconnectedDeviceState("设备状态读取失败");
        }
        finally { _isRefreshingDevices = false; }
    }

    private void SetDisconnectedDeviceState(string? detail = null)
    {
        _devices = [];
        _scannedDeviceId = null;
        _selectedDeviceBook = null;
        DeviceBooks.Clear();
        DeviceBookList.SelectedItem = null;
        DeleteDeviceBookButton.IsEnabled = false;
        KindleStatusText.Text = "无设备连接";
        KindleConnectionText.Text = detail ?? string.Empty;
        EjectDeviceButton.Visibility = Visibility.Collapsed;
        DeviceStorageText.Text = "无存储信息";
        _deviceUsedRatio = 0;
        UpdateDeviceStorageBar();
        DeviceNameText.Text = "未检测到设备";
        DeviceCapacityText.Text = "—";
        DeviceBookCountText.Text = "0 本";
    }

    private async Task ScanDeviceBooksAsync(KindleDevice device)
    {
        DeviceNameText.Text = $"{device.Name} · 正在读取书籍与封面…";
        var books = await _kindle.ScanBooksAsync(device);
        DeviceBooks.Clear();
        foreach (var book in books) DeviceBooks.Add(new KindleBookCardViewModel(book));
        _selectedDeviceBook = null;
        DeviceBookList.SelectedItem = null;
        DeleteDeviceBookButton.IsEnabled = false;
        DeviceBookCountText.Text = $"{books.Count} 本";
        DeviceNameText.Text = $"{device.Name} · {device.ConnectionLabel}";
        _scannedDeviceId = device.VolumeSerial;
    }

    private void LibraryPane_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "拖放到电脑书库";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        DropImportOverlay.Visibility = Visibility.Visible;
    }

    private void LibraryPane_DragLeave(object sender, DragEventArgs e)
    {
        DropImportOverlay.Visibility = Visibility.Collapsed;
    }

    private async void LibraryPane_Drop(object sender, DragEventArgs e)
    {
        DropImportOverlay.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items
            .OfType<Windows.Storage.StorageFile>()
            .Where(file => ImportableExtensions.Contains(file.FileType))
            .Select(file => file.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        if (paths.Length == 0)
        {
            await ShowMessageAsync("无法导入", "请拖入 EPUB、PDF、MOBI 或 AZW3 书籍文件。");
            return;
        }
        await ImportAsync(paths);
    }

    private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        ViewModel.SearchText = sender.Text;
        await RefreshLibraryAsync();
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.SearchText = SearchBox.Text;
        await RefreshLibraryAsync();
    }

    private void UpdateFilterControls()
    {
        _isUpdatingFilters = true;
        try
        {
            AuthorFilterBox.ItemsSource = new[] { "全部作者" }.Concat(ViewModel.AvailableAuthors).ToArray();
            TagFilterBox.ItemsSource = new[] { "全部标签" }.Concat(ViewModel.AvailableTags).ToArray();
            FormatFilterBox.ItemsSource = new[] { "全部格式" }.Concat(ViewModel.AvailableFormats).ToArray();
            AuthorFilterBox.SelectedItem = ViewModel.AuthorFilter ?? "全部作者";
            TagFilterBox.SelectedItem = ViewModel.TagFilter ?? "全部标签";
            FormatFilterBox.SelectedItem = ViewModel.FormatFilter?.ToUpperInvariant() ?? "全部格式";
            var activeCount = new[] { ViewModel.AuthorFilter, ViewModel.TagFilter, ViewModel.FormatFilter }
                .Count(value => !string.IsNullOrWhiteSpace(value));
            FilterButton.Content = activeCount == 0 ? "筛选" : $"筛选 · {activeCount}";
        }
        finally { _isUpdatingFilters = false; }
    }

    private void UpdateEmptyLibraryState()
    {
        EmptyLibraryState.Visibility = ViewModel.Books.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var hasQuery = !string.IsNullOrWhiteSpace(ViewModel.SearchText) || ViewModel.HasActiveFilters;
        EmptyLibraryTitleText.Text = hasQuery ? "没有符合条件的书籍" : "电脑书库还是空的";
        EmptyLibraryMessageText.Text = hasQuery
            ? "调整搜索词或清除筛选后再试"
            : "拖入书籍文件，或使用右上角的导入按钮";
    }

    private async void FilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingFilters) return;
        ViewModel.AuthorFilter = AuthorFilterBox.SelectedIndex <= 0 ? null : AuthorFilterBox.SelectedItem as string;
        ViewModel.TagFilter = TagFilterBox.SelectedIndex <= 0 ? null : TagFilterBox.SelectedItem as string;
        ViewModel.FormatFilter = FormatFilterBox.SelectedIndex <= 0
            ? null
            : (FormatFilterBox.SelectedItem as string)?.ToLowerInvariant();
        await RefreshLibraryAsync();
    }

    private async void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.AuthorFilter = null;
        ViewModel.TagFilter = null;
        ViewModel.FormatFilter = null;
        await RefreshLibraryAsync();
    }

    private async void ImportFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.ViewMode = PickerViewMode.Thumbnail;
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        foreach (var extension in new[] { ".epub", ".pdf", ".mobi", ".azw3" }) picker.FileTypeFilter.Add(extension);
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0) await ImportAsync(files.Select(x => x.Path));
    }

    private async void ImportFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.ViewMode = PickerViewMode.List;
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) await ImportAsync([folder.Path]);
    }

    private async Task ImportAsync(IEnumerable<string> paths)
    {
        TaskProgress.Visibility = Visibility.Visible;
        TaskProgress.Value = 0;
        try
        {
            var progress = new Progress<TransferProgress>(value =>
            {
                TaskProgress.Value = value.Percentage;
                TaskStatusText.Text = value.Message;
            });
            var result = await ViewModel.ImportAsync(paths, progress);
            TaskStatusText.Text = ViewModel.StatusText;
            if (result.FailureCount > 0)
            {
                var failures = string.Join("\n", result.Items.Where(x => !x.Succeeded).Take(5).Select(x => $"{Path.GetFileName(x.SourcePath)}：{x.Message}"));
                await ShowMessageAsync("部分文件未导入", failures);
            }
            LibrarySummaryText.Text = ViewModel.StatusText;
            SidebarCountText.Text = ViewModel.Books.Count.ToString();
            UpdateFilterControls();
            UpdateEmptyLibraryState();
        }
        catch (OperationCanceledException)
        {
            TaskStatusText.Text = "已取消";
        }
        catch (Exception ex)
        {
            TaskStatusText.Text = "导入失败";
            await ShowMessageAsync("导入失败", ex.Message);
        }
        finally
        {
            TaskProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void BookGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BookCardViewModel card) SelectBook(card.Book);
    }

    private void BookList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BookList.SelectedItem is BookCardViewModel card) SelectBook(card.Book);
    }

    private void SelectBook(Book book)
    {
        _selectedBook = book;
        DetailsTitleBox.Text = book.Title;
        DetailsAuthorsBox.Text = book.Authors;
        DetailsSeriesBox.Text = book.Series ?? string.Empty;
        DetailsTagsBox.Text = book.Tags;
        DetailsDescriptionBox.Text = book.Description ?? string.Empty;
        DetailCoverImage.Source = null;
        if (!string.IsNullOrWhiteSpace(book.CoverPath))
        {
            var coverPath = Path.GetFullPath(Path.Combine(_paths.Data, book.CoverPath));
            if (File.Exists(coverPath))
            {
                try { DetailCoverImage.Source = new BitmapImage(new Uri(coverPath)); }
                catch { }
            }
        }
        DetailColumn.Width = new GridLength(320);
        DetailPane.Visibility = Visibility.Visible;
    }

    private async void SaveDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBook is null) return;
        _selectedBook.Title = string.IsNullOrWhiteSpace(DetailsTitleBox.Text) ? "未命名书籍" : DetailsTitleBox.Text.Trim();
        _selectedBook.Authors = string.IsNullOrWhiteSpace(DetailsAuthorsBox.Text) ? "未知作者" : DetailsAuthorsBox.Text.Trim();
        _selectedBook.Series = string.IsNullOrWhiteSpace(DetailsSeriesBox.Text) ? null : DetailsSeriesBox.Text.Trim();
        _selectedBook.Tags = DetailsTagsBox.Text.Trim();
        _selectedBook.Description = string.IsNullOrWhiteSpace(DetailsDescriptionBox.Text) ? null : DetailsDescriptionBox.Text.Trim();
        await _library.UpdateMetadataAsync(_selectedBook);
        await RefreshLibraryAsync();
        SelectBook(_selectedBook);
        TaskStatusText.Text = "元数据已保存";
    }

    private async void SendToKindleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTransferring) return;
        if (_selectedBook is null || _selectedBook.Files.Count == 0)
        {
            await ShowMessageAsync("无法发送", "这本书没有可用的文件格式。");
            return;
        }
        await RefreshDevicesAsync();
        if (_devices.Count == 0)
        {
            await ShowMessageAsync("未找到 Kindle", "请连接并解锁 Kindle，然后重试。");
            return;
        }

        var file = _selectedBook.Files[0];
        var source = _library.GetAbsoluteFilePath(file);
        var device = _devices[0];
        if (!await ShowDevicePromptAsync(
                "发送到 Kindle？",
                $"将“{_selectedBook.Title}”发送到 {device.Name}。\n\n如果设备上存在同名文件，Kkindle 会自动使用带序号的新文件名，不会覆盖原文件。",
                "发送",
                "取消")) return;

        _isTransferring = true;
        _transferCancellation = new CancellationTokenSource();
        TaskProgress.Visibility = Visibility.Visible;
        try
        {
            var progress = new Progress<TransferProgress>(value =>
            {
                TaskProgress.Value = value.Percentage;
                TaskStatusText.Text = value.Message;
            });
            await _kindle.SendBookAsync(device, file, source, progress, _transferCancellation.Token);
            TaskStatusText.Text = "已发送到 Kindle";
            _scannedDeviceId = null;
            await RefreshDevicesAsync();
        }
        catch (OperationCanceledException)
        {
            TaskStatusText.Text = "发送已中断";
            await ShowMessageAsync("发送已中断", "Kindle 已断开或传输已取消；未完成的临时文件会被清理。");
        }
        catch (Exception ex)
        {
            TaskStatusText.Text = "发送失败";
            await ShowMessageAsync("发送失败", ex.Message);
        }
        finally
        {
            _isTransferring = false;
            _transferCancellation.Dispose();
            _transferCancellation = null;
            TaskProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void DeviceBookList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedDeviceBook = DeviceBookList.SelectedItem as KindleBookCardViewModel;
        DeleteDeviceBookButton.IsEnabled = _selectedDeviceBook is not null && !_isTransferring;
    }

    private async void DeleteDeviceBookButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDeviceBook is null || _devices.Count == 0 || _isTransferring) return;
        var device = _devices[0];
        var book = _selectedDeviceBook.Book;
        if (!await ShowDevicePromptAsync(
                "从 Kindle 删除这本书？",
                $"将永久删除“{book.Title}”。\n\n目标仅限 documents\\{book.RelativePath}，此操作不会修改 Kindle 系统目录。",
                "删除",
                "取消")) return;

        DeleteDeviceBookButton.IsEnabled = false;
        try
        {
            await _kindle.RemoveBookAsync(device, book);
            TaskStatusText.Text = "已从 Kindle 删除书籍";
            _scannedDeviceId = null;
            await ScanDeviceBooksAsync(device);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("无法删除设备书籍", ex.Message);
            DeleteDeviceBookButton.IsEnabled = _selectedDeviceBook is not null;
        }
    }

    private async void DeleteBookButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBook is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "删除这本书？",
            Content = $"将从 Kkindle 书库中删除“{_selectedBook.Title}”及其文件。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await _library.DeleteAsync(_selectedBook.Id);
        _selectedBook = null;
        CloseDetails();
        await RefreshLibraryAsync();
    }

    private void CloseDetailButton_Click(object sender, RoutedEventArgs e) => CloseDetails();

    private void CloseDetails()
    {
        DetailPane.Visibility = Visibility.Collapsed;
        DetailColumn.Width = new GridLength(0);
    }

    private void GridViewButton_Click(object sender, RoutedEventArgs e)
    {
        BookGrid.Visibility = Visibility.Visible;
        BookList.Visibility = Visibility.Collapsed;
    }

    private void ListViewButton_Click(object sender, RoutedEventArgs e)
    {
        BookGrid.Visibility = Visibility.Collapsed;
        BookList.Visibility = Visibility.Visible;
    }

    private void FilterButton_Click(object sender, RoutedEventArgs e) =>
        FilterPanel.Visibility = FilterPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    private async void MoreButton_Click(object sender, RoutedEventArgs e) => await ShowMessageAsync("Kkindle", $"便携数据目录：{_paths.Data}");
    private async void AddTagButton_Click(object sender, RoutedEventArgs e) => await ShowMessageAsync("标签", "可以在书籍详情中直接编辑标签，多个标签用逗号分隔。");
    private async void AddCategoryButton_Click(object sender, RoutedEventArgs e) => await ShowMessageAsync("分类", "分类功能将在书库筛选基础完成后接入。");
    private async void SettingsButton_Click(object sender, RoutedEventArgs e) => await ShowMessageAsync("设置", $"当前书库位于：{_paths.Library}");
    private void KindleBooksButton_Click(object sender, RoutedEventArgs e) => OpenDevicePage(showBooks: true);

    private void DeviceOverviewButton_Click(object sender, RoutedEventArgs e) => OpenDevicePage(showBooks: false);

    private void OpenDevicePage(bool showBooks)
    {
        SetActiveNavigation(showBooks ? KindleBooksButton : DeviceOverviewButton);
        DevicePageTitleText.Text = showBooks ? "Kindle 书籍" : "设备概览";
        DeviceBookList.Visibility = showBooks ? Visibility.Visible : Visibility.Collapsed;
        DeviceReadOnlyNote.Visibility = showBooks ? Visibility.Visible : Visibility.Collapsed;
        LibraryPane.Visibility = Visibility.Collapsed;
        DetailPane.Visibility = Visibility.Collapsed;
        DetailColumn.Width = new GridLength(0);
        DevicePage.Visibility = Visibility.Visible;
    }

    private void AllBooksButton_Click(object sender, RoutedEventArgs e) => ShowLibrary();

    private async void RefreshDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        _ignoredDeviceId = null;
        _scannedDeviceId = null;
        await RefreshDevicesAsync();
    }

    private async void EjectDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTransferring)
        {
            await ShowMessageAsync("正在传输", "请等待书籍传输完成后再弹出设备。");
            return;
        }
        if (_devices.Count == 0) return;

        var device = _devices[0];
        var isWpd = device.Transport == KindleTransport.Wpd;
        if (!await ShowDevicePromptAsync(
                isWpd ? "断开 Kindle？" : "安全弹出 Kindle？",
                isWpd
                    ? "Kindle Scribe 使用 MTP 连接，不提供磁盘安全弹出。Kkindle 将停止访问设备，随后可以断开 USB。"
                    : "请确认当前没有正在进行的传输。",
                isWpd ? "断开" : "弹出",
                "取消")) return;

        try
        {
            if (!isWpd) await _kindle.EjectAsync(device);
            _acceptedDeviceId = null;
            _ignoredDeviceId = device.VolumeSerial;
            SetDisconnectedDeviceState(isWpd ? $"已断开 {device.Name}" : $"已弹出 {device.Name}");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("无法弹出设备", ex.Message);
        }
    }

    private void ShowLibrary()
    {
        SetActiveNavigation(AllBooksButton);
        DevicePage.Visibility = Visibility.Collapsed;
        LibraryPane.Visibility = Visibility.Visible;
    }

    private async void OpenBookMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: Book book }) return;

        var file = book.Files
            .Where(bookFile => bookFile.Format.Equals("epub", StringComparison.OrdinalIgnoreCase)
                || bookFile.Format.Equals("pdf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(bookFile => GetReadingFormatPriority(bookFile.Format))
            .FirstOrDefault();
        if (file is null)
        {
            await ShowMessageAsync("暂不支持阅读", "内置阅读器目前支持 EPUB 和 PDF。");
            return;
        }

        try
        {
            var path = _library.GetAbsoluteFilePath(file);
            if (!File.Exists(path)) throw new FileNotFoundException("书籍文件不存在。", path);

            await ReaderWebView.EnsureCoreWebView2Async();
            ConfigureReaderWebView();
            ReaderTitleText.Text = book.Title;
            ReaderPane.Visibility = Visibility.Visible;

            if (file.Format.Equals("pdf", StringComparison.OrdinalIgnoreCase))
            {
                _readerChapters = [];
                _readerChapterIndex = -1;
                _readerAllowedRoot = null;
                _readerAllowedFile = Path.GetFullPath(path);
                ReaderStatusText.Text = "PDF";
                ReaderChapterText.Text = string.Empty;
                ReaderPreviousButton.Visibility = Visibility.Collapsed;
                ReaderNextButton.Visibility = Visibility.Collapsed;
                ReaderWebView.Source = new Uri(path);
                return;
            }

            ReaderStatusText.Text = "正在准备 EPUB…";
            var document = await _epubReader.PrepareAsync(path, file.Sha256);
            _readerChapters = document.Chapters;
            _readerChapterIndex = 0;
            _readerAllowedRoot = document.RootPath;
            _readerAllowedFile = null;
            ReaderStatusText.Text = "EPUB";
            ReaderPreviousButton.Visibility = Visibility.Visible;
            ReaderNextButton.Visibility = Visibility.Visible;
            ShowReaderChapter();
        }
        catch (Exception ex)
        {
            CloseReader();
            await ShowMessageAsync("无法打开书籍", ex.Message);
        }
    }

    private void ConfigureReaderWebView()
    {
        var settings = ReaderWebView.CoreWebView2.Settings;
        settings.IsScriptEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
    }

    private void ShowReaderChapter()
    {
        if (_readerChapterIndex < 0 || _readerChapterIndex >= _readerChapters.Count) return;
        ReaderChapterText.Text = $"{_readerChapterIndex + 1} / {_readerChapters.Count}";
        ReaderPreviousButton.IsEnabled = _readerChapterIndex > 0;
        ReaderNextButton.IsEnabled = _readerChapterIndex + 1 < _readerChapters.Count;
        ReaderWebView.Source = new Uri(_readerChapters[_readerChapterIndex]);
    }

    private void ReaderPreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_readerChapterIndex <= 0) return;
        _readerChapterIndex--;
        ShowReaderChapter();
    }

    private void ReaderNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_readerChapterIndex + 1 >= _readerChapters.Count) return;
        _readerChapterIndex++;
        ShowReaderChapter();
    }

    private void CloseReaderButton_Click(object sender, RoutedEventArgs e) => CloseReader();

    private void CloseReader()
    {
        ReaderPane.Visibility = Visibility.Collapsed;
        _readerChapters = [];
        _readerChapterIndex = -1;
        _readerAllowedRoot = null;
        _readerAllowedFile = null;
        if (ReaderWebView.CoreWebView2 is not null)
            ReaderWebView.CoreWebView2.Navigate("about:blank");
    }

    private void ReaderWebView_NavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (args.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return;
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || !uri.IsFile)
        {
            args.Cancel = true;
            return;
        }

        var target = Path.GetFullPath(uri.LocalPath);
        var allowed = _readerAllowedFile is not null
            ? target.Equals(_readerAllowedFile, StringComparison.OrdinalIgnoreCase)
            : _readerAllowedRoot is not null && IsPathInside(_readerAllowedRoot, target);
        if (!allowed) args.Cancel = true;
    }

    private static bool IsPathInside(string root, string path)
    {
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(boundary, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetReadingFormatPriority(string format) => format.ToLowerInvariant() switch
    {
        "epub" => 0,
        "pdf" => 1,
        "mobi" => 2,
        "azw3" => 3,
        _ => 4
    };

    private void SidebarSectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, BookManagementSectionButton))
        {
            ToggleSidebarSection(BookManagementSectionButton, BookManagementChildren, BookManagementChevron, "书籍管理");
            return;
        }

        if (ReferenceEquals(sender, DeviceManagementSectionButton))
        {
            ToggleSidebarSection(DeviceManagementSectionButton, DeviceManagementChildren, DeviceManagementChevron, "设备管理");
            return;
        }

        if (ReferenceEquals(sender, ReadingSectionButton))
        {
            ToggleSidebarSection(ReadingSectionButton, ReadingChildren, ReadingChevron, "阅读资料");
            return;
        }

        if (ReferenceEquals(sender, SystemSectionButton))
            ToggleSidebarSection(SystemSectionButton, SystemChildren, SystemChevron, "系统");
    }

    private void ToggleSidebarSection(Button sectionButton, StackPanel children, FontIcon chevron, string title)
    {
        var shouldExpand = children.Visibility != Visibility.Visible;
        SetSidebarSectionState(sectionButton, children, chevron, title, shouldExpand);
    }

    private void ExpandSidebarSection(Button sectionButton, StackPanel children, FontIcon chevron, string title)
    {
        SetSidebarSectionState(sectionButton, children, chevron, title, expanded: true);
    }

    private void SetSidebarSectionState(
        Button sectionButton,
        StackPanel children,
        FontIcon chevron,
        string title,
        bool expanded)
    {
        children.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        chevron.Glyph = expanded ? "\uE70D" : "\uE76C";
        ApplySidebarSectionColors(
            sectionButton,
            chevron,
            darkBackground: expanded != _hoveredSidebarSections.Contains(sectionButton),
            animate: false);
        sectionButton.SetValue(
            Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty,
            $"{title}，{(expanded ? "已展开" : "已收起")}");
    }

    private void SidebarSectionButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button sectionButton
            || !TryGetSidebarSectionVisuals(sectionButton, out var children, out var chevron)) return;
        if (!_hoveredSidebarSections.Add(sectionButton)) return;
        ApplySidebarSectionColors(
            sectionButton,
            chevron,
            darkBackground: children.Visibility != Visibility.Visible,
            animate: true);
    }

    private void SidebarSectionButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button sectionButton
            || !TryGetSidebarSectionVisuals(sectionButton, out var children, out var chevron)) return;
        _hoveredSidebarSections.Remove(sectionButton);
        ApplySidebarSectionColors(
            sectionButton,
            chevron,
            darkBackground: children.Visibility == Visibility.Visible,
            animate: true);
    }

    private bool TryGetSidebarSectionVisuals(
        Button sectionButton,
        out StackPanel children,
        out FontIcon chevron)
    {
        if (ReferenceEquals(sectionButton, BookManagementSectionButton))
            (children, chevron) = (BookManagementChildren, BookManagementChevron);
        else if (ReferenceEquals(sectionButton, DeviceManagementSectionButton))
            (children, chevron) = (DeviceManagementChildren, DeviceManagementChevron);
        else if (ReferenceEquals(sectionButton, ReadingSectionButton))
            (children, chevron) = (ReadingChildren, ReadingChevron);
        else if (ReferenceEquals(sectionButton, SystemSectionButton))
            (children, chevron) = (SystemChildren, SystemChevron);
        else
        {
            children = null!;
            chevron = null!;
            return false;
        }
        return true;
    }

    private static void ApplySidebarSectionColors(
        Button sectionButton,
        FontIcon chevron,
        bool darkBackground,
        bool animate)
    {
        var targetBackground = darkBackground ? Colors.Black : Colors.White;
        var targetForeground = darkBackground ? Colors.White : Colors.Black;
        var currentBackground = (sectionButton.Background as SolidColorBrush)?.Color ?? targetBackground;
        var currentForeground = (sectionButton.Foreground as SolidColorBrush)?.Color ?? targetForeground;
        var backgroundBrush = new SolidColorBrush(currentBackground);
        var foregroundBrush = new SolidColorBrush(currentForeground);
        sectionButton.Background = backgroundBrush;
        sectionButton.Foreground = foregroundBrush;
        chevron.Foreground = foregroundBrush;

        if (!animate)
        {
            backgroundBrush.Color = targetBackground;
            foregroundBrush.Color = targetForeground;
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(120));
        var backgroundAnimation = new ColorAnimation
        {
            From = currentBackground,
            To = targetBackground,
            Duration = duration
        };
        var foregroundAnimation = new ColorAnimation
        {
            From = currentForeground,
            To = targetForeground,
            Duration = duration
        };
        Storyboard.SetTarget(backgroundAnimation, backgroundBrush);
        Storyboard.SetTargetProperty(backgroundAnimation, "Color");
        Storyboard.SetTarget(foregroundAnimation, foregroundBrush);
        Storyboard.SetTargetProperty(foregroundAnimation, "Color");
        var storyboard = new Storyboard();
        storyboard.Children.Add(backgroundAnimation);
        storyboard.Children.Add(foregroundAnimation);
        backgroundBrush.Color = targetBackground;
        foregroundBrush.Color = targetForeground;
        storyboard.Begin();
    }

    private void SetActiveNavigation(Button activeButton)
    {
        _activeNavigationButton = activeButton;
        if (activeButton == DeviceOverviewButton)
            ExpandSidebarSection(DeviceManagementSectionButton, DeviceManagementChildren, DeviceManagementChevron, "设备管理");
        else
            ExpandSidebarSection(BookManagementSectionButton, BookManagementChildren, BookManagementChevron, "书籍管理");

        var ink = (Brush)Application.Current.Resources["InkBrush"];
        var paper = (Brush)Application.Current.Resources["CardBrush"];
        var muted = (Brush)Application.Current.Resources["MutedInkBrush"];
        var idleIndicator = (Brush)Application.Current.Resources["SidebarIndicatorBrush"];
        foreach (var button in new[] { AllBooksButton, KindleBooksButton, DeviceOverviewButton })
        {
            var isActive = button == activeButton;
            button.Background = paper;
            button.Foreground = isActive ? ink : muted;
            button.BorderBrush = isActive ? ink : idleIndicator;
            button.FontWeight = isActive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
        AllBooksLabelText.Foreground = activeButton == AllBooksButton ? ink : muted;
        SidebarCountText.Foreground = activeButton == AllBooksButton ? ink : muted;
    }

    private void DeviceStorageBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateDeviceStorageBar();

    private void UpdateDeviceStorageBar()
    {
        var availableWidth = Math.Max(0, DeviceStorageBar.ActualWidth - 2);
        DeviceStorageUsedBar.Width = availableWidth * _deviceUsedRatio;
    }

    private async void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button == AllBooksButton)
        {
            ShowLibrary();
            return;
        }
        if (sender is Button)
            await ShowMessageAsync("Kkindle", "首版当前聚焦书架与 Kindle 同步。");
    }

    private Task<bool> ShowDevicePromptAsync(string title, string message, string primaryText, string cancelText)
    {
        if (_devicePromptCompletion is not null)
            throw new InvalidOperationException("已有设备确认窗口正在显示。");

        _devicePromptCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DevicePromptTitleText.Text = title;
        DevicePromptMessageText.Text = message;
        DevicePromptPrimaryButton.Content = primaryText;
        DevicePromptCancelButton.Content = cancelText;
        DevicePromptOverlay.Visibility = Visibility.Visible;
        DevicePromptOverlay.Focus(FocusState.Programmatic);
        return _devicePromptCompletion.Task;
    }

    private void DevicePromptPrimaryButton_Click(object sender, RoutedEventArgs e) => CompleteDevicePrompt(true);

    private void DevicePromptCancelButton_Click(object sender, RoutedEventArgs e) => CompleteDevicePrompt(false);

    private void DevicePromptOverlay_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            CompleteDevicePrompt(false);
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            CompleteDevicePrompt(true);
        }
    }

    private void CompleteDevicePrompt(bool result)
    {
        var completion = _devicePromptCompletion;
        if (completion is null) return;
        _devicePromptCompletion = null;
        DevicePromptOverlay.Visibility = Visibility.Collapsed;
        completion.TrySetResult(result);
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }
}
