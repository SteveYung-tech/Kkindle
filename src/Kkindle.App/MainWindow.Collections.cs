using System.Collections.ObjectModel;
using Kkindle.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Kkindle;

public sealed partial class MainWindow
{
    private enum LibraryViewMode { Grid, List, Collections }

    private sealed record BookCollectionMenuMarker(Book Book);
    private sealed record BookCollectionMenuContext(Book Book, BookCollection Collection);

    private IReadOnlyList<BookCollection> _bookCollections = [];
    private LibraryViewMode _libraryViewMode = LibraryViewMode.Grid;

    public ObservableCollection<BookCollectionFolderViewModel> CollectionFolders { get; } = [];

    private async Task RefreshBookCollectionsAsync()
    {
        _bookCollections = await _library.GetCollectionsAsync();
        CollectionFolders.Clear();
        foreach (var collection in _bookCollections)
        {
            var booksInCollection = ViewModel.LibraryBooks
                .Where(book => book.CollectionIds.Contains(collection.Id))
                .OrderByDescending(book => book.UpdatedAt)
                .ToList();
            var count = booksInCollection.Count;
            CollectionFolders.Add(new BookCollectionFolderViewModel(
                collection,
                count,
                _paths.Data,
                booksInCollection.Take(3).Select(book => book.CoverPath).ToArray()));
        }

        if (ViewModel.CollectionFilterId is { } selectedId
            && _bookCollections.All(collection => collection.Id != selectedId))
        {
            ViewModel.CollectionFilterId = null;
            ViewModel.CollectionFilterName = null;
        }
        else if (ViewModel.CollectionFilterId is { } activeId
                 && CollectionFolders.FirstOrDefault(folder => folder.Collection.Id == activeId) is { } activeFolder)
        {
            ActiveCollectionTitleText.Text = $"{activeFolder.Name} · {activeFolder.BookCountLabel}";
        }
        UpdateCollectionEmptyState();
    }

    private Task ShowAllBooksAsync()
    {
        ViewModel.CollectionFilterId = null;
        ViewModel.CollectionFilterName = null;
        RefreshLibraryView();
        SetLibraryViewMode(LibraryViewMode.Grid);
        ShowLibrary();
        return Task.CompletedTask;
    }

    private void CollectionFolderGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not BookCollectionFolderViewModel folder) return;
        ViewModel.CollectionFilterId = folder.Collection.Id;
        ViewModel.CollectionFilterName = folder.Collection.Name;
        RefreshLibraryView();
        SetLibraryViewMode(LibraryViewMode.Grid);
        ActiveCollectionTitleText.Text = $"{folder.Name} · {folder.BookCountLabel}";
    }

    private void BackToCollectionsButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CollectionFilterId = null;
        ViewModel.CollectionFilterName = null;
        RefreshLibraryView();
        SetLibraryViewMode(LibraryViewMode.Collections);
    }

    private void UpdateCollectionEmptyState()
    {
        if (CollectionFolderGrid is null || EmptyCollectionState is null) return;
        EmptyCollectionState.Visibility = _libraryViewMode == LibraryViewMode.Collections
            && CollectionFolders.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private TaskCompletionSource<string?>? _createCollectionCompletion;

    // Monochrome name input overlay; returns the trimmed name or null on cancel.
    private Task<string?> PromptCreateCollectionNameAsync(string? bookTitle = null)
    {
        if (_createCollectionCompletion is not null)
            return _createCollectionCompletion.Task;

        CreateCollectionNameBox.Text = string.Empty;
        CreateCollectionMessageText.Text = bookTitle is null
            ? "输入收藏夹名称，创建后可在书籍右键菜单中把书籍加入其中。"
            : $"输入收藏夹名称，创建后会自动把《{bookTitle}》加入其中。";
        CreateCollectionOkButton.Content = bookTitle is null ? "创建" : "创建并加入";
        CreateCollectionOverlay.Visibility = Visibility.Visible;
        CreateCollectionOverlay.Focus(FocusState.Programmatic);
        CreateCollectionNameBox.Focus(FocusState.Programmatic);

        var completion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _createCollectionCompletion = completion;
        return completion.Task;
    }

    private async void CreateCollectionOkButton_Click(object sender, RoutedEventArgs e)
    {
        var name = CreateCollectionNameBox.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            await ShowMessageAsync("名称不能为空", "请输入收藏夹名称。");
            return;
        }
        CompleteCreateCollectionName(name);
    }

    private void CreateCollectionCancelButton_Click(object sender, RoutedEventArgs e) =>
        CompleteCreateCollectionName(null);

    private void CreateCollectionOverlay_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            CompleteCreateCollectionName(null);
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            CreateCollectionOkButton_Click(sender, e);
        }
    }

    private void CompleteCreateCollectionName(string? name)
    {
        var completion = _createCollectionCompletion;
        if (completion is null) return;
        _createCollectionCompletion = null;
        CreateCollectionOverlay.Visibility = Visibility.Collapsed;
        completion.TrySetResult(name);
    }

    private async Task<BookCollection?> PromptCreateBookCollectionCoreAsync(string? bookTitle = null)
    {
        var name = await PromptCreateCollectionNameAsync(bookTitle);
        if (string.IsNullOrWhiteSpace(name)) return null;

        try
        {
            var collection = await _library.CreateCollectionAsync(name);
            await RefreshLibraryAsync();
            await RefreshBookCollectionsAsync();
            return collection;
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法创建收藏夹", exception.Message);
            return null;
        }
    }

    private async Task<BookCollection?> PromptCreateBookCollectionAsync(Book bookToAdd)
    {
        var collection = await PromptCreateBookCollectionCoreAsync(bookToAdd.Title);
        if (collection is null) return null;
        try
        {
            if (_appSettings.CollectionsMutuallyExclusive)
            {
                foreach (var otherCollectionId in bookToAdd.CollectionIds.ToArray())
                    await _library.RemoveBookFromCollectionAsync(bookToAdd.Id, otherCollectionId);
            }
            await _library.AddBookToCollectionAsync(bookToAdd.Id, collection.Id);
            await RefreshLibraryAsync();
            await RefreshBookCollectionsAsync();
            TaskStatusText.Text = $"已创建收藏夹“{collection.Name}”并加入《{bookToAdd.Title}》";
            return collection;
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法加入收藏夹", exception.Message);
            return collection;
        }
    }

    private async Task<BookCollection?> CreateEmptyBookCollectionAsync()
    {
        var collection = await PromptCreateBookCollectionCoreAsync("创建");
        if (collection is null) return null;
        TaskStatusText.Text = $"已创建收藏夹“{collection.Name}”";
        return collection;
    }

    private async void CreateBookCollectionMenuItem_Click(object sender, RoutedEventArgs e)
        => await CreateEmptyBookCollectionAsync();

    private void AddBookCollectionContextMenu(MenuFlyout flyout, Book book)
    {
        var submenu = new MenuFlyoutSubItem
        {
            Text = "收藏夹",
            Tag = new BookCollectionMenuMarker(book)
        };
        foreach (var collection in _bookCollections)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = collection.Name,
                IsChecked = book.CollectionIds.Contains(collection.Id),
                Tag = new BookCollectionMenuContext(book, collection)
            };
            item.Click += BookCollectionMembershipMenuItem_Click;
            submenu.Items.Add(item);
        }

        if (_bookCollections.Count > 0)
            submenu.Items.Add(new MenuFlyoutSeparator());
        var createItem = new MenuFlyoutItem { Text = "新建收藏夹…", Tag = book };
        createItem.Click += CreateBookCollectionForBookMenuItem_Click;
        submenu.Items.Add(createItem);

        if (_bookCollections.Count > 0)
        {
            var deleteSubmenu = new MenuFlyoutSubItem { Text = "删除收藏夹" };
            foreach (var collection in _bookCollections)
            {
                var deleteItem = new MenuFlyoutItem { Text = collection.Name, Tag = collection };
                deleteItem.Click += DeleteBookCollectionMenuItem_Click;
                deleteSubmenu.Items.Add(deleteItem);
            }
            submenu.Items.Add(deleteSubmenu);
        }

        var deleteIndex = flyout.Items
            .Select((item, index) => (item, index))
            .FirstOrDefault(entry => entry.item is MenuFlyoutSubItem { Text: "删除格式" })
            .index;
        flyout.Items.Insert(Math.Max(0, deleteIndex - 1), submenu);
    }

    private async void BookCollectionMembershipMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: BookCollectionMenuContext context } item) return;

        try
        {
            if (item.IsChecked)
            {
                if (_appSettings.CollectionsMutuallyExclusive)
                {
                    foreach (var otherCollectionId in context.Book.CollectionIds
                        .Where(id => id != context.Collection.Id)
                        .ToArray())
                    {
                        await _library.RemoveBookFromCollectionAsync(context.Book.Id, otherCollectionId);
                    }
                }
                await _library.AddBookToCollectionAsync(context.Book.Id, context.Collection.Id);
            }
            else
                await _library.RemoveBookFromCollectionAsync(context.Book.Id, context.Collection.Id);
            await RefreshLibraryAsync();
            await RefreshBookCollectionsAsync();
            TaskStatusText.Text = item.IsChecked
                ? $"已将《{context.Book.Title}》加入“{context.Collection.Name}”"
                : $"已将《{context.Book.Title}》移出“{context.Collection.Name}”";
        }
        catch (Exception exception)
        {
            item.IsChecked = !item.IsChecked;
            await ShowMessageAsync("无法更新收藏夹", exception.Message);
        }
    }

    private async void CreateBookCollectionForBookMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: Book book })
            await PromptCreateBookCollectionAsync(book);
    }

    private async void DeleteBookCollectionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: BookCollection collection }) return;
        if (!await ShowDevicePromptAsync(
                "删除收藏夹？",
                $"将删除“{collection.Name}”。其中的书籍仍会保留在电脑书库。",
                "删除",
                "取消")) return;

        try
        {
            var wasActiveCollection = ViewModel.CollectionFilterId == collection.Id;
            await _library.DeleteCollectionAsync(collection.Id);
            if (wasActiveCollection)
            {
                ViewModel.CollectionFilterId = null;
                ViewModel.CollectionFilterName = null;
            }
            await RefreshLibraryAsync();
            await RefreshBookCollectionsAsync();
            if (wasActiveCollection) SetLibraryViewMode(LibraryViewMode.Collections);
            TaskStatusText.Text = $"已删除收藏夹“{collection.Name}”";
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法删除收藏夹", exception.Message);
        }
    }
}

public sealed class BookCollectionFolderViewModel
{
    private readonly BitmapImage?[] _covers = new BitmapImage?[3];

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

    private static BitmapImage? LoadCover(string dataRoot, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(dataRoot, path));
            return File.Exists(fullPath) ? new BitmapImage(new Uri(fullPath)) : null;
        }
        catch
        {
            return null;
        }
    }

    public BookCollection Collection { get; }
    public string Name => Collection.Name;
    public int BookCount { get; }
    public string BookCountLabel => $"{BookCount} 本书";
    public BitmapImage? Cover1 => _covers[0];
    public BitmapImage? Cover2 => _covers[1];
    public BitmapImage? Cover3 => _covers[2];
}
