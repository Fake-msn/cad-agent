using System.Net;
using System.Text.Json;
using CadAgent.Core;
using CadAgent.Llm;

namespace CadAgent.Llm.Tests;

public class OpenAiClientTests
{
    private sealed class MockHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        public string Body = "";
        public HttpStatusCode Code = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(Code) { Content = new StringContent(ResponseJson) };
        }
        public string ResponseJson = """
            {"choices":[{"message":{"content":"你好"}}],"usage":{"prompt_tokens":10,"completion_tokens":5},"model":"model-x"}
            """;
    }

    private static ProviderProfile P() => new("t", "https://api.test/v1/", "model-x", false, DataPolicy.CloudAllowed, new ProfileLimits());

    [Fact]
    public async Task Chat_ParsesContentAndUsage_SendsBearer()
    {
        var h = new MockHandler();
        var client = new OpenAiClient(P(), "sk-key", h);
        var resp = await client.ChatAsync(new ChatRequest(new[] { new ChatMessage("user", "hi") }, 100));
        Assert.Equal("你好", resp.Content);
        Assert.Equal(10, resp.Usage.PromptTokens);
        Assert.Equal(5, resp.Usage.CompletionTokens);
        Assert.Equal("Bearer", h.Last!.Headers.Authorization!.Scheme);
        Assert.Equal("sk-key", h.Last.Headers.Authorization.Parameter);
        Assert.Contains("\"max_tokens\":100", h.Body);
        Assert.EndsWith("/chat/completions", h.Last.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Chat_401_ThrowsAuth_NoKeyInMessage()
    {
        var h = new MockHandler { Code = HttpStatusCode.Unauthorized, ResponseJson = "{\"error\":\"bad key\"}" };
        var client = new OpenAiClient(P(), "sk-supersecret", h);
        var ex = await Assert.ThrowsAsync<LlmAuthException>(() => client.ChatAsync(new ChatRequest(new[] { new ChatMessage("user", "hi") }, 10)));
        Assert.DoesNotContain("sk-supersecret", ex.Message);
    }

    [Fact]
    public async Task ListModels_ParsesIds()
    {
        var h = new MockHandler { ResponseJson = """{"data":[{"id":"m1"},{"id":"m2"}]}""" };
        var client = new OpenAiClient(P(), "k", h);
        var models = await client.ListModelsAsync();
        Assert.Equal(new[] { "m1", "m2" }, models);
    }

    [Fact]
    public async Task Chat_500_ThrowsLlmException()
    {
        var h = new MockHandler { Code = HttpStatusCode.InternalServerError, ResponseJson = "{\"error\":\"boom\"}" };
        var client = new OpenAiClient(P(), "k", h);
        await Assert.ThrowsAsync<LlmException>(() => client.ChatAsync(new ChatRequest(new[] { new ChatMessage("user", "hi") }, 10)));
    }

    [Fact]
    public async Task Endpoint_TrailingSlash_Normalized()
    {
        var h = new MockHandler { ResponseJson = """{"data":[{"id":"m1"}]}""" };
        var client = new OpenAiClient(P(), "k", h);
        await client.ListModelsAsync();
        Assert.Equal("https://api.test/v1/models", h.Last!.RequestUri!.AbsoluteUri);
    }
}