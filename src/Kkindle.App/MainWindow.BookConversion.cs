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
    private CancellationTokenSource? _automaticReaderFormatGenerationCancellation;
    private bool _automaticReaderFormatGenerationInProgress;

    private sealed record AutomaticReaderFormatGenerationResult(
        int GeneratedCount,
        IReadOnlyList<string> Failures);

    private async void ConvertBookToEpubMenuItem_Click(object sender, RoutedEventArgs e) =>
        await ConvertBookFromMenuAsync(sender, "epub");

    private async void ConvertBookToAzw3MenuItem_Click(object sender, RoutedEventArgs e) =>
        await ConvertBookFromMenuAsync(sender, "azw3");

    private async void ConvertBookToPdfMenuItem_Click(object sender, RoutedEventArgs e) =>
        await ConvertBookFromMenuAsync(sender, "pdf");

    private async Task ConvertBookFromMenuAsync(object sender, string targetFormat)
    {
        if (sender is not MenuFlyoutItem { Tag: Book book }) return;
        if (_bookFormatConversionInProgress || _automaticReaderFormatGenerationInProgress)
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
            await ShowMessageAsync("格式转换", "需要 EPUB、AZW3、PDF 或 MOBI 作为转换源。");
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
            AppendConversionLog($"《{book.Title}》{sourceFile.Format}→{target}：{exception.Message}");
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

    private async Task<AutomaticReaderFormatGenerationResult> AutoGenerateReaderFormatsForImportsAsync(
        ImportBatchResult importResult,
        CancellationToken cancellationToken = default)
    {
        if (!_appSettings.AutoGenerateEpubAndAzw3OnImport)
            return new AutomaticReaderFormatGenerationResult(0, []);

        var books = importResult.Items
            .Where(item => item.Succeeded && item.Added && item.Book is not null)
            .Select(item => item.Book!)
            .GroupBy(book => book.Id)
            .Select(group => group.First())
            .Where(book => BookFormatConversionPolicy.GetMissingDefaultReaderFormats(book.Files).Count > 0)
            .ToArray();
        if (books.Length == 0)
            return new AutomaticReaderFormatGenerationResult(0, []);

        if (_bookFormatConversionInProgress || _automaticReaderFormatGenerationInProgress)
            return new AutomaticReaderFormatGenerationResult(
                0,
                ["已有格式转换正在进行，未启动 EPUB/AZW3 自动补齐。"]);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _automaticReaderFormatGenerationCancellation = linkedCancellation;
        _automaticReaderFormatGenerationInProgress = true;
        var failures = new List<string>();
        var generatedCount = 0;
        try
        {
            foreach (var book in books)
            {
                foreach (var targetFormat in BookFormatConversionPolicy.GetMissingDefaultReaderFormats(book.Files))
                {
                    linkedCancellation.Token.ThrowIfCancellationRequested();
                    var sourceFile = BookFormatConversionPolicy.SelectSource(book.Files, targetFormat);
                    if (sourceFile is null)
                    {
                        failures.Add($"《{book.Title}》没有可用于生成 {targetFormat.ToUpperInvariant()} 的源格式。");
                        continue;
                    }

                    string? temporaryOutput = null;
                    try
                    {
                        var sourcePath = _library.GetAbsoluteFilePath(sourceFile);
                        temporaryOutput = CreateTemporaryFormatPath(book.Title, targetFormat);
                        TaskStatusText.Text = $"正在为《{book.Title}》自动生成 {targetFormat.ToUpperInvariant()}…";
                        var progress = new Progress<FormatConversionProgress>(value =>
                        {
                            TaskProgress.Visibility = Visibility.Visible;
                            TaskProgress.Value = value.Percentage;
                            TaskStatusText.Text = $"正在生成 {targetFormat.ToUpperInvariant()}：{book.Title}（{value.RoundedPercentage}%）";
                        });
                        await _formatConverter.ConvertAsync(
                            sourcePath,
                            temporaryOutput,
                            progress,
                            linkedCancellation.Token);
                        var addedFile = await _library.AddFileToBookAsync(
                            book.Id,
                            temporaryOutput,
                            linkedCancellation.Token);
                        book.Files.Add(addedFile);
                        generatedCount++;
                    }
                    catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        AppendConversionLog($"《{book.Title}》自动生成 {targetFormat.ToUpperInvariant()}：{exception.Message}");
                        failures.Add($"《{book.Title}》生成 {targetFormat.ToUpperInvariant()}：{exception.Message}");
                    }
                    finally
                    {
                        if (temporaryOutput is not null)
                            TryDeleteTemporaryFormatPath(temporaryOutput);
                    }
                }
            }

            if (generatedCount > 0)
                await RefreshLibraryAsync();

            TaskStatusText.Text = failures.Count == 0
                ? $"已自动补齐 {generatedCount} 个 EPUB/AZW3 文件"
                : $"已生成 {generatedCount} 个 EPUB/AZW3 文件，{failures.Count} 个失败";
            return new AutomaticReaderFormatGenerationResult(generatedCount, failures);
        }
        finally
        {
            TaskProgress.Visibility = Visibility.Collapsed;
            _automaticReaderFormatGenerationInProgress = false;
            if (ReferenceEquals(_automaticReaderFormatGenerationCancellation, linkedCancellation))
                _automaticReaderFormatGenerationCancellation = null;
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
        // The conversion popup is the progress surface for format work. Keep the
        // generic task panel hidden so the two bottom-right overlays never stack.
        TaskProgress.Visibility = Visibility.Collapsed;
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

    private void AppendConversionLog(string message)
    {
        try
        {
            Directory.CreateDirectory(_paths.Logs);
            File.AppendAllText(
                Path.Combine(_paths.Logs, "conversion.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}",
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // Logging must never break a conversion or import flow.
        }
    }
}
