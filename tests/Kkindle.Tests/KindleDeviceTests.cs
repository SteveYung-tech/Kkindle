using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class KindleDeviceTests
{
    [Fact]
    public async Task SendsAndScansBooksInDocumentsDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "documents"));
        var source = Path.Combine(root, "source.epub");
        await File.WriteAllTextAsync(source, "hello kindle");
        try
        {
            var hash = await ComputeHashAsync(source);
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            var file = new BookFile { Format = "epub", Sha256 = hash, RelativePath = "source.epub" };
            var service = new KindleDeviceService();

            await service.SendBookAsync(device, file, source);
            var books = await service.ScanBooksAsync(device);

            var book = Assert.Single(books);
            Assert.Equal("source.epub", book.FileName);
            Assert.Equal(hash, book.Sha256);
            Assert.True(File.Exists(Path.Combine(root, "documents", "source.epub")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task SameNameWithDifferentContentCreatesNumberedFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        var firstDirectory = Path.Combine(root, "first");
        var secondDirectory = Path.Combine(root, "second");
        Directory.CreateDirectory(Path.Combine(root, "documents"));
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var firstSource = Path.Combine(firstDirectory, "book.epub");
        var secondSource = Path.Combine(secondDirectory, "book.epub");
        await File.WriteAllTextAsync(firstSource, "first book");
        await File.WriteAllTextAsync(secondSource, "second book");
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            await service.SendBookAsync(device, new BookFile { Sha256 = await ComputeHashAsync(firstSource) }, firstSource);
            await service.SendBookAsync(device, new BookFile { Sha256 = await ComputeHashAsync(secondSource) }, secondSource);

            var books = await service.ScanBooksAsync(device);

            Assert.Equal(2, books.Count);
            Assert.Contains(books, book => book.FileName == "book.epub");
            Assert.Contains(books, book => book.FileName == "book (2).epub");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task RemovesOnlySelectedBookInsideDocumentsDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(root, "documents");
        Directory.CreateDirectory(documents);
        var selected = Path.Combine(documents, "remove.epub");
        var retained = Path.Combine(documents, "keep.epub");
        await File.WriteAllTextAsync(selected, "remove me");
        await File.WriteAllTextAsync(retained, "keep me");
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            await service.RemoveBookAsync(device, new KindleBook { RelativePath = Path.Combine("documents", "remove.epub") });

            Assert.False(File.Exists(selected));
            Assert.True(File.Exists(retained));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task RefusesToDeleteOutsideDocumentsDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "documents"));
        var outside = Path.Combine(root, "outside.epub");
        await File.WriteAllTextAsync(outside, "do not delete");
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.RemoveBookAsync(device, new KindleBook { RelativePath = "outside.epub" }));
            Assert.True(File.Exists(outside));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task CancellationCleansPartialTransferFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(root, "documents");
        Directory.CreateDirectory(documents);
        var source = Path.Combine(root, "cancel.epub");
        await File.WriteAllBytesAsync(source, new byte[4 * 1024 * 1024]);
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            var file = new BookFile { Sha256 = await ComputeHashAsync(source) };
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress<TransferProgress>(_ => cancellation.Cancel());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.SendBookAsync(device, file, source, progress, cancellation.Token));
            Assert.Empty(Directory.EnumerateFiles(documents, "*.kkindle-part", SearchOption.AllDirectories));
            Assert.False(File.Exists(Path.Combine(documents, "cancel.epub")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
