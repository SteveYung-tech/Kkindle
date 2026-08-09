using System.IO.Compression;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class BackupTests
{
    [Fact]
    public async Task ExportsAndRestoresLibraryReaderDataAndSafeSettings()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourceBook = Path.Combine(root, "source.epub");
            CreateEpub(sourceBook);
            var sourcePaths = new AppPaths(Path.Combine(root, "source-app"));
            var sourceLibrary = new SqliteBookLibraryService(sourcePaths, new BookMetadataService());
            var sourceReaderData = new ReaderDataService(sourcePaths);
            await sourceLibrary.InitializeAsync();
            await sourceReaderData.InitializeAsync();
            await sourceLibrary.ImportAsync([sourceBook]);

            await new AiSettingsStore(sourcePaths).SaveAsync(new AiConnectionSettings
            {
                Provider = "openai",
                BaseUrl = "https://api.example.com/v1",
                Model = "example-model",
                ApiKey = "source-api-key"
            });
            await new KindleEmailSettingsStore(sourcePaths).SaveAsync(new KindleEmailSettings
            {
                KindleEmailAddress = "kindle@example.com",
                SenderEmailAddress = "sender@example.com",
                SmtpHost = "smtp.example.com",
                SmtpPort = 465,
                SmtpUsername = "sender@example.com",
                SmtpPassword = "source-smtp-password",
                EnableSsl = true
            });

            var backupPath = Path.Combine(root, "Kkindle.kkindle");
            var sourceBackup = new AppBackupService(sourcePaths);
            var export = await sourceBackup.ExportAsync(backupPath);

            Assert.Equal(1, export.BookCount);
            Assert.Equal(1, export.FileCount);
            Assert.True(export.ArchiveSize > 0);
            using (var archive = ZipFile.OpenRead(backupPath))
            {
                var settingsEntry = archive.GetEntry("settings/settings.json");
                Assert.NotNull(settingsEntry);
                using var reader = new StreamReader(settingsEntry!.Open());
                var settingsJson = await reader.ReadToEndAsync();
                Assert.DoesNotContain("source-api-key", settingsJson, StringComparison.Ordinal);
                Assert.DoesNotContain("source-smtp-password", settingsJson, StringComparison.Ordinal);
                Assert.Contains("example-model", settingsJson, StringComparison.Ordinal);
            }

            var targetPaths = new AppPaths(Path.Combine(root, "target-app"));
            var targetLibrary = new SqliteBookLibraryService(targetPaths, new BookMetadataService());
            var targetReaderData = new ReaderDataService(targetPaths);
            await targetLibrary.InitializeAsync();
            await targetReaderData.InitializeAsync();
            await new AiSettingsStore(targetPaths).SaveAsync(new AiConnectionSettings { ApiKey = "target-api-key" });
            await new KindleEmailSettingsStore(targetPaths).SaveAsync(new KindleEmailSettings
            {
                SmtpPassword = "target-smtp-password"
            });

            var imported = await new AppBackupService(targetPaths).ImportAsync(backupPath);
            await targetLibrary.InitializeAsync();
            await targetReaderData.InitializeAsync();

            var book = Assert.Single(await targetLibrary.SearchAsync());
            Assert.Equal("测试书", book.Title);
            Assert.True(File.Exists(targetLibrary.GetAbsoluteFilePath(book.Files[0])));
            Assert.Equal("openai", imported.AiSettings.Provider);
            Assert.Equal("target-api-key", imported.AiSettings.ApiKey);
            Assert.Equal("target-smtp-password", imported.KindleEmailSettings.SmtpPassword);
            Assert.Equal("smtp.example.com", imported.KindleEmailSettings.SmtpHost);
        }
        finally
        {
            TryDelete(root);
        }
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
              </metadata>
            </package>
            """);
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
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
