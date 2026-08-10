using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed class ZLibrarySettings
{
    public const string DefaultBaseUrl = "https://api.z-lib.org";

    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    public bool IsConfigured => Validate() is null;

    public ZLibrarySettings Clone() => new()
    {
        Email = Email,
        Password = Password,
        BaseUrl = BaseUrl
    };

    public static ZLibrarySettings Normalize(ZLibrarySettings settings)
    {
        var baseUrl = (settings.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (baseUrl.Length > 0 && !baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            baseUrl = "https://" + baseUrl;
        if (baseUrl.Length == 0) baseUrl = DefaultBaseUrl;
        return new ZLibrarySettings
        {
            Email = (settings.Email ?? string.Empty).Trim(),
            Password = settings.Password ?? string.Empty,
            BaseUrl = baseUrl
        };
    }

    public string? Validate()
    {
        if (!TryCreateAddress(Email)) return "请输入有效的 Z-Library 账号邮箱地址。";
        if (string.IsNullOrWhiteSpace(Password)) return "请输入 Z-Library 账号密码。";
        var baseUrl = Normalize(this).BaseUrl;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
            return "请输入有效的 Z-Library API 服务地址。";
        return null;
    }

    private static bool TryCreateAddress(string value)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(value) && new MailAddress(value).Address.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class ZLibrarySettingsStore
{
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public ZLibrarySettingsStore(AppPaths paths)
    {
        _paths = paths;
    }

    private string SettingsPath => Path.Combine(_paths.Data, "zlibrary-settings.json");

    public async Task<ZLibrarySettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        if (!File.Exists(SettingsPath)) return new ZLibrarySettings();

        try
        {
            await using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var persisted = await JsonSerializer.DeserializeAsync<PersistedZLibrarySettings>(stream, _jsonOptions, cancellationToken);
            if (persisted is null) return new ZLibrarySettings();

            return ZLibrarySettings.Normalize(new ZLibrarySettings
            {
                Email = persisted.Email ?? string.Empty,
                Password = string.IsNullOrWhiteSpace(persisted.ProtectedPassword)
                    ? string.Empty
                    : Encoding.UTF8.GetString(WindowsDataProtection.Unprotect(Convert.FromBase64String(persisted.ProtectedPassword))),
                BaseUrl = string.IsNullOrWhiteSpace(persisted.BaseUrl)
                    ? ZLibrarySettings.DefaultBaseUrl
                    : persisted.BaseUrl
            });
        }
        catch (Exception exception) when (exception is IOException
            or JsonException
            or FormatException
            or System.ComponentModel.Win32Exception
            or System.Security.Cryptography.CryptographicException)
        {
            return new ZLibrarySettings();
        }
    }

    public async Task SaveAsync(ZLibrarySettings settings, CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        var normalized = ZLibrarySettings.Normalize(settings);
        var persisted = new PersistedZLibrarySettings
        {
            Email = normalized.Email,
            ProtectedPassword = string.IsNullOrWhiteSpace(normalized.Password)
                ? string.Empty
                : Convert.ToBase64String(WindowsDataProtection.Protect(Encoding.UTF8.GetBytes(normalized.Password))),
            BaseUrl = normalized.BaseUrl
        };

        var temporaryPath = SettingsPath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await JsonSerializer.SerializeAsync(stream, persisted, _jsonOptions, cancellationToken);
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    private sealed class PersistedZLibrarySettings
    {
        public string? Email { get; set; }
        public string? ProtectedPassword { get; set; }
        public string? BaseUrl { get; set; }
    }
}

public sealed class ZLibraryService : IZLibraryService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private string _email = string.Empty;
    private string _password = string.Empty;
    private string _baseUrl = ZLibrarySettings.DefaultBaseUrl;
    private string _remixUserId = string.Empty;
    private string _remixUserKey = string.Empty;
    private string? _personalDomain;

    public bool IsLoggedIn => _remixUserId.Length > 0 && _remixUserKey.Length > 0;

    public ZLibraryService(HttpMessageHandler? handler = null)
    {
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromSeconds(120);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _loginLock.Dispose();
    }

    public async Task LoginAsync(
        string email,
        string password,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        await _loginLock.WaitAsync(cancellationToken);
        try
        {
            var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
            var form = new Dictionary<string, string>
            {
                ["email"] = (email ?? string.Empty).Trim(),
                ["password"] = password ?? string.Empty
            };
            using var request = CreateFormRequest(normalizedBaseUrl, "/eapi/user/login", form);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = ParseApiResponse(response, body);
            var root = document.RootElement;

            var userId = ReadFirstString(root, "remix-userid", "remix_userid", "remixUserId", "user_id");
            var userKey = ReadFirstString(root, "remix-userkey", "remix_userkey", "remixUserKey", "user_key");
            if (userId.Length == 0 || userKey.Length == 0)
                throw new InvalidDataException("登录响应中缺少用户凭证，请检查账号密码或服务地址。");

            _email = (email ?? string.Empty).Trim();
            _password = password ?? string.Empty;
            _baseUrl = normalizedBaseUrl;
            _remixUserId = userId;
            _remixUserKey = userKey;
            _personalDomain = FindPersonalDomain(root);
        }
        finally
        {
            _loginLock.Release();
        }
    }

    public async Task<ZLibrarySearchResult> SearchAsync(
        string query,
        int page = 1,
        int limit = 20,
        IReadOnlyList<string>? extensions = null,
        IReadOnlyList<string>? languages = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new ZLibrarySearchResult([], 0, 1, 0);
        if (!IsLoggedIn) await EnsureLoggedInAsync(cancellationToken);

        var form = new Dictionary<string, string>
        {
            ["message"] = query.Trim(),
            ["page"] = Math.Max(1, page).ToString(CultureInfo.InvariantCulture),
            ["limit"] = Math.Clamp(limit, 1, 100).ToString(CultureInfo.InvariantCulture),
            ["e"] = "0"
        };
        if (extensions is { Count: > 0 })
            foreach (var extension in extensions.Where(value => !string.IsNullOrWhiteSpace(value)))
                form.Add($"extensions[]", extension.Trim().ToLowerInvariant());
        if (languages is { Count: > 0 })
            foreach (var language in languages.Where(value => !string.IsNullOrWhiteSpace(value)))
                form.Add($"languages[]", language.Trim().ToLowerInvariant());

        using var request = CreateFormRequest(_baseUrl, "/eapi/book/search", form);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = ParseApiResponse(response, body);
        var root = document.RootElement;

        var books = new List<ZLibraryBook>();
        if (root.TryGetProperty("books", out var booksElement) && booksElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in booksElement.EnumerateArray())
            {
                if (!item.TryGetProperty("book", out var bookElement) || bookElement.ValueKind != JsonValueKind.Object)
                    continue;
                var id = GetInt64(item, "id", bookElement, "id");
                if (id <= 0) continue;
                books.Add(new ZLibraryBook
                {
                    Id = id,
                    Title = ReadString(bookElement, "title") ?? "未命名书籍",
                    Author = ReadString(bookElement, "author") ?? "未知作者",
                    Extension = (ReadString(bookElement, "extension") ?? string.Empty).Trim(),
                    Size = GetInt64(bookElement, "filesize", bookElement, "filesize"),
                    Language = ReadString(bookElement, "language") ?? string.Empty,
                    CoverUrl = ReadString(bookElement, "cover_url"),
                    Hash = ReadString(item, "hash") ?? string.Empty,
                    Year = GetInt32Nullable(bookElement, "year"),
                    Publisher = ReadString(bookElement, "publisher"),
                    Series = ReadString(bookElement, "series")
                });
            }
        }

        var total = (int)Math.Max(0, GetInt64(root, "total"));
        var pageCount = limit <= 0 ? 0 : (int)Math.Ceiling(total / (double)limit);
        return new ZLibrarySearchResult(books, total, Math.Max(1, page), pageCount);
    }

    public async Task<string?> GetDownloadUrlAsync(
        ZLibraryBook book,
        string preferredExtension,
        CancellationToken cancellationToken = default)
    {
        if (book.Id <= 0 || string.IsNullOrWhiteSpace(book.Hash)) return null;
        if (!IsLoggedIn) await EnsureLoggedInAsync(cancellationToken);

        var endpoint = $"/eapi/book/{book.Id}/{Uri.EscapeDataString(book.Hash)}/formats";
        using var request = CreateRequest(_baseUrl, endpoint);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = ParseApiResponse(response, body);
        var root = document.RootElement;

        if (!root.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array)
            return null;

        string? fallback = null;
        foreach (var format in formats.EnumerateArray())
        {
            var extension = ReadString(format, "extension") ?? string.Empty;
            var url = ReadString(format, "download_url");
            if (string.IsNullOrWhiteSpace(url)) continue;
            fallback ??= url;
            if (extension.Equals(preferredExtension, StringComparison.OrdinalIgnoreCase))
                return RewriteDownloadHost(url);
        }
        return fallback is null ? null : RewriteDownloadHost(fallback);
    }

    public async Task<string> DownloadAsync(
        ZLibraryBook book,
        string destinationDirectory,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var url = await GetDownloadUrlAsync(book, book.Extension, cancellationToken);
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("未找到可用的下载地址，可能是会员额度限制或文件已下架。");

        Directory.CreateDirectory(destinationDirectory);
        var fileName = GetUniqueFileName(destinationDirectory, book);
        var destinationPath = Path.Combine(destinationDirectory, fileName);
        var temporaryPath = destinationPath + ".part";
        try
        {
            using var request = CreateRequest(url, null);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"下载失败（HTTP {(int)response.StatusCode}）。");
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                using var errorDocument = ParseApiResponse(response, errorBody);
                throw new InvalidOperationException("下载被服务拒绝，请检查账号状态或会员额度。");
            }

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[81920];
            long copied = 0;
            await using (var destination = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read <= 0) break;
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    copied += read;
                    progress?.Report(new TransferProgress(copied, totalBytes, $"正在下载 {fileName}"));
                }
                await destination.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, destinationPath, overwrite: true);
            return destinationPath;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private async Task EnsureLoggedInAsync(CancellationToken cancellationToken)
    {
        if (IsLoggedIn) return;
        if (string.IsNullOrWhiteSpace(_email) || string.IsNullOrWhiteSpace(_password))
            throw new InvalidOperationException("尚未登录 Z-Library，请先在账号设置中配置账号。");
        await LoginAsync(_email, _password, _baseUrl, cancellationToken);
    }

    private HttpRequestMessage CreateFormRequest(string baseUrl, string endpoint, IReadOnlyDictionary<string, string> form)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl.TrimEnd('/')), endpoint));
        request.Content = new FormUrlEncodedContent(form);
        request.Headers.UserAgent.ParseAdd("Kkindle/1.0");
        if (IsLoggedIn)
        {
            request.Headers.Add("remix-userid", _remixUserId);
            request.Headers.Add("remix-userkey", _remixUserKey);
        }
        return request;
    }

    private HttpRequestMessage CreateRequest(string baseUrl, string? endpoint)
    {
        var url = string.IsNullOrEmpty(endpoint) ? baseUrl : new Uri(new Uri(baseUrl.TrimEnd('/')), endpoint).ToString();
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Kkindle/1.0");
        if (IsLoggedIn)
        {
            request.Headers.Add("remix-userid", _remixUserId);
            request.Headers.Add("remix-userkey", _remixUserKey);
        }
        return request;
    }

    private static JsonDocument ParseApiResponse(HttpResponseMessage response, string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"Z-Library 服务返回了无法识别的响应（HTTP {(int)response.StatusCode}）。");
        }

        if (document.RootElement.TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.False)
        {
            var error = ReadString(document.RootElement, "error")
                ?? ReadString(document.RootElement, "message")
                ?? "未知错误";
            throw new InvalidOperationException($"Z-Library 请求失败：{error}");
        }
        return document;
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        var normalized = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (normalized.Length == 0) return ZLibrarySettings.DefaultBaseUrl;
        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            normalized = "https://" + normalized;
        return normalized;
    }

    private string? FindPersonalDomain(JsonElement root)
    {
        if (root.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            var fromUser = ReadString(user, "personal_domain");
            if (!string.IsNullOrWhiteSpace(fromUser)) return fromUser.Trim();
        }
        var fromRoot = ReadString(root, "personal_domain");
        return string.IsNullOrWhiteSpace(fromRoot) ? null : fromRoot.Trim();
    }

    private string RewriteDownloadHost(string url)
    {
        if (string.IsNullOrWhiteSpace(_personalDomain)) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out var baseUri)) return url;
        if (!uri.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase)) return url;

        var personalDomain = _personalDomain!.Trim().TrimEnd('/');
        return personalDomain.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || personalDomain.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? $"{personalDomain}{uri.PathAndQuery}"
                : $"https://{personalDomain}{uri.PathAndQuery}";
    }

    private static string GetUniqueFileName(string directory, ZLibraryBook book)
    {
        var extension = string.IsNullOrWhiteSpace(book.Extension) ? "epub" : book.Extension.Trim().TrimStart('.');
        var baseName = SanitizeFileName(book.Title);
        if (baseName.Length == 0) baseName = $"book-{book.Id}";
        var candidate = $"{baseName}.{extension}";
        var index = 2;
        while (File.Exists(Path.Combine(directory, candidate)))
        {
            candidate = $"{baseName}-{index}.{extension}";
            index++;
        }
        return candidate;
    }

    private static string SanitizeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(title.Trim()
            .Select(character => invalid.Contains(character) ? ' ' : character)
            .ToArray())
            .Trim();
        return cleaned.Length > 80 ? cleaned[..80].Trim() : cleaned;
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string ReadFirstString(JsonElement element, params string[] properties)
    {
        foreach (var property in properties)
        {
            var value = ReadString(element, property);
            if (value is not null) return value;
        }
        return string.Empty;
    }

    private static long GetInt64(JsonElement element, string property, JsonElement fallbackElement, string fallbackProperty)
    {
        var value = GetInt64Core(element, property);
        if (value is not null) return value.Value;
        value = GetInt64Core(fallbackElement, fallbackProperty);
        return value ?? 0;
    }

    private static long GetInt64(JsonElement element, string property)
    {
        var value = GetInt64Core(element, property);
        return value ?? 0;
    }

    private static long? GetInt64Core(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        return null;
    }

    private static int? GetInt32Nullable(JsonElement element, string property)
    {
        var value = GetInt64Core(element, property);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    }
}
