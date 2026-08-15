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
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Platform.Windows;

namespace Kkindle;

public sealed partial class MainWindow : Window
{
    private enum KindleBookViewMode { Grid, List }

    private static readonly HashSet<string> ImportableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw3"
    };
    private readonly AppPaths _paths;
    private readonly IBookLibraryService _library;
    private readonly IBookFormatConverter _formatConverter;
    private readonly ReaderFormatCacheService _readerFormatCache;
    private readonly IKindleDeviceService _kindle;
    private readonly DeviceModelStore _deviceModelStore;
    private readonly EpubReaderPreparationService _epubReader;
    private readonly DispatcherQueueTimer _deviceTimer;
    private readonly DispatcherQueueTimer _deviceConnectedToastTimer;
    private Book? _selectedBook;
    private bool _detailFavorite;
    private LibraryReadingStatus _detailReadingStatus;
    private Storyboard? _detailPaneAnimation;
    private IReadOnlyList<KindleDevice> _devices = [];
    private string? _deviceDisplayName;
    private bool _isRefreshingDevices;
    private bool _isTransferring;
    private readonly object _deviceOperationSync = new();
    private readonly HashSet<Task> _activeDeviceOperations = [];
    private bool _deviceEjectInProgress;
    private double _deviceUsedRatio;
    private EventHandler<object>? _deviceStatusLayoutUpdatedHandler;
    private CancellationTokenSource? _transferCancellation;
    private CancellationTokenSource? _deviceScanCancellation;
    private Task? _deviceScanTask;
    private bool _isUpdatingFilters;
    private CancellationTokenSource? _librarySearchDebounceCancellation;
    private string? _scannedDeviceId;
    private string? _acceptedDeviceId;
    private string? _ignoredDeviceId;
    private string? _manuallyDisconnectedDeviceId;
    private Button? _activeNavigationButton;
    private Button? _activeNavigationSectionButton;
    private readonly HashSet<Button> _hoveredSidebarSections = [];
    private KindleBookViewMode _kindleBookViewMode = KindleBookViewMode.Grid;
    // Cached on the UI thread so the low-level mouse-hook callback can check
    // the foreground window without touching WinUI objects.
    private IntPtr _readerWindowHandle;
    private TaskCompletionSource<bool>? _devicePromptCompletion;
    private sealed record MessageDialogRequest(string Title, string Message, TaskCompletionSource<bool> Completion);
    private readonly Queue<MessageDialogRequest> _messageDialogQueue = new();
    private MessageDialogRequest? _activeMessageDialog;
    private readonly List<(string FilePath, ToggleSwitch Toggle)> _importFormatSelectionRows = [];
    private TaskCompletionSource<IReadOnlyDictionary<string, IReadOnlyCollection<string>>?>? _importFormatSelectionCompletion;
    private bool _nativeChromeConfigured;
    private AppWindow? _appWindow;
    private OverlappedPresenter? _windowPresenter;
    private IDeviceChangeNotifier? _deviceChangeMonitor;
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
    private bool _readerAssistantExpanded;
    private bool _readerHasToc;
    private bool _readerZenMode;
    // Zen mode + maximized window → the app switches to the FullScreen
    // presenter, which hides the Windows taskbar so the reader fills the whole
    // screen. This tracks whether that presenter switch is active.
    private bool _zenFullScreenActive;
    private bool _zenWasMaximizedBeforeFullScreen;
    // Zen auto-hide chrome: the top Kreader text, zen buttons and window
    // caption buttons hide after inactivity and reappear on mouse movement.
    // The minimal TOC rail on the left stays untouched while open.
    private bool _readerZenChromeVisible = true;
    private long _readerZenLastMouseMoveTick;
    private DispatcherQueueTimer? _readerZenChromeHideTimer;
    private bool _readerPreZenTocExpanded = true;
    private bool _readerPreZenTocMinimal;
    private bool _readerPreZenAssistantExpanded;
    private const int ReaderAnimationNone = 0;
    private const int ReaderAnimationFade = 1;
    private const int ReaderAnimationSlide = 2;
    private const int ReaderAnimationWave = 3;
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
    private IntPtr _readerKeyboardHook;
    private volatile bool _readerMouseDownInside;
    private POINT _readerMouseDownPoint;
    private volatile int _readerWheelDeltaRemainder;
    private LowLevelMouseProc? _readerMouseProc;
    private LowLevelKeyboardProc? _readerKeyboardProc;
    private volatile bool _readerTextInputFocused;
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
    // The first EPUB document must not be revealed until Kreader's font CSS
    // has been injected and the browser font set has finished loading. Without
    // this guard the EPUB font paints for one frame before the reader font.
    private bool _readerInitialRevealPending;
    private Task? _readerWebViewInitializationTask;
    // Dual WebView preload: the visible surface shows the current chapter while
    // the hidden surface prepares the next one. ReaderActiveWebView resolves to
    // whichever surface is currently on screen, so every existing reader path
    // keeps operating against the visible document.
    private bool _readerShowingPreload;
    private int _readerPreloadChapterIndex = -1;
    private Uri? _readerPreloadTarget;
    private WebView2? _readerPreloadControl;
    private bool _readerPreloadReady;
    private bool _readerPreloadInProgress;
    private Task? _readerPreloadWebViewInitializationTask;

    // Direction + animation style for the incoming chapter transition. Style is
    // 1 = fade (淡入淡出), 2 = slide (左右滑动), 3 = wave (水波流动). Recorded when the
    // navigation starts so a far-chapter jump never plays a long per-page-looking
    // slide. Wave transitions carry the outgoing page snapshot so the same
    // flowing effect can reveal the new chapter underneath.
    private readonly record struct ReaderTurnInAnimation(
        int Direction,
        int Style,
        byte[]? Snapshot = null);

    public MainWindow(
        AppPaths paths,
        ISecretProtector protector,
        IBookLibraryService library,
        IBookFormatConverter formatConverter,
        IKindleDeviceService kindle,
        DeviceModelStore deviceModels,
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
        _backupService = new AppBackupService(paths, protector);
        _appSettingsStore = new AppSettingsStore(paths);
        _dictionaryService = new DictionaryService(paths);
        _fontLibrary = new FontLibraryService(paths);
        _pdfTextService = new PdfTextService();
        _formatConverter = formatConverter;
        _readerFormatCache = new ReaderFormatCacheService(paths, formatConverter);
        _kindle = kindle;
        _deviceModelStore = deviceModels;
        _kindleEmailSettingsStore = new KindleEmailSettingsStore(paths, protector);
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
        ReadingMaterialsList.ItemsSource = ReadingMaterialGroups;
        ConfigureReaderFeatureHosts();
        ConfigureTitleBar();
        SetActiveNavigation(AllBooksButton);
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;

        _deviceTimer = DispatcherQueue.CreateTimer();
        _deviceTimer.Interval = TimeSpan.FromSeconds(3);
        _deviceTimer.Tick += async (_, _) =>
        {
            // Native device notifications handle removal while connected. Polling a
            // live MTP Kindle repeatedly reopens WPD and can defeat its Disconnect UI.
            if ((_devices.Count == 0 || _deviceChangeMonitor is null)
                && _manuallyDisconnectedDeviceId is null)
                await RefreshDevicesAsync();
        };
        _deviceTimer.Start();
        _deviceConnectedToastTimer = DispatcherQueue.CreateTimer();
        _deviceConnectedToastTimer.Interval = TimeSpan.FromSeconds(2);
        _deviceConnectedToastTimer.Tick += async (_, _) =>
        {
            _deviceConnectedToastTimer.Stop();
            ClearPendingDeviceStatusPositionUpdate();
            await FadeOutDeviceStatusToastAsync();
        };
        RootGrid.Loaded += MainWindow_Loaded;
        RootGrid.Loaded += (_, _) => EnsureInteractiveControlToolTips(RootGrid);
        RootGrid.KeyDown += RootGrid_KeyDown;
        RootGrid.GotFocus += (_, _) => _readerTextInputFocused = IsReaderTextInputFocused();
        RootGrid.LostFocus += (_, _) => DispatcherQueue.TryEnqueue(
            () => _readerTextInputFocused = IsReaderTextInputFocused());
        ReaderProgressSlider.ThumbToolTipValueConverter =
            new ReaderProgressToolTipValueConverter(GetReaderProgressSliderLabel);
    }

    public LibraryViewModel ViewModel { get; }
    public ObservableCollection<KindleBookCardViewModel> DeviceBooks { get; } = [];
    public ObservableCollection<KindleDeviceResource> DeviceResources { get; } = [];
    public ObservableCollection<ReadingMaterialItemViewModel> ReadingMaterials { get; } = [];
    public ObservableCollection<ReadingMaterialGroupViewModel> ReadingMaterialGroups { get; } = [];

    private void ConfigureTitleBar()
    {
        Title = "Kkindle";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        _readerWindowHandle = WindowNative.GetWindowHandle(this);
        _windowActive = args.WindowActivationState == WindowActivationState.CodeActivated
            || args.WindowActivationState == WindowActivationState.PointerActivated;
        if (!_windowActive)
        {
            // Do not carry a press or partial wheel delta across an
            // Alt+Tab/minimize transition. Otherwise the next activation
            // could complete input that began in another application.
            _readerMouseDownInside = false;
            _readerWheelDeltaRemainder = 0;
        }
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
            _deviceChangeMonitor = new WindowsDeviceChangeNotifier(windowHandle);
            _deviceChangeMonitor.DeviceChanged += DeviceChangeMonitor_DeviceChanged;
        }
        catch
        {
            _deviceChangeMonitor = null; // The three-second polling timer remains the reliable fallback.
        }
        appWindow.Title = "Kkindle";
        appWindow.Changed += AppWindow_Changed;
        // The OS caption X / Alt+F4 also follow the "close the reader first"
        // default while Kreader is open: cancel the window close and return to
        // the library instead of exiting the whole application.
        appWindow.Closing += AppWindow_Closing;

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
        // In zen fullscreen the taskbar is hidden; the caption button then
        // acts as "restore" back to a normal maximized window.
        if (_zenFullScreenActive)
        {
            _zenFullScreenActive = false;
            _appWindow?.SetPresenter(AppWindowPresenterKind.Overlapped);
            SetWindowsTaskbarVisible(true);
            if (_appWindow?.Presenter is OverlappedPresenter restored)
            {
                if (_zenWasMaximizedBeforeFullScreen)
                    restored.Maximize();
            }
            UpdateMaximizeGlyph();
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplySquareWindowFrame);
            return;
        }

        if (_windowPresenter is null) return;
        if (_windowPresenter.State == OverlappedPresenterState.Maximized)
            _windowPresenter.Restore();
        else
        {
            _windowPresenter.Maximize();
            if (_readerZenMode) ApplyReaderZenFullScreen();
        }
        UpdateMaximizeGlyph();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplySquareWindowFrame);
    }

    // While Kreader is open the caption X acts as "close the reader" and
    // returns to the main interface (same default as the reader's 返回书架
    // button); only a second click on the library exits the application.
    private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReaderPane.Visibility == Visibility.Visible)
        {
            if (!_readerCloseInProgress) CloseReader();
            return;
        }
        Close();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static IntPtr _taskbarWindowHandle;
    private static bool _taskbarHiddenByZen;

    // Windows 11's auto-hide taskbar can leave a thin line at the bottom even
    // while an app is in the FullScreen presenter. Hide the real taskbar window
    // while zen fullscreen is active and restore it afterwards.
    private static void SetWindowsTaskbarVisible(bool visible)
    {
        try
        {
            if (!visible && _taskbarHiddenByZen) return;
            if (visible && !_taskbarHiddenByZen) return;
            if (_taskbarWindowHandle == IntPtr.Zero)
                _taskbarWindowHandle = FindWindow("Shell_TrayWnd", null);
            if (_taskbarWindowHandle != IntPtr.Zero)
            {
                _ = ShowWindow(_taskbarWindowHandle, visible ? 5 : 0);
                _taskbarHiddenByZen = !visible;
            }
        }
        catch
        {
        }
    }

    private void UpdateMaximizeGlyph()
    {
        var isMaximized = _zenFullScreenActive
            || _windowPresenter?.State == OverlappedPresenterState.Maximized;
        MaximizeWindowGlyph.Glyph = isMaximized ? "\uE923" : "\uE922";
        MaximizeWindowButton.SetValue(
            Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty,
            isMaximized ? "还原" : "最大化");
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (ReaderPane.Visibility != Visibility.Visible || _readerCloseInProgress) return;
        args.Cancel = true;
        CloseReader();
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

        // In fullscreen there is no window frame to color; the black border
        // attribute can render as a thin line along the window edges. Paint it
        // white (the reader background) so no line shows at the bottom.
        var borderColor = _zenFullScreenActive ? 0xFFFFFF : 0x000000;
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
        DispatcherQueue.TryEnqueue(async () =>
        {
            // A real USB removal/reconnect is the signal that polling may resume
            // after the user explicitly disconnected an MTP Kindle.
            _manuallyDisconnectedDeviceId = null;
            await RefreshDevicesAsync();
        });
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        // Never leave the Windows taskbar hidden if the app exits while zen
        // fullscreen is active.
        SetWindowsTaskbarVisible(true);
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
        _deviceResourceScanCancellation?.Cancel();
        _deviceResourceScanCancellation?.Dispose();
        _deviceResourceScanCancellation = null;
        _readingMaterialsCancellation?.Cancel();
        _readingMaterialsCancellation?.Dispose();
        _readingMaterialsCancellation = null;
        _librarySearchDebounceCancellation?.Cancel();
        _librarySearchDebounceCancellation?.Dispose();
        _librarySearchDebounceCancellation = null;
        StopScrollbarAutoHide();
        _ = FlushReaderSessionSafelyAsync(skipWebViewCapture: true);

        _deviceTimer.Stop();
        _deviceConnectedToastTimer.Stop();
        _deviceScanCancellation?.Cancel();
        _deviceScanCancellation?.Dispose();
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
        QueueScrollbarAutoHideRefresh();
        ConstrainRootToViewport();
        SettingsDataPathText.Text = _paths.Data;
        DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () => _ = WarmReaderActiveWebViewAsync());
        await LoadProductivityStateAsync();
        _zLibrarySettings = await _zLibrarySettingsStore.LoadAsync();
        UpdateZLibraryAccountStatus();
        await RefreshLibraryAsync();
        await RefreshBookCollectionsAsync();
        await RefreshDevicesAsync();
    }

    private async Task WarmReaderActiveWebViewAsync()
    {
        try { await EnsureReaderActiveWebViewReadyAsync(); }
        catch { }
    }

    // The reader document that is currently on screen. All reader code paths
    // operate against this instance; the preload surface is only ever shown by
    // flipping this flag during a chapter swap.
    private WebView2 ReaderActiveWebView =>
        _readerShowingPreload ? ReaderPreloadWebView : ReaderWebView;

    private void SetReaderWebViewLayer(WebView2 webView, bool visible)
    {
        Canvas.SetZIndex(webView, visible ? 2 : 1);
        webView.Opacity = visible ? 1 : 0;
        webView.IsHitTestVisible = visible;
    }

    private void ResetReaderPreloadState()
    {
        _readerShowingPreload = false;
        _readerPreloadChapterIndex = -1;
        _readerPreloadTarget = null;
        _readerPreloadControl = null;
        _readerPreloadReady = false;
        _readerPreloadInProgress = false;
        SetReaderWebViewLayer(ReaderWebView, visible: true);
        SetReaderWebViewLayer(ReaderPreloadWebView, visible: false);
    }

    private Task EnsureReaderPreloadWebViewReadyAsync()
    {
        if (ReaderPreloadWebView.CoreWebView2 is not null)
        {
            ConfigureReaderWebViewSettings(ReaderPreloadWebView);
            return Task.CompletedTask;
        }

        return _readerPreloadWebViewInitializationTask ??= InitializeReaderPreloadWebViewAsync();
    }

    private async Task InitializeReaderPreloadWebViewAsync()
    {
        try
        {
            await ReaderPreloadWebView.EnsureCoreWebView2Async();
            ConfigureReaderWebViewSettings(ReaderPreloadWebView);
        }
        catch
        {
            _readerPreloadWebViewInitializationTask = null;
            throw;
        }
    }

    private Task EnsureReaderActiveWebViewReadyAsync()
    {
        if (ReaderActiveWebView.CoreWebView2 is not null)
        {
            ConfigureReaderActiveWebView();
            return Task.CompletedTask;
        }

        return _readerWebViewInitializationTask ??= InitializeReaderActiveWebViewAsync();
    }

    private async Task InitializeReaderActiveWebViewAsync()
    {
        try
        {
            await ReaderActiveWebView.EnsureCoreWebView2Async();
            ConfigureReaderActiveWebView();
        }
        catch
        {
            _readerWebViewInitializationTask = null;
            throw;
        }
    }

    private async Task RefreshLibraryAsync()
    {
        ClearMultiSelection();
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
        ReconcileLibraryPresence();
        ApplyLibraryGalleryDisplay();
        // Collection folder counts and cover thumbnails depend on the current
        // library membership, so keep them in sync after imports/deletes/etc.
        _ = RefreshBookCollectionsAsync();
    }

    private async Task RefreshDevicesAsync()
    {
        if (_isRefreshingDevices) return;
        // Do not reopen Windows Shell/WPD sessions after an explicit disconnect.
        // Repeated polling prevented Kindle's own Disconnect button from completing
        // on the first press. RefreshDeviceButton and native USB changes clear this.
        if (_manuallyDisconnectedDeviceId is not null) return;
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
            var displayName = await _deviceModelStore.GetModelAsync(device.Identity) ?? device.Name;
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
                        SetDisconnectedDeviceState($"已忽略 {displayName}");
                        return;
                    }

                    if (!await ShowDevicePromptAsync(
                            "发现 Kindle 设备",
                            $"发现 {displayName}（{device.ConnectionLabel}）。是否连接到 Kkindle？",
                            "连接",
                            "暂不连接"))
                    {
                        _ignoredDeviceId = device.Identity;
                        SetDisconnectedDeviceState($"已忽略 {displayName}");
                        return;
                    }
                    _acceptedDeviceId = device.Identity;
                    _ignoredDeviceId = null;
                }
            }

            _devices = [device];
            _deviceDisplayName = displayName;
            KindleStatusText.Text = _deviceDisplayName;
            KindleConnectionText.Text = $"{device.ConnectionLabel} · 已连接";
            EjectDeviceButton.Visibility = Visibility.Visible;
            EjectDeviceButton.IsEnabled = true;
            var disconnectAction = device.Transport == KindleTransport.Wpd
                ? "停止访问设备"
                : "安全弹出设备";
            AutomationProperties.SetName(EjectDeviceButton, disconnectAction);
            ToolTipService.SetToolTip(EjectDeviceButton, disconnectAction);
            DeviceStorageText.Text = device.CapacityLabel;
            DevicePageEjectButton.IsEnabled = true;
            AutomationProperties.SetName(DevicePageEjectButton, disconnectAction);
            ToolTipService.SetToolTip(DevicePageEjectButton, disconnectAction);
            _deviceUsedRatio = device.TotalBytes <= 0
                ? 0
                : Math.Clamp((device.TotalBytes - device.FreeBytes) / (double)device.TotalBytes, 0, 1);
            UpdateDeviceStorageBar();
            DeviceNameButton.IsEnabled = true;
            DeviceNameText.Text = $"{_deviceDisplayName} · {device.ConnectionLabel}";
            KindleConnectionText.Visibility = Visibility.Visible;
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
        catch (OperationCanceledException) when (_manuallyDisconnectedDeviceId is not null)
        {
            // Explicit disconnect cancels the active WPD scan before ejecting.
        }
        catch
        {
            SetDisconnectedDeviceState("设备状态读取失败");
        }
        finally { _isRefreshingDevices = false; }
    }

    private void SetDisconnectedDeviceState(
        string? detail = null,
        bool preserveDeviceToast = false)
    {
        var refreshReadingMaterials = ReadingMaterialsPage.Visibility == Visibility.Visible
            && (_readingMaterialsDeviceId is not null
                || _allReadingMaterials.Any(item => item.Source == ReadingMaterialSource.Kindle));
        _deviceResourceCancellation?.Cancel();
        _deviceResourceScanCancellation?.Cancel();
        _readingMaterialsCancellation?.Cancel();
        _deviceScanCancellation?.Cancel();
        _devices = [];
        _deviceDisplayName = null;
        if (!preserveDeviceToast)
        {
            _deviceConnectedToastTimer.Stop();
            ClearPendingDeviceStatusPositionUpdate();
            DeviceStatusPopup.IsOpen = false;
            DeviceStatusToast.Opacity = 1;
        }
        _scannedDeviceId = null;
        _scannedResourceDeviceId = null;
        _scannedResourceKind = null;
        _readingMaterialsDeviceId = null;
        DeviceBooks.Clear();
        ClearDeviceMultiSelection();
        ReconcileLibraryPresence();
        KindleStatusText.Text = "无设备连接";
        KindleConnectionText.Text = detail ?? string.Empty;
        KindleConnectionText.Visibility = string.IsNullOrWhiteSpace(detail)
            ? Visibility.Collapsed
            : Visibility.Visible;
        EjectDeviceButton.Visibility = Visibility.Visible;
        EjectDeviceButton.IsEnabled = false;
        AutomationProperties.SetName(EjectDeviceButton, "未连接设备");
        ToolTipService.SetToolTip(EjectDeviceButton, "未连接设备");
        DeviceStorageText.Text = "无存储信息";
        DevicePageEjectButton.IsEnabled = false;
        AutomationProperties.SetName(DevicePageEjectButton, "未连接设备");
        ToolTipService.SetToolTip(DevicePageEjectButton, "未连接设备");
        _deviceUsedRatio = 0;
        UpdateDeviceStorageBar();
        DeviceNameButton.IsEnabled = false;
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
        => ShowDeviceStatusToast($"{_deviceDisplayName ?? device.Name} 已连接");

    private void ShowDeviceStatusToast(string message)
    {
        DeviceConnectedToastText.Text = message;
        DeviceStatusToast.Opacity = 1;
        DeviceStatusPopup.IsOpen = true;
        PositionDeviceStatusToast();
        ScheduleDeviceStatusPositionAfterLayout();
        _deviceConnectedToastTimer.Stop();
        _deviceConnectedToastTimer.Start();
    }

    // Fades the device status toast out over ~350 ms before closing it, so the
    // connection/disconnection bubble does not vanish abruptly.
    private async Task FadeOutDeviceStatusToastAsync()
    {
        try
        {
            var storyboard = new Storyboard();
            var fade = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(350)),
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(fade, DeviceStatusToast);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);
            storyboard.Begin();
            await Task.Delay(350);
            try { storyboard.Stop(); } catch { }
        }
        catch
        {
        }
        DeviceStatusToast.Opacity = 1;
        DeviceStatusPopup.IsOpen = false;
    }

    private void ScheduleDeviceStatusPositionAfterLayout()
    {
        ClearPendingDeviceStatusPositionUpdate();
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            if (handler is not null) EjectDeviceButton.LayoutUpdated -= handler;
            if (ReferenceEquals(_deviceStatusLayoutUpdatedHandler, handler))
                _deviceStatusLayoutUpdatedHandler = null;
            DispatcherQueue.TryEnqueue(PositionDeviceStatusToast);
        };
        _deviceStatusLayoutUpdatedHandler = handler;
        EjectDeviceButton.LayoutUpdated += handler;
    }

    private void ClearPendingDeviceStatusPositionUpdate()
    {
        if (_deviceStatusLayoutUpdatedHandler is null) return;
        EjectDeviceButton.LayoutUpdated -= _deviceStatusLayoutUpdatedHandler;
        _deviceStatusLayoutUpdatedHandler = null;
    }

    private void PositionDeviceStatusToast()
    {
        var anchor = EjectDeviceButton;
        if (!DeviceStatusPopup.IsOpen || anchor.XamlRoot is null) return;
        DeviceStatusToast.Measure(
            new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var popupSize = DeviceStatusToast.DesiredSize;
        // Both eject icons are 8-DIP upward triangles centered in their buttons.
        // Anchor to the triangle apex rather than the button bounds.
        var anchorTriangleApex = anchor.TransformToVisual(RootGrid).TransformPoint(
            new Windows.Foundation.Point(anchor.ActualWidth / 2, anchor.ActualHeight / 2 - 4));
        const double edgeMargin = 8;
        const double pointerCenterFromLeft = 20;
        var maxLeft = Math.Max(edgeMargin, RootGrid.ActualWidth - popupSize.Width - edgeMargin);
        var popupLeft = Math.Clamp(anchorTriangleApex.X - pointerCenterFromLeft, edgeMargin, maxLeft);
        var popupTop = Math.Max(edgeMargin, anchorTriangleApex.Y - popupSize.Height - 2);
        var pointerLeft = Math.Clamp(
            anchorTriangleApex.X - popupLeft - DeviceStatusToastPointer.Width / 2,
            0,
            Math.Max(0, popupSize.Width - DeviceStatusToastPointer.Width));

        DeviceStatusToastPointer.Margin = new Thickness(pointerLeft, 0, 0, 0);
        DeviceStatusPopup.HorizontalOffset = popupLeft;
        DeviceStatusPopup.VerticalOffset = popupTop;
    }

    private async Task ScanDeviceBooksAsync(KindleDevice device)
    {
        _deviceScanCancellation?.Cancel();
        _deviceScanCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _deviceScanCancellation = cancellation;
        DeviceNameText.Text = $"{_deviceDisplayName ?? device.Name} · 正在快速读取书籍列表…";
        var scanProgress = new Progress<KindleScanProgress>(ApplyKindleScanProgress);
        var scanTask = _kindle.ScanBooksProgressivelyAsync(device, scanProgress, cancellation.Token);
        _deviceScanTask = scanTask;
        try
        {
            // Backstop: per-book enrichment already has a timeout, but a hung
            // enumeration or cache write must never leave the UI stuck on
            // "正在完成扫描" forever.
            var completed = await Task.WhenAny(
                scanTask,
                Task.Delay(TimeSpan.FromMinutes(10)));
            if (completed != scanTask)
            {
                cancellation.Cancel();
                DeviceNameText.Text = $"{_deviceDisplayName ?? device.Name} · 扫描超时，已停止读取";
                _scannedDeviceId = device.Identity;
                try { await scanTask; } catch (OperationCanceledException) { } catch { }
                return;
            }

            var books = await scanTask;
            cancellation.Token.ThrowIfCancellationRequested();
            DeviceBooks.Clear();
            ClearDeviceMultiSelection();
            foreach (var book in books) DeviceBooks.Add(new KindleBookCardViewModel(book));
            ApplyLibraryGalleryDisplay();
            ReconcileLibraryPresence();
            DeviceBookCountText.Text = books.Count.ToString();
            DeviceNameText.Text = $"{_deviceDisplayName ?? device.Name} · {device.ConnectionLabel}";
            _scannedDeviceId = device.Identity;
        }
        finally
        {
            if (ReferenceEquals(_deviceScanTask, scanTask)) _deviceScanTask = null;
            if (ReferenceEquals(_deviceScanCancellation, cancellation))
            {
                _deviceScanCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private async Task StopDeviceAccessAsync()
    {
        _deviceResourceCancellation?.Cancel();
        _readingMaterialsCancellation?.Cancel();
        _deviceScanCancellation?.Cancel();

        var scanTask = _deviceScanTask;
        if (scanTask is null) return;
        try
        {
            await scanTask.WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
            throw new IOException("后台 Kindle 扫描未能及时停止，请稍后重试断开。");
        }
    }

    private void RefreshLibraryView()
    {
        ViewModel.RefreshView();
        UpdateLibraryPresentationState();
    }

    private void ApplyKindleScanProgress(KindleScanProgress progress)
    {
        if (progress.Stage == KindleScanStage.Enumerated)
        {
            DeviceBooks.Clear();
            ClearDeviceMultiSelection();
            foreach (var book in progress.Books)
                DeviceBooks.Add(new KindleBookCardViewModel(book));
        }
        else
        {
            foreach (var path in progress.RemovedPaths)
            {
                var existing = DeviceBooks.FirstOrDefault(card => card.Book.RelativePath.Equals(
                    path,
                    StringComparison.OrdinalIgnoreCase));
                if (existing is not null) DeviceBooks.Remove(existing);
            }

            foreach (var book in progress.Books)
            {
                var existingIndex = -1;
                for (var index = 0; index < DeviceBooks.Count; index++)
                {
                    if (!DeviceBooks[index].Book.RelativePath.Equals(
                            book.RelativePath,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    existingIndex = index;
                    break;
                }
                var card = new KindleBookCardViewModel(book);
                if (existingIndex >= 0) DeviceBooks[existingIndex] = card;
                else DeviceBooks.Add(card);
            }
        }

        DeviceBookCountText.Text = DeviceBooks.Count.ToString();
        var deviceName = _deviceDisplayName ?? _devices.FirstOrDefault()?.Name ?? "Kindle";
        DeviceNameText.Text = progress.Processed >= progress.Total
            ? $"{deviceName} · 正在完成扫描…"
            : $"{deviceName} · 已读取 {progress.Processed} / {progress.Total}";
        ReconcileLibraryPresence();
    }

    private void ReconcileLibraryPresence()
    {
        var comparisonEnabled = _appSettings.CompareKindleLibraryEnabled;
        foreach (var card in ViewModel.Books) card.SetLibraryPresenceVisible(comparisonEnabled);
        foreach (var card in DeviceBooks) card.SetLibraryPresenceVisible(comparisonEnabled);
        if (!comparisonEnabled) return;

        var comparison = BookLibraryComparer.Compare(
            ViewModel.LibraryBooks,
            DeviceBooks.Select(card => card.Book));

        foreach (var card in ViewModel.Books)
            card.SetLibraryPresence(comparison.BooksOnKindle.Contains(card.Book.Id)
                ? BookLibraryPresence.Both
                : BookLibraryPresence.ComputerOnly);

        foreach (var card in DeviceBooks)
            card.SetLibraryPresence(comparison.KindleBooksOnComputer.Contains(card.Book.RelativePath)
                ? BookLibraryPresence.Both
                : BookLibraryPresence.KindleOnly);
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

    // Expands dropped or picked paths into the actual book files to import:
    // files are kept as-is when supported, folders are scanned recursively.
    private static IEnumerable<string> ExpandImportableFiles(IEnumerable<string> paths)
    {
        foreach (var input in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (File.Exists(input) && ImportableExtensions.Contains(Path.GetExtension(input)))
            {
                yield return Path.GetFullPath(input);
                continue;
            }

            if (!Directory.Exists(input)) continue;
            foreach (var file in Directory.EnumerateFiles(input, "*.*", SearchOption.AllDirectories)
                .Where(file => ImportableExtensions.Contains(Path.GetExtension(file))))
                yield return Path.GetFullPath(file);
        }
    }

    private async void LibraryPane_Drop(object sender, DragEventArgs e)
    {
        DropImportOverlay.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items
            .Where(item => item is Windows.Storage.StorageFile or Windows.Storage.StorageFolder)
            .Select(item => item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        var files = ExpandImportableFiles(paths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
        {
            await ShowMessageAsync("无法导入", "拖入的文件或文件夹中没有 EPUB、PDF、MOBI 或 AZW3 书籍文件。");
            return;
        }

        if (!_appSettings.AutoGenerateEpubAndAzw3OnImport)
        {
            await ImportAsync(files);
            return;
        }

        var formatSelection = await PromptImportFormatSelectionAsync(files);
        if (formatSelection is null) return;
        await ImportAsync(
            formatSelection.Count == 0 ? files : formatSelection.Keys,
            formatSelection);
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        CancelLibrarySearchDebounce();
        ViewModel.SearchText = sender.Text;
        RefreshLibraryView();
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.SearchText = SearchBox.Text;
        CancelLibrarySearchDebounce();
        var cancellation = new CancellationTokenSource();
        _librarySearchDebounceCancellation = cancellation;
        try
        {
            await Task.Delay(300, cancellation.Token);
            if (!ReferenceEquals(_librarySearchDebounceCancellation, cancellation)) return;
            RefreshLibraryView();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_librarySearchDebounceCancellation, cancellation))
                _librarySearchDebounceCancellation = null;
            cancellation.Dispose();
        }
    }

    private void CancelLibrarySearchDebounce()
    {
        var cancellation = _librarySearchDebounceCancellation;
        _librarySearchDebounceCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
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
            FavoritesOnlyFilterBox.ItemsSource = new[] { "全部", "仅收藏" };
            AuthorFilterBox.SelectedItem = ViewModel.AuthorFilter ?? "全部作者";
            TagFilterBox.SelectedItem = ViewModel.TagFilter ?? "全部标签";
            FormatFilterBox.SelectedItem = ViewModel.FormatFilter?.ToUpperInvariant() ?? "全部格式";
            CategoryFilterBox.SelectedItem = ViewModel.CategoryFilter ?? "全部分类";
            ReadingStatusFilterBox.SelectedIndex = ViewModel.ReadingStatusFilter is { } status ? (int)status + 1 : 0;
            LibrarySortBox.SelectedIndex = (int)ViewModel.SortMode;
            FavoritesOnlyFilterBox.SelectedIndex = ViewModel.FavoritesOnly ? 1 : 0;
            var activeCount = new[] { ViewModel.AuthorFilter, ViewModel.TagFilter, ViewModel.FormatFilter, ViewModel.CategoryFilter }
                .Count(value => !string.IsNullOrWhiteSpace(value));
            if (ViewModel.ReadingStatusFilter is not null) activeCount++;
            if (ViewModel.FavoritesOnly) activeCount++;
            var filterLabel = activeCount == 0 ? "筛选" : $"筛选 · 已启用 {activeCount} 项";
            AutomationProperties.SetName(FilterButton, filterLabel);
            ToolTipService.SetToolTip(FilterButton, filterLabel);
        }
        finally { _isUpdatingFilters = false; }
    }

    private void UpdateEmptyLibraryState()
    {
        EmptyLibraryState.Visibility = _libraryViewMode != LibraryViewMode.Collections && ViewModel.Books.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        var hasQuery = !string.IsNullOrWhiteSpace(ViewModel.SearchText) || ViewModel.HasActiveFilters;
        EmptyLibraryTitleText.Text = hasQuery ? "没有符合条件的书籍" : "电脑书库还是空的";
        EmptyLibraryMessageText.Text = hasQuery
            ? "调整搜索词或清除筛选后再试"
            : "拖入书籍文件，或使用右上角的导入按钮";
    }

    private void FilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
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
        ViewModel.FavoritesOnly = FavoritesOnlyFilterBox.SelectedIndex > 0;
        ViewModel.SortMode = LibrarySortBox.SelectedIndex < 0
            ? LibrarySortMode.UpdatedDescending
            : (LibrarySortMode)LibrarySortBox.SelectedIndex;
        RefreshLibraryView();
    }

    private void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.AuthorFilter = null;
        ViewModel.TagFilter = null;
        ViewModel.FormatFilter = null;
        ViewModel.CategoryFilter = null;
        ViewModel.ReadingStatusFilter = null;
        ViewModel.FavoritesOnly = false;
        ViewModel.SortMode = LibrarySortMode.UpdatedDescending;
        RefreshLibraryView();
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
        if (folder is null) return;

        if (!_appSettings.AutoGenerateEpubAndAzw3OnImport)
        {
            await ImportAsync([folder.Path]);
            return;
        }

        var files = ExpandImportableFiles([folder.Path]).ToList();
        var formatSelection = await PromptImportFormatSelectionAsync(
            files,
            file => Path.GetRelativePath(folder.Path, file));
        if (formatSelection is null) return;
        await ImportAsync(
            formatSelection.Count == 0 ? [folder.Path] : formatSelection.Keys,
            formatSelection);
    }

    // Import with the "补齐 EPUB 与 AZW3" option enabled asks the user to choose
    // per book whether the missing formats should be generated after the import.
    // The monochrome overlay lists every book file with one toggle switch (on by
    // default); the returned map is keyed by the full path of each book file and
    // holds the formats the user kept switched on.
    private Task<IReadOnlyDictionary<string, IReadOnlyCollection<string>>?> PromptImportFormatSelectionAsync(
        IReadOnlyCollection<string> files,
        Func<string, string>? displayNameSelector = null)
    {
        if (_importFormatSelectionCompletion is not null)
            return _importFormatSelectionCompletion.Task;

        var orderedFiles = files
            .OrderBy(file => displayNameSelector?.Invoke(file) ?? Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFullPath)
            .ToList();

        if (orderedFiles.Count == 0)
            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyCollection<string>>?>(
                new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase));

        ImportFormatSelectionList.Children.Clear();
        _importFormatSelectionRows.Clear();
        foreach (var file in orderedFiles)
        {
            var displayName = displayNameSelector?.Invoke(file) ?? Path.GetFileName(file);
            var format = Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
            var name = new TextBlock
            {
                Text = $"{displayName}（{format}）",
                FontSize = 14,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(name, file);
            var toggle = new ToggleSwitch
            {
                OnContent = "开",
                OffContent = "关",
                IsOn = true,
                MinWidth = 120,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(toggle, "导入后自动补齐缺失的 EPUB / AZW3");
            var row = new Grid { ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(name, 0);
            Grid.SetColumn(toggle, 1);
            row.Children.Add(name);
            row.Children.Add(toggle);
            ImportFormatSelectionList.Children.Add(row);
            _importFormatSelectionRows.Add((file, toggle));
        }

        ImportFormatSelectionSummaryText.Text =
            $"将导入 {orderedFiles.Count} 本书籍文件；打开开关表示导入后自动补齐缺失的 EPUB / AZW3（默认全部开启）。";
        ImportFormatSelectionOverlay.Visibility = Visibility.Visible;
        QueueScrollbarAutoHideRefresh(ImportFormatSelectionOverlay);
        ImportFormatSelectionOverlay.Focus(FocusState.Programmatic);

        var completion = new TaskCompletionSource<IReadOnlyDictionary<string, IReadOnlyCollection<string>>?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _importFormatSelectionCompletion = completion;
        return completion.Task;
    }

    private void ImportFormatSelectionPrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        var completion = _importFormatSelectionCompletion;
        if (completion is null) return;
        _importFormatSelectionCompletion = null;

        var result = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (filePath, toggle) in _importFormatSelectionRows)
            result[filePath] = toggle.IsOn ? new[] { "epub", "azw3" } : [];
        ImportFormatSelectionOverlay.Visibility = Visibility.Collapsed;
        completion.TrySetResult(result);
    }

    private void ImportFormatSelectionCancelButton_Click(object sender, RoutedEventArgs e) =>
        CompleteImportFormatSelection(cancelled: true);

    private void ImportFormatSelectionOverlay_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            CompleteImportFormatSelection(cancelled: true);
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            ImportFormatSelectionPrimaryButton_Click(sender, e);
        }
    }

    private void CompleteImportFormatSelection(bool cancelled)
    {
        var completion = _importFormatSelectionCompletion;
        if (completion is null) return;
        _importFormatSelectionCompletion = null;
        ImportFormatSelectionOverlay.Visibility = Visibility.Collapsed;
        completion.TrySetResult(
            cancelled
                ? null
                : new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase));
    }

    private async Task ImportAsync(
        IEnumerable<string> paths,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? requestedFormatsBySourcePath = null)
    {
        ShowTaskProgressPopup();
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
            // Books are already in the library; refresh the presentation before
            // the automatic EPUB/AZW3 generation starts so the empty state is
            // hidden and the newly imported books are visible right away.
            UpdateLibraryPresentationState();
            if (result.FailureCount > 0)
            {
                var failures = string.Join("\n", result.Items.Where(x => !x.Succeeded).Take(5).Select(x => $"{Path.GetFileName(x.SourcePath)}：{x.Message}"));
                await ShowMessageAsync("部分文件未导入", failures);
            }
            var automaticFormats = await AutoGenerateReaderFormatsForImportsAsync(
                result,
                cancellationToken: default,
                requestedFormatsBySourcePath);
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
            HideTaskProgressPopup();
        }
    }

    private void SelectBook(Book book)
    {
        HideSettingsPanel();
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
        var wasVisible = DetailPane.Visibility == Visibility.Visible;
        DetailColumn.Width = new GridLength(ComputeGoldenDetailWidth());
        MainContentColumn.Width = new GridLength(1, GridUnitType.Star);
        DetailPane.Visibility = Visibility.Visible;
        QueueScrollbarAutoHideRefresh(DetailPane);
        if (!wasVisible || _detailPaneAnimation is not null)
        {
            // First open of the details pane (or superseding an in-flight
            // entrance/exit): slide the pane in from the right edge. Deferred
            // so the column width is laid out before the offset is measured.
            DispatcherQueue.TryEnqueue(AnimateDetailPaneIn);
        }
        else
        {
            // Re-selecting a book while the pane is already open (e.g. after
            // saving edits) should not replay the entrance animation.
            ResetDetailPaneAnimationState();
        }
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

    private async void SendToKindleButton_Click(object sender, RoutedEventArgs e) =>
        await TrackDeviceOperationAsync(SendToKindleAsync);

    private async Task SendToKindleAsync()
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
        ShowTaskProgressPopup();
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
            HideTaskProgressPopup();
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
        if (DetailPane.Visibility != Visibility.Visible)
        {
            HideDetailPaneInstant();
            return;
        }
        AnimateDetailPaneOut();
    }

    // Slides the book-details pane in from the right edge of the window while
    // fading it in. Plays only once per open (see SelectBook).
    private void AnimateDetailPaneIn()
    {
        if (DetailPane.Visibility != Visibility.Visible) return;
        _detailPaneAnimation?.Stop();
        _detailPaneAnimation = null;

        var offset = DetailPane.ActualWidth > 0 ? DetailPane.ActualWidth : ComputeGoldenDetailWidth();
        if (offset <= 0)
        {
            ResetDetailPaneAnimationState();
            return;
        }

        DetailPaneTranslate.X = offset;
        DetailPane.Opacity = 0;

        var duration = new Duration(TimeSpan.FromMilliseconds(220));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        var translate = new DoubleAnimation
        {
            From = offset,
            To = 0,
            Duration = duration,
            EnableDependentAnimation = true,
            EasingFunction = easing
        };
        Storyboard.SetTarget(translate, DetailPaneTranslate);
        Storyboard.SetTargetProperty(translate, "X");

        var opacity = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = duration,
            EnableDependentAnimation = true,
            EasingFunction = easing
        };
        Storyboard.SetTarget(opacity, DetailPane);
        Storyboard.SetTargetProperty(opacity, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(translate);
        storyboard.Children.Add(opacity);
        storyboard.Completed += (_, _) =>
        {
            // A completed storyboard still owns its animated properties; stop
            // it before restoring the base transform/opacity.
            _detailPaneAnimation = null;
            storyboard.Stop();
            ResetDetailPaneAnimationState();
        };
        _detailPaneAnimation = storyboard;
        storyboard.Begin();
    }

    // Slides the book-details pane out to the right edge of the window while
    // fading it out; the pane and its column collapse only after it has left.
    private void AnimateDetailPaneOut()
    {
        var offset = DetailPane.ActualWidth > 0 ? DetailPane.ActualWidth : ComputeGoldenDetailWidth();
        if (offset <= 0)
        {
            HideDetailPaneInstant();
            return;
        }

        // Capture the current animated values before the running storyboard is
        // stopped, so a close that interrupts an entrance continues from where
        // the pane actually is instead of snapping to the base values.
        var fromX = DetailPaneTranslate.X;
        var fromOpacity = DetailPane.Opacity;
        _detailPaneAnimation?.Stop();
        _detailPaneAnimation = null;

        var duration = new Duration(TimeSpan.FromMilliseconds(180));
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };

        var translate = new DoubleAnimation
        {
            From = fromX,
            To = offset,
            Duration = duration,
            EnableDependentAnimation = true,
            EasingFunction = easing
        };
        Storyboard.SetTarget(translate, DetailPaneTranslate);
        Storyboard.SetTargetProperty(translate, "X");

        var opacity = new DoubleAnimation
        {
            From = fromOpacity,
            To = 0,
            Duration = duration,
            EnableDependentAnimation = true,
            EasingFunction = easing
        };
        Storyboard.SetTarget(opacity, DetailPane);
        Storyboard.SetTargetProperty(opacity, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(translate);
        storyboard.Children.Add(opacity);
        storyboard.Completed += (_, _) =>
        {
            _detailPaneAnimation = null;
            storyboard.Stop();
            HideDetailPaneInstant();
        };
        _detailPaneAnimation = storyboard;
        storyboard.Begin();
    }

    // Collapses the details pane immediately (no animation) and resets the
    // animation state so the next open starts from a clean transform. Used by
    // page switches that replace the pane, and by the exit completion handler.
    private void HideDetailPaneInstant()
    {
        _detailPaneAnimation?.Stop();
        _detailPaneAnimation = null;
        DetailPane.Visibility = Visibility.Collapsed;
        MainContentColumn.Width = new GridLength(1, GridUnitType.Star);
        DetailColumn.Width = new GridLength(0);
        ResetDetailPaneAnimationState();
    }

    private void ResetDetailPaneAnimationState()
    {
        DetailPaneTranslate.X = 0;
        DetailPane.Opacity = 1;
    }

    private void ShowSettingsPanel(FrameworkElement panel)
    {
        HideDetailPaneInstant();
        KindleEmailSettingsPane.Visibility = Visibility.Collapsed;
        ZLibraryAccountPane.Visibility = Visibility.Collapsed;
        ReaderAiSettingsPane.Visibility = Visibility.Collapsed;
        panel.Visibility = Visibility.Visible;
        QueueScrollbarAutoHideRefresh(panel);
        // The settings panel fills the whole right side of the window.
        MainContentColumn.Width = new GridLength(0);
        DetailColumn.Width = new GridLength(1, GridUnitType.Star);
    }

    private void HideSettingsPanel()
    {
        KindleEmailSettingsPane.Visibility = Visibility.Collapsed;
        ZLibraryAccountPane.Visibility = Visibility.Collapsed;
        ReaderAiSettingsPane.Visibility = Visibility.Collapsed;
        MainContentColumn.Width = new GridLength(1, GridUnitType.Star);
        DetailColumn.Width = new GridLength(0);
    }

    private async void LibraryViewMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        var nextMode = tag switch
        {
            "List" => LibraryViewMode.List,
            "Collections" => LibraryViewMode.Collections,
            _ => LibraryViewMode.Grid
        };
        if (nextMode == LibraryViewMode.Collections && ViewModel.CollectionFilterId is not null)
        {
            ViewModel.CollectionFilterId = null;
            ViewModel.CollectionFilterName = null;
            await RefreshLibraryAsync();
        }
        SetLibraryViewMode(nextMode);
    }

    private void SetLibraryViewMode(LibraryViewMode mode)
    {
        _libraryViewMode = mode;
        BookGrid.Visibility = mode == LibraryViewMode.Grid ? Visibility.Visible : Visibility.Collapsed;
        BookList.Visibility = mode == LibraryViewMode.List ? Visibility.Visible : Visibility.Collapsed;
        CollectionFolderGrid.Visibility = mode == LibraryViewMode.Collections ? Visibility.Visible : Visibility.Collapsed;
        CollectionBookHeader.Visibility = mode != LibraryViewMode.Collections
            && ViewModel.CollectionFilterId is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
        EmptyLibraryState.Visibility = mode != LibraryViewMode.Collections && ViewModel.Books.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateCollectionEmptyState();

        var (symbol, label) = mode switch
        {
            LibraryViewMode.Grid => (Symbol.ViewAll, "网格"),
            LibraryViewMode.List => (Symbol.Bullets, "列表"),
            _ => (Symbol.Folder, "收藏夹")
        };
        LibraryViewToggleIcon.Symbol = symbol;
        if (LibraryViewGridItem is not null)
        {
            LibraryViewGridItem.IsChecked = mode == LibraryViewMode.Grid;
            LibraryViewListItem.IsChecked = mode == LibraryViewMode.List;
            LibraryViewCollectionsItem.IsChecked = mode == LibraryViewMode.Collections;
        }
        var viewLabel = $"当前：{label}视图";
        AutomationProperties.SetName(LibraryViewToggleButton, viewLabel);
        ToolTipService.SetToolTip(LibraryViewToggleButton, viewLabel);
    }

    private void FilterButton_Click(object sender, RoutedEventArgs e) =>
        FilterPanel.Visibility = FilterPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    private async void MoreButton_Click(object sender, RoutedEventArgs e) => await ShowMessageAsync("Kkindle", $"便携数据目录：{_paths.Data}");
    private async void AddTagButton_Click(object sender, RoutedEventArgs e) => await ShowMessageAsync("标签", "可以在书籍详情中直接编辑标签，多个标签用逗号分隔。");
    private async void AddCategoryButton_Click(object sender, RoutedEventArgs e) => await ShowMessageAsync("分类", "分类功能将在书库筛选基础完成后接入。");
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void SettingsCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        ShowSettingsSection(tag);
    }

    private void ShowSettingsSection(string tag)
    {
        SettingsGeneralSection.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
        SettingsLibrarySection.Visibility = tag == "Library" ? Visibility.Visible : Visibility.Collapsed;
        SettingsKindleSection.Visibility = tag == "Kindle" ? Visibility.Visible : Visibility.Collapsed;
        SettingsReadingSection.Visibility = tag == "Reading" ? Visibility.Visible : Visibility.Collapsed;
        SettingsBackupSection.Visibility = tag == "Backup" ? Visibility.Visible : Visibility.Collapsed;
        SettingsAboutSection.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;

        // Keep the content panel anchored to its top so every category opens with
        // the same starting position relative to the left navigation column.
        SettingsScrollViewer.ChangeView(null, 0, null, disableAnimation: true);

        var selectedColor = Color.FromArgb(255, 0x00, 0x00, 0x00);
        var idleColor = Color.FromArgb(255, 0x5A, 0x5A, 0x5A);
        var idleIndicatorColor = Color.FromArgb(255, 0xCF, 0xCF, 0xCF);
        foreach (var button in new[]
        {
            SettingsGeneralButton, SettingsLibraryButton, SettingsKindleButton,
            SettingsReadingButton, SettingsBackupButton, SettingsAboutButton
        })
        {
            var selected = string.Equals(button.Tag as string, tag, StringComparison.OrdinalIgnoreCase);
            button.Background = new SolidColorBrush(Colors.White);
            button.Foreground = new SolidColorBrush(selected ? selectedColor : idleColor);
            button.BorderBrush = new SolidColorBrush(selected ? selectedColor : idleIndicatorColor);
            button.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }
    private void KindleBooksButton_Click(object sender, RoutedEventArgs e) => OpenDevicePage();

    private void KindleViewMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        SetKindleBookViewMode(tag == "List" ? KindleBookViewMode.List : KindleBookViewMode.Grid);
    }

    private void SetKindleBookViewMode(KindleBookViewMode mode)
    {
        _kindleBookViewMode = mode;
        DeviceBookGrid.Visibility = mode == KindleBookViewMode.Grid ? Visibility.Visible : Visibility.Collapsed;
        DeviceBookList.Visibility = mode == KindleBookViewMode.List ? Visibility.Visible : Visibility.Collapsed;

        var (symbol, label) = mode == KindleBookViewMode.List
            ? (Symbol.Bullets, "列表")
            : (Symbol.ViewAll, "网格");
        DeviceViewToggleIcon.Symbol = symbol;
        DeviceViewGridItem.IsChecked = mode == KindleBookViewMode.Grid;
        DeviceViewListItem.IsChecked = mode == KindleBookViewMode.List;
        ToolTipService.SetToolTip(DeviceViewToggleButton, $"当前：{label}视图");
    }

    private void OpenDevicePage()
    {
        SetActiveNavigation(KindleBooksButton);
        DevicePageTitleText.Text = "Kindle书库";
        SetKindleBookViewMode(_kindleBookViewMode);
        LibraryPane.Visibility = Visibility.Collapsed;
        SettingsPane.Visibility = Visibility.Collapsed;
        ZLibraryPage.Visibility = Visibility.Collapsed;
        DeviceResourcePage.Visibility = Visibility.Collapsed;
        ReadingMaterialsPage.Visibility = Visibility.Collapsed;
        ReadingDashboardPage.Visibility = Visibility.Collapsed;
        HideDetailPaneInstant();
        HideSettingsPanel();
        DevicePage.Visibility = Visibility.Visible;
    }

    private async void AllBooksButton_Click(object sender, RoutedEventArgs e) =>
        await ShowAllBooksAsync();

    private async void RefreshDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        _ignoredDeviceId = null;
        _manuallyDisconnectedDeviceId = null;
        _scannedDeviceId = null;
        await RefreshDevicesAsync();
    }

    // The bottom-left device status box is clickable: with a connected device
    // it opens the model picker; without one it re-detects and connects the
    // Kindle. The eject button inside the box keeps its own action.
    private async void DeviceStatusBox_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject original
            && IsAncestorOf(EjectDeviceButton, original))
        {
            return;
        }
        if (_devices.Count > 0)
        {
            ShowDeviceModelPicker(DeviceStatusBox);
            return;
        }
        _ignoredDeviceId = null;
        _manuallyDisconnectedDeviceId = null;
        _scannedDeviceId = null;
        await RefreshDevicesAsync();
    }

    private static bool IsAncestorOf(DependencyObject ancestor, DependencyObject node)
    {
        var current = node;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private bool HasActiveDeviceOperations
    {
        get
        {
            lock (_deviceOperationSync) return _activeDeviceOperations.Count > 0;
        }
    }

    private async Task TrackDeviceOperationAsync(Func<Task> operation)
    {
        // Once eject begins, existing operations are drained and new device work
        // must not reopen a WPD session before the removal request completes.
        if (_deviceEjectInProgress) return;
        var task = operation();
        lock (_deviceOperationSync) _activeDeviceOperations.Add(task);
        try
        {
            await task;
        }
        finally
        {
            lock (_deviceOperationSync) _activeDeviceOperations.Remove(task);
        }
    }

    private async Task WaitForActiveDeviceOperationsAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_deviceOperationSync) tasks = _activeDeviceOperations.ToArray();
            if (tasks.Length == 0) return;
            await Task.WhenAll(tasks);
        }
    }

    private void DeviceStorageBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateDeviceStorageBar();

    private void UpdateDeviceStorageBar()
    {
        var availableWidth = Math.Max(0, DeviceStorageBar.ActualWidth - 2);
        DeviceStorageUsedBar.Width = availableWidth * _deviceUsedRatio;
    }

    private async void EjectDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_devices.Count == 0 || _deviceEjectInProgress) return;
        _deviceEjectInProgress = true;
        var refreshReadingMaterialsAfterEject = false;

        var device = _devices[0];
        var isWpd = device.Transport == KindleTransport.Wpd;
        try
        {
            if (!await ShowDevicePromptAsync(
                    isWpd ? "停止访问 Kindle？" : "安全弹出 Kindle？",
                    isWpd
                        ? "Kkindle 将停止访问并释放设备会话；随后请在 Kindle 屏幕上点击“断开连接”。若有传输任务正在进行，将等待其完成后自动断开。"
                        : "若有传输任务正在进行，将等待其完成后自动断开。",
                    isWpd ? "停止访问" : "弹出",
                    "取消")) return;

            // Block refresh polling before waiting so no new WPD session can be
            // opened between the final operation and the eject request.
            _manuallyDisconnectedDeviceId = device.Identity;
            _deviceResourceScanCancellation?.Cancel();
            _readingMaterialsCancellation?.Cancel();
            _deviceScanCancellation?.Cancel();

            if (_isTransferring || _deviceResourceOperationInProgress || HasActiveDeviceOperations)
            {
                ShowTransferToast(
                    "正在等待设备操作完成",
                    "检测到设备任务正在进行，完成后将自动断开设备。",
                    isIndeterminate: true);
                try
                {
                    await WaitForActiveDeviceOperationsAsync();
                }
                finally
                {
                    HideTransferToast();
                }
            }

            await StopDeviceAccessAsync();
            await _kindle.EjectAsync(device);
            _acceptedDeviceId = null;
            _ignoredDeviceId = null;
            var disconnectedMessage = isWpd
                ? $"{device.Name} 已停止访问，现在可以安全移除你的设备"
                : $"{device.Name} 已安全弹出，现在可以安全移除你的设备";
            SetDisconnectedDeviceState(preserveDeviceToast: true);
            ShowDeviceStatusToast(disconnectedMessage);
            refreshReadingMaterialsAfterEject = ReadingMaterialsPage.Visibility == Visibility.Visible;
        }
        catch (Exception ex)
        {
            _manuallyDisconnectedDeviceId = null;
            await ShowMessageAsync("无法弹出设备", ex.Message);
        }
        finally
        {
            _deviceEjectInProgress = false;
            if (refreshReadingMaterialsAfterEject) _ = RefreshReadingMaterialsAsync();
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
        HideSettingsPanel();
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
            await TryRemoveReaderWaveOverlayAsync();
            ResetReaderPreloadState();
            BeginReaderSession(book, file);
            var readerToken = _readerFeatureCancellation!.Token;
            var webViewTask = EnsureReaderActiveWebViewReadyAsync();
            var sessionDataTask = LoadReaderSessionDataAsync(readerToken);
            await Task.WhenAll(webViewTask, sessionDataTask);
            ReaderBookInfoText.Text = $"{book.Title} · {file.Format.ToUpperInvariant()}";
            var isPdf = file.Format.Equals("pdf", StringComparison.OrdinalIgnoreCase);
            _readerInitialRevealPending = !isPdf;
            ReaderWebViewHost.Opacity = isPdf ? 1 : 0;
            ReaderPane.Visibility = Visibility.Visible;
            QueueScrollbarAutoHideRefresh();
            ReaderBrandText.Visibility = Visibility.Visible;
            ReaderPane.UpdateLayout();
            _readerTocExpanded = true;
            _readerTocMinimal = false;
            _readerAssistantExpanded = false;
            _readerFlowMode = _readerLayout.FlowMode;
            _readerZenMode = false;
            _readerContinuousLocked = false;
            ResetReaderChromeLayout();
            UpdateReaderZoom();
            UpdateReaderFlowButton();
            SyncReaderPageAnimationMenu();
            ApplyReaderPanelLayout();

            if (isPdf)
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
            SetReaderFooterNavigationMode(chapterNavigation: true);
            ReaderProgressSlider.Visibility = Visibility.Visible;
            ReaderPdfBottomText.Visibility = Visibility.Collapsed;
            ReaderFlowButton.Visibility = Visibility.Visible;
            ReaderHighlightButton.Visibility = Visibility.Collapsed;
            ReaderAnnotateButton.Visibility = Visibility.Collapsed;
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

    private void ConfigureReaderActiveWebView() => ConfigureReaderWebViewSettings(ReaderActiveWebView);

    private void ConfigureReaderWebViewSettings(WebView2 webView)
    {
        var settings = webView.CoreWebView2.Settings;
        settings.IsScriptEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        // The Chromium context menu is an implementation detail of WebView2
        // and appears in English independently of Kreader's UI language. Text
        // actions are provided by the native selection toolbar instead.
        settings.AreDefaultContextMenusEnabled = false;
    }

    private void ResetReaderAssistant()
    {
        ResetReaderFeatures();
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // SizeChanged carries the layout pass that is actually being rendered.
        // XamlRoot.Size can lag one pass behind while a window is maximizing,
        // which used to leave the reader constrained to the pre-maximize width.
        ConstrainRootToViewport(e.NewSize.Width);
    }

    private void ConstrainRootToViewport() => ConstrainRootToViewport(null);

    private void ConstrainRootToViewport(double? layoutWidth)
    {
        var viewportWidth = layoutWidth is > 0
            ? layoutWidth.Value
            : RootGrid.XamlRoot?.Size.Width ?? 0;
        if (viewportWidth <= 0) return;
        ApplyGoldenSidebarWidth(viewportWidth);
        if (DetailPane.Visibility == Visibility.Visible)
        {
            MainContentColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailColumn.Width = new GridLength(ComputeGoldenDetailWidth());
        }
        else if (KindleEmailSettingsPane.Visibility == Visibility.Visible
            || ZLibraryAccountPane.Visibility == Visibility.Visible
            || ReaderAiSettingsPane.Visibility == Visibility.Visible)
        {
            MainContentColumn.Width = new GridLength(0);
            DetailColumn.Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            MainContentColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailColumn.Width = new GridLength(0);
        }
        if (double.IsNaN(RootGrid.Width) || Math.Abs(RootGrid.Width - viewportWidth) > 0.5)
            RootGrid.Width = viewportWidth;
        ApplyReaderPanelLayout(viewportWidth);
    }

    private void ApplyGoldenSidebarWidth(double viewportWidth)
    {
        // Fixed narrow sidebar: proportional (golden-ratio) sizing made the
        // left side too wide on large displays.
        SidebarColumn.Width = new GridLength(200);
    }

    private double ComputeGoldenDetailWidth()
    {
        // Fixed detail panel width; the right side no longer mirrors a golden
        // fraction of the remaining width.
        return 320;
    }

    private void ReaderTocToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_readerSearchVisible && ReferenceEquals(sender, ReaderTocToggleButton))
        {
            ShowReaderTocFromSearch(bookmarkTab: false);
            return;
        }
        if (_readerSearchVisible)
            HideReaderSearchPanel(restorePreviousLayout: false);
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
        // An explicit width comes from the current SizeChanged pass and is more
        // reliable than XamlRoot.Size during maximize/restore transitions.
        var width = availableWidth ?? RootGrid.XamlRoot?.Size.Width ?? RootGrid.ActualWidth;
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
            ReaderSearchPanel.Visibility = Visibility.Visible;
        }

        UpdateReaderAssistantPopup(_readerAssistantExpanded);
        if (_readerZenMode) UpdateReaderZenPopup(true);

        ReaderTocToggleButton.Opacity = _readerTocExpanded ? 0.58 : 1;
        ReaderAssistantToggleButton.Opacity = _readerAssistantExpanded ? 0.58 : 1;
        // Refresh the cached WebView screen rect used by the low-level mouse
        // hook (the hook thread itself must never touch XAML). Layout changes
        // always re-run this and keep the cache in sync.
        try { GetReaderActiveWebViewScreenRect(); } catch { }
    }

    private void ReaderContentPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ReaderContentClip.Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        ScheduleReaderRelayout();
        try { GetReaderActiveWebViewScreenRect(); } catch { }
    }

    private void ReaderWebViewHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // WebView2 is a composition island and can trail its XAML Grid during
        // assistant/TOC width changes. Give the controller explicit bounds so
        // Chromium never keeps laying out a page at the previous wider size.
        if (e.NewSize.Width > 0) ReaderActiveWebView.Width = e.NewSize.Width;
        if (e.NewSize.Height > 0) ReaderActiveWebView.Height = e.NewSize.Height;
        // Keep BOTH surfaces pinned to the current reading viewport: the
        // hidden preload surface must not carry a stale explicit width into a
        // later swap, or the swapped-in document can be laid out for the wrong
        // width and clip its right boundary.
        var hiddenWebView = _readerShowingPreload ? ReaderWebView : ReaderPreloadWebView;
        if (e.NewSize.Width > 0 && hiddenWebView.CoreWebView2 is not null)
            hiddenWebView.Width = e.NewSize.Width;
        if (e.NewSize.Height > 0 && hiddenWebView.CoreWebView2 is not null)
            hiddenWebView.Height = e.NewSize.Height;
        ScheduleReaderRelayout();
        try { GetReaderActiveWebViewScreenRect(); } catch { }
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
        if (ReaderActiveWebView.CoreWebView2 is null || _readerAllowedRoot is null) return;
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
                    var converged = await WaitForReaderViewportToMatchHostAsync(token);
                    if (token.IsCancellationRequested) return;
                    await ApplyReaderAppearanceToVisibleAndPreloadAsync();
                    if (token.IsCancellationRequested) return;
                    await RealignReaderAfterRelayoutAsync();
                    // Chromium can converge AFTER the bounded wait. Re-apply
                    // once it does so pagination also fills the final viewport.
                    // If it never converges the appearance pass is still safe:
                    // columns are never laid out wider than the visible width.
                    if (!converged && !token.IsCancellationRequested)
                    {
                        await Task.Delay(200, token);
                        if (token.IsCancellationRequested) return;
                        if (await WaitForReaderViewportToMatchHostAsync(token)
                            && !token.IsCancellationRequested)
                        {
                            await ApplyReaderAppearanceToVisibleAndPreloadAsync();
                            if (token.IsCancellationRequested) return;
                            await RealignReaderAfterRelayoutAsync();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                }
            });
        });
    }

    // WebView2's Chromium viewport may trail the WinUI host by a few compositor
    // frames during maximize/restore. Applying pagination against that stale
    // viewport creates columns using the old width and leaves the visible page
    // horizontally offset. Wait until both surfaces agree before the final
    // appearance pass and page snap.
    private async Task<bool> WaitForReaderViewportToMatchHostAsync(CancellationToken token)
    {
        if (ReaderActiveWebView.CoreWebView2 is null) return false;

        const int maximumAttempts = 10;
        const double tolerance = 2;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var expectedWidth = ReaderWebViewHost.ActualWidth;
            var expectedHeight = ReaderWebViewHost.ActualHeight;
            if (expectedWidth <= 0 || expectedHeight <= 0) return false;

            try
            {
                var json = await ReaderActiveWebView.CoreWebView2.ExecuteScriptAsync(
                    "JSON.stringify({width: window.innerWidth, height: window.innerHeight})");
                var serialized = JsonSerializer.Deserialize<string>(json);
                if (!string.IsNullOrWhiteSpace(serialized))
                {
                    using var document = JsonDocument.Parse(serialized);
                    var root = document.RootElement;
                    var viewportWidth = root.GetProperty("width").GetDouble();
                    var viewportHeight = root.GetProperty("height").GetDouble();
                    if (Math.Abs(viewportWidth - expectedWidth) <= tolerance
                        && Math.Abs(viewportHeight - expectedHeight) <= tolerance)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // A navigation can briefly make the document unavailable.
                // The next attempt will observe the settled viewport.
            }

            await Task.Delay(40, token);
        }

        return false;
    }

    private async Task ClampReaderScrollAsync()
    {
        if (ReaderActiveWebView.CoreWebView2 is null || _readerAllowedRoot is null) return;
        if (_readerFlowMode == 1)
        {
            // Pagination: snap to the nearest column boundary (also clamps the
            // maximum scroll range so the reader never rests past the last page).
            await SnapReaderPaginationAsync();
            return;
        }
        var script = "(function(){var el=document.scrollingElement;var max=Math.max(0,el.scrollHeight-el.clientHeight);if(el.scrollTop>max)window.scrollTo({top:max});})()";
        try { await ReaderActiveWebView.CoreWebView2.ExecuteScriptAsync(script); }
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

    private void ReaderTocSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_readerSearchVisible)
        {
            ScheduleReaderSearch(ReaderTocSearchBox.Text.Trim());
            return;
        }

        ApplyReaderTocFilter();
    }

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
    //   timing is deliberate:
    //   1. The reader surface is hidden BEFORE the WebView starts navigating.
    //      Chromium swaps to the raw, unstyled new document the moment it
    //      commits — that paint can beat the NavigationCompleted handler's own
    //      hide-by-then and flash the chapter's first screen on top of the old
    //      one. Hiding up front means no raw content is ever visible; the
    //      pane's opaque background covers the (usually short) local-file load.
    //   2. After NavigationCompleted the essential first-screen work runs
    //      (styling/viewport, cover/image fit, target position restore,
    //      pagination snap, scroll-edge priming) while the new page is held
    //      behind the pane's opaque background.
    //   3. Only then does the selected fade/slide/wave animation reveal the
    //      ready first screen; 无动画 shows it immediately. Wave chapter
    //      transitions replay the captured outgoing page so the same flowing
    //      effect plays across chapters. Slow non-first-screen work (annotations,
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
        if (await TrySwapToPreloadedReaderChapterAsync(direction, animate, intent)) return;
        await NavigateReaderSourceAsync(new Uri(_readerChapters[_readerChapterIndex]), direction, animate, intent);
    }

    private async Task NavigateReaderSourceAsync(
        Uri target,
        int direction,
        bool animate,
        ReaderNavigationIntent intent = ReaderNavigationIntent.None)
    {
        if (target is null || ReaderActiveWebView.CoreWebView2 is null)
        {
            if (target is not null) ReaderActiveWebView.Source = target;
            return;
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
            ReaderActiveWebView.Source,
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
            {
                await RunSameChapterLocationAsync(intent, target, locationSequence);
                return;
            }
            return;
        }

        // Animations are decorative: never run them while closing, while the
        // pane is hidden, or when the user selected "无动画". "Jump" navigations
        // (TOC/search/bookmark/annotation/AI/progress slider) always use the
        // selected animation style, so every navigation path has predictable
        // behavior without pretending to drag through intermediate chapters.
        // Chapter switches use the same animation as in-chapter page turns,
        // including the wave: the outgoing page is snapshotted below and washed
        // away once the new chapter's first screen is ready.
        var chapterStyle = _readerPageAnimation;
        var shouldAnimate = animate
            && chapterStyle > ReaderAnimationNone
            && !_readerCloseRequested
            && ReaderPane.Visibility == Visibility.Visible;

        _readerChapterTransitionCancellation?.Cancel();
        _readerChapterTransitionCancellation?.Dispose();
        _readerChapterTransitionCancellation = new CancellationTokenSource();
        var token = _readerChapterTransitionCancellation.Token;
        var sequence = ++_readerChapterTransitionSequence;

        // Capture the outgoing page while it is still visible and before the
        // WebView navigates away; the wave turn-in later replays it on top of
        // the prepared new chapter. A failed capture simply falls back to the
        // fade reveal, so a chapter switch is never blocked by the animation.
        byte[]? waveSnapshot = null;
        if (shouldAnimate && chapterStyle == ReaderAnimationWave)
        {
            try
            {
                waveSnapshot = await CaptureReaderPageSnapshotAsync(
                    ReaderActiveWebView.CoreWebView2,
                    token);
            }
            catch (OperationCanceledException)
            {
                // A newer navigation or a reader close superseded this one;
                // that flow owns the reader state now.
                return;
            }
            catch
            {
                waveSnapshot = null;
            }
            if (token.IsCancellationRequested) return;
        }

        // The current page is kept on screen while the document loads; the
        // reader surface is hidden only after the new chapter commits and its
        // first screen is prepared (see ReaderWebView_NavigationCompleted).
        _readerTransitionActive = true;
        _readerPendingTurnInAnimation = shouldAnimate
            ? new ReaderTurnInAnimation(direction, chapterStyle, waveSnapshot)
            : null;
        _readerPendingNavigationTarget = target;
        // Hide the surface BEFORE the WebView starts navigating: the new
        // document's raw first paint lands at commit time, which can beat the
        // NavigationCompleted handler's own hide-by-then and flash an unstyled,
        // wrongly-positioned chapter on screen. Hiding up front guarantees the
        // reveal below only ever shows the prepared first screen.
        ReaderWebViewHost.Opacity = 0;
        try
        {
            ReaderActiveWebView.Source = target;
        }
        catch
        {
            // A stale navigation is fine; make sure the reader surface is never
            // left in a transformed/hidden state.
            _readerPendingTurnInAnimation = null;
            _readerPendingNavigationTarget = null;
            ResetReaderWebViewTransform();
            _readerTransitionActive = false;
            return;
        }
        // Watchdog: NavigationCompleted normally releases the transition guard;
        // if the navigation never reports back (or fails while a still-pending
        // target is waiting), release it after a few seconds so the scroll poll
        // can never be blocked permanently. Armed for every navigation, not just
        // animated ones, so a failed chapter in 无动画 mode is never stuck.
        _ = Task.Delay(3000).ContinueWith(
            _ => _readerTransitionActive = false,
            TaskScheduler.Default);
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
        if (_readerChapterIndex < 0)
        {
            ReaderChapterText.Text = string.Empty;
        }
        else
        {
            var chapterName = GetReaderChapterDisplayName(_readerChapterIndex);
            ReaderChapterText.Text = $"{_readerChapterIndex + 1} / {_readerChapters.Count} · {chapterName}";
            ToolTipService.SetToolTip(ReaderChapterText, chapterName);
        }
        ReaderPreviousButton.IsEnabled = _readerChapterIndex > 0;
        ReaderNextButton.IsEnabled = _readerChapterIndex + 1 < _readerChapters.Count;
        QueueReaderCompactScrollIndicatorUpdate();
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

    private string GetReaderChapterDisplayName(int chapterIndex)
    {
        var selected = ReaderTocList.SelectedItem as EpubReaderNavigationItem;
        var item = selected?.ChapterIndex == chapterIndex
            ? selected
            : _readerNavigation.FirstOrDefault(candidate => candidate.ChapterIndex == chapterIndex);
        if (!string.IsNullOrWhiteSpace(item?.Title)) return item.Title.Trim();
        if (chapterIndex >= 0 && chapterIndex < _readerChapters.Count)
        {
            var fileName = Path.GetFileNameWithoutExtension(_readerChapters[chapterIndex]);
            if (!string.IsNullOrWhiteSpace(fileName)) return fileName;
        }
        return $"第 {chapterIndex + 1} 章";
    }

    private string GetReaderProgressSliderLabel(int current)
    {
        var total = Math.Max(1, (int)ReaderProgressSlider.Maximum);
        current = Math.Clamp(current, 1, total);
        var label = IsPdfReader
            ? $"第 {current} 页"
            : GetReaderChapterDisplayName(current - 1);
        return $"{current} / {total} · {label}";
    }

    private async void ReaderPreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPdfReader) { await NavigatePdfPageAsync(_pdfCurrentPage - 1); return; }
        await NavigateReaderChapterAsync(-1);
    }

    private async void ReaderNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPdfReader) { await NavigatePdfPageAsync(_pdfCurrentPage + 1); return; }
        await NavigateReaderChapterAsync(1);
    }

    private async Task<bool> NavigateReaderChapterAsync(int direction)
    {
        if (!IsReaderWindowForeground()) return false;
        if (ReaderPane.Visibility != Visibility.Visible) return false;
        if (!_readerHasToc || _readerChapters.Count == 0) return false;
        if (_readerCloseRequested || _readerTransitionActive) return false;

        var normalizedDirection = direction < 0 ? -1 : 1;
        var targetIndex = _readerChapterIndex + normalizedDirection;
        if (targetIndex < 0 || targetIndex >= _readerChapters.Count) return false;

        _readerContinuousLocked = false;
        _readerChapterIndex = targetIndex;
        _readerNavigateToEnd = false;
        _readerLastChapterChange = DateTimeOffset.UtcNow;
        UpdateReaderChapterControls();
        _ = SaveReaderProgressThrottledAsync();
        await ShowReaderChapterAsync(
            normalizedDirection,
            animate: _readerPageAnimation > ReaderAnimationNone,
            intent: ReaderNavigationIntent.Toc);
        return true;
    }

    private void SetReaderFooterNavigationMode(bool chapterNavigation)
    {
        var previousLabel = chapterNavigation ? "上一章" : "上一页";
        var nextLabel = chapterNavigation ? "下一章" : "下一页";
        AutomationProperties.SetName(ReaderPreviousButton, previousLabel);
        AutomationProperties.SetName(ReaderNextButton, nextLabel);
        ToolTipService.SetToolTip(ReaderPreviousButton, previousLabel);
        ToolTipService.SetToolTip(ReaderNextButton, nextLabel);
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
        _ = ApplyReaderAppearanceToVisibleAndPreloadAsync();
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
        await ApplyReaderAppearanceToVisibleAndPreloadAsync();
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
        var entering = !_readerZenMode;
        _readerZenMode = entering;
        if (!entering)
        {
            _ = ExitReaderZenModeSmoothlyAsync();
            return;
        }

        ApplyReaderZenLayout();
        ReaderZenMenuItem.IsChecked = true;
        ReaderZenTitleExitButton.Visibility = Visibility.Visible;
        UpdateReaderZenTocToggle();
        ApplyReaderZenFullScreen();
        UpdateReaderZenChrome(false);
    }

    // Leaving zen restores the side panels, header/footer bars and the window
    // size in one go, which makes the paginated body text reflow and jump.
    // Mask that behind an opaque cover (the reader pane floats above the
    // library, so fading the pane itself would reveal the bookshelf), restore
    // everything, let the relayout settle, then fade the cover away.
    private async Task ExitReaderZenModeSmoothlyAsync()
    {
        try
        {
            ReaderTransitionCover.Opacity = 1;
            ApplyReaderZenLayout();
            ReaderZenMenuItem.IsChecked = false;
            ReaderZenTitleExitButton.Visibility = Visibility.Collapsed;
            UpdateReaderZenTocToggle();
            ApplyReaderZenFullScreen();
            // The reader relayout pipeline (120 ms debounce + viewport sync +
            // appearance re-apply + page snap) needs a moment to settle before
            // the restored body text is revealed.
            await Task.Delay(320);
            await FadeReaderTransitionCoverAsync(1, 0, 180);
        }
        catch
        {
            ReaderTransitionCover.Opacity = 0;
        }
    }

    private async Task FadeReaderTransitionCoverAsync(double from, double to, int durationMs)
    {
        try
        {
            var storyboard = new Storyboard();
            var fade = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(fade, ReaderTransitionCover);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);
            storyboard.Begin();
            await Task.Delay(durationMs);
            try { storyboard.Stop(); } catch { }
        }
        catch
        {
        }
        ReaderTransitionCover.Opacity = to;
    }

    // Entering zen mode always switches to the FullScreen presenter: the
    // Windows taskbar is hidden and the reading surface truly fills the whole
    // screen, regardless of the window's previous size. Leaving zen (or closing
    // the reader) restores the previous overlapped window state.
    private void ApplyReaderZenFullScreen()
    {
        if (_appWindow is null) return;
        try
        {
            if (_readerZenMode)
            {
                if (!_zenFullScreenActive)
                {
                    _zenFullScreenActive = true;
                    _zenWasMaximizedBeforeFullScreen =
                        _appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
                    _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                    SetWindowsTaskbarVisible(false);
                    ForceReaderFullScreenBounds();
                    DispatcherQueue.TryEnqueue(
                        DispatcherQueuePriority.Low,
                        ForceReaderFullScreenBounds);
                }
            }
            else if (_zenFullScreenActive)
            {
                _zenFullScreenActive = false;
                _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                SetWindowsTaskbarVisible(true);
                if (_appWindow.Presenter is OverlappedPresenter restored)
                {
                    if (_zenWasMaximizedBeforeFullScreen)
                        restored.Maximize();
                }
            }
        }
        catch
        {
            // Presenter switches are best-effort; a failure must never break
            // the reader itself.
        }
    }

    // The FullScreen presenter can land the window 1 px short of the display on
    // some DPI/monitor setups, which shows a thin line at the bottom edge.
    // Snap the window to the exact display outer bounds so nothing peeks below.
    private void ForceReaderFullScreenBounds()
    {
        try
        {
            if (!_zenFullScreenActive || _appWindow is null) return;
            var displayArea = DisplayArea.GetFromWindowId(
                _appWindow.Id,
                DisplayAreaFallback.Nearest);
            _appWindow.MoveAndResize(displayArea.OuterBounds);
        }
        catch
        {
        }
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
        var visibility = _readerZenMode && _readerZenChromeVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
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
            // The body fills the whole screen in zen mode; the auto-hidden
            // chrome floats over it when revealed by the mouse.
            ReaderTocPanel.Margin = new Thickness(0);
            ReaderTocCompactPanel.Margin = new Thickness(0);
            ReaderContentPanel.Margin = new Thickness(0);
            ReaderAssistantPanel.Margin = new Thickness(0);
            ReaderWebViewHost.Margin = new Thickness(0, 12, 0, 0);
            ReaderWebViewBottomCover.Margin = new Thickness(0, 0, 0, 0);
            ReaderHeaderRow.Height = new GridLength(0);
            ReaderHeaderBar.Visibility = Visibility.Collapsed;
            ReaderFooterRow.Height = new GridLength(0);
            ReaderFooterBar.Visibility = Visibility.Collapsed;
            ReaderTocToggleButton.Opacity = 1;
            ReaderAssistantToggleButton.Opacity = 1;
            UpdateReaderZenPopup(_readerZenChromeVisible);
            UpdateReaderZenTocToggle();
        }
        else
        {
            ReaderTocPanel.Margin = new Thickness(0, 38, 0, 0);
            ReaderTocCompactPanel.Margin = new Thickness(0, 38, 0, 0);
            ReaderContentPanel.Margin = new Thickness(0, 38, 0, 0);
            ReaderWebViewHost.Margin = new Thickness(0, 12, 0, 10);
            ReaderWebViewBottomCover.Margin = new Thickness(0, 0, 0, 10);
            ReaderHeaderRow.Height = new GridLength(52);
            ReaderHeaderBar.Visibility = Visibility.Visible;
            ReaderFooterRow.Height = new GridLength(50);
            ReaderFooterBar.Visibility = Visibility.Visible;
            _readerTocExpanded = _readerPreZenTocExpanded;
            _readerTocMinimal = _readerPreZenTocMinimal;
            _readerAssistantExpanded = _readerPreZenAssistantExpanded;
            ReaderTocToggleButton.Opacity = _readerTocExpanded ? 0.58 : 1;
            ReaderAssistantToggleButton.Opacity = _readerAssistantExpanded ? 0.58 : 1;
            UpdateReaderZenChrome(true);
            UpdateReaderZenTocToggle();
        }
        ApplyReaderPanelLayout();
    }

    // Zen mode auto-hides the top chrome (Kreader text, zen bar buttons and the
    // window caption buttons) so only the body remains; the minimal TOC rail on
    // the left is not part of this chrome and stays visible. Mouse movement
    // reveals it again, and it hides after ~2.5 s of inactivity.
    private void UpdateReaderZenChrome(bool visible)
    {
        _readerZenChromeVisible = visible;
        ReaderZenTitleTocButton.Visibility = _readerZenMode && visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReaderZenTitleExitButton.Visibility = _readerZenMode && visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReaderZenTocButton.Visibility = _readerZenMode && visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        // The Kreader brand text sits at the top-left and would float over the
        // minimal TOC rail in zen mode, so it stays hidden there even when the
        // chrome is revealed (only the right-side controls come back). It must
        // also stay hidden once the reader closes: ResetReaderChromeLayout()
        // re-runs this with visible=true while returning to the bookshelf, and
        // without the pane check the brand would float over the library title.
        ReaderBrandText.Visibility = !_readerZenMode
            && visible
            && ReaderPane.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        MinimizeWindowButton.Visibility = visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        MaximizeWindowButton.Visibility = visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        CloseWindowButton.Visibility = visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateReaderZenPopup(_readerZenMode && visible);

        if (visible)
            RestartReaderZenChromeHideTimer();
        else
            _readerZenChromeHideTimer?.Stop();
    }

    private void RestartReaderZenChromeHideTimer()
    {
        _readerZenChromeHideTimer ??= DispatcherQueue.CreateTimer();
        _readerZenChromeHideTimer.Interval = TimeSpan.FromMilliseconds(2500);
        _readerZenChromeHideTimer.IsRepeating = false;
        _readerZenChromeHideTimer.Tick -= ReaderZenChromeHideTimer_Tick;
        _readerZenChromeHideTimer.Tick += ReaderZenChromeHideTimer_Tick;
        _readerZenChromeHideTimer.Start();
    }

    private void ReaderZenChromeHideTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_readerZenMode) UpdateReaderZenChrome(false);
    }

    private void ResetReaderChromeLayout()
    {
        _readerZenMode = false;
        ApplyReaderZenFullScreen();
        ReaderTocPanel.Margin = new Thickness(0, 38, 0, 0);
        ReaderTocCompactPanel.Margin = new Thickness(0, 38, 0, 0);
        ReaderContentPanel.Margin = new Thickness(0, 38, 0, 0);
        ReaderWebViewHost.Margin = new Thickness(0, 12, 0, 10);
        ReaderWebViewBottomCover.Margin = new Thickness(0, 0, 0, 10);
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
        UpdateReaderZenChrome(true);
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
        else if (ReferenceEquals(sender, ReaderAnimationWaveItem))
            _readerPageAnimation = ReaderAnimationWave;
        else
            _readerPageAnimation = ReaderAnimationNone;
    }

    private void SyncReaderPageAnimationMenu()
    {
        ReaderAnimationNoneItem.IsChecked = _readerPageAnimation == ReaderAnimationNone;
        ReaderAnimationFadeItem.IsChecked = _readerPageAnimation == ReaderAnimationFade;
        ReaderAnimationSlideItem.IsChecked = _readerPageAnimation == ReaderAnimationSlide;
        ReaderAnimationWaveItem.IsChecked = _readerPageAnimation == ReaderAnimationWave;
    }

    // ------------------------------------------------------------------
    // Page turning shared by the keyboard arrows and pagination-mode click
    // zones. The footer arrows use direct chapter navigation instead.
    // ------------------------------------------------------------------

    private async Task<bool> TurnReaderPageAsync(int direction)
    {
        if (!IsReaderWindowForeground()) return false;
        if (ReaderPane.Visibility != Visibility.Visible) return false;
        if (ReaderActiveWebView.CoreWebView2 is null) return false;
        if (!_readerHasToc || _readerChapters.Count == 0) return false;
        if (_readerCloseRequested || _readerTransitionActive) return false;

        // Turn within the current chapter when content remains (pagination
        // columns or scroll direction). Crossing a chapter funnels through
        // ShowReaderChapterAsync so the selected transition plays there too.
        if (await TryTurnWithinChapterAsync(direction))
        {
            await UpdateReaderBookmarkIndicatorAsync();
            return true;
        }

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
        // Chapter wave transitions use their own dedicated reveal
        // (AnimateReaderChapterWaveInAsync); this host transform only plays
        // fade/slide. Keep the defensive mapping in case wave ever arrives.
        if (style == ReaderAnimationWave)
            style = ReaderAnimationFade;
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
    // Kindle-style wave ("水波流动") page-turn animation. For in-chapter turns
    // the target is the neighbouring column of the same document: the outgoing
    // page is snapshotted, the WebView is scrolled to the target while hidden
    // behind that snapshot, then the snapshot washes away from the incoming
    // side in a flowing wave (vertical strips with sine-modulated slide, inner
    // flow and ripple), revealing the real next page underneath. Chapter
    // switches replay the same overlay on the prepared new document
    // (AnimateReaderChapterWaveInAsync), so jumping chapters uses the exact
    // same effect. The overlay lives entirely inside the WebView
    // (ReaderWaveScripts), so no XAML overlay or host transform is needed; if
    // the snapshot or injection cannot run, the turn still happens instead of
    // dropping the input.
    // ------------------------------------------------------------------

    private async Task<bool> AnimateReaderPageWaveAsync(
        int direction,
        CancellationToken cancellationToken)
    {
        var core = ReaderActiveWebView.CoreWebView2;
        if (core is null || _readerCloseRequested) return false;
        cancellationToken.ThrowIfCancellationRequested();

        // Snapshot the outgoing page; the WebView itself becomes the next page
        // underneath once it has been scrolled while hidden behind the overlay.
        byte[]? png;
        try
        {
            png = await CaptureReaderPageSnapshotAsync(core, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            png = null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (png is null || png.Length == 0)
        {
            // The animation is decorative: if the snapshot is unavailable, turn
            // the page instantly instead of dropping the input.
            return await ExecuteReaderBooleanScriptAsync(
                ReaderPaginationScripts.CreateTurnScript(direction, smooth: false));
        }

        // The snapshot is embedded as a data URL in the injected script. Guard
        // the size: a capture that huge would make the injection slow enough to
        // defeat the animation, so turn the page instantly instead.
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(png);
        if (dataUrl.Length > 4_500_000)
        {
            return await ExecuteReaderBooleanScriptAsync(
                ReaderPaginationScripts.CreateTurnScript(direction, smooth: false));
        }

        var width = Math.Max(1, ReaderWebViewHost.ActualWidth);
        var height = Math.Max(1, ReaderWebViewHost.ActualHeight);
        var overlayScript = ReaderWaveScripts.CreateWaveOverlayScript(
            dataUrl,
            width,
            height,
            forward: direction > 0);
        if (!await ExecuteReaderBooleanScriptAsync(overlayScript))
        {
            return await ExecuteReaderBooleanScriptAsync(
                ReaderPaginationScripts.CreateTurnScript(direction, smooth: false));
        }

        // The overlay now covers the viewport with the captured page; the jump
        // to the next column happens invisibly underneath.
        var turned = await ExecuteReaderBooleanScriptAsync(
            ReaderPaginationScripts.CreateTurnScript(direction, smooth: false));
        if (!turned || _readerCloseRequested)
        {
            await TryRemoveReaderWaveOverlayAsync();
            return false;
        }

        try
        {
            // Slightly longer than the last strip's keyframe so the wave fully
            // settles before the overlay is torn down.
            await Task.Delay(ReaderWaveScripts.TotalDurationMs + 100, cancellationToken);
            if (_readerCloseRequested) throw new OperationCanceledException();
        }
        catch (OperationCanceledException)
        {
            await TryRemoveReaderWaveOverlayAsync();
            throw;
        }
        catch
        {
            await TryRemoveReaderWaveOverlayAsync();
            throw;
        }

        await TryRemoveReaderWaveOverlayAsync();
        return true;
    }

    // Reveals a prepared new chapter with the same Kindle-style wave: the
    // captured outgoing page is injected as the overlay on the new document,
    // the host is shown underneath, and the wave strips wash the old page away
    // to reveal the new chapter. Falls back to the fade reveal when the
    // snapshot or injection is unavailable so the chapter is never left hidden.
    private async Task AnimateReaderChapterWaveInAsync(
        int direction,
        byte[]? snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            var core = ReaderActiveWebView.CoreWebView2;
            if (core is null || _readerCloseRequested || snapshot is null || snapshot.Length == 0)
            {
                await AnimateReaderPageTurnAsync(direction, isOut: false, ReaderAnimationFade, cancellationToken);
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();

            // The snapshot is embedded as a data URL in the injected script.
            // Guard the size: an oversized capture would make the injection slow
            // enough to defeat the animation, so fall back to the fade reveal.
            var dataUrl = "data:image/png;base64," + Convert.ToBase64String(snapshot);
            if (dataUrl.Length > 4_500_000)
            {
                await AnimateReaderPageTurnAsync(direction, isOut: false, ReaderAnimationFade, cancellationToken);
                return;
            }

            var width = Math.Max(1, ReaderWebViewHost.ActualWidth);
            var height = Math.Max(1, ReaderWebViewHost.ActualHeight);
            var overlayScript = ReaderWaveScripts.CreateWaveOverlayScript(
                dataUrl,
                width,
                height,
                forward: direction > 0);
            if (!await ExecuteReaderBooleanScriptAsync(overlayScript))
            {
                await AnimateReaderPageTurnAsync(direction, isOut: false, ReaderAnimationFade, cancellationToken);
                return;
            }

            // The prepared new chapter now sits underneath the captured old
            // page; reveal it and let the wave wash the old page away.
            ReaderWebViewHost.Opacity = 1;
            await Task.Delay(ReaderWaveScripts.TotalDurationMs + 100, cancellationToken);
            if (_readerCloseRequested) throw new OperationCanceledException();
            await TryRemoveReaderWaveOverlayAsync();
        }
        catch (OperationCanceledException)
        {
            await TryRemoveReaderWaveOverlayAsync();
            throw;
        }
        catch
        {
            // The wave is decorative; never leave the new chapter hidden.
            await TryRemoveReaderWaveOverlayAsync();
            await AnimateReaderPageTurnAsync(direction, isOut: false, ReaderAnimationFade, cancellationToken);
        }
    }

    private async Task TryRemoveReaderWaveOverlayAsync()
    {
        try
        {
            await ExecuteReaderBooleanScriptAsync(ReaderWaveScripts.CreateWaveCleanupScript());
        }
        catch
        {
            // A closing WebView may already be gone; the next navigation clears
            // any leftover overlay anyway.
        }
    }

    private static async Task<byte[]?> CaptureReaderPageSnapshotAsync(
        CoreWebView2 core,
        CancellationToken cancellationToken)
    {
        // Page.captureScreenshot is the host-side DevTools Protocol API; it
        // works even with the reader's IsScriptEnabled=false and returns the
        // exact visible viewport as base64 PNG. A short timeout keeps a slow
        // capture from blocking a page turn.
        var capture = core.CallDevToolsProtocolMethodAsync(
            "Page.captureScreenshot",
            """{"format":"png","fromSurface":true,"captureBeyondViewport":false}""")
            .AsTask();
        var completed = await Task.WhenAny(capture, Task.Delay(1500, cancellationToken));
        if (completed != capture)
        {
            // Observe the abandoned capture so a late failure cannot surface as
            // an unobserved task exception.
            _ = capture.ContinueWith(static _ => { }, TaskScheduler.Default);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        var json = await capture;
        var data = ExtractReaderScreenshotData(json);
        return data is null ? null : Convert.FromBase64String(data);
    }

    private static string? ExtractReaderScreenshotData(string json)
    {
        const string key = "\"data\":\"";
        var start = json.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return null;
        start += key.Length;
        var end = json.IndexOf('"', start);
        if (end < 0) return null;
        return json.Substring(start, end - start);
    }

    // ------------------------------------------------------------------
    // Keyboard reading navigation. Paginated single/double-page modes use
    // left/right for pages. Continuous scroll mode uses left/right for chapters
    // and up/down only for scrolling, so reaching a scroll edge never changes
    // chapter unexpectedly.
    // ------------------------------------------------------------------

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ReaderPane.Visibility != Visibility.Visible) return;
        if (!IsReaderWindowForeground()) return;

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
                if (IsPdfReader) ShowReaderSearchPanel();
                else ShowReaderInPageSearch();
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

        if (TryHandleReaderArrowKey(e.Key)) e.Handled = true;
    }

    private bool TryHandleReaderArrowKey(Windows.System.VirtualKey key)
    {
        if (_readerCloseRequested || _readerTransitionActive) return false;
        if (_readerFlowMode == 1)
        {
            var pageDirection = key switch
            {
                Windows.System.VirtualKey.Left => -1,
                Windows.System.VirtualKey.Right => 1,
                _ => 0
            };
            if (pageDirection == 0) return false;
            _ = TurnReaderPageAsync(pageDirection);
            return true;
        }

        var chapterDirection = key switch
        {
            Windows.System.VirtualKey.Left => -1,
            Windows.System.VirtualKey.Right => 1,
            _ => 0
        };
        if (chapterDirection != 0)
        {
            _ = NavigateReaderChapterAsync(chapterDirection);
            return true;
        }

        var scrollDirection = key switch
        {
            Windows.System.VirtualKey.Up => -1,
            Windows.System.VirtualKey.Down => 1,
            _ => 0
        };
        if (scrollDirection == 0) return false;
        _ = ScrollReaderWithKeyboardAsync(scrollDirection);
        return true;
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
        if (!IsReaderWindowForeground()) return;
        if (_readerCloseRequested || _readerTransitionActive) return;
        if (ReaderActiveWebView.CoreWebView2 is null || _readerAllowedRoot is null) return;

        // Up/down are scroll-only in continuous mode. At a chapter edge they
        // simply stop; left/right own chapter navigation explicitly.
        await ExecuteReaderBooleanScriptAsync(
            CreateReaderKeyboardScrollScript(direction, _readerLayout.VerticalWriting));
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
        if (_readerAllowedRoot is null || ReaderActiveWebView.CoreWebView2 is null) return false;
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
            if (style == ReaderAnimationWave)
            {
                // Kindle-style flowing wave rendered inside the WebView. If the
                // capture or injection fails, the wave method still turns the
                // page instantly so input is never dropped.
                return await AnimateReaderPageWaveAsync(direction, token);
            }
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
        if (ReaderActiveWebView.CoreWebView2 is null) return false;
        try { return await ReaderActiveWebView.CoreWebView2.ExecuteScriptAsync(script) == "true"; }
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
        if (ReaderActiveWebView.CoreWebView2 is null) return string.Empty;
        try
        {
            var json = await ReaderActiveWebView.CoreWebView2.ExecuteScriptAsync(script);
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
        _readerInitialRevealPending = false;
        _readerRelayoutCancellation?.Cancel();
            _readerRelayoutCancellation?.Dispose();
            _readerRelayoutCancellation = null;
            ResetReaderWebViewTransform();
            _ = TryRemoveReaderWaveOverlayAsync();
            ResetReaderPreloadState();
            try
            {
                if (ReaderPreloadWebView.CoreWebView2 is not null)
                    ReaderPreloadWebView.CoreWebView2.Navigate("about:blank");
            }
            catch { }

        // 2) Close every reader Popup. WebView2 renders as an HWND composition
        //    island, so popups are the only surfaces that can float above it and
        //    must be closed explicitly.
        UpdateReaderAssistantPopup(false);
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
        if (ReaderActiveWebView.CoreWebView2 is not null)
        {
            try { ReaderActiveWebView.CoreWebView2.Navigate("about:blank"); }
            catch { }
        }
    }

    private void ReaderWebView_NavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        => HandleReaderNavigationStarting(sender, args);

    private void ReaderPreloadWebView_NavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        => HandleReaderNavigationStarting(sender, args);

    private void HandleReaderNavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
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

        // A preload navigation prepares the hidden surface only; it must never
        // change the visible chapter index, TOC selection or search/footnote
        // state of the surface the user is currently reading. Which control is
        // the preload surface changes after every swap, so this is decided by
        // control identity, not by which XAML event handler fired.
        var isPreload = ReferenceEquals(sender, _readerPreloadControl);
        if (isPreload) return;

        ResetReaderInPageSearchForNavigation();
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
        => await HandleReaderNavigationCompletedAsync(sender, args);

    private async void ReaderPreloadWebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        => await HandleReaderNavigationCompletedAsync(sender, args);

    private async Task HandleReaderNavigationCompletedAsync(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_readerCloseRequested)
        {
            // The reader is closing: never run post-navigation work and never
            // let a stale animation touch the (now hidden) reader surface.
            _readerTransitionActive = false;
            return;
        }
        // A completion on the hidden preload surface finishes the background
        // preparation. The visible reader flow must not run for it, and the
        // preload document must never touch the visible chapter's state.
        if (ReferenceEquals(sender, _readerPreloadControl)
            && _readerPreloadTarget is { } preloadTarget
            && _readerPendingNavigationTarget is null
            && sender.Source is { } preloadSource
            && preloadSource.AbsoluteUri.Equals(preloadTarget.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
        {
            _readerPreloadInProgress = false;
            if (!args.IsSuccess)
            {
                _readerPreloadReady = false;
                return;
            }

            try
            {
                // Prepare the hidden document exactly like the visible first
                // screen (reader styling + plain chapter start), so the swap
                // below is instant. Edge metrics are intentionally NOT applied
                // to the visible reader.
                await ApplyReaderAppearanceAsync(includeChapterStart: true, webView: sender);
                if (ReferenceEquals(sender, _readerPreloadControl)
                    && ReaderPreloadTargetsEqual(_readerPreloadTarget, preloadTarget))
                {
                    _readerPreloadReady = true;
                }
            }
            catch
            {
                _readerPreloadReady = false;
            }
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
                || ReaderActiveWebView.Source is not { } source
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
                // is what releases the transition guard. The surface is now
                // hidden up front (see NavigateReaderSourceAsync), so a
                // genuinely-failed load must restore visibility itself — but
                // only if no newer navigation was requested in the meantime
                // (then that navigation's own completion owns the reveal).
                var failedTarget = pendingTarget;
                _ = Task.Delay(1200).ContinueWith(
                    _ => DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_readerCloseRequested) return;
                        if (ReferenceEquals(_readerPendingNavigationTarget, failedTarget))
                            ResetReaderWebViewTransform();
                    }),
                    TaskScheduler.Default);
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
            var initialReveal = _readerInitialRevealPending;
            // A plain chapter start (TOC entry without an explicit anchor,
            // progress-slider jump, or open/prev/next with no breakpoint
            // restore) can be fully prepared in the SAME script pass as the
            // appearance styling: normalize the opening, inject the reader
            // CSS, scroll to the first line and snap the pagination columns in
            // one IPC round trip, and read back the scroll-edge metrics so the
            // post-reveal poll is primed without another DOM read.
            var intent = _readerNavigationIntent;
            var plainChapterStart = ReaderNavigationLocationPolicy.ShouldNormalizeChapterStart(
                intent,
                pendingTarget,
                _pendingReaderRestorePosition is not null);
            if (turnIn is not null || initialReveal) ReaderWebViewHost.Opacity = 0;
            var appearanceMetrics = await ApplyReaderAppearanceAsync(
                includeChapterStart: plainChapterStart,
                webView: ReaderActiveWebView);
            if (IsStaleReaderNavigation(sequence, token)) return;
            if (initialReveal)
            {
                await WaitForReaderFontsAsync(token);
                if (IsStaleReaderNavigation(sequence, token)) return;
            }
            if (plainChapterStart)
            {
                // The chapter start was already applied inside the appearance
                // pass; only the edge metrics still need to land. If the script
                // could not report them (fixed-layout page), fall back to the
                // dedicated read so the scroll poll never misfires.
                if (!ApplyReaderEdgeMetrics(appearanceMetrics))
                    await PrimeReaderScrollEdgesAsync();
                if (IsStaleReaderNavigation(sequence, token)) return;
            }
            else
            {
                // The first screen is positioned according to WHY this
                // navigation was requested. An explicit user target (fragment
                // anchor, search/bookmark/annotation/AI location) always wins;
                // automatic breakpoint restore only runs for the open-book
                // flow (intent None). Stale pending locations from a superseded
                // navigation were already pruned when this navigation started,
                // so a TOC jump can never inherit the old chapter's offset.
                await ApplyReaderNavigationLocationAsync(intent, pendingTarget);
                if (IsStaleReaderNavigation(sequence, token)) return;
                await PrimeReaderScrollEdgesAsync();
                if (IsStaleReaderNavigation(sequence, token)) return;
            }
            if (_readerNavigateToEnd)
            {
                await MoveReaderToEndAsync();
                // Only clear the intent if this navigation is still current; a
                // newer navigation may have re-armed it for its own chapter.
                if (!IsStaleReaderNavigation(sequence, token))
                    _readerNavigateToEnd = false;
            }
            // The new first screen is ready: short fade/slide reveal (or show
            // immediately in 无动画 mode), then let the deferred work run.
            _readerInitialRevealPending = false;
            if (turnIn is { } pending)
            {
                if (pending.Style == ReaderAnimationWave)
                    await AnimateReaderChapterWaveInAsync(pending.Direction, pending.Snapshot, token);
                else
                    await AnimateReaderPageTurnAsync(pending.Direction, isOut: false, pending.Style, token);
            }
            else
            {
                ResetReaderWebViewTransform();
            }
            _readerTransitionActive = false;
            _readerPendingNavigationTarget = null;

            _ = RunReaderPostNavigationWorkAsync(sequence, token);
            ScheduleReaderPreloadAsync();
        }
        catch
        {
            // Post-navigation styling is best-effort; a failure here must never
            // leave the reader or the window in a broken state.
            _readerPendingTurnInAnimation = null;
            _readerPendingNavigationTarget = null;
            _readerInitialRevealPending = false;
            ResetReaderWebViewTransform();
            _readerTransitionActive = false;
        }
    }

    // Arms the hidden surface to load and style the next chapter in the
    // background. Called after every successful reveal (and after a preload
    // swap), so forward navigation almost always finds the target ready.
    private void ScheduleReaderPreloadAsync()
    {
        if (IsPdfReader || _readerCloseRequested) return;
        if (_readerChapters.Count == 0 || _readerChapterIndex < 0) return;
        var nextIndex = _readerChapterIndex + 1;
        if (nextIndex >= _readerChapters.Count)
        {
            _readerPreloadChapterIndex = -1;
            _readerPreloadTarget = null;
            _readerPreloadControl = null;
            _readerPreloadReady = false;
            return;
        }

        var target = new Uri(_readerChapters[nextIndex]);
        if (ReaderPreloadTargetsEqual(_readerPreloadTarget, target)
            && (_readerPreloadReady || _readerPreloadInProgress)) return;

        _readerPreloadChapterIndex = nextIndex;
        _readerPreloadTarget = target;
        _readerPreloadControl = _readerShowingPreload ? ReaderWebView : ReaderPreloadWebView;
        _readerPreloadReady = false;
        _ = PreloadReaderChapterAsync(target);
    }

    private async Task PreloadReaderChapterAsync(Uri target)
    {
        if (_readerPreloadInProgress) return;
        _readerPreloadInProgress = true;
        try
        {
            await EnsureReaderPreloadWebViewReadyAsync();
            if (_readerCloseRequested) return;
            var control = _readerPreloadControl;
            if (control is null || control.CoreWebView2 is null) return;
            if (control.Source is { } current
                && current.AbsoluteUri.Equals(target.AbsoluteUri, StringComparison.OrdinalIgnoreCase)
                && _readerPreloadReady)
            {
                return;
            }
            control.CoreWebView2.Navigate(target.AbsoluteUri);
        }
        catch
        {
            _readerPreloadReady = false;
            _readerPreloadInProgress = false;
        }
    }

    // A forward chapter switch can skip the WebView navigation entirely when
    // the hidden surface already finished loading and styling the target: flip
    // which surface is visible, reveal the prepared document and immediately
    // start preloading the next-next chapter on the surface that just went
    // hidden. Falls back to the normal navigation path when the preload is not
    // ready (first chapter, rapid double-next, settings changed mid-flight).
    private async Task<bool> TrySwapToPreloadedReaderChapterAsync(
        int direction,
        bool animate,
        ReaderNavigationIntent intent)
    {
        if (IsPdfReader || _readerCloseRequested || ReaderPane.Visibility != Visibility.Visible) return false;
        if (_readerNavigateToEnd) return false;
        if (_readerChapterIndex != _readerPreloadChapterIndex || !_readerPreloadReady) return false;
        var target = new Uri(_readerChapters[_readerChapterIndex]);
        if (!ReaderPreloadTargetsEqual(_readerPreloadTarget, target)) return false;
        var preloadControl = _readerPreloadControl;
        if (preloadControl is null || preloadControl.CoreWebView2 is null) return false;

        PruneReaderPendingLocations(intent);
        _readerNavigationIntent = intent;
        _readerActiveLocationTarget = target;

        _readerChapterTransitionCancellation?.Cancel();
        _readerChapterTransitionCancellation?.Dispose();
        _readerChapterTransitionCancellation = new CancellationTokenSource();
        var token = _readerChapterTransitionCancellation.Token;
        var sequence = ++_readerChapterTransitionSequence;

        // Chapter switches use the same animation as in-chapter page turns; the
        // wave snapshots the outgoing page before the surface swap below.
        var chapterStyle = _readerPageAnimation;
        var shouldAnimate = animate
            && chapterStyle > ReaderAnimationNone
            && !_readerCloseRequested
            && ReaderPane.Visibility == Visibility.Visible;

        byte[]? waveSnapshot = null;
        if (shouldAnimate && chapterStyle == ReaderAnimationWave)
        {
            try
            {
                waveSnapshot = await CaptureReaderPageSnapshotAsync(
                    ReaderActiveWebView.CoreWebView2,
                    token);
            }
            catch (OperationCanceledException)
            {
                // A newer navigation or a reader close superseded this one;
                // that flow owns the reader state now.
                return true;
            }
            catch
            {
                waveSnapshot = null;
            }
            if (token.IsCancellationRequested) return true;
        }

        _readerTransitionActive = true;
        _readerPendingTurnInAnimation = shouldAnimate
            ? new ReaderTurnInAnimation(direction, chapterStyle, waveSnapshot)
            : null;
        _readerPendingNavigationTarget = null;
        _readerInitialRevealPending = false;
        // The visible document changed without a WebView navigation on the
        // active surface, so clear per-chapter transient state explicitly.
        ResetReaderInPageSearchForNavigation();
        ClearReaderFootnotePage();

        var previousVisible = ReaderActiveWebView;
        _readerShowingPreload = !_readerShowingPreload;
        var nextVisible = ReaderActiveWebView;
        SetReaderWebViewLayer(nextVisible, visible: true);
        SetReaderWebViewLayer(previousVisible, visible: false);
        _readerPreloadReady = false;
        _readerPreloadChapterIndex = -1;
        _readerPreloadTarget = null;
        _readerPreloadControl = null;

        ReaderWebViewHost.Opacity = 0;
        try
        {
            if (_readerPendingTurnInAnimation is { } pending)
            {
                if (pending.Style == ReaderAnimationWave)
                    await AnimateReaderChapterWaveInAsync(pending.Direction, pending.Snapshot, token);
                else
                    await AnimateReaderPageTurnAsync(pending.Direction, isOut: false, pending.Style, token);
            }
            else
                ResetReaderWebViewTransform();
            _readerTransitionActive = false;
        }
        catch
        {
            ResetReaderWebViewTransform();
            _readerTransitionActive = false;
            throw;
        }

        await PrimeReaderScrollEdgesAsync();
        _ = RunReaderPostNavigationWorkAsync(sequence, token);
        ScheduleReaderPreloadAsync();
        return true;
    }

    private static bool ReaderPreloadTargetsEqual(Uri? left, Uri? right) =>
        left is not null
        && right is not null
        && left.AbsoluteUri.Equals(right.AbsoluteUri, StringComparison.OrdinalIgnoreCase);

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
            // The corner bookmark marker is a UI indicator, not part of the
            // first screen: update it after the reveal so the pre-reveal path
            // does not pay an extra DOM read.
            await UpdateReaderBookmarkIndicatorAsync();
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

    private async Task<string?> ApplyReaderAppearanceAsync(
        bool includeChapterStart = false,
        WebView2? webView = null)
    {
        var view = webView ?? ReaderActiveWebView;
        if (_readerAllowedRoot is null || view.CoreWebView2 is null) return null;
        const string background = "#FFFFFF";
        const string foreground = "#111111";
        const string link = "#222222";
        var fontPercent = (int)Math.Round(_readerLayout.FontScale * 100);
        var hostViewportWidth = ReaderWebViewHost.ActualWidth.ToString(
            "0.###",
            System.Globalization.CultureInfo.InvariantCulture);
        // Vertical writing is supported in both continuous and paginated flow.
        // The pagination CSS keeps the viewport horizontal while Chromium lays
        // out vertical-rl columns from right to left.
        var vertical = _readerLayout.VerticalWriting;
        var flowCss = ReaderPaginationScripts.CreateFlowCss(
            pagination: _readerFlowMode == 1,
            vertical: vertical,
            twoPage: _readerLayout.TwoPageMode,
            horizontalPadding: _readerLayout.BodyPadding,
            maxContentWidth: _readerLayout.MaxWidth);
        var lineHeight = _readerLayout.LineHeight.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var bodyPadding = (int)_readerLayout.BodyPadding;
        var bodyLayoutCss = vertical
            ? $"max-width: none !important; writing-mode: vertical-rl !important; text-orientation: mixed;"
              + $" margin: 0 auto !important; padding: {bodyPadding}px !important;"
            : $"width: 100%; max-width: calc({(int)_readerLayout.MaxWidth}px + {bodyPadding * 2}px); margin: 0 auto !important;"
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
        view.DefaultBackgroundColor = Colors.White;
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
                ::selection, body *::selection {
                  background: #000000 !important;
                  background-color: #000000 !important;
                  color: #FFFFFF !important;
                  -webkit-text-fill-color: #FFFFFF !important;
                }
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
              // visualViewport is the actual visible WebView width. During a
              // multicolumn reflow, documentElement.getBoundingClientRect()
              // can transiently describe the laid-out content width and make
              // the right column extend underneath the assistant panel.
              const hostViewportWidth = {{hostViewportWidth}};
              // Chromium lays the multicolumns out in CSS pixels
              // (100vw = window.innerWidth), which at non-100% DPI scaling is
              // WIDER than the DIP-based XAML host width. The page step and
              // the actual column pitch must use the SAME unit or every page
              // drifts and the right boundary gets clipped. The WebView's own
              // viewport is therefore the source of truth; the DIP host width
              // is only a fallback while the document is not measurable.
              const inPageWidth = window.visualViewport?.width || window.innerWidth || 0;
              const viewportWidth = inPageWidth > 0
                ? inPageWidth
                : (hostViewportWidth > 0
                  ? hostViewportWidth
                  : (root?.clientWidth || kkScroller?.clientWidth || 0));
              if (root && viewportWidth > 0) {
                root.style.setProperty('{{ReaderPaginationScripts.ViewportWidthVariable}}', viewportWidth + 'px');
              }
              window.__kkindleReaderTwoPage = {{(_readerLayout.TwoPageMode ? "true" : "false")}};
              {{ReaderPaginationScripts.PageAlignmentHelperDefinition}}
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
                // Explicitly start loading the computed reader font before the
                // hidden first document is revealed. The host polls the font
                // set status; no page callback or script event is required.
                try {
                  if (document.fonts)
                    void document.fonts.load('1rem ' + kkStyle.fontFamily);
                } catch (_) {}
              }
            })();
            """;
        // One combined first-screen pass: for a plain chapter start the
        // opening normalization runs BEFORE the style injection (the raw DOM
        // is cheap to mutate, and the multicolumn reflow then happens exactly
        // once against the already-normalized document), then the cover fit +
        // pagination snap finish the layout. The script reports the scroll-edge
        // metrics so the host does not need another DOM read before reveal.
        var combinedScript = new System.Text.StringBuilder();
        if (includeChapterStart)
            combinedScript.Append(ReaderNavigationScripts.NormalizeChapterStart).Append('\n');
        combinedScript.Append(script);
        if (includeChapterStart)
            combinedScript.Append("\nwindow.scrollTo({ left: 0, top: 0, behavior: 'instant' });\n");
        if (_readerFlowMode == 1)
        {
            combinedScript.Append(GetReaderCoverFitScript()).Append('\n');
            combinedScript.Append(ReaderPaginationScripts.Snap).Append('\n');
        }
        if (includeChapterStart)
        {
            combinedScript.Append(
                "JSON.stringify((function(){var el=document.scrollingElement||document.documentElement;")
                .Append("return {st:el.scrollTop||0,sl:el.scrollLeft||0,sh:el.scrollHeight||0,sw:el.scrollWidth||0,")
                .Append("ch:el.clientHeight||window.innerHeight||0,cw:el.clientWidth||window.innerWidth||0};})());");
        }

        try
        {
            var result = await view.CoreWebView2.ExecuteScriptAsync(combinedScript.ToString());
            return includeChapterStart ? result : null;
        }
        catch
        {
            // Some fixed-layout EPUB pages don't expose a normal document head.
            return null;
        }
    }

    // Settings/layout changes must restyle the visible document immediately and
    // keep the hidden preload document in sync so a later chapter swap never
    // reveals stale typography or pagination.
    private async Task ApplyReaderAppearanceToVisibleAndPreloadAsync()
    {
        await ApplyReaderAppearanceAsync(webView: ReaderActiveWebView);
        var hidden = _readerShowingPreload ? ReaderWebView : ReaderPreloadWebView;
        if (hidden.CoreWebView2 is not null
            && hidden.Source is { IsFile: true }
            && !ReferenceEquals(hidden, ReaderActiveWebView))
        {
            await ApplyReaderAppearanceAsync(webView: hidden);
        }
    }

    private async Task WaitForReaderFontsAsync(CancellationToken cancellationToken)
    {
        if (ReaderActiveWebView.CoreWebView2 is null) return;
        // ExecuteScriptAsync does not await promises when page scripts are
        // disabled, so poll the synchronous FontFaceSet status with a short,
        // bounded wait. The body measurement in ApplyReaderAppearanceAsync has
        // already triggered loading of the newly injected @font-face rules.
        for (var attempt = 0; attempt < 24; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var status = await ReaderActiveWebView.CoreWebView2.ExecuteScriptAsync(
                    "document.fonts ? document.fonts.status : 'loaded';");
                if (status.Trim().Trim('"').Equals("loaded", StringComparison.OrdinalIgnoreCase))
                    return;
            }
            catch
            {
                return;
            }
            await Task.Delay(25, cancellationToken);
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
        if (_readerAllowedRoot is null || ReaderActiveWebView.CoreWebView2 is null) return;
        if (_readerFlowMode != 1) return;
        try { await ReaderActiveWebView.CoreWebView2.ExecuteScriptAsync(GetReaderCoverFitScript()); }
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
        if (_readerAllowedRoot is null || ReaderActiveWebView.CoreWebView2 is null) return;
        try
        {
            await Task.Delay(250);
            if (sequence != _readerChapterTransitionSequence || _readerCloseRequested) return;
            if (ReaderActiveWebView.CoreWebView2 is null || ReaderActiveWebView.Source is not { IsFile: true }) return;
            await FitReaderImagesAsync();
            if (sequence != _readerChapterTransitionSequence || _readerCloseRequested) return;
            if (_readerFlowMode == 1) await RealignReaderAfterRelayoutAsync();
            await Task.Delay(700);
            if (sequence != _readerChapterTransitionSequence || _readerCloseRequested) return;
            if (ReaderActiveWebView.CoreWebView2 is null || ReaderActiveWebView.Source is not { IsFile: true }) return;
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
        if (ReaderActiveWebView.CoreWebView2 is null || _readerAllowedRoot is null) return;
        if (_readerFlowMode != 1) return;
        try
        {
            await ReaderActiveWebView.CoreWebView2.ExecuteScriptAsync(ReaderPaginationScripts.Snap);
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
        if (intent != ReaderNavigationIntent.Search)
        {
            _pendingReaderSearchQuery = null;
            _pendingReaderSearchContext = null;
        }
        if (!ReaderNavigationLocationPolicy.KeepsBookmarkQuote(intent))
        {
            _pendingReaderBookmarkQuote = null;
            _pendingReaderBookmarkPosition = null;
            _pendingReaderBookmarkFlowMode = 0;
        }
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
        if (ReaderActiveWebView.CoreWebView2 is null) return;
        try
        {
            var script = ReaderNavigationScripts.NormalizeChapterStart
                + "\nwindow.scrollTo({ left: 0, top: 0, behavior: 'instant' });";
            if (_readerFlowMode == 1)
                script += "\n" + ReaderPaginationScripts.Snap;
            await ReaderActiveWebView.CoreWebView2.ExecuteScriptAsync(
                script);
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
        if (ReaderActiveWebView.CoreWebView2 is null || string.IsNullOrWhiteSpace(fragment)) return;
        var needle = Uri.UnescapeDataString(fragment).Replace("\\", "\\\\").Replace("'", "\\'");
        var flowMode = _readerFlowMode;
        var vertical = _readerLayout.VerticalWriting;
        string result;
        try
        {
            result = await ReaderActiveWebView.CoreWebView2.ExecuteScriptAsync(
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
        if (ReaderActiveWebView.CoreWebView2 is null) return;
        var script = _readerFlowMode switch
        {
            0 when _readerLayout.VerticalWriting =>
                "window.scrollTo({ left: document.scrollingElement.scrollWidth, top: 0, behavior: 'instant' });",
            0 => "window.scrollTo({ top: document.scrollingElement.scrollHeight, behavior: 'instant' });",
            _ => "window.scrollTo({ left: document.scrollingElement.scrollWidth, top: 0, behavior: 'instant' });"
        };
        try { await ReaderActiveWebView.CoreWebView2.ExecuteScriptAsync(script); }
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
            ToggleSidebarSection(SystemSectionButton, SystemChildren, SystemChevron, "系统设置");
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
            ? Colors.White
            : isHovered
                ? Windows.UI.Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2)
                : Colors.White;
        var targetForeground = Colors.Black;
        var targetBorder = isActive || isHovered
            ? Colors.Black
            : Windows.UI.Color.FromArgb(0xFF, 0xBF, 0xBF, 0xBF);
        var currentBackground = (sectionButton.Background as SolidColorBrush)?.Color ?? targetBackground;
        var currentForeground = (sectionButton.Foreground as SolidColorBrush)?.Color ?? targetForeground;
        var currentBorder = (sectionButton.BorderBrush as SolidColorBrush)?.Color ?? targetBorder;
        var backgroundBrush = new SolidColorBrush(currentBackground);
        var foregroundBrush = new SolidColorBrush(currentForeground);
        var borderBrush = new SolidColorBrush(currentBorder);
        sectionButton.Background = backgroundBrush;
        sectionButton.Foreground = foregroundBrush;
        sectionButton.BorderBrush = borderBrush;
        chevron.Foreground = foregroundBrush;

        if (!animate)
        {
            backgroundBrush.Color = targetBackground;
            foregroundBrush.Color = targetForeground;
            borderBrush.Color = targetBorder;
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
        var borderAnimation = new ColorAnimation
        {
            From = currentBorder,
            To = targetBorder,
            Duration = duration
        };
        Storyboard.SetTarget(backgroundAnimation, backgroundBrush);
        Storyboard.SetTargetProperty(backgroundAnimation, "Color");
        Storyboard.SetTarget(foregroundAnimation, foregroundBrush);
        Storyboard.SetTargetProperty(foregroundAnimation, "Color");
        Storyboard.SetTarget(borderAnimation, borderBrush);
        Storyboard.SetTargetProperty(borderAnimation, "Color");
        var storyboard = new Storyboard();
        storyboard.Children.Add(backgroundAnimation);
        storyboard.Children.Add(foregroundAnimation);
        storyboard.Children.Add(borderAnimation);
        backgroundBrush.Color = targetBackground;
        foregroundBrush.Color = targetForeground;
        borderBrush.Color = targetBorder;
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
            || activeButton == ZLibraryAccountNavigationButton || activeButton == ReaderAiSettingsNavigationButton)
        {
            _activeNavigationSectionButton = SystemSectionButton;
            ExpandSidebarSection(SystemSectionButton, SystemChildren, SystemChevron, "系统设置");
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
        QueueScrollbarAutoHideRefresh();
        QueueInteractiveControlToolTipRefresh();
    }

    private async void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button == AllBooksButton)
        {
            await ShowAllBooksAsync();
            return;
        }
        if (sender is Button)
            await ShowMessageAsync("Kkindle", "首版当前聚焦书架与 Kindle 同步。");
    }

    private void DeviceNameButton_Click(object sender, RoutedEventArgs e)
        => ShowDeviceModelPicker(DeviceNameButton);

    private void ShowDeviceModelPicker(FrameworkElement anchor)
    {
        var device = _devices.FirstOrDefault();
        if (device is null) return;

        // Dismiss any hover tooltip on the anchor before the picker opens so it
        // does not linger underneath the flyout; restore it once the picker
        // closes so hovering still shows the hint again.
        var anchorToolTip = ToolTipService.GetToolTip(anchor);
        ToolTipService.SetToolTip(anchor, null);

        var flyout = new MenuFlyout();
        var defaultItem = new MenuFlyoutItem
        {
            Text = "默认名称（设备自带）"
        };
        defaultItem.Click += (_, _) => _ = ApplyDeviceModelAsync(null);
        flyout.Items.Add(defaultItem);
        flyout.Items.Add(new MenuFlyoutSeparator());

        foreach (var vendor in DeviceModelCatalog.Vendors)
        {
            var submenu = new MenuFlyoutSubItem { Text = vendor.Name };
            foreach (var model in vendor.Models)
            {
                var item = new MenuFlyoutItem { Text = model };
                item.Click += (_, _) => _ = ApplyDeviceModelAsync(model);
                submenu.Items.Add(item);
            }
            flyout.Items.Add(submenu);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        var customItem = new MenuFlyoutItem { Text = "自定义型号…" };
        customItem.Click += (_, _) => ShowDeviceModelInput();
        flyout.Items.Add(customItem);

        flyout.Closed += (_, _) =>
        {
            if (anchorToolTip is not null)
                ToolTipService.SetToolTip(anchor, anchorToolTip);
        };
        flyout.ShowAt(anchor, new FlyoutShowOptions
        {
            Placement = FlyoutPlacementMode.TopEdgeAlignedLeft
        });
    }

    private async Task ApplyDeviceModelAsync(string? model)
    {
        var device = _devices.FirstOrDefault();
        if (device is null) return;
        var normalized = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        try
        {
            if (normalized is null)
                await _deviceModelStore.DeleteModelAsync(device.Identity);
            else
                await _deviceModelStore.SetModelAsync(device.Identity, normalized);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法保存设备型号", exception.Message);
            return;
        }

        _deviceDisplayName = normalized ?? device.Name;
        DeviceNameText.Text = $"{_deviceDisplayName} · {device.ConnectionLabel}";
        KindleStatusText.Text = _deviceDisplayName;
        DeviceResourceDeviceText.Text = $"{_deviceDisplayName} · {device.ConnectionLabel}";
    }

    private void ShowDeviceModelInput()
    {
        DeviceModelInputTextBox.Text = _deviceDisplayName ?? string.Empty;
        DeviceModelInputOverlay.Visibility = Visibility.Visible;
        DeviceModelInputOverlay.Focus(FocusState.Programmatic);
        DeviceModelInputTextBox.Focus(FocusState.Programmatic);
        DeviceModelInputTextBox.SelectAll();
    }

    private async void DeviceModelInputOkButton_Click(object sender, RoutedEventArgs e)
    {
        var model = DeviceModelInputTextBox.Text?.Trim() ?? string.Empty;
        if (model.Length == 0)
        {
            await ShowMessageAsync("型号不能为空", "请输入设备型号，或选择“默认名称”。");
            return;
        }
        DeviceModelInputOverlay.Visibility = Visibility.Collapsed;
        await ApplyDeviceModelAsync(model);
    }

    private void DeviceModelInputCancelButton_Click(object sender, RoutedEventArgs e)
        => DeviceModelInputOverlay.Visibility = Visibility.Collapsed;

    private void DeviceModelInputOverlay_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            DeviceModelInputOverlay.Visibility = Visibility.Collapsed;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            DeviceModelInputOkButton_Click(sender, e);
        }
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

    private Task ShowMessageAsync(string title, string message)
    {
        // Fully custom monochrome dialog: avoids the WinUI ContentDialog
        // chrome (accent buttons, rounded corners, system theming).
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _messageDialogQueue.Enqueue(new MessageDialogRequest(title, message, completion));
        TryShowNextMessageDialog();
        return completion.Task;
    }

    private void TryShowNextMessageDialog()
    {
        if (_activeMessageDialog is not null || _messageDialogQueue.Count == 0) return;

        var next = _messageDialogQueue.Dequeue();
        _activeMessageDialog = next;
        MessageTitleText.Text = next.Title;
        MessageBodyText.Text = next.Message;
        MessageOverlay.Visibility = Visibility.Visible;
        MessageOverlay.Focus(FocusState.Programmatic);
    }

    private void MessageOkButton_Click(object sender, RoutedEventArgs e) => CompleteMessageDialog();

    private void MessageOverlay_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.Escape or Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            CompleteMessageDialog();
        }
    }

    private void CompleteMessageDialog()
    {
        var active = _activeMessageDialog;
        if (active is null) return;

        _activeMessageDialog = null;
        MessageOverlay.Visibility = Visibility.Collapsed;
        active.Completion.TrySetResult(true);
        TryShowNextMessageDialog();
    }
}
