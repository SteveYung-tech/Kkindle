using System.IO.Compression;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class EpubReaderTests
{
    [Fact]
    public async Task PreparesChaptersInSpineOrder()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "reader.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                      <rootfiles><rootfile full-path="OEBPS/content.opf" /></rootfiles>
                    </container>
                    """);
                TestHelpers.AddZipEntry(archive, "OEBPS/content.opf", """
                    <package xmlns="http://www.idpf.org/2007/opf">
                      <manifest>
                        <item id="two" href="chapter-2.xhtml" media-type="application/xhtml+xml" />
                        <item id="one" href="chapter-1.xhtml" media-type="application/xhtml+xml" />
                      </manifest>
                      <spine><itemref idref="one" /><itemref idref="two" /></spine>
                    </package>
                    """);
                TestHelpers.AddZipEntry(archive, "OEBPS/chapter-1.xhtml", "<html><body>第一章</body></html>");
                TestHelpers.AddZipEntry(archive, "OEBPS/chapter-2.xhtml", "<html><body>第二章</body></html>");
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
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ReadsEpub3NavigationAndFragmentTargets()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "toc.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                      <rootfiles><rootfile full-path="EPUB/package.opf" /></rootfiles>
                    </container>
                    """);
                TestHelpers.AddZipEntry(archive, "EPUB/package.opf", """
                    <package xmlns="http://www.idpf.org/2007/opf">
                      <manifest>
                        <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav" />
                        <item id="one" href="text/one.xhtml" media-type="application/xhtml+xml" />
                        <item id="two" href="text/two.xhtml" media-type="application/xhtml+xml" />
                      </manifest>
                      <spine><itemref idref="one" /><itemref idref="two" /></spine>
                    </package>
                    """);
                TestHelpers.AddZipEntry(archive, "EPUB/nav.xhtml", """
                    <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
                      <body><nav epub:type="toc"><ol>
                        <li><a href="text/one.xhtml">开始阅读</a></li>
                        <li><a href="text/two.xhtml#part-2">第二部分</a></li>
                      </ol></nav></body>
                    </html>
                    """);
                TestHelpers.AddZipEntry(archive, "EPUB/text/one.xhtml", "<html><body>一</body></html>");
                TestHelpers.AddZipEntry(archive, "EPUB/text/two.xhtml", "<html><body><h1 id=\"part-2\">二</h1></body></html>");
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var document = await new EpubReaderPreparationService(paths)
                .PrepareAsync(epub, new string('c', 64));

            Assert.Equal(["开始阅读", "第二部分"], document.Navigation.Select(item => item.Title));
            Assert.Equal([0, 1], document.Navigation.Select(item => item.ChapterIndex));
            Assert.EndsWith("#part-2", document.Navigation[1].Target);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task SanitizesHtmlScriptsEventsExternalResourcesAndAddsReaderBridge()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "unsafe-content.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container><rootfiles><rootfile full-path="OEBPS/content.opf" /></rootfiles></container>
                    """);
                TestHelpers.AddZipEntry(archive, "OEBPS/content.opf", """
                    <package><manifest>
                      <item id="one" href="chapter.xhtml" media-type="application/xhtml+xml" />
                      <item id="css" href="styles/book.css" media-type="text/css" />
                    </manifest><spine><itemref idref="one" /></spine></package>
                    """);
                TestHelpers.AddZipEntry(archive, "OEBPS/chapter.xhtml", """
                    <!DOCTYPE html>
                    <html xmlns="http://www.w3.org/1999/xhtml">
                      <head>
                        <script>window.pwned = true;</script>
                        <style>.local { background-image: url("../images/ok.jpg"); } .remote { background-image: url("https://example.com/x.png"); }</style>
                      </head>
                      <body onload="window.pwned = true">
                        <img src="https://example.com/remote.jpg" />
                        <img class="local" src="../images/ok.jpg" />
                        <a class="footnote" href="#note-1"><img src="https://example.com/note.png" alt="" width="24" height="24" /></a>
                        <p id="note-1">Footnote text</p>
                        <a href="javascript:alert(1)">unsafe link</a>
                        <a href="chapter.xhtml#part">safe link</a>
                      </body>
                    </html>
                    """);
                TestHelpers.AddZipEntry(archive, "OEBPS/styles/book.css", """
                    .local { background: url("../images/ok.jpg"); }
                    .remote { background: url("data:image/png;base64,AAAA"); }
                    """);
                TestHelpers.AddZipEntry(archive, "OEBPS/images/ok.jpg", "image");
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var document = await new EpubReaderPreparationService(paths)
                .PrepareAsync(epub, new string('f', 64));

            var html = await File.ReadAllTextAsync(document.Chapters[0]);
            Assert.DoesNotContain("<script>window.pwned", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("onload=", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("https://example.com", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Content-Security-Policy", html, StringComparison.Ordinal);
            Assert.Contains("script-src 'nonce-", html, StringComparison.Ordinal);
            Assert.Contains("invokeCSharpAction", html, StringComparison.Ordinal);
            Assert.Contains("chrome.webview", html, StringComparison.Ordinal);
            Assert.Contains("type: \"scroll\"", html, StringComparison.Ordinal);
            Assert.Contains("contextmenu", html, StringComparison.Ordinal);
            Assert.Contains("reportSelection(event)", html, StringComparison.Ordinal);
            Assert.Contains("contextMenu: !!contextEvent", html, StringComparison.Ordinal);
            Assert.Contains("../images/ok.jpg", html, StringComparison.Ordinal);
            Assert.Contains("class=\"kkindle-footnote-marker\">注</sup>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("width=\"24\"", html, StringComparison.Ordinal);

            var cssPath = Path.Combine(document.RootPath, "OEBPS", "styles", "book.css");
            var css = await File.ReadAllTextAsync(cssPath);
            Assert.Contains("../images/ok.jpg", css, StringComparison.Ordinal);
            Assert.DoesNotContain("data:image", css, StringComparison.OrdinalIgnoreCase);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ReadsNestedEpub3SubchaptersAsSeparateNavigationItems()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "nested-toc.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                      <rootfiles><rootfile full-path="EPUB/package.opf" /></rootfiles>
                    </container>
                    """);
                TestHelpers.AddZipEntry(archive, "EPUB/package.opf", """
                    <package xmlns="http://www.idpf.org/2007/opf">
                      <manifest>
                        <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav" />
                        <item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml" />
                      </manifest>
                      <spine><itemref idref="chapter" /></spine>
                    </package>
                    """);
                TestHelpers.AddZipEntry(archive, "EPUB/nav.xhtml", """
                    <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
                      <body><nav epub:type="toc"><ol>
                        <li><a href="chapter.xhtml">Chapter</a><ol>
                          <li><a href="chapter.xhtml#part-1">Part 1</a></li>
                          <li><a href="chapter.xhtml#part-2">Part 2</a></li>
                        </ol></li>
                      </ol></nav></body>
                    </html>
                    """);
                TestHelpers.AddZipEntry(
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
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task RejectsArchivePathOutsideReaderCache()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "unsafe.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
                TestHelpers.AddZipEntry(archive, "../outside.txt", "unsafe");

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var service = new EpubReaderPreparationService(paths);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.PrepareAsync(epub, new string('b', 64)));
            Assert.False(File.Exists(Path.Combine(paths.ReaderCache, "outside.txt")));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task ReusesCompletedExtractionForSameContentHash()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "cached.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container><rootfiles><rootfile full-path="content.opf" /></rootfiles></container>
                    """);
                TestHelpers.AddZipEntry(archive, "content.opf", """
                    <package><manifest><item id="one" href="one.xhtml" media-type="application/xhtml+xml" /></manifest>
                    <spine><itemref idref="one" /></spine></package>
                    """);
                TestHelpers.AddZipEntry(archive, "one.xhtml", "<html><body>original</body></html>");
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
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task RebuildsStaleExtractionWhenReaderBridgeVersionChanges()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "stale-cache.epub");
            using (var archive = ZipFile.Open(epub, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "META-INF/container.xml", """
                    <container><rootfiles><rootfile full-path="content.opf" /></rootfiles></container>
                    """);
                TestHelpers.AddZipEntry(archive, "content.opf", """
                    <package><manifest><item id="one" href="one.xhtml" media-type="application/xhtml+xml" /></manifest>
                    <spine><itemref idref="one" /></spine></package>
                    """);
                TestHelpers.AddZipEntry(archive, "one.xhtml", "<html><body>original chapter</body></html>");
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var service = new EpubReaderPreparationService(paths);
            var hash = new string('9', 64);
            var first = await service.PrepareAsync(epub, hash);
            var marker = Path.Combine(first.RootPath, ".kkindle-extracted");
            await File.WriteAllTextAsync(first.Chapters[0], "<html><body>stale transformed chapter</body></html>");
            await File.WriteAllTextAsync(marker, $"{hash}\n0");

            var rebuilt = await service.PrepareAsync(epub, hash);
            var html = await File.ReadAllTextAsync(rebuilt.Chapters[0]);
            var markerText = await File.ReadAllTextAsync(marker);

            Assert.Contains("original chapter", html, StringComparison.Ordinal);
            Assert.DoesNotContain("stale transformed chapter", html, StringComparison.Ordinal);
            Assert.Contains("data-action=\"highlight-menu\"", html, StringComparison.Ordinal);
            Assert.Contains("荧光标记（黑白反色）  ▰", html, StringComparison.Ordinal);
            Assert.Contains(".kk-sel-styles.above", html, StringComparison.Ordinal);
            Assert.Contains("dismissedSelectionText", html, StringComparison.Ordinal);
            Assert.Contains("document.addEventListener(\"pointerup\"", html, StringComparison.Ordinal);
            Assert.Contains("direction: x < width / 3 ? -1 : 1", html, StringComparison.Ordinal);
            Assert.False(markerText.EndsWith("\n0", StringComparison.Ordinal));
        }
        finally { TestHelpers.TryDelete(root); }
    }
}
