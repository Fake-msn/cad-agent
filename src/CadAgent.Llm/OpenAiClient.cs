using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CadAgent.Core;

namespace CadAgent.Llm;

/// <summary>
/// OpenAI 兼容 HTTP 客户端：POST {endpoint}/chat/completions + GET {endpoint}/models。
/// Bearer 鉴权；401/403→LlmAuthException；其他非成功→LlmException；
/// **异常消息永不携带密钥**（仅状态码+脱敏 body 摘要）。endpoint 尾斜杠归一。
/// HttpMessageHandler 可注入（mock 测试）；生产用默认 HttpClientHandler。
/// </summary>
public sealed class OpenAiClient : ILlmClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly ProviderProfile _profile;
    private readonly string _apiKey;
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public OpenAiClient(ProviderProfile profile, string apiKey, HttpMessageHandler? handler = null)
    {
        _profile = profile;
        _apiKey = apiKey;
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(profile.Limits.TimeoutSeconds);
        _baseUrl = profile.Endpoint.TrimEnd('/');
    }

    public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = _profile.Model,
            messages = request.Messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            max_tokens = request.MaxTokens,
            temperature = request.Temperature
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var resp = await SendAsync(req, ct);
        string json = await resp.Content.ReadAsStringAsync(ct);
        EnsureSuccess(resp, json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        var usage = root.TryGetProperty("usage", out var u)
            ? new TokenUsage(
                u.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0,
                u.TryGetProperty("completion_tokens", out var cpt) ? cpt.GetInt32() : 0)
            : new TokenUsage(0, 0);
        string model = root.TryGetProperty("model", out var mEl) ? mEl.GetString() ?? _profile.Model : _profile.Model;
        return new ChatResponse(content, usage, model);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/models");
        if (!string.IsNullOrEmpty(_apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var resp = await SendAsync(req, ct);
        string json = await resp.Content.ReadAsStringAsync(ct);
        EnsureSuccess(resp, json);
        using var doc = JsonDocument.Parse(json);
        var ids = new List<string>();
        foreach (var el in doc.RootElement.GetProperty("data").EnumerateArray())
            if (el.TryGetProperty("id", out var idEl))
                ids.Add(idEl.GetString() ?? "");
        return ids;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(req, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LlmException($"LLM 请求超时（{_profile.Limits.TimeoutSeconds}s）: {_baseUrl}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"LLM 请求失败: {_baseUrl} ({ex.Message})", ex);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage resp, string json)
    {
        if (resp.IsSuccessStatusCode) return;
        int code = (int)resp.StatusCode;
        // 脱敏：body 截断 200 字符 + sk- 模式替换；绝不回显请求头/密钥
        string snippet = json.Length > 200 ? json[..200] : json;
        snippet = System.Text.RegularExpressions.Regex.Replace(snippet, @"sk-[A-Za-z0-9]{8,}", "***");
        if (code == 401 || code == 403)
            throw new LlmAuthException($"LLM 鉴权失败 ({code}): {snippet}");
        throw new LlmException($"LLM 调用失败 ({code}): {snippet}");
    }

    public void Dispose() => _http.Dispose();
}