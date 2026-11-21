namespace CadAgent.Core;

/// <summary>LLM 聊天消息（role: system/user/assistant）。</summary>
public sealed record ChatMessage(string Role, string Content);

/// <summary>聊天请求：消息列表 + max_tokens + temperature；ImageUrl 加性（视觉探针/读图，默认 null 不破坏既有）。</summary>
public sealed record ChatRequest(IReadOnlyList<ChatMessage> Messages, int MaxTokens, double Temperature = 0.2, string? ImageUrl = null);

/// <summary>token 用量（API 响应 usage 解析）。</summary>
public sealed record TokenUsage(int PromptTokens, int CompletionTokens);

/// <summary>聊天响应：内容 + 用量 + 模型。</summary>
public sealed record ChatResponse(string Content, TokenUsage Usage, string Model);

/// <summary>
/// LLM 客户端契约（Core 纯接口，厂商无关）；OpenAI 兼容实现在 CadAgent.Llm。
/// </summary>
public interface ILlmClient
{
    Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default);
}

/// <summary>LLM 调用失败（消息永不携带密钥）。</summary>
public class LlmException : Exception
{
    public LlmException(string message) : base(message) { }
    public LlmException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>鉴权失败（401/403）。</summary>
public class LlmAuthException : LlmException
{
    public LlmAuthException(string message) : base(message) { }
}

/// <summary>会话预算超额（编排层 3b 消费）。</summary>
public class LlmBudgetExceededException : LlmException
{
    public LlmBudgetExceededException(string message) : base(message) { }
}