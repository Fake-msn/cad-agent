using System.Net;
using CadAgent.Core;
using CadAgent.Llm;

namespace CadAgent.Llm.Tests;

public class ProbeTests
{
    private sealed class FakeClient : ILlmClient
    {
        public ChatRequest? Last;
        public bool Throw { get; set; }
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
        {
            Last = request;
            if (Throw) throw new LlmException("connection refused sk-secret12345678");
            return Task.FromResult(new ChatResponse("OK", new TokenUsage(3, 2), "m"));
        }
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "m" });
    }

    [Fact]
    public async Task Probe_CompletionOk_UsageRecorded()
    {
        var client = new FakeClient();
        var result = await new LlmProbe(client).RunAsync(testVision: false);
        Assert.True(result.Connected);
        Assert.True(result.CompletionOk);
        Assert.Null(result.VisionOk);
        Assert.Null(result.Error);
        Assert.Equal(5, result.Usage!.PromptTokens + result.Usage.CompletionTokens);
    }

    [Fact]
    public async Task Probe_Failure_RedactedError()
    {
        var result = await new LlmProbe(new FakeClient { Throw = true }).RunAsync(testVision: false);
        Assert.False(result.Connected);
        Assert.False(result.CompletionOk);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("sk-secret12345678", result.Error);
    }

    [Fact]
    public async Task Probe_Vision_SendsImageUrl()
    {
        var client = new FakeClient();
        var result = await new LlmProbe(client).RunAsync(testVision: true);
        Assert.True(result.VisionOk);
        Assert.NotNull(client.Last!.ImageUrl);
        Assert.StartsWith("data:image/png;base64,", client.Last.ImageUrl);
    }

    private sealed class MockHandler : HttpMessageHandler
    {
        public string Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"choices":[{"message":{"content":"OK"}}],"usage":{"prompt_tokens":1,"completion_tokens":1},"model":"m"}
                    """)
            };
        }
    }

    [Fact]
    public async Task OpenAiClient_ImageUrl_ContentArray()
    {
        var h = new MockHandler();
        var profile = new ProviderProfile("t", "https://api.test/v1", "m", true, DataPolicy.CloudAllowed, new ProfileLimits());
        var client = new OpenAiClient(profile, "k", h);
        await client.ChatAsync(new ChatRequest(new[] { new ChatMessage("user", "看图") }, 16, ImageUrl: "data:image/png;base64,AAAA"));
        Assert.Contains("image_url", h.Body);
        Assert.Contains("data:image/png;base64,AAAA", h.Body);
        Assert.Contains("\"type\":\"text\"", h.Body);
    }
}