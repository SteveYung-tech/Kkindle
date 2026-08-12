using Kkindle.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kkindle;

public sealed partial class MainWindow
{
    private async void SendBookToKindleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: Book book })
            await TrackDeviceOperationAsync(() => SendBooksToKindleFromContextAsync([book]));
    }

    private async Task SendBooksToKindleFromContextAsync(IReadOnlyList<Book> books)
    {
        if (_isTransferring)
        {
            ShowTransferToast("发送到 Kindle 设备", "已有发送任务正在进行中。", autoHide: true);
            return;
        }

        var pending = books.Where(book => book.Files.Count > 0).ToArray();
        if (pending.Length == 0)
        {
            ShowTransferToast("发送到 Kindle 设备", "所选书籍没有可发送的文件。", autoHide: true);
            return;
        }

        await RefreshDevicesAsync();
        if (_devices.Count == 0)
        {
            ShowTransferToast("发送到 Kindle 设备", "未检测到 Kindle，请连接并解锁设备。", autoHide: true);
            return;
        }

        var device = _devices[0];
        var titleLines = string.Join("\n", pending.Take(3).Select(book => $"《{book.Title}》"));
        if (pending.Length > 3) titleLines += $"\n…等 {pending.Length} 本";
        if (!await ShowDevicePromptAsync(
                $"发送到 Kindle：{pending.Length} 本书",
                $"将以下书籍发送到 {device.Name}：\n\n{titleLines}\n\n如果设备上存在同名文件，Kkindle 会自动使用带序号的新文件名，不会覆盖原文件。",
                "发送",
                "取消")) return;

        _isTransferring = true;
        _transferCancellation = new CancellationTokenSource();
        TaskProgress.Value = 0;
        TaskProgress.IsIndeterminate = false;
        ShowTaskProgressPopup();
        ShowTransferToast("发送到 Kindle 设备", $"正在发送 {pending.Length} 本书…", progress: 0);
        var acceptProgressUpdates = true;
        var hideCompletionToastWithTaskPopup = false;
        var succeeded = 0;
        var failed = 0;

        try
        {
            var progress = new Progress<TransferProgress>(value =>
            {
                if (!acceptProgressUpdates) return;
                TaskProgress.Value = value.Percentage;
                TaskStatusText.Text = value.Message;
                ShowTransferToast("发送到 Kindle 设备", value.Message, progress: value.Percentage);
            });
            for (var index = 0; index < pending.Length; index++)
            {
                var book = pending[index];
                TaskStatusText.Text = $"正在发送《{book.Title}》（{index + 1}/{pending.Length}）…";
                ShowTransferToast(
                    "发送到 Kindle 设备",
                    $"正在发送《{book.Title}》（{index + 1}/{pending.Length}）…",
                    progress: index * 100 / pending.Length);
                try
                {
                    using var prepared = await PrepareKindleTransferAsync(book, progress, _transferCancellation.Token);
                    await _kindle.SendBookAsync(
                        device,
                        prepared.File,
                        prepared.SourcePath,
                        progress,
                        _transferCancellation.Token);
                    succeeded++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failed++;
                    ShowTransferToast("发送到 Kindle 设备", $"《{book.Title}》发送失败：{exception.Message}", autoHide: true);
                }
            }

            acceptProgressUpdates = false;
            TaskStatusText.Text = failed == 0
                ? $"已发送 {succeeded} 本书"
                : $"发送完成：成功 {succeeded} 本，失败 {failed} 本";
            ShowTransferToast(
                "发送到 Kindle 设备",
                failed == 0
                    ? $"已发送 {succeeded} 本书到 {device.Name}。"
                    : $"发送完成：成功 {succeeded} 本，失败 {failed} 本。",
                progress: 100);
            _scannedDeviceId = null;
            await RefreshDevicesAsync();
            // Match the import task popup: hide the completion toast at the same
            // moment the task popup collapses (in the finally block below).
            hideCompletionToastWithTaskPopup = true;
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
            HideTaskProgressPopup();
            if (hideCompletionToastWithTaskPopup)
                HideTransferToast();
        }
    }
}
