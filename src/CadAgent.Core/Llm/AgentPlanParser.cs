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
}