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
            Assert.Single(books[0].Files);
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

    private static void CreateEpub(string path)
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
}
