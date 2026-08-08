using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Kkindle.Core;

namespace Kkindle;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _bookFormatConversionCancellation;
    private BookCardViewModel? _bookFormatConversionCard;
    private Guid? _bookFormatConversionBookId;
    private FormatConversionProgress _bookFormatConversionLastProgress = new(0, "正在转换…");
    private bool _bookFormatConversionMinimized;
    private bool _bookFormatConversionInProgress;

    private async void ConvertBookToEpubMenuItem_Click(object sender, RoutedEventArgs e) =>
        await ConvertBookFromMenuAsync(sender, "epub");

    private async void ConvertBookToAzw3MenuItem_Click(object sender, RoutedEventArgs e) =>
        await ConvertBookFromMenuAsync(sender, "azw3");

    private async void ConvertBookToPdfMenuItem_Click(object sender, RoutedEventArgs e) =>
        await ConvertBookFromMenuAsync(sender, "pdf");

    private async Task ConvertBookFromMenuAsync(object sender, string targetFormat)
    {
        if (sender is not MenuFlyoutItem { Tag: Book book }) return;
        if (_bookFormatConversionInProgress)
        {
            await ShowMessageAsync("格式转换", "已有一本书正在转换，请稍候。 ");
            return;
        }

        var target = BookFormatConversionPolicy.Normalize(targetFormat);
        if (!BookFormatConversionPolicy.IsConvertibleFormat(target)) return;
        if (book.Files.Any(file =>
                string.Equals(BookFormatConversionPolicy.Normalize(file.Format), target, StringComparison.OrdinalIgnoreCase)))
        {
            await ShowMessageAsync("格式转换", $"这本书已经有 {target.ToUpperInvariant()} 格式。 ");
            return;
        }

        var sourceFile = BookFormatConversionPolicy.SelectSource(book.Files, target);
        if (sourceFile is null)
        {
            await ShowMessageAsync("格式转换", "需要 EPUB、AZW3 或 PDF 作为转换源。 ");
            return;
        }

        string sourcePath;
        try
        {
            sourcePath = _library.GetAbsoluteFilePath(sourceFile);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("格式转换", exception.Message);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _bookFormatConversionCancellation = cancellation;
        _bookFormatConversionInProgress = true;
        _bookFormatConversionBookId = book.Id;
        _bookFormatConversionCard = FindBookCard(book.Id);
        _bookFormatConversionMinimized = false;
        var initialProgress = new FormatConversionProgress(
            0,
            $"准备将 {sourceFile.Format.ToUpperInvariant()} 转换为 {target.ToUpperInvariant()}…");
        _bookFormatConversionLastProgress = initialProgress;
        ShowBookConversionPopup(book, sourceFile.Format, target, initialProgress);

        string? temporaryOutput = null;
        TaskStatusText.Text = initialProgress.Message;
        try
        {
            temporaryOutput = CreateTemporaryFormatPath(book.Title, target);
            var progress = new Progress<FormatConversionProgress>(ApplyBookConversionProgress);
            await _formatConverter.ConvertAsync(
                sourcePath,
                temporaryOutput,
                progress,
                cancellation.Token);

            ApplyBookConversionProgress(new FormatConversionProgress(100, "正在写入书库…"));
            await _library.AddFileToBookAsync(book.Id, temporaryOutput, cancellation.Token);
            await RefreshLibraryAsync();

            var refreshed = ViewModel.Books
                .Select(card => card.Book)
                .FirstOrDefault(item => item.Id == book.Id);
            if (refreshed is not null) SelectBook(refreshed);
            ApplyBookConversionProgress(new FormatConversionProgress(100, "转换完成。"));
            TaskStatusText.Text = $"已为《{book.Title}》添加 {target.ToUpperInvariant()} 格式。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            TaskStatusText.Text = "格式转换已取消。 ";
        }
        catch (Exception exception)
        {
            TaskStatusText.Text = "格式转换失败。 ";
            ApplyBookConversionProgress(new FormatConversionProgress(
                _bookFormatConversionLastProgress.Percentage,
                "格式转换失败。"));
            await ShowMessageAsync("格式转换失败", exception.Message);
        }
        finally
        {
            if (temporaryOutput is not null)
                TryDeleteTemporaryFormatPath(temporaryOutput);

            BookConversionPopup.Visibility = Visibility.Collapsed;
            _bookFormatConversionCard?.ClearConversionProgress();
            _bookFormatConversionCard = null;
            _bookFormatConversionBookId = null;
            _bookFormatConversionMinimized = false;
            _bookFormatConversionInProgress = false;
            if (ReferenceEquals(_bookFormatConversionCancellation, cancellation))
                _bookFormatConversionCancellation = null;
            cancellation.Dispose();
        }
    }

    private BookCardViewModel? FindBookCard(Guid bookId) =>
        ViewModel.Books.FirstOrDefault(card => card.Book.Id == bookId);

    private void ShowBookConversionPopup(
        Book book,
        string sourceFormat,
        string targetFormat,
        FormatConversionProgress progress)
    {
        BookConversionPopupTitleText.Text = $"转换《{book.Title}》";
        BookConversionPopupFormatText.Text =
            $"Calibre · {sourceFormat.ToUpperInvariant()} → {targetFormat.ToUpperInvariant()}";
        BookConversionPopup.Visibility = Visibility.Visible;
        ApplyBookConversionProgress(progress);
    }

    private void ApplyBookConversionProgress(FormatConversionProgress progress)
    {
        _bookFormatConversionLastProgress = progress;
        var percentage = Math.Clamp(progress.Percentage, 0, 100);
        BookConversionPopupProgress.Value = percentage;
        BookConversionPopupPercentageText.Text = $"{progress.RoundedPercentage}%";
        BookConversionPopupMessageText.Text = GetBookConversionPopupMessage(progress);
        _bookFormatConversionCard = FindBookCard(_bookFormatConversionBookId ?? Guid.Empty)
            ?? _bookFormatConversionCard;
        _bookFormatConversionCard?.SetConversionProgress(progress, _bookFormatConversionMinimized);
    }

    private static string GetBookConversionPopupMessage(FormatConversionProgress progress)
    {
        // Calibre's progress stream is a console-oriented, localized stream. Only show
        // the stable app-owned status text in the popup; the exact percentage is already
        // shown by the bar and the large percentage label above it.
        if (progress.Percentage <= 0 || progress.Percentage >= 100)
            return progress.Message;

        return "Calibre 正在转换…";
    }

    private void ApplyBookConversionCardState()
    {
        if (!_bookFormatConversionInProgress || _bookFormatConversionBookId is not Guid bookId)
            return;

        _bookFormatConversionCard = FindBookCard(bookId);
        _bookFormatConversionCard?.SetConversionProgress(
            _bookFormatConversionLastProgress,
            _bookFormatConversionMinimized);
    }

    private void MinimizeBookConversionPopup()
    {
        if (!_bookFormatConversionInProgress) return;
        _bookFormatConversionMinimized = true;
        _bookFormatConversionCard?.SetConversionProgress(_bookFormatConversionLastProgress, true);
        BookConversionPopup.Visibility = Visibility.Collapsed;
    }

    private void RestoreBookConversionPopup()
    {
        if (!_bookFormatConversionInProgress) return;
        _bookFormatConversionMinimized = false;
        _bookFormatConversionCard?.SetConversionProgress(_bookFormatConversionLastProgress, false);
        BookConversionPopup.Visibility = Visibility.Visible;
        ApplyBookConversionProgress(_bookFormatConversionLastProgress);
    }

    private void BookConversionPopup_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is Button) return;
        e.Handled = true;
        MinimizeBookConversionPopup();
    }

    private void BookConversionBackgroundButton_Click(object sender, RoutedEventArgs e) =>
        MinimizeBookConversionPopup();

    private void BookConversionCancelButton_Click(object sender, RoutedEventArgs e) =>
        _bookFormatConversionCancellation?.Cancel();

    private void BookConversionProgressIndicator_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: BookCardViewModel card }
            || _bookFormatConversionBookId != card.Book.Id) return;
        _bookFormatConversionCard = card;
        RestoreBookConversionPopup();
    }
}
