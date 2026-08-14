using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class MainWindow
{
    private int _readerWholeSearchSequence;
    private int _readerPdfSearchSequence;

    private CancellationToken ReaderToken =>
        _readerSessionCancellation?.Token ?? _lifetimeCancellation.Token;

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!ReaderRoot.IsVisible) return;
        // F11 toggles zen mode (enter/exit), matching the common fullscreen
        // convention used by browsers and readers; Esc is an additional way to
        // leave zen mode (both from the WinUI reference's global hook).
        if (e.Key == Key.F11)
        {
            e.Handled = true;
            ToggleReaderZenMode();
            return;
        }
        if (e.Key == Key.Escape && _readerZenMode)
        {
            e.Handled = true;
            ToggleReaderZenMode();
            return;
        }
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 && e.Key == Key.F)
        {
            e.Handled = true;
            if (_readerIsPdf)
            {
                _readerTocMinimal = false;
                _readerTocExpanded = true;
                ApplyReaderPanelLayout();
                ShowReaderSearchTab();
                ReaderTocSearchBox.Focus();
            }
            else
            {
                ReaderSearchButton_Click(sender, e);
            }
            return;
        }

        if ((e.KeyModifiers & KeyModifiers.Control) != 0 && e.Key == Key.B
            && !IsReaderTextInputFocused())
        {
            e.Handled = true;
            _ = ObserveReaderTaskAsync(ToggleReaderBookmarkAsync());
        }
    }

    private bool IsReaderTextInputFocused()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        return focused is TextBox or ComboBox;
    }

    private async Task OpenPdfReaderAsync(
        BookCardViewModel card,
        BookFile file,
        string path)
    {
        _readerSessionCancellation?.Cancel();
        _readerSessionCancellation?.Dispose();
        _readerSessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        var token = _readerSessionCancellation.Token;

        try
        {
            SetTaskStatus($"正在准备《{card.Title}》的 PDF 阅读器…");
            var pages = await _pdfTextService.ExtractAsync(path, token);
            if (pages.Count == 0)
                throw new InvalidDataException("PDF 没有可读取的页面文本。");

            _readerBookCard = card;
            _readerBookFile = file;
            _readerDocument = null;
            _readerIsPdf = true;
            _readerPdfPages = pages;
            _readerPdfPage = 1;
            _readerChapterIndex = 0;
            _readerScrollRatio = 0;
            _readerScrollPosition = 0;

            // The PDF surface is rendered as a local, selectable HTML text view.
            // This keeps paging, search, highlights and the assistant consistent
            // with EPUB instead of handing control to an opaque browser plugin.
            await InitializeReaderInteractionAsync(
                new EpubReaderDocument(Path.GetDirectoryName(path) ?? string.Empty, [], []),
                file,
                token);
            _readerIsPdf = true;
            _readerLayout = ReaderLayoutDefaults.Normalize(_readerLayout with
            {
                FlowMode = 0,
                TwoPageMode = false
            });

            var progress = await _readerData.GetProgressAsync(file.Id, token);
            if (progress is not null)
                _readerPdfPage = Math.Clamp(progress.ChapterIndex + 1, 1, pages.Count);
            _readerChapterIndex = _readerPdfPage - 1;

            ReaderTitleText.Text = card.Title;
            ReaderChapterText.Text = GetReaderChapterLabel();
            ReaderStatusText.Text = $"PDF · {pages.Count} 页 · 正在加载";
            ReaderRoot.IsVisible = true;
            LibraryRoot.IsVisible = false;
            WindowBrandText.IsVisible = true;
            // The WinUI reference keeps the TOC panel open for PDF with an
            // explanatory empty state; bookmarks still work per page.
            _readerTocExpanded = true;
            _readerTocMinimal = false;
            ReaderTocEmptyText.Text = "PDF 使用内置查看器；Kkindle 已启用本地搜索、页码进度、书签和页面笔记。";
            ReaderTocEmptyText.IsVisible = true;
            ApplyReaderPanelLayout();
            ShowReaderTocTab();
            ReaderAiView.IsVisible = true;
            ReaderNotesView.IsVisible = false;
            ReaderAiComposer.IsVisible = true;
            ReaderAssistantPanel.IsVisible = true;
            ReaderBodyGrid.ColumnDefinitions[2].Width = new GridLength(330);

            await EnsureReaderHostsAsync();
            SetReaderHostLayer();
            if (CurrentReaderHost is not { } host || !await NavigateReaderHostAndWaitAsync(host, new Uri("about:blank"), token))
                throw new InvalidOperationException("PDF 阅读器页面加载失败。");

            await RenderPdfPagesAsync(host, token);
            await ApplySavedReaderPdfAnnotationsAsync(token);
            ReaderStatusText.Text = $"PDF · {pages.Count} 页 · 可搜索文本已加载";
            UpdateReaderToolbar();
            await SaveReaderProgressAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await CloseReaderAsync();
            SetTaskStatus($"打开 PDF 阅读器失败：{exception.Message}");
        }
    }

    private async Task RenderPdfPagesAsync(IReaderHost host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pages = JsonSerializer.Serialize(_readerPdfPages.Select(page => new
        {
            page.PageNumber,
            page.Text
        }));

        var script = """
            (() => {
              const pages = __PAGES__;
              document.open();
              document.write('<!doctype html><html><head><meta charset="utf-8"><title>Kreader PDF</title></head><body></body></html>');
              document.close();
              const style = document.createElement('style');
              style.textContent = `
                :root { color-scheme: light; }
                html, body { margin: 0; padding: 0; background: #fff; color: #111; }
                body { font-family: "Microsoft YaHei UI", "Segoe UI", sans-serif; padding: 36px 8vw 80px; }
                .kkindle-pdf-page { max-width: 900px; margin: 0 auto 34px; padding: 34px 42px; border: 1px solid #e1e1e1; background: #fff; box-shadow: 0 2px 12px rgba(0,0,0,.04); }
                .kkindle-pdf-page-title { color: #777; font-size: 12px; margin-bottom: 22px; letter-spacing: .08em; }
                .kkindle-pdf-page-text { white-space: pre-wrap; font-size: 16px; line-height: 1.9; user-select: text; }
                mark.kkindle-page-find-hit { background: #d8d8d8 !important; color: #000 !important; }
                mark.kkindle-saved-annotation { background: #e6e6e6 !important; color: #000 !important; }
              `;
              document.head.appendChild(style);
              const root = document.body;
              for (const page of pages) {
                const article = document.createElement('article');
                article.className = 'kkindle-pdf-page';
                article.dataset.page = String(page.PageNumber);
                const title = document.createElement('div');
                title.className = 'kkindle-pdf-page-title';
                title.textContent = 'PDF · 第 ' + page.PageNumber + ' 页';
                const text = document.createElement('div');
                text.className = 'kkindle-pdf-page-text';
                text.textContent = page.Text || '（本页没有可提取的文本）';
                article.append(title, text);
                root.appendChild(article);
              }
              const send = value => {
                try {
                  const body = JSON.stringify(value);
                  const webview = window.chrome && window.chrome.webview;
                  if (webview && typeof webview.postMessage === 'function') webview.postMessage(body);
                } catch (_) { }
              };
              const report = () => {
                const pagesOnScreen = Array.from(document.querySelectorAll('[data-page]'));
                let current = pagesOnScreen[0]?.dataset.page || '1';
                let best = Number.POSITIVE_INFINITY;
                for (const page of pagesOnScreen) {
                  const distance = Math.abs(page.getBoundingClientRect().top - 96);
                  if (distance < best) { best = distance; current = page.dataset.page || current; }
                }
                const element = document.scrollingElement || document.documentElement;
                send({ type: 'pdfPage', page: Number(current) || 1, top: element.scrollTop || 0,
                  scrollWidth: element.scrollWidth || 0, scrollHeight: element.scrollHeight || 0,
                  clientWidth: element.clientWidth || 0, clientHeight: element.clientHeight || 0 });
              };
              let queued = false;
              const queue = () => { if (queued) return; queued = true; requestAnimationFrame(() => { queued = false; report(); }); };
              document.addEventListener('scroll', queue, { passive: true });
              document.addEventListener('mouseup', () => {
                const text = (window.getSelection()?.toString() || '').trim();
                send({ type: 'selection', text: text.slice(0, 12000) });
              }, true);
              document.addEventListener('keydown', event => {
                if (['ArrowLeft','ArrowRight','PageUp','PageDown'].includes(event.key))
                  send({ type: 'key', key: event.key });
              }, true);
              document.addEventListener('click', event => {
                const width = window.innerWidth || document.documentElement.clientWidth || 0;
                const x = event.clientX || 0;
                if (width > 0 && x <= width * .28) send({ type: 'page', direction: -1 });
                else if (width > 0 && x >= width * .72) send({ type: 'page', direction: 1 });
              }, true);
              window.addEventListener('resize', queue, { passive: true });
              report();
              return true;
            })();
            """.Replace("__PAGES__", pages, StringComparison.Ordinal);

        await host.InvokeScriptAsync(script);
        await NavigatePdfPageAsync(_readerPdfPage, cancellationToken, saveProgress: false);
    }

    private async Task NavigatePdfPageAsync(
        int page,
        CancellationToken cancellationToken,
        bool saveProgress = true)
    {
        if (!_readerIsPdf || _readerPdfPages.Count == 0 || CurrentReaderHost is not { } host) return;
        _readerPdfPage = Math.Clamp(page, 1, _readerPdfPages.Count);
        _readerChapterIndex = _readerPdfPage - 1;
        var selector = JsonSerializer.Serialize($"[data-page='{_readerPdfPage}']");
        await host.InvokeScriptAsync($"(() => {{ const el = document.querySelector({selector}); el?.scrollIntoView({{ block: 'start', behavior: 'instant' }}); return !!el; }})();");
        ReaderChapterText.Text = GetReaderChapterLabel();
        UpdateReaderToolbar();
        await UpdateReaderBookmarkIndicatorAsync();
        if (saveProgress) await SaveReaderProgressAsync(cancellationToken);
    }

    private async Task ApplySavedReaderPdfAnnotationsAsync(CancellationToken cancellationToken)
    {
        if (!_readerIsPdf || _readerBookFile is null || CurrentReaderHost is not { } host) return;
        var annotations = (await _readerData.GetAnnotationsAsync(_readerBookFile.Id, cancellationToken))
            .Where(annotation => annotation.ChapterPath.StartsWith("pdf:page:", StringComparison.OrdinalIgnoreCase))
            .Where(annotation => !string.IsNullOrWhiteSpace(annotation.SelectedText))
            .Select(annotation => new
            {
                Page = annotation.ChapterPath["pdf:page:".Length..],
                Quote = annotation.SelectedText.Trim(),
                Color = NormalizeReaderAnnotationColor(annotation.Color),
                annotation.UnderlineStyle
            })
            .Take(100)
            .ToArray();
        if (annotations.Length == 0) return;

        var serialized = JsonSerializer.Serialize(annotations);
        var script = """
            (() => {
              const annotations = __ANNOTATIONS__;
              for (const oldMark of Array.from(document.querySelectorAll('mark.kkindle-saved-annotation'))) {
                const parent = oldMark.parentNode; if (!parent) continue;
                parent.replaceChild(document.createTextNode(oldMark.textContent || ''), oldMark); parent.normalize?.();
              }
              for (const annotation of annotations) {
                const root = document.querySelector('[data-page="' + annotation.Page + '"]');
                if (!root || !annotation.Quote) continue;
                const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
                const folded = annotation.Quote.toLocaleLowerCase(); let found = false;
                while (walker.nextNode() && !found) {
                  const node = walker.currentNode; const parent = node.parentElement;
                  if (!parent || ['SCRIPT','STYLE','MARK'].includes(parent.tagName)) continue;
                  const start = (node.data || '').toLocaleLowerCase().indexOf(folded); if (start < 0) continue;
                  const range = document.createRange(); range.setStart(node, start); range.setEnd(node, start + annotation.Quote.length);
                  const mark = document.createElement('mark'); mark.className = 'kkindle-saved-annotation';
                  const color = /^#[0-9a-f]{6}$/i.test(annotation.Color || '') ? annotation.Color : '#E6E6E6';
                  if ((annotation.UnderlineStyle || 'solid') === 'marker') mark.style.background = color;
                  else mark.style.textDecoration = 'underline 2px ' + color + ' ' + (annotation.UnderlineStyle || 'solid');
                  range.surroundContents(mark); found = true;
                }
              }
              return true;
            })();
            """.Replace("__ANNOTATIONS__", serialized, StringComparison.Ordinal);
        await host.InvokeScriptAsync(script);
    }

    private async Task RefreshReaderBookmarksAsync(CancellationToken cancellationToken)
    {
        ReaderBookmarks.Clear();
        if (_readerBookFile is null) return;
        var bookmarks = await _readerData.GetBookmarksAsync(_readerBookFile.Id, cancellationToken);
        foreach (var bookmark in bookmarks
                     .OrderBy(item => item.ChapterIndex)
                     .ThenBy(item => item.CreatedAt))
            ReaderBookmarks.Add(bookmark);
        ReaderBookmarkEmptyText.IsVisible = ReaderBookmarks.Count == 0;
    }

    private async Task RefreshReaderAnnotationsAsync(CancellationToken cancellationToken)
    {
        ReaderAnnotations.Clear();
        _selectedReaderAnnotation = null;
        ReaderDeleteAnnotationButton.IsEnabled = false;
        if (_readerBookFile is null) return;
        var annotations = await _readerData.GetAnnotationsAsync(_readerBookFile.Id, cancellationToken);
        foreach (var annotation in annotations) ReaderAnnotations.Add(annotation);
    }

    private void ShowReaderTocTab()
    {
        ReaderTocView.IsVisible = true;
        ReaderBookmarkPane.IsVisible = false;
        ReaderSearchPanel.IsVisible = false;
        ReaderTocEmptyText.IsVisible = _readerTocItems.Count == 0;
        SetReaderTocTabState(bookmarkTab: false);
    }

    private void ShowReaderBookmarkTab()
    {
        ReaderTocView.IsVisible = false;
        ReaderBookmarkPane.IsVisible = true;
        ReaderSearchPanel.IsVisible = false;
        ReaderBookmarkEmptyText.IsVisible = ReaderBookmarks.Count == 0;
        SetReaderTocTabState(bookmarkTab: true);
    }

    private void ShowReaderSearchTab()
    {
        ReaderTocView.IsVisible = false;
        ReaderBookmarkPane.IsVisible = false;
        ReaderSearchPanel.IsVisible = true;
        SetReaderTocTabState(bookmarkTab: false, searchTab: true);
    }

    // Hollow tabs: transparent fill for every state; the selected tab is
    // outlined with a black border instead of a filled rectangle (WinUI
    // reference's ReaderTocTabsPanel visual).
    private void SetReaderTocTabState(bool bookmarkTab, bool searchTab = false)
    {
        if (ReaderTocTabButton is null || ReaderBookmarkTabButton is null) return;
        ApplyReaderTabVisual(ReaderTocTabButton, !bookmarkTab && !searchTab);
        ApplyReaderTabVisual(ReaderBookmarkTabButton, bookmarkTab);
        if (ReaderWholeSearchTabButton is not null)
            ApplyReaderTabVisual(ReaderWholeSearchTabButton, searchTab);
    }

    private static void ApplyReaderTabVisual(Button button, bool selected)
    {
        button.Background = Brushes.Transparent;
        button.BorderBrush = selected ? Brushes.Black : new SolidColorBrush(Color.FromArgb(255, 213, 213, 209));
        button.BorderThickness = new Thickness(1);
    }

    private void ReaderTocTabButton_Click(object? sender, RoutedEventArgs e) => ShowReaderTocTab();

    private void ReaderBookmarkTabButton_Click(object? sender, RoutedEventArgs e) => ShowReaderBookmarkTab();

    private async void ReaderWholeSearchTabButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowReaderSearchTab();
        ReaderTocSearchBox.Focus();
        await RefreshReaderWholeSearchAsync(ReaderTocSearchBox.Text?.Trim() ?? string.Empty);
    }

    private void ReaderSearchToolbarButton_Click(object? sender, RoutedEventArgs e)
    {
        _readerTocMinimal = false;
        _readerTocExpanded = true;
        ApplyReaderPanelLayout();
        ShowReaderSearchTab();
        ReaderTocSearchBox.Focus();
    }

    private async void ReaderBookmarkList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is ReaderBookmark bookmark)
            await NavigateToReaderBookmarkAsync(bookmark);
    }

    private async void ReaderBookmarkDeleteButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ReaderBookmark bookmark } || _readerBookFile is null) return;
        await _readerData.DeleteBookmarkAsync(bookmark.Id, ReaderToken);
        await RefreshReaderBookmarksAsync(ReaderToken);
        await UpdateReaderBookmarkIndicatorAsync();
        ShowReaderTransientStatus("书签已删除");
    }

    private async Task ToggleReaderBookmarkAsync()
    {
        if (_readerBookCard is null || _readerBookFile is null) return;
        var currentPath = _readerIsPdf
            ? $"pdf:page:{_readerPdfPage}"
            : GetReaderChapterPath();
        if (string.IsNullOrWhiteSpace(currentPath)) return;

        var quote = _readerPendingSelection
            ?? (await CaptureCurrentSectionQuoteAsync());
        var existing = ReaderBookmarks.FirstOrDefault(bookmark =>
            bookmark.ChapterIndex == _readerChapterIndex
            && string.Equals(bookmark.ChapterPath, currentPath, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(bookmark.Quote)
                || string.IsNullOrWhiteSpace(quote)
                || string.Equals(bookmark.Quote, quote, StringComparison.Ordinal)));
        if (existing is not null)
        {
            await _readerData.DeleteBookmarkAsync(existing.Id, ReaderToken);
            ShowReaderTransientStatus("已取消书签");
            ShowReaderBookmarkFeedback("已取消书签");
        }
        else
        {
            await _readerData.SaveBookmarkAsync(new ReaderBookmark
            {
                BookId = _readerBookCard.Book.Id,
                BookFileId = _readerBookFile.Id,
                ChapterPath = currentPath,
                ChapterIndex = _readerChapterIndex,
                ScrollPosition = (int)Math.Round(_readerScrollPosition),
                FlowMode = _readerLayout.FlowMode,
                Title = GetReaderChapterLabel(),
                Quote = quote ?? string.Empty
            }, ReaderToken);
            ShowReaderTransientStatus("已添加书签");
            ShowReaderBookmarkFeedback("已添加书签");
        }
        await RefreshReaderBookmarksAsync(ReaderToken);
        await UpdateReaderBookmarkIndicatorAsync();
    }

    private async Task<string?> CaptureCurrentSectionQuoteAsync()
    {
        if (CurrentReaderHost is not { } host) return null;
        try
        {
            var result = await host.InvokeScriptAsync(
                "(document.body?.innerText || document.body?.textContent || '').trim().slice(0, 40)");
            var normalized = result?.Trim().Trim('"') ?? string.Empty;
            return normalized.Length <= 40 ? normalized : normalized[..40];
        }
        catch
        {
            return null;
        }
    }

    // Shows a transient ToolTip near the bookmark button so the user gets
    // immediate feedback without clobbering the header status text (which is
    // also updated). Mirrors the WinUI ReaderBookmarkFeedbackToolTip.
    private void ShowReaderBookmarkFeedback(string message)
    {
        if (ReaderBookmarkButton is null) return;
        ToolTip.SetTip(ReaderBookmarkButton, message);
        ToolTip.SetIsOpen(ReaderBookmarkButton, true);
        _ = Task.Delay(1600).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (ReaderBookmarkButton is not null)
                    ToolTip.SetIsOpen(ReaderBookmarkButton, false);
            }),
            TaskScheduler.Default);
    }

    private async Task UpdateReaderBookmarkIndicatorAsync()
    {
        if (ReaderBookmarkCornerMarker is null || _readerBookFile is null)
        {
            return;
        }
        var currentPath = _readerIsPdf
            ? $"pdf:page:{_readerPdfPage}"
            : GetReaderChapterPath();
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            ReaderBookmarkCornerMarker.IsVisible = false;
            return;
        }
        var tolerance = _readerLayout.FlowMode == 1 ? 8 : 4;
        var position = (int)Math.Round(_readerScrollPosition);
        var isBookmarked = ReaderBookmarks.Any(bookmark =>
            string.Equals(bookmark.ChapterPath, currentPath, StringComparison.OrdinalIgnoreCase)
            && (bookmark.ScrollPosition is int savedPosition
                ? Math.Abs(savedPosition - position) <= tolerance
                : true));
        ReaderBookmarkCornerMarker.IsVisible = isBookmarked;
        await Task.CompletedTask;
    }

    private async Task NavigateToReaderBookmarkAsync(ReaderBookmark bookmark)
    {
        if (_readerIsPdf)
        {
            await NavigatePdfPageAsync(bookmark.ChapterIndex + 1, ReaderToken);
            await UpdateReaderBookmarkIndicatorAsync();
            return;
        }
        if (_readerDocument is null) return;
        var path = Path.GetFullPath(Path.Combine(
            _readerDocument.RootPath,
            bookmark.ChapterPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsPathInside(_readerDocument.RootPath, path) || !File.Exists(path)) return;
        var target = new Uri(path);
        if (!string.IsNullOrWhiteSpace(bookmark.Fragment))
            target = new Uri(target.AbsoluteUri + "#" + Uri.EscapeDataString(bookmark.Fragment.TrimStart('#')));
        await NavigateToReaderItemAsync(
            new EpubReaderNavigationItem(bookmark.Title, target.AbsoluteUri, bookmark.ChapterIndex),
            ReaderToken);
        if (bookmark.ScrollPosition is { } position && CurrentReaderHost is { } host)
        {
            _readerScrollPosition = Math.Max(0, position);
            var left = _readerLayout.FlowMode == 1 ? position : 0;
            var top = _readerLayout.FlowMode == 1 ? 0 : position;
            await host.InvokeScriptAsync($"window.scrollTo({{ left: {left}, top: {top}, behavior: 'instant' }});");
        }
        await UpdateReaderBookmarkIndicatorAsync();
    }

    private async void ReaderTocSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var sequence = ++_readerWholeSearchSequence;
        await Task.Delay(160);
        if (sequence != _readerWholeSearchSequence) return;
        await RefreshReaderWholeSearchAsync(ReaderTocSearchBox.Text?.Trim() ?? string.Empty, sequence);
    }

    private async Task RefreshReaderWholeSearchAsync(string query, int? sequence = null)
    {
        if (sequence is not null && sequence.Value != _readerWholeSearchSequence) return;
        ReaderSearchResults.Clear();
        if (query.Length == 0)
        {
            ReaderWholeSearchCountText.Text = string.Empty;
            ReaderSearchStatusText.IsVisible = true;
            ReaderSearchStatusText.Text = "输入关键词，实时搜索整本书。";
            ReaderSearchResultList.IsVisible = false;
            return;
        }

        try
        {
            ReaderSearchStatusText.IsVisible = false;
            ReaderSearchStatusText.Text = "正在本地搜索…";
            ReaderSearchResultList.IsVisible = false;
            if (_readerIsPdf)
            {
                var results = PdfTextService.Search(_readerPdfPages, query, int.MaxValue);
                foreach (var result in results)
                    ReaderSearchResults.Add(new ReaderSearchResultViewModel(
                        $"第 {result.PageNumber} 页",
                        result.Excerpt,
                        result.PageNumber - 1,
                        $"pdf:page:{result.PageNumber}",
                        pageNumber: result.PageNumber,
                        query: query));
                ReaderWholeSearchCountText.Text = $"全书 {ReaderSearchResults.Count} 条结果 · PDF 本地文本索引";
            }
            else if (_readerBookCard is not null && _readerBookFile is not null && _readerDocument is not null)
            {
                ReaderAiStatusText.Text = "正在准备本地全文索引…";
                await _bookContent.EnsureIndexedAsync(_readerBookCard.Book, _readerBookFile, _readerDocument, ReaderToken);
                var results = await _readerData.SearchBookAsync(
                    _readerBookCard.Book.Id,
                    query,
                    int.MaxValue,
                    ReaderToken);
                // The same visible excerpt can come from duplicate EPUB spine
                // entries or legacy chunks with different paths/offsets. At this
                // final presentation boundary, identical title + snippet means an
                // identical user-facing result and must only be shown once.
                var distinct = results
                    .Select(result => new
                    {
                        result,
                        Title = result.ChapterTitle,
                        Snippet = CreateSearchExcerpt(result.Content, query)
                    })
                    .DistinctBy(
                        item => $"{item.Title}\u001f{item.Snippet}",
                        StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
                foreach (var item in distinct)
                    ReaderSearchResults.Add(new ReaderSearchResultViewModel(
                        item.Title,
                        item.Snippet,
                        item.result.ChapterIndex,
                        item.result.ChapterPath,
                        new Uri(Path.GetFullPath(Path.Combine(_readerDocument.RootPath, item.result.ChapterPath.Replace('/', Path.DirectorySeparatorChar)))).AbsoluteUri,
                        query: query));
                ReaderWholeSearchCountText.Text = $"全书 {ReaderSearchResults.Count} 段结果";
            }
            ReaderSearchResultList.IsVisible = ReaderSearchResults.Count > 0;
            ReaderSearchStatusText.IsVisible = ReaderSearchResults.Count == 0;
            ReaderSearchStatusText.Text = _readerIsPdf ? "没有找到匹配的内容。" : "没有找到匹配的片段。";
        }
        catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReaderWholeSearchCountText.Text = $"搜索失败：{exception.Message}";
            ReaderSearchStatusText.IsVisible = false;
            ReaderSearchResultList.IsVisible = false;
        }
    }

    private async void ReaderSearchResultButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ReaderSearchResultViewModel result }) return;
        if (result.PageNumber is { } page)
        {
            await NavigatePdfPageAsync(page, ReaderToken);
            return;
        }
        if (_readerDocument is null || string.IsNullOrWhiteSpace(result.Target)) return;
        ReaderSearchStatusText.Text = "正在跳转并定位关键词…";
        await NavigateToReaderItemAsync(
            new EpubReaderNavigationItem(result.Title, result.Target, result.ChapterIndex),
            ReaderToken);
        if (!string.IsNullOrWhiteSpace(result.Query))
        {
            var sequence = ++_readerSearchSequence;
            await ApplyReaderSearchAsync(result.Query, sequence);
        }
        ReaderSearchStatusText.Text = $"已跳转到《{result.Title}》相关位置。";
    }

    private async void ReaderInPageSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var sequence = _readerIsPdf
            ? ++_readerPdfSearchSequence
            : ++_readerSearchSequence;
        await Task.Delay(120);
        if (_readerIsPdf
            ? sequence != _readerPdfSearchSequence
            : sequence != _readerSearchSequence)
            return;
        var query = ReaderInPageSearchBox.Text?.Trim() ?? string.Empty;
        if (_readerIsPdf)
            await ApplyReaderPdfSearchAsync(query, sequence);
        else
            await ApplyReaderSearchAsync(query, sequence);
        ReaderInPageSearchCountText.Text = _readerSearchCount <= 0
            ? "0/0"
            : $"{_readerSearchIndex + 1}/{_readerSearchCount}";
    }

    private async void ReaderInPageSearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            await ReaderInPageSearchCloseAsync();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await NavigateReaderSearchAsync(_readerSearchIndex + ((e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1));
            ReaderInPageSearchCountText.Text = _readerSearchCount <= 0 ? "0/0" : $"{_readerSearchIndex + 1}/{_readerSearchCount}";
        }
    }

    private async void ReaderInPageSearchPreviousButton_Click(object? sender, RoutedEventArgs e)
    {
        await NavigateReaderSearchAsync(_readerSearchIndex - 1);
        ReaderInPageSearchCountText.Text = _readerSearchCount <= 0 ? "0/0" : $"{_readerSearchIndex + 1}/{_readerSearchCount}";
    }

    private async void ReaderInPageSearchNextButton_Click(object? sender, RoutedEventArgs e)
    {
        await NavigateReaderSearchAsync(_readerSearchIndex + 1);
        ReaderInPageSearchCountText.Text = _readerSearchCount <= 0 ? "0/0" : $"{_readerSearchIndex + 1}/{_readerSearchCount}";
    }

    private async void ReaderInPageSearchCloseButton_Click(object? sender, RoutedEventArgs e)
        => await ReaderInPageSearchCloseAsync();

    private async Task ReaderInPageSearchCloseAsync()
    {
        await ClearReaderSearchAsync();
        ReaderInPageSearchBar.IsVisible = false;
        ReaderInPageSearchBox.Text = string.Empty;
    }

    private async Task ApplyReaderPdfSearchAsync(string query, int sequence)
    {
        if (!_readerIsPdf || CurrentReaderHost is not { } host) return;
        var serialized = JsonSerializer.Serialize(query);
        var script = """
            (() => {
              for (const mark of Array.from(document.querySelectorAll('mark.kkindle-page-find-hit'))) {
                const parent = mark.parentNode; if (!parent) continue;
                parent.replaceChild(document.createTextNode(mark.textContent || ''), mark); parent.normalize?.();
              }
              const query = (__QUERY__ || '').trim(); if (!query) return 0;
              const folded = query.toLocaleLowerCase(); const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT); const matches = [];
              while (walker.nextNode()) { const node = walker.currentNode; const parent = node.parentElement;
                if (!parent || ['SCRIPT','STYLE','MARK'].includes(parent.tagName)) continue;
                const text = node.data || ''; let start = text.toLocaleLowerCase().indexOf(folded);
                while (start >= 0) { matches.push({ node, start }); start = text.toLocaleLowerCase().indexOf(folded, start + Math.max(1, folded.length)); }
              }
              for (let index = matches.length - 1; index >= 0; index--) { const match = matches[index]; const range = document.createRange();
                range.setStart(match.node, match.start); range.setEnd(match.node, match.start + query.length);
                const mark = document.createElement('mark'); mark.className = 'kkindle-page-find-hit'; range.surroundContents(mark); }
              return matches.length;
            })()
            """.Replace("__QUERY__", serialized, StringComparison.Ordinal);
        var result = await host.InvokeScriptAsync(script);
        if (sequence != _readerPdfSearchSequence) return;
        _readerSearchCount = ParseScriptInt(result);
        _readerSearchIndex = _readerSearchCount > 0 ? 0 : -1;
        await NavigateReaderSearchAsync(_readerSearchIndex);
    }

    private void ReaderProgressSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_readerProgressSliderUpdating || !IsInitialized) return;
        _ = ObserveReaderTaskAsync(NavigateReaderProgressAsync(e.NewValue));
    }

    private async Task NavigateReaderProgressAsync(double value)
    {
        var percent = Math.Clamp(value, 0, 100);
        if (_readerIsPdf)
        {
            var page = Math.Clamp((int)Math.Round(percent / 100d * Math.Max(0, _readerPdfPages.Count - 1)) + 1, 1, Math.Max(1, _readerPdfPages.Count));
            await NavigatePdfPageAsync(page, ReaderToken);
            return;
        }
        if (_readerDocument is null || _readerDocument.Chapters.Count == 0) return;
        var position = percent / 100d * _readerDocument.Chapters.Count;
        var chapter = Math.Clamp((int)Math.Floor(position), 0, _readerDocument.Chapters.Count - 1);
        var target = new Uri(_readerDocument.Chapters[chapter]);
        await NavigateToReaderItemAsync(
            new EpubReaderNavigationItem($"第 {chapter + 1} 章", target.AbsoluteUri, chapter),
            ReaderToken);
    }

    private void ReaderLayoutSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        ReaderFontScaleSlider.Value = _readerLayout.FontScale;
        ReaderLineHeightSlider.Value = _readerLayout.LineHeight;
        ReaderMaxWidthSlider.Value = _readerLayout.MaxWidth;
        ReaderBodyPaddingSlider.Value = _readerLayout.BodyPadding;
        ReaderVerticalWritingCheck.IsChecked = _readerLayout.VerticalWriting;
        SelectReaderFontFamily(_readerLayout.FontFamily);
        SelectReaderFlowMode(_readerLayout.FlowMode, _readerLayout.TwoPageMode);
        SelectReaderPageAnimation(_readerPageAnimation);
        UpdateReaderLayoutSliderLabels();
        UpdateReaderLayoutStatus();
        ReaderLayoutSettingsOverlay.IsVisible = true;
    }

    // ValueChanged / SelectionChanged can fire while XAML is still being
    // parsed (slider Minimum/Maximum/Value assignment clamps the value and
    // raises the event before sibling controls exist). Guard every layout
    // event handler so a partially-initialized panel can never be touched.
    private bool AreReaderLayoutControlsReady() =>
        ReaderFontScaleSlider is not null
        && ReaderLineHeightSlider is not null
        && ReaderMaxWidthSlider is not null
        && ReaderBodyPaddingSlider is not null
        && ReaderFontFamilyBox is not null
        && ReaderVerticalWritingCheck is not null;

    private bool _suppressReaderLayoutChange;
    private CancellationTokenSource? _readerLayoutApplyCancellation;

    private void ReaderLayoutSettingChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressReaderLayoutChange || !AreReaderLayoutControlsReady()) return;
        UpdateReaderLayoutSliderLabels();
        UpdateReaderLayoutStatus();
        ScheduleReaderLayoutApply();
    }

    private void ReaderFontFamilyBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressReaderLayoutChange || !AreReaderLayoutControlsReady()) return;
        UpdateReaderLayoutStatus();
        ScheduleReaderLayoutApply();
    }

    private void ReaderFlowModeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressReaderLayoutChange || !AreReaderLayoutControlsReady()) return;
        UpdateReaderLayoutStatus();
        ScheduleReaderLayoutApply();
    }

    private void ReaderPageAnimationBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressReaderLayoutChange || !AreReaderLayoutControlsReady()) return;
        _readerPageAnimation = GetSelectedReaderPageAnimation();
        SyncReaderAnimationMenu();
    }

    private void ReaderVerticalWritingCheck_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderLayoutChange || !AreReaderLayoutControlsReady()) return;
        UpdateReaderLayoutStatus();
        ScheduleReaderLayoutApply();
    }

    private void UpdateReaderLayoutStatus()
    {
        var (flowMode, twoPageMode) = GetSelectedReaderFlowMode();
        ReaderLayoutSettingsStatusText.Text = twoPageMode && flowMode != 1
            ? "双页仅用于分页模式；当前模式下暂不生效。"
            : "设置立即生效，并保存在本机。";
    }

    private void ScheduleReaderLayoutApply()
    {
        _readerLayoutApplyCancellation?.Cancel();
        _readerLayoutApplyCancellation?.Dispose();
        _readerLayoutApplyCancellation = new CancellationTokenSource();
        var token = _readerLayoutApplyCancellation.Token;
        _ = Task.Run(async () =>
        {
            await Task.Delay(160);
            if (token.IsCancellationRequested) return;
            Dispatcher.UIThread.Post(async () =>
            {
                if (token.IsCancellationRequested) return;
                try
                {
                    _readerLayout = ReaderLayoutDefaults.Normalize(ReadReaderLayoutFromControls());
                    _readerPageAnimation = GetSelectedReaderPageAnimation();
                    UpdateReaderZoomLabel();
                    await ApplyReaderLayoutToHostsAsync(_readerSessionCancellation?.Token ?? CancellationToken.None);
                    await SaveReaderLayoutAsync(CancellationToken.None);
                }
                catch
                {
                }
            });
        });
    }

    private ReaderLayoutSettings ReadReaderLayoutFromControls() => new(
        ReaderFontScaleSlider.Value,
        ReaderLineHeightSlider.Value,
        ReaderMaxWidthSlider.Value,
        ReaderBodyPaddingSlider.Value,
        (ReaderFontFamilyBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty,
        GetSelectedReaderFlowMode().FlowMode,
        ReaderVerticalWritingCheck.IsChecked == true,
        GetSelectedReaderFlowMode().TwoPageMode);

    private void ReaderLayoutSettingsCloseButton_Click(object? sender, RoutedEventArgs e)
        => ReaderLayoutSettingsOverlay.IsVisible = false;

    private async void ReaderLayoutResetButton_Click(object? sender, RoutedEventArgs e)
    {
        _suppressReaderLayoutChange = true;
        try
        {
            ReaderFontScaleSlider.Value = ReaderLayoutDefaults.DefaultFontScale;
            ReaderLineHeightSlider.Value = ReaderLayoutDefaults.DefaultLineHeight;
            ReaderMaxWidthSlider.Value = ReaderLayoutDefaults.DefaultMaxWidth;
            ReaderBodyPaddingSlider.Value = ReaderLayoutDefaults.DefaultBodyPadding;
            ReaderVerticalWritingCheck.IsChecked = false;
            SelectReaderFontFamily(ReaderFontDefaults.DefaultFamily);
            SelectReaderFlowMode(1, false);
            SelectReaderPageAnimation(ReaderAnimationFade);
        }
        finally
        {
            _suppressReaderLayoutChange = false;
        }
        UpdateReaderLayoutSliderLabels();
        _readerLayout = ReaderLayoutDefaults.Normalize(ReadReaderLayoutFromControls());
        _readerPageAnimation = ReaderAnimationFade;
        UpdateReaderZoomLabel();
        await ApplyReaderLayoutToHostsAsync(ReaderToken);
        await SaveReaderLayoutAsync(CancellationToken.None);
        ReaderLayoutSettingsStatusText.Text = "已恢复默认排版。";
    }

    private void UpdateReaderLayoutSliderLabels()
    {
        ReaderFontScaleValueText.Text = $"{ReaderFontScaleSlider.Value:0.00}×";
        ReaderLineHeightValueText.Text = ReaderLineHeightSlider.Value.ToString("0.00");
        ReaderMaxWidthValueText.Text = $"{ReaderMaxWidthSlider.Value:0} px";
        ReaderBodyPaddingValueText.Text = $"{ReaderBodyPaddingSlider.Value:0} px";
    }

    private void SelectReaderFontFamily(string family)
    {
        var item = ReaderFontFamilyBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, family, StringComparison.OrdinalIgnoreCase));
        ReaderFontFamilyBox.SelectedItem = item ?? ReaderFontFamilyBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private (int FlowMode, bool TwoPageMode) GetSelectedReaderFlowMode()
    {
        var tag = (ReaderFlowModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return tag switch
        {
            "scroll" => (0, false),
            "double" => (1, true),
            _ => (1, false)
        };
    }

    private void SelectReaderFlowMode(int flowMode, bool twoPageMode)
    {
        var tag = flowMode == 0 ? "scroll" : twoPageMode ? "double" : "single";
        ReaderFlowModeBox.SelectedItem = ReaderFlowModeBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal));
    }

    private int GetSelectedReaderPageAnimation()
    {
        var tag = (ReaderPageAnimationBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return tag switch
        {
            "none" => ReaderAnimationNone,
            "slide" => ReaderAnimationSlide,
            "wave" => ReaderAnimationWave,
            _ => ReaderAnimationFade
        };
    }

    private void SelectReaderPageAnimation(int animation)
    {
        var tag = animation switch
        {
            ReaderAnimationNone => "none",
            ReaderAnimationSlide => "slide",
            ReaderAnimationWave => "wave",
            _ => "fade"
        };
        ReaderPageAnimationBox.SelectedItem = ReaderPageAnimationBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal));
    }

    private void ReaderZenMinimalTocButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_readerZenMode) return;
        _readerTocExpanded = false;
        _readerTocMinimal = !_readerTocMinimal;
        ApplyReaderPanelLayout();
        UpdateReaderZenTocToggle();
    }

    private void ReaderExitZenButton_Click(object? sender, RoutedEventArgs e) => ExitReaderZenMode();

    private void ReaderAssistantToggleButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_readerZenMode) return;
        var visible = !ReaderAssistantPanel.IsVisible;
        ReaderAssistantPanel.IsVisible = visible;
        ReaderBodyGrid.ColumnDefinitions[2].Width = visible ? new GridLength(330) : new GridLength(0);
    }

    private static string CreateSearchExcerpt(string content, string query)
    {
        var text = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var index = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (index < 0) return text.Length <= 180 ? text : text[..180] + "…";
        var start = Math.Max(0, index - 65);
        var end = Math.Min(text.Length, index + query.Length + 115);
        return (start > 0 ? "…" : string.Empty) + text[start..end] + (end < text.Length ? "…" : string.Empty);
    }
}
