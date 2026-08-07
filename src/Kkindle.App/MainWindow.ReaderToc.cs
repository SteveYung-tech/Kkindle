using Microsoft.UI.Xaml;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public sealed partial class MainWindow
{
    private const double ReaderTocMinimalWidth = 30d;

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
            && element.DataContext is EpubReaderNavigationItem item)
        {
            NavigateToReaderTocItem(item);
        }
    }

    private void SetReaderTocMinimal(bool minimal)
    {
        _readerTocMinimal = minimal;
        _readerTocExpanded = !minimal;
        ApplyReaderPanelLayout();
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
