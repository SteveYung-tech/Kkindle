using System.Collections.ObjectModel;
using System.Net;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Kkindle.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Kkindle;

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
    public string SeriesLabel => string.IsNullOrWhiteSpace(Book.Series) ? "未设置系列" : Book.Series;
    public string TagsLabel => string.IsNullOrWhiteSpace(Book.Tags) ? "未设置标签" : Book.Tags;
    public string CategoryLabel => string.IsNullOrWhiteSpace(Book.Category) ? "未分类" : Book.Category;
    public string FavoriteLabel => Book.IsFavorite ? "★ 收藏" : string.Empty;
    public string ReadingStatusLabel => Book.ReadingStatus switch
    {
        LibraryReadingStatus.Reading => "阅读中",
        LibraryReadingStatus.Finished => "已读",
        _ => "待读"
    };
    public string DescriptionLabel => string.IsNullOrWhiteSpace(Book.Description) ? "暂无简介" : Book.Description;
    public BitmapImage? CoverImage { get; private set; }
    private bool _isConversionProgressVisible;
    private double _conversionProgress;
    private string _conversionProgressLabel = "0%";
    private string _conversionProgressMessage = "正在转换…";
    private BookLibraryPresence _libraryPresence = BookLibraryPresence.ComputerOnly;
    private bool _isLibraryPresenceVisible = true;

    public BookLibraryPresence LibraryPresence
    {
        get => _libraryPresence;
        private set
        {
            if (!SetProperty(ref _libraryPresence, value)) return;
            OnPropertyChanged(nameof(PresenceGlyph));
            OnPropertyChanged(nameof(PresenceLabel));
            OnPropertyChanged(nameof(ComputerOnlyPresenceVisibility));
            OnPropertyChanged(nameof(KindleOnlyPresenceVisibility));
            OnPropertyChanged(nameof(BothLibrariesPresenceVisibility));
        }
    }

    public string PresenceGlyph => LibraryPresence switch
    {
        BookLibraryPresence.Both => "\uE73E",
        BookLibraryPresence.KindleOnly => "\uE70A",
        _ => "\uE7F4"
    };

    public string PresenceLabel => LibraryPresence switch
    {
        BookLibraryPresence.Both => "电脑与 Kindle 书库都有",
        BookLibraryPresence.KindleOnly => "仅 Kindle 书库有",
        _ => "仅电脑书库有"
    };

    public Visibility ComputerOnlyPresenceVisibility => LibraryPresence == BookLibraryPresence.ComputerOnly
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility KindleOnlyPresenceVisibility => LibraryPresence == BookLibraryPresence.KindleOnly
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility BothLibrariesPresenceVisibility => LibraryPresence == BookLibraryPresence.Both
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility PresenceVisibility => _isLibraryPresenceVisible
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool IsConversionProgressVisible
    {
        get => _isConversionProgressVisible;
        private set
        {
            if (SetProperty(ref _isConversionProgressVisible, value))
                OnPropertyChanged(nameof(ConversionProgressVisibility));
        }
    }

    public Visibility ConversionProgressVisibility => IsConversionProgressVisible
        ? Visibility.Visible
        : Visibility.Collapsed;

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

    public void SetLibraryPresence(BookLibraryPresence presence) => LibraryPresence = presence;

    public void SetLibraryPresenceVisible(bool visible)
    {
        if (_isLibraryPresenceVisible == visible) return;
        _isLibraryPresenceVisible = visible;
        OnPropertyChanged(nameof(PresenceVisibility));
    }

    public void Refresh()
    {
        CoverImage = null;
        if (!string.IsNullOrWhiteSpace(Book.CoverPath))
        {
            var path = Path.GetFullPath(Path.Combine(DataRoot, Book.CoverPath));
            if (File.Exists(path))
            {
                try { CoverImage = new BitmapImage(new Uri(path)); }
                catch { CoverImage = null; }
            }
        }
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Authors));
        OnPropertyChanged(nameof(FormatLabel));
        OnPropertyChanged(nameof(FileCountLabel));
        OnPropertyChanged(nameof(SeriesLabel));
        OnPropertyChanged(nameof(TagsLabel));
        OnPropertyChanged(nameof(CategoryLabel));
        OnPropertyChanged(nameof(FavoriteLabel));
        OnPropertyChanged(nameof(ReadingStatusLabel));
        OnPropertyChanged(nameof(DescriptionLabel));
        OnPropertyChanged(nameof(CoverImage));
    }
}

public sealed class KindleBookCardViewModel : ObservableObject
{
    private BookLibraryPresence _libraryPresence = BookLibraryPresence.KindleOnly;
    private bool _isLibraryPresenceVisible = true;

    public KindleBookCardViewModel(KindleBook book)
    {
        Book = book;
        if (!string.IsNullOrWhiteSpace(book.CoverPath) && File.Exists(book.CoverPath))
        {
            try { CoverImage = new BitmapImage(new Uri(book.CoverPath)); }
            catch { CoverImage = null; }
        }
    }

    public KindleBook Book { get; }
    public string Title => Book.Title;
    public string Authors => Book.Authors;
    public string FormatLabel => Book.Format.ToUpperInvariant();
    public string SizeLabel => Book.SizeLabel;
    public string InfoLabel => $"{FormatLabel} · {SizeLabel}";
    public string RelativePath => Book.RelativePath;
    public BitmapImage? CoverImage { get; }

    public BookLibraryPresence LibraryPresence
    {
        get => _libraryPresence;
        private set
        {
            if (!SetProperty(ref _libraryPresence, value)) return;
            OnPropertyChanged(nameof(PresenceGlyph));
            OnPropertyChanged(nameof(PresenceLabel));
            OnPropertyChanged(nameof(ComputerOnlyPresenceVisibility));
            OnPropertyChanged(nameof(KindleOnlyPresenceVisibility));
            OnPropertyChanged(nameof(BothLibrariesPresenceVisibility));
        }
    }

    public string PresenceGlyph => LibraryPresence switch
    {
        BookLibraryPresence.Both => "\uE73E",
        BookLibraryPresence.ComputerOnly => "\uE7F4",
        _ => "\uE70A"
    };

    public string PresenceLabel => LibraryPresence switch
    {
        BookLibraryPresence.Both => "电脑与 Kindle 书库都有",
        BookLibraryPresence.ComputerOnly => "仅电脑书库有",
        _ => "仅 Kindle 书库有"
    };

    public Visibility ComputerOnlyPresenceVisibility => LibraryPresence == BookLibraryPresence.ComputerOnly
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility KindleOnlyPresenceVisibility => LibraryPresence == BookLibraryPresence.KindleOnly
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility BothLibrariesPresenceVisibility => LibraryPresence == BookLibraryPresence.Both
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility PresenceVisibility => _isLibraryPresenceVisible
        ? Visibility.Visible
        : Visibility.Collapsed;

    public void SetLibraryPresence(BookLibraryPresence presence) => LibraryPresence = presence;

    public void SetLibraryPresenceVisible(bool visible)
    {
        if (_isLibraryPresenceVisible == visible) return;
        _isLibraryPresenceVisible = visible;
        OnPropertyChanged(nameof(PresenceVisibility));
    }
}

public sealed class ZLibraryBookCardViewModel : ObservableObject
{
    private static readonly HttpClient CoverClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private bool _isDownloading;
    private double _downloadProgress;
    private string _statusMessage = string.Empty;

    public ZLibraryBookCardViewModel(ZLibraryBook book)
    {
        Book = book;
    }

    public ZLibraryBook Book { get; }
    public string Title => Book.Title;
    public string Authors => Book.Author;
    public string InfoLabel => Book.InfoLabel;
    public string YearLabel => Book.Year is { } year && year > 0 ? year.ToString() : string.Empty;
    public string PublicationLabel => string.Join(" · ", new[]
    {
        Book.Publisher ?? string.Empty,
        YearLabel,
        string.IsNullOrWhiteSpace(Book.Series) ? string.Empty : $"系列：{Book.Series}",
        string.IsNullOrWhiteSpace(Book.Edition) ? string.Empty : $"版本：{Book.Edition}"
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string IdentifierLabel => string.IsNullOrWhiteSpace(Book.Identifier)
        ? string.Empty
        : $"ISBN {Book.Identifier.Replace(",", " / ", StringComparison.Ordinal)}";
    public string AvailabilityLabel => string.Join(" · ", new[]
    {
        Book.ReadOnlineAvailable ? "可在线阅读" : string.Empty,
        Book.KindleAvailable ? "支持 Kindle" : string.Empty
    }.Where(value => value.Length > 0));
    public string ExtraInfoLabel => string.Join(" · ", new[] { IdentifierLabel, AvailabilityLabel }
        .Where(value => value.Length > 0));
    public string VolumeLabel => string.IsNullOrWhiteSpace(Book.Volume) ? "未提供" : Book.Volume;
    public string DetailDescription
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Book.Description)) return "暂无简介。";
            var withoutTags = Regex.Replace(Book.Description, "<[^>]+>", " ");
            var decoded = WebUtility.HtmlDecode(withoutTags);
            return Regex.Replace(decoded, @"\s+", " ").Trim();
        }
    }
    public string DetailMetadataLabel => string.Join(" · ", new[]
    {
        PublicationLabel,
        InfoLabel,
        IdentifierLabel
    }.Where(value => value.Length > 0));
    public bool CanOpenOfficialDetail => Uri.TryCreate(Book.OfficialDetailUrl, UriKind.Absolute, out _);
    public bool CanReadOnline => Book.ReadOnlineAvailable
        && Uri.TryCreate(Book.ReadOnlineUrl, UriKind.Absolute, out _);
    public bool CanSendToEmail => Book.SendToEmailAvailable
        && Book.Extension is not null
        && (Book.Extension.Equals("epub", StringComparison.OrdinalIgnoreCase)
            || Book.Extension.Equals("pdf", StringComparison.OrdinalIgnoreCase));
    public bool CanSendToEmailNow => CanSendToEmail && !IsDownloading;
    public BitmapImage? CoverImage { get; private set; }

    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (SetProperty(ref _isDownloading, value))
            {
                OnPropertyChanged(nameof(IsNotDownloading));
                OnPropertyChanged(nameof(DownloadButtonText));
                OnPropertyChanged(nameof(DownloadProgressVisibility));
                OnPropertyChanged(nameof(CanSendToEmailNow));
            }
        }
    }

    public bool IsNotDownloading => !IsDownloading;

    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => SetProperty(ref _downloadProgress, value);
    }

    public string DownloadButtonText => IsDownloading ? DownloadProgressLabel : "下载";
    public string DownloadProgressLabel => $"{Math.Clamp((int)Math.Round(DownloadProgress), 0, 100)}%";
    public Visibility DownloadProgressVisibility => IsDownloading ? Visibility.Visible : Visibility.Collapsed;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public void SetDownloadProgress(TransferProgress progress)
    {
        DownloadProgress = progress.Percentage;
    }

    public void MarkDownloadCompleted()
    {
        DownloadProgress = 100;
    }

    public void SetStatus(string message)
    {
        StatusMessage = message;
    }

    public async Task LoadCoverAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Book.CoverUrl)) return;
        try
        {
            using var response = await CoverClient.GetAsync(Book.CoverUrl, cancellationToken);
            if (!response.IsSuccessStatusCode) return;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0) return;
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream.AsRandomAccessStream());
            CoverImage = image;
            OnPropertyChanged(nameof(CoverImage));
        }
        catch
        {
            // Cover loading is decorative; never fail the search result.
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
        set => SetProperty(ref _isBusy, value);
    }

    public string? AuthorFilter
    {
        get => _authorFilter;
        set => SetProperty(ref _authorFilter, value);
    }

    public string? TagFilter
    {
        get => _tagFilter;
        set => SetProperty(ref _tagFilter, value);
    }

    public string? FormatFilter
    {
        get => _formatFilter;
        set => SetProperty(ref _formatFilter, value);
    }

    public string? CategoryFilter
    {
        get => _categoryFilter;
        set => SetProperty(ref _categoryFilter, value);
    }

    public Guid? CollectionFilterId
    {
        get => _collectionFilterId;
        set => SetProperty(ref _collectionFilterId, value);
    }

    public string? CollectionFilterName
    {
        get => _collectionFilterName;
        set => SetProperty(ref _collectionFilterName, value);
    }

    public LibraryReadingStatus? ReadingStatusFilter
    {
        get => _readingStatusFilter;
        set => SetProperty(ref _readingStatusFilter, value);
    }

    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set => SetProperty(ref _favoritesOnly, value);
    }

    public LibrarySortMode SortMode
    {
        get => _sortMode;
        set => SetProperty(ref _sortMode, value);
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
        set => SetProperty(ref _statusText, value);
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
                .SelectMany(book => book.Authors.Split(',', '，', ';', '；'))
                .Select(author => author.Trim())
                .Where(author => author.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            AvailableTags = allBooks
                .SelectMany(book => book.Tags.Split(',', '，', ';', '；'))
                .Select(tag => tag.Trim())
                .Where(tag => tag.Length > 0)
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

            ApplyCurrentView();
        }
        finally { IsBusy = false; }
    }

    public void RefreshView() => ApplyCurrentView();

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
    }

    private bool MatchesFilters(Book book)
    {
        if (!string.IsNullOrWhiteSpace(SearchText)
            && !book.Title.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            && !book.Authors.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            && !book.Tags.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(AuthorFilter)
            && !book.Authors.Split(',', '，', ';', '；').Any(author =>
                string.Equals(author.Trim(), AuthorFilter, StringComparison.CurrentCultureIgnoreCase))) return false;
        if (!string.IsNullOrWhiteSpace(TagFilter)
            && !book.Tags.Split(',', '，', ';', '；').Any(tag =>
                string.Equals(tag.Trim(), TagFilter, StringComparison.CurrentCultureIgnoreCase))) return false;
        if (!string.IsNullOrWhiteSpace(CategoryFilter)
            && !string.Equals(book.Category.Trim(), CategoryFilter, StringComparison.CurrentCultureIgnoreCase)) return false;
        if (ReadingStatusFilter is { } readingStatus && book.ReadingStatus != readingStatus) return false;
        if (FavoritesOnly && !book.IsFavorite) return false;
        if (CollectionFilterId is { } collectionId && !book.CollectionIds.Contains(collectionId)) return false;
        return string.IsNullOrWhiteSpace(FormatFilter)
            || book.Files.Any(file => string.Equals(file.Format, FormatFilter, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ImportBatchResult> ImportAsync(IEnumerable<string> paths, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
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
        finally { IsBusy = false; }
    }
}
