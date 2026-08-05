using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public sealed partial class MainWindow : Window
{
    private readonly AppPaths _paths;
    private readonly IBookLibraryService _library;
    private readonly IKindleDeviceService _kindle;
    private readonly DispatcherQueueTimer _deviceTimer;
    private Book? _selectedBook;
    private IReadOnlyList<KindleDevice> _devices = [];
    private bool _isRefreshingDevices;
    private bool _isTransferring;
    private string? _scannedDeviceId;
    private double _deviceUsedRatio;
    private string? _acceptedDeviceId;
    private string? _ignoredDeviceId;

    public MainWindow(AppPaths paths, IBookLibraryService library, IKindleDeviceService kindle)
    {
        _paths = paths;
        _library = library;
        _kindle = kindle;
        ViewModel = new LibraryViewModel(library, paths.Data);
        InitializeComponent();
        Closed += MainWindow_Closed;

        _deviceTimer = DispatcherQueue.CreateTimer();
        _deviceTimer.Interval = TimeSpan.FromSeconds(3);
        _deviceTimer.Tick += async (_, _) => await RefreshDevicesAsync();
        _deviceTimer.Start();
        RootGrid.Loaded += MainWindow_Loaded;
    }

    public LibraryViewModel ViewModel { get; }
    public ObservableCollection<KindleBook> DeviceBooks { get; } = [];

    private void DeviceChangeMonitor_DeviceChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(async () => await RefreshDevicesAsync());
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _deviceTimer.Stop();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshLibraryAsync();
        await RefreshDevicesAsync();
    }

    private async Task RefreshLibraryAsync()
    {
        try
        {
            await ViewModel.RefreshAsync();
            LibrarySummaryText.Text = ViewModel.StatusText;
            SidebarCountText.Text = ViewModel.Books.Count.ToString();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("无法读取书库", ex.Message);
        }
    }

    private async Task RefreshDevicesAsync()
    {
        if (_isRefreshingDevices) return;
        _isRefreshingDevices = true;
        try
        {
            var detectedDevices = await _kindle.DetectDevicesAsync();
            if (detectedDevices.Count == 0)
            {
                _acceptedDeviceId = null;
                _ignoredDeviceId = null;
                SetDisconnectedDeviceState();
                return;
            }

            var device = detectedDevices[0];
            if (!string.Equals(_acceptedDeviceId, device.VolumeSerial, StringComparison.Ordinal))
            {
                if (string.Equals(_ignoredDeviceId, device.VolumeSerial, StringComparison.Ordinal))
                {
                    SetDisconnectedDeviceState($"已忽略 {device.Name}");
                    return;
                }

                var prompt = new ContentDialog
                {
                    XamlRoot = ((FrameworkElement)Content).XamlRoot,
                    Title = "发现 Kindle 设备",
                    Content = $"发现 {device.Name}（{device.ConnectionLabel}）。是否连接到 Kkindle？",
                    PrimaryButtonText = "连接",
                    CloseButtonText = "暂不连接",
                    DefaultButton = ContentDialogButton.Primary
                };
                if (await prompt.ShowAsync() != ContentDialogResult.Primary)
                {
                    _ignoredDeviceId = device.VolumeSerial;
                    SetDisconnectedDeviceState($"已忽略 {device.Name}");
                    return;
                }
                _acceptedDeviceId = device.VolumeSerial;
                _ignoredDeviceId = null;
            }

            _devices = [device];
            KindleStatusText.Text = device.Name;
            KindleConnectionText.Text = $"{device.ConnectionLabel} · 已连接";
            EjectDeviceButton.Visibility = Visibility.Visible;
            DeviceStorageText.Text = device.CapacityLabel;
            _deviceUsedRatio = device.TotalBytes <= 0
                ? 0
                : Math.Clamp((device.TotalBytes - device.FreeBytes) / (double)device.TotalBytes, 0, 1);
            UpdateDeviceStorageBar();
            DeviceNameText.Text = $"{device.Name} · {device.ConnectionLabel}";
            DeviceCapacityText.Text = device.CapacityLabel;
            if (!string.Equals(_scannedDeviceId, device.VolumeSerial, StringComparison.Ordinal))
                await ScanDeviceBooksAsync(device);
        }
        catch
        {
            SetDisconnectedDeviceState("设备状态读取失败");
        }
        finally { _isRefreshingDevices = false; }
    }

    private void SetDisconnectedDeviceState(string? detail = null)
    {
        _devices = [];
        _scannedDeviceId = null;
        DeviceBooks.Clear();
        KindleStatusText.Text = "无设备连接";
        KindleConnectionText.Text = detail ?? string.Empty;
        EjectDeviceButton.Visibility = Visibility.Collapsed;
        DeviceStorageText.Text = "无存储信息";
        _deviceUsedRatio = 0;
        UpdateDeviceStorageBar();
        DeviceNameText.Text = "未检测到设备";
        DeviceCapacityText.Text = "—";
        DeviceBookCountText.Text = "0 本";
    }

    private async Task ScanDeviceBooksAsync(KindleDevice device)
    {
        var books = await _kindle.ScanBooksAsync(device);
        DeviceBooks.Clear();
        foreach (var book in books) DeviceBooks.Add(book);
        DeviceBookCountText.Text = $"{books.Count} 本";
        _scannedDeviceId = device.VolumeSerial;
    }

    private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        ViewModel.SearchText = sender.Text;
        await RefreshLibraryAsync();
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.SearchText = SearchBox.Text;
        await RefreshLibraryAsync();
    }

    private async void ImportFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.ViewMode = PickerViewMode.Thumbnail;
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        foreach (var extension in new[] { ".epub", ".pdf", ".mobi", ".azw3" }) picker.FileTypeFilter.Add(extension);
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0) await ImportAsync(files.Select(x => x.Path));
    }

    private async void ImportFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.ViewMode = PickerViewMode.List;
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) await ImportAsync([folder.Path]);
    }

    private async Task ImportAsync(IEnumerable<string> paths)
    {
        TaskProgress.Visibility = Visibility.Visible;
        TaskProgress.Value = 0;
        try
        {
            var progress = new Progress<TransferProgress>(value =>
            {
                TaskProgress.Value = value.Percentage;
                TaskStatusText.Text = value.Message;
            });
            var result = await ViewModel.ImportAsync(paths, progress);
            TaskStatusText.Text = ViewModel.StatusText;
            if (result.FailureCount > 0)
            {
                var failures = string.Join("\n", result.Items.Where(x => !x.Succeeded).Take(5).Select(x => $"{Path.GetFileName(x.SourcePath)}：{x.Message}"));
                await ShowMessageAsync("部分文件未导入", failures);
            }
            LibrarySummaryText.Text = ViewModel.StatusText;
            SidebarCountText.Text = ViewModel.Books.Count.ToString();
        }
        catch (OperationCanceledException)
        {
            TaskStatusText.Text = "已取消";
        }
        catch (Exception ex)
        {
            TaskStatusText.Text = "导入失败";
            await ShowMessageAsync("导入失败", ex.Message);
        }
        finally
        {
            TaskProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void BookGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BookCardViewModel card) SelectBook(card.Book);
    }

    private void BookList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BookList.SelectedItem is BookCardViewModel card) SelectBook(card.Book);
    }

    private void SelectBook(Book book)
    {
        _selectedBook = book;
        DetailsTitleBox.Text = book.Title;
        DetailsAuthorsBox.Text = book.Authors;
        DetailsSeriesBox.Text = book.Series ?? string.Empty;
        DetailsTagsBox.Text = book.Tags;
        DetailsDescriptionBox.Text = book.Description ?? string.Empty;
        DetailCoverImage.Source = null;
        if (!string.IsNullOrWhiteSpace(book.CoverPath))
        {
            var coverPath = Path.GetFullPath(Path.Combine(_paths.Data, book.CoverPath));
            if (File.Exists(coverPath))
            {
                try { DetailCoverImage.Source = new BitmapImage(new Uri(coverPath)); }
                catch { }
            }
        }
        DetailColumn.Width = new GridLength(320);
        DetailPane.Visibility = Visibility.Visible;
    }

    private async void SaveDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBook is null) return;
        _selectedBook.Title = string.IsNullOrWhiteSpace(DetailsTitleBox.Text) ? "未命名书籍" : DetailsTitleBox.Text.Trim();
        _selectedBook.Authors = string.IsNullOrWhiteSpace(DetailsAuthorsBox.Text) ? "未知作者" : DetailsAuthorsBox.Text.Trim();
        _selectedBook.Series = string.IsNullOrWhiteSpace(DetailsSeriesBox.Text) ? null : DetailsSeriesBox.Text.Trim();
        _selectedBook.Tags = DetailsTagsBox.Text.Trim();
        _selectedBook.Description = string.IsNullOrWhiteSpace(DetailsDescriptionBox.Text) ? null : DetailsDescriptionBox.Text.Trim();
        await _library.UpdateMetadataAsync(_selectedBook);
        await RefreshLibraryAsync();
        SelectBook(_selectedBook);
        TaskStatusText.Text = "元数据已保存";
    }

    private async void SendToKindleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTransferring) return;
        if (_selectedBook is null || _selectedBook.Files.Count == 0)
        {
            await ShowMessageAsync("无法发送", "这本书没有可用的文件格式。");
            return;
        }
        await RefreshDevicesAsync();
        if (_devices.Count == 0)
        {
            await ShowMessageAsync("未找到 Kindle", "请连接并解锁 Kindle，然后重试。");
            return;
        }

        var file = _selectedBook.Files[0];
        var source = _library.GetAbsoluteFilePath(file);
        var device = _devices[0];
        var confirmation = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "发送到 Kindle？",
            Content = $"将“{_selectedBook.Title}”发送到 {device.Name}。\n\n如果设备上存在同名文件，Kkindle 会自动使用带序号的新文件名，不会覆盖原文件。",
            PrimaryButtonText = "发送",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

        _isTransferring = true;
        TaskProgress.Visibility = Visibility.Visible;
        try
        {
            var progress = new Progress<TransferProgress>(value =>
            {
                TaskProgress.Value = value.Percentage;
                TaskStatusText.Text = value.Message;
            });
            await _kindle.SendBookAsync(device, file, source, progress);
            TaskStatusText.Text = "已发送到 Kindle";
            _scannedDeviceId = null;
            await RefreshDevicesAsync();
        }
        catch (Exception ex)
        {
            TaskStatusText.Text = "发送失败";
            await ShowMessageAsync("发送失败", ex.Message);
        }
        finally
        {
            _isTransferring = false;
            TaskProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void DeleteBookButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBook is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "删除这本书？",
            Content = $"将从 Kkindle 书库中删除“{_selectedBook.Title}”及其文件。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await _library.DeleteAsync(_selectedBook.Id);
        _selectedBook = null;
        CloseDetails();
        await RefreshLibraryAsync();
    }

    private void CloseDetailButton_Click(object sender, RoutedEventArgs e) => CloseDetails();

    private void CloseDetails()
    {
        DetailPane.Visibility = Visibility.Collapsed;
        DetailColumn.Width = new GridLength(0);
    }

    private void GridViewButton_Click(object sender, RoutedEventArgs e)
    {
        BookGrid.Visibility = Visibility.Visible;
        BookList.Visibility = Visibility.Collapsed;
    }

    private void ListViewButton_Click(object sender, RoutedEventArgs e)
    {
        BookGrid.Visibility = Visibility.Collapsed;
        BookList.Visibility = Visibility.Visible;
    }

    private async void FilterButton_Click(object sender, RoutedEventArgs e) => await ShowMessageAsync("筛选", "首版先支持搜索；作者、标签和格式筛选将在下一轮接入。");
    private async void MoreButton_Click(object sender, RoutedEventArgs e) => await ShowMessageAsync("Kkindle", $"便携数据目录：{_paths.Data}");
    private async void AddTagButton_Click(object sender, RoutedEventArgs e) => await ShowMessageAsync("标签", "可以在书籍详情中直接编辑标签，多个标签用逗号分隔。");
    private async void AddCategoryButton_Click(object sender, RoutedEventArgs e) => await ShowMessageAsync("分类", "分类功能将在书库筛选基础完成后接入。");
    private async void SettingsButton_Click(object sender, RoutedEventArgs e) => await ShowMessageAsync("设置", $"当前书库位于：{_paths.Library}");
    private void KindleBooksButton_Click(object sender, RoutedEventArgs e) => OpenDevicePage(showBooks: true);

    private void DeviceOverviewButton_Click(object sender, RoutedEventArgs e) => OpenDevicePage(showBooks: false);

    private void OpenDevicePage(bool showBooks)
    {
        SetActiveNavigation(showBooks ? KindleBooksButton : DeviceOverviewButton);
        DevicePageTitleText.Text = showBooks ? "Kindle 书籍" : "设备概览";
        DeviceBookList.Visibility = showBooks ? Visibility.Visible : Visibility.Collapsed;
        DeviceReadOnlyNote.Visibility = showBooks ? Visibility.Visible : Visibility.Collapsed;
        LibraryPane.Visibility = Visibility.Collapsed;
        DetailPane.Visibility = Visibility.Collapsed;
        DetailColumn.Width = new GridLength(0);
        DevicePage.Visibility = Visibility.Visible;
    }

    private void AllBooksButton_Click(object sender, RoutedEventArgs e) => ShowLibrary();

    private async void RefreshDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        _ignoredDeviceId = null;
        _scannedDeviceId = null;
        await RefreshDevicesAsync();
    }

    private async void EjectDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTransferring)
        {
            await ShowMessageAsync("正在传输", "请等待书籍传输完成后再弹出设备。");
            return;
        }
        if (_devices.Count == 0) return;

        var device = _devices[0];
        var isWpd = device.Transport == KindleTransport.Wpd;
        var confirmation = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = isWpd ? "断开 Kindle？" : "安全弹出 Kindle？",
            Content = isWpd
                ? "Kindle Scribe 使用 MTP 连接，不提供磁盘安全弹出。Kkindle 将停止访问设备，随后可以断开 USB。"
                : "请确认当前没有正在进行的传输。",
            PrimaryButtonText = isWpd ? "断开" : "弹出",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            if (!isWpd) await _kindle.EjectAsync(device);
            _acceptedDeviceId = null;
            _ignoredDeviceId = device.VolumeSerial;
            SetDisconnectedDeviceState(isWpd ? $"已断开 {device.Name}" : $"已弹出 {device.Name}");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("无法弹出设备", ex.Message);
        }
    }

    private void ShowLibrary()
    {
        SetActiveNavigation(AllBooksButton);
        DevicePage.Visibility = Visibility.Collapsed;
        LibraryPane.Visibility = Visibility.Visible;
    }

    private void SetActiveNavigation(Button activeButton)
    {
        var ink = (Brush)Application.Current.Resources["InkBrush"];
        var paper = (Brush)Application.Current.Resources["CardBrush"];
        foreach (var button in new[] { AllBooksButton, KindleBooksButton, DeviceOverviewButton })
        {
            var isActive = button == activeButton;
            button.Background = isActive ? ink : paper;
            button.Foreground = isActive ? paper : ink;
        }
        AllBooksLabelText.Foreground = activeButton == AllBooksButton ? paper : ink;
        SidebarCountText.Foreground = activeButton == AllBooksButton ? paper : ink;
    }

    private void DeviceStorageBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateDeviceStorageBar();

    private void UpdateDeviceStorageBar()
    {
        var availableWidth = Math.Max(0, DeviceStorageBar.ActualWidth - 2);
        DeviceStorageUsedBar.Width = availableWidth * _deviceUsedRatio;
    }

    private async void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button == AllBooksButton)
        {
            ShowLibrary();
            return;
        }
        if (sender is Button)
            await ShowMessageAsync("Kkindle", "首版当前聚焦书架与 Kindle 同步。");
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }
}
