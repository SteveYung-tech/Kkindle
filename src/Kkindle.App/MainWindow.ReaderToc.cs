using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
    private const double ReaderCompactScrollAnimationDurationMs = 160d;
    private const double ReaderCompactMarkerHoverScale = 1.3d;
    private const double ReaderCompactMarkerAnimationDurationMs = 140d;
    private IReadOnlyList<EpubReaderNavigationItem> _readerCompactNavigationItems = [];
    private DispatcherQueueTimer? _readerCompactScrollTimer;
    private bool _readerCompactScrollAnimating;
    private double _readerCompactScrollStart;
    private double _readerCompactScrollTarget;
    private DateTimeOffset _readerCompactScrollStartedAt;

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

        var baseOffset = _readerCompactScrollAnimating
            ? _readerCompactScrollTarget
            : scrollViewer.VerticalOffset;
        _readerCompactScrollStart = scrollViewer.VerticalOffset;
        _readerCompactScrollTarget = Math.Clamp(
            baseOffset - delta * 0.45,
            0,
            scrollViewer.ScrollableHeight);
        _readerCompactScrollStartedAt = DateTimeOffset.UtcNow;
        _readerCompactScrollAnimating = true;
        EnsureReaderCompactScrollTimer();
        _readerCompactScrollTimer!.Start();
        e.Handled = true;
    }

    private void EnsureReaderCompactScrollTimer()
    {
        if (_readerCompactScrollTimer is not null) return;
        _readerCompactScrollTimer = DispatcherQueue.CreateTimer();
        _readerCompactScrollTimer.Interval = TimeSpan.FromMilliseconds(16);
        _readerCompactScrollTimer.Tick += ReaderCompactScrollTimer_Tick;
    }

    private void ReaderCompactScrollTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!_readerCompactScrollAnimating
            || ReaderTocCompactScrollViewer.Visibility != Visibility.Visible)
        {
            StopReaderCompactScrollAnimation();
            return;
        }

        var progress = Math.Clamp(
            (DateTimeOffset.UtcNow - _readerCompactScrollStartedAt).TotalMilliseconds
                / ReaderCompactScrollAnimationDurationMs,
            0,
            1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        var offset = _readerCompactScrollStart
            + (_readerCompactScrollTarget - _readerCompactScrollStart) * eased;
        ReaderTocCompactScrollViewer.ChangeView(null, offset, null, disableAnimation: true);

        if (progress >= 1) StopReaderCompactScrollAnimation();
    }

    private void StopReaderCompactScrollAnimation()
    {
        _readerCompactScrollAnimating = false;
        _readerCompactScrollTimer?.Stop();
    }

    private void ReaderCompactTocMarker_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button { Content: Border marker })
            AnimateCompactMarker(marker, ReaderCompactMarkerHoverScale);
    }

    private void ReaderCompactTocMarker_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button { Content: Border marker })
            AnimateCompactMarker(marker, 1);
    }

    private static void AnimateCompactMarker(Border marker, double targetScale)
    {
        if (marker.RenderTransform is not ScaleTransform scale) return;

        var animation = new DoubleAnimation
        {
            From = scale.ScaleX,
            To = targetScale,
            Duration = new Duration(TimeSpan.FromMilliseconds(ReaderCompactMarkerAnimationDurationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, scale);
        Storyboard.SetTargetProperty(animation, "ScaleX");
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void SetReaderTocMinimal(bool minimal)
    {
        _readerTocMinimal = minimal;
        _readerTocExpanded = !minimal;
        ApplyReaderPanelLayout();
    }

    private void SetReaderCompactNavigationItems(IReadOnlyList<EpubReaderNavigationItem> items)
    {
        StopReaderCompactScrollAnimation();
        _readerCompactNavigationItems = items;
        RefreshReaderCompactMarkers();
    }

    private void ClearReaderCompactNavigationItems()
    {
        StopReaderCompactScrollAnimation();
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
