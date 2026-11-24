using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
/// 步骤为 kind ∈ {change-layer,move,move-entity,delete,delete-entity} 且含 handle 参数，
/// 且每个目标句柄对 overlay.NativeEntities 通过 NativeGateRefinement 判据（本轮 live 重读
/// 一致）时，对原生 DWG 原文开活厂商会话执行，读回校验通过后交付 &lt;drawing&gt;.live.dwg。
/// **kind 词汇归一**（最终复审 finding 1）：产品计划 kind 是 `move-entity`/`delete-entity`
/// （HandlerRegistry/EditHandlers），Replay 通道 case 是 `move`/`delete`——准入侧两套词汇
/// 全收，克隆 Replay 计划时改写为运行时词汇（不改 ComReplayService 既有 case）。
/// **会话顺序**（@T10 修复）：先 ReadNativeParams(path≠null)（经 EnsureAppTArch 以 /p 拉起
/// 天正并缓存 _app，返回值即准入重读；该形态以 ReadOnly=true 打开原图不另建副本）→ Open 原图
/// 读改写（执行侧需要；复用同一 _app）→ 执行 → ReadNativeParams()（null 形态读当前活文档）。
/// 颠倒会令默认 profile 缓存使 TArch 永不加载。
/// **原图零写入**（源件 SHA256 前后比对，两侧空哈希均按验证失败 fail-closed；唯一写出是
/// .live.dwg 新文件）。**证据窗口闭合**（复审 finding 2）：交付后源件 SHA 复核推迟到
/// SaveAs/Close 之后且运行时 Dispose（内部 Close+Quit+等待进程退出）之后——文档关闭且实例
/// 退出前哈希可能读到未落定字节；交付件 liveSha 不可验证同样按失败退出（保留 .live.dwg
/// 作为证据但诚实报失败）。
/// 不创建 tx 快照、不调 StepEngine（native-live 独执行）。审计全部落 area "run"
/// （同 store/session/op/planId）：
/// native-gate-pass{handles,sourceSha,rereadHash}/native-gate-refused{verdicts|reason}/
/// native-live-step{kind,handle}/native-live-committed{outPath,sourceSha,liveSha}/
/// native-live-failed{step?,error|readback|reason}。
/// 成功路径 stdout 收据（finding 11）：{"ok":true,"outPath":...,"handles":[...]}。
/// R11（句柄歧义防护）：先"剥 0x+大写"原串查 overlay（全数字串可能是 hex，如 "21000"），
/// 未命中再十进制→hex 查；Evaluate 与运行时 op 用同一命中的 overlay 形态（Replay 跑句柄
/// 已替换的计划副本，避免 HandleToHex 二次归一改错实体）。R12：change-layer 空/空白层名
/// 准入即拒（InvalidLayer，不扩枚举）；层名读回 OrdinalIgnoreCase。R6：move 读回在 bbox
/// 不可得时退化为 params 不变检查（诚实降级，审计记 readback=reread-bbox-null）。
/// 同句柄多步读回按**终态聚合**：delete 支配 / change-layer 末步层名胜出 / move 位移求和。
/// 句柄形态解析异常（bool/数组/超 ulong 数值等）→ native-gate-refused{MalformedHandle}。
/// </summary>
public static partial class CommandRouter
{
    // move 读回 bbox 分量容差（R6：after bbox 可得则必验平移）。
    private const double BboxTolerance = 1e-6;

    // 哈希缝（复审 finding 2d）：测试注入空串/定向空值以覆盖 fail-closed 分支；
    // 生产默认=真实 SHA256（不可读→空串，由调用侧判定失败）。
    internal static Func<string, string> Sha256File = SafeSha256File;

    private static int? NativeLivePlan(string drawingPath, StepPlan plan, DrawingSession drawing,
        string agentDir, string session, SqliteAuditStore audit, OperatorIdentity op,
        TextWriter stdout, TextWriter stderr)
    {
        // 1. 准入分类（R5）：零步骤/任一步骤无 handle 参数/kind 非句柄寻址编辑类 → 不适用，
        // 回落既有 run-rejected（生成类/全图级修改不因 Yellow 松绑放开；kind 白名单先于
        // 一切运行时副作用——生成类步骤即便带 handle 也不得挤进 Replay 通道）。
        // 词汇归一（finding 1）：产品计划 kind（move-entity/delete-entity）与运行时 case
        // （move/delete）两套全收；归一后仍非句柄寻址编辑类 → 回落。
        if (plan.Steps.Count == 0) return null;
        foreach (var step in plan.Steps)
        {
            if (NormalizeKind(step.Kind) is not ("change-layer" or "move" or "delete")) return null;
            if (step.Params.ValueKind != JsonValueKind.Object
                || !step.Params.TryGetProperty("handle", out _)) return null;
        }

        string sourceSha = Sha256File(drawingPath);
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

        // 1b. 零写入断言前置（fail-closed）：源件不可哈希 → 无法证明原图零写入，
        // 不得带着 ""=="" 的假通过执行（空 SHA 两侧相等曾 fail-open 放行）。
        if (string.IsNullOrEmpty(sourceSha))
        {
            Audit("native-live-failed", drawingPath,
                new { error = "源图 SHA 不可验证（无法证明原图零写入）", reason = "sourcesha-unverifiable" });
            stderr.WriteLine("native-live 失败: 源图 SHA 不可验证（无法证明原图零写入）");
            return 1;
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
        // 句柄形态解析（bool/数组/对象/超 ulong 数值等）可能抛异常——必须收口为审计拒绝，
        // 不得未捕获穿透（审计链/退出码须完整）。
        var matched = new string?[plan.Steps.Count];
        var verdicts = new List<(string Handle, string Verdict)>();
        try
        {
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
        }
        catch (Exception ex)
        {
            return Refuse(new { reason = "MalformedHandle", error = ex.Message },
                $"native-live 拒绝: 句柄参数形态非法（{ex.Message}）");
        }
        if (verdicts.Count > 0) return RefuseVerdicts(verdicts);

        string outPath = drawingPath + ".live.dwg";
        try
        {
            // 4. 活会话对准**原图**（原生 DWG，TDb 存活）。运行时获取镜像 T6 R8 裁定：
            // 测试注入优先；否则按 ResolveRuntimeName 直构（不调 CreateRuntimeIfAvailable——
            // 其 GetInfo 预激活/降级文案与 native-live 语义不符；构造/Open 失败经下方 catch
            // 响亮留痕）。会话顺序：path 形态读参（拉起 TArch）→ Open 原图 → null 形态读回。
            ICadRuntime? rt = RuntimeFactory != null ? RuntimeFactory() : CreateRuntimeInstance(ResolveRuntimeName());
            if (rt == null)
            {
                Audit("native-live-failed", drawingPath, new { error = "无可用厂商运行时" });
                stderr.WriteLine("native-live 失败: 无可用厂商运行时（native-live 需活 CAD 会话）");
                return 1;
            }
            try
            {
                // 4a. 天正会话开路（@T10 修复，顺序不可颠倒）：先 path 形态读参——
                // ReadNativeParams(path≠null) 内部经 EnsureAppTArch 以 /p 拉起 TArch profile
                // 并把 _app 缓存；返回值即准入重读事实（以 ReadOnly=true 打开原图读完即关，
                // 不另建副本——复审 finding 3）。
                // 若先 Open（EnsureApp 默认 profile 先缓存 _app），EnsureAppTArch 将早退复用
                // 默认实例、TArch 永不加载→真机 TDb 目标全 StaleMismatch（通道失效）。
                var reread = rt.ReadNativeParams(drawingPath);
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

                // 台账 T7@T9：准入重读指纹（定序 "handle|objectName|bbox|params" 的 SHA256）
                // 加性落 native-gate-pass，供审计复核"放行所依据的重读事实"。
                string rereadHash = RereadHash(reread, matched);
                Audit("native-gate-pass", drawingPath, new { handles = matched, sourceSha, rereadHash });

                // 4b. 执行窗口：Open 原图读改写（执行侧需要）——EnsureApp 复用 4a 已缓存的
                // TArch _app（同一实例开原始 DWG，TDb 存活）；实例启动已由 4a 完成。
                rt.Open(drawingPath);

                // 5. 全 Pass → 句柄/kind 归一替换副本执行（Replay 内置归一不得再改写已命中形态）。
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

                // 6. 读回闸：after 读一次，期望按句柄**终态聚合**比对（同句柄多步不得按
                // 首快照逐 step 误拒）：delete 支配（句柄须消失）；change-layer 末步层名胜出；
                // move 位移按句柄求和后作用于该句柄 before-bbox。
                var after = rt.ReadNativeParams();
                var expectations = AggregateReadback(plan, matched);
                foreach (var exp in expectations)
                {
                    string? detail = CheckReadback(exp, FindRereadByHandle(reread, exp.Handle),
                        after, out bool bboxNull);
                    if (detail != null)
                    {
                        Audit("native-live-failed", drawingPath, new { readback = detail });
                        stderr.WriteLine($"native-live 读回不符: {detail}（未交付 .live.dwg，原图零写入）");
                        return 1;
                    }
                    exp.BboxNull = bboxNull;
                }
                // 逐 step 审计（按句柄首现+计划内顺序；kind 记运行时口径）；move 的 bbox
                // 不可得降级如实披露。
                foreach (var exp in expectations)
                    foreach (var step in exp.Steps)
                    {
                        string kind = NormalizeKind(step.Kind);
                        Audit("native-live-step", exp.Handle, exp.BboxNull && kind == "move"
                            ? new { kind, handle = exp.Handle, readback = "reread-bbox-null" }
                            : new { kind, handle = exp.Handle });
                    }

                // 7. 交付：唯一写出是 .live.dwg 新文件（SaveAs+Close 仍在会话内；证据哈希在
                //    运行时 Dispose 后读取，见 finally 之后的零写入断言）。
                rt.SaveAs(outPath);
                rt.Close(false);
            }
            finally
            {
                // 证据窗口闭合（复审 finding 2a）：Dispose 内部 Close(false)+Quit+等待进程退出。
                // 源件/交付件哈希必须在文档关闭且实例退出后读取——Open 期间源文件句柄由
                // AutoCAD 持有，此时读到的不完整/被拒字节会污染零写入断言。Dispose 自身不抛。
                rt.Dispose();
            }
        }
        catch (Exception ex)
        {
            Audit("native-live-failed", drawingPath, new { error = ex.Message });
            stderr.WriteLine($"native-live 失败: {ex.Message}（未交付 .live.dwg，原图零写入）");
            return 1;
        }

        // 8. 原图零写入断言（源件 SHA256 前后比对；窗口已闭合）→ 唯一写出是 .live.dwg。
        //    空 SHA 不得 ""=="" 假通过（fail-open）：哈希不可得即视为验证失败。
        string sourceShaNow = Sha256File(drawingPath);
        if (string.IsNullOrEmpty(sourceShaNow))
        {
            Audit("native-live-failed", drawingPath,
                new { error = "源图 SHA 不可验证（原图零写入契约无法断言）", reason = "sourcesha-unverifiable", sourceSha, sourceShaNow });
            stderr.WriteLine("native-live 失败: 源图 SHA 不可验证（原图零写入契约无法断言）");
            return 1;
        }
        if (sourceShaNow != sourceSha)
        {
            Audit("native-live-failed", drawingPath,
                new { error = "源图字节被改动（原图零写入契约破坏）", sourceSha, sourceShaNow });
            stderr.WriteLine($"native-live 失败: 源图字节被改动（原图零写入契约破坏；{outPath} 已产出但按失败退出）");
            return 1;
        }
        // 交付件证据（复审 finding 2b）：liveSha 不可验证同样按失败退出——交付件可读性
        // 未证不得报成功；保留 .live.dwg 作为诊断证据但审计/退出码诚实报失败。
        string liveSha = Sha256File(outPath);
        if (string.IsNullOrEmpty(liveSha))
        {
            Audit("native-live-failed", outPath,
                new { outPath, error = "交付件 SHA 不可验证（.live.dwg 可读性未证）", reason = "liveSha-unverifiable", sourceSha, liveSha });
            stderr.WriteLine($"native-live 失败: 交付件 {outPath} SHA 不可验证（可读性未证，按失败退出；文件保留供诊断）");
            return 1;
        }
        Audit("native-live-committed", outPath, new { outPath, sourceSha, liveSha }, liveSha);
        // 成功收据（finding 11）：json 管道消费面（stdout 仅此一条，与 run 绿色路径同口径）。
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            outPath,
            handles = matched.Select(h => h!).ToArray()
        }, JsonOpts));
        stderr.WriteLine($"native-live: 已交付 {outPath}（源图未改写）");
        return 0;
    }

    /// <summary>产品计划 kind ↔ Replay 运行时 kind 归一（复审 finding 1）：生成侧词汇
    /// move-entity/delete-entity → 运行时 case move/delete；其余原样（change-layer 两侧同名）。
    /// 仅准入/Replay 克隆使用，ComReplayService case 词汇零改动。</summary>
    private static string NormalizeKind(string kind) => kind switch
    {
        "move-entity" => "move",
        "delete-entity" => "delete",
        _ => kind,
    };

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

    /// <summary>句柄已命中副本（R10/R11）：以 JsonNode 仅替换 handle 键，其余参数逐字保留；
    /// kind 同步归一（move-entity→move / delete-entity→delete，复审 finding 1）——Replay
    /// 通道 case 词汇与产品计划词汇的解耦点，ComReplayService 零改动。</summary>
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
            steps.Add(new StepSpec(NormalizeKind(step.Kind), doc.RootElement.Clone()));
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

    /// <summary>同句柄多步聚合期望（读回按终态一次性比对，不按首快照逐 step 误拒）：
    /// delete 支配（句柄应消失）；change-layer 以末步层名为准；move 位移按句柄求和。</summary>
    private sealed class HandleReadback
    {
        public string Handle = "";
        public readonly List<StepSpec> Steps = new();
        public bool Delete;
        public string? Layer;
        public double Dx;
        public double Dy;
        public bool HasMove;
        public bool BboxNull;
    }

    /// <summary>按规划顺序聚合：每句柄首现固定顺序（审计/失败文案确定性），
    /// 末次 change-layer 层名覆盖前者，move 的 dx/dy 逐 step 累加，delete 置支配位。</summary>
    private static List<HandleReadback> AggregateReadback(StepPlan plan, IReadOnlyList<string?> matched)
    {
        var byHandle = new Dictionary<string, HandleReadback>(StringComparer.Ordinal);
        var list = new List<HandleReadback>();
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            string handle = matched[i]!;
            if (!byHandle.TryGetValue(handle, out var exp))
            {
                exp = new HandleReadback { Handle = handle };
                byHandle.Add(handle, exp);
                list.Add(exp); // 首现顺序
            }
            var step = plan.Steps[i];
            exp.Steps.Add(step);
            // 词汇归一（finding 1）：产品计划 move-entity/delete-entity 与运行时 move/delete
            // 同判——聚合读回期望按运行时语义归类。
            switch (NormalizeKind(step.Kind))
            {
                case "delete":
                    exp.Delete = true;
                    break;
                case "change-layer":
                    exp.Layer = step.Params.GetProperty("layer").GetString() ?? "";
                    break;
                case "move":
                    exp.HasMove = true;
                    exp.Dx += step.Params.GetProperty("dx").GetDouble();
                    exp.Dy += step.Params.GetProperty("dy").GetDouble();
                    break;
            }
        }
        return list;
    }

    /// <summary>句柄聚合期望 vs 终态读回（返回 null=通过；非 null=不符详情）：
    /// delete→句柄消失（支配其余期望）；change-layer→末步目标层（R12 OrdinalIgnoreCase）；
    /// move→before-bbox 整体平移位移和 (Dx,Dy)（容差 1e-6；bbox 不可得→params 不变退化，
    /// bboxNull=true 供审计如实披露）。</summary>
    private static string? CheckReadback(HandleReadback exp, NativeEntityParam? before,
        IReadOnlyList<NativeEntityParam> after, out bool bboxNull)
    {
        bboxNull = false;
        var afterRec = FindRereadByHandle(after, exp.Handle);
        if (exp.Delete)
            return afterRec == null ? null : $"delete {exp.Handle} 重读仍存在（未删除）";
        if (afterRec == null)
        {
            string kindLabel = exp.HasMove && exp.Layer != null ? "change-layer/move"
                : exp.HasMove ? "move" : "change-layer";
            return $"{kindLabel} {exp.Handle} 重读缺失（句柄不存在）";
        }
        if (exp.Layer != null
            && (afterRec.Layer == null
                || !string.Equals(afterRec.Layer, exp.Layer, StringComparison.OrdinalIgnoreCase)))
            return $"change-layer {exp.Handle} 图层不符（期望 {exp.Layer}，实际 {afterRec.Layer ?? "null"}）";
        if (!exp.HasMove) return null;
        if (before?.Bbox is { Length: 6 } b0 && afterRec.Bbox is { Length: 6 } b1)
        {
            double[] deltas = { exp.Dx, exp.Dy, 0, exp.Dx, exp.Dy, 0 };
            for (int i = 0; i < 6; i++)
                if (Math.Abs(b1[i] - b0[i] - deltas[i]) > BboxTolerance)
                    return $"move {exp.Handle} bbox 位移不符（分量{i} 期望 {b0[i] + deltas[i]} 实际 {b1[i]}）";
            return null;
        }
        // R6 诚实降级：bbox 不可得（尽力读取面）→ 退化为原生参数不变检查。
        bboxNull = true;
        if (!ParamsEqual(before?.Params, afterRec.Params))
            return $"move {exp.Handle} bbox 不可得且原生参数不一致（reread-bbox-null）";
        return null;
    }

    /// <summary>台账 T7@T9 准入重读指纹：已评估句柄去重+Ordinal 排序后逐条拼
    /// "handle|objectName|bbox|params"（bbox 分量按序、params 键 Ordinal 排序），
    /// 以 \n 连接做 SHA256——定长十六进制、完全确定性（仅取参与判据的重读记录；
    /// Pass 路径必非缺失，null 跳过属防御）。</summary>
    private static string RereadHash(IReadOnlyList<NativeEntityParam> reread, IReadOnlyList<string?> matched)
    {
        var lines = new List<string>();
        foreach (string handle in matched.Select(h => h!).Distinct(StringComparer.Ordinal)
                     .OrderBy(h => h, StringComparer.Ordinal))
        {
            var rec = FindRereadByHandle(reread, handle);
            if (rec == null) continue;
            string bbox = rec.Bbox == null
                ? "null"
                : string.Join(",", rec.Bbox.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
            string parms = string.Join(";", rec.Params
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + "=" + kv.Value));
            lines.Add($"{handle}|{rec.ObjectName}|{bbox}|{parms}");
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
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
