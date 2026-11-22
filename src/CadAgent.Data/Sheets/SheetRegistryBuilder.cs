using System.Globalization;
using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using CadAgent.Core;
using CadAgent.Core.Cad;

namespace CadAgent.Data.Sheets;

/// <summary>
/// 幅面登记簿：图框块引用检测（块名含 图框/图签/BORD 或携带 图号 属性）→
/// 框 bounds（块几何经 InsertTransform 展开）+ 标题栏属性 + 框内/近邻幅面名文字关联。
/// 无图框→单默认幅面。实体归属经 SheetIdForPoint（框外=null，远端残元不混入）。
/// </summary>
public static class SheetRegistryBuilder
{
    private static readonly string[] FrameMarkers = { "图框", "图签", "BORD" };

    /// <summary>图框 Insert 判定（Build 与 Sheets 缓存指纹共用同一口径）。</summary>
    internal static bool IsFrameInsert(Insert i) =>
        i.Block != null &&
        (FrameMarkers.Any(m => i.Block.Name.Contains(m, StringComparison.OrdinalIgnoreCase))
         || i.Attributes.Any(a => a.Tag == "图号"));

    /// <summary>
    /// Sheets 缓存轻量指纹（T4 接住 T1 minor；T5 收口边界）：实体计数 +
    /// 图框 Insert（句柄/插入点/旋转/缩放/属性集）+ 非图框文字（句柄/插入点/图层/值）+
    /// 布局视口（句柄/中心/宽高/ViewTarget/ViewHeight）+ 无框路径几何极值。
    /// handler 就地编辑（增删实体、增删/移动/旋转/缩放图框、改图框属性、移动/改层/改值幅面名文字、
    /// 移动/缩放视口、无框移动实体）→ 指纹变化 → getter 重建；指纹不变则复用缓存。
    /// 诚实边界（仍经 Reload 失效）：图框块定义内部几何编辑（块内增删/移动图元）不在指纹内——
    /// 指纹仅覆盖顶层实体与图框插入变换，块定义内容变化需 Reload。
    /// 性能注记：O(n) 单趟枚举（无框路径另趟极值）；高频访问可后续脏标记优化（T4 minor 交接）。
    /// </summary>
    internal static string Fingerprint(CadDocument doc)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        int count = 0;
        var frames = new List<Insert>();
        var texts = new List<TextEntity>();
        foreach (var e in doc.Entities)
        {
            count++;
            if (e is Insert ins && IsFrameInsert(ins)) frames.Add(ins);
            if (e is TextEntity t) texts.Add(t);
        }
        sb.Append(count).Append('|');
        // 图框：句柄@插入点,旋转,缩放 + 属性集
        foreach (var f in frames.OrderBy(f => f.Handle))
        {
            sb.Append(f.Handle).Append('@')
              .Append(f.InsertPoint.X.ToString("R", inv)).Append(',')
              .Append(f.InsertPoint.Y.ToString("R", inv)).Append(',')
              .Append(f.Rotation.ToString("R", inv)).Append(',')
              .Append(f.XScale.ToString("R", inv)).Append(',')
              .Append(f.YScale.ToString("R", inv)).Append(';');
            foreach (var a in f.Attributes.OrderBy(a => a.Tag, StringComparer.Ordinal))
                sb.Append(a.Tag).Append('=').Append(a.Value).Append(';');
            sb.Append('|');
        }
        // 非图框文字：句柄@插入点;图层;值（幅面名文字移动/改层/改值 → 名称重算）
        foreach (var t in texts.OrderBy(t => t.Handle))
        {
            sb.Append(t.Handle).Append('@')
              .Append(t.InsertPoint.X.ToString("R", inv)).Append(',')
              .Append(t.InsertPoint.Y.ToString("R", inv)).Append(';')
              .Append(t.Layer?.Name ?? "").Append(';')
              .Append(t.Value).Append('|');
        }
        // 布局视口：句柄@中心,宽,高,ViewTarget,ViewHeight（视口移动/缩放/改视图 → 布局幅面重建）
        if (doc.Layouts is not null)
        {
            foreach (var layout in doc.Layouts)
            {
                if (!layout.IsPaperSpace || layout.AssociatedBlock is null) continue;
                foreach (var vp in layout.AssociatedBlock.Entities.OfType<Viewport>().OrderBy(v => v.Handle))
                {
                    sb.Append(vp.Handle).Append('@')
                      .Append(vp.Center.X.ToString("R", inv)).Append(',')
                      .Append(vp.Center.Y.ToString("R", inv)).Append(',')
                      .Append(vp.Width.ToString("R", inv)).Append(',')
                      .Append(vp.Height.ToString("R", inv)).Append(',')
                      .Append(vp.ViewTarget.X.ToString("R", inv)).Append(',')
                      .Append(vp.ViewTarget.Y.ToString("R", inv)).Append(',')
                      .Append(vp.ViewHeight.ToString("R", inv)).Append('|');
                }
            }
        }
        // 无框路径几何极值（单幅面 ModelBounds 依赖全实体几何；移动实体 → 极值变 → 重建）
        if (frames.Count == 0)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var e in doc.Entities)
                foreach (var (x, y) in PointsOf(e))
                {
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
            sb.Append("ext:").Append(minX.ToString("R", inv)).Append(',')
              .Append(minY.ToString("R", inv)).Append(',')
              .Append(maxX.ToString("R", inv)).Append(',')
              .Append(maxY.ToString("R", inv));
        }
        return sb.ToString();
    }

    public static IReadOnlyList<SheetRecord> Build(CadDocument doc)
    {
        var sheets = new List<SheetRecord>();
        var frames = doc.Entities.OfType<Insert>().Where(IsFrameInsert).ToList();
        if (frames.Count == 0)
        {
            var b = ModelBounds(doc);
            sheets.Add(new SheetRecord("S0", "(单幅面)", null, null, null, null, b, null,
                new Dictionary<string, string>()));
        }
        else
        {
            var nameTexts = doc.Entities.OfType<TextEntity>()
                .Where(t => t.Layer?.Name is "图框" or "图名" or "TITLE" || FrameMarkers.Any(m => (t.Layer?.Name ?? "").Contains(m)))
                .ToList();
            foreach (var f in frames.OrderBy(f => f.InsertPoint.Y).ThenBy(f => f.InsertPoint.X))
            {
                var bounds = FrameBounds(f);
                // 同 Tag 多属性（真实图纸不罕见）→ 后值覆盖，诚实降级而非抛 ArgumentException
                var attrs = new Dictionary<string, string>();
                foreach (var a in f.Attributes) attrs[a.Tag] = a.Value;
                // 幅面名：框内文字优先，其次框上缘近邻（y 距框顶 <5% 框高、x 在框内）
                string? name = nameTexts.Where(t => Inside(bounds, t.InsertPoint.X, t.InsertPoint.Y))
                    .Select(t => t.Value).FirstOrDefault()
                    ?? nameTexts.Where(t => t.InsertPoint.X >= bounds.MinX && t.InsertPoint.X <= bounds.MaxX
                        && t.InsertPoint.Y > bounds.MaxY && t.InsertPoint.Y - bounds.MaxY < (bounds.MaxY - bounds.MinY) * 0.05)
                        .Select(t => t.Value).FirstOrDefault();
                attrs.TryGetValue("图号", out string? number);
                attrs.TryGetValue("比例", out string? scale);
                attrs.TryGetValue("图别", out string? discipline);
                attrs.TryGetValue("日期", out string? date);
                sheets.Add(new SheetRecord(f.Handle.ToString(), name ?? attrs.GetValueOrDefault("图纸名称") ?? $"幅面{sheets.Count + 1}",
                    number, scale, discipline, date, bounds, null, attrs));
            }
        }
        // 布局视口幅面（T5）：模型幅面在前，布局幅面在后（统一列表）
        sheets.AddRange(BuildLayoutSheets(doc));
        return sheets;
    }

    /// <summary>
    /// 布局视口幅面登记（T5）：doc.Layouts 中 IsPaperSpace 且 AssociatedBlock 含真实 Viewport
    /// 实体者 → SheetRecord(LayoutName=布局名, SheetId="L"+视口句柄, FrameBounds=视口纸面 bounds,
    /// TitleAttributes[viewTarget/viewHeight/viewportHandle])。
    /// 诚实边界：空布局/无视口不登记；纸边界视口（RepresentsPaper，仅边界不 visualize 模型，
    /// ViewHeight=0）与退化视口（ViewHeight/Width/Height≤0）跳过。模型区域经 ViewTarget/ViewHeight
    /// 记录于 TitleAttributes，供 View BuildViewport 裁剪+纸面映射消费。
    /// </summary>
    private static IEnumerable<SheetRecord> BuildLayoutSheets(CadDocument doc)
    {
        if (doc.Layouts is null) yield break;
        foreach (var layout in doc.Layouts)
        {
            if (!layout.IsPaperSpace || layout.AssociatedBlock is null) continue;
            foreach (var vp in layout.AssociatedBlock.Entities.OfType<Viewport>())
            {
                if (vp.RepresentsPaper || vp.ViewHeight <= 1e-12 || vp.Width <= 1e-12 || vp.Height <= 1e-12) continue;
                double cx = vp.Center.X, cy = vp.Center.Y, hw = vp.Width / 2, hh = vp.Height / 2;
                var bounds = (MinX: cx - hw, MinY: cy - hh, MaxX: cx + hw, MaxY: cy + hh);
                var attrs = new Dictionary<string, string>
                {
                    ["viewTarget"] = vp.ViewTarget.X.ToString("R", CultureInfo.InvariantCulture) + "," + vp.ViewTarget.Y.ToString("R", CultureInfo.InvariantCulture),
                    ["viewHeight"] = vp.ViewHeight.ToString("R", CultureInfo.InvariantCulture),
                    ["viewportHandle"] = vp.Handle.ToString(),
                };
                yield return new SheetRecord("L" + vp.Handle, layout.Name, null, null, null, null,
                    bounds, layout.Name, attrs);
            }
        }
    }

    public static string? SheetIdForPoint(IReadOnlyList<SheetRecord> sheets, double x, double y)
        // R11：模型点归属仅模型幅面——跳过布局幅面（FrameBounds 为纸面坐标，与模型世界坐标
        // 数值重叠时不得把 stray 模型实体误归 "L…"，防闸口 InScope 跨空间污染）。
        => sheets.FirstOrDefault(s => s.LayoutName is null && Inside(s.FrameBounds, x, y))?.SheetId;

    private static bool Inside((double MinX, double MinY, double MaxX, double MaxY) b, double x, double y)
        => x >= b.MinX && x <= b.MaxX && y >= b.MinY && y <= b.MaxY;

    /// <summary>
    /// 图框块几何经插入变换（Affine.InsertAffine：缩放/旋转/基点）展开求 bounds；
    /// 嵌套 Insert 经 Compose 递归展开（深度守卫 maxDepth=32 + 块名访问栈，同 BlockWalker）。
    /// 空几何倒置哨兵（minX&gt;maxX）→ 回退插入点单位框（±1），不产生非法 bounds。
    /// </summary>
    private static (double MinX, double MinY, double MaxX, double MaxY) FrameBounds(Insert f)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        var stack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Expand(Entity e, Affine t, int depth)
        {
            foreach (var (x, y) in PointsOf(e))
            {
                var (wx, wy) = t.Apply(x, y);
                if (wx < minX) minX = wx; if (wx > maxX) maxX = wx;
                if (wy < minY) minY = wy; if (wy > maxY) maxY = wy;
            }
            if (e is Insert ins && ins.Block != null && depth < 32 && stack.Add(ins.Block.Name))
            {
                var childT = t.Compose(Affine.InsertAffine(ins));
                foreach (var child in ins.Block.Entities) Expand(child, childT, depth + 1);
                stack.Remove(ins.Block.Name);
            }
        }
        var t0 = Affine.InsertAffine(f);
        stack.Add(f.Block!.Name);
        foreach (var e in f.Block.Entities) Expand(e, t0, 1);
        if (minX > maxX)
            return (f.InsertPoint.X - 1, f.InsertPoint.Y - 1, f.InsertPoint.X + 1, f.InsertPoint.Y + 1);
        return (minX, minY, maxX, maxY);
    }

    private static IEnumerable<(double X, double Y)> PointsOf(Entity e) => e switch
    {
        Line l => new[] { (l.StartPoint.X, l.StartPoint.Y), (l.EndPoint.X, l.EndPoint.Y) },
        LwPolyline lw => lw.Vertices.Select(v => (v.Location.X, v.Location.Y)),
        Polyline2D p2 => p2.Vertices.Select(v => (v.Location.X, v.Location.Y)),
        TextEntity t => new[] { (t.InsertPoint.X, t.InsertPoint.Y) },
        Circle c => new[] { (c.Center.X - c.Radius, c.Center.Y - c.Radius), (c.Center.X + c.Radius, c.Center.Y + c.Radius) }, // Arc : Circle 同覆盖
        Insert ins => new[] { (ins.InsertPoint.X, ins.InsertPoint.Y) },
        _ => Array.Empty<(double, double)>()
    };

    private static (double, double, double, double) ModelBounds(CadDocument doc)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var e in doc.Entities)
            foreach (var (x, y) in PointsOf(e))
            {
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
        return minX > maxX ? (0, 0, 1, 1) : (minX, minY, maxX, maxY);
    }
}
