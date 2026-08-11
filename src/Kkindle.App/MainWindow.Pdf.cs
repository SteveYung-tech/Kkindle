using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.UI.Xaml;

namespace Kkindle;

public sealed partial class MainWindow
{
    private IReadOnlyList<PdfPageText> _pdfPages = [];
    private int _pdfCurrentPage = 1;
    private bool IsPdfReader => _readerBookFile?.Format.Equals("pdf", StringComparison.OrdinalIgnoreCase) == true;

    private async Task OpenPdfReaderAsync(string path, CancellationToken cancellationToken)
    {
        ReaderStatusText.Text = "正在建立 PDF 本地索引…";
        _readerAllowedRoot = null;
        _readerAllowedFile = Path.GetFullPath(path);
        _readerNavigation = [];
        ClearReaderCompactNavigationItems();
        ReaderTocSearchBox.Visibility = Visibility.Collapsed;
        ReaderTocList.Visibility = Visibility.Collapsed;
        ReaderTocEmptyText.Visibility = Visibility.Visible;
        ReaderTocEmptyText.Text = "PDF 使用内置查看器；Kkindle 已启用本地搜索、页码进度、书签和页面笔记。";
        try
        {
            _pdfPages = await _pdfTextService.ExtractAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _pdfPages = [];
            ReaderStatusText.Text = $"PDF 文本索引不可用：{exception.Message}";
        }
        var pageCount = Math.Max(1, _pdfPages.Count);
        _pdfCurrentPage = Math.Clamp((_savedReaderProgress?.ChapterIndex ?? 0) + 1, 1, pageCount);
        _readerChapterIndex = _pdfCurrentPage - 1;
        _readerChapters = Enumerable.Range(1, pageCount).Select(page => $"pdf:{page}").ToArray();
        _readerHasToc = true;
        ReaderZoomOutButton.Visibility = Visibility.Collapsed;
        ReaderZoomText.Visibility = Visibility.Collapsed;
        ReaderZoomInButton.Visibility = Visibility.Collapsed;
        ReaderPreviousButton.Visibility = Visibility.Visible;
        ReaderNextButton.Visibility = Visibility.Visible;
        SetReaderFooterNavigationMode(chapterNavigation: false);
        ReaderProgressSlider.Visibility = Visibility.Visible;
        ReaderPdfBottomText.Visibility = Visibility.Visible;
        ReaderFlowButton.Visibility = Visibility.Collapsed;
        ReaderHighlightButton.Visibility = Visibility.Collapsed;
        ReaderAnnotateButton.Visibility = Visibility.Collapsed;
        ReaderBookmarkButton.Visibility = Visibility.Visible;
        ReaderSearchToolbarButton.Visibility = Visibility.Visible;
        ReaderBookmarkTabButton.Visibility = Visibility.Visible;
        if (_pdfPages.Count == 0)
            SetReaderIndexUnavailable("PDF 本地索引不可用。");
        else
        {
            _readerIndexAvailable = true;
            _readerIndexTask = Task.CompletedTask;
            ReaderAiStatusText.Text = $"PDF 本地索引已准备：{_pdfPages.Count} 页";
        }
        ApplyReaderPanelLayout();
        await NavigatePdfPageAsync(_pdfCurrentPage, saveProgress: false);
        StartReaderToolsTimers();
    }

    private async Task NavigatePdfPageAsync(int page, bool saveProgress = true)
    {
        if (!IsPdfReader || string.IsNullOrWhiteSpace(_readerAllowedFile)) return;
        var pageCount = Math.Max(1, _pdfPages.Count);
        _pdfCurrentPage = Math.Clamp(page, 1, pageCount);
        _readerChapterIndex = _pdfCurrentPage - 1;
        var source = new Uri(_readerAllowedFile).AbsoluteUri + $"#page={_pdfCurrentPage}";
        ReaderWebView.Source = new Uri(source);
        ReaderChapterText.Text = $"第 {_pdfCurrentPage} 页";
        ReaderReadingProgressText.Text = $"已读 {_pdfCurrentPage} / {pageCount} 页";
        var percent = _pdfCurrentPage * 100d / pageCount;
        ReaderProgressPercentText.Text = ReaderFormatting.FormatPercent(percent);
        _isUpdatingReaderProgress = true;
        ReaderProgressSlider.Minimum = 1;
        ReaderProgressSlider.Maximum = pageCount;
        ReaderProgressSlider.Value = _pdfCurrentPage;
        _isUpdatingReaderProgress = false;
        ReaderPreviousButton.IsEnabled = _pdfCurrentPage > 1;
        ReaderNextButton.IsEnabled = _pdfCurrentPage < pageCount;
        ReaderStatusText.Text = _pdfPages.Count == 0 ? "PDF 查看模式" : "PDF · 本地文本索引可用";
        if (saveProgress) await SavePdfProgressAsync();
    }

    private async Task SavePdfProgressAsync()
    {
        if (_readerBook is null || _readerBookFile is null) return;
        var pageCount = Math.Max(1, _pdfPages.Count);
        var row = new ReaderProgressRow(
            _readerBook.Id,
            _readerBookFile.Id,
            $"pdf:{_pdfCurrentPage}",
            $"page={_pdfCurrentPage}",
            _pdfCurrentPage - 1,
            0,
            _pdfCurrentPage * 100d / pageCount,
            0,
            DateTimeOffset.UtcNow);
        await _readerData.SaveProgressAsync(row, _readerFeatureCancellation?.Token ?? CancellationToken.None);
        _readerLastProgress = row;
        await UpdateBookReadingStatusAsync(_readerBook, row.ProgressPercent);
    }

    private IReadOnlyList<BookContentChunk> SearchPdf(string query)
    {
        return PdfTextService.Search(_pdfPages, query, 40)
            .Select((result, index) => new BookContentChunk(
                index + 1,
                _readerBook?.Id ?? Guid.Empty,
                _readerBookFile?.Id ?? Guid.Empty,
                _readerBookFile?.Sha256 ?? string.Empty,
                result.PageNumber - 1,
                index,
                $"第 {result.PageNumber} 页",
                $"pdf:{result.PageNumber}",
                result.MatchIndex,
                result.MatchIndex + query.Length,
                result.Excerpt))
            .ToArray();
    }

    private async Task NavigatePdfLocationAsync(string chapterPath)
    {
        if (!chapterPath.StartsWith("pdf:", StringComparison.OrdinalIgnoreCase)) return;
        if (int.TryParse(chapterPath[4..], out var page)) await NavigatePdfPageAsync(page);
    }

    private ReaderSelectionAnchor CreatePdfPageAnchor() => new()
    {
        Text = $"PDF 第 {_pdfCurrentPage} 页",
        StartOffset = 0,
        EndOffset = 1,
        Prefix = string.Empty,
        Suffix = string.Empty
    };
}
