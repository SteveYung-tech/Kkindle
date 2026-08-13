using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class MainWindow : Window
{
    private const string MaximizeGlyphData = "M 0.5,0.5 H 9.5 V 9.5 H 0.5 Z";
    private const string RestoreGlyphData = "M 2.5,0.5 H 9.5 V 7.5 M 0.5,2.5 H 7.5 V 9.5 H 0.5 Z";

    private readonly AppPaths _paths;
    private readonly IBookLibraryService _library;
    private readonly IBookFormatConverter _formatConverter;
    private readonly DoubanMetadataService _douban;
    private readonly IKindleDeviceService? _kindle;
    private readonly ISecretProtector _secretProtector;
    private readonly AppBackupService _backupService;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly FontLibraryService _fontLibrary;
    private readonly DictionaryService _dictionaryService;
    private readonly ReaderDataService _readerData;
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
    private IReaderHost? _readerActiveHost;
    private IReaderHost? _readerPreloadHost;
    private bool _filterControlsReady;
    private bool _updatingFilterControls;
    private bool _updatingDetails;
    private LibraryViewMode _libraryViewMode = LibraryViewMode.Grid;
    private BookCardViewModel? _selectedCard;
    private readonly HashSet<Guid> _selectedBookIds = [];
    private TaskCompletionSource<bool>? _confirmationCompletion;
    private TaskCompletionSource<string?>? _collectionNameCompletion;
    private bool _conversionInProgress;

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
        _douban = douban ?? new DoubanMetadataService();
        _kindle = services?.KindleDeviceService;
        _secretProtector = services?.SecretProtector ?? new PlaintextSecretProtector();
        _backupService = new AppBackupService(paths, _secretProtector);
        _appSettingsStore = new AppSettingsStore(paths);
        _fontLibrary = new FontLibraryService(paths);
        _dictionaryService = new DictionaryService(paths);
        _readerData = new ReaderDataService(paths);
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
        SetLibraryViewMode(LibraryViewMode.Grid);
        UpdateLibraryUi();
        ConfigureStage3Timer();
    }

    public LibraryViewModel ViewModel { get; }

    public ObservableCollection<BookCollectionFolderViewModel> CollectionFolders { get; } = [];

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
        }
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

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _stage3Timer.Stop();
        _readerNavigationCancellation?.Cancel();
        _readerSessionCancellation?.Cancel();
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        _douban.Dispose();
        _zLibraryService.Dispose();
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

        var showingBooks = _libraryViewMode is LibraryViewMode.Grid or LibraryViewMode.List;
        var hasBooks = ViewModel.Books.Count > 0;
        EmptyLibraryState.IsVisible = showingBooks && !hasBooks && !ViewModel.IsBusy;

        if (ViewModel.LibraryBooks.Count == 0)
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
        UpdateLibraryUi();
    }

    private void SelectBook(BookCardViewModel card)
    {
        _selectedCard = card;
        DetailCoverImage.Source = card.CoverImage;
        DetailCoverPlaceholder.IsVisible = card.CoverImage is null;
        DetailTitleText.Text = card.Title;
        DetailAuthorsText.Text = card.Authors;
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
        DetailFavoriteButton.Content = card.Book.IsFavorite ? "取消收藏" : "加入收藏";
        DetailReadingStatusButton.Content = $"标记为：{GetReadingStatusName(card.Book.ReadingStatus)}";
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
        DetailCoverImage.Source = null;
        DetailCoverPlaceholder.IsVisible = true;
        DetailTitleText.Text = "请选择一本书";
        DetailAuthorsText.Text = string.Empty;
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
        DetailFavoriteButton.Content = "加入收藏";
        DetailReadingStatusButton.Content = "标记阅读";
        DetailDescriptionText.Text = "暂无简介";
        DetailFiles.ItemsSource = Array.Empty<BookFile>();
        DetailCollectionBox.ItemsSource = CollectionFolders;
        DetailCollectionBox.SelectedItem = null;
        CollectionMembershipButton.Content = "加入收藏夹";
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
        var inputPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (inputPaths.Length == 0)
        {
            SetTaskStatus("没有选择可导入的文件。");
            return;
        }

        try
        {
            SetTaskStatus($"正在导入 {inputPaths.Length} 个位置…");
            var progress = new Progress<TransferProgress>(value =>
                SetTaskStatus(string.IsNullOrWhiteSpace(value.Message)
                    ? $"正在导入：{value.Percentage:0}%"
                    : value.Message));
            var result = await ViewModel.ImportAsync(inputPaths, progress, _lifetimeCancellation.Token);
            await RefreshCollectionsAsync();
            UpdateLibraryUi();
            SetTaskStatus(result.FailureCount == 0
                ? $"已导入 {result.SuccessCount} 本书。"
                : $"已导入 {result.SuccessCount} 本书，{result.FailureCount} 项失败。 ");
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetTaskStatus($"导入失败：{exception.Message}");
        }
    }

    private async Task OpenBookAsync(BookCardViewModel card, BookFile? requestedFile = null)
    {
        var file = requestedFile ?? ReaderBookSelectionPolicy.SelectPreferred(card.Book.Files);
        if (file is null)
        {
            SetTaskStatus("这本书没有可打开的文件。");
            return;
        }

        var path = ViewModel.GetAbsoluteFilePath(file);
        if (!File.Exists(path))
        {
            SetTaskStatus($"找不到文件：{file.RelativePath}");
            return;
        }

        if (string.Equals(file.Format, "epub", StringComparison.OrdinalIgnoreCase))
        {
            await OpenEpubReaderAsync(card, file, path);
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
        }
    }

    private async Task DeleteSelectedBookAsync()
    {
        if (_selectedCard is null) return;
        var card = _selectedCard;
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
        }
    }

    private async Task DeleteFileAsync(BookFile file)
    {
        if (_selectedCard is null) return;
        var card = _selectedCard;
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
        var selectedCount = GetSelectedCards().Count;
        MultiSelectionBar.IsVisible = selectedCount > 1;
        MultiSelectionText.Text = selectedCount > 0 ? $"已选择 {selectedCount} 本书" : string.Empty;
    }

    private ContextMenu BuildBookContextMenu(BookCardViewModel card)
    {
        var menu = new ContextMenu();

        var openMenu = new MenuItem { Header = "打开书籍" };
        foreach (var file in ReaderBookSelectionPolicy.GetSupportedFiles(card.Book.Files))
        {
            var item = new MenuItem { Header = file.Format.ToUpperInvariant(), Tag = file };
            item.Click += async (_, _) => await OpenBookAsync(card, file);
            openMenu.Items.Add(item);
        }
        openMenu.IsEnabled = openMenu.Items.Count > 0;
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

        menu.Items.Add(CreateMenuItem("豆瓣匹配", () => MatchDoubanAsync(card)));
        menu.Items.Add(new Separator());

        var deleteFormatMenu = new MenuItem { Header = "删除格式" };
        foreach (var file in card.Book.Files)
        {
            var item = new MenuItem { Header = file.Format.ToUpperInvariant(), Tag = file };
            item.Click += async (_, _) => await DeleteFileAsync(file);
            deleteFormatMenu.Items.Add(item);
        }
        deleteFormatMenu.IsEnabled = deleteFormatMenu.Items.Count > 0;
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
        }
        _selectedCard = card;
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
        }
    }

    private async Task DeleteBookFromContextAsync(BookCardViewModel card)
    {
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
            SetTaskStatus("已有一本书正在转换，请稍候。");
            return;
        }

        var target = BookFormatConversionPolicy.Normalize(targetFormat);
        if (!BookFormatConversionPolicy.IsConvertibleFormat(target)) return;
        if (card.Book.Files.Any(file =>
                string.Equals(BookFormatConversionPolicy.Normalize(file.Format), target, StringComparison.OrdinalIgnoreCase)))
        {
            SetTaskStatus($"《{card.Title}》已经有 {target.ToUpperInvariant()} 格式。");
            return;
        }

        var sourceFile = BookFormatConversionPolicy.SelectSource(card.Book.Files, target);
        if (sourceFile is null)
        {
            SetTaskStatus("需要 EPUB、AZW3、PDF 或 MOBI 作为转换来源。");
            return;
        }

        var sourcePath = ViewModel.GetAbsoluteFilePath(sourceFile);
        if (!File.Exists(sourcePath))
        {
            SetTaskStatus($"找不到转换来源：{sourceFile.RelativePath}");
            return;
        }

        _conversionInProgress = true;
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "KkindleConversions", Guid.NewGuid().ToString("N"));
        var temporaryOutput = Path.Combine(temporaryDirectory, "converted." + target);
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            SetTaskStatus($"正在将 {sourceFile.Format.ToUpperInvariant()} 转换为 {target.ToUpperInvariant()}…");
            var progress = new Progress<FormatConversionProgress>(value =>
                SetTaskStatus(string.IsNullOrWhiteSpace(value.Message)
                    ? $"格式转换：{value.Percentage:0}%"
                    : value.Message));
            await _formatConverter.ConvertAsync(
                sourcePath,
                temporaryOutput,
                progress,
                _lifetimeCancellation.Token);
            await _library.AddFileToBookAsync(card.Book.Id, temporaryOutput, _lifetimeCancellation.Token);
            await RefreshLibraryAsync();
            SetTaskStatus($"已为《{card.Title}》添加 {target.ToUpperInvariant()} 格式。");
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetTaskStatus($"格式转换失败：{exception.Message}");
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
            _conversionInProgress = false;
        }
    }

    private async Task MatchDoubanAsync(BookCardViewModel card)
    {
        try
        {
            SetTaskStatus($"正在搜索《{card.Title}》的豆瓣信息…");
            var candidates = await _douban.SearchAsync(
                card.Book.Title,
                card.Book.Authors,
                _lifetimeCancellation.Token);
            if (candidates.Count == 0)
            {
                SetTaskStatus("豆瓣没有返回匹配结果。");
                return;
            }

            var candidate = candidates[0];
            var metadata = await _douban.GetDetailsAsync(candidate, _lifetimeCancellation.Token);
            if (!await ConfirmAsync("应用豆瓣信息", $"将使用“{metadata.Title}”的豆瓣信息更新当前书籍，继续吗？")) return;

            var book = card.Book;
            if (!string.IsNullOrWhiteSpace(metadata.Title)) book.Title = metadata.Title;
            if (!string.IsNullOrWhiteSpace(metadata.Authors)) book.Authors = metadata.Authors;
            book.Series = metadata.Series;
            book.Description = metadata.Description;
            book.Publisher = metadata.Publisher;
            book.PublishDate = metadata.PublishDate;
            book.Isbn = metadata.Isbn;
            book.PageCount = metadata.Pages;
            book.Binding = metadata.Binding;
            book.DoubanRating = metadata.Rating;
            book.DoubanRatingCount = metadata.RatingCount;

            if (!string.IsNullOrWhiteSpace(metadata.CoverUrl))
            {
                try
                {
                    var coverBytes = await _douban.DownloadCoverAsync(metadata.CoverUrl, _lifetimeCancellation.Token);
                    _paths.EnsureDirectories();
                    var coverPath = Path.Combine(_paths.Covers, $"{book.Id:N}.jpg");
                    await File.WriteAllBytesAsync(coverPath, coverBytes, _lifetimeCancellation.Token);
                    book.CoverPath = Path.GetRelativePath(_paths.Data, coverPath);
                }
                catch (Exception exception)
                {
                    SetTaskStatus($"豆瓣信息已读取，但封面下载失败：{exception.Message}");
                }
            }

            await _library.UpdateMetadataAsync(book, _lifetimeCancellation.Token);
            await RefreshLibraryAsync();
            SetTaskStatus($"已应用“{book.Title}”的豆瓣信息。");
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetTaskStatus($"豆瓣匹配失败：{exception.Message}");
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        if (_confirmationCompletion is not null) return false;
        ConfirmationTitleText.Text = title;
        ConfirmationMessageText.Text = message;
        ConfirmationOkButton.Content = title.Contains("删除", StringComparison.Ordinal) ? "确认删除" : "应用";
        ConfirmationOverlay.IsVisible = true;
        _confirmationCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = _confirmationCompletion;
        var confirmed = await completion.Task;
        if (ReferenceEquals(_confirmationCompletion, completion))
            _confirmationCompletion = null;
        return confirmed;
    }

    private Task<string?> PromptCollectionNameAsync()
    {
        if (_collectionNameCompletion is not null) return Task.FromResult<string?>(null);
        CollectionNameBox.Text = string.Empty;
        CollectionNameOverlay.IsVisible = true;
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
        }
    }

    private void ShowAllBooksButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
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
        => FilterPanel.IsVisible = !FilterPanel.IsVisible;

    private void GridViewButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => SetLibraryViewMode(LibraryViewMode.Grid);

    private void ListViewButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => SetLibraryViewMode(LibraryViewMode.List);

    private void CollectionViewButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => SetLibraryViewMode(LibraryViewMode.Collections);

    private async void CreateCollectionButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await CreateCollectionAsync();

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

    private async void BookCard_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not BookCardViewModel card) return;
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

    private void CollectionFolderButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not BookCollectionFolderViewModel folder) return;
        ViewModel.CollectionFilterId = folder.Collection.Id;
        ViewModel.CollectionFilterName = folder.Name;
        ViewModel.RefreshView();
        SetLibraryViewMode(LibraryViewMode.Grid);
    }

    private async void DeleteCollectionButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is BookCollectionFolderViewModel folder)
            await DeleteCollectionAsync(folder);
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

    private void CollectionNameOkButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var name = CollectionNameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            SetTaskStatus("收藏夹名称不能为空。");
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
}
