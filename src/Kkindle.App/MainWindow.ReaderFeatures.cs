using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Web.WebView2.Core;
using WinRT.Interop;
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
    private readonly List<string> _readerAiAvailableModels = [];
    private CancellationTokenSource? _readerAiModelListCancellation;
    private string _readerAiReasoningDepth = "auto";
    private ReaderSelectionAnchor? _pendingReaderSelection;
    private Guid? _pendingReaderAnnotationScroll;
    private int? _pendingReaderChunkOffset;
    private bool _readerIndexAvailable;
    private bool _readerAiBusy;
    private bool _suppressAiProviderChange;
    private bool _suppressAiModelChange;
    private bool _suppressAiReasoningDepthChange;
    private Guid? _readerAnnotationNavigationInFlight;
    private Popup? _readerAssistantPopup;
    private Popup? _readerSettingsPopup;
    private Popup? _readerZenPopup;

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

        // The zen-mode bar floats above the WebView2 (an HWND island), so it
        // must live in its own Popup like the assistant and settings overlays.
        if (ReaderZenBar.Parent is Panel zenParent)
        {
            zenParent.Children.Remove(ReaderZenBar);
            ReaderZenBar.Margin = new Thickness(0);
            ReaderZenBar.Visibility = Visibility.Visible;
            _readerZenPopup = new Popup
            {
                Child = ReaderZenBar,
                IsLightDismissEnabled = false,
                IsOpen = false
            };
        }

        ConfigureReaderToolsPopupHosts();
        SetReaderTocTab(bookmarkTab: false);
    }

    private void UpdateReaderZenPopup(bool visible)
    {
        if (_readerZenPopup is null || RootGrid.XamlRoot is null) return;
        if (visible)
        {
            var viewport = RootGrid.XamlRoot.Size;
            _readerZenPopup.XamlRoot = RootGrid.XamlRoot;
            ReaderZenBar.Visibility = Visibility.Visible;
            ReaderZenBar.Measure(new Windows.Foundation.Size(viewport.Width, Math.Max(0, viewport.Height - 38)));
            var width = Math.Max(200, ReaderZenBar.DesiredSize.Width);
            _readerZenPopup.HorizontalOffset = Math.Max(0, viewport.Width - width - 24);
            _readerZenPopup.VerticalOffset = 38;
        }
        else
        {
            ReaderZenBar.Visibility = Visibility.Collapsed;
        }
        _readerZenPopup.IsOpen = visible;
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
        _readerAiModelListCancellation?.Cancel();
        _readerAiModelListCancellation?.Dispose();
        _readerAiModelListCancellation = null;
        _readerAiAvailableModels.Clear();
        _readerFeatureCancellation = new CancellationTokenSource();
        _readerBook = book;
        _readerBookFile = file;
        _readerDocument = null;
        _readerIndexTask = null;
        _readerIndexAvailable = false;
        _pendingReaderAnnotationScroll = null;
        _pendingReaderChunkOffset = null;
        ResetReaderFeatures();
        ResetReaderToolsSession();
        // A previous session may have left the TOC pane on the bookmark tab;
        // reset the visual tab state together with the persisted session data.
        SetReaderTocTab(bookmarkTab: false);
    }

    private async Task LoadReaderSessionDataAsync(CancellationToken cancellationToken)
    {
        _readerAiSettings = await _aiSettingsStore.LoadAsync(cancellationToken);
        UpdateReaderAiHeader();
        _ = RefreshReaderAiModelSelectorAsync(cancellationToken);
        if (_readerBookFile is null) return;
        var annotations = await _readerData.GetAnnotationsAsync(_readerBookFile.Id, cancellationToken);
        ReplaceReaderAnnotations(annotations);

        _readerBookmarks.Clear();
        foreach (var bookmark in await _readerData.GetBookmarksAsync(_readerBookFile.Id, cancellationToken))
            _readerBookmarks.Add(bookmark);
        RefreshReaderBookmarkList();

        var savedLayout = await _readerData.GetLayoutSettingsAsync(_readerBookFile.Id, cancellationToken);
        if (savedLayout is not null) _readerLayout = NormalizeReaderLayoutSettings(savedLayout);

        var stats = await _readerData.GetReadingStatsAsync(_readerBookFile.Id, cancellationToken);
        _readerStatsBaseSeconds = stats?.CumulativeSeconds ?? 0;

        _savedReaderProgress = await _readerData.GetProgressAsync(_readerBookFile.Id, cancellationToken);
    }

    private void EndReaderSession()
    {
        // The persistence flush is owned by CloseReader/MainWindow_Closed
        // (FlushReaderSessionSafelyAsync) and must run BEFORE this method
        // nulls the session fields, otherwise the last progress snapshot would
        // be lost. Keep this method to cancellation/cleanup only.
        StopReaderToolsTimers();
        _readerAiCancellation?.Cancel();
        _readerAiCancellation?.Dispose();
        _readerAiCancellation = null;
        _readerAiModelListCancellation?.Cancel();
        _readerAiModelListCancellation?.Dispose();
        _readerAiModelListCancellation = null;
        _readerAiAvailableModels.Clear();
        _readerFeatureCancellation?.Cancel();
        _readerFeatureCancellation?.Dispose();
        _readerFeatureCancellation = null;
        ResetReaderFootnoteSession();
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
        ResetReaderFootnoteSession();
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
        ReaderAiModelSelectorBox.IsEnabled = true;
        ReaderAiReasoningDepthBox.IsEnabled = true;
        _readerAiReasoningDepth = "auto";
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
        annotation.Color = GetSelectedReaderAnnotationColor();
        annotation.UnderlineStyle = GetSelectedReaderAnnotationStyle();
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

    private string GetSelectedReaderAnnotationColor() =>
        (ReaderAnnotationColorBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "#000000";

    private string GetSelectedReaderAnnotationStyle() =>
        (ReaderAnnotationStyleBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "solid";

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

    private async Task<int> ApplyReaderAnnotationsToPageAsync()
    {
        var chapterPath = GetCurrentReaderChapterPath();
        if (chapterPath is null || ReaderWebView.CoreWebView2 is null) return 0;
        var payload = _readerAnnotations
            .Where(annotation => annotation.ChapterPath.Equals(chapterPath, StringComparison.OrdinalIgnoreCase))
            .Select(annotation => new
            {
                id = annotation.Id.ToString("N"),
                startOffset = annotation.StartOffset,
                endOffset = annotation.EndOffset,
                note = annotation.Note,
                color = annotation.Color,
                underlineStyle = annotation.UnderlineStyle
            })
            .OrderByDescending(annotation => annotation.startOffset)
            .ToArray();
        var data = JsonSerializer.Serialize(payload);
        var script = $$"""
            (() => {
              try {
                const root = document.body;
                if (!root) return `0|0|0|NO_BODY`;
                document.querySelectorAll('span[data-kkindle-annotation]').forEach(mark => {
                  const parent = mark.parentNode;
                  while (mark.firstChild) parent.insertBefore(mark.firstChild, mark);
                  parent.removeChild(mark);
                  parent.normalize();
                });

              const annotations = {{data}};
              let appliedCount = 0;
              let matchedRanges = 0;
              let lastNodeCount = 0;
              let maxTextCursor = 0;
              const normalizeColor = value => /^#[0-9a-f]{6}$/i.test(value || '') ? value : '#000000';
              const toHighlightColor = value => {
                const color = normalizeColor(value);
                const hex = color.slice(1);
                const red = parseInt(hex.slice(0, 2), 16);
                const green = parseInt(hex.slice(2, 4), 16);
                const blue = parseInt(hex.slice(4, 6), 16);
                return `rgba(${red}, ${green}, ${blue}, ${color.toLowerCase() === '#000000' ? 0.18 : 0.32})`;
              };
              const textNodes = () => {
                const nodes = [];
                const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
                while (walker.nextNode()) {
                  if (walker.currentNode.data.length) nodes.push(walker.currentNode);
                }
                return nodes;
              };

              for (const item of annotations) {
                let cursor = 0;
                const segments = [];
                const nodes = textNodes();
                lastNodeCount = nodes.length;
                for (const node of nodes) {
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
                maxTextCursor = Math.max(maxTextCursor, cursor);
                if (segments.length > 0) matchedRanges++;

                for (let index = segments.length - 1; index >= 0; index--) {
                  const segment = segments[index];
                  if (segment.end <= segment.start) continue;
                  let selected = segment.node;
                  if (segment.end < selected.data.length) selected.splitText(segment.end);
                  if (segment.start > 0) selected = selected.splitText(segment.start);
                  const mark = document.createElement('span');
                  mark.dataset.kkindleAnnotation = item.id;
                  const color = normalizeColor(item.color);
                  mark.style.setProperty('background-color', toHighlightColor(color), 'important');
                  mark.style.setProperty('text-decoration-line', 'underline', 'important');
                  mark.style.setProperty('text-decoration-color', color, 'important');
                  mark.style.setProperty('text-decoration-style', item.underlineStyle === 'wavy' ? 'wavy' : 'solid', 'important');
                  mark.style.setProperty('text-decoration-thickness', '2px', 'important');
                  mark.style.setProperty('text-underline-offset', '3px', 'important');
                  mark.style.setProperty('text-decoration-skip-ink', 'none', 'important');
                  mark.style.setProperty('display', 'inline', 'important');
                  mark.style.cursor = 'pointer';
                  if (item.note) mark.title = item.note;
                  selected.parentNode.insertBefore(mark, selected);
                  mark.appendChild(selected);
                  appliedCount++;
                }
              }
                return `${appliedCount}|${matchedRanges}|${lastNodeCount}`;
              } catch (error) {
                return `ERROR|${error?.name || ''}|${error?.message || String(error)}`;
              }
            })();
            """;
        try
        {
            var appliedJson = string.Empty;
            var diagnostic = string.Empty;
            for (var attempt = 0; attempt < 6; attempt++)
            {
                appliedJson = await ReaderWebView.CoreWebView2.ExecuteScriptAsync(script);
                diagnostic = appliedJson.Trim().Trim('"');
                var loopParts = diagnostic.Split('|');
                var nodeCount = loopParts.ElementAtOrDefault(2);
                if (payload.Length == 0 || !string.Equals(nodeCount, "0", StringComparison.Ordinal))
                    break;
                if (attempt < 5)
                    await Task.Delay(100 + (attempt * 150));
            }
            var parts = diagnostic.Split('|');
            _ = int.TryParse(parts.ElementAtOrDefault(0), out var appliedCount);
            _ = int.TryParse(parts.ElementAtOrDefault(1), out var matchedCount);
            if (payload.Length > 0)
                ReaderStatusText.Text = $"本页已恢复 {Math.Max(0, matchedCount)} 条划线";
            return Math.Max(0, appliedCount);
        }
        catch { return 0; }
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
        if (e.AddedItems.FirstOrDefault() is ReaderAnnotation annotation)
            _ = NavigateToReaderAnnotationAsync(annotation);
    }

    private void ReaderAnnotationList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ReaderAnnotation annotation) return;
        if (ReaderAnnotationList.SelectedItem is ReaderAnnotation selected
            && selected.Id == annotation.Id)
        {
            _ = NavigateToReaderAnnotationAsync(annotation);
        }
        else
        {
            ReaderAnnotationList.SelectedItem = annotation;
        }
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

    private async Task NavigateToReaderAnnotationAsync(ReaderAnnotation annotation)
    {
        if (_readerAnnotationNavigationInFlight == annotation.Id) return;
        _readerAnnotationNavigationInFlight = annotation.Id;
        try
        {
            await NavigateToReaderAnnotationCoreAsync(annotation);
        }
        finally
        {
            if (_readerAnnotationNavigationInFlight == annotation.Id)
                _readerAnnotationNavigationInFlight = null;
        }
    }

    private async Task NavigateToReaderAnnotationCoreAsync(ReaderAnnotation annotation)
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
        var targetUri = new Uri(target);
        if (ReaderNavigationLocationPolicy.TargetsSameDocument(ReaderWebView.Source, targetUri))
        {
            await ApplyReaderAnnotationsToPageAsync();
            await ScrollToPendingReaderAnnotationAsync();
        }
        else
        {
            await NavigateReaderSourceAsync(targetUri, 1, animate: true, ReaderNavigationIntent.Annotation);
        }
    }

    private async Task ScrollToPendingReaderAnnotationAsync()
    {
        if (_pendingReaderAnnotationScroll is not Guid annotationId || ReaderWebView.CoreWebView2 is null) return;
        var annotation = _readerAnnotations.FirstOrDefault(item => item.Id == annotationId);
        if (annotation is null) return;
        var id = annotationId.ToString("N");
        var startOffset = Math.Max(0, annotation.StartOffset);
        var endOffset = Math.Max(startOffset, annotation.EndOffset);
        var pagination = _readerFlowMode == 1 ? "true" : "false";
        var script = $$"""
            (() => {
              const pagination = {{pagination}};
              const revealRect = rect => {
                if (!rect || (rect.width <= 0 && rect.height <= 0)) return false;
                const scroller = document.scrollingElement || document.documentElement;
                if (!scroller) return false;
                if (pagination) {
                  const step = scroller.getBoundingClientRect?.().width
                    || scroller.clientWidth
                    || document.documentElement.clientWidth
                    || window.innerWidth
                    || 0;
                  if (step <= 0) return false;
                  const absoluteX = rect.left + (scroller.scrollLeft || 0) + Math.max(0, rect.width) / 2;
                  const max = Math.max(0, scroller.scrollWidth - scroller.clientWidth);
                  const target = Math.max(0, Math.min(max, Math.floor(absoluteX / step) * step));
                  // Set the scroll position synchronously. This keeps the
                  // subsequent page snap on the same page as the annotation.
                  scroller.scrollLeft = target;
                  scroller.scrollTop = 0;
                  return true;
                }
                return false;
              };
              const revealElement = element => {
                if (!element) return false;
                const rects = element.getClientRects ? Array.from(element.getClientRects()) : [];
                const rect = rects.find(item => item.width > 0 || item.height > 0)
                  || element.getBoundingClientRect?.();
                if (revealRect(rect)) return true;
                if (element.scrollIntoView) {
                  element.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'auto' });
                  return true;
                }
                return false;
              };
              const mark = document.querySelector('[data-kkindle-annotation="{{id}}"]');
              if (revealElement(mark)) return true;
              const root = document.body;
              if (!root) return false;
              const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
              let cursor = 0;
              let startNode = null;
              let endNode = null;
              let startLocal = 0;
              let endLocal = 0;
              while (walker.nextNode()) {
                const node = walker.currentNode;
                if (!node.data.length) continue;
                const next = cursor + node.data.length;
                if (!startNode && {{startOffset}} <= next) {
                  startNode = node;
                  startLocal = Math.max(0, {{startOffset}} - cursor);
                }
                if ({{endOffset}} <= next) {
                  endNode = node;
                  endLocal = Math.max(0, {{endOffset}} - cursor);
                  break;
                }
                cursor = next;
              }
              if (!startNode) return false;
              endNode = endNode || startNode;
              const range = document.createRange();
              range.setStart(startNode, Math.min(startLocal, startNode.data.length));
              range.setEnd(endNode, Math.min(endLocal, endNode.data.length));
              const rangeRects = Array.from(range.getClientRects ? range.getClientRects() : []);
              const rangeRect = rangeRects.find(item => item.width > 0 || item.height > 0);
              if (revealRect(rangeRect)) return true;
              const target = range.commonAncestorContainer.nodeType === Node.ELEMENT_NODE
                ? range.commonAncestorContainer
                : range.commonAncestorContainer.parentElement;
              if (target && target.scrollIntoView) {
                target.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'auto' });
                return true;
              }
              return false;
            })();
            """;
        try
        {
            var located = false;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(100);
                    await ApplyReaderAnnotationsToPageAsync();
                }

                var result = await ReaderWebView.CoreWebView2.ExecuteScriptAsync(script);
                if (string.Equals(result.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                {
                    located = true;
                    break;
                }
            }

            if (located)
            {
                _pendingReaderAnnotationScroll = null;
                if (_readerFlowMode == 1)
                {
                    await Task.Delay(30);
                    await SnapReaderPaginationAsync();
                }
            }
            else
            {
                _pendingReaderAnnotationScroll = null;
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------
    // Host-side reading input.
    //
    // EPUB page scripts stay disabled (IsScriptEnabled=false). Under that
    // setting WebView2 freezes the page's event dispatch, so injected
    // document listeners (scroll/click/key) never fire and the old
    // `window.chrome.webview.postMessage` navigation path is dead. The host
    // therefore observes input directly:
    //   - Scroll mode continuously polls the real scrolling element and
    //     advances/recedes chapters at the bottom/top edges.
    //   - Pagination mode watches a low-level mouse hook for clicks inside
    //     the WebView host rect and maps left 1/3 -> previous, right 2/3 ->
    //     next.
    // All DOM access uses ExecuteScriptAsync (read/write), which works even
    // with page scripts disabled; the EPUB path whitelist is unchanged.
    // ------------------------------------------------------------------

    private void StartReaderScrollPoll()
    {
        if (_readerScrollPollTimer is null)
        {
            _readerScrollPollTimer = DispatcherQueue.CreateTimer();
            _readerScrollPollTimer.Interval = TimeSpan.FromMilliseconds(150);
            _readerScrollPollTimer.Tick += async (_, _) => await PollReaderScrollAsync();
        }
        _readerScrollPollTimer.Start();
    }

    private void StopReaderScrollPoll()
    {
        _readerScrollPollTimer?.Stop();
    }

    private async Task PollReaderScrollAsync()
    {
        if (_readerPollRunning) return;
        if (ReaderPane.Visibility != Visibility.Visible) return;
        if (_readerCloseRequested || _readerTransitionActive) return;
        if (_readerFlowMode != 0 || !_readerHasToc || _readerChapters.Count <= 1) return;
        if (ReaderWebView.CoreWebView2 is null || _readerAllowedRoot is null) return;
        if (_readerChapterIndex < 0 || _readerChapterIndex >= _readerChapters.Count) return;

        _readerPollRunning = true;
        try
        {
            var json = await ReaderWebView.CoreWebView2.ExecuteScriptAsync(
                "(function(){var el=document.scrollingElement||document.documentElement;return {st:el.scrollTop||0,sl:el.scrollLeft||0,sh:el.scrollHeight||0,sw:el.scrollWidth||0,ch:el.clientHeight||window.innerHeight||0,cw:el.clientWidth||window.innerWidth||0};})()");
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var scrollTop = root.TryGetProperty("st", out var st) ? st.GetDouble() : 0;
            var scrollLeft = root.TryGetProperty("sl", out var sl) ? sl.GetDouble() : 0;
            var scrollHeight = root.TryGetProperty("sh", out var sh) ? sh.GetDouble() : 0;
            var scrollWidth = root.TryGetProperty("sw", out var sw) ? sw.GetDouble() : 0;
            var clientHeight = root.TryGetProperty("ch", out var ch) ? ch.GetDouble() : 0;
            var clientWidth = root.TryGetProperty("cw", out var cw) ? cw.GetDouble() : 0;

            var vertical = _readerLayout.VerticalWriting;
            var scrollSize = vertical ? scrollWidth : scrollHeight;
            var clientSize = vertical ? clientWidth : clientHeight;
            var scrollPosition = vertical ? scrollLeft : scrollTop;
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
                _ = SaveReaderProgressThrottledAsync();
                return;
            }

            if (_readerContinuousLocked)
            {
                // A locked chapter that barely overflows can jump from one edge
                // to the other without crossing the middle zone (e.g. scrollbar
                // drag, PageUp/Down). Force the navigation instead of leaving
                // the reader stuck. Forward-locked chapters force at the bottom,
                // backward-locked chapters force at the top.
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
                else return;
            }

            if (nearBottom && !_readerLastNearBottom)
            {
                if (_readerChapterIndex + 1 < _readerChapters.Count)
                {
                    _readerChapterIndex++;
                    _readerNavigateToEnd = false;
                    _readerContinuousLocked = true;
                    _readerContinuousDirection = 1;
                    _readerLastChapterChange = DateTimeOffset.UtcNow;
                    UpdateReaderChapterControls();
                    _ = ShowReaderChapterAsync(1);
                }
            }
            else if (nearTop && !_readerLastNearTop)
            {
                if (_readerChapterIndex > 0)
                {
                    _readerChapterIndex--;
                    _readerNavigateToEnd = true;
                    _readerContinuousLocked = true;
                    _readerContinuousDirection = -1;
                    _readerLastChapterChange = DateTimeOffset.UtcNow;
                    UpdateReaderChapterControls();
                    _ = ShowReaderChapterAsync(-1);
                }
            }
            _readerLastNearTop = nearTop;
            _readerLastNearBottom = nearBottom;
        }
        catch
        {
            // Navigation or script interruption during chapter switches is
            // expected; the next poll tick recovers.
        }
        finally { _readerPollRunning = false; }
    }

    private async Task PrimeReaderScrollEdgesAsync()
    {
        if (ReaderWebView.CoreWebView2 is null || _readerAllowedRoot is null) return;
        try
        {
            var json = await ReaderWebView.CoreWebView2.ExecuteScriptAsync(
                "(function(){var el=document.scrollingElement||document.documentElement;return {st:el.scrollTop||0,sl:el.scrollLeft||0,sh:el.scrollHeight||0,sw:el.scrollWidth||0,ch:el.clientHeight||window.innerHeight||0,cw:el.clientWidth||window.innerWidth||0};})()");
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var scrollTop = root.TryGetProperty("st", out var st) ? st.GetDouble() : 0;
            var scrollLeft = root.TryGetProperty("sl", out var sl) ? sl.GetDouble() : 0;
            var scrollHeight = root.TryGetProperty("sh", out var sh) ? sh.GetDouble() : 0;
            var scrollWidth = root.TryGetProperty("sw", out var sw) ? sw.GetDouble() : 0;
            var clientHeight = root.TryGetProperty("ch", out var ch) ? ch.GetDouble() : 0;
            var clientWidth = root.TryGetProperty("cw", out var cw) ? cw.GetDouble() : 0;
            var vertical = _readerLayout.VerticalWriting;
            var scrollSize = vertical ? scrollWidth : scrollHeight;
            var clientSize = vertical ? clientWidth : clientHeight;
            var scrollPosition = vertical ? scrollLeft : scrollTop;
            _readerLastNearTop = scrollPosition <= 48;
            _readerLastNearBottom = scrollSize > 0
                && clientSize > 0
                && scrollPosition + clientSize >= scrollSize - 48;
        }
        catch
        {
        }
    }

    // ------------------------------------------------------------------
    // Pagination click zones (host-side). WebView2 renders as a composition
    // island inside the WinUI HWND, so XAML pointer events over the reading
    // surface never fire. A low-level mouse hook observes the raw left-button
    // click and maps it to the left 1/3 / right 2/3 zones of the WebView
    // host rect; the header/footer/TOC/assistant live outside that rect, so
    // their controls are never mis-triggered.
    // ------------------------------------------------------------------

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WhMouseLl = 14;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const int ClickDragTolerance = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    private void InstallReaderMouseHook()
    {
        if (_readerMouseHook != IntPtr.Zero) return;
        _readerHookEnabled = true;
        _readerMouseProc = ReaderMouseHookCallback;
        _readerMouseHook = SetWindowsHookEx(WhMouseLl, _readerMouseProc, GetModuleHandle(null), 0);
        if (_readerMouseHook == IntPtr.Zero) _readerHookEnabled = false;
    }

    private void UninstallReaderMouseHook()
    {
        _readerHookEnabled = false;
        if (_readerMouseHook == IntPtr.Zero) return;
        _ = UnhookWindowsHookEx(_readerMouseHook);
        _readerMouseHook = IntPtr.Zero;
        _readerMouseProc = null;
    }

    // Runs on the system hook thread. It only reads plain cached fields (never
    // XAML), so uninstalling the hook while a callback is in flight cannot
    // deadlock the UI thread; the click handling is enqueued to the dispatcher.
    private IntPtr ReaderMouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _readerHookEnabled)
        {
            var data = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
            switch ((uint)wParam.ToInt64())
            {
                case WmLButtonDown:
                    if (IsInsideCachedReaderWebViewScreenRect(data.pt))
                    {
                        _readerMouseDownInside = true;
                        _readerMouseDownPoint = data.pt;
                    }
                    break;
                case WmLButtonUp:
                    if (_readerMouseDownInside && IsInsideCachedReaderWebViewScreenRect(data.pt))
                    {
                        var moved = Math.Abs(data.pt.X - _readerMouseDownPoint.X)
                            + Math.Abs(data.pt.Y - _readerMouseDownPoint.Y);
                        if (moved <= ClickDragTolerance)
                        {
                            var point = data.pt;
                            DispatcherQueue.TryEnqueue(() => _ = HandleReaderZoneClickAsync(point));
                        }
                    }
                    _readerMouseDownInside = false;
                    break;
            }
        }
        return CallNextHookEx(_readerMouseHook, nCode, wParam, lParam);
    }

    private bool IsInsideCachedReaderWebViewScreenRect(POINT point)
    {
        var rect = _readerWebViewScreenRect;
        return rect.Width > 0
            && point.X >= rect.Left && point.X <= rect.Right
            && point.Y >= rect.Top && point.Y <= rect.Bottom;
    }

    private Windows.Foundation.Rect GetReaderWebViewScreenRect()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var clientOrigin = new POINT { X = 0, Y = 0 };
        _ = ClientToScreen(hwnd, ref clientOrigin);
        var origin = ReaderWebViewHost.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        var scale = ReaderWebViewHost.XamlRoot?.RasterizationScale ?? 1.0;
        var rect = new Windows.Foundation.Rect(
            clientOrigin.X + origin.X * scale,
            clientOrigin.Y + origin.Y * scale,
            ReaderWebViewHost.ActualWidth * scale,
            ReaderWebViewHost.ActualHeight * scale);
        // Cache for the low-level hook thread (which must never touch XAML).
        _readerWebViewScreenRect = rect;
        return rect;
    }

    private bool IsInsideReaderWebViewScreenRect(POINT point)
    {
        var rect = GetReaderWebViewScreenRect();
        return point.X >= rect.Left && point.X <= rect.Right
            && point.Y >= rect.Top && point.Y <= rect.Bottom;
    }

    private async Task HandleReaderZoneClickAsync(POINT screenPoint)
    {
        if (_readerCloseRequested || _readerTransitionActive) return;
        if (_readerFlowMode != 1 || ReaderWebView.CoreWebView2 is null) return;
        if (_readerSearchVisible) return; // The search panel is open: don't page-turn underneath it.
        var rect = GetReaderWebViewScreenRect();
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var relativeX = screenPoint.X - rect.Left;
        var relativeY = screenPoint.Y - rect.Top;
        if (relativeX < 0 || relativeX > rect.Width || relativeY < 0 || relativeY > rect.Height) return;

        // Keep parity with the old in-page guard: ignore clicks on links /
        // form controls and clicks that produced a text selection.
        try
        {
            var viewport = await ReaderWebView.CoreWebView2.ExecuteScriptAsync(
                "({w:document.documentElement.clientWidth||document.body.clientWidth||0,h:window.innerHeight||0})");
            using var document = JsonDocument.Parse(viewport);
            var root = document.RootElement;
            var cssWidth = root.TryGetProperty("w", out var widthProperty) ? widthProperty.GetDouble() : 0;
            var cssHeight = root.TryGetProperty("h", out var heightProperty) ? heightProperty.GetDouble() : 0;
            if (cssWidth > 0 && cssHeight > 0)
            {
                var cssX = (int)(relativeX * cssWidth / rect.Width);
                var cssY = (int)(relativeY * cssHeight / rect.Height);
                var guard = await ReaderWebView.CoreWebView2.ExecuteScriptAsync(
                    $$"""
                    (function(){
                      const el = document.elementFromPoint({{cssX}}, {{cssY}});
                      const control = el && el.closest ? el.closest('a, button, input, textarea, select, [contenteditable]') : null;
                      let selected = false;
                      try { selected = !!window.getSelection && !!window.getSelection().toString(); } catch (err) {}
                      return JSON.stringify({ blocked: !!(control || selected) });
                    })();
                    """);
                using var guardDocument = JsonDocument.Parse(guard);
                if (guardDocument.RootElement.TryGetProperty("blocked", out var blocked)
                    && blocked.GetBoolean())
                    return;
            }
        }
        catch
        {
        }

        var direction = relativeX < rect.Width / 3 ? -1 : 1;
        await TurnReaderPageAsync(direction);
    }

    private async Task SkipShortChapterIfNeededAsync()
    {
        if (!_readerContinuousLocked || ReaderWebView.CoreWebView2 is null) return;
        if (_readerCloseRequested) return;
        await Task.Delay(60);
        if (!_readerContinuousLocked || ReaderWebView.CoreWebView2 is null) return;
        if (_readerCloseRequested) return;
        string result;
        try
        {
            result = await ReaderWebView.CoreWebView2.ExecuteScriptAsync(
                "(document.scrollingElement && document.scrollingElement.scrollHeight > document.scrollingElement.clientHeight + 16) ? 'yes' : 'no';");
        }
        catch { return; }
        if (result == "\"yes\"") return; // Scrollable content: let the scroll listener take over.
        var targetIndex = _readerChapterIndex + _readerContinuousDirection;
        if (targetIndex < 0 || targetIndex >= _readerChapters.Count) return;
        _readerChapterIndex = targetIndex;
        _readerNavigateToEnd = _readerContinuousDirection < 0;
        _readerLastChapterChange = DateTimeOffset.UtcNow;
        UpdateReaderChapterControls();
        await ShowReaderChapterAsync(_readerContinuousDirection);
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
