using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Kkindle;

public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _transferToastTimer;

    private void ShowTransferToast(
        string title,
        string message,
        double? progress = null,
        bool isIndeterminate = false,
        bool autoHide = false)
    {
        TransferToastTitleText.Text = title;
        TransferToastMessageText.Text = message;
        TransferToastProgress.IsIndeterminate = isIndeterminate;

        if (isIndeterminate)
        {
            TransferToastProgress.Visibility = Visibility.Visible;
        }
        else if (progress.HasValue)
        {
            TransferToastProgress.Value = Math.Clamp(progress.Value, 0, 100);
            TransferToastProgress.Visibility = Visibility.Visible;
        }
        else
        {
            TransferToastProgress.Visibility = Visibility.Collapsed;
        }

        TransferToast.Visibility = Visibility.Visible;
        if (!autoHide)
        {
            _transferToastTimer?.Stop();
            return;
        }

        _transferToastTimer ??= DispatcherQueue.CreateTimer();
        _transferToastTimer.Interval = TimeSpan.FromSeconds(4);
        _transferToastTimer.Tick -= TransferToastTimer_Tick;
        _transferToastTimer.Tick += TransferToastTimer_Tick;
        _transferToastTimer.Start();
    }

    private void TransferToastTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        TransferToast.Visibility = Visibility.Collapsed;
    }

    private void HideTransferToast()
    {
        _transferToastTimer?.Stop();
        TransferToast.Visibility = Visibility.Collapsed;
    }
}
