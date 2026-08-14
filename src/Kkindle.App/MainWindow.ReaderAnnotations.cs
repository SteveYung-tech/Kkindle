using System.Text;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class MainWindow
{
    private async Task SaveReaderAnnotationAsync(string note)
    {
        if (_readerBookCard is null || _readerBookFile is null) return;
        var selectedText = (_readerPendingSelection ?? string.Empty).Trim();
        if (selectedText.Length == 0 && _selectedReaderAnnotation is null)
        {
            ReaderStatusText.Text = "请先在正文中选择一段文字。";
            return;
        }

        var chapterPath = _readerIsPdf
            ? $"pdf:page:{_readerPdfPage}"
            : GetReaderChapterPath();
        if (string.IsNullOrWhiteSpace(chapterPath)) return;

        var annotation = _selectedReaderAnnotation ?? new ReaderAnnotation
        {
            BookId = _readerBookCard.Book.Id,
            BookFileId = _readerBookFile.Id,
            ChapterPath = chapterPath,
            SelectedText = selectedText,
            CreatedAt = DateTimeOffset.UtcNow
        };
        annotation.ChapterPath = chapterPath;
        annotation.Fragment = _readerIsPdf
            ? null
            : CurrentReaderHost?.Source?.Fragment.TrimStart('#');
        if (selectedText.Length > 0) annotation.SelectedText = selectedText;
        annotation.Note = note.Trim();
        annotation.Color = NormalizeReaderAnnotationColor(
            GetReaderComboTag(ReaderAnnotationColorBox, "#000000"));
        annotation.UnderlineStyle = GetReaderComboTag(ReaderAnnotationStyleBox, "solid");
        annotation.StartOffset = _readerPendingSelectionStartOffset;
        annotation.EndOffset = _readerPendingSelectionEndOffset > _readerPendingSelectionStartOffset
            ? _readerPendingSelectionEndOffset
            : annotation.StartOffset + annotation.SelectedText.Length;
        annotation.Prefix = _readerPendingSelectionPrefix;
        annotation.Suffix = _readerPendingSelectionSuffix;
        annotation.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            await _readerData.SaveAnnotationAsync(annotation, ReaderToken);
            await RefreshReaderAnnotationsAsync(ReaderToken);
            _selectedReaderAnnotation = ReaderAnnotations.FirstOrDefault(item => item.Id == annotation.Id);
            if (!_readerIsPdf && CurrentReaderHost is { } host)
                await ApplySavedAnnotationsAsync(host, ReaderToken);
            else if (_readerIsPdf)
                await ApplySavedReaderPdfAnnotationsAsync(ReaderToken);
            ReaderSelectionBar.IsVisible = false;
            ReaderHighlightButton.IsVisible = false;
            ReaderAnnotateButton.IsVisible = false;
            _readerPendingSelection = null;
            _readerPendingSelectionStartOffset = 0;
            _readerPendingSelectionEndOffset = 0;
            _readerPendingSelectionPrefix = string.Empty;
            _readerPendingSelectionSuffix = string.Empty;
            ReaderAnnotationSelectionText.Text = "请先在正文中选择一段文字，再点击顶部“批注”。";
            ReaderStatusText.Text = string.IsNullOrWhiteSpace(annotation.Note) ? "划线已保存" : "划线与笔记已保存";
        }
        catch (Exception exception)
        {
            ReaderStatusText.Text = $"保存批注失败：{exception.Message}";
        }
    }

    private async void ReaderAnnotationSaveButton_Click(object? sender, RoutedEventArgs e)
        => await SaveReaderAnnotationAsync(ReaderAnnotationNoteBox.Text ?? string.Empty);

    private async void ReaderAnnotationItemButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ReaderAnnotation annotation }) return;
        _selectedReaderAnnotation = annotation;
        ReaderAnnotationSelectionText.Text = annotation.SelectedText;
        ReaderAnnotationNoteBox.Text = annotation.Note;
        SelectReaderComboTag(ReaderAnnotationColorBox, annotation.Color);
        SelectReaderComboTag(ReaderAnnotationStyleBox, annotation.UnderlineStyle);
        ReaderDeleteAnnotationButton.IsEnabled = true;
        _readerPendingSelection = annotation.SelectedText;
        _readerPendingSelectionStartOffset = annotation.StartOffset;
        _readerPendingSelectionEndOffset = annotation.EndOffset;
        _readerPendingSelectionPrefix = annotation.Prefix;
        _readerPendingSelectionSuffix = annotation.Suffix;
        await NavigateToReaderAnnotationAsync(annotation);
    }

    private async Task NavigateToReaderAnnotationAsync(ReaderAnnotation annotation)
    {
        if (_readerIsPdf)
        {
            var prefix = "pdf:page:";
            var pageText = annotation.ChapterPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? annotation.ChapterPath[prefix.Length..]
                : "1";
            if (int.TryParse(pageText, out var page)) await NavigatePdfPageAsync(page, ReaderToken);
            return;
        }
        if (_readerDocument is null) return;
        var chapterIndex = _readerDocument.Chapters
            .Select((path, index) => (path, index))
            .Where(item => string.Equals(
                Path.GetRelativePath(_readerDocument.RootPath, item.path).Replace('\\', '/'),
                annotation.ChapterPath,
                StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();
        if (chapterIndex < 0 || chapterIndex >= _readerDocument.Chapters.Count) return;
        await NavigateToReaderItemAsync(
            new EpubReaderNavigationItem(
                $"第 {chapterIndex + 1} 章",
                new Uri(_readerDocument.Chapters[chapterIndex]).AbsoluteUri,
                chapterIndex),
            ReaderToken);
    }

    private async void ReaderDeleteAnnotationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedReaderAnnotation is null) return;
        await _readerData.DeleteAnnotationAsync(_selectedReaderAnnotation.Id, ReaderToken);
        _selectedReaderAnnotation = null;
        await RefreshReaderAnnotationsAsync(ReaderToken);
        ReaderAnnotationSelectionText.Text = "请先在正文中选择一段文字，再点击顶部“批注”。";
        ReaderAnnotationNoteBox.Text = string.Empty;
        ReaderDeleteAnnotationButton.IsEnabled = false;
        ReaderStatusText.Text = "批注已删除";
    }

    private async void ReaderSelectionCopyButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_readerPendingSelection)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(_readerPendingSelection);
        ReaderStatusText.Text = "已复制选中文字";
    }

    private async void ReaderSelectionHighlightButton_Click(object? sender, RoutedEventArgs e)
        => await SaveReaderAnnotationAsync(string.Empty);

    private void ReaderSelectionAnnotateButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowReaderNotesTab();
        ReaderAnnotationNoteBox.Focus();
    }

    private async void ReaderSelectionAiExplainButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_readerPendingSelection)) return;
        ShowReaderAiTab();
        await SendReaderAiQuestionAsync($"请解释下面这段文字的含义、上下文和隐含前提，并给出一个简单例子：\n\n{_readerPendingSelection}");
    }

    private void ReaderSelectionSearchButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_readerPendingSelection)) return;
        ReaderTocPanel.IsVisible = true;
        ShowReaderSearchTab();
        ReaderTocSearchBox.Text = _readerPendingSelection;
        ReaderTocSearchBox.Focus();
    }

    private async void ReaderSelectionDictionaryButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_readerPendingSelection)) return;
        var term = _readerPendingSelection.Trim();
        var entries = await _dictionaryService.LookupAsync(term, ReaderToken);
        ReaderStatusText.Text = entries.Count == 0
            ? $"词典中没有找到“{term}”。"
            : $"{entries[0].DictionaryName}：{entries[0].Term} — {entries[0].Definition}";
    }

    private void ReaderFootnoteCloseButton_Click(object? sender, RoutedEventArgs e)
        => ReaderFootnotePopup.IsVisible = false;

    private async void ReaderExportMarkdownButton_Click(object? sender, RoutedEventArgs e)
        => await ExportReaderAnnotationsAsync(markdown: true);

    private async void ReaderExportTextButton_Click(object? sender, RoutedEventArgs e)
        => await ExportReaderAnnotationsAsync(markdown: false);

    private async Task ExportReaderAnnotationsAsync(bool markdown)
    {
        if (_readerBookCard is null) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var extension = markdown ? "md" : "txt";
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = markdown ? "导出 Kreader 批注 Markdown" : "导出 Kreader 批注文本",
            SuggestedFileName = $"{_readerBookCard.Title}-批注.{extension}",
            FileTypeChoices = [new FilePickerFileType(markdown ? "Markdown" : "文本") { Patterns = [$"*.{extension}"] }]
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        var resolver = new Func<string, string>(chapterPath =>
            _readerDocument?.Navigation
                .FirstOrDefault(item => string.Equals(
                    Path.GetRelativePath(_readerDocument.RootPath, new Uri(item.Target).LocalPath).Replace('\\', '/'),
                    chapterPath,
                    StringComparison.OrdinalIgnoreCase))?.Title
            ?? chapterPath);
        var content = markdown
            ? ReaderAnnotationExport.BuildMarkdown(_readerBookCard.Title, _readerBookCard.Authors, ReaderAnnotations, resolver)
            : ReaderAnnotationExport.BuildPlainText(_readerBookCard.Title, _readerBookCard.Authors, ReaderAnnotations, resolver);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(true), ReaderToken);
        ReaderExportStatusText.Text = $"已导出 {ReaderAnnotations.Count} 条批注。";
    }

    private static string GetReaderComboTag(ComboBox comboBox, string fallback)
        => (comboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    private static void SelectReaderComboTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
    }
}
