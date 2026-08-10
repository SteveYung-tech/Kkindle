using Kkindle.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kkindle;

public sealed partial class MainWindow
{
    private async void SendBookToKindleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: Book book })
            await SendBookToKindleFromContextAsync(book);
    }

    private async Task SendBookToKindleFromContextAsync(Book book)
    {
        if (_isTransferring)
        {
            ShowTransferToast("发送到 Kindle 设备", "已有发送任务正在进行中。", autoHide: true);
            return;
        }

        if (book.Files.Count == 0)
        {
            ShowTransferToast("发送到 Kindle 设备", "这本书没有可发送的文件。", autoHide: true);
            return;
        }

        await RefreshDevicesAsync();
        if (_devices.Count == 0)
        {
            ShowTransferToast("发送到 Kindle 设备", "未检测到 Kindle，请连接并解锁设备。", autoHide: true);
            return;
        }

        var device = _devices[0];
        if (!await ShowDevicePromptAsync(
                "发送到 Kindle？",
                $"将《{book.Title}》发送到 {device.Name}。\n\n如果设备上存在同名文件，Kkindle 会自动使用带序号的新文件名，不会覆盖原文件。",
                "发送",
                "取消")) return;

        _isTransferring = true;
        _transferCancellation = new CancellationTokenSource();
        TaskProgress.Value = 0;
        TaskProgress.IsIndeterminate = false;
        TaskProgress.Visibility = Visibility.Visible;
        ShowTransferToast("发送到 Kindle 设备", $"正在发送《{book.Title}》…", progress: 0);
        var acceptProgressUpdates = true;

        try
        {
            var progress = new Progress<TransferProgress>(value =>
            {
                if (!acceptProgressUpdates) return;
                TaskProgress.Value = value.Percentage;
                TaskStatusText.Text = value.Message;
                ShowTransferToast("发送到 Kindle 设备", value.Message, progress: value.Percentage);
            });
            using var prepared = await PrepareKindleTransferAsync(
                book,
                progress,
                _transferCancellation.Token);
            await _kindle.SendBookAsync(
                device,
                prepared.File,
                prepared.SourcePath,
                progress,
                _transferCancellation.Token);
            acceptProgressUpdates = false;
            TaskStatusText.Text = "已发送到 Kindle";
            ShowTransferToast("发送到 Kindle 设备", $"《{book.Title}》已发送完成。", progress: 100, autoHide: true);
            _scannedDeviceId = null;
            await RefreshDevicesAsync();
        }
        catch (OperationCanceledException)
        {
            acceptProgressUpdates = false;
            TaskStatusText.Text = "发送已中断";
            ShowTransferToast("发送到 Kindle 设备", "发送已中断，未完成的临时文件已清理。", autoHide: true);
        }
        catch (Exception exception)
        {
            acceptProgressUpdates = false;
            TaskStatusText.Text = "发送失败";
            ShowTransferToast("发送到 Kindle 设备", $"发送失败：{exception.Message}", autoHide: true);
        }
        finally
        {
            _isTransferring = false;
            _transferCancellation.Dispose();
            _transferCancellation = null;
            TaskProgress.Visibility = Visibility.Collapsed;
        }
    }
}
