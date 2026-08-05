using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kkindle.Core;
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
    public string DescriptionLabel => string.IsNullOrWhiteSpace(Book.Description) ? "暂无简介" : Book.Description;
    public BitmapImage? CoverImage { get; private set; }

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

    public bool HasActiveFilters => !string.IsNullOrWhiteSpace(AuthorFilter)
        || !string.IsNullOrWhiteSpace(TagFilter)
        || !string.IsNullOrWhiteSpace(FormatFilter);

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

            var books = allBooks.Where(MatchesFilters).ToArray();
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
