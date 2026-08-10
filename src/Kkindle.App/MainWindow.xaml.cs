using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
    private readonly IBookFormatConverter _formatConverter;
    private readonly ReaderFormatCacheService _readerFormatCache;
    private readonly IKindleDeviceService _kindle;
    private readonly EpubReaderPreparationService _epubReader;
    private readonly DispatcherQueueTimer _deviceTimer;
    private readonly DispatcherQueueTimer _deviceConnectedToastTimer;
    private Book? _selectedBook;
    private bool _detailFavorite;
    private LibraryReadingStatus _detailReadingStatus;
    private IReadOnlyList<KindleDevice> _devices = [];
    private bool _isRefreshingDevices;
    private bool _isTransferring;
    private CancellationTokenSource? _transferCancellation;
    private bool _isUpdatingFilters;
    private string? _scannedDeviceId;
    private double _deviceUsedRatio;
    private string? _acceptedDeviceId;
    private string? _ignoredDeviceId;
    private string? _manuallyDisconnectedDeviceId;
    private Button? _activeNavigationButton;
    private Button? _activeNavigationSectionButton;
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
    private int _readerFlowMode = 1;
    private bool _isUpdatingReaderToc;
    private bool _isUpdatingReaderProgress;
    private bool _readerNavigateToEnd;
    private bool _readerTocExpanded = true;
    private bool _readerTocMinimal;
    private bool _readerAssistantExpanded = true;
    private bool _readerHasToc;
    private bool _readerZenMode;
    private bool _readerPreZenTocExpanded = true;
    private bool _readerPreZenTocMinimal;
    private bool _readerPreZenAssistantExpanded = true;
    private const int ReaderAnimationNone = 0;
    private const int ReaderAnimationFade = 1;
    private const int ReaderAnimationSlide = 2;
    private int _readerPageAnimation = ReaderAnimationFade;
    private bool _readerContinuousLocked;
    private int _readerContinuousDirection = 1;
    private DateTimeOffset _readerLastChapterChange = DateTimeOffset.MinValue;
    // The incoming transition to play once the new chapter's first screen is
    // ready (NavigationCompleted + essential preparation). The style is pinned
    // at navigation time so a "jump" navigation (TOC/search/bookmark/annotation/
    // AI/progress slider) uses the animation selected in the reader menu.
    private ReaderTurnInAnimation? _readerPendingTurnInAnimation;
    // The most recently requested navigation target. NavigationCompleted for a
    // superseded navigation must be ignored, otherwise a stale handler could
    // consume the pending turn-in and run preparation against an older page.
    private Uri? _readerPendingNavigationTarget;
    // The logical target also preserves a same-document TOC fragment when
    // WebView2 performs only an in-page jump without a document lifecycle.
    private Uri? _readerActiveLocationTarget;
    // Why the pending navigation was requested (TOC / search / bookmark /
    // annotation / AI source / progress slider / open-book restore). An
    // explicit user target outranks any automatic breakpoint restore, and the
    // pending location work in NavigationCompleted is chosen from this intent.
    private ReaderNavigationIntent _readerNavigationIntent = ReaderNavigationIntent.None;
    private CancellationTokenSource? _readerRelayoutCancellation;
    private DispatcherQueueTimer? _readerScrollPollTimer;
    private bool _readerPollRunning;
    private bool _readerLastNearTop = true;
    private bool _readerLastNearBottom;
    private IntPtr _readerMouseHook;
    private bool _readerMouseDownInside;
    private POINT _readerMouseDownPoint;
    private int _readerWheelDeltaRemainder;
    private LowLevelMouseProc? _readerMouseProc;
    // The low-level mouse hook runs on a system thread and must NEVER touch
    // XAML objects (cross-thread DependencyObject access can block the hook
    // thread, and UnhookWindowsHookEx then deadlocks the UI thread). The hook
    // callback only reads these plain cached values; the click itself is
    // dispatched back to the UI thread.
    private volatile bool _readerHookEnabled;
    private Windows.Foundation.Rect _readerWebViewScreenRect;
    // Closing guard: entered before any reader teardown starts so repeated
    // X/返回书架/window-close calls stay idempotent.
    private bool _readerCloseRequested;
    private bool _readerCloseInProgress;
    // Chapter transition state: prevents the scroll poll / repeated input from
    // interleaving with an in-flight cross-chapter animation or navigation.
    private bool _readerTransitionActive;
    private CancellationTokenSource? _readerChapterTransitionCancellation;
    private int _readerChapterTransitionSequence;
    // Same-chapter jumps do not produce NavigationCompleted, so they need their
    // own sequence guard to prevent an older async location task from winning.
    private int _readerLocationSequence;
    private bool _readerOpenInProgress;
    private Task? _readerWebViewInitializationTask;

    // Direction + animation style for the incoming chapter transition. Style is
    // 1 = fade (淡入淡出), 2 = slide (左右滑动). Recorded when the navigation starts
    // so a far-chapter jump never plays a long per-page-looking slide.
    private readonly record struct ReaderTurnInAnimation(int Direction, int Style);

    public MainWindow(
        AppPaths paths,
        IBookLibraryService library,
        IBookFormatConverter formatConverter,
        IKindleDeviceService kindle,
        ReaderDataService readerData,
        EpubBookContentService bookContent,
        EpubFootnoteResolver footnotes,
        AiSettingsStore aiSettingsStore,
        AiChatClient aiChatClient,
        IZLibraryService zLibraryService,
        ZLibrarySettingsStore zLibrarySettingsStore)
    {
        _paths = paths;
        _library = library;
        _backupService = new AppBackupService(paths);
        _appSettingsStore = new AppSettingsStore(paths);
        _dictionaryService = new DictionaryService(paths);
        _fontLibrary = new FontLibraryService(paths);
        _pdfTextService = new PdfTextService();
        _formatConverter = formatConverter;
        _readerFormatCache = new ReaderFormatCacheService(paths, formatConverter);
        _kindle = kindle;
        _kindleEmailSettingsStore = new KindleEmailSettingsStore(paths);
        _kindleEmailSender = new KindleEmailSender();
        _readerData = readerData;
        _bookContent = bookContent;
        _footnotes = footnotes;
        _aiSettingsStore = aiSettingsStore;
        _aiChatClient = aiChatClient;
        _zLibraryService = zLibraryService;
        _zLibrarySettingsStore = zLibrarySettingsStore;
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
        _deviceConnectedToastTimer = DispatcherQueue.CreateTimer();
        _deviceConnectedToastTimer.Interval = TimeSpan.FromSeconds(3);
        _deviceConnectedToastTimer.Tick += (_, _) =>
        {
            _deviceConnectedToastTimer.Stop();
            DeviceConnectedToast.Visibility = Visibility.Collapsed;
        };
        RootGrid.Loaded += MainWindow_Loaded;
        RootGrid.Loaded += (_, _) => EnsureInteractiveControlToolTips(RootGrid);
        RootGrid.KeyDown += RootGrid_KeyDown;
    }

    public LibraryViewModel ViewModel { get; }
    public ObservableCollection<KindleBookCardViewModel> DeviceBooks { get; } = [];
    public ObservableCollection<KindleDeviceResource> DeviceResources { get; } = [];
    public ObservableCollection<ReadingMaterialItemViewModel> ReadingMaterials { get; } = [];

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
        // Reader teardown must never block the UI thread. The previous
        // implementation synchronously waited on FlushReaderSessionAsync()
        // (GetAwaiter().GetResult()); a WebView ExecuteScriptAsync that never
        // returns (navigation/shutdown race) froze the whole window. Now we
        // stop all reader machinery first, then fire the persistence flush
        // fire-and-forget with a short timeout (it uses the last captured
        // progress snapshot and never touches the WebView).
        _readerCloseRequested = true;
        UninstallReaderMouseHook();
        StopReaderScrollPoll();
        StopReaderToolsTimers();
        _readerChapterTransitionCancellation?.Cancel();
        _readerChapterTransitionCancellation?.Dispose();
        _readerChapterTransitionCancellation = null;
        _readerRelayoutCancellation?.Cancel();
        _readerRelayoutCancellation?.Dispose();
        _readerFeatureCancellation?.Cancel();
        _readerFeatureCancellation?.Dispose();
        _readerAiCancellation?.Cancel();
        _readerAiCancellation?.Dispose();
        _readerAiModelListCancellation?.Cancel();
        _readerAiModelListCancellation?.Dispose();
        _kindleEmailSendCancellation?.Cancel();
        _bookFormatConversionCancellation?.Cancel();
        _bookFormatConversionCancellation?.Dispose();
        _bookFormatConversionCancellation = null;
        _automaticReaderFormatGenerationCancellation?.Cancel();
        _automaticReaderFormatGenerationCancellation = null;
        _deviceResourceCancellation?.Cancel();
        _deviceResourceCancellation?.Dispose();
        _deviceResourceCancellation = null;
        _readingMaterialsCancellation?.Cancel();
        _readingMaterialsCancellation?.Dispose();
        _readingMaterialsCancellation = null;
        _ = FlushReaderSessionSafelyAsync(skipWebViewCapture: true);

        _deviceTimer.Stop();
        _deviceConnectedToastTimer.Stop();
        HideTransferToast();
        _transferCancellation?.Cancel();
        _transferCancellation?.Dispose();
        _doubanMatchCancellation?.Cancel();
        _doubanMatchCancellation?.Dispose();
        _doubanMetadataService.Dispose();
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
        SettingsDataPathText.Text = _paths.Data;
        DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () => _ = WarmReaderWebViewAsync());
        await LoadProductivityStateAsync();
        _zLibrarySettings = await _zLibrarySettingsStore.LoadAsync();
        UpdateZLibraryAccountStatus();
        await RefreshLibraryAsync();
        await RefreshDevicesAsync();
    }

    private async Task WarmReaderWebViewAsync()
    {
        try { await EnsureReaderWebViewReadyAsync(); }
        catch { }
    }

    private Task EnsureReaderWebViewReadyAsync()
    {
        if (ReaderWebView.CoreWebView2 is not null)
        {
            ConfigureReaderWebView();
            return Task.CompletedTask;
        }

        return _readerWebViewInitializationTask ??= InitializeReaderWebViewAsync();
    }

    private async Task InitializeReaderWebViewAsync()
    {
        try
        {
            await ReaderWebView.EnsureCoreWebView2Async();
            ConfigureReaderWebView();
        }
        catch
        {
            _readerWebViewInitializationTask = null;
            throw;
        }
    }

    private async Task RefreshLibraryAsync()
    {
        try
        {
            await ViewModel.RefreshAsync();
            UpdateLibraryPresentationState();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("无法读取书库", ex.Message);
        }
    }

    private void UpdateLibraryPresentationState()
    {
        LibrarySummaryText.Text = ViewModel.StatusText;
        SidebarCountText.Text = ViewModel.Books.Count.ToString();
        UpdateFilterControls();
        UpdateEmptyLibraryState();
        ApplyBookConversionCardState();
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
                _manuallyDisconnectedDeviceId = null;
                SetDisconnectedDeviceState();
                return;
            }

            var device = detectedDevices[0];
            if (string.Equals(
                    _manuallyDisconnectedDeviceId,
                    device.Identity,
                    StringComparison.OrdinalIgnoreCase))
            {
                SetDisconnectedDeviceState($"已断开 {device.Name} · 点击刷新设备可重新连接");
                return;
            }
            if (_manuallyDisconnectedDeviceId is not null)
                _manuallyDisconnectedDeviceId = null;
            var wasConnectedToSameDevice = _devices.Count > 0
                && string.Equals(_devices[0].Identity, device.Identity, StringComparison.OrdinalIgnoreCase);
            if (_isTransferring
                && _devices.Count > 0
                && !string.Equals(_devices[0].Identity, device.Identity, StringComparison.OrdinalIgnoreCase))
                _transferCancellation?.Cancel();
            if (!string.Equals(_acceptedDeviceId, device.Identity, StringComparison.OrdinalIgnoreCase))
            {
                if (_appSettings.AutoConnectDevice)
                {
                    _acceptedDeviceId = device.Identity;
                    _ignoredDeviceId = null;
                }
                else
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
            }

            _devices = [device];
            KindleStatusText.Text = device.Name;
            KindleConnectionText.Text = $"{device.ConnectionLabel} · 已连接";
            EjectDeviceButton.Visibility = Visibility.Visible;
            EjectDeviceButton.IsEnabled = true;
            ToolTipService.SetToolTip(EjectDeviceButton, "弹出设备");
            DeviceStorageText.Text = device.CapacityLabel;
            _deviceUsedRatio = device.TotalBytes <= 0
                ? 0
                : Math.Clamp((device.TotalBytes - device.FreeBytes) / (double)device.TotalBytes, 0, 1);
            UpdateDeviceStorageBar();
            DeviceNameText.Text = $"{device.Name} · {device.ConnectionLabel}";
            if (!wasConnectedToSameDevice) ShowDeviceConnectedToast(device);
            if (!string.Equals(_scannedDeviceId, device.Identity, StringComparison.OrdinalIgnoreCase))
                await ScanDeviceBooksAsync(device);
            if (DeviceResourcePage.Visibility == Visibility.Visible
                && !_deviceResourceOperationInProgress
                && (!string.Equals(_scannedResourceDeviceId, device.Identity, StringComparison.OrdinalIgnoreCase)
                    || _scannedResourceKind != _deviceResourceKind))
                await RefreshDeviceResourcesAsync();
            if (ReadingMaterialsPage.Visibility == Visibility.Visible
                && !string.Equals(_readingMaterialsDeviceId, device.Identity, StringComparison.OrdinalIgnoreCase))
                await RefreshReadingMaterialsAsync();
        }
        catch
        {
            SetDisconnectedDeviceState("设备状态读取失败");
        }
        finally { _isRefreshingDevices = false; }
    }

    private void SetDisconnectedDeviceState(string? detail = null)
    {
        var refreshReadingMaterials = ReadingMaterialsPage.Visibility == Visibility.Visible
            && (_readingMaterialsDeviceId is not null
                || _allReadingMaterials.Any(item => item.Source == ReadingMaterialSource.Kindle));
        _deviceResourceCancellation?.Cancel();
        _readingMaterialsCancellation?.Cancel();
        _devices = [];
        _deviceConnectedToastTimer.Stop();
        DeviceConnectedToast.Visibility = Visibility.Collapsed;
        _scannedDeviceId = null;
        _scannedResourceDeviceId = null;
        _scannedResourceKind = null;
        _readingMaterialsDeviceId = null;
        DeviceBooks.Clear();
        KindleStatusText.Text = "无设备连接";
        KindleConnectionText.Text = detail ?? string.Empty;
        EjectDeviceButton.Visibility = Visibility.Visible;
        EjectDeviceButton.IsEnabled = false;
        ToolTipService.SetToolTip(EjectDeviceButton, "未连接设备");
        DeviceStorageText.Text = "无存储信息";
        _deviceUsedRatio = 0;
        UpdateDeviceStorageBar();
        DeviceNameText.Text = "未检测到设备";
        DeviceBookCountText.Text = "0";
        DeviceResources.Clear();
        DeviceResourceList.SelectedItem = null;
        DeviceResourceDeviceText.Text = "未检测到设备";
        DeviceResourceStatusText.Text = detail ?? "请连接 Kindle";
        DeviceResourceCountText.Text = "0 个文件";
        ImportDeviceResourceButton.IsEnabled = false;
        ExportDeviceResourceButton.IsEnabled = false;
        DeleteDeviceResourceButton.IsEnabled = false;
        if (refreshReadingMaterials) _ = RefreshReadingMaterialsAsync();
    }

    private void ShowDeviceConnectedToast(KindleDevice device)
    {
        DeviceConnectedToastText.Text = $"{device.Name} 已连接";
        DeviceConnectedToast.Visibility = Visibility.Visible;
        _deviceConnectedToastTimer.Stop();
        _deviceConnectedToastTimer.Start();
    }

    private async Task ScanDeviceBooksAsync(KindleDevice device)
    {
        DeviceNameText.Text = $"{device.Name} · 正在读取书籍与封面…";
        var books = await _kindle.ScanBooksAsync(device);
        DeviceBooks.Clear();
        foreach (var book in books) DeviceBooks.Add(new KindleBookCardViewModel(book));
        DeviceBookCountText.Text = books.Count.ToString();
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
            CategoryFilterBox.ItemsSource = new[] { "全部分类" }.Concat(ViewModel.AvailableCategories).ToArray();
            ReadingStatusFilterBox.ItemsSource = new[] { "全部状态", "待读", "阅读中", "已读" };
            LibrarySortBox.ItemsSource = new[] { "最近更新", "标题", "作者", "导入时间", "阅读状态" };
            AuthorFilterBox.SelectedItem = ViewModel.AuthorFilter ?? "全部作者";
            TagFilterBox.SelectedItem = ViewModel.TagFilter ?? "全部标签";
            FormatFilterBox.SelectedItem = ViewModel.FormatFilter?.ToUpperInvariant() ?? "全部格式";
            CategoryFilterBox.SelectedItem = ViewModel.CategoryFilter ?? "全部分类";
            ReadingStatusFilterBox.SelectedIndex = ViewModel.ReadingStatusFilter is { } status ? (int)status + 1 : 0;
            LibrarySortBox.SelectedIndex = (int)ViewModel.SortMode;
            FavoritesOnlyCheck.IsChecked = ViewModel.FavoritesOnly;
            var activeCount = new[] { ViewModel.AuthorFilter, ViewModel.TagFilter, ViewModel.FormatFilter, ViewModel.CategoryFilter }
                .Count(value => !string.IsNullOrWhiteSpace(value));
            if (ViewModel.ReadingStatusFilter is not null) activeCount++;
            if (ViewModel.FavoritesOnly) activeCount++;
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
        ViewModel.CategoryFilter = CategoryFilterBox.SelectedIndex <= 0 ? null : CategoryFilterBox.SelectedItem as string;
        ViewModel.ReadingStatusFilter = ReadingStatusFilterBox.SelectedIndex <= 0
            ? null
            : (LibraryReadingStatus)(ReadingStatusFilterBox.SelectedIndex - 1);
        ViewModel.SortMode = LibrarySortBox.SelectedIndex < 0
            ? LibrarySortMode.UpdatedDescending
            : (LibrarySortMode)LibrarySortBox.SelectedIndex;
        await RefreshLibraryAsync();
    }

    private async void FavoritesOnlyCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingFilters) return;
        ViewModel.FavoritesOnly = FavoritesOnlyCheck.IsChecked == true;
        await RefreshLibraryAsync();
    }

    private async void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.AuthorFilter = null;
        ViewModel.TagFilter = null;
        ViewModel.FormatFilter = null;
        ViewModel.CategoryFilter = null;
        ViewModel.ReadingStatusFilter = null;
        ViewModel.FavoritesOnly = false;
        ViewModel.SortMode = LibrarySortMode.UpdatedDescending;
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
            var automaticFormats = await AutoGenerateReaderFormatsForImportsAsync(result);
            if (automaticFormats.Failures.Count > 0)
            {
                await ShowMessageAsync(
                    "书籍已导入，但格式补齐失败",
                    string.Join("\n", automaticFormats.Failures.Take(5)));
            }
            UpdateLibraryPresentationState();
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

    private void SelectBook(Book book)
    {
        _selectedBook = book;
        DetailsTitleBox.Text = book.Title;
        DetailsAuthorsBox.Text = book.Authors;
        DetailsSeriesBox.Text = book.Series ?? string.Empty;
        DetailsPublisherBox.Text = book.Publisher ?? string.Empty;
        DetailsPublishDateBox.Text = book.PublishDate ?? string.Empty;
        DetailsIsbnBox.Text = book.Isbn ?? string.Empty;
        DetailsPageCountBox.Text = book.PageCount ?? string.Empty;
        DetailsBindingBox.Text = book.Binding ?? string.Empty;
        DetailsDoubanRatingBox.Text = book.DoubanRating is null
            ? string.Empty
            : $"{book.DoubanRating:0.0}（{book.DoubanRatingCount ?? 0} 人评价）";
        DetailsTagsBox.Text = book.Tags;
        DetailsCategoryBox.Text = book.Category;
        _detailFavorite = book.IsFavorite;
        _detailReadingStatus = book.ReadingStatus;
        UpdateDetailStateButtons();
        DetailsDescriptionBox.Text = book.Description ?? string.Empty;
        DetailCoverImage.Source = null;
        ResetDetailCoverFrame();
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
        _selectedBook.Publisher = NullIfWhiteSpace(DetailsPublisherBox.Text);
        _selectedBook.PublishDate = NullIfWhiteSpace(DetailsPublishDateBox.Text);
        _selectedBook.Isbn = NullIfWhiteSpace(DetailsIsbnBox.Text);
        _selectedBook.PageCount = NullIfWhiteSpace(DetailsPageCountBox.Text);
        _selectedBook.Binding = NullIfWhiteSpace(DetailsBindingBox.Text);
        _selectedBook.Tags = DetailsTagsBox.Text.Trim();
        _selectedBook.Category = DetailsCategoryBox.Text.Trim();
        _selectedBook.IsFavorite = _detailFavorite;
        _selectedBook.ReadingStatus = _detailReadingStatus;
        _selectedBook.Description = string.IsNullOrWhiteSpace(DetailsDescriptionBox.Text) ? null : DetailsDescriptionBox.Text.Trim();
        await _library.UpdateMetadataAsync(_selectedBook);
        await RefreshLibraryAsync();
        SelectBook(_selectedBook);
        TaskStatusText.Text = "元数据已保存";
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void DetailsFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        _detailFavorite = !_detailFavorite;
        UpdateDetailStateButtons();
    }

    private void DetailsReadingStatusButton_Click(object sender, RoutedEventArgs e)
    {
        _detailReadingStatus = _detailReadingStatus switch
        {
            LibraryReadingStatus.Unread => LibraryReadingStatus.Reading,
            LibraryReadingStatus.Reading => LibraryReadingStatus.Finished,
            _ => LibraryReadingStatus.Unread
        };
        UpdateDetailStateButtons();
    }

    private void UpdateDetailStateButtons()
    {
        DetailsFavoriteIcon.Glyph = _detailFavorite ? "\uE735" : "\uE734";
        var favoriteLabel = _detailFavorite ? "已收藏；点击取消收藏" : "未收藏；点击加入收藏";
        ToolTipService.SetToolTip(DetailsFavoriteButton, favoriteLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DetailsFavoriteButton, favoriteLabel);

        var (glyph, label) = _detailReadingStatus switch
        {
            LibraryReadingStatus.Reading => ("\uE736", "阅读中；点击标记为已读"),
            LibraryReadingStatus.Finished => ("\uE73E", "已读；点击重置为待读"),
            _ => ("\uE8A4", "待读；点击标记为阅读中")
        };
        DetailsReadingStatusIcon.Glyph = glyph;
        ToolTipService.SetToolTip(DetailsReadingStatusButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DetailsReadingStatusButton, label);
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
            using var prepared = await PrepareKindleTransferAsync(
                _selectedBook,
                progress,
                _transferCancellation.Token);
            await _kindle.SendBookAsync(
                device,
                prepared.File,
                prepared.SourcePath,
                progress,
                _transferCancellation.Token);
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

    private async void DeleteBookButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBook is null) return;
        await DeleteEntireBookAsync(_selectedBook);
    }

    private void CloseDetailButton_Click(object sender, RoutedEventArgs e) => CloseDetails();

    private void ResetDetailCoverFrame()
    {
        DetailCoverBorder.Width = 180;
        DetailCoverBorder.Height = 250;
    }

    private void CloseDetails()
    {
        DetailPane.Visibility = Visibility.Collapsed;
        DetailColumn.Width = new GridLength(0);
    }

    private void LibraryViewToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var showList = BookGrid.Visibility == Visibility.Visible;
        BookGrid.Visibility = showList ? Visibility.Collapsed : Visibility.Visible;
        BookList.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;

        LibraryViewToggleIcon.Symbol = showList ? Symbol.ViewAll : Symbol.Bullets;
        var nextView = showList ? "网格" : "列表";
        var label = $"切换到{nextView}视图";
        AutomationProperties.SetName(LibraryViewToggleButton, label);
        ToolTipService.SetToolTip(LibraryViewToggleButton, label);
    }

    private void FilterButton_Click(object sender, RoutedEventArgs e) =>
        FilterPanel.Visibility = FilterPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    private async void MoreButton_Click(object sender, RoutedEventArgs e) => await ShowMessageAsync("Kkindle", $"便携数据目录：{_paths.Data}");
    private async void AddTagButton_Click(object sender, RoutedEventArgs e) => await ShowMessageAsync("标签", "可以在书籍详情中直接编辑标签，多个标签用逗号分隔。");
    private async void AddCategoryButton_Click(object sender, RoutedEventArgs e) => await ShowMessageAsync("分类", "分类功能将在书库筛选基础完成后接入。");
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void KindleBooksButton_Click(object sender, RoutedEventArgs e) => OpenDevicePage();

    private void OpenDevicePage()
    {
        SetActiveNavigation(KindleBooksButton);
        DevicePageTitleText.Text = "Kindle书库";
        DeviceBookList.Visibility = Visibility.Visible;
        LibraryPane.Visibility = Visibility.Collapsed;
        SettingsPane.Visibility = Visibility.Collapsed;
        ZLibraryPage.Visibility = Visibility.Collapsed;
        DeviceResourcePage.Visibility = Visibility.Collapsed;
        ReadingMaterialsPage.Visibility = Visibility.Collapsed;
        ReadingDashboardPage.Visibility = Visibility.Collapsed;
        DetailPane.Visibility = Visibility.Collapsed;
        DetailColumn.Width = new GridLength(0);
        DevicePage.Visibility = Visibility.Visible;
    }

    private void AllBooksButton_Click(object sender, RoutedEventArgs e) => ShowLibrary();

    private async void RefreshDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        _ignoredDeviceId = null;
        _manuallyDisconnectedDeviceId = null;
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
            _ignoredDeviceId = null;
            _manuallyDisconnectedDeviceId = device.Identity;
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
        DeviceResourcePage.Visibility = Visibility.Collapsed;
        ReadingMaterialsPage.Visibility = Visibility.Collapsed;
        ReadingDashboardPage.Visibility = Visibility.Collapsed;
        SettingsPane.Visibility = Visibility.Collapsed;
        ZLibraryPage.Visibility = Visibility.Collapsed;
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
        if (_readerOpenInProgress) return;

        var file = ReaderBookSelectionPolicy.SelectPreferred(book.Files, _appSettings.PreferredOpenFormat);
        if (file is null)
        {
            await ShowMessageAsync("暂不支持阅读", "内置阅读器目前支持 EPUB、PDF、AZW3 和 MOBI。");
            return;
        }

        await OpenBookAsync(book, file);
    }

    private async Task OpenBookAsync(Book book, BookFile file)
    {
        if (_readerOpenInProgress) return;

        _readerOpenInProgress = true;
        try
        {
            var path = _library.GetAbsoluteFilePath(file);
            if (!File.Exists(path)) throw new FileNotFoundException("书籍文件不存在。", path);

            // A fresh reader session: re-arm the close guard and clear any
            // leftover transition/animation state from a previous session.
            _readerCloseInProgress = false;
            _readerCloseRequested = false;
            _readerTransitionActive = false;
            _readerPendingTurnInAnimation = null;
            _readerPendingNavigationTarget = null;
            _readerNavigationIntent = ReaderNavigationIntent.None;
            _readerChapterTransitionCancellation?.Cancel();
            _readerChapterTransitionCancellation?.Dispose();
            _readerChapterTransitionCancellation = null;
            ResetReaderWebViewTransform();
            BeginReaderSession(book, file);
            var readerToken = _readerFeatureCancellation!.Token;
            var webViewTask = EnsureReaderWebViewReadyAsync();
            var sessionDataTask = LoadReaderSessionDataAsync(readerToken);
            await Task.WhenAll(webViewTask, sessionDataTask);
            ReaderBookInfoText.Text = $"{book.Title} · {file.Format.ToUpperInvariant()}";
            ReaderPane.Visibility = Visibility.Visible;
            ReaderBrandText.Visibility = Visibility.Visible;
            ReaderPane.UpdateLayout();
            _readerTocExpanded = true;
            _readerTocMinimal = false;
            _readerAssistantExpanded = true;
            _readerFlowMode = _readerLayout.FlowMode;
            _readerZenMode = false;
            _readerContinuousLocked = false;
            ResetReaderChromeLayout();
            UpdateReaderZoom();
            UpdateReaderFlowButton();
            SyncReaderPageAnimationMenu();
            ApplyReaderPanelLayout();

            if (file.Format.Equals("pdf", StringComparison.OrdinalIgnoreCase))
            {
                await OpenPdfReaderAsync(path, _readerFeatureCancellation!.Token);
                return;
            }

            _readerHasToc = true;
            ReaderStatusText.Text = "正在准备…";
            var readerSourcePath = path;
            var readerSourceHash = file.Sha256;
            if (file.Format.Equals("azw3", StringComparison.OrdinalIgnoreCase)
                || file.Format.Equals("mobi", StringComparison.OrdinalIgnoreCase))
            {
                ReaderStatusText.Text = $"正在准备 {file.Format.ToUpperInvariant()}…";
                var cachedSource = await _readerFormatCache.PrepareEpubAsync(
                    path,
                    file.Sha256,
                    file.Format,
                    readerToken);
                readerSourcePath = cachedSource.EpubPath;
                readerSourceHash = cachedSource.CacheKey;
            }

            var document = await _epubReader.PrepareAsync(
                readerSourcePath,
                readerSourceHash,
                _readerFeatureCancellation!.Token);
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
            ReaderBookmarkButton.Visibility = Visibility.Visible;
            ReaderSearchToolbarButton.Visibility = Visibility.Visible;
            ReaderBookmarkTabButton.Visibility = Visibility.Visible;
            ApplyReaderPanelLayout();
            await ShowReaderChapterAsync(animate: false);
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
        finally { _readerOpenInProgress = false; }
    }

    private static string CreateTemporaryFormatPath(string extension)
        => CreateTemporaryFormatPath("reader", extension);

    private static string CreateTemporaryFormatPath(string fileStem, string extension)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "KkindleConversions",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var invalid = Path.GetInvalidFileNameChars();
        var safeStem = new string(fileStem
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.');
        if (string.IsNullOrWhiteSpace(safeStem)) safeStem = "book";
        if (safeStem.Length > 120) safeStem = safeStem[..120].TrimEnd();
        return Path.Combine(directory, $"{safeStem}.{extension.TrimStart('.')}");
    }

    private static void TryDeleteTemporaryFormatPath(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            var directory = Path.GetDirectoryName(path);
            if (directory is not null
                && Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch { }
    }

    private void ConfigureReaderWebView()
    {
        var settings = ReaderWebView.CoreWebView2.Settings;
        settings.IsScriptEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
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
        if (_readerTocMinimal)
        {
            _readerTocMinimal = false;
            _readerTocExpanded = true;
        }
        else
        {
            _readerTocExpanded = !_readerTocExpanded;
        }
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
        // Keep the reader background across the title-bar area while the
        // assistant is hosted in a Popup outside the reader grid.
        ReaderPane.Width = width;
        ReaderPane.HorizontalAlignment = HorizontalAlignment.Left;
        var tocWidth = _readerTocExpanded ? 286d : _readerTocMinimal ? ReaderTocMinimalWidth : 0d;
        ReaderTocColumn.Width = new GridLength(tocWidth);
        ReaderContentColumn.Width = new GridLength(Math.Max(0, readerWidth - tocWidth));
        ReaderAssistantColumn.Width = new GridLength(Math.Min(assistantWidth, width));

        ReaderTocPanel.Visibility = _readerTocExpanded ? Visibility.Visible : Visibility.Collapsed;
        ReaderTocCompactPanel.Visibility = _readerTocMinimal ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(ReaderTocPanel, 0);
        Grid.SetColumnSpan(ReaderTocPanel, 1);
        ReaderTocPanel.Width = double.NaN;
        ReaderTocPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        Canvas.SetZIndex(ReaderTocPanel, 0);
        Grid.SetColumn(ReaderTocCompactPanel, 0);
        Grid.SetColumnSpan(ReaderTocCompactPanel, 1);
        ReaderTocCompactPanel.Width = double.NaN;
        ReaderTocCompactPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        Canvas.SetZIndex(ReaderTocCompactPanel, 0);
        if (_readerSearchVisible)
        {
            ReaderSearchPanel.Width = tocWidth;
            ReaderSearchPanel.Visibility = Visibility.Visible;
        }

        UpdateReaderAssistantPopup(_readerAssistantExpanded);
        if (_readerZenMode) UpdateReaderZenPopup(true);

        ReaderTocToggleButton.Opacity = _readerTocExpanded ? 0.58 : 1;
        ReaderAssistantToggleButton.Opacity = _readerAssistantExpanded ? 0.58 : 1;
        // Refresh the cached WebView screen rect used by the low-level mouse
        // hook (the hook thread itself must never touch XAML). Layout changes
        // always re-run this and keep the cache in sync.
        try { GetReaderWebViewScreenRect(); } catch { }
    }

    private void ReaderContentPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ReaderContentClip.Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        ScheduleReaderRelayout();
        try { GetReaderWebViewScreenRect(); } catch { }
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
                    await RealignReaderAfterRelayoutAsync();
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
        if (_readerFlowMode == 1)
        {
            // Pagination: snap to the nearest column boundary (also clamps the
            // maximum scroll range so the reader never rests past the last page).
            await SnapReaderPaginationAsync();
            return;
        }
        var script = "(function(){var el=document.scrollingElement;var max=Math.max(0,el.scrollHeight-el.clientHeight);if(el.scrollTop>max)window.scrollTo({top:max});})()";
        try { await ReaderWebView.CoreWebView2.ExecuteScriptAsync(script); }
        catch { }
    }

    // A TOC fragment is an explicit location, not merely the nearest page.
    // Font loading, image decoding and host-size changes can reflow columns
    // after the first NavigationCompleted positioning pass. Re-resolve that
    // anchor after each delayed relayout; other locations keep normal clamping.
    private async Task RealignReaderAfterRelayoutAsync()
    {
        if (_readerFlowMode == 1
            && _readerNavigationIntent == ReaderNavigationIntent.Toc
            && _readerActiveLocationTarget is { } source
            && ReaderNavigationLocationPolicy.TocTargetHasAnchor(source))
        {
            await ScrollToReaderFragmentAsync(
                ReaderNavigationLocationPolicy.TocAnchorId(source));
            return;
        }

        await ClampReaderScrollAsync();
    }

    private void ReaderTocSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyReaderTocFilter();

    private void ApplyReaderTocFilter()
    {
        if (!_readerHasToc)
        {
            ReaderTocList.ItemsSource = null;
            ClearReaderCompactNavigationItems();
            return;
        }

        var query = ReaderTocSearchBox.Text.Trim();
        var items = string.IsNullOrEmpty(query)
            ? _readerNavigation
            : _readerNavigation.Where(item =>
                item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        ReaderTocList.ItemsSource = items;
        SetReaderCompactNavigationItems(items);
    }

    // ------------------------------------------------------------------
    // Chapter navigation. Every real chapter-switch path funnels through
    // ShowReaderChapterAsync / NavigateReaderSourceAsync. The transition
    // timing is deliberate:
    //   1. The CURRENT chapter stays fully visible while the new document
    //      loads — we never fade/slide the old page away up front, so the
    //      navigation never shows a blank/frozen screen for its whole duration.
    //   2. After NavigationCompleted the essential first-screen work runs
    //      (styling/viewport, cover/image fit, target position restore,
    //      pagination snap, scroll-edge priming) while the new page is held
    //      behind the pane's opaque background.
    //   3. Only then does the selected fade or slide animation reveal the
    //      ready first screen; 无动画 shows it immediately. Slow non-first-screen
    //      work (annotations,
    //      footnote hover, stats/progress) is deferred behind the reveal and
    //      guarded by the navigation sequence so a stale chapter can never
    //      overwrite the current one. The transition never blocks the UI
    //      thread and is cancelled immediately when the reader closes.
    // ------------------------------------------------------------------

    private async Task ShowReaderChapterAsync(
        int direction = 1,
        bool animate = true,
        ReaderNavigationIntent intent = ReaderNavigationIntent.None)
    {
        if (_readerChapterIndex < 0 || _readerChapterIndex >= _readerChapters.Count) return;
        UpdateReaderChapterControls();
        SelectReaderTocItem(_readerNavigation.FirstOrDefault(item => item.ChapterIndex == _readerChapterIndex));
        await NavigateReaderSourceAsync(new Uri(_readerChapters[_readerChapterIndex]), direction, animate, intent);
    }

    private Task NavigateReaderSourceAsync(
        Uri target,
        int direction,
        bool animate,
        ReaderNavigationIntent intent = ReaderNavigationIntent.None)
    {
        if (target is null || ReaderWebView.CoreWebView2 is null)
        {
            if (target is not null) ReaderWebView.Source = target;
            return Task.CompletedTask;
        }

        // An explicit user target must win over any automatic breakpoint
        // restore, and a navigation must never inherit the pending location of
        // the navigation it superseded (rapid TOC clicks, or a TOC click right
        // after a search/bookmark/annotation jump). Only the location payload
        // belonging to THIS intent survives the pruning.
        PruneReaderPendingLocations(intent);
        _readerNavigationIntent = intent;
        _readerActiveLocationTarget = target;

        var locationSequence = ++_readerLocationSequence;
        var sameDocument = ReaderNavigationLocationPolicy.TargetsSameDocument(
            ReaderWebView.Source,
            target);
        if (sameDocument && _readerPendingNavigationTarget is null && !_readerNavigateToEnd)
        {
            // A different #fragment in the current XHTML is an in-page jump in
            // WebView2 and is not guaranteed to raise NavigationCompleted.
            // Run Kreader's location logic directly rather than waiting for an
            // event that may never arrive and leaving the browser-default
            // anchor halfway down the page.
            _readerPendingTurnInAnimation = null;
            // Even without a WebView navigation, an explicit user click must
            // still move the reading position: TOC chapter entries go to the
            // chapter's first line, fragment entries to their anchor, and
            // bookmark/annotation/search/AI locations to their own target.
            if (intent != ReaderNavigationIntent.None)
                _ = RunSameChapterLocationAsync(intent, target, locationSequence);
            return Task.CompletedTask;
        }

        // Animations are decorative: never run them while closing, while the
        // pane is hidden, or when the user selected "无动画". "Jump" navigations
        // (TOC/search/bookmark/annotation/AI/progress slider) always use the
        // selected animation style, so every navigation path has predictable
        // behavior without pretending to drag through intermediate chapters.
        var shouldAnimate = animate
            && _readerPageAnimation > ReaderAnimationNone
            && !_readerCloseRequested
            && ReaderPane.Visibility == Visibility.Visible;
        var turnInStyle = shouldAnimate ? _readerPageAnimation : ReaderAnimationNone;

        _readerChapterTransitionCancellation?.Cancel();
        _readerChapterTransitionCancellation?.Dispose();
        _readerChapterTransitionCancellation = new CancellationTokenSource();
        var token = _readerChapterTransitionCancellation.Token;
        var sequence = ++_readerChapterTransitionSequence;

        // The current page is kept on screen while the document loads; the
        // reader surface is hidden only after the new chapter commits and its
        // first screen is prepared (see ReaderWebView_NavigationCompleted).
        _readerTransitionActive = true;
        _readerPendingTurnInAnimation = shouldAnimate
            ? new ReaderTurnInAnimation(direction, turnInStyle)
            : null;
        _readerPendingNavigationTarget = target;
        try
        {
            ReaderWebView.Source = target;
        }
        catch
        {
            // A stale navigation is fine; make sure the reader surface is never
            // left in a transformed/hidden state.
            _readerPendingTurnInAnimation = null;
            _readerPendingNavigationTarget = null;
            ResetReaderWebViewTransform();
            _readerTransitionActive = false;
            return Task.CompletedTask;
        }
        // Watchdog: NavigationCompleted normally releases the transition guard;
        // if the navigation never reports back (or fails while a still-pending
        // target is waiting), release it after a few seconds so the scroll poll
        // can never be blocked permanently. Armed for every navigation, not just
        // animated ones, so a failed chapter in 无动画 mode is never stuck.
        _ = Task.Delay(3000).ContinueWith(
            _ => _readerTransitionActive = false,
            TaskScheduler.Default);
        return Task.CompletedTask;
    }

    private void ReaderTocList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingReaderToc || ReaderTocList.SelectedItem is not EpubReaderNavigationItem item) return;
        NavigateToReaderTocItem(item);
    }

    private void SelectReaderTocItem(EpubReaderNavigationItem? item)
    {
        _isUpdatingReaderToc = true;
        ReaderTocList.SelectedItem = item;
        if (item is not null) ReaderTocList.ScrollIntoView(item);
        _isUpdatingReaderToc = false;
        SetReaderCompactSelectedItem(item);
    }

    private void UpdateReaderChapterControls()
    {
        ReaderChapterText.Text = _readerChapterIndex < 0
            ? string.Empty
            : $"{_readerChapterIndex + 1} / {_readerChapters.Count} 章";
        ReaderPreviousButton.IsEnabled = _readerChapterIndex > 0;
        ReaderNextButton.IsEnabled = _readerChapterIndex + 1 < _readerChapters.Count;
        RefreshReaderCompactMarkers();
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
        UpdateReaderStatsDisplay();
    }

    private void ReaderProgressSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (IsPdfReader)
        {
            if (_isUpdatingReaderProgress) return;
            _ = NavigatePdfPageAsync((int)Math.Round(e.NewValue));
            return;
        }
        if (_isUpdatingReaderProgress || !_readerHasToc || _readerChapters.Count == 0) return;
        var chapterIndex = Math.Clamp((int)Math.Round(e.NewValue) - 1, 0, _readerChapters.Count - 1);
        if (chapterIndex == _readerChapterIndex) return;
        var previousIndex = _readerChapterIndex;
        _readerContinuousLocked = false;
        _readerChapterIndex = chapterIndex;
        _readerNavigateToEnd = false;
        _ = ShowReaderChapterAsync(previousIndex < chapterIndex ? 1 : -1, intent: ReaderNavigationIntent.Progress);
    }

    private async void ReaderPreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPdfReader) { await NavigatePdfPageAsync(_pdfCurrentPage - 1); return; }
        _readerContinuousLocked = false;
        await TurnReaderPageAsync(-1);
    }

    private async void ReaderNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPdfReader) { await NavigatePdfPageAsync(_pdfCurrentPage + 1); return; }
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

    private async void ReaderFlowModeItem_Click(object sender, RoutedEventArgs e)
    {
        var mode = (sender as RadioMenuFlyoutItem)?.Tag?.ToString() ?? "single";
        var flowMode = string.Equals(mode, "scroll", StringComparison.Ordinal) ? 0 : 1;
        var twoPageMode = string.Equals(mode, "double", StringComparison.Ordinal);
        if (_readerFlowMode == flowMode && _readerLayout.TwoPageMode == twoPageMode)
        {
            ReaderFlowButton.Flyout?.Hide();
            return;
        }

        _readerFlowMode = flowMode;
        _readerWheelDeltaRemainder = 0;
        _readerLayout = _readerLayout with
        {
            FlowMode = flowMode,
            TwoPageMode = twoPageMode
        };
        _readerNavigateToEnd = false;
        _readerContinuousLocked = false;
        UpdateReaderFlowButton();
        await ApplyReaderAppearanceAsync();
        await ResetReaderToChapterStartAsync();
        await PrimeReaderScrollEdgesAsync();
        _ = SaveReaderLayoutSettingsAsync();
        UpdateReaderLayoutStatus();
        ReaderFlowButton.Flyout?.Hide();
    }

    private void UpdateReaderFlowButton()
    {
        ReaderFlowButton.Content = _readerFlowMode == 0
            ? "滚动"
            : _readerLayout.TwoPageMode ? "双栏" : "单页";
        if (ReaderScrollModeItem is not null)
            ReaderScrollModeItem.IsChecked = _readerFlowMode == 0;
        if (ReaderSinglePageModeItem is not null)
            ReaderSinglePageModeItem.IsChecked = _readerFlowMode == 1 && !_readerLayout.TwoPageMode;
        if (ReaderTwoPageModeItem is not null)
            ReaderTwoPageModeItem.IsChecked = _readerFlowMode == 1 && _readerLayout.TwoPageMode;
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
        ReaderZenTitleExitButton.Visibility = _readerZenMode
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateReaderZenTocToggle();
    }

    private void ReaderZenMinimalTocButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_readerZenMode) return;
        _readerTocExpanded = false;
        _readerTocMinimal = !_readerTocMinimal;
        ApplyReaderPanelLayout();
        UpdateReaderZenTocToggle();
    }

    private void UpdateReaderZenTocToggle()
    {
        if (ReaderZenTitleTocButton is null || ReaderZenTocButton is null) return;
        var label = _readerTocMinimal ? "隐藏极简目录" : "显示极简目录";
        ReaderZenTitleTocButton.Content = label;
        ReaderZenTocButton.Content = label;
        var visibility = _readerZenMode ? Visibility.Visible : Visibility.Collapsed;
        ReaderZenTitleTocButton.Visibility = visibility;
        ReaderZenTocButton.Visibility = visibility;
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
            _readerPreZenTocMinimal = _readerTocMinimal;
            _readerPreZenAssistantExpanded = _readerAssistantExpanded;
            _readerTocExpanded = false;
            _readerTocMinimal = true;
            _readerAssistantExpanded = false;
            ReaderHeaderRow.Height = new GridLength(0);
            ReaderHeaderBar.Visibility = Visibility.Collapsed;
            ReaderFooterRow.Height = new GridLength(0);
            ReaderFooterBar.Visibility = Visibility.Collapsed;
            ReaderTocToggleButton.Opacity = 1;
            ReaderAssistantToggleButton.Opacity = 1;
            UpdateReaderZenPopup(true);
            UpdateReaderZenTocToggle();
        }
        else
        {
            ReaderHeaderRow.Height = new GridLength(52);
            ReaderHeaderBar.Visibility = Visibility.Visible;
            ReaderFooterRow.Height = new GridLength(50);
            ReaderFooterBar.Visibility = Visibility.Visible;
            _readerTocExpanded = _readerPreZenTocExpanded;
            _readerTocMinimal = _readerPreZenTocMinimal;
            _readerAssistantExpanded = _readerPreZenAssistantExpanded;
            ReaderTocToggleButton.Opacity = _readerTocExpanded ? 0.58 : 1;
            ReaderAssistantToggleButton.Opacity = _readerAssistantExpanded ? 0.58 : 1;
            UpdateReaderZenPopup(false);
            UpdateReaderZenTocToggle();
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
        ReaderZenTitleExitButton.Visibility = Visibility.Collapsed;
        ReaderZenTitleTocButton.Visibility = Visibility.Collapsed;
        ReaderZenTocButton.Visibility = Visibility.Collapsed;
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
            _readerPageAnimation = ReaderAnimationFade;
        else if (ReferenceEquals(sender, ReaderAnimationSlideItem))
            _readerPageAnimation = ReaderAnimationSlide;
        else
            _readerPageAnimation = ReaderAnimationNone;
    }

    private void SyncReaderPageAnimationMenu()
    {
        ReaderAnimationNoneItem.IsChecked = _readerPageAnimation == ReaderAnimationNone;
        ReaderAnimationFadeItem.IsChecked = _readerPageAnimation == ReaderAnimationFade;
        ReaderAnimationSlideItem.IsChecked = _readerPageAnimation == ReaderAnimationSlide;
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
        if (_readerCloseRequested || _readerTransitionActive) return false;

        // Turn within the current chapter when content remains (pagination
        // columns or scroll direction). Crossing a chapter funnels through
        // ShowReaderChapterAsync so the selected transition plays there too.
        if (await TryTurnWithinChapterAsync(direction)) return true;

        var targetIndex = _readerChapterIndex + direction;
        if (targetIndex < 0 || targetIndex >= _readerChapters.Count)
        {
            // At the very first/last chapter: never leave the surface in an
            // animated state.
            ResetReaderWebViewTransform();
            return false;
        }

        _readerChapterIndex = targetIndex;
        _readerNavigateToEnd = direction < 0;
        _readerContinuousLocked = false;
        _readerLastChapterChange = DateTimeOffset.UtcNow;
        UpdateReaderChapterControls();
        _ = SaveReaderProgressThrottledAsync();
        await ShowReaderChapterAsync(direction, animate: _readerPageAnimation > ReaderAnimationNone);
        return true;
    }

    private async Task AnimateReaderPageTurnAsync(
        int direction,
        bool isOut,
        int style,
        CancellationToken cancellationToken = default)
    {
        if (style == ReaderAnimationNone)
        {
            ResetReaderWebViewTransform();
            return;
        }
        if (_readerCloseRequested)
        {
            ResetReaderWebViewTransform();
            throw new OperationCanceledException();
        }
        cancellationToken.ThrowIfCancellationRequested();

        var width = Math.Max(1, ReaderWebViewHost.ActualWidth);
        var storyboard = new Storyboard();
        var duration = new Duration(TimeSpan.FromMilliseconds(isOut ? 130 : 180));
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

        if (style == ReaderAnimationFade)
        {
            // Fade: gentle opacity transition combined with a slight scale. The incoming
            // page starts hidden and fades up to full opacity. For an in-place
            // page turn, the old page fades out completely before the scroll
            // position changes, so no two pages are blended together.
            ReaderWebViewHost.Opacity = isOut ? 1 : 0;
            ReaderWebViewTransform.ScaleX = isOut ? 1 : 0.985;
            ReaderWebViewTransform.ScaleY = isOut ? 1 : 0.985;
            var opacity = new DoubleAnimation
            {
                From = isOut ? 1 : 0,
                To = isOut ? 0 : 1,
                Duration = duration,
                EnableDependentAnimation = true,
                EasingFunction = easing
            };
            Storyboard.SetTarget(opacity, ReaderWebViewHost);
            Storyboard.SetTargetProperty(opacity, "Opacity");
            storyboard.Children.Add(opacity);

            var scaleX = new DoubleAnimation
            {
                From = isOut ? 1 : 0.985,
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
                From = isOut ? 1 : 0.985,
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
            // direction of travel; it starts parked off-screen so the pane's
            // opaque background shows while the first screen is prepared.
            var from = isOut ? 0d : (direction > 0 ? width : -width);
            var to = isOut ? (direction > 0 ? -width : width) : 0d;
            // Cross-chapter preparation parks the host with Opacity=0. Reveal
            // it before moving it in; otherwise the slide runs invisibly.
            ReaderWebViewHost.Opacity = 1;
            ReaderWebViewTransform.TranslateX = from;
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

        try
        {
            storyboard.Begin();
            await Task.Delay(isOut ? 130 : 180, cancellationToken);
            if (_readerCloseRequested) throw new OperationCanceledException();

            // A completed storyboard still owns its animated properties. Stop
            // it before writing the final base values, otherwise the next turn
            // can inherit a stale opacity, scale, or translation.
            StopReaderStoryboard(storyboard);
            if (style == ReaderAnimationFade)
            {
                ReaderWebViewHost.Opacity = isOut ? 0 : 1;
                ReaderWebViewTransform.ScaleX = isOut ? 0.985 : 1;
                ReaderWebViewTransform.ScaleY = isOut ? 0.985 : 1;
                ReaderWebViewTransform.TranslateX = 0;
            }
            else
            {
                ReaderWebViewHost.Opacity = 1;
                ReaderWebViewTransform.TranslateX = isOut
                    ? (direction > 0 ? -width : width)
                    : 0;
                ReaderWebViewTransform.ScaleX = 1;
                ReaderWebViewTransform.ScaleY = 1;
            }
        }
        catch (OperationCanceledException)
        {
            // A newer transition or a reader close superseded this one; stop the
            // storyboard and restore the identity transform so the content is
            // never left faded/slid off-screen.
            StopReaderStoryboard(storyboard);
            ResetReaderWebViewTransform();
            throw;
        }
        catch
        {
            StopReaderStoryboard(storyboard);
            ResetReaderWebViewTransform();
            throw;
        }
    }

    private static void StopReaderStoryboard(Storyboard storyboard)
    {
        try { storyboard.Stop(); } catch { }
    }

    private void ResetReaderWebViewTransform()
    {
        ReaderWebViewHost.Opacity = 1;
        ReaderWebViewTransform.TranslateX = 0;
        ReaderWebViewTransform.ScaleX = 1;
        ReaderWebViewTransform.ScaleY = 1;
    }

    // ------------------------------------------------------------------
    // Keyboard reading navigation. Paginated single/double-page modes use
    // left/right; continuous scroll mode uses up/down. Keeping the modes
    // disjoint avoids an arrow key unexpectedly changing the reading position.
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
                _readerLayoutPopupOpen = false;
            }
            else if (_readerZenMode)
            {
                e.Handled = true;
                ToggleReaderZenMode();
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

        if (IsReaderTextInputFocused()) return;

        var direction = _readerFlowMode switch
        {
            1 when e.Key == Windows.System.VirtualKey.Left => -1,
            1 when e.Key == Windows.System.VirtualKey.Right => 1,
            0 when e.Key == Windows.System.VirtualKey.Up => -1,
            0 when e.Key == Windows.System.VirtualKey.Down => 1,
            _ => 0
        };
        if (direction == 0) return;
        e.Handled = true;
        _ = _readerFlowMode == 0
            ? ScrollReaderWithKeyboardAsync(direction)
            : TurnReaderPageAsync(direction);
    }

    private bool IsReaderTextInputFocused()
    {
        if (Content is not FrameworkElement root || root.XamlRoot is null) return false;
        var focused = FocusManager.GetFocusedElement(root.XamlRoot);
        return focused is TextBox or PasswordBox or RichEditBox or AutoSuggestBox
            || focused is ComboBox { IsEditable: true };
    }

    private async Task ScrollReaderWithKeyboardAsync(int direction)
    {
        if (_readerCloseRequested || _readerTransitionActive) return;
        if (ReaderWebView.CoreWebView2 is null || _readerAllowedRoot is null) return;

        // Arrow keys in continuous mode should feel like scrolling, not like a
        // nearly full-screen page jump. If the chapter edge has been reached,
        // fall back to the shared turn path so continuous reading can still
        // cross into the adjacent chapter.
        if (await ExecuteReaderBooleanScriptAsync(
                CreateReaderKeyboardScrollScript(direction, _readerLayout.VerticalWriting)))
        {
            return;
        }

        await TurnReaderPageAsync(direction);
    }

    private static string CreateReaderKeyboardScrollScript(int direction, bool vertical) =>
        $$"""
        (() => {
          const el = document.scrollingElement || document.documentElement;
          if (!el) return false;
          const horizontal = {{(vertical ? "true" : "false")}};
          const position = horizontal ? el.scrollLeft : el.scrollTop;
          const viewport = horizontal ? el.clientWidth : el.clientHeight;
          const extent = horizontal ? el.scrollWidth : el.scrollHeight;
          const sign = {{(direction < 0 ? -1 : 1)}};
          if (sign < 0 && position <= 4) return false;
          if (sign > 0 && position + viewport >= extent - 4) return false;
          const delta = sign * 72;
          window.scrollBy(horizontal
            ? { left: delta, top: 0, behavior: 'smooth' }
            : { left: 0, top: delta, behavior: 'smooth' });
          return true;
        })();
        """;

    private async Task<bool> TryTurnWithinChapterAsync(int direction)
    {
        if (_readerAllowedRoot is null || ReaderWebView.CoreWebView2 is null) return false;
        var vertical = _readerLayout.VerticalWriting;
        var pagination = _readerFlowMode == 1;
        var canTurnScript = pagination
            ? ReaderPaginationScripts.CreateCanTurnScript(direction)
            : CreateReaderScrollCanTurnScript(direction, vertical);
        if (!await ExecuteReaderBooleanScriptAsync(canTurnScript)) return false;

        var style = pagination ? _readerPageAnimation : ReaderAnimationNone;
        var turnScript = pagination
            ? ReaderPaginationScripts.CreateTurnScript(
                direction,
                smooth: false)
            : CreateReaderScrollTurnScript(
                direction,
                vertical,
                smooth: _readerPageAnimation != ReaderAnimationNone);
        if (style == ReaderAnimationNone)
            return await ExecuteReaderBooleanScriptAsync(turnScript);

        _readerTransitionActive = true;
        try
        {
            var token = _readerChapterTransitionCancellation?.Token ?? CancellationToken.None;
            ResetReaderWebViewTransform();
            await AnimateReaderPageTurnAsync(direction, isOut: true, style, token);
            if (!await ExecuteReaderBooleanScriptAsync(turnScript)) return false;
            await AnimateReaderPageTurnAsync(direction, isOut: false, style, token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            ResetReaderWebViewTransform();
            _readerTransitionActive = false;
        }
    }

    private static string CreateReaderScrollCanTurnScript(int direction, bool vertical) =>
        $$"""
        (() => {
          const el = document.scrollingElement || document.documentElement;
          if (!el) return false;
          const horizontal = {{(vertical ? "true" : "false")}};
          const position = horizontal ? el.scrollLeft : el.scrollTop;
          const viewport = horizontal ? el.clientWidth : el.clientHeight;
          const extent = horizontal ? el.scrollWidth : el.scrollHeight;
          return {{(direction < 0 ? -1 : 1)}} < 0
            ? position > 4
            : position + viewport < extent - 4;
        })();
        """;

    private static string CreateReaderScrollTurnScript(int direction, bool vertical, bool smooth) =>
        $$"""
        (() => {
          const el = document.scrollingElement || document.documentElement;
          if (!el) return false;
          const horizontal = {{(vertical ? "true" : "false")}};
          const viewport = horizontal ? window.innerWidth : window.innerHeight;
          const step = Math.max(200, viewport * 0.86);
          const delta = {{(direction < 0 ? -1 : 1)}} < 0 ? -step : step;
          const position = horizontal ? el.scrollLeft : el.scrollTop;
          const extent = horizontal ? el.scrollWidth : el.scrollHeight;
          const currentViewport = horizontal ? el.clientWidth : el.clientHeight;
          if (delta < 0 && position <= 4) return false;
          if (delta > 0 && position + currentViewport >= extent - 4) return false;
          window.scrollBy(horizontal
            ? { left: delta, top: 0, behavior: '{{(smooth ? "smooth" : "instant")}}' }
            : { left: 0, top: delta, behavior: '{{(smooth ? "smooth" : "instant")}}' });
          return true;
        })();
        """;

    private async Task<bool> ExecuteReaderBooleanScriptAsync(string script)
    {
        if (ReaderWebView.CoreWebView2 is null) return false;
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
        // Idempotent: repeated X / 返回书架 / window-close calls must be no-ops
        // while a close is already in progress (and after it completes).
        if (_readerCloseInProgress) return;
        _readerCloseInProgress = true;
        _readerCloseRequested = true;

        // 1) Tear down every background reader mechanism first, before any UI
        //    work or persistence. None of these block the UI thread.
        UninstallReaderMouseHook();
        StopReaderScrollPoll();
        StopReaderToolsTimers();
        _readerChapterTransitionCancellation?.Cancel();
        _readerChapterTransitionCancellation?.Dispose();
        _readerChapterTransitionCancellation = null;
        _readerTransitionActive = false;
        _readerLocationSequence++;
        _readerPendingTurnInAnimation = null;
        _readerPendingNavigationTarget = null;
        _readerActiveLocationTarget = null;
        _readerNavigationIntent = ReaderNavigationIntent.None;
        _readerRelayoutCancellation?.Cancel();
        _readerRelayoutCancellation?.Dispose();
        _readerRelayoutCancellation = null;
        ResetReaderWebViewTransform();

        // 2) Close every reader Popup. WebView2 renders as an HWND composition
        //    island, so popups are the only surfaces that can float above it and
        //    must be closed explicitly.
        UpdateReaderAssistantPopup(false);
        SetReaderAiSettingsVisible(false);
        if (_readerLayoutPopup is not null) _readerLayoutPopup.IsOpen = false;
        _readerLayoutPopupOpen = false;
        if (_readerSelectionPopup is not null) _readerSelectionPopup.IsOpen = false;
        if (_readerZenPopup is not null) _readerZenPopup.IsOpen = false;
        _readerSearchVisible = false;

        // 3) Hide the reader and return to the library immediately.
        ReaderPane.Visibility = Visibility.Collapsed;
        ReaderBrandText.Visibility = Visibility.Collapsed;

        // 4) Persist progress/statistics without blocking: the flush uses the
        //    last captured progress snapshot and a short timeout, so a failing
        //    or hanging save can never prevent the reader from closing. It must
        //    start before EndReaderSession nulls the session fields.
        _ = FlushReaderSessionSafelyAsync(skipWebViewCapture: true);
        EndReaderSession();

        // 5) Reset session state and unload the reader WebView. Navigating to
        //    about:blank is fire-and-forget; NavigationCompleted early-returns
        //    once _readerCloseRequested is set.
        _readerChapters = [];
        _readerNavigation = [];
        _readerChapterIndex = -1;
        _readerAllowedRoot = null;
        _readerAllowedFile = null;
        _readerNavigateToEnd = false;
        _readerHasToc = false;
        _readerTocMinimal = false;
        _readerZenMode = false;
        _readerContinuousLocked = false;
        ResetReaderChromeLayout();
        ReaderTocList.ItemsSource = null;
        ClearReaderCompactNavigationItems();
        ReaderTocSearchBox.Text = string.Empty;
        ReaderBookInfoText.Text = string.Empty;
        ResetReaderAssistant();
        if (ReaderWebView.CoreWebView2 is not null)
        {
            try { ReaderWebView.CoreWebView2.Navigate("about:blank"); }
            catch { }
        }
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

        ClearReaderFootnotePage();
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
        if (_readerCloseRequested)
        {
            // The reader is closing: never run post-navigation work and never
            // let a stale animation touch the (now hidden) reader surface.
            _readerTransitionActive = false;
            return;
        }
        try
        {
            // Only the most recently requested navigation may complete the flow.
            // A stale NavigationCompleted for a superseded chapter must not run
            // first-screen preparation or consume the pending turn-in, otherwise
            // rapid TOC clicks could let an old chapter's tasks overwrite the
            // newest one.
            var pendingTarget = _readerPendingNavigationTarget;
            if (pendingTarget is null
                || ReaderWebView.Source is not { } source
                || !source.AbsoluteUri.Equals(pendingTarget.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!args.IsSuccess)
            {
                // A superseded (canceled) navigation reports failure while a
                // newer chapter is already requested and still loading; clearing
                // the guard/turn-in here would let the newer chapter reveal
                // without its transition and could also release the guard while
                // the newer document is mid-flight. So a failed/canceled
                // completion never tears down state for a still-pending newer
                // navigation: the successful NavigationCompleted of the newest
                // chapter (or the 3s watchdog for a genuinely-failed chapter)
                // is what releases the transition guard. The host is only ever
                // parked for the reveal, so a failure leaves it at the identity
                // transform automatically.
                return;
            }

            var token = _readerChapterTransitionCancellation?.Token ?? CancellationToken.None;
            var sequence = _readerChapterTransitionSequence;
            var turnIn = _readerPendingTurnInAnimation;
            _readerPendingTurnInAnimation = null;

            // Essential first-screen work: style/layout/viewport, cover/image
            // fit, target position restore and pagination snap must all be done
            // before the new chapter is revealed, so the user never sees a raw
            // unstyled or wrongly-positioned page. While this runs the host is
            // hidden behind the pane's opaque background (never a black flash).
            // Slow, non-first-screen work (annotations, footnotes, stats,
            // progress) is deferred to RunReaderPostNavigationWorkAsync.
            if (turnIn is not null) ReaderWebViewHost.Opacity = 0;
            await ApplyReaderAppearanceAsync();
            if (IsStaleReaderNavigation(sequence, token)) return;
            // The first screen is positioned according to WHY this navigation
            // was requested. An explicit user target (TOC chapter first line,
            // fragment anchor, search/bookmark/annotation/AI location, progress
            // slider) always wins; automatic breakpoint restore only runs for
            // the open-book flow (intent None). Stale pending locations from a
            // superseded navigation were already pruned when this navigation
            // started, so a TOC jump can never inherit the old chapter's offset.
            var intent = _readerNavigationIntent;
            await ApplyReaderNavigationLocationAsync(intent, pendingTarget);
            if (IsStaleReaderNavigation(sequence, token)) return;
            if (_readerNavigateToEnd)
            {
                await MoveReaderToEndAsync();
                // Only clear the intent if this navigation is still current; a
                // newer navigation may have re-armed it for its own chapter.
                if (!IsStaleReaderNavigation(sequence, token))
                    _readerNavigateToEnd = false;
            }
            // Edge state is aligned only after the new chapter is styled and
            // positioned; release the transition guard right after so the poll
            // can never misfire on a not-yet-primed page.
            await PrimeReaderScrollEdgesAsync();
            if (IsStaleReaderNavigation(sequence, token)) return;

            // The new first screen is ready: short fade/slide reveal (or show
            // immediately in 无动画 mode), then let the deferred work run.
            if (turnIn is { } pending)
            {
                await AnimateReaderPageTurnAsync(pending.Direction, isOut: false, pending.Style, token);
            }
            else
            {
                ResetReaderWebViewTransform();
            }
            _readerTransitionActive = false;
            _readerPendingNavigationTarget = null;

            _ = RunReaderPostNavigationWorkAsync(sequence, token);
        }
        catch
        {
            // Post-navigation styling is best-effort; a failure here must never
            // leave the reader or the window in a broken state.
            _readerPendingTurnInAnimation = null;
            _readerPendingNavigationTarget = null;
            ResetReaderWebViewTransform();
            _readerTransitionActive = false;
        }
    }

    // True when a newer navigation started, the transition was cancelled (new
    // navigation or reader close), or the reader is closing. Deferred work
    // checks this before and after every await so a stale chapter's tasks can
    // never apply to (or override) the current chapter.
    private bool IsStaleReaderNavigation(int sequence, CancellationToken token) =>
        _readerCloseRequested
        || token.IsCancellationRequested
        || sequence != _readerChapterTransitionSequence;

    // Non-first-screen work runs after the first screen is revealed. It is
    // fire-and-forget with a bounded lifecycle: every await is followed by the
    // navigation-sequence guard, so a chapter that got superseded or the reader
    // closing can stop it immediately. Failures are isolated (never throw into
    // the UI event loop).
    private async Task RunReaderPostNavigationWorkAsync(int sequence, CancellationToken token)
    {
        try
        {
            if (IsStaleReaderNavigation(sequence, token)) return;
            _ = RetryReaderImageFitAsync(sequence);
            if (IsStaleReaderNavigation(sequence, token)) return;
            await ApplyReaderAnnotationsToPageAsync();
            if (IsStaleReaderNavigation(sequence, token)) return;
            if (_pendingReaderAnnotationScroll is not null)
                await ScrollToPendingReaderAnnotationAsync();
            if (IsStaleReaderNavigation(sequence, token)) return;
            await ConfigureReaderFootnoteHoverAsync();
            if (IsStaleReaderNavigation(sequence, token)) return;
            await RefreshReaderProgressAsync();
            _ = SaveReaderProgressThrottledAsync();
            if (IsStaleReaderNavigation(sequence, token)) return;
            if (_readerFlowMode == 0 && _readerContinuousLocked)
                await SkipShortChapterIfNeededAsync();
        }
        catch
        {
            // Deferred reader work is best-effort; never let it crash the loop.
        }
    }

    private string BuildReaderFontFaceCss()
    {
        var fontPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Fonts",
            ReaderFontDefaults.BundledFontFileName);
        var css = new System.Text.StringBuilder();
        if (File.Exists(fontPath))
        {
            var fontUri = new Uri(Path.GetFullPath(fontPath)).AbsoluteUri;
            css.Append($"@font-face{{font-family:\"{ReaderFontDefaults.BundledFamily}\";src:url(\"{fontUri}\") format(\"truetype\");font-style:normal;font-weight:400;font-display:swap;}}");
        }
        foreach (var font in _managedFonts)
        {
            try
            {
                var path = _fontLibrary.GetAbsolutePath(font);
                if (!File.Exists(path)) continue;
                var uri = new Uri(path).AbsoluteUri;
                css.Append($"@font-face{{font-family:\"{font.CssFamily}\";src:url(\"{uri}\");font-style:normal;font-weight:400;font-display:swap;}}");
            }
            catch { }
        }
        return css.ToString();
    }

    private async Task ApplyReaderAppearanceAsync()
    {
        if (_readerAllowedRoot is null || ReaderWebView.CoreWebView2 is null) return;
        const string background = "#FFFFFF";
        const string foreground = "#111111";
        const string link = "#222222";
        var fontPercent = (int)Math.Round(_readerLayout.FontScale * 100);
        // Vertical writing is supported in both continuous and paginated flow.
        // The pagination CSS keeps the viewport horizontal while Chromium lays
        // out vertical-rl columns from right to left.
        var vertical = _readerLayout.VerticalWriting;
        var flowCss = ReaderPaginationScripts.CreateFlowCss(
            pagination: _readerFlowMode == 1,
            vertical: vertical,
            twoPage: _readerLayout.TwoPageMode,
            horizontalPadding: _readerLayout.BodyPadding);
        var lineHeight = _readerLayout.LineHeight.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var bodyPadding = (int)_readerLayout.BodyPadding;
        var bodyLayoutCss = vertical
            ? $"max-width: none !important; writing-mode: vertical-rl !important; text-orientation: mixed;"
              + $" margin: 0 auto !important; padding: {bodyPadding}px !important;"
            : $"max-width: {(int)_readerLayout.MaxWidth}px; margin: 0 auto !important;"
              + $" padding: {bodyPadding}px !important;"
              + " writing-mode: horizontal-tb !important;";
        var bodyTextCss = vertical
            ? "overflow-wrap: anywhere; box-sizing: border-box; line-break: strict; word-break: normal;"
            : "overflow-wrap: anywhere; box-sizing: border-box; line-break: strict; word-break: normal; text-align: justify;";
        var fontFamily = BuildReaderFontStack(_readerLayout.FontFamily);
        var bundledFontFaceCss = BuildReaderFontFaceCss();
        // EPUB image sizing is driven by the real WebView viewport/content box,
        // never the whole window. Inside this page 100vw/100vh are exactly the
        // WebView's own viewport, and --kkindle-page-content-h is the measured
        // body content-box height (viewport minus the body's top/bottom padding).
        //
        //   - Pagination: every image is treated as a "contain" box. `width` and
        //     `height` are forced to `auto` so no EPUB width/height/style can
        //     stretch the image; `max-width: 100%` caps the column width and
        //     `max-height` (content height minus 3.6em for the image's own 1.8em
        //     top/bottom margins) caps the current page height, so the whole
        //     figure never spills past the bottom of a page. The first large
        //     image in a chapter gets `.kkindle-cover` (tighter max-height and
        //     smaller margins) so a cover and its title can share one page.
        //   - Scroll: only the width is capped at the content width; the natural
        //     height is kept so figures stay readable and vertical scrolling
        //     never needs horizontal overflow.
        var imageCss = _readerFlowMode == 1
            ? "img { display: block; width: auto !important; height: auto !important;"
              + " max-width: 100% !important;"
              + " max-height: calc(var(--kkindle-page-content-h, 100vh) - 3.6em) !important;"
              + " object-fit: contain; margin: 1.8em auto !important; }"
              + " svg { display: block; width: auto !important; height: auto !important; max-width: 100% !important;"
              + " max-height: calc(var(--kkindle-page-content-h, 100vh) - 3.6em) !important; margin: 1.8em auto !important; }"
              + " svg image { max-width: 100% !important; }"
              + " img.kkindle-cover, .kkindle-cover img, svg.kkindle-cover, .kkindle-cover svg {"
              + " max-height: calc(var(--kkindle-page-content-h, 100vh) - 6em) !important; margin: 1em auto !important; }"
            : "img { display: block; height: auto !important; max-width: 100% !important; margin: 1.8em auto !important; }"
              + " svg { display: block; height: auto !important; max-width: 100% !important; margin: 1.8em auto !important; }"
              + " svg image { max-width: 100% !important; }";
        ReaderWebView.DefaultBackgroundColor = Colors.White;
        var script = $$"""
            (() => {
              const root = document.documentElement;
              let style = document.getElementById('kkindle-reader-style');
              if (!style) {
                if (!document.head) return;
                style = document.createElement('style');
                style.id = 'kkindle-reader-style';
                document.head.appendChild(style);
              }
              style.textContent = `
                {{bundledFontFaceCss}}
                html { font-size: {{fontPercent}}% !important; text-rendering: optimizeLegibility; }
                html, body { background: {{background}} !important; color: {{foreground}} !important;
                             border: 0 !important; outline: 0 !important; box-shadow: none !important; }
                body { {{bodyLayoutCss}}
                       font-family: {{fontFamily}} !important;
                       font-size: 1rem !important; line-height: {{lineHeight}} !important; letter-spacing: 0.012em;
                       {{bodyTextCss}} }
                body { margin-left: auto !important; margin-right: auto !important;
                       padding: {{bodyPadding}}px !important; }
                ruby { ruby-align: center !important; }
                rt { font-size: 0.5em !important; color: inherit !important; }
                p { margin: 0.55em 0 1.05em !important; }
                li, blockquote { font-size: 1rem !important; line-height: 1.78 !important; }
                h1, h2, h3, h4 { color: {{foreground}} !important; line-height: 1.35 !important; margin: 1.35em 0 0.72em !important; }
                blockquote { margin: 1.4em 0 !important; padding: 0.2em 1.1em !important; border-left: 3px solid {{link}} !important; opacity: 0.88; }
                {{flowCss}}
                {{imageCss}}
                {{ReaderAppearanceScripts.MonochromeScrollbarCss}}
                a { color: {{link}} !important; }
                pre, table { max-width: 100%; overflow-x: auto; }
                hr { border: 0 !important; border-top: 1px solid {{link}} !important; opacity: 0.24; margin: 2em 0 !important; }
                /* Fragment-anchor navigation: the temporary break forces the
                   target subchapter to start at the top of a new column in
                   pagination mode. The marker is removed by plain
                   chapter-start navigation.
                   Never add page-break-before here: it overrides break-before
                   and prevents a screen multicolumn break. */
                .kkindle-fragment-break { break-before: column !important; }
                .kkindle-fragment-zeroed { margin-top: 0 !important; }
              `;
              // Apply the flow rules before measuring the viewport. A newly
              // loaded XHTML document can still have a vertical scrollbar at
              // this point; measuring root.clientWidth before html overflow is
              // hidden would make the pagination columns narrower than the
              // actual reading surface and expose a clipped right edge.
              const kkScroller = document.scrollingElement || root;
              const renderedWidth = kkScroller?.getBoundingClientRect?.().width || 0;
              const viewportWidth = renderedWidth || kkScroller?.clientWidth || root?.clientWidth || window.innerWidth || 0;
              if (root && viewportWidth > 0) {
                root.style.setProperty('{{ReaderPaginationScripts.ViewportWidthVariable}}', viewportWidth + 'px');
              }
              // Expose the real body content box (WebView viewport minus the
              // body's top/bottom padding) so image max-heights target the
              // actual page box instead of guessing from the window size.
              const kkRoot = document.documentElement;
              const kkBody = document.body;
              if (kkRoot && kkBody) {
                const kkStyle = getComputedStyle(kkBody);
                const kkPadTop = parseFloat(kkStyle.paddingTop) || 0;
                const kkPadBottom = parseFloat(kkStyle.paddingBottom) || 0;
                const kkContentH = kkBody.clientHeight - kkPadTop - kkPadBottom;
                if (kkContentH > 0) kkRoot.style.setProperty('--kkindle-page-content-h', kkContentH + 'px');
              }
            })();
            """;
        try { await ReaderWebView.CoreWebView2.ExecuteScriptAsync(script); }
        catch { /* Some fixed-layout EPUB pages don't expose a normal document head. */ }
        if (_readerFlowMode == 1)
        {
            // Pagination mode: mark the first large image in the chapter as the
            // cover so it gets a tighter page fit (size-based detection, never
            // file/book names), then snap the reading area onto the nearest
            // column boundary (top pinned to 0) so each viewport shows exactly
            // one full page instead of a vertically offset strip or two partial
            // columns split by the column gap.
            await FitReaderImagesAsync();
            await SnapReaderPaginationAsync();
        }
    }

    // Marks the first large image in a chapter as `.kkindle-cover`. The
    // decision is based purely on the image's own dimensions (natural size, or
    // width/height attributes before the raster has decoded) compared with the
    // WebView viewport; never on book titles, file names or element classes, so
    // it works for raster covers, SVG covers and image-only chapters.
    private static string GetReaderCoverFitScript() =>
        """
        (() => {
          const view = {
            w: document.documentElement.clientWidth || window.innerWidth || 0,
            h: document.documentElement.clientHeight || window.innerHeight || 0
          };
          if (view.w <= 0 || view.h <= 0) return;
          const candidates = Array.from(document.querySelectorAll('body img, body svg, body svg image'));
          for (const el of candidates) {
            const isImage = el.tagName.toLowerCase() === 'image';
            const naturalW = el.naturalWidth || parseFloat(el.getAttribute('width')) || 0;
            const naturalH = el.naturalHeight || parseFloat(el.getAttribute('height')) || 0;
            if (naturalW <= 0 || naturalH <= 0) continue;
            const pageArea = view.w * view.h;
            if (naturalW * naturalH < pageArea * 0.35
                && !(naturalW >= view.w * 0.6 && naturalH >= view.h * 0.6)) continue;
            el.classList.add('kkindle-cover');
            if (isImage && el.parentElement && /^svg$/i.test(el.parentElement.tagName)) {
              el.parentElement.classList.add('kkindle-cover');
            }
            break;
          }
        })();
        """;

    private async Task FitReaderImagesAsync()
    {
        if (_readerAllowedRoot is null || ReaderWebView.CoreWebView2 is null) return;
        if (_readerFlowMode != 1) return;
        try { await ReaderWebView.CoreWebView2.ExecuteScriptAsync(GetReaderCoverFitScript()); }
        catch { }
    }

    // Images inside file:// EPUB chapters can finish decoding after
    // NavigationCompleted. Page scripts stay disabled (no load events fire), so
    // retry the cover fit a couple of times from the host side and re-snap the
    // page if the cover sizing changed the column layout. The retry is guarded
    // by the navigation sequence that requested it: if a newer chapter started
    // (or the reader closed), the delayed calls bail out before touching the DOM.
    private async Task RetryReaderImageFitAsync(int sequence)
    {
        if (_readerAllowedRoot is null || ReaderWebView.CoreWebView2 is null) return;
        try
        {
            await Task.Delay(250);
            if (sequence != _readerChapterTransitionSequence || _readerCloseRequested) return;
            if (ReaderWebView.CoreWebView2 is null || ReaderWebView.Source is not { IsFile: true }) return;
            await FitReaderImagesAsync();
            if (sequence != _readerChapterTransitionSequence || _readerCloseRequested) return;
            if (_readerFlowMode == 1) await RealignReaderAfterRelayoutAsync();
            await Task.Delay(700);
            if (sequence != _readerChapterTransitionSequence || _readerCloseRequested) return;
            if (ReaderWebView.CoreWebView2 is null || ReaderWebView.Source is not { IsFile: true }) return;
            await FitReaderImagesAsync();
            if (sequence != _readerChapterTransitionSequence || _readerCloseRequested) return;
            if (_readerFlowMode == 1) await RealignReaderAfterRelayoutAsync();
        }
        catch
        {
        }
    }

    private async Task SnapReaderPaginationAsync()
    {
        if (ReaderWebView.CoreWebView2 is null || _readerAllowedRoot is null) return;
        if (_readerFlowMode != 1) return;
        try
        {
            await ReaderWebView.CoreWebView2.ExecuteScriptAsync(ReaderPaginationScripts.Snap);
        }
        catch { }
    }

    private async Task RunSameChapterLocationAsync(
        ReaderNavigationIntent intent,
        Uri target,
        int locationSequence)
    {
        if (IsStaleReaderLocation(locationSequence)) return;
        await ApplyReaderNavigationLocationAsync(intent, target);
        if (IsStaleReaderLocation(locationSequence)) return;
        // A same-chapter re-position never goes through NavigationCompleted, so
        // re-align the scroll-edge state here: otherwise a reset-to-top could
        // look like "user scrolled to the top edge" and the continuous-scroll
        // poll would immediately jump backward to the previous chapter.
        await PrimeReaderScrollEdgesAsync();
    }

    private bool IsStaleReaderLocation(int sequence) =>
        _readerCloseRequested || sequence != _readerLocationSequence;

    // Only the pending location payload belonging to the current navigation
    // intent may survive a navigation start. Everything else is cleared so a
    // superseded navigation's pending scroll target can never re-position the
    // new chapter (rapid TOC clicks, or a TOC click right after a search /
    // bookmark / annotation jump).
    private void PruneReaderPendingLocations(ReaderNavigationIntent intent)
    {
        if (!ReaderNavigationLocationPolicy.KeepsChunkOffset(intent)) _pendingReaderChunkOffset = null;
        if (!ReaderNavigationLocationPolicy.KeepsBookmarkQuote(intent)) _pendingReaderBookmarkQuote = null;
        if (!ReaderNavigationLocationPolicy.KeepsAnnotationScroll(intent)) _pendingReaderAnnotationScroll = null;
        if (!ReaderNavigationLocationPolicy.KeepsRestorePosition(intent)) _pendingReaderRestorePosition = null;
    }

    // Positions the newly loaded chapter according to WHY the navigation was
    // requested. Runs as part of the first-screen preparation (before reveal)
    // and also directly against the current page for same-chapter clicks.
    private async Task ApplyReaderNavigationLocationAsync(ReaderNavigationIntent intent, Uri target)
    {
        switch (intent)
        {
            case ReaderNavigationIntent.Toc when ReaderNavigationLocationPolicy.TocTargetHasAnchor(target):
                // A TOC entry that explicitly carries a fragment anchor goes to
                // that anchor (the browser already scrolled there on load; this
                // re-applies it after our CSS pass for layout shifts).
                await ScrollToReaderFragmentAsync(ReaderNavigationLocationPolicy.TocAnchorId(target));
                return;
            case ReaderNavigationIntent.Toc:
            case ReaderNavigationIntent.Progress:
                // Plain chapter entries / progress-slider jumps: start at the
                // chapter's first line, never inherit the old chapter's offset.
                await ResetReaderToChapterStartAsync();
                return;
            case ReaderNavigationIntent.Bookmark:
                await ScrollToPendingReaderBookmarkAsync();
                return;
            case ReaderNavigationIntent.Annotation:
                // Apply the mark before positioning the new chapter. The
                // annotation jump itself is part of the first-screen path;
                // waiting until deferred post-navigation work means the
                // scroll target can be cleared before the mark exists.
                await ApplyReaderAnnotationsToPageAsync();
                await ScrollToPendingReaderAnnotationAsync();
                return;
            case ReaderNavigationIntent.Search:
            case ReaderNavigationIntent.AiSource:
                await ScrollToPendingReaderChunkAsync();
                return;
            case ReaderNavigationIntent.None:
            default:
                // Open-book breakpoint restore (only armed by the open-book
                // flow) or a plain prev/next/continuous chapter switch. When no
                // breakpoint is pending the fresh chapter should also start at
                // its first line (with the opening normalization), not inherit
                // an old position or an EPUB opener margin.
                if (ReaderNavigationLocationPolicy.ShouldNormalizeChapterStart(
                        intent, target, _pendingReaderRestorePosition is not null))
                {
                    await ResetReaderToChapterStartAsync();
                    return;
                }
                await ApplyReaderRestorePositionAsync();
                return;
        }
    }

    // Moves the actual scroll container back to the start of the chapter body.
    // Scroll mode: scrollTop = 0 keeps the chapter's own top padding, so the
    // first line starts at the viewport's top inner padding (never flush
    // against the window edge). Pagination: scrollTop is pinned to 0 and
    // scrollLeft is snapped onto the first viewport boundary (0), so the
    // chapter can never open mid-column from a previous page.
    //
    // scrollTop = 0 alone is NOT enough: some chapters carry a large top
    // margin on their FIRST element (e.g. the EPUB stylesheet `div.chatu-part
    // { margin-top: 30% }` used by every part/thanks/reference opener of this
    // book, or an opener heading's own margin), or start with blank nodes
    // (empty br/p/div). Those push the first visible line away from the design
    // start line even though the scroll container is at 0. So before scrolling
    // we first normalize the chapter opening: drop the leading blank blocks and
    // zero only the first content element's top margin (heading hierarchy,
    // interior paragraph spacing, image/cover fit and anchor targets stay
    // untouched).
    private async Task ResetReaderToChapterStartAsync()
    {
        if (ReaderWebView.CoreWebView2 is null) return;
        await NormalizeReaderChapterStartAsync();
        try
        {
            await ReaderWebView.CoreWebView2.ExecuteScriptAsync(
                "window.scrollTo({ left: 0, top: 0, behavior: 'instant' });");
        }
        catch { }
        if (_readerFlowMode == 1) await SnapReaderPaginationAsync();
    }

    private async Task NormalizeReaderChapterStartAsync()
    {
        if (ReaderWebView.CoreWebView2 is null) return;
        try
        {
            await ReaderWebView.CoreWebView2.ExecuteScriptAsync(ReaderNavigationScripts.NormalizeChapterStart);
        }
        catch { }
    }

    // Jumps to an explicit fragment anchor (a genuine TOC heading anchor) and
    // pins the target subchapter to the TOP of the reading area in both modes.
    //
    // This is NOT a plain scrollIntoView: the reported bug is that a subchapter
    // fragment inside one XHTML lands mid-column in pagination mode even after
    // scrollTop=0 — the target heading shares a CSS column with the previous
    // paragraph, so it stops in the middle of the page (y≈405 in the reported
    // screenshot). scrollIntoView only moves the scroll container; it cannot
    // move the heading to the top of a column.
    //
    // The fix therefore:
    //   1. Resolves the anchor by id/name; hidden/empty targets fall back to
    //      the next valid heading/paragraph/image, and a completely missing
    //      target falls back to the chapter's first line (host-side).
    //   2. Marks the target's block with a one-off `.kkindle-fragment-break`
    //      class so `break-before: column` forces it to start a NEW column in
    //      pagination mode (never sharing a column with the previous text).
    //      The class stays for the current chapter only and is cleared by
    //      plain chapter-start navigation / chapter switches — the book's
    //      source structure is never modified.
    //   3. Zeroes the target block's top margin so the heading text is flush
    //      with the content-box start line (body padding is preserved).
    //   4. Scrolls the real reading surface:
    //        - scroll mode: target's document top minus the body's padding-top,
    //          so the heading lands on the content-box start line (padding
    //          retained, never flush against the window edge);
    //        - pagination: scrollLeft = target column's viewport boundary (a
    //          snap-stable `n × viewport` boundary), scrollTop = 0;
    //        - vertical writing: block-start (right edge) aligned to the
    //          content box's right edge, inline-start (top) to its top.
    private async Task ScrollToReaderFragmentAsync(string fragment)
    {
        if (ReaderWebView.CoreWebView2 is null || string.IsNullOrWhiteSpace(fragment)) return;
        var needle = Uri.UnescapeDataString(fragment).Replace("\\", "\\\\").Replace("'", "\\'");
        var flowMode = _readerFlowMode;
        var vertical = _readerLayout.VerticalWriting;
        string result;
        try
        {
            result = await ReaderWebView.CoreWebView2.ExecuteScriptAsync(
                ReaderNavigationScripts.CreateFragmentScroll(
                    needle,
                    flowMode,
                    vertical,
                    _readerLayout.TwoPageMode)) ?? "null";
        }
        catch
        {
            result = "null";
        }

        var positioned = false;
        try
        {
            using var document = JsonDocument.Parse(result);
            positioned = document.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch
        {
        }

        if (!positioned)
        {
            // The fragment points to a hidden/empty/missing node and no valid
            // fallback content exists in front of it: land on the chapter's
            // first line instead of leaving a blank or mispositioned page.
            await ResetReaderToChapterStartAsync();
            return;
        }

        // The script aligns from the target's rendered column. A generic
        // n * viewport snap here would discard its measured inset correction
        // and recreate the left/right offset at fractional WebView widths.
        // Delayed font/image/host reflows call this target-aware method again.
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
        if (_readerFlowMode == 1) await SnapReaderPaginationAsync();
    }

    private static bool IsPathInside(string root, string path)
    {
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(boundary, StringComparison.OrdinalIgnoreCase);
    }

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
            isActive: ReferenceEquals(sectionButton, _activeNavigationSectionButton),
            isHovered: _hoveredSidebarSections.Contains(sectionButton),
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
            isActive: ReferenceEquals(sectionButton, _activeNavigationSectionButton),
            isHovered: true,
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
            isActive: ReferenceEquals(sectionButton, _activeNavigationSectionButton),
            isHovered: false,
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
        bool isActive,
        bool isHovered,
        bool animate)
    {
        var targetBackground = isActive
            ? Colors.Black
            : isHovered
                ? Windows.UI.Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2)
                : Colors.White;
        var targetForeground = isActive ? Colors.White : Colors.Black;
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
        if (activeButton == FontManagementNavigationButton || activeButton == DictionaryManagementNavigationButton)
        {
            _activeNavigationSectionButton = DeviceManagementSectionButton;
            ExpandSidebarSection(DeviceManagementSectionButton, DeviceManagementChildren, DeviceManagementChevron, "设备管理");
        }
        else if (activeButton == ReaderNotesNavigationButton || activeButton == ReaderExportNavigationButton
            || activeButton == ReadingDashboardNavigationButton)
        {
            _activeNavigationSectionButton = ReadingSectionButton;
            ExpandSidebarSection(ReadingSectionButton, ReadingChildren, ReadingChevron, "阅读资料");
        }
        else if (activeButton == SettingsNavigationButton || activeButton == KindleEmailSettingsNavigationButton
            || activeButton == ZLibraryAccountNavigationButton)
        {
            _activeNavigationSectionButton = SystemSectionButton;
            ExpandSidebarSection(SystemSectionButton, SystemChildren, SystemChevron, "系统");
        }
        else
        {
            _activeNavigationSectionButton = BookManagementSectionButton;
            ExpandSidebarSection(BookManagementSectionButton, BookManagementChildren, BookManagementChevron, "书籍管理");
        }

        var ink = (Brush)Application.Current.Resources["InkBrush"];
        var paper = (Brush)Application.Current.Resources["CardBrush"];
        var muted = (Brush)Application.Current.Resources["MutedInkBrush"];
        var idleIndicator = (Brush)Application.Current.Resources["SidebarIndicatorBrush"];
        foreach (var button in new[]
        {
            AllBooksButton,
            KindleBooksButton,
            ZLibraryBooksButton,
            FontManagementNavigationButton,
            DictionaryManagementNavigationButton,
            ReaderNotesNavigationButton,
            ReaderExportNavigationButton,
            ReadingDashboardNavigationButton,
            SettingsNavigationButton,
            KindleEmailSettingsNavigationButton,
            ZLibraryAccountNavigationButton
        })
        {
            var isActive = button == activeButton;
            button.Background = paper;
            button.Foreground = isActive ? ink : muted;
            button.BorderBrush = isActive ? ink : idleIndicator;
            button.FontWeight = isActive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
        AllBooksLabelText.Foreground = activeButton == AllBooksButton ? ink : muted;
        SidebarCountText.Foreground = activeButton == AllBooksButton ? ink : muted;

        ApplySidebarSectionColors(BookManagementSectionButton, BookManagementChevron,
            isActive: ReferenceEquals(BookManagementSectionButton, _activeNavigationSectionButton),
            isHovered: _hoveredSidebarSections.Contains(BookManagementSectionButton), animate: false);
        ApplySidebarSectionColors(DeviceManagementSectionButton, DeviceManagementChevron,
            isActive: ReferenceEquals(DeviceManagementSectionButton, _activeNavigationSectionButton),
            isHovered: _hoveredSidebarSections.Contains(DeviceManagementSectionButton), animate: false);
        ApplySidebarSectionColors(ReadingSectionButton, ReadingChevron,
            isActive: ReferenceEquals(ReadingSectionButton, _activeNavigationSectionButton),
            isHovered: _hoveredSidebarSections.Contains(ReadingSectionButton), animate: false);
        ApplySidebarSectionColors(SystemSectionButton, SystemChevron,
            isActive: ReferenceEquals(SystemSectionButton, _activeNavigationSectionButton),
            isHovered: _hoveredSidebarSections.Contains(SystemSectionButton), animate: false);
        QueueInteractiveControlToolTipRefresh();
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
