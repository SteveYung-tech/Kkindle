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
            + $" body {{ height: 100%; overflow: visible !important; padding: {topPadding}px {horizontalPadding}px {bottomPadding}px !important; box-sizing: border-box;"
            + $" writing-mode: horizontal-tb !important; column-width: calc(var({ViewportWidthVariable}, 100vw) - {columnWidthSubtraction}px);"
            + $" column-gap: {columnGap}px; column-fill: auto; column-count: auto !important; max-width: none !important; }}";
    }

    // The scroll container is the source of truth for page geometry. In
    // particular, do not use window.innerWidth here: it can differ from the
    // WebView's client width when a scrollbar, DPI scale, or composition
    // island boundary is involved.
    public static string Snap => CreateSnapScript();

    private static string CreateSnapScript()
    {
        var tolerance = Format(ReaderPaginationDefaults.SnapTolerance);
        return $$"""
            (() => {
              const el = document.scrollingElement || document.documentElement;
              if (!el) return;
              const step = el.clientWidth || document.documentElement.clientWidth || window.innerWidth || 0;
              if (step <= 0) return;
              const body = document.body;
              const padLeft = body ? (parseFloat(getComputedStyle(body).paddingLeft) || 0) : 0;
              const max = Math.max(0, el.scrollWidth - el.clientWidth);
              const nearest = padLeft + Math.round((el.scrollLeft - padLeft) / step) * step;
              const target = el.scrollLeft >= max - {{tolerance}}
                ? max
                : Math.max(0, Math.min(max, nearest));
              window.scrollTo({ left: target, top: 0, behavior: 'instant' });
            })();
            """;
    }

    public static string CreateTurnScript(int direction)
    {
        var safeDirection = direction < 0 ? -1 : 1;
        var tolerance = Format(ReaderPaginationDefaults.SnapTolerance);
        return $$"""
            (() => {
              const el = document.scrollingElement || document.documentElement;
              if (!el) return false;
              const step = el.clientWidth || document.documentElement.clientWidth || window.innerWidth || 0;
              if (step <= 0) return false;
              const body = document.body;
              const padLeft = body ? (parseFloat(getComputedStyle(body).paddingLeft) || 0) : 0;
              const max = Math.max(0, el.scrollWidth - el.clientWidth);
              const nearest = padLeft + Math.round((el.scrollLeft - padLeft) / step) * step;
              const current = el.scrollLeft >= max - 4
                ? max
                : Math.max(0, Math.min(max, nearest));
              if ({{safeDirection}} < 0 && current <= padLeft + {{tolerance}}) return false;
              if ({{safeDirection}} > 0 && current >= max - {{tolerance}}) return false;
              const target = Math.max(
                0,
                Math.min(max, current + ({{safeDirection}} < 0 ? -step : step)));
              window.scrollTo({ left: target, top: 0, behavior: 'smooth' });
              return true;
            })();
            """;
    }

    private static string Format(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}
