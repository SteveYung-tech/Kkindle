using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

/// <summary>
/// Reader interactions that are independent of the native webview control:
/// layout injection, TOC/fragment navigation, in-page search, basic marks and
/// the small productivity tools that make the first reader surface usable.
/// </summary>
public partial class MainWindow
{
    private const int ReaderAnimationNone = 0;
    private const int ReaderAnimationFade = 1;
    private const int ReaderAnimationSlide = 2;
    private const int ReaderAnimationWave = 3;

    private ReaderLayoutSettings _readerLayout = ReaderLayoutDefaults.Normalize(new ReaderLayoutSettings());
    private int _readerPageAnimation = ReaderAnimationFade;
    private IReadOnlyList<EpubReaderNavigationItem> _readerTocItems = [];
    private ReaderProgressRow? _readerRestoredProgress;
    private int _readerSearchSequence;
    private int _readerSearchCount;
    private int _readerSearchIndex = -1;
    private int? _readerPendingChunkOffset;
    private string? _readerPendingSearchQuery;
    private string? _readerPendingSearchContext;
    private int _readerBookmarkIndicatorSequence;
    private int _readerFootnoteHoverSequence;
    private bool _readerFootnotePinned;
    private string? _readerPendingBookmarkQuote;
    private int? _readerPendingBookmarkPosition;
    private int _readerPendingBookmarkFlowMode;
    private string? _readerCurrentFragment;
    private ReaderAnnotation? _readerPendingAnnotation;
    private bool _suppressReaderTocSelectionNavigation;
    private readonly SemaphoreSlim _readerSearchMutationGate = new(1, 1);
    private string? _readerPendingSelection;
    private int _readerPendingSelectionStartOffset;
    private int _readerPendingSelectionEndOffset;
    private string _readerPendingSelectionPrefix = string.Empty;
    private string _readerPendingSelectionSuffix = string.Empty;
    private double _readerScrollPosition;
    private double _readerScrollRatio;
    private double _readerScrollWidth;
    private double _readerScrollHeight;
    private double _readerClientWidth;
    private double _readerClientHeight;
    private DateTimeOffset _readerSessionStarted;
    private WindowState _readerWindowStateBeforeZen = WindowState.Normal;
    private bool _readerZenMode;
    private bool _readerAssistantVisibleBeforeZen = true;
    private bool _readerTocExpandedBeforeZen = true;
    private bool _readerTocMinimalBeforeZen;
    private long _readerActiveSeconds;
    private long _readerSessionSeconds;
    private long _readerStatsBaseSeconds;
    private DispatcherTimer? _readerStatsTimer;
    private readonly SemaphoreSlim _readerStatsFlushGate = new(1, 1);
    private int _readerTransientStatusSequence;
    private bool _readerContinuousLocked;
    private int _readerContinuousDirection;
    private bool _readerLastNearTop;
    private bool _readerLastNearBottom;
    private bool _readerContinuousPositionInitialized;
    private double _readerPreviousScrollPosition;
    private DateTimeOffset _readerLastChapterChange = DateTimeOffset.MinValue;
    private bool _readerScrollPollRunning;
    private int _readerWheelDeltaRemainder;
    private int _readerContinuousSkipDepth;
    private int _readerProgressSaveSequence;
    private bool _readerProgressSliderUpdating;
    private bool _readerAiBusy;
    private bool _suppressAiProviderChange;
    private bool _suppressAiModelChange;
    private bool _suppressAiReasoningDepthChange;
    private string _readerAiReasoningDepth = "auto";
    private readonly List<AiConversationTurn> _readerAiConversation = [];
    private CancellationTokenSource? _readerAiCancellation;
    private AiConnectionSettings _readerAiSettings = new();
    private IReadOnlyList<string> _readerAiAvailableModels = [];
    private IReadOnlyList<PdfPageText> _readerPdfPages = [];
    private int _readerPdfPage = 1;
    private string? _readerPdfSourcePath;
    private bool _readerIsPdf;
    private ReaderAnnotation? _selectedReaderAnnotation;

    private sealed record ReaderScrollState(
        double Position,
        double Ratio,
        double ScrollWidth,
        double ScrollHeight,
        double ClientWidth,
        double ClientHeight);

    public ObservableCollection<ReaderBookmark> ReaderBookmarks { get; } = [];
    public ObservableCollection<ReaderAnnotation> ReaderAnnotations { get; } = [];
    public ObservableCollection<ReaderSearchResultViewModel> ReaderSearchResults { get; } = [];
    public ObservableCollection<ReaderAiMessageViewModel> ReaderAiMessages { get; } = [];
    public ObservableCollection<ReaderAiSourceViewModel> ReaderAiSources { get; } = [];

    private async Task InitializeReaderInteractionAsync(
        EpubReaderDocument document,
        BookFile file,
        CancellationToken cancellationToken)
    {
        var settings = await _readerData.GetLayoutSettingsAsync(file.Id, cancellationToken);
        _readerLayout = ReaderLayoutDefaults.Normalize(settings ?? new ReaderLayoutSettings());
        _readerTocItems = document.Navigation.Count == 0
            ? document.Chapters.Select((path, index) => new EpubReaderNavigationItem(
                $"第 {index + 1} 章",
                new Uri(path).AbsoluteUri,
                index)).ToArray()
            : document.Navigation;
        _readerRestoredProgress = null;
        _readerBookmarkIndicatorSequence++;
        _readerPendingBookmarkQuote = null;
        _readerPendingBookmarkPosition = null;
        _readerPendingBookmarkFlowMode = 0;
        _readerCurrentFragment = null;
        _readerPendingAnnotation = null;
        _readerPendingSelection = null;
        _readerPendingSelectionStartOffset = 0;
        _readerPendingSelectionEndOffset = 0;
        _readerPendingSelectionPrefix = string.Empty;
        _readerPendingSelectionSuffix = string.Empty;
        _readerScrollPosition = 0;
        _readerScrollRatio = 0;
        _readerScrollWidth = 0;
        _readerScrollHeight = 0;
        _readerClientWidth = 0;
        _readerClientHeight = 0;
        _readerSearchCount = 0;
        _readerSearchIndex = -1;
        _readerPendingChunkOffset = null;
        _readerPendingSearchQuery = null;
        _readerPendingSearchContext = null;
        _readerSearchSequence++;
        _readerWholeSearchSequence++;
        _readerContinuousSkipDepth = 0;
        _readerContinuousLocked = false;
        _readerContinuousDirection = 0;
        ResetReaderContinuousEdgeTracking();
        _readerScrollPollRunning = false;
        _readerWheelDeltaRemainder = 0;
        _readerPdfSearchSequence++;
        _readerSessionStarted = DateTimeOffset.UtcNow;
        _readerZenMode = false;
        _readerAssistantVisibleBeforeZen = true;
        _readerIsPdf = false;
        _readerPdfPages = [];
        _readerPdfPage = 1;
        _readerTocExpanded = true;
        _readerTocMinimal = false;
        _readerTocExpandedBeforeZen = true;
        _readerTocMinimalBeforeZen = false;
        _readerActiveSeconds = 0;
        _readerSessionSeconds = 0;
        _readerStatsBaseSeconds = 0;
        ReaderBookInfoText.Text = _readerBookCard?.Title ?? "目录";
        ReaderTocList.ItemsSource = _readerTocItems;
        ReaderTocList.SelectedIndex = -1;
        ReaderTocPanel.IsVisible = false;
        ReaderTocView.IsVisible = true;
        ReaderBookmarkPane.IsVisible = false;
        ReaderSearchPanel.IsVisible = false;
        ReaderBookmarkEmptyText.IsVisible = ReaderBookmarks.Count == 0;
        ReaderSearchResults.Clear();
        ReaderWholeSearchCountText.Text = string.Empty;
        ReaderSearchStatusText.IsVisible = true;
        ReaderSearchResultList.IsVisible = false;
        ReaderInPageSearchBar.IsVisible = false;
        ReaderLayoutSettingsOverlay.IsVisible = false;
        ReaderHighlightButton.IsVisible = false;
        ReaderAnnotateButton.IsVisible = false;
        ReaderFootnotePopup.IsVisible = false;
        ReaderBookmarkCornerMarker.IsVisible = false;
        ReaderAiMessages.Clear();
        ReaderAiSources.Clear();
        ReaderAiEmptyState.IsVisible = true;
        ReaderAiView.IsVisible = true;
        ReaderNotesView.IsVisible = false;
        ReaderAiSettingsView.IsVisible = false;
        ReaderAiComposer.IsVisible = true;
        ReaderAssistantPanel.IsVisible = true;
        ReaderRoot.ColumnDefinitions[2].Width = new GridLength(360);
        SetReaderCompactNavigationItems(_readerTocItems);
        SetReaderCompactSelectedItem(
            _readerTocItems.FirstOrDefault(item => item.ChapterIndex == _readerChapterIndex));
        ApplyReaderPanelLayout();
        UpdateReaderZoomLabel();
        await RefreshReaderBookmarksAsync(cancellationToken);
        await RefreshReaderAnnotationsAsync(cancellationToken);
        await InitializeReaderAiAsync(cancellationToken);
        UpdateReaderToolbar();
        await LoadReaderStatsBaseAsync();
        StartReaderStatsTimer();
    }

    private async Task ConfigureReaderHostAsync(
        IReaderHost host,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // PDF renders inside WebView2's built-in viewer: there is no document
        // to inject layout into (and InvokeScript would throw), so host
        // configuration applies to EPUB pages only.
        if (_readerIsPdf) return;
        var pagination = _readerLayout.FlowMode == 1;
        var flowCss = ReaderPaginationScripts.CreateFlowCss(
            pagination,
            _readerLayout.VerticalWriting,
            _readerLayout.TwoPageMode,
            _readerLayout.BodyPadding,
            _readerLayout.MaxWidth);
        var css = ReaderAppearanceScripts.MonochromeScrollbarCss
            + "\n"
            + flowCss
            + BuildReaderAppearanceCss(
                _readerLayout.FontScale,
                _readerLayout.LineHeight,
                BuildReaderFontStack(_readerLayout.FontFamily),
                _readerLayout.VerticalWriting,
                pagination);
        if (!pagination)
        {
            css += $"\nbody {{ max-width: {Format(_readerLayout.MaxWidth)}px !important; margin-left: auto !important; margin-right: auto !important; }}";
        }

        var serializedCss = JsonSerializer.Serialize(css);
        var script = $$"""
            (() => {
              const css = {{serializedCss}};
              let style = document.getElementById('kkindle-reader-style');
              if (!style) {
                style = document.createElement('style');
                style.id = 'kkindle-reader-style';
                document.head.appendChild(style);
              }
              style.textContent = css;
              // FontFaceSet is asynchronous. Reset the per-document marker so
              // the host waits for this style pass before revealing a newly
              // navigated chapter.
              window.__kkindleReaderFontReady = false;
              window.__kkindleReaderFontWaitStarted = false;
              document.documentElement.style.setProperty(
                '--kkindle-reader-page-viewport-width',
                (window.innerWidth || document.documentElement.clientWidth || 0) + 'px');
              window.__kkindleReaderFlowMode = {{_readerLayout.FlowMode}};
              window.__kkindleReaderVertical = {{(_readerLayout.VerticalWriting ? "true" : "false")}};
              window.__kkindleReaderTwoPage = {{(_readerLayout.TwoPageMode ? "true" : "false")}};
              // Image limits must use the body's real content box. Using
              // 100vh without subtracting page padding makes a tall image plus
              // its margins too large for the first column, so Chromium moves
              // the whole image to page two and leaves page one blank.
              const root = document.documentElement;
              const body = document.body;
              if (root && body) {
                const bodyStyle = getComputedStyle(body);
                const paddingTop = parseFloat(bodyStyle.paddingTop) || 0;
                const paddingBottom = parseFloat(bodyStyle.paddingBottom) || 0;
                const contentHeight = body.clientHeight - paddingTop - paddingBottom;
                if (contentHeight > 0)
                  root.style.setProperty('--kkindle-page-content-h', contentHeight + 'px');
              }
              return true;
            })();
            """;
        await host.InvokeScriptAsync(script);
        await WaitForReaderFontsAsync(host, cancellationToken);
        await host.InvokeScriptAsync(ReaderPaginationScripts.PageAlignmentHelperDefinition);
        if (pagination)
        {
            await host.InvokeScriptAsync(FitReaderCoverImageScript);
            await host.InvokeScriptAsync(ReaderPaginationScripts.Snap);
        }

        if (ReferenceEquals(host, CurrentReaderHost))
        {
            if (_readerRestoredProgress is { } progress
                && progress.ChapterIndex == _readerChapterIndex)
            {
                if (progress.ScrollPosition > 0)
                {
                    var horizontal = pagination || _readerLayout.VerticalWriting;
                    var left = horizontal ? progress.ScrollPosition : 0;
                    var top = horizontal ? 0 : progress.ScrollPosition;
                    await host.InvokeScriptAsync(
                        $"(() => {{ window.scrollTo({{ left: {left.ToString(CultureInfo.InvariantCulture)}, top: {top.ToString(CultureInfo.InvariantCulture)}, behavior: 'instant' }}); }})();");
                }
                else if (!string.IsNullOrWhiteSpace(progress.Fragment))
                {
                    var fragment = EscapeJavaScriptSingleQuoted(
                        DecodeReaderFragment(progress.Fragment) ?? string.Empty);
                    await host.InvokeScriptAsync(ReaderNavigationScripts.CreateFragmentScroll(
                        fragment,
                        _readerLayout.FlowMode,
                        _readerLayout.VerticalWriting,
                        _readerLayout.TwoPageMode));
                }
                _readerRestoredProgress = null;
            }
            await ApplySavedAnnotationsAsync(host, cancellationToken);
        }
    }

    // Reader appearance overrides, mirroring the WinUI reference's
    // ApplyReaderAppearanceAsync: white surface, bundled @font-face, selection
    // inversion, justified text, link/heading/paragraph/blockquote spacing,
    // image constraints for paginated columns, horizontal overflow guards and
    // the fragment-break anchor rule used by CreateFragmentScroll.
    private static string BuildReaderAppearanceCss(
        double fontScale,
        double lineHeight,
        string fontStack,
        bool vertical,
        bool pagination)
    {
        var builder = new StringBuilder();
        builder.Append($"\nhtml {{ font-size: {Format(fontScale * 100)}% !important; text-rendering: optimizeLegibility; }}");
        builder.Append("\nhtml, body { background: #FFFFFF !important; color: #111111 !important; border: 0 !important; outline: 0 !important; box-shadow: none !important; }");
        builder.Append($"\nbody {{ font-size: 1rem !important; line-height: {Format(lineHeight)} !important; font-family: {fontStack} !important; letter-spacing: 0.012em !important; overflow-wrap: anywhere; box-sizing: border-box; line-break: strict; word-break: normal; }}");
        if (!vertical)
            builder.Append("\nbody { text-align: justify !important; }");
        builder.Append("\n::selection, body *::selection { background: #000000 !important; background-color: #000000 !important; color: #FFFFFF !important; -webkit-text-fill-color: #FFFFFF !important; }");
        builder.Append("\na { color: #222222 !important; }");
        builder.Append("\np { margin: 0.55em 0 1.05em 0 !important; }");
        builder.Append("\nli, blockquote { font-size: 1rem !important; line-height: 1.78 !important; }");
        builder.Append("\nh1, h2, h3, h4 { color: #111111 !important; line-height: 1.35 !important; font-weight: bold !important; margin: 1.35em 0 0.72em 0 !important; }");
        builder.Append("\nblockquote { border-left: 3px solid #222222 !important; margin: 1.4em 0 !important; padding: 0.2em 1.1em !important; color: #333333 !important; opacity: 0.88; }");
        builder.Append("\nimg, svg { display: block; width: auto !important; max-width: 100% !important; height: auto !important; margin: 1.8em auto !important; break-inside: avoid; } svg image { max-width: 100% !important; }");
        if (pagination)
            builder.Append("\nimg, svg { max-height: calc(var(--kkindle-page-content-h, 100vh) - 3.6em) !important; object-fit: contain !important; } img.kkindle-cover, .kkindle-cover img, svg.kkindle-cover, .kkindle-cover svg { max-height: calc(var(--kkindle-page-content-h, 100vh) - 6em) !important; margin: 1em auto !important; }");
        builder.Append("\npre, table { max-width: 100% !important; overflow-x: auto !important; }");
        builder.Append("\nhr { border: 0 !important; border-top: 1px solid #222222 !important; opacity: 0.24; margin: 2em 0 !important; }");
        builder.Append("\nruby { ruby-align: center !important; } rt { font-size: 0.5em !important; color: inherit !important; }");
        builder.Append("\n.kkindle-fragment-break { break-before: column !important; }");
        var bundledFontUri = GetBundledFontFileUri();
        if (bundledFontUri is not null)
            builder.Append($"\n@font-face {{ font-family: \"{ReaderFontDefaults.BundledFamily}\"; src: url(\"{bundledFontUri}\") format(\"truetype\"); font-display: swap; }}");
        return builder.ToString();
    }

    // Image-only chapters and cover pages often contain a raster that is
    // nearly as large as the WebView. Mark the first large image so the
    // tighter cover rule keeps it on the first pagination column. Dimensions
    // come from the media itself rather than book-specific names or classes.
    private const string FitReaderCoverImageScript = """
        (() => {
          const root = document.documentElement;
          const body = document.body;
          if (!root || !body) return false;
          const viewWidth = root.clientWidth || window.innerWidth || 0;
          const viewHeight = root.clientHeight || window.innerHeight || 0;
          if (viewWidth <= 0 || viewHeight <= 0) return false;

          const bodyStyle = getComputedStyle(body);
          const paddingTop = parseFloat(bodyStyle.paddingTop) || 0;
          const paddingBottom = parseFloat(bodyStyle.paddingBottom) || 0;
          const contentHeight = body.clientHeight - paddingTop - paddingBottom;
          if (contentHeight > 0)
            root.style.setProperty('--kkindle-page-content-h', contentHeight + 'px');

          const candidates = Array.from(
            document.querySelectorAll('body img, body svg, body svg image'));
          for (const element of candidates) {
            const isSvgImage = element.tagName.toLowerCase() === 'image';
            const naturalWidth = element.naturalWidth
              || parseFloat(element.getAttribute('width')) || 0;
            const naturalHeight = element.naturalHeight
              || parseFloat(element.getAttribute('height')) || 0;
            if (naturalWidth <= 0 || naturalHeight <= 0) continue;
            const viewportArea = viewWidth * viewHeight;
            if (naturalWidth * naturalHeight < viewportArea * 0.35
                && !(naturalWidth >= viewWidth * 0.6
                  && naturalHeight >= viewHeight * 0.6)) continue;
            element.classList.add('kkindle-cover');
            if (isSvgImage && element.parentElement
                && /^svg$/i.test(element.parentElement.tagName))
              element.parentElement.classList.add('kkindle-cover');
            return true;
          }
          return false;
        })();
        """;

    // A WebView navigation completion only means that the document itself is
    // ready; it does not mean a font introduced by the injected style has
    // finished downloading. Keep the native host hidden while FontFaceSet
    // settles so a TOC swap cannot reveal fallback text and then reflow into
    // the bundled reading font.
    private static async Task WaitForReaderFontsAsync(
        IReaderHost host,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 24;
        const int delayMilliseconds = 25;
        const string waitScript = """
            (() => {
              const fonts = document.fonts;
              if (!fonts) return 'ready';
              if (!window.__kkindleReaderFontWaitStarted) {
                window.__kkindleReaderFontWaitStarted = true;
                const requests = [fonts.ready];
                try {
                  // Explicitly request the bundled family. It is the fallback
                  // used by the default reader stack and this also starts the
                  // load when the page's own CSS did not specify a font face.
                  requests.push(fonts.load('1em "KingHwaOldSong"'));
                } catch (_) { }
                Promise.allSettled(requests).then(() => {
                  window.__kkindleReaderFontReady = true;
                });
              }
              return window.__kkindleReaderFontReady === true ? 'ready' : 'pending';
            })();
            """;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await host.InvokeScriptAsync(waitScript);
                if (string.Equals(
                    result?.Trim().Trim('"'),
                    "ready",
                    StringComparison.OrdinalIgnoreCase))
                    return;
            }
            catch
            {
                // A fixed-layout or partially initialized document may reject
                // FontFaceSet access. The style has still been applied, so do
                // not turn a cosmetic wait into a chapter navigation failure.
                return;
            }

            await Task.Delay(delayMilliseconds, cancellationToken);
        }
    }

    // Fallback stack for the reading font, mirroring the WinUI reference's
    // BuildReaderFontStack: the chosen family first (comma-separated combined
    // families like "Source Han Serif SC, Noto Serif CJK SC" are split), then
    // the bundled KingHwaOldSong, common serif CJK fonts and sans-serif.
    private static string BuildReaderFontStack(string? fontFamily)
    {
        var families = new List<string>();
        void Add(string? family)
        {
            var value = family?.Trim();
            if (string.IsNullOrWhiteSpace(value)) return;
            if (families.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
                return;
            families.Add(value);
        }
        if (!string.IsNullOrWhiteSpace(fontFamily))
        {
            foreach (var part in fontFamily.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Add(part);
            }
        }
        Add(ReaderFontDefaults.BundledFamily);
        Add("Source Han Serif SC");
        Add("Noto Serif CJK SC");
        Add("Microsoft YaHei UI");
        families.Add("sans-serif");
        return string.Join(", ", families.Select(family => $"\"{family}\""));
    }

    private static string? GetBundledFontFileUri()
    {
        try
        {
            var path = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "Fonts",
                "KingHwaOldSong-v3.0.ttf");
            return File.Exists(path) ? new Uri(path).AbsoluteUri : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<ReaderScrollState?> CaptureReaderScrollStateAsync(IReaderHost host)
    {
        if (_readerIsPdf) return null;
        try
        {
            var result = await host.InvokeScriptAsync(
                "(() => { const el = document.scrollingElement || document.documentElement; if (!el) return null; return JSON.stringify({ left: el.scrollLeft || 0, top: el.scrollTop || 0, scrollWidth: el.scrollWidth || 0, scrollHeight: el.scrollHeight || 0, clientWidth: el.clientWidth || 0, clientHeight: el.clientHeight || 0 }); })();");
            var raw = DecodeReaderScriptString(result);
            if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase))
                return null;

            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var horizontal = _readerLayout.FlowMode == 1 || _readerLayout.VerticalWriting;
            var position = horizontal ? ReadDouble(root, "left") : ReadDouble(root, "top");
            var scrollWidth = ReadDouble(root, "scrollWidth");
            var scrollHeight = ReadDouble(root, "scrollHeight");
            var clientWidth = ReadDouble(root, "clientWidth");
            var clientHeight = ReadDouble(root, "clientHeight");
            var maximum = horizontal
                ? Math.Max(0, scrollWidth - clientWidth)
                : Math.Max(0, scrollHeight - clientHeight);
            var ratio = maximum > 0 ? Math.Clamp(position / maximum, 0, 1) : 0;
            return new ReaderScrollState(
                Math.Max(0, position),
                ratio,
                scrollWidth,
                scrollHeight,
                clientWidth,
                clientHeight);
        }
        catch
        {
            return null;
        }
    }

    private async Task UpdateReaderScrollStateAsync(IReaderHost host)
    {
        var state = await CaptureReaderScrollStateAsync(host);
        if (state is null) return;
        _readerScrollPosition = state.Position;
        _readerScrollRatio = state.Ratio;
        _readerScrollWidth = state.ScrollWidth;
        _readerScrollHeight = state.ScrollHeight;
        _readerClientWidth = state.ClientWidth;
        _readerClientHeight = state.ClientHeight;
    }

    private async Task RestoreReaderScrollStateAsync(
        IReaderHost host,
        ReaderScrollState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var horizontal = _readerLayout.FlowMode == 1 || _readerLayout.VerticalWriting;
        var ratio = state.Ratio.ToString(CultureInfo.InvariantCulture);
        var script = $$"""
            (() => {
              const el = document.scrollingElement || document.documentElement;
              if (!el) return false;
              const horizontal = {{(horizontal ? "true" : "false")}};
              const maximum = horizontal
                ? Math.max(0, (el.scrollWidth || 0) - (el.clientWidth || 0))
                : Math.max(0, (el.scrollHeight || 0) - (el.clientHeight || 0));
              const target = Math.max(0, Math.min(maximum, maximum * {{ratio}}));
              window.scrollTo(horizontal
                ? { left: target, top: 0, behavior: 'instant' }
                : { left: 0, top: target, behavior: 'instant' });
              return true;
            })();
            """;
        try
        {
            await host.InvokeScriptAsync(script);
            if (_readerLayout.FlowMode == 1)
                await host.InvokeScriptAsync(ReaderPaginationScripts.Snap);
            await UpdateReaderScrollStateAsync(host);
        }
        catch
        {
            // A layout pass can race a native host swap. The next bridge
            // scroll report will refresh the state when the host is stable.
        }
    }

    private async Task ApplyReaderLayoutToHostsAsync(CancellationToken cancellationToken)
    {
        ResetReaderContinuousEdgeTracking();
        var currentHost = CurrentReaderHost;
        var scrollState = currentHost is not null
            ? await CaptureReaderScrollStateAsync(currentHost)
            : null;
        var hosts = new[] { _readerActiveHost, _readerPreloadHost }
            .Where(host => host is not null)
            .Cast<IReaderHost>()
            .Distinct()
            .ToArray();
        await Task.WhenAll(hosts.Select(host => ConfigureReaderHostAsync(host, cancellationToken)));
        if (scrollState is not null
            && currentHost is not null
            && ReferenceEquals(CurrentReaderHost, currentHost))
        {
            await RestoreReaderScrollStateAsync(currentHost, scrollState, cancellationToken);
        }
        if (currentHost is not null && ReferenceEquals(CurrentReaderHost, currentHost))
        {
            await UpdateReaderScrollStateAsync(currentHost);
            PrimeReaderContinuousEdgeTracking();
        }
        if (!_readerIsPdf
            && currentHost is not null
            && ReferenceEquals(CurrentReaderHost, currentHost)
            && ReaderInPageSearchBar.IsVisible
            && !string.IsNullOrWhiteSpace(ReaderInPageSearchBox.Text))
        {
            var previousSearchIndex = _readerSearchIndex;
            var searchSequence = ++_readerSearchSequence;
            await ApplyReaderSearchAsync(
                ReaderInPageSearchBox.Text.Trim(),
                searchSequence,
                navigate: false);
            if (_readerSearchCount > 0)
            {
                _readerSearchIndex = Math.Clamp(previousSearchIndex, 0, _readerSearchCount - 1);
                await NavigateReaderSearchAsync(_readerSearchIndex, searchSequence);
            }
        }
        UpdateReaderToolbar();
    }

    private async Task<bool> NavigateToReaderItemAsync(
        EpubReaderNavigationItem item,
        CancellationToken cancellationToken,
        ReaderNavigationIntent intent = ReaderNavigationIntent.None)
    {
        if (_readerDocument is null || CurrentReaderHost is null) return false;
        if (item.ChapterIndex < 0 || item.ChapterIndex >= _readerDocument.Chapters.Count) return false;
        if (!Uri.TryCreate(item.Target, UriKind.Absolute, out var target) || !target.IsFile) return false;
        if (!IsPathInside(_readerDocument.RootPath, target.LocalPath)) return false;
        PruneReaderPendingLocations(intent);
        if (!ReaderNavigationLocationPolicy.UsesRestorePosition(intent))
            _readerRestoredProgress = null;
        ResetReaderContinuousEdgeTracking();

        var current = CurrentReaderHost;
        if (ReaderNavigationLocationPolicy.TargetsSameDocument(current.Source, target))
        {
            try
            {
                var direction = item.ChapterIndex < _readerChapterIndex ? -1 : 1;
                await RunReaderContentTransitionAsync(
                    current,
                    current,
                    direction,
                    async () =>
                    {
                        await ApplyReaderLocationAsync(
                            current,
                            target,
                            cancellationToken,
                            intent,
                            _readerRestoredProgress is not null);
                        _readerChapterIndex = item.ChapterIndex;
                        _readerCurrentFragment = GetReaderTargetFragment(target);
                        await UpdateReaderScrollStateAsync(current);
                        return true;
                    },
                    cancellationToken,
                    animate: intent != ReaderNavigationIntent.None);
                PrimeReaderContinuousEdgeTracking();
                SetReaderTocSelection(item);
                ReaderChapterText.Text = GetReaderChapterLabel();
                await UpdateReaderBookmarkIndicatorAsync();
                await SaveReaderProgressAsync(cancellationToken);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception)
            {
                ReaderStatusText.Text = $"定位失败：{exception.Message}";
                return false;
            }
        }

        await ResetReaderInPageSearchForNavigationAsync();
        var sessionToken = _readerSessionCancellation?.Token ?? cancellationToken;
        _readerNavigationCancellation?.Cancel();
        var navigationCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        _readerNavigationCancellation = navigationCancellation;
        var navigationToken = navigationCancellation.Token;
        var host = HiddenReaderHost ?? CurrentReaderHost;
        try
        {
            ReaderStatusText.Text = $"正在打开“{item.Title}”…";
            var loaded = await NavigateReaderHostAndWaitAsync(host, target, navigationToken);
            if (!loaded) throw new InvalidOperationException("章节加载失败。");

            await ApplySavedAnnotationsAsync(host, navigationToken);
            var direction = item.ChapterIndex < _readerChapterIndex ? -1 : 1;
            await RunReaderContentTransitionAsync(
                current,
                host,
                direction,
                async () =>
                {
                    _readerChapterIndex = item.ChapterIndex;
                    _readerScrollPosition = 0;
                    _readerScrollRatio = 0;
                    // host was picked as the hidden host, so the layer must flip
                    // unconditionally; deriving it from CurrentReaderHost would read
                    // the stale pre-swap flag and freeze the visible chapter after the
                    // first jump (TOC / next-chapter worked only once).
                    _readerShowingPreload = ReferenceEquals(host, _readerPreloadHost);
                    SetReaderHostLayer();
                    await ApplyReaderLocationAsync(
                        host,
                        target,
                        navigationToken,
                        intent,
                        _readerRestoredProgress is not null);
                    _readerCurrentFragment = GetReaderTargetFragment(target);
                    await UpdateReaderScrollStateAsync(host);
                    return true;
                },
                navigationToken,
                animate: intent != ReaderNavigationIntent.None);
            PrimeReaderContinuousEdgeTracking();
            SetReaderTocSelection(item);
            ReaderChapterText.Text = GetReaderChapterLabel();
            ReaderStatusText.Text = $"共 {_readerDocument.Chapters.Count} 个章节";
            await UpdateReaderBookmarkIndicatorAsync();
            await SaveReaderProgressAsync(sessionToken);
            _ = PreloadNextReaderChapterAsync(sessionToken);
            return true;
        }
        catch (OperationCanceledException) when (navigationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            ReaderStatusText.Text = $"打开章节失败：{exception.Message}";
            return false;
        }
        finally
        {
            if (ReferenceEquals(_readerNavigationCancellation, navigationCancellation))
                _readerNavigationCancellation = null;
            navigationCancellation.Dispose();
        }
    }

    private async Task ResetReaderInPageSearchForNavigationAsync()
    {
        if (_readerIsPdf
            || (!ReaderInPageSearchBar.IsVisible && _readerSearchCount <= 0
                && string.IsNullOrWhiteSpace(ReaderInPageSearchBox.Text)))
            return;

        await ClearReaderSearchAsync();
        ReaderInPageSearchBar.IsVisible = false;
        ReaderInPageSearchBox.Text = string.Empty;
    }

    // Intent-aware positioning (Kkindle.Core.ReaderNavigationLocationPolicy):
    // TOC entries without an explicit anchor and progress-slider jumps start
    // at the chapter's first line (normalizing the chapter start); anchors are
    // scrolled for TOC/bookmark/annotation targets; search and AI-source
    // targets keep the DOM untouched because their own offset-based scrolling
    // (and annotation offset math) depends on it; plain switches normalize
    // unless a breakpoint restore is pending.
    private void PruneReaderPendingLocations(ReaderNavigationIntent intent)
    {
        if (!ReaderNavigationLocationPolicy.KeepsChunkOffset(intent))
            _readerPendingChunkOffset = null;
        if (intent != ReaderNavigationIntent.Search)
        {
            _readerPendingSearchQuery = null;
            _readerPendingSearchContext = null;
        }
        if (!ReaderNavigationLocationPolicy.KeepsBookmarkQuote(intent))
        {
            _readerPendingBookmarkQuote = null;
            _readerPendingBookmarkPosition = null;
            _readerPendingBookmarkFlowMode = 0;
        }
        if (intent != ReaderNavigationIntent.Annotation)
            _readerPendingAnnotation = null;
    }

    private async Task ApplyReaderLocationAsync(
        IReaderHost host,
        Uri target,
        CancellationToken cancellationToken,
        ReaderNavigationIntent intent,
        bool hasPendingRestorePosition)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ReaderNavigationLocationPolicy.ShouldNormalizeChapterStart(intent, target, hasPendingRestorePosition))
        {
            await host.InvokeScriptAsync(ReaderNavigationScripts.NormalizeChapterStart);
            return;
        }

        if (intent is ReaderNavigationIntent.Search or ReaderNavigationIntent.AiSource)
        {
            await UpdateReaderLocationHashAsync(host, target, cancellationToken);
            await ScrollToPendingReaderChunkAsync(host, cancellationToken);
            return;
        }

        if (intent == ReaderNavigationIntent.Bookmark
            && ((_readerPendingBookmarkPosition is not null
                 && _readerPendingBookmarkFlowMode == _readerLayout.FlowMode)
                || !string.IsNullOrWhiteSpace(_readerPendingBookmarkQuote)))
        {
            await UpdateReaderLocationHashAsync(host, target, cancellationToken);
            await ScrollToPendingReaderBookmarkAsync(host, cancellationToken);
            return;
        }

        if (intent == ReaderNavigationIntent.Annotation
            && _readerPendingAnnotation is { } annotation)
        {
            await UpdateReaderLocationHashAsync(host, target, cancellationToken);
            await ScrollToPendingReaderAnnotationAsync(host, annotation, cancellationToken);
            _readerPendingAnnotation = null;
            return;
        }

        var fragment = target.Fragment;
        if (!string.IsNullOrWhiteSpace(fragment)
            && intent is not (ReaderNavigationIntent.Search or ReaderNavigationIntent.AiSource))
        {
            var escaped = EscapeJavaScriptSingleQuoted(
                DecodeReaderFragment(fragment) ?? string.Empty);
            await host.InvokeScriptAsync(ReaderNavigationScripts.CreateFragmentScroll(
                escaped,
                _readerLayout.FlowMode,
                _readerLayout.VerticalWriting,
                _readerLayout.TwoPageMode));
        }
    }

    private static async Task UpdateReaderLocationHashAsync(
        IReaderHost host,
        Uri target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var escaped = EscapeJavaScriptSingleQuoted(
            DecodeReaderFragment(target.Fragment) ?? string.Empty);
        await host.InvokeScriptAsync(
            ReaderNavigationScripts.CreateLocationHashUpdate(escaped));
    }

    private static string? GetReaderTargetFragment(Uri target)
    {
        var fragment = target.Fragment.TrimStart('#');
        if (string.IsNullOrWhiteSpace(fragment)) return null;
        return DecodeReaderFragment(fragment);
    }

    private static string? DecodeReaderFragment(string? value)
    {
        var fragment = value?.TrimStart('#');
        if (string.IsNullOrWhiteSpace(fragment)) return null;
        try { return Uri.UnescapeDataString(fragment); }
        catch { return fragment; }
    }

    private void SetReaderTocSelection(EpubReaderNavigationItem? item)
    {
        var index = item is null ? -1 : FindReaderTocIndex(item);
        _suppressReaderTocSelectionNavigation = true;
        try
        {
            ReaderTocList.SelectedIndex = index;
        }
        finally
        {
            _suppressReaderTocSelectionNavigation = false;
        }
        if (item is not null)
            ReaderTocList.ScrollIntoView(item);
        SetReaderCompactSelectedItem(item);
    }

    private void SetReaderTocSelectionForChapter(int chapterIndex)
    {
        SetReaderTocSelection(
            _readerTocItems.FirstOrDefault(item => item.ChapterIndex == chapterIndex));
    }

    private async Task ScrollToPendingReaderAnnotationAsync(
        IReaderHost host,
        ReaderAnnotation annotation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var serializedId = JsonSerializer.Serialize(annotation.Id.ToString("N"));
        var serializedQuote = JsonSerializer.Serialize(annotation.SelectedText.Trim());
        var startOffset = Math.Max(0, annotation.StartOffset);
        var endOffset = Math.Max(startOffset, annotation.EndOffset);
        var pagination = _readerLayout.FlowMode == 1 ? "true" : "false";
        var script = $$"""
            (() => {
              const id = {{serializedId}};
              const quote = {{serializedQuote}};
              const pagination = {{pagination}};
              const ignored = node => {
                const parent = node?.parentElement;
                return !parent
                  || ['SCRIPT', 'STYLE', 'NOSCRIPT'].includes(parent.tagName)
                  || !!parent.closest?.('#kkindle-selection-bar, .kkindle-wave-sweep');
              };
              const nodes = [];
              const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
              while (walker.nextNode()) {
                if (!ignored(walker.currentNode)) nodes.push(walker.currentNode);
              }
              if (nodes.length === 0) return false;
              const text = nodes.map(node => node.data || '').join('');
              const rangeFromOffsets = (start, end) => {
                let cursor = 0;
                let startNode = null;
                let endNode = null;
                let startLocal = 0;
                let endLocal = 0;
                for (const node of nodes) {
                  const next = cursor + (node.data || '').length;
                  if (!startNode && start <= next) {
                    startNode = node;
                    startLocal = Math.max(0, start - cursor);
                  }
                  if (end <= next) {
                    endNode = node;
                    endLocal = Math.max(0, end - cursor);
                    break;
                  }
                  cursor = next;
                }
                if (!startNode) return null;
                endNode = endNode || startNode;
                const range = document.createRange();
                range.setStart(startNode, Math.min(startLocal, startNode.data.length));
                range.setEnd(endNode, Math.min(endLocal, endNode.data.length));
                return range;
              };
              const reveal = range => {
                if (!range) return false;
                const scroller = document.scrollingElement || document.documentElement;
                const rects = range.getClientRects ? Array.from(range.getClientRects()) : [];
                const rect = rects.find(item => item.width > 0 || item.height > 0)
                  || range.getBoundingClientRect?.();
                if (!rect) return false;
                if (pagination) {
                  const step = {{ReaderPaginationScripts.PageStepExpression}};
                  if (step <= 0) return false;
                  const absoluteLeft = rect.left + (scroller.scrollLeft || 0) + Math.max(0, rect.width) / 2;
                  const maximum = Math.max(0, (scroller.scrollWidth || 0) - (scroller.clientWidth || 0));
                  const target = Math.max(0, Math.min(maximum, Math.floor(Math.max(0, absoluteLeft) / step) * step));
                  window.scrollTo({ left: target, top: 0, behavior: 'instant' });
                  return true;
                }
                const element = range.startContainer.parentElement;
                element?.scrollIntoView?.({ block: 'center', inline: 'nearest', behavior: 'instant' });
                return !!element;
              };

              const marked = document.querySelector(`[data-kkindle-annotation="${id}"]`);
              if (marked) {
                const markedRange = document.createRange();
                markedRange.selectNodeContents(marked);
                if (reveal(markedRange)) return true;
              }

              let start = {{startOffset}};
              let end = {{endOffset}};
              if (end <= start || start >= text.length) {
                const needle = (quote || '').trim();
                const at = needle ? text.toLocaleLowerCase().indexOf(needle.toLocaleLowerCase()) : -1;
                if (at < 0) return false;
                start = at;
                end = at + needle.length;
              }
              return reveal(rangeFromOffsets(
                Math.max(0, Math.min(start, text.length)),
                Math.max(0, Math.min(end, text.length))));
            })();
            """;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await host.InvokeScriptAsync(script);
                if (string.Equals(result?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                {
                    if (_readerLayout.FlowMode == 1)
                        await host.InvokeScriptAsync(ReaderPaginationScripts.Snap);
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // The hidden/native host may still be settling its document.
            }
            if (attempt < 3)
                await Task.Delay(100, cancellationToken);
        }
    }

    private async Task ApplySavedAnnotationsAsync(
        IReaderHost host,
        CancellationToken cancellationToken)
    {
        if (_readerBookFile is null || _readerDocument is null) return;
        var chapterPath = GetReaderChapterPath();
        if (chapterPath is null) return;
        var annotations = await _readerData.GetAnnotationsAsync(_readerBookFile.Id, cancellationToken);
        var marks = annotations
            .Where(item => string.Equals(item.ChapterPath, chapterPath, StringComparison.OrdinalIgnoreCase))
            .Where(item => !string.IsNullOrWhiteSpace(item.SelectedText))
            .Select(item => new
            {
                Id = item.Id.ToString("N"),
                Quote = item.SelectedText.Trim(),
                Prefix = item.Prefix,
                Suffix = item.Suffix,
                Color = NormalizeReaderAnnotationColor(item.Color),
                Style = item.UnderlineStyle
            })
            .Take(80)
            .ToArray();

        var serialized = JsonSerializer.Serialize(marks);
        var script = $$"""
            (() => {
              const annotations = {{serialized}};
              const commonSuffixLength = (left, right) => {
                let length = 0;
                const max = Math.min(left.length, right.length);
                while (length < max && left[left.length - 1 - length] === right[right.length - 1 - length]) length++;
                return length;
              };
              const commonPrefixLength = (left, right) => {
                let length = 0;
                const max = Math.min(left.length, right.length);
                while (length < max && left[length] === right[length]) length++;
                return length;
              };
              const unwrap = mark => {
                const parent = mark.parentNode;
                if (!parent) return;
                while (mark.firstChild) parent.insertBefore(mark.firstChild, mark);
                parent.removeChild(mark);
                parent.normalize?.();
              };
              for (const oldMark of Array.from(document.querySelectorAll('mark.kkindle-saved-annotation'))) {
                unwrap(oldMark);
              }
              const ignored = node => {
                const parent = node?.parentElement;
                return !parent
                  || ['SCRIPT', 'STYLE', 'NOSCRIPT'].includes(parent.tagName)
                  || !!parent.closest?.('#kkindle-selection-bar, .kkindle-wave-sweep, mark.kkindle-saved-annotation');
              };
              const collectNodes = () => {
                const nodes = [];
                const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
                while (walker.nextNode()) {
                  if (!ignored(walker.currentNode)) nodes.push(walker.currentNode);
                }
                return nodes;
              };
              const segmentsFor = (nodes, start, end) => {
                const segments = [];
                let cursor = 0;
                for (const node of nodes) {
                  const length = (node.data || '').length;
                  const nodeStart = cursor;
                  const nodeEnd = cursor + length;
                  const from = Math.max(start, nodeStart);
                  const to = Math.min(end, nodeEnd);
                  if (to > from) {
                    segments.push({
                      node,
                      start: from - nodeStart,
                      end: to - nodeStart
                    });
                  }
                  cursor = nodeEnd;
                  if (cursor >= end) break;
                }
                return segments;
              };
              const styleMark = (mark, annotation) => {
                mark.className = 'kkindle-saved-annotation';
                if (annotation.Id) mark.setAttribute('data-kkindle-annotation', annotation.Id);
                const color = /^#[0-9a-f]{6}$/i.test(annotation.Color || '') ? annotation.Color : '#E6E6E6';
                if ((annotation.Style || 'solid') === 'marker') {
                  mark.style.setProperty('background', color, 'important');
                } else {
                  mark.style.setProperty('background', 'transparent', 'important');
                  mark.style.setProperty('text-decoration', `underline 2px ${color} ${annotation.Style || 'solid'}`, 'important');
                }
                mark.style.setProperty('color', '#000000', 'important');
              };
              const wrapSegments = (segments, annotation) => {
                for (let index = segments.length - 1; index >= 0; index--) {
                  const segment = segments[index];
                  if (!segment.node.parentNode) return false;
                  const range = document.createRange();
                  range.setStart(segment.node, Math.min(segment.start, segment.node.data.length));
                  range.setEnd(segment.node, Math.min(segment.end, segment.node.data.length));
                  const mark = document.createElement('mark');
                  styleMark(mark, annotation);
                  try {
                    range.surroundContents(mark);
                  } catch (_) {
                    return false;
                  }
                }
                return true;
              };
              for (const annotation of annotations) {
                const quote = annotation.Quote || '';
                if (!quote) continue;
                // Search the logical text stream, not individual text nodes.
                // EPUB markup commonly splits one visible sentence across
                // spans, emphasis tags, and inline links.
                const nodes = collectNodes();
                const text = nodes.map(node => node.data || '').join('');
                const foldedText = text.toLocaleLowerCase();
                const foldedQuote = quote.toLocaleLowerCase();
                const prefix = (annotation.Prefix || '').slice(-72).toLocaleLowerCase();
                const suffix = (annotation.Suffix || '').slice(0, 72).toLocaleLowerCase();
                let bestAt = -1;
                let bestScore = -1;
                let at = foldedText.indexOf(foldedQuote);
                while (at >= 0) {
                  const before = text.slice(Math.max(0, at - 72), at).toLocaleLowerCase();
                  const after = text.slice(at + quote.length, at + quote.length + 72).toLocaleLowerCase();
                  const score = commonSuffixLength(before, prefix) + commonPrefixLength(after, suffix);
                  if (score > bestScore) {
                    bestScore = score;
                    bestAt = at;
                  }
                  at = foldedText.indexOf(foldedQuote, at + Math.max(1, foldedQuote.length));
                }
                if (bestAt < 0) continue;
                wrapSegments(segmentsFor(nodes, bestAt, bestAt + quote.length), annotation);
              }
              return true;
            })();
            """;
        await host.InvokeScriptAsync(script);
    }

    private async Task ApplyReaderSearchAsync(string query, int sequence, bool navigate = true)
    {
        if (_readerDocument is null || CurrentReaderHost is not { } host) return;
        if (sequence != _readerSearchSequence) return;
        await _readerSearchMutationGate.WaitAsync(ReaderToken);
        try
        {
            if (sequence != _readerSearchSequence) return;
        var serializedQuery = JsonSerializer.Serialize(query);
        var script = $$"""
            (() => {
              const oldMarks = Array.from(document.querySelectorAll('mark.kkindle-page-find-hit'));
              const unwrap = mark => {
                const parent = mark.parentNode;
                if (!parent) return;
                while (mark.firstChild) parent.insertBefore(mark.firstChild, mark);
                parent.removeChild(mark);
                parent.normalize?.();
              };
              for (let index = oldMarks.length - 1; index >= 0; index--) {
                unwrap(oldMarks[index]);
              }
              const query = ({{serializedQuery}} || '').trim();
              if (!query || !document.body) return 0;
              const folded = query.toLocaleLowerCase();
              const ignored = node => {
                const parent = node?.parentElement;
                return !parent
                  || ['SCRIPT', 'STYLE', 'NOSCRIPT'].includes(parent.tagName)
                  || !!parent.closest?.('#kkindle-selection-bar, .kkindle-wave-sweep, mark.kkindle-page-find-hit');
              };
              const nodes = [];
              const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
              while (walker.nextNode()) {
                if (!ignored(walker.currentNode)) nodes.push(walker.currentNode);
              }
              const text = nodes.map(node => node.data || '').join('');
              const foldedText = text.toLocaleLowerCase();
              const matches = [];
              const markedMatches = new Set();
              let start = foldedText.indexOf(folded);
              while (start >= 0) {
                matches.push({ start, length: query.length });
                start = foldedText.indexOf(folded, start + Math.max(1, folded.length));
              }
              const segmentsFor = (start, end) => {
                const segments = [];
                let cursor = 0;
                for (const node of nodes) {
                  const length = (node.data || '').length;
                  const nodeStart = cursor;
                  const nodeEnd = cursor + length;
                  const from = Math.max(start, nodeStart);
                  const to = Math.min(end, nodeEnd);
                  if (to > from) {
                    segments.push({
                      node,
                      start: from - nodeStart,
                      end: to - nodeStart
                    });
                  }
                  cursor = nodeEnd;
                  if (cursor >= end) break;
                }
                return segments;
              };
              for (let index = matches.length - 1; index >= 0; index--) {
                const match = matches[index];
                const segments = segmentsFor(match.start, match.start + match.length);
                let didMark = false;
                for (let segmentIndex = segments.length - 1; segmentIndex >= 0; segmentIndex--) {
                  const segment = segments[segmentIndex];
                  if (!segment.node.parentNode) continue;
                  const range = document.createRange();
                  range.setStart(segment.node, Math.min(segment.start, segment.node.data.length));
                  range.setEnd(segment.node, Math.min(segment.end, segment.node.data.length));
                  const mark = document.createElement('mark');
                  mark.className = 'kkindle-page-find-hit';
                  mark.setAttribute('data-kkindle-page-hit', String(index));
                  mark.style.setProperty('background', '#D8D8D8', 'important');
                  mark.style.setProperty('color', '#000000', 'important');
                  mark.style.setProperty('text-decoration', 'none', 'important');
                  try {
                    range.surroundContents(mark);
                    didMark = true;
                  } catch (_) {
                    // A malformed EPUB node should not abort the remaining hits.
                  }
                }
                if (didMark) markedMatches.add(index);
              }
              return markedMatches.size;
            })();
            """;
        string? result;
        try
        {
            result = await host.InvokeScriptAsync(script);
        }
        catch
        {
            if (sequence != _readerSearchSequence) return;
            _readerSearchCount = 0;
            _readerSearchIndex = -1;
            UpdateReaderSearchCount();
            return;
        }
        if (sequence != _readerSearchSequence) return;
        _readerSearchCount = ParseScriptInt(result);
        _readerSearchIndex = _readerSearchCount > 0
            ? navigate
                ? 0
                : Math.Clamp(_readerSearchIndex, 0, _readerSearchCount - 1)
            : -1;
        if (navigate)
            await NavigateReaderSearchAsync(_readerSearchIndex, sequence);
        else
            UpdateReaderSearchCount();
        }
        finally
        {
            _readerSearchMutationGate.Release();
        }
    }

    private async Task NavigateReaderSearchAsync(int index, int? sequence = null)
    {
        if (_readerSearchCount <= 0 || CurrentReaderHost is not { } host)
        {
            UpdateReaderSearchCount();
            return;
        }
        if (sequence is not null && sequence.Value != _readerSearchSequence) return;
        _readerSearchIndex = (index % _readerSearchCount + _readerSearchCount) % _readerSearchCount;
        var pagination = _readerLayout.FlowMode == 1 ? "true" : "false";
        var script = $$"""
            (() => {
              const groups = [];
              const byKey = new Map();
              for (const mark of Array.from(document.querySelectorAll('mark.kkindle-page-find-hit'))) {
                const key = mark.getAttribute('data-kkindle-page-hit') || `single-${groups.length}`;
                let group = byKey.get(key);
                if (!group) {
                  group = [];
                  byKey.set(key, group);
                  groups.push(group);
                }
                group.push(mark);
              }
              for (let i = 0; i < groups.length; i++) {
                const current = i === {{_readerSearchIndex}};
                for (const mark of groups[i]) {
                  mark.style.setProperty('background', current ? '#000000' : '#D8D8D8', 'important');
                  mark.style.setProperty('color', current ? '#FFFFFF' : '#000000', 'important');
                }
              }
              const mark = groups[{{_readerSearchIndex}}]?.[0];
              if (!mark) return false;
              if ({{pagination}}) {
                const scroller = document.scrollingElement || document.documentElement;
                mark.scrollIntoView({ block: 'nearest', inline: 'center', behavior: 'instant' });
                const step = {{ReaderPaginationScripts.PageStepExpression}};
                if (step > 0) {
                  const rawMax = Math.max(0, scroller.scrollWidth - scroller.clientWidth);
                  const trailing = parseFloat(getComputedStyle(document.body).paddingRight) || 0;
                  const max = Math.max(0, Math.min(rawMax, Math.round(Math.max(0, rawMax - trailing) / step) * step));
                  window.scrollTo({ left: Math.max(0, Math.min(max, Math.round(scroller.scrollLeft / step) * step)), top: 0, behavior: 'instant' });
                }
              } else {
                mark.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'smooth' });
              }
              return true;
            })();
            """;
        try
        {
            await host.InvokeScriptAsync(script);
        }
        catch
        {
            // Search navigation is best-effort when a chapter is being
            // replaced by the other reader host.
        }
        UpdateReaderSearchCount();
    }

    private async Task ClearReaderSearchAsync()
    {
        _readerSearchSequence++;
        _readerPdfSearchSequence++;
        _readerSearchCount = 0;
        _readerSearchIndex = -1;
        await _readerSearchMutationGate.WaitAsync(ReaderToken);
        try
        {
        if (CurrentReaderHost is { } host)
        {
            try
            {
                await host.InvokeScriptAsync("""
                    (() => {
                      const unwrap = mark => {
                        const parent = mark.parentNode;
                        if (!parent) return;
                        while (mark.firstChild) parent.insertBefore(mark.firstChild, mark);
                        parent.removeChild(mark);
                        parent.normalize?.();
                      };
                      for (const mark of Array.from(document.querySelectorAll('mark.kkindle-page-find-hit, mark.kkindle-search-hit'))) {
                        unwrap(mark);
                      }
                    })();
                    """);
            }
            catch
            {
                // PDF's built-in viewer and a just-swapped WebView do not
                // expose a scriptable DOM; clearing search is still complete.
            }
        }
        UpdateReaderSearchCount();
        }
        finally
        {
            _readerSearchMutationGate.Release();
        }
    }

    private async Task SaveReaderLayoutAsync(CancellationToken cancellationToken)
    {
        if (_readerBookCard is null || _readerBookFile is null) return;
        try
        {
            await _readerData.SaveLayoutSettingsAsync(
                _readerBookCard.Book.Id,
                _readerBookFile.Id,
                ReaderLayoutDefaults.Normalize(_readerLayout),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    // ------------------------------------------------------------------
    // Reading stats: cumulative active reading time plus a progress
    // snapshot. Time only accrues while the window is active and the
    // reader pane is visible, so simply leaving the book open is not
    // counted as reading time. Mirrors the WinUI reference.
    // ------------------------------------------------------------------

    private void StartReaderStatsTimer()
    {
        if (_readerStatsTimer is null)
        {
            _readerStatsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _readerStatsTimer.Tick += ReaderStatsTimer_Tick;
        }
        _readerStatsTimer.Start();
    }

    private void StopReaderStatsTimer()
    {
        _readerStatsTimer?.Stop();
    }

    private async Task LoadReaderStatsBaseAsync()
    {
        if (_readerBookFile is null) return;
        try
        {
            var stats = await _readerData.GetReadingStatsAsync(
                _readerBookFile.Id,
                _readerSessionCancellation?.Token ?? CancellationToken.None);
            _readerStatsBaseSeconds = stats?.CumulativeSeconds ?? 0;
            UpdateReaderStatsDisplay();
        }
        catch
        {
        }
    }

    private void ReaderStatsTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsActive || !ReaderRoot.IsVisible) return;
        _readerActiveSeconds++;
        _readerSessionSeconds++;
        if (_readerActiveSeconds % 30 == 0)
            _ = FlushReaderActiveSecondsAsync();
        UpdateReaderStatsDisplay();
    }

    private async Task FlushReaderActiveSecondsAsync()
    {
        await _readerStatsFlushGate.WaitAsync();
        try
        {
            if (_readerBookCard is null || _readerBookFile is null || _readerActiveSeconds <= 0) return;
            var activeSeconds = Interlocked.Exchange(ref _readerActiveSeconds, 0);
            if (activeSeconds <= 0) return;
            try
            {
                await _readerData.AddReadingTimeAsync(
                    _readerBookCard.Book.Id,
                    _readerBookFile.Id,
                    activeSeconds,
                    CalculateReaderProgressPercent(),
                    _readerChapterIndex,
                    _readerDocument?.Chapters.Count ?? (_readerIsPdf ? _readerPdfPages.Count : 0),
                    CancellationToken.None);
            }
            catch
            {
                // Keep unsaved seconds pending so the next periodic flush or
                // reader close can retry instead of silently losing time.
                Interlocked.Add(ref _readerActiveSeconds, activeSeconds);
            }
        }
        finally
        {
            _readerStatsFlushGate.Release();
        }
    }

    private void UpdateReaderStatsDisplay()
    {
        if (ReaderStatsText is null) return;
        var cumulative = _readerStatsBaseSeconds + _readerSessionSeconds;
        ReaderStatsText.Text = $"累计阅读 {FormatReaderDuration(cumulative)} · 本次 {FormatReaderDuration(_readerSessionSeconds)}";
    }

    private static string FormatReaderDuration(long seconds)
    {
        if (seconds < 60) return $"{seconds} 秒";
        if (seconds < 3600) return $"{seconds / 60} 分钟";
        return $"{seconds / 3600.0:0.0} 小时";
    }

    private void UpdateReaderZoomLabel()
    {
        if (ReaderZoomText is not null)
            ReaderZoomText.Text = $"{_readerLayout.FontScale:P0}";
    }

    // Transient reader-header status: auto-clears after a short moment instead
    // of lingering forever. A sequence guard plus an exact-text check ensure an
    // older timer never wipes a newer or longer-lived message.
    private void ShowReaderTransientStatus(string message)
    {
        ReaderStatusText.Text = message;
        var sequence = ++_readerTransientStatusSequence;
        _ = Task.Delay(2500).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (sequence == _readerTransientStatusSequence
                    && string.Equals(ReaderStatusText.Text, message, StringComparison.Ordinal))
                {
                    ReaderStatusText.Text = string.Empty;
                }
            }),
            TaskScheduler.Default);
    }

    private void HandleReaderBridgeShortcut(string key, bool ctrlKey)
    {
        if (string.Equals(key, "escape", StringComparison.OrdinalIgnoreCase))
        {
            HandleReaderEscapeShortcut();
            return;
        }
        if (string.Equals(key, "f11", StringComparison.OrdinalIgnoreCase))
        {
            ToggleReaderZenMode();
            return;
        }
        if (ctrlKey && string.Equals(key, "f", StringComparison.OrdinalIgnoreCase))
        {
            OpenReaderSearchShortcut();
            return;
        }
        if (ctrlKey
            && string.Equals(key, "b", StringComparison.OrdinalIgnoreCase)
            && !IsReaderTextInputFocused())
        {
            _ = ObserveReaderTaskAsync(ToggleReaderBookmarkAsync());
        }
    }

    private async Task HandleReaderLinkAsync(string href, bool showFootnote = false)
    {
        _readerFootnoteHoverSequence++;
        _readerFootnotePinned = false;
        if (_readerDocument is null || !Uri.TryCreate(href, UriKind.Absolute, out var uri) || !uri.IsFile) return;
        var path = Path.GetFullPath(uri.LocalPath);
        if (!IsPathInside(_readerDocument.RootPath, path)) return;

        if (showFootnote && !string.IsNullOrWhiteSpace(uri.Fragment))
        {
            var targets = await _footnotes.ResolveAsync(
                _readerDocument.RootPath,
                [uri.AbsoluteUri],
                ReaderToken);
            if (targets.TryGetValue(EpubFootnoteResolver.NormalizeTargetKey(uri.AbsoluteUri), out var footnote))
            {
                ReaderFootnoteText.Text = footnote;
                _readerFootnotePinned = true;
                ReaderFootnotePopup.IsVisible = true;
                return;
            }
        }

        var match = _readerDocument.Chapters
            .Select((chapter, index) => (chapter, index))
            .FirstOrDefault(item => string.Equals(Path.GetFullPath(item.chapter), path, StringComparison.OrdinalIgnoreCase));
        if (match.chapter is null) return;
        var chapterIndex = match.index;
        var item = new EpubReaderNavigationItem(
            $"第 {chapterIndex + 1} 章",
            uri.AbsoluteUri,
            chapterIndex);
        ReaderFootnotePopup.IsVisible = false;
        await NavigateToReaderItemAsync(
            item,
            _readerSessionCancellation?.Token ?? CancellationToken.None,
            ReaderNavigationIntent.Link);
    }

    private async Task HandleReaderFootnoteHoverAsync(string href, bool isFootnote)
    {
        if (!isFootnote
            || _readerDocument is null
            || !Uri.TryCreate(href, UriKind.Absolute, out var uri)
            || !uri.IsFile
            || string.IsNullOrWhiteSpace(uri.Fragment))
            return;
        var sequence = ++_readerFootnoteHoverSequence;
        _readerFootnotePinned = false;
        var path = Path.GetFullPath(uri.LocalPath);
        if (!IsPathInside(_readerDocument.RootPath, path)) return;

        var targets = await _footnotes.ResolveAsync(
            _readerDocument.RootPath,
            [uri.AbsoluteUri],
            ReaderToken);
        if (sequence == _readerFootnoteHoverSequence
            && targets.TryGetValue(EpubFootnoteResolver.NormalizeTargetKey(uri.AbsoluteUri), out var footnote))
        {
            ReaderFootnoteText.Text = footnote;
            ReaderFootnotePopup.IsVisible = true;
        }
    }

    private void HandleReaderBridgeMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)) return;
            switch (typeElement.GetString())
            {
                case "ready":
                    ReaderStatusText.Text = _readerIsPdf
                        ? $"PDF · {_readerPdfPages.Count} 页"
                        : $"共 {_readerDocument?.Chapters.Count ?? 0} 个章节";
                    break;
                case "pdfPage":
                    if (_readerIsPdf && root.TryGetProperty("page", out var pdfPage)
                        && pdfPage.TryGetInt32(out var page))
                    {
                        _readerPdfPage = Math.Clamp(page, 1, Math.Max(1, _readerPdfPages.Count));
                        _readerChapterIndex = _readerPdfPage - 1;
                        ReaderChapterText.Text = GetReaderChapterLabel();
                        UpdateReaderToolbar();
                    }
                    goto case "scroll";
                case "scroll":
                    var horizontalScroll = _readerLayout.FlowMode == 1 || _readerLayout.VerticalWriting;
                    if (!_readerIsPdf)
                    {
                        var reportedFragment = ReadString(root, "fragment").TrimStart('#');
                        try { reportedFragment = Uri.UnescapeDataString(reportedFragment); } catch { }
                        _readerCurrentFragment = string.IsNullOrWhiteSpace(reportedFragment)
                            ? null
                            : reportedFragment;
                    }
                    _readerScrollPosition = horizontalScroll
                        ? ReadDouble(root, "left")
                        : ReadDouble(root, "top");
                    _readerScrollWidth = ReadDouble(root, "scrollWidth");
                    _readerScrollHeight = ReadDouble(root, "scrollHeight");
                    _readerClientWidth = ReadDouble(root, "clientWidth");
                    _readerClientHeight = ReadDouble(root, "clientHeight");
                    var max = horizontalScroll
                        ? Math.Max(0, _readerScrollWidth - _readerClientWidth)
                        : Math.Max(0, _readerScrollHeight - _readerClientHeight);
                    _readerScrollRatio = max > 0 ? Math.Clamp(_readerScrollPosition / max, 0, 1) : 0;
                    ReaderProgressPercentText.Text = $"{CalculateReaderProgressPercent():0}%";
                    _ = SaveReaderProgressAfterScrollAsync(++_readerProgressSaveSequence);
                    // The bridge already supplied the exact scroll position
                    // and fragment. Reusing that snapshot avoids issuing a
                    // second WebView script call for every animation frame.
                    UpdateReaderBookmarkIndicatorFromTrackedLocation();
                    TryAdvanceReaderScrollChapter();
                    break;
                case "selection":
                    _readerPendingSelection = root.TryGetProperty("text", out var selection)
                        ? selection.GetString()
                        : null;
                    _readerPendingSelectionStartOffset = ReadInt(root, "startOffset");
                    _readerPendingSelectionEndOffset = ReadInt(root, "endOffset");
                    _readerPendingSelectionPrefix = ReadString(root, "prefix");
                    _readerPendingSelectionSuffix = ReadString(root, "suffix");
                    if (!string.IsNullOrWhiteSpace(_readerPendingSelection))
                    {
                        ReaderStatusText.Text = "已选中文字，可点击“划线”保存";
                        ReaderAnnotationSelectionText.Text = _readerPendingSelection;
                        ReaderHighlightButton.IsVisible = true;
                        ReaderAnnotateButton.IsVisible = true;
                    }
                    else
                    {
                        _readerPendingSelection = null;
                        _readerPendingSelectionStartOffset = 0;
                        _readerPendingSelectionEndOffset = 0;
                        _readerPendingSelectionPrefix = string.Empty;
                        _readerPendingSelectionSuffix = string.Empty;
                        ReaderAnnotationSelectionText.Text = "请先在正文中选择一段文字，再点击顶部“批注”。";
                        ReaderHighlightButton.IsVisible = false;
                        ReaderAnnotateButton.IsVisible = false;
                    }
                    break;
                case "selectionAction":
                    DispatchReaderSelectionAction(root);
                    break;
                case "link":
                    if (root.TryGetProperty("href", out var href))
                    {
                        var showFootnote = root.TryGetProperty("footnote", out var footnote)
                            && footnote.ValueKind == JsonValueKind.True;
                        _ = ObserveReaderTaskAsync(
                            HandleReaderLinkAsync(href.GetString() ?? string.Empty, showFootnote));
                    }
                    break;
                case "page":
                    if ((_readerIsPdf || _readerLayout.FlowMode == 1)
                        && root.TryGetProperty("direction", out var pageDirection)
                        && pageDirection.TryGetInt32(out var pageTurnDirection))
                    {
                        pageTurnDirection = Math.Sign(pageTurnDirection);
                        if (pageTurnDirection != 0)
                            _ = ObserveReaderTaskAsync(TurnReaderPageAsync(pageTurnDirection));
                    }
                    break;
                case "footnoteHover":
                    if (root.TryGetProperty("href", out var footnoteHref))
                        _ = ObserveReaderTaskAsync(
                            HandleReaderFootnoteHoverAsync(footnoteHref.GetString() ?? string.Empty, true));
                    break;
                case "footnoteLeave":
                    _readerFootnoteHoverSequence++;
                    if (!_readerFootnotePinned)
                        ReaderFootnotePopup.IsVisible = false;
                    break;
                case "resize":
                    _ = ObserveReaderTaskAsync(
                        ApplyReaderLayoutToHostsAsync(_readerSessionCancellation?.Token ?? CancellationToken.None));
                    break;
                case "wheel":
                    // Paginated EPUB pages and the PDF text view translate the
                    // vertical wheel into page turns, mirroring the WinUI
                    // reference's low-level mouse hook (120 units per page,
                    // direction flips reset the remainder, and the browser is
                    // told to ignore the event so it never double-scrolls).
                    if ((_readerIsPdf || _readerLayout.FlowMode == 1)
                        && root.TryGetProperty("deltaY", out var wheel))
                    {
                        var delta = (int)Math.Round(wheel.GetDouble());
                        if (delta != 0)
                        {
                            if (_readerWheelDeltaRemainder != 0
                                && Math.Sign(_readerWheelDeltaRemainder) != Math.Sign(delta))
                            {
                                _readerWheelDeltaRemainder = 0;
                            }
                            _readerWheelDeltaRemainder += delta;
                            if (Math.Abs(_readerWheelDeltaRemainder) >= 120)
                            {
                                var direction = _readerWheelDeltaRemainder > 0 ? 1 : -1;
                                _readerWheelDeltaRemainder %= 120;
                                _ = ObserveReaderTaskAsync(TurnReaderPageAsync(direction));
                            }
                        }
                    }
                    break;
                case "pointermove":
                    // Zen chrome wake-up for pointer movement over the webview
                    // (an HWND island whose events never reach Avalonia). The
                    // page throttles the reports; keep a host-side tick guard
                    // so a burst of messages cannot restart the timer faster
                    // than the 80 ms window.
                    if (_readerZenMode)
                    {
                        var moveNow = Environment.TickCount64;
                        if (moveNow - _readerZenLastMouseMoveTick > 80)
                        {
                            _readerZenLastMouseMoveTick = moveNow;
                            if (!_readerZenChromeVisible)
                                UpdateReaderZenChrome(visible: true);
                            else
                                RestartReaderZenChromeHideTimer();
                        }
                    }
                    break;
                case "key":
                    if (root.TryGetProperty("key", out var key))
                    {
                        var keyName = key.GetString();
                        if (_readerIsPdf || _readerLayout.FlowMode == 1)
                        {
                            // Paginated/PDF: arrows and paging keys turn pages.
                            var direction = string.Equals(keyName, "ArrowLeft", StringComparison.Ordinal)
                                || string.Equals(keyName, "PageUp", StringComparison.Ordinal)
                                ? -1
                                : string.Equals(keyName, "ArrowRight", StringComparison.Ordinal)
                                    || string.Equals(keyName, "PageDown", StringComparison.Ordinal)
                                    ? 1
                                    : 0;
                            if (direction != 0)
                                _ = ObserveReaderTaskAsync(TurnReaderPageAsync(direction));
                        }
                        else if (_readerLayout.FlowMode == 0 && _readerDocument is not null)
                        {
                            // Continuous mode (WinUI reference): left/right own
                            // chapter navigation; up/down scroll smoothly and
                            // stop at chapter edges instead of advancing.
                            var chapterDirection = string.Equals(keyName, "ArrowLeft", StringComparison.Ordinal)
                                ? -1
                                : string.Equals(keyName, "ArrowRight", StringComparison.Ordinal)
                                    ? 1
                                    : 0;
                            if (chapterDirection != 0)
                            {
                                _ = ObserveReaderTaskAsync(MoveReaderChapterAsync(chapterDirection));
                            }
                            else
                            {
                                var scrollDirection = string.Equals(keyName, "ArrowUp", StringComparison.Ordinal)
                                    ? -1
                                    : string.Equals(keyName, "ArrowDown", StringComparison.Ordinal)
                                        ? 1
                                        : 0;
                                if (scrollDirection != 0)
                                    _ = ObserveReaderTaskAsync(ScrollReaderWithKeyboardAsync(scrollDirection));
                            }
                        }
                    }
                    break;
                case "shortcut":
                    HandleReaderBridgeShortcut(
                        ReadString(root, "key"),
                        root.TryGetProperty("ctrlKey", out var ctrlKey)
                            && ctrlKey.ValueKind == JsonValueKind.True);
                    break;
            }
        }
        catch (JsonException)
        {
        }
    }

    // The selection bar now lives inside the reader page (the webview is a
    // native HWND island Avalonia cannot paint over), so its buttons arrive as
    // bridge messages instead of XAML clicks. Each action mirrors the WinUI
    // reference's selection-bar handlers.
    private void DispatchReaderSelectionAction(JsonElement root)
    {
        if (!root.TryGetProperty("action", out var actionElement)) return;
        switch (actionElement.GetString())
        {
            case "copy":
                _ = ObserveReaderTaskAsync(PerformReaderSelectionCopyAsync());
                break;
            case "highlight":
                if (root.TryGetProperty("style", out var styleElement)
                    && styleElement.GetString() is { } style)
                {
                    _ = ObserveReaderTaskAsync(ApplyReaderHighlightStyleAsync(style));
                }
                break;
            case "annotate":
                ShowReaderNotesTab();
                ReaderAnnotationNoteBox.Focus();
                break;
            case "ai":
                if (!string.IsNullOrWhiteSpace(_readerPendingSelection))
                {
                    ShowReaderAiTab();
                    _ = ObserveReaderTaskAsync(SendReaderAiQuestionAsync(
                        $"请解释下面这段文字的含义、上下文和隐含前提，并给出一个简单例子：\n\n{_readerPendingSelection}"));
                }
                break;
            case "search":
                if (string.IsNullOrWhiteSpace(_readerPendingSelection)) break;
                _readerTocMinimal = false;
                _readerTocExpanded = true;
                ApplyReaderPanelLayout();
                ShowReaderSearchTab();
                ReaderTocSearchBox.Text = _readerPendingSelection;
                ReaderTocSearchBox.Focus();
                break;
            case "dictionary":
                _ = ObserveReaderTaskAsync(PerformReaderSelectionDictionaryAsync());
                break;
        }
    }

    // Continuous-mode short-chapter skip (WinUI reference's
    // SkipShortChapterIfNeededAsync): shortly after entering a chapter, if it
    // cannot scroll at all (content fits the viewport), advance to the next
    // one so the reader never stops on an empty page. Depth-capped so a chain
    // of empty chapters cannot loop forever.
    private async Task SkipShortReaderChapterIfNeededAsync(
        int enteredIndex,
        CancellationToken cancellationToken)
    {
        if (_readerDocument is null || CurrentReaderHost is not { } host) return;
        try
        {
            await Task.Delay(60, cancellationToken);
            var result = await host.InvokeScriptAsync(
                "(() => { const el = document.scrollingElement || document.documentElement; if (!el) return '{}'; return JSON.stringify({ sh: el.scrollHeight, ch: el.clientHeight, sw: el.scrollWidth, cw: el.clientWidth }); })();");
            if (result is null) return;
            var raw = result.Trim().Trim('"');
            if (raw.Length == 0 || raw == "{}") return;
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var horizontal = _readerLayout.VerticalWriting;
            var scrollSize = horizontal
                ? ReadDouble(root, "sw")
                : ReadDouble(root, "sh");
            var clientSize = horizontal
                ? ReadDouble(root, "cw")
                : ReadDouble(root, "ch");
            if (scrollSize <= 0 || clientSize <= 0) return;
            if (scrollSize > clientSize + 16) return;
            if (_readerChapterIndex != enteredIndex) return;
            if (_readerChapterIndex + 1 >= _readerDocument.Chapters.Count) return;
            if (_readerContinuousSkipDepth >= 5) return;
            _readerContinuousSkipDepth++;
            await MoveReaderChapterAsync(1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    private static async Task ObserveReaderTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // DOM events can race the host being replaced or disposed. A stale
            // event must never become an unobserved UI exception.
        }
    }

    private async Task SaveReaderProgressAfterScrollAsync(int sequence)
    {
        var token = _readerSessionCancellation?.Token ?? CancellationToken.None;
        try
        {
            await Task.Delay(700, token);
            if (sequence != _readerProgressSaveSequence || (_readerDocument is null && !_readerIsPdf)) return;
            await SaveReaderProgressAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    // ------------------------------------------------------------------
    // Scroll-edge chapter advance (滚动接章). In scroll mode the reader
    // continuously advances to the next chapter when the scroll position
    // reaches the bottom edge and steps back at the top edge, with a lock
    // that prevents one chapter from triggering repeated transitions.
    // Mirrors the WinUI reference's PollReaderScrollAsync.
    // ------------------------------------------------------------------

    private void ResetReaderContinuousEdgeTracking()
    {
        _readerContinuousPositionInitialized = false;
        _readerPreviousScrollPosition = 0;
        _readerLastNearTop = false;
        _readerLastNearBottom = false;
    }

    private void PrimeReaderContinuousEdgeTracking()
    {
        if (_readerIsPdf || _readerLayout.FlowMode != 0)
        {
            ResetReaderContinuousEdgeTracking();
            return;
        }

        var vertical = _readerLayout.VerticalWriting;
        var scrollSize = vertical ? _readerScrollWidth : _readerScrollHeight;
        var clientSize = vertical ? _readerClientWidth : _readerClientHeight;
        _readerPreviousScrollPosition = _readerScrollPosition;
        _readerContinuousPositionInitialized = true;
        _readerLastNearTop = _readerScrollPosition <= 48;
        _readerLastNearBottom = scrollSize > 0
            && clientSize > 0
            && _readerScrollPosition + clientSize >= scrollSize - 48;
    }

    private void TryAdvanceReaderScrollChapter()
    {
        if (_readerIsPdf || _readerScrollPollRunning) return;
        if (_readerLayout.FlowMode != 0) return;
        if (_readerDocument is null || _readerDocument.Chapters.Count <= 1) return;
        if (_readerChapterIndex < 0 || _readerChapterIndex >= _readerDocument.Chapters.Count) return;
        if (_readerZenMode) return;

        var vertical = _readerLayout.VerticalWriting;
        var scrollSize = vertical ? _readerScrollWidth : _readerScrollHeight;
        var clientSize = vertical ? _readerClientWidth : _readerClientHeight;
        var scrollPosition = _readerScrollPosition;
        if (scrollSize <= 0 || clientSize <= 0) return;

        var nearTop = scrollPosition <= 48;
        var nearBottom = scrollPosition + clientSize >= scrollSize - 48;
        var overflows = scrollSize > clientSize + 16;
        if (!_readerContinuousPositionInitialized)
        {
            _readerContinuousPositionInitialized = true;
            _readerPreviousScrollPosition = scrollPosition;
            _readerLastNearTop = nearTop;
            _readerLastNearBottom = nearBottom;
            return;
        }

        var movement = scrollPosition - _readerPreviousScrollPosition;
        _readerPreviousScrollPosition = scrollPosition;

        if (!nearTop && !nearBottom)
        {
            // Scrolled into the middle: release the continuous lock so the
            // next edge transition is treated as a fresh user action.
            _readerContinuousLocked = false;
            _readerLastNearTop = nearTop;
            _readerLastNearBottom = nearBottom;
            return;
        }

        if (_readerContinuousLocked)
        {
            var forceForward = _readerContinuousDirection > 0
                && overflows
                && nearBottom
                && DateTimeOffset.UtcNow - _readerLastChapterChange > TimeSpan.FromMilliseconds(500);
            var forceBackward = _readerContinuousDirection < 0
                && overflows
                && nearTop
                && DateTimeOffset.UtcNow - _readerLastChapterChange > TimeSpan.FromMilliseconds(500);
            if (forceForward || forceBackward)
                _readerContinuousLocked = false;
            else
                return;
        }

        if (nearBottom && !_readerLastNearBottom && movement > 0.5)
        {
            if (_readerChapterIndex + 1 < _readerDocument.Chapters.Count)
            {
                _readerContinuousLocked = true;
                _readerContinuousDirection = 1;
                _readerLastChapterChange = DateTimeOffset.UtcNow;
                _readerScrollPollRunning = true;
                _ = ObserveReaderTaskAsync(MoveReaderChapterFromScrollAsync(1));
            }
        }
        else if (nearTop && !_readerLastNearTop && movement < -0.5)
        {
            if (_readerChapterIndex > 0)
            {
                _readerContinuousLocked = true;
                _readerContinuousDirection = -1;
                _readerLastChapterChange = DateTimeOffset.UtcNow;
                _readerScrollPollRunning = true;
                _ = ObserveReaderTaskAsync(MoveReaderChapterFromScrollAsync(-1));
            }
        }
        _readerLastNearTop = nearTop;
        _readerLastNearBottom = nearBottom;
    }

    private async Task MoveReaderChapterFromScrollAsync(int direction)
    {
        try
        {
            await MoveReaderChapterAsync(direction);
        }
        finally
        {
            _readerScrollPollRunning = false;
        }
    }

    // Continuous-mode keyboard scroll: up/down move 72 px smoothly and stop at
    // chapter edges (left/right own chapter navigation), exactly like the
    // WinUI reference's ScrollReaderWithKeyboardAsync.
    private async Task ScrollReaderWithKeyboardAsync(int direction)
    {
        if (CurrentReaderHost is not { } host) return;
        try
        {
            await host.InvokeScriptAsync(
                CreateReaderKeyboardScrollScript(direction, _readerLayout.VerticalWriting));
        }
        catch
        {
            // A stale host must never surface a keyboard scroll failure.
        }
    }

    private static string CreateReaderKeyboardScrollScript(int direction, bool vertical) =>
        $$"""
        (() => {
          const el = document.scrollingElement || document.documentElement;
          if (!el) return false;
          const horizontal = {{(vertical ? "true" : "false")}};
          const position = horizontal ? el.scrollLeft : el.scrollTop;
          const viewport = horizontal ? el.clientWidth : el.clientHeight;
          const extent = horizontal ? el.scrollWidth : el.scrollHeight;
          const sign = {{(direction < 0 ? -1 : 1)}};
          if (sign < 0 && position <= 4) return false;
          if (sign > 0 && position + viewport >= extent - 4) return false;
          const delta = sign * 72;
          window.scrollBy(horizontal
            ? { left: delta, top: 0, behavior: 'smooth' }
            : { left: 0, top: delta, behavior: 'smooth' });
          return true;
        })();
        """;

    private async Task TurnReaderPageAsync(int direction)
    {
        if (_readerIsPdf)
        {
            await NavigatePdfPageAsync(_readerPdfPage + direction, ReaderToken);
            return;
        }
        if (CurrentReaderHost is not { } host) return;
        var result = await TurnReaderPageWithAnimationAsync(host, direction);
        if (string.Equals(result?.Trim(), "false", StringComparison.OrdinalIgnoreCase)
            && direction > 0 && _readerDocument is not null
            && _readerChapterIndex < _readerDocument.Chapters.Count - 1)
        {
            await MoveReaderChapterAsync(1);
        }
        else if (string.Equals(result?.Trim(), "false", StringComparison.OrdinalIgnoreCase)
            && direction < 0 && _readerChapterIndex > 0)
        {
            await MoveReaderChapterAsync(-1);
        }
    }

    private async Task<string?> TurnReaderPageWithAnimationAsync(
        IReaderHost host,
        int direction)
    {
        return await RunReaderContentTransitionAsync(
            host,
            host,
            direction,
            () => host.InvokeScriptAsync(ReaderPaginationScripts.CreateTurnScript(direction)),
            ReaderToken);
    }

    /// <summary>
    /// Runs the one transition pipeline used by both in-chapter page turns and
    /// chapter/TOC navigation. With two hosts, the outgoing document animates
    /// out and the prepared incoming document resumes from the same visual
    /// state after the host swap.
    /// </summary>
    private async Task<T> RunReaderContentTransitionAsync<T>(
        IReaderHost outgoingHost,
        IReaderHost incomingHost,
        int direction,
        Func<Task<T>> changeContentAsync,
        CancellationToken cancellationToken,
        bool animate = true)
    {
        var animation = animate ? _readerPageAnimation : ReaderAnimationNone;
        if (animation == ReaderAnimationNone)
            return await changeContentAsync();

        await TryInvokeReaderTransitionAsync(
            outgoingHost,
            CreateReaderTransitionScript(animation, direction, restore: false));

        try
        {
            await Task.Delay(GetReaderTransitionOutDuration(animation), cancellationToken);
            if (!ReferenceEquals(outgoingHost, incomingHost))
            {
                await TryInvokeReaderTransitionAsync(
                    incomingHost,
                    CreateReaderTransitionScript(animation, direction, restore: false));
            }

            var result = await changeContentAsync();
            try
            {
                await incomingHost.InvokeScriptAsync(
                    CreateReaderTransitionScript(animation, direction, restore: true));
                await Task.Delay(GetReaderTransitionInDuration(animation), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Content has already changed; a cosmetic reveal failure must
                // not turn a successful page/chapter navigation into an error.
            }
            return result;
        }
        finally
        {
            await TryInvokeReaderTransitionAsync(outgoingHost, CreateReaderTransitionCleanupScript());
            if (!ReferenceEquals(outgoingHost, incomingHost))
                await TryInvokeReaderTransitionAsync(incomingHost, CreateReaderTransitionCleanupScript());
        }
    }

    private static int GetReaderTransitionOutDuration(int animation) =>
        animation == ReaderAnimationWave ? 90 : 110;

    private static int GetReaderTransitionInDuration(int animation) =>
        animation == ReaderAnimationWave ? 320 : 190;

    private static async Task TryInvokeReaderTransitionAsync(IReaderHost host, string script)
    {
        try
        {
            await host.InvokeScriptAsync(script);
        }
        catch
        {
            // Animations are decorative and must never block navigation.
        }
    }

    private static string CreateReaderTransitionScript(int animation, int direction, bool restore)
    {
        var safeAnimation = Math.Clamp(animation, ReaderAnimationNone, ReaderAnimationWave);
        var offset = direction > 0 ? -18 : 18;
        return $$"""
            (() => {
              const root = document.documentElement;
              if (!root) return false;
              let style = document.getElementById('kkindle-reader-transition-style');
              if (!style) {
                style = document.createElement('style');
                style.id = 'kkindle-reader-transition-style';
                document.head.appendChild(style);
              }
              style.textContent = `
                @keyframes kkindle-reader-wave {
                  0% { filter: grayscale(1) contrast(1); }
                  35% { filter: grayscale(1) contrast(1.08); }
                  65% { filter: grayscale(1) contrast(.94); }
                  100% { filter: grayscale(1) contrast(1); }
                }
                @keyframes kkindle-reader-sweep {
                  0% { transform: translateX(-120%) skewX(-14deg); }
                  100% { transform: translateX(120%) skewX(-14deg); }
                }
                .kkindle-wave-sweep {
                  position: fixed; inset: 0; z-index: 2147483647; pointer-events: none;
                  background: linear-gradient(105deg, transparent 30%, rgba(0,0,0,.16) 50%, transparent 70%);
                  animation: kkindle-reader-sweep 380ms ease-in-out both;
                }
              `;
              root.style.transition = 'opacity 160ms ease, transform 180ms ease, filter 220ms ease';
              if ({{(restore ? "true" : "false")}}) {
                root.style.opacity = '1';
                root.style.transform = 'translateX(0)';
                root.style.filter = 'none';
                root.style.animation = 'none';
                const sweep = document.querySelector('.kkindle-wave-sweep');
                if (sweep) sweep.remove();
              } else {
                root.style.opacity = {{(safeAnimation == ReaderAnimationFade ? "'0.2'" : "'0.42'")}};
                root.style.transform = {{(safeAnimation == ReaderAnimationSlide ? $"'translateX({offset}px)'" : "'translateX(0)'")}};
                root.style.filter = {{(safeAnimation == ReaderAnimationWave ? "'grayscale(1) contrast(1.06)'" : "'none'")}};
                root.style.animation = {{(safeAnimation == ReaderAnimationWave ? "'kkindle-reader-wave 380ms ease both'" : "'none'")}};
                if ({{(safeAnimation == ReaderAnimationWave ? "true" : "false")}}) {
                  const sweep = document.createElement('div');
                  sweep.className = 'kkindle-wave-sweep';
                  document.body.appendChild(sweep);
                }
              }
              return true;
            })();
            """;
    }

    private static string CreateReaderTransitionCleanupScript() =>
        """
        (() => {
          const root = document.documentElement;
          if (!root) return false;
          root.style.removeProperty('transition');
          root.style.removeProperty('opacity');
          root.style.removeProperty('transform');
          root.style.removeProperty('filter');
          root.style.removeProperty('animation');
          document.querySelectorAll('.kkindle-wave-sweep').forEach(node => node.remove());
          return true;
        })();
        """;

    private void ReaderTocButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_readerTocMinimal)
        {
            _readerTocMinimal = false;
            _readerTocExpanded = true;
        }
        else
        {
            _readerTocExpanded = !_readerTocExpanded;
        }
        ApplyReaderPanelLayout();
        if (_readerTocExpanded)
        {
            ShowReaderTocTab();
            SetReaderTocSelectionForChapter(_readerChapterIndex);
        }
    }

    private void ReaderTocList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressReaderTocSelectionNavigation) return;
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is EpubReaderNavigationItem item)
            _ = ObserveReaderTaskAsync(
                NavigateToReaderItemAsync(
                    item,
                    _readerSessionCancellation?.Token ?? CancellationToken.None,
                    ReaderNavigationIntent.Toc));
    }

    private async void ReaderSearchButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (ReaderInPageSearchBar.IsVisible)
            {
                await ClearReaderSearchAsync();
                ReaderInPageSearchBar.IsVisible = false;
                ReaderInPageSearchBox.Text = string.Empty;
                return;
            }
            OpenReaderSearchShortcut();
        }
        catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested)
        {
        }
    }

    private async void ReaderFlowModeItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        if (_readerIsPdf)
        {
            ReaderStatusText.Text = "PDF 使用页面模式，可用底部进度条或左右按钮翻页。";
            return;
        }
        var flowMode = tag switch
        {
            "scroll" => 0,
            "double" => 1,
            _ => 1
        };
        var twoPage = string.Equals(tag, "double", StringComparison.Ordinal);
        _readerLayout = ReaderLayoutDefaults.Normalize(_readerLayout with
        {
            FlowMode = flowMode,
            TwoPageMode = twoPage
        });
        SyncReaderFlowMenu();
        ReaderFlowButton.Content = flowMode == 0 ? "滚动" : twoPage ? "双栏" : "单页";
        await ApplyReaderLayoutToHostsAsync(_readerSessionCancellation?.Token ?? CancellationToken.None);
        await SaveReaderLayoutAsync(CancellationToken.None);
        ReaderStatusText.Text = twoPage ? "已切换为双栏阅读。" : flowMode == 0 ? "已切换为滚动阅读。" : "已切换为单页阅读。";
    }

    private void SyncReaderFlowMenu()
    {
        if (ReaderScrollModeItem is null || ReaderSinglePageModeItem is null || ReaderTwoPageModeItem is null) return;
        var flowMode = _readerLayout.FlowMode;
        var twoPage = _readerLayout.TwoPageMode;
        ReaderScrollModeItem.IsChecked = flowMode == 0;
        ReaderSinglePageModeItem.IsChecked = flowMode == 1 && !twoPage;
        ReaderTwoPageModeItem.IsChecked = flowMode == 1 && twoPage;
    }

    private void ReaderZenMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        ToggleReaderZenMode();
    }

    private void ReaderLayoutSettingsMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        ReaderLayoutSettingsButton_Click(sender, e);
    }

    private void ReaderAnimationItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        _readerPageAnimation = tag switch
        {
            "none" => ReaderAnimationNone,
            "slide" => ReaderAnimationSlide,
            "wave" => ReaderAnimationWave,
            _ => ReaderAnimationFade
        };
        SyncReaderAnimationMenu();
    }

    private void SyncReaderAnimationMenu()
    {
        if (ReaderAnimationNoneItem is null
            || ReaderAnimationFadeItem is null
            || ReaderAnimationSlideItem is null
            || ReaderAnimationWaveItem is null)
        {
            return;
        }
        ReaderAnimationNoneItem.IsChecked = _readerPageAnimation == ReaderAnimationNone;
        ReaderAnimationFadeItem.IsChecked = _readerPageAnimation == ReaderAnimationFade;
        ReaderAnimationSlideItem.IsChecked = _readerPageAnimation == ReaderAnimationSlide;
        ReaderAnimationWaveItem.IsChecked = _readerPageAnimation == ReaderAnimationWave;
    }

    private async void ReaderDecreaseFontButton_Click(object? sender, RoutedEventArgs e) =>
        await ChangeReaderFontAsync(-0.1);

    private async void ReaderIncreaseFontButton_Click(object? sender, RoutedEventArgs e) =>
        await ChangeReaderFontAsync(0.1);

    private async Task ChangeReaderFontAsync(double delta)
    {
        _readerLayout = ReaderLayoutDefaults.Normalize(_readerLayout with { FontScale = _readerLayout.FontScale + delta });
        UpdateReaderZoomLabel();
        await ApplyReaderLayoutToHostsAsync(_readerSessionCancellation?.Token ?? CancellationToken.None);
        await SaveReaderLayoutAsync(CancellationToken.None);
    }

    private async void ReaderBookmarkButton_Click(object? sender, RoutedEventArgs e)
    {
        await ToggleReaderBookmarkAsync();
    }

    private void ReaderAnnotateButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowReaderNotesTab();
        ReaderAnnotationNoteBox.Focus();
    }

    private async void ReaderHighlightButton_Click(object? sender, RoutedEventArgs e)
    {
        await SaveReaderAnnotationAsync(string.Empty);
    }

    private async void ToggleReaderZenMode()
    {
        if (!_readerZenMode)
        {
            _readerWindowStateBeforeZen = WindowState;
            _readerAssistantVisibleBeforeZen = ReaderAssistantPanel.IsVisible;
            _readerTocExpandedBeforeZen = ReaderTocPanel.IsVisible;
            _readerTocMinimalBeforeZen = ReaderTocCompactPanel.IsVisible;
            WindowState = WindowState.FullScreen;
            _readerZenMode = true;
            ReaderAssistantPanel.IsVisible = false;
            ReaderRoot.ColumnDefinitions[2].Width = new GridLength(0);
            ReaderAssistantToggleButton.IsVisible = false;
            ReaderZenBar.IsVisible = true;
            if (ReaderZenMenuItem is not null) ReaderZenMenuItem.IsChecked = true;
            // Zen mode starts with the minimal TOC rail, matching the WinUI
            // reference: the full TOC panel is collapsed and only the 52-DIP
            // marker rail keeps the chapter map visible. The content header and
            // footer bars collapse so the body fills the whole reading area.
            _readerTocExpanded = false;
            _readerTocMinimal = true;
            ReaderContentPanel.RowDefinitions[0].Height = new GridLength(0);
            ReaderHeaderBar.IsVisible = false;
            ReaderContentPanel.RowDefinitions[2].Height = new GridLength(0);
            ReaderFooterBar.IsVisible = false;
            ReaderTocPanel.Margin = new Thickness(0);
            ReaderTocCompactPanel.Margin = new Thickness(0);
            ReaderContentPanel.Margin = new Thickness(0);
            ReaderAssistantPanel.Margin = new Thickness(0);
            ReaderWebViewBottomCover.Margin = new Thickness(0);
            ReaderWebViewHost.Margin = new Thickness(0, 12, 0, 0);
            ApplyReaderPanelLayout();
            UpdateReaderZenTocToggle();
            UpdateReaderZenChrome(visible: true);
        }
        else
        {
            await ExitReaderZenModeSmoothlyAsync();
        }
    }

    // Leaving zen restores the side panels, header/footer bars and the window
    // size in one go, which makes the paginated body text reflow and jump.
    // Mask that behind an opaque cover, restore everything, let the relayout
    // settle, then fade the cover away (WinUI reference behavior).
    private async Task ExitReaderZenModeSmoothlyAsync()
    {
        try
        {
            ReaderTransitionCover.Opacity = 1;
            ExitReaderZenModeCore();
            await Task.Delay(320);
            await FadeReaderTransitionCoverAsync(1, 0, 180);
        }
        catch
        {
            ReaderTransitionCover.Opacity = 0;
        }
    }

    private async Task FadeReaderTransitionCoverAsync(double from, double to, int durationMs)
    {
        try
        {
            var animation = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(durationMs),
                FillMode = Avalonia.Animation.FillMode.Forward
            };
            var frame = new KeyFrame { Cue = new Cue(1d) };
            frame.Setters.Add(new Avalonia.Styling.Setter(Border.OpacityProperty, to));
            animation.Children.Add(frame);
            ReaderTransitionCover.Opacity = from;
            await animation.RunAsync(ReaderTransitionCover);
        }
        catch
        {
        }
        ReaderTransitionCover.Opacity = to;
    }

    private void ExitReaderZenMode()
    {
        if (!_readerZenMode) return;
        ReaderTransitionCover.Opacity = 0;
        ExitReaderZenModeCore();
    }

    private void ExitReaderZenModeCore()
    {
        if (!_readerZenMode) return;
        WindowState = _readerWindowStateBeforeZen;
        _readerZenMode = false;
        ReaderTocPanel.IsVisible = false;
        ReaderAssistantPanel.IsVisible = _readerAssistantVisibleBeforeZen;
        ReaderRoot.ColumnDefinitions[2].Width = _readerAssistantVisibleBeforeZen
            ? new GridLength(360)
            : new GridLength(0);
        ReaderAssistantToggleButton.IsVisible = true;
        ReaderZenBar.IsVisible = false;
        if (ReaderZenMenuItem is not null) ReaderZenMenuItem.IsChecked = false;
        _readerTocExpanded = _readerTocExpandedBeforeZen;
        _readerTocMinimal = _readerTocMinimalBeforeZen;
        ReaderContentPanel.RowDefinitions[0].Height = new GridLength(52);
        ReaderHeaderBar.IsVisible = true;
        ReaderContentPanel.RowDefinitions[2].Height = new GridLength(50);
        ReaderFooterBar.IsVisible = true;
        ReaderTocPanel.Margin = new Thickness(0, 38, 0, 0);
        ReaderTocCompactPanel.Margin = new Thickness(0, 38, 0, 0);
        ReaderContentPanel.Margin = new Thickness(0, 38, 0, 0);
        ReaderAssistantPanel.Margin = new Thickness(0, 38, 0, 0);
        ReaderLayoutSettingsOverlay.Margin = new Thickness(0, 38, 0, 0);
        ReaderWebViewBottomCover.Margin = new Thickness(0, 0, 0, 10);
        ReaderWebViewHost.Margin = new Thickness(0, 12, 0, 10);
        ApplyReaderPanelLayout();
        UpdateReaderZenTocToggle();
        // Leaving zen restores the chrome unconditionally; the hide timer must
        // not keep running for the bookshelf.
        _readerZenChromeHideTimer?.Stop();
        _readerZenChromeVisible = true;
        ReaderZenTitleTocButton.IsVisible = false;
        ReaderZenTitleExitButton.IsVisible = false;
        MinimizeWindowButton.IsVisible = true;
        MaximizeWindowButton.IsVisible = true;
        CloseWindowButton.IsVisible = true;
        WindowBrandText.IsVisible = ReaderRoot.IsVisible;
    }

    // Zen mode auto-hides the top chrome (brand text, zen title buttons and the
    // window caption buttons) so only the body remains; the minimal TOC rail on
    // the left is not part of this chrome and stays visible. Mouse movement
    // reveals it again, and it hides after ~2.5 s of inactivity.
    private bool _readerZenChromeVisible = true;
    private DispatcherTimer? _readerZenChromeHideTimer;
    private long _readerZenLastMouseMoveTick;

    private void ReaderRoot_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_readerZenMode) return;
        var now = Environment.TickCount64;
        if (now - _readerZenLastMouseMoveTick <= 80) return;
        _readerZenLastMouseMoveTick = now;
        if (_readerZenMode && !_readerZenChromeVisible)
            UpdateReaderZenChrome(visible: true);
        else if (_readerZenMode)
            RestartReaderZenChromeHideTimer();
    }

    private void RestartReaderZenChromeHideTimer()
    {
        _readerZenChromeHideTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(2500)
        };
        _readerZenChromeHideTimer.Stop();
        _readerZenChromeHideTimer.Tick -= ReaderZenChromeHideTimer_Tick;
        _readerZenChromeHideTimer.Tick += ReaderZenChromeHideTimer_Tick;
        _readerZenChromeHideTimer.Start();
    }

    private void ReaderZenChromeHideTimer_Tick(object? sender, EventArgs e)
    {
        _readerZenChromeHideTimer?.Stop();
        if (_readerZenMode) UpdateReaderZenChrome(visible: false);
    }

    private void UpdateReaderZenChrome(bool visible)
    {
        _readerZenChromeVisible = visible;
        ReaderZenTitleTocButton.IsVisible = _readerZenMode && visible;
        ReaderZenTitleExitButton.IsVisible = _readerZenMode && visible;
        MinimizeWindowButton.IsVisible = visible;
        MaximizeWindowButton.IsVisible = visible;
        CloseWindowButton.IsVisible = visible;
        // The brand text floats over the minimal TOC rail in zen mode, so it
        // stays hidden there; it is restored when returning to the bookshelf.
        WindowBrandText.IsVisible = !_readerZenMode
            && visible
            && ReaderRoot.IsVisible;

        if (visible)
            RestartReaderZenChromeHideTimer();
        else
            _readerZenChromeHideTimer?.Stop();
    }

    private void UpdateReaderToolbar()
    {
        if (ReaderFlowButton is not null)
        {
            ReaderFlowButton.Content = _readerIsPdf
                ? "PDF 页"
                : _readerLayout.FlowMode == 0
                ? "滚动"
                : _readerLayout.TwoPageMode ? "双栏" : "单页";
            // The WinUI reference hides the flow selector entirely for PDF.
            ReaderFlowButton.IsVisible = !_readerIsPdf;
        }
        SyncReaderFlowMenu();
        SyncReaderAnimationMenu();
        if (ReaderZoomText is not null)
            ReaderZoomText.Text = $"{_readerLayout.FontScale:P0}";
        if (ReaderProgressPercentText is not null)
            ReaderProgressPercentText.Text = $"{CalculateReaderProgressPercent():0}%";
        if (ReaderReadingProgressText is not null)
            ReaderReadingProgressText.Text = _readerIsPdf
                ? $"已读 {_readerPdfPage} / {Math.Max(1, _readerPdfPages.Count)} 页"
                : $"已读 {Math.Clamp(_readerChapterIndex + 1, 0, _readerDocument?.Chapters.Count ?? 0)} / {_readerDocument?.Chapters.Count ?? 0} 章";
        if (ReaderProgressSlider is not null)
        {
            _readerProgressSliderUpdating = true;
            if (_readerIsPdf)
            {
                var pageCount = Math.Max(1, _readerPdfPages.Count);
                ReaderProgressSlider.Minimum = 1;
                ReaderProgressSlider.Maximum = pageCount;
                ReaderProgressSlider.Value = Math.Clamp(_readerPdfPage, 1, pageCount);
            }
            else if (_readerDocument is not null && _readerDocument.Chapters.Count > 0)
            {
                ReaderProgressSlider.Minimum = 1;
                ReaderProgressSlider.Maximum = _readerDocument.Chapters.Count;
                ReaderProgressSlider.Value = Math.Clamp(_readerChapterIndex + 1, 1, _readerDocument.Chapters.Count);
            }
            else
            {
                ReaderProgressSlider.Minimum = 1;
                ReaderProgressSlider.Maximum = 1;
                ReaderProgressSlider.Value = 1;
            }
            _readerProgressSliderUpdating = false;
        }
        // PDF hides the zoom controls and shows the PDF badge, matching the
        // WinUI reference toolbar states; chapter buttons disable at the edges.
        if (ReaderZoomOutButton is not null && ReaderZoomText is not null && ReaderZoomInButton is not null)
        {
            ReaderZoomOutButton.IsVisible = !_readerIsPdf;
            ReaderZoomText.IsVisible = !_readerIsPdf;
            ReaderZoomInButton.IsVisible = !_readerIsPdf;
        }
        if (ReaderPdfBadge is not null)
            ReaderPdfBadge.IsVisible = _readerIsPdf;
        if (ReaderPreviousButton is not null)
            ReaderPreviousButton.IsEnabled = _readerIsPdf ? _readerPdfPage > 1 : _readerChapterIndex > 0;
        if (ReaderNextButton is not null)
            ReaderNextButton.IsEnabled = _readerIsPdf
                ? _readerPdfPage < Math.Max(1, _readerPdfPages.Count)
                : _readerDocument is not null && _readerChapterIndex + 1 < _readerDocument.Chapters.Count;
        UpdateReaderSearchCount();
    }

    private void UpdateReaderSearchCount()
    {
        var text = _readerSearchCount <= 0
            ? "0/0"
            : $"{_readerSearchIndex + 1}/{_readerSearchCount}";
        ReaderInPageSearchCountText.Text = text;
    }

    private int FindReaderTocIndex(EpubReaderNavigationItem item)
    {
        for (var index = 0; index < _readerTocItems.Count; index++)
            if (ReferenceEquals(_readerTocItems[index], item) || _readerTocItems[index] == item) return index;
        return -1;
    }

    private int FindReaderTocIndexForChapter(int chapterIndex) =>
        _readerTocItems.FirstOrDefault(item => item.ChapterIndex == chapterIndex) is { } item
            ? FindReaderTocIndex(item)
            : -1;

    private string? GetReaderChapterPath()
    {
        if (_readerDocument is null || _readerChapterIndex < 0 || _readerChapterIndex >= _readerDocument.Chapters.Count)
            return null;
        return Path.GetRelativePath(
                _readerDocument.RootPath,
                _readerDocument.Chapters[_readerChapterIndex])
            .Replace('\\', '/');
    }

    private double CalculateReaderProgressPercent()
    {
        if (_readerIsPdf)
        {
            if (_readerPdfPages.Count <= 1) return _readerPdfPages.Count == 0 ? 0 : 100;
            return Math.Clamp((_readerPdfPage - 1d) * 100d / (_readerPdfPages.Count - 1d), 0, 100);
        }
        if (_readerDocument is null || _readerDocument.Chapters.Count == 0) return 0;
        return Math.Clamp(
            (_readerChapterIndex + _readerScrollRatio) * 100d / _readerDocument.Chapters.Count,
            0,
            100);
    }

    private static double ReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.TryGetDouble(out var value) && double.IsFinite(value)
            ? value
            : 0;

    private static int ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.TryGetInt32(out var value)
            ? Math.Max(0, value)
            : 0;

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// The reader's custom visual language is intentionally monochrome. Older
    /// databases may still contain the colored annotation values from the
    /// WinUI prototype, so normalize them at the UI boundary before they are
    /// injected into the native webview.
    /// </summary>
    private static string NormalizeReaderAnnotationColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "#D8D8D8";
        var normalized = value.Trim();
        if (normalized.Length != 7 || normalized[0] != '#') return "#D8D8D8";
        if (!int.TryParse(normalized.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return "#D8D8D8";

        var red = (rgb >> 16) & 0xFF;
        var green = (rgb >> 8) & 0xFF;
        var blue = rgb & 0xFF;
        var gray = Math.Clamp((red * 299 + green * 587 + blue * 114 + 500) / 1000, 0, 255);
        return $"#{gray:X2}{gray:X2}{gray:X2}";
    }

    private static int ParseScriptInt(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return 0;
        try
        {
            using var json = JsonDocument.Parse(result);
            var element = json.RootElement;
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number)) return number;
            if (element.ValueKind == JsonValueKind.String
                && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        }
        catch (JsonException)
        {
        }
        return int.TryParse(result.Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fallback)
            ? fallback
            : 0;
    }

    private static string EscapeJavaScriptSingleQuoted(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string EscapeCssString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("{", string.Empty, StringComparison.Ordinal)
            .Replace("}", string.Empty, StringComparison.Ordinal)
            .Replace(";", string.Empty, StringComparison.Ordinal);

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
