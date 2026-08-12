using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Kkindle.Core;

namespace Kkindle;

public sealed partial class MainWindow
{
    private void LibraryPane_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (DetailPane.Visibility == Visibility.Visible) CloseDetails();
        if (KindleEmailSettingsPane.Visibility == Visibility.Visible
            || ZLibraryAccountPane.Visibility == Visibility.Visible
            || ReaderAiSettingsPane.Visibility == Visibility.Visible)
            HideSettingsPanel();
    }

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
        await OpenReadingMaterialsPageAsync(exportMode: false);
    }

    private async void ReaderExportNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(ReaderExportNavigationButton);
        await OpenReadingMaterialsPageAsync(exportMode: true);
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
