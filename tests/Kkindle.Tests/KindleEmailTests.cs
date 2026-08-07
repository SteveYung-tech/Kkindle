using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class KindleEmailTests
{
    [Fact]
    public void SelectsEpubBeforePdfAndRejectsUnsupportedFormats()
    {
        var bookId = Guid.NewGuid();
        var files = new[]
        {
            new BookFile { BookId = bookId, Format = "mobi" },
            new BookFile { BookId = bookId, Format = "PDF" },
            new BookFile { BookId = bookId, Format = "EPUB" }
        };

        var selected = KindleEmailSelectionPolicy.SelectPreferred(files);

        Assert.NotNull(selected);
        Assert.Equal("EPUB", selected!.Format);
        Assert.False(KindleEmailSelectionPolicy.IsSupportedFormat("mobi"));
    }

    [Fact]
    public void ValidatesSmtpSettingsAndNormalizesWhitespace()
    {
        var settings = new KindleEmailSettings
        {
            KindleEmailAddress = " kindle@example.com ",
            SenderEmailAddress = " sender@example.com ",
            SmtpHost = " smtp.example.com ",
            SmtpPort = 587,
            SmtpUsername = " sender@example.com ",
            SmtpPassword = "app-password"
        };

        Assert.Null(settings.Validate());
        var normalized = KindleEmailSettings.Normalize(settings);
        Assert.Equal("kindle@example.com", normalized.KindleEmailAddress);
        Assert.Equal("smtp.example.com", normalized.SmtpHost);
        Assert.Equal("app-password", normalized.SmtpPassword);
    }

    [Fact]
    public async Task EncryptsSmtpPasswordAtRest()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var store = new KindleEmailSettingsStore(paths);
            const string secret = "smtp-app-password";
            await store.SaveAsync(new KindleEmailSettings
            {
                KindleEmailAddress = "kindle@example.com",
                SenderEmailAddress = "sender@example.com",
                SmtpHost = "smtp.example.com",
                SmtpPort = 587,
                SmtpUsername = "sender@example.com",
                SmtpPassword = secret
            });

            var json = await File.ReadAllTextAsync(Path.Combine(paths.Data, "kindle-email-settings.json"));
            var loaded = await store.LoadAsync();

            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
            Assert.Equal(secret, loaded.SmtpPassword);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch { }
        }
    }
}
