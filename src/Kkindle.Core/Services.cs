namespace Kkindle.Core;

public interface IBookLibraryService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Book>> SearchAsync(string? query = null, CancellationToken cancellationToken = default);
    Task<ImportBatchResult> ImportAsync(IEnumerable<string> paths, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<BookFile> AddFileToBookAsync(Guid bookId, string sourcePath, CancellationToken cancellationToken = default);
    Task UpdateMetadataAsync(Book book, CancellationToken cancellationToken = default);
    Task DeleteFileAsync(Guid bookId, Guid bookFileId, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid bookId, CancellationToken cancellationToken = default);
    string GetAbsoluteFilePath(BookFile file);
}

public interface IBookFormatConverter
{
    Task ConvertAsync(
        string sourcePath,
        string destinationPath,
        IProgress<FormatConversionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IMetadataService
{
    Task<BookMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default);
}

public interface IKindleDeviceService
{
    Task<IReadOnlyList<KindleDevice>> DetectDevicesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KindleBook>> ScanBooksAsync(KindleDevice device, CancellationToken cancellationToken = default);
    Task SendBookAsync(KindleDevice device, BookFile bookFile, string sourcePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<string> ExportBookAsync(KindleDevice device, KindleBook book, string destinationDirectory, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KindleDeviceResource>> ScanResourcesAsync(KindleDevice device, KindleResourceKind kind, CancellationToken cancellationToken = default);
    Task SendResourceAsync(KindleDevice device, KindleResourceKind kind, string sourcePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task ExportResourceAsync(KindleDevice device, KindleDeviceResource resource, string destinationPath, CancellationToken cancellationToken = default);
    Task RemoveResourceAsync(KindleDevice device, KindleDeviceResource resource, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KindleClipping>> ReadClippingsAsync(KindleDevice device, CancellationToken cancellationToken = default);
    Task DeleteClippingAsync(KindleDevice device, string clippingId, CancellationToken cancellationToken = default);
    Task EjectAsync(KindleDevice device, CancellationToken cancellationToken = default);
}
