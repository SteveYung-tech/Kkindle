using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public sealed record ReaderTocMarker(EpubReaderNavigationItem Item, bool IsCurrent)
{
    private static readonly SolidColorBrush BlackBrush = new(Colors.Black);
    private static readonly SolidColorBrush WhiteBrush = new(Colors.White);

    public string Title => Item.Title;
    public SolidColorBrush Fill => IsCurrent ? BlackBrush : WhiteBrush;
    public SolidColorBrush Stroke => BlackBrush;
}

public sealed partial class MainWindow
{
    private const double ReaderTocMinimalWidth = 30d;
    private IReadOnlyList<EpubReaderNavigationItem> _readerCompactNavigationItems = [];

    private void ReaderTocMinimalToggleButton_Click(object sender, RoutedEventArgs e)
    {
        SetReaderTocMinimal(!_readerTocMinimal);
    }

    private void ReaderTocCompactExpandButton_Click(object sender, RoutedEventArgs e)
    {
        SetReaderTocMinimal(false);
    }

    private void ReaderCompactTocItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element
            && element.DataContext is ReaderTocMarker marker)
        {
            NavigateToReaderTocItem(marker.Item);
        }
    }

    private void ReaderTocCompactScrollViewer_PointerWheelChanged(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer) return;
        var delta = e.GetCurrentPoint(scrollViewer).Properties.MouseWheelDelta;
        if (delta == 0) return;

        var target = Math.Clamp(
            scrollViewer.VerticalOffset - delta * 0.45,
            0,
            scrollViewer.ScrollableHeight);
        scrollViewer.ChangeView(null, target, null);
        e.Handled = true;
    }

    private void SetReaderTocMinimal(bool minimal)
    {
        _readerTocMinimal = minimal;
        _readerTocExpanded = !minimal;
        ApplyReaderPanelLayout();
    }

    private void SetReaderCompactNavigationItems(IReadOnlyList<EpubReaderNavigationItem> items)
    {
        _readerCompactNavigationItems = items;
        RefreshReaderCompactMarkers();
    }

    private void ClearReaderCompactNavigationItems()
    {
        _readerCompactNavigationItems = [];
        ReaderTocCompactList.ItemsSource = null;
    }

    private void RefreshReaderCompactMarkers()
    {
        ReaderTocCompactList.ItemsSource = _readerCompactNavigationItems
            .Select(item => new ReaderTocMarker(item, item.ChapterIndex == _readerChapterIndex))
            .ToArray();
    }

    private void NavigateToReaderTocItem(EpubReaderNavigationItem item)
    {
        // A TOC click is an explicit user target: it must start at the target
        // chapter's first line (or its own anchor), never inherit a leftover
        // "move to chapter end" intent from a superseded previous-chapter turn.
        _readerContinuousLocked = false;
        _readerChapterIndex = item.ChapterIndex;
        _readerNavigateToEnd = false;
        SelectReaderTocItem(item);
        UpdateReaderChapterControls();
        _ = NavigateReaderSourceAsync(new Uri(item.Target), 1, animate: true, ReaderNavigationIntent.Toc);
    }
}
