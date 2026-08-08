using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Kkindle.Core;

namespace Kkindle;

public sealed partial class MainWindow
{
    private void BookContextFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout) return;

        var submenus = flyout.Items
            .OfType<MenuFlyoutSubItem>()
            .ToArray();
        var book = submenus
            .Select(item => item.Tag)
            .OfType<Book>()
            .FirstOrDefault();
        if (book is null) return;

        foreach (var submenu in submenus)
        {
            var formats = string.Equals(submenu.Text, "删除格式", StringComparison.Ordinal)
                ? book.Files.Select(file => file.Format.Trim())
                : ReaderBookSelectionPolicy.GetSupportedFiles(book.Files).Select(file => file.Format.Trim());
            var supportedFormats = formats.ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var item in submenu.Items.OfType<MenuFlyoutItem>())
            {
                var format = item.Text?.Trim() ?? string.Empty;
                var supported = supportedFormats.Contains(format);
                item.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
                item.IsEnabled = supported;
            }

            submenu.IsEnabled = supportedFormats.Count > 0;
        }
    }

    private async void OpenBookFormatMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: Book book } item) return;

        var targetFormat = item.Text?.Trim();
        if (string.IsNullOrWhiteSpace(targetFormat)) return;

        var file = ReaderBookSelectionPolicy
            .GetSupportedFiles(book.Files)
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Format.Trim(), targetFormat, StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            await ShowMessageAsync("无法打开书籍", "所选格式文件不存在或已不再支持。");
            return;
        }

        await OpenBookAsync(book, file);
    }

    private async void DeleteBookFormatMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: Book book } item) return;

        var targetFormat = item.Text?.Trim();
        if (string.IsNullOrWhiteSpace(targetFormat)) return;

        var file = book.Files.FirstOrDefault(candidate =>
            string.Equals(candidate.Format.Trim(), targetFormat, StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            await ShowMessageAsync("无法删除格式", "所选格式文件不存在或已被删除。");
            return;
        }

        if (_readerBookFile?.Id == file.Id)
        {
            await ShowMessageAsync("无法删除格式", "当前正在阅读这个文件，请先关闭阅读器。");
            return;
        }

        var fileName = Path.GetFileName(file.RelativePath);
        var lastFormat = book.Files.Count <= 1;
        var message = lastFormat
            ? "这是这本书的最后一个格式，删除后会同时移除书籍记录。"
            : "只删除这个格式，不会影响同一本书的其他格式。";
        if (!await ShowDevicePromptAsync(
                "删除 " + targetFormat.ToUpperInvariant() + "？",
                "将删除“" + fileName + "”。\n\n" + message,
                "删除",
                "取消")) return;

        try
        {
            await _library.DeleteFileAsync(book.Id, file.Id);
            TaskStatusText.Text = "已删除 " + targetFormat.ToUpperInvariant() + " 文件";
            await RefreshLibraryAsync();

            var refreshed = ViewModel.Books
                .Select(card => card.Book)
                .FirstOrDefault(candidate => candidate.Id == book.Id);
            if (_selectedBook?.Id == book.Id)
            {
                if (refreshed is null)
                {
                    _selectedBook = null;
                    CloseDetails();
                }
                else
                {
                    SelectBook(refreshed);
                }
            }
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法删除格式", exception.Message);
        }
    }
}
