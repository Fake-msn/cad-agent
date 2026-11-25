namespace CadAgent.Core.Cad;

/// <summary>炸开后单基元（spec 切片B §3）：Core 零 COM——纯数据。
/// Points=平铺坐标（直线6/圆3/文字插入点3）；Text=文字内容；BlockName=块引用名。</summary>
public sealed record ExplodePrimitive(
    string Kind,
    double[] Points,
    string? Text,
    string? BlockName);

/// <summary>一实体一次深度炸开取证（spec 切片B §3）。Core 零 COM——纯数据。
/// FullyExplodable=递归炸开到叶全部成功；NestedTdbCount=炸开后残留 TDb 叶数（0=干净）；
/// CenterlineJson=通用轴（推导对任意≥2平行线成立；消费侧按 TDbWall/Class 过滤），可为 null；ReadAt=取证时刻。</summary>
public sealed record ExplodeEvidence(
    string Handle,
    string RxClass,
    string? WrapperType,
    string? Layer,
    double[]? Bbox,
    bool FullyExplodable,
    int PrimitiveCount,
    int NestedTdbCount,
    IReadOnlyList<ExplodePrimitive> Primitives,
    IReadOnlyDictionary<string, string> XData,
    string? CenterlineJson,
    DateTimeOffset ReadAt);

/// <summary>句柄级炸开替换结果（spec 切片B §4）：BboxMatch=替换前后包围盒（仅 X/Y）容差内一致。</summary>
public sealed record ExplodeReplaceResult(
    string Handle,
    int NewCount,
    string SavedPath,
    bool BboxMatch,
    double Tolerance);
