using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class BookFormatConversionServiceTests
{
    [Fact]
    public async Task ThrowsWhenSourceBookDoesNotExist()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var service = new BookFormatConversionService();
            var missing = Path.Combine(root, "missing.epub");

            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                service.ConvertAsync(missing, Path.Combine(root, "out.pdf")));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task RejectsUnsupportedSourceOrTargetFormats()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "book.epub");
            var text = Path.Combine(root, "book.txt");
            await File.WriteAllTextAsync(epub, "epub");
            await File.WriteAllTextAsync(text, "text");
            var service = new BookFormatConversionService();

            await Assert.ThrowsAsync<NotSupportedException>(() =>
                service.ConvertAsync(epub, Path.Combine(root, "out.txt")));
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                service.ConvertAsync(text, Path.Combine(root, "out.epub")));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task RejectsSameSourceAndDestinationPath()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "book.epub");
            await File.WriteAllTextAsync(source, "epub");
            var service = new BookFormatConversionService();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ConvertAsync(source, source));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task RejectsExistingDestinationFile()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "book.epub");
            var destination = Path.Combine(root, "book.pdf");
            await File.WriteAllTextAsync(source, "epub");
            await File.WriteAllTextAsync(destination, "occupied");
            var service = new BookFormatConversionService();

            await Assert.ThrowsAsync<IOException>(() =>
                service.ConvertAsync(source, destination));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public void LocateExecutableHonorsKkindleCalibreConvertOverride()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var fakeExecutable = Path.Combine(root, "ebook-convert.exe");
            File.WriteAllText(fakeExecutable, "fake");
            var previous = Environment.GetEnvironmentVariable("KKINDLE_CALIBRE_CONVERT");
            Environment.SetEnvironmentVariable("KKINDLE_CALIBRE_CONVERT", fakeExecutable);
            try
            {
                Assert.Equal(
                    Path.GetFullPath(fakeExecutable),
                    BookFormatConversionService.LocateExecutable());
            }
            finally
            {
                Environment.SetEnvironmentVariable("KKINDLE_CALIBRE_CONVERT", previous);
            }
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }
}
