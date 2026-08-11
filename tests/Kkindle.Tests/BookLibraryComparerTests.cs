using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class BookLibraryComparerTests
{
    [Fact]
    public void MatchesBooksByFileHashEvenWhenMetadataDiffers()
    {
        var local = CreateBook("电脑标题", "电脑作者", "ABC123");
        var kindle = CreateKindleBook("Kindle title", "Kindle author", "abc123", "documents/book.azw3");

        var result = BookLibraryComparer.Compare([local], [kindle]);

        Assert.Contains(local.Id, result.BooksOnKindle);
        Assert.Contains(kindle.RelativePath, result.KindleBooksOnComputer);
    }

    [Fact]
    public void MatchesConvertedCopiesByNormalizedTitleAndAuthors()
    {
        var local = CreateBook(" 三体 ", "刘慈欣; 译者", "LOCAL");
        var kindle = CreateKindleBook("三 体", "译者，刘慈欣", "DEVICE", "documents/three-body.azw3");

        var result = BookLibraryComparer.Compare([local], [kindle]);

        Assert.Contains(local.Id, result.BooksOnKindle);
        Assert.Contains(kindle.RelativePath, result.KindleBooksOnComputer);
    }

    [Fact]
    public void KeepsUnmatchedBooksInTheirOwnLibraries()
    {
        var local = CreateBook("本地书", "作者甲", "LOCAL");
        var kindle = CreateKindleBook("设备书", "作者乙", "DEVICE", "documents/device.epub");

        var result = BookLibraryComparer.Compare([local], [kindle]);

        Assert.DoesNotContain(local.Id, result.BooksOnKindle);
        Assert.DoesNotContain(kindle.RelativePath, result.KindleBooksOnComputer);
    }

    [Fact]
    public void DoesNotMatchUnknownAuthorsByTitleAlone()
    {
        var local = CreateBook("同名书", "未知作者", "LOCAL");
        var kindle = CreateKindleBook("同名书", "未知作者", "DEVICE", "documents/other.epub");

        var result = BookLibraryComparer.Compare([local], [kindle]);

        Assert.Empty(result.BooksOnKindle);
        Assert.Empty(result.KindleBooksOnComputer);
    }

    private static Book CreateBook(string title, string authors, string hash) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Authors = authors,
        Files = [new BookFile { Sha256 = hash, Format = "epub" }]
    };

    private static KindleBook CreateKindleBook(
        string title,
        string authors,
        string hash,
        string relativePath) => new()
    {
        Title = title,
        Authors = authors,
        Sha256 = hash,
        RelativePath = relativePath,
        Format = Path.GetExtension(relativePath).TrimStart('.')
    };
}
