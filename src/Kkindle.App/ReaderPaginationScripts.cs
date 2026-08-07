using System.Globalization;
using Kkindle.Core;

namespace Kkindle;

internal static class ReaderPaginationScripts
{
    public const string ViewportWidthVariable = "--kkindle-reader-page-viewport-width";

    public static string CreateFlowCss(bool pagination, bool vertical)
    {
        if (!pagination)
        {
            return vertical
                ? "html { height: 100%; overflow: hidden !important; } body { height: 100%; overflow: visible !important; box-sizing: border-box; }"
                : "html, body { min-height: 100%; overflow-x: hidden !important; } body { column-width: auto !important; column-count: auto !important; column-gap: normal !important; writing-mode: horizontal-tb !important; }";
        }

        var topPadding = Format(ReaderPaginationDefaults.TopPadding);
        var horizontalPadding = Format(ReaderPaginationDefaults.HorizontalPadding);
        var bottomPadding = Format(ReaderPaginationDefaults.BottomPadding);
        var columnGap = Format(ReaderPaginationDefaults.ColumnGap);
        var columnWidthSubtraction = Format(ReaderPaginationDefaults.HorizontalPadding * 2);
        return $"html {{ height: 100%; overflow: hidden !important; writing-mode: horizontal-tb !important; }}"
            + $" body {{ width: 100% !important; min-width: 0 !important; height: 100% !important; margin: 0 !important; overflow: visible !important; padding: {topPadding}px {horizontalPadding}px {bottomPadding}px !important; box-sizing: border-box !important;"
            + $" writing-mode: horizontal-tb !important; column-width: calc(var({ViewportWidthVariable}, 100vw) - {columnWidthSubtraction}px) !important;"
            + $" column-gap: {columnGap}px !important; column-fill: auto !important; column-count: auto !important; max-width: none !important; }}";
    }

    // The scroll container is the source of truth for page geometry. Prefer
    // its rendered width over window.innerWidth/clientWidth: the latter can
    // be rounded under DPI scaling, while CSS columns use fractional pixels.
    // Page boundaries start at scroll origin 0; the body's horizontal padding
    // must remain visible inside each viewport.
    public static string Snap => CreateSnapScript();

    private static string CreateSnapScript()
    {
        var tolerance = Format(ReaderPaginationDefaults.SnapTolerance);
        return $$"""
            (() => {
              const el = document.scrollingElement || document.documentElement;
              if (!el) return;
              const renderedWidth = el.getBoundingClientRect?.().width || 0;
              const step = renderedWidth || el.clientWidth || document.documentElement.clientWidth || window.innerWidth || 0;
              if (step <= 0) return;
              const max = Math.max(0, el.scrollWidth - el.clientWidth);
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
              const renderedWidth = el.getBoundingClientRect?.().width || 0;
              const step = renderedWidth || el.clientWidth || document.documentElement.clientWidth || window.innerWidth || 0;
              if (step <= 0) return false;
              const max = Math.max(0, el.scrollWidth - el.clientWidth);
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
              const renderedWidth = el.getBoundingClientRect?.().width || 0;
              const step = renderedWidth || el.clientWidth || document.documentElement.clientWidth || window.innerWidth || 0;
              if (step <= 0) return false;
              const max = Math.max(0, el.scrollWidth - el.clientWidth);
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
