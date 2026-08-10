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
            Assert.Equal(["第 1 章", "第 2 章"], document.Navigation.Select(item => item.Title));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task ReadsEpub3NavigationAndFragmentTargets()
    {
        var root = CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "toc.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                AddEntry(archive, "META-INF/container.xml", """
                    <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                      <rootfiles><rootfile full-path="EPUB/package.opf" /></rootfiles>
                    </container>
                    """);
                AddEntry(archive, "EPUB/package.opf", """
                    <package xmlns="http://www.idpf.org/2007/opf">
                      <manifest>
                        <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav" />
                        <item id="one" href="text/one.xhtml" media-type="application/xhtml+xml" />
                        <item id="two" href="text/two.xhtml" media-type="application/xhtml+xml" />
                      </manifest>
                      <spine><itemref idref="one" /><itemref idref="two" /></spine>
                    </package>
                    """);
                AddEntry(archive, "EPUB/nav.xhtml", """
                    <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
                      <body><nav epub:type="toc"><ol>
                        <li><a href="text/one.xhtml">开始阅读</a></li>
                        <li><a href="text/two.xhtml#part-2">第二部分</a></li>
                      </ol></nav></body>
                    </html>
                    """);
                AddEntry(archive, "EPUB/text/one.xhtml", "<html><body>一</body></html>");
                AddEntry(archive, "EPUB/text/two.xhtml", "<html><body><h1 id=\"part-2\">二</h1></body></html>");
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var document = await new EpubReaderPreparationService(paths)
                .PrepareAsync(epub, new string('c', 64));

            Assert.Equal(["开始阅读", "第二部分"], document.Navigation.Select(item => item.Title));
            Assert.Equal([0, 1], document.Navigation.Select(item => item.ChapterIndex));
            Assert.EndsWith("#part-2", document.Navigation[1].Target);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task ReadsNestedEpub3SubchaptersAsSeparateNavigationItems()
    {
        var root = CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "nested-toc.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                AddEntry(archive, "META-INF/container.xml", """
                    <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                      <rootfiles><rootfile full-path="EPUB/package.opf" /></rootfiles>
                    </container>
                    """);
                AddEntry(archive, "EPUB/package.opf", """
                    <package xmlns="http://www.idpf.org/2007/opf">
                      <manifest>
                        <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav" />
                        <item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml" />
                      </manifest>
                      <spine><itemref idref="chapter" /></spine>
                    </package>
                    """);
                AddEntry(archive, "EPUB/nav.xhtml", """
                    <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
                      <body><nav epub:type="toc"><ol>
                        <li><a href="chapter.xhtml">Chapter</a><ol>
                          <li><a href="chapter.xhtml#part-1">Part 1</a></li>
                          <li><a href="chapter.xhtml#part-2">Part 2</a></li>
                        </ol></li>
                      </ol></nav></body>
                    </html>
                    """);
                AddEntry(
                    archive,
                    "EPUB/chapter.xhtml",
                    "<html><body><h1>Chapter</h1><h2 id=\"part-1\">Part 1</h2><h2 id=\"part-2\">Part 2</h2></body></html>");
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var document = await new EpubReaderPreparationService(paths)
                .PrepareAsync(epub, new string('e', 64));

            Assert.Equal(["Chapter", "Part 1", "Part 2"], document.Navigation.Select(item => item.Title));
            Assert.Equal([0, 0, 0], document.Navigation.Select(item => item.ChapterIndex));
            Assert.EndsWith("chapter.xhtml", document.Navigation[0].Target);
            Assert.EndsWith("chapter.xhtml#part-1", document.Navigation[1].Target);
            Assert.EndsWith("chapter.xhtml#part-2", document.Navigation[2].Target);
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

    [Fact]
    public async Task ReusesCompletedExtractionForSameContentHash()
    {
        var root = CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "cached.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                AddEntry(archive, "META-INF/container.xml", """
                    <container><rootfiles><rootfile full-path="content.opf" /></rootfiles></container>
                    """);
                AddEntry(archive, "content.opf", """
                    <package><manifest><item id="one" href="one.xhtml" media-type="application/xhtml+xml" /></manifest>
                    <spine><itemref idref="one" /></spine></package>
                    """);
                AddEntry(archive, "one.xhtml", "<html><body>original</body></html>");
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var service = new EpubReaderPreparationService(paths);
            var hash = new string('d', 64);
            var first = await service.PrepareAsync(epub, hash);
            await File.WriteAllTextAsync(first.Chapters[0], "<html><body>cached</body></html>");

            var second = await service.PrepareAsync(epub, hash);

            Assert.Equal("<html><body>cached</body></html>", await File.ReadAllTextAsync(second.Chapters[0]));
            Assert.True(File.Exists(Path.Combine(second.RootPath, ".kkindle-extracted")));
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
