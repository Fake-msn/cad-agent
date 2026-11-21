using CadAgent.Core;

namespace CadAgent.Llm;

/// <summary>探针结果：连通/补全/视觉（未测=null）/脱敏错误/用量。</summary>
public sealed record ProbeResult(bool Connected, bool CompletionOk, bool? VisionOk, string? Error, TokenUsage? Usage);

/// <summary>
/// 连通探针（§8.3 llm test 消费）：小补全（"回复 OK"，maxTokens 16）；
/// testVision 时发 1×1 PNG data-url 图片消息。异常→ProbeResult(false,...,脱敏 Error)。
/// </summary>
public sealed class LlmProbe
{
    // 1×1 透明 PNG（视觉探针最小载荷）
    public const string TestImageUrl =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private readonly ILlmClient _client;
    public LlmProbe(ILlmClient client) => _client = client;

    public async Task<ProbeResult> RunAsync(bool testVision, CancellationToken ct = default)
    {
        try
        {
            var resp = await _client.ChatAsync(
                new ChatRequest(new[] { new ChatMessage("user", "回复 OK") }, 16), ct);
            bool? visionOk = null;
            TokenUsage usage = resp.Usage;
            if (testVision)
            {
                var vresp = await _client.ChatAsync(
                    new ChatRequest(new[] { new ChatMessage("user", "这张图是什么颜色？回复 OK") }, 16, ImageUrl: TestImageUrl), ct);
                visionOk = true;
                usage = new TokenUsage(usage.PromptTokens + vresp.Usage.PromptTokens,
                    usage.CompletionTokens + vresp.Usage.CompletionTokens);
            }
            return new ProbeResult(true, true, visionOk, null, usage);
        }
        catch (Exception ex)
        {
            // 脱敏：sk- 模式替换；绝不回显密钥
            string error = System.Text.RegularExpressions.Regex.Replace(ex.Message, @"sk-[A-Za-z0-9]{8,}", "***");
            return new ProbeResult(false, false, null, error, null);
        }
    }
}