using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public sealed partial class MainWindow
{
    private readonly ReaderDataService _readerData;
    private readonly EpubBookContentService _bookContent;
    private readonly EpubFootnoteResolver _footnotes;
    private readonly AiSettingsStore _aiSettingsStore;
    private readonly AiChatClient _aiChatClient;
    private readonly ObservableCollection<ReaderAnnotation> _readerAnnotations = [];
    private readonly List<AiConversationTurn> _readerAiConversation = [];
    private CancellationTokenSource? _readerFeatureCancellation;
    private CancellationTokenSource? _readerAiCancellation;
    private Task? _readerIndexTask;
    private Book? _readerBook;
    private BookFile? _readerBookFile;
    private EpubReaderDocument? _readerDocument;
    private AiConnectionSettings _readerAiSettings = new();
    private ReaderSelectionAnchor? _pendingReaderSelection;
    private Guid? _pendingReaderAnnotationScroll;
    private int? _pendingReaderChunkOffset;
    private bool _readerIndexAvailable;
    private bool _readerAiBusy;
    private bool _suppressAiProviderChange;
    private Popup? _readerAssistantPopup;
    private Popup? _readerSettingsPopup;

    private void ConfigureReaderFeatureHosts()
    {
        ReaderPane.Children.Remove(ReaderAssistantPanel);
        ReaderAssistantPanel.Margin = new Thickness(0);
        ReaderAssistantPanel.Width = 360;
        ReaderAssistantPanel.Visibility = Visibility.Visible;
        _readerAssistantPopup = new Popup
        {
            Child = ReaderAssistantPanel,
            IsLightDismissEnabled = false,
            IsOpen = false
        };

        ReaderPane.Children.Remove(ReaderAiSettingsOverlay);
        ReaderAiSettingsOverlay.Margin = new Thickness(0);
        ReaderAiSettingsOverlay.Visibility = Visibility.Visible;
        _readerSettingsPopup = new Popup
        {
            Child = ReaderAiSettingsOverlay,
            IsLightDismissEnabled = false,
            IsOpen = false
        };
    }

    private void UpdateReaderAssistantPopup(bool visible)
    {
        if (_readerAssistantPopup is null || RootGrid.XamlRoot is null) return;
        var viewport = RootGrid.XamlRoot.Size;
        _readerAssistantPopup.XamlRoot = RootGrid.XamlRoot;
        _readerAssistantPopup.HorizontalOffset = Math.Max(0, viewport.Width - 360);
        _readerAssistantPopup.VerticalOffset = 38;
        ReaderAssistantPanel.Width = Math.Min(360, viewport.Width);
        ReaderAssistantPanel.Height = Math.Max(0, viewport.Height - 38);
        _readerAssistantPopup.IsOpen = visible;
    }

    private void SetReaderAiSettingsVisible(bool visible)
    {
        if (_readerSettingsPopup is null) return;
        if (visible && RootGrid.XamlRoot is not null)
        {
            var viewport = RootGrid.XamlRoot.Size;
            _readerSettingsPopup.XamlRoot = RootGrid.XamlRoot;
            _readerSettingsPopup.HorizontalOffset = 0;
            _readerSettingsPopup.VerticalOffset = 38;
            ReaderAiSettingsOverlay.Width = viewport.Width;
            ReaderAiSettingsOverlay.Height = Math.Max(0, viewport.Height - 38);
        }
        _readerSettingsPopup.IsOpen = visible;
    }

    private void BeginReaderSession(Book book, BookFile file)
    {
        _readerFeatureCancellation?.Cancel();
        _readerFeatureCancellation?.Dispose();
        _readerFeatureCancellation = new CancellationTokenSource();
        _readerBook = book;
        _readerBookFile = file;
        _readerDocument = null;
        _readerIndexTask = null;
        _readerIndexAvailable = false;
        _pendingReaderAnnotationScroll = null;
        _pendingReaderChunkOffset = null;
        ResetReaderFeatures();
    }

    private async Task LoadReaderSessionDataAsync(CancellationToken cancellationToken)
    {
        _readerAiSettings = await _aiSettingsStore.LoadAsync(cancellationToken);
        UpdateReaderAiHeader();
        if (_readerBookFile is null) return;
        var annotations = await _readerData.GetAnnotationsAsync(_readerBookFile.Id, cancellationToken);
        ReplaceReaderAnnotations(annotations);
    }

    private void EndReaderSession()
    {
        _readerAiCancellation?.Cancel();
        _readerAiCancellation?.Dispose();
        _readerAiCancellation = null;
        _readerFeatureCancellation?.Cancel();
        _readerFeatureCancellation?.Dispose();
        _readerFeatureCancellation = null;
        _readerBook = null;
        _readerBookFile = null;
        _readerDocument = null;
        _readerIndexTask = null;
        _pendingReaderSelection = null;
        _pendingReaderAnnotationScroll = null;
        _pendingReaderChunkOffset = null;
        _readerIndexAvailable = false;
    }

    private void ResetReaderFeatures()
    {
        _readerAiCancellation?.Cancel();
        _readerAiCancellation?.Dispose();
        _readerAiCancellation = null;
        _readerAiBusy = false;
        _pendingReaderSelection = null;
        _readerAiConversation.Clear();
        _readerAnnotations.Clear();
        ReaderAnnotationList.ItemsSource = _readerAnnotations;
        ReaderAnnotationList.SelectedItem = null;
        ReaderDeleteAnnotationButton.IsEnabled = false;
        ReaderAnnotationSelectionText.Text = "请先在正文中选择一段文字，再点击顶部“批注”。";
        ReaderAnnotationNoteBox.Text = string.Empty;
        ReaderChatMessagesPanel.Children.Clear();
        ReaderAiEmptyState.Visibility = Visibility.Visible;
        ReaderAiQuestionBox.Text = string.Empty;
        ReaderAiSendButton.IsEnabled = true;
        SetReaderAiSettingsVisible(false);
        ReaderAiStatusText.Text = "本地索引将在打开 EPUB 后自动准备";
        ResetReaderAiSources();
        ShowReaderAiTab();
        UpdateReaderAiHeader();
    }

    private void StartReaderIndexing()
    {
        if (_readerBook is null || _readerBookFile is null || _readerDocument is null || _readerFeatureCancellation is null)
            return;
        _readerIndexTask = IndexCurrentBookAsync(
            _readerBook,
            _readerBookFile,
            _readerDocument,
            _readerFeatureCancellation.Token);
    }

    private async Task IndexCurrentBookAsync(
        Book book,
        BookFile file,
        EpubReaderDocument document,
        CancellationToken cancellationToken)
    {
        ReaderAiStatusText.Text = "正在提取章节并准备本地索引…";
        try
        {
            var indexed = await _bookContent.EnsureIndexedAsync(book, file, document, cancellationToken);
            _readerIndexAvailable = true;
            ReaderAiStatusText.Text = indexed > 0
                ? $"本地索引已就绪 · 新建 {indexed:N0} 个片段"
                : "本地索引已就绪 · 已使用缓存";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _readerIndexAvailable = false;
            ReaderAiStatusText.Text = $"本地索引失败：{exception.Message}";
        }
    }

    private void SetReaderIndexUnavailable(string message)
    {
        _readerIndexAvailable = false;
        _readerIndexTask = null;
        ReaderAiStatusText.Text = message;
    }

    private async void ReaderHighlightButton_Click(object sender, RoutedEventArgs e)
    {
        var selection = await CaptureReaderSelectionAsync();
        if (selection is null)
        {
            ReaderStatusText.Text = "请先在正文中选择一段文字";
            return;
        }

        await SaveReaderAnnotationAsync(selection, string.Empty, preserveExistingNote: true);
    }

    private async void ReaderAnnotateButton_Click(object sender, RoutedEventArgs e)
    {
        ShowReaderNotesTab();
        var selection = await CaptureReaderSelectionAsync();
        if (selection is null)
        {
            ReaderAnnotationSelectionText.Text = "没有检测到选中文字。请回到正文选择一段文字后重试。";
            return;
        }

        _pendingReaderSelection = selection;
        ReaderAnnotationSelectionText.Text = selection.Text;
        ReaderAnnotationNoteBox.Text = string.Empty;
        ReaderAnnotationNoteBox.Focus(FocusState.Programmatic);
    }

    private async void ReaderAnnotationSaveButton_Click(object sender, RoutedEventArgs e)
    {
        var selection = _pendingReaderSelection ?? await CaptureReaderSelectionAsync();
        if (selection is null)
        {
            ReaderAnnotationSelectionText.Text = "请先在正文中选择一段文字。";
            return;
        }

        await SaveReaderAnnotationAsync(
            selection,
            ReaderAnnotationNoteBox.Text.Trim(),
            preserveExistingNote: false);
        _pendingReaderSelection = null;
        ReaderAnnotationNoteBox.Text = string.Empty;
        ReaderAnnotationSelectionText.Text = "批注已保存。继续选择正文即可添加下一条。";
    }

    private async Task SaveReaderAnnotationAsync(
        ReaderSelectionAnchor selection,
        string note,
        bool preserveExistingNote)
    {
        if (_readerBook is null || _readerBookFile is null || _readerFeatureCancellation is null) return;
        var chapterPath = GetCurrentReaderChapterPath();
        if (chapterPath is null)
        {
            ReaderStatusText.Text = "当前页面无法保存批注";
            return;
        }

        var exact = _readerAnnotations.FirstOrDefault(annotation =>
            annotation.ChapterPath.Equals(chapterPath, StringComparison.OrdinalIgnoreCase)
            && annotation.StartOffset == selection.StartOffset
            && annotation.EndOffset == selection.EndOffset);
        var overlaps = _readerAnnotations.Any(annotation =>
            annotation.Id != exact?.Id
            && annotation.ChapterPath.Equals(chapterPath, StringComparison.OrdinalIgnoreCase)
            && selection.StartOffset < annotation.EndOffset
            && selection.EndOffset > annotation.StartOffset);
        if (overlaps)
        {
            ReaderStatusText.Text = "这段文字与已有划线重叠，请缩小选择范围";
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var annotation = exact ?? new ReaderAnnotation
        {
            Id = Guid.NewGuid(),
            BookId = _readerBook.Id,
            BookFileId = _readerBookFile.Id,
            CreatedAt = now
        };
        annotation.ChapterPath = chapterPath;
        annotation.Fragment = ReaderWebView.Source?.Fragment.TrimStart('#');
        annotation.StartOffset = selection.StartOffset;
        annotation.EndOffset = selection.EndOffset;
        annotation.SelectedText = selection.Text;
        annotation.Prefix = selection.Prefix;
        annotation.Suffix = selection.Suffix;
        annotation.Color = "#000000";
        annotation.Note = preserveExistingNote && string.IsNullOrWhiteSpace(note) && exact is not null
            ? exact.Note
            : note;
        annotation.UpdatedAt = now;

        try
        {
            await _readerData.SaveAnnotationAsync(annotation, _readerFeatureCancellation.Token);
            var annotations = await _readerData.GetAnnotationsAsync(_readerBookFile.Id, _readerFeatureCancellation.Token);
            ReplaceReaderAnnotations(annotations);
            await ApplyReaderAnnotationsToPageAsync();
            await ClearReaderSelectionAsync();
            ReaderStatusText.Text = string.IsNullOrWhiteSpace(annotation.Note)
                ? "划线已保存"
                : "批注已保存";
        }
        catch (OperationCanceledException) when (_readerFeatureCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReaderStatusText.Text = $"保存批注失败：{exception.Message}";
        }
    }

    private void ReplaceReaderAnnotations(IEnumerable<ReaderAnnotation> annotations)
    {
        _readerAnnotations.Clear();
        foreach (var annotation in annotations.OrderBy(item => item.ChapterPath).ThenBy(item => item.StartOffset))
            _readerAnnotations.Add(annotation);
    }

    private async Task<ReaderSelectionAnchor?> CaptureReaderSelectionAsync()
    {
        if (_readerAllowedRoot is null || ReaderWebView.CoreWebView2 is null) return null;
        return await ExecuteReaderJsonScriptAsync<ReaderSelectionAnchor>(GetReaderSelectionScript());
    }

    private static string GetReaderSelectionScript() =>
        """
        (() => {
          const selection = window.getSelection ? window.getSelection() : null;
          if (!selection || selection.rangeCount === 0 || selection.isCollapsed) return null;
          const range = selection.getRangeAt(0);
          const root = document.body;
          if (!root || !root.contains(range.commonAncestorContainer)) return null;

          const pointOffset = (container, offset) => {
            const before = document.createRange();
            before.selectNodeContents(root);
            before.setEnd(container, offset);
            return (before.cloneContents().textContent || '').length;
          };

          let text = selection.toString();
          let startOffset = pointOffset(range.startContainer, range.startOffset);
          let endOffset = pointOffset(range.endContainer, range.endOffset);
          const leading = text.length - text.trimStart().length;
          const trailing = text.length - text.trimEnd().length;
          startOffset += leading;
          endOffset -= trailing;
          text = text.trim();
          if (!text || endOffset <= startOffset) return null;
          const fullText = root.textContent || '';
          return {
            text,
            startOffset,
            endOffset,
            prefix: fullText.slice(Math.max(0, startOffset - 72), startOffset),
            suffix: fullText.slice(endOffset, Math.min(fullText.length, endOffset + 72))
          };
        })();
        """;

    private async Task<T?> ExecuteReaderJsonScriptAsync<T>(string script)
    {
        if (ReaderWebView.CoreWebView2 is null) return default;
        try
        {
            var json = await ReaderWebView.CoreWebView2.ExecuteScriptAsync(script);
            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return default;
        }
    }

    private string? GetCurrentReaderChapterPath()
    {
        if (_readerAllowedRoot is null || ReaderWebView.Source is not { IsFile: true } source) return null;
        var fullPath = Path.GetFullPath(source.LocalPath);
        if (!IsPathInside(_readerAllowedRoot, fullPath)) return null;
        return Path.GetRelativePath(_readerAllowedRoot, fullPath).Replace('\\', '/');
    }

    private async Task ApplyReaderAnnotationsToPageAsync()
    {
        var chapterPath = GetCurrentReaderChapterPath();
        if (chapterPath is null || ReaderWebView.CoreWebView2 is null) return;
        var payload = _readerAnnotations
            .Where(annotation => annotation.ChapterPath.Equals(chapterPath, StringComparison.OrdinalIgnoreCase))
            .Select(annotation => new
            {
                id = annotation.Id.ToString("N"),
                startOffset = annotation.StartOffset,
                endOffset = annotation.EndOffset,
                note = annotation.Note
            })
            .OrderByDescending(annotation => annotation.startOffset)
            .ToArray();
        var data = JsonSerializer.Serialize(payload);
        var script = $$"""
            (() => {
              const root = document.body;
              if (!root) return;
              document.querySelectorAll('span[data-kkindle-annotation]').forEach(mark => {
                const parent = mark.parentNode;
                while (mark.firstChild) parent.insertBefore(mark.firstChild, mark);
                parent.removeChild(mark);
                parent.normalize();
              });

              const annotations = {{data}};
              const textNodes = () => {
                const nodes = [];
                const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
                  acceptNode(node) {
                    return node.data.length ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_REJECT;
                  }
                });
                while (walker.nextNode()) nodes.push(walker.currentNode);
                return nodes;
              };

              for (const item of annotations) {
                let cursor = 0;
                const segments = [];
                for (const node of textNodes()) {
                  const nodeStart = cursor;
                  const nodeEnd = cursor + node.data.length;
                  if (item.startOffset < nodeEnd && item.endOffset > nodeStart) {
                    segments.push({
                      node,
                      start: Math.max(0, item.startOffset - nodeStart),
                      end: Math.min(node.data.length, item.endOffset - nodeStart)
                    });
                  }
                  cursor = nodeEnd;
                  if (cursor >= item.endOffset) break;
                }

                for (let index = segments.length - 1; index >= 0; index--) {
                  const segment = segments[index];
                  if (segment.end <= segment.start) continue;
                  let selected = segment.node;
                  if (segment.end < selected.data.length) selected.splitText(segment.end);
                  if (segment.start > 0) selected = selected.splitText(segment.start);
                  const mark = document.createElement('span');
                  mark.dataset.kkindleAnnotation = item.id;
                  mark.style.backgroundColor = 'transparent';
                  mark.style.textDecorationLine = 'underline';
                  mark.style.textDecorationColor = '#000000';
                  mark.style.textDecorationThickness = '2px';
                  mark.style.textUnderlineOffset = '2px';
                  mark.style.cursor = 'pointer';
                  if (item.note) mark.title = item.note;
                  selected.parentNode.insertBefore(mark, selected);
                  mark.appendChild(selected);
                }
              }
            })();
            """;
        try { await ReaderWebView.CoreWebView2.ExecuteScriptAsync(script); }
        catch { }
    }

    private async Task ClearReaderSelectionAsync()
    {
        if (ReaderWebView.CoreWebView2 is null) return;
        try { await ReaderWebView.CoreWebView2.ExecuteScriptAsync("window.getSelection && window.getSelection().removeAllRanges();"); }
        catch { }
    }

    private void ReaderAnnotationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ReaderDeleteAnnotationButton.IsEnabled = ReaderAnnotationList.SelectedItem is ReaderAnnotation;
    }

    private void ReaderAnnotationList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ReaderAnnotation annotation) NavigateToReaderAnnotation(annotation);
    }

    private async void ReaderDeleteAnnotationButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReaderAnnotationList.SelectedItem is not ReaderAnnotation annotation || _readerFeatureCancellation is null)
            return;
        try
        {
            await _readerData.DeleteAnnotationAsync(annotation.Id, _readerFeatureCancellation.Token);
            _readerAnnotations.Remove(annotation);
            ReaderAnnotationList.SelectedItem = null;
            await ApplyReaderAnnotationsToPageAsync();
            ReaderAnnotationSelectionText.Text = "批注已删除。";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReaderAnnotationSelectionText.Text = $"删除失败：{exception.Message}";
        }
    }

    private void NavigateToReaderAnnotation(ReaderAnnotation annotation)
    {
        if (_readerDocument is null || _readerAllowedRoot is null) return;
        var relative = annotation.ChapterPath.Replace('/', Path.DirectorySeparatorChar);
        var targetPath = Path.GetFullPath(Path.Combine(_readerAllowedRoot, relative));
        if (!IsPathInside(_readerAllowedRoot, targetPath) || !File.Exists(targetPath)) return;
        var chapterIndex = _readerChapters.ToList().FindIndex(chapter =>
            Path.GetFullPath(chapter).Equals(targetPath, StringComparison.OrdinalIgnoreCase));
        if (chapterIndex < 0) return;

        _readerChapterIndex = chapterIndex;
        _pendingReaderAnnotationScroll = annotation.Id;
        UpdateReaderChapterControls();
        var target = new Uri(targetPath).AbsoluteUri;
        if (!string.IsNullOrWhiteSpace(annotation.Fragment)) target += $"#{annotation.Fragment}";
        if (ReaderWebView.Source?.AbsoluteUri.Equals(target, StringComparison.OrdinalIgnoreCase) == true)
        {
            _ = ApplyReaderAnnotationsToPageAsync();
            _ = ScrollToPendingReaderAnnotationAsync();
        }
        else
        {
            ReaderWebView.Source = new Uri(target);
        }
    }

    private async Task ScrollToPendingReaderAnnotationAsync()
    {
        if (_pendingReaderAnnotationScroll is not Guid annotationId || ReaderWebView.CoreWebView2 is null) return;
        _pendingReaderAnnotationScroll = null;
        var id = annotationId.ToString("N");
        var script = $"document.querySelector('[data-kkindle-annotation=\"{id}\"]')?.scrollIntoView({{ block: 'center', behavior: 'smooth' }});";
        try { await ReaderWebView.CoreWebView2.ExecuteScriptAsync(script); }
        catch { }
    }

    private async Task ConfigureReaderFootnoteHoverAsync()
    {
        if (_readerAllowedRoot is null || ReaderWebView.CoreWebView2 is null || _readerFeatureCancellation is null) return;
        var targets = await ExecuteReaderJsonScriptAsync<string[]>(
            "Array.from(document.querySelectorAll('a[href*=\"#\"]')).map(a => { try { return new URL(a.getAttribute('href'), location.href).href; } catch { return ''; } }).filter(Boolean);")
            ?? [];
        IReadOnlyDictionary<string, string> footnotes;
        try
        {
            footnotes = await _footnotes.ResolveAsync(_readerAllowedRoot, targets, _readerFeatureCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var data = JsonSerializer.Serialize(footnotes);
        var script = $$"""
            (() => {
              const noteMap = {{data}};
              let popup = document.getElementById('kkindle-footnote-popup');
              if (!popup) {
                popup = document.createElement('div');
                popup.id = 'kkindle-footnote-popup';
                popup.setAttribute('role', 'tooltip');
                Object.assign(popup.style, {
                  position: 'fixed', display: 'none', zIndex: '2147483647', maxWidth: '360px',
                  maxHeight: '260px', overflow: 'auto', padding: '12px 14px', background: '#ffffff',
                  color: '#000000', border: '2px solid #000000', borderRadius: '0',
                  boxShadow: 'none', fontSize: '.9rem', lineHeight: '1.65',
                  pointerEvents: 'none', whiteSpace: 'normal'
                });
                document.body.appendChild(popup);
              }

              const hide = () => { popup.style.display = 'none'; };
              document.querySelectorAll('a[href*="#"]').forEach(anchor => {
                if (anchor.dataset.kkindleFootnoteBound) return;
                anchor.dataset.kkindleFootnoteBound = '1';
                anchor.addEventListener('mouseenter', () => {
                  let url;
                  try { url = new URL(anchor.getAttribute('href'), location.href); } catch { return; }
                  let text = noteMap[url.href] || '';
                  if (url.pathname === location.pathname && url.hash) {
                    let id = url.hash.slice(1);
                    try { id = decodeURIComponent(id); } catch { }
                    const target = document.getElementById(id) || Array.from(document.getElementsByName(id))[0];
                    text = (target?.innerText || target?.textContent || text || '').trim();
                  }
                  text = text.replace(/\s+/g, ' ').replace(/[↩↵↑]+$/g, '').trim();
                  if (!text || text === (anchor.innerText || '').trim()) return;
                  popup.textContent = text;
                  popup.style.display = 'block';
                  const rect = anchor.getBoundingClientRect();
                  const width = Math.min(360, Math.max(220, window.innerWidth - 24));
                  popup.style.width = width + 'px';
                  const left = Math.min(window.innerWidth - width - 12, Math.max(12, rect.left));
                  popup.style.left = left + 'px';
                  const popupHeight = popup.getBoundingClientRect().height;
                  const below = rect.bottom + 10;
                  popup.style.top = (below + popupHeight < window.innerHeight
                    ? below
                    : Math.max(12, rect.top - popupHeight - 10)) + 'px';
                });
                anchor.addEventListener('mouseleave', hide);
                anchor.addEventListener('blur', hide);
              });
              document.addEventListener('scroll', hide, { passive: true });
            })();
            """;
        try { await ReaderWebView.CoreWebView2.ExecuteScriptAsync(script); }
        catch { }
    }

    private sealed class ReaderSelectionAnchor
    {
        public string Text { get; set; } = string.Empty;
        public int StartOffset { get; set; }
        public int EndOffset { get; set; }
        public string Prefix { get; set; } = string.Empty;
        public string Suffix { get; set; } = string.Empty;
    }
}
