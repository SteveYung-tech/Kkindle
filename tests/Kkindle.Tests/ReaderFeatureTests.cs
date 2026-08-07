using System.IO.Compression;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class ReaderFeatureTests
{
    [Fact]
    public async Task BuildsCachedChineseBookIndexAndFindsRelevantChunks()
    {
        var root = CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "index.epub");
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
                        <item id="one" href="one.xhtml" media-type="application/xhtml+xml" />
                        <item id="two" href="two.xhtml" media-type="application/xhtml+xml" />
                      </manifest>
                      <spine><itemref idref="one" /><itemref idref="two" /></spine>
                    </package>
                    """);
                AddEntry(archive, "EPUB/nav.xhtml", """
                    <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
                      <body><nav epub:type="toc"><ol>
                        <li><a href="one.xhtml">规模与规律</a></li>
                        <li><a href="two.xhtml">城市系统</a></li>
                      </ol></nav></body>
                    </html>
                    """);
                AddEntry(archive, "EPUB/one.xhtml", """
                    <html xmlns="http://www.w3.org/1999/xhtml"><body>
                      <h1>规模与规律</h1>
                      <p>规模法则描述系统尺度变化时仍然保持的数量关系。</p>
                      <p>它帮助读者比较动物、企业和城市在不同尺度下的共同结构。</p>
                    </body></html>
                    """);
                AddEntry(archive, "EPUB/two.xhtml", """
                    <html xmlns="http://www.w3.org/1999/xhtml"><body>
                      <h1>城市系统</h1><p>城市人口和基础设施之间存在可测量的统计关系。</p>
                    </body></html>
                    """);
            }

            var paths = new AppPaths(Path.Combine(root, "app"));
            var readerData = new ReaderDataService(paths);
            await readerData.InitializeAsync();
            var hash = new string('d', 64);
            var document = await new EpubReaderPreparationService(paths).PrepareAsync(epub, hash);
            var book = new Book { Id = Guid.NewGuid(), Title = "规模", Authors = "测试作者" };
            var file = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Sha256 = hash, Format = "epub" };
            var indexer = new EpubBookContentService(readerData);

            var firstCount = await indexer.EnsureIndexedAsync(book, file, document);
            var secondCount = await indexer.EnsureIndexedAsync(book, file, document);
            var results = await readerData.SearchBookAsync(book.Id, "这本书如何解释规模法则？");

            Assert.True(firstCount >= 2);
            Assert.Equal(0, secondCount);
            Assert.Contains(results, chunk => chunk.Content.Contains("规模法则", StringComparison.Ordinal));
            Assert.Contains(results, chunk => chunk.ChapterTitle == "规模与规律");
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task PersistsUpdatesAndDeletesReaderAnnotations()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new ReaderDataService(paths);
            await service.InitializeAsync();
            var fileId = Guid.NewGuid();
            var annotation = new ReaderAnnotation
            {
                Id = Guid.NewGuid(),
                BookId = Guid.NewGuid(),
                BookFileId = fileId,
                ChapterPath = "text/chapter.xhtml",
                StartOffset = 12,
                EndOffset = 20,
                SelectedText = "规模法则",
                Prefix = "理解",
                Suffix = "的意义",
                Note = "重要概念"
            };

            await service.SaveAnnotationAsync(annotation);
            annotation.Note = "更新后的笔记";
            annotation.UpdatedAt = DateTimeOffset.UtcNow;
            await service.SaveAnnotationAsync(annotation);
            var saved = Assert.Single(await service.GetAnnotationsAsync(fileId));

            Assert.Equal("更新后的笔记", saved.Note);
            Assert.Equal(12, saved.StartOffset);

            await service.DeleteAnnotationAsync(annotation.Id);
            Assert.Empty(await service.GetAnnotationsAsync(fileId));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task ResolvesOnlyFootnotesInsideEpubCache()
    {
        var root = CreateTempDirectory();
        try
        {
            var epubRoot = Path.Combine(root, "cache");
            Directory.CreateDirectory(epubRoot);
            var notes = Path.Combine(epubRoot, "notes.xhtml");
            await File.WriteAllTextAsync(notes, """
                <html xmlns="http://www.w3.org/1999/xhtml"><body>
                  <aside id="note-1"><p>这是跨文件脚注内容。</p><a href="chapter.xhtml#back">↩</a></aside>
                </body></html>
                """);
            var outside = Path.Combine(root, "outside.xhtml");
            await File.WriteAllTextAsync(outside, "<html><body><p id=\"bad\">不应读取</p></body></html>");
            var service = new EpubFootnoteResolver();

            var result = await service.ResolveAsync(epubRoot,
            [
                new Uri(notes).AbsoluteUri + "#note-1",
                new Uri(outside).AbsoluteUri + "#bad"
            ]);

            var text = Assert.Single(result).Value;
            Assert.Contains("跨文件脚注内容", text);
            Assert.DoesNotContain("不应读取", text);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task ResolvesFootnoteMarkerParagraphInsteadOfOnlyAnchorText()
    {
        var root = CreateTempDirectory();
        try
        {
            var epubRoot = Path.Combine(root, "cache");
            Directory.CreateDirectory(epubRoot);
            var chapter = Path.Combine(epubRoot, "chapter.xhtml");
            await File.WriteAllTextAsync(chapter, """
                <html xmlns="http://www.w3.org/1999/xhtml"><body>
                  <p><a href="chapter.xhtml#note1" id="note1n">[1]</a> full footnote details after the marker.</p>
                </body></html>
                """);

            var service = new EpubFootnoteResolver();
            var result = await service.ResolveAsync(
                epubRoot,
                [new Uri(chapter).AbsoluteUri + "#note1n"]);

            var text = Assert.Single(result).Value;
            Assert.Contains("[1]", text);
            Assert.Contains("full footnote details after the marker", text);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task EncryptsAiApiKeyAtRestForCurrentWindowsUser()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var store = new AiSettingsStore(paths);
            const string secret = "sk-test-secret-value";
            await store.SaveAsync(new AiConnectionSettings
            {
                Provider = "deepseek",
                BaseUrl = "https://api.deepseek.com",
                Model = "deepseek-v4-flash",
                ApiKey = secret
            });

            var json = await File.ReadAllTextAsync(Path.Combine(paths.Data, "ai-settings.json"));
            var loaded = await store.LoadAsync();

            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
            Assert.Equal(secret, loaded.ApiKey);
            Assert.Equal("deepseek-v4-flash", loaded.Model);
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
