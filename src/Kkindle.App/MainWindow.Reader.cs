using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

/// <summary>
/// First Avalonia Kreader slice: EPUB preparation, a two-host native webview
/// surface, chapter switching and a local-file navigation guard. The richer
/// TOC, pagination and annotation tools stay behind this boundary for the
/// following reader slices.
/// </summary>
public partial class MainWindow
{
    private EpubReaderDocument? _readerDocument;
    private BookCardViewModel? _readerBookCard;
    private BookFile? _readerBookFile;
    private int _readerChapterIndex;
    private bool _readerShowingPreload;
    private CancellationTokenSource? _readerSessionCancellation;
    private CancellationTokenSource? _readerNavigationCancellation;
    private int _readerCloseInProgress;
    private readonly SemaphoreSlim _readerActiveHostNavigationGate = new(1, 1);
    private readonly SemaphoreSlim _readerPreloadHostNavigationGate = new(1, 1);

    private IReaderHost? CurrentReaderHost =>
        _readerShowingPreload ? _readerPreloadHost : _readerActiveHost;

    private IReaderHost? HiddenReaderHost =>
        _readerShowingPreload ? _readerActiveHost : _readerPreloadHost;

    private async Task OpenEpubReaderAsync(
        BookCardViewModel card,
        BookFile file,
        string epubPath)
    {
        _readerSessionCancellation?.Cancel();
        _readerSessionCancellation?.Dispose();
        _readerSessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        var sessionToken = _readerSessionCancellation.Token;

        try
        {
            SetTaskStatus($"正在准备《{card.Title}》的阅读缓存…");
            var contentHash = file.Sha256;
            if (contentHash.Length != 64)
                contentHash = await Hashing.Sha256Async(epubPath, sessionToken);

            var document = await _epubReader.PrepareAsync(
                epubPath,
                contentHash,
                sessionToken);

            _readerDocument = document;
            _readerBookCard = card;
            _readerBookFile = file;
            _readerChapterIndex = 0;
            _readerShowingPreload = false;
            await InitializeReaderInteractionAsync(document, file, sessionToken);

            var savedProgress = await _readerData.GetProgressAsync(file.Id, sessionToken);
            _readerRestoredProgress = ValidateReaderProgress(document, savedProgress);
            if (_readerRestoredProgress is { } progress)
            {
                _readerChapterIndex = progress.ChapterIndex;
                _readerScrollPosition = progress.ScrollPosition;
                _readerCurrentFragment = DecodeReaderFragment(progress.Fragment);
            }

            ReaderBookInfoText.Text = $"{card.Title} · {file.Format.ToUpperInvariant()}";
            ReaderChapterText.Text = GetReaderChapterLabel();
            ReaderStatusText.Text = $"共 {_readerTocItems.Count} 个章节 · 正在加载";
            ReaderRoot.IsVisible = true;
            LibraryRoot.IsVisible = false;
            WindowBrandText.IsVisible = true;
            ApplyReaderPanelLayout();

            await EnsureReaderHostsAsync();
            // Keep the first document behind the opaque reader surface until
            // its Kreader CSS and bundled font have been applied. Native
            // WebView2 paints the navigated XHTML before the first script
            // injection, so showing this slot here would expose the EPUB's
            // original typography for a frame.
            SetReaderHostLayer(revealActiveHost: false);

            var target = new Uri(document.Chapters[_readerChapterIndex]);
            if (!string.IsNullOrWhiteSpace(_readerCurrentFragment))
            {
                target = new Uri(
                    target.AbsoluteUri
                    + "#"
                    + Uri.EscapeDataString(_readerCurrentFragment));
            }
            var loaded = await NavigateReaderHostAndWaitAsync(
                CurrentReaderHost!,
                target,
                sessionToken);
            if (!loaded)
                throw new InvalidOperationException("阅读器无法加载 EPUB 章节。");

            SetReaderHostLayer();
            FocusCurrentReaderHost();
            SetReaderTocSelectionForLocation(_readerChapterIndex, _readerCurrentFragment);
            await UpdateReaderScrollStateAsync(CurrentReaderHost!);
            UpdateReaderToolbar();
            PrimeReaderContinuousEdgeTracking();

            await UpdateReaderBookmarkIndicatorAsync();
            await SaveReaderProgressAsync(sessionToken);
            _ = PreloadNextReaderChapterAsync(sessionToken);
        }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await CloseReaderAsync();
            SetTaskStatus($"打开 EPUB 阅读器失败：{exception.Message}");
        }
    }

    private static ReaderProgressRow? ValidateReaderProgress(
        EpubReaderDocument document,
        ReaderProgressRow? progress)
    {
        if (progress is null || string.IsNullOrWhiteSpace(progress.ChapterPath)) return null;

        try
        {
            var savedPath = Path.GetFullPath(Path.Combine(
                document.RootPath,
                progress.ChapterPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathInside(document.RootPath, savedPath)) return null;

            var chapterIndex = document.Chapters
                .Select((chapter, index) => (chapter, index))
                .Where(item => string.Equals(
                    Path.GetFullPath(item.chapter),
                    savedPath,
                    StringComparison.OrdinalIgnoreCase))
                .Select(item => item.index)
                .DefaultIfEmpty(-1)
                .First();
            if (chapterIndex < 0) return null;

            return progress with
            {
                ChapterIndex = chapterIndex,
                ScrollPosition = Math.Max(0, progress.ScrollPosition),
                Fragment = DecodeReaderFragment(progress.Fragment)
            };
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or IOException
                                          or NotSupportedException
                                          or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task EnsureReaderHostsAsync()
    {
        if (_readerActiveHost is null)
        {
            _readerActiveHost = _readerHostFactory();
            _readerPreloadHost = _readerHostFactory();
            if (ReferenceEquals(_readerActiveHost, _readerPreloadHost))
                throw new InvalidOperationException("阅读器宿主工厂必须返回两个不同实例。");

            _readerActiveHost.NavigationStarting += ReaderHost_NavigationStarting;
            _readerActiveHost.NavigationCompleted += ReaderHost_NavigationCompleted;
            _readerActiveHost.WebMessageReceived += ReaderHost_WebMessageReceived;
            _readerPreloadHost.NavigationStarting += ReaderHost_NavigationStarting;
            _readerPreloadHost.NavigationCompleted += ReaderHost_NavigationCompleted;
            _readerPreloadHost.WebMessageReceived += ReaderHost_WebMessageReceived;

            ReaderActiveHostSlot.Content = _readerActiveHost.View;
            ReaderPreloadHostSlot.Content = _readerPreloadHost.View;
        }

        await Task.WhenAll(
            _readerActiveHost.ReadyTask,
            _readerPreloadHost!.ReadyTask);
    }

    private async Task<bool> NavigateReaderHostAndWaitAsync(
        IReaderHost host,
        Uri target,
        CancellationToken cancellationToken)
    {
        var gate = ReferenceEquals(host, _readerPreloadHost)
            ? _readerPreloadHostNavigationGate
            : _readerActiveHostNavigationGate;
        await gate.WaitAsync(cancellationToken);
        try
        {
            // A TOC click can request a fragment in the chapter currently
            // prepared by the hidden host. Same-document WebView navigation
            // is not guaranteed to raise NavigationCompleted, so reuse the
            // loaded document and let ApplyReaderLocationAsync perform the
            // exact anchor/offset jump after the host swap.
            if (ReaderNavigationLocationPolicy.TargetsSameDocument(host.Source, target))
            {
                await ConfigureReaderHostAsync(host, cancellationToken);
                return true;
            }

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<ReaderNavigationCompletedEventArgs>? handler = null;
            handler = (_, args) =>
            {
                if (!ReaderNavigationLocationPolicy.TargetsSameDocument(args.Request, target)
                    && !UriEquals(args.Request, target)) return;
                completion.TrySetResult(args.IsSuccess);
            };
            host.NavigationCompleted += handler;
            try
            {
                host.Navigate(target);
                var loaded = await completion.Task.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken);
                if (loaded)
                    await ConfigureReaderHostAsync(host, cancellationToken);
                return loaded;
            }
            finally
            {
                host.NavigationCompleted -= handler;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task PreloadNextReaderChapterAsync(CancellationToken cancellationToken)
    {
        if (_readerDocument is null
            || HiddenReaderHost is not { } host
            || _readerChapterIndex >= _readerDocument.Chapters.Count - 1) return;

        var target = new Uri(_readerDocument.Chapters[_readerChapterIndex + 1]);
        try
        {
            await NavigateReaderHostAndWaitAsync(
                host,
                target,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Preloading is an optimization. The visible host remains usable
            // and will load the chapter on demand if this fails.
        }
    }

    private async Task MoveReaderChapterAsync(int offset, bool startAtChapterTitle = false)
    {
        await ResetReaderInPageSearchForNavigationAsync();
        _readerPendingBookmarkQuote = null;
        _readerPendingBookmarkPosition = null;
        _readerPendingBookmarkFlowMode = 0;
        _readerPendingAnnotation = null;
        if (_readerIsPdf)
        {
            await NavigatePdfPageAsync(_readerPdfPage + offset, ReaderToken);
            return;
        }
        if (_readerDocument is null || CurrentReaderHost is null) return;
        if (startAtChapterTitle
            && FindAdjacentReaderSubchapter(offset) is { } subchapter)
        {
            await NavigateToReaderItemAsync(
                subchapter,
                ReaderToken,
                ReaderNavigationIntent.Toc,
                transitionDirection: Math.Sign(offset));
            return;
        }

        _readerCurrentFragment = null;
        var targetIndex = _readerChapterIndex + offset;
        if (targetIndex < 0 || targetIndex >= _readerDocument.Chapters.Count)
        {
            ReaderStatusText.Text = offset < 0 ? "已经是第一章。" : "已经是最后一章。";
            return;
        }
        ResetReaderContinuousEdgeTracking();

        var sessionToken = _readerSessionCancellation?.Token ?? _lifetimeCancellation.Token;
        _readerNavigationCancellation?.Cancel();
        var navigationCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        _readerNavigationCancellation = navigationCancellation;
        var token = navigationCancellation.Token;
        var target = new Uri(_readerDocument.Chapters[targetIndex]);
        var host = HiddenReaderHost ?? CurrentReaderHost;
        try
        {
            ReaderStatusText.Text = $"正在加载第 {targetIndex + 1} 章…";
            var loaded = await NavigateReaderHostAndWaitAsync(host, target, token);
            if (!loaded) throw new InvalidOperationException("章节加载失败。");

            await ApplySavedAnnotationsAsync(host, token);
            await PositionReaderChapterBoundaryAsync(
                host,
                moveToEnd: offset < 0 && !startAtChapterTitle,
                token);
            var outgoingHost = CurrentReaderHost;
            await RunReaderContentTransitionAsync(
                outgoingHost,
                host,
                offset,
                async () =>
                {
                    _readerChapterIndex = targetIndex;
                    // host was picked as the hidden host, so the layer must flip
                    // unconditionally; deriving it from CurrentReaderHost would read
                    // the stale pre-swap flag and freeze the visible chapter after the
                    // first jump (TOC / next-chapter worked only once).
                    _readerShowingPreload = ReferenceEquals(host, _readerPreloadHost);
                    SetReaderHostLayer();
                    await UpdateReaderScrollStateAsync(host);
                    return true;
                },
                token);
            FocusCurrentReaderHost();
            PrimeReaderContinuousEdgeTracking();
            ReaderChapterText.Text = GetReaderChapterLabel();
            UpdateReaderToolbar();
            ReaderStatusText.Text = $"共 {_readerTocItems.Count} 个章节";
            SetReaderTocSelectionForChapter(targetIndex);
            await UpdateReaderBookmarkIndicatorAsync();
            await SaveReaderProgressAsync(sessionToken);
            _ = PreloadNextReaderChapterAsync(sessionToken);
            // Scroll mode keeps advancing across chapters that are too short to
            // scroll (WinUI reference's SkipShortChapterIfNeededAsync).
            if (!_readerIsPdf && _readerLayout.FlowMode == 0 && offset > 0)
                _ = SkipShortReaderChapterIfNeededAsync(targetIndex, sessionToken);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReaderStatusText.Text = $"章节加载失败：{exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_readerNavigationCancellation, navigationCancellation))
                _readerNavigationCancellation = null;
            navigationCancellation.Dispose();
        }
    }

    private async Task PositionReaderChapterBoundaryAsync(
        IReaderHost host,
        bool moveToEnd,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var horizontal = _readerLayout.FlowMode == 1 || _readerLayout.VerticalWriting;
        if (!moveToEnd)
            await host.InvokeScriptAsync(ReaderNavigationScripts.NormalizeChapterStart);
        await host.InvokeScriptAsync(
            ReaderPaginationScripts.CreateChapterBoundaryScript(moveToEnd, horizontal));
        if (_readerLayout.FlowMode == 1)
            await host.InvokeScriptAsync(ReaderPaginationScripts.Snap);
        await UpdateReaderScrollStateAsync(host);
    }

    private string GetReaderChapterLabel() => _readerIsPdf
        ? $"{_readerPdfPage} / {Math.Max(1, _readerPdfPages.Count)} · 第 {_readerPdfPage} 页"
        : _readerDocument is null
            ? string.Empty
            : GetCurrentReaderTocIndex() is var tocIndex && tocIndex >= 0
                ? $"{tocIndex + 1} / {_readerTocItems.Count} · {_readerTocItems[tocIndex].Title}"
                : $"{_readerChapterIndex + 1} / {_readerDocument.Chapters.Count} · {GetReaderChapterDisplayName(_readerChapterIndex)}";

    private async Task MoveReaderFooterTocAsync(int direction)
    {
        direction = Math.Sign(direction);
        if (direction == 0) return;
        if (_readerIsPdf)
        {
            await NavigatePdfPageAsync(_readerPdfPage + direction, ReaderToken);
            return;
        }

        var currentIndex = GetCurrentReaderTocIndex();
        var targetIndex = currentIndex + direction;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= _readerTocItems.Count)
        {
            ReaderStatusText.Text = direction < 0 ? "已经是目录第一项。" : "已经是目录最后一项。";
            return;
        }

        await NavigateToReaderItemAsync(
            _readerTocItems[targetIndex],
            ReaderToken,
            ReaderNavigationIntent.Toc,
            transitionDirection: direction);
    }

    private string GetReaderChapterDisplayName(int chapterIndex)
    {
        var currentFragment = chapterIndex == _readerChapterIndex
            ? DecodeReaderFragment(_readerCurrentFragment)
            : null;
        var item = string.IsNullOrWhiteSpace(currentFragment)
            ? null
            : _readerTocItems.FirstOrDefault(candidate =>
                candidate.ChapterIndex == chapterIndex
                && Uri.TryCreate(candidate.Target, UriKind.Absolute, out var target)
                && string.Equals(
                    GetReaderTargetFragment(target),
                    currentFragment,
                    StringComparison.Ordinal));
        item ??= _readerTocItems.FirstOrDefault(candidate => candidate.ChapterIndex == chapterIndex)
            ?? _readerTocItems
                .Where(candidate => candidate.ChapterIndex <= chapterIndex)
                .OrderByDescending(candidate => candidate.ChapterIndex)
                .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(item?.Title)) return item.Title.Trim();
        if (_readerDocument is not null
            && chapterIndex >= 0
            && chapterIndex < _readerDocument.ChapterTitles.Count
            && !string.IsNullOrWhiteSpace(_readerDocument.ChapterTitles[chapterIndex]))
        {
            return _readerDocument.ChapterTitles[chapterIndex].Trim();
        }
        if (_readerDocument is not null
            && chapterIndex >= 0
            && chapterIndex < _readerDocument.Chapters.Count)
        {
            var fileName = Path.GetFileNameWithoutExtension(_readerDocument.Chapters[chapterIndex]);
            if (!string.IsNullOrWhiteSpace(fileName)) return fileName;
        }
        return $"第 {chapterIndex + 1} 章";
    }

    private void SetReaderHostLayer(bool revealActiveHost = true)
    {
        var activeSlot = _readerShowingPreload ? ReaderPreloadHostSlot : ReaderActiveHostSlot;
        var hiddenSlot = _readerShowingPreload ? ReaderActiveHostSlot : ReaderPreloadHostSlot;
        // Native webviews are HWND-backed. Opacity and ZIndex alone only affect
        // the Avalonia wrapper; the hidden child window can still cover the
        // visible chapter and consume input. Toggle actual visibility as well.
        activeSlot.IsVisible = revealActiveHost;
        hiddenSlot.IsVisible = false;
        activeSlot.Opacity = revealActiveHost ? 1 : 0;
        activeSlot.IsHitTestVisible = revealActiveHost;
        hiddenSlot.Opacity = 0;
        hiddenSlot.IsHitTestVisible = false;
        activeSlot.ZIndex = 1;
        hiddenSlot.ZIndex = 0;
    }

    private void FocusCurrentReaderHost()
    {
        if (CurrentReaderHost?.View is not Control readerControl) return;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (ReaderRoot.IsVisible && ReferenceEquals(CurrentReaderHost?.View, readerControl))
                    readerControl.Focus();
            },
            DispatcherPriority.Input);
    }

    private void ReaderHost_NavigationStarting(
        object? sender,
        ReaderNavigationStartingEventArgs e)
    {
        if (e.Request is null) return;
        if (string.Equals(e.Request.Scheme, "about", StringComparison.OrdinalIgnoreCase)) return;
        if (!e.Request.IsFile)
        {
            e.Cancel = true;
            return;
        }

        try
        {
            var target = Path.GetFullPath(e.Request.LocalPath);
            var allowed = _readerIsPdf && !string.IsNullOrWhiteSpace(_readerPdfSourcePath)
                ? target.Equals(Path.GetFullPath(_readerPdfSourcePath), StringComparison.OrdinalIgnoreCase)
                : _readerDocument is not null && IsPathInside(_readerDocument.RootPath, target);
            e.Cancel = !allowed;
        }
        catch (Exception) when (e.Request.IsFile)
        {
            // Malformed file URIs fail closed instead of escaping the active
            // EPUB cache or the single PDF file selected for this session.
            e.Cancel = true;
        }
    }

    private void ReaderHost_NavigationCompleted(
        object? sender,
        ReaderNavigationCompletedEventArgs e)
    {
        if (sender is not IReaderHost host || !ReferenceEquals(host, CurrentReaderHost)) return;
        if (!e.IsSuccess)
        {
            ReaderStatusText.Text = "当前章节加载失败。";
            return;
        }
        ReaderStatusText.Text = _readerIsPdf
            ? $"PDF · {_readerPdfPages.Count} 页"
            : $"共 {_readerTocItems.Count} 个章节";
    }

    private void ReaderHost_WebMessageReceived(
        object? sender,
        ReaderWebMessageReceivedEventArgs e)
    {
        if (sender is not IReaderHost host || !ReferenceEquals(host, CurrentReaderHost)) return;
        HandleReaderBridgeMessage(e.Body);
    }

    private async Task CloseReaderAsync()
    {
        if (Interlocked.Exchange(ref _readerCloseInProgress, 1) != 0) return;
        try
        {
        await SaveReaderProgressAsync(CancellationToken.None);
        await SaveReaderLayoutAsync(CancellationToken.None);
        StopReaderStatsTimer();
        // Reading time is accounted by the active-seconds flush (the stats
        // timer), matching the WinUI reference: time only accrues while the
        // window is active and the reader is visible.
        await FlushReaderActiveSecondsAsync();
        ExitReaderZenMode();
        Interlocked.Exchange(ref _readerPendingKeyboardNavigation, 0);
        _readerNavigationCancellation?.Cancel();
        _readerNavigationCancellation?.Dispose();
        _readerNavigationCancellation = null;
        _readerSessionCancellation?.Cancel();
        _readerSessionCancellation?.Dispose();
        _readerSessionCancellation = null;
        _readerLayoutApplyCancellation?.Cancel();
        _readerLayoutApplyCancellation?.Dispose();
        _readerLayoutApplyCancellation = null;
        _readerAiCancellation?.Cancel();
        _readerAiCancellation?.Dispose();
        _readerAiCancellation = null;
        _readerActiveHost?.Stop();
        _readerPreloadHost?.Stop();
        ReaderRoot.IsVisible = false;
        LibraryRoot.IsVisible = true;
        WindowBrandText.IsVisible = false;
        ReaderLayoutSettingsOverlay.IsVisible = false;
        ReaderInPageSearchBar.IsVisible = false;
        _readerFootnoteHoverSequence++;
        _readerFootnotePinned = false;
        ReaderFootnotePopup.IsVisible = false;
        ReaderHighlightButton.IsVisible = false;
        ReaderAnnotateButton.IsVisible = false;
        _readerBookmarkIndicatorSequence++;
        ReaderBookmarkCornerMarker.IsVisible = false;
        ReaderTocCompactPanel.IsVisible = false;
        ReaderTocCompactHoverLabel.IsVisible = false;
        ClearReaderCompactNavigationItems();
        ReaderAssistantPanel.IsVisible = false;
        ReaderRoot.ColumnDefinitions[2].Width = new GridLength(0);
        ReaderContentPanel.RowDefinitions[0].Height = new GridLength(52);
        ReaderHeaderBar.IsVisible = true;
        ReaderContentPanel.RowDefinitions[2].Height = new GridLength(50);
        ReaderFooterBar.IsVisible = true;
        ReaderTransitionCover.Opacity = 0;
        _readerAssistantVisibleBeforeZen = false;
        _readerIsPdf = false;
        _readerPdfPages = [];
        _readerPdfSourcePath = null;
        ReaderBookmarks.Clear();
        ReaderAnnotations.Clear();
        ReaderSearchResults.Clear();
        _readerPendingChunkOffset = null;
        _readerPendingSearchQuery = null;
        _readerPendingSearchContext = null;
        _readerPendingBookmarkQuote = null;
        _readerPendingBookmarkPosition = null;
        _readerPendingBookmarkFlowMode = 0;
        _readerPendingAnnotation = null;
        _readerCurrentFragment = null;
        _readerSearchSequence++;
        _readerWholeSearchSequence++;
        ReaderAiMessages.Clear();
        ReaderAiSources.Clear();
        _readerPendingSelection = null;
        _readerPendingSelectionStartOffset = 0;
        _readerPendingSelectionEndOffset = 0;
        _readerPendingSelectionPrefix = string.Empty;
        _readerPendingSelectionSuffix = string.Empty;
        _readerDocument = null;
        _readerBookCard = null;
        _readerBookFile = null;
        await Task.CompletedTask;
        }
        finally
        {
            Interlocked.Exchange(ref _readerCloseInProgress, 0);
        }
    }

    private async Task SaveReaderProgressAsync(CancellationToken cancellationToken)
    {
        if (_readerBookCard is null || _readerBookFile is null) return;

        if (_readerIsPdf)
        {
            if (_readerPdfPages.Count == 0) return;
            var pdfProgress = new ReaderProgressRow(
                _readerBookCard.Book.Id,
                _readerBookFile.Id,
                $"pdf:page:{_readerPdfPage}",
                null,
                _readerPdfPage - 1,
                (int)Math.Round(_readerScrollPosition),
                CalculateReaderProgressPercent(),
                0,
                DateTimeOffset.UtcNow);
            try { await _readerData.SaveProgressAsync(pdfProgress, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch { }
            return;
        }

        if (_readerDocument is null
            || _readerChapterIndex < 0
            || _readerChapterIndex >= _readerDocument.Chapters.Count) return;

        var chapterPath = Path.GetRelativePath(
                _readerDocument.RootPath,
                _readerDocument.Chapters[_readerChapterIndex])
            .Replace('\\', '/');
        var progress = new ReaderProgressRow(
            _readerBookCard.Book.Id,
            _readerBookFile.Id,
            chapterPath,
            _readerCurrentFragment,
            _readerChapterIndex,
            (int)Math.Round(_readerScrollPosition),
            CalculateReaderProgressPercent(),
            _readerLayout.FlowMode,
            DateTimeOffset.UtcNow);

        try
        {
            await _readerData.SaveProgressAsync(progress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Progress is best-effort and must not make a readable chapter fail.
        }
    }

    private static bool UriEquals(Uri? left, Uri right) =>
        left is not null
        && string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.OrdinalIgnoreCase);

    private static bool IsPathInside(string root, string path)
    {
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(boundary, StringComparison.OrdinalIgnoreCase);
    }

    private async void CloseReaderButton_Click(object? sender, RoutedEventArgs e)
        => await CloseReaderAsync();

    private async void ReaderPreviousButton_Click(object? sender, RoutedEventArgs e)
        => await MoveReaderFooterTocAsync(-1);

    private async void ReaderNextButton_Click(object? sender, RoutedEventArgs e)
        => await MoveReaderFooterTocAsync(1);
}
