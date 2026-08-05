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

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
