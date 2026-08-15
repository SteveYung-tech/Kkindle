using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class MainWindow : Window
{
    private const string MaximizeGlyphData = "M 0.5,0.5 H 9.5 V 9.5 H 0.5 Z";
    private const string RestoreGlyphData = "M 2.5,0.5 H 9.5 V 7.5 M 0.5,2.5 H 7.5 V 9.5 H 0.5 Z";
    private const string SidebarChevronDownData = "M 1,2 L 5,6 L 9,2";
    private const string SidebarChevronRightData = "M 2,1 L 6,5 L 2,9";

    private readonly AppPaths _paths;
    private readonly IBookLibraryService _library;
    private readonly IBookFormatConverter _formatConverter;
    private readonly ReaderFormatCacheService _readerFormatCache;
    private readonly DoubanMetadataService _douban;
    private readonly IKindleDeviceService? _kindle;
    private readonly DeviceModelStore _deviceModelStore;
    private readonly ISecretProtector _secretProtector;
    private readonly AppBackupService _backupService;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly FontLibraryService _fontLibrary;
    private readonly DictionaryService _dictionaryService;
    private readonly ReaderDataService _readerData;
    private readonly EpubBookContentService _bookContent;
    private readonly EpubFootnoteResolver _footnotes;
    private readonly PdfTextService _pdfTextService;
    private readonly AiSettingsStore _aiSettingsStore;
    private readonly AiChatClient _aiChatClient;
    private readonly EpubReaderPreparationService _epubReader;
    private readonly Func<IReaderHost> _readerHostFactory;
    private readonly ZLibraryService _zLibraryService;
    private readonly ZLibrarySettingsStore _zLibrarySettingsStore;
    private readonly KindleEmailSettingsStore _kindleEmailSettingsStore;
    private readonly KindleEmailSender _kindleEmailSender;
    private AppSettings _appSettings = new();
    private ZLibrarySettings _zLibrarySettings = new();
    private KindleEmailSettings _kindleEmailSettings = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private string? _deviceDisplayName;
    private Button? _activeNavigationSectionButton;
    private IReaderHost? _readerActiveHost;
    private IReaderHost? _readerPreloadHost;
    private bool _filterControlsReady;
    private bool _updatingFilterControls;
    private bool _updatingDetails;
    private LibraryViewMode _libraryViewMode = LibraryViewMode.Grid;
    private BookCardViewModel? _selectedCard;
    private BookCardViewModel? _multiSelectAnchor;
    private readonly HashSet<Guid> _selectedBookIds = [];
    private TaskCompletionSource<bool>? _confirmationCompletion;
    private TaskCompletionSource<string?>? _collectionNameCompletion;
    private TaskCompletionSource<bool>? _messageCompletion;
    private bool _conversionInProgress;
    private bool _automaticReaderFormatGenerationInProgress;
    private CancellationTokenSource? _conversionCancellation;
    private BookCardViewModel? _conversionCard;
    private bool _conversionMinimized;
    private FormatConversionProgress _conversionLastProgress = new(0, "正在转换…");
    private TaskCompletionSource<DoubanBookCandidate?>? _doubanCandidateCompletion;
    private TaskCompletionSource<DoubanUpdateChoices?>? _doubanApplyCompletion;
    private DoubanBookCandidate? _doubanSelectedCandidate;
    private DoubanBookMetadata? _doubanPreviewMetadata;
    private CancellationTokenSource? _doubanMatchCancellation;
    private TaskCompletionSource<IReadOnlyDictionary<string, IReadOnlyCollection<string>>?>? _importFormatSelectionCompletion;
    private readonly List<(string FilePath, ToggleSwitch Toggle)> _importFormatSelectionRows = [];
    private bool _rubberBandSelecting;
    private Point _rubberBandStart;
    private Point _rubberBandCurrent;
    private bool _rubberBandPressedOnCard;

    public MainWindow()
        : this(CreateDefaultDependencies())
    {
    }

    private MainWindow((AppPaths Paths, IBookLibraryService Library, IBookFormatConverter FormatConverter, DoubanMetadataService Douban) dependencies)
        : this(dependencies.Paths, dependencies.Library, dependencies.FormatConverter, dependencies.Douban)
    {
    }

    public MainWindow(
        AppPaths paths,
        IBookLibraryService library,
        IBookFormatConverter? formatConverter = null,
        DoubanMetadataService? douban = null,
        AppServices? services = null)
    {
        _paths = paths;
        _library = library;
        _formatConverter = formatConverter ?? new BookFormatConversionService();
        _readerFormatCache = new ReaderFormatCacheService(paths, _formatConverter);
        _douban = douban ?? new DoubanMetadataService();
        _kindle = services?.KindleDeviceService;
        _deviceModelStore = new DeviceModelStore(paths);
        _secretProtector = services?.SecretProtector ?? new PlaintextSecretProtector();
        _backupService = new AppBackupService(paths, _secretProtector);
        _appSettingsStore = new AppSettingsStore(paths);
        _fontLibrary = new FontLibraryService(paths);
        _dictionaryService = new DictionaryService(paths);
        _readerData = new ReaderDataService(paths);
        _bookContent = new EpubBookContentService(_readerData);
        _footnotes = new EpubFootnoteResolver();
        _pdfTextService = new PdfTextService();
        _aiSettingsStore = new AiSettingsStore(paths, _secretProtector);
        _aiChatClient = new AiChatClient();
        _epubReader = new EpubReaderPreparationService(paths);
        _readerHostFactory = services?.ReaderHostFactory ?? (() => new NativeWebViewReaderHost());
        _zLibraryService = new ZLibraryService();
        _zLibrarySettingsStore = new ZLibrarySettingsStore(paths, _secretProtector);
        _kindleEmailSettingsStore = new KindleEmailSettingsStore(paths, _secretProtector);
        _kindleEmailSender = new KindleEmailSender();
        ViewModel = new LibraryViewModel(library, paths.Data);

        InitializeComponent();
        DataContext = this;
        Closed += MainWindow_Closed;
        UpdateMaximizeGlyph();
        UpdateWindowShadowMargin();
        SetSidebarActive(AllBooksButton);
        SetLibraryViewMode(LibraryViewMode.Grid);
        UpdateLibraryUi();
        ConfigureStage3Timer();
        SetEjectButtonsEnabled(false);
        Opened += (_, _) => EnsureInteractiveControlToolTips();
    }

    public LibraryViewModel ViewModel { get; }

    public ObservableCollection<BookCollectionFolderViewModel> CollectionFolders { get; } = [];
    public ObservableCollection<DoubanBookCandidate> DoubanCandidates { get; } = [];

    private enum LibraryViewMode
    {
        Grid,
        List,
        Collections
    }

    public async Task InitializeLibraryAsync()
    {
        try
        {
            SetTaskStatus("正在准备本地书库…");
            await _library.InitializeAsync(_lifetimeCancellation.Token);
            await InitializeStage3Async(_lifetimeCancellation.Token);
            await ViewModel.RefreshAsync(_lifetimeCancellation.Token);
            SetupFilterControls();
            await RefreshCollectionsAsync();
            _filterControlsReady = true;
            UpdateLibraryUi();
            SetTaskStatus(ViewModel.StatusText);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetTaskStatus($"无法读取本地书库：{exception.Message}");
            EmptyLibraryTitleText.Text = "本地书库暂时不可用";
            EmptyLibraryMessageText.Text = "请检查数据目录后重启 Kkindle。";
            EmptyLibraryState.IsVisible = true;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty
            && MaximizeWindowGlyph is not null
            && MaximizeWindowButton is not null)
        {
            UpdateMaximizeGlyph();
            UpdateWindowShadowMargin();
        }
    }

    // The shadow host reserves 12 DIP around the UI; maximized windows span the
    // full screen, so the margin (and with it the shadow) collapses to zero.
    private void UpdateWindowShadowMargin()
    {
        if (WindowShadowHost is null) return;
        var maximized = WindowState == WindowState.Maximized;
        WindowShadowHost.Margin = maximized ? new Thickness(0) : new Thickness(12);
        if (WindowResizeLayer is not null)
            WindowResizeLayer.IsVisible = !maximized;
    }

    // Manual window resizing: the transparent (layered) window has no system
    // resize border, so the eight edge/corner handles drive Width/Height and
    // Position directly. Position is in physical pixels; size is in DIPs, so
    // the left/top edge shifts are scaled by the window scaling factor.
    private string? _windowResizeEdge;
    private Point _windowResizeStart;
    private Size _windowResizeStartSize;
    private PixelPoint _windowResizeStartPosition;

    private void WindowResize_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: string edge }
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _windowResizeEdge = edge;
        // Screen-space anchor: the left/top edges move the window itself, so
        // window-relative coordinates would drift under a stationary cursor
        // and feed an endless resize loop (window moves -> synthetic mouse
        // move -> window moves again) that keeps flickering while idle.
        _windowResizeStart = e.GetPosition(null);
        _windowResizeStartSize = new Size(Width, Height);
        _windowResizeStartPosition = Position;
        e.Pointer.Capture(sender as IInputElement);
        e.Handled = true;
    }

    private void WindowResize_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_windowResizeEdge is null) return;
        var current = e.GetPosition(null);
        var dx = current.X - _windowResizeStart.X;
        var dy = current.Y - _windowResizeStart.Y;

        var newWidth = _windowResizeStartSize.Width;
        var newHeight = _windowResizeStartSize.Height;
        var newX = _windowResizeStartPosition.X;
        var newY = _windowResizeStartPosition.Y;

        if (_windowResizeEdge.Contains("Left", StringComparison.Ordinal))
        {
            newWidth = Math.Max(MinWidth, _windowResizeStartSize.Width - dx);
            newX = _windowResizeStartPosition.X + (int)((_windowResizeStartSize.Width - newWidth) * RenderScaling);
        }
        if (_windowResizeEdge.Contains("Right", StringComparison.Ordinal))
            newWidth = Math.Max(MinWidth, _windowResizeStartSize.Width + dx);
        if (_windowResizeEdge.Contains("Top", StringComparison.Ordinal))
        {
            newHeight = Math.Max(MinHeight, _windowResizeStartSize.Height - dy);
            newY = _windowResizeStartPosition.Y + (int)((_windowResizeStartSize.Height - newHeight) * RenderScaling);
        }
        if (_windowResizeEdge.Contains("Bottom", StringComparison.Ordinal))
            newHeight = Math.Max(MinHeight, _windowResizeStartSize.Height + dy);

        var newPosition = new PixelPoint(newX, newY);
        if (newWidth == Width && newHeight == Height && newPosition == Position)
            return;

        Width = newWidth;
        Height = newHeight;
        Position = newPosition;
        e.Handled = true;
    }

    private void WindowResize_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_windowResizeEdge is null) return;
        _windowResizeEdge = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private static (AppPaths Paths, IBookLibraryService Library, IBookFormatConverter FormatConverter, DoubanMetadataService Douban) CreateDefaultDependencies()
    {
        var paths = new AppPaths(AppRootConfiguration.ResolveRoot(AppContext.BaseDirectory));
        return (
            paths,
            new SqliteBookLibraryService(paths, new BookMetadataService()),
            new BookFormatConversionService(),
            new DoubanMetadataService());
    }

    private sealed class PlaintextSecretProtector : ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _stage3Timer.Stop();
        _transferToastTimer.Stop();
        _deviceStatusToastTimer.Stop();
        _appSettingsAutoSaveCancellation?.Cancel();
        _appSettingsAutoSaveCancellation?.Dispose();
        _appSettingsAutoSaveCancellation = null;
        _conversionCancellation?.Cancel();
        _transferCancellation?.Cancel();
        _isTransferring = false;
        _doubanCandidateCompletion?.TrySetResult(null);
        _doubanCandidateCompletion = null;
        _doubanApplyCompletion?.TrySetResult(null);
        _doubanApplyCompletion = null;
        _doubanMatchCancellation?.Cancel();
        _doubanMatchCancellation?.Dispose();
        _doubanMatchCancellation = null;
        _messageCompletion?.TrySetResult(true);
        _messageCompletion = null;
        _importFormatSelectionCompletion?.TrySetResult(null);
        _importFormatSelectionCompletion = null;
        if (_readerDocument is not null || _readerIsPdf)
            await CloseReaderAsync();
        _readerNavigationCancellation?.Cancel();
        _readerSessionCancellation?.Cancel();
        _zLibrarySearchCancellation?.Cancel();
        _zLibrarySearchCancellation?.Dispose();
        _zLibrarySearchCancellation = null;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        _douban.Dispose();
        _zLibraryService.Dispose();
        _aiChatClient.Dispose();
        _readerNavigationCancellation?.Dispose();
        _readerSessionCancellation?.Dispose();
        _readerActiveHost?.Dispose();
        _readerPreloadHost?.Dispose();
        foreach (var card in ViewModel.Books)
            card.CoverImage?.Dispose();
        foreach (var folder in CollectionFolders)
        {
            folder.Cover1?.Dispose();
            folder.Cover2?.Dispose();
            folder.Cover3?.Dispose();
        }
    }

    private void SetupFilterControls()
    {
        _updatingFilterControls = true;
        try
        {
            AuthorFilterBox.ItemsSource = new[] { "全部作者" }.Concat(ViewModel.AvailableAuthors).ToArray();
            TagFilterBox.ItemsSource = new[] { "全部标签" }.Concat(ViewModel.AvailableTags).ToArray();
            FormatFilterBox.ItemsSource = new[] { "全部格式" }.Concat(ViewModel.AvailableFormats).ToArray();
            CategoryFilterBox.ItemsSource = new[] { "全部分类" }.Concat(ViewModel.AvailableCategories).ToArray();
            ReadingStatusFilterBox.ItemsSource = new[] { "全部状态", "待读", "阅读中", "已读" };
            LibrarySortBox.ItemsSource = new[] { "最近更新", "标题升序", "作者升序", "创建时间", "进度优先" };

            AuthorFilterBox.SelectedIndex = 0;
            TagFilterBox.SelectedIndex = 0;
            FormatFilterBox.SelectedIndex = 0;
            CategoryFilterBox.SelectedIndex = 0;
            ReadingStatusFilterBox.SelectedIndex = 0;
            LibrarySortBox.SelectedIndex = (int)ViewModel.SortMode;
            FavoritesOnlyCheckBox.IsChecked = ViewModel.FavoritesOnly;
        }
        finally
        {
            _updatingFilterControls = false;
        }
    }

    private void UpdateLibraryUi()
    {
        LibraryBusyProgress.IsVisible = ViewModel.IsBusy;
        LibrarySummaryText.Text = ViewModel.StatusText;
        TaskStatusText.Text = ViewModel.StatusText;
        SidebarCountText.Text = ViewModel.Books.Count.ToString();
        foreach (var card in ViewModel.Books)
        {
            card.SetGalleryTextVisible(!_appSettings.GridGalleryDisplay);
            card.SetLibraryPresenceVisible(_appSettings.CompareKindleLibraryEnabled);
        }
        foreach (var card in DeviceBooks)
        {
            card.SetGalleryTextVisible(!_appSettings.GridGalleryDisplay);
            card.SetLibraryPresenceVisible(_appSettings.CompareKindleLibraryEnabled);
        }
        SyncCardSelectionVisuals();

        var showingBooks = _libraryViewMode is LibraryViewMode.Grid or LibraryViewMode.List;
        var hasBooks = ViewModel.Books.Count > 0;
        var showingCollections = _libraryViewMode == LibraryViewMode.Collections;
        var collectionsEmpty = CollectionFolders.Count == 0;
        EmptyLibraryState.IsVisible = !ViewModel.IsBusy
            && ((showingBooks && !hasBooks) || (showingCollections && collectionsEmpty));

        if (showingCollections && collectionsEmpty)
        {
            EmptyLibraryTitleText.Text = "还没有收藏夹";
            EmptyLibraryMessageText.Text = "点击“新建收藏夹”创建，之后可在书籍右键菜单中把书籍加入其中。";
        }
        else if (ViewModel.LibraryBooks.Count == 0)
        {
            EmptyLibraryTitleText.Text = "电脑书库还是空的";
            EmptyLibraryMessageText.Text = "导入 EPUB、PDF、MOBI 或 AZW3 文件开始阅读。";
        }
        else
        {
            EmptyLibraryTitleText.Text = "没有符合条件的书籍";
            EmptyLibraryMessageText.Text = "试试清除筛选条件，或换一个搜索词。";
        }

        CollectionHeader.IsVisible = _libraryViewMode is LibraryViewMode.Grid or LibraryViewMode.List
            && ViewModel.CollectionFilterId is not null;
        ActiveCollectionTitleText.Text = ViewModel.CollectionFilterName ?? string.Empty;
        CreateCollectionButton.IsVisible = _libraryViewMode == LibraryViewMode.Collections;

        if (_selectedCard is not null)
        {
            var refreshedCard = ViewModel.Books.FirstOrDefault(card => card.Book.Id == _selectedCard.Book.Id);
            if (refreshedCard is not null)
                SelectBook(refreshedCard);
            else if (!ViewModel.LibraryBooks.Any(book => book.Id == _selectedCard.Book.Id))
                ClearSelectedBook();
        }
    }

    private void SetTaskStatus(string message)
    {
        TaskStatusText.Text = message;
        LibrarySummaryText.Text = ViewModel.StatusText;
    }

    private void SetLibraryViewMode(LibraryViewMode mode)
    {
        _libraryViewMode = mode;
        BookGrid.IsVisible = mode == LibraryViewMode.Grid;
        BookList.IsVisible = mode == LibraryViewMode.List;
        CollectionScroll.IsVisible = mode == LibraryViewMode.Collections;
        CollectionHeader.IsVisible = mode is LibraryViewMode.Grid or LibraryViewMode.List
            && ViewModel.CollectionFilterId is not null;
        CreateCollectionButton.IsVisible = mode == LibraryViewMode.Collections;
        LibraryViewGridItem.IsChecked = mode == LibraryViewMode.Grid;
        LibraryViewListItem.IsChecked = mode == LibraryViewMode.List;
        LibraryViewCollectionsItem.IsChecked = mode == LibraryViewMode.Collections;
        LibraryViewToggleIcon.Data = Geometry.Parse(mode switch
        {
            LibraryViewMode.List => "M 3,4 H 5 M 8,4 H 20 M 3,12 H 5 M 8,12 H 20 M 3,20 H 5 M 8,20 H 20",
            LibraryViewMode.Collections => "M 2,5 H 9 L 11,7 H 22 V 20 H 2 Z M 2,5 V 3 H 8 L 10,5",
            _ => "M 2,2 H 8 V 8 H 2 Z M 14,2 H 20 V 8 H 14 Z M 2,14 H 8 V 20 H 2 Z M 14,14 H 20 V 20 H 14 Z"
        });
        UpdateLibraryUi();
    }

    private void SelectBook(BookCardViewModel card)
    {
        _selectedCard = card;
        LibraryDetailPane.IsVisible = true;
        LibraryDetailPane.Opacity = 0;
        Dispatcher.UIThread.Post(() => LibraryDetailPane.Opacity = 1);
        if (LibraryRoot.ColumnDefinitions.Count >= 3)
            LibraryRoot.ColumnDefinitions[2].Width = new GridLength(320);
        DetailCoverImage.Source = card.CoverImage;
        DetailCoverPlaceholder.IsVisible = card.CoverImage is null;
        DetailTitleText.Text = card.Title;
        DetailAuthorsText.Text = card.Authors;
        DetailDoubanRatingBox.Text = card.Book.DoubanRating is null
            ? string.Empty
            : $"{card.Book.DoubanRating:0.0}（{card.Book.DoubanRatingCount ?? 0} 人评价）";
        DetailStateText.Text = card.ReadingStateLabel;
        DetailOrganizationText.Text = card.OrganizationLabel;
        DetailPublicationText.Text = card.PublicationLabel;
        DetailIdentifierText.Text = card.IdentifierLabel;
        DetailDescriptionText.Text = card.DescriptionLabel;
        DetailTagsBox.Text = card.Book.Tags;
        DetailCategoryBox.Text = card.Book.Category;
        DetailDescriptionBox.Text = card.Book.Description ?? string.Empty;
        DetailSeriesBox.Text = card.Book.Series ?? string.Empty;
        DetailPublisherBox.Text = card.Book.Publisher ?? string.Empty;
        DetailPublishDateBox.Text = card.Book.PublishDate ?? string.Empty;
        DetailIsbnBox.Text = card.Book.Isbn ?? string.Empty;
        DetailPageCountBox.Text = card.Book.PageCount ?? string.Empty;
        DetailBindingBox.Text = card.Book.Binding ?? string.Empty;
        UpdateDetailActionIcons(card.Book.IsFavorite, card.Book.ReadingStatus);
        DetailFiles.ItemsSource = card.Book.Files;

        _updatingDetails = true;
        try
        {
            DetailCollectionBox.ItemsSource = CollectionFolders;
            DetailCollectionBox.SelectedItem = CollectionFolders.FirstOrDefault(folder =>
                card.Book.CollectionIds.Contains(folder.Collection.Id));
            CollectionMembershipButton.Content = DetailCollectionBox.SelectedItem is null ? "加入收藏夹" : "移出收藏夹";
        }
        finally
        {
            _updatingDetails = false;
        }
    }

    private void ClearSelectedBook()
    {
        _selectedCard = null;
        LibraryDetailPane.IsVisible = false;
        if (LibraryRoot.ColumnDefinitions.Count >= 3)
            LibraryRoot.ColumnDefinitions[2].Width = new GridLength(0);
        DetailCoverImage.Source = null;
        DetailCoverPlaceholder.IsVisible = true;
        DetailTitleText.Text = "请选择一本书";
        DetailAuthorsText.Text = string.Empty;
        DetailDoubanRatingBox.Text = string.Empty;
        DetailStateText.Text = string.Empty;
        DetailOrganizationText.Text = string.Empty;
        DetailPublicationText.Text = string.Empty;
        DetailIdentifierText.Text = string.Empty;
        DetailTagsBox.Text = string.Empty;
        DetailCategoryBox.Text = string.Empty;
        DetailDescriptionBox.Text = string.Empty;
        DetailSeriesBox.Text = string.Empty;
        DetailPublisherBox.Text = string.Empty;
        DetailPublishDateBox.Text = string.Empty;
        DetailIsbnBox.Text = string.Empty;
        DetailPageCountBox.Text = string.Empty;
        DetailBindingBox.Text = string.Empty;
        UpdateDetailActionIcons(false, LibraryReadingStatus.Unread);
        DetailDescriptionText.Text = "暂无简介";
        DetailFiles.ItemsSource = Array.Empty<BookFile>();
        DetailCollectionBox.ItemsSource = CollectionFolders;
        DetailCollectionBox.SelectedItem = null;
        CollectionMembershipButton.Content = "加入收藏夹";
    }

    private void UpdateDetailActionIcons(bool isFavorite, LibraryReadingStatus readingStatus)
    {
        // Star glyph: hollow outline when not favorite, filled when favorite
        // (WinUI E734 / E735).
        DetailFavoriteIcon.Fill = isFavorite ? Brushes.Black : Brushes.Transparent;
        var favoriteLabel = isFavorite ? "已收藏；点击取消收藏" : "未收藏；点击加入收藏";
        ToolTip.SetTip(DetailFavoriteButton, favoriteLabel);
        AutomationProperties.SetName(DetailFavoriteButton, favoriteLabel);

        // Reading-state glyphs (WinUI E8A4 / E736 / E73E): a standing book for
        // unread, an open book for reading, a check mark for finished.
        var (data, label) = readingStatus switch
        {
            LibraryReadingStatus.Reading => (
                "M 3,4 L 8,5.5 L 8,13 L 3,11.5 Z M 13,4 L 8,5.5 L 8,13 L 13,11.5 Z M 8,5.5 L 8,13",
                "阅读中；点击标记为已读"),
            LibraryReadingStatus.Finished => (
                "M 3,8.5 L 6.5,12 L 13,4.5",
                "已读；点击重置为待读"),
            _ => (
                "M 5,2.5 L 12,2 L 12,14 L 5,14.5 Z M 5,5 L 12,4.5 M 5,8 L 12,7.5 M 5,11 L 12,10.5",
                "待读；点击标记为阅读中")
        };
        DetailReadingStatusIcon.Data = Geometry.Parse(data);
        ToolTip.SetTip(DetailReadingStatusButton, label);
        AutomationProperties.SetName(DetailReadingStatusButton, label);
    }

    private async Task RefreshLibraryAsync()
    {
        await ViewModel.RefreshAsync(_lifetimeCancellation.Token);
        SetupFilterControls();
        await RefreshCollectionsAsync();
        UpdateLibraryUi();
        SetTaskStatus(ViewModel.StatusText);
    }

    private async Task RefreshCollectionsAsync()
    {
        var collections = await _library.GetCollectionsAsync(_lifetimeCancellation.Token);
        var books = ViewModel.LibraryBooks;

        foreach (var folder in CollectionFolders)
        {
            folder.Cover1?.Dispose();
            folder.Cover2?.Dispose();
            folder.Cover3?.Dispose();
        }
        CollectionFolders.Clear();

        foreach (var collection in collections)
        {
            var collectionBooks = books
                .Where(book => book.CollectionIds.Contains(collection.Id))
                .OrderByDescending(book => book.UpdatedAt)
                .ToArray();
            var coverPaths = collectionBooks
                .Select(book => book.CoverPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Take(3)
                .ToArray();
            CollectionFolders.Add(new BookCollectionFolderViewModel(
                collection,
                collectionBooks.Length,
                _paths.Data,
                coverPaths));
        }

        if (_selectedCard is not null)
            SelectBook(_selectedCard);
    }

    private async Task ImportPathsAsync(IEnumerable<string> paths)
    {
        var selectedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedPaths.Length == 0)
        {
            SetTaskStatus("没有选择可导入的文件。");
            return;
        }

        try
        {
            var inputPaths = ExpandImportableFiles(selectedPaths);
            if (inputPaths.Length == 0)
            {
                SetTaskStatus("所选位置没有 EPUB、PDF、MOBI 或 AZW3 文件。");
                await ShowMessageAsync("无法导入", "拖入的文件或文件夹中没有 EPUB、PDF、MOBI 或 AZW3 书籍文件。");
                return;
            }
            IReadOnlyDictionary<string, IReadOnlyCollection<string>>? requestedFormats = null;
            if (_appSettings.AutoGenerateEpubAndAzw3OnImport)
            {
                requestedFormats = await ChooseImportFormatsAsync(inputPaths);
                if (requestedFormats is null)
                {
                    SetTaskStatus("已取消导入。");
                    return;
                }
            }
            SetTaskStatus($"正在导入 {inputPaths.Length} 个位置…");
            ShowTaskProgressPopup();
            TaskProgressPopupText.Text = $"正在导入 {inputPaths.Length} 个位置…";
            var progress = new Progress<TransferProgress>(value =>
            {
                var message = string.IsNullOrWhiteSpace(value.Message)
                    ? $"正在导入：{value.Percentage:0}%"
                    : value.Message;
                SetTaskStatus(message);
                TaskProgressPopupBar.Value = value.Percentage;
                TaskProgressPopupText.Text = message;
            });
            var result = await ViewModel.ImportAsync(inputPaths, progress, _lifetimeCancellation.Token);
            var automaticFormats = await AutoGenerateReaderFormatsForImportsAsync(
                result,
                _lifetimeCancellation.Token,
                requestedFormats);
            HideTaskProgressPopup();
            await RefreshCollectionsAsync();
            UpdateLibraryUi();
            var automaticSuffix = automaticFormats.Failures.Count > 0
                ? $"；格式补齐失败 {automaticFormats.Failures.Count} 项"
                : automaticFormats.GeneratedCount > 0
                    ? $"；已补齐 {automaticFormats.GeneratedCount} 个 EPUB/AZW3 文件"
                    : string.Empty;
            SetTaskStatus(result.FailureCount == 0
                ? $"已导入 {result.SuccessCount} 本书{automaticSuffix}。"
                : $"已导入 {result.SuccessCount} 本书，{result.FailureCount} 项失败{automaticSuffix}。 ");
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            HideTaskProgressPopup();
        }
        catch (Exception exception)
        {
            HideTaskProgressPopup();
            SetTaskStatus($"导入失败：{exception.Message}");
            await ShowMessageAsync("导入失败", exception.Message);
        }
    }

    private static string[] ExpandImportableFiles(IEnumerable<string> selectedPaths)
    {
        var supported = new HashSet<string>([".epub", ".pdf", ".mobi", ".azw3"], StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selectedPath in selectedPaths)
        {
            try
            {
                var fullPath = Path.GetFullPath(selectedPath);
                if (File.Exists(fullPath))
                {
                    if (supported.Contains(Path.GetExtension(fullPath))) files.Add(fullPath);
                    continue;
                }
                if (!Directory.Exists(fullPath)) continue;
                foreach (var file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
                    if (supported.Contains(Path.GetExtension(file))) files.Add(Path.GetFullPath(file));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return files.OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private Task<IReadOnlyDictionary<string, IReadOnlyCollection<string>>?> ChooseImportFormatsAsync(
        IReadOnlyList<string> files)
    {
        _importFormatSelectionCompletion?.TrySetResult(null);
        _importFormatSelectionRows.Clear();
        ImportFormatSelectionList.Children.Clear();
        foreach (var file in files)
        {
            var toggle = new ToggleSwitch
            {
                IsChecked = true,
                OnContent = "补齐",
                OffContent = "仅导入"
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 14 };
            row.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = Path.GetFileName(file), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = Path.GetDirectoryName(file) ?? string.Empty, FontSize = 10, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis }
                }
            });
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);
            ImportFormatSelectionList.Children.Add(new Border
            {
                Padding = new Thickness(10, 8),
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(1),
                Child = row
            });
            _importFormatSelectionRows.Add((file, toggle));
        }
        ImportFormatSelectionSummaryText.Text = $"共 {files.Count} 个文件。可逐项决定是否在导入后补齐 EPUB 与 AZW3；原始文件始终保留。";
        ShowOverlay(ImportFormatSelectionOverlay);
        ImportFormatSelectionOverlay.Focus();
        _importFormatSelectionCompletion = new TaskCompletionSource<IReadOnlyDictionary<string, IReadOnlyCollection<string>>?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return _importFormatSelectionCompletion.Task;
    }

    private void CompleteImportFormatSelection(bool import)
    {
        var completion = _importFormatSelectionCompletion;
        if (completion is null) return;
        _importFormatSelectionCompletion = null;
        ImportFormatSelectionOverlay.IsVisible = false;
        var result = import
            ? _importFormatSelectionRows.ToDictionary(
                row => row.FilePath,
                row => (IReadOnlyCollection<string>)(row.Toggle.IsChecked == true ? new[] { "epub", "azw3" } : Array.Empty<string>()),
                StringComparer.OrdinalIgnoreCase)
            : null;
        _importFormatSelectionRows.Clear();
        ImportFormatSelectionList.Children.Clear();
        completion.TrySetResult(result);
    }

    private void ImportFormatSelectionPrimaryButton_Click(object? sender, RoutedEventArgs e) =>
        CompleteImportFormatSelection(true);

    private void ImportFormatSelectionCancelButton_Click(object? sender, RoutedEventArgs e) =>
        CompleteImportFormatSelection(false);

    private void ImportFormatSelectionOverlay_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.Enter)) return;
        e.Handled = true;
        CompleteImportFormatSelection(e.Key == Key.Enter);
    }

    private static string[] GetDraggedPaths(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?
            .Select(item => item.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToArray()
        ?? [];

    private void LibraryPane_DragOver(object? sender, DragEventArgs e)
    {
        var paths = GetDraggedPaths(e);
        e.DragEffects = paths.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        LibraryDropOverlay.IsVisible = paths.Length > 0;
        e.Handled = true;
    }

    private void LibraryPane_DragLeave(object? sender, RoutedEventArgs e)
    {
        LibraryDropOverlay.IsVisible = false;
        e.Handled = true;
    }

    private void LibraryContentHost_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control host
            || !e.GetCurrentPoint(host).Properties.IsLeftButtonPressed
            || IsBookCardSource(e.Source))
            return;

        if (LibraryDetailPane.IsVisible)
            ClearSelectedBook();
    }

    private static bool IsBookCardSource(object? source)
    {
        for (var current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            if ((current is Grid || current is Border)
                && current is Control control
                && control.DataContext is BookCardViewModel)
                return true;
        }

        return false;
    }

    private async void LibraryPane_Drop(object? sender, DragEventArgs e)
    {
        var paths = GetDraggedPaths(e);
        LibraryDropOverlay.IsVisible = false;
        e.Handled = true;
        if (paths.Length > 0)
            await ImportPathsAsync(paths);
    }

    private async Task OpenBookAsync(BookCardViewModel card, BookFile? requestedFile = null)
    {
        var file = requestedFile ?? ReaderBookSelectionPolicy.SelectPreferred(
            card.Book.Files,
            _appSettings.PreferredOpenFormat);
        if (file is null)
        {
            SetTaskStatus("这本书没有可打开的文件。");
            await ShowMessageAsync("无法打开书籍", "所选格式文件不存在或已不再支持。");
            return;
        }

        var path = ViewModel.GetAbsoluteFilePath(file);
        if (!File.Exists(path))
        {
            SetTaskStatus($"找不到文件：{file.RelativePath}");
            await ShowMessageAsync("无法打开书籍", "所选格式文件不存在或已被删除。");
            return;
        }

        if (string.Equals(file.Format, "epub", StringComparison.OrdinalIgnoreCase))
        {
            await OpenEpubReaderAsync(card, file, path);
            return;
        }

        if (string.Equals(file.Format, "pdf", StringComparison.OrdinalIgnoreCase))
        {
            await OpenPdfReaderAsync(card, file, path);
            return;
        }

        if (file.Format.Equals("mobi", StringComparison.OrdinalIgnoreCase)
            || file.Format.Equals("azw3", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                SetTaskStatus($"正在准备《{card.Title}》的阅读缓存…");
                var cache = await _readerFormatCache.PrepareEpubAsync(
                    path,
                    file.Sha256,
                    file.Format,
                    _lifetimeCancellation.Token);
                await OpenEpubReaderAsync(card, file, cache.EpubPath);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                SetTaskStatus($"准备阅读缓存失败：{exception.Message}");
                await ShowMessageAsync("无法打开书籍", exception.Message);
            }
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            SetTaskStatus($"已用系统默认程序打开《{card.Title}》。");
        }
        catch (Exception exception)
        {
            SetTaskStatus($"打开文件失败：{exception.Message}");
            await ShowMessageAsync("无法打开书籍", exception.Message);
        }
    }

    private async Task DeleteSelectedBookAsync()
    {
        if (_selectedCard is null) return;
        var card = _selectedCard;
        if (_readerDocument is not null || _readerIsPdf)
        {
            await ShowMessageAsync("无法删除书籍", "当前正在阅读这本书，请先关闭阅读器。");
            return;
        }
        if (!await ConfirmAsync("删除书籍", $"确定删除《{card.Title}》及其全部文件吗？")) return;

        try
        {
            await ViewModel.DeleteBookAsync(card.Book, _lifetimeCancellation.Token);
            ClearSelectedBook();
            await RefreshCollectionsAsync();
            UpdateLibraryUi();
            SetTaskStatus(ViewModel.StatusText);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetTaskStatus($"删除失败：{exception.Message}");
            await ShowMessageAsync("无法删除书籍", exception.Message);
        }
    }

    private async Task DeleteFileAsync(BookFile file)
    {
        if (_selectedCard is null) return;
        var card = _selectedCard;
        if (_readerDocument is not null || _readerIsPdf)
        {
            await ShowMessageAsync("无法删除格式", "当前正在阅读这本书，请先关闭阅读器。");
            return;
        }
        if (!await ConfirmAsync("删除文件", $"确定从《{card.Title}》中删除 {file.Format.ToUpperInvariant()} 文件吗？")) return;

        try
        {
            await ViewModel.DeleteFileAsync(card.Book, file, _lifetimeCancellation.Token);
            await RefreshLibraryAsync();
            SetTaskStatus(ViewModel.StatusText);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetTaskStatus($"删除文件失败：{exception.Message}");
            await ShowMessageAsync("无法删除格式", exception.Message);
        }
    }

    private IReadOnlyList<BookCardViewModel> GetSelectedCards()
    {
        var selected = ViewModel.Books
            .Where(card => _selectedBookIds.Contains(card.Book.Id))
            .ToArray();
        return selected.Length > 0 || _selectedCard is null
            ? selected
            : [_selectedCard];
    }

    private void UpdateMultiSelectionUi()
    {
        SyncCardSelectionVisuals();
        var selectedCount = GetSelectedCards().Count;
        MultiSelectionBar.IsVisible = selectedCount > 1;
        MultiSelectionText.Text = selectedCount > 0 ? $"已选择 {selectedCount} 本书" : string.Empty;
    }

    private void SyncCardSelectionVisuals()
    {
        // 单点选中的书显示黑边；只有真正多选（≥2 本）时才附带 ✓ 徽标。
        var multi = _selectedBookIds.Count > 1;
        foreach (var card in ViewModel.Books)
        {
            var selected = _selectedBookIds.Contains(card.Book.Id);
            card.IsSelected = selected;
            card.IsMultiSelected = selected && multi;
        }
    }

    private ContextMenu BuildBookContextMenu(BookCardViewModel card)
    {
        var menu = new ContextMenu();

        var openMenu = new MenuItem { Header = "打开书籍" };
        foreach (var format in new[] { "EPUB", "PDF", "AZW3" })
        {
            var item = new MenuItem
            {
                Header = format,
                IsEnabled = card.Book.Files.Any(file =>
                    string.Equals(file.Format, format, StringComparison.OrdinalIgnoreCase)
                    && ReaderBookSelectionPolicy.GetSupportedFiles([file]).Count > 0)
            };
            item.Click += async (_, _) => await OpenBookFormatAsync(card, format);
            openMenu.Items.Add(item);
        }
        openMenu.IsEnabled = openMenu.Items.OfType<MenuItem>().Any(item => item.IsEnabled);
        menu.Items.Add(openMenu);

        var convertMenu = new MenuItem { Header = "转换为" };
        foreach (var target in new[] { "epub", "azw3", "pdf" })
        {
            var item = new MenuItem { Header = target.ToUpperInvariant(), Tag = target };
            item.Click += async (_, _) => await ConvertBookAsync(card, target);
            convertMenu.Items.Add(item);
        }
        menu.Items.Add(convertMenu);

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem(
            card.Book.IsFavorite ? "取消收藏" : "加入收藏",
            () => ToggleFavoriteAsync(card)));

        var statusMenu = new MenuItem { Header = "标记阅读状态" };
        foreach (var status in Enum.GetValues<LibraryReadingStatus>())
        {
            var item = new MenuItem { Header = GetReadingStatusName(status), Tag = status };
            item.Click += async (_, _) => await UpdateReadingStatusAsync(card, status);
            statusMenu.Items.Add(item);
        }
        menu.Items.Add(statusMenu);

        var collectionMenu = new MenuItem { Header = "收藏夹" };
        foreach (var folder in CollectionFolders)
        {
            var item = new MenuItem
            {
                Header = card.Book.CollectionIds.Contains(folder.Collection.Id)
                    ? $"移出“{folder.Name}”"
                    : $"加入“{folder.Name}”",
                Tag = folder
            };
            item.Click += async (_, _) => await ToggleBookCollectionAsync(card, folder);
            collectionMenu.Items.Add(item);
        }
        collectionMenu.Items.Add(new Separator());
        collectionMenu.Items.Add(CreateMenuItem("新建收藏夹…", async () =>
        {
            var name = await PromptCollectionNameAsync();
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                var collection = await _library.CreateCollectionAsync(name, _lifetimeCancellation.Token);
                await _library.AddBookToCollectionAsync(card.Book.Id, collection.Id, _lifetimeCancellation.Token);
                await RefreshLibraryAsync();
                SetTaskStatus($"已创建并加入收藏夹“{name}”。");
            }
            catch (Exception exception)
            {
                SetTaskStatus($"创建收藏夹失败：{exception.Message}");
            }
        }));
        menu.Items.Add(collectionMenu);

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem(
            GetSelectedCards().Count > 1 ? $"发送选中书籍到 Kindle（{GetSelectedCards().Count}）" : "发送到 Kindle",
            SendSelectedBooksToKindleAsync));
        menu.Items.Add(CreateMenuItem(
            GetSelectedCards().Count > 1 ? $"邮件发送选中书籍（{GetSelectedCards().Count}）" : "邮件发送",
            SendSelectedBooksByEmailAsync));

        menu.Items.Add(CreateMenuItem("豆瓣匹配", () => MatchDoubanAsync(card)));
        menu.Items.Add(new Separator());

        var deleteFormatMenu = new MenuItem { Header = "删除格式" };
        foreach (var format in new[] { "EPUB", "PDF", "MOBI", "AZW3" })
        {
            var file = card.Book.Files.FirstOrDefault(candidate =>
                string.Equals(candidate.Format, format, StringComparison.OrdinalIgnoreCase));
            var item = new MenuItem { Header = format, IsEnabled = file is not null, Tag = file };
            item.Click += async (_, _) =>
            {
                if (item.Tag is BookFile selectedFile)
                    await DeleteFileAsync(selectedFile);
            };
            deleteFormatMenu.Items.Add(item);
        }
        deleteFormatMenu.IsEnabled = deleteFormatMenu.Items.OfType<MenuItem>().Any(item => item.IsEnabled);
        menu.Items.Add(deleteFormatMenu);
        menu.Items.Add(CreateMenuItem("删除全部格式", () => DeleteBookFromContextAsync(card)));

        if (GetSelectedCards().Count > 1)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem(
                $"删除所选 ({GetSelectedCards().Count})",
                DeleteSelectedBooksAsync));
        }

        return menu;
    }

    private async Task OpenBookFormatAsync(BookCardViewModel card, string format)
    {
        var file = ReaderBookSelectionPolicy.GetSupportedFiles(card.Book.Files)
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Format, format, StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            SetTaskStatus("所选格式文件不存在或不支持。");
            return;
        }

        await OpenBookAsync(card, file);
    }

    private static MenuItem CreateMenuItem(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    private void BookCard_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not BookCardViewModel card) return;
        if (!_selectedBookIds.Contains(card.Book.Id))
        {
            _selectedBookIds.Clear();
            _selectedBookIds.Add(card.Book.Id);
            var activeList = BookGrid.IsVisible ? BookGrid : BookList;
            activeList.SelectedItems?.Clear();
            activeList.SelectedItems?.Add(card);
        }
        _selectedCard = card;
        _multiSelectAnchor = card;
        SelectBook(card);
        UpdateMultiSelectionUi();
        var menu = BuildBookContextMenu(card);
        menu.Open(control);
        e.Handled = true;
    }

    private static string GetReadingStatusName(LibraryReadingStatus status) => status switch
    {
        LibraryReadingStatus.Reading => "阅读中",
        LibraryReadingStatus.Finished => "已读",
        _ => "待读"
    };

    private async Task DeleteSelectedBooksAsync()
    {
        var cards = GetSelectedCards();
        if (cards.Count == 0) return;
        if (_readerDocument is not null || _readerIsPdf)
        {
            await ShowMessageAsync("无法删除书籍", "当前正在阅读其中一本书，请先关闭阅读器。");
            return;
        }
        if (!await ConfirmAsync("删除所选书籍", $"确定删除选中的 {cards.Count} 本书及其文件吗？")) return;

        try
        {
            foreach (var card in cards)
                await _library.DeleteAsync(card.Book.Id, _lifetimeCancellation.Token);
            _selectedBookIds.Clear();
            ClearSelectedBook();
            await RefreshLibraryAsync();
            SetTaskStatus($"已删除 {cards.Count} 本书。");
        }
        catch (Exception exception)
        {
            SetTaskStatus($"批量删除失败：{exception.Message}");
            await ShowMessageAsync("无法删除书籍", exception.Message);
        }
    }

    private async Task DeleteBookFromContextAsync(BookCardViewModel card)
    {
        if (_readerDocument is not null || _readerIsPdf)
        {
            await ShowMessageAsync("无法删除书籍", "当前正在阅读这本书，请先关闭阅读器。");
            return;
        }
        if (!await ConfirmAsync("删除书籍", $"确定删除《{card.Title}》及其全部文件吗？")) return;
        try
        {
            await _library.DeleteAsync(card.Book.Id, _lifetimeCancellation.Token);
            _selectedBookIds.Remove(card.Book.Id);
            if (_selectedCard?.Book.Id == card.Book.Id) ClearSelectedBook();
            await RefreshLibraryAsync();
            SetTaskStatus($"已删除《{card.Title}》。");
        }
        catch (Exception exception)
        {
            SetTaskStatus($"删除失败：{exception.Message}");
            await ShowMessageAsync("无法删除书籍", exception.Message);
        }
    }

    private async Task ToggleFavoriteAsync(BookCardViewModel card)
    {
        card.Book.IsFavorite = !card.Book.IsFavorite;
        await SaveBookMetadataAsync(card, card.Book.IsFavorite ? "已加入收藏。" : "已取消收藏。");
    }

    private async Task UpdateReadingStatusAsync(BookCardViewModel card, LibraryReadingStatus status)
    {
        card.Book.ReadingStatus = status;
        await SaveBookMetadataAsync(card, $"已标记为“{GetReadingStatusName(status)}”。");
    }

    private async Task ToggleBookCollectionAsync(BookCardViewModel card, BookCollectionFolderViewModel folder)
    {
        try
        {
            if (card.Book.CollectionIds.Contains(folder.Collection.Id))
                await _library.RemoveBookFromCollectionAsync(card.Book.Id, folder.Collection.Id, _lifetimeCancellation.Token);
            else
                await _library.AddBookToCollectionAsync(card.Book.Id, folder.Collection.Id, _lifetimeCancellation.Token);
            await RefreshLibraryAsync();
            SetTaskStatus($"已更新“{folder.Name}”中的书籍归属。");
        }
        catch (Exception exception)
        {
            SetTaskStatus($"更新收藏夹失败：{exception.Message}");
            await ShowMessageAsync("无法更新收藏夹", exception.Message);
        }
    }

    private async Task SaveBookMetadataAsync(BookCardViewModel card, string successMessage)
    {
        try
        {
            await _library.UpdateMetadataAsync(card.Book, _lifetimeCancellation.Token);
            await RefreshLibraryAsync();
            SetTaskStatus(successMessage);
        }
        catch (Exception exception)
        {
            SetTaskStatus($"保存书籍信息失败：{exception.Message}");
        }
    }

    private async Task ConvertBookAsync(BookCardViewModel card, string targetFormat)
    {
        if (_conversionInProgress)
        {
            await ShowMessageAsync("格式转换", "已有一本书正在转换，请稍候。");
            return;
        }

        var target = BookFormatConversionPolicy.Normalize(targetFormat);
        if (!BookFormatConversionPolicy.IsConvertibleFormat(target)) return;
        if (card.Book.Files.Any(file =>
                string.Equals(BookFormatConversionPolicy.Normalize(file.Format), target, StringComparison.OrdinalIgnoreCase)))
        {
            await ShowMessageAsync("格式转换", $"这本书已经有 {target.ToUpperInvariant()} 格式。");
            return;
        }

        var sourceFile = BookFormatConversionPolicy.SelectSource(card.Book.Files, target);
        if (sourceFile is null)
        {
            await ShowMessageAsync("格式转换", "需要 EPUB、AZW3、PDF 或 MOBI 作为转换源。");
            return;
        }

        var sourcePath = ViewModel.GetAbsoluteFilePath(sourceFile);
        if (!File.Exists(sourcePath))
        {
            SetTaskStatus($"找不到转换来源：{sourceFile.RelativePath}");
            return;
        }

        _conversionInProgress = true;
        _conversionCard = card;
        _conversionMinimized = false;
        _conversionCancellation?.Dispose();
        _conversionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var initialProgress = new FormatConversionProgress(
            0,
            $"准备将 {sourceFile.Format.ToUpperInvariant()} 转换为 {target.ToUpperInvariant()}…");
        _conversionLastProgress = initialProgress;
        ShowBookConversionPopup(card.Title, sourceFile.Format, target, initialProgress);
        SetTaskStatus(initialProgress.Message);
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "KkindleConversions", Guid.NewGuid().ToString("N"));
        var temporaryOutput = Path.Combine(temporaryDirectory, "converted." + target);
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            var progress = new Progress<FormatConversionProgress>(ApplyBookConversionProgress);
            await _formatConverter.ConvertAsync(
                sourcePath,
                temporaryOutput,
                progress,
                _conversionCancellation.Token);
            ApplyBookConversionProgress(new FormatConversionProgress(100, "正在写入书库…"));
            await _library.AddFileToBookAsync(card.Book.Id, temporaryOutput, _conversionCancellation.Token);
            await RefreshLibraryAsync();
            ApplyBookConversionProgress(new FormatConversionProgress(100, "转换完成。"));
            SetTaskStatus($"已为《{card.Title}》添加 {target.ToUpperInvariant()} 格式。");
        }
        catch (OperationCanceledException) when (_conversionCancellation?.IsCancellationRequested == true)
        {
            SetTaskStatus("格式转换已取消。");
        }
        catch (Exception exception)
        {
            SetTaskStatus($"格式转换失败：{exception.Message}");
            ApplyBookConversionProgress(new FormatConversionProgress(
                _conversionLastProgress.Percentage,
                "格式转换失败。"));
            await ShowMessageAsync("格式转换失败", exception.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryOutput)) File.Delete(temporaryOutput);
                if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch
            {
            }
            BookConversionPopup.IsVisible = false;
            _conversionCard?.ClearConversionProgress();
            _conversionCard = null;
            _conversionMinimized = false;
            _conversionInProgress = false;
            _conversionCancellation?.Dispose();
            _conversionCancellation = null;
        }
    }

    private async Task<AutomaticReaderFormatGenerationResult> AutoGenerateReaderFormatsForImportsAsync(
        ImportBatchResult importResult,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? requestedFormatsBySourcePath = null)
    {
        if (!_appSettings.AutoGenerateEpubAndAzw3OnImport)
            return new AutomaticReaderFormatGenerationResult(0, []);

        var books = importResult.Items
            .Where(item => item.Succeeded && item.Added && item.Book is not null)
            .Select(item => new
            {
                Book = item.Book!,
                Formats = requestedFormatsBySourcePath is null
                    ? (IReadOnlyCollection<string>)["epub", "azw3"]
                    : requestedFormatsBySourcePath.GetValueOrDefault(Path.GetFullPath(item.SourcePath), Array.Empty<string>())
            })
            .Where(item => item.Formats.Count > 0)
            .GroupBy(item => item.Book.Id)
            .Select(group => new
            {
                Book = group.First().Book,
                Formats = group.SelectMany(item => item.Formats).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            })
            .Where(item => BookFormatConversionPolicy.GetMissingDefaultReaderFormats(item.Book.Files).Count > 0)
            .ToArray();
        if (books.Length == 0)
            return new AutomaticReaderFormatGenerationResult(0, []);
        if (_conversionInProgress || _automaticReaderFormatGenerationInProgress)
            return new AutomaticReaderFormatGenerationResult(
                0,
                ["已有格式转换正在进行，未启动 EPUB/AZW3 自动补齐。"]);

        _automaticReaderFormatGenerationInProgress = true;
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "Kkindle", "automatic-formats", Guid.NewGuid().ToString("N"));
        var failures = new List<string>();
        var generatedCount = 0;
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            foreach (var item in books)
            {
                var book = item.Book;
                foreach (var targetFormat in BookFormatConversionPolicy.GetMissingDefaultReaderFormats(book.Files)
                             .Where(item.Formats.Contains))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourceFile = BookFormatConversionPolicy.SelectSource(book.Files, targetFormat);
                    if (sourceFile is null)
                    {
                        failures.Add($"《{book.Title}》没有可用于生成 {targetFormat.ToUpperInvariant()} 的源格式。");
                        continue;
                    }

                    var temporaryOutput = Path.Combine(temporaryRoot, $"{Guid.NewGuid():N}.{targetFormat}");
                    try
                    {
                        var sourcePath = _library.GetAbsoluteFilePath(sourceFile);
                        SetTaskStatus($"正在为《{book.Title}》生成 {targetFormat.ToUpperInvariant()}…");
                        await _formatConverter.ConvertAsync(
                            sourcePath,
                            temporaryOutput,
                            new Progress<FormatConversionProgress>(value =>
                                SetTaskStatus($"正在生成 {targetFormat.ToUpperInvariant()}：{book.Title}（{value.RoundedPercentage}%）")),
                            cancellationToken);
                        var addedFile = await _library.AddFileToBookAsync(book.Id, temporaryOutput, cancellationToken);
                        book.Files.Add(addedFile);
                        generatedCount++;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        failures.Add($"《{book.Title}》生成 {targetFormat.ToUpperInvariant()}：{exception.Message}");
                    }
                    finally
                    {
                        try { if (File.Exists(temporaryOutput)) File.Delete(temporaryOutput); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }

            if (generatedCount > 0)
                await ViewModel.RefreshAsync(cancellationToken);
            return new AutomaticReaderFormatGenerationResult(generatedCount, failures);
        }
        finally
        {
            _automaticReaderFormatGenerationInProgress = false;
            try { if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record AutomaticReaderFormatGenerationResult(
        int GeneratedCount,
        IReadOnlyList<string> Failures);

    private void ShowBookConversionPopup(string title, string sourceFormat, string targetFormat, FormatConversionProgress progress)
    {
        BookConversionPopupTitleText.Text = $"转换《{title}》";
        BookConversionPopupFormatText.Text = $"Calibre · {sourceFormat.ToUpperInvariant()} → {targetFormat.ToUpperInvariant()}";
        BookConversionPopup.IsVisible = true;
        ApplyBookConversionProgress(progress);
    }

    private void ApplyBookConversionProgress(FormatConversionProgress progress)
    {
        _conversionLastProgress = progress;
        if (!_conversionInProgress) return;
        var percentage = Math.Clamp(progress.Percentage, 0, 100);
        BookConversionPopupProgress.Value = percentage;
        BookConversionPopupPercentageText.Text = $"{progress.RoundedPercentage}%";
        BookConversionPopupMessageText.Text = GetBookConversionPopupMessage(progress);
        _conversionCard?.SetConversionProgress(progress, _conversionMinimized);
        SetTaskStatus($"格式转换：{progress.RoundedPercentage}%");
    }

    private static string GetBookConversionPopupMessage(FormatConversionProgress progress)
    {
        if (progress.Percentage <= 0 || progress.Percentage >= 100)
            return progress.Message;
        return "Calibre 正在转换…";
    }

    private void MinimizeBookConversionPopup()
    {
        if (!_conversionInProgress) return;
        _conversionMinimized = true;
        _conversionCard?.SetConversionProgress(_conversionLastProgress, showIndicator: true);
        BookConversionPopup.IsVisible = false;
        SetTaskStatus($"格式转换已在后台进行：{_conversionLastProgress.RoundedPercentage}%（点击书籍卡片可恢复进度）");
    }

    private void RestoreBookConversionPopup()
    {
        if (!_conversionInProgress) return;
        _conversionMinimized = false;
        _conversionCard?.SetConversionProgress(_conversionLastProgress, showIndicator: false);
        BookConversionPopup.IsVisible = true;
        ApplyBookConversionProgress(_conversionLastProgress);
    }

    private void BookConversionPopup_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Button) return;
        e.Handled = true;
        MinimizeBookConversionPopup();
    }

    private void BookConversionBackgroundButton_Click(object? sender, RoutedEventArgs e) =>
        MinimizeBookConversionPopup();

    private void BookConversionProgressIndicator_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: BookCardViewModel card }) return;
        if (!_conversionInProgress || _conversionCard?.Book.Id != card.Book.Id) return;
        e.Handled = true;
        _conversionCard = card;
        RestoreBookConversionPopup();
    }

    private void BookConversionCancelButton_Click(object? sender, RoutedEventArgs e) =>
        _conversionCancellation?.Cancel();

    private async Task MatchDoubanAsync(BookCardViewModel card)
    {
        if (_doubanMatchCancellation is not null)
        {
            SetTaskStatus("豆瓣匹配正在进行中，请稍候。");
            return;
        }
        if (!_appSettings.NetworkEnabled)
        {
            await ShowMessageAsync("网络功能已关闭", "请先在应用设置中允许网络功能，再使用豆瓣匹配。");
            return;
        }

        var cancellation = new CancellationTokenSource();
        _doubanMatchCancellation = cancellation;
        try
        {
            SetTaskStatus($"正在搜索《{card.Title}》的豆瓣信息…");
            ShowTaskProgressPopup();
            TaskProgressPopupBar.IsIndeterminate = true;
            TaskProgressPopupText.Text = $"正在搜索《{card.Title}》的豆瓣信息…";
            var candidates = await _douban.SearchAsync(
                card.Book.Title,
                card.Book.Authors,
                cancellation.Token);
            if (candidates.Count == 0)
            {
                SetTaskStatus("豆瓣没有返回匹配结果。");
                await ShowMessageAsync("没有找到", "豆瓣没有返回匹配条目。可以先修正本地书名或作者后再试。");
                return;
            }

            DoubanCandidates.Clear();
            foreach (var item in candidates)
                DoubanCandidates.Add(item);

            while (true)
            {
                var candidate = await ChooseDoubanCandidateAsync();
                if (candidate is null)
                {
                    SetTaskStatus("已取消豆瓣匹配。");
                    return;
                }
                _doubanSelectedCandidate = candidate;

                SetTaskStatus($"正在读取《{candidate.Title}》的豆瓣详情…");
                var metadata = await _douban.GetDetailsAsync(candidate, cancellation.Token);
                var choices = await ConfirmDoubanMetadataAsync(metadata, candidate);
                if (choices?.GoBack == true) continue;
                if (choices is null)
                {
                    SetTaskStatus("已取消豆瓣匹配。");
                    return;
                }

                var book = card.Book;
                if (choices.UpdateTitle && !string.IsNullOrWhiteSpace(metadata.Title)) book.Title = metadata.Title.Trim();
                if (choices.UpdateAuthors && !string.IsNullOrWhiteSpace(metadata.Authors)) book.Authors = metadata.Authors.Trim();
                if (choices.UpdateSeries && !string.IsNullOrWhiteSpace(metadata.Series)) book.Series = metadata.Series.Trim();
                if (choices.UpdateDescription && !string.IsNullOrWhiteSpace(metadata.Description)) book.Description = metadata.Description.Trim();
                if (choices.UpdatePublication)
                {
                    if (!string.IsNullOrWhiteSpace(metadata.Publisher)) book.Publisher = metadata.Publisher.Trim();
                    if (!string.IsNullOrWhiteSpace(metadata.PublishDate)) book.PublishDate = metadata.PublishDate.Trim();
                    if (!string.IsNullOrWhiteSpace(metadata.Isbn)) book.Isbn = metadata.Isbn.Trim();
                    if (!string.IsNullOrWhiteSpace(metadata.Pages)) book.PageCount = metadata.Pages.Trim();
                    if (!string.IsNullOrWhiteSpace(metadata.Binding)) book.Binding = metadata.Binding.Trim();
                    if (metadata.Rating is not null) book.DoubanRating = metadata.Rating;
                    book.DoubanRatingCount = metadata.RatingCount;
                }

                if (choices.UpdateCover && !string.IsNullOrWhiteSpace(metadata.CoverUrl))
                {
                    try
                    {
                        SetTaskStatus("正在下载并保存豆瓣封面…");
                        var coverBytes = await _douban.DownloadCoverAsync(metadata.CoverUrl, cancellation.Token);
                        _paths.EnsureDirectories();
                        var coverName = $"{book.Id:N}-douban.jpg";
                        var coverPath = Path.Combine(_paths.Covers, coverName);
                        var temporaryPath = coverPath + ".tmp";
                        await File.WriteAllBytesAsync(temporaryPath, coverBytes, cancellation.Token);
                        File.Move(temporaryPath, coverPath, overwrite: true);
                        book.CoverPath = Path.GetRelativePath(_paths.Data, coverPath);
                    }
                    catch (Exception exception)
                    {
                        SetTaskStatus($"豆瓣信息已读取，但封面下载失败：{exception.Message}");
                    }
                }

                await _library.UpdateMetadataAsync(book, _lifetimeCancellation.Token);
                await RefreshLibraryAsync();
                SetTaskStatus($"已用豆瓣信息更新《{book.Title}》。");
                return;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetTaskStatus("豆瓣匹配已取消。");
        }
        catch (Exception exception)
        {
            SetTaskStatus($"豆瓣匹配失败：{exception.Message}");
            await ShowMessageAsync("豆瓣匹配失败", exception.Message);
        }
        finally
        {
            TaskProgressPopupBar.IsIndeterminate = false;
            HideTaskProgressPopup();
            DoubanCandidateOverlay.IsVisible = false;
            DoubanPreviewOverlay.IsVisible = false;
            _doubanApplyCompletion?.TrySetResult(null);
            _doubanApplyCompletion = null;
            _doubanPreviewMetadata = null;
            _doubanSelectedCandidate = null;
            DoubanPreviewCoverImage.Source = null;
            if (ReferenceEquals(_doubanMatchCancellation, cancellation)) _doubanMatchCancellation = null;
            cancellation.Dispose();
        }
    }

    private Task<DoubanBookCandidate?> ChooseDoubanCandidateAsync()
    {
        DoubanPreviewOverlay.IsVisible = false;
        DoubanCandidateList.SelectedIndex = DoubanCandidates.Count > 0 ? 0 : -1;
        SetDoubanCandidateButtonsEnabled(DoubanCandidateList.SelectedItem is DoubanBookCandidate);
        ShowOverlay(DoubanCandidateOverlay);
        _doubanCandidateCompletion?.TrySetResult(null);
        _doubanCandidateCompletion = new TaskCompletionSource<DoubanBookCandidate?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return _doubanCandidateCompletion.Task;
    }

    private Task<DoubanUpdateChoices?> ConfirmDoubanMetadataAsync(
        DoubanBookMetadata metadata,
        DoubanBookCandidate candidate)
    {
        _doubanPreviewMetadata = metadata;
        DoubanCandidateOverlay.IsVisible = false;
        DoubanPreviewSummaryText.Text = BuildDoubanSummary(metadata);
        DoubanPreviewStatusText.Text = "未勾选的本地字段不会被修改";
        DoubanPreviewCoverImage.Source = null;

        DoubanUpdateTitleCheck.IsChecked = !string.IsNullOrWhiteSpace(metadata.Title);
        DoubanUpdateAuthorsCheck.IsChecked = !string.IsNullOrWhiteSpace(metadata.Authors);
        DoubanUpdateSeriesCheck.IsChecked = !string.IsNullOrWhiteSpace(metadata.Series);
        DoubanUpdateSeriesCheck.IsEnabled = !string.IsNullOrWhiteSpace(metadata.Series);
        DoubanUpdateDescriptionCheck.IsChecked = !string.IsNullOrWhiteSpace(metadata.Description);
        DoubanUpdateDescriptionCheck.IsEnabled = !string.IsNullOrWhiteSpace(metadata.Description);
        DoubanUpdateCoverCheck.IsChecked = !string.IsNullOrWhiteSpace(metadata.CoverUrl);
        DoubanUpdateCoverCheck.IsEnabled = !string.IsNullOrWhiteSpace(metadata.CoverUrl);
        var hasPublicationData = !string.IsNullOrWhiteSpace(metadata.Publisher)
            || !string.IsNullOrWhiteSpace(metadata.PublishDate)
            || !string.IsNullOrWhiteSpace(metadata.Isbn)
            || !string.IsNullOrWhiteSpace(metadata.Pages)
            || !string.IsNullOrWhiteSpace(metadata.Binding)
            || metadata.Rating is not null;
        DoubanUpdatePublicationCheck.IsChecked = hasPublicationData;
        DoubanUpdatePublicationCheck.IsEnabled = hasPublicationData;

        ShowOverlay(DoubanPreviewOverlay);
        DoubanPreviewOverlay.Focus();

        // Candidate covers are decorative; a failure must never block the
        // metadata confirmation flow.
        if (!string.IsNullOrWhiteSpace(candidate.CoverUrl) && _doubanMatchCancellation is { } cancellation)
        {
            _ = LoadDoubanPreviewCoverAsync(candidate.CoverUrl, cancellation.Token);
        }

        _doubanApplyCompletion?.TrySetResult(null);
        _doubanApplyCompletion = new TaskCompletionSource<DoubanUpdateChoices?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return _doubanApplyCompletion.Task;
    }

    private async Task LoadDoubanPreviewCoverAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await _douban.DownloadCoverAsync(url, cancellationToken);
            if (bytes.Length == 0) return;
            await using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new Bitmap(stream);
            if (!ReferenceEquals(_doubanPreviewMetadata, null))
                DoubanPreviewCoverImage.Source = bitmap;
            else
                bitmap.Dispose();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Covers are decorative; never fail the preview for a bad image.
        }
    }

    private static string BuildDoubanSummary(DoubanBookMetadata metadata)
    {
        static string Fallback(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();
        var rows = new List<string>
        {
            $"书名：{metadata.Title}",
            $"作者：{Fallback(metadata.Authors)}",
            $"译者：{Fallback(metadata.Translators)}",
            $"出版社：{Fallback(metadata.Publisher)}",
            $"出版年：{Fallback(metadata.PublishDate)}",
            $"ISBN：{Fallback(metadata.Isbn)}",
            $"页数 / 装帧：{Fallback(metadata.Pages)} / {Fallback(metadata.Binding)}",
            $"定价：{Fallback(metadata.Price)}",
            $"系列：{Fallback(metadata.Series)}",
            metadata.Rating is null ? "豆瓣评分：暂无" : $"豆瓣评分：{metadata.Rating:0.0}（{metadata.RatingCount} 人评价）",
            string.Empty,
            $"简介：{Fallback(metadata.Description)}"
        };
        return string.Join(Environment.NewLine, rows);
    }

    private void DoubanCandidateList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DoubanCandidateList.SelectedItem is DoubanBookCandidate candidate)
        {
            _doubanSelectedCandidate = candidate;
            SetDoubanCandidateButtonsEnabled(true);
        }
        else
        {
            SetDoubanCandidateButtonsEnabled(false);
        }
    }

    private void SetDoubanCandidateButtonsEnabled(bool enabled)
    {
        DoubanCandidateApplyButton.IsEnabled = enabled;
        DoubanCandidateSourceButton.IsEnabled = enabled;
    }

    private void DoubanCandidateList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DoubanCandidateList.SelectedItem is DoubanBookCandidate)
            DoubanCandidateApplyButton_Click(sender, new RoutedEventArgs());
    }

    private void DoubanCandidateSourceButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DoubanCandidateList.SelectedItem is not DoubanBookCandidate candidate) return;
        OpenDoubanUrl(candidate.Url);
    }

    private void DoubanPreviewSourceButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_doubanPreviewMetadata is null) return;
        OpenDoubanUrl(_doubanPreviewMetadata.Url);
    }

    private void OpenDoubanUrl(string? url)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(url))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetTaskStatus($"无法打开豆瓣详情页：{exception.Message}");
        }
    }

    private void DoubanCandidateApplyButton_Click(object? sender, RoutedEventArgs e)
    {
        var candidate = DoubanCandidateList.SelectedItem as DoubanBookCandidate;
        DoubanCandidateOverlay.IsVisible = false;
        _doubanCandidateCompletion?.TrySetResult(candidate);
        _doubanCandidateCompletion = null;
    }

    private void DoubanCandidateCancelButton_Click(object? sender, RoutedEventArgs e)
    {
        DoubanCandidateOverlay.IsVisible = false;
        DoubanPreviewOverlay.IsVisible = false;
        _doubanCandidateCompletion?.TrySetResult(null);
        _doubanApplyCompletion?.TrySetResult(null);
        _doubanCandidateCompletion = null;
        _doubanApplyCompletion = null;
        _doubanMatchCancellation?.Cancel();
    }

    private void DoubanPreviewApplyButton_Click(object? sender, RoutedEventArgs e)
    {
        DoubanPreviewOverlay.IsVisible = false;
        _doubanApplyCompletion?.TrySetResult(new DoubanUpdateChoices(
            GoBack: false,
            UpdateTitle: DoubanUpdateTitleCheck.IsChecked == true,
            UpdateAuthors: DoubanUpdateAuthorsCheck.IsChecked == true,
            UpdateSeries: DoubanUpdateSeriesCheck.IsChecked == true,
            UpdateDescription: DoubanUpdateDescriptionCheck.IsChecked == true,
            UpdateCover: DoubanUpdateCoverCheck.IsChecked == true,
            UpdatePublication: DoubanUpdatePublicationCheck.IsChecked == true));
        _doubanApplyCompletion = null;
    }

    private void DoubanPreviewBackButton_Click(object? sender, RoutedEventArgs e)
    {
        DoubanPreviewOverlay.IsVisible = false;
        _doubanApplyCompletion?.TrySetResult(new DoubanUpdateChoices(
            GoBack: true,
            UpdateTitle: false,
            UpdateAuthors: false,
            UpdateSeries: false,
            UpdateDescription: false,
            UpdateCover: false,
            UpdatePublication: false));
        _doubanApplyCompletion = null;
    }

    private void DoubanPreviewOverlay_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        DoubanCandidateCancelButton_Click(sender, new RoutedEventArgs());
    }

    private sealed record DoubanUpdateChoices(
        bool GoBack,
        bool UpdateTitle,
        bool UpdateAuthors,
        bool UpdateSeries,
        bool UpdateDescription,
        bool UpdateCover,
        bool UpdatePublication);

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        if (_confirmationCompletion is not null) return false;
        ConfirmationTitleText.Text = title;
        ConfirmationMessageText.Text = message;
        ConfirmationOkButton.Content = title.Contains("删除", StringComparison.Ordinal) ? "确认删除" : "应用";
        ShowOverlay(ConfirmationOverlay);
        _confirmationCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = _confirmationCompletion;
        var confirmed = await completion.Task;
        if (ReferenceEquals(_confirmationCompletion, completion))
            _confirmationCompletion = null;
        return confirmed;
    }

    // Fade-in helpers: the overlays and pages carry an Opacity transition in
    // XAML; showing them at 0 and restoring 1 on the next frame animates the
    // entrance instead of popping it in.
    private static void ShowOverlay(Control overlay)
    {
        overlay.IsVisible = true;
        overlay.Opacity = 0;
        Dispatcher.UIThread.Post(() => overlay.Opacity = 1);
    }

    private static void FadeInPage(Control page)
    {
        page.IsVisible = true;
        page.Opacity = 0;
        Dispatcher.UIThread.Post(() => page.Opacity = 1);
    }

    // Monochrome information dialog (WinUI ShowMessageAsync). Fire-and-forget
    // callers can use "_ = ShowMessageAsync(...)"; the awaited task completes
    // when the user dismisses the dialog.
    private Task ShowMessageAsync(string title, string message)
    {
        MessageTitleText.Text = title;
        MessageBodyText.Text = message;
        ShowOverlay(MessageOverlay);
        MessageOverlay.Focus();
        _messageCompletion?.TrySetResult(true);
        _messageCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _messageCompletion.Task;
    }

    private void MessageOkButton_Click(object? sender, RoutedEventArgs e) => CompleteMessage();

    private void MessageOverlay_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.Enter)) return;
        e.Handled = true;
        CompleteMessage();
    }

    private void CompleteMessage()
    {
        MessageOverlay.IsVisible = false;
        var completion = _messageCompletion;
        _messageCompletion = null;
        completion?.TrySetResult(true);
    }

    private Task<string?> PromptCollectionNameAsync()
    {
        if (_collectionNameCompletion is not null) return Task.FromResult<string?>(null);
        CollectionNameBox.Text = string.Empty;
        ShowOverlay(CollectionNameOverlay);
        CollectionNameBox.Focus();
        _collectionNameCompletion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _collectionNameCompletion.Task;
    }

    private void CompleteConfirmation(bool confirmed)
    {
        ConfirmationOverlay.IsVisible = false;
        _confirmationCompletion?.TrySetResult(confirmed);
    }

    private void CompleteCollectionName(string? name)
    {
        CollectionNameOverlay.IsVisible = false;
        var completion = _collectionNameCompletion;
        _collectionNameCompletion = null;
        completion?.TrySetResult(name);
    }

    private async Task CreateCollectionAsync()
    {
        var name = await PromptCollectionNameAsync();
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            await _library.CreateCollectionAsync(name, _lifetimeCancellation.Token);
            await RefreshCollectionsAsync();
            SetLibraryViewMode(LibraryViewMode.Collections);
            SetTaskStatus($"已创建收藏夹“{name.Trim()}”。");
        }
        catch (Exception exception)
        {
            SetTaskStatus($"创建收藏夹失败：{exception.Message}");
            await ShowMessageAsync("无法创建收藏夹", exception.Message);
        }
    }

    private async Task DeleteCollectionAsync(BookCollectionFolderViewModel folder)
    {
        if (!await ConfirmAsync("删除收藏夹", $"确定删除收藏夹“{folder.Name}”吗？书籍文件不会被删除。")) return;

        try
        {
            await _library.DeleteCollectionAsync(folder.Collection.Id, _lifetimeCancellation.Token);
            if (ViewModel.CollectionFilterId == folder.Collection.Id)
            {
                ViewModel.CollectionFilterId = null;
                ViewModel.CollectionFilterName = null;
            }
            await RefreshLibraryAsync();
            SetLibraryViewMode(LibraryViewMode.Collections);
            SetTaskStatus($"已删除收藏夹“{folder.Name}”。");
        }
        catch (Exception exception)
        {
            SetTaskStatus($"删除收藏夹失败：{exception.Message}");
            await ShowMessageAsync("无法删除收藏夹", exception.Message);
        }
    }

    private async Task ToggleCollectionMembershipAsync()
    {
        if (_selectedCard is null) return;
        if (DetailCollectionBox.SelectedItem is not BookCollectionFolderViewModel folder)
        {
            SetTaskStatus("请先选择一个收藏夹。");
            return;
        }

        try
        {
            var bookId = _selectedCard.Book.Id;
            if (_selectedCard.Book.CollectionIds.Contains(folder.Collection.Id))
            {
                await _library.RemoveBookFromCollectionAsync(bookId, folder.Collection.Id, _lifetimeCancellation.Token);
                SetTaskStatus($"已从“{folder.Name}”移出。 ");
            }
            else
            {
                await _library.AddBookToCollectionAsync(bookId, folder.Collection.Id, _lifetimeCancellation.Token);
                SetTaskStatus($"已加入“{folder.Name}”。");
            }

            await RefreshLibraryAsync();
        }
        catch (Exception exception)
        {
            SetTaskStatus($"更新收藏夹失败：{exception.Message}");
            await ShowMessageAsync("无法更新收藏夹", exception.Message);
        }
    }

    private void ShowAllBooksButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ShowLibraryPage();
        ViewModel.CollectionFilterId = null;
        ViewModel.CollectionFilterName = null;
        ViewModel.RefreshView();
        SetLibraryViewMode(LibraryViewMode.Grid);
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        ViewModel.SearchText = SearchBox.Text ?? string.Empty;
        ViewModel.RefreshView();
        UpdateLibraryUi();
    }

    private void FilterButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (FilterPanel.IsVisible)
        {
            FilterPanel.IsVisible = false;
            return;
        }
        FilterPanel.IsVisible = true;
        FilterPanel.MaxHeight = 0;
        FilterPanel.Opacity = 0;
        Dispatcher.UIThread.Post(() =>
        {
            FilterPanel.MaxHeight = 80;
            FilterPanel.Opacity = 1;
        });
    }

    private void LibraryViewMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        switch (tag)
        {
            case "List":
                SetLibraryViewMode(LibraryViewMode.List);
                break;
            case "Collections":
                SetLibraryViewMode(LibraryViewMode.Collections);
                break;
            default:
                SetLibraryViewMode(LibraryViewMode.Grid);
                break;
        }
    }

    private void BackToCollectionsButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.CollectionFilterId = null;
        ViewModel.CollectionFilterName = null;
        ViewModel.RefreshView();
        SetLibraryViewMode(LibraryViewMode.Collections);
    }

    private async void CreateCollectionButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await CreateCollectionAsync();

    private void ImportButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var importFiles = new MenuItem { Header = "导入文件" };
        importFiles.Click += ImportFilesButton_Click;
        menu.Items.Add(importFiles);

        var importFolder = new MenuItem { Header = "导入文件夹" };
        importFolder.Click += ImportFolderButton_Click;
        menu.Items.Add(importFolder);

        menu.Items.Add(new Separator());
        var importBackup = new MenuItem { Header = "导入备份" };
        importBackup.Click += ImportBackupButton_Click;
        menu.Items.Add(importBackup);

        menu.Open(ImportButton);
    }

    private async void ImportFilesButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入书籍文件",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("电子书")
                {
                    Patterns = ["*.epub", "*.pdf", "*.mobi", "*.azw3"]
                }
            ]
        });
        await ImportPathsAsync(files
            .Select(file => file.TryGetLocalPath())
            .Where(path => path is not null)
            .Select(path => path!));
    }

    private async void ImportFolderButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "导入书籍文件夹",
            AllowMultiple = false
        });
        await ImportPathsAsync(folders
            .Select(folder => folder.TryGetLocalPath())
            .Where(path => path is not null)
            .Select(path => path!));
    }

    private void FilterComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_filterControlsReady || _updatingFilterControls) return;
        ViewModel.AuthorFilter = AuthorFilterBox.SelectedIndex <= 0 ? null : AuthorFilterBox.SelectedItem as string;
        ViewModel.TagFilter = TagFilterBox.SelectedIndex <= 0 ? null : TagFilterBox.SelectedItem as string;
        ViewModel.FormatFilter = FormatFilterBox.SelectedIndex <= 0 ? null : FormatFilterBox.SelectedItem as string;
        ViewModel.CategoryFilter = CategoryFilterBox.SelectedIndex <= 0 ? null : CategoryFilterBox.SelectedItem as string;
        ViewModel.ReadingStatusFilter = ReadingStatusFilterBox.SelectedIndex <= 0
            ? null
            : (LibraryReadingStatus)(ReadingStatusFilterBox.SelectedIndex - 1);
        ViewModel.SortMode = LibrarySortBox.SelectedIndex < 0
            ? LibrarySortMode.UpdatedDescending
            : (LibrarySortMode)LibrarySortBox.SelectedIndex;
        ViewModel.RefreshView();
        UpdateLibraryUi();
    }

    private void FavoritesOnlyCheckBox_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!_filterControlsReady || _updatingFilterControls) return;
        ViewModel.FavoritesOnly = FavoritesOnlyCheckBox.IsChecked == true;
        ViewModel.RefreshView();
        UpdateLibraryUi();
    }

    private void ClearFiltersButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _updatingFilterControls = true;
        try
        {
            ViewModel.SearchText = string.Empty;
            ViewModel.AuthorFilter = null;
            ViewModel.TagFilter = null;
            ViewModel.FormatFilter = null;
            ViewModel.CategoryFilter = null;
            ViewModel.ReadingStatusFilter = null;
            ViewModel.FavoritesOnly = false;
            ViewModel.CollectionFilterId = null;
            ViewModel.CollectionFilterName = null;
            ViewModel.SortMode = LibrarySortMode.UpdatedDescending;
            SearchBox.Text = string.Empty;
            AuthorFilterBox.SelectedIndex = 0;
            TagFilterBox.SelectedIndex = 0;
            FormatFilterBox.SelectedIndex = 0;
            CategoryFilterBox.SelectedIndex = 0;
            ReadingStatusFilterBox.SelectedIndex = 0;
            LibrarySortBox.SelectedIndex = 0;
            FavoritesOnlyCheckBox.IsChecked = false;
            ViewModel.RefreshView();
            SetLibraryViewMode(LibraryViewMode.Grid);
        }
        finally
        {
            _updatingFilterControls = false;
        }
    }

    private void BookList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        foreach (var card in e.RemovedItems.OfType<BookCardViewModel>())
            _selectedBookIds.Remove(card.Book.Id);
        foreach (var card in e.AddedItems.OfType<BookCardViewModel>())
            _selectedBookIds.Add(card.Book.Id);
        var selectedCard = e.AddedItems.OfType<BookCardViewModel>().FirstOrDefault();
        if (selectedCard is not null) SelectBook(selectedCard);
        UpdateMultiSelectionUi();
    }

    private void BookCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not BookCardViewModel card)
            return;
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;

        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            if (_selectedBookIds.Remove(card.Book.Id))
                card.IsMultiSelected = false;
            else
                _selectedBookIds.Add(card.Book.Id);
            _selectedCard = card;
            _multiSelectAnchor = card;
        }
        else if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
        {
            ApplyCardRangeSelection(card);
        }
        else
        {
            _selectedBookIds.Clear();
            _selectedBookIds.Add(card.Book.Id);
            _selectedCard = card;
            _multiSelectAnchor = card;
            SelectBook(card);
        }

        UpdateMultiSelectionUi();
        e.Handled = true;
    }

    // 悬停整张卡片（封面或文字区）时显示黑色细边框，与选中态一致。
    private void BookCard_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: BookCardViewModel card })
            card.IsHovered = true;
    }

    private void BookCard_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: BookCardViewModel card })
            card.IsHovered = false;
    }

    private void BookGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!BookGrid.IsVisible
            || !e.GetCurrentPoint(BookGrid).Properties.IsLeftButtonPressed)
            return;

        // 按下发生在书籍卡片上时由 BookCard_PointerPressed 处理点选/多选，
        // 松开时不得再清空选择（否则点选的黑边在松开瞬间被抹掉）。
        _rubberBandPressedOnCard = IsBookCardSource(e.Source);
        if (_rubberBandPressedOnCard) return;

        _rubberBandStart = e.GetPosition(BookGrid);
        _rubberBandCurrent = _rubberBandStart;
        _rubberBandSelecting = false;
        BookGrid.Focus();
    }

    private void BookGrid_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(BookGrid).Properties.IsLeftButtonPressed) return;
        _rubberBandCurrent = e.GetPosition(BookGrid);
        if (!_rubberBandSelecting)
        {
            // 多选框选只允许“从右往左”拖拽（水平向左超过 8 DIP），避免
            // 点选时轻微晃动被误识别成多选。
            if (_rubberBandCurrent.X >= _rubberBandStart.X - 8)
                return;
            _rubberBandSelecting = true;
            e.Pointer.Capture(BookGrid);
            RubberBandRectangle.IsVisible = true;
        }
        UpdateRubberBandSelection();
        e.Handled = true;
    }

    private void BookGrid_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_rubberBandSelecting)
        {
            // 按在卡片上松开：点选结果已由 BookCard_PointerPressed 维护。
            // 按在空白处松开：取消全部选择。
            if (!_rubberBandPressedOnCard)
            {
                _selectedBookIds.Clear();
                _multiSelectAnchor = null;
                UpdateMultiSelectionUi();
            }
            _rubberBandPressedOnCard = false;
            return;
        }
        _rubberBandCurrent = e.GetPosition(BookGrid);
        UpdateRubberBandSelection();
        FinishRubberBandSelection(e.Pointer);
        _rubberBandPressedOnCard = false;
        e.Handled = true;
    }

    private void BookGrid_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_rubberBandSelecting) FinishRubberBandSelection(null);
        _rubberBandPressedOnCard = false;
    }

    private void BookGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        _selectedBookIds.Clear();
        _multiSelectAnchor = null;
        BookGrid.SelectedItems?.Clear();
        UpdateMultiSelectionUi();
    }

    private void UpdateRubberBandSelection()
    {
        var left = Math.Min(_rubberBandStart.X, _rubberBandCurrent.X);
        var top = Math.Min(_rubberBandStart.Y, _rubberBandCurrent.Y);
        var width = Math.Abs(_rubberBandCurrent.X - _rubberBandStart.X);
        var height = Math.Abs(_rubberBandCurrent.Y - _rubberBandStart.Y);
        Canvas.SetLeft(RubberBandRectangle, left);
        Canvas.SetTop(RubberBandRectangle, top);
        RubberBandRectangle.Width = width;
        RubberBandRectangle.Height = height;

        var selection = new Rect(left, top, width, height);
        _selectedBookIds.Clear();
        foreach (var card in ViewModel.Books)
        {
            if (BookGrid.ContainerFromItem(card) is not Control container) continue;
            var origin = container.TranslatePoint(default, BookGrid);
            if (origin is not { } point) continue;
            var bounds = new Rect(point, container.Bounds.Size);
            if (bounds.Intersects(selection)) _selectedBookIds.Add(card.Book.Id);
        }
        _multiSelectAnchor = ViewModel.Books.FirstOrDefault(card => _selectedBookIds.Contains(card.Book.Id));
        UpdateMultiSelectionUi();
    }

    private void FinishRubberBandSelection(IPointer? pointer)
    {
        _rubberBandSelecting = false;
        pointer?.Capture(null);
        RubberBandRectangle.IsVisible = false;
        UpdateMultiSelectionUi();
    }

    private void ApplyCardRangeSelection(BookCardViewModel clicked)
    {
        var cards = ViewModel.Books.ToList();
        var clickedIndex = cards.FindIndex(card => ReferenceEquals(card, clicked));
        if (clickedIndex < 0) return;

        var anchorIndex = _multiSelectAnchor is null
            ? -1
            : cards.FindIndex(card => ReferenceEquals(card, _multiSelectAnchor));
        _selectedBookIds.Clear();

        var start = anchorIndex < 0 ? clickedIndex : Math.Min(anchorIndex, clickedIndex);
        var end = anchorIndex < 0 ? clickedIndex : Math.Max(anchorIndex, clickedIndex);
        for (var index = start; index <= end; index++)
            _selectedBookIds.Add(cards[index].Book.Id);

        _selectedCard = clicked;
        _multiSelectAnchor = clicked;
        SelectBook(clicked);
    }

    private async void BookCard_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not BookCardViewModel card) return;
        _selectedBookIds.Clear();
        _selectedBookIds.Add(card.Book.Id);
        _selectedCard = card;
        _multiSelectAnchor = card;
        UpdateMultiSelectionUi();
        SelectBook(card);
        await OpenBookAsync(card);
    }

    private async void OpenCardButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is BookCardViewModel card)
        {
            SelectBook(card);
            await OpenBookAsync(card);
        }
    }

    private void CollectionFolder_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: BookCollectionFolderViewModel folder }) return;
        ViewModel.CollectionFilterId = folder.Collection.Id;
        ViewModel.CollectionFilterName = folder.Name;
        ViewModel.RefreshView();
        SetLibraryViewMode(LibraryViewMode.Grid);
        e.Handled = true;
    }

    // Right-clicking a collection card opens the delete action (WinUI
    // reference); right-clicking empty space in the collections view offers
    // the create action.
    private void CollectionFolder_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: BookCollectionFolderViewModel folder } control) return;
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem("删除收藏夹", () => DeleteCollectionAsync(folder)));
        menu.Open(control);
        e.Handled = true;
    }

    private void CollectionScroll_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is Control { DataContext: BookCollectionFolderViewModel })
            return;
        if (sender is not Control source) return;
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem("创建收藏夹", CreateCollectionAsync));
        menu.Open(source);
        e.Handled = true;
    }

    private async void OpenSelectedBookButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedCard is not null)
            await OpenBookAsync(_selectedCard);
    }

    private async void DeleteSelectedBookButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await DeleteSelectedBookAsync();

    private async void DeleteSelectedBooksButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await DeleteSelectedBooksAsync();

    private void ClearMultiSelectionButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _selectedBookIds.Clear();
        _multiSelectAnchor = null;
        BookGrid.SelectedItems?.Clear();
        BookList.SelectedItems?.Clear();
        UpdateMultiSelectionUi();
    }

    private async void OpenFileButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedCard is not null && (sender as Button)?.Tag is BookFile file)
            await OpenBookAsync(_selectedCard, file);
    }

    private async void DeleteFileButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is BookFile file)
            await DeleteFileAsync(file);
    }

    private async void CollectionMembershipButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!_updatingDetails)
            await ToggleCollectionMembershipAsync();
    }

    private async void DetailFavoriteButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedCard is not null)
            await ToggleFavoriteAsync(_selectedCard);
    }

    private async void DetailReadingStatusButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedCard is null) return;
        var nextStatus = _selectedCard.Book.ReadingStatus switch
        {
            LibraryReadingStatus.Unread => LibraryReadingStatus.Reading,
            LibraryReadingStatus.Reading => LibraryReadingStatus.Finished,
            _ => LibraryReadingStatus.Unread
        };
        await UpdateReadingStatusAsync(_selectedCard, nextStatus);
    }

    private async void SaveDetailsButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedCard is null) return;
        var book = _selectedCard.Book;
        book.Title = string.IsNullOrWhiteSpace(DetailTitleText.Text) ? book.Title : DetailTitleText.Text.Trim();
        book.Authors = string.IsNullOrWhiteSpace(DetailAuthorsText.Text) ? book.Authors : DetailAuthorsText.Text.Trim();
        book.Tags = DetailTagsBox.Text?.Trim() ?? string.Empty;
        book.Category = DetailCategoryBox.Text?.Trim() ?? string.Empty;
        book.Description = DetailDescriptionBox.Text?.Trim();
        book.Series = DetailSeriesBox.Text?.Trim();
        book.Publisher = DetailPublisherBox.Text?.Trim();
        book.PublishDate = DetailPublishDateBox.Text?.Trim();
        book.Isbn = DetailIsbnBox.Text?.Trim();
        book.PageCount = DetailPageCountBox.Text?.Trim();
        book.Binding = DetailBindingBox.Text?.Trim();
        await SaveBookMetadataAsync(_selectedCard, "书籍信息已保存。");
    }

    private async void DoubanMatchButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedCard is not null)
            await MatchDoubanAsync(_selectedCard);
    }

    private void ConfirmationCancelButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => CompleteConfirmation(false);

    private void ConfirmationOkButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => CompleteConfirmation(true);

    private void CollectionNameCancelButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => CompleteCollectionName(null);

    private async void CollectionNameOkButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var name = CollectionNameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            SetTaskStatus("收藏夹名称不能为空。");
            await ShowMessageAsync("名称不能为空", "请输入收藏夹名称。");
            return;
        }
        CompleteCollectionName(name);
    }

    private void TitleBarDragRegion_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximized();
            return;
        }
        BeginMoveDrag(e);
    }

    private void MinimizeWindowButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeWindowButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ToggleMaximized();

    private void CloseWindowButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Close();

    private void ToggleMaximized()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateMaximizeGlyph()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeWindowGlyph.Data = Geometry.Parse(isMaximized ? RestoreGlyphData : MaximizeGlyphData);
        AutomationProperties.SetName(MaximizeWindowButton, isMaximized ? "还原" : "最大化");
    }

    private void LibraryRoot_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (LibraryRoot.ColumnDefinitions.Count < 3) return;
        var width = e.NewSize.Width;
        // The reference keeps a 200px sidebar and reserves a 320px details
        // column only when a book is selected. Narrow windows collapse the
        // details surface so the library remains usable.
        LibraryRoot.ColumnDefinitions[0].Width = new GridLength(200);
        if (_settingsPanelVisible)
        {
            LibraryRoot.ColumnDefinitions[1].Width = new GridLength(0);
            LibraryRoot.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            return;
        }
        LibraryRoot.ColumnDefinitions[2].Width = _selectedCard is not null && width >= 1040
            ? new GridLength(320)
            : new GridLength(0);
    }

    private void SidebarSectionButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, BookManagementSectionButton))
            BookManagementChildren.IsVisible = !BookManagementChildren.IsVisible;
        else if (ReferenceEquals(sender, DeviceManagementSectionButton))
            DeviceManagementChildren.IsVisible = !DeviceManagementChildren.IsVisible;
        else if (ReferenceEquals(sender, ReadingSectionButton))
            ReadingChildren.IsVisible = !ReadingChildren.IsVisible;
        else if (ReferenceEquals(sender, SystemSectionButton))
            SystemChildren.IsVisible = !SystemChildren.IsVisible;

        UpdateSidebarSectionVisuals();
    }

    private void UpdateSidebarSectionVisuals()
    {        BookManagementChevron.Data = Geometry.Parse(
            BookManagementChildren.IsVisible ? SidebarChevronDownData : SidebarChevronRightData);
        DeviceManagementChevron.Data = Geometry.Parse(
            DeviceManagementChildren.IsVisible ? SidebarChevronDownData : SidebarChevronRightData);
        ReadingChevron.Data = Geometry.Parse(
            ReadingChildren.IsVisible ? SidebarChevronDownData : SidebarChevronRightData);
        SystemChevron.Data = Geometry.Parse(
            SystemChildren.IsVisible ? SidebarChevronDownData : SidebarChevronRightData);

        var ink = Application.Current?.Resources["InkBrush"] as IBrush ?? Brushes.Black;
        var muted = Application.Current?.Resources["MutedInkBrush"] as IBrush ?? Brushes.Gray;
        var sections = new[]
        {
            (Button: BookManagementSectionButton, Chevron: BookManagementChevron),
            (Button: DeviceManagementSectionButton, Chevron: DeviceManagementChevron),
            (Button: ReadingSectionButton, Chevron: ReadingChevron),
            (Button: SystemSectionButton, Chevron: SystemChevron)
        };
        foreach (var section in sections)
        {
            var active = ReferenceEquals(section.Button, _activeNavigationSectionButton);
            section.Button.Classes.Set("active", active);
            section.Chevron.Stroke = active ? ink : muted;
        }
    }

    // Interactive-control tool tips, mirroring the WinUI ToolTips.cs pass: the
    // first time the window opens, every Button/TextBox/ComboBox/NumberBox/
    // Slider/ToggleSwitch/CheckBox without an explicit tool tip gets one built
    // from its accessible name, content text or placeholder.
    private bool _interactiveToolTipsApplied;

    private void EnsureInteractiveControlToolTips()
    {
        if (_interactiveToolTipsApplied) return;
        _interactiveToolTipsApplied = true;
        ApplyInteractiveControlToolTips(this);
    }

    private static void ApplyInteractiveControlToolTips(Visual root)
    {
        if (root is Control control)
        {
            if (ToolTip.GetTip(control) is null)
            {
                var text = BuildControlToolTip(control);
                if (!string.IsNullOrWhiteSpace(text))
                    ToolTip.SetTip(control, text);
            }
            foreach (var child in root.GetVisualChildren())
                ApplyInteractiveControlToolTips(child);
        }
    }

    private static string? BuildControlToolTip(Control control)
    {
        var accessibleName = AutomationProperties.GetName(control);
        return control switch
        {
            ToggleSwitch toggleSwitch => DescribeField(accessibleName, "切换开关"),
            CheckBox checkBox => DescribeField(accessibleName, "切换选项"),
            Button button => FirstNonEmpty(accessibleName, ReadContentText(button.Content)),
            ComboBox comboBox => DescribeField(accessibleName, "选择选项"),
            NumericUpDown numberBox => DescribeField(accessibleName, "输入或调整数值"),
            TextBox textBox => DescribeField(
                accessibleName,
                string.IsNullOrWhiteSpace(textBox.PlaceholderText) ? "输入文本" : textBox.PlaceholderText),
            Slider slider => DescribeField(accessibleName, "拖动以调整数值"),
            _ => null
        };
    }

    private static string? DescribeField(string? accessibleName, string action)
    {
        return string.IsNullOrWhiteSpace(accessibleName) ? action : $"{accessibleName}：{action}";
    }

    private static string? ReadContentText(object? content) => content switch
    {
        string text => text.Trim(),
        TextBlock textBlock => textBlock.Text?.Trim(),
        _ => null
    };

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
