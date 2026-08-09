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
            var isDeleteSubmenu = string.Equals(submenu.Text, "删除格式", StringComparison.Ordinal);
            var isConvertSubmenu = string.Equals(submenu.Text, "转换为", StringComparison.Ordinal);
            var formats = isDeleteSubmenu
                ? book.Files.Select(file => file.Format.Trim())
                : ReaderBookSelectionPolicy.GetSupportedFiles(book.Files).Select(file => file.Format.Trim());
            var supportedFormats = formats.ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var item in submenu.Items.OfType<MenuFlyoutItem>())
            {
                var format = item.Text?.Trim() ?? string.Empty;
                if (isConvertSubmenu)
                {
                    item.Visibility = Visibility.Visible;
                    item.IsEnabled = true;
                    continue;
                }

                if (isDeleteSubmenu && string.Equals(format, "删除全部格式", StringComparison.Ordinal))
                {
                    item.Visibility = Visibility.Visible;
                    item.IsEnabled = supportedFormats.Count > 0;
                    continue;
                }

                var supported = supportedFormats.Contains(format);
                item.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
                item.IsEnabled = supported;
            }

            submenu.IsEnabled = isConvertSubmenu || supportedFormats.Count > 0;
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

    private async void DeleteAllBookFormatsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: Book book })
            await DeleteEntireBookAsync(book);
    }

    private async Task DeleteEntireBookAsync(Book book)
    {
        if (_readerBook?.Id == book.Id)
        {
            await ShowMessageAsync("无法删除书籍", "当前正在阅读这本书，请先关闭阅读器。");
            return;
        }

        if (!await ShowDevicePromptAsync(
                "删除全部格式？",
                "将从 Kkindle 书库中删除“" + book.Title + "”的所有格式、书籍记录和封面。",
                "删除",
                "取消")) return;

        try
        {
            await _library.DeleteAsync(book.Id);
            if (_selectedBook?.Id == book.Id)
            {
                _selectedBook = null;
                CloseDetails();
            }

            TaskStatusText.Text = "已删除全部格式";
            await RefreshLibraryAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法删除书籍", exception.Message);
        }
    }
}
