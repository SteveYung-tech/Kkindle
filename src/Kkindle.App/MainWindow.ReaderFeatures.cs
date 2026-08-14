using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
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
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 && e.Key == Key.F)
        {
            e.Handled = true;
            if (_readerIsPdf)
            {
                ReaderTocPanel.IsVisible = true;
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
            ReaderTocPanel.IsVisible = false;
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
                .kkindle-pdf-page { max-width: 900px; margin: 0 auto 34px; padding: 34px 42px; border: 1px solid #e1e1dd; background: #fff; box-shadow: 0 2px 12px rgba(0,0,0,.04); }
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
                annotation.Color,
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
        foreach (var bookmark in bookmarks) ReaderBookmarks.Add(bookmark);
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
    }

    private void ShowReaderBookmarkTab()
    {
        ReaderTocView.IsVisible = false;
        ReaderBookmarkPane.IsVisible = true;
        ReaderSearchPanel.IsVisible = false;
        ReaderBookmarkEmptyText.IsVisible = ReaderBookmarks.Count == 0;
    }

    private void ShowReaderSearchTab()
    {
        ReaderTocView.IsVisible = false;
        ReaderBookmarkPane.IsVisible = false;
        ReaderSearchPanel.IsVisible = true;
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
        ReaderTocPanel.IsVisible = true;
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
        ReaderStatusText.Text = "书签已删除";
    }

    private async Task ToggleReaderBookmarkAsync()
    {
        if (_readerBookCard is null || _readerBookFile is null) return;
        var currentPath = _readerIsPdf
            ? $"pdf:page:{_readerPdfPage}"
            : GetReaderChapterPath();
        if (string.IsNullOrWhiteSpace(currentPath)) return;

        var existing = ReaderBookmarks.FirstOrDefault(bookmark =>
            bookmark.ChapterIndex == _readerChapterIndex
            && string.Equals(bookmark.ChapterPath, currentPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(bookmark.Quote, _readerPendingSelection ?? string.Empty, StringComparison.Ordinal));
        if (existing is not null)
        {
            await _readerData.DeleteBookmarkAsync(existing.Id, ReaderToken);
            ReaderStatusText.Text = "已取消当前书签";
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
                Quote = _readerPendingSelection ?? string.Empty
            }, ReaderToken);
            ReaderStatusText.Text = "已添加书签";
        }
        await RefreshReaderBookmarksAsync(ReaderToken);
    }

    private async Task NavigateToReaderBookmarkAsync(ReaderBookmark bookmark)
    {
        if (_readerIsPdf)
        {
            await NavigatePdfPageAsync(bookmark.ChapterIndex + 1, ReaderToken);
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
            return;
        }

        try
        {
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
                foreach (var result in results)
                    ReaderSearchResults.Add(new ReaderSearchResultViewModel(
                        result.ChapterTitle,
                        CreateSearchExcerpt(result.Content, query),
                        result.ChapterIndex,
                        result.ChapterPath,
                        new Uri(Path.GetFullPath(Path.Combine(_readerDocument.RootPath, result.ChapterPath.Replace('/', Path.DirectorySeparatorChar)))).AbsoluteUri,
                        query: query));
            }
            ReaderWholeSearchCountText.Text = $"找到 {ReaderSearchResults.Count} 个结果";
        }
        catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReaderWholeSearchCountText.Text = $"搜索失败：{exception.Message}";
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
        await NavigateToReaderItemAsync(
            new EpubReaderNavigationItem(result.Title, result.Target, result.ChapterIndex),
            ReaderToken);
        if (!string.IsNullOrWhiteSpace(result.Query))
        {
            var sequence = ++_readerSearchSequence;
            await ApplyReaderSearchAsync(result.Query, sequence);
        }
    }

    private async void ReaderInPageSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var sequence = _readerIsPdf
            ? ++_readerPdfSearchSequence
            : ++_readerSearchSequence;
        await Task.Delay(120);
        if (sequence != _readerPdfSearchSequence) return;
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

    private void ReaderMoreButton_Click(object? sender, RoutedEventArgs e)
        => ReaderLayoutSettingsButton_Click(sender, e);

    private void ReaderLayoutSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        ReaderFontScaleSlider.Value = _readerLayout.FontScale;
        ReaderLineHeightSlider.Value = _readerLayout.LineHeight;
        ReaderMaxWidthSlider.Value = _readerLayout.MaxWidth;
        ReaderBodyPaddingSlider.Value = _readerLayout.BodyPadding;
        ReaderVerticalWritingCheck.IsChecked = _readerLayout.VerticalWriting;
        SelectReaderFontFamily(_readerLayout.FontFamily);
        UpdateReaderLayoutSliderLabels();
        ReaderLayoutSettingsStatusText.Text = "修改只会作用于当前这本书。";
        ReaderLayoutSettingsOverlay.IsVisible = true;
    }

    private async void ReaderLayoutSettingsApplyButton_Click(object? sender, RoutedEventArgs e)
    {
        var fontFamily = (ReaderFontFamilyBox.SelectedItem as ComboBoxItem)?.Tag as string
            ?? _readerLayout.FontFamily;
        _readerLayout = ReaderLayoutDefaults.Normalize(new ReaderLayoutSettings(
            ReaderFontScaleSlider.Value,
            ReaderLineHeightSlider.Value,
            ReaderMaxWidthSlider.Value,
            ReaderBodyPaddingSlider.Value,
            fontFamily,
            _readerLayout.FlowMode,
            ReaderVerticalWritingCheck.IsChecked == true,
            _readerLayout.TwoPageMode));
        await ApplyReaderLayoutToHostsAsync(ReaderToken);
        await SaveReaderLayoutAsync(CancellationToken.None);
        ReaderLayoutSettingsOverlay.IsVisible = false;
        ReaderStatusText.Text = "阅读排版已应用";
    }

    private void ReaderLayoutSettingsCloseButton_Click(object? sender, RoutedEventArgs e)
        => ReaderLayoutSettingsOverlay.IsVisible = false;

    private void ReaderLayoutResetButton_Click(object? sender, RoutedEventArgs e)
    {
        ReaderFontScaleSlider.Value = ReaderLayoutDefaults.DefaultFontScale;
        ReaderLineHeightSlider.Value = ReaderLayoutDefaults.DefaultLineHeight;
        ReaderMaxWidthSlider.Value = ReaderLayoutDefaults.DefaultMaxWidth;
        ReaderBodyPaddingSlider.Value = ReaderLayoutDefaults.DefaultBodyPadding;
        ReaderVerticalWritingCheck.IsChecked = false;
        SelectReaderFontFamily(ReaderFontDefaults.DefaultFamily);
        UpdateReaderLayoutSliderLabels();
        ReaderLayoutSettingsStatusText.Text = "已恢复默认值，点击“应用”后生效。";
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

    private void ReaderZenMinimalTocButton_Click(object? sender, RoutedEventArgs e)
    {
        ReaderTocPanel.IsVisible = !ReaderTocPanel.IsVisible;
        if (ReaderTocPanel.IsVisible)
        {
            ShowReaderTocTab();
            ReaderTocList.SelectedIndex = FindReaderTocIndexForChapter(_readerChapterIndex);
        }
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
