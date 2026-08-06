using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Kkindle.Infrastructure;

public sealed class AiConnectionSettings
{
    public string Provider { get; set; } = "deepseek";
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string Model { get; set; } = "deepseek-v4-flash";
    public string ApiKey { get; set; } = string.Empty;

    public bool IsConfigured =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out _)
        && !string.IsNullOrWhiteSpace(Model)
        && !string.IsNullOrWhiteSpace(ApiKey);

    public string ProviderDisplayName => Provider.ToLowerInvariant() switch
    {
        "openai" => "OpenAI",
        "custom" => "自定义 API",
        _ => "DeepSeek"
    };

    public AiConnectionSettings Clone() => new()
    {
        Provider = Provider,
        BaseUrl = BaseUrl,
        Model = Model,
        ApiKey = ApiKey
    };

    public static (string BaseUrl, string Model) GetDefaults(string provider) => provider.ToLowerInvariant() switch
    {
        "openai" => ("https://api.openai.com/v1", "gpt-5.6-sol"),
        "custom" => ("http://127.0.0.1:11434/v1", string.Empty),
        _ => ("https://api.deepseek.com", "deepseek-v4-flash")
    };
}

public sealed class AiSettingsStore
{
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public AiSettingsStore(AppPaths paths)
    {
        _paths = paths;
    }

    private string SettingsPath => Path.Combine(_paths.Data, "ai-settings.json");

    public async Task<AiConnectionSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        if (!File.Exists(SettingsPath)) return new AiConnectionSettings();
        try
        {
            await using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var persisted = await JsonSerializer.DeserializeAsync<PersistedAiSettings>(stream, _jsonOptions, cancellationToken);
            if (persisted is null) return new AiConnectionSettings();
            var provider = persisted.Provider?.Trim().ToLowerInvariant() ?? "deepseek";
            if (provider is not ("deepseek" or "openai" or "custom")) provider = "custom";
            var defaults = AiConnectionSettings.GetDefaults(provider);
            return new AiConnectionSettings
            {
                Provider = provider,
                BaseUrl = string.IsNullOrWhiteSpace(persisted.BaseUrl) ? defaults.BaseUrl : persisted.BaseUrl.Trim(),
                Model = string.IsNullOrWhiteSpace(persisted.Model) ? defaults.Model : persisted.Model.Trim(),
                ApiKey = string.IsNullOrWhiteSpace(persisted.ProtectedApiKey)
                    ? string.Empty
                    : Encoding.UTF8.GetString(WindowsDataProtection.Unprotect(Convert.FromBase64String(persisted.ProtectedApiKey)))
            };
        }
        catch (Exception exception) when (exception is IOException or JsonException or FormatException or System.ComponentModel.Win32Exception)
        {
            return new AiConnectionSettings();
        }
    }

    public async Task SaveAsync(AiConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        var persisted = new PersistedAiSettings
        {
            Provider = settings.Provider.Trim().ToLowerInvariant(),
            BaseUrl = settings.BaseUrl.Trim(),
            Model = settings.Model.Trim(),
            ProtectedApiKey = string.IsNullOrWhiteSpace(settings.ApiKey)
                ? string.Empty
                : Convert.ToBase64String(WindowsDataProtection.Protect(Encoding.UTF8.GetBytes(settings.ApiKey.Trim())))
        };
        var temporaryPath = SettingsPath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await JsonSerializer.SerializeAsync(stream, persisted, _jsonOptions, cancellationToken);
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    private sealed class PersistedAiSettings
    {
        public string? Provider { get; set; }
        public string? BaseUrl { get; set; }
        public string? Model { get; set; }
        public string? ProtectedApiKey { get; set; }
    }
}

public sealed record AiConversationTurn(string Role, string Content);

public sealed class AiChatClient : IDisposable
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(120) };

    public async Task<string> CompleteAsync(
        AiConnectionSettings settings,
        string instructions,
        string question,
        IReadOnlyList<AiConversationTurn> history,
        CancellationToken cancellationToken = default)
    {
        if (!settings.IsConfigured) throw new InvalidOperationException("请先配置 AI 服务、模型和 API Key。");
        return settings.Provider.Equals("openai", StringComparison.OrdinalIgnoreCase)
            ? await CompleteOpenAiResponsesAsync(settings, instructions, question, history, cancellationToken)
            : await CompleteChatCompletionsAsync(settings, instructions, question, history, cancellationToken);
    }

    private async Task<string> CompleteChatCompletionsAsync(
        AiConnectionSettings settings,
        string instructions,
        string question,
        IReadOnlyList<AiConversationTurn> history,
        CancellationToken cancellationToken)
    {
        var messages = new List<object> { new { role = "system", content = instructions } };
        messages.AddRange(history.TakeLast(8).Select(turn => (object)new
        {
            role = turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user",
            content = Limit(turn.Content, 3500)
        }));
        messages.Add(new { role = "user", content = question });
        var payload = JsonSerializer.Serialize(new { model = settings.Model, messages, stream = false });
        var endpoint = BuildEndpoint(settings.BaseUrl, "chat/completions");
        using var response = await SendAsync(endpoint, settings.ApiKey, payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body);
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String) return content.GetString()?.Trim() ?? string.Empty;
            if (content.ValueKind == JsonValueKind.Array)
            {
                return string.Join("", content.EnumerateArray()
                    .Where(item => item.TryGetProperty("text", out _))
                    .Select(item => item.GetProperty("text").GetString())).Trim();
            }
        }
        throw new InvalidDataException("AI 服务返回了无法识别的响应格式。");
    }

    private async Task<string> CompleteOpenAiResponsesAsync(
        AiConnectionSettings settings,
        string instructions,
        string question,
        IReadOnlyList<AiConversationTurn> history,
        CancellationToken cancellationToken)
    {
        var conversation = new StringBuilder();
        foreach (var turn in history.TakeLast(8))
        {
            conversation.Append(turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "助手：" : "读者：")
                .AppendLine(Limit(turn.Content, 3500));
        }
        conversation.Append("读者：").Append(question);
        var payload = JsonSerializer.Serialize(new
        {
            model = settings.Model,
            instructions,
            input = conversation.ToString()
        });
        var endpoint = BuildEndpoint(settings.BaseUrl, "responses");
        using var response = await SendAsync(endpoint, settings.ApiKey, payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body);
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("output_text", out var direct)
            && direct.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(direct.GetString()))
            return direct.GetString()!.Trim();

        if (document.RootElement.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                foreach (var contentItem in content.EnumerateArray())
                    if (contentItem.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        parts.Add(text.GetString() ?? string.Empty);
            }
            var combined = string.Join("\n", parts).Trim();
            if (combined.Length > 0) return combined;
        }
        throw new InvalidDataException("OpenAI 返回了无法识别的 Responses API 响应。");
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri endpoint,
        string apiKey,
        string payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Headers.UserAgent.ParseAdd("Kkindle/1.0");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    private static Uri BuildEndpoint(string baseUrl, string operation)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("API Base URL 必须是有效的 HTTP 或 HTTPS 地址。");
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith('/' + operation, StringComparison.OrdinalIgnoreCase)) return new Uri(trimmed);
        return new Uri($"{trimmed}/{operation}");
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body)
    {
        if (response.IsSuccessStatusCode) return;
        var message = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) message = error.GetString() ?? string.Empty;
                else if (error.TryGetProperty("message", out var detail)) message = detail.GetString() ?? string.Empty;
            }
        }
        catch (JsonException) { }
        if (string.IsNullOrWhiteSpace(message)) message = Limit(body, 500);
        throw new HttpRequestException($"AI 请求失败（{(int)response.StatusCode}）：{message}");
    }

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "…";

    public void Dispose() => _httpClient.Dispose();
}

internal static class WindowsDataProtection
{
    private const int CryptProtectUiForbidden = 0x1;

    public static byte[] Protect(byte[] value) => Transform(value, protect: true);
    public static byte[] Unprotect(byte[] value) => Transform(value, protect: false);

    private static byte[] Transform(byte[] value, bool protect)
    {
        if (value.Length == 0) return [];
        var input = CreateBlob(value);
        try
        {
            var succeeded = protect
                ? CryptProtectData(ref input, "Kkindle AI API Key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
            if (!succeeded) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, output.Length);
                return result;
            }
            finally
            {
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            }
        }
        finally
        {
            if (input.Data != IntPtr.Zero)
            {
                Marshal.Copy(new byte[value.Length], 0, input.Data, value.Length);
                Marshal.FreeHGlobal(input.Data);
            }
        }
    }

    private static DataBlob CreateBlob(byte[] value)
    {
        var pointer = Marshal.AllocHGlobal(value.Length);
        Marshal.Copy(value, 0, pointer, value.Length);
        return new DataBlob { Length = value.Length, Data = pointer };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
