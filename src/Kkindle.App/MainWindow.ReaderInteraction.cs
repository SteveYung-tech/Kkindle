using System.Collections.ObjectModel;
using System.Globalization;
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
    private long _readerStatsBaseSeconds;
    private DispatcherTimer? _readerStatsTimer;
    private int _readerTransientStatusSequence;
    private bool _readerContinuousLocked;
    private int _readerContinuousDirection;
    private bool _readerLastNearTop;
    private bool _readerLastNearBottom;
    private DateTimeOffset _readerLastChapterChange = DateTimeOffset.MinValue;
    private bool _readerScrollPollRunning;
    private int _readerProgressSaveSequence;
    private bool _readerProgressSliderUpdating;
    private bool _readerAiBusy;
    private bool _readerAiSettingsVisible;
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
    private bool _readerIsPdf;
    private ReaderAnnotation? _selectedReaderAnnotation;

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
        _readerSearchSequence++;
        _readerWholeSearchSequence++;
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
        ReaderSearchResultList.IsVisible = true;
        ReaderInPageSearchBar.IsVisible = false;
        ReaderSelectionBar.IsVisible = false;
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
        SetReaderCompactSelectedItem(_readerTocItems.FirstOrDefault());
        ApplyReaderPanelLayout();
        UpdateReaderZoomLabel();
        await RefreshReaderBookmarksAsync(cancellationToken);
        await RefreshReaderAnnotationsAsync(cancellationToken);
        await InitializeReaderAiAsync(cancellationToken);
        UpdateReaderToolbar();
        StartReaderStatsTimer();
    }

    private async Task ConfigureReaderHostAsync(
        IReaderHost host,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pagination = _readerLayout.FlowMode == 1;
        var flowCss = ReaderPaginationScripts.CreateFlowCss(
            pagination,
            _readerLayout.VerticalWriting,
            _readerLayout.TwoPageMode,
            _readerLayout.BodyPadding,
            _readerLayout.MaxWidth);
        var fontFamily = EscapeCssString(_readerLayout.FontFamily);
        var css = ReaderAppearanceScripts.MonochromeScrollbarCss
            + "\n"
            + flowCss
            + $"\nhtml, body {{ background: #FFFFFF !important; color: #000000 !important; }}"
            + $"\nbody {{ font-size: {Format(_readerLayout.FontScale)}em !important; line-height: {Format(_readerLayout.LineHeight)} !important; font-family: \"{fontFamily}\" !important; }}";
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
              document.documentElement.style.setProperty(
                '--kkindle-reader-page-viewport-width',
                (window.innerWidth || document.documentElement.clientWidth || 0) + 'px');
              window.__kkindleReaderFlowMode = {{_readerLayout.FlowMode}};
              window.__kkindleReaderVertical = {{(_readerLayout.VerticalWriting ? "true" : "false")}};
              window.__kkindleReaderTwoPage = {{(_readerLayout.TwoPageMode ? "true" : "false")}};
              return true;
            })();
            """;
        await host.InvokeScriptAsync(script);
        await host.InvokeScriptAsync(ReaderPaginationScripts.PageAlignmentHelperDefinition);
        if (pagination)
            await host.InvokeScriptAsync(ReaderPaginationScripts.Snap);

        if (ReferenceEquals(host, CurrentReaderHost))
        {
            if (_readerRestoredProgress is { } progress
                && progress.ChapterIndex == _readerChapterIndex
                && progress.ScrollPosition > 0)
            {
                var left = pagination ? progress.ScrollPosition : 0;
                var top = pagination ? 0 : progress.ScrollPosition;
                await host.InvokeScriptAsync(
                    $"(() => {{ window.scrollTo({{ left: {left.ToString(CultureInfo.InvariantCulture)}, top: {top.ToString(CultureInfo.InvariantCulture)}, behavior: 'instant' }}); }})();");
                _readerRestoredProgress = null;
            }
            await ApplySavedAnnotationsAsync(host, cancellationToken);
        }
    }

    private async Task ApplyReaderLayoutToHostsAsync(CancellationToken cancellationToken)
    {
        var hosts = new[] { _readerActiveHost, _readerPreloadHost }
            .Where(host => host is not null)
            .Cast<IReaderHost>()
            .Distinct()
            .ToArray();
        await Task.WhenAll(hosts.Select(host => ConfigureReaderHostAsync(host, cancellationToken)));
        UpdateReaderToolbar();
    }

    private async Task NavigateToReaderItemAsync(
        EpubReaderNavigationItem item,
        CancellationToken cancellationToken)
    {
        if (_readerDocument is null || CurrentReaderHost is null) return;
        if (item.ChapterIndex < 0 || item.ChapterIndex >= _readerDocument.Chapters.Count) return;
        if (!Uri.TryCreate(item.Target, UriKind.Absolute, out var target) || !target.IsFile) return;
        if (!IsPathInside(_readerDocument.RootPath, target.LocalPath)) return;

        var current = CurrentReaderHost;
        if (ReferenceEquals(current, CurrentReaderHost)
            && ReaderNavigationLocationPolicy.TargetsSameDocument(current.Source, target))
        {
            await ApplyReaderFragmentAsync(current, target.Fragment, cancellationToken);
            _readerChapterIndex = item.ChapterIndex;
            _readerScrollPosition = 0;
            _readerScrollRatio = 0;
            ReaderTocList.SelectedIndex = FindReaderTocIndex(item);
            SetReaderCompactSelectedItem(item);
            ReaderChapterText.Text = GetReaderChapterLabel();
            await UpdateReaderBookmarkIndicatorAsync();
            await SaveReaderProgressAsync(cancellationToken);
            return;
        }

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

            _readerChapterIndex = item.ChapterIndex;
            _readerScrollPosition = 0;
            _readerScrollRatio = 0;
            _readerShowingPreload = !ReferenceEquals(host, CurrentReaderHost);
            SetReaderHostLayer();
            await ApplySavedAnnotationsAsync(host, navigationToken);
            await ApplyReaderFragmentAsync(host, target.Fragment, navigationToken);
            ReaderTocList.SelectedIndex = FindReaderTocIndex(item);
            SetReaderCompactSelectedItem(item);
            ReaderChapterText.Text = GetReaderChapterLabel();
            ReaderStatusText.Text = $"共 {_readerDocument.Chapters.Count} 个章节";
            await UpdateReaderBookmarkIndicatorAsync();
            await SaveReaderProgressAsync(sessionToken);
            _ = PreloadNextReaderChapterAsync(sessionToken);
        }
        catch (OperationCanceledException) when (navigationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReaderStatusText.Text = $"打开章节失败：{exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_readerNavigationCancellation, navigationCancellation))
                _readerNavigationCancellation = null;
            navigationCancellation.Dispose();
        }
    }

    private async Task ApplyReaderFragmentAsync(
        IReaderHost host,
        string? fragment,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(fragment))
        {
            await host.InvokeScriptAsync(ReaderNavigationScripts.NormalizeChapterStart);
            return;
        }

        var escaped = EscapeJavaScriptSingleQuoted(Uri.UnescapeDataString(fragment.TrimStart('#')));
        await host.InvokeScriptAsync(ReaderNavigationScripts.CreateFragmentScroll(
            escaped,
            _readerLayout.FlowMode,
            _readerLayout.VerticalWriting,
            _readerLayout.TwoPageMode));
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
                Quote = item.SelectedText.Trim(),
                Color = NormalizeReaderAnnotationColor(item.Color),
                Style = item.UnderlineStyle
            })
            .GroupBy(item => item.Quote, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(80)
            .ToArray();
        if (marks.Length == 0) return;

        var serialized = JsonSerializer.Serialize(marks);
        var script = $$"""
            (() => {
              const annotations = {{serialized}};
              for (const oldMark of Array.from(document.querySelectorAll('mark.kkindle-saved-annotation'))) {
                const parent = oldMark.parentNode;
                if (!parent) continue;
                parent.replaceChild(document.createTextNode(oldMark.textContent || ''), oldMark);
                parent.normalize?.();
              }
              for (const annotation of annotations) {
                const quote = annotation.Quote || '';
                if (!quote) continue;
                let found = false;
                const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
                while (walker.nextNode() && !found) {
                  const node = walker.currentNode;
                  const parent = node.parentElement;
                  if (!parent || ['SCRIPT', 'STYLE', 'MARK'].includes(parent.tagName)) continue;
                  const at = (node.data || '').toLocaleLowerCase().indexOf(quote.toLocaleLowerCase());
                  if (at < 0) continue;
                  const range = document.createRange();
                  range.setStart(node, at);
                  range.setEnd(node, at + quote.length);
                  const mark = document.createElement('mark');
                  mark.className = 'kkindle-saved-annotation';
                  const color = /^#[0-9a-f]{6}$/i.test(annotation.Color || '') ? annotation.Color : '#E6E6E6';
                  if ((annotation.Style || 'solid') === 'marker') {
                    mark.style.setProperty('background', color, 'important');
                  } else {
                    mark.style.setProperty('background', 'transparent', 'important');
                    mark.style.setProperty('text-decoration', `underline 2px ${color} ${annotation.Style || 'solid'}`, 'important');
                  }
                  mark.style.setProperty('color', '#000000', 'important');
                  range.surroundContents(mark);
                  found = true;
                }
              }
              return true;
            })();
            """;
        await host.InvokeScriptAsync(script);
    }

    private async Task ApplyReaderSearchAsync(string query, int sequence)
    {
        if (_readerDocument is null || CurrentReaderHost is not { } host) return;
        var serializedQuery = JsonSerializer.Serialize(query);
        var script = $$"""
            (() => {
              const oldMarks = Array.from(document.querySelectorAll('mark.kkindle-page-find-hit'));
              for (let index = oldMarks.length - 1; index >= 0; index--) {
                const mark = oldMarks[index];
                const parent = mark.parentNode;
                if (!parent) continue;
                parent.replaceChild(document.createTextNode(mark.textContent || ''), mark);
                parent.normalize?.();
              }
              const query = ({{serializedQuery}} || '').trim();
              if (!query || !document.body) return 0;
              const folded = query.toLocaleLowerCase();
              const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
              const matches = [];
              while (walker.nextNode()) {
                const node = walker.currentNode;
                const parent = node.parentElement;
                if (!parent || ['SCRIPT', 'STYLE', 'NOSCRIPT', 'MARK'].includes(parent.tagName)) continue;
                const text = (node.data || '').toLocaleLowerCase();
                let start = text.indexOf(folded);
                while (start >= 0) {
                  matches.push({ node, start, length: query.length });
                  start = text.indexOf(folded, start + Math.max(1, folded.length));
                }
              }
              for (let index = matches.length - 1; index >= 0; index--) {
                const match = matches[index];
                const range = document.createRange();
                range.setStart(match.node, match.start);
                range.setEnd(match.node, match.start + match.length);
                const mark = document.createElement('mark');
                mark.className = 'kkindle-page-find-hit';
                mark.style.setProperty('background', '#D8D8D8', 'important');
                mark.style.setProperty('color', '#000000', 'important');
                range.surroundContents(mark);
              }
              return matches.length;
            })();
            """;
        var result = await host.InvokeScriptAsync(script);
        if (sequence != _readerSearchSequence) return;
        _readerSearchCount = ParseScriptInt(result);
        _readerSearchIndex = _readerSearchCount > 0 ? 0 : -1;
        await NavigateReaderSearchAsync(_readerSearchIndex, sequence);
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
              const marks = Array.from(document.querySelectorAll('mark.kkindle-page-find-hit'));
              for (let i = 0; i < marks.length; i++) {
                const current = i === {{_readerSearchIndex}};
                marks[i].style.setProperty('background', current ? '#000000' : '#D8D8D8', 'important');
                marks[i].style.setProperty('color', current ? '#FFFFFF' : '#000000', 'important');
              }
              const mark = marks[{{_readerSearchIndex}}];
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
        await host.InvokeScriptAsync(script);
        UpdateReaderSearchCount();
    }

    private async Task ClearReaderSearchAsync()
    {
        _readerSearchSequence++;
        _readerPdfSearchSequence++;
        _readerSearchCount = 0;
        _readerSearchIndex = -1;
        if (CurrentReaderHost is { } host)
        {
            await host.InvokeScriptAsync("""
                (() => {
                  for (const mark of Array.from(document.querySelectorAll('mark.kkindle-page-find-hit'))) {
                    const parent = mark.parentNode;
                    if (!parent) continue;
                    parent.replaceChild(document.createTextNode(mark.textContent || ''), mark);
                    parent.normalize?.();
                  }
                })();
                """);
        }
        UpdateReaderSearchCount();
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
        _ = LoadReaderStatsBaseAsync();
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
        if (_readerActiveSeconds % 30 == 0)
            _ = FlushReaderActiveSecondsAsync();
        UpdateReaderStatsDisplay();
    }

    private async Task FlushReaderActiveSecondsAsync()
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
                _readerSessionCancellation?.Token ?? CancellationToken.None);
        }
        catch
        {
        }
    }

    private void UpdateReaderStatsDisplay()
    {
        if (ReaderStatsText is null) return;
        var cumulative = _readerStatsBaseSeconds + _readerActiveSeconds;
        ReaderStatsText.Text = $"累计阅读 {FormatReaderDuration(cumulative)} · 本次 {FormatReaderDuration(_readerActiveSeconds)}";
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

    private async Task HandleReaderLinkAsync(string href)
    {
        if (_readerDocument is null || !Uri.TryCreate(href, UriKind.Absolute, out var uri) || !uri.IsFile) return;
        var path = Path.GetFullPath(uri.LocalPath);
        if (!IsPathInside(_readerDocument.RootPath, path)) return;

        if (!string.IsNullOrWhiteSpace(uri.Fragment))
        {
            var targets = await _footnotes.ResolveAsync(
                _readerDocument.RootPath,
                [uri.AbsoluteUri],
                ReaderToken);
            if (targets.TryGetValue(EpubFootnoteResolver.NormalizeTargetKey(uri.AbsoluteUri), out var footnote))
            {
                ReaderFootnoteText.Text = footnote;
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
        await NavigateToReaderItemAsync(item, _readerSessionCancellation?.Token ?? CancellationToken.None);
    }

    private async Task HandleReaderFootnoteHoverAsync(string href)
    {
        if (_readerDocument is null
            || !Uri.TryCreate(href, UriKind.Absolute, out var uri)
            || !uri.IsFile
            || string.IsNullOrWhiteSpace(uri.Fragment))
            return;
        var path = Path.GetFullPath(uri.LocalPath);
        if (!IsPathInside(_readerDocument.RootPath, path)) return;

        var targets = await _footnotes.ResolveAsync(
            _readerDocument.RootPath,
            [uri.AbsoluteUri],
            ReaderToken);
        if (targets.TryGetValue(EpubFootnoteResolver.NormalizeTargetKey(uri.AbsoluteUri), out var footnote))
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
                    _readerScrollPosition = _readerLayout.FlowMode == 1
                        ? ReadDouble(root, "left")
                        : ReadDouble(root, "top");
                    _readerScrollWidth = ReadDouble(root, "scrollWidth");
                    _readerScrollHeight = ReadDouble(root, "scrollHeight");
                    _readerClientWidth = ReadDouble(root, "clientWidth");
                    _readerClientHeight = ReadDouble(root, "clientHeight");
                    var max = _readerLayout.FlowMode == 1
                        ? Math.Max(0, _readerScrollWidth - _readerClientWidth)
                        : Math.Max(0, _readerScrollHeight - _readerClientHeight);
                    _readerScrollRatio = max > 0 ? Math.Clamp(_readerScrollPosition / max, 0, 1) : 0;
                    ReaderProgressPercentText.Text = $"{CalculateReaderProgressPercent():0}%";
                    _ = SaveReaderProgressAfterScrollAsync(++_readerProgressSaveSequence);
                    _ = UpdateReaderBookmarkIndicatorAsync();
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
                        ReaderSelectionBar.IsVisible = true;
                        ReaderHighlightButton.IsVisible = true;
                        ReaderAnnotateButton.IsVisible = true;
                    }
                    else
                    {
                        ReaderSelectionBar.IsVisible = false;
                        ReaderHighlightButton.IsVisible = false;
                        ReaderAnnotateButton.IsVisible = false;
                    }
                    break;
                case "link":
                    if (root.TryGetProperty("href", out var href))
                        _ = ObserveReaderTaskAsync(HandleReaderLinkAsync(href.GetString() ?? string.Empty));
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
                        _ = ObserveReaderTaskAsync(HandleReaderFootnoteHoverAsync(footnoteHref.GetString() ?? string.Empty));
                    break;
                case "footnoteLeave":
                    ReaderFootnotePopup.IsVisible = false;
                    break;
                case "resize":
                    _ = ObserveReaderTaskAsync(
                        ApplyReaderLayoutToHostsAsync(_readerSessionCancellation?.Token ?? CancellationToken.None));
                    break;
                case "key":
                    if ((_readerIsPdf || _readerLayout.FlowMode == 1)
                        && root.TryGetProperty("key", out var key))
                    {
                        var keyName = key.GetString();
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
                    break;
            }
        }
        catch (JsonException)
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
        var scrollPosition = vertical ? _readerScrollPosition : _readerScrollPosition;
        if (scrollSize <= 0 || clientSize <= 0) return;

        var nearTop = scrollPosition <= 48;
        var nearBottom = scrollPosition + clientSize >= scrollSize - 48;
        var overflows = scrollSize > clientSize + 16;

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

        if (nearBottom && !_readerLastNearBottom)
        {
            if (_readerChapterIndex + 1 < _readerDocument.Chapters.Count)
            {
                _readerContinuousLocked = true;
                _readerContinuousDirection = 1;
                _readerLastChapterChange = DateTimeOffset.UtcNow;
                _readerScrollPollRunning = true;
                _ = MoveReaderChapterAsync(1).ContinueWith(
                    _ => _readerScrollPollRunning = false,
                    TaskScheduler.Default);
            }
        }
        else if (nearTop && !_readerLastNearTop)
        {
            if (_readerChapterIndex > 0)
            {
                _readerContinuousLocked = true;
                _readerContinuousDirection = -1;
                _readerLastChapterChange = DateTimeOffset.UtcNow;
                _readerScrollPollRunning = true;
                _ = MoveReaderChapterAsync(-1).ContinueWith(
                    _ => _readerScrollPollRunning = false,
                    TaskScheduler.Default);
            }
        }
        _readerLastNearTop = nearTop;
        _readerLastNearBottom = nearBottom;
    }

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
        if (_readerPageAnimation == ReaderAnimationNone)
            return await host.InvokeScriptAsync(ReaderPaginationScripts.CreateTurnScript(direction));

        var token = ReaderToken;
        var animation = _readerPageAnimation;
        try
        {
            await host.InvokeScriptAsync(CreateReaderTransitionScript(animation, direction, restore: false));
        }
        catch
        {
            // A browser that rejects a cosmetic transition must not block a
            // normal page turn.
        }

        try
        {
            await Task.Delay(animation == ReaderAnimationWave ? 90 : 110, token);
            var result = await host.InvokeScriptAsync(ReaderPaginationScripts.CreateTurnScript(direction));
            try
            {
                await host.InvokeScriptAsync(CreateReaderTransitionScript(animation, direction, restore: true));
                await Task.Delay(animation == ReaderAnimationWave ? 320 : 190, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // The page has already turned; cleanup is best effort.
            }
            return result;
        }
        finally
        {
            try
            {
                await host.InvokeScriptAsync(CreateReaderTransitionCleanupScript());
            }
            catch
            {
            }
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
              `;
              root.style.transition = 'opacity 160ms ease, transform 180ms ease, filter 220ms ease';
              if ({{(restore ? "true" : "false")}}) {
                root.style.opacity = '1';
                root.style.transform = 'translateX(0)';
                root.style.filter = 'none';
                root.style.animation = 'none';
              } else {
                root.style.opacity = {{(safeAnimation == ReaderAnimationFade ? "'0.2'" : "'0.42'")}};
                root.style.transform = {{(safeAnimation == ReaderAnimationSlide ? $"'translateX({offset}px)'" : "'translateX(0)'")}};
                root.style.filter = {{(safeAnimation == ReaderAnimationWave ? "'grayscale(1) contrast(1.06)'" : "'none'")}};
                root.style.animation = {{(safeAnimation == ReaderAnimationWave ? "'kkindle-reader-wave 380ms ease both'" : "'none'")}};
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
            ReaderTocList.SelectedIndex = FindReaderTocIndexForChapter(_readerChapterIndex);
        }
    }

    private async void ReaderTocList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is EpubReaderNavigationItem item)
            await NavigateToReaderItemAsync(item, _readerSessionCancellation?.Token ?? CancellationToken.None);
    }

    private async void ReaderSearchButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ReaderInPageSearchBar.IsVisible)
        {
            await ClearReaderSearchAsync();
            ReaderInPageSearchBar.IsVisible = false;
            ReaderInPageSearchBox.Text = string.Empty;
            return;
        }
        ReaderInPageSearchBar.IsVisible = true;
        ReaderInPageSearchBox.Focus();
        ReaderInPageSearchBox.SelectAll();
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
        SelectReaderPageAnimation(_readerPageAnimation);
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
        if (ReaderPdfBottomText is not null)
            ReaderPdfBottomText.IsVisible = _readerIsPdf;
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
