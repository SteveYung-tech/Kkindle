using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

/// <summary>
/// Port of the WinUI reference's minimal TOC rail (极简目录): a narrow marker
/// rail that keeps the chapter map visible without taking the reading column
/// away from the body. Markers wave toward the pointer, a floating label shows
/// the hovered chapter title, the wheel scrolls with a short eased animation
/// and the up/down indicators reflect the scroll extent.
/// </summary>
public sealed record ReaderTocMarker(EpubReaderNavigationItem Item, bool IsCurrent)
{
    // The WinUI reference's current-marker grey was blue-tinted (91,98,104)
    // and the inactive one green-tinted (211,213,209); the reader palette is
    // strictly black/white/grey, so both are neutralised to the same
    // luminance.
    private static readonly IBrush CurrentBrush = new SolidColorBrush(Color.FromArgb(255, 98, 98, 98));
    private static readonly IBrush InactiveBrush = new SolidColorBrush(Color.FromArgb(255, 211, 211, 211));
    public static readonly IBrush HoverBrush = new SolidColorBrush(Color.FromArgb(255, 96, 96, 96));

    public string Title => Item.Title;
    public IBrush Fill => GetFill(IsCurrent);
    public static IBrush GetFill(bool isCurrent) => isCurrent ? CurrentBrush : InactiveBrush;
}

public partial class MainWindow
{
    private const double ReaderTocMinimalWidth = 52d;
    private const double ReaderCompactMarkerMinimumWidth = 8d;
    // A right-only wave starts at the centered 8 px resting marker. Keep its
    // longest stroke inside the 52 px rail (including the right divider).
    private const double ReaderCompactMarkerMaximumWidth = 28d;
    private const double ReaderCompactMarkerWaveRadius = 96d;
    private const double ReaderCompactScrollAnimationDurationMs = 160d;
    private bool _readerTocMinimal;
    private bool _readerTocExpanded = true;
    private IReadOnlyList<EpubReaderNavigationItem> _readerCompactNavigationItems = [];
    private string? _readerCompactSelectedTarget;
    private DispatcherTimer? _readerCompactScrollTimer;
    private bool _readerCompactScrollAnimating;
    private double _readerCompactScrollStart;
    private double _readerCompactScrollTarget;
    private DateTimeOffset _readerCompactScrollStartedAt;
    private bool _readerCompactPointerActive;
    private double _readerCompactPointerY;

    private void ReaderTocMinimalToggleButton_Click(object? sender, RoutedEventArgs e)
    {
        SetReaderTocMinimal(!_readerTocMinimal);
    }

    private void ReaderTocCompactExpandButton_Click(object? sender, RoutedEventArgs e)
    {
        SetReaderTocMinimal(false);
    }

    private bool _readerCompactPointerPressedHandlerRegistered;

    private void ReaderTocCompactPanel_Loaded(object? sender, RoutedEventArgs e)
    {
        if (_readerCompactPointerPressedHandlerRegistered) return;
        // The ScrollViewer can mark the pointer press as handled while starting
        // its own scroll gesture, so subscribe with handledEventsToo like the
        // WinUI reference's AddHandler call.
        ReaderTocCompactScrollViewer.AddHandler(
            InputElement.PointerPressedEvent,
            ReaderTocCompactScrollViewer_PointerPressed,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        _readerCompactPointerPressedHandlerRegistered = true;
        UpdateReaderCompactMarkerWave();
    }

    private void ReaderTocCompactScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        UpdateReaderCompactScrollIndicators();
        UpdateReaderCompactMarkerWave();
    }

    private void ReaderTocCompactScrollViewer_PointerMoved(object? sender, PointerEventArgs e)
    {
        _readerCompactPointerActive = true;
        _readerCompactPointerY = Math.Clamp(
            e.GetPosition(ReaderTocCompactScrollViewer).Y,
            0,
            ReaderTocCompactScrollViewer.Bounds.Height);
        UpdateReaderCompactMarkerWave();
    }

    private void ReaderTocCompactScrollViewer_PointerExited(object? sender, PointerEventArgs e)
    {
        _readerCompactPointerActive = false;
        UpdateReaderCompactMarkerWave();
    }

    private void ReaderTocCompactScrollViewer_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ReaderTocCompactScrollViewer).Properties.IsLeftButtonPressed) return;
        var pointerY = e.GetPosition(ReaderTocCompactScrollViewer).Y;
        ReaderTocMarker? closestMarker = null;
        var closestDistance = double.MaxValue;
        foreach (var button in FindDescendants<Button>(ReaderTocCompactList))
        {
            if (button.DataContext is not ReaderTocMarker marker || button.Bounds.Height <= 0) continue;
            try
            {
                var markerCenter = button
                    .TranslatePoint(new Point(0, button.Bounds.Height / 2), ReaderTocCompactScrollViewer)?
                    .Y ?? 0;
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

    private void ReaderTocCompactScrollViewer_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var delta = e.Delta.Y;
        if (Math.Abs(delta) < 0.01) return;
        var scrollViewer = ReaderTocCompactScrollViewer;
        var maximum = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var baseOffset = _readerCompactScrollAnimating
            ? _readerCompactScrollTarget
            : scrollViewer.Offset.Y;
        _readerCompactScrollStart = scrollViewer.Offset.Y;
        _readerCompactScrollTarget = Math.Clamp(
            baseOffset - delta * 120 * 0.45,
            0,
            maximum);
        _readerCompactScrollStartedAt = DateTimeOffset.UtcNow;
        _readerCompactScrollAnimating = true;
        EnsureReaderCompactScrollTimer();
        _readerCompactScrollTimer!.Start();
        e.Handled = true;
    }

    private void EnsureReaderCompactScrollTimer()
    {
        if (_readerCompactScrollTimer is not null) return;
        _readerCompactScrollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _readerCompactScrollTimer.Tick += ReaderCompactScrollTimer_Tick;
    }

    private void ReaderCompactScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_readerCompactScrollAnimating || !ReaderTocCompactPanel.IsVisible)
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
        ReaderTocCompactScrollViewer.Offset = new Vector(0, offset);
        UpdateReaderCompactScrollIndicators();
        UpdateReaderCompactMarkerWave();

        if (progress >= 1) StopReaderCompactScrollAnimation();
    }

    private void StopReaderCompactScrollAnimation()
    {
        _readerCompactScrollAnimating = false;
        _readerCompactScrollTimer?.Stop();
    }

    private void UpdateReaderCompactScrollIndicators()
    {
        if (ReaderTocCompactUpIndicator is null || ReaderTocCompactDownIndicator is null) return;

        const double edgeTolerance = 0.5d;
        var offset = ReaderTocCompactScrollViewer.Offset.Y;
        var maximum = Math.Max(0, ReaderTocCompactScrollViewer.Extent.Height - ReaderTocCompactScrollViewer.Viewport.Height);
        ReaderTocCompactUpIndicator.Text = offset <= edgeTolerance ? "\u25B3" : "\u25B2";
        ReaderTocCompactDownIndicator.Text = offset >= maximum - edgeTolerance ? "\u25BD" : "\u25BC";
    }

    private void UpdateReaderCompactMarkerWave()
    {
        if (ReaderTocCompactScrollViewer is null
            || ReaderTocCompactList is null
            || ReaderTocCompactScrollViewer.Bounds.Height <= 0)
        {
            return;
        }

        Border? hoveredMarker = null;
        Button? hoveredButton = null;
        string? hoveredTitle = null;
        var hoveredDistance = double.MaxValue;
        foreach (var button in FindDescendants<Button>(ReaderTocCompactList))
        {
            if (button.Content is not Border marker || button.Bounds.Height <= 0) continue;
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
                    .TranslatePoint(new Point(0, button.Bounds.Height / 2), ReaderTocCompactScrollViewer)?
                    .Y ?? 0;
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
            ReaderTocCompactHoverLabel.IsVisible = true;
            var markerCenter = target
                .TranslatePoint(new Point(0, target.Bounds.Height / 2), ReaderRoot)?
                .Y ?? 0;
            var labelHeight = ReaderTocCompactHoverLabel.Bounds.Height;
            const double minimumTop = 38d;
            var maximumTop = Math.Max(minimumTop, ReaderRoot.Bounds.Height - labelHeight);
            var top = Math.Clamp(markerCenter - labelHeight / 2, minimumTop, maximumTop);
            ReaderTocCompactHoverLabel.Margin = new Thickness(ReaderTocMinimalWidth + 6, top, 0, 0);
        }
        catch (InvalidOperationException)
        {
            HideReaderCompactHoverLabel();
        }
    }

    private void HideReaderCompactHoverLabel()
    {
        if (ReaderTocCompactHoverLabel is not null)
            ReaderTocCompactHoverLabel.IsVisible = false;
    }

    private static void SetReaderCompactMarkerWidth(Border marker, double width)
    {
        marker.Width = width;
        if (marker.RenderTransform is not TranslateTransform translation)
        {
            translation = new TranslateTransform();
            marker.RenderTransform = translation;
        }

        // The resting marker stays centered. Render translation does not take
        // part in layout, so shifting by half of the added width precisely
        // cancels the centered marker's leftward growth. Its left edge remains
        // fixed and only the right half-wave extends into the reading area.
        translation.X = (width - ReaderCompactMarkerMinimumWidth) / 2;
    }

    private static IEnumerable<T> FindDescendants<T>(Visual root) where T : Visual
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is T match) yield return match;

            foreach (var descendant in FindDescendants<T>(child))
                yield return descendant;
        }
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
        RefreshReaderCompactMarkers();
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

    private void QueueReaderCompactScrollIndicatorUpdate()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ReaderTocCompactPanel.IsVisible)
            {
                UpdateReaderCompactScrollIndicators();
                UpdateReaderCompactMarkerWave();
            }
        }, DispatcherPriority.Background);
    }

    private void NavigateToReaderTocItem(EpubReaderNavigationItem item)
    {
        // A TOC click is an explicit user target: it must start at the target
        // chapter's first line (or its own anchor), never inherit a leftover
        // "move to chapter end" intent from a superseded previous-chapter turn.
        SetReaderCompactSelectedItem(item);
        // Selecting the item in the full TOC list triggers the selection
        // handler for real user clicks. This is a programmatic selection,
        // therefore use the guarded sync helper and start exactly one jump.
        SetReaderTocSelection(item);
        _ = ObserveReaderTaskAsync(
            NavigateToReaderItemAsync(
                item,
                _readerSessionCancellation?.Token ?? CancellationToken.None,
                ReaderNavigationIntent.Toc));
    }

    private void ApplyReaderPanelLayout()
    {
        if (!ReaderRoot.IsVisible) return;

        // Mirror the WinUI reference's TOC column sizing: the first column is
        // 286 when expanded, 52 when the minimal rail is shown, and 0 when the
        // TOC is hidden entirely.
        ReaderRoot.ColumnDefinitions[0].Width = new GridLength(
            _readerTocExpanded ? 286d : _readerTocMinimal ? ReaderTocMinimalWidth : 0d);
        ReaderTocPanel.IsVisible = _readerTocExpanded;
        ReaderTocCompactPanel.IsVisible = _readerTocMinimal;
        ReaderTocToggleButton.Opacity = _readerTocExpanded ? 0.58 : 1;
    }

    private void UpdateReaderZenTocToggle()
    {
        if (ReaderZenTocButton is null) return;
        ReaderZenTocButton.Content = _readerTocMinimal ? "隐藏极简目录" : "极简目录";
    }
}
