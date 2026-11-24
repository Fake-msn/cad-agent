using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data;
using CadAgent.Data.Cad;
using CadAgent.Data.Semantic;

namespace CadAgent.Cli;

/// <summary>
/// 切片A §4.2：run 主闸逐实体精准松绑准入 + native-live 独执行（spec §4.2）。
/// census Yellow 图纸全图级拦截（GateRefusesRun）的精准旁路：仅当 `--live` + 计划**全部**
/// 步骤为句柄寻址（含 handle 参数）且每个目标句柄对 overlay.NativeEntities 通过
/// NativeGateRefinement 判据（本轮 live 重读一致）时，对原生 DWG 原文开活厂商会话执行
/// change-layer/move/delete，读回校验通过后交付 &lt;drawing&gt;.live.dwg。
/// **原图零写入**（源件 SHA256 前后比对；唯一写出是 .live.dwg 新文件）；不创建 tx 快照、
/// 不调 StepEngine（native-live 独执行）。审计全部落 area "run"（同 store/session/op/planId）：
/// native-gate-pass{handles,sourceSha}/native-gate-refused{verdicts|reason}/
/// native-live-step{kind,handle}/native-live-committed{outPath,sourceSha,liveSha}/
/// native-live-failed{step?,error|readback}。
/// R11（句柄歧义防护）：先"剥 0x+大写"原串查 overlay（全数字串可能是 hex，如 "21000"），
/// 未命中再十进制→hex 查；Evaluate 与运行时 op 用同一命中的 overlay 形态（Replay 跑句柄
/// 已替换的计划副本，避免 HandleToHex 二次归一改错实体）。R12：change-layer 空/空白层名
/// 准入即拒（InvalidLayer，不扩枚举）；层名读回 OrdinalIgnoreCase。R6：move 读回在 bbox
/// 不可得时退化为 params 不变检查（诚实降级，审计记 readback=reread-bbox-null）。
/// </summary>
public static partial class CommandRouter
{
    // move 读回 bbox 分量容差（R6：after bbox 可得则必验平移）。
    private const double BboxTolerance = 1e-6;

    private static int? NativeLivePlan(string drawingPath, StepPlan plan, DrawingSession drawing,
        string agentDir, string session, SqliteAuditStore audit, OperatorIdentity op, TextWriter stderr)
    {
        // 1. 准入分类（R5）：零步骤/任一步骤无 handle 参数 → 不适用，回落既有 run-rejected
        //（生成类/全图级修改不因 Yellow 松绑放开）。
        if (plan.Steps.Count == 0) return null;
        foreach (var step in plan.Steps)
            if (step.Params.ValueKind != JsonValueKind.Object
                || !step.Params.TryGetProperty("handle", out _)) return null;

        string sourceSha = SafeSha256File(drawingPath);
        void Audit(string kind, string target, object payload, string? afterHash = null) =>
            audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                session, "run", kind, target, JsonSerializer.Serialize(payload),
                sourceSha, afterHash ?? sourceSha, "", plan.PlanId));

        int Refuse(object payload, string message)
        {
            Audit("native-gate-refused", drawingPath, payload);
            stderr.WriteLine(message);
            return 1;
        }

        int RefuseVerdicts(IReadOnlyList<(string Handle, string Verdict)> verdicts)
        {
            string detail = string.Join(", ", verdicts.Select(v => $"{v.Handle}={v.Verdict}"));
            // R12 层名准入属用法错误：独立 usage 文案（其余判据拒绝给逐实体 verdict 明细）。
            string message = verdicts.Any(v => v.Verdict == "InvalidLayer")
                ? $"用法: change-layer 需要非空 layer 参数（逐实体判据: {detail}）"
                : $"native-live 拒绝: 逐实体判据未通过（{detail}）";
            return Refuse(new
            {
                verdicts = verdicts.Select(v => new { handle = v.Handle, verdict = v.Verdict }).ToArray()
            }, message);
        }

        // 2. overlay 加载（缺失/损坏 → 拒绝留痕，不静默降级为"无 overlay 放行"）。
        string overlayPath = SemanticOverlayPath(agentDir);
        if (!File.Exists(overlayPath))
            return Refuse(new { reason = "overlay-missing" },
                "native-live 拒绝: 未找到语义叠加层（请先 semantic scan --native）");
        SemanticOverlay overlay;
        try
        {
            overlay = SemanticOverlay.Load(overlayPath);
        }
        catch (Exception ex)
        {
            return Refuse(new { reason = "overlay-load-failed", error = ex.Message },
                $"native-live 拒绝: 语义叠加层损坏（{ex.Message}）");
        }

        // 3. 句柄预匹配（R11）+ 层名准入（R12）：纯 overlay 判据，先于任何运行时副作用。
        var matched = new string?[plan.Steps.Count];
        var verdicts = new List<(string Handle, string Verdict)>();
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            string raw = ComReplayService.HandleString(step.Params);
            if (step.Kind == "change-layer" && IsBlankLayer(step.Params))
            {
                verdicts.Add((raw, "InvalidLayer"));
                continue;
            }
            string? match = MatchOverlayHandle(overlay, raw);
            matched[i] = match;
            if (match == null) verdicts.Add((raw, NativeGateVerdict.NotRead.ToString()));
        }
        if (verdicts.Count > 0) return RefuseVerdicts(verdicts);

        try
        {
            // 4. 活会话对准**原图**（原生 DWG，TDb 存活）；ReadNativeParams() 只读一次。
            // 运行时获取镜像 T6 R8 裁定：测试注入优先；否则按 ResolveRuntimeName 直构
            // （不调 CreateRuntimeIfAvailable——其 GetInfo 预激活/降级文案与 native-live
            // 语义不符；构造/Open 失败经下方 catch 响亮留痕）。
            ICadRuntime? rt = RuntimeFactory != null ? RuntimeFactory() : CreateRuntimeInstance(ResolveRuntimeName());
            if (rt == null)
            {
                Audit("native-live-failed", drawingPath, new { error = "无可用厂商运行时" });
                stderr.WriteLine("native-live 失败: 无可用厂商运行时（native-live 需活 CAD 会话）");
                return 1;
            }
            using (rt)
            {
                rt.Open(drawingPath);
                var reread = rt.ReadNativeParams();
                var gateVerdicts = new List<(string Handle, string Verdict)>();
                for (int i = 0; i < plan.Steps.Count; i++)
                {
                    string handle = matched[i]!;
                    // 台账 T7 守卫：重读记录句柄须等于查询句柄（防管道错配误放行）；
                    // 不等/缺失 → null → Evaluate 判 StaleMismatch。
                    var rec = FindRereadByHandle(reread, handle);
                    var verdict = NativeGateRefinement.Evaluate(overlay, handle, rec);
                    if (verdict != NativeGateVerdict.Pass)
                        gateVerdicts.Add((handle, verdict.ToString()));
                }
                if (gateVerdicts.Count > 0) return RefuseVerdicts(gateVerdicts);

                Audit("native-gate-pass", drawingPath, new { handles = matched, sourceSha });

                // 5. 全 Pass → 句柄替换副本执行（Replay 内置归一不得再改写已命中形态）。
                var replayPlan = ReplaceStepHandles(plan, matched);
                var results = new ComReplayService(rt).Replay(replayPlan, drawing);
                for (int i = 0; i < results.Count; i++)
                {
                    var r = results[i];
                    if (!r.Ok)
                    {
                        Audit("native-live-failed", matched[i]!,
                            new { step = r.Kind, handle = matched[i], error = r.Error });
                        stderr.WriteLine($"native-live 执行失败: {r.Kind}: {r.Error}（未交付 .live.dwg，原图零写入）");
                        return 1;
                    }
                }

                // 6. 读回闸：after 读一次，逐 step 期望校验（不符即拒，不 SaveAs）。
                var after = rt.ReadNativeParams();
                for (int i = 0; i < plan.Steps.Count; i++)
                {
                    var step = plan.Steps[i];
                    string handle = matched[i]!;
                    var beforeRec = FindRereadByHandle(reread, handle);
                    string? detail = CheckReadback(step, handle, beforeRec, after, out bool bboxNull);
                    if (detail != null)
                    {
                        Audit("native-live-failed", drawingPath, new { readback = detail });
                        stderr.WriteLine($"native-live 读回不符: {detail}（未交付 .live.dwg，原图零写入）");
                        return 1;
                    }
                    Audit("native-live-step", handle, bboxNull
                        ? new { kind = step.Kind, handle, readback = "reread-bbox-null" }
                        : new { kind = step.Kind, handle });
                }

                // 7. 原图零写入断言（源件 SHA256 前后比对）→ 唯一写出是 .live.dwg 新文件。
                string sourceShaNow = SafeSha256File(drawingPath);
                if (sourceShaNow != sourceSha)
                {
                    Audit("native-live-failed", drawingPath,
                        new { error = "源图字节被改动（原图零写入契约破坏）", sourceSha, sourceShaNow });
                    stderr.WriteLine("native-live 失败: 源图字节被改动（原图零写入契约破坏）");
                    return 1;
                }
                string outPath = drawingPath + ".live.dwg";
                rt.SaveAs(outPath);
                rt.Close(false);
                string liveSha = SafeSha256File(outPath);
                Audit("native-live-committed", outPath, new { outPath, sourceSha, liveSha }, liveSha);
                stderr.WriteLine($"native-live: 已交付 {outPath}（源图未改写）");
                return 0;
            }
        }
        catch (Exception ex)
        {
            Audit("native-live-failed", drawingPath, new { error = ex.Message });
            stderr.WriteLine($"native-live 失败: {ex.Message}（未交付 .live.dwg，原图零写入）");
            return 1;
        }
    }

    /// <summary>R11 句柄形态匹配：候选①剥 0x+大写原串精确命中 overlay；候选②未命中且
    /// 可十进制解析→ToString("X") 再命中；仍不命中→null（该步骤 NotRead）。</summary>
    private static string? MatchOverlayHandle(SemanticOverlay overlay, string rawHandle)
    {
        string trimmed = rawHandle.Trim();
        string upper = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? trimmed[2..] : trimmed;
        upper = upper.ToUpperInvariant();
        if (upper.Length == 0) return null;
        if (overlay.NativeEntities.Any(n => n.Handle == upper)) return upper;
        if (ulong.TryParse(upper, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong dec))
        {
            string hex = dec.ToString("X");
            if (overlay.NativeEntities.Any(n => n.Handle == hex)) return hex;
        }
        return null;
    }

    /// <summary>重读记录定位（台账 T7 守卫）：Ordinal 精确匹配（大小写不同的同名句柄视为
    /// 错配→null→StaleMismatch），绝不宽松归一后当同一实体放行。</summary>
    private static NativeEntityParam? FindRereadByHandle(IReadOnlyList<NativeEntityParam> reread, string handle) =>
        reread.FirstOrDefault(n => string.Equals(n.Handle, handle, StringComparison.Ordinal));

    /// <summary>R12 层名准入：change-layer 的 layer 缺失/非字符串/空/空白 → InvalidLayer。</summary>
    private static bool IsBlankLayer(JsonElement p) =>
        !p.TryGetProperty("layer", out var v) || v.ValueKind != JsonValueKind.String
        || string.IsNullOrWhiteSpace(v.GetString());

    /// <summary>句柄已命中副本（R10/R11）：以 JsonNode 仅替换 handle 键，其余参数逐字保留。
    /// 替换值为 <see cref="ReplayHandleArg"/>——经 ComReplayService.HandleToHex 归一后**恰等于**
    /// 命中的 overlay 形态（全数字 hex 句柄如 "21000" 若直传会被十进制归一改错实体，故喂其
    /// hex 值的十进制形态，归一后回落 "21000"）。</summary>
    private static StepPlan ReplaceStepHandles(StepPlan plan, IReadOnlyList<string?> matched)
    {
        var steps = new List<StepSpec>(plan.Steps.Count);
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            var node = JsonNode.Parse(step.Params.GetRawText())
                ?? throw new InvalidPlanException($"步骤 {i} 参数为空，无法保留句柄替换");
            if (node is not JsonObject obj)
                throw new InvalidPlanException($"步骤 {i} 参数不是 JSON 对象，无法保留句柄替换");
            obj["handle"] = ReplayHandleArg(matched[i]!);
            using var doc = JsonDocument.Parse(obj.ToJsonString());
            steps.Add(new StepSpec(step.Kind, doc.RootElement.Clone()));
        }
        return new StepPlan(plan.PlanId, steps);
    }

    /// <summary>R11 尾部闭合：命中形态 → ComReplayService.Replay 的 handle 参数形态。
    /// HandleToHex：含字母→大写恒等；全数字可按十进制解析→转 hex。故全数字命中形态须喂其
    /// hex 值的十进制串（hex→dec→ToString("X") 恒等），其余原样；17+ 位全数字超 ulong
    /// 的极端形态两侧解析均失败→恒等，保守侧由读回闸兜底。</summary>
    private static string ReplayHandleArg(string matched)
    {
        if (!matched.All(c => c is >= '0' and <= '9')) return matched;
        if (!ulong.TryParse(matched, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return matched; // HandleToHex 十进制解析失败→大写原样返回（恒等）
        if (ulong.TryParse(matched, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hexValue))
            return hexValue.ToString(CultureInfo.InvariantCulture);
        return matched;
    }

    /// <summary>读回期望校验（返回 null=通过；非 null=不符详情）：
    /// change-layer→Layer==目标层（R12 OrdinalIgnoreCase）；move→bbox 各分量恰平移 dx/dy
    /// （容差 1e-6；bbox 不可得→params 不变退化，bboxNull=true 供审计如实披露）；
    /// delete→句柄消失。</summary>
    private static string? CheckReadback(StepSpec step, string handle, NativeEntityParam? before,
        IReadOnlyList<NativeEntityParam> after, out bool bboxNull)
    {
        bboxNull = false;
        var afterRec = FindRereadByHandle(after, handle);
        switch (step.Kind)
        {
            case "change-layer":
            {
                string layer = step.Params.GetProperty("layer").GetString() ?? "";
                if (afterRec == null) return $"change-layer {handle} 重读缺失（句柄不存在）";
                if (afterRec.Layer == null
                    || !string.Equals(afterRec.Layer, layer, StringComparison.OrdinalIgnoreCase))
                    return $"change-layer {handle} 图层不符（期望 {layer}，实际 {afterRec.Layer ?? "null"}）";
                return null;
            }
            case "move":
            {
                if (afterRec == null) return $"move {handle} 重读缺失（句柄不存在）";
                double dx = step.Params.GetProperty("dx").GetDouble();
                double dy = step.Params.GetProperty("dy").GetDouble();
                if (before?.Bbox is { Length: 6 } b0 && afterRec.Bbox is { Length: 6 } b1)
                {
                    double[] deltas = { dx, dy, 0, dx, dy, 0 };
                    for (int i = 0; i < 6; i++)
                        if (Math.Abs(b1[i] - b0[i] - deltas[i]) > BboxTolerance)
                            return $"move {handle} bbox 位移不符（分量{i} 期望 {b0[i] + deltas[i]} 实际 {b1[i]}）";
                    return null;
                }
                // R6 诚实降级：bbox 不可得（尽力读取面）→ 退化为原生参数不变检查。
                bboxNull = true;
                if (!ParamsEqual(before?.Params, afterRec.Params))
                    return $"move {handle} bbox 不可得且原生参数不一致（reread-bbox-null）";
                return null;
            }
            case "delete":
                return afterRec == null ? null : $"delete {handle} 重读仍存在（未删除）";
            default:
                return $"native-live 不支持读回校验类型 {step.Kind}";
        }
    }

    private static bool ParamsEqual(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string> b)
    {
        if (a is null || a.Count != b.Count) return false;
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out var v) || !string.Equals(v, kv.Value, StringComparison.Ordinal))
                return false;
        return true;
    }

    private static string SafeSha256File(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs));
        }
        catch
        {
            return ""; // 哈希不可得→空串（审计诚实留痕，不掩盖原始拒绝/失败路径）
        }
    }
}
