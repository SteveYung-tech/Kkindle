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
        ShowSettingsPanel(KindleEmailSettingsPane);
    }

    private async void KindleEmailSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(KindleEmailSettingsNavigationButton);
        await ShowKindleEmailSettingsAsync();
    }

    private void KindleEmailSettingsCancelButton_Click(object sender, RoutedEventArgs e)
    {
        HideSettingsPanel();
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
            HideSettingsPanel();
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
        await SendBooksToKindleEmailAsync([book]);
    }

    private async void SendSelectedBookToKindleEmailButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBook is null) return;
        await SendBooksToKindleEmailAsync([_selectedBook]);
    }

    private async Task SendBooksToKindleEmailAsync(IReadOnlyList<Book> books)
    {
        if (_kindleEmailSending) return;
        if (!_appSettings.NetworkEnabled)
        {
            await ShowMessageAsync("网络功能已关闭", "请在应用设置中允许网络功能后再发送 Kindle 邮件。");
            return;
        }

        var pending = new List<(Book Book, string SourcePath)>();
        foreach (var book in books)
        {
            var file = KindleEmailSelectionPolicy.SelectPreferred(book.Files);
            if (file is null) continue;
            var sourcePath = _library.GetAbsoluteFilePath(file);
            if (File.Exists(sourcePath)) pending.Add((book, sourcePath));
        }
        if (pending.Count == 0)
        {
            await ShowMessageAsync("无法发送", "发送到 Kindle 邮箱目前只支持 EPUB 或 PDF 文件。\n请先为所选书籍导入 EPUB 或 PDF 版本。");
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

        var titleLines = string.Join("\n", pending.Take(3).Select(entry => $"《{entry.Book.Title}》"));
        if (pending.Count > 3) titleLines += $"\n…等 {pending.Count} 本";
        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "发送到 Kindle 邮箱",
            Content = $"确定将以下 {pending.Count} 本书发送到 {settings.KindleEmailAddress}？\n\n{titleLines}",
            PrimaryButtonText = "发送",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var cancellation = new CancellationTokenSource();
        _kindleEmailSendCancellation = cancellation;
        _kindleEmailSending = true;
        TaskProgress.IsIndeterminate = true;
        ShowTaskProgressPopup();
        TaskStatusText.Text = "正在发送到 Kindle 邮箱…";
        var succeeded = 0;
        var failed = 0;
        try
        {
            for (var index = 0; index < pending.Count; index++)
            {
                var (entry, sourcePath) = pending[index];
                TaskStatusText.Text = $"正在发送《{entry.Title}》（{index + 1}/{pending.Count}）…";
                try
                {
                    await _kindleEmailSender.SendAsync(
                        settings,
                        sourcePath,
                        $"Send to Kindle: {entry.Title}",
                        cancellation.Token);
                    succeeded++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    failed++;
                }
            }

            TaskStatusText.Text = failed == 0
                ? $"已提交 {succeeded} 本书到 Kindle 邮箱"
                : $"发送完成：成功 {succeeded} 本，失败 {failed} 本";
            await ShowMessageAsync(
                "发送完成",
                failed == 0
                    ? $"已提交 {succeeded} 本书到 {settings.KindleEmailAddress}。Amazon 完成转换后，书籍会出现在 Kindle 或 Kindle 应用中。"
                    : $"已提交 {succeeded} 本，{failed} 本发送失败。请检查邮箱设置后重试。");
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
            HideTaskProgressPopup();
        }
    }
}
