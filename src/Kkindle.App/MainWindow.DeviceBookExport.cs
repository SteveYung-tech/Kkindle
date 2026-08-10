using Kkindle.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Kkindle;

public sealed partial class MainWindow
{
    private void DeviceBookCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: KindleBookCardViewModel card } element) return;

        var exportItem = new MenuFlyoutItem
        {
            Text = "导出到电脑书库",
            Tag = card
        };
        exportItem.Click += ExportDeviceBookToLibraryMenuItem_Click;

        var flyout = new MenuFlyout();
        if (Application.Current.Resources["MonochromeMenuFlyoutPresenterStyle"] is Style presenterStyle)
            flyout.MenuFlyoutPresenterStyle = presenterStyle;
        flyout.Items.Add(exportItem);
        flyout.ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }

    private async void ExportDeviceBookToLibraryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: KindleBookCardViewModel card }) return;
        await ExportDeviceBookToLibraryAsync(card.Book);
    }

    private async Task ExportDeviceBookToLibraryAsync(KindleBook book)
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

        var format = BookFormatConversionPolicy.Normalize(book.Format);
        if (!ImportableExtensions.Contains('.' + format) && format != "kfx")
        {
            await ShowMessageAsync("无法导出", $"电脑书库暂不支持 {format.ToUpperInvariant()} 格式。");
            return;
        }

        var device = _devices[0];
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "Kkindle", "device-export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        _isTransferring = true;
        _transferCancellation = new CancellationTokenSource();
        TaskProgress.Value = 0;
        TaskProgress.Visibility = Visibility.Visible;
        ShowTransferToast("导出到电脑书库", $"正在从 Kindle 读取《{book.Title}》…", progress: 0);
        var acceptProgressUpdates = true;

        try
        {
            var transferProgress = new Progress<TransferProgress>(value =>
            {
                if (!acceptProgressUpdates) return;
                TaskProgress.Value = value.Percentage;
                TaskStatusText.Text = value.Message;
                ShowTransferToast("导出到电脑书库", value.Message, progress: value.Percentage);
            });
            var localSource = await _kindle.ExportBookAsync(
                device,
                book,
                temporaryDirectory,
                transferProgress,
                _transferCancellation.Token);

            var importPath = localSource;
            if (format == "kfx")
            {
                importPath = Path.Combine(temporaryDirectory, Path.GetFileNameWithoutExtension(localSource) + ".epub");
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
            UpdateLibraryPresentationState();

            acceptProgressUpdates = false;
            TaskStatusText.Text = "已导入电脑书库";
            ShowTransferToast(
                "导出到电脑书库",
                format == "kfx" ? $"《{book.Title}》已转换为 EPUB 并导入。" : $"《{book.Title}》已导入。",
                progress: 100,
                autoHide: true);
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
            TaskProgress.Visibility = Visibility.Collapsed;
            try { Directory.Delete(temporaryDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
