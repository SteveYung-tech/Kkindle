using Avalonia.Controls;
using Avalonia.Interactivity;
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

            var progress = await _readerData.GetProgressAsync(
                file.Id,
                sessionToken);
            if (progress is not null)
            {
                _readerChapterIndex = Math.Clamp(progress.ChapterIndex, 0, document.Chapters.Count - 1);
                _readerRestoredProgress = progress;
                _readerScrollPosition = Math.Max(0, progress.ScrollPosition);
            }

            ReaderBookInfoText.Text = $"{card.Title} · {file.Format.ToUpperInvariant()}";
            ReaderChapterText.Text = GetReaderChapterLabel();
            ReaderStatusText.Text = $"共 {document.Chapters.Count} 个章节 · 正在加载";
            ReaderRoot.IsVisible = true;
            LibraryRoot.IsVisible = false;
            WindowBrandText.IsVisible = true;
            ApplyReaderPanelLayout();

            await EnsureReaderHostsAsync();
            SetReaderHostLayer();

            var target = new Uri(document.Chapters[_readerChapterIndex]);
            var loaded = await NavigateReaderHostAndWaitAsync(
                CurrentReaderHost!,
                target,
                sessionToken);
            if (!loaded)
                throw new InvalidOperationException("阅读器无法加载 EPUB 章节。");

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
            using var cancellation = cancellationToken.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
                completion);
            var loaded = await completion.Task;
            if (loaded)
                await ConfigureReaderHostAsync(host, cancellationToken);
            return loaded;
        }
        finally
        {
            host.NavigationCompleted -= handler;
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

    private async Task MoveReaderChapterAsync(int offset)
    {
        if (_readerIsPdf)
        {
            await NavigatePdfPageAsync(_readerPdfPage + offset, ReaderToken);
            return;
        }
        if (_readerDocument is null || CurrentReaderHost is null) return;
        var targetIndex = _readerChapterIndex + offset;
        if (targetIndex < 0 || targetIndex >= _readerDocument.Chapters.Count)
        {
            ReaderStatusText.Text = offset < 0 ? "已经是第一章。" : "已经是最后一章。";
            return;
        }

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

            _readerChapterIndex = targetIndex;
            _readerScrollPosition = 0;
            _readerScrollRatio = 0;
            _readerShowingPreload = !ReferenceEquals(host, CurrentReaderHost);
            SetReaderHostLayer();
            await ApplySavedAnnotationsAsync(host, token);
            ReaderChapterText.Text = GetReaderChapterLabel();
            ReaderStatusText.Text = $"共 {_readerDocument.Chapters.Count} 个章节";
            SetReaderCompactSelectedItem(_readerTocItems.FirstOrDefault(item => item.ChapterIndex == targetIndex));
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

    private string GetReaderChapterLabel() => _readerIsPdf
        ? $"{_readerPdfPage} / {Math.Max(1, _readerPdfPages.Count)} · 第 {_readerPdfPage} 页"
        : _readerDocument is null
            ? string.Empty
            : $"{_readerChapterIndex + 1} / {_readerDocument.Chapters.Count} · {GetReaderChapterDisplayName(_readerChapterIndex)}";

    private string GetReaderChapterDisplayName(int chapterIndex)
    {
        var item = _readerTocItems.FirstOrDefault(candidate => candidate.ChapterIndex == chapterIndex);
        if (!string.IsNullOrWhiteSpace(item?.Title)) return item.Title.Trim();
        if (_readerDocument is not null
            && chapterIndex >= 0
            && chapterIndex < _readerDocument.Chapters.Count)
        {
            var fileName = Path.GetFileNameWithoutExtension(_readerDocument.Chapters[chapterIndex]);
            if (!string.IsNullOrWhiteSpace(fileName)) return fileName;
        }
        return $"第 {chapterIndex + 1} 章";
    }

    private void SetReaderHostLayer()
    {
        var activeSlot = _readerShowingPreload ? ReaderPreloadHostSlot : ReaderActiveHostSlot;
        var hiddenSlot = _readerShowingPreload ? ReaderActiveHostSlot : ReaderPreloadHostSlot;
        activeSlot.Opacity = 1;
        activeSlot.IsHitTestVisible = true;
        hiddenSlot.Opacity = 0;
        hiddenSlot.IsHitTestVisible = false;
        activeSlot.ZIndex = 1;
        hiddenSlot.ZIndex = 0;
    }

    private void ReaderHost_NavigationStarting(
        object? sender,
        ReaderNavigationStartingEventArgs e)
    {
        if (e.Request is null) return;
        if (string.Equals(e.Request.Scheme, "about", StringComparison.OrdinalIgnoreCase)) return;
        if (_readerDocument is null
            || !e.Request.IsFile
            || !IsPathInside(_readerDocument.RootPath, e.Request.LocalPath))
            e.Cancel = true;
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
            : $"共 {_readerDocument?.Chapters.Count ?? 0} 个章节";
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
        await SaveReaderProgressAsync(CancellationToken.None);
        await SaveReaderLayoutAsync(CancellationToken.None);
        StopReaderStatsTimer();
        // Reading time is accounted by the active-seconds flush (the stats
        // timer), matching the WinUI reference: time only accrues while the
        // window is active and the reader is visible.
        await FlushReaderActiveSecondsAsync();
        ExitReaderZenMode();
        _readerNavigationCancellation?.Cancel();
        _readerNavigationCancellation?.Dispose();
        _readerNavigationCancellation = null;
        _readerSessionCancellation?.Cancel();
        _readerSessionCancellation?.Dispose();
        _readerSessionCancellation = null;
        _readerAiCancellation?.Cancel();
        _readerAiCancellation?.Dispose();
        _readerAiCancellation = null;
        _readerActiveHost?.Stop();
        _readerPreloadHost?.Stop();
        ReaderRoot.IsVisible = false;
        LibraryRoot.IsVisible = true;
        WindowBrandText.IsVisible = false;
        ReaderLayoutSettingsOverlay.IsVisible = false;
        ReaderSelectionBar.IsVisible = false;
        ReaderInPageSearchBar.IsVisible = false;
        ReaderFootnotePopup.IsVisible = false;
        ReaderHighlightButton.IsVisible = false;
        ReaderAnnotateButton.IsVisible = false;
        ReaderBookmarkCornerMarker.IsVisible = false;
        ReaderTocCompactPanel.IsVisible = false;
        ReaderTocCompactHoverLabel.IsVisible = false;
        ClearReaderCompactNavigationItems();
        ReaderAssistantPanel.IsVisible = true;
        ReaderRoot.ColumnDefinitions[2].Width = new GridLength(360);
        ReaderContentPanel.RowDefinitions[0].Height = new GridLength(52);
        ReaderHeaderBar.IsVisible = true;
        ReaderContentPanel.RowDefinitions[2].Height = new GridLength(50);
        ReaderFooterBar.IsVisible = true;
        ReaderTransitionCover.Opacity = 0;
        _readerAssistantVisibleBeforeZen = true;
        _readerIsPdf = false;
        _readerPdfPages = [];
        _readerPdfSourcePath = null;
        ReaderBookmarks.Clear();
        ReaderAnnotations.Clear();
        ReaderSearchResults.Clear();
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
            null,
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
        => await MoveReaderChapterAsync(-1);

    private async void ReaderNextButton_Click(object? sender, RoutedEventArgs e)
        => await MoveReaderChapterAsync(1);
}
