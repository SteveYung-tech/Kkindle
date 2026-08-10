using System.Collections.ObjectModel;
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
    private readonly AppSettingsStore _appSettingsStore;
    private readonly DictionaryService _dictionaryService;
    private readonly FontLibraryService _fontLibrary;
    private readonly PdfTextService _pdfTextService;
    private AppSettings _appSettings = new();
    private readonly ObservableCollection<ManagedFont> _managedFonts = [];
    private readonly ObservableCollection<DictionaryDefinition> _managedDictionaries = [];
    private readonly ObservableCollection<ReadingDashboardDisplayItem> _readingDashboardItems = [];

    private async Task LoadProductivityStateAsync()
    {
        _appSettings = await _appSettingsStore.LoadAsync();
        ApplyAppSettingsToRuntime();
        PopulateApplicationSettingsControls();
        await RefreshManagedFontsAsync();
        await RefreshManagedDictionariesAsync();
        await RefreshReadingDashboardAsync();
        await RunAutoBackupIfNeededAsync();
    }

    private void ApplyAppSettingsToRuntime()
    {
        if (!string.IsNullOrWhiteSpace(_appSettings.CalibrePath))
            Environment.SetEnvironmentVariable("KKINDLE_CALIBRE_CONVERT", _appSettings.CalibrePath, EnvironmentVariableTarget.Process);
    }

    private void PopulateApplicationSettingsControls()
    {
        PreferredOpenFormatBox.SelectedItem = PreferredOpenFormatBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, _appSettings.PreferredOpenFormat, StringComparison.OrdinalIgnoreCase))
            ?? PreferredOpenFormatBox.Items[0];
        CalibrePathBox.Text = _appSettings.CalibrePath;
        AutoBackupCheck.IsChecked = _appSettings.AutoBackupEnabled;
        AutoGenerateReaderFormatsCheck.IsChecked = _appSettings.AutoGenerateEpubAndAzw3OnImport;
        AutoBackupRetentionBox.Value = _appSettings.AutoBackupRetention;
        AiEnabledCheck.IsChecked = _appSettings.AiEnabled;
        NetworkEnabledCheck.IsChecked = _appSettings.NetworkEnabled;
        AutoConnectDeviceCheck.IsChecked = _appSettings.AutoConnectDevice;
        DefaultFontScaleBox.Value = _appSettings.DefaultReaderLayout.FontScale;
        DefaultLineHeightBox.Value = _appSettings.DefaultReaderLayout.LineHeight;
        DefaultMaxWidthBox.Value = _appSettings.DefaultReaderLayout.MaxWidth;
        DefaultBodyPaddingBox.Value = _appSettings.DefaultReaderLayout.BodyPadding;
        SelectFontFamily(DefaultFontFamilyBox, _appSettings.DefaultReaderLayout.FontFamily);
        DefaultVerticalWritingCheck.IsChecked = _appSettings.DefaultReaderLayout.VerticalWriting;
    }

    private async void SaveApplicationSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _appSettings = AppSettings.Normalize(_appSettings with
            {
                PreferredOpenFormat = (PreferredOpenFormatBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "epub",
                CalibrePath = CalibrePathBox.Text,
                AutoBackupEnabled = AutoBackupCheck.IsChecked == true,
                AutoGenerateEpubAndAzw3OnImport = AutoGenerateReaderFormatsCheck.IsChecked == true,
                AutoBackupRetention = double.IsFinite(AutoBackupRetentionBox.Value) ? (int)AutoBackupRetentionBox.Value : 5,
                AiEnabled = AiEnabledCheck.IsChecked == true,
                NetworkEnabled = NetworkEnabledCheck.IsChecked == true,
                AutoConnectDevice = AutoConnectDeviceCheck.IsChecked == true,
                DefaultReaderLayout = new ReaderLayoutSettings(
                    DefaultFontScaleBox.Value,
                    DefaultLineHeightBox.Value,
                    DefaultMaxWidthBox.Value,
                    DefaultBodyPaddingBox.Value,
                    (DefaultFontFamilyBox.SelectedItem as ComboBoxItem)?.Tag as string ?? ReaderFontDefaults.DefaultFamily,
                    _appSettings.DefaultReaderLayout.FlowMode,
                    DefaultVerticalWritingCheck.IsChecked == true,
                    _appSettings.DefaultReaderLayout.TwoPageMode)
            });
            await _appSettingsStore.SaveAsync(_appSettings);
            ApplyAppSettingsToRuntime();
            ApplicationSettingsStatusText.Text = "设置已保存";
            if (_appSettings.AutoConnectDevice)
            {
                _ignoredDeviceId = null;
                await RefreshDevicesAsync();
            }
        }
        catch (Exception exception) { ApplicationSettingsStatusText.Text = $"保存失败：{exception.Message}"; }
    }

    private async void BrowseCalibreButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".exe");
        var file = await picker.PickSingleFileAsync();
        if (file is not null) CalibrePathBox.Text = file.Path;
    }

    private void OpenDataDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        _paths.EnsureDirectories();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{_paths.Data}\"") { UseShellExecute = true });
    }

    private async void MigrateDataDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReaderPane.Visibility == Visibility.Visible)
        {
            await ShowMessageAsync("请先返回书库", "迁移数据目录前请关闭阅读器。");
            return;
        }
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        var targetRoot = Path.GetFullPath(folder.Path);
        if (targetRoot.Equals(Path.GetFullPath(_paths.Root), StringComparison.OrdinalIgnoreCase))
        {
            ApplicationSettingsStatusText.Text = "所选目录已经是当前数据根目录";
            return;
        }
        try
        {
            var migrationBackup = AppRootConfiguration.MigrationBackupPath(targetRoot);
            await _backupService.ExportAsync(migrationBackup);
            AppRootConfiguration.Save(AppContext.BaseDirectory, targetRoot);
            ApplicationSettingsStatusText.Text = "迁移包已准备；重启 Kkindle 后自动完成迁移。";
        }
        catch (Exception exception) { ApplicationSettingsStatusText.Text = $"迁移准备失败：{exception.Message}"; }
    }

    private void ShowSettingsSection(Button navigationButton, FrameworkElement section)
    {
        ShowSettings();
        SetActiveNavigation(navigationButton);
        section.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0 });
    }

    private async void FontManagementButton_Click(object sender, RoutedEventArgs e) => await OpenDeviceResourcePageAsync(KindleResourceKind.Font);
    private async void DictionaryManagementButton_Click(object sender, RoutedEventArgs e) => await OpenDeviceResourcePageAsync(KindleResourceKind.Dictionary);

    private async void ReadingDashboardButton_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(ReadingDashboardNavigationButton);
        LibraryPane.Visibility = Visibility.Collapsed;
        SettingsPane.Visibility = Visibility.Collapsed;
        DevicePage.Visibility = Visibility.Collapsed;
        DeviceResourcePage.Visibility = Visibility.Collapsed;
        ReadingMaterialsPage.Visibility = Visibility.Collapsed;
        ZLibraryPage.Visibility = Visibility.Collapsed;
        DetailPane.Visibility = Visibility.Collapsed;
        DetailColumn.Width = new GridLength(0);
        ReadingDashboardPage.Visibility = Visibility.Visible;
        await RefreshReadingDashboardAsync();
    }

    private async Task RefreshManagedFontsAsync()
    {
        _managedFonts.Clear();
        foreach (var font in await _fontLibrary.ListAsync()) _managedFonts.Add(font);
        ManagedFontList.ItemsSource = _managedFonts;
        PopulateManagedReaderFonts();
    }

    private void PopulateManagedReaderFonts()
    {
        if (ReaderFontFamilyBox is null) return;
        var readerSelection = _readerLayout.FontFamily;
        var defaultSelection = _appSettings.DefaultReaderLayout.FontFamily;
        RemoveManagedFontItems(ReaderFontFamilyBox);
        RemoveManagedFontItems(DefaultFontFamilyBox);
        foreach (var font in _managedFonts)
        {
            ReaderFontFamilyBox.Items.Add(new ComboBoxItem { Content = $"{font.DisplayName}（本地）", Tag = font.CssFamily, DataContext = "managed-font-marker" });
            DefaultFontFamilyBox.Items.Add(new ComboBoxItem { Content = $"{font.DisplayName}（本地）", Tag = font.CssFamily, DataContext = "managed-font-marker" });
        }
        SelectFontFamily(ReaderFontFamilyBox, readerSelection);
        SelectFontFamily(DefaultFontFamilyBox, defaultSelection);
    }

    private static void RemoveManagedFontItems(ComboBox comboBox)
    {
        foreach (var old in comboBox.Items.OfType<ComboBoxItem>().Where(item => Equals(item.DataContext, "managed-font-marker")).ToArray())
            comboBox.Items.Remove(old);
    }

    private static void SelectFontFamily(ComboBox comboBox, string family)
    {
        comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, family, StringComparison.OrdinalIgnoreCase))
            ?? comboBox.Items[0];
    }

    private async void ImportFontButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        foreach (var extension in new[] { ".ttf", ".otf", ".woff", ".woff2" }) picker.FileTypeFilter.Add(extension);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try { await _fontLibrary.ImportAsync(file.Path); await RefreshManagedFontsAsync(); FontManagementStatusText.Text = "字体已导入"; }
        catch (Exception exception) { FontManagementStatusText.Text = $"导入失败：{exception.Message}"; }
    }

    private async void RemoveFontButton_Click(object sender, RoutedEventArgs e)
    {
        if (ManagedFontList.SelectedItem is not ManagedFont font) return;
        try { await _fontLibrary.RemoveAsync(font.Id); await RefreshManagedFontsAsync(); FontManagementStatusText.Text = "字体已移除"; }
        catch (Exception exception) { FontManagementStatusText.Text = $"移除失败：{exception.Message}"; }
    }

    private async Task RefreshManagedDictionariesAsync()
    {
        _managedDictionaries.Clear();
        foreach (var dictionary in await _dictionaryService.ListAsync()) _managedDictionaries.Add(dictionary);
        ManagedDictionaryList.ItemsSource = _managedDictionaries;
    }

    private async void ImportDictionaryButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add(".tsv");
        picker.FileTypeFilter.Add(".csv");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try { await _dictionaryService.ImportAsync(file.Path); await RefreshManagedDictionariesAsync(); DictionaryResultText.Text = "词典已导入"; }
        catch (Exception exception) { DictionaryResultText.Text = $"导入失败：{exception.Message}"; }
    }

    private async void RemoveDictionaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (ManagedDictionaryList.SelectedItem is not DictionaryDefinition dictionary) return;
        try { await _dictionaryService.RemoveAsync(dictionary.Id); await RefreshManagedDictionariesAsync(); DictionaryResultText.Text = "词典已移除"; }
        catch (Exception exception) { DictionaryResultText.Text = $"移除失败：{exception.Message}"; }
    }

    private async Task LookupDictionaryAsync(string text, TextBlock target)
    {
        var entries = await _dictionaryService.LookupAsync(text);
        target.Text = entries.Count == 0
            ? $"没有找到“{text.Trim()}”"
            : string.Join("\n\n", entries.Select(entry => $"{entry.Term} · {entry.DictionaryName}\n{entry.Definition}"));
    }

    private async void DictionaryTestButton_Click(object sender, RoutedEventArgs e) => await LookupDictionaryAsync(DictionaryTestBox.Text, DictionaryResultText);
    private async void DictionaryTestBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        await LookupDictionaryAsync(DictionaryTestBox.Text, DictionaryResultText);
    }

    private async void ReaderSelectionDictionaryButton_Click(object sender, RoutedEventArgs e)
    {
        var text = _readerSelectionText ?? await CaptureReaderSelectionTextAsync();
        HideReaderSelectionPopup();
        if (string.IsNullOrWhiteSpace(text)) return;
        var entries = await _dictionaryService.LookupAsync(text);
        await ShowMessageAsync($"词典 · {text.Trim()}", entries.Count == 0
            ? "没有找到释义。请先在“字典管理”中导入词典。"
            : string.Join("\n\n", entries.Select(entry => $"[{entry.DictionaryName}] {entry.Definition}")));
    }

    private async Task RefreshReadingDashboardAsync()
    {
        var dashboard = await _readerData.GetReadingDashboardAsync(100);
        var hours = dashboard.TotalSeconds / 3600d;
        ReadingDashboardSummaryText.Text = $"已开始 {dashboard.BooksStarted} 本 · 已读完 {dashboard.BooksFinished} 本 · 累计 {hours:0.0} 小时";
        ReadingDashboardDetailText.Text = $"平均进度 {dashboard.AverageProgress:0}% · 书签 {dashboard.BookmarkCount} 条 · 批注 {dashboard.AnnotationCount} 条";
        var books = await _library.SearchAsync();
        var titles = books.ToDictionary(book => book.Id, book => book.Title);
        _readingDashboardItems.Clear();
        foreach (var item in dashboard.RecentBooks)
        {
            var title = titles.GetValueOrDefault(item.BookId) ?? "已删除书籍";
            _readingDashboardItems.Add(new ReadingDashboardDisplayItem(title, item.ProgressPercent, item.CumulativeSeconds, item.UpdatedAt));
        }
        ReadingDashboardRecentList.ItemsSource = _readingDashboardItems;

        ReadingDashboardTotalTimeText.Text = FormatReadingDuration(dashboard.TotalSeconds);
        ReadingDashboardStartedText.Text = $"{dashboard.BooksStarted} 本";
        ReadingDashboardFinishedText.Text = $"{dashboard.BooksFinished} 本";
        ReadingDashboardNotesText.Text = $"{dashboard.BookmarkCount} / {dashboard.AnnotationCount}";

        ReadingDailyChart.SetData(
            dashboard.DailyReading.Select(day => new MonochromeChartValue(
                day.Date.ToString("MM-dd"),
                day.ActiveSeconds / 60d,
                $"{day.ActiveSeconds / 60d:0.#} 分")),
            accessibleName: "近十四天每天阅读时长柱状图");

        ReadingBookTimeChart.SetData(
            dashboard.RecentBooks
                .OrderByDescending(item => item.CumulativeSeconds)
                .Take(8)
                .Select(item => new MonochromeChartValue(
                    titles.GetValueOrDefault(item.BookId) ?? "已删除书籍",
                    item.CumulativeSeconds,
                    FormatReadingDuration(item.CumulativeSeconds))),
            MonochromeBarChartOrientation.Horizontal,
            "单本书累计阅读时长排行");

        var progressBuckets = new[]
        {
            new { Label = "0–24%", Count = dashboard.RecentBooks.Count(item => item.ProgressPercent < 25) },
            new { Label = "25–49%", Count = dashboard.RecentBooks.Count(item => item.ProgressPercent >= 25 && item.ProgressPercent < 50) },
            new { Label = "50–74%", Count = dashboard.RecentBooks.Count(item => item.ProgressPercent >= 50 && item.ProgressPercent < 75) },
            new { Label = "75–99%", Count = dashboard.RecentBooks.Count(item => item.ProgressPercent >= 75 && item.ProgressPercent < 99.5) },
            new { Label = "完成", Count = dashboard.RecentBooks.Count(item => item.ProgressPercent >= 99.5) }
        };
        ReadingProgressChart.SetData(
            progressBuckets.Select(bucket => new MonochromeChartValue(bucket.Label, bucket.Count, $"{bucket.Count} 本")),
            accessibleName: "阅读进度区间分布柱状图");

        var readingCount = Math.Max(0, dashboard.BooksStarted - dashboard.BooksFinished);
        ReadingStatusChart.SetData(
            [
                new MonochromeChartValue("阅读中", readingCount, $"{readingCount} 本"),
                new MonochromeChartValue("已完成", dashboard.BooksFinished, $"{dashboard.BooksFinished} 本")
            ],
            accessibleName: "阅读中与已完成书籍分布柱状图");
    }

    private static string FormatReadingDuration(long seconds)
    {
        if (seconds < 60) return $"{seconds} 秒";
        if (seconds < 3600) return $"{seconds / 60d:0.#} 分";
        return $"{seconds / 3600d:0.#} 小时";
    }

    private async void RefreshReadingDashboardButton_Click(object sender, RoutedEventArgs e) => await RefreshReadingDashboardAsync();

    private async void ExportReadingDashboardButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshReadingDashboardAsync();
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeChoices.Add("CSV", [".csv"]);
        picker.SuggestedFileName = $"Kkindle-阅读数据-{DateTime.Now:yyyyMMdd}";
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        var csv = new StringBuilder("书名,进度,阅读秒数,最近阅读\r\n");
        foreach (var item in _readingDashboardItems) csv.AppendLine($"\"{item.Title.Replace("\"", "\"\"")}\",{item.ProgressPercent:0.##},{item.Seconds},{item.UpdatedAt:O}");
        await File.WriteAllTextAsync(file.Path, csv.ToString(), new UTF8Encoding(true));
    }

    private async Task RunAutoBackupIfNeededAsync()
    {
        if (!_appSettings.AutoBackupEnabled) return;
        Directory.CreateDirectory(_paths.Backups);
        var existing = Directory.GetFiles(_paths.Backups, "*.kkindle").Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc).ToList();
        if (existing.FirstOrDefault() is { } latest && DateTime.UtcNow - latest.LastWriteTimeUtc < TimeSpan.FromHours(20)) return;
        try
        {
            var destination = Path.Combine(_paths.Backups, $"Kkindle-auto-{DateTime.Now:yyyyMMdd-HHmmss}.kkindle");
            await _backupService.ExportAsync(destination);
            existing = Directory.GetFiles(_paths.Backups, "*.kkindle").Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc).ToList();
            foreach (var old in existing.Skip(_appSettings.AutoBackupRetention)) old.Delete();
        }
        catch (Exception exception) { ApplicationSettingsStatusText.Text = $"自动备份失败：{exception.Message}"; }
    }

    private sealed record ReadingDashboardDisplayItem(string Title, double ProgressPercent, long Seconds, DateTimeOffset UpdatedAt)
    {
        public string Display => $"{Title} · {ProgressPercent:0}% · {Seconds / 60} 分钟 · {UpdatedAt.ToLocalTime():MM-dd HH:mm}";
    }
}
