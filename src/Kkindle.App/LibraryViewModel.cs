using System.Collections.ObjectModel;
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

public sealed class KindleBookCardViewModel
{
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
}

public sealed class LibraryViewModel : ObservableObject
{
    private readonly IBookLibraryService _library;
    private readonly string _dataRoot;
    private string _searchText = string.Empty;
    private string? _authorFilter;
    private string? _tagFilter;
    private string? _formatFilter;
    private string? _categoryFilter;
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
        || FavoritesOnly;

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

            var filtered = allBooks.Where(MatchesFilters);
            var books = SortMode switch
            {
                LibrarySortMode.TitleAscending => filtered.OrderBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase).ToArray(),
                LibrarySortMode.AuthorAscending => filtered.OrderBy(book => book.Authors, StringComparer.CurrentCultureIgnoreCase).ThenBy(book => book.Title).ToArray(),
                LibrarySortMode.CreatedDescending => filtered.OrderByDescending(book => book.CreatedAt).ToArray(),
                LibrarySortMode.ProgressDescending => filtered.OrderByDescending(book => book.ReadingStatus).ThenByDescending(book => book.UpdatedAt).ToArray(),
                _ => filtered.OrderByDescending(book => book.UpdatedAt).ToArray()
            };
            Books.Clear();
            foreach (var book in books) Books.Add(new BookCardViewModel(book, _dataRoot));
            StatusText = books.Length == 0
                ? allBooks.Count == 0 ? "书库还是空的" : "没有符合条件的书籍"
                : HasActiveFilters || !string.IsNullOrWhiteSpace(SearchText)
                    ? $"找到 {books.Length} 本书"
                    : $"共 {books.Length} 本书";
        }
        finally { IsBusy = false; }
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
