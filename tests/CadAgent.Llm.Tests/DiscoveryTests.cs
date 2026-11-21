using System.Net;
using CadAgent.Core;
using CadAgent.Llm;

namespace CadAgent.Llm.Tests;

public class DiscoveryTests
{
    private sealed class FakeProbe : IEndpointProbe
    {
        public HashSet<string> Reachable { get; } = new();
        public Task<bool> IsReachableAsync(string baseUrl, CancellationToken ct = default) =>
            Task.FromResult(Reachable.Contains(baseUrl));
    }

    private sealed class FakeClient : ILlmClient
    {
        public IReadOnlyList<string> Models { get; set; } = Array.Empty<string>();
        public bool Throw { get; set; }
        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse("ok", new TokenUsage(1, 1), "m"));
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Throw ? Task.FromException<IReadOnlyList<string>>(new LlmException("down")) : Task.FromResult(Models);
    }

    [Fact]
    public async Task DiscoverModels_Success_ReturnsList()
    {
        var client = new FakeClient { Models = new[] { "a", "b" } };
        Assert.Equal(new[] { "a", "b" }, await Discovery.DiscoverModelsAsync(client));
    }

    [Fact]
    public async Task DiscoverModels_ClientThrows_EmptyNoThrow()
    {
        var client = new FakeClient { Throw = true };
        Assert.Empty(await Discovery.DiscoverModelsAsync(client));
    }

    [Fact]
    public async Task DetectLocalOllama_Reachable_ReturnsPreset()
    {
        var probe = new FakeProbe();
        probe.Reachable.Add("http://localhost:11434/v1");
        var preset = await Discovery.DetectLocalOllamaAsync(probe);
        Assert.NotNull(preset);
        Assert.Equal("Ollama", preset!.Name);
        Assert.True(preset.Local);
    }

    [Fact]
    public async Task DetectLocalOllama_Unreachable_Null()
    {
        Assert.Null(await Discovery.DetectLocalOllamaAsync(new FakeProbe()));
    }

    [Fact]
    public void Presets_ThreeKnownEndpoints()
    {
        Assert.Equal(3, Presets.All.Count);
        Assert.Contains(Presets.All, p => p.Endpoint == "https://api.openai.com/v1");
        Assert.Contains(Presets.All, p => p.Endpoint == "https://dashscope.aliyuncs.com/compatible-mode/v1");
        Assert.Contains(Presets.All, p => p.Endpoint == "http://localhost:11434/v1" && p.Local);
    }

    [Theory]
    [InlineData("gpt-4o", true)]
    [InlineData("gpt-4o-mini", true)]
    [InlineData("qwen-vl-max", true)]
    [InlineData("claude-3-opus", true)]
    [InlineData("llava", true)]
    [InlineData("llama3", false)]
    [InlineData("text-embedding-ada", false)]
    public void GuessVisionCapable_Heuristic(string model, bool expected)
    {
        Assert.Equal(expected, Discovery.GuessVisionCapable(model));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpStatusCode Code { get; set; } = HttpStatusCode.OK;
        public bool ThrowTimeout { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (ThrowTimeout) throw new TaskCanceledException("timeout", new TimeoutException());
            return Task.FromResult(new HttpResponseMessage(Code));
        }
    }

    [Fact]
    public async Task HttpProbe_200_And_401_Reachable()
    {
        Assert.True(await new HttpEndpointProbe(new StubHandler { Code = HttpStatusCode.OK }).IsReachableAsync("https://x/v1"));
        Assert.True(await new HttpEndpointProbe(new StubHandler { Code = HttpStatusCode.Unauthorized }).IsReachableAsync("https://x/v1"));
    }

    [Fact]
    public async Task HttpProbe_Timeout_NotReachable()
    {
        Assert.False(await new HttpEndpointProbe(new StubHandler { ThrowTimeout = true }).IsReachableAsync("https://x/v1"));
    }
}