using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Kkindle.Core;

namespace Kkindle;

public sealed partial class MainWindow
{
    private void BookGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BookCardViewModel card) SelectBook(card.Book);
    }

    private void BookList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BookList.SelectedItem is BookCardViewModel card) SelectBook(card.Book);
    }

    private async void BookGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FindBookCard(e.OriginalSource as DependencyObject) is not { } card) return;

        e.Handled = true;
        SelectBook(card.Book);
        await OpenBookAsync(card.Book);
    }

    private async void BookList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FindBookCard(e.OriginalSource as DependencyObject) is not { } card) return;

        e.Handled = true;
        SelectBook(card.Book);
        await OpenBookAsync(card.Book);
    }

    private async void ReaderNotesNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(ReaderNotesNavigationButton);
        await OpenReaderMaterialsAsync(export: false);
    }

    private async void ReaderExportNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(ReaderExportNavigationButton);
        await OpenReaderMaterialsAsync(export: true);
    }

    private async Task OpenReaderMaterialsAsync(bool export)
    {
        var book = _readerBook ?? _selectedBook;
        if (book is null)
        {
            await ShowMessageAsync("阅读资料", "请先在电脑书库选择一本 EPUB 书籍。");
            return;
        }

        var epubFile = ReaderBookSelectionPolicy.SelectEpub(book.Files);
        if (epubFile is null)
        {
            await ShowMessageAsync("阅读资料", "笔记与导出目前仅支持 EPUB；当前书籍没有 EPUB 文件。");
            return;
        }

        if (!ReferenceEquals(_readerBook, book)
            || !string.Equals(_readerBookFile?.Format, "epub", StringComparison.OrdinalIgnoreCase))
            await OpenBookAsync(book, epubFile);

        if (ReaderPane.Visibility != Visibility.Visible || _readerBook is null)
            return;

        ShowReaderNotesTab();
        if (export)
            await ExportReaderAnnotationsAsync(markdown: true);
    }

    private static BookCardViewModel? FindBookCard(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement element && element.DataContext is BookCardViewModel card)
                return card;

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }
}
