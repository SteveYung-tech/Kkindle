using System.Collections.ObjectModel;
using Kkindle.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
            var count = ViewModel.LibraryBooks.Count(book => book.CollectionIds.Contains(collection.Id));
            CollectionFolders.Add(new BookCollectionFolderViewModel(collection, count));
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

    private async Task<BookCollection?> PromptCreateBookCollectionAsync(Book bookToAdd)
    {
        var nameBox = new TextBox
        {
            PlaceholderText = "例如：待读、技术、小说",
            MaxLength = 60,
            MinWidth = 320
        };
        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "新建收藏夹",
            Content = nameBox,
            PrimaryButtonText = "创建并加入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

        try
        {
            var collection = await _library.CreateCollectionAsync(nameBox.Text);
            await _library.AddBookToCollectionAsync(bookToAdd.Id, collection.Id);
            await RefreshLibraryAsync();
            await RefreshBookCollectionsAsync();
            TaskStatusText.Text = $"已创建收藏夹“{collection.Name}”并加入《{bookToAdd.Title}》";
            return collection;
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法创建收藏夹", exception.Message);
            return null;
        }
    }

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
                await _library.AddBookToCollectionAsync(context.Book.Id, context.Collection.Id);
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
        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "删除收藏夹？",
            Content = $"将删除“{collection.Name}”。其中的书籍仍会保留在电脑书库。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

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
    public BookCollectionFolderViewModel(BookCollection collection, int bookCount)
    {
        Collection = collection;
        BookCount = bookCount;
    }

    public BookCollection Collection { get; }
    public string Name => Collection.Name;
    public int BookCount { get; }
    public string BookCountLabel => $"{BookCount} 本书";
}
