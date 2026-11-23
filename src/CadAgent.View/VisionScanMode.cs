using System.Text.Json;
using System.Text.Json.Serialization;
using CadAgent.Core;
using CadAgent.Core.Llm;
using CadAgent.Data;
using CadAgent.Data.Semantic;
using CadAgent.Data.Vision;
using CadAgent.Llm;
using CadAgent.View.Rendering;

namespace CadAgent.View;

/// <summary>
/// 视觉接线 T3：--vision-scan headless 入口编排（先于窗口 AppBuilder，无窗口生命周期）。
/// 顺序钉住：读图+census sidecar → 闸口（Red 拒绝留痕；Yellow/Green 只读审查继续）→
/// profile/key 解析 → 出境闸（BlockedLocalOnly 拒；NeedsFirstCloudConsent 须 --confirm-egress
/// 显式确认，不静默发送）→ 瓦片预渲染 → VisionReader 逐瓦读图（llm-call 每瓦留痕）→
/// VisionCrossChecker 逐幅交叉验证种子 → overlay（无 seeded 不写，防空覆盖）→ vision-scan 留痕 + stdout JSON。
/// R7 线程契约：RenderPng 须与平台初始化同线程（headless SetupWithoutStarting 无 dispatcher loop）——
/// 阶段一在主调线程同步预渲染全部瓦片 PNG，阶段二才顺序 await reader（await 后不再渲染）。
/// R3：--confirm-egress 即显式确认通道本身——生产由人工权衡后手动给 flag（T4 协议注记）；
/// 测试行使该通道（用户先前已授权云端测试），不伪造人工确认语义。
/// 密钥仅入 OpenAiClient；审计仅 model/usage/attempt；stdout 不含密钥。
/// </summary>
internal static class VisionScanMode
{
    // 瓦片像素尺寸与 SheetTiler 默认视口一致（可读性前提：目标字高 24px 按此视口反算世界区域）
    private const int TileW = 2800;
    private const int TileH = 1750;

    // stdout JSON 契约（镜像 Cli SemanticJsonOpts 口径）：camelCase + 中文按字面（终端/管道消费）
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// 入口：exit 0=扫描完成（Yellow 只读审查亦 0）；exit 1=用法/闸口/出境/异常拒绝。
    /// clientOverride/profileOverride 仅测试注入（fake 不触网）；生产路径 LlmConfigStore + DPAPI 解析。
    /// </summary>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr,
        ILlmClient? clientOverride = null, ProviderProfile? profileOverride = null)
    {
        // 1. 用法解析（先于任何副作用）
        string? drawing = GetOption(args, "--drawing");
        if (drawing is null)
        {
            stderr.WriteLine("用法: CadAgent.View --vision-scan --drawing <file> [--project <dir>] [--confirm-egress] [--name <profile>] [--max-tiles <正整数>]");
            return 1;
        }
        string project = GetOption(args, "--project") ?? Directory.GetCurrentDirectory();
        bool confirmEgress = args.Contains("--confirm-egress");
        string? profileName = GetOption(args, "--name");
        // T4 成本闸：--max-tiles 瓦片预算（正整数；跨幅面按产瓦顺序取前 N，截断必须披露）。
        // 用法错误先于一切副作用（不建 .cadagent/审计——check --skill 冲突先例）。
        int? maxTiles = null;
        string? maxTilesRaw = GetOption(args, "--max-tiles");
        if (maxTilesRaw is not null)
        {
            if (!int.TryParse(maxTilesRaw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int n) || n < 1)
            {
                stderr.WriteLine($"用法: --max-tiles 须为正整数（收到: {maxTilesRaw}）");
                return 1;
            }
            maxTiles = n;
        }
        string agentDir = Path.Combine(project, ".cadagent");
        string session = GetOption(args, "--session") ?? Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(agentDir);
            using var audit = new SqliteAuditStore(Path.Combine(agentDir, "audit.db"),
                new CadAgent.Core.Audit.AnchorService(agentDir));
            var op = new WindowsIdentityProvider(RoleConfig.Load(Path.Combine(agentDir, "roles.yaml"))).Current;
            return RunCore(drawing, project, agentDir, session, confirmEgress, profileName, maxTiles,
                clientOverride, profileOverride, audit, op, stdout, stderr);
        }
        catch (Exception ex)
        {
            // 异常兜底：stderr + exit 1（不崩进程栈）
            stderr.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunCore(string drawing, string project, string agentDir, string session,
        bool confirmEgress, string? profileName, int? maxTiles, ILlmClient? clientOverride, ProviderProfile? profileOverride,
        SqliteAuditStore audit, OperatorIdentity op, TextWriter stdout, TextWriter stderr)
    {
        // 2. 读图 + census sidecar（与 Cli LoadWithCensus 同模式：persisted 直载 + 非空回写）
        var doc = LoadOrReject(() =>
        {
            var persisted = UnparsedCensusStore.Load(project, drawing);
            var s = DrawingSession.Load(drawing, persisted);
            if (s.Unparsed.TotalCount > 0)
                UnparsedCensusStore.Save(project, drawing, s.Unparsed);
            return s;
        }, drawing, session, audit, op, stderr);
        if (doc is null) return 1;

        // 3. 准入闸：视觉扫描只读不改图——仅 Red 拒绝（零原生实体，读图无意义）；
        // Yellow 继续（只读审查）。注意与 Cli GateRefusesRun（Level!=Green，修改闸）口径不同：
        // 此处等价判定=Level==Red，Yellow 视觉读图属只读审查放行（绑定约束：Red 拒，Yellow/Green 继续）。
        var gate = GateDetector.Inspect(doc.Summarize(), new EmptySupportMatrix());
        if (gate.Level == GateLevel.Red)
        {
            string gateHash = doc.ComputeHash();
            Append(audit, op, session, "run-rejected", drawing, new
            {
                level = gate.Level.ToString(),
                unsupportedCount = gate.Unsupported.Count,
                unparsedCount = doc.Unparsed.TotalCount,
                guidance = gate.Guidance
            }, gateHash, gateHash);
            stderr.WriteLine(gate.Guidance);
            return 1;
        }

        // 4. profile/key（Agent.cs 同模式：活动/--name → DPAPI/env；缺→指引 exit 1）
        ProviderProfile? profile = profileOverride;
        if (profile is null)
        {
            var store = LlmConfigStore.Load(Path.Combine(agentDir, "llm-profiles.json"));
            string? name = profileName ?? store.ActiveProfileName;
            if (name is null) { stderr.WriteLine("无活动 profile，请先 llm use 或 --name 指定"); return 1; }
            profile = store.Profiles.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (profile is null) { stderr.WriteLine($"profile 不存在: {name}"); return 1; }
        }
        string? key = null;
        if (clientOverride is null)
        {
            var protector = new DpapiKeyProtector(Path.Combine(agentDir, "keys"));
            key = DpapiKeyProtector.ResolveKey(protector, profile.Name);
            if (string.IsNullOrEmpty(key)) { stderr.WriteLine("无可用密钥（llm add --key 或 CADAGENT_LLM_API_KEY）"); return 1; }
        }

        // 5. 出境闸（EgressPolicy(agentDir) 与 Agent.cs/Repl 同口径：local-only.txt/cloud-consent.json
        // 落 .cadagent——同一项目约束/同意文件跨命令一致，不得绕开既有 local-only 硬约束）。
        var egress = new EgressPolicy(agentDir);
        var decision = egress.Evaluate(profile, drawingData: true);
        if (decision == EgressDecision.BlockedLocalOnly)
        {
            Append(audit, op, session, "egress-blocked", profile.Name,
                new { reason = "local-only 约束", profile = profile.Name });
            stderr.WriteLine("数据出境被本地约束阻止（local-only）");
            return 1;
        }
        if (decision == EgressDecision.NeedsFirstCloudConsent)
        {
            if (!confirmEgress)
            {
                Append(audit, op, session, "egress-blocked", profile.Name,
                    new { reason = "首次云端出境未获确认", profile = profile.Name });
                stderr.WriteLine($"首次云端出境需 --confirm-egress 明确确认（图纸数据将发送至 {profile.Endpoint}）");
                return 1;
            }
            // R3：flag 即显式确认通道（生产人工手动给定；测试经授权行使）——确认持久化 + 留痕
            egress.ConfirmCloudConsent();
            Append(audit, op, session, "egress-consent", profile.Name,
                new { profile = profile.Name, endpoint = profile.Endpoint });
        }

        // 6. 遍历/瓦片/provider/reader（密钥仅入 OpenAiClient，reader 不触密钥）
        var walked = BlockWalker.Walk(doc.Doc, doc.Sheets).ToList();
        var tiles = doc.Sheets.SelectMany(s => SheetTiler.Tiles(s, walked)).ToList();
        // T4 成本闸：瓦片预算截断（跨幅面按产瓦顺序取前 N）——截断必须披露（stderr+stdout+审计），
        // 未读瓦片/幅面不假装已审查；无预算→全量行为逐字保持。
        int totalTiles = tiles.Count;
        if (maxTiles is { } cap && tiles.Count > cap)
        {
            tiles = tiles.Take(cap).ToList();
            stderr.WriteLine($"瓦片预算 --max-tiles {cap}：共 {totalTiles} 瓦，仅读前 {tiles.Count} 瓦（其余未读，非全量审查）");
        }
        var provider = new RenderSnapshotProvider(doc);
        ILlmClient client = clientOverride ?? new OpenAiClient(profile, key!);
        var reader = new VisionReader(client, profile.Model);

        // 7-阶段一（R7 线程契约，顺序钉住）：主调线程同步预渲染全部瓦片 PNG——
        // RenderPng 须与平台初始化同线程（EnsurePlatform 在本线程 SetupWithoutStarting，
        // headless 无 dispatcher loop）；await 之后绝不再渲染。
        var rendered = new List<(string SheetId, (double MinX, double MinY, double MaxX, double MaxY) Rect, byte[] Png)>();
        foreach (var (sheetId, rect) in tiles)
            rendered.Add((sheetId, rect, provider.RenderPng(sheetId, rect, TileW, TileH)));

        // 7-阶段二（R7）：渲染全部完成后才顺序 await reader（Task.Run 线程池执行，
        // 不捕获主调线程上下文——headless 测试会话 dispatcher 阻塞等待亦不死锁）。
        var read = Task.Run(() => ReadTilesAsync(reader, rendered, audit, op, session, profile.Model))
            .GetAwaiter().GetResult();

        // 8. 逐幅交叉验证（claims 按 sheet 聚合；种子 Upsert，歧义聚合去重）
        string overlayPath = Path.Combine(agentDir, "semantic.json");
        var overlay = LoadOrReject(() => SemanticOverlay.Load(overlayPath), overlayPath, session, audit, op, stderr);
        if (overlay is null) return 1;
        int seededCount = 0, claimCount = 0;
        var ambiguous = new List<VisionAmbiguous>();
        foreach (var sheet in doc.Sheets)
        {
            read.ClaimsBySheet.TryGetValue(sheet.SheetId, out var sheetClaimsRaw);
            var sheetClaims = (sheetClaimsRaw ?? new List<VisionClaim>()).Distinct().ToList();
            claimCount += sheetClaims.Count;
            var cross = VisionCrossChecker.Check(new VisionReadResult(sheetClaims), overlay, walked, sheet.SheetId);
            foreach (var a in cross.Seeded) { overlay.UpsertAnnotation(a); seededCount++; }
            ambiguous.AddRange(cross.Ambiguous);
        }
        var distinctAmbiguous = ambiguous.Distinct().ToList();

        // 9. 无 seeded 不写 overlay（防空覆盖：Save=Version++ 原子写，零种子不得覆盖既有人工确认）
        if (seededCount > 0)
            overlay.Save(overlayPath);

        // 10. vision-scan 留痕（扫描不改图：前后哈希一致）+ stdout JSON + exit 0
        // 预算披露（仅 --max-tiles 给定时加性输出 tilesTotal/maxTiles；无预算契约逐字不变）。
        string hash = doc.ComputeHash();
        var usage = new { promptTokens = read.PromptTokens, completionTokens = read.CompletionTokens };
        object scanPayload = maxTiles is null
            ? new { tiles = rendered.Count, claims = claimCount, seeded = seededCount, ambiguousCount = distinctAmbiguous.Count, usage }
            : new { tiles = rendered.Count, tilesTotal = totalTiles, maxTiles = maxTiles.Value, claims = claimCount, seeded = seededCount, ambiguousCount = distinctAmbiguous.Count, usage };
        Append(audit, op, session, "vision-scan", drawing, scanPayload, hash, hash);
        object stdoutPayload = maxTiles is null
            ? new { ok = true, level = gate.Level.ToString(), tiles = rendered.Count, claims = claimCount, seeded = seededCount, ambiguous = distinctAmbiguous, usage, parseErrors = read.ParseErrors }
            : new { ok = true, level = gate.Level.ToString(), tiles = rendered.Count, tilesTotal = totalTiles, maxTiles = maxTiles.Value, claims = claimCount, seeded = seededCount, ambiguous = distinctAmbiguous, usage, parseErrors = read.ParseErrors };
        stdout.WriteLine(JsonSerializer.Serialize(stdoutPayload, JsonOpts));
        return 0;
    }

    /// <summary>读瓦片阶段产出：claims 按幅面聚合 + 解析错误清单 + usage 合计。</summary>
    private sealed record ReadPhaseResult(
        Dictionary<string, List<VisionClaim>> ClaimsBySheet, List<string> ParseErrors,
        int PromptTokens, int CompletionTokens);

    /// <summary>
    /// R7 阶段二：顺序 await reader（无渲染）；llm-call 每瓦留痕——payload 镜像
    /// AgentOrchestrator.AuditLlmCall（Source=llm，Target=model，仅 model/usage/attempt/planId，
    /// 无图纸内容/密钥）。解析失败→ParseError 收集（诚实降级，不编造）。
    /// </summary>
    private static async Task<ReadPhaseResult> ReadTilesAsync(VisionReader reader,
        List<(string SheetId, (double MinX, double MinY, double MaxX, double MaxY) Rect, byte[] Png)> rendered,
        SqliteAuditStore audit, OperatorIdentity op, string session, string model)
    {
        var claimsBySheet = new Dictionary<string, List<VisionClaim>>();
        var parseErrors = new List<string>();
        int promptTokens = 0, completionTokens = 0, attempt = 0;
        foreach (var (sheetId, rect, png) in rendered)
        {
            var read = await reader.ReadTileAsync(png, sheetId, rect).ConfigureAwait(false);
            attempt++;
            audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(), session,
                "llm", "llm-call", model,
                JsonSerializer.Serialize(new { model, read.Usage.PromptTokens, read.Usage.CompletionTokens, attempt, planId = (string?)null }),
                "", "", "", ""));
            promptTokens += read.Usage.PromptTokens;
            completionTokens += read.Usage.CompletionTokens;
            if (read.ParseError is not null) parseErrors.Add(read.ParseError);
            if (!claimsBySheet.TryGetValue(sheetId, out var list))
                claimsBySheet[sheetId] = list = new List<VisionClaim>();
            list.AddRange(read.Claims);
        }
        return new ReadPhaseResult(claimsBySheet, parseErrors, promptTokens, completionTokens);
    }

    /// <summary>vision-scan 族审计（Source=vision-scan；egress kind payload 镜像 AgentOrchestrator.AuditAgent）。</summary>
    private static void Append(SqliteAuditStore audit, OperatorIdentity op, string session,
        string kind, string target, object payload, string before = "", string after = "") =>
        audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(), session,
            "vision-scan", kind, target, JsonSerializer.Serialize(payload), before, after, "", ""));

    /// <summary>加载守卫（镜像 Cli LoadOrReject：错误入审计 intake-rejected + stderr，返回 null=已拒绝）。</summary>
    private static T? LoadOrReject<T>(Func<T> load, string file, string session,
        SqliteAuditStore audit, OperatorIdentity op, TextWriter stderr) where T : class
    {
        try
        {
            return load();
        }
        catch (Exception ex)
        {
            Append(audit, op, session, "intake-rejected", file, new { Error = ex.Message, File = file });
            stderr.WriteLine(ex.Message);
            return null;
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
