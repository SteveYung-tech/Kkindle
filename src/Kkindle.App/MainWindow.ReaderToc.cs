using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public sealed record ReaderTocMarker(EpubReaderNavigationItem Item, bool IsCurrent)
{
    private static readonly SolidColorBrush CurrentBrush = new(ColorHelper.FromArgb(255, 91, 98, 104));
    private static readonly SolidColorBrush InactiveBrush = new(ColorHelper.FromArgb(255, 211, 213, 209));
    public static readonly SolidColorBrush HoverBrush = new(Colors.Black);

    public string Title => Item.Title;
    public SolidColorBrush Fill => IsCurrent ? CurrentBrush : InactiveBrush;
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
    private ToolTip? _readerCompactHoverToolTip;
    private bool _readerCompactPointerPressedHandlerRegistered;

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
        _readerCompactPointerActive = false;
        SetReaderCompactHoverToolTip(null);
        UpdateReaderCompactMarkerWave();
        NavigateToReaderTocItem(closestMarker.Item);
        e.Handled = true;
    }

    private void ReaderCompactTocItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element
            && element.DataContext is ReaderTocMarker marker)
        {
            SetReaderCompactHoverToolTip(null);
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
        ToolTip? hoveredToolTip = null;
        var hoveredDistance = double.MaxValue;
        foreach (var button in FindDescendants<Button>(ReaderTocCompactList))
        {
            if (button.Content is not Border marker || button.ActualHeight <= 0) continue;
            if (button.DataContext is ReaderTocMarker markerData)
                marker.Background = markerData.Fill;

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
                    hoveredToolTip = ToolTipService.GetToolTip(button) as ToolTip;
                }
            }
            catch (InvalidOperationException)
            {
                // The item may be between visual trees during a layout pass.
            }
        }

        if (hoveredMarker is not null)
            hoveredMarker.Background = ReaderTocMarker.HoverBrush;
        SetReaderCompactHoverToolTip(hoveredToolTip);
    }

    private void SetReaderCompactHoverToolTip(ToolTip? toolTip)
    {
        if (!ReferenceEquals(_readerCompactHoverToolTip, toolTip))
        {
            if (_readerCompactHoverToolTip is not null)
                _readerCompactHoverToolTip.IsOpen = false;
            _readerCompactHoverToolTip = toolTip;
        }

        if (_readerCompactHoverToolTip is not null)
        {
            _readerCompactHoverToolTip.HorizontalOffset = 6;
            _readerCompactHoverToolTip.IsOpen = true;
        }
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
        SetReaderCompactHoverToolTip(null);
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
        SetReaderCompactHoverToolTip(null);
        ReaderTocCompactList.ItemsSource = null;
        QueueReaderCompactScrollIndicatorUpdate();
    }

    private void SetReaderCompactSelectedItem(EpubReaderNavigationItem? item)
    {
        _readerCompactSelectedTarget = item?.Target;
        RefreshReaderCompactMarkers();
        QueueReaderCompactScrollIndicatorUpdate();
    }

    private void RefreshReaderCompactMarkers()
    {
        SetReaderCompactHoverToolTip(null);
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
