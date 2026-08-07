using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    private IReadOnlyList<EpubReaderNavigationItem> _readerNavigation = [];
    private int _readerChapterIndex = -1;
    private string? _readerAllowedRoot;
    private string? _readerAllowedFile;
    private int _readerFlowMode;
    private bool _isUpdatingReaderToc;
    private bool _isUpdatingReaderProgress;
    private bool _readerNavigateToEnd;
    private bool _readerTocExpanded = true;
    private bool _readerAssistantExpanded = true;
    private bool _readerHasToc;
    private bool _readerZenMode;
    private bool _readerPreZenTocExpanded = true;
    private bool _readerPreZenAssistantExpanded = true;
    private int _readerPageAnimation; // 0 = none, 1 = simulated, 2 = slide
    private bool _readerContinuousLocked;
    private int _readerContinuousDirection = 1;
    private DateTimeOffset _readerLastChapterChange = DateTimeOffset.MinValue;
    private int? _readerPendingTurnInAnimation;
    private CancellationTokenSource? _readerRelayoutCancellation;
    private DispatcherQueueTimer? _readerScrollPollTimer;
    private bool _readerPollRunning;
    private bool _readerLastNearTop = true;
    private bool _readerLastNearBottom;
    private IntPtr _readerMouseHook;
    private bool _readerMouseDownInside;
    private POINT _readerMouseDownPoint;
    private LowLevelMouseProc? _readerMouseProc;

    public MainWindow(
        AppPaths paths,
        IBookLibraryService library,
        IKindleDeviceService kindle,
        ReaderDataService readerData,
        EpubBookContentService bookContent,
        EpubFootnoteResolver footnotes,
        AiSettingsStore aiSettingsStore,
        AiChatClient aiChatClient)
    {
        _paths = paths;
        _library = library;
        _kindle = kindle;
        _readerData = readerData;
        _bookContent = bookContent;
        _footnotes = footnotes;
        _aiSettingsStore = aiSettingsStore;
        _aiChatClient = aiChatClient;
        _epubReader = new EpubReaderPreparationService(paths);
        ViewModel = new LibraryViewModel(library, paths.Data);
        InitializeComponent();
        ConfigureReaderFeatureHosts();
        ConfigureTitleBar();
        SetActiveNavigation(AllBooksButton);
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;

        _deviceTimer = DispatcherQueue.CreateTimer();
        _deviceTimer.Interval = TimeSpan.FromSeconds(3);
        _deviceTimer.Tick += async (_, _) => await RefreshDevicesAsync();
        _deviceTimer.Start();
        RootGrid.Loaded += MainWindow_Loaded;
        RootGrid.KeyDown += RootGrid_KeyDown;
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
        _windowActive = args.WindowActivationState == WindowActivationState.CodeActivated
            || args.WindowActivationState == WindowActivationState.PointerActivated;
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
        if (args.DidPresenterChange)
        {
            UpdateMaximizeGlyph();
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplySquareWindowFrame);
        }
        if (args.DidSizeChange)
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ConstrainRootToViewport);
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
        try { FlushReaderSessionAsync().GetAwaiter().GetResult(); }
        catch { }
        _deviceTimer.Stop();
        _transferCancellation?.Cancel();
        _transferCancellation?.Dispose();
        _readerFeatureCancellation?.Cancel();
        _readerFeatureCancellation?.Dispose();
        _readerRelayoutCancellation?.Cancel();
        _readerRelayoutCancellation?.Dispose();
        StopReaderScrollPoll();
        UninstallReaderMouseHook();
        _aiChatClient.Dispose();
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
        ConstrainRootToViewport();
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
                && !string.Equals(_devices[0].Identity, device.Identity, StringComparison.OrdinalIgnoreCase))
                _transferCancellation?.Cancel();
            if (!string.Equals(_acceptedDeviceId, device.Identity, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(_ignoredDeviceId, device.Identity, StringComparison.OrdinalIgnoreCase))
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
                    _ignoredDeviceId = device.Identity;
                    SetDisconnectedDeviceState($"已忽略 {device.Name}");
                    return;
                }
                _acceptedDeviceId = device.Identity;
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
            if (!string.Equals(_scannedDeviceId, device.Identity, StringComparison.OrdinalIgnoreCase))
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
        _scannedDeviceId = device.Identity;
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
        OpenSelectedBookButton.IsEnabled = book.Files.Any(bookFile =>
            bookFile.Format.Equals("epub", StringComparison.OrdinalIgnoreCase)
            || bookFile.Format.Equals("pdf", StringComparison.OrdinalIgnoreCase));
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
            _ignoredDeviceId = device.Identity;
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

        await OpenBookAsync(book);
    }

    private async void OpenSelectedBookButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBook is null) return;

        await OpenBookAsync(_selectedBook);
    }

    private async Task OpenBookAsync(Book book)
    {

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
            ConfigureReaderBookInformation(book, file);
            BeginReaderSession(book, file);
            await LoadReaderSessionDataAsync(_readerFeatureCancellation!.Token);
            ReaderTitleText.Text = book.Title;
            ReaderPane.Visibility = Visibility.Visible;
            ReaderBrandText.Visibility = Visibility.Visible;
            ReaderPane.UpdateLayout();
            _readerTocExpanded = true;
            _readerAssistantExpanded = true;
            _readerFlowMode = _readerLayout.FlowMode;
            _readerZenMode = false;
            _readerContinuousLocked = false;
            _readerPendingTurnInAnimation = null;
            ResetReaderChromeLayout();
            UpdateReaderZoom();
            UpdateReaderFlowButton();
            SyncReaderPageAnimationMenu();
            ApplyReaderPanelLayout();

            if (file.Format.Equals("pdf", StringComparison.OrdinalIgnoreCase))
            {
                _readerHasToc = false;
                _readerChapters = [];
                _readerNavigation = [];
                _readerChapterIndex = -1;
                _readerAllowedRoot = null;
                _readerAllowedFile = Path.GetFullPath(path);
                ReaderStatusText.Text = "PDF · 可使用阅读区内置工具栏搜索和翻页";
                ReaderChapterText.Text = string.Empty;
                ReaderReadingProgressText.Text = "PDF 文档";
                ReaderProgressPercentText.Text = "—";
                ReaderTocList.ItemsSource = null;
                ReaderTocList.Visibility = Visibility.Collapsed;
                ReaderTocSearchBox.Visibility = Visibility.Collapsed;
                ReaderTocEmptyText.Text = "PDF 使用内置查看器。可通过查看器工具栏搜索、缩放和翻页。";
                ReaderTocEmptyText.Visibility = Visibility.Visible;
                ReaderZoomOutButton.Visibility = Visibility.Collapsed;
                ReaderZoomText.Visibility = Visibility.Collapsed;
                ReaderZoomInButton.Visibility = Visibility.Collapsed;
                ReaderPreviousButton.Visibility = Visibility.Collapsed;
                ReaderNextButton.Visibility = Visibility.Collapsed;
                ReaderProgressSlider.Visibility = Visibility.Collapsed;
                ReaderPdfBottomText.Visibility = Visibility.Visible;
                ReaderFlowButton.Visibility = Visibility.Collapsed;
                ReaderHighlightButton.Visibility = Visibility.Collapsed;
                ReaderAnnotateButton.Visibility = Visibility.Collapsed;
                SetReaderIndexUnavailable("PDF 暂不支持本地全文索引与批注；可继续使用内置查看器。");
                ApplyReaderPanelLayout();
                ReaderWebView.Source = new Uri(path);
                return;
            }

            _readerHasToc = true;
            ReaderStatusText.Text = "正在准备…";
            var document = await _epubReader.PrepareAsync(path, file.Sha256);
            _readerDocument = document;
            _readerChapters = document.Chapters;
            _readerNavigation = document.Navigation;
            _readerChapterIndex = 0;
            _readerAllowedRoot = document.RootPath;
            _readerAllowedFile = null;
            if (_savedReaderProgress is { } savedProgress
                && savedProgress.ChapterIndex >= 0
                && savedProgress.ChapterIndex < _readerChapters.Count)
            {
                _readerChapterIndex = savedProgress.ChapterIndex;
                if (savedProgress.FlowMode == _readerFlowMode)
                    _pendingReaderRestorePosition = savedProgress.ScrollPosition;
            }
            ReaderStatusText.Text = string.Empty;
            ReaderTocSearchBox.Text = string.Empty;
            ReaderTocSearchBox.Visibility = Visibility.Visible;
            ReaderTocList.Visibility = Visibility.Visible;
            ReaderTocEmptyText.Visibility = Visibility.Collapsed;
            ApplyReaderTocFilter();
            ReaderZoomOutButton.Visibility = Visibility.Visible;
            ReaderZoomText.Visibility = Visibility.Visible;
            ReaderZoomInButton.Visibility = Visibility.Visible;
            ReaderPreviousButton.Visibility = Visibility.Visible;
            ReaderNextButton.Visibility = Visibility.Visible;
            ReaderProgressSlider.Visibility = Visibility.Visible;
            ReaderPdfBottomText.Visibility = Visibility.Collapsed;
            ReaderFlowButton.Visibility = Visibility.Visible;
            ReaderHighlightButton.Visibility = Visibility.Visible;
            ReaderAnnotateButton.Visibility = Visibility.Visible;
            ApplyReaderPanelLayout();
            ShowReaderChapter();
            StartReaderIndexing();
            StartReaderScrollPoll();
            InstallReaderMouseHook();
            StartReaderToolsTimers();
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

    private void ConfigureReaderBookInformation(Book book, BookFile file)
    {
        ReaderBookTitleText.Text = book.Title;
        ReaderBookAuthorText.Text = book.Authors;
        ReaderBookFormatText.Text = file.Format.ToUpperInvariant();
        ReaderCoverImage.Source = null;
        if (string.IsNullOrWhiteSpace(book.CoverPath)) return;

        var coverPath = Path.GetFullPath(Path.Combine(_paths.Data, book.CoverPath));
        if (!File.Exists(coverPath)) return;
        try { ReaderCoverImage.Source = new BitmapImage(new Uri(coverPath)); }
        catch { ReaderCoverImage.Source = null; }
    }

    private void ResetReaderAssistant()
    {
        ResetReaderFeatures();
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ConstrainRootToViewport();
    }

    private void ConstrainRootToViewport()
    {
        var viewportWidth = RootGrid.XamlRoot?.Size.Width ?? 0;
        if (viewportWidth <= 0) return;
        if (double.IsNaN(RootGrid.Width) || Math.Abs(RootGrid.Width - viewportWidth) > 0.5)
            RootGrid.Width = viewportWidth;
        ApplyReaderPanelLayout(viewportWidth);
    }

    private void ReaderTocToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _readerTocExpanded = !_readerTocExpanded;
        ApplyReaderPanelLayout();
    }

    private void ReaderAssistantToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _readerAssistantExpanded = !_readerAssistantExpanded;
        ApplyReaderPanelLayout();
    }

    private void ApplyReaderPanelLayout(double? availableWidth = null)
    {
        if (ReaderPane.Visibility != Visibility.Visible) return;
        var width = RootGrid.XamlRoot?.Size.Width ?? availableWidth ?? RootGrid.ActualWidth;
        if (width <= 0) return;
        var assistantWidth = _readerAssistantExpanded ? 360d : 0d;
        var readerWidth = Math.Max(0, width - assistantWidth);
        ReaderPane.Width = readerWidth;
        ReaderPane.HorizontalAlignment = HorizontalAlignment.Left;
        var tocWidth = _readerTocExpanded ? 286d : 0d;
        ReaderTocColumn.Width = new GridLength(tocWidth);
        ReaderContentColumn.Width = new GridLength(Math.Max(0, readerWidth - tocWidth));
        ReaderAssistantColumn.Width = new GridLength(0);

        ReaderTocPanel.Visibility = _readerTocExpanded ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(ReaderTocPanel, 0);
        Grid.SetColumnSpan(ReaderTocPanel, 1);
        ReaderTocPanel.Width = double.NaN;
        ReaderTocPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        Canvas.SetZIndex(ReaderTocPanel, 0);

        UpdateReaderAssistantPopup(_readerAssistantExpanded);
        if (_readerZenMode) UpdateReaderZenPopup(true);

        ReaderTocToggleButton.Opacity = _readerTocExpanded ? 0.58 : 1;
        ReaderAssistantToggleButton.Opacity = _readerAssistantExpanded ? 0.58 : 1;
    }

    private void ReaderContentPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ReaderContentClip.Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        ScheduleReaderRelayout();
    }

    // ------------------------------------------------------------------
    // Re-adaptation: whenever the reading surface changes size (window
    // resize, TOC/assistant collapse, zen-mode toggle), re-apply the
    // viewport-based appearance so pagination re-flows to the current
    // reading viewport, and clamp the current scroll position so the
    // reader never rests outside the available pages.
    // ------------------------------------------------------------------

    private void ScheduleReaderRelayout()
    {
        if (ReaderWebView.CoreWebView2 is null || _readerAllowedRoot is null) return;
        if (ReaderPane.Visibility != Visibility.Visible) return;
        _readerRelayoutCancellation?.Cancel();
        _readerRelayoutCancellation?.Dispose();
        _readerRelayoutCancellation = new CancellationTokenSource();
        var token = _readerRelayoutCancellation.Token;
        _ = Task.Run(async () =>
        {
            await Task.Delay(120);
            if (token.IsCancellationRequested) return;
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (token.IsCancellationRequested) return;
                try
                {
                    await ApplyReaderAppearanceAsync();
                    await ClampReaderScrollAsync();
                }
                catch
                {
                }
            });
        });
    }

    private async Task ClampReaderScrollAsync()
    {
        if (ReaderWebView.CoreWebView2 is null || _readerAllowedRoot is null) return;
        var script = _readerFlowMode == 0
            ? "(function(){var el=document.scrollingElement;var max=Math.max(0,el.scrollHeight-el.clientHeight);if(el.scrollTop>max)window.scrollTo({top:max});})()"
            : "(function(){var el=document.scrollingElement;var max=Math.max(0,el.scrollWidth-el.clientWidth);if(el.scrollLeft>max)window.scrollTo({left:max,top:0});})()";
        try { await ReaderWebView.CoreWebView2.ExecuteScriptAsync(script); }
        catch { }
    }

    private void ReaderTocSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyReaderTocFilter();

    private void ApplyReaderTocFilter()
    {
        if (!_readerHasToc)
        {
            ReaderTocList.ItemsSource = null;
            return;
        }

        var query = ReaderTocSearchBox.Text.Trim();
        ReaderTocList.ItemsSource = string.IsNullOrEmpty(query)
            ? _readerNavigation
            : _readerNavigation.Where(item =>
                item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }

    private void ShowReaderChapter()
    {
        if (_readerChapterIndex < 0 || _readerChapterIndex >= _readerChapters.Count) return;
        UpdateReaderChapterControls();
        SelectReaderTocItem(_readerNavigation.FirstOrDefault(item => item.ChapterIndex == _readerChapterIndex));
        ReaderWebView.Source = new Uri(_readerChapters[_readerChapterIndex]);
    }

    private void ReaderTocList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingReaderToc || ReaderTocList.SelectedItem is not EpubReaderNavigationItem item) return;
        _readerContinuousLocked = false;
        _readerChapterIndex = item.ChapterIndex;
        UpdateReaderChapterControls();
        ReaderWebView.Source = new Uri(item.Target);
    }

    private void SelectReaderTocItem(EpubReaderNavigationItem? item)
    {
        _isUpdatingReaderToc = true;
        ReaderTocList.SelectedItem = item;
        if (item is not null) ReaderTocList.ScrollIntoView(item);
        _isUpdatingReaderToc = false;
    }

    private void UpdateReaderChapterControls()
    {
        ReaderChapterText.Text = _readerChapterIndex < 0
            ? string.Empty
            : $"{_readerChapterIndex + 1} / {_readerChapters.Count} 章";
        ReaderPreviousButton.IsEnabled = _readerChapterIndex > 0;
        ReaderNextButton.IsEnabled = _readerChapterIndex + 1 < _readerChapters.Count;
        UpdateReaderProgress();
    }

    private void UpdateReaderProgress()
    {
        if (_readerChapterIndex < 0 || _readerChapters.Count == 0) return;
        var current = _readerChapterIndex + 1;
        var percentage = _readerLastProgress is { ProgressPercent: > 0 } progress
            ? (int)Math.Round(progress.ProgressPercent)
            : (int)Math.Round(current * 100d / _readerChapters.Count);
        ReaderReadingProgressText.Text = $"已读 {current} / {_readerChapters.Count} 章";
        ReaderProgressPercentText.Text = $"{Math.Clamp(percentage, 0, 100)}%";
        _isUpdatingReaderProgress = true;
        ReaderProgressSlider.Minimum = 1;
        ReaderProgressSlider.Maximum = Math.Max(1, _readerChapters.Count);
        ReaderProgressSlider.Value = current;
        _isUpdatingReaderProgress = false;
    }

    private void ReaderProgressSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingReaderProgress || !_readerHasToc || _readerChapters.Count == 0) return;
        var chapterIndex = Math.Clamp((int)Math.Round(e.NewValue) - 1, 0, _readerChapters.Count - 1);
        if (chapterIndex == _readerChapterIndex) return;
        _readerContinuousLocked = false;
        _readerChapterIndex = chapterIndex;
        _readerNavigateToEnd = false;
        ShowReaderChapter();
    }

    private async void ReaderPreviousButton_Click(object sender, RoutedEventArgs e)
    {
        _readerContinuousLocked = false;
        await TurnReaderPageAsync(-1);
    }

    private async void ReaderNextButton_Click(object sender, RoutedEventArgs e)
    {
        _readerContinuousLocked = false;
        await TurnReaderPageAsync(1);
    }

    private void ReaderZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        _readerLayout = _readerLayout with { FontScale = Math.Max(0.8, _readerLayout.FontScale - 0.1) };
        UpdateReaderZoom();
        _ = SaveReaderLayoutSettingsAsync();
    }

    private void ReaderZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        _readerLayout = _readerLayout with { FontScale = Math.Min(1.8, _readerLayout.FontScale + 0.1) };
        UpdateReaderZoom();
        _ = SaveReaderLayoutSettingsAsync();
    }

    private void UpdateReaderZoom()
    {
        UpdateReaderZoomLabel();
        _ = ApplyReaderAppearanceAsync();
    }

    private async void ReaderFlowButton_Click(object sender, RoutedEventArgs e)
    {
        _readerFlowMode = (_readerFlowMode + 1) % 2;
        _readerNavigateToEnd = false;
        _readerContinuousLocked = false;
        _readerLayout = _readerLayout with { FlowMode = _readerFlowMode };
        UpdateReaderFlowButton();
        await ApplyReaderAppearanceAsync();
        await ResetReaderPositionAsync();
        await PrimeReaderScrollEdgesAsync();
        _ = SaveReaderLayoutSettingsAsync();
        UpdateReaderLayoutStatus();
    }

    private void UpdateReaderFlowButton()
    {
        ReaderFlowButton.Content = _readerFlowMode == 0 ? "滚动" : "分页";
    }

    // ------------------------------------------------------------------
    // Zen mode: maximize the reading surface by hiding the TOC and the AI
    // assistant panels and collapsing the reader header/footer bars. The
    // custom window title bar (with window caption buttons) is untouched.
    // ------------------------------------------------------------------

    private void ReaderZenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ToggleReaderZenMode();
        ReaderMoreButton.Flyout?.Hide();
    }

    private void ReaderExitZenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_readerZenMode) ToggleReaderZenMode();
    }

    private void ToggleReaderZenMode()
    {
        _readerZenMode = !_readerZenMode;
        ApplyReaderZenLayout();
        ReaderZenMenuItem.IsChecked = _readerZenMode;
    }

    private void ApplyReaderZenLayout()
    {
        if (ReaderPane.Visibility != Visibility.Visible)
        {
            ResetReaderChromeLayout();
            return;
        }

        if (_readerZenMode)
        {
            _readerPreZenTocExpanded = _readerTocExpanded;
            _readerPreZenAssistantExpanded = _readerAssistantExpanded;
            _readerTocExpanded = false;
            _readerAssistantExpanded = false;
            ReaderHeaderRow.Height = new GridLength(0);
            ReaderHeaderBar.Visibility = Visibility.Collapsed;
            ReaderFooterRow.Height = new GridLength(0);
            ReaderFooterBar.Visibility = Visibility.Collapsed;
            ReaderTocToggleButton.Opacity = 1;
            ReaderAssistantToggleButton.Opacity = 1;
            UpdateReaderZenPopup(true);
        }
        else
        {
            ReaderHeaderRow.Height = new GridLength(52);
            ReaderHeaderBar.Visibility = Visibility.Visible;
            ReaderFooterRow.Height = new GridLength(50);
            ReaderFooterBar.Visibility = Visibility.Visible;
            _readerTocExpanded = _readerPreZenTocExpanded;
            _readerAssistantExpanded = _readerPreZenAssistantExpanded;
            ReaderTocToggleButton.Opacity = _readerTocExpanded ? 0.58 : 1;
            ReaderAssistantToggleButton.Opacity = _readerAssistantExpanded ? 0.58 : 1;
            UpdateReaderZenPopup(false);
        }
        ApplyReaderPanelLayout();
    }

    private void ResetReaderChromeLayout()
    {
        _readerZenMode = false;
        ReaderHeaderRow.Height = new GridLength(52);
        ReaderHeaderBar.Visibility = Visibility.Visible;
        ReaderFooterRow.Height = new GridLength(50);
        ReaderFooterBar.Visibility = Visibility.Visible;
        ReaderZenMenuItem.IsChecked = false;
        ReaderTocToggleButton.Opacity = _readerTocExpanded ? 0.58 : 1;
        ReaderAssistantToggleButton.Opacity = _readerAssistantExpanded ? 0.58 : 1;
        UpdateReaderZenPopup(false);
    }

    // ------------------------------------------------------------------
    // Page-turn animation selection (session state). Applied only to
    // pagination-mode page turns; scroll mode is never animated.
    // ------------------------------------------------------------------

    private void ReaderAnimationItem_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, ReaderAnimationFadeItem))
            _readerPageAnimation = 1;
        else if (ReferenceEquals(sender, ReaderAnimationSlideItem))
            _readerPageAnimation = 2;
        else
            _readerPageAnimation = 0;
    }

    private void SyncReaderPageAnimationMenu()
    {
        ReaderAnimationNoneItem.IsChecked = _readerPageAnimation == 0;
        ReaderAnimationFadeItem.IsChecked = _readerPageAnimation == 1;
        ReaderAnimationSlideItem.IsChecked = _readerPageAnimation == 2;
    }

    // ------------------------------------------------------------------
    // Page turning shared by the prev/next buttons, the keyboard arrows
    // and the pagination-mode click zones. Returns whether a turn happened.
    // ------------------------------------------------------------------

    private async Task<bool> TurnReaderPageAsync(int direction)
    {
        if (ReaderPane.Visibility != Visibility.Visible) return false;
        if (ReaderWebView.CoreWebView2 is null) return false;
        if (!_readerHasToc || _readerChapters.Count == 0) return false;

        var animated = _readerFlowMode == 1 && _readerPageAnimation > 0;
        var crossesChapter = false;
        if (animated)
        {
            crossesChapter = !await CanTurnWithinChapterAsync(direction);
            if (crossesChapter)
            {
                var nextIndex = _readerChapterIndex + direction;
                if (nextIndex < 0 || nextIndex >= _readerChapters.Count) crossesChapter = false;
            }
            if (crossesChapter) await AnimateReaderPageTurnAsync(direction, isOut: true);
        }

        if (await TryTurnWithinChapterAsync(direction)) return true;

        var targetIndex = _readerChapterIndex + direction;
        if (targetIndex < 0 || targetIndex >= _readerChapters.Count)
        {
            if (animated && crossesChapter) await AnimateReaderPageTurnAsync(direction, isOut: false);
            return false;
        }

        _readerChapterIndex = targetIndex;
        _readerNavigateToEnd = direction < 0;
        _readerContinuousLocked = false;
        _readerLastChapterChange = DateTimeOffset.UtcNow;
        UpdateReaderChapterControls();
        _ = SaveReaderProgressThrottledAsync();
        ShowReaderChapter();
        if (animated && crossesChapter) _readerPendingTurnInAnimation = direction;
        return true;
    }

    private async Task<bool> CanTurnWithinChapterAsync(int direction)
    {
        if (ReaderWebView.CoreWebView2 is null) return false;
        var script = $$"""
            (() => {
              const el = document.scrollingElement;
              if (!el) return false;
              const max = Math.max(0, el.scrollWidth - el.clientWidth);
              if ({{direction}} < 0) return el.scrollLeft > 4;
              return el.scrollLeft < max - 4;
            })();
            """;
        try { return await ReaderWebView.CoreWebView2.ExecuteScriptAsync(script) == "true"; }
        catch { return false; }
    }

    private Task AnimateReaderPageTurnAsync(int direction, bool isOut)
    {
        if (_readerPageAnimation == 0)
        {
            ResetReaderWebViewTransform();
            return Task.CompletedTask;
        }

        var width = ReaderWebViewHost.ActualWidth;
        var storyboard = new Storyboard();
        var duration = new Duration(TimeSpan.FromMilliseconds(isOut ? 130 : 190));
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

        if (_readerPageAnimation == 1)
        {
            // Simulated: gentle fade combined with a slight scale.
            var opacity = new DoubleAnimation
            {
                To = isOut ? 0.2 : 1,
                Duration = duration,
                EnableDependentAnimation = true,
                EasingFunction = easing
            };
            Storyboard.SetTarget(opacity, ReaderWebViewHost);
            Storyboard.SetTargetProperty(opacity, "Opacity");
            storyboard.Children.Add(opacity);

            var scaleX = new DoubleAnimation
            {
                To = isOut ? 0.985 : 1,
                Duration = duration,
                EnableDependentAnimation = true,
                EasingFunction = easing
            };
            Storyboard.SetTarget(scaleX, ReaderWebViewTransform);
            Storyboard.SetTargetProperty(scaleX, "ScaleX");
            storyboard.Children.Add(scaleX);

            var scaleY = new DoubleAnimation
            {
                To = isOut ? 0.985 : 1,
                Duration = duration,
                EnableDependentAnimation = true,
                EasingFunction = easing
            };
            Storyboard.SetTarget(scaleY, ReaderWebViewTransform);
            Storyboard.SetTargetProperty(scaleY, "ScaleY");
            storyboard.Children.Add(scaleY);
        }
        else
        {
            // Slide: horizontal translation. Incoming content enters from the
            // direction of travel; the off-screen jump is hidden by the clip.
            var from = isOut ? 0d : (direction > 0 ? width : -width);
            var to = isOut ? (direction > 0 ? -width : width) : 0d;
            var translate = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = duration,
                EnableDependentAnimation = true,
                EasingFunction = easing
            };
            Storyboard.SetTarget(translate, ReaderWebViewTransform);
            Storyboard.SetTargetProperty(translate, "TranslateX");
            storyboard.Children.Add(translate);
        }

        storyboard.Begin();
        return Task.Delay(isOut ? 130 : 190);
    }

    private void ResetReaderWebViewTransform()
    {
        ReaderWebViewHost.Opacity = 1;
        ReaderWebViewTransform.TranslateX = 0;
        ReaderWebViewTransform.ScaleX = 1;
        ReaderWebViewTransform.ScaleY = 1;
    }

    // ------------------------------------------------------------------
    // Keyboard page turning. Left/right arrows only turn pages while the
    // reader is open and the focus is not on a text-editing control.
    // ------------------------------------------------------------------

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ReaderPane.Visibility != Visibility.Visible) return;

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (_readerSearchVisible)
            {
                e.Handled = true;
                HideReaderSearchPanel();
            }
            else if (_readerLayoutPopup?.IsOpen == true)
            {
                e.Handled = true;
                _readerLayoutPopup.IsOpen = false;
            }
            return;
        }

        var controlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control);
        var controlDown = (controlState & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (controlDown)
        {
            if (e.Key == Windows.System.VirtualKey.F)
            {
                e.Handled = true;
                ShowReaderSearchPanel();
                return;
            }
            if (e.Key == Windows.System.VirtualKey.B && !IsReaderTextInputFocused())
            {
                e.Handled = true;
                _ = ToggleReaderBookmarkAsync();
                return;
            }
        }

        if (e.Key != Windows.System.VirtualKey.Left && e.Key != Windows.System.VirtualKey.Right) return;
        if (IsReaderTextInputFocused()) return;
        e.Handled = true;
        var direction = e.Key == Windows.System.VirtualKey.Left ? -1 : 1;
        _ = TurnReaderPageAsync(direction);
    }

    private bool IsReaderTextInputFocused()
    {
        if (Content is not FrameworkElement root || root.XamlRoot is null) return false;
        var focused = FocusManager.GetFocusedElement(root.XamlRoot);
        return focused is TextBox or PasswordBox or RichEditBox or AutoSuggestBox
            || focused is ComboBox { IsEditable: true };
    }

    private async Task<bool> TryTurnWithinChapterAsync(int direction)
    {
        if (_readerAllowedRoot is null || ReaderWebView.CoreWebView2 is null) return false;
        var vertical = _readerLayout.VerticalWriting;
        var script = _readerFlowMode == 0
            ? vertical
                ? $$"""
                    (() => {
                      const el = document.scrollingElement;
                      const step = Math.max(200, window.innerWidth * 0.86);
                      if ({{direction}} < 0 && el.scrollLeft > 4) {
                        window.scrollBy({ left: -step, behavior: 'smooth' }); return true;
                      }
                      if ({{direction}} > 0 && el.scrollLeft + window.innerWidth < el.scrollWidth - 4) {
                        window.scrollBy({ left: step, behavior: 'smooth' }); return true;
                      }
                      return false;
                    })();
                    """
                : $$"""
                    (() => {
                      const el = document.scrollingElement;
                      const step = Math.max(200, window.innerHeight * 0.86);
                      if ({{direction}} < 0 && el.scrollTop > 4) {
                        window.scrollBy({ top: -step, behavior: 'smooth' }); return true;
                      }
                      if ({{direction}} > 0 && el.scrollTop + window.innerHeight < el.scrollHeight - 4) {
                        window.scrollBy({ top: step, behavior: 'smooth' }); return true;
                      }
                      return false;
                    })();
                    """
            : $$"""
                (() => {
                  const el = document.scrollingElement;
                  const step = window.innerWidth;
                  const max = Math.max(0, el.scrollWidth - window.innerWidth);
                  if ({{direction}} < 0 && el.scrollLeft > 4) {
                    window.scrollTo({ left: Math.max(0, el.scrollLeft - step), top: 0, behavior: 'smooth' }); return true;
                  }
                  if ({{direction}} > 0 && el.scrollLeft < max - 4) {
                    window.scrollTo({ left: Math.min(max, el.scrollLeft + step), top: 0, behavior: 'smooth' }); return true;
                  }
                  return false;
                })();
                """;
        try { return await ReaderWebView.CoreWebView2.ExecuteScriptAsync(script) == "true"; }
        catch { return false; }
    }

    private static string GetReaderSectionTextScript() =>
        """
        (() => {
          const body = document.body;
          if (!body) return '';

          let fragment = location.hash ? location.hash.slice(1) : '';
          try { fragment = decodeURIComponent(fragment); } catch { }

          const anchor = fragment ? document.getElementById(fragment) : null;
          const start = anchor?.closest('h1, h2, h3, h4, h5, h6')
            ?? anchor
            ?? body.firstElementChild;
          if (!start) return body.innerText || '';

          const startHeading = /^H([1-6])$/i.exec(start.tagName);
          const startLevel = startHeading ? Number(startHeading[1]) : 0;
          const pieces = [];
          let current = start;

          while (current) {
            if (current !== start) {
              const heading = /^H([1-6])$/i.exec(current.tagName);
              if (heading && (startLevel === 0 || Number(heading[1]) <= startLevel)) break;
            }

            const value = (current.innerText || '').trim();
            if (value) pieces.push(value);
            current = current.nextElementSibling;
          }

          return pieces.join('\n\n') || start.innerText || body.innerText || '';
        })();
        """;

    private async Task<string> ExecuteReaderStringScriptAsync(string script)
    {
        if (ReaderWebView.CoreWebView2 is null) return string.Empty;
        try
        {
            var json = await ReaderWebView.CoreWebView2.ExecuteScriptAsync(script);
            return JsonSerializer.Deserialize<string>(json) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeReaderText(string text)
    {
        return string.Join(" ", text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
    }

    private void CloseReaderButton_Click(object sender, RoutedEventArgs e) => CloseReader();

    private void CloseReader()
    {
        ReaderPane.Visibility = Visibility.Collapsed;
        ReaderBrandText.Visibility = Visibility.Collapsed;
        StopReaderScrollPoll();
        StopReaderToolsTimers();
        UninstallReaderMouseHook();
        _readerRelayoutCancellation?.Cancel();
        _readerRelayoutCancellation?.Dispose();
        _readerRelayoutCancellation = null;
        UpdateReaderAssistantPopup(false);
        SetReaderAiSettingsVisible(false);
        SetReaderTocTab(bookmarkTab: false);
        _readerChapters = [];
        _readerNavigation = [];
        _readerChapterIndex = -1;
        _readerAllowedRoot = null;
        _readerAllowedFile = null;
        _readerNavigateToEnd = false;
        _readerHasToc = false;
        _readerZenMode = false;
        _readerContinuousLocked = false;
        _readerPendingTurnInAnimation = null;
        ResetReaderChromeLayout();
        ReaderTocList.ItemsSource = null;
        ReaderTocSearchBox.Text = string.Empty;
        ReaderCoverImage.Source = null;
        ResetReaderAssistant();
        EndReaderSession();
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
        if (!allowed)
        {
            args.Cancel = true;
            return;
        }

        if (_readerAllowedRoot is not null)
        {
            var chapterIndex = _readerChapters.ToList().FindIndex(chapter =>
                Path.GetFullPath(chapter).Equals(target, StringComparison.OrdinalIgnoreCase));
            if (chapterIndex >= 0)
            {
                _readerChapterIndex = chapterIndex;
                UpdateReaderChapterControls();
                var targetWithoutFragment = new Uri(target).AbsoluteUri;
                var selectedItem = _readerNavigation.FirstOrDefault(item =>
                    item.Target.Equals(args.Uri, StringComparison.OrdinalIgnoreCase))
                    ?? _readerNavigation.FirstOrDefault(item =>
                        item.Target.StartsWith(targetWithoutFragment, StringComparison.OrdinalIgnoreCase));
                SelectReaderTocItem(selectedItem);
            }
        }
    }

    private async void ReaderWebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        // Always restore the page-turn transform, even on a failed navigation,
        // so the reader content is never left off-screen.
        if (_readerPendingTurnInAnimation is int pendingDirection)
        {
            _readerPendingTurnInAnimation = null;
            await AnimateReaderPageTurnAsync(pendingDirection, isOut: false);
        }
        if (!args.IsSuccess) return;
        await ApplyReaderAppearanceAsync();
        await ApplyReaderAnnotationsToPageAsync();
        await ConfigureReaderFootnoteHoverAsync();
        await ScrollToPendingReaderAnnotationAsync();
        await ScrollToPendingReaderChunkAsync();
        await ScrollToPendingReaderBookmarkAsync();
        await ApplyReaderRestorePositionAsync();
        if (_readerNavigateToEnd)
        {
            await MoveReaderToEndAsync();
            _readerNavigateToEnd = false;
        }
        await PrimeReaderScrollEdgesAsync();
        await RefreshReaderProgressAsync();
        _ = SaveReaderProgressThrottledAsync();
        if (_readerFlowMode == 0 && _readerContinuousLocked)
            _ = SkipShortChapterIfNeededAsync();
    }

    private async Task ApplyReaderAppearanceAsync()
    {
        if (_readerAllowedRoot is null || ReaderWebView.CoreWebView2 is null) return;
        const string background = "#FFFFFF";
        const string foreground = "#111111";
        const string link = "#222222";
        var fontPercent = (int)Math.Round(_readerLayout.FontScale * 100);
        var vertical = _readerFlowMode == 0 && _readerLayout.VerticalWriting;
        var flowCss = _readerFlowMode == 0
            ? vertical
                ? "html { height: 100%; overflow: hidden !important; } body { height: 100%; overflow: visible !important; box-sizing: border-box; }"
                : "html, body { min-height: 100%; overflow-x: hidden !important; }"
            : "html { height: 100%; overflow: hidden !important; }"
              + " body { height: 100%; overflow: visible !important; padding: 48px 24px 64px !important; box-sizing: border-box;"
              + " column-width: calc(100vw - 96px); column-gap: 48px; column-fill: auto; max-width: none !important; }";
        var lineHeight = _readerLayout.LineHeight.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var bodyLayoutCss = vertical
            ? $"max-width: none !important; writing-mode: vertical-rl !important; text-orientation: mixed;"
              + " margin: 0 auto !important; padding: 58px 24px 100px 24px !important;"
            : $"max-width: {(int)_readerLayout.MaxWidth}px; margin: 0 auto !important;"
              + $" padding: 58px {(int)_readerLayout.BodyPadding}px 100px !important;";
        var bodyTextCss = vertical
            ? "overflow-wrap: anywhere; box-sizing: border-box; line-break: strict; word-break: normal;"
            : "overflow-wrap: anywhere; box-sizing: border-box; line-break: strict; word-break: normal; text-align: justify;";
        var fontFamily = BuildReaderFontStack(_readerLayout.FontFamily);
        ReaderWebView.DefaultBackgroundColor = Colors.White;
        var script = $$"""
            (() => {
              let style = document.getElementById('kkindle-reader-style');
              if (!style) {
                style = document.createElement('style');
                style.id = 'kkindle-reader-style';
                document.head.appendChild(style);
              }
              style.textContent = `
                html { font-size: {{fontPercent}}% !important; text-rendering: optimizeLegibility; }
                html, body { background: {{background}} !important; color: {{foreground}} !important; }
                body { {{bodyLayoutCss}}
                       font-family: {{fontFamily}} !important;
                       font-size: 1rem !important; line-height: {{lineHeight}} !important; letter-spacing: 0.012em;
                       {{bodyTextCss}} }
                ruby { ruby-align: center !important; }
                rt { font-size: 0.5em !important; color: inherit !important; }
                p { margin: 0.55em 0 1.05em !important; }
                li, blockquote { font-size: 1rem !important; line-height: 1.78 !important; }
                h1, h2, h3, h4 { color: {{foreground}} !important; line-height: 1.35 !important; margin: 1.35em 0 0.72em !important; }
                blockquote { margin: 1.4em 0 !important; padding: 0.2em 1.1em !important; border-left: 3px solid {{link}} !important; opacity: 0.88; }
                {{flowCss}}
                a { color: {{link}} !important; }
                img, svg { display: block; max-width: 100% !important; height: auto !important; margin: 1.8em auto !important; }
                pre, table { max-width: 100%; overflow-x: auto; }
                hr { border: 0 !important; border-top: 1px solid {{link}} !important; opacity: 0.24; margin: 2em 0 !important; }
              `;
            })();
            """;
        try { await ReaderWebView.CoreWebView2.ExecuteScriptAsync(script); }
        catch { /* Some fixed-layout EPUB pages don't expose a normal document head. */ }
        if (_readerFlowMode == 1)
        {
            // Pagination mode: the document may still carry a vertical scroll
            // position from a previous flow/zoom/layout state. Pin the reading
            // area to the top of the current column so each viewport shows one
            // full page instead of a vertically offset strip.
            try
            {
                await ReaderWebView.CoreWebView2.ExecuteScriptAsync(
                    "window.scrollTo({ left: (document.scrollingElement||document.documentElement).scrollLeft, top: 0 });");
            }
            catch { }
        }
    }

    private async Task ResetReaderPositionAsync()
    {
        if (ReaderWebView.CoreWebView2 is null) return;
        try { await ReaderWebView.CoreWebView2.ExecuteScriptAsync("window.scrollTo({ left: 0, top: 0 });"); }
        catch { }
    }

    private async Task MoveReaderToEndAsync()
    {
        if (ReaderWebView.CoreWebView2 is null) return;
        var script = _readerFlowMode switch
        {
            0 when _readerLayout.VerticalWriting =>
                "window.scrollTo({ left: document.scrollingElement.scrollWidth, top: 0, behavior: 'instant' });",
            0 => "window.scrollTo({ top: document.scrollingElement.scrollHeight, behavior: 'instant' });",
            _ => "window.scrollTo({ left: document.scrollingElement.scrollWidth, top: 0, behavior: 'instant' });"
        };
        try { await ReaderWebView.CoreWebView2.ExecuteScriptAsync(script); }
        catch { }
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
