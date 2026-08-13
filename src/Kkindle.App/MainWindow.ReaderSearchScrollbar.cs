using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Kkindle;

public sealed partial class MainWindow
{
    private const double ReaderSearchScrollBarWidth = 14d;
    private const double ReaderSearchScrollBarThumbWidth = 8d;
    private const double ReaderSearchScrollBarMinimumThumbHeight = 28d;
    private const double ReaderSearchScrollBarHideDelayMs = 900d;

    private ScrollViewer? _readerSearchResultScrollViewer;
    private DispatcherQueueTimer? _readerSearchScrollBarHideTimer;
    private bool _readerSearchScrollBarRetryAttached;
    private bool _readerSearchScrollBarRefreshQueued;
    private bool _readerSearchScrollBarRevealPending;
    private bool _readerSearchScrollBarPointerOver;
    private bool _readerSearchScrollBarDragging;
    private double _readerSearchScrollBarDragOffset;
    private double _readerSearchScrollBarTrackHeight;
    private double _readerSearchScrollBarThumbHeight;

    private void ReaderSearchResultList_Loaded(object sender, RoutedEventArgs e) =>
        QueueReaderSearchScrollBarRefresh(reveal: false);

    private void QueueReaderSearchScrollBarRefresh(bool reveal)
    {
        _readerSearchScrollBarRevealPending |= reveal;
        if (_readerSearchScrollBarRefreshQueued) return;

        _readerSearchScrollBarRefreshQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _readerSearchScrollBarRefreshQueued = false;
            if (ReaderSearchPanel.Visibility == Visibility.Visible
                && ReaderSearchResultList.Visibility == Visibility.Visible)
                ReaderSearchScrollBarOverlay.Visibility = Visibility.Visible;
            AttachReaderSearchResultScrollViewer();
            var canScroll = UpdateReaderSearchScrollBarGeometry();
            if (_readerSearchScrollBarRevealPending)
            {
                _readerSearchScrollBarRevealPending = false;
                if (canScroll) RevealReaderSearchScrollBar();
            }
        });
    }

    private void AttachReaderSearchResultScrollViewer()
    {
        var viewer = FindDescendants<ScrollViewer>(ReaderSearchResultList).FirstOrDefault();
        if (ReferenceEquals(viewer, _readerSearchResultScrollViewer))
        {
            UpdateReaderSearchScrollBarGeometry();
            return;
        }

        if (_readerSearchResultScrollViewer is not null)
        {
            _readerSearchResultScrollViewer.ViewChanged -= ReaderSearchResultScrollViewer_ViewChanged;
            _readerSearchResultScrollViewer.SizeChanged -= ReaderSearchResultScrollViewer_SizeChanged;
            _readerSearchResultScrollViewer.PointerEntered -= ReaderSearchResultScrollViewer_PointerEntered;
            _readerSearchResultScrollViewer.PointerExited -= ReaderSearchResultScrollViewer_PointerExited;
        }

        _readerSearchResultScrollViewer = viewer;
        if (viewer is null)
        {
            if (!_readerSearchScrollBarRetryAttached)
            {
                _readerSearchScrollBarRetryAttached = true;
                ReaderSearchResultList.LayoutUpdated += ReaderSearchResultList_LayoutUpdated;
            }
            return;
        }

        _readerSearchScrollBarRetryAttached = false;
        ReaderSearchResultList.LayoutUpdated -= ReaderSearchResultList_LayoutUpdated;
        viewer.ViewChanged += ReaderSearchResultScrollViewer_ViewChanged;
        viewer.SizeChanged += ReaderSearchResultScrollViewer_SizeChanged;
        viewer.PointerEntered += ReaderSearchResultScrollViewer_PointerEntered;
        viewer.PointerExited += ReaderSearchResultScrollViewer_PointerExited;
        _readerSearchScrollBarHideTimer ??= CreateReaderSearchScrollBarHideTimer();
        UpdateReaderSearchScrollBarGeometry();
    }

    private DispatcherQueueTimer CreateReaderSearchScrollBarHideTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(ReaderSearchScrollBarHideDelayMs);
        timer.IsRepeating = false;
        timer.Tick += ReaderSearchScrollBarHideTimer_Tick;
        return timer;
    }

    private void ReaderSearchResultList_LayoutUpdated(object? sender, object e)
    {
        ReaderSearchResultList.LayoutUpdated -= ReaderSearchResultList_LayoutUpdated;
        _readerSearchScrollBarRetryAttached = false;
        AttachReaderSearchResultScrollViewer();
    }

    private void ReaderSearchResultScrollViewer_ViewChanged(
        object? sender,
        ScrollViewerViewChangedEventArgs e)
    {
        if (!UpdateReaderSearchScrollBarGeometry()) return;
        RevealReaderSearchScrollBar();
    }

    private void ReaderSearchResultScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e) =>
        QueueReaderSearchScrollBarRefresh(reveal: false);

    private void ReaderSearchResultScrollViewer_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        RevealReaderSearchScrollBar();

    private void ReaderSearchResultScrollViewer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_readerSearchScrollBarPointerOver) RestartReaderSearchScrollBarHideTimer();
    }

    private void ReaderSearchScrollBarOverlay_SizeChanged(object sender, SizeChangedEventArgs e) =>
        QueueReaderSearchScrollBarRefresh(reveal: false);

    private void ReaderSearchScrollBarOverlay_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _readerSearchScrollBarPointerOver = true;
        RevealReaderSearchScrollBar();
    }

    private void ReaderSearchScrollBarOverlay_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _readerSearchScrollBarPointerOver = false;
        if (!_readerSearchScrollBarDragging) RestartReaderSearchScrollBarHideTimer();
    }

    private void ReaderSearchScrollBarOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_readerSearchResultScrollViewer is null
            || _readerSearchResultScrollViewer.ScrollableHeight <= 0)
            return;

        var point = e.GetCurrentPoint(ReaderSearchScrollBarOverlay);
        if (!point.Properties.IsLeftButtonPressed) return;

        RevealReaderSearchScrollBar();
        var y = point.Position.Y;
        var thumbTop = Canvas.GetTop(ReaderSearchScrollBarThumb);
        if (double.IsNaN(thumbTop)) thumbTop = 0;
        if (y >= thumbTop && y <= thumbTop + _readerSearchScrollBarThumbHeight)
        {
            _readerSearchScrollBarDragging = true;
            _readerSearchScrollBarDragOffset = y - thumbTop;
            ReaderSearchScrollBarOverlay.CapturePointer(e.Pointer);
        }
        else
        {
            var page = Math.Max(1, _readerSearchResultScrollViewer.ViewportHeight);
            var target = y < thumbTop
                ? _readerSearchResultScrollViewer.VerticalOffset - page
                : _readerSearchResultScrollViewer.VerticalOffset + page;
            _readerSearchResultScrollViewer.ChangeView(null, target, null, disableAnimation: true);
        }

        e.Handled = true;
    }

    private void ReaderSearchScrollBarOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_readerSearchScrollBarDragging || _readerSearchResultScrollViewer is null) return;

        var point = e.GetCurrentPoint(ReaderSearchScrollBarOverlay);
        var maxThumbTop = Math.Max(0, _readerSearchScrollBarTrackHeight - _readerSearchScrollBarThumbHeight);
        var thumbTop = Math.Clamp(
            point.Position.Y - _readerSearchScrollBarDragOffset,
            0,
            maxThumbTop);
        var ratio = maxThumbTop <= 0 ? 0 : thumbTop / maxThumbTop;
        _readerSearchResultScrollViewer.ChangeView(
            null,
            ratio * _readerSearchResultScrollViewer.ScrollableHeight,
            null,
            disableAnimation: true);
        e.Handled = true;
    }

    private void ReaderSearchScrollBarOverlay_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_readerSearchScrollBarDragging) return;
        _readerSearchScrollBarDragging = false;
        ReaderSearchScrollBarOverlay.ReleasePointerCapture(e.Pointer);
        if (!_readerSearchScrollBarPointerOver) RestartReaderSearchScrollBarHideTimer();
        e.Handled = true;
    }

    private bool UpdateReaderSearchScrollBarGeometry()
    {
        var viewer = _readerSearchResultScrollViewer;
        var trackHeight = ReaderSearchScrollBarOverlay.ActualHeight;
        if (viewer is null
            || ReaderSearchPanel.Visibility != Visibility.Visible
            || ReaderSearchResultList.Visibility != Visibility.Visible
            || trackHeight <= 0
            || viewer.ViewportHeight <= 0
            || viewer.ScrollableHeight <= 0)
        {
            HideReaderSearchScrollBar();
            return false;
        }

        var viewportHeight = viewer.ViewportHeight;
        var thumbHeight = Math.Clamp(
            trackHeight * viewportHeight / (viewportHeight + viewer.ScrollableHeight),
            ReaderSearchScrollBarMinimumThumbHeight,
            trackHeight);
        var maxThumbTop = Math.Max(0, trackHeight - thumbHeight);
        var thumbTop = viewer.ScrollableHeight <= 0
            ? 0
            : maxThumbTop * viewer.VerticalOffset / viewer.ScrollableHeight;

        _readerSearchScrollBarTrackHeight = trackHeight;
        _readerSearchScrollBarThumbHeight = thumbHeight;
        ReaderSearchScrollBarTrack.Height = trackHeight;
        ReaderSearchScrollBarThumb.Height = thumbHeight;
        Canvas.SetTop(ReaderSearchScrollBarTrack, 0);
        Canvas.SetTop(ReaderSearchScrollBarThumb, Math.Clamp(thumbTop, 0, maxThumbTop));
        return true;
    }

    private void RevealReaderSearchScrollBar()
    {
        if (!UpdateReaderSearchScrollBarGeometry()) return;

        _readerSearchScrollBarHideTimer?.Stop();
        ReaderSearchScrollBarOverlay.Visibility = Visibility.Visible;
        ReaderSearchScrollBarOverlay.Opacity = 1;
        ReaderSearchScrollBarOverlay.IsHitTestVisible = true;
        ReaderSearchScrollBarThumb.Opacity = 1;
        if (!_readerSearchScrollBarDragging && !_readerSearchScrollBarPointerOver)
            RestartReaderSearchScrollBarHideTimer();
    }

    private void RestartReaderSearchScrollBarHideTimer()
    {
        if (_readerSearchScrollBarDragging || _readerSearchScrollBarPointerOver) return;
        _readerSearchScrollBarHideTimer?.Stop();
        _readerSearchScrollBarHideTimer?.Start();
    }

    private void ReaderSearchScrollBarHideTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_readerSearchScrollBarDragging || _readerSearchScrollBarPointerOver) return;
        ReaderSearchScrollBarThumb.Opacity = 0;
        ReaderSearchScrollBarOverlay.IsHitTestVisible = false;
    }

    private void HideReaderSearchScrollBar()
    {
        _readerSearchScrollBarHideTimer?.Stop();
        _readerSearchScrollBarDragging = false;
        _readerSearchScrollBarPointerOver = false;
        ReaderSearchScrollBarOverlay.IsHitTestVisible = false;
        ReaderSearchScrollBarThumb.Opacity = 0;
        ReaderSearchScrollBarOverlay.Opacity = 1;
        ReaderSearchScrollBarOverlay.Visibility = ReaderSearchPanel.Visibility == Visibility.Visible
            && ReaderSearchResultList.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void StopReaderSearchScrollbar()
    {
        HideReaderSearchScrollBar();
        _readerSearchScrollBarRevealPending = false;
        if (_readerSearchScrollBarRetryAttached)
        {
            ReaderSearchResultList.LayoutUpdated -= ReaderSearchResultList_LayoutUpdated;
            _readerSearchScrollBarRetryAttached = false;
        }
    }
}
