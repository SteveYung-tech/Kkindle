using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Kkindle.Core;
using Windows.Foundation;

namespace Kkindle;

public sealed partial class MainWindow
{
    private bool _rubberBandSelecting;
    private bool _rubberBandPointerCaptured;
    private bool _rubberBandPressOnBook;
    private bool _rubberBandPressWasLeft;
    private Point _rubberBandStart;
    private Point _rubberBandCurrent;
    private readonly HashSet<Guid> _multiSelectedBookIds = [];
    private Windows.System.VirtualKeyModifiers _lastLibraryPointerModifiers;
    private BookCardViewModel? _multiSelectAnchor;

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
        if (e.ClickedItem is BookCardViewModel card) HandleLibraryCardClick(card);
    }

    private void BookList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BookCardViewModel card) HandleLibraryCardClick(card);
    }

    private void BookList_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _lastLibraryPointerModifiers = e.KeyModifiers;
    }

    private void HandleLibraryCardClick(BookCardViewModel card)
    {
        var modifiers = _lastLibraryPointerModifiers;
        if ((modifiers & Windows.System.VirtualKeyModifiers.Control) != 0)
        {
            ToggleMultiSelection(card);
            return;
        }

        if ((modifiers & Windows.System.VirtualKeyModifiers.Shift) != 0)
        {
            ApplyRangeSelection(card);
            return;
        }

        ClearMultiSelection();
        SelectBook(card.Book);
        _multiSelectAnchor = card;
    }

    // Left-button drag over the grid draws a rubber band; every book card that
    // intersects the rectangle joins the multi selection and can be deleted
    // together from the floating action bar. A plain left-click (no drag) keeps
    // the normal single-book behavior.
    private void BookGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _lastLibraryPointerModifiers = e.KeyModifiers;
        var point = e.GetCurrentPoint(BookGrid);
        _rubberBandPressOnBook = FindBookCard(e.OriginalSource as DependencyObject) is not null;
        _rubberBandPressWasLeft = point.Properties.IsLeftButtonPressed;
        if (!_rubberBandPressWasLeft) return;
        _rubberBandStart = point.Position;
        _rubberBandCurrent = point.Position;
        _rubberBandSelecting = false;
        _rubberBandPointerCaptured = false;
    }

    private void ToggleMultiSelection(BookCardViewModel card)
    {
        if (_multiSelectedBookIds.Contains(card.Book.Id))
        {
            _multiSelectedBookIds.Remove(card.Book.Id);
            card.IsMultiSelected = false;
        }
        else
        {
            _multiSelectedBookIds.Add(card.Book.Id);
            card.IsMultiSelected = true;
        }

        _multiSelectAnchor = card;
        UpdateMultiSelectBar();
    }

    private void ApplyRangeSelection(BookCardViewModel clicked)
    {
        var books = ViewModel.Books.ToList();
        var anchorIndex = _multiSelectAnchor is null
            ? -1
            : books.FindIndex(candidate => ReferenceEquals(candidate, _multiSelectAnchor));
        var clickedIndex = books.FindIndex(candidate => ReferenceEquals(candidate, clicked));
        if (clickedIndex < 0) return;

        _multiSelectedBookIds.Clear();
        foreach (var card in ViewModel.Books) card.IsMultiSelected = false;

        if (anchorIndex < 0)
        {
            _multiSelectedBookIds.Add(clicked.Book.Id);
            clicked.IsMultiSelected = true;
        }
        else
        {
            var start = Math.Min(anchorIndex, clickedIndex);
            var end = Math.Max(anchorIndex, clickedIndex);
            for (var index = start; index <= end; index++)
            {
                var card = books[index];
                _multiSelectedBookIds.Add(card.Book.Id);
                card.IsMultiSelected = true;
            }
        }

        _multiSelectAnchor = clicked;
        UpdateMultiSelectBar();
    }

    private void BookGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(BookGrid);
        if (!point.Properties.IsLeftButtonPressed) return;
        _rubberBandCurrent = point.Position;
        if (!_rubberBandSelecting)
        {
            if (Math.Abs(_rubberBandCurrent.X - _rubberBandStart.X) < 8
                && Math.Abs(_rubberBandCurrent.Y - _rubberBandStart.Y) < 8)
                return;
            _rubberBandSelecting = true;
            _rubberBandPointerCaptured = BookGrid.CapturePointer(e.Pointer);
            RubberBandRectangle.Visibility = Visibility.Visible;
        }

        e.Handled = true;
        UpdateRubberBand();
    }

    private void BookGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_rubberBandSelecting)
        {
            // A plain left-click on empty grid space clears the multi selection,
            // matching the usual desktop rubber-band behavior.
            if (_rubberBandPressWasLeft && !_rubberBandPressOnBook)
                ClearMultiSelection();
            return;
        }
        e.Handled = true;
        _rubberBandCurrent = e.GetCurrentPoint(BookGrid).Position;
        UpdateRubberBand();
        FinishRubberBandSelection();
    }

    private void BookGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_rubberBandSelecting) return;
        FinishRubberBandSelection();
    }

    private void BookGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        e.Handled = true;
        ClearMultiSelection();
    }

    private void UpdateRubberBand()
    {
        var left = Math.Min(_rubberBandStart.X, _rubberBandCurrent.X);
        var top = Math.Min(_rubberBandStart.Y, _rubberBandCurrent.Y);
        var width = Math.Abs(_rubberBandCurrent.X - _rubberBandStart.X);
        var height = Math.Abs(_rubberBandCurrent.Y - _rubberBandStart.Y);
        RubberBandRectangle.Margin = new Thickness(left, top, 0, 0);
        RubberBandRectangle.Width = width;
        RubberBandRectangle.Height = height;
        ApplyMultiSelection(new Rect(left, top, width, height));
    }

    private void FinishRubberBandSelection()
    {
        _rubberBandSelecting = false;
        if (_rubberBandPointerCaptured)
        {
            _rubberBandPointerCaptured = false;
            BookGrid.ReleasePointerCaptures();
        }
        RubberBandRectangle.Visibility = Visibility.Collapsed;
        UpdateMultiSelectBar();
    }

    private void ApplyMultiSelection(Rect selection)
    {
        _multiSelectedBookIds.Clear();
        if (selection.Width < 1 || selection.Height < 1)
        {
            foreach (var card in ViewModel.Books) card.IsMultiSelected = false;
            return;
        }

        foreach (var card in ViewModel.Books)
        {
            var selected = GetBookCardBounds(card) is Rect bounds && RectsIntersect(bounds, selection);
            card.IsMultiSelected = selected;
            if (selected) _multiSelectedBookIds.Add(card.Book.Id);
        }
        _multiSelectAnchor = ViewModel.Books.FirstOrDefault(card => card.IsMultiSelected);
    }

    private static bool RectsIntersect(Rect first, Rect second) =>
        first.X < second.X + second.Width
        && second.X < first.X + first.Width
        && first.Y < second.Y + second.Height
        && second.Y < first.Y + first.Height;

    private Rect? GetBookCardBounds(BookCardViewModel card)
    {
        if (BookGrid.ContainerFromItem(card) is not FrameworkElement container) return null;
        var origin = container.TransformToVisual(BookGrid).TransformPoint(new Point(0, 0));
        return new Rect(origin.X, origin.Y, container.ActualWidth, container.ActualHeight);
    }

    private void UpdateMultiSelectBar()
    {
        if (_multiSelectedBookIds.Count == 0)
        {
            BookMultiSelectBar.Visibility = Visibility.Collapsed;
            return;
        }

        BookMultiSelectCountText.Text = $"已选择 {_multiSelectedBookIds.Count} 本";
        BookMultiSelectBar.Visibility = Visibility.Visible;
    }

    private void ClearMultiSelection()
    {
        if (_multiSelectedBookIds.Count == 0 && !ViewModel.Books.Any(card => card.IsMultiSelected))
        {
            BookMultiSelectBar.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var card in ViewModel.Books) card.IsMultiSelected = false;
        _multiSelectedBookIds.Clear();
        _multiSelectAnchor = null;
        BookMultiSelectBar.Visibility = Visibility.Collapsed;
    }

    private void ClearMultiSelectionButton_Click(object sender, RoutedEventArgs e) => ClearMultiSelection();

    private List<Book> GetMultiSelectedBooks() =>
        _multiSelectedBookIds
            .Select(id => ViewModel.Books.FirstOrDefault(card => card.Book.Id == id)?.Book)
            .Where(book => book is not null)
            .Cast<Book>()
            .ToList();

    private void BookCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BookCardViewModel card } element) return;

        if (_multiSelectedBookIds.Contains(card.Book.Id) && _multiSelectedBookIds.Count > 0)
        {
            var selected = GetMultiSelectedBooks();
            var sendDeviceItem = new MenuFlyoutItem { Text = $"发送到 Kindle 设备 ({selected.Count})" };
            sendDeviceItem.Click += SendSelectedBooksToKindleDeviceMenuItem_Click;
            var sendEmailItem = new MenuFlyoutItem { Text = $"发送到 Kindle 邮箱 ({selected.Count})" };
            sendEmailItem.Click += SendSelectedBooksToKindleEmailMenuItem_Click;
            var deleteItem = new MenuFlyoutItem { Text = $"删除所选 ({selected.Count})" };
            deleteItem.Click += DeleteSelectedBooksButton_Click;
            var clearItem = new MenuFlyoutItem { Text = "取消选择" };
            clearItem.Click += ClearMultiSelectionMenuItem_Click;

            var flyout = new MenuFlyout();
            if (Application.Current.Resources["MonochromeMenuFlyoutPresenterStyle"] is Style presenterStyle)
                flyout.MenuFlyoutPresenterStyle = presenterStyle;
            flyout.Items.Add(sendDeviceItem);
            flyout.Items.Add(sendEmailItem);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(deleteItem);
            flyout.Items.Add(clearItem);
            flyout.ShowAt(element, e.GetPosition(element));
            e.Handled = true;
            return;
        }

        // Right-clicking an unselected card collapses the current multi selection
        // and lets the built-in single-book context menu take over.
        if (_multiSelectedBookIds.Count > 0) ClearMultiSelection();
    }

    private async void DeleteSelectedBooksButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetMultiSelectedBooks();
        if (selected.Count == 0) return;

        if (_readerBook is { } reading && selected.Any(book => book.Id == reading.Id))
        {
            await ShowMessageAsync("无法删除书籍", "当前正在阅读其中一本书，请先关闭阅读器。");
            return;
        }

        var titleLines = string.Join("\n", selected.Take(3).Select(book => "《" + book.Title + "》"));
        if (selected.Count > 3) titleLines += $"\n…等 {selected.Count} 本";
        if (!await ShowDevicePromptAsync(
                $"删除选中的 {selected.Count} 本书？",
                "将从 Kkindle 书库中删除以下书籍的所有格式、书籍记录和封面：\n\n" + titleLines,
                "删除",
                "取消")) return;

        try
        {
            foreach (var book in selected)
            {
                await _library.DeleteAsync(book.Id);
                if (_selectedBook?.Id == book.Id)
                {
                    _selectedBook = null;
                    CloseDetails();
                }
            }

            ClearMultiSelection();
            TaskStatusText.Text = $"已删除 {selected.Count} 本书";
            await RefreshLibraryAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法删除书籍", exception.Message);
        }
    }

    private async void SendSelectedBooksToKindleButton_Click(object sender, RoutedEventArgs e) =>
        await SendSelectedBooksToKindleDeviceCoreAsync();

    private async void SendSelectedBooksToKindleDeviceMenuItem_Click(object sender, RoutedEventArgs e) =>
        await SendSelectedBooksToKindleDeviceCoreAsync();

    private async Task SendSelectedBooksToKindleDeviceCoreAsync()
    {
        var books = GetMultiSelectedBooks();
        if (books.Count == 0) return;
        await TrackDeviceOperationAsync(() => SendBooksToKindleFromContextAsync(books));
    }

    private async void SendSelectedBooksToKindleEmailButton_Click(object sender, RoutedEventArgs e) =>
        await SendSelectedBooksToKindleEmailCoreAsync();

    private async void SendSelectedBooksToKindleEmailMenuItem_Click(object sender, RoutedEventArgs e) =>
        await SendSelectedBooksToKindleEmailCoreAsync();

    private async Task SendSelectedBooksToKindleEmailCoreAsync()
    {
        var books = GetMultiSelectedBooks();
        if (books.Count == 0) return;
        await SendBooksToKindleEmailAsync(books);
    }

    private void ClearMultiSelectionMenuItem_Click(object sender, RoutedEventArgs e) => ClearMultiSelection();

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
