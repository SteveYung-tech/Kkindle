using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class KindleDeviceTests
{
    [Fact]
    public void DeviceIdentityUsesVolumeSerialAcrossDriveLetterChanges()
    {
        var firstDetection = new KindleDevice { RootPath = @"E:\", VolumeSerial = "A1B2C3D4" };
        var secondDetection = new KindleDevice { RootPath = @"F:\", VolumeSerial = "a1b2c3d4" };
        var unidentifiedDevice = new KindleDevice { RootPath = @"F:\" };

        Assert.Equal(firstDetection.Identity, secondDetection.Identity, ignoreCase: true);
        Assert.NotEqual(firstDetection.RootPath, secondDetection.RootPath);
        Assert.NotEqual(firstDetection.Identity, unidentifiedDevice.Identity);
    }

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
    public async Task ScanBooksExcludesKindleDictionaryDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(root, "documents");
        var dictionaries = Path.Combine(documents, "Dictionaries");
        Directory.CreateDirectory(dictionaries);
        await File.WriteAllTextAsync(Path.Combine(documents, "novel.azw3"), "book");
        await File.WriteAllTextAsync(Path.Combine(dictionaries, "english.azw3"), "dictionary");
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };

            var books = await service.ScanBooksAsync(device);

            var book = Assert.Single(books);
            Assert.Equal("novel.azw3", book.FileName);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task ScanBooksExcludesDictionaryTaggedBookOutsideDictionaryDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        var downloads = Path.Combine(root, "documents", "Downloads", "Items01");
        Directory.CreateDirectory(downloads);
        var dictionaryPath = Path.Combine(downloads, "dictionary.azw");
        await File.WriteAllBytesAsync(dictionaryPath, CreateDictionaryTaggedKindleFile());
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };

            var books = await service.ScanBooksAsync(device);

            Assert.Empty(books);
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
    public async Task ExportsBookFromDriveConnectedKindle()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(root, "documents");
        Directory.CreateDirectory(documents);
        var source = Path.Combine(documents, "export.epub");
        await File.WriteAllTextAsync(source, "exported book");
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            var book = new KindleBook { RelativePath = Path.Combine("documents", "export.epub") };

            var exportedPath = await service.ExportBookAsync(device, book, destination);

            Assert.Equal("exported book", await File.ReadAllTextAsync(exportedPath));
            Assert.Equal("export.epub", Path.GetFileName(exportedPath));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            try { Directory.Delete(destination, true); } catch { }
        }
    }

    [Fact]
    public async Task HashMismatchCleansPartialTransferFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(root, "documents");
        Directory.CreateDirectory(documents);
        var source = Path.Combine(root, "mismatch.epub");
        await File.WriteAllTextAsync(source, "content whose hash will not match");
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            var wrongHash = new string('0', 64);

            await Assert.ThrowsAsync<IOException>(() =>
                service.SendBookAsync(device, new BookFile { Sha256 = wrongHash }, source));

            Assert.Empty(Directory.EnumerateFiles(documents, "*.kkindle-part", SearchOption.AllDirectories));
            Assert.False(File.Exists(Path.Combine(documents, "mismatch.epub")));
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

    [Fact]
    public async Task ManagesKindleFontsInsideFontsDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "documents"));
        var sourceDirectory = Path.Combine(root, "sources");
        Directory.CreateDirectory(sourceDirectory);
        var source = Path.Combine(sourceDirectory, "reader.ttf");
        var exported = Path.Combine(root, "exported.ttf");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4, 5]);
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };

            await service.SendResourceAsync(device, KindleResourceKind.Font, source);
            var font = Assert.Single(await service.ScanResourcesAsync(device, KindleResourceKind.Font));
            Assert.Equal(Path.Combine("fonts", "reader.ttf"), font.RelativePath);
            Assert.Equal(5, font.Size);
            Assert.NotEmpty(font.Sha256);

            await service.ExportResourceAsync(device, font, exported);
            Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(exported));

            await service.RemoveResourceAsync(device, font);
            Assert.Empty(await service.ScanResourcesAsync(device, KindleResourceKind.Font));
            Assert.True(Directory.Exists(Path.Combine(root, "documents")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task ManagesKindleDictionariesInsideDedicatedDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "documents"));
        var source = Path.Combine(root, "english.azw3");
        await File.WriteAllBytesAsync(source, [9, 8, 7]);
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };

            await service.SendResourceAsync(device, KindleResourceKind.Dictionary, source);
            var dictionary = Assert.Single(await service.ScanResourcesAsync(device, KindleResourceKind.Dictionary));
            Assert.Equal(Path.Combine("documents", "dictionaries", "english.azw3"), dictionary.RelativePath);
            Assert.Empty(await service.ScanResourcesAsync(device, KindleResourceKind.Font));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task ResourceOperationsRejectWrongFormatsAndPathTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "documents"));
        var wrong = Path.Combine(root, "not-a-font.exe");
        var outside = Path.Combine(root, "outside.ttf");
        await File.WriteAllTextAsync(wrong, "wrong");
        await File.WriteAllTextAsync(outside, "keep");
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.SendResourceAsync(device, KindleResourceKind.Font, wrong));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.RemoveResourceAsync(device, new KindleDeviceResource
                {
                    Kind = KindleResourceKind.Font,
                    RelativePath = Path.Combine("fonts", "..", "outside.ttf")
                }));
            Assert.True(File.Exists(outside));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Theory]
    [InlineData(KindleResourceKind.Font, "fonts/font.otf", true)]
    [InlineData(KindleResourceKind.Font, "documents/font.otf", false)]
    [InlineData(KindleResourceKind.Dictionary, "documents/dictionaries/main.mobi", true)]
    [InlineData(KindleResourceKind.Dictionary, "documents/main.mobi", false)]
    public void ResourcePolicyConfinesFilesToExpectedKindleDirectory(
        KindleResourceKind kind,
        string path,
        bool expected)
    {
        Assert.Equal(expected, KindleResourcePolicy.TryGetPathWithinRoot(kind, path, out _));
    }

    [Fact]
    public async Task CancelledResourceTransferLeavesNoPartialFont()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "documents"));
        var source = Path.Combine(root, "large.ttf");
        await File.WriteAllBytesAsync(source, new byte[4 * 1024 * 1024]);
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress<TransferProgress>(_ => cancellation.Cancel());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.SendResourceAsync(device, KindleResourceKind.Font, source, progress, cancellation.Token));
            var fonts = Path.Combine(root, "fonts");
            Assert.Empty(Directory.EnumerateFiles(fonts, "*.kkindle-part", SearchOption.AllDirectories));
            Assert.False(File.Exists(Path.Combine(fonts, "large.ttf")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task ReadsAndDeletesIndividualKindleClippingWithoutLosingOthers()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(root, "documents");
        Directory.CreateDirectory(documents);
        var clippingsPath = Path.Combine(documents, "My Clippings.txt");
        var content = """
            Scale (Geoffrey West)
            - Your Highlight on page 12 | Location 180-182 | Added on Sunday, August 9, 2026

            Cities are living systems.
            ==========
            规模（杰弗里·韦斯特）
            - 您在位置 220 的笔记 | 添加于 2026年8月10日星期一

            复习这一段
            ==========
            """;
        await File.WriteAllTextAsync(clippingsPath, content, new System.Text.UTF8Encoding(true));
        try
        {
            var service = new KindleDeviceService();
            var device = new KindleDevice { RootPath = root, Name = "Fake Kindle", IsReady = true };
            var items = await service.ReadClippingsAsync(device);
            Assert.Equal(2, items.Count);
            Assert.Equal(KindleClippingType.Highlight, items[0].Type);
            Assert.Equal("Cities are living systems.", items[0].Content);
            Assert.Equal(KindleClippingType.Note, items[1].Type);
            Assert.Equal("规模", items[1].BookTitle);
            Assert.Equal("杰弗里·韦斯特", items[1].Author);

            await service.DeleteClippingAsync(device, items[0].Id);
            var remaining = Assert.Single(await service.ReadClippingsAsync(device));
            Assert.Equal("复习这一段", remaining.Content);
            Assert.DoesNotContain("Cities are living systems", await File.ReadAllTextAsync(clippingsPath));
            Assert.False(File.Exists(clippingsPath + ".kkindle-part"));
            Assert.False(File.Exists(clippingsPath + ".kkindle-backup"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void ClippingsParserKeepsDuplicateRecordsIndividuallyAddressable()
    {
        const string block = "Book (Author)\n- Your Highlight at Location 1\n\nSame quote";
        var parsed = KindleClippingsParser.Parse($"{block}\n==========\n{block}\n==========\n");

        Assert.Equal(2, parsed.Count);
        Assert.NotEqual(parsed[0].Id, parsed[1].Id);
        var rebuilt = KindleClippingsParser.BuildDocument([parsed[1]]);
        Assert.Single(KindleClippingsParser.Parse(rebuilt));
        Assert.EndsWith("==========\r\n", rebuilt);
    }

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] CreateDictionaryTaggedKindleFile()
    {
        var bytes = new byte[128];
        "EXTH"u8.CopyTo(bytes.AsSpan(32));
        WriteBigEndian(bytes, 36, 32);
        WriteBigEndian(bytes, 40, 1);
        WriteBigEndian(bytes, 44, 105);
        WriteBigEndian(bytes, 48, 20);
        "Dictionaries"u8.CopyTo(bytes.AsSpan(52));
        return bytes;
    }

    private static void WriteBigEndian(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
