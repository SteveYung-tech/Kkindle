namespace Kkindle.Core;

// Geometry shared by the reader host and its pagination scripts. Keeping the
// page model independent from WinUI makes the boundary math easy to test and
// prevents each navigation path from inventing a different page step.
public static class ReaderPaginationDefaults
{
    public const double HorizontalPadding = 24;
    public const double ColumnGap = 48;
    // Keep all four page edges on the same inset so each pagination viewport
    // has balanced whitespace around its content.
    public const double TopPadding = HorizontalPadding;
    public const double BottomPadding = HorizontalPadding;
    public const double SnapTolerance = 4;

    public static double GetColumnWidth(double viewportWidth, int pagesPerView = 1)
    {
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)
            return 0;

        var safePagesPerView = Math.Clamp(pagesPerView, 1, 2);
        // The multicol container is border-box sized, so its left/right
        // padding is outside the usable column area. A spread must reserve
        // both outer insets plus the gaps between its columns; otherwise two
        // requested columns do not actually fit in one viewport and the
        // browser redistributes them onto uneven page boundaries.
        var reservedWidth = HorizontalPadding * 2
            + ColumnGap * (safePagesPerView - 1);
        return Math.Max(0, (viewportWidth - reservedWidth) / safePagesPerView);
    }
}

public static class ReaderPaginationPolicy
{
    public static double SnapScrollLeft(
        double scrollLeft,
        double clientWidth,
        double scrollWidth)
    {
        if (!double.IsFinite(clientWidth) || clientWidth <= 0)
            return 0;

        var max = GetMaxScrollLeft(clientWidth, scrollWidth);
        var safeScrollLeft = double.IsFinite(scrollLeft) ? scrollLeft : 0;
        if (safeScrollLeft >= max - ReaderPaginationDefaults.SnapTolerance)
            return max;

        var nearest = Math.Round(
            safeScrollLeft / clientWidth,
            MidpointRounding.AwayFromZero)
            * clientWidth;

        return Math.Clamp(nearest, 0, max);
    }

    public static bool CanTurn(
        double scrollLeft,
        int direction,
        double clientWidth,
        double scrollWidth)
    {
        if (direction == 0 || !double.IsFinite(clientWidth) || clientWidth <= 0)
            return false;

        var current = SnapScrollLeft(scrollLeft, clientWidth, scrollWidth);
        var max = GetMaxScrollLeft(clientWidth, scrollWidth);
        return direction < 0
            ? current > ReaderPaginationDefaults.SnapTolerance
            : current < max - ReaderPaginationDefaults.SnapTolerance;
    }

    public static double GetTurnTarget(
        double scrollLeft,
        int direction,
        double clientWidth,
        double scrollWidth)
    {
        var current = SnapScrollLeft(scrollLeft, clientWidth, scrollWidth);
        if (direction == 0 || !double.IsFinite(clientWidth) || clientWidth <= 0)
            return current;

        var max = GetMaxScrollLeft(clientWidth, scrollWidth);
        var delta = direction < 0 ? -clientWidth : clientWidth;
        return Math.Clamp(current + delta, 0, max);
    }

    public static double GetMaxScrollLeft(double clientWidth, double scrollWidth)
    {
        if (!double.IsFinite(clientWidth) || clientWidth <= 0
            || !double.IsFinite(scrollWidth))
            return 0;

        return Math.Max(0, scrollWidth - clientWidth);
    }
}
