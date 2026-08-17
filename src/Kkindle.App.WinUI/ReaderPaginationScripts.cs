using System.Globalization;
using Kkindle.Core;

namespace Kkindle;

internal static class ReaderPaginationScripts
{
    public const string ViewportWidthVariable = "--kkindle-reader-page-viewport-width";
    // Pagination CSS and every navigation path must use the exact same page
    // width. visualViewport/clientWidth can briefly describe the unclipped
    // WebView while WinUI side panes are being laid out, which makes a
    // fragment jump land one body inset before the real column boundary.
    public const string PageStepExpression =
        "parseFloat(getComputedStyle(document.documentElement).getPropertyValue('"
        + ViewportWidthVariable
        + "')) || window.visualViewport?.width || window.innerWidth"
        + " || document.documentElement.clientWidth"
        + " || document.scrollingElement?.clientWidth || 0";

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
        // Pin the number of visible columns instead of asking Chromium to
        // infer it from a calculated column-width. Under WebView DPI scaling,
        // innerWidth can briefly differ from the CSS layout viewport; the
        // inferred two-page layout then collapses to one wide column. With an
        // explicit count, one page is always one column and a spread is always
        // two columns, while padding + gaps still total exactly one viewport.
        var columnCount = twoPage ? 2 : 1;
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
            + $" writing-mode: horizontal-tb !important; column-width: auto !important; column-count: {columnCount} !important;"
            + $" column-gap: var(--kkindle-page-column-gap) !important; column-fill: auto !important; max-width: none !important; }}"
            // Chromium's scrollWidth for the overflowing multicolumns does not
            // include the body's right padding, so the maximum scroll position
            // lands with the LAST column's text flush against the viewport's
            // right edge. This invisible trailing block overflows its column by
            // exactly the half-gap, extending scrollWidth by that inset so the
            // final page can center its column like every other page.
            + $" body::after {{ content: \"\"; display: block; height: 0.1px; width: calc(100% + var(--kkindle-page-column-gap) / 2); }}";
    }

    // Page boundaries start at scroll origin 0; body padding stays inside
    // each viewport. The step comes from the same CSS variable that sizes the
    // multicolumn layout so side panes and DPI changes cannot desynchronise it.
    public static string Snap => CreateSnapScript();

    private static string CreateSnapScript()
    {
        var tolerance = Format(ReaderPaginationDefaults.SnapTolerance);
        return $$"""
            (() => {
              const el = document.scrollingElement || document.documentElement;
              if (!el) return;
              const step = {{PageStepExpression}};
              if (step <= 0) return;
              const rawMax = Math.max(0, el.scrollWidth - el.clientWidth);
              const trailingInset = parseFloat(getComputedStyle(document.body).paddingRight) || 0;
              // scrollWidth/clientWidth are integer-rounded, while scrollLeft
              // is device-pixel precise. Keep the logical page boundary here
              // and let Chromium clamp the request to its fractional maximum.
              const max = Math.max(0, Math.round(Math.max(0, rawMax - trailingInset) / step) * step);
              const nearest = Math.round(el.scrollLeft / step) * step;
              const target = el.scrollLeft >= max - {{tolerance}}
                ? max
                : Math.max(0, Math.min(max, nearest));
              window.scrollTo({ left: target, top: 0, behavior: 'instant' });
            })();
            """;
    }

    public static string CreateTurnScript(int direction, bool smooth = false)
    {
        var safeDirection = direction < 0 ? -1 : 1;
        var tolerance = Format(ReaderPaginationDefaults.SnapTolerance);
        var behavior = smooth ? "smooth" : "instant";
        return $$"""
            (() => {
              const el = document.scrollingElement || document.documentElement;
              if (!el) return false;
              const step = {{PageStepExpression}};
              if (step <= 0) return false;
              const rawMax = Math.max(0, el.scrollWidth - el.clientWidth);
              const trailingInset = parseFloat(getComputedStyle(document.body).paddingRight) || 0;
              const max = Math.max(0, Math.round(Math.max(0, rawMax - trailingInset) / step) * step);
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
              const step = {{PageStepExpression}};
              if (step <= 0) return false;
              const rawMax = Math.max(0, el.scrollWidth - el.clientWidth);
              const trailingInset = parseFloat(getComputedStyle(document.body).paddingRight) || 0;
              const max = Math.max(0, Math.round(Math.max(0, rawMax - trailingInset) / step) * step);
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

    public static string CreateRestorePositionScript(
        double left,
        double top,
        bool pagination)
    {
        var safeLeft = double.IsFinite(left) ? Math.Max(0, left) : 0;
        var safeTop = double.IsFinite(top) ? Math.Max(0, top) : 0;
        if (!pagination)
        {
            return $$"""
            (() => {
              window.scrollTo({ left: {{Format(safeLeft)}}, top: {{Format(safeTop)}}, behavior: 'instant' });
            })();
            """;
        }

        // Persisted positions may come from a different WebView width. Resolve
        // the saved pixel to a page index first and write only the final page
        // boundary; briefly restoring the stale raw pixel exposes a clipped
        // column and lets asynchronous layout work preserve the bad offset.
        return $$"""
            (() => {
              const el = document.scrollingElement || document.documentElement;
              const body = document.body;
              if (!el || !body) return false;
              const step = {{PageStepExpression}};
              if (step <= 0) return false;
              const requested = {{Format(safeLeft)}};
              const rawMax = Math.max(0, el.scrollWidth - el.clientWidth);
              const trailingInset = parseFloat(getComputedStyle(body).paddingRight) || 0;
              const max = Math.max(
                0,
                Math.round(Math.max(0, rawMax - trailingInset) / step) * step);
              const pageIndex = Math.round(requested / step);
              const target = requested >= max - 4
                ? max
                : Math.max(0, Math.min(max, pageIndex * step));
              window.scrollTo({ left: target, top: 0, behavior: 'instant' });
              return true;
            })();
            """;
    }

    public static string CreateChapterBoundaryScript(bool moveToEnd, bool horizontal) =>
        $$"""
        (() => {
          const el = document.scrollingElement || document.documentElement;
          if (!el) return false;
          const moveToEnd = {{(moveToEnd ? "true" : "false")}};
          const horizontal = {{(horizontal ? "true" : "false")}};
          window.scrollTo(horizontal
            ? { left: moveToEnd ? el.scrollWidth : 0, top: 0, behavior: 'instant' }
            : { left: 0, top: moveToEnd ? el.scrollHeight : 0, behavior: 'instant' });
          return true;
        })();
        """;

    private static string Format(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}
