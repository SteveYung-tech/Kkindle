using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public sealed record ReaderTocMarker(EpubReaderNavigationItem Item, bool IsCurrent)
{
    private static readonly SolidColorBrush CurrentBrush = new(ColorHelper.FromArgb(255, 91, 98, 104));
    private static readonly SolidColorBrush InactiveBrush = new(ColorHelper.FromArgb(255, 211, 213, 209));
    public static readonly SolidColorBrush HoverBrush = new(ColorHelper.FromArgb(255, 96, 96, 96));

    public string Title => Item.Title;
    public SolidColorBrush Fill => GetFill(IsCurrent);
    public static SolidColorBrush GetFill(bool isCurrent) => isCurrent ? CurrentBrush : InactiveBrush;
}

public sealed partial class MainWindow
{
    private const double ReaderTocMinimalWidth = 52d;
    private const double ReaderCompactMarkerMinimumWidth = 8d;
    // A right-only wave starts at the centered 8 px resting marker. Keep its
    // longest stroke inside the 52 px rail (including the right divider).
    private const double ReaderCompactMarkerMaximumWidth = 28d;
    private const double ReaderCompactMarkerWaveRadius = 96d;
    private const double ReaderCompactScrollAnimationDurationMs = 160d;
    private IReadOnlyList<EpubReaderNavigationItem> _readerCompactNavigationItems = [];
    private string? _readerCompactSelectedTarget;
    private DispatcherQueueTimer? _readerCompactScrollTimer;
    private bool _readerCompactScrollAnimating;
    private double _readerCompactScrollStart;
    private double _readerCompactScrollTarget;
    private DateTimeOffset _readerCompactScrollStartedAt;
    private bool _readerCompactPointerActive;
    private double _readerCompactPointerY;
    private bool _readerCompactPointerPressedHandlerRegistered;

    // Main TOC list scrollbar: the XAML resources keep its colors soft, and
    // this fades it away while idle so the side panel stays out of the reading
    // flow. It reappears on scroll or hover.
    private const double ReaderTocScrollbarIdleDelayMs = 900d;
    private ScrollViewer? _readerTocListScrollViewer;
    private ScrollBar? _readerTocListScrollBar;
    private DispatcherQueueTimer? _readerTocScrollbarHideTimer;
    private Storyboard? _readerTocScrollbarFadeStoryboard;

    private void ReaderTocList_Loaded(object sender, RoutedEventArgs e) =>
        AttachReaderTocScrollbarAutoHide();

    private void AttachReaderTocScrollbarAutoHide()
    {
        if (_readerTocListScrollViewer is not null && _readerTocListScrollBar is not null) return;

        _readerTocListScrollViewer ??= FindDescendants<ScrollViewer>(ReaderTocList).FirstOrDefault();
        _readerTocListScrollBar ??= FindDescendants<ScrollBar>(ReaderTocList)
            .FirstOrDefault(bar => bar.Orientation == Orientation.Vertical);

        if (_readerTocListScrollViewer is null || _readerTocListScrollBar is null)
        {
            // Template parts may not be realized yet on the very first layout;
            // retry once after the next layout pass.
            ReaderTocList.LayoutUpdated += ReaderTocList_RetryAttachLayoutUpdated;
            return;
        }

        _readerTocListScrollViewer.ViewChanged += ReaderTocListScrollViewer_ViewChanged;
        _readerTocListScrollViewer.PointerEntered += ReaderTocListScrollViewer_PointerEntered;
        _readerTocListScrollViewer.PointerExited += ReaderTocListScrollViewer_PointerExited;

        _readerTocScrollbarHideTimer = DispatcherQueue.CreateTimer();
        _readerTocScrollbarHideTimer.Interval = TimeSpan.FromMilliseconds(ReaderTocScrollbarIdleDelayMs);
        _readerTocScrollbarHideTimer.IsRepeating = false;
        _readerTocScrollbarHideTimer.Tick += ReaderTocScrollbarHideTimer_Tick;

        // Briefly reveal the scrollbar when the panel opens (only when the list
        // actually overflows), then the idle timer folds it away. Scrolling or
        // hovering brings it back.
        SetReaderTocListScrollBarVisible(visible: true, animate: false);
        _readerTocScrollbarHideTimer.Stop();
        _readerTocScrollbarHideTimer.Start();
    }

    private void ReaderTocList_RetryAttachLayoutUpdated(object? sender, object e)
    {
        ReaderTocList.LayoutUpdated -= ReaderTocList_RetryAttachLayoutUpdated;
        AttachReaderTocScrollbarAutoHide();
    }

    private void ReaderTocListScrollViewer_ViewChanged(
        object? sender,
        ScrollViewerViewChangedEventArgs e)
    {
        SetReaderTocListScrollBarVisible(visible: true, animate: true);
        if (e.IsIntermediate) return;

        _readerTocScrollbarHideTimer?.Stop();
        _readerTocScrollbarHideTimer?.Start();
    }

    private void ReaderTocListScrollViewer_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetReaderTocListScrollBarVisible(visible: true, animate: true);
        _readerTocScrollbarHideTimer?.Stop();
    }

    private void ReaderTocListScrollViewer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _readerTocScrollbarHideTimer?.Stop();
        _readerTocScrollbarHideTimer?.Start();
    }

    private void ReaderTocScrollbarHideTimer_Tick(DispatcherQueueTimer sender, object args) =>
        SetReaderTocListScrollBarVisible(visible: false, animate: true);

    private void SetReaderTocListScrollBarVisible(bool visible, bool animate)
    {
        if (_readerTocListScrollBar is null) return;

        _readerTocListScrollBar.IsHitTestVisible = visible;
        if (!animate)
        {
            _readerTocScrollbarFadeStoryboard?.Stop();
            _readerTocListScrollBar.Opacity = visible ? 1d : 0d;
            return;
        }

        _readerTocScrollbarFadeStoryboard?.Stop();
        var animation = new DoubleAnimation
        {
            To = visible ? 1d : 0d,
            Duration = TimeSpan.FromMilliseconds(180),
        };
        Storyboard.SetTarget(animation, _readerTocListScrollBar);
        Storyboard.SetTargetProperty(animation, "Opacity");
        _readerTocScrollbarFadeStoryboard = new Storyboard();
        _readerTocScrollbarFadeStoryboard.Children.Add(animation);
        _readerTocScrollbarFadeStoryboard.Begin();
    }

    private void ReaderTocMinimalToggleButton_Click(object sender, RoutedEventArgs e)
    {
        SetReaderTocMinimal(!_readerTocMinimal);
    }

    private void ReaderTocCompactExpandButton_Click(object sender, RoutedEventArgs e)
    {
        SetReaderTocMinimal(false);
    }

    private void ReaderTocCompactScrollViewer_ViewChanged(
        object sender,
        ScrollViewerViewChangedEventArgs e)
    {
        UpdateReaderCompactScrollIndicators();
        UpdateReaderCompactMarkerWave();
    }

    private void ReaderTocCompactScrollViewer_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_readerCompactPointerPressedHandlerRegistered)
        {
            ReaderTocCompactScrollViewer.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler(ReaderTocCompactScrollViewer_PointerPressed),
                handledEventsToo: true);
            _readerCompactPointerPressedHandlerRegistered = true;
        }

        UpdateReaderCompactMarkerWave();
    }

    private void ReaderTocCompactScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateReaderCompactMarkerWave();

    private void ReaderTocCompactScrollViewer_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        UpdateReaderCompactPointerPosition(e);

    private void ReaderTocCompactScrollViewer_PointerMoved(object sender, PointerRoutedEventArgs e) =>
        UpdateReaderCompactPointerPosition(e);

    private void ReaderTocCompactScrollViewer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _readerCompactPointerActive = false;
        UpdateReaderCompactMarkerWave();
    }

    private void UpdateReaderCompactPointerPosition(PointerRoutedEventArgs e)
    {
        _readerCompactPointerActive = true;
        _readerCompactPointerY = Math.Clamp(
            e.GetCurrentPoint(ReaderTocCompactScrollViewer).Position.Y,
            0,
            ReaderTocCompactScrollViewer.ActualHeight);
        UpdateReaderCompactMarkerWave();
    }

    private void ReaderTocCompactScrollViewer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ReaderTocCompactScrollViewer);
        if (!point.Properties.IsLeftButtonPressed) return;

        var pointerY = point.Position.Y;
        ReaderTocMarker? closestMarker = null;
        var closestDistance = double.MaxValue;
        foreach (var button in FindDescendants<Button>(ReaderTocCompactList))
        {
            if (button.DataContext is not ReaderTocMarker marker || button.ActualHeight <= 0) continue;
            try
            {
                var markerCenter = button
                    .TransformToVisual(ReaderTocCompactScrollViewer)
                    .TransformPoint(new Windows.Foundation.Point(0, button.ActualHeight / 2))
                    .Y;
                var distance = Math.Abs(markerCenter - pointerY);
                if (distance >= closestDistance) continue;
                closestDistance = distance;
                closestMarker = marker;
            }
            catch (InvalidOperationException)
            {
                // The item may be between visual trees during a layout pass.
            }
        }

        if (closestMarker is null) return;
        NavigateToReaderTocItem(closestMarker.Item);
        e.Handled = true;
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
        UpdateReaderCompactScrollIndicators();
        UpdateReaderCompactMarkerWave();

        if (progress >= 1) StopReaderCompactScrollAnimation();
    }

    private void UpdateReaderCompactScrollIndicators()
    {
        if (ReaderTocCompactUpIndicator is null || ReaderTocCompactDownIndicator is null) return;

        const double edgeTolerance = 0.5d;
        var offset = ReaderTocCompactScrollViewer.VerticalOffset;
        var maximum = ReaderTocCompactScrollViewer.ScrollableHeight;
        ReaderTocCompactUpIndicator.Text = offset <= edgeTolerance ? "\u25B3" : "\u25B2";
        ReaderTocCompactDownIndicator.Text = offset >= maximum - edgeTolerance ? "\u25BD" : "\u25BC";
    }

    private void QueueReaderCompactScrollIndicatorUpdate()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateReaderCompactScrollIndicators();
            UpdateReaderCompactMarkerWave();
        });
    }

    private void UpdateReaderCompactMarkerWave()
    {
        if (ReaderTocCompactScrollViewer is null
            || ReaderTocCompactList is null
            || ReaderTocCompactScrollViewer.ActualHeight <= 0)
        {
            return;
        }

        Border? hoveredMarker = null;
        Button? hoveredButton = null;
        string? hoveredTitle = null;
        var hoveredDistance = double.MaxValue;
        foreach (var button in FindDescendants<Button>(ReaderTocCompactList))
        {
            if (button.Content is not Border marker || button.ActualHeight <= 0) continue;
            var markerData = button.DataContext as ReaderTocMarker;
            if (markerData is not null)
            {
                var isCurrent = _readerCompactSelectedTarget is not null
                    && markerData.Item.Target.Equals(
                        _readerCompactSelectedTarget,
                        StringComparison.OrdinalIgnoreCase);
                marker.Background = ReaderTocMarker.GetFill(isCurrent);
            }

            try
            {
                if (!_readerCompactPointerActive)
                {
                    SetReaderCompactMarkerWidth(marker, ReaderCompactMarkerMinimumWidth);
                    continue;
                }

                var markerCenter = button
                    .TransformToVisual(ReaderTocCompactScrollViewer)
                    .TransformPoint(new Windows.Foundation.Point(0, button.ActualHeight / 2))
                    .Y;
                var normalizedDistance = Math.Clamp(
                    Math.Abs(markerCenter - _readerCompactPointerY) / ReaderCompactMarkerWaveRadius,
                    0,
                    1);
                var distance = Math.Abs(markerCenter - _readerCompactPointerY);
                var wave = Math.Sin((1 - normalizedDistance) * Math.PI / 2);
                SetReaderCompactMarkerWidth(
                    marker,
                    ReaderCompactMarkerMinimumWidth
                        + (ReaderCompactMarkerMaximumWidth - ReaderCompactMarkerMinimumWidth) * wave);
                if (distance < hoveredDistance)
                {
                    hoveredDistance = distance;
                    hoveredMarker = marker;
                    hoveredButton = button;
                    hoveredTitle = markerData?.Title;
                }
            }
            catch (InvalidOperationException)
            {
                // The item may be between visual trees during a layout pass.
            }
        }

        if (hoveredMarker is not null)
            hoveredMarker.Background = ReaderTocMarker.HoverBrush;
        UpdateReaderCompactHoverLabel(hoveredButton, hoveredTitle);
    }

    private void UpdateReaderCompactHoverLabel(Button? target, string? title)
    {
        if (target is null || string.IsNullOrWhiteSpace(title))
        {
            HideReaderCompactHoverLabel();
            return;
        }

        try
        {
            ReaderTocCompactHoverLabelText.Text = title;
            ReaderTocCompactHoverLabel.Visibility = Visibility.Visible;
            ReaderTocCompactHoverLabel.Measure(
                new Windows.Foundation.Size(340, double.PositiveInfinity));
            var markerCenter = target
                .TransformToVisual(ReaderPane)
                .TransformPoint(new Windows.Foundation.Point(0, target.ActualHeight / 2));
            var labelHeight = ReaderTocCompactHoverLabel.DesiredSize.Height;
            const double minimumTop = 38d;
            var maximumTop = Math.Max(minimumTop, ReaderPane.ActualHeight - labelHeight);
            var top = Math.Clamp(markerCenter.Y - labelHeight / 2, minimumTop, maximumTop);
            var translation = ReaderTocCompactHoverLabel.RenderTransform as TranslateTransform;
            if (translation is null)
            {
                translation = new TranslateTransform();
                ReaderTocCompactHoverLabel.RenderTransform = translation;
            }
            translation.X = ReaderTocMinimalWidth + 6;
            translation.Y = top;
        }
        catch (InvalidOperationException)
        {
            HideReaderCompactHoverLabel();
        }
    }

    private void HideReaderCompactHoverLabel()
    {
        if (ReaderTocCompactHoverLabel is not null)
            ReaderTocCompactHoverLabel.Visibility = Visibility.Collapsed;
    }

    private static void SetReaderCompactMarkerWidth(Border marker, double width)
    {
        marker.Width = width;
        var translation = marker.RenderTransform as TranslateTransform;
        if (translation is null)
        {
            translation = new TranslateTransform();
            marker.RenderTransform = translation;
        }

        // The resting marker stays centered. As the hover wave grows, shift it
        // by half of the added width so its left edge remains fixed and only
        // the right-hand side extends into the reading area.
        translation.X = (width - ReaderCompactMarkerMinimumWidth) / 2;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;

            foreach (var descendant in FindDescendants<T>(child))
                yield return descendant;
        }
    }

    private void StopReaderCompactScrollAnimation()
    {
        _readerCompactScrollAnimating = false;
        _readerCompactScrollTimer?.Stop();
    }

    private void SetReaderTocMinimal(bool minimal)
    {
        _readerTocMinimal = minimal;
        _readerTocExpanded = !minimal;
        _readerCompactPointerActive = false;
        HideReaderCompactHoverLabel();
        ApplyReaderPanelLayout();
        UpdateReaderZenTocToggle();
        QueueReaderCompactScrollIndicatorUpdate();
    }

    private void SetReaderCompactNavigationItems(IReadOnlyList<EpubReaderNavigationItem> items)
    {
        StopReaderCompactScrollAnimation();
        _readerCompactNavigationItems = items;
        RefreshReaderCompactMarkers();
        QueueReaderCompactScrollIndicatorUpdate();
    }

    private void ClearReaderCompactNavigationItems()
    {
        StopReaderCompactScrollAnimation();
        _readerCompactNavigationItems = [];
        _readerCompactSelectedTarget = null;
        HideReaderCompactHoverLabel();
        ReaderTocCompactList.ItemsSource = null;
        QueueReaderCompactScrollIndicatorUpdate();
    }

    private void SetReaderCompactSelectedItem(EpubReaderNavigationItem? item)
    {
        _readerCompactSelectedTarget = item?.Target;
        QueueReaderCompactScrollIndicatorUpdate();
    }

    private void RefreshReaderCompactMarkers()
    {
        HideReaderCompactHoverLabel();
        ReaderTocCompactList.ItemsSource = _readerCompactNavigationItems
            .Select(item => new ReaderTocMarker(
                item,
                _readerCompactSelectedTarget is not null
                    && item.Target.Equals(
                        _readerCompactSelectedTarget,
                        StringComparison.OrdinalIgnoreCase)))
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
