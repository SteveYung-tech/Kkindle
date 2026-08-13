using Kkindle.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Kkindle;

public sealed partial class MainWindow
{
    private readonly HashSet<string> _multiSelectedDeviceBookKeys = new(StringComparer.OrdinalIgnoreCase);
    private Windows.System.VirtualKeyModifiers _lastDevicePointerModifiers;
    private KindleBookCardViewModel? _deviceSelectAnchor;

    private void DeviceBookGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is KindleBookCardViewModel card) HandleDeviceBookClick(card);
    }

    private void DeviceBookList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is KindleBookCardViewModel card) HandleDeviceBookClick(card);
    }

    private void DeviceBookGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _lastDevicePointerModifiers = e.KeyModifiers;
    }

    private void DeviceBookList_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _lastDevicePointerModifiers = e.KeyModifiers;
    }

    private void HandleDeviceBookClick(KindleBookCardViewModel card)
    {
        var modifiers = _lastDevicePointerModifiers;
        if ((modifiers & Windows.System.VirtualKeyModifiers.Control) != 0)
        {
            ToggleDeviceMultiSelection(card);
            return;
        }

        if ((modifiers & Windows.System.VirtualKeyModifiers.Shift) != 0)
        {
            ApplyDeviceRangeSelection(card);
            return;
        }

        ClearDeviceMultiSelection();
        _multiSelectedDeviceBookKeys.Add(card.Book.RelativePath);
        card.IsMultiSelected = true;
        _deviceSelectAnchor = card;
        UpdateDeviceMultiSelectBar();
    }

    private void ToggleDeviceMultiSelection(KindleBookCardViewModel card)
    {
        var key = card.Book.RelativePath;
        if (_multiSelectedDeviceBookKeys.Contains(key))
        {
            _multiSelectedDeviceBookKeys.Remove(key);
            card.IsMultiSelected = false;
        }
        else
        {
            _multiSelectedDeviceBookKeys.Add(key);
            card.IsMultiSelected = true;
        }

        _deviceSelectAnchor = card;
        UpdateDeviceMultiSelectBar();
    }

    private void ApplyDeviceRangeSelection(KindleBookCardViewModel clicked)
    {
        var books = DeviceBooks.ToList();
        var anchorIndex = _deviceSelectAnchor is null
            ? -1
            : books.FindIndex(candidate => ReferenceEquals(candidate, _deviceSelectAnchor));
        var clickedIndex = books.FindIndex(candidate => ReferenceEquals(candidate, clicked));
        if (clickedIndex < 0) return;

        _multiSelectedDeviceBookKeys.Clear();
        foreach (var card in DeviceBooks) card.IsMultiSelected = false;

        if (anchorIndex < 0)
        {
            _multiSelectedDeviceBookKeys.Add(clicked.Book.RelativePath);
            clicked.IsMultiSelected = true;
        }
        else
        {
            var start = Math.Min(anchorIndex, clickedIndex);
            var end = Math.Max(anchorIndex, clickedIndex);
            for (var index = start; index <= end; index++)
            {
                var card = books[index];
                _multiSelectedDeviceBookKeys.Add(card.Book.RelativePath);
                card.IsMultiSelected = true;
            }
        }

        _deviceSelectAnchor = clicked;
        UpdateDeviceMultiSelectBar();
    }

    private void ClearDeviceMultiSelection()
    {
        if (_multiSelectedDeviceBookKeys.Count == 0 && !DeviceBooks.Any(card => card.IsMultiSelected))
        {
            DeviceBookMultiSelectBar.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var card in DeviceBooks) card.IsMultiSelected = false;
        _multiSelectedDeviceBookKeys.Clear();
        _deviceSelectAnchor = null;
        DeviceBookMultiSelectBar.Visibility = Visibility.Collapsed;
    }

    private void UpdateDeviceMultiSelectBar()
    {
        if (_multiSelectedDeviceBookKeys.Count == 0)
        {
            DeviceBookMultiSelectBar.Visibility = Visibility.Collapsed;
            return;
        }

        DeviceBookMultiSelectCountText.Text = $"已选择 {_multiSelectedDeviceBookKeys.Count} 本";
        DeviceBookMultiSelectBar.Visibility = Visibility.Visible;
    }

    private List<KindleBookCardViewModel> GetMultiSelectedDeviceBooks() =>
        DeviceBooks
            .Where(card => _multiSelectedDeviceBookKeys.Contains(card.Book.RelativePath))
            .ToList();

    private void ClearDeviceMultiSelectionButton_Click(object sender, RoutedEventArgs e) => ClearDeviceMultiSelection();

    private void ClearDeviceMultiSelectionMenuItem_Click(object sender, RoutedEventArgs e) => ClearDeviceMultiSelection();

    private async void ExportSelectedDeviceBooksButton_Click(object sender, RoutedEventArgs e) =>
        await ExportSelectedDeviceBooksCoreAsync();

    private async void ExportSelectedDeviceBooksMenuItem_Click(object sender, RoutedEventArgs e) =>
        await ExportSelectedDeviceBooksCoreAsync();

    private async Task ExportSelectedDeviceBooksCoreAsync()
    {
        var cards = GetMultiSelectedDeviceBooks();
        if (cards.Count == 0) return;
        await TrackDeviceOperationAsync(() => ExportDeviceBooksToLibraryAsync(cards.Select(card => card.Book).ToArray()));
    }

    private async void DeleteSelectedDeviceBooksButton_Click(object sender, RoutedEventArgs e) =>
        await DeleteSelectedDeviceBooksCoreAsync();

    private async void DeleteSelectedDeviceBooksMenuItem_Click(object sender, RoutedEventArgs e) =>
        await DeleteSelectedDeviceBooksCoreAsync();

    private async Task DeleteSelectedDeviceBooksCoreAsync()
    {
        var cards = GetMultiSelectedDeviceBooks();
        if (cards.Count == 0) return;
        await TrackDeviceOperationAsync(() => DeleteDeviceBooksAsync(cards));
    }

    private void DeviceBookCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: KindleBookCardViewModel card } element) return;

        if (_multiSelectedDeviceBookKeys.Contains(card.Book.RelativePath) && _multiSelectedDeviceBookKeys.Count > 0)
        {
            var selected = GetMultiSelectedDeviceBooks();
            var exportItem = new MenuFlyoutItem { Text = $"导出到电脑书库 ({selected.Count})" };
            exportItem.Click += ExportSelectedDeviceBooksMenuItem_Click;
            var deleteItem = new MenuFlyoutItem { Text = $"从 Kindle 删除 ({selected.Count})" };
            deleteItem.Click += DeleteSelectedDeviceBooksMenuItem_Click;
            var clearItem = new MenuFlyoutItem { Text = "取消选择" };
            clearItem.Click += ClearDeviceMultiSelectionMenuItem_Click;

            var flyout = new MenuFlyout();
            if (Application.Current.Resources["MonochromeMenuFlyoutPresenterStyle"] is Style presenterStyle)
                flyout.MenuFlyoutPresenterStyle = presenterStyle;
            flyout.Items.Add(exportItem);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(deleteItem);
            flyout.Items.Add(clearItem);
            flyout.ShowAt(element, e.GetPosition(element));
            e.Handled = true;
            return;
        }

        // Right-clicking an unselected card collapses the current multi selection.
        if (_multiSelectedDeviceBookKeys.Count > 0) ClearDeviceMultiSelection();

        var singleExportItem = new MenuFlyoutItem
        {
            Text = "导出到电脑书库",
            Tag = card
        };
        singleExportItem.Click += ExportDeviceBookToLibraryMenuItem_Click;

        var singleDeleteItem = new MenuFlyoutItem
        {
            Text = "从 Kindle 删除",
            Tag = card
        };
        singleDeleteItem.Click += DeleteDeviceBookMenuItem_Click;

        var singleFlyout = new MenuFlyout();
        if (Application.Current.Resources["MonochromeMenuFlyoutPresenterStyle"] is Style presenterStyle2)
            singleFlyout.MenuFlyoutPresenterStyle = presenterStyle2;
        singleFlyout.Items.Add(singleExportItem);
        singleFlyout.Items.Add(new MenuFlyoutSeparator());
        singleFlyout.Items.Add(singleDeleteItem);
        singleFlyout.ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }

    private async void ExportDeviceBookToLibraryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: KindleBookCardViewModel card }) return;
        await TrackDeviceOperationAsync(() => ExportDeviceBooksToLibraryAsync([card.Book]));
    }

    private async void DeleteDeviceBookMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: KindleBookCardViewModel card }) return;
        await TrackDeviceOperationAsync(() => DeleteDeviceBooksAsync([card]));
    }

    private async Task DeleteDeviceBooksAsync(IReadOnlyList<KindleBookCardViewModel> cards)
    {
        if (_isTransferring)
        {
            ShowTransferToast("从 Kindle 删除", "已有传输任务正在进行中。", autoHide: true);
            return;
        }
        if (_devices.Count == 0)
        {
            ShowTransferToast("从 Kindle 删除", "Kindle 已断开连接。", autoHide: true);
            return;
        }
        if (cards.Count == 0) return;

        var device = _devices[0];
        var titleLines = string.Join("\n", cards.Take(3).Select(card => $"《{card.Book.Title}》"));
        if (cards.Count > 3) titleLines += $"\n…等 {cards.Count} 本";
        if (!await ShowDevicePromptAsync(
                $"从 Kindle 删除：{cards.Count} 本书",
                $"将从 {device.Name} 永久删除以下书籍：\n\n{titleLines}\n\n此操作不会删除电脑书库中的副本，且无法撤销。",
                "删除",
                "取消")) return;

        _isTransferring = true;
        _transferCancellation = new CancellationTokenSource();
        TaskProgress.IsIndeterminate = true;
        ShowTaskProgressPopup();
        TaskStatusText.Text = "正在从 Kindle 删除…";
        ShowTransferToast("从 Kindle 删除", $"正在删除 {cards.Count} 本书…");
        var succeeded = 0;
        var failed = 0;
        try
        {
            foreach (var card in cards.ToArray())
            {
                try
                {
                    TaskStatusText.Text = $"正在从 Kindle 删除《{card.Book.Title}》…";
                    await _kindle.RemoveBookAsync(device, card.Book, _transferCancellation.Token);
                    DeviceBooks.Remove(card);
                    succeeded++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failed++;
                    ShowTransferToast("从 Kindle 删除", $"《{card.Book.Title}》删除失败：{exception.Message}", autoHide: true);
                }
            }

            ReconcileLibraryPresence();
            DeviceBookCountText.Text = DeviceBooks.Count.ToString();
            _scannedDeviceId = null;
            ClearDeviceMultiSelection();
            TaskStatusText.Text = failed == 0
                ? "已从 Kindle 删除"
                : $"删除完成：成功 {succeeded} 本，失败 {failed} 本";
            ShowTransferToast(
                "从 Kindle 删除",
                failed == 0
                    ? $"已删除 {succeeded} 本书。"
                    : $"删除完成：成功 {succeeded} 本，失败 {failed} 本。",
                progress: 100,
                autoHide: true);
        }
        catch (OperationCanceledException)
        {
            TaskStatusText.Text = "删除已取消";
            ShowTransferToast("从 Kindle 删除", "删除已取消。", autoHide: true);
        }
        catch (Exception exception)
        {
            TaskStatusText.Text = "删除失败";
            ShowTransferToast("从 Kindle 删除", $"删除失败：{exception.Message}", autoHide: true);
            await ShowMessageAsync("无法从 Kindle 删除", exception.Message);
        }
        finally
        {
            _isTransferring = false;
            _transferCancellation.Dispose();
            _transferCancellation = null;
            TaskProgress.IsIndeterminate = false;
            HideTaskProgressPopup();
        }
    }

    private async Task ExportDeviceBooksToLibraryAsync(IReadOnlyList<KindleBook> books)
    {
        if (_isTransferring)
        {
            ShowTransferToast("导出到电脑书库", "已有传输任务正在进行中。", autoHide: true);
            return;
        }
        if (_devices.Count == 0)
        {
            ShowTransferToast("导出到电脑书库", "Kindle 已断开连接。", autoHide: true);
            return;
        }

        var pending = books.Where(book =>
        {
            var format = BookFormatConversionPolicy.Normalize(book.Format);
            return ImportableExtensions.Contains('.' + format) || format == "kfx";
        }).ToArray();
        if (pending.Length == 0)
        {
            await ShowMessageAsync("无法导出", "所选书籍的格式电脑书库暂不支持。");
            return;
        }

        var device = _devices[0];
        var titleLines = string.Join("\n", pending.Take(3).Select(book => $"《{book.Title}》"));
        if (pending.Length > 3) titleLines += $"\n…等 {pending.Length} 本";
        if (!await ShowDevicePromptAsync(
                $"导出到电脑书库：{pending.Length} 本书",
                $"将从 {device.Name} 导出以下书籍并导入电脑书库：\n\n{titleLines}",
                "导出",
                "取消")) return;

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "Kkindle", "device-export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        _isTransferring = true;
        _transferCancellation = new CancellationTokenSource();
        TaskProgress.Value = 0;
        ShowTaskProgressPopup();
        ShowTransferToast("导出到电脑书库", $"正在从 Kindle 读取 {pending.Length} 本书…", progress: 0);
        var acceptProgressUpdates = true;
        var succeeded = 0;
        var failed = 0;

        try
        {
            var transferProgress = new Progress<TransferProgress>(value =>
            {
                if (!acceptProgressUpdates) return;
                TaskProgress.Value = value.Percentage;
                TaskStatusText.Text = value.Message;
                ShowTransferToast("导出到电脑书库", value.Message, progress: value.Percentage);
            });
            for (var index = 0; index < pending.Length; index++)
            {
                var book = pending[index];
                var format = BookFormatConversionPolicy.Normalize(book.Format);
                var bookDirectory = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(bookDirectory);
                try
                {
                    TaskStatusText.Text = $"正在从 Kindle 读取《{book.Title}》（{index + 1}/{pending.Length}）…";
                    ShowTransferToast(
                        "导出到电脑书库",
                        $"正在从 Kindle 读取《{book.Title}》（{index + 1}/{pending.Length}）…",
                        progress: index * 100 / pending.Length);
                    var localSource = await _kindle.ExportBookAsync(
                        device,
                        book,
                        bookDirectory,
                        transferProgress,
                        _transferCancellation.Token);

                    var importPath = localSource;
                    if (format == "kfx")
                    {
                        importPath = Path.Combine(bookDirectory, Path.GetFileNameWithoutExtension(localSource) + ".epub");
                        var conversionProgress = new Progress<FormatConversionProgress>(value =>
                        {
                            if (!acceptProgressUpdates) return;
                            TaskProgress.Value = value.Percentage;
                            TaskStatusText.Text = value.Message;
                            ShowTransferToast("KFX 转换为 EPUB", value.Message, progress: value.Percentage);
                        });
                        ShowTransferToast("KFX 转换为 EPUB", "正在准备 Calibre KFX Input 插件…", progress: 0);
                        await _formatConverter.ConvertAsync(localSource, importPath, conversionProgress, _transferCancellation.Token);
                    }

                    TaskStatusText.Text = "正在导入电脑书库…";
                    var importProgress = new Progress<TransferProgress>(value =>
                    {
                        if (!acceptProgressUpdates) return;
                        TaskProgress.Value = value.Percentage;
                        TaskStatusText.Text = value.Message;
                        ShowTransferToast("导出到电脑书库", value.Message, progress: value.Percentage);
                    });
                    var result = await ViewModel.ImportAsync([importPath], importProgress, _transferCancellation.Token);
                    var failure = result.Items.FirstOrDefault(item => !item.Succeeded);
                    if (failure is not null) throw new IOException(failure.Message ?? "导入电脑书库失败。");
                    // Refresh the library presentation before the automatic format
                    // generation so the imported book is visible (and the empty
                    // state is hidden) while the conversion is still running.
                    UpdateLibraryPresentationState();
                    var automaticFormats = await AutoGenerateReaderFormatsForImportsAsync(result, _transferCancellation.Token);
                    UpdateLibraryPresentationState();
                    succeeded++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failed++;
                    ShowTransferToast("导出到电脑书库", $"《{book.Title}》导出失败：{exception.Message}", autoHide: true);
                }
            }

            acceptProgressUpdates = false;
            TaskStatusText.Text = succeeded > 0 ? "已导入电脑书库" : "导出失败";
            var completionMessage = failed == 0
                ? $"已从 {device.Name} 导出 {succeeded} 本书并导入电脑书库。"
                : $"导出完成：成功 {succeeded} 本，失败 {failed} 本。";
            ShowTransferToast("导出到电脑书库", completionMessage, progress: 100, autoHide: true);
        }
        catch (OperationCanceledException)
        {
            acceptProgressUpdates = false;
            TaskStatusText.Text = "导出已取消";
            ShowTransferToast("导出到电脑书库", "导出已取消，临时文件已清理。", autoHide: true);
        }
        catch (Exception exception)
        {
            acceptProgressUpdates = false;
            TaskStatusText.Text = "导出失败";
            ShowTransferToast("导出到电脑书库", $"导出失败：{exception.Message}", autoHide: true);
            await ShowMessageAsync("导出到电脑书库失败", exception.Message);
        }
        finally
        {
            _isTransferring = false;
            _transferCancellation.Dispose();
            _transferCancellation = null;
            HideTaskProgressPopup();
            try { Directory.Delete(temporaryDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
