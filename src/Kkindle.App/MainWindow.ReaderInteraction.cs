using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    private ReaderLayoutSettings _readerLayout = ReaderLayoutDefaults.Normalize(new ReaderLayoutSettings());
    private IReadOnlyList<EpubReaderNavigationItem> _readerTocItems = [];
    private ReaderProgressRow? _readerRestoredProgress;
    private int _readerSearchSequence;
    private int _readerSearchCount;
    private int _readerSearchIndex = -1;
    private string? _readerPendingSelection;
    private double _readerScrollPosition;
    private double _readerScrollRatio;
    private double _readerScrollWidth;
    private double _readerScrollHeight;
    private double _readerClientWidth;
    private double _readerClientHeight;
    private DateTimeOffset _readerSessionStarted;
    private WindowState _readerWindowStateBeforeZen = WindowState.Normal;
    private bool _readerZenMode;
    private int _readerProgressSaveSequence;

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
        _readerScrollPosition = 0;
        _readerScrollRatio = 0;
        _readerScrollWidth = 0;
        _readerScrollHeight = 0;
        _readerClientWidth = 0;
        _readerClientHeight = 0;
        _readerSearchCount = 0;
        _readerSearchIndex = -1;
        _readerSearchSequence++;
        _readerSessionStarted = DateTimeOffset.UtcNow;
        _readerZenMode = false;
        ReaderTocList.ItemsSource = _readerTocItems;
        ReaderTocList.SelectedIndex = -1;
        ReaderTocPanel.IsVisible = false;
        ReaderSearchBox.Text = string.Empty;
        ReaderSearchBox.IsVisible = false;
        ReaderSearchPreviousButton.IsVisible = false;
        ReaderSearchNextButton.IsVisible = false;
        ReaderSearchCountText.IsVisible = false;
        UpdateReaderToolbar();
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

        var current = CurrentReaderHost;
        if (ReferenceEquals(current, CurrentReaderHost)
            && ReaderNavigationLocationPolicy.TargetsSameDocument(current.Source, target))
        {
            await ApplyReaderFragmentAsync(current, target.Fragment, cancellationToken);
            _readerChapterIndex = item.ChapterIndex;
            ReaderTocList.SelectedIndex = FindReaderTocIndex(item);
            ReaderChapterText.Text = GetReaderChapterLabel();
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
            _readerShowingPreload = !ReferenceEquals(host, CurrentReaderHost);
            SetReaderHostLayer();
            await ApplyReaderFragmentAsync(host, target.Fragment, navigationToken);
            ReaderTocList.SelectedIndex = FindReaderTocIndex(item);
            ReaderChapterText.Text = GetReaderChapterLabel();
            ReaderStatusText.Text = $"共 {_readerDocument.Chapters.Count} 个章节";
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
        var quotes = annotations
            .Where(item => string.Equals(item.ChapterPath, chapterPath, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.SelectedText.Trim())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(80)
            .ToArray();
        if (quotes.Length == 0) return;

        var serialized = JsonSerializer.Serialize(quotes);
        var script = $$"""
            (() => {
              const quotes = {{serialized}};
              for (const oldMark of Array.from(document.querySelectorAll('mark.kkindle-saved-annotation'))) {
                const parent = oldMark.parentNode;
                if (!parent) continue;
                parent.replaceChild(document.createTextNode(oldMark.textContent || ''), oldMark);
                parent.normalize?.();
              }
              const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
              for (const quote of quotes) {
                if (!quote) continue;
                let found = false;
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
                  mark.style.setProperty('background', '#E6E6E6', 'important');
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

    private async Task SaveReaderSessionAsync(CancellationToken cancellationToken)
    {
        if (_readerBookCard is null || _readerBookFile is null || _readerSessionStarted == default) return;
        var seconds = Math.Max(0, (long)(DateTimeOffset.UtcNow - _readerSessionStarted).TotalSeconds);
        if (seconds <= 0) return;
        try
        {
            await _readerData.AddReadingTimeAsync(
                _readerBookCard.Book.Id,
                _readerBookFile.Id,
                seconds,
                CalculateReaderProgressPercent(),
                _readerChapterIndex,
                _readerDocument?.Chapters.Count ?? 0,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    private async Task HandleReaderLinkAsync(string href)
    {
        if (_readerDocument is null || !Uri.TryCreate(href, UriKind.Absolute, out var uri) || !uri.IsFile) return;
        var path = Path.GetFullPath(uri.LocalPath);
        if (!IsPathInside(_readerDocument.RootPath, path)) return;
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
                    ReaderStatusText.Text = $"共 {_readerDocument?.Chapters.Count ?? 0} 个章节";
                    break;
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
                    ReaderProgressText.Text = $"{CalculateReaderProgressPercent():0}%";
                    _ = SaveReaderProgressAfterScrollAsync(++_readerProgressSaveSequence);
                    break;
                case "selection":
                    _readerPendingSelection = root.TryGetProperty("text", out var selection)
                        ? selection.GetString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(_readerPendingSelection))
                        ReaderStatusText.Text = "已选中文字，可点击“划线”保存";
                    break;
                case "link":
                    if (root.TryGetProperty("href", out var href))
                        _ = ObserveReaderTaskAsync(HandleReaderLinkAsync(href.GetString() ?? string.Empty));
                    break;
                case "resize":
                    _ = ObserveReaderTaskAsync(
                        ApplyReaderLayoutToHostsAsync(_readerSessionCancellation?.Token ?? CancellationToken.None));
                    break;
                case "key":
                    if (_readerLayout.FlowMode == 1 && root.TryGetProperty("key", out var key))
                        _ = ObserveReaderTaskAsync(
                            TurnReaderPageAsync(
                                string.Equals(key.GetString(), "ArrowLeft", StringComparison.Ordinal)
                                    || string.Equals(key.GetString(), "PageUp", StringComparison.Ordinal)
                                    ? -1
                                    : 1));
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
            if (sequence != _readerProgressSaveSequence || _readerDocument is null) return;
            await SaveReaderProgressAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task TurnReaderPageAsync(int direction)
    {
        if (CurrentReaderHost is not { } host) return;
        var result = await host.InvokeScriptAsync(ReaderPaginationScripts.CreateTurnScript(direction));
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

    private void ReaderTocButton_Click(object? sender, RoutedEventArgs e)
    {
        ReaderTocPanel.IsVisible = !ReaderTocPanel.IsVisible;
        if (ReaderTocPanel.IsVisible)
            ReaderTocList.SelectedIndex = FindReaderTocIndexForChapter(_readerChapterIndex);
    }

    private async void ReaderTocList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is EpubReaderNavigationItem item)
            await NavigateToReaderItemAsync(item, _readerSessionCancellation?.Token ?? CancellationToken.None);
    }

    private async void ReaderSearchButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ReaderSearchBox.IsVisible)
        {
            await ClearReaderSearchAsync();
            ReaderSearchBox.IsVisible = false;
            ReaderSearchPreviousButton.IsVisible = false;
            ReaderSearchNextButton.IsVisible = false;
            ReaderSearchCountText.IsVisible = false;
            return;
        }
        ReaderSearchBox.IsVisible = true;
        ReaderSearchPreviousButton.IsVisible = true;
        ReaderSearchNextButton.IsVisible = true;
        ReaderSearchCountText.IsVisible = true;
        ReaderSearchBox.Focus();
    }

    private async void ReaderSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_readerDocument is null) return;
        var sequence = ++_readerSearchSequence;
        var query = ReaderSearchBox.Text?.Trim() ?? string.Empty;
        await Task.Delay(120);
        if (sequence != _readerSearchSequence) return;
        await ApplyReaderSearchAsync(query, sequence);
    }

    private async void ReaderSearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            await ClearReaderSearchAsync();
            ReaderSearchBox.IsVisible = false;
            ReaderSearchPreviousButton.IsVisible = false;
            ReaderSearchNextButton.IsVisible = false;
            ReaderSearchCountText.IsVisible = false;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await NavigateReaderSearchAsync(_readerSearchIndex + ((e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1));
        }
    }

    private async void ReaderSearchPreviousButton_Click(object? sender, RoutedEventArgs e) =>
        await NavigateReaderSearchAsync(_readerSearchIndex - 1);

    private async void ReaderSearchNextButton_Click(object? sender, RoutedEventArgs e) =>
        await NavigateReaderSearchAsync(_readerSearchIndex + 1);

    private async void ReaderModeButton_Click(object? sender, RoutedEventArgs e)
    {
        _readerLayout = ReaderLayoutDefaults.Normalize(_readerLayout with { FlowMode = _readerLayout.FlowMode == 1 ? 0 : 1 });
        await ApplyReaderLayoutToHostsAsync(_readerSessionCancellation?.Token ?? CancellationToken.None);
        await SaveReaderLayoutAsync(CancellationToken.None);
    }

    private async void ReaderDecreaseFontButton_Click(object? sender, RoutedEventArgs e) =>
        await ChangeReaderFontAsync(-0.1);

    private async void ReaderIncreaseFontButton_Click(object? sender, RoutedEventArgs e) =>
        await ChangeReaderFontAsync(0.1);

    private async Task ChangeReaderFontAsync(double delta)
    {
        _readerLayout = ReaderLayoutDefaults.Normalize(_readerLayout with { FontScale = _readerLayout.FontScale + delta });
        await ApplyReaderLayoutToHostsAsync(_readerSessionCancellation?.Token ?? CancellationToken.None);
        await SaveReaderLayoutAsync(CancellationToken.None);
    }

    private async void ReaderBookmarkButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_readerBookCard is null || _readerBookFile is null || _readerDocument is null) return;
        var chapterPath = GetReaderChapterPath();
        if (chapterPath is null) return;
        await _readerData.SaveBookmarkAsync(new ReaderBookmark
        {
            BookId = _readerBookCard.Book.Id,
            BookFileId = _readerBookFile.Id,
            ChapterPath = chapterPath,
            Fragment = null,
            ChapterIndex = _readerChapterIndex,
            ScrollPosition = (int)Math.Round(_readerScrollPosition),
            FlowMode = _readerLayout.FlowMode,
            Title = GetReaderChapterLabel(),
            Quote = _readerPendingSelection ?? string.Empty
        }, _readerSessionCancellation?.Token ?? CancellationToken.None);
        ReaderStatusText.Text = "书签已保存";
    }

    private async void ReaderAnnotateButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_readerBookCard is null || _readerBookFile is null || _readerDocument is null) return;
        var text = _readerPendingSelection?.Trim();
        var chapterPath = GetReaderChapterPath();
        if (string.IsNullOrWhiteSpace(text) || chapterPath is null)
        {
            ReaderStatusText.Text = "请先在正文中选择文字";
            return;
        }
        await _readerData.SaveAnnotationAsync(new ReaderAnnotation
        {
            BookId = _readerBookCard.Book.Id,
            BookFileId = _readerBookFile.Id,
            ChapterPath = chapterPath,
            SelectedText = text,
            StartOffset = 0,
            EndOffset = text.Length,
            Color = "#000000",
            UnderlineStyle = "solid"
        }, _readerSessionCancellation?.Token ?? CancellationToken.None);
        _readerPendingSelection = null;
        if (CurrentReaderHost is { } host)
            await ApplySavedAnnotationsAsync(host, _readerSessionCancellation?.Token ?? CancellationToken.None);
        ReaderStatusText.Text = "划线已保存";
    }

    private void ReaderZenButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_readerZenMode)
        {
            _readerWindowStateBeforeZen = WindowState;
            WindowState = WindowState.FullScreen;
            _readerZenMode = true;
        }
        else
        {
            WindowState = _readerWindowStateBeforeZen;
            _readerZenMode = false;
        }
    }

    private void ExitReaderZenMode()
    {
        if (!_readerZenMode) return;
        WindowState = _readerWindowStateBeforeZen;
        _readerZenMode = false;
    }

    private void UpdateReaderToolbar()
    {
        if (ReaderModeButton is not null)
            ReaderModeButton.Content = _readerLayout.FlowMode == 1 ? "分页" : "滚动";
        if (ReaderProgressText is not null)
            ReaderProgressText.Text = $"{CalculateReaderProgressPercent():0}%";
        UpdateReaderSearchCount();
    }

    private void UpdateReaderSearchCount()
    {
        ReaderSearchCountText.Text = _readerSearchCount <= 0
            ? "0/0"
            : $"{_readerSearchIndex + 1}/{_readerSearchCount}";
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
