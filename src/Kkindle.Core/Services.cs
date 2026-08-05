namespace Kkindle.Core;

public interface IBookLibraryService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Book>> SearchAsync(string? query = null, CancellationToken cancellationToken = default);
    Task<ImportBatchResult> ImportAsync(IEnumerable<string> paths, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task UpdateMetadataAsync(Book book, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid bookId, CancellationToken cancellationToken = default);
    string GetAbsoluteFilePath(BookFile file);
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
    Task RemoveBookAsync(KindleDevice device, KindleBook book, CancellationToken cancellationToken = default);
    Task EjectAsync(KindleDevice device, CancellationToken cancellationToken = default);
}
