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

    /// <summary>模拟 qwen3-vl 系约束的 fake：拒绝宽/高 <=10 的探针图；可注入视觉调用异常。</summary>
    private sealed class VisionConstraintClient : ILlmClient
    {
        public bool ThrowOnVision { get; set; }
        public string VisionError { get; set; } = "vision failed";
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
        {
            if (request.ImageUrl != null)
            {
                if (ThrowOnVision) throw new LlmException(VisionError);
                // 解析 PNG IHDR 宽高（字节 16..23，大端），复刻 "height:1 or width:1 must be larger than 10"
                var png = Convert.FromBase64String(request.ImageUrl.Split(',')[1]);
                int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
                int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
                if (w <= 10 || h <= 10)
                    throw new LlmException($"height:{h} or width:{w} must be larger than 10");
            }
            return Task.FromResult(new ChatResponse("OK", new TokenUsage(3, 2), "m"));
        }
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "m" });
    }

    [Fact]
    public async Task Probe_Vision_ImageAcceptedByQwenVlConstraint()
    {
        // qwen3-vl 系拒绝 <=10px 图：探针图必须 16×16 才能 visionOk=true
        var result = await new LlmProbe(new VisionConstraintClient()).RunAsync(testVision: true);
        Assert.True(result.Connected);
        Assert.True(result.CompletionOk);
        Assert.True(result.VisionOk);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Probe_VisionFailure_KeepsCompletionSuccess()
    {
        // 部分结果保真：补全成功 + 视觉抛 → (true, true, false, 脱敏error, 补全usage)
        var client = new VisionConstraintClient { ThrowOnVision = true, VisionError = "vision endpoint 400" };
        var result = await new LlmProbe(client).RunAsync(testVision: true);
        Assert.True(result.Connected);
        Assert.True(result.CompletionOk);
        Assert.False(result.VisionOk);
        Assert.NotNull(result.Error);
        Assert.Equal(5, result.Usage!.PromptTokens + result.Usage.CompletionTokens);
    }

    [Fact]
    public async Task Probe_Failure_RedactsWorkspaceStyleKey()
    {
        // workspace 键含 . 与 -（假键样式）：脱敏正则必须覆盖，绝不回显密钥
        var client = new VisionConstraintClient
        {
            ThrowOnVision = true,
            VisionError = "invalid api key sk-ws-FAKE.KEY-123456789 rejected"
        };
        var result = await new LlmProbe(client).RunAsync(testVision: true);
        Assert.NotNull(result.Error);
        Assert.Contains("***", result.Error);
        Assert.DoesNotContain("sk-ws-FAKE.KEY-123456789", result.Error);
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
