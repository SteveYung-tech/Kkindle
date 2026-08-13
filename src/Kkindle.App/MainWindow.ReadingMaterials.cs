using System.Globalization;
using System.Text;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Kkindle;

public sealed partial class MainWindow
{
    private readonly List<ReadingMaterialItemViewModel> _allReadingMaterials = [];
    private readonly HashSet<ReadingMaterialItemViewModel> _selectedReadingMaterials = [];
    private readonly Dictionary<string, string> _readingMaterialCoverPaths = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _readingMaterialsCancellation;
    private string? _readingMaterialsDeviceId;
    private bool _readingMaterialsExportMode;

    private async Task OpenReadingMaterialsPageAsync(bool exportMode)
    {
        _readingMaterialsExportMode = exportMode;
        SetActiveNavigation(exportMode ? ReaderExportNavigationButton : ReaderNotesNavigationButton);
        LibraryPane.Visibility = Visibility.Collapsed;
        SettingsPane.Visibility = Visibility.Collapsed;
        DevicePage.Visibility = Visibility.Collapsed;
        DeviceResourcePage.Visibility = Visibility.Collapsed;
        ZLibraryPage.Visibility = Visibility.Collapsed;
        ReadingDashboardPage.Visibility = Visibility.Collapsed;
        DetailPane.Visibility = Visibility.Collapsed;
        DetailColumn.Width = new GridLength(0);
        HideSettingsPanel();
        ReadingMaterialsPage.Visibility = Visibility.Visible;
        ReadingMaterialsNotesActions.Visibility = exportMode ? Visibility.Collapsed : Visibility.Visible;
        ReadingMaterialsExportActions.Visibility = exportMode ? Visibility.Visible : Visibility.Collapsed;
        ReadingMaterialsExportPanel.Visibility = exportMode ? Visibility.Visible : Visibility.Collapsed;
        ReadingMaterialsPageTitle.Text = exportMode ? "导出记录" : "笔记与标注";
        ReadingMaterialsStatusText.Text = exportMode
            ? "先筛选要导出的阅读资料，再选择文件格式保存到电脑"
            : "统一浏览本地书籍与 Kindle 的划线、笔记和批注";
        await RefreshReadingMaterialsAsync();
    }

    private Task RefreshReadingMaterialsAsync() =>
        TrackDeviceOperationAsync(RefreshReadingMaterialsCoreAsync);

    private async Task RefreshReadingMaterialsCoreAsync()
    {
        _readingMaterialsCancellation?.Cancel();
        _readingMaterialsCancellation?.Dispose();
        _readingMaterialsCancellation = new CancellationTokenSource();
        var cancellationToken = _readingMaterialsCancellation.Token;
        _allReadingMaterials.Clear();
        _readingMaterialCoverPaths.Clear();
        _selectedReadingMaterials.Clear();
        DeleteReadingMaterialButton.IsEnabled = false;
        ReadingMaterialsStatusText.Text = "正在汇总阅读资料…";
        try
        {
            var books = await _library.SearchAsync(cancellationToken: cancellationToken);
            var bookMap = books.ToDictionary(book => book.Id);
            foreach (var book in books)
            {
                if (string.IsNullOrWhiteSpace(book.CoverPath)) continue;
                var coverPath = Path.GetFullPath(Path.Combine(_paths.Data, book.CoverPath));
                if (File.Exists(coverPath))
                    _readingMaterialCoverPaths[BuildReadingMaterialCoverKey(ReadingMaterialSource.Local, book.Title)] = coverPath;
            }
            foreach (var card in DeviceBooks)
            {
                var coverPath = card.Book.CoverPath;
                if (!string.IsNullOrWhiteSpace(coverPath) && File.Exists(coverPath))
                    _readingMaterialCoverPaths[BuildReadingMaterialCoverKey(ReadingMaterialSource.Kindle, card.Title)] = coverPath;
            }
            foreach (var annotation in await _readerData.GetAllAnnotationsAsync(cancellationToken))
            {
                var title = bookMap.TryGetValue(annotation.BookId, out var book) ? book.Title : "已删除的本地书籍";
                _allReadingMaterials.Add(new ReadingMaterialItemViewModel
                {
                    Source = ReadingMaterialSource.Local,
                    BookTitle = title,
                    TypeLabel = string.IsNullOrWhiteSpace(annotation.Note) ? "划线" : "划线与笔记",
                    ChapterLabel = BuildLocalChapterLabel(annotation),
                    Location = BuildLocalMaterialLocation(annotation),
                    Quote = annotation.SelectedText,
                    Note = annotation.Note,
                    UpdatedAt = annotation.UpdatedAt,
                    LocalAnnotation = annotation
                });
            }

            string kindleStatus;
            if (_devices.Count == 0)
            {
                kindleStatus = "Kindle 未连接";
                _readingMaterialsDeviceId = null;
            }
            else
            {
                var clippings = await _kindle.ReadClippingsAsync(_devices[0], cancellationToken);
                foreach (var clipping in clippings.Where(item => item.Type != KindleClippingType.Bookmark))
                {
                    _allReadingMaterials.Add(new ReadingMaterialItemViewModel
                    {
                        Source = ReadingMaterialSource.Kindle,
                        BookTitle = clipping.BookTitle,
                        TypeLabel = clipping.TypeLabel,
                        ChapterLabel = BuildKindleChapterLabel(clipping.Metadata),
                        Location = clipping.Metadata.TrimStart('-', ' '),
                        Quote = clipping.Type == KindleClippingType.Note ? string.Empty : clipping.Content,
                        Note = clipping.Type == KindleClippingType.Note ? clipping.Content : string.Empty,
                        UpdatedAt = ParseKindleDate(clipping.Metadata),
                        KindleClipping = clipping
                    });
                }
                kindleStatus = $"Kindle {_allReadingMaterials.Count(item => item.Source == ReadingMaterialSource.Kindle)} 条";
                _readingMaterialsDeviceId = _devices[0].Identity;
            }
            ApplyReadingMaterialsFilter();
            ReadingMaterialsStatusText.Text = _readingMaterialsExportMode
                ? $"导出预览已准备 · {kindleStatus}"
                : $"本地资料已读取 · {kindleStatus}";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            ApplyReadingMaterialsFilter();
            ReadingMaterialsStatusText.Text = $"部分资料读取失败：{exception.Message}";
        }
    }

    private static string BuildLocalMaterialLocation(ReaderAnnotation annotation)
    {
        if (annotation.ChapterPath.StartsWith("pdf:", StringComparison.OrdinalIgnoreCase))
            return $"PDF 第 {annotation.ChapterPath[4..]} 页";
        return $"{annotation.ChapterPath} · {annotation.StartOffset}-{annotation.EndOffset}";
    }

    private static string BuildLocalChapterLabel(ReaderAnnotation annotation)
    {
        if (annotation.ChapterPath.StartsWith("pdf:", StringComparison.OrdinalIgnoreCase))
            return $"PDF 第 {annotation.ChapterPath[4..]} 页";
        return string.IsNullOrWhiteSpace(annotation.ChapterPath) ? "未指定章节" : annotation.ChapterPath;
    }

    private static string BuildKindleChapterLabel(string metadata)
    {
        var label = metadata.TrimStart('-', ' ');
        var separator = label.IndexOf('|');
        if (separator >= 0) label = label[..separator].Trim();
        return string.IsNullOrWhiteSpace(label) ? "Kindle 位置未知" : label;
    }

    private static DateTimeOffset? ParseKindleDate(string metadata)
    {
        var separator = metadata.IndexOf('|');
        var dateText = separator >= 0 ? metadata[(separator + 1)..] : metadata;
        dateText = dateText
            .Replace("Added on", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("添加于", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim(' ', '-', '：', ':');
        if (dateText.Length == 0) return null;

        var cultures = new[]
        {
            CultureInfo.CurrentCulture,
            CultureInfo.GetCultureInfo("en-US"),
            CultureInfo.GetCultureInfo("zh-CN")
        };
        foreach (var culture in cultures.Distinct())
        {
            if (DateTimeOffset.TryParse(
                    dateText,
                    culture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                    out var value))
                return value;
        }
        return null;
    }

    private static string BuildReadingMaterialCoverKey(ReadingMaterialSource source, string bookTitle) =>
        $"{source}\u001F{bookTitle}";

    private string? GetReadingMaterialCoverPath(ReadingMaterialSource source, string bookTitle) =>
        _readingMaterialCoverPaths.TryGetValue(BuildReadingMaterialCoverKey(source, bookTitle), out var path)
            ? path
            : null;

    private void ApplyReadingMaterialsFilter()
    {
        _selectedReadingMaterials.Clear();
        var source = (ReadingMaterialsSourceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        var query = ReadingMaterialsSearchBox.Text.Trim();
        var filtered = _allReadingMaterials.Where(item =>
            (source == "all"
                || source == "local" && item.Source == ReadingMaterialSource.Local
                || source == "kindle" && item.Source == ReadingMaterialSource.Kindle)
            && (query.Length == 0 || item.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            .OrderByDescending(item => item.UpdatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.BookTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        ReadingMaterials.Clear();
        foreach (var item in filtered) ReadingMaterials.Add(item);
        ReadingMaterialGroups.Clear();
        foreach (var group in filtered
            .GroupBy(item => item.Source)
            .SelectMany(sourceGroup => sourceGroup
                .GroupBy(item => item.BookTitle, StringComparer.CurrentCultureIgnoreCase)
                .Select(bookGroup => new ReadingMaterialGroupViewModel(
                    sourceGroup.Key,
                    bookGroup.Key,
                    bookGroup,
                    GetReadingMaterialCoverPath(sourceGroup.Key, bookGroup.Key))))
            .OrderByDescending(group => group.Max(item => item.UpdatedAt ?? DateTimeOffset.MinValue))
            .ThenBy(group => group.BookTitle, StringComparer.CurrentCultureIgnoreCase))
        {
            group.IsExpanded = !_appSettings.ReadingMaterialsCollapsedByDefault;
            ReadingMaterialGroups.Add(group);
        }
        ReadingMaterialsExportScopeText.Text = $"当前筛选范围：{GetReadingMaterialsSourceLabel(source)} · 共 {filtered.Length} 条记录";
        ReadingMaterialsEmptyText.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ReadingMaterialsEmptyText.Text = _readingMaterialsExportMode
            ? "当前筛选范围没有可导出的阅读资料"
            : "没有符合条件的划线、笔记与批注";
        UpdateReadingMaterialSelectionState();
    }

    private void ApplyReadingMaterialExpansionPreference()
    {
        var isExpanded = !_appSettings.ReadingMaterialsCollapsedByDefault;
        foreach (var group in ReadingMaterialGroups)
            group.IsExpanded = isExpanded;
    }

    private void ReadingMaterialsFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ReadingMaterialsList is not null) ApplyReadingMaterialsFilter();
    }

    private void ReadingMaterialsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ReadingMaterialsList is not null) ApplyReadingMaterialsFilter();
    }

    private void ReadingMaterialGroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var item in e.RemovedItems.OfType<ReadingMaterialItemViewModel>())
            _selectedReadingMaterials.Remove(item);
        foreach (var item in e.AddedItems.OfType<ReadingMaterialItemViewModel>())
            _selectedReadingMaterials.Add(item);
        UpdateReadingMaterialSelectionState();
    }

    private void ReadingMaterialGroupToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ReadingMaterialGroupViewModel group })
            group.IsExpanded = !group.IsExpanded;
    }

    private void UpdateReadingMaterialSelectionState()
    {
        foreach (var item in _allReadingMaterials)
            item.IsSelected = _selectedReadingMaterials.Contains(item);

        var selected = _selectedReadingMaterials.ToArray();
        DeleteReadingMaterialButton.IsEnabled = !_readingMaterialsExportMode && selected.Length > 0;
    }

    private async void ReadingMaterialEntry_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var item = (sender as FrameworkElement)?.DataContext switch
        {
            ReadingMaterialItemViewModel readingMaterial => readingMaterial,
            ReadingMaterialGroupViewModel group => group.FirstItem,
            _ => null
        };
        if (item is not null) await LocateReadingMaterialAsync(item);
    }

    private async Task LocateReadingMaterialAsync(ReadingMaterialItemViewModel item)
    {
        if (item.LocalAnnotation is not { } annotation) return;
        var book = (await _library.SearchAsync()).FirstOrDefault(value => value.Id == annotation.BookId);
        var file = book?.Files.FirstOrDefault(value => value.Id == annotation.BookFileId);
        if (book is null || file is null)
        {
            ReadingMaterialsStatusText.Text = "原书或对应格式已不存在，无法定位。";
            return;
        }
        await OpenBookAsync(book, file);
        if (ReaderPane.Visibility != Visibility.Visible) return;
        ShowReaderNotesTab();
        var loaded = _readerAnnotations.FirstOrDefault(value => value.Id == annotation.Id);
        if (loaded is not null) await NavigateToReaderAnnotationAsync(loaded);
    }

    private async void DeleteReadingMaterialButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _selectedReadingMaterials.ToArray();
        if (selected.Length == 0) return;
        if (selected.Any(item => item.Source == ReadingMaterialSource.Kindle) && _devices.Count == 0)
        {
            ReadingMaterialsStatusText.Text = "Kindle 未连接，无法删除所选 Kindle 记录。";
            return;
        }
        var kindleCount = selected.Count(item => item.Source == ReadingMaterialSource.Kindle);
        var message = selected.Length == 1
            ? kindleCount == 1
                ? "将删除 Kindle 的 My Clippings.txt 中这条记录。此操作不会删除书籍内部或云端已同步的标注。"
                : "将永久删除这条本地划线与笔记。"
            : $"将删除选中的 {selected.Length} 条阅读记录（本地 {selected.Length - kindleCount} 条，Kindle {kindleCount} 条）。此操作不可撤销。";
        if (!await ShowDevicePromptAsync("删除阅读记录？", message, "删除", "取消")) return;
        try
        {
            foreach (var item in selected)
            {
                if (item.LocalAnnotation is { } annotation)
                {
                    await _readerData.DeleteAnnotationAsync(annotation.Id);
                    var loaded = _readerAnnotations.FirstOrDefault(value => value.Id == annotation.Id);
                    if (loaded is not null) _readerAnnotations.Remove(loaded);
                }
                else if (item.KindleClipping is { } clipping)
                    await _kindle.DeleteClippingAsync(_devices[0], clipping.Id);
            }
            await RefreshReadingMaterialsAsync();
        }
        catch (Exception exception) { ReadingMaterialsStatusText.Text = $"删除失败：{exception.Message}"; }
    }

    private async void RefreshReadingMaterialsButton_Click(object sender, RoutedEventArgs e)
    {
        _ignoredDeviceId = null;
        await RefreshDevicesAsync();
        await RefreshReadingMaterialsAsync();
    }

    private async void ExportReadingMaterialsMarkdownButton_Click(object sender, RoutedEventArgs e) => await ExportReadingMaterialsAsync(true);
    private async void ExportReadingMaterialsTextButton_Click(object sender, RoutedEventArgs e) => await ExportReadingMaterialsAsync(false);

    private async Task ExportReadingMaterialsAsync(bool markdown)
    {
        var records = ReadingMaterials.Select(item => item.ToRecord()).ToArray();
        if (records.Length == 0)
        {
            ReadingMaterialsStatusText.Text = "当前筛选结果没有可导出的记录。";
            return;
        }
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeChoices.Add(markdown ? "Markdown" : "文本", [markdown ? ".md" : ".txt"]);
        picker.SuggestedFileName = $"Kkindle-阅读资料-{DateTime.Now:yyyyMMdd-HHmmss}";
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        var text = markdown
            ? ReadingMaterialsExport.BuildMarkdown(records)
            : ReadingMaterialsExport.BuildPlainText(records);
        await File.WriteAllTextAsync(file.Path, text, new UTF8Encoding(true));
        ReadingMaterialsStatusText.Text = $"已导出 {records.Length} 条记录到 {file.Path}";
    }

    private static string GetReadingMaterialsSourceLabel(string source) => source switch
    {
        "local" => "本地书籍",
        "kindle" => "Kindle",
        _ => "全部来源"
    };
}
