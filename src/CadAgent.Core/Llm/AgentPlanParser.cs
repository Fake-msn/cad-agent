using System.Text.Json;
using System.Text.RegularExpressions;

namespace CadAgent.Core.Llm;

/// <summary>
/// LLM 响应→步骤计划（3b-i）：```json 围栏优先，回退首{末}子串；
/// planId 缺省注入 "llm-{Guid:N}"（LLM 不必编造 id）；StepPlanParser schema 校验；
/// kind 白名单（注册处理器之外的一律拒绝——LLM 幻觉挡在引擎外）。
/// </summary>
public static class AgentPlanParser
{
    private static readonly Regex FencePattern = new(@"```json\s*(.*?)```", RegexOptions.Singleline | RegexOptions.Compiled);

    public static StepPlan ExtractPlan(string llmResponse, IReadOnlySet<string> allowedKinds)
    {
        string? json = null;
        var fence = FencePattern.Match(llmResponse);
        if (fence.Success)
            json = fence.Groups[1].Value.Trim();
        else
        {
            int first = llmResponse.IndexOf('{');
            int last = llmResponse.LastIndexOf('}');
            if (first >= 0 && last > first)
                json = llmResponse[first..(last + 1)];
        }
        if (json == null)
            throw new InvalidPlanException("LLM 响应不含 JSON 计划");

        // planId 注入（缺省时）——解析前用 JsonDocument 检查/改写
        string normalized;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidPlanException("LLM 计划 JSON 须为对象");
            if (doc.RootElement.TryGetProperty("planId", out var pid) && pid.ValueKind == JsonValueKind.String)
                normalized = json;
            else
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
                dict["planId"] = JsonSerializer.SerializeToElement($"llm-{Guid.NewGuid():N}");
                normalized = JsonSerializer.Serialize(dict);
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidPlanException($"LLM 计划 JSON 非法: {ex.Message}");
        }

        var plan = StepPlanParser.Parse(normalized);
        foreach (var step in plan.Steps)
            if (!allowedKinds.Contains(step.Kind))
                throw new InvalidPlanException($"计划含未注册的步骤类型: {step.Kind}（可用: {string.Join(", ", allowedKinds.OrderBy(k => k, StringComparer.Ordinal))}）");
        return plan;
    }

    /// <summary>
    /// 3i 读回验证响应解析：{"verdict":"ok"}→(true,null)；纠正计划→(false,plan)；
    /// 空 steps 计划视为 ok（无纠正动作）；verdict 非 ok 且无 steps→InvalidPlanException；
    /// 不含 JSON→InvalidPlanException（验证不可解析不信任完成）。
    /// </summary>
    public static (bool Ok, StepPlan? Fix) ExtractVerification(string llmResponse, IReadOnlySet<string> allowedKinds)
    {
        string? json = ExtractJson(llmResponse);
        if (json == null)
            throw new InvalidPlanException("验证响应不含 JSON");
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("verdict", out var v)
                && v.ValueKind == JsonValueKind.String
                && string.Equals(v.GetString(), "ok", StringComparison.OrdinalIgnoreCase))
                return (true, null);
        }
        catch (JsonException ex)
        {
            throw new InvalidPlanException($"验证响应 JSON 非法: {ex.Message}");
        }
        var plan = ExtractPlan(llmResponse, allowedKinds);
        return plan.Steps.Count == 0 ? (true, null) : (false, plan);
    }

    private static string? ExtractJson(string llmResponse)
    {
        var fence = FencePattern.Match(llmResponse);
        if (fence.Success) return fence.Groups[1].Value.Trim();
        int first = llmResponse.IndexOf('{');
        int last = llmResponse.LastIndexOf('}');
        return first >= 0 && last > first ? llmResponse[first..(last + 1)] : null;
    }
}