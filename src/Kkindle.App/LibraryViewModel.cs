using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Kkindle.Core;

namespace Kkindle;

/// <summary>
/// Presentation data for one local-library book. The domain model remains
/// untouched; this class only turns paths and enum values into UI labels and a
/// loadable Avalonia bitmap.
/// </summary>
public sealed class BookCardViewModel : ObservableObject
{
    public BookCardViewModel(Book book, string dataRoot)
    {
        Book = book;
        DataRoot = dataRoot;
        Refresh();
    }

    public Book Book { get; }
    public string DataRoot { get; }
    public string Title => Book.Title;
    public string Authors => Book.Authors;
    public string FormatLabel => Book.FormatSummary;
    public string FileCountLabel => Book.Files.Count == 0 ? string.Empty : $"{Book.Files.Count} 个文件";
    public string TotalSizeLabel => Book.Files.Count == 0 ? "暂无文件" : FormatSize(Book.Files.Sum(file => file.Size));
    public string FileSummaryLabel => string.Join(" · ", new[] { FormatLabel, FileCountLabel, TotalSizeLabel }
        .Where(value => value.Length > 0));
    public string ReadingStatusLabel => Book.ReadingStatus switch
    {
        LibraryReadingStatus.Reading => "阅读中",
        LibraryReadingStatus.Finished => "已读",
        _ => "待读"
    };
    public string ReadingStateLabel => Book.IsFavorite
        ? $"{ReadingStatusLabel} · ★ 收藏"
        : ReadingStatusLabel;
    public string OrganizationLabel
    {
        get
        {
            var labels = new[]
            {
                string.IsNullOrWhiteSpace(Book.Category) ? string.Empty : $"分类：{Book.Category}",
                string.IsNullOrWhiteSpace(Book.Tags) ? string.Empty : $"标签：{Book.Tags}"
            }.Where(value => value.Length > 0).ToArray();
            return labels.Length == 0 ? "暂无分类或标签" : string.Join(" · ", labels);
        }
    }
    public string PublicationLabel
    {
        get
        {
            var labels = new[]
            {
                string.IsNullOrWhiteSpace(Book.Publisher) ? string.Empty : $"出版社：{Book.Publisher}",
                string.IsNullOrWhiteSpace(Book.PublishDate) ? string.Empty : $"出版：{Book.PublishDate}",
                string.IsNullOrWhiteSpace(Book.PageCount) ? string.Empty : $"页数：{Book.PageCount}",
                string.IsNullOrWhiteSpace(Book.Binding) ? string.Empty : $"装帧：{Book.Binding}"
            }.Where(value => value.Length > 0).ToArray();
            return labels.Length == 0 ? "暂无出版信息" : string.Join(" · ", labels);
        }
    }
    public string IdentifierLabel
    {
        get
        {
            var labels = new[]
            {
                string.IsNullOrWhiteSpace(Book.Isbn) ? string.Empty : $"ISBN：{Book.Isbn}",
                Book.DoubanRating is { } rating
                    ? $"豆瓣：{rating:0.0}（{Book.DoubanRatingCount ?? 0} 人评价）"
                    : string.Empty
            }.Where(value => value.Length > 0).ToArray();
            return labels.Length == 0 ? "暂无 ISBN 或评分" : string.Join(" · ", labels);
        }
    }
    public string DescriptionSummaryLabel
    {
        get
        {
            var description = string.IsNullOrWhiteSpace(Book.Description)
                ? "暂无简介"
                : Regex.Replace(Book.Description, @"\s+", " ").Trim();
            return description.Length <= 90 ? description : $"{description[..90]}…";
        }
    }
    public string UpdatedLabel => $"更新于 {Book.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
    public string DescriptionLabel => string.IsNullOrWhiteSpace(Book.Description) ? "暂无简介" : Book.Description;
    public Bitmap? CoverImage { get; private set; }

    // Keep selection on the card itself. The legacy WinUI shelf did not use
    // the list control's selection fill; it drew a black outline around the
    // exact 154-DIP card footprint instead.
    private bool _isSelected;
    private bool _isMultiSelected;
    private bool _isHovered;

    // Any selection (single click or multi-select) turns the card outline
    // black; the check badge below is reserved for genuine multi-selection.
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                OnPropertyChanged(nameof(IsFrameVisible));
        }
    }

    public bool IsMultiSelected
    {
        get => _isMultiSelected;
        set => SetProperty(ref _isMultiSelected, value);
    }

    // Hovering the whole card (cover or text) shows the same thin black
    // frame as selection, so the outline lives on the card, not on the cover
    // image alone.
    public bool IsHovered
    {
        get => _isHovered;
        set
        {
            if (SetProperty(ref _isHovered, value))
                OnPropertyChanged(nameof(IsFrameVisible));
        }
    }

    // The black frame around the entire card appears on hover or selection.
    public bool IsFrameVisible => IsSelected || IsHovered;

    // The original gallery always surfaced where a book is available. The
    // portable library starts with the authoritative local copy and changes
    // this value when a future device scan supplies a matching Kindle copy.
    private BookLibraryPresence _libraryPresence = BookLibraryPresence.ComputerOnly;
    private bool _isLibraryPresenceVisible = true;
    private bool _isGalleryTextVisible = true;

    public BookLibraryPresence LibraryPresence
    {
        get => _libraryPresence;
        private set
        {
            if (!SetProperty(ref _libraryPresence, value)) return;
            OnPropertyChanged(nameof(PresenceLabel));
            OnPropertyChanged(nameof(ComputerOnlyPresenceVisibility));
            OnPropertyChanged(nameof(KindleOnlyPresenceVisibility));
            OnPropertyChanged(nameof(BothLibrariesPresenceVisibility));
        }
    }

    // The WinUI reference draws three distinct monochrome glyphs for the
    // library-presence badge (PC only / Kindle only / both). Avalonia keeps
    // the same three-state model so the comparison stays readable instead of
    // using a fixed icon on every card.
    public bool ComputerOnlyPresenceVisibility => LibraryPresence == BookLibraryPresence.ComputerOnly;
    public bool KindleOnlyPresenceVisibility => LibraryPresence == BookLibraryPresence.KindleOnly;
    public bool BothLibrariesPresenceVisibility => LibraryPresence == BookLibraryPresence.Both;

    public string PresenceLabel => LibraryPresence switch
    {
        BookLibraryPresence.Both => "电脑与 Kindle 书库都有",
        BookLibraryPresence.KindleOnly => "仅 Kindle 书库有",
        _ => "仅电脑书库有"
    };

    public bool PresenceVisibility => _isLibraryPresenceVisible;

    public bool GalleryTextVisibility => _isGalleryTextVisible;

    public void SetLibraryPresence(BookLibraryPresence presence) => LibraryPresence = presence;

    public void SetLibraryPresenceVisible(bool visible)
    {
        if (_isLibraryPresenceVisible == visible) return;
        _isLibraryPresenceVisible = visible;
        OnPropertyChanged(nameof(PresenceVisibility));
    }

    public void SetGalleryTextVisible(bool visible)
    {
        if (_isGalleryTextVisible == visible) return;
        _isGalleryTextVisible = visible;
        OnPropertyChanged(nameof(GalleryTextVisibility));
    }

    // Format conversion progress shown on the book card while the conversion
    // popup is minimized to the background. The badge is a tap target that
    // restores the popup, matching the WinUI card behaviour.
    private bool _isConversionProgressVisible;
    private double _conversionProgress;
    private string _conversionProgressLabel = "0%";
    private string _conversionProgressMessage = "正在转换…";

    public bool IsConversionProgressVisible
    {
        get => _isConversionProgressVisible;
        private set
        {
            if (!SetProperty(ref _isConversionProgressVisible, value)) return;
            OnPropertyChanged(nameof(ConversionProgressVisibility));
        }
    }

    public bool ConversionProgressVisibility => IsConversionProgressVisible;

    public double ConversionProgress
    {
        get => _conversionProgress;
        private set => SetProperty(ref _conversionProgress, value);
    }

    public string ConversionProgressLabel
    {
        get => _conversionProgressLabel;
        private set => SetProperty(ref _conversionProgressLabel, value);
    }

    public string ConversionProgressMessage
    {
        get => _conversionProgressMessage;
        private set => SetProperty(ref _conversionProgressMessage, value);
    }

    public void SetConversionProgress(FormatConversionProgress progress, bool showIndicator)
    {
        ConversionProgress = Math.Clamp(progress.Percentage, 0, 100);
        ConversionProgressLabel = $"{progress.RoundedPercentage}%";
        ConversionProgressMessage = progress.Message;
        IsConversionProgressVisible = showIndicator;
    }

    public void ClearConversionProgress()
    {
        IsConversionProgressVisible = false;
        ConversionProgress = 0;
        ConversionProgressLabel = "0%";
        ConversionProgressMessage = "正在转换…";
    }

    public void Refresh()
    {
        CoverImage?.Dispose();
        CoverImage = LoadCover(DataRoot, Book.CoverPath);

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Authors));
        OnPropertyChanged(nameof(FormatLabel));
        OnPropertyChanged(nameof(FileCountLabel));
        OnPropertyChanged(nameof(TotalSizeLabel));
        OnPropertyChanged(nameof(FileSummaryLabel));
        OnPropertyChanged(nameof(ReadingStatusLabel));
        OnPropertyChanged(nameof(ReadingStateLabel));
        OnPropertyChanged(nameof(OrganizationLabel));
        OnPropertyChanged(nameof(PublicationLabel));
        OnPropertyChanged(nameof(IdentifierLabel));
        OnPropertyChanged(nameof(DescriptionSummaryLabel));
        OnPropertyChanged(nameof(UpdatedLabel));
        OnPropertyChanged(nameof(DescriptionLabel));
        OnPropertyChanged(nameof(CoverImage));
    }

    private static Bitmap? LoadCover(string dataRoot, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        try
        {
            var path = Path.GetFullPath(Path.Combine(dataRoot, relativePath));
            return File.Exists(path) ? new Bitmap(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024 / 1024:0.0} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / 1024d / 1024:0.0} MB";
        if (bytes >= 1024L) return $"{bytes / 1024d:0} KB";
        return $"{bytes} B";
    }
}

public sealed class BookCollectionFolderViewModel
{
    private readonly Bitmap?[] _covers = new Bitmap?[3];

    public BookCollectionFolderViewModel(
        BookCollection collection,
        int bookCount,
        string dataRoot,
        IReadOnlyList<string?> coverPaths)
    {
        Collection = collection;
        BookCount = bookCount;
        for (var index = 0; index < _covers.Length; index++)
        {
            var path = index < coverPaths.Count ? coverPaths[index] : null;
            _covers[index] = LoadCover(dataRoot, path);
        }
    }

    public BookCollection Collection { get; }
    public string Name => Collection.Name;
    public int BookCount { get; }
    public string BookCountLabel => $"{BookCount} 本书";
    public Bitmap? Cover1 => _covers[0];
    public Bitmap? Cover2 => _covers[1];
    public Bitmap? Cover3 => _covers[2];

    private static Bitmap? LoadCover(string dataRoot, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        try
        {
            var path = Path.GetFullPath(Path.Combine(dataRoot, relativePath));
            return File.Exists(path) ? new Bitmap(path) : null;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class LibraryViewModel : ObservableObject
{
    private readonly IBookLibraryService _library;
    private readonly string _dataRoot;
    private readonly Dictionary<Guid, BookCardViewModel> _bookCards = [];
    private string _searchText = string.Empty;
    private string? _authorFilter;
    private string? _tagFilter;
    private string? _formatFilter;
    private string? _categoryFilter;
    private Guid? _collectionFilterId;
    private string? _collectionFilterName;
    private LibraryReadingStatus? _readingStatusFilter;
    private bool _favoritesOnly;
    private LibrarySortMode _sortMode;
    private bool _isBusy;
    private string _statusText = "准备就绪";

    public LibraryViewModel(IBookLibraryService library, string dataRoot)
    {
        _library = library;
        _dataRoot = dataRoot;
    }

    public ObservableCollection<BookCardViewModel> Books { get; } = [];
    public IReadOnlyList<Book> LibraryBooks { get; private set; } = [];
    public IReadOnlyList<string> AvailableAuthors { get; private set; } = [];
    public IReadOnlyList<string> AvailableTags { get; private set; } = [];
    public IReadOnlyList<string> AvailableFormats { get; private set; } = [];
    public IReadOnlyList<string> AvailableCategories { get; private set; } = [];

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string? AuthorFilter
    {
        get => _authorFilter;
        set => SetFilter(ref _authorFilter, value);
    }

    public string? TagFilter
    {
        get => _tagFilter;
        set => SetFilter(ref _tagFilter, value);
    }

    public string? FormatFilter
    {
        get => _formatFilter;
        set => SetFilter(ref _formatFilter, value);
    }

    public string? CategoryFilter
    {
        get => _categoryFilter;
        set => SetFilter(ref _categoryFilter, value);
    }

    public Guid? CollectionFilterId
    {
        get => _collectionFilterId;
        set => SetFilter(ref _collectionFilterId, value);
    }

    public string? CollectionFilterName
    {
        get => _collectionFilterName;
        set => SetProperty(ref _collectionFilterName, value);
    }

    public LibraryReadingStatus? ReadingStatusFilter
    {
        get => _readingStatusFilter;
        set => SetFilter(ref _readingStatusFilter, value);
    }

    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set => SetFilter(ref _favoritesOnly, value);
    }

    public LibrarySortMode SortMode
    {
        get => _sortMode;
        set => SetFilter(ref _sortMode, value);
    }

    public bool HasActiveFilters => !string.IsNullOrWhiteSpace(AuthorFilter)
        || !string.IsNullOrWhiteSpace(TagFilter)
        || !string.IsNullOrWhiteSpace(FormatFilter)
        || !string.IsNullOrWhiteSpace(CategoryFilter)
        || ReadingStatusFilter is not null
        || FavoritesOnly
        || CollectionFilterId is not null;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            var allBooks = await _library.SearchAsync(cancellationToken: cancellationToken);
            LibraryBooks = allBooks;
            _bookCards.Clear();
            foreach (var book in allBooks)
                _bookCards[book.Id] = new BookCardViewModel(book, _dataRoot);

            AvailableAuthors = allBooks
                .SelectMany(book => SplitValues(book.Authors))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            AvailableTags = allBooks
                .SelectMany(book => SplitValues(book.Tags))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            AvailableFormats = allBooks
                .SelectMany(book => book.Files.Select(file => file.Format.ToUpperInvariant()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            AvailableCategories = allBooks
                .Select(book => book.Category.Trim())
                .Where(category => category.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            OnPropertyChanged(nameof(AvailableAuthors));
            OnPropertyChanged(nameof(AvailableTags));
            OnPropertyChanged(nameof(AvailableFormats));
            OnPropertyChanged(nameof(AvailableCategories));
            ApplyCurrentView();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void RefreshView() => ApplyCurrentView();

    public async Task<ImportBatchResult> ImportAsync(
        IEnumerable<string> paths,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            var result = await _library.ImportAsync(paths, progress, cancellationToken);
            await RefreshAsync(cancellationToken);
            StatusText = result.FailureCount == 0
                ? $"已导入 {result.SuccessCount} 项"
                : $"已导入 {result.SuccessCount} 项，{result.FailureCount} 项失败";
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteBookAsync(Book book, CancellationToken cancellationToken = default)
    {
        await _library.DeleteAsync(book.Id, cancellationToken);
        await RefreshAsync(cancellationToken);
        StatusText = $"已删除《{book.Title}》";
    }

    public async Task DeleteFileAsync(Book book, BookFile file, CancellationToken cancellationToken = default)
    {
        await _library.DeleteFileAsync(book.Id, file.Id, cancellationToken);
        await RefreshAsync(cancellationToken);
        StatusText = $"已删除 {file.Format.ToUpperInvariant()} 文件";
    }

    public string GetAbsoluteFilePath(BookFile file) => _library.GetAbsoluteFilePath(file);

    private void ApplyCurrentView()
    {
        var filtered = LibraryBooks.Where(MatchesFilters);
        var books = SortMode switch
        {
            LibrarySortMode.TitleAscending => filtered.OrderBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            LibrarySortMode.AuthorAscending => filtered.OrderBy(book => book.Authors, StringComparer.CurrentCultureIgnoreCase).ThenBy(book => book.Title).ToArray(),
            LibrarySortMode.CreatedDescending => filtered.OrderByDescending(book => book.CreatedAt).ToArray(),
            LibrarySortMode.ProgressDescending => filtered.OrderByDescending(book => book.ReadingStatus).ThenByDescending(book => book.UpdatedAt).ToArray(),
            _ => filtered.OrderByDescending(book => book.UpdatedAt).ToArray()
        };

        Books.Clear();
        foreach (var book in books)
        {
            if (!_bookCards.TryGetValue(book.Id, out var card))
            {
                card = new BookCardViewModel(book, _dataRoot);
                _bookCards[book.Id] = card;
            }
            Books.Add(card);
        }

        StatusText = books.Length == 0
            ? LibraryBooks.Count == 0 ? "书库还是空的"
                : CollectionFilterId is not null ? $"“{CollectionFilterName}”收藏夹还是空的"
                : "没有符合条件的书籍"
            : HasActiveFilters || !string.IsNullOrWhiteSpace(SearchText)
                ? CollectionFilterId is not null
                    ? $"{CollectionFilterName} · {books.Length} 本书"
                    : $"找到 {books.Length} 本书"
                : $"共 {books.Length} 本书";

        OnPropertyChanged(nameof(HasActiveFilters));
    }

    private bool MatchesFilters(Book book)
    {
        if (!string.IsNullOrWhiteSpace(SearchText)
            && !book.Title.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            && !book.Authors.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            && !book.Tags.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(AuthorFilter)
            && !SplitValues(book.Authors).Any(author => string.Equals(author, AuthorFilter, StringComparison.CurrentCultureIgnoreCase))) return false;
        if (!string.IsNullOrWhiteSpace(TagFilter)
            && !SplitValues(book.Tags).Any(tag => string.Equals(tag, TagFilter, StringComparison.CurrentCultureIgnoreCase))) return false;
        if (!string.IsNullOrWhiteSpace(CategoryFilter)
            && !string.Equals(book.Category.Trim(), CategoryFilter, StringComparison.CurrentCultureIgnoreCase)) return false;
        if (ReadingStatusFilter is { } readingStatus && book.ReadingStatus != readingStatus) return false;
        if (FavoritesOnly && !book.IsFavorite) return false;
        if (CollectionFilterId is { } collectionId && !book.CollectionIds.Contains(collectionId)) return false;
        return string.IsNullOrWhiteSpace(FormatFilter)
            || book.Files.Any(file => string.Equals(file.Format, FormatFilter, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> SplitValues(string? value) =>
        (value ?? string.Empty)
        .Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(item => item.Length > 0);

    private void SetFilter<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName)) return;
        OnPropertyChanged(nameof(HasActiveFilters));
    }
}
