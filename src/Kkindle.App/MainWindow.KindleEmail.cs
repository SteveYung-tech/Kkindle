using System.Globalization;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kkindle;

public sealed partial class MainWindow
{
    private readonly KindleEmailSettingsStore _kindleEmailSettingsStore;
    private readonly KindleEmailSender _kindleEmailSender;
    private KindleEmailSettings _kindleEmailSettings = new();
    private CancellationTokenSource? _kindleEmailSendCancellation;
    private bool _kindleEmailSending;

    private async Task ShowKindleEmailSettingsAsync(string? status = null)
    {
        _kindleEmailSettings = await _kindleEmailSettingsStore.LoadAsync();
        KindleEmailRecipientBox.Text = _kindleEmailSettings.KindleEmailAddress;
        KindleEmailSenderBox.Text = _kindleEmailSettings.SenderEmailAddress;
        KindleEmailSmtpHostBox.Text = _kindleEmailSettings.SmtpHost;
        KindleEmailSmtpPortBox.Text = _kindleEmailSettings.SmtpPort.ToString(CultureInfo.InvariantCulture);
        KindleEmailUsernameBox.Text = _kindleEmailSettings.SmtpUsername;
        KindleEmailPasswordBox.Password = _kindleEmailSettings.SmtpPassword;
        KindleEmailSslCheck.IsChecked = _kindleEmailSettings.EnableSsl;
        KindleEmailSettingsStatusText.Text = status ?? string.Empty;
        KindleEmailSettingsOverlay.Visibility = Visibility.Visible;
    }

    private async void KindleEmailSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(KindleEmailSettingsNavigationButton);
        await ShowKindleEmailSettingsAsync();
    }

    private void KindleEmailSettingsCancelButton_Click(object sender, RoutedEventArgs e)
    {
        KindleEmailSettingsOverlay.Visibility = Visibility.Collapsed;
        KindleEmailSettingsStatusText.Text = string.Empty;
    }

    private async void KindleEmailSettingsSaveButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = ReadKindleEmailSettingsFromControls();
        var validationError = settings.Validate();
        if (validationError is not null)
        {
            KindleEmailSettingsStatusText.Text = validationError;
            return;
        }

        KindleEmailSettingsStatusText.Text = "正在安全保存设置…";
        try
        {
            await _kindleEmailSettingsStore.SaveAsync(settings);
            _kindleEmailSettings = KindleEmailSettings.Normalize(settings);
            KindleEmailSettingsOverlay.Visibility = Visibility.Collapsed;
            TaskStatusText.Text = "Kindle 邮箱设置已保存";
        }
        catch (Exception exception)
        {
            KindleEmailSettingsStatusText.Text = $"保存失败：{exception.Message}";
        }
    }

    private KindleEmailSettings ReadKindleEmailSettingsFromControls()
    {
        _ = int.TryParse(
            KindleEmailSmtpPortBox.Text,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var port);
        return new KindleEmailSettings
        {
            KindleEmailAddress = KindleEmailRecipientBox.Text,
            SenderEmailAddress = KindleEmailSenderBox.Text,
            SmtpHost = KindleEmailSmtpHostBox.Text,
            SmtpPort = port,
            SmtpUsername = KindleEmailUsernameBox.Text,
            SmtpPassword = KindleEmailPasswordBox.Password,
            EnableSsl = KindleEmailSslCheck.IsChecked == true
        };
    }

    private async void SendBookToKindleEmailMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: Book book }) return;
        await SendBookToKindleEmailAsync(book);
    }

    private async void SendSelectedBookToKindleEmailButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBook is null) return;
        await SendBookToKindleEmailAsync(_selectedBook);
    }

    private async Task SendBookToKindleEmailAsync(Book book)
    {
        if (_kindleEmailSending) return;

        var file = KindleEmailSelectionPolicy.SelectPreferred(book.Files);
        if (file is null)
        {
            await ShowMessageAsync("无法发送", "发送到 Kindle 邮箱目前只支持 EPUB 或 PDF 文件。\n请先为本书导入 EPUB 或 PDF 版本。");
            return;
        }

        var sourcePath = _library.GetAbsoluteFilePath(file);
        if (!File.Exists(sourcePath))
        {
            await ShowMessageAsync("无法发送", "找不到这本书的本地文件。请先刷新书库。");
            return;
        }

        var settings = await _kindleEmailSettingsStore.LoadAsync();
        _kindleEmailSettings = settings;
        var validationError = settings.Validate();
        if (validationError is not null)
        {
            await ShowKindleEmailSettingsAsync($"请先完成设置：{validationError}");
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "发送到 Kindle 邮箱",
            Content = $"确定将《{book.Title}》发送到 {settings.KindleEmailAddress}？",
            PrimaryButtonText = "发送",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var cancellation = new CancellationTokenSource();
        _kindleEmailSendCancellation = cancellation;
        _kindleEmailSending = true;
        TaskProgress.IsIndeterminate = true;
        TaskProgress.Visibility = Visibility.Visible;
        TaskStatusText.Text = $"正在发送《{book.Title}》到 Kindle 邮箱…";
        try
        {
            await _kindleEmailSender.SendAsync(
                settings,
                sourcePath,
                $"Send to Kindle: {book.Title}",
                cancellation.Token);
            TaskStatusText.Text = $"《{book.Title}》已提交到 Kindle 邮箱";
            await ShowMessageAsync("发送成功", "邮件已发送。Amazon 完成转换后，书籍会出现在 Kindle 或 Kindle 应用中。");
        }
        catch (OperationCanceledException)
        {
            TaskStatusText.Text = "Kindle 邮箱发送已取消";
        }
        catch (Exception exception)
        {
            TaskStatusText.Text = "Kindle 邮箱发送失败";
            await ShowMessageAsync("发送失败", exception.Message);
        }
        finally
        {
            _kindleEmailSending = false;
            if (ReferenceEquals(_kindleEmailSendCancellation, cancellation))
                _kindleEmailSendCancellation = null;
            cancellation.Dispose();
            TaskProgress.IsIndeterminate = false;
            TaskProgress.Visibility = Visibility.Collapsed;
        }
    }
}
