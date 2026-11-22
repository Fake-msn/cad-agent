using ACadSharp;
using ACadSharp.Entities;
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data.Sheets;

namespace CadAgent.Data.Semantic;

/// <summary>
/// 块遍历产出：实体引用 + 句柄路径（顶层=句柄，嵌套="{parent}:{i}"，与渲染器同 convention）
/// + 归一层名（约定匹配用；映射/歧义清单保留原名）+ 锚点世界坐标 + 幅面归属。
/// WorldPoints=实体全点经当前仿射变换后的世界坐标（检测器几何统一口径）：
/// Line=两端点；LwPolyline/Polyline2D=顶点列；Text/MText/Insert=插入点；
/// Circle/Arc：Points[0]=世界圆心，Points[1]=(世界半径,0)（与 RenderItem 同 convention，
/// 半径经 Affine.RadiusScale 近似——非均匀缩放下真 CAD 为椭圆，此处文档化近似）。
/// </summary>
public sealed record WalkedEntity(Entity Entity, string HandlePath, string CanonicalLayer,
    double WorldX, double WorldY, string? SheetId, IReadOnlyList<(double X, double Y)> WorldPoints);

/// <summary>
/// 块遍历（底图完整性）：深入 bound xref/嵌套块 yield 实体（世界坐标+后缀句柄+归一层名+幅面归属）。
/// 循环守卫=块名访问栈+maxDepth；检测器/渲染消费同一遍历口径。
/// R3：Insert 在递归子实体之前先 yield 自身（世界坐标=插入点经外层变换），供 T3 块属性消费。
/// </summary>
public static class BlockWalker
{
    public static IEnumerable<WalkedEntity> Walk(CadDocument doc, IReadOnlyList<SheetRecord> sheets, int maxDepth = 32)
    {
        foreach (var e in doc.Entities)
            foreach (var w in WalkEntity(e, e.Handle.ToString(), Affine.Identity, 1,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase), sheets, maxDepth))
                yield return w;
    }

    private static IEnumerable<WalkedEntity> WalkEntity(Entity e, string path, Affine t, int depth,
        HashSet<string> stack, IReadOnlyList<SheetRecord> sheets, int maxDepth)
    {
        string layer = e.Layer?.Name ?? "0";
        string canon = CanonicalLayer.Normalize(layer);
        var (px, py) = AnchorOf(e);
        var (wx, wy) = t.Apply(px, py);
        // Insert 自身先 yield（R3），再递归子实体；访问栈守卫自指/互指块
        yield return new WalkedEntity(e, path, canon, wx, wy,
            SheetRegistryBuilder.SheetIdForPoint(sheets, wx, wy), WorldPointsOf(e, t));
        if (e is Insert ins && ins.Block != null && depth < maxDepth && stack.Add(ins.Block.Name))
        {
            // 复合：outer(inner(p))——插入变换（R·S·(p-base)+insertPoint）叠加外层仿射
            var childT = t.Compose(Affine.InsertAffine(ins));
            int i = 0;
            foreach (var child in ins.Block.Entities)
            {
                foreach (var w in WalkEntity(child, path + ":" + i, childT, depth + 1, stack, sheets, maxDepth))
                    yield return w;
                i++;
            }
            stack.Remove(ins.Block.Name);
        }
    }

    private static (double X, double Y) AnchorOf(Entity e) => e switch
    {
        Line l => (l.StartPoint.X, l.StartPoint.Y),
        LwPolyline lw when lw.Vertices.Count > 0 => (lw.Vertices[0].Location.X, lw.Vertices[0].Location.Y),
        Polyline2D p2 when p2.Vertices.Count > 0 => (p2.Vertices[0].Location.X, p2.Vertices[0].Location.Y),
        TextEntity t => (t.InsertPoint.X, t.InsertPoint.Y),
        MText mt => (mt.InsertPoint.X, mt.InsertPoint.Y),
        Circle c => (c.Center.X, c.Center.Y), // Arc : Circle，同取圆心
        Insert ins => (ins.InsertPoint.X, ins.InsertPoint.Y),
        _ => (0, 0)
    };

    /// <summary>实体全点施加当前仿射→世界坐标点列（convention 见 WalkedEntity 注释）。</summary>
    private static IReadOnlyList<(double X, double Y)> WorldPointsOf(Entity e, Affine t) => e switch
    {
        Line l => new[] { t.Apply(l.StartPoint.X, l.StartPoint.Y), t.Apply(l.EndPoint.X, l.EndPoint.Y) },
        LwPolyline lw => lw.Vertices.Select(v => t.Apply(v.Location.X, v.Location.Y)).ToList(),
        Polyline2D p2 => p2.Vertices.Select(v => t.Apply(v.Location.X, v.Location.Y)).ToList(),
        TextEntity tx => new[] { t.Apply(tx.InsertPoint.X, tx.InsertPoint.Y) },
        MText mt => new[] { t.Apply(mt.InsertPoint.X, mt.InsertPoint.Y) },
        Circle c => new[] { t.Apply(c.Center.X, c.Center.Y), (c.Radius * t.RadiusScale, 0.0) },
        Insert ins => new[] { t.Apply(ins.InsertPoint.X, ins.InsertPoint.Y) },
        _ => Array.Empty<(double, double)>()
    };
}
