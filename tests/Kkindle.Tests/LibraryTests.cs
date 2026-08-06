using System.IO.Compression;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class LibraryTests
{
    [Fact]
    public async Task ImportsEpubMetadataCoverAndAvoidsDuplicateHash()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "纸上书.epub");
            CreateEpub(source);
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new SqliteBookLibraryService(paths, new BookMetadataService());
            await service.InitializeAsync();

            var first = await service.ImportAsync([source]);
            var second = await service.ImportAsync([source]);
            var books = await service.SearchAsync();

            Assert.Equal(1, first.SuccessCount);
            Assert.Equal(1, second.SuccessCount);
            Assert.Single(books);
            Assert.Equal("测试书", books[0].Title);
            Assert.Equal("测试作者", books[0].Authors);
            Assert.Equal("一本测试用书", books[0].Description);
            Assert.Single(books[0].Files);
            Assert.Equal("纸上书.epub", Path.GetFileName(books[0].Files[0].RelativePath));
            Assert.NotNull(books[0].CoverPath);
            Assert.True(File.Exists(service.GetAbsoluteFilePath(books[0].Files[0])));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task SearchMatchesTitleAndTags()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "search.epub");
            CreateEpub(source);
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new SqliteBookLibraryService(paths, new BookMetadataService());
            await service.InitializeAsync();
            var imported = await service.ImportAsync([source]);
            var book = (await service.SearchAsync()).Single();
            book.Tags = "阅读,测试";
            await service.UpdateMetadataAsync(book);

            Assert.Single(await service.SearchAsync("测试"));
            Assert.Empty(await service.SearchAsync("不存在"));
            Assert.Equal(1, imported.SuccessCount);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task BatchImportReportsMetadataFailureAndContinuesWithOtherFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var broken = Path.Combine(root, "损坏.epub");
            var valid = Path.Combine(root, "正常.epub");
            CreateEpub(broken, "broken");
            CreateEpub(valid, "valid");
            var paths = new AppPaths(Path.Combine(root, "app"));
            var metadata = new SelectiveFailureMetadataService("损坏.epub", new BookMetadataService());
            var service = new SqliteBookLibraryService(paths, metadata);
            await service.InitializeAsync();

            var result = await service.ImportAsync([broken, valid]);
            var book = Assert.Single(await service.SearchAsync());

            Assert.Equal(1, result.SuccessCount);
            Assert.Equal(1, result.FailureCount);
            Assert.Contains(result.Items, item => item.SourcePath == broken && !item.Succeeded);
            Assert.Contains(result.Items, item => item.SourcePath == valid && item.Succeeded);
            Assert.Equal("测试书", book.Title);
            Assert.Empty(Directory.EnumerateFiles(paths.Library, "*.part", SearchOption.AllDirectories));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task SameBookWithSameSourceNameKeepsBothFilesUsingNumberedName()
    {
        var root = CreateTempDirectory();
        try
        {
            var firstDirectory = Path.Combine(root, "first");
            var secondDirectory = Path.Combine(root, "second");
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            var first = Path.Combine(firstDirectory, "同名.epub");
            var second = Path.Combine(secondDirectory, "同名.epub");
            CreateEpub(first, "first");
            CreateEpub(second, "second");
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new SqliteBookLibraryService(paths, new BookMetadataService());
            await service.InitializeAsync();

            var result = await service.ImportAsync([first, second]);
            var book = Assert.Single(await service.SearchAsync());
            var importedNames = book.Files
                .Select(file => Path.GetFileName(file.RelativePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.Equal(2, result.SuccessCount);
            Assert.Equal(2, book.Files.Count);
            Assert.Contains("同名.epub", importedNames);
            Assert.Contains("同名 (2).epub", importedNames);
            Assert.All(book.Files, file => Assert.True(File.Exists(service.GetAbsoluteFilePath(file))));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task CancellationCleansPartialImportAndLeavesLibraryEmpty()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "取消导入.pdf");
            await File.WriteAllBytesAsync(source, new byte[8 * 1024 * 1024]);
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new SqliteBookLibraryService(paths, new BookMetadataService());
            await service.InitializeAsync();
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress<TransferProgress>(_ => cancellation.Cancel());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ImportAsync([source], progress, cancellation.Token));

            Assert.Empty(await service.SearchAsync());
            Assert.Empty(Directory.EnumerateFiles(paths.Library, "*.part", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(paths.Library, "*", SearchOption.AllDirectories));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void RejectsBookFilePathOutsideManagedDataDirectory()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new SqliteBookLibraryService(paths, new BookMetadataService());
            var outside = Path.Combine(root, "outside.epub");
            var relativeOutsidePath = Path.GetRelativePath(paths.Data, outside);

            Assert.Throws<InvalidOperationException>(() =>
                service.GetAbsoluteFilePath(new BookFile { RelativePath = relativeOutsidePath }));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task FallbackMetadataCleansHashBeforeDownloadSourceMarker()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "纸上作品_0123456789ABCDEF0123456789ABCDEF (Z-Library).pdf");
            await File.WriteAllTextAsync(source, "not a parsed PDF");

            var metadata = await new BookMetadataService().ReadMetadataAsync(source);

            Assert.Equal("纸上作品", metadata.Title);
            Assert.Equal("未知作者", metadata.Authors);
        }
        finally { TryDelete(root); }
    }

    private static void CreateEpub(string path, string? uniqueMarker = null)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        AddEntry(archive, "META-INF/container.xml", """
            <?xml version="1.0"?>
            <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
              <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml" /></rootfiles>
            </container>
            """);
        AddEntry(archive, "OEBPS/content.opf", """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>测试书</dc:title>
                <dc:creator>测试作者</dc:creator>
                <dc:description>一本测试用书</dc:description>
                <meta name="cover" content="cover" />
              </metadata>
              <manifest><item id="cover" href="cover.jpg" media-type="image/jpeg" /></manifest>
            </package>
            """);
        if (uniqueMarker is not null)
            AddEntry(archive, "OEBPS/test-marker.txt", uniqueMarker);
        var cover = archive.CreateEntry("OEBPS/cover.jpg");
        using var stream = cover.Open();
        stream.Write([1, 2, 3, 4]);
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { }
    }

    private sealed class SelectiveFailureMetadataService(
        string failingFileName,
        IMetadataService inner) : IMetadataService
    {
        public Task<BookMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default)
        {
            return string.Equals(Path.GetFileName(path), failingFileName, StringComparison.Ordinal)
                ? Task.FromException<BookMetadata>(new InvalidDataException("图书文件已损坏。"))
                : inner.ReadMetadataAsync(path, cancellationToken);
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
