using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace Kkindle;

public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _transferToastTimer;
    private readonly Dictionary<FrameworkElement, Storyboard> _popupFadeStoryboards = [];
    private readonly Dictionary<FrameworkElement, int> _popupShowGenerations = [];

    // Shows a bottom-right popup after stopping any in-flight fade and restoring
    // full opacity, so a new task never appears semi-transparent while the
    // previous popup was still fading out.
    private void ShowBottomPopup(FrameworkElement popup)
    {
        if (_popupFadeStoryboards.TryGetValue(popup, out var active))
        {
            active.Stop();
            _popupFadeStoryboards.Remove(popup);
        }
        _popupShowGenerations[popup] = (_popupShowGenerations.GetValueOrDefault(popup) + 1) & int.MaxValue;
        popup.Opacity = 1;
    }

    // Keeps the popup fully visible for 2 seconds after the task completes,
    // then fades it out. A re-shown popup (new task) bumps the generation, so a
    // stale delayed hide never closes a popup that was opened again meanwhile.
    private async Task DelayThenFadeOut(FrameworkElement popup, Action hide)
    {
        var generation = _popupShowGenerations.GetValueOrDefault(popup);
        try { await Task.Delay(TimeSpan.FromSeconds(2)); }
        catch { return; }
        if (generation != _popupShowGenerations.GetValueOrDefault(popup)) return;
        FadeOutAndHide(popup, hide);
    }

    // Fades the popup out over ~220 ms instead of collapsing it abruptly, then
    // runs the hide action (usually setting the popup visibility to collapsed).
    private void FadeOutAndHide(FrameworkElement popup, Action hide)
    {
        if (popup.Visibility != Visibility.Visible)
        {
            hide();
            return;
        }

        if (_popupFadeStoryboards.TryGetValue(popup, out var active))
        {
            active.Stop();
            _popupFadeStoryboards.Remove(popup);
        }
        popup.Opacity = 1;

        var storyboard = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(fade, popup);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);
        _popupFadeStoryboards[popup] = storyboard;
        storyboard.Completed += (_, _) =>
        {
            popup.Opacity = 1;
            _popupFadeStoryboards.Remove(popup);
            hide();
        };
        storyboard.Begin();
    }

    private void ShowTaskProgressPopup()
    {
        ShowBottomPopup(TaskProgressPopup);
        TaskProgress.Visibility = Visibility.Visible;
    }

    private void HideTaskProgressPopup() =>
        _ = DelayThenFadeOut(TaskProgressPopup, () => TaskProgress.Visibility = Visibility.Collapsed);

    private void ShowTransferToast(
        string title,
        string message,
        double? progress = null,
        bool isIndeterminate = false,
        bool autoHide = false)
    {
        // The transfer toast is the progress surface for Kindle operations. Keep the
        // generic task panel hidden so the two bottom-right overlays never stack.
        TaskProgress.Visibility = Visibility.Collapsed;
        ShowBottomPopup(TransferToast);
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
        _transferToastTimer.Interval = TimeSpan.FromSeconds(2);
        _transferToastTimer.Tick -= TransferToastTimer_Tick;
        _transferToastTimer.Tick += TransferToastTimer_Tick;
        _transferToastTimer.Start();
    }

    private void TransferToastTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        HideTransferToast();
    }

    private void HideTransferToast()
    {
        _transferToastTimer?.Stop();
        _ = DelayThenFadeOut(TransferToast, () => TransferToast.Visibility = Visibility.Collapsed);
    }
}
