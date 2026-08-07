using System.Net;
using System.Text;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class AiModelTests
{
    [Fact]
    public async Task ListsModelsFromOpenAiCompatibleModelsEndpoint()
    {
        using var client = new AiChatClient(new StubHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/models", request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer sk-test", request.Headers.Authorization?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"object":"list","data":[{"id":"deepseek-v4-pro"},{"id":"deepseek-v4-flash"},{"id":"deepseek-v4-pro"}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }));

        var models = await client.ListModelsAsync(new AiConnectionSettings
        {
            BaseUrl = "https://api.deepseek.com",
            ApiKey = "sk-test"
        });

        Assert.Equal(["deepseek-v4-pro", "deepseek-v4-flash"], models);
    }

    [Fact]
    public void NormalizesDeprecatedDeepSeekModelNames()
    {
        Assert.Equal(
            "deepseek-v4-flash",
            AiConnectionSettings.NormalizeModel("deepseek", "deepseek-chat"));
        Assert.Equal(
            "deepseek-v4-flash",
            AiConnectionSettings.NormalizeModel("deepseek", "deepseek-reasoner"));
        Assert.DoesNotContain(
            "deepseek-chat",
            AiConnectionSettings.GetModelOptions("deepseek", "deepseek-v4-flash"));
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
