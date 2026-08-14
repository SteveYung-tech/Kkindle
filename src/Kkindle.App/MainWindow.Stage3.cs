using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

/// <summary>
/// Stage 3 is intentionally kept in a separate partial file while the
/// Avalonia port is in progress. It owns page switching and presentation
/// state; device, backup, settings and network policy remain in Core and
/// Infrastructure.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _stage3Timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private IReadOnlyList<KindleDevice> _devices = [];
    private string? _lastDeviceIdentity;
    private bool _stage3Ready;
    private bool _deviceResourceBusy;
    private KindleResourceKind _deviceResourceKind = KindleResourceKind.Font;
    private bool _backupBusy;
    private bool _zLibrarySearching;
    private int _zLibraryPage = 1;
    private int _zLibraryPageCount;
    private bool _readingMaterialsExportMode;
    private bool _suppressMainAiProviderChange;

    public ObservableCollection<KindleBookCardViewModel> DeviceBooks { get; } = [];
    public ObservableCollection<KindleDeviceResource> DeviceResources { get; } = [];
    public ObservableCollection<Stage3ReadingMaterialViewModel> ReadingMaterials { get; } = [];
    public ObservableCollection<Stage3DashboardDayViewModel> DashboardDays { get; } = [];
    public ObservableCollection<ZLibraryBookCardViewModel> ZLibraryBooks { get; } = [];
    public ObservableCollection<ManagedFont> ManagedFonts { get; } = [];
    public ObservableCollection<DictionaryDefinition> ManagedDictionaries { get; } = [];

    private readonly List<Stage3ReadingMaterialViewModel> _allStage3ReadingMaterials = [];

    private KindleDevice? CurrentDevice => _devices.FirstOrDefault();

    private void ConfigureStage3Timer()
    {
        _stage3Timer.Tick += async (_, _) =>
        {
            if (!_stage3Ready || _kindle is null || !DevicePage.IsVisible) return;
            await RefreshDevicesAsync(scanBooks: DevicePage.IsVisible);
        };
        _stage3Timer.Start();
    }

    private async Task InitializeStage3Async(CancellationToken cancellationToken)
    {
        if (_stage3Ready) return;

        await _readerData.InitializeAsync(cancellationToken);
        _appSettings = await _appSettingsStore.LoadAsync(cancellationToken);
        _zLibrarySettings = await _zLibrarySettingsStore.LoadAsync(cancellationToken);
        _kindleEmailSettings = await _kindleEmailSettingsStore.LoadAsync(cancellationToken);
        PopulateSettingsControls();
        await RefreshManagedResourcesAsync(cancellationToken);
        DevicePageDeviceText.Text = "打开 Kindle 书库后检测设备";
        DevicePageStatusText.Text = "设备枚举按需执行，避免启动时唤醒 WPD/MTP。";
        SidebarDeviceStatusText.Text = "Kindle：未检测";
        _stage3Ready = true;
    }

    private void ShowLibraryPage()
    {
        WindowBrandText.IsVisible = false;
        SetSidebarActive(AllBooksButton);
        LibraryWorkspace.IsVisible = true;
        LibraryDetailPane.IsVisible = _selectedCard is not null;
        if (LibraryRoot.ColumnDefinitions.Count >= 3)
            LibraryRoot.ColumnDefinitions[2].Width = _selectedCard is null
                ? new GridLength(0)
                : new GridLength(320);
        DevicePage.IsVisible = false;
        DeviceResourcePage.IsVisible = false;
        ReadingMaterialsPage.IsVisible = false;
        ReadingDashboardPage.IsVisible = false;
        ZLibraryPage.IsVisible = false;
        SettingsPage.IsVisible = false;
    }

    private void ShowStage3Page(Control page, Button? activeButton = null)
    {
        WindowBrandText.IsVisible = false;
        activeButton ??= page switch
        {
            _ when ReferenceEquals(page, DevicePage) => KindleBooksButton,
            _ when ReferenceEquals(page, DeviceResourcePage) =>
                _deviceResourceKind == KindleResourceKind.Font ? FontManagementButton : DictionaryManagementButton,
            _ when ReferenceEquals(page, ReadingMaterialsPage) =>
                _readingMaterialsExportMode ? ReaderExportNavigationButton : ReaderNotesNavigationButton,
            _ when ReferenceEquals(page, ReadingDashboardPage) => ReadingDashboardButton,
            _ when ReferenceEquals(page, ZLibraryPage) => ZLibraryBooksButton,
            _ => SettingsNavigationButton
        };
        SetSidebarActive(activeButton);
        LibraryWorkspace.IsVisible = false;
        LibraryDetailPane.IsVisible = false;
        if (LibraryRoot.ColumnDefinitions.Count >= 3)
            LibraryRoot.ColumnDefinitions[2].Width = new GridLength(0);
        DevicePage.IsVisible = ReferenceEquals(page, DevicePage);
        DeviceResourcePage.IsVisible = ReferenceEquals(page, DeviceResourcePage);
        ReadingMaterialsPage.IsVisible = ReferenceEquals(page, ReadingMaterialsPage);
        ReadingDashboardPage.IsVisible = ReferenceEquals(page, ReadingDashboardPage);
        ZLibraryPage.IsVisible = ReferenceEquals(page, ZLibraryPage);
        SettingsPage.IsVisible = ReferenceEquals(page, SettingsPage);
    }

    private void SetSidebarActive(Button activeButton)
    {
        Button[] buttons =
        [
            AllBooksButton,
            KindleBooksButton,
            ZLibraryBooksButton,
            FontManagementButton,
            DictionaryManagementButton,
            ReaderNotesNavigationButton,
            ReaderExportNavigationButton,
            ReadingDashboardButton,
            SettingsNavigationButton,
            KindleEmailSettingsNavigationButton,
            ZLibraryAccountNavigationButton,
            ReaderAiSettingsNavigationButton
        ];
        foreach (var button in buttons)
            button.Classes.Remove("active");
        activeButton.Classes.Add("active");
    }

    private async Task RefreshDevicesAsync(
        bool scanBooks,
        CancellationToken cancellationToken = default)
    {
        if (_kindle is null)
        {
            _devices = [];
            DevicePageDeviceText.Text = "当前启动头未提供 Kindle 平台服务";
            DevicePageStatusText.Text = "设备功能将在 Windows 平台启动头中启用。";
            SidebarDeviceStatusText.Text = "Kindle：平台服务未连接";
            SetEjectButtonsEnabled(false);
            return;
        }

        try
        {
            var detected = await _kindle.DetectDevicesAsync(cancellationToken);
            var identity = detected.FirstOrDefault()?.Identity;
            var changed = !string.Equals(identity, _lastDeviceIdentity, StringComparison.OrdinalIgnoreCase);
            _devices = detected;
            _lastDeviceIdentity = identity;

            if (CurrentDevice is { } device)
            {
                DevicePageDeviceText.Text = $"{device.Name} · {device.ConnectionLabel} · {device.CapacityLabel}";
                DevicePageStatusText.Text = changed ? "设备已连接，正在准备设备信息…" : "设备已连接。";
                SidebarDeviceStatusText.Text = $"Kindle：{device.Name} · {device.ConnectionLabel}";
                KindleStatusText.Text = device.Name;
                KindleConnectionText.Text = device.ConnectionLabel;
                DeviceStorageText.Text = device.CapacityLabel;
                DeviceStorageUsedBar.Width = device.TotalBytes > 0
                    ? Math.Max(0, DeviceStorageBar.Bounds.Width * (1 - (double)device.FreeBytes / device.TotalBytes))
                    : 0;
                SetEjectButtonsEnabled(true);
                if (scanBooks && (changed || DeviceBooks.Count == 0))
                    await RefreshDeviceBooksAsync(cancellationToken);
            }
            else
            {
                foreach (var book in DeviceBooks) book.Dispose();
                DeviceBooks.Clear();
                DevicePageDeviceText.Text = "未检测到 Kindle";
                DevicePageStatusText.Text = "请连接并解锁 Kindle；支持 USB 磁盘与 MTP。";
                SidebarDeviceStatusText.Text = "Kindle：未检测";
                KindleStatusText.Text = "未检测到 Kindle";
                KindleConnectionText.Text = string.Empty;
                DeviceStorageText.Text = "无存储信息";
                DeviceStorageUsedBar.Width = 0;
                SetEjectButtonsEnabled(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DevicePageStatusText.Text = $"设备检测失败：{exception.Message}";
            SidebarDeviceStatusText.Text = "Kindle：检测失败";
        }
    }

    private async Task RefreshDeviceBooksAsync(CancellationToken cancellationToken = default)
    {
        if (_kindle is null || CurrentDevice is not { } device) return;

        DevicePageStatusText.Text = "正在扫描 Kindle 书籍…";
        DeviceBookEmptyText.Text = "正在读取设备书库…";
        try
        {
            var books = await _kindle.ScanBooksAsync(device, cancellationToken);
            foreach (var old in DeviceBooks) old.Dispose();
            DeviceBooks.Clear();
            foreach (var book in books)
                DeviceBooks.Add(new KindleBookCardViewModel(book));

            var comparison = BookLibraryComparer.Compare(ViewModel.LibraryBooks, books);
            foreach (var card in DeviceBooks)
            {
                var presence = comparison.KindleBooksOnComputer.Contains(card.Book.RelativePath)
                    ? BookLibraryPresence.Both
                    : BookLibraryPresence.KindleOnly;
                card.SetLibraryPresence(presence);
            }

            DeviceBookEmptyText.Text = books.Count == 0 ? "设备中没有可识别的书籍。" : $"已读取 {books.Count} 本书。";
            DevicePageStatusText.Text = $"已读取 {books.Count} 本书 · {device.ConnectionLabel}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DeviceBookEmptyText.Text = $"扫描失败：{exception.Message}";
            DevicePageStatusText.Text = "Kindle 书库扫描失败。";
        }
    }

    private void SetEjectButtonsEnabled(bool enabled)
    {
        DeviceStatusEjectButton.IsEnabled = enabled;
        EjectDeviceButton.IsEnabled = enabled;
    }

    private async Task OpenKindlePageAsync()
    {
        ShowStage3Page(DevicePage);
        await RefreshDevicesAsync(scanBooks: true);
        if (CurrentDevice is not null && DeviceBooks.Count == 0)
            await RefreshDeviceBooksAsync();
    }

    private async void KindleBooksButton_Click(object? sender, RoutedEventArgs e) => await OpenKindlePageAsync();

    private async void RefreshDevicesButton_Click(object? sender, RoutedEventArgs e)
    {
        await RefreshDevicesAsync(scanBooks: DevicePage.IsVisible);
        if (DevicePage.IsVisible && CurrentDevice is not null)
            await RefreshDeviceBooksAsync();
    }

    private async void ScanDeviceBooksButton_Click(object? sender, RoutedEventArgs e) => await RefreshDeviceBooksAsync();

    private async void EjectDeviceButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_kindle is null || CurrentDevice is not { } device) return;
        try
        {
            SetEjectButtonsEnabled(false);
            DevicePageStatusText.Text = "正在安全弹出设备…";
            await _kindle.EjectAsync(device, _lifetimeCancellation.Token);
            foreach (var book in DeviceBooks) book.Dispose();
            DeviceBooks.Clear();
            await RefreshDevicesAsync(scanBooks: false);
        }
        catch (Exception exception)
        {
            DevicePageStatusText.Text = $"弹出失败：{exception.Message}";
            SetEjectButtonsEnabled(true);
        }
    }

    private async void DeviceBook_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: KindleBookCardViewModel card })
            await ExportDeviceBookAsync(card);
    }

    private async void ExportDeviceBookButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: KindleBookCardViewModel card })
            await ExportDeviceBookAsync(card);
    }

    private async void SendSelectedBookToKindleButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedCard is null)
        {
            SetTaskStatus("请先选择一本书。");
            return;
        }
        if (_kindle is null)
        {
            SetTaskStatus("当前启动头未提供 Kindle 平台服务。");
            return;
        }

        var file = ReaderBookSelectionPolicy.SelectPreferred(_selectedCard.Book.Files);
        if (file is null)
        {
            SetTaskStatus("这本书没有可发送的文件。");
            return;
        }
        var sourcePath = ViewModel.GetAbsoluteFilePath(file);
        if (!File.Exists(sourcePath))
        {
            SetTaskStatus($"找不到文件：{file.RelativePath}");
            return;
        }

        await RefreshDevicesAsync(scanBooks: false, _lifetimeCancellation.Token);
        if (CurrentDevice is not { } device)
        {
            SetTaskStatus("请先连接并解锁 Kindle。");
            return;
        }

        try
        {
            SetTaskStatus($"正在发送《{_selectedCard.Title}》到 Kindle…");
            var progress = new Progress<TransferProgress>(value =>
                SetTaskStatus($"正在发送《{_selectedCard!.Title}》：{value.Percentage:0}%"));
            await _kindle.SendBookAsync(device, file, sourcePath, progress, _lifetimeCancellation.Token);
            SetTaskStatus($"已发送《{_selectedCard.Title}》到 {device.Name}。 ");
            if (DevicePage.IsVisible)
                await RefreshDeviceBooksAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetTaskStatus($"发送到 Kindle 失败：{exception.Message}");
        }
    }

    private async void SendSelectedBookByEmailButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedCard is null)
        {
            SetTaskStatus("请先选择一本书。");
            return;
        }
        if (!_kindleEmailSettings.IsConfigured)
        {
            SetTaskStatus("请先在设置与备份中填写并保存 Kindle 邮箱设置。");
            ShowStage3Page(SettingsPage);
            return;
        }

        var file = ReaderBookSelectionPolicy.SelectPreferred(_selectedCard.Book.Files);
        if (file is null)
        {
            SetTaskStatus("这本书没有可发送的文件。");
            return;
        }
        var sourcePath = ViewModel.GetAbsoluteFilePath(file);
        if (!File.Exists(sourcePath))
        {
            SetTaskStatus($"找不到文件：{file.RelativePath}");
            return;
        }

        try
        {
            SetTaskStatus($"正在通过邮件发送《{_selectedCard.Title}》…");
            await _kindleEmailSender.SendAsync(
                _kindleEmailSettings,
                sourcePath,
                $"Kkindle：{_selectedCard.Title}",
                _lifetimeCancellation.Token);
            SetTaskStatus($"已通过邮件发送《{_selectedCard.Title}》。");
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetTaskStatus($"邮件发送失败：{exception.Message}");
        }
    }

    private async Task ExportDeviceBookAsync(KindleBookCardViewModel card)
    {
        if (_kindle is null || CurrentDevice is not { } device) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择导出目录",
            AllowMultiple = false
        });
        var destination = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(destination)) return;

        try
        {
            DevicePageStatusText.Text = $"正在导出《{card.Title}》…";
            var path = await _kindle.ExportBookAsync(device, card.Book, destination, cancellationToken: _lifetimeCancellation.Token);
            DevicePageStatusText.Text = $"已导出到 {path}";
        }
        catch (Exception exception)
        {
            DevicePageStatusText.Text = $"导出失败：{exception.Message}";
        }
    }

    private async void DeleteDeviceBookButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: KindleBookCardViewModel card }
            || _kindle is null
            || CurrentDevice is not { } device) return;
        if (!await ConfirmAsync("从 Kindle 删除书籍", $"确定从 {device.Name} 删除《{card.Title}》吗？电脑书库中的文件不会受影响。")) return;
        try
        {
            await _kindle.RemoveBookAsync(device, card.Book, _lifetimeCancellation.Token);
            DeviceBooks.Remove(card);
            card.Dispose();
            DevicePageStatusText.Text = $"已从 Kindle 删除《{card.Title}》。";
        }
        catch (Exception exception)
        {
            DevicePageStatusText.Text = $"删除失败：{exception.Message}";
        }
    }

    private async void ReaderNotesNavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        _readingMaterialsExportMode = false;
        ShowStage3Page(ReadingMaterialsPage);
        ReadingMaterialsPageTitle.Text = "笔记与标注";
        ReadingMaterialsStatusText.Text = "统一浏览本地书籍与 Kindle 的划线、笔记和批注。";
        ReadingMaterialsNotesActions.IsVisible = true;
        ReadingMaterialsExportActions.IsVisible = false;
        ReadingMaterialsExportPanel.IsVisible = false;
        await RefreshReadingMaterialsAsync();
    }

    private async void ReaderExportNavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        _readingMaterialsExportMode = true;
        ShowStage3Page(ReadingMaterialsPage);
        ReadingMaterialsPageTitle.Text = "导出记录";
        ReadingMaterialsStatusText.Text = "先筛选要导出的阅读资料，再选择文件格式保存到电脑。";
        ReadingMaterialsNotesActions.IsVisible = false;
        ReadingMaterialsExportActions.IsVisible = true;
        ReadingMaterialsExportPanel.IsVisible = true;
        await RefreshReadingMaterialsAsync();
    }

    private async Task RefreshReadingMaterialsAsync()
    {
        _allStage3ReadingMaterials.Clear();
        ReadingMaterials.Clear();
        try
        {
            var books = await _library.SearchAsync(cancellationToken: _lifetimeCancellation.Token);
            var titles = books.ToDictionary(book => book.Id, book => book.Title);
            foreach (var annotation in await _readerData.GetAllAnnotationsAsync(_lifetimeCancellation.Token))
            {
                var chapter = string.IsNullOrWhiteSpace(annotation.ChapterPath) ? "未指定章节" : annotation.ChapterPath;
                _allStage3ReadingMaterials.Add(new Stage3ReadingMaterialViewModel(
                    ReadingMaterialSource.Local,
                    titles.GetValueOrDefault(annotation.BookId, "已删除的本地书籍"),
                    string.IsNullOrWhiteSpace(annotation.Note) ? "划线" : "划线与笔记",
                    chapter,
                    $"{annotation.ChapterPath} · {annotation.StartOffset}-{annotation.EndOffset}",
                    annotation.SelectedText,
                    annotation.Note,
                    annotation.UpdatedAt,
                    annotation,
                    null));
            }

            if (CurrentDevice is { } device && _kindle is not null)
            {
                foreach (var clipping in (await _kindle.ReadClippingsAsync(device, _lifetimeCancellation.Token))
                    .Where(item => item.Type != KindleClippingType.Bookmark))
                {
                    _allStage3ReadingMaterials.Add(new Stage3ReadingMaterialViewModel(
                        ReadingMaterialSource.Kindle,
                        clipping.BookTitle,
                        clipping.TypeLabel,
                        clipping.Metadata,
                        clipping.Metadata,
                        clipping.Type == KindleClippingType.Note ? string.Empty : clipping.Content,
                        clipping.Type == KindleClippingType.Note ? clipping.Content : string.Empty,
                        ParseClippingDate(clipping.Metadata),
                        null,
                        clipping));
                }
            }

            ApplyReadingMaterialsFilter();
            ReadingMaterialsStatusText.Text = $"共 {ReadingMaterials.Count} 条阅读资料。";
        }
        catch (Exception exception)
        {
            ReadingMaterialsStatusText.Text = $"读取阅读资料失败：{exception.Message}";
        }
    }

    private void ApplyReadingMaterialsFilter()
    {
        if (!_stage3Ready
            || ReadingMaterialsSearchBox is null
            || ReadingMaterialsSourceBox is null
            || ReadingMaterialsEmptyText is null
            || ReadingMaterialsStatusText is null)
            return;
        var source = (ReadingMaterialsSourceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        var query = ReadingMaterialsSearchBox.Text?.Trim() ?? string.Empty;
        var filtered = _allStage3ReadingMaterials
            .Where(item => source == "all"
                || source == "local" && item.Source == ReadingMaterialSource.Local
                || source == "kindle" && item.Source == ReadingMaterialSource.Kindle)
            .Where(item => query.Length == 0 || item.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(item => item.UpdatedAt ?? DateTimeOffset.MinValue)
            .ToArray();
        ReadingMaterials.Clear();
        foreach (var item in filtered) ReadingMaterials.Add(item);
        ReadingMaterialsEmptyText.IsVisible = filtered.Length == 0;
        ReadingMaterialsEmptyText.Text = _readingMaterialsExportMode
            ? "当前筛选范围没有可导出的阅读资料。"
            : "没有符合条件的阅读资料。";
        ReadingMaterialsExportScopeText.Text = $"当前筛选范围：{GetReadingMaterialsSourceLabel(source)} · 共 {filtered.Length} 条记录";
        DeleteReadingMaterialsButton.IsEnabled = !_readingMaterialsExportMode
            && ReadingMaterials.Any(item => item.IsSelected);
        ReadingMaterialsStatusText.Text = $"当前筛选范围：{filtered.Length} 条";
    }

    private void ReadingMaterialsSearchBox_TextChanged(object? sender, TextChangedEventArgs e) => ApplyReadingMaterialsFilter();
    private void ReadingMaterialsSourceBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyReadingMaterialsFilter();

    private void ReadingMaterialSelectionChanged(object? sender, RoutedEventArgs e)
    {
        DeleteReadingMaterialsButton.IsEnabled = !_readingMaterialsExportMode
            && ReadingMaterials.Any(item => item.IsSelected);
    }

    private async void RefreshReadingMaterialsButton_Click(object? sender, RoutedEventArgs e)
    {
        await RefreshDevicesAsync(scanBooks: false);
        await RefreshReadingMaterialsAsync();
    }

    private async void DeleteReadingMaterialsButton_Click(object? sender, RoutedEventArgs e)
    {
        var selected = ReadingMaterials.Where(item => item.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            ReadingMaterialsStatusText.Text = "请先勾选要删除的记录。";
            return;
        }
        if (!await ConfirmAsync("删除阅读资料", $"确定删除选中的 {selected.Length} 条记录吗？Kindle 记录只会从 My Clippings.txt 删除。")) return;
        try
        {
            foreach (var item in selected)
            {
                if (item.LocalAnnotation is { } annotation)
                    await _readerData.DeleteAnnotationAsync(annotation.Id, _lifetimeCancellation.Token);
                else if (item.KindleClipping is { } clipping && CurrentDevice is { } device && _kindle is not null)
                    await _kindle.DeleteClippingAsync(device, clipping.Id, _lifetimeCancellation.Token);
            }
            await RefreshReadingMaterialsAsync();
        }
        catch (Exception exception)
        {
            ReadingMaterialsStatusText.Text = $"删除失败：{exception.Message}";
        }
    }

    private async void ExportReadingMaterialsMarkdownButton_Click(object? sender, RoutedEventArgs e)
        => await ExportReadingMaterialsAsync(markdown: true);

    private async void ExportReadingMaterialsTextButton_Click(object? sender, RoutedEventArgs e)
        => await ExportReadingMaterialsAsync(markdown: false);

    private async Task ExportReadingMaterialsAsync(bool markdown)
    {
        var records = ReadingMaterials.Select(item => item.ToRecord()).ToArray();
        if (records.Length == 0)
        {
            ReadingMaterialsStatusText.Text = "当前筛选结果没有可导出的记录。";
            return;
        }
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出阅读资料",
            SuggestedFileName = $"Kkindle-阅读资料-{DateTime.Now:yyyyMMdd-HHmmss}.{(markdown ? "md" : "txt")}",
            FileTypeChoices = [new FilePickerFileType(markdown ? "Markdown" : "文本")
            {
                Patterns = [markdown ? "*.md" : "*.txt"]
            }]
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        var content = markdown
            ? ReadingMaterialsExport.BuildMarkdown(records)
            : ReadingMaterialsExport.BuildPlainText(records);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(true), _lifetimeCancellation.Token);
        ReadingMaterialsStatusText.Text = $"已导出 {records.Length} 条记录到 {path}。";
    }

    private static string GetReadingMaterialsSourceLabel(string source) => source switch
    {
        "local" => "本地书库",
        "kindle" => "Kindle",
        _ => "全部来源"
    };

    private async Task OpenDeviceResourcePageAsync(KindleResourceKind kind)
    {
        _deviceResourceKind = kind;
        ShowStage3Page(DeviceResourcePage);
        DeviceResourcePageTitle.Text = kind == KindleResourceKind.Font ? "Kindle 字体" : "Kindle 字典";
        DeviceResourcePathText.Text = kind == KindleResourceKind.Font ? @"Kindle\fonts" : @"Kindle\documents\dictionaries";
        DeviceResourceSafetyText.Text = kind == KindleResourceKind.Font
            ? "仅读写 fonts 目录；支持 TTF、OTF。"
            : "仅读写 documents\\dictionaries 目录；支持 AZW、AZW3、MOBI、KFX。";
        await RefreshDeviceResourcesAsync();
    }

    private async Task RefreshDeviceResourcesAsync()
    {
        DeviceResources.Clear();
        if (_kindle is null || CurrentDevice is not { } device)
        {
            DeviceResourceStatusText.Text = "请先连接 Kindle。";
            DeviceResourceEmptyText.IsVisible = true;
            return;
        }
        try
        {
            var resources = await _kindle.ScanResourcesAsync(device, _deviceResourceKind, _lifetimeCancellation.Token);
            foreach (var resource in resources) DeviceResources.Add(resource);
            DeviceResourceStatusText.Text = $"{device.Name} · 已读取 {resources.Count} 个文件";
            DeviceResourceEmptyText.IsVisible = resources.Count == 0;
        }
        catch (Exception exception)
        {
            DeviceResourceStatusText.Text = $"读取失败：{exception.Message}";
            DeviceResourceEmptyText.IsVisible = true;
        }
    }

    private async void FontManagementButton_Click(object? sender, RoutedEventArgs e) => await OpenDeviceResourcePageAsync(KindleResourceKind.Font);
    private async void DictionaryManagementButton_Click(object? sender, RoutedEventArgs e) => await OpenDeviceResourcePageAsync(KindleResourceKind.Dictionary);

    private async void RefreshDeviceResourcesButton_Click(object? sender, RoutedEventArgs e)
    {
        await RefreshDevicesAsync(scanBooks: false);
        await RefreshDeviceResourcesAsync();
    }

    private async void ImportDeviceResourceButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_deviceResourceBusy || _kindle is null || CurrentDevice is null) return;
        var extensions = _deviceResourceKind == KindleResourceKind.Font
            ? new[] { "*.ttf", "*.otf" }
            : new[] { "*.azw", "*.azw3", "*.mobi", "*.kfx" };
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入 Kindle 资源",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Kindle 资源") { Patterns = extensions }]
        });
        var paths = files.Select(file => file.TryGetLocalPath()).Where(path => path is not null).Select(path => path!).ToArray();
        if (paths.Length == 0) return;

        _deviceResourceBusy = true;
        try
        {
            foreach (var path in paths)
            {
                DeviceResourceStatusText.Text = $"正在导入 {Path.GetFileName(path)}…";
                await _kindle.SendResourceAsync(CurrentDevice!, _deviceResourceKind, path, cancellationToken: _lifetimeCancellation.Token);
            }
            await RefreshDeviceResourcesAsync();
        }
        catch (Exception exception)
        {
            DeviceResourceStatusText.Text = $"导入失败：{exception.Message}";
        }
        finally { _deviceResourceBusy = false; }
    }

    private async void ExportDeviceResourceButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: KindleDeviceResource resource } || _kindle is null || CurrentDevice is not { } device) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var extension = Path.GetExtension(resource.FileName);
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出 Kindle 资源",
            SuggestedFileName = resource.FileName,
            FileTypeChoices = [new FilePickerFileType(resource.Kind == KindleResourceKind.Font ? "字体" : "字典") { Patterns = [$"*{extension}"] }]
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            await _kindle.ExportResourceAsync(device, resource, path, _lifetimeCancellation.Token);
            DeviceResourceStatusText.Text = $"已导出 {resource.FileName}";
        }
        catch (Exception exception) { DeviceResourceStatusText.Text = $"导出失败：{exception.Message}"; }
    }

    private async void DeleteDeviceResourceButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: KindleDeviceResource resource } || _kindle is null || CurrentDevice is not { } device) return;
        if (!await ConfirmAsync("删除 Kindle 资源", $"确定删除设备文件 {resource.RelativePath} 吗？")) return;
        try
        {
            await _kindle.RemoveResourceAsync(device, resource, _lifetimeCancellation.Token);
            await RefreshDeviceResourcesAsync();
        }
        catch (Exception exception) { DeviceResourceStatusText.Text = $"删除失败：{exception.Message}"; }
    }

    private async void ReadingDashboardButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowStage3Page(ReadingDashboardPage);
        await RefreshReadingDashboardAsync();
    }

    private async Task RefreshReadingDashboardAsync()
    {
        try
        {
            var dashboard = await _readerData.GetReadingDashboardAsync(cancellationToken: _lifetimeCancellation.Token);
            DashboardBooksStartedText.Text = $"{dashboard.BooksStarted} 本";
            DashboardBooksFinishedText.Text = $"{dashboard.BooksFinished} 本";
            DashboardTotalTimeText.Text = FormatReadingTime(dashboard.TotalSeconds);
            DashboardAverageProgressText.Text = $"{dashboard.AverageProgress:0.#}%";
            DashboardBookmarksText.Text = dashboard.BookmarkCount.ToString(CultureInfo.InvariantCulture);
            DashboardAnnotationsText.Text = dashboard.AnnotationCount.ToString(CultureInfo.InvariantCulture);

            var recentLines = dashboard.RecentBooks
                .Select(item =>
                {
                    var title = ViewModel.LibraryBooks.FirstOrDefault(book => book.Id == item.BookId)?.Title
                        ?? "未导入的书籍";
                    return $"{title}  ·  {item.ProgressPercent:0.#}%  ·  {FormatReadingTime(item.CumulativeSeconds)}";
                })
                .ToArray();
            DashboardRecentText.Text = recentLines.Length == 0
                ? "还没有阅读记录。打开一本 EPUB 后，这里会显示最近进度。"
                : string.Join(Environment.NewLine, recentLines);

            DashboardDays.Clear();
            var maximumSeconds = Math.Max(1, dashboard.DailyReading.Max(day => day.ActiveSeconds));
            foreach (var day in dashboard.DailyReading)
            {
                DashboardDays.Add(new Stage3DashboardDayViewModel(
                    day.Date.ToString("MM/dd", CultureInfo.InvariantCulture),
                    day.ActiveSeconds == 0 ? "" : FormatReadingTime(day.ActiveSeconds),
                    day.ActiveSeconds == 0 ? 4 : 10 + 108d * day.ActiveSeconds / maximumSeconds));
            }
            ReadingDashboardStatusText.Text = "统计本地阅读器的进度、时长、书签与标注。";
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReadingDashboardStatusText.Text = $"阅读数据暂时不可用：{exception.Message}";
        }
    }

    private static string FormatReadingTime(long seconds)
    {
        if (seconds < 60) return $"{seconds} 秒";
        if (seconds < 3600) return $"{Math.Max(1, seconds / 60)} 分钟";
        return $"{seconds / 3600d:0.#} 小时";
    }

    private void SettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowStage3Page(SettingsPage, SettingsNavigationButton);
        SettingsDataPathText.Text = _paths.Data;
    }

    private void KindleEmailSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowStage3Page(SettingsPage, KindleEmailSettingsNavigationButton);
        SettingsStatusText.Text = "请在下方保存 Kindle 邮箱设置。";
        KindleEmailRecipientBox.Focus();
    }

    private async void ReaderAiSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowStage3Page(SettingsPage, ReaderAiSettingsNavigationButton);
        await LoadMainReaderAiSettingsAsync();
        MainReaderAiBaseUrlBox.Focus();
    }

    private async Task LoadMainReaderAiSettingsAsync()
    {
        try
        {
            _readerAiSettings = await _aiSettingsStore.LoadAsync(_lifetimeCancellation.Token);
            _suppressMainAiProviderChange = true;
            try
            {
                var provider = _readerAiSettings.Provider.Trim().ToLowerInvariant();
                MainReaderAiProviderBox.SelectedItem = MainReaderAiProviderBox.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), provider, StringComparison.OrdinalIgnoreCase))
                    ?? MainReaderAiProviderBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
                MainReaderAiBaseUrlBox.Text = _readerAiSettings.BaseUrl;
                MainReaderAiModelBox.Text = _readerAiSettings.Model;
                MainReaderAiApiKeyBox.Text = _readerAiSettings.ApiKey;
                MainReaderAiSettingsStatusText.Text = string.Empty;
            }
            finally
            {
                _suppressMainAiProviderChange = false;
            }
        }
        catch (Exception exception)
        {
            MainReaderAiSettingsStatusText.Text = $"读取 AI 设置失败：{exception.Message}";
        }
    }

    private void MainReaderAiProviderBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressMainAiProviderChange
            || MainReaderAiProviderBox is null
            || MainReaderAiBaseUrlBox is null
            || MainReaderAiModelBox is null
            || MainReaderAiSettingsStatusText is null
            || MainReaderAiProviderBox.SelectedItem is not ComboBoxItem { Tag: not null } item)
            return;
        var defaults = AiConnectionSettings.GetDefaults(item.Tag.ToString()!);
        MainReaderAiBaseUrlBox.Text = defaults.BaseUrl;
        MainReaderAiModelBox.Text = defaults.Model;
        MainReaderAiSettingsStatusText.Text = item.Tag.ToString() == "custom"
            ? "自定义服务使用 OpenAI-compatible Chat Completions。"
            : string.Empty;
    }

    private async void MainReaderAiSettingsSaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (MainReaderAiProviderBox.SelectedItem is not ComboBoxItem { Tag: not null } item) return;
        var provider = item.Tag.ToString()!;
        var baseUrl = MainReaderAiBaseUrlBox.Text?.Trim() ?? string.Empty;
        var model = MainReaderAiModelBox.Text?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
        {
            MainReaderAiSettingsStatusText.Text = "请输入有效的 HTTP 或 HTTPS Base URL。";
            return;
        }
        if (model.Length == 0)
        {
            MainReaderAiSettingsStatusText.Text = "请输入模型名称。";
            return;
        }

        var settings = new AiConnectionSettings
        {
            Provider = provider,
            BaseUrl = baseUrl,
            Model = AiConnectionSettings.NormalizeModel(provider, model),
            ApiKey = (MainReaderAiApiKeyBox.Text ?? string.Empty).Trim()
        };
        try
        {
            MainReaderAiSettingsStatusText.Text = "正在安全保存…";
            await _aiSettingsStore.SaveAsync(settings, _lifetimeCancellation.Token);
            _readerAiSettings = settings;
            ApplyReaderAiSettingsToControls();
            MainReaderAiSettingsStatusText.Text = "AI 设置已保存。";
        }
        catch (Exception exception)
        {
            MainReaderAiSettingsStatusText.Text = $"保存失败：{exception.Message}";
        }
    }

    private void PopulateSettingsControls()
    {
        PreferredOpenFormatBox.SelectedIndex = _appSettings.PreferredOpenFormat switch
        {
            "pdf" => 1,
            "azw3" => 2,
            "mobi" => 3,
            _ => 0
        };
        CalibrePathBox.Text = _appSettings.CalibrePath;
        AutoBackupCheck.IsChecked = _appSettings.AutoBackupEnabled;
        NetworkEnabledCheck.IsChecked = _appSettings.NetworkEnabled;
        AutoConnectDeviceCheck.IsChecked = _appSettings.AutoConnectDevice;
        SettingsDataPathText.Text = _paths.Data;
        ZLibraryEmailBox.Text = _zLibrarySettings.Email;
        ZLibraryPasswordBox.Text = _zLibrarySettings.Password;
        ZLibraryBaseUrlBox.Text = _zLibrarySettings.BaseUrl;
        KindleEmailRecipientBox.Text = _kindleEmailSettings.KindleEmailAddress;
        KindleEmailSenderBox.Text = _kindleEmailSettings.SenderEmailAddress;
        KindleEmailSmtpHostBox.Text = _kindleEmailSettings.SmtpHost;
        KindleEmailSmtpPortBox.Text = _kindleEmailSettings.SmtpPort.ToString(CultureInfo.InvariantCulture);
        KindleEmailUsernameBox.Text = _kindleEmailSettings.SmtpUsername;
        KindleEmailPasswordBox.Text = _kindleEmailSettings.SmtpPassword;
        KindleEmailSslCheck.IsChecked = _kindleEmailSettings.EnableSsl;
        UpdateZLibraryAccountStatus();
    }

    private async Task RefreshManagedResourcesAsync(CancellationToken cancellationToken = default)
    {
        ManagedFonts.Clear();
        foreach (var font in await _fontLibrary.ListAsync(cancellationToken)) ManagedFonts.Add(font);
        ManagedDictionaries.Clear();
        foreach (var dictionary in await _dictionaryService.ListAsync(cancellationToken)) ManagedDictionaries.Add(dictionary);
    }

    private async void RefreshLocalResourcesButton_Click(object? sender, RoutedEventArgs e)
    {
        await RefreshManagedResourcesAsync(_lifetimeCancellation.Token);
        SettingsStatusText.Text = "本地字体与字典列表已刷新。";
    }

    private async void ImportFontButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入本地字体",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("字体") { Patterns = ["*.ttf", "*.otf", "*.woff", "*.woff2"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            await _fontLibrary.ImportAsync(path, _lifetimeCancellation.Token);
            await RefreshManagedResourcesAsync(_lifetimeCancellation.Token);
            SettingsStatusText.Text = "字体已导入。";
        }
        catch (Exception exception) { SettingsStatusText.Text = $"字体导入失败：{exception.Message}"; }
    }

    private async void ImportDictionaryButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入本地字典",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("字典文本") { Patterns = ["*.txt", "*.tsv", "*.csv"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            await _dictionaryService.ImportAsync(path, cancellationToken: _lifetimeCancellation.Token);
            await RefreshManagedResourcesAsync(_lifetimeCancellation.Token);
            DictionaryResultText.Text = "字典已导入。";
        }
        catch (Exception exception) { DictionaryResultText.Text = $"字典导入失败：{exception.Message}"; }
    }

    private async void BrowseCalibreButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 Calibre ebook-convert",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Calibre") { Patterns = ["*.exe", "ebook-convert", "ebook-convert.exe"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path)) CalibrePathBox.Text = path;
    }

    private void OpenDataDirectoryButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _paths.EnsureDirectories();
            Process.Start(new ProcessStartInfo { FileName = _paths.Data, UseShellExecute = true });
        }
        catch (Exception exception) { SettingsStatusText.Text = $"无法打开数据目录：{exception.Message}"; }
    }

    private async void SaveApplicationSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        var selectedFormat = (PreferredOpenFormatBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "epub";
        _appSettings = AppSettings.Normalize(_appSettings with
        {
            PreferredOpenFormat = selectedFormat,
            CalibrePath = CalibrePathBox.Text ?? string.Empty,
            AutoBackupEnabled = AutoBackupCheck.IsChecked == true,
            NetworkEnabled = NetworkEnabledCheck.IsChecked != false,
            AutoConnectDevice = AutoConnectDeviceCheck.IsChecked != false
        });
        try
        {
            await _appSettingsStore.SaveAsync(_appSettings, _lifetimeCancellation.Token);
            if (!string.IsNullOrWhiteSpace(_appSettings.CalibrePath))
                Environment.SetEnvironmentVariable("KKINDLE_CALIBRE_CONVERT", _appSettings.CalibrePath, EnvironmentVariableTarget.Process);
            SettingsStatusText.Text = "基础设置已保存。";
        }
        catch (Exception exception) { SettingsStatusText.Text = $"保存失败：{exception.Message}"; }
    }

    private async void ExportBackupButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_backupBusy) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出 Kkindle 备份",
            SuggestedFileName = $"Kkindle-备份-{DateTime.Now:yyyyMMdd-HHmmss}{AppBackupService.FileExtension}",
            FileTypeChoices = [new FilePickerFileType("Kkindle 备份") { Patterns = [$"*{AppBackupService.FileExtension}"] }]
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        _backupBusy = true;
        try
        {
            var result = await _backupService.ExportAsync(path, _lifetimeCancellation.Token);
            SettingsBackupStatusText.Text = $"已导出 {result.BookCount} 本书、{result.FileCount} 个文件。";
        }
        catch (Exception exception) { SettingsBackupStatusText.Text = $"备份导出失败：{exception.Message}"; }
        finally { _backupBusy = false; }
    }

    private async void ImportBackupButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_backupBusy) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入 Kkindle 备份",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Kkindle 备份") { Patterns = [$"*{AppBackupService.FileExtension}"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) || !await ConfirmAsync("导入 Kkindle 备份", "导入会覆盖当前书库、封面和阅读记录，确定继续吗？")) return;
        _backupBusy = true;
        try
        {
            var result = await _backupService.ImportAsync(path, _lifetimeCancellation.Token);
            await _library.InitializeAsync(_lifetimeCancellation.Token);
            await _readerData.InitializeAsync(_lifetimeCancellation.Token);
            await ViewModel.RefreshAsync(_lifetimeCancellation.Token);
            await RefreshCollectionsAsync();
            SettingsBackupStatusText.Text = $"已导入 {result.BookCount} 本书、{result.FileCount} 个文件。";
            UpdateLibraryUi();
        }
        catch (Exception exception) { SettingsBackupStatusText.Text = $"备份导入失败：{exception.Message}"; }
        finally { _backupBusy = false; }
    }

    private void UpdateZLibraryAccountStatus()
    {
        ZLibraryStatusText.Text = _zLibrarySettings.IsConfigured
            ? $"已配置账号：{_zLibrarySettings.Email}"
            : "未配置账号，可搜索书籍；下载前需要登录。";
    }

    private async void ZLibraryBooksButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowStage3Page(ZLibraryPage);
        UpdateZLibraryAccountStatus();
        await Task.CompletedTask;
    }

    private async void ZLibrarySearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await StartZLibrarySearchAsync();
    }

    private async void ZLibrarySearchButton_Click(object? sender, RoutedEventArgs e) => await StartZLibrarySearchAsync();

    private async Task StartZLibrarySearchAsync()
    {
        var query = ZLibrarySearchBox.Text?.Trim() ?? string.Empty;
        if (!_appSettings.NetworkEnabled)
        {
            ZLibraryResultText.Text = "网络功能已关闭，请在设置中开启。";
            return;
        }
        if (query.Length == 0)
        {
            ZLibraryResultText.Text = "请输入书名或作者。";
            return;
        }
        _zLibraryPage = 1;
        await PerformZLibrarySearchAsync(query, _zLibraryPage);
    }

    private async Task PerformZLibrarySearchAsync(string query, int page)
    {
        if (_zLibrarySearching) return;
        _zLibrarySearching = true;
        ZLibrarySearchButton.IsEnabled = false;
        ZLibraryResultText.Text = $"正在搜索《{query}》…";
        try
        {
            if (_zLibrarySettings.IsConfigured && !_zLibraryService.IsLoggedIn)
                await _zLibraryService.LoginAsync(_zLibrarySettings.Email, _zLibrarySettings.Password, _zLibrarySettings.BaseUrl, _lifetimeCancellation.Token);
            var extension = (ZLibraryExtensionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var language = (ZLibraryLanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var result = await _zLibraryService.SearchAsync(
                query,
                page,
                extensions: string.IsNullOrWhiteSpace(extension) ? null : [extension],
                languages: string.IsNullOrWhiteSpace(language) ? null : [language],
                cancellationToken: _lifetimeCancellation.Token);
            ZLibraryBooks.Clear();
            foreach (var book in result.Books) ZLibraryBooks.Add(new ZLibraryBookCardViewModel(book));
            _zLibraryPage = result.Page;
            _zLibraryPageCount = result.PageCount;
            ZLibraryResultText.Text = result.Books.Count == 0 ? "没有找到匹配书籍。" : $"共找到 {result.Total} 本相关书籍 · 第 {_zLibraryPage} / {Math.Max(1, _zLibraryPageCount)} 页";
        }
        catch (Exception exception) { ZLibraryResultText.Text = $"搜索失败：{exception.Message}"; }
        finally
        {
            _zLibrarySearching = false;
            ZLibrarySearchButton.IsEnabled = true;
        }
    }

    private async void ZLibraryPrevPageButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_zLibraryPage > 1) await PerformZLibrarySearchAsync(ZLibrarySearchBox.Text?.Trim() ?? string.Empty, _zLibraryPage - 1);
    }

    private async void ZLibraryNextPageButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_zLibraryPageCount > 0 && _zLibraryPage < _zLibraryPageCount)
            await PerformZLibrarySearchAsync(ZLibrarySearchBox.Text?.Trim() ?? string.Empty, _zLibraryPage + 1);
    }

    private void ZLibraryDetailsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ZLibraryBookCardViewModel item })
            ZLibraryResultText.Text = $"{item.Title}\n{item.DetailDescription}";
    }

    private async void ZLibraryDownloadButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ZLibraryBookCardViewModel item } || item.IsDownloading) return;
        if (!_appSettings.NetworkEnabled)
        {
            item.SetStatus("网络功能已关闭");
            return;
        }
        if (!_zLibrarySettings.IsConfigured)
        {
            item.SetStatus("请先在设置中配置账号");
            return;
        }
        item.IsDownloading = true;
        try
        {
            if (!_zLibraryService.IsLoggedIn)
                await _zLibraryService.LoginAsync(_zLibrarySettings.Email, _zLibrarySettings.Password, _zLibrarySettings.BaseUrl, _lifetimeCancellation.Token);
            var downloadDirectory = Path.Combine(_paths.Data, "downloads");
            var downloaded = await _zLibraryService.DownloadAsync(item.Book, downloadDirectory, new Progress<TransferProgress>(item.SetDownloadProgress), _lifetimeCancellation.Token);
            var result = await _library.ImportAsync([downloaded], cancellationToken: _lifetimeCancellation.Token);
            if (result.FailureCount > 0) throw new IOException(result.Items.FirstOrDefault()?.Message ?? "导入书库失败。");
            item.MarkDownloadCompleted();
            item.SetStatus("已下载并导入电脑书库");
            await ViewModel.RefreshAsync(_lifetimeCancellation.Token);
            await RefreshCollectionsAsync();
            UpdateLibraryUi();
            try { File.Delete(downloaded); } catch { }
        }
        catch (Exception exception) { item.SetStatus($"下载失败：{exception.Message}"); }
        finally { item.IsDownloading = false; }
    }

    private void ZLibraryAccountButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowStage3Page(SettingsPage, ZLibraryAccountNavigationButton);
        SettingsStatusText.Text = "请在下方保存 Z-Library 账号。";
        ZLibraryEmailBox.Focus();
    }

    private async void ZLibraryAccountSaveButton_Click(object? sender, RoutedEventArgs e)
    {
        var settings = ZLibrarySettings.Normalize(new ZLibrarySettings
        {
            Email = ZLibraryEmailBox.Text ?? string.Empty,
            Password = ZLibraryPasswordBox.Text ?? string.Empty,
            BaseUrl = ZLibraryBaseUrlBox.Text ?? string.Empty
        });
        var validation = settings.Validate();
        if (validation is not null)
        {
            ZLibraryAccountStatusText.Text = validation;
            return;
        }
        try
        {
            if (_appSettings.NetworkEnabled)
            {
                await _zLibraryService.LoginAsync(settings.Email, settings.Password, settings.BaseUrl, _lifetimeCancellation.Token);
                settings.BaseUrl = _zLibraryService.ActiveBaseUrl;
            }
            await _zLibrarySettingsStore.SaveAsync(settings, _lifetimeCancellation.Token);
            _zLibrarySettings = settings;
            UpdateZLibraryAccountStatus();
            ZLibraryAccountStatusText.Text = "账号已保存。";
        }
        catch (Exception exception) { ZLibraryAccountStatusText.Text = $"保存或验证失败：{exception.Message}"; }
    }

    private async void KindleEmailSettingsSaveButton_Click(object? sender, RoutedEventArgs e)
    {
        var settings = KindleEmailSettings.Normalize(new KindleEmailSettings
        {
            KindleEmailAddress = KindleEmailRecipientBox.Text ?? string.Empty,
            SenderEmailAddress = KindleEmailSenderBox.Text ?? string.Empty,
            SmtpHost = KindleEmailSmtpHostBox.Text ?? string.Empty,
            SmtpPort = int.TryParse(KindleEmailSmtpPortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : 587,
            SmtpUsername = KindleEmailUsernameBox.Text ?? string.Empty,
            SmtpPassword = KindleEmailPasswordBox.Text ?? string.Empty,
            EnableSsl = KindleEmailSslCheck.IsChecked != false
        });
        var validation = settings.Validate();
        if (validation is not null)
        {
            KindleEmailSettingsStatusText.Text = validation;
            return;
        }
        try
        {
            await _kindleEmailSettingsStore.SaveAsync(settings, _lifetimeCancellation.Token);
            _kindleEmailSettings = settings;
            KindleEmailSettingsStatusText.Text = "Kindle 邮箱设置已保存。";
        }
        catch (Exception exception) { KindleEmailSettingsStatusText.Text = $"保存失败：{exception.Message}"; }
    }

    private static DateTimeOffset? ParseClippingDate(string metadata)
    {
        var separator = metadata.IndexOf('|');
        var value = separator >= 0 ? metadata[(separator + 1)..] : metadata;
        value = value.Replace("Added on", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("添加于", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim(' ', '-', ':', '：');
        return DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : null;
    }
}

public sealed record Stage3DashboardDayViewModel(
    string DateLabel,
    string TimeLabel,
    double BarHeight);

public sealed class Stage3ReadingMaterialViewModel : ObservableObject
{
    private bool _isSelected;

    public Stage3ReadingMaterialViewModel(
        ReadingMaterialSource source,
        string bookTitle,
        string typeLabel,
        string chapterLabel,
        string location,
        string quote,
        string note,
        DateTimeOffset? updatedAt,
        ReaderAnnotation? localAnnotation,
        KindleClipping? kindleClipping)
    {
        Source = source;
        BookTitle = bookTitle;
        TypeLabel = typeLabel;
        ChapterLabel = chapterLabel;
        Location = location;
        Quote = quote;
        Note = note;
        UpdatedAt = updatedAt;
        LocalAnnotation = localAnnotation;
        KindleClipping = kindleClipping;
    }

    public ReadingMaterialSource Source { get; }
    public string SourceLabel => Source == ReadingMaterialSource.Local ? "本地" : "Kindle";
    public string BookTitle { get; }
    public string TypeLabel { get; }
    public string ChapterLabel { get; }
    public string Location { get; }
    public string Quote { get; }
    public string Note { get; }
    public DateTimeOffset? UpdatedAt { get; }
    public ReaderAnnotation? LocalAnnotation { get; }
    public KindleClipping? KindleClipping { get; }
    public string QuoteLabel => string.IsNullOrWhiteSpace(Quote) ? "无划线内容" : $"“{Quote}”";
    public string NoteLabel => string.IsNullOrWhiteSpace(Note) ? "" : $"批注：{Note}";
    public string DateLabel => UpdatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "时间未知";
    public string SearchText => string.Join('\n', SourceLabel, BookTitle, TypeLabel, ChapterLabel, Location, Quote, Note);
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ReadingMaterialRecord ToRecord() => new(Source, BookTitle, TypeLabel, Location, Quote, Note, UpdatedAt);
}

public sealed class KindleBookCardViewModel : ObservableObject, IDisposable
{
    private Bitmap? _coverImage;
    private BookLibraryPresence _libraryPresence = BookLibraryPresence.KindleOnly;
    private bool _isDownloading;
    private double _downloadProgress;
    private string _statusMessage = string.Empty;

    public KindleBookCardViewModel(KindleBook book)
    {
        Book = book;
        if (!string.IsNullOrWhiteSpace(book.CoverPath) && File.Exists(book.CoverPath))
        {
            try { _coverImage = new Bitmap(book.CoverPath); } catch { }
        }
    }

    public KindleBook Book { get; }
    public Bitmap? CoverImage => _coverImage;
    public string Title => Book.Title;
    public string Authors => Book.Authors;
    public string FormatLabel => Book.Format.ToUpperInvariant();
    public string InfoLabel => $"{FormatLabel} · {Book.SizeLabel}";
    public string RelativePath => Book.RelativePath;
    public string PresenceLabel => LibraryPresence switch
    {
        BookLibraryPresence.Both => "电脑与 Kindle 都有",
        BookLibraryPresence.ComputerOnly => "仅电脑书库",
        _ => "仅 Kindle"
    };
    public BookLibraryPresence LibraryPresence
    {
        get => _libraryPresence;
        private set
        {
            if (SetProperty(ref _libraryPresence, value)) OnPropertyChanged(nameof(PresenceLabel));
        }
    }
    public bool IsDownloading
    {
        get => _isDownloading;
        set => SetProperty(ref _isDownloading, value);
    }
    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => SetProperty(ref _downloadProgress, value);
    }
    public void SetLibraryPresence(BookLibraryPresence presence) => LibraryPresence = presence;
    public void SetDownloadProgress(TransferProgress progress) => DownloadProgress = progress.Percentage;
    public void Dispose() => _coverImage?.Dispose();
}

public sealed class ZLibraryBookCardViewModel : ObservableObject
{
    private bool _isDownloading;
    private double _downloadProgress;
    private string _statusMessage = string.Empty;

    public ZLibraryBookCardViewModel(ZLibraryBook book) => Book = book;
    public ZLibraryBook Book { get; }
    public string Title => Book.Title;
    public string Authors => Book.Author;
    public string InfoLabel => Book.InfoLabel;
    public string PublicationLabel => string.Join(" · ", new[]
    {
        Book.Publisher ?? string.Empty,
        Book.Year?.ToString() ?? string.Empty,
        string.IsNullOrWhiteSpace(Book.Series) ? string.Empty : $"系列：{Book.Series}"
    }.Where(value => value.Length > 0));
    public string DetailDescription => string.IsNullOrWhiteSpace(Book.Description) ? "暂无简介。" : Book.Description;
    public bool IsDownloading
    {
        get => _isDownloading;
        set => SetProperty(ref _isDownloading, value);
    }
    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => SetProperty(ref _downloadProgress, value);
    }
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }
    public void SetDownloadProgress(TransferProgress progress)
    {
        DownloadProgress = progress.Percentage;
        StatusMessage = $"正在下载 {progress.Percentage:0}%";
    }
    public void MarkDownloadCompleted() => DownloadProgress = 100;
    public void SetStatus(string message) => StatusMessage = message;
}
