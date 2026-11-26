using System.Collections.ObjectModel;
using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data.Semantic;

namespace CadAgent.View.ViewModels;

/// <summary>原生参数区行：一次 COM 读参的只读展示（Class=语义类名，Layer 缺失记 "—"）。
/// FailReason：NativeEntityParam 无独立失败原因字段——Params 的 Error/FailReason 键为
/// 适配器留痕通道，读不到且 ReadOk=false 时给诚实占位 "ReadOk=false"（不猜原因）。</summary>
public sealed record NativeParamRow(
    string Handle, string ObjectName, string Class, string Layer,
    bool ReadOk, string ParamsSummary, string? FailReason);

/// <summary>炸开证据区行：Degenerate=首条/全部失败原因含 "extents:degenerate"（退化包围盒）。</summary>
public sealed record ExplodeRow(
    string Handle, string RxClass, bool FullyExplodable, int PrimitiveCount,
    int NestedTdbCount, bool Degenerate, string? FailReasonsSummary);

/// <summary>符号候选区行：从炸开基元 Kind=="block" 按天正库块名前缀重算（只披露不落标注）。</summary>
public sealed record SymbolCandidateRow(string Handle, string BlockName, string Kind, string ClassHint);

/// <summary>块定义区行：定义段 (块名, 类) 计数 + 来源图纸哈希；IsCurrentDrawing
/// null=行哈希或当前哈希缺失（不可比不猜），false=同名块来自其它图纸（计数不得混读）。</summary>
public sealed record BlockDefRow(string BlockName, string RxClass, int Count, string? DrawingHash, bool? IsCurrentDrawing);

/// <summary>证据状态区行：逐句柄两表可验性汇总（只读预览，非放行依据）。</summary>
public sealed record EvidenceStateRow(string Handle, string ParamsState, string ExplodeState, string Advisory);

/// <summary>
/// 天读证据面板视图模型（只读）：从 agentDir/semantic.json 读 NativeEntities/
/// ExplodeEvidence/BlockDefTdb，重建五区行（原生参数/炸开证据/符号候选/块定义/
/// 证据状态）并按句柄 Ordinal 排序。加载守卫镜像 SemanticViewModel：overlay 损坏/
/// IO 错误 → ErrorMessage 设置、五区清空（不抛异常）；**只读面板不写审计、不落盘**。
/// 中心线覆盖：逐条解析 ExplodeEvidence.CenterlineJson（kind=line/p1/p2），畸形行
/// 跳过并计入 SkewedCenterlines；HasCenterlines 供 UI 决定开关可用性。
/// 块定义来源哈希由调用方经 currentHashProvider 提供（每轮 Reload 现取；缺省→null）。
/// </summary>
public sealed class DeepReadViewModel
{
    private readonly Func<string?>? _currentHashProvider;

    public DeepReadViewModel(string agentDir, Func<string?>? currentHashProvider = null)
    {
        AgentDir = agentDir;
        _currentHashProvider = currentHashProvider;
        Reload();
    }

    /// <summary>overlay 所在目录（调用方传 .cadagent 目录）。</summary>
    public string AgentDir { get; }

    /// <summary>overlay 文件路径（.cadagent/semantic.json）。</summary>
    public string OverlayPath => Path.Combine(AgentDir, "semantic.json");

    /// <summary>最近一次加载失败原因（overlay 损坏/IO）；null=成功。</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>原生参数区（句柄 Ordinal 序）。</summary>
    public ObservableCollection<NativeParamRow> NativeRows { get; } = new();

    /// <summary>炸开证据区（句柄 Ordinal 序）。</summary>
    public ObservableCollection<ExplodeRow> ExplodeRows { get; } = new();

    /// <summary>符号候选区（句柄→块名 序；来自炸开基元前缀重算）。</summary>
    public ObservableCollection<SymbolCandidateRow> SymbolRows { get; } = new();

    /// <summary>块定义区（块名→类 序）。</summary>
    public ObservableCollection<BlockDefRow> BlockDefRows { get; } = new();

    /// <summary>证据状态区（原生/炸开句柄并集，Ordinal 序）。</summary>
    public ObservableCollection<EvidenceStateRow> EvidenceRows { get; } = new();

    /// <summary>可绘制中心线段（仅 kind=line 且 p1/p2 ≥2 有限分量）。</summary>
    public IReadOnlyList<CenterlineSeg> Centerlines { get; private set; } = Array.Empty<CenterlineSeg>();

    /// <summary>畸形中心线 JSON 行数（跳过并披露，不静默）。</summary>
    public int SkewedCenterlines { get; private set; }

    /// <summary>是否有可绘制中心线（UI 开关可用性）。</summary>
    public bool HasCenterlines => Centerlines.Count > 0;

    /// <summary>五区计数摘要（头部展示）。</summary>
    public string CountsSummary =>
        $"原生参数 {NativeRows.Count} · 炸开证据 {ExplodeRows.Count} · 符号候选 {SymbolRows.Count}" +
        $" · 块定义 {BlockDefRows.Count} · 证据状态 {EvidenceRows.Count}";

    /// <summary>
    /// 只读重读 overlay 并重建五区（初始加载/语义扫描完成/编辑刷新后调用）。
    /// 任何异常（JSON 损坏/IO）→ ErrorMessage + 五区清空，不抛出。
    /// </summary>
    public void Reload()
    {
        try
        {
            SemanticOverlay overlay = SemanticOverlay.Load(OverlayPath);
            // 块定义来源哈希：仅存在定义段时现取（无定义段不付哈希计算代价）。
            string? currentHash = overlay.BlockDefTdb.Count > 0 ? SafeCurrentHash() : null;

            Clear();
            foreach (NativeEntityParam p in overlay.NativeEntities
                .Where(p => p is not null)
                .OrderBy(p => p.Handle, StringComparer.Ordinal))
                NativeRows.Add(ToNativeRow(p));

            foreach (ExplodeEvidence e in overlay.ExplodeEvidence
                .Where(e => e is not null)
                .OrderBy(e => e.Handle, StringComparer.Ordinal))
            {
                ExplodeRows.Add(ToExplodeRow(e));
                foreach (SymbolCandidateRow symbol in SymbolRowsOf(e)) SymbolRows.Add(symbol);
            }

            List<BlockDefRow> blockDefs = new();
            foreach (BlockDefTdbEntry d in overlay.BlockDefTdb
                .Where(d => d is not null)
                .OrderBy(d => d.BlockName, StringComparer.Ordinal)
                .ThenBy(d => d.RxClass, StringComparer.Ordinal))
            {
                bool? isCurrent = d.DrawingHash is null || currentHash is null
                    ? null
                    : string.Equals(d.DrawingHash, currentHash, StringComparison.Ordinal);
                blockDefs.Add(new BlockDefRow(d.BlockName, d.RxClass, d.Count, d.DrawingHash, isCurrent));
            }
            foreach (BlockDefRow row in blockDefs) BlockDefRows.Add(row);

            foreach (EvidenceStateRow row in EvidenceStates(overlay)) EvidenceRows.Add(row);

            Centerlines = ParseCenterlines(overlay.ExplodeEvidence, out int skewed);
            SkewedCenterlines = skewed;
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            Clear();
            ErrorMessage = ex.Message;
        }
    }

    private void Clear()
    {
        NativeRows.Clear();
        ExplodeRows.Clear();
        SymbolRows.Clear();
        BlockDefRows.Clear();
        EvidenceRows.Clear();
        Centerlines = Array.Empty<CenterlineSeg>();
        SkewedCenterlines = 0;
    }

    private static NativeParamRow ToNativeRow(NativeEntityParam p) => new(
        p.Handle ?? "",
        p.ObjectName ?? "",
        p.Class.ToString(),
        p.Layer ?? "—",
        p.ReadOk,
        SummarizeParams(p.Params),
        p.ReadOk ? null : NativeFailReason(p));

    /// <summary>参数摘要：键 Ordinal 序 "k=v"，"; " 连接（确定性展示）。</summary>
    private static string SummarizeParams(IReadOnlyDictionary<string, string>? parms) =>
        parms is null || parms.Count == 0
            ? ""
            : string.Join("; ", parms.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));

    private static string? NativeFailReason(NativeEntityParam p)
    {
        foreach (string key in new[] { "Error", "FailReason" })
            if (p.Params is not null && p.Params.TryGetValue(key, out string? value) && value is not null)
                return value;
        return "ReadOk=false";
    }

    private static ExplodeRow ToExplodeRow(ExplodeEvidence e) => new(
        e.Handle,
        e.RxClass,
        e.FullyExplodable,
        e.PrimitiveCount,
        e.NestedTdbCount,
        IsDegenerate(e),
        SummarizeFailReasons(e));

    private static bool IsDegenerate(ExplodeEvidence e) =>
        ContainsDegenerate(e.FailReason)
        || (e.FailReasons?.Any(r => ContainsDegenerate(r)) ?? false);

    private static bool ContainsDegenerate(string? reason) =>
        reason is not null && reason.Contains("extents:degenerate", StringComparison.Ordinal);

    /// <summary>首条原因在前，其余按桥输出序去重追加（"; " 连接）；无失败行 → null。</summary>
    private static string? SummarizeFailReasons(ExplodeEvidence e)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(e.FailReason)) parts.Add(e.FailReason!);
        if (e.FailReasons is not null)
            foreach (string reason in e.FailReasons)
                if (!string.IsNullOrEmpty(reason) && !parts.Contains(reason, StringComparer.Ordinal))
                    parts.Add(reason);
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>符号候选重算：Kind=="block" 且命中天正库块名前缀（TarchSymbolPrefixes，
    /// Data 层公开口径——不复制常量）。</summary>
    private static IEnumerable<SymbolCandidateRow> SymbolRowsOf(ExplodeEvidence e)
    {
        if (e.Primitives is null) yield break;
        foreach (ExplodePrimitive prim in e.Primitives)
        {
            if (prim is null || !string.Equals(prim.Kind, "block", StringComparison.Ordinal)) continue;
            if (prim.BlockName is null) continue;
            (string Kind, string ClassHint)? hit = TarchSymbolPrefixes.Match(prim.BlockName);
            if (hit is null) continue;
            yield return new SymbolCandidateRow(e.Handle, prim.BlockName, hit.Value.Kind, hit.Value.ClassHint);
        }
    }

    /// <summary>逐句柄证据状态：原生/炸开两表并集（Ordinal 序，句柄匹配大小写不敏感）。</summary>
    private static IEnumerable<EvidenceStateRow> EvidenceStates(SemanticOverlay overlay)
    {
        List<string> handles = overlay.NativeEntities.Select(n => n.Handle)
            .Concat(overlay.ExplodeEvidence.Select(e => e.Handle))
            .Where(h => !string.IsNullOrEmpty(h))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToList();
        foreach (string handle in handles)
        {
            NativeEntityParam? native = overlay.NativeEntities
                .FirstOrDefault(n => string.Equals(n.Handle, handle, StringComparison.OrdinalIgnoreCase));
            ExplodeEvidence? explode = overlay.ExplodeEvidence
                .FirstOrDefault(e => string.Equals(e.Handle, handle, StringComparison.OrdinalIgnoreCase));

            string paramsState = native is null ? "不可验(缺行)" : native.ReadOk ? "可验" : "不可验(ReadOk=false)";
            string explodeState = explode is null ? "不可验(缺失)"
                : !explode.FullyExplodable ? "不可验(未全炸)"
                : explode.Bbox is null ? "不可验(bbox缺失)"
                : "可验";
            string advisory = paramsState == "可验" && explodeState == "可验"
                ? "参数与炸开证据均可验（只读预览，非放行依据）"
                : "证据不完整（只读预览，非放行依据）";
            yield return new EvidenceStateRow(handle, paramsState, explodeState, advisory);
        }
    }

    /// <summary>中心线解析：畸形行跳过并计数（不静默吞掉——SkewedCenterlines 披露）。</summary>
    private static IReadOnlyList<CenterlineSeg> ParseCenterlines(
        IEnumerable<ExplodeEvidence> evidences, out int skewed)
    {
        var segments = new List<CenterlineSeg>();
        skewed = 0;
        foreach (ExplodeEvidence e in evidences
            .Where(e => e is not null)
            .OrderBy(e => e.Handle, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(e.CenterlineJson)) continue;
            if (TryParseCenterline(e.Handle, e.CenterlineJson!, out CenterlineSeg? seg))
                segments.Add(seg!);
            else
                skewed++;
        }
        return segments;
    }

    /// <summary>单条中心线 JSON（kind=line, p1/p2=[x,y,(z)], width）：结构不合法 → false。</summary>
    private static bool TryParseCenterline(string handle, string json, out CenterlineSeg? seg)
    {
        seg = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("kind", out JsonElement kind)
                || kind.ValueKind != JsonValueKind.String
                || !string.Equals(kind.GetString(), "line", StringComparison.Ordinal)) return false;
            if (!TryPoint(root, "p1", out double x1, out double y1)) return false;
            if (!TryPoint(root, "p2", out double x2, out double y2)) return false;
            seg = new CenterlineSeg(handle, x1, y1, x2, y2);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryPoint(JsonElement root, string name, out double x, out double y)
    {
        x = y = 0;
        if (!root.TryGetProperty(name, out JsonElement point)
            || point.ValueKind != JsonValueKind.Array
            || point.GetArrayLength() < 2) return false;
        if (!point[0].TryGetDouble(out x) || !point[1].TryGetDouble(out y)) return false;
        return double.IsFinite(x) && double.IsFinite(y);
    }

    /// <summary>当前图纸哈希（调用方提供）；不可得/抛异常 → null（块定义行不猜来源）。</summary>
    private string? SafeCurrentHash()
    {
        if (_currentHashProvider is null) return null;
        try { return _currentHashProvider(); }
        catch (Exception) { return null; } // 只读面板：哈希不可得不阻断加载
    }
}
