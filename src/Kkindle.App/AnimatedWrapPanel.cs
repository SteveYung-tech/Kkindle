using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Kkindle;

/// <summary>
/// Fixed-slot wrap panel for the library shelf.
/// </summary>
public sealed class AnimatedWrapPanel : WrapPanel
{
    private const double LayoutThreshold = 0.5;
    private double _viewportWidth;

    public double ViewportWidth
    {
        get => _viewportWidth;
        set
        {
            var width = Math.Max(0, value);
            if (Math.Abs(_viewportWidth - width) <= LayoutThreshold) return;
            _viewportWidth = width;
            InvalidateMeasure();
            InvalidateArrange();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Orientation != Avalonia.Layout.Orientation.Horizontal)
            return base.MeasureOverride(availableSize);

        var slotWidth = ResolveSlotWidth();
        var slotHeight = ResolveSlotHeight();
        if (slotWidth <= 0 || slotHeight <= 0)
            return base.MeasureOverride(availableSize);

        foreach (var child in Children)
        {
            child.RenderTransform = new TranslateTransform();
            child.Measure(new Size(slotWidth, slotHeight));
        }

        var shelfWidth = ResolveShelfWidth(availableSize.Width, slotWidth);
        var rows = CalculateRowCount(Children.Count, shelfWidth, slotWidth);
        return new Size(shelfWidth, rows * slotHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Orientation != Avalonia.Layout.Orientation.Horizontal)
            return base.ArrangeOverride(finalSize);

        var slotWidth = ResolveSlotWidth();
        var slotHeight = ResolveSlotHeight();
        if (slotWidth <= 0 || slotHeight <= 0)
            return base.ArrangeOverride(finalSize);

        var shelfWidth = ResolveShelfWidth(finalSize.Width, slotWidth);
        var columns = CalculateColumnCount(shelfWidth, slotWidth);

        for (var index = 0; index < Children.Count; index++)
        {
            var child = Children[index];
            child.RenderTransform = new TranslateTransform();
            var column = index % columns;
            var row = index / columns;
            child.Arrange(new Rect(column * slotWidth, row * slotHeight, slotWidth, slotHeight));
        }

        var rows = CalculateRowCount(Children.Count, shelfWidth, slotWidth);
        return new Size(shelfWidth, rows * slotHeight);
    }

    private double ResolveShelfWidth(double layoutWidth, double slotWidth)
    {
        if (_viewportWidth > 0) return _viewportWidth;
        if (!double.IsInfinity(layoutWidth) && !double.IsNaN(layoutWidth) && layoutWidth > 0) return layoutWidth;
        return Math.Max(slotWidth, Children.Count * slotWidth);
    }

    private double ResolveSlotWidth()
        => Orientation == Avalonia.Layout.Orientation.Horizontal ? ItemWidth : ItemHeight;

    private double ResolveSlotHeight()
        => Orientation == Avalonia.Layout.Orientation.Horizontal ? ItemHeight : ItemWidth;

    private static int CalculateColumnCount(double shelfWidth, double slotWidth)
        => Math.Max(1, (int)Math.Floor((shelfWidth + LayoutThreshold) / slotWidth));

    private static int CalculateRowCount(int itemCount, double shelfWidth, double slotWidth)
    {
        if (itemCount <= 0) return 0;
        return (int)Math.Ceiling(itemCount / (double)CalculateColumnCount(shelfWidth, slotWidth));
    }
}
