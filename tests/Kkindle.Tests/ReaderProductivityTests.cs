using System.IO.Compression;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class ReaderProductivityTests
{
    [Fact]
    public async Task SavesAndRestoresReadingProgress()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new ReaderDataService(paths);
            await service.InitializeAsync();
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();

            await service.SaveProgressAsync(new ReaderProgressRow(
                bookId,
                fileId,
                "text/chapter-2.xhtml",
                "part-3",
                ChapterIndex: 1,
                ScrollPosition: 4820,
                ProgressPercent: 23,
                FlowMode: 0,
                UpdatedAt: DateTimeOffset.UtcNow));

            var restored = await service.GetProgressAsync(fileId);
            Assert.NotNull(restored);
            Assert.Equal(bookId, restored!.BookId);
            Assert.Equal(1, restored.ChapterIndex);
            Assert.Equal(4820, restored.ScrollPosition);
            Assert.Equal("part-3", restored.Fragment);
            Assert.Equal(0, restored.FlowMode);

            // Overwrite on the same file and make sure the row updates in place.
            await service.SaveProgressAsync(restored with
            {
                ChapterIndex = 2,
                ScrollPosition = 900,
                ProgressPercent = 30,
                FlowMode = 1
            });
            var updated = await service.GetProgressAsync(fileId);
            Assert.Equal(2, updated!.ChapterIndex);
            Assert.Equal(900, updated.ScrollPosition);
            Assert.Equal(1, updated.FlowMode);
            Assert.Null(await service.GetProgressAsync(Guid.NewGuid()));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task AddsDeletesAndListsBookmarks()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new ReaderDataService(paths);
            await service.InitializeAsync();
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();

            var first = new ReaderBookmark
            {
                BookId = bookId,
                BookFileId = fileId,
                ChapterPath = "text/chapter-1.xhtml",
                Fragment = null,
                ChapterIndex = 0,
                Title = "第一章",
                Quote = "规模法则描述系统尺度变化"
            };
            var second = new ReaderBookmark
            {
                BookId = bookId,
                BookFileId = fileId,
                ChapterPath = "text/chapter-3.xhtml",
                Fragment = "section-2",
                ChapterIndex = 2,
                Title = "第三章",
                Quote = "城市人口和基础设施"
            };
            await service.SaveBookmarkAsync(first);
            await service.SaveBookmarkAsync(second);

            var list = await service.GetBookmarksAsync(fileId);
            Assert.Equal(2, list.Count);
            Assert.Contains(list, bookmark => bookmark.Quote.Contains("城市人口", StringComparison.Ordinal));

            await service.DeleteBookmarkAsync(second.Id);
            var remaining = await service.GetBookmarksAsync(fileId);
            Assert.Single(remaining);
            Assert.Equal("第一章", remaining[0].Title);

            // Another book's bookmarks are never mixed in.
            var other = await service.GetBookmarksAsync(Guid.NewGuid());
            Assert.Empty(other);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task PersistsPerBookLayoutSettingsAndDefaultsAreRestored()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new ReaderDataService(paths);
            await service.InitializeAsync();
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();

            Assert.Null(await service.GetLayoutSettingsAsync(fileId));

            var settings = new ReaderLayoutSettings(
                FontScale: 1.2,
                LineHeight: 2.1,
                MaxWidth: 960,
                BodyPadding: 96,
                FontFamily: "SimSun",
                FlowMode: 1,
                VerticalWriting: true);
            await service.SaveLayoutSettingsAsync(bookId, fileId, settings);

            var restored = await service.GetLayoutSettingsAsync(fileId);
            Assert.NotNull(restored);
            Assert.Equal(1.2, restored!.FontScale);
            Assert.Equal(2.1, restored.LineHeight);
            Assert.Equal(960, restored.MaxWidth);
            Assert.Equal("SimSun", restored.FontFamily);
            Assert.True(restored.VerticalWriting);

            // A book with no saved settings still resolves to the default record.
            Assert.Null(await service.GetLayoutSettingsAsync(Guid.NewGuid()));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task AccumulatesReadingTimeWithoutLosingExistingStats()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new ReaderDataService(paths);
            await service.InitializeAsync();
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();

            await service.AddReadingTimeAsync(bookId, fileId, activeSeconds: 95, 12, completedChapters: 2, totalChapters: 10);
            await service.AddReadingTimeAsync(bookId, fileId, activeSeconds: 30, 18, completedChapters: 3, totalChapters: 10);

            var stats = await service.GetReadingStatsAsync(fileId);
            Assert.NotNull(stats);
            Assert.Equal(125, stats!.CumulativeSeconds);
            Assert.Equal(18, stats.ProgressPercent);
            Assert.Equal(3, stats.CompletedChapters);

            // Zero-length sessions are ignored so no bogus rows are created.
            await service.AddReadingTimeAsync(bookId, fileId, activeSeconds: 0, 18, 3, 10);
            var after = await service.GetReadingStatsAsync(fileId);
            Assert.Equal(125, after!.CumulativeSeconds);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task SearchesIndexedBookAndUsesLikeFallbackForShortTerms()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var service = new ReaderDataService(paths);
            await service.InitializeAsync();
            var bookId = Guid.NewGuid();
            var fileId = Guid.NewGuid();
            var hash = new string('e', 64);

            await service.ReplaceBookChunksAsync(bookId, fileId, hash,
            [
                new BookContentChunkDraft(0, 0, "第一章", "text/one.xhtml", 0, 40,
                    "规模法则描述系统尺度变化时仍然保持的数量关系。"),
                new BookContentChunkDraft(1, 0, "第二章", "text/two.xhtml", 0, 30,
                    "城市人口和基础设施之间存在可测量的统计关系。")
            ]);

            // Long CJK term: FTS/trigram should find the relevant chapter.
            var results = await service.SearchBookAsync(bookId, "城市人口和基础设施");
            Assert.Contains(results, chunk => chunk.ChapterTitle == "第二章");

            // Short term (1-2 characters) falls back to LIKE and still works.
            var fallback = await service.SearchBookAsync(bookId, "规模");
            Assert.Contains(fallback, chunk => chunk.ChapterTitle == "第一章");
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void BuildsMarkdownAndPlainTextAnnotationExports()
    {
        var annotations = new[]
        {
            new ReaderAnnotation
            {
                Id = Guid.NewGuid(),
                BookId = Guid.NewGuid(),
                BookFileId = Guid.NewGuid(),
                ChapterPath = "text/one.xhtml",
                StartOffset = 10,
                EndOffset = 20,
                SelectedText = "规模法则描述系统",
                Prefix = "本书提出",
                Suffix = "尺度变化",
                Note = "关键定义",
                CreatedAt = new DateTimeOffset(2026, 8, 7, 4, 0, 0, TimeSpan.Zero)
            }
        };
        string? chapterTitle = null;
        string Resolve(string path)
        {
            chapterTitle = "第一章";
            return chapterTitle;
        }

        var markdown = ReaderAnnotationExport.BuildMarkdown("规模与规律", "测试作者", annotations, Resolve);
        var plain = ReaderAnnotationExport.BuildPlainText("规模与规律", "测试作者", annotations, Resolve);

        Assert.Contains("# 规模与规律", markdown);
        Assert.Contains("作者：测试作者", markdown);
        Assert.Contains("## 第一章", markdown);
        Assert.Contains("> 规模法则描述系统", markdown);
        Assert.Contains("关键定义", markdown);
        Assert.Contains("2026-08-07", markdown);
        Assert.Contains("text/one.xhtml（偏移 10–20）", markdown);

        Assert.Contains("规模与规律", plain);
        Assert.Contains("[1] 第一章", plain);
        Assert.Contains("关键定义", plain);
        Assert.DoesNotContain("##", plain);

        // Empty annotation list still produces a valid, explicit document.
        var empty = ReaderAnnotationExport.BuildMarkdown("空书", "作者", [], null);
        Assert.Contains("暂无划线与批注", empty);
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
