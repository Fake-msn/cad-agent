using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CadAgent.Core;

namespace CadAgent.Data.Semantic;

// records namespace 级 public（ScanResult public 引用要求；类保持 internal）
public sealed record BlockCandidate(string SymbolName, double Distance, double Confidence);
public sealed record BlockSuggestion(string BlockName, int Instances, IReadOnlyList<BlockCandidate> Candidates);
/// <summary>
/// 实体类型直方图条目（R9②，spec §13.2 歧义诊断）。**必须命名 record**——
/// ValueTuple 的 Item1/Item2 是字段非属性，System.Text.Json 会序列化为 {}（陷阱钉死）。
/// </summary>
public sealed record BlockHistEntry(string Type, int Count);
/// <summary>块定义几何 bbox（R9②，PointsOf 口径；零几何→null）。命名 record 同上防 {} 退化。</summary>
public sealed record BlockBbox(double MinX, double MinY, double MaxX, double MaxY);
/// <summary>
/// 块级歧义项。R9② 加性末位：Histogram=块定义全部实体类型计数（含非白名单，诊断全量口径，
/// 按 Type Ordinal 排序）；Bbox=块定义几何 bbox（BboxOf 口径：零 PointsOf 点→null；
/// Hatch+AttDef 代理块可得退化点 bbox）。旧四字段逐字不变。
/// </summary>
public sealed record BlockAmbiguous(string BlockName, int Instances, string Reason, IReadOnlyList<string> SampleHandles,
    IReadOnlyList<BlockHistEntry> Histogram, BlockBbox? Bbox);
public sealed record BlockAutoSeed(string BlockName, string SymbolName, IReadOnlyList<string> Handles);
/// <summary>
/// R9③ 加性末位 NonFiniteDropped：matcher 内块+符号全部 Extract 调用剔除的非有限坐标实体
/// 累计数（spec §13.4 "剔除+披露"）；无符号早退路径 0。旧七字段逐字不变。
/// </summary>
public sealed record BlockMatchResult(
    IReadOnlyList<BlockAutoSeed> AutoSeeds, IReadOnlyList<BlockSuggestion> Suggestions,
    IReadOnlyList<BlockAmbiguous> Ambiguous, int SymbolsLoaded, int SymbolsSkipped,
    int BlocksScanned, int BlocksExcluded, int NonFiniteDropped);

/// <summary>
/// 块级纠偏三级匹配（spec §13.3）：符号索引（skill 包 DXF）→ 排除启发 →
/// 精确自动种子 GeometryInference 0.9 / 近似建议（不自动）/ 无命中高频歧义。
/// 自动种子 marker evidence 前缀="几何指纹精确匹配:"——re-scan 仅清此 marker（保墙/轴启发式）。
/// 精确多符号命中取首（包序+yaml 序确定性，镜像 insert-symbol 首包命中）；
/// 距离消费固定序 Distance(块fp, 符号fp)（T1 非对称口径）。
/// 歧义 reason 存短值{"代理图形不可读","无匹配"}——T3 指引后缀由 CLI 披露侧拼接，不入 record。
/// </summary>
internal static class BlockSymbolMatcher
{
    internal const string EvidencePrefix = "几何指纹精确匹配:";

    /// <summary>排除启发：系统/图框前缀（OrdinalIgnoreCase）、空定义、ATTDEF 且 bbox 最长边≥500（图框特征）。</summary>
    internal static bool IsExcluded(BlockRecord block)
    {
        string n = block.Name;
        if (n.StartsWith("*Model", StringComparison.OrdinalIgnoreCase) || n.StartsWith("*Paper", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.StartsWith("$LinePat$", StringComparison.OrdinalIgnoreCase) || n.StartsWith("$TCHSYS$", StringComparison.OrdinalIgnoreCase)) return true;
        var ents = block.Entities.ToList();
        if (ents.Count == 0) return true;
        if (ents.Any(e => e is AttributeDefinition))
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var e in ents)
                foreach (var (x, y) in PointsOf(e))
                { if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y; }
            if (maxX > minX && Math.Max(maxX - minX, maxY - minY) >= 500) return true;
        }
        return false;
    }

    // bbox 贡献点（排除启发专用）：线端点/圆外接矩形两角/多段线顶点/文本插入点
    private static IEnumerable<(double X, double Y)> PointsOf(Entity e) => e switch
    {
        Line l => new[] { (l.StartPoint.X, l.StartPoint.Y), (l.EndPoint.X, l.EndPoint.Y) },
        Circle c => new[] { (c.Center.X - c.Radius, c.Center.Y - c.Radius), (c.Center.X + c.Radius, c.Center.Y + c.Radius) },
        LwPolyline lw => lw.Vertices.Select(v => (v.Location.X, v.Location.Y)),
        TextEntity t => new[] { (t.InsertPoint.X, t.InsertPoint.Y) },
        _ => Array.Empty<(double, double)>(),
    };

    // R9② 诊断直方图：块定义**全部实体**类型计数（GetType().Name，含非白名单——诊断全量口径，
    // 与指纹白名单剔除口径区分）；按 Type Ordinal 排序（披露确定性）。
    private static IReadOnlyList<BlockHistEntry> HistogramOf(IEnumerable<Entity> ents) =>
        ents.GroupBy(e => e.GetType().Name, StringComparer.Ordinal)
            .Select(g => new BlockHistEntry(g.Key, g.Count()))
            .OrderBy(h => h.Type, StringComparer.Ordinal)
            .ToList();

    // R9② 诊断 bbox：复用 PointsOf 口径（线端点/圆外接两角/多段线顶点/文本插入点——
    // AttDef : TextEntity 插入点亦参与）。整条口径与 Extract 一致：任一点非有限→该实体整条
    // 跳过（NaN/∞ 不入 JSON——System.Text.Json 默认拒绝非有限 double）；零点→null。
    private static BlockBbox? BboxOf(IEnumerable<Entity> ents)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;
        foreach (var e in ents)
        {
            var pts = PointsOf(e).ToList();
            if (pts.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))) continue;
            foreach (var (x, y) in pts)
            {
                any = true;
                if (x < minX) minX = x; if (y < minY) minY = y;
                if (x > maxX) maxX = x; if (y > maxY) maxY = y;
            }
        }
        return any ? new BlockBbox(minX, minY, maxX, maxY) : null;
    }

    /// <summary>
    /// 三级匹配主入口：自动种子直接落 overlay（建议/歧义只披露不落）。
    /// 符号读失败/超白名单→跳过计数，不整体失败；无符号库→空结果且不动 overlay
    /// （防误清既有种子——无索引时"无匹配"歧义亦无意义）。
    /// </summary>
    internal static BlockMatchResult MatchAndSeed(
        CadDocument doc, IReadOnlyList<WalkedEntity> walked, SemanticOverlay overlay,
        IReadOnlyList<SkillPackage> packages, int minInstances = 3)
    {
        // 符号索引（读失败/超白名单→跳过计数，不整体失败）；白名单校验先于 Extract。
        // R2 catch 面实证（ACadSharp 3.8.35，坏 DXF 夹具）：DxfReader 抛型异构且不可靠枚举——
        // 文本/空/二进制垃圾→EndOfStreamException(:IOException)；截断/结构非法→DxfException(:Exception)
        // 或 NullReferenceException（库内 DxfStreamReaderBase.get_ValueAsString 空引用）。
        // 符号 DXF 为外部不可信输入，任何读取失败语义统一=跳过该符号；spec 硬性"不整体失败"，
        // 故此处宽捕（同 ComReplayService.Replay / StepEngine 隔离先例），漏型即整体失败。
        var symbols = new List<(string Name, SemanticClass Class, BlockFingerprint.Fingerprint Fp)>();
        int skipped = 0;
        int nonFiniteDropped = 0; // R9③：块+符号全部 Extract 调用剔除数累计（早退路径归 0）
        foreach (var pkg in packages)
            foreach (var meta in pkg.Symbols)
            {
                try
                {
                    string path = Path.Combine(pkg.SourceDir, "symbols", meta.File);
                    using var fs = File.OpenRead(path);
                    var src = new DxfReader(fs).Read();
                    if (src.Entities.Any(e => e is not (Line or Circle or Arc or TextEntity))) { skipped++; continue; }
                    var fp = BlockFingerprint.Extract(src.Entities, out int symDropped);
                    nonFiniteDropped += symDropped; // null（跳过）路径剔除数亦披露
                    if (fp is null) { skipped++; continue; }
                    symbols.Add((meta.Name, meta.Class, fp));
                }
                catch (Exception)
                { skipped++; }
            }

        // 无符号库→空结果（不清 overlay、不报歧义——无索引可匹配）；早退 NonFiniteDropped=0
        if (symbols.Count == 0)
            return new BlockMatchResult(Array.Empty<BlockAutoSeed>(), Array.Empty<BlockSuggestion>(),
                Array.Empty<BlockAmbiguous>(), 0, skipped, 0, 0, 0);

        // re-scan 清陈旧块级种子（仅 marker；保墙/轴 GeometryInference 与 HumanConfirmed）
        overlay.Annotations.RemoveAll(a => a.Provenance == Provenance.GeometryInference
            && a.Evidence.StartsWith(EvidencePrefix, StringComparison.Ordinal));
        var humanHandles = overlay.Annotations.Where(a => a.Provenance == Provenance.HumanConfirmed)
            .Select(a => a.TargetHandle).ToHashSet();

        var insertsByBlock = walked.Where(w => w.Entity is Insert)
            .GroupBy(w => ((Insert)w.Entity).Block?.Name ?? "", StringComparer.OrdinalIgnoreCase).ToList();
        var autoSeeds = new List<BlockAutoSeed>();
        var suggestions = new List<BlockSuggestion>();
        var ambiguous = new List<BlockAmbiguous>();
        int scanned = 0, excluded = 0;

        foreach (var block in doc.BlockRecords)
        {
            if (IsExcluded(block)) { excluded++; continue; }
            scanned++;
            var instances = insertsByBlock.FirstOrDefault(g => g.Key.Equals(block.Name, StringComparison.OrdinalIgnoreCase))?.ToList()
                ?? new List<WalkedEntity>();
            var fp = BlockFingerprint.Extract(block.Entities, out int blkDropped);
            nonFiniteDropped += blkDropped; // R9③：null（代理）路径剔除数亦披露
            if (fp is null)
            {
                // 零白名单几何——天正代理同构（代理实体不可读→块内无原生几何）
                if (instances.Count >= minInstances)
                    ambiguous.Add(new BlockAmbiguous(block.Name, instances.Count, "代理图形不可读",
                        instances.Take(5).Select(w => w.HandlePath).ToList(),
                        HistogramOf(block.Entities), BboxOf(block.Entities)));
                continue;
            }
            // 第一档：哈希精确→自动种子（多符号取首=包序+yaml 序确定性）
            var exact = symbols.Where(s => s.Fp.Hash == fp.Hash).ToList();
            if (exact.Count > 0)
            {
                var sym = exact[0];
                var handles = new List<string>();
                foreach (var w in instances)
                {
                    if (humanHandles.Contains(w.HandlePath)) continue; // HumanConfirmed 句柄跳过自动种子
                    overlay.UpsertAnnotation(new SemanticAnnotation(w.HandlePath, sym.Class,
                        Provenance.GeometryInference, 0.9, $"{EvidencePrefix} {sym.Name}", w.SheetId));
                    handles.Add(w.HandlePath);
                }
                if (handles.Count > 0) autoSeeds.Add(new BlockAutoSeed(block.Name, sym.Name, handles));
                continue;
            }
            // 第二档：结构相容且 d≤NearThreshold→只建议不自动；全列按 d 升序+名 Ordinal 次序
            var candidates = symbols
                .Where(s => BlockFingerprint.Compatible(s.Fp, fp))
                .Select(s => (s.Name, D: BlockFingerprint.Distance(fp, s.Fp)))
                .Where(t => t.D <= BlockFingerprint.NearThreshold)
                .OrderBy(t => t.D).ThenBy(t => t.Name, StringComparer.Ordinal)
                .Select(t => new BlockCandidate(t.Name, t.D, BlockFingerprint.Confidence(t.D)))
                .ToList();
            if (candidates.Count > 0)
            {
                suggestions.Add(new BlockSuggestion(block.Name, instances.Count, candidates));
                continue;
            }
            // 第三档：无命中且高频（instances≥minInstances）→歧义披露
            if (instances.Count >= minInstances)
                ambiguous.Add(new BlockAmbiguous(block.Name, instances.Count, "无匹配",
                    instances.Take(5).Select(w => w.HandlePath).ToList(),
                    HistogramOf(block.Entities), BboxOf(block.Entities)));
        }
        return new BlockMatchResult(autoSeeds, suggestions, ambiguous, symbols.Count, skipped, scanned, excluded, nonFiniteDropped);
    }
}
