using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public sealed partial class MainWindow
{
    // ------------------------------------------------------------------
    // Reader productivity tools: per-book layout settings, progress
    // persistence (breakpoint restore), bookmarks, whole-book search,
    // text-selection quick actions, annotation export and reading stats.
    // All persistence goes through ReaderDataService (local SQLite).
    // ------------------------------------------------------------------

    private ReaderLayoutSettings _readerLayout = new();
    private Popup? _readerLayoutPopup;
    private Popup? _readerSearchPopup;
    private Popup? _readerSelectionPopup;
    private DispatcherQueueTimer? _readerSelectionTimer;
    private DispatcherQueueTimer? _readerStatsTimer;
    private CancellationTokenSource? _readerLayoutApplyCancellation;
    private bool _suppressReaderLayoutChange;
    private bool _readerBookmarkTabActive;
    private readonly ObservableCollection<ReaderBookmark> _readerBookmarks = [];
    private string? _readerSelectionText;
    private bool _readerPollingSelection;
    private int? _pendingReaderRestorePosition;
    private string? _pendingReaderBookmarkQuote;
    private ReaderProgressRow? _savedReaderProgress;
    private ReaderProgressRow? _readerLastProgress;
    private DateTimeOffset _readerProgressLastSave = DateTimeOffset.MinValue;
    private DateTimeOffset _readerSessionStart = DateTimeOffset.UtcNow;
    private long _readerActiveSeconds;
    private long _readerStatsBaseSeconds;
    private bool _windowActive = true;
    private bool _readerSearchVisible;

    private void ConfigureReaderToolsPopupHosts()
    {
        ReaderPane.Children.Remove(ReaderLayoutSettingsOverlay);
        ReaderLayoutSettingsOverlay.Margin = new Thickness(0);
        ReaderLayoutSettingsOverlay.Visibility = Visibility.Visible;
        _readerLayoutPopup = new Popup
        {
            Child = ReaderLayoutSettingsOverlay,
            IsLightDismissEnabled = false,
            IsOpen = false
        };

        ReaderPane.Children.Remove(ReaderSearchPanel);
        ReaderSearchPanel.Margin = new Thickness(0);
        ReaderSearchPanel.Visibility = Visibility.Visible;
        _readerSearchPopup = new Popup
        {
            Child = ReaderSearchPanel,
            IsLightDismissEnabled = true,
            IsOpen = false
        };

        ReaderPane.Children.Remove(ReaderSelectionBar);
        ReaderSelectionBar.Margin = new Thickness(0);
        ReaderSelectionBar.Visibility = Visibility.Visible;
        _readerSelectionPopup = new Popup
        {
            Child = ReaderSelectionBar,
            IsLightDismissEnabled = false,
            IsOpen = false
        };

        ReaderPane.Children.Remove(ReaderFootnotePopup);
        ReaderFootnotePopup.Margin = new Thickness(0);
        ReaderFootnotePopup.Visibility = Visibility.Visible;
        _readerFootnotePopup = new Popup
        {
            Child = ReaderFootnotePopup,
            IsLightDismissEnabled = false,
            IsOpen = false
        };
    }

    private void ResetReaderToolsSession()
    {
        ResetReaderFootnoteSession();
        _readerLayout = new ReaderLayoutSettings();
        _readerBookmarks.Clear();
        _readerSelectionText = null;
        _pendingReaderRestorePosition = null;
        _pendingReaderBookmarkQuote = null;
        _savedReaderProgress = null;
        _readerLastProgress = null;
        _readerProgressLastSave = DateTimeOffset.MinValue;
        _readerActiveSeconds = 0;
        _readerStatsBaseSeconds = 0;
        _readerSessionStart = DateTimeOffset.UtcNow;
        _readerBookmarkTabActive = false;
        _readerSearchVisible = false;
    }

    private void StopReaderToolsTimers()
    {
        StopReaderFootnoteHoverPoll();
        StopReaderSelectionPoll();
        StopReaderStatsTimer();
        HideReaderSearchPanel();
        HideReaderSelectionPopup();
        if (_readerLayoutPopup is not null) _readerLayoutPopup.IsOpen = false;
        _readerLayoutApplyCancellation?.Cancel();
        _readerLayoutApplyCancellation?.Dispose();
        _readerLayoutApplyCancellation = null;
    }

    private void StartReaderToolsTimers()
    {
        StartReaderFootnoteHoverPoll();
        StartReaderSelectionPoll();
        StartReaderStatsTimer();
    }

    // ------------------------------------------------------------------
    // Reading layout settings (per book).
    // ------------------------------------------------------------------

    private void ReaderLayoutSettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ReaderMoreButton.Flyout?.Hide();
        ShowReaderLayoutSettings();
    }

    private void ShowReaderLayoutSettings()
    {
        if (_readerLayoutPopup is null || RootGrid.XamlRoot is null) return;
        PopulateReaderLayoutControls();
        var viewport = RootGrid.XamlRoot.Size;
        _readerLayoutPopup.XamlRoot = RootGrid.XamlRoot;
        ReaderLayoutSettingsOverlay.Width = viewport.Width;
        ReaderLayoutSettingsOverlay.Height = Math.Max(0, viewport.Height - 38);
        _readerLayoutPopup.IsOpen = true;
    }

    private void ReaderLayoutSettingsCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_readerLayoutPopup is not null) _readerLayoutPopup.IsOpen = false;
    }

    private void PopulateReaderLayoutControls()
    {
        _suppressReaderLayoutChange = true;
        ReaderFontScaleSlider.Value = Math.Clamp(_readerLayout.FontScale, 0.8, 1.8);
        ReaderLineHeightSlider.Value = Math.Clamp(_readerLayout.LineHeight, 1.3, 2.6);
        ReaderMaxWidthSlider.Value = Math.Clamp(_readerLayout.MaxWidth, 480, 1200);
        ReaderBodyPaddingSlider.Value = Math.Clamp(_readerLayout.BodyPadding, 24, 160);
        var font = _readerLayout.FontFamily?.Trim() ?? string.Empty;
        ReaderFontFamilyBox.SelectedItem = ReaderFontFamilyBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => (item.Tag as string)?.Equals(font, StringComparison.OrdinalIgnoreCase) == true)
            ?? ReaderFontFamilyBox.Items[0];
        ReaderVerticalWritingCheck.IsChecked = _readerLayout.VerticalWriting;
        _suppressReaderLayoutChange = false;
        UpdateReaderLayoutValueLabels();
        UpdateReaderLayoutStatus();
    }

    private void UpdateReaderLayoutFromControls()
    {
        _readerLayout = new ReaderLayoutSettings(
            ReaderFontScaleSlider.Value,
            ReaderLineHeightSlider.Value,
            ReaderMaxWidthSlider.Value,
            ReaderBodyPaddingSlider.Value,
            (ReaderFontFamilyBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty,
            _readerFlowMode,
            ReaderVerticalWritingCheck.IsChecked == true);
    }

    private void ReaderLayoutSettingChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressReaderLayoutChange) return;
        if (!AreReaderLayoutControlsReady()) return;
        UpdateReaderLayoutFromControls();
        UpdateReaderLayoutValueLabels();
        UpdateReaderLayoutStatus();
        ScheduleReaderLayoutApply();
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

    private void ReaderFontFamilyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressReaderLayoutChange) return;
        if (!AreReaderLayoutControlsReady()) return;
        UpdateReaderLayoutFromControls();
        UpdateReaderLayoutStatus();
        ScheduleReaderLayoutApply();
    }

    private void ReaderVerticalWritingCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressReaderLayoutChange) return;
        UpdateReaderLayoutFromControls();
        UpdateReaderLayoutStatus();
        ScheduleReaderLayoutApply();
    }

    private void UpdateReaderLayoutValueLabels()
    {
        if (ReaderFontScaleValueText is null) return;
        ReaderFontScaleValueText.Text = $"{_readerLayout.FontScale:P0}";
        ReaderLineHeightValueText.Text = _readerLayout.LineHeight.ToString("0.00");
        ReaderMaxWidthValueText.Text = $"{(int)_readerLayout.MaxWidth} px";
        ReaderBodyPaddingValueText.Text = $"{(int)_readerLayout.BodyPadding} px";
    }

    private void UpdateReaderLayoutStatus()
    {
        if (ReaderLayoutSettingsStatusText is null) return;
        ReaderLayoutSettingsStatusText.Text = _readerLayout.VerticalWriting && _readerFlowMode != 0
            ? "竖排仅用于滚动模式；分页模式下竖排暂不生效。"
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
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (token.IsCancellationRequested) return;
                try
                {
                    UpdateReaderZoomLabel();
                    await ApplyReaderAppearanceAsync();
                    await ClampReaderScrollAsync();
                    await SaveReaderLayoutSettingsAsync();
                }
                catch
                {
                }
            });
        });
    }

    private async Task SaveReaderLayoutSettingsAsync()
    {
        var book = _readerBook;
        var bookFile = _readerBookFile;
        var token = _readerFeatureCancellation?.Token ?? CancellationToken.None;
        if (book is null || bookFile is null) return;
        try
        {
            await _readerData.SaveLayoutSettingsAsync(book.Id, bookFile.Id, _readerLayout, token);
        }
        catch
        {
        }
    }

    private async void ReaderLayoutResetButton_Click(object sender, RoutedEventArgs e)
    {
        var flowMode = _readerFlowMode;
        _suppressReaderLayoutChange = true;
        _readerLayout = _readerLayout with
        {
            FontScale = 1.0,
            LineHeight = 1.88,
            MaxWidth = 800,
            BodyPadding = 68,
            FontFamily = string.Empty,
            VerticalWriting = false
        };
        ReaderFontScaleSlider.Value = 1.0;
        ReaderLineHeightSlider.Value = 1.88;
        ReaderMaxWidthSlider.Value = 800;
        ReaderBodyPaddingSlider.Value = 68;
        ReaderFontFamilyBox.SelectedItem = ReaderFontFamilyBox.Items[0];
        ReaderVerticalWritingCheck.IsChecked = false;
        _suppressReaderLayoutChange = false;
        UpdateReaderLayoutValueLabels();
        UpdateReaderLayoutStatus();
        UpdateReaderZoomLabel();
        await SaveReaderLayoutSettingsAsync();
        await ApplyReaderAppearanceAsync();
        await ClampReaderScrollAsync();
        ReaderLayoutSettingsStatusText.Text = "已恢复默认排版。";
    }

    // ------------------------------------------------------------------
    // Reading progress (breakpoint restore + throttled saving).
    // ------------------------------------------------------------------

    private async Task<ReaderProgressRow?> CaptureReaderProgressAsync(Book? book = null, BookFile? bookFile = null)
    {
        book ??= _readerBook;
        bookFile ??= _readerBookFile;
        if (book is null || bookFile is null || ReaderWebView.CoreWebView2 is null) return null;
        if (_readerChapterIndex < 0 || _readerChapters.Count == 0) return null;
        var chapterPath = GetCurrentReaderChapterPath();
        if (chapterPath is null) return null;

        double position = 0;
        double ratio = 0;
        try
        {
            var json = await ReaderWebView.CoreWebView2.ExecuteScriptAsync(
                "(function(){var el=document.scrollingElement||document.documentElement;return {st:el.scrollTop||0,sl:el.scrollLeft||0,sh:el.scrollHeight||0,sw:el.scrollWidth||0,ch:el.clientHeight||0,cw:el.clientWidth||0};})()");
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var scrollTop = root.TryGetProperty("st", out var st) ? st.GetDouble() : 0;
            var scrollLeft = root.TryGetProperty("sl", out var sl) ? sl.GetDouble() : 0;
            var scrollHeight = root.TryGetProperty("sh", out var sh) ? sh.GetDouble() : 0;
            var scrollWidth = root.TryGetProperty("sw", out var sw) ? sw.GetDouble() : 0;
            var clientHeight = root.TryGetProperty("ch", out var ch) ? ch.GetDouble() : 0;
            var clientWidth = root.TryGetProperty("cw", out var cw) ? cw.GetDouble() : 0;

            var horizontal = _readerFlowMode == 1 || _readerLayout.VerticalWriting;
            var axisMax = horizontal ? Math.Max(0, scrollWidth - clientWidth) : Math.Max(0, scrollHeight - clientHeight);
            position = horizontal ? scrollLeft : scrollTop;
            if (axisMax > 0) ratio = Math.Clamp(position / axisMax, 0, 1);
        }
        catch
        {
            return null;
        }

        var percent = (int)Math.Round((_readerChapterIndex + ratio) * 100d / _readerChapters.Count);
        return new ReaderProgressRow(
            book.Id,
            bookFile.Id,
            chapterPath,
            ReaderWebView.Source?.Fragment.TrimStart('#'),
            _readerChapterIndex,
            (int)Math.Round(position),
            percent,
            _readerFlowMode,
            DateTimeOffset.UtcNow);
    }

    private async Task SaveReaderProgressThrottledAsync(bool force = false)
    {
        var book = _readerBook;
        var bookFile = _readerBookFile;
        var token = _readerFeatureCancellation?.Token ?? CancellationToken.None;
        if (book is null || bookFile is null) return;
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _readerProgressLastSave < TimeSpan.FromSeconds(4)) return;
        _readerProgressLastSave = now;
        try
        {
            var progress = await CaptureReaderProgressAsync();
            if (progress is null) return;
            await _readerData.SaveProgressAsync(progress, token);
            _readerLastProgress = progress;
            UpdateReaderStatsDisplay();
        }
        catch
        {
        }
    }

    private async Task RefreshReaderProgressAsync()
    {
        if (_readerChapterIndex < 0 || _readerChapters.Count == 0) return;
        var progress = await CaptureReaderProgressAsync();
        if (progress is null) return;
        _readerLastProgress = progress;
        var current = progress.ChapterIndex + 1;
        ReaderReadingProgressText.Text = $"已读 {current} / {_readerChapters.Count} 章";
        ReaderProgressPercentText.Text = ReaderFormatting.FormatPercent(progress.ProgressPercent);
        UpdateReaderStatsDisplay();
    }

    private async Task ApplyReaderRestorePositionAsync()
    {
        if (ReaderWebView.CoreWebView2 is null) return;
        var position = _pendingReaderRestorePosition;
        _pendingReaderRestorePosition = null;
        if (position is not int restore) return;
        var horizontal = _readerFlowMode == 1 || _readerLayout.VerticalWriting;
        var script = horizontal
            ? $"window.scrollTo({{ left: {restore}, top: 0 }});"
            : $"window.scrollTo({{ top: {restore} }});";
        try { await ReaderWebView.CoreWebView2.ExecuteScriptAsync(script); }
        catch { }
        // A breakpoint saved from an older layout can land mid-column; snap it
        // onto the nearest full page so pagination never opens on a split view.
        if (_readerFlowMode == 1) await SnapReaderPaginationAsync();
    }

    // ------------------------------------------------------------------
    // Bookmarks.
    // ------------------------------------------------------------------

    private async void ReaderBookmarkButton_Click(object sender, RoutedEventArgs e) => await ToggleReaderBookmarkAsync();

    private async Task ToggleReaderBookmarkAsync()
    {
        var book = _readerBook;
        var bookFile = _readerBookFile;
        var token = _readerFeatureCancellation?.Token ?? CancellationToken.None;
        if (book is null || bookFile is null) return;
        var chapterPath = GetCurrentReaderChapterPath();
        if (chapterPath is null)
        {
            ReaderStatusText.Text = "当前页面无法保存书签";
            return;
        }
        var fragment = ReaderWebView.Source?.Fragment.TrimStart('#');
        var quote = await CaptureReaderSelectionTextAsync() ?? await CaptureCurrentSectionQuoteAsync();

        var existing = _readerBookmarks.FirstOrDefault(bookmark =>
            bookmark.ChapterPath.Equals(chapterPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(bookmark.Fragment, fragment, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(bookmark.Quote)
                || string.IsNullOrWhiteSpace(quote)
                || bookmark.Quote.Equals(quote, StringComparison.OrdinalIgnoreCase)));
        try
        {
            if (existing is not null)
            {
                await _readerData.DeleteBookmarkAsync(existing.Id, token);
                _readerBookmarks.Remove(existing);
                ReaderStatusText.Text = "已取消书签";
            }
            else
            {
                var bookmark = new ReaderBookmark
                {
                    BookId = book.Id,
                    BookFileId = bookFile.Id,
                    ChapterPath = chapterPath,
                    Fragment = fragment,
                    ChapterIndex = _readerChapterIndex,
                    Title = GetCurrentReaderChapterTitle(),
                    Quote = quote ?? string.Empty,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                await _readerData.SaveBookmarkAsync(bookmark, token);
                _readerBookmarks.Add(bookmark);
                ReaderStatusText.Text = "书签已保存（Ctrl+B 可再次取消）";
            }
        }
        catch (Exception exception)
        {
            ReaderStatusText.Text = $"书签保存失败：{exception.Message}";
        }
        RefreshReaderBookmarkList();
    }

    private async Task<string?> CaptureCurrentSectionQuoteAsync()
    {
        var section = await ExecuteReaderStringScriptAsync(GetReaderSectionTextScript());
        var normalized = NormalizeReaderText(section ?? string.Empty);
        return normalized.Length <= 40 ? normalized : normalized[..40];
    }

    private void RefreshReaderBookmarkList()
    {
        ReaderBookmarkList.ItemsSource = _readerBookmarks
            .OrderBy(bookmark => bookmark.ChapterIndex)
            .ThenBy(bookmark => bookmark.CreatedAt)
            .ToArray();
        ReaderBookmarkList.Visibility = _readerBookmarks.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ReaderBookmarkEmptyText.Visibility = _readerBookmarks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ReaderBookmarkList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ReaderBookmark bookmark) NavigateToReaderBookmark(bookmark);
    }

    private void NavigateToReaderBookmark(ReaderBookmark bookmark)
    {
        if (_readerAllowedRoot is null) return;
        var relative = bookmark.ChapterPath.Replace('/', Path.DirectorySeparatorChar);
        var targetPath = Path.GetFullPath(Path.Combine(_readerAllowedRoot, relative));
        if (!IsPathInside(_readerAllowedRoot, targetPath) || !File.Exists(targetPath)) return;
        var chapterIndex = _readerChapters.ToList().FindIndex(chapter =>
            Path.GetFullPath(chapter).Equals(targetPath, StringComparison.OrdinalIgnoreCase));
        if (chapterIndex < 0) return;

        _readerChapterIndex = chapterIndex;
        _readerNavigateToEnd = false;
        _pendingReaderBookmarkQuote = bookmark.Quote;
        UpdateReaderChapterControls();
        var target = new Uri(targetPath).AbsoluteUri;
        if (!string.IsNullOrWhiteSpace(bookmark.Fragment)) target += $"#{bookmark.Fragment}";
        if (ReaderWebView.Source?.AbsoluteUri.Equals(target, StringComparison.OrdinalIgnoreCase) == true)
            _ = ScrollToPendingReaderBookmarkAsync();
        else
            _ = NavigateReaderSourceAsync(new Uri(target), 1, animate: true, ReaderNavigationIntent.Bookmark);
    }

    private async void ReaderBookmarkDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var token = _readerFeatureCancellation?.Token ?? CancellationToken.None;
        if (sender is not Button { DataContext: ReaderBookmark bookmark }) return;
        try
        {
            await _readerData.DeleteBookmarkAsync(bookmark.Id, token);
            _readerBookmarks.Remove(bookmark);
            RefreshReaderBookmarkList();
            ReaderStatusText.Text = "书签已删除";
        }
        catch (Exception exception)
        {
            ReaderStatusText.Text = $"删除书签失败：{exception.Message}";
        }
    }

    private async Task ScrollToPendingReaderBookmarkAsync()
    {
        if (_pendingReaderBookmarkQuote is not string quote || ReaderWebView.CoreWebView2 is null) return;
        _pendingReaderBookmarkQuote = null;
        if (quote.Length < 2) return;
        var needle = quote.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", " ");
        var script = $$"""
            (() => {
              const root = document.body;
              if (!root) return;
              const text = root.textContent || '';
              const index = text.indexOf('{{needle}}');
              if (index < 0) return;
              let cursor = 0;
              const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
                acceptNode(node) {
                  const parent = node.parentElement;
                  return !parent || ['SCRIPT','STYLE','NOSCRIPT'].includes(parent.tagName)
                    ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT;
                }
              });
              while (walker.nextNode()) {
                const node = walker.currentNode;
                if (cursor + node.data.length >= index) {
                  (node.parentElement || root).scrollIntoView({ block: 'center', behavior: 'smooth' });
                  return;
                }
                cursor += node.data.length;
              }
            })();
            """;
        try { await ReaderWebView.CoreWebView2.ExecuteScriptAsync(script); }
        catch { }
        if (_readerFlowMode == 1) await SnapReaderPaginationAsync();
    }

    private void ReaderTocTabButton_Click(object sender, RoutedEventArgs e) => SetReaderTocTab(bookmarkTab: false);

    private void ReaderBookmarkTabButton_Click(object sender, RoutedEventArgs e) => SetReaderTocTab(bookmarkTab: true);

    private void SetReaderTocTab(bool bookmarkTab)
    {
        _readerBookmarkTabActive = bookmarkTab;
        ReaderTocTabButton.Background = new SolidColorBrush(bookmarkTab ? Colors.Transparent : Colors.Black);
        ReaderTocTabButton.Foreground = new SolidColorBrush(bookmarkTab ? ColorHelper.FromArgb(255, 36, 36, 36) : Colors.White);
        ReaderBookmarkTabButton.Background = new SolidColorBrush(bookmarkTab ? Colors.Black : Colors.Transparent);
        ReaderBookmarkTabButton.Foreground = new SolidColorBrush(bookmarkTab ? Colors.White : ColorHelper.FromArgb(255, 36, 36, 36));
        ReaderTocSearchBox.Visibility = bookmarkTab ? Visibility.Collapsed : Visibility.Visible;
        ReaderTocList.Visibility = bookmarkTab ? Visibility.Collapsed : Visibility.Visible;
        ReaderTocEmptyText.Visibility = bookmarkTab || _readerHasToc ? Visibility.Collapsed : Visibility.Visible;
        ReaderBookmarkPane.Visibility = bookmarkTab ? Visibility.Visible : Visibility.Collapsed;
        RefreshReaderBookmarkList();
    }

    // ------------------------------------------------------------------
    // Whole-book search (local FTS with LIKE fallback; never calls AI and
    // never uploads content).
    // ------------------------------------------------------------------

    private void ReaderSearchToolbarButton_Click(object sender, RoutedEventArgs e) => ShowReaderSearchPanel();

    private void CloseReaderSearchButton_Click(object sender, RoutedEventArgs e) => HideReaderSearchPanel();

    private void ShowReaderSearchPanel()
    {
        if (_readerSearchPopup is null || RootGrid.XamlRoot is null) return;
        var viewport = RootGrid.XamlRoot.Size;
        var width = Math.Min(360, Math.Max(280, viewport.Width - 24));
        var height = Math.Min(540, Math.Max(240, viewport.Height - 90));
        _readerSearchPopup.XamlRoot = RootGrid.XamlRoot;
        ReaderSearchPanel.Width = width;
        ReaderSearchPanel.MaxHeight = height;
        ReaderSearchPanel.Visibility = Visibility.Visible;
        _readerSearchPopup.HorizontalOffset = Math.Max(0, viewport.Width - width - 16);
        _readerSearchPopup.VerticalOffset = 48;
        _readerSearchPopup.IsOpen = true;
        _readerSearchVisible = true;
        if (string.IsNullOrWhiteSpace(ReaderSearchStatusText.Text))
            ReaderSearchStatusText.Text = "输入关键词后按 Enter 或点击“搜索”。";
        ReaderSearchBox.Focus(FocusState.Programmatic);
    }

    private void HideReaderSearchPanel()
    {
        _readerSearchVisible = false;
        if (_readerSearchPopup is not null) _readerSearchPopup.IsOpen = false;
    }

    private async void ReaderSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        await RunReaderSearchAsync(ReaderSearchBox.Text.Trim());
    }

    private async void ReaderSearchButton_Click(object sender, RoutedEventArgs e)
    {
        await RunReaderSearchAsync(ReaderSearchBox.Text.Trim());
    }

    private async Task RunReaderSearchAsync(string query)
    {
        var book = _readerBook;
        var token = _readerFeatureCancellation?.Token ?? CancellationToken.None;
        if (book is null || _readerBookFile is null)
        {
            ReaderSearchStatusText.Text = "请先打开 EPUB 再搜索。";
            return;
        }
        if (query.Length == 0)
        {
            ReaderSearchStatusText.Text = "请输入搜索关键词。";
            return;
        }
        ReaderSearchStatusText.Text = "正在本地搜索…";
        ReaderSearchResultList.ItemsSource = null;
        try
        {
            if (_readerIndexTask is not null) await _readerIndexTask;
            var results = await _readerData.SearchBookAsync(book.Id, query, 40, token);
            ReaderSearchResultList.ItemsSource = results;
            ReaderSearchCountText.Text = $"{results.Count} 条结果 · 仅本地检索，不上传正文";
            ReaderSearchStatusText.Text = results.Count == 0
                ? "没有找到匹配的片段。"
                : "点击结果跳转到对应章节和片段。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReaderSearchStatusText.Text = $"搜索失败：{exception.Message}";
        }
    }

    private async void ReaderSearchResultList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BookContentChunk source) await NavigateToReaderChunkAsync(source);
    }

    private async Task NavigateToReaderChunkAsync(BookContentChunk source)
    {
        if (_readerAllowedRoot is null) return;
        var targetPath = Path.GetFullPath(Path.Combine(
            _readerAllowedRoot,
            source.ChapterPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsPathInside(_readerAllowedRoot, targetPath) || !File.Exists(targetPath)) return;
        _readerChapterIndex = Math.Clamp(source.ChapterIndex, 0, _readerChapters.Count - 1);
        _pendingReaderChunkOffset = source.StartOffset;
        _readerNavigateToEnd = false;
        UpdateReaderChapterControls();
        var target = new Uri(targetPath);
        if (ReaderWebView.Source?.LocalPath.Equals(target.LocalPath, StringComparison.OrdinalIgnoreCase) == true)
            await ScrollToPendingReaderChunkAsync();
        else
            await NavigateReaderSourceAsync(target, 1, animate: true, ReaderNavigationIntent.Search);
        ReaderSearchStatusText.Text = $"已跳转到《{source.ChapterTitle}》相关位置。";
    }

    // ------------------------------------------------------------------
    // Text-selection quick action bar. The page scripts stay disabled
    // (IsScriptEnabled=false), which freezes DOM event dispatch, so the
    // host polls the current selection on a timer and shows a monochrome
    // action bar near the selection rectangle. Actions reuse the existing
    // production code paths (annotation save, AI explain, book search).
    // ------------------------------------------------------------------

    private static string GetReaderSelectionStateScript() =>
        """
        (() => {
          const sel = window.getSelection();
          if (!sel || sel.rangeCount === 0 || sel.isCollapsed) return null;
          const text = (sel.toString() || '').trim();
          if (!text) return null;
          const range = sel.getRangeAt(0);
          const rect = range.getBoundingClientRect();
          if (!rect || (rect.width < 2 && rect.height < 2)) return null;
          const doc = document.scrollingElement || document.documentElement;
          return {
            text: text.slice(0, 500),
            left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom,
            vw: doc.clientWidth || window.innerWidth || 0,
            vh: doc.clientHeight || window.innerHeight || 0
          };
        })();
        """;

    private void StartReaderSelectionPoll()
    {
        if (_readerSelectionTimer is null)
        {
            _readerSelectionTimer = DispatcherQueue.CreateTimer();
            _readerSelectionTimer.Interval = TimeSpan.FromMilliseconds(300);
            _readerSelectionTimer.Tick += async (_, _) => await PollReaderSelectionAsync();
        }
        _readerSelectionTimer.Start();
    }

    private void StopReaderSelectionPoll()
    {
        _readerSelectionTimer?.Stop();
        HideReaderSelectionPopup();
    }

    private async Task PollReaderSelectionAsync()
    {
        if (_readerPollingSelection) return;
        if (ReaderPane.Visibility != Visibility.Visible) return;
        if (_readerLayoutPopup?.IsOpen == true || _readerSettingsPopup?.IsOpen == true) return;
        if (_readerSearchVisible)
        {
            HideReaderSelectionPopup();
            return;
        }
        if (ReaderWebView.CoreWebView2 is null || _readerAllowedRoot is null) return;
        _readerPollingSelection = true;
        try
        {
            var info = await ExecuteReaderJsonScriptAsync<SelectionInfo>(GetReaderSelectionStateScript());
            if (info is null || string.IsNullOrWhiteSpace(info.Text))
            {
                HideReaderSelectionPopup();
                return;
            }
            _readerSelectionText = info.Text;
            ShowReaderSelectionPopup(info);
        }
        catch
        {
        }
        finally { _readerPollingSelection = false; }
    }

    private void ShowReaderSelectionPopup(SelectionInfo info)
    {
        if (_readerSelectionPopup is null || RootGrid.XamlRoot is null) return;
        if (info.Vw <= 0 || info.Vh <= 0) return;
        var hostRect = GetReaderWebViewScreenRect();
        if (hostRect.Width <= 0 || hostRect.Height <= 0) return;
        var scale = ReaderWebViewHost.XamlRoot?.RasterizationScale ?? 1.0;
        var hwnd = WindowNative.GetWindowHandle(this);
        var origin = new POINT { X = 0, Y = 0 };
        _ = ClientToScreen(hwnd, ref origin);

        var screenX = hostRect.Left + (info.Left / info.Vw) * hostRect.Width;
        var screenY = hostRect.Top + (info.Top / info.Vh) * hostRect.Height;
        var dipX = (screenX - origin.X) / scale;
        var dipY = (screenY - origin.Y) / scale;

        _readerSelectionPopup.XamlRoot = RootGrid.XamlRoot;
        ReaderSelectionBar.Visibility = Visibility.Visible;
        ReaderSelectionBar.Measure(new Windows.Foundation.Size(double.PositiveInfinity, 48));
        var barWidth = Math.Max(200, ReaderSelectionBar.DesiredSize.Width);
        var barHeight = Math.Max(30, ReaderSelectionBar.DesiredSize.Height);
        var viewport = RootGrid.XamlRoot.Size;
        var left = Math.Clamp(dipX, 8, Math.Max(8, viewport.Width - barWidth - 8));
        var top = dipY - barHeight - 10;
        if (top < 46) top = dipY + (info.Bottom - info.Top) + 12;
        top = Math.Clamp(top, 46, Math.Max(46, viewport.Height - barHeight - 8));
        _readerSelectionPopup.HorizontalOffset = left;
        _readerSelectionPopup.VerticalOffset = top;
        ReaderSelectionBar.Width = barWidth;
        if (!_readerSelectionPopup.IsOpen) _readerSelectionPopup.IsOpen = true;
    }

    private void HideReaderSelectionPopup()
    {
        if (_readerSelectionPopup is not null) _readerSelectionPopup.IsOpen = false;
        _readerSelectionText = null;
    }

    private async void ReaderSelectionCopyButton_Click(object sender, RoutedEventArgs e)
    {
        var text = _readerSelectionText ?? await CaptureReaderSelectionTextAsync();
        HideReaderSelectionPopup();
        if (string.IsNullOrWhiteSpace(text))
        {
            ReaderStatusText.Text = "没有可复制的文字";
            return;
        }
        var dataPackage = new DataPackage();
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);
        ReaderStatusText.Text = "已复制选中文字";
    }

    private async void ReaderSelectionHighlightButton_Click(object sender, RoutedEventArgs e)
    {
        HideReaderSelectionPopup();
        var selection = await CaptureReaderSelectionAsync();
        if (selection is null)
        {
            ReaderStatusText.Text = "请先在正文中选择一段文字";
            return;
        }
        await SaveReaderAnnotationAsync(selection, string.Empty, preserveExistingNote: true);
    }

    private async void ReaderSelectionAnnotateButton_Click(object sender, RoutedEventArgs e)
    {
        HideReaderSelectionPopup();
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

    private void ReaderSelectionAiExplainButton_Click(object sender, RoutedEventArgs e)
    {
        HideReaderSelectionPopup();
        ReaderAiExplainSelectionButton_Click(this, new RoutedEventArgs());
    }

    private async void ReaderSelectionSearchButton_Click(object sender, RoutedEventArgs e)
    {
        var text = _readerSelectionText ?? await CaptureReaderSelectionTextAsync();
        HideReaderSelectionPopup();
        ShowReaderSearchPanel();
        ReaderSearchBox.Text = text ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(ReaderSearchBox.Text))
            await RunReaderSearchAsync(ReaderSearchBox.Text);
    }

    private async Task<string?> CaptureReaderSelectionTextAsync()
    {
        if (ReaderWebView.CoreWebView2 is null) return null;
        var text = await ExecuteReaderStringScriptAsync("window.getSelection ? window.getSelection().toString() : ''");
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    // ------------------------------------------------------------------
    // Annotation export (Markdown / plain text) via the Windows save picker.
    // ------------------------------------------------------------------

    private async void ReaderExportMarkdownButton_Click(object sender, RoutedEventArgs e) =>
        await ExportReaderAnnotationsAsync(markdown: true);

    private async void ReaderExportTextButton_Click(object sender, RoutedEventArgs e) =>
        await ExportReaderAnnotationsAsync(markdown: false);

    private async Task ExportReaderAnnotationsAsync(bool markdown)
    {
        var book = _readerBook;
        var bookFile = _readerBookFile;
        if (book is null || bookFile is null)
        {
            ReaderExportStatusText.Text = "请先打开 EPUB 再导出。";
            return;
        }
        if (_readerAnnotations.Count == 0)
        {
            ReaderExportStatusText.Text = "本书还没有划线与批注，没有可导出的内容。";
            return;
        }

        string content;
        try
        {
            content = markdown
                ? ReaderAnnotationExport.BuildMarkdown(book.Title, book.Authors, _readerAnnotations.ToArray(), ResolveChapterTitle)
                : ReaderAnnotationExport.BuildPlainText(book.Title, book.Authors, _readerAnnotations.ToArray(), ResolveChapterTitle);
        }
        catch (Exception exception)
        {
            ReaderExportStatusText.Text = $"导出失败：{exception.Message}";
            return;
        }

        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add(markdown ? "Markdown 文档" : "纯文本", [markdown ? ".md" : ".txt"]);
        var safeTitle = string.Concat((book.Title ?? "book").Where(character => !Path.GetInvalidFileNameChars().Contains(character))).Trim();
        if (string.IsNullOrWhiteSpace(safeTitle)) safeTitle = "book";
        picker.SuggestedFileName = $"{safeTitle}-{(bookFile.Format ?? "reader")}-{(markdown ? "annotations" : "annotations")}";

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            ReaderExportStatusText.Text = "已取消导出，笔记未被修改。";
            return;
        }

        try
        {
            await File.WriteAllTextAsync(file.Path, content);
            ReaderExportStatusText.Text = $"已导出到：{file.Path}";
        }
        catch (Exception exception)
        {
            ReaderExportStatusText.Text = $"写入失败：{exception.Message}";
        }
    }

    private string ResolveChapterTitle(string chapterPath)
    {
        if (_readerAllowedRoot is null) return chapterPath;
        var relative = chapterPath.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_readerAllowedRoot, relative));
        var index = _readerChapters.ToList().FindIndex(chapter =>
            Path.GetFullPath(chapter).Equals(full, StringComparison.OrdinalIgnoreCase));
        return index >= 0
            ? _readerNavigation.FirstOrDefault(item => item.ChapterIndex == index)?.Title ?? $"第 {index + 1} 章"
            : chapterPath;
    }

    // ------------------------------------------------------------------
    // Reading stats: cumulative active reading time plus a progress
    // snapshot. Time only accrues while the window is active and the
    // reader pane is visible, so simply leaving the book open is not
    // counted as reading time.
    // ------------------------------------------------------------------

    private void StartReaderStatsTimer()
    {
        if (_readerStatsTimer is null)
        {
            _readerStatsTimer = DispatcherQueue.CreateTimer();
            _readerStatsTimer.Interval = TimeSpan.FromSeconds(1);
            _readerStatsTimer.Tick += ReaderStatsTimer_Tick;
        }
        _readerStatsTimer.Start();
    }

    private void StopReaderStatsTimer()
    {
        _readerStatsTimer?.Stop();
    }

    private void ReaderStatsTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!_windowActive || ReaderPane.Visibility != Visibility.Visible) return;
        _readerActiveSeconds++;
        if (_readerActiveSeconds % 30 == 0) _ = FlushReaderSessionSafelyAsync();
        UpdateReaderStatsDisplay();
    }

    // Flush used on close paths. It must be started while the reader session
    // fields are still populated (CloseReader/窗口关闭前) so the last known
    // snapshot survives; it never touches the WebView when skipWebViewCapture
    // is set, so it can never hang on a WebView that is navigating or being
    // torn down.
    private async Task FlushReaderSessionAsync(bool skipWebViewCapture = false)
    {
        var book = _readerBook;
        var bookFile = _readerBookFile;
        // Close-path flushes must not be cancelled by EndReaderSession's
        // _readerFeatureCancellation.Cancel() (which runs right after the flush
        // is kicked off); the bounded timeout in FlushReaderSessionSafelyAsync
        // is what keeps the close fast.
        var token = skipWebViewCapture
            ? CancellationToken.None
            : _readerFeatureCancellation?.Token ?? CancellationToken.None;
        if (book is null || bookFile is null) return;
        if (_readerActiveSeconds > 0)
        {
            var percent = _readerLastProgress?.ProgressPercent ?? 0;
            var completed = _readerLastProgress is { ChapterIndex: >= 0 } last
                ? last.ChapterIndex + 1
                : (_readerChapterIndex >= 0 ? _readerChapterIndex + 1 : 0);
            var total = _readerChapters.Count;
            // Reset the counter synchronously before the first await so a
            // concurrent flush (stats timer vs close) can never double-count.
            var activeSeconds = _readerActiveSeconds;
            _readerActiveSeconds = 0;
            try
            {
                await _readerData.AddReadingTimeAsync(
                    book.Id,
                    bookFile.Id,
                    activeSeconds,
                    percent,
                    completed,
                    total,
                    token);
            }
            catch
            {
            }
        }
        try
        {
            ReaderProgressRow? progress;
            if (skipWebViewCapture)
            {
                // Use the most recently captured snapshot (updated on every
                // NavigationCompleted and throttled save) instead of issuing a
                // script call that could hang during close.
                progress = _readerLastProgress;
            }
            else
            {
                progress = await CaptureReaderProgressAsync(book, bookFile);
            }
            if (progress is not null)
            {
                await _readerData.SaveProgressAsync(progress, token);
                _readerLastProgress = progress;
            }
        }
        catch
        {
        }
    }

    // Bounded, non-blocking persistence for close paths. Never call .Wait()/
    // .Result() on the UI thread; the timeout simply abandons the flush if the
    // SQLite gate or a WebView script call cannot finish quickly, so the window
    // and reader close immediately either way.
    private async Task FlushReaderSessionSafelyAsync(bool skipWebViewCapture = false)
    {
        try
        {
            await FlushReaderSessionAsync(skipWebViewCapture).WaitAsync(TimeSpan.FromMilliseconds(1500));
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private void UpdateReaderStatsDisplay()
    {
        if (ReaderStatsText is null) return;
        if (_readerChapterIndex < 0 || _readerChapters.Count == 0)
        {
            ReaderStatsText.Text = string.Empty;
            return;
        }
        var cumulative = _readerStatsBaseSeconds + _readerActiveSeconds;
        ReaderStatsText.Text = $"累计阅读 {FormatReaderDuration(cumulative)} · 本次 {FormatReaderDuration(_readerActiveSeconds)}";
    }

    private static string FormatReaderDuration(long seconds)
    {
        if (seconds < 60) return $"{seconds} 秒";
        if (seconds < 3600) return $"{seconds / 60} 分钟";
        return $"{seconds / 3600.0:0.0} 小时";
    }

    // ------------------------------------------------------------------
    // Shared helpers.
    // ------------------------------------------------------------------

    private static string BuildReaderFontStack(string? fontFamily)
    {
        const string fallback = "\"Source Han Serif SC\", \"Noto Serif CJK SC\", \"Microsoft YaHei UI\", sans-serif";
        if (string.IsNullOrWhiteSpace(fontFamily)) return fallback;
        var names = string.Join(", ", fontFamily
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => $"\"{part.Trim('"')}\""));
        return $"{names}, {fallback}";
    }

    // ------------------------------------------------------------------
    // Layout settings safety net. Persisted per-book settings can carry
    // stale or corrupted values (NaN, out-of-range widths, invalid flow
    // modes) from older builds. Clamp every field to the supported ranges
    // so a bad row can never force an EPUB into an unreadable layout; the
    // rest of the user's reading data is never touched. The normalization
    // itself lives in Kkindle.Core.ReaderLayoutDefaults so it stays
    // unit-testable without the WinUI shell.
    // ------------------------------------------------------------------

    private static ReaderLayoutSettings NormalizeReaderLayoutSettings(ReaderLayoutSettings settings) =>
        ReaderLayoutDefaults.Normalize(settings);

    private void UpdateReaderZoomLabel()
    {
        ReaderZoomText.Text = $"{_readerLayout.FontScale:P0}";
    }

    private sealed class SelectionInfo
    {
        public string Text { get; set; } = string.Empty;
        public double Left { get; set; }
        public double Top { get; set; }
        public double Right { get; set; }
        public double Bottom { get; set; }
        public double Vw { get; set; }
        public double Vh { get; set; }
    }
}
