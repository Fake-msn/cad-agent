namespace CadAgent.Core;

/// <summary>数据出境策略：LocalOnly 数据不出本机（仅本地端点）；CloudAllowed 允许发送至云端端点。</summary>
public enum DataPolicy { LocalOnly, CloudAllowed }

/// <summary>套餐窗口（可选）：窗口总额度与重置天数。计量消费留 plan 3b。</summary>
/// <param name="TotalTokens">窗口内总 token 额度。</param>
/// <param name="ResetDays">窗口重置天数。</param>
public sealed record TokenPlan(long TotalTokens, int ResetDays);

/// <summary>profile 限额：超时、单次调用上限、会话预算；Plan 为空表示无套餐窗口。</summary>
/// <param name="TimeoutSeconds">单次调用超时（秒）。默认 600：视觉长输出实证超 60s（2026-11-23 用户裁定 60→600）。</param>
/// <param name="MaxTokensPerCall">单次调用 max_tokens 上限。</param>
/// <param name="SessionBudgetTokens">会话累计 token 预算。</param>
/// <param name="Plan">可选套餐窗口。</param>
public sealed record ProfileLimits(int TimeoutSeconds = 600, int MaxTokensPerCall = 4096, long SessionBudgetTokens = 200_000, TokenPlan? Plan = null);

/// <summary>
/// LLM 供应商档案（纯模型，无密钥字段——密钥仅经 DPAPI/env 通道，spec 密钥安全钉死之一）。
/// </summary>
/// <param name="Name">档案名（唯一，OrdinalIgnoreCase）。</param>
/// <param name="Endpoint">OpenAI 兼容 base URL（http/https 绝对 Uri）。</param>
/// <param name="Model">模型标识。</param>
/// <param name="VisionCapable">是否支持图像输入。</param>
/// <param name="DataPolicy">数据出境策略。</param>
/// <param name="Limits">限额。</param>
public sealed record ProviderProfile(string Name, string Endpoint, string Model, bool VisionCapable, DataPolicy DataPolicy, ProfileLimits Limits);

/// <summary>profile 非法（名称/模型为空、endpoint 非 http(s) 绝对 Uri、SetActive 指向不存在的档案等）。</summary>
public sealed class InvalidProfileException : Exception
{
    public InvalidProfileException(string message) : base(message) { }
    public InvalidProfileException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>profile 校验：name 非空、endpoint 为 http(s) 绝对 Uri、model 非空 → 否则 InvalidProfileException。</summary>
public static class ProfileValidator
{
    public static void Validate(ProviderProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.Name))
            throw new InvalidProfileException("profile 名称不能为空");
        if (string.IsNullOrWhiteSpace(p.Model))
            throw new InvalidProfileException($"profile \"{p.Name}\" 的模型标识不能为空");
        if (!Uri.TryCreate(p.Endpoint, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidProfileException($"profile \"{p.Name}\" 的 endpoint 必须是 http(s) 绝对 URI: {p.Endpoint}");
    }
}
