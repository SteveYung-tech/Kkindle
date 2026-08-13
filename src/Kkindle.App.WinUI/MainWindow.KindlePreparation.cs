using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public sealed partial class MainWindow
{
    private sealed class PreparedKindleTransfer : IDisposable
    {
        private readonly string? _temporaryDirectory;

        public PreparedKindleTransfer(BookFile file, string sourcePath, string? temporaryDirectory = null)
        {
            File = file;
            SourcePath = sourcePath;
            _temporaryDirectory = temporaryDirectory;
        }

        public BookFile File { get; }
        public string SourcePath { get; }

        public void Dispose()
        {
            if (_temporaryDirectory is null) return;
            try
            {
                if (System.IO.File.Exists(SourcePath)) System.IO.File.Delete(SourcePath);
                if (Directory.Exists(_temporaryDirectory)
                    && !Directory.EnumerateFileSystemEntries(_temporaryDirectory).Any())
                {
                    Directory.Delete(_temporaryDirectory, recursive: false);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<PreparedKindleTransfer> PrepareKindleTransferAsync(
        Book book,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sourceFile = KindleTransferPolicy.SelectPreferred(book.Files)
            ?? throw new NotSupportedException("没有可发送到 Kindle 的 AZW3、MOBI、EPUB 或 PDF 文件。");
        var sourcePath = _library.GetAbsoluteFilePath(sourceFile);
        if (!System.IO.File.Exists(sourcePath))
            throw new FileNotFoundException("找不到本地书籍文件，请先刷新书库。", sourcePath);

        var requiresConversion = KindleTransferPolicy.RequiresConversionToAzw3(sourceFile);

        if (!requiresConversion)
            return new PreparedKindleTransfer(sourceFile, sourcePath);

        var temporaryRoot = Path.Combine(Path.GetTempPath(), "Kkindle", "kindle-ready");
        var temporaryDirectory = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var destinationPath = Path.Combine(
            temporaryDirectory,
            KindleTransferPolicy.CreateSafeFileName(book.Title, ".azw3"));

        try
        {
            var conversionProgress = progress is null
                ? null
                : new Progress<FormatConversionProgress>(value => progress.Report(new TransferProgress(
                    value.RoundedPercentage,
                    100,
                    $"正在生成 Kindle 兼容版本 · {value.RoundedPercentage}%")));
            await _formatConverter.ConvertAsync(sourcePath, destinationPath, conversionProgress, cancellationToken);

            var output = new FileInfo(destinationPath);
            if (!output.Exists || output.Length < 1024)
                throw new InvalidDataException("Kindle 兼容版本生成失败：输出文件为空。");

            var metadata = await new BookMetadataService().ReadMetadataAsync(destinationPath, cancellationToken);
            if (metadata.CoverBytes is not { Length: > 0 })
                throw new InvalidDataException("Kindle 兼容版本未包含可识别的封面，已停止发送。");

            var preparedFile = new BookFile
            {
                Id = sourceFile.Id,
                BookId = sourceFile.BookId,
                Format = "azw3",
                RelativePath = Path.GetFileName(destinationPath),
                Size = output.Length,
                Sha256 = await Hashing.Sha256Async(destinationPath, cancellationToken)
            };
            return new PreparedKindleTransfer(preparedFile, destinationPath, temporaryDirectory);
        }
        catch
        {
            try
            {
                if (System.IO.File.Exists(destinationPath)) System.IO.File.Delete(destinationPath);
                if (Directory.Exists(temporaryDirectory)
                    && !Directory.EnumerateFileSystemEntries(temporaryDirectory).Any())
                {
                    Directory.Delete(temporaryDirectory, recursive: false);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
