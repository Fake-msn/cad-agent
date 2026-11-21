using CadAgent.Core;

namespace CadAgent.Llm;

/// <summary>端点可达性探测（Ollama 本地发现用）。</summary>
public interface IEndpointProbe
{
    Task<bool> IsReachableAsync(string baseUrl, CancellationToken ct = default);
}

/// <summary>
/// HTTP 探测：GET {base}/models，2xx/401 均视为可达（401 说明服务在）；
/// 超时/异常→false。短超时（3s）。HttpMessageHandler 可注入测试。
/// </summary>
public sealed class HttpEndpointProbe : IEndpointProbe, IDisposable
{
    private readonly HttpClient _http;

    public HttpEndpointProbe(HttpMessageHandler? handler = null)
    {
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(3);
    }

    public async Task<bool> IsReachableAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(baseUrl.TrimEnd('/') + "/models", ct);
            int code = (int)resp.StatusCode;
            return (code >= 200 && code < 300) || code == 401;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>供应商预设。</summary>
public sealed record LlmPreset(string Name, string Endpoint, string SuggestedModel, bool Local);

/// <summary>内置预设（§8.3）：OpenAI / DashScope / Ollama 本地。</summary>
public static class Presets
{
    public static IReadOnlyList<LlmPreset> All { get; } = new[]
    {
        new LlmPreset("OpenAI", "https://api.openai.com/v1", "gpt-4o", false),
        new LlmPreset("DashScope", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-vl-max", false),
        new LlmPreset("Ollama", "http://localhost:11434/v1", "llava", true),
    };
}

/// <summary>模型发现 + 本地 Ollama 探测 + 视觉能力启发式。</summary>
public static class Discovery
{
    /// <summary>ListModelsAsync 包装：异常→空列表（不抛——发现是尽力而为）。</summary>
    public static async Task<IReadOnlyList<string>> DiscoverModelsAsync(ILlmClient client)
    {
        try { return await client.ListModelsAsync(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    /// <summary>探测 localhost:11434 → Ollama 预设；不可达→null。</summary>
    public static async Task<LlmPreset?> DetectLocalOllamaAsync(IEndpointProbe probe)
    {
        var ollama = Presets.All.First(p => p.Name == "Ollama");
        return await probe.IsReachableAsync(ollama.Endpoint) ? ollama : null;
    }

    /// <summary>
    /// 视觉能力保守启发式（modelId 子串）——最终判定靠实弹探针（LlmProbe）。
    /// </summary>
    public static bool GuessVisionCapable(string modelId)
    {
        string m = modelId.ToLowerInvariant();
        return m.Contains("vision") || m.Contains("-vl") || m.Contains("vl-")
            || m.Contains("gpt-4o") || m.Contains("claude") || m.Contains("llava")
            || m.Contains("gemini");
    }
}