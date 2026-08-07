using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class ReaderBookSelectionTests
{
    [Fact]
    public void PrefersEpubRegardlessOfLibraryFileOrder()
    {
        var pdf = new BookFile { Format = "pdf" };
        var epub = new BookFile { Format = "EPUB" };

        Assert.Same(epub, ReaderBookSelectionPolicy.SelectPreferred([pdf, epub]));
    }

    [Fact]
    public void FallsBackToPdfWhenEpubIsMissing()
    {
        var mobi = new BookFile { Format = "mobi" };
        var pdf = new BookFile { Format = "pdf" };

        Assert.Same(pdf, ReaderBookSelectionPolicy.SelectPreferred([mobi, pdf]));
    }

    [Fact]
    public void ReturnsNullWhenNoReaderFormatExists()
    {
        Assert.Null(ReaderBookSelectionPolicy.SelectPreferred([
            new BookFile { Format = "mobi" },
            new BookFile { Format = "azw3" }
        ]));
        Assert.Null(ReaderBookSelectionPolicy.SelectPreferred(null));
    }
}
