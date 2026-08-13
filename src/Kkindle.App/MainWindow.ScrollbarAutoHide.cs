using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Kkindle;

public sealed partial class MainWindow
{
    private const double ScrollbarAutoHideDelayMs = 900d;
    private readonly Dictionary<ScrollViewer, ScrollbarAutoHideRegistration> _scrollbarAutoHideRegistrations = [];
    private readonly HashSet<ScrollViewer> _scrollbarAutoHidePendingViewers = [];
    private readonly HashSet<DependencyObject> _scrollbarAutoHideRoots = [];
    private bool _scrollbarAutoHideRefreshQueued;

    private void QueueScrollbarAutoHideRefresh(params DependencyObject[] additionalRoots)
    {
        foreach (var root in additionalRoots)
            _scrollbarAutoHideRoots.Add(root);

        if (_scrollbarAutoHideRefreshQueued) return;
        _scrollbarAutoHideRefreshQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _scrollbarAutoHideRefreshQueued = false;
            AttachScrollbarAutoHide(RootGrid);
            foreach (var root in _scrollbarAutoHideRoots.ToArray())
                AttachScrollbarAutoHide(root);
        });
    }

    private void RegisterScrollbarAutoHidePopup(Popup popup)
    {
        if (popup.Child is not DependencyObject child) return;

        _scrollbarAutoHideRoots.Add(child);
        popup.Opened += (_, _) => QueueScrollbarAutoHideRefresh(child);
    }

    private void AttachScrollbarAutoHide(DependencyObject root)
    {
        foreach (var viewer in EnumerateScrollViewers(root))
        {
            if (_scrollbarAutoHideRegistrations.ContainsKey(viewer))
                continue;

            if (IsDescendantOf(viewer, ReaderTocList))
            {
                StopScrollbarAutoHideRetry(viewer);
                continue;
            }

            viewer.ApplyTemplate();
            var hasEnabledScrollBar = IsScrollbarEnabled(viewer, Orientation.Vertical)
                || IsScrollbarEnabled(viewer, Orientation.Horizontal);
            if (!hasEnabledScrollBar)
            {
                StopScrollbarAutoHideRetry(viewer);
                continue;
            }

            var bars = EnumerateOwnedScrollBars(viewer)
                .Where(bar => IsScrollbarEnabled(viewer, bar.Orientation))
                .ToArray();
            if (bars.Length == 0)
            {
                QueueScrollbarAutoHideRetry(viewer);
                continue;
            }

            StopScrollbarAutoHideRetry(viewer);
            var registration = new ScrollbarAutoHideRegistration(viewer, bars, DispatcherQueue);
            _scrollbarAutoHideRegistrations.Add(viewer, registration);
            registration.Attach();
        }
    }

    private static IEnumerable<ScrollViewer> EnumerateScrollViewers(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
            yield return viewer;

        foreach (var descendant in FindDescendants<ScrollViewer>(root))
            yield return descendant;
    }

    private static IEnumerable<ScrollBar> EnumerateOwnedScrollBars(ScrollViewer viewer)
    {
        foreach (var bar in FindDescendants<ScrollBar>(viewer))
        {
            for (var current = VisualTreeHelper.GetParent(bar); current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (ReferenceEquals(current, viewer))
                {
                    yield return bar;
                    break;
                }

                if (current is ScrollViewer)
                    break;
            }
        }
    }

    private void QueueScrollbarAutoHideRetry(ScrollViewer viewer)
    {
        if (!_scrollbarAutoHidePendingViewers.Add(viewer)) return;
        viewer.LayoutUpdated += ScrollbarAutoHideViewer_LayoutUpdated;
    }

    private void StopScrollbarAutoHideRetry(ScrollViewer viewer)
    {
        if (!_scrollbarAutoHidePendingViewers.Remove(viewer)) return;
        viewer.LayoutUpdated -= ScrollbarAutoHideViewer_LayoutUpdated;
    }

    private void ScrollbarAutoHideViewer_LayoutUpdated(object? sender, object e)
    {
        if (sender is not ScrollViewer viewer) return;
        StopScrollbarAutoHideRetry(viewer);
        AttachScrollbarAutoHide(viewer);
    }

    private static bool IsScrollbarEnabled(ScrollViewer viewer, Orientation orientation)
    {
        var visibility = orientation == Orientation.Vertical
            ? viewer.VerticalScrollBarVisibility
            : viewer.HorizontalScrollBarVisibility;
        return visibility is ScrollBarVisibility.Auto or ScrollBarVisibility.Visible;
    }

    private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private void StopScrollbarAutoHide()
    {
        foreach (var registration in _scrollbarAutoHideRegistrations.Values)
            registration.Detach();
        _scrollbarAutoHideRegistrations.Clear();
        foreach (var viewer in _scrollbarAutoHidePendingViewers.ToArray())
            StopScrollbarAutoHideRetry(viewer);
        _scrollbarAutoHideRoots.Clear();
    }

    private sealed class ScrollbarAutoHideRegistration
    {
        private readonly ScrollViewer _viewer;
        private readonly IReadOnlyList<ScrollBar> _bars;
        private readonly DispatcherQueueTimer _hideTimer;
        private readonly Dictionary<ScrollBar, Storyboard> _storyboards = [];

        public ScrollbarAutoHideRegistration(
            ScrollViewer viewer,
            IReadOnlyList<ScrollBar> bars,
            DispatcherQueue dispatcherQueue)
        {
            _viewer = viewer;
            _bars = bars;
            _hideTimer = dispatcherQueue.CreateTimer();
            _hideTimer.Interval = TimeSpan.FromMilliseconds(ScrollbarAutoHideDelayMs);
            _hideTimer.IsRepeating = false;
        }

        public void Attach()
        {
            _viewer.ViewChanged += Viewer_ViewChanged;
            _viewer.PointerEntered += Viewer_PointerEntered;
            _viewer.PointerExited += Viewer_PointerExited;
            _hideTimer.Tick += HideTimer_Tick;
            SetVisible(visible: false, animate: false);
        }

        public void Detach()
        {
            _viewer.ViewChanged -= Viewer_ViewChanged;
            _viewer.PointerEntered -= Viewer_PointerEntered;
            _viewer.PointerExited -= Viewer_PointerExited;
            _hideTimer.Tick -= HideTimer_Tick;
            _hideTimer.Stop();
            foreach (var storyboard in _storyboards.Values) storyboard.Stop();
            _storyboards.Clear();
        }

        private void Viewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            SetVisible(visible: true, animate: true);
            if (e.IsIntermediate) return;
            RestartHideTimer();
        }

        private void Viewer_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _hideTimer.Stop();
            SetVisible(visible: true, animate: true);
        }

        private void Viewer_PointerExited(object sender, PointerRoutedEventArgs e) => RestartHideTimer();

        private void HideTimer_Tick(DispatcherQueueTimer sender, object args) =>
            SetVisible(visible: false, animate: true);

        private void RestartHideTimer()
        {
            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void SetVisible(bool visible, bool animate)
        {
            foreach (var bar in _bars)
            {
                var canScroll = bar.Orientation == Orientation.Vertical
                    ? _viewer.ScrollableHeight > 0
                    : _viewer.ScrollableWidth > 0;
                var show = visible && canScroll;
                bar.IsHitTestVisible = show;

                if (_storyboards.Remove(bar, out var previous)) previous.Stop();
                if (!animate)
                {
                    bar.Opacity = show ? 1d : 0d;
                    continue;
                }

                var animation = new DoubleAnimation
                {
                    To = show ? 1d : 0d,
                    Duration = TimeSpan.FromMilliseconds(180),
                };
                Storyboard.SetTarget(animation, bar);
                Storyboard.SetTargetProperty(animation, "Opacity");
                var storyboard = new Storyboard();
                storyboard.Children.Add(animation);
                _storyboards[bar] = storyboard;
                storyboard.Begin();
            }
        }
    }
}
