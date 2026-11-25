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
/// 步骤为 kind ∈ {change-layer,move,move-entity,delete,delete-entity,explode-native} 且含
/// handle 参数，且每个目标句柄对 overlay.NativeEntities 通过 NativeGateRefinement 判据（本轮
/// live 重读一致，切片B 起改为候选门合并：参数门 ∨ 炸开门）时，对原生 DWG 原文开活厂商会话
/// 执行，读回校验通过后交付 &lt;drawing&gt;.live.dwg。
/// **kind 词汇归一**（最终复审 finding 1）：产品计划 kind 是 `move-entity`/`delete-entity`
/// （HandlerRegistry/EditHandlers），Replay 通道 case 是 `move`/`delete`——准入侧两套词汇
/// 全收，克隆 Replay 计划时改写为运行时词汇（不改 ComReplayService 既有 case）。
/// **会话顺序**（@T10 修复 + 切片B 前插）：先 ReadExplodeEvidence(path)（拉起 TArch+NETLOAD、
/// 契约要求无打开文档）→ ReadNativeParams(path≠null)（经 EnsureAppTArch 以 /p 拉起天正并缓存
/// _app，返回值即准入重读；该形态以 ReadOnly=true 打开原图不另建副本）→ Open 原图读改写
/// （执行侧需要；复用同一 _app）→ 执行 → ReadNativeParams()（null 形态读当前活文档）。
/// 颠倒会令默认 profile 缓存使 TArch 永不加载。
/// **原图零写入**（源件 SHA256 前后比对，两侧空哈希均按验证失败 fail-closed；唯一写出是
/// .live.dwg 新文件）。**证据窗口闭合**（复审 finding 2）：交付后源件 SHA 复核推迟到
/// SaveAs/Close 之后且运行时 Dispose（内部 Close+Quit+等待进程退出）之后——文档关闭且实例
/// 退出前哈希可能读到未落定字节；交付件 liveSha 不可验证同样按失败退出（保留 .live.dwg
/// 作为证据但诚实报失败）。
/// 不创建 tx 快照、不调 StepEngine（native-live 独执行）。审计全部落 area "run"
/// （同 store/session/op/planId）：
/// native-gate-pass{handles,sourceSha,rereadHash,explodeGateHandles}/native-gate-refused{verdicts|reason}/
/// native-explode-step{hex,newCount,savedPath,bboxMatch}/native-live-step{kind,handle}/
/// native-live-committed{outPath,sourceSha,liveSha}/
/// native-live-failed{step?,error|readback|reason}。
/// native-gate-refused 的 verdicts 逐条 {handle,verdict(=合并判据结论),paramsVerdict(参数门结论)}。
/// 成功路径 stdout 收据（finding 11）：{"ok":true,"outPath":...,"handles":[...]}。
/// R11（句柄歧义防护）：先"剥 0x+大写"原串查 overlay（全数字串可能是 hex，如 "21000"），
/// 未命中再十进制→hex 查；Evaluate 与运行时 op 用同一命中的 overlay 形态（Replay 跑句柄
/// 已替换的计划副本，避免 HandleToHex 二次归一改错实体）。R12：change-layer 空/空白层名
/// 准入即拒（InvalidLayer，不扩枚举）；层名读回 OrdinalIgnoreCase。R6：move 读回在 bbox
/// 不可得时退化为 params 不变检查（诚实降级，审计记 readback=reread-bbox-null）。
/// 同句柄多步读回按**终态聚合**：delete 支配 / change-layer 末步层名胜出 / move 位移求和。
/// 句柄形态解析异常（bool/数组/超 ulong 数值等）→ native-gate-refused{MalformedHandle}。
/// **切片B 炸开门**（spec §4，R16 裁定）：kind 白名单扩 `explode-native`——桥事务替换原实体为
/// 炸开叶（rt.ExplodeReplaceByHandle(hex, &lt;drawing&gt;.live.dwg)）。准入对**全部**步骤一律用
/// `EvaluateCombined`（参数门 ∨ 炸开门，spec §4.2 附加证据："change-layer/move/delete 准入扩为
/// 参数门 ∨ 炸开门"），不再按 kind 分派门；R14：合并判据永不返回参数门拒因、炸开门拒因优先，
/// 故并行调 Evaluate 把参数门 verdict 以 `paramsVerdict` 加性落 native-gate-refused，准入
/// 文案/审计不得宣称"参数已读全"。仅凭炸开门放行的句柄逐字落 native-gate-pass.explodeGateHandles。
/// **R16 子裁定（change-layer 需参数门）**：change-layer 的读回期望是层名，参数行缺失时既无
/// before 佐证也无 after 对照（层名只能从参数面读回），仅凭炸开证据放行属不可对账放行 →
/// 准入即 fail-closed 拒绝 `native-gate-refused{reason:"change-layer-requires-params"}`；
/// move（bbox 基准可回退炸开证据 bbox）与 delete（只验句柄消失）允许仅凭炸开门放行。
/// 会话开路序扩为**三段**：ReadExplodeEvidence(path)（拉起 TArch+NETLOAD、
/// 契约要求无打开文档）→ ReadNativeParams(path)（拉起 TArch profile 的参数门重读）→ Open。
/// 执行由"一次性 Replay"改为**按计划顺序逐 step**：explode-native → 桥替换 + 审计
/// native-explode-step{hex,newCount,savedPath,bboxMatch}；其余句柄类 kind → 单步计划过
/// ComReplayService（shadow-agnostic；不消费 skill 包）。读回：参数面聚合**排除**
/// explode-native 步（同句柄多步语义不跨炸开通道），move 期望 bbox 以参数面 before-bbox
/// 为基准、参数行缺失时回退准入快照炸开证据 bbox（两者皆无 → native-live-failed
/// {readback:"no-bbox-baseline"}，fail-closed）；炸开结果各以 null 形态读回断言原句柄
/// 消失且 NewCount&gt;0 && BboxMatch，否则 native-live-failed{readback:"explode-native"}。
/// 零写入/Dispose 后哈希/证据窗口闭合等切片A 契约逐字不动。
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
            // 切片B：explode-native 同列白名单（桥事务替换=句柄寻址编辑类）；缺 handle 参数
            // 或非句柄寻址 kind 一律回落既有 run-rejected（R5 不收窄）。
            if (NormalizeKind(step.Kind) is not ("change-layer" or "move" or "delete" or "explode-native"))
                return null;
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

        int RefuseVerdicts(IReadOnlyList<(string Handle, string Verdict, string? ParamsVerdict)> verdicts)
        {
            // R14：verdict=合并判据结论（炸开门拒因优先），paramsVerdict=参数门独立结论
            // （仅解释用，nullable=该阶段未取参数门结论，如 InvalidLayer 用法拒绝）。
            string detail = string.Join(", ", verdicts.Select(v => v.ParamsVerdict == null
                ? $"{v.Handle}={v.Verdict}"
                : $"{v.Handle}={v.Verdict}/参数门 {v.ParamsVerdict}"));
            // R12 层名准入属用法错误：独立 usage 文案（其余判据拒绝给逐实体 verdict 明细）。
            string message = verdicts.Any(v => v.Verdict == "InvalidLayer")
                ? $"用法: change-layer 需要非空 layer 参数（逐实体判据: {detail}）"
                : $"native-live 拒绝: 逐实体判据未通过（{detail}）";
            return Refuse(new
            {
                verdicts = verdicts.Select(v => new
                {
                    handle = v.Handle,
                    verdict = v.Verdict,
                    paramsVerdict = v.ParamsVerdict
                }).ToArray()
            }, message);
        }

        // R16 子裁定：change-layer 仅凭炸开门（参数门未过）→ 准入 fail-closed。
        // 独立 reason（不与逐实体 verdict 混淆）：这是"该 kind 的读回面不可对账"的政策拒绝，
        // verdict 仍逐条保留合并/参数门双结论供复核（合并可能已是 Pass）。
        int RefuseLayerNoParams(IReadOnlyList<(string Handle, string Verdict, string? ParamsVerdict)> rows)
        {
            string detail = string.Join(", ", rows.Select(v => $"{v.Handle}={v.Verdict}/参数门 {v.ParamsVerdict}"));
            Audit("native-gate-refused", drawingPath, new
            {
                reason = "change-layer-requires-params",
                verdicts = rows.Select(v => new
                {
                    handle = v.Handle,
                    verdict = v.Verdict,
                    paramsVerdict = v.ParamsVerdict
                }).ToArray()
            });
            stderr.WriteLine($"native-live 拒绝: change-layer 需参数门放行（仅凭炸开证据无参数面记录，层名读回无从核对；{detail}）");
            return 1;
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
        var verdicts = new List<(string Handle, string Verdict, string? ParamsVerdict)>();
        try
        {
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                var step = plan.Steps[i];
                string raw = ComReplayService.HandleString(step.Params);
                if (step.Kind == "change-layer" && IsBlankLayer(step.Params))
                {
                    verdicts.Add((raw, "InvalidLayer", null));
                    continue;
                }
                // R11 匹配语义对 explode-native 同口径（精确 hex 优先、十进制回退）：目录来源
                // 仍是 overlay.NativeEntities（炸开句柄亦须有原生读参行才可定位）。
                string? match = MatchOverlayHandle(overlay, raw);
                matched[i] = match;
                if (match == null)
                    verdicts.Add((raw, NativeGateVerdict.NotRead.ToString(),
                        NativeGateVerdict.NotRead.ToString()));
            }
        }
        catch (Exception ex)
        {
            return Refuse(new { reason = "MalformedHandle", error = ex.Message },
                $"native-live 拒绝: 句柄参数形态非法（{ex.Message}）");
        }
        if (verdicts.Count > 0) return RefuseVerdicts(verdicts);

        string outPath = drawingPath + ".live.dwg";

        // 会话工作（4a–7）收进局部函数：各失败早退只返回码，不再绕过零写入复核
        // （I3 复审修复）——运行时一旦创建（存在副作用可能），成功/失败路径一律在
        // Dispose（Close+Quit+进程退出）之后重算源件 SHA 并与初值比对；失败文案不得
        // 仅凭声称"原图零写入"而未经复核。
        int RunSession(ICadRuntime rt)
        {
            // 4a-0. 炸开取证开路（切片B，必须最先）：ReadExplodeEvidence(path) 契约=以 /p
            // 拉起 TArch+NETLOAD 并在**无打开文档**时逐实体递归取证（Task 4 守卫对"已有打开
            // 的图纸"响亮拒绝）；先 Open 或先读参都会让通道/实例缓存错位（镜像 @T10 教训）。
            var rereadExplode = rt.ReadExplodeEvidence(drawingPath);
            // 4a. 天正会话开路（@T10 修复，顺序不可颠倒）：随后 path 形态读参——
            // ReadNativeParams(path≠null) 内部经 EnsureAppTArch 以 /p 拉起 TArch profile
            // 并把 _app 缓存；返回值即准入重读事实（以 ReadOnly=true 打开原图读完即关，
            // 不另建副本——复审 finding 3）。
            // 若先 Open（EnsureApp 默认 profile 先缓存 _app），EnsureAppTArch 将早退复用
            // 默认实例、TArch 永不加载→真机 TDb 目标全 StaleMismatch（通道失效）。
            var reread = rt.ReadNativeParams(drawingPath);
            // 准入判据（R16 裁定，spec §4.2 附加证据为约束）：**全部**步骤一律用合并判据
            // （参数门 ∨ 炸开门）——change-layer/move/delete 与 explode-native 同门，不存在
            // "句柄编辑类只认参数门"的按 kind 分派。
            // R14：合并判据永不返回参数门拒因（炸开门拒因优先），故并行调 Evaluate 取得参数门
            // 独立结论，以 paramsVerdict 加性落 refused/pass 证据；准入文案不得宣称"参数已读全"。
            var gateVerdicts = new List<(string Handle, string Verdict, string? ParamsVerdict)>();
            var explodeGateHandles = new List<string>();
            var layerNoParams = new List<(string Handle, string Verdict, string? ParamsVerdict)>();
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                var step = plan.Steps[i];
                string handle = matched[i]!;
                // 台账 T7 守卫：重读记录句柄须等于查询句柄（防管道错配误放行）；
                // 不等/缺失 → null → 参数门判 StaleMismatch。
                var rec = FindRereadByHandle(reread, handle);
                // 参数门独立结论：R14 合并判据**永不**返回参数门拒因（炸开门拒因优先），
                // 故审计必须并行调 Evaluate 才能说清"参数门为何没放行"。
                var paramsVerdict = NativeGateRefinement.Evaluate(overlay, handle, rec);
                // 炸开取证行（Ordinal 精确匹配；错配/缺失即不取用 → ExplodeNotRead/StaleExplode 兜底）。
                var ev = rereadExplode.FirstOrDefault(e =>
                    string.Equals(e.Handle, handle, StringComparison.Ordinal));
                // 合并准入（参数门 ∨ 炸开门），任一 Pass 即 Pass；两门均不过时炸开门拒因优先。
                var combined = NativeGateRefinement.EvaluateCombined(overlay, handle, rec, ev);
                if (combined != NativeGateVerdict.Pass)
                {
                    gateVerdicts.Add((handle, combined.ToString(), paramsVerdict.ToString()));
                    continue;
                }
                if (paramsVerdict == NativeGateVerdict.Pass) continue; // 参数门放行：无需披露
                // 仅凭炸开门放行（参数门未过）：
                //  · change-layer → 准入拒绝（R16 子裁定：层名读回无 before/after 参数面记录，
                //    放行后必然不可对账；move 的 bbox 基准可回退炸开证据、delete 只验句柄消失）。
                //  · move/delete/explode-native → 放行，句柄逐字落 explodeGateHandles 披露。
                if (NormalizeKind(step.Kind) == "change-layer")
                    layerNoParams.Add((handle, combined.ToString(), paramsVerdict.ToString()));
                else
                    explodeGateHandles.Add(handle);
            }
            if (gateVerdicts.Count > 0) return RefuseVerdicts(gateVerdicts);
            if (layerNoParams.Count > 0) return RefuseLayerNoParams(layerNoParams);

            // 台账 T7@T9：准入重读指纹（定序 "handle|objectName|bbox|params" 的 SHA256）
            // 加性落 native-gate-pass，供审计复核"放行所依据的重读事实"。抽检注意：该指纹
            // 只覆盖参数面记录（炸开门单凭证据放行的句柄可能无参数记录→按缺失跳过），
            // explodeGateHandles 逐字列出"未经参数门"的句柄，二者合读才是完整准入事实。
            string rereadHash = RereadHash(reread, matched);
            Audit("native-gate-pass", drawingPath, new
            {
                handles = matched,
                sourceSha,
                rereadHash,
                explodeGateHandles = explodeGateHandles.Distinct(StringComparer.Ordinal).ToArray()
            });

            // 4b. 执行窗口：Open 原图读改写（执行侧需要）——EnsureApp 复用 4a 已缓存的
            // TArch _app（同一实例开原始 DWG，TDb 存活）；实例启动已由 4a 完成。
            rt.Open(drawingPath);

            // 5. 全 Pass → 按计划顺序逐 step 执行（切片B 接替"一次性 Replay"）：
            //    explode-native → 桥事务替换并落 .live.dwg 候选；其余句柄类 kind → 单步计划
            //    过 ComReplayService（替换副本的 handle 已是 R11 命中形态，Replay 内置归一不得
            //    再改写）。packages：句柄类 kind 不消费 skill 包（仅 insert-symbol 消费，已被
            //    白名单排除），按 brief 传空表；shadow 由 Replay 形参承接（句柄类 shadow-agnostic）。
            //    native-live-step 审计仍落在读回后（move 的 bbox 不可得降级需读回结论）。
            var replay = new ComReplayService(rt);
            var explodeResults = new List<(string Hex, ExplodeReplaceResult Rep)>();
            for (int si = 0; si < plan.Steps.Count; si++)
            {
                var step = plan.Steps[si];
                string kind = NormalizeKind(step.Kind);
                string hex = matched[si]!;
                if (kind == "explode-native")
                {
                    // 桥内自证：NewCount=叶数、BboxMatch=包含性校验结论（R10 口径在桥侧算）。
                    var rep = rt.ExplodeReplaceByHandle(hex, outPath);
                    Audit("native-explode-step", hex,
                        new { hex, newCount = rep.NewCount, savedPath = rep.SavedPath, bboxMatch = rep.BboxMatch });
                    explodeResults.Add((hex, rep));
                    continue;
                }
                var single = new StepPlan(plan.PlanId, new[] { ReplacedStep(step, hex) });
                var rr = replay.Replay(single, drawing);
                if (!rr[0].Ok)
                {
                    Audit("native-live-failed", hex, new { step = kind, handle = hex, error = rr[0].Error });
                    stderr.WriteLine($"native-live 执行失败: {kind}: {rr[0].Error}（未交付 .live.dwg，原图零写入）");
                    return 1;
                }
            }

            // 6. 读回闸（参数面）：after 读一次，期望按句柄**终态聚合**比对（同句柄多步不得按
            // 首快照逐 step 误拒）：delete 支配（句柄须消失）；change-layer 末步层名胜出；
            // move 位移按句柄求和后作用于该句柄读回基准 bbox（参数面 before-bbox 优先，缺失
            // 回退准入炸开证据 bbox）。explode-native 步**不参与**聚合（其读回语义=句柄消失+
            // 叶数+bbox 包含性，见 6b；跨通道聚合会造出"期望句柄仍在"的假期望）。
            var after = rt.ReadNativeParams();
            // 读回基准（R16 子裁定）：move 期望 bbox 优先取参数面 before-bbox；参数行缺失
            // （仅凭炸开门放行的句柄）时回退准入快照的炸开证据 bbox；两者皆无 → 无基准
            // fail-closed（不得退化为"无从比对即通过"）。
            var expectations = AggregateReadback(plan, matched, reread, rereadExplode);
            foreach (var exp in expectations)
            {
                if (!exp.Delete && exp.HasMove && exp.BaselineBbox == null)
                {
                    Audit("native-live-failed", exp.Handle,
                        new { readback = "no-bbox-baseline", handle = exp.Handle });
                    stderr.WriteLine($"native-live 读回无基准: move {exp.Handle} 既无参数面 before-bbox 也无炸开证据 bbox（未交付 .live.dwg，原图零写入）");
                    return 1;
                }
                string? detail = CheckReadback(exp, after, out bool bboxNull);
                if (detail != null)
                {
                    Audit("native-live-failed", drawingPath, new { readback = detail });
                    stderr.WriteLine($"native-live 读回不符: {detail}（未交付 .live.dwg，原图零写入）");
                    return 1;
                }
                exp.BboxNull = bboxNull;
            }

            // 6b. 炸开读回（切片B）：每个 explode 结果以 null 形态读活文档——原句柄必须消失
            // （桥事务替换成功），且替换结果自证 NewCount>0（叶非空）+ BboxMatch（包含性）。
            // 任一不成立 → native-live-failed{readback:"explode-native"}，不 SaveAs 不交付。
            if (explodeResults.Count > 0)
            {
                var afterExplode = rt.ReadNativeParams();
                foreach (var (hex, rep) in explodeResults)
                {
                    string? detail = FindRereadByHandle(afterExplode, hex) != null
                        ? $"炸开替换 {hex} 后重读仍存在（替换未生效）"
                        : rep.NewCount <= 0
                            ? $"炸开替换 {hex} 叶数为 0（NewCount={rep.NewCount}）"
                            : !rep.BboxMatch
                                ? $"炸开替换 {hex} bbox 包含性校验未通过（savedPath={rep.SavedPath}）"
                                : null;
                    if (detail == null) continue;
                    Audit("native-live-failed", hex,
                        new { readback = "explode-native", handle = hex, error = detail });
                    stderr.WriteLine($"native-live 读回不符: {detail}（未交付 .live.dwg，原图零写入）");
                    return 1;
                }
            }

            // 逐 step 审计（按句柄首现+计划内顺序；kind 记运行时口径）；move 的 bbox
            // 不可得降级如实披露。explode-native 步已在执行处即时审计（native-explode-step）。
            foreach (var exp in expectations)
                foreach (var step in exp.Steps)
                {
                    string kind = NormalizeKind(step.Kind);
                    Audit("native-live-step", exp.Handle, exp.BboxNull && kind == "move"
                        ? new { kind, handle = exp.Handle, readback = "reread-bbox-null" }
                        : new { kind, handle = exp.Handle });
                }

            // 7. 交付：唯一写出是 .live.dwg 新文件（SaveAs+Close 仍在会话内；证据哈希在
            //    运行时 Dispose 后读取，见下方零写入复核）。
            rt.SaveAs(outPath);
            rt.Close(false);
            return 0; // committed/收据在零写入复核之后才落（I3）
        }

        int rc;
        try
        {
            // 4. 活会话对准**原图**（原生 DWG，TDb 存活）。运行时获取镜像 T6 R8 裁定：
            // 测试注入优先；否则按 ResolveRuntimeName 直构（不调 CreateRuntimeIfAvailable——
            // 其 GetInfo 预激活/降级文案与 native-live 语义不符；构造/Open 失败经下方 catch
            // 响亮留痕）。会话顺序（切片B 三段）：ReadExplodeEvidence(path) 炸开取证（拉起
            // TArch+NETLOAD、须无打开文档）→ ReadNativeParams(path)（拉起 TArch profile）→
            // Open 原图 → null 形态读回。
            ICadRuntime? rt = RuntimeFactory != null ? RuntimeFactory() : CreateRuntimeInstance(ResolveRuntimeName());
            if (rt == null)
            {
                Audit("native-live-failed", drawingPath, new { error = "无可用厂商运行时" });
                stderr.WriteLine("native-live 失败: 无可用厂商运行时（native-live 需活 CAD 会话）");
                return 1;
            }
            try
            {
                rc = RunSession(rt);
            }
            finally
            {
                // 证据窗口闭合（复审 finding 2a / I3）：Dispose 内部 Close(false)+Quit+等待进程退出。
                // 源件/交付件哈希必须在文档关闭且实例退出后读取——Open 期间源文件句柄由
                // AutoCAD 持有，此时读到的不完整/被拒字节会污染零写入断言。Dispose 自身不抛。
                rt.Dispose();
            }
        }
        catch (Exception ex)
        {
            Audit("native-live-failed", drawingPath, new { error = ex.Message });
            stderr.WriteLine($"native-live 失败: {ex.Message}（未交付 .live.dwg，原图零写入）");
            rc = 1;
        }

        // 8. 原图零写入复核（源件 SHA256 前后比对；窗口已闭合；成功/失败路径一律复核——
        //    I3：失败路径同样不得未经复核声称零写入）→ 唯一写出是 .live.dwg。
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
            // I3：失败路径已留原失败痕；此处追加漂移证据并强制失败（committed 不落、.live.dwg
            // 保留口径不变）。成功路径同样在此被拦下（如 Dispose 内被恶意/缺陷运行时回写源图）。
            Audit("native-live-failed", drawingPath,
                new { error = "源图字节被改动（原图零写入契约破坏）", reason = "sourceSha-changed", sourceSha, sourceShaNow },
                sourceShaNow);
            stderr.WriteLine($"native-live 失败: 源图字节被改动（原图零写入契约破坏；{outPath} 已产出但按失败退出）");
            return 1;
        }
        if (rc != 0) return rc; // 原失败路径：复核无漂移，保持原失败码/原审计，不追加成功动作
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
    /// move-entity/delete-entity → 运行时 case move/delete；其余原样（change-layer 两侧同名；
    /// 切片B explode-native 两侧同名——执行侧不经 Replay，按原名分支）。仅准入/替换副本/
    /// 聚合读回使用，ComReplayService case 词汇零改动。</summary>
    private static string NormalizeKind(string kind) => kind switch
    {
        "move-entity" => "move",
        "delete-entity" => "delete",
        "explode-native" => "explode-native",
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

    /// <summary>单步替换副本（R10/R11）：以 JsonNode 仅替换 handle 键，其余参数逐字保留；
    /// kind 同步归一（move-entity→move / delete-entity→delete，复审 finding 1）。
    /// 切片B 执行循环按计划顺序**逐 step** 归一（explode-native 走炸开通道、其余过单步
    /// Replay），故归一粒度由整计划收窄为单步——替换语义逐字不变。</summary>
    private static StepSpec ReplacedStep(StepSpec step, string hex)
    {
        var node = JsonNode.Parse(step.Params.GetRawText())
            ?? throw new InvalidPlanException("步骤参数为空，无法保留句柄替换");
        if (node is not JsonObject obj)
            throw new InvalidPlanException("步骤参数不是 JSON 对象，无法保留句柄替换");
        obj["handle"] = ReplayHandleArg(hex);
        using var doc = JsonDocument.Parse(obj.ToJsonString());
        return new StepSpec(NormalizeKind(step.Kind), doc.RootElement.Clone());
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
    /// delete 支配（句柄应消失）；change-layer 以末步层名为准；move 位移按句柄求和。
    /// BeforeParams=参数面 before 记录（缺失=该句柄仅凭炸开门放行）；BaselineBbox=move 读回
    /// 基准 bbox（参数面 before-bbox 优先，缺失回退准入快照炸开证据 bbox；皆无=null → 拒绝）。</summary>
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
        public NativeEntityParam? BeforeParams;
        public double[]? BaselineBbox;
    }

    /// <summary>按规划顺序聚合：每句柄首现固定顺序（审计/失败文案确定性），
    /// 末次 change-layer 层名覆盖前者，move 的 dx/dy 逐 step 累加，delete 置支配位。
    /// 切片B：explode-native 步**排除**在参数面聚合之外（替换后句柄必消失，其读回由
    /// 炸开通道独立校验 NewCount/BboxMatch；纳入聚合只会产生"期望句柄仍在"的假期望）。
    /// R16 子裁定：move 读回基准 bbox 在参数面 before 记录缺失（或该记录 bbox 不可得）时
    /// 回退准入快照炸开证据 bbox——仅凭炸开门放行的 move 只有这一份可核基准。</summary>
    private static List<HandleReadback> AggregateReadback(StepPlan plan, IReadOnlyList<string?> matched,
        IReadOnlyList<NativeEntityParam> rereadParams, IReadOnlyList<ExplodeEvidence> rereadExplode)
    {
        var byHandle = new Dictionary<string, HandleReadback>(StringComparer.Ordinal);
        var list = new List<HandleReadback>();
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            if (NormalizeKind(step.Kind) == "explode-native") continue;
            string handle = matched[i]!;
            if (!byHandle.TryGetValue(handle, out var exp))
            {
                exp = new HandleReadback
                {
                    Handle = handle,
                    BeforeParams = FindRereadByHandle(rereadParams, handle),
                };
                double[]? paramsBbox = exp.BeforeParams is { Bbox: { Length: 6 } b } ? b : null;
                if (paramsBbox == null)
                    paramsBbox = rereadExplode.FirstOrDefault(e =>
                        string.Equals(e.Handle, handle, StringComparison.Ordinal))?.Bbox;
                exp.BaselineBbox = paramsBbox;
                byHandle.Add(handle, exp);
                list.Add(exp); // 首现顺序
            }
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
    /// move→读回基准 bbox（参数面 before-bbox，缺失回退炸开证据 bbox）整体平移位移和
    /// (Dx,Dy)（容差 1e-6）；after bbox 不可得且参数面 before 记录存在→params 不变退化，
    /// bboxNull=true 供审计如实披露（before 记录缺失时退化必判不符，fail-closed）。</summary>
    private static string? CheckReadback(HandleReadback exp,
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
        if (exp.BaselineBbox is { Length: 6 } b0 && afterRec.Bbox is { Length: 6 } b1)
        {
            double[] deltas = { exp.Dx, exp.Dy, 0, exp.Dx, exp.Dy, 0 };
            for (int i = 0; i < 6; i++)
                // 非有限守卫（R15 同族，fail-closed）：NaN/Inf 分量不得借 `> tol` 恒 false 放行。
                if (!(Math.Abs(b1[i] - b0[i] - deltas[i]) <= BboxTolerance))
                    return $"move {exp.Handle} bbox 位移不符（分量{i} 期望 {b0[i] + deltas[i]} 实际 {b1[i]}）";
            return null;
        }
        // R6 诚实降级：after bbox 不可得（尽力读取面）→ 退化为原生参数不变检查。参数面 before
        // 记录缺失（仅凭炸开门放行）时无从比对参数 → ParamsEqual(null,…)=false 必判不符（安全侧）。
        bboxNull = true;
        if (!ParamsEqual(exp.BeforeParams?.Params, afterRec.Params))
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
