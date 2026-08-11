using System.Text;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Kkindle;

public sealed partial class MainWindow
{
    private readonly List<ReadingMaterialItemViewModel> _allReadingMaterials = [];
    private ReadingMaterialItemViewModel? _selectedReadingMaterial;
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
        _selectedReadingMaterial = null;
        ReadingMaterialsList.SelectedItem = null;
        LocateReadingMaterialButton.IsEnabled = false;
        DeleteReadingMaterialButton.IsEnabled = false;
        ReadingMaterialsStatusText.Text = "正在汇总阅读资料…";
        try
        {
            var books = await _library.SearchAsync(cancellationToken: cancellationToken);
            var bookMap = books.ToDictionary(book => book.Id);
            foreach (var annotation in await _readerData.GetAllAnnotationsAsync(cancellationToken))
            {
                var title = bookMap.TryGetValue(annotation.BookId, out var book) ? book.Title : "已删除的本地书籍";
                _allReadingMaterials.Add(new ReadingMaterialItemViewModel
                {
                    Source = ReadingMaterialSource.Local,
                    BookTitle = title,
                    TypeLabel = string.IsNullOrWhiteSpace(annotation.Note) ? "划线" : "划线与笔记",
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
                        Location = clipping.Metadata.TrimStart('-', ' '),
                        Quote = clipping.Type == KindleClippingType.Note ? string.Empty : clipping.Content,
                        Note = clipping.Type == KindleClippingType.Note ? clipping.Content : string.Empty,
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

    private void ApplyReadingMaterialsFilter()
    {
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
                    bookGroup)))
            .OrderByDescending(group => group.Max(item => item.UpdatedAt ?? DateTimeOffset.MinValue))
            .ThenBy(group => group.BookTitle, StringComparer.CurrentCultureIgnoreCase))
        {
            ReadingMaterialGroups.Add(group);
        }
        var localCount = _allReadingMaterials.Count(item => item.Source == ReadingMaterialSource.Local);
        var kindleCount = _allReadingMaterials.Count(item => item.Source == ReadingMaterialSource.Kindle);
        ReadingMaterialsSummaryText.Text = _readingMaterialsExportMode
            ? $"导出预览 · 本地 {localCount} 条 · Kindle {kindleCount} 条 · 当前将导出 {filtered.Length} 条"
            : $"本地 {localCount} 条 · Kindle {kindleCount} 条 · 当前显示 {filtered.Length} 条";
        ReadingMaterialsExportScopeText.Text = $"当前筛选范围：{GetReadingMaterialsSourceLabel(source)} · 共 {filtered.Length} 条记录";
        ReadingMaterialsEmptyText.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ReadingMaterialsEmptyText.Text = _readingMaterialsExportMode
            ? "当前筛选范围没有可导出的阅读资料"
            : "没有符合条件的划线、笔记与批注";
    }

    private void ReadingMaterialsFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ReadingMaterialsList is not null) ApplyReadingMaterialsFilter();
    }

    private void ReadingMaterialsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ReadingMaterialsList is not null) ApplyReadingMaterialsFilter();
    }

    private void ReadingMaterialsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedReadingMaterial = ReadingMaterialsList.SelectedItem as ReadingMaterialItemViewModel;
        LocateReadingMaterialButton.IsEnabled = !_readingMaterialsExportMode
            && _selectedReadingMaterial?.Source == ReadingMaterialSource.Local;
        DeleteReadingMaterialButton.IsEnabled = !_readingMaterialsExportMode
            && _selectedReadingMaterial is not null;
    }

    private async void ReadingMaterialsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ReadingMaterialItemViewModel item) return;
        if (ReferenceEquals(_selectedReadingMaterial, item) && item.Source == ReadingMaterialSource.Local)
            await LocateReadingMaterialAsync(item);
        else
            ReadingMaterialsList.SelectedItem = item;
    }

    private async void LocateReadingMaterialButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedReadingMaterial is not null) await LocateReadingMaterialAsync(_selectedReadingMaterial);
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
        if (_selectedReadingMaterial is not { } item) return;
        var kindle = item.Source == ReadingMaterialSource.Kindle;
        var message = kindle
            ? "将删除 Kindle 的 My Clippings.txt 中这条记录。此操作不会删除书籍内部或云端已同步的标注。"
            : "将永久删除这条本地划线与笔记。";
        if (!await ShowDevicePromptAsync("删除阅读记录？", message, "删除", "取消")) return;
        try
        {
            if (item.LocalAnnotation is { } annotation)
            {
                await _readerData.DeleteAnnotationAsync(annotation.Id);
                var loaded = _readerAnnotations.FirstOrDefault(value => value.Id == annotation.Id);
                if (loaded is not null) _readerAnnotations.Remove(loaded);
            }
            else if (item.KindleClipping is { } clipping && _devices.Count > 0)
                await _kindle.DeleteClippingAsync(_devices[0], clipping.Id);
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
