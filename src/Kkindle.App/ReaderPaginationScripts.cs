using System.Globalization;
using Kkindle.Core;

namespace Kkindle;

internal static class ReaderPaginationScripts
{
    public const string ViewportWidthVariable = "--kkindle-reader-page-viewport-width";

    public static string CreateFlowCss(
        bool pagination,
        bool vertical,
        bool twoPage = false,
        double horizontalPadding = ReaderPaginationDefaults.HorizontalPadding,
        double maxContentWidth = ReaderLayoutDefaults.DefaultMaxWidth)
    {
        if (!pagination)
        {
            return vertical
                ? "html { height: 100%; overflow: hidden !important; } body { height: 100%; overflow: visible !important; box-sizing: border-box; }"
                : "html, body { min-height: 100%; overflow-x: hidden !important; } body { column-width: auto !important; column-count: auto !important; column-gap: normal !important; writing-mode: horizontal-tb !important; }";
        }

        var topPadding = Format(ReaderPaginationDefaults.TopPadding);
        var safeHorizontalPadding = double.IsFinite(horizontalPadding)
            ? Math.Clamp(
                horizontalPadding,
                ReaderLayoutDefaults.MinBodyPadding,
                ReaderLayoutDefaults.MaxBodyPadding)
            : ReaderPaginationDefaults.HorizontalPadding;
        var safeMaxContentWidth = double.IsFinite(maxContentWidth)
            ? Math.Clamp(
                maxContentWidth,
                ReaderLayoutDefaults.MinMaxWidth,
                ReaderLayoutDefaults.MaxMaxWidth)
            : ReaderLayoutDefaults.DefaultMaxWidth;
        var horizontalPaddingCss = Format(safeHorizontalPadding);
        var maxContentWidthCss = Format(safeMaxContentWidth);
        var bottomPadding = Format(ReaderPaginationDefaults.BottomPadding);
        // The distance from one column start to the next must remain exactly
        // one viewport (or half a viewport in a two-page spread). Treat the
        // inter-column gap as the adjoining right + left page margins. Grow
        // that gap when the viewport is wider than the requested text width;
        // this centers a capped text column without changing the page step.
        var minimumColumnGap = Format(safeHorizontalPadding * 2);
        var columnGap = twoPage
            ? $"max({minimumColumnGap}px, calc((var({ViewportWidthVariable}, 100vw) - {Format(safeMaxContentWidth * 2)}px) / 2))"
            : $"max({minimumColumnGap}px, calc(var({ViewportWidthVariable}, 100vw) - {maxContentWidthCss}px))";
        var columnWidth = twoPage
            ? $"calc((var({ViewportWidthVariable}, 100vw) - var(--kkindle-page-column-gap) - var(--kkindle-page-column-gap)) / 2)"
            : $"calc(var({ViewportWidthVariable}, 100vw) - var(--kkindle-page-column-gap))";
        if (vertical)
        {
            var verticalColumnHeight = $"calc(100vh - {Format(ReaderPaginationDefaults.TopPadding + ReaderPaginationDefaults.BottomPadding)}px)";
            return $"html {{ width: 100%; height: 100%; overflow: hidden !important; }}"
                + $" body {{ width: 100% !important; height: 100% !important; margin: 0 !important; overflow: visible !important;"
                + $" padding: {topPadding}px {horizontalPaddingCss}px {bottomPadding}px !important; box-sizing: border-box !important;"
                + $" writing-mode: vertical-rl !important; text-orientation: mixed !important; column-width: {verticalColumnHeight} !important;"
                + $" column-gap: {minimumColumnGap}px !important; column-fill: auto !important; column-count: auto !important; max-width: none !important; }}";
        }
        return $"html {{ height: 100%; overflow: hidden !important; writing-mode: horizontal-tb !important; }}"
            + $" body {{ --kkindle-page-column-gap: {columnGap}; width: 100% !important; min-width: 0 !important; height: 100% !important; margin: 0 !important; overflow: visible !important; padding: {topPadding}px calc(var(--kkindle-page-column-gap) / 2) {bottomPadding}px !important; box-sizing: border-box !important;"
            + $" writing-mode: horizontal-tb !important; column-width: {columnWidth} !important;"
            + $" column-gap: var(--kkindle-page-column-gap) !important; column-fill: auto !important; column-count: auto !important; max-width: none !important; }}";
    }

    // visualViewport is the source of truth for page geometry: unlike the
    // multicolumn document bounds it always describes the visible WebView and
    // retains fractional CSS pixels under DPI scaling. Page boundaries start
    // at scroll origin 0; body padding stays inside each viewport.
    public static string Snap => CreateSnapScript();

    private static string CreateSnapScript()
    {
        var tolerance = Format(ReaderPaginationDefaults.SnapTolerance);
        return $$"""
            (() => {
              const el = document.scrollingElement || document.documentElement;
              if (!el) return;
              const step = window.visualViewport?.width || window.innerWidth
                || document.documentElement.clientWidth || el.clientWidth || 0;
              if (step <= 0) return;
              const rawMax = Math.max(0, el.scrollWidth - el.clientWidth);
              const trailingInset = parseFloat(getComputedStyle(document.body).paddingRight) || 0;
              const max = Math.max(0, Math.min(rawMax, Math.round(Math.max(0, rawMax - trailingInset) / step) * step));
              const nearest = Math.round(el.scrollLeft / step) * step;
              const target = el.scrollLeft >= max - {{tolerance}}
                ? max
                : Math.max(0, Math.min(max, nearest));
              window.scrollTo({ left: target, top: 0, behavior: 'instant' });
            })();
            """;
    }

    public static string CreateTurnScript(int direction, bool smooth = true)
    {
        var safeDirection = direction < 0 ? -1 : 1;
        var tolerance = Format(ReaderPaginationDefaults.SnapTolerance);
        var behavior = smooth ? "smooth" : "instant";
        return $$"""
            (() => {
              const el = document.scrollingElement || document.documentElement;
              if (!el) return false;
              const step = window.visualViewport?.width || window.innerWidth
                || document.documentElement.clientWidth || el.clientWidth || 0;
              if (step <= 0) return false;
              const rawMax = Math.max(0, el.scrollWidth - el.clientWidth);
              const trailingInset = parseFloat(getComputedStyle(document.body).paddingRight) || 0;
              const max = Math.max(0, Math.min(rawMax, Math.round(Math.max(0, rawMax - trailingInset) / step) * step));
              const nearest = Math.round(el.scrollLeft / step) * step;
              const current = el.scrollLeft >= max - 4
                ? max
                : Math.max(0, Math.min(max, nearest));
              if ({{safeDirection}} < 0 && current <= {{tolerance}}) return false;
              if ({{safeDirection}} > 0 && current >= max - {{tolerance}}) return false;
              const target = Math.max(
                0,
                Math.min(max, current + ({{safeDirection}} < 0 ? -step : step)));
              window.scrollTo({ left: target, top: 0, behavior: '{{behavior}}' });
              return true;
            })();
            """;
    }

    public static string CreateCanTurnScript(int direction)
    {
        var safeDirection = direction < 0 ? -1 : 1;
        var tolerance = Format(ReaderPaginationDefaults.SnapTolerance);
        return $$"""
            (() => {
              const el = document.scrollingElement || document.documentElement;
              if (!el) return false;
              const step = window.visualViewport?.width || window.innerWidth
                || document.documentElement.clientWidth || el.clientWidth || 0;
              if (step <= 0) return false;
              const rawMax = Math.max(0, el.scrollWidth - el.clientWidth);
              const trailingInset = parseFloat(getComputedStyle(document.body).paddingRight) || 0;
              const max = Math.max(0, Math.min(rawMax, Math.round(Math.max(0, rawMax - trailingInset) / step) * step));
              const nearest = Math.round(el.scrollLeft / step) * step;
              const current = el.scrollLeft >= max - {{tolerance}}
                ? max
                : Math.max(0, Math.min(max, nearest));
              return {{safeDirection}} < 0
                ? current > {{tolerance}}
                : current < max - {{tolerance}};
            })();
            """;
    }

    private static string Format(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}
