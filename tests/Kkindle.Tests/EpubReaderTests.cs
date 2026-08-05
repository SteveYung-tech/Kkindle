using System.IO.Compression;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class EpubReaderTests
{
    [Fact]
    public async Task PreparesChaptersInSpineOrder()
    {
        var root = CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "reader.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                AddEntry(archive, "META-INF/container.xml", """
                    <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                      <rootfiles><rootfile full-path="OEBPS/content.opf" /></rootfiles>
                    </container>
                    """);
                AddEntry(archive, "OEBPS/content.opf", """
                    <package xmlns="http://www.idpf.org/2007/opf">
                      <manifest>
                        <item id="two" href="chapter-2.xhtml" media-type="application/xhtml+xml" />
                        <item id="one" href="chapter-1.xhtml" media-type="application/xhtml+xml" />
                      </manifest>
                      <spine><itemref idref="one" /><itemref idref="two" /></spine>
                    </package>
                    """);
                AddEntry(archive, "OEBPS/chapter-1.xhtml", "<html><body>第一章</body></html>");
                AddEntry(archive, "OEBPS/chapter-2.xhtml", "<html><body>第二章</body></html>");
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var service = new EpubReaderPreparationService(paths);
            var document = await service.PrepareAsync(epub, new string('a', 64));

            Assert.Equal(2, document.Chapters.Count);
            Assert.EndsWith("chapter-1.xhtml", document.Chapters[0]);
            Assert.EndsWith("chapter-2.xhtml", document.Chapters[1]);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task RejectsArchivePathOutsideReaderCache()
    {
        var root = CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "unsafe.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
                AddEntry(archive, "../outside.txt", "unsafe");

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var service = new EpubReaderPreparationService(paths);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.PrepareAsync(epub, new string('b', 64)));
            Assert.False(File.Exists(Path.Combine(paths.ReaderCache, "outside.txt")));
        }
        finally { TryDelete(root); }
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
