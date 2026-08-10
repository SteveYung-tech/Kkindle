using System.Net;
using System.Text;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class ZLibraryTests
{
    [Fact]
    public async Task LoginParsesCredentialsAndPersonalDomain()
    {
        using var service = new ZLibraryService(new StubHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/eapi/user/login", request.RequestUri?.AbsolutePath);
            var body = request.Content?.ReadAsStringAsync().Result ?? string.Empty;
            Assert.Contains("email=user%40example.com", body);
            Assert.Contains("password=secret", body);
            return JsonResponse("""{"success":true,"token":"abc","remix-userid":"12345","remix-userkey":"abcdef","user":{"personal_domain":"user.zlib.example.com"}}""");
        }));

        await service.LoginAsync("user@example.com", "secret", "https://api.z-lib.org");

        Assert.True(service.IsLoggedIn);
    }

    [Fact]
    public async Task SearchParsesBooksTotalAndPageCount()
    {
        using var service = new ZLibraryService(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/eapi/user/login")
                return JsonResponse("""{"success":true,"remix-userid":"12345","remix-userkey":"abcdef"}""");
            Assert.Equal("/eapi/book/search", request.RequestUri?.AbsolutePath);
            var body = request.Content?.ReadAsStringAsync().Result ?? string.Empty;
            Assert.Contains("message=The+Hobbit", body);
            Assert.Contains("extensions%5B%5D=epub", body);
            Assert.Contains("languages%5B%5D=english", body);
            return JsonResponse("""
                {"success":true,"total":42,"books":[
                  {"id":1001,"book":{"id":1001,"title":"The Hobbit","author":"J.R.R. Tolkien","cover_url":"https://cover.example.com/1.jpg","language":"english","extension":"epub","filesize":5242880,"year":1937,"publisher":"Allen & Unwin"},"hash":"hash1001"}
                ]}
                """);
        }));

        await service.LoginAsync("user@example.com", "secret", "https://api.z-lib.org");
        var result = await service.SearchAsync(
            "The Hobbit",
            extensions: ["epub"],
            languages: ["english"]);

        Assert.Equal(42, result.Total);
        Assert.Equal(3, result.PageCount);
        var book = Assert.Single(result.Books);
        Assert.Equal(1001, book.Id);
        Assert.Equal("The Hobbit", book.Title);
        Assert.Equal("J.R.R. Tolkien", book.Author);
        Assert.Equal("epub", book.Extension);
        Assert.Equal(5242880, book.Size);
        Assert.Equal("hash1001", book.Hash);
        Assert.Equal("5.0 MB", book.SizeLabel);
    }

    [Fact]
    public async Task FormatsEndpointPicksPreferredExtensionAndRewritesHost()
    {
        using var service = new ZLibraryService(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/eapi/user/login")
                return JsonResponse("""{"success":true,"remix-userid":"12345","remix-userkey":"abcdef","user":{"personal_domain":"user.zlib.example.com"}}""");
            Assert.Equal("/eapi/book/1001/hash1001/formats", request.RequestUri?.AbsolutePath);
            return JsonResponse("""{"success":true,"formats":[{"extension":"mobi","download_url":"https://api.z-lib.org/d/1.mobi"},{"extension":"epub","download_url":"https://api.z-lib.org/d/2.epub"}]}""");
        }));

        await service.LoginAsync("user@example.com", "secret", "https://api.z-lib.org");
        var url = await service.GetDownloadUrlAsync(
            new ZLibraryBook { Id = 1001, Hash = "hash1001" },
            "epub");

        Assert.Equal("https://user.zlib.example.com/d/2.epub", url);
    }

    [Fact]
    public async Task DownloadWritesFileAndReportsProgress()
    {
        var payload = Encoding.UTF8.GetBytes("fake-ebook-content");
        var service = new ZLibraryService(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/eapi/user/login")
                return JsonResponse("""{"success":true,"remix-userid":"12345","remix-userkey":"abcdef"}""");
            if (request.RequestUri?.AbsolutePath.EndsWith("/formats", StringComparison.Ordinal) == true)
                return JsonResponse("""{"success":true,"formats":[{"extension":"epub","download_url":"https://api.z-lib.org/d/book.epub"}]}""");
            Assert.Equal("/d/book.epub", request.RequestUri?.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
        }));
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        try
        {
            await service.LoginAsync("user@example.com", "secret", "https://api.z-lib.org");
            var progress = new List<TransferProgress>();
            var destination = await service.DownloadAsync(
                new ZLibraryBook { Id = 1001, Hash = "hash1001", Title = "The Hobbit", Extension = "epub" },
                root,
                new Progress<TransferProgress>(progress.Add));

            var fileName = Path.GetFileName(destination);
            Assert.StartsWith("The Hobbit", fileName);
            Assert.EndsWith(".epub", fileName);
            Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
            Assert.True(progress.Count > 0);
            Assert.Equal(payload.Length, progress[^1].BytesCopied);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ApiErrorThrowsWithServerMessage()
    {
        using var service = new ZLibraryService(new StubHttpMessageHandler(_ =>
            JsonResponse("""{"success":false,"error":"daily download limit exceeded"}""")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LoginAsync("user@example.com", "secret", "https://api.z-lib.org"));
        Assert.Contains("daily download limit exceeded", exception.Message);
    }

    [Fact]
    public async Task SettingsStoreEncryptsPasswordAtRest()
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            var store = new ZLibrarySettingsStore(paths);
            const string secret = "zlibrary-password";
            await store.SaveAsync(new ZLibrarySettings
            {
                Email = "user@example.com",
                Password = secret,
                BaseUrl = "https://api.z-lib.org"
            });

            var json = await File.ReadAllTextAsync(Path.Combine(paths.Data, "zlibrary-settings.json"));
            var loaded = await store.LoadAsync();

            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
            Assert.Equal("user@example.com", loaded.Email);
            Assert.Equal(secret, loaded.Password);
            Assert.Equal("https://api.z-lib.org", loaded.BaseUrl);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ValidatesSettingsAndNormalizesBaseUrl()
    {
        var settings = new ZLibrarySettings { Email = "user@example.com", Password = "secret", BaseUrl = " api.z-lib.org " };
        Assert.Null(settings.Validate());
        var normalized = ZLibrarySettings.Normalize(settings);
        Assert.Equal("https://api.z-lib.org", normalized.BaseUrl);

        Assert.NotNull(new ZLibrarySettings { Email = "not-an-email", Password = "x" }.Validate());
        Assert.NotNull(new ZLibrarySettings { Email = "user@example.com", Password = "" }.Validate());
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
