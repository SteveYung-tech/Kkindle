namespace Kkindle;

internal static class ReaderAppearanceScripts
{
    // WebView2 uses Chromium scrollbars, so mirror the native reader scrollbar
    // resources with the same 10px monochrome geometry.
    public const string MonochromeScrollbarCss = """
        html, body, html * {
          scrollbar-color: #000000 #FFFFFF !important;
          scrollbar-width: auto !important;
        }
        html::-webkit-scrollbar,
        body::-webkit-scrollbar,
        html *::-webkit-scrollbar {
          width: 10px;
          height: 10px;
        }
        html::-webkit-scrollbar-track,
        body::-webkit-scrollbar-track,
        html *::-webkit-scrollbar-track {
          background: #FFFFFF;
          border: 0;
        }
        html::-webkit-scrollbar-thumb,
        body::-webkit-scrollbar-thumb,
        html *::-webkit-scrollbar-thumb {
          background: #000000;
          border: 1px solid #000000;
          border-radius: 0;
          min-height: 8px;
        }
        html::-webkit-scrollbar-thumb:hover,
        body::-webkit-scrollbar-thumb:hover,
        html *::-webkit-scrollbar-thumb:hover {
          background: #111111;
        }
        html::-webkit-scrollbar-thumb:active,
        body::-webkit-scrollbar-thumb:active,
        html *::-webkit-scrollbar-thumb:active {
          background: #000000;
        }
        html::-webkit-scrollbar-corner,
        body::-webkit-scrollbar-corner,
        html *::-webkit-scrollbar-corner {
          background: #FFFFFF;
        }
        """;
}
