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

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
