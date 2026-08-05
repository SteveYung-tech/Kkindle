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
        OnPropertyChanged(nameof(CoverImage));
    }
}

public sealed class LibraryViewModel : ObservableObject
{
    private readonly IBookLibraryService _library;
    private readonly string _dataRoot;
    private string _searchText = string.Empty;
    private bool _isBusy;
    private string _statusText = "准备就绪";

    public LibraryViewModel(IBookLibraryService library, string dataRoot)
    {
        _library = library;
        _dataRoot = dataRoot;
    }

    public ObservableCollection<BookCardViewModel> Books { get; } = [];

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
            var books = await _library.SearchAsync(SearchText, cancellationToken);
            Books.Clear();
            foreach (var book in books) Books.Add(new BookCardViewModel(book, _dataRoot));
            StatusText = books.Count == 0 ? "书库还是空的" : $"共 {books.Count} 本书";
        }
        finally { IsBusy = false; }
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
