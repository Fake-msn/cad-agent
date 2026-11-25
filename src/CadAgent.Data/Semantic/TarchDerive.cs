using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CadAgent.Core;
using CadAgent.Core.Cad;

namespace CadAgent.Data.Semantic;

/// <summary>符号块候选（spec 切片B Task6）：形似天正符号库的块引用——**只披露不落标注**，
/// 由人工/上层决定是否确认为门窗或设备符号。</summary>
public sealed record SymbolCandidate(string Handle, string BlockName, string Kind, string ClassHint);

/// <summary>Task6 派生结果：仅符号块候选需回传；中心线/种子/参数并入均直写 overlay。</summary>
public sealed record TarchDeriveResult(IReadOnlyList<SymbolCandidate> SymbolCandidates);

/// <summary>天正符号库块名前缀→候选口径（spec 切片B Task6）：命中即候选，永不成标注。
/// 匹配口径：块名 Contains（库块名前缀之外还常带块缩放/嵌套层前缀），大小写敏感（Ordinal）。</summary>
public static class TarchSymbolPrefixes
{
    public static (string Kind, string ClassHint)? Match(string blockName)
    {
        if (blockName is null) return null;
        if (blockName.Contains("$DorLib2D$", StringComparison.Ordinal))
            return ("door-window", "门窗库块（候选）");
        if (blockName.Contains("$TWTSYS$", StringComparison.Ordinal))
            return ("equipment-sym", "设备系统符号块（候选）");
        return null;
    }
}

/// <summary>天正深度炸开证据派生（spec 切片B Task6）：中心线推导 + XData 并入原生参数 +
/// NativeExplode 标注种子 + 符号块候选 + 陈旧证据行清理。纯数据层（零 COM/零 IO）。
/// 幂等：按句柄 Upsert，重扫刷新 CenterlineJson；重扫先删本轮未读到的证据行（镜像
/// NativeParamSeeder "当前原生数据"语义）。空/缺基元、null XData 一律按空处理（不 NRE）。
/// 标注同口径：本轮未成功种子的 NativeExplode 标注一律回收（证据消失/存疑→不得残留授权）。</summary>
public static class TarchDerive
{
    private const double ParallelTol = 1e-3;   // |cross(dir, dirK)| 平行判据
    private const double WidthTol = 0.5;       // 推导间距 vs TotalWidth 容差（mm）
    private const double BboxTol = 1e-3;       // 种子的原生/炸开 bbox 逐分量容差
    private const double LengthEps = 1e-9;     // XY 长度下限（退化线剔除）
    private const double ZeroWidthEps = 1e-6;  // 推导宽度下限（重合线无墙厚，不给中心线）
    private const double NativeExplodeConfidence = 0.9;
    private const int RoundDigits = 3;

    private static readonly JsonSerializerOptions JsonOpts = new();

    /// <summary>一轮炸开证据落 overlay。返回符号块候选（不落标注）。
    /// 顺序：先按入参算 live 句柄集 → 删陈旧证据行 → 逐条 upsert（本轮行不会被误删）
    /// → 回收本轮未成功种子的 NativeExplode 标注。</summary>
    public static TarchDeriveResult Enrich(SemanticOverlay overlay, IReadOnlyList<ExplodeEvidence> evidences)
    {
        ArgumentNullException.ThrowIfNull(overlay);

        var list = new List<ExplodeEvidence>();
        foreach (var e in evidences ?? Array.Empty<ExplodeEvidence>())
            if (e is not null) list.Add(e);

        // 陈旧行清理（重扫口径）：本轮未读到的句柄整行移除。
        // M7（PR#30 终审）：句柄比较与大表 Upsert 同口径 OrdinalIgnoreCase——旧 Ordinal 会把
        // "同一实体、大小写不同"的存活行当陈旧行删掉（随后 Upsert 再追加，行序/身份漂移）；
        // 对齐后：本轮读到的实体一律原地刷新，未读到的整行回收。
        var live = new HashSet<string>(list.Select(e => e.Handle), StringComparer.OrdinalIgnoreCase);
        overlay.ExplodeEvidence.RemoveAll(e => !live.Contains(e.Handle));

        var candidates = new List<SymbolCandidate>();
        var seeded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ev in list)
        {
            overlay.UpsertExplode(ev with { CenterlineJson = DeriveCenterline(overlay, ev) });

            if (SeedNativeExplode(overlay, ev)) seeded.Add(ev.Handle);
            MergeXData(overlay, ev);
            CollectSymbolCandidates(ev, candidates);
        }

        // 标注回收（重扫口径）：证据行消失或本轮种子条件不满足 → 上轮 0.9 不得残留（否则可单凭 SemanticGate 放行）。
        overlay.Annotations.RemoveAll(a =>
            a.Provenance == Provenance.NativeExplode && !seeded.Contains(a.TargetHandle));

        return new TarchDeriveResult(candidates);
    }

    // ---- 种子：Fully && 类已知 && bbox 口径一致（参数面缺失即不比）----

    /// <summary>返回是否本轮成功种子（供标注回收判定）。</summary>
    private static bool SeedNativeExplode(SemanticOverlay overlay, ExplodeEvidence ev)
    {
        var cls = TDbClassMap.ClassFor(ev.RxClass);
        if (!ev.FullyExplodable || cls == SemanticClass.Unknown) return false;

        var p = overlay.NativeEntities.FirstOrDefault(n => n.Handle == ev.Handle);
        if (p?.Bbox is not null && ev.Bbox is not null && !BboxClose(p.Bbox, ev.Bbox, BboxTol))
            return false;   // 原生 bbox 与炸开 bbox 不一致：证据存疑，不种子

        // UpsertAnnotation 以 (handle+provenance) 去重 → 重扫幂等。
        overlay.UpsertAnnotation(new SemanticAnnotation(
            ev.Handle, cls, Provenance.NativeExplode, NativeExplodeConfidence,
            $"NETLOAD 炸开证据: {ev.RxClass}"));
        return true;
    }

    /// <summary>bbox 逐分量容差比较（仅共同分量；生产者两侧均为 X/Y/Z 六分量）。</summary>
    internal static bool BboxClose(double[] a, double[] b, double tol)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (!double.IsFinite(a[i]) || !double.IsFinite(b[i])) return false;
            if (Math.Abs(a[i] - b[i]) > tol) return false;
        }
        return true;
    }

    // ---- XData 并入：仅既有 NativeEntities 行（无行不造骨架）----

    private static void MergeXData(SemanticOverlay overlay, ExplodeEvidence ev)
    {
        var xdata = ev.XData;
        if (xdata is null || xdata.Count == 0) return;

        int idx = overlay.NativeEntities.FindIndex(n => n.Handle == ev.Handle);
        if (idx < 0) return;

        var row = overlay.NativeEntities[idx];
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        // 非 XData 参数原样保留；既有 XData:* 一律丢弃后由本轮 XData 重建（否则上轮已消失的键会残留成幽灵）。
        if (row.Params is not null)
            foreach (var kv in row.Params)
                if (!kv.Key.StartsWith("XData:", StringComparison.Ordinal)) merged[kv.Key] = kv.Value;
        foreach (var kv in xdata)
        {
            if (kv.Key is null || kv.Value is null) continue;   // 空键/空值按缺失处理
            merged["XData:" + kv.Key] = kv.Value;               // 同名键覆盖既有
        }
        overlay.NativeEntities[idx] = row with { Params = merged };
    }

    // ---- 符号块候选 ----

    private static void CollectSymbolCandidates(ExplodeEvidence ev, List<SymbolCandidate> sink)
    {
        var prims = ev.Primitives;
        if (prims is null) return;
        foreach (var prim in prims)
        {
            if (prim is null) continue;
            if (!string.Equals(prim.Kind, "block", StringComparison.Ordinal)) continue;
            if (prim.BlockName is null) continue;
            var hit = TarchSymbolPrefixes.Match(prim.BlockName);
            if (hit is null) continue;
            sink.Add(new SymbolCandidate(ev.Handle, prim.BlockName, hit.Value.Kind, hit.Value.ClassHint));
        }
    }

    // ---- 中心线推导（直墙：平行对 + 宽度匹配）----

    /// <summary>直墙中心线：取全部 Kind=="line" 且 6 分量有限的基元，按首线方向归一；
    /// 每线两端点在法向投影均值=offset；少于 2 条平行线或推导宽度≈0（重合线）→null。
    /// TotalWidth 存在且可解析时，外线间距须在 0.5mm 内（不可解析按缺失，不拒推）。
    /// 端点=两条外线沿方向的投影区间并集。输出 JSON 固定字段序，分量四舍五入 3 位。</summary>
    private static string? DeriveCenterline(SemanticOverlay overlay, ExplodeEvidence ev)
    {
        var segs = CollectLines(ev);
        if (segs.Count < 2) return null;

        // 参考方向取自首条基元线并从首线方向归一；若某类墙首产物为封口线，会判证据不足（fail-safe）。
        // 法向右旋 90°（n=(-dy,dx)）。
        double dx = segs[0].DirX, dy = segs[0].DirY;
        double nx = -dy, ny = dx;

        var offs = new List<(double Offset, double T0, double T1, double Z0, double Z1)>();
        foreach (var s in segs)
        {
            double sx = s.DirX, sy = s.DirY;
            double ax = s.X1, ay = s.Y1, az = s.Z1, bx = s.X2, by = s.Y2, bz = s.Z2;
            if (dx * sx + dy * sy < 0)
            {
                // 反向线段：先翻转方向与端点，使 offset 符号/投影区间与首线同向（反向线仍须成对）
                sx = -sx; sy = -sy;
                (ax, bx) = (bx, ax);
                (ay, by) = (by, ay);
                (az, bz) = (bz, az);
            }
            if (Math.Abs(dx * sy - dy * sx) > ParallelTol) continue;   // 非平行丢弃

            double offset = ((ax * nx + ay * ny) + (bx * nx + by * ny)) / 2.0;
            offs.Add((offset, ax * dx + ay * dy, bx * dx + by * dy, az, bz));
        }
        if (offs.Count < 2) return null;   // 平行线不足 → 证据不足

        double minOff = offs[0].Offset, maxOff = offs[0].Offset;
        foreach (var o in offs)
        {
            if (o.Offset < minOff) minOff = o.Offset;
            if (o.Offset > maxOff) maxOff = o.Offset;
        }

        double width = maxOff - minOff;
        if (width <= ZeroWidthEps) return null;   // 重合线（无墙厚）→ 证据不足，不给中心线
        double? totalWidth = TryTotalWidth(overlay, ev.Handle);
        if (totalWidth is double tw && Math.Abs(width - tw) > WidthTol) return null;

        // 两条最外线的投影区间并集 → 中线端点。
        double tStart = double.MaxValue, tEnd = double.MinValue, zStart = 0, zEnd = 0;
        foreach (var o in offs)
        {
            if (o.Offset > minOff && o.Offset < maxOff) continue;
            bool forward = o.T0 <= o.T1;
            double oT0 = forward ? o.T0 : o.T1, oT1 = forward ? o.T1 : o.T0;
            double oZ0 = forward ? o.Z0 : o.Z1, oZ1 = forward ? o.Z1 : o.Z0;
            if (oT0 < tStart) { tStart = oT0; zStart = oZ0; }
            if (oT1 > tEnd) { tEnd = oT1; zEnd = oZ1; }
        }

        double mid = (minOff + maxOff) / 2.0;
        var dto = new CenterlineDto(
            "line",
            new[] { Round3(tStart * dx + mid * nx), Round3(tStart * dy + mid * ny), Round3(zStart) },
            new[] { Round3(tEnd * dx + mid * nx), Round3(tEnd * dy + mid * ny), Round3(zEnd) },
            Round3(width));
        return JsonSerializer.Serialize(dto, JsonOpts);
    }

    /// <summary>取可用线段：Kind=="line"、≥6 分量且全有限、XY 长度非退化。方向在 XY 归一。</summary>
    private static List<Seg> CollectLines(ExplodeEvidence ev)
    {
        var segs = new List<Seg>();
        var prims = ev.Primitives;
        if (prims is null) return segs;
        foreach (var prim in prims)
        {
            if (prim is null) continue;
            if (!string.Equals(prim.Kind, "line", StringComparison.Ordinal)) continue;
            var pts = prim.Points;
            if (pts is null || pts.Length < 6) continue;

            bool finite = true;
            for (int i = 0; i < 6; i++)
                if (!double.IsFinite(pts[i])) { finite = false; break; }
            if (!finite) continue;

            double vx = pts[3] - pts[0], vy = pts[4] - pts[1];
            double len = Math.Sqrt(vx * vx + vy * vy);
            if (len <= LengthEps) continue;   // 退化线（零长）不给方向

            segs.Add(new Seg(pts[0], pts[1], pts[2], pts[3], pts[4], pts[5], vx / len, vy / len));
        }
        return segs;
    }

    /// <summary>TotalWidth 读取：缺失/不可解析（InvariantCulture）→ null（不参与比对）。</summary>
    private static double? TryTotalWidth(SemanticOverlay overlay, string handle)
    {
        var row = overlay.NativeEntities.FirstOrDefault(n => n.Handle == handle);
        if (row?.Params is null) return null;
        if (!row.Params.TryGetValue("TotalWidth", out var raw) || raw is null) return null;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double w) ? w : null;
    }

    private static double Round3(double v)
    {
        double r = Math.Round(v, RoundDigits);
        return r == 0 ? 0 : r;   // 归并 -0 → 0（JSON 不出现 "-0"）
    }

    private sealed record Seg(
        double X1, double Y1, double Z1, double X2, double Y2, double Z2, double DirX, double DirY);

    /// <summary>中心线 JSON 载荷：字段序固定 kind/p1/p2/width（spec 切片B Task6）。</summary>
    private sealed record CenterlineDto(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("p1")] double[] P1,
        [property: JsonPropertyName("p2")] double[] P2,
        [property: JsonPropertyName("width")] double Width);
}
